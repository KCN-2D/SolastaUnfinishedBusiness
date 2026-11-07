"""Current character-model extraction, shared facial authoring and mesh recipes.

Shape math belongs to production.py. This entry owns immutable bundle inputs,
source-sharing closure, source-channel verification and Unity input recipes.
"""
import argparse
import ast
import collections
import copy
import gzip
import hashlib
import json
import math
import os
import re
import shutil
import struct
import sys
import types
import uuid
from pathlib import Path
from typing import Any

ROOT=next(p for p in Path(__file__).resolve().parents if (p/'SolastaUnfinishedBusiness.sln').is_file())
sys.path.insert(0,str(ROOT/'tools/blender-models/CharacterFaces'))
sys.path.insert(0,str(ROOT/'tools/asset-authoring'))
sys.path.insert(0,str(ROOT/'tools/asset-authoring/UnityAssetBuild'))
from collect_sources import fingerprint
import asset_names
from mesh_recipe_codec import four_slot_skin

HERE: Path | None=None

def require_workspace() -> Path:
    if HERE is None:
        raise ValueError('Initialize the model workspace before running this operation.')
    return HERE
PUBLIC=ROOT/'SolastaUnfinishedBusiness/BlenderModels/CharacterModels'
EXACT_MAIN_TEXTURE_MAX_BYTES=8*1024**2
EXACT_MAIN_CATALOG_MAX_BYTES=256*1024**2
read=lambda path:json.loads(Path(path).read_text(encoding='utf-8-sig'))
def sha(path):
    with Path(path).open('rb') as stream:
        return hashlib.file_digest(stream,'sha256').hexdigest()


def write(path,value):
    path=Path(path);path.parent.mkdir(parents=True,exist_ok=True)
    temporary=path.with_name(path.name+'.writing')
    temporary.write_text(json.dumps(value,ensure_ascii=False,separators=(',',':'),allow_nan=False)+'\n',encoding='utf8')
    temporary.replace(path)


def catalog_payloads(manifest):
    """The existing bundle/binding closure, with collision and leaf checks."""
    result={};folded=set()
    rows=[(r['file'],r['sha256']) for r in manifest['bundles']]
    rows += [(r['bindingFile'],r['bindingSha256']) for r in manifest['meshes'] if r.get('bindingFile')]
    for file,digest in rows:
        if not isinstance(file,str) or Path(file).name!=file or re.search(r'[<>:"\\/|?*\x00-\x1f]',file) or file.endswith(('.', ' ')):
            raise ValueError('A catalog payload is not a safe leaf filename: '+repr(file))
        asset_names.source_label(file)
        if file in result:
            if result[file]!=digest:raise ValueError('A shared payload has conflicting content authority: '+file)
            continue
        if file.casefold() in folded:raise ValueError('Case-insensitive catalog payload collision: '+file)
        if not re.fullmatch('[0-9a-f]{64}',digest):raise ValueError('A payload has no exact SHA256 authority: '+file)
        folded.add(file.casefold());result[file]=digest
    return result


def checked_catalog_path(directory,leaf=None):
    """Reject junction/symlink traversal before any publication write."""
    directory=Path(directory).absolute()
    if not directory.is_relative_to(ROOT) or directory.resolve()!=directory:
        raise ValueError('A catalog directory is outside the owned repository or traverses a link.')
    paths=[directory,*directory.parents]
    if leaf is not None:
        if not isinstance(leaf,str) or Path(leaf).name!=leaf or re.search(r'[<>:"\\/|?*\x00-\x1f]',leaf) or leaf.endswith(('.', ' ')):
            raise ValueError('A publication leaf escaped its catalog.')
        asset_names.source_label(leaf)
        paths.insert(0,directory/leaf)
    for path in paths:
        if path.exists() or path.is_symlink():
            info=path.lstat()
            if path.is_symlink() or getattr(info,'st_file_attributes',0)&0x400:
                raise ValueError('A publication path contains a reparse point: '+str(path))
        if path==ROOT:break
    return directory/leaf if leaf is not None else directory


def rewrite_catalog_files(manifest,names):
    """Change only typed public file references, including nested joint/profile properties."""
    result=copy.deepcopy(manifest)
    for row in result['bundles']:row['file']=names.get(row['file'],row['file'])
    for resource in result.get('resources',[]):
        for bundle in resource['bundles']:
            old=bundle['file'];bundle['file']=names.get(old,old)
            if bundle['name']==old:bundle['name']=bundle['file']
    def visit(value):
        if isinstance(value,dict):
            for key,child in value.items():
                if key in ('bundleFile','bindingFile') and child is not None:
                    value[key]=names.get(child,child)
                else:visit(child)
        elif isinstance(value,list):
            for child in value:visit(child)
    visit(result)
    return result


def public_payload_keys(manifest):
    """Logical selectors exclude generated payload bytes and internal asset paths."""
    keys=collections.defaultdict(list)
    for row in manifest['meshes']:
        if row.get('bundleFile'):keys[row['bundleFile']].append(('mesh',row['sourceFingerprint']))
        if row.get('bindingFile'):keys[row['bindingFile']].append(('binding',row['sourceFingerprint']))
        for joint in row.get('jointAttachments',[]):
            keys[joint['bundleFile']].append(('joint',row['sourceFingerprint'],joint['bodySourceFingerprint'],joint.get('donorPart')))
    for row in manifest['materials']:
        material=(row['sourceFingerprint'],row['materialSlot'],row['sourceShader'])
        profile=row.get('authoredProfile')
        if profile:keys[profile['bundleFile']].append(('profile',*material))
        for prop in row['properties']:
            if prop.get('textureAsset'):
                file=prop.get('bundleFile') or row.get('bundleFile')
                if file:keys[file].append(('property',*material,prop['name'],prop.get('sourceTextureFingerprint'),prop.get('sourceBodyTextureFingerprint')))
    for prop in manifest['textureRemaps']:
        keys[prop['bundleFile']].append(('remap',prop['sourceShader'],prop['name'],prop['sourceTextureFingerprint'],prop.get('sourceBodyTextureFingerprint')))
    return {file:json.dumps(sorted(set(rows),key=repr),separators=(',',':')) for file,rows in keys.items()}


def public_source_label(sources):
    """Readable true-model/LOD groups; a shared alias is not a separate payload."""
    models=collections.defaultdict(set)
    for source in sources:
        if set(source)!={'sourceFingerprint','sourceModel','part','lod'} or not re.fullmatch('[0-9a-f]{64}',source['sourceFingerprint']):
            raise ValueError('A public name has incomplete native source provenance.')
        if type(source['lod']) is not int or not 0<=source['lod']<=2 or not isinstance(source['part'],str) or not re.fullmatch('[A-Za-z][A-Za-z0-9]*',source['part']):
            raise ValueError('A public name has an invalid native part/LOD.')
        label=asset_names.source_label(source['sourceModel']).lower()
        label=label.replace('_faceshape_','_face_').replace('_shape_','_')
        if not re.fullmatch('[a-z0-9_]+',label):raise ValueError('Use actual alphanumeric native source labels.')
        models[label].add(source['lod'])
    if not models:raise ValueError('A public payload has no actual native source model.')
    def fold(values):
        values=sorted(set(values))
        if len(values)==1:return values[0]
        parts=[value.split('_') for value in values];left=right=0
        while all(len(p)>left and p[left]==parts[0][left] for p in parts):left+=1
        while all(len(p)>left+right and p[-1-right]==parts[0][-1-right] for p in parts):right+=1
        if left==right==0:
            groups=collections.defaultdict(list)
            for value in values:groups[value.split('_')[0]].append(value)
            if len(groups)>1:return '-'.join(fold(group) for _,group in sorted(groups.items()))
        middle=['_'.join(p[left:len(p)-right if right else None]) for p in parts]
        return '_'.join(parts[0][:left]+['-'.join(middle)]+(parts[0][-right:] if right else []))
    groups=collections.defaultdict(list)
    for model,levels in models.items():groups[tuple(sorted(levels))].append(model)
    return '__'.join(fold(group)+'_lod'+'-'.join(map(str,levels)) for levels,group in sorted(groups.items()))


def public_name_plan(manifest,provenance,previous):
    """One common publishing boundary for migrated and future producer payloads."""
    payloads=catalog_payloads(manifest);prior_payloads=catalog_payloads(previous);before_keys=public_payload_keys(previous);keys=public_payload_keys(manifest)
    if set(provenance)!={'schemaVersion','manifestSha256','authorities','payloads'} or provenance['schemaVersion']!=1:
        raise ValueError('Unknown public naming provenance schema.')
    if len({r['file'] for r in provenance['payloads']})!=len(provenance['payloads']):
        raise ValueError('Duplicate public naming source rows.')
    records={r['file']:r for r in provenance['payloads']}
    if not set(records)<=set(payloads):raise ValueError('Public naming provenance has an undeclared payload.')
    retained={}
    for file,key in before_keys.items():
        if not re.search(r'(?:^|[_-])[0-9a-f]{12,}(?=[_.-]|$)',file,re.I):
            if key in retained:raise ValueError('An existing logical payload has conflicting semantic names.')
            retained[key]=file
    names={};resources=[]
    for file in payloads:
        row=records.get(file)
        if row is None:
            if file not in prior_payloads or keys.get(file)!=before_keys.get(file) or re.search(r'(?:^|[_-])[0-9a-f]{12,}(?=[_.-]|$)',file,re.I):
                raise ValueError('A candidate-only or opaque public payload lacks actual source naming provenance: '+file)
            names[file]=file;continue
        if not re.fullmatch('[a-z][a-z0-9_]*',row.get('purpose','')):
            raise ValueError('A public payload has no explicit producer purpose.')
        if file not in keys:raise ValueError('A public named payload has no typed manifest consumer: '+file)
        if set(row)=={'file','purpose','textureOwner'}:
            label=public_texture_owner_label(row['textureOwner'],json.loads(keys[file]),provenance['authorities'])
        elif set(row)=={'file','purpose','sources'}:
            receivers={consumer[1] for consumer in json.loads(keys[file]) if consumer[0] in ('mesh','binding','joint','property','profile')}
            if any(source['sourceFingerprint'] not in receivers for source in row['sources']):
                raise ValueError('A public source model belongs to another payload consumer: '+file)
            label=public_source_label(row['sources'])
        else:raise ValueError('A public payload has unknown semantic naming ownership.')
        # An unchanged texture bundle can lose consumers after an explicit
        # typed transition. Validate its actual source provenance above, then
        # retain its existing semantic name rather than rename the same bytes.
        old_consumers={tuple(value)for value in json.loads(before_keys.get(file,'[]'))}
        new_consumers={tuple(value)for value in json.loads(keys[file])}
        if file in prior_payloads and payloads[file]==prior_payloads[file] and \
                not re.search(r'(?:^|[_-])[0-9a-f]{12,}(?=[_.-]|$)',file,re.I) and \
                bool(new_consumers) and new_consumers<old_consumers and \
                all(value[0]in('property','remap')for value in old_consumers):
            names[file]=file;continue
        if keys[file] in retained:
            names[file]=retained[keys[file]];continue
        extension='.binding.bytes' if file.endswith('.binding.bytes') else '.bundle'
        resources.append(asset_names.ResourceName(file,label,suffix=row['purpose']))
        # Allocate each extension as its own typed resource; final namespace
        # validation below includes retained and untouched names as well.
        names[file]=asset_names.resource_names([resources[-1]],extension,lowercase=True,strict=True,maximum=128)[file]
    folded=collections.defaultdict(list)
    for old,new in names.items():
        if len(new)>128:raise ValueError('A public resource exceeds the recipe leaf budget: '+new)
        folded[new.casefold()].append(old)
    if any(len(group)>1 for group in folded.values()):
        raise ValueError('Public semantic name collision with source provenance: '+repr({name:[records.get(file,{'file':file}) for file in group] for name,group in folded.items() if len(group)>1}))
    renamed=rewrite_catalog_files(manifest,names);after=catalog_payloads(renamed)
    inverse={new:old for old,new in names.items()}
    if {inverse[file]:digest for file,digest in after.items()}!=payloads or rewrite_catalog_files(renamed,inverse)!=manifest:
        raise ValueError('Public renaming changed a payload SHA or a non-filename declaration.')
    return names,renamed


def native_name_index(authorities):
    """Read the existing native part/head source inventories, not payload names."""
    known=collections.defaultdict(set)
    for row in authorities:
        if set(row)!={'path','sha256'}:raise ValueError('Unknown source naming authority fields.')
        authority=checked_catalog_path(Path(row['path']).parent,Path(row['path']).name)
        if sha(authority)!=row['sha256']:raise ValueError('A native source naming authority changed.')
        document=read(authority)
        for source in document.get('rows',[]):
            if 'sourceAliases' in source:
                for alias in source['sourceAliases']:
                    model=alias.get('nativePrefabPath') or alias.get('actualFactoryPath')
                    if model:
                        known[source['sourceFingerprint']].add((asset_names.source_label(model),source['part'],source['lod']))
            elif all(key in source for key in ('nativeHeadFingerprint','head','internalId','lod')):
                if asset_names.source_label(source['internalId'])!=source['head']:
                    raise ValueError('A native head label differs from its exact prefab source.')
                known[source['nativeHeadFingerprint']].add((source['head'],'Head',source['lod']))
    return known


def make_name_sources(manifest,manifest_sha,metadata_paths,previous=None):
    """Future producers supply native inventories; publication computes names."""
    authorities=[dict(path=str(checked_catalog_path(Path(path).parent,Path(path).name)),sha256=sha(path)) for path in metadata_paths]
    known=native_name_index(authorities);keys=public_payload_keys(manifest);payloads=[]
    prior=manifest if previous is None else previous;prior_files=catalog_payloads(prior);prior_keys=public_payload_keys(prior)
    for file in catalog_payloads(manifest):
        if file in prior_files and keys.get(file)==prior_keys.get(file) and not re.search(r'(?:^|[_-])[0-9a-f]{12,}(?=[_.-]|$)',file,re.I):continue
        consumers=json.loads(keys.get(file,'[]'));sources=set()
        for consumer in consumers:
            if consumer[0] in ('mesh','binding','joint','property','profile'):
                fp=consumer[1]
                sources.update((fp,model,part,lod) for model,part,lod in known[fp])
        if not sources:raise ValueError('A generated public payload has no actual source-model inventory: '+file)
        kinds={row[0] for row in consumers};parts={row[2] for row in sources}
        if kinds=={'binding'}:purpose='surface'
        elif 'joint' in kinds:purpose='joint'
        elif kinds=={'property'}:
            properties={row[4] for row in consumers}
            purpose='spec_alpha' if properties=={'_SpecTex'} else 'textures'
        else:purpose='head_base' if parts=={'Head'} else 'pn'
        payloads.append(dict(file=file,purpose=purpose,sources=[dict(sourceFingerprint=fp,sourceModel=model,part=part,lod=lod)
            for fp,model,part,lod in sorted(sources)]))
    return dict(schemaVersion=1,manifestSha256=manifest_sha,authorities=authorities,payloads=payloads)


def read_name_sources(path,manifest_path):
    """Pin metadata provenance; never infer model identity from opaque filenames."""
    path=checked_catalog_path(Path(path).parent,Path(path).name);result=read(path)
    if result.get('manifestSha256')!=sha(manifest_path):raise ValueError('Public naming sources use another candidate manifest.')
    known=native_name_index(result.get('authorities',[]))
    if not result.get('authorities'):raise ValueError('Public naming requires pinned native source provenance.')
    for payload in result.get('payloads',[]):
        for source in payload.get('sources',[]):
            actual=(source['sourceModel'],source['part'],source['lod'])
            if actual not in known[source['sourceFingerprint']]:
                raise ValueError('A public source label is not declared by its pinned native authority: '+repr(actual))
    return result


def apply_style_field(points,marks,guard=None):
    target,mask=p.humanoid_face_style_field(points,marks,marks.get('strength',1.))
    if guard is not None:
        target=points+(target-points)*guard[:,None]
        mask=mask*guard
    return target,mask


def refine_style_packet(packet,skin,marks):
    return p.refine_humanoid_face_style(packet,skin,marks,marks.get('strength',1.))



def export_current_mesh(obj):
    from UnityPy.helpers.MeshHelper import MeshHandler
    tree = obj.read_typetree()
    mesh = obj.read()
    handler = MeshHandler(mesh)
    handler.process()
    count = handler.m_VertexCount
    def flat(value, dimension=None):
        return [component for row in value or [] for component in (row if dimension is None else row[:dimension])]
    uv = []
    for channel in range(8):
        dimension = tree['m_VertexData']['m_Channels'][channel + 4]['dimension']
        if dimension:
            rows = getattr(handler, 'm_UV' + str(channel))
            uv.append(dict(channel=channel, dimension=dimension, values=flat([list(row) + [0.] * (4 - len(row)) for row in rows])))
    poses = [matrix['e%d%d' % (row, column)] for matrix in tree['m_BindPose'] for column in range(4) for row in range(4)]
    shapes = []
    shape_data = tree['m_Shapes']
    for index, channel in enumerate(shape_data['channels']):
        frames = []
        for ordinal in range(channel['frameIndex'], channel['frameIndex'] + channel['frameCount']):
            frame = shape_data['shapes'][ordinal]
            values = {key: [0.] * (count * 3) for key in ('vertices', 'normals', 'tangents')}
            for vertex in shape_data['vertices'][frame['firstVertex']:frame['firstVertex'] + frame['vertexCount']]:
                for target, source in (('vertices', 'vertex'), ('normals', 'normal'), ('tangents', 'tangent')):
                    if target == 'normals' and not frame['hasNormals'] or target == 'tangents' and not frame['hasTangents']:
                        continue
                    offset = vertex['index'] * 3
                    values[target][offset:offset+3] = [vertex[source][axis] for axis in ('x', 'y', 'z')]
            frames.append(dict(weight=shape_data['fullWeights'][ordinal], **values))
        shapes.append(dict(index=index, name=channel['name'], frames=frames))
    bounds = tree['m_LocalAABB']
    colors = flat(handler.m_Colors)
    color_channel = tree['m_VertexData']['m_Channels'][3]
    if colors and color_channel['dimension']:
        # MeshHandler retains normalized integer streams as storage integers.
        # Unity's public color channel exposes their floating-point values.
        format = color_channel['format']
        if format in (2, 3, 4, 5):
            denominator = {2: 255., 3: 127., 4: 65535., 5: 32767.}[format]
            colors = [max(-1., value / denominator) for value in colors]
    weights,indices=four_slot_skin(handler.m_BoneWeights,handler.m_BoneIndices,count)
    result: dict[str, Any] = dict(name=tree['m_Name'], nativeReadable=tree['m_IsReadable'], vertexCount=count,
        indexFormat=tree['m_IndexFormat'], vertices=flat(handler.m_Vertices, 3), normals=flat(handler.m_Normals, 3),
        tangents=flat(handler.m_Tangents), colors=colors, bindPoses=poses,
        weights=weights, boneIndices=indices,
        subMeshes=[flat(triangles) for triangles in handler.get_triangles()], uv=uv, shapes=shapes,
        bounds=[bounds['m_Center'][axis] for axis in ('x', 'y', 'z')] + [bounds['m_Extent'][axis] * 2 for axis in ('x', 'y', 'z')])
    result['sourceFingerprint'] = fingerprint(result)[0]
    return result


def recipe_mesh(packet):
    return dict(name=packet['name'],vertices=packet['vertices'],normals=packet['normals'],tangents=packet['tangents'],
        colors=packet['colors'],uvChannels=[dict(channel=u['channel'],dimension=u['dimension'],
            values=[v for i in range(packet['vertexCount']) for v in u['values'][i*4:i*4+u['dimension']]]) for u in packet['uv']],
        subMeshes=[dict(topology='Triangles',indices=s) for s in packet['subMeshes']],
        bounds=dict(center=packet['bounds'][:3],size=packet['bounds'][3:]),indexFormat='UInt32' if packet['indexFormat'] else 'UInt16',
        skin=dict(bindPoses=packet['bindPoses'],boneWeights=packet['weights'],boneIndices=packet['boneIndices']),
        blendShapes=[dict(name=s['name'],frames=s['frames']) for s in packet['shapes']])


def source_owner():
    import numpy as np
    owner=ROOT/'tools/blender-models/CharacterFaces'
    scope=dict(np=np,math=math,copy=copy,gzip=gzip,hashlib=hashlib,json=json,struct=struct,Path=Path,
        REPO=ROOT,SOURCE=ROOT/'tools/asset-authoring/workspace/FaceRefinement/NativeSources',
        CONVERT=np.array([[1.,0,0,0],[0,0,-1,0],[0,1,0,0],[0,0,0,1]]),
        resource_naming=asset_names,IMAGE_NAMES={})
    for name,environment in [('generate.py',scope)]:
        path=owner/name
        nodes: list[ast.stmt]=[n for n in ast.parse(path.read_text(encoding='utf8')).body if isinstance(n,ast.FunctionDef)]
        exec(compile(ast.Module(body=nodes,type_ignores=[]),str(path),'exec'),environment)
    art=types.SimpleNamespace(**scope)
    env=dict(scope,art=art,fingerprint=fingerprint,MATERIAL_CHANNEL_CONTRACTS={})
    path=owner/'production.py'
    nodes: list[ast.stmt]=[n for n in ast.parse(path.read_text(encoding='utf8')).body if isinstance(n,ast.FunctionDef)]
    exec(compile(ast.Module(body=nodes,type_ignores=[]),str(path),'exec'),env)
    return types.SimpleNamespace(**env),art


def audit_source_channels(native,authored,skin=None,marks=None):
    for channel in ('vertexCount','uv','weights','boneIndices','bindPoses','subMeshes','indexFormat'):
        if native[channel]!=authored[channel]:raise ValueError('A source-owned native channel changed: '+channel)
    physical=0
    for before,after in zip(native['shapes'],authored['shapes']):
        if before['index']!=after['index'] or before['name']!=after['name'] or len(before['frames'])!=len(after['frames']):raise ValueError('A native physical channel identity/order changed.')
        for a,b in zip(before['frames'],after['frames']):
            if a['weight']!=b['weight']:raise ValueError('A native physical frame weight changed.')
            for key in ('vertices','normals','tangents'):
                if len(b[key])!=native['vertexCount']*3 or not np.isfinite(b[key]).all():raise ValueError('An authored physical frame is incomplete or nonfinite.')
            physical+=1
    if len(native['shapes'])!=len(authored['shapes']):raise ValueError('A native physical channel count changed.')
    for key,size in [('vertices',3),('normals',3),('tangents',4)]:
        if len(authored[key])!=native['vertexCount']*size or not np.isfinite(authored[key]).all():raise ValueError('An authored base channel is incomplete or nonfinite.')
    if skin is not None:
        if marks is None: raise ValueError("A skin channel audit requires its source landmarks.")
        protected=p.humanoid_face_contact_guard(native,skin,marks.get('eyeContactRegions',()))==0
        for key,size in [('vertices',3),('normals',3),('tangents',4)]:
            if not np.array_equal(np.asarray(native[key],dtype=np.float32).view(np.uint32).reshape(-1,size)[protected],
                    np.asarray(authored[key],dtype=np.float32).view(np.uint32).reshape(-1,size)[protected]):
                raise ValueError('A source-owned eye contact base channel changed: '+key)
        for before_shape,after_shape in zip(native['shapes'],authored['shapes']):
            for before_frame,after_frame in zip(before_shape['frames'],after_shape['frames']):
                for key in ('vertices','normals','tangents'):
                    if not np.array_equal(np.asarray(before_frame[key],dtype=np.float32).view(np.uint32).reshape(-1,3)[protected],
                            np.asarray(after_frame[key],dtype=np.float32).view(np.uint32).reshape(-1,3)[protected]):
                        raise ValueError('A source-owned eye contact physical frame changed: '+key)
    if fingerprint(authored)[0]!=authored['sourceFingerprint']:raise ValueError('The emitted authored identity is inconsistent.')
    return physical


def facial(skin):
    names=[b.rsplit('/',1)[-1].lower() if b else '' for b in skin['bones']]
    return any(name.startswith(('b_eye_','b_eyelid_','b_lip_','b_nostril_')) for name in names) or any(token in skin['mesh'].lower() for token in ('teeth','tongue','jaw','eyeoccluder','eye_occluder','eyelash'))


def primary(asset):
    for skin in asset['skins']:
        if p.lod_index(skin):continue
        packet=art.load_packet(skin)
        _,_,marks=art.landmarks(packet,skin,art.CONVERT@art.matrix(skin['rendererToPrefab']))
        if all(marks[k] is not None for k in ('left','right','mouth','nose','head')):return skin,marks
    raise ValueError('A combined NPC has no source-owned facial primary')


def extract_current():
    parser = argparse.ArgumentParser()
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--selection', type=Path)
    parser.add_argument('--material-inputs', type=Path)
    args = parser.parse_args()
    if args.material_inputs:
        request_path=args.material_inputs.absolute();request=material_paths(read(request_path),request_path.parent)
        request['outputDirectory']=str(args.output.absolute())
        result=extract_material_native(request)
        result.update(sourceRequest=dict(path=str(request_path),sha256=sha(request_path)),extractionOwnerSha256=sha(__file__))
        write(checked_catalog_path(args.output,'NativeBc3.json'),result)
        print(json.dumps(dict(status=result['status'],sources=len(result['rows']))));return
    public = PUBLIC
    manifest = read(public / 'CharacterModels.manifest.json')
    census = read(ROOT / 'tools/asset-authoring/workspace/FaceRefinement/NativeSources/Census.json')
    humanoids = [a for a in census['assets'] if '/EditableCharacters/' in a['internalId'] and a['name'].split('_')[0] in ('Human', 'Elf', 'Dwarf', 'Halfling', 'Gnome', 'Tiefling')]
    if args.selection:
        selected = set(read(args.selection)['names'])
        humanoids = [a for a in census['assets'] if a['name'] in selected]
    keys = {s['sourceFingerprint'] for a in humanoids for s in a['skins']}
    entries = [m for m in manifest['meshes'] if m['sourceFingerprint'] in keys and m['mode'] == 'authored']
    bundles = collections.defaultdict(list)
    for entry in entries:
        bundles[entry['bundleFile']].append(entry)
    args.output.mkdir(parents=True, exist_ok=True)
    records = []
    for file, group in sorted(bundles.items()):
        environment = UnityPy.load(str(public / file))
        container = {name.lower(): value for name, value in environment.container.items()}
        packets = {}
        for entry in group:
            asset = entry['authoredMeshAsset']
            if asset not in packets:
                pointer = container.get(asset.lower())
                if pointer is None:
                    matches = [value for key, value in container.items() if Path(key).stem.lower() == asset.lower()]
                    if len(matches) != 1:
                        raise ValueError('Ambiguous catalog Mesh asset: ' + file + ':' + asset)
                    pointer = matches[0]
                packet = export_current_mesh(pointer.deref())
                if packet['sourceFingerprint'] != entry['authoredFingerprint']:
                    raise ValueError('Extracted Mesh identity differs: ' + file + ':' + asset + ':' + packet['sourceFingerprint'] + ' expected ' + entry['authoredFingerprint'])
                packets[asset] = packet
            else:
                packet = packets[asset]
            path = args.output / (entry['sourceFingerprint'] + '.json.gz')
            with gzip.open(path, 'wt', encoding='utf8') as stream:
                json.dump(packet, stream, separators=(',', ':'))
            records.append(dict(sourceFingerprint=entry['sourceFingerprint'], authoredFingerprint=entry['authoredFingerprint'],
                bundleFile=file, asset=asset, path=str(path.resolve()), sha256=hashlib.sha256(path.read_bytes()).hexdigest()))
        print(json.dumps(dict(bundle=file, meshes=len(group))), flush=True)
    (args.output / 'Index.json').write_text(json.dumps(dict(status='PASS_CURRENT_CATALOG_MESH_EXPORT',
        manifestSha256=hashlib.sha256((public / 'CharacterModels.manifest.json').read_bytes()).hexdigest(),
        sourceDirectory=str(public),sourceRecipeVersion=manifest['recipeVersion'],
        humanoidAssets=[dict(name=a['name'], sourceContentKey=a['sourceContentKey']) for a in humanoids],
        meshes=records), separators=(',', ':')), encoding='utf8')


def generate_humanoids():
    current = read(require_workspace() / 'CurrentHumanoids/Index.json')
    if 'HumanoidContours1' in current.get('sourceRecipeVersion',''):
        raise ValueError('This source already contains the face style. Use the retained source catalog.')
    wanted = {a['sourceContentKey'] for a in current['humanoidAssets']}
    census = read(ROOT / 'tools/asset-authoring/workspace/FaceRefinement/NativeSources/Census.json')
    exported = {r['sourceFingerprint']: r for r in current['meshes']}
    def current_packet(skin):
        item = exported.get(skin['sourceFingerprint'])
        if item:
            with gzip.open(item['path'], 'rt', encoding='utf8') as stream:
                packet = json.load(stream)
            if p.fingerprint(packet)[0] != item['authoredFingerprint']:
                raise ValueError('Adopted baseline identity changed')
            return packet
        return art.load_packet(skin)
    selected = [a for a in census['assets'] if a['sourceContentKey'] in wanted]
    parents = {a['sourceContentKey']: a['sourceContentKey'] for a in selected}
    def find(key):
        while parents[key] != key:
            parents[key] = parents[parents[key]]
            key = parents[key]
        return key
    sharing = {}
    native_marks = {}
    for asset in selected:
        primary = next(s for s in asset['skins'] if s['path'].lower().endswith('lod0') and 'faceshape' in s['path'].lower())
        _, _, marks = art.landmarks(art.load_packet(primary), primary, art.CONVERT @ art.matrix(primary['rendererToPrefab']))
        marks['eyeContactRegions']=p.humanoid_face_contact_regions(current_packet(primary),primary)
        native_marks[asset['sourceContentKey']] = marks
        for skin in asset['skins']:
            old = sharing.setdefault(skin['sourceFingerprint'], asset['sourceContentKey'])
            parents[find(asset['sourceContentKey'])] = find(old)
    groups = collections.defaultdict(list)
    for asset in selected:
        groups[find(asset['sourceContentKey'])].append(asset)
    fields = {}
    group_reports = []
    for key, members in groups.items():
        marks = {name: p.np.mean([native_marks[a['sourceContentKey']][name] for a in members], axis=0)
            for name in ('left', 'right', 'mouth', 'nose', 'head')}
        marks['eyeContactRegions']=[region for a in members for region in native_marks[a['sourceContentKey']]['eyeContactRegions']]
        strength = 1.
        reductions = []
        seen = set()
        for asset in members:
            for skin in asset['skins']:
                fp = skin['sourceFingerprint']
                if fp in seen:
                    continue
                seen.add(fp)
                packet = current_packet(skin)
                transform = art.CONVERT @ art.matrix(skin['rendererToPrefab'])
                before = p.np.asarray(packet['vertices']).reshape(-1,3)
                guard=p.humanoid_face_contact_guard(packet,skin,marks['eyeContactRegions'])
                triangles = p.np.concatenate([p.np.asarray(t).reshape(-1,3) for t in packet['subMeshes']])
                frames = [None] + [frame for shape in packet['shapes'] for frame in shape['frames']]
                for frame in frames:
                    posed = before if frame is None else before + p.np.asarray(frame['vertices']).reshape(-1,3)
                    target, _ = apply_style_field(art.points(posed,transform), marks,guard)
                    after = art.points(target,p.np.linalg.inv(transform))
                    _, review = p.preserve_surface_orientation(posed, after, triangles, transform)
                    factor = review['surfaceScaleFactor']
                    strength = min(strength, factor)
                    if factor < 1:
                        reductions.append(dict(sourceFingerprint=fp,rendererPath=skin['path'],frame='base' if frame is None else frame['weight'],**review))
        marks['strength'] = strength
        for asset in members:
            fields[asset['sourceContentKey']] = marks
        group_reports.append(dict(sourceKeys=[a['sourceContentKey'] for a in members],names=[a['name'] for a in members],
            strength=strength,sharedSources=len(seen),reductions=reductions))
        print(json.dumps(dict(preflight=members[0]['name'],sources=len(members),strength=strength)),flush=True)
    output = require_workspace() / 'ModernHumanoids'
    (output / 'Meshes').mkdir(parents=True, exist_ok=True)
    generated = {}
    shared_conflicts = []
    errors = []
    assets = []
    for asset in census['assets']:
        if asset['sourceContentKey'] not in wanted:
            continue
        primaries = [s for s in asset['skins'] if s['path'].lower().endswith('lod0') and any(m and '/Skin' in m['shader'] for m in s['materials'])]
        primary = next((s for s in primaries if 'faceshape' in s['path'].lower()), None)
        if primary is None:
            errors.append(dict(name=asset['name'], reason='No source-owned skin primary'))
            continue
        marks = fields[asset['sourceContentKey']]
        if any(marks[k] is None for k in ('left', 'right', 'mouth', 'nose')):
            errors.append(dict(name=asset['name'], reason='Incomplete source-owned facial landmarks'))
            continue
        asset_rows = []
        try:
            for skin in asset['skins']:
                fp = skin['sourceFingerprint']
                packet = current_packet(skin)
                candidate, metrics = refine_style_packet(packet, skin, marks)
                # Channel and expression identities remain owned by the source.
                audit_source_channels(packet, candidate,skin,marks)
                if fp in generated and generated[fp]['authoredFingerprint'] != candidate['sourceFingerprint']:
                    shared_conflicts.append(dict(sourceFingerprint=fp, first=generated[fp]['firstOwner'],
                        second=asset['name'], firstAuthored=generated[fp]['authoredFingerprint'], secondAuthored=candidate['sourceFingerprint']))
                if fp not in generated:
                    path = output / 'Meshes' / (fp + '.json.gz')
                    with gzip.open(path, 'wt', encoding='utf8') as stream:
                        json.dump(candidate, stream, separators=(',', ':'))
                    generated[fp] = dict(sourceFingerprint=fp, authoredFingerprint=candidate['sourceFingerprint'],
                        baselineFingerprint=packet['sourceFingerprint'], path=str(path), sha256=sha(path), firstOwner=asset['name'], **metrics)
                asset_rows.append(dict(rendererPath=skin['path'], sourceFingerprint=fp, **metrics))
            assets.append(dict(name=asset['name'], sourceContentKey=asset['sourceContentKey'], primary=primary['sourceFingerprint'], meshes=asset_rows))
            print(json.dumps(dict(name=asset['name'], meshes=len(asset_rows))), flush=True)
        except Exception as error:
            errors.append(dict(name=asset['name'], reason=str(error)))
    result = dict(status='PASS_GENERATED_HUMANOID_SOURCES' if not errors and not shared_conflicts else 'REVIEW_REQUIRED',
        currentManifestSha256=current['manifestSha256'],sourceDirectory=current.get('sourceDirectory',str(PUBLIC)),
        sourceRecipeVersion=current.get('sourceRecipeVersion'),generatorSha256=sha((ROOT/'tools/blender-models/CharacterFaces/production.py')),
        assets=assets, meshes=list(generated.values()), sourceGroups=group_reports,sharedConflicts=shared_conflicts, errors=errors,
        unchangedNonHumanoidSourceKeys=len({a['sourceContentKey'] for a in census['assets']} - wanted), artAccepted=False)
    write(output / 'Index.json', result)
    print(json.dumps(dict(status=result['status'], assets=len(assets), meshes=len(generated), conflicts=len(shared_conflicts), errors=errors)), flush=True)


