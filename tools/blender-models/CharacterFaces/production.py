"""Source-owned facial surfaces, expression-preserving refinement and sculpt baking."""
import argparse
import copy
import gzip
import hashlib
import json
import math
import struct
import sys
from pathlib import Path
from typing import Any

import bpy
import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
import generate as art
from collect_sources import fingerprint
from blender_pipeline import Matrix, Vector, empty_scene, write_packed_rgba, set_mesh_custom_normals

REPO = art.REPO
sys.path.insert(0,str(REPO/'tools/asset-authoring'))
import asset_names as resource_naming
MATERIAL_CHANNEL_CONTRACTS = {}


def require(value, message):
    if not value:
        raise ValueError(message)


def load_inputs(path):
    """Resolve explicitly declared production inputs relative to their owner."""
    path = Path(path).resolve()
    document = art.read_json(path)
    required = {'schemaVersion', 'sourceRoot', 'census', 'selection', 'profiles', 'materialChannels', 'framing', 'nativePoses'}
    if not (type(document) is dict and set(document) == required and type(document['schemaVersion']) is int and document['schemaVersion'] == 1): raise ValueError('Invalid character source input document.')
    paths = {}
    for name in sorted(required - {'schemaVersion'}):
        value = document[name]
        if not (type(value) is str and value.strip()): raise ValueError('A character source input path is missing: ' + name)
        resolved = (path.parent / str(value)).resolve()
        if not (resolved.is_dir() if name == 'sourceRoot' else resolved.is_file()): raise ValueError('A character source input does not exist: ' + name)
        paths[name] = resolved
    return paths


def select_source_assets(census: 'dict[str, Any]', path):
    """Every stage uses an explicit source-content selection, including aliases."""
    import re
    if not (path.is_file()): raise ValueError('A source-owned Selection.json is required; no face-name or all-census fallback exists.')
    selection = art.read_json(path)
    if not (type(selection) is dict and set(selection) == {'schemaVersion', 'assetSourceContentKeys', 'expectedAssetCount'} and selection['schemaVersion'] == 1): raise ValueError('Invalid source-owned selection schema.')
    keys = selection['assetSourceContentKeys']
    if not (type(keys) is list and keys and all(type(key) is str and re.fullmatch('[0-9a-f]{64}', key) for key in keys) and len(set(keys)) == len(keys)): raise ValueError('Source-content keys must be nonempty, typed, distinct native identities.')
    if not (type(selection['expectedAssetCount']) is int and selection['expectedAssetCount'] > 0): raise ValueError('Source selection requires an explicit positive asset count, including content aliases.')
    selected = [asset for asset in census['assets'] if asset['sourceContentKey'] in keys]
    if not (len(selected) == selection['expectedAssetCount'] and {asset['sourceContentKey'] for asset in selected} == set(keys)): raise ValueError('Unknown, omitted or changed source-content selection/alias count.')
    art.configure_image_names(selected)
    return selected


def require_selected_guides(assets):
    """No selected face silently falls back to global fairing or guessed anatomy."""
    missing = sorted({asset['sourceContentKey'] for asset in assets if not any(
        skin.get('_faceProfile', {}).get('operation') == 'source-guide' and lod_index(skin) == 0 for skin in asset['skins'])})
    require(not missing, 'Selected source content has no explicit native LOD0 anatomy guide: ' + ','.join(missing))


