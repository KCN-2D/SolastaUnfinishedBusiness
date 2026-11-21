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


def source_filter_budget():
    """One operator's temporary work, excluding retained source/final images."""
    return dict(workBytes=512*1024*1024,gatherBytes=8*1024*1024,nativeQueries=4096,nativeWaveBytes=128*1024*1024,
        nativeCacheBytes=32*1024*1024,nativeTapBlockBytes=64*1024*1024)


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
        vertices=packet['vertices']
        return np.sum(np.asarray([vertices[i*3:i*3+3]for i in indices])*weights[:, None], axis=0)
    require(set(anchor) == {'type', 'boneIndex', 'expectedBonePath', 'localPoint'}, 'Unknown bind-bone anchor fields.')
    require(type(anchor['localPoint']) is list and len(anchor['localPoint']) == 3 and all(type(x) in (int, float) for x in anchor['localPoint']), 'Bone-local coordinates must be numeric.')
    index = anchor['boneIndex']; point = np.asarray(anchor['localPoint'], dtype=np.float64)
    require(type(index) is int and 0 <= index < len(skin['bones']) and skin['bones'][index] == anchor['expectedBonePath'], 'Source landmark bone mapping differs.')
    require(point.shape == (3,) and np.all(np.isfinite(point)), 'Invalid source bone-local point.')
    bind = np.asarray(packet['bindPoses']).reshape(-1, 4, 4)[index].T
    return (np.linalg.inv(bind) @ np.r_[point, 1.])[:3]


def load_profiles(census: 'dict[str, Any]', path, *, resource_owners=False):
    from functools import lru_cache
    document = art.read_json(path)
    if not (type(document) is dict and set(document) == {'schemaVersion', 'profiles'} and document['schemaVersion'] == 1 and type(document['profiles']) is list): raise ValueError('Invalid face profile document.')
    by_source = {(a['sourceContentKey'], s['path']): (a, s) for a in census['assets'] for s in a['skins']}
    by_path={skin['meshFile']:skin for _,skin in by_source.values()}
    @lru_cache(maxsize=16)
    def read_packet(path):return native_packet(by_path[path])
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
        _, skin = by_source[key]; packet = read_packet(skin['meshFile'])
        positions=np.asarray(packet['vertices']).reshape(-1,3)
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
                guide_controls.append(dict(id=control['id'], source=point.tolist(), target=target.tolist(), sourceWeight=weight, radiusMetres=radius,sourceTriangleVertices=positions[triangle].tolist(),sourceTriangleWeights=mask[triangle].tolist(),sourceTriangleBarycentric=anchor['barycentric']))
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
            donor=donors[0];donor_packet=read_packet(donor['meshFile'])
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


def anatomy_surface_pair(positions, normals, ownership, origins, centre, frame, scale_metres):
    """Find bilateral cheek crests at corresponding source anatomical heights."""
    positions=np.asarray(positions,float);normals=np.asarray(normals,float)
    origins=np.asarray(origins,float);frame=np.asarray(frame,float);centre=np.asarray(centre,float)
    require(positions.shape==normals.shape and positions.ndim==2 and positions.shape[1]==3 and
            origins.shape==(2,3) and frame.shape==(3,3) and centre.shape==(3,) and
            all(np.isfinite(value).all()for value in(positions,normals,origins,frame,centre)) and
            np.isfinite(scale_metres) and scale_metres>0,'Invalid paired native anatomical frame.')
    require(np.allclose(frame.T@frame,np.eye(3),rtol=0,atol=1e-6),'A paired anatomical frame must be orthonormal.')
    ownership=np.asarray(ownership,float)
    require(ownership.shape==(len(positions),) and np.isfinite(ownership).all() and
            np.all((ownership>=0)&(ownership<=1)),'Invalid paired native surface ownership.')
    side,forward,up=frame.T;local=(positions-centre)@frame;candidates=[]
    for sign,origin in zip((-1,1),origins):
        height=(positions-origin)@up
        region=(ownership>0)&(sign*local[:,0]>.55*scale_metres)&(sign*local[:,0]<1.4*scale_metres)
        region&=(np.abs(height)<.12*scale_metres)&(local[:,1]>-.25*scale_metres)&(local[:,1]<.6*scale_metres)
        region&=(normals@(sign*.8*side+.6*forward)>0)
        ids=np.flatnonzero(region)
        require(len(ids)>0,'The source paired zygomatic surface is absent.')
        candidates.append((ids,height[ids]))
    left,lh=candidates[0];right,rh=candidates[1]
    score=local[right,0][None,:]-local[left,0][:,None]-4*np.abs(lh[:,None]-rh[None,:])-.5*(np.abs(lh[:,None])+np.abs(rh[None,:]))
    a,b=np.unravel_index(np.argmax(score),score.shape);indices=(int(left[a]),int(right[b]))
    return indices,dict(sourceBoneOffsetsMetres=[float(lh[a]),float(rh[b])],
        bilateralSourceHeightResidualMetres=float(abs(lh[a]-rh[b])),
        surfaceBreadthMetres=float(local[indices[1],0]-local[indices[0],0]))


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


def anatomy_contact_curves(asset, skin: 'dict[str, Any]', packet: 'dict[str, Any]', positions, normals, graph, representative, ownership, functions, marks, parts=('eyelids','lips')):
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
    require(parts and set(parts)<={'eyelids','lips'},'Unknown native contact tissue selection.')
    curves = {}
    if 'eyelids' in parts:
        eyes = [s for s in asset['skins'] if s['path'].lower().endswith('lod0') and any((m and m['shader'] == 'Custom/CharacterEye' for m in s['materials']))]
        if len(eyes) != 1:
            raise ValueError('An unambiguous native ocular owner is required for contact construction.')
        eye = eyes[0]
        ep = native_packet(eye)
        ev = functions['points'](ep['vertices'], art.CONVERT @ functions['matrix'](eye['rendererToPrefab']))
        et = np.concatenate([np.asarray(s, dtype=int).reshape(-1, 3) for s in ep['subMeshes']])
        eye_bones = [b.rsplit('/', 1)[-1].lower() if b else '' for b in eye['bones']]
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
    if 'lips'not in parts:return curves
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


def anatomy_curvature_field(positions, normals, graph, anchors, radius, path_cache, *, preserved_knots=None):
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
    fixed = np.asarray(anchors if preserved_knots is None else preserved_knots, dtype=int)
    require(fixed.ndim == 1 and len(fixed) >= 2 and len(set(fixed.tolist())) == len(fixed) and
            set(fixed.tolist()) <= set(anchors) and anchors[0] in fixed and anchors[-1] in fixed,
            'A native curvature section must preserve its distinct anatomical endpoints.')
    constraints = design[fixed]
    rhs = depth[fixed]
    fit_weight = weight.copy()
    coeff = None
    for iteration in range(3):
        left = design[ids].T @ (design[ids] * fit_weight[ids, None])
        target = design[ids].T @ (depth[ids] * fit_weight[ids])
        system = np.block([[left, constraints.T], [constraints, np.zeros((len(fixed), len(fixed)))]])
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
    shift[fixed] = 0
    return (shift * weight[:, None], weight, dict(nativePathVertices=len(path), knotVertices=anchors, preservedKnotVertices=fixed.tolist(), supportRadiusMetres=radius, nativePatchSamples=len(ids), nativeSectionLengthMetres=float(np.sum(np.linalg.norm(np.diff(positions[anchors], axis=0), axis=1))), largestSectionCorrectionMetres=float(np.linalg.norm(shift[ids], axis=1).max()), sourcePatchFit='native weighted constrained robust quadric; declared anatomical knots fixed', maximumKnotResidualMetres=float(np.abs(constraints @ coeff - rhs).max() * radius)))


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