def generate_npcs():
    exported=read(require_workspace() /'CurrentNpcHumanoids/Index.json');rows={r['sourceFingerprint']:r for r in exported['meshes']}
    if 'HumanoidContours1' in exported.get('sourceRecipeVersion',''):
        raise ValueError('This source already contains the face style. Use the retained source catalog.')
    wanted={a['sourceContentKey'] for a in exported['humanoidAssets']}
    census=read(ROOT/'tools/asset-authoring/workspace/FaceRefinement/NativeSources/Census.json')
    assets=[a for a in census['assets'] if a['sourceContentKey'] in wanted];families=collections.defaultdict(list)
    def packet(skin):
        item=rows.get(skin['sourceFingerprint'])
        if not item:return art.load_packet(skin)
        with gzip.open(item['path'],'rt',encoding='utf8') as stream:value=json.load(stream)
        p.require(p.fingerprint(value)[0]==item['authoredFingerprint'],'The NPC current Mesh changed')
        return value
    for asset in assets:
        skin,marks=primary(asset);families[skin['sourceFingerprint']].append((asset,skin,marks))
    output=require_workspace() /'ModernNpcHumanoids';(output/'Meshes').mkdir(parents=True,exist_ok=True)
    completed=[];generated={};groups=[]
    for family in families.values():
        marks: dict[str, Any]={key:np.mean([row[2][key] for row in family],axis=0) for key in ('left','right','mouth','nose','head')}
        marks['eyeContactRegions']=[region for _,skin,_ in family for region in p.humanoid_face_contact_regions(packet(skin),skin)]
        strength=1.
        for asset,_,_ in family:
            for skin in asset['skins']:
                if not facial(skin):continue
                original=packet(skin);base=np.asarray(original['vertices']).reshape(-1,3);transform=art.CONVERT@art.matrix(skin['rendererToPrefab'])
                guard=p.humanoid_face_contact_guard(original,skin,marks['eyeContactRegions'])
                tri=np.concatenate([np.asarray(s).reshape(-1,3) for s in original['subMeshes']])
                for frame in [None]+[f for shape in original['shapes'] for f in shape['frames']]:
                    posed=base if frame is None else base+np.asarray(frame['vertices']).reshape(-1,3)
                    target,_=apply_style_field(art.points(posed,transform),marks,guard)
                    _,review=p.preserve_surface_orientation(posed,art.points(target,np.linalg.inv(transform)),tri,transform)
                    strength=min(strength,review['surfaceScaleFactor'])
        marks['strength']=strength
        for asset,main_skin,_ in family:
            asset_rows=[]
            for skin in asset['skins']:
                if not facial(skin):continue
                fp=skin['sourceFingerprint'];original=packet(skin)
                changed,metrics=refine_style_packet(original,skin,marks)
                transform=art.CONVERT@art.matrix(skin['rendererToPrefab']);_,mask=apply_style_field(art.points(original['vertices'],transform),marks)
                outside=mask==0
                for channel,size in (('vertices',3),('normals',3),('tangents',4)):
                    before=np.asarray(original[channel]).reshape(-1,size);after=np.asarray(changed[channel]).reshape(-1,size)
                    after[outside]=before[outside];changed[channel]=after.ravel().tolist()
                for before_shape,after_shape in zip(original['shapes'],changed['shapes']):
                    for before_frame,after_frame in zip(before_shape['frames'],after_shape['frames']):
                        for channel in ('vertices','normals','tangents'):
                            before=np.asarray(before_frame[channel]).reshape(-1,3);after=np.asarray(after_frame[channel]).reshape(-1,3)
                            after[outside]=before[outside];after_frame[channel]=after.ravel().tolist()
                changed['sourceFingerprint']=p.fingerprint(changed)[0];changed['bounds']=p.physical_mesh_bounds(changed)
                audit_source_channels(original,changed,skin,marks)
                if fp in generated:p.require(generated[fp]['authoredFingerprint']==changed['sourceFingerprint'],'A shared combined NPC target differs')
                else:
                    path=output/'Meshes'/(fp+'.json.gz')
                    with gzip.open(path,'wt',encoding='utf8') as stream:json.dump(changed,stream,separators=(',',':'))
                    generated[fp]=dict(sourceFingerprint=fp,authoredFingerprint=changed['sourceFingerprint'],baselineFingerprint=original['sourceFingerprint'],
                        path=str(path),sha256=sha(path),firstOwner=asset['name'],unchangedOutsideFaceVertices=int(outside.sum()),**metrics)
                asset_rows.append(dict(sourceFingerprint=fp,rendererPath=skin['path'],**metrics))
            completed.append(dict(name=asset['name'],sourceContentKey=asset['sourceContentKey'],primary=main_skin['sourceFingerprint'],meshes=asset_rows))
            print(json.dumps(dict(name=asset['name'],meshes=len(asset_rows),strength=strength)),flush=True)
        groups.append(dict(names=[row[0]['name'] for row in family],strength=strength))
    write(output/'Index.json',dict(status='PASS_GENERATED_HUMANOID_SOURCES',currentManifestSha256=exported['manifestSha256'],
        sourceDirectory=exported.get('sourceDirectory',str(PUBLIC)),sourceRecipeVersion=exported.get('sourceRecipeVersion'),
        generatorSha256=sha(Path(__file__)),assets=completed,meshes=list(generated.values()),sourceGroups=groups,
        sharedConflicts=[],errors=[],artAccepted=False,combinedBodyOutsideFaceAttributesExact=True))


def prepare_bundles():
    parser=argparse.ArgumentParser();parser.add_argument('--generation',type=Path,default=require_workspace() /'ModernHumanoids/Index.json')
    parser.add_argument('--destination',type=Path,default=require_workspace() /'UnityInput');args=parser.parse_args()
    generated=read(args.generation)
    public=Path(generated.get('sourceDirectory',str(PUBLIC)))
    manifest_path=public/'CharacterModels.manifest.json';manifest=read(manifest_path)
    if generated['status']!='PASS_GENERATED_HUMANOID_SOURCES' or sha(manifest_path)!=generated['currentManifestSha256']:
        raise ValueError('The current catalog or humanoid generation is unresolved.')
    source_entries={m['sourceFingerprint']:m for m in manifest['meshes']}
    candidates={m['sourceFingerprint']:m for m in generated['meshes']}
    # Hair geometry uses a source-attachment binding and its own authoring owner.
    held=[fp for fp in candidates if source_entries.get(fp,{}).get('bindingFile')]
    for fp in held:candidates.pop(fp)
    assignments={}
    for asset in generated['assets']:
        host=source_entries[asset['primary']]['bundleFile']
        for row in asset['meshes']:
            fp=row['sourceFingerprint']
            if fp in candidates:
                assignments.setdefault(fp,source_entries[fp].get('bundleFile') or host)
    affected=set(assignments.values())
    roots=collections.defaultdict(dict)
    for row in manifest['meshes']:
        if row['mode']=='authored' and row['bundleFile'] in affected:
            roots[row['bundleFile']][('Mesh',row['authoredMeshAsset'])]=row['sourceFingerprint']
    for row in manifest['materials']:
        for prop in row['properties']:
            if prop['kind'] in ('texture','texture-remap'):
                file=prop.get('bundleFile') or row['bundleFile']
                if file in affected:roots[file][('Texture2D',prop['textureAsset'])]=None
    for prop in manifest['textureRemaps']:
        if prop['bundleFile'] in affected:roots[prop['bundleFile']][('Texture2D',prop['textureAsset'])]=None
    destination=args.destination
    destination.mkdir(exist_ok=True)
    jobs=[];mapping={};native_added=0
    declarations={r['file']:r for r in manifest['bundles']}
    for ordinal,file in enumerate(sorted(affected)):
        folder=destination/('Job%03d'%ordinal);(folder/'Source').mkdir(parents=True,exist_ok=True)
        source=public/file
        if sha(source)!=declarations[file]['sha256']:raise ValueError('A source bundle changed: '+file)
        link=folder/'Source'/file
        if not link.exists():os.link(source,link)
        environment=UnityPy.load(str(source));container=environment.container
        def select(selector,kind):
            found=[(key,value) for key,value in container.items() if
                (key.lower()==selector.lower() or Path(key).stem.lower()==selector.lower()) and value.type.name==kind]
            if len(found)!=1:raise ValueError('Missing/ambiguous source selector '+file+':'+selector)
            return found[0]
        meshes=[];retained=[];names=set();replaced=set()
        for (kind,selector),fp in roots[file].items():
            asset,pointer=select(selector,kind);name=pointer.deref().peek_name()
            if name in names:raise ValueError('Duplicate selected output name: '+file+':'+name)
            names.add(name)
            if kind=='Mesh' and fp in candidates:
                item=candidates[fp]
                if sha(item['path'])!=item['sha256']:raise ValueError('The generated mesh changed')
                with gzip.open(item['path'],'rt',encoding='utf8') as stream:packet=json.load(stream)
                packet['name']=name;meshes.append(recipe_mesh(packet));replaced.add(fp)
                mapping[fp]=dict(bundleFile=file,assetName=name,authoredFingerprint=item['authoredFingerprint'])
            else:
                retained.append(dict(asset=asset,type=kind,name=name))
        for fp,host in assignments.items():
            if host!=file or fp in replaced:continue
            item=candidates[fp]
            with gzip.open(item['path'],'rt',encoding='utf8') as stream:packet=json.load(stream)
            name=packet['name']
            if name in names:name='NativePart_'+fp[:24]
            names.add(name);packet['name']=name;meshes.append(recipe_mesh(packet));native_added+=1
            mapping[fp]=dict(bundleFile=file,assetName=name,authoredFingerprint=item['authoredFingerprint'])
        recipe=dict(schemaVersion=1,bundleName=file.lower(),meshes=meshes,textures=[])
        if retained:recipe['sourceBundles']=[dict(path='Source/'+file,sha256=sha(source),assets=retained)]
        path=folder/'mesh-recipe.json'
        if path.exists():raise ValueError('The prepared job already exists')
        encoding=encode_recipe(recipe,path)
        job=dict(outputFile=file,recipe=dict(path=str(path),sha256=sha(path)),meshes=len(meshes),textures=0,
            sourceAssets=len(retained),sourceBundles=int(bool(retained)),numericBytes=encoding['bytes'])
        jobs.append(job);print(json.dumps(dict(job=ordinal,file=file,meshes=len(meshes),retained=len(retained))),flush=True)
    (destination/'Plan.json').write_text(json.dumps(dict(status='READY_HUMANOID_ENGINE_BATCH',
        baseManifest=dict(path=str(manifest_path),sha256=sha(manifest_path)),generation=str(args.generation.resolve()),
        jobs=jobs,jobCount=len(jobs),mapping=mapping,heldHairBindingSources=held,nativeAdded=native_added),separators=(',',':')),encoding='utf8')


def humanoid_sources(census,scope='all'):
    selected=[]
    for asset in census['assets']:
        editable='/EditableCharacters/' in asset['internalId'] and asset['name'].split('_')[0] in ('Human','Elf','Dwarf','Halfling','Gnome','Tiefling')
        npc='/Monsters/NPC/' in asset['internalId']
        if scope=='editable' and editable or scope=='npc' and npc or scope=='all' and (editable or npc):selected.append(asset)
    return selected


def select_scope():
    parser=argparse.ArgumentParser();parser.add_argument('--scope',choices=('editable','npc','all'),default='all');parser.add_argument('--output',type=Path,required=True)
    args=parser.parse_args();census=read(ROOT/'tools/asset-authoring/workspace/FaceRefinement/NativeSources/Census.json')
    selected=humanoid_sources(census,args.scope)
    write(args.output,dict(names=sorted({a['name'] for a in selected}),sourceKeys=sorted({a['sourceContentKey'] for a in selected}),
        scope=args.scope,nonhumanFormsPreserved=True))


def capture_source_catalog():
    parser=argparse.ArgumentParser();parser.add_argument('--destination',type=Path,required=True)
    parser.add_argument('--name-sources',type=Path)
    parser.add_argument('--name-source-metadata',type=Path,action='append');args=parser.parse_args()
    if args.name_sources and args.name_source_metadata:raise ValueError('Select pinned naming sources or their native metadata inputs.')
    destination=checked_catalog_path(args.destination)
    checked_catalog_path(PUBLIC)
    if not destination.is_relative_to(ROOT/'tools') or destination==PUBLIC:
        raise ValueError('Source catalog output must be a separate tools workspace directory.')
    if destination.exists() and any(destination.iterdir()):raise ValueError('The source catalog output is not empty.')
    destination.mkdir(parents=True,exist_ok=True)
    manifest=read(PUBLIC/'CharacterModels.manifest.json')
    inputs=catalog_payloads(manifest)
    name_sources=(read_name_sources(args.name_sources,PUBLIC/'CharacterModels.manifest.json') if args.name_sources else
        make_name_sources(manifest,sha(PUBLIC/'CharacterModels.manifest.json'),args.name_source_metadata) if args.name_source_metadata else None)
    for file,expected in inputs.items():
        source=checked_catalog_path(PUBLIC,file)
        if sha(source)!=expected:raise ValueError('Source catalog data hash differs: '+file)
        os.link(source,destination/file)
    (destination/'CharacterModels.manifest.json').write_bytes((PUBLIC/'CharacterModels.manifest.json').read_bytes())
    write(destination/'SourceCatalog.json',dict(status='PASS_IMMUTABLE_SOURCE_CATALOG_CAPTURE',
        manifestSha256=sha(destination/'CharacterModels.manifest.json'),source=str(PUBLIC),files=inputs,
        publicNameSources=name_sources,
        ownership='Hard links are immutable inputs. Public replacements use new files and directory-entry replacement.'))
    print(json.dumps(dict(status='PASS_IMMUTABLE_SOURCE_CATALOG_CAPTURE',files=len(inputs),destination=str(destination))))


def finalize_catalog():
    parser=argparse.ArgumentParser();parser.add_argument('--plan',type=Path,action='append',required=True)
    parser.add_argument('--results',type=Path,required=True);parser.add_argument('--destination',type=Path,required=True)
    args=parser.parse_args();plans=[read(path) for path in args.plan]
    source=PUBLIC;manifest=read(source/'CharacterModels.manifest.json');source_hash=sha(source/'CharacterModels.manifest.json')
    if source==ROOT/'SolastaUnfinishedBusiness/BlenderModels/CharacterModels':
        raise ValueError('Finalize against the retained source catalog, using --source-directory.')
    if any(plan['baseManifest']['sha256']!=source_hash for plan in plans):raise ValueError('The plans use different source catalogs.')
    result_document=read(args.results);results=result_document.get('results') if isinstance(result_document,dict) else result_document
    if results is None: raise ValueError('The material build result list is missing.')
    by_file={row['file']:row for row in results}
    if len(by_file)!=len(results):raise ValueError('The completed output index has duplicate bundle files.')
    expected_jobs={job['outputFile']:job for plan in plans for job in plan['jobs']}
    if len(expected_jobs)!=sum(len(plan['jobs']) for plan in plans):raise ValueError('Input plans contain duplicate output bundle files.')
    if set(expected_jobs)!=set(by_file):raise ValueError('The completed output index differs from the full recipe closure.')
    destination=args.destination.resolve()
    if not destination.is_relative_to(ROOT/'tools') or destination==source:raise ValueError('Use a separate tools candidate directory.')
    destination.mkdir(parents=True,exist_ok=True)
    if any(destination.iterdir()):raise ValueError('The candidate catalog output is not empty.')
    original=copy.deepcopy(manifest)
    mappings={}
    for plan in plans:
        for key,value in plan['mapping'].items():
            if key in mappings and mappings[key]!=value:raise ValueError('Shared target catalog mapping differs.')
            mappings[key]=value
    selectors={};containers={};mesh_fingerprints={};files=[]
    for file,job in expected_jobs.items():
        result=by_file[file];bundle=Path(result['bundlePath'])
        if sha(bundle)!=result['sha256']:raise ValueError('A completed bundle changed: '+file)
        report=read(result['report'])
        if not report.get('success'):raise ValueError('The member create/save/reload report did not pass: '+file)
        if len(report.get('meshes',[]))!=job['meshes'] or len(report.get('sourceAssets',[]))!=job['sourceAssets']:
            raise ValueError('A completed member asset count differs: '+file)
        recipe=read(job['recipe']['path'])
        selected=[]
        for source_bundle in recipe.get('sourceBundles',[]):selected.extend(source_bundle['assets'])
        selectors[file]=selected
        environment=UnityPy.load(str(bundle));entries=[]
        for asset,pointer in environment.container.items():
            if pointer.type.name not in ('Mesh','Texture2D'):raise ValueError('Unexpected candidate asset type: '+file)
            obj=pointer.deref();entries.append(dict(asset=asset,name=obj.peek_name(),type=pointer.type.name))
            if pointer.type.name=='Mesh':mesh_fingerprints[file,asset]=export_current_mesh(obj)['sourceFingerprint']
        if len(entries)!=job['meshes']+job['sourceAssets']:raise ValueError('The reloaded candidate contains unexpected assets: '+file)
        containers[file]=entries
        os.link(bundle,destination/file)
        files.append(dict(file=file,sha256=result['sha256'],meshes=job['meshes'],preservedAssets=job['sourceAssets']))
    def resolve(file,selector,kind):
        name=selector
        retained=[row for row in selectors[file] if row['type']==kind and
            (row['asset']==selector or row['name']==selector or Path(row['asset']).stem.lower()==selector.lower())]
        if len(retained)==1:name=retained[0]['name']
        matches=[row for row in containers[file] if row['type']==kind and
            (row['name']==name or row['asset']==name or Path(row['asset']).stem.lower()==name.lower())]
        if len(matches)!=1:raise ValueError('The output asset reference is missing or ambiguous: '+file+':'+selector)
        return matches[0]['asset']
    modified=[];preserved_meshes=0
    for row in manifest['meshes']:
        fp=row['sourceFingerprint'];mapping=mappings.get(fp)
        if mapping:
            row.update(mode='authored',bundleFile=mapping['bundleFile'],authoredFingerprint=mapping['authoredFingerprint'],
                authoredMeshAsset=resolve(mapping['bundleFile'],mapping['assetName'],'Mesh'))
            modified.append(fp)
        elif row['mode']=='authored' and row['bundleFile'] in containers:
            row['authoredMeshAsset']=resolve(row['bundleFile'],row['authoredMeshAsset'],'Mesh');preserved_meshes+=1
        if row['mode']=='authored' and row['bundleFile'] in containers:
            if mesh_fingerprints[row['bundleFile'],row['authoredMeshAsset']]!=row['authoredFingerprint']:
                raise ValueError('The reloaded candidate Mesh differs from its catalog: '+fp)
    texture_references=0
    for material in manifest['materials']:
        for prop in material['properties']:
            if prop['kind'] not in ('texture','texture-remap'):continue
            file=prop.get('bundleFile') or material.get('bundleFile')
            if file in containers:prop['textureAsset']=resolve(file,prop['textureAsset'],'Texture2D');texture_references+=1
    for prop in manifest['textureRemaps']:
        if prop['bundleFile'] in containers:
            prop['textureAsset']=resolve(prop['bundleFile'],prop['textureAsset'],'Texture2D');texture_references+=1
    for row in manifest['bundles']:
        if row['file'] in by_file:row['sha256']=by_file[row['file']]['sha256']
        else:os.link(source/row['file'],destination/row['file'])
    for row in manifest['meshes']:
        if row.get('bindingFile') and not (destination/row['bindingFile']).exists():os.link(source/row['bindingFile'],destination/row['bindingFile'])
    manifest['recipeVersion']+='-HumanoidContours1'
    def material_semantics(rows):
        result=copy.deepcopy(rows)
        for material in result:
            for prop in material['properties']:
                if prop['kind'] in ('texture','texture-remap'):prop.pop('textureAsset',None)
        return result
    if material_semantics(original['materials'])!=material_semantics(manifest['materials']):
        raise ValueError('A native material parameter or appearance declaration changed.')
    target=destination/'CharacterModels.manifest.json'
    target.write_text(json.dumps(manifest,indent=2,ensure_ascii=False)+'\n',encoding='utf8')
    census=read(ROOT/'tools/asset-authoring/workspace/FaceRefinement/NativeSources/Census.json')
    expected_sources={a['sourceContentKey'] for a in humanoid_sources(census)}
    outside=[a for a in census['assets'] if a['sourceContentKey'] not in expected_sources]
    outside_fingerprints={skin['sourceFingerprint'] for a in outside for skin in a['skins']}
    before_by_source={row['sourceFingerprint']:row for row in original['meshes']}
    after_by_source={row['sourceFingerprint']:row for row in manifest['meshes']}
    for identity in outside_fingerprints:
        before=copy.deepcopy(before_by_source.get(identity));after=copy.deepcopy(after_by_source.get(identity))
        # A retained mesh can acquire a new bundle namespace; its checked payload stays identical.
        if before and after and before['mode']=='authored' and before['bundleFile'] in containers:
            before.pop('authoredMeshAsset',None);after.pop('authoredMeshAsset',None)
        if before!=after:raise ValueError('A preserved source morphology changed: '+identity)
    generated_sources=set();generated_meshes={};held=set()
    for plan in plans:
        if not plan.get('generation'):continue
        generation=read(plan['generation'])
        if generation['errors'] or generation['sharedConflicts']:raise ValueError('The authored source graph is unresolved.')
        generated_sources.update(a['sourceContentKey'] for a in generation['assets'])
        held.update(plan.get('heldHairBindingSources',[]))
        for row in generation['meshes']:
            identity=row['sourceFingerprint'];value=row['authoredFingerprint']
            if identity in generated_meshes and generated_meshes[identity]!=value:raise ValueError('The generated shared Mesh differs between plans.')
            generated_meshes[identity]=value
    complete=generated_sources==expected_sources and set(mappings)==set(generated_meshes)-held
    if complete and any(mappings[key]['authoredFingerprint']!=generated_meshes[key] for key in mappings):
        raise ValueError('A planned Mesh differs from the frozen generation.')
    proof=dict(status='PASS_COMPLETE_HUMANOID_CATALOG' if complete else 'PASS_PARTIAL_HUMANOID_CATALOG',
        completeHumanlikeScope=complete,sourceKeysCovered=len(generated_sources),expectedSourceKeys=len(expected_sources),
        preservedOtherSourceKeys=len({a['sourceContentKey'] for a in outside}),preservedOtherSourceMetadataExact=True,
        sourceCatalog=str(source),sourceManifestSha256=source_hash,
        candidate=str(destination),candidateManifestSha256=sha(target),modifiedMeshes=len(modified),reloadedMeshesChecked=len(mesh_fingerprints),
        preservedMeshPayloadsChecked=preserved_meshes,textureReferencesResolved=texture_references,files=files,
        nativeChoicePropertiesRetained=True,nativeRuntimePending=True)
    name_metadata={}
    for plan in plans:
        for authority in plan.get('publicNamingMetadata',[]):
            path=authority['path']
            if path in name_metadata and name_metadata[path]!=authority:
                raise ValueError('Producer naming inventories have conflicting immutable authority.')
            name_metadata[path]=authority
    if name_metadata:proof['publicNamingMetadata']=list(name_metadata.values())
    write(destination/'CatalogQuality.json',proof);print(json.dumps({key:value for key,value in proof.items() if key!='files'}))


def publication_json_proof(reference):
    if set(reference)!={'path','sha256'} or not re.fullmatch('[0-9a-fA-F]{64}',reference['sha256']):
        raise ValueError('A texture publication proof has no exact file authority.')
    path=Path(reference['path']);checked_catalog_path(path.parent,path.name)
    if sha(path)!=reference['sha256'].lower():raise ValueError('A texture publication proof changed: '+str(path))
    return read(path)


def typed_texture_selectors(manifest):
    result={}
    def add(key,prop,file):
        if key in result:raise ValueError('A typed texture selector is duplicated: '+repr(key))
        if not file or not prop.get('textureAsset'):raise ValueError('A typed texture payload is incomplete.')
        result[key]=(prop,file,prop['textureAsset'])
    for row in manifest['materials']:
        for prop in row['properties']:
            if prop.get('textureAsset'):
                add(('material',row['sourceFingerprint'],row['materialSlot'],row['sourceShader'],prop['name'],
                    prop.get('sourceTextureFingerprint'),prop.get('sourceBodyTextureFingerprint')),prop,prop.get('bundleFile') or row.get('bundleFile'))
    for prop in manifest['textureRemaps']:
        add(('global',prop['sourceShader'],prop['name'],prop.get('sourceTextureFingerprint'),
            prop.get('sourceBodyTextureFingerprint')),prop,prop.get('bundleFile'))
    return result


