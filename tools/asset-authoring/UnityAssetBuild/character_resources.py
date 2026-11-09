"""Assemble standalone Unity character resources from prepared prefab inputs.

The input owner prepares geometry and materials. This module also prepares
serialized cloth attachments before closing the object references.
This module closes object references and preserves compressed texture spans.
"""
from pathlib import Path
from collections import defaultdict, deque
from functools import lru_cache
from typing import Any, Callable, cast
import base64
import hashlib
import json
import re
import struct
import UnityPy
from UnityPy.files.SerializedFile import FileIdentifier
from UnityPy.streams import EndianBinaryReader
from mesh_recipe_codec import four_slot_skin, skin_bone_order

def read(path):
    return json.loads(Path(path).read_text(encoding='utf-8-sig'))

def digest(path):
    with Path(path).open('rb') as stream:
        return hashlib.file_digest(stream,'sha256').hexdigest()


def resource_optimizer_identity():
    inputs=[__file__,Path(__file__).with_name('mesh_recipe_codec.py')]
    return hashlib.sha256(''.join(digest(path)for path in inputs).encode()).hexdigest()


def serialized_object_bytes(obj: Any) -> bytes:
    """Return staged serialized edits, or the unchanged source bytes."""
    return obj.data if obj.data is not None else obj.get_raw_data()


def read_staged_object(obj: Any) -> Any:
    """Read staged bytes; UnityPy's ordinary reader still points at the input."""
    import copy
    if obj.data is None:return obj.read()
    staged=copy.copy(obj)
    staged.reader=EndianBinaryReader(obj.data,endian=obj.reader.endian)
    staged.byte_start=0;staged.byte_size=len(obj.data)
    return staged.read()


