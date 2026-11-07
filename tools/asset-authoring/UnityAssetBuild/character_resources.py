"""Assemble standalone Unity character resources from prepared prefab inputs.

The input owner prepares geometry and materials. This module also prepares
serialized cloth attachments before closing the object references.
This module closes object references and preserves compressed texture spans.
"""
from pathlib import Path
from collections import defaultdict, deque
from functools import lru_cache
from typing import Any
import base64
import hashlib
import json
import re
import struct
import UnityPy
from UnityPy.files.SerializedFile import FileIdentifier
from UnityPy.streams import EndianBinaryReader
from mesh_recipe_codec import four_slot_skin

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


def texture_rgba32_storage(tree: dict[str, Any], payload: bytes, offset: int) -> dict[str, Any]:
    """Store an exact Main mip chain while retaining its sampler and dimensions."""
    if tree['m_TextureFormat']!=25 or tree['m_ImageCount']!=1 or tree['m_TextureDimension']!=2:
        raise ValueError('Exact Main storage requires one source BC7 2D texture.')
    width,height,levels=tree['m_Width'],tree['m_Height'],tree['m_MipCount']
    if min(width,height,levels)<1 or levels>max(width,height).bit_length() or offset<0 or offset%16:
        raise ValueError('Invalid exact Main mip layout or stream alignment.')
    expected=sum(max(1,width>>level)*max(1,height>>level)*4 for level in range(levels))
    stream=tree['m_StreamData']
    if len(payload)!=expected or not stream['path']or not stream['size']:
        raise ValueError('Exact Main storage needs a complete streamed mip chain.')
    return dict(tree,m_TextureFormat=4,m_CompleteImageSize=expected,m_StreamData=dict(stream,offset=offset,size=expected))


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

def write_mesh_frame(obj: Any,packet: dict[str, Any]) -> dict[str, Any]:
    """Replace the declared mesh frame while retaining all other vertex streams."""
    import numpy as np
    from UnityPy.helpers.MeshHelper import MeshHandler
    tree=obj.read_typetree();helper=MeshHandler(obj.read());helper.process()
    count=packet['vertexCount'];raw=bytearray(tree['m_VertexData']['m_DataSize'])
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

def load_serialized_bundle(path, *, in_memory=False) -> tuple[Any, Any]:
    source=Path(path)
    environment: Any = UnityPy.load(source.read_bytes(),path=str(source.parent)) if in_memory else UnityPy.load(str(source))
    files = [value for value in environment.file.files.values() if hasattr(value,'objects')]
    if len(files) != 1:
        raise ValueError('A character resource bundle must contain one serialized file.')
    return environment, files[0]

def typed_bundle_asset(environment: Any, asset: str, kind: str) -> Any:
    accepted={'AnimatorController','AnimatorOverrideController'}if kind=='RuntimeAnimatorController'else{kind}
    files=[value for value in environment.file.files.values()if hasattr(value,'objects')]
    if len(files)!=1:raise ValueError('A typed character address requires one serialized owner.')
    # Environment.container eagerly resolves preload dependencies. The root
    # address belongs to this serialized file and requires no dependency search.
    matches=[value for name,value in files[0].container.items()if name.casefold()==asset.casefold()and value.type.name in accepted]
    if len(matches)!=1:raise ValueError('A typed character resource address is missing or ambiguous: '+asset+' ('+kind+')')
    return matches[0]

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
    changed={};mips=[];grooms=[];support=[];pigment=[];native={}
    # Shader role, not an animal/character or mesh-name heuristic, owns this data.
    groom_materials={identifier for identifier,obj in file.objects.items()if obj.type.name=='Material'and
        '_CoreResponse'in dict(obj.read_typetree()['m_SavedProperties']['m_Floats'])}
    filters={};groom_ids=set()
    for obj in file.objects.values():
        if obj.type.name=='MeshFilter':
            tree=obj.read_typetree();filters[tree['m_GameObject']['m_PathID']]=tree['m_Mesh']
    for obj in file.objects.values():
        if obj.type.name not in ('SkinnedMeshRenderer','MeshRenderer'):continue
        tree=obj.read_typetree()
        if not any(p['m_FileID']==0 and p['m_PathID']in groom_materials for p in tree['m_Materials']):continue
        pointer=tree.get('m_Mesh')or filters.get(tree['m_GameObject']['m_PathID'])
        if pointer and pointer['m_FileID']==0 and pointer['m_PathID']:groom_ids.add(pointer['m_PathID'])
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
            support.append(dict(name=tree['m_Name'],**smooth_groom_support(tree,helper)))
            obj.save_typetree(tree);changed[identifier]=obj.data
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
        temporary.write_bytes(archive.save(packer='lzma'if grooms else'lz4'))
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


def prepare_standalone_part(source_path, expected_sha256, target_path, replacements, *, export_mesh, material_updates=(), repair_cloth=False):
    """Bake declared geometry, cloth and material values into a complete prefab resource."""
    import copy
    import math
    import os
    source_path,target_path=Path(source_path),Path(target_path)
    if source_path.resolve()==target_path.resolve() or digest(source_path)!=expected_sha256.lower():
        raise ValueError('The immutable standalone mesh source differs.')
    fingerprints=[row['sourceFingerprint']for row in replacements]
    if (not fingerprints and not material_updates and not repair_cloth)or len(set(fingerprints))!=len(fingerprints):
        raise ValueError('A standalone mesh batch requires unique source fingerprints.')
    environment,file=load_serialized_bundle(source_path,in_memory=True)
    before={key:serialized_object_bytes(obj)for key,obj in file.objects.items()}
    # Cloth owns its component metadata, not the complete sparse mesh frames.
    # Keep mesh trees outside that mutable metadata transaction.
    mutable_types: tuple[str,...]=('MonoBehaviour','AssetBundle','Material')if replacements or repair_cloth else('Material',)
    old_trees={key:obj.read_typetree()for key,obj in file.objects.items()if obj.type.name in mutable_types}
    trees=copy.deepcopy(old_trees)
    native={key:export_mesh(obj)for key,obj in file.objects.items()if replacements and obj.type.name=='Mesh'}
    cloth_replacements={};selected={};mesh_trees={}
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
    allowed=set(selected)|material_ids|({key for key,obj in file.objects.items()if obj.type.name=='MonoBehaviour'}if replacements or repair_cloth else set())
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
                meshes=[dict(pathId=key,fingerprint=value)for key,value in selected.items()],materialFloatUpdates=material_proofs,cloth=cloth,changedObjects=sorted(changed),
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