def profile_anchor(anchor: 'dict[str, Any]', packet: 'dict[str, Any]', skin: 'dict[str, Any]'):
    require(type(anchor) is dict and anchor.get('type') in ('sourceTriangle', 'sourceBindBone'), 'Invalid source landmark anchor.')
    if anchor['type'] == 'sourceTriangle':
        require(set(anchor) == {'type', 'subMesh', 'triangleOrdinal', 'barycentric'}, 'Unknown triangle anchor fields.')
        slot, ordinal = anchor['subMesh'], anchor['triangleOrdinal']
        require(type(slot) is int and 0 <= slot < len(packet['subMeshes']) and type(ordinal) is int and 0 <= ordinal < len(packet['subMeshes'][slot])//3, 'Triangle anchor is outside its native submesh.')
        require(type(anchor['barycentric']) is list and len(anchor['barycentric']) == 3 and all(type(x) in (int, float) for x in anchor['barycentric']), 'Barycentric coordinates must be numeric.')
        weights = np.asarray(anchor['barycentric'], dtype=np.float64)
        require(weights.shape == (3,) and np.all(np.isfinite(weights)) and np.all(weights >= 0) and abs(float(weights.sum())-1) < 1e-7, 'Invalid source barycentric weights.')
        indices = packet['subMeshes'][slot][ordinal*3:ordinal*3+3]
        return np.sum(np.asarray(packet['vertices']).reshape(-1, 3)[indices]*weights[:, None], axis=0)
    require(set(anchor) == {'type', 'boneIndex', 'expectedBonePath', 'localPoint'}, 'Unknown bind-bone anchor fields.')
    require(type(anchor['localPoint']) is list and len(anchor['localPoint']) == 3 and all(type(x) in (int, float) for x in anchor['localPoint']), 'Bone-local coordinates must be numeric.')
    index = anchor['boneIndex']; point = np.asarray(anchor['localPoint'], dtype=np.float64)
    require(type(index) is int and 0 <= index < len(skin['bones']) and skin['bones'][index] == anchor['expectedBonePath'], 'Source landmark bone mapping differs.')
    require(point.shape == (3,) and np.all(np.isfinite(point)), 'Invalid source bone-local point.')
    bind = np.asarray(packet['bindPoses']).reshape(-1, 4, 4)[index].T
    return (np.linalg.inv(bind) @ np.r_[point, 1.])[:3]


def load_profiles(census: 'dict[str, Any]', path, *, resource_owners=False):
    document = art.read_json(path)
    if not (type(document) is dict and set(document) == {'schemaVersion', 'profiles'} and document['schemaVersion'] == 1 and type(document['profiles']) is list): raise ValueError('Invalid face profile document.')
    by_source = {(a['sourceContentKey'], s['path']): (a, s) for a in census['assets'] for s in a['skins']}
    resolved = {}; shared = {}; identifiers = set()
    profiles: list[dict[str, Any]] = document['profiles']
    for profile in profiles:
        if not (type(profile) is dict and set(profile) == {'id', 'source', 'regions', 'points', 'operation', 'parameters', 'donorSourceFingerprint'}): raise ValueError('Unknown or missing face profile fields.')
        if not (type(profile['id']) is str and profile['id'] and profile['id'] not in identifiers): raise ValueError('Profile ID must be distinct.')
        identifiers.add(profile['id'])
        source: dict[str, Any] = profile['source']
        if not (type(source) is dict and set(source) == {'assetSourceContentKey', 'rendererPath', 'nativeFingerprint', 'vertexCount', 'bonePathsOrdered', 'rendererToPrefab'}): raise ValueError('Invalid source profile identity.')
        key = (source['assetSourceContentKey'], source['rendererPath'])
        if not (key in by_source and key not in resolved): raise ValueError('Unknown or duplicate source profile binding.')
        _, skin = by_source[key]; packet = native_packet(skin)
        if not (source['nativeFingerprint'] == packet['sourceFingerprint'] and source['vertexCount'] == packet['vertexCount'] and source['bonePathsOrdered'] == skin['bones'] and source['rendererToPrefab'] == skin['rendererToPrefab']): raise ValueError('Source profile native geometry/mapping/transform changed.')
        if not (profile['operation'] in ('surface-fairing', 'native-features', 'source-lod-transfer', 'source-guide', 'source-guide-follow')): raise ValueError('Unknown face profile operation.')
        mask = np.zeros(packet['vertexCount'])
        if not (type(profile['regions']) is list and profile['regions']): raise ValueError('A face profile requires an explicitly selected region.')
        for region in profile['regions']:
            region_role='attached-surface' if profile['operation']=='source-guide-follow' else 'face-surface'
            if not (type(region) is dict and set(region) == {'role', 'materialSlot', 'vertexWeights'} and region['role'] == region_role): raise ValueError('Invalid anatomical region.')
            if not (type(region['vertexWeights']) is list and all(type(x) in (int, float) for x in region['vertexWeights'])): raise ValueError('Source mask must contain numeric weights.')
            slot = region['materialSlot']; values = np.asarray(region['vertexWeights'], dtype=np.float64)
            if not (type(slot) is int and 0 <= slot < min(len(skin['materials']), len(packet['subMeshes']))): raise ValueError('Region material slot is outside source ownership.')
            if not (values.shape == mask.shape and np.all(np.isfinite(values)) and np.all((values >= 0) & (values <= 1)) and np.any(values > 0)): raise ValueError('Invalid or empty source face mask.')
            selected = np.zeros(len(mask), dtype=bool); selected[np.asarray(packet['subMeshes'][slot], dtype=np.int64)] = True
            if not (not np.any(values[~selected])): raise ValueError('Face mask crosses its source material slot.')
            mask = np.maximum(mask, values)
        if not (type(profile['points']) is dict and set(profile['points']).issubset({'left', 'right', 'mouth', 'nose', 'head'})): raise ValueError('Unknown source landmark semantics.')
        landmarks = {name: profile_anchor(value, packet, skin).tolist() for name, value in profile['points'].items()}
        parameters: dict[str, Any] = profile['parameters']
        if not (type(parameters) is dict): raise ValueError('Invalid profile parameters.')
        guide_controls = []
        if profile['operation'] == 'source-guide':
            if not (set(parameters) in ({'controls'},{'controls','compiledField'}) and isinstance(parameters['controls'], list) and parameters['controls']): raise ValueError('A source guide requires explicit rest-surface target controls.')
            control_ids = set()
            for control in parameters['controls']:
                if not (type(control) is dict and set(control) == {'id', 'anchor', 'target', 'radiusMetres'}): raise ValueError('Unknown source guide control fields.')
                if not (type(control['id']) is str and control['id'] and control['id'] not in control_ids): raise ValueError('Missing or duplicate guide control ID.')
                control_ids.add(control['id'])
                anchor = control['anchor']; point = profile_anchor(anchor, packet, skin)
                if not (anchor['type'] == 'sourceTriangle'): raise ValueError('A guide target requires a source-surface anchor with explicit regional ownership.')
                triangle = packet['subMeshes'][anchor['subMesh']][anchor['triangleOrdinal']*3:anchor['triangleOrdinal']*3+3]
                weight = float(np.dot(mask[triangle], anchor['barycentric']))
                if not (type(control['target']) is list and len(control['target']) == 3 and all(type(v) in (int, float) for v in control['target'])): raise ValueError('Guide targets must be three numeric source-rest coordinates.')
                target = np.asarray(control['target'], dtype=np.float64)
                radius = control['radiusMetres']
                if not (weight > 0 and target.shape == (3,) and np.all(np.isfinite(target)) and type(radius) in (int, float) and math.isfinite(radius) and radius > 0): raise ValueError('Invalid source guide target or unowned anchor.')
                guide_controls.append(dict(id=control['id'], source=point.tolist(), target=target.tolist(), sourceWeight=weight, radiusMetres=radius,sourceTriangleVertices=np.asarray(packet['vertices']).reshape(-1,3)[triangle].tolist(),sourceTriangleWeights=mask[triangle].tolist(),sourceTriangleBarycentric=anchor['barycentric']))
            if 'compiledField' in parameters:
                field: dict[str, Any]=parameters['compiledField']
                if not (type(field) is dict and set(field)=={'sourceFingerprint','coordinateDomain','centers','radii','coefficients'} and field['sourceFingerprint']==packet['sourceFingerprint'] and field['coordinateDomain']=='prefab-metres-after-native-rendererToPrefab-and-DCC-conversion'): raise ValueError('Invalid constrained guide field identity.')
                for name in ('centers','coefficients'):
                    if not (type(field[name]) is list and field[name] and all(type(v) is list and len(v)==3 and all((type(x) is int or type(x) is float) and math.isfinite(float(x)) for x in v) for v in field[name])): raise ValueError('Invalid constrained guide vector data.')
                if not (type(field['radii']) is list and len(field['centers'])==len(field['coefficients'])==len(field['radii']) and all((type(x) is int or type(x) is float) and math.isfinite(x) and x>0 for x in field['radii'])): raise ValueError('Invalid constrained guide radii.')
        elif profile['operation']=='source-guide-follow':
            if not (set(parameters)=={'anchors'} and type(parameters['anchors']) is list and len(parameters['anchors'])==packet['vertexCount'] and np.all(mask==1) and not landmarks): raise ValueError('An attached source requires complete source-triangle ownership.')
            donors=[s for a,s in by_source.values() if a['sourceContentKey']==source['assetSourceContentKey'] and s['sourceFingerprint']==profile['donorSourceFingerprint']]
            if not (len(donors)==1): raise ValueError('An attached source has no unique native donor in the same asset.')
            donor=donors[0];donor_packet=native_packet(donor)
            for anchor in parameters['anchors']:
                profile_anchor(anchor,donor_packet,donor)
                if not (anchor['type']=='sourceTriangle'): raise ValueError('An attached source requires actual source-triangle correspondences.')
        elif profile['operation'] == 'native-features':
            fields = {'bridgeProjectionMm', 'lipReliefMm', 'eyelidWidthFactor', 'lidRimMm', 'cheekContinuityMm', 'lipLiftMm', 'cornerLiftMm', 'nostrilProjectionMm'}
            if not (set(landmarks) == {'left', 'right', 'mouth', 'nose', 'head'} and set(parameters) == fields): raise ValueError('Feature operation requires actual points and explicit source art parameters.')
            if not (all(type(v) in (int, float) and math.isfinite(v) and abs(v) <= (0.1 if k == 'eyelidWidthFactor' else 2) for k,v in parameters.items())): raise ValueError('Invalid bounded source feature parameters.')
        elif profile['operation'] == 'surface-fairing':
            if not (set(parameters) == {'iterations', 'strength', 'maximumStepMm'} and type(parameters['iterations']) is int and 1 <= parameters['iterations'] <= 8 and type(parameters['strength']) in (int, float) and 0 < parameters['strength'] <= 1 and type(parameters['maximumStepMm']) in (int, float) and 0 < parameters['maximumStepMm'] <= 2): raise ValueError('Invalid bounded profile fairing parameters.')
        else:
            if not (lod_index(skin) > 0 and not parameters and not landmarks and type(profile['donorSourceFingerprint']) is str and len(profile['donorSourceFingerprint']) == 64): raise ValueError('A lower-LOD profile requires a native LOD0 donor identity.')
        if not (profile['operation'] in ('source-lod-transfer','source-guide-follow') or profile['donorSourceFingerprint'] is None): raise ValueError('Only source transfer accepts a donor.')
        ready = dict(id=profile['id'], mask=mask.tolist(), restLandmarks={k: np.asarray(v).reshape(1, 3).tolist() for k,v in landmarks.items()}, operation=profile['operation'], parameters=parameters, donorSourceFingerprint=profile['donorSourceFingerprint'])
        if guide_controls:
            ready['guideControls'] = guide_controls
            if 'compiledField' in parameters:ready['compiledGuide']=parameters['compiledField']
        semantic = json.dumps({k: v for k,v in ready.items() if k != 'id'}, sort_keys=True, separators=(',', ':'), allow_nan=False)
        fingerprint_key = (source['assetSourceContentKey'],packet['sourceFingerprint']) if resource_owners else packet['sourceFingerprint']
        if not (fingerprint_key not in shared or shared[fingerprint_key] == semantic): raise ValueError('Shared native source has inconsistent face authoring profiles.')
        shared[fingerprint_key] = semantic; resolved[key] = ready
    supplied = {p['source']['nativeFingerprint'] for p in document['profiles'] if p['operation'] != 'source-lod-transfer' and lod_index(by_source[(p['source']['assetSourceContentKey'], p['source']['rendererPath'])][1]) == 0}
    if not (all(p['operation'] != 'source-lod-transfer' or p['donorSourceFingerprint'] in supplied for p in resolved.values())): raise ValueError('Source LOD profile donor is missing.')
    for key,p in resolved.items():
        if p['operation']=='source-guide-follow':
            if not (any(other_key[0]==key[0] and by_source[other_key][1]['sourceFingerprint']==p['donorSourceFingerprint'] and other['operation'] in ('source-guide','source-lod-transfer') for other_key,other in resolved.items())): raise ValueError('An attached source requires an explicitly guided surface donor.')
    result = copy.deepcopy(census)
    for asset in result['assets']:
        guide_owned = any(p['operation'] == 'source-guide' for key, p in resolved.items() if key[0] == asset['sourceContentKey'])
        for skin in asset['skins']:
            profile = resolved.get((asset['sourceContentKey'], skin['path']))
            if profile is not None:
                skin['_faceProfile'] = profile
            elif guide_owned:
                skin['_guideCompanionPreserved'] = True
    return result



def anatomy_triangle_distances(points, vertices, triangles):
    a, b, c = (vertices[triangles[:, i]] for i in range(3))
    ab = b - a
    ac = c - a
    d00 = np.sum(ab * ab, axis=1)
    d01 = np.sum(ab * ac, axis=1)
    d11 = np.sum(ac * ac, axis=1)
    denominator = d00 * d11 - d01 * d01
    valid = denominator > 0
    result = []
    for p in points:
        ap = p - a
        d20 = np.sum(ap * ab, axis=1)
        d21 = np.sum(ap * ac, axis=1)
        v = np.divide(d11 * d20 - d01 * d21, denominator, out=np.zeros(len(a)), where=valid)
        w = np.divide(d00 * d21 - d01 * d20, denominator, out=np.zeros(len(a)), where=valid)
        projection = a + v[:, None] * ab + w[:, None] * ac
        distances = np.sum((projection - p) ** 2, axis=1)
        distances[~(valid & (v >= 0) & (w >= 0) & (v + w <= 1))] = np.inf
        for start, end in ((a, b), (b, c), (c, a)):
            edge = end - start
            length = np.sum(edge * edge, axis=1)
            t = np.clip(np.divide(np.sum((p - start) * edge, axis=1), length, out=np.zeros(len(a)), where=length > 0), 0, 1)
            distances = np.minimum(distances, np.sum((start + t[:, None] * edge - p) ** 2, axis=1))
        result.append(float(np.sqrt(distances.min())))
    return np.asarray(result)


def anatomy_influence(packet: 'dict[str, Any]', bones, tokens):
    indices = np.asarray(packet['boneIndices']).reshape(-1, 4)
    weights = np.asarray(packet['weights']).reshape(-1, 4)
    matched = np.asarray([any((token in name for token in tokens)) for name in bones], dtype=float)
    return np.sum(weights * matched[indices], axis=1)


def face_surface_strength(packet: 'dict[str, Any]', skin: 'dict[str, Any]'):
    """Keep articulated contact tissue fixed and retain each source face's proportions."""
    bones=[name.rsplit('/',1)[-1].lower()if name else''for name in skin['bones']]
    contact=anatomy_influence(packet,bones,('eyelid','lip_'))
    facial=anatomy_influence(packet,bones,('eyelid','lip_','nostril','cheek','eyebrow'))
    smooth=lambda value:(lambda t:t*t*(3-2*t))(np.clip(value,0.,1.))
    head_paths=['bip001 head'if 'bip001 head'in name.lower().split('/')or name.lower()=='bip001 jaw'else''for name in skin['bones']]
    head=anatomy_influence(packet,head_paths,('bip001 head',))
    facial=np.maximum(facial,smooth((head-.5)*2))
    strength=(1-.3*smooth(facial/.25))*(1-smooth((contact-.02)/.23))
    for slot,material in enumerate(skin.get('materials',[])):
        if material and material['shader']=='Custom/CharacterEye'and slot<len(packet['subMeshes']):
            strength[np.asarray(packet['subMeshes'][slot],dtype=int)]=0
    # UV aliases of the same tissue must share one contact constraint.
    _,groups=np.unique(np.asarray(packet['vertices']).reshape(-1,3),axis=0,return_inverse=True)
    minimum=np.ones(int(groups.max())+1)if len(groups)else np.empty(0)
    np.minimum.at(minimum,groups,strength)
    return minimum[groups]


def retain_source_proportions(native: 'dict[str, Any]', authored: 'dict[str, Any]', skin: 'dict[str, Any]', strength=None):
    """Finish a prepared surface against its own native topology and contacts."""
    count=native['vertexCount'];total=authored['vertexCount']
    require(0<count<=total,'A prepared face lost its native vertex prefix.')
    factor=face_surface_strength(native,skin)if strength is None else np.asarray(strength,float)
    require(factor.shape==(count,)and np.isfinite(factor).all()and np.all((factor>=0)&(factor<=1)),'Invalid source facial strength.')
    for name in ('weights','boneIndices'):
        a=np.asarray(native[name]).reshape(-1,4);b=np.asarray(authored[name][:count*4]).reshape(-1,4)
        require(a.shape==b.shape and np.allclose(a[factor<1],b[factor<1],rtol=0,atol=1e-6),'The native facial skin palette changed: '+name)
    require(native['bindPoses']==authored['bindPoses'][:len(native['bindPoses'])],'A native facial bind pose changed.')
    factor=np.r_[factor,np.ones(total-count)]
    protected=factor==0;untouched=factor==1;touched=~untouched
    result=copy.deepcopy(authored)
    source_p=np.asarray(native['vertices'],float).reshape(-1,3)
    source_n=np.asarray(native['normals'],float).reshape(-1,3)
    source_t=np.asarray(native['tangents'],float).reshape(-1,4)
    old_p=np.asarray(authored['vertices'],float).reshape(-1,3)
    old_n=np.asarray(authored['normals'],float).reshape(-1,3)
    old_t=np.asarray(authored['tangents'],float).reshape(-1,4)
    require(len(source_n)==len(source_t)==count and len(old_n)==len(old_t)==total,'A prepared facial frame is incomplete.')
    triangles=np.concatenate([np.asarray(slot,np.int64).reshape(-1,3)for slot in authored['subMeshes']])
    groups=normal_groups(old_p,normalise(old_n))
    affected=np.any(touched[triangles],axis=1)
    native_triangles=np.all(triangles<count,axis=1)
    def orientation_reference(a,na):
        points=a[triangles];reference=np.cross(points[:,1]-points[:,0],points[:,2]-points[:,0])
        points=na[triangles[native_triangles]]
        reference[native_triangles]=np.cross(points[:,1]-points[:,0],points[:,2]-points[:,0])
        return reference
    def orientation_limit(a,na):
        baseline=a.copy();baseline[:count][touched[:count]]=na[touched[:count]]
        target=a.copy();target[:count]=na+(a[:count]-na)*factor[:count,None]
        target[untouched]=a[untouched]
        p=baseline[triangles];delta=(target-baseline)[triangles]
        e1,e2=p[:,1]-p[:,0],p[:,2]-p[:,0];d1,d2=delta[:,1]-delta[:,0],delta[:,2]-delta[:,0]
        reference=orientation_reference(a,na);eligible=affected&(np.linalg.norm(reference,axis=1)>1e-16)
        aa=np.sum(reference*np.cross(d1,d2),axis=1)
        bb=np.sum(reference*(np.cross(d1,e2)+np.cross(e1,d2)),axis=1)
        cc=np.sum(reference*np.cross(e1,e2),axis=1)-np.sum(reference*reference,axis=1)*1e-6
        tolerance=np.finfo(float).eps*np.maximum(np.maximum(np.abs(aa),np.abs(bb)),np.abs(cc))*16
        end_value=aa+bb+cc;minimum=np.minimum(cc,end_value)
        convex=(aa>tolerance);stationary=np.zeros_like(aa)
        np.divide(-bb,2*aa,out=stationary,where=convex)
        interior=convex&(stationary>0)&(stationary<1)
        minimum[interior]=np.minimum(minimum[interior],(aa[interior]*stationary[interior]+bb[interior])*stationary[interior]+cc[interior])
        forbidden=[]
        for index in np.flatnonzero(eligible&(minimum<=0)):
            a,b,c=aa[index],bb[index],cc[index];roots=[]
            if abs(a)<=tolerance[index]:
                if abs(b)>tolerance[index]:roots=[-c/b]
            else:
                discriminant=b*b-4*a*c
                if discriminant>=0:
                    q=-.5*(b+math.copysign(math.sqrt(discriminant),b))
                    roots=[q/a]+([c/q]if q else[])
            knots=sorted({0.,1.,*[float(root)for root in roots if 0<root<1]})
            for low,high in zip(knots,knots[1:]):
                middle=(low+high)/2
                if (a*middle+b)*middle+c<=0:forbidden.append((low,high))
            if c<=0:forbidden.append((0.,0.))
            if a+b+c<=0:forbidden.append((1.,1.))
        return forbidden
    source_shapes={shape['name']:shape for shape in native['shapes']}
    require(source_shapes.keys()<={shape['name']for shape in authored['shapes']},'A native facial channel was removed.')
    frame_pairs=[];inherited_frames=0;zero=[0.]* (count*3)
    for old_shape,new_shape in zip(authored['shapes'],result['shapes']):
        source_shape=source_shapes.get(old_shape['name'])
        source_frames={frame['weight']:frame for frame in source_shape['frames']}if source_shape else{}
        if source_shape:require(source_frames.keys()<={frame['weight']for frame in old_shape['frames']},'A native facial frame was removed.')
        for old_frame,new_frame in zip(old_shape['frames'],new_shape['frames']):
            source_frame=source_frames.get(old_frame['weight'])
            if source_frame is None:
                require(source_shape is None,'A native facial channel added an unsupported frame weight.')
                source_frame=dict(weight=old_frame['weight'],vertices=zero,normals=zero,tangents=zero);inherited_frames+=1
            frame_pairs.append((source_frame,old_frame,new_frame))
    forbidden=orientation_limit(old_p,source_p)
    for source_frame,old_frame,_ in frame_pairs:
        forbidden.extend(orientation_limit(old_p+np.asarray(old_frame['vertices']).reshape(-1,3),source_p+np.asarray(source_frame['vertices']).reshape(-1,3)))
    merged=[]
    for low,high in sorted(forbidden):
        if merged and low<=merged[-1][1]:merged[-1]=(merged[-1][0],max(merged[-1][1],high))
        else:merged.append((low,high))
    limit=1.
    for index in range(len(merged)-1,-1,-1):
        low,high=merged[index]
        if low<=limit<=high:
            previous=merged[index-1][1]if index else 0.
            limit=low-(low-previous)*1e-4
            require(low>previous,'No common facial strength preserves all native stored poses.')
    factor[touched]*=limit
    def finish(a,b,c,na,nb,nc):
        positions=a.copy();positions[:count]=na+(a[:count]-na)*factor[:count,None];positions[untouched]=a[untouched]
        positions[protected]=na[protected[:count]]
        before=surface_normals(a,triangles,groups,a,normalise(b))
        after=surface_normals(positions,triangles,groups,a,normalise(b))
        normals=normalise(rotate_between(b,before,after))
        tangents=rotate_between(c,before,after)
        tangents=normalise(tangents-normals*np.sum(tangents*normals,axis=1,keepdims=True))
        normals[untouched]=b[untouched];tangents[untouched]=c[untouched]
        normals[protected]=nb[protected[:count]];tangents[protected]=nc[protected[:count]]
        new=positions[triangles]
        new_area=np.cross(new[:,1]-new[:,0],new[:,2]-new[:,0])
        reference=orientation_reference(a,na)
        valid=(np.sum(reference*new_area,axis=1)>0)|(np.linalg.norm(reference,axis=1)<=1e-16)
        require(bool(np.all(valid|~affected)),'A finished facial pose reverses native tissue.')
        return positions,normals,tangents
    base_p,base_n,base_t=finish(old_p,old_n,old_t[:,:3],source_p,source_n,source_t[:,:3])
    for key,value in [('vertices',base_p),('normals',base_n),('tangents',np.column_stack((base_t,old_t[:,3])))]:
        result[key]=value.astype(np.float32).ravel().tolist()
    frames=0
    for source_frame,old_frame,new_frame in frame_pairs:
        old_delta=[np.asarray(old_frame[key],float).reshape(-1,3)for key in ('vertices','normals','tangents')]
        source_delta=[np.asarray(source_frame[key],float).reshape(-1,3)for key in ('vertices','normals','tangents')]
        pp,nn,tt=finish(old_p+old_delta[0],old_n+old_delta[1],old_t[:,:3]+old_delta[2],
            source_p+source_delta[0],source_n+source_delta[1],source_t[:,:3]+source_delta[2])
        for key,value,base,old,source in zip(('vertices','normals','tangents'),(pp,nn,tt),(base_p,base_n,base_t),old_delta,source_delta):
            delta=value-base;delta[untouched]=old[untouched];delta[protected]=source[protected[:count]]
            new_frame[key]=delta.astype(np.float32).ravel().tolist()
        frames+=1
    result['bounds']=physical_mesh_bounds(result);result['sourceFingerprint']=fingerprint(result)[0]
    for key,width in [('vertices',3),('normals',3),('tangents',4)]:
        require(np.array_equal(np.asarray(result[key],np.float32).reshape(-1,width)[untouched],
            np.asarray(authored[key],np.float32).reshape(-1,width)[untouched]),'An unowned body channel changed.')
        require(np.array_equal(np.asarray(result[key],np.float32).reshape(-1,width)[protected],
            np.asarray(native[key],np.float32).reshape(-1,width)[protected[:count]]),'A native contact channel changed.')
    return result,dict(nativeVertices=count,preparedVertices=total,contactVertices=int(protected.sum()),
        refinedVertices=int(np.count_nonzero((factor>0)&(factor<1))),frames=frames,nativeContactChannelsExact=True,
        unownedBodyChannelsExact=True,frameIdentityAndSkinPalettePreserved=True,allStoredPoseOrientationsPreserved=True,
        commonStoredPoseStrengthLimit=float(limit),inheritedBodyFrames=inherited_frames)


def lower_face_strength(packet,donor,donor_strength):
    """Carry source LOD0 contact ownership into a lower facial LOD."""
    positions=np.asarray(packet['vertices'],float).reshape(-1,3)
    source=np.asarray(donor['vertices'],float).reshape(-1,3);values=np.asarray(donor_strength,float)
    result=np.ones(len(positions))
    for first in range(0,len(positions),128):
        current=positions[first:first+128];distance=np.sum((current[:,None]-source[None])**2,axis=2)
        nearest=np.argpartition(distance,2,axis=1)[:,:3];square=np.take_along_axis(distance,nearest,axis=1)
        weights=1/np.maximum(square,1e-12);weights/=weights.sum(axis=1,keepdims=True)
        result[first:first+len(current)]=np.where(square.min(axis=1)<.015**2,np.sum(values[nearest]*weights,axis=1),1.)
    return result


def anatomy_surface_anchor(positions, normals, ownership, point, candidates, *, outward_direction=(0., -1., 0.)):
    ids = np.flatnonzero((ownership > 0) & candidates)
    if not len(ids):
        raise ValueError('No owned native surface exists at the declared anatomical region.')
    direction = np.asarray(outward_direction, dtype=float)
    require(direction.shape == (3,) and np.isfinite(direction).all() and np.linalg.norm(direction) > 0,
            'An anatomical surface direction must be a finite nonzero vector.')
    outward = normals[ids] @ direction > 0
    if not np.any(outward):
        raise ValueError('The declared anatomical tissue has no native surface facing its owned direction.')
    ids = ids[outward]
    return int(ids[np.argmin(np.sum((positions[ids] - point) ** 2, axis=1))])


def anatomy_edge_path(start, end, graph, positions):
    import heapq
    distances = {start: 0.0}
    previous = {}
    queue = [(0.0, start)]
    while queue:
        distance, vertex = heapq.heappop(queue)
        if distance != distances[vertex]:
            continue
        if vertex == end:
            path = [end]
            while path[-1] != start:
                path.append(previous[path[-1]])
            return list(reversed(path))
        for neighbor in graph[vertex]:
            candidate = distance + float(np.linalg.norm(positions[vertex] - positions[neighbor]))
            if candidate < distances.get(neighbor, float('inf')):
                distances[neighbor] = candidate
                previous[neighbor] = vertex
                heapq.heappush(queue, (candidate, neighbor))
    raise ValueError('Anatomical surface sections must remain on connected native triangles.')


def anatomy_contact_path(positions, graph, score, metric, horizontal, endpoints=None):
    import heapq
    allowed = score > 0
    ids = np.flatnonzero(allowed)
    if len(ids) < 4:
        raise ValueError('The actual anatomical contact has insufficient native surface samples.')
    projection = positions @ horizontal
    low = ids[metric[ids] <= np.quantile(metric[ids], 0.65)]
    first, last = (int(low[np.argmin(projection[low])]), int(low[np.argmax(projection[low])])) if endpoints is None else endpoints
    if not allowed[first] or not allowed[last]:
        raise ValueError('A contact endpoint lies outside its own connected tissue.')
    typical = np.median([np.linalg.norm(positions[a] - positions[b]) for a in ids for b in graph[a] if allowed[b]])
    distance = {first: 0.0}
    previous = {}
    queue = [(0.0, first)]
    while queue:
        value, current = heapq.heappop(queue)
        if value != distance[current]:
            continue
        if current == last:
            result = [last]
            while result[-1] != first:
                result.append(previous[result[-1]])
            return result[::-1]
        for adjacent in graph[current]:
            if not allowed[adjacent]:
                continue
            edge = np.linalg.norm(positions[current] - positions[adjacent])
            candidate = value + edge * (1 + (metric[current] + metric[adjacent]) / (2 * typical)) ** 2
            if candidate < distance.get(adjacent, float('inf')):
                distance[adjacent] = candidate
                previous[adjacent] = current
                heapq.heappush(queue, (candidate, adjacent))
    raise ValueError('The actual contact tissue has no connected native edge curve.')


def anatomy_contact_curves(asset, skin: 'dict[str, Any]', packet: 'dict[str, Any]', positions, normals, graph, representative, ownership, functions, marks):
    horizontal = marks['right'] - marks['left']
    horizontal /= np.linalg.norm(horizontal)
    vertical = (marks['left'] + marks['right']) * 0.5 - marks['mouth']
    vertical -= horizontal * np.dot(vertical, horizontal)
    vertical /= np.linalg.norm(vertical)
    front = np.cross(horizontal, vertical)
    if np.dot(front, marks['nose'] - (marks['left'] + marks['right']) * 0.5) < 0:
        front = -front
    exterior = normals @ front > 0
    bones = [b.rsplit('/', 1)[-1].lower() if b else '' for b in skin['bones']]

    def region(tokens):
        return anatomy_influence(packet, bones, tokens)[representative] * exterior * (ownership > 0)
    eyes = [s for s in asset['skins'] if s['path'].lower().endswith('lod0') and any((m and m['shader'] == 'Custom/CharacterEye' for m in s['materials']))]
    if len(eyes) != 1:
        raise ValueError('An unambiguous native ocular owner is required for contact construction.')
    eye = eyes[0]
    ep = native_packet(eye)
    ev = functions['points'](ep['vertices'], art.CONVERT @ functions['matrix'](eye['rendererToPrefab']))
    et = np.concatenate([np.asarray(s, dtype=int).reshape(-1, 3) for s in ep['subMeshes']])
    eye_bones = [b.rsplit('/', 1)[-1].lower() if b else '' for b in eye['bones']]
    curves = {}
    for side in ('l', 'r'):
        ew = anatomy_influence(ep, eye_bones, ('b_eye_' + side,))
        triangles = et[np.all(ew[et] > 0.5, axis=1)]
        if not len(triangles):
            raise ValueError('The actual ocular source has no independently owned globe triangles.')
        globe = ev[np.unique(triangles)]
        center = globe.mean(axis=0)
        radius = np.linalg.norm(globe - center, axis=1).max()
        nearby = np.linalg.norm(positions - center, axis=1) < radius * 1.6
        metric = np.full(len(positions), np.inf)
        metric[nearby] = anatomy_triangle_distances(positions[nearby], ev, triangles)
        for part in ('upper', 'lower'):
            curves['lid-' + part + '-' + side] = anatomy_contact_path(positions, graph, region(('eyelid_' + part + '_' + side,)) * nearby, metric, horizontal)
    upper = region(('lip_upper',))
    lower = region(('lip_lower',))
    corner = region(('lip_corner',))
    corners = []
    for side in ('l', 'r'):
        weights = region(('lip_corner_' + side,))
        ids = np.flatnonzero((weights > 0) & (weights >= upper) & (weights >= lower))
        if not len(ids):
            raise ValueError('The native lip corner has no exterior owned tissue.')
        sign = -1 if np.mean((positions[ids] - marks['mouth']) @ horizontal) < 0 else 1
        corners.append(int(ids[np.argmax(sign * (positions[ids] @ horizontal))]))
    corners.sort(key=lambda a: positions[a] @ horizontal)
    shared = (corner >= upper) & (corner >= lower)
    for part, score, opposite in [('upper', (upper + corner) * ((upper >= lower) | shared), lower), ('lower', (lower + corner) * ((lower >= upper) | shared), upper)]:
        other = positions[opposite > 0]
        if not len(other):
            raise ValueError('The native lip surface has no opposite aperture tissue.')
        metric = np.min(np.linalg.norm(positions[:, None] - other[None], axis=2), axis=1)
        curves['lip-' + part] = anatomy_contact_path(positions, graph, score, metric, horizontal, corners)
    return curves


def anatomy_curvature_field(positions, normals, graph, anchors, radius, path_cache):
    import heapq
    "Fit a person's connected native tissue, preserving its anatomical crests.\n\n    A depth quadric follows the observed patch rather than bridging a face with\n    an invented chord. A constrained fit keeps the actual crest knots fixed.\n    Its support follows mesh distance and cannot cross an eye or oral island.\n    "
    paths = []
    for a, b in zip(anchors[:-1], anchors[1:]):
        if (a, b) not in path_cache:
            path_cache[a, b] = anatomy_edge_path(a, b, graph, positions)
        paths.extend(path_cache[a, b])
    path = sorted(set(paths))
    distances = np.full(len(positions), np.inf)
    distances[path] = 0
    queue = [(0.0, a) for a in path]
    heapq.heapify(queue)
    while queue:
        distance, current = heapq.heappop(queue)
        if distance != distances[current] or distance > radius:
            continue
        for adjacent in graph[current]:
            candidate = distance + float(np.linalg.norm(positions[current] - positions[adjacent]))
            if candidate < distances[adjacent] and candidate <= radius:
                distances[adjacent] = candidate
                heapq.heappush(queue, (candidate, adjacent))
    weight = np.maximum(0.0, 1 - distances / radius) ** 4 * (4 * np.minimum(distances / radius, 1) + 1)
    ids = np.flatnonzero(weight > 0)
    if len(ids) < 6:
        raise ValueError('The owned anatomical patch has insufficient surface samples.')
    normal = np.sum(normals[ids] * weight[ids, None], axis=0)
    normal /= np.linalg.norm(normal)
    direction = positions[anchors[-1]] - positions[anchors[0]]
    direction -= normal * np.dot(direction, normal)
    if np.linalg.norm(direction) < 1e-10:
        raise ValueError('The owned anatomical patch has no stable native tangent.')
    direction /= np.linalg.norm(direction)
    across = np.cross(normal, direction)
    center = np.average(positions[ids], axis=0, weights=weight[ids])
    uv = np.column_stack(((positions - center) @ direction, (positions - center) @ across)) / radius
    design = np.column_stack((np.ones(len(positions)), uv, uv[:, 0] ** 2, uv[:, 0] * uv[:, 1], uv[:, 1] ** 2))
    depth = (positions - center) @ normal / radius
    compatibility = np.maximum(0.0, np.sum(normals * normal, axis=1))
    weight *= np.maximum(0, (compatibility - 0.5) / 0.5) ** 2
    ids = np.flatnonzero(weight > 0)
    if len(ids) < 6:
        raise ValueError('The native tissue patch has insufficient compatible normal samples.')
    constraints = design[anchors]
    rhs = depth[anchors]
    fit_weight = weight.copy()
    coeff = None
    for iteration in range(3):
        left = design[ids].T @ (design[ids] * fit_weight[ids, None])
        target = design[ids].T @ (depth[ids] * fit_weight[ids])
        system = np.block([[left, constraints.T], [constraints, np.zeros((len(anchors), len(anchors)))]])
        if np.linalg.matrix_rank(system) < len(system):
            raise ValueError('The native anatomical surface constraints are dependent.')
        coeff = np.linalg.solve(system, np.r_[target, rhs])[:6]
        residual = np.abs(design[ids] @ coeff - depth[ids])
        scale = np.median(residual)
        if scale <= np.finfo(float).eps:
            break
        fit_weight[ids] = weight[ids] * np.minimum(1.0, scale / np.maximum(residual, scale))
    if coeff is None:raise ValueError('The native anatomical surface fit did not produce coefficients.')
    correction = (design @ coeff - depth) * radius
    shift = normals * np.divide(correction, compatibility, out=np.zeros_like(correction), where=compatibility > 0.5)[:, None]
    shift[anchors] = 0
    return (shift * weight[:, None], weight, dict(nativePathVertices=len(path), knotVertices=anchors, supportRadiusMetres=radius, nativePatchSamples=len(ids), nativeSectionLengthMetres=float(np.sum(np.linalg.norm(np.diff(positions[anchors], axis=0), axis=1))), largestSectionCorrectionMetres=float(np.linalg.norm(shift[ids], axis=1).max()), sourcePatchFit='native weighted constrained robust quadric; actual anatomical crest knots fixed', maximumKnotResidualMetres=float(np.abs(constraints @ coeff - rhs).max() * radius)))


def anatomy_orientation_review(source, candidate, triangles):
    before = np.cross(source[triangles[:, 1]] - source[triangles[:, 0]], source[triangles[:, 2]] - source[triangles[:, 0]])
    after = np.cross(candidate[triangles[:, 1]] - candidate[triangles[:, 0]], candidate[triangles[:, 2]] - candidate[triangles[:, 0]])
    square = np.sum(before * before, axis=1)
    edge = np.maximum(np.linalg.norm(source[triangles[:, 1]] - source[triangles[:, 0]], axis=1), np.linalg.norm(source[triangles[:, 2]] - source[triangles[:, 0]], axis=1))
    supported = np.sqrt(square) > np.finfo(np.float32).eps * 16 * (np.max(np.abs(source[triangles]), axis=(1, 2)) * edge + edge * edge)
    ratio = np.divide(np.sum(before * after, axis=1), square, out=np.ones_like(square), where=square > 0)
    bad = np.flatnonzero(supported & (ratio < 0.05))
    return (bad, float(ratio[supported].min()) if np.any(supported) else None)


def native_anatomy_surface_targets(asset, skin: 'dict[str, Any]', packet: 'dict[str, Any]', functions):
    transform = art.CONVERT @ functions['matrix'](skin['rendererToPrefab'])
    positions = functions['points'](packet['vertices'], transform)
    normals = functions['native_normals'](packet, transform)
    bones, origins, marks = functions['landmarks'](packet, skin, transform)
    facial_influence = anatomy_influence(packet, bones, ('eyelid', 'lip_', 'nostril', 'cheek', 'eyebrow'))
    face_slots = [slot for slot, indices in enumerate(packet['subMeshes']) if indices and np.any(facial_influence[np.asarray(indices, dtype=int)] > 0)]
    if not face_slots:
        return dict(status='ANATOMY_OWNERS_REQUIRE_SOURCE_SECTIONS', name=asset['name'], sourceFingerprint=skin['sourceFingerprint'], sourceContentKey=asset['sourceContentKey'], preservationReason='The primary is a nonstandard or combined-body source without an owned articulated facial material slot. Preserve its native whole-body data; its head/muzzle target declaration remains explicit in the complete source queue.')
    triangles = np.concatenate([np.asarray(packet['subMeshes'][slot], dtype=int).reshape(-1, 3) for slot in face_slots])
    physical_owners = {}
    memberships = []
    source_positions = np.asarray(packet['vertices'], np.float32).reshape(-1, 3)
    skin_indices = np.asarray(packet['boneIndices']).reshape(-1, 4)
    skin_weights = np.asarray(packet['weights'], np.float32).reshape(-1, 4)
    for point, indices, weights in zip(source_positions, skin_indices, skin_weights):
        influences = tuple(sorted(((int(index), float(weight)) for index, weight in zip(indices, weights) if weight != 0)))
        memberships.append(physical_owners.setdefault((tuple(point), influences), len(physical_owners)))
    groups = np.asarray(memberships, dtype=int)
    _, representative = np.unique(groups, return_index=True)
    edges = np.sort(np.concatenate((groups[triangles[:, [0, 1]]], groups[triangles[:, [1, 2]]], groups[triangles[:, [2, 0]]])), axis=1)
    edges = edges[edges[:, 0] != edges[:, 1]]
    unique, counts = np.unique(edges, axis=0, return_counts=True)
    boundary_groups = set(unique[counts == 1].ravel().tolist())
    boundary = np.asarray([group in boundary_groups for group in groups],dtype=bool)
    face_vertices = np.zeros(len(positions), dtype=bool)
    face_vertices[triangles.ravel()] = True
    head_roots = [path for path in skin['bones'] if path and path.rsplit('/', 1)[-1].lower() == 'bip001 head']
    if len(head_roots) != 1:
        return dict(status='ANATOMY_OWNERS_REQUIRE_SOURCE_SECTIONS', name=asset['name'], sourceFingerprint=skin['sourceFingerprint'], sourceContentKey=asset['sourceContentKey'], preservationReason='The source has no unambiguous native head bone owner. Keep its combined-body/other surface unchanged until its own anatomical ownership is declared.')
    head_root = head_roots[0]
    head_bones = np.asarray([bool(path and (path == head_root or path.startswith(head_root + '/'))) for path in skin['bones']], dtype=float)
    head_influence = np.sum(skin_weights * head_bones[skin_indices], axis=1)
    neck_influence = anatomy_influence(packet, bones, ('neck', 'spine', 'clavicle'))
    ownership = (~boundary & face_vertices).astype(float) * functions['smooth'](1 - np.clip(neck_influence, 0, 1)) * functions['smooth'](head_influence)
    group_ownership = np.ones(len(representative))
    np.minimum.at(group_ownership, groups, ownership)
    ownership = group_ownership[groups]
    named = {name: index for index, name in enumerate(bones)}
    graph = [set() for _ in representative]
    for left, right in unique:
        if left != right:
            graph[int(left)].add(int(right))
            graph[int(right)].add(int(left))
    vertices = positions[representative]
    vertex_normals = normals[representative]
    mask = ownership[representative]
    components = np.full(len(vertices), -1, dtype=int)
    for seed in range(len(vertices)):
        if components[seed] >= 0 or not graph[seed]:
            continue
        pending = [seed]
        components[seed] = seed
        while pending:
            current = pending.pop()
            for adjacent in graph[current]:
                if components[adjacent] < 0:
                    components[adjacent] = seed
                    pending.append(adjacent)
    owned_component = max(set(components[components >= 0].tolist()), key=lambda c: float(facial_influence[representative][components == c].sum()))
    mask = mask * (components == owned_component)
    if any((marks.get(role) is None for role in ('left', 'right', 'mouth', 'nose'))):
        return dict(status='ANATOMY_OWNERS_REQUIRE_SOURCE_SECTIONS', name=asset['name'], sourceFingerprint=skin['sourceFingerprint'], sourceContentKey=asset['sourceContentKey'], preservationReason='This source does not declare the paired ocular, lip and nasal anatomy needed by these tissue patches. Preserve its own muzzle/head/jaw topology and retain its anatomy-specific authoring row.')
    pending_contacts = []
    try:
        contacts = anatomy_contact_curves(asset, skin, packet, vertices, vertex_normals, graph, representative, mask, functions, marks)
    except ValueError as error:
        contacts = {}
        pending_contacts.append(dict(id='native-ocular-and-oral-contact-curves', reason=str(error)))
        missing = anatomy_influence(packet, bones, ('eyelid', 'lip_'))[representative] > 0
        mask[missing] = 0
    sections = []
    side_anchors = {}
    for side in ('l', 'r'):
        names = {'lid': 'b_eyelid_lower_' + side, 'upperCheek': 'b_cheek_upper_' + side, 'lowerCheek': 'b_cheek_lower_' + side, 'upperLid': 'b_eyelid_upper_' + side, 'brow': 'b_eyebrow_' + side + '_01'}
        anchors = {}
        for role, name in names.items():
            if role in ('lid', 'upperLid') and (not contacts):
                continue
            if name not in named:
                continue
            weights = anatomy_influence(packet, bones, (name,))[representative]
            if np.max(weights) <= 0:
                continue
            if not np.any((weights > np.max(weights) * 0.2) & (mask > 0) & (vertex_normals[:, 1] < 0)):
                pending_contacts.append(dict(id=role + '-owned-surface-' + side, reason='No exterior native tissue remains after contact/neck ownership protection.'))
                continue
            anchors[role] = anatomy_surface_anchor(vertices, vertex_normals, mask, origins[named[name]], weights > np.max(weights) * 0.2)
        for role, part in [('lid', 'lower'), ('upperLid', 'upper')]:
            curve = contacts.get('lid-' + part + '-' + side)
            if not curve:
                continue
            span = np.r_[0, np.cumsum(np.linalg.norm(np.diff(vertices[curve], axis=0), axis=1))]
            anchors[role] = int(curve[np.argmin(np.abs(span - span[-1] * 0.5))])
        side_anchors[side] = anchors
        if {'lid', 'upperCheek', 'lowerCheek'}.issubset(anchors):
            path = [anchors[name] for name in ('lid', 'upperCheek', 'lowerCheek')]
            lengths = np.linalg.norm(np.diff(vertices[path], axis=0), axis=1)
            sections.append(dict(id='orbital-malar-lower-cheek-' + side, anchors=path, radius=float(np.mean(lengths))))
        if {'upperLid', 'brow'}.issubset(anchors):
            path = [anchors['upperLid'], anchors['brow']]
            sections.append(dict(id='outer-upper-lid-roll-' + side, anchors=path, radius=float(np.linalg.norm(vertices[path[1]] - vertices[path[0]])) * 0.5))
        if {'lid', 'upperCheek'}.issubset(anchors):
            path = [anchors['lid'], anchors['upperCheek']]
            sections.append(dict(id='outer-lower-lid-roll-' + side, anchors=path, radius=float(np.linalg.norm(vertices[path[1]] - vertices[path[0]])) * 0.5))
    jaw_weights = anatomy_influence(packet, bones, ('jaw', 'lip_lower', 'cheek_lower'))[representative]
    mouth = marks.get('mouth')
    lower = (jaw_weights > 0) & (mask > 0) & (vertex_normals[:, 1] < 0)
    if mouth is not None:
        lower &= vertices[:, 2] < mouth[2]
    if np.any(lower):
        ids = np.flatnonzero(lower)
        chin = int(ids[np.argmin(vertices[ids, 2])])
        middle_x = vertices[chin, 0]
        for side, sign in [('l', -1), ('r', 1)]:
            candidates = ids[(vertices[ids, 0] - middle_x) * sign > 0]
            if len(candidates) and 'lowerCheek' in side_anchors[side]:
                jaw = int(candidates[np.argmax((vertices[candidates, 0] - middle_x) * sign)])
                path = [side_anchors[side]['lowerCheek'], jaw, chin]
                if len(set(path)) == len(path):
                    lengths = np.linalg.norm(np.diff(vertices[path], axis=0), axis=1)
                    sections.append(dict(id='lower-cheek-jaw-chin-' + side, anchors=path, radius=float(np.mean(lengths)) * 0.5))
    if all(('brow' in side_anchors[side] for side in ('l', 'r'))):
        brow_pair = [side_anchors[side]['brow'] for side in ('l', 'r')]
        brow = np.mean(vertices[brow_pair], axis=0)
        width = abs(vertices[brow_pair[0], 0] - vertices[brow_pair[1], 0])
        frontal = (mask > 0) & (vertex_normals[:, 1] < 0) & (vertices[:, 2] > np.max(vertices[brow_pair, 2])) & (np.abs(vertices[:, 0] - brow[0]) < width * 0.5)
        if np.any(frontal):
            intended = brow.copy()
            intended[2] = (float(np.max(vertices[frontal, 2])) + brow[2]) * 0.5
            forehead = anatomy_surface_anchor(vertices, vertex_normals, mask, intended, frontal)
            path = [brow_pair[0], forehead, brow_pair[1]]
            sections.append(dict(id='forehead-brow-temple-connection', anchors=path, radius=width * 0.5))
    alar = {}
    for side in ('l', 'r'):
        name = 'b_nostril_' + side
        if name not in named:
            continue
        support = anatomy_influence(packet, bones, (name,))[representative]
        if np.max(support) > 0:
            candidates = np.flatnonzero((support > np.max(support) * 0.2) & (mask > 0) & (vertex_normals[:, 1] < 0))
            if len(candidates):
                sign = np.sign(origins[named[name], 0] - marks['nose'][0])
                alar[side] = int(candidates[np.argmax((vertices[candidates, 0] - marks['nose'][0]) * sign)])
    if len(alar) == 2 and marks.get('left') is not None and (marks.get('right') is not None):
        alar_vertices = vertices[[alar['l'], alar['r']]]
        middle = np.mean(alar_vertices, axis=0)
        eye_z = (marks['left'][2] + marks['right'][2]) * 0.5
        half_width = abs(alar_vertices[0, 0] - alar_vertices[1, 0]) * 0.5
        middle_band = (mask > 0) & (vertex_normals[:, 1] < 0) & (np.abs(vertices[:, 0] - middle[0]) <= half_width) & (vertices[:, 2] >= np.min(alar_vertices[:, 2])) & (vertices[:, 2] <= eye_z)
        if np.any(middle_band):
            tip_ids = np.flatnonzero(middle_band)
            tip = int(tip_ids[np.argmin(vertices[tip_ids, 1])])
            intended = middle.copy()
            intended[2] = (middle[2] + eye_z) * 0.5
            intended[1] = vertices[tip, 1]
            bridge = anatomy_surface_anchor(vertices, vertex_normals, mask, intended, middle_band)
            bridge_path = [bridge, tip]
            if len(set(bridge_path)) == 2:
                sections.append(dict(id='nasal-bridge-and-tip', anchors=bridge_path, radius=float(np.linalg.norm(vertices[tip] - vertices[bridge])) * 0.5))
            for side in ('l', 'r'):
                path = [tip, alar[side]]
                if len(set(path)) == 2:
                    radius = float(np.linalg.norm(vertices[path[1]] - vertices[path[0]])) * 0.5
                    sections.append(dict(id='nasal-alar-fold-' + side, anchors=path, radius=radius))
    corners = {}
    for side in ('l', 'r'):
        name = 'b_lip_corner_' + side
        if name in named:
            support = anatomy_influence(packet, bones, (name,))[representative]
            if np.max(support) > 0 and np.any((support > np.max(support) * 0.2) & (mask > 0)):
                corners[side] = anatomy_surface_anchor(vertices, vertex_normals, mask, origins[named[name]], support > np.max(support) * 0.2)
    lip_centers = {}
    for kind in ('upper', 'lower'):
        support = anatomy_influence(packet, bones, ('lip_' + kind,))[representative]
        owners = [index for index, name in enumerate(bones) if 'lip_' + kind in name]
        if owners and np.max(support) > 0 and np.any((support > np.max(support) * 0.2) & (mask > 0)):
            center = np.mean(origins[owners], axis=0)
            candidates = support > np.max(support) * 0.2
            lip_centers[kind] = anatomy_surface_anchor(vertices, vertex_normals, mask, center, candidates)
    if len(corners) == 2 and len(lip_centers) == 2:
        thickness = float(np.linalg.norm(vertices[lip_centers['upper']] - vertices[lip_centers['lower']]))
        if thickness > 0:
            for kind in ('upper', 'lower'):
                path = [corners['l'], lip_centers[kind], corners['r']]
                if len(set(path)) == 3:
                    sections.append(dict(id=kind + '-lip-roll-and-corners', anchors=path, radius=thickness))
    if not sections:
        return dict(status='ANATOMY_OWNERS_REQUIRE_SOURCE_SECTIONS', name=asset['name'], sourceFingerprint=skin['sourceFingerprint'], sourceContentKey=asset['sourceContentKey'], preservationReason='No verified native articulated facial region can define these particular sections. Its own head, muzzle and oral surfaces remain queued; no human section is substituted.')
    accumulated = np.zeros_like(vertices)
    denominator = np.zeros(len(vertices))
    diagnostics = []
    pending = list(pending_contacts)
    path_cache = {}
    landmark_groups = sorted({a for s in sections for a in s['anchors']} | {v for path in contacts.values() for v in path})
    landmark_points = vertices[landmark_groups]
    landmark_support = np.asarray([np.median([np.linalg.norm(vertices[a] - vertices[b]) for b in graph[a]]) * 2 for a in landmark_groups])
    landmark_distance = np.linalg.norm(vertices[:, None] - landmark_points[None], axis=2) / landmark_support[None]
    landmark_fade = functions['smooth'](np.minimum(1.0, landmark_distance.min(axis=1)))
    typical = np.median([np.linalg.norm(vertices[a] - vertices[b]) for a in range(len(vertices)) for b in graph[a] if a < b])

    def hold_native_patch(section):
        distance = np.linalg.norm(vertices[:, None] - vertices[section['anchors']][None], axis=2).min(axis=1)
        mask[:] *= functions['smooth']((distance - section['radius']) / (typical * 2))
    for section in sections:
        try:
            change, support, diagnostic = anatomy_curvature_field(vertices, vertex_normals, graph, section['anchors'], section['radius'], path_cache)
        except ValueError as error:
            pending.append(dict(id=section['id'], reason=str(error), sourceKnots=representative[section['anchors']].tolist()))
            hold_native_patch(section)
            continue
        single = change * (mask * landmark_fade)[:, None]
        failed, _ = anatomy_orientation_review(positions, positions + single[groups], triangles)
        if len(failed):
            pending.append(dict(id=section['id'], reason='This native fold cannot accept the proposed local surface without crossing its own source-facing triangles.', sourceFacingFailures=len(failed), sourceTriangles=triangles[failed].tolist()))
            hold_native_patch(section)
            continue
        accumulated += change
        denominator += support
        diagnostics.append(dict(id=section['id'], **diagnostic))
    displacement = accumulated / np.maximum(1.0, denominator[:, None])
    displacement *= mask[:, None]
    displacement *= landmark_fade[:, None]
    reconstructed = positions + displacement[groups]
    active = np.flatnonzero(np.linalg.norm(displacement[groups], axis=1) > 0)
    local_target = source_positions.copy()
    if len(active):
        local_target[active] = functions['points'](reconstructed[active].ravel().tolist(), np.linalg.inv(transform)).astype(np.float32)
    _, same_position = np.unique(source_positions, axis=0, return_inverse=True)
    minimum = np.full((same_position.max() + 1, 3), np.inf)
    maximum = np.full_like(minimum, -np.inf)
    np.minimum.at(minimum, same_position, local_target)
    np.maximum.at(maximum, same_position, local_target)
    gap = np.linalg.norm(maximum - minimum, axis=1)
    rounding = np.finfo(np.float32).eps * 8 * max(float(np.abs(source_positions).max()), float(np.linalg.norm(np.ptp(source_positions, axis=0))))
    split_seams = int(np.count_nonzero(gap > rounding))
    failed, minimum_ratio = anatomy_orientation_review(positions, reconstructed, triangles)
    facing_failures = len(failed)
    status = ('PARTIAL_SOURCE_TARGETS_ANATOMY_AUTHORING_PENDING' if pending else 'SOURCE_ANATOMY_SECTION_TARGETS_PREPARED_NOT_ART_ACCEPTED') if not facing_failures else 'SOURCE_SECTION_TARGETS_REJECTED_SURFACE_ORIENTATION'
    if split_seams:
        status = 'SOURCE_TARGETS_REJECTED_NEW_NATIVE_SEAM_GAP'
    return dict(status=status, name=asset['name'], sourceContentKey=asset['sourceContentKey'], sourceFingerprint=skin['sourceFingerprint'], rendererPath=skin['path'], sourceVertices=len(positions), sourceBoundaryVertices=int(boundary.sum()), targetPositionsPrefabDccMetres=reconstructed[active].tolist(), activeSourceVertices=active.tolist(), ownedFaceMaterialSlots=face_slots, largestCorrectionMetres=float(np.linalg.norm(displacement, axis=1).max()), sections=diagnostics, nativeProtectedPositionChannelsBitExact=bool(np.array_equal(local_target[np.linalg.norm(displacement[groups], axis=1) == 0].view(np.uint32), source_positions[np.linalg.norm(displacement[groups], axis=1) == 0].view(np.uint32))), sourceHeadOwner=head_root, changedVerticesOutsideNativeHead=int(np.count_nonzero(head_influence[active] <= 0)), newlySeparatedNativePositionSeams=split_seams, largestNativeSeamGapMetres=float(gap.max()), nativeSeamRoundingAllowanceMetres=rounding, localTargetPositionSha256=hashlib.sha256(local_target.tobytes()).hexdigest(), preparedRegions=sorted({section['id'] for section in diagnostics}), pendingRegions=pending, protectedSourceLandmarkVertices=representative[landmark_groups].tolist(), sourceConnectedFaceComponent=int(owned_component), nativeContactCurves={name: representative[path].tolist() for name, path in contacts.items()}, nativeContactVerticesBitExact=bool(np.all(displacement[landmark_groups] == 0)), sourceFacingFailures=facing_failures, sourceFacingFailureTriangles=triangles[failed].tolist(), minimumSourceProjectedAreaRatio=minimum_ratio, sourceNormalsConstrainCurvatureSurface=True, sourceLandmarkKnotsPreserved=True, globalMillimetreLimit=None, faceWidthScaleApplied=False, artAccepted=False)


def prepare_source_surface_guide(asset, skin: 'dict[str, Any]', packet: 'dict[str, Any]', target_positions, active_source_vertices, material_slots):
    """Encode an owned source surface with the existing guide recipe contract.

    The target is in prefab metres after rendererToPrefab and the DCC axis
    conversion. No face name, race, artist displacement or appearance policy is
    chosen here. Native zero controls surround the edited tissue, while native
    vertex/triangle correspondence owns the actual guide controls.
    """
    transform=art.CONVERT@art.matrix(skin['rendererToPrefab'])
    native=np.asarray(packet['vertices'],dtype=np.float32).reshape(-1,3)
    original=art.points(native.ravel().tolist(),transform)
    active=np.asarray(active_source_vertices,dtype=np.int64)
    target=np.asarray(target_positions,dtype=np.float64)
    require(active.ndim==1 and len(active)==len(np.unique(active)) and np.all((active>=0)&(active<len(native))) and target.shape==(len(active),3) and np.isfinite(target).all(),'Invalid owned source target coordinates.')
    local=native.copy()
    local[active]=art.points(target.ravel().tolist(),np.linalg.inv(transform)).astype(np.float32)
    mask=np.any(local!=native,axis=1).astype(float)
    require(np.any(mask),'The source target has no representable geometry change.')
    desired=art.points(local.ravel().tolist(),transform)
    slots=sorted(set(material_slots));triangles=[];anchors={};regions=[]
    for slot in slots:
        require(type(slot) is int and 0<=slot<min(len(packet['subMeshes']),len(skin['materials'])),'A source guide has no owned native material slot.')
        tri=np.asarray(packet['subMeshes'][slot],dtype=np.int64).reshape(-1,3)
        triangles.append(tri)
        values=np.zeros(len(native));values[np.unique(tri)]=mask[np.unique(tri)]
        if np.any(values):regions.append(dict(role='face-surface',materialSlot=slot,vertexWeights=values.tolist()))
        for ordinal,corners in enumerate(tri):
            for corner,vertex in enumerate(corners):
                if mask[vertex] and int(vertex) not in anchors:
                    bary=[0.,0.,0.];bary[corner]=1.
                    anchors[int(vertex)]=dict(type='sourceTriangle',subMesh=slot,triangleOrdinal=ordinal,barycentric=bary)
    require(regions and len(anchors)==int(mask.sum()),'Source target ownership crosses its declared native material slots.')
    triangles=np.concatenate(triangles)
    edges=np.unique(np.sort(np.concatenate((triangles[:,[0,1]],triangles[:,[1,2]],triangles[:,[2,0]])),axis=1),axis=0)
    lengths=np.linalg.norm(original[edges[:,0]]-original[edges[:,1]],axis=1)
    affected=np.any(mask[edges]>0,axis=1)&(lengths>0)
    require(np.any(affected),'The source target has no native tissue edge scale.')
    radius=float(np.median(lengths[affected])*2)
    low=original[mask>0].min(axis=0)-radius;high=original[mask>0].max(axis=0)+radius
    candidates=np.flatnonzero(np.all((original>=low)&(original<=high),axis=1))
    # Use the existing micrometre native weld domain for numerical centres.
    # All controls still reference their own original source triangle exactly.
    groups=art.weld_groups(original[candidates])
    _,representative=np.unique(groups,return_index=True)
    center_indices=candidates[representative]
    centers=original[center_indices];radii=np.full(len(centers),radius)
    kernel=art.guide_kernel(centers,centers,radii)
    coefficients=np.linalg.solve(kernel,desired[center_indices]-original[center_indices])
    require(np.isfinite(coefficients).all(),'A source target produced a nonfinite guide field.')
    moved=art.guide_kernel(original,centers,radii)@coefficients
    residual=np.linalg.norm((moved-(desired-original))[mask>0],axis=1)
    require(float(residual.max())<=.00001,'The compiled native surface misses its owned target by more than 0.01 mm.')
    controls=[dict(id='Vertex'+str(vertex),anchor=anchors[vertex],target=local[vertex].astype(float).tolist(),radiusMetres=radius) for vertex in sorted(anchors)]
    profile=dict(id=resource_naming.source_label(asset['name'])+'_'+resource_naming.source_label(skin['path'])+'_Guide',
        source=dict(assetSourceContentKey=asset['sourceContentKey'],rendererPath=skin['path'],nativeFingerprint=packet['sourceFingerprint'],vertexCount=packet['vertexCount'],bonePathsOrdered=skin['bones'],rendererToPrefab=skin['rendererToPrefab']),
        regions=regions,points={},operation='source-guide',donorSourceFingerprint=None,
        parameters=dict(controls=controls,compiledField=dict(sourceFingerprint=packet['sourceFingerprint'],coordinateDomain='prefab-metres-after-native-rendererToPrefab-and-DCC-conversion',centers=centers.tolist(),radii=radii.tolist(),coefficients=coefficients.tolist())))
    return profile,dict(sourceVertices=len(native),editedVertices=int(mask.sum()),nativeGuideCenters=len(centers),nativeEdgeSupportRadiusMetres=radius,maximumTargetResidualMetres=float(residual.max()))


def prepare_source_triangle_anchors(packet: 'dict[str, Any]', skin: 'dict[str, Any]', donor_packet, donor_skin, donor_slots=None):
    """Closest actual donor triangles for the existing surface-follow codec."""
    transform=art.CONVERT@art.matrix(skin['rendererToPrefab'])
    donor_transform=art.CONVERT@art.matrix(donor_skin['rendererToPrefab'])
    positions=art.points(packet['vertices'],transform)
    donor=art.points(donor_packet['vertices'],donor_transform)
    entries=[];vertices=[]
    slots=range(len(donor_packet['subMeshes'])) if donor_slots is None else donor_slots
    for slot in slots:
        for ordinal,indices in enumerate(np.asarray(donor_packet['subMeshes'][slot],dtype=np.int64).reshape(-1,3)):
            a,b,c=donor[indices]
            if np.linalg.norm(np.cross(b-a,c-a))>0:entries.append((slot,ordinal));vertices.append(indices)
    require(entries,'An attached surface has no nondegenerate native donor triangle.')
    triangles=np.asarray(vertices,dtype=np.int64)
    a,b,c=(donor[triangles[:,i]] for i in range(3))
    ab=b-a;ac=c-a
    aa=np.sum(ab*ab,axis=1);ad=np.sum(ab*ac,axis=1);dd=np.sum(ac*ac,axis=1)
    determinant=aa*dd-ad*ad
    anchors=[];largest_distance=0.
    for point in positions:
        ap=point-a;apb=np.sum(ap*ab,axis=1);apc=np.sum(ap*ac,axis=1)
        v=np.divide(dd*apb-ad*apc,determinant,out=np.zeros(len(a)),where=determinant>0)
        w=np.divide(aa*apc-ad*apb,determinant,out=np.zeros(len(a)),where=determinant>0)
        bary=np.column_stack((1-v-w,v,w))
        closest=a+v[:,None]*ab+w[:,None]*ac
        distance=np.sum((closest-point)**2,axis=1)
        distance[~((determinant>0)&np.all(bary>=0,axis=1))]=np.inf
        for first,second,columns in [(a,b,(0,1)),(b,c,(1,2)),(c,a,(2,0))]:
            edge=second-first;length=np.sum(edge*edge,axis=1)
            part=np.clip(np.divide(np.sum((point-first)*edge,axis=1),length,out=np.zeros(len(a)),where=length>0),0,1)
            edge_point=first+part[:,None]*edge
            value=np.sum((edge_point-point)**2,axis=1);better=value<distance
            edge_bary=np.zeros_like(bary);edge_bary[:,columns[0]]=1-part;edge_bary[:,columns[1]]=part
            bary[better]=edge_bary[better];distance[better]=value[better]
        selected=int(np.argmin(distance));slot,ordinal=entries[selected]
        require(np.isfinite(distance[selected]),'An attached source has no finite native donor projection.')
        weights=np.maximum(0,bary[selected]);weights/=weights.sum()
        anchors.append(dict(type='sourceTriangle',subMesh=slot,triangleOrdinal=ordinal,barycentric=weights.tolist()))
        largest_distance=max(largest_distance,float(np.sqrt(distance[selected])))
    return anchors,dict(sourceVertices=len(positions),nativeDonorTriangles=len(triangles),largestNativeAttachmentOffsetMetres=largest_distance)


def recover_source_lod_frames(packet, skin, donor_packet, donor_skin):
    """Recover impossible native LOD morphs from their same-source LOD0."""
    diagonal = float(np.linalg.norm(np.ptp(np.asarray(packet['vertices']).reshape(-1, 3), axis=0)))
    invalid = []
    for si, shape in enumerate(packet['shapes']):
        for fi, frame in enumerate(shape['frames']):
            delta = np.asarray(frame['vertices']).reshape(-1, 3)
            if not np.isfinite(delta).all() or float(np.linalg.norm(delta, axis=1).max()) > diagonal:
                invalid.append((si, fi))
    if not invalid: return packet, []
    require(lod_index(skin) > 0 and lod_index(donor_skin) == 0,
            'An impossible native physical frame requires its own valid LOD0 source.')
    anchors, projection = prepare_source_triangle_anchors(packet, skin, donor_packet, donor_skin)
    triangles = np.asarray([donor_packet['subMeshes'][a['subMesh']][a['triangleOrdinal']*3:a['triangleOrdinal']*3+3]
                            for a in anchors], dtype=np.int64)
    bary = np.asarray([a['barycentric'] for a in anchors])
    transform = art.CONVERT @ art.matrix(skin['rendererToPrefab'])
    source_transform = art.CONVERT @ art.matrix(donor_skin['rendererToPrefab'])
    own_matrix = transform[:3, :3]; source_matrix = source_transform[:3, :3]
    source_normal = np.asarray(donor_packet['normals']).reshape(-1, 3)
    own_normal = np.asarray(packet['normals']).reshape(-1, 3)
    own_tangent = np.asarray(packet['tangents']).reshape(-1, 4)[:, :3]
    def interpolate(values): return np.einsum('vi,vij->vj', bary, values[triangles])
    rest_n = normalise(interpolate(normalise(source_normal @ np.linalg.inv(source_matrix))))
    own_n = normalise(own_normal @ np.linalg.inv(own_matrix))
    own_t = normalise(own_tangent @ own_matrix.T)
    result = copy.deepcopy(packet); reports = []
    for si, fi in invalid:
        channel = packet['shapes'][si]; frame = channel['frames'][fi]
        matching = [s for s in donor_packet['shapes'] if s['name'] == channel['name']]
        require(len(matching) == 1, 'A broken native LOD frame has no unambiguous source channel.')
        frames = [f for f in matching[0]['frames'] if f['weight'] == frame['weight']]
        require(len(frames) == 1, 'A broken native LOD frame has no matching source weight.')
        source = frames[0]; delta = np.asarray(source['vertices']).reshape(-1, 3)
        source_diagonal = float(np.linalg.norm(np.ptp(np.asarray(donor_packet['vertices']).reshape(-1, 3), axis=0)))
        require(np.isfinite(delta).all() and float(np.linalg.norm(delta, axis=1).max()) <= source_diagonal,
                'The native LOD0 physical source is also outside its mesh envelope.')
        target = result['shapes'][si]['frames'][fi]
        target['vertices'] = (interpolate(delta @ source_matrix.T) @ np.linalg.inv(own_matrix).T).astype(np.float32).ravel().tolist()
        posed_n = normalise(interpolate(normalise((source_normal + np.asarray(source['normals']).reshape(-1, 3)) @ np.linalg.inv(source_matrix))))
        new_n = normalise(rotate_between(own_n, rest_n, posed_n) @ own_matrix)
        new_t = rotate_between(own_t, rest_n, posed_n) @ np.linalg.inv(own_matrix).T
        new_t = normalise(new_t - np.sum(new_t * new_n, axis=1)[:, None] * new_n)
        target['normals'] = (new_n - own_normal).astype(np.float32).ravel().tolist()
        target['tangents'] = (new_t - own_tangent).astype(np.float32).ravel().tolist()
        reports.append(dict(channelIndex=si, name=channel['name'], weight=frame['weight'],
            nativeSourceFingerprint=packet['sourceFingerprint'], donorSourceFingerprint=donor_packet['sourceFingerprint'],
            method='same-source LOD0 triangle barycentric displacement and normal-frame rotation', **projection))
    return result, reports


def load_material_channels(path):
    document = art.read_json(path)
    if not (type(document) is dict and set(document) == {'schemaVersion', 'contracts'} and document['schemaVersion'] == 1 and type(document['contracts']) is list): raise ValueError('Invalid material channel document.')
    result = {}
    contracts=document['contracts']
    if not isinstance(contracts,list): raise ValueError('Material contracts must be a list.')
    for contract in contracts:
        if not (type(contract) is dict and set(contract) == {'sourceShader', 'requiredProperties', 'color', 'normal', 'alpha', 'normalEncoding', 'evidence'}): raise ValueError('Invalid material channel contract fields.')
        shader = contract['sourceShader']
        if not (type(shader) is str and shader and shader not in result): raise ValueError('Duplicate or missing source shader contract.')
        if not (contract['color'] in (None, '_MainTex') and contract['normal'] in (None, '_BumpMap') and (contract['color'] or contract['normal']) and contract['alpha'] in ('preserve', 'smoothness') and contract['normalEncoding'] == 'packed-ag'): raise ValueError('Unsupported source channel operation.')
        if not (type(contract['requiredProperties']) is dict and contract['requiredProperties'] and all(type(k) is str and type(v) is str for k,v in contract['requiredProperties'].items())): raise ValueError('Source material property schema is required.')
        for property_name in (contract['color'], contract['normal']):
            if not (property_name is None or contract['requiredProperties'].get(property_name) == 'Texture'): raise ValueError('Authored channel is not covered by the source property contract.')
        evidence = contract['evidence']
        if not (type(evidence) is dict and set(evidence) == {'path', 'sha256'}): raise ValueError('Source shader channel evidence is required.')
        source = Path(evidence['path'])
        if not (source.is_file() and art.hash_file(source) == evidence['sha256']): raise ValueError('Source shader evidence changed or is absent.')
        result[shader] = contract
    return result


def material_channels(material):
    # Geometry ownership is independent from these source shader channel contracts.
    shader = material['shader']
    if shader in MATERIAL_CHANNEL_CONTRACTS:
        contract = MATERIAL_CHANNEL_CONTRACTS[shader]
        properties = {p['name']: p['type'] for p in material['properties']}
        require(all(properties.get(k) == v for k,v in contract['requiredProperties'].items()), 'Material source property contract differs.')
        return dict(status='supported', color=contract['color'], normal=contract['normal'], normalEncoding=contract['normalEncoding'], alpha=contract['alpha'], evidence=contract['evidence'])
    if '/Skin' in shader:
        return dict(status='supported', color='_MainTex', normal='_BumpMap', normalEncoding='packed-ag', alpha='smoothness')
    if shader in ('Standard', 'Standard (Double Sided)'):
        return dict(status='supported', color='_MainTex', normal='_BumpMap', normalEncoding='packed-ag', alpha='preserve')
    return dict(status='pending-source-channel-contract', color=None, normal=None, alpha='preserve', shader=shader)


def bind_source_lod_families(asset):
    import re
    def family(skin: 'dict[str, Any]'):
        return re.sub(r'(?i)lod\d+(?=$|[/_])', 'LOD', skin['path'])
    sources = {}
    for skin in asset['skins']:
        if lod_index(skin) == 0 and role(skin) == 'skin':
            packet = native_packet(skin)
            _, _, mask, _ = sculpt_surface(packet, skin, 'skin')
            if np.any(mask):
                key = family(skin)
                require(key not in sources, 'Ambiguous native LOD0 face family.')
                sources[key] = (skin, packet, facial_material_slots(packet, skin))
    for skin in asset['skins']:
        if lod_index(skin) == 0 or role(skin) != 'native' or family(skin) not in sources:
            continue
        source, packet, slots = sources[family(skin)]
        matched = [i for i in slots if i < len(skin['materials']) and source['materials'][i] is not None and skin['materials'][i] is not None and material_identity(source['materials'][i]) == material_identity(skin['materials'][i])]
        if matched:
            skin['_faceLodDonor'] = packet['sourceFingerprint']
            skin['_faceOwnedSlots'] = matched
    return asset


def face_work_status(reports):
    changed = [m for m in reports if m['role'] == 'skin'
               and m.get('faceOperation', {}).get('complete', False)]
    if not changed:
        return 'pending-source-anatomy-profile'
    if any(c['status'] != 'supported' for m in changed for c in m.get('materialChannelWork', [])):
        return 'pending-source-material-channel-contract'
    return 'authored-awaiting-visual-review'


def face_operation(original, requested, native_vertices, authored_vertices, zero_roundtrip, mask, native_fp, authored_fp):
    """Count the actual selected field, excluding coordinate-conversion residue."""
    original = np.asarray(original, dtype=np.float64)
    requested = np.asarray(requested, dtype=np.float64)
    native = np.asarray(native_vertices, dtype=np.float32).reshape(-1, 3)
    actual = np.asarray(authored_vertices, dtype=np.float32).reshape(-1, 3)
    control = np.asarray(zero_roundtrip, dtype=np.float32).reshape(-1, 3)
    ownership = np.asarray(mask, dtype=np.float64)
    require(original.shape == requested.shape == native.shape == actual.shape == control.shape
            and ownership.shape == (len(original),), 'Face-operation channel lengths differ.')
    finite = all(np.all(np.isfinite(value)) for value in (original, requested, native, actual, control, ownership))
    require(finite, 'Nonfinite face-operation field.')
    # Float64 subtraction of two already evaluated positions. This is an
    # arithmetic bound, not a fixed millimetre or float32 displacement limit.
    bound = 8 * np.maximum(np.abs(np.spacing(original)), np.abs(np.spacing(requested)))
    active = ownership > 0
    intended = active & np.any(np.abs(requested - original) > bound, axis=1)
    emitted = active & np.any(actual != native, axis=1) & np.any(actual != control, axis=1)
    effective = bool(np.any(active) and authored_fp != native_fp)
    operated = intended & emitted
    return dict(complete=bool(effective and np.any(operated)), finite=True,
                operatingVertices=int(np.count_nonzero(active)), intendedFieldVertices=int(np.count_nonzero(intended)),
                changedFloat32OutputVertices=int(np.count_nonzero(emitted)), operatedVertices=int(np.count_nonzero(operated)),
                effectiveOutputMode='authored' if effective else 'native',
                maximumRequestedDisplacementMm=float(np.max(np.linalg.norm(requested-original, axis=1))*1000),
                maximumArithmeticBoundMm=float(np.max(np.linalg.norm(bound, axis=1))*1000),
                arithmetic='8 binary64 ULP per source/requested component; final binary32 output differs from native and zero-field roundtrip')


def save_json(path, data):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(data, separators=(',', ':'), ensure_ascii=False, allow_nan=False), encoding='utf-8')