def checked_texture_payload(endpoint,bundle_hashes,typed_keys,span_cache=None):
    if set(endpoint)!={'bundleFile','asset','proof'}:raise ValueError('A texture endpoint has unknown authority fields.')
    file=endpoint['bundleFile'];asset=endpoint['asset'];value=publication_json_proof(endpoint['proof'])
    if value.get('schemaVersion')!=1 or value.get('status')!='PASS_ENCODED_TEXTURE_PAYLOAD_IDENTITY':
        raise ValueError('No completed encoded texture identity proof.')
    span=value.get('evidenceKind')
    if span not in(None,'RESOURCE_SPAN_PRIOR_GPU_EQUIVALENCE'):raise ValueError('Unknown texture GPU evidence kind.')
    native=value.get('sourceNative');native_record=None
    if span is not None and native is not None:raise ValueError('A retained BC7 span is not a native BC3 codec transition.')
    if native is not None:
        fields={'authority','encodedSha256','unpackedBc3','serializedFile','pathId'}
        if set(native)!=fields:raise ValueError('A native BC3 source omitted its serialized/unpacked provenance.')
        native_record=publication_json_proof(native['authority'])['records'].get(native['encodedSha256'])
        if native_record is None or native_record['serializedFile']!=native['serializedFile']or native_record['pathId']!=native['pathId']or native_record['asset'].casefold()!=asset.casefold():
            raise ValueError('A native BC3 source has another exact container/PPtr.')
    expected_bundle=bundle_hashes.get(file)if file in bundle_hashes else(native_record['sourceBundleSha256']if native_record else None)
    if value.get('bundleFile')!=file or value.get('asset')!=asset or value.get('bundleSha256')!=expected_bundle:
        raise ValueError('A texture proof belongs to another declared bundle/asset.')
    if sorted(value.get('typedKeys',[]),key=repr)!=sorted([list(key)for key in typed_keys],key=repr):
        raise ValueError('A texture proof has another complete typed consumer set.')
    required=('sourceBundleUnchanged','nativeGpuExpansionExact')if native is not None else('sourceBundleUnchanged','encodedSaveReloadExact','gpuSaveReloadExact')
    if span is not None:
        required=('sourceBundleUnchanged','encodedSaveReopenExact','serializedMetadataExact','unownedBytesExact')
        if value.get('checks',{}).get('freshGpuReadback')is not False or value.get('checks',{}).get('gpuSaveReloadExact',False)is not False:
            raise ValueError('Prior GPU equivalence must not claim a fresh GPU readback.')
    if any(value.get('checks',{}).get(key)is not True for key in required):
        raise ValueError('An encoded texture proof lacks source/save/reload/GPU closure.')
    content=value['content'];metadata=content['metadata']
    if set(content)!={'metadata','mips','byteLength','compressedSha256','gpuMipHashChainSha256'}:
        raise ValueError('A texture identity proof has unknown payload/hash semantics.')
    fields={'name','format','graphicsFormat','width','height','mipCount','sRGB','filterMode','wrapModeU','wrapModeV','wrapModeW','anisoLevel','mipMapBias'}
    if set(metadata)!=fields or metadata['format']not in('BC7','DXT5','DXT5Crunched')or type(metadata['sRGB'])is not bool:
        raise ValueError('A texture payload lacks its supported exact compression/metadata contract.')
    gpu_format='BC7'if metadata['format']=='BC7'else'DXT5'
    if metadata['graphicsFormat']!=('RGBA_'+gpu_format+('_SRGB'if metadata['sRGB']else'_UNorm')):
        raise ValueError('A texture color-space/format identity changed.')
    if(metadata['format']=='BC7')!=(native is None):raise ValueError('A native BC3 source and an owned BC7 result require different explicit proof types.')
    width=metadata['width'];height=metadata['height'];count=metadata['mipCount']
    if type(width)is not int or type(height)is not int or min(width,height)<=0 or count!=max(width,height).bit_length():
        raise ValueError('A texture payload is not a complete physical mip chain.')
    if not math.isfinite(metadata['mipMapBias']) or type(metadata['anisoLevel'])is not int or metadata['anisoLevel']<0:
        raise ValueError('A texture sampler is invalid.')
    mips=content['mips']
    if len(mips)!=count:raise ValueError('A texture payload omitted a physical mip.')
    def digest(value):return isinstance(value,str)and re.fullmatch('[0-9a-f]{64}',value)is not None
    for level,mip in enumerate(mips):
        w=max(1,width>>level);h=max(1,height>>level)
        if mip['level']!=level or mip['width']!=w or mip['height']!=h or mip['byteLength']!=((w+3)//4)*((h+3)//4)*16:
            raise ValueError('A texture mip has another dimension/span/order.')
        if not digest(mip['compressedSha256']) or not digest(mip['gpuSha256']):raise ValueError('A texture mip has no compressed/GPU identity.')
    gpu_chain=hashlib.sha256(json.dumps([[mip['level'],mip['width'],mip['height'],mip['gpuSha256']]for mip in mips],separators=(',',':')).encode('utf-8')).hexdigest()
    if not digest(content['compressedSha256'])or content['gpuMipHashChainSha256']!=gpu_chain or content['byteLength']!=sum(mip['byteLength']for mip in mips):
        raise ValueError('A texture has no full compressed/GPU payload identity.')
    if native is not None:
        if native_record is None: raise ValueError('The native texture layout authority is missing.')
        source_format={12:'DXT5',29:'DXT5Crunched'}.get(native_record['format'])
        if source_format!=metadata['format']or native_record['width']!=width or native_record['height']!=height or native_record['mipCount']!=count or(metadata['sRGB']!=(native_record['colorSpace']==1))or native_record['sourceBundleSha256']!=value['bundleSha256']or native_record['name']!=metadata['name']:
            raise ValueError('A native texture layout/format/color space differs from its captured authority.')
        raw_ref=native['unpackedBc3'];path=Path(raw_ref['path']);checked_catalog_path(path.parent,path.name);payload=path.read_bytes()
        if hashlib.sha256(payload).hexdigest()!=raw_ref['sha256']or raw_ref['sha256']!=content['compressedSha256']or len(payload)!=content['byteLength']:
            raise ValueError('Native unpacked BC3 bytes differ from their exact source authority.')
        if metadata['format']=='DXT5'and native['encodedSha256']!=content['compressedSha256']:
            raise ValueError('An uncrunched native DXT5 encoded authority differs.')
        offset=0
        for mip in mips:
            block=payload[offset:offset+mip['byteLength']];offset+=mip['byteLength']
            if hashlib.sha256(block).hexdigest()!=mip['compressedSha256']:raise ValueError('A native BC3 mip byte span changed.')
    if span is not None:checked_span_texture_identity(value,{}if span_cache is None else span_cache)
    return value


def typed_texture_payload_closure(original,candidate,receipt,source_sha,candidate_sha):
    """Strict typed texture regrouping; unrelated declarations/payloads stay exact."""
    if receipt.get('schemaVersion')!=1 or receipt.get('status')!='PASS_TYPED_TEXTURE_PAYLOAD_CLOSURE' or \
        receipt.get('sourceManifestSha256')!=source_sha or receipt.get('candidateManifestSha256')!=candidate_sha:
        raise ValueError('A typed texture receipt has another complete manifest epoch.')
    before=typed_texture_selectors(original);after=typed_texture_selectors(candidate)
    if set(before)-set(after):raise ValueError('A prior typed texture selector was removed.')
    old_payloads=catalog_payloads(original);new_payloads=catalog_payloads(candidate)
    rows=receipt.get('updates',[]);records={}
    for row in rows:
        expected={'key','policy','before','after','nativeOwner'}if row.get('policy')=='native-main'else{'key','policy','before','after'}
        if set(row)!=expected:raise ValueError('A typed texture transition has unknown fields.')
        key=tuple(row['key'])
        if key in records:raise ValueError('A typed texture transition is duplicated.')
        records[key]=row
    changed={key for key in before if before[key][1:]!=after[key][1:]or old_payloads[before[key][1]]!=new_payloads[after[key][1]]}|(set(after)-set(before))
    if not changed or set(records)!=changed:raise ValueError('The declared texture transition set differs from the exact changed consumers.')
    restored=copy.deepcopy(candidate);restored_index=typed_texture_selectors(restored)
    groups_before=collections.defaultdict(set);groups_after=collections.defaultdict(set)
    for key,(_,file,asset)in before.items():groups_before[file,asset].add(key)
    for key,(_,file,asset)in after.items():groups_after[file,asset].add(key)
    origins=collections.defaultdict(set);updates=[]
    span_cache={};span_owned=collections.defaultdict(set);span_proofs={}
    ordered=list(after)
    if receipt.get('resourceSpanPublication')is True:ordered.sort(key=lambda k:(after[k][1],repr(k)))
    for key in ordered:
        native_add=key not in before
        old_prop,old_file,old_asset=before[key]if not native_add else(None,None,None)
        new_prop,new_file,new_asset=after[key]
        if old_file is not None:origins[new_file].add(old_file)
        else:origins[new_file]
        if key not in records:continue
        row=records[key];a=row['before'];b=row['after']
        if native_add:
            if row['policy']!='native-main'or key[0]!='global'or key[2]!='_MainTex':raise ValueError('An added selector is not an explicit native Body Main transition.')
            public_texture_owner_label(row['nativeOwner'],[('remap',*key[1:])],receipt.get('nativeAuthorities',[]))
            if len(row['nativeOwner']['sources'])!=1:raise ValueError('A native Main transition has another source-owner cardinality.')
            source=row['nativeOwner']['sources'][0];input=source['input']
            if input['asset'].casefold()!=a['asset'].casefold():raise ValueError('A native transition names another original asset.')
            for alias in source['aliases']:
                if any(k[0]=='material'and k[1]==alias['sourceMeshFingerprint']and k[2]==alias['slot']and k[3]==alias['shader']and k[4]=='_MainTex'for k in before):
                    raise ValueError('A supposedly native global source already has an absolute owned material override.')
            old_file,old_asset=a['bundleFile'],a['asset']
            if set(new_prop)-{'sourceShader','name','kind','sourceTextureFingerprint','sourceBodyTextureFingerprint','textureAsset','bundleFile','sourceWidth','sourceHeight'}or new_prop.get('kind')!='texture-remap':
                raise ValueError('A native Main transition added unrelated remap fields.')
        if (a['bundleFile'],a['asset'])!=(old_file,old_asset)or(b['bundleFile'],b['asset'])!=(new_file,new_asset):
            raise ValueError('A texture transition endpoints do not match its actual typed selector.')
        if not native_add and old_prop is None: raise ValueError('The retained material property is missing.')
        if not native_add and old_prop is not None and{name:value for name,value in old_prop.items()if name not in('bundleFile','textureAsset')}!={name:value for name,value in new_prop.items()if name not in('bundleFile','textureAsset')}:
            raise ValueError('A texture transition changed an unowned property field or ST.')
        old=checked_texture_payload(a,old_payloads,{key}if native_add else groups_before[old_file,old_asset])
        new=checked_texture_payload(b,new_payloads,groups_after[new_file,new_asset],span_cache)
        span_policy=new.get('evidenceKind')=='RESOURCE_SPAN_PRIOR_GPU_EQUIVALENCE'
        if span_policy and(receipt.get('resourceSpanPublication')is not True or row['policy']not in('changed-main','retained')):
            raise ValueError('A CPU span equivalence proof requires its explicit evidence kind and unchanged ownership policy.')
        prop=key[4]if key[0]=='material'else key[2]
        if span_policy:
            if native_add or(old_file,old_asset)!=(new_file,new_asset)or old['content']['metadata']!=new['content']['metadata']:
                raise ValueError('A same-layout span transition changed its typed selector/runtime metadata.')
            ref=new['resourceSpanEvidence']['container'];container=publication_json_proof(ref);proof_key=(ref['path'],ref['sha256'])
            if container['source']['sha256']!=old_payloads[old_file]or container['output']['sha256']!=new_payloads[new_file]:
                raise ValueError('A span transition has another exact source/candidate bundle.')
            span_proofs[proof_key]=container
            identity=new['resourceSpanEvidence']['texture'];identity=(identity['serializedFile'],str(identity['pathId']))
            if row['policy']=='retained':
                if prop not in('_MainTex','_BumpMap')or old['content']!=new['content']:raise ValueError('An unowned retained texture changed property/bytes/metadata/prior GPU identity.')
            else:
                if prop!='_MainTex'or new.get('codecApplications')!=1 or type(new.get('codecApplications'))is not int or new.get('ownedTypedKeys')!=new.get('typedKeys'):
                    raise ValueError('An owned Main span lacks its exact once-codec consumer authority.')
                matches=[r for r in container['textures']if(r['serializedFile'],str(r['pathId']))==identity]
                if len(matches)!=1 or matches[0]['sourceSha256']!=old['content']['compressedSha256']or matches[0]['patchedSha256']!=new['content']['compressedSha256']:
                    raise ValueError('An owned Main span differs from its exact typed before/after bytes.')
                span_owned[proof_key].add(identity)
        elif row['policy']=='retained':
            if prop not in('_MainTex','_BumpMap')or old['content']['metadata']['format']!='BC7'or new['content']['metadata']['format']!='BC7'or old['content']!=new['content']:
                raise ValueError('A retained Normal/Main changed compressed mips, GPU data or metadata.')
        elif row['policy']in('changed-main','native-main'):
            if prop!='_MainTex'or type(new.get('codecApplications'))is not int or new['codecApplications']!=1 or new.get('ownedTypedKeys')!=new.get('typedKeys'):
                raise ValueError('A changed Main lacks exactly one codec and its complete owned consumer set.')
            old_meta=dict(old['content']['metadata']);new_meta=dict(new['content']['metadata'])
            old_meta.pop('name');new_meta.pop('name')
            if new_meta['format']!='BC7':raise ValueError('A once-codec Main output must be BC7.')
            old_meta.pop('format');old_meta.pop('graphicsFormat');new_meta.pop('format');new_meta.pop('graphicsFormat')
            if old_meta!=new_meta:raise ValueError('A changed Main altered format, dimensions, color space or sampler metadata.')
        else:raise ValueError('An undeclared texture transition policy was requested.')
        if native_add:
            if old.get('sourceNative')is None:raise ValueError('An added native Main has no exact original BC3/PPtr proof.')
            input=row['nativeOwner']['sources'][0]['input'];native=old['sourceNative']
            if input['authority']!=native['authority']or input['encodedSha256']!=native['encodedSha256']or input['serializedFile']!=native['serializedFile']or input['pathId']!=native['pathId']or input['sourceBundleSha256']!=old['bundleSha256']:
                raise ValueError('Native consumer ownership and exact compressed source proofs disagree.')
            if new_prop.get('sourceWidth')!=old['content']['metadata']['width']or new_prop.get('sourceHeight')!=old['content']['metadata']['height']:
                raise ValueError('A native Main route changed its declared source layout.')
        else:
            target=restored_index[key][0];target.clear();target.update(copy.deepcopy(old_prop))
        updates.append(copy.deepcopy(row))
    for proof_key,container in span_proofs.items():
        actual={(r['serializedFile'],str(r['pathId']))for r in container['textures']}
        if actual!=span_owned[proof_key]:raise ValueError('A resource span modified bytes without exact typed Main ownership.')
    added=set(after)-set(before)
    if added:
        restored['textureRemaps']=[prop for prop in restored['textureRemaps']if('global',prop['sourceShader'],prop['name'],prop.get('sourceTextureFingerprint'),prop.get('sourceBodyTextureFingerprint'))not in added]
    restored['bundles']=copy.deepcopy(original['bundles'])
    if restored!=original:raise ValueError('Texture regrouping changed an unowned manifest declaration.')
    old_keys=public_payload_keys(original);new_keys=public_payload_keys(candidate)
    controlled_old={before[key][1]for key in changed if key in before}
    controlled_new={after[key][1]for key in changed}
    for file in set(old_payloads)|set(new_payloads):
        unchanged=file in old_payloads and file in new_payloads and old_payloads[file]==new_payloads[file]
        if unchanged:origins[file].add(file);continue
        if file in old_payloads:
            refs=json.loads(old_keys.get(file,'[]'))
            all_texture=bool(refs)and all(key[0]in('property','remap')for key in refs)
            if file not in controlled_old or not all_texture or any(key not in changed for key,value in before.items()if value[1]==file):
                raise ValueError('A changed/retired bundle contains an unowned or non-texture payload.')
        if file in new_payloads:
            refs=json.loads(new_keys.get(file,'[]'))
            if file not in controlled_new or not refs or any(key[0]not in('property','remap')for key in refs):
                raise ValueError('A new bundle is unrelated to the declared texture transition.')
    for file in new_payloads:
        if file not in origins:raise ValueError('A candidate payload has no exact original consumer provenance.')
    if set().union(*origins.values())!=set(old_payloads):raise ValueError('Texture regrouping lost its complete rollback payload closure.')
    return dict(candidateToOriginalFiles={file:sorted(origins[file])for file in new_payloads},textureUpdates=updates,
        retainedPayloads={file:digest for file,digest in new_payloads.items()if old_payloads.get(file)==digest},
        retiredOriginalFiles=sorted(set(old_payloads)-set(new_payloads)),inverseTypedTextureClosureExact=True)


def public_texture_owner_label(owner,consumers,authorities):
    """Name a global Body atlas from native texture/consumer inventories, never a Head."""
    if set(owner)!={'kind','sources'}or owner['kind']!='BodyAtlas'or not owner['sources']:
        raise ValueError('A global texture payload has no explicit native Body atlas owner.')
    if any(key[0]!='remap'for key in consumers):raise ValueError('A Body atlas owner was attached to a different typed consumer kind.')
    authority_keys={(str(Path(row['path']).absolute()),row['sha256'].lower())for row in authorities}
    def load(reference):
        if(str(Path(reference['path']).absolute()),reference['sha256'].lower())not in authority_keys:
            raise ValueError('A Body atlas owner omitted its source authority from publication provenance.')
        return publication_json_proof(reference)
    expected={tuple(key[1:])for key in consumers};seen=set();names=set();parts=collections.defaultdict(set)
    for source in owner['sources']:
        fields={'sourceShader','property','sourceTextureFingerprint','sourceBodyTextureFingerprint','nativeTextureName','nativeAuthorities','aliases','input'}
        if set(source)!=fields:raise ValueError('Unknown native Body atlas owner fields.')
        key=(source['sourceShader'],source['property'],source['sourceTextureFingerprint'],source['sourceBodyTextureFingerprint'])
        if key in seen or key not in expected:raise ValueError('A Body atlas source belongs to another/duplicate global consumer.')
        seen.add(key);aliases={};native_names=set()
        for reference in source['nativeAuthorities']:
            document=load(reference)
            if not isinstance(document.get('actors'),list)or not isinstance(document.get('textures'),list):
                raise ValueError('A Body naming source is not the native material/texture inventory.')
            for texture in document['textures']:
                if texture['sourceFingerprint']==source['sourceTextureFingerprint']:native_names.add(texture['name'])
            for actor in document['actors']:
                for material in actor['materials']:
                    if material['shader']!=source['sourceShader']:continue
                    if not any(texture['property']==source['property']and texture['sourceFingerprint']==source['sourceTextureFingerprint']for texture in material['textures']):continue
                    alias=dict(renderer=material['renderer'],slot=material['slot'],sourceMeshFingerprint=material['sourceMeshFingerprint'],shader=material['shader'])
                    aliases[json.dumps(alias,sort_keys=True,separators=(',',':'))]=alias
        if native_names!={source['nativeTextureName']}or not aliases:
            raise ValueError('A Body atlas name/fingerprint has no unique actual native inventory authority.')
        declared={json.dumps(alias,sort_keys=True,separators=(',',':'))for alias in source['aliases']}
        if len(declared)!=len(source['aliases'])or declared!=set(aliases):
            raise ValueError('A Body atlas owner does not retain every actual native part/LOD alias.')
        input=source['input']
        if set(input)!={'authority','encodedSha256','serializedFile','pathId','asset','sourceBundleSha256'}:
            raise ValueError('A Body atlas input has no exact container/PPtr provenance.')
        records=load(input['authority'])['records'];actual=records.get(input['encodedSha256'])
        if actual is None or 'Body'not in actual['roles']or any(input[k]!=actual[k]for k in('serializedFile','pathId','asset','sourceBundleSha256')):
            raise ValueError('A Body atlas effective input differs from its closed container/PPtr authority.')
        names.add(asset_names.source_label(source['nativeTextureName']))
        for alias in aliases.values():
            match=re.fullmatch(r'(?P<part>Body|Forearms|Hands|Feet)_LOD(?P<lod>[0-2])',alias['renderer'].rsplit('/',1)[-1])
            if match is None:raise ValueError('A native Body atlas has an undeclared anatomical part/LOD alias.')
            parts[match['part'].lower()].add(int(match['lod']))
    if seen!=expected:raise ValueError('A Body atlas naming owner omits a global texture consumer.')
    return '_and_'.join(sorted(names))+'_'+'_'.join(part+'_lod'+''.join(map(str,sorted(lods)))for part,lods in sorted(parts.items()))


def publish_catalog():
    parser=argparse.ArgumentParser();parser.add_argument('--catalog',type=Path,required=True)
    parser.add_argument('--expected-current-sha256',required=True)
    parser.add_argument('--name-sources',type=Path)
    parser.add_argument('--name-source-metadata',type=Path,action='append');args=parser.parse_args()
    if args.name_sources and args.name_source_metadata:raise ValueError('Select pinned naming sources or their native metadata inputs.')
    candidate=checked_catalog_path(args.catalog);candidate_manifest=checked_catalog_path(candidate,'CharacterModels.manifest.json')
    current=ROOT/'SolastaUnfinishedBusiness/BlenderModels/CharacterModels';manifest_path=current/'CharacterModels.manifest.json'
    checked_catalog_path(current)
    if sha(manifest_path)!=args.expected_current_sha256:raise ValueError('The public catalog changed before publication.')
    quality=read(candidate/'CatalogQuality.json')
    if quality['status']!='PASS_COMPLETE_HUMANOID_CATALOG' or not quality.get('completeHumanlikeScope') or sha(candidate_manifest)!=quality['candidateManifestSha256']:
        raise ValueError('The candidate catalog has no matching complete verification receipt.')
    manifest=read(candidate_manifest);source=checked_catalog_path(Path(quality['sourceCatalog']))
    if source==current or sha(source/'CharacterModels.manifest.json')!=quality['sourceManifestSha256']:
        raise ValueError('The immutable original catalog is missing or changed.')
    if sha(manifest_path)!=quality['sourceManifestSha256']:
        raise ValueError('The current public catalog differs from the retained rollback source.')
    if manifest.get('schemaVersion')==3:
        publish_resource_catalog(candidate,current,source,manifest,quality)
        return
    wanted=catalog_payloads(manifest)
    original=read(source/'CharacterModels.manifest.json')
    original_hashes=catalog_payloads(original)
    texture_closure=None
    if quality.get('texturePayloadClosure'):
        texture_closure=typed_texture_payload_closure(original,manifest,
            publication_json_proof(quality['texturePayloadClosure']),quality['sourceManifestSha256'],quality['candidateManifestSha256'])
    origins={file:file for file in wanted}
    if texture_closure is not None:
        origins=texture_closure['candidateToOriginalFiles']
        for file,digest in original_hashes.items():
            if sha(checked_catalog_path(source,file))!=digest:raise ValueError('An immutable rollback source payload changed: '+file)
    elif set(wanted)!=set(original_hashes):
        # A future private producer may still use its job/cache output names.
        # Match its complete typed selector groups to the immutable snapshot,
        # not a prefix, content digest, guessed model, or ordinal fallback.
        old_keys=public_payload_keys(original);new_keys=public_payload_keys(manifest);old_by_key={}
        for file,key in old_keys.items():
            if key in old_by_key:raise ValueError('The original logical file closure is ambiguous.')
            old_by_key[key]=file
        origins={file:old_by_key.get(new_keys.get(file)) for file in wanted}
        if None in origins.values() or len(set(origins.values()))!=len(origins) or set(origins.values())!=set(original_hashes):
            raise ValueError('This publication requires the original catalog file closure through an exact logical inverse map.')
    # Preserve the original closure before public renaming, then prove the
    # complete inverse map rather than waiving it for migration/regeneration.
    if args.name_sources:
        provenance=read_name_sources(args.name_sources,candidate_manifest)
    elif args.name_source_metadata:
        provenance=make_name_sources(manifest,sha(candidate_manifest),args.name_source_metadata,original)
    else:
        snapshot=read(source/'SourceCatalog.json')
        provenance=snapshot.get('publicNameSources')
        authorities=list(quality.get('publicNamingMetadata',[]))
        if provenance is not None:authorities+=provenance['authorities']
        if authorities:
            native_name_index(authorities)
            provenance=make_name_sources(manifest,sha(candidate_manifest),dict.fromkeys(row['path'] for row in authorities),original)
        elif provenance is None:
            provenance=dict(schemaVersion=1,manifestSha256=sha(candidate_manifest),authorities=[],payloads=[])
    names,published=public_name_plan(manifest,provenance,original)
    inverse={new:old for old,new in names.items()}
    published_payloads=catalog_payloads(published)
    if any(value is None for value in origins.values()): raise ValueError('A published payload source owner is missing.')
    original_files=set().union(*(set(value)for value in origins.values()if value is not None))if texture_closure is not None else set(origins.values())
    if {inverse[file]:value for file,value in published_payloads.items()}!=wanted or original_files!=set(original_hashes):
        raise ValueError('A public rename did not preserve its original inverse closure and SHA.')
    manifest_bytes=(json.dumps(published,indent=2,ensure_ascii=False)+'\n').encode('utf-8')
    published_sha=hashlib.sha256(manifest_bytes).hexdigest()
    changed=[]
    for file,expected in published_payloads.items():
        original_file=inverse[file]
        input_path=checked_catalog_path(candidate,original_file);output_path=checked_catalog_path(current,file)
        if sha(input_path)!=expected:raise ValueError('A candidate input changed: '+original_file)
        if output_path.exists() and file not in original_hashes:
            raise ValueError('A semantic name would overwrite an unowned public file: '+file)
        if not output_path.is_file() or sha(output_path)!=expected:changed.append(file)
    receipt=dict(status='RUNNING_ATOMIC_PUBLICATION',sourceCatalog=str(source),candidate=str(candidate),
        changed=changed,renames=({old:new for old,new in names.items()if old!=new}if texture_closure is not None else
            {origins[old]:new for old,new in names.items() if origins[old]!=new}),
        originalCandidateManifestSha256=quality['candidateManifestSha256'],
        candidateToOriginalFiles=origins,
        publishedManifestSha256=published_sha,inverseFileClosureExact=texture_closure is None,payloadShaUnchangedByNaming=True)
    if texture_closure is not None:
        receipt.update(inverseTypedTextureClosureExact=True,texturePayloadClosure=quality['texturePayloadClosure'],
            textureUpdates=[dict(row,afterPublicBundle=names[row['after']['bundleFile']])for row in texture_closure['textureUpdates']])
    atomic_publish_catalog(candidate,current,source,manifest_bytes,published_payloads,inverse,original_hashes,receipt)
    print(json.dumps(receipt))


def atomic_publish_catalog(candidate,current,source,manifest_bytes,payloads,candidate_files,original_hashes,receipt):
    """One rollback-preserving publication path for generated catalog formats."""
    manifest_path=current/'CharacterModels.manifest.json'
    published_sha=hashlib.sha256(manifest_bytes).hexdigest()
    changed=receipt['changed'];written=[];removed=[]
    obsolete=sorted(set(original_hashes)-set(payloads))
    if sha(manifest_path)!=sha(source/'CharacterModels.manifest.json'):
        raise ValueError('The public catalog changed before the atomic publication.')
    if any(sha(checked_catalog_path(source,file))!=digest for file,digest in original_hashes.items()):
        raise ValueError('An immutable rollback source changed before publication.')
    try:
        for file in changed+['CharacterModels.manifest.json']:
            temporary=current/(file+'.new-'+uuid.uuid4().hex)
            try:
                checked_catalog_path(current,temporary.name)
                if file=='CharacterModels.manifest.json':temporary.write_bytes(manifest_bytes)
                else:os.link(checked_catalog_path(candidate,candidate_files[file]),temporary)
                os.replace(temporary,current/file)
                written.append(file)
            finally:
                if temporary.exists():temporary.unlink()
        if sha(manifest_path)!=published_sha:raise ValueError('The published manifest differs.')
        if any(sha(checked_catalog_path(source,file))!=digest for file,digest in original_hashes.items()):
            raise ValueError('Publication changed an immutable rollback payload.')
        # Delete only exact old names after the new manifest is committed;
        # private source snapshots and historical proof paths remain untouched.
        for file in obsolete:
            path=checked_catalog_path(current,file)
            if sha(path)!=original_hashes[file]:raise ValueError('An obsolete public name changed before retirement: '+file)
            path.unlink();removed.append(file)
        receipt.update(status='PASS_ATOMIC_PUBLICATION',written=len(written),retired=removed,sourceBytesPreserved=True)
    except BaseException as error:
        for file in removed:os.link(checked_catalog_path(source,file),checked_catalog_path(current,file))
        for file in reversed(written):
            if file not in original_hashes:continue
            temporary=current/(file+'.rollback-'+uuid.uuid4().hex)
            os.link(checked_catalog_path(source,file),temporary);os.replace(temporary,current/file)
        temporary=current/('CharacterModels.manifest.json.rollback-'+uuid.uuid4().hex)
        os.link(checked_catalog_path(source,'CharacterModels.manifest.json'),temporary);os.replace(temporary,manifest_path)
        for file in written:
            if file not in original_hashes and file!='CharacterModels.manifest.json':checked_catalog_path(current,file).unlink()
        receipt.update(status='FAIL_ROLLED_BACK',error=str(error))
        raise
    finally:write(require_workspace() /'Publication.json',receipt)


def validate_prepared_resource(row,assembly,verified):
    """Check an owned prepared mutation without claiming its authored fields are unchanged."""
    def pinned(reference):
        path=Path(reference['path']);checked_catalog_path(path.parent,path.name)
        expected=reference['sha256'].lower();key=(str(path.resolve()),expected)
        if key not in verified:
            if sha(path)!=expected:raise ValueError('A prepared resource input changed: '+str(path))
            verified[key]=True
        return path
    def document(reference):
        path=pinned(reference);key=('document',str(path.resolve()),reference['sha256'].lower())
        if key not in verified:verified[key]=read(path)
        return verified[key]
    mutations=row.get('declaredPreparedMutations',[])
    if 'optimization'in assembly:
        optimizer=assembly['optimization'];producer=optimizer.get('producerInputs',[])
        for reference in producer:pinned(reference)
        if len(producer)!=2 or hashlib.sha256(''.join(item['sha256'].lower()for item in producer).encode()).hexdigest()!=optimizer['codeSha256']:
            raise ValueError('An optimized resource has no matching immutable producer.')
        finishing=assembly.get('faceFinishing',{})
        baseline=document(finishing['sourceAssembly'])if finishing else assembly
        key=('optimized-files',id(baseline))
        if key not in verified:verified[key]={item['file']:item for item in baseline['proofs']}
        original=verified[key][row['file']]
        if any(row.get(key)!=original.get(key)for key in ('oldCab','newCab','objects','removedObjects','dependencies','sourceSha256','codeSha256'))or \
            original['codeSha256']!=optimizer['codeSha256']or original['removedObjects']!=0 or \
            any(original.get(flag)is not True for flag in ('unrelatedObjectBytesExact','sourceResourcesUntouched')):
            raise ValueError('An optimized resource changed its retained ownership.')
        expected=original['sha256'];prepared=[]
        if finishing:
            if finishing.get('creatureAnatomyPreserved')is not True:raise ValueError('Native creature anatomy is not retained.')
            if sum(change['additionalBytes']for operation in finishing.get('colorCorrections',[])for change in operation.get('ownedTextureStorageChanges',[]))>EXACT_MAIN_CATALOG_MAX_BYTES:
                raise ValueError('Exact Main storage exceeds its catalog-wide budget.')
            for reference in finishing['inputs']:pinned(reference)
            pinned(dict(path=str(Path(finishing['source'])/'Catalog/CharacterModels.manifest.json'),sha256=finishing['sourceCatalogSha256']))
            geometry=document(finishing['geometryPreparation'])
            if geometry.get('status')!='PREPARED_GEOMETRY':raise ValueError('The face geometry checkpoint is incomplete.')
            key=('geometry-files',id(geometry))
            if key not in verified:verified[key]={item['file']:item for item in geometry['assembly']['proofs']}
            geometry_row=verified[key][row['file']];prepared=geometry_row.get('appearanceFinishing',[])
            expected=geometry_row['sha256']
            for operation in prepared:
                flags=('unrelatedObjectBytesExact','savedObjectBytesExact','cabAndExternalReferencesUnchanged')
                if operation.get('kind')!='source-owned-face'or not operation.get('objects')or any(operation.get(flag)is not True for flag in flags):
                    raise ValueError('A face mutation lost its object or reference ownership.')
                facts=operation['facts'];facts=facts if isinstance(facts,list)else[facts]
                for fact in facts:
                    if any(fact.get(flag)is not True for flag in ('nativeContactChannelsExact','unownedBodyChannelsExact','frameIdentityAndSkinPalettePreserved','allStoredPoseOrientationsPreserved')):
                        raise ValueError('A face mutation changed contact, body or expression channels.')
        operations=row.get('appearanceFinishing',[])
        if operations[:len(prepared)]!=prepared:raise ValueError('A face checkpoint differs from the final resource.')
        for operation in operations[len(prepared):]:
            if operation.get('kind')=='shared-skin-boundary':
                if operation.get('beforeSha256')!=expected or not operation.get('objects')or not operation.get('facts')or \
                    any(operation.get(flag)is not True for flag in ('unrelatedObjectBytesExact','savedObjectBytesExact','cabAndExternalReferencesUnchanged')):
                    raise ValueError('A shared skin-cut mutation lost its object ownership.')
                for reference in operation['authority']['inputs']:pinned(reference)
                for fact in operation['facts']:
                    if fact.get('kind')=='physical-skin-frame':
                        if any(fact.get(flag)is not True for flag in ('restChannelsExact','positionFramesExact','uvSkinTopologyAndChannelsExact','physicalAliasPoseNormalsCoherent'))or \
                            fact['changedFrameVertices']<=0 or not fact['frames']:
                            raise ValueError('A physical skin frame changed protected geometry or rest channels.')
                        continue
                    shading_exact=fact.get('skinAndShadingChannelsExact')is True or \
                        (fact.get('skinAndRestShadingChannelsExact')is True and fact.get('posedShadingFollowsDonor')is True)
                    if not shading_exact or any(fact.get(flag)is not True for flag in ('unownedVertexStreamsExact','uvSkinTopologyAndChannelsExact','physicalChannelIdentityExact'))or \
                        not 0<fact['donorInteriorOverlapMetres']<=1e-4 or fact['maximumSourceGap']>1e-4 or \
                        len(fact['vertexPairs'])!=fact['sharedVertices']:
                        raise ValueError('A shared skin-cut mutation changed protected skin or exceeded its physical tolerance.')
                expected=operation['sha256']
                continue
            storage=operation.get('ownedTextureStorageChanges',[])
            fields_exact=operation.get('objectFieldsExact')is True or (storage and operation.get('objectFieldChangesLimitedToTextureStorage')is True)
            if operation.get('kind')!='source-calibrated-main'or operation.get('beforeSha256')!=expected or not fields_exact or \
                any(operation.get(flag)is not True for flag in ('unownedResourceSpansExact','savedResourceBytesExact')):
                raise ValueError('A Main colour mutation lost its resource span ownership.')
            storage_by_id={change['object']:change for change in storage}
            if len(storage_by_id)!=len(storage):raise ValueError('An exact Main storage owner is repeated.')
            for reference in operation['authority']['inputs']:pinned(reference)
            if not operation['textures']:raise ValueError('A Main colour mutation owns no texture.')
            for texture in operation['textures']:
                for key in ('preparation','coefficient','nativeRgba','encodedSource'):pinned(texture[key])
                if texture.get('preCalibrationAlphaAllMipsExact')is not True:raise ValueError('A Main colour mutation changed source alpha.')
                mips=texture['mips'];texels=sum(mip['width']*mip['height']for mip in mips)
                change=storage_by_id.get(texture['object'])
                if change:
                    before,after=change['before'],change['after'];size=texels*4
                    keys={'m_TextureFormat','m_CompleteImageSize','m_StreamData'}
                    if set(before)!=keys or set(after)!=keys or before['m_TextureFormat']!=25 or after['m_TextureFormat']!=4 or \
                        change.get('exactRgbaMipChain')is not True or not 0<size<=EXACT_MAIN_TEXTURE_MAX_BYTES or change['additionalBytes']!=size or \
                        after['m_CompleteImageSize']!=size or after['m_StreamData']['size']!=size or after['m_StreamData']['offset']%16 or \
                        before['m_StreamData']['path']!=after['m_StreamData']['path']:
                        raise ValueError('Exact Main storage changed unowned metadata or its mip layout.')
                if not texels:raise ValueError('A Main colour mutation has no mip texels.')
                rms=math.sqrt(sum(mip['codec']['rootMeanSquareRgbError']**2*mip['width']*mip['height']for mip in mips)/texels)
                if rms>2 or not math.isclose(rms,texture['allMipTexelWeightedRgbRms'],abs_tol=1e-12):raise ValueError('A Main colour mutation exceeds its RGB error bound.')
                for index,mip in enumerate(mips):
                    codec=mip['codec'];exact=change and codec.get('storage')=='RGBA32'and codec.get('sourceOwnedBlocksExact')is True and codec['maximumRgbError']==0 and codec['rootMeanSquareRgbError']==0
                    if (index<3 and codec['maximumRgbError']>16)or codec.get('alphaExact')is not True or \
                        (not exact and codec.get('partitionsAndIndicesRetained')is not True)or \
                        mip['tone'].get('policy')!='surface-local-v4'or any(mip['tone'].get(flag)is not True for flag in ('preToneAlphaExact','c1WeightOneRgbaExact','unmappedRawRgbaExact','finiteRange','sourceC1BaselineUsed')):
                        raise ValueError('A Main colour mutation changed protected colour channels.')
            expected=operation['sha256']
        if not mutations:
            if expected!=row['sha256']:raise ValueError('The prepared resource has unverified final bytes.')
            return
        mutation=mutations[-1]
        if mutation.get('kind')!='compiled-shader-only'or mutation.get('source',{}).get('sha256')!=expected:
            raise ValueError('An optimized resource has an unverified following mutation.')
    if not mutations:raise ValueError('A changed resource has no prepared mutation proof.')
    mutation=mutations[-1];kind=mutation.get('kind');proof=mutation
    if 'proof'in mutation:
        reference=mutation['proof']
        proof=read(pinned(reference))if set(reference)=={'path','sha256'}else reference
        if 'proof'in proof:
            for key in ('sourceCatalog','sourceAssembly'):pinned(proof[key])
            for item in proof['rows']:
                for key in ('prepared','engine','compiledBundle','attachment'):pinned(item[key])
            proof=proof['proof']
    target=proof.get('target',proof.get('output'))
    if not target or target['sha256']!=row['sha256']:
        raise ValueError('A prepared mutation does not own the published bytes.')
    pinned(proof['source']);pinned(target)
    if kind in ('all-lod-precompiled-groom-mesh-and-cloth','owned-fiber-anisotropy','owned-cloth-state'):
        flags=('allOtherObjectBytesExact','resourcePayloadsExact','cabAndExternalReferencesUnchanged','savedObjectBytesExact')
    elif kind=='compiled-shader-only':
        flags=('allOtherObjectBytesExact','savedObjectBytesExact')
        if read(pinned(proof['validation'])).get('status')!='PASS_SHADER_MATERIAL_AND_BRDF_CONTRACTS':
            raise ValueError('A prepared shader has no matching validation.')
    elif kind=='Main-encoded-spans':
        flags=('allCabBytesExact','memberNamesOrderFlagsExact','allUnownedResourceBytesExact','allTextureMetadataAndAliasesExact','sourceUnchanged','reopenedEncodedBytesExact')
        if proof.get('status')!='PASS_CPU_BC7_RESOURCE_SPAN_REPLACEMENT':raise ValueError('Invalid prepared texture spans.')
        codecs=assembly.get('surfaceFinishing',{}).get('codecs',[])
        if not codecs:raise ValueError('Prepared texture spans require saved GPU evidence.')
        encoded_hashes=set()
        for codec in codecs:
            for key in ('input','report','verification'):pinned(codec[key])
            report=read(Path(codec['report']['path']));validation=read(Path(codec['verification']['path']))
            if not report.get('completed')or not report.get('savedReloadGpuExact')or validation.get('status')!='PRODUCTION_FIELD_ONLY_BC7_PAYLOAD_AND_ALL_GPU_MIPS_EXACT':
                raise ValueError('Prepared texture GPU evidence is incomplete.')
            encoded_hashes.update(item['encodedSha256']for item in report.get('saved',[])if item.get('variant')=='FieldBC7')
        if not proof.get('textures')or any(item['patchedSha256']not in encoded_hashes for item in proof['textures']):
            raise ValueError('Prepared texture bytes differ from their saved GPU evidence.')
    else:raise ValueError('Unknown prepared resource mutation: '+str(kind))
    if any(proof.get(flag)is not True for flag in flags):raise ValueError('Prepared resource ownership proof is incomplete.')


def publish_resource_catalog(candidate,current,source,manifest,quality):
    """Publish the complete prepared-resource closure, retaining authoring inputs."""
    proof=publication_json_proof(quality['resourceAssembly'])
    if proof.get('status')!='PASS_STANDALONE_CHARACTER_RESOURCES' or proof.get('originalModelDependenciesInherited')is not False:
        raise ValueError('Standalone resource publication needs a closed assembly proof.')
    payloads=catalog_payloads(manifest);rows=proof['proofs']
    if len(rows)!=len(payloads) or {row['file']:row['sha256']for row in rows}!=payloads or \
        proof['resources']!=len(manifest['resources']) or proof['bundles']!=len(rows) or proof['bytes']!=sum(row['bytes']for row in rows):
        raise ValueError('The standalone assembly proof differs from the candidate closure.')
    if manifest['meshes']or manifest['materials']or manifest['textureRemaps']:
        raise ValueError('A standalone resource catalog must not retain runtime replacement declarations.')
    if any(len(file)>128 or re.search('[a-f0-9]{16}',file)for file in payloads):
        raise ValueError('A standalone resource filename lacks its semantic owner.')
    sources=set();used=set();types={'GameObject','Material','RuntimeAnimatorController','AnimationClip','Avatar','AudioClip','Texture'}
    for resource in manifest['resources']:
        if not resource['source']or resource['source']in sources or resource['type']not in types or not resource['bundles']:
            raise ValueError('A standalone resource address/type is missing or ambiguous.')
        sources.add(resource['source'])
        files=[row['file']for row in resource['bundles']]
        if len(set(files))!=len(files) or any(file not in payloads for file in files):
            raise ValueError('A standalone resource has an undeclared dependency.')
        used.update(files)
    if used!=set(payloads):raise ValueError('A standalone payload has no resource owner.')
    verified={}
    for row in rows:
        retained=Path(row['source'])
        if retained.is_relative_to(current):retained=source/retained.relative_to(current)
        if sha(retained)!=row['sourceSha256'] or sha(checked_catalog_path(candidate,row['file']))!=row['sha256']:
            raise ValueError('A prepared resource or its packed fields changed.')
        if row['allRetainedObjectFieldsExactAfterReferenceRebasing']is not True:
            validate_prepared_resource(row,proof,verified)
    for input in proof['inputs']:
        if sha(Path(input['path']))!=input['sha256']:raise ValueError('A standalone preparation authority changed.')
    original_hashes=catalog_payloads(read(source/'CharacterModels.manifest.json'))
    changed=[]
    for file,digest in payloads.items():
        target=checked_catalog_path(current,file)
        if target.exists()and file not in original_hashes:raise ValueError('An unowned public payload would be overwritten: '+file)
        if not target.exists()or sha(target)!=digest:changed.append(file)
    manifest_bytes=(json.dumps(manifest,indent=2,ensure_ascii=False)+'\n').encode('utf-8')
    receipt=dict(status='RUNNING_ATOMIC_PUBLICATION',sourceCatalog=str(source),candidate=str(candidate),changed=changed,
                 originalCandidateManifestSha256=quality['candidateManifestSha256'],publishedManifestSha256=hashlib.sha256(manifest_bytes).hexdigest(),
                 resourceAssembly=quality['resourceAssembly'],standaloneResources=len(sources),runtimeReplacementDeclarations=0,
                 payloadShaUnchangedByNaming=True)
    atomic_publish_catalog(candidate,current,source,manifest_bytes,payloads,{file:file for file in payloads},original_hashes,receipt)
    print(json.dumps(receipt))



def material_packet(reference):
    """Load one pinned canonical packet using the existing source contract."""
    from collect_sources import validate_packet
    path=Path(reference['path']);payload=path.read_bytes()
    if hashlib.sha256(payload).hexdigest()!=reference['sha256'].lower():
        raise ValueError('The material source packet changed.')
    value=json.loads(gzip.decompress(payload) if path.suffix.lower()=='.gz' else payload)
    validate_packet(value)
    return value


def material_main_inputs(row,family_rows):
    """Existing packet authority remains outside the numeric field module."""
    packets={}
    refs=[row['headCanonical'],row['bodyCanonical']]+[r['headSource']['initialCanonical']for r in family_rows]
    for ref in refs:
        if ref['path'] not in packets:packets[ref['path']]=material_packet(ref)
    return packets


def material_codebook(receipt,alpha_receipt):
    """Read exactly the already measured native GPU palette/alpha tables."""
    import numpy as np
    if receipt['status']!='PASS_ACTUAL_GPU_PALETTES_AND_ALL_MIP_512_POINT_OBJECTIVES':
        raise ValueError('No measured native GPU codebook authority.')
    gpu={r['id']:r for r in receipt['rows']};palettes={}
    for mode in ('Srgb','Linear'):
        for bits in (5,6):
            r=gpu['Palette'+str(bits)+mode]['mips'][0]
            if sha(r['path'])!=r['sha256'].lower():raise ValueError('The GPU palette changed.')
            n=1<<bits;palettes[bits,mode]=np.fromfile(r['path'],'<f4').reshape(n,n,4,4)
    r=alpha_receipt['table']
    if sha(r['path'])!=r['sha256'].lower():raise ValueError('The GPU alpha table changed.')
    alpha=np.fromfile(r['path'],'<f4').reshape(256,256,8)
    linear=palettes[6,'Linear'][:,:,:,1].ravel();srgb=palettes[6,'Srgb'][:,:,:,1].ravel();codes=np.rint(linear*255).astype(int);table={}
    for code,value in zip(codes,srgb):
        if int(code)in table:assert table[int(code)]==float(value)
        else:table[int(code)]=float(value)
    sorted_codes=np.asarray(sorted(table,key=lambda c:(table[c],c)));sorted_values=np.asarray([table[int(c)]for c in sorted_codes])
    return dict(palettes=palettes,alpha=alpha),sorted_values,sorted_codes


def material_native_mips(record,codebook,linear=False):
    """Consume a pinned unpacked BC3 cache; Crunch and BC3 hashes stay distinct."""
    import numpy as np
    import material_fields as fields
    payload=Path(record['path']).read_bytes()
    if hashlib.sha256(payload).hexdigest()!=record['sha256'].lower():raise ValueError('The native BC3 cache changed.')
    result=[]
    for mip in record['mips']:
        raw=payload[mip['offset']:mip['offset']+mip['byteLength']]
        pixels=fields.decode_bc3(raw,mip['width'],mip['height'],'Linear' if linear else 'Srgb',codebook)
        result.append(np.rint(pixels*255).astype(np.uint8) if linear else pixels)
    return result


def material_owned_main_mips(reference):
    """Read a completed BC7 Main by its exact serialized container and encoded bytes."""
    import numpy as np
    from UnityPy.enums import TextureFormat
    from UnityPy.export import Texture2DConverter
    sys.path.insert(0,str(ROOT/'tools/asset-authoring/UnityAssetBuild'))
    from character_resources import load_serialized_bundle,typed_bundle_asset
    path=checked_catalog_path(reference['path'])
    if sha(path)!=reference['sha256'].lower():raise ValueError('The owned Main source bundle changed.')
    environment,serialized=load_serialized_bundle(path)
    obj=typed_bundle_asset(environment,reference['asset'],'Texture2D').deref();texture=obj.read();payload=texture.get_image_data()
    digest=hashlib.sha256(payload).hexdigest()
    if digest!=reference['encodedSha256']or texture.m_TextureFormat!=25 or texture.m_ColorSpace!=1:
        raise ValueError('The owned Main is not the declared sRGB BC7 source.')
    if texture.m_Width!=texture.m_Height or texture.m_MipCount!=texture.m_Width.bit_length():raise ValueError('The owned Main mip layout is incomplete.')
    images=[];offset=0
    for level in range(texture.m_MipCount):
        width=max(1,texture.m_Width>>level);length=((width+3)//4)**2*16
        image=Texture2DConverter.parse_image_data(payload[offset:offset+length],width,width,TextureFormat.BC7,obj.version,obj.platform,flip=False)
        images.append(np.asarray(image.convert('RGBA'),dtype=np.uint8));offset+=length
    if offset!=len(payload):raise ValueError('The owned Main encoded mip span differs.')
    authority=dict(status='EXACT_UNIQUE_TEXTURE_CONTAINER',sourceBundleSha256=reference['sha256'].lower(),asset=reference['asset'],
        serializedFile=serialized.name,pathId=obj.path_id,width=texture.m_Width,height=texture.m_Height,mipCount=texture.m_MipCount,
        format=texture.m_TextureFormat,colorSpace=texture.m_ColorSpace)
    return images,authority


def retone_materials():
    """Transform completed Main fields without repeating their geometric boundary solve."""
    import numpy as np
    import material_fields as fields
    parser=argparse.ArgumentParser();parser.add_argument('--inputs',type=Path,required=True);args=parser.parse_args()
    input_path=checked_catalog_path(args.inputs);plan=material_paths(read(input_path),input_path.parent)
    if plan.get('schemaVersion')!=1 or not 1<=len(plan['operations'])<=32:raise ValueError('A retained Main batch needs 1..32 explicit operations.')
    for reference in plan['inputs']:
        if sha(reference['path'])!=reference['sha256'].lower():raise ValueError('A retained Main dependency changed.')
    index=material_pinned(plan['contextIndex'])['rows'];closure=material_pinned(plan['calibrationClosure'])
    if closure['status']!='PASS_ALL156_SOURCE_FIELDS_AND468_LOD_MAPPINGS'or closure['sourceOwner']['sha256']!=sha(fields.__file__):
        raise ValueError('The complete source/LOD calibration closure differs.')
    authority={};prepared=[]
    for operation in plan['operations']:
        selector=operation['selector'];matches=[row for row in index if all(row[key]==selector[key]for key in('nativeHeadFingerprint','nativeBodyFingerprint','lod','samplerSourceInputKey'))]
        if len(matches)!=1:raise ValueError('The retained Main context is not unique.')
        row=matches[0]
        if row['sampler']['shaderFamily']!='Lux/Human/Skin':raise ValueError('The source Main shader has different pigment semantics.')
        proof=[entry for entry in closure['rows']if entry['head']==row['head']and entry['status']=='PASS']
        if len(proof)!=1 or proof[0]['calibration']!=operation['coefficient']:raise ValueError('The LOD0 calibration belongs to another source.')
        coefficient=material_pinned(operation['coefficient']);source=operation['source'];images,record=material_owned_main_mips(source)
        if images[0].shape[:2]!=(row['sampler']['textureInputs']['headMain']['height'],row['sampler']['textureInputs']['headMain']['width']):
            raise ValueError('The retained source no longer has this Head atlas layout.')
        family=[r for r in index if r['internalId']==row['internalId']];packets=material_main_inputs(row,family)
        head=fields.source_context(row,packets[row['headCanonical']['path']],packets[row['bodyCanonical']['path']])[0]
        domain=fields.domain_buffers(row,packets[row['headCanonical']['path']],packets[row['bodyCanonical']['path']],family,packets,
            dict(fieldSha256=sha(fields.__file__),domainSha256=sha(fields.__file__)))
        height,width=images[0].shape[:2];atlas,uv_proof=fields.surface_tone_atlas(head,coefficient,width,height)
        def source_mips():
            for image in images:yield image,np.ones(image.shape[:2],bool),np.zeros(image.shape[:2],bool)
        key=source['encodedSha256'];name=row['head']+'_L'+str(row['lod'])+'_Main';folder=require_workspace()/name
        task=write_material_main_task(folder,name,source_mips(),images,dict(sourceBundle=source['path'],sourceBundleSha256=source['sha256'],asset=source['asset']),
            key,coefficient,domain['weight'],spatial_field=atlas)
        previous=authority.setdefault(key,record)
        if previous!=record:raise ValueError('A batch assigned one encoded source to different serialized owners.')
        preparation=dict(status='MAIN_ONLY_SOURCE_PREPARED_GPU_PENDING',kind='HeadMain',head=row['head'],lod=row['lod'],task=task,
            sourceInput=operation,sourceContext=selector,uvProof=uv_proof,numericSourceSha256=sha(fields.__file__),sourceMainAlphaAllMipsExact=True)
        write(folder/'Preparation.json',preparation);prepared.append(dict(id=name,preparation=dict(path=str(folder/'Preparation.json'),sha256=sha(folder/'Preparation.json'))))
        print('Prepared',name,flush=True)
    write(require_workspace()/'SourceAssetAuthority.json',dict(schemaVersion=1,records=authority))
    write(require_workspace()/'MaterialPreparation.json',dict(status='READY_RETONED_MAIN_TASKS_GPU_PENDING',tasks=prepared,inputs=dict(path=str(input_path),sha256=sha(input_path)),
        sourceOwner=dict(path=fields.__file__,sha256=sha(fields.__file__)),nativeRuntimeMaterialsChanged=False))


def generate_material_main(row,family_rows,packets,head_main,body_main,source_epoch,memo=None):
    """Use the one Main-only numeric owner, with explicit immutable data inputs."""
    import material_fields as fields
    return fields.prepare_head_main(row,packets[row['headCanonical']['path']],packets[row['bodyCanonical']['path']],family_rows,packets,head_main,body_main,source_epoch,memo)


def generate_body_material_main(packet_reference,bone_paths,consumer_references,main) -> 'tuple[Any, dict[str, Any]]':
    """Independent Body atlas: no Head-field or Head-texture dependency."""
    import material_fields as fields
    packet=material_packet(packet_reference)
    protected=[(reference,material_packet(reference))for reference in consumer_references]
    return fields.prepare_body_main(packet,bone_paths,protected,main)


def material_roi(reference):
    import numpy as np
    if sha(reference['path'])!=reference['sha256'].lower():raise ValueError('The physical source ROI changed.')
    return np.load(reference['path'])


def resolve_material_task(task,authority):
    """Return a copy with only asset replaced; keep every bundle/layout field."""
    authority=read(authority)if isinstance(authority,(str,Path))else authority
    key=str(task['sourceEncodedSha256']).lower();record=authority['records'].get(key)
    if record is None or record['status']!='EXACT_UNIQUE_TEXTURE_CONTAINER':raise ValueError('No unique exact source container authority: '+key)
    if str(task['sourceBundleSha256']).lower()!=record['sourceBundleSha256']:raise ValueError('The task source bundle is outside the exact PPtr authority.')
    for name in('width','height','mipCount'):
        if name in task and task[name]!=record[name]:raise ValueError('The task layout differs from its serialized source: '+name)
    result=dict(task);result['asset']=record['asset'];return result

def extract_material_native(request) -> 'dict[str, Any]':
 if HERE is None:raise ValueError('Initialize the existing model workspace before native extraction.')
 workspace=checked_catalog_path(HERE)
 if not workspace.is_relative_to(ROOT/'tools'):raise ValueError('Model work data must be in the repository tools workspace.')
 out=checked_catalog_path(request['outputDirectory'])
 if not out.is_relative_to(workspace):raise ValueError('Native extraction output escaped the active model workspace.')
 import UnityPy,texture2ddecoder
 from UnityPy.helpers.ResourceReader import get_resource_data
 out.mkdir(parents=True,exist_ok=True);groups=collections.defaultdict(list)
 for row in request['records']:groups[row['sourceBundle']].append(row)
 result=[]
 for source,rows in groups.items():
  expected={r['sourceBundleSha256'].lower() for r in rows};assert len(expected)==1 and sha(source)in expected
  env=UnityPy.load(source)
  for row in rows:
   objects=[v for v in env.objects if v.type.name=='Texture2D' and int(v.path_id)==int(row['pathId']) and v.assets_file.name==row['serializedFile']]
   assert len(objects)==1,('Serialized Texture2D pointer ambiguity',source,row['serializedFile'],row['pathId'],len(objects))
   obj=objects[0];metadata=obj.read_typetree();data=obj.read();raw=bytes(data.image_data)
   if not raw:
    stream=metadata['m_StreamData'];assert stream['size']>0
    raw=get_resource_data(stream['path'],obj.assets_file,stream['offset'],stream['size'])
   assert hashlib.sha256(raw).hexdigest()==row['encodedSha256']
   for key in ('m_Width','m_Height','m_MipCount','m_TextureFormat','m_ColorSpace'):assert metadata[key]==row['metadata'][key]
   width,height,count,format=(metadata[k]for k in ('m_Width','m_Height','m_MipCount','m_TextureFormat'));assert format in (12,29)
   complete=[];mips=[];at=0
   for level in range(count):
    w,h=max(1,width>>level),max(1,height>>level);size=((w+3)//4)*((h+3)//4)*16
    value=getattr(texture2ddecoder,'unpack_unity_crunch')(raw,level) if format==29 else raw[at:at+size]
    assert len(value)==size;offset=sum(map(len,complete));complete.append(value);mips.append(dict(level=level,width=w,height=h,offset=offset,byteLength=size));at+=size
   value=b''.join(complete);path=checked_catalog_path(out,row['encodedSha256']+'.bc3');payload_sha=hashlib.sha256(value).hexdigest()
   if path.exists():assert sha(path)==payload_sha
   else:path.write_bytes(value)
   result.append(dict(encodedSha256=row['encodedSha256'],path=str(path),sha256=payload_sha,byteLength=len(value),mips=mips,pixelDecode=False,origin={key:row[key]for key in('encodedSha256','sourceBundleSha256','serializedFile','pathId')}))
  del env
 return dict(status='EXACT_NATIVE_BC3_MIPS_FROM_PINNED_SOURCES',rows=result,bundlesRead=len(groups),pixelDecodeCount=0)

def write_material_main_task(folder,name,iterator,originals,locator,key,tone=None,c1_weight=None,tone_fraction=None,spatial_field=None):
    """Serialize one bounded Main task; the existing Builder owns Unity output."""
    import numpy as np
    import material_fields as fields
    if HERE is None:raise ValueError('Initialize the existing model workspace before task serialization.')
    workspace=checked_catalog_path(HERE)
    if not workspace.is_relative_to(ROOT/'tools'):raise ValueError('Model work data must be in the repository tools workspace.')
    folder=checked_catalog_path(folder)
    if not folder.is_relative_to(workspace):raise ValueError('The Main task escaped the active model workspace.')
    folder.mkdir(parents=True,exist_ok=False)
    field=folder/'Main.rgba8';control=folder/'Main.native.rgba8';proof=[];offset=0
    if tone is not None and c1_weight is None: raise ValueError('Head tone correction requires its source weight field.')
    weight=c1_weight.astype(float) if c1_weight is not None and tone is not None else None
    iterator=iter(iterator);mask_paths={}
    with field.open('wb')as output,control.open('wb')as baseline:
        for level,original in enumerate(originals):
            pixels,owned,protected=next(iterator)
            if not level:
                for mask_name,mask in(('owned',owned),('protected',protected)):
                    if tone is not None:mask=np.full(mask.shape,mask_name=='owned',bool)
                    path=folder/('Main-'+mask_name.title()+'.u8');path.write_bytes(mask[::-1].astype(np.uint8).tobytes());mask_paths[mask_name]=(path,sha(path))
            tone_proof=None
            if tone is not None:
                assert weight is not None
                if level:weight=fields.downsample(weight).max(axis=(1,3))
                if tone.get('policy')=='surface-local-v4':
                    if spatial_field is None:raise ValueError('Surface tone needs its source-metric UV field.')
                    if level:spatial_field=fields.surface_tone_mip(spatial_field)
                    pixels,tone_proof=fields.surface_tone_main_mip(pixels,weight,tone,spatial_field)
                elif tone.get('policy')=='regional-head-bone-v1':
                    if tone_fraction is None:raise ValueError('Regional tone needs its explicit UV semantic field.')
                    if level:tone_fraction=fields.regional_fraction_mip(tone_fraction)
                    pixels,tone_proof=fields.regional_tone_main_mip(pixels,weight,tone,tone_fraction)
                else:pixels,tone_proof=fields.tone_main_mip(pixels,weight,tone)
            output.write(pixels.tobytes());baseline.write(original.tobytes())
            row: dict[str, Any]=dict(level=level,width=pixels.shape[1],height=pixels.shape[0],offset=offset,byteLength=pixels.nbytes,ownedTexels=int((owned&~protected).sum()),protectedTexels=int(protected.sum()))
            if tone is not None:row.update(toneProof=tone_proof,c1AliasOwnedTexels=row['ownedTexels'],ownedTexels=pixels.shape[0]*pixels.shape[1],protectedTexels=0)
            proof.append(row);offset+=pixels.nbytes
    try:next(iterator)
    except StopIteration:pass
    else:raise ValueError('The Main field added an undeclared mip.')
    h,w=originals[0].shape[:2]
    task=dict(id=name,name=name,role='Main',width=w,height=h,mipCount=len(originals),sRGB=True,raw=str(field),rawSha256=sha(field),nativeRaw=str(control),nativeRawSha256=sha(control),mips=proof,sourceEncodedSha256=key)
    for mask,(path,mask_sha)in mask_paths.items():task.update({mask+'Mask':str(path),mask+'MaskSha256':mask_sha})
    task.update({k:locator[k]for k in('sourceBundle','sourceBundleSha256','asset')})
    # Bundle/asset selection is validated through resolve_material_task before
    # passing this Task to the existing Unity authoring owner; no Texture.name.
    return task


def material_paths(value: Any,base) -> Any:
    """Resolve only explicit file references, never Unity asset/renderer names."""
    if isinstance(value,list):return [material_paths(row,base)for row in value]
    if not isinstance(value,dict):return value
    result={key:material_paths(child,base)for key,child in value.items()}
    for key in('path','sourceBundle','resolvedSourceBundle'):
        is_reference=(key=='path'and 'sha256'in value)or(key!='path'and 'sourceBundleSha256'in value)
        if is_reference and isinstance(value.get(key),str):
            path=Path(value[key]);result[key]=str(Path(os.path.abspath(path if path.is_absolute()else base/path)))
    return result


def material_pinned(reference: dict[str, Any]) -> Any:
    path=Path(reference['path'])
    if sha(path)!=reference['sha256'].lower():raise ValueError('A material input changed: '+str(path))
    return material_paths(read(path),path.absolute().parent)


def material_native_layout(key,native,locator,authority):
    declared=authority['records'].get(key)
    if declared is None or declared.get('status')!='EXACT_UNIQUE_TEXTURE_CONTAINER':raise ValueError('No exact source-container authority.')
    if native.get('encodedSha256')!=key or any(str(locator[k]).lower()!=str(declared[k]).lower()for k in('sourceBundleSha256','serializedFile','pathId')):
        raise ValueError('The cache/locator changed its exact source PPtr authority.')
    origin=native.get('origin',{})
    if any(origin.get(name)!=declared[name]for name in('sourceBundleSha256','serializedFile','pathId'))or origin.get('encodedSha256')!=key:
        raise ValueError('The native BC3 extraction belongs to another encoded/CAB/PPtr source.')
    if declared['format']not in(12,29)or declared['colorSpace']!=1:
        raise ValueError('This Main source contract requires native sRGB DXT5/Crunch.')
    if len(native['mips'])!=declared['mipCount']:raise ValueError('The native cache omitted a declared mip.')
    offset=0
    for level,mip in enumerate(native['mips']):
        width=max(1,declared['width']>>level);height=max(1,declared['height']>>level);length=((width+3)//4)*((height+3)//4)*16
        expected=dict(level=level,width=width,height=height,offset=offset,byteLength=length)
        if any(type(mip.get(name))is not int or mip[name]!=value for name,value in expected.items()):
            raise ValueError('The native BC3 mip order, layout or contiguous span changed.')
        offset+=length
    if native.get('byteLength',offset)!=offset or Path(native['path']).stat().st_size!=offset:
        raise ValueError('The native BC3 cache has another complete byte span.')
    if native.get('uncrunchedSha256',native['sha256']).lower()!=native['sha256'].lower():
        raise ValueError('The native BC3 unpacked identity is inconsistent.')
    return declared


def material_body_source(operation,pinned,authority):
    """Use the existing independent Body owner's one data authority."""
    source=pinned(operation['sourceAuthority']);row=source['referenceRow'];key=operation['sourceEncodedSha256']
    if row['sampler']['textureInputs']['bodyMain']['encodedSha256']!=key:
        raise ValueError('The Body atlas belongs to another encoded source owner.')
    if row['sampler']['shaderFamily']!='Lux/Human/Skin':raise ValueError('Unsupported native Main shader semantics.')
    packet=row['bodyCanonical'];bones=row['bodyNativeBonePaths'];protected=source['protectedConsumers']
    if not protected or len({(item['part'],item['lod'])for item in protected})!=len(protected):
        raise ValueError('The Body source has an empty/duplicate protected consumer scope.')
    parts={item['part']for item in protected}
    if parts!={'Forearms','Hands','Feet'}or any({item['lod']for item in protected if item['part']==part}!={0,1,2}for part in parts):
        raise ValueError('The native Body atlas protection must cover the other three parts at every native LOD.')
    for field,expected in(('packet',packet),('bonePaths',bones),('protectedConsumers',protected)):
        if field in operation and operation[field]!=expected:raise ValueError('The Body operation differs from its exact '+field+' source authority.')
    name=authority['records'][key]['name']
    if 'sourceName'in operation and operation['sourceName']!=name:raise ValueError('The Body label is not its actual source texture name.')
    return packet,bones,protected,name


def material_calibration_owner(row,family,body_groups,plan):
    low=next(r for r in family if r['lod']==0);body_key=low['sampler']['textureInputs']['bodyMain']['encodedSha256']
    body=body_groups[body_key]['outputs'][0]
    if row['samplerSourceInputKey']!=low['samplerSourceInputKey']:
        raise ValueError('The explicit shared-LOD calibration has different native sampler inputs.')
    return dict(headInternalId=low['internalId'],sourceInputKey=low['samplerSourceInputKey'],calibratedLod=0,
        nativeHeadFingerprint=low['nativeHeadFingerprint'],nativeBodyFingerprint=low['nativeBodyFingerprint'],
        headMainEncodedSha256=low['sampler']['textureInputs']['headMain']['encodedSha256'],
        headCanonical=low['headCanonical'],bodyCanonical=low['bodyCanonical'],derivedBodyMainSha256=body['sha256'],
        numericSourceSha256=plan['numericSourceSha256'],sourceEpochSha256=plan['sourceEpoch']['sha256'],contextIndexSha256=plan['contextIndex']['sha256'])


def material_resume(folder,basis):
    """A completed source may be reused only with the same inputs and output bytes."""
    ready=folder/'Preparation.json'
    if not folder.exists():return None
    if not ready.is_file():raise ValueError('A prior material source is incomplete; preserve it before retrying.')
    record=read(ready)
    if record.get('status')!='MAIN_ONLY_SOURCE_PREPARED_GPU_PENDING'or record.get('basis')!=basis:
        raise ValueError('A prior material field has another input/operation/numeric epoch.')
    for reference in record['inputPins']:
        if sha(reference['path'])!=reference['sha256'].lower():raise ValueError('A completed material input changed before resume.')
    task=record['task']
    for key in('raw','nativeRaw','ownedMask','protectedMask'):
        path=Path(task[key])
        if not path.is_file()or sha(path)!=task[key+'Sha256']:
            raise ValueError('A completed material output changed or was released; use its checked codec owner rather than silently regenerating it.')
    if record['kind']=='BodyMain':
        reference=record['outputs'][0]
        if sha(reference['path'])!=reference['sha256']:raise ValueError('A completed Body mip0 changed before resume.')
    else:
        reference=record['calibration']
        if sha(reference['path'])!=reference['sha256']:raise ValueError('A completed Head calibration changed before resume.')
    return record


def generate_materials():
    """One source at a time; Body data is independent of completed Head work."""
    import numpy as np
    import material_fields as fields
    parser=argparse.ArgumentParser();parser.add_argument('--inputs',type=Path,required=True)
    parser.add_argument('--maximum-sources',type=int,default=1);parser.add_argument('--stop-file',type=Path)
    args=parser.parse_args()
    if args.maximum_sources<1:raise ValueError('A material batch must own at least one new source.')
    if args.stop_file and args.stop_file.exists():return
    input_path=args.inputs.absolute();plan=material_paths(read(input_path),input_path.parent)
    if plan['schemaVersion']!=1 or not plan['operations']or any(row['kind']not in('HeadMain','BodyMain')for row in plan['operations']):
        raise ValueError('Invalid/empty material source plan or operation kind.')
    if sha(fields.__file__)!=plan['numericSourceSha256'].lower():raise ValueError('The material numeric source epoch changed.')
    pinned=material_pinned;epoch=pinned(plan['sourceEpoch'])
    needs_head=any(row['kind']=='HeadMain'for row in plan['operations'])
    index=pinned(plan['contextIndex'])if needs_head else None
    body_groups={}
    if needs_head:
        body_delivery=pinned(plan['bodyDelivery'])
        if body_delivery['status']not in('COMPLETE_NATIVE_PACKING_BODY13','COMPLETE_INDEPENDENT_BODY_MAIN'):
            raise ValueError('The independent Body Main authority is incomplete.')
        body_groups={row['encodedMainSha256']:row for row in body_delivery['groups']}
        if len(body_groups)!=len(body_delivery['groups']):raise ValueError('Duplicate independent Body Main source keys.')
    locators=pinned(plan['sourceLocators'])['sourceLocators'];authority=pinned(plan['nativeAssetAuthority'])
    extracted=pinned(plan['nativeBc3Authority'])
    if extracted['status']!='EXACT_NATIVE_BC3_MIPS_FROM_PINNED_SOURCES':raise ValueError('No completed native BC3 extraction authority.')
    native_bc3={row['encodedSha256']:row for row in extracted['rows']}
    if len(native_bc3)!=len(extracted['rows']):raise ValueError('Duplicate native BC3 extraction source keys.')
    required_keys={row['selector']['headMainEncodedSha256']if row['kind']=='HeadMain'else row['sourceEncodedSha256']for row in plan['operations']}
    for key in required_keys:material_native_layout(key,native_bc3[key],locators[key],authority)
    common=[plan[key]for key in('sourceEpoch','sourceLocators','nativeAssetAuthority','nativeBc3Authority','gpuPalettes','gpuAlpha')]
    input_pin=dict(path=str(input_path),sha256=sha(input_path));completed=[];created=0;resumed=0;palette_state=None
    def pin(path):return dict(path=str(Path(path)),sha256=sha(path))
    def references(values):
        result={}
        def visit(value):
            if isinstance(value,dict):
                if isinstance(value.get('path'),str)and isinstance(value.get('sha256'),str):result[value['path']]=dict(path=value['path'],sha256=value['sha256'])
                for child in value.values():visit(child)
            elif isinstance(value,list):
                for child in value:visit(child)
        visit(values);return list(result.values())
    def checkpoint(status):
        write(require_workspace() /'MaterialPreparation.json',dict(status=status,operations=completed,newSources=created,resumedSources=resumed,inputs=input_pin,editor=False,native=False))
        bodies=[dict(encodedMainSha256=row['sourceEncodedSha256'],outputs=row['outputs'],sourceAuthority=row['sourceAuthority'],preparation=pin(Path(row['directory'])/'Preparation.json'))for row in completed if row['kind']=='BodyMain']
        if bodies:
            total=sum(row['kind']=='BodyMain'for row in plan['operations'])
            write(require_workspace() /'IndependentBodyMain.json',dict(status='COMPLETE_INDEPENDENT_BODY_MAIN'if len(bodies)==total else'PARTIAL_INDEPENDENT_BODY_MAIN',groups=bodies,declaredSources=total,inputs=input_pin,numericSourceSha256=plan['numericSourceSha256']))
    for operation in plan['operations']:
        if args.stop_file and args.stop_file.exists():break
        kind=operation['kind'];pins=list(common);head_source=None;body_source=None
        if kind=='HeadMain':
            if index is None:raise ValueError('Head Main requires its declared context index.')
            tone_policy=operation.get('tonePolicy','uniform-v3')
            if tone_policy not in('uniform-v3','regional-head-bone-v1','surface-local-v4'):raise ValueError('Unknown Head Main tone policy.')
            selector=operation['selector']
            matches=[row for row in index['rows']if all(row[key]==selector[key]for key in('nativeHeadFingerprint','nativeBodyFingerprint','lod','samplerSourceInputKey'))and row['sampler']['textureInputs']['headMain']['encodedSha256']==selector['headMainEncodedSha256']]
            if len(matches)!=1:raise ValueError('The exact Head geometry/material selector is not unique.')
            row=matches[0]
            if row['sampler']['shaderFamily']!='Lux/Human/Skin':raise ValueError('Unsupported native Main shader semantics.')
            key=selector['headMainEncodedSha256'];family=[r for r in index['rows']if r['internalId']==row['internalId']]
            if len(family)!=3 or {r['lod']for r in family}!={0,1,2}:raise ValueError('The native Head protection family does not cover all three LODs.')
            body_key=row['sampler']['textureInputs']['bodyMain']['encodedSha256'];body_ref=body_groups[body_key]['outputs'][0]
            if any(operation['derivedBodyMain'][name]!=body_ref[name]for name in('path','sha256')):raise ValueError('The derived Body input belongs to another packing epoch.')
            coefficient_owner=material_calibration_owner(row,family,body_groups,plan)
            if tone_policy!='uniform-v3':coefficient_owner=dict(coefficient_owner,tonePolicy=tone_policy)
            calibration=None
            if row['lod']:
                calibration=pinned(operation['coefficient']);actual_owner=calibration['owner']
                low=next(r for r in family if r['lod']==0)
                low_operations=[op for op in plan['operations']if op['kind']=='HeadMain'and op['selector']['nativeHeadFingerprint']==low['nativeHeadFingerprint']and op['selector']['nativeBodyFingerprint']==low['nativeBodyFingerprint']and op['selector']['lod']==0 and op['selector']['samplerSourceInputKey']==low['samplerSourceInputKey']and op['selector']['headMainEncodedSha256']==low['sampler']['textureInputs']['headMain']['encodedSha256']]
                if len(low_operations)>1:raise ValueError('The LOD0 calibration operation is duplicated.')
                roi=low_operations[0]['roi']if low_operations else actual_owner['roi']
                coefficient_owner=dict(coefficient_owner,roi=roi)
                if actual_owner!=coefficient_owner:raise ValueError('A lower LOD changed its derived Body/numeric/LOD0 calibration basis.')
                if calibration['coefficient']['sourceInputKey']!=coefficient_owner['sourceInputKey']or calibration['coefficient']['calibratedLod']!=0:
                    raise ValueError('The lower-LOD coefficient does not belong to its explicit LOD0 source.')
                pins.extend([operation['coefficient'],roi,low['headCanonical'],low['bodyCanonical']])
            else:coefficient_owner=dict(coefficient_owner,roi=operation['roi']);pins.append(operation['roi'])
            pins.extend([plan['contextIndex'],plan['bodyDelivery'],body_ref,row['headCanonical'],row['bodyCanonical']]+[r['headSource']['initialCanonical']for r in family])
            head_source=(row,family,body_ref,coefficient_owner,calibration,tone_policy)
            name=row['head']+'_L'+str(row['lod'])+'_Main'
        else:
            key=operation['sourceEncodedSha256'];packet_ref,bones,protected,name=material_body_source(operation,pinned,authority)
            pins.extend([operation['sourceAuthority'],packet_ref,protected]);name=asset_names.source_label(name)+'_Main'
            body_source=(packet_ref,bones,protected)
        folder=checked_catalog_path(require_workspace(),name)
        paths=[folder/leaf for leaf in('Main.rgba8','Main.native.rgba8','Main-Owned.u8','Main-Protected.u8')]
        if max(len(str(path))for path in paths+[Path(locators[key]['sourceBundle'])])>240:raise ValueError('A material task exceeded the existing short-path contract.')
        native=native_bc3[key]
        resolve_material_task(dict(sourceEncodedSha256=key,sourceBundleSha256=locators[key]['sourceBundleSha256'],width=authority['records'][key]['width'],height=authority['records'][key]['height'],mipCount=authority['records'][key]['mipCount']),authority)
        basis=dict(inputs=input_pin,operation=operation,numericSourceSha256=plan['numericSourceSha256'],sourceEpochSha256=plan['sourceEpoch']['sha256'])
        old=material_resume(folder,basis)
        if old is not None:completed.append(old);resumed+=1;continue
        if created>=args.maximum_sources:break
        if palette_state is None:
            gpu=pinned(plan['gpuPalettes']);alpha=pinned(plan['gpuAlpha']);codebook,sorted_values,sorted_codes=material_codebook(gpu,alpha)
            palette_pins=[r['mips'][0]for r in gpu['rows']if r['id']in('Palette5Srgb','Palette6Srgb','Palette5Linear','Palette6Linear')]+[alpha['table']]
            palette_state=(codebook,sorted_values,sorted_codes,palette_pins)
        codebook,sorted_values,sorted_codes,palette_pins=palette_state
        pins.extend([native,palette_pins,dict(path=locators[key]['sourceBundle'],sha256=locators[key]['sourceBundleSha256'])])
        for reference in references(pins):
            if sha(reference['path'])!=reference['sha256'].lower():raise ValueError('A material operation input changed before generation.')
        source=material_native_mips(native,codebook)[0]
        if head_source is not None:
            row,family,body_ref,coefficient_owner,calibration,tone_policy=head_source
            packets=material_main_inputs(row,family);shape=row['sampler']['textureInputs']['bodyMain']
            body=fields.linear_rgba(np.fromfile(body_ref['path'],np.uint8).reshape(shape['height'],shape['width'],4))
            field,buffers,proof=generate_material_main(row,family,packets,source,body,epoch['operatorSignatureEpoch']);c1_weight=buffers['weight'].copy()
            if row['lod']==0:
                with material_roi(operation['roi'])as roi:
                    if tone_policy=='surface-local-v4':
                        base_coefficient=fields.coefficient(row,fields.encode_main_rgba(field,sorted_values,sorted_codes),body,dict(roi=operation['roi']),roi)
                        head=fields.source_context(row,packets[row['headCanonical']['path']],packets[row['bodyCanonical']['path']])[0]
                        coefficient=dict(fields.surface_tone_references(head,field,base_coefficient['t']),sourceInputKey=row['samplerSourceInputKey'],bodyReference=base_coefficient['bodyReference'],roi=operation['roi'])
                    elif tone_policy=='regional-head-bone-v1':
                        region_roi,region_proof=fields.regional_head_roi(row,packets[row['headCanonical']['path']],packets[row['bodyCanonical']['path']],roi)
                        coefficient=fields.regional_coefficient(row,fields.encode_main_rgba(field,sorted_values,sorted_codes),body,dict(sourceFrameAndBodyRoi=operation['roi'],derivedFullHeadFront=region_proof),region_roi)
                    else:coefficient=fields.coefficient(row,fields.encode_main_rgba(field,sorted_values,sorted_codes),body,dict(roi=operation['roi']),roi)
            else:
                if calibration is None:raise ValueError('A lower LOD requires its source calibration.')
                coefficient=calibration['coefficient']
            tone_fraction=None;spatial_field=None
            if tone_policy=='surface-local-v4':
                if coefficient.get('policy')!=tone_policy:raise ValueError('Lower LOD surface calibration policy differs from LOD0.')
                texture=row['sampler']['textureInputs']['headMain'];head=fields.source_context(row,packets[row['headCanonical']['path']],packets[row['bodyCanonical']['path']])[0]
                spatial_field,surface_proof=fields.surface_tone_atlas(head,coefficient,texture['width'],texture['height'])
                proof=dict(proof,surfaceToneUvField=surface_proof)
            if tone_policy=='regional-head-bone-v1':
                if coefficient.get('policy')!=tone_policy:raise ValueError('Lower LOD regional calibration policy differs from LOD0.')
                texture=row['sampler']['textureInputs']['headMain']
                tone_fraction,region_uv_proof=fields.regional_fraction_atlas(row,packets[row['headCanonical']['path']],packets[row['bodyCanonical']['path']],texture['width'],texture['height'])
                proof=dict(proof,regionalUvField=region_uv_proof)
            changed=np.any(field!=source,axis=2);buffers['weight']=np.maximum(buffers['weight'],changed[::-1].astype(np.float32))
            originals=material_native_mips(native,codebook,True);iterator=fields.head_main_mips(field,buffers,originals,sorted_values,sorted_codes)
            task=write_material_main_task(folder,name,iterator,originals,locators[key],key,coefficient,c1_weight,tone_fraction,spatial_field)
            calibration=dict(headInternalId=row['internalId'],sourceInputKey=row['samplerSourceInputKey'],coefficient=coefficient,owner=coefficient_owner)
            write(folder/'Coefficient.json',calibration);extra=dict(calibration=pin(folder/'Coefficient.json'))
        else:
            if body_source is None:raise ValueError('The Body Main source was not prepared.')
            packet_ref,bones,protected=body_source
            field,proof=generate_body_material_main(packet_ref,bones,protected,source);originals=material_native_mips(native,codebook,True)
            candidate=fields.encode_main_rgba(field,sorted_values,sorted_codes,source_float=source,source_rgba=originals[0],source_protected=proof['protected'][::-1],native_alpha=True)
            task=write_material_main_task(folder,name,fields.body_main_mips(candidate,proof['locked'],originals),originals,locators[key],key)
            mip0=checked_catalog_path(folder,'Main-Mip0.rgba8');mip0.write_bytes(candidate.tobytes())
            extra=dict(sourceEncodedSha256=key,outputs=[dict(role='Main',**pin(mip0))],sourceAuthority=operation['sourceAuthority'])
            proof=dict(sourcePart='Body',headGeometryOrTextureDependency=False,alphaAndProtectedOutsideExact=True)
        task=resolve_material_task(task,authority)
        record=dict(status='MAIN_ONLY_SOURCE_PREPARED_GPU_PENDING',kind=kind,task=task,proof=proof,inputs=input_pin,basis=basis,inputPins=references(pins),numericSourceSha256=sha(fields.__file__),normalOrSpecGenerated=False,directory=str(folder),**extra)
        write(folder/'Preparation.json',record);completed.append(record);created+=1;checkpoint('SOURCE_BOUNDARY')
    checkpoint('HELD_AT_SOURCE_BOUNDARY'if args.stop_file and args.stop_file.exists()else'BOUNDED_MAIN_SOURCE_BATCH_COMPLETE')



def material_codec_file(path,base):
    path=Path(path);path=Path(os.path.abspath(path if path.is_absolute()else base/path))
    if not path.drive or str(path).startswith('\\\\'):raise ValueError('A local codec input is required.')
    for node in(path,*path.parents):
        if node.exists()or node.is_symlink():
            if node.is_symlink()or getattr(node.lstat(),'st_file_attributes',0)&0x400:
                raise ValueError('A codec input traverses a reparse point: '+str(path))
    return path


def material_codec_mask_counts(task):
    import numpy as np
    width,height=task['width'],task['height'];arrays=[]
    for name in('owned','protected'):
        data=Path(task[name+'Mask']).read_bytes()
        if len(data)!=width*height or hashlib.sha256(data).hexdigest()!=task[name+'MaskSha256']or not set(data)<=set((0,1)):
            raise ValueError('The actual codec mask differs from its binary authority.')
        arrays.append(np.frombuffer(data,np.uint8).reshape(height,width)[::-1]>0)
    owned,protected=arrays;result=[]
    if len(task['mips'])!=task['mipCount']:raise ValueError('A codec task omitted a mip.')
    for level,mip in enumerate(task['mips']):
        if level:
            h,w=owned.shape
            owned=owned.reshape(h//2,2,w//2,2).any(axis=(1,3))
            protected=protected.reshape(h//2,2,w//2,2).any(axis=(1,3))
        count=int(np.count_nonzero(owned&~protected))
        if mip['level']!=level or mip['ownedTexels']!=count:
            raise ValueError('The actual codec mask count differs: '+task['id']+'/'+str(level))
        result.append(dict(level=level,width=owned.shape[1],height=owned.shape[0],ownedTexels=count,protectedTexels=int(protected.sum())))
    return result


def material_codec_control(task,reference,add_pin):
    if set(reference)!={'input','report','id'}:raise ValueError('Unknown native control evidence.')
    old_input=material_pinned(reference['input']);report=material_pinned(reference['report'])
    add_pin(reference['input']);add_pin(reference['report'])
    if not report.get('completed')or not report.get('savedReloadAccepted')or report['inputSha256']!=reference['input']['sha256'].lower():
        raise ValueError('The native control has no completed immutable save/GPU epoch.')
    rows=[row for row in old_input['tasks']if row['id']==reference['id']]
    if len(rows)!=1:raise ValueError('The native control input is not unique.')
    old=rows[0]
    for key in('nativeRawSha256','sourceEncodedSha256','sourceBundleSha256','width','height','mipCount','sRGB'):
        if old[key]!=task[key]:raise ValueError('The native control belongs to another exact source/layout: '+key)
    rows=[row for row in report['saved']if row['id']==old['id']and row['variant']=='NativeBC7Control']
    if len(rows)!=1:raise ValueError('The native control payload is not unique.')
    saved=rows[0];encoded=material_codec_file(saved['encodedPath'],Path(reference['report']['path']).parent)
    add_pin(dict(path=str(encoded),sha256=saved['encodedSha256']))
    payload=encoded.read_bytes();offset=0
    if not saved['reloadGpuExact']or not saved['unreadable']or saved['graphicsFormat']!=('RGBA_BC7_SRGB'if task['sRGB']else'RGBA_BC7_UNorm')or len(saved['mips'])!=task['mipCount']:
        raise ValueError('The native control format or full GPU chain changed.')
    for level,mip in enumerate(saved['mips']):
        width=max(1,task['width']>>level);length=((width+3)//4)**2*16
        if mip['level']!=level or mip['offset']!=offset or mip['encodedBytes']!=length or hashlib.sha256(payload[offset:offset+length]).hexdigest()!=mip['encodedSha256']or mip['createdGpuSha256']!=mip['reloadedGpuSha256']:
            raise ValueError('A native control compressed/GPU mip changed.')
        offset+=length
    if len(payload)!=offset or saved['encodedBytes']!=offset:raise ValueError('The native control has another physical span.')
    return encoded,saved


def render_material_codec(resource,folder,generated,bundle,count):
    tokens={'__MATERIAL_FIELD_FOLDER__':Path(folder).as_posix(),'__MATERIAL_FIELD_GENERATED__':generated,
            '__MATERIAL_FIELD_BUNDLE__':bundle,'__MATERIAL_FIELD_TASK_COUNT__':str(count)}
    text=Path(resource).read_text('utf-8-sig')
    if not 1<=count<=32 or any('"'in value or '\\'in value or '\n'in value or '\r'in value for value in tokens.values()):
        raise ValueError('The codec resource arguments are not exact safe literals.')
    for token,value in tokens.items():
        if text.count(token)!=1:raise ValueError('The frozen codec resource has another token count.')
        text=text.replace(token,value)
    return text


def prepare_material_codec():
    parser=argparse.ArgumentParser();parser.add_argument('--inputs',type=Path,required=True)
    parser.add_argument('--destination',type=Path,required=True);args=parser.parse_args()
    input_path=material_codec_file(args.inputs,Path.cwd());plan=material_paths(read(input_path),input_path.parent)
    required={'schemaVersion','name','tasks','ownerSource','nativeAssetAuthority','ageScalars','scalarAuthority','scalarAuthorityProof','holdFile'}
    if set(plan)!=required or plan['schemaVersion']!=1:raise ValueError('Unknown material codec plan contract.')
    if not re.fullmatch('[A-Za-z][A-Za-z0-9_]{0,63}',plan['name']):raise ValueError('A codec batch needs a safe private semantic name.')
    if not isinstance(plan['tasks'],list)or not 1<=len(plan['tasks'])<=32:raise ValueError('A codec batch must contain 1..32 Main tasks.')
    if len(plan['ageScalars'])!=3 or any(type(v)not in(int,float)or not math.isfinite(v)or v<=0 for v in plan['ageScalars'])or not isinstance(plan['scalarAuthority'],str)or not plan['scalarAuthority']:
        raise ValueError('Native shader scalar authority is incomplete.')
    destination=checked_catalog_path(args.destination)
    if not destination.is_relative_to(require_workspace())or destination.exists():raise ValueError('The codec output must be a new active-workspace directory.')
    hold=material_codec_file(plan['holdFile'],input_path.parent)
    if not hold.is_relative_to(ROOT/'tools'):raise ValueError('The codec hold file escaped the owned tools workspace.')
    if hold.exists():raise ValueError('The codec is held at its normal pack boundary.')
    resource=Path(__file__).resolve().parents[2]/'asset-authoring/UnityAssetBuild/MaterialFieldBc7.cs.in'
    resource=checked_catalog_path(resource.parent,resource.name)
    pins={};observed={}
    def add_pin(reference):
        if set(reference)!={'path','sha256'}or not re.fullmatch('[0-9a-fA-F]{64}',reference['sha256']):raise ValueError('No exact codec input pin.')
        path=material_codec_file(reference['path'],input_path.parent);digest=reference['sha256'].lower();key=os.path.normcase(str(path))
        if key in pins and pins[key]['sha256']!=digest:raise ValueError('Conflicting codec source pins.')
        signature=(path.stat().st_size,path.stat().st_mtime_ns)
        if key not in observed or observed[key]!=signature:
            if sha(path)!=digest:raise ValueError('A codec source pin changed: '+str(path))
            observed[key]=signature
        pins[key]=dict(path=str(path),sha256=digest);return pins[key]
    source_input=add_pin(dict(path=str(input_path),sha256=sha(input_path)))
    preparation_source=add_pin(dict(path=str(Path(__file__).resolve()),sha256=sha(__file__)))
    owner=add_pin(plan['ownerSource']);authority_ref=add_pin(plan['nativeAssetAuthority'])
    scalar_ref=add_pin(plan['scalarAuthorityProof']);resource_ref=add_pin(dict(path=str(resource),sha256=sha(resource)))
    authority=material_pinned(authority_ref);tasks=[];proof=[];controls=[];identities=set()
    for entry in plan['tasks']:
        if set(entry)not in({'preparation','id'},{'preparation','id','nativeControl'}):raise ValueError('Unknown material task reference.')
        prep_ref=add_pin(entry['preparation']);preparation=material_pinned(prep_ref)
        rows=preparation.get('tasks',[preparation.get('task')]);rows=[row for row in rows if row and row['id']==entry['id']]
        if len(rows)!=1:raise ValueError('The exact prepared Main task is not unique.')
        task=copy.deepcopy(rows[0]);width=task['width']
        if task['role']!='Main'or type(width)is not int or width<=0 or width&(width-1)or task['height']!=width or task['mipCount']!=width.bit_length()or type(task['sRGB'])is not bool:
            raise ValueError('The frozen codec requires a complete square Main mip chain.')
        if not re.fullmatch('[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}',task['id'])or task['id']in identities:raise ValueError('Invalid or duplicate codec identity.')
        identities.add(task['id'])
        if any(task.get(k)for k in('precompressedNative','precompressedField','precompressedNativeSha256','precompressedFieldSha256')):
            raise ValueError('Precompression needs explicit saved native-control evidence; changed-field reuse is not an implicit fresh encode.')
        for key in('raw','nativeRaw','ownedMask','protectedMask','sourceBundle'):
            task[key]=str(material_codec_file(task[key],Path(prep_ref['path']).parent))
            add_pin(dict(path=task[key],sha256=task[key+'Sha256']))
        task=resolve_material_task(task,authority);declared=authority['records'][task['sourceEncodedSha256']]
        if task['sRGB']!=(declared['colorSpace']==1):raise ValueError('A native Main source color space changed.')
        expected_bytes=sum(max(1,width>>level)**2*4 for level in range(task['mipCount']))
        if any(Path(task[k]).stat().st_size!=expected_bytes for k in('raw','nativeRaw')):raise ValueError('A raw Main mip payload has another physical layout.')
        masks=material_codec_mask_counts(task)
        if max(len(task[k])for k in('raw','nativeRaw','ownedMask','protectedMask','sourceBundle'))>240:raise ValueError('A codec input exceeded the existing short-path contract.')
        control=None
        if 'nativeControl'in entry:
            path,saved=material_codec_control(task,entry['nativeControl'],add_pin)
            control=dict(path=path,saved=saved,proof=entry['nativeControl']);controls.append(control)
        tasks.append(task);proof.append(dict(id=task['id'],preparation=prep_ref,masks=masks,control=control))
    if hold.exists():raise ValueError('The codec was held before writing its immutable pack.')
    generated='Assets/MaterialFields_'+plan['name'];bundle='material_fields_'+plan['name'].lower()+'.bundle'
    source_text=render_material_codec(resource,destination,generated,bundle,len(tasks))
    destination.mkdir()
    for task,row in zip(tasks,proof):
        control=row['control']
        if control is None:continue
        folder=checked_catalog_path(destination,'NativeControls');folder.mkdir(exist_ok=True)
        target=checked_catalog_path(folder,control['saved']['encodedSha256']+'.bc7')
        if not target.exists():os.link(control['path'],target)
        if sha(target)!=control['saved']['encodedSha256']:raise ValueError('The retained native control cache changed.')
        task.update(precompressedNative=str(target),precompressedNativeSha256=control['saved']['encodedSha256'])
        add_pin(dict(path=str(target),sha256=control['saved']['encodedSha256']))
        row['control']=dict(encoded=dict(path=str(target),sha256=control['saved']['encodedSha256']),evidence=control['proof'])
    private=checked_catalog_path(destination,'MaterialFieldBc7.cs');private.write_text(source_text,'utf-8')
    private_ref=add_pin(dict(path=str(private),sha256=sha(private)))
    record=dict(status='PRIVATE_PERSISTENT_NATIVE_CONTROL_FIELD_BC7_PAIR_INPUT',tasks=tasks,ageScalars=plan['ageScalars'],scalarAuthority=plan['scalarAuthority'],
                privateSource=private_ref,ownerSource=owner,codecResource=resource_ref,sourceInput=source_input,nativeAssetAuthority=authority_ref,
                scalarAuthorityProof=scalar_ref,generatedAssets=generated,bundleName=bundle,holdFile=str(hold),sourcePins=list(pins.values()))
    write(destination/'Input.json',record)
    for row in proof:
        if isinstance(row['control'],dict):row['control']=dict(row['control'])
    write(destination/'SourceValidation.json',dict(status='EXACT_PREPARED_MAIN_MASK_LAYOUT_SOURCE_AND_CONTROL_PINS',rows=proof,normalOrSpecGenerated=False))
    ready=dict(status='CANONICAL_MATERIAL_CODEC_MAIN_BATCH_READY',input=dict(path=str(destination/'Input.json'),sha256=sha(destination/'Input.json')),
               source=private_ref,resource=resource_ref,preparationSource=preparation_source,ownerSource=owner,fields=len(tasks),mips=sum(t['mipCount']for t in tasks),nativeControlsReused=len(controls),
               newNativeControlsToEncode=len(tasks)-len(controls),fieldEncodeCount=1,editorInvoked=False,nativeInvoked=False,publicChanged=False)
    write(destination/'Ready.json',ready);print(json.dumps(ready))


def verify_material_codec():
    """Read compressed payloads, preserving the existing codec-vs-ART boundary."""
    import UnityPy
    from UnityPy.helpers.ResourceReader import get_resource_data
    parser=argparse.ArgumentParser();parser.add_argument('--inputs',type=Path,required=True)
    parser.add_argument('--report',type=Path,required=True);parser.add_argument('--output-directory',type=Path,required=True);args=parser.parse_args()
    input_path=material_codec_file(args.inputs,Path.cwd());report_path=material_codec_file(args.report,Path.cwd())
    inputs=read(input_path);report=read(report_path);destination=checked_catalog_path(args.output_directory)
    if not destination.is_relative_to(require_workspace()):raise ValueError('The codec proof escaped the active workspace.')
    paths=[checked_catalog_path(destination,name)for name in('SavedBundleValidation.json','ProductionBundleValidation.json')]
    if any(path.exists()for path in paths):raise ValueError('An immutable codec proof already exists.')
    if inputs.get('holdFile')and Path(inputs['holdFile']).exists():raise ValueError('Codec proof is held at its normal pack boundary.')
    tasks={r['id']:r for r in inputs['tasks']}
    if len(tasks)!=len(inputs['tasks'])or not 1<=len(tasks)<=32 or any(t['role']!='Main'for t in tasks.values()):raise ValueError('Not a bounded Main-only codec epoch.')
    if report['inputSha256']!=sha(input_path)or not report['completed']or not report['savedReloadAccepted']or not report['savedReloadGpuExact']:
        raise ValueError('The codec has no complete input/save/reload epoch.')
    saved=report['saved'];metrics={r['id']:r for r in report['rows']}
    if {(r['id'],r['variant'])for r in saved}!={(key,variant)for key in tasks for variant in('NativeBC7Control','FieldBC7')}or len(saved)!=2*len(tasks)or set(metrics)!=set(tasks):
        raise ValueError('The measured/saved codec consumer set differs.')
    for key,task in tasks.items():
        row=metrics[key]
        if not row['sourceUnchanged']or len(row['mips'])!=task['mipCount']or any(not(m['nativeRgbaExpansionExact']and m['fieldReferenceProtectionExact']and m['whollyUnownedBlocksExact'])for m in row['mips']):
            raise ValueError('Original expansion, desired RGBA protection or wholly-unowned blocks changed.')
    production=report['production']
    if not production['fieldOnlyTextureSet']or not production['allMipGpuReloadExact']or not production['noRecompression']:
        raise ValueError('The field-only production bundle changed its codec contract.')
    def inspect(path,digest,expected):
        path=material_codec_file(path,report_path.parent)
        if sha(path)!=digest:raise ValueError('A saved codec bundle changed.')
        env=UnityPy.load(str(path));indexed={r['asset'].casefold():r for r in expected}
        if len(indexed)!=len(expected)or {k.casefold()for k in env.container}!=set(indexed):raise ValueError('A saved codec bundle has extra/missing assets.')
        rows=[]
        for asset,ptr in env.container.items():
            record=indexed[asset.casefold()];task=tasks[record['id']];obj=ptr.deref();tree=obj.read_typetree();texture=obj.read()
            if not isinstance(tree,dict): raise TypeError('A saved texture requires its dictionary typetree.')
            from UnityPy.classes import Texture2D
            if not isinstance(texture,Texture2D): raise TypeError('A saved texture has another Unity object type.')
            if obj.type.name!='Texture2D'or tree['m_TextureFormat']!=25 or tree['m_IsReadable']or tree['m_ImageCount']!=1 or(tree['m_Width'],tree['m_Height'],tree['m_MipCount'])!=(task['width'],task['height'],task['mipCount'])or tree['m_ColorSpace']!=int(task['sRGB']):
                raise ValueError('A saved BC7 texture changed type/layout/readability/color space.')
            if record['graphicsFormat']!=('RGBA_BC7_SRGB'if task['sRGB']else'RGBA_BC7_UNorm')or not record['unreadable']or not record['reloadGpuExact']:
                raise ValueError('A saved texture has another format/GPU closure.')
            payload=bytes(texture.image_data)
            if not payload:
                stream=tree['m_StreamData'];payload=bytes(get_resource_data(stream['path'],obj.assets_file,stream['offset'],stream['size']))
            if len(payload)!=record['encodedBytes']or hashlib.sha256(payload).hexdigest()!=record['encodedSha256']or sha(record['encodedPath'])!=record['encodedSha256']:
                raise ValueError('Saved full compressed bytes differ from the measured payload.')
            if len(record['mips'])!=task['mipCount']:raise ValueError('A saved compressed mip was omitted.')
            offset=0
            for level,mip in enumerate(record['mips']):
                width=max(1,task['width']>>level);length=((width+3)//4)**2*16
                if mip['level']!=level or mip['offset']!=offset or mip['encodedBytes']!=length or hashlib.sha256(payload[offset:offset+length]).hexdigest()!=mip['encodedSha256']or mip['createdGpuSha256']!=mip['reloadedGpuSha256']:
                    raise ValueError('A saved compressed/GPU mip differs.')
                offset+=length
            if offset!=len(payload):raise ValueError('A saved compressed payload has trailing/missing bytes.')
            rows.append(dict(id=record['id'],variant=record.get('variant','FieldBC7'),asset=asset,mips=task['mipCount'],encodedBytes=len(payload),encodedSha256=record['encodedSha256'],unreadable=True))
        return rows
    rows=inspect(report['bundlePath'],report['bundleSha256'],saved)
    fields=production['fields']
    if len(fields)!=len(tasks)or {r['id']for r in fields}!=set(tasks):raise ValueError('Production fields differ from the Main input.')
    diagnostic={(r['id'],r['variant']):r for r in saved}
    for field in fields:
        if field!=diagnostic[field['id'],'FieldBC7']:raise ValueError('Field-only repacking changed its measured texture record.')
    field_rows=inspect(production['bundlePath'],production['bundleSha256'],fields)
    proof=dict(status='PASS_PERSISTENT_BC7_ALL_ENCODED_MIPS_AND_GPU_RELOAD_EXACT_NOT_RENDER_ACCEPTANCE',sourceReportSha256=sha(report_path),
               bundleSha256=report['bundleSha256'],textures=len(rows),mips=sum(r['mips']for r in rows),rows=rows,
               legacyStrictAcceptance=report['strictAcceptance'],legacyStrictAcceptanceScope='outside/protected masks; no whole-field native pixel claim',
               actorRenderAccepted=False,originalShaderDecodeProven=False,
               editorInvoked=False,nativeGameInvoked=False,pixelsDecodedOrGenerated=False)
    result=dict(status='PRODUCTION_FIELD_ONLY_BC7_PAYLOAD_AND_ALL_GPU_MIPS_EXACT',bundlePath=production['bundlePath'],bundleSha256=production['bundleSha256'],
                textures=len(field_rows),mips=sum(r['mips']for r in field_rows),rows=field_rows,noRecompression=True,diagnosticControlsShipped=False,nativeArtAccepted=False)
    destination.mkdir(parents=True,exist_ok=True);write(paths[0],proof);write(paths[1],result)
    print(json.dumps(dict(status=result['status'],textures=result['textures'],mips=result['mips'],actorRenderAccepted=False)))


def rebase_bundle_archive(environment: Any, bundle_name: str, cab: str) -> 'dict[str, Any]':
    """Keep streamed resource references valid when baking an independent part."""
    if not re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9_.-]*\.bundle', bundle_name) or not re.fullmatch(r'CAB-[a-f0-9]{32}', cab):
        raise ValueError('A bundle requires a portable filename and a CAB identity.')
    bundle = environment.file
    files = [file for file in bundle.files.values() if hasattr(file, 'objects')]
    if len(files) != 1:
        raise ValueError('Archive rebasing requires one serialized asset file.')
    file = files[0]
    previous = file.name
    if not re.fullmatch(r'CAB-[a-f0-9]{32}', previous) or previous == cab:
        raise ValueError('The source CAB identity is invalid or unchanged.')
    remapped = {name: cab + name[len(previous):] if name == previous or name.startswith(previous + '.') else name
                for name in bundle.files}
    if len(set(name.casefold() for name in remapped.values())) != len(remapped):
        raise ValueError('A rebased resource name collides with another archive member.')
    stream_changes = []
    for obj in file.objects.values():
        if obj.type.name not in ('AssetBundle', 'Mesh', 'Texture2D'):
            continue
        tree = obj.read_typetree()
        changed = False
        stream = tree.get('m_StreamData')
        if stream and stream['path'].startswith(f'archive:/{previous}/'):
            member = stream['path'].split('/')[-1]
            if stream['size'] and member not in remapped:
                raise ValueError('A streamed payload is absent from its source archive.')
            target = remapped.get(member, member)
            stream['path'] = f'archive:/{cab}/{target}'
            stream_changes.append(dict(pathId=obj.path_id, member=target, bytes=stream['size']))
            changed = True
        if obj.type.name == 'AssetBundle':
            tree['m_Name'] = bundle_name
            if 'm_AssetBundleName' in tree:
                tree['m_AssetBundleName'] = bundle_name
            changed = True
        if changed:
            obj.save_typetree(tree)
    for external in file.externals:
        if external.path.startswith(f'archive:/{previous}/'):
            member = external.path.split('/')[-1]
            external.path = f'archive:/{cab}/{remapped.get(member, member)}'
    file.name = cab
    bundle.files = {remapped[name]: value for name, value in bundle.files.items()}
    return dict(previousCab=previous, cab=cab, bundle=bundle_name, streams=stream_changes,
                members=list(bundle.files))


def resource_span_digest(data):
    return hashlib.sha256(data).hexdigest()


def resource_span_snapshot(environment):
    """Read raw member and texture authority without saving a SerializedFile."""
    from UnityPy.files.BundleFile import BundleFile
    from UnityPy.files.SerializedFile import SerializedFile
    from UnityPy.streams import EndianBinaryReader
    bundles=list(environment.files.values())
    if len(bundles)!=1 or not isinstance(bundles[0],BundleFile):
        raise ValueError('Resource span publication requires one top-level UnityFS bundle.')
    bundle=bundles[0]
    if bundle.signature!='UnityFS' or bundle.decryptor is not None:
        raise ValueError('Only an unencrypted UnityFS bundle has raw-member authority.')
    names=list(bundle.files)
    if len(set(name.casefold() for name in names))!=len(names):
        raise ValueError('Resource span member names collide case-insensitively.')
    members={}
    for name,file in bundle.files.items():
        if isinstance(file,SerializedFile):
            unexpected=sorted({obj.type.name for obj in file.objects.values()}-{'AssetBundle','Texture2D'})
            if unexpected:raise ValueError('Resource span publication only owns AssetBundle/Texture2D bundles; unsupported serialized object types: '+repr(unexpected))
            raw=file.reader.bytes
        elif isinstance(file,EndianBinaryReader):raw=file.bytes
        else:raise ValueError('An unsupported nested bundle member has no original raw authority.')
        flags=getattr(file,'flags',None)
        if type(flags)is not int: raise ValueError('A native bundle resource member has no flags.')
        members[name]={'bytes':raw,'flags':flags,'kind':type(file).__name__}
    aliases={};folded=set()
    for name,pointer in environment.container.items():
        if name.casefold() in folded:raise ValueError('Container aliases collide case-insensitively.')
        folded.add(name.casefold())
        obj=pointer.deref();key=(obj.assets_file.name,str(obj.path_id))
        aliases.setdefault(key,[]).append(name)
    textures={}
    for file in bundle.files.values():
        if not isinstance(file,SerializedFile):continue
        for obj in file.objects.values():
            if obj.type.name!='Texture2D':continue
            key=(file.name,str(obj.path_id));tree=obj.read_typetree()
            stream=tree.get('m_StreamData')
            record={'tree':tree,'aliases':sorted(aliases.get(key,[])),'member':None,'offset':None,'size':None}
            if stream and stream.get('size',0):
                member=stream['path'].replace('\\','/').split('/')[-1]
                if member not in members or not member.endswith('.resS'):
                    raise ValueError('A streamed texture does not resolve to one exact resource member.')
                offset=stream['offset'];size=stream['size']
                if type(offset)is not int or type(size)is not int or offset<0 or size<=0 or offset+size>len(members[member]['bytes']):
                    raise ValueError('A texture stream pointer is outside its original member.')
                record.update(member=member,offset=offset,size=size)
            textures[key]=record
    header={key:getattr(bundle,key)for key in ('signature','version','version_player','version_engine')}
    header.update(dataFlags=int(bundle.dataflags),blockInfoFlag=int(bundle._block_info_flags))
    return bundle,members,textures,header


def resource_span_validate(members,textures,patches):
    """Validate same-length BC7 replacements, explicit aliases, all mip hashes."""
    if not isinstance(patches,list) or not patches:raise ValueError('No typed BC7 span replacement was declared.')
    selected={};ranges={};payloads={};facts=[]
    for patch in patches:
        key=(patch['serializedFile'],str(patch['pathId']))
        if key in selected:raise ValueError('Two replacements alias the same CAB/PPtr.')
        if key not in textures:raise ValueError('The declared texture CAB/PPtr is absent.')
        record=textures[key];tree=record['tree'];aliases=patch['assetAliases']
        if not isinstance(aliases,list) or not aliases or len(set(x.casefold()for x in aliases))!=len(aliases) or sorted(aliases)!=record['aliases'] or patch['asset']not in aliases:
            raise ValueError('The replacement does not own the exact container alias set.')
        if tree['m_TextureFormat']!=25 or tree['m_IsReadable'] or tree['m_ImageCount']!=1:
            raise ValueError('Only one unreadable BC7 Texture2D image can use same-length resource replacement.')
        expected=patch['metadata'];settings=tree['m_TextureSettings']
        observed={'name':tree['m_Name'],'format':'BC7','width':tree['m_Width'],'height':tree['m_Height'],'mipCount':tree['m_MipCount'],
                  'sRGB':tree['m_ColorSpace']==1,'colorSpace':tree['m_ColorSpace'],'sampler':settings}
        if observed!=expected:raise ValueError('BC7 name/layout/sRGB/sampler authority differs.')
        member=record['member'];start=record['offset'];length=record['size']
        if member!=patch['streamMember'] or start!=patch['streamOffset'] or length!=patch['byteLength']:
            raise ValueError('The declared resource span differs from the native CAB/PPtr.')
        source=members[member]['bytes'][start:start+length]
        if resource_span_digest(source)!=patch['sourceSha256']:raise ValueError('The original BC7 span SHA256 differs.')
        payload=patch['payload'];path=Path(payload['path'])
        path=checked_catalog_path(path.parent,path.name)
        if not path.is_file() or path.stat().st_size!=length or payload['byteLength']!=length:
            raise ValueError('A BC7 replacement changes the exact original span length.')
        data=path.read_bytes()
        if resource_span_digest(data)!=payload['sha256']:raise ValueError('The replacement BC7 full SHA256 differs.')
        width=tree['m_Width'];height=tree['m_Height'];count=tree['m_MipCount'];full=1;size=max(width,height)
        while size>1:full+=1;size>>=1
        if count!=full or len(patch['mips'])!=count:raise ValueError('The replacement does not declare the complete BC7 mip chain.')
        at=0;mip_facts=[]
        for level,mip in enumerate(patch['mips']):
            w=max(1,width>>level);h=max(1,height>>level);n=((w+3)//4)*((h+3)//4)*16
            if (mip['level'],mip['width'],mip['height'],mip['offset'],mip['byteLength'])!=(level,w,h,at,n):
                raise ValueError('A BC7 replacement mip layout differs.')
            a=resource_span_digest(source[at:at+n]);b=resource_span_digest(data[at:at+n])
            if a!=mip['sourceSha256'] or b!=mip['patchedSha256']:raise ValueError('A BC7 replacement mip SHA256 differs.')
            mip_facts.append({'level':level,'offset':at,'byteLength':n,'sourceSha256':a,'patchedSha256':b});at+=n
        if at!=length:raise ValueError('The complete BC7 mip chain does not cover the original span.')
        for other_key,other in textures.items():
            if other_key!=key and other['member']==member and max(start,other['offset'])<min(start+length,other['offset']+other['size']):
                raise ValueError('The changed span overlaps another texture CAB/PPtr, including an unowned alias.')
        for other_start,other_end in ranges.setdefault(member,[]):
            if max(start,other_start)<min(start+length,other_end):raise ValueError('Declared resource replacements overlap.')
        ranges[member].append((start,start+length));selected[key]=record;payloads[key]=data
        facts.append({'serializedFile':key[0],'pathId':key[1],'assetAliases':record['aliases'],'metadata':observed,'streamMember':member,
                      'streamOffset':start,'byteLength':length,'sourceSha256':patch['sourceSha256'],'patchedSha256':payload['sha256'],'mips':mip_facts})
    return selected,payloads,ranges,facts


def patch_bc7_resource_spans(source_path,source_sha256,output_path,patches,*,verify_existing=False):
    """Replace owned BC7 resource bytes; retain every serialized CAB byte.

    The result is a CPU structural/encoded proof. It is not a fresh GPU reload.
    Native GPU evidence belongs to each pinned input codec closure.
    """
    import time
    import UnityPy
    from UnityPy.streams import EndianBinaryReader
    if UnityPy.__version__!='1.25.4':raise ValueError('The raw-member save contract requires pinned UnityPy1.25.4.')
    watch=time.perf_counter();source_path=Path(source_path);output_path=Path(output_path)
    source_path=checked_catalog_path(source_path.parent,source_path.name)
    output_path=checked_catalog_path(output_path.parent,output_path.name)
    temporary=checked_catalog_path(output_path.parent,output_path.name+'.writing')
    if source_path==output_path or temporary.exists()or output_path.exists()!=verify_existing:raise ValueError('The resource replacement output existence differs or aliases its source.')
    if not output_path.name.endswith('.bundle'):raise ValueError('Resource span output must be a bundle.')
    raw=source_path.read_bytes()
    if resource_span_digest(raw)!=source_sha256:raise ValueError('The original bundle SHA256 differs.')
    environment=UnityPy.load(raw);bundle,members,textures,header=resource_span_snapshot(environment)
    selected,payloads,ranges,facts=resource_span_validate(members,textures,patches)
    expected={name:record['bytes']for name,record in members.items()}
    for key,record in selected.items():
        member=record['member'];data=bytearray(expected[member]);start=record['offset'];data[start:start+record['size']]=payloads[key];expected[member]=bytes(data)
    # All members, including every SerializedFile/CAB, must take save_fs's raw
    # reader branch. Leaving a SerializedFile here would invoke save() and
    # reserialize its metadata, invalidating the original-CAB authority.
    if verify_existing:packed=output_path.read_bytes()
    else:
        for name,record in members.items():
            reader=EndianBinaryReader(expected[name]);setattr(reader,'flags',record['flags']);bundle.files[name]=reader
        packed=bundle.save(packer='original')
    reopened=UnityPy.load(packed);rebundle,remembers,retextures,reheader=resource_span_snapshot(reopened)
    if header!=reheader or list(members)!=list(remembers):raise ValueError('The saved bundle changed member ordering or header flags.')
    if textures!=retextures:raise ValueError('The saved bundle changed a Texture2D name/layout/sampler/container/CAB/PPtr.')
    member_proofs=[]
    for name,before in members.items():
        after=remembers[name]
        if before['flags']!=after['flags'] or len(before['bytes'])!=len(after['bytes']) or after['bytes']!=expected[name]:
            raise ValueError('The raw member byte/length/flag closure differs.')
        outside=[];at=0
        for start,end in sorted(ranges.get(name,[])):
            a=before['bytes'][at:start];b=after['bytes'][at:start]
            if a!=b:raise ValueError('An unowned resource range changed.')
            outside.append({'offset':at,'byteLength':len(a),'sha256':resource_span_digest(a)});at=end
        a=before['bytes'][at:];b=after['bytes'][at:]
        if a!=b:raise ValueError('An unowned resource tail changed.')
        outside.append({'offset':at,'byteLength':len(a),'sha256':resource_span_digest(a)})
        member_proofs.append({'name':name,'flags':before['flags'],'byteLength':len(before['bytes']),
                              'sourceSha256':resource_span_digest(before['bytes']),'patchedSha256':resource_span_digest(after['bytes']),
                              'wholeMemberExact':not ranges.get(name),'unownedRanges':outside})
    for key,record in selected.items():
        actual=remembers[record['member']]['bytes'][record['offset']:record['offset']+record['size']]
        if actual!=payloads[key]:raise ValueError('The reopened BC7 span differs from its pinned codec payload.')
    if resource_span_digest(source_path.read_bytes())!=source_sha256:raise ValueError('The source bundle changed during resource publication.')
    output_path.parent.mkdir(parents=True,exist_ok=True)
    if not verify_existing:
        try:
            with temporary.open('xb')as stream:stream.write(packed)
            if resource_span_digest(temporary.read_bytes())!=resource_span_digest(packed):raise ValueError('The written bundle differs from its validated reopened snapshot.')
            if output_path.exists():raise ValueError('The resource output appeared during publication.')
            temporary.replace(output_path)
        finally:
            if temporary.exists():temporary.unlink()
    elif resource_span_digest(output_path.read_bytes())!=resource_span_digest(packed):raise ValueError('The verified existing resource changed during inspection.')
    return {'schemaVersion':1,'status':'PASS_CPU_BC7_RESOURCE_SPAN_REPLACEMENT',
            'source':{'path':str(source_path),'sha256':source_sha256,'byteLength':len(raw)},
            'output':{'path':str(output_path),'sha256':resource_span_digest(packed),'byteLength':len(packed)},
            'unityPyVersion':UnityPy.__version__,'elapsedSeconds':time.perf_counter()-watch,'header':header,'members':member_proofs,'textures':facts,
            'allCabBytesExact':all(row['wholeMemberExact']for row in member_proofs if not row['name'].endswith('.resS')),
            'memberNamesOrderFlagsExact':True,'allUnownedResourceBytesExact':True,'allTextureMetadataAndAliasesExact':True,
            'sourceUnchanged':True,'reopenedEncodedBytesExact':True,'additionalCodecApplications':0,'imageDecoded':False,'editorInvoked':False,
            'freshGpuEvidence':False,'priorGpuEvidenceRequiredByPublisher':True,'verifiedExistingOutput':verify_existing}


def checked_resource_span_context(reference,cache):
    """Independently reopen a CPU span proof; this produces no GPU evidence."""
    import UnityPy
    proof=publication_json_proof(reference)
    if proof.get('schemaVersion')!=1 or proof.get('status')!='PASS_CPU_BC7_RESOURCE_SPAN_REPLACEMENT' or proof.get('freshGpuEvidence')is not False or proof.get('additionalCodecApplications')!=0:
        raise ValueError('No explicit CPU-only resource span publication proof.')
    required=('allCabBytesExact','memberNamesOrderFlagsExact','allUnownedResourceBytesExact','allTextureMetadataAndAliasesExact','sourceUnchanged','reopenedEncodedBytesExact')
    if any(proof.get(k)is not True for k in required):raise ValueError('A resource span proof lacks its complete CAB/unowned closure.')
    key=(proof['source']['path'],proof['source']['sha256'],proof['output']['path'],proof['output']['sha256'])
    if key in cache:return cache[key]
    snapshots=[]
    for role in('source','output'):
        record=proof[role];path=Path(record['path']);checked_catalog_path(path.parent,path.name)
        payload=path.read_bytes()
        if len(payload)!=record['byteLength']or hashlib.sha256(payload).hexdigest()!=record['sha256']:raise ValueError('A resource span bundle changed after its proof.')
        env=UnityPy.load(payload);_,members,textures,header=resource_span_snapshot(env);snapshots.append((members,textures,header))
    (before,old_textures,old_header),(after,new_textures,new_header)=snapshots
    if list(before)!=list(after)or old_header!=new_header or old_textures!=new_textures:raise ValueError('A resource span changed CAB/texture/header metadata.')
    members={r['name']:r for r in proof['members']}
    if set(members)!=set(before):raise ValueError('A resource span proof omitted a member.')
    replacements={};selected=set()
    for row in proof['textures']:
        identity=(row['serializedFile'],str(row['pathId']))
        if identity in selected or identity not in old_textures:raise ValueError('A resource span target is duplicated/missing.')
        selected.add(identity);texture=old_textures[identity]
        if texture['aliases']!=row['assetAliases']or(texture['member'],texture['offset'],texture['size'])!=(row['streamMember'],row['streamOffset'],row['byteLength']):raise ValueError('A resource span proof changed its actual target pointer.')
        member=texture['member'];start=texture['offset'];end=start+texture['size']
        if texture['tree']['m_TextureFormat']!=25:raise ValueError('Only BC7 texture spans may change.')
        for other_id,other in old_textures.items():
            if other_id!=identity and other['member']==member and max(start,other['offset'])<min(end,other['offset']+other['size']):
                raise ValueError('A changed span overlaps another texture identity.')
        if hashlib.sha256(before[member]['bytes'][start:end]).hexdigest()!=row['sourceSha256']or hashlib.sha256(after[member]['bytes'][start:end]).hexdigest()!=row['patchedSha256']:
            raise ValueError('A resource span proof has different actual compressed bytes.')
        for a,b in replacements.setdefault(member,[]):
            if max(start,a)<min(end,b):raise ValueError('Owned resource spans overlap.')
        replacements[member].append((start,end))
    for name,old in before.items():
        new=after[name];record=members[name]
        if old['flags']!=new['flags']or old['flags']!=record['flags']or len(old['bytes'])!=len(new['bytes'])or len(old['bytes'])!=record['byteLength']:
            raise ValueError('A resource span member changed size/flags.')
        if hashlib.sha256(old['bytes']).hexdigest()!=record['sourceSha256']or hashlib.sha256(new['bytes']).hexdigest()!=record['patchedSha256']:
            raise ValueError('A resource span member hash differs.')
        expected=bytearray(old['bytes'])
        for a,b in replacements.get(name,[]):expected[a:b]=new['bytes'][a:b]
        if bytes(expected)!=new['bytes']:raise ValueError('A resource span changed unowned bytes.')
        if not name.endswith('.resS')and old['bytes']!=new['bytes']:raise ValueError('A serialized CAB byte changed.')
    context=dict(proof=proof,before=before,after=after,textures=new_textures)
    cache.clear();cache[key]=context
    return context


def checked_prior_gpu_identity(reference,content):
    """Bind inferred GPU equivalence to an actual earlier saved-codec record."""
    if set(reference)!={'input','report','id','variant'}or reference['variant']!='FieldBC7':raise ValueError('No exact earlier field GPU authority.')
    inputs=publication_json_proof(reference['input']);report=publication_json_proof(reference['report'])
    if not report.get('completed')or not report.get('savedReloadAccepted')or report['inputSha256']!=reference['input']['sha256'].lower():raise ValueError('The earlier GPU epoch is incomplete or changed.')
    tasks=[r for r in inputs['tasks']if r['id']==reference['id']]
    rows=[r for r in report['saved']if r['id']==reference['id']and r['variant']==reference['variant']]
    if len(tasks)!=1 or len(rows)!=1:raise ValueError('The earlier GPU texture is not unique.')
    task=tasks[0];row=rows[0];meta=content['metadata']
    if any(task[k]!=meta[k]for k in('width','height','mipCount','sRGB'))or row['graphicsFormat']!=meta['graphicsFormat']or row['encodedSha256']!=content['compressedSha256']or row['encodedBytes']!=content['byteLength']:
        raise ValueError('Earlier GPU proof differs from the exact compressed payload/layout.')
    if not row['unreadable']or not row['reloadGpuExact']or len(row['mips'])!=len(content['mips']):raise ValueError('The earlier GPU chain is incomplete.')
    for a,b in zip(row['mips'],content['mips']):
        if(a['level'],a['encodedBytes'],a['encodedSha256'])!=(b['level'],b['byteLength'],b['compressedSha256'])or a['createdGpuSha256']!=a['reloadedGpuSha256']or a['reloadedGpuSha256']!=b['gpuSha256']:
            raise ValueError('An earlier compressed/GPU mip differs.')
    return task


def checked_span_texture_identity(value,cache):
    evidence=value['resourceSpanEvidence']
    if set(evidence)!={'container','texture','priorGpu'}or set(evidence['texture'])!={'serializedFile','pathId'}:raise ValueError('Unknown resource span texture authority.')
    context=checked_resource_span_context(evidence['container'],cache);proof=context['proof']
    if proof['output']['sha256']!=value['bundleSha256']:raise ValueError('A span identity belongs to another output bundle.')
    identity=(evidence['texture']['serializedFile'],str(evidence['texture']['pathId']))
    if identity not in context['textures']:raise ValueError('The span texture CAB/PPtr is absent.')
    row=context['textures'][identity];tree=row['tree'];content=value['content'];settings=tree['m_TextureSettings']
    meta=dict(name=tree['m_Name'],format='BC7',graphicsFormat='RGBA_BC7_'+('SRGB'if tree['m_ColorSpace']==1 else'UNorm'),width=tree['m_Width'],height=tree['m_Height'],mipCount=tree['m_MipCount'],sRGB=tree['m_ColorSpace']==1,
        filterMode=settings['m_FilterMode'],wrapModeU=settings['m_WrapU'],wrapModeV=settings['m_WrapV'],wrapModeW=settings['m_WrapW'],anisoLevel=settings['m_Aniso'],mipMapBias=settings['m_MipBias'])
    if tree['m_TextureFormat']!=25 or tree['m_IsReadable']or tree['m_ImageCount']!=1 or content['metadata']!=meta or value['asset'].casefold()not in{x.casefold()for x in row['aliases']}:
        raise ValueError('A span identity changed actual runtime name/type/sampler/layout/alias.')
    start=row['offset'];data=context['after'][row['member']]['bytes'][start:start+row['size']]
    if len(data)!=content['byteLength']or hashlib.sha256(data).hexdigest()!=content['compressedSha256']:raise ValueError('The actual reopened span differs from its texture identity.')
    at=0
    for mip in content['mips']:
        if hashlib.sha256(data[at:at+mip['byteLength']]).hexdigest()!=mip['compressedSha256']:raise ValueError('A reopened span mip differs.')
        at+=mip['byteLength']
    if at!=len(data):raise ValueError('A reopened span has undeclared bytes.')
    checked_prior_gpu_identity(evidence['priorGpu'],content)
    return context


def resource_span_codec_content(texture,payload,reference):
    """Describe actual compressed bytes using an explicitly pinned prior GPU epoch."""
    report=publication_json_proof(reference['report'])
    saved=[row for row in report['saved']if row['id']==reference['id']and row['variant']==reference['variant']]
    if len(saved)!=1:raise ValueError('The exact saved texture is not unique.')
    saved=saved[0];tree=texture['tree'];settings=tree['m_TextureSettings']
    metadata: dict[str, Any]=dict(name=tree['m_Name'],format='BC7',graphicsFormat=saved['graphicsFormat'],width=tree['m_Width'],height=tree['m_Height'],mipCount=tree['m_MipCount'],sRGB=tree['m_ColorSpace']==1,
        filterMode=settings['m_FilterMode'],wrapModeU=settings['m_WrapU'],wrapModeV=settings['m_WrapV'],wrapModeW=settings['m_WrapW'],anisoLevel=settings['m_Aniso'],mipMapBias=settings['m_MipBias'])
    if len(payload)!=saved['encodedBytes']or resource_span_digest(payload)!=saved['encodedSha256']:
        raise ValueError('Actual source/desired BC7 bytes differ from their prior codec.')
    mips=[]
    for level,row in enumerate(saved['mips']):
        if row['createdGpuSha256']!=row['reloadedGpuSha256']:raise ValueError('A prior GPU mip was not exact.')
        data=payload[row['offset']:row['offset']+row['encodedBytes']]
        if resource_span_digest(data)!=row['encodedSha256']:raise ValueError('A prior compressed mip differs.')
        mips.append(dict(level=level,width=max(1,metadata['width']>>level),height=max(1,metadata['height']>>level),byteLength=len(data),compressedSha256=row['encodedSha256'],gpuSha256=row['reloadedGpuSha256']))
    chain=resource_span_digest(json.dumps([[m['level'],m['width'],m['height'],m['gpuSha256']]for m in mips],separators=(',',':')).encode())
    content=dict(metadata=metadata,compressedSha256=saved['encodedSha256'],byteLength=len(payload),mips=mips,gpuMipHashChainSha256=chain)
    checked_prior_gpu_identity(reference,content)
    return content


def checked_span_main_codec(reference,gpu_reference,typed_key):
    closure=publication_json_proof(reference)
    if closure.get('status')!='MAIN_ONLY_SOURCE_CONTRACTS_AND_ALL_MIP_PERSISTENCE_PASS'or closure.get('nativeExpansionAndDesiredRgbaProtectionExact')is not True or closure.get('controlRelativeWhollyUnownedBlocksExact')is not True:
        raise ValueError('The Main has no complete original source/ownership codec closure.')
    if closure['batchInput']!=gpu_reference['input']or closure['result']!=gpu_reference['report']:
        raise ValueError('The Main codec and the selected prior GPU epoch differ.')
    preparation=publication_json_proof(closure['preparation'])
    rows=preparation.get('tasks',[preparation.get('task')]);rows=[r for r in rows if r and r['id']==gpu_reference['id']and r['role']=='Main']
    if len(rows)!=1:raise ValueError('The Main preparation source is not unique.')
    if typed_key[0]!='material'or typed_key[4]!='_MainTex':raise ValueError('Resource-span Main ownership requires an existing material Main selector.')
    if preparation.get('nativeHeadFingerprint',typed_key[1])!=typed_key[1]:raise ValueError('The Main source has another native Head.')
    publication_json_proof(closure['savedBundleProof']);production=publication_json_proof(closure['productionBundleProof'])
    report=publication_json_proof(gpu_reference['report']);gpu=report['production']
    if gpu.get('allMipGpuReloadExact')is not True or gpu.get('fieldOnlyTextureSet')is not True or gpu.get('noRecompression')is not True or production['bundleSha256']!=gpu['bundleSha256']:
        raise ValueError('The original Main production bundle lacks all-mip GPU identity.')
    inputs=publication_json_proof(gpu_reference['input']);task=next(r for r in inputs['tasks']if r['id']==gpu_reference['id'])
    if any(task[k]!=rows[0][k]for k in('rawSha256','nativeRawSha256','sourceEncodedSha256','width','height','mipCount','sRGB')):
        raise ValueError('The Main codec differs from the exact numerical preparation.')
    return closure


def prepare_resource_span_catalog():
    """Prepare an immutable CPU-only texture span candidate in the existing owner."""
    import time
    import UnityPy
    started=time.perf_counter()
    parser=argparse.ArgumentParser();parser.add_argument('--inputs',type=Path,required=True)
    parser.add_argument('--destination',type=Path,required=True);args=parser.parse_args()
    path=checked_catalog_path(args.inputs.parent,args.inputs.name)
    if path.stat().st_size>64*1024*1024:raise ValueError('The explicit resource-span input exceeded the 64MiB material input boundary.')
    plan=read(path)
    fields={'schemaVersion','sourceManifest','sourceMainAuthority','targets','priorGpu','holdFile','expectedHeadMainFields'}
    if set(plan)!=fields or plan['schemaVersion']!=1 or type(plan['expectedHeadMainFields'])is not int or plan['expectedHeadMainFields']<=0 or len(plan['targets'])!=plan['expectedHeadMainFields']:
        raise ValueError('No explicit complete resource-span preparation input.')
    hold=checked_catalog_path(Path(plan['holdFile']).parent,Path(plan['holdFile']).name)
    if hold.exists():raise ValueError('Resource span preparation is held before its normal bundle boundary.')
    original=publication_json_proof(plan['sourceManifest']);manifest_path=Path(plan['sourceManifest']['path']);source=checked_catalog_path(manifest_path.parent)
    destination=checked_catalog_path(args.destination)
    if destination.exists()or not destination.is_relative_to(require_workspace()):raise ValueError('Resource span output must be a new active-workspace directory.')
    selectors=typed_texture_selectors(original);payloads=catalog_payloads(original);targets={};priors={}
    source_rows=publication_json_proof(plan['sourceMainAuthority'])['rows'];source_index={}
    for row in source_rows:
        prop=row['currentPublicMain'];key=('material',row['nativeHeadFingerprint'],0,'Lux/Human/Skin',prop['name'],prop.get('sourceTextureFingerprint'),prop.get('sourceBodyTextureFingerprint'))
        if key in source_index:raise ValueError('A Head source authority has duplicated typed ownership.')
        source_index[key]=row
    for row in plan['targets']:
        if set(row)!={'key','gpu','codecClosure'}:raise ValueError('Unknown resource span target fields.')
        key=tuple(row['key'])
        if key in targets or key not in selectors or key[0]!='material'or key[4]!='_MainTex':raise ValueError('A resource span Main target is duplicated or unowned.')
        if key not in source_index:raise ValueError('A Main span is outside the declared source authority.')
        authority=source_index[key]
        if row['gpu']['id']!=authority['head']+'_L'+str(authority['lod'])+'_Main':raise ValueError('The Main codec belongs to another source Head/LOD.')
        closure=checked_span_main_codec(row['codecClosure'],row['gpu'],key);preparation=publication_json_proof(closure['preparation'])
        if preparation['sourceInputKey']!=authority['sourceInputKey']or preparation['currentPublicMain']!=authority['currentPublicMain']or(selectors[key][1],selectors[key][2])!=(authority['currentPublicMain']['bundleFile'],authority['currentPublicMain']['textureAsset']):
            raise ValueError('The Main source, exact original selector and final preparation differ.')
        targets[key]=row
    for row in plan['priorGpu']:
        if set(row)!={'key','gpu'}:raise ValueError('Unknown prior GPU source fields.')
        key=tuple(row['key'])
        if key in priors:raise ValueError('A prior GPU source is duplicated.')
        priors[key]=row['gpu']
    by_file=collections.defaultdict(list);groups=collections.defaultdict(list)
    for key,(_,file,asset)in selectors.items():
        groups[file,asset].append(list(key))
        if key in targets:by_file[file].append(key)
    changed_keys={key for key,value in selectors.items()if value[1]in by_file}
    if not changed_keys.issubset(priors):raise ValueError('A changed bundle omits prior GPU provenance for retained typed consumers.')
    destination.mkdir();reuse_snapshot=source!=PUBLIC and source.is_relative_to(require_workspace())
    snapshot=source if reuse_snapshot else destination/'SourceCatalog';catalog=destination/'CandidateCatalog'
    if not reuse_snapshot:snapshot.mkdir()
    catalog.mkdir();proofs=destination/'Proofs';proofs.mkdir()
    source_sha=plan['sourceManifest']['sha256'];candidate=copy.deepcopy(original);bundle_rows={r['file']:r for r in candidate['bundles']}
    def pin(value):return dict(path=str(Path(value).resolve()),sha256=sha(value))
    # The immutable rollback snapshot is a hardlink set: no second content copy.
    for file,digest in payloads.items():
        src=checked_catalog_path(source,file)
        if sha(src)!=digest:raise ValueError('An original payload differs before snapshot: '+file)
        if not reuse_snapshot:os.link(src,checked_catalog_path(snapshot,file))
        if file not in by_file:os.link(src,checked_catalog_path(catalog,file))
    if not reuse_snapshot:(snapshot/'CharacterModels.manifest.json').write_bytes(manifest_path.read_bytes())
    if sha(snapshot/'CharacterModels.manifest.json')!=source_sha:raise ValueError('The snapshot manifest changed.')
    updates=[];container_pins=[];watch=time.perf_counter();snapshot_seconds=watch-started;ordinal=0
    for file in sorted(by_file):
        if hold.exists():raise ValueError('Resource span preparation stopped at a closed bundle boundary; retain the partial private output.')
        src=checked_catalog_path(snapshot,file);environment=UnityPy.load(str(src));_,members,textures,_=resource_span_snapshot(environment)
        selected={};patches=[]
        for key in by_file[file]:
            asset=selectors[key][2];matches=[(identity,row)for identity,row in textures.items()if asset.casefold()in{alias.casefold()for alias in row['aliases']}]
            if len(matches)!=1:raise ValueError('A typed Main does not resolve to one exact CAB/PPtr.')
            identity,texture=matches[0]
            if identity in selected:raise ValueError('Two Head selectors would modify the same texture span.')
            selected[identity]=key;reference=targets[key]['gpu'];report=publication_json_proof(reference['report'])
            matches=[r for r in report['saved']if r['id']==reference['id']and r['variant']=='FieldBC7']
            if len(matches)!=1:raise ValueError('A desired Main codec texture is not unique.')
            saved=matches[0];raw=Path(saved['encodedPath']).read_bytes();old=members[texture['member']]['bytes'][texture['offset']:texture['offset']+texture['size']]
            old_content=resource_span_codec_content(texture,old,priors[key]);new_content=resource_span_codec_content(texture,raw,reference)
            if old_content['metadata']!=new_content['metadata']:raise ValueError('The desired Main has another exact runtime metadata layout.')
            tree=texture['tree'];at=0;mips=[]
            for a,b in zip(old_content['mips'],new_content['mips']):
                mips.append(dict(level=a['level'],width=a['width'],height=a['height'],offset=at,byteLength=a['byteLength'],sourceSha256=a['compressedSha256'],patchedSha256=b['compressedSha256']));at+=a['byteLength']
            patches.append(dict(asset=next(alias for alias in texture['aliases']if alias.casefold()==asset.casefold()),assetAliases=texture['aliases'],serializedFile=identity[0],pathId=identity[1],
                metadata=dict(name=tree['m_Name'],format='BC7',width=tree['m_Width'],height=tree['m_Height'],mipCount=tree['m_MipCount'],sRGB=tree['m_ColorSpace']==1,colorSpace=tree['m_ColorSpace'],sampler=tree['m_TextureSettings']),
                streamMember=texture['member'],streamOffset=texture['offset'],byteLength=texture['size'],sourceSha256=old_content['compressedSha256'],payload=dict(path=saved['encodedPath'],sha256=saved['encodedSha256'],byteLength=len(raw)),mips=mips))
        proof=patch_bc7_resource_spans(src,payloads[file],checked_catalog_path(catalog,file),patches)
        proof_path=proofs/(file+'.span.json');write(proof_path,proof);container_ref=pin(proof_path);container_pins.append(container_ref);bundle_rows[file]['sha256']=proof['output']['sha256']
        # All typed references to a modified bundle change SHA, including every
        # retained Normal and Dragon Main. Their texture content stays exact.
        for key,(_,consumer_file,asset)in selectors.items():
            if consumer_file!=file:continue
            matches=[(identity,row)for identity,row in textures.items()if asset.casefold()in{alias.casefold()for alias in row['aliases']}]
            if len(matches)!=1:raise ValueError('A retained typed consumer is not unique in its actual CAB.')
            identity,texture=matches[0];before_data=members[texture['member']]['bytes'][texture['offset']:texture['offset']+texture['size']]
            prior=priors[key];old_content=resource_span_codec_content(texture,before_data,prior);gpu=prior;policy='retained';new_content=old_content
            if key in targets:
                gpu=targets[key]['gpu'];report=publication_json_proof(gpu['report']);saved=next(r for r in report['saved']if r['id']==gpu['id']and r['variant']=='FieldBC7')
                new_content=resource_span_codec_content(texture,Path(saved['encodedPath']).read_bytes(),gpu);policy='changed-main'
            before=dict(schemaVersion=1,status='PASS_ENCODED_TEXTURE_PAYLOAD_IDENTITY',bundleFile=file,asset=asset,bundleSha256=payloads[file],typedKeys=groups[file,asset],content=old_content,
                checks=dict(sourceBundleUnchanged=True,encodedSaveReloadExact=True,gpuSaveReloadExact=True),gpuEvidenceScope='Pinned prior actual saved/reload GPU epoch; no new sampling.')
            after=dict(schemaVersion=1,status='PASS_ENCODED_TEXTURE_PAYLOAD_IDENTITY',bundleFile=file,asset=asset,bundleSha256=proof['output']['sha256'],typedKeys=groups[file,asset],content=new_content,
                checks=dict(sourceBundleUnchanged=True,encodedSaveReopenExact=True,serializedMetadataExact=True,unownedBytesExact=True,freshGpuReadback=False,gpuSaveReloadExact=False),
                evidenceKind='RESOURCE_SPAN_PRIOR_GPU_EQUIVALENCE',resourceSpanEvidence=dict(container=container_ref,texture=dict(serializedFile=identity[0],pathId=identity[1]),priorGpu=gpu))
            if policy=='changed-main':after.update(codecApplications=1,ownedTypedKeys=after['typedKeys'],codecClosure=targets[key]['codecClosure'])
            a=proofs/('%04d-before.json'%ordinal);b=proofs/('%04d-after.json'%ordinal);ordinal+=1;write(a,before);write(b,after)
            updates.append(dict(key=list(key),policy=policy,before=dict(bundleFile=file,asset=asset,proof=pin(a)),after=dict(bundleFile=file,asset=asset,proof=pin(b))))
        print(json.dumps(dict(stage='RESOURCE_SPAN_BUNDLE_CLOSED',file=file,changedMain=len(patches),typedConsumers=sum(1 for k,v in selectors.items()if v[1]==file),elapsedSeconds=time.perf_counter()-watch)),flush=True)
    manifest=catalog/'CharacterModels.manifest.json';write(manifest,candidate)
    closure=dict(schemaVersion=1,status='PASS_TYPED_TEXTURE_PAYLOAD_CLOSURE',resourceSpanPublication=True,sourceManifestSha256=source_sha,candidateManifestSha256=sha(manifest),updates=updates)
    typed_path=destination/'TexturePayloadClosure.json';write(typed_path,closure)
    checked=typed_texture_payload_closure(original,candidate,closure,source_sha,sha(manifest))
    result=dict(schemaVersion=1,status='PRIVATE_HEAD_MAIN_RESOURCE_SPAN_CATALOG_CLOSED_BODY_OVERLAY_PENDING',input=pin(path),sourceCatalog=str(snapshot),catalog=str(catalog),manifestSha256=sha(manifest),
        texturePayloadClosure=pin(typed_path),changedMainFields=len(targets),changedBundles=len(by_file),allChangedBundleTypedReferences=len(updates),retainedTypedReferences=sum(r['policy']=='retained'for r in updates),
        containerProofs=container_pins,allCabAndUnownedBytesExact=True,inverseTypedTextureClosureExact=checked['inverseTypedTextureClosureExact'],additionalCodecApplications=0,
        priorCodecGpuEvidenceOnly=True,freshGpuReadback=False,renderedArtAccepted=False,body13OverlayComplete=False,completeFinalBodyCatalog=False,
        sourceSnapshotReused=reuse_snapshot,snapshotValidationSeconds=snapshot_seconds,spanAndTypedClosureSeconds=time.perf_counter()-watch,totalSeconds=time.perf_counter()-started)
    write(destination/'Ready.json',result);print(json.dumps(result))


def generate_extremities():
    from collect_sources import validate_packet
    parser = argparse.ArgumentParser()
    parser.add_argument('--inputs', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    input_path = args.inputs.resolve()
    document = read(input_path)
    destination = checked_catalog_path(args.output)
    if not destination.is_relative_to(require_workspace()) or destination == HERE or destination.exists():
        raise ValueError('Use a new extremity output directory under the workspace.')
    declared, ids = [], set()
    for row in document['rows']:
        identity = row['id']
        if asset_names.source_label(identity) != identity or identity.casefold() in ids or row['part'] not in ('Hands', 'Feet'):
            raise ValueError('An extremity requires a unique source name and Hands or Feet ownership.')
        ids.add(identity.casefold())
        reference = dict(row['source'])
        path = Path(reference['path'])
        reference['path'] = str((input_path.parent / path).resolve() if not path.is_absolute() else path)
        declared.append((identity, row, reference))
    if not declared:
        raise ValueError('At least one extremity source is required.')
    destination.mkdir()
    rows = []
    for identity, row, reference in declared:
        packet = material_packet(reference)
        if fingerprint(packet)[0] != row['sourceFingerprint']:
            raise ValueError('The extremity source fingerprint differs from its declaration.')
        candidate, added, metrics = p.refine_extremity_packet(packet, row['rendererToPrefab'], row['lod'])
        validate_packet(candidate)
        target = destination / (identity + '.json.gz')
        payload = (json.dumps(candidate, separators=(',', ':'), allow_nan=False) + '\n').encode('utf-8')
        target.write_bytes(gzip.compress(payload, mtime=0))
        mapping = destination / (identity + '.added.json')
        write(mapping, added)
        rows.append(dict(id=identity, part=row['part'], lod=row['lod'], source=reference,
                         sourceFingerprint=row['sourceFingerprint'], authoredFingerprint=candidate['sourceFingerprint'],
                         packet=dict(path=str(target), sha256=sha(target)), addedVertices=dict(path=str(mapping), sha256=sha(mapping)), metrics=metrics))
    write(destination / 'Generation.json', dict(schemaVersion=1, status='NUMERIC_EXTREMITY_GENERATION_COMPLETE',
          input=dict(path=str(input_path), sha256=sha(input_path)), profile=p.extremity_refinement_profile(), rows=rows))


def generate_skin_boundaries():
    parser=argparse.ArgumentParser()
    parser.add_argument('--inputs',type=Path,required=True)
    parser.add_argument('--destination',type=Path,required=True)
    parser.add_argument('--material-catalog',type=Path)
    args=parser.parse_args()
    input_path=args.inputs.resolve();document=read(input_path)
    material_authority=None;authored_normals=set();authored_meshes={}
    if args.material_catalog:
        catalog_path=args.material_catalog.resolve();catalog=read(catalog_path)
        if catalog.get('schemaVersion')!=2:raise ValueError('Skin authoring needs the retained mesh/material source catalog.')
        material_authority=dict(path=str(catalog_path),sha256=sha(catalog_path))
        authored_meshes={row['sourceFingerprint']:row for row in catalog['meshes']if row['mode']=='authored'}
        authored_normals={row['sourceFingerprint']for row in catalog['materials']if any(p['name']=='_BumpMap'and p['kind']in('texture','texture-remap')for p in row['properties'])}
    destination=checked_catalog_path(args.destination)
    if not destination.is_relative_to(require_workspace()) or destination.exists():
        raise ValueError('Use a new skin-boundary output directory under the workspace.')
    sys.path.insert(0,str(ROOT/'tools/asset-authoring/UnityAssetBuild'))
    from mesh_recipe_codec import encode_recipe
    selected=[row for row in document['rows']if row['lod']==0]
    if not selected:raise ValueError('No declared native skin cut was selected.')
    identities=[row['internalId'].casefold()for row in selected]
    if len(identities)!=len(set(identities)):raise ValueError('The native skin cut source is ambiguous.')
    destination.mkdir(parents=True);rows=[];meshes=[]
    for row in selected:
        receiver=material_packet(row['headCanonical']);donor=material_packet(row['bodyCanonical'])
        cuts=row['headSource']['correspondence']['sourceCorrespondences']
        pairs=[(int(vertex),min(cut['bodyVertices']))for cut in cuts for vertex in cut['headVertices']]
        tolerance=max(1e-4,max(cut['distanceMetres']for cut in cuts)+1e-5)
        shading_owner='donor'
        if row['nativeHeadFingerprint']in authored_normals:
            declaration=authored_meshes.get(row['nativeHeadFingerprint'])
            if declaration is None or declaration['authoredFingerprint']!=fingerprint(receiver)[0]:
                raise ValueError('The authored normal atlas and its receiver mesh do not share a rest shading frame.')
            shading_owner='receiver'
        candidate,proof=art.conform_skin_boundary(receiver,donor,row['headNativeBonePaths'],row['bodyNativeBonePaths'],pairs,maximum_source_gap=tolerance,shading_frame_owner=shading_owner)
        candidate['sourceFingerprint']=fingerprint(candidate)[0]
        name=asset_names.source_label(row['head'])+'_LOD0'
        target=destination/(name+'.json.gz')
        target.write_bytes(gzip.compress((json.dumps(candidate,separators=(',',':'),allow_nan=False)+'\n').encode('utf-8'),mtime=0))
        mesh=recipe_mesh(candidate);mesh['name']=name;meshes.append(mesh)
        rows.append(dict(asset=row['internalId'],nativeFingerprint=row['nativeHeadFingerprint'],input=row['headCanonical'],donor=row['bodyCanonical'],
            packet=dict(path=str(target),sha256=sha(target)),mesh=name,authoredFingerprint=candidate['sourceFingerprint'],vertexPairs=pairs,sourceGapBudget=tolerance,**proof))
    recipes=[]
    for first in range(0,len(meshes),100):
        group=first//100+1;target=destination/('Group'+str(group)+'/Recipe.json')
        encode_recipe(dict(schemaVersion=1,bundleName='completed_skin_boundary_'+str(group)+'.bundle',meshes=meshes[first:first+100]),target)
        recipes.append(str(target))
    result=dict(status='PREPARED_SHARED_NATIVE_BOUNDARY',input=dict(path=str(input_path),sha256=sha(input_path)),
        source=dict(path=str(ROOT/'tools/blender-models/CharacterFaces/generate.py'),sha256=sha(ROOT/'tools/blender-models/CharacterFaces/generate.py')),
        rows=rows,recipes=recipes,materialCatalog=material_authority,scope='Declared LOD0 physical skin cuts, preserving each material family and its native anatomy.')
    write(destination/'Preparation.json',result)
    print(json.dumps(dict(status=result['status'],meshes=len(rows),recipes=recipes)))


def assemble_resources():
    parser=argparse.ArgumentParser()
    parser.add_argument('--prepared-parts',type=Path,required=True)
    parser.add_argument('--native-index',type=Path,required=True)
    parser.add_argument('--native-catalog',type=Path,required=True)
    parser.add_argument('--material-definitions',type=Path)
    parser.add_argument('--output',type=Path,required=True)
    args=parser.parse_args()
    output=checked_catalog_path(args.output)
    if output==PUBLIC.resolve():raise ValueError('Assemble a candidate catalog before publishing it.')
    sys.path.insert(0,str(ROOT/'tools/asset-authoring/UnityAssetBuild'))
    from character_resources import assemble_character_resources
    names=[]if args.material_definitions is None else sorted({row['material']for row in read(args.material_definitions)if row['material']})
    result=assemble_character_resources(args.prepared_parts,args.native_index,args.native_catalog,PUBLIC,output,rebase_bundle_archive,names)
    write(require_workspace()/'ResourceAssembly.json',result)
    print(json.dumps({key:result[key]for key in ('status','resources','bundles','bytes')}))




def create_resource_packs(manifest_path,output_directory,release_version,maximum_bytes=256*1024*1024) -> dict[str, Any]:
    """Stream independent generated resources into bounded release archives."""
    import zipfile
    if not re.fullmatch(r'\d{4}\.\d{2}\.\d{2}\.[1-9]\d*',release_version)or not 0<maximum_bytes<=1024*1024*1024:
        raise ValueError('A dated content version and a positive pack limit are required.')
    manifest_path=Path(manifest_path).resolve();output=checked_catalog_path(output_directory)
    manifest=read(manifest_path)
    if manifest['schemaVersion']!=3 or manifest['meshes']or manifest['materials']or manifest['textureRemaps']:
        raise ValueError('Resource packs require a standalone character catalog.')
    files=[];names=set()
    for row in sorted(manifest['bundles'],key=lambda row:row['file']):
        path=checked_catalog_path(manifest_path.parent,row['file']);name=path.name
        if name in names or not re.fullmatch('[a-f0-9]{64}',row['sha256']):raise ValueError('Invalid resource pack declaration.')
        size=path.stat().st_size
        if not 0<size<1024*1024*1024:raise ValueError('A resource exceeds the individual pack limit: '+name)
        files.append(dict(file=name,sha256=row['sha256'],bytes=size));names.add(name)
    if not files:raise ValueError('An empty resource catalog cannot be packaged.')
    owner=dict(schemaVersion=1,kind='CharacterResourcePacks',source=str(manifest_path),
        sourceSha256=sha(manifest_path),version=release_version,maximumBytes=maximum_bytes)
    output.mkdir(parents=True,exist_ok=True);marker=output/'PackageInputs.json';metadata=output/'CharacterModels.packs.json'
    if marker.exists():
        if read(marker)!=owner:raise ValueError('The pack directory belongs to other inputs.')
        if metadata.exists():
            prior=read(metadata)
            if prior['files']!=files:raise ValueError('Resource lengths changed after packaging.')
            for pack in prior['packs']:
                if (output/pack['file']).stat().st_size>maximum_bytes or sha(output/pack['file'])!=pack['sha256']:
                    raise ValueError('A completed resource pack changed.')
            return prior
    elif any(output.iterdir()):raise ValueError('Resource packs need an empty or matching owned directory.')
    else:write(marker,owner)
    # Seekable ZIP_STORED output uses a 30-byte local header, a 20-byte
    # ZIP64 local extra, a 46-byte central header and both UTF-8 filenames.
    # Every group also owns the 22-byte end-of-central-directory record.
    groups=[];group=[];size=22
    for row in files:
        packed_size=row['bytes']+96+2*len(row['file'].encode('utf8'))
        if packed_size+22>maximum_bytes:raise ValueError('A resource exceeds the archive size limit: '+row['file'])
        if group and size+packed_size>maximum_bytes:groups.append(group);group=[];size=22
        group.append(row);size+=packed_size
    if group:groups.append(group)
    if len(groups)>99:raise ValueError('The content set exceeds its supported pack count.')
    packs=[]
    for ordinal,group in enumerate(groups,1):
        name=f'CharacterModels-{ordinal:02}.zip';path=output/name;temporary=output/(name+'.writing')
        try:
            with zipfile.ZipFile(temporary,'w',compression=zipfile.ZIP_STORED,allowZip64=True)as archive:
                for row in group:
                    info=zipfile.ZipInfo(row['file'],(2020,1,1,0,0,0));info.create_system=0
                    hasher=hashlib.sha256()
                    with (manifest_path.parent/row['file']).open('rb')as source,archive.open(info,'w',force_zip64=True)as destination:
                        while block:=source.read(1024*1024):hasher.update(block);destination.write(block)
                    if hasher.hexdigest()!=row['sha256']:raise ValueError('A resource changed during packaging: '+row['file'])
            with zipfile.ZipFile(temporary)as archive:
                for row in group:
                    with archive.open(row['file'])as source:
                        verified=hashlib.sha256()
                        while block:=source.read(1024*1024):verified.update(block)
                        if verified.hexdigest()!=row['sha256']:raise ValueError('A saved resource pack differs.')
            if temporary.stat().st_size>maximum_bytes:raise ValueError('The saved resource archive exceeds its size limit.')
            temporary.replace(path)
            packs.append(dict(file=name,sha256=sha(path),bytes=path.stat().st_size,files=[row['file']for row in group]))
        finally:
            if temporary.exists():temporary.unlink()
        print('Packaged character resources:',ordinal,'/',len(groups),flush=True)
    if sha(manifest_path)!=owner['sourceSha256']:raise ValueError('The resource catalog changed during packaging.')
    result=dict(schemaVersion=1,version=release_version,catalogSha256=owner['sourceSha256'],
        baseUrl=f'https://github.com/KCN-2D/SolastaUnfinishedBusiness/releases/download/character-models-{release_version}/',files=files,packs=packs)
    write(metadata,result)
    return result


def package_resources():
    parser=argparse.ArgumentParser();parser.add_argument('--manifest',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True);parser.add_argument('--release-version',required=True)
    args=parser.parse_args();result=create_resource_packs(args.manifest,args.output,args.release_version)
    print(json.dumps(dict(status='PASS_RESOURCE_PACKS',packs=len(result['packs']),bytes=sum(row['bytes']for row in result['packs']))))


def resource_tone_context(contexts,record):
    selector=record['sourceContext']
    keys=('nativeHeadFingerprint','nativeBodyFingerprint','lod','samplerSourceInputKey')
    rows=[row for row in contexts['rows']if all(row[key]==selector[key]for key in keys)]
    if len(rows)!=1:raise ValueError('A Main field has no unique native face context.')
    row=rows[0];encoded=row['sampler']['textureInputs']['headMain']['encodedSha256']
    family=[other for other in contexts['rows']if other['sampler']['textureInputs']['headMain']['encodedSha256']==encoded]
    return row,family


def prepare_resource_tone_coefficient(job) -> dict[str, Any]:
    import numpy as np
    import material_fields as fields
    record=read(job['preparation']);task=record['task'];raw_path=Path(task['nativeRaw'])
    if sha(raw_path)!=task['nativeRawSha256']:raise ValueError('A native Main reference changed.')
    old=record['sourceInput']['coefficient']
    if sha(old['path'])!=old['sha256']:raise ValueError('A native Body calibration changed.')
    inputs=[dict(path=str(path),sha256=sha(path))for path in (job['preparation'],job['contexts'],raw_path,old['path'],fields.__file__)]
    if Path(job['output']).exists():
        cached=read(job['output'])
        if cached.get('status')=='PREPARED_SOURCE_CALIBRATION'and cached.get('inputs')==inputs and cached['coefficient'].get('policy')=='surface-local-v4':
            return dict(head=record['head'],path=str(job['output']),sha256=sha(job['output']),reused=True)
    contexts=read(job['contexts']);row,family=resource_tone_context(contexts,record)
    packets=material_main_inputs(row,family)
    head=fields.source_context(row,packets[row['headCanonical']['path']],packets[row['bodyCanonical']['path']])[0]
    raw=np.fromfile(raw_path,np.uint8,count=task['width']*task['height']*4).reshape(task['height'],task['width'],4)
    coefficient=fields.surface_tone_references(head,fields.linear_rgba(raw),read(old['path'])['bodyTarget'])
    result=dict(status='PREPARED_SOURCE_CALIBRATION',head=record['head'],coefficient=coefficient,inputs=inputs)
    write(job['output'],result)
    return dict(head=record['head'],path=str(job['output']),sha256=sha(job['output']))


def retone_resource_bundle(job) -> dict[str, Any]:
    import inspect
    import numpy as np
    import material_fields as fields
    import character_resources as resources
    from PIL import Image
    from UnityPy.helpers.ResourceReader import get_resource_data
    from UnityPy.streams import EndianBinaryReader
    target=Path(job['catalog'])/job['file'];before=sha(target)
    references={str(Path(job['contexts']).resolve()),str(Path(fields.__file__).resolve()),str(Path(resources.__file__).resolve())}
    for item in job['textures']:
        record=read(item['preparation'])
        references.update(str(Path(path).resolve())for path in (item['preparation'],item['coefficient'],record['task']['nativeRaw'],
            Path(job['authoringSources'])/Path(record['sourceInput']['source']['path']).name))
    signature=hashlib.sha256('\n'.join(inspect.getsource(function)for function in
        (retone_resource_bundle,resource_tone_context,material_packet,material_main_inputs)).encode('utf8')).hexdigest()
    authority: dict[str,Any]=dict(sourceSha256=job['expectedSha256'],codeSignature=signature,inputs=[dict(path=path,sha256=sha(path))for path in sorted(references)])
    if Path(job['receipt']).exists():
        cached=read(job['receipt'])
        if cached.get('authority')==authority and cached.get('sha256')==before:return cached
    if before!=job['expectedSha256']:
        original=Path(job['geometryCatalog'])/job['file']
        if sha(original)!=job['expectedSha256']:raise ValueError('A saved geometry source changed.')
        temporary=target.with_name(target.name+'.restoring');temporary.hardlink_to(original);temporary.replace(target);before=sha(target)
    if before!=job['expectedSha256']:raise ValueError('A Main resource changed before correction.')
    env,file=resources.load_serialized_bundle(target,in_memory=True)
    object_bytes={identifier:resources.serialized_object_bytes(obj)for identifier,obj in file.objects.items()}
    streams={};original_streams={};proofs=[];storage_changes=[];contexts=read(job['contexts'])
    source_cache={}
    for item in job['textures']:
        record=read(item['preparation']);task=record['task'];raw_path=Path(task['nativeRaw'])
        if sha(raw_path)!=task['nativeRawSha256']:raise ValueError('A Main field source changed.')
        source=record['sourceInput']['source'];source_path=Path(job['authoringSources'])/Path(source['path']).name
        if source_path not in source_cache:
            if sha(source_path)!=source['sha256']:raise ValueError('An encoded Main authority changed.')
            source_cache[source_path]=resources.load_serialized_bundle(source_path,in_memory=True)
        source_env,_=source_cache[source_path]
        original=resources.typed_bundle_asset(source_env,source['asset'],'Texture2D').deref()
        data=original.read();source_tree=original.read_typetree();encoded=bytes(data.image_data)
        if not encoded:
            stream=source_tree['m_StreamData'];encoded=get_resource_data(stream['path'],original.assets_file,stream['offset'],stream['size'])
        if hashlib.sha256(encoded).hexdigest()!=source['encodedSha256']or source_tree['m_TextureFormat']!=25:
            raise ValueError('An encoded pre-calibration Main field differs.')
        row,family=resource_tone_context(contexts,record);packets=material_main_inputs(row,family)
        head=fields.source_context(row,packets[row['headCanonical']['path']],packets[row['bodyCanonical']['path']])[0]
        identity=sha(fields.__file__)
        buffers=fields.domain_buffers(row,packets[row['headCanonical']['path']],packets[row['bodyCanonical']['path']],family,packets,dict(fieldSha256=identity,domainSha256=identity))
        coefficient=read(item['coefficient'])['coefficient'];weight=buffers['weight']
        obj=file.objects[item['identifier']];tree=obj.read_typetree()
        if tree['m_TextureFormat']!=25:raise ValueError('A calibrated Main resource must retain BC7.')
        stream=tree['m_StreamData'];member=stream['path'].replace('\\','/').rsplit('/',1)[-1]
        if member not in streams:
            reader=env.file.files[member];original_streams[member]=bytes(reader.bytes if isinstance(reader,EndianBinaryReader)else reader)
            streams[member]=bytearray(original_streams[member])
        old=bytes(streams[member][stream['offset']:stream['offset']+stream['size']])
        expected=[];source_mips=[];exact_mips=[];start=0;raw=raw_path.read_bytes();mip_proofs=[];tone_memo=fields.RasterMemo();needs_exact=False
        for level,mip in enumerate(task['mips']):
            width,height=mip['width'],mip['height'];size=((width+3)//4)*((height+3)//4)*16
            if level:weight=fields.downsample(weight).max(axis=(1,3))
            payload=encoded[start:start+size];start+=size
            if width>tree['m_Width']or height>tree['m_Height']:continue
            atlas,atlas_proof=fields.surface_tone_atlas(head,coefficient,width,height,memo=tone_memo,padding_texels=4.5)
            pixels=np.frombuffer(raw[mip['offset']:mip['offset']+mip['byteLength']],np.uint8).reshape(height,width,4)
            original_pixels=np.asarray(Image.frombytes('RGBA',(width,height),payload,'bcn',7))
            corrected,tone_proof=fields.surface_tone_main_mip(pixels,weight,coefficient,atlas,c1_source=original_pixels)
            editable=np.isfinite(atlas[::-1,:,0])&(weight[::-1]<1)
            replacement,codec_proof=fields.fit_bc7_rgb_field(payload,width,height,corrected,editable,maximum_rgb_error=16 if len(expected)<3 else None)
            _,owned_pixels=fields.whole_block_pixels(editable)
            exact=corrected.copy();exact[:,:,3]=original_pixels[:,:,3];exact[~owned_pixels]=original_pixels[~owned_pixels];exact_mips.append(exact.tobytes())
            source_mips.append(payload)
            if len(expected)<3 and codec_proof['maximumRgbError']>16:
                needs_exact=True
            expected.append(replacement);mip_proofs.append(dict(level=level,width=width,height=height,tone=tone_proof,codec=codec_proof,atlas=atlas_proof))
        if start!=len(encoded)or len(expected)!=tree['m_MipCount']:raise ValueError('A Main correction omitted a declared native mip.')
        texels=sum(mip['width']*mip['height']for mip in mip_proofs)
        rgb_error=math.sqrt(sum(mip['codec']['rootMeanSquareRgbError']**2*mip['width']*mip['height']for mip in mip_proofs)/texels)
        needs_exact|=rgb_error>2
        replacement=b''.join(expected);pre_calibration=b''.join(source_mips);legacy_alpha=[]
        if len(replacement)!=stream['size']:raise ValueError('A Main correction changed its resource span.')
        if needs_exact:
            replacement=b''.join(exact_mips)
            if len(replacement)>EXACT_MAIN_TEXTURE_MAX_BYTES:raise ValueError('An exact Main mip chain exceeds its bounded storage budget.')
            for mip in mip_proofs:
                mip['bc7Attempt']=mip['codec'];mip['codec']=dict(storage='RGBA32',alphaExact=True,maximumRgbError=0,rootMeanSquareRgbError=0.,sourceOwnedBlocksExact=True)
            rgb_error=0.
        for level in range(tree['m_MipCount']):
            width=max(1,tree['m_Width']>>level);height=max(1,tree['m_Height']>>level);size=((width+3)//4)*((height+3)//4)*16
            at=sum(((max(1,tree['m_Width']>>i)+3)//4)*((max(1,tree['m_Height']>>i)+3)//4)*16 for i in range(level))
            a=np.asarray(Image.frombytes('RGBA',(width,height),pre_calibration[at:at+size],'bcn',7))
            legacy=np.asarray(Image.frombytes('RGBA',(width,height),old[at:at+size],'bcn',7))
            difference=legacy[:,:,3].astype(float)-a[:,:,3]
            legacy_alpha.append(dict(level=level,maximumError=int(np.abs(difference).max()),rootMeanSquareError=float(np.sqrt(np.mean(difference**2)))))
            b=np.frombuffer(exact_mips[level],np.uint8).reshape(height,width,4)if needs_exact else np.asarray(Image.frombytes('RGBA',(width,height),replacement[at:at+size],'bcn',7))
            if not np.array_equal(a[:,:,3],b[:,:,3]):raise ValueError('A Main correction changed its pre-calibration source alpha.')
        if needs_exact:
            streams[member].extend(bytes((-len(streams[member]))%16));offset=len(streams[member]);streams[member].extend(replacement)
            changed=resources.texture_rgba32_storage(tree,replacement,offset);obj.save_typetree(changed)
            storage_changes.append(dict(object=item['identifier'],before={key:tree[key]for key in ('m_TextureFormat','m_CompleteImageSize','m_StreamData')},
                after={key:changed[key]for key in ('m_TextureFormat','m_CompleteImageSize','m_StreamData')},additionalBytes=len(replacement),exactRgbaMipChain=True))
        else:streams[member][stream['offset']:stream['offset']+stream['size']]=replacement
        proofs.append(dict(texture=item['name'],object=item['identifier'],mips=mip_proofs,allMipTexelWeightedRgbRms=rgb_error,
            preparation=dict(path=item['preparation'],sha256=sha(item['preparation'])),coefficient=dict(path=item['coefficient'],sha256=sha(item['coefficient'])),
            nativeRgba=dict(path=str(raw_path),sha256=sha(raw_path)),encodedSource=dict(path=str(source_path),sha256=sha(source_path)),
            beforePayloadSha256=hashlib.sha256(old).hexdigest(),afterPayloadSha256=hashlib.sha256(replacement).hexdigest(),preCalibrationAlphaAllMipsExact=True,legacyAlphaDrift=legacy_alpha))
    for member,value in streams.items():
        reader=EndianBinaryReader(bytes(value));setattr(reader,'flags',0);env.file.files[member]=reader
    changed_ids={row['object']for row in storage_changes}
    if any(resources.serialized_object_bytes(obj)!=object_bytes[identifier]for identifier,obj in file.objects.items()if identifier not in changed_ids):
        raise ValueError('Exact Main storage changed another serialized object.')
    expected_objects={identifier:resources.serialized_object_bytes(obj)for identifier,obj in file.objects.items()}
    pending=target.with_name(target.name+'.writing');pending.write_bytes(env.file.save(packer='lz4'))
    reopened,saved=resources.load_serialized_bundle(pending,in_memory=True)
    if any(resources.serialized_object_bytes(obj)!=expected_objects[identifier]for identifier,obj in saved.objects.items()):
        raise ValueError('Main finishing changed serialized object fields.')
    for member,value in streams.items():
        reader=reopened.file.files[member];actual=reader.bytes if isinstance(reader,EndianBinaryReader)else reader
        if bytes(actual)!=bytes(value):raise ValueError('A Main resource span failed its saved reload.')
    pending.replace(target)
    if any(sha(pin['path'])!=pin['sha256']for pin in authority['inputs']):raise ValueError('A Main calibration input changed during encoding.')
    result=dict(kind='source-calibrated-main',file=job['file'],beforeSha256=before,sha256=sha(target),textures=proofs,authority=authority,
        objectFieldsExact=not storage_changes,ownedTextureStorageChanges=storage_changes,objectFieldChangesLimitedToTextureStorage=True,unownedResourceSpansExact=True,savedResourceBytesExact=True)
    write(job['receipt'],result)
    return result


def retone_resource_fields(catalog,textures,material_contexts,tone_inputs,authoring_sources,output,geometry_catalog=None,workers=3) -> list[dict[str, Any]]:
    from concurrent.futures import ProcessPoolExecutor
    if not isinstance(workers,int)or not 1<=workers<=16:raise ValueError('Main finishing requires between1 and16 workers.')
    output.mkdir(parents=True,exist_ok=True);tasks={};heads={}
    for path in sorted(Path(tone_inputs).glob('*/Codec/Input.json')):
        for task in read(path)['tasks']:
            preparation=Path(task['raw']).parent/'Preparation.json'
            record=read(preparation)
            if 'sourceContext'not in record:continue
            tasks[task['name']+'_FieldBC7']=str(preparation)
            if record['lod']==0:
                head=record['head'];asset_names.source_label(head)
                if Path(head).name!=head:raise ValueError('A calibration name is not a source label.')
                heads[head]=dict(preparation=str(preparation),contexts=str(material_contexts),output=output/'Calibrations'/(head+'.json'))
    requested={};retained=[]
    for (file,identifier),texture in textures.items():
        name=texture['name']
        if name not in tasks:
            if texture['styled']:raise ValueError('A human-style Main field has no immutable pre-calibration source: '+name)
            retained.append(dict(file=file,identifier=identifier,name=name,owners=texture['owners'],reason='native-scaled-or-creature-palette'))
            continue
        preparation=tasks[name];record=read(preparation);head=record['head']
        if head not in heads:raise ValueError('A lower face Main field has no declared LOD0 calibration.')
        requested[head]=heads[head]
    write(output/'Scope.json',dict(calibratedHeads=sorted(requested),retainedNativeFields=retained))
    print('Calibrating native face pigments:',len(requested),flush=True)
    with ProcessPoolExecutor(max_workers=workers,max_tasks_per_child=8)as pool:
        for result in pool.map(prepare_resource_tone_coefficient,requested.values()):print('Calibrated Main:',result['head'],flush=True)
    groups={}
    for (file,identifier),texture in textures.items():
        name=texture['name']
        if name not in tasks:continue
        preparation=tasks[name];head=read(preparation)['head']
        groups.setdefault(file,[]).append(dict(identifier=identifier,name=name,preparation=preparation,coefficient=str(heads[head]['output'])))
    geometry_catalog=Path(catalog).parent/'GeometryCatalog'if geometry_catalog is None else Path(geometry_catalog)
    jobs=[dict(file=file,textures=rows,catalog=str(catalog),geometryCatalog=str(geometry_catalog),expectedSha256=sha(geometry_catalog/file),contexts=str(material_contexts),authoringSources=str(authoring_sources),
        receipt=str(output/'Receipts'/(file+'.json')))for file,rows in sorted(groups.items())]
    result=[]
    with ProcessPoolExecutor(max_workers=workers,max_tasks_per_child=8)as pool:
        for proof in pool.map(retone_resource_bundle,jobs):
            result.append(proof);print('Corrected Main resources:',proof['file'],len(proof['textures']),flush=True)
    if sum(change['additionalBytes']for proof in result for change in proof['ownedTextureStorageChanges'])>EXACT_MAIN_CATALOG_MAX_BYTES:
        raise ValueError('Exact Main storage exceeds its catalog-wide budget.')
    return result


def finish_resource_skin_cuts(catalog,assembly,material_contexts,output):
    """Close raster cracks on the shared, already authored native skin cuts."""
    from functools import lru_cache
    import numpy as np
    import character_resources as resources
    shape,geometry=source_owner()
    catalog=Path(catalog);output=Path(output);output.mkdir(parents=True,exist_ok=True)
    manifest=read(catalog/'CharacterModels.manifest.json')
    proofs={row['file']:row for row in assembly['proofs']}
    current_cabs={row['newCab']:row['file']for row in assembly['proofs']}
    resources_by_source={row['asset']:row for row in manifest['resources']}
    producers=output/'Producers';producers.mkdir(exist_ok=True)
    inputs=[]
    for source in (Path(__file__),ROOT/'tools/blender-models/CharacterFaces/generate.py',ROOT/'tools/blender-models/CharacterFaces/production.py'):
        retained=producers/(source.name+'.source')
        if retained.exists()and sha(retained)!=sha(source):raise ValueError('A skin-cut producer changed during preparation.')
        if not retained.exists():retained.write_bytes(source.read_bytes())
        inputs.append(dict(path=str(retained.resolve()),sha256=sha(retained)))
    inputs.append(dict(path=str(Path(material_contexts).resolve()),sha256=sha(material_contexts)))
    @lru_cache(maxsize=12)
    def current(name):return resources.load_serialized_bundle(catalog/name,in_memory=True)
    def resolve(file,pointer):
        if not pointer['m_PathID']:raise ValueError('A shared skin-cut object is null.')
        name=file.name if not pointer['m_FileID']else file.externals[pointer['m_FileID']-1].name
        dest=current(current_cabs[name])[1]
        return dest,dest.objects[pointer['m_PathID']]
    def bone_paths(file,renderer):
        paths=[]
        for pointer in renderer['m_Bones']:
            tf,to=resolve(file,pointer);names=[];seen=set()
            while True:
                key=(tf.name,to.path_id)
                if key in seen:raise ValueError('A skin-cut native rig is cyclic.')
                seen.add(key);tree=to.read_typetree();_,go=resolve(tf,tree['m_GameObject']);names.append(go.peek_name())
                if not tree['m_Father']['m_PathID']:break
                tf,to=resolve(tf,tree['m_Father'])
            names.reverse()
            if 'Bip001'in names:names=names[names.index('Bip001'):]
            paths.append('/'.join(names))
        return paths
    mesh_packets={}
    @lru_cache(maxsize=16)
    def renderers(name):
        cf=current(name)[1];rows=[]
        for identifier,obj in cf.objects.items():
            if obj.type.name!='SkinnedMeshRenderer':continue
            tree=obj.read_typetree();mf,mo=resolve(cf,tree['m_Mesh']);mesh_key=(current_cabs[mf.name],mo.path_id)
            if mesh_key not in mesh_packets:mesh_packets[mesh_key]=export_current_mesh(mo)
            packet=mesh_packets[mesh_key]
            rows.append(dict(meshIdentity=packet['sourceFingerprint'],name=packet['name'],file=current_cabs[mf.name],object=mo.path_id,
                             packet=packet,bones=bone_paths(cf,tree)))
        return rows
    bodies={};body_families=[]
    for resource in manifest['resources']:
        if '/Graphics/Shape'not in resource['asset']or not resource['asset'].endswith('_Body.prefab'):continue
        name=resource['bundles'][0]['file'];rows=renderers(name)
        body_families.append((name,rows))
        for row in rows:
            if 'body_lod'in row['name'].lower():bodies.setdefault(row['meshIdentity'],[]).append(row)
    edits={};facts={};completed={};audited=[];normal_completed=set()
    def cohere(receiver,source):
        key=(receiver['file'],receiver['object'])
        if key in normal_completed:return
        normal_completed.add(key);packet=receiver['packet'];candidate,proof=shape.cohere_skin_frame_normals(packet)
        if not proof['changedFrameVertices']:return
        if any(candidate[k]!=packet[k]for k in packet if k!='shapes')or any(af['vertices']!=bf['vertices']for a,b in zip(candidate['shapes'],packet['shapes'])for af,bf in zip(a['frames'],b['frames'])):
            raise ValueError('Skin frame shading correction changed geometry or rest channels.')
        obj=current(receiver['file'])[1].objects[receiver['object']]
        edits.setdefault(receiver['file'],{})[receiver['object']]=resources.write_mesh_frame(obj,candidate)
        facts.setdefault(receiver['file'],[]).append(dict(kind='physical-skin-frame',source=source,mesh=receiver['name'],object=receiver['object'],**proof))
        packet.clear();packet.update(candidate)
    for name,rows in body_families:
        for row in rows:cohere(row,name)
    def common_bind_frame(receiver,donor):
        common=[(i,receiver['bones'].index(path))for i,path in enumerate(donor['bones'])if path in receiver['bones']]
        if not common:raise ValueError('Skin parts have no common renderer frame.')
        first,second=common[0]
        hp=np.asarray(receiver['packet']['bindPoses']).reshape(-1,4,4).transpose(0,2,1)
        bp=np.asarray(donor['packet']['bindPoses']).reshape(-1,4,4).transpose(0,2,1)
        return np.linalg.inv(bp[first])@hp[second]
    def close(receiver,donor,pairs,source,common_frame):
        key=(receiver['file'],receiver['object'])
        if key in completed:
            if completed[key]!=donor['meshIdentity']:raise ValueError('A shared skin-cut receiver has conflicting donors.')
            return
        if not pairs:
            audited.append(dict(source=source,mesh=receiver['name'],status='NO_SHARED_PHYSICAL_CUT'));return
        packet=receiver['packet'];candidate,proof=geometry.overlap_skin_boundary(packet,donor['packet'],pairs,common_frame,follow_donor_shading=True)
        keys=('vertexCount','uv','weights','boneIndices','bindPoses','subMeshes','colors')
        if any(candidate[k]!=packet[k]for k in keys):raise ValueError('Closing a skin cut changed its palette, UVs or topology.')
        cut=np.asarray([i for i,_ in pairs],int);unowned=np.ones(packet['vertexCount'],bool);unowned[cut]=False
        for channel in ('vertices','normals','tangents'):
            width=4 if channel=='tangents'else 3
            if not np.array_equal(np.asarray(candidate[channel]).reshape(-1,width)[unowned],np.asarray(packet[channel]).reshape(-1,width)[unowned]):
                raise ValueError('Closing a skin cut changed an unrelated vertex stream.')
        if [(s['name'],[f['weight']for f in s['frames']])for s in candidate['shapes']]!=[(s['name'],[f['weight']for f in s['frames']])for s in packet['shapes']]:
            raise ValueError('Closing a skin cut changed its expression channels.')
        for a,b in zip(candidate['shapes'],packet['shapes']):
            for af,bf in zip(a['frames'],b['frames']):
                for channel in ('vertices','normals','tangents'):
                    if not np.array_equal(np.asarray(af[channel]).reshape(-1,3)[unowned],np.asarray(bf[channel]).reshape(-1,3)[unowned]):
                        raise ValueError('Closing a skin cut changed an unrelated physical frame.')
        obj=current(receiver['file'])[1].objects[receiver['object']]
        edits.setdefault(receiver['file'],{})[receiver['object']]=resources.write_mesh_frame(obj,candidate)
        facts.setdefault(receiver['file'],[]).append(dict(source=source,mesh=receiver['name'],donor=donor['name'],object=receiver['object'],
            vertexPairs=pairs,unownedVertexStreamsExact=True,uvSkinTopologyAndChannelsExact=True,**proof))
        completed[key]=donor['meshIdentity'];packet.clear();packet.update(candidate)
    for context in read(material_contexts)['rows']:
        resource=resources_by_source.get(context['internalId'])
        if resource is None:raise ValueError('A declared skin-cut resource is missing.')
        canonical=material_packet(context['headSource']['initialCanonical'])
        native_uv=next(u for u in canonical['uv']if u['channel']==0)['values']
        candidates=[row for row in renderers(resource['bundles'][0]['file'])if row['name'].endswith('LOD'+str(context['lod']))and
            row['packet']['vertexCount']>=canonical['vertexCount']and
            next(u for u in row['packet']['uv']if u['channel']==0)['values'][:len(native_uv)]==native_uv]
        if len(candidates)!=1:raise ValueError('A native head cut has no unique renderer: '+context['head'])
        receiver=candidates[0];cohere(receiver,context['internalId']);body_canonical=material_packet(context['bodyCanonical']);count=body_canonical['vertexCount']
        donors=[row for rows in bodies.values()for row in rows if row['name'].endswith('_LOD'+str(context['lod']))and row['packet']['vertexCount']>=count and
            np.max(np.abs(np.asarray(row['packet']['vertices'][:count*3])-body_canonical['vertices']))<1e-5]
        unique={fingerprint(row['packet'])[0]:row for row in donors}
        if len(unique)!=1:raise ValueError('A native head cut has no unique authored Body: '+context['head'])
        donor=next(iter(unique.values()))
        common_frame=np.asarray(context['headSource']['joint']['headToBody'],float).reshape(4,4).T if context['lod']else common_bind_frame(receiver,donor)
        pairs=geometry.shared_skin_boundary_pairs(receiver['packet'],donor['packet'],receiver['bones'],donor['bones'],common_frame=common_frame)
        if not pairs:raise ValueError('A declared native Head/Body cut is open: '+context['head'])
        close(receiver,donor,pairs,context['internalId'],common_frame)
    for name,rows in body_families:
        for part,donor_part in (('Forearms','Body'),('Hands','Forearms'),('Feet','Body')):
            for receiver in (r for r in rows if r['name'].startswith(part+'_LOD')):
                lod=receiver['name'].rsplit('_LOD',1)[-1]
                donors=[r for r in rows if r['name']==donor_part+'_LOD'+lod]
                if len(donors)!=1:raise ValueError('An extremity has no unique native skin donor.')
                donor=donors[0];pairs=geometry.shared_skin_boundary_pairs(receiver['packet'],donor['packet'],receiver['bones'],donor['bones'])
                close(receiver,donor,pairs,name,common_bind_frame(receiver,donor))
    for name,objects in edits.items():
        env,file=resources.load_serialized_bundle(catalog/name,in_memory=True);before=sha(catalog/name)
        untouched={i:resources.serialized_object_bytes(o)for i,o in file.objects.items()if i not in objects}
        expected={}
        for identifier,tree in objects.items():file.objects[identifier].save_typetree(tree);expected[identifier]=file.objects[identifier].data
        pending=catalog/(name+'.writing');pending.write_bytes(env.file.save(packer='lz4'))
        _,saved=resources.load_serialized_bundle(pending,in_memory=True)
        if any(resources.serialized_object_bytes(o)!=(expected[i]if i in expected else untouched[i])for i,o in saved.objects.items()):
            raise ValueError('A saved skin-cut resource changed unrelated object bytes.')
        pending.replace(catalog/name);proof=proofs[name]
        operation=dict(kind='shared-skin-boundary',beforeSha256=before,sha256=sha(catalog/name),objects=list(objects),facts=facts[name],
            authority=dict(inputs=inputs),unrelatedObjectBytesExact=True,savedObjectBytesExact=True,cabAndExternalReferencesUnchanged=True)
        proof.setdefault('appearanceFinishing',[]).append(operation);proof['sha256']=operation['sha256'];proof['bytes']=(catalog/name).stat().st_size
        proof['allRetainedObjectFieldsExactAfterReferenceRebasing']=False
        write(output/(name+'.json'),operation);print('Closed shared skin cuts:',name,len(objects),flush=True)
    for row in manifest['bundles']:row['sha256']=proofs[row['file']]['sha256']
    for pin in inputs:
        if sha(pin['path'])!=pin['sha256']:raise ValueError('A skin-cut input changed during preparation.')
    write(catalog/'CharacterModels.manifest.json',manifest)
    assembly['catalogSha256']=sha(catalog/'CharacterModels.manifest.json');assembly['bytes']=sum(r['bytes']for r in assembly['proofs'])
    report=dict(status='PREPARED_SHARED_SKIN_CUTS_NOT_RENDERED',meshes=len(completed),bundles=len(edits),unsharedParts=audited,inputs=inputs)
    write(output/'Preparation.json',report)
    return report


def prepare_resource_faces(source,index_path,output,census_path):
    """Finish all native face resources and their shared Main fields in one catalog."""
    from functools import lru_cache
    import inspect
    import numpy as np
    import character_resources as resources
    owner=sys.modules[__name__];shape,_=source_owner();read=resources.read;sha=resources.digest
    census=read(census_path);native_index=read(index_path)
    parts={part['asset'].casefold():row['file']for row in native_index['bundles']for part in row.get('parts',[])}
    coverage=[dict(asset=a['internalId'],name=a['name'],kind='editable'if '/EditableCharacters/'in a['internalId']else'npc'if '/Monsters/NPC/'in a['internalId']else'creature',
        file=parts.get(a['internalId'].casefold()))for a in census['assets']]
    missing=[row for row in coverage if not row['file']and not row['asset'].lower().endswith('.fbx')]
    if missing:raise ValueError('A runtime face has no independently compiled resource: '+str(missing))
    names=sorted({row['file']for row in coverage if row['file']})
    styled={parts[a['internalId'].casefold()]for a in humanoid_sources(census)if a['internalId'].casefold()in parts}
    pins=[dict(path=str(Path(path).resolve()),sha256=sha(path))for path in (index_path,census_path,ROOT/'tools/blender-models/CharacterFaces/production.py')]
    def strength_field(packet,skin,styled_face):
        guard=shape.humanoid_face_contact_guard(packet,skin)
        return np.minimum(shape.face_surface_strength(packet,skin),guard)if styled_face else np.ones(packet['vertexCount'])
    source=checked_catalog_path(source);output=checked_catalog_path(output)
    if not output.is_relative_to(ROOT/'tools')or output==source:raise ValueError('Appearance output must be an independent tools workspace.')
    output.mkdir(parents=True,exist_ok=True)
    catalog=source/'Catalog';manifest=read(catalog/'CharacterModels.manifest.json');assembly=read(source/'ResourceAssembly.json')
    target=output/'Catalog';target.mkdir(exist_ok=True)
    checkpoint=output/'GeometryPreparation.json';geometry_catalog=output/'GeometryCatalog'
    face_code=inspect.getsource(prepare_resource_faces)
    shape_code=face_code[:face_code.index('\n    source=checked_catalog_path(source)')]+face_code[face_code.index('\n    @lru_cache'):face_code.index("\n    for row in manifest['bundles']:row['sha256']=old_proofs")]
    functions=(export_current_mesh,source_owner,resources.write_mesh_frame)
    signature=hashlib.sha256('\n'.join([shape_code,*[inspect.getsource(function)for function in functions]]).encode('utf8')).hexdigest()
    geometry_owner=dict(sourceCatalogSha256=sha(catalog/'CharacterModels.manifest.json'),sourceAssemblySha256=sha(source/'ResourceAssembly.json'),inputs=pins,codeSignature=signature)
    if checkpoint.exists():
        saved=read(checkpoint)
        if saved['owner']!=geometry_owner:raise ValueError('The saved geometry belongs to different source inputs or shape code.')
        for row in saved['manifest']['bundles']:
            original=geometry_catalog/row['file']
            if sha(original)!=row['sha256']:raise ValueError('A saved geometry bundle changed.')
            receipt=output/'ColorCorrections/Receipts'/(row['file']+'.json')
            if receipt.exists()and (target/row['file']).exists():
                color=read(receipt)
                if color.get('beforeSha256')==row['sha256']and color.get('sha256')==sha(target/row['file']):continue
            temporary=target/(row['file']+'.restoring');temporary.hardlink_to(original);temporary.replace(target/row['file'])
        print('Reused complete native face geometry:',saved['meshes'],flush=True)
        return saved
    if(output/'ResourceAssembly.json').exists():raise ValueError('A completed candidate already owns this directory.')
    marker=output/'WorkingDirectory.json'
    expected: dict[str, Any]=dict(kind='OwnedFacePreparation',source=str(source),catalogSha256=sha(catalog/'CharacterModels.manifest.json'))
    expected['inputs']=pins
    if marker.exists():
        if read(marker)!=expected:raise ValueError('A working directory belongs to other inputs.')
        allowed={row['file']for row in manifest['bundles']}
        allowed|={name+'.writing'for name in allowed}
        for path in target.iterdir():
            if not path.is_file()or path.name not in allowed:raise ValueError('An unknown file occupies the preparation workspace.')
        for path in target.iterdir():path.unlink()
    elif any(target.iterdir()):raise ValueError('An empty candidate catalog is required.')
    else:owner.write(marker,expected)
    by_file={row['file']:row for row in read(index_path)['bundles']};native_cabs={row['cab']:row for row in by_file.values()}
    if any(name not in by_file for name in names):raise ValueError('Unknown native resource file: '+str(set(names)-by_file.keys()))
    current_cabs={row['newCab']:row['file']for row in assembly['proofs']}
    old_proofs={row['file']:row for row in assembly['proofs']}
    for row in manifest['bundles']:
        path=catalog/row['file']
        if sha(path)!=row['sha256']:raise ValueError('A candidate source changed: '+str(path))
        (target/row['file']).hardlink_to(path)
    @lru_cache(maxsize=32)
    def native(cab):
        row=native_cabs[cab]
        if sha(row['source'])!=row['sha256']:raise ValueError('A native source changed.')
        return resources.load_serialized_bundle(row['source'])[1]
    @lru_cache(maxsize=16)
    def current(cab):return resources.load_serialized_bundle(catalog/current_cabs[cab],in_memory=True)[1]
    def resolve(file,pointer,original):
        if not pointer['m_PathID']:return None
        cab=file.name if pointer['m_FileID']==0 else file.externals[pointer['m_FileID']-1].name
        value=(native if original else current)(cab)
        return value,value.objects[pointer['m_PathID']]
    def required_resolve(file,pointer,original):
        result=resolve(file,pointer,original)
        if result is None:raise ValueError('A required facial source reference is null.')
        return result
    def shader_name(file,material,original):
        resolved=resolve(file,material,original)
        if not resolved:return None
        mf,mo=resolved;mt=mo.read_typetree();sf,so=required_resolve(mf,mt['m_Shader'],original)
        tree=so.read_typetree();return dict(shader=tree['m_ParsedForm']['m_Name'])
    def bone_names(file,renderer):
        result=[]
        for pointer in renderer['m_Bones']:
            resolved=resolve(file,pointer,True)
            if not resolved:result.append('');continue
            tf,to=resolved;names=[];visited=set()
            while True:
                identity=(tf.name,to.path_id)
                if identity in visited:raise ValueError('A native facial bone hierarchy is cyclic.')
                visited.add(identity);transform=to.read_typetree()
                _,game_object=required_resolve(tf,transform['m_GameObject'],True);names.append(game_object.peek_name())
                parent=resolve(tf,transform['m_Father'],True)
                if parent is None:break
                tf,to=parent
            result.append('/'.join(reversed(names)))
        return result
    def save(file_name,objects,kind,facts):
        env,file=resources.load_serialized_bundle(target/file_name,in_memory=True)
        before={i:hashlib.sha256(resources.serialized_object_bytes(o)).hexdigest()for i,o in file.objects.items()if i not in objects}
        expected={}
        for identifier,tree in objects.items():file.objects[identifier].save_typetree(tree);expected[identifier]=file.objects[identifier].data
        temp=target/(file_name+'.writing');temp.write_bytes(env.file.save(packer='lz4'))
        _,check=resources.load_serialized_bundle(temp,in_memory=True)
        for identifier,obj in check.objects.items():
            actual=resources.serialized_object_bytes(obj)
            if identifier in expected:
                if actual!=expected[identifier]:raise ValueError('A finished object failed its saved reload.')
            elif hashlib.sha256(actual).hexdigest()!=before[identifier]:raise ValueError('An unrelated object changed.')
        temp.replace(target/file_name)
        proof=old_proofs[file_name];proof['sha256']=sha(target/file_name);proof['bytes']=(target/file_name).stat().st_size
        proof['allRetainedObjectFieldsExactAfterReferenceRebasing']=False
        proof.setdefault('appearanceFinishing',[]).append(dict(kind=kind,objects=list(objects),facts=facts,
            unrelatedObjectBytesExact=True,savedObjectBytesExact=True,cabAndExternalReferencesUnchanged=True))
    completed={};texture_jobs={};reports=[]
    for file_name in names:
        row=by_file[file_name];nf=native(row['cab']);cf=current(next(cab for cab,name in current_cabs.items()if name==file_name))
        edits={};facts=[];donors={}
        for identifier,renderer in nf.objects.items():
            if renderer.type.name!='SkinnedMeshRenderer'or identifier not in cf.objects:continue
            nr=renderer.read_typetree();resolved=resolve(nf,nr['m_Mesh'],True)
            if not resolved:continue
            _,mesh=resolved
            if not mesh.peek_name().endswith('LOD0'):continue
            skin=dict(mesh=mesh.peek_name(),bones=bone_names(nf,nr),materials=[shader_name(nf,p,True)for p in nr['m_Materials']])
            if not any(m and 'Skin'in m['shader']for m in skin['materials']):continue
            if not any('eyelid'in name.lower()for name in skin['bones']):continue
            packet=owner.export_current_mesh(mesh);key=tuple(nr['m_RootBone'].values())
            if key in donors:raise ValueError('Ambiguous facial LOD0 owner.')
            donors[key]=(packet,strength_field(packet,skin,file_name in styled),skin)
        for identifier,renderer in nf.objects.items():
            if renderer.type.name!='SkinnedMeshRenderer'or identifier not in cf.objects:continue
            nr=renderer.read_typetree();cr=cf.objects[identifier].read_typetree()
            native_mesh=resolve(nf,nr['m_Mesh'],True);current_mesh=resolve(cf,cr['m_Mesh'],False)
            if not native_mesh or not current_mesh:continue
            nfile,nobj=native_mesh;cfile,cobj=current_mesh
            materials=[shader_name(nf,p,True)for p in nr['m_Materials']]
            skin=dict(mesh=nobj.peek_name(),path=nobj.peek_name(),bones=bone_names(nf,nr),materials=materials)
            facial=any(any(token in name.lower()for token in ('eyelid','lip_','nostril','cheek','eyebrow'))for name in skin['bones'])
            donor=donors.get(tuple(nr['m_RootBone'].values()))
            if donor is None:
                matching=[value for value in donors.values()if value[0]['name'].rsplit('LOD',1)[0]==skin['mesh'].rsplit('LOD',1)[0]]
                if len(matching)>1:raise ValueError('Ambiguous native facial LOD family.')
                if matching:donor=matching[0]
            facial|=donor is not None and any(m and 'Skin'in m['shader']for m in materials)
            companion=any(token in skin['mesh'].lower()for token in ('eyes','eyelash','occluder','jaw','teeth','tongue'))
            if not facial and not companion:continue
            if not any(m and ('Skin'in m['shader']or'CharacterEye'in m['shader'])for m in materials)and not companion:continue
            key=(current_cabs[cfile.name],cobj.path_id)
            if key in completed:continue
            npacket=owner.export_current_mesh(nobj);cpacket=owner.export_current_mesh(cobj)
            strength=np.zeros(npacket['vertexCount'])if companion else strength_field(npacket,skin,file_name in styled)
            if not companion and donor and not skin['mesh'].endswith('LOD0'):
                strength=np.minimum(strength_field(npacket,skin,file_name in styled),shape.lower_face_strength(npacket,*donor[:2]))
            try:
                if file_name in styled:packet,proof=shape.retain_source_proportions(npacket,cpacket,skin,strength)
                else:packet,proof=cpacket,dict(creatureAnatomyChannelsRetained=True,nativeVertices=npacket['vertexCount'],preparedVertices=cpacket['vertexCount'])
            except ValueError as error:raise ValueError(file_name+'/'+skin['mesh']+': '+str(error))from error
            completed[key]=npacket['sourceFingerprint']
            mesh=None if packet['sourceFingerprint']==cpacket['sourceFingerprint']else resources.write_mesh_frame(cobj,packet)
            if mesh is not None:
                if key[0]==file_name:edits[key[1]]=mesh;facts.append(dict(name=skin['mesh'],**proof))
                else:save(key[0],{key[1]:mesh},'source-owned-face',dict(name=skin['mesh'],**proof))
            if not companion:
                for pointer in cr['m_Materials']:
                    if not pointer['m_PathID']:continue
                    mf,mo=required_resolve(cf,pointer,False);mt=mo.read_typetree();textures=dict(mt['m_SavedProperties']['m_TexEnvs'])
                    if '_MainTex'not in textures or not textures['_MainTex']['m_Texture']['m_PathID']:continue
                    tf,to=required_resolve(mf,textures['_MainTex']['m_Texture'],False)
                    if not to.peek_name().endswith('_Main_FieldBC7'):continue
                    texture=texture_jobs.setdefault((current_cabs[tf.name],to.path_id),dict(name=to.peek_name(),styled=False,owners=[]))
                    texture['styled']|=file_name in styled
                    if file_name not in texture['owners']:texture['owners'].append(file_name)
        if edits:save(file_name,edits,'source-owned-face',facts)
        reports.append(dict(file=file_name,meshes=len(facts)))
        print('Finished face:',file_name,len(facts),flush=True)
    for row in manifest['bundles']:row['sha256']=old_proofs[row['file']]['sha256']
    for pin in pins:
        if sha(pin['path'])!=pin['sha256']:raise ValueError('A geometry source changed during preparation.')
    geometry_catalog.mkdir()
    for row in manifest['bundles']:(geometry_catalog/row['file']).hardlink_to(target/row['file'])
    result=dict(status='PREPARED_GEOMETRY',owner=geometry_owner,manifest=manifest,assembly=assembly,coverage=coverage,rows=reports,humanStyleFiles=sorted(styled),
        meshes=len(completed),textures=[dict(file=file,identifier=identifier,**value)for(file,identifier),value in texture_jobs.items()])
    write(checkpoint,result)
    return result


def prepare_resource_appearance(source,index_path,output,census_path,material_contexts,tone_inputs,authoring_sources):
    import character_resources as resources
    geometry=prepare_resource_faces(source,index_path,output,census_path)
    target=output/'Catalog';catalog=source/'Catalog';manifest=geometry['manifest'];assembly=geometry['assembly']
    old_proofs={row['file']:row for row in assembly['proofs']}
    texture_jobs={(row['file'],row['identifier']):row for row in geometry['textures']}
    coverage=geometry['coverage'];reports=geometry['rows'];styled=geometry['humanStyleFiles']
    pins=[dict(path=str(Path(path).resolve()),sha256=sha(path))for path in (index_path,census_path,material_contexts,__file__,
        ROOT/'tools/blender-models/CharacterFaces/production.py',ROOT/'tools/blender-models/CharacterFaces/material_fields.py',Path(resources.__file__))]
    color_proofs=retone_resource_fields(target,texture_jobs,material_contexts,tone_inputs,authoring_sources,output/'ColorCorrections')
    for proof in color_proofs:
        row=old_proofs[proof['file']];row['sha256']=sha(target/proof['file']);row['bytes']=(target/proof['file']).stat().st_size
        row['allRetainedObjectFieldsExactAfterReferenceRebasing']=False
        row.setdefault('appearanceFinishing',[]).append(proof)
    finish_resource_skin_cuts(target,assembly,material_contexts,output/'SkinCuts')
    cab_to_file={row['newCab']:row['file']for row in assembly['proofs']}
    dependencies={row['file']:{cab_to_file[cab]for cab in row['dependencies']}for row in assembly['proofs']}
    manifest['resources']=resources.complete_resource_dependencies(manifest['resources'],dependencies)
    for row in manifest['bundles']:row['sha256']=old_proofs[row['file']]['sha256']
    for pin in pins:
        if sha(pin['path'])!=pin['sha256']:raise ValueError('An appearance source changed during preparation.')
    write(target/'CharacterModels.manifest.json',manifest)
    assembly['catalogSha256']=sha(target/'CharacterModels.manifest.json');assembly['bytes']=sum(row['bytes']for row in assembly['proofs'])
    assembly['bundleDependencyPolicy']='serialized-file-closure'
    assembly['faceFinishing']=dict(source=str(source),sourceCatalogSha256=sha(catalog/'CharacterModels.manifest.json'),inputs=pins,rows=reports,coverage=coverage,
        humanStyleFiles=sorted(styled),creatureAnatomyPreserved=True,colorCorrections=color_proofs)
    write(output/'ResourceAssembly.json',assembly)
    return dict(status='PREPARED_NOT_VISUALLY_ACCEPTED',meshes=geometry['meshes'],textures=len(texture_jobs),catalog=str(target),runtimeFaces=sum(bool(row['file'])for row in coverage))


def prepare_appearance_resources():
    parser=argparse.ArgumentParser();parser.add_argument('--source',type=Path,required=True);parser.add_argument('--native-index',type=Path,required=True)
    parser.add_argument('--census',type=Path,required=True);parser.add_argument('--material-contexts',type=Path,required=True)
    parser.add_argument('--tone-inputs',type=Path,required=True);parser.add_argument('--authoring-sources',type=Path,required=True)
    args=parser.parse_args()
    print(json.dumps(prepare_resource_appearance(args.source,args.native_index,require_workspace(),args.census,args.material_contexts,args.tone_inputs,args.authoring_sources)))


def main():
    global HERE,PUBLIC,p,art,np,UnityPy,MeshHandler,encode_recipe
    parser=argparse.ArgumentParser(description=__doc__,allow_abbrev=False)
    parser.add_argument('--workspace',type=Path,required=True)
    parser.add_argument('--source-directory',type=Path)
    parser.add_argument('command',choices=('scope','capture','extract','generate','generate-npc','generate-extremities','generate-skin-boundaries','generate-materials','retone-materials','prepare-material-codec','verify-material-codec','prepare-resource-spans','prepare','finalize','assemble-resources','prepare-appearance-resources','package-resources','publish'))
    args,remaining=parser.parse_known_args();HERE=checked_catalog_path(args.workspace)
    if not HERE.is_relative_to(ROOT/'tools'):
        raise ValueError('Model work data must be in the repository tools workspace.')
    HERE.mkdir(parents=True,exist_ok=True);sys.argv=[sys.argv[0]]+remaining
    if args.source_directory:PUBLIC=args.source_directory.resolve()
    if args.command in ('extract','prepare','finalize','assemble-resources') and not(args.command=='extract'and any(value=='--material-inputs'or value.startswith('--material-inputs=')for value in remaining)):
        import UnityPy
        from UnityPy.helpers.MeshHelper import MeshHandler
    if args.command=='prepare':
        sys.path.insert(0,str(ROOT/'tools/asset-authoring/UnityAssetBuild'))
        from mesh_recipe_codec import encode_recipe
    if args.command in ('generate','generate-npc','generate-extremities','generate-skin-boundaries'):
        import numpy as np
        p,art=source_owner()
    {'scope':select_scope,'capture':capture_source_catalog,'extract':extract_current,'generate':generate_humanoids,'generate-npc':generate_npcs,'generate-extremities':generate_extremities,'generate-skin-boundaries':generate_skin_boundaries,
        'generate-materials':generate_materials,'retone-materials':retone_materials,'prepare-material-codec':prepare_material_codec,'verify-material-codec':verify_material_codec,'prepare-resource-spans':prepare_resource_span_catalog,'prepare':prepare_bundles,'finalize':finalize_catalog,'assemble-resources':assemble_resources,'prepare-appearance-resources':prepare_appearance_resources,'package-resources':package_resources,'publish':publish_catalog}[args.command]()


if __name__=='__main__':main()