def recover_source_lod_frames(packet, skin, donor_packet, donor_skin, *, protected_vertices=()):
    """Recover impossible morphs from the same source's validated LOD field."""
    diagonal = float(np.linalg.norm(np.ptp(np.asarray(packet['vertices']).reshape(-1, 3), axis=0)))
    invalid = []
    for si, shape in enumerate(packet['shapes']):
        for fi, frame in enumerate(shape['frames']):
            delta = np.asarray(frame['vertices']).reshape(-1, 3)
            if not np.isfinite(delta).all() or float(np.linalg.norm(delta, axis=1).max()) > diagonal:
                invalid.append((si, fi))
    if not invalid: return packet, []
    prepared_reference=donor_packet.get('_sourceFrameReference')is not None and lod_index(donor_skin)==lod_index(skin)
    require(lod_index(skin) > 0 and (lod_index(donor_skin) == 0 or prepared_reference),
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
        protected=np.asarray(sorted(protected_vertices),int)
        if prepared_reference:
            limit=max(float(np.linalg.norm(delta,axis=1).max())*4,diagonal*1e-6)
            retained=np.asarray(frame['vertices']).reshape(-1,3)
            valid=np.isfinite(retained).all(axis=1)&(np.linalg.norm(retained,axis=1)<=limit)
            for key in ('normals','tangents'):
                values=np.asarray(frame[key]).reshape(-1,3)
                valid&=np.isfinite(values).all(axis=1)&(np.linalg.norm(values,axis=1)<=4)
            protected=np.union1d(protected,np.flatnonzero(valid))
        require(np.all((protected>=0)&(protected<packet['vertexCount'])),'A recovered source frame has invalid protected vertices.')
        for key in ('vertices','normals','tangents'):
            values=np.asarray(target[key]).reshape(-1,3);original=np.asarray(frame[key]).reshape(-1,3)
            values[protected]=original[protected];target[key]=values.astype(np.float32).ravel().tolist()
        require(np.isfinite(target['vertices']).all()and np.linalg.norm(np.asarray(target['vertices']).reshape(-1,3),axis=1).max()<=diagonal,
            'A protected source collar still contains an impossible physical frame.')
        reports.append(dict(channelIndex=si, name=channel['name'], weight=frame['weight'],
            nativeSourceFingerprint=packet['sourceFingerprint'], donorSourceFingerprint=donor_packet['sourceFingerprint'],
            preservedPreparedVertices=len(protected)if prepared_reference else 0,
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
        if lod_index(skin) == 0 or family(skin) not in sources:
            continue
        source, packet, slots = sources[family(skin)]
        matched = [i for i in slots if i < len(skin['materials']) and source['materials'][i] is not None and skin['materials'][i] is not None and material_identity(source['materials'][i]) == material_identity(skin['materials'][i])]
        if matched:
            skin['_nativeLodDonor'] = packet['sourceFingerprint']
            if role(skin)=='native':
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


def garment_crossing_depth(garment, skin, normal, *, positive_only=True):
    """Signed depth intervals of the surfaces inside one garment triangle's prism."""
    edges = garment[1:] - garment[0]
    gram = edges @ edges.T
    if np.linalg.det(gram) < 1e-20:
        return
    relative = skin - garment[0]
    uv = relative @ np.linalg.solve(gram, edges).T
    heights = relative @ normal
    bary = np.concatenate((uv, 1 - uv.sum(axis=2, keepdims=True)), axis=2)
    overlap = (bary.max(axis=1) >= 0).all(axis=1)
    if positive_only:
        overlap &= heights.max(axis=1) > 0
    for i in np.flatnonzero(overlap):
        if (bary[i] >= 0).all():
            yield heights[i].min(), heights[i].max()
        else:
            interval = clipped_garment_depth(np.column_stack((uv[i], heights[i])))
            if interval is not None:
                yield interval


def clipped_garment_depth(polygon):
    for boundary in range(3):
        distances = polygon[:, boundary] if boundary < 2 else 1 - polygon[:, :2].sum(axis=1)
        previous, previous_distance = polygon[-1], distances[-1]
        inside = []
        for point, distance in zip(polygon, distances):
            if (previous_distance < 0) != (distance < 0):
                fraction = previous_distance / (previous_distance - distance)
                inside.append(previous + fraction * (point - previous))
            if distance >= 0:
                inside.append(point)
            previous, previous_distance = point, distance
        if not inside:
            return None
        polygon = np.asarray(inside)
    return polygon[:, 2].min(), polygon[:, 2].max()


def overlapping_surface_faces(low, high, surface_low, surface_high):
    """Find exact AABB overlaps without testing each cloth face against all skin."""
    candidates = [[] for _ in low]
    if len(surface_low) == 0:
        return [np.empty(0, dtype=int) for _ in low]
    centers = (surface_low + surface_high) * .5
    pending = [(np.arange(len(surface_low)), np.arange(len(low)))]
    while pending:
        surface, queries = pending.pop()
        bounds_low = surface_low[surface].min(axis=0)
        bounds_high = surface_high[surface].max(axis=0)
        queries = queries[((low[queries] <= bounds_high) & (high[queries] >= bounds_low)).all(axis=1)]
        if not len(queries):
            continue
        if len(surface) <= 48:
            hits = ((low[queries, None] <= surface_high[surface]) &
                    (high[queries, None] >= surface_low[surface])).all(axis=2)
            for row, column in zip(*np.nonzero(hits)):
                candidates[queries[row]].append(surface[column])
            continue
        axis = np.argmax(np.ptp(centers[surface], axis=0))
        count = len(surface) // 2
        children = np.argpartition(centers[surface, axis], count)
        pending.extend(((surface[children[:count]], queries), (surface[children[count:]], queries)))
    return [np.array(sorted(matches), dtype=int) for matches in candidates]


def garment_inside_faces(exterior, lining, search_distance):
    """Find the nearest exterior surface at interior samples of each native lining face."""
    origins = exterior[:, 0]
    first, second = exterior[:, 1] - origins, exterior[:, 2] - origins
    outer_normals = normalise(np.cross(first, second))
    inner_normals = normalise(np.cross(lining[:, 1] - lining[:, 0], lining[:, 2] - lining[:, 0]))
    coordinates = np.linalg.pinv(np.stack((first, second), axis=2))
    neighbours = overlapping_surface_faces(lining.min(axis=1) - search_distance,
        lining.max(axis=1) + search_distance, exterior.min(axis=1), exterior.max(axis=1))
    sample_weights = np.array([[1/3, 1/3, 1/3], [.7, .15, .15], [.15, .7, .15], [.15, .15, .7]])
    covered = np.zeros(len(lining), dtype=bool)
    for index, (points, direction, nearby) in enumerate(zip(sample_weights @ lining, inner_normals, neighbours)):
        alignment = outer_normals[nearby] @ direction
        aligned = alignment > .6
        selected = nearby[aligned]
        if not len(selected):
            continue
        separation = origins[selected, None] - points[None]
        depths = np.einsum('nij,nj->ni', separation, outer_normals[selected]) / alignment[aligned, None]
        offsets = depths[:, :, None] * direction - separation
        uv = np.einsum('nji,nki->nkj', coordinates[selected], offsets)
        hits = (np.abs(depths) <= search_distance) & (uv >= -1e-7).all(axis=2) & (uv.sum(axis=2) <= 1+1e-7)
        nearest = np.argmin(np.where(hits, np.abs(depths), np.inf), axis=0)
        sample_ids = np.arange(len(points))
        inside = hits[nearest, sample_ids] & (depths[nearest, sample_ids] > 0)
        covered[index] = inside[0] or np.count_nonzero(inside[1:]) >= 2
    return covered


def fit_garment_crossings(positions, triangles, skin, *, clearance, maximum_offset):
    """Fit the outer garment in the drawn pose while moving UV duplicates together."""
    require(0 < clearance < maximum_offset and math.isfinite(maximum_offset),
            'Garment clearance must be positive and smaller than its displacement limit.')
    groups = art.weld_groups(positions)
    directions = surface_normals(positions, triangles, groups)
    skin_normals = normalise(np.cross(skin[:, 1] - skin[:, 0], skin[:, 2] - skin[:, 0]))
    skin_low, skin_high = skin.min(axis=1), skin.max(axis=1)
    # Bounds enclose all iterations because each vertex stays within maximum_offset.
    low = positions[triangles].min(axis=1) - maximum_offset
    high = positions[triangles].max(axis=1) + maximum_offset
    candidates = overlapping_surface_faces(low, high, skin_low, skin_high)
    displacement = np.zeros(groups.max() + 1)
    group_faces = groups[triangles]
    edges = np.sort(np.stack((group_faces, np.roll(group_faces, -1, axis=1)), axis=-1), axis=-1)
    unique_edges, edge_ids = np.unique(edges.reshape(-1, 2), axis=0, return_inverse=True)
    edges_for_faces = edge_ids.reshape(triangles.shape)
    fitted = positions.copy()
    for _ in range(5):
        vertices = fitted[triangles]
        normals = normalise(np.cross(vertices[:, 1] - vertices[:, 0], vertices[:, 2] - vertices[:, 0]))
        advance = np.zeros_like(displacement)
        depths_by_face = np.zeros(len(triangles))
        crossing = np.zeros(len(triangles), dtype=bool)
        for i, (indices, face, normal, nearby) in enumerate(zip(triangles, vertices, normals, candidates)):
            projections = directions[indices] @ normal
            if not len(nearby) or projections.min() < .4:
                continue
            neighbours = nearby[skin_normals[nearby] @ normal > .6]
            for shallow, deep in garment_crossing_depth(face, skin[neighbours], normal):
                if deep <= 0 or shallow >= maximum_offset:
                    continue
                depths_by_face[i] = max(depths_by_face[i], min(deep, maximum_offset))
                crossing[i] |= shallow <= 0
        # Extend a measured intersection into its interior. Fully protruding faces
        # have no crossing edge of their own. Leave separate parallel sheets alone.
        participating = crossing
        while participating.any():
            touched = np.zeros(len(unique_edges), dtype=bool)
            touched[edges_for_faces[participating]] = True
            connected = (depths_by_face > 0) & touched[edges_for_faces].any(axis=1)
            if np.array_equal(connected, participating):
                break
            participating = connected
        for i in np.flatnonzero(participating):
            projections = directions[triangles[i]] @ normals[i]
            np.maximum.at(advance, group_faces[i], (depths_by_face[i] + clearance) / projections)
        next_displacement = np.minimum(displacement + advance, maximum_offset)
        if np.array_equal(displacement, next_displacement):
            break
        displacement = next_displacement
        fitted = positions + directions * displacement[groups, None]
    return fitted - positions


def garment_skinning_frame(packet, observed, points, posed, triangles, bone_paths, tolerance, world_to_prefab):
    poses = np.asarray(packet['bindPoses']).reshape(-1, 4, 4).transpose(0, 2, 1)
    bones = observed['bones'][:len(poses)]
    require([bone['name'] for bone in bones] == [path.rsplit('/', 1)[-1] for path in bone_paths[:len(poses)]],
            'The drawn garment and the source renderer have different bone orders.')
    worlds = np.asarray([bone['localToWorld'] for bone in bones]).reshape(-1, 4, 4).transpose(0, 2, 1)
    weights = np.asarray(packet['weights']).reshape(-1, 4)
    indices = np.asarray(packet['boneIndices']).reshape(-1, 4)
    transform = np.einsum('vb,vbij->vij', weights, (world_to_prefab @ worlds @ poses)[indices])
    linear = transform[:, :3, :3].copy()
    reference = points.copy()
    active = {row['name']: row['weight'] for row in observed.get('blendWeights', []) if row['weight']}
    for shape in packet['shapes']:
        weight = active.get(shape['name'])
        if weight is None:
            continue
        frames = sorted([(0., None)] + [(frame['weight'], frame) for frame in shape['frames'] if frame['weight']],
                        key=lambda row: row[0])
        if len(frames) < 2:
            raise ValueError('An active garment blend shape has no weighted frames.')
        if any(a[0] == b[0] for a, b in zip(frames, frames[1:])):
            raise ValueError('Two garment blend frames have the same weight.')
        end = min(max(1, np.searchsorted([w for w, _ in frames], weight)), len(frames) - 1)
        (low, first), (high, last) = frames[end-1:end+1]
        fraction = (weight - low) / (high - low)
        for frame, factor in ((first, 1 - fraction), (last, fraction)):
            if frame is not None:
                reference += np.asarray(frame['vertices']).reshape(-1, 3) * factor
    predicted = np.einsum('vij,vj->vi', linear, reference) + transform[:, :3, 3]
    unbound = np.linalg.norm(predicted - posed, axis=1) > tolerance
    if not unbound.any() or 'observedBindPoses' not in observed:
        return linear, unbound
    live = np.asarray(observed['observedBindPoses'][:len(poses)]).reshape(-1, 16)
    require(live.shape == (len(poses), 16) and np.allclose(live, np.asarray(packet['bindPoses']).reshape(-1, 16), rtol=1e-6, atol=1e-7),
            'The pose mesh and source mesh have different bind poses.')
    # Cloth and procedural morphs move the surface after skeletal skinning.
    # Use a nondegenerate incident triangle to transfer motion in its tangent
    # plane, and preserve the thickness of the fabric in the normal direction.
    def face_frames(vertices):
        faces = vertices[triangles]
        u, v = faces[:, 1] - faces[:, 0], faces[:, 2] - faces[:, 0]
        cross = np.cross(u, v)
        area = np.linalg.norm(cross, axis=1)
        quality = area / np.maximum(np.sum(u*u + v*v + (u-v)**2, axis=1), 1e-20)
        return np.stack((u, v, normalise(cross)), axis=2), quality
    before, original_quality = face_frames(predicted)
    after, posed_quality = face_frames(posed)
    quality = np.minimum(original_quality, posed_quality)
    # UV seams can split one physical point into multiple indices.
    groups = art.weld_groups(np.column_stack((points, posed)))
    support = np.full(groups.max()+1, -1, int)
    for face in np.argsort(quality, kind='stable'):
        if quality[face] > 1e-4:
            support[groups[triangles[face]]] = face
    chart = support[groups]
    usable = unbound & (chart >= 0)
    faces = chart[usable]
    linear[usable] = after[faces] @ np.linalg.solve(before[faces], linear[usable])
    return linear, unbound & ~usable


def fit_posed_garment(packet, observed, skin, bone_paths, renderer_to_prefab, *, clearance, maximum_offset, previous=None):
    """Map correction of the final posed cloth surface back into the original garment."""
    require(packet['subMeshes'] == observed['submeshes'], 'Drawn garment topology differs from its prefab.')
    points = np.asarray(packet['vertices']).reshape(-1, 3)
    # The character preview magnifies the actor independently of the camera.
    # Measure in the same prefab metres as the equipment and its simulation.
    rendered_frame = np.asarray(observed['localToWorld']).reshape(4, 4).T
    world_to_prefab = np.linalg.solve(rendered_frame.T, renderer_to_prefab.T).T
    def to_prefab(vertices):
        return vertices @ world_to_prefab[:3, :3].T + world_to_prefab[:3, 3]
    posed = to_prefab(np.asarray(observed['vertices'], dtype=float).reshape(points.shape))
    skin = to_prefab(np.asarray(skin, dtype=float))
    require(np.isfinite(posed).all() and np.isfinite(skin).all(),
            'The observed garment or skin has non-finite vertices.')
    triangles = np.concatenate(packet['subMeshes']).reshape(-1, 3)
    outer = [indices for is_skin, indices in zip(observed['skinMaterials'], packet['subMeshes'], strict=True)
             if indices and not is_skin]
    current = previous if previous is not None else packet
    if not outer:
        return current, 0
    linear, unbound = garment_skinning_frame(packet, observed, points, posed, triangles, bone_paths, clearance,
                                              world_to_prefab)
    current_points = np.asarray(current['vertices']).reshape(points.shape)
    offset = current_points - points
    prior = np.einsum('vij,vj->vi', linear, offset)
    delta = fit_garment_crossings(posed + prior, np.concatenate(outer).reshape(-1, 3), skin,
                                  clearance=clearance, maximum_offset=maximum_offset)
    moved = np.any(delta != 0, axis=1)
    if not moved.any():
        return current, 0
    require(not np.any(unbound & moved), 'A fitted garment vertex has no matching bone or posed triangle.')
    requested = (prior + delta)[moved]
    limit = np.maximum(1, np.linalg.norm(requested, axis=1, keepdims=True) / maximum_offset)
    rest_offsets = np.linalg.solve(linear[moved], (requested / limit)[:, :, None])[:, :, 0]
    rest_distance = np.linalg.norm(rest_offsets @ renderer_to_prefab[:3, :3].T, axis=1, keepdims=True)
    offset[moved] = rest_offsets / np.maximum(1, rest_distance / maximum_offset)
    correction = points + offset - current_points
    welded = art.weld_groups(points)
    native_normals = np.asarray(packet['normals']).reshape(-1, 3)
    groups = normal_groups(points, native_normals)
    before = surface_normals(points, triangles, groups, points, native_normals)
    for attempt in range(8):
        proposal = current_points + correction * (2.0 ** -attempt)
        fitted, _ = preserve_surface_orientation(points, proposal, triangles, renderer_to_prefab, groups=welded)
        after = surface_normals(fitted, triangles, groups, points, native_normals)
        if np.all(np.sum(before * after, axis=1) >= 0):
            break
    else:
        return current, 0
    changed = np.count_nonzero(np.any(fitted != current_points, axis=1))
    if not changed:
        return current, 0
    candidate = dict(packet, vertices=fitted.astype(np.float32).ravel().tolist())
    for field, width in [('normals', 3), ('tangents', 4)]:
        values = np.array(packet[field]).reshape(-1, width)
        values[:, :3] = rotate_between(values[:, :3], before, after)
        candidate[field] = values.astype(np.float32).ravel().tolist()
    # Preserve the native culling envelope, including conservative artist padding.
    old_center, old_size = np.asarray(packet['bounds']).reshape(2, 3)
    center, size = np.asarray(physical_mesh_bounds(candidate)).reshape(2, 3)
    low = np.minimum(old_center - old_size / 2, center - size / 2)
    high = np.maximum(old_center + old_size / 2, center + size / 2)
    candidate['bounds'] = np.r_[(low + high) / 2, high - low].tolist()
    candidate['sourceFingerprint'] = fingerprint(candidate)[0]
    return candidate, int(changed)


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


def preserve_surface_orientation(reference, candidate, triangles, transform, *, groups=None):
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
    factors = np.ones(groups.max() + 1) if groups is not None else None
    for attempt in range(12):
        factor = (2.0 ** -attempt if attempt < 9 else 0.) if factors is None else factors[groups, None]
        local = (reference + (candidate - reference) * factor).astype(np.float32)
        points = art.points(local.ravel().tolist(), transform)
        area = np.cross(points[triangles[:, 1]] - points[triangles[:, 0]],
                        points[triangles[:, 2]] - points[triangles[:, 0]])
        projection = np.sum(source_area * area, axis=1)
        bad = supported & (projection < squared * .05)
        if initial_bad is None:
            initial_bad = int(np.count_nonzero(bad))
        if not np.any(bad):
            return local, dict(surfaceScaleFactor=float(np.min(factor)), surfaceBacktrackingAttempts=attempt,
                initialFacingFailures=initial_bad, supportedFacingTriangles=int(supported.sum()),
                floatLimitedTriangles=int((~supported).sum()), minimumSourceProjectedAreaRatio=.05)
        if factors is not None:
            affected = np.unique(groups[triangles[bad]])
            if attempt < 9:
                factors[affected] *= .5
            else:
                factors[affected] = 0.
    raise ValueError('The native reference surface cannot retain its source-facing triangles.')


def handle_curvature_coordinates(edges, inverse_lengths, mass, free, anchored, target_indices, target_count):
    """Evaluate the biharmonic system on rows incident to a free surface point."""
    count = len(mass)
    free_column = np.full(count, -1, dtype=int)
    free_column[free] = np.arange(len(free))
    fixed_column = np.full(count, -1, dtype=int)
    fixed_column[anchored] = target_indices
    connected = np.concatenate((edges, edges[:, ::-1]))
    edge_weights = np.tile(inverse_lengths, 2)
    source, target = connected.T
    active = np.unique(np.r_[free, source[free_column[target] >= 0]])
    row = np.full(count, -1, dtype=int)
    row[active] = np.arange(len(active))
    degrees = np.bincount(source, weights=edge_weights, minlength=count)
    diagonal = np.arange(count)
    source = np.r_[source, diagonal]
    target = np.r_[target, diagonal]
    amounts = np.r_[-edge_weights, degrees]
    keep = row[source] >= 0
    source, target, amounts = source[keep], target[keep], amounts[keep]
    scale = np.sqrt(mass[source])
    moving = free_column[target] >= 0
    matrix = np.zeros((len(active), len(free)))
    fixed = np.zeros((len(active), target_count))
    np.add.at(matrix, (row[source[moving]], free_column[target[moving]]), amounts[moving] / scale[moving])
    np.add.at(fixed, (row[source[~moving]], fixed_column[target[~moving]]), amounts[~moving] / scale[~moving])
    return np.linalg.solve(matrix.T @ matrix, -matrix.T @ fixed)


def interpolate_source_handles(packet, skin, pose, positions, controls, reports, ownership, transform, units=(),sections=()):
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
        require(bones.any(),'The declared contact unit has no owned source tissue.')
        mode=unit.get('ownership','bone-influence')
        require(mode in('bone-influence','native-rim'),'Unknown articulated contact ownership.')
        selected=((humanoid_face_contact_guard(packet,skin,contact_bone_prefixes=prefixes)==0)
                  if mode=='native-rim'else np.any(bones[indices] & (weights > 0), axis=1)) & allowed
        require(np.any(selected), 'The declared contact unit has no owned source tissue.')
        handle = next((i for i, report in enumerate(reports) if report['name'] == unit['handle']), None)
        require(handle is not None, 'A contact unit has no matching authored handle.')
        offset = controls[handle][1] - controls[handle][0]
        unit_rows.append((selected, offset, handle, mode))
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
        # Curvature coordinates spread a source anchor continuously. Adjacent
        # coarse triangles remain free instead of becoming a translated plate.
        patch=np.flatnonzero(distance<=.003)
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
    for selected, offset, handle, mode in unit_rows:
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
        reports[handle]['contactUnitOwnership'] = mode
        reports[handle]['contactUnitOffsetMm'] = (offset*1000).tolist()
    edges = np.unique(np.sort(groups[np.concatenate((triangles[:,[0,1]], triangles[:,[1,2]], triangles[:,[2,0]]))],axis=1),axis=0)
    edges = edges[edges[:,0]!=edges[:,1]]
    _, representatives = np.unique(groups,return_index=True)
    lengths = np.linalg.norm(rest[representatives[edges[:,0]]]-rest[representatives[edges[:,1]]],axis=1)
    free = np.flatnonzero(~fixed)
    anchored = np.flatnonzero(fixed)
    if len(free):
        # Biharmonic coordinates minimize changes in the displacement gradient.
        # Include the fixed outer tissue in the curvature energy so that the
        # deformation approaches its protected boundary with a smooth slope.
        area=np.linalg.norm(np.cross(rest[triangles[:,1]]-rest[triangles[:,0]],
            rest[triangles[:,2]]-rest[triangles[:,0]]),axis=1)*.5
        mass=np.zeros(count)
        for column in range(3):np.add.at(mass,groups[triangles[:,column]],area/3)
        mass=np.maximum(mass,max(float(mass.max())*1e-9,1e-20))
        targets,target_indices=np.unique(displacement[anchored],axis=0,return_inverse=True)
        coordinates=handle_curvature_coordinates(edges,1/np.maximum(lengths,1e-6),
            mass,free,anchored,target_indices,len(targets))
        if not np.isfinite(coordinates).all():raise ValueError('The source curvature coordinates are not finite.')
        # A C1 positive partition retains a convex combination of source
        # displacements, with smooth slopes at fixed tissue and handle patches.
        coordinates=np.maximum(coordinates,0)
        coordinates=np.where(coordinates<0.02,coordinates*coordinates/(2*0.02),coordinates-0.02/2)
        total=coordinates.sum(axis=1,keepdims=True)
        require(bool(np.all(total>1e-12)),'The source curvature partition has no owned target.')
        coordinates/=total
        displacement[free]=coordinates@targets
        for report in reports:report['surfaceInterpolation']='bounded-c1-curvature-coordinates'
    if not np.isfinite(displacement).all():
        raise ValueError('The source surface edit is not finite.')
    # Every free sample is a positive harmonic combination of explicit handles.
    if np.linalg.norm(displacement,axis=1).max() > np.linalg.norm(displacement[anchored],axis=1).max()+1e-10:
        raise ValueError('The source handle interpolation overshot its owned targets.')
    if sections:
        graph=[set()for _ in range(count)]
        for left,right in edges:graph[left].add(right);graph[right].add(left)
        current=(apply_native_reference_pose(packet,skin,pose)if pose is not None else packet)
        normals=art.native_normals(current,art.CONVERT if pose is not None else transform)[representatives]
        contact=humanoid_face_contact_guard(packet,skin,contact_bone_prefixes=('b_eye_','b_eyelid_','b_lip_'))
        mask=np.ones(count);np.minimum.at(mask,groups,np.minimum(ownership,contact));mask[~group_allowed]=0
        curvature=np.zeros((count,3));support=np.zeros(count);path_cache={}
        by_name={report['name']:int(groups[report['sourceVertex']])for report in reports}
        for section in sections:
            required={'id','anchors','radiusMetres','retain'}
            require(isinstance(section,dict)and required<=set(section)<=required|{'preservedKnots'}and
                    isinstance(section['id'],str)and section['id']and isinstance(section['anchors'],list)and
                    len(section['anchors'])>=2 and all(name in by_name for name in section['anchors']),
                    'A native curvature section requires explicit source handles.')
            radius=section['radiusMetres'];retain=section['retain']
            require(type(radius)in(int,float)and np.isfinite(radius)and radius>0 and
                    type(retain)in(int,float)and np.isfinite(retain)and 0<=retain<=1,
                    'A native curvature section requires a positive physical radius and bounded retention.')
            anchors=[by_name[name]for name in section['anchors']]
            require(len(set(anchors))==len(anchors),'A native curvature section has coincident source handles.')
            preserved=section.get('preservedKnots',[section['anchors'][0],section['anchors'][-1]])
            require(isinstance(preserved,list)and all(name in section['anchors']for name in preserved),
                    'A native curvature section cannot preserve an unrelated source knot.')
            change,weight,proof=anatomy_curvature_field(positions[representatives],normals,graph,anchors,radius,path_cache,
                preserved_knots=[by_name[name]for name in preserved])
            curvature+=change*(1-retain);support+=weight
            owner=next(report for report in reports if report['name']==section['anchors'][0])
            owner.setdefault('curvatureSections',[]).append(dict(id=section['id'],retain=retain,**proof))
        correction=curvature/np.maximum(1.,support[:,None])*mask[:,None]
        ids=representatives[np.flatnonzero(np.linalg.norm(correction,axis=1)>0)]
        if len(ids):
            target=positions[ids]+correction[groups[ids]]
            target=(reference_pose_surface_to_rest(packet,skin,pose,ids,target)if pose is not None else target)
            displacement[groups[ids]]+=target-rest[ids]
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
    if packet['sourceFingerprint'] != skin.get('sourceFingerprint'):
        raise ValueError('Native packet differs from its declared source fingerprint: ' + skin['meshFile'])
    return packet


def role(skin: 'dict[str, Any]'):
    if skin.get('_faceProfile',{}).get('operation')=='source-guide-follow':return 'attached'
    if skin.get('_faceProfile',{}).get('operation')=='source-guide':return 'skin'
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
        if reason is None and any(left != right for left, right in maps):
            # Averaged skin at a new midpoint does not reproduce the posed
            # coarse edge when its endpoints use different bone transforms.
            reason = 'different-native-skin-influences'
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
                             ownership=min(ownership[i] for edge in twins for i in edge),
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
            ids, skin = indices[a].copy(), weights[a].copy()
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
                bow = pn - linear
                maximum = min(settings['maximumBowMillimetres'] / 1000,
                              np.linalg.norm((fp[b] - fp[a]) @ m.T) * settings['maximumBowEdgeFraction'])
                factor = min(entry['factor'], maximum / max(np.linalg.norm(bow @ m.T), 1e-30) * entry['ownership'])
                group_positions[key] = linear + bow * factor
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


def rigid_attachment_topology(packet,frame):
    """Resolve real welded root loops and their geodesic distance on a rigid attachment."""
    import heapq
    frame=np.asarray(frame,float);require(frame.shape==(4,4)and np.isfinite(frame).all()and abs(np.linalg.det(frame[:3,:3]))>1e-12,
        'An attachment requires its actual nonsingular support frame.')
    p=np.asarray(packet['vertices'],float).reshape(-1,3);world=p@frame[:3,:3].T+frame[:3,3]
    weights=np.asarray(packet['weights']).reshape(-1,4);bones=np.asarray(packet['boneIndices']).reshape(-1,4)
    require(len(set(bones[weights>0]))==1 and np.allclose(weights.sum(axis=1),1,atol=1e-6),
        'A rigid attachment root requires one shared native support bone.')
    signatures=np.concatenate((np.rint(world*1e7),weights,bones),axis=1)
    _,first,groups=np.unique(signatures,axis=0,return_index=True,return_inverse=True);points=world[first]
    faces=groups[np.concatenate([np.asarray(slot,int).reshape(-1,3)for slot in packet['subMeshes']])]
    edges=np.sort(np.concatenate((faces[:,[0,1]],faces[:,[1,2]],faces[:,[2,0]])),axis=1)
    edges,counts=np.unique(edges,axis=0,return_counts=True);boundary=edges[counts==1]
    require(len(boundary)>0 and np.all(counts<=2),'An attachment requires manifold triangles and actual open root loops.')
    graph=[{}for _ in points];cut_graph={}
    for a,b in edges:
        length=float(np.linalg.norm(points[a]-points[b]));require(length>0,'An attachment has a zero-length physical edge.')
        graph[a][b]=length;graph[b][a]=length
    for a,b in boundary:cut_graph.setdefault(int(a),set()).add(int(b));cut_graph.setdefault(int(b),set()).add(int(a))
    require(all(len(peers)==2 for peers in cut_graph.values()),'An attachment root must be a closed degree-two boundary.')
    loops=[];remaining=set(cut_graph)
    while remaining:
        seed=remaining.pop();loop={seed};wave={seed}
        while wave:
            wave=set().union(*(cut_graph[v]for v in wave))&remaining;remaining-=wave;loop|=wave
        loops.append(sorted(loop))
    radii=np.array([sum(graph[a][b]for a in loop for b in cut_graph[a])/(4*np.pi)for loop in loops])
    roots=np.asarray(sorted(cut_graph),int);distance=np.full(len(points),np.inf);distance[roots]=0
    owner=np.full(len(points),-1,dtype=int)
    for number,loop in enumerate(loops):owner[loop]=number
    queue=[(0.,int(i))for i in roots];heapq.heapify(queue)
    while queue:
        value,index=heapq.heappop(queue)
        if value!=distance[index]:continue
        for other,length in graph[index].items():
            candidate=value+length
            if candidate<distance[other]:
                distance[other]=candidate
                owner[other]=owner[index]
                heapq.heappush(queue,(candidate,other))
    return dict(frame=frame,world=world,points=points,first=first,groups=groups,faces=faces,roots=roots,loops=loops,
                distance=distance,radius=float(np.median(radii)),radii=radii,owner=owner)


def fit_rigid_attachment_roots(packet,frame,supports,*,depth_metres,falloff_radii,maximum_shift_metres):
    """Seat open attachment roots inside the shared envelope of their support surfaces."""
    from mesh_recipe_codec import closest_triangle
    require(all(np.isfinite(v)and v>0 for v in(depth_metres,falloff_radii,maximum_shift_metres)),
        'Attachment root distances and limits must be finite and positive.')
    topology=rigid_attachment_topology(packet,frame)
    frame,world,points,first,groups,faces,roots,loops,distance=(topology[key]for key in('frame','world','points','first','groups','faces','roots','loops','distance'))
    widths=topology['radii']*falloff_radii
    owners=topology['owner']
    field=np.clip(1-distance/widths[owners],0,1);field=field*field*(3-2*field)
    require(supports,'Attachment root fitting requires every declared support surface.')
    targets=[];directions=[]
    for root in roots:
        samples=[];normals=[]
        for support in supports:
            vertices=np.asarray(support['vertices'],float);triangles=np.asarray(support['triangles'],int).reshape(-1,3)
            index,bary,_=closest_triangle(points[root],vertices[triangles]);ids=triangles[index]
            samples.append(bary@vertices[ids]);normals.append(normalise(bary@np.asarray(support['normals'],float)[ids]))
        normal=normalise(np.mean(normals,axis=0));center=np.mean(samples,axis=0)
        require(np.linalg.norm(normal)>.5,'Attachment support normals have inconsistent orientations.')
        inset=min((sample-center)@normal for sample in samples)-depth_metres
        targets.append(center+normal*inset);directions.append(normal)
    targets=np.asarray(targets);directions=np.asarray(directions);delta=targets-points[roots]
    for loop in loops:
        selected=np.searchsorted(roots,loop);normals=directions[selected];axis=normalise(normals.mean(axis=0));cosine=normals@axis
        require(np.all(cosine>.5),'An attachment root does not share a coherent support hemisphere.')
        movement=delta[selected].mean(axis=0)
        correction=min(0.,float(np.min(np.sum((delta[selected]-movement)*normals,axis=1)/cosine)))
        delta[selected]=movement+axis*correction
    require(np.linalg.norm(delta,axis=1).max()<=maximum_shift_metres,'An attachment root is outside its declared support envelope.')
    offsets=np.zeros_like(points)
    support_normals=np.zeros_like(points)
    for number, loop in enumerate(loops):
        selected=np.searchsorted(roots,loop)
        region=np.flatnonzero((owners==number)&(field>0))
        squared=np.sum((points[region,None]-points[roots[selected]][None])**2,axis=2)
        influence=np.exp(-(squared-squared.min(axis=1,keepdims=True))/max(widths[number]**2,1e-12))
        influence/=influence.sum(axis=1,keepdims=True)
        offsets[region]=(influence@delta[selected])*field[region,None]
        support_normals[region]=influence@directions[selected]
    offsets[roots]=delta
    changed=points+offsets
    before=points[faces];after=changed[faces]
    original_cross=np.cross(before[:,1]-before[:,0],before[:,2]-before[:,0]);authored_cross=np.cross(after[:,1]-after[:,0],after[:,2]-after[:,0])
    require(np.all(np.sum(original_cross*authored_cross,axis=1)>1e-20),'Attachment root fitting reversed a native triangle.')
    for shape in packet['shapes']:
        for pose in shape['frames']:
            displacement=np.asarray(pose['vertices'],float).reshape(-1,3)@frame[:3,:3].T
            original=(world+displacement)[first][faces];authored=(changed+displacement[first])[faces]
            old_cross=np.cross(original[:,1]-original[:,0],original[:,2]-original[:,0])
            new_cross=np.cross(authored[:,1]-authored[:,0],authored[:,2]-authored[:,0])
            require(np.all(np.sum(old_cross*new_cross,axis=1)>1e-20),'Attachment root fitting reversed a stored physical frame.')
    inverse=np.linalg.inv(frame);local=changed[groups]@inverse[:3,:3].T+inverse[:3,3]
    normals=normalise(np.asarray(packet['normals']).reshape(-1,3)@np.linalg.inv(frame[:3,:3]))
    mass=field[groups,None];aligned=normalise(support_normals)[groups]
    normals=normalise(normals*(1-mass)+aligned*mass)@frame[:3,:3];normals=normalise(normals)
    tangents=np.asarray(packet['tangents']).reshape(-1,4).copy();t=tangents[:,:3]-np.sum(tangents[:,:3]*normals,axis=1)[:,None]*normals
    fallback=np.linalg.norm(t,axis=1)<1e-10
    if fallback.any():t[fallback]=np.cross(np.eye(3)[np.abs(normals[fallback]).argmin(axis=1)],normals[fallback])
    tangents[:,:3]=normalise(t)
    candidate=copy.deepcopy(packet);candidate['vertices']=local.astype(np.float32).ravel().tolist()
    candidate['normals']=normals.astype(np.float32).ravel().tolist();candidate['tangents']=tangents.astype(np.float32).ravel().tolist()
    outside=field[groups]==0
    for key,values in(('vertices',local),('normals',normals),('tangents',tangents)):
        original=np.asarray(packet[key]).reshape(-1,values.shape[1]);output=np.asarray(candidate[key]).reshape(original.shape)
        output[outside]=original[outside];candidate[key]=output.ravel().tolist()
    candidate['bounds']=physical_mesh_bounds(candidate);candidate['sourceFingerprint']=fingerprint(candidate)[0]
    return candidate,dict(rootLoops=len(loops),rootVertices=len(roots),supportSurfaces=len(supports),rootDepthMetres=depth_metres,
        falloffMetres=float(np.median(widths)),rootFalloffMetres=widths.tolist(),maximumRootShiftMm=float(np.linalg.norm(delta,axis=1).max()*1000),
        originalTopologyUvSkinAndUnownedVerticesExact=True,nativePhysicalFrameChannelsExact=True,rootField=field[groups].tolist())


def trim_attachment_collar(packet,frame,radii):
    """Remove the baked flesh flange while retaining native vertex, UV and pose channels."""
    require(np.isfinite(radii)and radii>0,'Attachment collar trimming requires a positive native root radius.')
    topology=rigid_attachment_topology(packet,frame);distance=topology['distance'][topology['groups']]
    limit=topology['radius']*radii;candidate=copy.deepcopy(packet);removed=0
    for slot,indices in enumerate(packet['subMeshes']):
        triangles=np.asarray(indices,int).reshape(-1,3);keep=np.all(distance[triangles]>=limit,axis=1)
        require(keep.any(),'Attachment collar trimming removed a complete material surface.')
        candidate['subMeshes'][slot]=triangles[keep].ravel().tolist();removed+=int((~keep).sum())
    require(removed>0,'The declared attachment collar does not contain source triangles.')
    retained=rigid_attachment_topology(candidate,frame)
    require(len(retained['loops'])==len(topology['loops']),'Attachment collar trimming changed the number of actual root loops.')
    candidate['sourceFingerprint']=fingerprint(candidate)[0]
    return candidate,dict(removedTriangles=removed,trimDistanceMetres=limit,nativeVertexUvSkinAndPhysicalFrameChannelsExact=True,
        retainedTriangleOrderExact=True,rootLoops=len(retained['loops']))


def refine_attachment_surface(packet,frame,supports,profile,lod):
    """Use the shared root fit and native curvature transport for an attachment LOD."""
    counts=profile['passesPerLod']
    require(type(lod)is int and lod in(0,1,2)and len(counts)==3 and all(type(value)is int and 0<=value<=3 for value in counts),
        'An attachment requires bounded refinement passes for every native detail level.')
    trim=None
    if profile.get('trimRootRadii',0):packet,trim=trim_attachment_collar(packet,frame,profile['trimRootRadii'])
    candidate,root=fit_rigid_attachment_roots(packet,frame,supports,depth_metres=profile['rootDepthMetres'],
        falloff_radii=profile['falloffRadii'],maximum_shift_metres=profile['maximumRootShiftMetres'])
    if trim:root['collarTrim']=trim
    passes=[]
    for _ in range(counts[lod]):
        candidate,_,proof=refine_skin_packet(candidate,np.asarray(frame).T.ravel().tolist(),lod,profile=profile['surface']);passes.append(proof)
    require(candidate['vertexCount']<=profile['maximumVerticesPerLod'][lod],'An attachment exceeds its declared detail-level vertex budget.')
    return candidate,dict(root=root,passes=passes)


def indexed_surface_uv_metrics(packet: 'dict[str, Any]',skin: 'dict[str, Any]',slot,shape,feather,sampler,vertex_field=None,bone_domain=None,additional_domains=None,triangle_field=None,sample_receiver=None):
    import heapq
    """Actual native slot sample support, source seam closure and rest metric."""
    h,w=shape
    require(sample_receiver is None or callable(sample_receiver),'A source surface sample receiver must be callable.')
    filter_mode=sampler.get('filterMode')
    require(filter_mode is None or type(filter_mode)is int and filter_mode in(0,1,2),'Unknown native texture filter mode.')
    if filter_mode is not None:
        require(type(sampler.get('anisoLevel',1))is int and 0<=sampler.get('anisoLevel',1)<=1,
                'An anisotropic source requires its actual sampling footprint.')
        require(all(type(sampler[key])is int and sampler[key]in(0,1,2,3)for key in('wrapU','wrapV')),
                'Unknown native texture wrap mode.')
    radius=0. if filter_mode is None else .5 if filter_mode==0 else 1.
    positions=np.asarray(packet['vertices'],dtype='<f4').reshape(-1,3)
    transform=np.asarray(skin['rendererToPrefab']).reshape(4,4).T
    world=positions@transform[:3,:3].T+transform[:3,3]
    uvrow=next(u for u in packet['uv'] if u['channel']==0)
    uv=np.asarray(uvrow['values']).reshape(-1,4)[:,:2]
    material=skin['materials'][slot]
    prop=next(p for p in material['properties'] if p['name']=='_MainTex')
    uv=uv*np.asarray(prop['scale'])+np.asarray(prop['offset'])
    triangles=np.asarray(packet['subMeshes'][slot],dtype=int).reshape(-1,3)
    triangle_values=None if triangle_field is None else np.asarray(triangle_field,float)
    require(triangle_values is None or triangle_values.shape==(len(triangles),)and np.isfinite(triangle_values).all()and
            np.all((triangle_values>=0)&(triangle_values<=1)),'Invalid declared native triangle field.')
    outside=[bool(np.min(uv[triangles,axis]) < -1e-6 or np.max(uv[triangles,axis]) > 1+1e-6) for axis in (0,1)]
    require(all(not outside[axis] or sampler['wrapU' if axis==0 else 'wrapV']==0 for axis in (0,1)), 'Periodic source must retain its actual Repeat sampler.')
    tile_instances=[]
    for ordinal,tri in enumerate(triangles):
        low=np.floor(uv[tri].min(axis=0)+1e-6).astype(int);high=np.floor(uv[tri].max(axis=0)-1e-6).astype(int)
        low=np.minimum(low,high)
        for u in range(int(low[0]),int(high[0])+1):
            for v in range(int(low[1]),int(high[1])+1):tile_instances.append((ordinal,tri,np.array([u,v])))
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
    require(sum(value is not None for value in(bone_domain,vertex_field,triangle_field))<=1,'A source surface must have one ownership evaluator.')
    if bone_domain is not None:
        require(isinstance(bone_domain,dict)and set(bone_domain)=={'bones','declaration'},'Unknown source bone domain.')
    if vertex_field is not None:
        vertex_weight=np.asarray(vertex_field,dtype=float)
        require(vertex_weight.shape==(len(positions),) and np.all(np.isfinite(vertex_weight)) and np.all((vertex_weight>=0)&(vertex_weight<=1)), 'Invalid declared native vertex field.')
    require(additional_domains is None or isinstance(additional_domains,dict)and all(
        isinstance(name,str)and isinstance(domain,dict)and set(domain)=={'bones','declaration'}for name,domain in additional_domains.items()),
        'Unknown additional source bone domains.')
    extra_ownership={name:np.zeros(shape)for name in additional_domains or {}}
    extra_preparation={name:{}for name in additional_domains or {}}
    ownership=np.zeros(shape);metric=np.zeros(shape);anisotropy=np.zeros(shape);domain_preparation={}
    covered=np.zeros(shape,dtype=bool);overlaps=0;physical_triangles=0
    for ordinal,tri,tile in tile_instances:
        tex=np.column_stack(((uv[tri,0]-tile[0])*w-.5,(1-(uv[tri,1]-tile[1]))*h-.5))
        delta=np.stack((tex[1]-tex[0],tex[2]-tex[0]),axis=1)
        determinant=np.linalg.det(delta)
        if abs(determinant)<1e-10:continue
        jacobian=np.stack((world[tri[1]]-world[tri[0]],world[tri[2]]-world[tri[0]]),axis=1)@np.linalg.inv(delta)
        singular=np.linalg.svd(jacobian,compute_uv=False)
        if singular[-1]<=0:continue
        density=float(np.sqrt(np.prod(singular)))
        ratio=float(singular[0]/singular[-1])
        # The sampler reads neighbouring texels around u*size-.5. Intersect
        # that fetch footprint with the indexed source triangle, retaining a
        # source barycentric witness rather than dilating the finished mask.
        halo=int(np.ceil(radius))
        lo=np.maximum(-halo,np.ceil(tex.min(axis=0)-radius).astype(int))
        hi=np.minimum((w-1+halo,h-1+halo),np.floor(tex.max(axis=0)+radius).astype(int))
        if np.any(hi<lo):continue
        yy,xx=np.mgrid[lo[1]:hi[1]+1,lo[0]:hi[0]+1]
        ab=np.stack((xx-tex[0,0],yy-tex[0,1]),axis=-1)@np.linalg.inv(delta).T
        bary=np.dstack((1-ab.sum(axis=2),ab))
        inside=np.all(bary>=-1e-7,axis=2)
        if filter_mode is not None:
            # Interior centres retain the existing sample. For gutter texels,
            # the intersection polygon vertices consist of triangle vertices,
            # box corners and edge/box-line crossings. Their mean lies on the
            # actual source surface, including subpixel triangles.
            flat=bary.reshape(-1,3);centres=np.column_stack((xx.ravel(),yy.ravel()))
            outside_ids=np.flatnonzero(~inside.ravel());inverse=np.linalg.inv(delta)
            for start in range(0,len(outside_ids),4096):
                ids=outside_ids[start:start+4096];centre=centres[ids];total=np.zeros((len(ids),3));count=np.zeros(len(ids),int)
                for corner in range(3):
                    valid=np.all(np.abs(tex[corner]-centre)<=radius,axis=1)
                    total[valid,corner]+=1;count[valid]+=1
                for dx in(-radius,radius):
                    for dy in(-radius,radius):
                        query=centre+np.array([dx,dy]);value=(query-tex[0])@inverse.T
                        value=np.column_stack((1-value.sum(axis=1),value));valid=np.all(value>=-1e-10,axis=1)
                        total[valid]+=value[valid];count[valid]+=1
                for first,last in((0,1),(1,2),(2,0)):
                    edge=tex[last]-tex[first]
                    for axis in(0,1):
                        if abs(edge[axis])<=1e-15:continue
                        for sign in(-1,1):
                            fraction=(centre[:,axis]+sign*radius-tex[first,axis])/edge[axis]
                            query=tex[first]+fraction[:,None]*edge
                            valid=(fraction>=0)&(fraction<=1)&np.all(np.abs(query-centre)<=radius+1e-12,axis=1)
                            total[valid,first]+=1-fraction[valid];total[valid,last]+=fraction[valid];count[valid]+=1
                valid=count>0
                if not np.any(valid):continue
                selected=ids[valid];value=np.maximum(0,total[valid]/count[valid,None]);value/=value.sum(axis=1)[:,None]
                query=value@tex
                roundoff=32*np.finfo(float).eps*max(1.,float(np.max(np.abs(tex))))
                contributes=(np.all(np.floor(query+.5).astype(int)==centres[selected],axis=1)if filter_mode==0 else
                    np.all(np.abs(query-centres[selected])<1.-roundoff,axis=1))
                selected=selected[contributes];flat[selected]=value[contributes];inside.ravel()[selected]=True
            # The centre test tolerance must not produce points outside the
            # native triangle when they are passed to the ownership evaluator.
            flat[inside.ravel()]=np.maximum(0,flat[inside.ravel()]);flat[inside.ravel()]/=flat[inside.ravel()].sum(axis=1)[:,None]
        if not np.any(inside):continue
        rows=yy[inside];cols=xx[inside]
        interpolated=(np.full(int(np.count_nonzero(inside)),triangle_values[ordinal])if triangle_values is not None else
            source_material_ownership(packet,bone_domain['bones'],bone_domain['declaration'],
            renderer_to_prefab=skin['rendererToPrefab'],sample_triangle=tri,sample_barycentric=bary[inside],_prepared=domain_preparation)
            if bone_domain is not None else np.clip(bary[inside]@vertex_weight[tri],0,1))
        extra_interpolated={name:source_material_ownership(packet,domain['bones'],domain['declaration'],
            renderer_to_prefab=skin['rendererToPrefab'],sample_triangle=tri,sample_barycentric=bary[inside],_prepared=extra_preparation[name])
            for name,domain in(additional_domains or {}).items()}
        if filter_mode is not None:
            for values,size,mode in((cols,w,sampler['wrapU']),(rows,h,sampler['wrapV'])):
                if mode==0:values[:]=values%size
                elif mode==1:np.clip(values,0,size-1,out=values)
                elif mode==2:
                    values[:]=values%(2*size);values[:]=np.where(values<size,values,2*size-1-values)
                else:values[:]=np.minimum(size-1,np.where(values>=0,values,-values-1))
            if sample_receiver is not None:
                # Keep each actual footprint witness until its consumer has
                # resolved native coordinates; a UV address can have several owners.
                sample_receiver(ordinal,rows.copy(),cols.copy(),bary[inside].copy(),interpolated.copy(),density)
            # Repeat/Clamp can map several footprint witnesses onto one texel.
            # Reduce them before the shared source-overlap minimum operation.
            addresses=rows*w+cols;unique,reduce=np.unique(addresses,return_inverse=True)
            values=np.full(len(unique),np.inf);np.minimum.at(values,reduce,interpolated)
            rows=unique//w;cols=unique%w;interpolated=values
            for name,value in extra_interpolated.items():
                reduced=np.full(len(unique),np.inf);np.minimum.at(reduced,reduce,value);extra_interpolated[name]=reduced
        elif sample_receiver is not None:
            sample_receiver(ordinal,rows.copy(),cols.copy(),bary[inside].copy(),interpolated.copy(),density)
        repeat=covered[rows,cols]
        overlaps+=int(repeat.sum())
        ownership[rows,cols]=np.where(repeat,np.minimum(ownership[rows,cols],interpolated),interpolated)
        for name,value in extra_interpolated.items():
            extra_ownership[name][rows,cols]=np.where(repeat,np.minimum(extra_ownership[name][rows,cols],value),value)
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
                sampledSupport=('legacy texel centres'if filter_mode is None else
                    'native Point footprint'if filter_mode==0 else'native Bilinear footprint at this mip'),
                boundaryRule=('declared native triangle field; shared sampled support retains minimum owning value'if triangle_values is not None else
                    'source anatomy at actual triangle samples; shared UV coverage retains minimum owning value'if bone_domain is not None else
                    'original indexed topology; no split-vertex weld with omitted expression frames; native indexed open boundary fade' if vertex_field is None else 'declared native vertex field; shared UV coverage retains minimum owning value'))
    result=(ownership,metric,report,covered)
    if additional_domains is not None:
        rule='source anatomy at actual triangle samples; shared UV coverage retains minimum owning value'
        result+=({name:dict(ownership=value,report=dict(report,boundaryRule=rule))for name,value in extra_ownership.items()},)
    return result


def source_surface_junction(first,first_skin,first_slot,second,second_skin,second_slot,first_shape,second_shape,tolerance=.001,*,split_uv=False):
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
        third=pixels[2]-pixels[0]
        height=abs(line[0]*third[1]-line[1]*third[0])/max(np.linalg.norm(line),1e-20)
        return np.asarray([[((1-t)*p[0]+t*p[1])*(1-min(.2,depth/max(height,1e-20)))+p[2]*min(.2,depth/max(height,1e-20)) for depth in (.5,1.5,2.5)] for t in (.25,.5,.75)])
    if split_uv:
        require(first is second and first_slot==second_slot,'A split-UV junction must retain one source Mesh and material slot.')
        points=np.asarray(first['vertices'],dtype=np.float32).reshape(-1,3)
        frame=art.matrix(first_skin['rendererToPrefab'])
        require(np.isfinite(frame).all()and np.array_equal(frame,art.matrix(second_skin['rendererToPrefab']))and
                first_skin['bones']==second_skin['bones'],'A split-UV junction changed its native renderer frame or palette.')
        rest_points=points@frame[:3,:3].T+frame[:3,3]
        triangles=np.asarray(first['subMeshes'][first_slot],int).reshape(-1,3)
        uv=texture_uv(first,first_skin,first_slot)
        require(np.array_equal(uv,texture_uv(second,second_skin,second_slot)),
                'A split-UV junction changed its sampled material coordinates.')
        precision=8*np.finfo(np.float32).eps*max(1.,float(np.max(np.abs(points))))
        weights=np.asarray(first['weights'],float).reshape(-1,4);indices=np.asarray(first['boneIndices'],int).reshape(-1,4)
        bones=first_skin['bones'];position_fields=[points]
        for shape in first.get('shapes',[]):
            for frame in shape['frames']:position_fields.append(np.asarray(frame['vertices'],float).reshape(-1,3))
        signatures=[]
        for index in range(len(points)):
            skin={}
            for bone,weight in zip(indices[index],weights[index]):
                if weight>0:
                    name=bones[int(bone)];skin[name]=skin.get(name,0.)+weight
            semantic=tuple(sorted((name,int(np.rint(value/np.finfo(np.float32).eps)))for name,value in skin.items()))
            signatures.append((tuple(tuple(np.rint(field[index]/precision).astype(np.int64))for field in position_fields),semantic))
        parents=np.arange(len(triangles));edge_rows=defaultdict(list)
        def root(index):
            while parents[index]!=index:parents[index]=parents[parents[index]];index=int(parents[index])
            return index
        for ordinal,triangle in enumerate(triangles):
            for a,b,c in ((triangle[0],triangle[1],triangle[2]),(triangle[1],triangle[2],triangle[0]),(triangle[2],triangle[0],triangle[1])):
                edge_rows[tuple(sorted((int(a),int(b))))].append((int(a),int(b),int(c),ordinal))
        for edges in edge_rows.values():
            for edge in edges[1:]:parents[root(edge[3])]=root(edges[0][3])
        groups=defaultdict(list)
        for edges in edge_rows.values():
            if len(edges)==1:
                edge=edges[0];groups[tuple(sorted((signatures[edge[0]],signatures[edge[1]])))].append(edge)
        pairs=[];ambiguous=0;hard_normals=0
        for edges in groups.values():
            if len(edges)<2:continue
            if len(edges)!=2:ambiguous+=1;continue
            a,b=edges
            if signatures[a[0]]!=signatures[b[0]]:b=(b[1],b[0],b[2],b[3])
            if np.array_equal(uv[list(a[:2])],uv[list(b[:2])]):continue
            require('normals'in first,'A paired source UV cut requires its native normals.')
            normal=np.asarray(first['normals'],float).reshape(-1,3)
            fields=[normal]
            fields.extend(normal+np.asarray(frame['normals'],float).reshape(-1,3)for shape in first.get('shapes',[])for frame in shape['frames'])
            continuous=True
            for field in fields:
                left=normalise(field[list(a[:2])]);right=normalise(field[list(b[:2])])
                if np.min(np.sum(left*right,axis=1))<1-8*np.finfo(np.float32).eps:continuous=False;break
            if not continuous:hard_normals+=1;continue
            length=float(np.linalg.norm(rest_points[a[1]]-rest_points[a[0]]))
            if length<=0:continue
            pairs.append(dict(firstEdge=list(a[:3]),secondEdge=list(b[:3]),firstTriangle=a[3],secondTriangle=b[3],
                firstComponent=root(a[3]),secondComponent=root(b[3]),lengthMetres=length,
                firstUv=inside_uv(a[:3],uv,first_shape).tolist(),secondUv=inside_uv(b[:3],uv,second_shape).tolist(),
                firstCutUv=[((1-t)*uv[a[0]]+t*uv[a[1]]).tolist()for t in(.25,.5,.75)],
                secondCutUv=[((1-t)*uv[b[0]]+t*uv[b[1]]).tolist()for t in(.25,.5,.75)]))
        return dict(pairs=pairs,splitUv=True,ambiguousPhysicalEdgeGroups=ambiguous,hardNormalEdgesRetained=hard_normals,
            sourceFloat32PositionTolerance=precision,sourceSkinAndAllStoredFramesRequired=True,
            components=[root(index)for index in range(len(triangles))],geometryChanged=False)
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


def source_native_closure(triangles,pairs):
    import collections
    rows=collections.defaultdict(list);result={}
    for ordinal,triangle in enumerate(triangles):
        for a,b in zip(triangle,np.roll(triangle,-1)):
            rows[frozenset((int(a),int(b)))].append(ordinal)
    for edge,owners in rows.items():
        if len(owners)>2:raise ValueError('Unresolved nonmanifold native edge.')
        if len(owners)==2:
            for first,last in((owners[0],owners[1]),(owners[1],owners[0])):
                result[(first,edge)]=(last,{value:value for value in edge})
    for row in pairs:
        a,b=row['firstEdge'][:2];c,d=row['secondEdge'][:2]
        for first,own,last,peer in((row['firstTriangle'],(a,b),row['secondTriangle'],(c,d)),
                                   (row['secondTriangle'],(c,d),row['firstTriangle'],(a,b))):
            key=(first,frozenset(own))
            if key in result:raise ValueError('Unresolved conflicting native peer.')
            result[key]=(last,dict(zip(own,peer,strict=True)))
    return result


def source_native_unfold(points,triangles,neighbours,current,layout,edge):
    """Carry an indexed or verified peer edge into its adjacent native triangle."""
    triangle=triangles[current];edge=list(edge);a,b=map(int,edge)
    connection=neighbours.get((current,frozenset(edge)))
    if connection is None:raise ValueError('Native fan is not closed.')
    following,mapping=connection;other=triangles[following]
    peer_a,peer_b=mapping[a],mapping[b]
    c=next(int(value)for value in other if value not in(peer_a,peer_b))
    at={int(value):index for index,value in enumerate(triangle)}
    corner=next(index for index,value in enumerate(triangle)if value not in(a,b))
    origin=layout[at[a]];vector=layout[at[b]]-origin;length=np.linalg.norm(vector)
    relative=points[c]-points[peer_a];native_edge=points[peer_b]-points[peer_a]
    if not np.isclose(np.linalg.norm(native_edge),length,rtol=0,atol=1e-12):
        raise ValueError('Native fan peer has different edge length.')
    along=np.dot(relative,native_edge)/length;height2=np.dot(relative,relative)-along*along
    if height2<=0:raise ValueError('Native fan triangle is collapsed.')
    tangent=vector/length;normal=np.array([-tangent[1],tangent[0]])
    side=np.sign(np.dot(layout[corner]-origin,normal))
    if side==0:raise ValueError('Native fan side is ambiguous.')
    locations={peer_a:origin,peer_b:layout[at[b]],c:origin+tangent*along-normal*side*np.sqrt(height2)}
    return following,np.array([locations[int(value)]for value in other]),mapping


def source_native_complete_fan(points,triangles,neighbours,seed,initial,vertex,*,_reserve=None):
    """Verify the entire incident native cycle before using either limit."""
    if _reserve is not None:_reserve(4096)
    current=seed;layout=np.asarray(initial,float);centre=int(vertex)
    edge=(centre,next(int(value)for value in triangles[seed]if value!=centre))
    visited=set()
    for _ in range(len(triangles)+1):
        if current in visited:raise ValueError('Native fan repeats before closing at its source.')
        if _reserve is not None:_reserve(512)
        visited.add(current)
        following,next_layout,mapping=source_native_unfold(points,triangles,neighbours,current,layout,edge)
        entered=frozenset(mapping[value]for value in edge);centre=mapping[centre]
        current=following;layout=next_layout
        if current==seed:
            if centre!=vertex:raise ValueError('Native fan closes on another source vertex.')
            return visited
        edge=next((int(centre),int(value))for value in triangles[current]
            if value!=centre and frozenset((int(centre),int(value)))!=entered)
    raise ValueError('Native fan is not completely traversed.')


def source_native_fan_limits(points,triangles,neighbours,seed,initial,vertex,query,*,_reserve=None):
    """Resolve both angular limits through a complete qualified native vertex fan."""
    if _reserve is not None:_reserve(4096)
    initial=np.asarray(initial,float);query=np.asarray(query,float)
    at=int(np.flatnonzero(triangles[seed]==vertex)[0]);origin=initial[at]
    if np.linalg.norm(query-origin)==0:raise ValueError('Native fan direction is absent.')
    edges=[(int(vertex),int(value))for value in triangles[seed]if value!=vertex]
    results=[]
    for start_edge in edges:
        current=seed;layout=initial.copy();centre=int(vertex);edge=start_edge;visited=set();walk=[]
        for _ in range(len(triangles)+1):
            signature=(current,centre,frozenset(edge))
            if signature in visited:raise ValueError('Native fan direction has no outgoing wedge.')
            if _reserve is not None:_reserve(512)
            visited.add(signature)
            following,next_layout,mapping=source_native_unfold(points,triangles,neighbours,current,layout,edge)
            entered=frozenset(mapping[value]for value in edge);centre=mapping[centre]
            current=following;layout=next_layout;walk.append(int(current))
            triangle=triangles[current];corner=int(np.flatnonzero(triangle==centre)[0])
            inverse=np.linalg.inv(np.stack((layout[1]-layout[0],layout[2]-layout[0]),axis=1))
            target=inverse@(query-origin);direction=np.r_[-target.sum(),target]
            others=[index for index in range(3)if index!=corner]
            if min(direction[others])>=-1e-10:
                # The full query may extend beyond this one triangle. The caller can
                # continue this limit using the ordinary native ray walker.
                results.append(dict(triangle=int(current),vertex=int(centre),layout=layout,
                    origin=layout[corner],walk=walk))
                break
            edge=next((int(centre),int(value))for value in triangle
                if value!=centre and frozenset((int(centre),int(value)))!=entered)
        else:raise ValueError('Native fan traversal is incomplete.')
    return results


def source_native_ray(points,triangles,neighbours,seed,initial,centre,query,*,unresolved_edges=(),_fan_visits=frozenset(),_reserve=None):
    if _reserve is not None:_reserve(4096)
    layout=np.asarray(initial,float).copy();position=np.asarray(centre,float);query=np.asarray(query,float)
    current=seed;visited=set()
    for hop in range(2*len(triangles)+1):
        if _reserve is not None:_reserve(1024)
        triangle=triangles[current]
        inverse=np.linalg.inv(np.stack((layout[1]-layout[0],layout[2]-layout[0]),axis=1))
        delta=query-layout[0]
        target=np.array([delta[0]*inverse[0,0]+delta[1]*inverse[0,1],delta[0]*inverse[1,0]+delta[1]*inverse[1,1]])
        target=np.r_[1-target.sum(),target]
        if min(target)>=-1e-10:
            return dict(state='source',triangle=int(current),bary=target,point=target@points[triangle],hops=hop)
        start=inverse@(position-layout[0]);start=np.r_[1-start.sum(),start]
        # The existing barycentric boundary contract also applies while
        # crossing. A ray on an edge does not exit it at amount zero merely
        # because a cancelled barycentric coordinate has a tiny negative sign.
        collinear=(np.abs(start)<=1e-10)&(np.abs(target)<=1e-10)
        crossing=[]
        for corner in np.flatnonzero((target<0)&~collinear):
            amount=start[corner]/(start[corner]-target[corner])
            if amount>=-1e-10:crossing.append((max(0.,amount),int(corner)))
        if not crossing:return dict(state='unresolved',reason='no-forward-native-crossing',hops=hop)
        crossing.sort();amount,corner=crossing[0]
        if collinear.any()or len(crossing)>1 and abs(crossing[1][0]-amount)<1e-12:
            opposite={value[1]for value in crossing if abs(value[0]-amount)<1e-12}
            opposite.update(np.flatnonzero(collinear).tolist())
            remaining=[index for index in range(3)if index not in opposite]
            if len(remaining)!=1:return dict(state='unresolved',reason='native-vertex-fan-needs-disambiguation',hops=hop)
            vertex=int(triangle[remaining[0]])
            point=position+(query-position)*amount
            signature=tuple(np.round(points[vertex],14))
            if signature in _fan_visits:return dict(state='unresolved',reason='native-fan-transport-cycle',hops=hop)
            try:
                closed=source_native_complete_fan(points,triangles,neighbours,current,layout,vertex,_reserve=_reserve)
                limits=source_native_fan_limits(points,triangles,neighbours,current,layout,vertex,query,_reserve=_reserve)
            except ValueError as error:
                return dict(state='unresolved',reason=str(error),hops=hop)
            if any((ordinal,edge)in unresolved_edges for ordinal in closed for edge in
                   (frozenset((int(a),int(b)))for a,b in zip(triangles[ordinal],np.roll(triangles[ordinal],-1)))
                   if vertex in edge):
                return dict(state='unresolved',reason='native-peer-closure-incomplete',hops=hop)
            if _reserve is not None:_reserve(1024)
            results=[source_native_ray(points,triangles,neighbours,value['triangle'],value['layout'],value['origin'],query,
                unresolved_edges=unresolved_edges,_fan_visits=_fan_visits|{signature},_reserve=_reserve)for value in limits]
            if any(value['state']=='unresolved'for value in results):
                return dict(state='unresolved',reason='native-fan-limit-is-unresolved',limits=results,hops=hop)
            samples=[];outside_weight=0.
            for value in results:
                if _reserve is not None:_reserve(512+512*len(value.get('samples',())))
                if value['state']=='source':samples.append(dict(triangle=value['triangle'],bary=value['bary'],point=value['point'],weight=.5))
                elif value['state']=='source-stencil':
                    samples.extend(dict(sample,weight=sample['weight']*.5)for sample in value['samples'])
                    outside_weight+=.5*value['outsideWeight']
                elif value['state']=='outside':outside_weight+=.5
            if not samples:return dict(state='outside',nativeFanVertex=vertex,hops=hop)
            return dict(state='source-stencil',samples=samples,outsideWeight=outside_weight,
                nativeFanVertex=vertex,closedFanTriangles=sorted(closed),hops=hop,
                interpretation='two-sided symmetric native transport limits')
        a,b=[int(triangle[index])for index in range(3)if index!=corner]
        key=(current,frozenset((a,b)))
        if key in unresolved_edges:return dict(state='unresolved',reason='native-peer-closure-incomplete',hops=hop)
        connection=neighbours.get(key)
        if connection is None:return dict(state='outside',nativeBoundary=list(map(int,(a,b))),hops=hop)
        position=position+(query-position)*amount
        signature=(key,tuple(np.round(position,14)))
        if signature in visited:return dict(state='unresolved',reason='native-transport-cycle',hops=hop)
        visited.add(signature)
        following,mapping=connection;other=triangles[following]
        peer_a,peer_b=mapping[a],mapping[b];c=next(int(value)for value in other if value not in(peer_a,peer_b))
        at={int(value):index for index,value in enumerate(triangle)}
        origin=layout[at[a]];edge=layout[at[b]]-origin;length=np.linalg.norm(edge)
        relative=points[c]-points[peer_a];source_edge=points[peer_b]-points[peer_a]
        if not np.isclose(np.linalg.norm(source_edge),length,rtol=0,atol=1e-12):
            return dict(state='unresolved',reason='native-peer-length-differs',hops=hop)
        along=np.dot(relative,source_edge)/length;perpendicular=np.dot(relative,relative)-along*along
        if perpendicular<=0:return dict(state='unresolved',reason='native-triangle-collapsed',hops=hop)
        tangent=edge/length;normal=np.array([-tangent[1],tangent[0]])
        side=np.sign(np.dot(layout[corner]-origin,normal))
        if side==0:return dict(state='unresolved',reason='native-edge-side-ambiguous',hops=hop)
        vertices={peer_a:origin,peer_b:layout[at[b]],c:origin+tangent*along-normal*side*np.sqrt(perpendicular)}
        layout=np.array([vertices[int(value)]for value in other]);current=following
    return dict(state='unresolved',reason='finite-native-traversal-not-complete',hops=2*len(triangles)+1)


def source_native_rays(points, triangles, neighbours, seed, initial, centre, queries,
                *, unresolved_edges=(), retain_layouts=False, _frame=None, _unfold=None, _work_report=None):
    """Same native wavefront decisions, with query arithmetic grouped by hop."""
    import collections,sys
    queries=np.asarray(queries,float);centre=np.asarray(centre,float);count=len(queries)
    limits=source_filter_budget()
    def report_size(value):
        amount=sys.getsizeof(value)
        if isinstance(value,dict):amount+=sum(report_size(key)+report_size(item)for key,item in value.items())
        elif isinstance(value,(list,tuple,set,frozenset)):amount+=sum(report_size(item)for item in value)
        elif isinstance(value,np.ndarray):amount=max(amount,value.nbytes)
        return amount
    if count>limits['nativeQueries']:
        require(count*33<=limits['workBytes']//8,'Native receiver result arrays exceed their metadata work budget.')
        result=dict(states=np.empty(count,np.uint8),triangles=np.empty(count,np.int32),bary=np.empty((count,3)),
            hops=np.empty(count,np.int32),fanSamples={},failures={},groups=0,peakPendingQueries=0,layouts={})
        retained_reports=0
        for first in range(0,count,limits['nativeQueries']):
            part=source_native_rays(points,triangles,neighbours,seed,initial,centre,
                queries[first:first+limits['nativeQueries']],unresolved_edges=unresolved_edges,
                retain_layouts=retain_layouts,_frame=_frame,_unfold=_unfold,_work_report=_work_report)
            last=first+len(part['states'])
            retained_reports+=sum(report_size(part[name])for name in('fanSamples','failures','layouts'))
            require(retained_reports+count*113+limits['nativeWaveBytes']+limits['nativeCacheBytes']+
                limits['workBytes']//8<=limits['workBytes'],'Native receiver reports exceed their temporary work budget.')
            for name in('states','triangles','bary','hops'):result[name][first:last]=part[name]
            for name in('fanSamples','failures'):result[name].update((first+index,value)for index,value in part[name].items())
            for current,values in part['layouts'].items():result['layouts'].setdefault(current,[]).extend(values)
            result['groups']+=part['groups'];result['peakPendingQueries']=max(result['peakPendingQueries'],part['peakPendingQueries'])
        return result
    # Dense arithmetic/temporary views cost at most 795 bytes/q. Measure the
    # installed Python/NumPy object headers for two generations of frames,
    # ragged children and their pointer lists rather than assuming a version.
    frame_work=sys.getsizeof((0,None,None,None))+sys.getsizeof(0)+sum(
        sys.getsizeof(np.empty(shape))for shape in((3,2),(2,2),(3,)))
    ragged_work=sum(sys.getsizeof(np.empty(1,dtype=dtype))for dtype in(np.int64,np.float64,np.int32))+3*np.dtype(np.intp).itemsize
    wave_arrays=count*(795+2*frame_work+ragged_work+2*np.dtype(np.intp).itemsize)+8192
    require(wave_arrays<=limits['nativeWaveBytes'],'Native wave arrays exceed their temporary work budget.')
    if _work_report is not None:_work_report['peakNativeWaveBytes']=max(_work_report.get('peakNativeWaveBytes',0),wave_arrays)
    metadata_bytes=0;retained_layout_bytes=0
    states=np.zeros(count,np.uint8);owners=np.full(count,-1,np.int32);barycentrics=np.zeros((count,3))
    hops=np.zeros(count,np.int32);fan_samples={};failures={};layouts=collections.defaultdict(list)
    indices=np.arange(count);group=np.zeros(count,np.int32);entered=np.zeros(count)
    def direct_frame(current,layout,centre):
        inverse=np.linalg.inv(np.stack((layout[1]-layout[0],layout[2]-layout[0]),axis=1))
        origin=inverse@(centre-layout[0]);origin=np.r_[1-origin.sum(),origin]
        return int(current),layout,inverse,origin
    frame=direct_frame if _frame is None else _frame
    advance=(lambda current,layout,edge:source_native_unfold(points,triangles,neighbours,current,layout,edge))if _unfold is None else _unfold
    frames=[frame(int(seed),np.asarray(initial,float),centre)]
    groups=0;hop=0
    def retain(index,result):
        nonlocal metadata_bytes
        if result['state']not in('source','outside'):
            amount=report_size(result)+128
            if wave_arrays+metadata_bytes+amount>limits['nativeWaveBytes']:
                raise MemoryError('Native ragged reports exceed their temporary work budget.')
            metadata_bytes+=amount
            if _work_report is not None:_work_report['peakNativeWaveBytes']=max(_work_report.get('peakNativeWaveBytes',0),wave_arrays+metadata_bytes)
        hops[index]=result.get('hops',0)
        if result['state']=='source':
            states[index]=1;owners[index]=result['triangle'];barycentrics[index]=result['bary']
        elif result['state']=='outside':states[index]=2
        elif result['state']=='source-stencil':
            states[index]=3;fan_samples[int(index)]=result
        else:states[index]=4;failures[int(index)]=result
    while frames:
        groups+=len(frames)
        if retain_layouts:
            for current,layout,_,_ in frames:
                if isinstance(retain_layouts,(bool,np.bool_))or current in retain_layouts:
                    if retained_layout_bytes+400>limits['workBytes']//8:
                        raise MemoryError('Retained native layouts exceed their metadata work budget.')
                    layouts[current].append(layout.copy());retained_layout_bytes+=400
                    if _work_report is not None:_work_report['peakNativeLayoutBytes']=max(_work_report.get('peakNativeLayoutBytes',0),retained_layout_bytes)
        if hop>2*len(triangles):
            for index in indices:retain(index,dict(state='unresolved',reason='finite-native-traversal-not-complete',hops=hop))
            break
        inverse=np.asarray([frame[2]for frame in frames])[group]
        origins=np.asarray([frame[1][0]for frame in frames])[group]
        origin=np.asarray([frame[3]for frame in frames])[group]
        current=np.asarray([frame[0]for frame in frames],np.int32)[group]
        delta=queries[indices]-origins
        target=np.empty((len(indices),2))
        target[:,0]=delta[:,0]*inverse[:,0,0]+delta[:,1]*inverse[:,0,1]
        target[:,1]=delta[:,0]*inverse[:,1,0]+delta[:,1]*inverse[:,1,1]
        target=np.c_[1-target.sum(1),target]
        inside=np.min(target,axis=1)>=-1e-10
        selected=indices[inside];states[selected]=1;owners[selected]=current[inside]
        barycentrics[selected]=target[inside];hops[selected]=hop
        indices=indices[~inside];target=target[~inside];group=group[~inside];entered=entered[~inside];origin=origin[~inside]
        if not len(indices):break
        collinear=(np.abs(origin)<=1e-10)&(np.abs(target)<=1e-10)
        roots=np.divide(origin,origin-target,out=np.full_like(target,np.inf),
            where=(origin!=target)&(target<0)&~collinear)
        valid=(target<0)&(roots>=entered[:,None]-1e-10)&(roots>=-1e-10)
        roots=np.where(valid,np.maximum(0.,roots),np.inf)
        corner=np.argmin(roots,axis=1);ordered=np.sort(roots,axis=1);amount=ordered[:,0]
        missing=~np.isfinite(amount)
        ties=(ordered[:,1]-amount<1e-12)&~missing
        ties|=np.any(collinear,axis=1)&~missing
        for offset in np.flatnonzero(missing|ties):
            current,layout,_,_=frames[int(group[offset])]
            start=centre+(queries[indices[offset]]-centre)*entered[offset]
            charged=[wave_arrays+metadata_bytes]
            def reserve(amount):
                if charged[0]+amount>limits['nativeWaveBytes']:
                    raise MemoryError('Native fan allocations exceed their temporary work budget.')
                charged[0]+=amount
                if _work_report is not None:_work_report['peakNativeWaveBytes']=max(_work_report.get('peakNativeWaveBytes',0),charged[0])
            retain(indices[offset],source_native_ray(points,triangles,neighbours,current,layout,start,
                queries[indices[offset]],unresolved_edges=unresolved_edges,_reserve=reserve))
        propagate=np.flatnonzero(~missing&~ties)
        buckets=group[propagate]*3+corner[propagate]
        order=np.argsort(buckets,kind='stable');propagate=propagate[order];buckets=buckets[order]
        cuts=np.r_[0,np.flatnonzero(buckets[1:]!=buckets[:-1])+1,len(buckets)]
        next_indices=[];next_entered=[];next_groups=[];next_frames=[]
        for first,last in zip(cuts[:-1],cuts[1:]):
            if first==last:continue
            selected=propagate[first:last];parent=int(buckets[first]//3);at=int(buckets[first]%3)
            current,layout,_,_=frames[parent];own=indices[selected]
            edge=frozenset(int(value)for index,value in enumerate(triangles[current])if index!=at)
            key=(current,edge)
            if key in unresolved_edges:
                for index in own:retain(index,dict(state='unresolved',reason='native-peer-closure-incomplete',hops=hop))
            elif key not in neighbours:
                states[own]=2;hops[own]=hop
            else:
                following,next_layout,_=advance(current,layout,edge)
                next_indices.append(own);next_entered.append(amount[selected])
                next_groups.append(np.full(len(own),len(next_frames),np.int32))
                next_frames.append(frame(following,next_layout,centre))
        if not next_indices:break
        indices=np.concatenate(next_indices);entered=np.concatenate(next_entered);group=np.concatenate(next_groups)
        frames=next_frames;hop+=1
    return dict(states=states,triangles=owners,bary=barycentrics,fanSamples=fan_samples,failures=failures,
        hops=hops,groups=groups,peakPendingQueries=count,layouts=dict(layouts))


def source_uniform_support(profile, spacing, regions):
    settings = {key: profile[key] for key in ('fineSigmaMetres', 'grainSigmaMetres', 'coherenceSigmaMetres')}
    settings['fineCoherenceRetain'] = profile.get('pigmentFineCoherenceRetain', 1.0)
    radius = source_uniform_radius(settings, spacing)
    radius = max(radius, rgb_uniform_radius(profile['featherMetres'] / 3, spacing))
    local = [dict(source=rgb_uniform_radius(value['sourceLowFrequencySigmaMetres'], spacing),
                  reference=rgb_uniform_radius(value['referenceSigmaMetres'], spacing)if 'referenceSigmaMetres'in value else 0)
             for value in regions]
    return dict(globalRadiusPixels=radius, regionalRadiusPixels=local, filterRadiusPixels=max([radius] + [max(value.values()) for value in local]), receiverInterpolationPixels=1, metric='same finite uniform native-prefab metre operator; bounded work, no sigma cap')

def source_canonical_receivers(points, triangles, neighbours, requests):
    """Share a boundary owner only through its exact qualified native edges."""
    canonical = []
    boundary_paths = []
    for ordinal, bary in requests:
        ordinal = int(ordinal)
        bary = np.asarray(bary, float)
        active = np.flatnonzero(bary > 1e-10)
        if len(active) not in (1, 2):
            canonical.append((ordinal, bary))
            continue
        pending = [(ordinal, bary)]
        owners = {ordinal: bary}
        paths = {ordinal: []}
        while pending:
            current, weights = pending.pop()
            triangle = triangles[current]
            support = {int(triangle[index]): weights[index] for index in np.flatnonzero(weights > 1e-10)}
            for a, b in zip(triangle, np.roll(triangle, -1)):
                edge = frozenset((int(a), int(b)))
                if not set(support) <= edge:
                    continue
                adjacent = neighbours.get((current, edge))
                if adjacent is None:
                    continue
                following, mapping = adjacent
                following = int(following)
                if following in owners:
                    continue
                transferred = {int(mapping[vertex]): weight for vertex, weight in support.items()}
                value = np.array([transferred.get(int(vertex), 0.0) for vertex in triangles[following]])
                owners[following] = value
                paths[following] = paths[current] + [(current, edge)]
                pending.append((following, value))
        owner = min(owners)
        if np.linalg.norm(owners[owner] @ points[triangles[owner]] - bary @ points[triangles[ordinal]]) > 1e-10:
            raise ValueError('A qualified native alias changes its original physical receiver point.')
        canonical.append((owner, owners[owner]))
        boundary_paths.append((ordinal, paths[owner]))
    return (canonical, boundary_paths)

def source_receiver_coordinates(points, triangles, neighbours, seed, initial, centre, requests, layouts, *, diagnostics=None, statistics=None, sampler=None):
    count = len(requests)
    if not count:
        return np.empty((0, 2))
    ordinals = np.asarray([row[0] for row in requests], int)
    bary = np.asarray([row[1] for row in requests], float)
    candidates = [None] * count
    native = np.zeros((count, 3))
    allowed = [None] * count
    maximum = 0
    original_queries = 0
    distinct_queries = 0
    for triangle in np.unique(ordinals):
        ids = np.flatnonzero(ordinals == triangle)
        values = bary[ids]
        options = np.asarray(layouts.get(int(triangle), []))
        native[ids] = values @ points[triangles[triangle]]
        if not len(options):
            continue
        queries = np.einsum('ni,kij->nkj', values, options)
        order = np.argsort(np.linalg.norm(queries - centre, axis=2), axis=1, kind='stable')
        patterns = np.sum((values > 1e-10) * [1, 2, 4], axis=1)
        owners_by_pattern = {}
        for pattern in np.unique(patterns):
            active = np.flatnonzero(np.array([pattern & 1, pattern & 2, pattern & 4]) > 0)
            owners = {int(triangle)}
            if len(active) == 2:
                edge = frozenset((int(triangles[triangle, index]) for index in active))
                other = neighbours.get((int(triangle), edge))
                if other is not None:
                    owners.add(int(other[0]))
            elif len(active) == 1:
                pending = [(int(triangle), int(triangles[triangle, active[0]]))]
                visited = set(pending)
                while pending:
                    current, vertex = pending.pop()
                    for other in triangles[current]:
                        if other == vertex:
                            continue
                        connection = neighbours.get((current, frozenset((vertex, int(other)))))
                        if connection is None:
                            continue
                        following, mapping = connection
                        key = (int(following), int(mapping[vertex]))
                        if key not in visited:
                            visited.add(key)
                            pending.append(key)
                owners = {value[0] for value in visited}
            owners_by_pattern[int(pattern)] = owners
        for at, index in enumerate(ids):
            seen = set()
            unique = []
            for query in queries[at, order[at]]:
                identity = (query.dtype.str, query.tobytes())
                if identity not in seen:
                    seen.add(identity)
                    unique.append(query)
            candidates[index] = np.asarray(unique)
            allowed[index] = owners_by_pattern[int(patterns[at])]
            original_queries += len(options)
            distinct_queries += len(unique)
            maximum = max(maximum, len(unique))
    result = np.full((count, 2), np.nan)
    walks = 0
    attempts = {}
    for rank in range(maximum):
        ids = np.asarray([index for index in range(count) if not np.isfinite(result[index]).all() and candidates[index] is not None and (rank < len(candidates[index]))], int)
        if not len(ids):
            continue
        chosen = np.asarray([candidates[index][rank] for index in ids])
        walked = source_native_rays(points, triangles, neighbours, seed, initial, centre, chosen) if sampler is None else sampler.queries(seed, initial, centre, chosen)
        walks += 1
        valid = np.zeros(len(ids), bool)
        ordinary = np.flatnonzero(walked['states'] == 1)
        if len(ordinary):
            actual = np.einsum('ni,nij->nj', walked['bary'][ordinary], points[triangles[walked['triangles'][ordinary]]])
            owners = np.asarray([int(walked['triangles'][at]) in allowed[int(ids[at])] for at in ordinary])
            valid[ordinary] = owners & (np.linalg.norm(actual - native[ids[ordinary]], axis=1) <= 1e-10)
        for at, fan in walked['fanSamples'].items():
            index = int(ids[at])
            valid[at] = any((row['triangle'] in allowed[index] and np.linalg.norm(np.asarray(row['bary']) @ points[triangles[row['triangle']]] - native[index]) <= 1e-10 for row in fan['samples']))
        for at in np.flatnonzero(~valid):
            index = int(ids[at])
            state = int(walked['states'][at])
            fact = dict(query=chosen[at].tolist(), state=state)
            if state == 1:
                owner = int(walked['triangles'][at])
                point = walked['bary'][at] @ points[triangles[owner]]
                fact.update(triangle=owner, pointMetres=point.tolist(), distanceMetres=float(np.linalg.norm(point - native[index])))
            elif state == 3:
                fact['fanSampleTriangles'] = [int(row['triangle']) for row in walked['fanSamples'][int(at)]['samples']]
            elif state == 4:
                fact['reason'] = walked['failures'][int(at)].get('reason')
            attempts.setdefault(index, []).append(fact)
        result[ids[valid]] = chosen[valid]
    missing = np.flatnonzero(~np.isfinite(result).all(1))
    if len(missing):
        facts = []
        for triangle in np.unique(ordinals[missing]):
            indices = missing[ordinals[missing] == triangle]
            index = int(indices[0])
            candidate = candidates[index]
            facts.append(dict(seed=int(seed), receiverTriangle=int(triangle), missingReceivers=len(indices), firstRequestIndex=index, receiverBarycentric=bary[index].tolist(), receiverPointMetres=native[index].tolist(), allowedTriangles=sorted(allowed[index]) if allowed[index] is not None else [], candidateLayouts=0 if candidate is None else len(candidate), candidates=attempts.get(index, [])[:8], reason='no-prepared-native-layout' if candidate is None else 'native-point-round-trip-mismatch', missingRequestIndicesSha256=hashlib.sha256(indices.astype('<u4').tobytes()).hexdigest()))
        if diagnostics is not None:
            diagnostics.extend(facts)
        elif any((fact['reason'] == 'no-prepared-native-layout' for fact in facts)):
            raise ValueError('An owned receiver has no prepared native layout.')
        else:
            raise ValueError('An owned receiver cannot reproduce its native surface correspondence: ' + json.dumps(facts[0], separators=(',', ':')))
    if statistics is not None:
        statistics.update(witnesses=count, rankBatchedWalks=walks, allWitnessesRetained=True, unresolvedWitnesses=len(missing), nativeReceiverTriangles=len(np.unique(ordinals)), originalCandidateQueries=original_queries, distinctCandidateQueries=distinct_queries, identicalCandidateQueriesReused=original_queries - distinct_queries)
    return result

def source_qualified_coordinates(points, triangles, neighbours, seed, initial, centre, requests, layouts, *, diagnostics=None, sampler=None):
    """Give qualified UV aliases one receiver owner before chart evaluation."""
    canonical, boundary_paths = source_canonical_receivers(points, triangles, neighbours, requests)
    expanded = {int(ordinal): list(values) for ordinal, values in layouts.items()}
    needed = {ordinal for ordinal, _ in canonical} | {int(ordinal) for ordinal, _ in requests}
    for ordinal, values in list(layouts.items()):
        triangle = triangles[ordinal]
        for a, b in zip(triangle, np.roll(triangle, -1)):
            edge = frozenset((int(a), int(b)))
            adjacent = neighbours.get((int(ordinal), edge))
            if adjacent is None or int(adjacent[0]) not in needed:
                continue
            for layout in values:
                following, value, _ = (source_native_unfold(points, triangles, neighbours, int(ordinal), layout, edge) if sampler is None else sampler.unfold(int(ordinal), layout, edge))
                entries = expanded.setdefault(int(following), [])
                if not any((np.array_equal(value, existing) for existing in entries)):
                    entries.append(value)
    for ordinal, path in boundary_paths:
        for source_layout in list(expanded.get(ordinal, [])):
            current = ordinal
            value = source_layout
            for expected, edge in path:
                if current != expected:
                    raise ValueError('A qualified native alias path changed its triangle owner.')
                current, value, _ = (source_native_unfold(points, triangles, neighbours, current, value, edge) if sampler is None else sampler.unfold(current, value, edge))
            entries = expanded.setdefault(int(current), [])
            if not any((np.array_equal(value, existing) for existing in entries)):
                entries.append(value)
    return source_receiver_coordinates(points, triangles, neighbours, seed, initial, centre, canonical, expanded, diagnostics=diagnostics, sampler=sampler)

def source_receiver_grid_half(coordinates, centre, spacing, support):
    """Cover the actual receivers and every finite read of the same operator."""
    return np.ceil(np.max(np.abs(np.asarray(coordinates) - centre), axis=0) / spacing).astype(int) + support['filterRadiusPixels'] + support['receiverInterpolationPixels']

def source_neighbourhood_context(packet, skin, slot, shape, sampler, profile, regions, evaluation, *, deadline=None, require_complete=False):
    """Cover every declared source cut; collect only actual stage-owned texels."""
    import heapq, time
    shape = tuple(shape)
    evaluation = np.asarray(evaluation, bool)
    if evaluation.shape != shape:
        raise ValueError('Native filter receiver ownership differs from its texture.')
    if not evaluation.any():
        return ([], dict(pairs=None, patches=0, receivers=0, noWritableOutput=True))
    matches = source_surface_junction(packet, skin, slot, packet, skin, slot, shape, shape, split_uv=True)
    if matches['ambiguousPhysicalEdgeGroups']:
        raise ValueError('A native RGB cut has ambiguous physical peer ownership.')
    pairs = matches['pairs']
    if (not pairs and not require_complete) or not evaluation.any():
        return ([], dict(pairs=len(pairs), patches=0, receivers=0))
    matrix = np.asarray(skin['rendererToPrefab']).reshape(4, 4).T
    points = np.asarray(packet['vertices']).reshape(-1, 3) @ matrix[:3, :3].T + matrix[:3, 3]
    triangles = np.asarray(packet['subMeshes'][slot]).reshape(-1, 3)
    uv = np.asarray(next((value['values'] for value in packet['uv'] if value['channel'] == 0))).reshape(-1, 4)[:, :2]
    prop = next((value for value in skin['materials'][slot]['properties'] if value['name'] == '_MainTex'))
    uv = uv * np.asarray(prop['scale']) + np.asarray(prop['offset'])
    neighbours = source_native_closure(triangles, pairs)
    graph = [{} for _ in points]
    for triangle in triangles:
        for a, b in zip(triangle, np.roll(triangle, -1)):
            graph[int(a)][int(b)] = graph[int(b)][int(a)] = float(np.linalg.norm(points[a] - points[b]))
    for pair in pairs:
        for a, b in zip(pair['firstEdge'][:2], pair['secondEdge'][:2], strict=True):
            graph[a][b] = graph[b][a] = 0.0

    def distances(seeds):
        result = np.full(len(points), np.inf)
        queue = []
        for vertex, value in seeds:
            if value < result[vertex]:
                result[vertex] = value
                heapq.heappush(queue, (float(value), int(vertex)))
        while queue:
            value, vertex = heapq.heappop(queue)
            if value != result[vertex]:
                continue
            for other, length in graph[vertex].items():
                following = value + length
                if following < result[other]:
                    result[other] = following
                    heapq.heappush(queue, (following, other))
        return result
    incident = {}
    for index, pair in enumerate(pairs):
        for side in ('first', 'second'):
            for vertex in pair[side + 'Edge'][:2]:
                incident.setdefault(int(vertex), []).append((pair['lengthMetres'], index, side))
    cut_vertices = np.array(sorted(incident), int)
    covered = np.zeros(len(points), bool)
    contexts = []

    def support_at(spacing, validate):
        if validate:
            return source_uniform_support(profile, spacing, regions)
        valid = max([profile['coherenceSigmaMetres'], profile['featherMetres'] / 3] +
                    [max(region['sourceLowFrequencySigmaMetres'],region.get('referenceSigmaMetres',0.))for region in regions]) / 128
        if spacing >= valid:
            return source_uniform_support(profile, spacing, regions)
        adjusted = source_uniform_support(profile, valid, regions)
        adjusted['filterRadiusPixels'] = int(np.ceil((adjusted['filterRadiusPixels'] + 1) * valid / spacing))
        return adjusted
    while not np.all(covered[cut_vertices]):
        if deadline is not None and time.perf_counter() > deadline:
            return ([], dict(pairs=len(pairs), patches=0, receivers=0, complete=False, unplannedCutVertices=int(np.count_nonzero(~covered[cut_vertices])), failureGroups=[]))
        vertex = int(cut_vertices[np.flatnonzero(~covered[cut_vertices])[0]])
        _, index, side = min(incident[vertex])
        pair = pairs[index]
        edge = pair[side + 'Edge']
        if edge[0] != vertex:
            edge = [edge[1], edge[0], edge[2]]
        ordinal = pair[side + 'Triangle']
        source_triangles = [pair['firstTriangle'], pair['secondTriangle']]
        metrics = []
        for source in source_triangles:
            triangle = triangles[source]
            texture = np.column_stack((uv[triangle, 0] * shape[1], uv[triangle, 1] * shape[0]))
            area = abs(np.linalg.det(np.stack((texture[1] - texture[0], texture[2] - texture[0]), axis=1)))
            physical = np.linalg.norm(np.cross(points[triangle[1]] - points[triangle[0]], points[triangle[2]] - points[triangle[0]]))
            if area <= 0 or physical <= 0:
                raise ValueError('A native filter cut has a collapsed physical or texture triangle.')
            metrics.append(np.sqrt(physical / area))
        spacing = float(min(metrics))
        support = support_at(spacing, False)
        radius = (support['filterRadiusPixels'] + support['receiverInterpolationPixels']) * spacing
        inner = radius / 2
        length = pair['lengthMetres']
        t = min(0.5, inner / (2 * length))
        field_distance = distances([(edge[0], t * length), (edge[1], (1 - t) * length)])
        field = np.clip((radius - field_distance) / (radius - inner), 0, 1)
        field = field * field * (3 - 2 * field)
        covered |= field_distance <= inner
        vector = points[edge[1]] - points[edge[0]]
        relative = points[edge[2]] - points[edge[0]]
        along = np.dot(relative, vector) / length
        height2 = np.dot(relative, relative) - along * along
        if height2 <= 0:
            raise ValueError('A native filter seed has no unfolded surface.')
        locations = {edge[0]: [0.0, 0.0], edge[1]: [0.0, length], edge[2]: [-np.sqrt(height2), along]}
        initial = np.array([locations[int(value)] for value in triangles[ordinal]])
        contexts.append(dict(seed=ordinal, initial=initial, centre=np.array([0.0, t * length]), spacing=spacing, radius=radius, support=support, field=field, requests=[], cut=index, seedSide=side))
    by_triangle = {}
    for index, context in enumerate(contexts):
        for ordinal in np.flatnonzero(np.max(context['field'][triangles], axis=1) > 0):
            by_triangle.setdefault(int(ordinal), []).append(index)
    witness_chunks = []
    witness_count = 0

    def receive(ordinal, rows, cols, bary, ownership, density):
        nonlocal witness_count
        selected = evaluation[rows, cols]
        if not selected.any():
            return
        rows = rows[selected]
        cols = cols[selected]
        bary = bary[selected]
        weights = [(index, bary @ contexts[index]['field'][triangles[ordinal]]) for index in by_triangle.get(ordinal, [])]
        required = np.full(len(rows),require_complete,bool)
        for _, weight in weights:
            required |= weight > 0
        if not required.any():
            return
        ids = np.full(len(rows), -1, int)
        ids[required] = np.arange(witness_count, witness_count + int(required.sum()))
        witness_chunks.append((ordinal, rows[required], cols[required], bary[required]))
        witness_count += int(required.sum())
        for index, weight in weights:
            valid = weight > 0
            if valid.any():
                contexts[index]['requests'].append((ordinal, rows[valid], cols[valid], bary[valid], weight[valid], ids[valid]))
    indexed_surface_uv_metrics(packet, skin, slot, shape, 0.001, sampler, vertex_field=np.ones(len(points)), sample_receiver=receive)
    return (dict(contexts=contexts, points=points, triangles=triangles, uv=uv, neighbours=neighbours, witnessChunks=witness_chunks, witnessCount=witness_count), dict(pairs=len(pairs)))

def prepare_source_neighbourhoods(packet, skin, slot, shape, sampler, profile, regions, evaluation, *, deadline=None, require_complete=False):
    """Retain existing cut-centred charts; replace dense tap tables by suppliers.

    Discovery layouts are provisional inverse-map candidates only. Their holes
    never authorize a missing filter sample: every actual finite stage support
    is independently resolved by the canonical native block supplier.
    """
    import time
    started=time.perf_counter()
    context, initial_proof = source_neighbourhood_context(packet, skin, slot, shape, sampler, profile, regions, evaluation,
        deadline=deadline,require_complete=require_complete)
    context_seconds=time.perf_counter()-started
    if not context or not context['witnessCount']:
        return ([], dict(initial_proof, requiredWitnesses=0, coveredWitnesses=0, noWritableCutWitnesses=True))
    points = context['points']
    triangles = context['triangles']
    uv = context['uv']
    neighbours = context['neighbours']
    supplier_stats={}
    make_blocks = source_native_sampler(points, triangles, uv, neighbours,report=supplier_stats)
    count = context['witnessCount']
    chunks = context['witnessChunks']
    ordinals = np.concatenate([np.full(len(row[1]), row[0], np.int32) for row in chunks])
    rows = np.concatenate([row[1] for row in chunks])
    cols = np.concatenate([row[2] for row in chunks])
    bary = np.concatenate([row[3] for row in chunks])
    wanted = np.zeros(count)
    covered = np.zeros(count)
    for current in context['contexts']:
        for request in current['requests']:
            np.add.at(wanted, request[5], request[4])
    if require_complete:wanted=np.maximum(wanted,1.)
    patches = []
    patch_ids = []
    records = []
    retained_layout_bytes = 0

    def budget():
        if deadline is not None and time.perf_counter() > deadline:
            raise TimeoutError('Bounded receiver preparation reached its explicit diagnostic deadline.')

    def append_chart(seed, initial, centre, spacing, support, ids, blend, half, kind, direct=None):
        nonlocal retained_layout_bytes
        chart_started=time.perf_counter()
        budget()
        ids = np.asarray(ids, int)
        blend = np.asarray(blend, float)
        if not len(ids):
            return
        half = np.asarray(half, int)
        grid_shape = tuple((int(value) for value in 2 * half[::-1] + 1))
        record = dict(kind=kind, seed=int(seed), grid=list(grid_shape), requests=len(ids), spacingMetres=float(spacing), support=support)
        if direct is None:
            width = grid_shape[1]
            total = int(np.prod(grid_shape))
            layouts = {}
            unknown = 0
            peak = 0
            required = set((int(value) for value in ordinals[ids]))
            required.add(int(seed))
            layouts,lattice_proof=make_blocks.lattice(int(seed),initial,centre,spacing,grid_shape,tuple(map(int,half)),sampler,tuple(shape),required)
            budget()
            unknown=lattice_proof.get('unknownQueries',0)
            peak=min(source_filter_budget()['nativeQueries'],lattice_proof.get('nativePointQueries',0))
            retained_layout_bytes=sum(value.nbytes for entries in layouts.values()for value in entries)
            if retained_layout_bytes>source_filter_budget()['workBytes']//8:
                raise ValueError('Inverse-layout candidates exceed the bounded metadata work budget.')
            discovery_seconds=time.perf_counter()-chart_started
            inverse_started=time.perf_counter();failures = []
            coordinates = source_qualified_coordinates(points, triangles, neighbours, int(seed), initial, centre, [(int(ordinals[index]), bary[index]) for index in ids], layouts, diagnostics=failures, sampler=make_blocks)
            valid = np.isfinite(coordinates).all(1)
            record.update(discoveryQueries=total, peakDiscoveryQueries=peak, discoveryUnknownQueries=unknown, discoveryLayoutsAreNotFilterCoverage=True,
                qualifiedReceivers=int(valid.sum()),discoverySeconds=discovery_seconds,inverseReceiverSeconds=time.perf_counter()-inverse_started,
                nativeLattice=lattice_proof)
        else:
            coordinates = np.asarray(direct, float)
            walked = make_blocks.queries(int(seed), initial, centre, coordinates)
            valid = walked['states'] == 1
            native_point = np.einsum('ni,nij->nj', walked['bary'][valid], points[triangles[walked['triangles'][valid]]])
            expected = np.asarray(direct)
            if expected.shape != (len(ids), 2):
                raise ValueError('Known native receiver coordinates differ from their requests.')
            canonical_values = source_canonical_receivers(points, triangles, neighbours, [(int(ordinals[index]), bary[index]) for index in ids])[0]
            target = np.asarray([weights @ points[triangles[ordinal]] for ordinal, weights in canonical_values])
            good = (walked['triangles'][valid] == seed) & (np.linalg.norm(native_point - target[valid], axis=1) <= 1e-10)
            valid[np.flatnonzero(valid)] = good
            record.update(discoveryQueries=0, knownSeedForwardVerified=True, qualifiedReceivers=int(valid.sum()),
                discoverySeconds=0.,inverseReceiverSeconds=time.perf_counter()-chart_started)
        records.append(record)
        if len(records) % 16 == 0:
            print('Bounded chart candidates:', len(records), 'required receivers covered:', int(np.count_nonzero(covered)), '/', count, flush=True)
        if not valid.any():
            return
        ids = ids[valid]
        blend = blend[valid]
        coordinates = coordinates[valid]
        required_half = source_receiver_grid_half(coordinates, centre, spacing, support)
        half = np.maximum(half, required_half)
        grid_shape = tuple((int(value) for value in 2 * half[::-1] + 1))
        record.update(preparedGrid=list(grid_shape), receiverRequiredHalf=required_half.tolist())
        origin = centre - half * spacing
        fraction = (coordinates - origin) / spacing
        lower = np.floor(fraction).astype(int)
        fraction -= lower
        py = []
        px = []
        pw = []
        for dy in (0, 1):
            for dx in (0, 1):
                yy = lower[:, 1] + dy
                xx = lower[:, 0] + dx
                weight = (fraction[:, 1] if dy else 1 - fraction[:, 1]) * (fraction[:, 0] if dx else 1 - fraction[:, 0])
                if np.any((weight > 0) & ((yy < 0) | (yy >= grid_shape[0]) | (xx < 0) | (xx >= grid_shape[1]))):
                    raise ValueError('A qualified receiver exceeds its unchanged source lattice.')
                py.append(np.clip(yy, 0, grid_shape[0] - 1))
                px.append(np.clip(xx, 0, grid_shape[1] - 1))
                pw.append(weight)
        patch = dict(gridShape=grid_shape, sourceBlocks=make_blocks(int(seed), initial, centre, spacing, grid_shape, tuple((int(value) for value in half)), sampler, tuple(shape)), metresPerTexel=spacing, targetRows=rows[ids], targetCols=cols[ids], patchRows=np.stack(py, 1), patchCols=np.stack(px, 1), patchWeights=np.stack(pw, 1), blend=blend)
        patches.append(patch)
        patch_ids.append(ids)
        np.add.at(covered, ids, blend)
    for current in context['contexts']:
        if not current['requests']:
            continue
        support = source_uniform_support(profile, current['spacing'], regions)
        ids = np.concatenate([request[5] for request in current['requests']])
        blend = np.concatenate([request[4] for request in current['requests']])
        half = int(np.ceil(current['radius'] / current['spacing'])) + support['filterRadiusPixels'] + 1
        append_chart(current['seed'], current['initial'], current['centre'], current['spacing'], support, ids, blend, [half, half], 'cut-centred')
    missing = np.flatnonzero(covered == 0)
    canonical_values = source_canonical_receivers(points, triangles, neighbours, [(int(ordinals[index]), bary[index]) for index in missing])[0]
    owners = np.asarray([value[0] for value in canonical_values])
    canonical_bary = np.asarray([value[1] for value in canonical_values])
    for seed in np.unique(owners):
        selected = np.flatnonzero(owners == seed)
        ids = missing[selected]
        first, second, third = points[triangles[seed]]
        edge = second - first
        length = np.linalg.norm(edge)
        along = np.dot(third - first, edge) / length
        height2 = np.dot(third - first, third - first) - along * along
        if length <= 0 or height2 <= 0:
            raise ValueError('A required receiver has a collapsed native triangle.')
        initial = np.array([[0.0, 0.0], [0.0, length], [-np.sqrt(height2), along]])
        centre = initial.mean(0)
        texture = uv[triangles[seed]] * [shape[1], shape[0]]
        texture_area = abs(np.linalg.det(np.stack((texture[1] - texture[0], texture[2] - texture[0]), 1)))
        if texture_area <= 0:
            raise ValueError('A required receiver has a collapsed source UV metric.')
        spacing = float(np.sqrt(np.linalg.norm(np.cross(second - first, third - first)) / texture_area))
        support = source_uniform_support(profile, spacing, regions)
        coordinates = canonical_bary[selected] @ initial
        half = np.ceil(np.max(abs(coordinates - centre), axis=0) / spacing).astype(int) + support['filterRadiusPixels'] + 1
        append_chart(int(seed), initial, centre, spacing, support, ids, np.minimum(wanted[ids], 1.0), half, 'known-receiver-triangle', direct=coordinates)
    missing = np.flatnonzero(covered == 0)
    if len(missing):
        raise ValueError('Required native receivers remain unresolved: ' + str(len(missing)))
    for patch, ids in zip(patches, patch_ids, strict=True):
        patch['blend'] *= np.minimum(wanted[ids], 1.0) / covered[ids]
        if np.any(patch['blend'] > 1 + 32 * np.finfo(float).eps):
            raise ValueError('A native receiver blend exceeds its declared source support.')
        np.clip(patch['blend'], 0, 1, out=patch['blend'])
    return (patches, dict(initial_proof, requiredWitnesses=count, coveredWitnesses=int(np.count_nonzero(covered)), charts=len(patches), chartRecords=records,
        allRequiredWitnessesCovered=True,completeEvaluationRequested=require_complete,finiteFilterHaloDeferredToCanonicalSupplier=True,
        denseTapTablesRetained=False,retainedInverseLayoutBytes=retained_layout_bytes,
        preparedArraysBytes=sum((value.nbytes for patch in patches for value in patch.values() if isinstance(value, np.ndarray))),
        contextSeconds=context_seconds,preparationSeconds=time.perf_counter()-started,sourceSupplier=supplier_stats))

def source_native_lattice(points, triangles, neighbours, seed, initial, centre, spacing, grid_shape, half, required=(), *, unresolved_edges=()):
    """Certify the same finite native rays as integer rectangles, not fewer rays.

    The sampler owns the immutable packed geometry/run result. Exported inverse
    candidates preserve first original chunk, hop and native edge order.
    """
    import sys
    import time
    class CacheUnavailable(MemoryError):
        pass
    require(type(seed)is int and 0<=seed<len(triangles)and np.asarray(initial).shape==(3,2)and
        np.asarray(centre).shape==(2,)and np.isfinite(initial).all()and np.isfinite(centre).all()and
        np.isfinite(spacing)and spacing>0 and len(grid_shape)==len(half)==2 and
        all(type(value)is int and value>0 for value in grid_shape)and all(type(value)is int for value in half),
        'A native lattice needs its original finite seed, frame and integer coordinates.')

    def frame(current, layout, centre):
        inverse = np.linalg.inv(np.stack((layout[1] - layout[0], layout[2] - layout[0]), axis=1))
        origin = inverse @ (centre - layout[0])
        origin = np.r_[1 - origin.sum(), origin]
        return (int(current), layout, inverse, origin)

    def interval_targets(current_frame, low, high):
        _, layout, inverse, _ = current_frame
        lower = np.nextafter(low - layout[0], -np.inf)
        upper = np.nextafter(high - layout[0], np.inf)
        first_product=lower[:,None,:]*inverse[None,:,:]
        last_product=upper[:,None,:]*inverse[None,:,:]
        first=np.nextafter(np.minimum(first_product,last_product),-np.inf)
        last=np.nextafter(np.maximum(first_product,last_product),np.inf)
        lo=np.nextafter(first[:,:,0]+first[:,:,1],-np.inf)
        hi=np.nextafter(last[:,:,0]+last[:,:,1],np.inf)
        return (np.column_stack((np.nextafter(1-np.nextafter(hi[:,0]+hi[:,1],np.inf),-np.inf),lo)),
                np.column_stack((np.nextafter(1-np.nextafter(lo[:,0]+lo[:,1],-np.inf),np.inf),hi)))

    def decide_rectangles(current_frame, previous, low, high):
        """The original independent enclosures, grouped for one fixed frame."""
        lo, hi = interval_targets(current_frame, low, high)
        n = len(low)
        origin = current_frame[3]
        inside = np.all(lo >= -1e-10, axis=1)
        entered_low = np.zeros(n)
        entered_high = np.zeros(n)
        previous_valid = np.ones(n, bool)
        if previous is not None:
            old, corner = previous
            ol, oh = interval_targets(old, low, high)
            o = old[3][corner]
            dl = np.nextafter(o - oh[:, corner], -np.inf)
            dh = np.nextafter(o - ol[:, corner], np.inf)
            previous_valid = ~((dl <= 0) & (dh >= 0))
            with np.errstate(divide='ignore', invalid='ignore', over='ignore'):
                a = o / dl
                b = o / dh
            entered_low = np.maximum(0.0, np.nextafter(np.minimum(a, b), -np.inf))
            entered_high = np.maximum(0.0, np.nextafter(np.maximum(a, b), np.inf))
        dl = np.nextafter(origin - hi, -np.inf)
        dh = np.nextafter(origin - lo, np.inf)
        known = ~((dl <= 0) & (dh >= 0))
        with np.errstate(divide='ignore', invalid='ignore', over='ignore'):
            a = origin / dl
            b = origin / dh
        rlo = np.nextafter(np.minimum(a, b), -np.inf)
        rhi = np.nextafter(np.maximum(a, b), np.inf)
        applicable = lo < 0
        possible = applicable & ~(rhi < np.maximum(-1e-10, entered_low[:, None] - 1e-10))
        good = ~inside & np.any(hi < -1e-10, axis=1) & previous_valid & ~np.any(applicable & ~known, axis=1)
        good &= ~np.any((np.abs(origin) <= 1e-10) & (lo <= 1e-10) & (hi >= -1e-10), axis=1)
        corners = np.full(n, -1, np.int8)
        for corner in range(3):
            selected = good & possible[:, corner] & (hi[:, corner] < 0) & (rlo[:, corner] >= np.maximum(-1e-10, entered_high - 1e-10))
            ordered = ~possible | (np.maximum(0.0, rlo) > np.maximum(0.0, rhi[:, corner, None]) + 1e-12)
            ordered[:, corner] = True
            selected &= ordered.all(1)
            corners[selected] = corner
        return (inside, corners)

    def plan(points, triangles, neighbours, seed, initial, centre, spacing, grid_shape, half, *, unresolved_edges=(), retain_outputs=True, selected_indices=(), region_receiver=None):
        """Keep all original finite q; split only an unproved integer rectangle."""
        started = time.perf_counter()
        height, width = grid_shape
        # Rectangle certification is a cache optimization. If it costs more
        # steps than the original query batch, retain the bounded canonical
        # ray walk instead of spending unbounded CPU on the cache itself.
        interval_step_limit=max(source_filter_budget()['nativeQueries'],height*width)
        frames = [dict(value=frame(seed, np.asarray(initial, float), centre), parent=-1, corner=-1, hop=0, first=height * width)]
        children = {}
        pending = [(0, np.array([[0, height, 0, width]], np.int64))]
        regions = []
        fallback = {}
        steps = 0
        splits = 0
        maximum = 0
        pending_count = 1
        selected_indices = np.unique(np.asarray(selected_indices, np.int64))
        sampled = {}
        region_count = 0
        fallback_count = 0
        unresolved_count = 0

        def emit(kind, identifier, rect, hop):
            nonlocal region_count
            region_count += 1
            if region_receiver is not None:
                region_receiver(kind, identifier, tuple(map(int, rect)), hop)
            if retain_outputs:
                regions.append((kind, identifier, tuple(map(int, rect)), hop))
            if len(selected_indices):
                y0, y1, x0, x1 = rect
                lo, hi = np.searchsorted(selected_indices, [y0 * width + x0, (y1 - 1) * width + x1])
                ids = selected_indices[lo:hi]
                ids = ids[(ids % width >= x0) & (ids % width < x1)]
                for index in ids:
                    sampled[int(index)] = (kind, identifier, hop)

        def mark(index, first):
            frames[index]['first'] = min(frames[index]['first'], int(first))

        def child(parent, corner, next_value=None):
            key = (parent, corner)
            if key not in children:
                current, layout, _, _ = frames[parent]['value']
                edge = frozenset((int(value) for at, value in enumerate(triangles[current]) if at != corner))
                if next_value is None:
                    following, next_layout, _ = source_native_unfold(points, triangles, neighbours, current, layout, edge)
                    next_value = frame(following, next_layout, centre)
                children[key] = len(frames)
                frames.append(dict(value=next_value, parent=parent, corner=corner, hop=frames[parent]['hop'] + 1, first=height * width))
            return children[key]

        def coordinates(rect):
            y0, y1, x0, x1 = rect
            return (np.array([(x0 - half[0]) * spacing + centre[0], (y0 - half[1]) * spacing + centre[1]]), np.array([(x1 - 1 - half[0]) * spacing + centre[0], (y1 - 1 - half[1]) * spacing + centre[1]]))

        def scalar(y, x):
            nonlocal fallback_count,unresolved_count
            index = y * width + x
            query = np.array([(x - half[0]) * spacing + centre[0], (y - half[1]) * spacing + centre[1]])
            chain = []
            crossed = [None]

            def captured_frame(current, layout, origin):
                value = frame(current, layout, origin)
                identifier = 0 if not chain else child(chain[-1], crossed[0], value)
                chain.append(identifier)
                mark(identifier, index)
                return value

            def captured_unfold(current, layout, edge):
                crossed[0] = next((at for at, value in enumerate(triangles[current]) if int(value) not in edge))
                return source_native_unfold(points, triangles, neighbours, current, layout, edge)
            result = source_native_rays(points, triangles, neighbours, seed, initial, centre, query[None], unresolved_edges=unresolved_edges, retain_layouts=True, _frame=captured_frame, _unfold=captured_unfold)
            fallback_count += 1
            unresolved_count += len(result['failures'])
            if retain_outputs:
                fallback[index] = result
            emit(0, -1, (y, y + 1, x, x + 1), 0)
            if index in sampled:
                sampled[index] = result
        while pending:
            identifier, todo = pending.pop()
            pending_count -= len(todo)
            node = frames[identifier]
            mark(identifier, np.min(todo[:, 0] * width + todo[:, 2]))
            current_frame = node['value']
            parent = node['parent']
            current = current_frame[0]
            previous = None if parent < 0 else (frames[parent]['value'], node['corner'])
            following = {}
            while len(todo):
                maximum = max(maximum, pending_count + len(todo))
                split = []
                for first in range(0, len(todo), source_filter_budget()['nativeQueries']):
                    rects = todo[first:first + source_filter_budget()['nativeQueries']]
                    steps += len(rects)
                    if steps>interval_step_limit:
                        raise CacheUnavailable('Native lattice certification exceeds its original query work allowance.')
                    low = np.stack(((rects[:, 2] - half[0]) * spacing + centre[0], (rects[:, 0] - half[1]) * spacing + centre[1]), 1)
                    high = np.stack(((rects[:, 3] - 1 - half[0]) * spacing + centre[0], (rects[:, 1] - 1 - half[1]) * spacing + centre[1]), 1)
                    if node['hop'] > 2 * len(triangles):
                        inside = np.zeros(len(rects), bool)
                        corners = np.full(len(rects), -1)
                    else:
                        inside, corners = decide_rectangles(current_frame, previous, low, high)
                    for rect in rects[inside]:
                        emit(1, identifier, rect, node['hop'])
                    unresolved = ~inside & (corners < 0)
                    for corner in range(3):
                        ids = ~inside & (corners == corner)
                        if not ids.any():
                            continue
                        edge = frozenset((int(value) for at, value in enumerate(triangles[current]) if at != corner))
                        key = (current, edge)
                        if key in unresolved_edges:
                            for rect in rects[ids]:
                                emit(4, identifier, rect, node['hop'])
                            continue
                        if key not in neighbours:
                            for rect in rects[ids]:
                                emit(2, identifier, rect, node['hop'])
                            continue
                        try:
                            next_identifier = child(identifier, corner)
                        except ValueError:
                            unresolved |= ids
                        else:
                            following.setdefault(next_identifier, []).append(rects[ids].copy())
                    selected = rects[unresolved]
                    heights = selected[:, 1] - selected[:, 0]
                    widths = selected[:, 3] - selected[:, 2]
                    leaves = (heights == 1) & (widths == 1)
                    for rect in selected[leaves]:
                        scalar(int(rect[0]), int(rect[2]))
                    selected = selected[~leaves]
                    heights = heights[~leaves]
                    widths = widths[~leaves]
                    splits += len(selected)
                    if len(selected):
                        a = selected.copy()
                        b = selected.copy()
                        horizontal = widths >= heights
                        middle = (selected[:, 2] + selected[:, 3]) // 2
                        a[horizontal, 3] = middle[horizontal]
                        b[horizontal, 2] = middle[horizontal]
                        middle = (selected[:, 0] + selected[:, 1]) // 2
                        a[~horizontal, 1] = middle[~horizontal]
                        b[~horizontal, 0] = middle[~horizontal]
                        split.extend((a, b))
                todo = np.concatenate(split) if split else np.empty((0, 4), np.int64)
                estimated = len(frames) * 1200 + len(regions) * 160 + (pending_count + len(todo)) * 160 + len(fallback) * 4096 + len(sampled) * 4096
                if estimated > source_filter_budget()['workBytes'] // 8:
                    raise CacheUnavailable('Native lattice metadata exceeds its bounded work allowance.')
            for next_identifier, parts in following.items():
                rects = np.concatenate(parts)
                pending.append((next_identifier, rects))
                pending_count += len(rects)

        def route(index):
            result = []
            while frames[index]['parent'] >= 0:
                result.append(frames[index]['corner'])
                index = frames[index]['parent']
            return tuple(reversed(result))
        ordered = sorted(range(len(frames)), key=lambda index: (frames[index]['first'] // source_filter_budget()['nativeQueries'], frames[index]['hop'], route(index)))
        layouts = {}
        for index in ordered:
            value = frames[index]['value']
            layouts.setdefault(value[0], {}).setdefault(value[1].tobytes(), value[1])
        layouts = {key: list(value.values()) for key, value in layouts.items()}
        compact_children = np.full((len(frames), 3), -1, np.int32)
        for (parent, corner), identifier in children.items():
            compact_children[parent, corner] = identifier
        result = dict(frames=frames, regions=regions, fallback=fallback, layouts=layouts, sampled=sampled, children=compact_children)
        seen = set()

        def resident(value):
            if id(value) in seen:
                return 0
            seen.add(id(value))
            amount = sys.getsizeof(value)
            if isinstance(value, dict):
                amount += sum((resident(key) + resident(item) for key, item in value.items()))
            elif isinstance(value, (list, tuple, set, frozenset)):
                amount += sum((resident(item) for item in value))
            elif isinstance(value, np.ndarray) and value.base is not None:
                amount += resident(value.base)
            return amount
        resident_bytes = resident(result)
        return (result, dict(elapsedSeconds=time.perf_counter() - started, latticeQueries=height * width, frameCount=len(frames), regions=region_count, intervalSteps=steps, rectangleSplits=splits, canonicalFallbackQueries=fallback_count, fallbackUnresolvedQueries=unresolved_count, peakPendingRectangles=maximum, estimatedMetadataBytes=len(frames) * 1200 + len(regions) * 160 + len(fallback) * 4096 + len(sampled) * 4096, retainedGeometryAndSelectedOutputBytes=resident_bytes, fullOutputMetadataRetained=retain_outputs, selectedOutputWitnesses=len(sampled)))

    class Row:
        __slots__ = ('data', 'count')

        def __init__(self, data):
            self.data = data
            self.count = 0

    class Runs:

        def __init__(self, budget):
            self.rows = {}
            self.budget = budget
            self.reserved = 0
            self.peak = 0
            self.insertions = 0
            self.merges = 0
            self.frozen = False
            self.copy_work_bytes = 0
            self.geometry = None

        def emit(self, kind, identifier, rect, hop):
            if self.frozen:
                raise ValueError('A private native run table is immutable after sealing.')
            y0, y1, x0, x1 = rect
            label = 0 if kind == 0 else identifier << 3 | kind
            for y in range(y0, y1):
                self.insert(y, x0, x1, label)

        def insert(self, y, first, last, label):
            if self.frozen:
                raise ValueError('A private native run table is immutable after sealing.')
            if not first < last:
                raise ValueError('A native run needs a nonempty original integer interval.')
            if y not in self.rows:
                amount = 512 + 8 * 12
                if self.reserved + amount > self.budget:
                    raise CacheUnavailable('Native row runs exceed their bounded storage before allocation.')
                row = Row(np.empty((8, 3), np.int32))
                self.rows[y] = row
                self.reserved += amount
                self.peak = max(self.peak, self.reserved)
            row = self.rows[y]
            n = row.count
            data = row.data
            at = int(np.searchsorted(data[:n, 0], first))
            if at and data[at - 1, 1] > first or (at < n and data[at, 0] < last):
                raise ValueError('Private native row runs overlap previously certified q.')
            if at and data[at - 1, 1] == first and (data[at - 1, 2] == label):
                data[at - 1, 1] = last
                self.merges += 1
                if at < n and data[at, 0] == last and (data[at, 2] == label):
                    data[at - 1, 1] = data[at, 1]
                    self.copy_work_bytes = max(self.copy_work_bytes, (n - at - 1) * 12)
                    data[at:n - 1] = data[at + 1:n].copy()
                    row.count -= 1
                    self.merges += 1
            elif at < n and data[at, 0] == last and (data[at, 2] == label):
                data[at, 0] = first
                self.merges += 1
            else:
                if n == len(data):
                    growth = data.nbytes
                    if self.reserved + growth > self.budget:
                        raise CacheUnavailable('Native row runs exceed their bounded growth before allocation.')
                    self.copy_work_bytes = max(self.copy_work_bytes, data.nbytes * 3)
                    larger = np.empty((len(data) * 2, 3), np.int32)
                    larger[:n] = data
                    row.data = larger
                    data = larger
                    self.reserved += growth
                    self.peak = max(self.peak, self.reserved)
                self.copy_work_bytes = max(self.copy_work_bytes, (n - at) * 12)
                data[at + 1:n + 1] = data[at:n].copy()
                data[at] = (first, last, label)
                row.count += 1
            self.insertions += 1

        def seal(self, height, width):
            if len(self.rows) != height:
                raise ValueError('Native row runs omit a required integer row.')
            for y in range(height):
                row = self.rows[y]
                data = row.data[:row.count]
                if not row.count or data[0, 0] != 0 or data[-1, 1] != width or (not np.all(data[1:, 0] == data[:-1, 1])):
                    raise ValueError('Native row runs omit a required original lattice interval.')
                self.copy_work_bytes = max(self.copy_work_bytes, row.data.nbytes + data.nbytes)
                kept = data.copy()
                kept.flags.writeable = False
                self.reserved -= row.data.nbytes - kept.nbytes
                row.data = kept
            self.frozen = True

        def bind(self, planned):
            if not self.frozen:
                raise ValueError('Native row runs need complete immutable coverage before binding frames.')
            frames = planned['frames']
            self.geometry = dict(owners=np.asarray([row['value'][0] for row in frames], np.int32), hops=np.asarray([row['hop'] for row in frames], np.int32), origins=np.asarray([row['value'][1][0] for row in frames]), inverses=np.asarray([row['value'][2] for row in frames]))
            for value in self.geometry.values():
                value.flags.writeable = False
    rows = Runs(source_filter_budget()['nativeCacheBytes'])
    try:
        if max(grid_shape)>np.iinfo(np.int32).max:raise CacheUnavailable('Native lattice dimensions exceed bounded run indices.')
        planned, proof = plan(points, triangles, neighbours, int(seed), initial, centre, spacing, tuple(grid_shape), tuple(half), unresolved_edges=unresolved_edges, retain_outputs=False, region_receiver=rows.emit)
        rows.seal(*grid_shape)
        rows.bind(planned)
    except CacheUnavailable as error:
        return None,None,dict(cacheUnavailable=True,reason=str(error),allOriginalLatticePoints=int(grid_shape[0])*int(grid_shape[1]))
    entry = dict(rows={key: value.data for key, value in rows.rows.items()}, geometry=rows.geometry, shape=tuple(grid_shape), half=tuple(half))
    selected = {}
    for ordinal, values in planned['layouts'].items():
        if required is None or ordinal in required:
            selected[ordinal] = [value.copy() for value in values]
    entry.update(layouts=selected,layoutRequests=None if required is None else tuple(sorted(required)))
    for values in entry['layouts'].values():
        for value in values:value.flags.writeable=False
    unknown=sum(int((value[:,1]-value[:,0])[(value[:,2]&7)==4].sum())for value in entry['rows'].values())
    proof.update(nativePointQueries=proof['canonicalFallbackQueries'],unknownQueries=unknown+proof['fallbackUnresolvedQueries'], allOriginalLatticePoints=int(grid_shape[0]) * int(grid_shape[1]), runCount=sum((value.count for value in rows.rows.values())), maximumRowCopyWorkBytes=rows.copy_work_bytes, fullPixelOutputMetadataRetained=False)
    return (entry, selected, proof)

def source_native_sampler(points,triangles,uv,neighbours,*,report=None):
    """Own immutable geometry and bounded geometry/sample caches per operation."""
    import material_fields as fields
    import collections
    import sys
    import time
    points=np.array(points,float,copy=True);triangles=np.array(triangles,copy=True);uv=np.array(uv,float,copy=True)
    require(points.ndim==2 and points.shape[1]==3 and triangles.ndim==2 and triangles.shape[1]==3 and
        triangles.dtype.kind in'iu'and np.all((triangles>=0)&(triangles<len(points)))and uv.shape==(len(points),2)and
        all(np.isfinite(value).all()for value in(points,uv)),
        'A native source sampler has inconsistent source geometry.')
    neighbours={key:(int(value[0]),dict(value[1]))for key,value in neighbours.items()}
    require(report is None or isinstance(report,dict),'A native sampler report must belong to its operation.')
    if report is not None:report.update(sourceArrayBytes=points.nbytes+triangles.nbytes+uv.nbytes,nativeEdges=len(neighbours),
        chartSuppliers=0,blockRequests=0,queries=0,nativeWalkQueries=0,peakQueries=0,peakTapBytes=0,
        nativeWalkSeconds=0.,samplerSeconds=0.,cachedBlockReuses=0,cacheEvictions=0,peakCacheBytes=0,
        defensiveCopyBytes=0,frameCacheHits=0,frameCacheMisses=0,unfoldCacheHits=0,unfoldCacheMisses=0,
        geometryCacheEvictions=0,peakGeometryCacheBytes=0,
        geometryQueryBatches=0,peakGeometryQueries=0)
    if report is not None:report.update(latticeBuilds=0,latticeCacheHits=0,latticeCacheMisses=0,latticeBudgetFallbacks=0,
        latticeLookupQueries=0,latticeNativeFallbackQueries=0,latticeOuterMissQueries=0,latticeBuildSeconds=0.,peakLatticeEntryBytes=0)
    for value in(points,triangles,uv):value.flags.writeable=False
    cache=collections.OrderedDict();cache_bytes=0;next_chart=0
    geometry_cache=collections.OrderedDict();geometry_bytes=0
    cache_allowance=min(source_filter_budget()['nativeCacheBytes'],source_filter_budget()['workBytes']//8)
    # Keep native geometry reusable across filtered blocks. Taps and lattices
    # can use space that is still free in the geometry cache.
    geometry_limit=cache_allowance//4
    # Two ordered versions of each opposite edge use the helper's exact scalar
    # dot/norm order. Only layout-independent native data belongs in these arrays.
    static_bytes=len(triangles)*3*2*(3*8+8+1)
    require(static_bytes<=source_filter_budget()['workBytes']//8,
        'Native edge metadata exceeds its bounded work budget.')
    native=np.empty((len(triangles),3,2,3))
    third=np.empty((len(triangles),3,2),np.int64)
    ready=np.zeros((len(triangles),3,2),bool)
    static_bytes=native.nbytes+third.nbytes+ready.nbytes
    if report is not None:report['nativeStaticBytes']=static_bytes

    def size(value):
        amount=sys.getsizeof(value)
        if isinstance(value,dict):amount+=sum(size(key)+size(item)for key,item in value.items())
        elif isinstance(value,(tuple,list)):amount+=sum(size(item)for item in value)
        elif isinstance(value,np.ndarray):amount=max(amount,value.nbytes)
        return amount

    def tap_cache_limit():
        return cache_allowance-(geometry_bytes+sys.getsizeof(geometry_cache) if geometry_cache else 0)

    def trim_tap_cache():
        nonlocal cache_bytes
        cache_limit=tap_cache_limit()
        while cache and cache_bytes+sys.getsizeof(cache)>cache_limit:
            _,(_,removed)=cache.popitem(last=False);cache_bytes-=removed
            if report is not None:report['cacheEvictions']+=1
        if not cache and sys.getsizeof(cache)>cache_limit:cache.clear()

    def retain(key,value,entry_bytes):
        nonlocal cache_bytes
        cache_limit=tap_cache_limit()
        if cache_limit and entry_bytes+sys.getsizeof(cache)<=cache_limit:
            cache[key]=(value,entry_bytes);cache_bytes+=entry_bytes
            trim_tap_cache()
            report_cache_size()

    def report_cache_size():
        if report is not None:
            geometry_size=geometry_bytes+sys.getsizeof(geometry_cache)if geometry_cache else 0
            sample_size=cache_bytes+sys.getsizeof(cache)if cache else 0
            report['peakGeometryCacheBytes']=max(report['peakGeometryCacheBytes'],geometry_size)
            report['peakCacheBytes']=max(report['peakCacheBytes'],geometry_size+sample_size)

    def fixed(key,factory,kind):
        nonlocal geometry_bytes
        if key in geometry_cache:
            geometry_cache.move_to_end(key)
            if report is not None:report[kind+'CacheHits']+=1
            return geometry_cache[key][0]
        result=factory()
        if report is not None:report[kind+'CacheMisses']+=1
        if geometry_limit:
            entry_bytes=size(key)+size(result)+sys.getsizeof((None,0))+sys.getsizeof(0)+128
            if entry_bytes+sys.getsizeof(geometry_cache)<=geometry_limit:
                geometry_cache[key]=(result,entry_bytes);geometry_bytes+=entry_bytes
                while geometry_cache and geometry_bytes+sys.getsizeof(geometry_cache)>geometry_limit:
                    _,(_,removed)=geometry_cache.popitem(last=False);geometry_bytes-=removed
                    if report is not None:report['geometryCacheEvictions']+=1
                if not geometry_cache and sys.getsizeof(geometry_cache)>geometry_limit:geometry_cache.clear()
                trim_tap_cache()
                report_cache_size()
        return result

    def frame(current,layout,centre):
        key=('frame',int(current),np.asarray(layout).tobytes(),np.asarray(centre).tobytes())
        def create():
            kept=np.array(layout,float,copy=True)
            inverse=np.linalg.inv(np.stack((kept[1]-kept[0],kept[2]-kept[0]),axis=1))
            origin=inverse@(centre-kept[0]);origin=np.r_[1-origin.sum(),origin]
            for value in(kept,inverse,origin):value.flags.writeable=False
            return int(current),kept,inverse,origin
        return fixed(key,create,'frame')

    def unfold(current,layout,edge):
        ordered=tuple(int(value)for value in edge)
        key=('unfold',int(current),np.asarray(layout).tobytes(),ordered)
        def create():
            connection=neighbours.get((current,frozenset(edge)))
            if connection is None:
                value=source_native_unfold(points,triangles,neighbours,current,layout,edge)
                return value[0],value[1]
            a,b=ordered;triangle=triangles[current]
            ia=0 if triangle[0]==a else 1 if triangle[1]==a else 2
            ib=0 if triangle[0]==b else 1 if triangle[1]==b else 2
            corner=3-ia-ib;direction=int(ia>ib);index=(int(current),corner,direction)
            following,mapping=connection;other=triangles[following];pa,pb=mapping[a],mapping[b]
            if not ready[index]:
                c=next(int(value)for value in other if value not in(pa,pb))
                relative=points[c]-points[pa];native_edge=points[pb]-points[pa]
                native[index]=np.linalg.norm(native_edge),np.dot(relative,native_edge),np.dot(relative,relative)
                third[index]=c;ready[index]=True
            native_length,along_dot,relative_dot=native[index];c=int(third[index])
            origin=layout[ia];vector=layout[ib]-origin;length=np.linalg.norm(vector)
            # Same np.isclose(... rtol=0, atol=1e-12) scalar contract, including
            # equality/nonfinite behavior; no NumPy scalar-array construction.
            if not(native_length==length or math.isfinite(length)and abs(native_length-length)<=1e-12):
                raise ValueError('Native fan peer has different edge length.')
            along=along_dot/length;height2=relative_dot-along*along
            if height2<=0:raise ValueError('Native fan triangle is collapsed.')
            tangent=vector/length;normal=np.array([-tangent[1],tangent[0]])
            side=np.sign(np.dot(layout[corner]-origin,normal))
            if side==0:raise ValueError('Native fan side is ambiguous.')
            values={pa:origin,pb:layout[ib],c:origin+tangent*along-normal*side*np.sqrt(height2)}
            result=np.array([values[int(value)]for value in other]);result.flags.writeable=False
            return following,result
        following,next_layout=fixed(key,create,'unfold')
        return following,next_layout,dict(neighbours[(current,frozenset(edge))][1])

    def queries(seed,initial,centre,values,*,unresolved_edges=(),retain_layouts=False):
        values=np.asarray(values,float);limit=source_filter_budget()['nativeQueries']
        require(values.ndim==2 and values.shape[1]==2,'Native queries need their finite two-dimensional chart coordinates.')
        def evaluate(selected):
            if report is not None:
                report['geometryQueryBatches']+=1;report['peakGeometryQueries']=max(report['peakGeometryQueries'],len(selected))
            return source_native_rays(points,triangles,neighbours,seed,initial,centre,selected,
                unresolved_edges=unresolved_edges,retain_layouts=retain_layouts,_frame=frame,_unfold=unfold,_work_report=report)
        if len(values)<=limit:return evaluate(values)
        # Receiver metadata can contain more requests than one walk chunk.
        # Keep each native work batch bounded and scatter every original index.
        require(len(values)*33<=source_filter_budget()['workBytes']//8,
            'Native receiver results exceed the bounded metadata work budget.')
        result=dict(states=np.empty(len(values),np.uint8),triangles=np.empty(len(values),np.int32),
            bary=np.empty((len(values),3)),hops=np.empty(len(values),np.int32),fanSamples={},failures={},
            groups=0,peakPendingQueries=0,layouts={})
        retained_reports=0
        for first in range(0,len(values),limit):
            part=evaluate(values[first:first+limit]);last=first+len(part['states'])
            retained_reports+=size(part['fanSamples'])+size(part['failures'])+size(part['layouts'])
            receiver_work=(retained_reports+len(values)*113+source_filter_budget()['nativeWaveBytes']+
                source_filter_budget()['nativeCacheBytes']+static_bytes+source_filter_budget()['workBytes']//8)
            require(receiver_work<=source_filter_budget()['workBytes'],
                'Native receiver reports exceed their bounded temporary work budget.')
            if report is not None:
                report['peakReceiverReportBytes']=max(report.get('peakReceiverReportBytes',0),retained_reports+len(values)*113)
                report['peakReceiverWorkBytes']=max(report.get('peakReceiverWorkBytes',0),receiver_work)
            for name in('states','triangles','bary','hops'):result[name][first:last]=part[name]
            for name in('fanSamples','failures'):
                result[name].update((first+index,value)for index,value in part[name].items())
            for current,layouts in part['layouts'].items():result['layouts'].setdefault(current,[]).extend(layouts)
            result['groups']+=part['groups'];result['peakPendingQueries']=max(result['peakPendingQueries'],part['peakPendingQueries'])
        return result

    def lattice_key(seed,initial,centre,spacing,sampler,source_shape,unresolved_edges):
        return ('lattice',int(seed),np.asarray(initial,float).tobytes(),np.asarray(centre,float).tobytes(),float(spacing),
            json.dumps(sampler,sort_keys=True),tuple(source_shape),source_filter_budget()['nativeQueries'],
            tuple(sorted((int(ordinal),tuple(sorted(map(int,edge))))for ordinal,edge in unresolved_edges)))

    def store_lattice(key,entry,proof):
        nonlocal cache_bytes
        entry['proof']=dict(proof)
        entry_bytes=size(key)+size(entry)+sys.getsizeof((None,0))+sys.getsizeof(0)+128
        if entry_bytes+sys.getsizeof(cache)>tap_cache_limit():
            if report is not None:report['latticeBudgetFallbacks']+=1
            return None
        # Replacement is one ownership mutation, not a second charge for the key.
        if key in cache:
            _,old_bytes=cache.pop(key);cache_bytes-=old_bytes
        retain(key,entry,entry_bytes)
        if report is not None:report['peakLatticeEntryBytes']=max(report['peakLatticeEntryBytes'],entry_bytes)
        return entry

    def lattice(seed,initial,centre,spacing,grid_shape,half,sampler,source_shape,required,*,unresolved_edges=()):
        """One finite-lattice owner for discovery and subsequent raw sampling."""
        unresolved_edges=frozenset((int(ordinal),frozenset(map(int,edge)))for ordinal,edge in unresolved_edges)
        key=lattice_key(seed,initial,centre,spacing,sampler,source_shape,unresolved_edges)
        requested=None if required is None else set(map(int,required))
        if key in cache:
            entry=cache[key][0];old=entry['layoutRequests']
            if entry['shape']==tuple(grid_shape)and entry['half']==tuple(half)and(
                    old is None or requested is not None and requested<=set(old)):
                cache.move_to_end(key)
                if report is not None:report['latticeCacheHits']+=1
                layouts={ordinal:[value.copy()for value in values]for ordinal,values in entry['layouts'].items()
                    if requested is None or ordinal in requested}
                return layouts,dict(entry['proof'],cacheHit=True,nativePointQueries=0)
        if report is not None:report['latticeCacheMisses']+=1
        if cache_allowance:
            phase=time.perf_counter()
            entry,layouts,proof=source_native_lattice(points,triangles,neighbours,int(seed),initial,centre,float(spacing),
                tuple(map(int,grid_shape)),tuple(map(int,half)),requested,unresolved_edges=unresolved_edges)
            if report is not None:
                report['latticeBuilds']+=1;report['latticeBuildSeconds']+=time.perf_counter()-phase
            if entry is not None:
                store_lattice(key,entry,proof)
                return {ordinal:[value.copy()for value in values]for ordinal,values in layouts.items()},proof
            if report is not None:report['latticeBudgetFallbacks']+=1
        # An unavailable optimization does not turn any required q into exterior.
        layouts={};unknown=0;total=int(np.prod(grid_shape));width=int(grid_shape[1]);fallback=0;retained_layout_bytes=0
        for first in range(0,total,source_filter_budget()['nativeQueries']):
            index=np.arange(first,min(total,first+source_filter_budget()['nativeQueries']))
            values=np.stack(((index%width-half[0])*spacing+centre[0],(index//width-half[1])*spacing+centre[1]),1)
            result=queries(int(seed),initial,centre,values,unresolved_edges=unresolved_edges,retain_layouts=requested if requested is not None else True)
            unknown+=len(result['failures']);fallback+=len(values)
            for ordinal,rows in result['layouts'].items():
                entries=layouts.setdefault(ordinal,{})
                for value in rows:
                    identity=value.tobytes()
                    if identity not in entries:
                        require(retained_layout_bytes+value.nbytes<=source_filter_budget()['workBytes']//8,
                            'Inverse-layout candidates exceed the bounded metadata work budget.')
                        entries[identity]=value;retained_layout_bytes+=value.nbytes
        return {ordinal:list(rows.values())for ordinal,rows in layouts.items()},dict(allOriginalLatticePoints=total,
            nativePointQueries=fallback,unknownQueries=unknown,cacheUnavailable=True)

    def lattice_samples(entry,seed,initial,centre,spacing,half,x,y,*,unresolved_edges=()):
        count=len(x);values=np.stack(((x-half[0])*spacing+centre[0],(y-half[1])*spacing+centre[1]),1)
        source_x=x-half[0]+entry['half'][0];source_y=y-half[1]+entry['half'][1]
        labels=np.full(count,-1,np.int32)
        within=(source_y>=0)&(source_y<entry['shape'][0])&(source_x>=0)&(source_x<entry['shape'][1])
        for row_id in np.unique(source_y[within]):
            ids=np.flatnonzero(within&(source_y==row_id));row=entry['rows'].get(int(row_id))
            require(row is not None,'A cached native lattice lost a covered row.')
            at=np.searchsorted(row[:,0],source_x[ids],side='right')-1
            require(np.all(at>=0)and np.all(source_x[ids]<row[at,1]),'A cached native lattice lost a covered interval.')
            labels[ids]=row[at,2]
        result=dict(states=np.zeros(count,np.uint8),triangles=np.full(count,-1,np.int32),bary=np.zeros((count,3)),
            hops=np.zeros(count,np.int32),fanSamples={},failures={},layouts={})
        selected=np.flatnonzero(labels>0);owners=labels[selected]>>3;kinds=labels[selected]&7;geometry=entry['geometry']
        require(np.all(owners<len(geometry['owners'])),'A cached native lattice has another frame owner.')
        result['states'][selected]=kinds;result['hops'][selected]=geometry['hops'][owners]
        ordinary=selected[kinds==1];at=labels[ordinary]>>3
        inverse=geometry['inverses'][at];delta=values[ordinary]-geometry['origins'][at]
        target=np.empty((len(ordinary),2));target[:,0]=delta[:,0]*inverse[:,0,0]+delta[:,1]*inverse[:,0,1]
        target[:,1]=delta[:,0]*inverse[:,1,0]+delta[:,1]*inverse[:,1,1]
        result['bary'][ordinary]=np.c_[1-target.sum(1),target];result['triangles'][ordinary]=geometry['owners'][at]
        for index in selected[kinds==4]:result['failures'][int(index)]=dict(state='unresolved',reason='native-peer-closure-incomplete',hops=int(result['hops'][index]))
        missing=np.flatnonzero(labels<=0)
        if len(missing):
            native_result=queries(int(seed),initial,centre,values[missing],unresolved_edges=unresolved_edges)
            for name in('states','triangles','bary','hops'):result[name][missing]=native_result[name]
            for name in('fanSamples','failures'):result[name].update((int(missing[index]),value)for index,value in native_result[name].items())
        if report is not None:
            report['latticeLookupQueries']+=count;report['latticeNativeFallbackQueries']+=len(missing)
            report['latticeOuterMissQueries']+=int(np.count_nonzero(labels<0))
            report['geometryQueryBatches']+=1;report['peakGeometryQueries']=max(report['peakGeometryQueries'],count)
        return result,len(missing)

    def prepare(seed,initial,centre,spacing,grid_shape,half,sampler,source_shape,*,unresolved_edges=()):
        nonlocal next_chart
        chart=next_chart;next_chart+=1
        if report is not None:report['chartSuppliers']+=1
        initial=np.array(initial,float,copy=True);centre=np.array(centre,float,copy=True)
        require(initial.shape==(3,2)and centre.shape==(2,)and np.isfinite(initial).all()and np.isfinite(centre).all(),
            'A native source sampler has an inconsistent chart frame.')
        require(type(seed)is int and 0<=seed<len(triangles)and len(grid_shape)==len(half)==2 and
            all(type(value)is int and value>0 for value in grid_shape)and
            all(isinstance(value,(int,np.integer))and value>=0 for value in half)and np.isfinite(spacing)and spacing>0,
            'A native source sampler has no finite source lattice.')
        grid_shape=tuple(grid_shape);half=tuple(half)
        unresolved_edges=frozenset((int(ordinal),frozenset(map(int,edge)))for ordinal,edge in unresolved_edges)
        sampler=copy.deepcopy(sampler);source_shape=tuple(int(value)for value in source_shape)
        mode=sampler['filterMode'];wrap=(sampler['wrapU'],sampler['wrapV'])
        require(sampler.get('anisoLevel',1)<=1,'An anisotropic source needs its actual sampler footprint.')
        scale=sampler.get('scale',(1.,1.));offset=sampler.get('offset',(0.,0.))
        fields.texture_sampler_taps(np.empty((0,2)),source_shape,mode,wrap,scale=scale,offset=offset)
        initial.flags.writeable=False;centre.flags.writeable=False
        native_key=lattice_key(seed,initial,centre,spacing,sampler,source_shape,unresolved_edges)
        preflight_request=[None]
        def ensure_lattice(bounds,reserved_work_bytes=0):
            preflight_request[0]=tuple(bounds)
            if native_key in cache:
                cache.move_to_end(native_key)
                if report is not None:report['latticeCacheHits']+=1
                return
            y0,y1,x0,x1=bounds
            if not cache_allowance or (y1-y0)*(x1-x0)<=source_filter_budget()['nativeQueries']:return
            limits=source_filter_budget()
            preparation_work=limits['workBytes']//8+3*limits['nativeCacheBytes']+limits['nativeWaveBytes']+static_bytes
            if preparation_work+reserved_work_bytes>limits['workBytes']:
                if report is not None:report['latticeBudgetFallbacks']+=1
                return
            phase=time.perf_counter()
            entry,_,proof=source_native_lattice(points,triangles,neighbours,int(seed),initial,centre,float(spacing),
                (int(y1-y0),int(x1-x0)),(int(half[0]-x0),int(half[1]-y0)),(),unresolved_edges=unresolved_edges)
            if report is not None:
                report['latticeBuilds']+=1;report['latticeBuildSeconds']+=time.perf_counter()-phase
                report['latticeCacheMisses']+=1
            if entry is not None:store_lattice(native_key,entry,proof)
            elif report is not None:report['latticeBudgetFallbacks']+=1
        def blocks(bounds):
            require(isinstance(bounds,(tuple,list))and len(bounds)==4 and all(type(value)is int for value in bounds),
                'A native source sampler needs integer lattice bounds.')
            y0,y1,x0,x1=bounds
            require(0<=y0<y1<=grid_shape[0]and 0<=x0<x1<=grid_shape[1],'Native source bounds exceed the prepared lattice.')
            width=x1-x0;count=(y1-y0)*width;limit=source_filter_budget()['nativeQueries']
            if report is not None:report['blockRequests']+=1
            # Preflight runs before the response allocates dense input fields.
            # Direct bounded consumers reserve their one previous yielded block.
            if preflight_request[0]!=tuple(bounds):ensure_lattice(bounds,source_filter_budget()['nativeTapBlockBytes'])
            preflight_request[0]=None
            active_entry=cache[native_key][0]if native_key in cache else None
            try:
                for first in range(0,count,limit):
                    count_here=min(limit,count-first);key=(chart,tuple(bounds),first,count_here)
                    if report is not None:
                        report['queries']+=count_here;report['peakQueries']=max(report['peakQueries'],count_here)
                    if key in cache:
                        cache.move_to_end(key)
                        if report is not None:report['cachedBlockReuses']+=1
                        samples=cache[key][0]
                        rows,cols,weights,outside=(value.copy()for value in samples)
                        result=dict(firstIndex=first,rows=rows,cols=cols,weights=weights,
                            resolved=np.ones(count_here,bool),outside=outside)
                        if report is not None:report['defensiveCopyBytes']+=sum(value.nbytes for value in samples)
                        yield result
                        del result,samples,rows,cols,weights,outside
                        continue
                    indices=np.arange(first,min(count,first+limit));y=indices//width+y0;x=indices%width+x0
                    values=np.stack(((x-half[0])*spacing+centre[0],(y-half[1])*spacing+centre[1]),axis=1)
                    walk_started=time.perf_counter()if report is not None else 0.
                    if active_entry is None:
                        walked=queries(seed,initial,centre,values,unresolved_edges=unresolved_edges);native_points=len(indices)
                    else:
                        if native_key in cache:cache.move_to_end(native_key)
                        walked,native_points=lattice_samples(active_entry,seed,initial,centre,spacing,half,x,y,unresolved_edges=unresolved_edges)
                    sample_started=time.perf_counter()if report is not None else 0.
                    require(not walked['failures']and np.all(np.isin(walked['states'],[1,2,3])),
                        'A bounded native source leaves required finite support unresolved.')
                    fan_count=max((len(value['samples'])for value in walked['fanSamples'].values()),default=1)
                    taps=(1 if mode==0 else 4)*max(1,fan_count)
                    require(len(indices)*(taps*16+2)<=source_filter_budget()['nativeTapBlockBytes'],
                        'A native sampler tap block exceeds its bounded temporary work budget.')
                    rows=np.zeros((len(indices),taps),np.int32);cols=np.zeros_like(rows);weights=np.zeros((len(indices),taps))
                    ordinary=np.flatnonzero(walked['states']==1)
                    coordinates=np.einsum('ni,nij->nj',walked['bary'][ordinary],uv[triangles[walked['triangles'][ordinary]]])
                    source_y,source_x,source_weight=fields.texture_sampler_taps(coordinates,source_shape,mode,wrap,scale=scale,offset=offset)
                    rows[ordinary,:source_y.shape[1]]=source_y;cols[ordinary,:source_x.shape[1]]=source_x
                    weights[ordinary,:source_weight.shape[1]]=source_weight
                    fan=sample=None
                    for index,fan in walked['fanSamples'].items():
                        at=0
                        for sample in fan['samples']:
                            coordinates=np.asarray(sample['bary'])@uv[triangles[sample['triangle']]]
                            source_y,source_x,source_weight=fields.texture_sampler_taps(coordinates[None,:],source_shape,mode,wrap,scale=scale,offset=offset)
                            end=at+source_y.shape[1];rows[index,at:end]=source_y[0];cols[index,at:end]=source_x[0]
                            weights[index,at:end]=source_weight[0]*sample['weight'];at=end
                    block=dict(firstIndex=first,rows=rows,cols=cols,weights=weights,resolved=np.ones(len(indices),bool),outside=walked['states']==2)
                    if report is not None:
                        report['nativeWalkQueries']+=native_points
                        report['peakTapBytes']=max(report['peakTapBytes'],rows.nbytes+cols.nbytes+weights.nbytes)
                        report['nativeWalkSeconds']+=sample_started-walk_started
                        report['samplerSeconds']+=time.perf_counter()-sample_started
                    del walked,values,ordinary,coordinates,source_y,source_x,source_weight,fan,sample
                    # The cache key already owns the block position. Resolved
                    # taps are invariant, so retain only the sampled arrays.
                    samples=rows,cols,weights,block['outside']
                    entry_bytes=size(key)+size(samples)+sys.getsizeof((None,0))+sys.getsizeof(0)+128
                    if cache_allowance and entry_bytes+sys.getsizeof(cache)<=tap_cache_limit():
                        retained=tuple(value.copy()for value in samples)
                        for value in retained:value.flags.writeable=False
                        if report is not None:report['defensiveCopyBytes']+=sum(value.nbytes for value in retained)
                        retain(key,retained,entry_bytes)
                        del retained
                    del samples,rows,cols,weights,indices,x,y
                    yield dict(block)
                    del block
            finally:
                if active_entry is not None and native_key not in cache:store_lattice(native_key,active_entry,active_entry['proof'])
                active_entry=None
        blocks.native_work_bytes=static_bytes+cache_allowance
        blocks.prepare=ensure_lattice
        return blocks
    # These callables share this exact operation's immutable surface and LRUs.
    # They never retain sampled color, a mutable ownership field, or a sampler.
    prepare.queries=queries
    prepare.unfold=unfold
    prepare.lattice=lattice
    return prepare


def native_source_blocks(points,triangles,uv,neighbours,seed,initial,centre,spacing,grid_shape,half,sampler,source_shape,*,unresolved_edges=()):
    """Prepare one chart when the caller does not share an operation sampler."""
    return source_native_sampler(points,triangles,uv,neighbours)(seed,initial,centre,spacing,grid_shape,half,sampler,source_shape,
        unresolved_edges=unresolved_edges)


def rgb_gaussian_axis(values, kernel, radius, axis):
    """One unchanged finite-kernel axis, shared by atlas and native strips."""
    moved=np.moveaxis(values,axis,0);n=moved.shape[0]
    if len(kernel)<=33:
        padding=[(0,0)]*moved.ndim;padding[0]=(radius,radius)
        padded=np.pad(moved,padding,mode='constant')
        windows=np.lib.stride_tricks.sliding_window_view(padded,len(kernel),axis=0)
        return np.moveaxis(np.einsum('...k,k->...',windows,kernel,optimize=False),0,axis)
    matrix=moved.reshape(n,-1);length=1<<(n+len(kernel)-2).bit_length()
    frequency=np.fft.rfft(kernel,n=length)[:,None];result=np.empty_like(matrix)
    for first in range(0,matrix.shape[1],64):
        spectrum=np.fft.rfft(matrix[:,first:first+64],n=length,axis=0)
        spectrum*=frequency
        convolution=np.fft.irfft(spectrum,n=length,axis=0)
        result[:,first:first+64]=convolution[radius:radius+n]
    return np.moveaxis(result.reshape(moved.shape),0,axis)


def rgb_gaussian(values, sigma):
    """Finite Gaussian convolution with direct windows or bounded FFT chunks."""
    if sigma<.15:return values.copy()
    radius=int(np.ceil(sigma*3));offsets=np.arange(-radius,radius+1)
    kernel=np.exp(-(offsets/sigma)**2*.5);kernel/=kernel.sum()
    out=values.astype(np.float64,copy=True)
    for axis in(0,1):out=rgb_gaussian_axis(out,kernel,radius,axis)
    return out


def rgb_uniform_radius(sigma_metres,spacing):
    require(np.isfinite(spacing)and spacing>0 and np.isfinite(sigma_metres)and sigma_metres>=0,
        'A native uniform radius needs finite nonnegative metres and positive spacing.')
    sigma=float(sigma_metres)/spacing
    return 0 if sigma<.15 else int(np.ceil(3*sigma))


def source_uniform_radius(settings,spacing):
    fine=rgb_uniform_radius(settings['fineSigmaMetres'],spacing)
    coarse=rgb_uniform_radius(settings['grainSigmaMetres'],spacing)
    coherence=rgb_uniform_radius(settings['coherenceSigmaMetres'],spacing)
    directed=0 if settings.get('fineCoherenceRetain',1.)==1 else fine+int(np.ceil(3*settings['coherenceSigmaMetres']/spacing))+1
    return max(fine,coarse,fine+1+coherence,directed)


def rgb_uniform_average(values,domain,spacing,sigma_metres,evaluation,*,source_block=None,reserved_work_bytes=0,report=None,packed=False):
    """The same finite scalar-metric operator, with bounded output/input strips.

    A large sigma is a larger finite halo, not a reason to change its physical
    width. Check all working fields and FFT/strip/kernel buffers before allocating
    them; retained decoded source/final atlas images are accounted separately.
    """
    shape=domain.shape
    require(values.shape==shape and len(shape)==2 and evaluation.shape==shape and evaluation.dtype==np.bool_ and
        np.isfinite(spacing)and spacing>0 and np.isfinite(sigma_metres)and sigma_metres>=0 and type(packed)is bool,
        'A bounded uniform field has an invalid scalar sampling contract.')
    sigma=float(sigma_metres)/spacing;radius=rgb_uniform_radius(sigma_metres,spacing);taps=2*radius+1
    rows=min(shape[0],64+2*radius)
    row_length=1<<max(0,rows+taps-2).bit_length();col_length=1<<max(0,shape[1]+taps-2).bit_length()
    fft_length=max(row_length,col_length)
    retained_work=values.nbytes+domain.nbytes+reserved_work_bytes
    selected_count=int(np.count_nonzero(evaluation))
    output_work=int(np.prod(shape))*(4 if packed else 13)+(selected_count*9+(shape[0]+1)*8 if packed else 0)
    fft_work=64*(fft_length*24+(fft_length//2+1)*32)
    strip_work=8*(6*rows*min(64,shape[1])+4*min(64,shape[0])*shape[1])
    estimated_work=retained_work+output_work+fft_work+strip_work+taps*32
    limit=source_filter_budget()['workBytes']
    require(estimated_work<=limit,'The prepared uniform filter exceeds its bounded temporary work budget.')
    offsets=np.arange(-radius,radius+1)
    kernel=np.ones(1)if radius==0 else np.exp(-.5*(offsets/sigma)**2);kernel/=kernel.sum()
    output_shape=(selected_count,)if packed else shape
    output=np.zeros(output_shape);supported=np.zeros(output_shape,bool);selected_rows=np.flatnonzero(evaluation.any(axis=1));blocks=0
    row_offsets=np.r_[0,np.cumsum(np.count_nonzero(evaluation,axis=1))]if packed else None
    for block in np.unique(selected_rows//64):
        target_rows=selected_rows[selected_rows//64==block]
        y0=max(0,int(target_rows[0])-radius);y1=min(shape[0],int(target_rows[-1])+radius+1)
        numerator=np.empty((len(target_rows),shape[1]));denominator=np.empty_like(numerator)
        for x0 in range(0,shape[1],64):
            x1=min(shape[1],x0+64);weights=domain[y0:y1,x0:x1]
            source=values[y0:y1,x0:x1]if source_block is None else np.asarray(source_block(y0,y1,x0,x1),float)
            require(source.shape==weights.shape and np.isfinite(source).all(),
                'A bounded uniform source block changed its shape or finite values.')
            numerator[:,x0:x1]=rgb_gaussian_axis(source*weights,kernel,radius,0)[target_rows-y0]
            denominator[:,x0:x1]=rgb_gaussian_axis(weights,kernel,radius,0)[target_rows-y0];blocks+=1
        numerator=rgb_gaussian_axis(numerator,kernel,radius,1);denominator=rgb_gaussian_axis(denominator,kernel,radius,1)
        selected=evaluation[target_rows];valid=denominator>1e-14
        np.divide(numerator,denominator,out=numerator,where=valid);numerator[~valid|~selected]=0.
        if packed:
            for at,row in enumerate(target_rows):
                first,last=row_offsets[row:row+2]
                output[first:last]=numerator[at,selected[at]];supported[first:last]=valid[at,selected[at]]
        else:output[target_rows]=numerator;supported[target_rows]=valid&selected
    if report is not None:report.update(estimatedTemporaryBytes=estimated_work,workBudgetBytes=limit,
        reservedWorkingBytes=reserved_work_bytes,sourceBlocks=blocks,gaussianSigma=sigma,finiteKernelTaps=taps,
        outputRows=len(selected_rows),fftColumns=64,packedOutputs=packed,spacingChanged=False,radiusTruncated=False)
    return output,supported

def source_neighbourhood_response(values,domain,evaluation,patch,operation,trailing,support_radius,reserved_work_bytes,packed_output=False):
    """Validate and sample one dense or bounded native stencil before filtering."""
    shape=domain.shape;streamed=isinstance(patch,dict)and'sourceBlocks'in patch
    required={'metresPerTexel','targetRows','targetCols','patchRows','patchCols','patchWeights','blend'}
    required|={'gridShape','sourceBlocks'}if streamed else{'rows','cols','weights','resolved','outside','requiredSupport'}
    require(isinstance(patch,dict)and required<=set(patch)<=required|{'targetOrder'},'Unknown native RGB neighbourhood stencil.')
    if streamed:
        grid_shape=patch['gridShape']
        require(isinstance(grid_shape,(tuple,list))and len(grid_shape)==2 and
            all(type(value)is int and value>0 for value in grid_shape)and callable(patch['sourceBlocks']),
            'A streamed source neighbourhood has no finite native grid or sampler.')
        grid_shape=tuple(grid_shape)
    else:
        rows=np.asarray(patch['rows']);cols=np.asarray(patch['cols']);weights=np.asarray(patch['weights'],float)
        require(rows.dtype.kind in'iu'and cols.dtype.kind in'iu'and rows.shape==cols.shape==weights.shape and rows.ndim==3,
            'Source RGB neighbourhood taps differ.')
        require(np.all((rows>=0)&(rows<shape[0]))and np.all((cols>=0)&(cols<shape[1]))and
            np.isfinite(weights).all()and np.all(weights>=0)and np.all(weights.sum(2)<=1+32*np.finfo(float).eps),
            'Source RGB neighbourhood taps exceed their source image.')
        grid_shape=rows.shape[:2]
        resolved=np.asarray(patch['resolved']);outside=np.asarray(patch['outside']);required_support=np.asarray(patch['requiredSupport'])
        require(all(value.shape==grid_shape and value.dtype==np.bool_ for value in(resolved,outside,required_support)),
            'Source RGB neighbourhood needs explicit native transport coverage.')
        require(not np.any(required_support&~resolved),'A source RGB neighbourhood leaves finite filter support unresolved.')
        geometry=weights.sum(2)
        require(not np.any(outside&~resolved)and np.all(geometry[outside]==0)and
            np.all(geometry[resolved&~outside]>0)and np.all(geometry[~resolved]==0),
            'Source RGB neighbourhood geometry disagrees with its native boundary classification.')
    spacing=patch['metresPerTexel']
    require(type(spacing)in(int,float)and np.isfinite(spacing)and spacing>0,'Source RGB neighbourhood has no physical spacing.')
    target_y=np.asarray(patch['targetRows']);target_x=np.asarray(patch['targetCols'])
    sample_y=np.asarray(patch['patchRows']);sample_x=np.asarray(patch['patchCols']);sample_weight=np.asarray(patch['patchWeights'],float)
    blend=np.asarray(patch['blend'],float)
    require(target_y.dtype.kind in'iu'and target_x.dtype.kind in'iu'and target_y.shape==target_x.shape==blend.shape and
        target_y.ndim==1 and sample_y.dtype.kind in'iu'and sample_x.dtype.kind in'iu'and
        sample_y.shape==sample_x.shape==sample_weight.shape and sample_y.ndim==2 and len(sample_y)==len(target_y),
        'Source RGB neighbourhood targets differ.')
    require(np.all((target_y>=0)&(target_y<shape[0]))and np.all((target_x>=0)&(target_x<shape[1]))and
        np.all((sample_y>=0)&(sample_y<grid_shape[0]))and np.all((sample_x>=0)&(sample_x<grid_shape[1]))and
        np.isfinite(sample_weight).all()and np.all(sample_weight>=0)and
        np.all(abs(sample_weight.sum(1)-1)<=32*np.finfo(float).eps)and
        np.isfinite(blend).all()and np.all((blend>=0)&(blend<=1)),
        'Source RGB neighbourhood target samples exceed their physical patch.')
    target_order=np.asarray(patch['targetOrder'])if'targetOrder'in patch else None
    if target_order is not None:require(target_order.dtype.kind in'iu'and target_order.shape==target_y.shape and np.all(target_order>=0),
        'Native RGB tiles have invalid original receiver indices.')
    selected=evaluation[target_y,target_x]&(blend>0)
    if not selected.any():return None
    target_y=target_y[selected];target_x=target_x[selected];sample_y=sample_y[selected];sample_x=sample_x[selected]
    sample_weight=sample_weight[selected];blend=blend[selected]
    if target_order is not None:target_order=target_order[selected]
    limits=source_filter_budget();bounds=(0,grid_shape[0],0,grid_shape[1]);radius=None
    if callable(support_radius):
        radius=support_radius(spacing)
        require(type(radius)is int and radius>=0,'A source filter has an invalid finite support.')
    if streamed:
        require(callable(support_radius),'A streamed source filter needs its declared finite support.')
        positive=sample_weight>0
        bounds=(max(0,int(sample_y[positive].min())-radius),min(grid_shape[0],int(sample_y[positive].max())+radius+1),
            max(0,int(sample_x[positive].min())-radius),min(grid_shape[1],int(sample_x[positive].max())+radius+1))
        grid_shape=(bounds[1]-bounds[0],bounds[3]-bounds[2])
        # Zero interpolation weights do not own a tap in the cropped support.
        # Keep their unused addresses valid without moving any positive tap.
        sample_y=np.clip(sample_y-bounds[0],0,grid_shape[0]-1)
        sample_x=np.clip(sample_x-bounds[2],0,grid_shape[1]-1)
    cells=int(np.prod(grid_shape));channels=max(1,int(np.prod(values.shape[2:])))
    output_channels=max(1,int(np.prod(trailing)))
    scratch=64*1024*1024
    if radius is not None:
        fft_length=1<<max(0,max(grid_shape)+2*radius-1).bit_length()
        fft_work=64*(fft_length*24+(fft_length//2+1)*32)
        strip_work=8*(6*min(grid_shape[0],64+2*radius)*min(64,grid_shape[1])+4*min(64,grid_shape[0])*grid_shape[1])
        scratch=max(scratch,fft_work+strip_work+(2*radius+1)*32)
    # A packed frequency response retains its fine scalar field, while coarse
    # and tensor outputs occupy only the requested interpolation taps.
    output_work=cells*(8*(channels+2)+6)if packed_output else cells*(8*(channels+1+output_channels)+2)
    estimated_work=reserved_work_bytes+output_work+scratch
    if streamed:
        native_work=reserved_work_bytes+cells*(8*(channels+1)+2)+max(3*limits['nativeTapBlockBytes'],limits['nativeWaveBytes']+limits['nativeTapBlockBytes'])+getattr(patch['sourceBlocks'],'native_work_bytes',0)
        estimated_work=max(estimated_work,native_work)+limits['nativeCacheBytes']
    if not streamed:estimated_work+=sum(value.nbytes for value in(rows,cols,weights,resolved,outside,required_support))
    require(estimated_work<=limits['workBytes'],'The prepared native input exceeds its bounded temporary work budget.')
    if streamed:
        preflight=getattr(patch['sourceBlocks'],'prepare',None)
        if callable(preflight):preflight(bounds,reserved_work_bytes=reserved_work_bytes)
    denominator=np.zeros(grid_shape);current=np.zeros(grid_shape+values.shape[2:])
    if streamed:
        geometry=np.zeros(grid_shape,bool);first_index=0
        for block in patch['sourceBlocks'](bounds):
            require(isinstance(block,dict)and set(block)=={'firstIndex','rows','cols','weights','resolved','outside'},
                'Unknown bounded native source block.')
            source_rows=np.asarray(block['rows']);source_cols=np.asarray(block['cols']);source_weights=np.asarray(block['weights'],float)
            require(source_rows.dtype.kind in'iu'and source_cols.dtype.kind in'iu'and source_rows.ndim==2 and
                source_rows.shape==source_cols.shape==source_weights.shape and source_rows.shape[1]>0,
                'Bounded native source block taps differ.')
            count=len(source_rows)
            require(type(block['firstIndex'])is int and block['firstIndex']==first_index and
                0<count<=limits['nativeQueries']and first_index+count<=cells,
                'Bounded native source blocks omit, repeat or exceed their required lattice.')
            require(np.all((source_rows>=0)&(source_rows<shape[0]))and np.all((source_cols>=0)&(source_cols<shape[1]))and
                np.isfinite(source_weights).all()and np.all(source_weights>=0)and
                np.all(source_weights.sum(1)<=1+32*np.finfo(float).eps),
                'Bounded native source block taps exceed their source image.')
            resolved=np.asarray(block['resolved']);outside=np.asarray(block['outside']);amount=source_weights.sum(1)
            require(resolved.dtype==np.bool_ and outside.dtype==np.bool_ and resolved.shape==outside.shape==(count,)and
                resolved.all(),'A bounded source RGB neighbourhood leaves finite filter support unresolved.')
            require(np.all(amount[outside]==0)and np.all(amount[~outside]>0),
                'Bounded source geometry disagrees with its native boundary classification.')
            geometry.ravel()[first_index:first_index+count]=amount>0
            step=max(1,limits['gatherBytes']//max(1,source_rows.shape[1]*channels*8))
            for at in range(0,count,step):
                last=min(count,at+step);support=source_weights[at:last]*domain[source_rows[at:last],source_cols[at:last]]
                block_denominator=support.sum(1);source=values[source_rows[at:last],source_cols[at:last]]
                multiplier=support.reshape(support.shape+(1,)*(source.ndim-support.ndim));numerator=(source*multiplier).sum(1)
                divisor=block_denominator.reshape(block_denominator.shape+(1,)*(numerator.ndim-block_denominator.ndim))
                view=current.reshape((cells,)+values.shape[2:])[first_index+at:first_index+last]
                np.divide(numerator,divisor,out=view,where=divisor>0)
                denominator.ravel()[first_index+at:first_index+last]=block_denominator
            first_index+=count
        require(first_index==cells,'Bounded native source blocks omit their required lattice.')
        del block,source_rows,source_cols,source_weights,resolved,outside,amount,support,block_denominator,source,multiplier,numerator,divisor,view
    else:
        block_rows=max(1,limits['gatherBytes']//max(1,rows.shape[1]*rows.shape[2]*channels*8))
        for first in range(0,grid_shape[0],block_rows):
            last=min(grid_shape[0],first+block_rows);source_rows=rows[first:last];source_cols=cols[first:last]
            support=weights[first:last]*domain[source_rows,source_cols];block_denominator=support.sum(2)
            source=values[source_rows,source_cols];multiplier=support.reshape(support.shape+(1,)*(source.ndim-support.ndim))
            numerator=(source*multiplier).sum(2)
            divisor=block_denominator.reshape(block_denominator.shape+(1,)*(numerator.ndim-block_denominator.ndim))
            np.divide(numerator,divisor,out=current[first:last],where=divisor>0);denominator[first:last]=block_denominator
        del source_rows,source_cols,support,block_denominator,source,multiplier,numerator,divisor
    patch_evaluation=np.zeros(grid_shape,bool);patch_evaluation[sample_y[sample_weight>0],sample_x[sample_weight>0]]=True
    if not streamed:require(not np.any(patch_evaluation&~required_support)and not np.any(patch_evaluation&~resolved),
        'A source RGB neighbourhood leaves a positive evaluation tap unresolved or outside required support.')
    require(np.all(np.sum(geometry[sample_y,sample_x]*sample_weight,axis=1)>0),
        'A source RGB neighbourhood leaves an evaluation point without native geometry.')
    filtered=np.asarray(operation(current,denominator,spacing,patch_evaluation),float)
    expected=(int(np.count_nonzero(patch_evaluation)),)+trailing if packed_output else grid_shape+trailing
    require(filtered.shape==expected and np.isfinite(filtered).all(),
        'A source RGB neighbourhood returned inconsistent filter fields.')
    if packed_output:
        positive=sample_weight>0;flat=np.flatnonzero(patch_evaluation)
        indices=np.searchsorted(flat,(sample_y*grid_shape[1]+sample_x)[positive])
        response=np.zeros(sample_y.shape+trailing);response[positive]=filtered[indices]
    else:response=filtered[sample_y,sample_x]
    response=(response*sample_weight.reshape(sample_weight.shape+(1,)*len(trailing))).sum(1)
    return target_y,target_x,response,blend,target_order


def filter_source_neighbourhoods(values,domain,evaluation,neighbourhoods,operation,baseline,*,support_radius=None,packed_output=False):
    """Filter bounded native inputs and scatter witnesses in their original order."""
    shape=domain.shape;trailing=baseline.shape[2:]
    require(values.shape[:2]==shape and baseline.shape[:2]==shape and evaluation.shape==shape and evaluation.dtype==np.bool_ and type(packed_output)is bool,
        'Source RGB filter fields and evaluation differ.')
    atlas_work=baseline.size*8+int(np.prod(shape))*8
    require(atlas_work<=source_filter_budget()['workBytes'],'The source filter atlas exceeds its temporary work budget.')
    accumulated=np.zeros_like(baseline,dtype=float);amount=np.zeros(shape)
    response_order=None;staged=[];staged_bytes=0
    for patch in neighbourhoods:
        ordered=isinstance(patch,dict)and'targetOrder'in patch
        if response_order is None:response_order=ordered
        require(response_order==ordered,'Native RGB tiles mix original receiver ordering contracts.')
        sampled=source_neighbourhood_response(values,domain,evaluation,patch,operation,trailing,support_radius,
            atlas_work+staged_bytes,packed_output)
        if sampled is None:continue
        target_y,target_x,response,blend,target_order=sampled
        weighted=response*blend.reshape(blend.shape+(1,)*len(trailing))
        if ordered:
            entry=(target_order,target_y,target_x,weighted,blend)
            staged.append(entry);staged_bytes+=sum(value.nbytes for value in entry)
        else:
            np.add.at(accumulated,(target_y,target_x),weighted)
            np.add.at(amount,(target_y,target_x),blend)
    if staged:
        original_order,target_rows,target_cols,weighted_response,target_weights=(
            np.concatenate([entry[index]for entry in staged])for index in range(5))
        sequence=np.argsort(original_order,kind='stable')
        require(np.all(np.diff(original_order[sequence])>0),'Native RGB tiles repeat an original receiver index.')
        np.add.at(accumulated,(target_rows[sequence],target_cols[sequence]),weighted_response[sequence])
        np.add.at(amount,(target_rows[sequence],target_cols[sequence]),target_weights[sequence])
    selected=amount>0
    if not selected.any():return baseline,amount
    result=baseline.copy();weight=np.minimum(amount[selected],1)
    total=amount[selected].reshape(amount[selected].shape+(1,)*len(trailing))
    weight=weight.reshape(weight.shape+(1,)*len(trailing))
    result[selected]=baseline[selected]*(1-weight)+accumulated[selected]/total*weight
    require(np.isfinite(result).all(),'A transported source RGB filter returned a nonfinite field.')
    return result,amount


def rgb_physical_average(values, domain, metres_per_texel, sigma_metres, evaluation=None, *, require_coverage=False,source_neighbourhoods=()):
    """Interpolate the source area-equivalent UV metric, not a global UV size.

    Local shear/anisotropy is reported by the source adapter; this scalar filter
    uses the actual surface-area-equivalent radius, not a geodesic blur claim.
    Only declared continuous skin participates in each normalized convolution.
    """
    if evaluation is None:
        evaluation = domain > 0
    if np.ndim(metres_per_texel)==0:
        # A native patch is uniform in measured metres. It does not need the
        # scale interpolation used by the spatially varying source UV metric.
        spacing=float(metres_per_texel)
        require(np.isfinite(spacing)and spacing>0,'A measured RGB support requires a positive physical UV metric.')
        output,supported=rgb_uniform_average(values,domain,spacing,sigma_metres,evaluation)
        transported=None
        if source_neighbourhoods:
            output,transported=filter_source_neighbourhoods(values,domain,evaluation,source_neighbourhoods,
                lambda values,domain,metric,evaluation:rgb_physical_average(values,domain,metric,sigma_metres,evaluation,
                    require_coverage=require_coverage),output,support_radius=lambda metric:rgb_uniform_radius(sigma_metres,metric))
        if require_coverage:
            missing=evaluation&~supported
            if transported is not None:missing&=transported<1
            count=int(np.count_nonzero(missing))
            require(count==0,f'An RGB source reference leaves {count} target samples without filter support.')
        return output
    require(np.all(metres_per_texel[evaluation]>0),'A measured RGB support requires a positive physical UV metric.')
    desired = np.divide(sigma_metres,metres_per_texel,out=np.zeros_like(metres_per_texel,dtype=float),where=metres_per_texel>0)
    if source_neighbourhoods and np.any(desired[evaluation]>128):
        # The array-metric implementation cannot supply this output. Its
        # complete native evaluation replaces it; no texel or radius is lost.
        output,transported=filter_source_neighbourhoods(values,domain,evaluation,source_neighbourhoods,
            lambda value,weight,metric,wanted:rgb_physical_average(value,weight,metric,sigma_metres,wanted,
                require_coverage=require_coverage),np.zeros_like(values,dtype=float),
            support_radius=lambda metric:rgb_uniform_radius(sigma_metres,metric))
        require(not np.any(evaluation&(transported<1-32*np.finfo(float).eps)),
            'A native uniform RGB operation leaves required array-unsupported targets unresolved.')
        return output
    levels = [0., .5, 1., 2., 4., 8., 16.]
    maximum = float(np.max(desired[evaluation]))
    require(maximum <= 128, 'RGB physical radius exceeds prepared image support.')
    while levels[-1] < maximum:
        levels.append(levels[-1]*2)
    output = np.zeros_like(values, dtype=np.float64)
    lower = np.zeros_like(values, dtype=np.float64)
    coverage = np.zeros_like(evaluation, dtype=bool) if require_coverage else None
    lower_supported = None
    previous_sigma = 0.
    numerator = values * domain
    denominator = domain.copy()
    for index, sigma in enumerate(levels):
        increment = np.sqrt(max(0., sigma * sigma - previous_sigma * previous_sigma))
        numerator = rgb_gaussian(numerator, increment)
        denominator = rgb_gaussian(denominator, increment)
        current = np.divide(numerator, denominator, out=np.zeros_like(numerator), where=denominator > 1e-14)
        supported = denominator > 1e-14 if require_coverage else None
        if index:
            before = levels[index-1]
            selected = (desired >= before) & (desired <= sigma) & evaluation
            # Interpolate variance: Gaussian scale is a physical variance.
            blend = np.clip((desired * desired - before * before) / (sigma * sigma - before * before), 0, 1)
            output[selected] = (lower * (1-blend) + current * blend)[selected]
            if require_coverage:
                # Both normalized samples must exist when they contribute.
                # A missing adjacent UV chart is not a black reference color.
                coverage[selected] = (((blend == 1) | lower_supported) &
                                      ((blend == 0) | supported))[selected]
        lower = current
        lower_supported = supported
        previous_sigma = sigma
        if sigma >= maximum:
            break
    transported=None
    if source_neighbourhoods:
        output,transported=filter_source_neighbourhoods(values,domain,evaluation,source_neighbourhoods,
            lambda values,domain,metric,evaluation:rgb_physical_average(values,domain,metric,sigma_metres,evaluation,
                require_coverage=require_coverage),output,support_radius=lambda metric:rgb_uniform_radius(sigma_metres,metric))
    if require_coverage:
        missing=evaluation & ~coverage
        if transported is not None:missing&=transported<1
        count=int(np.count_nonzero(missing))
        require(count == 0, f'An RGB source reference leaves {count} target samples without filter support.')
    return output


def source_uniform_structure(log_luma,domain,spacing,settings,evaluation,*,report=None,packed=False):
    """Evaluate the existing tensor at its requested lattice outputs only."""
    shape=domain.shape;selected=np.asarray(evaluation)
    require(log_luma.shape==shape==selected.shape and selected.dtype==np.bool_ and type(packed)is bool,
        'A uniform frequency field needs its native input and boolean output domain.')
    selected_count=int(np.count_nonzero(selected));cells=int(np.prod(shape))
    selected_work=selected_count*8*26
    final_work=log_luma.nbytes+domain.nbytes+cells+(selected_count*16 if packed else cells*17)+selected_work
    require(final_work<=source_filter_budget()['workBytes'],
        'The prepared frequency field exceeds its bounded temporary work budget.')
    selected_y,selected_x=np.nonzero(selected);owned=domain>0
    stages=[]
    def average(sigma,wanted,*,source_block=None,reserved=0,packed=False):
        record={}
        supported_targets=owned if wanted is owned else wanted&owned
        field=rgb_uniform_average(log_luma,domain,spacing,sigma,supported_targets,source_block=source_block,
            reserved_work_bytes=reserved+selected_work,report=record,packed=packed)[0]
        if packed:
            response=np.zeros(int(np.count_nonzero(wanted)));response[domain[wanted]>0]=field;field=response
        stages.append(record);return field
    fine=average(settings['fineSigmaMetres'],owned)
    coarse=average(settings['grainSigmaMetres'],selected,reserved=fine.nbytes,packed=True)
    def gradient_source(kind):
        def read(y0,y1,x0,x1):
            y=np.arange(y0,y1)[:,None];x=np.arange(x0,x1)[None,:]
            left,right=np.maximum(0,x-1),np.minimum(shape[1]-1,x+1)
            before,after=np.maximum(0,y-1),np.minimum(shape[0]-1,y+1)
            dx=(fine[y,right]-fine[y,left])/2;dy=(fine[after,x]-fine[before,x])/2
            dx*=(x>0)&(x<shape[1]-1)&(domain[y,left]>0)&(domain[y,right]>0)
            dy*=(y>0)&(y<shape[0]-1)&(domain[before,x]>0)&(domain[after,x]>0)
            return dx*dx if kind=='xx'else dy*dy if kind=='yy'else dx*dy
        return read
    jxx=average(settings['coherenceSigmaMetres'],selected,source_block=gradient_source('xx'),reserved=fine.nbytes,packed=True)
    jyy=average(settings['coherenceSigmaMetres'],selected,source_block=gradient_source('yy'),reserved=fine.nbytes,packed=True)
    jxy=average(settings['coherenceSigmaMetres'],selected,source_block=gradient_source('xy'),reserved=fine.nbytes,packed=True)
    energy=jxx+jyy
    coherence=np.divide(np.sqrt(np.maximum(0,(jxx-jyy)**2+4*jxy*jxy)),energy,
        out=np.ones_like(energy),where=energy>1e-16);coherence=np.clip(coherence,0,1)
    removed=(fine[selected]-coarse)*(1-coherence*settings.get('coherenceRetain',1.))
    fine_retain=settings.get('fineCoherenceRetain',1.)
    if fine_retain!=1:
        angle=.5*np.arctan2(2*jxy,jxx-jyy);tangent_x,tangent_y=-np.sin(angle),np.cos(angle)
        radius=np.where(domain[selected]>0,settings['coherenceSigmaMetres']/spacing,0.)
        numerator=np.zeros(len(selected_y));denominator=np.zeros_like(numerator)
        for offset in np.linspace(-3.,3.,17):
            sample_x=selected_x+tangent_x*radius*offset;sample_y=selected_y+tangent_y*radius*offset
            x0,y0=np.floor(sample_x).astype(np.int64),np.floor(sample_y).astype(np.int64)
            fraction_x,fraction_y=sample_x-x0,sample_y-y0;kernel=np.exp(-.5*offset*offset)
            for step_y,weight_y in((0,1-fraction_y),(1,fraction_y)):
                for step_x,weight_x in((0,1-fraction_x),(1,fraction_x)):
                    sx,sy=x0+step_x,y0+step_y;valid=(sx>=0)&(sx<shape[1])&(sy>=0)&(sy<shape[0])
                    sx,sy=np.clip(sx,0,shape[1]-1),np.clip(sy,0,shape[0]-1)
                    weight=kernel*weight_x*weight_y*valid
                    high=(log_luma[sy,sx]-fine[sy,sx])*domain[sy,sx]
                    numerator+=high*weight;denominator+=domain[sy,sx]*weight
        directed=np.divide(numerator,denominator,out=np.zeros_like(numerator),where=denominator>1e-14)
        removed+=directed*coherence*(1-fine_retain)
    del fine
    if packed:output=np.stack((removed,coherence),axis=1)
    else:
        output=np.zeros(shape+(2,));output[selected,0]=removed;output[selected,1]=coherence
    if report is not None:report.update(selectedOutputs=len(selected_y),stages=stages,
        maximumEstimatedWorkBytes=max([final_work]+[row['estimatedTemporaryBytes']for row in stages]),
        fullTensorArraysRetained=False,packedOutputs=packed,kernelOrThresholdChanged=False)
    return output


def refine_rgb_structure(current, ownership, continuous_skin, protected_features, metres_per_texel, settings, *, source_neighbourhoods=()):
    """One source-owned RGB operation. Alpha and the other material channels are inputs only.

    Keep chromaticity, coarse pigment distribution, the finest retained detail
    and explicit pigment/anatomy exclusions. The source profile controls how
    much directed structure remains inside the declared luminance band.
    """
    require(current.ndim == 3 and current.shape[2] == 4, 'RGB surface requires current RGBA.')
    shape = current.shape[:2]
    require(all(x.shape == shape for x in (ownership, continuous_skin, protected_features, metres_per_texel)), 'RGB ownership/metric shapes differ.')
    require(np.isfinite(current).all() and np.all((current >= 0) & (current <= 1)), 'Invalid current skin RGBA.')
    require(all(np.isfinite(x).all() for x in (ownership, continuous_skin, protected_features, metres_per_texel)), 'Nonfinite RGB authoring ownership.')
    require(np.all(metres_per_texel[ownership > 0] > 0), 'RGB source has no positive physical UV metric.')
    required={'fineSigmaMetres', 'grainSigmaMetres', 'coherenceSigmaMetres', 'featherMetres'}
    require(required<=set(settings)<=required|{'coherenceRetain','fineCoherenceRetain'}, 'Unknown RGB frequency declaration.')
    require(0 < settings['fineSigmaMetres'] < settings['grainSigmaMetres'] <= settings['coherenceSigmaMetres'] < settings['featherMetres'], 'RGB frequency scales are not ordered physical distances.')
    require(all(type(v) in (int, float) and np.isfinite(v) for v in settings.values()), 'RGB frequency scales must be finite numbers.')
    coherence_retain=settings.get('coherenceRetain',1.)
    fine_coherence_retain=settings.get('fineCoherenceRetain',1.)
    require(0<=coherence_retain<=1,'RGB coherence retention must be within zero and one.')
    require(0<=fine_coherence_retain<=1,'RGB fine coherence retention must be within zero and one.')
    require(all(np.all((x >= 0) & (x <= 1)) for x in (ownership, continuous_skin, protected_features)), 'RGB masks exceed their source domain.')
    eligible = ownership * continuous_skin * (protected_features == 0)
    output = current.copy()
    if not np.any(eligible):
        return output, dict(operation='preserve-source-pattern', affectedPixels=0)
    # Explicit features stay exact and are excluded from the averaging support.
    domain = ((ownership > 0) & (continuous_skin > 0) & (protected_features == 0)).astype(np.float64)
    uniform_only=bool(source_neighbourhoods)and np.any((domain>0)&
        (metres_per_texel<max(settings['coherenceSigmaMetres'],settings['featherMetres']/3)/128))
    feathered=(np.zeros(shape)if uniform_only else
        rgb_physical_average(domain,np.ones(shape),metres_per_texel,settings['featherMetres']/3,evaluation=domain>0))
    if source_neighbourhoods:
        # This is a geometric mask convolution: true exterior is zero rather
        # than a colour donor. Pigment averages below exclude unowned support.
        feathered,transported=filter_source_neighbourhoods(domain,np.ones(shape),domain>0,source_neighbourhoods,
            lambda value,support,metric,evaluation:rgb_physical_average(value,np.ones(value.shape),metric,
                settings['featherMetres']/3,evaluation=evaluation),feathered,
            support_radius=lambda metric:rgb_uniform_radius(settings['featherMetres']/3,metric))
        require(not uniform_only or not np.any((domain>0)&(transported<1-32*np.finfo(float).eps)),
            'A native uniform skin feather leaves required targets unresolved.')
    eligible *= np.clip(feathered, 0, 1)
    rgb = current[:, :, :3]
    luma = np.einsum('...c,c->...', rgb, np.array([.2126, .7152, .0722]))
    log_luma = np.log(np.maximum(luma, 1/65535))
    def source_structure(log_luma,domain,metres_per_texel,evaluation=None):
        if np.ndim(metres_per_texel)==0 and evaluation is not None:
            return source_uniform_structure(log_luma,domain,float(metres_per_texel),settings,evaluation)
        shape=domain.shape
        fine = rgb_physical_average(log_luma, domain, metres_per_texel, settings['fineSigmaMetres'])
        coarse = rgb_physical_average(log_luma, domain, metres_per_texel, settings['grainSigmaMetres'])
        dy, dx = np.gradient(fine)
        # An unowned UV chart has no measured skin luminance. Its zero sentinel
        # must not become a directed crease at the edge of a protected feature.
        valid_x=np.zeros(shape,bool);valid_y=np.zeros(shape,bool)
        valid_x[:,1:-1]=(domain[:,:-2]>0)&(domain[:,2:]>0)
        valid_y[1:-1]=(domain[:-2]>0)&(domain[2:]>0)
        dx*=valid_x;dy*=valid_y
        jxx = rgb_physical_average(dx*dx, domain, metres_per_texel, settings['coherenceSigmaMetres'])
        jyy = rgb_physical_average(dy*dy, domain, metres_per_texel, settings['coherenceSigmaMetres'])
        jxy = rgb_physical_average(dx*dy, domain, metres_per_texel, settings['coherenceSigmaMetres'])
        energy = jxx+jyy
        coherence = np.divide(np.sqrt(np.maximum(0,(jxx-jyy)**2+4*jxy*jxy)), energy,
                              out=np.ones_like(energy), where=energy > 1e-16)
        coherence = np.clip(coherence, 0, 1)
        band = fine-coarse
        # Directional shading and isotropic grain share the same declared physical
        # band. The source profile decides how much directional structure to retain;
        # explicit anatomy and pigment exclusions remain outside this operation.
        removed=band*(1-coherence*coherence_retain)
        # The source profile may also attenuate coherent lines below the fine
        # boundary. Estimate their residual along the native line tangent so an
        # isotropic pore does not inherit a nearby crease's coherence ownership.
        if fine_coherence_retain!=1:
            angle=.5*np.arctan2(2*jxy,jxx-jyy)
            tangent_x=-np.sin(angle);tangent_y=np.cos(angle)
            grid_y,grid_x=np.indices(shape);high=(log_luma-fine)*domain
            numerator=np.zeros(shape);denominator=np.zeros(shape)
            radius=np.divide(settings['coherenceSigmaMetres'],metres_per_texel,out=np.zeros(shape),where=(domain>0)&(metres_per_texel>0))
            for offset in np.linspace(-3.,3.,17):
                sample_x=grid_x+tangent_x*radius*offset;sample_y=grid_y+tangent_y*radius*offset
                x0=np.floor(sample_x).astype(np.int64);y0=np.floor(sample_y).astype(np.int64)
                fraction_x=sample_x-x0;fraction_y=sample_y-y0;kernel=np.exp(-.5*offset*offset)
                for step_y,weight_y in((0,1-fraction_y),(1,fraction_y)):
                    for step_x,weight_x in((0,1-fraction_x),(1,fraction_x)):
                        sx=x0+step_x;sy=y0+step_y;valid=(sx>=0)&(sx<shape[1])&(sy>=0)&(sy<shape[0])
                        sx=np.clip(sx,0,shape[1]-1);sy=np.clip(sy,0,shape[0]-1)
                        weight=kernel*weight_x*weight_y*valid
                        numerator+=high[sy,sx]*weight;denominator+=domain[sy,sx]*weight
            directed=np.divide(numerator,denominator,out=np.zeros(shape),where=denominator>1e-14)
            removed+=directed*coherence*(1-fine_coherence_retain)
        return np.dstack((removed,coherence))
    structure=np.zeros(shape+(2,))if uniform_only else source_structure(log_luma,domain,metres_per_texel)
    if source_neighbourhoods:
        structure,transported=filter_source_neighbourhoods(log_luma,domain,domain>0,source_neighbourhoods,
            lambda value,support,metric,evaluation:source_uniform_structure(value,support,metric,settings,evaluation,packed=True),structure,
            support_radius=lambda metric:source_uniform_radius(settings,metric),packed_output=True)
        require(not uniform_only or not np.any((domain>0)&(transported<1-32*np.finfo(float).eps)),
            'A native uniform skin frequency field leaves required targets unresolved.')
    removed=structure[:,:,0];coherence=structure[:,:,1]
    gain_delta = np.expm1(-removed) * eligible
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
                        coherenceRetain=coherence_retain,
                        fineCoherenceRetain=fine_coherence_retain,
                        removedLogLumaBandRms=float(np.sqrt(np.mean((removed*eligible)**2))),
                        gamutLimitedPixels=gamut_pixels,alphaExact=True, protectedFeaturesExact=True,
                        localChromaticity='single multiplicative luminance gain; no palette/color target',
                        physicalMetric='source triangle area-equivalent metres per texel; anisotropy reported, no geodesic claim')



def source_material_ownership(packet, bones, declaration, *, renderer_to_prefab=None,sample_triangle=None,sample_barycentric=None,_prepared=None,chart_source=None):
    """Evaluate the source domain; one raster owns its immutable prepared inputs."""
    prepared={}if _prepared is None else _prepared
    require(isinstance(prepared,dict),'A source material preparation must be local to its raster.')
    identity=(id(packet),id(bones),id(declaration),id(renderer_to_prefab),id(chart_source))
    if not prepared:
        required={'includeBranches','protectPrefixes'}
        require(required<=set(declaration)<=required|{'protectSurfaceRadiusMetres','surfaceRegion','protectSourceCharts'},'Unknown source material domain.')
        require(all(isinstance(declaration[key],list)and declaration[key] and
                    all(isinstance(v,str)and v for v in declaration[key])for key in required),
                'A source material domain requires explicit bone ownership.')
        branches={name.casefold()for name in declaration['includeBranches']}
        prefixes=tuple(name.casefold()for name in declaration['protectPrefixes'])
        included=np.array([bool(branches.intersection((name or '').casefold().split('/')))for name in bones])
        protected=np.array([(name or '').rsplit('/',1)[-1].casefold().startswith(prefixes)for name in bones])
        require(included.any(),'The declared material domain is absent from its source skeleton.')
        weights=np.asarray(packet['weights'],float).reshape(-1,4)
        indices=np.asarray(packet['boneIndices'],int).reshape(-1,4)
        vertices=np.asarray(packet['vertices'],float).reshape(-1,3)if 'vertices'in packet else None
        limits=declaration.get('protectSurfaceRadiusMetres');limited_rows=[];region=None;frame=None
        if limits is not None:
            require(isinstance(limits,dict)and limits and all(isinstance(prefix,str)and prefix.casefold()in prefixes
                    and isinstance(radius,list)and len(radius)==2 and all(type(v)in(int,float)and np.isfinite(v)for v in radius)
                    and 0<=radius[0]<radius[1]for prefix,radius in limits.items()),
                    'A protected source feature requires declared inner and outer physical radii.')
            require(renderer_to_prefab is not None,'A physical feature limit requires its source renderer frame.')
            frame=art.matrix(renderer_to_prefab);binds=np.asarray(packet['bindPoses'],float).reshape(-1,16)
            require(vertices is not None and len(vertices)==len(weights)and len(binds)==len(bones)and np.isfinite(frame).all(),
                    'A protected source feature has inconsistent skin or renderer coordinates.')
            limited_bones=np.zeros(len(bones),dtype=bool)
            for prefix,(inner,outer)in limits.items():
                limited=np.array([bool(name)and name.rsplit('/',1)[-1].casefold().startswith(prefix.casefold())for name in bones])
                if not limited.any():continue
                require(not np.any(limited_bones&limited),'Protected source feature limits overlap on the same native bones.')
                limited_bones|=limited;origins=np.zeros((len(bones),3))
                for index in np.flatnonzero(limited):origins[index]=np.linalg.inv(art.matrix(binds[index]))[:3,3]
                limited_rows.append((limited,origins,inner,outer))
        if 'surfaceRegion'in declaration:
            value=declaration['surfaceRegion']
            require(isinstance(value,dict)and set(value)=={'coordinateDomain','ellipsoids'}and
                    value['coordinateDomain']=='native-rest-prefab-dcc-metres'and isinstance(value['ellipsoids'],list)and value['ellipsoids'],
                    'A local material region requires its source frame and physical support.')
            require(renderer_to_prefab is not None,'A physical material region requires its source renderer frame.')
            require(vertices is not None,'A physical material region requires its source surface positions.')
            if frame is None:frame=art.matrix(renderer_to_prefab)
            centers=[];radii=[];fractions=[];directions=[];normal_ranges=[];normal_selected=[]
            for ellipsoid in value['ellipsoids']:
                required={'center','radii','innerFraction'};normal_fields={'surfaceNormal','normalRange'}
                require(isinstance(ellipsoid,dict)and required<=set(ellipsoid)<=required|normal_fields and
                        (not normal_fields&set(ellipsoid)or normal_fields<=set(ellipsoid)),
                        'A source material region has unknown support fields.')
                center=np.asarray(ellipsoid['center'],float);radius=np.asarray(ellipsoid['radii'],float);inner=ellipsoid['innerFraction']
                require(center.shape==radius.shape==(3,)and np.isfinite(center).all()and np.isfinite(radius).all()and
                        np.all(radius>0)and type(inner)in(int,float)and np.isfinite(inner)and 0<=inner<1,
                        'A source material region requires finite radii and a bounded inner fraction.')
                centers.append(center);radii.append(radius);fractions.append(inner)
                direction=np.asarray(ellipsoid.get('surfaceNormal',[0.,0.,1.]),float)
                normal_range=np.asarray(ellipsoid.get('normalRange',[-1.,1.]),float)
                require(direction.shape==(3,)and np.isfinite(direction).all()and np.linalg.norm(direction)>0 and
                        normal_range.shape==(2,)and np.isfinite(normal_range).all()and -1<=normal_range[0]<normal_range[1]<=1,
                        'A source material direction requires a finite normal and ordered cosine limits.')
                directions.append(direction/np.linalg.norm(direction));normal_ranges.append(normal_range);normal_selected.append('surfaceNormal'in ellipsoid)
            centers=np.asarray(centers);radii=np.asarray(radii)
            region=(art.CONVERT@frame,centers,radii,np.asarray(fractions),np.min(centers-radii,axis=0),np.max(centers+radii,axis=0),
                    np.asarray(directions),np.asarray(normal_ranges),np.asarray(normal_selected))
            surface_normals=art.native_normals(packet,art.CONVERT@frame)if any(normal_selected)else None
        else:surface_normals=None
        chart_vertices=None;chart_triangles=None
        resolved_declaration=declaration;chart_recovery=None
        if 'protectSourceCharts'in declaration:
            charts=declaration['protectSourceCharts']
            require(isinstance(charts,dict)and charts and all(isinstance(key,str)and len(key)==64 and
                    all(character in'0123456789abcdef'for character in key)and isinstance(value,list)and value
                    for key,value in charts.items()),'Source chart protection requires mesh fingerprints and source-triangle anchors.')
            identity_key=packet.get('sourceFingerprint')
            topologies={}
            def topology(source,slot):
                key=(id(source),slot)
                if key in topologies:return topologies[key]
                require(type(slot)is int and 0<=slot<len(source['subMeshes']),'Source chart protection has an invalid material slot.')
                triangles=np.asarray(source['subMeshes'][slot],int).reshape(-1,3)
                source_count=len(source['vertices'])//3
                require(source.get('vertexCount',source_count)==source_count and np.all((triangles>=0)&(triangles<source_count)),
                        'Source chart protection has invalid native triangle indices.')
                parents=np.arange(len(triangles));edge_owner={}
                def root(index):
                    while parents[index]!=index:parents[index]=parents[parents[index]];index=int(parents[index])
                    return index
                for index,triangle in enumerate(triangles):
                    for first,last in zip(triangle,np.roll(triangle,1)):
                        if first==last:continue
                        edge=tuple(sorted((int(first),int(last))))
                        if edge in edge_owner:parents[root(index)]=root(edge_owner[edge])
                        else:edge_owner[edge]=index
                for index in range(len(parents)):parents[index]=root(index)
                topologies[key]=(triangles,parents);return triangles,parents
            if identity_key not in charts and chart_source is not None:
                require(isinstance(chart_source,dict)and set(chart_source)=={'source','native'},'Unknown source chart recovery authority.')
                source=chart_source['source'];native=chart_source['native'];source_key=source.get('sourceFingerprint')
                require(source_key in charts,'Source chart protection has no declaration for its retained native source identity.')
                count=native['vertexCount']
                require(all(value['vertexCount']>=count and len(value['uv'])==len(native['uv'])for value in(source,packet)),
                        'Source chart recovery lost its native UV prefix.')
                for value in(source,packet):
                    for first,last in zip(native['uv'],value['uv'],strict=True):
                        require((first['channel'],first['dimension'],first['values'])==
                            (last['channel'],last['dimension'],last['values'][:len(first['values'])]),
                            'Source chart recovery changed its native UV prefix.')
                source_uv=np.asarray(next(value['values']for value in source['uv']if value['channel']==0),float).reshape(-1,4)[:,:2]
                current_uv=np.asarray(next(value['values']for value in packet['uv']if value['channel']==0),float).reshape(-1,4)[:,:2]
                recovered=[];bindings=[];claimed=set()
                for anchor in charts[source_key]:
                    profile_anchor(anchor,source,dict(bones=bones));slot=anchor['subMesh'];ordinal=anchor['triangleOrdinal']
                    source_triangles,source_components=topology(source,slot)
                    native_triangles,native_components=topology(native,slot)
                    target_triangles,target_components=topology(packet,slot)
                    owned=np.unique(source_triangles[source_components==source_components[ordinal]])
                    source_prefix=owned[owned<count]
                    native_candidates=np.flatnonzero(np.all(np.isin(native_triangles,source_prefix),axis=1))
                    require(len(native_candidates)>0 and len(set(native_components[native_candidates]))==1,
                            'A source chart has no unique native coarse component.')
                    native_component=int(native_components[native_candidates[0]])
                    native_vertices=np.unique(native_triangles[native_components==native_component])
                    require(np.array_equal(np.sort(source_prefix),native_vertices),
                            'A source chart changed its native indexed component ownership.')
                    targets=[]
                    for component in np.unique(target_components):
                        candidate=np.unique(target_triangles[target_components==component]);prefix=candidate[candidate<count]
                        if np.array_equal(prefix,native_vertices):targets.append(int(component))
                    require(len(targets)==1,'A source chart has no unique recovered indexed component.')
                    component=targets[0];require((slot,component)not in claimed,'Source chart recovery repeats one native chart.')
                    claimed.add((slot,component))
                    query=np.asarray(anchor['barycentric'],float)@source_uv[source_triangles[ordinal]]
                    candidates=np.flatnonzero(target_components==component);tex=current_uv[target_triangles[candidates]]
                    delta=np.stack((tex[:,1]-tex[:,0],tex[:,2]-tex[:,0]),axis=2);valid=abs(np.linalg.det(delta))>1e-12
                    candidates=candidates[valid];delta=delta[valid];tex=tex[valid]
                    bary=np.einsum('nij,nj->ni',np.linalg.inv(delta),query-tex[:,0]);bary=np.c_[1-bary.sum(1),bary]
                    inside=np.flatnonzero(np.min(bary,axis=1)>=-1e-7)
                    require(len(inside)>0,'A source chart anchor is absent from its recovered UV component.')
                    selected=int(inside[0]);anchor_weights=np.maximum(bary[selected],0.);anchor_weights/=anchor_weights.sum()
                    target=dict(type='sourceTriangle',subMesh=slot,triangleOrdinal=int(candidates[selected]),barycentric=anchor_weights.tolist())
                    recovered.append(target);bindings.append(dict(source=anchor,target=target,nativeComponent=native_component))
                resolved_declaration=copy.deepcopy(declaration)
                resolved_declaration['protectSourceCharts'][identity_key]=recovered
                charts=resolved_declaration['protectSourceCharts']
                chart_recovery=dict(sourceFingerprint=source_key,nativeFingerprint=native['sourceFingerprint'],
                    targetFingerprint=identity_key,nativeUvPrefixExact=True,indexedComponentsExact=True,anchors=bindings)
            require(identity_key in charts,'Source chart protection has no declaration for this input mesh identity.')
            require(vertices is not None and len(vertices)==len(weights)and 'subMeshes'in packet,
                    'Source chart protection requires its actual indexed mesh.')
            uvrows=[row for row in packet.get('uv',[])if row['channel']==0]
            require(len(uvrows)==1,'Source chart protection requires its native UV channel.')
            source_uv=np.asarray(uvrows[0]['values'],float).reshape(-1,4)[:,:2]
            require(source_uv.shape==(len(vertices),2)and np.isfinite(source_uv).all(),
                    'Source chart protection has inconsistent native UV coordinates.')
            chart_vertices=np.zeros(len(vertices),bool);chart_triangles=set();chosen=set()
            for anchor in charts[identity_key]:
                require(isinstance(anchor,dict)and anchor.get('type')=='sourceTriangle',
                        'Source chart protection requires an owned source-triangle anchor.')
                profile_anchor(anchor,packet,dict(bones=bones))
                slot=anchor['subMesh'];ordinal=anchor['triangleOrdinal']
                triangles,components=topology(packet,slot);triangle=triangles[ordinal]
                tex=source_uv[triangle]
                require(abs(float(np.linalg.det(np.column_stack((tex[1]-tex[0],tex[2]-tex[0])))))>1e-12,
                        'Source chart protection has a collapsed UV anchor.')
                component=int(components[ordinal]);key=(slot,component)
                require(key not in chosen,'Source chart protection repeats the same native chart.')
                chosen.add(key);owned=triangles[components==component]
                chart_vertices[np.unique(owned)]=True
                chart_triangles.update(tuple(sorted(map(int,triangle)))for triangle in owned)
        prepared.update(identity=identity,weights=weights,indices=indices,vertices=vertices,included=included,
            protected=protected,limits=limited_rows,frame=frame,region=region,surfaceNormals=surface_normals,
            protectedChartVertices=chart_vertices,protectedChartTriangles=chart_triangles,
            resolvedDeclaration=resolved_declaration,chartRecovery=chart_recovery)
    require(prepared['identity']==identity,'A source material preparation belongs to another surface or declaration.')
    weights=prepared['weights'];indices=prepared['indices'];vertices=prepared['vertices']
    require((sample_triangle is None)==(sample_barycentric is None),'A source sample needs its indexed triangle and barycentric coordinates.')
    if sample_triangle is not None:
        tri=np.asarray(sample_triangle,int);bary=np.asarray(sample_barycentric,float)
        require(tri.shape==(3,)and bary.ndim==2 and bary.shape[1]==3 and np.isfinite(bary).all()and
                np.all(bary>=-1e-7)and np.allclose(bary.sum(axis=1),1,rtol=0,atol=1e-6),'Invalid source surface samples.')
        vertices=bary@vertices[tri]
        weights=(weights[tri][None]*bary[:,:,None]).reshape(-1,12)
        indices=np.broadcast_to(indices[tri],(len(bary),3,4)).reshape(-1,12)
    influence=(weights*prepared['included'][indices]).sum(axis=1)
    smooth=lambda value:(lambda t:t*t*(3-2*t))(np.clip(value,0.,1.))
    exclusion=(weights*prepared['protected'][indices]).sum(axis=1)
    for limited,origins,inner,outer in prepared['limits']:
        feature_weights=weights*limited[indices];feature_influence=feature_weights.sum(axis=1)
        feature_origin=np.divide((feature_weights[:,:,None]*origins[indices]).sum(axis=1),feature_influence[:,None],
            out=vertices.copy(),where=feature_influence[:,None]>0)
        distance=np.linalg.norm((vertices-feature_origin)@prepared['frame'][:3,:3].T,axis=1)
        exclusion-=feature_influence*smooth((distance-inner)/(outer-inner))
    result=smooth((influence-.5)*2)*(1-smooth(exclusion/.35))
    if prepared['protectedChartVertices']is not None:
        # Indexed charts remain separate across UV seams and physically close
        # surfaces. A triangle sample owns its chart even at a shared point.
        if sample_triangle is None:result[prepared['protectedChartVertices']]=0
        elif tuple(sorted(map(int,tri)))in prepared['protectedChartTriangles']:result[:]=0
    if prepared['region']is not None:
        frame,centers,radii,fractions,low,high,directions,normal_ranges,normal_selected=prepared['region'];source=art.points(vertices.ravel().tolist(),frame)
        surface_normals=prepared['surfaceNormals']
        if surface_normals is not None:
            if sample_triangle is not None:surface_normals=bary@surface_normals[tri]
            lengths=np.linalg.norm(surface_normals,axis=1)
            require(np.all(lengths>1e-12),'A directed source region has unresolved surface normals.')
            surface_normals=surface_normals/lengths[:,None]
        owned=np.zeros(len(source));selected=np.flatnonzero(np.all((source>=low)&(source<=high),axis=1))
        for begin in range(0,len(selected),2048):
            ids=selected[begin:begin+2048];distance=np.linalg.norm((source[ids,None]-centers[None])/radii[None],axis=2)
            weight=smooth((1-distance)/(1-fractions))
            if surface_normals is not None:
                facing=surface_normals[ids]@directions.T
                normal_weight=smooth((facing-normal_ranges[:,0])/(normal_ranges[:,1]-normal_ranges[:,0]))
                normal_weight[:,~normal_selected]=1;weight*=normal_weight
            owned[ids]=np.max(weight,axis=1)
        result*=owned
    require(sample_triangle is not None or np.any(result),'The declared source material domain has no owned surface.')
    return result


def source_skin_region_mode(region):
    """Validate regional color declarations before resolving their source domains."""
    required={'target','reference','protected','strength'}
    mode=region.get('reconstruction','luminance-gain')if isinstance(region,dict)else None
    require(mode in('luminance-gain','adjacent-low-frequency-rgb','reference-mean-rgb','paired-mean-rgb'),
            'Unknown source skin luminance region.')
    if mode!='paired-mean-rgb':required.add('sourceLowFrequencySigmaMetres')
    if mode in('luminance-gain','adjacent-low-frequency-rgb'):required.add('referenceSigmaMetres')
    limit='maximumLogGain'if mode=='luminance-gain'else'maximumLinearRgbDelta'
    optional={'reconstruction'}|({'detailRetain'}if mode in('adjacent-low-frequency-rgb','reference-mean-rgb')else set())
    require(required|{limit}<=set(region)<=required|{limit}|optional,'Unknown source skin luminance region.')
    strength=region['strength'];maximum=region[limit]
    require(all(type(value)in(int,float)and np.isfinite(value)for value in(strength,maximum))and
            0<=strength<=1 and maximum>0,'A source skin luminance region requires bounded strength.')
    if mode!='paired-mean-rgb':
        fine=region['sourceLowFrequencySigmaMetres'];broad=region.get('referenceSigmaMetres',fine)
        require(all(type(value)in(int,float)and np.isfinite(value)for value in(fine,broad))and 0<fine<=broad,
                'A source skin luminance region requires ordered physical scales.')
    if mode!='luminance-gain':
        require(maximum<=1,'A source RGB reconstruction limit must be within linear LDR range.')
        detail_retain=region.get('detailRetain',1.)
        require(type(detail_retain)in(int,float)and np.isfinite(detail_retain)and 0<=detail_retain<=1,
                'A source RGB detail retention must be within zero and one.')
    return mode


def refine_source_skin_material(pixels, ownership, metres_per_texel, profile, channel, *, normal_encoding=None,luminance_regions=(),source_neighbourhoods=()):
    """Refine an explicitly owned source surface in physical units before packing."""
    import material_fields as fields
    pixels=np.asarray(pixels,np.uint8);ownership=np.asarray(ownership,float);metric=np.asarray(metres_per_texel,float)
    required={'pigmentRetain','normalRetain','normalRadiusMetres','fineSigmaMetres','grainSigmaMetres','coherenceSigmaMetres','featherMetres'}
    micro={'microNormalRetain','microNormalRadiusMetres'}
    macro={'macroNormalRetain','macroNormalRadiusMetres'}
    optional={'pigmentCoherenceRetain','pigmentFineCoherenceRetain'}
    require(required<=set(profile)<=required|micro|macro|optional and
            (not micro&set(profile)or micro<=set(profile)) and (not macro&set(profile)or macro<=set(profile)) and
            all(type(value)in(int,float)and np.isfinite(value)for value in profile.values()),
            'A source skin finish requires finite declared physical scales and detail retention.')
    require(0<=profile['pigmentRetain']<=1 and 0<=profile['normalRetain']<=1 and profile['normalRadiusMetres']>0,
            'A source skin finish has invalid detail retention or normal radius.')
    require(not micro<=set(profile) or (0<=profile['microNormalRetain']<=1 and
            0<profile['microNormalRadiusMetres']<profile['normalRadiusMetres']),
            'Skin microdetail requires a smaller physical radius and bounded retention.')
    require(not macro<=set(profile) or (0<=profile['macroNormalRetain']<=1 and
            profile['macroNormalRadiusMetres']>profile['normalRadiusMetres']),
            'Skin macrodetail requires a larger physical radius and bounded retention.')
    require(0<=profile.get('pigmentCoherenceRetain',1)<=1,'Skin pigment coherence retention must be within zero and one.')
    require(0<=profile.get('pigmentFineCoherenceRetain',1)<=1,'Skin pigment fine coherence retention must be within zero and one.')
    require(pixels.ndim==3 and pixels.shape[2]==4 and pixels.shape[:2]==ownership.shape==metric.shape and
            np.isfinite(ownership).all() and np.isfinite(metric).all() and np.all((ownership>=0)&(ownership<=1)) and
            np.all(metric[ownership>0]>0),'A source skin finish has inconsistent surface ownership or metric.')
    require(channel in('color','normal'),'Unknown source skin channel.')
    require(not luminance_regions or channel=='color','Local luminance regions belong to the source color channel.')
    retained=profile['pigmentRetain']==1 if channel=='color' else (
        profile['normalRetain']==1 and profile.get('microNormalRetain',1)==1 and profile.get('macroNormalRetain',1)==1)
    if not np.any(ownership)or min(pixels.shape[:2])<4 or (retained and not luminance_regions):
        return pixels.copy(),dict(affectedPixels=0,sourceRetained=True)
    result=pixels.copy();selected=ownership>0
    if channel=='color':
        current=pixels.astype(float)/255;current[:,:,:3]=fields.lookup[pixels[:,:,:3]]
        settings={name:profile[name]for name in('fineSigmaMetres','grainSigmaMetres','coherenceSigmaMetres','featherMetres')}
        settings['coherenceRetain']=profile.get('pigmentCoherenceRetain',1.)
        settings['fineCoherenceRetain']=profile.get('pigmentFineCoherenceRetain',1.)
        physical_filters=dict(source_neighbourhoods=source_neighbourhoods)if source_neighbourhoods else{}
        refined,proof=(refine_rgb_structure(current,ownership,selected.astype(float),(~selected).astype(float),metric,settings,**physical_filters)
                       if not retained else(current.copy(),dict(operation='preserve-source-pattern',affectedPixels=0)))
        linear=current[:,:,:3]+(refined[:,:,:3]-current[:,:,:3])*(1-profile['pigmentRetain'])
        regions=[]
        for region in luminance_regions:
            mode=source_skin_region_mode(region)
            limit='maximumLogGain'if mode=='luminance-gain'else'maximumLinearRgbDelta'
            masks=[np.asarray(region[key],float)for key in('target','reference','protected')]
            require(all(mask.shape==ownership.shape and np.isfinite(mask).all()and np.all((mask>=0)&(mask<=1))for mask in masks),
                    'A source skin luminance region has inconsistent ownership.')
            strength=region['strength'];maximum=region[limit]
            target,reference,protected=masks;target=target*ownership*(1-protected);reference=reference*ownership*(1-protected)
            fine=region.get('sourceLowFrequencySigmaMetres');broad=region.get('referenceSigmaMetres',fine)
            if not source_neighbourhoods and fine is not None:
                target[metric<broad/128]=0
                if mode!='reference-mean-rgb':reference[metric<broad/128]=0
            require(np.any(target)and np.any(reference),'A source skin luminance region has no resolved target or adjacent reference.')
            before_region=linear.copy()
            if mode=='paired-mean-rgb':
                areas=[mask*metric**2 for mask in (target,reference)]
                totals=[float(area.sum()) for area in areas]
                require(all(np.isfinite(total) and total>0 for total in totals),
                        'Paired source skin colors require two finite physical areas.')
                owned, adjacent=(np.einsum('ij,ijc->c',area/total,pixels) for area,total,pixels in
                                 zip(areas,totals,(before_region,current[:,:,:3]),strict=True))
                delta=np.clip(adjacent-owned,-maximum,maximum)
                linear=np.clip(linear+target[:,:,None]*strength*delta,0,1)
                detail=dict(targetAreaMetresSquared=totals[0], referenceAreaMetresSquared=totals[1],
                            targetLinearRgb=owned.tolist(),referenceLinearRgb=adjacent.tolist(),linearRgbDelta=delta.tolist())
            elif mode=='luminance-gain':
                source_luma=np.einsum('...c,c->...',current[:,:,:3],[.2126,.7152,.0722])
                log_luma=np.log(np.maximum(source_luma,1/65535))
                low=rgb_physical_average(log_luma,selected.astype(float),metric,fine,evaluation=target>0,**physical_filters)
                adjacent=rgb_physical_average(log_luma,reference,metric,broad,evaluation=target>0,require_coverage=True,**physical_filters)
                gain=np.exp(np.clip(adjacent-low,0,maximum)*target*strength)
                upper=np.divide(1,np.max(linear,axis=2),out=np.full(ownership.shape,np.inf),where=np.max(linear,axis=2)>0)
                gain=np.minimum(gain,upper);linear*=gain[:,:,None]
                detail=dict(gainQuantiles=np.quantile(gain[target>0],[0,.5,.9,1]).tolist(),sourceChromaticityRetained=True)
            else:
                detail_retain=region.get('detailRetain',1.)
                support=ownership*(1-protected)
                low=np.stack([rgb_physical_average(current[:,:,axis],support,metric,fine,evaluation=target>0,**physical_filters)for axis in range(3)],axis=2)
                if mode=='reference-mean-rgb':
                    # A declared nonlocal reference owns a single surface color.
                    # Weight native tissue by physical area rather than UV area;
                    # no local filter is allowed to invent missing support.
                    area=reference*metric**2;total=float(area.sum())
                    require(np.isfinite(total)and total>0,'A source RGB reference has no finite physical area.')
                    adjacent=np.einsum('ij,ijc->c',area/total,current[:,:,:3])
                else:
                    adjacent=np.stack([rgb_physical_average(current[:,:,axis],reference,metric,broad,evaluation=target>0,require_coverage=True,**physical_filters)for axis in range(3)],axis=2)
                # Restore only the declared low-frequency skin field from its
                # own declared native tissue. Fine residual retention is local
                # to the declared surface; protected pigment is not filter support.
                # The global stage has already changed this residual. Removing
                # native detail again would invert rather than attenuate it.
                residual=linear-low
                delta=np.clip(adjacent-low-residual*(1-detail_retain),-maximum,maximum)*target[:,:,None]*strength
                linear=np.clip(linear+delta,0,1)
                detail=dict(linearRgbDeltaQuantiles=np.quantile(np.linalg.norm(delta[target>0],axis=1),[0,.5,.9,1]).tolist(),
                    colorReference='physical-area-weighted native skin RGB'if mode=='reference-mean-rgb'else'adjacent native skin RGB',detailRetain=detail_retain,
                    protectedFeaturesExcludedFromSupport=True)
                if mode=='reference-mean-rgb':detail.update(referenceAreaMetresSquared=total,referenceLinearRgb=adjacent.tolist())
            if fine is not None:
                detail['sourceLowFrequencySigmaMetres']=fine
            if 'referenceSigmaMetres' in region:
                detail['referenceSigmaMetres']=broad
            regions.append(dict(targetPixels=int(np.count_nonzero(target)),referencePixels=int(np.count_nonzero(reference)),
                reconstruction=mode,protectedRegionExact=bool(np.array_equal(linear[protected==1],before_region[protected==1])),**detail))
        if regions:proof['luminanceRegions']=regions
        srgb=np.where(linear<=.0031308,12.92*linear,1.055*np.maximum(linear,0)**(1/2.4)-.055)
        result[:,:,:3]=np.rint(np.clip(srgb,0,1)*255).astype(np.uint8)
        result[~selected]=pixels[~selected]
        require(np.array_equal(result[:,:,3],pixels[:,:,3]),'Skin pigment refinement changed material alpha.')
        proof.update(alphaExact=True)
    else:
        require(normal_encoding=='packed-rgorag','A source normal finish requires its declared shader decoding contract.')
        raw=pixels.astype(float)/255
        x=raw[:,:,0]*raw[:,:,3]*2-1;y=raw[:,:,1]*2-1
        normal=np.dstack((x,y,np.sqrt(np.maximum(0,1-x*x-y*y))))
        macro_active=profile.get('macroNormalRetain',1)!=1
        averaged=(normal if profile['normalRetain']==1 else normalise(np.stack([
            rgb_physical_average(normal[:,:,axis],selected.astype(float),metric,profile['normalRadiusMetres'])for axis in range(3)],axis=2)))
        if micro<=set(profile):
            # Keep pores in their own frequency band while softening larger creases.
            target=normal+(averaged-normal)*(1-profile['normalRetain'])
            if profile['microNormalRetain']!=profile['normalRetain']:
                smooth=np.stack([rgb_physical_average(normal[:,:,axis],selected.astype(float),metric,
                    profile['microNormalRadiusMetres'])for axis in range(3)],axis=2)
                target+=(normal-normalise(smooth))*(profile['microNormalRetain']-profile['normalRetain'])
            if not macro_active:desired=normalise(normal+(target-normal)*ownership[:,:,None])
        else:
            if macro_active:target=normal+(averaged-normal)*(1-profile['normalRetain'])
            else:desired=normalise(normal+(averaged-normal)*(ownership*(1-profile['normalRetain']))[:,:,None])
        if macro_active:
            # The source normal map can retain broad sculpted hollows after
            # the mesh itself has changed. Recenter this separate low band;
            # preserve the wrinkle and pore bands already composed above.
            coarse=np.stack([rgb_physical_average(normal[:,:,axis],selected.astype(float),metric,
                profile['macroNormalRadiusMetres'])for axis in range(3)],axis=2)
            flat=np.zeros_like(coarse);flat[:,:,2]=1
            # Apply before the final normalization so broad and fine bands
            # remain additive in the same decoded tangent-space source.
            target+=(flat-normalise(coarse))*(1-profile['macroNormalRetain'])
            desired=normalise(normal+(target-normal)*ownership[:,:,None])
        alpha=np.divide(desired[:,:,0]*.5+.5,raw[:,:,0],out=raw[:,:,3].copy(),where=raw[:,:,0]>0)
        representable=selected&(alpha>=0)&(alpha<=1)&(raw[:,:,0]>0)
        result[:,:,1][representable]=np.rint(np.clip(desired[:,:,1][representable]*.5+.5,0,1)*255).astype(np.uint8)
        result[:,:,3][representable]=np.rint(alpha[representable]*255).astype(np.uint8)
        require(np.array_equal(result[:,:,[0,2]],pixels[:,:,[0,2]]),'Skin normal refinement changed source carrier channels.')
        proof=dict(normalEncoding=normal_encoding,sourceCarriersExact=True,unrepresentablePixels=int(np.count_nonzero(selected&~representable)))
        if macro<=set(profile):proof.update(macroNormalRetain=profile['macroNormalRetain'],macroNormalRadiusMetres=profile['macroNormalRadiusMetres'])
    require(np.array_equal(result[~selected],pixels[~selected]),'Skin refinement changed an unowned pixel.')
    proof.update(affectedPixels=int(np.count_nonzero(np.any(result!=pixels,axis=2))),unownedPixelsExact=True)
    return result,proof



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




def facial_surface_topology(triangles, groups):
    _, representatives = np.unique(groups, return_index=True)
    triangles = groups[triangles]
    triangles = triangles[np.all(triangles != np.roll(triangles, 1, axis=1), axis=1)]
    edges = np.sort(np.concatenate((triangles[:, :2], triangles[:, 1:], triangles[:, ::2])), axis=1)
    edges, incidence = np.unique(edges, axis=0, return_counts=True)
    return representatives, triangles, edges, np.unique(edges[incidence != 2])


def facial_surface_mobility(groups, ownership, count, boundary):
    mobility = np.ones(count)
    np.minimum.at(mobility, groups, ownership)
    mobility[boundary] = 0.
    return mobility


def fair_facial_surface(reference, topology, groups, ownership, normal_limit, steps):
    """Fair the facial shell along its normals on the welded source topology."""
    representatives, triangles, edges, boundary = topology
    if not len(edges):
        return reference
    surface = reference[representatives].copy()
    count = len(surface)
    # Treat UV splits as one surface and leave open rims anchored.
    mobility = facial_surface_mobility(groups, ownership, count, boundary)
    start, end = np.concatenate((edges, edges[:, ::-1])).T
    length = np.linalg.norm(surface[end] - surface[start], axis=1)
    weight = 1. / np.maximum(length, np.median(length) * .4)
    summed = np.bincount(start, weights=weight, minlength=count)
    summed[summed == 0.] = 1.

    base = surface.copy()
    for step in steps:
        local = surface[triangles]
        triangle_normals = np.cross(local[:, 1] - local[:, 0], local[:, 2] - local[:, 0])
        normals = np.zeros_like(surface)
        np.add.at(normals, triangles.ravel(), np.repeat(triangle_normals, 3, axis=0))
        normals /= np.maximum(np.linalg.norm(normals, axis=1, keepdims=True), 1.e-16)
        differences = (surface[end] - surface[start]) * weight[:, None]
        mean = np.column_stack([np.bincount(start, weights=axis, minlength=count) for axis in differences.T])
        along_normal = np.einsum('ij,ij->i', mean, normals) / summed
        surface += (step * mobility * along_normal)[:, None] * normals

    offset = surface - base
    length = np.linalg.norm(offset, axis=1)
    offset *= np.minimum(1., normal_limit / np.maximum(length, 1.e-16))[:, None]
    return reference + offset[groups]


def lower_face_region(points, eyes, mouth, height):
    """Select the lateral cheek, mandibular turn and chin on each source face."""
    eye_centre = (eyes[0] + eyes[1]) * .5
    eye_spacing = max(abs(eyes[1][0] - eyes[0][0]), height)
    lateral = art.smooth((np.abs(points[:, 0] - eye_centre[0]) / eye_spacing - .34) / .28)
    below_lip = art.smooth(((mouth[2] - points[:, 2]) / height - .22) / .35)
    below_eye = art.smooth(((eye_centre[2] - points[:, 2]) / height - .32) / .36)
    above_neck = art.smooth(((points[:, 2] - mouth[2]) / height + 1.25) / .45)
    in_front = art.smooth(((mouth[1] - points[:, 1]) / height + 1.6) / .7)
    return np.maximum(lateral, below_lip) * below_eye * above_neck * in_front


def fair_mandibular_contours(points, topology, groups, mobility, eyes, mouth, height, limit):
    """Reduce short dents and protrusions along the lower face in several views."""
    vertices, _, edges, boundary = topology
    surface = points[vertices]
    ownership = facial_surface_mobility(groups, mobility, len(surface), boundary)
    edges = edges[np.max(ownership[edges], axis=1) > 0.]
    if not len(edges):
        return points
    first, second = surface[edges].transpose(1, 0, 2)
    rise = second[:, 2] - first[:, 2]
    yaw = np.linspace(-np.pi / 2., np.pi / 2., 5)
    directions = np.column_stack((np.sin(yaw), -np.cos(yaw)))
    # Sampling in anatomical units keeps the same contour filter across head sizes.
    heights = mouth[2] + height * np.linspace(-1.25, .7, 49)
    envelope = np.full((len(heights), len(directions)), np.nan)
    for index, level in enumerate(heights):
        crossing = (rise != 0.) & ((first[:, 2] <= level) != (second[:, 2] <= level))
        at = (level - first[crossing, 2]) / rise[crossing]
        cut = first[crossing] + at[:, None] * (second[crossing] - first[crossing])
        cut_weight = np.sum(ownership[edges[crossing]] * np.column_stack((1. - at, at)), axis=1)
        visible = lower_face_region(cut, eyes, mouth, height) * cut_weight > .2
        if np.any(visible):
            envelope[index] = np.max(cut[visible, :2] @ directions.T, axis=0)

    # This filter preserves quadratic profiles and damps isolated changes in slope.
    # Leave the ends of each continuous section anchored at the original contour.
    windows = np.lib.stride_tricks.sliding_window_view(envelope, 5, axis=0)
    complete = np.all(np.isfinite(windows), axis=2)
    kernel = np.array([-3., 12., 17., 12., -3.]) / 35.
    correction = np.zeros_like(envelope)
    correction[2:-2] = np.where(complete, windows @ kernel - envelope[2:-2], 0.)
    correction = np.clip(correction, -limit, limit)

    projected = surface[:, :2] @ directions.T
    delta = np.zeros_like(surface)
    support = np.zeros(len(surface))
    for axis, direction in enumerate(directions):
        contour = np.interp(surface[:, 2], heights, envelope[:, axis], left=np.nan, right=np.nan)
        adjustment = np.interp(surface[:, 2], heights, correction[:, axis], left=0., right=0.)
        proximity = np.where(np.isfinite(contour), art.smooth(1. - (contour - projected[:, axis]) / (height * .22)), 0.)
        delta[:, :2] += (proximity * adjustment)[:, None] * direction
        support += proximity
    region = lower_face_region(surface, eyes, mouth, height)
    delta *= (region * ownership / np.maximum(support, 1.))[:, None]
    return points + delta[groups]


def fair_assembled_face(native, assembled, skin, strength, collar_vertices, *, marks=None):
    """Smooth the saved head while keeping its fitted anatomy and morph deltas."""
    transform = art.CONVERT @ art.matrix(skin['rendererToPrefab'])
    reference = np.asarray(assembled['vertices'], dtype=float).reshape(-1, 3)
    points = art.points(assembled['vertices'], transform)
    count = native['vertexCount']
    require(0 < count <= len(reference), 'The assembled face lost its native vertex prefix.')
    triangles = np.concatenate([np.asarray(slot, dtype=np.int64).reshape(-1, 3) for slot in assembled['subMeshes']])
    groups = art.weld_groups(points)
    if marks is None:
        _, _, marks = art.landmarks(native, skin, transform)
    eyes, mouth = (marks['left'], marks['right']), marks['mouth']
    head_bones = ['head' if name and ('bip001 head' in name.lower().split('/') or
                  name.lower().rsplit('/', 1)[-1] == 'bip001 jaw') else '' for name in skin['bones']]
    head = anatomy_influence(native, head_bones, ('head',))
    mobility = np.r_[strength * art.smooth((head - .2) / .65), np.ones(len(reference) - count)]
    lower_face = np.zeros(len(reference))
    if mouth is not None and all(eye is not None for eye in eyes):
        height = max(abs(((eyes[0] + eyes[1]) * .5 - mouth)[2]), 1e-6)
        lower_face = lower_face_region(points, eyes, mouth, height)
        mobility *= art.smooth((points[:, 2] - mouth[2] + height * 1.4) / (height * .65))
        protected = np.unique(points[:count][strength == 0.], axis=0)
        if len(reference) > count and len(protected):
            contact_distance = np.concatenate([
                np.linalg.norm(chunk[:, None] - protected, axis=2).min(axis=1)
                for chunk in np.array_split(points[count:], 1 + (len(reference) - count) // 256)])
            mobility[count:] *= art.smooth(contact_distance / (height * .12))
    else:
        mobility[count:] = 0.
    mobility[list(collar_vertices)] = 0.
    selected = mobility > 0.
    if not np.any(selected):
        return assembled, dict(fairedVertices=0, preservedMorphDeltas=True)
    limit = .015 * float(np.linalg.norm(np.ptp(points[selected], axis=0)))
    topology = facial_surface_topology(triangles, groups)
    # Continue the mandibular turn across abrupt facet changes while limiting
    # the displacement to the source face's own scale and articulation mask.
    target = fair_facial_surface(points, topology, groups, mobility, limit, (.52, -.54, .52, -.54))
    if np.any(lower_face * mobility):
        jaw_limit = min(limit, height * .055)
        target = fair_facial_surface(target, topology, groups, mobility * lower_face, jaw_limit,
                                     (.62, .62, .54, -.28))
        target = fair_mandibular_contours(target, topology, groups, mobility, eyes, mouth, height, jaw_limit)
    offset = (target - points) @ np.linalg.inv(transform[:3, :3]).T
    # A single displacement field is shared by the rest and every authored
    # expression. Limit individual welded groups against the whole pose set.
    poses = [reference]
    for shape in assembled['shapes']:
        for frame in shape['frames']:
            delta = np.asarray(frame['vertices'], dtype=float).reshape(-1, 3)
            if np.any(delta):
                poses.append(reference + delta)
    while True:
        rejected = np.zeros(len(reference), dtype=bool)
        for pose in poses:
            fitted, proof = preserve_surface_orientation(pose, pose + offset, triangles, transform, groups=groups)
            if proof['surfaceScaleFactor'] < 1.:
                rejected |= np.any(fitted != (pose + offset).astype(np.float32), axis=1)
        if not np.any(rejected):
            break
        offset[np.isin(groups, np.unique(groups[rejected]))] = 0.

    position = (reference + offset).astype(np.float32)
    changed_groups = np.unique(groups[np.any(position != reference, axis=1)])
    changed = np.isin(groups, changed_groups)
    if not np.any(changed):
        return assembled, dict(fairedVertices=0, preservedMorphDeltas=True)
    frame_vertices = np.unique(triangles[np.any(changed[triangles], axis=1)])
    update = np.zeros(len(reference), dtype=bool)
    update[frame_vertices] = mobility[frame_vertices] > 0.
    normals = np.asarray(assembled['normals'], dtype=float).reshape(-1, 3)
    tangents = np.asarray(assembled['tangents'], dtype=float).reshape(-1, 4)
    normal, tangent = surface_basis(reference, position, normals, tangents, triangles, transform, mobility, False)
    result = copy.deepcopy(assembled)
    result['vertices'] = position.ravel().tolist()
    result['normals'] = np.where(update[:, None], normal, normals).astype(np.float32).ravel().tolist()
    result['tangents'] = np.where(update[:, None], tangent, tangents).astype(np.float32).ravel().tolist()
    result['sourceFingerprint'] = fingerprint(result)[0]
    result['bounds'] = physical_mesh_bounds(result)
    return result, dict(fairedVertices=int(changed.sum()), lowerFaceVertices=int(np.count_nonzero(changed & (lower_face > .5))),
        preservedMorphDeltas=True, authoredPoses=len(poses),
        maximumDisplacementMm=float(np.max(np.linalg.norm(offset @ transform[:3, :3].T, axis=1)) * 1000))


def transport_prepared_surface(reference,target,native_count,triangles,protected,graph=None):
    """Extend a native control field over the saved curve's actual topology."""
    if len(reference)==native_count:return target,dict(addedVertices=0),graph
    if graph is None:
        groups=art.weld_groups(reference);size=int(groups.max())+1
        fixed=np.zeros(size,bool);fixed[groups[:native_count]]=True;fixed[groups[protected]]=True
        edges=np.unique(np.sort(groups[np.concatenate((triangles[:,[0,1]],triangles[:,[1,2]],triangles[:,[2,0]]))],axis=1),axis=0)
        edges=edges[edges[:,0]!=edges[:,1]];_,representatives=np.unique(groups,return_index=True)
        lengths=np.linalg.norm(reference[representatives[edges[:,0]]]-reference[representatives[edges[:,1]]],axis=1)
        conductance=1/np.maximum(lengths,1e-6);diagonal=np.zeros(size)
        np.add.at(diagonal,edges[:,0],conductance);np.add.at(diagonal,edges[:,1],conductance)
        free=np.flatnonzero(~fixed);lookup=np.full(size,-1,int);lookup[free]=np.arange(len(free))
        interior=np.all(~fixed[edges],axis=1);links=lookup[edges[interior]]
        graph=dict(groups=groups,size=size,fixed=fixed,edges=edges,conductance=conductance,diagonal=diagonal,
            free=free,links=links,linkConductance=conductance[interior])
    groups=graph['groups'];size=graph['size'];fixed=graph['fixed'];edges=graph['edges'];free=graph['free']
    values=np.zeros((size,3));counts=np.zeros(size);delta=target[:native_count]-reference[:native_count]
    np.add.at(values,groups[:native_count],delta);np.add.at(counts,groups[:native_count],1)
    values/=np.maximum(counts[:,None],1)
    require(float(np.max(np.abs(values[groups[:native_count]]-delta),initial=0))<=1e-6,
        'Coincident native curve controls have conflicting displacements.')
    values[groups[protected]]=0;iterations=0
    if len(free):
        weight=graph['conductance'];rhs=np.zeros_like(values)
        np.add.at(rhs,edges[:,0],weight[:,None]*values[edges[:,1]])
        np.add.at(rhs,edges[:,1],weight[:,None]*values[edges[:,0]])
        rhs=rhs[free];diagonal=np.maximum(graph['diagonal'][free],1e-30)
        links=graph['links'];conductance=graph['linkConductance']
        def multiply(x):
            result=x*diagonal[:,None]
            for axis in range(3):
                result[:,axis]-=np.bincount(links[:,0],weights=conductance*x[links[:,1],axis],minlength=len(free))
                result[:,axis]-=np.bincount(links[:,1],weights=conductance*x[links[:,0],axis],minlength=len(free))
            return result
        x=np.zeros_like(rhs);residual=rhs.copy();z=residual/diagonal[:,None];direction=z.copy();rho=np.sum(residual*z,axis=0)
        for iterations in range(max(64,len(free))+1):
            if float(np.max(np.abs(residual)/diagonal[:,None],initial=0))<=1e-11:break
            product=multiply(direction);denominator=np.sum(direction*product,axis=0);active=rho>1e-30
            require(bool(np.all(denominator[active]>0)),'The prepared curve field has a singular owned component.')
            step=np.divide(rho,denominator,out=np.zeros(3),where=active)
            x+=direction*step;residual-=product*step;z=residual/diagonal[:,None];updated=np.sum(residual*z,axis=0)
            beta=np.divide(updated,rho,out=np.zeros(3),where=active);direction=z+direction*beta;rho=updated
        else:raise ValueError('The prepared curve field did not converge.')
        require(np.isfinite(x).all(),'The prepared curve field contains a nonfinite displacement.')
        require(float(np.linalg.norm(x,axis=1).max(initial=0))<=float(np.linalg.norm(values,axis=1).max(initial=0))+1e-7,
            'The prepared curve field overshot its native controls.')
        values[free]=x
    result=reference+values[groups];result[:native_count]=target[:native_count];result[protected]=reference[protected]
    return result,dict(addedVertices=len(reference)-native_count,transportedAddedVertices=int(np.count_nonzero(np.linalg.norm(result[native_count:]-reference[native_count:],axis=1)>1e-9)),
        maximumAddedDisplacementMm=float(np.linalg.norm(result[native_count:]-reference[native_count:],axis=1).max(initial=0))*1000,
        iterations=iterations,sourceTopologyAndSkinExact=True,protectedCollarExact=True),graph


def refine_native_face_contours(native: 'dict[str, Any]', authored: 'dict[str, Any]', skin: 'dict[str, Any]', marks: 'dict[str, Any] | None'=None, collar_vertices=(), *, refined=None, guide_report=None, previous_refined=None, surface_weight=None):
    """Compose an owned face target with the assembled collar and its poses."""
    transform = art.CONVERT @ art.matrix(skin['rendererToPrefab'])
    if marks is None and surface_weight is None:
        _, _, marks = art.landmarks(native, skin, transform)
        marks['eyeContactRegions'] = humanoid_face_contact_regions(native, skin)
    if surface_weight is None:
        require(marks is not None and all(marks[k] is not None for k in ('left','right','mouth','nose','head')),
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
    frame_reference=previous_refined if previous_refined is not None else refined
    if frame_reference.get('_sourceFrameReference')is not None:
        authored,recoveries=recover_source_lod_frames(authored,skin,frame_reference,skin,protected_vertices=collar_vertices)
        if recoveries:
            authored['sourceFingerprint']=fingerprint(authored)[0]
            authored['bounds']=physical_mesh_bounds(authored)
            details['preparedPhysicalFrameRecovery']=recoveries
    if previous_refined is not None:
        require(refined is not None, 'A source guide revision requires its next absolute target.')
        for key in ('vertexCount','uv','weights','boneIndices','bindPoses','subMeshes','indexFormat'):
            require(previous_refined[key] == native[key], 'The previous guide changes native correspondence: ' + key)
        if fingerprint(previous_refined)[0] == fingerprint(refined)[0]:
            return copy.deepcopy(authored), dict(details, operation='unchanged-source-guide', previousGuideExact=True)
    points = art.points(native['vertices'], transform)
    if surface_weight is None:
        assert marks is not None
        height = abs(((marks['left'] + marks['right']) * .5)[2] - marks['mouth'][2])
        weight = art.smooth((points[:, 2] - marks['mouth'][2] + height * 1.35) / (height * .65))
    else:
        weight=np.asarray(surface_weight,float)
        require(weight.shape==(count,) and np.isfinite(weight).all() and np.all((weight>=0)&(weight<=1)),
                'A declared contact component has invalid surface ownership.')
    collar = np.asarray(sorted(set(collar_vertices)), dtype=np.int64)
    require(np.all((collar >= 0) & (collar < authored['vertexCount'])), 'A collar constraint is outside its assembled face.')
    if len(collar):
        groups=art.weld_groups(np.asarray(authored['vertices']).reshape(-1,3))
        protected = np.zeros(int(groups.max())+1, dtype=bool); protected[groups[collar]] = True
        face_triangles = np.concatenate([np.asarray(t).reshape(-1, 3) for t in authored['subMeshes']])
        face_triangles=groups[face_triangles]
        fade = np.ones(len(protected)); fade[protected] = 0
        for amount in (0., .25, .75):
            neighbours = np.unique(face_triangles[np.any(protected[face_triangles], axis=1)])
            ring = neighbours[~protected[neighbours]]
            fade[ring] = amount; protected[ring] = True
        weight *= fade[groups[:count]]
    result = copy.deepcopy(authored)
    triangles = np.concatenate([np.asarray(t).reshape(-1, 3) for t in authored['subMeshes']])
    curve_graph=None;curve_reports=[]

    def compose(current_p, current_n, current_t, source_p, source_n, source_t, reference_p, previous_pose=None):
        nonlocal curve_graph
        positions, normals, tangents = current_p.copy(), current_n.copy(), current_t.copy()
        if previous_pose is None:
            positions[:count] += (source_p - positions[:count]) * weight[:, None]
            normals[:count] = normalise(normals[:count] * (1 - weight[:, None]) + source_n * weight[:, None])
            tangent = tangents[:count, :3] * (1 - weight[:, None]) + source_t[:, :3] * weight[:, None]
            tangents[:count, :3] = normalise(tangent - normals[:count] * np.sum(tangent * normals[:count], axis=1, keepdims=True))
            reference = current_p.copy()
            reference[:count] += (reference_p - reference[:count]) * weight[:, None]
        else:
            # The saved collar already contains its source/target composition.
            # Apply the change between absolute guides, rather than blending
            # that collar a second time on each authoring iteration.
            # Later source-owned LOD repairs and skin-frame finishing can add
            # a residual to the previous guide. Its authenticated target owns
            # the revision delta; the current saved surface owns that residual.
            positions[:count] += (source_p - previous_pose[0]) * weight[:, None]
            changed = np.any(source_n != previous_pose[1], axis=1) & (weight > 0)
            normals[:count][changed] = normalise(normals[:count][changed] + (source_n-previous_pose[1])[changed]*weight[changed,None])
            tangent = tangents[:count, :3] + (source_t[:, :3]-previous_pose[2][:, :3])*weight[:, None]
            changed |= np.any(source_t != previous_pose[2], axis=1) & (weight > 0)
            tangents[:count, :3][changed] = normalise(tangent[changed] - normals[:count][changed] * np.sum(tangent[changed] * normals[:count][changed], axis=1, keepdims=True))
            reference = current_p
        positions,curve,curve_graph=transport_prepared_surface(current_p,positions,count,triangles,collar,curve_graph)
        curve_reports.append(curve)
        if len(current_p)>count:
            nn,tt=surface_basis(current_p,positions,current_n,current_t,triangles,transform,np.ones(len(current_p)),False)
            normals[count:]=nn[count:];tangents[count:]=tt[count:]
            normals[collar]=current_n[collar];tangents[collar]=current_t[collar]
        positions, orientation = preserve_surface_orientation(reference, positions, triangles, transform)
        require(orientation['surfaceScaleFactor'] == 1, 'Native contour replacement reverses an assembled surface.')
        # Exact source contacts and exact assembled collars also retain their frames.
        for output, current, source in ((positions, current_p, source_p), (normals, current_n, source_n), (tangents, current_t, source_t)):
            if previous_pose is None:output[:count][weight == 1] = source[weight == 1]
            output[:count][weight == 0] = current[:count][weight == 0]
        return positions, normals, tangents

    old = [np.asarray(authored[k]).reshape(-1, d) for k, d in (('vertices', 3), ('normals', 3), ('tangents', 4))]
    new = [np.asarray(refined[k]).reshape(-1, d) for k, d in (('vertices', 3), ('normals', 3), ('tangents', 4))]
    original = [np.asarray(native[k]).reshape(-1, d) for k, d in (('vertices', 3), ('normals', 3), ('tangents', 4))]
    previous = [np.asarray(previous_refined[k]).reshape(-1, d) for k, d in (('vertices', 3), ('normals', 3), ('tangents', 4))] if previous_refined is not None else None
    base = compose(old[0],old[1],old[2],new[0],new[1],new[2],original[0],previous)
    for key, value in zip(('vertices', 'normals', 'tangents'), base):
        result[key] = value.astype(np.float32).ravel().tolist()
    channels = {shape['name']: shape for shape in refined['shapes']}
    native_channels = {shape['name']: shape for shape in refined.get('_sourceFrameReference', native['shapes'])}
    previous_channels = {shape['name']: shape for shape in previous_refined['shapes']} if previous_refined is not None else {}
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
            prior_shape = previous_channels.get(current_shape['name'])
            prior_frame = next((frame for frame in prior_shape['frames'] if frame['weight']==current['weight']), None) if prior_shape else None
            if previous is not None and prior_frame is None:
                require(source_shape is None, 'A guide revision omitted the previous physical frame.')
                prior_frame = {key: [0.] * (count * 3) for key in ('vertices','normals','tangents')}
            prior_pose = posed(prior_frame, previous) if previous is not None else None
            value = compose(*posed(current, old), *posed(source, new), reference_p,prior_pose)
            for key, field, rest in zip(('vertices', 'normals', 'tangents'), value, base):
                target[key] = (field[:, :3] - rest[:, :3]).astype(np.float32).ravel().tolist()
                if len(collar):
                    a = np.asarray(target[key]).reshape(-1, 3)[collar]
                    b = np.asarray(current[key]).reshape(-1, 3)[collar]
                    collar_frame_error = max(collar_frame_error, float(np.max(np.abs(a - b))))
    result['sourceFingerprint'] = fingerprint(result)[0]
    result['bounds'] = physical_mesh_bounds(result)
    details.update(nativeContourBaseline=native['sourceFingerprint'],
                   preparedSurfaceTransport=curve_reports,
                   assembledCollarVertices=len(collar), collarFrameMaximumError=collar_frame_error,
                   nativeFaceVertices=int(np.count_nonzero(weight == 1)),
                   collarOnlyInheritedChannels=inherited_channels)
    if previous_refined is not None:
        details.update(previousGuideFingerprint=fingerprint(previous_refined)[0], sourceCorrespondenceExact=True,
                       subsequentSourceRefinementsRetained=True)
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
        native_key=skin.get('_nativeLodDonor',donor_key)
        if lod_index(skin) > 0 and native_key in native_donors:
            working, recoveries = recover_source_lod_frames(packet, skin, *native_donors[native_key])
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