def normalise(values):
    return values / np.maximum(np.linalg.norm(values, axis=-1, keepdims=True), 1e-12)


def physical_mesh_bounds(packet: 'dict[str, Any]'):
    base=np.asarray(packet['vertices'],dtype=np.float64).reshape(-1,3)
    low=base.min(axis=0);high=base.max(axis=0)
    for shape in packet['shapes']:
        for frame in shape['frames']:
            positions=base+np.asarray(frame['vertices'],dtype=np.float64).reshape(-1,3)
            low=np.minimum(low,positions.min(axis=0));high=np.maximum(high,positions.max(axis=0))
    center=((low+high)*.5).astype(np.float32)
    extent=np.maximum(center.astype(float)-low,high-center.astype(float)).astype(np.float32)
    extent=np.nextafter(extent,np.float32(np.inf))
    return np.r_[center,extent*np.float32(2)].tolist()


def rotate_between(values, before, after):
    axis = np.cross(before, after)
    cosine = np.sum(before * after, axis=1)
    if np.any(cosine < -.95):
        raise ValueError('An authored surface reversed its source normal.')
    first = np.cross(axis, values)
    return values + first + np.cross(axis, first) / np.maximum(1 + cosine[:, None], 1e-8)


def normal_groups(positions, native_normals):
    groups = art.weld_groups(positions)
    result = np.empty(len(positions), dtype=np.int64)
    cohorts = {}
    count = 0
    for index, group in enumerate(groups):
        matches = cohorts.setdefault(int(group), [])
        found = next((item for item in matches if np.dot(native_normals[index], item[0]) > 1 - 1e-6), None)
        if found is None:
            found = (native_normals[index], count)
            matches.append(found)
            count += 1
        result[index] = found[1]
    return result






def cohere_skin_frame_normals(packet):
    """Keep an authored smooth skin surface smooth at every physical frame."""
    positions=np.asarray(packet['vertices'],float).reshape(-1,3);base_normals=np.asarray(packet['normals'],float).reshape(-1,3)
    base_tangents=np.asarray(packet['tangents'],float).reshape(-1,4)
    groups=normal_groups(positions,normalise(base_normals));weights=np.asarray(packet['weights']).reshape(-1,4);bones=np.asarray(packet['boneIndices']).reshape(-1,4)
    frames=[frame for shape in packet['shapes']for frame in shape['frames']]
    offsets=[np.asarray(frame['vertices']).reshape(-1,3)for frame in frames]
    cohorts={};physical=[]
    for index,group in enumerate(groups):
        skin=tuple(sorted((int(bone),float(weight))for bone,weight in zip(bones[index],weights[index])if weight>0))
        candidates=cohorts.setdefault((int(group),skin),[])
        matched=next((entry for entry in candidates if all(np.linalg.norm(value[index]-value[entry[0]])<=1e-6 for value in offsets)),None)
        if matched is None:matched=[index,len(physical)];candidates.append(matched);physical.append([])
        physical[matched[1]].append(index)
    aliases=[np.asarray(ids,int)for ids in physical if len(ids)>1]
    triangles=np.concatenate([np.asarray(slot,int).reshape(-1,3)for slot in packet['subMeshes']])
    labels=np.empty(len(positions),int)
    for index,ids in enumerate(physical):labels[ids]=index
    rest_surface=surface_normals(positions,triangles,labels,positions,base_normals)
    result=copy.deepcopy(packet);changed=0;maximum_before=0.;changed_frames=[]
    for shape,source_shape in zip(result['shapes'],packet['shapes']):
        for frame,source_frame in zip(shape['frames'],source_shape['frames']):
            pose=positions+np.asarray(source_frame['vertices']).reshape(-1,3)
            normal=normalise(base_normals+np.asarray(source_frame['normals']).reshape(-1,3))
            tangent=normalise(base_tangents[:,:3]+np.asarray(source_frame['tangents']).reshape(-1,3))
            area=np.linalg.norm(np.cross(pose[triangles[:,1]]-pose[triangles[:,0]],pose[triangles[:,2]]-pose[triangles[:,0]]),axis=1)
            mass=np.zeros(len(positions))
            for corner in range(3):np.add.at(mass,triangles[:,corner],area)
            destination=normal.copy();selected=[]
            posed_surface=surface_normals(pose,triangles,labels,positions,base_normals)
            for ids in aliases:
                used=ids[mass[ids]>0]
                if len(used)<2:continue
                cosine=float(np.clip(np.min(normal[used]@normal[used].T),-1,1))
                if cosine>1-1e-6:continue
                base=normalise(np.sum(base_normals[used]*mass[used,None],axis=0))[None,:]
                target=normalise(rotate_between(base,rest_surface[used[:1]],posed_surface[used[:1]]))[0]
                destination[ids]=target;selected.extend(ids);maximum_before=max(maximum_before,float(np.degrees(np.arccos(cosine))))
            if not selected:continue
            selected=np.asarray(selected,int);safe=np.sum(normal[selected]*destination[selected],axis=1)>-.95
            rotated=np.empty((len(selected),3))
            if safe.any():rotated[safe]=rotate_between(tangent[selected[safe]],normal[selected[safe]],destination[selected[safe]])
            if (~safe).any():rotated[~safe]=rotate_between(base_tangents[selected[~safe],:3],rest_surface[selected[~safe]],posed_surface[selected[~safe]])
            rotated=normalise(rotated-destination[selected]*np.sum(rotated*destination[selected],axis=1)[:,None])
            normals=np.asarray(source_frame['normals']).reshape(-1,3).copy();tangents=np.asarray(source_frame['tangents']).reshape(-1,3).copy()
            normals[selected]=(destination[selected]-base_normals[selected]).astype(np.float32);tangents[selected]=(rotated-base_tangents[selected,:3]).astype(np.float32)
            frame['normals']=normals.ravel().tolist();frame['tangents']=tangents.ravel().tolist()
            changed+=len(selected);changed_frames.append(dict(shape=shape['name'],weight=frame['weight'],vertices=len(selected)))
    return result,dict(changedFrameVertices=changed,frames=changed_frames,maximumAliasAngleBeforeDegrees=maximum_before,
        restChannelsExact=True,positionFramesExact=True,uvSkinTopologyAndChannelsExact=True,physicalAliasPoseNormalsCoherent=True)


