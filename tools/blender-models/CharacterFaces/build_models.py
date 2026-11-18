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
import stat
import struct
import sys
import types
import uuid
from functools import lru_cache
from pathlib import Path
from typing import Any

ROOT=next(p for p in Path(__file__).resolve().parents if (p/'SolastaUnfinishedBusiness.sln').is_file())
sys.path.insert(0,str(ROOT/'tools/blender-models/CharacterFaces'))
sys.path.insert(0,str(ROOT/'tools/asset-authoring'))
sys.path.insert(0,str(ROOT/'tools/asset-authoring/UnityAssetBuild'))
from collect_sources import fingerprint
import asset_names
from mesh_recipe_codec import four_slot_skin, skin_bone_order, skin_palette_frame

HERE: Path | None=None

def require_workspace() -> Path:
    if HERE is None:
        raise ValueError('Initialize the model workspace before running this operation.')
    return HERE
PUBLIC_RELATIVE_PATH=Path('SolastaUnfinishedBusiness/BlenderModels/CharacterModels')
PUBLIC=ROOT/PUBLIC_RELATIVE_PATH
EXACT_MAIN_TEXTURE_MAX_BYTES=8*1024**2
EXACT_MAIN_CATALOG_MAX_BYTES=256*1024**2
MAIN_RGB_BLOCK_REFINEMENT_ERROR=16
MAIN_RGB_RMS_LIMIT=2.
read=lambda path:json.loads(Path(path).read_text(encoding='utf-8-sig'))


def resource_package_configuration(path=None):
    """The package owner declares public names, release copy and distribution URLs."""
    owner=Path(__file__)
    configuration=read(path or owner.with_name('resource-package.json'+('.source'if owner.suffix=='.source'else'')))
    if configuration.get('schemaVersion')!=1:raise ValueError('Unsupported resource package configuration.')
    files=configuration['files'];names=list(files.values())
    if any(not isinstance(name,str)or not name or Path(name).name!=name or '/'in name or '\\'in name for name in names)or len({name.casefold()for name in names})!=len(names):
        raise ValueError('Resource package files require distinct leaf names.')
    if type(configuration['maximumPacks'])is not int or configuration['maximumPacks']<1:raise ValueError('A positive resource pack count is required.')
    archive_names=[configuration['archiveName'].format(ordinal=ordinal)for ordinal in range(1,configuration['maximumPacks']+1)]
    if len({name.casefold()for name in archive_names})!=len(archive_names)or any(Path(name).name!=name or '/'in name or '\\'in name or Path(name).suffix.lower()!='.zip'for name in archive_names):
        raise ValueError('Resource archives require distinct ZIP leaf names.')
    re.compile(configuration['versionPattern'])
    metadata=configuration['releaseMetadata']
    if not isinstance(metadata,dict)or any(not isinstance(metadata.get(key),str)or not metadata[key].strip()for key in ('name','body'))or metadata.get('generate_release_notes')is not False:
        raise ValueError('Resource releases require declared title and description without generated notes.')
    return configuration


RESOURCE_PACKAGE=resource_package_configuration()
RESOURCE_FILES=RESOURCE_PACKAGE['files']
def sha(path):
    with Path(path).open('rb') as stream:
        return hashlib.file_digest(stream,'sha256').hexdigest()


def resource_metadata_summary(path,fields):
    """Read selected catalog metadata and its exact byte identity in one pass."""
    path=checked_source_path(path)
    if not fields or any(not isinstance(field,str)or not field for field in fields):
        raise ValueError('Choose explicit top-level metadata properties.')
    payload=path.read_bytes();document=json.loads(payload.decode('utf-8-sig'))
    if not isinstance(document,dict):raise ValueError('Resource metadata must be a JSON object.')
    return dict(source=dict(path=str(path),sha256=hashlib.sha256(payload).hexdigest(),bytes=len(payload)),
        metadata={field:document[field]for field in fields if field in document},
        missingFields=[field for field in fields if field not in document])


def inspect_resource_metadata():
    parser=argparse.ArgumentParser();parser.add_argument('--assembly',type=Path,required=True)
    parser.add_argument('--fields',nargs='+',required=True)
    args=parser.parse_args();print(json.dumps(resource_metadata_summary(args.assembly,args.fields),ensure_ascii=False))


def write(path,value):
    path=Path(path);path.parent.mkdir(parents=True,exist_ok=True)
    temporary=path.with_name(path.name+'.writing')
    temporary.write_text(json.dumps(value,ensure_ascii=False,separators=(',',':'),allow_nan=False)+'\n',encoding='utf8')
    temporary.replace(path)


def resource_art_acceptance(assembly,catalog_sha):
    """Review authority belongs to the exact catalog, rather than its copied flag."""
    if assembly.get('artAccepted')is not True:return False
    try:
        proof=assembly['artAcceptance'];path=checked_source_path(proof['path'])
        if sha(path)!=proof['sha256']:return False
        review=read(path);candidate=review.get('candidate',review.get('candidateManifest',{}))
        return str(review.get('status','')).startswith('PASS_')and candidate.get('sha256')==catalog_sha
    except (KeyError,ValueError,OSError,TypeError):return False


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


def checked_source_path(path):
    """Validate a local immutable input without granting publication ownership."""
    path=Path(path).absolute()
    if path.drive.startswith('\\\\')or path.resolve()!=path or not path.is_file():
        raise ValueError('A model input is not a regular local file or traverses a link.')
    for member in (path,*path.parents):
        info=member.lstat()
        if stat.S_ISLNK(info.st_mode)or getattr(info,'st_file_attributes',0)&0x400:
            raise ValueError('A model input contains a reparse point: '+str(member))
    return path


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
        sourceReadMode='serialized-mesh-data',
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


def source_owner(*,production_path=None,art_path=None):
    import numpy as np
    owner=ROOT/'tools/blender-models/CharacterFaces'
    scope=dict(np=np,math=math,copy=copy,gzip=gzip,hashlib=hashlib,json=json,struct=struct,Path=Path,
        REPO=ROOT,SOURCE=ROOT/'tools/asset-authoring/workspace/FaceRefinement/NativeSources',
        CONVERT=np.array([[1.,0,0,0],[0,0,-1,0],[0,1,0,0],[0,0,0,1]]),
        resource_naming=asset_names,IMAGE_NAMES={},skin_palette_frame=skin_palette_frame)
    art=types.ModuleType("character_face_source")
    art.__dict__.update(scope)
    art.__file__=str(Path(art_path)if art_path is not None else owner/'generate.py')
    for path,environment in [(Path(art.__file__),art.__dict__)]:
        nodes: list[ast.stmt]=[n for n in ast.parse(path.read_text(encoding='utf8')).body if isinstance(n,ast.FunctionDef)]
        exec(compile(ast.Module(body=nodes,type_ignores=[]),str(path),'exec'),environment)
    production=types.ModuleType("character_face_production")
    production.__dict__.update(scope,art=art,fingerprint=fingerprint,MATERIAL_CHANNEL_CONTRACTS={})
    production.__file__=str(Path(production_path)if production_path is not None else owner/'production.py')
    env=production.__dict__
    path=Path(production.__file__)
    production_nodes: list[ast.stmt]=[n for n in ast.parse(path.read_text(encoding='utf8')).body if isinstance(n,ast.FunctionDef)]
    exec(compile(ast.Module(body=production_nodes,type_ignores=[]),str(path),'exec'),env)
    return production,art


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


def extract_serialized_sources(census_path, index_path, output, selection_path=None):
    """Pair runtime renderer metadata with original, unskinned bundle channels."""
    from functools import lru_cache
    import character_resources as resources
    import collect_sources as sources
    census_path=Path(census_path).resolve();index_path=Path(index_path).resolve()
    output=checked_catalog_path(output)
    if output.exists() and any(output.iterdir()):
        raise ValueError('A new empty native-source directory is required.')
    pins=[dict(path=str(Path(path).resolve()),sha256=sha(path)) for path in
          (census_path,index_path,Path(__file__),Path(sources.__file__),Path(resources.__file__),Path(resources.__file__).with_name('mesh_recipe_codec.py'))]
    if selection_path is not None:pins.append(dict(path=str(Path(selection_path).resolve()),sha256=sha(selection_path)))
    for pin in pins:
        if Path(pin['path']).suffix=='.py':
            archive=output/'SourceFiles'/Path(pin['path']).name
            archive.parent.mkdir(parents=True,exist_ok=True)
            shutil.copyfile(pin['path'],archive)
            if sha(archive)!=pin['sha256']:raise ValueError('A native-source producer changed while being captured.')
            pin['archivePath']=str(archive)
    census=read(census_path);index=read(index_path)
    selected=None if selection_path is None else set(read(selection_path)['names'])
    candidates=[a for a in census['assets'] if selected is None or a['name'] in selected]
    if selected is not None and {a['name'] for a in candidates}!=selected:
        raise ValueError('The declared native source selection is incomplete.')
    by_cab={row['cab']:row for row in index['bundles']}
    parts={part['asset'].casefold():row['cab'] for row in index['bundles'] for part in row.get('parts',[])}
    origins={};images={};written={};assets=[];omitted=[];proofs=[]
    @lru_cache(maxsize=2)
    def native(cab):
        row=by_cab[cab]
        if sha(row['source'])!=row['sha256']:raise ValueError('An original native bundle changed: '+row['source'])
        origins[cab]=dict(path=row['source'],sha256=row['sha256'])
        return resources.load_serialized_bundle(row['source'],in_memory=True)
    def resolve(file,pointer):
        if not pointer['m_PathID']:return None
        cab=file.name if pointer['m_FileID']==0 else file.externals[pointer['m_FileID']-1].name
        _,owner=native(cab)
        return owner,owner.objects[pointer['m_PathID']]
    def required(file,pointer):
        value=resolve(file,pointer)
        if value is None:raise ValueError('A native renderer has a null ownership reference.')
        return value
    def object_name(file,transform):
        return required(file,transform['m_GameObject'])[1].peek_name()
    def relative_path(file,transform,root):
        names=[];seen=set()
        while (file.name,transform.path_id)!=root:
            key=(file.name,transform.path_id)
            if key in seen:raise ValueError('A native transform hierarchy is cyclic.')
            seen.add(key);tree=transform.read_typetree();names.append(object_name(file,tree))
            file,transform=required(file,tree['m_Father'])
        return '/'.join(reversed(names))
    def image_digest(path):
        path=Path(path).resolve()
        if path not in images:images[path]=sha(path)
        return images[path]
    write(output/'WorkingDirectory.json',dict(kind='SerializedNativeFaceSources',inputs=pins))
    for asset in candidates:
        cab=parts.get(asset['internalId'].casefold())
        if cab is None:
            if selected is not None or not asset['internalId'].lower().endswith('.fbx'):
                raise ValueError('A source asset has no typed native prefab owner: '+asset['internalId'])
            omitted.append(dict(asset=asset['internalId'],reason='unbound model-import alias'));continue
        if sources.source_content_key(asset,image_digest)!=asset['sourceContentKey']:
            raise ValueError('The source census content key does not match its current renderer/material content.')
        environment,file=native(cab)
        root=resources.typed_bundle_asset(environment,asset['internalId'],'GameObject').deref()
        components=root.read_typetree()['m_Component']
        root_transform=next(required(file,row['component']) for row in components
                            if required(file,row['component'])[1].type.name=='Transform')
        root_key=(root_transform[0].name,root_transform[1].path_id)
        renderers={};stack=[(root_transform[0],root_transform[1])];visited=set()
        while stack:
            current,transform=stack.pop();key=(current.name,transform.path_id)
            if key in visited:raise ValueError('A source prefab repeats a transform.')
            visited.add(key);tree=transform.read_typetree();_,game_object=required(current,tree['m_GameObject'])
            path=relative_path(current,transform,root_key)
            for component in game_object.read_typetree()['m_Component']:
                owner,obj=required(current,component['component'])
                if obj.type.name=='SkinnedMeshRenderer':
                    if path in renderers:raise ValueError('A renderer path has more than one skin owner.')
                    renderers[path]=(owner,obj)
            stack.extend(required(current,child) for child in tree['m_Children'])
        if set(renderers)!={s['path']for s in asset['skins']}:
            raise ValueError('The serialized native prefab and runtime skin closure differ: '+asset['name'])
        refreshed=copy.deepcopy(asset)
        for skin in refreshed['skins']:
            owner,renderer=renderers[skin['path']];tree=renderer.read_typetree()
            if any(not pointer['m_PathID']for pointer in tree['m_Bones']):raise ValueError('A native mesh has an unowned bone slot.')
            native_bones=[relative_path(*required(owner,pointer),root_key)for pointer in tree['m_Bones']]
            if native_bones!=skin['bones']:raise ValueError('The native renderer bone palette differs: '+asset['name']+'/'+skin['path'])
            mesh_file,mesh=required(owner,tree['m_Mesh']);packet=export_current_mesh(mesh)
            if packet['vertexCount']!=skin['vertexCount']or packet['name']!=skin['mesh']:
                raise ValueError('The typed native renderer mesh identity differs.')
            bone_hashes=mesh.read_typetree()['m_BoneNameHashes']
            order=skin_bone_order(native_bones,bone_hashes)
            source_matrices=skin['bonesToPrefab']
            skin['bones']=[native_bones[index]for index in order]
            skin['bonesToPrefab']=[value for index in order for value in source_matrices[index*16:(index+1)*16]]
            source_path=Path(skin['meshFile']).resolve()
            if not source_path.is_relative_to(census_path.parent):raise ValueError('A census mesh is outside its declared source root.')
            target=output/source_path.relative_to(census_path.parent)
            previous=written.get(target)
            if previous is not None and previous!=packet['sourceFingerprint']:
                raise ValueError('Two original meshes conflict at the same declared source path.')
            if previous is None:
                target.parent.mkdir(parents=True,exist_ok=True)
                asset_names.assert_path_budget(target,259)
                with gzip.open(target,'wt',encoding='utf-8')as stream:json.dump(packet,stream,separators=(',',':'))
                written[target]=packet['sourceFingerprint']
            provenance=dict(bundle=origins[mesh_file.name],serializedFile=mesh_file.name,meshPathId=str(mesh.path_id),
                rendererPathId=str(renderer.path_id),sourceReadMode='serialized-mesh-data',
                priorCensusFingerprint=skin['sourceFingerprint'],boneOrder=dict(sourcePaths=native_bones,
                    meshBoneNameHashes=bone_hashes,permutation=order))
            skin.update(meshFile=str(target),sourceFingerprint=packet['sourceFingerprint'],sourceProvenance=provenance)
            proofs.append(dict(asset=asset['internalId'],renderer=skin['path'],sourceFingerprint=packet['sourceFingerprint'],**provenance))
        refreshed['sourceContentKey']=sources.source_content_key(refreshed,image_digest);assets.append(refreshed)
        print(json.dumps(dict(nativeSource=asset['name'],skins=len(asset['skins']))),flush=True)
    if any(sha(path)!=signature for path,signature in images.items()):raise ValueError('A native material image changed during extraction.')
    if any(sha(row['path'])!=row['sha256']for row in origins.values()):raise ValueError('A native source bundle changed during extraction.')
    if any(sha(row['path'])!=row['sha256']for row in pins):raise ValueError('A native source extraction input changed during processing.')
    output.mkdir(parents=True,exist_ok=True)
    write(output/'Census.json',dict(census,assets=assets,unboundSourceAliases=omitted,
        geometrySourceMode='serialized-mesh-data'))
    write(output/'SerializedSources.json',dict(status='PASS_SERIALIZED_NATIVE_SOURCES',assets=len(assets),skins=len(proofs),
        sourceCensus=dict(path=str(census_path),sha256=sha(census_path)),nativeIndex=dict(path=str(index_path),sha256=sha(index_path)),
        sourceReadMode='serialized-mesh-data',producerInputs=pins,origins=list(origins.values()),meshes=proofs,
        materialImages=[dict(path=str(path),sha256=signature)for path,signature in images.items()],omitted=omitted))


def extract_current():
    parser = argparse.ArgumentParser()
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--selection', type=Path)
    parser.add_argument('--material-inputs', type=Path)
    parser.add_argument('--native-index',type=Path)
    parser.add_argument('--census',type=Path,default=ROOT/'tools/asset-authoring/workspace/FaceRefinement/NativeSources/Census.json')
    args = parser.parse_args()
    if args.native_index:
        if args.material_inputs:raise ValueError('Select native mesh sources or material inputs.')
        extract_serialized_sources(args.census,args.native_index,args.output,args.selection);return
    if args.material_inputs:
        request_path=args.material_inputs.absolute();request=material_paths(read(request_path),request_path.parent)
        request['outputDirectory']=str(args.output.absolute())
        result=extract_material_native(request)
        result.update(sourceRequest=dict(path=str(request_path),sha256=sha(request_path)),extractionOwnerSha256=sha(__file__))
        write(checked_catalog_path(args.output,'NativeBc3.json'),result)
        print(json.dumps(dict(status=result['status'],sources=len(result['rows']))));return
    public = PUBLIC
    manifest = read(public / RESOURCE_FILES['manifest'])
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
        manifestSha256=hashlib.sha256((public / RESOURCE_FILES['manifest']).read_bytes()).hexdigest(),
        sourceDirectory=str(public),sourceRecipeVersion=manifest['recipeVersion'],
        humanoidAssets=[dict(name=a['name'], sourceContentKey=a['sourceContentKey']) for a in humanoids],
        meshes=records), separators=(',', ':')), encoding='utf8')


def generate_source_guides(input_name, output_name):
    """Adapt the common facial production owner to the standalone catalog format."""
    parser=argparse.ArgumentParser()
    parser.add_argument('--profiles',type=Path,required=True)
    parser.add_argument('--census',type=Path,default=ROOT/'tools/asset-authoring/workspace/FaceRefinement/NativeSources/Census.json')
    parser.add_argument('--source-root',type=Path)
    args=parser.parse_args()
    current=read(require_workspace()/input_name/'Index.json')
    wanted={row['sourceContentKey']for row in current['humanoidAssets']}
    census=read(args.census)
    selected=[asset for asset in census['assets']if asset['sourceContentKey']in wanted]
    if {asset['sourceContentKey']for asset in selected}!=wanted:
        raise ValueError('The native census omits a selected face source.')
    setattr(art,'SOURCE',(args.source_root or args.census.parent).resolve())
    selected=p.load_profiles(dict(census,assets=selected),args.profiles)['assets']
    p.require_selected_guides(selected)
    selected=[p.bind_source_lod_families(asset)for asset in selected]
    pins=[dict(path=str(path.resolve()),sha256=sha(path))for path in
          (Path(__file__),ROOT/'tools/blender-models/CharacterFaces/production.py',ROOT/'tools/blender-models/CharacterFaces/generate.py',args.profiles,args.census)]
    output=require_workspace()/output_name
    (output/'Meshes').mkdir(parents=True,exist_ok=True)
    generated={};assets=[];errors=[];conflicts=[];sharing=collections.defaultdict(list)
    for asset in selected:
        rows=[];primary=None
        try:
            for result in p.refine_source_asset(asset):
                skin=result['skin'];native=result['native'];authored=result['authored'];metrics=result['details']
                identity=native['sourceFingerprint']
                audit_source_channels(native,authored)
                if primary is None and p.role(skin)=='skin'and p.lod_index(skin)==0:primary=identity
                prior=generated.get(identity)
                if prior is not None and prior['authoredFingerprint']!=authored['sourceFingerprint']:
                    conflicts.append(dict(sourceFingerprint=identity,first=prior['firstOwner'],second=asset['name']))
                    raise ValueError('A shared native mesh has conflicting authored guides.')
                if prior is None:
                    path=output/'Meshes'/(identity+'.json.gz')
                    with gzip.open(path,'wt',encoding='utf-8')as stream:json.dump(authored,stream,separators=(',',':'))
                    entry=dict(metrics)
                    entry.update(sourceFingerprint=identity,authoredFingerprint=authored['sourceFingerprint'],
                        baselineFingerprint=identity,path=str(path),sha256=sha(path),firstOwner=asset['name'])
                    generated[identity]=entry
                row=dict(metrics);row.update(rendererPath=skin['path'],sourceFingerprint=identity);rows.append(row)
                sharing[identity].append(asset['sourceContentKey'])
            if primary is None:raise ValueError('The source guide has no LOD0 facial skin owner.')
            assets.append(dict(name=asset['name'],sourceContentKey=asset['sourceContentKey'],primary=primary,meshes=rows))
            print(json.dumps(dict(name=asset['name'],meshes=len(rows),sourceGuide=True)),flush=True)
        except (ValueError,RuntimeError)as error:
            errors.append(dict(name=asset['name'],reason=str(error)))
    for pin in pins:
        if sha(pin['path'])!=pin['sha256']:raise ValueError('A facial authoring input changed during generation.')
    result=dict(status='PASS_GENERATED_HUMANOID_SOURCES'if not errors and not conflicts else'REVIEW_REQUIRED',
        currentManifestSha256=current['manifestSha256'],sourceDirectory=current.get('sourceDirectory',str(PUBLIC)),
        sourceRecipeVersion=current.get('sourceRecipeVersion'),generatorSha256=sha(Path(__file__)),producerInputs=pins,
        assets=assets,meshes=list(generated.values()),sharedConflicts=conflicts,errors=errors,
        sourceGroups=[dict(sourceFingerprint=key,sourceKeys=sorted(set(value)))for key,value in sharing.items()],
        unchangedNonHumanoidSourceKeys=len({asset['sourceContentKey']for asset in census['assets']}-wanted),artAccepted=False)
    write(output/'Index.json',result)
    print(json.dumps(dict(status=result['status'],assets=len(assets),meshes=len(generated),conflicts=len(conflicts),errors=errors)),flush=True)


def generate_humanoids():
    generate_source_guides('CurrentHumanoids', 'ModernHumanoids')


def generate_npcs():
    generate_source_guides('CurrentNpcHumanoids', 'ModernNpcHumanoids')


def prepare_bundles():
    parser=argparse.ArgumentParser();parser.add_argument('--generation',type=Path,default=require_workspace() /'ModernHumanoids/Index.json')
    parser.add_argument('--destination',type=Path,default=require_workspace() /'UnityInput');args=parser.parse_args()
    generated=read(args.generation)
    public=Path(generated.get('sourceDirectory',str(PUBLIC)))
    manifest_path=public/RESOURCE_FILES['manifest'];manifest=read(manifest_path)
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
        editable='/EditableCharacters/' in asset['internalId']
        npc='/Monsters/NPC/' in asset['internalId']
        if scope=='editable' and editable or scope=='npc' and npc or scope=='all' and (editable or npc):selected.append(asset)
    return selected


def select_scope():
    parser=argparse.ArgumentParser();parser.add_argument('--scope',choices=('editable','npc','all'),default='all');parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--census',type=Path,default=ROOT/'tools/asset-authoring/workspace/FaceRefinement/NativeSources/Census.json')
    args=parser.parse_args();census=read(args.census)
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
    manifest=read(PUBLIC/RESOURCE_FILES['manifest'])
    inputs=catalog_payloads(manifest)
    name_sources=(read_name_sources(args.name_sources,PUBLIC/RESOURCE_FILES['manifest']) if args.name_sources else
        make_name_sources(manifest,sha(PUBLIC/RESOURCE_FILES['manifest']),args.name_source_metadata) if args.name_source_metadata else None)
    for file,expected in inputs.items():
        source=checked_catalog_path(PUBLIC,file)
        if sha(source)!=expected:raise ValueError('Source catalog data hash differs: '+file)
        os.link(source,destination/file)
    (destination/RESOURCE_FILES['manifest']).write_bytes((PUBLIC/RESOURCE_FILES['manifest']).read_bytes())
    write(destination/'SourceCatalog.json',dict(status='PASS_IMMUTABLE_SOURCE_CATALOG_CAPTURE',
        manifestSha256=sha(destination/RESOURCE_FILES['manifest']),source=str(PUBLIC),files=inputs,
        publicNameSources=name_sources,
        ownership='Hard links are immutable inputs. Public replacements use new files and directory-entry replacement.'))
    print(json.dumps(dict(status='PASS_IMMUTABLE_SOURCE_CATALOG_CAPTURE',files=len(inputs),destination=str(destination))))


def finalize_catalog():
    parser=argparse.ArgumentParser();parser.add_argument('--plan',type=Path,action='append',required=True)
    parser.add_argument('--results',type=Path,required=True);parser.add_argument('--destination',type=Path,required=True)
    args=parser.parse_args();plans=[read(path) for path in args.plan]
    source=PUBLIC;manifest=read(source/RESOURCE_FILES['manifest']);source_hash=sha(source/RESOURCE_FILES['manifest'])
    if source==ROOT/PUBLIC_RELATIVE_PATH:
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
    target=destination/RESOURCE_FILES['manifest']
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
    parser.add_argument('--validate-only',action='store_true')
    parser.add_argument('--name-sources',type=Path)
    parser.add_argument('--name-source-metadata',type=Path,action='append');args=parser.parse_args()
    if args.name_sources and args.name_source_metadata:raise ValueError('Select pinned naming sources or their native metadata inputs.')
    candidate=checked_catalog_path(args.catalog);candidate_manifest=checked_catalog_path(candidate,RESOURCE_FILES['manifest'])
    current=ROOT/PUBLIC_RELATIVE_PATH;manifest_path=current/RESOURCE_FILES['manifest']
    checked_catalog_path(current)
    if sha(manifest_path)!=args.expected_current_sha256:raise ValueError('The public catalog changed before publication.')
    quality=read(candidate/'CatalogQuality.json')
    if quality['status']!='PASS_COMPLETE_HUMANOID_CATALOG' or not quality.get('completeHumanlikeScope') or sha(candidate_manifest)!=quality['candidateManifestSha256']:
        raise ValueError('The candidate catalog has no matching complete verification receipt.')
    manifest=read(candidate_manifest);source=checked_catalog_path(Path(quality['sourceCatalog']))
    if source==current or sha(source/RESOURCE_FILES['manifest'])!=quality['sourceManifestSha256']:
        raise ValueError('The immutable original catalog is missing or changed.')
    if sha(manifest_path)!=quality['sourceManifestSha256']:
        raise ValueError('The current public catalog differs from the retained rollback source.')
    if manifest.get('schemaVersion')==3:
        publish_resource_catalog(candidate,current,source,manifest,quality,validate_only=args.validate_only)
        return
    wanted=catalog_payloads(manifest)
    original=read(source/RESOURCE_FILES['manifest'])
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
    atomic_publish_catalog(candidate,current,source,manifest_bytes,published_payloads,inverse,original_hashes,receipt,validate_only=args.validate_only)
    print(json.dumps(receipt))


def atomic_publish_catalog(candidate,current,source,manifest_bytes,payloads,candidate_files,original_hashes,receipt,*,validate_only=False):
    """One rollback-preserving publication path for generated catalog formats."""
    manifest_path=current/RESOURCE_FILES['manifest']
    published_sha=hashlib.sha256(manifest_bytes).hexdigest()
    changed=receipt['changed'];written=[];removed=[]
    obsolete=sorted(set(original_hashes)-set(payloads))
    if sha(manifest_path)!=sha(source/RESOURCE_FILES['manifest']):
        raise ValueError('The public catalog changed before the atomic publication.')
    if any(sha(checked_catalog_path(source,file))!=digest for file,digest in original_hashes.items()):
        raise ValueError('An immutable rollback source changed before publication.')
    if validate_only:
        if any(sha(checked_catalog_path(candidate,candidate_files[file]))!=payloads[file]for file in changed):
            raise ValueError('A candidate payload changed before publication validation.')
        receipt.update(status='PASS_PUBLICATION_VALIDATION',plannedWrites=len(changed)+1,plannedRetirements=obsolete,
            written=0,retired=[],sourceBytesPreserved=True)
        write(require_workspace()/'PublicationValidation.json',receipt)
        return
    try:
        for file in changed+[RESOURCE_FILES['manifest']]:
            temporary=current/(file+'.new-'+uuid.uuid4().hex)
            try:
                checked_catalog_path(current,temporary.name)
                if file==RESOURCE_FILES['manifest']:temporary.write_bytes(manifest_bytes)
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
        if isinstance(error,OSError)and not written and not removed:
            receipt.update(status='FAIL_NO_PUBLICATION',error=str(error))
            raise
        for file in removed:os.link(checked_catalog_path(source,file),checked_catalog_path(current,file))
        for file in reversed(written):
            if file not in original_hashes:continue
            temporary=current/(file+'.rollback-'+uuid.uuid4().hex)
            os.link(checked_catalog_path(source,file),temporary);os.replace(temporary,current/file)
        temporary=current/(manifest_path.name+'.rollback-'+uuid.uuid4().hex)
        os.link(checked_catalog_path(source,RESOURCE_FILES['manifest']),temporary);os.replace(temporary,manifest_path)
        for file in written:
            if file not in original_hashes and file!=RESOURCE_FILES['manifest']:checked_catalog_path(current,file).unlink()
        receipt.update(status='FAIL_ROLLED_BACK',error=str(error))
        raise
    finally:write(require_workspace() /'Publication.json',receipt)


def exact_main_storage_bytes(operations):
    latest={(operation['file'],texture['object']):texture for operation in operations for texture in operation['textures']}
    return sum(mip['width']*mip['height']*4 for texture in latest.values()for mip in texture['mips']if mip['codec'].get('storage')=='RGBA32')


def validate_main_calibration(texture,change):
    """Apply the encoder's alpha, RGB and storage contract to its saved proof."""
    mips=texture['mips'];texels=sum(mip['width']*mip['height']for mip in mips)
    if not texels or texture.get('preCalibrationAlphaAllMipsExact')is not True:
        raise ValueError('A Main colour mutation has no complete alpha-preserved mip chain.')
    exact=all(mip['codec'].get('storage')=='RGBA32'for mip in mips)
    if any(mip['codec'].get('storage')=='RGBA32'for mip in mips)!=exact:
        raise ValueError('A Main colour mutation mixes texture storage formats.')
    if exact and texels*4>EXACT_MAIN_TEXTURE_MAX_BYTES:raise ValueError('An exact Main texture exceeds its storage budget.')
    if change:
        before,after=change['before'],change['after'];format=4 if exact else 25
        size=texels*4 if exact else sum(((mip['width']+3)//4)*((mip['height']+3)//4)*16 for mip in mips)
        keys={'m_TextureFormat','m_CompleteImageSize','m_StreamData'}
        if set(before)!=keys or set(after)!=keys or before['m_TextureFormat']not in (4,25)or after['m_TextureFormat']!=format or \
            change.get('exactRgbaMipChain')!=exact or change['additionalBytes']!=size or \
            after['m_CompleteImageSize']!=size or after['m_StreamData']['size']!=size or after['m_StreamData']['offset']%16 or \
            before['m_StreamData']['path']!=after['m_StreamData']['path']:
            raise ValueError('Main storage changed unowned metadata or its mip layout.')
    rms=math.sqrt(sum(mip['codec']['rootMeanSquareRgbError']**2*mip['width']*mip['height']for mip in mips)/texels)
    if not math.isfinite(rms)or rms>MAIN_RGB_RMS_LIMIT or not math.isclose(rms,texture['allMipTexelWeightedRgbRms'],abs_tol=1e-12):
        raise ValueError('A Main colour mutation exceeds its RGB error bound.')
    for index,mip in enumerate(mips):
        codec=mip['codec']
        exact_mip=exact and codec.get('sourceOwnedBlocksExact')is True and codec['maximumRgbError']==0 and codec['rootMeanSquareRgbError']==0
        encoded=codec.get('partitionsAndIndicesRetained')is True or (codec.get('unownedBlocksExact')is True and
            0<codec.get('reencodedRgbBlocks',0)<=codec.get('changedBlocks',0)<=codec.get('blocks',0))
        if codec.get('alphaExact')is not True or codec.get('unownedBlocksExact')is False or (exact and not exact_mip)or (not exact and not encoded)or \
            (index<3 and codec['rootMeanSquareRgbError']>MAIN_RGB_RMS_LIMIT)or \
            mip['tone'].get('policy')!='surface-local-v4'or any(mip['tone'].get(flag)is not True for flag in
                ('preToneAlphaExact','c1WeightOneRgbaExact','unmappedRawRgbaExact','finiteRange','sourceC1BaselineUsed')):
            raise ValueError('A Main colour mutation changed protected colour channels.')


def validate_groom_shading(proof,file,before,after):
    """Validate native geometry retention across a groom normal-field migration."""
    flags=('nativeTextureCoordinatesExact','nativeGeometryFittingAndSkinningExact',
        'unrelatedObjectsExact','serializedObjectOwnershipExact')
    if proof.get('status')!='PASS_SAVED_GROOM_SHADING_ONLY'or proof.get('file')!=file or \
        proof.get('sourceSha256')!=before or proof.get('sha256')!=after or any(proof.get(flag)is not True for flag in flags):
        raise ValueError('A groom shading mutation lost its native source or object ownership.')
    meshes=proof.get('groomSupport',[])
    if not meshes or any(mesh.get('normalCarryUv')!=0 or mesh.get('normalCarryComponents')!='zw'or
        any(mesh.get(flag)is not True for flag in ('nativeVertexValuesExact','originalMorphsAndSkinningUnchanged',
            'nativeUv0XYExact','metallicPixelsUseNativeFrame'))for mesh in meshes):
        raise ValueError('A groom shading mutation changed native geometry, texture coordinates or morphs.')


def register_groom_shading_proofs(assembly):
    """Carry saved batch receipts into the same per-resource mutation chain."""
    stage=assembly.get('groomShading')
    if not stage:return
    producers=stage.get('producers',[])
    # Receipts belong to the resource stage. Producer byte archives may be
    # relocated independently and do not own the saved resource directory.
    root=stage.get('proofRoot')
    if root is None:
        roots={Path(pin['path']).resolve().parent.parent for pin in producers}
        if len(roots)!=1:raise ValueError('A groom preparation has no explicit saved receipt owner.')
        root=next(iter(roots))
    root=checked_catalog_path(Path(root));inputs=producers+[stage['sourceCatalog']]
    for pin in inputs:
        if sha(pin['path'])!=pin['sha256']:raise ValueError('A groom preparation authority changed.')
    verified={}
    for row in assembly['proofs']:
        path=root/'Work'/(row['file']+'.json')
        if not path.is_file():continue
        if any(operation.get('kind')=='groom-normal-transport'for operation in row.get('appearanceFinishing',[])):continue
        proof=read(path)
        validate_groom_shading(proof,row['file'],proof.get('sourceSha256'),row['sha256'])
        target=root/'Catalog'/row['file']
        operation=dict(kind='groom-normal-transport',beforeSha256=proof['sourceSha256'],sha256=proof['sha256'],
            proof=dict(path=str(path),sha256=sha(path)),target=dict(path=str(target),sha256=proof['sha256']),authority=dict(inputs=inputs))
        row.setdefault('appearanceFinishing',[]).append(operation)
        try:validate_prepared_resource(row,assembly,verified)
        except Exception:
            row['appearanceFinishing'].pop();raise


def validate_compiled_shader_proof(proof,validation):
    """Tie the adopted shader object to its validated compiler output."""
    import character_resources as resources
    compiled=proof.get('compiledSource')
    if not compiled or validation.get('bundle')!=compiled:
        raise ValueError('A prepared shader differs from its validated compiler output.')
    files=[]
    for reference in (proof['source'],proof['target'],compiled):
        if sha(reference['path'])!=reference['sha256']:
            raise ValueError('A compiled shader input changed.')
        _,file=resources.load_serialized_bundle(Path(reference['path']),in_memory=True);files.append(file)
    before,after,compiler=files
    if before.name!=after.name or set(before.objects)!=set(after.objects):
        raise ValueError('A compiled shader changed its serialized object ownership.')
    shader_id=proof.get('changedObject')
    if shader_id not in before.objects or before.objects[shader_id].type.name!='Shader' or after.objects[shader_id].type.name!='Shader':
        raise ValueError('A prepared shader has no matching serialized shader owner.')
    target=after.objects[shader_id].read_typetree()
    candidates=[obj for obj in compiler.objects.values()if obj.type.name=='Shader'and
        obj.read_typetree()['m_ParsedForm']['m_Name']==target['m_ParsedForm']['m_Name']]
    if len(candidates)!=1 or candidates[0].read_typetree()!=target or target.get('m_Dependencies'):
        raise ValueError('The adopted shader object differs from its validated compiler output.')
    for key,obj in before.objects.items():
        if key!=shader_id and resources.serialized_object_bytes(obj)!=resources.serialized_object_bytes(after.objects[key]):
            raise ValueError('A compiled shader mutation changed another serialized object.')


def validate_retained_skin_prefix(native, packet):
    """Keep original skin samples and every physical channel across refinement."""
    count=native['vertexCount']
    if count>packet['vertexCount']or native['bindPoses']!=packet['bindPoses']:
        raise ValueError('A skin surface changed its native prefix or bone palette.')
    for channel in ('vertices','normals','tangents','weights','boneIndices','colors'):
        if native[channel]!=packet[channel][:len(native[channel])]:
            raise ValueError('A skin surface changed a retained vertex channel: '+channel)
    if len(native['uv'])!=len(packet['uv'])or len(native['shapes'])!=len(packet['shapes']):
        raise ValueError('A skin surface changed its UV or physical channel identities.')
    for old,new in zip(native['uv'],packet['uv']):
        if old['channel']!=new['channel']or old['dimension']!=new['dimension']or old['values']!=new['values'][:len(old['values'])]:
            raise ValueError('A skin surface changed retained UV samples.')
    for old,new in zip(native['shapes'],packet['shapes']):
        if (old['name'],old['index'],len(old['frames']))!=(new['name'],new['index'],len(new['frames'])):
            raise ValueError('A skin surface changed physical channel identity.')
        for first,second in zip(old['frames'],new['frames']):
            if first['weight']!=second['weight']or any(first[channel]!=second[channel][:count*3]for channel in ('vertices','normals','tangents')):
                raise ValueError('A skin surface changed retained physical frame samples.')


def validate_compiled_skin_surface(receipt):
    """Check saved skin meshes against compiler output and their retained prefix."""
    import gzip
    import numpy as np
    import character_resources as resources
    _,before=resources.load_serialized_bundle(receipt['source']['path'],in_memory=True)
    _,after=resources.load_serialized_bundle(receipt['target']['path'],in_memory=True)
    _,compiled=resources.load_serialized_bundle(receipt['compiledBundle']['path'],in_memory=True)
    if before.name!=after.name or set(before.objects)!=set(after.objects)or \
            [entry.path for entry in before.externals]!=[entry.path for entry in after.externals]:
        raise ValueError('A compiled skin surface changed serialized identity or external references.')
    selected={mesh['meshPathId']:mesh for mesh in receipt['meshes']}
    if not selected or set(receipt['changedObjects'])!=set(selected):
        raise ValueError('A compiled skin surface changed an undeclared object.')
    for key,obj in before.objects.items():
        if key not in selected:
            if resources.serialized_object_bytes(obj)!=resources.serialized_object_bytes(after.objects[key]):
                raise ValueError('A compiled skin surface changed an unrelated object.')
            continue
        mesh=selected[key]
        with gzip.open(mesh['nativePacket']['path'],'rt',encoding='utf-8')as stream:native=json.load(stream)
        with gzip.open(mesh['packet']['path'],'rt',encoding='utf-8')as stream:packet=json.load(stream)
        if obj.type.name!='Mesh'or after.objects[key].type.name!='Mesh'or \
                export_current_mesh(obj)['sourceFingerprint']!=native['sourceFingerprint']or \
                export_current_mesh(after.objects[key])['sourceFingerprint']!=packet['sourceFingerprint']:
            raise ValueError('A compiled skin surface differs from its source or saved numeric channels.')
        matches=[value for value in compiled.objects.values()if value.type.name=='Mesh'and value.peek_name()==mesh['name']]
        if len(matches)!=1 or export_current_mesh(matches[0])['sourceFingerprint']!=packet['sourceFingerprint']:
            raise ValueError('A saved skin surface differs from its compiled mesh.')
        count=native['vertexCount']
        if packet['vertexCount']!=mesh['authoredVertices']or count!=mesh['nativeVertices']:
            raise ValueError('A compiled skin surface changed its prefix or bone palette.')
        validate_retained_skin_prefix(native,packet)
        if not np.isfinite(np.asarray(packet['vertices'])).all():raise ValueError('A compiled skin surface is nonfinite.')



def native_surface_restoration_sources(index,manifest,proofs,rows,owner,load,*,target_packets=None,target_bindings=None,target_palettes=None):
    """Resolve original mesh authority through the exact prefab and renderer."""
    import numpy as np
    import character_resources as resources
    declarations={row['source']:row for row in manifest['resources']}
    assets={declarations[row['resource']]['asset']for row in rows}
    audit=resources.audit_native_bone_orders(index,assets)
    if audit['status']!='PASS_NATIVE_SKIN_SLOT_AUDIT':
        raise ValueError('A native surface restoration has incomplete original prefab ownership.')
    authorities=audit['rows']
    originals={row['cab']:row for row in index['bundles']}
    native_manifest=dict(resources=[dict(source=part['sourceKey'],asset=part['asset'],bundles=[dict(file=row['file'])])
        for row in index['bundles']for part in row.get('parts',[])])
    native_proofs={row['file']:dict(newCab=row['cab'])for row in index['bundles']}
    @lru_cache(maxsize=4)
    def native_load(cab):
        pin=originals[cab]
        if sha(pin['source'])!=pin['sha256']:raise ValueError('An original native surface bundle changed.')
        return resources.load_serialized_bundle(pin['source'],in_memory=True)[1]
    def canonical(paths,permutation):
        if not isinstance(permutation,list)or len(permutation)!=len(paths)or \
                any(type(index)is not int for index in permutation)or sorted(permutation)!=list(range(len(paths))):
            raise ValueError('A native surface authority has no complete Mesh-slot bone permutation.')
        result=[]
        for index in permutation:
            value=paths[index]
            parts=value.split('/');result.append('/'.join(parts[parts.index('Bip001'):]if'Bip001'in parts else parts))
        return result
    current_prefabs={};native_prefabs={};packets={}
    simulated=set()
    if target_bindings is not None and target_packets is None:raise ValueError('A native attachment requires its restored target packet.')
    for obj in owner.objects.values():
        if obj.type.name!='MonoBehaviour':continue
        deformer=obj.read_typetree().get('deformer')
        pointer=deformer.get('sharedMesh',{})if isinstance(deformer,dict)else{}
        if pointer.get('m_FileID')==0:simulated.add(pointer.get('m_PathID'))
    for row in rows:
        resource=row['resource'];asset=declarations[resource]['asset']
        if resource not in current_prefabs:
            current_prefabs[resource]=resource_prefab_skins(manifest,proofs,resource,load)
            native_prefabs[resource]=resource_prefab_skins(native_manifest,native_proofs,resource,native_load)
        current_skins,current_resolve=current_prefabs[resource];native_skins,native_resolve=native_prefabs[resource]
        current_matches=[renderer for relative,renderer in current_skins if relative==row['renderer']]
        native_matches=[renderer for relative,renderer in native_skins if relative==row['renderer']]
        if len(current_matches)!=1 or len(native_matches)!=1:
            raise ValueError('A native surface restoration has no unique prefab renderer.')
        current_renderer=current_matches[0];native_renderer=native_matches[0]
        cf,co=current_resolve(current_renderer.assets_file,current_renderer.read_typetree()['m_Mesh'])
        nf,no=native_resolve(native_renderer.assets_file,native_renderer.read_typetree()['m_Mesh'])
        matches=[value for value in authorities if value['asset']==asset and value['renderer']==native_renderer.path_id and value['nativeMesh']==row['nativeMesh']]
        authority=matches[0]if len(matches)==1 else None
        if cf.name!=owner.name or co.path_id!=row['meshPathId'] or authority is None or \
                no.path_id!=authority['nativeMesh']['pathId'] or \
                dict(path=originals[nf.name]['source'],sha256=originals[nf.name]['sha256'],pathId=no.path_id)!=row['nativeMesh']:
            raise ValueError('A native surface restoration changed its original prefab mesh authority.')
        native=export_current_mesh(no);current=export_current_mesh(co)
        current_bones=resources.skin_bone_paths(current_renderer.assets_file,current_renderer.read_typetree(),current_resolve)
        bones=canonical(authority['sourceBonePaths'],authority.get('permutation'));face_recovery='faceRecovery'in row
        if len(native['bindPoses'])!=len(bones)*16 or len(current['bindPoses'])!=len(current_bones)*16 or \
                (current_bones!=bones or current['bindPoses']!=native['bindPoses'])and not(face_recovery and
                current_bones[:len(bones)]==bones and current['bindPoses'][:len(native['bindPoses'])]==native['bindPoses']):
            raise ValueError('A native surface restoration has incompatible renderer bones or bind poses.')
        if current['sourceFingerprint']!=row['currentFingerprint']:
            raise ValueError('A native surface restoration has another current source.')
        if row['meshPathId']in packets:raise ValueError('A native surface restoration repeats its mesh owner.')
        packets[row['meshPathId']]=native
        if target_packets is not None:target_packets[row['meshPathId']]=native
        if 'faceRecovery'not in row:continue
        import gzip
        declaration=row['faceRecovery']
        if target_packets is None or not isinstance(declaration,dict)or set(declaration)!={
                'retainedFacePacket','nativeHeadBonePath','discardNonNativeChannels','refinement','producerSources'}:
            raise ValueError('Owned face recovery requires its retained target, native bone owner and producer inputs.')
        pins=declaration['producerSources']
        if not isinstance(pins,dict)or set(pins)!={'production','art'}or any(sha(pin['path'])!=pin['sha256']for pin in pins.values()):
            raise ValueError('An owned face recovery producer changed.')
        shape,_=source_owner(production_path=pins['production']['path'],art_path=pins['art']['path'])
        pin=declaration['retainedFacePacket']
        if sha(pin['path'])!=pin['sha256']:raise ValueError('A retained facial surface packet changed.')
        with gzip.open(pin['path'],'rt',encoding='utf-8')as stream:retained=json.load(stream)
        if retained!=current:raise ValueError('A retained facial surface differs from its current resource owner.')
        count=native['vertexCount']
        if count>current['vertexCount']or len(native['uv'])!=len(current['uv']):
            raise ValueError('A retained face has no true native coarse correspondence.')
        for first,last in zip(native['uv'],current['uv']):
            if (first['channel'],first['dimension'],first['values'])!=(last['channel'],last['dimension'],last['values'][:len(first['values'])]):
                raise ValueError('A retained face changed its native coarse UV correspondence.')
        # The typed native renderer owns the physical coordinate frame, just
        # as it owns the external mesh and ordered bone palette.
        renderer_frames=[resources.renderer_frame(renderer,resolver)for renderer,resolver in
            ((native_renderer,native_resolve),(current_renderer,current_resolve))]
        if not np.array_equal(*renderer_frames):raise ValueError('A retained face changed its original renderer coordinate frame.')
        frame=renderer_frames[0]
        # A mesh replacement and palette update affect every local renderer
        # referencing that Mesh, including another consumer in the same prefab.
        consumers={identifier for identifier,obj in owner.objects.items()if obj.type.name=='SkinnedMeshRenderer'and
            obj.read_typetree()['m_Mesh']==dict(m_FileID=0,m_PathID=co.path_id)}
        scoped={renderer.path_id for _,renderer in current_skins if renderer.assets_file.name==owner.name and renderer.path_id in consumers}
        if scoped!=consumers:raise ValueError('A recovered face Mesh has an undeclared local renderer consumer.')
        for relative,consumer in current_skins:
            if consumer.assets_file.name!=owner.name or consumer.path_id not in consumers:continue
            original_matches=[renderer for path,renderer in native_skins if path==relative]
            if len(original_matches)!=1:raise ValueError('A recovered face consumer has no unique native renderer authority.')
            original=original_matches[0]
            of,om=native_resolve(original.assets_file,original.read_typetree()['m_Mesh'])
            original_pin=dict(path=originals[of.name]['source'],sha256=originals[of.name]['sha256'],pathId=om.path_id)
            matches=[value for value in authorities if value['asset']==asset and value['renderer']==original.path_id and value['nativeMesh']==original_pin]
            if len(matches)!=1 or original_pin!=row['nativeMesh']or canonical(matches[0]['sourceBonePaths'],matches[0].get('permutation'))!=bones:
                raise ValueError('A recovered face Mesh has conflicting native consumer palettes or Mesh authority.')
            paths=resources.skin_bone_paths(consumer.assets_file,consumer.read_typetree(),current_resolve)
            if paths!=current_bones or not np.array_equal(resources.renderer_frame(consumer,current_resolve),resources.renderer_frame(original,native_resolve))or \
                    not np.array_equal(resources.renderer_frame(original,native_resolve),frame):
                raise ValueError('A recovered face Mesh has conflicting consumer palettes or renderer coordinate frames.')
        if target_palettes is not None:target_palettes[row['meshPathId']]=dict(sourceBonePaths=current_bones,bonePaths=bones)
        materials=[]
        for pointer in native_renderer.read_typetree()['m_Materials']:
            if not pointer['m_PathID']:materials.append(None);continue
            mf,material=native_resolve(native_renderer.assets_file,pointer)
            sf,shader=native_resolve(mf,material.read_typetree()['m_Shader'])
            materials.append(dict(shader=shader.read_typetree()['m_ParsedForm']['m_Name']))
        skin=dict(mesh=native['name'],path=row['renderer'],bones=bones,materials=materials,rendererToPrefab=frame.T.ravel().tolist())
        root=declaration['nativeHeadBonePath']
        head=np.zeros(count,bool);head[shape.native_head_vertices(native,skin,root)]=True
        included=np.array([bool(name)and(name==root or name.startswith(root+'/'))for name in bones])
        indices=np.asarray(native['boneIndices'],int).reshape(-1,4);weights=np.asarray(native['weights'],float).reshape(-1,4)
        owned=(head&(np.sum(weights*~included[indices],axis=1)==0)).astype(float)
        if not np.any(owned):raise ValueError('An owned face recovery has no exclusive native facial tissue.')
        refinement=declaration['refinement']
        if not isinstance(refinement,dict)or set(refinement)!={'lod','passes','profile'}or type(refinement['passes'])is not int or not 0<=refinement['passes']<=4 or \
                type(refinement['lod'])is not int or refinement['lod']not in(0,1,2)or refinement['lod']!=shape.lod_index(skin):
            raise ValueError('Owned face recovery requires bounded native surface refinement.')
        regions=[];working=native
        if refinement['lod']>0 and any(material and material['shader'].startswith('Lux/Human/Skin')for material in materials):
            donors=[]
            for relative,renderer in native_skins:
                if shape.lod_index(dict(path=relative))!=0:continue
                donor_materials=[]
                for pointer in renderer.read_typetree()['m_Materials']:
                    if not pointer['m_PathID']:continue
                    mf,material=native_resolve(renderer.assets_file,pointer)
                    _,shader=native_resolve(mf,material.read_typetree()['m_Shader'])
                    donor_materials.append(shader.read_typetree()['m_ParsedForm']['m_Name'])
                if not any(shader.startswith('Lux/Human/Skin')for shader in donor_materials):continue
                df,dm=native_resolve(renderer.assets_file,renderer.read_typetree()['m_Mesh'])
                donor_pin=dict(path=originals[df.name]['source'],sha256=originals[df.name]['sha256'],pathId=dm.path_id)
                matches=[value for value in authorities if value['asset']==asset and value['renderer']==renderer.path_id and value['nativeMesh']==donor_pin]
                if len(matches)!=1:raise ValueError('A lower native face contact owner has no unique Mesh-slot authority.')
                donor_bones=canonical(matches[0]['sourceBonePaths'],matches[0].get('permutation'))
                if root not in donor_bones or not any(path.rsplit('/',1)[-1].casefold().startswith('b_eyelid_')for path in donor_bones):continue
                donor_skin=dict(mesh=dm.peek_name(),path=relative,bones=donor_bones,
                    rendererToPrefab=resources.renderer_frame(renderer,native_resolve).T.ravel().tolist())
                donors.append((export_current_mesh(dm),donor_skin))
            if len(donors)!=1:raise ValueError('A lower native face has no unique LOD0 contact owner.')
            working,_=shape.recover_source_lod_frames(native,skin,*donors[0])
            regions=shape.humanoid_face_contact_regions(*donors[0])
            if not regions:raise ValueError('A lower native face has no LOD0 ocular contact surface.')
        else:
            working,_=shape.recover_source_lod_frames(native,skin,native,skin)
        target=copy.deepcopy(working)
        for key,width in(('vertices',3),('normals',3),('tangents',4)):target[key]=current[key][:count*width]
        channels={value['name']:value for value in current['shapes']}
        if len(channels)!=len(current['shapes']):raise ValueError('A retained face has ambiguous physical channel names.')
        native_channels={value['name']for value in native['shapes']};discard=declaration['discardNonNativeChannels']
        if not isinstance(discard,list)or len(discard)!=len(set(discard))or set(discard)!=set(channels)-native_channels:
            raise ValueError('Non-native Head channels require an explicit retained-source declaration.')
        for channel in target['shapes']:
            prepared=channels.get(channel['name'])
            if prepared is None:raise ValueError('A retained face lost a native physical channel.')
            frames={value['weight']:value for value in prepared['frames']}
            if len(frames)!=len(prepared['frames'])or set(frames)!={value['weight']for value in channel['frames']}:
                raise ValueError('A retained face changes its native physical frame weight set.')
            for value in channel['frames']:
                for key in('vertices','normals','tangents'):value[key]=frames[value['weight']][key][:count*3]
        target['sourceFingerprint']=fingerprint(target)[0];target['bounds']=shape.physical_mesh_bounds(target)
        # A lower-LOD native frame can be corrupt even when the retained
        # target was already repaired. Compose against its validated source
        # field; keep the original packet as the serialized authority above.
        packet,_=shape.refine_native_face_contours(working,working,skin,refined=target,surface_weight=owned)
        ownership=owned*shape.humanoid_face_contact_guard(native,skin,regions)
        stencils=[{index:1.}for index in range(count)]if co.path_id in simulated else None
        for _ in range(refinement['passes']):
            packet,added,_=shape.refine_skin_packet(packet,skin['rendererToPrefab'],refinement['lod'],
                profile=refinement['profile'],vertex_ownership=ownership)
            if stencils is not None:
                for vertex in added:
                    if vertex['index']!=len(stencils):raise ValueError('A native subdivision changed its append-only source order.')
                    a,b=vertex['edge'];sample=collections.defaultdict(float)
                    for anchor,factor in[*stencils[a].items(),*stencils[b].items()]:sample[anchor]+=factor*.5
                    if len(sample)>3:raise ValueError('A native subdivision escaped its original triangular source.')
                    stencils.append(dict(sample))
            ownership=np.r_[ownership,[min(ownership[a],ownership[b])for a,b in(value['edge']for value in added)]]
        target_packets[row['meshPathId']]=packet
        if stencils is not None:
            from mesh_recipe_codec import source_surface_binding,source_surface_skin
            faces=[list(map(int,face))for slot in native['subMeshes']for face in np.asarray(slot,int).reshape(-1,3)]
            owners=collections.defaultdict(set)
            for ordinal,face in enumerate(faces):
                for vertex in face:owners[vertex].add(ordinal)
            surfaces=[]
            for vertex in range(count,len(stencils)):
                sample=stencils[vertex];candidates=set.intersection(*(owners[index]for index in sample))
                if not candidates:raise ValueError('A native subdivision lost its exact original face owner.')
                triangle=faces[min(candidates)]
                surfaces.append(dict(vertex=vertex,surface=dict(triangle=triangle,barycentric=[sample.get(index,0.)for index in triangle])))
            for declaration in surfaces:
                vertex=declaration['vertex'];weights,indices=source_surface_skin(native,declaration['surface']);offset=vertex*4
                before={bone:weight for bone,weight in zip(packet['boneIndices'][offset:offset+4],packet['weights'][offset:offset+4])if weight>0}
                after={bone:weight for bone,weight in zip(indices,weights)if weight>0}
                if before.keys()!=after.keys()or any(abs(before[bone]-after[bone])>1e-6 for bone in before):
                    raise ValueError('A native subdivision changes positive surface skin support.')
                packet['weights'][offset:offset+4]=weights;packet['boneIndices'][offset:offset+4]=indices
            packet['sourceFingerprint']=fingerprint(packet)[0]
            if target_bindings is not None:
                target_bindings[row['meshPathId']]=source_surface_binding(native,packet,skin['rendererToPrefab'],surfaces)
    return packets


def validate_native_surface_restoration(receipt):
    """Verify original game mesh channels and unchanged surrounding owners."""
    import gzip
    import character_resources as resources
    for pin in (receipt['source'],receipt['target'],receipt['compiledBundle'],receipt['nativeIndex'],
                receipt['sourceCatalog']['manifest'],receipt['sourceCatalog']['assembly']):
        if sha(pin['path'])!=pin['sha256']:raise ValueError('A native surface restoration input changed.')
    before_environment,before=resources.load_serialized_bundle(receipt['source']['path'],in_memory=True)
    after_environment,after=resources.load_serialized_bundle(receipt['target']['path'],in_memory=True)
    compiled=resources.load_serialized_bundle(receipt['compiledBundle']['path'],in_memory=True)[1]
    def payloads(environment):
        return {key:bytes(value)if isinstance(value,(bytes,bytearray))else value.bytes
                for key,value in environment.file.files.items()if not hasattr(value,'objects')}
    if payloads(before_environment)!=payloads(after_environment):
        raise ValueError('A native surface restoration changed a retained resource payload.')

    if before.name!=after.name or set(before.objects)!=set(after.objects)or \
            [entry.path for entry in before.externals]!=[entry.path for entry in after.externals]:
        raise ValueError('A native surface restoration changed prefab ownership.')
    manifest=read(receipt['sourceCatalog']['manifest']['path']);assembly=read(receipt['sourceCatalog']['assembly']['path'])
    if assembly['catalogSha256']!=receipt['sourceCatalog']['manifest']['sha256']:
        raise ValueError('A native surface restoration has another source catalog.')
    proofs={row['file']:row for row in assembly['proofs']};by_cab={row['newCab']:row for row in proofs.values()}
    catalog=Path(receipt['sourceCatalog']['manifest']['path']).parent
    source_owner=by_cab.get(before.name)
    if source_owner is None or source_owner['sha256']!=receipt['source']['sha256']:
        raise ValueError('A native surface restoration has another source catalog bundle.')
    @lru_cache(maxsize=4)
    def load(cab):
        pin=by_cab[cab];path=catalog/pin['file']
        if sha(path)!=pin['sha256']:raise ValueError('A native surface restoration resource changed.')
        return resources.load_serialized_bundle(path,in_memory=True)[1]
    target_packets={};target_bindings={};target_palettes={}
    native_sources=native_surface_restoration_sources(read(receipt['nativeIndex']['path']),manifest,proofs,
        receipt['restoredSurfaces'],before,load,target_packets=target_packets,target_bindings=target_bindings,target_palettes=target_palettes)
    selected={row['meshPathId']:row for row in receipt['restoredSurfaces']}
    palette_updates={row['renderer']:row for row in receipt.get('rendererBoneUpdates',[])}
    if len(palette_updates)!=len(receipt.get('rendererBoneUpdates',[]))or any(
            row['mesh']not in selected or 'faceRecovery'not in selected[row['mesh']]for row in palette_updates.values()):
        raise ValueError('A native surface restoration changed an undeclared renderer palette.')
    expected_palettes={}
    for identifier,obj in before.objects.items():
        if obj.type.name!='SkinnedMeshRenderer':continue
        tree=obj.read_typetree();pointer=tree['m_Mesh']
        if pointer['m_FileID']or pointer['m_PathID']not in target_palettes:continue
        mesh=pointer['m_PathID'];declaration=target_palettes[mesh]
        expected,proof=resources.extend_skin_bone_palette(before,tree,declaration['sourceBonePaths'],
            declaration['bonePaths'],packet=target_packets[mesh])
        expected_palettes[identifier]=dict(proof,renderer=identifier,mesh=mesh)
        if resources.serialized_object_bytes(obj,tree=expected)!=resources.serialized_object_bytes(after.objects[identifier]):
            raise ValueError('A recovered face renderer palette differs from its native slot target.')
    if palette_updates!=expected_palettes:
        raise ValueError('A recovered face palette ledger omits or changes a source Mesh consumer.')
    allowed=set(selected)|set(palette_updates)
    if target_bindings:allowed.update(key for key,obj in before.objects.items()if obj.type.name=='MonoBehaviour')
    if not selected or len(selected)!=len(receipt['restoredSurfaces'])or not set(selected)<=set(receipt['changedObjects'])<=allowed:
        raise ValueError('A native surface restoration changed prefab ownership.')
    replay_replacements={}
    for key,obj in before.objects.items():
        if key not in selected:
            if key in palette_updates:
                if obj.type.name!='SkinnedMeshRenderer':raise ValueError('A recovered face palette has another renderer owner.')
                continue
            if target_bindings and obj.type.name=='MonoBehaviour':continue
            if resources.serialized_object_bytes(obj)!=resources.serialized_object_bytes(after.objects[key]):
                raise ValueError('A native surface restoration changed an unrelated object.')
            continue
        row=selected[key];pin=row['nativePacket']
        if sha(pin['path'])!=pin['sha256']:raise ValueError('A retained native surface packet changed.')
        with gzip.open(pin['path'],'rt',encoding='utf-8')as stream:native=json.load(stream)
        if native_sources[key]!=native:
            raise ValueError('A retained surface packet differs from its original source bundle mesh.')
        current=export_current_mesh(obj)
        if obj.type.name!='Mesh'or current['sourceFingerprint']!=row['currentFingerprint']:
            raise ValueError('A native surface restoration has another current source.')
        packet=target_packets[key]
        if 'faceRecovery'in row:
            pin=row.get('packet')
            if not isinstance(pin,dict)or sha(pin['path'])!=pin['sha256']:raise ValueError('A recovered face packet changed.')
            with gzip.open(pin['path'],'rt',encoding='utf-8')as stream:retained=json.load(stream)
            if retained!=packet:raise ValueError('A recovered face differs from its native authority and retained target.')
        matches=[value for value in compiled.objects.values()if value.type.name=='Mesh'and value.peek_name()==row['name']]
        if len(matches)!=1 or export_current_mesh(matches[0])['sourceFingerprint']!=packet['sourceFingerprint']or \
                export_current_mesh(after.objects[key])['sourceFingerprint']!=packet['sourceFingerprint']:
            raise ValueError('A restored surface differs from its complete native numeric source.')
        replacement=dict(sourceFingerprint=current['sourceFingerprint'],sourcePathIds=[key],packet=packet,
            binding=target_bindings.get(key),compiledTree=matches[0].read_typetree())
        if key in target_palettes:replacement.update(target_palettes[key])
        if key in target_bindings:replacement['nativeClothSource']=row['nativeMesh']
        replay_replacements[key]=replacement
    if target_bindings:
        import tempfile
        with tempfile.TemporaryDirectory(prefix='native-surface-replay-',dir=Path(receipt['target']['path']).parent)as directory:
            replay=resources.prepare_standalone_part(receipt['source']['path'],receipt['source']['sha256'],Path(directory)/'saved.bundle',
                [replay_replacements[key]for key in selected],export_mesh=export_current_mesh)
            replay=json.loads(json.dumps(replay,allow_nan=False))
            stored=json.loads(json.dumps(receipt,allow_nan=False))
            differences=['targetSha256']if replay['target']['sha256']!=receipt['target']['sha256']else[]
            differences.extend(field for field in
                ('meshes','cloth','nativeClothSources','changedObjects','objectsAdded','rendererBoneUpdates','rendererMaterialUpdates','materialFloatUpdates')
                if replay[field]!=stored.get(field))
            if differences:
                raise ValueError('A native surface lost its complete source deformation transaction: '+', '.join(differences))
    if any(receipt.get(flag)is not True for flag in ('allOtherObjectBytesExact','resourcePayloadsExact','cabAndExternalReferencesUnchanged','savedObjectBytesExact')):
        raise ValueError('A native surface restoration lost its serialized transaction proof.')


def missing_groom_domains(source,*,file=None):
    """Use the highest prepared renderer surface of each material to complete native LODs."""
    import numpy as np
    import material_fields as fields
    import character_resources as resources
    import importlib.util
    if file is None:file=resources.load_serialized_bundle(source,in_memory=True)[1]
    plan=resources.groom_lod_plan(file);ids=plan['meshes']
    absent=[key for key in ids if file.objects[key].read_typetree()['m_VertexData']['m_Channels'][6]['dimension']!=4]
    if not absent:return []
    renderers={}
    for obj in file.objects.values():
        if obj.type.name!='SkinnedMeshRenderer':continue
        tree=obj.read_typetree();pointer=tree['m_Mesh']
        if not pointer['m_FileID']and pointer['m_PathID']in ids:renderers[pointer['m_PathID']]=tree
    def local(owner,pointer):
        if pointer['m_FileID']:raise ValueError('A native groom rig has external bone ownership.')
        return owner,owner.objects[pointer['m_PathID']]
    rows=[]
    for key in sorted(absent):
        tree=file.objects[key].read_typetree()
        if tree['m_VertexData']['m_Channels'][6]['dimension']:raise ValueError('A missing groom domain has another existing UV2 owner.')
        materials=renderers[key]['m_Materials']
        donors=[plan['donors'][key]]if key in plan['donors']else[candidate for candidate in ids if candidate not in absent and renderers[candidate]['m_Materials']==materials]
        if not donors:raise ValueError('A missing groom LOD has no prepared surface with the same material owner.')
        donor=max(donors,key=lambda index:file.objects[index].read_typetree()['m_VertexData']['m_VertexCount'])
        own=export_current_mesh(file.objects[key]);prepared=export_current_mesh(file.objects[donor])
        bones=resources.skin_bone_paths(file,renderers[key],local);donor_bones=resources.skin_bone_paths(file,renderers[donor],local)
        frame,_,_=fields.skin_palette_frame(own,prepared,bones,donor_bones)
        coordinates=np.asarray(next(u['values']for u in prepared['uv']if u['channel']==2)).reshape(-1,4)
        count=own['vertexCount'];values=np.zeros((count,4),dtype='<f4');values[:,3]=fields.array(own,'tangents',4)[:,3]
        positions=fields.points(fields.array(own,'vertices',3),frame)
        vertices=np.r_[positions,fields.array(prepared,'vertices',3)]
        native_uv=np.r_[np.asarray(next(u['values']for u in own['uv']if u['channel']==0)).reshape(-1,4),np.asarray(next(u['values']for u in prepared['uv']if u['channel']==0)).reshape(-1,4)]
        groom_uv=np.r_[values,coordinates];slots=[]
        for original,reference in zip(own['subMeshes'],prepared['subMeshes']):
            triangles=np.asarray(reference,int).reshape(-1,3);roles=coordinates[triangles,2]
            fibers=triangles[np.all((roles>=.5)&(roles<64.5),axis=1)]
            slots.append(list(original)+(fibers+count).ravel().tolist())
        packet=dict(vertices=vertices.ravel().tolist(),subMeshes=slots,uv=[dict(channel=0,values=native_uv.ravel().tolist()),dict(channel=2,values=groom_uv.ravel().tolist())])
        path=ROOT/'tools/blender-models/CharacterHair/GenerateFlowCoverageSweptProfile.py'
        spec=importlib.util.spec_from_file_location('missing_native_groom_domain',path)
        if spec is None or spec.loader is None:raise ValueError('The shared native groom coordinate producer is unavailable.')
        producer=importlib.util.module_from_spec(spec);spec.loader.exec_module(producer)
        metrics=producer.initialize_core_coordinates(packet,np.eye(4).ravel())
        if sum(row['coreVerticesWithMetricCoordinates']for row in metrics)!=count:raise ValueError('A native groom LOD has no complete measured strand domain.')
        values=np.asarray(packet['uv'][1]['values'],dtype='<f4').reshape(-1,4)[:count].copy()
        rows.append(dict(object=key,donor=donor,coordinates=values,nativeCoreRole=True,coordinateAuthority='Actual native UV charts conditioned by the prepared source fiber axes',charts=metrics))
    return rows


def complete_groom_frames():
    import character_resources as resources
    parser=argparse.ArgumentParser();parser.add_argument('--source',type=Path,required=True);args=parser.parse_args()
    source=args.source.resolve();output=require_workspace();catalog=output/'Catalog';catalog.mkdir(exist_ok=True)
    if source==output or(output/'ResourceAssembly.json').exists():raise ValueError('Groom completion needs an independent unfinished output.')
    assembly=read(source/'ResourceAssembly.json');manifest=read(source/(Path('Catalog')/RESOURCE_FILES['manifest']));proofs={row['file']:row for row in assembly['proofs']}
    pin=lambda path:dict(path=str(Path(path).resolve()),sha256=sha(path))
    pins=[pin(source/'ResourceAssembly.json'),pin(source/(Path('Catalog')/RESOURCE_FILES['manifest']))]
    for original in(Path(__file__),Path(resources.__file__),Path(__file__).with_name('material_fields.py'),ROOT/'tools/blender-models/CharacterHair/GenerateFlowCoverageSweptProfile.py'):
        archive=output/'ProducerSources'/original.name;archive.parent.mkdir(exist_ok=True);archive.write_bytes(original.read_bytes());pins.append(pin(archive))
    changed=[];total=0;parameter_updates=[];scanned=0
    for bundle in manifest['bundles']:
        name=bundle['file'];original=source/'Catalog'/name;target=catalog/name
        row=proofs[name]
        if not row.get('groomSupport'):
            if not target.exists():target.hardlink_to(original)
            continue
        file=resources.load_serialized_bundle(original,in_memory=True)[1]
        domains=missing_groom_domains(original,file=file);scanned+=1
        if scanned%50==0:print('Audited groom resources:',scanned,flush=True)
        if domains:
            coordinates=[]
            for domain in domains:
                path=output/'Domains'/(str(domain['object'])+'.f32');path.parent.mkdir(exist_ok=True);path.write_bytes(domain['coordinates'].tobytes());coordinates.append(dict(object=domain['object'],coordinates=pin(path)))
            job=dict(source=str(original.resolve()),target=str(target.resolve()),sha256=row['sha256'],codeSha256=sha(resources.__file__),receipt=str((output/'Receipts'/(name+'.json')).resolve()),gpuBridge=True,missingOnly=True,coordinates=coordinates,materialSources=[dict(object=key,source=value)for key,value in resources.groom_lod_plan(file)['materials'].items()])
            result=resources.refine_groom_resource_frames(job)
            validate_groom_vertex_frames(result)
            row.update(sha256=result['sha256'],bytes=result['bytes'],allRetainedObjectFieldsExactAfterReferenceRebasing=False)
            row.setdefault('appearanceFinishing',[]).append(dict(kind='groom-vertex-normal-frame',beforeSha256=result['identity']['sourceSha256'],authority=dict(inputs=pins),**result));changed.append(result)
            environment,file=resources.load_serialized_bundle(target,in_memory=True)
            copied={row['object']for row in job['materialSources']}
            for resource in manifest['resources']:
                if resource['bundles'][0]['file']!=name:continue
                before=copy.deepcopy(resource['parameters'])
                root=resources.typed_bundle_asset(environment,resource['asset'],'GameObject')
                if hasattr(root,'deref'):root=root.deref()
                def local(owner,pointer):
                    if pointer['m_FileID']:raise ValueError('A completed groom prefab has an external hierarchy owner.')
                    return owner,owner.objects[pointer['m_PathID']]
                for path,renderer in resources.prefab_skin_renderers(root,local):
                    for slot,pointer in enumerate(renderer.read_typetree()['m_Materials']):
                        if pointer['m_FileID']or pointer['m_PathID']not in copied:continue
                        matches=[value for value in resource['parameters']if value['renderer']==path and value['slot']==slot]
                        if matches and matches!=[dict(renderer=path,slot=slot,set='Groom')]:raise ValueError('A completed native LOD has another existing dye owner.')
                        if not matches:resource['parameters'].append(dict(renderer=path,slot=slot,set='Groom'))
                if resource['parameters']!=before:parameter_updates.append(dict(source=resource['source'],before=before,after=copy.deepcopy(resource['parameters'])))
        elif not target.exists():target.hardlink_to(original)
        total+=resources.validate_groom_frame_coverage(file)['meshes']
    for row in manifest['bundles']:row['sha256']=proofs[row['file']]['sha256']
    write(catalog/RESOURCE_FILES['manifest'],manifest);assembly.update(catalogSha256=sha(catalog/RESOURCE_FILES['manifest']),bytes=sum(r['bytes']for r in assembly['proofs']),artAccepted=False,preparedSource=dict(assembly=pin(source/'ResourceAssembly.json'),catalog=pin(source/(Path('Catalog')/RESOURCE_FILES['manifest']))),groomFrameCompletion=dict(inputs=pins,resources=len(changed),meshes=sum(len(r['meshes'])for r in changed),allGroomMeshes=total,parameterUpdates=parameter_updates,review='pending'))
    write(output/'ResourceAssembly.json',assembly);write(output/'GroomFrameCoverage.json',dict(status='PASS_SAVED_GROOM_FRAME_COVERAGE',meshes=total,resources=len(changed),completedMeshes=sum(len(r['meshes'])for r in changed),catalog=pin(catalog/RESOURCE_FILES['manifest'])))
    print('Complete groom renderer coverage:',total,'meshes;',len(changed),'resources completed.',flush=True)


def validate_groom_vertex_frames(proof):
    """Reopen generated flow frames without permitting geometry or native UV loss."""
    import character_resources as resources
    import numpy as np
    from UnityPy.helpers.MeshHelper import MeshHandler
    first_env,first=resources.load_serialized_bundle(proof['source']['path'],in_memory=True)
    last_env,last=resources.load_serialized_bundle(proof['target']['path'],in_memory=True)
    if proof.get('flowOnly'):
        gpu_bridge=proof.get('gpuBridge',False);materials=resources.groom_material_identifiers(first)
        source_formats={dict(first.objects[key].read_typetree()['m_SavedProperties']['m_Floats'])['_FlowInVertexFrame']for key in materials}
        if len(source_formats)!=1 or not source_formats<={1,2}:raise ValueError('A groom axis has an ambiguous source GPU layout.')
        source_format=int(next(iter(source_formats)));target_format=2 if gpu_bridge else source_format
        owned=resources.groom_mesh_identifiers(first,1,flow_format=source_format)
        declared_flows={row['object']:row['flow']for row in proof.get('identity',{}).get('declaredFlow',[])}
        if set(declared_flows)-owned:raise ValueError('A saved groom flow declaration lost its renderer-owned mesh.')
        if not owned or owned!=resources.groom_mesh_identifiers(last,1,flow_format=target_format)or {row['object']for row in proof['meshes']}!=owned:
            raise ValueError('A groom axis update lost its declared material/mesh ownership.')
        if first.name!=last.name or set(first.objects)!=set(last.objects)or [v.path for v in first.externals]!=[v.path for v in last.externals]:
            raise ValueError('A groom axis update changed its serialized identity or dependencies.')
        for key,obj in first.objects.items():
            if key in materials and gpu_bridge:
                before=obj.read_typetree();after=last.objects[key].read_typetree()
                floats=dict(before['m_SavedProperties']['m_Floats']);floats['_FlowInVertexFrame']=2.
                before['m_SavedProperties']['m_Floats']=list(floats.items())
                if before!=after:raise ValueError('A packed groom axis changed another material value.')
                continue
            if key not in owned:
                if resources.serialized_object_bytes(obj)!=resources.serialized_object_bytes(last.objects[key]):raise ValueError('A groom axis update changed an unrelated object.')
                continue
            before=obj.read_typetree();after=last.objects[key].read_typetree();old=before['m_VertexData'];new=after['m_VertexData']
            first_raw=old.pop('m_DataSize');last_raw=new.pop('m_DataSize')
            if before!=after or len(first_raw)!=len(last_raw):raise ValueError('A groom axis update changed native geometry, frames, UV layouts or metadata.')
            helper=MeshHandler(obj.read());helper.process();count=old['m_VertexCount'];channel=old['m_Channels'][8];data=helper.src.m_VertexData
            if data is None or data.m_Channels is None or channel['dimension']!=2 or channel['format']!=0:raise ValueError('A groom axis has no owned float32 UV4 layout.')
            stream=helper.get_streams(data.m_Channels,count)[channel['stream']]
            allowed=np.zeros(len(first_raw),bool);indices=stream.offset+channel['offset']+np.arange(count)[:,None]*stream.stride+np.arange(8)
            allowed[indices.ravel()]=True
            if gpu_bridge:
                source_channel=old['m_Channels'][4];source_stream=helper.get_streams(data.m_Channels,count)[source_channel['stream']]
                if(source_channel['format'],source_channel['dimension'])!=(0,4):raise ValueError('Packed groom flow lost its owned source UV0 ZW.')
                indices=source_stream.offset+source_channel['offset']+8+np.arange(count)[:,None]*source_stream.stride+np.arange(8)
                allowed[indices.ravel()]=True
            if not np.array_equal(np.frombuffer(first_raw,np.uint8)[~allowed],np.frombuffer(last_raw,np.uint8)[~allowed]):raise ValueError('A groom axis update changed another vertex attribute.')
            values=np.ndarray((count,2),'<f4',buffer=last_raw,offset=stream.offset+channel['offset'],strides=(stream.stride,4))
            if not np.isfinite(values).all()or np.any(abs(np.sum(values.astype(float)**2,axis=1)-1)>1e-5):raise ValueError('A saved groom axis is nonfinite or not normalized.')
            if key in declared_flows:
                expected=copy.deepcopy(before);expected['m_VertexData']['m_DataSize']=first_raw
                resources.bake_groom_vertex_flow(expected,helper,replace_owned=True,declared_flow=declared_flows[key])
                if gpu_bridge:resources.pack_groom_gpu_frame(expected,helper,source_format)
                if bytes(expected['m_VertexData']['m_DataSize'])!=last_raw:
                    raise ValueError('A saved groom axis differs from its declared source-flow replay.')
            if gpu_bridge:
                def source_uv(raw):return np.ndarray((count,4),'<f4',buffer=raw,offset=source_stream.offset+source_channel['offset'],strides=(source_stream.stride,4))
                carry=source_uv(first_raw)[:,2:4].astype(float).copy()
                if source_format==2:
                    high=np.floor(carry[:,0]/4096);carry=np.c_[carry[:,0]-high*4096,high]*(2/4095)-1
                def packed(pair):
                    q=np.rint((np.clip(pair,-1,1)+1)*(.5*4095)).astype(np.uint32)
                    return(q[:,0]+q[:,1]*4096).astype('<f4')
                expected=np.c_[packed(carry),packed(values)]
                if not np.array_equal(source_uv(last_raw)[:,2:4],expected):raise ValueError('Saved GPU groom coordinates differ from the source carry and current authoring flow.')
        old_streams={name:hashlib.sha256(member.bytes).hexdigest()for name,member in first_env.file.files.items()if name!=first.name}
        new_streams={name:hashlib.sha256(member.bytes).hexdigest()for name,member in last_env.file.files.items()if name!=last.name}
        if old_streams!=new_streams:raise ValueError('A groom axis update changed a texture or resource stream.')
        return
    if first.name!=last.name or set(first.objects)!=set(last.objects)or [v.name for v in first.externals]!=[v.name for v in last.externals]:
        raise ValueError('A groom vertex field changed serialized ownership.')
    def resource_streams(env,file):
        return {name:hashlib.sha256(bytes(value.bytes if hasattr(value,'bytes')else value)).hexdigest()
                for name,value in env.file.files.items()if name!=file.name}
    if resource_streams(first_env,first)!=resource_streams(last_env,last):raise ValueError('A groom vertex field changed texture or resource bytes.')
    meshes=resources.groom_mesh_identifiers(first);material_owners=resources.groom_material_identifiers(first)|{row['object']for row in proof.get('materialSources',[])};cloth=set(proof['clothObjects']);seen=set();declared={}
    if 'materialObjects'in proof and set(proof['materialObjects'])!=material_owners:raise ValueError('A groom field has inconsistent material-role ownership.')
    if proof.get('missingOnly'):
        expected=missing_groom_domains(proof['source']['path']);declared={r['object']:r for r in proof['coordinates']}
        if {r['object']for r in expected}!=set(declared):raise ValueError('The completed groom domains omit a renderer/LOD.')
        for domain in expected:
            pin=declared[domain['object']]['coordinates']
            if sha(pin['path'])!=pin['sha256']or Path(pin['path']).read_bytes()!=domain['coordinates'].tobytes():raise ValueError('A completed groom domain differs from its actual prepared source surface.')
        meshes=set(declared)
        resources.validate_groom_frame_coverage(last)

    def cloth_frames(value):
        if isinstance(value,dict):return {key:cloth_frames(child)for key,child in value.items()if key not in ('localNor','localTan')}
        if isinstance(value,list):return [cloth_frames(child)for child in value]
        return value
    for key,obj in first.objects.items():
        other=last.objects[key]
        if key in meshes:
            old,new=obj.read_typetree(),other.read_typetree();seen.add(old['m_Name'])
            fixed=lambda tree:{k:v for k,v in tree.items()if k not in ('m_VertexData','m_Shapes')}
            if fixed(old)!=fixed(new):raise ValueError('A groom vertex field changed fitting, topology, bounds or skinning.')
            before,after=old['m_VertexData'],new['m_VertexData']
            if {k:v for k,v in before.items()if k not in ('m_DataSize','m_Channels')}!= \
                    {k:v for k,v in after.items()if k not in ('m_DataSize','m_Channels')}:
                raise ValueError('A groom vertex field changed vertex-data ownership.')
            helpers=[MeshHandler(value.read())for value in (obj,other)]
            layouts=[value['m_Channels']for value in (before,after)]
            count=before['m_VertexCount']
            vertex_streams=[]
            for helper in helpers:
                data=helper.src.m_VertexData
                if data is None or data.m_Channels is None:raise ValueError('A groom vertex field has no native channel layout.')
                vertex_streams.append(helper.get_streams(data.m_Channels,count))
            def channel_bytes(side,index):
                layout=layouts[side][index];stream=vertex_streams[side][layout['stream']]
                data=helpers[side].src.m_VertexData
                if data is None or data.m_Channels is None:raise ValueError('A groom vertex field lost its native channel layout.')
                size=helpers[side].get_channel_component_size(data.m_Channels[index])
                return np.ndarray((count,layout['dimension']*size),dtype=np.uint8,
                    buffer=(before,after)[side]['m_DataSize'],offset=stream.offset+layout['offset'],strides=(stream.stride,1)),size
            old_channels={i for i,v in enumerate(layouts[0])if v['dimension']}
            new_channels={i for i,v in enumerate(layouts[1])if v['dimension']}
            added={6,8}if proof.get('missingOnly')else{8}
            if 8 in old_channels or new_channels!=old_channels|added or layouts[1][8]['dimension']!=2 or layouts[1][8]['format']!=0:
                raise ValueError('A groom vertex field did not use its declared free UV4 owner.')
            authored_normals=None
            def same_direction(a,z):
                first_length=np.linalg.norm(a,axis=1);last_length=np.linalg.norm(z,axis=1)
                if not np.array_equal(first_length>1e-12,last_length>1e-12):return False
                lengths=first_length*last_length;valid=lengths>1e-12
                return np.all(np.sum(a[valid]*z[valid],axis=1)/lengths[valid]>1-1e-5)
            for channel in old_channels:
                a,z=layouts[0][channel],layouts[1][channel]
                carrier_extension=channel==4 and a['dimension']==2 and z['dimension']==4 and a['format']==z['format']==0 and any(
                    value.get('object')==key and value.get('supportPreparation')for value in proof['meshes'])
                if a['format']!=z['format']or(a['dimension']!=z['dimension']and not carrier_extension):
                    raise ValueError('A groom vertex field changed an existing native channel layout.')
                if channel in (1,2):
                    if channel==1 and proof.get('preserveSupportNormals'):
                        first_bytes,_=channel_bytes(0,channel);last_bytes,_=channel_bytes(1,channel)
                        dtype='<f4'if a['format']==0 else'<f2';old_normals=first_bytes.copy().view(dtype).reshape(count,-1).astype(float);new_normals=last_bytes.copy().view(dtype).reshape(count,-1).astype(float)
                        if not same_direction(old_normals,new_normals):raise ValueError('A groom finish erased authored support normals.')
                        authored_normals=(old_normals,new_normals)
                    continue
                first_bytes,size=channel_bytes(0,channel);last_bytes,_=channel_bytes(1,channel)
                if channel==4:first_bytes,last_bytes=first_bytes[:,:2*size],last_bytes[:,:2*size]
                if not np.array_equal(first_bytes,last_bytes):raise ValueError('A groom vertex field changed native vertex data: '+str(channel))
            if proof.get('missingOnly'):
                actual,_=channel_bytes(1,6)
                if actual.tobytes()!=Path(declared[key]['coordinates']['path']).read_bytes():raise ValueError('The saved missing groom domain differs from its measured coordinates.')
            for channel in (1,2,8):
                values,_=channel_bytes(1,channel);format=layouts[1][channel]['format']
                if format not in (0,1)or not np.isfinite(values.copy().view('<f4'if format==0 else'<f2')).all():
                    raise ValueError('A groom vertex field contains a nonfinite frame.')
            shapes,posed=old['m_Shapes'],new['m_Shapes']
            if {k:v for k,v in shapes.items()if k!='vertices'}!={k:v for k,v in posed.items()if k!='vertices'}or len(shapes['vertices'])!=len(posed['vertices']):
                raise ValueError('A groom vertex field changed physical channel identities.')
            for a,z in zip(shapes['vertices'],posed['vertices']):
                if {k:v for k,v in a.items()if k not in ('normal','tangent')}!={k:v for k,v in z.items()if k not in ('normal','tangent')}:
                    raise ValueError('A groom vertex field changed a physical position frame.')
                if not all(math.isfinite(value)for name in ('normal','tangent')for value in z[name].values()):
                    raise ValueError('A groom physical frame contains nonfinite directions.')
            if authored_normals is not None:
                indices=np.asarray([value['index']for value in shapes['vertices']],int)
                before_normals=authored_normals[0][indices]+np.asarray([[value['normal'][axis]for axis in 'xyz']for value in shapes['vertices']]).reshape(-1,3)
                after_normals=authored_normals[1][indices]+np.asarray([[value['normal'][axis]for axis in 'xyz']for value in posed['vertices']]).reshape(-1,3)
                if not same_direction(before_normals,after_normals):raise ValueError('A groom finish erased an authored physical normal frame.')
        elif key in material_owners:
            copied=next((row for row in proof.get('materialSources',[])if row['object']==key),None)
            if copied:
                expected=copy.deepcopy(first.objects[copied['source']].read_typetree());expected['m_Name']=obj.read_typetree()['m_Name']
                if expected!=other.read_typetree():raise ValueError('A completed native LOD material differs from its actual prepared owner.')
                continue
            a,z=obj.read_typetree(),other.read_typetree();af=dict(a['m_SavedProperties']['m_Floats']);zf=dict(z['m_SavedProperties']['m_Floats'])
            if zf.get('_SupportInVertexFrame')!=1 or zf.get('_FlowInVertexFrame')!=(2 if proof.get('gpuBridge')else 1)or \
                    {k:v for k,v in af.items()if k not in ('_SupportInVertexFrame','_FlowInVertexFrame')}!= \
                    {k:v for k,v in zf.items()if k not in ('_SupportInVertexFrame','_FlowInVertexFrame')}:
                raise ValueError('A groom vertex field changed an unrelated material property.')
            del a['m_SavedProperties']['m_Floats'];del z['m_SavedProperties']['m_Floats']
            if a!=z:raise ValueError('A groom vertex field changed material ownership, colours or samplers.')
        elif key in cloth:
            if obj.type.name!='MonoBehaviour'or cloth_frames(obj.read_typetree())!=cloth_frames(other.read_typetree()):
                raise ValueError('A groom vertex field changed an unrelated cloth property.')
        elif resources.serialized_object_bytes(obj)!=resources.serialized_object_bytes(other):
            raise ValueError('A groom vertex field changed an unrelated object.')
    if seen!={row['mesh']for row in proof['meshes']}:raise ValueError('A groom vertex field has incomplete mesh coverage.')


def validate_shared_skin_surface(receipt):
    """Replay saved collar geometry and its renderer-owned bone transaction."""
    import numpy as np
    import character_resources as resources
    def pinned(reference):
        path=Path(reference['path'])
        if sha(path)!=reference['sha256']:raise ValueError('A shared skin surface input changed.')
        return path
    source=pinned(receipt['source']);target=pinned(receipt['target'])
    preparation=read(pinned(receipt['preparation']))
    owners=[pinned(reference)for reference in preparation['producers']]
    generator=[path for path in owners if path.name=='generate.py']
    if len(generator)!=1:raise ValueError('A shared skin surface has no immutable geometry owner.')
    scope: dict[str,Any]=dict(np=np,math=math,copy=copy)
    transport=[path for path in owners if path.name=='mesh_recipe_codec.py']
    if len(transport)>1:raise ValueError('A shared skin surface has ambiguous numeric transport owners.')
    if transport:
        helpers: list[ast.stmt]=[node for node in ast.parse(transport[0].read_text('utf-8')).body if isinstance(node,ast.FunctionDef)and node.name=='skin_palette_frame']
        if len(helpers)!=1:raise ValueError('A shared skin surface has no frozen native palette owner.')
        exec(compile(ast.Module(body=helpers,type_ignores=[]),str(transport[0]),'exec'),scope)
    nodes: list[ast.stmt]=[node for node in ast.parse(generator[0].read_text('utf-8')).body if isinstance(node,ast.FunctionDef)]
    exec(compile(ast.Module(body=nodes,type_ignores=[]),str(generator[0]),'exec'),scope)
    source_env,sf=resources.load_serialized_bundle(source,in_memory=True)
    target_env,tf=resources.load_serialized_bundle(target,in_memory=True)
    if sf.name!=tf.name or set(sf.objects)!=set(tf.objects)or [item.path for item in sf.externals]!=[item.path for item in tf.externals]:
        raise ValueError('A shared skin surface changed serialized identities or dependencies.')
    selected=set();renderers=set();dependencies={sf.name:sf}
    source_authority=pinned(preparation['source'])
    if source_authority.resolve()!=(source.parent.parent/'ResourceAssembly.json').resolve():raise ValueError('A shared skin sampler has another resource assembly owner.')
    source_assembly=read(source_authority)
    source_owners={item['newCab']:item for item in source_assembly['proofs']}
    def resolve(file,pointer):
        cab=file.externals[pointer['m_FileID']-1].name if pointer['m_FileID']else file.name
        if cab not in dependencies:
            owner=source_owners[cab];path=source.parent/owner['file']
            if sha(path)!=owner['sha256']:raise ValueError('A shared skin sampler dependency changed.')
            dependencies[cab]=resources.load_serialized_bundle(path,in_memory=True)[1]
        result=dependencies[cab];return result,result.objects[pointer['m_PathID']]
    for row in receipt['rows']:
        pinned(row['packet']);pinned(row['donor'])
        if pinned(row['source'])!=source:raise ValueError('A shared skin mesh has another resource owner.')
        identifier=row['meshPathId'];native=export_current_mesh(sf.objects[identifier]);donor=material_packet(row['donor'])
        if native['sourceFingerprint']!=row['sourceFingerprint']:raise ValueError('A shared skin mesh changed its native source.')
        if row['proof'].get('kind')=='rebuilt-native-skin-collar':
            repeat_uv=resources.renderer_repeat_uv(sf,sf.objects[row['sourceRenderer']].read_typetree(),resolve)
            if row['proof'].get('repeatUvAliases',repeat_uv)!=repeat_uv:raise ValueError('A rebuilt skin collar changed its actual sampler contract.')
            expected,proof=scope['rebuild_skin_boundary'](native,donor,row['sourceBonePaths'],row['donorBonePaths'],repeat_uv=repeat_uv)
        else:
            expected,proof=scope['conform_skin_boundary'](native,donor,row['sourceBonePaths'],row['donorBonePaths'],row['pairs'],shading_frame_owner='donor')
        saved=export_current_mesh(tf.objects[identifier])
        if proof!=row['proof']or fingerprint(expected)[0]!=saved['sourceFingerprint']or saved['sourceFingerprint']!=material_packet(row['packet'])['sourceFingerprint']:
            raise ValueError('A saved shared skin surface differs from its independent numeric replay.')
        paths=[*row['sourceBonePaths'],*[addition['path']for addition in proof['appendedBones']]]
        if paths!=row['bonePaths']:raise ValueError('A shared skin surface changed its declared bone palette.')
        for key,obj in sf.objects.items():
            if obj.type.name!='SkinnedMeshRenderer':continue
            tree=obj.read_typetree()
            if tree['m_Mesh']!=dict(m_FileID=0,m_PathID=identifier):continue
            wanted,_=resources.extend_skin_bone_palette(sf,tree,row['sourceBonePaths'],paths)
            if resources.serialized_object_bytes(obj,tree=wanted)!=resources.serialized_object_bytes(tf.objects[key]):
                raise ValueError('A shared skin surface changed another renderer field.')
            renderers.add(key)
        selected.add(identifier)
    if not selected or not renderers or receipt.get('materialFloatUpdates')or receipt.get('cloth',{}).get('changes'):
        raise ValueError('A shared skin surface has missing owners or unrelated material/cloth mutations.')
    for key,obj in sf.objects.items():
        if key not in selected|renderers and resources.serialized_object_bytes(obj)!=resources.serialized_object_bytes(tf.objects[key]):
            raise ValueError('A shared skin surface changed an unowned serialized object.')
    for name,member in source_env.file.files.items():
        if hasattr(member,'objects'):continue
        other=target_env.file.files[name]
        if (bytes(member)if isinstance(member,(bytes,bytearray))else member.bytes)!=(bytes(other)if isinstance(other,(bytes,bytearray))else other.bytes):
            raise ValueError('A shared skin surface changed an unowned resource stream.')


def validate_owned_groom_surface(receipt,inputs,verified):
    """Replay a source-owned surface and its current-frame cloth attachment."""
    import importlib.util
    import tempfile
    import character_resources as resources
    def pinned(reference):
        path=Path(reference['path']);checked_source_path(path)
        if sha(path)!=reference['sha256']:raise ValueError('A groom surface input changed.')
        return path
    def owner(reference):
        path=pinned(reference);key=('groom-surface-owner',reference['sha256'])
        if key not in verified:
            spec=importlib.util.spec_from_file_location('owned_groom_'+reference['sha256'][:12],path)
            if spec is None or spec.loader is None:raise ValueError('A groom surface producer cannot be loaded.')
            module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module);verified[key]=module
        return verified[key]
    def parameter_catalogs(plan):
        declaration=receipt.get('parameterCatalogs')
        if declaration is None:
            if any(row.get('materialSlots')is not None for row in receipt['rows']):
                raise ValueError('A composed fine material has no manifest parameter ownership.')
            return
        first_path=pinned(declaration['source']);last_path=pinned(declaration['target'])
        key=('groom-parameter-catalogs',receipt['composition']['plan']['sha256'],plan['source']['sha256'],
             declaration['source']['sha256'],declaration['target']['sha256'],tuple(declaration['files']))
        if key not in verified:
            source_assembly=read(pinned(plan['source']));first=read(first_path);last=read(last_path)
            if source_assembly['catalogSha256']!=declaration['source']['sha256']:
                raise ValueError('A groom parameter catalog differs from its proven source.')
            selected=declaration['files'];planned=collections.defaultdict(dict)
            for row in plan['rows']:planned[Path(row['source']['path']).name][row['mesh']]=row
            if not selected or len(selected)!=len(set(selected))or not set(selected)<=set(planned):
                raise ValueError('A groom parameter catalog has unknown or repeated part owners.')
            proofs={row['file']:row for row in source_assembly['proofs']};by_cab={row['newCab']:row for row in proofs.values()};loaded={}
            catalog=Path(declaration['source']['path']).parent
            def load(cab):
                if cab not in loaded:
                    proof=by_cab[cab];path=catalog/proof['file']
                    if sha(path)!=proof['sha256']:raise ValueError('A groom parameter prefab source changed.')
                    loaded[cab]=resources.load_serialized_bundle(path,in_memory=True)[1]
                return loaded[cab]
            expected=copy.deepcopy(first);updates=collections.defaultdict(list)
            for resource in expected['resources']:
                name=resource['bundles'][0]['file']
                if resource['type']!='GameObject'or name not in selected:continue
                current=load(proofs[name]['newCab']);parameters=resource.get('parameters',[]);before=copy.deepcopy(parameters)
                skins,_=resource_prefab_skins(first,proofs,resource['source'],load)
                for renderer,skin in skins:
                    pointer=skin.read_typetree()['m_Mesh']
                    if skin.assets_file is not current or pointer['m_FileID']or pointer['m_PathID']not in planned[name]:continue
                    for slots in planned[name][pointer['m_PathID']].get('materialSlots',[]):
                        for value in before:
                            if value['renderer']==renderer and value['slot']==slots['sourceSlot']:
                                addition=dict(value,slot=slots['targetSlot'])
                                if addition not in parameters:parameters.append(addition)
                if parameters!=before:
                    resource['parameters']=parameters;updates[name].append(dict(source=resource['source'],before=before,after=copy.deepcopy(parameters)))
            target_bundles={row['file']:row for row in last['bundles']}
            for bundle in expected['bundles']:
                if bundle['file']in selected:bundle['sha256']=target_bundles[bundle['file']]['sha256']
            if expected!=last:raise ValueError('A groom manifest changed unowned resources or lost its cloned slot parameters.')
            verified[key]=dict(updates)
        if receipt['file']not in declaration['files']or receipt.get('rendererParameterUpdates',[])!=verified[key].get(receipt['file'],[]):
            raise ValueError('A groom renderer parameter ledger differs from its source-slot replay.')
    source=pinned(receipt['source']);target=pinned(receipt['target'])
    if receipt.get('composition'):
        plan=read(pinned(receipt['composition']['plan']));donor_assembly=read(pinned(receipt['composition']['fibers']))
        parameter_catalogs(plan)
        donor_rows={row['file']:row for row in donor_assembly['proofs']}
        converters=[reference for reference in inputs if Path(reference['path']).name=='GenerateFlowCoverageSweptProfile.py']
        if len(converters)!=1 or converters[0]!=plan['producer']:raise ValueError('A composed groom has no retained source producer.')
        converter=owner(converters[0]);report=read(pinned(receipt['engineReport']))
        primary=plan.get('primaryAtlas')
        if primary is not None:
            if groom_primary_atlas(pinned(primary['plan']),pinned(primary['report']))!=primary:
                raise ValueError('A composed primary atlas differs from its pinned recipe and all-mip proof.')
        source_purpose_assembly=None
        if not report.get('success')or receipt['materialFloatUpdates']:raise ValueError('A composed groom changed its established frame representation.')
        original=resources.load_serialized_bundle(source,in_memory=True)[1];replacements=[];seen=set()
        with tempfile.TemporaryDirectory(prefix='groom-composition-',dir=target.parent)as directory:
            temporary=Path(directory)
            for row in receipt['rows']:
                identifier=row['mesh']
                if identifier in seen or row['source']!=receipt['source']:raise ValueError('A composed source renderer is repeated or differs.')
                seen.add(identifier);old=export_current_mesh(original.objects[identifier])
                donor_proof=donor_rows[Path(row['fibers']['path']).name]
                if donor_proof['sha256']!=row['fibers']['sha256']:raise ValueError('The composed fine domain lost its proven bundle owner.')
                donor_key=('validated-resource',receipt['composition']['fibers']['sha256'],donor_proof['file'])
                if donor_key not in verified:
                    validate_prepared_resource(donor_proof,donor_assembly,verified);verified[donor_key]=True
                donor=resources.load_serialized_bundle(pinned(row['fibers']),in_memory=True)[1]
                binding=converter.decode_binding(pinned(row['binding']));options={};support_proof=None
                if row['composition'].get('retainedVertexCapacity')is not None:options['vertex_capacity']=row['composition']['retainedVertexCapacity']
                declaration=row
                if row['composition'].get('authoredSupport')and not row.get('sourceTerminalField'):
                    fine_plan=read(pinned(plan['finePlan']))
                    declaration=next(value for value in fine_plan['rows']if value['mesh']==identifier and value['asset']==row['asset'])
                if row.get('carrierSurface')is not None:
                    fine_plan=read(pinned(plan['finePlan']))
                    declared=next(value for value in fine_plan['rows']if value['mesh']==identifier and value['asset']==row['asset'])
                    if any(row.get(field)!=declared.get(field)for field in('carrierSurface','native','materials','retainedSupport')):
                        raise ValueError('A composed carrier lost its original fitted partition ownership.')
                    if declared['carrierSurface']!=declared['attachment'].get('compiledCarrierSurface'):
                        raise ValueError('A composed carrier differs from its proven fitting range.')
                    options['carrier_replacement']=dict(native=converter.read(pinned(declared['native'])),surface=declared['carrierSurface'],
                        materials=declared['materials'],maximumSupportOffsetMetres=declared['retainedSupport']['maximumSupportOffsetMetres'])
                elif declaration.get('sourceTerminalField')is not None:
                    support,support_proof=converter.authored_terminal_support(converter.read(pinned(declaration['native'])),old,
                        binding['matrix'],declaration['materials'],declaration['sourceTerminalField'])
                    options['authored_support']=support
                if row.get('materialSlots')is not None:
                    slots=row['materialSlots']
                    options['fine_material_slots']=[{key:value for key,value in slot.items()if key!='purpose'}for slot in slots if slot.get('purpose','fine')=='fine']
                    if any(slot.get('purpose')=='primary'for slot in slots):
                        if primary is None:raise ValueError('A composed primary surface lost its atlas ownership.')
                        options['primary_material_slots']=[{key:value for key,value in slot.items()if key!='purpose'}for slot in slots if slot.get('purpose')=='primary']
                        if source_purpose_assembly is None:source_purpose_assembly=read(pinned(plan['source']))
                        source_purposes=[{key:value for key,value in item.items()if key!='file'}for item in source_purpose_assembly.get('groomSourceDesigns',{}).get('materialPurposes',[])if item['file']==Path(row['source']['path']).name]
                        primary_ids=[i for i,value in enumerate(binding['vertices'])if value.get('role')=='primary-lock']
                        expected_slots=groom_fine_material_slots(original,identifier,export_current_mesh(donor.objects[identifier]),primary_vertices=primary_ids,source_purposes=source_purposes)
                        if expected_slots!=slots:raise ValueError('A composed material-purpose ledger differs from its actual source renderers.')
                if row['composition'].get('surfaceSkin')is not None:
                    fine_plan=read(pinned(plan['finePlan']))
                    declared=next(value for value in fine_plan['rows']if value['mesh']==identifier and value['asset']==row['asset'])
                    skin=declared['attachment'].get('surfaceSkin');native=converter.read(pinned(declared['native']))
                    if skin is None or skin['sourceFingerprint']!=native['sourceFingerprint']:
                        raise ValueError('A composed groom lost its original center skin authority.')
                    options['surface_skin']=skin
                packet,attachment,facts=converter.compose_finished_source_groom(old,export_current_mesh(donor.objects[identifier]),binding,**options)
                if support_proof is not None:facts['authoredSupport']=support_proof
                if packet!=converter.read(pinned(row['packet']))or facts!=row['composition']:raise ValueError('A composed groom changed retained support or finished fibers.')
                binding_path=temporary/(str(identifier)+'.bytes');converter.encode_legacy_binding(attachment,binding_path)
                if binding_path.read_bytes()!=pinned(row['clothBinding']).read_bytes():raise ValueError('A composed groom lost its source cloth rows.')
                compiled=resources.load_serialized_bundle(pinned(row['compiledBundle']),in_memory=True)[1]
                meshes=[obj for obj in compiled.objects.values()if obj.type.name=='Mesh'and obj.peek_name()==row['compiledName']]
                if len(meshes)!=1 or export_current_mesh(meshes[0])['sourceFingerprint']!=packet['sourceFingerprint']:raise ValueError('Unity changed a composed groom.')
                if facts.get('surfaceSkin')is not None:
                    converter.validate_surface_skin(old,packet,facts['surfaceSkin'],binding=attachment);attachment['surfaceSkin']=facts['surfaceSkin']
                replacements.append(dict(sourceFingerprint=old['sourceFingerprint'],sourcePathIds=[identifier],packet=packet,binding=attachment,compiledTree=meshes[0].read_typetree()))
            if seen!=resources.groom_mesh_identifiers(original):raise ValueError('A composed groom omitted a source-owned LOD.')
            replay=resources.prepare_standalone_part(source,receipt['source']['sha256'],temporary/'saved.bundle',replacements,
                export_mesh=export_current_mesh,material_slot_updates=receipt.get('materialSlotUpdates',[]))
            if receipt.get('rendererMaterialUpdates',[])!=replay.get('rendererMaterialUpdates',[]):
                raise ValueError('A composed groom lost its renderer/material clone ownership.')
            added_objects=set(replay.get('objectsAdded',[]));changed_objects=set(replay['changedObjects'])
            if primary is not None:
                prepared=resources.load_serialized_bundle(replay['target']['path'],in_memory=True)[1]
                wanted={};atlas=plan.get('fineAtlas')
                for row in receipt['rows']:
                    renderers=[obj.read_typetree()for obj in prepared.objects.values()if obj.type.name=='SkinnedMeshRenderer'and
                        obj.read_typetree()['m_Mesh']==dict(m_FileID=0,m_PathID=row['mesh'])]
                    for slots in row.get('materialSlots',[]):
                        purpose=slots.get('purpose','fine')
                        for renderer in renderers:
                            pointer=renderer['m_Materials'][slots['targetSlot']]
                            if pointer['m_FileID']:raise ValueError('A composed atlas material is not locally owned.')
                            material=pointer['m_PathID']
                            if purpose=='primary':declarations=primary['materialProperties']
                            elif purpose=='fine'and atlas is not None:
                                source_pointer=renderer['m_Materials'][slots['sourceSlot']]
                                if source_pointer['m_FileID']:raise ValueError('A composed fine template is not locally owned.')
                                matches=[value for value in atlas['compatibility']['materials']if value['file']==receipt['file']and value['material']==source_pointer['m_PathID']]
                                if {value['property']for value in matches}!={'_GroomColor','_GroomNormal'}:
                                    raise ValueError('A composed fine material lost its source atlas properties.')
                                names=atlas['compatibility']['textures']
                                declarations={value['property']:dict(atlas['target'],object=value['texture'],name=names.get(str(value['texture']),names.get(value['texture'])))for value in matches}
                            else:declarations={}
                            for property,reference in declarations.items():
                                key=(material,property)
                                if key in wanted and wanted[key]!=reference:raise ValueError('A composed material aliases different atlas purposes.')
                                wanted[key]=reference
                textures=receipt.get('materialTextures')
                if textures is None:raise ValueError('A composed primary material lost its saved atlas transaction.')
                sources={value['object']:value['source']for value in textures['textures']}
                actual={}
                for value in textures['materialUpdates']:
                    key=(value['pathId'],value['property']);pointer=value['after']
                    if key in actual or pointer['m_FileID']or pointer['m_PathID']not in sources:
                        raise ValueError('A composed atlas repeats or loses a local material texture owner.')
                    actual[key]=sources[pointer['m_PathID']]
                if actual!=wanted:raise ValueError('A composed atlas changed another material purpose or lost an owned property.')
            if receipt.get('materialTextures'):
                textures=receipt['materialTextures'];resources.validate_owned_material_textures(textures)
                if replay['target']['sha256']!=receipt['geometryTarget']['sha256']:raise ValueError('The dedicated fine geometry/material slots cannot be reproduced.')
                sources={value['object']:value['source']for value in textures['textures']}
                updates=[dict(pathId=value['pathId'],property=value['property'],before=value['before'],texture=sources[value['after']['m_PathID']])for value in textures['materialUpdates']]
                replay=resources.replace_owned_material_textures(Path(replay['target']['path']),replay['target']['sha256'],temporary/'textured.bundle',updates)
                added_objects.update(replay.get('objectsAdded',[]))
                changed_objects.update(replay.get('objectsAdded',[]));changed_objects.update(value['pathId']for value in replay['materialUpdates'])
                changed_objects.add(replay['metadataObject'])
                replay=dict(target=replay['target'])
            if receipt.get('objectsAdded',[])!=sorted(added_objects)or receipt.get('changedObjects',[])!=sorted(changed_objects):
                raise ValueError('A composed groom lost its added or changed serialized object ledger.')
            if replay['target']['sha256']!=receipt['target']['sha256']:raise ValueError('The composed groom cannot reproduce its saved transaction.')
        return
    generation=read(pinned(receipt['generation']));report=read(pinned(receipt['engineReport']))
    if generation.get('status')not in('PASS_CURVED_CORE_NUMERIC_PROOFS_RENDER_PENDING','PASS_SOURCE_GROOM_NUMERIC_PROOFS_RENDER_PENDING')or not report.get('success'):
        raise ValueError('A groom surface requires complete physical and engine proofs.')
    prover=owner(dict(path=generation['generatorPath'],sha256=generation['generatorSha256']))
    converters=[reference for reference in inputs if Path(reference['path']).name=='GenerateFlowCoverageSweptProfile.py']
    if len(converters)!=1:raise ValueError('A groom surface has no unique attachment producer.')
    converter=owner(converters[0]);rows={(r['asset'],r['lod']):r for r in generation['rows']}
    _,original=resources.load_serialized_bundle(source,in_memory=True)
    replacements=[];seen=set()
    with tempfile.TemporaryDirectory(prefix='groom-surface-',dir=target.parent)as directory:
        temporary=Path(directory)
        for row in receipt['rows']:
            entry=rows[row['asset'],row['lod']];key=('groom-surface-numeric',receipt['generation']['sha256'],row['asset'],row['lod'])
            for field,reference in (('generatedPacket',row['rawPacket']),('nativePacketPath',row['native']),('bindingPath',row['binding'])):
                if Path(entry[field]).resolve()!=pinned(reference).resolve():raise ValueError('A groom surface lost its original input ownership.')
            if key not in verified:
                verified[key]=prover.prove_row(entry,generation['profile'])
            if verified[key]!=row['numeric']or row['numeric'].get('declaredZeroFine'):
                raise ValueError('A groom surface lost its native or fine geometry proof.')
            raw=converter.read(pinned(row['rawPacket']));native=converter.read(pinned(row['native']))
            binding=converter.decode_binding(pinned(row['binding']));identifier=row['mesh']
            if identifier in seen:raise ValueError('A groom surface mesh is repeated.')
            seen.add(identifier);old=export_current_mesh(original.objects[identifier])
            expected_support=groom_retained_support(entry,generation['profile'])
            if row.get('retainedSupport')!=expected_support:
                raise ValueError('A groom surface lost its declared physical support owner.')
            options=dict(retained_support=row['retainedSupport'])if row.get('retainedSupport')is not None else{}
            packet,attachment,facts=converter.standalone_cloth_attachment(raw,native,binding,old,**options)
            if row.get('carrierSurface')!=facts.get('compiledCarrierSurface'):
                raise ValueError('A groom surface lost its compiled carrier range.')
            if packet!=converter.read(pinned(row['packet']))or facts!=row['attachment']:
                raise ValueError('A groom surface differs from its declared skin or cloth frame.')
            binding_path=temporary/(str(identifier)+'.bytes');converter.encode_legacy_binding(attachment,binding_path)
            if sha(binding_path)!=row['clothBinding']['sha256']or binding_path.read_bytes()!=pinned(row['clothBinding']).read_bytes():
                raise ValueError('A groom surface cloth attachment cannot be reproduced.')
            _,compiled=resources.load_serialized_bundle(pinned(row['compiledBundle']),in_memory=True)
            name=entry['manifestEntry']['authoredMeshAsset']
            meshes=[obj for obj in compiled.objects.values()if obj.type.name=='Mesh'and obj.peek_name()==name]
            if len(meshes)!=1 or export_current_mesh(meshes[0])['sourceFingerprint']!=packet['sourceFingerprint']:
                raise ValueError('A saved groom surface differs from its proven channels.')
            if facts.get('surfaceSkin')is not None:
                converter.validate_surface_skin(old,packet,facts['surfaceSkin'],source_fingerprint=native['sourceFingerprint'],binding=attachment);attachment['surfaceSkin']=facts['surfaceSkin']
            replacements.append(dict(sourceFingerprint=old['sourceFingerprint'],sourcePathIds=[identifier],
                packet=packet,binding=attachment,compiledTree=meshes[0].read_typetree()))
        if seen!=resources.groom_mesh_identifiers(original):
            raise ValueError('Resetting a groom frame requires all owning LOD meshes.')
        updates=receipt['materialFloatUpdates']
        if {r['pathId']for r in updates}!=resources.groom_material_identifiers(original)or any(
                set(r['values'])!={'_SupportInVertexFrame','_FlowInVertexFrame'}or any(v['after']!=0 for v in r['values'].values())for r in updates):
            raise ValueError('A groom surface reset another material property.')
        replay=resources.prepare_standalone_part(source,receipt['source']['sha256'],temporary/'saved.bundle',replacements,
            export_mesh=export_current_mesh,material_updates=updates)
        if replay['target']['sha256']!=receipt['target']['sha256']:
            raise ValueError('A groom surface saved transaction cannot be reproduced.')


def validate_groom_crown_clearance(receipt,inputs,verified):
    """Replay bounded crown fields and the complete saved position transaction."""
    import importlib.util
    import numpy as np
    import character_resources as resources
    resources.validate_owned_mesh_positions(receipt)
    producers=[row for row in inputs if Path(row['path']).name=='GenerateFlowCoverageSweptProfile.py']
    if len(producers)!=1:raise ValueError('A source crown requires one declared geometry producer.')
    reference=producers[0];key=('crown-owner',reference['sha256'])
    if sha(reference['path'])!=reference['sha256']:raise ValueError('The source crown producer changed.')
    if key not in verified:
        spec=importlib.util.spec_from_file_location('source_crown_'+reference['sha256'][:12],reference['path'])
        if spec is None or spec.loader is None:raise ValueError('The source crown producer cannot be loaded.')
        module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module);verified[key]=module
    producer=verified[key];reference=receipt['sourceScalp']
    if sha(reference['path'])!=reference['sha256']:raise ValueError('The compatible source scalp changed.')
    scalp=np.load(reference['path'])['triangles']
    source=resources.load_serialized_bundle(receipt['source']['path'],in_memory=True)[1]
    expected={row['object']:row for row in receipt['fields']};packets={};amplitudes=[]
    if len(receipt['crownMeshes'])!=3 or set(expected)!={row['object']for row in receipt['crownMeshes']}:
        raise ValueError('A source crown requires all three owning LOD meshes.')
    for row in receipt['crownMeshes']:
        packet=export_current_mesh(source.objects[row['object']]);packets[row['object']]=packet
        frame=np.asarray(row['scalpFrame']).reshape(4,4).T;current_scalp=scalp@frame[:3,:3].T+frame[:3,3]
        analysis=producer.measure_source_crown(packet,row['rendererToPrefab'],current_scalp,row['samplers'])
        if analysis!=row['analysis']:raise ValueError('The crown clearance differs from the alpha-visible source scalp fit.')
        amplitudes.append(analysis['maximumLift'])
    amplitude=max(amplitudes)
    if amplitude!=receipt['sharedLodLift']or not 0<amplitude<=.008:raise ValueError('The source crown has another shared LOD field.')
    for row in receipt['crownMeshes']:
        analysis=row['analysis'];packet=producer.apply_crown_field(packets[row['object']],row['rendererToPrefab'],amplitude,analysis['fieldStart'],analysis['fieldTop'])
        field=expected[row['object']]
        if np.asarray(packet['vertices'],'<f4').tobytes()!=Path(field['positions']['path']).read_bytes():
            raise ValueError('The saved crown position field differs from its bounded source operation.')
        frames=[frame for shape in packet['shapes']for frame in shape['frames']]
        if len(frames)!=len(field['shapes']):raise ValueError('A crown field omitted a source shape frame.')
        for frame,reference in zip(frames,field['shapes']):
            if np.asarray(frame['vertices'],'<f4').tobytes()!=Path(reference['positions']['path']).read_bytes():
                raise ValueError('A saved crown shape differs from its source field.')


def validate_compiled_groom_atlas(operation):
    """Replay the complete encoded atlas while preserving its resource owners."""
    import tempfile
    import character_resources as resources
    for key in ('source','target','compiledSource'):
        reference=operation[key];checked_source_path(reference['path'])
        if sha(reference['path'])!=reference['sha256']:raise ValueError('A groom atlas input changed.')
    if operation.get('samplerAndResourceReferencesExact')is not True:
        raise ValueError('A groom atlas lost its sampler or reference ownership.')
    with tempfile.TemporaryDirectory(prefix='groom-atlas-',dir=Path(operation['target']['path']).parent)as directory:
        replay=resources.replace_compiled_textures(operation['source']['path'],operation['source']['sha256'],
            Path(directory)/'saved.bundle',operation['compiledSource']['path'],operation['compiledSource']['sha256'])
        if replay['sha256']!=operation['sha256']or replay['textures']!=operation['textures']:
            raise ValueError('A groom atlas encoded mip transaction cannot be reproduced.')


def validate_groom_material_defaults(receipt):
    import tempfile
    import character_resources as resources
    for key in ('source','target','beforePreset','afterPreset'):
        reference=receipt[key];checked_source_path(reference['path'])
        if sha(reference['path'])!=reference['sha256']:raise ValueError('A groom material preset input changed.')
    with tempfile.TemporaryDirectory(prefix='groom-preset-',dir=Path(receipt['target']['path']).parent)as directory:
        replay=resources.retune_groom_defaults(dict(source=receipt['source']['path'],sha256=receipt['source']['sha256'],
            target=str(Path(directory)/'saved.bundle'),before=receipt['beforePreset'],after=receipt['afterPreset']))
        if not replay['changed']or replay['sha256']!=receipt['sha256']or replay['materialFloatUpdates']!=receipt['materialFloatUpdates']:
            raise ValueError('A groom material changed beyond its matching previous defaults.')


def prepared_resource_closure(catalog,manifest,assembly):
    """Use a complete prepared catalog's declared CAB owners for typed traversal."""
    import character_resources as resources
    payloads=catalog_payloads(manifest);rows=assembly.get('proofs',[])
    if assembly.get('status')!='PASS_STANDALONE_CHARACTER_RESOURCES'or \
            assembly.get('catalogSha256')!=sha(checked_catalog_path(catalog,RESOURCE_FILES['manifest']))or \
            len(rows)!=len(payloads)or {row['file']:row['sha256']for row in rows}!=payloads:
        raise ValueError('A prepared reference graph differs from its complete source catalog.')
    sources={}
    for row in rows:
        cab=row.get('newCab')
        if not isinstance(cab,str)or not cab or cab in sources:
            raise ValueError('A prepared reference graph has missing or duplicate CAB ownership.')
        sources[cab]=dict(source=str(checked_catalog_path(catalog,row['file'])),sha256=row['sha256'],file=row['file'])
    return resources.ResourceClosure(sources,rebase_archive=None)


def validate_prepared_resource(row,assembly,verified):
    """Check an owned prepared mutation without claiming its authored fields are unchanged."""
    import character_resources as resources
    retained=assembly.get('retainedSourceInputs',[])
    ledger_key=('producer-archives',hashlib.sha256(json.dumps(retained,sort_keys=True,separators=(',',':'),allow_nan=False).encode()).hexdigest())
    if ledger_key not in verified:
        for record in retained:
            original,archive=record['original'],record['archive']
            if original['sha256'].lower()!=archive['sha256'].lower():raise ValueError('A retained producer archive has another content authority.')
            path=Path(archive['path']);checked_catalog_path(path.parent,path.name)
            key=('producer-archive',str(Path(original['path']).resolve()),original['sha256'].lower())
            if key not in verified:
                if sha(path)!=archive['sha256'].lower():raise ValueError('A retained producer archive changed.')
                verified[key]=path
        # The ledger registration is shared by this validation operation. Every
        # consumed input still passes pinned's current path checks below.
        verified[ledger_key]=True
    def pinned(reference):
        requested=Path(reference['path'])
        path=verified.get(('producer-archive',str(requested.resolve()),reference['sha256'].lower()),requested)
        checked_source_path(path)
        expected=reference['sha256'].lower();key=(str(path.resolve()),expected)
        if key not in verified:
            if sha(path)!=expected:raise ValueError('A prepared resource input changed: '+str(path))
            verified[key]=True
        return path
    def document(reference):
        path=pinned(reference);key=('document',str(path.resolve()),reference['sha256'].lower())
        if key not in verified:verified[key]=read(path)
        return verified[key]
    if assembly.get('preparedSource'):
        source=assembly['preparedSource'];baseline=document(source['assembly']);catalog=document(source['catalog'])
        baseline_rows={item['file']:item for item in baseline['proofs']}
        original=baseline_rows[row['file']]
        payloads={item['file']:item['sha256']for item in catalog['bundles']}
        original_operations=original.get('appearanceFinishing',[]);operations=row.get('appearanceFinishing',[])
        if operations[:len(original_operations)]!=original_operations:
            raise ValueError('A resource refinement lost its preceding operation chain.')
        added_objects=sum(len(document(operation['proof']).get('objectsAdded',[]))for operation in operations[len(original_operations):]
                          if operation.get('kind')in('compiled-material-texture-bindings','source-skin-material-bindings','source-skin-interface','source-owned-groom-surface'))
        expected_objects=original.get('objects')
        if added_objects:
            if type(expected_objects)is not int:raise ValueError('A material texture refinement has no source object count.')
            expected_objects+=added_objects
        if payloads.get(row['file'])!=original['sha256']or any(row.get(key)!=original.get(key)for key in
                ('oldCab','newCab','removedObjects','dependencies','sourceSha256','codeSha256','declaredPreparedMutations'))or row.get('objects')!=expected_objects:
            raise ValueError('A resource refinement changed its original object or dependency ownership.')
        groom_operations=[];resource_seen=False
        for candidate in assembly.get('proofs',[row]):
            if candidate['file']==row['file']:candidate=row;resource_seen=True
            prior=baseline_rows.get(candidate['file']);previous=[]if prior is None else prior.get('appearanceFinishing',[])
            following=candidate.get('appearanceFinishing',[])
            for operation in following[len(previous):]:
                if operation.get('kind')=='source-owned-groom-surface':
                    if prior is None or following[:len(previous)]!=previous:
                        raise ValueError('A groom material-purpose owner lost its source operation chain.')
                    groom_operations.append((candidate['file'],operation))
        if not resource_seen:raise ValueError('A groom material-purpose context has no actual resource row.')
        groom_metadata=assembly.get('groomSourceDesigns',{});baseline_groom=baseline.get('groomSourceDesigns',{})
        import marshal
        purpose_state=dict(source=source,baseline=baseline_groom,target=groom_metadata,operations=groom_operations)
        purpose_key=('groom-material-purposes',hashlib.sha256(marshal.dumps(purpose_state,2)).hexdigest())
        if purpose_key not in verified:
            # This type-preserving fingerprint is private to the operation.
            # Every unseen state still passes the original strict JSON check.
            json.dumps(purpose_state,sort_keys=True,separators=(',',':'),allow_nan=False)
            purposes=copy.deepcopy(baseline_groom.get('materialPurposes',[]));files_seen=set();selected_files=None;selected_plan=None;pending_replays=[]
            for file,operation in groom_operations:
                receipt=document(operation['proof'])
                for reference in operation['authority']['inputs']:pinned(reference)
                if receipt.get('file')!=file or receipt['source']['sha256']!=operation.get('beforeSha256')or receipt['target']['sha256']!=operation.get('sha256'):
                    raise ValueError('A groom material-purpose receipt differs from its actual resource operation.')
                design_pin=receipt.get('designPlan')
                if design_pin is None:
                    if purposes or groom_metadata.get('materialPurposes'):
                        raise ValueError('A groom material-purpose change has no saved design owner.')
                    if receipt.get('composition')and document(receipt['composition']['plan']).get('primaryAtlas')is not None:
                        raise ValueError('A primary groom has no saved material-purpose plan ownership.')
                else:
                    design=document(design_pin);files=receipt.get('designFiles',[])
                    if not files or len(files)!=len(set(files))or files!=sorted(files)or file not in files or not set(files)<={Path(value['source']['path']).name for value in design['rows']}:
                        raise ValueError('A groom material-purpose ledger lost its selected resource owners.')
                    if design['source']!=source['assembly']or groom_metadata.get('plan')!=design_pin:
                        raise ValueError('A groom material-purpose ledger differs from its prepared source or design plan.')
                    if selected_plan is not None and(selected_plan!=design_pin or selected_files!=files)or file in files_seen:
                        raise ValueError('A groom material-purpose design has ambiguous resource receipts.')
                    selected_plan,selected_files=design_pin,files;files_seen.add(file)
                    composed=design.get('mode')=='retained-source-support'
                    if bool(receipt.get('composition'))!=composed or composed and(
                            receipt['composition']['plan']!=design_pin or receipt['composition']['fibers']!=design['fibers']):
                        raise ValueError('A groom material-purpose receipt lost its actual composition owner.')
                    planned=[value for value in design['rows']if Path(value['source']['path']).name==file]
                    actual={value['mesh']:value for value in receipt['rows']}
                    if len(actual)!=len(receipt['rows'])or len(planned)!=len({value['mesh']for value in planned})or set(actual)!={value['mesh']for value in planned}or any(
                            any(key not in actual[value['mesh']]or actual[value['mesh']][key]!=expected for key,expected in value.items())for value in planned):
                        raise ValueError('A groom material-purpose ledger differs from its actual receipt rows.')
                    for value in planned:
                        purposes=[item for item in purposes if(item['file'],item['mesh'])!=(file,value['mesh'])]
                        slots=value.get('materialSlots',[])
                        if composed and slots:
                            if 'sourceSlots'not in value:
                                if any(slot.get('purpose','fine')!='fine'for slot in slots):
                                    raise ValueError('A primary material-purpose slot has no native prefix declaration.')
                                continue
                            count=value['sourceSlots'];targets=set();normalized=[]
                            if type(count)is not int or count<=0:raise ValueError('A material-purpose ledger lost its native slot prefix.')
                            for slot in slots:
                                purpose=slot.get('purpose','fine')
                                if set(slot)-{'purpose'}!={'sourceSlot','targetSlot'}or purpose not in('fine','primary')or type(slot['sourceSlot'])is not int or type(slot['targetSlot'])is not int or not 0<=slot['sourceSlot']<count<=slot['targetSlot']or slot['targetSlot']in targets:
                                    raise ValueError('A material-purpose ledger has an invalid or duplicate slot owner.')
                                targets.add(slot['targetSlot']);normalized.append(dict(slot,purpose=purpose))
                            purposes.append(dict(file=file,mesh=value['mesh'],sourceSlots=count,slots=normalized))
                pending_replays.append((receipt,operation))
            if selected_files is not None and files_seen!=set(selected_files):
                raise ValueError('A groom material-purpose design omitted an actual resource receipt.')
            if groom_operations:purposes=sorted(purposes,key=lambda value:(value['file'],value['mesh']))
            if groom_metadata.get('materialPurposes',[])!=purposes:
                raise ValueError('A groom material-purpose ledger differs from its exact source and composition rows.')
            for receipt,operation in pending_replays:
                replay_key=('validated-groom-surface',operation['proof']['sha256'],hashlib.sha256(json.dumps(operation['authority'],sort_keys=True,separators=(',',':'),allow_nan=False).encode()).hexdigest())
                if replay_key not in verified:
                    validate_owned_groom_surface(receipt,operation['authority']['inputs'],verified);verified[replay_key]=True
            verified[purpose_key]=True
        baseline_key=('validated-resource',source['assembly']['sha256'],row['file'])
        if baseline_key not in verified:
            if original.get('allRetainedObjectFieldsExactAfterReferenceRebasing')is not True:
                validate_prepared_resource(original,baseline,verified)
            verified[baseline_key]=True
        expected=original['sha256']
        for operation in operations[len(original_operations):]:
            kind=operation.get('kind')
            if kind=='asset-root-preloads':
                receipt=document(operation['proof']);source=receipt['sourceCatalog']
                for receipt_key,baseline_key in(('manifest','catalog'),('assembly','assembly')):
                    declared=source[receipt_key];baseline_source=assembly['preparedSource'][baseline_key]
                    if declared['sha256'].lower()!=baseline_source['sha256'].lower()or pinned(declared)!=pinned(baseline_source):
                        raise ValueError('An asset preload repair belongs to another prepared source graph.')
                catalog=document(source['manifest']);original_assembly=document(source['assembly'])
                if operation.get('beforeSha256')!=expected or receipt['source']['sha256']!=expected or \
                        receipt['target']['sha256']!=operation['sha256']or receipt['file']!=row['file']:
                    raise ValueError('An asset preload repair lost its preceding source ownership.')
                for reference in(receipt['source'],receipt['target']):pinned(reference)
                for reference in operation['authority']['inputs']:pinned(reference)
                key=('prepared-reference-graph',source['manifest']['sha256'],source['assembly']['sha256'])
                if key not in verified:
                    verified[key]=prepared_resource_closure(Path(source['manifest']['path']).parent,catalog,original_assembly)
                facts=verified[key].validate_preloads(receipt)
                if any(receipt.get(name)!=value for name,value in facts.items()):
                    raise ValueError('A saved asset preload receipt differs from its reopened resource.')
                expected=operation['sha256']
            elif kind=='serialized-bone-order':
                receipt=document(operation['proof'])
                if operation.get('beforeSha256')!=expected or receipt['source']['sha256']!=expected or receipt['target']['sha256']!=operation['sha256']:
                    raise ValueError('A bone-order mutation lost its preceding resource ownership.')
                for reference in operation['authority']['inputs']:pinned(reference)
                resources.validate_renderer_bone_orders(receipt)
                expected=operation['sha256']
            elif kind=='source-owned-face':
                if operation.get('beforeSha256')!=expected or not operation.get('objects')or any(
                        operation.get(flag)is not True for flag in ('unrelatedObjectBytesExact','savedObjectBytesExact','cabAndExternalReferencesUnchanged')):
                    raise ValueError('An authored face mutation lost its preceding resource or object ownership.')
                for reference in operation['authority']['inputs']:pinned(reference)
                facts=operation.get('facts',[]);facts=facts if isinstance(facts,list)else[facts]
                if not facts or any(fact.get('sourceSkinUvAndFrameIdentityExact')is not True or
                        (not fact.get('sourceGuideCompanion')and fact.get('nativeOcularChannelsWithinTolerance')is not True)for fact in facts):
                    raise ValueError('An authored face mutation changed protected contact, body or expression channels.')
                expected=operation['sha256']
            elif kind=='native-eyebrow-contrast':
                if operation.get('file')!=row['file']or operation.get('identity',{}).get('sourceSha256')!=expected or any(
                        operation.get(flag)is not True for flag in ('unrelatedObjectsExact','alphaExact'))or not operation.get('textures'):
                    raise ValueError('A native eyebrow mutation lost its pigment or preceding resource ownership.')
                for reference in operation['identity']['inputs']:pinned(reference)
                for texture in operation['textures']:
                    pinned(texture['nativePigmentSource'])
                    if 'bc7PartitionsIndicesAndPbitsExact'in texture:
                        unowned=texture.get('bc7PartitionsIndicesAndPbitsExact')is True and texture.get('unownedDecodedTexelsExact')is True
                    else:
                        unowned=texture.get('unownedRgbaExact')is True
                    if texture.get('alphaExact')is not True or not unowned:
                        raise ValueError('A native eyebrow mutation changed unowned texture channels.')
                expected=operation['sha256']
            elif kind in ('native-facial-pigment','calibrated-facial-pigment'):
                if operation.get('file')!=row['file']or operation.get('beforeSha256')!=expected or operation.get('identity',{}).get('sourceSha256')!=expected:
                    raise ValueError('A native facial material lost its preceding resource owner.')
                for reference in operation['identity']['inputs']:pinned(reference)
                if operation['identity'].get('inputPlan'):pinned(operation['identity']['inputPlan'])
                if kind=='native-facial-pigment':validate_native_pigment_resource(operation)
                else:validate_calibrated_pigment_resource(operation)
                expected=operation['sha256']
            elif kind=='source-curved-skin-surface':
                receipt=document(operation['proof'])
                for reference in operation['authority']['inputs']:pinned(reference)
                for reference in (receipt['source'],receipt['target'],receipt['compiledBundle']):pinned(reference)
                if operation.get('beforeSha256')!=expected or receipt['source']['sha256']!=expected or \
                        receipt['target']['sha256']!=operation.get('sha256')or receipt.get('file')!=row['file']or any(
                        receipt.get(flag)is not True for flag in ('allOtherObjectBytesExact','resourcePayloadsExact','cabAndExternalReferencesUnchanged','savedObjectBytesExact')):
                    raise ValueError('A compiled skin surface lost its source resource or saved object ownership.')
                for mesh in receipt['meshes']:
                    pinned(mesh['packet']);pinned(mesh['nativePacket'])
                    if mesh['proof'].get('operation')!='adaptive-source-normal-surface'or not mesh['proof'].get('originalCutEdgesExact')or any(
                            part.get(flag)is not True for part in mesh['proof']['passes']for flag in
                            ('allOriginalVertexChannelsExact','allOriginalPhysicalFrameSamplesExact','nativeBindPosesExact','originalBoundaryLoopSamplesAndEdgesExact','allChildFacesRetainParentOrientation')):
                        raise ValueError('A compiled skin surface changed native skin, cuts or physical frames.')
                validate_compiled_skin_surface(receipt)
                expected=operation['sha256']
            elif kind=='source-native-skin-surface':
                receipt=document(operation['proof'])
                for reference in operation['authority']['inputs']:pinned(reference)
                if operation.get('beforeSha256')!=expected or receipt['source']['sha256']!=expected or receipt['target']['sha256']!=operation.get('sha256'):
                    raise ValueError('A native surface restoration lost its preceding resource owner.')
                validate_native_surface_restoration(receipt)
                expected=operation['sha256']
            elif kind=='saved-skin-lod-pigment':
                if operation['beforeSha256']!=expected:raise ValueError('A skin LOD pigment lost its preceding resource owner.')
                validate_skin_lod_pigment_resource(operation)
                expected=operation['sha256']
            elif kind=='source-skin-interface':
                receipt=document(operation['proof'])
                if operation['beforeSha256']!=expected or receipt['beforeSha256']!=expected or receipt['sha256']!=operation['sha256']:
                    raise ValueError('A skin interface lost its preceding resource owner.')
                validate_skin_interface_finish(receipt)
                expected=operation['sha256']
            elif kind=='skin-interface-cloth-repair':
                receipt=document(operation['proof'])
                if (receipt['file']!=row['file'] or operation['beforeSha256']!=expected or
                    receipt['source']['sha256']!=expected or receipt['target']['sha256']!=operation['sha256']):
                    raise ValueError('A skin interface repair lost its preceding resource owner.')
                for name in ('source', 'target', 'interface'):
                    pinned(receipt[name])
                verify_skin_interface_cloth_repair(receipt)
                expected=operation['sha256']
            elif kind=='source-skin-detail':
                if operation['beforeSha256']!=expected:raise ValueError('A source skin detail lost its preceding resource owner.')
                job=document(operation['inputPlan'])
                for reference in job['inputs']:pinned(reference)
                validate_source_skin_finish(operation)
                expected=operation['sha256']
            elif kind=='source-skin-material-bindings':
                receipt=document(operation['proof'])
                if operation['beforeSha256']!=expected or receipt['beforeSha256']!=expected or receipt['sha256']!=operation['sha256']:
                    raise ValueError('A source skin material lost its preceding resource owner.')
                for pin in operation.get('pigmentInputs',[]):pinned(pin)
                for reference in operation['fields']:
                    field=document(reference);key=('validated-source-skin-field',reference['sha256'])
                    if key not in verified:
                        for pin in document(field['inputPlan'])['inputs']:pinned(pin)
                        validate_source_skin_finish(field);verified[key]=True
                resources.validate_owned_material_textures(receipt)
                expected=operation['sha256']
            elif kind=='source-groom-crown-clearance':
                receipt=document(operation['proof'])
                for reference in operation['authority']['inputs']:pinned(reference)
                if operation.get('beforeSha256')!=expected or receipt['source']['sha256']!=expected or receipt['target']['sha256']!=operation.get('sha256')or receipt['file']!=row['file']:
                    raise ValueError('A source crown lost its preceding resource ownership.')
                validate_groom_crown_clearance(receipt,operation['authority']['inputs'],verified)
                expected=operation['sha256']
            elif kind=='source-groom-material-defaults':
                receipt=document(operation['proof'])
                for reference in operation['authority']['inputs']:pinned(reference)
                if operation.get('beforeSha256')!=expected or receipt['source']['sha256']!=expected or receipt['target']['sha256']!=operation.get('sha256')or receipt['file']!=row['file']:
                    raise ValueError('A groom material preset lost its preceding resource ownership.')
                validate_groom_material_defaults(receipt)
                expected=operation['sha256']
            elif kind=='source-owned-groom-surface':
                receipt=document(operation['proof'])
                for reference in operation['authority']['inputs']:pinned(reference)
                if operation.get('beforeSha256')!=expected or receipt['source']['sha256']!=expected or receipt['target']['sha256']!=operation.get('sha256')or receipt.get('file')!=row['file']:
                    raise ValueError('A groom surface lost its preceding resource ownership.')
                expected=operation['sha256']
            elif kind=='posed-garment-fit':
                receipt = document(operation['proof'])
                if (receipt['file'] != row['file'] or receipt['source']['sha256'] != expected
                        or operation['beforeSha256'] != expected or receipt['target']['sha256'] != operation['sha256']
                        or not receipt['meshes']):
                    raise ValueError('A garment fit lost its source mesh or catalog ownership.')
                pinned(receipt['source'])
                pinned(receipt['target'])
                for mesh in receipt['meshes']:
                    for pose in mesh['poses']:
                        pinned(pose['source'])
                        if 'layerReference'in pose:pinned(pose['layerReference'])
                expected = operation['sha256']
            elif kind=='source-owned-attachment-surface':
                receipt=document(operation['proof'])
                if operation.get('beforeSha256')!=expected or receipt['source']['sha256']!=expected or receipt['target']['sha256']!=operation.get('sha256')or receipt.get('file')!=row['file']:
                    raise ValueError('An attachment surface lost its preceding resource owner.')
                for reference in operation['authority']['inputs']:pinned(reference)
                key=('validated-attachment-surface',operation['proof']['sha256'])
                if key not in verified:
                    validate_attachment_surface(receipt,operation['authority']['inputs']);verified[key]=True
                expected=operation['sha256']
            elif kind=='compiled-groom-strand-atlas':
                if operation.get('sourceSha256')!=expected or operation.get('file')!=row['file']:
                    raise ValueError('A groom atlas lost its preceding resource ownership.')
                validate_compiled_groom_atlas(operation)
                expected=operation['sha256']
            elif kind=='source-shared-skin-boundary':
                receipt=document(operation['proof'])
                for reference in operation['authority']['inputs']:pinned(reference)
                for reference in (receipt['source'],receipt['target'],receipt['preparation'],receipt['engineReport']):pinned(reference)
                if operation.get('beforeSha256')!=expected or receipt['source']['sha256']!=expected or receipt['target']['sha256']!=operation.get('sha256')or receipt.get('file')!=row['file']:
                    raise ValueError('A shared skin surface lost its preceding resource ownership.')
                validate_shared_skin_surface(receipt)
                expected=operation['sha256']
            elif kind=='compiled-material-texture-bindings':
                import character_resources as resources
                receipt=document(operation['proof'])
                for reference in operation['authority']['inputs']:pinned(reference)
                if operation.get('beforeSha256')!=expected or receipt['beforeSha256']!=expected or receipt['sha256']!=operation.get('sha256')or receipt['file']!=row['file']:
                    raise ValueError('A compiled material texture lost its preceding resource ownership.')
                for reference in (receipt['source'],receipt['target'],*[texture['source']for texture in receipt['textures']]):pinned(reference)
                compiled={}
                for item in operation['compiledTextures']:
                    report=document(item['report']);recipe=document(item['recipe'])
                    if not report.get('success'):raise ValueError('A compiled material texture has no completed engine report.')
                    matches=[bundle for bundle in report.get('bundles')or[report]if bundle.get('inputSha256')==item['recipe']['sha256']]
                    if len(matches)!=1:raise ValueError('A compiled material texture has no matching recipe epoch.')
                    bundle=matches[0];assets=bundle.get('sourceAssets',[])
                    if bundle.get('sourceBundlesLoaded')!=bundle.get('sourceBundlesReleased')or not bundle.get('success'):
                        raise ValueError('A compiled material texture did not release its source resources.')
                    for asset in assets:
                        if asset.get('type')!='Texture2D':continue
                        patch=asset.get('encodedTexturePatch',{})
                        if any(asset.get(flag)is not True for flag in ('exact','physicalLayoutExact','encodedTextureGpuExact'))or any(
                                patch.get(flag)is not True for flag in ('nativeOriginalGpuUnchanged','samplerRgbaControlExact','sourceOriginalUnchanged','outsideAllowedBlocksExact','allNativeMipsGpuExact','metadataExact'))or len(patch.get('mips',[]))!=patch.get('mipCount'):
                            raise ValueError('A compiled material texture has incomplete saved mip or native-channel proof.')
                        declared=[value for source in recipe['sourceBundles']for value in source['assets']if value['name']==asset['name']]
                        if len(declared)!=1 or declared[0]['encodedTexturePatch']['patchedMipPayload']['sha256']!=patch.get('patchedMipSha256'):
                            raise ValueError('A compiled material texture differs from its declared encoded payload.')
                        compiled[(bundle['bundleSha256'].lower(),asset['name'])]=patch['patchedMipSha256']
                for texture in receipt['textures']:
                    if compiled.get((texture['source']['sha256'],texture['source']['name']))!=texture['payloadSha256']:
                        raise ValueError('A cloned material texture differs from its complete engine mip proof.')
                resources.validate_owned_material_textures(receipt)
                expected=operation['sha256']
            elif kind=='compiled-shader-only':
                for reference in operation['authority']['inputs']:pinned(reference)
                for name in ('source','target','compiledSource'):pinned(operation[name])
                validation=document(operation['validation'])
                if operation.get('beforeSha256')!=expected or operation['source']['sha256']!=expected or operation['target']['sha256']!=operation['sha256']:
                    raise ValueError('A compiled shader lost its preceding resource ownership.')
                validate_compiled_shader_proof(operation,validation)
                expected=operation['sha256']
            elif kind=='groom-vertex-normal-frame':
                for reference in operation['authority']['inputs']:pinned(reference)
                pinned(operation['source']);pinned(operation['target'])
                if operation.get('beforeSha256')!=expected or operation['source']['sha256']!=expected or operation['target']['sha256']!=operation['sha256']:
                    raise ValueError('A groom vertex field lost its preceding resource ownership.')
                validate_groom_vertex_frames(operation)
                expected=operation['sha256']
            elif kind in ('source-joint-skin-fields','source-skin-field-codec'):
                import character_resources as resources
                receipt=document(operation['proof'])
                for reference in operation['authority']['inputs']:pinned(reference)
                if operation.get('beforeSha256')!=expected or receipt['beforeSha256']!=expected or \
                        receipt['sha256']!=operation.get('sha256')or receipt.get('file')!=row['file']:
                    raise ValueError('A skin field lost its preceding resource ownership.')
                pinned(receipt['source']);pinned(receipt['target'])
                for texture in receipt['textures']:pinned(texture['raw'])
                resources.validate_owned_texture_mips(receipt)
                if kind=='source-skin-field-codec':
                    codecs=operation.get('codecs')or[operation['codec']];saved={}
                    for codec in codecs:
                        inputs=document(codec['input']);report=document(codec['report']);validation=document(codec['verification'])
                        if report.get('inputSha256')!=codec['input']['sha256']or not report.get('completed')or \
                                not report.get('savedReloadGpuExact')or validation.get('status')!='PRODUCTION_FIELD_ONLY_BC7_PAYLOAD_AND_ALL_GPU_MIPS_EXACT':
                            raise ValueError('A compressed skin field has no matching complete GPU/save/reload epoch.')
                        tasks={task['id']:task for task in inputs['tasks']}
                        for item in report['saved']:
                            if item.get('variant')!='FieldBC7':continue
                            record=(item,tasks[item['id']]);previous=saved.get(item['encodedSha256'])
                            if previous and (previous[1]['rawSha256'],previous[1]['width'],previous[1]['height'],previous[1]['mipCount'],previous[1]['sRGB'])!=(record[1]['rawSha256'],record[1]['width'],record[1]['height'],record[1]['mipCount'],record[1]['sRGB']):
                                raise ValueError('A compressed skin field has incompatible codec source authorities.')
                            saved[item['encodedSha256']]=record
                    for texture in receipt['textures']:
                        encoded=saved.get(texture['payloadSha256'])
                        if texture['format']!=25 or not encoded or not encoded[0].get('reloadGpuExact')or \
                                texture['sourcePayloadSha256']!=encoded[1]['rawSha256']:
                            raise ValueError('A compressed skin field differs from its actual prepared GPU control.')
                elif any(texture['format']!=4 for texture in receipt['textures']):
                    raise ValueError('A numeric skin field requires its complete RGBA source mips.')
                expected=operation['sha256']
            else:raise ValueError('A resource refinement has an unsupported following mutation: '+str(kind))
        if expected!=row['sha256']:raise ValueError('A resource refinement has unverified final bytes.')
        return
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
            if exact_main_storage_bytes(finishing.get('colorCorrections',[]))>EXACT_MAIN_CATALOG_MAX_BYTES:
                raise ValueError('Exact Main storage exceeds its catalog-wide budget.')
            for reference in finishing['inputs']:pinned(reference)
            pinned(dict(path=str(Path(finishing['source'])/(Path('Catalog')/RESOURCE_FILES['manifest'])),sha256=finishing['sourceCatalogSha256']))
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
            if operation.get('kind')=='groom-normal-transport':
                for reference in operation['authority']['inputs']:pinned(reference)
                receipt=document(operation['proof'])
                validate_groom_shading(receipt,row['file'],expected,operation.get('sha256'))
                if operation.get('beforeSha256')!=expected or operation['target']['sha256']!=operation['sha256']:
                    raise ValueError('A groom shading mutation is disconnected from its resource checkpoint.')
                pinned(dict(path=receipt['source'],sha256=expected));pinned(operation['target'])
                expected=operation['sha256']
                continue
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
                validate_main_calibration(texture,storage_by_id.get(texture['object']))
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
        validation=document(proof['validation'])
        if validation.get('status')!='PASS_SHADER_MATERIAL_AND_BRDF_CONTRACTS':
            raise ValueError('A prepared shader has no matching validation.')
        key=('compiled-shader',proof['source']['sha256'],target['sha256'],proof['validation']['sha256'])
        if key not in verified:
            validate_compiled_shader_proof(proof,validation);verified[key]=True
        pinned(proof['compiledSource'])
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


def publish_resource_catalog(candidate,current,source,manifest,quality,*,validate_only=False):
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
    verified={};accepted_rows={}
    accepted_path=source.parent/'ResourceAssembly.json'
    if accepted_path.is_file():
        accepted=read(accepted_path);source_payloads=catalog_payloads(read(source/RESOURCE_FILES['manifest']))
        if resource_art_acceptance(accepted,quality['sourceManifestSha256']):
            if accepted.get('status')!='PASS_STANDALONE_CHARACTER_RESOURCES'or accepted.get('originalModelDependenciesInherited')is not False or accepted.get('catalogSha256')!=quality['sourceManifestSha256']:
                raise ValueError('The accepted source proof has no matching closed catalog.')
            accepted_rows={row['file']:row for row in accepted['proofs']}
            if len(accepted['proofs'])!=len(source_payloads)or {name:row['sha256']for name,row in accepted_rows.items()}!=source_payloads:
                raise ValueError('The accepted source proof differs from its catalog.')
            accepted_sha=sha(accepted_path)
            for file,digest in source_payloads.items():
                if sha(checked_catalog_path(source,file))!=digest:raise ValueError('An accepted source payload changed: '+file)
                verified[('validated-resource',accepted_sha,file)]=True
    for row in rows:
        retained=Path(row['source'])
        if retained.is_relative_to(current):retained=source/retained.relative_to(current)
        if sha(checked_catalog_path(candidate,row['file']))!=row['sha256']:
            raise ValueError('A prepared resource or its packed fields changed.')
        approved=accepted_rows.get(row['file'])
        # A closed, adopted catalog owns its inherited bytes. Retired intermediate
        # input files are not a second source owner for later revisions.
        inherited=proof.get('preparedSource')and approved and all(row.get(key)==approved.get(key)for key in ('source','sourceSha256'))
        if not inherited and sha(retained)!=row['sourceSha256']:raise ValueError('A prepared resource source changed.')
        if proof.get('preparedSource')or row['allRetainedObjectFieldsExactAfterReferenceRebasing']is not True:
            validate_prepared_resource(row,proof,verified)
    for input in proof['inputs']:
        if sha(Path(input['path']))!=input['sha256']:raise ValueError('A standalone preparation authority changed.')
    original_hashes=catalog_payloads(read(source/RESOURCE_FILES['manifest']))
    changed=[]
    for file,digest in payloads.items():
        target=checked_catalog_path(current,file)
        if target.exists()and file not in original_hashes:raise ValueError('An unowned public payload would be overwritten: '+file)
        if not target.exists()or sha(target)!=digest:changed.append(file)
    manifest_bytes=(candidate/RESOURCE_FILES['manifest']).read_bytes()
    if hashlib.sha256(manifest_bytes).hexdigest()!=quality['candidateManifestSha256']:
        raise ValueError('The verified resource manifest changed before publication.')
    receipt=dict(status='RUNNING_ATOMIC_PUBLICATION',sourceCatalog=str(source),candidate=str(candidate),changed=changed,
                 originalCandidateManifestSha256=quality['candidateManifestSha256'],publishedManifestSha256=hashlib.sha256(manifest_bytes).hexdigest(),
                 resourceAssembly=quality['resourceAssembly'],standaloneResources=len(sources),runtimeReplacementDeclarations=0,
                 payloadShaUnchangedByNaming=True)
    atomic_publish_catalog(candidate,current,source,manifest_bytes,payloads,{file:file for file in payloads},original_hashes,receipt,validate_only=validate_only)
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


def material_main_contact_vertices(row,head):
    """Use the facial source's own articulated contact tissue for skin pigment."""
    import numpy as np
    paths=list(row['headNativeBonePaths'])
    if row['headSource'].get('joint'):
        paths.extend(row['bodyNativeBonePaths'][i]for i in row['headSource']['joint']['donorBones'])
    packet={key:(value.tolist()if isinstance(value,np.ndarray)else value)for key,value in head['packet'].items()}
    owner,_=source_owner()
    skin=dict(mesh=packet['name'],bones=paths,materials=[dict(shader=row['sampler']['shaderFamily'])])
    return owner.humanoid_face_contact_guard(packet,skin)<1e-6


def generate_material_main(row,family_rows,packets,head_main,body_main,source_epoch,memo=None):
    """Use the one Main-only numeric owner, with explicit immutable data inputs."""
    import material_fields as fields
    hp,bp=packets[row['headCanonical']['path']],packets[row['bodyCanonical']['path']]
    contact=material_main_contact_vertices(row,fields.source_context(row,hp,bp)[0])if row['sampler']['shaderFamily']=='Lux/Human/Skin'else None
    return fields.prepare_head_main(row,hp,bp,family_rows,packets,head_main,body_main,source_epoch,memo,protected_vertices=contact)


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
    if not isinstance(plan['tasks'],list)or not 1<=len(plan['tasks'])<=32:raise ValueError('A codec batch must contain 1..32 skin texture tasks.')
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
        if len(rows)!=1:raise ValueError('The exact prepared skin texture task is not unique.')
        task=copy.deepcopy(rows[0]);width=task['width']
        if task['role']not in ('Main','Normal')or type(width)is not int or width<=0 or width&(width-1)or task['height']!=width or task['mipCount']!=width.bit_length()or type(task['sRGB'])is not bool or task['sRGB']!=(task['role']=='Main'):
            raise ValueError('The frozen codec requires complete square skin mips in the role\'s colour space.')
        if not re.fullmatch('[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}',task['id'])or task['id']in identities:raise ValueError('Invalid or duplicate codec identity.')
        identities.add(task['id'])
        if any(task.get(k)for k in('precompressedNative','precompressedField','precompressedNativeSha256','precompressedFieldSha256')):
            raise ValueError('Precompression needs explicit saved native-control evidence; changed-field reuse is not an implicit fresh encode.')
        for key in('raw','nativeRaw','ownedMask','protectedMask','sourceBundle'):
            task[key]=str(material_codec_file(task[key],Path(prep_ref['path']).parent))
            add_pin(dict(path=task[key],sha256=task[key+'Sha256']))
        task=resolve_material_task(task,authority);declared=authority['records'][task['sourceEncodedSha256']]
        if task['sRGB']!=(declared['colorSpace']==1):raise ValueError('A skin texture source color space changed.')
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
    if len(tasks)!=len(inputs['tasks'])or not 1<=len(tasks)<=32 or any(t['role']not in ('Main','Normal')or t['sRGB']!=(t['role']=='Main')for t in tasks.values()):raise ValueError('Not a bounded skin texture codec epoch.')
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
    if not reuse_snapshot:(snapshot/RESOURCE_FILES['manifest']).write_bytes(manifest_path.read_bytes())
    if sha(snapshot/RESOURCE_FILES['manifest'])!=source_sha:raise ValueError('The snapshot manifest changed.')
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
    manifest=catalog/RESOURCE_FILES['manifest'];write(manifest,candidate)
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
    parser.add_argument('--rebuild-collars',action='store_true')
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
    selected=list(document['rows'])if args.rebuild_collars else[row for row in document['rows']if row['lod']==0]
    if not selected:raise ValueError('No declared native skin cut was selected.')
    identities=[(row['internalId'].casefold(),row['lod'])for row in selected]
    if len(identities)!=len(set(identities)):raise ValueError('The native skin cut source is ambiguous.')
    destination.mkdir(parents=True);rows=[];meshes=[]
    for row in selected:
        receiver=material_packet(row['headCanonical']);donor=material_packet(row['bodyCanonical'])
        cuts=row.get('headSource',{}).get('correspondence',{}).get('sourceCorrespondences',[])
        pairs=art.shared_skin_boundary_pairs(receiver,donor,row['headNativeBonePaths'],row['bodyNativeBonePaths'])if args.rebuild_collars else[(int(vertex),min(cut['bodyVertices']))for cut in cuts for vertex in cut['headVertices']]
        tolerance=max(1e-4,max((cut['distanceMetres']for cut in cuts),default=0)+1e-5)
        shading_owner='donor'
        if row['nativeHeadFingerprint']in authored_normals:
            declaration=authored_meshes.get(row['nativeHeadFingerprint'])
            if declaration is None or declaration['authoredFingerprint']!=fingerprint(receiver)[0]:
                raise ValueError('The authored normal atlas and its receiver mesh do not share a rest shading frame.')
            shading_owner='receiver'
        if args.rebuild_collars:
            if shading_owner!='donor':raise ValueError('Rebuilt collar topology requires its newly prepared geometric normal field.')
            candidate,proof=art.rebuild_skin_boundary(receiver,donor,row['headNativeBonePaths'],row['bodyNativeBonePaths'],repeat_uv=row['sampler']['repeatUvAliases'])
        else:
            candidate,proof=art.conform_skin_boundary(receiver,donor,row['headNativeBonePaths'],row['bodyNativeBonePaths'],pairs,maximum_source_gap=tolerance,shading_frame_owner=shading_owner)
        candidate['sourceFingerprint']=fingerprint(candidate)[0]
        name=asset_names.source_label(row['head'])+'_LOD'+str(row['lod'])
        target=destination/(name+'.json.gz')
        target.write_bytes(gzip.compress((json.dumps(candidate,separators=(',',':'),allow_nan=False)+'\n').encode('utf-8'),mtime=0))
        mesh=recipe_mesh(candidate);mesh['name']=name;meshes.append(mesh)
        rows.append(dict(asset=row['internalId'],lod=row['lod'],nativeFingerprint=row['nativeHeadFingerprint'],input=row['headCanonical'],donor=row['bodyCanonical'],
            sourceBonePaths=row['headNativeBonePaths'],bonePaths=[*row['headNativeBonePaths'],*[bone['path']for bone in proof['appendedBones']]],donorBonePaths=row['bodyNativeBonePaths'],
            packet=dict(path=str(target),sha256=sha(target)),mesh=name,authoredFingerprint=candidate['sourceFingerprint'],vertexPairs=pairs,sourceGapBudget=tolerance,**proof))
    recipes=[]
    for first in range(0,len(meshes),100):
        group=first//100+1;target=destination/('Group'+str(group)+'/Recipe.json')
        encode_recipe(dict(schemaVersion=1,bundleName='completed_skin_boundary_'+str(group)+'.bundle',meshes=meshes[first:first+100]),target)
        recipes.append(str(target))
    result=dict(status='PREPARED_SHARED_NATIVE_BOUNDARY',input=dict(path=str(input_path),sha256=sha(input_path)),
        source=dict(path=str(ROOT/'tools/blender-models/CharacterFaces/generate.py'),sha256=sha(ROOT/'tools/blender-models/CharacterFaces/generate.py')),
        rows=rows,recipes=recipes,materialCatalog=material_authority,scope='Declared physical skin cuts, preserving each material family and its native anatomy.')
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




def resource_release_metadata(version,configuration=None):
    configuration=RESOURCE_PACKAGE if configuration is None else configuration
    if not isinstance(version,str)or not re.fullmatch(configuration['versionPattern'],version):raise ValueError('A resource version matching the package configuration is required.')
    return dict(copy.deepcopy(configuration['releaseMetadata']),tag_name=configuration['tagName'].format(version=version),
        draft=True,prerelease=False,make_latest='false')


def validate_resource_release(metadata,version,configuration=None):
    configuration=RESOURCE_PACKAGE if configuration is None else configuration
    expected=resource_release_metadata(version,configuration)
    if not isinstance(metadata,dict)or any(metadata.get(key)!=expected[key]for key in (*configuration['releaseMetadata'],'tag_name')):
        raise ValueError('Resource release metadata differs from the package configuration.')


def create_resource_packs(manifest_path,output_directory,release_version,maximum_bytes=256*1024*1024,*,configuration=None) -> dict[str, Any]:
    """Stream independent generated resources into bounded release archives."""
    import zipfile
    configuration=RESOURCE_PACKAGE if configuration is None else configuration
    release=resource_release_metadata(release_version,configuration);validate_resource_release(release,release_version,configuration)
    if not 0<maximum_bytes<=1024*1024*1024:raise ValueError('A positive pack limit is required.')
    manifest_path=Path(manifest_path).resolve();output=checked_catalog_path(output_directory)
    manifest=read(manifest_path)
    assembly_path=manifest_path.parent.parent/'ResourceAssembly.json'
    if assembly_path.exists():
        assembly=read(assembly_path)
        if assembly.get('sourceSkinDetails')and not assembly.get('skinMaterialBindings',{}).get('localMaterials'):
            raise ValueError('Bind source skin fields to their renderer material owners before packaging.')
        if assembly.get('groomSourceDesigns'):
            plan_pin=assembly['groomSourceDesigns']['plan']
            if sha(plan_pin['path'])!=plan_pin['sha256']:raise ValueError('The groom source ownership plan changed.')
            if read(plan_pin['path']).get('mode')not in('retained-source-support','source-owned-core'):
                raise ValueError('Compose finished fibers with the retained source support before packaging.')
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
    files_configuration=configuration['files']
    output.mkdir(parents=True,exist_ok=True);marker=output/files_configuration['packageInputs'];metadata=output/files_configuration['packs']
    release_path=output/files_configuration['release']
    base_url=configuration['downloadBaseUrl'].format(tag=release['tag_name'])
    if marker.exists():
        if read(marker)!=owner:raise ValueError('The pack directory belongs to other inputs.')
        if metadata.exists():
            prior=read(metadata)
            if prior['files']!=files:raise ValueError('Resource lengths changed after packaging.')
            if prior['baseUrl']!=base_url:raise ValueError('The resource distribution configuration changed.')
            for ordinal,pack in enumerate(prior['packs'],1):
                if pack['file']!=configuration['archiveName'].format(ordinal=ordinal):raise ValueError('The resource archive naming configuration changed.')
                if (output/pack['file']).stat().st_size>maximum_bytes or sha(output/pack['file'])!=pack['sha256']:
                    raise ValueError('A completed resource pack changed.')
            write(release_path,release)
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
    if len(groups)>configuration['maximumPacks']:raise ValueError('The content set exceeds its declared pack count.')
    packs=[]
    for ordinal,group in enumerate(groups,1):
        name=configuration['archiveName'].format(ordinal=ordinal);path=checked_catalog_path(output,name);temporary=output/(name+'.writing')
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
        baseUrl=base_url,files=files,packs=packs)
    write(metadata,result)
    write(release_path,release)
    return result


def package_resources():
    parser=argparse.ArgumentParser();parser.add_argument('--manifest',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True);parser.add_argument('--release-version',required=True)
    args=parser.parse_args();result=create_resource_packs(args.manifest,args.output,args.release_version)
    print(json.dumps(dict(status='PASS_RESOURCE_PACKS',packs=len(result['packs']),bytes=sum(row['bytes']for row in result['packs']))))


def completed_resource_payloads(directory,assembly=None,manifest=None):
    """Resolve final and intermediate payloads through their completed producer owner."""
    directory=checked_catalog_path(directory)
    assembly=read(checked_source_path(directory/'ResourceAssembly.json'))if assembly is None else assembly
    manifest_path=checked_source_path(directory/(Path('Catalog')/RESOURCE_FILES['manifest']))
    if assembly.get('status')!='PASS_STANDALONE_CHARACTER_RESOURCES'or assembly.get('catalogSha256')!=sha(manifest_path):
        raise ValueError('A retired catalog has no unchanged completed resource owner.')
    manifest=read(manifest_path)if manifest is None else manifest
    payloads={directory/'Catalog'/name:digest for name,digest in catalog_payloads(manifest).items()}
    checkpoint=directory/'GeometryPreparation.json'
    if checkpoint.is_file():
        checked_source_path(checkpoint)
        geometry=read(checkpoint);declaration=assembly.get('nativeFaceContours',{})
        retained=assembly.get('faceFinishing',{}).get('geometryPreparation')
        pinned=retained and Path(retained['path']).resolve()==checkpoint.resolve()and retained['sha256']==sha(checkpoint)
        declared=bool(declaration.get('owner'))and declaration.get('owner')==geometry.get('owner')and all(
            declaration.get(key)==geometry.get(key)for key in ('coverage','rows','humanStyleFiles'))
        if geometry.get('status')!='PREPARED_GEOMETRY'or not(pinned or declared):
            raise ValueError('An intermediate geometry catalog has no matching completed producer owner.')
        expected=catalog_payloads(geometry['manifest']);proofs=geometry['assembly']['proofs']
        if {row['file']:row['sha256']for row in proofs}!=expected or len(proofs)!=len(expected)or \
                set(expected)!=set(catalog_payloads(manifest)):
            raise ValueError('An intermediate geometry catalog differs from its declared resource closure.')
        folder=checked_catalog_path(directory/'GeometryCatalog')
        payloads.update({folder/name:digest for name,digest in expected.items()})
    return payloads


def create_resource_retirement_plan(directories,reference_path,output,storage_root=None) -> dict[str, Any]:
    """Select declared old catalog payloads; retain every current input path."""
    storage_root=(storage_root or ROOT/'tools/game-test/artifacts').resolve()
    payloads={};guards=[]
    for directory in directories:
        directory=checked_catalog_path(directory)
        if directory==storage_root or not directory.is_relative_to(storage_root):raise ValueError('Retired resource catalogs must be below the declared workspace.')
        payloads.update({path:digest for path,digest in completed_resource_payloads(directory).items()if path.suffix=='.bundle'})
        path=directory/'ResourceAssembly.json';guards.append(dict(path=str(path),sha256=sha(path)))
    return create_declared_payload_plan(payloads,reference_path,output,storage_root,guards)


def create_declared_payload_plan(payloads,reference_path,output,storage_root=None,guard_inputs=()) -> dict[str,Any]:
    """Build the common retirement contract from validated producer declarations."""
    artifacts=ROOT/'tools/game-test/artifacts'
    owned_roots=(artifacts,ROOT/'tools/asset-authoring/workspace')
    storage_root=(storage_root or artifacts).resolve()
    if storage_root not in owned_roots:raise ValueError('Resource retirement needs an owned tool workspace root.')
    reference_path=checked_source_path(reference_path)
    reference_sha=sha(reference_path)
    reference=read(reference_path)
    if set(reference)!={'schemaVersion','paths'} or reference['schemaVersion']!=1 or not isinstance(reference['paths'],list):
        raise ValueError('Resource retirement needs a current-input reference manifest.')
    protected=set();protected_directories=[]
    for value in reference['paths']:
        if not isinstance(value,str)or not value:raise ValueError('A retained resource input is not a path.')
        path=Path(value);path=path if path.is_absolute()else reference_path.parent/path
        path=path.resolve()
        if not any(path.is_relative_to(root)for root in owned_roots):raise ValueError('A retained resource input escaped owned tool workspaces.')
        protected.add(str(path).casefold())
        if path.is_dir():protected_directories.append(str(path).casefold()+os.sep)
    rows={};roots=set()
    # Scandir supplies each leaf's own no-follow metadata. Deletion repeats the
    # path, content and producer-input checks while its input leases remain held.
    leaves={}
    for catalog in {Path(path).parent for path in payloads}:
        checked_catalog_path(catalog)
        if catalog==storage_root or not catalog.is_relative_to(storage_root):raise ValueError('Retired resource catalogs must be below the declared workspace.')
        if not catalog.exists():continue
        with os.scandir(catalog)as entries:
            leaves.update({catalog/entry.name:entry.stat(follow_symlinks=False)for entry in entries if entry.is_file(follow_symlinks=False)})
    guards={}
    for pin in guard_inputs:
        path=checked_source_path(pin['path'])
        if not path.is_relative_to(ROOT)or sha(path)!=pin['sha256']:raise ValueError('A retirement producer input changed.')
        guards[str(path).casefold()]=dict(path=str(path),sha256=pin['sha256'])
    for path,digest in payloads.items():
        path=Path(path);key=str(path).casefold();info=leaves.get(path)
        if info is None or key in protected or any(key.startswith(prefix)for prefix in protected_directories):continue
        if key in guards:raise ValueError('A retirement producer input cannot be selected as payload.')
        if getattr(info,'st_file_attributes',0)&0x400:raise ValueError('A retired payload contains a reparse point.')
        rows[key]=dict(path=str(path),bytes=info.st_size,lastWriteUtcTicks=info.st_mtime_ns//100+621355968000000000,sha256=digest)
        if guards:rows[key]['role']='payload'
        roots.add(str(path.parent.relative_to(storage_root)))
    if sha(reference_path)!=reference_sha:raise ValueError('Retained inputs changed during planning.')
    plan=dict(schemaVersion=1,artifactRoot=str(storage_root),reference=dict(path=str(reference_path),sha256=reference_sha),
        scope='Inactive declared resource outputs outside retained inputs.',roots=sorted(roots),files=sorted(rows.values(),key=lambda row:row['path']),
        logicalBytes=sum(row['bytes']for row in rows.values()),guardInputs=list(guards.values()))
    output=checked_catalog_path(output)
    if not output.is_relative_to(artifacts):raise ValueError('A resource retirement plan must be saved below artifacts.')
    write(output,plan)
    return plan


def plan_resource_retirement():
    parser=argparse.ArgumentParser();parser.add_argument('--retire-directory',type=Path,action='append',required=True)
    parser.add_argument('--reference',type=Path,required=True)
    args=parser.parse_args()
    result=create_resource_retirement_plan(args.retire_directory,args.reference,require_workspace()/'PayloadRetirementPlan.json')
    print(json.dumps(dict(status='PLANNED_RESOURCE_RETIREMENT',files=len(result['files']),logicalBytes=result['logicalBytes'])))


def resource_tone_context(contexts,record):
    selector=record['sourceContext']
    keys=('nativeHeadFingerprint','nativeBodyFingerprint','lod','samplerSourceInputKey')
    rows=[row for row in contexts['rows']if all(row[key]==selector[key]for key in keys)]
    if len(rows)!=1:raise ValueError('A Main field has no unique native face context.')
    row=rows[0];encoded=row['sampler']['textureInputs']['headMain']['encodedSha256']
    family=[other for other in contexts['rows']if other['sampler']['textureInputs']['headMain']['encodedSha256']==encoded]
    return row,family


def prepare_resource_tone_coefficient(job) -> dict[str, Any]:
    import inspect
    import numpy as np
    import material_fields as fields
    record=read(job['preparation']);task=record['task'];raw_path=Path(task['nativeRaw'])
    if sha(raw_path)!=task['nativeRawSha256']:raise ValueError('A native Main reference changed.')
    old=record['sourceInput']['coefficient']
    if sha(old['path'])!=old['sha256']:raise ValueError('A native Body calibration changed.')
    inputs=[dict(path=str(path),sha256=sha(path))for path in (job['preparation'],job['contexts'],raw_path,old['path'],fields.__file__)]
    signature=hashlib.sha256('\n'.join(inspect.getsource(function)for function in
        (prepare_resource_tone_coefficient,resource_tone_context,material_main_inputs)).encode('utf8')).hexdigest()
    if Path(job['output']).exists():
        cached=read(job['output'])
        if cached.get('status')=='PREPARED_SOURCE_CALIBRATION'and cached.get('inputs')==inputs and cached.get('codeSignature')==signature and cached['coefficient'].get('policy')=='surface-local-v4':
            return dict(head=record['head'],path=str(job['output']),sha256=sha(job['output']),reused=True)
    contexts=read(job['contexts']);row,family=resource_tone_context(contexts,record)
    packets=material_main_inputs(row,family)
    head,head_fraction,region_proof=fields.regional_head_source(row,packets[row['headCanonical']['path']],packets[row['bodyCanonical']['path']])
    raw=np.fromfile(raw_path,np.uint8,count=task['width']*task['height']*4).reshape(task['height'],task['width'],4)
    neck_skin=np.clip((1-head_fraction)*2,0,1);neck_skin=neck_skin*neck_skin*(3-2*neck_skin)
    coefficient=fields.surface_tone_references(head,fields.linear_rgba(raw),read(old['path'])['bodyTarget'],reference_eligibility=neck_skin)
    result=dict(status='PREPARED_SOURCE_CALIBRATION',head=record['head'],coefficient=coefficient,inputs=inputs,codeSignature=signature,skinReferenceRegion=region_proof)
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
    authority: dict[str,Any]=dict(sourceSha256=job['expectedSha256'],codeSignature=signature,
        codecPolicy=dict(blockRefinementError=MAIN_RGB_BLOCK_REFINEMENT_ERROR,firstThreeMipRgbRmsLimit=MAIN_RGB_RMS_LIMIT,allMipRgbRmsLimit=MAIN_RGB_RMS_LIMIT,
            maximumExactTextureBytes=EXACT_MAIN_TEXTURE_MAX_BYTES,maximumExactCatalogBytes=EXACT_MAIN_CATALOG_MAX_BYTES),
        inputs=[dict(path=path,sha256=sha(path))for path in sorted(references)])
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
        if tree['m_TextureFormat']not in (4,25):raise ValueError('A calibrated Main resource has no supported RGB/alpha owner.')
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
            replacement,codec_proof=fields.fit_bc7_rgb_field(payload,width,height,corrected,editable,maximum_rgb_error=MAIN_RGB_BLOCK_REFINEMENT_ERROR if len(expected)<3 else None,allow_reencoding=True)
            _,owned_pixels=fields.whole_block_pixels(editable)
            exact=corrected.copy();exact[:,:,3]=original_pixels[:,:,3];exact[~owned_pixels]=original_pixels[~owned_pixels];exact_mips.append(exact.tobytes())
            source_mips.append(payload)
            if len(expected)<3 and codec_proof['rootMeanSquareRgbError']>MAIN_RGB_RMS_LIMIT:
                needs_exact=True
            expected.append(replacement);mip_proofs.append(dict(level=level,width=width,height=height,tone=tone_proof,codec=codec_proof,atlas=atlas_proof))
        if start!=len(encoded)or len(expected)!=tree['m_MipCount']:raise ValueError('A Main correction omitted a declared native mip.')
        texels=sum(mip['width']*mip['height']for mip in mip_proofs)
        rgb_error=math.sqrt(sum(mip['codec']['rootMeanSquareRgbError']**2*mip['width']*mip['height']for mip in mip_proofs)/texels)
        needs_exact|=rgb_error>MAIN_RGB_RMS_LIMIT
        replacement=b''.join(expected);pre_calibration=b''.join(source_mips);legacy_alpha=[]
        old_format=tree['m_TextureFormat'];old_start=0
        if needs_exact:
            replacement=b''.join(exact_mips)
            if len(replacement)>EXACT_MAIN_TEXTURE_MAX_BYTES:
                write(Path(job['receipt']).with_suffix('.rejected.json'),dict(status='MAIN_CODEC_BUDGET_REJECTED',texture=item['name'],
                    exactBytes=len(replacement),maximumExactBytes=EXACT_MAIN_TEXTURE_MAX_BYTES,allMipTexelWeightedRgbRms=rgb_error,mips=mip_proofs,authority=authority))
                raise ValueError('An exact Main mip chain exceeds its bounded storage budget.')
            for mip in mip_proofs:
                mip['bc7Attempt']=mip['codec'];mip['codec']=dict(storage='RGBA32',alphaExact=True,maximumRgbError=0,rootMeanSquareRgbError=0.,sourceOwnedBlocksExact=True)
            rgb_error=0.
        for level in range(tree['m_MipCount']):
            width=max(1,tree['m_Width']>>level);height=max(1,tree['m_Height']>>level);size=((width+3)//4)*((height+3)//4)*16
            at=sum(((max(1,tree['m_Width']>>i)+3)//4)*((max(1,tree['m_Height']>>i)+3)//4)*16 for i in range(level))
            a=np.asarray(Image.frombytes('RGBA',(width,height),pre_calibration[at:at+size],'bcn',7))
            old_size=width*height*4 if old_format==4 else size
            legacy=np.asarray(Image.frombytes('RGBA',(width,height),old[old_start:old_start+old_size])if old_format==4 else
                Image.frombytes('RGBA',(width,height),old[old_start:old_start+old_size],'bcn',7));old_start+=old_size
            difference=legacy[:,:,3].astype(float)-a[:,:,3]
            legacy_alpha.append(dict(level=level,maximumError=int(np.abs(difference).max()),rootMeanSquareError=float(np.sqrt(np.mean(difference**2)))))
            b=np.frombuffer(exact_mips[level],np.uint8).reshape(height,width,4)if needs_exact else np.asarray(Image.frombytes('RGBA',(width,height),replacement[at:at+size],'bcn',7))
            if not np.array_equal(a[:,:,3],b[:,:,3]):raise ValueError('A Main correction changed its pre-calibration source alpha.')
        if old_start!=len(old):raise ValueError('The existing Main storage has an incomplete mip chain.')
        target_format=4 if needs_exact else 25
        if target_format!=old_format or len(replacement)!=stream['size']:
            streams[member].extend(bytes((-len(streams[member]))%16));offset=len(streams[member]);streams[member].extend(replacement)
            changed=resources.texture_main_storage(tree,replacement,offset,target_format);obj.save_typetree(changed)
            storage_changes.append(dict(object=item['identifier'],before={key:tree[key]for key in ('m_TextureFormat','m_CompleteImageSize','m_StreamData')},
                after={key:changed[key]for key in ('m_TextureFormat','m_CompleteImageSize','m_StreamData')},additionalBytes=len(replacement),exactRgbaMipChain=needs_exact))
        else:streams[member][stream['offset']:stream['offset']+stream['size']]=replacement
        proofs.append(dict(texture=item['name'],object=item['identifier'],mips=mip_proofs,allMipTexelWeightedRgbRms=rgb_error,
            preparation=dict(path=item['preparation'],sha256=sha(item['preparation'])),coefficient=dict(path=item['coefficient'],sha256=sha(item['coefficient'])),
            nativeRgba=dict(path=str(raw_path),sha256=sha(raw_path)),encodedSource=dict(path=str(source_path),sha256=sha(source_path)),
            beforePayloadSha256=hashlib.sha256(old).hexdigest(),afterPayloadSha256=hashlib.sha256(replacement).hexdigest(),preCalibrationAlphaAllMipsExact=True,legacyAlphaDrift=legacy_alpha))
        validate_main_calibration(proofs[-1],next((change for change in storage_changes if change['object']==item['identifier']),None))
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
    if exact_main_storage_bytes(result)>EXACT_MAIN_CATALOG_MAX_BYTES:
        raise ValueError('Exact Main storage exceeds its catalog-wide budget.')
    return result


def finish_resource_skin_cuts(catalog,assembly,material_contexts,output):
    """Close raster cracks on the shared, already authored native skin cuts."""
    from functools import lru_cache
    import numpy as np
    import character_resources as resources
    shape,geometry=source_owner()
    catalog=Path(catalog);output=Path(output);output.mkdir(parents=True,exist_ok=True)
    manifest=read(catalog/RESOURCE_FILES['manifest'])
    proofs={row['file']:row for row in assembly['proofs']}
    current_cabs={row['newCab']:row['file']for row in assembly['proofs']}
    resources_by_source={row['asset']:row for row in manifest['resources']}
    producers=output/'Producers';producers.mkdir(exist_ok=True)
    inputs=[]
    for source in (Path(__file__),ROOT/'tools/blender-models/CharacterFaces/generate.py',ROOT/'tools/blender-models/CharacterFaces/production.py',Path(resources.__file__),Path(resources.__file__).with_name('mesh_recipe_codec.py')):
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
                             packet=packet,bones=resources.skin_bone_paths(cf,tree,resolve)))
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
        return skin_palette_frame(receiver['packet'],donor['packet'],receiver['bones'],donor['bones'])[0]
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
    write(catalog/RESOURCE_FILES['manifest'],manifest)
    assembly['catalogSha256']=sha(catalog/RESOURCE_FILES['manifest']);assembly['bytes']=sum(r['bytes']for r in assembly['proofs'])
    report=dict(status='PREPARED_SHARED_SKIN_CUTS_NOT_RENDERED',meshes=len(completed),bundles=len(edits),unsharedParts=audited,inputs=inputs)
    write(output/'Preparation.json',report)
    return report


def resource_has_face_guide(proof):
    operations=[row for row in proof.get('appearanceFinishing',[])if row['kind']=='source-owned-face']
    if not operations:return False
    operation=operations[-1]
    return bool(operation.get('authority',{}).get('sourceProfiles')or any(
        isinstance(row,dict)and row.get('sourceProfileId')for row in operation.get('facts',[])))


def face_resource_profile_authority(proof,documents=None):
    """Resolve the guide actually applied to this resource, including partial revisions."""
    operations=[row for row in proof.get('appearanceFinishing',[])if row['kind']=='source-owned-face']
    if not operations:raise ValueError('A face resource has no completed source guide.')
    operation=operations[-1];authority=operation.get('authority',{})
    documents={}if documents is None else documents
    def profiles_at(pin):
        key=(pin['path'],pin['sha256'])
        if key not in documents:
            payload=Path(pin['path']).read_bytes()
            if hashlib.sha256(payload).hexdigest()!=pin['sha256']:raise ValueError('A retained face guide changed.')
            document=json.loads(payload.decode('utf-8-sig'))
            documents[key]=document.get('profiles')if isinstance(document,dict)else None
        return documents[key]
    if 'sourceProfiles'in authority:
        pin=authority['sourceProfiles']
        if not isinstance(profiles_at(pin),list):raise ValueError('A resource source guide has no profiles.')
        return pin
    identifiers={row['sourceProfileId']for row in operation['facts']if row.get('sourceProfileId')}
    # Earlier receipts retain producer inputs in declaration order: next guide,
    # then previous guide. Resolve the first complete declaration for this resource.
    for pin in authority.get('inputs',[]):
        path=Path(pin['path'])
        if path.suffix!='.json':continue
        profiles=profiles_at(pin)
        if identifiers and isinstance(profiles,list)and identifiers<={p['id']for p in profiles}:
            return pin
    raise ValueError('A face resource has no retained guide declaration.')


def prepare_resource_faces(source,index_path,output,census_path,*,selected_assets=(),profiles_path=None,restore_native_only=False,previous_profiles_path=None):
    """Finish all native face resources and their shared Main fields in one catalog."""
    from functools import lru_cache
    import inspect
    import numpy as np
    import character_resources as resources
    owner=sys.modules[__name__];shape,_=source_owner();read=resources.read;sha=resources.digest
    census=read(census_path);native_index=read(index_path)
    parts={part['asset'].casefold():row['file']for row in native_index['bundles']for part in row.get('parts',[])}
    if (profiles_path is None)==(not restore_native_only):
        raise ValueError('Select source face profiles or explicit native anatomy recovery.')
    guide_assets={};guide_owners={};previous_assets={}
    same_profiles=profiles_path is not None and previous_profiles_path is not None and sha(profiles_path)==sha(previous_profiles_path)
    if profiles_path is not None:
        shape.art.SOURCE=Path(census_path).resolve().parent
        guided=shape.load_profiles(census,profiles_path,resource_owners=True)['assets']
        guided=[asset for asset in guided if (not selected_assets or asset['name']in selected_assets) and any(
            skin.get('_faceProfile',{}).get('operation')=='source-guide' for skin in asset['skins'])]
        if not guided:raise ValueError('No source surface is owned by the requested face guides.')
        shape.require_selected_guides(guided)
        guide_assets={asset['sourceContentKey']:shape.bind_source_lod_families(asset)for asset in guided}
        if previous_profiles_path is not None:
            if same_profiles:previous_assets=guide_assets
            else:
                previous=shape.load_profiles(census,previous_profiles_path,resource_owners=True)['assets']
                previous_assets={asset['sourceContentKey']:shape.bind_source_lod_families(asset) for asset in previous
                    if asset['sourceContentKey']in guide_assets}
            shape.require_selected_guides(list(previous_assets.values()))
        for key,asset in guide_assets.items():
            for skin in asset['skins']:
                owner_key=(parts.get(asset['internalId'].casefold()),skin['sourceFingerprint'])
                if owner_key in guide_owners and guide_owners[owner_key]!=key:
                    raise ValueError('One serialized face resource has conflicting per-source sculpt owners.')
                guide_owners[owner_key]=key
    @lru_cache(maxsize=2)
    def guide_targets(source_key):
        return {row['native']['sourceFingerprint']:row for row in shape.refine_source_asset(guide_assets[source_key])}
    @lru_cache(maxsize=2)
    def previous_targets(source_key):
        if same_profiles:return guide_targets(source_key)
        return {row['native']['sourceFingerprint']:row for row in shape.refine_source_asset(previous_assets[source_key])}
    coverage=[dict(asset=a['internalId'],name=a['name'],kind='editable'if '/EditableCharacters/'in a['internalId']else'npc'if '/Monsters/NPC/'in a['internalId']else'creature',
        file=parts.get(a['internalId'].casefold()))for a in census['assets']]
    missing=[row for row in coverage if not row['file']and not row['asset'].lower().endswith('.fbx')]
    if missing:raise ValueError('A runtime face has no independently compiled resource: '+str(missing))
    names=sorted({row['file']for row in coverage if row['file']})
    if selected_assets:
        selected=set(selected_assets)
        if not selected<={row['name']for row in coverage}:raise ValueError('Unknown native contour source selection.')
        names=sorted({row['file']for row in coverage if row['file']and row['name']in selected})
    styled={parts[asset['internalId'].casefold()]for asset in (guide_assets.values() if profiles_path is not None else humanoid_sources(census))}
    if previous_profiles_path is not None:names=sorted(styled)
    pins=[dict(path=str(Path(path).resolve()),sha256=sha(path))for path in (
        index_path,census_path,shape.__file__,shape.art.__file__,__file__,resources.__file__,
        Path(__file__).with_name('resource-package.json'+('.source'if Path(__file__).suffix=='.source'else'')))]
    producer_count=len(pins)
    if profiles_path is not None:pins.append(dict(path=str(Path(profiles_path).resolve()),sha256=sha(profiles_path)))
    if previous_profiles_path is not None:pins.append(dict(path=str(Path(previous_profiles_path).resolve()),sha256=sha(previous_profiles_path)))
    def strength_field(packet,skin,styled_face):
        guard=shape.humanoid_face_contact_guard(packet,skin)
        return np.minimum(shape.face_surface_strength(packet,skin),guard)if styled_face else np.ones(packet['vertexCount'])
    source=checked_catalog_path(source);output=checked_catalog_path(output)
    if not output.is_relative_to(ROOT/'tools')or output==source:raise ValueError('Appearance output must be an independent tools workspace.')
    output.mkdir(parents=True,exist_ok=True)
    catalog=source/'Catalog';manifest=read(catalog/RESOURCE_FILES['manifest']);assembly=read(source/'ResourceAssembly.json')
    assembly['artAccepted']=False
    assembly.pop('artAcceptance',None)
    register_groom_shading_proofs(assembly)
    for index in range(2,producer_count):
        original=pins[index];archive=output/'ProducerSources'/(Path(original['path']).name.removesuffix('.source')+'.source')
        archive.parent.mkdir(exist_ok=True)
        if archive.exists()and sha(archive)!=original['sha256']:raise ValueError('A retained contour producer changed.')
        if not archive.exists():archive.write_bytes(Path(original['path']).read_bytes())
        pins[index]=dict(path=str(archive.resolve()),sha256=sha(archive))
        assembly.setdefault('retainedSourceInputs',[]).append(dict(original=original,archive=pins[index]))
    if previous_profiles_path is not None:
        proofs={row['file']:row for row in assembly['proofs']}
        supplied=read(previous_profiles_path)['profiles'];retained={};documents={}
        for key,asset in guide_assets.items():
            authority=face_resource_profile_authority(proofs[parts[asset['internalId'].casefold()]],documents)
            if authority not in pins:pins.append(authority)
            if authority['sha256']not in retained:retained[authority['sha256']]=read(authority['path'])['profiles']
            def owned(rows):return {p['source']['rendererPath']:p for p in rows if p['source']['assetSourceContentKey']==key}
            if not owned(supplied)or owned(supplied)!=owned(retained[authority['sha256']]):
                raise ValueError('The previous guide differs from its completed resource authority: '+asset['name'])
    else:
        proofs={row['file']:row for row in assembly['proofs']}
        if any(resource_has_face_guide(proofs[name])for name in styled):
            raise ValueError('An already sculpted source requires its completed previous face profiles.')
    target=output/'Catalog';target.mkdir(exist_ok=True)
    checkpoint=output/'GeometryPreparation.json'
    # A guide revision publishes its geometry directly. The pigment workflow
    # owns a separate checkpoint because it replaces bundles after this stage.
    geometry_catalog=target if previous_profiles_path is not None else output/'GeometryCatalog'
    face_code=inspect.getsource(prepare_resource_faces)
    shape_code=face_code[:face_code.index('\n    source=checked_catalog_path(source)')]+face_code[face_code.index('\n    @lru_cache'):face_code.index("\n    for row in manifest['bundles']:row['sha256']=old_proofs")]
    functions=(export_current_mesh,source_owner,resources.write_mesh_frame)
    signature=hashlib.sha256('\n'.join([shape_code,*[inspect.getsource(function)for function in functions]]).encode('utf8')).hexdigest()
    geometry_owner: dict[str,Any]=dict(sourceCatalogSha256=sha(catalog/RESOURCE_FILES['manifest']),sourceAssemblySha256=sha(source/'ResourceAssembly.json'),inputs=pins,codeSignature=signature,nativeContours=True,selectedAssets=sorted(selected_assets),faceMode='native-recovery'if restore_native_only else'source-guide')
    if profiles_path is not None:geometry_owner['sourceProfiles']=dict(path=str(Path(profiles_path).resolve()),sha256=sha(profiles_path))
    if previous_profiles_path is not None:geometry_owner['faceMode']='source-guide-revision'
    if checkpoint.exists():
        saved=read(checkpoint)
        if saved['owner']!=geometry_owner:raise ValueError('The saved geometry belongs to different source inputs or shape code.')
        for row in saved['manifest']['bundles']:
            original=geometry_catalog/row['file']
            if sha(original)!=row['sha256']:raise ValueError('A saved geometry bundle changed.')
            if geometry_catalog==target:continue
            receipt=output/'ColorCorrections/Receipts'/(row['file']+'.json')
            if receipt.exists()and (target/row['file']).exists():
                color=read(receipt)
                if color.get('beforeSha256')==row['sha256']and color.get('sha256')==sha(target/row['file']):continue
            temporary=target/(row['file']+'.restoring');temporary.hardlink_to(original);temporary.replace(target/row['file'])
        print('Reused complete native face geometry:',saved['meshes'],flush=True)
        return saved
    if(output/'ResourceAssembly.json').exists():raise ValueError('A completed candidate already owns this directory.')
    marker=output/'WorkingDirectory.json'
    expected: dict[str, Any]=dict(kind='OwnedFacePreparation',source=str(source),catalogSha256=sha(catalog/RESOURCE_FILES['manifest']))
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
    def source_context(file_name,skin):
        candidates=[s for a in census['assets'] if parts.get(a['internalId'].casefold())==file_name
                    for s in a['skins'] if s['mesh']==skin['mesh']]
        if not candidates:raise ValueError('A native face has no renderer transform authority: '+file_name+'/'+skin['mesh'])
        matrices=[np.asarray(s['rendererToPrefab'])for s in candidates]
        if any(not np.allclose(matrices[0],m,rtol=0,atol=1e-6)for m in matrices[1:]):
            raise ValueError('Shared face renderers have different coordinate domains: '+file_name)
        return dict(skin,rendererToPrefab=candidates[0]['rendererToPrefab'])
    def save(file_name,objects,kind,facts,additions=None):
        env,file=resources.load_serialized_bundle(target/file_name,in_memory=True)
        additions=additions or {};original_ids=set(file.objects)
        if set(additions)&original_ids:raise ValueError('A private face mesh collides with an existing resource object.')
        before={i:hashlib.sha256(resources.serialized_object_bytes(o)).hexdigest()for i,o in file.objects.items()if i not in objects}
        expected={}
        for identifier,(source,tree)in additions.items():
            if source.type.name!='Mesh'or tree.get('m_StreamData',{}).get('size',0):raise ValueError('A private face mesh requires an inline source Mesh.')
            if source.assets_file.header.version!=file.header.version or source.assets_file.target_platform!=file.target_platform:
                raise ValueError('A private face mesh has another serialized source format.')
            types=[i for i,value in enumerate(file.types)if value.class_id==source.class_id]
            if len(types)>1:raise ValueError('A private face mesh has ambiguous serialized type ownership.')
            if types:type_index=types[0]
            else:type_index=len(file.types);file.types.append(copy.copy(source.serialized_type))
            cloned=copy.copy(source);cloned.assets_file=file;cloned.path_id=identifier;cloned.type_id=type_index;cloned.serialized_type=file.types[type_index]
            cloned.save_typetree(tree);file.objects[identifier]=cloned;expected[identifier]=cloned.data
        for identifier,tree in objects.items():file.objects[identifier].save_typetree(tree);expected[identifier]=file.objects[identifier].data
        temp=target/(file_name+'.writing');temp.write_bytes(env.file.save(packer='lz4'))
        _,check=resources.load_serialized_bundle(temp,in_memory=True)
        if set(check.objects)!=original_ids|set(additions):raise ValueError('A private face mesh changed unrelated object ownership.')
        for identifier,obj in check.objects.items():
            actual=resources.serialized_object_bytes(obj)
            if identifier in expected:
                if actual!=expected[identifier]:raise ValueError('A finished object failed its saved reload.')
            elif hashlib.sha256(actual).hexdigest()!=before[identifier]:raise ValueError('An unrelated object changed.')
        temp.replace(target/file_name)
        proof=old_proofs[file_name];before_sha=proof['sha256'];proof['sha256']=sha(target/file_name);proof['bytes']=(target/file_name).stat().st_size
        if additions:proof['objects']+=len(additions)
        proof['allRetainedObjectFieldsExactAfterReferenceRebasing']=False
        authority=dict(inputs=pins)
        if kind=='source-owned-face'and profiles_path is not None:authority['sourceProfiles']=geometry_owner['sourceProfiles']
        proof.setdefault('appearanceFinishing',[]).append(dict(kind=kind,objects=list(objects),facts=facts,beforeSha256=before_sha,sha256=proof['sha256'],authority=authority,
            unrelatedObjectBytesExact=True,savedObjectBytesExact=True,cabAndExternalReferencesUnchanged=True,
            objectsAdded=sorted(additions)))
    completed={};texture_jobs={};reports=[];failures=[]
    for file_name in names:
        row=by_file[file_name];nf=native(row['cab']);cf=current(next(cab for cab,name in current_cabs.items()if name==file_name))
        edits={};facts=[];donors={};private_meshes={};private_bindings=[]
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
            context=source_context(file_name,skin)if file_name in styled else skin
            donors[key]=(packet,strength_field(packet,skin,file_name in styled),context)
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
            npacket=owner.export_current_mesh(nobj);cpacket=owner.export_current_mesh(cobj)
            strength=np.zeros(npacket['vertexCount'])if companion else strength_field(npacket,skin,file_name in styled)
            if not companion and donor and not skin['mesh'].endswith('LOD0'):
                strength=np.minimum(strength_field(npacket,skin,file_name in styled),shape.lower_face_strength(npacket,*donor[:2]))
            proof: dict[str, Any]={}
            try:
                guided=None;previous_guided=None
                if file_name in styled and profiles_path is not None:
                    identity=npacket['sourceFingerprint']
                    guide_key=(file_name,identity)
                    if guide_key not in guide_owners:raise ValueError('The face guide does not match the actual native resource mesh: '+skin['mesh'])
                    guided=guide_targets(guide_owners[guide_key])[identity]
                    if previous_profiles_path is not None:
                        previous_guided=previous_targets(guide_owners[guide_key])[identity]
                explicit=guided is not None and guided['skin'].get('_faceProfile',{}).get('operation')=='source-guide'
                completed_key=(file_name,identifier)if explicit and companion else key
                if completed_key in completed:continue
                if file_name in styled and not companion:
                    context=source_context(file_name,skin)
                    if skin['mesh'].endswith('LOD0'):
                        marks=None
                    else:
                        if donor is None:raise ValueError('A lower native face has no LOD0 contour owner.')
                        transform=shape.art.CONVERT@shape.art.matrix(donor[2]['rendererToPrefab'])
                        _,_,marks=shape.art.landmarks(donor[0],donor[2],transform)
                        marks['eyeContactRegions']=shape.humanoid_face_contact_regions(donor[0],donor[2])
                    contacts={pair[0]for operation in old_proofs[key[0]].get('appearanceFinishing',[])
                              for fact in operation.get('facts',[]) if isinstance(fact,dict) and fact.get('object')==cobj.path_id
                              for pair in fact.get('vertexPairs',[])}
                    packet,proof=shape.refine_native_face_contours(npacket,cpacket,context,marks,contacts,
                        refined=guided['authored']if guided else None,guide_report=guided['details']if guided else None,
                        previous_refined=previous_guided['authored']if previous_guided else None)
                elif guided is not None:
                    if previous_guided is not None:
                        packet,proof=shape.refine_native_face_contours(npacket,cpacket,source_context(file_name,skin),
                            refined=guided['authored'],previous_refined=previous_guided['authored'],
                            surface_weight=np.ones(npacket['vertexCount']))
                    else:packet=guided['authored']
                    proof=dict(guided['details'],**proof,sourceGuideCompanion=True)
                elif file_name in styled:packet,proof=shape.retain_source_proportions(npacket,cpacket,skin,strength)
                else:packet,proof=cpacket,dict(creatureAnatomyChannelsRetained=True,nativeVertices=npacket['vertexCount'],preparedVertices=cpacket['vertexCount'])
            except ValueError as error:
                failures.append(dict(file=file_name,mesh=skin['mesh'],error=str(error)))
                continue
            stable=('uv','weights','boneIndices','bindPoses','subMeshes','vertexCount','indexFormat','colors')
            def frame_identity(value):return [(item['index'],item['name'],[frame['weight']for frame in item['frames']])for item in value['shapes']]
            exact=all(packet[field]==cpacket[field]for field in stable)and frame_identity(packet)==frame_identity(cpacket)
            if not exact:raise ValueError('An authored face changed retained UV, skin, topology or frame identities: '+file_name+'/'+skin['mesh'])
            proof['sourceSkinUvAndFrameIdentityExact']=True
            if not companion:
                bone_indices=np.asarray(npacket['boneIndices'],int).reshape(-1,4);bone_weights=np.asarray(npacket['weights']).reshape(-1,4)
                ocular_bones=np.asarray([name.rsplit('/',1)[-1].lower().startswith(('b_eye_','b_eyelid_'))for name in skin['bones']],dtype=bool)
                ocular=np.any(ocular_bones[bone_indices]&(bone_weights>0),axis=1)
                errors=[]
                for field,width in (('vertices',3),('normals',3),('tangents',4)):
                    before=np.asarray(npacket[field]).reshape(-1,width)[ocular]
                    after=np.asarray(packet[field][:npacket['vertexCount']*width]).reshape(-1,width)[ocular]
                    errors.append(float(np.max(np.abs(before-after)))if len(before)else 0.)
                if explicit:
                    owned=np.asarray(guided['skin']['_faceProfile']['mask'])>0;ocular&=~owned
                    errors=[]
                    for field,width in(('vertices',3),('normals',3),('tangents',4)):
                        before=np.asarray(npacket[field]).reshape(-1,width)[ocular]
                        after=np.asarray(packet[field][:npacket['vertexCount']*width]).reshape(-1,width)[ocular]
                        errors.append(float(np.max(np.abs(before-after)))if len(before)else 0.)
                    proof['explicitOcularGuideVertices']=int(np.count_nonzero(owned))
                if max(errors)>1e-6:raise ValueError('An authored face changed its protected ocular channels: '+file_name+'/'+skin['mesh'])
                proof['nativeOcularChannelsWithinTolerance']=True
                proof['guardedOcularVertices']=int(ocular.sum())
                proof['maximumOcularChannelError']=max(errors)
            completed[completed_key]=npacket['sourceFingerprint']
            mesh=None if packet['sourceFingerprint']==cpacket['sourceFingerprint']else resources.write_mesh_frame(cobj,packet)
            if mesh is not None:
                if explicit and companion:
                    owned=any(fact.get('ownedRendererMesh',{}).get('renderer')==identifier and
                        fact['ownedRendererMesh'].get('mesh')==cobj.path_id for operation in old_proofs[file_name].get('appearanceFinishing',[])
                        for fact in(operation.get('facts',[])if isinstance(operation.get('facts'),list)else[operation.get('facts',{})]))
                    references=[obj.path_id for obj in cf.objects.values()if obj.type.name=='SkinnedMeshRenderer'and
                        obj.read_typetree()['m_Mesh']==dict(m_FileID=0,m_PathID=cobj.path_id)]if key[0]==file_name else[]
                    if not owned or references!=[identifier]:
                        mesh_id=1
                        while mesh_id in cf.objects or mesh_id in private_meshes:mesh_id+=1
                        private_meshes[mesh_id]=(cobj,mesh);private_bindings.append((identifier,mesh_id))
                        renderer=copy.deepcopy(cr);renderer['m_Mesh']=dict(m_FileID=0,m_PathID=mesh_id)
                        bones=resources.skin_bone_paths(cf,cr,lambda file,pointer:required_resolve(file,pointer,False))
                        edits[identifier],bounds=resources.extend_skin_bone_palette(cf,renderer,bones,bones,packet=packet)
                        proof.update(ownedRendererMesh=dict(renderer=identifier,mesh=mesh_id,
                            source=dict(file=key[0],object=key[1]),onlyDeclaredRendererRebound=True),rendererBounds=bounds)
                        facts.append(dict(name=skin['mesh'],**proof));continue
                changes={key[1]:mesh}
                if not companion:
                    resolve_owned=lambda file,pointer:required_resolve(file,pointer,False)
                    bounds=[]
                    for renderer in cf.objects.values():
                        if renderer.type.name!='SkinnedMeshRenderer':continue
                        tree=renderer.read_typetree()
                        if tree['m_Mesh']!=dict(m_FileID=0,m_PathID=cobj.path_id):continue
                        bones=resources.skin_bone_paths(cf,tree,resolve_owned)
                        changes[renderer.path_id],bound=resources.extend_skin_bone_palette(cf,tree,bones,bones,packet=packet)
                        bounds.append(dict(renderer=renderer.path_id,**bound))
                    proof['rendererBounds']=bounds
                if key[0]==file_name:edits.update(changes);facts.append(dict(name=skin['mesh'],**proof))
                else:save(key[0],changes,'source-owned-face',dict(name=skin['mesh'],**proof))
            if not companion:
                for material_index,pointer in enumerate(cr['m_Materials']):
                    if not pointer['m_PathID']:continue
                    mf,mo=required_resolve(cf,pointer,False);mt=mo.read_typetree();textures=dict(mt['m_SavedProperties']['m_TexEnvs'])
                    if '_MainTex'not in textures or not textures['_MainTex']['m_Texture']['m_PathID']:continue
                    tf,to=required_resolve(mf,textures['_MainTex']['m_Texture'],False)
                    if not to.peek_name().endswith('_Main_FieldBC7'):continue
                    texture=texture_jobs.setdefault((current_cabs[tf.name],to.path_id),dict(name=to.peek_name(),styled=False,owners=[]))
                    texture['styled']|=file_name in styled
                    if file_name not in texture['owners']:texture['owners'].append(file_name)
                    if file_name in styled:
                        native_material_file,native_material=required_resolve(nf,nr['m_Materials'][material_index],True)
                        native_textures=dict(native_material.read_typetree()['m_SavedProperties']['m_TexEnvs'])
                        native_texture_file,native_texture=required_resolve(native_material_file,native_textures['_MainTex']['m_Texture'],True)
                        packet_source,skin_source=npacket,skin
                        ownership={key:packet_source[key]for key in ('vertexCount','weights','boneIndices','uv','subMeshes')}
                        native_reference=native_cabs[native_texture_file.name]
                        pigment=dict(nativeTexture=dict(path=native_reference['source'],sha256=native_reference['sha256'],identifier=native_texture.path_id),
                            packet=ownership,skin=dict(bones=skin_source['bones'],materials=skin_source['materials']))
                        sources=texture.setdefault('pigmentSources',[])
                        if pigment not in sources:sources.append(pigment)
        if private_bindings:
            metadata=[obj for obj in cf.objects.values()if obj.type.name=='AssetBundle']
            if len(metadata)!=1:raise ValueError('A private face mesh has no unique prefab preload owner.')
            edits[metadata[0].path_id]=resources.resource_asset_preloads(cf,metadata[0].read_typetree(),private_bindings)
        if edits:save(file_name,edits,'source-owned-face',facts,private_meshes)
        reports.append(dict(file=file_name,meshes=len(facts)))
        print('Finished face:',file_name,len(facts),flush=True)
    if failures:
        write(output/'GeometryFailures.json',dict(owner=geometry_owner,failures=failures))
        raise ValueError('Native contour validation failed for '+str(len(failures))+' meshes; see GeometryFailures.json.')
    for row in manifest['bundles']:row['sha256']=old_proofs[row['file']]['sha256']
    for pin in pins:
        if sha(pin['path'])!=pin['sha256']:raise ValueError('A geometry source changed during preparation.')
    if geometry_catalog!=target:
        geometry_catalog.mkdir()
        for row in manifest['bundles']:(geometry_catalog/row['file']).hardlink_to(target/row['file'])
    result=dict(status='PREPARED_GEOMETRY',owner=geometry_owner,manifest=manifest,assembly=assembly,coverage=coverage,rows=reports,humanStyleFiles=sorted(styled),
        meshes=len(completed),textures=[dict(file=file,identifier=identifier,**value)for(file,identifier),value in texture_jobs.items()])
    write(checkpoint,result)
    return result


def restore_resource_pigment_bundle(job):
    """Keep native head material data while retaining calibrated neck and chest."""
    import numpy as np
    import character_resources as resources
    import material_fields as fields
    source=Path(job['source']);target=Path(job['target']);receipt=Path(job['receipt'])
    identity=dict(sourceSha256=job['sha256'],inputs=job['inputs'],policy='native-head-material-v2',calibratedSource=job.get('calibratedSource'))
    if job.get('inputPlan'):identity['inputPlan']=job['inputPlan']
    if receipt.exists():
        prior=read(receipt)
        if prior['identity']!=identity:raise ValueError('Facial pigment output belongs to other inputs.')
        if target.exists()and sha(target)==prior['sha256']:
            resources.validate_owned_texture_mips(prior)
            return prior
    if sha(source)!=job['sha256']:raise ValueError('A facial pigment source changed.')
    _,file=resources.load_serialized_bundle(source,in_memory=True)
    calibrated_source=job.get('calibratedSource',dict(path=str(source.resolve()),sha256=job['sha256']))
    if sha(calibrated_source['path'])!=calibrated_source['sha256']:raise ValueError('The calibrated facial material source changed.')
    calibrated_file=file if Path(calibrated_source['path']).resolve()==source.resolve()else resources.load_serialized_bundle(calibrated_source['path'],in_memory=True)[1]
    reports=[];replacements=[];fields_root=receipt.parent.parent/'Fields'/target.stem;fields_root.mkdir(parents=True,exist_ok=True)
    for ordinal,item in enumerate(job['textures'],start=1):
        obj=file.objects[item['identifier']];tree=obj.read_typetree()
        if tree['m_TextureFormat']not in (4,12,25):raise ValueError('A calibrated Main texture has no supported material-data owner.')
        references=item['pigmentSources'];native_sources=[];masses=[]
        width,height=tree['m_Width'],tree['m_Height'];mips=tree['m_MipCount']
        for reference in references:
            pin=reference['nativeTexture']
            if sha(pin['path'])!=pin['sha256']:raise ValueError('A native pigment texture changed.')
            _,native_file=resources.load_serialized_bundle(pin['path'],in_memory=True)
            native_obj=native_file.objects[pin['identifier']];native_data=native_obj.read();native_images=resources.texture_mip_images(native_obj)
            starts=[i for i,image in enumerate(native_images)if image.shape[:2]==(height,width)]
            if native_data.m_TextureFormat not in(10,12)or len(starts)!=1 or len(native_images)-starts[0]!=mips:
                raise ValueError('The native material reference has another complete mip layout.')
            start=starts[0];block_size=8 if native_data.m_TextureFormat==10 else 16
            skip=sum(((max(1,native_data.m_Width>>i)+3)//4)*((max(1,native_data.m_Height>>i)+3)//4)*block_size for i in range(start))
            native_sources.append(dict(images=native_images[start:],payload=native_data.get_image_data()[skip:],format=native_data.m_TextureFormat,start=start))
            packet=reference['packet'];skin=reference['skin'];fraction=fields.regional_bone_fraction(packet,skin['bones']);slot_masks=[]
            for slot,material in enumerate(skin['materials']):
                if slot>=len(packet['subMeshes'])or not material or 'Skin'not in material['shader']:continue
                main=next((p for p in material.get('properties',[])if p['name']=='_MainTex'),None)
                scale=np.asarray(main['scale']if main else[1.,1.],float);offset=np.asarray(main['offset']if main else[0.,0.],float)
                if scale.shape!=(2,)or offset.shape!=(2,)or not np.isfinite(np.r_[scale,offset]).all():raise ValueError('The native pigment sampler transform is invalid.')
                coords=np.asarray(next(p['values']for p in packet['uv']if p['channel']==0),float).reshape(-1,4).copy();coords[:,:2]=coords[:,:2]*scale+offset
                uv_packet=dict(packet,subMeshes=[packet['subMeshes'][slot]],uv=[dict(channel=0,values=coords.ravel().tolist())])
                mass,_=fields.regional_fraction_uv(uv_packet,fraction,width,height);slot_masks.append(mass)
            if not slot_masks:raise ValueError('The native pigment source has no declared skin material slot.')
            masses.append(np.fmax.reduce(slot_masks))
        native=native_sources[0]
        if any(any(not np.array_equal(a,z)for a,z in zip(native['images'],other['images']))for other in native_sources[1:]):
            raise ValueError('A shared Main field has incompatible native pigment mip sources.')
        calibrated_obj=calibrated_file.objects[obj.path_id];calibrated_data=calibrated_obj.read()
        if(calibrated_data.m_Width,calibrated_data.m_Height,calibrated_data.m_MipCount)!=(width,height,mips):raise ValueError('The calibrated Main source has another mip layout.')
        authored=resources.texture_mip_images(calibrated_obj);calibrated_bytes=calibrated_data.get_image_data();calibrated_offset=0
        mass=np.fmax.reduce(masses);payloads=[];mask_payload=[];calibrated_masks=[];levels=[];offset=0;mask_offset=0
        for level,(native_image,current)in enumerate(zip(native['images'],authored)):
            h,w=current.shape[:2];size=((w+3)//4)*((h+3)//4)*(8 if native['format']==10 else 16)
            restored,semantic=fields.preserve_head_pigment_mip(native_image,current,mass,native_alpha=True)
            offset+=size;block_bytes=((w+3)//4)*((h+3)//4)*16
            preserved=calibrated_bytes[calibrated_offset:calibrated_offset+block_bytes]if calibrated_data.m_TextureFormat==25 else None
            encoded,face,calibrated,codec=fields.native_face_bc7_mip(preserved,restored,mass);calibrated_offset+=block_bytes
            packed=np.packbits(face.ravel()).tobytes();mask_payload.append(packed)
            calibrated_masks.append(np.packbits(calibrated.ravel()).tobytes())
            levels.append(dict(level=level,width=w,height=h,maskOffset=mask_offset,maskBytes=len(packed),maskBits=face.size,semantic=semantic,codec=codec));mask_offset+=len(packed)
            payloads.append(encoded)
            if level<mips-1:mass=fields.regional_fraction_mip(mass)
        if offset!=len(native['payload'])or len(levels)!=mips:raise ValueError('Native pigment finishing omitted a mip.')
        raw=fields_root/('Main'+str(ordinal)+'.bc7');mask=fields_root/('Main'+str(ordinal)+'-Face.bits');calibrated_mask=fields_root/('Main'+str(ordinal)+'-Calibrated.bits')
        raw.write_bytes(b''.join(payloads));mask.write_bytes(b''.join(mask_payload));calibrated_mask.write_bytes(b''.join(calibrated_masks))
        raw_pin=dict(path=str(raw.resolve()),sha256=sha(raw));mask_pin=dict(path=str(mask.resolve()),sha256=sha(mask))
        replacements.append(dict(cab=file.name,object=obj.path_id,name=tree['m_Name'],dimensions=[width,height],mipCount=mips,raw=raw_pin,storageFormat=25))
        reports.append(dict(name=tree['m_Name'],identifier=obj.path_id,nativePigmentSource=references[0]['nativeTexture'],nativeFormat=native['format'],nativeMipStart=native['start'],faceMask=mask_pin,mips=levels,
            calibratedMask=dict(path=str(calibrated_mask.resolve()),sha256=sha(calibrated_mask)),calibratedFormat=calibrated_data.m_TextureFormat))
    if sha(source)!=job['sha256']:raise ValueError('A pigment source changed during finishing.')
    result=resources.replace_owned_texture_mips(source,job['sha256'],target,replacements)
    result.update(file=target.name,identity=identity,bytes=target.stat().st_size,nativePigment=reports,nativeReference='complete-original-native-mips',policy='native-head-material-v2',calibratedSource=calibrated_source)
    write(receipt,result);return result


def validate_native_pigment_resource(operation):
    """Check container ownership and every retained native face block."""
    import numpy as np
    from PIL import Image
    import character_resources as resources
    resources.validate_owned_texture_mips(operation)
    if operation.get('policy')not in('native-head-material-v1','native-head-material-v2')or operation.get('nativeReference')!='complete-original-native-mips':
        raise ValueError('The native facial material has another source policy.')
    payloads={row['object']:row for row in operation['textures']}
    calibrated_file=None
    if operation['policy']=='native-head-material-v2':
        reference=operation['calibratedSource']
        if sha(reference['path'])!=reference['sha256']:raise ValueError('A calibrated skin baseline changed.')
        calibrated_file=resources.load_serialized_bundle(reference['path'],in_memory=True)[1]
    if {row['identifier']for row in operation['nativePigment']}!=set(payloads):raise ValueError('The native face proof omits an owned texture.')
    for row in operation['nativePigment']:
        reference=row['nativePigmentSource'];mask=row['faceMask']
        if sha(reference['path'])!=reference['sha256']or sha(mask['path'])!=mask['sha256']:raise ValueError('A native face source or anatomical mask changed.')
        _,file=resources.load_serialized_bundle(reference['path'],in_memory=True);native=file.objects[reference['identifier']].read()
        if native.m_TextureFormat!=row['nativeFormat']or native.m_TextureFormat not in(10,12):raise ValueError('A native face source codec changed.')
        source=native.get_image_data();target=Path(payloads[row['identifier']]['raw']['path']).read_bytes();bits=Path(mask['path']).read_bytes()
        offset=sum(((max(1,native.m_Width>>i)+3)//4)*((max(1,native.m_Height>>i)+3)//4)*(8 if native.m_TextureFormat==10 else 16)for i in range(row['nativeMipStart']))
        target_offset=0;mask_offset=0;covered=0;calibrated_bits=None;calibrated_bytes=None
        if calibrated_file is not None:
            mask_ref=row['calibratedMask']
            if sha(mask_ref['path'])!=mask_ref['sha256']:raise ValueError('A calibrated skin mask changed.')
            calibrated_bits=Path(mask_ref['path']).read_bytes();calibrated=calibrated_file.objects[row['identifier']].read()
            if calibrated.m_TextureFormat!=row['calibratedFormat']:raise ValueError('The calibrated skin codec changed.')
            if calibrated.m_TextureFormat==25:calibrated_bytes=calibrated.get_image_data()
        for mip in row['mips']:
            w,h=mip['width'],mip['height'];count=((w+3)//4)*((h+3)//4);length=count*(8 if native.m_TextureFormat==10 else 16)
            if mip['maskOffset']!=mask_offset or mip['maskBits']!=count or mip['maskBytes']!=(count+7)//8:raise ValueError('An anatomical face mask has another block layout.')
            face=np.unpackbits(np.frombuffer(bits[mask_offset:mask_offset+mip['maskBytes']],np.uint8))[:count].astype(bool);mask_offset+=mip['maskBytes'];covered+=int(face.sum())
            native_mip=source[offset:offset+length];offset+=length;target_mip=target[target_offset:target_offset+count*16];target_offset+=count*16
            if len(native_mip)!=length or len(target_mip)!=count*16:raise ValueError('A native face proof has truncated encoded mips.')
            if operation['policy']=='native-head-material-v2':
                if calibrated_bits is None:raise ValueError('A native BC7 face has no calibration ownership.')
                cm=np.unpackbits(np.frombuffer(calibrated_bits[mip['maskOffset']:mask_offset],np.uint8))[:count].astype(bool)
                if len(cm)!=count or np.any(cm&face)or mip['codec']['calibratedBlocksExact']!=int(cm.sum()):raise ValueError('The native face and calibrated collar masks disagree.')
                if cm.any()and(calibrated_bytes is None or not np.array_equal(np.frombuffer(calibrated_bytes[target_offset-count*16:target_offset],np.uint8).reshape(-1,16)[cm],np.frombuffer(target_mip,np.uint8).reshape(-1,16)[cm])):
                    raise ValueError('The saved native face changed a calibrated collar block.')
                original=np.asarray(Image.frombytes('RGBA',(w,h),native_mip,'bcn',3 if native.m_TextureFormat==12 else 1));saved=np.asarray(Image.frombytes('RGBA',(w,h),target_mip,'bcn',7))
                selected=np.repeat(np.repeat(face.reshape((h+3)//4,(w+3)//4),4,axis=0),4,axis=1)[:h,:w]
                for channels in(slice(0,3),slice(3,4)):
                    difference=(saved.astype(float)-original)[:,:,channels][selected]
                    if np.abs(difference).max(initial=0)>16 or(difference.size and np.sqrt(np.mean(difference**2))>2):raise ValueError('The saved native face pigment exceeds its RGB/alpha budget.')
            elif native.m_TextureFormat==12:
                if not np.array_equal(np.frombuffer(native_mip,np.uint8).reshape(-1,16)[face],np.frombuffer(target_mip,np.uint8).reshape(-1,16)[face])or mip['codec']['nativeFaceBlocksExact']!=int(face.sum()):
                    raise ValueError('A saved native face block differs from its original source.')
            elif face.any():
                first=np.asarray(Image.frombytes('RGBA',(w,h),native_mip,'bcn',1));last=np.asarray(Image.frombytes('RGBA',(w,h),target_mip,'bcn',3))
                pixels=np.repeat(np.repeat(face.reshape((h+3)//4,(w+3)//4),4,axis=0),4,axis=1)[:h,:w]
                if np.abs(first[:,:,:3].astype(int)-last[:,:,:3].astype(int))[pixels].max()>16:raise ValueError('A native DXT1 face exceeds its preserved RGB budget.')
        if not covered or offset!=len(source)or target_offset!=len(target)or mask_offset!=len(bits)or(calibrated_bits is not None and len(calibrated_bits)!=mask_offset):raise ValueError('The native face material proof has no complete anatomical coverage.')


def head_material_fraction(item,width,height):
    """Raster the actual skin-slot Head weights, including sampler transforms."""
    import numpy as np
    import material_fields as fields
    masks=[]
    for reference in item['pigmentSources']:
        packet,skin=reference['packet'],reference['skin']
        fraction=fields.regional_bone_fraction(packet,skin['bones'])
        for slot,material in enumerate(skin['materials']):
            if not material or 'Skin'not in material['shader']:continue
            if slot>=len(packet['subMeshes']):raise ValueError('A head skin slot has no source triangles.')
            main=next((p for p in material.get('properties',[])if p['name']=='_MainTex'),dict(scale=[1.,1.],offset=[0.,0.]))
            scale=np.asarray(main['scale'],float);offset=np.asarray(main['offset'],float)
            if scale.shape!=(2,)or offset.shape!=(2,)or not np.isfinite(np.r_[scale,offset]).all():
                raise ValueError('The head material sampler transform is invalid.')
            coords=np.asarray(next(p['values']for p in packet['uv']if p['channel']==0),float).reshape(-1,4).copy()
            coords[:,:2]=coords[:,:2]*scale+offset
            uv_packet=dict(packet,subMeshes=[packet['subMeshes'][slot]],uv=[dict(channel=0,values=coords.ravel().tolist())])
            masks.append(fields.regional_fraction_uv(uv_packet,fraction,width,height,
                overlap='protect-complement'if reference.get('currentSource')else'reject')[0])
    if not masks:raise ValueError('The head material has no declared skin surface.')
    return np.fmax.reduce(masks)


def current_head_pigment_geometry(catalog,assembly,templates,baseline=None) -> dict[str,Any]:
    """Bind each Main texture to its saved LOD mesh, skin palette and UVs."""
    import copy
    import character_resources as resources
    by_cab={row['newCab']:row for row in assembly['proofs']}
    by_name={row['file']:row for row in assembly['proofs']}
    textures={(row['file'],row['identifier']):copy.deepcopy(row)for row in templates if row.get('pigmentSources')}
    for row in textures.values():
        originals=[]
        for reference in row['pigmentSources']:
            if reference['nativeTexture']not in originals:originals.append(reference['nativeTexture'])
        row['nativePigmentSources']=originals;row['pigmentSources']=[]
    pins={}
    @lru_cache(maxsize=12)
    def load(name):
        path=baseline/name if baseline is not None and(baseline/name).exists()else catalog/name;proof=by_name[name]
        if sha(path)!=proof['sha256']:raise ValueError('A current pigment geometry resource changed: '+name)
        pins[name]=dict(path=str(path.resolve()),sha256=proof['sha256'])
        return resources.load_serialized_bundle(path,in_memory=True)[1]
    def resolve(file,pointer):
        cab=file.name if not pointer['m_FileID']else file.externals[pointer['m_FileID']-1].name
        owner=load(by_cab[cab]['file']);return owner,owner.objects[pointer['m_PathID']]
    for name in sorted({name for row in textures.values()for name in row['owners']}):
        file=load(name)
        for renderer in file.objects.values():
            if renderer.type.name!='SkinnedMeshRenderer':continue
            tree=renderer.read_typetree()
            if not tree['m_Mesh']['m_PathID']:continue
            mesh_file,mesh=resolve(file,tree['m_Mesh'])
            if not mesh.peek_name().startswith('FaceShape'):continue
            packet=export_current_mesh(mesh);bones=resources.skin_bone_paths(file,tree,resolve)
            for slot,pointer in enumerate(tree['m_Materials']):
                if not pointer['m_PathID']:continue
                material_file,material=resolve(file,pointer)
                main=dict(material.read_typetree()['m_SavedProperties']['m_TexEnvs']).get('_MainTex')
                if not main or not main['m_Texture']['m_PathID']:continue
                texture_file,texture=resolve(material_file,main['m_Texture'])
                key=(by_cab[texture_file.name]['file'],texture.path_id)
                if key not in textures:continue
                if slot>=len(packet['subMeshes']):raise ValueError('A current head material has no saved LOD triangles.')
                ownership={key:packet[key]for key in('vertexCount','weights','boneIndices','uv')}
                ownership['subMeshes']=[packet['subMeshes'][slot]]
                source=dict(resource=pins[name],renderer=renderer.path_id,meshResource=pins[by_cab[mesh_file.name]['file']],mesh=mesh.path_id,
                            materialResource=pins[by_cab[material_file.name]['file']],material=material.path_id,materialSlot=slot)
                sampler=dict(name='_MainTex',scale=[main['m_Scale'][c]for c in'xy'],offset=[main['m_Offset'][c]for c in'xy'])
                textures[key]['pigmentSources'].append(dict(packet=ownership,skin=dict(bones=bones,
                    materials=[dict(shader='Skin',properties=[sampler])]),currentSource=source))
    if any(not row['pigmentSources']for row in textures.values()):raise ValueError('A head Main texture has no current saved LOD owner.')
    return dict(policy='saved-head-lod-material-v1',textures=list(textures.values()),inputs=list(pins.values()))


def native_head_pigment_mips(item,width,height,count):
    """Read the actual source material mips for this LOD, without resampling."""
    import numpy as np
    import character_resources as resources
    sources=[]
    for pin in item['nativePigmentSources']:
        if sha(pin['path'])!=pin['sha256']:raise ValueError('An original face pigment resource changed.')
        file=resources.load_serialized_bundle(pin['path'],in_memory=True)[1]
        images=resources.texture_mip_images(file.objects[pin['identifier']])
        starts=[i for i,image in enumerate(images)if image.shape[:2]==(height,width)]
        if len(starts)!=1 or len(images)-starts[0]!=count:raise ValueError('The original face pigment has another LOD mip layout.')
        sources.append(images[starts[0]:])
    if not sources or any(any(not np.array_equal(a,z)for a,z in zip(sources[0],other))for other in sources[1:]):
        raise ValueError('A current head Main has incompatible original LOD pigments.')
    return sources[0]


@lru_cache(maxsize=2)
def face_pigment_geometry(path,digest):
    if sha(path)!=digest:raise ValueError('The face pigment geometry authority changed.')
    return read(path)


def face_pigment_reference(current,aligned,item):
    import material_fields as fields
    import character_resources as resources
    identifier=item['identifier'];source=resources.texture_mip_images(current.objects[identifier]);target=resources.texture_mip_images(aligned.objects[identifier])
    level=next(i for i,pixels in enumerate(source)if max(pixels.shape[:2])<=256)
    height,width=source[level].shape[:2]
    if source[level].shape!=target[level].shape:raise ValueError('The paired face pigment references have different mip layouts.')
    mass=head_material_fraction(item,width,height)
    if item.get('nativePigmentSources'):
        data=current.objects[identifier].read()
        source=native_head_pigment_mips(item,data.m_Width,data.m_Height,len(source))
    return dict(level=level,width=width,height=height,
        coefficient=fields.head_pigment_coefficient(source[level],target[level],mass))


def prepare_face_pigment_coefficients(jobs,output,authority):
    import character_resources as resources
    if sha(authority['path'])!=authority['sha256']:raise ValueError('The face pigment matrix changed.')
    root=output/'PigmentCorrections/Coefficients';root.mkdir(parents=True,exist_ok=True);coefficients={}
    for job in jobs:
        selected=[row for row in job['textures']if '_L0_Main_'in row['name']]
        if not selected:continue
        source=dict(path=str(Path(job['source']).resolve()),sha256=job['sha256']);aligned=job['calibratedSource']
        if sha(source['path'])!=source['sha256']or sha(aligned['path'])!=aligned['sha256']:raise ValueError('A face reference source changed.')
        current_file=resources.load_serialized_bundle(source['path'],in_memory=True)[1]
        aligned_file=resources.load_serialized_bundle(aligned['path'],in_memory=True)[1]
        for item in selected:
            match=re.fullmatch(r'(.+)_L0_Main_FieldBC7',item['name'])
            if match is None:raise ValueError('The face pigment has no declared source/LOD identity.')
            asset=match[1];result=face_pigment_reference(current_file,aligned_file,item)
            record=dict(asset=asset,sourceResource=source,calibratedResource=aligned,geometry=authority,
                texture=dict(file=item['file'],identifier=item['identifier'],name=item['name']),**result)
            path=root/(asset+'.json');write(path,record)
            coefficients[asset]=dict(path=str(path.resolve()),sha256=sha(path))
    for job in jobs:
        for item in job['textures']:
            match=re.fullmatch(r'(.+)_L[012]_Main_FieldBC7',item['name'])
            if match is None or match[1]not in coefficients:raise ValueError('A face LOD has no shared LOD0 pigment reference.')
            item['pigmentCoefficient']=coefficients[match[1]]
    index=root/'Preparation.json';write(index,dict(policy='source-face-pigment-v3',geometry=authority,coefficients=coefficients))
    for job in jobs:job['inputs']=[*job['inputs'],dict(path=str(index.resolve()),sha256=sha(index))]


@lru_cache(maxsize=192)
def validated_face_pigment_coefficient(path,digest):
    import character_resources as resources
    if sha(path)!=digest:raise ValueError('A face pigment coefficient changed.')
    row=read(path);geometry=face_pigment_geometry(row['geometry']['path'],row['geometry']['sha256'])
    item=next(v for v in geometry['textures']if(v['file'],v['identifier'])==(row['texture']['file'],row['texture']['identifier']))
    for reference in(row['sourceResource'],row['calibratedResource']):
        if sha(reference['path'])!=reference['sha256']:raise ValueError('A face pigment reference input changed.')
    current=resources.load_serialized_bundle(row['sourceResource']['path'],in_memory=True)[1]
    aligned=resources.load_serialized_bundle(row['calibratedResource']['path'],in_memory=True)[1]
    expected=face_pigment_reference(current,aligned,item)
    if any(row[key]!=expected[key]for key in expected):raise ValueError('The face pigment differs from its paired source reference.')
    return row['coefficient']


def calibrated_pigment_payloads(job):
    """Keep body-calibrated face RGB, current alpha and the repaired collar."""
    import material_fields as fields
    import character_resources as resources
    source=job['source'];calibrated=job['calibratedSource']
    if sha(source)!=job['sha256']or sha(calibrated['path'])!=calibrated['sha256']:
        raise ValueError('A calibrated face material input changed.')
    current_file=resources.load_serialized_bundle(source,in_memory=True)[1]
    calibrated_file=resources.load_serialized_bundle(calibrated['path'],in_memory=True)[1]
    results=[]
    for item in job['textures']:
        identifier=item['identifier'];obj=current_file.objects[identifier];data=obj.read()
        reference=calibrated_file.objects[identifier];reference_data=reference.read()
        layout=(data.m_Width,data.m_Height,data.m_MipCount,data.m_ColorSpace)
        if data.m_TextureFormat!=25 or layout!=(reference_data.m_Width,reference_data.m_Height,reference_data.m_MipCount,reference_data.m_ColorSpace)or data.m_ColorSpace!=1:
            raise ValueError('A calibrated face material has another codec, layout or color space.')
        mass=head_material_fraction(item,data.m_Width,data.m_Height)
        current=resources.texture_mip_images(obj)
        corrected_ownership=bool(item.get('nativePigmentSources'))
        authored=resources.texture_mip_images(reference)if corrected_ownership else current
        originals=native_head_pigment_mips(item,data.m_Width,data.m_Height,len(current))if corrected_ownership else current
        coefficient=item.get('pigmentCoefficient')
        if coefficient is None:raise ValueError('A calibrated face requires its shared LOD0 pigment coefficient.')
        if sha(coefficient['path'])!=coefficient['sha256']:raise ValueError('The shared face pigment coefficient changed.')
        tone=validated_face_pigment_coefficient(coefficient['path'],coefficient['sha256'])
        payload=reference_data.get_image_data()if corrected_ownership else data.get_image_data();offset=0;encoded=[];levels=[]
        for level,(before,native)in enumerate(zip(authored,originals)):
            import numpy as np
            rgb=fields.v3_tone_rgb(fields.lookup[native[:,:,:3]],tone)
            srgb=np.where(rgb<=.0031308,rgb*12.92,1.055*np.maximum(rgb,0)**(1/2.4)-.055)
            face=before.copy();face[:,:,:3]=np.rint(np.clip(srgb,0,1)*255).astype(np.uint8)
            pixels,semantic=fields.compose_head_material_mip(face,before,mass)
            h,w=before.shape[:2];length=((w+3)//4)*((h+3)//4)*16
            try:value,_,_,codec=fields.native_face_bc7_mip(payload[offset:offset+length],pixels,mass,pigment_policy='calibrated')
            except ValueError as error:raise ValueError(data.m_Name+'/mip'+str(level)+': '+str(error))from error
            encoded.append(value);offset+=length
            levels.append(dict(level=level,width=w,height=h,semantic=semantic,
                targetRgb=codec['nativeFaceRgb'],currentAlpha=codec['nativeFaceAlpha'],
                currentCollarBlocksExact=codec['calibratedBlocksExact'],encodedSha256=hashlib.sha256(value).hexdigest()))
            if level<len(current)-1:mass=fields.regional_fraction_mip(mass)
        if len(levels)!=data.m_MipCount or offset!=len(payload):raise ValueError('Face pigment composition omitted a mip.')
        results.append(dict(cab=current_file.name,object=identifier,name=data.m_Name,dimensions=[data.m_Width,data.m_Height],
            mipCount=data.m_MipCount,storageFormat=25,payload=b''.join(encoded),mips=levels))
    return results


def calibrate_resource_pigment_bundle(job):
    import character_resources as resources
    source,target,receipt=Path(job['source']),Path(job['target']),Path(job['receipt'])
    identity=dict(sourceSha256=job['sha256'],inputs=job['inputs'],policy=job.get('policy','calibrated-head-material-v2'),
                  calibratedSource=job['calibratedSource'],inputPlan=job['inputPlan'])
    if receipt.exists():
        prior=read(receipt)
        if prior['identity']!=identity:raise ValueError('The calibrated pigment checkpoint has other inputs.')
        if target.exists()and sha(target)==prior['sha256']:
            resources.validate_owned_texture_mips(prior);return prior
    root=receipt.parent.parent/'Fields'/target.stem;root.mkdir(parents=True,exist_ok=True)
    replacements=[];reports=[]
    for ordinal,row in enumerate(calibrated_pigment_payloads(job),start=1):
        payload=row.pop('payload');levels=row.pop('mips');raw=root/('Main'+str(ordinal)+'.bc7');raw.write_bytes(payload)
        row['raw']=dict(path=str(raw.resolve()),sha256=sha(raw));replacements.append(row)
        reports.append(dict(identifier=row['object'],name=row['name'],mips=levels))
    result=resources.replace_owned_texture_mips(source,job['sha256'],target,replacements)
    result.update(file=target.name,identity=identity,policy=identity['policy'],calibratedSource=job['calibratedSource'],
                  calibratedPigment=reports)
    write(receipt,result);return result


def validate_calibrated_pigment_resource(operation):
    """Replay the source-owned face composition and every encoded mip."""
    import character_resources as resources
    resources.validate_owned_texture_mips(operation)
    if operation.get('policy')not in('calibrated-head-material-v2','calibrated-head-material-v3'):raise ValueError('Unknown calibrated face material policy.')
    reference=operation['identity']['inputPlan']
    if sha(reference['path'])!=reference['sha256']:raise ValueError('The calibrated face input plan changed.')
    job=read(reference['path'])
    if job['sha256']!=operation['beforeSha256']or job['calibratedSource']!=operation['calibratedSource']:
        raise ValueError('The calibrated face input plan has another resource owner.')
    expected={row['object']:row for row in calibrated_pigment_payloads(job)}
    if set(expected)!={row['object']for row in operation['textures']}:raise ValueError('A calibrated face proof omits an owned texture.')
    for row in operation['textures']:
        if Path(row['raw']['path']).read_bytes()!=expected[row['object']]['payload']:
            raise ValueError('The saved face pigment differs from the source-owned composition.')


def finish_resource_pigments(geometry,output,*,rgb_policy='calibrated',workers=3):
    from concurrent.futures import ProcessPoolExecutor,as_completed
    import character_resources as resources
    if rgb_policy not in ('native','calibrated'):raise ValueError('Choose native reference or calibrated face pigment.')
    if type(workers)is not int or not 1<=workers<=8:raise ValueError('Use one to eight independent material workers.')
    catalog=output/'Catalog';assembly=geometry['assembly'];manifest=geometry['manifest']
    source=output/'PigmentCorrections/Sources';source.mkdir(parents=True,exist_ok=True)
    live=[Path(__file__),Path(resources.__file__),Path(resources.__file__).with_name('mesh_recipe_codec.py'),ROOT/'tools/blender-models/CharacterFaces/material_fields.py',ROOT/'tools/asset-authoring/requirements.txt'];pins=[];live_pins=[]
    code_root=output/'PigmentCorrections/ProducerSources';code_root.mkdir(parents=True,exist_ok=True)
    for path in live:
        archived=code_root/path.name
        if archived.exists()and sha(archived)!=sha(path):raise ValueError('A retained pigment producer belongs to another epoch.')
        if not archived.exists():archived.write_bytes(path.read_bytes())
        pins.append(dict(path=str(archived.resolve()),sha256=sha(archived)));live_pins.append(dict(path=str(path.resolve()),sha256=sha(path)))
    pins.extend(geometry.get('pigmentInputs',[]))
    groups=collections.defaultdict(list)
    for texture in geometry['textures']:
        if texture.get('pigmentSources'):groups[texture['file']].append(texture)
    proofs={row['file']:row for row in assembly['proofs']}
    for name in groups:
        expected=proofs[name]['sha256']
        if (source/name).exists():
            if sha(source/name)!=expected:raise ValueError('A saved pigment baseline belongs to another preceding stage.')
        else:
            if sha(catalog/name)!=expected:raise ValueError('The preceding pigment stage changed before capture.')
            (source/name).hardlink_to(catalog/name)
    jobs: 'list[dict[str, Any]]'=[dict(source=str(source/name),target=str(catalog/name),sha256=sha(source/name),inputs=pins,textures=textures,
               receipt=str(output/'PigmentCorrections/Receipts'/(name+'.json')))for name,textures in sorted(groups.items())]
    for job in jobs:
        preceding=[row for row in proofs[Path(job['target']).name].get('appearanceFinishing',[])if row['kind']=='native-facial-pigment']
        if preceding:job['calibratedSource']=preceding[-1].get('calibratedSource',preceding[-1]['source'])
        if rgb_policy=='calibrated'and not preceding:raise ValueError('Body-aligned face pigment requires its retained calibrated source.')
    if rgb_policy=='calibrated':
        current=current_head_pigment_geometry(catalog,assembly,geometry['textures'],source)
        authority=output/'PigmentCorrections/SavedLodGeometry.json';write(authority,current)
        pins.extend([dict(path=str(authority.resolve()),sha256=sha(authority)),*current['inputs']])
        current_textures={(row['file'],row['identifier']):row for row in current['textures']}
        for job in jobs:
            job['textures']=[current_textures[(row['file'],row['identifier'])]for row in job['textures']]
            job['policy']='calibrated-head-material-v3'
        prepare_face_pigment_coefficients(jobs,output,dict(path=str(authority.resolve()),sha256=sha(authority)))
    plans=output/'PigmentCorrections/Inputs';plans.mkdir(parents=True,exist_ok=True)
    for job in jobs:
        path=plans/(Path(job['target']).name+'.json')
        if path.exists()and read(path)!=job:raise ValueError('A retained pigment input plan belongs to another source.')
        if not path.exists():write(path,job)
        job['inputPlan']=dict(path=str(path.resolve()),sha256=sha(path))
    reports=[]
    producer=calibrate_resource_pigment_bundle if rgb_policy=='calibrated'else restore_resource_pigment_bundle
    kind='calibrated-facial-pigment'if rgb_policy=='calibrated'else'native-facial-pigment'
    with ProcessPoolExecutor(max_workers=workers)as pool:
        for future in as_completed([pool.submit(producer,job)for job in jobs]):
            result=future.result();proof=proofs[result['file']];proof['sha256']=result['sha256'];proof['bytes']=result['bytes']
            proof['allRetainedObjectFieldsExactAfterReferenceRebasing']=False
            proof.setdefault('appearanceFinishing',[]).append(dict(kind=kind,**result));reports.append(result)
            print('Facial pigment:',rgb_policy,len(reports),'/',len(jobs),result['file'],flush=True)
    for row in manifest['bundles']:row['sha256']=proofs[row['file']]['sha256']
    if any(sha(pin['path'])!=pin['sha256']for pin in live_pins+pins):raise ValueError('A pigment finishing implementation changed during processing.')
    assembly['facialPigment']=dict(policy='calibrated-head-material-v3'if rgb_policy=='calibrated'else'native-head-material-v2',inputs=pins,files=len(reports),textures=sum(len(row['textures'])for row in reports),review='pending')



@lru_cache(maxsize=8)
def saved_skin_surface(reference_json):
    """Read the exact saved renderer, mesh and sampler used by an owned skin field."""
    import numpy as np
    import material_fields as fields
    import character_resources as resources
    reference=json.loads(reference_json)
    for pin in(reference['resource'],reference['meshResource'],reference['materialResource']):
        if sha(pin['path'])!=pin['sha256']:raise ValueError('A skin surface source changed.')
    file=resources.load_serialized_bundle(reference['resource']['path'],in_memory=True)[1]
    renderer=file.objects[reference['renderer']].read_typetree()
    def local(owner,pointer):
        if pointer['m_FileID']:raise ValueError('The saved skin rig has an external bone owner.')
        return owner,owner.objects[pointer['m_PathID']]
    bones=resources.skin_bone_paths(file,renderer,local)
    if bones!=reference['bones']:raise ValueError('A skin bake changed its actual ordered bone paths.')
    mesh_file=resources.load_serialized_bundle(reference['meshResource']['path'],in_memory=True)[1]
    packet=export_current_mesh(mesh_file.objects[reference['mesh']])
    material_file=resources.load_serialized_bundle(reference['materialResource']['path'],in_memory=True)[1]
    main=dict(material_file.objects[reference['material']].read_typetree()['m_SavedProperties']['m_TexEnvs'])[reference.get('property','_MainTex')]
    coords=np.asarray(next(v['values']for v in packet['uv']if v['channel']==0),float).reshape(-1,4)
    coords[:,:2]=coords[:,:2]*[main['m_Scale'][c]for c in'xy']+[main['m_Offset'][c]for c in'xy']
    packet['uv']=[dict(v,values=coords.ravel().tolist())if v['channel']==0 else v for v in packet['uv']]
    if not reference.get('completeMesh',False):packet['subMeshes']=[packet['subMeshes'][reference['materialSlot']]]
    return packet,bones


def skin_lod_pigment_payloads(job):
    """Replay lower-LOD colour from the same saved LOD0 physical skin surface."""
    import numpy as np
    import material_fields as fields
    import character_resources as resources
    if sha(job['source'])!=job['sha256']:raise ValueError('A skin LOD texture source changed.')
    current=resources.load_serialized_bundle(job['source'],in_memory=True)[1];results=[]
    loaded={}
    for item in job['textures']:
        original=item['lod0Texture'];key=(original['resource']['path'],original['identifier'])
        if key not in loaded:
            if sha(original['resource']['path'])!=original['resource']['sha256']:raise ValueError('The saved LOD0 pigment changed.')
            owner=resources.load_serialized_bundle(key[0],in_memory=True)[1]
            loaded[key]=resources.texture_mip_images(owner.objects[key[1]])[0]
        own,bones=saved_skin_surface(json.dumps(item['surface'],sort_keys=True))
        donor,donor_bones=saved_skin_surface(json.dumps(item['lod0Surface'],sort_keys=True))
        frame,_,_=fields.skin_palette_frame(own,donor,bones,donor_bones)
        target=fields.geometry(own,fields.points(fields.array(own,'vertices',3),frame),fields.normalize(fields.array(own,'normals',3)@np.linalg.inv(frame[:3,:3])),fields.array(own,'tangents',4))
        reference=fields.geometry(donor,fields.array(donor,'vertices',3),fields.array(donor,'normals',3),fields.array(donor,'tangents',4))
        obj=current.objects[item['identifier']];data=obj.read();images=resources.texture_mip_images(obj)
        if data.m_TextureFormat!=25 or data.m_ColorSpace!=1:raise ValueError('The skin LOD requires the established sRGB BC7 source.')
        height,width=images[0].shape[:2]
        mass=fields.regional_fraction_uv(own,fields.regional_bone_fraction(own,bones),width,height,overlap='protect-complement')[0]
        desired,owned,projection=fields.bake_skin_lod_pigment(reference,target,images[0],loaded[key],mass)
        payload,storage,levels=fields.encode_skin_lod_mips(images,data.get_image_data(),desired,owned)
        results.append(dict(cab=current.name,object=item['identifier'],name=data.m_Name,dimensions=[data.m_Width,data.m_Height],mipCount=data.m_MipCount,storageFormat=storage,payload=payload,mips=levels,projection=projection))
    return results


def finish_skin_lod_resource(job):
    import character_resources as resources
    target,receipt=Path(job['target']),Path(job['receipt'])
    identity=dict(sourceSha256=job['sha256'],inputPlan=job['inputPlan'],policy='saved-lod0-skin-surface-v1')
    if receipt.exists():
        prior=read(receipt)
        if prior['identity']!=identity:raise ValueError('A skin LOD checkpoint belongs to another input plan.')
        if target.exists()and sha(target)==prior['sha256']:
            resources.validate_owned_texture_mips(prior);return prior
    root=receipt.parent.parent/'Fields'/target.stem;root.mkdir(parents=True,exist_ok=True);replacements=[];reports=[]
    for ordinal,row in enumerate(skin_lod_pigment_payloads(job)):
        raw=root/('Main'+str(ordinal)+'.bc7');raw.write_bytes(row.pop('payload'))
        reports.append(dict(identifier=row['object'],mips=row.pop('mips'),projection=row.pop('projection')))
        row['raw']=dict(path=str(raw.resolve()),sha256=sha(raw));replacements.append(row)
    result=resources.replace_owned_texture_mips(job['source'],job['sha256'],target,replacements)
    result.update(file=target.name,identity=identity,policy=identity['policy'],skinLodPigment=reports)
    write(receipt,result);return result


def validate_skin_lod_pigment_resource(operation):
    import character_resources as resources
    resources.validate_owned_texture_mips(operation)
    pin=operation['identity']['inputPlan']
    if operation.get('policy')!='saved-lod0-skin-surface-v1'or sha(pin['path'])!=pin['sha256']:raise ValueError('The skin LOD input authority changed.')
    job=read(pin['path'])
    if job['sha256']!=operation['beforeSha256']:raise ValueError('The skin LOD input lost its preceding resource.')
    expected={row['object']:row for row in skin_lod_pigment_payloads(job)}
    if set(expected)!={row['object']for row in operation['textures']}:raise ValueError('A skin LOD transaction omitted an owned texture.')
    for row in operation['textures']:
        if Path(row['raw']['path']).read_bytes()!=expected[row['object']]['payload']:raise ValueError('A saved skin LOD differs from its actual source-surface bake.')


def finish_skin_lods():
    """Finish an independent catalog using its declared current-LOD skin owners."""
    import character_resources as resources
    from concurrent.futures import ProcessPoolExecutor,as_completed
    parser=argparse.ArgumentParser();parser.add_argument('--source',type=Path,required=True);parser.add_argument('--geometry',type=Path,required=True);parser.add_argument('--workers',type=int,default=4)
    args=parser.parse_args();source=args.source.resolve();output=require_workspace();catalog=output/'Catalog'
    if source==output or not 1<=args.workers<=8:raise ValueError('Skin LOD finishing requires an independent workspace and bounded workers.')
    source_manifest=source/(Path('Catalog')/RESOURCE_FILES['manifest']);assembly=read(source/'ResourceAssembly.json');manifest=read(source_manifest)
    if assembly['catalogSha256']!=sha(source_manifest):raise ValueError('The skin LOD catalog source changed.')
    geometry=read(args.geometry);catalog.mkdir(exist_ok=True);proofs={r['file']:r for r in assembly['proofs']}
    pin=lambda p:dict(path=str(Path(p).resolve()),sha256=sha(p))
    def current(reference):
        result=dict(reference)
        for name in('resource','meshResource','materialResource'):
            entry=reference[name];path=source/'Catalog'/Path(entry['path']).name
            if sha(path)!=entry['sha256']:raise ValueError('The actual skin geometry differs from its declared surface owner.')
            result[name]=pin(path)
        return result
    by_owner={}
    for item in geometry['textures']:
        if len(item['pigmentSources'])!=1 or len(item['owners'])!=1:raise ValueError('A skin bake requires one declared actual surface per texture owner.')
        declared=item['pigmentSources'][0];surface=current(declared['currentSource']);surface['bones']=declared['skin']['bones']
        file=resources.load_serialized_bundle(surface['resource']['path'],in_memory=True)[1]
        renderer=file.objects[surface['renderer']].read_typetree();pointer=renderer['m_GameObject']
        if pointer['m_FileID']:raise ValueError('A skin renderer lost its own source object.')
        name=file.objects[pointer['m_PathID']].read_typetree()['m_Name'];match=re.fullmatch(r'FaceShape_LOD([0-2])',name)
        if not match:raise ValueError('The declared face surface has no actual LOD identity: '+name)
        lod=int(match.group(1));owner=item['owners'][0];group=by_owner.setdefault(owner,{})
        if lod in group:raise ValueError('A face surface has two textures claiming the same LOD.')
        group[lod]=dict(identifier=item['identifier'],file=item['file'],surface=surface)
    jobs={};surfaces=0
    for owner,group in by_owner.items():
        if set(group)!={0,1,2}:raise ValueError('A skin source must declare every actual LOD.')
        donor=group[0];lod0=dict(resource=pin(source/'Catalog'/donor['file']),identifier=donor['identifier'])
        for lod in(1,2):
            item=group[lod];name=item['file'];job=jobs.setdefault(name,dict(source=str((source/'Catalog'/name).resolve()),sha256=proofs[name]['sha256'],target=str((catalog/name).resolve()),receipt=str((output/'SkinLodPigments/Receipts'/(name+'.json')).resolve()),textures=[]))
            job['textures'].append(dict(identifier=item['identifier'],surface=item['surface'],lod0Surface=donor['surface'],lod0Texture=lod0));surfaces+=1
    for row in manifest['bundles']:
        target=catalog/row['file']
        if row['file']not in jobs and not target.exists():target.hardlink_to(source/'Catalog'/row['file'])
    inputs=[pin(args.geometry),pin(source/'ResourceAssembly.json'),pin(source_manifest)]
    for original in(Path(__file__),Path(resources.__file__),Path(__file__).with_name('material_fields.py')):
        archive=output/'ProducerSources'/sha(original)[:12]/original.name;archive.parent.mkdir(parents=True,exist_ok=True)
        if archive.exists()and sha(archive)!=sha(original):raise ValueError('The saved skin LOD producer epoch changed.')
        archive.write_bytes(original.read_bytes());inputs.append(pin(archive))
    for name,job in jobs.items():
        plan=output/'SkinLodPigments/Inputs'/(name+'.json')
        if plan.exists()and read(plan)!=job:raise ValueError('A skin LOD checkpoint has another input plan.')
        if not plan.exists():write(plan,job)
        job['inputPlan']=pin(plan)
    reports=[]
    with ProcessPoolExecutor(max_workers=args.workers)as pool:
        pending=[pool.submit(finish_skin_lod_resource,job)for job in jobs.values()]
        for future in as_completed(pending):
            try:result=future.result()
            except Exception as error:
                for item in pending:item.cancel()
                print('REJECTED skin LOD resource:',repr(error),flush=True)
                raise
            row=proofs[result['file']];row.update(sha256=result['sha256'],bytes=Path(result['target']['path']).stat().st_size,allRetainedObjectFieldsExactAfterReferenceRebasing=False)
            row.setdefault('appearanceFinishing',[]).append(dict(kind='saved-skin-lod-pigment',**result));reports.append(result)
            print('Saved source-surface skin LOD:',len(reports),'/',len(jobs),result['file'],flush=True)
    precision_extra=sum(texture['bytes']-sum(((max(1,texture['width']>>level)+3)//4)*((max(1,texture['height']>>level)+3)//4)*16 for level in range(texture['mipCount']))for result in reports for texture in result['textures']if texture['format']==4)
    if precision_extra>256*1024**2:raise ValueError('Precise skin LODs exceed the complete catalog storage allowance.')
    for row in manifest['bundles']:row['sha256']=proofs[row['file']]['sha256']
    write(catalog/RESOURCE_FILES['manifest'],manifest)
    assembly.update(catalogSha256=sha(catalog/RESOURCE_FILES['manifest']),bytes=sum(r['bytes']for r in assembly['proofs']),artAccepted=False,preparedSource=dict(assembly=pin(source/'ResourceAssembly.json'),catalog=pin(source_manifest)),skinLodPigment=dict(policy='saved-lod0-skin-surface-v1',inputs=inputs,heads=len(by_owner),textures=surfaces,files=len(reports),rgbaPrecisionExtraBytes=precision_extra,review='pending'))
    write(output/'ResourceAssembly.json',assembly)
    print('Prepared saved skin LODs:',len(by_owner),'heads,',surfaces,'textures; native review pending.',flush=True)


def resource_prefab_skins(manifest,proofs,address,load):
    """Resolve typed prefab roots and their real serialized skin renderers."""
    import character_resources as resources
    by_cab={row['newCab']:row for row in proofs.values()}
    matches=[row for row in manifest['resources']if row['source']==address.removeprefix('UB/CharacterResources/')]
    if len(matches)!=1:raise ValueError('A skin interface needs one declared prefab resource: '+address)
    resource=matches[0];file=load(proofs[resource['bundles'][0]['file']]['newCab'])
    def resolve(file,pointer):
        target=load(file.name if not pointer['m_FileID']else file.externals[pointer['m_FileID']-1].name)
        return target,target.objects[pointer['m_PathID']]
    container=next(obj for obj in file.objects.values()if obj.type.name=='AssetBundle').read().m_Container
    roots=[obj for name,value in container if name.casefold()==resource['asset'].casefold()
        for _,obj in [resolve(file,dict(m_FileID=value.asset.file_id,m_PathID=value.asset.path_id))]
        if obj.type.name=='GameObject']
    if len(roots)!=1:raise ValueError('A skin interface has no unique prefab root.')
    return list(resources.prefab_skin_renderers(roots[0],resolve)),resolve


def resource_skin_surfaces(source,manifest,proofs,address,load):
    """Read the actual prefab, renderer, material slot and sampler ownership."""
    import character_resources as resources
    renderers,resolve=resource_prefab_skins(manifest,proofs,address,load);result=[]
    for path,renderer in renderers:
        tree=renderer.read_typetree();mesh_file,mesh=resolve(renderer.assets_file,tree['m_Mesh'])
        renderer_to_prefab=resources.renderer_frame(renderer,resolve)
        for slot,pointer in enumerate(tree['m_Materials']):
            mf,material=resolve(renderer.assets_file,pointer);mt=material.read_typetree();_,shader=resolve(mf,mt['m_Shader'])
            shader_form=shader.read_typetree()['m_ParsedForm'];shader_name=shader_form['m_Name']
            if not shader_name.startswith('Lux/Human/Skin'):continue
            declared={p['m_Name']:p for p in shader_form['m_PropInfo']['m_Props']}
            props=dict(mt['m_SavedProperties']['m_TexEnvs']);textures={};defaults={}
            channels=('_MainTex','_BumpMap','_SpecTex','_ColorMaskMap')
            if not {'_MainTex','_BumpMap'}<=declared.keys():raise ValueError('A skin shader has no albedo and normal sampler contract.')
            for prop in channels:
                if prop not in declared:continue
                if prop not in props or not props[prop]['m_Texture']['m_PathID']:
                    name=declared[prop]['m_DefTexture']['m_DefaultName']
                    color={'white':[1.,1.,1.,1.],'black':[0.,0.,0.,1.],'gray':[.5,.5,.5,1.],
                        'bump':[.5,.5,1.,1.],'red':[1.,0.,0.,1.]}.get(name,[.5,.5,.5,1.])
                    transform=props.get(prop,dict(m_Scale=dict(x=1.,y=1.),m_Offset=dict(x=0.,y=0.)))
                    defaults[prop]=dict(name=name,rgba=color,scale=[transform['m_Scale'][axis]for axis in'xy'],
                        offset=[transform['m_Offset'][axis]for axis in'xy']);continue
                tf,texture=resolve(mf,props[prop]['m_Texture']);data=texture.read()
                textures[prop]=dict(cab=tf.name,object=texture.path_id,name=data.m_Name,width=data.m_Width,height=data.m_Height,
                    mips=data.m_MipCount,colorSpace=data.m_ColorSpace,scale=[props[prop]['m_Scale'][axis]for axis in 'xy'],
                    offset=[props[prop]['m_Offset'][axis]for axis in 'xy'])
            normal_scales=declared.keys()&{'_BumpScale','_BumpMapScale'};floats=dict(mt['m_SavedProperties']['m_Floats'])
            if len(normal_scales)>1:raise ValueError('A skin shader has conflicting normal scale properties.')
            normal_scale=floats.get(next(iter(normal_scales)),1.)if normal_scales else 1.
            result.append(dict(path=path,renderer=renderer.path_id,cab=renderer.assets_file.name,meshCab=mesh_file.name,mesh=mesh.path_id,
                packet=export_current_mesh(mesh),bones=resources.skin_bone_paths(renderer.assets_file,tree,resolve),slot=slot,
                material=material.path_id,materialCab=mf.name,shader=shader_name,rendererToPrefab=renderer_to_prefab,
                textures=textures,defaultTextures=defaults,floats=floats,normalScale=normal_scale))
    return result


def collar_cloth_samples(native, target, proof):
    """Locate each added collar vertex on the same native chart used to build it."""
    count=native['vertexCount']
    samples=proof.get('sourceAttributeSamples',[])
    if len(samples)!=target['vertexCount']-count:
        raise ValueError('Missing native surface for an added simulated collar vertex.')
    if not samples:return []
    origins=proof.get('origins',[])
    stencils=[]
    for index,sample in enumerate(samples,count):
        if sample is None:
            if index>=len(origins) or not 0<=origins[index]<count:
                raise ValueError('A collar alias has no native source vertex.')
            stencil={origins[index]:1.}
        else:
            a,b=sample['edge'];fraction=sample['fraction']
            if a==b or not 0<=fraction<=1:raise ValueError('Invalid native edge sample for a simulated collar.')
            stencil={a:1.-fraction,b:fraction}
        stencils.append(stencil)
    required={vertex for stencil in stencils for vertex in stencil}
    faces_by_vertex=collections.defaultdict(list)
    for indices in native['subMeshes']:
        for start in range(0,len(indices),3):
            face=indices[start:start+3]
            if len(set(face))!=3:continue
            for vertex in required.intersection(face):faces_by_vertex[vertex].append(face)
    result=[]
    for index,stencil in enumerate(stencils,count):
        face=next((face for face in faces_by_vertex[next(iter(stencil))] if stencil.keys()<=set(face)),None)
        if face is None:raise ValueError('A collar attachment has no original native source triangle.')
        result.append(dict(vertex=index,surface=dict(triangle=list(face),barycentric=[stencil.get(i,0.) for i in face])))
    return result


def rebuild_surface_cloth(file, meshes):
    """Rebuild linked simulation data from the final render geometry and its native attachment."""
    import character_resources as resources
    from mesh_recipe_codec import source_cloth_binding
    components={i:obj.read_typetree() for i,obj in file.objects.items() if obj.type.name=='MonoBehaviour'}
    linked=set()
    for tree in components.values():
        deformer=tree.get('deformer')
        reference=deformer.get('sharedMesh') if isinstance(deformer,dict) else None
        if reference and reference['m_FileID']==0 and reference['m_PathID'] in meshes:
            linked.add(reference['m_PathID'])
    if not linked:return {},[]
    replacements={}
    for mesh in sorted(linked):
        native,target,frame,proof=meshes[mesh]
        samples=collar_cloth_samples(native,target,proof)
        binding=source_cloth_binding(native,target,frame.T.ravel().tolist(),samples)
        replacements[mesh]=dict(native=native,raw=native,packet=target,binding=binding)
    result=resources.prepare_native_cloth(components,replacements)
    return {i:tree for i,tree in components.items() if tree!=file.objects[i].read_typetree()},result['changes']


def skin_interface_cloth_edits(file, interface):
    """Replay a saved collar's cloth data without recomputing its texture fields."""
    import character_resources as resources
    geometry = interface['geometry']
    original = checked_source_path(geometry['source']['path'])
    if sha(original) != geometry['source']['sha256']:
        raise ValueError('The source used to build the skin interface changed.')
    native = resources.load_serialized_bundle(original, in_memory=True)[1]
    edited = set(geometry['objects'])
    links = set()
    for item in native.objects.values():
        if item.type.name != 'MonoBehaviour':
            continue
        deformer = item.read_typetree().get('deformer')
        pointer = deformer.get('sharedMesh') if isinstance(deformer, dict) else None
        if pointer and pointer['m_FileID'] == 0 and pointer['m_PathID'] in edited:
            links.add(pointer['m_PathID'])
    if not links:
        return {}, []
    geometry_file = checked_source_path(geometry['target']['path'])
    if sha(geometry_file) != geometry['target']['sha256']:
        raise ValueError('The saved collar geometry changed.')
    authored = resources.load_serialized_bundle(geometry_file, in_memory=True)[1]
    proofs = {row['renderer'].rsplit('/', 1)[-1]: row['geometry']
              for row in interface['facts'] if 'geometry' in row}
    def local(owner, reference):
        if reference['m_FileID']:
            raise ValueError('A skin interface uses an external skeletal transform.')
        return owner, owner.objects[reference['m_PathID']]
    meshes = {}
    for renderer in native.objects.values():
        if renderer.type.name != 'SkinnedMeshRenderer':
            continue
        tree = renderer.read_typetree()
        ident = tree['m_Mesh']['m_PathID']
        if tree['m_Mesh']['m_FileID'] or ident not in links:
            continue
        frame = resources.renderer_frame(renderer, local)
        name = local(native, tree['m_GameObject'])[1].peek_name()
        native_mesh = export_current_mesh(native.objects[ident])
        current = export_current_mesh(file.objects[ident])
        if current['sourceFingerprint'] != export_current_mesh(authored.objects[ident])['sourceFingerprint']:
            raise ValueError('The current collar differs from its recorded source.')
        meshes[ident] = (native_mesh, current, frame, proofs[name])
    if set(meshes) != links:
        raise ValueError('Skin interface cloth has no matching saved renderer.')
    edits, details = rebuild_surface_cloth(native, meshes)
    updates = {}
    for key, tree in edits.items():
        if file.objects[key].read_typetree() == tree:
            continue
        if resources.serialized_object_bytes(file.objects[key]) != resources.serialized_object_bytes(native.objects[key]):
            raise ValueError('Skin interface cloth has a later owner.')
        updates[key] = tree
    return updates, details


def verify_skin_interface_cloth_repair(receipt):
    import character_resources as resources
    source = resources.load_serialized_bundle(receipt['source']['path'], in_memory=True)[1]
    changes, _ = skin_interface_cloth_edits(source, read(receipt['interface']['path']))
    target = resources.load_serialized_bundle(receipt['target']['path'], in_memory=True)[1]
    if not changes or sorted(changes) != receipt['objects'] or set(source.objects) != set(target.objects):
        raise ValueError('The reopened skin interface repair changed its object set.')
    for key, tree in changes.items():
        source.objects[key].save_typetree(tree)
    if any(resources.serialized_object_bytes(obj) != resources.serialized_object_bytes(target.objects[key])
           for key, obj in source.objects.items()):
        raise ValueError('Saved cloth data does not match the repaired skin interface.')


def repair_skin_interface_cloth():
    import character_resources as resources
    parser = argparse.ArgumentParser()
    parser.add_argument('--source', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    source, output = map(checked_catalog_path, (args.source, args.output))
    if output.is_relative_to(source):
        raise ValueError('A repaired catalog requires an independent output.')
    manifest_path = source / 'Catalog' / RESOURCE_FILES['manifest']
    assembly_path = source / 'ResourceAssembly.json'
    manifest, assembly = read(manifest_path), read(assembly_path)
    pin = lambda path: dict(path=str(path.resolve()), sha256=sha(path))
    if assembly['catalogSha256'] != sha(manifest_path):
        raise ValueError('The skin interface repair source changed.')
    catalog = output / 'Catalog'
    catalog.mkdir(parents=True)
    proofs = {row['file']: row for row in assembly['proofs']}
    repairs = 0
    for bundle in manifest['bundles']:
        name = bundle['file']
        original, destination = checked_catalog_path(manifest_path.parent, name), checked_catalog_path(catalog, name)
        proof = proofs[name]
        interfaces = [row['proof'] for row in proof.get('appearanceFinishing', []) if row['kind'] == 'source-skin-interface']
        if interfaces:
            interface = interfaces[-1]
            if sha(original) != bundle['sha256'] or sha(interface['path']) != interface['sha256']:
                raise ValueError('A skin interface repair input changed.')
            env, file = resources.load_serialized_bundle(original, in_memory=True)
            edits, details = skin_interface_cloth_edits(file, read(interface['path']))
            if edits:
                for key, tree in edits.items():
                    file.objects[key].save_typetree(tree)
                destination.write_bytes(env.file.save(packer='lz4'))
                receipt = dict(file=name, source=pin(original), target=pin(destination), interface=interface,
                               objects=sorted(edits), physics=details)
                verify_skin_interface_cloth_repair(receipt)
                path = output / 'Receipts' / (name + '.json')
                write(path, receipt)
                proof.setdefault('appearanceFinishing', []).append(dict(kind='skin-interface-cloth-repair',
                    beforeSha256=bundle['sha256'], sha256=receipt['target']['sha256'], proof=pin(path)))
                bundle['sha256'] = receipt['target']['sha256']
                proof.update(sha256=bundle['sha256'], bytes=destination.stat().st_size, allRetainedObjectFieldsExactAfterReferenceRebasing=False)
                repairs += 1
                print('Repaired skin interface cloth:', name, flush=True)
                continue
        destination.hardlink_to(original)
    path = catalog / RESOURCE_FILES['manifest']
    write(path, manifest)
    assembly.update(catalogSha256=sha(path), artAccepted=False, bytes=sum(row['bytes'] for row in proofs.values()),
                    preparedSource=dict(assembly=pin(assembly_path), catalog=pin(manifest_path)))
    assembly.pop('artAcceptance', None)
    write(output / 'ResourceAssembly.json', assembly)
    print('Skin interface cloth bundles repaired:', repairs, flush=True)


def observed_garment_surfaces(capture, selected, layers=frozenset(), *, layer_reference=None, layer_distance=.01):
    """Pair equipment with skin from the same presentation and detail level."""
    import numpy as np
    production, _ = source_owner()
    reference_parts = collections.defaultdict(list)
    if layer_reference:
        for part in layer_reference['meshes']:
            reference_parts[part['renderer']].append(part)

    def reference_surface(part):
        matches = [reference for reference in reference_parts[part['renderer']]
                   if part['submeshes'] == reference['submeshes'] and part['skinMaterials'] == reference['skinMaterials']]
        return matches[0] if len(matches) == 1 else None

    def triangles_for(part, select_skin):
        vertices = np.asarray(part['vertices'], dtype=float).reshape(-1, 3)
        indices = [np.asarray(indices, int).reshape(-1, 3)
                   for is_skin, indices in zip(part['skinMaterials'], part['submeshes'], strict=True)
                   if is_skin == select_skin and indices]
        return vertices[np.concatenate(indices)] if indices else np.empty((0, 3, 3))
    levels = collections.defaultdict(list)
    owners = collections.defaultdict(list)
    for slot in capture['actor'].get('nativeParts', []):
        for mesh in slot['meshes']:
            owners[mesh['meshId']].append(slot)
    for part in capture['meshes']:
        if part['forceRenderingOff']:
            continue
        if isinstance(part['mesh'], dict):
            # Appearance UI captures enumerate enabled renderers for each LOD.
            mesh = part['mesh']
            part = dict(part, mesh=mesh['name'], observedBindPoses=mesh['bindPoses'], sourceSlots=owners[mesh['id']])
            detail = re.search(r'_LOD(\d+)$', part['renderer'], re.IGNORECASE)
            level = int(detail[1]) if detail else None
        else:
            # The scene capture records only the renderers drawn by this camera.
            level = None
        levels[level].append(part)
    for level, parts in levels.items():
        equipment = [(slot['source'].removeprefix('UB/CharacterResources/'), part)
                     for part in parts for slot in part['sourceSlots'] if not slot['shape']]
        equipment = [(address, part) for address, part in equipment if address in selected]
        if not equipment:
            continue
        sources = parts + (levels.get(None, []) if level is not None else [])
        skin = np.concatenate([triangles_for(part, True) for part in sources])
        if not len(skin):
            raise ValueError('No matching skin in the supplied garment observation.')
        for address, part in equipment:
            lining = []
            used_layers = set()
            for owner, mesh in equipment:
                if owner != address or mesh is part:
                    continue
                pair = mesh['renderer'], part['renderer']
                family = tuple(re.sub(r'_LOD\d+$', '', name, flags=re.IGNORECASE) for name in pair)
                matches = layers.intersection((pair, family, pair[::-1], family[::-1]))
                if not matches:
                    continue
                outside_reference, inside_reference = map(reference_surface, (part, mesh))
                if outside_reference is None or inside_reference is None:
                    raise ValueError(f'Layered garment {address} requires the same native reference geometry.')
                rest_outer, rest_inner = triangles_for(outside_reference, False), triangles_for(inside_reference, False)
                used_layers.update(matches)
                if not len(rest_outer) or not len(rest_inner):
                    continue
                transform = np.asarray(outside_reference['localToWorld']).reshape(4, 4).T
                distance = layer_distance * np.linalg.norm(transform[:3, :3], axis=0).max()
                mask = production.garment_inside_faces(rest_outer, rest_inner, distance)
                if mask.any():
                    lining.append(triangles_for(mesh, False)[mask])
            yield address, dict(part, fittedLayers=sorted(used_layers)), np.concatenate([skin, *lining]) if lining else skin


def fit_garment_resources():
    """Fit drawn equipment and publish its changed mesh and simulation fields into a candidate."""
    parser = argparse.ArgumentParser()
    parser.add_argument('--source', type=Path, required=True)
    parser.add_argument('--pose', type=Path, nargs='+')
    parser.add_argument('--equipment', nargs='+')
    parser.add_argument('--selection', type=Path, help='JSON mapping equipment addresses to their observed pose paths')
    parser.add_argument('--layer', nargs=2, action='append', default=[], metavar=('RENDERER', 'RENDERER'))
    parser.add_argument('--clearance', type=float, required=True)
    parser.add_argument('--maximum-offset', type=float, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    if args.selection and (args.pose or args.equipment) or not args.selection and not (args.pose and args.equipment):
        parser.error('Use --selection or supply both --pose and --equipment.')
    if args.layer and not args.selection:
        parser.error('Layer fitting requires a selection with native reference poses.')
    source = checked_catalog_path(args.source)
    output = checked_catalog_path(args.output)
    if output.is_relative_to(source):
        raise ValueError('Garment fitting requires a new independent catalog.')
    output.mkdir(parents=True, exist_ok=False)
    try:
        write_posed_garments(args, source, output)
    except BaseException:
        shutil.rmtree(checked_catalog_path(output))
        raise


def write_posed_garments(args, source, output):
    import character_resources as resources

    manifest_path = source / 'Catalog' / RESOURCE_FILES['manifest']
    assembly_path = source / 'ResourceAssembly.json'
    manifest, assembly = read(manifest_path), read(assembly_path)
    def pin(path):
        return dict(path=str(path.resolve()), sha256=sha(path))
    if assembly['catalogSha256'] != sha(manifest_path):
        raise ValueError('The garment input catalog has changed.')
    # Detect an unavailable hard link before spending time fitting poses.
    catalog = output / 'Catalog'
    catalog.mkdir()
    probe = catalog / '.link-probe'
    probe.hardlink_to(checked_catalog_path(source / 'Catalog', manifest['bundles'][0]['file']))
    probe.unlink()
    proofs = {row['file']: row for row in assembly['proofs']}
    by_cab = {row['newCab']: row for row in proofs.values()}
    @lru_cache(maxsize=8)
    def load(cab):
        row = by_cab[cab]
        path = checked_catalog_path(source / 'Catalog', row['file'])
        if sha(path) != row['sha256']:
            raise ValueError('The garment source bundle has changed.')
        return resources.load_serialized_bundle(path, in_memory=True)[1]

    prefix = 'UB/CharacterResources/'
    selection = collections.defaultdict(set)
    references = {}
    if args.selection:
        document = read(checked_source_path(args.selection))
        rows = document['equipment']
        references = {checked_source_path(path): checked_source_path(value)
                      for path, value in document.get('layerReferences', {}).items()}
        if not isinstance(rows, dict) or not rows:
            raise ValueError('The garment selection needs equipment addresses with pose lists.')
        for key, paths in rows.items():
            if not isinstance(paths, list) or not paths:
                raise ValueError('The garment selection needs poses for ' + key)
            for path in paths:
                selection[checked_source_path(path)].add(key.removeprefix(prefix))
    else:
        for path in args.pose:
            selection[checked_source_path(path)].update(key.removeprefix(prefix) for key in args.equipment)
    selected = set().union(*selection.values())
    layers = {tuple(pair) for pair in args.layer}
    if any(inner == outer for inner, outer in layers):
        raise ValueError('A renderer cannot be its own garment underlayer.')
    production, _ = source_owner()
    # Keep packets for fitted meshes only. Pose files may contain a whole party.
    jobs = {}
    seen = set()
    seen_layers = set()
    @lru_cache(maxsize=8)
    def load_pose(path):
        with gzip.open(path, 'rt', encoding='utf8') as stream:
            return json.load(stream), pin(path)
    for pose_path, pose_equipment in selection.items():
        capture, pose_pin = load_pose(pose_path)
        reference, reference_pin = load_pose(references[pose_path]) if pose_path in references else (None, None)
        for address, part, skin in observed_garment_surfaces(capture, pose_equipment, layers,
                layer_reference=reference, layer_distance=args.maximum_offset * 2):
            renderers, resolve = resource_prefab_skins(manifest, proofs, address, load)
            matches = []
            for path, renderer in renderers:
                tree = renderer.read_typetree()
                file, mesh = resolve(renderer.assets_file, tree['m_Mesh'])
                if path.rsplit('/', 1)[-1] == part['renderer'] and mesh.peek_name() == part['mesh'].removesuffix('(Clone)').strip():
                    matches.append((renderer, tree, file, mesh))
            if len(matches) != 1:
                raise ValueError('Drawn garment renderer does not resolve uniquely in its prefab: ' + part['renderer'])
            renderer, tree, file, mesh = matches[0]
            if file.name != renderer.assets_file.name:
                raise ValueError('The garment renderer and its simulation need the same serialized mesh owner.')
            seen.add(address)
            entries = jobs.setdefault(by_cab[file.name]['file'], {})
            if mesh.path_id not in entries:
                entries[mesh.path_id] = dict(native=export_current_mesh(mesh), target=None, poses=[],
                                             frame=resources.renderer_frame(renderer, resolve))
            entry = entries[mesh.path_id]
            bones = resources.skin_bone_paths(file, tree, resolve)
            target, count = production.fit_posed_garment(entry['native'], part, skin, bones, entry['frame'],
                clearance=args.clearance, maximum_offset=args.maximum_offset, previous=entry['target'])
            entry['target'] = target
            seen_layers.update(part['fittedLayers'])
            observation = dict(source=pose_pin, equipment=address, renderer=part['renderer'],
                              layers=part['fittedLayers'], fittedVertices=count)
            if part['fittedLayers']:
                observation['layerReference'] = reference_pin
            entry['poses'].append(observation)
    if seen != selected:
        raise ValueError('Equipment absent from the supplied poses: ' + ', '.join(sorted(selected - seen)))
    if seen_layers != layers:
        raise ValueError('Garment layer pairs absent from the supplied poses: ' + str(sorted(layers - seen_layers)))

    for bundle in manifest['bundles']:
        name = bundle['file']
        original = checked_catalog_path(source / 'Catalog', name)
        destination = checked_catalog_path(catalog, name)
        entries = {key: record for key, record in jobs.get(name, {}).items()
                   if record['target']['sourceFingerprint'] != record['native']['sourceFingerprint']}
        if not entries:
            destination.hardlink_to(original)
            continue
        env, file = resources.load_serialized_bundle(original, in_memory=True)
        if sha(original) != bundle['sha256']:
            raise ValueError('The observed garment changed before fitting.')
        edits, physics = rebuild_surface_cloth(file, {key: (v['native'], v['target'], v['frame'], {})
                                                      for key, v in entries.items()})
        for key, record in entries.items():
            edits[key] = resources.write_mesh_frame(file.objects[key], record['target'])
        for key, tree in edits.items():
            file.objects[key].save_typetree(tree)
        destination.write_bytes(env.file.save(packer='lz4'))
        _, saved = resources.load_serialized_bundle(destination, in_memory=True)
        if set(saved.objects) != set(file.objects) or any(resources.serialized_object_bytes(saved.objects[key]) !=
                resources.serialized_object_bytes(obj) for key, obj in file.objects.items()):
            raise ValueError('Saved garment and cloth data differ from the fitted objects.')
        receipt = dict(file=name, source=pin(original), target=pin(destination), clearance=args.clearance,
                       maximumOffset=args.maximum_offset, physics=physics, objects=sorted(edits),
                       meshes=[dict(object=key, before=v['native']['sourceFingerprint'],
                                    after=export_current_mesh(saved.objects[key])['sourceFingerprint'], poses=v['poses'])
                               for key, v in entries.items()])
        path = output / 'Receipts' / (name + '.json')
        write(path, receipt)
        proof = proofs[name]
        proof.setdefault('appearanceFinishing', []).append(dict(kind='posed-garment-fit', proof=pin(path),
            beforeSha256=bundle['sha256'], sha256=receipt['target']['sha256']))
        bundle['sha256'] = receipt['target']['sha256']
        proof.update(sha256=bundle['sha256'], bytes=destination.stat().st_size, allRetainedObjectFieldsExactAfterReferenceRebasing=False)
    write(catalog / RESOURCE_FILES['manifest'], manifest)
    assembly.update(catalogSha256=sha(catalog / RESOURCE_FILES['manifest']), artAccepted=False,
                    preparedSource=dict(assembly=pin(assembly_path), catalog=pin(manifest_path)),
                    bytes=sum(row['bytes'] for row in proofs.values()))
    write(output / 'ResourceAssembly.json', assembly)


def skin_interface_geometry(job):
    """Transport the physical cut in every LOD; keep original UVs and facial tissue."""
    import character_resources as resources
    import numpy as np
    source=Path(job['catalog']);manifest=read(source/(Path('Catalog')/RESOURCE_FILES['manifest']))
    proofs={row['file']:row for row in read(source/'ResourceAssembly.json')['proofs']};by_cab={row['newCab']:row for row in proofs.values()}
    @lru_cache(maxsize=12)
    def load(cab):
        pin=by_cab[cab];path=source/'Catalog'/pin['file']
        if sha(path)!=pin['sha256']:raise ValueError('A skin-interface source changed.')
        return resources.load_serialized_bundle(path,in_memory=True)[1]
    heads=resource_skin_surfaces(source,manifest,proofs,job['head'],load)
    bodies=resource_skin_surfaces(source,manifest,proofs,job['body'],load)
    import material_fields as fields
    _,art=source_owner();trees={};facts=[];surfaces=[];done={};cloth_meshes={}
    for head in heads:
        lod=re.search(r'LOD(\d+)$',head['path'])
        if lod is None:raise ValueError('A skin interface has no explicit renderer LOD.')
        matches=[]
        for body in bodies:
            if not body['path'].endswith('LOD'+lod[1])or body['shader']!=head['shader']:continue
            h=dict(head['packet'],subMeshes=[head['packet']['subMeshes'][head['slot']]])
            b=dict(body['packet'],subMeshes=[body['packet']['subMeshes'][body['slot']]])
            frame=np.linalg.solve(body['rendererToPrefab'],head['rendererToPrefab'])
            pairs=art.shared_skin_boundary_pairs(h,b,head['bones'],body['bones'],.001,common_frame=frame)
            if len(pairs)>=3 or art.skin_cut_matches(h,b,frame,allow_unshared=True,bone_paths=head['bones'],donor_bone_paths=body['bones']):
                skin_palette_frame(h,b,head['bones'],body['bones'],common_frame=frame)
                matches.append((body,pairs,frame))
        if not matches:
            facts.append(dict(renderer=head['path'],slot=head['slot'],status='NO_SHARED_PHYSICAL_CUT'));continue
        if len(matches)!=1:raise ValueError('A physical skin cut has conflicting body material owners.')
        body,pairs,frame=matches[0];key=(head['meshCab'],head['mesh']);identity=(body['meshCab'],body['mesh'],tuple(pairs),tuple(frame.ravel()))
        if any(not {'_MainTex','_BumpMap'}<=surface['textures'].keys()for surface in(head,body)):
            raise ValueError('A shared physical skin cut has no saved albedo or normal texture.')
        for prop in head['defaultTextures'].keys()|body['defaultTextures'].keys():
            hd=head['defaultTextures'].get(prop);bd=body['defaultTextures'].get(prop)
            if hd and bd and hd['rgba']==bd['rgba']:continue
            for receiver,other,default in((head,body,hd),(body,head,bd)):
                if default:
                    template=other['textures'].get(prop,receiver['textures']['_MainTex'])
                    receiver['textures'][prop]=dict(template,defaultColor=default['rgba'],defaultTexture=default['name'],
                        scale=default['scale'],offset=default['offset'])
                    if receiver is head:
                        material=trees.setdefault(head['material'],load(head['materialCab']).objects[head['material']].read_typetree())
                        entries=material['m_SavedProperties']['m_TexEnvs']
                        if prop not in dict(entries):entries.append((prop,dict(m_Texture=dict(m_FileID=0,m_PathID=0),
                            m_Scale=dict(zip('xy',default['scale'])),m_Offset=dict(zip('xy',default['offset'])))))
        if key in done:
            if done[key][0]!=identity:raise ValueError('A shared receiver mesh has conflicting skin-cut owners.')
            candidate,bones=done[key][1:]
        else:
            rebuild=len(pairs)<3
            if not rebuild:
                candidate,proof=art.conform_skin_boundary(head['packet'],body['packet'],head['bones'],body['bones'],pairs,
                    maximum_source_gap=.001,shading_frame_owner='donor',common_frame=frame)
                bones=[*head['bones'],*[row['path']for row in proof['appendedBones']]]
                try:fields.serialized_skin_context(dict(candidate,subMeshes=[candidate['subMeshes'][head['slot']]]),
                    dict(body['packet'],subMeshes=[body['packet']['subMeshes'][body['slot']]]),bones,body['bones'],common_frame=frame)
                except ValueError as error:
                    if str(error)not in ('The actual physical cut is not a degree-two closed boundary.','Serialized skin surfaces do not share a complete physical cut.'):raise
                    rebuild=True
            if rebuild:
                receiver=dict(head['packet'],subMeshes=[head['packet']['subMeshes'][head['slot']]])
                donor=dict(body['packet'],subMeshes=[body['packet']['subMeshes'][body['slot']]])
                try:candidate,proof=art.rebuild_skin_boundary(receiver,donor,head['bones'],body['bones'],repeat_uv=True,allow_unshared_cuts=True,common_frame=frame)
                except ValueError as error:
                    if str(error)!='The native skin cut has no outward, nondegenerate collar strip.':raise
                    continuation=None
                    if receiver['shapes']or donor['shapes']:
                        receiver,continuation=art.continue_skin_body_frame(receiver,donor,head['bones'],body['bones'],common_frame=frame)
                        try:candidate,proof=art.rebuild_skin_boundary(receiver,donor,head['bones'],body['bones'],repeat_uv=True,allow_unshared_cuts=True,common_frame=frame)
                        except ValueError as retry_error:
                            if str(retry_error)!='The native skin cut has no outward, nondegenerate collar strip.':raise
                            candidate,proof=art.retarget_skin_boundary(receiver,donor,head['bones'],body['bones'],common_frame=frame)
                    else:candidate,proof=art.retarget_skin_boundary(receiver,donor,head['bones'],body['bones'],common_frame=frame)
                    if continuation:proof['bodyFrameContinuation']=continuation
                span=min(float(np.linalg.norm(np.ptp(np.asarray(surface['packet']['vertices']).reshape(-1,3)[proof[key]],axis=0)))
                    for surface,key in ((head,'sourceCut'),(body,'bodyCut')))
                if proof['maximumNativeCutGap']>proof['maximumAllowedNativeCutGap']:raise ValueError('The matched native skin cuts exceed their physical aperture scale.')
                proof['apertureScaleMetres']=span
                slots=copy.deepcopy(head['packet']['subMeshes']);slots[head['slot']]=candidate['subMeshes'][0];candidate['subMeshes']=slots
                bones=[*head['bones'],*[row['path']for row in proof['appendedBones']]];rebuild=True
                fields.serialized_skin_context(dict(candidate,subMeshes=[candidate['subMeshes'][head['slot']]]),
                    dict(body['packet'],subMeshes=[body['packet']['subMeshes'][body['slot']]]),bones,body['bones'],common_frame=frame)
            proof.update(frameAuthority='serialized-renderer-frames',commonHeadToBody=frame.T.ravel().tolist())
            if any(by_cab[cab]['file']!=job['file']for cab in (head['meshCab'],head['cab'],head['materialCab'])):
                raise ValueError('A skin-cut mutation must belong to its complete prefab resource.')
            trees[head['mesh']]=resources.write_mesh_frame(load(head['meshCab']).objects[head['mesh']],candidate,
                source_bones=head['bones'],target_bones=bones,collar_topology=rebuild)
            cloth_meshes[head['mesh']]=(head['packet'],candidate,head['rendererToPrefab'],proof)
            facts.append(dict(renderer=head['path'],slot=head['slot'],geometry=proof));done[key]=(identity,candidate,bones)
        tree=load(head['cab']).objects[head['renderer']].read_typetree()
        tree,palette=resources.extend_skin_bone_palette(load(head['cab']),tree,head['bones'],bones,packet=candidate)
        trees[head['renderer']]=tree
        surfaces.append((dict(head,packet=candidate,bones=bones,sourceBones=head['bones'],commonFrame=frame),body,int(lod[1])))
    surfaces.sort(key=lambda value:value[2])
    if job.get('rasterOverlap'):
        closed=set()
        for head,body,_ in surfaces:
            key=(head['meshCab'],head['mesh'])
            if key in closed:continue
            closed.add(key)
            frame=head['commonFrame']
            pairs=art.shared_skin_boundary_pairs(head['packet'],body['packet'],head['bones'],body['bones'],common_frame=frame)
            raster,proof=art.overlap_skin_boundary(head['packet'],body['packet'],pairs,frame,follow_donor_shading=True)
            trees[head['mesh']]=resources.write_mesh_frame(load(head['meshCab']).objects[head['mesh']],raster,
                source_bones=head['sourceBones'],target_bones=head['bones'],
                collar_topology=raster['vertexCount']!=export_current_mesh(load(head['meshCab']).objects[head['mesh']])['vertexCount'])
            native,_,renderer_frame,collar=cloth_meshes[head['mesh']]
            cloth_meshes[head['mesh']]=(native,raster,renderer_frame,collar)
            facts.append(dict(kind='shared-cut-raster-overlap',renderer=head['path'],**proof))
    if cloth_meshes:
        updates,changes=rebuild_surface_cloth(load(surfaces[0][0]['meshCab']),cloth_meshes)
        trees.update(updates)
        if changes:facts.append(dict(kind='native-cloth-attachment',changes=changes))
    return trees,facts,surfaces,load,by_cab


def skin_interface_fields(surfaces,load,*,pigment_reference='body'):
    """Compose Main, tangent normals and scattering from their saved physical cut."""
    import numpy as np
    import character_resources as resources
    import material_fields as fields
    if pigment_reference not in ('body','source'):raise ValueError('Unknown skin pigment reference.')
    def mip_images(ref):
        images=resources.texture_mip_images(load(ref['cab']).objects[ref['object']])
        if 'defaultColor'not in ref:return images
        rgba=np.asarray(ref['defaultColor'],float).copy()
        if ref['colorSpace']==1:rgba[:3]=np.where(rgba[:3]<=.0031308,rgba[:3]*12.92,1.055*rgba[:3]**(1/2.4)-.055)
        color=np.rint(rgba*255).astype(np.uint8)
        return [np.broadcast_to(color,image.shape).copy()for image in images]
    packets={};family=[];owners=[];epoch=dict(fieldSha256=sha(fields.__file__),domainSha256=sha(fields.__file__))
    for index,(head,body,lod)in enumerate(surfaces):
        def packet(surface,prop):
            p=copy.deepcopy(surface['packet']);p['subMeshes']=[p['subMeshes'][surface['slot']]]
            for channel in p['uv']:
                if channel['channel']==0:
                    coords=np.asarray(channel['values']).reshape(-1,4);ref=surface['textures'][prop]
                    coords[:,:2]=coords[:,:2]*ref['scale']+ref['offset'];channel['values']=coords.ravel().tolist()
            return p
        hp=packet(head,'_MainTex');bp=packet(body,'_MainTex');hk='Head'+str(index);bk='Body'+str(index);packets.update({hk:hp,bk:bp})
        row=dict(head=head['path'],lod=lod,headNativeBonePaths=head['bones'],bodyNativeBonePaths=body['bones'],commonHeadToBody=head['commonFrame'].T.ravel().tolist(),
            headCanonical=dict(path=hk),bodyCanonical=dict(path=bk),headSource=dict(geometrySource='saved-serialized-skin-surfaces',initialCanonical=dict(path=hk)),
            sampler=dict(textureInputs=dict(headMain=head['textures']['_MainTex'],bodyMain=body['textures']['_MainTex']),
                capturedNativeMainST=dict(headMainScale=[1,1],headMainOffset=[0,0],bodyMainScale=[1,1],bodyMainOffset=[0,0])))
        family.append(row);owners.append((head,body,row,packet))
    def field_key(head,body,prop):
        scales=(head['normalScale'],body['normalScale'])if prop=='_BumpMap'else()
        return prop,json.dumps(head['textures'][prop],sort_keys=True),json.dumps(body['textures'][prop],sort_keys=True),scales
    emitted={}
    for head,body,row,packet in owners:
        if set(head['textures'])!=set(body['textures']):raise ValueError('Shared skin shaders have different saved sampler contracts.')
        for prop in head['textures']:
            ref=head['textures'][prop];other=body['textures'][prop];key=field_key(head,body,prop)
            # Private LOD materials can still sample one common texture. Own
            # that sampler/reference pair once across its complete LOD family.
            if key in emitted:continue
            images=mip_images(ref);other_images=mip_images(other)
            hp=packet(head,prop);bp=packet(body,prop);current=copy.deepcopy(row)
            current['sampler']['textureInputs']=dict(headMain=ref,bodyMain=other)
            role_family=[];role_packets={}
            for h,b,r,make_packet in owners:
                if field_key(h,b,prop)!=key:continue
                role_family.append(r);role_packets[r['headCanonical']['path']]=make_packet(h,prop);role_packets[r['bodyCanonical']['path']]=make_packet(b,prop)
            try:h,b,he,be,hc,bc,context=fields.source_context(current,hp,bp)
            except ValueError as error:raise ValueError(head['path']+'/'+prop+': '+str(error))from error
            memo=fields.RasterMemo()
            decode=lambda a,r:fields.linear_rgba(a)if r['colorSpace']==1 else a.astype(float)/255
            original=decode(images[0],ref);donor=decode(other_images[0],other)
            alignment=None
            if prop=='_MainTex':
                if ref['colorSpace']!=1 or other['colorSpace']!=1:raise ValueError('A skin Main field needs its native sRGB contract.')
                if pigment_reference=='body':_,alignment=fields.align_head_main(original,donor,hc,bc)
                values,domain,proof=fields.prepare_head_main(current,hp,bp,role_family,role_packets,original,donor,epoch,memo,pigment_alignment=alignment)
                encoded_levels=list(fields.head_main_mips(values,domain,images,fields.lookup,np.arange(256,dtype=np.uint8)))
            else:
                domain=fields.domain_buffers(current,hp,bp,role_family,role_packets,epoch)
                if prop=='_BumpMap':
                    if ref['colorSpace'] or other['colorSpace']:raise ValueError('A skin normal field needs its native linear contract.')
                    values,proof=fields.c1_normal(h,hc,bc,original,donor,memo,domain,
                        head['normalScale'],body['normalScale'])
                else:values,_,proof=fields.c1_main(current,h,hc,bc,original,donor,memo,domain)
                owned=(domain['weight']>0)[::-1];protected=domain['protected'][::-1];encoded_levels=[]
                for level,baseline in enumerate(images):
                    if level:
                        values=fields.downsample(values).mean(axis=(1,3));owned=fields.downsample(owned).any(axis=(1,3));protected=fields.downsample(protected).any(axis=(1,3))
                    encoded=values.copy()
                    if ref['colorSpace']==1:encoded[:,:,:3]=np.where(encoded[:,:,:3]<=.0031308,encoded[:,:,:3]*12.92,1.055*np.maximum(encoded[:,:,:3],0)**(1/2.4)-.055)
                    encoded=np.rint(np.clip(encoded,0,1)*255).astype(np.uint8);mask=owned&~protected;result=baseline.copy();result[mask]=encoded[mask]
                    encoded_levels.append((result,owned.copy(),protected.copy()))
            source_object=load(ref['cab']).objects[ref['object']];format,original_mips=resources.texture_mip_payloads(source_object)
            if format not in (4,10,12,25):raise ValueError('A skin interface needs its declared RGBA32, DXT1, DXT5 or BC7 source storage.')
            source_format=format
            if format==10 and any(np.any(value[:,:,3]!=baseline[:,:,3])for (value,_,_),baseline in zip(encoded_levels,images,strict=True)):
                format=12
            encoded=[];codecs=[]
            for level,((value,_,_),baseline,original_bits)in enumerate(zip(encoded_levels,images,original_mips,strict=True)):
                h,w=value.shape[:2];changed=np.ones((h,w),bool)if 'defaultColor'in ref else np.any(value!=baseline,axis=2)
                block_mask,pixel_mask=fields.whole_block_pixels(changed)
                if not changed.any()and format==source_format:bits=original_bits;codec=dict(unownedBlocksExact=True,unchanged=True)
                elif format==4:bits=value.tobytes();codec=dict(unownedPixelsExact=True)
                elif format==10:bits,codec=fields.native_face_bc1_mip(original_bits,value,pixel_mask)
                elif format==12:
                    bits,_,codec=fields.native_face_bc3_mip(original_bits,source_format,value,(~pixel_mask)[::-1].astype(float))
                elif prop=='_BumpMap':bits,codec=fields.fit_bc7_normal_field(original_bits,w,h,value,pixel_mask)
                else:
                    bits,codec=fields.encode_owned_bc7_rgb(original_bits,value,block_mask.reshape((h+3)//4,(w+3)//4),require_budget=False)
                    codec['precisionBudgetMet']=codec['rgbRms']<=3 and codec['alphaRms']<=2
                expected=len(original_bits)*(2 if source_format==10 and format==12 else 1)
                if len(bits)!=expected:raise ValueError('A skin interface differs from its declared runtime texture storage.')
                encoded.append(bits);codecs.append(dict(level=level,**codec))
            payload=b''.join(encoded)
            material_ids=sorted({h['material']for h,b,_,_ in owners if field_key(h,b,prop)==key})
            emitted[key]=dict(materials=material_ids,property=prop,reference=ref,payload=payload,storageFormat=format,
                report=dict(renderer=head['path'],slot=head['slot'],property=prop,context=context,composition=proof,
                    sourceShaderDefault=ref.get('defaultTexture'),
                    codecs=codecs,sourceRuntimeBytes=sum(map(len,original_mips)),outputRuntimeBytes=len(payload),
                    levels=[dict(level=i,ownedTexels=int(o.sum()),protectedTexels=int(p.sum()))for i,(_,o,p)in enumerate(encoded_levels)]))
    return list(emitted.values())


def finish_skin_interface_bundle(job):
    path=Path(job['receipt'])
    if path.is_file():
        receipt=read(path)
        if receipt['inputPlan']!=job['inputPlan']:raise ValueError('A skin-interface checkpoint has another plan.')
        validate_skin_interface_finish(receipt,replay=False)
        return receipt
    with retained_skin_producer(job['inputs'])as owner:
        worker=getattr(owner,'write_skin_interface_bundle',None)or owner.finish_skin_interface_bundle
        return worker(job)


def write_skin_interface_bundle(job):
    import character_resources as resources
    target=Path(job['target']);receipt_path=Path(job['receipt']);work=receipt_path.parent.parent/'Fields'/target.stem
    if receipt_path.exists():
        receipt=read(receipt_path)
        if receipt['inputPlan']!=job['inputPlan']:raise ValueError('A skin-interface checkpoint has another plan.')
        validate_skin_interface_finish(receipt);return receipt
    work.mkdir(parents=True,exist_ok=True);trees,facts,surfaces,load,by_cab=skin_interface_geometry(job)
    if not surfaces:raise ValueError('Displayed skin owners have no shared physical cut: '+job['head']+' / '+job['body'])
    env,file=resources.load_serialized_bundle(job['source'],in_memory=True)
    original={i:resources.serialized_object_bytes(obj)for i,obj in file.objects.items()}
    for i,tree in trees.items():file.objects[i].save_typetree(tree)
    expected={i:resources.serialized_object_bytes(obj)for i,obj in file.objects.items()}
    geometry=work/'Geometry.bundle';geometry.write_bytes(env.file.save(packer='lz4'))
    _,saved=resources.load_serialized_bundle(geometry,in_memory=True)
    if any(resources.serialized_object_bytes(obj)!=expected[i]for i,obj in saved.objects.items()):raise ValueError('A skin-interface geometry failed its saved-object comparison.')
    textures=[];updates=[];reports=[];payloads={}
    for value in skin_interface_fields(surfaces,load,pigment_reference=job.get('pigmentReference','body')):
        ref=value['reference'];source=Path(job['catalog'])/'Catalog'/by_cab[ref['cab']]['file']
        source_pin=dict(path=str(source.resolve()),sha256=by_cab[ref['cab']]['sha256'])
        mip=resources.texture_mip_report(load(ref['cab']).objects[ref['object']],value['payload'],value['storageFormat'])
        textures.append(dict(source=source_pin,textures=[mip]));reports.append(value['report']);payloads[mip['payloadSha256']]=value['payload']
        reference=dict(source_pin,name=ref['name'],object=ref['object'],mips=dict(sha256=mip['payloadSha256'],format=mip['format'],bytes=mip['bytes']))
        for identifier in value['materials']:
            material=file.objects[identifier].read_typetree();before=dict(material['m_SavedProperties']['m_TexEnvs'])[value['property']]['m_Texture']
            updates.append(dict(pathId=identifier,property=value['property'],before=before,texture=reference))
    if updates:
        binding=resources.replace_owned_material_textures(geometry,sha(geometry),target,updates,payloads=payloads)
        textures=[resources.embedded_texture_mips(field['source'],field['textures'],binding)for field in textures]
    else:target.hardlink_to(geometry);binding=None
    pin=lambda p:dict(path=str(Path(p).resolve()),sha256=sha(p))
    receipt=dict(file=target.name,beforeSha256=job['sha256'],sha256=sha(target),bytes=target.stat().st_size,inputPlan=job['inputPlan'],
        geometry=dict(source=pin(job['source']),target=pin(geometry),objects=sorted(trees),unrelatedObjectBytesExact=all(expected[i]==original[i]for i in original if i not in trees)),
        binding=binding,fields=textures,reports=reports,facts=facts,objectsAdded=binding['objectsAdded']if binding else[])
    write(receipt_path,receipt);print('Closed skin interface:',job['file'],len(surfaces),'surfaces',flush=True);return receipt


def validate_skin_interface_finish(receipt,*,replay=True):
    """Recheck saved topology/palettes, private fields and their immutable producer."""
    import character_resources as resources
    plan=receipt['inputPlan']
    if sha(plan['path'])!=plan['sha256']:raise ValueError('The skin-interface plan changed.')
    job=read(plan['path']);geometry=receipt['geometry']
    for pin in (geometry['source'],geometry['target'],*job['inputs']):
        if sha(pin['path'])!=pin['sha256']:raise ValueError('A skin-interface input or saved geometry changed.')
    if geometry['source']['sha256']!=job['sha256']or not geometry['unrelatedObjectBytesExact']:raise ValueError('A skin interface lost its source ownership.')
    before=resources.load_serialized_bundle(geometry['source']['path'],in_memory=True)[1];after=resources.load_serialized_bundle(geometry['target']['path'],in_memory=True)[1]
    if before.name!=after.name or set(before.objects)!=set(after.objects)or [v.name for v in before.externals]!=[v.name for v in after.externals]:raise ValueError('A skin interface changed resource references.')
    if any(resources.serialized_object_bytes(after.objects[i])!=resources.serialized_object_bytes(obj)for i,obj in before.objects.items()if i not in geometry['objects']):raise ValueError('A skin interface changed an unrelated object.')
    for field in receipt['fields']:resources.validate_owned_texture_mips(field)
    if receipt['binding']:
        if receipt['binding']['beforeSha256']!=geometry['target']['sha256']or receipt['binding']['sha256']!=receipt['sha256']:raise ValueError('A skin-interface material lost its geometry source.')
        resources.validate_owned_material_textures(receipt['binding'])
    elif geometry['target']['sha256']!=receipt['sha256']:raise ValueError('An unchanged skin interface has another payload.')
    if job['file']!=receipt['file']or str(Path(job['source']).resolve())!=geometry['source']['path']:
        raise ValueError('The skin-interface receipt belongs to another prefab resource.')
    if not replay:return
    identity=source_skin_replay_key(receipt,job['inputs']);cache=HERE/'SourceSkinValidation'/(identity+'.json')if HERE is not None else None
    if cache is not None and cache.exists()and read(cache)==dict(status='PASS_SKIN_INTERFACE_REPLAY',identity=identity):return
    with retained_skin_producer(job['inputs'])as owner:
        trees,_,surfaces,load,_=owner.skin_interface_geometry(job)
        if sorted(trees)!=geometry['objects']:raise ValueError('A skin interface omits an owned mesh or renderer.')
        for i,tree in trees.items():
            before.objects[i].save_typetree(tree)
            if resources.serialized_object_bytes(before.objects[i])!=resources.serialized_object_bytes(after.objects[i]):
                raise ValueError('The saved skin interface differs from its physical source calculation.')
        fields=owner.skin_interface_fields(surfaces,load,**({'pigment_reference':job['pigmentReference']}if 'pigmentReference'in job else{}))
        if len(fields)!=len(receipt['fields']):raise ValueError('A skin interface omits a material channel.')
        final=None
        for expected,field in zip(fields,receipt['fields']):
            textures=field['textures']
            if field.get('scope')=='embedded-textures':
                if final is None:final=resources.load_serialized_bundle(field['target']['path'],in_memory=True)[1]
                actual=[bytes(final.objects[i].read().get_image_data())for i in textures[0]['targets']]if len(textures)==1 else[]
            else:actual=[Path(textures[0]['raw']['path']).read_bytes()]if len(textures)==1 else[]
            if not actual or any(value!=expected['payload']for value in actual):
                raise ValueError('A skin-interface field differs from its source-owned calculation.')
    if cache is not None:write(cache,dict(status='PASS_SKIN_INTERFACE_REPLAY',identity=identity))


def compact_skin_interface_inputs(path,output):
    """Retire only this job's verified scratch files after replacing their consumers."""
    import character_resources as resources
    path=checked_source_path(path);output=checked_catalog_path(output);receipt=read(path)
    if not receipt.get('binding'):return 0,0
    if all(field.get('scope')=='embedded-textures'for field in receipt['fields'])and not receipt.get('temporaryInputs'):return 0,0
    plan=receipt['inputPlan']
    if sha(plan['path'])!=plan['sha256']:raise ValueError('A scratch cleanup lost its immutable input plan.')
    job=read(plan['path'])
    if Path(job['receipt']).resolve()!=path or not path.is_relative_to(output/'Receipts')or \
            Path(job['target']).resolve()!=output/'Catalog'/receipt['file']:
        raise ValueError('A scratch cleanup belongs to another resource job.')
    binding,fields,retired=resources.compact_material_texture_inputs(receipt['binding'],receipt['fields'])
    retired=[*receipt.get('temporaryInputs',[]),*retired]
    scratch=checked_catalog_path(output/'Fields'/Path(job['target']).stem)
    live={Path(pin['path']).resolve()for pin in (receipt['geometry']['source'],receipt['geometry']['target'],binding['source'],binding['target'],
        *[field['source']for field in fields],*job['inputs'])}
    paths={}
    for reference in retired:
        candidate=Path(reference['path'])
        if not candidate.is_absolute()or not candidate.is_relative_to(scratch)or candidate in live or candidate in paths:
            raise ValueError('A scratch cleanup escaped its owned temporary files.')
        if candidate.exists()and (checked_source_path(candidate)!=candidate or sha(candidate)!=reference['sha256']):
            raise ValueError('An owned scratch file changed before retirement.')
        paths[candidate]=reference
    receipt.update(binding=binding,fields=fields,temporaryInputs=list(paths.values()));write(path,receipt)
    count=size=0
    for candidate,reference in paths.items():
        if not candidate.exists():continue
        if sha(checked_source_path(candidate))!=reference['sha256']:raise ValueError('An owned scratch file changed during retirement.')
        size+=candidate.stat().st_size;candidate.unlink();count+=1
    receipt.pop('temporaryInputs',None);write(path,receipt)
    return count,size


def observed_skin_interface(context,documents):
    """Use a displayed body owner; covered and absent bodies are not cut donors."""
    reference=context.get('display')
    if reference is None:
        if context.get('kind')=='actual-npc-presentation':raise ValueError('An actual NPC interface requires its native clothing observation.')
        return True
    key=(reference['path'],reference['sha256'])
    if key not in documents:
        if sha(reference['path'])!=reference['sha256']:raise ValueError('The skin-interface display observation changed.')
        documents[key]=read(reference['path'])
    actors=[row for row in documents[key]['actors']if row['fixture']['id']==context['fixture']]
    if len(actors)!=1 or not actors[0].get('completed'):raise ValueError('A skin interface has no completed display context.')
    actor=actors[0]
    if 'nativeNpc'in actor and actor.get('nativeClothingRetained')is not True:
        raise ValueError('An NPC cut donor must retain its actual native clothing.')
    parts={row['slot']:row for row in actor['parts']}
    same=lambda a,b:a.removeprefix('UB/CharacterResources/')==b.removeprefix('UB/CharacterResources/')
    if parts['Head'].get('present')is not True or not same(parts['Head']['key'],context['head']):
        raise ValueError('The observed skin interface belongs to another head.')
    if parts['Body'].get('present')is False:return False
    if parts['Body'].get('present')is not True or not same(parts['Body']['key'],context['body']):
        raise ValueError('The observed skin interface has no matching visible body.')
    return True


def prepare_skin_interfaces():
    """Process source/reference pairs obtained from actual character presentations."""
    from concurrent.futures import ProcessPoolExecutor,as_completed
    parser=argparse.ArgumentParser();parser.add_argument('--source',type=Path,required=True);parser.add_argument('--contexts',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True);parser.add_argument('--workers',type=int,choices=range(1,9),default=4);parser.add_argument('--resume',action='store_true')
    parser.add_argument('--compact-only',action='store_true')
    args=parser.parse_args();source=checked_catalog_path(args.source);output=checked_catalog_path(args.output)
    if output==source or not output.is_relative_to(ROOT/'tools'):raise ValueError('Skin interfaces need an independent tools workspace.')
    if output.exists()and not args.resume:raise ValueError('The skin-interface workspace already exists.')
    if (output/'ResourceAssembly.json').exists():raise ValueError('The skin-interface candidate is already complete.')
    retired_count=retired_bytes=0
    paths=sorted((output/'Receipts').glob('*.json'))
    with ProcessPoolExecutor(max_workers=args.workers,max_tasks_per_child=4)as pool:
        pending={pool.submit(compact_skin_interface_inputs,path,output):path for path in paths}
        for future in as_completed(pending):
            count,size=future.result();retired_count+=count;retired_bytes+=size
            if count:print('Removed completed texture scratch inputs:',pending[future].name,count,size,flush=True)
    if args.compact_only:
        print(json.dumps(dict(status='PASS_SKIN_SCRATCH_CLEANUP',deletedFiles=retired_count,deletedLogicalBytes=retired_bytes)),flush=True)
        return
    manifest=read(source/(Path('Catalog')/RESOURCE_FILES['manifest']));assembly=read(source/'ResourceAssembly.json');proofs={p['file']:p for p in assembly['proofs']}
    pin=lambda p:dict(path=str(Path(p).resolve()),sha256=sha(p));pins=[pin(source/'ResourceAssembly.json'),pin(source/(Path('Catalog')/RESOURCE_FILES['manifest'])),pin(args.contexts)]
    import character_resources as resources,material_fields,mesh_recipe_codec,collect_sources
    production,art=source_owner()
    sources=list(map(Path,(__file__,resources.__file__,material_fields.__file__,production.__file__,art.__file__,Path(__file__).with_name('resource-package.json'),
        mesh_recipe_codec.__file__,asset_names.__file__,collect_sources.__file__)))
    epoch=hashlib.sha256(json.dumps([(p.name,sha(p))for p in sources],separators=(',',':')).encode()).hexdigest()
    for path in sources:
        archive=output/'ProducerSources'/epoch/(path.name+'.source');archive.parent.mkdir(parents=True,exist_ok=True)
        if archive.exists()and sha(archive)!=sha(path):raise ValueError('A skin-interface producer changed; start a new workspace.')
        if not archive.exists():archive.write_bytes(path.read_bytes())
        pins.append(pin(archive))
    by_source={r['source']:r for r in manifest['resources']};tasks=[];unique={};catalog=output/'Catalog';catalog.mkdir(exist_ok=True)
    contexts=read(args.contexts)['rows'];documents={};visible=[];covered=[]
    for context in contexts:
        if observed_skin_interface(context,documents):visible.append(context)
        else:covered.append(dict(fixture=context['fixture'],head=context['head']))
        if context.get('display')and context['display']not in pins:pins.append(context['display'])
    for context in visible:
        head=context['head'].removeprefix('UB/CharacterResources/');body=context['body'].removeprefix('UB/CharacterResources/')
        resource=by_source[head];name=resource['bundles'][0]['file']
        pigment=context.get('pigmentReference','body')
        if pigment not in ('body','source'):raise ValueError('A skin interface has an unknown pigment reference.')
        if head in unique:
            if unique[head]!=(body,pigment):raise ValueError('One saved head requires incompatible body or pigment references.')
            continue
        unique[head]=(body,pigment);job=dict(catalog=str(source.resolve()),head=head,body=body,pigmentReference=pigment,rasterOverlap=True,file=name,source=str((source/'Catalog'/name).resolve()),sha256=proofs[name]['sha256'],
            target=str((catalog/name).resolve()),receipt=str((output/'Receipts'/(name+'.json')).resolve()),inputs=pins)
        path=output/'Inputs'/(name+'.json')
        if path.exists():
            previous=read(path)
            substantive=lambda value:{key:item for key,item in value.items()if key!='inputs'}
            references=lambda value:[p for p in value['inputs']if not Path(p['path']).is_relative_to(output/'ProducerSources')]
            if substantive(previous)!=substantive(job)or references(previous)!=references(job):raise ValueError('A skin-interface plan changed.')
            if Path(job['receipt']).is_file():job=previous
            else:write(path,job)
        else:write(path,job)
        job['inputPlan']=pin(path);tasks.append(job)
    selected={job['file']for job in tasks}
    for row in manifest['bundles']:
        if row['file']not in selected and not(catalog/row['file']).exists():(catalog/row['file']).hardlink_to(source/'Catalog'/row['file'])
    failures=[];precision_review=[]
    with ProcessPoolExecutor(max_workers=args.workers,max_tasks_per_child=4)as pool:
        pending={pool.submit(finish_skin_interface_bundle,job):job for job in tasks}
        for future in as_completed(pending):
            try:result=future.result()
            except Exception as error:
                message=pending[future]['file']+': '+str(error);failures.append(message);print('Skin interface failed:',message,flush=True);continue
            proof=proofs[result['file']];proof.update(sha256=result['sha256'],bytes=result['bytes'],objects=proof['objects']+len(result['objectsAdded']),allRetainedObjectFieldsExactAfterReferenceRebasing=False)
            path=output/'Receipts'/(result['file']+'.json');proof.setdefault('appearanceFinishing',[]).append(dict(kind='source-skin-interface',beforeSha256=result['beforeSha256'],sha256=result['sha256'],proof=pin(path)))
            for report in result['reports']:
                for level in report['codecs']:
                    if level.get('precisionBudgetMet')is False:
                        precision_review.append(dict(file=result['file'],renderer=report['renderer'],property=report['property'],**level))
    if failures:write(output/'Failures.json',dict(failures=failures));raise ValueError('Skin interfaces have rejected sources; completed jobs can be resumed.')
    (output/'Failures.json').unlink(missing_ok=True)
    for row in manifest['bundles']:row['sha256']=proofs[row['file']]['sha256']
    write(catalog/RESOURCE_FILES['manifest'],manifest)
    assembly.update(catalogSha256=sha(catalog/RESOURCE_FILES['manifest']),bytes=sum(p['bytes']for p in proofs.values()),artAccepted=False,
        preparedSource=dict(assembly=pin(source/'ResourceAssembly.json'),catalog=pin(source/(Path('Catalog')/RESOURCE_FILES['manifest']))),
        skinInterfaces=dict(contexts=pin(args.contexts),resources=len(tasks),coveredBodyContexts=covered,precisionReview=precision_review,review='pending'))
    write(output/'ResourceAssembly.json',assembly);print('Prepared skin interfaces:',len(tasks),flush=True)


def source_skin_detail_authority(proof,identifier,channel,*,retained_sources=None,requested=None):
    """Recover the absolute detail source through the actual private texture owner."""
    def document(pin):
        if sha(pin['path'])!=pin['sha256']:raise ValueError('A preceding skin-detail authority changed.')
        return read(pin['path'])
    for operation in reversed(proof.get('appearanceFinishing',[])):
        if operation['kind']=='source-skin-detail':
            job=document(operation['inputPlan']);matches=[t for t in job['textures']if t['object']==identifier]
            if not matches:continue
            calculation=operation;output_identifier=identifier
        elif operation['kind']=='source-skin-material-bindings':
            binding=document(operation['proof']);textures=[row for row in binding['textures']if row['object']==identifier]
            if not textures:continue
            if len(textures)!=1:raise ValueError('A private skin texture has conflicting detail owners.')
            texture=textures[0];receipts=[document(pin)for pin in operation['fields']]
            receipts=[row for row in receipts if row['target']['path']==texture['source']['path']and row['sha256']==texture['source']['sha256']]
            if len(receipts)!=1:raise ValueError('A private skin texture has no unique detail calculation.')
            calculation=receipts[0];output_identifier=texture['sourceObject']
            job=document(calculation['inputPlan']);matches=[row for row in job['textures']if row['object']==output_identifier]
        else:continue
        if len(matches)!=1 or matches[0]['channel']!=channel:raise ValueError('The preceding skin detail has another channel owner.')
        row=matches[0]
        source=row.get('detailSource',dict(path=job['source'],sha256=job['sha256'],object=row['object']))
        recovery=None
        if not Path(source['path']).is_file():
            retained=(retained_sources or {}).get(source['sha256'])
            if retained is None:raise ValueError('The original skin-detail source is absent from the retained catalog.')
            if sha(retained)!=source['sha256']:raise ValueError('The retained skin-detail content differs from its original authority.')
            recovery=dict(mode='same-content-source-resource',originalLocation=source['path'],
                originalSha256=source['sha256'],object=source['object'])
            source=dict(source,path=str(Path(retained).resolve()))
            recovery['retainedResource']=dict(path=source['path'],sha256=source['sha256'])
        if recovery is None and sha(source['path'])!=source['sha256']:raise ValueError('The original skin-detail source changed.')
        result=dict(source=source,profile=row['profile'])
        for key in('domain','luminanceRegions'):
            if key in row:result[key]=row[key]
        if recovery is not None:result['recovery']=recovery
        if requested is not None:
            import character_resources as resources
            def producers(pins):
                values={}
                for reference in pins:
                    name=Path(reference['path']).name.removesuffix('.source')
                    if not name.endswith('.py'):continue
                    if name in values:raise ValueError('A skin computation has conflicting producer owners.')
                    if not Path(reference['path']).is_file()or sha(reference['path'])!=reference['sha256']:
                        raise ValueError('A skin computation producer changed.')
                    values[name]=reference['sha256']
                return values
            previous=producers(calculation.get('computationInputs',job.get('inputs',[])))
            current=producers(requested['inputs'])
            reusable=bool(previous)and previous==current and row.get('normalEncoding')==requested.get('normalEncoding')and \
                calculation.get('computationEnvironment')==requested['environment']
            before_surfaces=row.get('surfaces',[]);after_surfaces=requested['surfaces']
            if not before_surfaces or len(before_surfaces)!=len(after_surfaces):reusable=False
            if reusable:
                for before,after in zip(before_surfaces,after_surfaces,strict=True):
                    for name,default in(('rendererToPrefab',None),('materialSlot',0),('completeMesh',False)):
                        if before.get(name,default)!=after.get(name,default):reusable=False
                    if not reusable:break
                    # Whole resource pins may change because another texture was
                    # finished. Compare the actual saved mathematical surface,
                    # including ST-adjusted UV, channels and ordered bone paths.
                    if saved_skin_surface(json.dumps(before,sort_keys=True))!=saved_skin_surface(json.dumps(after,sort_keys=True)):
                        reusable=False;break
            saved=[value for value in calculation.get('textures',[])if value['object']==output_identifier]
            if len(saved)!=1 or 'target'not in calculation or 'raw'not in saved[0]:reusable=False
            if reusable:
                target=calculation['target'];raw=saved[0]['raw']
                if not Path(raw['path']).is_file()or sha(raw['path'])!=raw['sha256']:
                    raise ValueError('A retained skin mip payload changed.')
                target_path=Path(target['path'])
                if not target_path.is_file():target_path=Path((retained_sources or {}).get(target['sha256'],target_path))
                if not target_path.is_file()or sha(target_path)!=target['sha256']:
                    raise ValueError('A preceding skin sampling checkpoint changed.')
                owner=resources.load_serialized_bundle(target_path,in_memory=True)[1]
                saved_texture=owner.objects[output_identifier];current_texture=requested['texture']
                # Names and stream placement do not enter sampling; all layout,
                # format, colour-space, mip and sampler metadata still do.
                ignored={'m_Name','m_StreamData','image data'}
                first={key:value for key,value in saved_texture.read_typetree().items()if key not in ignored}
                last={key:value for key,value in current_texture.read_typetree().items()if key not in ignored}
                payload=Path(raw['path']).read_bytes()
                if bytes(saved_texture.read().get_image_data())!=payload:
                    raise ValueError('A saved skin texture differs from its retained mip payload.')
                reusable=first==last and bytes(current_texture.read().get_image_data())==payload
            result['reusable']=reusable
        return result
    return None


def skin_filter_neighbourhoods(production,surfaces,shape,sampler,profile,regions,ownership,metric):
    """Bind one sampled texture to its highest-detail physical surface owner."""
    import numpy as np
    import material_fields as fields
    active_regions=[value for value in regions if value['strength']>0]
    if profile['pigmentRetain']==1 and not active_regions:
        return [],dict(sourceRetained=True)
    evaluation=ownership>0
    if profile['pigmentRetain']==1:
        evaluation=np.zeros(shape,bool)
        for region in active_regions:evaluation|=region['target']*ownership*(1-region['protected'])>0
    _,writable=fields.whole_block_pixels((ownership>0)[::-1]);evaluation&=writable[::-1]
    if not evaluation.any():return [],dict(requiredWitnesses=0,coveredWitnesses=0,noWritableOutput=True)
    highest=min(production.lod_index(dict(path=packet['name']))for _,packet,_ in surfaces)
    selected=[value for value in surfaces if production.lod_index(dict(path=value[1]['name']))==highest]
    identities={json.dumps(dict(mesh=packet['sourceFingerprint'],bones=bones,frame=surface['rendererToPrefab'],
        slot=surface.get('materialSlot',0)if surface.get('completeMesh',False)else 0),sort_keys=True)
        for surface,packet,bones in selected}
    if len(identities)!=1:raise ValueError('A sampled skin texture has multiple highest-detail physical owners.')
    surface,packet,bones=selected[0];slot=surface.get('materialSlot',0)if surface.get('completeMesh',False)else 0
    materials=[None]*(slot+1);materials[slot]=dict(properties=[dict(name='_MainTex',scale=[1,1],offset=[0,0])])
    skin=dict(bones=bones,rendererToPrefab=surface['rendererToPrefab'],materials=materials)
    # UV/ST is already applied by saved_skin_surface. Keep per-mip source
    # sampling exact instead of applying the original material ST twice.
    force=profile['pigmentRetain']<1 and np.any((ownership>0)&
        (metric<max(profile['coherenceSigmaMetres'],profile['featherMetres']/3)/128))
    for region in active_regions:
        support=ownership*(1-region['protected'])*(region['target']+region['reference'])
        radius=max(region['sourceLowFrequencySigmaMetres'],region.get('referenceSigmaMetres',0.))
        force=force or np.any((support>0)&(metric<radius/128))
    if force:evaluation=(ownership>0)&writable[::-1]
    patches,proof=production.prepare_source_neighbourhoods(packet,skin,slot,shape,sampler,profile,regions,evaluation,
        require_complete=bool(force))
    proof.update(sourceFingerprint=packet['sourceFingerprint'],highestDetailLod=highest,
        sampler=dict(sampler),textureDimensions=list(shape),arrayMetricRadiusLimitTexels=128,
        nativeUniformRadiusLimitTexels=None)
    return patches,proof


def source_skin_payloads(job):
    """Bake declared source domains while retaining the saved texture format and mips."""
    import numpy as np
    from PIL import Image
    import character_resources as resources
    import material_fields as fields
    import time
    production,_=source_owner()
    if sha(job['source'])!=job['sha256']:raise ValueError('A source skin resource changed.')
    file=resources.load_serialized_bundle(job['source'],in_memory=True)[1];results=[];surface_cache={}
    for texture in job['textures']:
        obj=file.objects[texture['object']];data=obj.read();images=resources.texture_mip_images(obj)
        sampler=dict(wrapU=data.m_TextureSettings.m_WrapU,wrapV=data.m_TextureSettings.m_WrapV)
        if hasattr(data.m_TextureSettings,'m_FilterMode'):
            sampler.update(filterMode=data.m_TextureSettings.m_FilterMode,anisoLevel=data.m_TextureSettings.m_Aniso)
        if hasattr(data.m_TextureSettings,'m_MipBias'):sampler['mipBias']=data.m_TextureSettings.m_MipBias
        storage,original_mips=resources.texture_mip_payloads(obj)
        if storage not in(4,12,25):raise ValueError('Unsupported source skin storage: '+str(data.m_TextureFormat))
        if data.m_ColorSpace!=(1 if texture['channel']=='color'else 0):raise ValueError('A skin channel has another sampled color space.')
        detail=texture.get('detailSource');reference_images=images
        if detail is not None:
            if sha(detail['path'])!=detail['sha256']:raise ValueError('The absolute skin-detail source changed.')
            reference=resources.load_serialized_bundle(detail['path'],in_memory=True)[1].objects[detail['object']];reference_data=reference.read()
            if (reference_data.m_Width,reference_data.m_Height,reference_data.m_MipCount,reference_data.m_ColorSpace)!=(data.m_Width,data.m_Height,data.m_MipCount,data.m_ColorSpace):
                raise ValueError('The absolute skin detail has another texture sampling contract.')
            reference_images=resources.texture_mip_images(reference)
        surfaces=[];surface_identity=[]
        for surface in texture['surfaces']:
            packet,bones=saved_skin_surface(json.dumps(surface,sort_keys=True))
            surfaces.append((surface,packet,bones))
            surface_identity.append(dict(source={key:value for key,value in surface.items()if key!='property'},
                sampledUvSha256=hashlib.sha256(json.dumps(packet['uv'],sort_keys=True).encode('utf-8')).hexdigest()))
        region_declarations=[]
        for declaration in texture.get('luminanceRegions',[]):
            production.source_skin_region_mode(declaration)
            region_declarations.append(declaration)
        encoded=list(original_mips);levels=[]
        # Validate coarse surface support before the expensive full-resolution bake.
        # Serialization still follows the source mip order.
        for level,(pixels,reference_pixels,original)in reversed(list(enumerate(zip(images,reference_images,original_mips,strict=True)))):
            h,w=pixels.shape[:2]
            if min(h,w)<4:continue
            print('Starting skin mip:',data.m_Name,level,str(w)+'x'+str(h),flush=True)
            mip_started=time.perf_counter()
            mip_sampler=dict(sampler,mipLevel=level)
            surface_key=json.dumps(dict(shape=[h,w],surfaces=surface_identity,domain=texture['domain'],sampler=mip_sampler),sort_keys=True)
            domains={surface_key:texture['domain']};region_keys=[]
            for declaration in region_declarations:
                keys={}
                for kind in('target','reference','protected'):
                    if kind=='protected'and declaration[kind]is None:keys[kind]=None;continue
                    domain=dict(texture['domain'],surfaceRegion=declaration[kind])
                    key=json.dumps(dict(shape=[h,w],surfaces=surface_identity,domain=domain,sampler=mip_sampler),sort_keys=True)
                    domains[key]=domain;keys[kind]=key
                region_keys.append(keys)
            missing={key:domain for key,domain in domains.items()if key not in surface_cache}
            if missing:
                fields_by_key={key:dict(ownership=np.ones((h,w)),covered=np.zeros((h,w),bool),geometry=[])for key in missing}
                metric=np.zeros((h,w))if surface_key in missing else surface_cache[surface_key][1]
                for surface,packet,bones in surfaces:
                    material_slot=surface['materialSlot']if surface.get('completeMesh',False)else 0
                    materials=[None]*(material_slot+1);materials[material_slot]=dict(properties=[dict(name='_MainTex',scale=[1,1],offset=[0,0])])
                    skin=dict(rendererToPrefab=surface['rendererToPrefab'],materials=materials)
                    evaluator=(dict(bone_domain=dict(bones=bones,declaration=texture['domain']))
                        if {'protectSurfaceRadiusMetres','protectSourceCharts','surfaceRegion'}&set(texture['domain'])else
                        dict(vertex_field=production.source_material_ownership(packet,bones,texture['domain'])))
                    extra={key:dict(bones=bones,declaration=domain)for key,domain in missing.items()if key!=surface_key}
                    if extra:
                        own,density,proof,coverage,additional=production.indexed_surface_uv_metrics(packet,skin,material_slot,(h,w),.001,mip_sampler,
                            additional_domains=extra,**evaluator)
                    else:
                        own,density,proof,coverage=production.indexed_surface_uv_metrics(packet,skin,material_slot,(h,w),.001,mip_sampler,**evaluator);additional={}
                    sampled=dict(additional)
                    if surface_key in missing:
                        sampled[surface_key]=dict(ownership=own,report=proof)
                        metric[coverage]=np.maximum(metric[coverage],density[coverage])
                    for key,result in sampled.items():
                        combined=fields_by_key[key];combined['ownership'][coverage]=np.minimum(combined['ownership'][coverage],result['ownership'][coverage])
                        combined['covered']|=coverage;combined['geometry'].append(result['report'])
                if surface_key in missing:
                    coverage=fields_by_key[surface_key]['covered'];metric[~coverage]=np.median(metric[coverage])
                for key,combined in fields_by_key.items():
                    combined['ownership'][~combined['covered']]=0
                    surface_cache[key]=(combined['ownership'],metric,combined['geometry'],combined['covered'])
            base_ownership,metric,geometry,base_covered=surface_cache[surface_key]
            ownership=base_ownership.copy()
            scale=max(texture['profile']['normalRadiusMetres'],texture['profile'].get('macroNormalRadiusMetres',0.))if texture['channel']=='normal'else max(
                texture['profile']['coherenceSigmaMetres'],texture['profile']['featherMetres']/3)
            unresolved=(ownership>0)&(metric<scale/128)
            if texture['channel']!='color':ownership[unresolved]=0
            regions=[];regional_geometry=[];unresolved_regions=[]
            for region_index,(declaration,keys)in enumerate(zip(region_declarations,region_keys,strict=True)):
                region={key:value for key,value in declaration.items()if key not in('target','reference','protected')}
                for kind in('target','reference','protected'):
                    if keys[kind]is None:region[kind]=np.zeros((h,w));continue
                    field,_,field_geometry,_=surface_cache[keys[kind]]
                    region[kind]=np.clip(np.divide(field,base_ownership,out=np.zeros_like(field),where=base_ownership>0),0,1)
                    regional_geometry.append(dict(region=region_index,kind=kind,surfaces=field_geometry))
                if not np.any(region['target']*ownership)or not np.any(region['reference']*ownership):
                    unresolved_regions.append(region_index);continue
                regions.append(region)
            domain_seconds=time.perf_counter()-mip_started
            sampling_started=time.perf_counter();neighbourhoods=[];sampling_proof=None
            if texture['channel']=='color':
                neighbourhoods,sampling_proof=skin_filter_neighbourhoods(production,surfaces,(h,w),mip_sampler,
                    texture['profile'],regions,ownership,metric)
            sampling_seconds=time.perf_counter()-sampling_started;filter_started=time.perf_counter()
            filters=dict(source_neighbourhoods=neighbourhoods)if neighbourhoods else{}
            desired,proof=production.refine_source_skin_material(reference_pixels[::-1],ownership,metric,texture['profile'],texture['channel'],
                normal_encoding=texture['normalEncoding'],luminance_regions=regions,**filters)
            filter_seconds=time.perf_counter()-filter_started
            if regional_geometry:proof.update(regionalGeometry=regional_geometry,unresolvedLuminanceRegions=unresolved_regions)
            proof.update(retainedSubpixelSurfaceSamples=int(unresolved.sum())if texture['channel']!='color'else 0,
                arrayMetricRadiusLimitTexels=128)
            if sampling_proof is not None:proof['nativeUniformSampling']=sampling_proof
            desired=desired[::-1].copy();_,pixel_mask=fields.whole_block_pixels((ownership>0)[::-1])
            if 'filterMode'in sampler:
                _,protected_blocks=fields.whole_block_pixels((base_covered&(base_ownership==0))[::-1])
                pixel_mask&=~protected_blocks
                proof['sampledProtectedBlockPixelsRetained']=int(protected_blocks.sum())
            desired[~pixel_mask]=pixels[~pixel_mask]
            rgb_error_bound=MAIN_RGB_BLOCK_REFINEMENT_ERROR
            codec_started=time.perf_counter()
            if storage==4:
                desired[~pixel_mask]=pixels[~pixel_mask];target=desired.tobytes()
                codec=dict(storage='RGBA32',unownedPixelsExact=True,additionalCompression=False)
            elif storage==12:
                target,codec=fields.fit_bc3_skin_field(original,w,h,desired,pixel_mask,texture['channel'],maximum_rgb_error=rgb_error_bound)
            elif texture['channel']=='color':
                target,codec=fields.fit_bc7_rgb_field(original,w,h,desired,pixel_mask,maximum_rgb_error=rgb_error_bound,allow_reencoding=True)
            else:
                target,codec=fields.fit_bc7_normal_field(original,w,h,desired,pixel_mask)
            actual=np.frombuffer(target,np.uint8).reshape(h,w,4)if storage==4 else np.asarray(Image.frombytes('RGBA',(w,h),target,'bcn',3 if storage==12 else 7))
            if texture['channel']=='color'and not np.array_equal(actual[:,:,3],pixels[:,:,3]):raise ValueError('Skin finishing changed native material alpha.')
            if texture['channel']=='normal'and not np.array_equal(actual[:,:,[0,2]],pixels[:,:,[0,2]]):raise ValueError('Skin finishing changed normal carrier channels.')
            if not np.array_equal(actual[~pixel_mask],pixels[~pixel_mask]):raise ValueError('Skin finishing changed an unowned texture pixel.')
            if texture['channel']=='color'and storage!=4:
                measured_error=int(np.abs(actual[:,:,:3].astype(int)-desired[:,:,:3].astype(int))[pixel_mask].max(initial=0))
                codec.update(maximumRgbError=measured_error,requestedMaximumRgbError=rgb_error_bound)
                if measured_error>rgb_error_bound:
                    raise ValueError('Source skin RGB reconstruction exceeds the declared error bound: '+data.m_Name+' mip '+str(level))
            encoded[level]=target;levels.append(dict(level=level,proof=proof,codec=codec,geometry=geometry,
                timings=dict(surfaceDomainsSeconds=domain_seconds,receiverPreparationSeconds=sampling_seconds,
                    filterSeconds=filter_seconds,codecAndReadbackSeconds=time.perf_counter()-codec_started,
                    totalMipSeconds=time.perf_counter()-mip_started)))
            print('Finished skin mip:',data.m_Name,level,flush=True)
        levels.sort(key=lambda value:value['level'])
        results.append(dict(object=obj.path_id,name=data.m_Name,cab=file.name,dimensions=[data.m_Width,data.m_Height],mipCount=data.m_MipCount,
            storageFormat=storage,payload=b''.join(encoded),mips=levels))
    return results


def finish_source_skin_bundle(job):
    with retained_skin_producer(job.get('computationInputs',job['inputs']))as owner:
        worker=getattr(owner,'write_source_skin_bundle',None)or owner.finish_source_skin_bundle
        return worker(job)


def write_source_skin_bundle(job):
    import character_resources as resources
    receipt=Path(job['receipt']);target=Path(job['target']);replacements=[];reports=[]
    if receipt.exists():
        prior=read(receipt)
        if prior['inputPlan']!=job['inputPlan']:raise ValueError('The skin checkpoint has other inputs.')
        resources.validate_owned_texture_mips(prior);return prior
    for row in source_skin_payloads(job):
        payload=row.pop('payload');levels=row.pop('mips');raw=receipt.parent.parent/'Fields'/(target.stem+'_'+str(row['object'])+'.mips')
        raw.parent.mkdir(parents=True,exist_ok=True);raw.write_bytes(payload)
        row['raw']=dict(path=str(raw.resolve()),sha256=sha(raw));replacements.append(row);reports.append(dict(object=row['object'],mips=levels))
    result=resources.replace_owned_texture_mips(job['source'],job['sha256'],target,replacements)
    result.update(file=target.name,inputPlan=job['inputPlan'],sourceSkinDetails=reports,computationInputs=job.get('computationInputs',job['inputs']),
        computationEnvironment=source_skin_computation_environment())
    write(receipt,result);return result


def source_skin_computation_environment():
    """One runtime identity for encoded skin production, reuse and replay."""
    from importlib.metadata import version
    return dict(python=list(sys.version_info[:3]),packages={name:version(name)for name in ('numpy','Pillow','UnityPy','etcpak','texture2ddecoder')})


def source_skin_replay_key(operation,pins):
    identity=dict(policy='source-owned-skin-replay-v1',
        operation={key:value for key,value in operation.items()if key!='kind'},computationInputs=pins,
        environment=source_skin_computation_environment())
    return hashlib.sha256(json.dumps(identity,sort_keys=True,separators=(',',':')).encode()).hexdigest()


def retained_skin_producer(pins):
    """Load the retained geometry and material epoch for a deterministic replay."""
    from contextlib import contextmanager
    import importlib.util
    from importlib.machinery import SourceFileLoader
    @contextmanager
    def epoch():
        producers={Path(pin['path']).name.removesuffix('.source'):pin for pin in pins if Path(pin['path']).name.endswith(('.py','.py.source'))}
        for pin in pins:
            if sha(pin['path'])!=pin['sha256']:raise ValueError('A retained skin producer changed.')
        def module(name,pin,register=False):
            loader=SourceFileLoader(name,pin['path']);spec=importlib.util.spec_from_loader(name,loader)
            if spec is None:raise ValueError('A retained skin computation module is unavailable.')
            value=importlib.util.module_from_spec(spec)
            if register:sys.modules[name]=value
            loader.exec_module(value);return value
        module_names=[name for name in('collect_sources','asset_names','mesh_recipe_codec','material_fields','character_resources')if name+'.py'in producers]
        previous={name:sys.modules.get(name)for name in module_names}
        search_paths=sys.path[:]
        try:
            # The builder and material helpers bind imported functions at load time.
            # Install dependencies first so retained epochs never capture live ones.
            for name in module_names:module(name,producers[name+'.py'],register=True)
            producer=producers['build_models.py'];owner=module('retained_skin_'+producer['sha256'][:12],producer)
            shape_owner=getattr(owner,'source_owner',source_owner)
            owner.__dict__['source_owner']=lambda:shape_owner(production_path=producers['production.py']['path'],art_path=producers['generate.py']['path'])
            yield owner
        finally:
            sys.path[:]=search_paths
            for name,value in previous.items():
                if value is None:sys.modules.pop(name,None)
                else:sys.modules[name]=value
    return epoch()


def validate_source_skin_finish(operation):
    import character_resources as resources
    resources.validate_owned_texture_mips(operation);pin=operation['inputPlan']
    if sha(pin['path'])!=pin['sha256']:raise ValueError('The source skin plan changed.')
    job=read(pin['path'])
    if job['sha256']!=operation['beforeSha256']:raise ValueError('The skin finish lost its preceding resource owner.')
    pins=operation.get('computationInputs',job['inputs']);producers={Path(pin['path']).name.removesuffix('.source'):pin for pin in pins if Path(pin['path']).name.endswith(('.py','.py.source'))}
    for pin in pins:
        if sha(pin['path'])!=pin['sha256']:raise ValueError('A retained skin computation input changed.')
    key=source_skin_replay_key(operation,pins)
    cache=checked_catalog_path(HERE/'SourceSkinValidation',key+'.json')if HERE is not None else None
    if cache is not None and cache.is_file():
        saved=read(cache)
        if saved.get('status')=='PASS_SOURCE_SKIN_REPLAY'and saved.get('identity')==key:return
    with retained_skin_producer(pins)as owner:
        expected={row['object']:row['payload']for row in owner.source_skin_payloads(job)}
    if set(expected)!={row['object']for row in operation['textures']}:raise ValueError('A skin finish omits an owned texture.')
    for row in operation['textures']:
        if Path(row['raw']['path']).read_bytes()!=expected[row['object']]:raise ValueError('The encoded skin finish differs from its source-owned calculation.')
    if cache is not None:write(cache,dict(status='PASS_SOURCE_SKIN_REPLAY',identity=key,inputPlan=operation['inputPlan']))


def complete_source_skin_jobs(source,output,assembly,manifest,tasks,pins,coverage,workers,*,channels=('color','normal')):
    from concurrent.futures import ProcessPoolExecutor,as_completed
    proofs={row['file']:row for row in assembly['proofs']};failures=[]
    with ProcessPoolExecutor(max_workers=workers,max_tasks_per_child=8)as pool:
        for future in as_completed([pool.submit(finish_source_skin_bundle,job)for job in tasks]):
            try:result=future.result()
            except Exception as error:
                failures.append(str(error));print('Skin preparation failed:',str(error),flush=True);continue
            proof=proofs[result['file']];proof.update(sha256=result['sha256'],bytes=result['bytes'],allRetainedObjectFieldsExactAfterReferenceRebasing=False)
            proof.setdefault('appearanceFinishing',[]).append(dict(kind='source-skin-detail',**result))
            print('Finished source skin:',result['file'],flush=True)
    if failures:
        write(output/'Failures.json',dict(failures=failures));raise ValueError('Source skin preparation failed; completed receipts can be resumed.')
    for row in manifest['bundles']:row['sha256']=proofs[row['file']]['sha256']
    write(output/(Path('Catalog')/RESOURCE_FILES['manifest']),manifest)
    assembly.update(catalogSha256=sha(output/(Path('Catalog')/RESOURCE_FILES['manifest'])),bytes=sum(row['bytes']for row in assembly['proofs']),artAccepted=False,
        sourceSkinDetails=dict(inputs=pins,coverage=coverage,bundles=len(tasks),channels=sorted(channels),review='pending'),
        preparedSource=dict(assembly=dict(path=str((source/'ResourceAssembly.json').resolve()),sha256=sha(source/'ResourceAssembly.json')),
            catalog=dict(path=str((source/(Path('Catalog')/RESOURCE_FILES['manifest'])).resolve()),sha256=sha(source/(Path('Catalog')/RESOURCE_FILES['manifest'])))))
    write(output/'ResourceAssembly.json',assembly)
    print(json.dumps(dict(status='PREPARED_SOURCE_SKIN_REVIEW_PENDING',sources=len(coverage),bundles=len(tasks))))






def bind_source_skin_materials():
    """Embed finished fields only in the material owners declared by their renderers."""
    import character_resources as resources
    parser=argparse.ArgumentParser();parser.add_argument('--source',type=Path,required=True);parser.add_argument('--fields',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True);parser.add_argument('--resume',action='store_true');args=parser.parse_args()
    source=checked_catalog_path(args.source);fields=checked_catalog_path(args.fields);output=checked_catalog_path(args.output)
    if (output.exists()and not args.resume)or not any(output.is_relative_to(ROOT/p)for p in('tools/asset-authoring/workspace','tools/game-test/artifacts')):
        raise ValueError('Skin material binding requires a new authoring workspace.')
    if (output/'ResourceAssembly.json').exists():raise ValueError('The skin material candidate is already complete.')
    prepared=read(fields/'ResourceAssembly.json');original=prepared['preparedSource']
    if original['assembly']['sha256']!=sha(source/'ResourceAssembly.json')or original['catalog']['sha256']!=sha(source/(Path('Catalog')/RESOURCE_FILES['manifest'])):
        raise ValueError('The skin fields belong to another source material catalog.')
    manifest=read(source/(Path('Catalog')/RESOURCE_FILES['manifest']));assembly=read(source/'ResourceAssembly.json');proofs={row['file']:row for row in assembly['proofs']}
    groups={};references={}
    @lru_cache(maxsize=4)
    def material_owner(name):return resources.load_serialized_bundle(source/'Catalog'/name,in_memory=True)[1]
    for input_path in sorted((fields/'Inputs').glob('*.json')):
        job=read(input_path);receipt_path=Path(job['receipt']);receipt=read(receipt_path)
        if receipt['inputPlan']!=dict(path=str(input_path.resolve()),sha256=sha(input_path)):
            raise ValueError('A skin field has another immutable calculation plan.')
        resources.validate_owned_texture_mips(receipt)
        donor={row['object']:row for row in receipt['textures']}
        for texture in job['textures']:
            field=donor[texture['object']]
            for surface in texture['surfaces']:
                owner=surface['materialResource'];path=Path(owner['path']);name=path.name
                if owner!=surface['resource']or path!=(source/'Catalog'/name).resolve()or owner['sha256']!=proofs[name]['sha256']:
                    raise ValueError('A skin field requires its own native prefab material owner.')
                material=material_owner(name).objects[surface['material']].read_typetree()
                before=dict(material['m_SavedProperties']['m_TexEnvs'])[surface['property']]['m_Texture']
                update=dict(pathId=surface['material'],property=surface['property'],before=before,
                    texture=dict(path=receipt['target']['path'],sha256=receipt['sha256'],name=field['name'],object=field['object']))
                key=(surface['material'],surface['property']);group=groups.setdefault(name,{})
                if key in group and group[key]!=update:raise ValueError('A native material has conflicting source fields.')
                group[key]=update;references.setdefault(name,{})[str(receipt_path.resolve())]=dict(path=str(receipt_path.resolve()),sha256=sha(receipt_path))
    catalog=output/'Catalog';catalog.mkdir(parents=True,exist_ok=args.resume)
    for bundle in manifest['bundles']:
        name=bundle['file'];target=catalog/name
        if name not in groups:
            if target.exists():
                if sha(target)!=bundle['sha256']:raise ValueError('An unselected resumed skin resource changed.')
            else:target.hardlink_to(source/'Catalog'/name)
            continue
        path=output/'Receipts'/(name+'.json')
        if args.resume and path.exists():
            receipt=read(path);resources.validate_owned_material_textures(receipt)
            clones={row['object']:row for row in receipt['textures']}
            recovered={(row['pathId'],row['property']):dict(pathId=row['pathId'],property=row['property'],before=row['before'],
                texture=clones[row['after']['m_PathID']]['source'])for row in receipt['materialUpdates']}
            if receipt['source']!=dict(path=str((source/'Catalog'/name).resolve()),sha256=bundle['sha256'])or \
                    receipt['target']['path']!=str(target.resolve())or recovered!=groups[name]:
                raise ValueError('A resumed skin binding belongs to another source or field plan.')
        else:
            if target.exists():raise ValueError('A skin output without its completed binding receipt already exists.')
            receipt=resources.replace_owned_material_textures(source/'Catalog'/name,bundle['sha256'],target,list(groups[name].values()))
            write(path,receipt)
        proof=proofs[name]
        before_sha=proof['sha256'];proof.update(sha256=receipt['sha256'],bytes=receipt['bytes'],allRetainedObjectFieldsExactAfterReferenceRebasing=False,
            objects=proof['objects']+len(receipt['objectsAdded']))
        proof.setdefault('appearanceFinishing',[]).append(dict(kind='source-skin-material-bindings',beforeSha256=before_sha,sha256=receipt['sha256'],
            proof=dict(path=str(path.resolve()),sha256=sha(path)),fields=list(references[name].values())))
        bundle['sha256']=proof['sha256'];print('Bound local source skin:',name,flush=True)
    write(catalog/RESOURCE_FILES['manifest'],manifest)
    assembly.setdefault('retainedSourceInputs',[]).extend(prepared.get('retainedSourceInputs',[]))
    assembly.update(catalogSha256=sha(catalog/RESOURCE_FILES['manifest']),bytes=sum(row['bytes']for row in assembly['proofs']),artAccepted=False,
        preparedSource=dict(assembly=original['assembly'],catalog=original['catalog']),sourceSkinDetails=prepared['sourceSkinDetails'],
        skinMaterialBindings=dict(fields=dict(path=str((fields/'ResourceAssembly.json').resolve()),sha256=sha(fields/'ResourceAssembly.json')),
            localMaterials=True,bundles=len(groups),unselectedResourcesUnchanged=True))
    write(output/'ResourceAssembly.json',assembly)
    print(json.dumps(dict(status='PREPARED_LOCAL_SKIN_MATERIALS_REVIEW_PENDING',bundles=len(groups))))


def validate_skin_plan_snapshot(plans,names,reference):
    """A resumed calculation keeps the complete selected source declarations."""
    if sha(reference['path'])!=reference['sha256']:raise ValueError('The retained source skin declarations changed.')
    retained=read(reference['path'])['surfaces']
    if any(name not in retained or plans[name]!=retained[name]for name in names):
        raise ValueError('The selected source skin declarations changed; use a new output.')


def prepare_skin_surface_resources():
    """Resolve each art declaration against real source renderers and material slots."""
    import character_resources as resources
    import collect_sources
    import mesh_recipe_codec
    channel_settings={
        'color':dict(pigmentRetain=None,fineSigmaMetres=None,grainSigmaMetres=None,
            coherenceSigmaMetres=None,featherMetres=None,pigmentCoherenceRetain=1.,pigmentFineCoherenceRetain=1.),
        'normal':dict(normalRetain=None,normalRadiusMetres=None,microNormalRetain=None,microNormalRadiusMetres=None,
            macroNormalRetain=1.,macroNormalRadiusMetres=None)}
    parser=argparse.ArgumentParser();parser.add_argument('--source',type=Path,required=True);parser.add_argument('--census',type=Path,required=True)
    parser.add_argument('--plans',type=Path,default=Path(__file__).with_name('face-designs.json'));parser.add_argument('--assets',nargs='*',default=[])
    parser.add_argument('--output',type=Path,required=True);parser.add_argument('--workers',type=int,default=2)
    parser.add_argument('--channels',nargs='+',choices=tuple(channel_settings),default=tuple(channel_settings))
    parser.add_argument('--retained-inputs',type=Path)
    parser.add_argument('--resume',action='store_true')
    args=parser.parse_args();source=checked_catalog_path(args.source);output=checked_catalog_path(args.output)
    channels=sorted(args.channels)
    if len(channels)!=len(set(channels)):raise ValueError('Choose distinct source skin channels.')
    if (output.exists()and not args.resume)or not any(output.is_relative_to(ROOT/p)for p in('tools/asset-authoring/workspace','tools/game-test/artifacts')):
        raise ValueError('Source skin finishing requires a new authoring workspace.')
    if not 1<=args.workers<=16:raise ValueError('Choose one to sixteen independent skin workers.')
    assembly=read(source/'ResourceAssembly.json');manifest=read(source/(Path('Catalog')/RESOURCE_FILES['manifest']))
    if assembly['catalogSha256']!=sha(source/(Path('Catalog')/RESOURCE_FILES['manifest'])):raise ValueError('The source skin catalog changed.')
    census=read(args.census);assets={row['name']:row for row in census['assets']};plans=read(args.plans)['surfaces']
    names=set(args.assets)if args.assets else set(plans)
    if names-set(plans)or names-set(assets):raise ValueError('A skin finish has no declared source owner.')
    proofs={row['file']:row for row in assembly['proofs']};by_cab={row['newCab']:row for row in assembly['proofs']}
    retained_sources={row['sha256']:source/'Catalog'/row['file']for row in manifest['bundles']}
    if args.resume:
        if (output/'ResourceAssembly.json').exists():raise ValueError('The source skin candidate is already complete.')
        paths=sorted((output/'Inputs').glob('*.json'))
        if not paths:raise ValueError('There is no complete source skin job snapshot to resume.')
        declarations=[pin for pin in read(paths[0])['inputs']if Path(pin['path']).name==args.plans.name+'.source']
        if len(declarations)!=1:raise ValueError('The resumed skin calculation has no original declaration owner.')
        validate_skin_plan_snapshot(plans,names,declarations[0])
        tasks=[];expected={row['file']:row['sha256']for row in manifest['bundles']};computation=[]
        live=[Path(__file__),Path(resources.__file__),Path(__file__).with_name('production.py'),Path(__file__).with_name('generate.py'),Path(__file__).with_name('material_fields.py'),Path(__file__).with_name('resource-package.json'),
            Path(collect_sources.__file__),Path(asset_names.__file__),Path(mesh_recipe_codec.__file__)]
        epoch=hashlib.sha256(''.join(sha(path)for path in live).encode()).hexdigest()[:12]
        for path in live:
            archive=output/'ProducerEpochs'/epoch/(path.name+'.source');archive.parent.mkdir(parents=True,exist_ok=True)
            if archive.exists()and sha(archive)!=sha(path):raise ValueError('A retained skin computation epoch changed.')
            if not archive.exists():archive.write_bytes(path.read_bytes())
            computation.append(dict(path=str(archive.resolve()),sha256=sha(archive)))
        for path in paths:
            job=read(path);name=Path(job['source']).name
            if sorted(job.get('channels',channel_settings))!=channels:
                raise ValueError('The resumed skin channel selection changed; use a new output.')
            if job['sha256']!=expected.get(name)or Path(job['source'])!=(source/'Catalog'/name).resolve()or Path(job['target'])!=(output/'Catalog'/name).resolve():
                raise ValueError('A resumed skin job belongs to another catalog.')
            for texture in job['textures']:
                for owner in texture['sourceNames']:
                    settings=channel_settings[texture['channel']]
                    if owner not in names or plans[owner]['domain']!=texture.get('sourceDomains',{}).get(owner,texture['domain'])or any(
                            plans[owner]['profile'].get(key,default)!=texture['profile'].get(key,default)for key,default in settings.items())or (
                            texture['channel']=='color'and plans[owner].get('luminanceRegions',[])!=texture.get('luminanceRegions',[])):raise ValueError('The source skin recipe changed; use a new output.')
            for producer_pin in job['inputs']:
                if Path(producer_pin['path']).suffix=='.source'and sha(producer_pin['path'])!=producer_pin['sha256']:raise ValueError('A skin producer snapshot changed.')
            job['inputPlan']=dict(path=str(path.resolve()),sha256=sha(path));job['computationInputs']=computation;tasks.append(job)
        if args.retained_inputs:assembly.setdefault('retainedSourceInputs',[]).extend(read(args.retained_inputs))
        coverage=[dict(source=name,sourceContentKey=assets[name]['sourceContentKey'],profile=plans[name]['profile'],surfaces=plans[name]['renderers'])for name in sorted(names)]
        complete_source_skin_jobs(source,output,assembly,manifest,tasks,tasks[0]['inputs'],coverage,args.workers,channels=channels);return
    output.mkdir(parents=True);catalog=output/'Catalog';catalog.mkdir();pins=[]
    production,art=source_owner();art.SOURCE=args.census.resolve().parent
    producer_paths=[Path(__file__),Path(resources.__file__),Path(__file__).with_name('material_fields.py'),Path(__file__).with_name('resource-package.json'),args.plans,
        Path(collect_sources.__file__),Path(asset_names.__file__),Path(mesh_recipe_codec.__file__)]
    for module in(production,art):
        if not isinstance(module.__file__,str):raise ValueError('A skin producer has no source file.')
        producer_paths.append(Path(module.__file__))
    for original in producer_paths:
        archive=output/'ProducerSources'/(original.name+'.source');archive.parent.mkdir(exist_ok=True);archive.write_bytes(original.read_bytes())
        pins.append(dict(path=str(archive.resolve()),sha256=sha(archive)))
        assembly.setdefault('retainedSourceInputs',[]).append(dict(original=dict(path=str(original.resolve()),sha256=sha(original)),archive=pins[-1]))
    if args.retained_inputs:assembly.setdefault('retainedSourceInputs',[]).extend(read(args.retained_inputs))
    pins.append(dict(path=str(args.census.resolve()),sha256=sha(args.census)))
    @lru_cache(maxsize=12)
    def load(cab):
        path=source/'Catalog'/by_cab[cab]['file']
        if sha(path)!=by_cab[cab]['sha256']:raise ValueError('A source skin dependency changed.')
        return resources.load_serialized_bundle(path,in_memory=True)[1]
    def resolve(file,pointer):
        file=load(file.name if pointer['m_FileID']==0 else file.externals[pointer['m_FileID']-1].name)
        return file,file.objects[pointer['m_PathID']]
    def pin(file):
        row=by_cab[file.name];return dict(path=str((source/'Catalog'/row['file']).resolve()),sha256=row['sha256'])
    recovered_receipts={};verified_chart_receipts=set()
    def chart_native_packet(mesh_file,mesh,fallback):
        for operation in reversed(by_cab[mesh_file.name].get('appearanceFinishing',[])):
            if operation.get('kind')!='source-native-skin-surface':continue
            reference=operation['proof'];key=(reference['path'],reference['sha256'])
            if key not in recovered_receipts:
                if sha(reference['path'])!=reference['sha256']:raise ValueError('A source chart restoration receipt changed.')
                receipt=read(reference['path']);recovered_receipts[key]=receipt
            receipt=recovered_receipts[key];rows=[row for row in receipt['restoredSurfaces']if row['meshPathId']==mesh.path_id and 'faceRecovery'in row]
            if not rows:continue
            if len(rows)!=1:raise ValueError('A source chart has ambiguous native recovery authority.')
            if key not in verified_chart_receipts:
                validate_native_surface_restoration(receipt);verified_chart_receipts.add(key)
            reference=rows[0]['nativePacket']
            if sha(reference['path'])!=reference['sha256']:raise ValueError('A source chart native packet changed.')
            with gzip.open(reference['path'],'rt',encoding='utf-8')as stream:return json.load(stream),reference
        return fallback,None
    jobs={};coverage=[]
    for name in sorted(names):
        declaration=plans[name];asset=assets[name]
        if declaration['sourceContentKey']!=asset['sourceContentKey']:raise ValueError('A skin recipe belongs to another native source: '+name)
        resource=next(row for row in manifest['resources']if row['asset'].casefold()==asset['internalId'].casefold())
        file=load(proofs[resource['bundles'][0]['file']]['newCab'])
        coverage.append(dict(source=name,sourceContentKey=asset['sourceContentKey'],profile=declaration['profile'],surfaces=declaration['renderers']))
        domain=copy.deepcopy(declaration['domain']);chart_bindings=[];selected_surfaces=[]
        for surface in declaration['renderers']:
            native=next(row for row in asset['skins']if row['path']==surface['path'])
            renderer_id=int(native['sourceProvenance']['rendererPathId']);renderer=file.objects[renderer_id].read_typetree()
            mesh_file,mesh=resolve(file,renderer['m_Mesh']);bones=resources.skin_bone_paths(file,renderer,resolve)
            if 'protectSourceCharts'in domain:
                packet=export_current_mesh(mesh);chart_source=None;source_reference=None;native_reference=None
                if packet['sourceFingerprint']not in domain['protectSourceCharts']:
                    if not isinstance(native.get('meshFile'),str)or not isinstance(native.get('sourceFingerprint'),str):
                        raise ValueError('Source chart protection has no declaration for this input mesh identity or retained native packet authority.')
                    original=production.native_packet(native)
                    coarse,native_reference=chart_native_packet(mesh_file,mesh,original)
                    chart_packet=original if original['sourceFingerprint']in domain['protectSourceCharts']else coarse
                    if chart_packet is original:
                        source_path=art.source_path(native['meshFile']);source_reference=dict(path=str(source_path),sha256=sha(source_path))
                    else:source_reference=native_reference
                    chart_source=dict(source=chart_packet,native=coarse)
                prepared={}
                production.source_material_ownership(packet,bones,domain,renderer_to_prefab=native['rendererToPrefab'],
                    _prepared=prepared,chart_source=chart_source)
                domain=prepared['resolvedDeclaration']
                if prepared['chartRecovery']is not None:
                    chart_bindings.append(dict(renderer=surface['path'],mesh=mesh.path_id,source=source_reference,native=native_reference,
                        correspondence=prepared['chartRecovery']))
            selected_surfaces.append((surface,native,renderer_id,renderer,mesh_file,mesh,bones))
        for surface,native,renderer_id,renderer,mesh_file,mesh,bones in selected_surfaces:
            for slot in surface['slots']:
                material_file,material=resolve(file,renderer['m_Materials'][slot]);mt=material.read_typetree()
                _,shader=resolve(material_file,mt['m_Shader'])
                if shader.read_typetree()['m_ParsedForm']['m_Name']!=surface['shader']:raise ValueError('A skin slot has another shader contract.')
                textures=dict(mt['m_SavedProperties']['m_TexEnvs'])
                for prop,channel in(('_MainTex','color'),('_BumpMap','normal')):
                    if channel not in channels:continue
                    if declaration['profile']['pigmentRetain'if channel=='color'else'normalRetain']==1 and not(
                            channel=='color'and declaration.get('luminanceRegions'))and (
                            channel=='color' or declaration['profile'].get('microNormalRetain',1)==1 and
                            declaration['profile'].get('macroNormalRetain',1)==1):continue
                    texture_file,texture=resolve(material_file,textures[prop]['m_Texture']);key=(by_cab[texture_file.name]['file'],texture.path_id)
                    settings=channel_settings[channel]
                    authority=source_skin_detail_authority(by_cab[texture_file.name],texture.path_id,channel,retained_sources=retained_sources)
                    regions=declaration.get('luminanceRegions',[])if channel=='color'else[]
                    reference=dict(resource=pin(file),renderer=renderer_id,meshResource=pin(mesh_file),mesh=mesh.path_id,
                        materialResource=pin(material_file),material=material.path_id,materialSlot=slot,bones=bones,property=prop,rendererToPrefab=native['rendererToPrefab'],completeMesh=True)
                    entry=dict(object=texture.path_id,channel=channel,profile=declaration['profile'],domain=domain,normalEncoding=surface['normalEncoding'],
                        sourceDomains={name:declaration['domain']},sourceChartBindings=chart_bindings)
                    if regions:entry['luminanceRegions']=regions
                    if authority is not None:
                        entry['detailSource']=authority['source']
                        if authority.get('recovery'):entry['detailSourceRecovery']=authority['recovery']
                    if key in jobs:
                        previous=jobs[key]
                        first=previous['domain'];last=entry['domain']
                        if first!=last and {field:value for field,value in first.items()if field!='protectSourceCharts'}==\
                                {field:value for field,value in last.items()if field!='protectSourceCharts'}and 'protectSourceCharts'in first and 'protectSourceCharts'in last:
                            combined=copy.deepcopy(first['protectSourceCharts'])
                            for fingerprint,anchors in last['protectSourceCharts'].items():
                                if fingerprint in combined and combined[fingerprint]!=anchors:
                                    raise ValueError('A shared source skin texture has conflicting chart anchors for one Mesh identity.')
                                combined[fingerprint]=anchors
                            previous['domain']=entry['domain']=dict(first,protectSourceCharts=combined)
                        if any(previous[field]!=entry[field]for field in('object','channel','domain','normalEncoding'))or previous.get('luminanceRegions',[])!=regions or any(
                                previous['profile'].get(field,default)!=entry['profile'].get(field,default)for field,default in settings.items()):
                            raise ValueError('A shared skin texture has conflicting source declarations: '+str(previous['sourceNames'])+' / '+name+' '+str(key))
                        previous['sourceDomains'].update(entry['sourceDomains'])
                        for binding in chart_bindings:
                            if binding not in previous['sourceChartBindings']:previous['sourceChartBindings'].append(binding)
                    if key not in jobs:jobs[key]=dict(entry,surfaces=[],sourceNames=[])
                    if name not in jobs[key]['sourceNames']:jobs[key]['sourceNames'].append(name)
                    if reference not in jobs[key]['surfaces']:jobs[key]['surfaces'].append(reference)
    groups=collections.defaultdict(list);computation_environment=source_skin_computation_environment()
    for(name,identifier),job in jobs.items():
        authority=source_skin_detail_authority(proofs[name],identifier,job['channel'],retained_sources=retained_sources)
        settings=channel_settings[job['channel']]
        if authority is not None and authority.get('domain')==job['domain']and authority.get('luminanceRegions',[])==job.get('luminanceRegions',[])and all(
                authority['profile'].get(field,default)==job['profile'].get(field,default)for field,default in settings.items()):
            file=load(proofs[name]['newCab'])
            authority=source_skin_detail_authority(proofs[name],identifier,job['channel'],retained_sources=retained_sources,
                requested=dict(inputs=pins,surfaces=job['surfaces'],texture=file.objects[identifier],normalEncoding=job['normalEncoding'],environment=computation_environment))
            if authority['reusable']:continue
        groups[name].append(job)
    for row in manifest['bundles']:
        if row['file']not in groups:(catalog/row['file']).hardlink_to(source/'Catalog'/row['file'])
    tasks=[]
    for name,textures in sorted(groups.items()):
        job=dict(source=str((source/'Catalog'/name).resolve()),sha256=proofs[name]['sha256'],target=str((catalog/name).resolve()),
            receipt=str((output/'Receipts'/(name+'.json')).resolve()),textures=textures,inputs=pins,channels=channels)
        path=output/'Inputs'/(name+'.json');write(path,job);job['inputPlan']=dict(path=str(path.resolve()),sha256=sha(path));tasks.append(job)
    complete_source_skin_jobs(source,output,assembly,manifest,tasks,pins,coverage,args.workers,channels=channels)


def source_face_contour(plans,fixture):
    """Individual source declarations and cohort variants use one sculpt contract."""
    name=fixture['name'];individual=plans.get('contours',{}).get(name)
    if individual is not None:
        renderer=individual.get('renderer')or plans['surfaces'][name]['contourRenderer']
        return renderer,dict(zip(plans['columns'],individual['values'],strict=True)),('source',name)
    cohort='/'.join(fixture[k]for k in ('race','sex','origin'))
    suffix=fixture['faceDefinition'].removeprefix('FaceShape_')
    if not suffix.startswith('NPC_'):suffix=suffix.rsplit('_',1)[-1]
    values=plans['cohorts'].get(cohort,{}).get(suffix)
    if values is None:return None
    return plans['surfaces'][name]['contourRenderer'],dict(zip(plans['columns'],values,strict=True)),(cohort,suffix)


def compile_face_designs():
    """Compile the source matrix in measured native neutral coordinates."""
    import numpy as np
    parser=argparse.ArgumentParser()
    parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--census',type=Path,required=True)
    parser.add_argument('--reference-index',type=Path,required=True)
    parser.add_argument('--fixtures',type=Path,required=True)
    parser.add_argument('--plans',type=Path,default=Path(__file__).with_name('face-designs.json'))
    parser.add_argument('--assets',nargs='*',default=[])
    args=parser.parse_args();output=args.output.resolve()
    if not output.is_relative_to(ROOT/'tools')or(output/'Preparation.json').exists():
        raise ValueError('An independent tools workspace is required for face designs.')
    cp=args.census.resolve()
    census=read(cp);production,art=source_owner();setattr(art,'SOURCE',cp.parent)
    sources=(production,art,census);assets={a['name']:a for a in census['assets']}
    fixture_rows=read(args.fixtures);fixtures={a['name']:a for a in fixture_rows}
    poses=read(args.reference_index)['assets']
    plans=read(args.plans);completed=[];failures=[];separate=[];profiles=[];used=set()
    producer_paths=[Path(__file__)]
    for module_path in (production.__file__,art.__file__):
        if not isinstance(module_path,str):raise ValueError('The facial producer has no source file.')
        producer_paths.append(Path(module_path))
    pins=[]
    for original in producer_paths:
        target=output/'ProducerSources'/(original.name+'.source');target.parent.mkdir(parents=True,exist_ok=True)
        if target.exists()and target.read_bytes()!=original.read_bytes():raise ValueError('An immutable individual face producer changed.')
        target.write_bytes(original.read_bytes());pins.append(dict(path=str(target),sha256=sha(target),original=str(original.resolve())))
    requested=set(args.assets)
    available={row['name'] for row in poses}
    if requested-available:raise ValueError('Unknown source design requests: '+str(sorted(requested-available)))
    declared={(cohort,face)for cohort,faces in plans['cohorts'].items()for face in faces}
    declared|={('source',name)for name in plans.get('contours',{})}
    defaults=dict(plans,contours={});reachable=set();expected={}
    for fixture in fixture_rows:
        if fixture['name']not in plans['surfaces']:continue
        for candidate in(plans,defaults):
            contour=source_face_contour(candidate,fixture)
            if contour is not None:reachable.add(contour[2])
        contour=source_face_contour(plans,fixture)
        if contour is not None:expected[fixture['name']]=contour[2]
    if not requested:
        if declared-reachable:raise ValueError('Individual plans have no source consumer: '+str(sorted(declared-reachable)))
        if expected.keys()-available:raise ValueError('A declared source has no neutral reference: '+str(sorted(expected.keys()-available)))
    for capture in poses:
        if requested and capture['name'] not in requested:continue
        name=capture['name'];asset=assets[name];fixture=fixtures[name]
        cohort='/'.join(fixture[k]for k in ('race','sex','origin'))
        contour=source_face_contour(plans,fixture)
        if contour is None:
            separate.append(dict(name=name,reason='Source retained without a contour declaration.'));continue
        primary,plan,identity=contour;used.add(identity)
        skins=[skin for skin in asset['skins']if skin['path']==primary]
        if len(skins)!=1:raise ValueError('A contour declaration has no unique native renderer: '+name)
        skin=skins[0];packet=production.native_packet(skin)
        reference=output/'References'/(name+'.json')
        write(reference,capture)
        pose=capture['neutralPose'];neutral=production.apply_native_reference_pose(packet,skin,pose)
        points=art.points(neutral['vertices'],art.CONVERT);normals=art.native_normals(neutral,art.CONVERT)
        bones={bone.rsplit('/',1)[-1].lower():art.points(art.matrix(value)[:3,3].tolist(),art.CONVERT)[0]
               for bone,value in pose['worldMatrices'].items()}
        left,right=bones['b_eye_l'],bones['b_eye_r'];eye=(left+right)*.5;gap=float(np.linalg.norm(left-right))
        if not(left[0]<right[0]and .025<gap<.10):raise ValueError('A face plan has no declared native left/right eye frame.')
        mouth=(bones['b_lip_upper_c']+bones['b_lip_lower_c'])*.5
        nose=(bones['b_nostril_l']+bones['b_nostril_r'])*.5
        selected=np.zeros(len(points),bool)
        for slot in production.facial_material_slots(packet,skin):selected[np.asarray(packet['subMeshes'][slot],int)]=True
        scale=gap/.06;handles=[];anchors=[]
        declared_handles=plans.get('contours',{}).get(name,{}).get('handles',[])
        if not isinstance(declared_handles,list)or any(not isinstance(row,dict)or not isinstance(row.get('name'),str)or not row['name']for row in declared_handles):
            raise ValueError('A source contour requires named surface handles.')
        overrides={row['name']:row for row in declared_handles}
        if len(overrides)!=len(declared_handles):raise ValueError('A source contour repeats a surface handle.')
        horizontal=(right-left)/gap
        vertical=eye-mouth;vertical-=horizontal*np.dot(vertical,horizontal);vertical/=np.linalg.norm(vertical)
        forward=np.cross(horizontal,vertical)
        frame=np.column_stack((horizontal,forward,vertical))
        # Eye alignment gives the lateral axis. Keep the prefab's up axis as
        # the reference for lift, since protruding lips also tilt eye-to-mouth.
        up=np.array([0.,0.,1.])
        up-=horizontal*np.dot(up,horizontal)
        up/=np.linalg.norm(up)
        design_frame=np.column_stack((horizontal,np.cross(up,horizontal),up))
        def handle(label,at,move,radius,maximum=.022,normal=(0.,-1.,0.)):
            handles.append(copy.deepcopy(overrides.pop(label))if label in overrides else
                dict(name=label,at=np.asarray(at).tolist(),moveMm=(design_frame@move).tolist(),radiusMetres=radius*scale,
                    maximumAnchorDistanceMetres=maximum*scale,surfaceNormal=list(normal)))
            return handles[-1]
        try:
            cheek_indices,cheek_measurement=production.anatomy_surface_pair(points,normals,selected.astype(float),
                [bones['b_cheek_upper_'+suffix]for suffix in('l','r')],eye,frame,gap)
            for (sign,suffix),index in zip(((-1,'l'),(1,'r')),cheek_indices):
                actual=handle('UpperCheek_'+suffix,points[index],[-sign*plan['zygomaIn'],plan['zygomaBack'],.1],.030,.001,normals[index])
                anchored=production.anatomy_surface_anchor(points,normals,selected.astype(float),np.asarray(actual['at']),selected,
                    outward_direction=actual.get('surfaceNormal',(0.,-1.,0.)))
                anchors.append(dict(region='UpperCheek_'+suffix,vertex=int(anchored),point=points[anchored].tolist()))
                handle('LowerCheek_'+suffix,bones['b_cheek_lower_'+suffix],
                    [sign*plan['lowerCheekOut'],-.1-plan.get('lowerCheekForward',0),.2],.030,.015)
                if plan.get('midCheekForward',0):
                    handle('MidCheek_'+suffix,(bones['b_cheek_upper_'+suffix]+bones['b_cheek_lower_'+suffix])*.5,
                        [0.,-plan['midCheekForward'],.1],.028,.025)
                handle('JawAngle_'+suffix,mouth+[sign*gap*1.05,gap*1.2,-.010*scale],
                    [-sign*plan['jawIn'],0.,.2],.045,normal=(sign,0.,0.))
                handle('NasalWing_'+suffix,bones['b_nostril_'+suffix],[-sign*plan['nasalWingIn'],.1,.1],.018,.012)
                handle('BrowPlane_'+suffix,bones['b_eyebrow_'+suffix+'_01'],[0.,plan['browBack'],.1+plan.get('browLift',0)],.020,.014)
                handle('LipCorner_'+suffix,bones['b_lip_corner_'+suffix],[0.,plan['lipBack'],.1],.021,.012)
            handle('Chin',mouth+[0.,.013*scale,-.036*scale],[0.,.2-plan.get('chinForward',0),plan['chinUp']],.030)
            handle('LipSealCentre',mouth,[0.,plan['lipBack'],.1],.025,.012)
            nasal=selected&(np.abs(points[:,0]-nose[0])<gap*.22)
            nasal&=(points[:,2]>nose[2]-.005*scale)&(points[:,2]<nose[2]+.028*scale)&(normals[:,1]<-.2)
            candidates=np.flatnonzero(nasal)
            if not len(candidates):raise ValueError('The explicitly owned nasal surface is absent.')
            tip=int(candidates[np.argmin(points[candidates,1])])
            handle('NasalTip',points[tip],[0.,plan['nasalTipBack'],.1],.020,.001,normals[tip])
            handles.extend(copy.deepcopy(list(overrides.values())))
            domain=capture.get('coordinateDomain','native-neutral-prefab-dcc')
            if domain not in ('native-neutral-prefab-dcc','source-prefab-reference-dcc'):raise ValueError('A facial reference has another declared coordinate domain.')
            design=dict(asset=name,rendererPath=skin['path'],coordinateDomain=domain,
                collarProtectionZ=[float(mouth[2]-.070*scale),float(mouth[2]-.045*scale)],handles=handles,
                followContactComponents=True,contactUnits=copy.deepcopy(plans.get('contactUnits',
                    [dict(handle='LipSealCentre',bonePrefixes=['b_lip_'])])),
                surfaceSections=copy.deepcopy(plans.get('surfaceSections',{}).get(name,[])),
                referencePose=dict(path=str(reference.resolve()),sha256=sha(reference)))
            design_path=output/(name+'-Design.json');target_path=output/(name+'-Profiles.json');write(design_path,design)
            document=compile_source_face_design(argparse.Namespace(design=design_path,output=target_path,census=cp),sources)
            profiles.extend(document['profiles']);completed.append(dict(name=name,fixture=fixture['id'],cohort=cohort,plan=plan,
                sourceSurfaceAnchors=anchors,pairedCheekMeasurement=cheek_measurement,
                design=dict(path=str(design_path),sha256=sha(design_path)),
                profiles=dict(path=str(target_path),sha256=sha(target_path))))
            print('Compiled individual neutral source:',name,flush=True)
        except (ValueError,np.linalg.LinAlgError)as error:
            failures.append(dict(name=name,error=str(error)));print('REJECTED individual source:',name,str(error),flush=True)
    if not requested and set(expected.values())!=used:
        raise ValueError('Individual plans and the complete source scope differ: '+str(set(expected.values())^used))
    for pin in pins:
        if sha(pin['original'])!=pin['sha256']:raise ValueError('An individual source producer changed during compilation.')
    write(output/'Profiles.json',dict(schemaVersion=1,profiles=profiles))
    write(output/'Preparation.json',dict(status='SOURCE_SPECIFIC_GUIDES_COMPILED_REVIEW_PENDING'if not failures else'SOURCE_SPECIFIC_GUIDES_HAVE_REJECTED_TARGETS',
        rows=completed,failures=failures,separateAnatomy=separate,profiles=len(profiles),producers=pins,artAccepted=False,
        plans=dict(path=str(args.plans.resolve()),sha256=sha(args.plans)),referenceIndex=dict(path=str(args.reference_index.resolve()),sha256=sha(args.reference_index)),fixtures=dict(path=str(args.fixtures.resolve()),sha256=sha(args.fixtures)),sourceCensus=dict(path=str(cp),sha256=sha(cp))))
    if failures:raise SystemExit(1)


def compile_source_face_design(args, sources=None) -> dict[str, Any]:
    import numpy as np
    p, art, census = sources if sources is not None else (*source_owner(), read(args.census))
    setattr(art,'SOURCE',args.census.resolve().parent)
    design = read(args.design)
    asset = next(a for a in census['assets'] if a['name'] == design['asset'])
    skin = next(s for s in asset['skins'] if s['path'] == design['rendererPath'])
    packet = p.native_packet(skin)
    transform = art.CONVERT @ art.matrix(skin['rendererToPrefab'])
    domain = design.get('coordinateDomain', 'native-rest-prefab-dcc')
    if domain not in ('native-rest-prefab-dcc', 'native-neutral-prefab-dcc', 'source-prefab-reference-dcc'):
        raise ValueError('Unknown authored head coordinate domain.')
    pose = None
    if domain == 'native-neutral-prefab-dcc':
        reference = design['referencePose']
        reference_path = (args.design.parent / reference['path']).resolve()
        if sha(reference_path) != reference['sha256']:
            raise ValueError('The measured neutral reference changed.')
        measured = read(reference_path)
        if measured['sourceContentKey'] != asset['sourceContentKey'] or measured['internalId'] != asset['internalId']:
            raise ValueError('The measured neutral belongs to another sculpt source.')
        pose = measured['neutralPose']
    elif domain == 'source-prefab-reference-dcc':
        worlds = {}
        for source in asset['skins']:
            for bone, value in zip(source['bones'], np.asarray(source['bonesToPrefab']).reshape(-1, 16)):
                if bone is None:
                    continue
                if bone in worlds and not np.allclose(worlds[bone], value, rtol=0, atol=2e-6):
                    raise ValueError('Conflicting native neutral bone ownership.')
                worlds[bone] = value.tolist()
        pose = dict(worldMatrices=worlds, currentMorphBySource=[], allNativeMorphWeightsZero=True)
    if pose is not None:
        neutral_packet = p.apply_native_reference_pose(packet, skin, pose)
        positions = art.points(neutral_packet['vertices'], art.CONVERT)
        normals = art.native_normals(neutral_packet, art.CONVERT)
    else:
        positions = art.points(packet['vertices'], transform)
        normals = art.native_normals(packet, transform)
    selected = np.zeros(len(positions), dtype=bool)
    slots = p.facial_material_slots(packet, skin)
    for slot in slots:
        selected[np.asarray(packet['subMeshes'][slot], dtype=int)] = True
    controls = []
    reports = []
    handle_names=set()
    for handle in design['handles']:
        required={'name','at','moveMm','radiusMetres','maximumAnchorDistanceMetres'}
        p.require(isinstance(handle,dict)and required<=set(handle)<=required|{'surfaceNormal'}and
            isinstance(handle['name'],str)and handle['name']and handle['name']not in handle_names,
            'A source design requires unique named surface handles.')
        vectors=[np.asarray(handle[key],dtype=float)for key in('at','moveMm')]
        p.require(all(value.shape==(3,)and np.isfinite(value).all()for value in vectors)and
            all(type(handle[key])in(int,float)and np.isfinite(handle[key])and handle[key]>0
                for key in('radiusMetres','maximumAnchorDistanceMetres')),
            'A source surface handle requires finite coordinates, movement and positive physical support.')
        handle_names.add(handle['name'])
        index = p.anatomy_surface_anchor(positions, normals, selected.astype(float), np.asarray(handle['at']), selected,
            outward_direction=handle.get('surfaceNormal',(0.,-1.,0.)))
        distance = float(np.linalg.norm(positions[index] - handle['at']))
        if distance > handle['maximumAnchorDistanceMetres']:
            raise ValueError('A design handle misses its explicitly owned native surface: ' + handle['name'])
        controls.append((positions[index], positions[index] + np.asarray(handle['moveMm']) / 1000, handle['radiusMetres']))
        reports.append(dict(name=handle['name'], sourceVertex=index, source=positions[index].tolist(),
                            requestedOffsetMm=handle['moveMm'], distanceMetres=distance))
    contact_guard = p.humanoid_face_contact_guard(packet, skin, contact_bone_prefixes=('b_eye_', 'b_eyelid_'))
    protected = contact_guard == 0
    lower, upper = design['collarProtectionZ']
    ownership = art.smooth((positions[:, 2] - lower) / (upper - lower)) * contact_guard
    ownership[protected | ~selected] = 0
    desired_rest = p.interpolate_source_handles(packet,skin,pose,positions,controls,reports,ownership,transform,
        design.get('contactUnits',()),design.get('surfaceSections',()))
    rest = art.points(packet['vertices'],transform)
    desired_rest,topology_repair=p.retain_local_surface_areas(packet,rest,desired_rest,ownership)
    active = np.flatnonzero(np.linalg.norm(desired_rest-rest,axis=1)>1e-9)
    targets = desired_rest[active]
    desired_local = art.points(desired_rest.ravel().tolist(),np.linalg.inv(transform))
    desired_packet = dict(packet,vertices=desired_local.ravel().tolist())
    desired = (art.points(p.apply_native_reference_pose(desired_packet,skin,pose)['vertices'],art.CONVERT)
               if pose is not None else desired_rest)
    profile, metrics = p.prepare_source_surface_guide(asset, skin, packet, targets, active, slots)
    document: dict[str, Any] = dict(schemaVersion=1, profiles=[profile])
    # LOD meshes have different edges. Transfer the authored shape, then keep
    # each low-detail target's own tissue valid and encode it through the same
    # source-guide owner. Never scale the complete head to repair one sliver.
    write(args.output,document)
    initial=p.bind_source_lod_families(p.load_profiles(dict(census,assets=[asset]),args.output)['assets'][0])
    primary=next(s for s in initial['skins'] if s['path']==skin['path'])
    compiled=art.compile_guide(primary['_faceProfile'],transform)
    donor=(rest,desired_rest-rest,np.ones(len(rest)),compiled)
    lod_guides=[]
    guide_owners={0:(packet,skin,slots)}
    for low_skin in initial['skins']:
        lod=p.lod_index(low_skin)
        if lod==0 or low_skin.get('_faceLodDonor')!=packet['sourceFingerprint']:
            continue
        low_packet=p.native_packet(low_skin)
        low_transform=art.CONVERT@art.matrix(low_skin['rendererToPrefab'])
        low_original=art.points(low_packet['vertices'],low_transform)
        low_target,low_mask=p.lower_detail_surface(low_packet,low_skin,donor)
        low_slots=p.facial_material_slots(low_packet,low_skin)
        low_selected=np.zeros(len(low_original),dtype=bool)
        for slot in low_slots:low_selected[np.asarray(low_packet['subMeshes'][slot],dtype=int)]=True
        low_mask*=low_selected
        low_target[~low_selected]=low_original[~low_selected]
        low_target,repair=p.retain_local_surface_areas(low_packet,low_original,low_target,low_mask)
        low_active=np.flatnonzero(np.linalg.norm(low_target-low_original,axis=1)>1e-9)
        if not len(low_active):
            raise ValueError('An authored lower-detail head has no supported target.')
        low_profile,low_metrics=p.prepare_source_surface_guide(asset,low_skin,low_packet,low_target[low_active],low_active,low_slots)
        document['profiles'].append(low_profile)
        guide_owners[lod]=(low_packet,low_skin,low_slots)
        lod_guides.append(dict(renderer=low_skin['path'],metrics=low_metrics,thinTissueCorrection=repair))
    attachments = []
    if design.get('followContactComponents', False):
        for companion in asset['skins']:
            label = (companion['path']+'/'+companion['mesh']).lower()
            # Ocular tissue is fixed by this guide's contact guard. Its lashes,
            # eyes and occluders share that constraint, rather than following
            # normals of a nearby sculpted cheek/brow surface.
            if not any(token in label for token in ('jaw', 'teeth', 'tongue')):
                continue
            if any(material and ('/Skin' in material['shader'] or material['shader']=='Custom/CharacterEye')
                   for material in companion['materials']):
                continue
            source_packet = p.native_packet(companion)
            contact_packet,contact_skin,contact_slots=guide_owners.get(p.lod_index(companion),guide_owners[0])
            anchors, proof = p.prepare_source_triangle_anchors(source_packet, companion, contact_packet, contact_skin, contact_slots)
            if proof['largestNativeAttachmentOffsetMetres'] > .05:
                raise ValueError('A declared contact component is outside the native head surface.')
            regions = []
            covered = np.zeros(source_packet['vertexCount'], dtype=bool)
            for slot, indices in enumerate(source_packet['subMeshes']):
                if not indices:
                    continue
                owned = np.zeros(source_packet['vertexCount'])
                owned[np.asarray(indices, dtype=int)] = 1
                covered |= owned > 0
                regions.append(dict(role='attached-surface', materialSlot=slot, vertexWeights=owned.tolist()))
            if not np.all(covered):
                raise ValueError('A contact component has unowned source vertices.')
            document['profiles'].append(dict(id=profile['id']+'_'+companion['path'].replace('/', '_'),
                source=dict(assetSourceContentKey=asset['sourceContentKey'], rendererPath=companion['path'],
                    nativeFingerprint=source_packet['sourceFingerprint'], vertexCount=source_packet['vertexCount'],
                    bonePathsOrdered=companion['bones'], rendererToPrefab=companion['rendererToPrefab']),
                regions=regions, points={}, operation='source-guide-follow',
                parameters=dict(anchors=anchors), donorSourceFingerprint=contact_packet['sourceFingerprint']))
            attachments.append(dict(renderer=companion['path'], **proof))
    write(args.output, document)
    bound = p.load_profiles(dict(census, assets=[asset]), args.output)['assets'][0]
    rows = p.refine_source_asset(p.bind_source_lod_families(bound))
    primary = next(r for r in rows if r['skin']['path'] == skin['path'])
    actual = (art.points(p.apply_native_reference_pose(primary['authored'], skin, pose)['vertices'], art.CONVERT)
              if pose is not None else art.points(primary['authored']['vertices'], transform))
    residual = np.linalg.norm(actual[active] - desired[active], axis=1)
    if float(residual.max()) > .00001:
        raise ValueError('The common facial producer did not retain the artist target.')
    write(args.output.with_name(args.output.stem + '-Receipt.json'), dict(
        status='SOURCE_GUIDE_EVALUATED_NOT_VISUALLY_ACCEPTED', asset=asset['name'],
        sourceFingerprint=packet['sourceFingerprint'], designSha256=hashlib.sha256(args.design.read_bytes()).hexdigest(),
        profileSha256=sha(args.output), handles=reports, metrics=metrics,
        coordinateDomain=domain,
        sourceHandleInterpolation='positive harmonic surface displacement; zero outside owned support and contacts',
        thinTissueCorrection=topology_repair,
        lowDetailTargets=lod_guides,
        contactComponents=attachments,
        referencePoseSha256=(hashlib.sha256(json.dumps(pose, sort_keys=True, separators=(',', ':')).encode()).hexdigest()
                            if pose is not None else None),
        maximumCommonProducerResidualMm=float(residual.max()) * 1000,
        refinedMeshes=len(rows), preservedEyeContactVertices=int(protected.sum())))
    print(json.dumps(dict(asset=asset['name'], editedVertices=len(active), meshes=len(rows), maximumResidualMm=float(residual.max()) * 1000)), flush=True)
    return document



def prepare_native_face_resources():
    parser=argparse.ArgumentParser()
    parser.add_argument('--source',type=Path,required=True)
    parser.add_argument('--native-index',type=Path,required=True)
    parser.add_argument('--census',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--assets',nargs='*',default=[])
    mode=parser.add_mutually_exclusive_group(required=True)
    mode.add_argument('--profiles',type=Path)
    mode.add_argument('--restore-native-only',action='store_true')
    parser.add_argument('--previous-profiles',type=Path)
    args=parser.parse_args()
    if args.previous_profiles and args.profiles is None:raise ValueError('A guide revision requires next and previous profiles.')
    geometry=prepare_resource_faces(args.source,args.native_index,args.output,args.census,selected_assets=args.assets,
        profiles_path=args.profiles,restore_native_only=args.restore_native_only,previous_profiles_path=args.previous_profiles)
    if args.previous_profiles is None:finish_resource_pigments(geometry,args.output)
    assembly=geometry['assembly'];manifest=geometry['manifest']
    write(args.output/(Path('Catalog')/RESOURCE_FILES['manifest']),manifest)
    assembly['catalogSha256']=sha(args.output/(Path('Catalog')/RESOURCE_FILES['manifest']))
    assembly['bytes']=sum(row['bytes']for row in assembly['proofs'])
    assembly['nativeFaceContours']=dict(owner=geometry['owner'],coverage=geometry['coverage'],rows=geometry['rows'],
        humanStyleFiles=geometry['humanStyleFiles'],review='pending')
    assembly['preparedSource']=dict(assembly=dict(path=str((args.source/'ResourceAssembly.json').resolve()),sha256=sha(args.source/'ResourceAssembly.json')),
        catalog=dict(path=str((args.source/(Path('Catalog')/RESOURCE_FILES['manifest'])).resolve()),sha256=sha(args.source/(Path('Catalog')/RESOURCE_FILES['manifest']))))
    write(args.output/'ResourceAssembly.json',assembly)
    print(json.dumps(dict(status='PREPARED_NATIVE_RECOVERY_REVIEW_PENDING'if args.restore_native_only else'PREPARED_SOURCE_GUIDES_REVIEW_PENDING',meshes=geometry['meshes'],catalog=assembly['catalogSha256'])))


def save_compiled_groom_shader(source,catalog,proofs,shader_name,shader_path,shader,validation_path,pins):
    """Save the same validated shared shader transaction for every groom workflow."""
    import character_resources as resources
    shader_before=sha(source/'Catalog'/shader_name)
    if sha(catalog/shader_name)!=shader_before:raise ValueError('The shared shader target differs from its declared immutable source.')
    env,file=resources.load_serialized_bundle(catalog/shader_name,in_memory=True)
    _,compiled=resources.load_serialized_bundle(shader_path,in_memory=True)
    shared_cab=file.name;shared_externals=[item.path for item in file.externals]
    old=[o for o in file.objects.values()if o.type.name=='Shader'and o.read_typetree()['m_ParsedForm']['m_Name']=='KCN/SourceGroom/NativeHair']
    new=[o for o in compiled.objects.values()if o.type.name=='Shader'and o.read_typetree()['m_ParsedForm']['m_Name']=='KCN/SourceGroom/NativeHair']
    if len(old)!=1 or len(new)!=1:raise ValueError('The groom shader has no unique compiled program owner.')
    tree=new[0].read_typetree()
    if tree['m_Dependencies']or tree['m_NonModifiableTextures']:raise ValueError('The shared groom shader introduced external dependencies.')
    untouched={key:hashlib.sha256(resources.serialized_object_bytes(obj)).hexdigest()for key,obj in file.objects.items()if key!=old[0].path_id}
    old[0].save_typetree(new[0].read_typetree());expected=old[0].data
    temporary=catalog/(shader_name+'.writing');temporary.write_bytes(env.file.save(packer='original'))
    _,saved=resources.load_serialized_bundle(temporary,in_memory=True)
    if saved.name!=shared_cab or [item.path for item in saved.externals]!=shared_externals:
        raise ValueError('The shared groom shader reference identity changed.')
    for key,obj in saved.objects.items():
        actual=resources.serialized_object_bytes(obj)
        if key==old[0].path_id:
            if actual!=expected:raise ValueError('The compiled groom shader failed saved reload.')
        elif hashlib.sha256(actual).hexdigest()!=untouched[key]:raise ValueError('An unrelated shared shader object changed.')
    temporary.replace(catalog/shader_name)
    proofs[shader_name]['sha256']=sha(catalog/shader_name);proofs[shader_name]['bytes']=(catalog/shader_name).stat().st_size
    proofs[shader_name]['allRetainedObjectFieldsExactAfterReferenceRebasing']=False
    shader_proof=dict(kind='compiled-shader-only',beforeSha256=shader_before,sha256=proofs[shader_name]['sha256'],authority=dict(inputs=pins),
        source=dict(path=str((source/'Catalog'/shader_name).resolve()),sha256=shader_before),
        target=dict(path=str((catalog/shader_name).resolve()),sha256=proofs[shader_name]['sha256']),
        compiledSource=dict(path=str(shader_path.resolve()),sha256=shader['bundleSha256']),
        validation=dict(path=str(validation_path.resolve()),sha256=sha(validation_path)),
        changedObject=old[0].path_id,allOtherObjectBytesExact=True,savedObjectBytesExact=True)
    proofs[shader_name].setdefault('appearanceFinishing',[]).append(shader_proof)
    validate_compiled_shader_proof(shader_proof,read(validation_path))
    return shader_proof


def attachment_catalog_families(prefabs,discovery):
    """Pair the native attachment and support categories on the same rig family."""
    families=collections.defaultdict(lambda:dict(attachments=[],supports=[]))
    roles={discovery['attachmentCategory'].casefold():'attachments',discovery['supportCategory'].casefold():'supports'}
    for name,rows in prefabs.items():
        for row in rows:
            family,separator,part=row['asset'].rpartition('/Graphics/')
            if not separator:continue
            category=part.split('/',1)[0].casefold()
            role=next((value for key,value in roles.items()if category==key or category.startswith(key+'_')),None)
            if role:families[family][role].append(name)
    return {family:row for family,row in families.items()if row['attachments']}


def prepare_attachment_inputs():
    """Prepare rigid source attachments against every declared support surface."""
    import numpy as np
    import character_resources as resources
    from mesh_recipe_codec import encode_recipe
    parser=argparse.ArgumentParser();parser.add_argument('--source',type=Path,required=True);parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--designs',type=Path,default=Path(__file__).with_name('face-designs.json'));parser.add_argument('--assets',nargs='*',default=[])
    parser.add_argument('--native-index',type=Path)
    args=parser.parse_args();source=checked_catalog_path(args.source);output=checked_catalog_path(args.output)
    if output.exists()or not any(output.is_relative_to(ROOT/p)for p in('tools/asset-authoring/workspace','tools/game-test/artifacts')):
        raise ValueError('Attachment authoring requires a new independent workspace.')
    manifest=read(source/(Path('Catalog')/RESOURCE_FILES['manifest']));assembly=read(source/'ResourceAssembly.json')
    if assembly['catalogSha256']!=sha(source/(Path('Catalog')/RESOURCE_FILES['manifest'])):raise ValueError('The attachment source catalog changed.')
    designs=read(args.designs)['attachments']
    output.mkdir(parents=True);pin=lambda path:dict(path=str(Path(path).resolve()),sha256=sha(path));inputs=[];retained=[]
    for path in map(Path,(__file__,Path(__file__).with_name('production.py'),Path(__file__).with_name('generate.py'),
            Path(__file__).with_name('material_fields.py'),Path(__file__).with_name('resource-package.json'),resources.__file__,Path(resources.__file__).with_name('mesh_recipe_codec.py'),args.designs)):
        archived=output/'ProducerSources'/(path.name+'.source');archived.parent.mkdir(exist_ok=True);archived.write_bytes(path.read_bytes())
        inputs.append(pin(archived));retained.append(dict(original=pin(path),archive=pin(archived)))
    inputs.extend(pin(path)for path in(source/'ResourceAssembly.json',source/(Path('Catalog')/RESOURCE_FILES['manifest'])))
    native_index={}
    if args.native_index:
        inputs.append(pin(checked_source_path(args.native_index)))
        native_index={row['file']:row for row in read(args.native_index)['bundles']}
    proofs={row['file']:row for row in assembly['proofs']};by_cab={row['newCab']:row for row in assembly['proofs']};source_pins={}
    @lru_cache(maxsize=12)
    def load(cab):
        proof=by_cab[cab];path=source/'Catalog'/proof['file']
        if sha(path)!=proof['sha256']:raise ValueError('An attachment or support resource changed.')
        source_pins[str(path.resolve())]=pin(path);return resources.load_serialized_bundle(path,in_memory=True)[1]
    prefabs={}
    for resource in manifest['resources']:
        if resource['type']=='GameObject':prefabs.setdefault(resource['source'].split('/')[0],[]).append(resource)
    def prefab(name):
        rows=prefabs.get(name,[])
        if len(rows)!=1:raise ValueError('An attachment declaration needs one actual prefab: '+name)
        return rows[0]
    declarations=dict(designs.get('sources',{}));support_names=dict(designs.get('supports',{}))
    discovery=designs.get('discovery')
    if discovery:
        if not native_index:raise ValueError('Discovered attachments require their untouched native resource index.')
        for family,categories in attachment_catalog_families(prefabs,discovery).items():
            support_names[family]=sorted(categories['supports'])
            for name in categories['attachments']:
                declarations[name]=dict(declarations.get(name,{}),support=family,profile=discovery['profile'])
    names=set(args.assets)if args.assets else set(declarations)
    if not names or names-set(declarations):raise ValueError('Select declared native attachment sources.')

    support_cache={};rows=[];recipes=[];material_updates={};production,_=source_owner()
    for name in sorted(names):
        declaration=declarations[name];profile=dict(designs['profiles'][declaration['profile']])
        renderer_name=None if discovery else profile.get('supportRenderer')
        support_key=(declaration['support'],profile['supportBone'],renderer_name)
        if support_key not in support_cache:
            witnesses=[];supports=[];support_shaders=set()
            for support_name in support_names[declaration['support']]:
                resource=prefab(support_name);surfaces=resource_skin_surfaces(source,manifest,proofs,resource['source'],load)
                if renderer_name:
                    matches=[row for row in surfaces if row['path']==renderer_name]
                else:
                    matches=[row for row in surfaces if row['path'].endswith('LOD0') and
                             any(bone.rsplit('/',1)[-1].casefold().startswith('b_eyelid_')for bone in row['bones'])]
                if len(matches)!=1:raise ValueError('A support declaration needs one real skin slot: '+support_name)
                surface=matches[0];packet=surface['packet'];slots=[i for i,bone in enumerate(surface['bones'])if bone.split('/')[-1]==profile['supportBone']]
                support_shaders.add(surface['shader'])
                if len(slots)!=1:raise ValueError('An attachment support has no unique declared native bone.')
                frame=np.asarray(packet['bindPoses']).reshape(-1,4,4)[slots[0]].T
                vertices=np.asarray(packet['vertices']).reshape(-1,3)@frame[:3,:3].T+frame[:3,3]
                normals=np.asarray(packet['normals']).reshape(-1,3)@np.linalg.inv(frame[:3,:3]);normals=production.normalise(normals)
                witness=dict(name=support_name,vertices=vertices.tolist(),normals=normals.tolist(),triangles=packet['subMeshes'][surface['slot']],
                    source=pin(source/'Catalog'/by_cab[surface['meshCab']]['file']),mesh=surface['mesh'],slot=surface['slot'],fingerprint=packet['sourceFingerprint'],frame=frame.ravel().tolist())
                path=output/'Supports'/(support_name+'.json');write(path,witness);witnesses.append(pin(path))
                supports.append(witness)
            if not witnesses or len(support_shaders)!=1:raise ValueError('An attachment requires supports with one coherent skin contract.')
            support_cache[support_key]=witnesses,supports,next(iter(support_shaders))
        witnesses,supports,support_shader=support_cache[support_key]
        if discovery:profile.update(discovery.get('supportShaderOverrides',{}).get(support_shader,{}))
        if 'sourceLods'in declaration:profile['sourceLods']=declaration['sourceLods']
        resource=prefab(name);renderers,resolve=resource_prefab_skins(manifest,proofs,resource['source'],load)
        folder=output/name;folder.mkdir();meshes=[];levels=[];lod_sources={}
        basis_source=None;native_file=None
        if native_index:
            entry=native_index[resource['bundles'][0]['file']]
            original_path=checked_source_path(entry['source']);basis_source=pin(original_path)
            if basis_source['sha256']!=entry['sha256'] or not any(
                    part['asset'].casefold()==resource['asset'].casefold() for part in entry['parts']):
                raise ValueError('An attachment basis does not belong to its exact native prefab: '+name)
            source_pins[basis_source['path']]=basis_source
            native_file=resources.load_serialized_bundle(original_path,in_memory=True)[1]
        for path,renderer in renderers:
            match=re.search(r'LOD([0-2])$',path)
            if not match:raise ValueError('An attachment renderer has no explicit native detail level: '+path)
            tree=renderer.read_typetree();mesh_file,mesh=resolve(renderer.assets_file,tree['m_Mesh'])
            original=mesh if native_file is None else native_file.objects[mesh.path_id]
            level=int(match[1])
            if level in lod_sources:raise ValueError('An attachment declares a duplicate detail level: '+name)
            lod_sources[level]=dict(mesh=original.path_id,packet=export_current_mesh(original),bones=resources.skin_bone_paths(renderer.assets_file,tree,resolve))
        if sorted(lod_sources)!=[0,1,2]:raise ValueError('An attachment source must own every native detail level: '+name)
        for path,renderer in renderers:
            tree=renderer.read_typetree();mesh_file,mesh=resolve(renderer.assets_file,tree['m_Mesh']);packet=export_current_mesh(mesh)
            if mesh_file is not renderer.assets_file:raise ValueError('Attachment topology requires its own prefab mesh owner.')
            match=re.search(r'LOD([0-2])$',path)
            if not match:raise ValueError('An attachment renderer has no explicit native detail level: '+path)
            lod=int(match[1]);levels.append(lod);bones=resources.skin_bone_paths(renderer.assets_file,tree,resolve)
            slots=[i for i,bone in enumerate(bones)if bone.split('/')[-1]==profile['supportBone']]
            indices=np.asarray(packet['boneIndices']).reshape(-1,4);weights=np.asarray(packet['weights']).reshape(-1,4)
            if len(slots)!=1 or set(indices[weights>0])!={slots[0]}:raise ValueError('An attachment is not rigidly owned by its declared support bone.')
            frame=np.asarray(packet['bindPoses']).reshape(-1,4,4)[slots[0]].T
            preferred=profile['sourceLods'][lod]
            basis=lod_sources[preferred]
            if discovery:
                # A closed or folded far mesh has no authentic root loop. Use
                # the nearest finer native mesh with a real connection to fit.
                for basis_lod in range(preferred,-1,-1):
                    basis=lod_sources[basis_lod]
                    try:
                        production.rigid_attachment_topology(basis['packet'],frame)
                    except ValueError:
                        if basis_lod==0:raise
                    else:break
            basis_packet=basis['packet']
            frame_identity=lambda packet:[(shape['name'],[frame['weight']for frame in shape['frames']])for shape in packet['shapes']]
            if basis['bones']!=bones or basis_packet['bindPoses']!=packet['bindPoses']or frame_identity(basis_packet)!=frame_identity(packet):
                raise ValueError('An attachment detail reference has another native rig or physical-frame identity.')
            candidate,details=production.refine_attachment_surface(basis_packet,frame,supports,profile,lod)
            tag=name+'_LOD'+str(lod);compiled_name=tag+'_SourceSurface';meshes.append(recipe_mesh(dict(candidate,name=compiled_name)))
            packet_path=folder/(tag+'.json.gz');native_path=folder/(tag+'-Native.json.gz');basis_path=folder/(tag+'-Basis.json.gz')
            for target,value in((packet_path,candidate),(native_path,packet),(basis_path,basis_packet)):
                target.write_bytes(gzip.compress(json.dumps(value,separators=(',',':')).encode(),mtime=0))
            row=dict(asset=name,lod=lod,path=path,mesh=mesh.path_id,source=pin(source/'Catalog'/by_cab[mesh_file.name]['file']),
                packet=pin(packet_path),nativePacket=pin(native_path),basisPacket=pin(basis_path),basisMesh=basis['mesh'],bones=bones,frame=frame.ravel().tolist(),supports=witnesses,
                profile=profile,root=details['root'],passes=details['passes'],compiledName=compiled_name)
            if basis_source:row['basisSource']=basis_source
            rows.append(row)
            for pointer in tree['m_Materials']:
                mf,material=resolve(renderer.assets_file,pointer);mt=material.read_typetree();_,shader=resolve(mf,mt['m_Shader'])
                if shader.read_typetree()['m_ParsedForm']['m_Name']!=profile['sourceShader']:raise ValueError('An attachment surface has another material shader.')
                values=dict(mt['m_SavedProperties']['m_Floats']);updates={}
                for key,value in profile['materialFloats'].items():
                    if key not in values or type(value)not in(int,float)or not math.isfinite(value):
                        raise ValueError(f'Attachment {name} has no writable finite material value for {key}.')
                    target=float(np.float32(value))
                    if values[key]!=target:updates[key]=dict(before=values[key],after=target)
                key=(by_cab[mf.name]['file'],material.path_id);change=dict(pathId=material.path_id,values=updates)
                if key in material_updates and material_updates[key]!=change:raise ValueError('A shared attachment material has conflicting profiles.')
                material_updates[key]=change
        if sorted(levels)!=[0,1,2]:raise ValueError('An attachment source must own every native detail level exactly once.')
        recipe=folder/'MeshRecipe.json';encode_recipe(dict(schemaVersion=1,bundleName=name.lower()+'_source_surface.bundle',meshes=meshes),recipe,'Data/m.bin')
        recipes.append(str(recipe.resolve()));print('Prepared source attachment:',name,flush=True)
    inputs.extend(source_pins.values())
    inputs.extend(row for witnesses, _, _ in support_cache.values() for row in witnesses)
    for reference in inputs:
        if sha(reference['path'])!=reference['sha256']:raise ValueError('An attachment input changed during authoring.')
    write(output/'Plan.json',dict(source=pin(source/'ResourceAssembly.json'),catalog=pin(source/(Path('Catalog')/RESOURCE_FILES['manifest'])),
        inputs=inputs,retainedSourceInputs=retained,rows=rows,recipes=recipes,
        materials=[dict(file=key[0],update=value)for key,value in sorted(material_updates.items())if value['values']],review='pending'))


def validate_attachment_surface(receipt,producer_inputs):
    """Replay attachment geometry and verify its exact mesh, rig and material owners."""
    import numpy as np
    import character_resources as resources
    for reference in(receipt['source'],receipt['target'],*producer_inputs):
        if sha(reference['path'])!=reference['sha256']:raise ValueError('An attachment surface input or output changed.')
    before=resources.load_serialized_bundle(receipt['source']['path'],in_memory=True)[1]
    after=resources.load_serialized_bundle(receipt['target']['path'],in_memory=True)[1]
    if before.name!=after.name or set(before.objects)!=set(after.objects)or [v.path for v in before.externals]!=[v.path for v in after.externals]:
        raise ValueError('An attachment surface changed serialized object or external ownership.')
    allowed=set();packets={};cache={}
    def packet(reference):
        if sha(reference['path'])!=reference['sha256']:raise ValueError('An attachment packet changed.')
        return json.loads(gzip.decompress(Path(reference['path']).read_bytes()))
    for row in receipt['rows']:
        native=packet(row['nativePacket']);basis=packet(row['basisPacket']);authored=packet(row['packet']);key=row['mesh'];allowed.add(key);packets[key]=authored
        if export_current_mesh(before.objects[key])['sourceFingerprint']!=native['sourceFingerprint']or export_current_mesh(after.objects[key])['sourceFingerprint']!=authored['sourceFingerprint']:
            raise ValueError('A saved attachment differs from its actual source or authored packet.')
        basis_file=before
        if 'basisSource'in row:
            original=row['basisSource'];path=checked_source_path(original['path'])
            if original not in producer_inputs or sha(path)!=original['sha256']:
                raise ValueError('An original attachment basis changed.')
            basis_file=resources.load_serialized_bundle(path,in_memory=True)[1]
        if export_current_mesh(basis_file.objects[row['basisMesh']])['sourceFingerprint']!=basis['sourceFingerprint']:
            raise ValueError('An attachment detail reference differs from its actual source mesh.')
        supports=[]
        for reference in row['supports']:
            if sha(reference['path'])!=reference['sha256']:raise ValueError('An attachment support surface changed.')
            witness=read(reference['path']);identity=(witness['source']['sha256'],witness['mesh'])
            if identity not in cache:
                source=witness['source']
                if sha(source['path'])!=source['sha256']:raise ValueError('An original attachment support changed.')
                source_mesh=export_current_mesh(resources.load_serialized_bundle(source['path'],in_memory=True)[1].objects[witness['mesh']])
                frame=np.asarray(witness['frame']).reshape(4,4)
                positions=np.asarray(source_mesh['vertices']).reshape(-1,3)@frame[:3,:3].T+frame[:3,3]
                normals=np.asarray(source_mesh['normals']).reshape(-1,3)@np.linalg.inv(frame[:3,:3]);normals/=np.maximum(np.linalg.norm(normals,axis=1,keepdims=True),1e-12)
                if source_mesh['sourceFingerprint']!=witness['fingerprint']or source_mesh['subMeshes'][witness['slot']]!=witness['triangles']or \
                        not np.array_equal(positions,witness['vertices'])or not np.allclose(normals,witness['normals'],atol=1e-14,rtol=0):
                    raise ValueError('An attachment witness differs from its real native support mesh.')
                cache[identity]=True
            supports.append(witness)
        with retained_skin_producer(producer_inputs)as owner:
            production,_=owner.source_owner()
            replay,details=production.refine_attachment_surface(basis,np.asarray(row['frame']).reshape(4,4),supports,row['profile'],row['lod'])
        if replay['sourceFingerprint']!=authored['sourceFingerprint']or details!=dict(root=row['root'],passes=row['passes']):
            raise ValueError('An attachment surface differs from its retained source producer.')
    for update in receipt['rendererBoneUpdates']:
        key=update['renderer'];allowed.add(key)
        expected,_=resources.extend_skin_bone_palette(before,before.objects[key].read_typetree(),update['sourceBonePaths'],update['targetBonePaths'],packet=packets[update['mesh']])
        if resources.serialized_object_bytes(before.objects[key],tree=expected)!=resources.serialized_object_bytes(after.objects[key]):
            raise ValueError('An attachment changed an unowned rig or culling field.')
    for update in receipt['materialFloatUpdates']:
        key=update['pathId'];allowed.add(key);expected=before.objects[key].read_typetree();entries=expected['m_SavedProperties']['m_Floats']
        values=dict(entries)
        if any(values.get(name)!=row['before']for name,row in update['values'].items()):raise ValueError('An attachment finish lost its original shader float.')
        expected['m_SavedProperties']['m_Floats']=[(name,update['values'][name]['after']if name in update['values']else value)for name,value in entries]
        if resources.serialized_object_bytes(before.objects[key],tree=expected)!=resources.serialized_object_bytes(after.objects[key]):
            raise ValueError('An attachment material changed an unowned property.')
    changed={key for key in before.objects if resources.serialized_object_bytes(before.objects[key])!=resources.serialized_object_bytes(after.objects[key])}
    if not changed<=allowed or changed!=set(receipt['changedObjects']):raise ValueError('An attachment changed an unrelated serialized object.')


def bind_attachment_surfaces():
    """Bind compiled native attachments and their declared material finishes."""
    import character_resources as resources
    parser=argparse.ArgumentParser();parser.add_argument('--source',type=Path,required=True);parser.add_argument('--plan',type=Path,required=True)
    parser.add_argument('--report',type=Path,required=True);parser.add_argument('--output',type=Path,required=True);args=parser.parse_args()
    source=checked_catalog_path(args.source);output=checked_catalog_path(args.output)
    if output.exists()or not any(output.is_relative_to(ROOT/p)for p in('tools/asset-authoring/workspace','tools/game-test/artifacts')):
        raise ValueError('Attachment binding requires a new independent workspace.')
    plan=read(args.plan);report=read(args.report);pin=lambda path:dict(path=str(Path(path).resolve()),sha256=sha(path))
    if plan['source']!=pin(source/'ResourceAssembly.json')or plan['catalog']!=pin(source/(Path('Catalog')/RESOURCE_FILES['manifest']))or not report.get('success'):
        raise ValueError('The attachment plan or compilation belongs to another source.')
    for reference in plan['inputs']:
        if sha(reference['path'])!=reference['sha256']:raise ValueError('An attachment input changed before binding.')
    compiled={Path(row['bundlePath']).name:row for row in report.get('bundles')or[report]};groups=collections.defaultdict(list);materials=collections.defaultdict(list)
    for row in plan['rows']:groups[Path(row['source']['path']).name].append(row)
    for row in plan['materials']:materials[row['file']].append(row['update'])
    assembly=read(source/'ResourceAssembly.json');manifest=read(source/(Path('Catalog')/RESOURCE_FILES['manifest']));proofs={row['file']:row for row in assembly['proofs']}
    output.mkdir(parents=True);catalog=output/'Catalog';catalog.mkdir();count=0
    for bundle in manifest['bundles']:
        name=bundle['file'];target=catalog/name
        if name not in groups and name not in materials:target.hardlink_to(source/'Catalog'/name);continue
        replacements=[];entries=groups.get(name,[]);authorities=[]
        if entries:
            asset=entries[0]['asset'];child=compiled[asset.lower()+'_source_surface.bundle'];path=Path(child['bundlePath'])
            if not child.get('success')or sha(path)!=child['bundleSha256']:raise ValueError('A compiled attachment resource changed.')
            file=resources.load_serialized_bundle(path,in_memory=True)[1];meshes={obj.peek_name():obj for obj in file.objects.values()if obj.type.name=='Mesh'}
            for row in entries:
                packet=json.loads(gzip.decompress(Path(row['packet']['path']).read_bytes()));native=json.loads(gzip.decompress(Path(row['nativePacket']['path']).read_bytes()))
                mesh=meshes[row['compiledName']]
                if export_current_mesh(mesh)['sourceFingerprint']!=packet['sourceFingerprint']:raise ValueError('Unity changed an authored attachment mesh.')
                replacements.append(dict(sourceFingerprint=native['sourceFingerprint'],sourcePathIds=[row['mesh']],packet=packet,binding=None,
                    compiledTree=mesh.read_typetree(),sourceBonePaths=row['bones'],bonePaths=row['bones']))
                authorities.append(dict(row,compiledBundle=pin(path)))
        receipt=resources.prepare_standalone_part(source/'Catalog'/name,bundle['sha256'],target,replacements,
            export_mesh=export_current_mesh,material_updates=materials.get(name,[]))
        receipt.update(file=name,rows=authorities,plan=pin(args.plan),engineReport=pin(args.report))
        validate_attachment_surface(receipt,plan['inputs'])
        path=output/'Receipts'/(name+'.json');write(path,receipt);proof=proofs[name];before=proof['sha256']
        proof.update(sha256=receipt['target']['sha256'],bytes=target.stat().st_size,allRetainedObjectFieldsExactAfterReferenceRebasing=False)
        proof.setdefault('appearanceFinishing',[]).append(dict(kind='source-owned-attachment-surface',beforeSha256=before,sha256=proof['sha256'],
            proof=pin(path),authority=dict(inputs=plan['inputs'])))
        bundle['sha256']=proof['sha256'];count+=len(entries);print('Bound source attachment:',name,flush=True)
    write(catalog/RESOURCE_FILES['manifest'],manifest)
    assembly.setdefault('retainedSourceInputs',[]).extend(plan['retainedSourceInputs'])
    assembly.update(catalogSha256=sha(catalog/RESOURCE_FILES['manifest']),bytes=sum(row['bytes']for row in assembly['proofs']),artAccepted=False,
        preparedSource=dict(assembly=pin(source/'ResourceAssembly.json'),catalog=pin(source/(Path('Catalog')/RESOURCE_FILES['manifest']))),
        attachmentSurfaces=dict(plan=pin(args.plan),meshes=count,materials=len(plan['materials']),review='pending'))
    write(output/'ResourceAssembly.json',assembly)


def native_groom_fixed_vertices(file,mesh,count,resolve):
    """Resolve authored cloth selections through their actual render child."""
    fixed=set();owners=[]
    for component in file.objects.values():
        if component.type.name!='MonoBehaviour':continue
        tree=component.read_typetree()
        if 'clothSelection'not in tree or 'virtualDeformer'not in tree:continue
        selection_file,selection=resolve(file,tree['clothSelection'])
        virtual_file,virtual=resolve(file,tree['virtualDeformer'])
        selections=selection.read_typetree()['selectionList']
        children=virtual.read_typetree()['deformer']['renderDeformerList']
        if len(selections)!=len(children):raise ValueError('A native cloth selection has another render-child correspondence.')
        for child_index,(record,pointer)in enumerate(zip(selections,children)):
            if not pointer['m_PathID']:raise ValueError('A native groom cloth has an unresolved render child.')
            render_file,render=resolve(virtual_file,pointer);deformer=render.read_typetree()['deformer']
            mesh_file,source_mesh=resolve(render_file,deformer['sharedMesh'])
            if mesh_file.name!=file.name or source_mesh.path_id!=mesh:continue
            data_file,data=resolve(render_file,deformer['meshData']);hashes=data.read_typetree()['vertexHashList']
            codes=record['selectData'];source_hashes=record['vertexHashList']
            if len(hashes)!=count or len(codes)!=count or len(source_hashes)!=count or any(type(value)is not int or value not in(0,1,2,3)for value in codes):
                raise ValueError('A native cloth selection differs from its render vertex domain.')
            lookup={value:index for index,value in enumerate(source_hashes)}
            indices=[lookup.get(value,index)if value else index for index,value in enumerate(hashes)]
            mapped=[codes[index]for index in indices]
            fixed.update(index for index,value in enumerate(mapped)if value==2)
            owners.append(dict(component=dict(cab=file.name,object=component.path_id),
                selection=dict(cab=selection_file.name,object=selection.path_id),
                virtualDeformer=dict(cab=virtual_file.name,object=virtual.path_id),
                renderDeformer=dict(cab=render_file.name,object=render.path_id),child=child_index,
                mesh=dict(cab=mesh_file.name,object=source_mesh.path_id),meshData=dict(cab=data_file.name,object=data.path_id),
                hashRemappedVertices=sum(index!=target for index,target in enumerate(indices)),fixedVertices=sum(value==2 for value in mapped)))
    return dict(sourceVertexCount=count,fixedVertexIds=sorted(fixed),selectionOwners=owners,
        selectionMeaning='MagicaCloth.SelectionData.Fixed=2; authored immobility, not a growth-origin classification')


def prepare_native_groom_inputs():
    """Pair declared native groom geometry with its retained authored flow field."""
    import numpy as np
    import character_resources as resources
    sys.path.insert(0,str(ROOT/'tools/blender-models/CharacterHair'))
    import GenerateFlowCoverageSweptProfile as groom
    parser=argparse.ArgumentParser();parser.add_argument('--source',type=Path,required=True);parser.add_argument('--census',type=Path,required=True)
    parser.add_argument('--native-index',type=Path,required=True)
    parser.add_argument('--contact-census',type=Path)
    parser.add_argument('--assets',nargs='*',default=[])
    parser.add_argument('--catalog-scope',action='store_true')
    parser.add_argument('--domain-contract',type=Path,required=True);parser.add_argument('--output',type=Path,required=True)
    args=parser.parse_args();output=checked_catalog_path(args.output);source=checked_catalog_path(args.source)
    if output.exists()or not any(output.is_relative_to(ROOT/p)for p in('tools/asset-authoring/workspace','tools/game-test/artifacts')):
        raise ValueError('Native groom inputs require a new authoring workspace.')
    census=read(args.census);manifest=read(source/(Path('Catalog')/RESOURCE_FILES['manifest']))
    native_index=read(args.native_index);native_index_pin=dict(path=str(args.native_index.resolve()),sha256=sha(args.native_index))
    native_cabs={row['cab']:row for row in native_index['bundles']};native_inputs={}
    @lru_cache(maxsize=16)
    def native_load(cab):
        row=native_cabs[cab];path=Path(row['source'])
        if sha(path)!=row['sha256']:raise ValueError('A native groom material bundle changed.')
        native_inputs[str(path.resolve())]=row['sha256']
        file=resources.load_serialized_bundle(path,in_memory=True)[1]
        if file.name!=cab:raise ValueError('A native groom material bundle has another serialized owner.')
        return file
    def native_resolve(file,pointer):
        target=native_load(file.name if not pointer['m_FileID']else file.externals[pointer['m_FileID']-1].name)
        return target,target.objects[pointer['m_PathID']]
    def native_materials(skin):
        authority=skin['sourceProvenance'];file=native_load(authority['serializedFile'])
        origin=native_cabs[file.name]
        if origin['sha256']!=authority['bundle']['sha256']:raise ValueError('A native groom renderer differs from its source geometry authority.')
        renderer=file.objects[int(authority['rendererPathId'])].read_typetree();result=copy.deepcopy(skin['groomMaterialCapture'])
        def pointer_owner(owner,obj):return dict(bundle=dict(path=str(Path(native_cabs[owner.name]['source']).resolve()),sha256=native_cabs[owner.name]['sha256']),cab=owner.name,object=obj.path_id)
        for ordinal,material in enumerate(result):
            slot=material.get('slot',ordinal)
            material_file,obj=native_resolve(file,renderer['m_Materials'][slot]);tree=obj.read_typetree()
            shader_file,shader=native_resolve(material_file,tree['m_Shader']);parsed=shader.read_typetree()['m_ParsedForm']
            if tree['m_Name']!=material['name']or parsed['m_Name']!=material['shader']:
                raise ValueError('A captured groom material has another native serialized owner.')
            material['sourceShaderTextureDefaults']={row['m_Name']:row['m_DefTexture']['m_DefaultName']for row in parsed['m_PropInfo']['m_Props']if row['m_Type']==4}
            material['sourceTextureSamplers']={};textures={};environments=dict(tree['m_SavedProperties']['m_TexEnvs'])
            for prop in('_MainTex','_Mask'):
                value=environments.get(prop)
                if value is None or not value['m_Texture']['m_PathID']:
                    if prop in material['maps']:raise ValueError('A captured groom texture has no native material pointer.')
                    continue
                texture_file,texture=native_resolve(material_file,value['m_Texture']);data=texture.read_typetree()
                if prop not in material['maps']:raise ValueError('A native groom texture has no retained capture.')
                capture=material['maps'][prop]
                if [data['m_Width'],data['m_Height']]!=[capture['width'],capture['height']]or \
                        [value['m_Scale']['x'],value['m_Scale']['y']]!=capture['scale']or[value['m_Offset']['x'],value['m_Offset']['y']]!=capture['offset']:
                    raise ValueError('A captured groom texture differs from its native size or transform.')
                material['sourceTextureSamplers'][prop]=copy.deepcopy(data['m_TextureSettings'])
                textures[prop]=pointer_owner(texture_file,texture)
            material['sourceMaterialAuthority']=dict(nativeIndex=native_index_pin,renderer=pointer_owner(file,file.objects[int(authority['rendererPathId'])]),
                material=pointer_owner(material_file,obj),shader=pointer_owner(shader_file,shader),textures=textures)
        return result
    groom_resources={row['asset'].casefold()for row in manifest['resources']if row['type']=='GameObject'and any(p.get('set')=='Groom'for p in row.get('parameters',[]))}
    authorities={row['name']:row for row in census['assets']}
    if args.catalog_scope and args.assets:raise ValueError('Choose explicit native sources or the complete typed catalog scope.')
    if not args.assets:
        missing=groom_resources-{row['internalId'].casefold()for row in authorities.values()}
        if missing:raise ValueError('The native groom census omits typed groom resources: '+repr(sorted(missing)))
        names=[name for name,row in authorities.items()if row['internalId'].casefold()in groom_resources]
    else:names=args.assets
    if len(set(names))!=len(names)or any(name not in authorities or authorities[name]['internalId'].casefold()not in groom_resources for name in names):raise ValueError('A selected native groom has no matching typed catalog owner.')
    assembly=read(source/'ResourceAssembly.json');proofs={row['file']:row for row in assembly['proofs']};cabs={row['newCab']:row['file']for row in assembly['proofs']}
    if assembly['catalogSha256']!=sha(source/(Path('Catalog')/RESOURCE_FILES['manifest'])):
        raise ValueError('The groom source authority differs.')
    contract=read(args.domain_contract)
    if contract.get('status')!='PASS_NATIVE_MASK_GPU_AND_SOURCE_DOMAIN_POLICY':raise ValueError('Native groom material coverage has no completed authority.')
    for pin in contract['authorities']:
        if sha(pin['path'])!=pin['sha256']:raise ValueError('A groom native material authority changed.')
    contact_bytes=args.contact_census.read_bytes()if args.contact_census else None
    contact_census=json.loads(contact_bytes.decode('utf-8-sig'))if contact_bytes is not None else None;contact_heads={}
    contact_source=dict(path=str(args.contact_census.resolve()),sha256=hashlib.sha256(contact_bytes).hexdigest())if contact_bytes is not None else None
    if args.contact_census:
        from resource_storage import register_work_inputs
        register_work_inputs(args.contact_census.resolve(),output)
    def compatible_heads(asset,native):
        if contact_census is None:return None
        family=asset['internalId'].casefold().split('/graphics/')[0]
        fittings={shape['name'].casefold()for shape in native['shapes']}
        selected=[head for head in contact_census['assets']if head['name'].casefold()in fittings or
            head['internalId'].casefold().split('/graphics/')[0]==family]
        result=[]
        for head in selected:
            for skin in head['skins']:
                if not re.search(r'_LOD0(?:_|$)',skin['path'])or not any(material and material['shader'].startswith('Lux/Human/Skin')for material in skin['materials']):continue
                key=(skin['meshFile'],tuple(skin['rendererToPrefab']))
                if key not in contact_heads:
                    packet=groom.read(skin['meshFile'])
                    if fingerprint(packet)[0]!=skin['sourceFingerprint']:raise ValueError('A compatible groom contact source changed its native geometry.')
                    contact_heads[key]=dict(identity=head['name'],sourceFingerprint=skin['sourceFingerprint'],
                        packet=dict(path=str(Path(skin['meshFile']).resolve()),sha256=sha(skin['meshFile']),role='payload'),rendererToPrefab=skin['rendererToPrefab'])
                elif contact_heads[key]['sourceFingerprint']!=skin['sourceFingerprint']:raise ValueError('Compatible groom contact aliases disagree on native geometry.')
                result.append(dict(contact_heads[key],identity=head['name']))
        return dict(sourceCensus=contact_source,heads=result)
    rows=[]
    @lru_cache(maxsize=8)
    def load(cab):
        path=source/'Catalog'/cabs[cab]
        if sha(path)!=proofs[cabs[cab]]['sha256']:raise ValueError('A native groom flow source changed.')
        return resources.load_serialized_bundle(path,in_memory=True)[1]
    def resolve(file,pointer):
        file=load(file.name if pointer['m_FileID']==0 else file.externals[pointer['m_FileID']-1].name)
        return file,file.objects[pointer['m_PathID']]
    selections={};required_meshes={}
    for name in sorted(names,key=str.casefold):
        asset=authorities[name]
        resource=next(r for r in manifest['resources']if r['asset'].casefold()==asset['internalId'].casefold())
        file_name=resource['bundles'][0]['file'];file=load(proofs[file_name]['newCab'])
        owned=resources.groom_mesh_identifiers(file);required_meshes[file.name]=owned
        for skin in asset['skins']:
            renderer_id=int(skin['sourceProvenance']['rendererPathId'])
            if renderer_id not in file.objects or file.objects[renderer_id].type.name!='SkinnedMeshRenderer':
                raise ValueError('A native groom census renderer has no retained serialized owner.')
            renderer=file.objects[renderer_id].read_typetree()
            mesh_file,obj=resolve(file,renderer['m_Mesh'])
            if mesh_file.name!=file.name or obj.path_id not in owned:continue
            match=re.search(r'_LOD([012])(?:_|$)',skin['path'])
            if match is None:raise ValueError('A native groom renderer has no declared LOD: '+skin['path'])
            key=(mesh_file.name,obj.path_id)
            alias=dict(asset=name,internalId=asset['internalId'],nativeRenderer=skin['path'],rendererPathId=str(renderer_id))
            if key in selections:
                previous=selections[key]['skin']
                if previous['sourceFingerprint']!=skin['sourceFingerprint']or previous['groomMaterialCapture']!=skin['groomMaterialCapture']or \
                        not np.allclose(previous['rendererToPrefab'],skin['rendererToPrefab'],rtol=0,atol=2e-6):
                    raise ValueError('A shared groom mesh has conflicting native fitting or material authorities.')
                selections[key]['aliases'].append(alias);continue
            selections[key]=dict(asset=asset,skin=skin,fileCab=file.name,meshCab=mesh_file.name,meshId=obj.path_id,rendererId=renderer_id,
                fileName=file_name,lod=int(match.group(1)),aliases=[alias])
    for cab,required in required_meshes.items():
        if {key[1]for key in selections if key[0]==cab}!=required:
            raise ValueError('The native groom census omits retained renderer-owned meshes.')
    keys={key:str(key[0])+':'+str(key[1])for key in selections}
    tags=asset_names.resource_names(asset_names.ResourceName(keys[key],row['asset']['name']+'_LOD'+str(row['lod']),row['skin']['mesh'])for key,row in selections.items())
    output.mkdir(parents=True)
    for key,selection in selections.items():
        asset,skin=selection['asset'],selection['skin'];file=load(selection['fileCab']);mesh_file=load(selection['meshCab'])
        obj=mesh_file.objects[selection['meshId']];renderer=file.objects[selection['rendererId']].read_typetree()
        name=asset['name'];lod=selection['lod'];tag=tags[keys[key]];file_name=selection['fileName']
        current=export_current_mesh(obj);native=groom.read(skin['meshFile']);count=native['vertexCount']
        bones=resources.skin_bone_paths(file,renderer,resolve)
        retained_fitting=groom.validate_retained_groom_source(native,current,source_bones=skin['bones'],target_bones=bones)
        source_uv=np.asarray(next(c['values']for c in native['uv']if c['channel']==0)).reshape(-1,4)
        current_uv=np.asarray(next(c['values']for c in current['uv']if c['channel']==0)).reshape(-1,4)
        if not np.array_equal(source_uv[:,:2].astype('<f4'),current_uv[:count,:2].astype('<f4')):
            raise ValueError('A groom flow source changed native texture coordinates.')
        field=np.asarray(next(c['values']for c in current['uv']if c['channel']==2)).reshape(-1,4)[:count].copy()
        if not np.isfinite(field).all()or not np.ptp(field[:,:2],axis=0).min()>1e-4:raise ValueError('A native groom flow domain is degenerate.')
        field[:,2]=0;base=copy.deepcopy(native)
        if any(c['channel']==2 for c in native['uv']):raise ValueError('The native groom already owns the flow channel.')
        base['uv'].append(dict(channel=2,dimension=4,values=field.ravel().tolist()));base['sourceFingerprint']=fingerprint(base)[0]
        path=output/(tag+'.SourceFlow.json.gz');asset_names.assert_path_budget(path.resolve(),259)
        with gzip.open(path,'wt',encoding='utf-8')as stream:json.dump(base,stream,separators=(',',':'))
        slots=np.zeros(count,int)
        for slot,indices in enumerate(native['subMeshes']):slots[np.asarray(indices,int)]=slot
        tangents=np.asarray(native['tangents']).reshape(-1,4)
        cloth=any('meshData'in obj.read_typetree()for obj in file.objects.values()if obj.type.name=='MonoBehaviour')
        binding=dict(schemaVersion=3,sourceVertexCount=count,sourceFingerprint=native['sourceFingerprint'],cloth=cloth,
            matrix=np.asarray(skin['rendererToPrefab'],dtype='<f4').tolist(),vertices=[dict(anchor=i,native=True,role='volume-support',slot=int(slots[i]),
                uv0=source_uv[i,:2].tolist(),offset=[0.,0.,0.],normal=[0.,0.,1.],tangent=[1.,0.,0.],tangentW=float(tangents[i,3]),surface=None)for i in range(count)])
        binding_path=output/(tag+'.binding.bytes');groom.encode_binding(binding,binding_path)
        rows.append(dict(asset=name,category=asset['groomCategory'],nativeRenderer=skin['path'],sourceAliases=selection['aliases'],lod=lod,nativePacketPath=skin['meshFile'],
            nativePacketSha256=sha(skin['meshFile']),packetPath=str(path.resolve()),packetSha256=sha(path),bindingPath=str(binding_path.resolve()),
            rendererToPrefab=skin['rendererToPrefab'],bones=bones,materials=native_materials(skin),retainedSourceFitting=retained_fitting,
            manifestEntry=dict(sourceFingerprint=native['sourceFingerprint'],mode='authored',authoredMeshAsset=tag+'_SourceFibers',
                bundleFile=tag.lower()+'_source_fibers.bundle',bindingFile=binding_path.name,bindingSha256=sha(binding_path)),
            standaloneResource=dict(path=str((source/'Catalog'/file_name).resolve()),sha256=proofs[file_name]['sha256'],file=file_name,meshPathId=str(obj.path_id)),
            retainedCapacityAuthority=dict(path=str((source/'Catalog'/file_name).resolve()),sha256=proofs[file_name]['sha256'],meshPathId=str(obj.path_id),maximumStoredVertices=current['vertexCount']),
            nativeAuthority=skin['sourceProvenance'],
            nativeClothSelection=dict(native_groom_fixed_vertices(native_load(skin['sourceProvenance']['serializedFile']),
                int(skin['sourceProvenance']['meshPathId']),count,native_resolve),nativeIndex=native_index_pin,nativeFingerprint=native['sourceFingerprint']),
            contactSources=compatible_heads(asset,native),flowFieldAuthority=dict(path=str((source/'Catalog'/cabs[mesh_file.name]).resolve()),
                sha256=proofs[cabs[mesh_file.name]]['sha256'],meshPathId=str(obj.path_id))))
    if sha(args.native_index)!=native_index_pin['sha256']or any(sha(path)!=digest for path,digest in native_inputs.items()):raise ValueError('A native groom material input changed during preparation.')
    if contact_source is not None and sha(args.contact_census)!=contact_source['sha256']:raise ValueError('The groom contact census changed during input preparation.')
    contract_path=output/'DomainContract.json';contract_path.write_bytes(args.domain_contract.read_bytes())
    write(output/'Input.json',dict(schemaVersion=1,rows=rows,sourceCensus=dict(path=str(args.census.resolve()),sha256=sha(args.census)),nativeIndex=native_index_pin,
        domainContract=dict(path=str(contract_path.resolve()),sha256=sha(contract_path))))
    print(json.dumps(dict(status='NATIVE_GROOM_FLOW_INPUTS_PREPARED',sources=len(rows))))


def resource_producer_archives(output,paths=(),retained=()):
    """Keep exact producer versions for the existing prepared-resource replay."""
    records=[];seen=set()
    def keep(original,archive):
        if not isinstance(original,dict)or not isinstance(archive,dict)or any(
                not isinstance(value.get('path'),str)or not isinstance(value.get('sha256'),str)or
                re.fullmatch('[a-fA-F0-9]{64}',value['sha256'])is None for value in(original,archive)):
            raise ValueError('A retained groom producer has no exact path and digest.')
        digest=original['sha256'].lower()
        if archive['sha256'].lower()!=digest:raise ValueError('A retained groom archive has another content authority.')
        path=checked_source_path(Path(archive['path']))
        if sha(path)!=digest:raise ValueError('A retained groom producer archive changed.')
        key=(str(Path(original['path']).resolve()).casefold(),digest)
        if key not in seen:
            records.append(dict(original=dict(path=str(Path(original['path']).resolve()),sha256=digest),
                archive=dict(path=str(path.resolve()),sha256=digest)));seen.add(key)
    for record in retained:keep(record['original'],record['archive'])
    for source in paths:
        source=checked_source_path(Path(source));data=source.read_bytes();digest=hashlib.sha256(data).hexdigest()
        archive=Path(output)/'ProducerSources'/digest[:12]/(source.name+'.source')
        archive.parent.mkdir(parents=True,exist_ok=True)
        if archive.exists():
            if archive.read_bytes()!=data:raise ValueError('A groom producer snapshot changed.')
        else:archive.write_bytes(data)
        if sha(source)!=digest:raise ValueError('A groom producer changed while it was archived.')
        keep(dict(path=str(source.resolve()),sha256=digest),dict(path=str(archive.resolve()),sha256=digest))
    return records


def groom_retained_support(row,profile):
    """Use the numeric producer's declared carrier partition during fitting."""
    surface=row.get('curvedCoreSurface');carrier=bool(profile.get('carrierSurface'))
    if carrier and(not surface or not surface.get('flowCarrierCells')):
        raise ValueError('A reconstructed groom requires its completed physical carrier partition.')
    if surface and surface.get('flowCarrierCells')and not carrier:
        raise ValueError('A physical carrier partition has no declared production profile.')
    if not profile.get('layeredAtlasCards'):
        if carrier:raise ValueError('A reconstructed carrier requires its layered surface owner.')
        return None
    settings=profile.get('supportTerminals')
    if settings is None:raise ValueError('Layered groom compilation requires its bounded retained support domain.')
    result=dict(materials=row['materials'],maximumSupportOffsetMetres=settings['maximumSupportOffsetMetres'])
    if carrier:result['carrierSurface']=copy.deepcopy(surface)
    return result


def prepare_groom_designs():
    """Compile source-proven groom surfaces against their current prefab fitting."""
    import importlib.util
    from mesh_recipe_codec import encode_recipe
    import character_resources as resources
    parser=argparse.ArgumentParser();parser.add_argument('--source',type=Path,required=True);parser.add_argument('--generations',nargs='+',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True);args=parser.parse_args();source=checked_catalog_path(args.source);output=checked_catalog_path(args.output)
    if output.exists()or not any(output.is_relative_to(ROOT/p)for p in('tools/asset-authoring/workspace','tools/game-test/artifacts')):raise ValueError('Groom recipes require a new authoring workspace.')
    output.mkdir(parents=True);rows=[];recipes=[];generations=[];modes=set()
    converter_path=output/'ProducerSources/GenerateFlowCoverageSweptProfile.py';converter_path.parent.mkdir()
    converter_path.write_bytes((ROOT/'tools/blender-models/CharacterHair/GenerateFlowCoverageSweptProfile.py').read_bytes())
    retained=resource_producer_archives(output,[Path(__file__),Path(resources.__file__),
        Path(sys.modules['mesh_recipe_codec'].__file__),Path(sys.modules['collect_sources'].__file__),Path(asset_names.__file__)])
    converter_spec=importlib.util.spec_from_file_location('source_groom_attachment_'+sha(converter_path)[:12],converter_path)
    if converter_spec is None or converter_spec.loader is None:raise ValueError('The groom fitting producer is unavailable.')
    converter=importlib.util.module_from_spec(converter_spec);converter_spec.loader.exec_module(converter)
    for path in args.generations:
        generation=read(path)
        if generation['status']not in('PASS_SOURCE_GROOM_NUMERIC_PROOFS_RENDER_PENDING','PASS_CURVED_CORE_NUMERIC_PROOFS_RENDER_PENDING'):
            raise ValueError('Every source groom requires completed physical proofs.')
        modes.add('source-fibers-only'if generation['status']=='PASS_SOURCE_GROOM_NUMERIC_PROOFS_RENDER_PENDING'else'source-owned-core')
        producer=Path(generation['generatorPath'])
        if sha(producer)!=generation['generatorSha256']:raise ValueError('The numeric groom producer changed.')
        spec=importlib.util.spec_from_file_location('source_groom_'+sha(producer)[:12],producer)
        if spec is None or spec.loader is None:raise ValueError('The numeric groom producer is unavailable.')
        owner=importlib.util.module_from_spec(spec);spec.loader.exec_module(owner)
        meshes=[];assets={row['asset']for row in generation['rows']}
        if len(assets)!=1:raise ValueError('A source design generation must own one complete groom.')
        asset=next(iter(assets));folder=output/asset;folder.mkdir()
        files={row['standaloneResource']['file']for row in generation['rows']}
        if len(files)!=1:raise ValueError('A source groom has multiple prefab owners.')
        current_path=source/'Catalog'/next(iter(files));current_digest=sha(current_path)
        current=resources.load_serialized_bundle(current_path,in_memory=True)[1]
        required=resources.groom_mesh_identifiers(current);seen=set()
        for row in generation['rows']:
            numeric=owner.prove_row(row,generation['profile'])
            if numeric['declaredZeroFine']:raise ValueError('The declared fine source surface is empty.')
            identifier=int(row['standaloneResource']['meshPathId']);seen.add(identifier)
            if identifier not in required:raise ValueError('A source groom mesh has no current renderer.')
            if current_digest!=row['standaloneResource']['sha256']:raise ValueError('The retained groom fitting source changed before compilation.')
            raw=owner.read(row['generatedPacket']);native=owner.read(row['nativePacketPath']);binding=owner.decode_binding(row['bindingPath'])
            support=groom_retained_support(row,generation['profile'])
            old=export_current_mesh(current.objects[identifier]);packet,attachment,facts=converter.standalone_cloth_attachment(raw,native,binding,old,retained_support=support)
            tag=asset_names.source_label(row['manifestEntry']['authoredMeshAsset']);packet_path=folder/(tag+'.json.gz')
            with gzip.open(packet_path,'wt',encoding='utf-8')as stream:json.dump(packet,stream,separators=(',',':'))
            attachment_path=folder/(tag+'.cloth.bytes');converter.encode_legacy_binding(attachment,attachment_path)
            compiled=dict(packet,name=row['manifestEntry']['authoredMeshAsset']);meshes.append(recipe_mesh(compiled))
            pin=lambda value:dict(path=str(Path(value).resolve()),sha256=sha(value))
            rows.append(dict(asset=asset,lod=row['lod'],mesh=identifier,packet=pin(packet_path),clothBinding=pin(attachment_path),
                rawPacket=pin(row['generatedPacket']),native=pin(row['nativePacketPath']),binding=pin(row['bindingPath']),
                numeric=numeric,attachment=facts,source=pin(current_path),generation=pin(path),compiledName=compiled['name'],
                sourceTerminalField=row.get('sourceTerminalField'),materials=row['materials'],retainedSupport=support,
                carrierSurface=facts.get('compiledCarrierSurface')))
        if seen!=required:raise ValueError('A groom source design omitted an owning LOD.')
        recipe=folder/'MeshRecipe.json';encode_recipe(dict(schemaVersion=1,bundleName=asset.lower()+'_source_fibers.bundle',meshes=meshes),recipe,'Data/m.bin')
        recipes.append(str(recipe.resolve()));generations.append(dict(path=str(path.resolve()),sha256=sha(path)))
        print('Prepared complete source groom:',asset,flush=True)
    if len(modes)!=1:raise ValueError('A groom recipe batch requires one declared surface ownership policy.')
    write(output/'Plan.json',dict(mode=next(iter(modes)),retainedSourceInputs=retained,source=dict(path=str((source/'ResourceAssembly.json').resolve()),sha256=sha(source/'ResourceAssembly.json')),
        generations=generations,rows=rows,recipes=recipes,producer=dict(path=str(converter_path.resolve()),sha256=sha(converter_path))))


def groom_primary_atlas(atlas_path,report_path):
    """Read the owned primary recipe and canonical saved texture evidence."""
    import numpy as np
    import character_resources as resources
    if (atlas_path is None)!=(report_path is None):raise ValueError('A primary atlas requires both its receipt and canonical Unity report.')
    if atlas_path is None:return None
    def pin(path):
        path=checked_source_path(Path(path));return dict(path=str(path.resolve()),sha256=sha(path))
    atlas_pin,report_pin=pin(atlas_path),pin(report_path);atlas=read(atlas_pin['path']);report=read(report_pin['path'])
    properties=atlas.get('materialProperties')
    if not isinstance(properties,dict)or set(properties)!={'_GroomColor','_GroomNormal','_MainTex'}or any(not isinstance(value,str)or not value.strip()for value in properties.values())or len(set(properties.values()))!=3:
        raise ValueError('A primary atlas lost its declared material property roles.')
    if atlas.get('status')!='PRIMARY_LOCK_ATLAS_PREPARED_RENDER_REVIEW_PENDING'or atlas.get('materialPurpose')!='primary-lock'or atlas.get('tileEndModes')!=['continuous-body']*8+['owned-free-tip']*8:
        raise ValueError('A primary atlas lost its explicit material and physical-end purpose.')
    recipe_pin=atlas.get('recipe')
    if not isinstance(recipe_pin,dict)or pin(recipe_pin.get('path',''))!=recipe_pin or recipe_pin['sha256']!=atlas.get('recipeSha256'):
        raise ValueError('A primary atlas recipe pin changed.')
    if Path(recipe_pin['path']).parent!=Path(atlas_pin['path']).parent:raise ValueError('A primary atlas recipe escaped its owned receipt directory.')
    recipe=read(recipe_pin['path']);textures=recipe.get('textures',[])
    if recipe.get('schemaVersion')!=1 or recipe.get('meshes')!=[]or len(textures)!=3 or {v['name']for v in textures}!=set(properties.values()):
        raise ValueError('A primary atlas recipe has another texture purpose.')
    if not report.get('success')or report.get('stage')!='complete':raise ValueError('A primary atlas has no complete canonical Unity report.')
    children=report.get('bundles')or[report]
    matches=[value for value in children if Path(value.get('bundlePath','')).name==recipe['bundleName']]
    if len(matches)!=1:raise ValueError('A primary atlas has no unique compiled texture bundle.')
    child=matches[0]
    if not child.get('success')or child.get('stage')!='complete'or child.get('validationScope')!='mesh_templates'or child.get('inputSha256')!=recipe_pin['sha256']or child.get('actualColorSpace')!='Linear':
        raise ValueError('A primary atlas lost its exact recipe/Unity compilation authority.')
    bundle_pin=pin(child['bundlePath'])
    if bundle_pin['sha256']!=child.get('bundleSha256'):raise ValueError('A compiled primary atlas bundle changed.')
    file=resources.load_serialized_bundle(bundle_pin['path'],in_memory=True)[1]
    actual={}
    for obj in file.objects.values():
        if obj.type.name!='Texture2D':continue
        name=obj.peek_name()
        if name in actual:raise ValueError('A primary atlas repeats an actual texture object name.')
        actual[name]=obj
    if set(actual)!=set(properties.values()):raise ValueError('The saved primary atlas has extra or missing texture objects.')
    created=child.get('createdTextures',[]);saved=child.get('savedTextures',[])
    if len(created)!=3 or len(saved)!=3 or len({r['name']for r in saved})!=3 or created!=saved:
        raise ValueError('The primary atlas created/saved texture report differs.')
    inputs={};evidence={};objects={}
    for declaration in textures:
        name=declaration['name'];relative=Path(declaration['path'])
        if relative.is_absolute()or'..'in relative.parts:raise ValueError('A primary atlas image escaped its owned recipe directory.')
        image_pin=pin(Path(recipe_pin['path']).parent/relative)
        if image_pin['sha256']!=declaration['sha256']:raise ValueError('A primary atlas input image changed.')
        row=next((r for r in saved if r['name']==name),None)
        if row is None or row.get('path')!=declaration['path']or row.get('inputSha256')!=declaration['sha256']or not row.get('exact')or not row.get('importSettingsExact')or row.get('crunched')or not row.get('readable'):
            raise ValueError('A primary atlas lost a saved texture input/import proof.')
        for key in('normalMap','sRGB','mipmaps','compressionQuality'):
            if row.get(key)!=declaration.get(key):raise ValueError('A primary atlas texture import setting changed: '+key)
        property=next(key for key,value in properties.items()if value==name)
        if declaration.get('sRGB')is not False or declaration.get('mipmaps')is not True or declaration.get('normalMap')!=(property=='_GroomNormal'):
            raise ValueError('A primary texture changed its declared linear/normal-map sampling role.')
        obj=actual[name];tree=obj.read_typetree();fmt,payloads=resources.texture_mip_payloads(obj);images=resources.texture_mip_images(obj)
        width,height=tree['m_Width'],tree['m_Height'];expected=(1,1)if property=='_MainTex'else(atlas['width'],atlas['height'])
        if (width,height)!=expected or tree['m_MipCount']!=len(payloads)or len(images)!=len(payloads)or len(payloads)!=max(width,height).bit_length():
            raise ValueError('A primary atlas has an incomplete or different saved mip layout.')
        if (width,height,len(payloads))!=(row.get('width'),row.get('height'),row.get('mipCount'))or sum(map(len,payloads))!=row.get('rawBytes')or hashlib.sha256(b''.join(payloads)).hexdigest()!=row.get('rawSha256'):
            raise ValueError('A primary atlas differs from its all-mip saved byte proof.')
        levels=[]
        for level,(payload,image)in enumerate(zip(payloads,images)):
            image=np.asarray(image,dtype=np.uint8);w,h=max(1,width>>level),max(1,height>>level)
            if image.shape!=(h,w,4):raise ValueError('A primary atlas decoded mip has another dimension.')
            if property=='_MainTex'and not np.all(image==255):raise ValueError('A primary Main gate is not neutral at every mip.')
            alpha=image[:,:,3].astype(float)/255.
            levels.append(dict(level=level,width=w,height=h,bytes=len(payload),sha256=hashlib.sha256(payload).hexdigest(),
                decodedSha256=hashlib.sha256(image.tobytes()).hexdigest(),minimumAlpha=float(alpha.min()),maximumAlpha=float(alpha.max()),fractionAtCutoff07=float(np.mean(alpha>=.7))))
        # The declared primary body must reach the existing native fine cutoff;
        # measured coverage is retained for review rather than renamed acceptance.
        if property=='_GroomColor'and any(row['maximumAlpha']<.7 for row in levels):
            raise ValueError('A saved primary atlas completely collapses below its declared body cutoff.')
        inputs[name]=image_pin;objects[name]=dict(object=obj.path_id,name=name)
        evidence[name]=dict(width=width,height=height,format=fmt,mipCount=len(payloads),sampler=copy.deepcopy(tree['m_TextureSettings']),
            colorSpace=tree.get('m_ColorSpace'),savedImport=copy.deepcopy(row),mips=levels)
    if {v['path']:v['sha256']for v in child.get('inputAssets',[])}!={v['path']:v['sha256']for v in textures}:
        raise ValueError('The primary atlas Unity inputs differ from its exact owned recipe.')
    return dict(plan=atlas_pin,recipe=recipe_pin,report=report_pin,bundle=bundle_pin,images=inputs,
        materialProperties={prop:dict(bundle_pin,**objects[name])for prop,name in properties.items()},
        textureReports=evidence,artAccepted=False)


def groom_fine_material_slots(file,mesh,finished,*,primary_vertices=(),source_purposes=()):
    """Reuse explicit purpose slots or append distinct fine/primary clones."""
    import numpy as np
    old=export_current_mesh(file.objects[mesh]);native_count=len(finished['subMeshes'])
    channel=next(u for u in old['uv']if u['channel']==2)
    roles=np.asarray(channel['values']).reshape(-1,4)[:,2]
    if channel.get('dimension',4)<3 or len(roles)!=old['vertexCount']:
        raise ValueError('A groom purpose UV field lost its actual vertex layout.')
    for sub in old['subMeshes'][native_count:]:
        if sub and not np.all((roles[np.asarray(sub,int)]>=17)&(roles[np.asarray(sub,int)]<64.5)):
            raise ValueError('A retained extra material slot contains an unowned carrier.')
    renderers=[o.read_typetree()for o in file.objects.values()if o.type.name=='SkinnedMeshRenderer'and
        o.read_typetree()['m_Mesh']==dict(m_FileID=0,m_PathID=mesh)]
    if not renderers or any(len(r['m_Materials'])!=len(old['subMeshes'])for r in renderers):
        raise ValueError('Fine materials require explicit renderer/submesh ownership without overlays.')
    declared={};mesh_domains={}
    for row in source_purposes:
        if set(row)!={'mesh','sourceSlots','slots'}or type(row['mesh'])is not int or row['mesh']in mesh_domains or type(row['sourceSlots'])is not int or row['sourceSlots']<=0:
            raise ValueError('A groom material-purpose declaration lost its actual mesh owner.')
        identifier=row['mesh']
        if identifier not in file.objects or file.objects[identifier].type.name!='Mesh':
            raise ValueError('A groom material-purpose declaration names another mesh.')
        packet=old if identifier==mesh else export_current_mesh(file.objects[identifier])
        if row['sourceSlots']>len(packet['subMeshes']):raise ValueError('A groom material-purpose source prefix changed.')
        mesh_domains[identifier]=row['sourceSlots']
        for item in row['slots']:
            if set(item)!={'sourceSlot','targetSlot','purpose'}or item['purpose']not in('fine','primary')or type(item['sourceSlot'])is not int or type(item['targetSlot'])is not int or not 0<=item['sourceSlot']<row['sourceSlots']or not row['sourceSlots']<=item['targetSlot']<len(packet['subMeshes']):
                raise ValueError('A groom material-purpose slot escaped its original prefix or actual layout.')
            key=(identifier,item['targetSlot'])
            if key in declared:raise ValueError('A groom material-purpose slot is duplicated.')
            declared[key]=item
    if mesh in mesh_domains and mesh_domains[mesh]!=native_count:
        raise ValueError('A groom material-purpose declaration changed its native source slots.')
    packets={mesh:old};forbidden={'fine':set(),'primary':set()}
    for obj in file.objects.values():
        if not obj.type.name.endswith('Renderer'):continue
        renderer=obj.read_typetree();fine_slots=set();pointer=renderer.get('m_Mesh')
        owner_mesh=None
        if pointer and not pointer['m_FileID']and pointer['m_PathID']in file.objects:
            owner_mesh=pointer['m_PathID']
            if owner_mesh not in packets:packets[owner_mesh]=export_current_mesh(file.objects[owner_mesh])
            packet=packets[owner_mesh];channel=next((u for u in packet['uv']if u['channel']==2),None)
            if channel is not None and channel.get('dimension',4)>=3:
                values=np.asarray(channel['values']).reshape(-1,4)
                if len(values)!=packet['vertexCount']:raise ValueError('A groom purpose UV field lost its actual vertex layout.')
                field=values[:,2]
                fine_slots={slot for slot,sub in enumerate(packet['subMeshes'])if sub and
                    np.all((field[np.asarray(sub,int)]>=17)&(field[np.asarray(sub,int)]<64.5))}
        for slot,pointer in enumerate(renderer.get('m_Materials',[])):
            item=declared.get((owner_mesh,slot))
            if item is not None and slot not in fine_slots and packets[owner_mesh]['subMeshes'][slot]:
                raise ValueError('A declared purpose slot contains non-atlas/fine geometry.')
            if item is not None and item['purpose']=='primary':
                indices=packets[owner_mesh]['subMeshes'][slot]
                if indices and not np.all((field[np.asarray(indices,int)]>=48.5)&(field[np.asarray(indices,int)]<64.5)):
                    raise ValueError('A declared primary slot contains a non-atlas-card role.')
            actual=item['purpose']if item is not None else'fine'if slot in fine_slots else'core'
            if not pointer['m_FileID']:
                for purpose in forbidden:
                    if actual!=purpose:forbidden[purpose].add(pointer['m_PathID'])
    def template(renderer,slot,purpose):
        pointer=renderer['m_Materials'][slot]
        if pointer['m_FileID']or pointer['m_PathID']not in file.objects or file.objects[pointer['m_PathID']].type.name!='Material':
            raise ValueError('Fine material ownership requires a local source material.')
        tree=copy.deepcopy(file.objects[pointer['m_PathID']].read_typetree());tree.pop('m_Name',None)
        for name,entry in tree['m_SavedProperties']['m_TexEnvs']:
            if name in('_GroomColor','_GroomNormal')or purpose=='primary'and name=='_MainTex':
                entry['m_Texture']=dict(m_FileID=0,m_PathID=0)
        return tree
    channel=next(u for u in finished['uv']if u['channel']==2)
    fine_roles=np.asarray(channel['values']).reshape(-1,4)[:,2]
    if channel.get('dimension',4)<3 or len(fine_roles)!=finished['vertexCount']:
        raise ValueError('A finished groom purpose UV field lost its actual vertex layout.')
    primary=np.zeros(len(fine_roles),bool);ids=np.asarray(primary_vertices)
    if ids.ndim!=1 or(ids.size and not np.issubdtype(ids.dtype,np.integer))or np.any(ids<0)or np.any(ids>=len(primary))or len(np.unique(ids))!=len(ids):
        raise ValueError('Primary material vertices have another mesh owner.')
    if ids.size:
        primary[ids.astype(int)]=True
        if np.any((fine_roles[primary]<48.5)|(fine_roles[primary]>=64.5)):
            raise ValueError('A primary material surface is not an existing atlas-card role.')
    rows=[];used=set();next_slot=len(old['subMeshes'])
    for purpose in('fine','primary'):
        if purpose=='primary'and not ids.size:continue
        wanted=(fine_roles>=17)&~primary if purpose=='fine'else primary
        for source_slot,sub in enumerate(finished['subMeshes']):
            tri=np.asarray(sub,int).reshape(-1,3)
            if not len(tri):continue
            mixed=np.any(primary[tri],axis=1)&~np.all(primary[tri],axis=1)
            if np.any(mixed):raise ValueError('A source triangle mixes fine and primary material purposes.')
            if not np.any(np.all(wanted[tri],axis=1)):continue
            candidates=[]
            for slot in range(native_count,len(old['subMeshes'])):
                item=declared.get((mesh,slot))
                if slot in used or purpose=='primary'and(item is None or item['purpose']!='primary'or item['sourceSlot']!=source_slot):
                    continue
                if purpose=='fine'and item is not None and item['purpose']!='fine':continue
                if any(renderer['m_Materials'][slot]['m_PathID']in forbidden[purpose]for renderer in renderers):continue
                if all(template(renderer,slot,purpose)==template(renderer,source_slot,purpose)for renderer in renderers):
                    candidates.append(slot)
            target=candidates[0]if candidates else next_slot
            if not candidates:next_slot+=1
            used.add(target);item=dict(sourceSlot=source_slot,targetSlot=target)
            if ids.size:item['purpose']=purpose
            rows.append(item)
    if not rows:raise ValueError('A composed groom has no new fine material domain.')
    return rows


def prepare_groom_composition():
    """Assemble declared carrier and fine surfaces inside their source allocation."""
    import importlib.util
    from mesh_recipe_codec import encode_recipe
    import character_resources as resources
    parser=argparse.ArgumentParser();parser.add_argument('--source',type=Path,required=True);parser.add_argument('--fibers',type=Path,required=True)
    parser.add_argument('--plan',type=Path,required=True);parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--primary-atlas',type=Path);parser.add_argument('--primary-atlas-report',type=Path);args=parser.parse_args()
    source=checked_catalog_path(args.source);fibers=checked_catalog_path(args.fibers);output=checked_catalog_path(args.output)
    if output.exists()or not any(output.is_relative_to(ROOT/p)for p in('tools/asset-authoring/workspace','tools/game-test/artifacts')):raise ValueError('Groom composition requires a new authoring workspace.')
    pin=lambda path:dict(path=str(Path(path).resolve()),sha256=sha(path))
    plan=read(args.plan);finished=read(fibers/'ResourceAssembly.json');baseline=read(source/'ResourceAssembly.json')
    primary_atlas=groom_primary_atlas(args.primary_atlas,args.primary_atlas_report)
    if plan.get('mode')!='source-fibers-only'and not(plan.get('mode')=='source-owned-core'and
            plan.get('rows')and all(row.get('carrierSurface')is not None for row in plan['rows'])):
        raise ValueError('Groom composition requires a declared fine or reconstructed carrier domain.')
    if finished['groomSourceDesigns']['plan']!=pin(args.plan):raise ValueError('The finished fibers belong to another source design.')
    output.mkdir(parents=True);producer=output/'ProducerSources/GenerateFlowCoverageSweptProfile.py';producer.parent.mkdir()
    producer.write_bytes((ROOT/'tools/blender-models/CharacterHair/GenerateFlowCoverageSweptProfile.py').read_bytes())
    retained=resource_producer_archives(output,[Path(__file__),Path(resources.__file__),Path(sys.modules['mesh_recipe_codec'].__file__)],
        [*baseline.get('retainedSourceInputs',[]),*finished.get('retainedSourceInputs',[]),*plan.get('retainedSourceInputs',[])])
    spec=importlib.util.spec_from_file_location('source_groom_composition_'+sha(producer)[:12],producer)
    if spec is None or spec.loader is None:raise ValueError('The source composition producer is unavailable.')
    owner=importlib.util.module_from_spec(spec);spec.loader.exec_module(owner)
    groups=collections.defaultdict(list)
    for row in plan['rows']:groups[Path(row['source']['path']).name].append(row)
    fine_atlas=None
    texture=finished.get('groomVertexFrames',{}).get('textures')
    if texture is not None:
        name=texture['file']
        previous=next(row for row in baseline['proofs']if row['file']==name)
        updated=next(row for row in finished['proofs']if row['file']==name)
        if texture.get('samplerAndResourceReferencesExact')is not True or previous['sha256']!=texture['sourceSha256']or updated['sha256']not in(previous['sha256'],texture['sha256']):
            raise ValueError('The finished groom atlas belongs to another retained source.')
        old_pin=pin(source/'Catalog'/name);new_pin=pin(texture['target']['path'])
        if old_pin['sha256']!=previous['sha256']or new_pin['sha256']!=texture['sha256']:raise ValueError('A composed groom atlas input changed.')
        compatibility=resources.verify_groom_texture_replacement(old_pin,new_pin,[pin(source/'Catalog'/name)for name in groups])
        fine_atlas=dict(source=old_pin,target=new_pin,compatibility=compatibility)
    rows=[];recipes=[]
    for name,entries in groups.items():
        old_path=source/'Catalog'/name;fine_path=fibers/'Catalog'/name
        old=resources.load_serialized_bundle(old_path,in_memory=True)[1];fine=resources.load_serialized_bundle(fine_path,in_memory=True)[1]
        for file in(old,fine):
            formats={(dict(file.objects[k].read_typetree()['m_SavedProperties']['m_Floats']).get('_SupportInVertexFrame'),dict(file.objects[k].read_typetree()['m_SavedProperties']['m_Floats']).get('_FlowInVertexFrame'))for k in resources.groom_material_identifiers(file)}
            if formats!={(1.,2.)}:raise ValueError('Composition requires the same completed vertex-frame representation.')
        if {r['mesh']for r in entries}!=resources.groom_mesh_identifiers(old)or resources.groom_mesh_identifiers(old)!=resources.groom_mesh_identifiers(fine):raise ValueError('A source composition omitted an actual LOD.')
        asset=entries[0]['asset'];folder=output/asset;folder.mkdir();meshes=[]
        for entry in entries:
            if sha(entry['clothBinding']['path'])!=entry['clothBinding']['sha256']:raise ValueError('The fine attachment source changed.')
            original=export_current_mesh(old.objects[entry['mesh']]);attachment=owner.decode_binding(entry['clothBinding']['path'])
            support=None;support_proof=None;carrier=None
            if entry.get('carrierSurface')is not None:
                if entry['carrierSurface']!=entry['attachment'].get('compiledCarrierSurface')or not entry.get('retainedSupport',{}).get('carrierSurface'):
                    raise ValueError('The reconstructed groom lost its proven fitting range.')
                if sha(entry['native']['path'])!=entry['native']['sha256']:raise ValueError('The reconstructed groom native source changed.')
                carrier=dict(native=owner.read(entry['native']['path']),surface=entry['carrierSurface'],materials=entry['materials'],
                    maximumSupportOffsetMetres=entry['retainedSupport']['maximumSupportOffsetMetres'])
            elif entry.get('sourceTerminalField')is not None:
                if sha(entry['native']['path'])!=entry['native']['sha256']:raise ValueError('The terminal groom native source changed.')
                support,support_proof=owner.authored_terminal_support(owner.read(entry['native']['path']),original,
                    attachment['matrix'],entry['materials'],entry['sourceTerminalField'])
            finished_mesh=export_current_mesh(fine.objects[entry['mesh']])
            primary_vertices=[i for i,row in enumerate(attachment['vertices'])if row.get('role')=='primary-lock']
            if primary_vertices and primary_atlas is None:raise ValueError('A primary groom requires its owned atlas receipt and Unity report.')
            source_purposes=[{key:value for key,value in row.items()if key!='file'}for row in baseline.get('groomSourceDesigns',{}).get('materialPurposes',[])if row['file']==name]
            material_slots=groom_fine_material_slots(old,entry['mesh'],finished_mesh,primary_vertices=primary_vertices,source_purposes=source_purposes)
            fine_slots=[{key:value for key,value in row.items()if key!='purpose'}for row in material_slots if row.get('purpose','fine')=='fine']
            primary_slots=[{key:value for key,value in row.items()if key!='purpose'}for row in material_slots if row.get('purpose')=='primary']
            skin=entry['attachment'].get('surfaceSkin')
            if skin is not None:
                if sha(entry['native']['path'])!=entry['native']['sha256']or skin['sourceFingerprint']!=owner.read(entry['native']['path'])['sourceFingerprint']:
                    raise ValueError('A groom composition changed its native center skin authority.')
            packet,binding,proof=owner.compose_finished_source_groom(original,finished_mesh,
                attachment,vertex_capacity=original['vertexCount'],authored_support=support,fine_material_slots=fine_slots,carrier_replacement=carrier,
                **(dict(primary_material_slots=primary_slots)if primary_vertices else{}),
                **(dict(surface_skin=skin)if skin is not None else{}))
            if support_proof is not None:proof['authoredSupport']=support_proof
            tag=asset+'_LOD'+str(entry['lod']);packet_path=folder/(tag+'.json.gz');binding_path=folder/(tag+'.cloth.bytes')
            with gzip.open(packet_path,'wt',encoding='utf-8')as stream:json.dump(packet,stream,separators=(',',':'))
            owner.encode_legacy_binding(binding,binding_path);compiled_name=tag+'_SourceFibers';meshes.append(recipe_mesh(dict(packet,name=compiled_name)))
            rows.append(dict(asset=asset,lod=entry['lod'],mesh=entry['mesh'],source=pin(old_path),fibers=pin(fine_path),binding=entry['clothBinding'],
                packet=pin(packet_path),clothBinding=pin(binding_path),compiledName=compiled_name,composition=proof,
                materialSlots=material_slots,sourceSlots=len(finished_mesh['subMeshes']),native=entry['native'],materials=entry['materials'],sourceTerminalField=entry.get('sourceTerminalField'),
                carrierSurface=entry.get('carrierSurface'),retainedSupport=entry.get('retainedSupport')))
        recipe=folder/'MeshRecipe.json';encode_recipe(dict(schemaVersion=1,bundleName=asset.lower()+'_source_fibers.bundle',meshes=meshes),recipe,'Data/m.bin');recipes.append(str(recipe.resolve()))
        print('Prepared retained source support:',asset,flush=True)
    prepared=dict(mode='retained-source-support',source=pin(source/'ResourceAssembly.json'),fibers=pin(fibers/'ResourceAssembly.json'),
        finePlan=pin(args.plan),producer=pin(producer),rows=rows,recipes=recipes,fineAtlas=fine_atlas,retainedSourceInputs=retained)
    has_primary=any(any(slot.get('purpose')=='primary'for slot in row['materialSlots'])for row in rows)
    if primary_atlas is not None:
        if not has_primary:raise ValueError('An unconsumed primary atlas has no actual primary material surface.')
        prepared['primaryAtlas']=primary_atlas
    write(output/'Plan.json',prepared)


def bind_groom_designs():
    import character_resources as resources
    parser=argparse.ArgumentParser();parser.add_argument('--source',type=Path,required=True);parser.add_argument('--plan',type=Path,required=True)
    parser.add_argument('--report',type=Path,required=True);parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--assets',nargs='*',default=[]);args=parser.parse_args()
    source=checked_catalog_path(args.source);output=checked_catalog_path(args.output)
    if output.exists()or not any(output.is_relative_to(ROOT/p)for p in('tools/asset-authoring/workspace','tools/game-test/artifacts')):raise ValueError('Groom binding requires a new authoring workspace.')
    plan=read(args.plan);report=read(args.report)
    if plan.get('sharedTextures'):raise ValueError('New fine textures require dedicated material slots; shared carrier atlases are retained.')
    if not report.get('success'):raise ValueError('The groom mesh build failed.')
    compiled={Path(row['bundlePath']).name:row for row in report.get('bundles')or[report]}
    assembly=read(source/'ResourceAssembly.json');manifest=read(source/(Path('Catalog')/RESOURCE_FILES['manifest']));proofs={row['file']:row for row in assembly['proofs']}
    groups=collections.defaultdict(list)
    if set(args.assets)-{row['asset']for row in plan['rows']}:raise ValueError('An unknown source groom was requested.')
    for row in plan['rows']:
        if not args.assets or row['asset']in args.assets:groups[Path(row['source']['path']).name].append(row)
    catalog=output/'Catalog';catalog.mkdir(parents=True);parameter_receipts=[]
    material_purposes=copy.deepcopy(assembly.get('groomSourceDesigns',{}).get('materialPurposes',[]))
    primary=plan.get('primaryAtlas')
    if primary is not None and groom_primary_atlas(primary['plan']['path'],primary['report']['path'])!=primary:
        raise ValueError('The composed primary atlas authority or saved mip report changed.')
    donor=read(plan['fibers']['path'])if plan.get('mode')=='retained-source-support'else{}
    assembly['retainedSourceInputs']=resource_producer_archives(output,[Path(__file__),Path(resources.__file__)],
        [*assembly.get('retainedSourceInputs',[]),*plan.get('retainedSourceInputs',[]),*donor.get('retainedSourceInputs',[])])
    resource_original=dict(path=str(Path(resources.__file__).resolve()),sha256=sha(resources.__file__))
    resource_archive=next(record['archive']for record in assembly['retainedSourceInputs']if record['original']==resource_original)
    for bundle in manifest['bundles']:
        name=bundle['file'];target=catalog/name
        if name not in groups:target.hardlink_to(source/'Catalog'/name);continue
        rows=groups[name];composed=plan.get('mode')=='retained-source-support';generation_pin=None;producer=Path(plan['producer']['path'])
        if not composed:
            generations={json.dumps(row['generation'],sort_keys=True)for row in rows}
            if len(generations)!=1:raise ValueError('A groom prefab has different numeric generations.')
            generation_pin=json.loads(next(iter(generations)))
            if sha(generation_pin['path'])!=generation_pin['sha256']:raise ValueError('A proven groom generation changed.')
        if sha(producer)!=plan['producer']['sha256']:raise ValueError('A proven groom fitting producer changed.')
        import importlib.util
        spec=importlib.util.spec_from_file_location('source_groom_binding_'+sha(producer)[:12],producer)
        if spec is None or spec.loader is None:raise ValueError('The groom binding producer is unavailable.')
        owner=importlib.util.module_from_spec(spec);spec.loader.exec_module(owner)
        current=resources.load_serialized_bundle(source/'Catalog'/name,in_memory=True)[1]
        child=compiled[rows[0]['asset'].lower()+'_source_fibers.bundle'];compiled_path=Path(child['bundlePath'])
        if not child.get('success')or sha(compiled_path)!=child['bundleSha256']:raise ValueError('A compiled groom bundle changed.')
        saved=resources.load_serialized_bundle(compiled_path,in_memory=True)[1];meshes={obj.peek_name():obj for obj in saved.objects.values()if obj.type.name=='Mesh'}
        replacements=[];authorities=[]
        for row in rows:
            if row['source']['sha256']!=bundle['sha256']:raise ValueError('A groom source fitting changed before binding.')
            packet=owner.read(row['packet']['path']);attachment=owner.decode_binding(row['clothBinding']['path']);old=export_current_mesh(current.objects[row['mesh']])
            mesh=meshes[row['compiledName']]
            if export_current_mesh(mesh)['sourceFingerprint']!=packet['sourceFingerprint']:raise ValueError('Unity changed a source groom mesh.')
            skin=row.get('composition',row.get('attachment',{})).get('surfaceSkin')
            if skin is not None:
                if sha(row['native']['path'])!=row['native']['sha256']or skin['sourceFingerprint']!=owner.read(row['native']['path'])['sourceFingerprint']:
                    raise ValueError('A groom binding changed its native center skin authority.')
                owner.validate_surface_skin(old,packet,skin,binding=attachment);attachment['surfaceSkin']=skin
            replacements.append(dict(sourceFingerprint=old['sourceFingerprint'],sourcePathIds=[row['mesh']],packet=packet,binding=attachment,compiledTree=mesh.read_typetree()))
            authorities.append(dict(row,compiledBundle=dict(path=str(compiled_path.resolve()),sha256=sha(compiled_path))))
        if {row['mesh']for row in rows}!=resources.groom_mesh_identifiers(current):raise ValueError('A groom binding omits a renderer-owned LOD.')
        updates=[]
        for identifier in ([]if composed else sorted(resources.groom_material_identifiers(current))):
            values=dict(current.objects[identifier].read_typetree()['m_SavedProperties']['m_Floats'])
            updates.append(dict(pathId=identifier,values={key:dict(before=values[key],after=0.)for key in('_SupportInVertexFrame','_FlowInVertexFrame')}))
        slot_updates=[]
        if composed:
            for row in rows:
                count=len(export_current_mesh(current.objects[row['mesh']])['subMeshes'])
                added=[{key:value[key]for key in('sourceSlot','targetSlot')}for value in row.get('materialSlots',[])if value['targetSlot']>=count]
                if added:slot_updates.append(dict(meshPathId=row['mesh'],slots=added))
        receipt=resources.prepare_standalone_part(source/'Catalog'/name,bundle['sha256'],target,replacements,
            export_mesh=export_current_mesh,material_updates=updates,material_slot_updates=slot_updates)
        if slot_updates:receipt['materialSlotUpdates']=slot_updates
        if composed:
            atlas=plan.get('fineAtlas')
            for reference in((atlas['source'],atlas['target'])if atlas is not None else()):
                if sha(reference['path'])!=reference['sha256']:raise ValueError('A dedicated fine atlas input changed.')
            _,prepared=resources.load_serialized_bundle(target,in_memory=True);texture_updates=[];selected_textures=set()
            consumers=atlas['compatibility']['materials']if atlas is not None else[]
            textures=atlas['compatibility']['textures']if atlas is not None else{}
            for row in(rows if atlas is not None else[]):
                renderers=[(key,obj.read_typetree())for key,obj in prepared.objects.items()if obj.type.name=='SkinnedMeshRenderer'and
                    obj.read_typetree()['m_Mesh']==dict(m_FileID=0,m_PathID=row['mesh'])]
                for identifier,renderer in renderers:
                    for slots in row['materialSlots']:
                        if slots.get('purpose','fine')!='fine':continue
                        source_slot=slots['sourceSlot'];target_slot=slots['targetSlot']
                        core_material=renderer['m_Materials'][source_slot];fine_material=renderer['m_Materials'][target_slot]
                        if core_material['m_FileID']or fine_material['m_FileID']:raise ValueError('Dedicated fine material ownership is not local.')
                        fine_properties=dict(prepared.objects[fine_material['m_PathID']].read_typetree()['m_SavedProperties']['m_TexEnvs'])
                        matches=[value for value in consumers if value['file']==name and value['material']==core_material['m_PathID']]
                        if {value['property']for value in matches}!={'_GroomColor','_GroomNormal'}:
                            raise ValueError('A dedicated fine material has incomplete source atlas ownership.')
                        for value in matches:
                            selector=(fine_material['m_PathID'],value['property'])
                            if selector in selected_textures:continue
                            selected_textures.add(selector);texture_id=value['texture']
                            texture_updates.append(dict(pathId=selector[0],property=selector[1],before=fine_properties[selector[1]]['m_Texture'],
                                texture=dict(atlas['target'],object=texture_id,name=textures.get(str(texture_id),textures.get(texture_id)))))
            for row in rows:
                for slots in row.get('materialSlots',[]):
                    if slots.get('purpose')!='primary':continue
                    if primary is None:raise ValueError('A primary material has no exact compiled atlas authority.')
                    for renderer in(obj.read_typetree()for obj in prepared.objects.values()if obj.type.name=='SkinnedMeshRenderer'and obj.read_typetree()['m_Mesh']==dict(m_FileID=0,m_PathID=row['mesh'])):
                        pointer=renderer['m_Materials'][slots['targetSlot']]
                        others={value['m_PathID']for index,value in enumerate(renderer['m_Materials'])if index!=slots['targetSlot']and not value['m_FileID']}
                        if pointer['m_FileID']or pointer['m_PathID']in others:raise ValueError('A primary material aliases its core/fine source after binding.')
                        properties=dict(prepared.objects[pointer['m_PathID']].read_typetree()['m_SavedProperties']['m_TexEnvs'])
                        for prop,texture in primary['materialProperties'].items():
                            selector=(pointer['m_PathID'],prop)
                            if selector in selected_textures:continue
                            selected_textures.add(selector)
                            if prop not in properties:raise ValueError('A primary material lacks its declared texture property.')
                            texture_updates.append(dict(pathId=selector[0],property=prop,before=properties[prop]['m_Texture'],texture=copy.deepcopy(texture)))
            by_mesh={row['mesh']:row for row in rows};by_cab={row['newCab']:row for row in proofs.values()};loaded={prepared.name:prepared}
            def load(cab):
                if cab not in loaded:
                    proof=by_cab[cab];path=catalog/proof['file']
                    if not path.exists():path=source/'Catalog'/proof['file']
                    if sha(path)!=proof['sha256']:raise ValueError('A groom prefab hierarchy input changed.')
                    loaded[cab]=resources.load_serialized_bundle(path,in_memory=True)[1]
                return loaded[cab]
            parameter_updates=[]
            for resource in manifest['resources']:
                if resource['bundles'][0]['file']!=name or resource['type']!='GameObject':continue
                parameters=resource.get('parameters',[])
                before_parameters=copy.deepcopy(parameters)
                skins,_=resource_prefab_skins(manifest,proofs,resource['source'],load)
                for renderer,skin in skins:
                    pointer=skin.read_typetree()['m_Mesh']
                    if skin.assets_file is not prepared or pointer['m_FileID']or pointer['m_PathID']not in by_mesh:continue
                    for slots in by_mesh[pointer['m_PathID']]['materialSlots']:
                        sources=[value for value in before_parameters if value['renderer']==renderer and value['slot']==slots['sourceSlot']]
                        for value in sources:
                            addition=dict(value,slot=slots['targetSlot'])
                            if addition not in parameters:parameters.append(addition)
                if parameters!=before_parameters:
                    resource['parameters']=parameters
                    parameter_updates.append(dict(source=resource['source'],before=before_parameters,after=copy.deepcopy(parameters)))
            if parameter_updates:receipt['rendererParameterUpdates']=parameter_updates
            if texture_updates:
                geometry=output/'GeometrySources'/name;geometry.parent.mkdir(exist_ok=True);geometry.hardlink_to(target)
                receipt['geometryTarget']=dict(path=str(geometry.resolve()),sha256=sha(geometry))
                texture_receipt=resources.replace_owned_material_textures(geometry,sha(geometry),target,texture_updates)
                receipt['materialTextures']=texture_receipt;receipt['target']=texture_receipt['target']
                receipt['objectsAdded']=sorted(set(receipt.get('objectsAdded',[]))|set(texture_receipt.get('objectsAdded',[])))
                receipt['changedObjects']=sorted(set(receipt['changedObjects'])|set(texture_receipt.get('objectsAdded',[]))|
                    {value['pathId']for value in texture_receipt['materialUpdates']}|{texture_receipt['metadataObject']})
        receipt.update(file=name,rows=authorities,generation=generation_pin,engineReport=dict(path=str(args.report.resolve()),sha256=sha(args.report)),
            designPlan=dict(path=str(args.plan.resolve()),sha256=sha(args.plan)),designFiles=sorted(groups))
        if composed:
            receipt['composition']=dict(plan=dict(path=str(args.plan.resolve()),sha256=sha(args.plan)),fibers=plan['fibers'])
        for row in rows:
            material_purposes=[value for value in material_purposes if(value['file'],value['mesh'])!=(name,row['mesh'])]
            if composed and row.get('materialSlots')and 'sourceSlots'in row:
                slots=[dict(value,purpose=value.get('purpose','fine'))for value in row['materialSlots']]
                material_purposes.append(dict(file=name,mesh=row['mesh'],sourceSlots=row['sourceSlots'],slots=slots))
        path=output/'Receipts'/(name+'.json');write(path,receipt);proof=proofs[name];before=proof['sha256']
        proof.update(sha256=receipt['target']['sha256'],bytes=target.stat().st_size,
            objects=proof['objects']+len(receipt.get('objectsAdded',[])),allRetainedObjectFieldsExactAfterReferenceRebasing=False)
        proof.setdefault('appearanceFinishing',[]).append(dict(kind='source-owned-groom-surface',beforeSha256=before,sha256=proof['sha256'],
            proof=dict(path=str(path.resolve()),sha256=sha(path)),authority=dict(inputs=[dict(path=str(producer.resolve()),sha256=sha(producer)),
                resource_archive])))
        if composed:parameter_receipts.append((path,receipt,proof['appearanceFinishing'][-1]))
        bundle['sha256']=proof['sha256'];print('Bound source groom:',name,flush=True)
    write(catalog/RESOURCE_FILES['manifest'],manifest)
    for path,receipt,operation in parameter_receipts:
        receipt['parameterCatalogs']=dict(source=dict(path=str((source/'Catalog'/RESOURCE_FILES['manifest']).resolve()),sha256=sha(source/'Catalog'/RESOURCE_FILES['manifest'])),
            target=dict(path=str((catalog/RESOURCE_FILES['manifest']).resolve()),sha256=sha(catalog/RESOURCE_FILES['manifest'])),files=sorted(groups))
        write(path,receipt);operation['proof']['sha256']=sha(path)
    assembly.update(catalogSha256=sha(catalog/RESOURCE_FILES['manifest']),bytes=sum(row['bytes']for row in assembly['proofs']),artAccepted=False,
        preparedSource=dict(assembly=dict(path=str((source/'ResourceAssembly.json').resolve()),sha256=sha(source/'ResourceAssembly.json')),
            catalog=dict(path=str((source/(Path('Catalog')/RESOURCE_FILES['manifest'])).resolve()),sha256=sha(source/(Path('Catalog')/RESOURCE_FILES['manifest'])))),
        groomSourceDesigns=dict(plan=dict(path=str(args.plan.resolve()),sha256=sha(args.plan)),bundles=len(groups),review='pending'))
    if material_purposes:
        keys=[(row['file'],row['mesh'])for row in material_purposes]
        if len(set(keys))!=len(keys):raise ValueError('A groom material-purpose ledger repeats a mesh owner.')
        assembly['groomSourceDesigns']['materialPurposes']=sorted(material_purposes,key=lambda row:(row['file'],row['mesh']))
    write(output/'ResourceAssembly.json',assembly)


def prepare_groom_frame_resources():
    from concurrent.futures import ProcessPoolExecutor,as_completed
    import character_resources as resources
    parser=argparse.ArgumentParser()
    parser.add_argument('--source',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--shader-report',type=Path)
    parser.add_argument('--shader-validation',type=Path)
    parser.add_argument('--texture-report',type=Path)
    parser.add_argument('--files',nargs='*',default=[])
    parser.add_argument('--shader-only',action='store_true')
    parser.add_argument('--before-preset',type=Path)
    parser.add_argument('--after-preset',type=Path)
    parser.add_argument('--workers',type=int,choices=range(1,5),default=2)
    args=parser.parse_args()
    if bool(args.shader_report)!=bool(args.shader_validation):raise ValueError('A new groom shader requires both its build report and validation.')
    if args.shader_only and args.shader_report is None:raise ValueError('A shader-only update requires a new shader report and validation.')
    if bool(args.before_preset)!=bool(args.after_preset):raise ValueError('Groom preset migration requires both compiled presets.')
    source=checked_catalog_path(args.source);output=checked_catalog_path(args.output)
    if not output.is_relative_to(ROOT/'tools')or output==source:raise ValueError('Groom output requires an independent tools workspace.')
    assembly=read(source/'ResourceAssembly.json');manifest=read(source/(Path('Catalog')/RESOURCE_FILES['manifest']))
    if assembly.get('groomVertexFrames')and not args.shader_only:raise ValueError('Use the declared original frame catalog to regenerate groom support.')
    selected={r['bundles'][0]['file']for r in manifest['resources']if any(p.get('set')=='Groom'for p in r.get('parameters',[]))}
    all_grooms=set(selected)
    if args.files:
        if not set(args.files)<=selected:raise ValueError('An unknown groom resource was selected.')
        selected=set(args.files)
    if args.shader_only:
        if args.files or args.texture_report is not None:raise ValueError('A shader-only update cannot also change groom meshes or textures.')
        selected=set()
    shader=None;shader_path=None;shader_reuse=None
    if args.shader_report is not None:
        shader=read(args.shader_report)
        if not shader.get('success')or shader.get('validationScope')!='materials':raise ValueError('A completed material build is required.')
        shader_path=Path(shader['bundlePath'])
        if sha(shader_path)!=shader['bundleSha256']:raise ValueError('Compiled groom shader changed.')
        validation=read(args.shader_validation)
        if validation.get('status')!='PASS_SHADER_MATERIAL_AND_BRDF_CONTRACTS'or validation['bundle']['sha256']!=shader['bundleSha256']:
            raise ValueError('The compiled groom shader has no matching material and BRDF validation.')
    else:
        name=assembly['groomShading']['shader']['file'];bundle=next(row for row in manifest['bundles']if row['file']==name)
        shader_reuse=resources.verify_groom_shader_reuse(dict(path=str((source/'Catalog'/name).resolve()),sha256=bundle['sha256']),
            [dict(path=str((source/'Catalog'/row['file']).resolve()),sha256=row['sha256'])for row in manifest['bundles']if row['file']in selected])
    output.mkdir(parents=True,exist_ok=True)
    producers=[Path(__file__),Path(resources.__file__),Path(resources.__file__).with_name('mesh_recipe_codec.py')]
    live_producers=[dict(path=str(path.resolve()),sha256=sha(path))for path in producers];pins=[]
    for path in producers:
        saved=output/'ProducerSources'/path.name;saved.parent.mkdir(exist_ok=True)
        if saved.exists()and saved.read_bytes()!=path.read_bytes():raise ValueError('The groom producer epoch changed.')
        saved.write_bytes(path.read_bytes());pins.append(dict(path=str(saved.resolve()),sha256=sha(saved)))
    assembly['retainedSourceInputs']=resource_producer_archives(output,retained=[*assembly.get('retainedSourceInputs',[]),
        *(dict(original=original,archive=archive)for original,archive in zip(live_producers,pins))])
    if shader_reuse is not None:pins.append(shader_reuse['bundle'])
    else:pins.extend(dict(path=str(Path(path).resolve()),sha256=sha(path))for path in (args.shader_report,shader_path,args.shader_validation))
    presets=None
    if args.before_preset:
        presets={key:dict(path=str(path.resolve()),sha256=sha(path))for key,path in(('before',args.before_preset),('after',args.after_preset))}
        for pin in presets.values():resources.read_compiled_groom_defaults(pin)
        pins.extend(presets.values())
    texture=None
    if args.texture_report is not None:
        texture=read(args.texture_report)
        if texture.get('validationScope')=='mesh_templates_batch':
            if not texture.get('success'):raise ValueError('The atlas batch build did not complete.')
            candidates=[row for row in texture.get('bundles',[])if row.get('validationScope')=='mesh_templates'and not row.get('meshes')and row.get('textures')]
            if len(candidates)!=1:raise ValueError('A groom atlas batch must contain one unambiguous texture-only bundle.')
            texture=candidates[0]
        if (not texture.get('success')or texture.get('validationScope')!='mesh_templates'
                or texture.get('meshes')or not texture.get('textures')
                or not all(row.get('exact')and row.get('importSettingsExact')for row in texture['textures'])):
            raise ValueError('A completed texture-only build with saved mip validation is required.')
        texture_path=Path(texture['bundlePath'])
        if sha(texture_path)!=texture['bundleSha256']:raise ValueError('Compiled groom textures changed.')
        if texture_path.name not in {row['file']for row in manifest['bundles']}:
            raise ValueError('The compiled groom texture bundle has no existing resource owner.')
        pins.extend(dict(path=str(Path(path).resolve()),sha256=sha(path))for path in (args.texture_report,texture_path))
    marker=dict(sourceCatalogSha256=sha(source/(Path('Catalog')/RESOURCE_FILES['manifest'])),selectedFiles=sorted(selected),inputs=pins)
    output.mkdir(parents=True,exist_ok=True);catalog=output/'Catalog';catalog.mkdir(exist_ok=True)
    ownership=output/'WorkingDirectory.json'
    if ownership.exists():
        if read(ownership)!=marker:raise ValueError('The groom frame workspace belongs to different inputs.')
    else:
        if any(catalog.iterdir()):raise ValueError('An empty groom frame catalog is required.')
        write(ownership,marker)
    proofs={r['file']:r for r in assembly['proofs']};jobs=[]
    for row in manifest['bundles']:
        original=source/'Catalog'/row['file'];target=catalog/row['file']
        if sha(original)!=row['sha256']:raise ValueError('A groom catalog source changed: '+row['file'])
        if row['file']in selected:
            job=dict(source=str(original),target=str(target),sha256=row['sha256'],codeSha256=sha(resources.__file__),
                     receipt=str(output/'Receipts'/(row['file']+'.json')),gpuBridge=True)
            for operation in reversed(proofs[row['file']].get('appearanceFinishing',[])):
                if operation['kind']!='source-owned-groom-surface':continue
                if sha(operation['proof']['path'])!=operation['proof']['sha256']:raise ValueError('An authored groom frame authority changed.')
                receipt=read(operation['proof']['path']);generation=receipt.get('generation')
                if receipt.get('composition'):
                    job['flowOnly']=True
                    flows=[dict(object=value['mesh'],flow=value['composition']['declaredFlow'])for value in receipt['rows']if value.get('composition',{}).get('declaredFlow')]
                    if flows:job['declaredFlow']=flows
                if generation:
                    authored=read(generation['path'])
                    if any(value.get('curvedCoreSurface')for value in authored['rows']):
                        for reference in(operation['proof'],generation):
                            if sha(reference['path'])!=reference['sha256']:raise ValueError('An authored support normal authority changed.')
                        if not all(value.get('curvedCoreSurface')for value in authored['rows']):raise ValueError('An authored core generation has incomplete normal ownership.')
                        job['preserveSupportNormals']=True
                break
            jobs.append(job)
        elif not target.exists():target.hardlink_to(original)
    completed=[];failures=[]
    with ProcessPoolExecutor(max_workers=args.workers)as pool:
        futures={pool.submit(resources.refine_groom_resource_frames,job):job for job in jobs}
        for future in as_completed(futures):
            try:result=future.result()
            except (ValueError,RuntimeError)as error:
                failures.append(dict(file=Path(futures[future]['source']).name,error=str(error)))
                print('Groom validation failed:',failures[-1],flush=True)
                continue
            row=proofs[result['file']];row['sha256']=result['sha256'];row['bytes']=result['bytes']
            row['allRetainedObjectFieldsExactAfterReferenceRebasing']=False
            row.setdefault('appearanceFinishing',[]).append(dict(kind='groom-vertex-normal-frame',beforeSha256=result['identity']['sourceSha256'],authority=dict(inputs=pins),**result))
            completed.append(result);print('Groom frames:',len(completed),'/',len(jobs),result['file'],flush=True)
    if failures:
        write(output/'GroomFailures.json',dict(owner=marker,failures=failures))
        raise ValueError('Groom validation failed for '+str(len(failures))+' resources; see GroomFailures.json.')
    shader_name=assembly['groomShading']['shader']['file']
    shader_proof=save_compiled_groom_shader(source,catalog,proofs,shader_name,shader_path,shader,args.shader_validation,pins)if shader is not None else None
    texture_receipt=None
    if texture is not None:
        texture_path=Path(texture['bundlePath']);name=texture_path.name
        texture_output=output/'TextureSources'/name;texture_output.parent.mkdir(exist_ok=True)
        texture_receipt=resources.replace_compiled_textures(source/'Catalog'/name,proofs[name]['sha256'],
            texture_output,texture_path,texture['bundleSha256'])
        expected={row['name']:row['rawSha256']for row in texture['textures']}
        if {row['name']:row['encodedSha256']for row in texture_receipt['textures']}!=expected:
            raise ValueError('The groom atlas differs from its compiled mip validation.')
        texture_receipt['dedicatedFineMaterials']=True
    if presets is not None:
        sources=output/'MaterialSources';sources.mkdir(exist_ok=True);material_jobs=[];material_changes=[]
        for name in sorted(all_grooms):
            original=sources/name
            if original.exists():
                if sha(original)!=proofs[name]['sha256']:raise ValueError('A retained groom material source changed.')
            else:original.hardlink_to(catalog/name)
            for operation in proofs[name].get('appearanceFinishing',[]):
                target=operation.get('target')
                if target and Path(target['path'])==(catalog/name).resolve()and target['sha256']==proofs[name]['sha256']:
                    target['path']=str(original.resolve())
            frame_receipt=output/'Receipts'/(name+'.json')
            if frame_receipt.exists():
                frame=read(frame_receipt)
                if frame.get('sha256')==proofs[name]['sha256']:
                    frame['target']['path']=str(original.resolve());write(frame_receipt,frame)
            material_jobs.append(dict(source=str(original.resolve()),target=str((catalog/name).resolve()),sha256=proofs[name]['sha256'],**presets))
        with ProcessPoolExecutor(max_workers=args.workers)as pool:
            for future in as_completed([pool.submit(resources.retune_groom_defaults,job)for job in material_jobs]):
                receipt=future.result()
                if not receipt['changed']:continue
                name=receipt['file'];path=output/'MaterialReceipts'/(name+'.json');write(path,receipt);proof=proofs[name];before=proof['sha256']
                proof.update(sha256=receipt['sha256'],bytes=receipt['bytes'],allRetainedObjectFieldsExactAfterReferenceRebasing=False)
                proof.setdefault('appearanceFinishing',[]).append(dict(kind='source-groom-material-defaults',beforeSha256=before,sha256=receipt['sha256'],
                    proof=dict(path=str(path.resolve()),sha256=sha(path)),authority=dict(inputs=pins)))
                material_changes.append(name);print('Finished source groom material:',name,flush=True)
        assembly['groomMaterialDefaults']=dict(**presets,changedFiles=material_changes,sourceSpecificValuesRetained=True)
    for row in manifest['bundles']:row['sha256']=proofs[row['file']]['sha256']
    for pin in [*pins,*live_producers]:
        if sha(pin['path'])!=pin['sha256']:raise ValueError('Groom producer or shader changed during preparation.')
    write(catalog/RESOURCE_FILES['manifest'],manifest)
    assembly['catalogSha256']=sha(catalog/RESOURCE_FILES['manifest']);assembly['bytes']=sum(r['bytes']for r in assembly['proofs'])
    assembly['preparedSource']=dict(assembly=dict(path=str((source/'ResourceAssembly.json').resolve()),sha256=sha(source/'ResourceAssembly.json')),
        catalog=dict(path=str((source/(Path('Catalog')/RESOURCE_FILES['manifest'])).resolve()),sha256=sha(source/(Path('Catalog')/RESOURCE_FILES['manifest']))))
    finishing=dict(owner=marker,files=sorted(selected),meshes=sum(len(r['meshes'])for r in completed),
        shader=dict(file=shader_name,sha256=proofs[shader_name]['sha256']),review='pending')
    if shader_proof is not None:finishing['shader']['changedObject']=shader_proof['changedObject']
    if shader_reuse is not None:finishing['shader']['retainedSource']=shader_reuse
    if texture_receipt is not None:finishing['textures']=texture_receipt
    assembly['groomShaderFinishing'if args.shader_only else'groomVertexFrames']=finishing
    assembly['artAccepted']=False
    write(output/'ResourceAssembly.json',assembly)
    write(output/'Preparation.json',dict(status='PREPARED_GROOM_VERTEX_FRAMES_REVIEW_PENDING',**finishing))
    print(json.dumps(dict(status='PREPARED_GROOM_VERTEX_FRAMES_REVIEW_PENDING',groomFiles=len(selected),catalog=assembly['catalogSha256'])))


def prepare_resource_appearance(source,index_path,output,census_path,material_contexts,tone_inputs,authoring_sources,*,profiles_path):
    import character_resources as resources
    geometry=prepare_resource_faces(source,index_path,output,census_path,profiles_path=profiles_path)
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
    finish_resource_pigments(geometry,output)
    cab_to_file={row['newCab']:row['file']for row in assembly['proofs']}
    dependencies={row['file']:{cab_to_file[cab]for cab in row['dependencies']}for row in assembly['proofs']}
    manifest['resources']=resources.complete_resource_dependencies(manifest['resources'],dependencies)
    for row in manifest['bundles']:row['sha256']=old_proofs[row['file']]['sha256']
    for pin in pins:
        if sha(pin['path'])!=pin['sha256']:raise ValueError('An appearance source changed during preparation.')
    write(target/RESOURCE_FILES['manifest'],manifest)
    assembly['catalogSha256']=sha(target/RESOURCE_FILES['manifest']);assembly['bytes']=sum(row['bytes']for row in assembly['proofs'])
    assembly['bundleDependencyPolicy']='serialized-file-closure'
    assembly['faceFinishing']=dict(source=str(source),sourceCatalogSha256=sha(catalog/RESOURCE_FILES['manifest']),inputs=pins,rows=reports,coverage=coverage,
        humanStyleFiles=sorted(styled),creatureAnatomyPreserved=True,colorCorrections=color_proofs)
    write(output/'ResourceAssembly.json',assembly)
    return dict(status='PREPARED_NOT_VISUALLY_ACCEPTED',meshes=geometry['meshes'],textures=len(texture_jobs),catalog=str(target),runtimeFaces=sum(bool(row['file'])for row in coverage))


def repair_resource_preloads():
    """Close every selected bundle's asset roots without changing its objects."""
    import character_resources as resources
    parser=argparse.ArgumentParser();parser.add_argument('--source',type=Path,required=True)
    parser.add_argument('--resources',nargs='*',default=[]);args=parser.parse_args()
    source=checked_catalog_path(args.source);output=require_workspace()
    if source==output or any(output.iterdir()):raise ValueError('Preload repair requires an independent empty workspace.')
    manifest_path=checked_catalog_path(source/'Catalog',RESOURCE_FILES['manifest'])
    assembly_path=checked_catalog_path(source,'ResourceAssembly.json')
    manifest=read(manifest_path);assembly=read(assembly_path)
    closure=prepared_resource_closure(manifest_path.parent,manifest,assembly)
    declarations={row['source']:row for row in manifest['resources']}
    selected=set(args.resources)
    if len(declarations)!=len(manifest['resources'])or selected-set(declarations):
        raise ValueError('A preload resource selection has missing or ambiguous typed ownership.')
    files=({row['bundles'][0]['file']for key,row in declarations.items()if key in selected}if selected else
        {row['file']for row in manifest['bundles']})
    if not files:raise ValueError('Preload repair selected no resource bundles.')
    pin=lambda path:dict(path=str(Path(path).resolve()),sha256=sha(path))
    source_catalog=dict(manifest=pin(manifest_path),assembly=pin(assembly_path))
    implementations=[Path(__file__),Path(resources.__file__),Path(sys.modules['mesh_recipe_codec'].__file__),Path(asset_names.__file__),
        Path(__file__).with_name('resource-package.json'+('.source'if Path(__file__).suffix=='.source'else''))]
    current_archives=resource_producer_archives(output,implementations)
    assembly['retainedSourceInputs']=resource_producer_archives(output,retained=[*assembly.get('retainedSourceInputs',[]),*current_archives])
    inputs=[*source_catalog.values(),*[row['archive']for row in current_archives]]
    catalog=output/'Catalog';catalog.mkdir();proofs={row['file']:row for row in assembly['proofs']};completed=[]
    for bundle in manifest['bundles']:
        name=bundle['file'];original=checked_catalog_path(source/'Catalog',name);target=checked_catalog_path(catalog,name)
        if sha(original)!=bundle['sha256']:raise ValueError('A preload source resource changed: '+name)
        row=proofs[name]
        if name not in files:
            try:target.hardlink_to(original)
            except OSError:
                if target.exists():raise
                shutil.copyfile(original,target)
            if sha(target)!=bundle['sha256']:raise ValueError('An inherited preload resource changed while copying.')
            continue
        receipt=closure.repair_preloads(row['newCab'],target)
        if receipt['changed']:
            receipt['sourceCatalog']=source_catalog
            path=output/'Receipts'/(name+'.json');write(path,receipt)
            before=bundle['sha256'];bundle['sha256']=receipt['target']['sha256']
            row.update(sha256=bundle['sha256'],bytes=target.stat().st_size,allRetainedObjectFieldsExactAfterReferenceRebasing=False)
            row.setdefault('appearanceFinishing',[]).append(dict(kind='asset-root-preloads',beforeSha256=before,
                sha256=bundle['sha256'],proof=pin(path),authority=dict(inputs=inputs)))
            completed.append(dict(file=name,assetRoots=receipt['assetRoots'],preloadPointers=receipt['preloadPointers'],proof=pin(path)))
        print('Checked asset root preloads:',name,receipt['changed'],flush=True)
    for reference in inputs:
        if sha(reference['path'])!=reference['sha256']:raise ValueError('A preload preparation authority changed.')
    write(catalog/RESOURCE_FILES['manifest'],manifest)
    assembly.update(catalogSha256=sha(catalog/RESOURCE_FILES['manifest']),bytes=sum(row['bytes']for row in assembly['proofs']),
        preparedSource=dict(catalog=source_catalog['manifest'],assembly=source_catalog['assembly']),
        artAccepted=False,preloadClosures=dict(inputs=inputs,resources=completed,selectedResources=sorted(selected)))
    assembly.pop('artAcceptance',None)
    assembly['inputs']=[*assembly['inputs'],*inputs]
    write(output/'ResourceAssembly.json',assembly)
    write(output/'Preparation.json',dict(status='PREPARED_ASSET_ROOT_PRELOADS_REVIEW_PENDING',
        resources=completed,checkedBundles=len(files),catalogSha256=assembly['catalogSha256'],
        modelObjectAndStreamBytesUnchanged=True,publicResourcesChanged=False))


def repair_resource_bone_orders():
    """Normalize the native slot contract before further geometry authoring."""
    import character_resources as resources
    parser=argparse.ArgumentParser()
    parser.add_argument('--source',type=Path,required=True)
    parser.add_argument('--native-index',type=Path,required=True)
    parser.add_argument('--audit-only',action='store_true')
    args=parser.parse_args();source=checked_catalog_path(args.source);output=require_workspace()
    if source==output or any(output.iterdir()):raise ValueError('Bone-order repair requires an independent empty workspace.')
    manifest=read(source/(Path('Catalog')/RESOURCE_FILES['manifest']));assembly=read(source/'ResourceAssembly.json')
    if assembly.get('status')!='PASS_STANDALONE_CHARACTER_RESOURCES'or assembly['catalogSha256']!=sha(source/(Path('Catalog')/RESOURCE_FILES['manifest'])):
        raise ValueError('A complete standalone source catalog is required.')
    assets={row['asset']for row in manifest['resources']if row['type']=='GameObject'}
    audit=resources.audit_native_bone_orders(read(args.native_index),assets)
    audit_path=output/'NativeBoneOrders.json';write(audit_path,audit)
    if audit['status']!='PASS_NATIVE_SKIN_SLOT_AUDIT':raise ValueError('Native bone ownership is incomplete; see NativeBoneOrders.json.')
    if args.audit_only:
        print(json.dumps(dict(status=audit['status'],assets=len(assets),skins=len(audit['rows']),changed=sum(row['changed']for row in audit['rows']))));return
    producers=[]
    for path in (Path(__file__),Path(resources.__file__),Path(resources.__file__).with_name('mesh_recipe_codec.py')):
        archive=output/'ProducerSources'/path.name;archive.parent.mkdir(exist_ok=True);shutil.copyfile(path,archive)
        producers.append(dict(path=str(archive.resolve()),sha256=sha(archive)))
    pins=[*producers,*[dict(path=str(Path(path).resolve()),sha256=sha(path))for path in(args.native_index,audit_path)]]
    owners={row['asset']:row['bundles'][0]['file']for row in manifest['resources']};jobs={};aligned=[]
    for native in audit['rows']:
        if not native['changed']:continue
        name=owners[native['asset']];update=dict(renderer=native['renderer'],nativeMesh=native['nativeMesh'],sourceBonePaths=native['sourceBonePaths'])
        # Already normalized inputs retain their complete operation lineage.
        _,file=resources.load_serialized_bundle(source/'Catalog'/name,in_memory=True)
        renderer=file.objects[update['renderer']].read_typetree()
        def resolve(owner,pointer):
            if owner is not file or pointer['m_FileID']:raise ValueError('A prepared native rig is not locally owned.')
            return owner,owner.objects[pointer['m_PathID']]
        def canonical(path):
            parts=path.split('/');return '/'.join(parts[parts.index('Bip001'):]if'Bip001'in parts else parts)
        paths=[canonical(path)for path in update['sourceBonePaths']];expected=[paths[i]for i in native['permutation']]
        actual=resources.skin_bone_paths(file,renderer,resolve)
        if actual[:len(paths)]==expected:
            mesh=renderer['m_Mesh'];_,original=resources.load_serialized_bundle(update['nativeMesh']['path'],in_memory=True)
            if mesh['m_FileID']or file.objects[mesh['m_PathID']].read_typetree()['m_BindPose'][:len(paths)]!=original.objects[update['nativeMesh']['pathId']].read_typetree()['m_BindPose']:
                raise ValueError('An aligned renderer changed its native bind-pose contract.')
            aligned.append(dict(file=name,renderer=update['renderer']));continue
        owned=jobs.setdefault(name,{})
        if update['renderer']in owned and owned[update['renderer']]!=update:raise ValueError('Native aliases disagree about a shared renderer.')
        owned[update['renderer']]=update
    if not jobs:
        result=dict(status='PASS_NATIVE_BONE_ORDERS_ALREADY_ALIGNED',alreadyAligned=aligned,source=dict(path=str((source/'ResourceAssembly.json').resolve()),sha256=sha(source/'ResourceAssembly.json')))
        write(output/'Preparation.json',result);print(json.dumps(result));return
    catalog=output/'Catalog';catalog.mkdir();proofs={row['file']:row for row in assembly['proofs']};changed=[]
    for bundle in manifest['bundles']:
        name=bundle['file'];original=source/'Catalog'/name;target=catalog/name
        if name not in jobs:target.hardlink_to(original);continue
        receipt=resources.prepare_standalone_part(original,bundle['sha256'],target,[],export_mesh=export_current_mesh,bone_order_updates=list(jobs[name].values()))
        resources.validate_renderer_bone_orders(receipt)
        path=output/'Receipts'/(name+'.json');write(path,receipt)
        row=proofs[name];row['sha256']=receipt['target']['sha256'];row['bytes']=target.stat().st_size;row['allRetainedObjectFieldsExactAfterReferenceRebasing']=False
        row.setdefault('appearanceFinishing',[]).append(dict(kind='serialized-bone-order',beforeSha256=bundle['sha256'],sha256=row['sha256'],proof=dict(path=str(path.resolve()),sha256=sha(path)),authority=dict(inputs=pins)))
        bundle['sha256']=row['sha256'];changed.append(dict(file=name,renderers=len(jobs[name]),changedObjects=receipt['changedObjects']))
    write(catalog/RESOURCE_FILES['manifest'],manifest)
    assembly['catalogSha256']=sha(catalog/RESOURCE_FILES['manifest']);assembly['bytes']=sum(row['bytes']for row in assembly['proofs'])
    assembly['preparedSource']=dict(assembly=dict(path=str((source/'ResourceAssembly.json').resolve()),sha256=sha(source/'ResourceAssembly.json')),catalog=dict(path=str((source/(Path('Catalog')/RESOURCE_FILES['manifest'])).resolve()),sha256=sha(source/(Path('Catalog')/RESOURCE_FILES['manifest']))))
    assembly['nativeBoneOrder']=dict(inputs=pins,resources=changed,alreadyAligned=aligned,review='pending');assembly['artAccepted']=False
    write(output/'ResourceAssembly.json',assembly)
    result=dict(status='PREPARED_NATIVE_BONE_ORDERS_REVIEW_PENDING',resources=changed,alreadyAligned=aligned)
    write(output/'Preparation.json',result);print(json.dumps(result))


def restore_native_surface_resources():
    """Restore declared source topology without changing materials or renderers."""
    import gzip
    import character_resources as resources
    parser=argparse.ArgumentParser();parser.add_argument('--source',type=Path,required=True)
    mode=parser.add_mutually_exclusive_group(required=True)
    mode.add_argument('--plan',type=Path);mode.add_argument('--census',type=Path)
    parser.add_argument('--report',type=Path);parser.add_argument('--native-index',type=Path)
    parser.add_argument('--designs',type=Path);parser.add_argument('--assets',nargs='*',default=[])
    parser.add_argument('--output',type=Path,required=True);args=parser.parse_args()
    source=checked_catalog_path(args.source);output=checked_catalog_path(args.output)
    if output.exists()or not any(output.is_relative_to(ROOT/p)for p in('tools/asset-authoring/workspace','tools/game-test/artifacts')):
        raise ValueError('Native surface restoration requires a new authoring workspace.')
    pin=lambda path:dict(path=str(Path(path).resolve()),sha256=sha(path))
    assembly=read(source/'ResourceAssembly.json')
    manifest_path=source/'Catalog'/RESOURCE_FILES['manifest'];manifest=read(manifest_path)
    if assembly['catalogSha256']!=sha(manifest_path):raise ValueError('A native surface source catalog changed.')
    if args.census is not None:
        if args.report is not None or args.native_index is None:
            raise ValueError('Native head preparation requires its original index and no compilation report.')
        from mesh_recipe_codec import encode_recipe
        designs_path=args.designs or Path(__file__).with_name('face-designs.json')
        census=read(args.census);designs=read(designs_path);index=read(args.native_index)
        if designs.get('schemaVersion')!=1:raise ValueError('Unknown native face design schema.')
        assets={row['name']:row for row in census['assets']};selected=set(args.assets or designs['surfaces'])
        if not selected or not selected<=assets.keys()or not selected<=designs['surfaces'].keys():
            raise ValueError('A native head source selection has no declared design and Census owner.')
        declarations={row['asset']:row for row in manifest['resources']}
        proofs={row['file']:row for row in assembly['proofs']};by_cab={row['newCab']:row for row in proofs.values()}
        originals={row['cab']:row for row in index['bundles']}
        native_manifest=dict(resources=[dict(source=part['sourceKey'],asset=part['asset'],bundles=[dict(file=row['file'])])
            for row in index['bundles']for part in row.get('parts',[])])
        native_proofs={row['file']:dict(newCab=row['cab'])for row in index['bundles']}
        @lru_cache(maxsize=8)
        def load(cab):
            row=by_cab[cab];path=source/'Catalog'/row['file']
            if sha(path)!=row['sha256']:raise ValueError('A current head bundle changed.')
            return resources.load_serialized_bundle(path,in_memory=True)[1]
        @lru_cache(maxsize=8)
        def native_load(cab):
            row=originals[cab]
            if sha(row['source'])!=row['sha256']:raise ValueError('An original native head bundle changed.')
            file=resources.load_serialized_bundle(row['source'],in_memory=True)[1]
            if file.name!=cab:raise ValueError('An original native head changed its CAB identity.')
            return file
        production,art=source_owner();inputs=[pin(args.census),pin(designs_path),pin(args.native_index)]
        producer_sources={}
        for role,path in(('production',production.__file__),('art',art.__file__),('implementation',__file__),
                ('resources',resources.__file__),('naming',asset_names.__file__),('fingerprint',sys.modules[fingerprint.__module__].__file__),
                ('codec',sys.modules[encode_recipe.__module__].__file__),('package',Path(__file__).with_name('resource-package.json'+('.source'if Path(__file__).suffix=='.source'else'')))):
            path=Path(path);archive=output/'ProducerSources'/(path.name.removesuffix('.source')+'.source')
            archive.parent.mkdir(parents=True,exist_ok=True);archive.write_bytes(path.read_bytes())
            reference=pin(archive);inputs.append(reference)
            if role in('production','art'):producer_sources[role]=reference
        def packet_pin(path,packet):
            with gzip.open(path,'wt',encoding='utf-8')as stream:json.dump(packet,stream,separators=(',',':'))
            return pin(path)
        grouped={};owners={}
        for name in sorted(selected):
            asset=assets[name];design=designs['surfaces'][name]
            if design['sourceContentKey']!=asset['sourceContentKey']:
                raise ValueError('A native head design belongs to another source content identity: '+name)
            resource=declarations[asset['internalId']]['source']
            current_skins,current_resolve=resource_prefab_skins(manifest,proofs,resource,load)
            native_skins,native_resolve=resource_prefab_skins(native_manifest,native_proofs,resource,native_load)
            recovery=design.get('nativeRecovery',{})
            if not isinstance(recovery,dict)or not set(recovery)<={'passes','discardNonNativeChannels'}:
                raise ValueError('Unknown native head recovery declaration.')
            discard_by_renderer=recovery.get('discardNonNativeChannels',{});passes=recovery.get('passes',2)
            if not isinstance(discard_by_renderer,dict)or type(passes)is not int or not 0<=passes<=4 or \
                    not set(discard_by_renderer)<={row['path']for row in design['renderers']}:
                raise ValueError('A native head recovery has an unknown renderer or refinement declaration.')
            for renderer_design in design['renderers']:
                relative=renderer_design['path']
                matches=[[renderer for path,renderer in rows if path==relative]for rows in(current_skins,native_skins)]
                if any(len(value)!=1 for value in matches):raise ValueError('A declared native head renderer is absent or ambiguous: '+relative)
                current_renderer,native_renderer=(value[0]for value in matches)
                current_file,current_mesh=current_resolve(current_renderer.assets_file,current_renderer.read_typetree()['m_Mesh'])
                native_file,native_mesh=native_resolve(native_renderer.assets_file,native_renderer.read_typetree()['m_Mesh'])
                if current_mesh.type.name!='Mesh'or native_mesh.type.name!='Mesh':raise ValueError('A declared native head has another Mesh owner.')
                for slot in renderer_design['slots']:
                    tree=native_renderer.read_typetree()
                    if type(slot)is not int or not 0<=slot<len(tree['m_Materials']):raise ValueError('A native head material slot is outside its renderer.')
                    mf,material=native_resolve(native_renderer.assets_file,tree['m_Materials'][slot])
                    _,shader=native_resolve(mf,material.read_typetree()['m_Shader'])
                    if shader.read_typetree()['m_ParsedForm']['m_Name']!=renderer_design['shader']:
                        raise ValueError('A native head shader differs from its declared source material.')
                packet=export_current_mesh(current_mesh);native=export_current_mesh(native_mesh)
                allowed_discard=discard_by_renderer.get(relative,[])
                discard=sorted({value['name']for value in packet['shapes']}-{value['name']for value in native['shapes']})
                if not isinstance(allowed_discard,list)or any(not isinstance(value,str)for value in allowed_discard)or \
                        len(allowed_discard)!=len(set(allowed_discard))or not set(discard)<=set(allowed_discard):
                    raise ValueError('A native head has undeclared non-native physical channels: '+name+'/'+relative)
                bones=resources.skin_bone_paths(native_renderer.assets_file,native_renderer.read_typetree(),native_resolve)
                roots=[value for value in bones if value.rsplit('/',1)[-1]=='Bip001 Head']
                if len(roots)!=1:raise ValueError('A native head has no unique native facial bone owner.')
                parts=roots[0].split('/');root='/'.join(parts[parts.index('Bip001'):]if'Bip001'in parts else parts)
                current_pin=by_cab[current_file.name];native_pin=originals[native_file.name]
                file=current_pin['file'];identity=(current_file.name,current_mesh.path_id)
                authority=(native_file.name,native_mesh.path_id,tuple(discard),passes)
                if identity in owners:
                    if owners[identity]!=authority:raise ValueError('A native head Mesh has conflicting declared consumer authorities.')
                    continue
                owners[identity]=authority;directory=output/Path(file).stem;directory.mkdir(parents=True,exist_ok=True)
                prefix=str(current_mesh.path_id);grouped.setdefault(file,[]).append(dict(resource=resource,renderer=relative,
                    meshPathId=current_mesh.path_id,name=packet['name'],currentFingerprint=packet['sourceFingerprint'],
                    nativeMesh=dict(path=native_pin['source'],sha256=native_pin['sha256'],pathId=native_mesh.path_id),
                    nativePacket=packet_pin(directory/(prefix+'-Native.json.gz'),native),
                    faceRecovery=dict(retainedFacePacket=packet_pin(directory/(prefix+'-Retained.json.gz'),packet),
                        nativeHeadBonePath=root,discardNonNativeChannels=discard,
                        refinement=dict(lod=production.lod_index(dict(path=relative)),passes=passes,profile=production.extremity_refinement_profile()),
                        producerSources=producer_sources)))
        bundle_names=asset_names.resource_names([asset_names.ResourceName(name,Path(name).stem,suffix='surface')for name in grouped],
            extension='.bundle',lowercase=True,strict=True)
        rows=[];jobs=[]
        for name,meshes in grouped.items():
            owner=load(proofs[name]['newCab']);targets={}
            native=native_surface_restoration_sources(index,manifest,proofs,meshes,owner,load,target_packets=targets)
            directory=output/Path(name).stem;recipe=[]
            for row in meshes:
                packet=targets[row['meshPathId']];row['packet']=packet_pin(directory/(str(row['meshPathId'])+'-Recovered.json.gz'),packet)
                row.update(nativeVertices=native[row['meshPathId']]['vertexCount'],authoredVertices=packet['vertexCount'])
                recipe.append(recipe_mesh(packet))
            bundle=bundle_names[name];recipe_path=directory/'MeshRecipe.json'
            encode_recipe(dict(schemaVersion=1,bundleName=bundle,meshes=recipe),recipe_path,'Data/m.bin')
            rows.append(dict(file=name,sourceSha256=proofs[name]['sha256'],compiledFile=bundle,meshes=meshes))
            jobs.append(dict(outputFile=bundle,recipe=pin(recipe_path),meshes=len(meshes),sourceAssets=0,sourceBundles=0))
        write(output/'Plan.json',dict(kind='NativeSurfaceRestoration',schemaVersion=1,sourceAssembly=pin(source/'ResourceAssembly.json'),
            nativeIndex=pin(args.native_index),inputs=inputs,rows=rows))
        write(output/'BuildPlan.json',dict(baseManifest=pin(manifest_path),jobs=jobs))
        print(json.dumps(dict(status='OWNED_NATIVE_HEAD_RECIPES_READY_COMPILE_PENDING',resources=len(rows),meshes=sum(len(row['meshes'])for row in rows))))
        return
    if args.report is None or args.native_index is not None or args.designs is not None or args.assets:
        raise ValueError('Saved native surface restoration requires its plan and compilation report.')
    plan=read(args.plan);report=read(args.report)
    if plan.get('kind')!='NativeSurfaceRestoration'or plan.get('schemaVersion')!=1 or \
            plan.get('sourceAssembly')!=pin(source/'ResourceAssembly.json')or assembly['catalogSha256']!=sha(manifest_path):
        raise ValueError('A native surface restoration plan belongs to another catalog.')
    if not report.get('success'):raise ValueError('The native surface compilation failed.')
    native_index=plan.get('nativeIndex')
    if not isinstance(native_index,dict)or sha(native_index['path'])!=native_index['sha256']:
        raise ValueError('Native surface restoration requires its original source index.')
    index=read(native_index['path'])
    compiled={Path(row['bundlePath']).name:row for row in report.get('bundles')or[report]}
    jobs={row['file']:row for row in plan['rows']};bundles={row['file']:row for row in manifest['bundles']}
    if not jobs or len(jobs)!=len(plan['rows'])or not set(jobs)<=set(bundles):
        raise ValueError('Native surface restoration has missing or repeated resource owners.')
    catalog=output/'Catalog';catalog.mkdir(parents=True)
    inputs=[pin(args.plan),pin(args.report),native_index]
    for original in(Path(__file__),Path(resources.__file__)):
        archive=output/'ProducerSources'/(original.name+'.source');archive.parent.mkdir(exist_ok=True)
        archive.write_bytes(original.read_bytes());inputs.append(pin(archive))
        assembly.setdefault('retainedSourceInputs',[]).append(dict(original=pin(original),archive=pin(archive)))
    proofs={row['file']:row for row in assembly['proofs']};completed=[]
    source_proofs={name:dict(row)for name,row in proofs.items()}
    by_cab={row['newCab']:row for row in source_proofs.values()}
    @lru_cache(maxsize=4)
    def load(cab):
        row=by_cab[cab];path=source/'Catalog'/row['file']
        if sha(path)!=row['sha256']:raise ValueError('A native surface restoration resource changed.')
        return resources.load_serialized_bundle(path,in_memory=True)[1]
    source_catalog=dict(manifest=pin(manifest_path),assembly=pin(source/'ResourceAssembly.json'))
    for name,bundle in bundles.items():
        original=checked_catalog_path(source/'Catalog',name);target=checked_catalog_path(catalog,name)
        if name not in jobs:target.hardlink_to(original);continue
        job=jobs[name]
        if job['sourceSha256']!=bundle['sha256']or sha(original)!=bundle['sha256']:
            raise ValueError('A native surface restoration resource changed.')
        file=resources.load_serialized_bundle(original,in_memory=True)[1]
        target_packets={};target_bindings={};target_palettes={}
        native_sources=native_surface_restoration_sources(index,manifest,source_proofs,job['meshes'],file,load,
            target_packets=target_packets,target_bindings=target_bindings,target_palettes=target_palettes)
        child=compiled[job['compiledFile']];compiled_path=Path(child['bundlePath'])
        if child.get('success')is not True or sha(compiled_path)!=child['bundleSha256']:
            raise ValueError('A native surface compiled bundle changed.')
        compiled_file=resources.load_serialized_bundle(compiled_path,in_memory=True)[1]
        meshes={obj.peek_name():obj for obj in compiled_file.objects.values()if obj.type.name=='Mesh'}
        selected={row['meshPathId']for row in job['meshes']}
        if not selected or len(selected)!=len(job['meshes']):raise ValueError('A native surface mesh owner is missing or repeated.')
        for obj in file.objects.values():
            if obj.type.name!='MonoBehaviour':continue
            deformer=obj.read_typetree().get('deformer')
            pointer=deformer.get('sharedMesh',{})if isinstance(deformer,dict)else{}
            if pointer.get('m_FileID')==0 and pointer.get('m_PathID')in selected and pointer['m_PathID']not in target_bindings:
                raise ValueError('A simulation-bound surface requires its complete deformation owner.')
        replacements=[]
        for mesh in job['meshes']:
            native_pin=mesh['nativePacket']
            if sha(native_pin['path'])!=native_pin['sha256']:raise ValueError('A retained native surface packet changed.')
            with gzip.open(native_pin['path'],'rt',encoding='utf-8')as stream:native=json.load(stream)
            if native_sources[mesh['meshPathId']]!=native:
                raise ValueError('A retained surface differs from its original source mesh.')
            current=export_current_mesh(file.objects[mesh['meshPathId']])
            if current['sourceFingerprint']!=mesh['currentFingerprint']:raise ValueError('A native surface has another current mesh.')
            packet=target_packets[mesh['meshPathId']]
            if 'faceRecovery'in mesh:
                packet_reference=mesh.get('packet')
                if not isinstance(packet_reference,dict)or sha(packet_reference['path'])!=packet_reference['sha256']:raise ValueError('A recovered face packet changed.')
                with gzip.open(packet_reference['path'],'rt',encoding='utf-8')as stream:retained=json.load(stream)
                if retained!=packet:raise ValueError('A recovered face differs from its native authority and retained target.')
            obj=meshes[mesh['name']]
            if export_current_mesh(obj)['sourceFingerprint']!=packet['sourceFingerprint']:
                raise ValueError('A compiled native surface differs from its retained numeric source.')
            replacement=dict(sourceFingerprint=current['sourceFingerprint'],sourcePathIds=[mesh['meshPathId']],
                packet=packet,binding=target_bindings.get(mesh['meshPathId']),compiledTree=obj.read_typetree())
            if 'faceRecovery'in mesh:
                replacement.update(target_palettes[mesh['meshPathId']])
            if mesh['meshPathId']in target_bindings:replacement['nativeClothSource']=mesh['nativeMesh']
            replacements.append(replacement)
        receipt=resources.prepare_standalone_part(original,bundle['sha256'],target,replacements,export_mesh=export_current_mesh)
        receipt.update(restoredSurfaces=job['meshes'],compiledBundle=pin(compiled_path),nativeIndex=native_index,sourceCatalog=source_catalog)
        receipt_path=output/'Receipts'/(name+'.json');write(receipt_path,receipt)
        validate_native_surface_restoration(receipt)
        proof=proofs[name];before=bundle['sha256'];bundle['sha256']=receipt['target']['sha256']
        proof.update(sha256=bundle['sha256'],bytes=target.stat().st_size,allRetainedObjectFieldsExactAfterReferenceRebasing=False)
        proof.setdefault('appearanceFinishing',[]).append(dict(kind='source-native-skin-surface',beforeSha256=before,
            sha256=bundle['sha256'],proof=pin(receipt_path),authority=dict(inputs=inputs)))
        completed.append(dict(file=name,meshes=len(job['meshes']),proof=pin(receipt_path)))
        print('Restored native source surfaces:',name,flush=True)
    write(catalog/RESOURCE_FILES['manifest'],manifest)
    assembly.update(catalogSha256=sha(catalog/RESOURCE_FILES['manifest']),bytes=sum(row['bytes']for row in assembly['proofs']),artAccepted=False,
        preparedSource=dict(assembly=pin(source/'ResourceAssembly.json'),catalog=pin(manifest_path)),
        nativeSkinSurfaces=dict(inputs=inputs,resources=completed,review='pending'))
    assembly.pop('artAcceptance',None);write(output/'ResourceAssembly.json',assembly)
    write(output/'Preparation.json',dict(status='PREPARED_NATIVE_SOURCE_SURFACES_REVIEW_PENDING',resources=completed,catalogSha256=assembly['catalogSha256']))


def prepare_appearance_resources():
    parser=argparse.ArgumentParser();parser.add_argument('--source',type=Path,required=True);parser.add_argument('--native-index',type=Path,required=True)
    parser.add_argument('--census',type=Path,required=True);parser.add_argument('--material-contexts',type=Path,required=True)
    parser.add_argument('--tone-inputs',type=Path,required=True);parser.add_argument('--authoring-sources',type=Path,required=True)
    parser.add_argument('--profiles',type=Path,required=True)
    args=parser.parse_args()
    print(json.dumps(prepare_resource_appearance(args.source,args.native_index,require_workspace(),args.census,args.material_contexts,args.tone_inputs,args.authoring_sources,profiles_path=args.profiles)))


def main():
    global HERE,PUBLIC,p,art,np,UnityPy,MeshHandler,encode_recipe
    parser=argparse.ArgumentParser(description=__doc__,allow_abbrev=False)
    parser.add_argument('--workspace',type=Path,required=True)
    parser.add_argument('--source-directory',type=Path)
    parser.add_argument('command',choices=('inspect-resource-metadata','scope','capture','extract','generate','generate-npc','generate-extremities','generate-skin-boundaries','generate-materials','compile-face-designs','finish-skin-lods','finish-skin-interfaces','repair-skin-interface-cloth','fit-garments','bind-skin-surfaces','refine-skin-surfaces','restore-native-surfaces','complete-groom-frames','retone-materials','prepare-material-codec','verify-material-codec','prepare-resource-spans','prepare','finalize','assemble-resources','repair-resource-preloads','repair-resource-bone-orders','prepare-appearance-resources','refine-native-faces','prepare-groom-designs','prepare-groom-composition','bind-groom-designs','prepare-groom-inputs','refine-groom-frames','prepare-attachment-inputs','bind-attachments','package-resources','plan-resource-retirement','publish'))
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
    action={'inspect-resource-metadata':inspect_resource_metadata,'scope':select_scope,'capture':capture_source_catalog,'extract':extract_current,'generate':generate_humanoids,'generate-npc':generate_npcs,'generate-extremities':generate_extremities,'generate-skin-boundaries':generate_skin_boundaries,
        'generate-materials':generate_materials,'compile-face-designs':compile_face_designs,'finish-skin-lods':finish_skin_lods,'finish-skin-interfaces':prepare_skin_interfaces,'repair-skin-interface-cloth':repair_skin_interface_cloth,'fit-garments':fit_garment_resources,'bind-skin-surfaces':bind_source_skin_materials,'refine-skin-surfaces':prepare_skin_surface_resources,'restore-native-surfaces':restore_native_surface_resources,'complete-groom-frames':complete_groom_frames,'retone-materials':retone_materials,'prepare-material-codec':prepare_material_codec,'verify-material-codec':verify_material_codec,'prepare-resource-spans':prepare_resource_span_catalog,'prepare':prepare_bundles,'finalize':finalize_catalog,'assemble-resources':assemble_resources,'repair-resource-preloads':repair_resource_preloads,'repair-resource-bone-orders':repair_resource_bone_orders,'prepare-appearance-resources':prepare_appearance_resources,'refine-native-faces':prepare_native_face_resources,'prepare-groom-designs':prepare_groom_designs,'prepare-groom-composition':prepare_groom_composition,'bind-groom-designs':bind_groom_designs,'prepare-groom-inputs':prepare_native_groom_inputs,'refine-groom-frames':prepare_groom_frame_resources,'prepare-attachment-inputs':prepare_attachment_inputs,'bind-attachments':bind_attachment_surfaces,'package-resources':package_resources,'plan-resource-retirement':plan_resource_retirement,'publish':publish_catalog}[args.command]
    from resource_storage import resource_work
    ownership=(dict(receipt_flag='--census',metadata_names=('Preparation.json',))
        if args.command=='compile-face-designs' else {})
    with resource_work(args.command,remaining,default_output=HERE,**ownership):action()


if __name__=='__main__':main()