def texture_mip_tail(tree: dict[str, Any], payload: bytes, maximum: int) -> tuple[dict[str, Any], bytes]:
    """Select existing BC7 mips without decoding or recompressing their texels."""
    if tree['m_TextureFormat'] != 25 or tree['m_ImageCount'] != 1 or tree['m_TextureDimension'] != 2:
        raise ValueError('LOD mip selection requires a single BC7 2D texture.')
    width,height=tree['m_Width'],tree['m_Height'];levels=tree['m_MipCount'];skip=0
    if maximum<=0 or maximum&(maximum-1):raise ValueError('The texture limit must be a positive power of two.')
    expected=sum(max(1,((width>>level)+3)//4)*max(1,((height>>level)+3)//4)*16 for level in range(levels))
    if len(payload)!=expected or tree['m_CompleteImageSize']!=expected:
        raise ValueError('The BC7 mip layout does not match its complete image data.')
    while max(width,height)>maximum and levels>1:
        skip+=max(1,(width+3)//4)*max(1,(height+3)//4)*16
        width=max(1,width//2);height=max(1,height//2);levels-=1
    result=dict(tree,m_Width=width,m_Height=height,m_MipCount=levels,m_CompleteImageSize=len(payload)-skip)
    return result,payload[skip:]


def texture_main_storage(tree: dict[str, Any], payload: bytes, offset: int, texture_format: int) -> dict[str, Any]:
    """Replace complete skin mips, retaining sampler, dimensions and colour space."""
    if tree['m_TextureFormat']not in (4,10,12,25,28,29)or texture_format not in (4,12,25)or tree['m_ImageCount']!=1 or tree['m_TextureDimension']!=2:
        raise ValueError('Skin storage requires one supported source 2D texture.')
    width,height,levels=tree['m_Width'],tree['m_Height'],tree['m_MipCount']
    if min(width,height,levels)<1 or levels>max(width,height).bit_length() or offset<0 or offset%16:
        raise ValueError('Invalid Main mip layout or stream alignment.')
    def image_size(format: int) -> int:
        return sum(max(1,width>>level)*max(1,height>>level)*4 if format==4 else
            max(1,((width>>level)+3)//4)*max(1,((height>>level)+3)//4)*(8 if format==10 else 16) for level in range(levels))
    expected=image_size(texture_format)
    stream=tree['m_StreamData']
    if len(payload)!=expected:
        raise ValueError('Skin storage needs a complete replacement mip chain.')
    if not stream['path']or not stream['size']:
        if not tree.get('image data'):
            raise ValueError('The source skin texture has no complete inline mip chain.')
        return dict(tree,m_TextureFormat=texture_format,m_CompleteImageSize=expected,**{'image data':payload})
    if tree['m_TextureFormat']not in (28,29)and (stream['size']!=image_size(tree['m_TextureFormat'])or tree['m_CompleteImageSize']!=stream['size']):
        raise ValueError('Skin storage needs a complete source mip chain.')
    return dict(tree,m_TextureFormat=texture_format,m_CompleteImageSize=expected,m_StreamData=dict(stream,offset=offset,size=expected))


def texture_rgba32_storage(tree: dict[str, Any], payload: bytes, offset: int) -> dict[str, Any]:
    return texture_main_storage(tree,payload,offset,4)


def reconcile_shared_normal_mips(payloads):
    """Resolve only one-step quantization ties, independently of consumer order."""
    import numpy as np
    if not payloads or len({len(value) for value in payloads}) != 1:
        raise ValueError('Shared normal consumers require equal complete mip layouts.')
    values = [np.frombuffer(value, np.uint8) for value in payloads]
    low = np.minimum.reduce(values).astype(np.uint16)
    high = np.maximum.reduce(values).astype(np.uint16)
    maximum = int((high-low).max(initial=0))
    if maximum > 1:
        raise ValueError('Shared normal consumers require distinct source fields, beyond a one-step quantization tie.')
    result = np.rint((low+high)*.5).astype(np.uint8).tobytes()
    return result, dict(consumers=len(payloads), maximumSourceByteDifference=maximum,
                        reconciledComponents=int(np.count_nonzero(low != high)),
                        maximumComponentChange=maximum, orderIndependent=True)


def replace_owned_texture_mips(source_path, source_sha256, target_path, replacements):
    source, target = Path(source_path), Path(target_path)
    if source.resolve() == target.resolve() or digest(source) != source_sha256:
        raise ValueError('Texture mip replacement requires an immutable source and an independent target.')
    environment, file = load_serialized_bundle(source, in_memory=True)
    before = {key: serialized_object_bytes(obj) for key, obj in file.objects.items()}
    original_refs = [str(item.name) for item in file.externals]
    selected = {}
    for row in replacements:
        identifier = int(row['object'])
        if identifier in selected or identifier not in file.objects or row['cab'] != file.name:
            raise ValueError('A mip target is duplicated or belongs to another serialized resource.')
        obj = file.objects[identifier]
        if obj.type.name != 'Texture2D' or obj.peek_name() != row['name']:
            raise ValueError('A mip target does not match its source texture identity.')
        raw = Path(row['raw']['path'])
        if digest(raw) != row['raw']['sha256']:
            raise ValueError('A complete source mip payload changed.')
        payload = raw.read_bytes()
        storage_format = row.get('storageFormat', 4)
        if type(storage_format) is not int or storage_format not in (4, 12, 25):
            raise ValueError('An owned skin field requires explicit RGBA32, DXT5 or BC7 storage.')
        tree = obj.read_typetree()
        if [tree['m_Width'], tree['m_Height']] != row['dimensions'] or tree['m_MipCount'] != row['mipCount']:
            raise ValueError('A source-owned mip payload has different runtime dimensions.')
        selected[identifier] = (row, payload)
    if not selected:
        raise ValueError('At least one exact texture owner is required.')
    # Only Texture2D stream ranges are rebuilt. Refuse any other object using
    # these stream members instead of guessing its binary ownership.
    textures = {}
    streams = {}
    for key, obj in file.objects.items():
        if obj.type.name != 'Texture2D':
            continue
        tree = obj.read_typetree()
        stream = tree.get('m_StreamData')
        if not stream or not stream['path'] or not stream['size']:
            if key in selected:
                textures[key]=(tree,None,bytes(obj.read().get_image_data()))
            continue
        member = stream['path'].replace('\\', '/').rsplit('/', 1)[-1]
        if member not in environment.file.files:
            raise ValueError('An external texture stream is outside the standalone resource.')
        if member not in streams:
            content = environment.file.files[member]
            content = content.bytes if isinstance(content, EndianBinaryReader) else content
            streams[member] = dict(original=bytes(content), output=bytearray(), aliases={})
        content = streams[member]['original']
        start, size = stream['offset'], stream['size']
        if start < 0 or start+size > len(content):
            raise ValueError('A source texture stream range is incomplete.')
        textures[key] = (tree, member, content[start:start+size])
    if not selected.keys() <= textures.keys():
        raise ValueError('A selected texture has no complete source stream.')
    def foreign_stream(value):
        if isinstance(value, dict):
            if {'path','offset','size'} <= value.keys() and value.get('size', 0):
                if str(value['path']).replace('\\','/').rsplit('/',1)[-1] in streams:
                    return True
            return any(foreign_stream(child) for child in value.values())
        if isinstance(value, list):
            return any(foreign_stream(child) for child in value)
        return False
    for obj in file.objects.values():
        if obj.type.name != 'Texture2D' and foreign_stream(obj.read_typetree()):
            raise ValueError('A non-texture object shares a compacted texture stream.')
    expected = {}
    reports = []
    for key, (tree, member, original) in sorted(textures.items(), key=lambda item:(item[1][1]or'', item[1][0]['m_StreamData']['offset'], item[0])):
        replacement = selected[key][1] if key in selected else original
        if member is None:
            offset=0
        else:
            state = streams[member]
            identity = (tree['m_StreamData']['offset'], len(original), hashlib.sha256(replacement).hexdigest())
            if identity in state['aliases']:
                offset = state['aliases'][identity]
            else:
                state['output'].extend(bytes((-len(state['output'])) % 16))
                offset = len(state['output'])
                state['output'].extend(replacement)
                state['aliases'][identity] = offset
        changed = texture_main_storage(tree, replacement, offset, selected[key][0].get('storageFormat', 4)) if key in selected else dict(
            tree, m_StreamData=dict(tree['m_StreamData'], offset=offset))
        file.objects[key].save_typetree(changed)
        expected[key] = changed
        if key in selected:
            row = selected[key][0]
            reports.append(dict(object=key, name=tree['m_Name'], raw=row['raw'],
                width=tree['m_Width'], height=tree['m_Height'], mipCount=tree['m_MipCount'],
                beforeFormat=tree['m_TextureFormat'], format=changed['m_TextureFormat'], bytes=len(replacement),
                sourcePayloadSha256=hashlib.sha256(original).hexdigest(),
                payloadSha256=hashlib.sha256(replacement).hexdigest()))
    for member, state in streams.items():
        reader = EndianBinaryReader(bytes(state['output']))
        setattr(reader, 'flags', 0)
        environment.file.files[member] = reader
    expected_bytes = {key: serialized_object_bytes(obj) for key, obj in file.objects.items()}
    temporary = target.with_name(target.name+'.writing')
    target.parent.mkdir(parents=True, exist_ok=True)
    try:
        temporary.write_bytes(environment.file.save(packer='lz4'))
        reopened, saved = load_serialized_bundle(temporary, in_memory=True)
        if saved.name != file.name or [str(item.name) for item in saved.externals] != original_refs or set(saved.objects) != set(before):
            raise ValueError('A texture mip replacement changed serialized resource identities.')
        for key, obj in saved.objects.items():
            if serialized_object_bytes(obj) != expected_bytes[key]:
                raise ValueError('A texture mip replacement failed exact saved object reload.')
            if key not in expected and serialized_object_bytes(obj) != before[key]:
                raise ValueError('A texture mip replacement changed an unrelated object.')
            if key in expected:
                tree, member, original = textures[key]
                changed = obj.read_typetree()
                allowed = {'m_TextureFormat','m_CompleteImageSize','m_StreamData'} if key in selected else {'m_StreamData'}
                if member is None:allowed.add('image data')
                if {k:v for k,v in tree.items() if k not in allowed} != {k:v for k,v in changed.items() if k not in allowed}:
                    raise ValueError('A texture mip replacement changed its sampler, dimensions or colour space.')
                payload = bytes(obj.read().get_image_data())
                if payload != (selected[key][1] if key in selected else original):
                    raise ValueError('A texture mip replacement changed complete source or target texels.')
        for member, state in streams.items():
            data = reopened.file.files[member]
            data = data.bytes if isinstance(data, EndianBinaryReader) else data
            if bytes(data) != bytes(state['output']):
                raise ValueError('A compacted texture stream failed its saved reload.')
        if digest(source) != source_sha256:
            raise ValueError('A source texture resource changed while saving.')
        temporary.replace(target)
    finally:
        temporary.unlink(missing_ok=True)
    return dict(file=target.name, beforeSha256=source_sha256, sha256=digest(target),
        source=dict(path=str(source.resolve()),sha256=source_sha256),
        target=dict(path=str(target.resolve()),sha256=digest(target)),
        bytes=target.stat().st_size, textures=reports, nonTextureObjectBytesExact=True,
        unselectedTextureTexelsExact=True, samplersColorSpaceDimensionsExact=True,
        cabAndExternalReferencesUnchanged=True, savedObjectAndMipBytesExact=True,
        streamBytesBefore=sum(len(state['original']) for state in streams.values()),
        streamBytesAfter=sum(len(state['output']) for state in streams.values()),
        unusedTextureStreamSpansRetained=False)


def validate_owned_texture_mips(receipt):
    """Reopen both checkpoints and verify the exact mutation, including untouched data."""
    for name in ('source','target'):
        reference=receipt[name]
        if digest(reference['path'])!=reference['sha256']:
            raise ValueError('An owned skin field checkpoint changed.')
    if receipt['source']['sha256']!=receipt['beforeSha256']or receipt['target']['sha256']!=receipt['sha256']:
        raise ValueError('An owned skin field has a disconnected checkpoint.')
    _,before=load_serialized_bundle(receipt['source']['path'],in_memory=True)
    _,after=load_serialized_bundle(receipt['target']['path'],in_memory=True)
    if before.name!=after.name or set(before.objects)!=set(after.objects)or \
            [str(item.name)for item in before.externals]!=[str(item.name)for item in after.externals]:
        raise ValueError('An owned skin field changed CAB, objects or external references.')
    selected={int(row['object']):row for row in receipt['textures']}
    if not selected or len(selected)!=len(receipt['textures'])or not selected.keys()<=before.objects.keys():
        raise ValueError('An owned skin field has duplicate or missing texture owners.')
    for key,old in before.objects.items():
        new=after.objects[key]
        if old.type.name!='Texture2D':
            if serialized_object_bytes(old)!=serialized_object_bytes(new):
                raise ValueError('An owned skin field changed an unrelated object.')
            continue
        first,last=old.read_typetree(),new.read_typetree()
        allowed={'m_StreamData'}
        if key in selected:allowed|={'m_TextureFormat','m_CompleteImageSize','image data'}
        if {k:v for k,v in first.items()if k not in allowed}!={k:v for k,v in last.items()if k not in allowed}:
            raise ValueError('An owned skin field changed texture identity, sampler, layout or colour space.')
        original=bytes(old.read().get_image_data());actual=bytes(new.read().get_image_data())
        if key not in selected:
            if original!=actual:raise ValueError('An owned skin field changed unselected texels.')
            continue
        row=selected[key];raw=Path(row['raw']['path'])
        if digest(raw)!=row['raw']['sha256']or actual!=raw.read_bytes()or \
                hashlib.sha256(original).hexdigest()!=row['sourcePayloadSha256']or \
                hashlib.sha256(actual).hexdigest()!=row['payloadSha256']or \
                row['bytes']!=len(actual)or row['beforeFormat']!=first['m_TextureFormat']or \
                row['format']!=last['m_TextureFormat']or row['format']not in (4,12,25)or \
                (row['name'],row['width'],row['height'],row['mipCount'])!= \
                (last['m_Name'],last['m_Width'],last['m_Height'],last['m_MipCount']):
            raise ValueError('An owned skin field differs from its complete measured mip payload.')



def texture_mip_images(obj: Any) -> list[Any]:
    """Decode complete Unity mip chains in their serialized row orientation."""
    import numpy as np
    import texture2ddecoder
    from UnityPy.export import Texture2DConverter
    texture=obj.read();payload=texture.get_image_data();format=texture.m_TextureFormat
    crunched=format in (28,29)
    if crunched:format={28:10,29:12}[format]
    if format not in (4,10,12,25):raise ValueError('Unsupported source texture mip storage.')
    width,height,levels=texture.m_Width,texture.m_Height,texture.m_MipCount
    if min(width,height,levels)<1 or levels>max(width,height).bit_length():raise ValueError('Invalid source texture mip layout.')
    images=[];offset=0
    for level in range(levels):
        w,h=max(1,width>>level),max(1,height>>level)
        length=w*h*4 if format==4 else ((w+3)//4)*((h+3)//4)*(8 if format==10 else 16)
        if crunched:
            # The native API decodes one requested level. Its Python stub omits
            # the optional level index; calling it once only returns mip zero.
            unpack=cast(Callable[[bytes,int],bytes],texture2ddecoder.unpack_unity_crunch)
            data=unpack(payload,level)
            if len(data)!=length:raise ValueError('A Crunch source mip has an invalid decoded block count.')
        else:
            if offset+length>len(payload):raise ValueError('Incomplete source texture mip chain.')
            data=payload[offset:offset+length];offset+=length
        image=Texture2DConverter.parse_image_data(data,w,h,format,obj.version,obj.platform,flip=False)
        images.append(np.asarray(image.convert('RGBA'),dtype=np.uint8))
    if not crunched and offset!=len(payload):raise ValueError('Unexpected bytes after the source texture mip chain.')
    return images


def quantize_generated_morphs(tree: dict[str, Any], native_count: int, rest_positions: Any, triangles: Any) -> dict[str, Any]:
    """Bound added-vertex precision while preserving the native prefix and pose topology."""
    import numpy as np
    count=tree['m_VertexData']['m_VertexCount']
    if not 0<native_count<=count:raise ValueError('Invalid native groom prefix size.')
    # Rest positions are supplied by the caller's decoded mesh, not a second
    # serializer or an assumed packed vertex-channel layout.
    positions=np.asarray(rest_positions,dtype=np.float32)
    if positions.shape!=(count,3):raise ValueError('Incomplete groom rest positions.')
    triangles=np.asarray(triangles,dtype=np.int64).reshape(-1,3)
    records=tree['m_Shapes']['vertices'];changed=0;retained=0;maximum_position=0.;maximum_direction=0.
    for shape in tree['m_Shapes']['shapes']:
        rows=records[shape['firstVertex']:shape['firstVertex']+shape['vertexCount']]
        indices=np.asarray([r['index']for r in rows],dtype=np.int64)
        values=np.asarray([[r[field][axis]for field in ('vertex','normal','tangent')for axis in 'xyz']for r in rows],dtype=np.float32).reshape(-1,9)
        if not np.isfinite(values).all():raise ValueError('Nonfinite groom fitting data.')
        selected=indices>=native_count;rounded=values.copy()
        rounded[selected,:3]=np.rint(rounded[selected,:3]*2**22)/2**22
        rounded[selected,3:]=np.rint(rounded[selected,3:]*2**12)/2**12
        before=positions.copy();after=positions.copy()
        before[indices]+=values[:,:3];after[indices]+=rounded[:,:3]
        first=before[triangles];last=after[triangles]
        old_normals=np.cross(first[:,1]-first[:,0],first[:,2]-first[:,0])
        new_normals=np.cross(last[:,1]-last[:,0],last[:,2]-last[:,0])
        invalid=(np.sum(old_normals.astype(float)*new_normals,axis=1)<=0)&np.any(old_normals!=0,axis=1)
        if invalid.any():
            fixed=np.isin(indices,np.unique(triangles[invalid]));rounded[fixed]=values[fixed];retained+=int(np.count_nonzero(fixed&selected))
            after=positions.copy();after[indices]+=rounded[:,:3];last=after[triangles]
            new_normals=np.cross(last[:,1]-last[:,0],last[:,2]-last[:,0])
            if np.any((np.sum(old_normals.astype(float)*new_normals,axis=1)<=0)&np.any(old_normals!=0,axis=1)):
                # Restoring one thin edge can affect its neighbour. Retain this
                # complete fitting frame rather than iterating repairs.
                retained+=int(np.count_nonzero(selected&~fixed));rounded=values.copy()
        if len(values):
            maximum_position=max(maximum_position,float(np.max(np.abs(rounded[:,:3]-values[:,:3]))))
            maximum_direction=max(maximum_direction,float(np.max(np.abs(rounded[:,3:]-values[:,3:]))))
        if not np.array_equal(rounded[~selected],values[~selected]):raise ValueError('Native groom fitting data changed.')
        for row,value,original in zip(rows,rounded,values):
            if np.array_equal(value,original):continue
            changed+=1
            for field,part in zip(('vertex','normal','tangent'),value.reshape(3,3)):
                row[field]=dict(zip('xyz',map(float,part)))
    # Quantized deltas may move a fitting extreme by half a step.
    if changed:
        tree['m_LocalAABB']=dict(tree['m_LocalAABB'],m_Extent={axis:float(value)+2**-22 for axis,value in tree['m_LocalAABB']['m_Extent'].items()})
    return dict(changedRecords=changed,fullPrecisionThinPoseRecords=retained,
        nativePrefixVertices=native_count,nativeFittingPrefixExact=True,allStoredPoseOrientationsPreserved=True,
        maximumPositionComponentErrorMetres=maximum_position,maximumDirectionComponentError=maximum_direction)


def smooth_groom_support(tree: dict[str, Any], mesh: Any) -> dict[str, Any]:
    """Carry core shading in UV0.zw; retain texture XY, fitting and physics data."""
    import numpy as np
    positions=np.asarray(mesh.m_Vertices,dtype=np.float32);normals=np.asarray(mesh.m_Normals,dtype=np.float32)
    tangents=np.asarray(mesh.m_Tangents,dtype=np.float32);count=len(positions)
    uv=tree['m_VertexData']['m_Channels'][4]
    if uv['format']!=0 or uv['dimension']!=2:raise ValueError('Groom shading requires unowned float32 UV0.zw components.')
    if positions.shape!=normals.shape or tangents.shape!=(count,4):raise ValueError('Groom support requires complete native frames.')
    roles=np.asarray(mesh.m_UV2,dtype=np.float32)if mesh.m_UV2 is not None else np.zeros((count,4),dtype=np.float32)
    if roles.shape!=(count,4):raise ValueError('Groom support requires the source role coordinate layout.')
    core=roles[:,2]<.5
    # Join UV seams within the prepared position precision. These keys affect
    # shading only; the native positions and bone weights remain unchanged.
    keys=np.rint(positions.astype(float)*1e6)
    if tree['m_BindPose']:
        weight_slots,index_slots=four_slot_skin(mesh.m_BoneWeights,mesh.m_BoneIndices,count)
        weights=np.asarray(weight_slots,dtype=np.float32).reshape(-1,4);indices=np.asarray(index_slots,dtype=np.int32).reshape(-1,4)
        if weights.shape!=(count,4)or indices.shape!=weights.shape:raise ValueError('Groom support has an incomplete native skin palette: '+tree['m_Name'])
        order=np.argsort(np.where(weights>0,indices,0),axis=1,kind='stable')
        keys=np.column_stack((keys,np.take_along_axis(np.where(weights>0,indices,0),order,axis=1),
                              np.rint(np.take_along_axis(weights,order,axis=1).astype(float)*1e6)))
    triangles=np.asarray([tri for slot in mesh.get_triangles()for tri in slot],dtype=np.int64).reshape(-1,3)
    triangles=triangles[np.all(core[triangles],axis=1)]
    area=np.zeros(count,dtype=float)
    if len(triangles):
        points=positions[triangles];measure=np.linalg.norm(np.cross(points[:,1]-points[:,0],points[:,2]-points[:,0]),axis=1)
        for side in range(3):np.add.at(area,triangles[:,side],measure)
    n=normals/np.maximum(np.linalg.norm(normals,axis=1,keepdims=True),1e-20)
    smoothed=n.astype(float).copy()
    if core.any():
        vertices=np.flatnonzero(core)
        _,groups=np.unique(keys[core],axis=0,return_inverse=True)
        first=np.full(int(groups.max())+1,count,dtype=np.int64)
        np.minimum.at(first,groups,vertices)
        # Coincident front/back surfaces cannot share a shading normal.
        groups=groups*2+(np.sum(n[core]*n[first][groups],axis=1)<0).astype(np.int64)
        group_of=np.full(count,-1,dtype=np.int64);group_of[vertices]=groups
        total=np.zeros((int(groups.max())+1,3))
        np.add.at(total,groups,n[core]*area[core,None])
        average=total/np.maximum(np.linalg.norm(total,axis=1,keepdims=True),1e-20)
        fallback=np.zeros_like(average);np.add.at(fallback,groups,n[core])
        fallback/=np.maximum(np.linalg.norm(fallback,axis=1,keepdims=True),1e-20)
        empty=np.linalg.norm(total,axis=1)<1e-20;average[empty]=fallback[empty]
        edges=np.concatenate([group_of[triangles[:,pair]]for pair in ([0,1],[1,2],[2,0])])
        edges=np.unique(np.sort(edges,axis=1),axis=0);edges=edges[edges[:,0]!=edges[:,1]]
        edges=edges[np.sum(average[edges[:,0]]*average[edges[:,1]],axis=1)>.25]
        degree=np.bincount(edges.ravel(),minlength=len(average)).astype(float)
        original=average.copy()
        for _ in range(8):
            neighbours=np.zeros_like(average)
            np.add.at(neighbours,edges[:,0],average[edges[:,1]])
            np.add.at(neighbours,edges[:,1],average[edges[:,0]])
            value=.5*average+.5*neighbours/np.maximum(degree[:,None],1)
            value/=np.maximum(np.linalg.norm(value,axis=1,keepdims=True),1e-20)
            valid=(degree>0)&(np.sum(value*original,axis=1)>.25)
            average[valid]=value[valid]
        smoothed[core]=average[groups]
    supported=core&(area>0)&(np.sum(smoothed*n,axis=1)>=.25)
    changed_count=int(np.count_nonzero(supported&(np.linalg.norm(smoothed-n,axis=1)>1e-5)))
    proof=dict(coreVertices=int(core.sum()),smoothedVertices=changed_count,nativeVertexValuesExact=True,
        originalMorphsAndSkinningUnchanged=True,normalCarryUv=0,normalCarryComponents='zw',
        nativeUv0XYExact=True,metallicPixelsUseNativeFrame=True)
    t=tangents[:,:3]-n*np.sum(tangents[:,:3]*n,axis=1,keepdims=True)
    t/=np.maximum(np.linalg.norm(t,axis=1,keepdims=True),1e-20)
    b=np.cross(n,t)*tangents[:,3,None]
    field=np.zeros((count,2),dtype='<f4')
    field[supported]=np.column_stack((np.sum(smoothed*t,axis=1),np.sum(smoothed*b,axis=1)))[supported]
    vertex_data=tree['m_VertexData'];channels=vertex_data['m_Channels']
    streams=mesh.get_streams(mesh.src.m_VertexData.m_Channels,count)
    end=max(value.offset+value.stride*count for value in streams)
    raw=bytes(vertex_data['m_DataSize'])
    if not end<=len(raw)<=((end+15)&~15):raise ValueError('A groom vertex stream has undeclared trailing data.')
    # The installed player's skinned path does not transport TEXCOORD4
    # correctly. UV0.zw is verified on both static and skinned renderers.
    # Explicit zero components are required even when no smoothing is needed;
    # a two-component vertex attribute supplies a default W of one to HLSL.
    insertion=uv['offset']+8
    for index,channel in enumerate(channels):
        if index!=4 and channel['dimension'] and channel['stream']==uv['stream'] and channel['offset']>=insertion:
            channel['offset']+=8
    uv['dimension']=4
    output=bytearray()
    for index in sorted({channel['stream']for channel in channels if channel['dimension']}):
        stream=streams[index]
        output.extend(bytes((-len(output))%16))
        values=np.ndarray((count,stream.stride),dtype=np.uint8,buffer=raw,offset=stream.offset)
        if index==uv['stream']:
            values=np.concatenate((values[:,:insertion],field.view(np.uint8).reshape(count,8),values[:,insertion:]),axis=1)
        output.extend(values.tobytes())
    vertex_data['m_DataSize']=bytes(output)
    return proof

def transport_groom_support_to_vertices(tree: dict[str, Any], mesh: Any) -> tuple[dict[str, Any], dict[str, Any]]:
    """Interpolate the support in normal space, retaining native metal recovery data."""
    import numpy as np
    count=tree['m_VertexData']['m_VertexCount'];channels=tree['m_VertexData']['m_Channels']
    for index,dimension in ((1,3),(2,4)):
        if channels[index]['format']!=0 or channels[index]['dimension']!=dimension:
            raise ValueError('Vertex groom support requires complete float32 source frames and UV0.zw.')
    if channels[4]['format']!=0 or channels[4]['dimension']not in (2,4):
        raise ValueError('Vertex groom support requires float32 source UV coordinates.')
    def unit(v):return v/np.maximum(np.linalg.norm(v,axis=1,keepdims=True),1e-20)
    normals=np.asarray(mesh.m_Normals,dtype=np.float64)
    tangents=np.asarray(mesh.m_Tangents,dtype=np.float64)
    n=unit(normals);t=unit(tangents[:,:3]-n*np.sum(tangents[:,:3]*n,axis=1,keepdims=True))
    b=np.cross(n,t)*tangents[:,3,None]
    uv=np.asarray(mesh.m_UV0,dtype=np.float64);roles=np.asarray(mesh.m_UV2,dtype=np.float64)
    if uv.shape!=(count,channels[4]['dimension'])or roles.shape!=(count,4):raise ValueError('Missing groom source coordinates.')
    xy=uv[:,2:4]if uv.shape[1]==4 else np.zeros((count,2),dtype=np.float64)
    if not np.isfinite(xy).all()or np.any(np.sum(xy*xy,axis=1)>1.00001):raise ValueError('Invalid source support hemisphere.')
    changed=(roles[:,2]<.5)&(np.sum(xy*xy,axis=1)>1e-12)
    target=unit(t*xy[:,:1]+b*xy[:,1:2]+n*np.sqrt(np.maximum(0,1-np.sum(xy*xy,axis=1,keepdims=True))))
    target[~changed]=n[~changed]
    axis=np.cross(n,target);cos=np.sum(n*target,axis=1)
    def rotate(v,indices=None):
        a=axis if indices is None else axis[indices]
        c=cos if indices is None else cos[indices]
        return v+np.cross(a,v)+np.cross(a,np.cross(a,v))/np.maximum(1+c[:,None],1e-12)
    new_normals=normals.copy();new_normals[changed]=rotate(normals)[changed]
    new_tangents=tangents.copy();new_tangents[changed,:3]=rotate(tangents[:,:3])[changed]
    new_n=unit(new_normals);new_t=unit(new_tangents[:,:3]-new_n*np.sum(new_tangents[:,:3]*new_n,axis=1,keepdims=True))
    new_b=np.cross(new_n,new_t)*new_tangents[:,3,None]
    # Only metallic source pixels use this inverse transport in the shader.
    # Hair receives its final support through the ordinary interpolated TBN.
    inverse=np.zeros((count,2),dtype=np.float64)
    inverse[changed]=np.column_stack((np.sum(n*new_t,axis=1),np.sum(n*new_b,axis=1)))[changed]
    raw=bytearray(tree['m_VertexData']['m_DataSize'])
    streams=mesh.get_streams(mesh.src.m_VertexData.m_Channels,count)
    updates=[(1,new_normals,3),(2,new_tangents,4)]
    if uv.shape[1]==4:updates.append((4,np.column_stack((uv[:,:2],inverse)),4))
    for channel,value,width in updates:
        layout=channels[channel];stream=streams[layout['stream']]
        output=np.ndarray((count,width),dtype='<f4',buffer=raw,offset=stream.offset+layout['offset'],strides=(stream.stride,4))
        output[changed]=value[changed]
    tree['m_VertexData']['m_DataSize']=bytes(raw)
    records=tree['m_Shapes']['vertices']
    for key in ('normal','tangent'):
        if not records:continue
        indices=np.asarray([v['index']for v in records],dtype=np.int64)
        values=np.asarray([[v[key][k]for k in 'xyz']for v in records],dtype=np.float64)
        updated=rotate(values,indices)
        for ordinal in np.flatnonzero(changed[indices]):records[ordinal][key]=dict(zip('xyz',map(float,updated[ordinal])))
    binding=dict(changed=changed,sourceNormals=n,sourceTangents=t,sourceBitangents=b,
                 targetNormals=unit(new_normals),targetTangents=unit(new_tangents[:,:3]),hand=tangents[:,3])
    return dict(changedVertices=int(changed.sum()),vertexCount=count,positionsSkinUvXYAndIndicesExact=True,
                nativeMetalFrameRecoverable=True,physicalPositionFramesExact=True),binding


def transport_groom_cloth_frames(trees: dict[int, dict[str, Any]], bindings: dict[int, dict[str, Any]]) -> list[int]:
    """Carry the same normal rotation through serialized cloth render attachments."""
    import numpy as np
    changed=set()
    for key,tree in trees.items():
        deformer=tree.get('deformer')
        if not isinstance(deformer,dict)or 'renderDeformerList'not in deformer:continue
        data_key=deformer['meshData']['m_PathID'];data=trees[data_key]
        for ordinal,pointer in enumerate(deformer['renderDeformerList']):
            if not pointer['m_PathID']:continue
            render=trees[pointer['m_PathID']]['deformer'];binding=bindings.get(render['sharedMesh']['m_PathID'])
            if binding is None:continue
            child=data['childDataList'][ordinal]
            if child['vertexCount']!=len(binding['changed']):raise ValueError('Cloth support belongs to another render mesh.')
            for vertex in np.flatnonzero(binding['changed']):
                packed=child['vertexInfoList'][vertex];count,start=packed>>28,packed&0x0fffffff
                normal=binding['sourceNormals'][vertex];tangent=binding['sourceTangents'][vertex];bitangent=binding['sourceBitangents'][vertex]
                coordinates=[np.array([np.dot(value,tangent),np.dot(value,bitangent),np.dot(value,normal)])
                             for value in (binding['targetNormals'][vertex],binding['targetTangents'][vertex])]
                for weight in child['vertexWeightList'][start:start+count]:
                    n=np.asarray([weight['localNor'][k]for k in 'xyz']);n/=max(np.linalg.norm(n),1e-20)
                    t=np.asarray([weight['localTan'][k]for k in 'xyz']);t-=n*np.dot(n,t);t/=max(np.linalg.norm(t),1e-20)
                    b=np.cross(n,t)*binding['hand'][vertex]
                    for key2,coefficient in zip(('localNor','localTan'),coordinates):
                        value=t*coefficient[0]+b*coefficient[1]+n*coefficient[2]
                        value/=max(np.linalg.norm(value),1e-20)
                        weight[key2]=dict(zip('xyz',map(float,value)))
                    changed.add(data_key)
    return sorted(changed)


def bake_groom_vertex_flow(tree: dict[str, Any], mesh: Any, binding: dict[str, Any] | None=None, *, replace_owned: bool=False) -> dict[str, Any]:
    """Interpolate measured fiber flow in the skinned tangent frame across UV seams."""
    import numpy as np
    channels=tree['m_VertexData']['m_Channels'];count=tree['m_VertexData']['m_VertexCount']
    replacing=bool(channels[8]['dimension'])
    if replacing and not replace_owned:
        raise ValueError('Groom fiber flow requires unowned UV4 coordinates.')
    if replacing and(channels[8]['dimension']!=2 or channels[8]['format']!=0):
        raise ValueError('The owned groom fiber channel has another numeric format.')
    positions=np.asarray(mesh.m_Vertices,dtype=np.float64)
    coordinates=np.asarray(mesh.m_UV2,dtype=np.float64)
    triangles=np.asarray([t for slot in mesh.get_triangles()for t in slot],dtype=np.int64).reshape(-1,3)
    points=positions[triangles];uv=coordinates[triangles,:2]
    first=points[:,1]-points[:,0];second=points[:,2]-points[:,0]
    du=uv[:,1]-uv[:,0];dv=uv[:,2]-uv[:,0]
    determinant=du[:,0]*dv[:,1]-du[:,1]*dv[:,0]
    flow=(-first*dv[:,:1]+second*du[:,:1])*np.sign(determinant)[:,None]
    length=np.linalg.norm(flow,axis=1)
    area=np.linalg.norm(np.cross(first,second),axis=1)
    valid=(abs(determinant)>1e-20)&(length>1e-20)&(area>1e-20)
    flow/=np.maximum(length[:,None],1e-20)
    # A fiber has no polarity. Average its outer product, so choosing the first
    # incident triangle cannot change the axis at a UV seam or a curved lock.
    keys=np.column_stack((np.rint(positions*1e6),coordinates[:,2]))
    if tree['m_BindPose']:
        weights,indices=four_slot_skin(mesh.m_BoneWeights,mesh.m_BoneIndices,count)
        weights=np.asarray(weights).reshape(-1,4);indices=np.asarray(indices).reshape(-1,4)
        order=np.argsort(np.where(weights>0,indices,0),axis=1,kind='stable')
        keys=np.column_stack((keys,np.take_along_axis(np.where(weights>0,indices,0),order,axis=1),
                              np.rint(np.take_along_axis(weights,order,axis=1)*1e6)))
    _,groups=np.unique(keys,axis=0,return_inverse=True)
    group_count=int(groups.max())+1
    total=np.zeros((group_count,3,3));outer=flow[valid,:,None]*flow[valid,None,:]
    for side in range(3):
        group=groups[triangles[valid,side]]
        np.add.at(total,group,outer*area[valid,None,None])
    # UV charts describe texture placement, while the authored fiber curves
    # describe the actual comb direction. Their physical tangents guide the
    # opaque support independently of distorted or rotated support UV charts.
    roles=coordinates[triangles,2]
    fibers=valid&np.all((roles>=.5)&(roles<64.5),axis=1)
    core_groups=np.unique(groups[coordinates[:,2]<.5]);curve_guided=0
    if fibers.any()and len(core_groups):
        centers=points[fibers].mean(axis=1);fiber_flow=flow[fibers];fiber_area=area[fibers]
        _,first_vertices=np.unique(groups,return_index=True)
        queries=positions[first_vertices[core_groups]];neighbors=min(32,len(centers))
        for start in range(0,len(queries),128):
            query=queries[start:start+128]
            distances=np.sum((query[:,None,:]-centers[None,:,:])**2,axis=2)
            nearest=np.argpartition(distances,neighbors-1,axis=1)[:,:neighbors]
            squared=np.take_along_axis(distances,nearest,axis=1)
            radius=np.maximum(squared.max(axis=1,keepdims=True),1e-20)
            weights=fiber_area[nearest]*np.exp(-2*squared/radius)
            weights/=weights.sum(axis=1,keepdims=True)
            axes=fiber_flow[nearest]
            total[core_groups[start:start+len(query)]]=np.einsum('nk,nki,nkj->nij',weights,axes,axes)
        curve_guided=len(core_groups)
    if binding is None:
        normal=np.asarray(mesh.m_Normals,dtype=np.float64)
        normal/=np.maximum(np.linalg.norm(normal,axis=1,keepdims=True),1e-20)
        native_tangents=np.asarray(mesh.m_Tangents,dtype=np.float64)
        binding=dict(targetNormals=normal,targetTangents=native_tangents[:,:3],hand=native_tangents[:,3])
    normal=binding['targetNormals'];tangent=binding['targetTangents']
    tangent=tangent-normal*np.sum(tangent*normal,axis=1,keepdims=True)
    tangent/=np.maximum(np.linalg.norm(tangent,axis=1,keepdims=True),1e-20)
    bitangent=np.cross(normal,tangent)*binding['hand'][:,None]
    field=total[groups]
    a=np.einsum('ni,nij,nj->n',tangent,field,tangent)
    d=np.einsum('ni,nij,nj->n',bitangent,field,bitangent)
    c=np.einsum('ni,nij,nj->n',tangent,field,bitangent)
    eigenvalue=.5*(a+d+np.hypot(a-d,2*c))
    values=np.column_stack((np.where(a>=d,eigenvalue-d,c),np.where(a>=d,c,eigenvalue-a)))
    lengths=np.linalg.norm(values,axis=1);values/=np.maximum(lengths[:,None],1e-20)
    values[lengths<1e-20]=(0,1)
    if not np.isfinite(values).all():raise ValueError('The groom fiber flow is nonfinite.')
    # UV3 belongs to the runtime GPU surface projection. UV4 is constant in the
    # local TBN, so both skinning and cloth can rotate the
    # frame normally. An object-space direction attribute would remain unskinned.
    streams=mesh.get_streams(mesh.src.m_VertexData.m_Channels,count)
    raw=bytes(tree['m_VertexData']['m_DataSize'])
    if replacing:
        channel=channels[8];stream=streams[channel['stream']];output=bytearray(raw)
        stored=np.ndarray((count,2),dtype='<f4',buffer=output,offset=stream.offset+channel['offset'],strides=(stream.stride,4))
        stored[:]=values;tree['m_VertexData']['m_DataSize']=bytes(output)
        return dict(vertexCount=count,measuredVertices=int(np.count_nonzero(a+d>1e-20)),
            coordinateChannel='UV4.xy',axisAverage='area-weighted-tangent-plane-tensor',replacedOwnedFlow=True,
            curveGuidedSupportGroups=curve_guided,skinnedTangentFrame=True,sourceUvXYAndTopologyExact=True)
    stream_index=channels[4]['stream'];insertion=channels[4]['offset']+channels[4]['dimension']*4
    for channel in channels:
        if channel['dimension']and channel['stream']==stream_index and channel['offset']>=insertion:
            channel['offset']+=8
    channels[8].update(stream=stream_index,offset=insertion,format=0,dimension=2)
    output=bytearray()
    for index in sorted({c['stream']for c in channels if c['dimension']}):
        stream=streams[index];output.extend(bytes((-len(output))%16))
        data=np.ndarray((count,stream.stride),dtype=np.uint8,buffer=raw,offset=stream.offset)
        if index==stream_index:
            data=np.concatenate((data[:,:insertion],values.astype('<f4').view(np.uint8).reshape(count,8),data[:,insertion:]),axis=1)
        output.extend(data.tobytes())
    tree['m_VertexData']['m_DataSize']=bytes(output)
    return dict(vertexCount=count,measuredVertices=int(np.count_nonzero(lengths>1e-20)),
                coordinateChannel='UV4.xy',axisAverage='area-weighted-tangent-plane-tensor',curveGuidedSupportGroups=curve_guided,skinnedTangentFrame=True,sourceUvXYAndTopologyExact=True)


def pack_groom_gpu_frame(tree: dict[str,Any],mesh: Any,source_format: int=1) -> dict[str,Any]:
    """Bridge full-precision authoring flow through UV0 ZW for player skinning."""
    import numpy as np
    if source_format not in (0,1,2):raise ValueError('Unknown source groom GPU frame format.')
    channels=tree['m_VertexData']['m_Channels'];count=tree['m_VertexData']['m_VertexCount']
    source,flow=channels[4],channels[8]
    if (source['format'],source['dimension'],flow['format'],flow['dimension'])!=(0,4,0,2):
        raise ValueError('Packed GPU flow requires its owned float32 source ZW and authoring UV4.')
    streams=mesh.get_streams(mesh.src.m_VertexData.m_Channels,count)
    raw=bytearray(tree['m_VertexData']['m_DataSize']);stream=streams[source['stream']]
    coordinates=np.ndarray((count,4),'<f4',buffer=raw,offset=stream.offset+source['offset'],strides=(stream.stride,4))
    stream=streams[flow['stream']]
    axes=np.ndarray((count,2),'<f4',buffer=raw,offset=stream.offset+flow['offset'],strides=(stream.stride,4))
    carry=coordinates[:,2:4].astype(float).copy()
    if source_format==2:
        if not np.isfinite(carry).all()or np.any((carry<0)|(carry>16777215)|(carry!=np.rint(carry))):
            raise ValueError('A packed source groom frame has a non-integer or out-of-range word.')
        high=np.floor(carry[:,0]/4096);carry=np.c_[carry[:,0]-high*4096,high]*(2/4095)-1
    def pack(pair):
        if not np.isfinite(pair).all()or np.max(np.abs(pair),initial=0)>1.00001:
            raise ValueError('A GPU groom frame escaped its signed unit domain.')
        quantized=np.rint((np.clip(pair,-1,1)+1)*(.5*4095)).astype(np.uint32)
        return(quantized[:,0]+quantized[:,1]*4096).astype('<f4')
    coordinates[:,2]=pack(carry);coordinates[:,3]=pack(axes)
    tree['m_VertexData']['m_DataSize']=bytes(raw)
    return dict(storage='signed-pairs-12-in-source-uv0-zw',format=2,quantizationBits=12,
        maximumComponentError=1/4095,sourceUvXYExact=True,authoringFlowUv4Exact=True,
        sourceNormalCarrySha256=hashlib.sha256(carry.astype('<f4').tobytes()).hexdigest())


def groom_material_identifiers(file: Any) -> set[int]:
    """Serialized groom roles are independent of non-unique material names."""
    return {key for key,obj in file.objects.items()if obj.type.name=='Material'and
            '_CoreResponse'in dict(obj.read_typetree()['m_SavedProperties']['m_Floats'])}


def groom_mesh_identifiers(file: Any, frame_format: int | None=None, *, flow_format: int | None=None) -> set[int]:
    """Resolve groom ownership from renderer materials before touching streams."""
    materials=set()
    for key in groom_material_identifiers(file):
        obj=file.objects[key]
        values=dict(obj.read_typetree()['m_SavedProperties']['m_Floats'])
        value=values.get('_SupportInVertexFrame',0)
        if value not in (0,1):raise ValueError('Unknown groom vertex-frame format.')
        flow=values.get('_FlowInVertexFrame',0)
        if flow not in (0,1,2):raise ValueError('Unknown groom fiber-flow format.')
        if (frame_format is None or value==frame_format)and(flow_format is None or flow==flow_format):materials.add(key)
    filters={};result=set()
    for obj in file.objects.values():
        if obj.type.name=='MeshFilter':
            tree=obj.read_typetree();filters[tree['m_GameObject']['m_PathID']]=tree['m_Mesh']
    for obj in file.objects.values():
        if obj.type.name not in ('SkinnedMeshRenderer','MeshRenderer'):continue
        tree=obj.read_typetree()
        if not any(pointer['m_FileID']==0 and pointer['m_PathID']in materials for pointer in tree['m_Materials']):continue
        pointer=tree.get('m_Mesh')or filters.get(tree['m_GameObject']['m_PathID'])
        if pointer and pointer['m_FileID']==0 and pointer['m_PathID']:result.add(pointer['m_PathID'])
    return result




def groom_lod_plan(file: Any) -> dict[str, Any]:
    """Follow actual LOD groups so an untextured native placeholder cannot disappear from coverage."""
    owned=groom_mesh_identifiers(file,1,flow_format=2);renderers={};groups=[]
    for obj in file.objects.values():
        if obj.type.name=='SkinnedMeshRenderer':
            tree=obj.read_typetree();mesh=tree['m_Mesh']
            if mesh['m_FileID']==0 and mesh['m_PathID']:renderers[obj.path_id]=tree
        elif obj.type.name=='LODGroup':groups.append(obj.read_typetree()['m_LODs'])
    def placeholder(key):
        tree=file.objects[key].read_typetree();props=tree['m_SavedProperties']
        return not any(entry['m_Texture']['m_PathID']for _,entry in props['m_TexEnvs'])and dict(props['m_Floats']).get('_Metallic',0)<=.5
    donors={};materials={};all_meshes=set(owned)
    for group in groups:
        levels=[]
        for level in group:
            pointers=level['renderers']
            if len(pointers)!=1 or pointers[0]['renderer']['m_FileID']or pointers[0]['renderer']['m_PathID']not in renderers:levels.append(None);continue
            levels.append(renderers[pointers[0]['renderer']['m_PathID']])
        prepared=[(i,renderer)for i,renderer in enumerate(levels)if renderer and renderer['m_Mesh']['m_PathID']in owned]
        if not prepared:continue
        first,reference=prepared[0];source_materials=reference['m_Materials']
        if any(p['m_FileID']or p['m_PathID']not in groom_material_identifiers(file)for p in source_materials):continue
        for renderer in levels[first+1:]:
            if not renderer:continue
            key=renderer['m_Mesh']['m_PathID']
            if key in owned:continue
            target_materials=renderer['m_Materials']
            if len(target_materials)!=len(source_materials)or any(p['m_FileID']or not p['m_PathID']or not placeholder(p['m_PathID'])for p in target_materials):continue
            all_meshes.add(key);donors[key]=reference['m_Mesh']['m_PathID']
            for before,after in zip(target_materials,source_materials):
                previous=materials.setdefault(before['m_PathID'],after['m_PathID'])
                if previous!=after['m_PathID']:raise ValueError('One native placeholder has conflicting prepared LOD material owners.')
    for target in materials:
        consumers={r['m_Mesh']['m_PathID']for r in renderers.values()if any(p['m_FileID']==0 and p['m_PathID']==target for p in r['m_Materials'])}
        if not consumers<=set(donors):raise ValueError('A lower-LOD placeholder is shared with another material responsibility.')
    return dict(meshes=all_meshes,donors=donors,materials=materials,renderers={r['m_Mesh']['m_PathID']:r for r in renderers.values()})


def validate_groom_frame_coverage(file: Any) -> dict[str, Any]:
    """Every renderer using transported groom materials needs its full GPU carrier."""
    plan=groom_lod_plan(file);ids=plan['meshes'];missing=[]
    if plan['materials']:raise ValueError('A groom LOD still uses an untextured native placeholder: '+str(sorted(plan['materials'])))
    for key in sorted(ids):
        tree=file.objects[key].read_typetree();channels=tree['m_VertexData']['m_Channels']
        if any((channels[index]['format'],channels[index]['dimension'])!=(0,width)for index,width in((4,4),(6,4),(8,2))):missing.append(dict(object=key,name=tree['m_Name']))
    if missing:raise ValueError('A groom material has incomplete renderer/LOD frame coverage: '+str(missing))
    return dict(meshes=len(ids),allDeclaredRendererFramesComplete=True)


def append_groom_domain(tree: dict[str, Any],mesh: Any,coordinates: Any) -> None:
    """Add the declared free UV2 domain while retaining every original stream byte."""
    import numpy as np
    channels=tree['m_VertexData']['m_Channels'];count=tree['m_VertexData']['m_VertexCount'];values=np.asarray(coordinates,dtype='<f4')
    if values.shape!=(count,4)or not np.isfinite(values).all()or np.any(values[:,2]!=0):raise ValueError('A missing native-core domain has invalid coordinates or roles.')
    if channels[6]['dimension']or(channels[4]['format'],channels[4]['dimension'])!=(0,2):raise ValueError('A native core domain must use unowned UV2 and UV0 ZW.')
    streams=mesh.get_streams(mesh.src.m_VertexData.m_Channels,count);stream_index=channels[4]['stream'];insertion=channels[4]['offset']+8
    raw=bytes(tree['m_VertexData']['m_DataSize'])
    for channel in channels:
        if channel['dimension']and channel['stream']==stream_index and channel['offset']>=insertion:channel['offset']+=16
    channels[6].update(stream=stream_index,offset=insertion,format=0,dimension=4)
    output=bytearray()
    for index in sorted({c['stream']for c in channels if c['dimension']}):
        stream=streams[index];output.extend(bytes((-len(output))%16));data=np.ndarray((count,stream.stride),dtype=np.uint8,buffer=raw,offset=stream.offset)
        if index==stream_index:data=np.concatenate((data[:,:insertion],values.view(np.uint8).reshape(count,16),data[:,insertion:]),axis=1)
        output.extend(data.tobytes())
    tree['m_VertexData']['m_DataSize']=bytes(output)


def refine_groom_resource_frames(job: dict[str, Any]) -> dict[str, Any]:
    """Save one bounded, resumable groom frame update without re-encoding textures."""
    from UnityPy.helpers.MeshHelper import MeshHandler
    source=Path(job['source']);target=Path(job['target']);receipt=Path(job['receipt'])
    identity=dict(sourceSha256=job['sha256'],codeSha256=job['codeSha256'])
    flow_only=job.get('flowOnly',False)
    missing_only=job.get('missingOnly',False)
    coordinates={row['object']:row for row in job.get('coordinates',[])}
    if missing_only:identity.update(missingOnly=True,coordinates=job['coordinates'],materialSources=job.get('materialSources',[]))
    gpu_bridge=job.get('gpuBridge',False)
    if flow_only:identity['flowOnly']=True
    if gpu_bridge:identity['gpuBridge']=True
    if receipt.exists():
        prior=read(receipt)
        if prior['identity']!=identity:raise ValueError('Groom frame output belongs to other source inputs.')
        if target.exists()and digest(target)==prior['sha256']:return prior
    if digest(source)!=job['sha256']:raise ValueError('Groom frame source changed.')
    env,file=load_serialized_bundle(source,in_memory=True)
    streams={name:hashlib.sha256(member.bytes).hexdigest()for name,member in env.file.files.items()if name!=file.name}
    before={key:hashlib.sha256(serialized_object_bytes(obj)).hexdigest()for key,obj in file.objects.items()}
    material_copies=job.get('materialSources',[])
    if missing_only:
        import copy
        plan=groom_lod_plan(file)
        if {r['object']:r['source']for r in material_copies}!=plan['materials']:raise ValueError('The native LOD material completion differs from its actual group owner.')
        for row in material_copies:
            obj=file.objects[row['object']];before_material=obj.read_typetree();tree=copy.deepcopy(file.objects[row['source']].read_typetree());tree['m_Name']=before_material['m_Name'];obj.save_typetree(tree)
    # UnityPy's ordinary reader still sees source bytes after save_typetree.
    # Include the verified LOD owners explicitly until the saved bundle is reopened.
    groom_ids=groom_mesh_identifiers(file)|set(coordinates)
    material_ids=groom_material_identifiers(file)
    source_formats={dict(file.objects[key].read_typetree()['m_SavedProperties']['m_Floats']).get('_FlowInVertexFrame',0)for key in material_ids}
    if len(source_formats)!=1:raise ValueError('A groom resource has conflicting source flow formats.')
    source_format=int(next(iter(source_formats)))
    material_ids|={row['object']for row in material_copies}
    if flow_only and(groom_ids!=groom_mesh_identifiers(file,1)or source_format not in (1,2)):
        raise ValueError('A fiber-only update requires an existing owned groom frame and flow field.')
    changed={row['object']:file.objects[row['object']].data for row in material_copies};bindings={};meshes=[];materials=[]
    for key,obj in file.objects.items():
        if obj.type.name=='Mesh'and key in groom_ids:
            tree=obj.read_typetree();channels=tree['m_VertexData']['m_Channels']
            if missing_only and channels[6]['dimension']==4:continue
            helper=MeshHandler(obj.read());helper.process()
            if channels[6]['dimension']!=4:
                if not missing_only or key not in coordinates:raise ValueError('A groom renderer has no declared role/flow domain; incomplete LOD coverage cannot be skipped.')
                import numpy as np
                pin=coordinates[key]['coordinates']
                if digest(pin['path'])!=pin['sha256']:raise ValueError('The prepared missing groom domain changed.')
                append_groom_domain(tree,helper,np.fromfile(pin['path'],dtype='<f4').reshape(-1,4))
                obj.save_typetree(tree);helper=MeshHandler(read_staged_object(obj));helper.process()
            if flow_only:
                proof=dict(fiberFlow=bake_groom_vertex_flow(tree,helper,replace_owned=True))
            else:
                support=None
                if channels[4]['dimension']==2:
                    support=smooth_groom_support(tree,helper)
                    obj.save_typetree(tree)
                    helper=MeshHandler(read_staged_object(obj));helper.process()
                proof,binding=transport_groom_support_to_vertices(tree,helper)
                if support is not None:proof['supportPreparation']=support
                proof['fiberFlow']=bake_groom_vertex_flow(tree,helper,binding);bindings[key]=binding
            if gpu_bridge:
                obj.save_typetree(tree);staged=MeshHandler(read_staged_object(obj));staged.process()
                proof['gpuFrame']=pack_groom_gpu_frame(tree,staged,source_format if flow_only else 1)
            obj.save_typetree(tree);changed[key]=obj.data
            meshes.append(dict(mesh=tree['m_Name'],object=key,**proof))
        elif key in material_ids:
            if missing_only or(flow_only and not gpu_bridge):continue
            tree=obj.read_typetree();properties=tree['m_SavedProperties'];floats=dict(properties['m_Floats'])
            if not flow_only and floats.get('_SupportInVertexFrame',0)!=0:raise ValueError('The source groom is already transported.')
            floats['_SupportInVertexFrame']=1.;floats['_FlowInVertexFrame']=2. if gpu_bridge else 1.;properties['m_Floats']=list(floats.items())
            obj.save_typetree(tree);changed[key]=obj.data;materials.append(tree['m_Name'])
    if not meshes or(not flow_only and not missing_only and not materials):raise ValueError('A declared groom resource has no source support or owning material.')
    if missing_only and{row['object']for row in meshes}!=set(coordinates):raise ValueError('The missing groom domains do not cover every declared LOD.')
    trees={key:obj.read_typetree()for key,obj in file.objects.items()if obj.type.name=='MonoBehaviour'}
    cloth=transport_groom_cloth_frames(trees,bindings)
    for key in cloth:file.objects[key].save_typetree(trees[key]);changed[key]=file.objects[key].data
    temporary=target.with_suffix('.writing');temporary.write_bytes(env.file.save(packer='lz4'))
    saved_env,saved=load_serialized_bundle(temporary,in_memory=True)
    saved_streams={name:hashlib.sha256(member.bytes).hexdigest()for name,member in saved_env.file.files.items()if name!=saved.name}
    if streams!=saved_streams:raise ValueError('A groom texture or resource stream changed during frame transport.')
    if saved.name!=file.name or set(saved.objects)!=set(file.objects):raise ValueError('Groom frame update changed serialized ownership.')
    for key,obj in saved.objects.items():
        payload=serialized_object_bytes(obj)
        if key in changed:
            if payload!=changed[key]:raise ValueError('A groom frame object failed its saved roundtrip.')
        elif hashlib.sha256(payload).hexdigest()!=before[key]:raise ValueError('An unrelated groom object changed.')
    if digest(source)!=job['sha256']:raise ValueError('A groom source changed while saving.')
    temporary.replace(target)
    result=dict(file=target.name,identity=identity,sha256=digest(target),bytes=target.stat().st_size,
                source=dict(path=str(source.resolve()),sha256=job['sha256']),
                target=dict(path=str(target.resolve()),sha256=digest(target)),
                meshes=meshes,clothObjects=cloth,materials=materials,materialObjects=sorted(material_ids),unrelatedObjectBytesExact=True,
                textureEncodingAndTopologyUnchanged=True,resourceStreamsExact=streams,bundleCompression='lz4')
    if missing_only:result.update(missingOnly=True,coordinates=job['coordinates'],materialSources=material_copies)
    if flow_only:result['flowOnly']=True
    if gpu_bridge:result['gpuBridge']=True
    receipt.parent.mkdir(parents=True,exist_ok=True)
    receipt.write_text(json.dumps(result,separators=(',',':'))+'\n',encoding='utf-8')
    return result


def write_mesh_frame(obj: Any,packet: dict[str, Any]) -> dict[str, Any]:
    """Replace the declared mesh frame while retaining all other vertex streams."""
    import numpy as np
    from UnityPy.helpers.MeshHelper import MeshHandler
    tree=obj.read_typetree();helper=MeshHandler(obj.read());helper.process()
    count=packet['vertexCount'];raw=bytearray(tree['m_VertexData']['m_DataSize'])
    if count!=tree['m_VertexData']['m_VertexCount']:
        raise ValueError('A prepared mesh frame has different vertex correspondence.')
    vertex_data=helper.src.m_VertexData
    if vertex_data is None or vertex_data.m_Channels is None:raise ValueError('A prepared mesh has no vertex-channel layout.')
    streams=helper.get_streams(vertex_data.m_Channels,count)
    for channel,key,width in [(0,'vertices',3),(1,'normals',3),(2,'tangents',4)]:
        layout=tree['m_VertexData']['m_Channels'][channel]
        if layout['format']!=0 or layout['dimension']!=width:raise ValueError('Expected a complete prepared float32 facial frame.')
        stream=streams[layout['stream']];view=np.ndarray((count,width),dtype='<f4',buffer=raw,
            offset=stream.offset+layout['offset'],strides=(stream.stride,4))
        view[:]=np.asarray(packet[key],np.float32).reshape(count,width)
    tree['m_VertexData']['m_DataSize']=bytes(raw)
    records=[];ordinal=0
    for channel in packet['shapes']:
        for frame in channel['frames']:
            definition=tree['m_Shapes']['shapes'][ordinal];definition['firstVertex']=len(records)
            values=np.stack([np.asarray(frame[key],np.float32).reshape(count,3)for key in ('vertices','normals','tangents')],axis=1)
            if not definition['hasNormals']:values[:,1]=0
            if not definition['hasTangents']:values[:,2]=0
            for index in np.flatnonzero(np.any(values!=0,axis=(1,2))):
                records.append(dict(index=int(index),**{field:dict(zip('xyz',map(float,value)))for field,value in zip(('vertex','normal','tangent'),values[index])}))
            definition['vertexCount']=len(records)-definition['firstVertex'];ordinal+=1
    tree['m_Shapes']['vertices']=records
    tree['m_LocalAABB']=dict(m_Center=dict(zip('xyz',packet['bounds'][:3])),m_Extent=dict(zip('xyz',(v/2 for v in packet['bounds'][3:]))))
    return tree

def mesh_position_trees(file: Any, fields: list[dict[str,Any]]) -> tuple[dict[int,Any],list[dict[str,Any]]]:
    """Prepare position-only mesh edits and conservative skin-renderer bounds."""
    import copy
    import numpy as np
    from UnityPy.helpers.MeshHelper import MeshHandler
    edits={};facts=[];padding={}
    if len({row['object']for row in fields})!=len(fields):raise ValueError('A position field declares a mesh more than once.')
    def positions(reference,count):
        if digest(reference['path'])!=reference['sha256']:raise ValueError('A prepared position field changed.')
        raw=Path(reference['path']).read_bytes()
        if len(raw)!=count*12:raise ValueError('A prepared position field has another vertex count.')
        value=np.frombuffer(raw,'<f4').reshape(count,3)
        if not np.isfinite(value).all():raise ValueError('A prepared position field is nonfinite.')
        return value
    for field in fields:
        obj=file.objects[field['object']]
        if obj.type.name!='Mesh':raise ValueError('A position field does not own a mesh.')
        tree=obj.read_typetree();mesh=MeshHandler(obj.read());mesh.process();count=mesh.m_VertexCount
        if tree['m_Name']!=field['name']or count!=field['vertexCount']:raise ValueError('A position field has another source mesh identity.')
        channel=tree['m_VertexData']['m_Channels'][0]
        if(channel['dimension'],channel['format'])!=(3,0):raise ValueError('Position fields require an existing float32 XYZ stream.')
        vertex_data=mesh.src.m_VertexData
        if vertex_data is None or vertex_data.m_Channels is None:raise ValueError('The position mesh has no declared vertex layout.')
        stream=mesh.get_streams(vertex_data.m_Channels,count)[channel['stream']]
        raw=bytearray(tree['m_VertexData']['m_DataSize']);view=np.ndarray((count,3),'<f4',buffer=raw,
            offset=stream.offset+channel['offset'],strides=(stream.stride,4))
        before=view.copy();after=positions(field['positions'],count);view[:]=after
        distance=float(np.linalg.norm(after.astype(float)-before,axis=1).max(initial=0));posed=[after]
        shape_fields=field.get('shapes',[]);shapes=tree['m_Shapes']
        if sorted(row['frame']for row in shape_fields)!=list(range(len(shapes['shapes']))):
            raise ValueError('A position field must retain every declared source shape frame.')
        for row in shape_fields:
            definition=shapes['shapes'][row['frame']];records=shapes['vertices'][definition['firstVertex']:definition['firstVertex']+definition['vertexCount']]
            delta=positions(row['positions'],count);old=np.zeros((count,3),np.float32);owned=np.zeros(count,bool)
            for record in records:
                index=record['index'];owned[index]=True;old[index]=[record['vertex'][axis]for axis in 'xyz']
                record['vertex']=dict(zip('xyz',map(float,delta[index])))
            if np.any(delta[~owned]!=0):raise ValueError('A position-only field added source shape support.')
            distance=max(distance,float(np.linalg.norm((after.astype(float)+delta)-(before.astype(float)+old),axis=1).max(initial=0)))
            posed.append(after.astype(float)+delta)
        if distance==0:continue
        tree['m_VertexData']['m_DataSize']=bytes(raw)
        bounds=tree['m_LocalAABB'];center=np.array([bounds['m_Center'][a]for a in 'xyz']);extent=np.array([bounds['m_Extent'][a]for a in 'xyz'])
        low=np.minimum(center-extent,np.min([p.min(axis=0)for p in posed],axis=0))
        high=np.maximum(center+extent,np.max([p.max(axis=0)for p in posed],axis=0))
        guard=float(np.finfo(np.float32).eps*max(1.,float(np.max(np.abs(np.r_[low,high]))))*2)
        tree['m_LocalAABB']=dict(m_Center=dict(zip('xyz',map(float,(low+high)*.5))),m_Extent=dict(zip('xyz',map(float,(high-low)*.5+guard))))
        for sub in tree['m_SubMeshes']:
            if 'localAABB'in sub:
                sub['localAABB']['m_Extent']={a:float(v)+distance+guard for a,v in sub['localAABB']['m_Extent'].items()}
        edits[obj.path_id]=tree;padding[obj.path_id]=distance+guard
        facts.append(dict(object=obj.path_id,name=tree['m_Name'],vertexCount=count,maximumDisplacement=distance,
                          positions=field['positions'],shapes=shape_fields,otherVertexChannelsExact=True,shapeNormalsTangentsAndSupportExact=True))
    for obj in file.objects.values():
        if obj.type.name!='SkinnedMeshRenderer':continue
        tree=obj.read_typetree();pointer=tree['m_Mesh']
        if pointer['m_FileID']==0 and pointer['m_PathID']in padding:
            tree=copy.deepcopy(tree);amount=padding[pointer['m_PathID']]
            tree['m_AABB']['m_Extent']={a:float(v)+amount for a,v in tree['m_AABB']['m_Extent'].items()};edits[obj.path_id]=tree
    return edits,facts


def replace_owned_mesh_positions(source_path,source_sha256,target_path,fields):
    """Save/reload declared position frames; preserve texture and other objects."""
    source,target=Path(source_path),Path(target_path)
    if digest(source)!=source_sha256:raise ValueError('The mesh position resource source changed.')
    env,file=load_serialized_bundle(source,in_memory=True)
    streams={name:hashlib.sha256(member.bytes).hexdigest()for name,member in env.file.files.items()if name!=file.name}
    before={key:hashlib.sha256(serialized_object_bytes(obj)).hexdigest()for key,obj in file.objects.items()}
    edits,facts=mesh_position_trees(file,fields);staged={}
    for key,tree in edits.items():file.objects[key].save_typetree(tree);staged[key]=file.objects[key].data
    temporary=target.with_suffix('.writing');target.parent.mkdir(parents=True,exist_ok=True)
    try:
        temporary.write_bytes(env.file.save(packer='lz4'));saved_env,saved=load_serialized_bundle(temporary,in_memory=True)
        if saved.name!=file.name or set(saved.objects)!=set(file.objects)or [v.path for v in saved.externals]!=[v.path for v in file.externals]:
            raise ValueError('A position field changed serialized identity or dependencies.')
        if streams!={name:hashlib.sha256(member.bytes).hexdigest()for name,member in saved_env.file.files.items()if name!=saved.name}:
            raise ValueError('A position field changed a resource stream.')
        for key,obj in saved.objects.items():
            raw=serialized_object_bytes(obj)
            if key in staged:
                if raw!=staged[key]:raise ValueError('A mesh position object failed saved/reloaded roundtrip.')
            elif hashlib.sha256(raw).hexdigest()!=before[key]:raise ValueError('A position field changed an unrelated object.')
        if digest(source)!=source_sha256:raise ValueError('The position source changed during saving.')
        temporary.replace(target)
    finally:
        if temporary.exists():temporary.unlink()
    return dict(file=target.name,beforeSha256=source_sha256,sha256=digest(target),bytes=target.stat().st_size,
        source=dict(path=str(source.resolve()),sha256=source_sha256),target=dict(path=str(target.resolve()),sha256=digest(target)),
        fields=fields,meshes=facts,changedObjects=sorted(edits),resourceStreamsExact=True,unrelatedObjectBytesExact=True,
        topologySkinUvAndShadingChannelsExact=True,bundleCompression='lz4')


def validate_owned_mesh_positions(receipt):
    """Replay the owned XYZ/shape fields and compare every saved typed object."""
    for reference in(receipt['source'],receipt['target']):
        if digest(reference['path'])!=reference['sha256']:raise ValueError('A position resource receipt input changed.')
    first_env,first=load_serialized_bundle(receipt['source']['path'],in_memory=True)
    last_env,last=load_serialized_bundle(receipt['target']['path'],in_memory=True)
    edits,facts=mesh_position_trees(first,receipt['fields'])
    if facts!=receipt['meshes']or sorted(edits)!=receipt['changedObjects']:raise ValueError('A position field lost its declared object scope.')
    if first.name!=last.name or set(first.objects)!=set(last.objects)or [v.path for v in first.externals]!=[v.path for v in last.externals]:
        raise ValueError('A position resource changed identity or references.')
    def streams(env,file):return{name:hashlib.sha256(member.bytes).hexdigest()for name,member in env.file.files.items()if name!=file.name}
    if streams(first_env,first)!=streams(last_env,last):raise ValueError('A saved position field changed a resource stream.')
    for key,obj in first.objects.items():
        if key in edits:
            obj.save_typetree(edits[key])
            if serialized_object_bytes(last.objects[key])!=obj.data:raise ValueError('The saved mesh differs from its complete prepared position field.')
        elif serialized_object_bytes(obj)!=serialized_object_bytes(last.objects[key]):raise ValueError('A saved position field changed an unrelated object.')


def skin_bone_paths(file: Any,renderer: dict[str, Any],resolve: Any,*,canonical: bool=True) -> list[str]:
    """Resolve the ordered native skin palette through its serialized hierarchy."""
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
        if canonical and 'Bip001'in names:names=names[names.index('Bip001'):]
        paths.append('/'.join(names))
    return paths


def audit_native_bone_orders(index: dict[str,Any],assets: set[str]) -> dict[str,Any]:
    """Read each selected prefab's complete skin ownership from original bundles."""
    from functools import lru_cache
    by_cab={row['cab']:row for row in index['bundles']}
    owners={part['asset'].casefold():row['cab']for row in index['bundles']for part in row.get('parts',[])}
    verified={};rows=[];errors=[];absent=[]
    @lru_cache(maxsize=4)
    def native(cab):
        reference=by_cab[cab];path=reference['source']
        if cab not in verified:
            if digest(path)!=reference['sha256']:raise ValueError('An original skin resource changed.')
            verified[cab]=dict(path=path,sha256=reference['sha256'])
        return load_serialized_bundle(path,in_memory=True)
    def resolve(file,pointer):
        if not pointer['m_PathID']:raise ValueError('A selected native skin has a null component or transform.')
        cab=file.name if not pointer['m_FileID']else file.externals[pointer['m_FileID']-1].name
        owner=native(cab)[1];return owner,owner.objects[pointer['m_PathID']]
    for asset in sorted(assets,key=lambda name:(owners.get(name.casefold(),''),name)):
        cab=owners.get(asset.casefold())
        if cab is None:absent.append(asset);continue
        try:
            environment,file=native(cab);root=typed_bundle_asset(environment,asset,'GameObject').deref()
            pending=[(file,root)];seen=set()
            while pending:
                owner,obj=pending.pop();key=(owner.name,obj.path_id)
                if key in seen:raise ValueError('A native prefab repeats a hierarchy object.')
                seen.add(key)
                for component in obj.read_typetree()['m_Component']:
                    cf,co=resolve(owner,component['component']);tree=co.read_typetree()
                    if co.type.name=='Transform':
                        for pointer in tree['m_Children']:
                            tf,to=resolve(cf,pointer);pending.append(resolve(tf,to.read_typetree()['m_GameObject']))
                    elif co.type.name=='SkinnedMeshRenderer':
                        mf,mo=resolve(cf,tree['m_Mesh']);mt=mo.read_typetree()
                        paths=skin_bone_paths(cf,tree,resolve,canonical=False);order=skin_bone_order(paths,mt['m_BoneNameHashes'])
                        if len(mt['m_BindPose'])!=len(paths):raise ValueError('A native skin has incomplete bind poses.')
                        rows.append(dict(asset=asset,renderer=co.path_id,name=obj.peek_name(),mesh=mo.path_id,slots=len(paths),
                            changed=order!=list(range(len(order))),permutation=order,sourceBonePaths=paths,
                            nativeMesh=dict(**verified[mf.name],pathId=mo.path_id)))
        except(ValueError,KeyError,IndexError)as error:errors.append(dict(asset=asset,error=str(error)))
    return dict(status='PASS_NATIVE_SKIN_SLOT_AUDIT'if not errors and not absent else'INCOMPLETE_NATIVE_SKIN_SLOT_AUDIT',
        assets=len(assets),rows=rows,errors=errors,absentAssets=absent,inputs=list(verified.values()))


def extend_skin_bone_palette(file: Any,renderer: dict[str, Any],source_paths: list[str],target_paths: list[str]) -> tuple[dict[str, Any],dict[str, Any]]:
    """Append existing rig transforms without changing the native bone prefix."""
    import copy
    def canonical(path):
        parts=path.split('/')
        return '/'.join(parts[parts.index('Bip001'):]if 'Bip001'in parts else parts)
    source=[canonical(path)for path in source_paths];target=[canonical(path)for path in target_paths]
    if not source or target[:len(source)]!=source:
        raise ValueError('A prepared skin palette must retain its native bone prefix.')
    def resolve(owner,pointer):
        if owner is not file or pointer['m_FileID']or pointer['m_PathID']not in file.objects:
            raise ValueError('A prepared skin palette requires its own serialized rig transforms.')
        return file,file.objects[pointer['m_PathID']]
    if skin_bone_paths(file,renderer,resolve)!=source:
        raise ValueError('The prepared skin palette differs from the actual renderer bone order.')
    _,root=resolve(file,renderer['m_Bones'][0]);root_name=source[0].split('/')[0]
    seen=set()
    while True:
        if root.path_id in seen:raise ValueError('The native skin rig is cyclic.')
        seen.add(root.path_id);tree=root.read_typetree();_,game_object=resolve(file,tree['m_GameObject'])
        if game_object.peek_name()==root_name:break
        if not tree['m_Father']['m_PathID']:raise ValueError('The native skin rig has no declared root.')
        _,root=resolve(file,tree['m_Father'])
    transforms={};pending=[(root,root_name)];seen=set()
    while pending:
        node,path=pending.pop()
        if node.path_id in seen or path in transforms:raise ValueError('The native skin rig has ambiguous transform ownership.')
        seen.add(node.path_id);transforms[path]=dict(m_FileID=0,m_PathID=node.path_id)
        for pointer in node.read_typetree()['m_Children']:
            _,child=resolve(file,pointer);_,game_object=resolve(file,child.read_typetree()['m_GameObject'])
            pending.append((child,path+'/'+game_object.peek_name()))
    if any(path not in transforms for path in target):raise ValueError('A prepared skin palette references a missing native transform.')
    result=copy.deepcopy(renderer)
    result['m_Bones']=[*renderer['m_Bones'],*[transforms[path]for path in target[len(source):]]]
    if skin_bone_paths(file,result,resolve)!=target:raise ValueError('The prepared skin bone palette failed its typed hierarchy check.')
    return result,dict(sourceBonePaths=source,targetBonePaths=target,sourceBones=renderer['m_Bones'],targetBones=result['m_Bones'],nativeBonePrefixExact=True)


def load_serialized_bundle(path, *, in_memory=False) -> tuple[Any, Any]:
    source=Path(path)
    environment: Any = UnityPy.load(source.read_bytes(),path=str(source.parent)) if in_memory else UnityPy.load(str(source))
    files = [value for value in environment.file.files.values() if hasattr(value,'objects')]
    if len(files) != 1:
        raise ValueError('A character resource bundle must contain one serialized file.')
    return environment, files[0]

def replace_compiled_textures(source_path, source_sha256, target_path, compiled_path, compiled_sha256) -> dict[str, Any]:
    """Retain resource identities and samplers while replacing compiled texels."""
    import copy
    source_path,compiled_path,target_path=map(Path,(source_path,compiled_path,target_path))
    if target_path.resolve() in (source_path.resolve(),compiled_path.resolve()):
        raise ValueError('Compiled texture replacement requires an independent target path.')
    if digest(source_path)!=source_sha256 or digest(compiled_path)!=compiled_sha256:
        raise ValueError('A compiled texture replacement input changed.')
    environment,file=load_serialized_bundle(source_path,in_memory=True)
    _,compiled=load_serialized_bundle(compiled_path,in_memory=True)
    before={key:serialized_object_bytes(obj) for key,obj in file.objects.items()}
    old_items=[obj for obj in file.objects.values() if obj.type.name=='Texture2D']
    new_items=[obj for obj in compiled.objects.values() if obj.type.name=='Texture2D']
    old={obj.peek_name():obj for obj in old_items};new={obj.peek_name():obj for obj in new_items}
    if not new or len(old)!=len(old_items)or len(new)!=len(new_items)or old.keys()!=new.keys():
        raise ValueError('Texture replacements must own the same complete named texture set.')
    allowed={'m_Width','m_Height','m_CompleteImageSize','m_TextureFormat','m_MipCount','m_ImageCount',
             'm_TextureDimension','m_ColorSpace','image data','m_StreamData'}
    expected={};rows=[]
    for name,obj in old.items():
        original=obj.read_typetree();replacement=new[name].read_typetree()
        if set(original)!=set(replacement):raise ValueError('Compiled texture serialization versions differ.')
        payload=bytes(new[name].read().get_image_data())
        if not payload or replacement['m_ImageCount']!=1 or replacement['m_TextureDimension']!=2:
            raise ValueError('A replacement requires a complete compiled 2D mip chain.')
        tree=copy.deepcopy(original)
        for field in allowed-{'image data','m_StreamData'}:
            if field in replacement:tree[field]=replacement[field]
        tree['image data']=payload
        if tree.get('m_StreamData') is not None:
            tree['m_StreamData']=dict(tree['m_StreamData'],offset=0,size=0,path='')
        obj.save_typetree(tree);expected[obj.path_id]=tree
        rows.append(dict(name=name,identifier=obj.path_id,width=tree['m_Width'],height=tree['m_Height'],
            mipCount=tree['m_MipCount'],encodedBytes=len(payload),encodedSha256=hashlib.sha256(payload).hexdigest()))
    temporary=target_path.with_name(target_path.name+'.writing')
    try:
        temporary.write_bytes(environment.file.save(packer='original'))
        _,saved=load_serialized_bundle(temporary,in_memory=True)
        if set(saved.objects)!=set(before):raise ValueError('Texture replacement changed resource object identities.')
        for key,obj in saved.objects.items():
            if key in expected:
                if obj.read_typetree()!=expected[key]:raise ValueError('Compiled texture data failed saved reload.')
            elif serialized_object_bytes(obj)!=before[key]:raise ValueError('An unrelated resource object changed.')
        for row in rows:
            payload=bytes(saved.objects[row['identifier']].read().get_image_data())
            if hashlib.sha256(payload).hexdigest()!=row['encodedSha256']:
                raise ValueError('Compiled texture mip bytes changed after reload.')
        temporary.replace(target_path)
    finally:
        temporary.unlink(missing_ok=True)
    return dict(file=target_path.name,sha256=digest(target_path),bytes=target_path.stat().st_size,textures=rows,
        sourceSha256=source_sha256,compiledSha256=compiled_sha256,samplerAndResourceReferencesExact=True,
        source=dict(path=str(source_path.resolve()),sha256=source_sha256),
        target=dict(path=str(target_path.resolve()),sha256=digest(target_path)),
        compiledSource=dict(path=str(compiled_path.resolve()),sha256=compiled_sha256))


def typed_bundle_asset(environment: Any, asset: str, kind: str) -> Any:
    accepted={'AnimatorController','AnimatorOverrideController'}if kind=='RuntimeAnimatorController'else{kind}
    files=[value for value in environment.file.files.values()if hasattr(value,'objects')]
    if len(files)!=1:raise ValueError('A typed character address requires one serialized owner.')
    # Environment.container eagerly resolves preload dependencies. The root
    # address belongs to this serialized file and requires no dependency search.
    matches=[value for name,value in files[0].container.items()if name.casefold()==asset.casefold()and value.type.name in accepted]
    if len(matches)!=1:raise ValueError('A typed character resource address is missing or ambiguous: '+asset+' ('+kind+')')
    return matches[0]

def renderer_repeat_uv(file: Any,renderer: dict[str,Any],resolve: Any) -> bool:
    """Permit integral chart aliases only for the actual Repeat samplers."""
    samplers=[]
    for pointer in renderer['m_Materials']:
        if not pointer['m_PathID']:continue
        _,material=resolve(file,pointer)
        for _,entry in material.read_typetree()['m_SavedProperties']['m_TexEnvs']:
            if not entry['m_Texture']['m_PathID']:continue
            _,texture=resolve(material.assets_file,entry['m_Texture']);state=texture.read_typetree()['m_TextureSettings']
            samplers.append(state['m_WrapU']==0 and state['m_WrapV']==0 and all(value==round(value)for value in entry['m_Scale'].values()))
    if not samplers:raise ValueError('A skin collar has no actual material sampler contract.')
    return all(samplers)


def prefab_skin_renderers(root: Any,resolve: Any):
    """Visit renderer instances belonging to one typed prefab hierarchy."""
    if root.type.name!='GameObject':raise ValueError('A character prefab root must be a typed GameObject.')
    pending=[(root,'')];seen=set()
    while pending:
        game_object,path=pending.pop();identity=(game_object.assets_file.name,game_object.path_id)
        if identity in seen:raise ValueError('A character prefab hierarchy is cyclic or multiply owned.')
        seen.add(identity)
        for component in game_object.read_typetree()['m_Component']:
            _,obj=resolve(game_object.assets_file,component['component'])
            if obj.type.name=='Transform':
                for pointer in obj.read_typetree()['m_Children']:
                    child_file,child=resolve(obj.assets_file,pointer)
                    _,child_object=resolve(child_file,child.read_typetree()['m_GameObject'])
                    pending.append((child_object,(path+'/'if path else'')+child_object.peek_name()))
            elif obj.type.name=='SkinnedMeshRenderer':yield path,obj


def complete_resource_dependencies(resources: list[dict[str, Any]], dependencies: dict[str, set[str]]) -> list[dict[str, Any]]:
    """Retain the full serialized-file closure while any asset in its bundle lives."""
    import copy
    names={}
    for resource in resources:
        for bundle in resource['bundles']:
            previous=names.setdefault(bundle['file'],bundle['name'])
            if previous!=bundle['name']:raise ValueError('A character bundle has conflicting provider names.')
    for file,children in dependencies.items():
        if file not in names or not children<=names.keys():
            raise ValueError('A serialized character bundle dependency is missing: '+file)
    closure={}
    for file in dependencies:
        pending=[file]; found=set()
        while pending:
            current=pending.pop()
            if current in found:continue
            found.add(current);pending.extend(dependencies[current]-found)
        closure[file]=found
    result=copy.deepcopy(resources)
    for resource in result:
        if not resource['bundles'] or any(row['file']not in dependencies for row in resource['bundles']):
            raise ValueError('A character resource has an undeclared serialized owner.')
        primary=resource['bundles'][0]['file']
        resource['bundles']=[dict(file=file,name=names[file])for file in [primary,*sorted(closure[primary]-{primary})]]
    return result

def catalog_resources(path):
    """Read native address/provider metadata without loading its resource payloads."""
    catalog=read(path)
    encoded=base64.b64decode(catalog['m_EntryDataString'])
    count,=struct.unpack_from('<i',encoded)
    if len(encoded)!=4+28*count:raise ValueError('Invalid native catalog entry table.')
    entries=[struct.unpack_from('<7i',encoded,4+28*index)for index in range(count)]
    bucket_data=base64.b64decode(catalog['m_BucketDataString'])
    key_data=base64.b64decode(catalog['m_KeyDataString'])
    bucket_count,=struct.unpack_from('<i',bucket_data);at=4;buckets=[]
    for _ in range(bucket_count):
        key,length=struct.unpack_from('<2i',bucket_data,at);at+=8
        values=struct.unpack_from('<'+str(length)+'i',bucket_data,at);at+=4*length
        buckets.append((key,values))
    if at!=len(bucket_data):raise ValueError('Invalid native catalog bucket table.')
    def internal(index):
        value=catalog['m_InternalIds'][entries[index][0]]
        prefixes=catalog.get('m_InternalIdPrefixes')
        if prefixes and '#'in value:
            prefix,suffix=value.split('#',1);value=prefixes[int(prefix)]+suffix
        return value
    result=[]
    for index,entry in enumerate(entries):
        if entry[2]<0 or catalog['m_ProviderIds'][entry[1]].endswith('SyncBundledAssetProvider'):continue
        offset=buckets[entry[5]][0];kind=key_data[offset]
        if kind not in (0,1):continue
        length,=struct.unpack_from('<i',key_data,offset+1)
        key=key_data[offset+5:offset+5+length].decode('ascii'if kind==0 else'utf-16-le')
        dependencies=buckets[entry[2]][1]
        if not dependencies:continue
        result.append(dict(source=key,asset=internal(index),type=catalog['m_resourceTypes'][entry[6]]['m_ClassName'],
            bundle=Path(internal(dependencies[0]).replace('\\','/')).name))
    return result


@lru_cache(maxsize=32)
def native_mesh_sizes(path: str, expected: str) -> dict[tuple[int, str], int]:
    if digest(path)!=expected:raise ValueError('A native fitting reference changed: '+path)
    environment,_=load_serialized_bundle(path)
    result={}
    for obj in environment.objects:
        if obj.type.name!='Mesh':continue
        tree=obj.read_typetree();name=tree['m_Name'];count=tree['m_VertexData']['m_VertexCount']
        result[obj.path_id,name]=count
    return result


@lru_cache(maxsize=16)
def groom_pigment_mean(path: str, identifier: int, mask_path: str, mask_identifier: int, cutoff: float) -> float:
    import numpy as np
    from PIL import Image
    environment,file=load_serialized_bundle(path);texture=file.objects[identifier].read()
    rgba=np.asarray(texture.image.convert('RGBA'),dtype=float)/255
    rgb=rgba[:,:,:3]
    if texture.m_ColorSpace==1:rgb=np.where(rgb<=.04045,rgb/12.92,((rgb+.055)/1.055)**2.4)
    covered=rgba[:,:,3]>=cutoff
    if mask_identifier:
        _,mask_file=load_serialized_bundle(mask_path)
        mask=mask_file.objects[mask_identifier].read().image.convert('RGB').resize((rgba.shape[1],rgba.shape[0]),Image.Resampling.BILINEAR)
        covered&=np.asarray(mask)[:,:,0]<=127
    if not covered.any():return 0.
    return float(np.mean(rgb[covered]@np.asarray([.2126,.7152,.0722])))


def optimize_resource_bundle(job: dict[str, Any]) -> dict[str, Any]:
    """Write one immutable candidate; source files are never modified in place."""
    from UnityPy.helpers.MeshHelper import MeshHandler
    source=Path(job['source']);target=Path(job['target']);receipt=target.with_suffix('.bundle.json')
    if resource_optimizer_identity()!=job['codeSha256']or digest(source)!=job['sha256']:
        raise ValueError('A resource optimization input changed.')
    if receipt.exists()and target.exists():
        prior=read(receipt)
        if prior['sourceSha256']==job['sha256']and prior['codeSha256']==job['codeSha256']and digest(target)==prior['sha256']:
            return prior
    environment,file=load_serialized_bundle(source);archive=environment.file
    changed={};mips=[];grooms=[];support=[];pigment=[];native={};bindings={}
    groom_ids=groom_mesh_identifiers(file)
    transported_ids=groom_mesh_identifiers(file,1)
    if transported_ids&groom_mesh_identifiers(file,0):raise ValueError('One groom mesh has conflicting material frame formats.')
    flow_ids=groom_mesh_identifiers(file,flow_format=1)
    if flow_ids&groom_mesh_identifiers(file,flow_format=0):raise ValueError('One groom mesh has conflicting fiber-flow formats.')
    groom_names={file.objects[identifier].peek_name()for identifier in groom_ids}
    if groom_ids:
        for reference in job['groomSources']:
            for key,count in native_mesh_sizes(reference['path'],reference['sha256']).items():
                if key[1] not in groom_names:continue
                if key in native and native[key]!=count:raise ValueError('Conflicting native groom prefixes.')
                native[key]=count
    payload=bytearray()
    for identifier,obj in file.objects.items():
        if obj.type.name=='Texture2D':
            tree=obj.read_typetree();stream=tree.get('m_StreamData');old_stream=dict(stream)if stream else None
            if stream and stream['size']:
                member=archive.files[stream['path'].replace('\\','/').rsplit('/',1)[-1]]
                raw=member.bytes if isinstance(member,EndianBinaryReader)else member
                data=raw[stream['offset']:stream['offset']+stream['size']]
            else:data=tree['image data']
            match=re.fullmatch(r'.+_FaceShape_.+_L([012])_(Main|Normal)_FieldBC7',tree['m_Name'])
            if match:
                lod=int(match[1]);maximum=({'Main':2048,'Normal':1024}[match[2]])>>lod
                reduced,data=texture_mip_tail(tree,data,maximum)
                if reduced['m_Width']!=tree['m_Width']or reduced['m_Height']!=tree['m_Height']:
                    mips.append(dict(name=tree['m_Name'],originalSize=[tree['m_Width'],tree['m_Height']],size=[reduced['m_Width'],reduced['m_Height']],retainedMipBytesExact=True))
                    tree=reduced
            if stream and stream['size']:
                payload.extend(bytes((-len(payload))%16));tree['m_StreamData']=dict(stream,offset=len(payload),size=len(data))
                payload.extend(data)
            else:tree['image data']=data
            # Every texture stream is compacted together, including unchanged neighbours.
            if match or tree.get('m_StreamData')!=old_stream:
                obj.save_typetree(tree);changed[identifier]=obj.data
        elif obj.type.name=='Material':
            tree=obj.read_typetree();properties=tree['m_SavedProperties'];floats=dict(properties['m_Floats'])
            if '_CoreResponse'not in floats:continue
            floats['_SupportInVertexFrame']=1.;floats['_FlowInVertexFrame']=1.;properties['m_Floats']=list(floats.items())
            obj.save_typetree(tree);changed[identifier]=obj.data
            textures=dict(properties['m_TexEnvs'])
            def owner(pointer):
                cab=file.name if pointer['m_FileID']==0 else file.externals[pointer['m_FileID']-1].name
                return job['textureOwners'][cab]
            main=textures.get('_MainTex',{}).get('m_Texture',dict(m_FileID=0,m_PathID=0))
            if not main['m_PathID']:continue
            mask=textures.get('_Mask',{}).get('m_Texture',dict(m_FileID=0,m_PathID=0))
            mean=groom_pigment_mean(owner(main),main['m_PathID'],owner(mask)if mask['m_PathID']else'',mask['m_PathID'],floats.get('_Cutoff',.5))
            floats['_NativePigmentMean']=mean;properties['m_Floats']=list(floats.items())
            obj.save_typetree(tree);changed[identifier]=obj.data;pigment.append(dict(material=tree['m_Name'],nativePigmentMean=mean,sourceTintAndAlphaUnchanged=True))
        elif identifier in groom_ids:
            tree=obj.read_typetree();count=native.get((identifier,tree['m_Name']))
            if count is None:
                matching={value for (_,name),value in native.items()if name==tree['m_Name']}
                if len(matching)==1:count=matching.pop()
                elif matching:raise ValueError('A groom has no unambiguous native object identity: '+tree['m_Name'])
            if not tree['m_VertexData']['m_VertexCount']:continue
            helper=MeshHandler(obj.read());helper.process()
            if count is not None and count<tree['m_VertexData']['m_VertexCount']:
                triangles=[triangle for submesh in helper.get_triangles()for triangle in submesh]
                proof=quantize_generated_morphs(tree,count,helper.m_Vertices,triangles)
                if proof['changedRecords']:grooms.append(dict(name=tree['m_Name'],**proof))
            if identifier not in transported_ids:
                support.append(dict(name=tree['m_Name'],**smooth_groom_support(tree,helper)))
                obj.save_typetree(tree)
                prepared=MeshHandler(read_staged_object(obj));prepared.process()
                frame,binding=transport_groom_support_to_vertices(tree,prepared)
                frame['fiberFlow']=bake_groom_vertex_flow(tree,prepared,binding)
                support[-1]['vertexFrame']=frame;bindings[identifier]=binding
            else:
                support.append(dict(name=tree['m_Name'],existingVertexFrameRetained=True))
                if identifier not in flow_ids:support[-1]['fiberFlow']=bake_groom_vertex_flow(tree,helper)
            obj.save_typetree(tree);changed[identifier]=obj.data
    if bindings:
        cloth_trees={key:obj.read_typetree()for key,obj in file.objects.items()if obj.type.name=='MonoBehaviour'}
        for key in transport_groom_cloth_frames(cloth_trees,bindings):
            file.objects[key].save_typetree(cloth_trees[key]);changed[key]=file.objects[key].data
    if not changed:
        if target.exists():target.unlink()
        try:target.hardlink_to(source)
        except OSError:
            import shutil
            shutil.copy2(source,target)
        sha=job['sha256']
    else:
        # Texture payloads are self-contained in these compiled resource bundles.
        stream_names=[name for name in archive.files if name!=file.name]
        if payload:
            if len(stream_names)!=1:raise ValueError('An optimized texture bundle has an ambiguous stream owner.')
            reader=EndianBinaryReader(bytes(payload));setattr(reader,'flags',0);archive.files[stream_names[0]]=reader
        untouched={key:hashlib.sha256(serialized_object_bytes(obj)).digest()for key,obj in file.objects.items()if key not in changed}
        temporary=target.with_suffix('.prepared')
        temporary.write_bytes(archive.save(packer='lz4'))
        saved,saved_file=load_serialized_bundle(temporary,in_memory=True)
        if saved_file.name!=file.name or set(saved_file.objects)!=set(file.objects):raise ValueError('Optimization changed serialized ownership.')
        for identifier,obj in saved_file.objects.items():
            data=serialized_object_bytes(obj)
            if identifier in changed:
                if data!=changed[identifier]:raise ValueError('An optimized object did not round-trip exactly.')
            elif hashlib.sha256(data).digest()!=untouched[identifier]:raise ValueError('An unrelated object changed during optimization.')
        if digest(source)!=job['sha256']:raise ValueError('A source resource changed during optimization.')
        temporary.replace(target);sha=digest(target)
    result=dict(file=target.name,oldCab=file.name,newCab=file.name,sha256=sha,bytes=target.stat().st_size,
        source=str(source),sourceSha256=job['sha256'],codeSha256=job['codeSha256'],textureMips=mips,groomPrecision=grooms,groomSupport=support,groomPigment=pigment,
        objects=len(file.objects),removedObjects=0,dependencies=[external.name for external in file.externals],
        allRetainedObjectFieldsExactAfterReferenceRebasing=not bool(changed),unrelatedObjectBytesExact=True,sourceResourcesUntouched=True)
    receipt.write_text(json.dumps(result,separators=(',',':'))+'\n',encoding='utf-8')
    return result


def optimize_character_resources(manifest_path, native_index_path, output_directory, workers=2):
    """Compact an independent resource catalog, retaining its typed reference graph."""
    from concurrent.futures import ProcessPoolExecutor,as_completed
    source=Path(manifest_path).resolve();destination=Path(output_directory).resolve()
    if source.parent==destination or destination.is_relative_to(source.parent):raise ValueError('Resource optimization needs an independent output directory.')
    if not 1<=workers<=3:raise ValueError('Resource optimization supports one to three bounded workers.')
    manifest=read(source)
    if manifest['schemaVersion']!=3:raise ValueError('A compiled standalone resource catalog is required.')
    native_index=read(native_index_path);by_path={row['source']:row for row in native_index['bundles']}
    native_by_file={row['file']:row for row in native_index['bundles']}
    identity=resource_optimizer_identity();owner=dict(source=str(source),sourceSha256=digest(source),nativeIndexSha256=digest(native_index_path),codeSha256=identity)
    destination.mkdir(parents=True,exist_ok=True);marker=destination/'OptimizationInputs.json'
    if marker.exists():
        if read(marker)!=owner:raise ValueError('The optimization directory belongs to other inputs.')
    elif any(destination.iterdir()):raise ValueError('Resource optimization requires an empty or matching owned directory.')
    else:marker.write_text(json.dumps(owner)+'\n',encoding='utf-8')
    catalog=destination/'Catalog';catalog.mkdir(exist_ok=True);jobs=[];cab_paths={};externals={}
    for index,bundle in enumerate(manifest['bundles']):
        if Path(bundle['file']).name!=bundle['file']:raise ValueError('Resource filenames must remain inside the catalog.')
        path=source.parent/bundle['file'];environment,file=load_serialized_bundle(path)
        if file.name in cab_paths:raise ValueError('Two resource files claim the same serialized identity.')
        cab_paths[file.name]=str(path);externals[bundle['file']]=[file.name,*[external.name for external in file.externals]]
        if index%200==0:print('Indexed resources:',index,'/',len(manifest['bundles']),flush=True)
    for bundle in manifest['bundles']:
        name=bundle['file']
        if Path(name).name!=name:raise ValueError('Resource filenames must remain inside the catalog.')
        original=native_by_file.get(name);groom_sources=[]
        if original:
            for part in original.get('parts',[]):
                groom_sources.extend(path for path in part['dependencies']if by_path[path]['objectTypes'].get('Mesh',0))
        jobs.append(dict(source=str(source.parent/name),target=str(catalog/name),sha256=bundle['sha256'],
            codeSha256=identity,groomSources=[dict(path=path,sha256=by_path[path]['sha256'])for path in sorted(set(groom_sources))],textureOwners={cab:cab_paths[cab]for cab in externals[name]}))
    results=[]
    with ProcessPoolExecutor(max_workers=workers,max_tasks_per_child=8)as executor:
        pending={executor.submit(optimize_resource_bundle,job):job['source']for job in jobs}
        try:
            for future in as_completed(pending):
                try:row=future.result()
                except Exception as error:raise ValueError('Resource optimization failed for '+pending[future])from error
                results.append(row)
                if len(results)%100==0:print('Prepared resources:',len(results),'/',len(jobs),flush=True)
        except BaseException:
            for future in pending:future.cancel()
            raise
    by_file={row['file']:row for row in results}
    sources={row['newCab']:dict(source=str(catalog/row['file']),sha256=row['sha256'],file=row['file'])for row in results}
    closure=ResourceClosure(sources,rebase_archive=None);resources=[]
    for resource in manifest['resources']:
        name=resource['bundles'][0]['file'];cab=by_file[name]['newCab'];environment,_=closure.load(cab)
        root=typed_bundle_asset(environment,resource['asset'],resource['type']);nodes=closure.collect([(cab,root.path_id)])
        files={sources[owner]['file']for owner,_ in nodes}
        resources.append(dict(resource,bundles=[dict(file=file,name=file)for file in [name,*sorted(files-{name})]]))
    if resource_optimizer_identity()!=identity or digest(source)!=owner['sourceSha256']:raise ValueError('Resource optimization inputs changed.')
    manifest['recipeVersion']='standalone-character-resources-v2';manifest['resources']=resources
    manifest['bundles']=[dict(file=name,sha256=by_file[name]['sha256'])for name in sorted(by_file)]
    target=catalog/source.name;target.write_text(json.dumps(manifest,separators=(',',':'))+'\n',encoding='utf-8')
    result=dict(status='PASS_STANDALONE_CHARACTER_RESOURCES',resources=len(resources),bundles=len(results),
        bytes=sum(row['bytes']for row in results),proofs=sorted(results,key=lambda row:row['file']),
        catalogSha256=digest(target),optimization=owner,
        inputs=[dict(path=str(source),sha256=owner['sourceSha256']),dict(path=str(Path(native_index_path).resolve()),sha256=owner['nativeIndexSha256'])],
        originalModelDependenciesInherited=False)
    (destination/'ResourceAssembly.json').write_text(json.dumps(result,separators=(',',':'))+'\n',encoding='utf-8')
    return result

class ResourceClosure:
    def __init__(self, sources: dict[str, dict[str, Any]], aliases: dict[str, str] | None = None, *, rebase_archive):
        self.sources=sources;self.aliases=aliases or {};self.rebase_archive=rebase_archive
        self.adjacency={};self.scripts={};self.kept=defaultdict(set);self.checked=set()
    def normalize(self,cab):return self.aliases.get(cab,cab)
    @lru_cache(maxsize=16)
    def load(self,cab):
        row=self.sources[cab]
        if cab not in self.checked:
            assert digest(row['source'])==row['sha256'],('A resource input changed.',row['source'])
            self.checked.add(cab)
        environment,file=load_serialized_bundle(row['source'])
        assert file.name==cab
        self.scripts[cab]=[self.pointer(file,dict(m_FileID=row.local_serialized_file_index,m_PathID=row.local_identifier_in_file))for row in file.script_types or []]
        return environment,file
    def pointer(self,file,pointer):
        if not pointer['m_PathID']:return None
        cab=file.name if pointer['m_FileID']==0 else file.externals[pointer['m_FileID']-1].name
        cab=self.normalize(cab)
        assert cab in self.sources,('A serialized reference has no declared owner.',file.name,cab,pointer)
        return cab,pointer['m_PathID']
    def pointers(self,file,value):
        if isinstance(value,dict):
            if set(value)=={'m_FileID','m_PathID'}:
                target=self.pointer(file,value)
                if target:yield target
            else:
                for item in value.values():yield from self.pointers(file,item)
        elif isinstance(value,(list,tuple)):
            for item in value:yield from self.pointers(file,item)
    def neighbors(self,key):
        if key not in self.adjacency:
            cab,identifier=key;_,file=self.load(cab);obj=file.objects[identifier]
            if obj.type.name in ('Mesh','Texture2D','MonoScript'):
                children=[]
            else:children=list(self.pointers(file,obj.read_typetree()))
            self.adjacency[key]=children
        return self.adjacency[key]
    def collect(self,roots):
        queue=deque(roots);nodes=set();files=set()
        while queue:
            key=queue.popleft()
            if key is None or key in nodes:continue
            cab,identifier=key
            self.load(cab)
            if cab not in files:
                files.add(cab);queue.extend(self.scripts[cab])
            nodes.add(key);queue.extend(self.neighbors(key))
        for cab,identifier in nodes:self.kept[cab].add(identifier)
        return nodes

    def pack(self,cab,target_directory,outputs):
        source=self.sources[cab]
        if digest(source['source'])!=source['sha256']:raise ValueError('A resource input changed before packing: '+source['source'])
        environment,file=load_serialized_bundle(source['source'])
        assert file.name==cab
        original_count=len(file.objects)
        metadata=next(obj for obj in file.objects.values()if obj.type.name=='AssetBundle')
        keep=set(self.kept[cab]);keep.add(metadata.path_id)
        objects={key:obj for key,obj in file.objects.items()if key in keep}
        trees={key:obj.read_typetree()for key,obj in objects.items()}
        inverse={value['cab']:key for key,value in outputs.items()}
        def canonical_tree(value,owner,packed=False):
            if isinstance(value,dict):
                if set(value)=={'m_FileID','m_PathID'}:
                    if not value['m_PathID']:return ('ref',None,0)
                    other=owner.name if value['m_FileID']==0 else owner.externals[value['m_FileID']-1].name
                    other=inverse[other]if packed else self.normalize(other)
                    return ('ref',other,value['m_PathID'])
                return {key:({'size':item['size']}if key=='m_StreamData'else canonical_tree(item,owner,packed))for key,item in value.items()}
            if isinstance(value,(list,tuple)):return [canonical_tree(item,owner,packed)for item in value]
            if isinstance(value,float):return ('float32',struct.pack('<f',value).hex())
            return value
        expected_trees={key:canonical_tree(tree,file)for key,tree in trees.items()if key!=metadata.path_id}
        wanted=set()
        for identifier,tree in trees.items():
            if identifier==metadata.path_id:continue
            wanted.update(other for other,_ in self.pointers(file,tree)if other!=cab)
        wanted.update(target[0]for target in self.scripts[cab]if target is not None and target[0]!=cab)
        bundle_tree=trees[metadata.path_id]
        containers=[]
        container_nodes={}
        for name,value in bundle_tree['m_Container']:
            target=self.pointer(file,value['asset'])
            if target is not None and target[1]in self.kept[target[0]]:
                containers.append((name,value))
                container_nodes[name]=self.collect([target])
                wanted.update(other for other,_ in container_nodes[name]if other!=cab)
        external_order=sorted(wanted)
        external_indices={other:index+1 for index,other in enumerate(external_order)}
        def remap(value: Any) -> Any:
            if isinstance(value,dict):
                if set(value)=={'m_FileID','m_PathID'}:
                    target=self.pointer(file,value)
                    if target is None:return dict(m_FileID=0,m_PathID=0)
                    owner,identifier=target
                    assert identifier in self.kept[owner],('A reachable object was dropped.',owner,identifier)
                    return dict(m_FileID=0 if owner==cab else external_indices[owner],m_PathID=identifier)
                return {key:remap(item)for key,item in value.items()}
            if isinstance(value,list):return [remap(item)for item in value]
            if isinstance(value,tuple):return tuple(remap(item)for item in value)
            return value
        # Each asset preloads its own closure. The skeleton root must not pull
        # every body mesh and material stored in the same original archive.
        preload=[];container_records=[]
        for name,value in containers:
            index=len(preload)
            nodes=sorted((0 if owner==cab else external_indices[owner],identifier)for owner,identifier in container_nodes[name])
            preload.extend(dict(m_FileID=owner,m_PathID=identifier)for owner,identifier in nodes)
            container_records.append((name,dict(value,asset=remap(value['asset']),preloadIndex=index,preloadSize=len(nodes))))
        bundle_tree['m_PreloadTable']=preload
        bundle_tree['m_Container']=container_records
        bundle_tree['m_MainAsset']=dict(bundle_tree['m_MainAsset'],preloadIndex=0,preloadSize=0,asset=dict(m_FileID=0,m_PathID=0))
        bundle_tree['m_Dependencies']=[outputs[other]['file']for other in external_order]
        stream_payload=bytearray();spans={};payload_proofs=[]
        archive=environment.file
        for identifier,tree in trees.items():
            if identifier==metadata.path_id:continue
            tree=remap(tree);trees[identifier]=tree
            stream=tree.get('m_StreamData')
            if stream and stream['size']:
                member=stream['path'].replace('\\','/').rsplit('/',1)[-1]
                assert member in archive.files,('A stream belongs to another owner.',cab,identifier,member)
                span=(member,stream['offset'],stream['size'])
                if span not in spans:
                    source_member=archive.files[member]
                    raw=source_member.bytes if isinstance(source_member,EndianBinaryReader)else source_member
                    payload=raw[span[1]:span[1]+span[2]];assert len(payload)==span[2]
                    stream_payload.extend(bytes((-len(stream_payload))%16));spans[span]=len(stream_payload);stream_payload.extend(payload)
                else:payload=bytes(stream_payload[spans[span]:spans[span]+span[2]])
                stream['offset']=spans[span];stream['path']=f'archive:/{cab}/{cab}.resS'
                payload_proofs.append(dict(id=identifier,bytes=span[2],sha256=hashlib.sha256(payload).hexdigest()))
            elif stream:
                stream.update(offset=0,path='')
        # Script metadata uses the same local/external numbering as PPtr values.
        for item,target in zip(file.script_types or [],self.scripts[cab]):
            if target is None:continue
            item.local_serialized_file_index=0 if target[0]==cab else external_indices[target[0]]
        file.externals=[]
        for other in external_order:
            external=FileIdentifier.__new__(FileIdentifier)
            external.path=f'archive:/{outputs[other]["cab"]}/{outputs[other]["cab"]}'
            external.temp_empty='';external.guid=bytes(16);external.type=0;file.externals.append(external)
        file.objects=objects
        for identifier,tree in trees.items():
            obj=objects[identifier];obj.save_typetree(tree)
            obj.reader=EndianBinaryReader(obj.data,endian=obj.reader.endian);obj.byte_start=0;obj.byte_size=len(obj.data)
        archive.files={cab:file}
        if stream_payload:
            reader=EndianBinaryReader(bytes(stream_payload));setattr(reader,'flags',0)
            archive.files[cab+'.resS']=reader
        self.rebase_archive(environment,outputs[cab]['file'],outputs[cab]['cab'])
        destination=target_directory/outputs[cab]['file'];destination.write_bytes(archive.save(packer='lz4'))
        saved,saved_file=load_serialized_bundle(destination,in_memory=True)
        assert saved_file.name==outputs[cab]['cab']and set(saved_file.objects)==keep
        for identifier,expected in expected_trees.items():
            actual=canonical_tree(saved_file.objects[identifier].read_typetree(),saved_file,True)
            assert actual==expected,('An object changed beyond its resource references.',source['source'],identifier)
        for proof in payload_proofs:
            stream=saved_file.objects[proof['id']].read_typetree()['m_StreamData'];member=stream['path'].rsplit('/',1)[-1]
            raw=saved.file.files[member].bytes
            assert hashlib.sha256(raw[stream['offset']:stream['offset']+stream['size']]).hexdigest()==proof['sha256']
        for external in saved_file.externals:assert external.name in {value['cab']for value in outputs.values()}
        return dict(file=destination.name,sha256=digest(destination),bytes=destination.stat().st_size,
                    source=source['source'],sourceSha256=source['sha256'],oldCab=cab,newCab=saved_file.name,
                    objects=len(keep),removedObjects=original_count-len(keep),retainedStreamSpans=payload_proofs,
                    allRetainedObjectFieldsExactAfterReferenceRebasing=True,
                    dependencies=external_order)

def assemble_character_resources(prepared_path, native_index_path, native_catalog_path,
                                 source_directory, output_directory, rebase_archive, material_names=()):
    """Close the prepared prefab, animation and requested equipment-material roots."""
    prepared=read(prepared_path);native_index=read(native_index_path)
    original=native_index['bundles'];by_path={row['source']:row for row in original}
    sources={row['cab']:row for row in original};aliases={};ready=[]
    for row in prepared['parts']:
        if row.get('status')not in (None,'PASS_MATERIAL_COOK'):raise ValueError('A prepared character part failed validation.')
        if digest(row['output'])!=row['sha256']:raise ValueError('A prepared character part changed.')
        native=by_path[row['originalSource']['path']]
        _,file=load_serialized_bundle(row['output'])
        aliases[native['cab']]=file.name
        sources[file.name]=dict(source=row['output'],sha256=row['sha256'],file=Path(row['output']).name,parts=native['parts'])
        ready.append((file.name,row,native))
    source_directory=Path(source_directory)
    for cab,name in prepared['publicDependencies'].items():
        path=source_directory/name
        sources[cab]=dict(source=str(path),sha256=digest(path),file=name)
    closure=ResourceClosure(sources,aliases,rebase_archive=rebase_archive)
    roots={};parameters={};nodes={}
    for cab,part,native in ready:
        environment,_=closure.load(cab)
        for resource in native['parts']:
            selected=typed_bundle_asset(environment,resource['asset'],'GameObject')
            key=resource['sourceKey']
            if key in roots:raise ValueError('Duplicate character resource address: '+key)
            roots[key]=dict(asset=resource['asset'],root=(cab,selected.path_id),type='GameObject')
            parameters[key]=next((value for asset,value in part['parameters'].items()if asset.casefold()==resource['asset'].casefold()),[])
            nodes[key]=closure.collect([roots[key]['root']])
    native_files={Path(row['source']).name:row for row in original}
    for resource in catalog_resources(native_catalog_path):
        kind=resource['type']
        controller=kind in ('UnityEngine.RuntimeAnimatorController','UnityEngine.AnimatorOverrideController','UnityEngine.AnimatorController')
        material=kind=='UnityEngine.Material'and any(resource['source'].endswith('_'+name+'.mat')for name in material_names)
        if not controller and not material:continue
        owner=native_files.get(resource['bundle'])
        if owner is None:continue
        if owner['cab']not in aliases and not(material and owner['cab']in closure.kept and owner['objectTypes'].get('Mesh',0)):continue
        cab=closure.normalize(owner['cab']);environment,_=closure.load(cab)
        selected=typed_bundle_asset(environment,resource['asset'],'RuntimeAnimatorController'if controller else'Material')
        key=resource['source'];entry=dict(asset=resource['asset'],root=(cab,selected.path_id),type='RuntimeAnimatorController'if controller else'Material')
        if key in roots:
            if roots[key]!=entry:raise ValueError('A character address has conflicting resources: '+key)
            continue
        roots[key]=entry;parameters[key]=[];nodes[key]=closure.collect([entry['root']])
    identity=digest(__file__)
    outputs={cab:dict(file=sources[cab]['file'],cab='CAB-'+hashlib.sha256(('character-resources-v1:'+identity+sources[cab]['sha256']+json.dumps(sorted(closure.kept[cab]))).encode()).hexdigest()[:32])for cab in closure.kept}
    names=[value['file']for value in outputs.values()]
    if len(set(name.casefold()for name in names))!=len(names):raise ValueError('Character resource filenames collide.')
    if any(len(name)>128 or re.search(r'[a-f0-9]{16}',name)for name in names):raise ValueError('A character resource needs a semantic filename within 128 characters.')
    output_directory=Path(output_directory);output_directory.mkdir(parents=True,exist_ok=True)
    proofs=[]
    for cab in sorted(outputs):proofs.append(closure.pack(cab,output_directory,outputs))
    resources=[]
    for key,root in roots.items():
        used={cab for cab,_ in nodes[key]};ordered=[root['root'][0],*sorted(used-{root['root'][0]})]
        resources.append(dict(source=key,asset=root['asset'],type=root['type'],
            bundles=[dict(file=outputs[cab]['file'],name=outputs[cab]['file'])for cab in ordered],parameters=parameters[key]))
    dependencies={row['file']:{outputs[cab]['file']for cab in row['dependencies']}for row in proofs}
    resources=complete_resource_dependencies(resources,dependencies)
    for row in proofs:row['dependencies']=[outputs[cab]['cab']for cab in row['dependencies']]
    if digest(__file__)!=identity:raise ValueError('The resource assembler changed during packing.')
    manifest=dict(schemaVersion=3,recipeVersion='standalone-character-resources-v1',bundles=[dict(file=row['file'],sha256=row['sha256'])for row in proofs],
        resources=resources,meshes=[],materials=[],textureRemaps=[])
    target=output_directory/'CharacterModels.manifest.json';temporary=target.with_suffix('.writing')
    temporary.write_text(json.dumps(manifest,separators=(',',':'))+'\n',encoding='utf-8');temporary.replace(target)
    result=dict(status='PASS_STANDALONE_CHARACTER_RESOURCES',resources=len(resources),bundles=len(proofs),bytes=sum(row['bytes']for row in proofs),proofs=proofs,
        inputs=[dict(path=str(Path(path).resolve()),sha256=digest(path))for path in (prepared_path,native_index_path,native_catalog_path)],originalModelDependenciesInherited=False)
    return result


def material_texture_preloads(tree,bindings):
    """Extend only the prefab preloads that own a changed local material."""
    import copy
    result=copy.deepcopy(tree);original=tree['m_PreloadTable'];preloads=result['m_PreloadTable'];covered=set()
    for index,(name,entry)in enumerate(tree['m_Container']):
        start,size=entry['preloadIndex'],entry['preloadSize']
        if start<0 or size<0 or start+size>len(original):raise ValueError('A material texture owner has an invalid prefab preload span.')
        members=original[start:start+size];local={p['m_PathID']for p in members if p['m_FileID']==0}
        added=sorted({texture for material,texture in bindings if material in local and texture not in local})
        covered.update(material for material,_ in bindings if material in local)
        if not added:continue
        result['m_Container'][index]=(name,dict(entry,preloadIndex=len(preloads),preloadSize=size+len(added)))
        preloads.extend(copy.deepcopy(members));preloads.extend(dict(m_FileID=0,m_PathID=value)for value in added)
    if covered!={material for material,_ in bindings}:raise ValueError('A changed material is outside every declared prefab preload.')
    return result


def replace_owned_material_textures(source_path,source_sha256,target_path,updates):
    """Embed compiled textures in their material owner without adding runtime dependencies."""
    import copy
    source,target=Path(source_path),Path(target_path)
    if source.resolve()==target.resolve()or digest(source)!=source_sha256 or not updates:
        raise ValueError('A material texture refinement requires an immutable independent source.')
    env,file=load_serialized_bundle(source,in_memory=True)
    original={key:serialized_object_bytes(obj)for key,obj in file.objects.items()};before_ids=set(original)
    metadata=[obj for obj in file.objects.values()if obj.type.name=='AssetBundle']
    if len(metadata)!=1:raise ValueError('A material texture refinement has no unique bundle metadata owner.')
    materials={};clones={};bindings=[];changes=[];owners=set();next_id=1
    for update in updates:
        material_id=update['pathId'];property=update['property'];selector=(material_id,property)
        if selector in owners or material_id not in file.objects or file.objects[material_id].type.name!='Material':
            raise ValueError('A material texture owner is missing or repeated.')
        owners.add(selector);tree=materials.setdefault(material_id,file.objects[material_id].read_typetree())
        entries=tree['m_SavedProperties']['m_TexEnvs'];matches=[entry for name,entry in entries if name==property]
        if len(matches)!=1 or matches[0]['m_Texture']!=update['before']:
            raise ValueError('A material texture source pointer differs from its declared owner.')
        reference=update['texture'];key=(reference['sha256'],reference['name'])
        if key not in clones:
            if digest(reference['path'])!=reference['sha256']:raise ValueError('A compiled material texture source changed.')
            _,compiled=load_serialized_bundle(reference['path'],in_memory=True)
            if compiled.header.version!=file.header.version or compiled.target_platform!=file.target_platform:
                raise ValueError('A compiled material texture has an incompatible serialized format.')
            chosen=[obj for obj in compiled.objects.values()if obj.type.name=='Texture2D'and obj.peek_name()==reference['name']]
            if len(chosen)!=1:raise ValueError('A compiled material texture is not unique.')
            obj=chosen[0];texture=obj.read_typetree();payload=bytes(obj.read().get_image_data())
            while next_id in file.objects:next_id+=1
            type_indices=[i for i,value in enumerate(file.types)if value.class_id==obj.class_id]
            if len(type_indices)>1:raise ValueError('A material texture has ambiguous serialized type ownership.')
            if type_indices:type_index=type_indices[0]
            else:
                type_index=len(file.types);file.types.append(copy.deepcopy(obj.serialized_type))
            cloned=copy.copy(obj);cloned.assets_file=file;cloned.path_id=next_id;cloned.type_id=type_index;cloned.serialized_type=file.types[type_index]
            texture['m_StreamData']=dict(texture['m_StreamData'],offset=0,size=0,path='');texture['image data']=payload
            cloned.save_typetree(texture);file.objects[next_id]=cloned
            clones[key]=dict(object=next_id,source=copy.deepcopy(reference),sourceObject=obj.path_id,payloadSha256=hashlib.sha256(payload).hexdigest())
        selected=clones[key]['object'];pointer=dict(m_FileID=0,m_PathID=selected)
        matches[0]['m_Texture']=pointer;bindings.append((material_id,selected))
        changes.append(dict(pathId=material_id,property=property,before=copy.deepcopy(update['before']),after=pointer))
    for key,tree in materials.items():file.objects[key].save_typetree(tree)
    bundle=metadata[0];bundle.save_typetree(material_texture_preloads(bundle.read_typetree(),bindings))
    target.parent.mkdir(parents=True,exist_ok=True);temporary=target.with_name(target.name+'.writing')
    try:
        temporary.write_bytes(env.file.save(packer='lz4'))
        receipt=dict(file=target.name,beforeSha256=source_sha256,sha256=digest(temporary),bytes=temporary.stat().st_size,
            source=dict(path=str(source.resolve()),sha256=source_sha256),target=dict(path=str(temporary.resolve()),sha256=digest(temporary)),
            materialUpdates=changes,textures=list(clones.values()),objectsAdded=sorted(set(file.objects)-before_ids),metadataObject=bundle.path_id)
        validate_owned_material_textures(receipt)
        if digest(source)!=source_sha256:raise ValueError('A material texture source changed during saving.')
        temporary.replace(target);receipt['target']['path']=str(target.resolve())
    finally:temporary.unlink(missing_ok=True)
    return receipt


def validate_owned_material_textures(receipt):
    """Reopen local material bindings, compiled payloads and the exact preload graph."""
    import copy
    for reference in (receipt['source'],receipt['target'],*[row['source']for row in receipt['textures']]):
        if digest(reference['path'])!=reference['sha256']:raise ValueError('A material texture checkpoint changed.')
    source_env,before=load_serialized_bundle(receipt['source']['path'],in_memory=True)
    target_env,after=load_serialized_bundle(receipt['target']['path'],in_memory=True)
    additions=set(receipt['objectsAdded']);clones={row['object']:row for row in receipt['textures']}
    if not additions or additions!=set(clones)or additions&before.objects.keys()or set(after.objects)!=set(before.objects)|additions or \
            before.name!=after.name or [v.path for v in before.externals]!=[v.path for v in after.externals]:
        raise ValueError('A material texture refinement changed unrelated object or external ownership.')
    changes={};bindings=[]
    for row in receipt['materialUpdates']:
        key=row['pathId'];selector=(key,row['property'])
        if selector in changes or key not in before.objects or before.objects[key].type.name!='Material'or row['after']['m_FileID']!=0 or row['after']['m_PathID']not in additions:
            raise ValueError('A material texture refinement has an invalid or repeated binding.')
        changes[selector]=row;bindings.append((key,row['after']['m_PathID']))
    if {texture for _,texture in bindings}!=additions:raise ValueError('A cloned material texture has no owning material.')
    for key,obj in before.objects.items():
        if key==receipt['metadataObject']:
            if obj.type.name!='AssetBundle'or material_texture_preloads(obj.read_typetree(),bindings)!=after.objects[key].read_typetree():
                raise ValueError('A cloned material texture changed an unowned preload or bundle field.')
        elif any(material==key for material,_ in changes):
            expected=obj.read_typetree()
            for name,entry in expected['m_SavedProperties']['m_TexEnvs']:
                change=changes.get((key,name))
                if change:
                    if entry['m_Texture']!=change['before']:raise ValueError('A cloned material texture lost its source sampler.')
                    entry['m_Texture']=copy.deepcopy(change['after'])
            if expected!=after.objects[key].read_typetree():raise ValueError('A cloned material texture changed another material field.')
        elif serialized_object_bytes(obj)!=serialized_object_bytes(after.objects[key]):
            raise ValueError('A cloned material texture changed an unowned serialized object.')
    for key,row in clones.items():
        _,compiled=load_serialized_bundle(row['source']['path'],in_memory=True);obj=compiled.objects[row['sourceObject']]
        expected=obj.read_typetree();payload=bytes(obj.read().get_image_data())
        expected['m_StreamData']=dict(expected['m_StreamData'],offset=0,size=0,path='');expected['image data']=payload
        if obj.type.name!='Texture2D'or obj.peek_name()!=row['source']['name']or expected!=after.objects[key].read_typetree()or hashlib.sha256(payload).hexdigest()!=row['payloadSha256']:
            raise ValueError('A cloned material texture differs from its compiled object or complete mips.')
    for name,value in source_env.file.files.items():
        if hasattr(value,'objects'):continue
        other=target_env.file.files[name]
        if (bytes(value)if isinstance(value,(bytes,bytearray))else value.bytes)!=(bytes(other)if isinstance(other,(bytes,bytearray))else other.bytes):
            raise ValueError('A cloned material texture changed an unowned resource stream.')


def renderer_bone_order(file: Any, renderer: dict[str, Any], update: dict[str, Any]) -> tuple[dict[str, Any],dict[str, Any]]:
    """Restore native mesh slot identity without changing geometry or added slots."""
    import copy
    reference=update['nativeMesh']
    if digest(reference['path'])!=reference['sha256']:raise ValueError('The native bone-hash mesh source changed.')
    _,native=load_serialized_bundle(reference['path'],in_memory=True)
    native_tree=native.objects[reference['pathId']].read_typetree()
    hashes=native_tree['m_BoneNameHashes'];paths=update['sourceBonePaths'];order=skin_bone_order(paths,hashes)
    if not hashes or len(native_tree['m_BindPose'])!=len(paths):raise ValueError('The native bone hash palette has incomplete bind poses.')
    pointer=renderer['m_Mesh']
    if pointer['m_FileID']or pointer['m_PathID']not in file.objects:raise ValueError('Bone-order repair requires its owned prepared mesh.')
    target_mesh=file.objects[pointer['m_PathID']].read_typetree();count=len(paths)
    if target_mesh['m_BindPose'][:count]!=native_tree['m_BindPose']or len(target_mesh['m_BindPose'])!=len(renderer['m_Bones']):
        raise ValueError('The prepared skin does not retain the native mesh bind-pose prefix.')
    def resolve(owner,pointer):
        if owner is not file or pointer['m_FileID']or pointer['m_PathID']not in file.objects:raise ValueError('The prepared bone palette must own its rig.')
        return owner,owner.objects[pointer['m_PathID']]
    def canonical(path):
        parts=path.split('/');return '/'.join(parts[parts.index('Bip001'):]if'Bip001'in parts else parts)
    actual=skin_bone_paths(file,renderer,resolve);source=[canonical(p)for p in paths];expected=[source[i]for i in order]
    if actual[:count]!=source:raise ValueError('The current renderer differs from the declared original bone order.')
    result=copy.deepcopy(renderer)
    result['m_Bones']=[*[renderer['m_Bones'][i]for i in order],*renderer['m_Bones'][count:]]
    if skin_bone_paths(file,result,resolve)!=expected+actual[count:]:raise ValueError('The hash-aligned renderer did not retain its transform identity.')
    return result,dict(kind='native-bone-hash-order',renderer=update['renderer'],mesh=pointer['m_PathID'],nativeMesh=reference,
        sourceBonePaths=paths,meshBoneNameHashes=hashes,permutation=order,sourceBones=renderer['m_Bones'],targetBones=result['m_Bones'],
        bindPosesAndWeightsExact=True,appendedBoneSlotsExact=True)


def validate_renderer_bone_orders(receipt: dict[str, Any]) -> None:
    source,target=receipt['source'],receipt['target']
    for pin in (source,target):
        if digest(pin['path'])!=pin['sha256']:raise ValueError('A saved bone-order resource changed.')
    first_env,first=load_serialized_bundle(source['path'],in_memory=True);last_env,last=load_serialized_bundle(target['path'],in_memory=True)
    changes={row['renderer']:row for row in receipt['rendererBoneUpdates']}
    if not changes or len(changes)!=len(receipt['rendererBoneUpdates'])or any(row.get('kind')!='native-bone-hash-order'for row in changes.values()):raise ValueError('A bone-order receipt has another mutation owner.')
    if first.name!=last.name or set(first.objects)!=set(last.objects)or [v.path for v in first.externals]!=[v.path for v in last.externals]:
        raise ValueError('A bone-order repair changed its serialized ownership.')
    for key,obj in first.objects.items():
        if key in changes:
            if obj.type.name!='SkinnedMeshRenderer':raise ValueError('A bone-order repair changed another component type.')
            row=changes[key];expected,proof=renderer_bone_order(first,obj.read_typetree(),row)
            if proof!=row or expected!=last.objects[key].read_typetree():raise ValueError('The saved renderer differs from its native mesh hash order.')
        elif serialized_object_bytes(obj)!=serialized_object_bytes(last.objects[key]):raise ValueError('A bone-order repair changed an unowned object.')
    for name,member in first_env.file.files.items():
        if name==first.name:continue
        other=last_env.file.files[name]
        if (bytes(member)if isinstance(member,(bytes,bytearray))else member.bytes)!=(bytes(other)if isinstance(other,(bytes,bytearray))else other.bytes):
            raise ValueError('A bone-order repair changed an unowned stream.')
    if set(first_env.file.files)!=set(last_env.file.files):raise ValueError('A bone-order repair changed its stream ownership.')


def prepare_standalone_part(source_path, expected_sha256, target_path, replacements, *, export_mesh, material_updates=(), repair_cloth=False, bone_order_updates=()):
    """Bake declared geometry, cloth and material values into a complete prefab resource."""
    import copy
    import math
    import os
    if bone_order_updates and(replacements or material_updates or repair_cloth):raise ValueError('Bone-order repair owns a separate renderer-only transaction.')
    source_path,target_path=Path(source_path),Path(target_path)
    if source_path.resolve()==target_path.resolve() or digest(source_path)!=expected_sha256.lower():
        raise ValueError('The immutable standalone mesh source differs.')
    fingerprints=[row['sourceFingerprint']for row in replacements]
    if (not fingerprints and not material_updates and not repair_cloth and not bone_order_updates)or len(set(fingerprints))!=len(fingerprints):
        raise ValueError('A standalone mesh batch requires unique source fingerprints.')
    environment,file=load_serialized_bundle(source_path,in_memory=True)
    before={key:serialized_object_bytes(obj)for key,obj in file.objects.items()}
    # Cloth owns its component metadata, not the complete sparse mesh frames.
    # Keep mesh trees outside that mutable metadata transaction.
    mutable_types: tuple[str,...]=('MonoBehaviour','AssetBundle','Material')if replacements or repair_cloth else('Material',)
    if bone_order_updates or any('bonePaths'in row for row in replacements):mutable_types+=('SkinnedMeshRenderer',)
    old_trees={key:obj.read_typetree()for key,obj in file.objects.items()if obj.type.name in mutable_types}
    trees=copy.deepcopy(old_trees)
    native={key:export_mesh(obj)for key,obj in file.objects.items()if replacements and obj.type.name=='Mesh'}
    cloth_replacements={};selected={};mesh_trees={};bone_updates={}
    for update in bone_order_updates:
        key=update['renderer']
        if key in bone_updates or key not in trees or file.objects[key].type.name!='SkinnedMeshRenderer':raise ValueError('A declared bone-order renderer is missing or repeated.')
        trees[key],proof=renderer_bone_order(file,trees[key],update);bone_updates[key]=proof
    for row in replacements:
        matches=[key for key,packet in native.items()if packet['sourceFingerprint']==row['sourceFingerprint']]
        if 'sourcePathIds'in row:
            declared=list(row['sourcePathIds'])
            if not declared or len(set(declared))!=len(declared)or not set(declared)<=set(matches):
                raise ValueError('The declared standalone renderer mesh owners differ.')
            matches=declared
        elif len(matches)!=1:raise ValueError('The declared standalone source mesh is not unique.')
        for key in matches:
            if key in selected:raise ValueError('A standalone mesh owner is repeated.')
            packet=row['packet'];binding=row['binding'];tree=dict(row['compiledTree'])
            if tree.get('m_StreamData',{}).get('size',0)!=0 or len(binding['vertices'])!=packet['vertexCount']:
                raise ValueError('A prepared standalone mesh has a streamed or different attachment layout.')
            tree['m_Name']=file.objects[key].peek_name();mesh_trees[key]=tree
            cloth_replacements[key]=dict(native=native[key],raw=native[key],packet=packet,binding=binding,joint=None,entry=dict(authoredFingerprint=packet['sourceFingerprint']))
            selected[key]=packet['sourceFingerprint']
            if 'bonePaths'in row:
                if len(row['bonePaths'])*16!=len(packet['bindPoses']):raise ValueError('A prepared skin palette differs from its compiled bind poses.')
                renderer_ids=[identifier for identifier,obj in file.objects.items()if obj.type.name=='SkinnedMeshRenderer'and
                    trees[identifier]['m_Mesh']==dict(m_FileID=0,m_PathID=key)]
                if not renderer_ids:raise ValueError('A prepared skin mesh has no owned serialized renderer.')
                for identifier in renderer_ids:
                    if identifier in bone_updates:raise ValueError('A prepared skin renderer palette is repeated.')
                    trees[identifier],palette=extend_skin_bone_palette(file,trees[identifier],row['sourceBonePaths'],row['bonePaths'])
                    bone_updates[identifier]=dict(palette,renderer=identifier,mesh=key)
    material_ids=set();material_proofs=[]
    for update in material_updates:
        key=update['pathId']
        if key in material_ids or key not in file.objects or file.objects[key].type.name!='Material':
            raise ValueError('A declared material owner is missing or repeated.')
        material_ids.add(key);pairs=trees[key]['m_SavedProperties']['m_Floats'];values=dict(pairs)
        for name,value in update['values'].items():
            if name not in values or values[name]!=value['before']or type(value['after'])not in(int,float)or not math.isfinite(value['after']):
                raise ValueError('A declared material float changed or has an invalid replacement.')
        trees[key]['m_SavedProperties']['m_Floats']=[(name,update['values'][name]['after']if name in update['values']else value)for name,value in pairs]
        material_proofs.append(dict(pathId=key,values=copy.deepcopy(update['values'])))
    cloth=prepare_native_cloth(trees,cloth_replacements)if replacements else dict(renderDeltas={},virtualDeltas={},changes=[])
    cloth['disabledInvalidComponents']=prepare_native_cloth_state(trees)if replacements or repair_cloth else []
    trees.update(mesh_trees)
    for key,tree in trees.items():
        if key not in old_trees or tree!=old_trees[key]:file.objects[key].save_typetree(tree)
    changed={key for key,obj in file.objects.items()if serialized_object_bytes(obj)!=before[key]}
    allowed=set(selected)|material_ids|set(bone_updates)|({key for key,obj in file.objects.items()if obj.type.name=='MonoBehaviour'}if replacements or repair_cloth else set())
    if not changed<=allowed:raise ValueError('Standalone mesh preparation changed another object owner.')
    prepared={key:serialized_object_bytes(obj)for key,obj in file.objects.items()}
    resources={key:bytes(value)if isinstance(value,(bytes,bytearray))else value.bytes for key,value in environment.file.files.items()if not hasattr(value,'objects')}
    target_path.parent.mkdir(parents=True,exist_ok=True)
    temporary=target_path.with_name(target_path.name+'.prepared');created=False
    try:
        with temporary.open('xb')as output:
            created=True;output.write(environment.file.save(packer='lz4'))
        saved,saved_file=load_serialized_bundle(temporary,in_memory=True)
        if saved_file.name!=file.name or set(saved_file.objects)!=set(file.objects):
            raise ValueError('The prepared standalone serialized identity changed.')
        if [value.path for value in saved_file.externals]!=[value.path for value in file.externals]:
            raise ValueError('A prepared standalone external reference changed.')
        if any(serialized_object_bytes(obj)!=prepared[key]for key,obj in saved_file.objects.items()):
            raise ValueError('A prepared standalone object changed after save.')
        for key,value in resources.items():
            member=saved.file.files[key];payload=bytes(member)if isinstance(member,(bytes,bytearray))else member.bytes
            if payload!=value:raise ValueError('A retained standalone resource payload changed.')
        for key,fingerprint in selected.items():
            if export_mesh(saved_file.objects[key])['sourceFingerprint']!=fingerprint:
                raise ValueError('A saved standalone mesh differs from its compiled channels.')
        if digest(source_path)!=expected_sha256.lower():raise ValueError('The standalone source changed before publication.')
        os.replace(temporary,target_path);created=False
    finally:
        if created:temporary.unlink(missing_ok=True)
    return dict(source=dict(path=str(source_path.resolve()),sha256=expected_sha256.lower()),target=dict(path=str(target_path.resolve()),sha256=digest(target_path)),
                meshes=[dict(pathId=key,fingerprint=value)for key,value in selected.items()],materialFloatUpdates=material_proofs,rendererBoneUpdates=list(bone_updates.values()),cloth=cloth,changedObjects=sorted(changed),
                allOtherObjectBytesExact=True,resourcePayloadsExact=True,cabAndExternalReferencesUnchanged=True,savedObjectBytesExact=True)


def native_cloth_reference_mismatches(trees: dict[int, dict[str, Any]]) -> list[dict[str, Any]]:
    """Inspect stored cloth references without resigning potentially incompatible data."""
    repairs=[]
    for key,tree in trees.items():
        if 'clothData'not in tree or 'clothSelection'not in tree:continue
        for name in ('clothData','clothSelection','virtualDeformer'):
            pointer=tree.get(name)
            if not isinstance(pointer,dict)or pointer.get('m_FileID')!=0 or not pointer.get('m_PathID'):continue
            target=trees.get(pointer['m_PathID'])
            if target is None:raise ValueError('A native cloth share-data owner is missing.')
            for suffix,field in (('Hash','dataHash'),('Version','dataVersion')):
                cached=name+suffix
                if cached not in tree:continue
                value=target.get(field)
                if type(value)is not int or (suffix=='Hash'and value==0):
                    raise ValueError('Native cloth share data has no valid saved identity.')
                if tree[cached]!=value:
                    repairs.append(dict(component=key,reference=name,target=pointer['m_PathID'],field=cached,before=tree[cached],after=value))
    return repairs


def read_compiled_groom_defaults(reference: dict[str, str]) -> dict[str, float]:
    path=Path(reference['path'])
    if digest(path)!=reference['sha256']:raise ValueError('A compiled groom preset changed.')
    _,file=load_serialized_bundle(path,in_memory=True)
    materials=[obj for obj in file.objects.values()if obj.type.name=='Material']
    if len(materials)!=1:raise ValueError('A groom preset requires one compiled material.')
    return dict(materials[0].read_typetree()['m_SavedProperties']['m_Floats'])


def retune_groom_defaults(job: dict[str, Any]) -> dict[str, Any]:
    """Migrate matching preset values, retaining source-specific material values."""
    source,target=Path(job['source']),Path(job['target'])
    if digest(source)!=job['sha256']:raise ValueError('The groom preset source changed.')
    before=read_compiled_groom_defaults(job['before']);after=read_compiled_groom_defaults(job['after'])
    changed={key:(value,after[key])for key,value in before.items()if key in after and abs(value-after[key])>1e-7}
    _,file=load_serialized_bundle(source,in_memory=True);updates=[]
    for identifier in sorted(groom_material_identifiers(file)):
        values=dict(file.objects[identifier].read_typetree()['m_SavedProperties']['m_Floats'])
        selected={key:dict(before=values[key],after=value[1])for key,value in changed.items()
                  if key in values and abs(values[key]-value[0])<=1e-6}
        if selected:updates.append(dict(pathId=identifier,values=selected))
    if not updates:
        if not target.exists():target.hardlink_to(source)
        return dict(file=source.name,sha256=job['sha256'],bytes=source.stat().st_size,changed=False)
    receipt=prepare_standalone_part(source,job['sha256'],target,[],export_mesh=None,material_updates=updates)
    return dict(file=source.name,sha256=receipt['target']['sha256'],bytes=target.stat().st_size,changed=True,
        beforePreset=job['before'],afterPreset=job['after'],**receipt)


def prepare_native_cloth_state(trees: dict[int, dict[str, Any]]) -> list[dict[str, Any]]:
    """Keep invalid inherited simulations inactive while retaining their guard identities."""
    mismatches=native_cloth_reference_mismatches(trees)
    invalid={row['component']for row in mismatches}
    empty=set()
    for key,tree in trees.items():
        deformer=tree.get('deformer')
        if isinstance(deformer,dict)and isinstance(deformer.get('renderDeformerList'),list)and not any(pointer.get('m_PathID')for pointer in deformer['renderDeformerList']):empty.add(key)
    changes=[]
    for key,tree in trees.items():
        pointer=tree.get('virtualDeformer');target=pointer.get('m_PathID')if isinstance(pointer,dict)and pointer.get('m_FileID')==0 else None
        if tree.get('m_Enabled')and(key in invalid or key in empty or target in empty):
            reason='incompatible-share-data'if key in invalid else'no-render-deformer-target'
            changes.append(dict(component=key,field='m_Enabled',before=tree['m_Enabled'],after=0,reason=reason,referenceMismatches=[row for row in mismatches if row['component']==key]));tree['m_Enabled']=0
    return changes


def prepare_native_cloth(trees: dict[int, dict[str, Any]], replacements: dict[int, dict[str, Any]]) -> dict[str, Any]:
    """Prepare serialized render and simulation data for replaced prefab meshes."""
    import copy
    import numpy as np
    before=copy.deepcopy(trees)
    replaced=replacements
    def i32(value):
        return (int(value) + 2**31) % 2**32 - 2**31

    def vector(value):
        return np.array([value[axis] for axis in ('x', 'y', 'z')], dtype=np.float32)

    def serial_vector(value):
        return dict(zip(('x', 'y', 'z'), map(float, value)))

    def unit(value):
        length = np.sqrt(np.sum(value * value, dtype=np.float32), dtype=np.float32)
        return value / length if length > np.float32(1e-5) else np.zeros(3, dtype=np.float32)

    def mesh_hash(packet):
        return packet['vertexCount'] + sum(map(len, packet['subMeshes'])) + len(packet['subMeshes']) + int(packet['nativeReadable'])

    def vector_hashes(positions):
        # Installed MagicaCloth.DataHashExtensions.GetVectorDataHash(Vector3).
        bits = np.asarray(positions, dtype='<f4').reshape(-1, 3).view('<u4').astype(np.uint64)
        x = bits[:, 0] + np.uint64(1759446883)
        y = bits[:, 1] + np.uint64(1513705375)
        z = bits[:, 2] + np.uint64((-527125725) % 2**32)
        x ^= x << np.uint64(13)
        y ^= y >> np.uint64(17)
        z ^= z << np.uint64(15)
        x *= np.uint64((-1693206227) % 2**32)
        y *= np.uint64(1254033427)
        z *= np.uint64((-2046394269) % 2**32)
        return (x + y + z + np.uint64((-682290183) % 2**32)).tolist()

    render_deltas = {}
    data_changes = []
    for key, tree in list(trees.items()):
        deformer = tree.get('deformer')
        if not isinstance(deformer, dict) or 'sharedMesh' not in deformer:
            continue
        mesh_key = deformer['sharedMesh']['m_PathID']
        if mesh_key not in replaced:
            continue
        row = replaced[mesh_key]
        data_key = deformer['meshData']['m_PathID']
        data = trees[data_key]
        old_data = before[data_key]
        assert all(not data[name] for name in ('vertexInfoList', 'vertexWeightList', 'uvList', 'lineList', 'triangleList', 'vertexToTriangleInfoList', 'vertexToTriangleIndexList', 'childDataList'))
        assert len(old_data['vertexHashList']) == row['native']['vertexCount']
        # The native archives can retain older authoring hashes. The canonical
        # packet above establishes the actual mesh channels and index ownership.
        original_hashes = vector_hashes(row['raw']['vertices'])
        native_hash_differences = sum(a != b for a, b in zip(old_data['vertexHashList'], original_hashes))
        data['vertexCount'] = row['packet']['vertexCount']
        data['triangleCount'] = sum(map(len, row['packet']['subMeshes'])) // 3
        data['vertexHashList'] = vector_hashes(row['packet']['vertices'])
        data_delta = 2 * (data['vertexCount'] - old_data['vertexCount']) + data['triangleCount'] - old_data['triangleCount']
        data['dataHash'] = i32(old_data['dataHash'] + data_delta)
        delta = data_delta + mesh_hash(row['packet']) - mesh_hash(row['native'])
        deformer['dataHash'] = i32(deformer['dataHash'] + delta)
        tree['deformerHash'] = deformer['dataHash']
        tree['dataHash'] = i32(tree['dataHash'] + delta)
        render_deltas[key] = delta
        row['renderData'] = data
        data_changes.append(dict(kind='render', component=key, data=data_key, vertices=data['vertexCount'], delta=delta,
                                 nativeSerializedHashDifferences=native_hash_differences))

    virtual_deltas = {}
    for key, tree in list(trees.items()):
        deformer = tree.get('deformer')
        if not isinstance(deformer, dict) or 'renderDeformerList' not in deformer:
            continue
        data_key = deformer['meshData']['m_PathID']
        data = trees[data_key]
        child_delta = 0
        render_list_delta = 0
        for ordinal, child_ref in enumerate(deformer['renderDeformerList']):
            child_key = child_ref['m_PathID']
            child = data['childDataList'][ordinal]
            old_child = copy.deepcopy(child)
            render_list_delta = i32(render_list_delta * 31 + render_deltas.get(child_key, 0))
            child_delta = i32(child_delta * 31)
            if child_key not in render_deltas:
                continue
            render = trees[child_key]['deformer']
            row = replaced[render['sharedMesh']['m_PathID']]
            binding = row['binding']
            if binding is None:
                # With the same simulation vertices and indices, the original
                # virtual attachment is already the complete authored mapping.
                native, authored = row['raw'], row['packet']
                assert native['vertexCount'] == authored['vertexCount'] and native['subMeshes'] == authored['subMeshes'], 'A changed cloth topology requires attachment metadata.'
                assert native['boneIndices'] == authored['boneIndices']
                for channel in ('vertices', 'weights', 'bindPoses'):
                    assert np.array_equal(np.asarray(native[channel], np.float32).view(np.uint32), np.asarray(authored[channel], np.float32).view(np.uint32)), ('A changed cloth channel requires attachment metadata.', channel)
                assert len(native['shapes']) == len(authored['shapes'])
                for a, b in zip(native['shapes'], authored['shapes']):
                    assert a['name'] == b['name'] and len(a['frames']) == len(b['frames'])
                    for x, y in zip(a['frames'], b['frames']):
                        assert x['weight'] == y['weight'] and np.array_equal(np.asarray(x['vertices'], np.float32).view(np.uint32), np.asarray(y['vertices'], np.float32).view(np.uint32))
                row['identityCloth'] = True
                continue
            source_tangents = np.asarray(row['native']['tangents'], dtype=np.float32).reshape(-1, 4)
            matrix = np.asarray(binding['matrix'], dtype=np.float32).reshape(4, 4, order='F')
            scale = vector(row['renderData']['baseScale'])
            ratios = np.abs(scale) / np.linalg.norm(matrix[:3, :3], axis=0)
            metric = np.float32(np.sum(ratios, dtype=np.float32) / np.float32(3))
            assert metric > 0 and np.max(np.abs(ratios - metric)) <= metric * 1e-5
            info, weights, parents = [], [], []
            for vertex in binding['vertices']:
                anchor = vertex['anchor']
                packed = old_child['vertexInfoList'][anchor]
                count, start = packed >> 28, packed & 0x0fffffff
                info.append((count << 28) | len(weights))
                parents.append(old_child['parentIndexList'][anchor])
                for source_weight in old_child['vertexWeightList'][start:start + count]:
                    weight = copy.deepcopy(source_weight)
                    if not vertex['native']:
                        normal = unit(vector(weight['localNor']))
                        tangent = vector(weight['localTan'])
                        tangent = unit(tangent - normal * np.sum(normal * tangent, dtype=np.float32))
                        bitangent = np.cross(normal, tangent) * source_tangents[anchor, 3]
                        def transfer(value):
                            value = np.asarray(value, dtype=np.float32)
                            return (tangent * value[0] + bitangent * value[1]) + normal * value[2]
                        if any(vertex['offset']):
                            weight['localPos'] = serial_vector(vector(weight['localPos']) + transfer(vertex['offset']) * metric)
                        weight['localNor'] = serial_vector(unit(transfer(vertex['normal'])))
                        weight['localTan'] = serial_vector(unit(transfer(vertex['tangent'])))
                    weights.append(weight)
            child['vertexCount'] = len(info)
            child['vertexInfoList'] = info
            child['vertexWeightList'] = weights
            child['parentIndexList'] = parents
            child['childDataHash'] = i32(old_child['childDataHash'] + render_deltas[child_key])
            child_delta = i32(child_delta + render_deltas[child_key] + len(info) - old_child['vertexCount'])
            deformer['renderDeformerHashList'][ordinal] = trees[child_key]['dataHash']
        if not child_delta and not render_list_delta:
            continue
        data['dataHash'] = i32(data['dataHash'] + child_delta)
        delta = i32(child_delta + render_list_delta)
        deformer['dataHash'] = i32(deformer['dataHash'] + delta)
        tree['dataHash'] = i32(tree['dataHash'] + delta)
        tree['deformerHash'] = deformer['dataHash']
        virtual_deltas[key] = delta
        data_changes.append(dict(kind='virtual', component=key, data=data_key, childVertices=[c['vertexCount'] for c in data['childDataList']], delta=delta))

    for tree in trees.values():
        if 'virtualDeformer' in tree:
            virtual_key = tree['virtualDeformer']['m_PathID']
            if virtual_key in virtual_deltas:
                delta = virtual_deltas[virtual_key]
                tree['virtualDeformerHash'] = i32(tree['virtualDeformerHash'] + delta)
                tree['dataHash'] = i32(tree['dataHash'] + delta)
    return dict(renderDeltas=render_deltas,virtualDeltas=virtual_deltas,changes=data_changes)


if __name__=='__main__':
    import argparse
    parser=argparse.ArgumentParser(description='Compact independently compiled character resources.')
    parser.add_argument('--manifest',required=True)
    parser.add_argument('--native-index',required=True)
    parser.add_argument('--output',required=True)
    parser.add_argument('--workers',type=int,default=2,choices=(1,2,3))
    args=parser.parse_args()
    result=optimize_character_resources(args.manifest,args.native_index,args.output,args.workers)
    print(json.dumps(dict(status=result['status'],bundles=result['bundles'],bytes=result['bytes'])),flush=True)