def surface_normals(positions, triangles, groups, source_positions=None, native_normals=None):
    areas = np.cross(positions[triangles[:, 1]] - positions[triangles[:, 0]], positions[triangles[:, 2]] - positions[triangles[:, 0]])
    accumulated = np.zeros_like(positions)
    source_areas = areas if source_positions is None else np.cross(
        source_positions[triangles[:, 1]] - source_positions[triangles[:, 0]],
        source_positions[triangles[:, 2]] - source_positions[triangles[:, 0]])
    for corner in range(3):
        contribution = areas
        if native_normals is not None:
            ownership = np.sum(source_areas * native_normals[triangles[:, corner]], axis=1) > 0
            contribution = areas * ownership[:, None]
        np.add.at(accumulated, triangles[:, corner], contribution)
    welded = np.zeros((int(groups.max()) + 1, 3)); np.add.at(welded, groups, accumulated)
    normals = welded[groups]
    if native_normals is not None:
        unsupported = np.linalg.norm(normals, axis=1) < 1e-15
        normals[unsupported] = native_normals[unsupported]
    return normalise(normals)


def preserve_surface_orientation(reference, candidate, triangles, transform):
    source = art.points(reference.ravel().tolist(), transform)
    source_edges = np.stack((source[triangles[:, 1]] - source[triangles[:, 0]],
                             source[triangles[:, 2]] - source[triangles[:, 0]]), axis=1)
    source_area = np.cross(source_edges[:, 0], source_edges[:, 1])
    squared = np.sum(source_area * source_area, axis=1)
    edge_length = np.max(np.linalg.norm(source_edges, axis=2), axis=1)
    coordinate = np.max(np.abs(source[triangles]), axis=(1, 2))
    representable_area = np.finfo(np.float32).eps * 16 * (coordinate * edge_length + edge_length ** 2)
    supported = np.sqrt(squared) > representable_area
    initial_bad = None
    for attempt, factor in enumerate([2.0 ** -i for i in range(9)] + [0.]):
        local = (reference + (candidate - reference) * factor).astype(np.float32)
        points = art.points(local.ravel().tolist(), transform)
        area = np.cross(points[triangles[:, 1]] - points[triangles[:, 0]],
                        points[triangles[:, 2]] - points[triangles[:, 0]])
        projection = np.sum(source_area * area, axis=1)
        bad = supported & (projection < squared * .05)
        if initial_bad is None:
            initial_bad = int(np.count_nonzero(bad))
        if not np.any(bad):
            return local, dict(surfaceScaleFactor=factor, surfaceBacktrackingAttempts=attempt,
                initialFacingFailures=initial_bad, supportedFacingTriangles=int(supported.sum()),
                floatLimitedTriangles=int((~supported).sum()), minimumSourceProjectedAreaRatio=.05)
    raise ValueError('The native reference surface cannot retain its source-facing triangles.')


def interpolate_source_handles(packet, skin, pose, positions, controls, reports, ownership, transform, units=()):
    """Positive surface interpolation keeps handle edits local and bounded."""
    rest = art.points(packet['vertices'], transform)
    groups = art.weld_groups(rest)
    count = int(groups.max()) + 1
    triangles = np.concatenate([np.asarray(s,dtype=int).reshape(-1,3) for s in packet['subMeshes']])
    displacement = np.zeros((count, 3))
    allowed = ownership > 0
    inside = np.zeros(len(rest), dtype=bool)
    for origin, _, radius in controls:
        inside |= np.linalg.norm(positions-origin, axis=1) < radius
    unit_rows = []
    indices = np.asarray(packet['boneIndices'], dtype=int).reshape(len(rest), -1)
    weights = np.asarray(packet['weights']).reshape(len(rest), -1)
    for unit in units:
        prefixes = tuple(unit['bonePrefixes'])
        require(prefixes and all(isinstance(value, str) and value for value in prefixes),
                'A contact unit requires explicit source bone prefixes.')
        bones = np.array([bool(name) and name.rsplit('/', 1)[-1].lower().startswith(prefixes)
                          for name in skin['bones']])
        selected = np.any(bones[indices] & (weights > 0), axis=1) & allowed
        require(np.any(selected), 'The declared contact unit has no owned source tissue.')
        handle = next((i for i, report in enumerate(reports) if report['name'] == unit['handle']), None)
        require(handle is not None, 'A contact unit has no matching authored handle.')
        offset = controls[handle][1] - controls[handle][0]
        unit_rows.append((selected, offset, handle))
        inside |= selected
    allowed &= inside
    group_allowed = np.ones(count, dtype=bool)
    np.logical_and.at(group_allowed, groups, allowed)
    fixed = ~group_allowed
    assigned_distance = np.full(count, np.inf)
    for (origin, target, _), report in zip(controls, reports):
        index = report['sourceVertex']
        if not group_allowed[groups[index]]:
            continue
        endpoint = origin + (target-origin)*ownership[index]
        target_rest = (reference_pose_surface_to_rest(packet, skin, pose, [index], [endpoint])[0]
                       if pose is not None else endpoint)
        delta = target_rest-rest[index]
        distance=np.linalg.norm(rest-rest[index],axis=1)
        # A hard handle owns its immediate source tissue, including coarse and
        # thin triangles. A fixed metric patch alone can pinch a single corner
        # when the source edge is longer than the patch radius.
        ring=np.unique(triangles[np.any(groups[triangles]==groups[index],axis=1)])
        patch=np.union1d(np.flatnonzero(distance<=.003),ring)
        patch=patch[group_allowed[groups[patch]]]
        for vertex in patch:
            group=int(groups[vertex])
            if distance[vertex]<assigned_distance[group]:
                assigned_distance[group]=distance[vertex]
                fixed[group]=True
                displacement[group]=delta
        report['sourcePatchVertices']=len(patch)
    # Closed lip rims and other declared articulated units share one neutral
    # displacement. Independent nearest handles can expose an internal triangle
    # even when every individual triangle retains a positive signed area.
    for selected, offset, handle in unit_rows:
        ids = np.flatnonzero(selected)
        targets = positions[ids] + offset
        targets = reference_pose_surface_to_rest(packet, skin, pose, ids, targets) if pose is not None else targets
        values = targets - rest[ids]
        for group in np.unique(groups[ids]):
            local = values[groups[ids] == group]
            require(float(np.linalg.norm(local-local.mean(axis=0), axis=1).max()) <= 1e-6,
                    'A contact unit has conflicting coincident source skin transforms.')
            fixed[group] = True
            displacement[group] = local.mean(axis=0)
        reports[handle]['contactUnitVertices'] = len(ids)
        reports[handle]['contactUnitOffsetMm'] = (offset*1000).tolist()
    edges = np.unique(np.sort(groups[np.concatenate((triangles[:,[0,1]], triangles[:,[1,2]], triangles[:,[2,0]]))],axis=1),axis=0)
    edges = edges[edges[:,0]!=edges[:,1]]
    _, representatives = np.unique(groups,return_index=True)
    lengths = np.linalg.norm(rest[representatives[edges[:,0]]]-rest[representatives[edges[:,1]]],axis=1)
    weights = 1/np.maximum(lengths,1e-6)
    laplace = np.zeros((count,count))
    for (a,b), weight in zip(edges,weights):
        laplace[a,a] += weight; laplace[b,b] += weight
        laplace[a,b] -= weight; laplace[b,a] -= weight
    free = np.flatnonzero(~fixed)
    anchored = np.flatnonzero(fixed)
    if len(free):
        displacement[free] = np.linalg.solve(laplace[np.ix_(free,free)], -laplace[np.ix_(free,anchored)]@displacement[anchored])
    if not np.isfinite(displacement).all():
        raise ValueError('The source surface edit is not finite.')
    # Every free sample is a positive harmonic combination of explicit handles.
    if np.linalg.norm(displacement,axis=1).max() > np.linalg.norm(displacement[anchored],axis=1).max()+1e-10:
        raise ValueError('The source handle interpolation overshot its owned targets.')
    return rest+displacement[groups]



def retain_local_surface_areas(packet, reference, target, ownership):
    """Keep thin sculpted tissue open with bounded local corrections."""
    triangles = np.concatenate([np.asarray(s, dtype=np.int64).reshape(-1, 3) for s in packet['subMeshes']])
    groups = art.weld_groups(reference)
    group_owned = np.ones(int(groups.max()) + 1, dtype=bool)
    np.logical_and.at(group_owned, groups, np.asarray(ownership) > 0)
    source = reference[triangles]
    edges = np.stack((source[:, 1] - source[:, 0], source[:, 2] - source[:, 0]), axis=1)
    area = np.cross(edges[:, 0], edges[:, 1]); square = np.sum(area * area, axis=1)
    length = np.max(np.linalg.norm(edges, axis=2), axis=1)
    supported = np.sqrt(square) > np.finfo(np.float32).eps * 16 * (np.max(np.abs(source), axis=(1, 2)) * length + length * length)
    result = target.copy(); initial = 0
    for iteration in range(16):
        current = result[triangles]
        updated = np.cross(current[:, 1] - current[:, 0], current[:, 2] - current[:, 0])
        ratios = np.divide(np.sum(area * updated, axis=1), square, out=np.ones(len(square)), where=square > 0)
        bad = np.flatnonzero(supported & (ratios < .075))
        if iteration == 0: initial = len(bad)
        if not len(bad):
            return result, dict(repairedTriangles=initial, iterations=iteration,
                maximumCorrectionMm=float(np.linalg.norm(result-target, axis=1).max()) * 1000,
                minimumSourceProjectedAreaRatio=.075)
        for index in bad:
            a, b, c = result[triangles[index]]; n = area[index] / square[index]
            gb = np.cross(c-a, n); gc = np.cross(n, b-a)
            grouped = {}
            for group, gradient in zip(groups[triangles[index]], (-gb-gc, gb, gc)):
                if group_owned[group]: grouped[group] = grouped.get(group, np.zeros(3)) + gradient
            denominator = sum(float(g @ g) for g in grouped.values())
            require(denominator > 1e-20, 'Compressed surface tissue has no editable source vertex.')
            correction = (.10 - float(np.dot(area[index], np.cross(b-a, c-a)) / square[index])) / denominator
            for group, gradient in grouped.items(): result[groups == group] += gradient * correction
        require(float(np.linalg.norm(result-target, axis=1).max()) <= .00025,
                'Surface tissue requires more than a 0.25 mm local correction.')
    raise ValueError('Surface tissue has unresolved local compression.')


def surface_basis(reference, positions, source_normals, source_tangents, triangles, transform, mask, skin: bool):
    before = art.points(reference.ravel().tolist(), transform)
    after = art.points(positions.ravel().tolist(), transform)
    native = normalise(source_normals @ np.linalg.inv(transform[:3, :3]))
    area = np.cross(before[triangles[:, 1]] - before[triangles[:, 0]], before[triangles[:, 2]] - before[triangles[:, 0]])
    oriented = triangles[:, ::-1] if np.median(np.sum(area * native[triangles].mean(axis=1), axis=1)) < 0 else triangles
    groups = normal_groups(before, native)
    initial = surface_normals(before, oriented, groups, before, native)
    final = surface_normals(after, oriented, groups, before, native)
    normals = normalise(rotate_between(native, initial, final))
    if skin:
        compatible = art.smooth((np.sum(normals * final, axis=1) - .5) / .25)
        normals = normalise(normals + (final - normals) * (mask * compatible * .4)[:, None])
    local_normals = normalise(normals @ transform[:3, :3])
    tangents = source_tangents.copy()
    if len(tangents):
        world = normalise(tangents[:, :3] @ transform[:3, :3].T)
        world = rotate_between(world, initial, final)
        local = world @ np.linalg.inv(transform[:3, :3]).T
        local -= np.sum(local * local_normals, axis=1)[:, None] * local_normals
        tangents[:, :3] = normalise(local)
    return local_normals, tangents


def native_packet(skin: 'dict[str, Any]') -> 'dict[str, Any]':
    with gzip.open(art.source_path(skin['meshFile']), 'rt', encoding='utf-8') as stream:
        packet = json.load(stream)
    if not packet.get('nativeReadable', False) and packet.get('sourceReadMode') != 'serialized-mesh-data':
        raise ValueError('Facial authoring requires original serialized mesh data; a skinned read proxy is not a native rest source: ' + skin['meshFile'])
    if fingerprint(packet)[0] != packet['sourceFingerprint']:
        raise ValueError('Native packet fingerprint mismatch: ' + skin['meshFile'])
    return packet


def role(skin: 'dict[str, Any]'):
    if skin.get('_faceProfile',{}).get('operation')=='source-guide-follow':return 'attached'
    shaders=[m['shader']for m in skin['materials']if m]
    contact_name=(skin['mesh']+'/'+skin['path']).lower()
    if not any('/Skin'in shader for shader in shaders)and ('Custom/CharacterEye'in shaders or any(token in contact_name for token in ('eyelash','eyeocclud','jaw','teeth','tongue'))):
        return 'native'
    if skin.get('_faceProfile') is not None or skin.get('_faceLodDonor') is not None:
        return 'skin'
    if skin.get('_guideCompanionPreserved'):
        return 'native'
    facial_bones = [b.rsplit('/', 1)[-1].lower() for b in skin['bones'] if b]
    if any('/Skin' in s for s in shaders) or (any(b == 'bip001 head' for b in facial_bones) and
            any('eyelid' in b for b in facial_bones) and any('lip_' in b for b in facial_bones)):
        return 'skin'
    return 'native'


def lod_index(skin: 'dict[str, Any]'):
    import re
    match = re.search(r'(?:^|[/_])lod(\d+)$', skin['path'], re.IGNORECASE)
    return int(match[1]) if match else 0


def asset_key(asset):
    return resource_naming.source_label(asset['internalId'])



def facial_material_slots(packet: 'dict[str, Any]', skin: 'dict[str, Any]'):
    if '_faceOwnedSlots' in skin:
        return [i for i in skin['_faceOwnedSlots'] if i < min(len(packet['subMeshes']), len(skin['materials']))]
    bones = [b.rsplit('/', 1)[-1].lower() if b else '' for b in skin['bones']]
    face_bones = np.array([any(token in b for token in ('eyelid', 'lip_', 'nostril', 'cheek', 'eyebrow')) for b in bones])
    weights = np.asarray(packet['weights']).reshape(-1, 4)
    indices = np.asarray(packet['boneIndices']).reshape(-1, 4)
    facial_influence = np.sum(weights * face_bones[indices], axis=1)
    slots = []
    for slot, material in enumerate(skin['materials']):
        if not material or slot >= len(packet['subMeshes']):
            continue
        vertices = np.asarray(packet['subMeshes'][slot], dtype=np.int64)
        if not len(vertices):
            continue
        profile = skin.get('_faceProfile')
        owned = np.asarray(profile['mask'])[vertices] > 0 if profile is not None else facial_influence[vertices] > .001
        if '/Skin' in material['shader'] or np.any(owned):
            slots.append(slot)
    return slots


def sculpt_surface(packet: 'dict[str, Any]', skin: 'dict[str, Any]', which):
    surface, transform, mask, report = art.sculpt(packet, skin, which, skin.get('_faceProfile'))
    if which == 'skin':
        original = art.points(packet['vertices'], transform)
        selected = np.zeros(packet['vertexCount'], dtype=bool)
        slots = facial_material_slots(packet, skin)
        for slot in slots:
            selected[np.asarray(packet['subMeshes'][slot], dtype=np.int64)] = True
        mask = mask * selected
        surface[~selected] = original[~selected]
        profile = skin.get('_faceProfile')
        # A source guide already declares its target and protected tissue. A
        # second procedural anatomy mask would change that target and attenuate
        # it again in its LOD and physical-frame consumers.
        strength = (np.ones(packet['vertexCount']) if profile is not None and
                    profile['operation'] == 'source-guide' else face_surface_strength(packet, skin))
        surface=original+(surface-original)*strength[:,None]
        mask*=strength
        report['facialMaterialSlots'] = slots
        report['nativeContactVertices']=int(np.count_nonzero(strength==0))
    return surface, transform, mask, report


def lower_detail_surface(packet: 'dict[str, Any]', skin: 'dict[str, Any]', donor):
    skin.pop('_compiledSourceGuide',None)
    original = art.points(packet['vertices'], art.CONVERT @ art.matrix(skin['rendererToPrefab']))
    source, displacement, ownership = donor[:3]
    delta = np.zeros_like(original); mask = np.zeros(len(original))
    for first in range(0, len(original), 128):
        current = original[first:first + 128]
        distances = np.sum((current[:, None, :] - source[None, :, :]) ** 2, axis=2)
        nearest = np.argpartition(distances, 2, axis=1)[:, :3]
        near_distances = np.take_along_axis(distances, nearest, axis=1)
        weights = 1 / np.maximum(near_distances, 1e-12); weights /= weights.sum(axis=1)[:, None]
        supported = np.min(near_distances, axis=1) < .015 ** 2
        delta[first:first + len(current)] = np.sum(displacement[nearest] * weights[:, :, None], axis=1) * supported[:, None]
        mask[first:first + len(current)] = np.sum(ownership[nearest] * weights, axis=1) * supported
    mask=np.clip(mask,0.,1.)
    if len(donor) == 4:
        if skin.get('_faceProfile',{}).get('operation')=='source-lod-transfer':mask=np.minimum(mask,np.asarray(skin['_faceProfile']['mask'],dtype=np.float64))
        skin['_compiledSourceGuide'] = donor[3]
        return art.evaluate_guide(original, mask, donor[3]), mask
    delta, _ = art.coherent_displacements(original, delta, mask)
    return original + delta, mask


def guide_channels(packet: 'dict[str, Any]', authored, skin: 'dict[str, Any]', transform, mask):
    profile = skin.get('_faceProfile')
    compiled = (art.compile_guide(profile, transform) if profile is not None and profile['operation'] == 'source-guide'
                else skin.get('_compiledSourceGuide'))
    if compiled is None:
        return None
    matrix = transform[:3, :3]
    inverse = np.linalg.inv(transform)
    protected = mask == 0
    native_p = np.asarray(packet['vertices'], dtype=np.float64).reshape(-1, 3)
    native_n = np.asarray(packet['normals'], dtype=np.float64).reshape(-1, 3)
    native_t = np.asarray(packet['tangents'], dtype=np.float64).reshape(-1, 4)
    require(len(native_n) == len(native_t) == len(native_p), 'Source guide transport requires complete native normal and tangent channels.')
    triangles = np.concatenate([np.asarray(s, dtype=np.int64).reshape(-1, 3) for s in packet['subMeshes']])
    rest_world = art.points(native_p.ravel().tolist(), transform)
    rest_normal = normalise(native_n @ np.linalg.inv(matrix))
    rest_areas = np.cross(rest_world[triangles[:, 1]]-rest_world[triangles[:, 0]], rest_world[triangles[:, 2]]-rest_world[triangles[:, 0]])
    oriented = triangles[:, ::-1] if np.median(np.sum(rest_areas*rest_normal[triangles].mean(axis=1),axis=1)) < 0 else triangles
    groups = normal_groups(rest_world, rest_normal)
    # UV duplicates share a surface rotation only when their actual skin owner
    # also agrees. Native hard normals remain separate normal_groups.
    bone_indices = np.asarray(packet['boneIndices']).reshape(-1,4)
    bone_weights = np.asarray(packet['weights']).reshape(-1,4)
    cohorts = {}; split_groups = []
    for group, indices, weights in zip(groups, bone_indices, bone_weights):
        key = (int(group), tuple(sorted((int(i),float(w)) for i,w in zip(indices,weights) if w != 0)))
        split_groups.append(cohorts.setdefault(key,len(cohorts)))
    groups = np.asarray(split_groups,dtype=np.int64)

    tissue_reports = []
    def transformed(p, n, t):
        world = art.points(p.ravel().tolist(), transform)
        changed = art.evaluate_guide(world, mask, compiled)
        changed, tissue = retain_local_surface_areas(packet, world, changed, mask)
        tissue_reports.append(tissue)
        local_p = art.points(changed.ravel().tolist(), inverse)
        world_n = normalise(n @ np.linalg.inv(matrix))
        before = surface_normals(world, oriented, groups, world, world_n)
        after = surface_normals(changed, oriented, groups, world, world_n)
        changed_n = normalise(rotate_between(world_n,before,after))
        local_n = normalise(changed_n @ matrix)
        local_t = np.empty((0, 3))
        if len(t):
            changed_t = rotate_between(normalise(t @ matrix.T),before,after)
            local_t = changed_t @ np.linalg.inv(matrix).T
            local_t = normalise(local_t - np.sum(local_t * local_n, axis=1)[:, None] * local_n)
        local_p[protected] = p[protected]
        local_n[protected] = n[protected]
        if len(t): local_t[protected] = t[protected]
        return local_p, local_n, local_t

    base_p, base_n, base_t = transformed(native_p, native_n, native_t[:, :3])
    require(np.array_equal(base_p.astype(np.float32), np.asarray(authored['vertices'], dtype=np.float32).reshape(-1, 3)),
            'Guide target was changed by a later surface backtracking operation.')
    authored['normals'] = base_n.astype(np.float32).ravel().tolist()
    if len(native_t): authored['tangents'] = np.column_stack((base_t, native_t[:, 3])).astype(np.float32).ravel().tolist()
    physical = 0
    for source_shape, target_shape in zip(packet['shapes'], authored['shapes']):
        for source_frame, target_frame in zip(source_shape['frames'], target_shape['frames']):
            delta_p = np.asarray(source_frame['vertices'], dtype=np.float64).reshape(-1, 3)
            delta_n = np.asarray(source_frame['normals'], dtype=np.float64).reshape(-1, 3)
            delta_t = np.asarray(source_frame['tangents'], dtype=np.float64).reshape(-1, 3)
            frame_p, frame_n, frame_t = transformed(native_p + delta_p, native_n + delta_n, native_t[:, :3] + delta_t)
            for name, value, original in [('vertices', frame_p-base_p, delta_p), ('normals', frame_n-base_n, delta_n), ('tangents', frame_t-base_t, delta_t)]:
                value[protected] = original[protected]
                target_frame[name] = value.astype(np.float32).ravel().tolist()
            physical += 1
    return dict(operation='F(base+nativeFrame)-F(base)', physicalFrames=physical,
                localTissueCorrections=tissue_reports,
                normalTangentTransport='native-to-authored actual triangle area-normal rotation per native smooth-normal and exact skin group; tangent transported by the same rotation',
                protectedVertices=int(np.count_nonzero(protected)), boneWeightsBindposesUvAndFrameIdentityPreserved=True)






def follow_guide_surface(packet: 'dict[str, Any]', skin: 'dict[str, Any]', donor_packet, donor_skin, donor_authored, anchors):
    """Transfer an attached native surface through its declared source triangles.

    Root position and triangle rotation follow the same authored donor, while
    each native companion's own skin, UV, material and physical channels stay
    owned by that companion. No runtime name or face-variant selection exists.
    """
    require(len(anchors)==packet['vertexCount'],'Attached source correspondence count differs.')
    require(donor_packet['vertexCount']==donor_authored['vertexCount'] and donor_packet['subMeshes']==donor_authored['subMeshes'],'Attached source donor changed its declared native topology.')
    require([(s['name'],[f['weight'] for f in s['frames']]) for s in donor_packet['shapes']]==[(s['name'],[f['weight'] for f in s['frames']]) for s in donor_authored['shapes']],'Attached source donor changed its native physical frame identity.')
    transform=art.CONVERT@art.matrix(skin['rendererToPrefab']);inverse=np.linalg.inv(transform)
    donor_transform=art.CONVERT@art.matrix(donor_skin['rendererToPrefab'])
    triangles=[];weights=[]
    for anchor in anchors:
        profile_anchor(anchor,donor_packet,donor_skin)
        require(anchor['type']=='sourceTriangle','An attached surface requires a native triangle correspondence.')
        first=anchor['triangleOrdinal']*3
        triangles.append(donor_packet['subMeshes'][anchor['subMesh']][first:first+3]);weights.append(anchor['barycentric'])
    triangles=np.asarray(triangles,dtype=np.int64);weights=np.asarray(weights)

    def basis(positions):
        points=positions[triangles];edge=points[:,1]-points[:,0];normal=np.cross(edge,points[:,2]-points[:,0])
        require(np.all(np.linalg.norm(edge,axis=1)>0) and np.all(np.linalg.norm(normal,axis=1)>0),'Attached source has a degenerate triangle basis.')
        x=normalise(edge);z=normalise(normal);y=np.cross(z,x)
        return np.stack((x,y,z),axis=2)

    def mapped(p,n,t,donor_before,donor_after):
        before=art.points(donor_before.ravel().tolist(),donor_transform);after=art.points(donor_after.ravel().tolist(),donor_transform)
        old_basis=basis(before);new_basis=basis(after)
        require(np.all(np.sum(old_basis[:,:,2]*new_basis[:,:,2],axis=1)>0),'Attached source donor reversed its native surface.')
        rotation=new_basis@old_basis.transpose(0,2,1)
        old_anchor=np.einsum('vi,vij->vj',weights,before[triangles]);new_anchor=np.einsum('vi,vij->vj',weights,after[triangles])
        world=art.points(p.ravel().tolist(),transform)
        changed=new_anchor+np.einsum('vij,vj->vi',rotation,world-old_anchor)
        local_p=art.points(changed.ravel().tolist(),inverse)
        world_n=normalise(n@np.linalg.inv(transform[:3,:3]));world_n=np.einsum('vij,vj->vi',rotation,world_n)
        local_n=normalise(world_n@transform[:3,:3]);world_t=normalise(t@transform[:3,:3].T)
        local_t=np.einsum('vij,vj->vi',rotation,world_t)@np.linalg.inv(transform[:3,:3]).T
        local_t=normalise(local_t-np.sum(local_t*local_n,axis=1)[:,None]*local_n)
        return local_p,local_n,local_t

    p=np.asarray(packet['vertices']).reshape(-1,3);n=np.asarray(packet['normals']).reshape(-1,3);t=np.asarray(packet['tangents']).reshape(-1,4)
    dp=np.asarray(donor_packet['vertices']).reshape(-1,3);ap=np.asarray(donor_authored['vertices']).reshape(-1,3)
    require(len(p)==len(n)==len(t),'Attached source requires complete native direction channels.')
    changed_p,changed_n,changed_t=mapped(p,n,t[:,:3],dp,ap)
    authored=copy.deepcopy(packet);authored['vertices']=changed_p.astype(np.float32).ravel().tolist();authored['normals']=changed_n.astype(np.float32).ravel().tolist()
    authored['tangents']=np.column_stack((changed_t,t[:,3])).astype(np.float32).ravel().tolist()
    require(all(np.isfinite(v).all() for v in (changed_p,changed_n,changed_t)),'Attached source produced nonfinite channels.')
    donor_frames = {}
    for shape, authored_shape in zip(donor_packet['shapes'], donor_authored['shapes']):
        for frame, authored_frame in zip(shape['frames'], authored_shape['frames']):
            donor_frames.setdefault((shape['name'], frame['weight']), []).append((frame, authored_frame))
    occurrences = {}
    for shape,target_shape in zip(packet['shapes'],authored['shapes']):
        for frame,target_frame in zip(shape['frames'],target_shape['frames']):
            key = (shape['name'], frame['weight'])
            occurrence = occurrences.get(key, 0)
            matches = donor_frames.get(key, [])
            require(occurrence < len(matches), 'Attached native expression has no corresponding donor physical frame.')
            df, af = matches[occurrence]
            occurrences[key] = occurrence + 1
            fp,fn,ft=mapped(p+np.asarray(frame['vertices']).reshape(-1,3),n+np.asarray(frame['normals']).reshape(-1,3),t[:,:3]+np.asarray(frame['tangents']).reshape(-1,3),dp+np.asarray(df['vertices']).reshape(-1,3),ap+np.asarray(af['vertices']).reshape(-1,3))
            for channel,value in [('vertices',fp-changed_p),('normals',fn-changed_n),('tangents',ft-changed_t)]:target_frame[channel]=value.astype(np.float32).ravel().tolist()
    authored['bounds']=physical_mesh_bounds(authored)
    authored['sourceFingerprint']=fingerprint(authored)[0]
    return authored


def refine_packet(packet: 'dict[str, Any]', skin: 'dict[str, Any]', lod, donor=None):
    profile=skin.get('_faceProfile')
    if profile is not None and profile['operation']=='source-guide-follow':
        require(type(donor) is dict and set(donor)=={'native','skin','authored'},'Attached source guided donor was not generated first.')
        if donor is None: raise ValueError('The guided donor is missing.')
        authored=follow_guide_surface(packet,skin,donor['native'],donor['skin'],donor['authored'],profile['parameters']['anchors'])
        transform=art.CONVERT@art.matrix(skin['rendererToPrefab']);before=art.points(packet['vertices'],transform);after=art.points(authored['vertices'],transform)
        return authored,np.ones(packet['vertexCount']),dict(sourceFingerprint=packet['sourceFingerprint'],authoredFingerprint=authored['sourceFingerprint'],role='attached',lod=lod,authoredVertices=packet['vertexCount'],insertedVertices=0,preservedOriginalVertices=packet['vertexCount'],subdivisionSourceEdges=[],sourceTopologyPreserved=True,displacementMaximumMm=float(np.max(np.linalg.norm(after-before,axis=1))*1000),shapeFrameCount=sum(len(s['frames']) for s in packet['shapes']),guideFrameTransport=dict(operation='native source-triangle anchor and rigid surface rotation; native companion skin and physical frame identities retained'))
    which = role(skin)
    p, transform, mask, report = sculpt_surface(packet, skin, which)
    original = art.points(packet['vertices'], transform)
    if (lod > 0 and donor is not None and which == 'skin'
            and (profile is None or profile['operation'] != 'source-guide')):
        p, mask = lower_detail_surface(packet, skin, donor)
        report['mode'] = 'source-matched-lod-surface-transfer'
        if which == 'skin':
            selected = np.zeros(packet['vertexCount'], dtype=bool)
            for slot in facial_material_slots(packet, skin):
                selected[np.asarray(packet['subMeshes'][slot], dtype=np.int64)] = True
            mask *= selected
            p[~selected] = original[~selected]
            if skin.get('_faceProfile') is not None and skin.get('_compiledSourceGuide') is None:
                target_mask = np.asarray(skin['_faceProfile']['mask'])
                p = original + (p-original) * target_mask[:, None]
                mask *= target_mask
    delta = p - original
    authored = copy.deepcopy(packet)
    authored['name'] = resource_naming.resource_names([resource_naming.ResourceName(packet['sourceFingerprint'], skin['path'], suffix='Refined')])[packet['sourceFingerprint']]
    inv = np.linalg.inv(transform)
    local = art.points(p.ravel().tolist(), inv)
    authored['vertices'] = local.astype(np.float32).ravel().tolist()
    triangles = np.concatenate([np.asarray(s, dtype=np.int64).reshape(-1, 3) for s in packet['subMeshes']])
    ns = art.native_normals(packet, transform)
    reverse = np.median(np.sum(np.cross(original[triangles[:, 1]] - original[triangles[:, 0]], original[triangles[:, 2]] - original[triangles[:, 0]]) * ns[triangles].mean(axis=1), axis=1)) < 0
    oriented = triangles[:, ::-1] if reverse else triangles
    groups = normal_groups(original, ns)
    before_normals = surface_normals(original, oriented, groups, original, ns)
    after_normals = surface_normals(p, oriented, groups, original, ns)
    world_normals = normalise(rotate_between(ns, before_normals, after_normals))
    if which == 'skin':
        # Smooth broad facial planes in the authored base. Eye, lip and wrinkle
        # relief still comes from the sculpt and its tangent-space normal map.
        compatible = art.smooth((np.sum(world_normals * after_normals, axis=1) - .5) / .25)
        amount = (mask * compatible * .4)[:, None]
        world_normals = normalise(world_normals + (after_normals - world_normals) * amount)
    local_normals = normalise(world_normals @ transform[:3, :3])
    authored['normals'] = local_normals.astype(np.float32).ravel().tolist()
    if packet['tangents']:
        tangents = np.asarray(packet['tangents']).reshape(-1, 4).copy()
        world_tangents = normalise(tangents[:, :3] @ transform[:3, :3].T)
        world_tangents = rotate_between(world_tangents, before_normals, after_normals)
        local_tangents = world_tangents @ np.linalg.inv(transform[:3, :3]).T
        local_tangents -= np.sum(local_tangents * local_normals, axis=1)[:, None] * local_normals
        tangents[:, :3] = normalise(local_tangents)
        authored['tangents'] = tangents.astype(np.float32).ravel().tolist()
    count = packet['vertexCount']
    # Retain native correspondence for cloth simulation and expression buffers.
    authored['vertexCount'] = count
    authored['indexFormat'] = packet['indexFormat']
    reference = np.asarray(packet['vertices']).reshape(-1, 3)
    native_normals = np.asarray(packet['normals']).reshape(-1, 3)
    native_tangents = np.asarray(packet['tangents']).reshape(-1, 4)
    final_triangles = np.concatenate([np.asarray(s, dtype=np.int64).reshape(-1, 3) for s in authored['subMeshes']])
    verts, facing_report = preserve_surface_orientation(reference, np.asarray(authored['vertices']).reshape(-1, 3), final_triangles, transform)
    normals, tangents = surface_basis(reference, verts, native_normals, native_tangents, final_triangles, transform, mask, which == 'skin')
    if skin.get('_faceProfile') is not None:
        protected = mask == 0
        verts[protected] = reference[protected]
        normals[protected] = native_normals[protected]
        if len(tangents):
            tangents[protected] = native_tangents[protected]
    authored['vertices'] = verts.ravel().tolist()
    authored['normals'] = normals.astype(np.float32).ravel().tolist()
    authored['tangents'] = tangents.astype(np.float32).ravel().tolist()
    guide_transport = guide_channels(packet, authored, skin, transform, mask)
    if guide_transport is not None:
        report['guideFrameTransport'] = guide_transport
    report.update(facing_report)
    authored['bounds'] = physical_mesh_bounds(authored)
    report.update(authoredVertices=authored['vertexCount'], insertedVertices=0, preservedOriginalVertices=count,
                  authoredFingerprint=fingerprint(authored)[0], lod=lod,
                  subdivisionSourceEdges=[], sourceTopologyPreserved=True,
                  displacementMaximumMm=float(np.max(np.linalg.norm(art.points(verts[:count].ravel().tolist(), transform) - original, axis=1)) * 1000),
                  shapeFrameCount=sum(len(s['frames']) for s in authored['shapes']))
    zero_roundtrip = art.points(original.ravel().tolist(), inv).astype(np.float32)
    report['faceOperation'] = face_operation(original, p, packet['vertices'], authored['vertices'], zero_roundtrip,
                                             mask, packet['sourceFingerprint'], report['authoredFingerprint'])
    authored['sourceFingerprint'] = report['authoredFingerprint']
    return authored, mask, report


def mesh_object(packet: 'dict[str, Any]', name, mask=None):
    verts = np.asarray(packet['vertices']).reshape(-1, 3)
    triangles = np.concatenate([np.asarray(s).reshape(-1, 3) for s in packet['subMeshes']])
    normals = np.asarray(packet['normals']).reshape(-1, 3)
    if np.median(np.sum(np.cross(verts[triangles[:, 1]] - verts[triangles[:, 0]], verts[triangles[:, 2]] - verts[triangles[:, 0]]) * normals[triangles].mean(axis=1), axis=1)) < 0:
        triangles = triangles[:, ::-1]
    mesh = bpy.data.meshes.new(name)
    mesh.from_pydata(verts.tolist(), [], triangles.tolist()); mesh.update()
    uv = next((u for u in packet['uv'] if u['channel'] == 0), None)
    if uv is None:
        raise ValueError('Facial bake requires source UV0')
    uv_values = np.asarray(uv['values']).reshape(-1, 4)[:, :2]
    loops = np.asarray([loop.vertex_index for loop in mesh.loops], dtype=np.intp)
    layer = mesh.uv_layers.new(name='UVMap'); layer.data.foreach_set('uv', uv_values[loops].ravel())
    set_mesh_custom_normals(mesh, normals.tolist())
    obj = bpy.data.objects.new(name, mesh); bpy.context.collection.objects.link(obj)
    if mask is not None:
        group = obj.vertex_groups.new(name='FacialSurface')
        for index, weight in enumerate(mask):
            if weight > 0: group.add([index], float(weight), 'REPLACE')
    return obj


def image_pixels(path):
    image = art.source_image(path); image.colorspace_settings.name = 'Non-Color'
    data = np.empty(image.size[0] * image.size[1] * 4, dtype=np.float32); image.pixels.foreach_get(data)
    return image, data.reshape(image.size[1], image.size[0], 4)


def write_image(path, data, srgb=False):
    path.parent.mkdir(parents=True, exist_ok=True)
    rgba = np.clip(data, 0, 1).astype(np.float32)
    if srgb:
        rgba = rgba.copy(); rgb = rgba[:, :, :3]
        rgba[:, :, :3] = np.where(rgb <= .0031308, rgb * 12.92, 1.055 * np.power(rgb, 1 / 2.4) - .055)
    write_packed_rgba(path, data.shape[1], data.shape[0], np.rint(rgba * 255).astype(np.uint8).tobytes())
    image = bpy.data.images.load(str(path), check_existing=False)
    image.name = resource_naming.source_label(path)
    image.colorspace_settings.name = 'Non-Color'; image.alpha_mode = 'CHANNEL_PACKED'
    return image


def texture_fingerprint(data):
    rgba = np.rint(np.clip(data, 0, 1) * 255).astype(np.uint8)
    version = b'Solasta.TextureRgba32Linear.1'
    return hashlib.sha256(bytes([len(version)]) + version + struct.pack('<ii', data.shape[1], data.shape[0]) + rgba.tobytes()).hexdigest()


def resize(data, size):
    height, width = data.shape[:2]
    target_height, target_width = (size, size) if isinstance(size, int) else size
    if (height, width) == (target_height, target_width):
        return data.copy()
    x = np.clip((np.arange(target_width) + .5) * width / target_width - .5, 0, width - 1)
    y = np.clip((np.arange(target_height) + .5) * height / target_height - .5, 0, height - 1)
    x0 = x.astype(np.int64); y0 = y.astype(np.int64)
    x1 = np.minimum(x0 + 1, width - 1); y1 = np.minimum(y0 + 1, height - 1)
    wx = (x - x0).astype(np.float32)[None, :, None]
    wy = (y - y0).astype(np.float32)[:, None, None]
    horizontal = data[:, x0] * (1 - wx) + data[:, x1] * wx
    return horizontal[y0] * (1 - wy) + horizontal[y1] * wy


def blur(data, radius=1):
    total = np.zeros_like(data)
    for y in range(-radius, radius + 1):
        for x in range(-radius, radius + 1):
            total += np.roll(np.roll(data, x, axis=1), y, axis=0)
    return total / (radius * 2 + 1) ** 2


def bake_surface(packet: 'dict[str, Any]', mask, output, size=1024, material_slot=None, renderer_to_prefab=None):
    bpy.ops.object.select_all(action='SELECT'); bpy.ops.object.delete(use_global=False)
    scene = bpy.context.scene; scene.render.engine = 'CYCLES'; scene.cycles.samples = 4
    scene.render.threads_mode = 'FIXED'; scene.render.threads = 2
    bake_packet = packet if material_slot is None else dict(packet, subMeshes=[packet['subMeshes'][material_slot]])
    low = mesh_object(bake_packet, 'ExpressionSurface', mask)
    if renderer_to_prefab is not None:
        low.matrix_world = Matrix((art.CONVERT @ art.matrix(renderer_to_prefab)).tolist())
        bpy.context.view_layer.objects.active = low
        low.select_set(True)
        bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
    high = low.copy(); high.data = low.data.copy(); high.name = 'RegionalHighSculpt'; bpy.context.collection.objects.link(high)
    bpy.context.view_layer.objects.active = high; high.select_set(True)
    subdiv = high.modifiers.new('SculptSurfaceDensity', 'SUBSURF'); subdiv.levels = 2; subdiv.render_levels = 2
    bpy.ops.object.modifier_apply(modifier=subdiv.name)
    # Microscopic relief is sculpted in object space and transferred to tangent
    # normals. Existing wrinkles/pigmentation remain in their original channels.
    noise = bpy.data.textures.new('RestrainedSkinPores', type='CLOUDS'); noise.noise_scale = .00065; noise.noise_depth = 1
    displacement = high.modifiers.new('SkinMicroRelief', 'DISPLACE'); displacement.texture = noise
    displacement.strength = .000012; displacement.mid_level = .5; displacement.vertex_group = 'FacialSurface'
    bpy.ops.object.modifier_apply(modifier=displacement.name)
    material = bpy.data.materials.new('BakeDestination'); material.use_nodes = True
    image = bpy.data.images.new('SculptNormal', width=size, height=size, alpha=True, float_buffer=True)
    image.generated_color = (.5, .5, 1, 1); image.colorspace_settings.name = 'Non-Color'
    node = material.node_tree.nodes.new('ShaderNodeTexImage'); node.image = image
    material.node_tree.nodes.active = node; low.data.materials.clear(); low.data.materials.append(material)
    bpy.ops.object.select_all(action='DESELECT'); low.select_set(True); high.select_set(True); bpy.context.view_layer.objects.active = low
    scene.render.bake.use_selected_to_active = True; scene.render.bake.cage_extrusion = .004
    scene.render.bake.max_ray_distance = .008; scene.render.bake.margin = 12
    bpy.ops.object.bake(type='NORMAL', normal_space='TANGENT')
    values = np.empty(size * size * 4, dtype=np.float32); image.pixels.foreach_get(values)
    baked = values.reshape(size, size, 4).copy()
    write_image(output / 'SculptNormal.png', baked)
    # Bake the exact per-vertex correction ownership into the same native atlas.
    attr = low.data.color_attributes.new(name='FaceRegion', type='FLOAT_COLOR', domain='POINT')
    values = np.column_stack((mask, mask, mask, np.ones(len(mask)))).astype(np.float32)
    attr.data.foreach_set('color', values.ravel())
    nodes = material.node_tree.nodes; links = material.node_tree.links
    color = nodes.new('ShaderNodeVertexColor'); color.layer_name = 'FaceRegion'
    emission = nodes.new('ShaderNodeEmission'); links.new(color.outputs['Color'], emission.inputs['Color'])
    links.new(emission.outputs[0], nodes.get('Material Output').inputs['Surface'])
    region_image = bpy.data.images.new('FaceOwnership', width=size, height=size, alpha=True, float_buffer=True)
    region_image.colorspace_settings.name = 'Non-Color'; node.image = region_image; nodes.active = node
    high.select_set(False); scene.render.bake.use_selected_to_active = False
    bpy.ops.object.bake(type='EMIT')
    region = np.empty(size * size * 4, dtype=np.float32); region_image.pixels.foreach_get(region)
    region = region.reshape(size, size, 4)[:, :, 0]
    write_image(output / 'FacialOwnership.png', np.dstack((region, region, region, np.ones_like(region))))
    bpy.ops.wm.save_as_mainfile(filepath=str(output / 'FacialSculpt.blend'), check_existing=False)
    return baked, region, dict(highVertices=len(high.data.vertices), lowVertices=len(low.data.vertices),
                               bakeType='Cycles selected-to-active tangent NORMAL', size=size,
                               sculptUnits='native renderer-to-prefab metres',
                               sourceScalpNeckProtection=True, maximumMicroReliefMetres=.000006)


def recipe_mesh(packet: 'dict[str, Any]'):
    return dict(name=packet['name'], vertices=packet['vertices'], normals=packet['normals'], tangents=packet['tangents'],
                colors=packet['colors'], uvChannels=[dict(channel=u['channel'], dimension=u['dimension'],
                    values=np.asarray(u['values']).reshape(-1, 4)[:, :u['dimension']].ravel().tolist()) for u in packet['uv']],
                subMeshes=[dict(topology='Triangles', indices=indices) for indices in packet['subMeshes']],
                bounds=dict(center=packet['bounds'][:3], size=packet['bounds'][3:]),
                indexFormat='UInt32' if packet['indexFormat'] else 'UInt16',
                skin=dict(bindPoses=packet['bindPoses'], boneWeights=packet['weights'], boneIndices=packet['boneIndices']),
                blendShapes=[dict(name=s['name'], frames=s['frames']) for s in packet['shapes']])



def extremity_refinement_profile() -> 'dict[str, Any]':
    return dict(minimumEdgeMillimetres=[4, 8, 12], minimumBowMillimetres=0.12,
                maximumBowMillimetres=1.2, maximumBowEdgeFraction=0.12,
                minimumNormalAngleDegrees=8, maximumNormalAngleDegrees=110,
                hardSeamNormalAngleDegrees=28, protectedOpenCutRings=1,
                minimumChildParentNormalCosine=0.5)


def extremity_normalized(values):
    return values / np.maximum(np.linalg.norm(values, axis=-1, keepdims=True), 1e-30)


def extremity_f32(values):
    return np.asarray(values, dtype=np.float32).reshape(-1).tolist()

def extremity_native_effective_row(raw):
    """Unity2019 raw4 -> effective All1 model, gated by the full source witness."""
    values = np.asarray(raw, dtype=np.float32)
    assert values.shape == (4,) and np.all(np.isfinite(values))
    assert np.all((values >= 0) & (values <= 1))
    total = np.float32(np.float32(np.float32(values[0] + values[1]) + values[2]) + values[3])
    assert total > 0 and abs(sum(float(v) for v in values) - 1.0) < 1e-5
    reciprocal = np.float32(np.float32(1.0) / total)
    return [float(np.float32(value * reciprocal)) for value in values]

def extremity_skin_influences(weights, indices, i):
    # Preserve source duplicates and order. Only the derived mixed result merges bones.
    return [(int(index), weight) for weight, index in zip(extremity_native_effective_row(weights[i]), indices[i]) if weight > 0]

def canonical_skin_values(weights, indices, sources):
    result = {}
    for source, contribution in sources:
        for bone, weight in extremity_skin_influences(weights, indices, source):
            result[bone] = result.get(bone, 0.0) + weight * float(contribution)
    ordered = sorted(result.items(), key=lambda pair: (-pair[1], pair[0]))
    assert 1 <= len(ordered) <= 4
    return ordered

def canonical_skin(weights, indices, sources):
    ordered = canonical_skin_values(weights, indices, sources)
    total = sum(weight for _, weight in ordered)
    return [bone for bone, _ in ordered] + [0] * (4 - len(ordered)), extremity_f32([weight / total for _, weight in ordered] + [0.0] * (4 - len(ordered)))

def extremity_skin_map(weights, indices, i):
    result = {}
    for index, weight in extremity_skin_influences(weights, indices, i):
        result[index] = result.get(index, 0.0) + weight
    return result

def extremity_midpoint(p, n, a, b):
    edge = p[b] - p[a]
    c0 = (2 * p[a] + p[b] - np.dot(edge, n[a]) * n[a]) / 3
    c1 = (2 * p[b] + p[a] + np.dot(edge, n[b]) * n[b]) / 3
    return (p[a] + 3 * c0 + 3 * c1 + p[b]) / 8

def refine_native_skin_surface(native, current_frames, matrix, lod, forbidden=None, attempts=0,
                               *, profile=None, vertex_ownership=None):
    settings = extremity_refinement_profile() if profile is None else profile
    forbidden = set() if forbidden is None else forbidden
    assert attempts < 12, 'Adaptive physical-edge orientation protection did not converge'
    p = np.asarray(native['vertices'], dtype=np.float64).reshape(-1, 3)
    n = extremity_normalized(np.asarray(native['normals'], dtype=np.float64).reshape(-1, 3))
    tangents = np.asarray(native['tangents'], dtype=np.float64).reshape(-1, 4)
    uv = np.asarray(native['uv0'], dtype=np.float64).reshape(-1, 2)
    weights = np.asarray(native['weights'], dtype=np.float64).reshape(-1, 4)
    indices = np.asarray(native['boneIndices'], dtype=np.int64).reshape(-1, 4)
    m = np.asarray(matrix, dtype=np.float64).reshape(4, 4).T[:3, :3]
    world = p @ m.T
    ownership = np.ones(len(p)) if vertex_ownership is None else np.asarray(vertex_ownership, dtype=float)
    require(ownership.shape == (len(p),) and np.isfinite(ownership).all()
            and np.all((ownership >= 0) & (ownership <= 1)), 'Invalid native skin surface ownership.')
    faces = [np.asarray(s, dtype=np.int64).reshape(-1, 3) for s in native['subMeshes']]
    edge_faces = {}
    for slot, triangles in enumerate(faces):
        for fi, tri in enumerate(triangles):
            for a, b in zip(tri, np.roll(tri, -1)):
                edge_faces.setdefault(tuple(sorted((int(a), int(b)))), []).append((slot, fi))
    position_groups = {}
    for i, pos in enumerate(world):
        position_groups.setdefault(tuple(np.round(pos / 1e-6).astype(np.int64)), []).append(i)
    canonical_vertices = {i: min(g) for g in position_groups.values() for i in g}
    frame_motion = [np.asarray(frame['vertices'], dtype=np.float32).reshape(-1, 3)
                    for shape in current_frames for frame in shape.get('frames', [])]
    motion_seams = set()
    for group in position_groups.values():
        motions = [(extremity_skin_map(weights, indices, i), b''.join(frame[i].tobytes() for frame in frame_motion)) for i in group]
        if any(value != motions[0] for value in motions[1:]):
            motion_seams.update(canonical_vertices[i] for i in group)
    physical = {}
    for edge, members in edge_faces.items():
        key = tuple(sorted((canonical_vertices[edge[0]], canonical_vertices[edge[1]])))
        if key[0] != key[1]:
            physical.setdefault(key, []).append(edge)
    graph = {}
    for a, b in physical:
        graph.setdefault(a, set()).add(b)
        graph.setdefault(b, set()).add(a)
    cut_vertices = set();cut_edges = set()
    for edge, twins in physical.items():
        if len(set(f for t in twins for f in edge_faces[t])) == 1:
            cut_vertices.update(edge);cut_edges.add(edge)
    protected = set(cut_vertices) if settings.get('protectCutVertices', True) else set()
    for _ in range(settings['protectedOpenCutRings']):
        protected |= set(v for i in list(protected) for v in graph.get(i, []))
    hard = set()
    for group in position_groups.values():
        if len(group) > 1 and np.min(n[group] @ n[group].T) < np.cos(np.radians(settings['hardSeamNormalAngleDegrees'])):
            hard.update(canonical_vertices[i] for i in group)
    protected |= hard | motion_seams
    rejected = {}
    selected = {}
    for key, twins in physical.items():
        reason = None
        a0, b0 = twins[0]
        length = np.linalg.norm((p[b0] - p[a0]) @ m.T)
        angle = np.degrees(np.arccos(np.clip(np.dot(n[a0], n[b0]), -1, 1)))
        if key in forbidden:
            reason = 'physical-frame-orientation-protected-face'
        elif key in cut_edges:
            reason = 'native-open-cut-edge'
        elif any(i in protected for i in key):
            reason = 'protected-cut-or-hard-seam'
        elif min(ownership[i] for edge in twins for i in edge) <= 0:
            reason = 'unowned-source-surface'
        elif length * 1000 < settings['minimumEdgeMillimetres'][lod]:
            reason = 'short-edge'
        elif not settings['minimumNormalAngleDegrees'] <= angle <= settings['maximumNormalAngleDegrees']:
            reason = 'flat-or-sharp-normal-field'
        elif len(set(slot for t in twins for slot, _ in edge_faces[t])) > 1:
            reason = 'material-boundary'
        elif any(tangents[a, 3] != tangents[b, 3] for a, b in twins):
            reason = 'tangent-handedness-boundary'
        maps = [(extremity_skin_map(weights, indices, a), extremity_skin_map(weights, indices, b)) for a, b in twins]
        if reason is None and any(len(set(left) | set(right)) > 4 for left, right in maps):
            reason = 'more-than-four-merged-native-influences'
        if reason is None:
            # UV/normal duplicate edges may be ordered oppositely. Their semantic native skin must match.
            aligned = []
            for (a, b), pair in zip(twins, maps):
                aligned.append(pair if canonical_vertices[a] == key[0] else pair[::-1])
            if any(pair != aligned[0] for pair in aligned[1:]):
                reason = 'physical-seam-skin-mismatch'
        candidate = np.mean([extremity_midpoint(p, n, a, b) for a, b in twins], axis=0)
        linear = (p[a0] + p[b0]) / 2
        bow = candidate - linear
        bow_length = np.linalg.norm(bow @ m.T)
        if reason is None and bow_length * 1000 < settings['minimumBowMillimetres']:
            reason = 'flat-pn-edge'
        if reason:
            rejected[reason] = rejected.get(reason, 0) + 1
            continue
        maximum = min(settings['maximumBowMillimetres'] / 1000, length * settings['maximumBowEdgeFraction'])
        factor = min(1, maximum / max(bow_length, 1e-30)) * min(ownership[i] for edge in twins for i in edge)
        selected[key] = dict(twins=twins, position=linear + bow * factor, factor=factor,
                             bowMetres=min(maximum, bow_length), normal=extremity_normalized(np.mean(n[[i for edge in twins for i in edge]], axis=0)))
    positions = list(p)
    normals = list(n)
    new_tangents = list(tangents)
    new_uv = list(uv)
    new_weights: list[np.ndarray | list[float]] = list(weights)
    new_indices: list[np.ndarray | list[int]] = list(indices)
    added = []
    edge_index = {}
    for physical_key, entry in selected.items():
        for a, b in entry['twins']:
            idx = len(positions)
            edge_index[(a, b)] = idx
            positions.append(entry['position'])
            normals.append(entry['normal'])
            t = (tangents[a, :3] + tangents[b, :3]) / 2
            t = extremity_normalized(t - np.dot(t, entry['normal']) * entry['normal'])
            new_tangents.append(np.r_[t, tangents[a, 3]])
            new_uv.append((uv[a] + uv[b]) / 2)
            ids, skin = canonical_skin(weights, indices, [(a, 0.5), (b, 0.5)])
            new_indices.append(ids)
            new_weights.append(skin)
            added.append(dict(index=idx, edge=[a, b], physicalEdge=list(physical_key), factor=entry['factor']))
    output = copy.deepcopy(native)
    # The prefix is copied as serialized numbers, preserving every original Float32 sample exactly.
    for name, values, width in [('vertices', positions, 3), ('normals', normals, 3), ('tangents', new_tangents, 4),
                                ('uv0', new_uv, 2), ('weights', new_weights, 4), ('boneIndices', new_indices, 4)]:
        extra = values[len(p):]
        output[name] = list(native[name]) + (np.asarray(extra, dtype=np.int64).reshape(-1).tolist() if name == 'boneIndices' else extremity_f32(extra))
        assert output[name][:len(native[name])] == native[name]
    def inserted_vertex(a, b):
        return edge_index.get(tuple(sorted((int(a), int(b)))))
    new_submeshes = []
    orientation_cosines = []
    pp = np.asarray(output['vertices']).reshape(-1, 3)
    parent_for_child = []
    parent_triangles = np.concatenate(faces)
    parent_index = 0
    for triangles in faces:
        out = []
        for a, b, c in triangles:
            ab, bc, ca = inserted_vertex(a, b), inserted_vertex(b, c), inserted_vertex(c, a)
            count = sum(i is not None for i in [ab, bc, ca])
            if count == 0: child = [[a, b, c]]
            elif count == 3: child = [[a, ab, ca], [ab, b, bc], [ca, bc, c], [ab, bc, ca]]
            elif count == 1:
                if ab is not None: child = [[a, ab, c], [ab, b, c]]
                elif bc is not None: child = [[b, bc, a], [bc, c, a]]
                else: child = [[c, ca, b], [ca, a, b]]
            elif ab is None: child = [[c, ca, bc], [ca, a, b], [ca, b, bc]]
            elif bc is None: child = [[a, ab, ca], [ab, b, c], [ab, c, ca]]
            else: child = [[b, bc, ab], [ab, bc, c], [ab, c, a]]
            parent_normal = np.cross(p[b] - p[a], p[c] - p[a])
            for tri in child:
                child_normal = np.cross(pp[tri[1]] - pp[tri[0]], pp[tri[2]] - pp[tri[0]])
                dot = float(np.dot(parent_normal, child_normal))
                orientation_cosines.append(dot / max(np.linalg.norm(parent_normal) * np.linalg.norm(child_normal), 1e-30))
                parent_for_child.append(parent_index)
            out.extend(int(v) for tri in child for v in tri)
            parent_index += 1
        new_submeshes.append(out)
    output['subMeshes'] = new_submeshes
    output['name'] = 'PrivateAdaptivePnExtremity'
    output['blendShapeChannels'] = len(current_frames)
    new_frames = copy.deepcopy(current_frames)
    for channel in new_frames:
        channel['vertexCount'] = len(positions)
        for frame in channel.get('frames', []):
            fp = p + np.asarray(frame['vertices']).reshape(-1, 3)
            fn = extremity_normalized(n + np.asarray(frame['normals']).reshape(-1, 3))
            ft = tangents[:, :3] + np.asarray(frame['tangents']).reshape(-1, 3)
            extra_p, extra_n, extra_t = [], [], []
            group_positions = {}
            group_normals = {}
            for key, entry in selected.items():
                a, b = entry['twins'][0]
                linear = (fp[a] + fp[b]) / 2
                pn = np.mean([extremity_midpoint(fp, fn, x, y) for x, y in entry['twins']], axis=0)
                group_positions[key] = linear + (pn - linear) * entry['factor']
                group_normals[key] = extremity_normalized(np.mean(fn[[i for edge in entry['twins'] for i in edge]], axis=0))
            for added_row in added:
                idx = added_row['index']
                a, b = added_row['edge']
                pn = group_positions[tuple(added_row['physicalEdge'])]
                nn = group_normals[tuple(added_row['physicalEdge'])]
                tt = (ft[a] + ft[b]) / 2
                tt = extremity_normalized(tt - np.dot(tt, nn) * nn)
                extra_p.append(pn - positions[idx])
                extra_n.append(nn - normals[idx])
                extra_t.append(tt - new_tangents[idx][:3])
            for name, extra in [('vertices', extra_p), ('normals', extra_n), ('tangents', extra_t)]:
                original = list(frame[name])
                frame[name] = original + extremity_f32(extra)
                assert frame[name][:len(original)] == original
    assert output['bindPoses'] == native['bindPoses']
    original_vertex_count = len(p)
    new_faces = np.concatenate([np.asarray(s).reshape(-1, 3) for s in output['subMeshes']])
    pp = np.asarray(output['vertices']).reshape(-1, 3)
    # A source normal field may overshoot a narrow face near a nail, crease or claw.
    # Refuse the responsible physical edge everywhere (including its UV twins),
    # preserving conforming topology and the native local feature in every physical frame.
    poses = [(p, pp)]
    for source_channel, candidate_channel in zip(current_frames, new_frames):
        for source_frame, candidate_frame in zip(source_channel.get('frames', []), candidate_channel.get('frames', [])):
            poses.append((p + np.asarray(source_frame['vertices']).reshape(-1, 3),
                          pp + np.asarray(candidate_frame['vertices']).reshape(-1, 3)))
    parent_for_child = np.asarray(parent_for_child, dtype=np.int64)
    bad_parents = set()
    for source_pose, candidate_pose in poses:
        source_cross = np.cross(source_pose[parent_triangles[:, 1]] - source_pose[parent_triangles[:, 0]],
                                source_pose[parent_triangles[:, 2]] - source_pose[parent_triangles[:, 0]])
        child_cross = np.cross(candidate_pose[new_faces[:, 1]] - candidate_pose[new_faces[:, 0]],
                               candidate_pose[new_faces[:, 2]] - candidate_pose[new_faces[:, 0]])
        parent_cross = source_cross[parent_for_child]
        dots = np.sum(parent_cross * child_cross, axis=1)
        parent_square = np.sum(parent_cross * parent_cross, axis=1)
        denominator = np.maximum(np.linalg.norm(parent_cross,axis=1)*np.linalg.norm(child_cross,axis=1),1e-30)
        invalid = (dots <= parent_square * 1e-7) | (dots / denominator < settings['minimumChildParentNormalCosine'])
        bad_parents.update(int(i) for i in parent_for_child[invalid])
    if bad_parents:
        refuse = set()
        for i in bad_parents:
            tri = parent_triangles[i]
            for a, b in zip(tri, np.roll(tri, -1)):
                key = tuple(sorted((canonical_vertices[int(a)], canonical_vertices[int(b)])))
                if key in selected:
                    refuse.add(key)
        assert refuse - forbidden, 'Native parent triangle itself is degenerate or inverted'
        return refine_native_skin_surface(native, current_frames, matrix, lod, forbidden | refuse, attempts + 1,
                                          profile=profile, vertex_ownership=vertex_ownership)
    # Detect accidental topology inversion in the direct geometric sample.
    finite = all(np.isfinite(np.asarray(output[k])).all() for k in ['vertices', 'normals', 'tangents', 'uv0', 'weights'])
    assert finite and np.all(new_faces >= 0) and np.all(new_faces < len(pp))
    minimum_triangle_area = float(np.min(np.linalg.norm(np.cross(pp[new_faces[:, 1]] - pp[new_faces[:, 0]], pp[new_faces[:, 2]] - pp[new_faces[:, 0]]), axis=1) / 2))
    assert minimum_triangle_area > 1e-14
    report: dict[str, Any] = dict(originalVertices=original_vertex_count, candidateVertices=len(pp), addedVertices=len(added),
                  originalTriangles=sum(len(s) // 3 for s in native['subMeshes']), candidateTriangles=len(new_faces),
                  selectedPhysicalEdges=len(selected), protectedCutVertices=len(cut_vertices), protectedCutRingVertices=len(protected - hard),
                  protectedHardSeamVertices=len(hard), protectedMotionSeamVertices=len(motion_seams), rejectedEdges=rejected,
                  maxActualBowMillimetres=max([entry['bowMetres'] * 1000 for entry in selected.values()] or [0]),
                  allOriginalVertexChannelsExact=True, allOriginalPhysicalFrameSamplesExact=True,
                  nativeBindPosesExact=True, originalBoundaryLoopSamplesAndEdgesExact=True,
                  addedVertexNominalBytes=len(added) * 96,
                  addedPhysicalFrameDeltaBytes=len(added) * 36 * sum(len(f.get('frames', [])) for f in new_frames),
                  minimumTriangleAreaSquareMetres=minimum_triangle_area,
                  allChildFacesRetainParentOrientation=True,
                  minimumChildParentNormalCosine=float(min(orientation_cosines)),
                  addedFrameNormalsUseSamePhysicalTwinBasisAsRest=True,
                  physicalFramesOrientationValidated=len(poses)-1,
                  orientationProtectionPasses=attempts,
                  orientationProtectedPhysicalEdges=len(forbidden),
                  physicalFrameEnvelopeBounds=dict(min=np.min(np.concatenate([v for _, v in poses]),axis=0).tolist(),
                                                   max=np.max(np.concatenate([v for _, v in poses]),axis=0).tolist()))
    return output, new_frames, added, report

def refine_skin_packet(packet: 'dict[str, Any]', renderer_to_prefab, lod, *, profile=None, vertex_ownership=None):
    """Refine source curvature while retaining native cuts, skin and frames."""
    require(type(lod) is int and lod in (0, 1, 2), 'An extremity requires a native LOD from 0 to 2.')
    transform = np.asarray(renderer_to_prefab, dtype=float).reshape(4, 4).T
    require(np.isfinite(transform).all() and abs(np.linalg.det(transform[:3, :3])) > 1e-12,
            'The native extremity transform is singular or nonfinite.')
    uv0 = next(channel for channel in packet['uv'] if channel['channel'] == 0)
    basic = {key: copy.deepcopy(packet[key]) for key in ('vertices', 'normals', 'tangents', 'weights', 'boneIndices', 'subMeshes')}
    basic['uv0'] = np.asarray(uv0['values']).reshape(-1, 4)[:, :2].ravel().tolist()
    basic['bindPoses'] = np.asarray(packet['bindPoses']).reshape(-1, 16).tolist()
    basic['blendShapeChannels'] = len(packet['shapes'])
    refined, frames, added, metrics = refine_native_skin_surface(basic, packet['shapes'], renderer_to_prefab, lod,
                                                               profile=profile, vertex_ownership=vertex_ownership)
    candidate = copy.deepcopy(packet)
    for key in ('vertices', 'normals', 'tangents', 'weights', 'boneIndices', 'subMeshes'):
        candidate[key] = refined[key]
    candidate['vertexCount'] = len(candidate['vertices']) // 3
    candidate['shapes'] = frames
    for shape in frames:
        shape.pop('vertexCount', None)
    for channel in candidate['uv']:
        original = np.asarray(channel['values']).reshape(-1, 4)
        channel['values'] += extremity_f32([(original[row['edge'][0]] + original[row['edge'][1]]) * .5 for row in added])
    if packet['colors']:
        original = np.asarray(packet['colors']).reshape(-1, 4)
        candidate['colors'] += extremity_f32([(original[row['edge'][0]] + original[row['edge'][1]]) * .5 for row in added])
    bounds = np.asarray(packet['bounds'])
    low = np.minimum(metrics['physicalFrameEnvelopeBounds']['min'], bounds[:3] - bounds[3:] * .5)
    high = np.maximum(metrics['physicalFrameEnvelopeBounds']['max'], bounds[:3] + bounds[3:] * .5)
    candidate['bounds'] = np.r_[(low + high) * .5, high - low].tolist()
    for key in ('vertices', 'normals', 'tangents', 'weights', 'boneIndices', 'colors'):
        require(candidate[key][:len(packet[key])] == packet[key], 'An extremity changed a native vertex channel: ' + key)
    require(candidate['bindPoses'] == packet['bindPoses'], 'An extremity changed its native bindposes.')
    for before, after in zip(packet['uv'], candidate['uv']):
        require(before['channel'] == after['channel'] and before['dimension'] == after['dimension']
                and after['values'][:len(before['values'])] == before['values'], 'An extremity changed a native UV channel.')
    for before, after in zip(packet['shapes'], candidate['shapes']):
        require(before['index'] == after['index'] and before['name'] == after['name'], 'An extremity changed a native frame identity.')
        for source, target in zip(before['frames'], after['frames']):
            require(source['weight'] == target['weight'], 'An extremity changed a native frame weight.')
            for key in ('vertices', 'normals', 'tangents'):
                require(target[key][:len(source[key])] == source[key], 'An extremity changed a native physical frame.')
    candidate['sourceFingerprint'] = fingerprint(candidate)[0]
    return candidate, added, metrics


def refine_extremity_packet(packet: 'dict[str, Any]', renderer_to_prefab, lod):
    return refine_skin_packet(packet, renderer_to_prefab, lod)


def indexed_surface_uv_metrics(packet: 'dict[str, Any]',skin: 'dict[str, Any]',slot,shape,feather,sampler,vertex_field=None):
    import heapq
    """Actual native slot UV coverage, source seam closure and rest metric."""
    h,w=shape
    positions=np.asarray(packet['vertices'],dtype='<f4').reshape(-1,3)
    transform=np.asarray(skin['rendererToPrefab']).reshape(4,4).T
    world=positions@transform[:3,:3].T+transform[:3,3]
    uvrow=next(u for u in packet['uv'] if u['channel']==0)
    uv=np.asarray(uvrow['values']).reshape(-1,4)[:,:2]
    material=skin['materials'][slot]
    prop=next(p for p in material['properties'] if p['name']=='_MainTex')
    uv=uv*np.asarray(prop['scale'])+np.asarray(prop['offset'])
    triangles=np.asarray(packet['subMeshes'][slot],dtype=int).reshape(-1,3)
    outside=[bool(np.min(uv[triangles,axis]) < -1e-6 or np.max(uv[triangles,axis]) > 1+1e-6) for axis in (0,1)]
    require(all(not outside[axis] or sampler['wrapU' if axis==0 else 'wrapV']==0 for axis in (0,1)), 'Periodic source must retain its actual Repeat sampler.')
    tile_instances=[]
    for tri in triangles:
        low=np.floor(uv[tri].min(axis=0)+1e-6).astype(int);high=np.floor(uv[tri].max(axis=0)-1e-6).astype(int)
        low=np.minimum(low,high)
        for u in range(int(low[0]),int(high[0])+1):
            for v in range(int(low[1]),int(high[1])+1):tile_instances.append((tri,np.array([u,v])))
    # Weld only native duplicate position + skin + all native position frames.
    # Distinct skin/expression correspondences never become a fake interior edge.
    fields=[np.arange(len(positions),dtype='<u4').reshape(-1,1)]
    keys=np.ascontiguousarray(np.concatenate(fields,axis=1))
    _,indices,groups=np.unique(keys,axis=0,return_index=True,return_inverse=True)
    group_positions=world[indices]
    edge_counts={};edges={}
    for triangle in groups[triangles]:
        for a,b in zip(triangle,np.roll(triangle,-1)):
            if a==b:continue
            key=tuple(sorted((int(a),int(b))))
            edge_counts[key]=edge_counts.get(key,0)+1
            edges[key]=float(np.linalg.norm(group_positions[a]-group_positions[b]))
    adjacency=[[] for _ in indices]
    boundary=set()
    for (a,b),length in edges.items():
        adjacency[a].append((b,length));adjacency[b].append((a,length))
        if edge_counts[(a,b)]==1:boundary.update((a,b))
    distances=np.full(len(indices),np.inf)
    queue=[]
    for index in boundary:distances[index]=0;heapq.heappush(queue,(0,index))
    while queue:
        distance,index=heapq.heappop(queue)
        if distance!=distances[index]:continue
        for other,length in adjacency[index]:
            next_distance=distance+length
            if next_distance<distances[other]:distances[other]=next_distance;heapq.heappush(queue,(next_distance,other))
    vertex_weight=np.clip(distances[groups]/(6*feather),0,1)
    vertex_weight=vertex_weight*vertex_weight*(3-2*vertex_weight)
    if vertex_field is not None:
        vertex_weight=np.asarray(vertex_field,dtype=float)
        require(vertex_weight.shape==(len(positions),) and np.all(np.isfinite(vertex_weight)) and np.all((vertex_weight>=0)&(vertex_weight<=1)), 'Invalid declared native vertex field.')
    ownership=np.zeros(shape);metric=np.zeros(shape);anisotropy=np.zeros(shape)
    covered=np.zeros(shape,dtype=bool);overlaps=0;physical_triangles=0
    for tri,tile in tile_instances:
        tex=np.column_stack(((uv[tri,0]-tile[0])*w-.5,(1-(uv[tri,1]-tile[1]))*h-.5))
        delta=np.stack((tex[1]-tex[0],tex[2]-tex[0]),axis=1)
        determinant=np.linalg.det(delta)
        if abs(determinant)<1e-10:continue
        jacobian=np.stack((world[tri[1]]-world[tri[0]],world[tri[2]]-world[tri[0]]),axis=1)@np.linalg.inv(delta)
        singular=np.linalg.svd(jacobian,compute_uv=False)
        if singular[-1]<=0:continue
        density=float(np.sqrt(np.prod(singular)))
        ratio=float(singular[0]/singular[-1])
        lo=np.maximum(0,np.ceil(tex.min(axis=0)).astype(int));hi=np.minimum((w-1,h-1),np.floor(tex.max(axis=0)).astype(int))
        if np.any(hi<lo):continue
        yy,xx=np.mgrid[lo[1]:hi[1]+1,lo[0]:hi[0]+1]
        ab=np.stack((xx-tex[0,0],yy-tex[0,1]),axis=-1)@np.linalg.inv(delta).T
        bary=np.dstack((1-ab.sum(axis=2),ab))
        inside=np.all(bary>=-1e-7,axis=2)
        if not np.any(inside):continue
        rows=yy[inside];cols=xx[inside]
        interpolated=np.clip(bary[inside]@vertex_weight[tri],0,1)
        repeat=covered[rows,cols]
        overlaps+=int(repeat.sum())
        ownership[rows,cols]=np.where(repeat,np.minimum(ownership[rows,cols],interpolated),interpolated)
        metric[rows,cols]=np.where(repeat,np.maximum(metric[rows,cols],density),density)
        anisotropy[rows,cols]=ratio
        covered[rows,cols]=True
        physical_triangles+=1
    require(np.any(covered),'Native source slot has no rasterizable UV surface.')
    metric[~covered]=np.median(metric[covered])
    report=dict(nativeTriangles=len(triangles),rasterizedTriangles=physical_triangles,nativeVertices=len(positions),
                nativeWeldGroups=len(indices),trueOpenBoundaryGroups=len(boundary),
                boundaryFadeMetres=6*feather,duplicatedUvCoveragePixels=overlaps,
                metresPerTexelQuantiles=np.quantile(metric[covered],[0,.1,.5,.9,1]).tolist(),
                anisotropyQuantiles=np.quantile(anisotropy[covered],[0,.1,.5,.9,1]).tolist(),
                nativeSourceChannelsWritten=False,
                boundaryRule='original indexed topology; no split-vertex weld with omitted expression frames; native indexed open boundary fade' if vertex_field is None else 'declared native vertex field; shared UV coverage retains minimum owning value')
    return ownership,metric,report,covered


def source_surface_junction(first,first_skin,first_slot,second,second_skin,second_slot,first_shape,second_shape,tolerance=.001):
    """Match actual source-cut edges in common native bind frames and material UVs."""
    from collections import defaultdict
    def topology(triangles):
        edges=defaultdict(list)
        for a,b,c in triangles:
            for x,y,z in ((a,b,c),(b,c,a),(c,a,b)):
                edges[tuple(sorted((int(x),int(y))))].append((int(x),int(y),int(z)))
        return [value[0] for value in edges.values() if len(value)==1]
    def texture_uv(packet: 'dict[str, Any]',skin: 'dict[str, Any]',slot):
        uv=np.asarray(next(x['values'] for x in packet['uv'] if x['channel']==0)).reshape(-1,4)[:,:2]
        prop=next(x for x in skin['materials'][slot]['properties'] if x['name']=='_MainTex')
        return uv*np.asarray(prop['scale'])+np.asarray(prop['offset'])
    def inside_uv(edge,uv,shape):
        a,b,c=edge;h,w=shape[:2];p=uv[[a,b,c]];pixels=p*np.array([w,h]);line=pixels[1]-pixels[0]
        height=abs(np.cross(line,pixels[2]-pixels[0]))/max(np.linalg.norm(line),1e-20)
        return np.asarray([[((1-t)*p[0]+t*p[1])*(1-min(.2,depth/max(height,1e-20)))+p[2]*min(.2,depth/max(height,1e-20)) for depth in (.5,1.5,2.5)] for t in (.25,.5,.75)])
    fv=np.asarray(first['vertices'],dtype=float).reshape(-1,3);sv=np.asarray(second['vertices'],dtype=float).reshape(-1,3)
    transform,slots,_=art.skin_palette_frame(first,second,first_skin['bones'],second_skin['bones'])
    fb=np.asarray(first['bindPoses']).reshape(-1,4,4).transpose(0,2,1);sb=np.asarray(second['bindPoses']).reshape(-1,4,4).transpose(0,2,1)
    transforms=[np.linalg.inv(sb[donor])@fb[receiver]for donor,receiver in slots.items()];homogeneous=np.c_[fv,np.ones(len(fv))]
    frame_error=max(float(np.linalg.norm((homogeneous@(m-transform).T)[:,:3],axis=1).max()) for m in transforms)
    require(frame_error<tolerance/10,'Source bind frames do not share a consistent rest transform.')
    points=(homogeneous@transform.T)[:,:3]
    first_edges=topology(np.asarray(first['subMeshes'][first_slot]).reshape(-1,3));second_edges=topology(np.asarray(second['subMeshes'][second_slot]).reshape(-1,3))
    require(first_edges and second_edges,'Source surfaces do not expose a shared cut.')
    boundary=np.unique(np.asarray(second_edges,dtype=np.intp)[:,:2]);near=[];distances=[]
    for start in range(0,len(points),256):
        squared=np.sum((points[start:start+256,None]-sv[boundary][None])**2,axis=2);indices=np.argmin(squared,axis=1)
        near.extend(boundary[indices]);distances.extend(np.sqrt(squared[np.arange(len(indices)),indices]))
    near=np.asarray(near);distances=np.asarray(distances);matched=distances<tolerance
    edge_map={frozenset((a,b)):(a,b,c) for a,b,c in second_edges};fu=texture_uv(first,first_skin,first_slot);su=texture_uv(second,second_skin,second_slot)
    pairs=[];contact=set()
    for edge in first_edges:
        a,b,c=edge
        if not (matched[a] and matched[b]):continue
        other=edge_map.get(frozenset((int(near[a]),int(near[b]))))
        if other is None:
            candidates=[]
            for x,y,z in second_edges:
                if np.linalg.norm(sv[x]-points[a])<tolerance and np.linalg.norm(sv[y]-points[b])<tolerance:candidates.append((x,y,z))
                elif np.linalg.norm(sv[y]-points[a])<tolerance and np.linalg.norm(sv[x]-points[b])<tolerance:candidates.append((y,x,z))
            if not candidates:continue
            other=candidates[0]
        if np.linalg.norm(sv[other[0]]-points[a])>np.linalg.norm(sv[other[1]]-points[a]):other=(other[1],other[0],other[2])
        length=float(np.linalg.norm(points[a]-points[b]))
        if length<=np.finfo(float).eps:continue
        pairs.append(dict(firstEdge=list(edge),secondEdge=list(other),firstUv=inside_uv(edge,fu,first_shape).tolist(),secondUv=inside_uv(other,su,second_shape).tolist(),lengthMetres=length))
        contact.update((a,b))
    return dict(pairs=pairs,firstContactVertices=sorted(contact),sharedNativeBindFrames=len({second_skin['bones'][index]for index in slots}),maximumBindFrameDeviationMetres=frame_error,maximumContactGapMetres=float(distances[list(contact)].max()) if contact else None,toleranceMetres=tolerance)


def rgb_junction_gain(first_samples,second_samples,weights):
    """One robust low-frequency RGB correction from an actual surface junction."""
    first=np.asarray(first_samples,dtype=float);second=np.asarray(second_samples,dtype=float);weights=np.asarray(weights,dtype=float)
    require(first.shape==second.shape and first.ndim==2 and first.shape[1]==3 and weights.shape==(len(first),) and len(first)>0, 'Invalid paired RGB junction samples.')
    require(np.all(np.isfinite(first)) and np.all(np.isfinite(second)) and np.all(first>=0) and np.all(second>=0) and np.all(np.isfinite(weights)) and np.all(weights>=0), 'Unsupported or nonfinite RGB junction samples.')
    supported=np.all(first>0,axis=1)&np.all(second>0,axis=1)&(weights>0)
    require(np.sum(weights[supported])>=np.sum(weights)*.5 and np.any(supported), 'The native junction has insufficient non-padding color support.')
    values=np.log(second[supported]/first[supported]);weights=weights[supported];result=[]
    for channel in values.T:
        order=np.argsort(channel);cumulative=np.cumsum(weights[order]);result.append(channel[order[np.searchsorted(cumulative,cumulative[-1]/2)]])
    return np.exp(result)


def rgb_junction_palette(head_groups,body_groups):
    """Balance measured face and body owners, independent of alias/source counts."""
    def centers(groups):
        values=[]
        for group in groups:
            rgb=np.asarray(group['samples'],dtype=float);weights=np.asarray(group['weights'],dtype=float)
            require(rgb.ndim==2 and rgb.shape[1]==3 and weights.shape==(len(rgb),), 'Invalid measured palette group.')
            supported=np.all(np.isfinite(rgb)&(rgb>0),axis=1)&np.isfinite(weights)&(weights>0)
            require(np.any(supported),'A palette owner has no measured surface color.')
            rgb=np.log(rgb[supported]);weights=weights[supported];median=[]
            for channel in rgb.T:
                order=np.argsort(channel);cumulative=np.cumsum(weights[order]);median.append(channel[order[np.searchsorted(cumulative,cumulative[-1]/2)]])
            values.append(median)
        require(values,'A junction palette is missing one material owner category.')
        return np.median(values,axis=0)
    head=centers(head_groups);body=centers(body_groups)
    return np.exp((head+body)*.5),dict(headGroups=len(head_groups),bodyGroups=len(body_groups),headMedianLinearRgb=np.exp(head).tolist(),bodyMedianLinearRgb=np.exp(body).tolist(),categoryWeights=[.5,.5],ownership='equal category weight; each distinct authored/native atlas contributes once')


def surface_junction_band(packet: 'dict[str, Any]',skin: 'dict[str, Any]',slot,contact_vertices,ring_length,protected_vertices=()):
    """A source-scaled surface band with exact contact and neighboring-part limits."""
    import heapq
    positions=np.asarray(packet['vertices'],dtype=float).reshape(-1,3)
    transform=np.asarray(skin['rendererToPrefab']).reshape(4,4).T
    points=positions@transform[:3,:3].T+transform[:3,3]
    triangles=np.asarray(packet['subMeshes'][slot]).reshape(-1,3);graph=[{} for unused in positions]
    for triangle in triangles:
        for first,second in zip(triangle,np.roll(triangle,-1)):
            first,second=int(first),int(second);length=float(np.linalg.norm(points[first]-points[second]))
            graph[first][second]=length;graph[second][first]=length
    contact=np.asarray(contact_vertices,dtype=int);protected=np.asarray(protected_vertices,dtype=int)
    require(contact.size>0 and ring_length>0 and np.isfinite(ring_length),'A junction band requires its actual measured source cut.')
    # Repeated cut vertices keep independent indices/UVs; seeding their same
    # native rest point changes no topology, skin or expression correspondence.
    squared=np.sum((points[:,None]-points[contact][None])**2,axis=2)
    seeds=np.flatnonzero(np.min(squared,axis=1)<1e-12)
    def distance(indices):
        values=np.full(len(points),np.inf);values[indices]=0;queue=[(0.,int(i)) for i in indices];heapq.heapify(queue)
        while queue:
            old,index=heapq.heappop(queue)
            if old!=values[index]:continue
            for other,length in graph[index].items():
                candidate=old+length
                if candidate<values[other]:values[other]=candidate;heapq.heappush(queue,(candidate,other))
        return values
    distances=distance(seeds);radius=ring_length/(2*np.pi)
    field=np.clip(1-distances/radius,0,1);field=field*field*(3-2*field)
    if protected.size:
        outside=distance(protected);finite=np.isfinite(outside)&np.isfinite(distances)&((outside+distances)>0)
        ratio=np.ones(len(field));ratio[finite]=outside[finite]/(outside[finite]+distances[finite]);ratio=ratio*ratio*(3-2*ratio);field*=ratio;field[protected]=0
    field[seeds]=1
    require(np.all(np.isfinite(field)) and np.all((field>=0)&(field<=1)), 'A source junction field is invalid.')
    return field,dict(contactVertices=len(seeds),protectedPartVertices=len(protected),radiusMetres=radius,bandScale='equivalent radius from measured native cut circumference',geometryChanged=False,originalIndexedTopologyPreserved=True)


def refine_rgb_junction(rgba,field,gain):
    """Apply a source-owned neck/shoulder correction while preserving facial pixels and alpha."""
    rgba=np.asarray(rgba,dtype=float);field=np.asarray(field,dtype=float);gain=np.asarray(gain,dtype=float)
    require(rgba.ndim==3 and rgba.shape[2]==4 and field.shape==rgba.shape[:2] and gain.shape==(3,), 'Invalid source RGB junction field.')
    require(np.all(np.isfinite(field)) and np.all((field>=0)&(field<=1)) and np.all(np.isfinite(gain)) and np.all(gain>0), 'Invalid RGB junction ownership or gain.')
    result=rgba.copy();scaled=rgba[:,:,:3]*np.exp(field[:,:,None]*np.log(gain))
    gamut=np.maximum(1,np.max(scaled,axis=2));result[:,:,:3]=scaled/gamut[:,:,None]
    protected=field==0
    require(np.array_equal(result[:,:,3],rgba[:,:,3]) and np.array_equal(result[protected],rgba[protected]), 'RGB junction changed a protected facial pixel or alpha.')
    return result,dict(affectedPixels=int(np.count_nonzero(field)),protectedPixels=int(np.count_nonzero(protected)),gamutLimitedPixels=int(np.count_nonzero(gamut>1)),alphaExact=True,protectedPixelsExact=True,gain=gain.tolist(),operation='native-source junction RGB gain with declared bone/UV support')


def rgb_gaussian(values, sigma):
    """The same finite Gaussian kernel, evaluated in bounded 64-column FFT chunks."""
    if sigma < 0.15:
        return values.copy()
    radius = int(np.ceil(sigma * 3))
    offsets = np.arange(-radius, radius + 1)
    kernel = np.exp(-(offsets / sigma) ** 2 * .5)
    kernel /= kernel.sum()
    out = values.astype(np.float64, copy=True)
    for axis in (0, 1):
        moved = np.moveaxis(out, axis, 0)
        n = moved.shape[0]
        matrix = moved.reshape(n, -1)
        length = 1 << (n + len(kernel) - 2).bit_length()
        frequency = np.fft.rfft(kernel, n=length)[:, None]
        result = np.empty_like(matrix)
        for start in range(0, matrix.shape[1], 64):
            spectrum = np.fft.rfft(matrix[:, start:start+64], n=length, axis=0)
            spectrum *= frequency
            convolution = np.fft.irfft(spectrum, n=length, axis=0)
            result[:, start:start+64] = convolution[radius:radius+n]
        out = np.moveaxis(result.reshape(moved.shape), 0, axis)
    return out

def rgb_physical_average(values, domain, metres_per_texel, sigma_metres, evaluation=None):
    """Interpolate the source area-equivalent UV metric, not a global UV size.

    Local shear/anisotropy is reported by the source adapter; this scalar filter
    uses the actual surface-area-equivalent radius, not a geodesic blur claim.
    Only declared continuous skin participates in each normalized convolution.
    """
    desired = sigma_metres / np.maximum(metres_per_texel, np.finfo(float).tiny)
    if evaluation is None:
        evaluation = domain > 0
    levels = [0., .5, 1., 2., 4., 8., 16.]
    maximum = float(np.max(desired[evaluation]))
    require(maximum <= 128, 'RGB physical radius exceeds prepared image support.')
    while levels[-1] < maximum:
        levels.append(levels[-1]*2)
    output = np.zeros_like(values, dtype=np.float64)
    lower = np.zeros_like(values, dtype=np.float64)
    previous_sigma = 0.
    numerator = values * domain
    denominator = domain.copy()
    for index, sigma in enumerate(levels):
        increment = np.sqrt(max(0., sigma * sigma - previous_sigma * previous_sigma))
        numerator = rgb_gaussian(numerator, increment)
        denominator = rgb_gaussian(denominator, increment)
        current = np.divide(numerator, denominator, out=np.zeros_like(numerator), where=denominator > 1e-14)
        if index:
            before = levels[index-1]
            selected = (desired >= before) & (desired <= sigma) & evaluation
            # Interpolate variance: Gaussian scale is a physical variance.
            blend = np.clip((desired * desired - before * before) / (sigma * sigma - before * before), 0, 1)
            output[selected] = (lower * (1-blend) + current * blend)[selected]
        lower = current
        previous_sigma = sigma
        if sigma >= maximum:
            break
    return output

def refine_rgb_structure(current, ownership, continuous_skin, protected_features, metres_per_texel, settings):
    """One source-owned RGB operation. Alpha and the other material channels are inputs only.

    Keep chromaticity, coarse pigment distribution, the finest retained detail,
    directed structures and explicit pigment/anatomy exclusions. Remove only
    the isotropic luminance band between the two declared physical scales.
    This is a bounded artist operation, not a semantic freckle/noise classifier.
    """
    require(current.ndim == 3 and current.shape[2] == 4, 'RGB surface requires current RGBA.')
    shape = current.shape[:2]
    require(all(x.shape == shape for x in (ownership, continuous_skin, protected_features, metres_per_texel)), 'RGB ownership/metric shapes differ.')
    require(np.isfinite(current).all() and np.all((current >= 0) & (current <= 1)), 'Invalid current skin RGBA.')
    require(all(np.isfinite(x).all() for x in (ownership, continuous_skin, protected_features, metres_per_texel)), 'Nonfinite RGB authoring ownership.')
    require(np.all(metres_per_texel[ownership > 0] > 0), 'RGB source has no positive physical UV metric.')
    require(set(settings) == {'fineSigmaMetres', 'grainSigmaMetres', 'coherenceSigmaMetres', 'featherMetres'}, 'Unknown RGB frequency declaration.')
    require(0 < settings['fineSigmaMetres'] < settings['grainSigmaMetres'] <= settings['coherenceSigmaMetres'] < settings['featherMetres'], 'RGB frequency scales are not ordered physical distances.')
    require(all(type(v) in (int, float) and np.isfinite(v) for v in settings.values()), 'RGB frequency scales must be finite numbers.')
    require(all(np.all((x >= 0) & (x <= 1)) for x in (ownership, continuous_skin, protected_features)), 'RGB masks exceed their source domain.')
    eligible = ownership * continuous_skin * (protected_features == 0)
    output = current.copy()
    if not np.any(eligible):
        return output, dict(operation='preserve-source-pattern', affectedPixels=0)
    # Explicit features stay exact and are excluded from the averaging support.
    domain = ((ownership > 0) & (continuous_skin > 0) & (protected_features == 0)).astype(np.float64)
    feathered = rgb_physical_average(domain, np.ones(shape), metres_per_texel, settings['featherMetres']/3, evaluation=domain > 0)
    eligible *= np.clip(feathered, 0, 1)
    rgb = current[:, :, :3]
    luma = np.einsum('...c,c->...', rgb, np.array([.2126, .7152, .0722]))
    log_luma = np.log(np.maximum(luma, 1/65535))
    fine = rgb_physical_average(log_luma, domain, metres_per_texel, settings['fineSigmaMetres'])
    coarse = rgb_physical_average(log_luma, domain, metres_per_texel, settings['grainSigmaMetres'])
    dy, dx = np.gradient(fine)
    jxx = rgb_physical_average(dx*dx, domain, metres_per_texel, settings['coherenceSigmaMetres'])
    jyy = rgb_physical_average(dy*dy, domain, metres_per_texel, settings['coherenceSigmaMetres'])
    jxy = rgb_physical_average(dx*dy, domain, metres_per_texel, settings['coherenceSigmaMetres'])
    energy = jxx+jyy
    coherence = np.divide(np.sqrt(np.maximum(0,(jxx-jyy)**2+4*jxy*jxy)), energy,
                          out=np.ones_like(energy), where=energy > 1e-16)
    coherence = np.clip(coherence, 0, 1)
    band = fine-coarse
    gain_delta = np.expm1(-band*(1-coherence)) * eligible
    # Project out the three regional color sums using a scalar gain only.
    # This preserves source chromaticity and the mean tint, rather than adding
    # a gray/white target or replacing native pigmentation with a flat value.
    samples = rgb[eligible > 0]
    weights = eligible[eligible > 0]
    gram = samples.T @ (samples*weights[:,None])
    rhs = np.sum(samples*gain_delta[eligible > 0,None], axis=0)
    correction = np.linalg.pinv(gram, rcond=1e-10) @ rhs
    gain_delta -= eligible*np.einsum('...c,c->...', rgb, correction)
    # Limit a scalar gain at LDR gamut boundaries, preserving source RGB ratios.
    # The color-sum projection above is retained; gamut limits take priority at extrema.
    upper = np.nextafter(np.divide(1., np.max(rgb, axis=2), out=np.full(shape, np.inf), where=np.max(rgb, axis=2)>0), 0.)
    limited = np.minimum(np.maximum(1+gain_delta, 0.), upper)
    gamut_pixels = int(np.count_nonzero(limited != 1+gain_delta))
    changed = rgb*limited[:,:,None]
    require(np.isfinite(changed).all() and np.min(changed) >= 0 and np.max(changed) <= 1, 'RGB scalar gain produced an invalid LDR color.')
    output[:,:,:3] = changed
    # Avoid floating round-trip changes outside the explicit authoring domain.
    output[eligible == 0] = current[eligible == 0]
    require(np.array_equal(output[:,:,3], current[:,:,3]), 'RGB operation changed current smoothness alpha.')
    require(np.array_equal(output[protected_features > 0], current[protected_features > 0]), 'RGB operation changed declared pigment/anatomy.')
    return output, dict(operation='source-owned physical luminance band with coherence retention',
                        affectedPixels=int(np.count_nonzero(np.any(output[:,:,:3] != rgb, axis=2))),
                        ownedPixels=int(np.count_nonzero(eligible)),
                        sourceLinearRgbMean=rgb[eligible > 0].mean(axis=0).tolist(),
                        outputLinearRgbMean=changed[eligible > 0].mean(axis=0).tolist(),
                        coherenceQuantiles=np.quantile(coherence[eligible > 0], [.1,.5,.9]).tolist(),
                        removedLogLumaBandRms=float(np.sqrt(np.mean((band*(1-coherence)*eligible)**2))),
                        gamutLimitedPixels=gamut_pixels,alphaExact=True, protectedFeaturesExact=True,
                        localChromaticity='single multiplicative luminance gain; no palette/color target',
                        physicalMetric='source triangle area-equivalent metres per texel; anisotropy reported, no geodesic claim')


def material_surface(packet: 'dict[str, Any]', skin: 'dict[str, Any]', baked, region: 'np.ndarray', root, textures, slots=None):
    maps = []
    for slot, material in enumerate(skin['materials']):
        if slots is not None and slot not in slots:
            continue
        if not material:
            continue
        channels = material_channels(material)
        if channels['status'] != 'supported':
            continue
        changes = []
        for prop in material['properties']:
            if not prop.get('textureFile') or prop['name'] != channels['normal']:
                continue
            original_image, data = image_pixels(prop['textureFile'])
            source_fingerprint = texture_fingerprint(data)
            height, width = data.shape[:2]
            size = (height, width)
            mask = resize(region[:, :, None], size)[:, :, 0]
            identity = packet['sourceFingerprint'] + ':' + str(slot) + ':' + prop['name'] + ':' + source_fingerprint
            role = 'SkinNormal'
            name = resource_naming.resource_names([resource_naming.ResourceName(identity, skin['path'], suffix='Material' + str(slot) + '_' + role)])[identity]
            destination = root / 'Textures' / (name + '.png')
            resource_naming.assert_path_budget(destination.resolve(), 259)
            normal = np.dstack((data[:, :, 0] * data[:, :, 3] * 2 - 1,
                                data[:, :, 1] * 2 - 1, np.zeros(mask.shape, dtype=np.float32)))
            normal[:, :, 2] = np.sqrt(np.maximum(0, 1 - np.sum(normal[:, :, :2] ** 2, axis=2)))
            softened = normalise(blur(normal))
            native = normalise(normal + (softened - normal) * (mask * .38)[:, :, None])
            sculpted = resize(baked, size)[:, :, :3] * 2 - 1
            forward = np.array([0., 0., 1.], dtype=np.float32)
            detail = normalise(forward + (sculpted - forward) * mask[:, :, None])
            tangent = normalise(rotate_between(detail.reshape(-1, 3),
                np.tile(forward, (height * width, 1)), native.reshape(-1, 3))).reshape(height, width, 3)
            output = np.dstack((tangent * .5 + .5, np.ones(mask.shape, dtype=np.float32)))
            write_image(destination, output)
            textures.append(dict(name=name, path='Textures/' + destination.name, sha256=art.hash_file(destination),
                                 normalMap=True, sRGB=False, mipmaps=True))
            changes.append(dict(name=prop['name'], kind='texture-remap', sourceTextureFingerprint=source_fingerprint, textureAsset=name))
        if changes:
            maps.append(dict(sourceFingerprint=packet['sourceFingerprint'], materialSlot=slot,
                             sourceShader=material['shader'], properties=changes))
    return maps


def ocular_materials(packet: 'dict[str, Any]', skin: 'dict[str, Any]'):
    maps = []
    for slot, material in enumerate(skin['materials']):
        if not material or material['shader'] != 'Custom/CharacterEye':
            continue
        properties = {p['name']: p for p in material['properties']}
        smoothness = properties.get('_Smoothness')
        if smoothness and smoothness['type'] in ('Float', 'Range'):
            maps.append(dict(sourceFingerprint=packet['sourceFingerprint'], materialSlot=slot,
                sourceShader=material['shader'], properties=[dict(name='_Smoothness', kind='float',
                    value=float(np.clip(smoothness['value'] + .12, 0, .9)))]))
    return maps


def material_identity(material):
    return json.dumps({key: value for key, value in material.items() if key != 'name'},
                      sort_keys=True, separators=(',', ':'))


def review_surface_material(spec, changed, directory):
    result = art.material(spec, False)
    nodes = result.node_tree.nodes; links = result.node_tree.links
    bsdf = nodes.get('Principled BSDF')
    properties = {p['name']: p for p in spec['properties']}
    changed = {p['name']: p for p in changed}
    if spec['shader'] == 'Custom/CharacterEye':
        smoothness = changed.get('_Smoothness', properties.get('_Smoothness', {'value': .7}))['value']
        bsdf.inputs['Roughness'].default_value = 1 - smoothness
        bsdf.inputs['Coat Weight'].default_value = .3
        bsdf.inputs['Coat Roughness'].default_value = .08
    base = properties.get('_MainTex')
    if base and base.get('textureFile'):
        override = changed.get('_MainTex')
        path = directory / 'Textures' / (override['textureAsset'] + '.png') if override else Path(base['textureFile'])
        node = art.image_node(result, path, override['textureAsset'] if override else None)
        if override: node.image.colorspace_settings.name = 'sRGB'
        links.new(node.outputs['Color'], bsdf.inputs['Base Color'])
        if '/Skin' in spec['shader']:
            roughness = nodes.new('ShaderNodeMath'); roughness.operation = 'SUBTRACT'; roughness.inputs[0].default_value = 1
            links.new(node.outputs['Alpha'], roughness.inputs[1]); links.new(roughness.outputs[0], bsdf.inputs['Roughness'])
            specular = properties.get('_SpecTex')
            if specular and specular.get('textureFile'):
                spec_node = art.image_node(result, Path(specular['textureFile']))
                split = nodes.new('ShaderNodeSeparateColor'); links.new(spec_node.outputs['Color'], split.inputs['Color'])
                strength = nodes.new('ShaderNodeMath'); strength.operation = 'MULTIPLY'; strength.inputs[1].default_value = .08
                links.new(split.outputs['Green'], strength.inputs[0]); links.new(strength.outputs[0], bsdf.inputs['Subsurface Weight'])
                ao = nodes.new('ShaderNodeMixRGB'); ao.blend_type = 'MULTIPLY'; ao.inputs[0].default_value = 1
                links.new(node.outputs['Color'], ao.inputs[1]); links.new(spec_node.outputs['Alpha'], ao.inputs[2])
                links.new(ao.outputs[0], bsdf.inputs['Base Color'])
        elif spec['shader'] in ('Standard', 'Standard (Double Sided)'):
            bsdf.inputs['Roughness'].default_value = 1 - properties.get('_Glossiness', {}).get('value', .5)
    bump = changed.get('_BumpMap')
    if bump:
        tex = art.image_node(result, directory / 'Textures' / (bump['textureAsset'] + '.png'), bump['textureAsset'])
        normal = nodes.new('ShaderNodeNormalMap'); links.new(tex.outputs['Color'], normal.inputs['Color'])
        normal.inputs['Strength'].default_value = changed.get('_BumpMapScale', properties.get('_BumpMapScale', {'value': 1.}))['value']
        links.new(normal.outputs[0], bsdf.inputs['Normal'])
    return result


def packet_from_recipe(mesh):
    packet: dict[str, Any] = dict(name=mesh['name'], vertexCount=len(mesh['vertices']) // 3,
                  vertices=mesh['vertices'], normals=mesh['normals'], tangents=mesh['tangents'], colors=mesh['colors'],
                  weights=mesh['skin']['boneWeights'], boneIndices=mesh['skin']['boneIndices'],
                  bindPoses=mesh['skin']['bindPoses'], subMeshes=[s['indices'] for s in mesh['subMeshes']],
                  uv=[], shapes=mesh['blendShapes'], indexFormat=int(mesh['indexFormat'] == 'UInt32'),
                  bounds=mesh['bounds']['center'] + mesh['bounds']['size'])
    for uv in mesh['uvChannels']:
        data = np.zeros((packet['vertexCount'], 4)); data[:, :uv['dimension']] = np.asarray(uv['values']).reshape(-1, uv['dimension'])
        packet['uv'].append(dict(channel=uv['channel'], dimension=uv['dimension'], values=data.ravel().tolist()))
    return packet


def authored_recipe_meshes(directory):
    recipe = art.read_json(directory / 'recipe.json'); declarations = art.read_json(directory / 'declarations.json')
    meshes = {m['name']: m for m in recipe['meshes']}
    return {d['sourceFingerprint']: packet_from_recipe(meshes[d['authoredMeshAsset']])
            for d in declarations['meshes'] if d['mode'] == 'authored'}


def authored_recipe_index(root, assets):
    """Keep only source -> disk owner/hash, never every authored packet array."""
    result = {}
    for asset in assets:
        directory = root / 'Assets' / asset_key(asset)
        packets = authored_recipe_meshes(directory)
        for source, packet in packets.items():
            signature = hashlib.sha256(json.dumps(packet, sort_keys=True, separators=(',', ':'), allow_nan=False).encode()).hexdigest()
            require(source not in result or result[source]['signature'] == signature, 'Saved shared source review geometry differs.')
            if source not in result:
                result[source] = dict(directory=directory, signature=signature)
        packet = None
        packets.clear()
    return result


def authored_asset_closure(asset, index):
    """Resolve only this asset's source closure, including shared recipe owners."""
    required = {skin['sourceFingerprint'] for skin in asset['skins']}
    result = {}
    directories = {index[source]['directory'] for source in required if source in index}
    for directory in sorted(directories):
        packets = authored_recipe_meshes(directory)
        for source in required.intersection(packets):
            packet = packets[source]
            signature = hashlib.sha256(json.dumps(packet, sort_keys=True, separators=(',', ':'), allow_nan=False).encode()).hexdigest()
            require(source in index and signature == index[source]['signature'], 'Saved authored source changed after review indexing.')
            result[source] = packet
        packet = None
        packets.clear()
    require(set(result) == required.intersection(index), 'An authored source dependency is missing from this asset closure.')
    return result


def captured_neutral_pose(asset: 'dict[str, Any]', capture: 'dict[str, Any]'):
    """Bind a measured game neutral to its complete native renderer palettes."""
    require(capture.get('name') == 'Neutral' and capture.get('headSource') == asset['address']
            and capture.get('coordinateDomain') == 'runtime-part-instance-metres',
            'The captured neutral belongs to another native head or coordinate domain.')
    skins = {skin['path']: skin for skin in asset['skins'] if lod_index(skin) == 0}
    renderers = capture.get('renderers', [])
    require(len(renderers) == len(skins) and {row['renderer'] for row in renderers} == set(skins),
            'The captured neutral must own every native LOD0 renderer exactly once.')
    for row in renderers:
        skin = skins[row['renderer']]
        require(row['vertexCount'] == skin['vertexCount'] and row['bonePathsOrdered'] == skin['bones'],
                'The captured neutral renderer topology or ordered bone palette differs.')
        require(all(type(value['weight']) in (int, float) and value['weight'] == 0
                    for value in row['morphs']), 'A sculpt reference requires zero native morph weights.')
    matrices = capture.get('worldMatrices', {})
    for skin in asset['skins']:
        require(all(bone in matrices for bone in skin['bones']), 'The captured neutral is missing a native renderer bone.')
    for values in matrices.values():
        matrix = np.asarray(values, dtype=np.float64)
        require(matrix.shape == (16,) and np.isfinite(matrix).all()
                and np.allclose(matrix.reshape(4, 4)[:, 3], [0., 0., 0., 1.], rtol=0, atol=1e-6),
                'A captured neutral bone matrix must be a finite native affine transform.')
    return dict(worldMatrices=copy.deepcopy(matrices), currentMorphBySource=[], allNativeMorphWeightsZero=True)


def reference_skinning_matrices(packet: 'dict[str, Any]', skin: 'dict[str, Any]', pose):
    """One native LBS transform per vertex, in the prefab's own reference rig."""
    matrices = pose['worldMatrices']
    require(all(bone in matrices for bone in skin['bones']), 'Frozen native pose is missing a renderer bone.')
    bone_world = np.stack([art.matrix(matrices[bone]) for bone in skin['bones']])
    bindposes = np.asarray(packet['bindPoses']).reshape(-1, 4, 4).transpose(0, 2, 1)
    matrices = bone_world @ bindposes
    indices = np.asarray(packet['boneIndices']).reshape(-1, 4)
    weights = np.asarray(packet['weights']).reshape(-1, 4)
    selected = np.sum(matrices[indices] * weights[:, :, None, None], axis=1)
    require(np.isfinite(selected).all(), 'Native reference skinning contains nonfinite transforms.')
    return selected


def reference_pose_surface_to_rest(packet: 'dict[str, Any]', skin: 'dict[str, Any]', pose,
                                   active_vertices, target_positions):
    """Convert a sculpted neutral DCC surface to source-rest guide coordinates."""
    require(pose['allNativeMorphWeightsZero'] and not any(
        value['weight'] != 0 for row in pose.get('currentMorphBySource', []) for value in row['weights']),
        'A source-rest sculpt target requires a zero-morph native reference pose.')
    ids = np.asarray(active_vertices, dtype=np.int64)
    target = np.asarray(target_positions, dtype=np.float64)
    require(ids.ndim == 1 and len(ids) == len(np.unique(ids)) and np.all((ids >= 0) & (ids < packet['vertexCount']))
            and target.shape == (len(ids), 3) and np.isfinite(target).all(), 'Invalid neutral sculpt target ownership.')
    selected = reference_skinning_matrices(packet, skin, pose)[ids]
    linear = selected[:, :3, :3]
    require(np.all(np.abs(np.linalg.det(linear)) > 1e-12), 'The neutral sculpt has a singular source skin transform.')
    neutral = art.points(target.ravel().tolist(), np.linalg.inv(art.CONVERT))
    local = np.linalg.solve(linear, (neutral - selected[:, :3, 3])[:, :, None])[:, :, 0]
    restored = np.einsum('vij,vj->vi', linear, local) + selected[:, :3, 3]
    require(np.allclose(restored, neutral, rtol=0, atol=1e-9), 'Neutral sculpt inverse skinning lost its target.')
    return art.points(local.ravel().tolist(), art.CONVERT @ art.matrix(skin['rendererToPrefab']))


def apply_native_reference_pose(packet: 'dict[str, Any]', skin: 'dict[str, Any]', pose):
    """DCC articulation only: compiled guides retain source-rest coordinates."""
    selected = reference_skinning_matrices(packet, skin, pose)
    original = np.asarray(packet['vertices'], dtype=np.float32).astype(np.float64).reshape(-1, 3).copy()
    source_normals = np.asarray(packet['normals'], dtype=np.float32).astype(np.float64).reshape(-1, 3).copy()
    source_tangent = np.asarray(packet['tangents'], dtype=np.float32).astype(np.float64).reshape(-1, 4).copy() if packet['tangents'] else None
    current = [row for row in pose.get('currentMorphBySource', []) if row['sourceFingerprint'] == skin['sourceFingerprint'] and row['rendererPath'] == skin['path']]
    require(len(current) == 1 or (not current and pose['allNativeMorphWeightsZero']), 'Measured native physical morph owner is absent or ambiguous.')
    if current:
        for value in current[0]['weights']:
            if value['weight'] == 0: continue
            shape = [s for s in packet['shapes'] if s['name'] == value['name']]
            require(len(shape) == 1, 'Measured native physical channel is absent.')
            frame = [f for f in shape[0]['frames'] if f['weight'] == value['weight']]
            require(len(frame) == 1, 'This captured native pose requires an exact physical frame knot; unmeasured interpolation is unsupported.')
            original += np.asarray(frame[0]['vertices'], dtype=np.float32).astype(np.float64).reshape(-1, 3)
            source_normals += np.asarray(frame[0]['normals'], dtype=np.float32).astype(np.float64).reshape(-1, 3)
            if source_tangent is not None: source_tangent[:, :3] += np.asarray(frame[0]['tangents'], dtype=np.float32).astype(np.float64).reshape(-1, 3)
    vertices = np.einsum('vij,vj->vi', selected[:, :3, :3], original) + selected[:, :3, 3]
    normals = normalise(np.einsum('vij,vj->vi', selected[:, :3, :3], source_normals))
    result = dict(packet, vertices=vertices.ravel().tolist(), normals=normals.ravel().tolist())
    if source_tangent is not None:
        tangent = source_tangent
        xyz = np.einsum('vij,vj->vi', selected[:, :3, :3], tangent[:, :3])
        xyz = normalise(xyz - np.sum(xyz*normals, axis=1)[:, None]*normals)
        result['tangents'] = np.column_stack((xyz, tangent[:, 3])).ravel().tolist()
    return result


def native_head_vertices(packet: 'dict[str, Any]', skin: 'dict[str, Any]', head_root):
    """Native skinning ownership, independent of authoring mask or material names."""
    require(type(head_root) is str and head_root.rsplit('/', 1)[-1] == 'Bip001 Head', 'A declared native head bone owner is required; a root/body fallback is forbidden.')
    indices = np.asarray(packet['boneIndices'], dtype=np.int64).reshape(-1, 4)
    weights = np.asarray(packet['weights'], dtype=np.float64).reshape(-1, 4)
    require(indices.shape == weights.shape == (packet['vertexCount'], 4) and np.all(np.isfinite(weights)) and np.all(weights >= 0) and np.all((indices >= 0) & (indices < len(skin['bones']))), 'Invalid native head influence mapping.')
    owned = np.asarray([bool(bone and (bone == head_root or bone.startswith(head_root + '/'))) for bone in skin['bones']], dtype=bool)
    return np.flatnonzero(np.sum(weights * owned[indices], axis=1) > 0).tolist()


def source_face_scope(asset, native_poses=None, framing=None):
    """Presentation uses declared native head ownership, never the sculpt mask."""
    if framing is None: raise ValueError('A source presentation document is required.')
    document = framing
    if not (type(document) is dict and set(document)=={'schemaVersion','assets'} and document['schemaVersion']==1): raise ValueError('Invalid source presentation schema.')
    matches = [r for r in document['assets'] if r['sourceContentKey']==asset['sourceContentKey'] and r['internalId']==asset['internalId']]
    if not (len(matches)==1): raise ValueError('Missing or ambiguous source-owned head presentation. No edited-region or whole-body fallback.')
    declaration = matches[0]
    if not ({'sourceContentKey', 'internalId', 'headOwner', 'sources', 'camera', 'scope'}.issubset(declaration)): raise ValueError('Native head presentation is missing its source closure or camera declaration.')
    if not (type(declaration['camera']) is dict and type(declaration['scope']) is str and declaration['scope']): raise ValueError('Invalid native head camera or presentation scope.')
    sources=[]; coordinates=[]; presentations=[]
    owner = declaration.get('headOwner')
    if not (type(owner) is dict and set(owner) == {'path', 'sourceFingerprint', 'bonePath'}): raise ValueError('Missing native head owner classification.')
    primary = [skin for skin in asset['skins'] if skin['path'] == owner['path'] and skin['sourceFingerprint'] == owner['sourceFingerprint'] and lod_index(skin) == 0]
    if not (len(primary) == 1 and owner['bonePath'] in primary[0]['bones']): raise ValueError('Native head owner is not in the primary source bone mapping.')
    native_selection = {}
    for skin in asset['skins']:
        if lod_index(skin) != 0: continue
        packet = native_packet(skin)
        selected = native_head_vertices(packet, skin, owner['bonePath'])
        if selected:
            native_selection[(skin['path'], skin['sourceFingerprint'])] = (selected, 'head-attachment' if len(selected) == packet['vertexCount'] else 'head-region')
    declared = [(row['path'], row['sourceFingerprint']) for row in declaration['sources']]
    if not (len(set(declared)) == len(declared) and set(declared) == set(native_selection)): raise ValueError('Native head/attachment closure is incomplete or contains an unrelated body source.')
    for row in declaration['sources']:
        matches = [s for s in asset['skins'] if s['path']==row['path'] and s['sourceFingerprint']==row['sourceFingerprint'] and lod_index(s)==0]
        if not (len(matches)==1): raise ValueError('Presentation source differs from the actual native face content.')
        skin=matches[0];packet=native_packet(skin);indices=row['selectedIndices']
        expected_indices, expected_kind = native_selection[(skin['path'], skin['sourceFingerprint'])]
        if not (row.get('nativeKind') == expected_kind and indices == expected_indices): raise ValueError('Native head framing crosses root/body ownership or omits part of the classified native head.')
        if not (packet['vertexCount']==row['vertexCount'] and type(indices) is list and len(indices)>=3 and len(indices)==len(set(indices)) and all(type(i) is int and 0<=i<packet['vertexCount'] for i in indices)): raise ValueError('Invalid native full-head presentation indices.')
        presentation=dict(path=skin['path'],sourceFingerprint=packet['sourceFingerprint'],role=role(skin))
        skin['_sourcePresentation']=presentation;presentations.append(presentation)
        native=art.points(packet['vertices'],art.CONVERT@art.matrix(skin['rendererToPrefab']))
        coordinates.append(native[indices])
        for pose in native_poses or []:
            if pose is not None:
                posed=apply_native_reference_pose(packet,skin,pose)
                coordinates.append(art.points(posed['vertices'],art.CONVERT)[indices])
        sources.append(dict(path=skin['path'],sourceFingerprint=packet['sourceFingerprint'],role=role(skin),sourceVertices=packet['vertexCount'],selectedVertices=len(indices),selectedIndices=indices,rendererToPrefab=skin['rendererToPrefab'],presentationOwnership=expected_kind))
    if not (sources and coordinates and any(s['role']=='skin' for s in sources)): raise ValueError('No source-owned full native head is declared.')
    return dict(state='SOURCE_OWNED_FACE',coordinateDomain='fixed-union-native-rest-neutral-and-real-expression-poses',sourceContentKey=asset['sourceContentKey'],internalId=asset['internalId'],nativeCoordinates=np.concatenate(coordinates).tolist(),sources=sources,presentations=presentations,camera=declaration['camera'],presentationScope=declaration['scope'])


def review_asset(asset, directory, authored_sources=None, destination=None, face_scope=None, native_pose=None, framing=None, preserved_material_maps=None, baseline_sources=None, baseline_label=None, candidate_label=None):
    if face_scope is None:
        face_scope = source_face_scope(asset, framing=framing)
    destination = directory if destination is None else destination
    declarations = art.read_json(directory / 'declarations.json')
    authored: dict[str, dict[str, Any]] = authored_recipe_meshes(directory) if authored_sources is None else dict(authored_sources)
    baseline: dict[str, dict[str, Any]] = {} if baseline_sources is None else dict(baseline_sources)
    material_maps = {(m['sourceFingerprint'], m['materialSlot']): m['properties'] for m in declarations['materials']}
    preserved={(m['sourceFingerprint'],m['materialSlot']):m['properties'] for m in preserved_material_maps or []}
    original_make_object = art.make_object
    original_material = art.material
    def make_object(_, skin: 'dict[str, Any]', improved):
        packet = native_packet(skin); source_fingerprint = packet['sourceFingerprint']
        current=baseline.get(source_fingerprint,packet)
        target_packet = authored.get(source_fingerprint,current) if improved else current
        if native_pose is not None:
            target_packet = apply_native_reference_pose(target_packet, skin, native_pose)
        obj = mesh_object(target_packet, skin['path'])
        obj.matrix_world = Matrix((art.CONVERT if native_pose is not None else art.CONVERT @ art.matrix(skin['rendererToPrefab'])).tolist())
        for slot, spec in enumerate(skin['materials']):
            if spec:
                key=(source_fingerprint,slot)
                values={p['name']:p for p in preserved.get(key,[])}
                if improved:values.update({p['name']:p for p in material_maps.get(key,[])})
                overrides=list(values.values())
                obj.data.materials.append(review_surface_material(spec, overrides, directory))
        material_ids = np.concatenate([np.full(len(s) // 3, i) for i, s in enumerate(packet['subMeshes'])])
        if improved and source_fingerprint in authored:
            material_ids = np.concatenate([np.full(len(s) // 3, i) for i, s in enumerate(target_packet['subMeshes'])])
        obj.data.polygons.foreach_set('material_index', material_ids)
        presentation = skin.get('_sourcePresentation')
        if not (presentation is not None and presentation['sourceFingerprint'] == source_fingerprint): raise ValueError('Preview source presentation differs.')
        obj['role'] = presentation['role']
        return obj, dict(sourceFingerprint=source_fingerprint, authored=improved and source_fingerprint in authored)
    art.make_object = make_object
    try:
        art.render_asset(asset, destination / 'previews', False, face_scope,baseline_label)
        art.render_asset(asset, destination / 'previews', True, face_scope,candidate_label)
    finally:
        art.make_object = original_make_object; art.material = original_material





def humanoid_face_contact_regions(packet: 'dict[str, Any]',skin: 'dict[str, Any]'):
    """Source LOD0 eyelid domains for LODs without facial animation bones."""
    positions=art.points(packet['vertices'],art.CONVERT@art.matrix(skin['rendererToPrefab']))
    indices=np.asarray(packet['boneIndices'],dtype=int).reshape(packet['vertexCount'],-1)
    weights=np.asarray(packet['weights']).reshape(packet['vertexCount'],-1)
    regions=[]
    for suffix in ('_l','_r'):
        bones=np.array([bool(name) and name.rsplit('/',1)[-1].lower().startswith('b_eyelid_') and
            name.lower().endswith(suffix) for name in skin['bones']])
        selected=np.any(bones[indices] & (weights>0),axis=1)
        if np.any(selected):
            values=positions[selected]
            regions.append(dict(minimum=values.min(axis=0).tolist(),maximum=values.max(axis=0).tolist()))
    return regions


def humanoid_face_contact_guard(packet: 'dict[str, Any]', skin: 'dict[str, Any]', regions=(), *,
                                contact_bone_prefixes=('b_eye_','b_eyelid_','b_lip_')):
    """Preserve the articulated ocular unit and source oral contact rims."""
    count=packet['vertexCount']
    if not any(material and material['shader'].startswith('Lux/Human/Skin') for material in skin['materials']):
        return np.zeros(count)
    if any(token in skin['mesh'].lower() for token in ('eyelash','eyeoccluder','eye_occluder')) or (
            any(material and material['shader']=='Custom/CharacterEye' for material in skin['materials']) and
            not any(material and material['shader'].startswith('Lux/Human/Skin') for material in skin['materials'])):
        return np.zeros(count)
    eye_bones=np.array([bool(name) and name.rsplit('/',1)[-1].lower().startswith(contact_bone_prefixes)
        for name in skin['bones']])
    _,groups=np.unique(np.asarray(packet['vertices']).reshape(-1,3),axis=0,return_inverse=True)
    surface=np.concatenate([np.asarray(t).reshape(-1,3)for t in packet['subMeshes']])
    edges=np.sort(np.concatenate([groups[surface[:,pair]]for pair in ([0,1],[1,2],[2,0])]),axis=1)
    edges=edges[edges[:,0]!=edges[:,1]]
    physical,counts=np.unique(edges,axis=0,return_counts=True)
    protected=np.zeros(count,dtype=bool)
    if len(eye_bones) and np.any(eye_bones) and packet['weights']:
        indices=np.asarray(packet['boneIndices'],dtype=int).reshape(count,-1)
        weights=np.asarray(packet['weights']).reshape(count,-1)
        contact_influence=np.sum(np.where(eye_bones[indices],weights,0),axis=1)
        # An eyelid can be a closed surface: open-edge detection then misses
        # its contact tissue. Keep every vertex driven by an ocular bone with
        # its eye, lash and occluder consumers. Oral sculpting retains its
        # separate rim constraint.
        ocular_bones=np.array([bool(name) and name.rsplit('/',1)[-1].lower().startswith(('b_eye_','b_eyelid_'))
            for name in skin['bones']])&eye_bones
        ocular_influence=np.sum(np.where(ocular_bones[indices],weights,0),axis=1)
        boundary=np.isin(groups,np.unique(physical[counts==1]))
        oral=contact_influence-ocular_influence
        oral_rim=boundary&(oral>0)
        if not oral_rim.any():oral_rim=oral>=.85
        protected=(ocular_influence>0)|oral_rim
    elif regions:
        positions=art.points(packet['vertices'],art.CONVERT@art.matrix(skin['rendererToPrefab']))
        for region in regions:
            protected|=np.all((positions>=np.asarray(region['minimum'])-.002)&
                (positions<=np.asarray(region['maximum'])+.002),axis=1)
    positions=np.asarray(packet['vertices']).reshape(-1,3)
    _,_,marks=art.landmarks(packet,skin,np.eye(4))
    radius=float(np.linalg.norm(marks['left']-marks['right']))*.20 if marks['left'] is not None and marks['right'] is not None else .012
    contact=np.unique(positions[protected],axis=0)
    if not len(contact):return np.ones(count)
    # Physical distance gives coincident vertices the same fade and avoids
    # triangulation-dependent jumps across narrow eyelid triangles.
    distances=np.concatenate([np.linalg.norm(chunk[:,None]-contact[None],axis=2).min(axis=1)
        for chunk in np.array_split(positions,max(1,count//512))])
    return art.smooth(distances/radius)




def refine_native_face_contours(native: 'dict[str, Any]', authored: 'dict[str, Any]', skin: 'dict[str, Any]', marks: 'dict[str, Any] | None'=None, collar_vertices=(), *, refined=None, guide_report=None):
    """Compose an owned face target with the assembled collar and its poses."""
    transform = art.CONVERT @ art.matrix(skin['rendererToPrefab'])
    if marks is None:
        _, _, marks = art.landmarks(native, skin, transform)
        marks['eyeContactRegions'] = humanoid_face_contact_regions(native, skin)
    require(all(marks[k] is not None for k in ('left', 'right', 'mouth', 'nose', 'head')),
            'Native facial contours require their own anatomical landmarks.')
    if refined is None:
        refined = copy.deepcopy(native)
        details: dict[str, Any] = dict(operation="native-neutral-recovery", artisticAcceptance="not-reviewed")
    else:
        for key in ('vertexCount','uv','weights','boneIndices','bindPoses','subMeshes','indexFormat'):
            require(refined[key] == native[key], 'A face target changes native correspondence: ' + key)
        details = dict(guide_report or {}, operation="source-guide-collar-composition", artisticAcceptance="not-reviewed")
    count = native['vertexCount']
    require(count <= authored['vertexCount'], 'The assembled face lost its native vertex prefix.')
    points = art.points(native['vertices'], transform)
    height = abs(((marks['left'] + marks['right']) * .5)[2] - marks['mouth'][2])
    weight = art.smooth((points[:, 2] - marks['mouth'][2] + height * 1.35) / (height * .65))
    collar = np.asarray(sorted(set(collar_vertices)), dtype=np.int64)
    require(np.all((collar >= 0) & (collar < authored['vertexCount'])), 'A collar constraint is outside its assembled face.')
    if len(collar):
        protected = np.zeros(authored['vertexCount'], dtype=bool); protected[collar] = True
        face_triangles = np.concatenate([np.asarray(t).reshape(-1, 3) for t in authored['subMeshes']])
        fade = np.ones(authored['vertexCount']); fade[protected] = 0
        for amount in (0., .25, .75):
            neighbours = np.unique(face_triangles[np.any(protected[face_triangles], axis=1)])
            ring = neighbours[~protected[neighbours]]
            fade[ring] = amount; protected[ring] = True
        weight *= fade[:count]
    result = copy.deepcopy(authored)
    triangles = np.concatenate([np.asarray(t).reshape(-1, 3) for t in authored['subMeshes']])

    def compose(current_p, current_n, current_t, source_p, source_n, source_t, reference_p):
        positions, normals, tangents = current_p.copy(), current_n.copy(), current_t.copy()
        positions[:count] += (source_p - positions[:count]) * weight[:, None]
        normals[:count] = normalise(normals[:count] * (1 - weight[:, None]) + source_n * weight[:, None])
        tangent = tangents[:count, :3] * (1 - weight[:, None]) + source_t[:, :3] * weight[:, None]
        tangents[:count, :3] = normalise(tangent - normals[:count] * np.sum(tangent * normals[:count], axis=1, keepdims=True))
        reference = current_p.copy()
        reference[:count] += (reference_p - reference[:count]) * weight[:, None]
        positions, orientation = preserve_surface_orientation(reference, positions, triangles, transform)
        require(orientation['surfaceScaleFactor'] == 1, 'Native contour replacement reverses an assembled surface.')
        # Exact source contacts and exact assembled collars also retain their frames.
        for output, current, source in ((positions, current_p, source_p), (normals, current_n, source_n), (tangents, current_t, source_t)):
            output[:count][weight == 1] = source[weight == 1]
            output[:count][weight == 0] = current[:count][weight == 0]
        return positions, normals, tangents

    old = [np.asarray(authored[k]).reshape(-1, d) for k, d in (('vertices', 3), ('normals', 3), ('tangents', 4))]
    new = [np.asarray(refined[k]).reshape(-1, d) for k, d in (('vertices', 3), ('normals', 3), ('tangents', 4))]
    original = [np.asarray(native[k]).reshape(-1, d) for k, d in (('vertices', 3), ('normals', 3), ('tangents', 4))]
    base = compose(old[0],old[1],old[2],new[0],new[1],new[2],original[0])
    for key, value in zip(('vertices', 'normals', 'tangents'), base):
        result[key] = value.astype(np.float32).ravel().tolist()
    channels = {shape['name']: shape for shape in refined['shapes']}
    native_channels = {shape['name']: shape for shape in refined.get('_sourceFrameReference', native['shapes'])}
    inherited_channels = []; collar_frame_error = 0.
    for current_shape, target_shape in zip(authored['shapes'], result['shapes']):
        source_shape = channels.get(current_shape['name'])
        if source_shape is None:
            inherited_channels.append(current_shape['name'])
        sources = {f['weight']: f for f in source_shape['frames']} if source_shape else {}
        for current, target in zip(current_shape['frames'], target_shape['frames']):
            source = sources.get(current['weight'])
            if source is None:
                require(source_shape is None, 'An authored frame has no native weight.')
                source = {key: [0.] * (count * 3) for key in ('vertices', 'normals', 'tangents')}
            def posed(frame, rest):
                p, n, t = (v.copy() for v in rest)
                p += np.asarray(frame['vertices']).reshape(-1, 3)
                n += np.asarray(frame['normals']).reshape(-1, 3)
                t[:, :3] += np.asarray(frame['tangents']).reshape(-1, 3)
                return p, n, t
            original_shape = native_channels.get(current_shape['name'])
            original_frame = next((f for f in original_shape['frames'] if f['weight']==current['weight']), None) if original_shape else None
            reference_p = original[0] + np.asarray(original_frame['vertices']).reshape(-1,3) if original_frame else original[0]
            value = compose(*posed(current, old), *posed(source, new), reference_p)
            for key, field, rest in zip(('vertices', 'normals', 'tangents'), value, base):
                target[key] = (field[:, :3] - rest[:, :3]).astype(np.float32).ravel().tolist()
                if len(collar):
                    a = np.asarray(target[key]).reshape(-1, 3)[collar]
                    b = np.asarray(current[key]).reshape(-1, 3)[collar]
                    collar_frame_error = max(collar_frame_error, float(np.max(np.abs(a - b))))
    result['sourceFingerprint'] = fingerprint(result)[0]
    result['bounds'] = physical_mesh_bounds(result)
    details.update(nativeContourBaseline=native['sourceFingerprint'],
                   assembledCollarVertices=len(collar), collarFrameMaximumError=collar_frame_error,
                   nativeFaceVertices=int(np.count_nonzero(weight == 1)),
                   collarOnlyInheritedChannels=inherited_channels)
    return result, details


def refine_source_asset(asset: 'dict[str, Any]') -> 'list[dict[str, Any]]':
    """Evaluate one declared anatomical guide and its LOD/attachment consumers."""
    require_selected_guides([asset])
    donors = {}; guide_surfaces = {}; native_donors = {}; result = []
    for source_skin in asset['skins']:
        source_role = role(source_skin)
        if lod_index(source_skin) != 0 or source_role not in ('skin', 'lashes'):
            continue
        packet = native_packet(source_skin)
        native_donors[packet['sourceFingerprint']] = (packet, source_skin)
        surface, transform, ownership, _ = sculpt_surface(packet, source_skin, source_role)
        if not np.any(ownership):
            continue
        source_points = art.points(packet['vertices'], transform)
        donor = (source_points, surface - source_points, ownership)
        profile = source_skin.get('_faceProfile')
        if profile is not None and profile['operation'] == 'source-guide':
            donor += (art.compile_guide(profile, transform),)
        donors[packet['sourceFingerprint']] = donor
        if source_role not in donors or np.count_nonzero(ownership) > np.count_nonzero(donors[source_role][2]):
            donors[source_role] = donor
    for skin in sorted(asset['skins'], key=lambda value: (lod_index(value), role(value) == 'attached')):
        packet = native_packet(skin); identity = packet['sourceFingerprint']; which = role(skin)
        if which == 'native':
            result.append(dict(skin=skin, native=packet, authored=packet, mask=np.zeros(packet['vertexCount']),
                details=dict(sourceFingerprint=identity, authoredFingerprint=identity, role=which,
                             displacementMaximumMm=0., sourceTopologyPreserved=True)))
            continue
        profile = skin.get('_faceProfile')
        donor_key = profile['donorSourceFingerprint'] if profile is not None and profile['operation'] in ('source-lod-transfer', 'source-guide-follow') else skin.get('_faceLodDonor', which)
        if profile is not None and profile['operation'] == 'source-lod-transfer':
            require(donor_key in donors, 'Profile lower LOD donor is unavailable in the same source asset.')
        donor = guide_surfaces.get(donor_key) if which == 'attached' else donors.get(donor_key)
        working = packet; recoveries = []
        if lod_index(skin) > 0 and donor_key in native_donors:
            working, recoveries = recover_source_lod_frames(packet, skin, *native_donors[donor_key])
        authored, mask, details = refine_packet(working, skin, lod_index(skin), donor)
        if recoveries:
            authored['_sourceFrameReference'] = working['shapes']
            details['sourcePhysicalFrameRecovery'] = recoveries
        if profile is not None and profile['operation'] in ('source-guide', 'source-lod-transfer'):
            guide_surfaces[identity] = dict(native=packet, skin=skin, authored=authored)
        result.append(dict(skin=skin, native=packet, authored=authored, mask=mask, details=details))
    return result


def main(input_path=None):
    global MATERIAL_CHANNEL_CONTRACTS
    parser = argparse.ArgumentParser(); parser.add_argument('--stage', required=True); parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--inputs', type=Path, required=input_path is None)
    args = parser.parse_args(sys.argv[sys.argv.index('--') + 1:]); root = args.output.resolve()
    require(root.is_relative_to((REPO / 'tools/asset-authoring/workspace/Output').resolve()), 'Face candidate output must use managed authoring Output.')
    if args.stage not in ('preflight', 'pilot', 'generate', 'review-pilot', 'survey'):
        raise ValueError('Unknown facial production stage')
    inputs = load_inputs(input_path if input_path is not None else args.inputs)
    art.SOURCE = inputs['sourceRoot']
    MATERIAL_CHANNEL_CONTRACTS = load_material_channels(inputs['materialChannels'])
    census = art.read_json(inputs['census'])
    selected = select_source_assets(census, inputs['selection'])
    selected_census = load_profiles(dict(census, assets=selected), inputs['profiles'])
    selected = selected_census['assets']
    require_selected_guides(selected)
    selected = [bind_source_lod_families(a) for a in selected]
    if args.stage in ('survey', 'preflight'):
        records = {}; assets = []
        for asset in selected:
            owned = []
            for skin in asset['skins']:
                which = role(skin)
                if which == 'native':
                    continue
                packet = native_packet(skin)
                identity = packet['sourceFingerprint']
                if which=='attached':
                    mask=np.asarray(skin['_faceProfile']['mask']);report=dict(sourceFingerprint=identity,role=which,mode='declared-native-source-triangle-follow',donorSourceFingerprint=skin['_faceProfile']['donorSourceFingerprint'])
                else:
                    _, _, mask, report = sculpt_surface(packet, skin, which)
                record = dict(report, affectedVertices=int(np.count_nonzero(mask)),
                              nativePath=skin['path'], mesh=skin['mesh'], lod=lod_index(skin))
                if identity in records:
                    require(records[identity]['affectedVertices'] == record['affectedVertices'] and records[identity]['role'] == record['role'], 'Shared source has inconsistent survey ownership.')
                else:
                    records[identity] = record
                owned.append(identity)
            active = [fp for fp in owned if records[fp]['role'] == 'skin' and records[fp]['affectedVertices'] > 0]
            assets.append(dict(name=asset['name'], internalId=asset['internalId'], sourceContentKey=asset['sourceContentKey'], meshes=owned, requiredFaceStatus='calculated-source-surface' if active else 'pending-source-anatomy-profile', activeSkinSurfaces=len(active)))
        pending = [a['internalId'] for a in assets if a['requiredFaceStatus'] == 'pending-source-anatomy-profile']
        save_json(root / 'reports' / (args.stage + '.json'), dict(stage=args.stage, status='incomplete' if args.stage == 'preflight' and pending else 'passed',
            sourceCensus=str(inputs['census']), sourceSha256=art.hash_file(inputs['census']), sourceProfiles=str(inputs['profiles']), profileSha256=art.hash_file(inputs['profiles']), assets=assets,
            coverage=dict(status='incomplete' if pending else 'source-calculation-complete', sourceComplete=not pending, fullSourceSelection=len(selected)==len(census['assets']), requiredSourceAssets=len(census['assets']), selectedSourceAssets=len(selected), complete=False, pendingRequiredFaces=pending, artisticAcceptance='not-reviewed'), meshes=list(records.values())))
        return
    if args.stage == 'review-pilot':
        authored_index = authored_recipe_index(root, selected)
        framing = art.read_json(inputs['framing'])
        references = art.read_json(inputs['nativePoses'])
        for asset in selected:
            authored_sources = authored_asset_closure(asset, authored_index)
            try:
                reference = next(r for r in references['assets'] if r['sourceContentKey'] == asset['sourceContentKey'] and r['internalId'] == asset['internalId'])
                neutral = reference.get('neutralPose')
                neutral_scope = source_face_scope(asset, [neutral] + reference['phases'], framing)
                if neutral is not None:
                    coordinates = []
                    for source in neutral_scope['sources']:
                        skin = next(s for s in asset['skins'] if s['path'] == source['path'])
                        posed = apply_native_reference_pose(native_packet(skin), skin, neutral)
                        coordinates.append(art.points(posed['vertices'], art.CONVERT)[source['selectedIndices']])
                    neutral_scope['nativePoseCoordinates'] = np.concatenate(coordinates).tolist()
                    neutral_scope['poseReference'] = dict(phase='neutral', sourceFrame=neutral['sourceFrame'], sourceSignature=neutral['sourceSignature'], guideDomain='native renderer rest plus actual measured physical channels; canonical source bones, not live cloth draw')
                review_asset(asset, root / 'Assets' / asset_key(asset), authored_sources, face_scope=neutral_scope, native_pose=neutral)
                for pose in reference['phases']:
                    scope = copy.deepcopy(neutral_scope); coordinates = []
                    for source in scope['sources']:
                        skin = next(s for s in asset['skins'] if s['path'] == source['path'])
                        posed = apply_native_reference_pose(native_packet(skin), skin, pose)
                        positions = art.points(posed['vertices'], art.CONVERT)
                        coordinates.append(positions[source['selectedIndices']])
                    scope['nativePoseCoordinates'] = np.concatenate(coordinates).tolist()
                    scope['poseReference'] = dict(phase=pose['phase'], sourceFrame=pose['sourceFrame'], sourceSignature=pose['sourceSignature'], guideDomain='native renderer rest; actual frozen pose only after deformation')
                    review_asset(asset, root / 'Assets' / asset_key(asset), authored_sources,
                                 root / 'Assets' / asset_key(asset) / ('Pose-' + pose['phase']), scope, pose)
            finally:
                authored_sources.clear()
        save_json(root / 'reports/review-pilot.json', dict(stage=args.stage, status='passed', assets=[a['name'] for a in selected]))
        return
    report: dict[str, Any] = dict(stage=args.stage, status='started', sourceCensus=str(inputs['census']), sourceSha256=art.hash_file(inputs['census']), assets=[])
    authored_cache = {}; emitted = set()
    for asset in selected:
        empty_scene('FacialSurface')
        target = root / 'Assets' / asset_key(asset); target.mkdir(parents=True, exist_ok=True)
        recipe: dict[str, Any] = dict(schemaVersion=1, bundleName=resource_naming.source_label(asset['internalId']).lower() + '.bundle', meshes=[], textures=[])
        declarations = []; material_maps = []; reports = []
        skin_material_maps = {}
        for row in refine_source_asset(asset):
            skin = row['skin']; packet = row['native']; source_fingerprint = packet['sourceFingerprint']; which = role(skin)
            lod = lod_index(skin)
            if which == 'native':
                declarations.append(dict(sourceFingerprint=source_fingerprint, mode='native')); continue
            profile = skin.get('_faceProfile')
            authored, mask, details = row['authored'], row['mask'], row['details']
            if which == 'skin':
                details['materialChannelWork'] = [dict(slot=i, **material_channels(skin['materials'][i])) for i in facial_material_slots(packet, skin) if skin['materials'][i]]
            if which == 'eye':
                material_maps += ocular_materials(packet, skin)
            if not np.any(mask) or details['authoredFingerprint'] == source_fingerprint:
                declarations.append(dict(sourceFingerprint=source_fingerprint, mode='native')); reports.append(details); continue
            if source_fingerprint in authored_cache and authored_cache[source_fingerprint] != details['authoredFingerprint']:
                raise ValueError('Shared source mesh received inconsistent authored surfaces: ' + source_fingerprint)
            authored_cache[source_fingerprint] = details['authoredFingerprint']
            lineage = dict(schemaVersion=1, sourceFingerprint=source_fingerprint,
                authoredFingerprint=details['authoredFingerprint'], nativeVertices=packet['vertexCount'],
                edges=details.pop('subdivisionSourceEdges'))
            lineage_name = resource_naming.resource_names([resource_naming.ResourceName(source_fingerprint, skin['path'], suffix='Lineage')], extension='.json')[source_fingerprint]
            lineage_path = target / 'Lineage' / lineage_name
            resource_naming.assert_path_budget(lineage_path.resolve(), 259)
            save_json(lineage_path, lineage)
            details['sourceLineage'] = dict(path=str(lineage_path), sha256=art.hash_file(lineage_path))
            if source_fingerprint not in emitted:
                emitted.add(source_fingerprint)
                recipe['meshes'].append(recipe_mesh(authored))
                declarations.append(dict(sourceFingerprint=source_fingerprint, mode='authored', authoredMeshAsset=authored['name'], authoredFingerprint=details['authoredFingerprint']))
            if which == 'skin' and lod == 0 and not (profile is not None and profile['operation'] == 'source-guide'):
                directory = target / 'Sculpts' / resource_naming.source_label(skin['path']); directory.mkdir(parents=True, exist_ok=True)
                generated_maps = []; bake_reports = []
                for slot in facial_material_slots(packet, skin):
                    baked, region, bake_report = bake_surface(authored, mask, directory / ('Material-' + str(slot)),
                        material_slot=slot, renderer_to_prefab=skin['rendererToPrefab'])
                    bake_reports.append(dict(bake_report, materialSlot=slot))
                    generated_maps += material_surface(packet, skin, baked, region, target, recipe['textures'], slots=(slot,))
                details['sculptBake'] = bake_reports
                material_maps += generated_maps
                for material_map in generated_maps:
                    material = skin['materials'][material_map['materialSlot']]
                    skin_material_maps[material_identity(material)] = material_map['properties']
            elif which == 'skin':
                for slot, material in enumerate(skin['materials']):
                    if material and material_identity(material) in skin_material_maps:
                        material_maps.append(dict(sourceFingerprint=source_fingerprint, materialSlot=slot,
                            sourceShader=material['shader'], properties=skin_material_maps[material_identity(material)]))
            reports.append(details)
        save_json(target / 'recipe.json', recipe)
        identity = dict(name=asset['name'], internalId=asset['internalId'], sourceContentKey=asset['sourceContentKey'])
        save_json(target / 'declarations.json', dict(sourceIdentity=identity, meshes=declarations, materials=material_maps))
        report['assets'].append(dict(identity, directory=str(target), meshes=reports, recipe=str(target / 'recipe.json'), requiredFaceStatus=face_work_status(reports)))
        save_json(root / 'reports' / (args.stage + '-progress.json'), report)
    pending = [dict(internalId=a['internalId'], reason=a['requiredFaceStatus']) for a in report['assets'] if a['requiredFaceStatus'].startswith('pending-')]
    report['coverage'] = dict(sourceComplete=not pending, fullSourceSelection=len(selected)==len(census['assets']), requiredSourceAssets=len(census['assets']), selectedSourceAssets=len(selected), complete=False, pendingRequiredFaces=pending, artisticAcceptance='not-reviewed', sourceProfiles=str(inputs['profiles']), profileSha256=art.hash_file(inputs['profiles']))
    report['status'] = 'incomplete' if pending else 'passed'
    save_json(root / 'reports' / (args.stage + '.json'), report)


if __name__ == '__main__':
    main()
