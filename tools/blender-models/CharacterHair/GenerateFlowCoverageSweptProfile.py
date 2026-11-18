"""Native-source hair and beard surfaces, physical frames and attachment proofs."""
import os
for key in ('OMP_NUM_THREADS','OPENBLAS_NUM_THREADS','MKL_NUM_THREADS'):
    os.environ[key]='1'
from pathlib import Path
from typing import Any, TypedDict
import argparse
import ast
import collections
import copy
import gzip
import hashlib
import heapq
import json
import math
import re
import struct
import sys
import numpy as np
from PIL import Image

HERE=Path(__file__).resolve().parent
ROOT=next(p for p in HERE.parents if(p/'SolastaUnfinishedBusiness.sln').is_file())
PUBLIC=ROOT/'SolastaUnfinishedBusiness/BlenderModels/CharacterModels'
def read(path):
    path=Path(path)
    if path.suffix=='.gz':
        with gzip.open(path,'rt',encoding='utf-8-sig')as stream:return json.load(stream)
    return json.loads(path.read_text(encoding='utf-8-sig'))
def sha(path):
    with Path(path).open('rb')as stream:
        return hashlib.file_digest(stream,'sha256').hexdigest()
unit=lambda value:np.asarray(value)/np.maximum(np.linalg.norm(value,axis=-1,keepdims=True),1e-20)


def save_groom_packet(output, entry, packet):
    """Keep LOD-specific results distinct when native meshes share a fingerprint."""
    asset=entry['asset'];lod=entry['lod']
    if not isinstance(asset,str) or re.fullmatch(r'[A-Za-z0-9_]+',asset) is None or type(lod)is not int or lod not in (0,1,2):
        raise ValueError('A groom packet requires its declared asset and LOD owner.')
    name=entry.get('manifestEntry',{}).get('authoredMeshAsset')or(asset+'_LOD'+str(lod))
    if not isinstance(name,str)or re.fullmatch(r'[A-Za-z0-9_]+',name)is None:raise ValueError('A groom packet requires a safe declared mesh name.')
    path=Path(output)/(name+'.json')
    path.write_text(json.dumps(packet,separators=(',',':')),encoding='utf-8')
    return path


def write_generation(output, result, *, state='completed', created_payloads=()):
    """Declare created payloads separately from producer receipts and inputs."""
    if state not in ('completed','failed'):
        raise ValueError('A generation receipt requires its terminal state.')
    output=Path(output).resolve();receipt=output/'Generation.json';payloads={}
    for row in result['rows']:
        for path_key,hash_key in (('generatedPacket','generatedSha256'),('bindingPath','bindingSha256')):
            path=Path(row[path_key]);digest=row[hash_key]
            if not path.is_absolute()or not isinstance(digest,str)or re.fullmatch('[a-f0-9]{64}',digest)is None:
                raise ValueError('A generation output requires an absolute path and SHA256.')
            path=path.resolve(strict=True)
            if not path.is_relative_to(output)or path==receipt or not path.is_file():
                raise ValueError('A generation output escaped its producer owner.')
            if path in payloads and payloads[path]!=digest:
                raise ValueError('A generation declares conflicting output hashes.')
            payloads[path]=digest
    if created_payloads and state!='failed':raise ValueError('Unfinished payloads require a failed generation receipt.')
    for value in created_payloads:
        path=Path(value)
        if not path.is_absolute():raise ValueError('A partial generation output requires its absolute owner path.')
        path=path.resolve(strict=True)
        if not path.is_relative_to(output)or path==receipt or not path.is_file():
            raise ValueError('A partial generation output escaped its producer owner.')
        digest=sha(path)
        if path in payloads and payloads[path]!=digest:raise ValueError('A completed row changed during failure recording.')
        payloads[path]=digest
    for path,digest in payloads.items():
        if sha(path)!=digest:
            raise ValueError('A generation output changed before its receipt was completed.')
    document=dict(result,kind='CharacterResourceGeneration',schemaVersion=1,state=state,
                  outputs=[dict(path=str(path),sha256=payloads[path],role='payload')
                           for path in sorted(payloads,key=lambda value:str(value).casefold())])
    receipt.write_text(json.dumps(document,indent=2),encoding='utf-8')
    return document


def native_flow_surface(packet, native, binding, groom):
    """Identify native geometry independently of the chosen fine primitive."""
    if packet['vertexCount']!=native['vertexCount'] or not np.all(np.asarray(groom)[:,2]==0):return False
    if len(binding['vertices'])!=native['vertexCount'] or not all(row['native']for row in binding['vertices']):return False
    for channel in ('vertices','normals','tangents','colors','weights','boneIndices','bindPoses','subMeshes','shapes'):
        if packet[channel]!=native[channel]:raise ValueError('A native flow surface changed a protected source channel: '+channel)
    return True

# Source-only definitions moved verbatim from their recorded owners.
def encode_legacy_binding(binding,path):
    for vertex in binding['vertices']:
        for key in ('uv0','offset','normal','tangent'):
            vertex[key]=np.asarray(vertex[key],dtype=np.float32).tolist()
    roles=list(dict.fromkeys(v['role']for v in binding['vertices']))
    chunks=[b'UBSFB001',bytes.fromhex(binding['sourceFingerprint']),struct.pack('<?16fi',binding['cloth'],*binding['matrix'],len(roles))]
    for role in roles:
        raw=role.encode('utf-8');chunks.extend((struct.pack('<i',len(raw)),raw))
    chunks.append(struct.pack('<i',len(binding['vertices'])))
    for row in binding['vertices']:
        chunks.append(struct.pack('<i?ii12f',row['anchor'],row['native'],roles.index(row['role']),row['slot'],*row['uv0'],*row['offset'],*row['normal'],*row['tangent'],row['tangentW']))
    path.write_bytes(gzip.compress(b''.join(chunks),mtime=0))
    assert decode_binding(path)==binding

def native_frames(packet,matrix):
    m=np.asarray(matrix).reshape(4,4).T;linear=m[:3,:3]
    base_p=np.asarray(packet['vertices']).reshape(-1,3)
    base_n=np.asarray(packet['normals']).reshape(-1,3)
    base_t=np.asarray(packet['tangents']).reshape(-1,4)
    p=[];n=[];t=[];b=[]
    for frame in [None]+[f for s in packet['shapes']for f in s['frames']]:
        pp=base_p if frame is None else base_p+np.asarray(frame['vertices']).reshape(-1,3)
        nn=base_n if frame is None else base_n+np.asarray(frame['normals']).reshape(-1,3)
        tt=base_t[:,:3]if frame is None else base_t[:,:3]+np.asarray(frame['tangents']).reshape(-1,3)
        pp=pp@linear.T+m[:3,3];nn=unit(nn@np.linalg.inv(linear));tt=tt@linear.T;tt=unit(tt-nn*np.sum(tt*nn,axis=1)[:,None])
        p.append(pp);n.append(nn);t.append(tt);b.append(np.cross(nn,tt)*base_t[:,3,None]*np.sign(np.linalg.det(linear)))
    return np.asarray(p),np.asarray(n),np.asarray(t),np.asarray(b)


def native_position_frames(packet,matrix):
    """Match Unity's binary32 MultiplyPoint3x4 stores before selecting drivers."""
    transform=np.asarray(matrix,dtype='<f4').reshape(4,4).T
    base=np.asarray(packet['vertices'],dtype='<f4').reshape(-1,3)
    frames=[None]+[f for s in packet['shapes']for f in s['frames']]
    result=[]
    for frame in frames:
        p=base if frame is None else (base+np.asarray(frame['vertices'],dtype='<f4').reshape(-1,3)).astype('<f4')
        axes=[]
        for axis in range(3):
            first=(p[:,0]*transform[axis,0]).astype('<f4')
            second=(p[:,1]*transform[axis,1]).astype('<f4')
            third=(p[:,2]*transform[axis,2]).astype('<f4')
            axes.append((((first+second).astype('<f4')+third).astype('<f4')+transform[axis,3]).astype('<f4'))
        result.append(np.stack(axes,axis=1).astype(float))
    return np.asarray(result)

def source_texture_index(index,size,wrap):
    if wrap==0:return index%size
    if wrap==1:return np.clip(index,0,size-1)
    if wrap==2:
        phase=index%(2*size);return np.where(phase<size,phase,2*size-1-phase)
    if wrap==3:return np.clip(np.where(index<0,-index-1,index),0,size-1)
    raise ValueError('A source texture has an unsupported native wrap mode.')


def texture_sampler(row):
    if row is None:return None
    assert sha(row['path'])==row['sha256']
    pixels=np.asarray(Image.open(row['path']).convert('RGBA'))[::-1].astype(float)/255
    def sample(uv):
        uv=np.asarray(uv)*row['scale']+row['offset'];height,width=pixels.shape[:2]
        xy=uv*[width,height]-.5;low=np.floor(xy).astype(int);f=xy-low
        value=np.zeros((*uv.shape[:-1],4))
        for dx,dy,weight in ((0,0,(1-f[...,0])*(1-f[...,1])),(1,0,f[...,0]*(1-f[...,1])),(0,1,(1-f[...,0])*f[...,1]),(1,1,f[...,0]*f[...,1])):
            value+=pixels[(low[...,1]+dy)%height,(low[...,0]+dx)%width]*weight[...,None]
        return value
    return sample

def closest_triangle(point,triangles):
    from mesh_recipe_codec import closest_triangle as sample
    return sample(point,triangles)

def native_core_visibility(uv,triangles,slots,samplers):
    """Use each source material's actual alpha and UV transform for crown rays."""
    uv=np.asarray(uv,float);triangles=np.asarray(triangles,int);slots=np.asarray(slots,int)
    if len(slots)!=len(triangles)or np.any(slots<0)or np.any(slots>=len(samplers)):
        raise ValueError('Crown opacity requires the source material slot of every triangle.')
    lookup=[texture_sampler(row)for row in samplers]
    def visible(ids,b,c):
        vertices=uv[triangles[ids]]
        query=vertices[None,:,0]+b[:,:,None]*(vertices[None,:,1]-vertices[None,:,0])+c[:,:,None]*(vertices[None,:,2]-vertices[None,:,0])
        result=np.ones(b.shape,bool)
        for slot in np.unique(slots[ids]):
            sampler=lookup[slot]
            if sampler is None:continue
            selected=slots[ids]==slot
            result[:,selected]=sampler(query[:,selected])[:,:,3]>=samplers[slot].get('cutoff',.5)
        return result
    return visible


def crown_vertical_envelope(triangles,queries,weights=None,visibility=None):
    """Find the uppermost real triangle, excluding hidden interior sheets."""
    triangles=np.asarray(triangles,float);queries=np.asarray(queries,float)
    xy=triangles[:,:,[0,2]];a=xy[:,0];u=xy[:,1]-a;v=xy[:,2]-a;det=u[:,0]*v[:,1]-u[:,1]*v[:,0]
    valid=abs(det)>1e-14;source_ids=np.flatnonzero(valid);triangles=triangles[valid];xy=xy[valid];a=a[valid];u=u[valid];v=v[valid];det=det[valid]
    values=np.zeros((len(triangles),3))if weights is None else np.asarray(weights,float)[valid]
    result=np.full(len(queries),-np.inf);influence=np.zeros(len(queries))
    if not len(triangles)or not len(queries):return result,influence
    size=.01;origin=xy.min(axis=(0,1));cells=np.floor((queries[:,[0,2]]-origin)/size).astype(int)
    low=xy.min(axis=1);high=xy.max(axis=1)
    for cell in np.unique(cells,axis=0):
        query_ids=np.flatnonzero(np.all(cells==cell,axis=1));cell_low=origin+cell*size
        owners=np.flatnonzero(np.all(high>=cell_low-1e-9,axis=1)&np.all(low<=cell_low+size+1e-9,axis=1))
        if not len(owners):continue
        for first in range(0,len(query_ids),256):
            ids=query_ids[first:first+256];r=queries[ids,None][:,:,[0,2]]-a[None,owners]
            b=(r[:,:,0]*v[None,owners,1]-r[:,:,1]*v[None,owners,0])/det[owners]
            c=(u[None,owners,0]*r[:,:,1]-u[None,owners,1]*r[:,:,0])/det[owners]
            inside=(b>=-1e-9)&(c>=-1e-9)&(b+c<=1+1e-9)
            if visibility is not None:inside&=visibility(source_ids[owners],b,c)
            heights=triangles[None,owners,0,1]+b*(triangles[None,owners,1,1]-triangles[None,owners,0,1])+c*(triangles[None,owners,2,1]-triangles[None,owners,0,1])
            heights=np.where(inside,heights,-np.inf);selected=heights.argmax(axis=1);batch=np.arange(len(ids))
            result[ids]=heights[batch,selected]
            interpolated=values[None,owners,0]+b*(values[None,owners,1]-values[None,owners,0])+c*(values[None,owners,2]-values[None,owners,0])
            influence[ids]=interpolated[batch,selected]
    return result,influence


def fit_crown_clearance(points,core_triangles,scalp_triangles,*,margin=.0015,maximum=.008,core_visibility=None):
    """Fit the visible outer crown to the compatible source scalp in metres.

    Hidden inner sheets do not drive the fit. All vertices share the same
    height field, so strands and ornaments follow their core without adding
    hair to uncovered or shaved areas.
    """
    p=np.asarray(points,float);faces=np.asarray(core_triangles,int);scalp=np.asarray(scalp_triangles,float)
    if p.ndim!=2 or p.shape[1]!=3 or faces.ndim!=2 or faces.shape[1]!=3 or scalp.ndim!=3 or scalp.shape[1:]!=(3,3):
        raise ValueError('Crown fitting requires complete source points and triangles.')
    if not np.isfinite(p).all()or not np.isfinite(scalp).all()or not 0<margin<maximum:
        raise ValueError('Crown fitting has invalid coordinates or clearance bounds.')
    top=float(scalp[:,:,1].max());bottom=float(scalp[:,:,1].min());span=min(.05,max(.02,(top-bottom)*.25));start=top-span
    delta=np.cross(scalp[:,1]-scalp[:,0],scalp[:,2]-scalp[:,0]);length=np.linalg.norm(delta,axis=1)
    keep=(scalp[:,:,1].max(axis=1)>start+span*.5)&(np.abs(delta[:,1])>length*.25)&(length>1e-14)
    scalp=scalp[keep]
    if not len(scalp):raise ValueError('No upper scalp surface owns the crown fit.')
    t=np.clip((p[:,1]-start)/span,0,1);weights=t*t*(3-2*t)
    selected_ids=np.flatnonzero(p[faces,1].max(axis=1)>start+span*.5);selected=faces[selected_ids]
    bary=np.array([[1,0,0],[0,1,0],[0,0,1],[.5,.5,0],[0,.5,.5],[.5,0,.5],[1/3,1/3,1/3]])
    queries=np.einsum('bi,tij->tbj',bary,p[selected]).reshape(-1,3)
    envelope,_=crown_vertical_envelope(scalp,queries)
    visible=None if core_visibility is None else lambda ids,b,c:core_visibility(selected_ids[ids],b,c)
    outer,influence=crown_vertical_envelope(p[selected],queries,weights[selected],visible)
    # Buried roots are part of the haircut, including shaved/partial styles.
    # Only near-contact outer surfaces at the crown drive this bounded lift.
    candidates=np.isfinite(envelope)&np.isfinite(outer)&(envelope>start+span*.75)&(outer>start+span*.75)&(influence>1e-8)
    finite=np.isfinite(envelope)&np.isfinite(outer)
    gap=np.zeros_like(envelope);np.subtract(envelope,outer,out=gap,where=finite)
    buried=finite&(gap>maximum*.5)
    active=candidates&~buried
    amplitude=max(0.,float(np.max((envelope[active]+margin-outer[active])/influence[active])))if active.any()else 0.
    penetration=max(0.,float(np.max(envelope[active]-outer[active])))if active.any()else 0.
    if amplitude>maximum:raise ValueError('The source crown needs more than the bounded clearance field: '+str(amplitude))
    moved=p.copy();moved[:,1]+=amplitude*weights;derivative=amplitude*6*t*(1-t)/span
    return moved,derivative,dict(policy='source-scalp-crown-v1',sourceTriangles=len(scalp),testedSamples=int(active.sum()),
        maximumSourcePenetration=penetration,maximumLift=amplitude,minimumClearance=margin,fieldStart=start,fieldTop=top,
        addedVertices=0,lowerHairlineUnchanged=True,outerSurfaceOnly=True,deepRootSamplesPreserved=int(buried.sum()))


def measure_source_crown(packet,renderer_to_prefab,scalp,samplers):
    """Measure the alpha-visible native core, excluding generated fine sheets."""
    matrix=np.asarray(renderer_to_prefab,float).reshape(4,4).T
    local=np.asarray(packet['vertices'],float).reshape(-1,3)
    positions=local@matrix[:3,:3].T+matrix[:3,3]
    triangles=np.asarray([v for sub in packet['subMeshes']for v in sub],int).reshape(-1,3)
    slots=np.concatenate([np.full(len(sub)//3,i,int)for i,sub in enumerate(packet['subMeshes'])])
    source_field=next((v['values']for v in packet['uv']if v['channel']==2),None)
    role=np.zeros(len(local))if source_field is None else np.asarray(source_field).reshape(len(local),-1)[:,2]
    core=np.all(role[triangles]==0,axis=1);triangles=triangles[core];slots=slots[core]
    uv=np.asarray(next(v['values']for v in packet['uv']if v['channel']==0)).reshape(len(local),-1)[:,:2]
    visibility=native_core_visibility(uv,triangles,slots,samplers)
    result=fit_crown_clearance(positions,triangles,scalp,core_visibility=visibility)[2]
    if source_field is None:result['nativeCoreRoleImplicit']=True
    return result


def apply_crown_field(packet,renderer_to_prefab,amplitude,start,top):
    """Move only position frames; retain the source groom's shading and skin."""
    if not 0<=amplitude<=.008 or not np.isfinite([amplitude,start,top]).all()or top<=start:
        raise ValueError('A crown field requires bounded source-metre parameters.')
    matrix=np.asarray(renderer_to_prefab,float).reshape(4,4).T
    if not np.isfinite(matrix).all()or not np.allclose(matrix[3],[0,0,0,1],atol=1e-12):
        raise ValueError('A crown field requires its actual affine renderer transform.')
    inverse=np.linalg.inv(matrix[:3,:3]);result=copy.deepcopy(packet)
    if amplitude==0:return result
    def deform(local):
        local=np.asarray(local,float).reshape(-1,3);common=local@matrix[:3,:3].T+matrix[:3,3]
        t=np.clip((common[:,1]-start)/(top-start),0,1)
        delta=np.zeros_like(common);delta[:,1]=amplitude*t*t*(3-2*t)
        return(local+delta@inverse.T).astype(np.float32)
    base=np.asarray(packet['vertices']).reshape(-1,3);positions=deform(base);frames=[positions]
    for before,after in zip(packet['shapes'],result['shapes']):
        for old,new in zip(before['frames'],after['frames']):
            posed=deform(base+np.asarray(old['vertices']).reshape(-1,3))
            new['vertices']=(posed-positions).ravel().tolist();frames.append(posed)
    result['vertices']=positions.ravel().tolist();bounds=np.asarray(packet['bounds'])
    low=np.minimum(bounds[:3]-bounds[3:]*.5,np.min([frame.min(axis=0)for frame in frames],axis=0))
    high=np.maximum(bounds[:3]+bounds[3:]*.5,np.max([frame.max(axis=0)for frame in frames],axis=0))
    result['bounds']=np.r_[(low+high)*.5,high-low].tolist();result['sourceFingerprint']=fingerprint(result)[0]
    return result


def initialize_core_coordinates(packet,renderer_to_prefab):
    """Initialize only owned UV2 XY from native UV0 and measured card flow.

    The field is unoriented: V is a local strand axis, not a root-tip label.
    Native UV seams are retained because connected components use real vertex
    indices. The caller owns the unchanged geometry, skin and physical frames.
    """
    source_uv=next(u for u in packet['uv']if u['channel']==0);groom_uv=next(u for u in packet['uv']if u['channel']==2)
    q=np.asarray(source_uv['values'],dtype=np.float32).astype(np.float64).reshape(-1,4)[:,:2];g=np.asarray(groom_uv['values']).reshape(-1,4).copy()
    p=np.asarray(packet['vertices'],dtype=np.float32).astype(np.float64).reshape(-1,3);matrix=np.asarray(renderer_to_prefab).reshape(4,4).T;world=p@matrix[:3,:3].T+matrix[:3,3]
    slot_rows=[]
    for slot,indices in enumerate(packet['subMeshes']):
        tri=np.asarray(indices).reshape(-1,3);roles=g[tri,2]
        assert np.all(roles==roles[:,:1]);core=tri[roles[:,0]==0];cards=tri[roles[:,0]>0]
        if not len(core) or not len(cards):continue
        cp=world[cards];cg=g[cards,:2];edge=np.stack((cg[:,1]-cg[:,0],cg[:,2]-cg[:,0]),axis=2);det=np.linalg.det(edge);valid=abs(det)>1e-10
        cp=cp[valid];edge=edge[valid]
        if not len(cp):continue
        dp=np.stack((cp[:,1]-cp[:,0],cp[:,2]-cp[:,0]),axis=2);derivative=dp@np.linalg.inv(edge);flow=derivative[:,:,1];flow/=np.maximum(np.linalg.norm(flow,axis=1)[:,None],1e-20);centers=cp.mean(axis=1)
        adjacency=collections.defaultdict(set)
        for a,b,c in core:
            adjacency[int(a)].update((int(b),int(c)));adjacency[int(b)].update((int(a),int(c)));adjacency[int(c)].update((int(a),int(b)))
        components=[];seen=set()
        for start in adjacency:
            if start in seen:continue
            current=[start];seen.add(start);component=[]
            while current:
                i=current.pop();component.append(i)
                for j in adjacency[i]:
                    if j not in seen:seen.add(j);current.append(j)
            components.append(np.asarray(component,int))
        processed=0;held=0;charts=[]
        for vertices in components:
            selected=np.all(np.isin(core,vertices),axis=1);triangles=core[selected];pq=world[triangles];qq=q[triangles]
            uv_edges=np.stack((qq[:,1]-qq[:,0],qq[:,2]-qq[:,0]),axis=2);det=np.linalg.det(uv_edges)
            # A collapsed/near-collinear native UV chart cannot be inverted
            # into a meaningful metric field (one real source produced 3e8).
            # In that capability case use the same physical source component
            # and measured card axis; native UV0 remains exactly unchanged.
            valid=(abs(det)>1e-12)&(np.linalg.cond(uv_edges)<1e5)
            pq=pq[valid];uv_edges=uv_edges[valid]
            if not len(pq):
                native_points=world[triangles]
                physical_area=np.linalg.norm(np.cross(native_points[:,1]-native_points[:,0],native_points[:,2]-native_points[:,0]),axis=1)*.5
                if physical_area.sum()<=1e-14:held+=len(vertices);continue
                origin=world[vertices].mean(axis=0);center=native_points.mean(axis=1)
                nearest=np.argmin(np.sum((center[:,None,:]-centers[None,:,:])**2,axis=2),axis=1)
                covariance=np.einsum('n,ni,nj->ij',physical_area,flow[nearest],flow[nearest])
                values,vectors=np.linalg.eigh(covariance);v=vectors[:,-1]
                point_covariance=(world[vertices]-origin).T@(world[vertices]-origin)
                _,axes=np.linalg.eigh(point_covariance);normal=axes[:,0]
                v-=normal*np.dot(normal,v);v/=max(np.linalg.norm(v),1e-20)
                if np.linalg.norm(v)<.5:held+=len(vertices);continue
                v*=1 if v[np.argmax(abs(v))]>=0 else -1
                u=np.cross(v,normal);u/=max(np.linalg.norm(u),1e-20)
                g[vertices,0]=(world[vertices]-origin)@u/.01
                g[vertices,1]=(world[vertices]-origin)@v/.01
                processed+=len(vertices);charts.append(dict(vertices=len(vertices),coordinateAuthority='PHYSICAL_SOURCE_COMPONENT_CARD_AXIS_NATIVE_UV0_NOT_INVERTIBLE',
                    maximumAllowedUvEdgeConditionNumber=1e5,physicalOrigin=origin.tolist(),worldAxisU=u.tolist(),worldAxisV=v.tolist(),metresPerUnit=.01,
                    flowCoherence=float(values[-1]/max(values.sum(),1e-20))))
                continue
            center=pq.mean(axis=1);nearest=np.empty(len(center),int)
            for start in range(0,len(center),128):nearest[start:start+128]=np.argmin(np.sum((center[start:start+128,None,:]-centers[None,:,:])**2,axis=2),axis=1)
            surface=np.stack((pq[:,1]-pq[:,0],pq[:,2]-pq[:,0]),axis=2)@np.linalg.inv(uv_edges)
            target=np.einsum('ni,nij->nj',flow[nearest],surface);target/=np.maximum(np.linalg.norm(target,axis=1)[:,None],1e-20)
            area=np.linalg.norm(np.cross(pq[:,1]-pq[:,0],pq[:,2]-pq[:,0]),axis=1)*.5
            covariance=np.einsum('n,ni,nj->ij',area,target,target)
            values,vectors=np.linalg.eigh(covariance);v=vectors[:,-1];v*=1 if v[np.argmax(abs(v))]>=0 else -1;u=np.array([v[1],-v[0]])
            su=np.linalg.norm(surface@u,axis=1);sv=np.linalg.norm(surface@v,axis=1)
            def weighted_median(data):
                order=np.argsort(data);weights=area[order];return float(data[order[np.searchsorted(np.cumsum(weights),weights.sum()*.5)]])
            scales=np.array([weighted_median(su),weighted_median(sv)])/.01
            if not np.isfinite(scales).all() or np.any(scales<=1e-8):held+=len(vertices);continue
            g[vertices,0]=(q[vertices]@u)*scales[0];g[vertices,1]=(q[vertices]@v)*scales[1]
            processed+=len(vertices);charts.append(dict(vertices=len(vertices),coordinateAuthority='CONDITIONED_NATIVE_UV0_WITH_CARD_AXIS',nativeChartDirection=v.tolist(),unitsPerUv=scales.tolist(),flowCoherence=float(values[-1]/max(values.sum(),1e-20))))
        cross=np.cross(world[tri[:,1]]-world[tri[:,0]],world[tri[:,2]]-world[tri[:,0]]);area=np.linalg.norm(cross,axis=1)*.5
        mapped=g[core,:2]
        determinants=np.linalg.det(np.stack((mapped[:,1]-mapped[:,0],mapped[:,2]-mapped[:,0]),axis=2))
        source_determinants=np.linalg.det(np.stack((q[core[:,1]]-q[core[:,0]],q[core[:,2]]-q[core[:,0]]),axis=2))
        core_area=area[roles[:,0]==0]
        measurable=(core_area>1e-14)&(np.abs(source_determinants)>1e-12)
        unmapped=measurable&(np.abs(determinants)<=1e-12)
        assert not np.any(unmapped),'A measurable native UV0 core triangle has no metric UV2 field.'
        slot_rows.append(dict(slot=slot,coreTriangles=len(core),cardTriangles=len(cards),coreAreaFraction=float(area[roles[:,0]==0].sum()/max(area.sum(),1e-20)),coreVerticesWithMetricCoordinates=processed,heldDegenerateCoreVertices=held,charts=charts,
            measurableCoreTriangles=int(measurable.sum()),measurableCoreTrianglesWithMetricCoordinates=int((measurable&~unmapped).sum()),
            nativeDegenerateUvCoreTriangles=int((~measurable).sum()),measurableCoreAreaSquareMetres=float(core_area[measurable].sum())))
    # A very small but nonzero source triangle can collapse after an arbitrary
    # chart rotation is quantized to f32. Preserve the exact authored field
    # domain with a bounded representability adjustment in owned UV2 only.
    # This is not a change to native UV0 or a relaxed nondegeneracy threshold.
    quantized=g.astype(np.float32)
    for row in slot_rows:
        tri=np.asarray(packet['subMeshes'][row['slot']]).reshape(-1,3)
        core=tri[np.all(quantized[tri,2]==0,axis=1)]
        pq=world[core];qq=q[core]
        area=np.linalg.norm(np.cross(pq[:,1]-pq[:,0],pq[:,2]-pq[:,0]),axis=1)*.5
        determinant=lambda values:np.linalg.det(np.stack((values[:,1]-values[:,0],values[:,2]-values[:,0]),axis=2))
        valid=(area>1e-14)&(np.abs(determinant(qq))>1e-12)
        def current_determinant():return determinant(quantized[core,:2].astype(np.float64))
        bad=valid&(np.abs(current_determinant())<=1e-12)
        before=quantized[:,:2].copy();initial_bad=int(bad.sum());maximum_steps=0
        while np.any(bad):
            index=int(np.flatnonzero(bad)[0]);good=valid&~bad;repaired=False
            for vertex in core[index]:
                incident=np.flatnonzero(np.any(core==vertex,axis=1))
                for axis in (0,1):
                    old=quantized[vertex,axis]
                    for direction in (np.float32(np.inf),np.float32(-np.inf)):
                        proposed=old
                        for steps in range(1,9):
                            proposed=np.nextafter(proposed,direction)
                            if not np.isfinite(proposed)or abs(float(proposed)-float(old))>1e-4:break
                            quantized[vertex,axis]=proposed
                            values=determinant(quantized[core[incident],:2].astype(np.float64))
                            retained=np.abs(values)>1e-12
                            if retained[np.flatnonzero(incident==index)[0]] and np.all(retained[good[incident]]):
                                repaired=True;maximum_steps=max(maximum_steps,steps);break
                            quantized[vertex,axis]=old
                        if repaired:break
                    if repaired:break
                if repaired:break
            assert repaired,'The owned core UV2 field cannot preserve its f32 native-chart domain within eight ULP/1e-4 units.'
            remaining=valid&(np.abs(current_determinant())<=1e-12)
            assert remaining.sum()<bad.sum()
            bad=remaining
        delta=np.abs(quantized[:,:2].astype(np.float64)-before.astype(np.float64))
        row.update(measurableCoreTriangles=int(valid.sum()),measurableCoreTrianglesWithMetricCoordinates=int(valid.sum()),
            nativeDegenerateUvCoreTriangles=int((~valid).sum()),float32CollapsedBeforeRepair=initial_bad,
            float32RepairedVertices=int(np.any(delta>0,axis=1).sum()),maximumUv2RepresentabilityDelta=float(delta.max()),maximumUv2RepairUlpSteps=maximum_steps)
    groom_uv['values']=quantized.ravel().tolist()
    return slot_rows

sys.path.insert(0,str(ROOT/'tools/blender-models/CharacterFaces'))
from collect_sources import fingerprint

sys.path.insert(0,str(ROOT/'tools/asset-authoring/UnityAssetBuild'))
from mesh_recipe_codec import source_surface_skin, SurfaceSkinSupportError

_legacy_encode_binding = encode_legacy_binding

def decode_binding(path) -> 'dict[str, Any]':
    """Read the GCS legacy or explicit-native-surface contract without inference."""
    import math
    data=gzip.decompress(Path(path).read_bytes());at=8
    magic=data[:8];assert magic in (b'UBSFB001',b'UBSFB003',b'UBSFB004'),'Unsupported GCS surface binding format.'
    explicit=magic in (b'UBSFB003',b'UBSFB004')
    def unpack(fmt):
        nonlocal at
        size=struct.calcsize('<'+fmt);assert at+size<=len(data),'Truncated GCS surface binding.'
        values=struct.unpack_from('<'+fmt,data,at);at+=size;return values
    assert len(data)>=105
    fp=data[at:at+32].hex();at+=32
    cloth=unpack('?')[0];matrix=list(unpack('16f'))
    source_count=unpack('i')[0]if explicit else None
    roles=[];role_count=unpack('i')[0];assert 0<role_count<=len(data)//4
    for _ in range(role_count):
        length=unpack('i')[0];assert 0<length<=len(data)-at
        roles.append(data[at:at+length].decode('utf-8'));at+=length
    vertices=[];count=unpack('i')[0];assert 0<count<=len(data)//61
    for _ in range(count):
        anchor,native,role,slot=unpack('i?ii');assert 0<=role<len(roles)
        values=unpack('12f');assert all(math.isfinite(v)for v in values)
        vertex=dict(anchor=anchor,native=native,role=roles[role],slot=slot,uv0=list(values[:2]),
            offset=list(values[2:5]),normal=list(values[5:8]),tangent=list(values[8:11]),tangentW=values[11])
        if explicit:
            vertex['surface']=dict(triangle=list(unpack('3i')),barycentric=list(unpack('3f')))if unpack('?')[0]else None
        if magic==b'UBSFB004':
            vertex['geometrySurface']=dict(triangle=list(unpack('3i')),barycentric=list(unpack('3f')))if unpack('?')[0]else None
        vertices.append(vertex)
    assert at==len(data),'Trailing GCS surface binding bytes.'
    result: dict[str, Any]=dict(sourceFingerprint=fp,cloth=cloth,matrix=matrix,vertices=vertices)
    if explicit:result.update(schemaVersion=4 if magic==b'UBSFB004'else 3,sourceVertexCount=source_count)
    return result

def validate_surface_provenance(binding,native):
    """Check the actual native slot triangles and wire-precision source UVs."""
    version=binding.get('schemaVersion',1);assert version in (1,3,4)
    if version==1:
        assert 'sourceVertexCount'not in binding and all('surface'not in v for v in binding['vertices'])
        return dict(format='UBSFB001',explicitSurfaceVertices=0,legacyFieldsRetained=True)
    count=native['vertexCount'];assert binding['sourceVertexCount']==count
    assert binding['sourceFingerprint']==native['sourceFingerprint']
    assert len(binding['vertices'])>=count
    slots=[{tuple(sorted(map(int,t)))for t in np.asarray(indices,int).reshape(-1,3)}for indices in native['subMeshes']]
    uv=np.asarray(next(u['values']for u in native['uv']if u['channel']==0),dtype='<f4').reshape(-1,4)[:,:2].astype(float)
    native_triangles=set().union(*slots)
    source_positions=np.stack([np.asarray(native['vertices'],dtype='<f4').reshape(-1,3)]+
        [np.asarray(native['vertices'],dtype='<f4').reshape(-1,3)+np.asarray(f['vertices'],dtype='<f4').reshape(-1,3)
         for shape in native['shapes']for f in shape['frames']]).astype(float)
    explicit=0;maximum_uv=0.
    for index,vertex in enumerate(binding['vertices']):
        anchor=vertex['anchor'];assert type(anchor)is int and 0<=anchor<count
        if index<count:assert vertex['native']and anchor==index
        assert 'surface'in vertex
        if vertex['native']:
            assert vertex['surface']is None
            if version==4:assert vertex['geometrySurface']is None
            continue
        surface=vertex['surface'];assert isinstance(surface,dict)and set(surface)=={'triangle','barycentric'},'Recover explicit source provenance before modifying legacy fine vertices.'
        corners=surface['triangle'];assert len(corners)==len(set(corners))==3 and anchor in corners
        assert all(type(i)is int and 0<=i<count for i in corners)
        slot=vertex['slot'];assert type(slot)is int and 0<=slot<len(slots)
        assert tuple(sorted(corners))in slots[slot],'Explicit surface is not a triangle in its actual native material slot.'
        weights=np.asarray(surface['barycentric'],dtype='<f4').astype(float)
        assert weights.shape==(3,)and np.isfinite(weights).all()and np.all((weights>=0)&(weights<=1))and abs(float(weights.sum())-1)<=1e-6
        delta=float(np.max(np.abs(weights@uv[corners]-np.asarray(vertex['uv0'],dtype='<f4').astype(float))))
        assert delta<=1e-6,'Explicit surface source UV changed.'
        if version==4 and vertex['geometrySurface']is not None:
            geometry=vertex['geometrySurface'];g=geometry['triangle'];w=np.asarray(geometry['barycentric'],dtype='<f4').astype(float)
            assert len(g)==len(set(g))==3 and tuple(sorted(g))in native_triangles
            assert w.shape==(3,)and np.isfinite(w).all()and np.all((w>=0)&(w<=1))and abs(w.sum()-1)<=1e-6
            own=np.einsum('pij,i->pj',source_positions[:,corners],weights)
            other=np.einsum('pij,i->pj',source_positions[:,g],w)
            assert np.max(np.linalg.norm(own-other,axis=1))<=2e-6,'Geometry and source-UV surfaces disagree in a stored physical pose.'
        maximum_uv=max(maximum_uv,delta);explicit+=1
    return dict(format='UBSFB004'if version==4 else 'UBSFB003',sourceVertexCount=count,explicitSurfaceVertices=explicit,
        actualNativeSlotTrianglesAll=True,maximumWireUvError=maximum_uv,nativePrefixAndNativeNullAll=True)

def prepare_surface_binding(binding,native):
    """Promote only retained producer provenance; never infer old fine triangles."""
    if binding.get('schemaVersion',1)>=3 or any('surface'in v for v in binding['vertices']):
        binding.update(schemaVersion=binding.get('schemaVersion',3),sourceVertexCount=native['vertexCount'])
        for vertex in binding['vertices']:
            if vertex['native']:vertex['surface']=None
            elif isinstance(vertex.get('surface'),dict):
                vertex['surface']['barycentric']=np.asarray(vertex['surface']['barycentric'],dtype='<f4').tolist()
            if binding['schemaVersion']==4:
                vertex.setdefault('geometrySurface',None)
                if vertex['geometrySurface']is not None:
                    vertex['geometrySurface']['barycentric']=np.asarray(vertex['geometrySurface']['barycentric'],dtype='<f4').tolist()
    return validate_surface_provenance(binding,native)

def encode_binding(binding,path):
    """Keep old 001 bytes; forward explicit triangles to the shared 003 writer."""
    binding['matrix']=np.asarray(binding['matrix'],dtype='<f4').tolist()
    if binding.get('schemaVersion',1)==1:
        assert all('surface'not in v for v in binding['vertices']),'Refuse to discard explicit provenance into UBSFB001.'
        _legacy_encode_binding(binding,path)
        return
    assert binding['schemaVersion']in (3,4)and 'sourceVertexCount'in binding
    from mesh_recipe_codec import encode_surface_binding
    for vertex in binding['vertices']:
        for key in ('uv0','offset','normal','tangent'):
            vertex[key]=np.asarray(vertex[key],dtype='<f4').tolist()
    document=dict(schemaVersion=binding['schemaVersion'],sourceFingerprint=binding['sourceFingerprint'],
        sourceVertexCount=binding['sourceVertexCount'],rendererToPrefab=binding['matrix'],
        clothBindingGenerated=binding['cloth'],vertices=binding['vertices'])
    encode_surface_binding(document,path)
    assert decode_binding(path)==binding

def groom_added_indices(packet,native,fine,budget,curved):
    """Count only the explicit new fine/carrier allocation in source budgets."""
    if budget['fineOnly']or'sourceCarrierFacesBySlot'in budget:
        total=0
        for sub in packet['subMeshes']:
            values=np.asarray(fine)[np.asarray(sub,int).reshape(-1,3)]
            if np.any(np.any(values,axis=1)&~np.all(values,axis=1)):
                raise ValueError('A groom allocation mixes fine and carrier triangle ownership.')
            total+=int(np.all(values,axis=1).sum())*3
        if 'sourceCarrierFacesBySlot'in budget:
            if curved is None:raise ValueError('Carrier index allocation requires its explicit render owner.')
            if curved.get('flowCarrierCells',{}).get('model')!='primary-loft':
                total+=sum(curved['triangleCountsBySlot'])*3
        return total
    return max(0,sum(map(len,packet['subMeshes']))-sum(map(len,native['subMeshes'])))



def prove_row(row, profile: 'GroomProfile'):
    native = read(row['nativePacketPath'])
    packet = read(row['generatedPacket'])
    count = native['vertexCount']
    binding = decode_binding(Path(row['bindingPath']))
    surface_provenance = validate_surface_provenance(binding, native)
    matrix=np.asarray(binding['matrix'],dtype='<f4').reshape(4,4).T
    sign=float(np.sign(np.linalg.det(matrix[:3,:3])))
    target_hands=np.asarray(packet['tangents'],dtype='<f4').reshape(-1,4)[:,3]
    for index,vertex in enumerate(binding['vertices']):
        if not vertex['native']:
            assert target_hands[index]==vertex['tangentW']*sign,'Owned tangent hand disagrees with its declared source frame.'
    assert len(binding['vertices']) == packet['vertexCount'] and binding['sourceFingerprint'] == row['manifestEntry']['sourceFingerprint']
    assert all(v['native'] and v['anchor'] == i for i, v in enumerate(binding['vertices'][:count]))
    for key, width in [('vertices', 3), ('normals', 3), ('tangents', 4), ('weights', 4), ('boneIndices', 4)]:
        dtype = '<i4' if key == 'boneIndices' else '<f4'
        assert np.array_equal(np.asarray(packet[key], dtype=dtype).reshape(-1, width)[:count], np.asarray(native[key], dtype=dtype).reshape(-1, width)), key
    assert np.array_equal(np.asarray(packet['bindPoses'], dtype='<f4'), np.asarray(native['bindPoses'], dtype='<f4'))
    for native_uv in native['uv']:
        if native_uv['channel'] == 2:
            continue
        authored_uv = next(u for u in packet['uv'] if u['channel'] == native_uv['channel'])
        assert authored_uv['dimension'] == native_uv['dimension']
        assert np.array_equal(np.asarray(authored_uv['values'], dtype='<f4').reshape(-1, 4)[:count], np.asarray(native_uv['values'], dtype='<f4').reshape(-1, 4))
    assert len(packet['shapes']) == len(native['shapes'])
    for shape, source_shape in zip(packet['shapes'], native['shapes']):
        assert shape['name'] == source_shape['name'] and len(shape['frames']) == len(source_shape['frames'])
        for frame, source_frame in zip(shape['frames'], source_shape['frames']):
            assert frame['weight'] == source_frame['weight']
            for key in ('vertices', 'normals', 'tangents'):
                assert np.array_equal(np.asarray(frame[key], dtype='<f4').reshape(-1, 3)[:count], np.asarray(source_frame[key], dtype='<f4').reshape(-1, 3)), key
    uv = np.asarray(next(u['values'] for u in packet['uv'] if u['channel'] == 2), dtype='<f4').reshape(-1, 4)
    fine = uv[:, 2] >= 17
    curved = row.get('curvedCoreSurface')
    primary_count = curved['vertices'] if curved and curved.get('flowCarrierCells',{}).get('model')=='primary-loft' else 0
    core_count = curved['vertices']-primary_count if curved else 0
    assert fine.sum() + core_count == row['addedVertices']
    assert packet['vertexCount'] < 65536
    if profile.get('retainedSourceBudget'):
        reference=retained_groom_reference(row,native)
        if row.get('retainedBudget')is not None:
            if row.get('curvedCoreSurface',{}).get('flowCarrierCells'):
                contact=row['sourceContactDomain']
                budget=retained_carrier_allocation(native,reference,row['rendererToPrefab'],row['materials'],
                    carrier_render_faces(row['curvedCoreSurface']),contact['settings']['maximumSupportOffsetMetres'])[0]
            else:budget=retained_groom_budget(native,reference,fine_only=bool(profile.get('layeredAtlasCards')and profile.get('supportTerminals')and not profile.get('carrierSurface')))
            assert row['retainedBudget']==budget,'The groom source allocation changed.'
            added_indices=groom_added_indices(packet,native,fine,budget,row.get('curvedCoreSurface'))
            logical=row['addedVertices']*budget['vertexAndPhysicalCostPerVertexBytes']+added_indices*2
            assert row['addedVertices']<=budget['maximumAddedVertices']and logical<=budget['maximumAddedLogicalBytes'],'The groom exceeds its retained source vertex/frame/index bytes.'
            assert budget['keptVertices']+row['addedVertices']<=budget['maximumFinalVertices'],'The groom exceeds its retained composed source capacity.'
            assert packet['indexFormat']<=budget['sourceIndexFormat'],'The groom expanded its source index format.'
        else:
            assert row['addedVertices']<=profile['maximumAddedVerticesByLod'][row['lod']]
            assert packet['vertexCount']<=retained_groom_capacity(row,native),'The groom exceeds its retained source capacity.'
    else:assert row['addedVertices']<=profile['maximumAddedVerticesByLod'][row['lod']]
    assert np.all((uv[fine, :2] >= 0) & (uv[fine, :2] <= 1)) and np.all((abs(uv[fine, 3]) >= 1) & (abs(uv[fine, 3]) < 2))
    maximum_role=64 if profile.get('layeredAtlasCards',False) else 48
    assert np.all(uv[fine, 2] <= maximum_role) and np.all(uv[fine, 2] == np.floor(uv[fine, 2]))
    triangles = np.concatenate([np.asarray(s, dtype=int).reshape(-1, 3) for s in packet['subMeshes']])
    assert np.all(uv[triangles, 2] == uv[triangles[:, :1], 2]) and np.all(uv[triangles, 3] == uv[triangles[:, :1], 3])
    core_proof = validate_curved_core(row, packet, binding, native) if curved else None
    if not curved:
        for authored, native_indices in zip(packet['subMeshes'], native['subMeshes']):
            indices = np.asarray(authored).reshape(-1, 3)
            assert np.array_equal(indices[np.all(indices < count, axis=1)].ravel(), np.asarray(native_indices))
    owned = triangles[np.all(fine[triangles], axis=1)]
    minimum = None
    if not len(owned) and not curved:
        reason = row.get('zeroFineReason')
        assert row['addedVertices'] == 0 and not np.any(fine) and reason and reason['reason'] == 'NO_VALID_CURVE_AT_DECLARED_LOD_WITH_ALL_CURRENT_FACES_RETAINED'
        assert reason['removedBroadTriangles'] == 0 and reason['allCurrentFacesRetained']
        current = read(row['packetPath'])
        assert packet['vertexCount'] == current['vertexCount']
        for target, original in zip(packet['subMeshes'], current['subMeshes']):
            a, b = np.asarray(target).reshape(-1, 3), np.asarray(original).reshape(-1, 3)
            assert np.array_equal(a[~np.all(a < count, axis=1)], b[~np.all(b < count, axis=1)])
    elif len(owned):
        minimum = float('inf')
        for frame in [None] + [f for shape in packet['shapes'] for f in shape['frames']]:
            p = np.asarray(packet['vertices'], dtype='<f4').reshape(-1, 3).astype(float)
            n = np.asarray(packet['normals'], dtype='<f4').reshape(-1, 3).astype(float)
            if frame is not None:
                p += np.asarray(frame['vertices'], dtype='<f4').reshape(-1, 3)
                n += np.asarray(frame['normals'], dtype='<f4').reshape(-1, 3)
            signed = np.sum(np.cross(p[owned[:, 1]] - p[owned[:, 0]], p[owned[:, 2]] - p[owned[:, 0]]) * n[owned].mean(axis=1), axis=1)
            minimum = min(minimum, float(signed.min()))
            assert np.all(signed > 0)
    return dict(asset=row['asset'], lod=row['lod'], sourceFingerprint=row['manifestEntry']['sourceFingerprint'],
                status='PASS_NATIVE_PREFIX_FULL_FRAMES_FINE32_AND_F32_WINDING', nativeVertices=count, authoredVertices=packet['vertexCount'],
                physicalFrames=row['physicalFrames'], newFineVertices=int(fine.sum())-primary_count, newPrimaryVertices=primary_count,
                fineSurfaceVertices=int(((uv[:, 2] >= 17) & (uv[:, 2] < 33)).sum()), fineFringeVertices=int(((uv[:, 2] >= 33) & (uv[:, 2] < 49)).sum()),
                nativePntSkinSourceUvBindPosesAndPhysicalFramesBinary32Exact=True, nativeCoreOriginalIndexOrderExact=not bool(curved),
                curvedCoreProof=core_proof, minimumFineSignedAreaAfterFloat32=minimum, fineTriangleSampleCount=len(owned),
                declaredZeroFine=not len(owned), bindingsNativePrefixExact=True, sourceSurfaceProvenance=surface_provenance,
                authoringOriginalSourceUnchanged=True)


class _GroomProfileRequired(TypedDict):
    widthMetres: list[float]
    liftMetres: list[float]
    maximumRibbonsByLod: list[int]
    maximumAddedVerticesByLod: list[int]
    maximumAddedPhysicalAndVertexBytes: int
    targetPoissonCoverage: float
    assumedMeasuredMaskCoverage: float
    sourceProjectionLimitMetres: float
    neighborhoodRings: int
    minimumCurveLengthMetres: float
    minimumFacing: float
    traceStepMetres: float
    targetLengthExtentFraction: float
    targetLengthLimitsMetres: list[float]
    maximumChordErrorMetres: float
    maximumNormalChordDegrees: int
    maximumSourceUvChordErrorTexels: float
    nativeUvSeamTolerance: float
    transverseNormalDegrees: int
    geometricSeamToleranceMetres: float
    geometricSeamMinimumNormalDot: float


class GroomProfile(_GroomProfileRequired, total=False):
    nativeHairDomain: bool
    domainContract: dict[str, str]
    softCoveragePilot: bool
    exteriorCoverageRefinement: bool
    maximumAddedPhysicalAndVertexBytesByLod: list[int]
    coreSurfaceFineAllocationFraction: float
    layeredAtlasCards: bool
    retainedSourceBudget: bool
    fringeFraction: float
    fringeWidthMetres: list[float]
    fringeLengthMetres: list[float]
    fringeLiftMetres: list[float]
    candidateAttemptFactor: int
    supportTerminals: dict[str, float]
    carrierSurface: dict[str, Any]


PROFILE=GroomProfile(widthMetres=[.0014,.0034],liftMetres=[.0005,.0011],
    maximumRibbonsByLod=[1800,1100,480],maximumAddedVerticesByLod=[20000,15000,5000],
    maximumAddedPhysicalAndVertexBytes=8*1024*1024,targetPoissonCoverage=.93,
    assumedMeasuredMaskCoverage=.62,sourceProjectionLimitMetres=.0011,neighborhoodRings=4,
    minimumCurveLengthMetres=.004,minimumFacing=.45,traceStepMetres=.003,
    targetLengthExtentFraction=.38,targetLengthLimitsMetres=[.012,.14],
    maximumChordErrorMetres=.00024,maximumNormalChordDegrees=12,
    maximumSourceUvChordErrorTexels=.25,nativeUvSeamTolerance=1e-6,
    transverseNormalDegrees=48,geometricSeamToleranceMetres=.000002,
    geometricSeamMinimumNormalDot=.9)


def validate_terminal_settings(settings):
    physical={'contactMarginMetres','minimumRootDistanceMetres','terminalRegionLengthMetres','maximumRetreatMetres','maximumSupportOffsetMetres'}
    fractions={'maximumRetreatRootFraction','minimumRetainedAreaFraction','terminalSeedFraction'}
    if not isinstance(settings,dict)or set(settings)!=physical|fractions:
        raise ValueError('A terminal groom profile needs its complete physical and coverage settings.')
    for name,value in settings.items():
        if type(value)not in(int,float)or not np.isfinite(value)or not 0<value<=(.15 if name in physical else 1.):
            raise ValueError('Invalid terminal groom setting: '+name)
    if settings['maximumRetreatMetres']>=settings['terminalRegionLengthMetres']or settings['contactMarginMetres']>=settings['minimumRootDistanceMetres']:
        raise ValueError('Terminal retreat must preserve its contact-to-tip transition.')
    return settings


def validate_carrier_settings(settings):
    if isinstance(settings,dict)and'sourceSurface'in settings:
        if set(settings)-{'primaryLoft'}!={'sourceSurface','fineDonationFraction'}or type(settings['fineDonationFraction'])not in(int,float)or not np.isfinite(settings['fineDonationFraction'])or not 0<settings['fineDonationFraction']<1:
            raise ValueError('A source carrier requires its complete surface and fine allocation.')
        surface=settings['sourceSurface'];fields={'widthMetres','heightMetres','patternRadiusPixels'}
        if not isinstance(surface,dict)or set(surface)!=fields or any(type(surface[name])not in(int,float)or not np.isfinite(surface[name])for name in fields):
            raise ValueError('A source carrier surface requires finite physical dimensions and a pattern footprint.')
        if not 0<surface['heightMetres']<=surface['widthMetres']*.5 or type(surface['patternRadiusPixels'])is not int or not 0<=surface['patternRadiusPixels']<=8:
            raise ValueError('A source carrier has an invalid physical surface profile.')
        if 'primaryLoft'in settings:
            primary=settings['primaryLoft'];fields={'maximumSourceWidthFraction','minimumPrimaryWidthMetres','taperMetres','heightFraction'}
            if not isinstance(primary,dict)or set(primary)!=fields or any(type(value)not in(int,float)or not np.isfinite(value)or value<=0 for value in primary.values())or primary['maximumSourceWidthFraction']>1 or primary['heightFraction']>.25 or primary['minimumPrimaryWidthMetres']>=surface['widthMetres']or primary['taperMetres']>.15:
                raise ValueError('A primary loft requires its declared physical section and taper.')
        return settings
    fields={'widthMultiplier','heightFraction','tipWidthFraction','fineDonationFraction'}
    if not isinstance(settings,dict)or set(settings)!=fields or any(type(value)not in(int,float)or not np.isfinite(value)for value in settings.values()):
        raise ValueError('A carrier surface requires its declared physical design.')
    if not 0<settings['widthMultiplier']<=4 or not 0<settings['heightFraction']<=.5 or not 0<settings['tipWidthFraction']<1 or not 0<settings['fineDonationFraction']<1:
        raise ValueError('A carrier surface has an invalid cross-section or retained allocation.')
    return settings


def material_sampler(material,name):
    return texture_sampler(material['maps'].get(name))

class SourceHairDomain:
    """Keep conservative fine seeds separate from geometric material ownership.

    The shader classifies R <= .5 as hair.  Exported PNG channels have an
    additional half-UNorm8 rounding interval; uncertainty is retained rather
    than silently calling a rounded boundary value hair.  UV rectangles cover
    every bilinear tap of an emitted source-UV segment/triangle.  Native coarse
    faces containing metal/uncertainty therefore remain intact.
    """
    def __init__(self,material):
        assert material['shader']=='Custom/FakeAnisoHair'
        assert '_MainTex'in material['maps'],'An explicit observed source Main texture is required.'
        self.surface_samplers=material.get('sourceTextureSamplers',{})
        self.surface_defaults=material.get('sourceShaderTextureDefaults',{})
        self.fields={}
        for name,channel,default in (('_MainTex',3,1.),('_Mask',0,0.)):
            row=material['maps'].get(name)
            if row is None:
                self.fields[name]=dict(default=default,error=0.)
                continue
            assert row.get('encoding','').startswith('Existing immutable ExportTexture linear-GPU ARGB32 PNG, mip0 only;')
            assert sha(row['path'])==row['sha256']
            pixels=np.asarray(Image.open(row['path']).convert('RGBA'))[::-1,:,channel].astype(float)/255
            assert pixels.shape==(row['height'],row['width'])
            self.fields[name]=dict(pixels=pixels,scale=np.asarray(row['scale'],float),offset=np.asarray(row['offset'],float),
                                   minimum=float(pixels.min()),maximum=float(pixels.max()),error=.5/255)

    def range(self,name,coordinates):
        field=self.fields[name]
        if 'default'in field:return field['default'],field['default']
        coordinates=np.asarray(coordinates,float).reshape(-1,2)*field['scale']+field['offset']
        assert np.isfinite(coordinates).all()
        pixels=field['pixels'];height,width=pixels.shape
        low=np.floor(coordinates.min(axis=0)*[width,height]-.5).astype(int)
        high=np.floor(coordinates.max(axis=0)*[width,height]-.5).astype(int)+1
        # The older exporter did not retain Wrap mode.  If taps cross an atlas
        # edge, using whole-image bounds is safe for both Repeat and Clamp;
        # a default Wrap assumption must not create new fine on an ornament.
        if (low<0).any()or(high>=[width,height]).any():
            minimum,maximum=field['minimum'],field['maximum']
        else:
            region=pixels[low[1]:high[1]+1,low[0]:high[0]+1]
            minimum,maximum=float(region.min()),float(region.max())
        return max(0.,minimum-field['error']),min(1.,maximum+field['error'])

    def hair(self,coordinates):
        alpha=self.range('_MainTex',coordinates)[0]
        mask=self.range('_Mask',coordinates)[1]
        # .51 retains the existing conservative native-alpha seed margin.
        # Its semantic cutoff in the actual shader remains source _Cutoff.
        return alpha>=.51 and mask<=.5

    def surface_hair(self,coordinates):
        """Classify the actual source polygon's material; alpha stays cutout."""
        field=self.fields['_Mask']
        if 'default'in field:
            if self.surface_defaults.get('_Mask')!='black':
                raise ValueError('A geometry material default needs its actual shader authority.')
            return True
        sampler=self.surface_samplers.get('_Mask')
        if sampler is None:raise ValueError('A geometry material texture needs its actual native sampler.')
        pixels=field['pixels'];height,width=pixels.shape
        polygon=(np.asarray(coordinates,float).reshape(-1,2)*field['scale']+field['offset'])*[width,height]
        if not len(polygon)or not np.isfinite(polygon).all():raise ValueError('A surface material needs finite source UV coordinates.')
        radius={0:.5,1:1.,2:1.}.get(sampler['m_FilterMode'])
        if radius is None:raise ValueError('A surface material has an unsupported native filter mode.')
        low=np.ceil(polygon.min(axis=0)-radius-.5).astype(int);high=np.floor(polygon.max(axis=0)+radius-.5).astype(int)
        if np.any(high-low+1>np.asarray([width,height])*2+2):
            return min(1.,field['maximum']+field['error'])<=.5
        x,y=np.meshgrid(np.arange(low[0],high[0]+1),np.arange(low[1],high[1]+1));centers=np.stack((x+.5,y+.5),axis=2)
        edges=np.roll(polygon,-1,axis=0)-polygon
        axes=np.concatenate((np.eye(2),np.stack((-edges[:,1],edges[:,0]),axis=1)))
        keep=np.ones(x.shape,bool)
        for axis in axes:
            projected=centers@axis;bounds=polygon@axis;extent=abs(axis).sum()*radius
            keep&=(projected+extent>=bounds.min()-1e-10)&(projected-extent<=bounds.max()+1e-10)
        values=pixels[source_texture_index(y[keep],height,sampler['m_WrapV']),source_texture_index(x[keep],width,sampler['m_WrapU'])]
        if not len(values):raise ValueError('A surface material footprint has no native texels.')
        return min(1.,float(values.max())+field['error'])<=.5

class SourceShellBvh:
    """Read-only native triangle occlusion; no chart/skin/component welding."""
    def __init__(self,triangles):
        self.triangles=np.asarray(triangles,dtype=np.float64)
        assert self.triangles.ndim==3 and self.triangles.shape[1:]==(3,3) and np.isfinite(self.triangles).all()
        minimum=self.triangles.min(axis=1);maximum=self.triangles.max(axis=1)
        center=(minimum+maximum)*.5;self.nodes=[]
        def build(ids):
            index=len(self.nodes);lo=minimum[ids].min(axis=0);hi=maximum[ids].max(axis=0)
            self.nodes.append(None)
            if len(ids)<=12:self.nodes[index]=(lo,hi,ids,None,None)
            else:
                axis=int(np.argmax(np.ptp(center[ids],axis=0)));order=ids[np.argsort(center[ids,axis],kind='stable')];split=len(order)//2
                left,right=build(order[:split]),build(order[split:]);self.nodes[index]=(lo,hi,None,left,right)
            return index
        self.root=build(np.arange(len(self.triangles)))

    def blocked(self,points,directions,minimum_distance=.000002):
        points=np.asarray(points,dtype=np.float64);directions=np.asarray(directions,dtype=np.float64)
        assert points.shape==directions.shape and points.ndim==2 and points.shape[1]==3
        assert np.isfinite(points).all() and np.isfinite(directions).all() and np.all(np.linalg.norm(directions,axis=1)>.999)
        hit=np.zeros(len(points),bool)
        stack=[(self.root,np.arange(len(points)))]
        while stack:
            node,ids=stack.pop();ids=ids[~hit[ids]]
            if not len(ids):continue
            lo,hi,faces,left,right=self.nodes[node];o=points[ids];d=directions[ids]
            parallel=np.abs(d)<1e-14
            inverse=np.divide(1,d,out=np.zeros_like(d),where=~parallel)
            a=(lo-o)*inverse;b=(hi-o)*inverse
            lower=np.where(parallel,-np.inf,np.minimum(a,b));upper=np.where(parallel,np.inf,np.maximum(a,b))
            reaches=~np.any(parallel&((o<lo-1e-12)|(o>hi+1e-12)),axis=1)
            reaches&=upper.min(axis=1)>=np.maximum(lower.max(axis=1),minimum_distance)
            ids=ids[reaches]
            if not len(ids):continue
            if faces is None:
                stack.append((right,ids));stack.append((left,ids));continue
            tri=self.triangles[faces];e1=tri[:,1]-tri[:,0];e2=tri[:,2]-tri[:,0]
            p=np.cross(directions[ids,None,:],e2[None,:,:]);det=np.einsum('tj,ntj->nt',e1,p)
            valid=np.abs(det)>1e-14;inv=np.divide(1,det,out=np.zeros_like(det),where=valid)
            t=points[ids,None,:]-tri[None,:,0,:];u=np.einsum('ntj,ntj->nt',t,p)*inv
            q=np.cross(t,e1[None,:,:]);v=np.einsum('nj,ntj->nt',directions[ids],q)*inv
            distance=np.einsum('tj,ntj->nt',e2,q)*inv
            valid&=(u>=-1e-10)&(v>=-1e-10)&(u+v<=1+1e-10)&(distance>minimum_distance)
            hit[ids[np.any(valid,axis=1)]]=True
        return hit

def source_shell_stratum(positions,eligible) -> 'tuple[SourceShellBvh, np.ndarray, np.ndarray, np.ndarray, np.ndarray, dict[str, Any]]':
    """Prioritize native surfaces reachable from sampled exterior directions.

    This is a source-only seed heuristic, not a rendered visibility claim. The
    original triangles, indexed components and all deformation data stay exact.
    """
    bvh=SourceShellBvh(positions)
    directions=unit(np.asarray([(x,y,z)for x in(-1,0,1)for y in(-1,0,1)for z in(-1,0,1)
        if (x,y,z)!=(0,0,0)and sum(v!=0 for v in(x,y,z))in(1,3)],dtype=float))
    centers=positions.mean(axis=1);center=(positions.min(axis=(0,1))+positions.max(axis=(0,1)))*.5
    radial=unit(centers-center);outward=radial@directions.T>.2
    rows,columns=np.nonzero(outward&eligible[:,None]);rays=directions[columns]
    blocked=bvh.blocked(centers[rows],rays)
    clear=np.zeros_like(outward);clear[rows[~blocked],columns[~blocked]]=True
    score=clear.sum(axis=1);fraction=score/np.maximum(outward.sum(axis=1),1)
    reliable=eligible&(score>=2)
    assert reliable.any(),'No native exterior source stratum was resolved.'
    return bvh,directions,clear,reliable,fraction,dict(
        method='native triangle BVH, exact source positions; 6 axis + 8 diagonal outward rays per source centroid; at least 2 unobstructed sampled directions',
        nativeTriangles=len(positions),eligibleTriangles=int(eligible.sum()),selectedExteriorTriangles=int(reliable.sum()),
        sampledExteriorRays=int(len(rows)),blockedExteriorRays=int(blocked.sum()),unobstructedExteriorRays=int((~blocked).sum()),
        newGeometryFromVisibility=False,sourceTrianglesAndIndexedComponentsUnchanged=True)


def physical_core_coordinates(positions, coordinates, slot_triangles, selected_faces):
    """Center each source chart and recover its metric from real source edges.

    UV-derived flow keeps its strand axis, but its units and large absolute
    offsets do not dictate geometry density. A positive intrinsic metric also
    handles curved charts without flattening their three-dimensional surface.
    """
    positions, original = np.asarray(positions, float), np.asarray(coordinates, float)
    result = original.copy()
    reports = []
    for slot, indices in enumerate(slot_triangles):
        triangles = np.asarray(indices, dtype=int).reshape(-1, 3)
        selected = np.asarray(selected_faces[slot], dtype=bool)
        adjacency = collections.defaultdict(set)
        for triangle in triangles[selected]:
            for a, b in zip(triangle, np.roll(triangle, -1)):
                adjacency[int(a)].add(int(b)); adjacency[int(b)].add(int(a))
        seen = set()
        for first in sorted(adjacency):
            if first in seen:
                continue
            pending, vertices = [first], []
            seen.add(first)
            while pending:
                vertex = pending.pop(); vertices.append(vertex)
                for other in adjacency[vertex]:
                    if other not in seen:
                        seen.add(other); pending.append(other)
            owned = triangles[selected & np.all(np.isin(triangles, vertices), axis=1)]
            p, q = positions[owned], original[owned]
            physical = np.stack((p[:, 1] - p[:, 0], p[:, 2] - p[:, 0]), axis=2)
            parameter = np.stack((q[:, 1] - q[:, 0], q[:, 2] - q[:, 0]), axis=2)
            jacobian = physical @ np.linalg.inv(parameter)
            area = np.linalg.norm(np.cross(physical[:, :, 0], physical[:, :, 1]), axis=1) * .5
            metric = np.einsum('n,nki,nkj->ij', area, jacobian, jacobian) / area.sum()
            root = np.linalg.cholesky(metric)
            strand = root[1] / np.linalg.norm(root[1])
            axes = np.column_stack(([strand[1], -strand[0]], strand))
            transform = root @ axes
            centered = original[vertices] - original[vertices].mean(axis=0)
            result[vertices] = centered @ transform
            assert np.linalg.det(transform) > 0. and np.isfinite(result[vertices]).all()
            reports.append(dict(slot=slot, vertices=len(vertices), faces=len(owned),
                                oldMaximumAbsoluteMetres=float(np.abs(original[vertices]).max()),
                                newMaximumAbsoluteMetres=float(np.abs(result[vertices]).max()),
                                metric=metric.tolist(), strandAxisPreserved=True))
    return result, reports


def source_carrier_fields(guides, source_p, slot_triangles, terminal, width, settings, maximum_chord_error,*,physical_groups=None,eligible_faces=None):
    """Recover local fields from the original complete, unreduced surface walks."""
    poses=np.asarray(source_p,float);fields=collections.defaultdict(list);unresolved=[];mapped=0
    if poses.ndim!=3 or poses.shape[2]!=3 or width<=0:raise ValueError('Carrier guides lost their physical source frames.')
    triangles=[np.asarray(flat,int).reshape(-1,3)for flat in slot_triangles]
    lookup=[]
    for rows in triangles:
        keys=collections.defaultdict(list)
        for face,tri in enumerate(rows):keys[tuple(sorted(map(int,tri)))].append(face)
        lookup.append(keys)
    for guide in guides:
        slot=guide['slot'];samples=guide['samples'];identifier=guide['guide']
        if type(slot)is not int or not 0<=slot<len(triangles)or len(samples)<2 or guide['lengthMetres']<=0:
            raise ValueError('A carrier guide lost its actual native material owner.')
        ordered=np.asarray([sample['v']for sample in samples],float)
        if not np.isfinite(ordered).all()or np.any(ordered<0)or np.any(ordered>1)or np.any(np.diff(ordered)<0):
            raise ValueError('A carrier guide restarted its complete-curve coordinate.')
        runs=[]
        for sample in samples:
            tri=np.asarray(sample['triangle'],int);bary=np.asarray(sample['barycentric'],float)
            key=tuple(sorted(map(int,tri)))
            if tri.shape!=(3,)or bary.shape!=(3,)or np.any(bary<-1e-8)or abs(bary.sum()-1)>1e-8 or len(lookup[slot].get(key,()))!=1:
                raise ValueError('A carrier guide has no unique native source triangle.')
            face=lookup[slot][key][0];parent=triangles[slot][face]
            weights=np.asarray([bary[list(map(int,tri)).index(int(i))]for i in parent])
            node=dict(face=face,bary=weights,v=float(sample['v']))
            if runs and runs[-1][0]['face']==face:runs[-1].append(node)
            else:
                if runs:
                    previous=runs[-1][-1];old=triangles[slot][previous['face']]
                    a=np.einsum('i,pij->pj',previous['bary'],poses[:,old]);b=np.einsum('i,pij->pj',weights,poses[:,parent])
                    if np.max(np.linalg.norm(a-b,axis=1))>PROFILE['geometricSeamToleranceMetres']or abs(previous['v']-node['v'])>1e-8:
                        raise ValueError('A complete carrier walk omitted its exact physical edge crossing.')
                runs.append([node])
        visited=set()
        for nodes in runs:
            if len(nodes)<2:continue
            face=nodes[0]['face'];parent=triangles[slot][face]
            if face in visited:
                unresolved.append(dict(slot=slot,face=face,guide=identifier,reason='nonconsecutive-source-face-revisit'));continue
            visited.add(face);p=poses[0,parent]
            first=nodes[0]['bary']@p;last=nodes[-1]['bary']@p;delta=last-first;length=float(np.linalg.norm(delta))
            if length<=1e-10 or nodes[-1]['v']<=nodes[0]['v']:
                unresolved.append(dict(slot=slot,face=face,guide=identifier,reason='zero-source-span'));continue
            along=delta/length;normal=unit(np.cross(p[1]-p[0],p[2]-p[0]));across=unit(np.cross(along,normal))
            if np.linalg.norm(across)<.5:
                unresolved.append(dict(slot=slot,face=face,guide=identifier,reason='unresolved-physical-axis'));continue
            curve=np.asarray([node['bary']@p for node in nodes]);projection=first+np.clip((curve-first)@along,0,length)[:,None]*along
            error=float(np.max(np.linalg.norm(curve-projection,axis=1)))
            if error>maximum_chord_error:
                unresolved.append(dict(slot=slot,face=face,guide=identifier,reason='source-span-curvature-needs-more-than-one-local-field',maximumChordErrorMetres=error));continue
            terminal_values=np.asarray(terminal['terminalDistance'])[parent]
            if not np.isfinite(terminal_values).all():
                unresolved.append(dict(slot=slot,face=face,guide=identifier,reason='unresolved-physical-terminal'));continue
            v=nodes[0]['v']+(p-first)@along*((nodes[-1]['v']-nodes[0]['v'])/length)
            fields[slot,face].append(dict(guide=identifier,uMetres=((p-first)@across).tolist(),v=v.tolist(),
                halfWidthMetres=width*.5,heightMetres=width*settings['heightFraction'],lengthMetres=guide['lengthMetres'],
                terminalDistanceMetres=terminal_values.tolist(),terminalRegionMetres=settings['terminalRegionMetres'],
                tipWidthFraction=settings['tipWidthFraction']))
            mapped+=1
    physical=np.arange(poses.shape[1])if physical_groups is None else np.asarray(physical_groups,int)
    eligible=[np.ones(len(rows),bool)for rows in triangles]if eligible_faces is None else eligible_faces
    if physical.shape!=(poses.shape[1],)or len(eligible)!=len(triangles):raise ValueError('Carrier field transport lost physical ownership.')
    neighbours=collections.defaultdict(list)
    for slot,rows in enumerate(triangles):
        edges=collections.defaultdict(list)
        for face,tri in enumerate(rows):
            for a,b in((0,1),(1,2),(2,0)):
                if physical[tri[a]]==physical[tri[b]]:continue
                edges[tuple(sorted((int(physical[tri[a]]),int(physical[tri[b]]))))].append((face,a,b))
        for members in edges.values():
            if len(members)!=2 or members[0][0]==members[1][0]:continue
            for face,a,b in members:
                other,oa,ob=next(row for row in members if row[0]!=face)
                if physical[rows[face,a]]!=physical[rows[other,oa]]:oa,ob=ob,oa
                neighbours[slot,face].append((other,a,b,oa,ob))
    def band_polygon(u,v,half,slack=0.,length=1.):
        poly=[row for row in np.eye(3)]
        for values,limit,greater in((u,-half-slack,True),(u,half+slack,False),(v,-slack/length,True),(v,1+slack/length,False)):
            result=[]
            for a,b in zip(poly,poly[1:]+poly[:1]):
                first=float(a@values)-limit;last=float(b@values)-limit
                ia=first>=0 if greater else first<=0;ib=last>=0 if greater else last<=0
                if ia:result.append(a)
                if ia!=ib:result.append(a+(b-a)*(first/(first-last)))
            poly=result
            if len(poly)<3:return[]
        return poly
    indexed={(slot,face,field['guide']):field for(slot,face),rows in fields.items()for field in rows}
    queue=collections.deque(indexed);propagated=0
    while queue:
        slot,face,guide=queue.popleft();field=indexed[slot,face,guide];u=np.asarray(field['uMetres']);v=np.asarray(field['v'])
        tri=triangles[slot][face];p=poses[0,tri];normal=unit(np.cross(p[1]-p[0],p[2]-p[0]))
        for other,a,b,oa,ob in neighbours[slot,face]:
            key=slot,other,guide
            if key in indexed or not eligible[slot][other]:continue
            target=triangles[slot][other];terminal_values=np.asarray(terminal['terminalDistance'])[target]
            if not np.isfinite(terminal_values).all():continue
            edge=p[b]-p[a];edge_length=float(np.linalg.norm(edge))
            if edge_length<=1e-12:continue
            along=edge/edge_length;across=unit(np.cross(normal,along));side=float(np.dot(p[3-a-b]-p[a],across))
            if abs(side)<=1e-12:continue
            relative=poses[0,target[3-oa-ob]]-poses[0,target[oa]]
            x=float(relative@along);y=-np.sign(side)*float(np.linalg.norm(relative-along*x))
            if abs(y)<=1e-12:continue
            unfolded=p[a]+along*x+across*y
            e=np.stack((p[1]-p[0],p[2]-p[0]),axis=1);gram=e.T@e
            if np.linalg.det(gram)<=1e-24:continue
            weights=np.linalg.solve(gram,e.T@(unfolded-p[0]));bary=np.r_[1-weights.sum(),weights]
            target_u=np.empty(3);target_v=np.empty(3)
            target_u[[oa,ob]]=u[[a,b]];target_v[[oa,ob]]=v[[a,b]]
            target_u[3-oa-ob]=bary@u;target_v[3-oa-ob]=bary@v
            if not band_polygon(target_u,target_v,field['halfWidthMetres'],maximum_chord_error,field['lengthMetres']):continue
            copy_field=dict(field,uMetres=target_u.tolist(),v=target_v.tolist(),terminalDistanceMetres=terminal_values.tolist())
            indexed[key]=copy_field;fields[slot,other].append(copy_field);queue.append(key);propagated+=1
    # A scalar field on shared physical vertices gives both charts identical
    # edge cuts. Keeping the same global guide order also keeps the partition
    # conforming when overlapping locks claim their source domain.
    nodal=collections.defaultdict(list)
    for(slot,face,guide),field in indexed.items():
        tri=triangles[slot][face];p=poses[0,tri];weight=float(np.linalg.norm(np.cross(p[1]-p[0],p[2]-p[0])))
        for index,vertex in enumerate(tri):nodal[slot,guide,int(physical[vertex])].append((weight,np.array([field['uMetres'][index],field['v'][index]])))
    means={key:sum(weight*value for weight,value in rows)/sum(weight for weight,_ in rows)for key,rows in nodal.items()}
    rejected=set();maximum_phase_error=0.
    for(slot,face,guide),field in indexed.items():
        old_u=np.asarray(field['uMetres']);old_v=np.asarray(field['v']);values=np.asarray([means[slot,guide,int(physical[i])]for i in triangles[slot][face]])
        new_u,new_v=values[:,0],values[:,1]
        polygon=band_polygon(old_u,old_v,field['halfWidthMetres'])+band_polygon(new_u,new_v,field['halfWidthMetres'])
        if polygon:
            difference=np.column_stack((new_u-old_u,(new_v-old_v)*field['lengthMetres']))
            error=float(np.max(np.linalg.norm(np.asarray(polygon)@difference,axis=1)));maximum_phase_error=max(maximum_phase_error,error)
            if error>maximum_chord_error:rejected.add((slot,guide))
        field['uMetres']=new_u.tolist();field['v']=new_v.tolist()
    for slot,guide in sorted(rejected):unresolved.append(dict(slot=slot,guide=guide,reason='complete-guide-physical-phase-error',limitMetres=maximum_chord_error))
    result={key:sorted((field for field in rows if(key[0],field['guide'])not in rejected),key=lambda field:field['guide'])for key,rows in fields.items()}
    result={key:rows for key,rows in result.items()if rows}
    return result,dict(mappedNativeGuideSpans=mapped,propagatedNativeGuideSpans=propagated,unresolvedGuideSpans=unresolved,
        maximumPhysicalPhaseAdjustmentMetres=maximum_phase_error,rejectedCompleteGuides=len(rejected),
        fullCurveCoordinatesRetained=True,physicalFrameEdgeCrossingsChecked=True,sourceUvChartsDoNotCreateTips=True)


def _plan_flow_carrier_cells(coordinates, slot_triangles, selected_faces, width, vertex_budget,
                             reusable_vertices, positions, physical_groups, flow_fields, protected_vertices):
    """Partition the owned surface by complete physical guide spans, not UV charts."""
    coordinates=np.asarray(coordinates,float);points=np.asarray(positions,float)
    physical=np.asarray(physical_groups,int);protected=set(map(int,protected_vertices))
    reusable=set(map(int,reusable_vertices));records=[];lookup={};output=[];parents=[];groups_by_slot=[]
    components=[];allocated=0;maximum_error=0.;claimed_area=0.;unmodulated_area=0.
    if points.shape!=(len(coordinates),3)or physical.shape!=(len(points),)or not np.isfinite(points).all():
        raise ValueError('Flow carrier cells require their physical source positions and aliases.')
    if width<=0 or vertex_budget<0 or not protected.issubset(set(range(len(points)))):
        raise ValueError('Flow carrier cells lost their physical allocation or contact points.')
    guide_coordinates={}
    for(slot,face),fields in flow_fields.items():
        parent=np.asarray(slot_triangles[slot],int).reshape(-1,3)[face]
        for field in fields:
            values=np.column_stack((field['uMetres'],field['v']));mapping=guide_coordinates.setdefault((slot,field['guide']),{})
            for vertex,value in zip(parent,values):
                group=int(physical[vertex])
                if group in mapping and not np.allclose(mapping[group],value,rtol=0,atol=1e-10):
                    raise ValueError('A carrier guide has inconsistent physical-edge coordinates.')
                mapping[group]=value
    def area(poly):
        return sum(float(np.linalg.det(np.stack((poly[i][1:]-poly[0][1:],poly[i+1][1:]-poly[0][1:]))))for i in range(1,len(poly)-1))*.5
    def clip(poly,field,limit,greater):
        result=[]
        for first,second in zip(poly,poly[1:]+poly[:1]):
            a=float(first@field)-limit;b=float(second@field)-limit
            error_a=8*np.finfo(float).eps*(float(np.sum(abs(first*field)))+abs(limit))
            error_b=8*np.finfo(float).eps*(float(np.sum(abs(second*field)))+abs(limit))
            if abs(a)<=error_a:a=0.
            if abs(b)<=error_b:b=0.
            ia=a>=0 if greater else a<=0;ib=b>=0 if greater else b<=0
            if ia:result.append(first)
            if ia!=ib:result.append(first+(second-first)*(a/(a-b)))
        distinct=[]
        for value in result:
            if not distinct or np.max(abs(value-distinct[-1]))>1e-12:distinct.append(value)
        if len(distinct)>1 and np.max(abs(distinct[0]-distinct[-1]))<=1e-12:distinct.pop()
        return distinct if len(distinct)>=3 and area(distinct)>1e-14 else[]
    def triangulate(polygon):
        remaining=list(range(len(polygon)));triangles=[]
        while len(remaining)>3:
            for i,current in enumerate(remaining):
                previous=remaining[i-1];following=remaining[(i+1)%len(remaining)]
                a,b,c=(polygon[index][1:]for index in(previous,current,following))
                signed=float(np.linalg.det(np.stack((b-a,c-a))))
                if signed<=1e-14:continue
                chord=c-a;length=float(chord@chord)
                blocked=False
                for other in remaining:
                    if other in(previous,current,following):continue
                    relative=polygon[other][1:]-a
                    cross=float(np.linalg.det(np.stack((chord,relative))))
                    if abs(cross)<=1e-14 and 0<float(relative@chord)<length:
                        blocked=True;break
                if blocked:continue
                triangles.append((previous,current,following));remaining.pop(i);break
            else:raise ValueError('Carrier clipping produced an unrepresentable collinear cell.')
        if area([polygon[index]for index in remaining])<=1e-14:
            raise ValueError('Carrier clipping produced an unrepresentable final cell.')
        triangles.append(tuple(remaining));return triangles
    def intersect(poly,planes):
        outside=[];inside=poly
        for field,limit,greater in planes:
            other=clip(inside,field,limit,not greater)
            if other:outside.append(other)
            inside=clip(inside,field,limit,greater)
            if not inside:break
        return inside,outside
    all_edges=collections.Counter()
    for flat in slot_triangles:
        for triangle in np.asarray(flat,int).reshape(-1,3):
            for a,b in zip(triangle,np.roll(triangle,-1)):
                all_edges[tuple(sorted((int(physical[a]),int(physical[b]))))]+=1
    for slot,flat in enumerate(slot_triangles):
        source=np.asarray(flat,int).reshape(-1,3);selected=np.asarray(selected_faces[slot],bool)
        if selected.shape!=(len(source),):raise ValueError('A carrier ownership mask omitted source faces.')
        edge_faces=collections.defaultdict(list)
        for face in np.flatnonzero(selected):
            for a,b in zip(source[face],np.roll(source[face],-1)):
                edge_faces[tuple(sorted((int(physical[a]),int(physical[b]))))].append(int(face))
        neighbours=collections.defaultdict(set)
        for members in edge_faces.values():
            if len(members)==2:
                for face in members:neighbours[face].update(members)
        face_groups={}
        for first in map(int,np.flatnonzero(selected)):
            if first in face_groups:continue
            group=len(components);pending=[first];face_groups[first]=group;members=[]
            while pending:
                face=pending.pop();members.append(face)
                for other in neighbours[face]:
                    if other not in face_groups:face_groups[other]=group;pending.append(other)
            components.append(dict(slot=slot,faces=members))
        groups_by_slot.append([face_groups.get(face,-1)for face in range(len(source))])
        slot_triangles_out=[];slot_parents=[]
        for face in map(int,np.flatnonzero(selected)):
            triangle=source[face];q=points[triangle];cross=np.cross(q[1]-q[0],q[2]-q[0])
            double_area=float(np.linalg.norm(cross))
            if double_area<=0:raise ValueError('An owned carrier source face is degenerate.')
            boundary=[]
            for a,b in((0,1),(1,2),(2,0)):
                edge=tuple(sorted((int(physical[triangle[a]]),int(physical[triangle[b]]))))
                if len(edge_faces[edge])!=2 and all_edges[edge]!=1:
                    boundary.append((3-a-b,double_area/np.linalg.norm(q[b]-q[a])))
            fields=flow_fields.get((slot,face),())
            remaining=[[row for row in np.eye(3)]];cells=[];guide_ids=set()
            for field in fields:
                u=np.asarray(field['uMetres'],float);v=np.asarray(field['v'],float)
                half=field['halfWidthMetres'];height=field['heightMetres']
                if u.shape!=(3,)or v.shape!=(3,)or not np.isfinite(np.r_[u,v,half,height]).all()or half<=0 or height<0:
                    raise ValueError('A physical carrier guide lost its finite source field.')
                if field['guide']in guide_ids:raise ValueError('One carrier guide revisits its source face ambiguously.')
                guide_ids.add(field['guide'])
                terminal=np.asarray(field['terminalDistanceMetres'],float)
                if terminal.shape!=(3,)or not np.isfinite(terminal).all()or np.any(terminal<0)or not np.isfinite([field['tipWidthFraction'],field['terminalRegionMetres'],field['lengthMetres']]).all()or not 0<field['tipWidthFraction']<=1 or field['terminalRegionMetres']<=0 or field['lengthMetres']<=0:
                    raise ValueError('A carrier tip requires the resolved physical terminal field.')
                jac=np.array([[u[1]-u[0],u[2]-u[0]],[v[1]-v[0],v[2]-v[0]]])
                if abs(np.linalg.det(jac))<=1e-14 or np.linalg.cond(jac)>=1e8:
                    raise ValueError('A carrier guide does not parameterize its physical face.')
                after=[]
                for poly in remaining:
                    inside,outside=intersect(poly,((u,-half,True),(u,half,False),(v,0.,True),(v,1.,False)))
                    after.extend(outside)
                    if inside:
                        # A real middle ridge needs a sample on its centreline.
                        for greater in(False,True):
                            side=clip(inside,u,0.,greater)
                            if side:cells.append((side,field))
                remaining=after
            cells.extend((poly,None)for poly in remaining)
            total=0.
            for polygon,field in cells:
                signed_area=area(polygon);total+=signed_area
                if field is None:unmodulated_area+=signed_area*double_area
                else:claimed_area+=signed_area*double_area
                cell_vertices=[]
                for bary in polygon:
                    support=np.flatnonzero(bary>1e-10)
                    if len(support)==1:key=('vertex',int(triangle[support[0]]))
                    elif len(support)==2:
                        a,b=sorted(map(int,triangle[support]));fraction=float(bary[np.flatnonzero(triangle==b)[0]])
                        key=('edge',a,b,round(fraction,11))
                    else:key=('face',face,round(float(bary[1]),11),round(float(bary[2]),11))
                    # Middle surfaces share their boundary vertex. Separate
                    # lock ends are allowed only in the actual terminal field.
                    owner=(field['guide']if field is not None and float(bary@np.asarray(field['terminalDistanceMetres']))<field['terminalRegionMetres']else None)
                    key=(slot,owner,*key)
                    if key not in lookup:
                        index=len(records);lookup[key]=index
                        record=dict(slot=slot,face=face,triangle=triangle.tolist(),barycentric=bary.tolist(),
                            component=face_groups[face],curvatureGroup=face_groups[face],flowCarrier=copy.deepcopy(field),
                            protectedCorners=[int(i)in protected for i in triangle],boundaryCollars=boundary,
                            contactRadiusMetres=width*.25)
                        if len(support)==1 and int(triangle[support[0]])in reusable and(int(triangle[support[0]])in protected or field is None):
                            record['nativeVertex']=int(triangle[support[0]])
                        else:allocated+=1
                        records.append(record)
                        if allocated>vertex_budget:raise OverflowError('Flow carrier cells exceed their retained vertex/frame allocation.')
                    cell_vertices.append(lookup[key])
                for a,b,c in triangulate(polygon):
                    slot_triangles_out.append([cell_vertices[a],cell_vertices[b],cell_vertices[c]])
                    slot_parents.append(face)
            maximum_error=max(maximum_error,abs(total-.5))
            if abs(total-.5)>1e-9:raise ValueError('Flow carrier cells lost their source-domain coverage.')
        output.append(slot_triangles_out);parents.append(slot_parents)
    return dict(vertices=records,triangles=output,parents=parents,components=components,
        physicalGroups=physical.tolist(),curvatureGroupCount=len(components),curvatureGroupsBySlot=groups_by_slot,
        sourceTrianglesBySlot=[np.asarray(v,int).reshape(-1,3).tolist()for v in slot_triangles],
        widthMetres=width,maximumSourceAreaError=maximum_error,allocatedVertices=allocated,
        guidePhysicalCoordinates=guide_coordinates,
        reusedNativeVertices=len(records)-allocated,sourceFaces=sum(int(np.sum(v))for v in selected_faces),
        flowCarrierCells=dict(physicalGuideAreaSquareMetres=claimed_area,unmodulatedAreaSquareMetres=unmodulated_area,
            sourceDomainCoverageExact=True,sourceChartEndsAreNotPhysicalTips=True))


def _evaluate_flow_carrier_cells(plan,positions,normals,tangents):
    records=plan['vertices'];corners=np.asarray([row['triangle']for row in records],int)
    bary=np.asarray([row['barycentric']for row in records],float)
    p=np.asarray(positions,float)[corners];n=unit(np.asarray(normals,float)[corners]);t=np.asarray(tangents,float)[corners]
    before=unit(np.cross(p[:,1]-p[:,0],p[:,2]-p[:,0]))
    def surface(weights):
        base=np.einsum('ni,nij->nj',weights,p);result=base.copy()
        for index,row in enumerate(records):
            field=row['flowCarrier']
            if field is None:continue
            u=float(weights[index]@np.asarray(field['uMetres']));v=float(weights[index]@np.asarray(field['v']))
            half=field['halfWidthMetres'];fraction=np.clip((u+half)/(2*half),0,1)
            driver=row.get('geometrySurface')or row;driver_triangle=np.asarray(driver['triangle'],int)
            driver_p=np.asarray(positions,float)[driver_triangle]
            direction=unit(np.cross(driver_p[1]-driver_p[0],driver_p[2]-driver_p[0]))
            direction*=1 if np.dot(direction,before[index])>=0 else-1
            protection=0.
            for corner,protected in enumerate(row['protectedCorners']):
                if not protected:continue
                distance=float(np.linalg.norm(base[index]-p[index,corner]))
                collar=np.clip(distance/row['contactRadiusMetres'],0,1)
                protection=max(protection,float((1-collar)**2*(1+2*collar)))
            edge_fade=1.
            for opposite,altitude in row['boundaryCollars']:
                ramp=np.clip(weights[index,opposite]*altitude/(plan['widthMetres']*.5),0,1)
                edge_fade=min(edge_fade,float(ramp*ramp*(3-2*ramp)))
            # Only complete-guide endpoints feather. A chart cut has no role.
            feather=min(.5,half/field['lengthMetres'])
            along_fade=np.clip(min(v,1-v)/feather,0,1);along_fade=along_fade*along_fade*(3-2*along_fade)
            terminal=float(weights[index]@np.asarray(field['terminalDistanceMetres']))
            tip=np.clip(1-terminal/field['terminalRegionMetres'],0,1)
            scale=field['tipWidthFraction']+(1-field['tipWidthFraction'])*(1-tip)**2
            if scale!=1 and abs(u)>1e-14 and edge_fade>0 and protection<1:
                coordinates=plan['guidePhysicalCoordinates'][row['slot'],field['guide']]
                physical=np.asarray(plan['physicalGroups'],int)
                if any(int(physical[i])not in coordinates for i in driver_triangle):raise ValueError('A tapered carrier has no complete physical guide frame.')
                q=np.asarray([coordinates[int(physical[i])]for i in driver_triangle])
                jac=np.stack((q[1]-q[0],q[2]-q[0]),axis=1)
                if abs(np.linalg.det(jac))<=1e-14:raise ValueError('A tapered carrier has a singular physical guide frame.')
                derivative=np.stack((driver_p[1]-driver_p[0],driver_p[2]-driver_p[0]),axis=1)@np.linalg.inv(jac)
                along=unit(derivative[:,1]);across=unit(derivative[:,0]-along*np.dot(derivative[:,0],along))
                result[index]+=(scale-1)*u*across*(1-protection)*edge_fade
            height=4*fraction*(1-fraction)*field['heightMetres']*along_fade*scale*(1-protection)*edge_fade
            result[index]+=direction*height
        return result
    points=surface(bary);epsilon=1e-5;derivatives=[]
    for delta in(np.array([-epsilon,epsilon,0.]),np.array([-epsilon,0.,epsilon])):
        derivatives.append((surface(bary+delta)-surface(bary-delta))/(2*epsilon))
    after=unit(np.cross(derivatives[0],derivatives[1]));cosine=np.sum(before*after,axis=1)
    if not np.isfinite(points).all()or np.any(cosine<=0):raise ValueError('A physical carrier cell reversed its source differential.')
    plan['tuftDerivativeProof']=dict(mode='physical-source-flow-cell',minimumCosine=float(cosine.min()),
        completeGuideCoordinatesRetained=True,physicalTerminalFieldUsed=True,protectedSourcePointsExact=True)
    rotation=np.cross(before,after)
    def transport(values):return values+np.cross(rotation,values)+np.cross(rotation,np.cross(rotation,values))/(1+cosine)[:,None]
    target_n=unit(transport(np.einsum('ni,nij->nj',bary,n)))
    target_t=transport(np.einsum('ni,nij->nj',bary,t));target_t=unit(target_t-target_n*np.sum(target_t*target_n,axis=1)[:,None])
    return points,target_n,target_t


_CARRIER_SAMPLES=np.asarray([[1.,0.,0.],[0.,1.,0.],[0.,0.,1.],[.5,.5,0.],[0.,.5,.5],[.5,0.,.5]])

def _carrier_coefficients(bary):
    b=np.asarray(bary,float);a,c,d=np.moveaxis(b,-1,0)
    return np.stack((a*a,c*c,d*d,2*a*c,2*c*d,2*d*a),axis=-1)

def _carrier_derivative_coefficients(bary):
    b=np.asarray(bary,float);a,c,d=np.moveaxis(b,-1,0)
    return (np.stack((-2*a,2*c,np.zeros_like(a),2*(a-c),2*d,-2*d),axis=-1),
            np.stack((-2*a,np.zeros_like(a),2*d,-2*c,2*c,2*(a-d)),axis=-1))

def _carrier_interpolation_controls(samples):
    values=np.asarray(samples,float);result=values.copy()
    for output,a,b in((3,0,1),(4,1,2),(5,2,0)):result[...,output,:]=2*values[...,output,:]-.5*(values[...,a,:]+values[...,b,:])
    return result

def _carrier_shared_source_samples(samples,source_triangles,physical,held_edges,held_vertices):
    """Close real geometric controls; do not average coordinate phases."""
    result=np.asarray(samples,float).copy();owners=collections.defaultdict(list)
    for face,parent in enumerate(source_triangles):
        for corner,vertex in enumerate(parent):owners['vertex',int(physical[vertex])].append((face,corner))
        for index,a,b in((3,0,1),(4,1,2),(5,2,0)):
            owners['edge',*sorted(map(int,physical[parent[[a,b]]]))].append((face,index))
    changed=0
    for key,members in owners.items():
        held=key[0]=='vertex'and key[1]in held_vertices or key[0]=='edge'and tuple(key[1:])in held_edges
        value=np.zeros((len(result),3))if held else np.mean(np.stack([result[:,face,sample]for face,sample in members],axis=1),axis=1)
        for face,sample in members:result[:,face,sample]=value
        if len(members)>1:changed+=1
    return result,dict(sharedPhysicalControlSamples=changed,phaseCoordinatesAveraged=False)

def _carrier_evaluate(positions,triangles,controls,face_ids,bary):
    q=np.asarray(positions)[:,np.asarray(triangles)[np.asarray(face_ids)]]
    c=np.asarray(controls)[:,np.asarray(face_ids)]
    points=np.einsum('ni,pnij->pnj',bary,q)+np.einsum('ni,pnij->pnj',_carrier_coefficients(bary),c)
    d1,d2=_carrier_derivative_coefficients(bary)
    first=q[:,:,1]-q[:,:,0]+np.einsum('ni,pnij->pnj',d1,c)
    second=q[:,:,2]-q[:,:,0]+np.einsum('ni,pnij->pnj',d2,c)
    normal=np.cross(first,second);length=np.linalg.norm(normal,axis=2)
    return points,normal/np.maximum(length[:,:,None],1e-20)

def _carrier_normal_coefficients(parent_positions,controls):
    first,second=_carrier_derivative_coefficients(_CARRIER_SAMPLES)
    a=parent_positions[:,:,1]-parent_positions[:,:,0];b=parent_positions[:,:,2]-parent_positions[:,:,0]
    da=a[:,:,None]+np.einsum('qi,ptij->ptqj',first,controls)
    db=b[:,:,None]+np.einsum('qi,ptij->ptqj',second,controls)
    return _carrier_interpolation_controls(np.cross(da,db))

def _carrier_fit_controls(positions,triangles,samples,physical,*,maximum_steps=32):
    """Keep the entire quadratic differential in its source orientation cone."""
    parent=positions[:,triangles];before=np.cross(parent[:,:,1]-parent[:,:,0],parent[:,:,2]-parent[:,:,0])
    base=np.sum(before*before,axis=2);valid=base>0
    lookup={};ids=[]
    for triangle in triangles:
        row=[]
        for bary in _CARRIER_SAMPLES:
            weights=collections.defaultdict(float)
            for vertex,weight in zip(triangle,bary):
                if weight:weights[int(physical[vertex])]+=float(weight)
            key=tuple(sorted(weights.items()));row.append(lookup.setdefault(key,len(lookup)))
        ids.append(row)
    ids=np.asarray(ids,int);scale=np.ones(len(lookup));changed=set();affected=set()
    for iteration in range(maximum_steps):
        current=samples*scale[ids][None,:,:,None];controls=_carrier_interpolation_controls(current)
        signed=np.einsum('ptqi,pti->ptq',_carrier_normal_coefficients(parent,controls),before)
        bad=(signed<=0)&valid[:,:,None]
        if not bad.any():return controls,dict(iterations=iteration,adjustedPhysicalSamples=len(changed),affectedSourceFaces=len(affected),
            minimumOffsetScale=float(scale.min()),continuousOrientationConePositive=True)
        half=np.einsum('ptqi,pti->ptq',_carrier_normal_coefficients(parent,controls*.5),before)
        zero=np.broadcast_to(base[:,:,None],signed.shape);linear=4*half-signed-3*zero;quadratic=2*(signed+zero-2*half)
        update=np.ones(len(scale))
        for frame,face,index in np.argwhere(bad):
            c,b,a=float(zero[frame,face,index]),float(linear[frame,face,index]),float(quadratic[frame,face,index])
            if abs(a)<=np.finfo(float).eps*max(abs(b),abs(c)):roots=[-c/b]if b else[]
            else:
                determinant=b*b-4*a*c
                if determinant<0:raise ValueError('A quadratic carrier control lost its original orientation interval.')
                root=np.sqrt(determinant);q=-.5*(b+np.copysign(root,b));roots=[q/a]+([c/q]if q else[])
            roots=[value for value in roots if np.isfinite(value)and value>0]
            if not roots:raise ValueError('A quadratic carrier control has no positive displacement bound.')
            value=min(roots)*.5
            if not 0<value<1:raise ValueError('A quadratic carrier control did not reach its observed orientation limit.')
            update[ids[face]]=np.minimum(update[ids[face]],value);affected.add(int(face))
        for group in np.flatnonzero(update<1):scale[group]*=update[group];changed.add(int(group))
    raise ValueError('Quadratic carrier orientation constraints did not settle.')

def _carrier_source_normal_controls(positions,source_normals,source_tangents,triangles,controls,physical,held_edges,held_vertices,*,source_hand=None,slot=0,identity_normals=None,identity_tangents=None,retain_source_strength=False):
    """A PN normal field shares real controls, retaining original hard frames."""
    faces=np.repeat(np.arange(len(triangles)),len(_CARRIER_SAMPLES));bary=np.tile(_CARRIER_SAMPLES,(len(triangles),1))
    _,after=_carrier_evaluate(positions,triangles,controls,faces,bary);after=after.reshape(len(positions),len(triangles),len(_CARRIER_SAMPLES),3)
    q=positions[:,triangles];before=unit(np.cross(q[:,:,1]-q[:,:,0],q[:,:,2]-q[:,:,0]))
    original_vector=np.einsum('qi,ptij->ptqj',_CARRIER_SAMPLES,unit(source_normals[:,triangles]))
    original=unit(original_vector)
    tangent=np.einsum('qi,ptij->ptqj',_CARRIER_SAMPLES,source_tangents[:,triangles])
    identity_n=original if identity_normals is None else unit(np.einsum('qi,ptij->ptqj',_CARRIER_SAMPLES,identity_normals[:,triangles]))
    identity_t=tangent if identity_tangents is None else np.einsum('qi,ptij->ptqj',_CARRIER_SAMPLES,identity_tangents[:,triangles])
    axis=np.cross(before[:,:,None],after);cosine=np.sum(before[:,:,None]*after,axis=3)
    target=unit(original+np.cross(axis,original)+np.cross(axis,np.cross(axis,original))/np.maximum(1+cosine[:,:,:,None],1e-20))
    if retain_source_strength:target*=np.linalg.norm(original_vector,axis=3)[:,:,:,None]
    groups=collections.defaultdict(list);geometric=collections.defaultdict(set)
    for face,parent in enumerate(triangles):
        for sample,b in enumerate(_CARRIER_SAMPLES):
            weights=collections.defaultdict(float)
            for vertex,value in zip(parent,b):
                if value:weights[int(physical[vertex])]+=float(value)
            own=tuple(sorted(weights.items()))
            full_n=source_normals if identity_normals is None else identity_normals
            full_t=source_tangents if identity_tangents is None else identity_tangents
            signature=b''.join(np.asarray(np.concatenate((full_n[:,vertex],full_t[:,vertex]),axis=1),dtype='<f4').tobytes()
                for vertex,weight in sorted(zip(parent,b),key=lambda row:int(physical[row[0]]))if weight)
            hand=1. if source_hand is None else float(b@source_hand[parent])
            signature=(slot,hand,signature)
            groups[own,signature].append((face,sample));geometric[own].add(signature)
    for (point,_),members in groups.items():
        held=len(point)==1 and point[0][0]in held_vertices or len(point)==2 and tuple(group for group,_ in point)in held_edges
        if held:value=(original_vector if retain_source_strength else original)[:,members[0][0],members[0][1]]
        else:
            value=np.mean(np.stack([target[:,face,sample]for face,sample in members],axis=1),axis=1)
            value=unit(value)
            if retain_source_strength:value*=np.linalg.norm(original_vector[:,members[0][0],members[0][1]],axis=1)[:,None]
        for face,sample in members:target[:,face,sample]=value
    # Normals are directional Bernstein controls, not interpolatory positions.
    # Turning a midpoint direction into 2*Nmid-(Na+Nb)/2 can overshoot through
    # zero and create a false normal singularity on a perfectly valid surface.
    # A held edge keeps the original linearly interpolated endpoint normals.
    normal_controls=target.copy()
    for face,parent in enumerate(triangles):
        for sample,a,b in((3,0,1),(4,1,2),(5,2,0)):
            if tuple(sorted(map(int,physical[parent[[a,b]]])))in held_edges:
                normal_controls[:,face,sample]=.5*(normal_controls[:,face,a]+normal_controls[:,face,b])
    return normal_controls,dict(authority='source authored N/T over every stored frame, slot-qualified hair domain and original held alpha/material boundaries',
        basis='convex directional Bernstein controls; held edges retain linear source normals',
        sharedNormalControlGroups=sum(len(members)>1 for members in groups.values()),
        distinctSourceHardFrameExceptions=sum(len(value)>1 for value in geometric.values()),
        shaderSourceInterpolationStrengthRetained=retain_source_strength,
        sourceUvAndTangentAttributesNotMerged=True)

def _carrier_evaluate_normal(normal_controls,face_ids,bary):
    return unit(np.einsum('ni,pnij->pnj',_carrier_coefficients(bary),normal_controls[:,face_ids]))

def _carrier_quadratic_chord_bound(midpoint_errors):
    """Exact maximum of the scalar triangle-inequality residual envelope.

    If D_ij is the quadratic residual control, |E| is bounded by
    2*b0*b1*|D01|+2*b1*b2*|D12|+2*b2*b0*|D20|. Its maximum is on an edge or
    at the valid interior stationary point; each |D| is twice a midpoint error.
    """
    a,b,c=np.moveaxis(np.asarray(midpoint_errors,float)*2,-1,0)
    m=b-a-c;det=4*a*c-m*m
    valid=det>0
    x=np.divide(c*(2*a+m),det,out=np.zeros_like(det),where=valid)
    y=np.divide(a*(m+2*c),det,out=np.zeros_like(det),where=valid)
    interior=valid&(x>0)&(y>0)&(x+y<1)
    value=2*a*x+2*c*y-2*a*x*x-2*c*y*y+2*m*x*y
    boundary=np.maximum.reduce((a,b,c))*.5
    return np.maximum(boundary,np.where(interior,value,0.))+64*np.finfo(float).eps*np.maximum.reduce((a,b,c))

def _carrier_grid_plan(triangles,divisions):
    if type(divisions)is not int or divisions<1:raise ValueError('A carrier sampling division must be explicit.')
    rows=[];lookup={};cells=[];parents=[];source_bary=[]
    def vertex(face,ij):
        bary=np.asarray([divisions-sum(ij),*ij],float)/divisions;parent=triangles[face];support=np.flatnonzero(bary>0)
        if len(support)==1:key=('vertex',int(parent[support[0]]))
        elif len(support)==2:
            a,b=sorted(map(int,parent[support]));key=('edge',a,b,float(bary[np.flatnonzero(parent==b)[0]]))
        else:key=('face',face,*ij)
        if key not in lookup:
            lookup[key]=len(rows);rows.append(dict(face=face,barycentric=bary.tolist(),triangle=parent.tolist()))
        return lookup[key],bary
    for face in range(len(triangles)):
        for i in range(divisions):
            for j in range(divisions-i):
                first=[(i,j),(i+1,j),(i,j+1)]
                for ij in(first,[(i+1,j),(i+1,j+1),(i,j+1)]if i+j<divisions-1 else[]):
                    if not ij:continue
                    nodes=[vertex(face,value)for value in ij]
                    cells.append([row[0]for row in nodes]);source_bary.append([row[1]for row in nodes]);parents.append(face)
    return dict(vertices=rows,triangles=np.asarray(cells,int),parents=np.asarray(parents,int),sourceBarycentric=np.asarray(source_bary,float))

def _carrier_refine_plan(plan,triangles,physical,marked_cells,locked_edges=(),*,positions=None,controls=None):
    """Red/green dyadic refinement closes each marked physical source edge."""
    def point_key(face,bary):
        values=collections.defaultdict(float)
        for vertex,weight in zip(triangles[face],bary):
            if weight:values[int(physical[vertex])]+=float(weight)
        return tuple(sorted(values.items()))
    def edge_key(face,first,second):return tuple(sorted((point_key(face,first),point_key(face,second))))
    def locked(face,first,second):
        common=np.flatnonzero((first==0)&(second==0))
        if len(common)!=1:return False
        edge=tuple(sorted(map(int,physical[triangles[face,np.flatnonzero(np.arange(3)!=common[0])]])))
        return edge in locked_edges
    marked=set()
    for face,bary,selected in zip(plan['parents'],plan['sourceBarycentric'],marked_cells):
        if selected:
            for a,b in((0,1),(1,2),(2,0)):
                if not locked(face,bary[a],bary[b]):marked.add(edge_key(face,bary[a],bary[b]))
    if (positions is None)!=(controls is None):raise ValueError('Green carrier orientation requires both the fixed positions and their controls.')
    green_choices={};alternate=0;forced=0;compared=0
    def green_options(face,points,active):
        edges=[(0,1),(1,2),(2,0)];midpoint=[(points[a]+points[b])*.5 for a,b in edges]
        unmarked=active.index(False);c,a=edges[unmarked];b=3-a-c
        ab=next(midpoint[index]for index,edge in enumerate(edges)if set(edge)=={a,b})
        bc=next(midpoint[index]for index,edge in enumerate(edges)if set(edge)=={b,c})
        corner=[points[b],bc,ab]
        return [[corner,[points[a],ab,points[c]],[ab,bc,points[c]]],
                [corner,[points[a],ab,bc],[points[a],bc,points[c]]]]
    if positions is not None:
        def quality(face,cells):
            at=np.asarray(cells,float).reshape(-1,3);p,_=_carrier_evaluate(positions,triangles,controls,np.full(len(at),face),at)
            p=p.reshape(len(positions),len(cells),3,3);cross=np.cross(p[:,:,1]-p[:,:,0],p[:,:,2]-p[:,:,0]);area=np.linalg.norm(cross,axis=2)
            parent=positions[:,triangles[face]];before=np.cross(parent[:,1]-parent[:,0],parent[:,2]-parent[:,0])
            signed=np.einsum('pti,pi->pt',cross,before)
            if not np.isfinite(p).all()or np.any(signed<=0)or np.any(area<=0):return None
            lengths=np.linalg.norm(p-np.roll(p,1,axis=2),axis=3).max(axis=2)
            return float(np.min(area/np.maximum(lengths*lengths,1e-30)))
        while True:
            additional=set();green_choices={}
            for cell,(face,points)in enumerate(zip(plan['parents'],plan['sourceBarycentric'])):
                active=[edge_key(face,points[a],points[b])in marked for a,b in((0,1),(1,2),(2,0))]
                if sum(active)!=2:continue
                options=green_options(face,points,active);valid=[];compared+=2
                for index,choice in enumerate(options):
                    score=quality(face,choice)
                    if score is not None:valid.append((score,-index,choice))
                if valid:
                    _,index,choice=max(valid,key=lambda row:row[:2]);green_choices[cell]=choice;alternate+=index==-1
                else:
                    unmarked=active.index(False);a,b=((0,1),(1,2),(2,0))[unmarked]
                    if locked(face,points[a],points[b]):raise ValueError('A rejected green carrier cell cannot split its held native source edge.')
                    additional.add(edge_key(face,points[a],points[b]));forced+=1
            additional-=marked
            if not additional:break
            marked.update(additional)
    next_cells=[];next_parents=[]
    for cell,(face,points)in enumerate(zip(plan['parents'],plan['sourceBarycentric'])):
        edges=[(0,1),(1,2),(2,0)];active=[edge_key(face,points[a],points[b])in marked for a,b in edges]
        midpoint=[(points[a]+points[b])*.5 for a,b in edges];count=sum(active)
        if count==0:cells=[points]
        elif count==3:
            cells=[[points[0],midpoint[0],midpoint[2]],[midpoint[0],points[1],midpoint[1]],
                   [midpoint[2],midpoint[1],points[2]],[midpoint[0],midpoint[1],midpoint[2]]]
        elif count==1:
            edge=active.index(True);a,b=edges[edge];c=3-a-b;m=midpoint[edge]
            cells=[[points[a],m,points[c]],[m,points[b],points[c]]]
        else:
            cells=green_choices[cell]if positions is not None else green_options(face,points,active)[0]
        next_cells.extend(cells);next_parents.extend([face]*len(cells))
    rows=[];lookup={};indices=[]
    for face,points in zip(next_parents,next_cells):
        cell=[];parent=triangles[face]
        for bary in points:
            support=np.flatnonzero(bary>0)
            if len(support)==1:key=('vertex',int(parent[support[0]]))
            elif len(support)==2:
                a,b=sorted(map(int,parent[support]));key=('edge',a,b,float(bary[np.flatnonzero(parent==b)[0]]))
            else:key=('face',int(face),float(bary[1]),float(bary[2]))
            if key not in lookup:
                lookup[key]=len(rows);rows.append(dict(face=int(face),barycentric=np.asarray(bary).tolist(),triangle=parent.tolist()))
            cell.append(lookup[key])
        indices.append(cell)
    return dict(vertices=rows,triangles=np.asarray(indices,int),parents=np.asarray(next_parents,int),sourceBarycentric=np.asarray(next_cells,float),
        greenOrientation=dict(allStoredPosesUsed=positions is not None,candidatesCompared=compared,alternateChoices=alternate,forcedConformingCells=forced))

def _carrier_box(values,radius,sampler):
    if radius==0:return values.copy()
    result=np.asarray(values,float)
    for axis in(0,1):
        indices=source_texture_index(np.arange(-radius,result.shape[axis]+radius),result.shape[axis],sampler['m_WrapV'if axis==0 else'm_WrapU'])
        padded=np.take(result,indices,axis=axis)
        prefix=np.concatenate((np.zeros_like(np.take(padded,[0],axis=axis)),np.cumsum(padded,axis=axis)),axis=axis)
        a=[slice(None)]*result.ndim;b=list(a);a[axis]=slice(2*radius+1,None);b[axis]=slice(None,-(2*radius+1))
        result=(prefix[tuple(a)]-prefix[tuple(b)])/(2*radius+1)
    return result

def _carrier_source_tensor(path,*,radius=2,sampler):
    with Image.open(path)as image:pixels=np.asarray(image.convert('RGB'),float)[::-1]/255.
    luminance=pixels@np.asarray([.2126,.7152,.0722])
    # The central stencil follows each declared source texture wrap mode. Tensor
    # tangents remain unoriented, independently of a root/tip decision.
    height,width=luminance.shape
    dx=(luminance[:,source_texture_index(np.arange(width)+1,width,sampler['m_WrapU'])]-luminance[:,source_texture_index(np.arange(width)-1,width,sampler['m_WrapU'])])*.5
    dy=(luminance[source_texture_index(np.arange(height)+1,height,sampler['m_WrapV'])]-luminance[source_texture_index(np.arange(height)-1,height,sampler['m_WrapV'])])*.5
    return _carrier_box(np.stack((dx*dx,dx*dy,dy*dy),axis=2),radius,sampler)

def _carrier_sample_tensor(tensor,uv,scale,offset,sampler):
    h,w=tensor.shape[:2];xy=(np.asarray(uv)*scale+offset)*[w,h]-.5;low=np.floor(xy).astype(int);f=xy-low
    values=np.zeros((len(xy),3))
    mode=sampler['m_FilterMode']
    if mode==0:
        point=np.floor((np.asarray(uv)*scale+offset)*[w,h]).astype(int)
        values=tensor[source_texture_index(point[:,1],h,sampler['m_WrapV']),source_texture_index(point[:,0],w,sampler['m_WrapU'])]
    elif mode in(1,2):
        for dx,dy,weight in((0,0,(1-f[:,0])*(1-f[:,1])),(1,0,f[:,0]*(1-f[:,1])),(0,1,(1-f[:,0])*f[:,1]),(1,1,f[:,0]*f[:,1])):
            values+=tensor[source_texture_index(low[:,1]+dy,h,sampler['m_WrapV']),source_texture_index(low[:,0]+dx,w,sampler['m_WrapU'])]*weight[:,None]
    else:raise ValueError('A source flow texture has an unsupported native filter mode.')
    a,b,d=values.T;delta=np.hypot(a-d,2*b);large=(a+d+delta)*.5;small=(a+d-delta)*.5
    # Largest eigenvector is the transverse image gradient; rotate by90°.
    angle=.5*np.arctan2(2*b,a-d)
    flow=np.column_stack((-np.sin(angle),np.cos(angle)))
    coherence=np.divide(large-small,large+small,out=np.zeros_like(large),where=large+small>1e-20)
    return flow,coherence,large+small

def _carrier_face_flow(packet,positions,material,*,radius=2):
    uv=np.asarray(next(row['values']for row in packet['uv']if row['channel']==0)).reshape(-1,4)[:,:2]
    triangles=np.asarray(packet['subMeshes'][material['slot']],int).reshape(-1,3)
    reference=material['maps']['_MainTex'];sampler=material.get('sourceTextureSamplers',{}).get('_MainTex')
    if sampler is None:raise ValueError('A source pattern requires its actual native Main sampler.')
    tensor=_carrier_source_tensor(reference['path'],radius=radius,sampler=sampler)
    bary=np.asarray([[1/3,1/3,1/3],[.5,.5,0.],[.5,0.,.5],[0.,.5,.5]])
    query=np.einsum('qi,fij->fqj',bary,uv[triangles]);axis,coherence,signal=_carrier_sample_tensor(tensor,query.reshape(-1,2),reference['scale'],reference['offset'],sampler)
    # Gradients live in actual atlas pixel coordinates. Reverse each map scale
    # when returning their tangent to source UV0 before the geometric Jacobian.
    axis=axis/np.asarray([tensor.shape[1],tensor.shape[0]])/np.asarray(reference['scale'])
    p=np.asarray(positions)[triangles];e=np.stack((p[:,1]-p[:,0],p[:,2]-p[:,0]),axis=2)
    d=np.stack((uv[triangles[:,1]]-uv[triangles[:,0]],uv[triangles[:,2]]-uv[triangles[:,0]]),axis=2)
    valid=abs(np.linalg.det(d))>1e-12;derivative=np.zeros((len(triangles),3,2));derivative[valid]=e[valid]@np.linalg.inv(d[valid])
    directions=np.einsum('fij,fqj->fqi',derivative,axis.reshape(len(triangles),len(bary),2))
    lengths=np.linalg.norm(directions,axis=2);directions/=np.maximum(lengths[:,:,None],1e-20)
    weights=coherence.reshape(len(triangles),len(bary))*signal.reshape(len(triangles),len(bary))
    tensors=np.einsum('fq,fqi,fqj->fij',weights,directions,directions)
    values,vectors=np.linalg.eigh(tensors);flow=vectors[:,:,-1]
    quality=np.divide(values[:,-1]-values[:,-2],values.sum(axis=1),out=np.zeros(len(values)),where=values.sum(axis=1)>1e-20)
    return flow,quality,dict(radiusPixels=radius,quadratureSamples=len(bary),authority='original Main luminance structure tensor sampled through source UV0 and declared texture transform',
        nativeSampler=copy.deepcopy(sampler),sourceMip=0,unoriented=True,zeroSignalFaces=int((weights.sum(axis=1)<=1e-20).sum()),invalidSourceUvFaces=int((~valid).sum()))

def _carrier_source_patches(points,triangles,groups,eligible,flow,*,period):
    """Carry phase across a declared source edge using each face's local line.

    Every qualified face is assigned. Nonintegrable cycles and singular local
    coordinates make recorded splice edges, never invented strand tips and
    never a reason to drop a qualified source face.
    """
    points=np.asarray(points,float);triangles=np.asarray(triangles,int);groups=np.asarray(groups,int)
    q=points[triangles];cross=np.cross(q[:,1]-q[:,0],q[:,2]-q[:,0]);area=np.linalg.norm(cross,axis=1)*.5
    normal=cross/np.maximum(np.linalg.norm(cross,axis=1)[:,None],1e-20)
    flow=np.asarray(flow,float);flow-=normal*np.sum(flow*normal,axis=1)[:,None]
    flow/=np.maximum(np.linalg.norm(flow,axis=1)[:,None],1e-20)
    transverse=np.cross(flow,normal);edges=collections.defaultdict(list)
    for face,parent in enumerate(triangles):
        for a,b in((0,1),(1,2),(2,0)):
            first,last=int(groups[parent[a]]),int(groups[parent[b]])
            if first!=last:edges[tuple(sorted((first,last)))].append((face,a,b))
    adjacent=collections.defaultdict(list)
    for edge,members in edges.items():
        if len(members)!=2:continue
        for face,a,b in members:
            other,oa,ob=next(row for row in members if row[0]!=face)
            if groups[triangles[face,a]]!=groups[triangles[other,oa]]:oa,ob=ob,oa
            adjacent[face].append((other,a,b,oa,ob,edge))
    coordinates={};patch_id={};roots=[];blocked=set();scales=[]
    for seed in sorted(map(int,np.flatnonzero(eligible)),key=lambda face:(-area[face],face)):
        if seed in coordinates:continue
        patch=len(roots);roots.append(seed)
        coordinates[seed]=(q[seed]-q[seed].mean(axis=0))@transverse[seed];patch_id[seed]=patch;pending=collections.deque([seed])
        while pending:
            face=pending.popleft();parent=triangles[face]
            for other,a,b,oa,ob,edge in adjacent[face]:
                if not eligible[other]or other in coordinates:continue
                p=q[other];du=coordinates[face][b]-coordinates[face][a]
                direction=p[ob]-p[oa];length=float(np.linalg.norm(direction))
                if length<=0:blocked.add(edge);continue
                axis=direction/length
                current=np.stack((q[face,1]-q[face,0],q[face,2]-q[face,0]),axis=1)
                previous=np.linalg.lstsq(current.T,coordinates[face][1:]-coordinates[face][0],rcond=None)[0]
                angle=np.arctan2(float(axis@np.cross(normal[face],normal[other])),float(normal[face]@normal[other]))
                transported=previous*np.cos(angle)+np.cross(axis,previous)*np.sin(angle)+axis*np.dot(axis,previous)*(1-np.cos(angle))
                preferred=transverse[other].copy()
                if np.dot(preferred,transported)<0:preferred=-preferred
                # A local source pattern is a direction prior, not permission
                # to rescale an entire downstream component. Orthogonally
                # project that one face's covector onto its inherited physical
                # edge constraint. This retains the edge's measured phase
                # without averaging unrelated paths or multiplying scales.
                gradient=preferred+direction*((du-float(direction@preferred))/(length*length))
                scale=float(np.linalg.norm(gradient))
                if not np.isfinite(gradient).all()or scale<=1e-12:blocked.add(edge);continue
                values=coordinates[face][a]+(p-p[oa])@gradient
                values[oa]=coordinates[face][a];values[ob]=coordinates[face][b]
                coordinates[other]=values;patch_id[other]=patch;pending.append(other);scales.append(abs(float(scale)))
    if set(coordinates)!=set(map(int,np.flatnonzero(eligible))):raise ValueError('A carrier patch omitted a qualified source face.')
    cuts=[];reasons=collections.Counter();maximum_join=0.;matching=0
    for edge,members in edges.items():
        selected=[row for row in members if eligible[row[0]]]
        if not selected:continue
        if len(members)==1:reason='original-open-source-boundary'
        elif len(members)!=2:reason='native-nonmanifold-source-boundary'
        elif len(selected)!=2:reason='preserved-material-or-alpha-domain-boundary'
        else:
            (first,a,b),(second,oa,ob)=selected
            if groups[triangles[first,a]]!=groups[triangles[second,oa]]:oa,ob=ob,oa
            delta=coordinates[first][[a,b]]-coordinates[second][[oa,ob]]
            residual=delta-np.round(delta[0]/period)*period
            error=float(np.max(abs(residual)))
            if patch_id[first]==patch_id[second]and error<=2e-6:
                matching+=1;maximum_join=max(maximum_join,error);continue
            reason='local-phase-ancestry-splice'
        face,a,b=selected[0];cuts.append((triangles[face,[a,b]].tolist(),reason));reasons[reason]+=1
    return coordinates,dict(patchCount=len(roots),roots=roots,matchingSourceEdges=matching,
        maximumUncutEdgePhaseErrorMetres=maximum_join,boundaryReasons=dict(reasons),
        transportScaleQuantiles=np.quantile(scales,[0,.25,.5,.75,1]).tolist()if scales else[],
        qualifiedFaces=len(coordinates),excludedForDirectionAngle=0,wholeComponentCoordinateAveraging=False,
        phaseConstraintOwner='per-face original-pattern prior projected onto its inherited physical edge'),cuts

def _carrier_segment_distance(points,segments):
    points=np.asarray(points,float);segments=np.asarray(segments,float)
    if not len(segments):return np.full(len(points),np.inf)
    result=np.full(len(points),np.inf)
    for start in range(0,len(segments),64):
        a=segments[start:start+64,0];edge=segments[start:start+64,1]-a;length=np.sum(edge*edge,axis=1)
        if np.any(length<=0):raise ValueError('An original carrier boundary has a zero physical edge.')
        delta=points[:,None,:]-a[None];f=np.clip(np.sum(delta*edge[None],axis=2)/length[None],0,1)
        result=np.minimum(result,np.linalg.norm(delta-f[:,:,None]*edge[None],axis=2).min(axis=1))
    return result

def _carrier_profile_offsets(positions,normal_field,source,bary,phase,*,segments,protected,width,height,height_limits=None):
    """Evaluate the same source-owned profile for vertices or error probes."""
    source=np.asarray(source,int);bary=np.asarray(bary,float)
    base=np.einsum('ni,nij->nj',bary,positions[0,source])
    distance=_carrier_segment_distance(base,segments);fraction=np.clip(distance/(width*.25),0,1)
    fade=fraction*fraction*(3-2*fraction)
    point_distance=np.full(len(base),np.inf)
    for start in range(0,len(protected),64):
        centers=positions[0,protected[start:start+64]]
        point_distance=np.minimum(point_distance,np.linalg.norm(base[:,None,:]-centers[None,:,:],axis=2).min(axis=1))
    fraction=np.clip(point_distance/(width*.25),0,1);fade*=fraction*fraction*(3-2*fraction)
    lift=height*(1-np.cos(2*np.pi*np.asarray(phase)/width))*.5*fade
    if height_limits is not None:lift=np.minimum(lift,np.einsum('ni,ni->n',bary,height_limits[source]))
    normal=unit(np.einsum('ni,pnij->pnj',bary,normal_field[:,source]))
    return lift,normal*lift[None,:,None]


def prepare_source_curved_core(native,positions,normals,tangents,materials,selected_faces,physical,contact,settings,*,native_positions):
    """Own source-pattern curves without treating texture charts as hair tips."""
    for field in('protectedVertexIds','contactVertexIds','fixedVertexIds'):
        if any(type(index)is not int or not 0<=index<native['vertexCount']for index in contact.get(field,[])):
            raise ValueError('A carrier contact or fixed sample escaped its native render vertex owner.')
    count=native['vertexCount'];protected=set(map(int,contact['protectedVertexIds']))|set(map(int,contact.get('fixedVertexIds',[])))
    actual=set(map(int,contact['contactVertexIds']))
    uv=np.asarray(next(row['values']for row in native['uv']if row['channel']==0)).reshape(-1,4)[:,:2]
    slots=[np.asarray(flat,int).reshape(-1,3)for flat in native['subMeshes']]
    total=np.zeros((len(positions),int(physical.max())+1,3))
    for triangles in slots:
        p=positions[:,triangles];cross=np.cross(p[:,:,1]-p[:,:,0],p[:,:,2]-p[:,:,0])
        for corner in range(3):
            for pose in range(len(positions)):np.add.at(total[pose],physical[triangles[:,corner]],cross[pose])
    field=unit(total)[:,physical];prepared=[]
    for slot,(triangles,selected,material)in enumerate(zip(slots,selected_faces,materials)):
        selected=np.asarray(selected,bool).copy()
        if not selected.any():prepared.append(None);continue
        flow,quality,pattern=_carrier_face_flow(native,positions[0],material,radius=settings['patternRadiusPixels'])
        phase,phase_proof,cuts=_carrier_source_patches(positions[0],triangles,physical,selected,flow,period=settings['widthMetres'])
        chosen=np.flatnonzero(selected);source=triangles[chosen]
        held=[edge for edge,reason in cuts if reason!='local-phase-ancestry-splice']
        held_edges={tuple(sorted(map(int,physical[edge])))for edge in held};held_vertices=set(map(int,physical[list(protected)]))
        for edge in held_edges:held_vertices.update(edge)
        parents=np.repeat(source,len(_CARRIER_SAMPLES),axis=0);bary=np.tile(_CARRIER_SAMPLES,(len(source),1))
        values=np.asarray([_CARRIER_SAMPLES@phase[int(face)]for face in chosen]).ravel()
        _,raw=_carrier_profile_offsets(positions,field,parents,bary,values,segments=np.asarray([positions[0,edge]for edge in held]).reshape(-1,2,3),
            protected=np.asarray(sorted(protected),int),width=settings['widthMetres'],height=settings['heightMetres'])
        samples,sharing=_carrier_shared_source_samples(raw.reshape(len(positions),len(source),6,3),source,physical,held_edges,held_vertices)
        normal_edges={tuple(sorted(map(int,physical[edge])))for edge,reason in cuts if reason in('preserved-material-or-alpha-domain-boundary','native-nonmanifold-source-boundary')}
        normal_vertices=set(map(int,physical[list(actual)]))
        for parent in source:
            for a,b in((0,1),(1,2),(2,0)):
                edge=tuple(sorted(map(int,physical[parent[[a,b]]])))
                if all(vertex in normal_vertices for vertex in edge):normal_edges.add(edge)
        for edge in normal_edges:normal_vertices.update(edge)
        # Authored fixed render samples constrain their points. Two fixed
        # endpoints alone do not declare the intervening hair edge immutable.
        normal_vertices.update(map(int,physical[contact.get('fixedVertexIds',[])]))
        for face,parent in enumerate(source):
            for corner,vertex in enumerate(parent):
                if int(physical[vertex])in normal_vertices:samples[:,face,corner]=0
            for sample,a,b in((3,0,1),(4,1,2),(5,2,0)):
                if tuple(sorted(map(int,physical[parent[[a,b]]])))in normal_edges:samples[:,face,sample]=0
        combined=np.concatenate((positions,native_positions));combined_samples=np.concatenate((samples,samples))
        all_controls,fit=_carrier_fit_controls(combined,source,combined_samples,physical);controls=all_controls[:len(positions)]
        fit['beforeAndAfterSourceFittingChecked']=True
        changed=np.any(controls!=0,axis=(0,2,3))
        for parent in source[~changed]:
            for a,b in((0,1),(1,2),(2,0)):
                edge=tuple(sorted(map(int,physical[parent[[a,b]]])));normal_edges.add(edge);normal_vertices.update(edge)
        selected[chosen[~changed]]=False;selected_faces[slot]=selected
        chosen=chosen[changed];source=source[changed];controls=controls[:,changed]
        hand=np.asarray(native['tangents']).reshape(-1,4)[:,3]
        normal_controls,normal_proof=_carrier_source_normal_controls(positions,normals,tangents,source,controls,physical,normal_edges,normal_vertices,
            source_hand=hand,slot=slot,identity_normals=normals,identity_tangents=tangents,retain_source_strength=True)
        prepared.append(dict(slot=slot,triangles=source,sourceFaces=chosen,controls=controls,normalControls=normal_controls,heldEdges=normal_edges,
            flow=flow,phase=phase,pattern=pattern,phaseProof=phase_proof,sharing=sharing,fit=fit,normalProof=normal_proof,
            sourceMain=copy.deepcopy(material['maps']['_MainTex'])))
    return prepared


def plan_source_curved_core(prepared,positions,normals,tangents,physical,vertex_budget,width,*,slot_triangles,inverse,native_positions):
    """Refine declared P geometry; retain shader-normal approximation as evidence."""
    records=[];triangles=[];parents=[];p_all=[];n_all=[];t_all=[];flows=[];proofs=[];components=[];curvature_groups=[]
    linear=np.linalg.inv(inverse)
    for slot,data in enumerate(prepared):
        if data is None or not len(data['triangles']):
            triangles.append([]);parents.append([]);curvature_groups.append([-1]*len(slot_triangles[slot]));continue
        source=data['triangles'];control=data['controls'];plan=_carrier_grid_plan(source,1);stages=[]
        while True:
            face=np.asarray([row['face']for row in plan['vertices']],int);bary=np.asarray([row['barycentric']for row in plan['vertices']])
            verification=np.concatenate((positions,native_positions));offsets=np.concatenate((control,control))
            all_points,_=_carrier_evaluate(verification,source,offsets,face,bary);p=all_points[:len(positions)];ids=plan['triangles'];child=plan['sourceBarycentric'];owner=plan['parents']
            probes=np.asarray([[.5,.5,0.],[0.,.5,.5],[.5,0.,.5]])
            at=np.einsum('qi,tij->tqj',probes,child).reshape(-1,3);parent=np.repeat(owner,3)
            truth,_=_carrier_evaluate(verification,source,offsets,parent,at)
            approximation=np.einsum('qi,ptij->ptqj',probes,all_points[:,ids]);errors=np.linalg.norm(truth.reshape(approximation.shape)-approximation,axis=3)
            chord=_carrier_quadratic_chord_bound(errors);before=verification[:,source[owner]];cross=np.cross(before[:,:,1]-before[:,:,0],before[:,:,2]-before[:,:,0])
            blocks=[]
            for block in(all_points[:len(positions)],all_points[len(positions):]):
                local=block@inverse.T;base=local[0].astype('<f4')
                blocks.append(np.asarray([base]+[(base+(value-local[0]).astype('<f4')).astype('<f4')for value in local[1:]]).astype(float)@linear.T)
            stored=np.concatenate(blocks)
            q=stored[:,ids];signed=np.sum(np.cross(q[:,:,1]-q[:,:,0],q[:,:,2]-q[:,:,0])*cross,axis=2)
            chord+=np.linalg.norm(stored-all_points,axis=2)[:,ids].max(axis=2)
            bad=np.any(chord>PROFILE['maximumChordErrorMetres'],axis=0)|np.any(signed<=0,axis=0)
            stages.append(dict(vertices=len(face),triangles=len(ids),maximumChordBoundMetres=float(chord.max()),invalidChildPosePairs=int((signed<=0).sum()),normalErrorUsedToRefine=False))
            if len(records)+len(face)>vertex_budget:raise OverflowError('The declared source carrier exceeds its retained all-frame allocation.')
            if not bad.any():break
            refined=_carrier_refine_plan(plan,source,physical,bad,data['heldEdges'],positions=verification,controls=offsets)
            if len(refined['vertices'])<=len(plan['vertices']):raise ValueError('A source carrier cannot resolve its geometry beside retained source edges.')
            plan=refined
        n=_carrier_evaluate_normal(data['normalControls'],face,bary)
        source_t=np.einsum('ni,pnij->pnj',bary,tangents[:,source[face]]);t=unit(source_t-n*np.sum(source_t*n,axis=2)[:,:,None])
        if not np.isfinite(n).all()or not np.isfinite(t).all()or np.any(np.linalg.norm(n,axis=2)<=1e-20)or np.any(np.linalg.norm(t,axis=2)<=1e-20):
            raise ValueError('A source carrier has an invalid authored render frame.')
        normal_probes=np.asarray([[1/3,1/3,1/3],[.5,.5,0.],[0.,.5,.5],[.5,0.,.5]])
        query=np.einsum('qi,tij->tqj',normal_probes,child).reshape(-1,3)
        target=_carrier_evaluate_normal(data['normalControls'],np.repeat(owner,4),query).reshape(len(positions),len(ids),4,3)
        rendered=unit(np.einsum('qi,ptij->ptqj',normal_probes,n[:,ids]))
        normal_error=np.degrees(np.arccos(np.clip(np.sum(target*rendered,axis=3),-1,1)))
        start=len(records);component=len(components);components.append(dict(slot=slot,faces=data['sourceFaces'].tolist(),curvatureGroup=component))
        groups=np.full(len(slot_triangles[slot]),-1,int);groups[data['sourceFaces']]=component;curvature_groups.append(groups.tolist())
        for row in plan['vertices']:
            parent=int(row['face']);record=dict(row,slot=slot,face=int(data['sourceFaces'][parent]),component=component,curvatureGroup=component)
            records.append(record);flows.append(data['flow'][record['face']].tolist())
        triangles.append((ids+start).tolist());parents.append(data['sourceFaces'][owner].tolist());p_all.append(p);n_all.append(n);t_all.append(t)
        proofs.append(dict(slot=slot,pattern=data['pattern'],phase=data['phaseProof'],sharing=data['sharing'],continuousOrientation=data['fit'],
            sourceNormalOwner=data['normalProof'],geometryRefinement=stages,sourceMain=data['sourceMain'],
            shaderNormalApproximation=dict(maximumDegrees=float(normal_error.max()),cellsAboveDeclaredLimit=int(np.any(normal_error>PROFILE['maximumNormalChordDegrees'],axis=(0,2)).sum()),accepted=False)))
    if not records:raise ValueError('The declared source carrier has no changed material-owned surface.')
    return dict(vertices=records,triangles=triangles,parents=parents,components=components,physicalGroups=physical.tolist(),curvatureGroupCount=len(components),
        curvatureGroupsBySlot=curvature_groups,
        sourceTrianglesBySlot=[np.asarray(value,int).reshape(-1,3).tolist()for value in slot_triangles],widthMetres=width,maximumSourceAreaError=0.,allocatedVertices=len(records),reusedNativeVertices=0,
        sourceFaces=sum(len(data['triangles'])for data in prepared if data is not None),sourceQuadratic=dict(points=np.concatenate(p_all,axis=1),normals=np.concatenate(n_all,axis=1),tangents=np.concatenate(t_all,axis=1),
            flow=np.asarray(flows),proofs=proofs),flowCarrierCells=dict(model='source-quadratic',sourceDomainCoverageExact=True),flowGuideProof=dict(authority='native Main source pattern',slots=proofs))
def plan_curved_core(coordinates, slot_triangles, selected_faces, width, vertex_budget, reusable_vertices=(),
                     *, positions=None, physical_groups=None, subdivide_small_faces=True,flow_fields=None,protected_vertices=()) -> 'dict[str, Any]':
    """Partition source faces with shared physical-edge samples and face centers.

    Cells cover their source triangle, including its boundary. Shared source
    edges reuse one vertex; UV/material cuts retain independent vertices. The
    native vertex prefix remains deformation authority, not render topology.
    Coordinates are the existing physical groom field converted to metres.
    """
    if flow_fields is not None:
        return _plan_flow_carrier_cells(coordinates,slot_triangles,selected_faces,width,vertex_budget,
            reusable_vertices,positions,physical_groups,flow_fields,protected_vertices)
    assert width > 0 and vertex_budget >= 0
    coordinates = np.asarray(coordinates, dtype=float)
    assert coordinates.ndim == 2 and coordinates.shape[1] == 2 and np.isfinite(coordinates).all()
    vertices, lookup, triangles, parents, components = [], {}, [], [], []
    physical = np.arange(len(coordinates)) if physical_groups is None else np.asarray(physical_groups, dtype=int)
    points = np.c_[coordinates, np.zeros(len(coordinates))] if positions is None else np.asarray(positions, dtype=float)
    assert physical.shape == (len(coordinates),) and points.shape == (len(coordinates), 3)
    reusable_vertices = set(map(int, reusable_vertices))
    allocated = 0
    maximum_area_error = 0.

    physical_components = 0
    curvature_groups_by_slot=[]
    for slot, source in enumerate(slot_triangles):
        source = np.asarray(source, dtype=int).reshape(-1, 3)
        selected = np.asarray(selected_faces[slot], dtype=bool)
        assert selected.shape == (len(source),)
        edge_faces = collections.defaultdict(list)
        physical_edges = collections.defaultdict(list)
        for face in np.flatnonzero(selected):
            for a, b in zip(source[face], np.roll(source[face], -1)):
                edge_faces[tuple(sorted((int(a), int(b))))].append(int(face))
                physical_edges[tuple(sorted((int(physical[a]), int(physical[b]))))].append(int(face))
        physical_neighbours = collections.defaultdict(set)
        for members in physical_edges.values():
            if len(members) == 2:
                for face in members:physical_neighbours[face].update(members)
        physical_owner = {}
        for first in map(int, np.flatnonzero(selected)):
            if first in physical_owner:continue
            work = [first];physical_owner[first] = physical_components
            while work:
                face = work.pop()
                for other in physical_neighbours[face]:
                    if other not in physical_owner:physical_owner[other] = physical_components;work.append(other)
            physical_components += 1
        curvature_groups_by_slot.append([physical_owner.get(face,-1)for face in range(len(source))])
        # A physical edge has one split on both texture charts. UV corners keep
        # independent vertices/samples, while their PN edge chords remain equal.
        split_edges = set()
        for edge, members in physical_edges.items():
            if len(members) != 2:continue
            face = members[0];corners = source[face]
            ends = [int(v) for v in corners if int(physical[v]) in edge]
            if len(ends) == 2 and np.linalg.norm(points[ends[1]]-points[ends[0]]) > width:
                split_edges.add(edge)
        neighbours = collections.defaultdict(set)
        for members in edge_faces.values():
            for face in members:
                neighbours[face].update(members)
        seen = set()
        slot_triangles_out, slot_parents = [], []
        for first in map(int, np.flatnonzero(selected)):
            if first in seen:
                continue
            pending, faces = [first], []
            seen.add(first)
            while pending:
                face = pending.pop()
                faces.append(face)
                for other in neighbours[face]:
                    if other not in seen:
                        seen.add(other)
                        pending.append(other)
            component_id = len(components)
            face_set = set(faces)
            boundary = [edge for edge, members in edge_faces.items()
                        if len(members) == 1 and members[0] in face_set]
            # Closed charts need no cut envelope. Open/material/UV boundaries
            # use exact source segments, including charts without inner vertices.
            segments = np.asarray([coordinates[list(edge)] for edge in boundary], dtype=float).reshape(-1, 2, 2)
            used = np.unique(source[faces])
            origin = coordinates[used].min(axis=0)
            components.append(dict(slot=slot, faces=faces, origin=origin.tolist(), boundarySegments=segments.tolist(),
                                   curvatureGroup=physical_owner[first]))

            def vertex(face, bary):
                nonlocal allocated
                corners = source[face]
                support = np.flatnonzero(bary > 1e-10)
                if len(support) == 1:
                    key = (slot, 'vertex', int(corners[support[0]]))
                elif len(support) == 2:
                    a, b = sorted(map(int, corners[support]))
                    fraction = float(bary[np.flatnonzero(corners == b)[0]])
                    key = (slot, 'edge', a, b, round(fraction, 11))
                else:
                    key = (slot, 'face', face, round(float(bary[1]), 11), round(float(bary[2]), 11))
                if key not in lookup:
                    lookup[key] = len(vertices)
                    record = dict(slot=slot, face=face, triangle=corners.tolist(), barycentric=bary.tolist(), component=component_id,
                                  curvatureGroup=physical_owner[face])
                    if len(support) == 1 and int(corners[support[0]]) in reusable_vertices:
                        record['nativeVertex'] = int(corners[support[0]])
                    else:
                        allocated += 1
                    vertices.append(record)
                    if allocated > vertex_budget:
                        raise OverflowError('Curved core partition exceeds its existing vertex budget.')
                return lookup[key]

            for face in faces:
                corners = source[face];polygon = []
                for a,b in ((0,1),(1,2),(2,0)):
                    polygon.append(np.eye(3)[a])
                    edge = tuple(sorted((int(physical[corners[a]]),int(physical[corners[b]]))))
                    if edge in split_edges:polygon.append((np.eye(3)[a]+np.eye(3)[b])*.5)
                center = np.full(3,1./3.)
                small=len(polygon)==3 and max(np.linalg.norm(points[corners[a]]-points[corners[b]])for a,b in((0,1),(1,2),(2,0)))<=width
                cells = [polygon] if small and not subdivide_small_faces else [[center,polygon[i],polygon[(i+1)%len(polygon)]] for i in range(len(polygon))]
                area = 0.
                for cell in cells:
                    bary=np.asarray(cell)
                    signed=float(np.linalg.det(np.stack((bary[1,1:]-bary[0,1:],bary[2,1:]-bary[0,1:]))))*.5
                    assert signed>0.
                    area+=signed;slot_triangles_out.append([vertex(face,point)for point in bary]);slot_parents.append(face)
                error = abs(area - .5)
                assert error <= 1e-9, ('Source-domain partition lost coverage.', slot, face, area)
                maximum_area_error = max(maximum_area_error, error)
        triangles.append(slot_triangles_out)
        parents.append(slot_parents)
    return dict(vertices=vertices, triangles=triangles, parents=parents, components=components,
                physicalGroups=physical.tolist(), curvatureGroupCount=physical_components,
                curvatureGroupsBySlot=curvature_groups_by_slot,
                sourceTrianglesBySlot=[np.asarray(v, dtype=int).reshape(-1, 3).tolist() for v in slot_triangles],
                widthMetres=width, maximumSourceAreaError=maximum_area_error,
                allocatedVertices=allocated, reusedNativeVertices=len(vertices)-allocated,
                sourceFaces=sum(int(np.sum(s)) for s in selected_faces))


def evaluate_curved_core(plan, positions, normals, tangents, coordinates):
    """Evaluate bounded PN support and real transverse tuft curvature.

    Both displacement and its first derivative vanish at source chart cuts.
    Native shading normals are transported by the surface change, so a flat
    unchanged chart does not acquire per-triangle shading normals.
    """
    if 'flowCarrierCells'in plan:return _evaluate_flow_carrier_cells(plan,positions,normals,tangents)
    records = plan['vertices']
    if not records:
        return np.empty((0, 3)), np.empty((0, 3)), np.empty((0, 3))
    corners = np.asarray([r['triangle'] for r in records], dtype=int)
    bary = np.asarray([r['barycentric'] for r in records], dtype=float)
    p = np.asarray(positions, dtype=float)[corners]
    n = unit(np.asarray(normals, dtype=float)[corners])
    t = np.asarray(tangents, dtype=float)[corners]
    q = np.asarray(coordinates, dtype=float)[corners]
    q_edges = np.stack((q[:, 1] - q[:, 0], q[:, 2] - q[:, 0]), axis=2)
    inverse = np.linalg.inv(q_edges)
    width = plan['widthMetres']
    lift = min(.00035, width * .12)
    components = np.asarray([r['component'] for r in records], dtype=int)
    # Bound shared physical edges locally. A skinny face constrains its own
    # edges, rather than flattening every unrelated face in the same chart.
    groups=np.asarray(plan.get('physicalGroups',np.arange(len(positions))),dtype=int)
    edge_limits=collections.defaultdict(list)
    face_limits={}
    for component in plan['components']:
        slot=component['slot'];source=plan['sourceTrianglesBySlot'][slot]
        for face in component['faces']:
            triangle=np.asarray(source[face],dtype=int);points=positions[triangle]
            edges=np.asarray((points[1]-points[0],points[2]-points[1],points[0]-points[2]))
            limit=float(np.linalg.norm(np.cross(edges[0],-edges[2]))/max(np.linalg.norm(edges,axis=1).max(),1e-30))*.12
            face_limits[slot,face]=limit
            for a,b in zip(triangle,np.roll(triangle,-1)):
                edge_limits[slot,tuple(sorted((int(groups[a]),int(groups[b]))))].append(limit)
    curvature_limit=np.asarray([face_limits[r['slot'],r['face']]for r in records])
    lift = np.minimum(lift, curvature_limit)
    controls = {}
    for a, b in ((0, 1), (1, 0), (1, 2), (2, 1), (2, 0), (0, 2)):
        edge = p[:, b] - p[:, a]
        delta = -n[:, a] * np.sum(edge * n[:, a], axis=1)[:, None] / 3.
        limits=[]
        for record,triangle in zip(records,corners):
            edge=edge_limits[record['slot'],tuple(sorted((int(groups[triangle[a]]),int(groups[triangle[b]]))))]
            limits.append(min(edge)if len(edge)==2 else 0.)
        limit = np.asarray(limits)
        delta *= np.minimum(1., limit / np.maximum(np.linalg.norm(delta, axis=1), 1e-30))[:, None]
        controls[a, b] = (2. * p[:, a] + p[:, b]) / 3. + delta
    edge_mean = sum(controls.values()) / 6.
    center = edge_mean + (edge_mean - p.mean(axis=1)) * .5

    def distance_to_cut(uv):
        result = np.full(len(uv), np.inf)
        for component in np.unique(components):
            indices = np.flatnonzero(components == component)
            segments = np.asarray(plan['components'][int(component)]['boundarySegments'], dtype=float).reshape(-1, 2, 2)
            if not len(segments):
                continue
            edge = segments[:, 1] - segments[:, 0]
            length = np.sum(edge * edge, axis=1)
            chunk = max(1, min(256, 1000000 // len(segments)))
            for start in range(0, len(indices), chunk):
                selected = indices[start:start + chunk]
                relative = uv[selected, None, :] - segments[None, :, 0, :]
                fraction = np.clip(np.sum(relative * edge[None, :, :], axis=2) / np.maximum(length, 1e-30), 0., 1.)
                delta = relative - fraction[:, :, None] * edge[None, :, :]
                result[selected] = np.sqrt(np.min(np.sum(delta * delta, axis=2), axis=1))
        return result

    def surface(weights, include_tuft=True):
        u, v, w = weights.T
        pn = u[:, None] ** 3 * p[:, 0] + v[:, None] ** 3 * p[:, 1] + w[:, None] ** 3 * p[:, 2]
        for (a, b), control in controls.items():
            pn += (3. * weights[:, a] ** 2 * weights[:, b])[:, None] * control
        pn += (6. * u * v * w)[:, None] * center
        uv = np.sum(weights[:, :, None] * q, axis=1)
        ramp = np.clip(distance_to_cut(uv) / (width * .5), 0., 1.)
        envelope = ramp * ramp * (3. - 2. * ramp)
        origin = np.asarray([plan['components'][int(i)]['origin'] for i in components])
        phase = (uv[:, 0] - origin[:, 0]) / width
        height = lift * np.sin(np.pi * phase) ** 2 * envelope
        direction = unit(np.sum(weights[:, :, None] * n, axis=1))
        # PN edges follow geometry adjacency across UV aliases. Only the tuft
        # phase fades at UV cuts; texture charts do not flatten the support.
        support = pn
        return support + height[:, None] * direction if include_tuft else support

    epsilon = max(1e-9, width * 1e-4)
    deltas, support_derivatives = [], []
    for axis in range(2):
        local = inverse[:, :, axis] * epsilon
        delta = np.c_[-local.sum(axis=1), local]
        deltas.append(delta)
        support_derivatives.append((surface(bary + delta, False) - surface(bary - delta, False)) / (2. * epsilon))
    before = unit(np.cross(support_derivatives[0], support_derivatives[1]))
    face_lookup={};owners=np.asarray([face_lookup.setdefault((r['slot'],r['face']),len(face_lookup))for r in records])
    reduced=set();flat=set()
    attempt=-1;after=before.copy();cosine: np.ndarray=np.ones(len(records))
    for attempt in range(14):
        derivatives=[(surface(bary+delta)-surface(bary-delta))/(2.*epsilon)for delta in deltas]
        after=unit(np.cross(derivatives[0],derivatives[1]));cosine=np.sum(before*after,axis=1)
        failed=np.unique(owners[cosine<=0.])
        if not len(failed):break
        assert attempt<13, 'The PN support has no finite differential orientation.'
        reduced.update(map(int,failed));selected=np.isin(owners,failed)
        if attempt==12:lift[selected]=0.;flat.update(map(int,failed))
        else:lift[selected]*=.5
    plan['tuftDerivativeProof']=dict(attempts=attempt+1,maximumAttempts=14,reducedSourceFaces=len(reduced),
        flatSourceFaces=len(flat),sourceFaces=len(face_lookup),minimumCosine=float(cosine.min()),
        pnSupportUnchanged=True,sharedEdgePositionsUnchanged=True)
    points = surface(bary)
    rotation = np.cross(before, after)

    def transport(values):
        return values + np.cross(rotation, values) + np.cross(rotation, np.cross(rotation, values)) / (1. + cosine)[:, None]

    smooth_normal = unit(transport(unit(np.sum(bary[:, :, None] * n, axis=1))))
    smooth_tangent = transport(unit(np.sum(bary[:, :, None] * t, axis=1)))
    smooth_tangent = unit(smooth_tangent - smooth_normal * np.sum(smooth_tangent * smooth_normal, axis=1)[:, None])
    assert all(np.isfinite(v).all() for v in (points, smooth_normal, smooth_tangent))
    return points, smooth_normal, smooth_tangent


def transport_curved_core(points, normals, tangents, records, source_p, source_n, source_t, source_b, amplitude=None):
    """Use the same triangle-interpolated frame as NativeSurfacePlan at runtime."""
    if not records:
        empty = np.empty((len(source_p), 0, 3))
        return empty.copy(), empty.copy(), empty.copy()
    geometry=np.asarray([r.get('geometrySurface')is not None for r in records],dtype=bool)
    surfaces=[r.get('geometrySurface')or r for r in records]
    triangles = np.asarray([r['triangle'] for r in surfaces], dtype=int)
    bary = np.asarray([r['barycentric'] for r in surfaces], dtype='<f4').astype(float)
    position = np.sum(source_p[:, triangles] * bary[None, :, :, None], axis=2)
    normal = unit(np.sum(source_n[:, triangles] * bary[None, :, :, None], axis=2))
    tangent = np.sum(source_t[:, triangles] * bary[None, :, :, None], axis=2)
    tangent = unit(tangent - normal * np.sum(normal * tangent, axis=2)[:, :, None])
    hand = np.sign(np.sum(np.cross(source_n[0, triangles[:, 0]], source_t[0, triangles[:, 0]]) * source_b[0, triangles[:, 0]], axis=1))
    bitangent = np.cross(normal, tangent) * hand[None, :, None]
    if geometry.any():
        edges=source_p[:,triangles[geometry]]
        tangent[:,geometry]=unit(edges[:,:,1]-edges[:,:,0])
        normal[:,geometry]=unit(np.cross(edges[:,:,1]-edges[:,:,0],edges[:,:,2]-edges[:,:,0]))
        bitangent[:,geometry]=np.cross(normal[:,geometry],tangent[:,geometry])
    basis = (tangent, bitangent, normal)
    offset = np.asarray(points) - position[0]
    local_p, local_n, local_t = [np.stack([np.sum(value * axis[0], axis=1) for axis in basis], axis=1)
                               for value in (offset, normals, tangents)]
    if amplitude is not None:
        scale = np.asarray(amplitude)[:, None]
        local_p *= scale
        local_n = np.asarray((0., 0., 1.)) + scale * (local_n - (0., 0., 1.))
        local_t = np.asarray((1., 0., 0.)) + scale * (local_t - (1., 0., 0.))
    def transfer(values):
        return sum(axis * values[None, :, i, None] for i, axis in enumerate(basis))
    result_p = position + transfer(local_p)
    result_n = unit(transfer(local_n))
    result_t = transfer(local_t)
    result_t = unit(result_t - result_n * np.sum(result_n * result_t, axis=2)[:, :, None])
    assert all(np.isfinite(v).all() for v in (result_p, result_n, result_t))
    return result_p, result_n, result_t


def source_surface_walk(start, target, flow, positions, normals, uv, neighbors, charts, *, eligible, domain=None, stops=None, surface_domain=False, outcome=None):
    """Carry one physical step through consecutive native faces in order.

    Native UV aliases keep their two provenance samples. A shortest graph
    route to a separately projected endpoint cannot describe this walk: its
    edge intersections may run backwards or collapse into a vertex fan.
    """
    stops=collections.Counter()if stops is None else stops
    current=dict(start);route=[];seen=set();progress=0.
    direction=np.asarray(target,dtype=float)-current['point']
    remaining=float(np.linalg.norm(direction))
    if remaining<=1e-12:return []
    direction/=remaining
    def normalized(value):
        length=float(np.linalg.norm(value))
        return value/length if length>1e-14 else None
    def node(face,bary):
        bary=np.clip(bary,0,1);bary/=bary.sum()
        normal=normalized(bary@normals[face])
        if normal is None:return None
        return dict(point=bary@positions[face],normal=normal,uv=bary@uv[face],triangle=face,bary=bary,chart=charts[face])
    def derivative(face,axis):
        q=positions[face];edges=np.stack((q[1]-q[0],q[2]-q[0]),axis=1)
        normal=normalized(np.cross(edges[:,0],edges[:,1]))
        if normal is None:return None
        axis=normalized(axis-normal*np.dot(normal,axis))
        if axis is None:return None
        gram=edges.T@edges
        if np.linalg.det(gram)<=max(float(np.prod(np.diag(gram))),1e-30)*1e-12:return None
        values=np.linalg.solve(gram,edges.T@axis)
        return np.r_[-values.sum(),values],axis,normal
    def finish(reason):
        if outcome is not None:outcome.update(reason=reason,sourceFace=int(current['triangle']),barycentric=np.asarray(current['bary'],dtype='<f4').tolist(),direction=np.asarray(direction).tolist(),remainingMetres=float(remaining))
        stops['surface-walk-'+reason]+=1
        return route if progress>1e-9 else []
    for _ in range(64):
        face=int(current['triangle']);bary=np.asarray(current['bary'],float)
        frame=derivative(face,direction)
        if frame is None:return finish('degenerate-face-or-direction')
        delta,direction,face_normal=frame
        falling=np.flatnonzero(delta<-1e-10)
        times=np.maximum(0,-bary[falling]/delta[falling])
        exit_time=float(times.min())if len(times)else np.inf
        distance=min(remaining,exit_time)
        following=node(face,bary+delta*distance)
        if following is None:return finish('degenerate-source-normal')
        if not eligible(following['uv'])or(domain is not None and not (domain.surface_hair if surface_domain else domain.hair)([current['uv'],following['uv']])):
            return finish('native-domain-end')
        if distance>1e-10:
            progress+=distance;remaining=max(0.,remaining-distance)
            current=following;route.append(current)
        if remaining<=1e-9:return finish('step-complete')
        crossings=[]
        for other,a,b,oa,ob,continuous in neighbors.get(face,()):
            opposite=3-a-b
            if abs(following['bary'][opposite])>1e-7 or delta[opposite]>=-1e-10:continue
            mapped=np.zeros(3);mapped[oa]=following['bary'][a];mapped[ob]=following['bary'][b]
            incoming=node(other,mapped)
            normal=normalized(np.cross(positions[other,1]-positions[other,0],positions[other,2]-positions[other,0]))
            hinge=normalized(positions[face,b]-positions[face,a])
            if incoming is None or normal is None or hinge is None:continue
            angle=np.arctan2(np.dot(hinge,np.cross(face_normal,normal)),np.dot(face_normal,normal))
            transported=direction*np.cos(angle)+np.cross(hinge,direction)*np.sin(angle)+hinge*np.dot(hinge,direction)*(1-np.cos(angle))
            guide=flow(incoming['point'],incoming['normal'],transported,other)
            if guide is None:continue
            next_frame=derivative(other,guide)
            if next_frame is None:continue
            next_delta,next_direction,_=next_frame
            if next_delta[3-oa-ob]<=1e-10 or np.any(next_delta[mapped<1e-8]<-1e-8):continue
            if np.dot(next_direction,transported)<.35:continue
            if not eligible(incoming['uv'])or(domain is not None and not (domain.surface_hair if surface_domain else domain.hair)([incoming['uv']])):continue
            crossings.append((other,continuous,incoming,next_direction))
        if len(crossings)!=1:return finish('boundary-or-ambiguous-forward-edge')
        other,continuous,incoming,direction=crossings[0]
        key=face,other,tuple(np.round(incoming['bary'],9))
        if key in seen:return finish('zero-progress-fan-cycle')
        seen.add(key)
        if not continuous and(not route or np.linalg.norm(route[-1]['point']-following['point'])>1e-10):route.append(following)
        route.append(incoming);current=incoming
    return finish('bounded-crossing-end')


def source_contact_vertices(native,renderer_to_prefab,heads,margin):
    """Keep conservative protection separate from confirmed triangle contacts."""
    if not heads or not np.isfinite(margin)or margin<=0:raise ValueError('Groom contact protection needs compatible source heads and a physical margin.')
    prepared=[]
    for head in heads:
        pin=head['packet']
        if sha(pin['path'])!=pin['sha256']:raise ValueError('A groom contact head source changed.')
        packet=read(pin['path'])
        if fingerprint(packet)[0]!=head['sourceFingerprint']:raise ValueError('A groom contact head has another native geometry identity.')
        matrix=np.asarray(head['rendererToPrefab']).reshape(4,4).T
        base=np.asarray(packet['vertices']).reshape(-1,3)
        triangles=np.concatenate([np.asarray(flat,int).reshape(-1,3)for flat in packet['subMeshes']])
        spheres=[]
        for frame in [None]+[frame for shape in packet['shapes']for frame in shape['frames']]:
            points=base if frame is None else base+np.asarray(frame['vertices']).reshape(-1,3)
            points=points@matrix[:3,:3].T+matrix[:3,3];centers=points[triangles].mean(axis=1)
            spheres.append((centers,np.linalg.norm(points[triangles]-centers[:,None,:],axis=2).max(axis=1),points[triangles]))
        prepared.append((head['identity'].casefold(),spheres))
    points=native_frames(native,renderer_to_prefab)[0]
    names=[None]+[shape['name'].casefold()for shape in native['shapes']for frame in shape['frames']]
    protected=np.zeros(native['vertexCount'],bool);confirmed=np.zeros(native['vertexCount'],bool);checks=0;queries=0;unresolved=[]
    for name,positions in zip(names,points):
        selected=[spheres for identity,spheres in prepared if name==identity]
        if not selected:
            selected=[spheres for _,spheres in prepared]
            if name is not None:unresolved.append(name)
        for spheres in selected:
            for centers,radii,triangles in spheres:
                checks+=1
                for first in range(0,len(positions),128):
                    distance=np.linalg.norm(positions[first:first+128,None,:]-centers[None,:,:],axis=2)-radii
                    candidates=distance<=margin;near=candidates.any(axis=1)
                    protected[first:first+128]|=near
                    for local in np.flatnonzero(near&~confirmed[first:first+128]):
                        index=first+int(local);queries+=1
                        _,_,separation=closest_triangle(positions[index],triangles[candidates[local]])
                        if separation<=margin:confirmed[index]=True
    return protected,dict(compatibleHeads=len(heads),storedGroomFrames=len(points)-1,testedHeadPosePairs=checks,
        protectedVertices=int(protected.sum()),contactMarginMetres=margin,unresolvedFittings=unresolved,
        distanceOwner='conservative native triangle bounding spheres',allStoredHeadFramesIncluded=True,
        confirmedContactVertexIds=np.flatnonzero(confirmed).tolist(),confirmedContactVertices=int(confirmed.sum()),
        contactDistanceOwner='closest points on the declared native Head triangles',narrowPhaseQueries=queries)


def source_curve_paths(center,length,trace,first_sign=-1):
    """Give unused travel at a closed end to the opposite continuous flow."""
    if first_sign not in(-1,1)or not np.isfinite(length)or length<=0:
        raise ValueError('A source curve needs positive physical length and its first flow direction.')
    first=trace(first_sign,length*.5)
    points=np.asarray([center['point']]+[node['point']for node in first])
    completed=float(np.linalg.norm(np.diff(points,axis=0),axis=1).sum())
    if not np.isfinite(completed)or completed>length*.5+max(1.,length)*64*np.finfo(float).eps:
        raise ValueError('A source curve trace exceeded its physical travel budget.')
    remaining=max(0.,length-completed)
    second=trace(-first_sign,remaining)
    return {first_sign:first,-first_sign:second},dict(redistributedMetres=max(0.,length*.5-completed),
        requestedLengthMetres=float(length),firstDirection=first_sign,firstCompletedMetres=completed,secondSpanMetres=remaining)


def source_terminal_domain(native,renderer_to_prefab,materials,protected,settings,*,contact_vertices,fixed_vertices=()):
    """Resolve contact-seeded distances without making protected ornaments roots."""
    points=native_frames(native,renderer_to_prefab)[0][0]
    groups=curved_core_groups(native);count=int(groups.max()+1);graph=[{}for _ in range(count)]
    protected=np.asarray(protected,bool);contacts=np.asarray(contact_vertices)
    if protected.shape!=(native['vertexCount'],)or contacts.ndim!=1 or(contacts.size and not np.issubdtype(contacts.dtype,np.integer)):
        raise ValueError('The source contact domain has another native vertex owner.')
    contacts=contacts.astype(int);fixed=np.asarray(fixed_vertices)
    if fixed.ndim!=1 or(fixed.size and not np.issubdtype(fixed.dtype,np.integer)):raise ValueError('Fixed root seeds lost their native vertex owner.')
    fixed=fixed.astype(int)
    if np.any(fixed<0)or np.any(fixed>=native['vertexCount'])or len(np.unique(fixed))!=len(fixed)or not protected[fixed].all():raise ValueError('A fixed root seed has no protected native selection owner.')
    if np.any(contacts<0)or np.any(contacts>=native['vertexCount'])or len(np.unique(contacts))!=len(contacts):
        raise ValueError('The source contact domain has another native vertex owner.')
    if not protected[contacts].all():raise ValueError('A contact distance seed has no protected source contact.')
    held=np.zeros(count,bool);held[groups[protected]]=True;ornaments=np.zeros(count,bool);eligible=np.zeros(native['vertexCount'],bool)
    uv=np.asarray(next(channel['values']for channel in native['uv']if channel['channel']==0)).reshape(-1,4)[:,:2]
    for material,flat in zip(materials,native['subMeshes']):
        domain=SourceHairDomain(material)if material['shader']=='Custom/FakeAnisoHair'else None
        for tri in np.asarray(flat,int).reshape(-1,3):
            if domain is None or not domain.surface_hair(uv[tri]):ornaments[groups[tri]]=True;held[groups[tri]]=True
            else:eligible[tri]=True
            # Preserved nonhair faces can carry an existing native attachment
            # path. They remain held and never create a new distance origin.
            for a,b in zip(tri,np.roll(tri,-1)):
                first,last=int(groups[a]),int(groups[b])
                if first==last:continue
                length=float(np.linalg.norm(points[a]-points[b]))
                graph[first][last]=min(graph[first].get(last,np.inf),length);graph[last][first]=graph[first][last]
    def distances(seeds):
        values=np.full(count,np.inf);queue=[]
        for index in seeds:values[index]=0.;heapq.heappush(queue,(0.,int(index)))
        while queue:
            value,index=heapq.heappop(queue)
            if value!=values[index]:continue
            for following,length in graph[index].items():
                proposed=value+length
                if proposed<values[following]:values[following]=proposed;heapq.heappush(queue,(proposed,following))
        return values
    seeds=np.zeros(count,bool);seeds[groups[contacts]]=True;qualified=np.zeros(count,bool);qualified[groups[eligible]]=True
    seeds&=qualified&~ornaments
    root=distances(np.flatnonzero(seeds))
    fixed_seeds=np.zeros(count,bool)
    fixed_groups=np.unique(groups[fixed]);unresolved_fixed=fixed_groups[~np.isfinite(root[fixed_groups])]
    fixed_seeds[unresolved_fixed]=True;fixed_seeds&=qualified&~ornaments
    seeds|=fixed_seeds
    if fixed_seeds.any():root=distances(np.flatnonzero(seeds))
    held[~np.isfinite(root)]=True
    tips=[i for i in np.unique(groups[eligible])if not held[i]and graph[i]and root[i]>=settings['minimumRootDistanceMetres']
          and all(root[i]>=root[j]-1e-10 for j in graph[i])]
    terminal=distances(tips)
    unresolved=set(map(int,np.flatnonzero(~np.isfinite(root))));components=[]
    while unresolved:
        component={unresolved.pop()};queue=list(component)
        while queue:
            for following in graph[queue.pop()]:
                if following in unresolved:unresolved.remove(following);component.add(following);queue.append(following)
        ids=np.flatnonzero(np.isin(groups,list(component)));hair=ids[eligible[ids]]
        components.append(dict(groups=len(component),sourceVertices=ids.tolist(),qualifiedHairVertices=len(hair),
            protectedContactVertices=int(np.sum(protected[ids])),boundsMetres=[points[ids].min(axis=0).tolist(),points[ids].max(axis=0).tolist()],
            owner='rootless-qualified-hair'if len(hair)else'non-hair-or-mixed-native-footprint'))
    return dict(rootDistance=root[groups],terminalDistance=terminal[groups],protected=held[groups],groups=groups,
        tipGroups=list(map(int,tips)),eligible=eligible,unknownGroups=int(np.sum(~np.isfinite(root))),unresolvedComponents=components,
        contactRootGroups=np.intersect1d(np.flatnonzero(seeds),np.unique(groups[contacts])).tolist(),distanceBasis='confirmed native contacts; resolved native Fixed anchors only for rootless components; local maxima are terminal candidates'if len(fixed)else'confirmed native triangle contacts; local maxima are terminal candidates',
        fixedRootGroups=np.flatnonzero(fixed_seeds).tolist())


def source_root_potential(barycentric,values):
    """Leave an unresolved surface outside the ordered hair domain."""
    values=np.asarray(values,float)
    if not np.isfinite(values).all():return None
    result=float(np.asarray(barycentric,float)@values)
    return result if np.isfinite(result)else None



def transfer_terminal_support_field(native,current,matrix,source_delta,materials,protected,settings):
    """Transfer a native surface field to its authored render support and all fits."""
    validate_retained_groom_source(native,current)
    maximum_offset=settings['maximumSupportOffsetMetres']
    count=native['vertexCount'];transform=np.asarray(matrix).reshape(4,4).T;linear=transform[:3,:3];renderer_inverse=np.linalg.inv(linear)
    base=np.asarray(current['vertices'],dtype='<f4').reshape(-1,3).astype(float)
    local=np.asarray([base]+[base+np.asarray(frame['vertices'],dtype='<f4').reshape(-1,3)for shape in current['shapes']for frame in shape['frames']])
    before=local@linear.T+transform[:3,3]
    source_delta=np.asarray(source_delta,float);protected=np.asarray(protected,bool)
    if source_delta.shape!=(len(local),count,3)or not np.isfinite(source_delta).all()or protected.shape!=(count,):
        raise ValueError('A terminal field has another native source or fitting domain.')
    if len(materials)!=len(native['subMeshes'])or len(current['subMeshes'])<len(native['subMeshes']):
        raise ValueError('A terminal field has another source material domain.')
    source_uv=np.asarray(next(u['values']for u in native['uv']if u['channel']==0)).reshape(-1,4)[:,:2]
    target_uv=np.asarray(next(u['values']for u in current['uv']if u['channel']==0)).reshape(-1,4)[:,:2]
    roles=np.asarray(next(u['values']for u in current['uv']if u['channel']==2)).reshape(-1,4)[:,2]
    all_tri=np.concatenate([np.asarray(v,int).reshape(-1,3)for v in current['subMeshes']]);support_tri=all_tri[np.all(roles[all_tri]<16.5,axis=1)]
    referenced=set(map(int,np.unique(support_tri)))
    groups=curved_core_groups(current);group_count=int(groups.max()+1);delta=np.zeros((len(before),group_count,3));group_protected=np.zeros(group_count,bool)
    mappings={};stats=collections.Counter()
    for slot,(flat,retained)in enumerate(zip(native['subMeshes'],current['subMeshes'])):
        material=materials[slot]
        if material['shader']!='Custom/FakeAnisoHair':
            group_protected[groups[np.unique(np.asarray(retained,int))]]=True;continue
        domain=SourceHairDomain(material);tri=np.asarray(flat,int).reshape(-1,3);q=source_uv[tri]
        edge=np.stack((q[:,1]-q[:,0],q[:,2]-q[:,0]),axis=2);valid=abs(np.linalg.det(edge))>1e-12
        tri=tri[valid];q=q[valid];inverse=np.linalg.inv(edge[valid]);low=q.min(axis=1);high=q.max(axis=1)
        retained_tri=np.asarray(retained,int).reshape(-1,3)
        for face in retained_tri[np.all(roles[retained_tri]<16.5,axis=1)]:
            if not domain.hair(target_uv[face]):group_protected[groups[face]]=True
        vertices=sorted(referenced.intersection(map(int,np.unique(retained_tri))))
        for i in vertices:
            group=int(groups[i])
            if i<count:
                if protected[i]:group_protected[group]=True
                proposed=source_delta[:,i];mapping=dict(nativeAnchor=i)
            else:
                point=target_uv[i]
                candidates=np.flatnonzero(np.all((point>=low-1e-7)&(point<=high+1e-7),axis=1))
                if not len(candidates):stats['unresolvedUv']+=1;group_protected[group]=True;continue
                values=np.einsum('nij,nj->ni',inverse[candidates],point-q[candidates,0])
                bary=np.c_[1-values.sum(axis=1),values]
                inside=np.all(bary>=-2e-6,axis=1)&np.all(bary<=1+2e-6,axis=1)
                candidates=candidates[inside];bary=bary[inside]
                if not len(candidates):stats['unresolvedUv']+=1;group_protected[group]=True;continue
                bary=np.clip(bary,0,1);bary/=bary.sum(axis=1)[:,None]
                # Retained prefix includes legitimate later crown fitting. Use it for
                # geometric disambiguation, while the native triangle/UV identity stays immutable.
                sampled=np.einsum('ni,nij->nj',bary,before[0,tri[candidates]])
                error=np.linalg.norm(sampled-before[0,i],axis=1);order=np.argsort(error);best=int(order[0])
                if error[best]>maximum_offset:stats['unresolvedPhysicalOffset']+=1;group_protected[group]=True;continue
                close=order[error[order]<=error[best]+1e-7]
                best_tri=tri[candidates[best]];best_bary=bary[best]
                sampled_delta=np.einsum('ni,pnij->pnj',bary,source_delta[:,tri[candidates]])
                if len(close)>1 and np.max(np.linalg.norm(sampled_delta[:,close]-sampled_delta[:,best,None,:],axis=2))>1e-7:
                    stats['ambiguousPhysicalOwner']+=1;group_protected[group]=True;continue
                if np.any(protected[best_tri]&(best_bary>1e-6)):group_protected[group]=True
                proposed=sampled_delta[:,best];mapping=dict(triangle=best_tri.tolist(),barycentric=best_bary.tolist(),physicalOffsetMetres=float(error[best]))
            if group in mappings and np.max(np.linalg.norm(delta[:,group]-proposed,axis=1))>1e-7:
                group_protected[group]=True;stats['inconsistentAliasField']+=1;continue
            mappings[group]=mapping;delta[:,group]=proposed
    delta[:,group_protected]=0.
    scales=np.ones(group_count);original=before[:,support_tri]
    original_cross=np.cross(original[:,:,1]-original[:,:,0],original[:,:,2]-original[:,:,0]);original_area=np.linalg.norm(original_cross,axis=2)
    for step in range(16):
        local_delta=(delta[:,groups]*scales[groups][None,:,None])@renderer_inverse.T
        authored=local+local_delta
        stored=authored[0].astype('<f4').astype(float)[None]+(authored-authored[0]).astype('<f4').astype(float)
        after=stored@linear.T+transform[:3,3];q=after[:,support_tri]
        cross=np.cross(q[:,:,1]-q[:,:,0],q[:,:,2]-q[:,:,0]);area=np.linalg.norm(cross,axis=2)
        affected=np.any(np.any(np.abs(stored-local)>0,axis=(0,2))[support_tri],axis=1)
        bad=affected&np.any((np.sum(cross*original_cross,axis=2)<=0)|(area<original_area*settings['minimumRetainedAreaFraction']),axis=0)
        if not bad.any():break
        bad_groups=np.unique(groups[support_tri[bad]]);changed=bad_groups[np.linalg.norm(delta[:,bad_groups],axis=(0,2))>1e-12]
        scales[changed]=0. if step>=12 else scales[changed]*.5
    else:raise AssertionError('The actual retained carrier could not preserve all stored face orientations.')
    moved=np.linalg.norm(after-before,axis=2);modified=np.flatnonzero(moved.max(axis=0)>1e-8)
    if np.any(local_delta[:,group_protected[groups]]!=0.):raise AssertionError('A protected actual carrier group changed.')
    return dict(renderedSupportTriangles=len(support_tri),renderedSupportVertices=len(referenced),modifiedRenderedSupportVertices=len(modified),
        actualRenderVerticesBeyondTwoMillimetres=int(np.sum(moved[0]>.002)),maximumRenderedRestDisplacementMetres=float(moved[0].max()),
        maximumRenderedStoredDisplacementMetres=float(moved.max()),actualCarrierAllFramesOrientationAndAreaRetained=True,
        protectedActualCarrierVerticesExact=True,sourceReferenceExact=True,sourceMappingRejections=dict(stats),affectedTriangles=int(affected.sum()),serializedBinary32FramesChecked=True,
        retainedSubdividedGeometryActuallyReached=bool(np.any(modified>=count)),lineSearchIterations=step),authored


def source_terminal_field(native,flow_packet,matrix,materials,terminal,settings):
    """Keep a compact native surface map for the owned longitudinal retreat."""
    p,n,_,_=native_frames(native,matrix);groups=terminal['groups'];count=int(groups.max()+1)
    references=np.asarray([np.flatnonzero(groups==i)[0]for i in range(count)])
    root=terminal['rootDistance'];distance=terminal['terminalDistance'];held=terminal['protected']
    amount=np.minimum(settings['maximumRetreatMetres'],root*settings['maximumRetreatRootFraction'])*np.maximum(0.,1-distance/settings['terminalRegionLengthMetres'])**2
    amount[held]=0.;amount[~np.isfinite(amount)]=0.
    uv=np.asarray(next(u['values']for u in native['uv']if u['channel']==0)).reshape(-1,4)[:,:2]
    field=np.asarray(next(u['values']for u in flow_packet['uv']if u['channel']==2)).reshape(-1,4)[:,:2]
    triangles=np.concatenate([np.asarray(flat,int).reshape(-1,3)for flat in native['subMeshes']])
    slots=np.concatenate([np.full(len(flat)//3,slot,int)for slot,flat in enumerate(native['subMeshes'])])
    positions=p[0,triangles];normals=n[0,triangles];coordinates=uv[triangles]
    domains=[SourceHairDomain(material)if material['shader']=='Custom/FakeAnisoHair'else None for material in materials]
    edge_uv=np.stack((field[triangles[:,1]]-field[triangles[:,0]],field[triangles[:,2]]-field[triangles[:,0]]),axis=2)
    valid=abs(np.linalg.det(edge_uv))>1e-12;flows=np.zeros((len(triangles),3))
    flows[valid]=(np.stack((positions[valid,1]-positions[valid,0],positions[valid,2]-positions[valid,0]),axis=2)@np.linalg.inv(edge_uv[valid]))[:,:,1]
    flows=unit(flows);edges=collections.defaultdict(list);incident=collections.defaultdict(list);neighbors=collections.defaultdict(list)
    for index,tri in enumerate(triangles):
        for local,anchor in enumerate(tri):incident[int(groups[anchor])].append((index,local))
        for a,b in((0,1),(1,2),(2,0)):edges[(int(slots[index]),*sorted((int(groups[tri[a]]),int(groups[tri[b]]))))].append((index,a,b))
    for owners in edges.values():
        for index,a,b in owners:
            for other,oa,ob in owners:
                if other==index:continue
                if groups[triangles[index,a]]!=groups[triangles[other,oa]]:oa,ob=ob,oa
                continuous=np.max(np.abs(coordinates[index,[a,b]]-coordinates[other,[oa,ob]]))<=PROFILE['nativeUvSeamTolerance']
                neighbors[index].append((other,a,b,oa,ob,continuous))
    def flow(point,normal,hint,index):
        if not valid[index]:return None
        value=flows[index]-normal*np.dot(flows[index],normal)
        if np.linalg.norm(value)<1e-12:return None
        value=unit(value);return -value if np.dot(value,hint)<0 else value
    target=p[:,references].copy();samples={};stops=collections.Counter()
    for group,anchor in enumerate(references):
        if amount[anchor]<=1e-7:continue
        candidates=[]
        for face,local in incident[group]:
            domain=domains[int(slots[face])]
            if domain is None or not domain.hair(coordinates[face]):continue
            if not np.isfinite(root[triangles[face]]).all():stops['unresolved-source-root-domain']+=1;continue
            bary=np.zeros(3);bary[local]=1.
            start=dict(point=positions[face,local],normal=normals[face,local],uv=coordinates[face,local],triangle=face,bary=bary,chart=face)
            gradient=np.linalg.lstsq(positions[face,1:]-positions[face,0],root[triangles[face]][1:]-root[triangles[face,0]],rcond=None)[0]
            axis=flow(start['point'],start['normal'],-gradient,face)
            if axis is None:continue
            route=source_surface_walk(start,start['point']+axis*amount[anchor],flow,positions,normals,coordinates,neighbors,np.arange(len(triangles)),eligible=lambda value:domain.hair([value]),domain=domain,stops=stops)
            if not route:continue
            end=route[-1];corners=triangles[end['triangle']];weights=end['bary']
            potential=source_root_potential(weights,root[corners]);length=sum(np.linalg.norm(b['point']-a['point'])for a,b in zip([start]+route,route))
            if potential is None:stops['unresolved-source-root-domain']+=1;continue
            if potential>=root[anchor]-amount[anchor]*.15 or length<amount[anchor]*.5:continue
            candidates.append((root[anchor]-potential,corners,weights))
        if candidates:
            _,corners,weights=max(candidates,key=lambda value:value[0]);target[:,group]=np.einsum('i,pij->pj',weights,p[:,corners])
            samples[group]=dict(triangle=corners.tolist(),barycentric=weights.tolist())
    delta=target-p[:,references];scales=np.ones(count);old=p[:,triangles]
    original=np.cross(old[:,:,1]-old[:,:,0],old[:,:,2]-old[:,:,0]);area=np.linalg.norm(original,axis=2)
    for step in range(16):
        proposed=p+delta[:,groups]*scales[groups][None,:,None];q=proposed[:,triangles]
        cross=np.cross(q[:,:,1]-q[:,:,0],q[:,:,2]-q[:,:,0]);changed=np.any(np.any(proposed!=p,axis=(0,2))[triangles],axis=1)
        bad=changed&np.any((np.sum(cross*original,axis=2)<=0)|(np.linalg.norm(cross,axis=2)<area*settings['minimumRetainedAreaFraction']),axis=0)
        if not bad.any():break
        affected=np.unique(groups[triangles[bad]]);affected=affected[np.linalg.norm(delta[:,affected],axis=(0,2))>1e-12]
        scales[affected]=0. if step>=12 else scales[affected]*.5
    else:raise ValueError('The source terminal field could not preserve stored face orientation.')
    records=[dict(anchors=np.flatnonzero(groups==group).tolist(),scale=float(scales[group]),**sample)for group,sample in samples.items()if scales[group]>0]
    return dict(kind='SourceGroomTerminalField',schemaVersion=1,nativeFingerprint=native['sourceFingerprint'],rendererToPrefab=matrix,
        vertices=records,protectedVertexIds=np.flatnonzero(held).tolist(),
        settings=copy.deepcopy(settings),nativeReferenceUnchanged=True,nativeStoredOrientationsRetained=True,traceStops=dict(stops))


def terminal_field_displacements(native,matrix,field):
    if field.get('kind')!='SourceGroomTerminalField'or field.get('schemaVersion')!=1:raise ValueError('Unknown source terminal field.')
    if fingerprint(native)[0]!=field['nativeFingerprint']:raise ValueError('A terminal field belongs to another native reference.')
    if not np.array_equal(np.asarray(matrix,dtype='<f4'),np.asarray(field['rendererToPrefab'],dtype='<f4')):raise ValueError('A terminal field changed its declared renderer frame.')
    p=native_frames(native,matrix)[0];delta=np.zeros_like(p);seen=set()
    groups=curved_core_groups(native);faces={tuple(sorted(tri))for flat in native['subMeshes']for tri in np.asarray(flat,int).reshape(-1,3)}
    for record in field['vertices']:
        anchors=record['anchors'];corners=record['triangle'];weights=np.asarray(record['barycentric'],float);scale=record['scale']
        if not anchors or any(type(i)is not int or not 0<=i<native['vertexCount']or i in seen for i in anchors):raise ValueError('A terminal field has conflicting native owners.')
        if len(corners)!=3 or len(set(corners))!=3 or any(type(i)is not int or not 0<=i<native['vertexCount']for i in corners)or weights.shape!=(3,)or not np.isfinite(weights).all()or np.any(weights<0)or abs(float(weights.sum())-1)>1e-6 or type(scale)not in(int,float)or not np.isfinite(scale)or not 0<scale<=1:
            raise ValueError('A terminal field has invalid surface coordinates.')
        if tuple(sorted(corners))not in faces or set(anchors)!=set(map(int,np.flatnonzero(groups==groups[anchors[0]]))):raise ValueError('A terminal field changed its native physical surface owner.')
        seen.update(anchors);target=np.einsum('i,pij->pj',weights,p[:,corners]);delta[:,anchors]=(target[:,None]-p[:,anchors])*scale
    protected=np.asarray(field['protectedVertexIds'],int)
    if np.any((protected<0)|(protected>=native['vertexCount']))or np.any(delta[:,protected]!=0.):raise ValueError('A terminal field moved a protected native contact.')
    return delta


def authored_terminal_support(native,current,matrix,materials,field):
    """Apply the owned field without replacing its retained sampling reference."""
    settings=validate_terminal_settings(field['settings'])
    delta=terminal_field_displacements(native,matrix,field)
    protected=np.zeros(native['vertexCount'],bool);protected[field['protectedVertexIds']]=True
    proof,positions=transfer_terminal_support_field(native,current,matrix,delta,materials,protected,settings)
    base=np.asarray(current['vertices'],dtype='<f4').reshape(-1,3).astype(float)
    frames=[frame for shape in current['shapes']for frame in shape['frames']]
    original=np.asarray([base]+[base+np.asarray(frame['vertices'],dtype='<f4').reshape(-1,3)for frame in frames])
    changed=np.flatnonzero(np.any(positions!=original,axis=(0,2)));result=copy.deepcopy(current)
    points=np.asarray(result['vertices']).reshape(-1,3);points[changed]=positions[0,changed];result['vertices']=points.ravel().tolist()
    for ordinal,frame in enumerate(frame for shape in result['shapes']for frame in shape['frames']):
        values=np.asarray(frame['vertices']).reshape(-1,3);values[changed]=positions[ordinal+1,changed]-positions[0,changed];frame['vertices']=values.ravel().tolist()
    bounds=np.asarray(current['bounds']);low=np.minimum(bounds[:3]-bounds[3:]*.5,positions.min(axis=(0,1)))
    high=np.maximum(bounds[:3]+bounds[3:]*.5,positions.max(axis=(0,1)));result['bounds']=np.r_[(low+high)*.5,high-low].tolist()
    result['sourceFingerprint']=fingerprint(result)[0]
    proof.update(fieldKind=field['kind'],fieldSchemaVersion=field['schemaVersion'],retainedReferenceFingerprint=current['sourceFingerprint'],
        sourceUvSkinPaletteMaterialSlotsAndAllFittingNamesWeightsExact=True,addedVertices=0)
    return result,proof


def source_fiber_frames(records, source_p, source_n, source_t, source_b):
    """Use the surface binding's interpolated frame for every stored pose."""
    anchors=np.asarray([record['anchor'] for record in records],dtype=int)
    offset=np.asarray([record['offset'] for record in records]).reshape(-1,3)
    normal=np.asarray([record['normal'] for record in records]).reshape(-1,3)
    tangent=np.asarray([record['tangent'] for record in records]).reshape(-1,3)
    basis=(source_t[0,anchors],source_b[0,anchors],source_n[0,anchors])
    def transfer(values):
        return sum(axis*values[:,i,None] for i,axis in enumerate(basis))
    points=source_p[0,anchors]+transfer(offset)
    normals=unit(transfer(normal));tangents=unit(transfer(tangent))
    return transport_curved_core(points,normals,tangents,
        [dict(record['surface'],geometrySurface=record.get('geometrySurface'))for record in records],source_p,source_n,source_t,source_b)


def source_ribbon_frames(records, triangles, source_p, source_n, source_t, source_b):
    """Build ribbon width from the deformed source curve in every stored pose."""
    if len(records)<4 or len(records)%2:
        raise ValueError('A source ribbon requires paired width samples.')
    rest=source_fiber_frames(records,source_p,source_n,source_t,source_b)
    surfaces=[record['surface']for record in records[::2]]
    corners=np.asarray([surface['triangle']for surface in surfaces],int)
    bary=np.asarray([surface['barycentric']for surface in surfaces],dtype='<f4').astype(float)
    normal=unit(np.sum(source_n[:,corners]*bary[None,:,:,None],axis=2))
    # The surface driver transports the complete authored centerline. Projecting
    # it back onto that surface retains only lift and erases tangential bends
    # or free ends, even though their binding already owns every stored pose.
    centers=rest[0].reshape(len(source_p),-1,2,3)
    center=centers.mean(axis=2)
    pairs=centers[0];original_center=center[0]
    previous=[];following=[]
    for index in range(len(surfaces)):
        first=index-1;last=index+1
        while first>=0 and np.linalg.norm(original_center[first]-original_center[index])<1e-8:first-=1
        while last<len(surfaces)and np.linalg.norm(original_center[last]-original_center[index])<1e-8:last+=1
        previous.append(max(first,0));following.append(min(last,len(surfaces)-1))
    along=center[:,following]-center[:,previous]
    along-=normal*np.sum(along*normal,axis=2)[:,:,None]
    if np.any(np.linalg.norm(along,axis=2)<1e-12):return None
    along=unit(along)
    across=np.cross(along,normal)
    if np.any(np.linalg.norm(across[:,0],axis=1)<1e-12):return None
    across[:,0]=unit(across[:,0])
    original_width=pairs[:,1]-pairs[:,0]
    width=np.linalg.norm(original_width,axis=1)*.5
    hand=-1. if np.dot(across[0,0],original_width[0])<0 else 1.
    across[:,0]*=hand
    for index in range(1,len(surfaces)):
        previous=along[:,index-1];following=along[:,index]
        rotation=np.cross(previous,following);cosine=np.sum(previous*following,axis=1)
        if np.any(cosine<=-1.+1e-6):return None
        before=across[:,index-1]
        transported=before+np.cross(rotation,before)+np.cross(rotation,np.cross(rotation,before))/(1.+cosine)[:,None]
        candidate=across[:,index]
        degenerate=np.linalg.norm(candidate,axis=1)<1e-12
        candidate[degenerate]=transported[degenerate]
        candidate=unit(candidate)
        candidate*=np.where(np.sum(candidate*transported,axis=1)<0,-1.,1.)[:,None]
        across[:,index]=candidate
    # Native fitting triangles sometimes turn their normal through the
    # centerline. A sheet's two sides cannot swap at that point: transport
    # its width continuously and derive its outward normal from that frame.
    normal=unit(np.cross(across,along))
    rest_normals=rest[1][0].reshape(-1,2,3)
    cosine=np.clip(np.sum(rest_normals.mean(axis=1)*normal[0],axis=1),0,1)
    sine=np.sqrt(np.maximum(1-cosine*cosine,0))
    sides=np.asarray([-1.,1.])
    posed_n=unit(normal[:,:,None,:]*cosine[None,:,None,None]
        +across[:,:,None,:]*(sine[:,None]*sides)[None,:,:,None]).reshape(len(source_p),-1,3)
    posed_t=np.repeat(across,2,axis=1)
    expected=posed_n[:,triangles].mean(axis=2)
    for scale in (1.,.75,.5,.25):
        posed_p=(center[:,:,None,:]+across[:,:,None,:]*(width[:,None]*sides*scale)[None,:,:,None]).reshape(len(source_p),-1,3)
        points=posed_p[:,triangles]
        signed=np.sum(np.cross(points[:,:,1]-points[:,:,0],points[:,:,2]-points[:,:,0])*expected,axis=2)
        if np.any(signed<=1e-14):continue
        anchors=np.asarray([record['anchor']for record in records],int)
        basis=(source_t[0,anchors],source_b[0,anchors],source_n[0,anchors])
        for key,values in (('offset',posed_p[0]-source_p[0,anchors]),('normal',posed_n[0]),('tangent',posed_t[0])):
            local=np.stack([np.sum(values*axis,axis=1)for axis in basis],axis=1)
            for record,value in zip(records,local):record[key]=value.tolist()
        return posed_p,posed_n,posed_t,dict(widthScale=scale,minimumSignedArea=float(signed.min()),
            physicalFrameOwner='deformed-source-centerline',sourceTrianglesAndBarycentricSamplesExact=True)
    return None


def source_ribbon_pieces(records, connections, source_p, source_n, source_t, source_b, *, soft_coverage, preserve_curve_coordinates=False):
    """Split source-chart or deformation discontinuities into tapered sheets."""
    if preserve_curve_coordinates:
        # Texture chart aliases are one physical curve. Frame their complete
        # centerline together, while retaining only the real within-chart
        # triangles and every original native-UV/taper coordinate.
        updated=copy.deepcopy(records)
        triangles=np.asarray([[a*2,a*2+1,b*2]for a,b in connections]+[[a*2+1,b*2+1,b*2]for a,b in connections])
        if not len(triangles):return None
        framed=source_fiber_frames(updated,source_p,source_n,source_t,source_b)
        centers=framed[0][0].reshape(-1,2,3).mean(axis=1)
        if np.linalg.norm(np.diff(centers,axis=0),axis=1).sum()<PROFILE['minimumCurveLengthMetres']:return None
        points=framed[0][:,triangles];expected=framed[1][:,triangles].mean(axis=2)
        signed=np.sum(np.cross(points[:,:,1]-points[:,:,0],points[:,:,2]-points[:,:,0])*expected,axis=2)
        if np.any(signed<=1e-14):
            # An abruptly turning source fitting driver does not own ribbon
            # width. Adapt width/driver to the complete deformed centerline;
            # accept only positive winding in every stored fitting frame.
            adjusted=source_ribbon_frames(updated,triangles,source_p,source_n,source_t,source_b)
            if adjusted is None:return None
            framed=adjusted[:3]
        return updated,connections,framed,1
    def piece(values,depth):
        rest=source_fiber_frames(values,source_p,source_n,source_t,source_b)[0][0].reshape(-1,2,3)
        centers=rest.mean(axis=1)
        distance=np.r_[0.,np.cumsum(np.linalg.norm(np.diff(centers,axis=0),axis=1))]
        if distance[-1]<PROFILE['minimumCurveLengthMetres']:return []
        updated=copy.deepcopy(values)
        v=distance/distance[-1]
        old_v=np.asarray([record['groom'][1]for record in values[::2]])
        rank=abs(values[0]['groom'][3])-1
        power=.55+.35*rank if soft_coverage else .7
        floor=.025 if soft_coverage else .04
        old_taper=floor+(1-floor)*np.maximum(0,np.sin(math.pi*old_v))**power
        taper=floor+(1-floor)*np.maximum(0,np.sin(math.pi*v))**power
        surface=[record['surface']for record in values[::2]]
        corners=np.asarray([row['triangle']for row in surface],int)
        bary=np.asarray([row['barycentric']for row in surface],dtype='<f4').astype(float)
        base=np.sum(source_p[0,corners]*bary[:,:,None],axis=1)
        normal=unit(np.sum(source_n[0,corners]*bary[:,:,None],axis=1))
        lift=np.sum((centers-base)*normal,axis=1)
        if soft_coverage:
            lift=lift/(.12+.88*np.maximum(0,np.sin(math.pi*old_v))**.9)
            centers=base+normal*(lift*(.12+.88*np.maximum(0,np.sin(math.pi*v))**.9))[:,None]
        width=(rest[:,1]-rest[:,0])*(taper/old_taper)[:,None]*.5
        points=np.stack((centers-width,centers+width),axis=1).reshape(-1,3)
        anchors=np.asarray([row['anchor']for row in values],int)
        offsets=points-source_p[0,anchors]
        basis=(source_t[0,anchors],source_b[0,anchors],source_n[0,anchors])
        local=np.stack([np.sum(offsets*axis,axis=1)for axis in basis],axis=1)
        for index,row in enumerate(updated):row['offset']=local[index].tolist();row['groom'][1]=float(v[index//2])
        links=[(index,index+1)for index in range(len(v)-1)]
        triangles=np.asarray([[a*2,a*2+1,b*2]for a,b in links]+[[a*2+1,b*2+1,b*2]for a,b in links])
        framed=source_ribbon_frames(updated,triangles,source_p,source_n,source_t,source_b)
        if framed is not None:return [(updated,links,framed)]
        if depth==4 or len(v)<3:return []
        middle=len(v)//2
        return piece(values[:(middle+1)*2],depth+1)+piece(values[middle*2:],depth+1)
    runs=[]
    for first,last in connections:
        if runs and first==runs[-1][-1]:runs[-1].append(last)
        else:runs.append([first,last])
    pieces=[]
    for run in runs:
        generated=piece([record for index in run for record in records[index*2:index*2+2]],0)
        if generated is None:return None
        pieces.extend(generated)
    if not pieces:return None
    owned=[];links=[];frames=[]
    for values,connections,framed in pieces:
        first=len(owned)//2;owned.extend(values);links.extend((first+a,first+b)for a,b in connections);frames.append(framed)
    return owned,links,tuple(np.concatenate([frame[index]for frame in frames],axis=1)for index in range(3)),len(pieces)


def arched_ribbon_sections(records, connections, frames, source_p, source_n, source_t, source_b, height_fraction):
    """Give a source-bound lock a real cross section in every stored fit."""
    if not 0<height_fraction<=.25 or len(records)%2 or len(records)<4:
        raise ValueError('An arched lock requires paired source samples and a finite section profile.')
    p,n,t=(np.asarray(value,float).reshape(len(source_p),-1,2,3)for value in frames)
    center=p.mean(axis=2);across=unit(p[:,:,1]-p[:,:,0]);along=np.zeros_like(center)
    for first,last in connections:
        direction=center[:,last]-center[:,first];along[:,first]+=direction;along[:,last]+=direction
    # The new owned section follows the actual deformed curve differential.
    # Old corner shading normals can cancel in a stored fit and do not own P.
    normal=unit(np.cross(across,unit(along)))
    width=np.linalg.norm(p[0,:,1]-p[0,:,0],axis=1);height=width*height_fraction
    if np.any(width<=0)or np.any(np.linalg.norm(normal,axis=2)<.5):return None
    posed_p=np.stack((p[:,:,0],center+normal*height[None,:,None],p[:,:,1]),axis=2)
    slope=4*height_fraction
    posed_n=unit(np.stack((normal-across*slope,normal,normal+across*slope),axis=2))
    posed_t=np.repeat(across[:,:,None,:],3,axis=2)
    result=[]
    for index in range(len(records)//2):
        first,last=records[index*2:index*2+2]
        if first['surface']!=last['surface'] or first.get('geometrySurface')!=last.get('geometrySurface'):
            raise ValueError('One lock cross section cannot span unrelated source surfaces.')
        for side,donor in enumerate((first,first,last)):
            row=copy.deepcopy(donor);row['groom'][0]=side*.5;anchor=row['anchor']
            basis=np.stack((source_t[0,anchor],source_b[0,anchor],source_n[0,anchor]))
            row.update(offset=(basis@(posed_p[0,index,side]-source_p[0,anchor])).tolist(),
                normal=(basis@posed_n[0,index,side]).tolist(),tangent=(basis@posed_t[0,index,side]).tolist())
            result.append(row)
    triangles=np.asarray([[a*3+side,a*3+side+1,b*3+side]for a,b in connections for side in range(2)]
        +[[a*3+side+1,b*3+side+1,b*3+side]for a,b in connections for side in range(2)],int)
    values=tuple(value.reshape(len(source_p),-1,3)for value in(posed_p,posed_n,posed_t))
    q=values[0][:,triangles];expected=values[1][:,triangles].mean(axis=2)
    signed=np.sum(np.cross(q[:,:,1]-q[:,:,0],q[:,:,2]-q[:,:,0])*expected,axis=2)
    if not np.isfinite(signed).all()or np.any(signed<=1e-14):return None
    proof=dict(sectionVertices=3,heightFraction=height_fraction,
        minimumSignedArea=float(signed.min()),sourceCenterStencilShared=True,physicalFits=len(source_p),
        sectionFrameOwner='actual deformed source centerline and width')
    return result,values,triangles,proof


def source_ribbon_width(node,across,width,fraction,positions,normals,uv,neighbors,charts,*,eligible,domain=None,return_samples=False,surface_domain=False):
    """Bound a medium lock by its real transverse surface, across UV seams."""
    if not 0<fraction<=1 or width<=0:raise ValueError('A lock width requires its declared source-surface fraction.')
    limits=[];paths=[]
    for sign in(-1,1):
        direction=np.asarray(across)*sign;span=width*.5/fraction
        def cross_flow(point,normal,transported=None,*_):
            # The physical walk already rotated this direction around the
            # crossed edge. Returning to the original world vector reverses
            # a valid transverse step on a strong native fold.
            value=direction if transported is None else np.asarray(transported)
            value=value-normal*np.dot(value,normal)
            return unit(value)if np.linalg.norm(value)>1e-12 else None
        path=source_surface_walk(node,node['point']+direction*span,cross_flow,positions,normals,uv,neighbors,charts,
            eligible=eligible,domain=domain,surface_domain=surface_domain)
        distance=0.;previous=node['point']
        for sample in path:
            distance+=float(np.linalg.norm(sample['point']-previous));previous=sample['point']
        limits.append(distance);paths.append(path)
    resolved=min(width,2*fraction*min(limits))
    if not return_samples:return resolved
    samples=[]
    for path in paths:
        remaining=resolved*.5;previous=node
        for sample in path:
            distance=float(np.linalg.norm(sample['point']-previous['point']))
            if distance>0 and remaining<=distance:
                if previous['triangle']!=sample['triangle']:
                    raise ValueError('A transverse source sample omitted its physical/UV edge crossing.')
                t=remaining/distance;value=copy.deepcopy(previous)
                value.update(point=(1-t)*previous['point']+t*sample['point'],
                    normal=unit((1-t)*previous['normal']+t*sample['normal']),
                    uv=(1-t)*previous['uv']+t*sample['uv'],bary=(1-t)*previous['bary']+t*sample['bary'])
                samples.append(value);break
            remaining-=distance;previous=sample
        else:
            if remaining>1e-10:raise ValueError('A transverse source color escaped its resolved physical width.')
            samples.append(copy.deepcopy(previous))
    return resolved,samples


def bound_fine_attachment(packet, ids, records, points, normals, tangents,
                          source_p, source_n, source_t, source_b, linear, target_poses=None):
    """Clear the curved support with one rigid offset per complete fine sheet."""
    inverse=np.linalg.inv(linear)
    base_p=np.asarray(packet['vertices']).reshape(-1,3)
    base_n=np.asarray(packet['normals']).reshape(-1,3)
    base_t=np.asarray(packet['tangents']).reshape(-1,4)[:,:3]
    frames=[None]+[f for s in packet['shapes']for f in s['frames']]
    original=[np.stack([base if frame is None else base+np.asarray(frame[key]).reshape(-1,3)
                       for frame in frames])for key,base in (('vertices',base_p),('normals',base_n),('tangents',base_t))]
    posed_p=original[0][:,ids]@linear.T
    posed_n=unit(original[1][:,ids]@inverse)
    posed_t=unit(original[2][:,ids]@linear.T)
    if target_poses is None:
        target_poses=transport_curved_core(points,normals,tangents,
            [record['surface']for record in records],source_p,source_n,source_t,source_b)
    uv=np.asarray(next(u['values']for u in packet['uv']if u['channel']==2)).reshape(-1,4)
    lookup={int(index):offset for offset,index in enumerate(ids)}
    groups=collections.defaultdict(set);triangles=[];triangle_keys=[]
    for slot,sub in enumerate(packet['subMeshes']):
        for tri in np.asarray(sub,dtype=int).reshape(-1,3):
            if np.all(uv[tri,2]>=17):
                key=(slot,*uv[tri[0],2:]);groups[key].update(lookup[int(i)]for i in tri)
                triangles.append([lookup[int(i)]for i in tri]);triangle_keys.append(key)
    owned=np.asarray(triangles,dtype=int).reshape(-1,3)
    initial=set();maximum=0.;minimum_clearance=float('inf');unresolved=set();unresolved_rest=set()
    proposed_p=target_poses[0]@inverse.T;proposed_n=unit(target_poses[1]@linear)
    for pose in range(len(frames)):
        if len(owned):
            p=proposed_p[pose,owned];n=proposed_n[pose,owned].mean(axis=1)
            signed=np.sum(np.cross(p[:,1]-p[:,0],p[:,2]-p[:,0])*n,axis=1)
            initial.update(triangle_keys[int(i)]for i in np.flatnonzero(signed<=0))
        for key,members in groups.items():
            selected=np.asarray(sorted(members),dtype=int);normal=posed_n[pose,selected]
            delta=target_poses[0][pose,selected]-posed_p[pose,selected]
            required=np.sum(delta*normal,axis=1)+.0002
            offset=np.zeros(3);limit=2*float(np.linalg.norm(delta,axis=1).max())+.002
            for step in range(64):
                violation=required-normal@offset;worst=int(violation.argmax())
                if float(violation[worst])<=1e-5:break
                offset+=normal[worst]*violation[worst]
                if float(np.linalg.norm(offset))>limit:break
            if float(np.max(required-normal@offset))>1e-5 or float(np.linalg.norm(offset))>limit:
                unresolved.add((pose,key))
                if pose==0:unresolved_rest.add(key)
                continue
            posed_p[pose,selected]+=offset;maximum=max(maximum,float(np.linalg.norm(offset)))
            remaining=np.sum((posed_p[pose,selected]-target_poses[0][pose,selected])*normal,axis=1)
            minimum_clearance=min(minimum_clearance,float(remaining.min()))
    pp=posed_p@inverse.T;nn=original[1][:,ids].copy();tt=original[2][:,ids].copy()
    minimum=float('inf')
    for pose in range(len(frames)):
        p=(pp[0].astype('<f4').astype(float)+(pp[pose]-pp[0]).astype('<f4').astype(float))[owned]
        n=(nn[0].astype('<f4').astype(float)+(nn[pose]-nn[0]).astype('<f4').astype(float))[owned].mean(axis=1)
        signed=np.sum(np.cross(p[:,1]-p[:,0],p[:,2]-p[:,0])*n,axis=1)
        if len(signed):minimum=min(minimum,float(signed.min()))
        assert np.all(signed>0),'Rigid clearance changed a stored fine sheet winding.'
    proof=dict(attempts=1,maximumAttempts=1,initialInvalidSheets=len(initial),finalInvalidSheets=0,
        reducedSheets=len(initial),sourceAttachmentSheets=0,minimumSignedArea=minimum if np.isfinite(minimum)else None,
        allFineGeometryRetained=True,mode='whole-sheet-rigid-clearance',unresolvedSheets=len(unresolved),unresolvedRestSheets=len(unresolved_rest),
        maximumTranslationMetres=maximum,minimumClearanceMetres=minimum_clearance if np.isfinite(minimum_clearance)else None,
        normalsTangentsAndWithinSheetGeometryUnchanged=True)
    return posed_p[0],posed_n[0],posed_t[0],(pp,nn,tt),proof



def bound_curved_core(plan, points, normals, tangents, source_p, source_n, source_t, source_b, inverse):
    """Bound each connected chart using all serialized physical source poses.

    The finite line search changes curvature, never coverage or vertex count.
    A chart uses one amplitude so shared source edges remain shared. A source
    that cannot represent even its zero-offset partition is rejected.
    """
    allocated = [i for i, record in enumerate(plan['vertices']) if 'nativeVertex' not in record]
    records = [plan['vertices'][i] for i in allocated]
    components = np.asarray([r.get('curvatureGroup',r['component']) for r in records], dtype=int)
    native_count = source_p.shape[1]
    mapping = np.empty(len(plan['vertices']), dtype=int)
    for i, record in enumerate(plan['vertices']):
        if 'nativeVertex' in record:
            mapping[i] = record['nativeVertex']
    mapping[allocated] = native_count + np.arange(len(allocated))
    # UV-connected faces can belong to distinct physical curvature groups at
    # coincident/non-manifold edges. Bound the actual parent of each child.
    face_components = {(slot,face):group for slot,groups in enumerate(plan['curvatureGroupsBySlot'])
                       for face,group in enumerate(groups)if group>=0}
    target_triangles, source_triangles, triangle_components = [], [], []
    for slot, triangles in enumerate(plan['triangles']):
        target_triangles.extend(mapping[np.asarray(triangles, dtype=int).reshape(-1, 3)])
        for parent in plan['parents'][slot]:
            component = face_components[slot, parent]
            # Every parent has at least one partition record; reuse its exact
            # native oriented triangle rather than infer one from UVs.
            triangle_components.append(component)
    for slot, parents in enumerate(plan['parents']):
        source_triangles.extend(plan['sourceTrianglesBySlot'][slot][face] for face in parents)
    target_triangles = np.asarray(target_triangles, dtype=int).reshape(-1, 3)
    source_triangles = np.asarray(source_triangles, dtype=int).reshape(-1, 3)
    triangle_components = np.asarray(triangle_components, dtype=int)
    source_local = source_p @ inverse.T
    source_crosses = []
    for pose in source_local:
        p = pose.astype('<f4').astype(float)[source_triangles]
        source_crosses.append(np.cross(p[:, 1] - p[:, 0], p[:, 2] - p[:, 0]))
    amplitudes = np.ones(plan.get('curvatureGroupCount',len(plan['components'])))
    initial_failures = None
    for attempt in range(14):
        pp, nn, tt = transport_curved_core(points, normals, tangents, records, source_p, source_n, source_t, source_b,
                                           amplitudes[components])
        local = pp @ inverse.T
        base = local[0].astype('<f4')
        bad_components = set()
        failed_samples, minimum = 0, float('inf')
        for pose in range(len(source_p)):
            owned = base if pose == 0 else (base + (local[pose] - local[0]).astype('<f4')).astype('<f4')
            vertices = np.vstack((source_local[pose].astype('<f4'), owned)).astype(float)
            p = vertices[target_triangles]
            crosses = np.cross(p[:, 1] - p[:, 0], p[:, 2] - p[:, 0])
            orientation = np.sum(source_crosses[pose] * crosses, axis=1)
            failed = orientation <= 0.
            failed_samples += int(failed.sum())
            minimum = min(minimum, float(orientation.min()))
            bad_components.update(map(int, triangle_components[failed]))
        if initial_failures is None:
            initial_failures = failed_samples
        if not bad_components:
            return pp, nn, tt, dict(attempts=attempt + 1, maximumAttempts=14,
                                    initialFailedSamples=initial_failures, finalFailedSamples=0,
                                    reducedCharts=int(np.sum(amplitudes < 1.)), flatCharts=int(np.sum(amplitudes == 0.)),
                                    minimumAmplitude=float(amplitudes.min()), minimumSerializedOrientation=minimum,
                                    allStoredPosesAndBinary32ChannelsChecked=True)
        assert attempt < 13, 'Even a zero-offset core chart cannot preserve its serialized source winding.'
        selected = np.asarray(sorted(bad_components), dtype=int)
        amplitudes[selected] = 0. if attempt == 12 else amplitudes[selected] * .5
    raise AssertionError('Unreachable curved core bound.')


def fine_core_owners(plan):
    """Cache the source-to-child partition independently of physical poses."""
    owners = {}
    for slot, (children, parents) in enumerate(zip(plan['triangles'], plan['parents'])):
        source = np.asarray(plan['sourceTrianglesBySlot'][slot], dtype=int)
        grouped = collections.defaultdict(list)
        for child, parent in zip(children, parents):
            grouped[int(parent)].append(child)
        for parent, children in grouped.items():
            triangle = source[parent]
            children = np.asarray(children, dtype=int)
            corners = []
            for child in children:
                row = []
                for index in child:
                    record = plan['vertices'][int(index)]
                    weights = dict(zip(record['triangle'], record['barycentric']))
                    row.append([weights.get(int(i), 0.) for i in triangle])
                corners.append(row)
            corners = np.asarray(corners)
            matrix = np.stack((corners[:, 1, 1:] - corners[:, 0, 1:],
                               corners[:, 2, 1:] - corners[:, 0, 1:]), axis=2)
            owners[slot, tuple(sorted(map(int, triangle)))] = (triangle, children, corners[:, 0, 1:], np.linalg.inv(matrix))
    return owners


def couple_fine_to_core(plan, core_p, core_n, core_t, fine_p, fine_n, fine_t, records,
                        source_p, source_n, source_t, source_b, owners=None, *, mapped_surfaces=None, transport_frame=True, require_complete=True,vertex_ids=None):
    """Keep attached fibers above the actual rendered child surface.

    Source triangle provenance selects the child in barycentric coordinates;
    ornaments and fibers on unmodified source faces retain their own ownership.
    The caller transports the resulting rest attachment through every native
    physical frame using the same triangle basis as the runtime surface plan.
    """
    if owners is None:owners=fine_core_owners(plan)
    selected=None if vertex_ids is None else set(map(int,vertex_ids))
    if selected is not None and any(index<0 or index>=len(records)for index in selected):raise ValueError('An attachment target escaped its owned vertex set.')
    groups = collections.defaultdict(list)
    for index, record in enumerate(records):
        if(selected is None or index in selected)and not record['native'] and record.get('surface'):
            groups[record['slot'], tuple(sorted(record['surface']['triangle']))].append(index)
    moved, positions, normals, tangents = [], [], [], []
    for key, indices in groups.items():
        if key not in owners:
            continue
        triangle, children, origin, inverse = owners[key]
        weights = []
        for index in indices:
            surface = records[index]['surface']
            lookup = dict(zip(surface['triangle'], surface['barycentric']))
            weights.append([lookup[int(i)] for i in triangle])
        weights = np.asarray(weights, dtype='<f4').astype(float)
        # Each retained fiber belongs to one closed child partition. Boundary
        # points may choose either incident child, whose source edge is shared.
        for start in range(0, len(indices), 256):
            ids = np.asarray(indices[start:start+256], dtype=int)
            bary = weights[start:start+256]
            local = np.einsum('cij,pcj->pci', inverse, bary[:, None, 1:] - origin[None])
            child_bary = np.concatenate((1.-local.sum(axis=2, keepdims=True), local), axis=2)
            # Stored attachment barycentrics use binary32. Slender child cells
            # amplify that quantization; bound it in the source coordinates.
            error = 2.*np.finfo(np.float32).eps*np.maximum(1., np.abs(inverse).sum(axis=2))
            tolerance = np.c_[error.sum(axis=1), error]
            inside = np.all(child_bary >= -tolerance[None], axis=2)
            covered=inside.any(axis=1)
            if require_complete:
                assert np.all(covered), ('Attached fiber is outside its source-owned curved partition.',
                    key, ids[~covered].tolist(), child_bary.min(axis=2).max(axis=1)[~covered].tolist())
            if not covered.all():
                ids=ids[covered];bary=bary[covered];inside=inside[covered];child_bary=child_bary[covered]
            if not len(ids):continue
            child = inside.argmax(axis=1)
            cb = child_bary[np.arange(len(ids)), child]
            cb = np.maximum(cb, 0.); cb /= cb.sum(axis=1, keepdims=True)
            corners = children[child]
            if mapped_surfaces is not None:
                for identifier,child_triangle,corner_weights in zip(ids,corners,cb):
                    mapped_surfaces[int(identifier)]=dict(triangle=child_triangle.tolist(),barycentric=corner_weights.tolist())
            old_p = bary @ source_p[triangle]
            old_n = unit(bary @ source_n[triangle])
            old_t = bary @ source_t[triangle]
            old_t = unit(old_t - old_n * np.sum(old_n * old_t, axis=1)[:, None])
            hand = np.sign(np.dot(np.cross(source_n[triangle[0]], source_t[triangle[0]]), source_b[triangle[0]]))
            old_b = np.cross(old_n, old_t) * hand
            new_p = np.sum(core_p[corners] * cb[:, :, None], axis=1)
            new_n = unit(np.sum(core_n[corners] * cb[:, :, None], axis=1))
            new_t = np.sum(core_t[corners] * cb[:, :, None], axis=1)
            new_t = unit(new_t - new_n * np.sum(new_n * new_t, axis=1)[:, None])
            new_b = np.cross(new_n, new_t) * hand
            before = np.stack((old_t, old_b, old_n), axis=2)
            after = np.stack((new_t, new_b, new_n), axis=2)
            rotation = after @ before.transpose(0, 2, 1)
            transfer = lambda values: np.einsum('nij,nj->ni', rotation, values)if transport_frame else values
            moved.extend(ids.tolist())
            positions.append(new_p + transfer(fine_p[ids] - old_p))
            normals.append(unit(transfer(fine_n[ids])))
            tangents.append(unit(transfer(fine_t[ids])))
    if not moved:
        empty = np.empty((0, 3))
        return np.empty(0, dtype=int), empty.copy(), empty.copy(), empty.copy()
    return np.asarray(moved, dtype=int), np.concatenate(positions), np.concatenate(normals), np.concatenate(tangents)


def retained_core_owners(native,current,matrix,materials,maximum_offset):
    """Recover the retained child partition in its native UV/physical domain."""
    validate_retained_groom_source(native,current)
    if not np.isfinite(maximum_offset)or maximum_offset<=0 or len(materials)!=len(native['subMeshes']):
        raise ValueError('Retained groom support requires its material domain and bounded physical offset.')
    old_p,_,_,_=native_frames(current,matrix)
    old_uv=np.asarray(next(u['values']for u in native['uv']if u['channel']==0)).reshape(-1,4)[:,:2]
    new_uv=np.asarray(next(u['values']for u in current['uv']if u['channel']==0)).reshape(-1,4)[:,:2]
    roles=np.asarray(next(u['values']for u in current['uv']if u['channel']==2)).reshape(-1,4)[:,2]
    groups=collections.defaultdict(list);stats=collections.Counter()
    for slot,(source,retained,material)in enumerate(zip(native['subMeshes'],current['subMeshes'],materials)):
        if material['shader']!='Custom/FakeAnisoHair':continue
        parent=np.asarray(source,int).reshape(-1,3);uv=old_uv[parent]
        edge=np.stack((uv[:,1]-uv[:,0],uv[:,2]-uv[:,0]),axis=2)
        valid=np.abs(np.linalg.det(edge))>1e-12;parent=parent[valid];uv=uv[valid]
        inverse=np.linalg.inv(edge[valid]);lo=uv.min(axis=1);hi=uv.max(axis=1)
        child=np.asarray(retained,int).reshape(-1,3);child=child[np.all(roles[child]<.5,axis=1)]
        for triangle in child:
            q=new_uv[triangle]
            candidates=np.flatnonzero(np.all(q.min(axis=0)>=lo-1e-7,axis=1)&np.all(q.max(axis=0)<=hi+1e-7,axis=1))
            if not len(candidates):stats['unresolvedChildUv']+=1;continue
            points=np.einsum('nij,nkj->nki',inverse[candidates],q[None]-uv[candidates,0,None])
            corners=np.concatenate((1-points.sum(axis=2,keepdims=True),points),axis=2)
            # Appended UV0 samples were rounded to binary32 independently of
            # their native parent corners. Recover only parent-boundary values
            # inside that representable half-ULP interval; otherwise a slender
            # retained partition leaves an artificial gap along a native edge.
            wire=q.astype('<f4')
            radius=np.maximum(abs(np.nextafter(wire,np.float32(np.inf)).astype(float)-q),
                abs(np.nextafter(wire,np.float32(-np.inf)).astype(float)-q))*.5
            radius+=np.finfo(float).eps*np.maximum(1.,abs(q))*4
            error=np.einsum('nij,kj->nki',abs(inverse[candidates]),radius)
            error=np.concatenate((error.sum(axis=2,keepdims=True),error),axis=2)
            near_zero=abs(corners)<=error;near_one=abs(corners-1)<=error
            stats['recoveredWireBoundaryCoordinates']+=int((near_zero&(corners!=0)).sum()+(near_one&(corners!=1)).sum())
            corners=np.where(near_zero,0,np.where(near_one,1,corners))
            inside=np.all((corners>=-2e-6)&(corners<=1+2e-6),axis=(1,2))
            candidates=candidates[inside];corners=corners[inside]
            if not len(candidates):stats['unresolvedChildUv']+=1;continue
            corners=np.maximum(corners,0);corners/=corners.sum(axis=2,keepdims=True)
            sampled=np.einsum('nki,pnij->pnkj',corners,old_p[:,parent[candidates]])
            errors=np.linalg.norm(sampled-old_p[:,triangle][:,None],axis=3).max(axis=(0,2))
            order=np.argsort(errors);best=int(order[0])
            if errors[best]>maximum_offset:stats['unresolvedPhysicalOffset']+=1;continue
            equivalent=order[errors[order]<=errors[best]+1e-7]
            if len(equivalent)>1 and np.max(np.linalg.norm(sampled[:,equivalent]-sampled[:,best,None],axis=3))>2e-6:
                stats['ambiguousPhysicalOwner']+=1;continue
            for selected in equivalent:
                key=(slot,tuple(sorted(map(int,parent[candidates[selected]]))))
                groups[key].append((parent[candidates[selected]],triangle,corners[selected]))
            stats['mappedChildTriangles']+=1
    owners={}
    for key,rows in groups.items():
        triangle=rows[0][0];children=[];corners=[]
        for source,child,weights in rows:
            permutation=[list(map(int,source)).index(int(i))for i in triangle]
            children.append(child);corners.append(weights[:,permutation])
        corners=np.asarray(corners);edges=np.stack((corners[:,1,1:]-corners[:,0,1:],corners[:,2,1:]-corners[:,0,1:]),axis=2)
        valid=np.abs(np.linalg.det(edges))>1e-14
        if not np.all(valid):stats['degenerateChildPartitions']+=int((~valid).sum())
        if valid.any():owners[key]=(triangle,np.asarray(children,int)[valid],corners[valid,0,1:],np.linalg.inv(edges[valid]))
    return owners,dict(stats,sourceParentPartitions=len(owners),maximumPhysicalOffsetMetres=float(maximum_offset))


def retained_carrier_face_mask(native,current,matrix,materials,maximum_offset):
    """Own only complete, qualified retained child partitions with valid fits."""
    owners,proof=retained_core_owners(native,current,matrix,materials,maximum_offset)
    positions=native_frames(current,matrix)[0];uv=np.asarray(next(u['values']for u in native['uv']if u['channel']==0)).reshape(-1,4)[:,:2]
    masks=[];reasons=collections.Counter();checked=0
    for slot,flat in enumerate(native['subMeshes']):
        triangles=np.asarray(flat,int).reshape(-1,3);mask=np.zeros(len(triangles),bool)
        domain=SourceHairDomain(materials[slot])if materials[slot]['shader']=='Custom/FakeAnisoHair'else None
        for face,parent in enumerate(triangles):
            key=(slot,tuple(sorted(map(int,parent))))
            if domain is None or not domain.surface_hair(uv[parent]):reasons['non-hair-or-uncertain-material']+=1;continue
            try:source_surface_skin(native,dict(triangle=parent.tolist(),barycentric=[1/3]*3))
            except SurfaceSkinSupportError:reasons['native-skin-support-exceeds-four']+=1;continue
            if key not in owners:reasons['unresolved-source-partition']+=1;continue
            _,children,_,inverse=owners[key];determinants=np.linalg.det(inverse)
            if np.any(determinants<=0)or abs(float(np.sum(.5/determinants))-.5)>1e-5:
                reasons['incomplete-source-partition']+=1;continue
            p=positions[:,parent];q=positions[:,children]
            original=np.cross(p[:,1]-p[:,0],p[:,2]-p[:,0]);child=np.cross(q[:,:,1]-q[:,:,0],q[:,:,2]-q[:,:,0])
            if np.any(np.sum(original[:,None]*child,axis=2)<=0):reasons['invalid-retained-physical-fit']+=1;continue
            mask[face]=True;checked+=len(children)*len(positions)
        masks.append(mask)
    return masks,dict(proof,retainedChildPhysicalSamplesChecked=checked,heldNativeFaces=dict(reasons),
        selectedNativeFaces=sum(int(mask.sum())for mask in masks),existingSourceUnchanged=True)


def carrier_render_faces(surface):
    """Physical source samples do not by themselves replace rendered coverage."""
    source=surface['sourceFacesBySlot'];replacement=surface.get('renderReplacementFacesBySlot')
    if replacement is None:return source
    if not isinstance(replacement,list)or len(replacement)!=len(source):
        raise ValueError('Carrier render replacement omitted a source material owner.')
    for owned,removed in zip(source,replacement):
        if not isinstance(removed,list)or any(type(face)is not int for face in removed)or len(set(removed))!=len(removed)or not set(removed)<=set(owned):
            raise ValueError('Carrier render replacement exceeds its declared source domain.')
    return replacement


def retained_carrier_allocation(native,current,matrix,materials,source_faces,maximum_offset):
    """Reserve all source vertices still used outside the declared carrier faces."""
    masks,domain=retained_carrier_face_mask(native,current,matrix,materials,maximum_offset)
    owners,_=retained_core_owners(native,current,matrix,materials,maximum_offset);replaced=set()
    if len(source_faces)!=len(native['subMeshes']):raise ValueError('Carrier allocation omitted native material owners.')
    for slot,faces in enumerate(source_faces):
        triangles=np.asarray(native['subMeshes'][slot],int).reshape(-1,3)
        if len(set(faces))!=len(faces):raise ValueError('Carrier allocation repeats a native source face.')
        for face in faces:
            if type(face)is not int or not 0<=face<len(triangles)or not masks[slot][face]:
                raise ValueError('Carrier allocation exceeds its verified source domain.')
            key=(slot,tuple(sorted(map(int,triangles[face]))))
            replaced.update((slot,tuple(sorted(map(int,tri))))for tri in owners[key][1])
    roles=np.asarray(next(u['values']for u in current['uv']if u['channel']==2)).reshape(-1,4)[:,2]
    fine=(roles>=17)&(roles<64.5);keep=set(range(native['vertexCount']));kept_indices=0
    for slot,sub in enumerate(current['subMeshes']):
        for tri in np.asarray(sub,int).reshape(-1,3):
            if np.any(fine[tri])and not np.all(fine[tri]):raise ValueError('Carrier allocation mixes source fine and support domains.')
            if not np.all(fine[tri])and(slot,tuple(sorted(map(int,tri))))not in replaced:
                keep.update(map(int,tri));kept_indices+=3
    budget=retained_groom_budget(native,current,fine_only=False);stride=budget['vertexAndPhysicalCostPerVertexBytes']
    vertices=current['vertexCount']-len(keep);indices=sum(map(len,current['subMeshes']))-kept_indices
    index_bytes=2 if current['indexFormat']==0 else 4;logical=vertices*stride+indices*index_bytes
    maximum=min(vertices,65535-len(keep),logical//(stride+12))
    if maximum<=0:raise ValueError('Carrier allocation has no complete physical-frame capacity.')
    budget.update(keptVertices=len(keep),sourceReplaceableVertices=vertices,sourceReplaceableIndices=indices,
        sourceVertexAndPhysicalBytes=vertices*stride,sourceIndexBytes=indices*index_bytes,
        maximumAddedLogicalBytes=logical,maximumAddedVertices=maximum,maximumCurves=maximum//4,
        sourceCarrierFacesBySlot=copy.deepcopy(source_faces),retainedSourceIndexCount=kept_indices)
    return budget,np.asarray(sorted(keep),int),replaced,domain


def fit_retained_carrier(packet,native,binding,current,materials,maximum_offset,surface):
    """Place the new cross-sections on the source's actual fitted child surface."""
    first,count=surface['firstVertex'],surface['vertices'];ids=np.arange(first,first+count)
    if surface.get('nativeFingerprint')!=native['sourceFingerprint']or not surface.get('flowCarrierCells')or first<native['vertexCount']or count<=0 or first+count!=packet['vertexCount']:
        raise ValueError('The fitted carrier lost its proven source partition.')
    if surface['flowCarrierCells'].get('model')=='primary-loft':
        validate_retained_groom_source(native,current)
        proof=validate_primary_core(dict(curvedCoreSurface=surface),packet,binding,native,prefix_source=current)
        return copy.deepcopy(packet),dict(proof,declaredSourceSurfaceRetained=True,retainedSurfaceProjectionApplied=False,
            sourceFittingOwner='retain_source_fitting',storedFits=1+sum(len(shape['frames'])for shape in native['shapes']))
    if surface['flowCarrierCells'].get('model')=='source-quadratic':
        validate_retained_groom_source(native,current)
        proof=validate_curved_core(dict(curvedCoreSurface=surface),packet,binding,native)
        return copy.deepcopy(packet),dict(proof,declaredSourceSurfaceRetained=True,retainedSurfaceProjectionApplied=False,
            sourceFittingOwner='retain_source_fitting',storedFits=1+sum(len(shape['frames'])for shape in native['shapes']))
    owners,_=retained_core_owners(native,current,binding['matrix'],materials,maximum_offset)
    old=native_frames(current,binding['matrix']);before=native_frames(packet,binding['matrix'])
    pp,nn,tt=(value.copy()for value in before[:3]);minimum=float('inf')
    for pose in range(len(pp)):
        moved,p,n,t=couple_fine_to_core(None,old[0][pose],old[1][pose],old[2][pose],
            before[0][pose],before[1][pose],before[2][pose],binding['vertices'],
            before[0][pose],before[1][pose],before[2][pose],before[3][pose],owners,vertex_ids=ids,transport_frame=False)
        if set(map(int,moved))!=set(map(int,ids)):raise ValueError('A reconstructed carrier lost its retained physical source owner.')
        pp[pose,moved]=p;nn[pose,moved]=n;tt[pose,moved]=t
    guides=surface.get('carrierGuideIds');distances=surface.get('carrierTerminalDistanceMetres');region=surface.get('terminalRegionMetres')
    if not isinstance(guides,list)or not isinstance(distances,list)or len(guides)!=count or len(distances)!=count or type(region)not in(int,float)or not np.isfinite(region)or region<=0 or any(type(value)is not int or value<-1 for value in guides)or any(type(value)not in(int,float)or not np.isfinite(value)or value<0 for value in distances):
        raise ValueError('The fitted carrier lost its authored lock and terminal ownership.')
    groups=collections.defaultdict(list)
    for offset,identifier in enumerate(ids):
        row=binding['vertices'][int(identifier)];driver=row.get('geometrySurface')or row['surface'];source=row['surface']
        d=before[0][0,np.asarray(driver['triangle'])];s=before[0][0,np.asarray(source['triangle'])]
        sign=1 if np.dot(np.cross(d[1]-d[0],d[2]-d[0]),np.cross(s[1]-s[0],s[2]-s[0]))>=0 else-1
        key=(tuple(driver['triangle']),tuple(np.asarray(driver['barycentric'],dtype='<f4')),sign,
            guides[offset]if distances[offset]<region else-1)
        groups[key].append(int(identifier))
    for values in groups.values():
        # Source UV cuts do not create independent geometric endpoints.
        pp[:,values]=np.mean(pp[:,values],axis=1)[:,None,:]
    carrier_triangles=np.concatenate([np.asarray(packet['subMeshes'][slot],int).reshape(-1,3)[-amount:]
        for slot,amount in enumerate(surface['triangleCountsBySlot'])if amount])
    uv=np.asarray(next(row['values']for row in packet['uv']if row['channel']==0)).reshape(-1,4)[:,:2]
    q=uv[carrier_triangles];parameter=np.stack((q[:,1]-q[:,0],q[:,2]-q[:,0]),axis=2);det=np.linalg.det(parameter)
    valid_uv=abs(det)>1e-14;fallback=0
    for pose in range(len(pp)):
        p=pp[pose,carrier_triangles];cross=np.cross(p[:,1]-p[:,0],p[:,2]-p[:,0])
        cross*=np.where(np.sum(cross*before[1][pose,carrier_triangles].mean(axis=1),axis=1)>=0,1.,-1.)[:,None]
        summed=np.zeros_like(pp[pose]);summed_t=np.zeros_like(pp[pose])
        for corner in range(3):np.add.at(summed,carrier_triangles[:,corner],cross)
        if np.any(np.linalg.norm(summed[ids],axis=1)<=1e-20):raise ValueError('A fitted carrier has no valid geometric normal.')
        derivative=np.stack((p[valid_uv,1]-p[valid_uv,0],p[valid_uv,2]-p[valid_uv,0]),axis=2)@np.linalg.inv(parameter[valid_uv])
        tangent=derivative[:,:,0]*np.linalg.norm(cross[valid_uv],axis=1)[:,None]
        for corner in range(3):np.add.at(summed_t,carrier_triangles[valid_uv,corner],tangent)
        for values in groups.values():summed[values]=summed[values].sum(axis=0)
        nn[pose,ids]=unit(summed[ids]);target=summed_t[ids]
        missing=np.linalg.norm(target,axis=1)<=1e-20;fallback+=int(missing.sum());target[missing]=before[2][pose,ids[missing]]
        target-=nn[pose,ids]*np.sum(target*nn[pose,ids],axis=1)[:,None]
        if np.any(np.linalg.norm(target,axis=1)<=1e-20):raise ValueError('A fitted carrier has no valid source UV tangent.')
        tt[pose,ids]=unit(target)
    transform=np.asarray(binding['matrix']).reshape(4,4).T;linear=transform[:3,:3];inverse=np.linalg.inv(linear)
    positions=(pp-transform[:3,3])@inverse.T;normals=unit(nn@linear);tangents=unit(tt@inverse.T)
    tangents=unit(tangents-normals*np.sum(tangents*normals,axis=2)[:,:,None]);result=copy.deepcopy(packet)
    for name,values in(('vertices',positions),('normals',normals),('tangents',tangents)):
        base=np.asarray(result[name]).reshape(-1,4 if name=='tangents'else 3);base[ids,:3]=values[0,ids];result[name]=base.ravel().tolist()
        for frame,absolute in zip((f for s in result['shapes']for f in s['frames']),values[1:]):
            delta=np.asarray(frame[name]).reshape(-1,3);delta[ids]=absolute[ids]-values[0,ids];frame[name]=delta.ravel().tolist()
    source_p=native_frames(current,binding['matrix'])[0]
    local_base=np.asarray(result['vertices'],dtype='<f4').reshape(-1,3)
    for pose,frame in enumerate([None]+[f for shape in result['shapes']for f in shape['frames']]):
        stored=local_base if frame is None else(local_base+np.asarray(frame['vertices'],dtype='<f4').reshape(-1,3)).astype('<f4')
        world=stored.astype(float)@linear.T+transform[:3,3]
        for slot,amount in enumerate(surface['triangleCountsBySlot']):
            triangles=np.asarray(result['subMeshes'][slot],int).reshape(-1,3)
            triangles=triangles[-amount:]if amount else np.empty((0,3),int)
            source=np.asarray(native['subMeshes'][slot],int).reshape(-1,3)[surface['parentFacesBySlot'][slot]]
            a=source_p[pose,source];b=world[triangles]
            orientation=np.sum(np.cross(a[:,1]-a[:,0],a[:,2]-a[:,0])*np.cross(b[:,1]-b[:,0],b[:,2]-b[:,0]),axis=1)
            if len(orientation):minimum=min(minimum,float(orientation.min()))
            if np.any(orientation<=0):raise ValueError('A fitted carrier reversed or collapsed a stored physical source face.')
    low=positions.min(axis=(0,1));high=positions.max(axis=(0,1));result['bounds']=np.r_[(low+high)*.5,high-low].tolist()
    result['sourceFingerprint']=fingerprint(result)[0]
    return result,dict(placedCarrierVertices=len(ids),storedFits=len(pp),minimumSerializedOrientation=minimum,
        actualSourceChildBaseUsed=True,sourceDriverPrefixAndProtectedSamplesUnchanged=True,
        finalGeometryNormalsAndSourceUvTangents=True,sourceTangentFallbackSamples=fallback,physicalLockAliases=len(groups))


def couple_retained_groom_support(packet,native,binding,current,materials,maximum_offset,*,carrier_surface=None):
    """Attach new fine locks to the actual retained carrier in every saved fit."""
    binding=copy.deepcopy(binding)
    if carrier_surface is not None and carrier_surface.get('flowCarrierCells',{}).get('model')=='primary-loft':
        packet,proof=fit_retained_carrier(packet,native,binding,current,materials,maximum_offset,carrier_surface)
        provenance=validate_surface_provenance(binding,native)
        expected=sum(not row['native']for row in binding['vertices'][native['vertexCount']:])
        if provenance['explicitSurfaceVertices']!=expected:raise ValueError('A primary/fine surface lost its complete native physical driver.')
        return packet,binding,dict(proof,fineVertices=expected,retainedFineVertices=expected,removedFineVertices=0,
            authoredFinePntAndAllStoredFramesExact=True,retainedRenderSurfaceDefinesPhysicalCoverage=False,
            sourceFittingOwner='retain_source_fitting',maximumFineTranslationMetres=0.,continuousCurvesReframed=False)
    owners,proof=retained_core_owners(native,current,binding['matrix'],materials,maximum_offset)
    carrier_proof=None
    if carrier_surface is not None:
        packet,carrier_proof=fit_retained_carrier(packet,native,binding,current,materials,maximum_offset,carrier_surface)
    if carrier_surface is not None and carrier_surface.get('flowCarrierCells',{}).get('model')=='source-quadratic':
        # This owner already authored both the macro and its continuous fine
        # curves in every source fit. The retained C8 render surface can end
        # before those free lengths; it is not their complete physical driver.
        # Validate the explicit rendered-prefix/macro partition without moving
        # or reframing the curves a second time.
        sources=[np.asarray(sub,int).reshape(-1,3)for sub in native['subMeshes']]
        plan=dict(sourceTrianglesBySlot=sources,triangles=[],parents=[],vertices=[])
        for index,row in enumerate(binding['vertices']):
            plan['vertices'].append(row.get('surface')or dict(triangle=[index],barycentric=[1.]))
        for slot,flat in enumerate(packet['subMeshes']):
            triangles=np.asarray(flat,int).reshape(-1,3);amount=carrier_surface['triangleCountsBySlot'][slot]
            kept=triangles[:-amount]if amount else triangles
            kept=kept[np.all(kept<native['vertexCount'],axis=1)]
            lookup={tuple(sorted(map(int,tri))):face for face,tri in enumerate(sources[slot])}
            parent=[lookup[tuple(sorted(map(int,tri)))]for tri in kept]
            macro=triangles[-amount:]if amount else np.empty((0,3),int)
            plan['triangles'].append(np.concatenate((kept,macro)))
            plan['parents'].append(parent+carrier_surface['parentFacesBySlot'][slot])
        physical=fine_core_owners(plan)
        maximum_area_error=0.
        for parent,children,origin,inverse in physical.values():
            area=float(np.sum(.5/np.linalg.det(inverse)));maximum_area_error=max(maximum_area_error,abs(area-.5))
            if abs(area-.5)>1e-5:raise ValueError('An authored carrier lost its complete native physical partition.')
        frames=native_frames(packet,binding['matrix']);roles=np.asarray(next(u['values']for u in packet['uv']if u['channel']==2)).reshape(-1,4)[:,2]
        expected=set(map(int,np.flatnonzero((roles>=17)&(roles<64.5))))
        moved,_,_,_=couple_fine_to_core(None,frames[0][0],frames[1][0],frames[2][0],
            frames[0][0],frames[1][0],frames[2][0],binding['vertices'],frames[0][0],frames[1][0],frames[2][0],frames[3][0],
            physical,transport_frame=False,vertex_ids=expected)
        if set(map(int,moved))!=expected:raise ValueError('An authored fine curve lost its complete native physical driver.')
        return packet,binding,dict(proof,fineVertices=len(expected),retainedFineVertices=len(expected),removedFineVertices=0,
            storedFits=len(frames[0]),reconstructedCarrier=carrier_proof,physicalParentPartitions=len(physical),
            maximumPhysicalPartitionAreaError=maximum_area_error,authoredFinePntAndAllStoredFramesExact=True,
            retainedRenderSurfaceDefinesPhysicalCoverage=False,sourceFittingOwner='retain_source_fitting',
            maximumFineTranslationMetres=0.,maximumRestFineTranslationMetres=0.,continuousCurvesReframed=False)
    core_p,core_n,core_t,core_b=native_frames(current,binding['matrix'])
    if carrier_surface is not None:
        added=native_frames(packet,binding['matrix']);offset=len(core_p[0]);new_owners,_=retained_core_owners(native,packet,binding['matrix'],materials,maximum_offset)
        for slot,faces in enumerate(carrier_surface['sourceFacesBySlot']):
            source=np.asarray(native['subMeshes'][slot],int).reshape(-1,3)
            for face in faces:
                key=(slot,tuple(sorted(map(int,source[face]))))
                if key not in new_owners:raise ValueError('A reconstructed carrier lost its complete native source domain.')
                parent,children,origin,inverse=new_owners[key];owners[key]=(parent,children+offset,origin,inverse)
        core_p,core_n,core_t,core_b=(np.concatenate((old,new),axis=1)for old,new in zip((core_p,core_n,core_t,core_b),added))
    fine_p,fine_n,fine_t,_=native_frames(packet,binding['matrix'])
    roles=np.asarray(next(u['values']for u in packet['uv']if u['channel']==2)).reshape(-1,4)[:,2]
    expected=set(map(int,np.flatnonzero((roles>=17)&(roles<64.5))))
    target_p,target_n,target_t=fine_p.copy(),fine_n.copy(),fine_t.copy();mapped={}
    moved,points,normals,tangents=couple_fine_to_core(None,core_p[0],core_n[0],core_t[0],
        fine_p[0],fine_n[0],fine_t[0],binding['vertices'],core_p[0],core_n[0],core_t[0],core_b[0],owners,
        mapped_surfaces=mapped,transport_frame=False,vertex_ids=expected)
    if set(map(int,moved))!=expected:
        raise ValueError('A new fine lock has no unambiguous retained render-support owner: '+str(len(expected-set(map(int,moved)))))
    target_p[0,moved]=points;target_n[0,moved]=normals;target_t[0,moved]=tangents
    uv=np.asarray(next(u['values']for u in packet['uv']if u['channel']==2)).reshape(-1,4)
    curves=collections.defaultdict(list);widths=collections.Counter();minimum=float('inf');rejected=set();rejections=collections.Counter()
    for slot,sub in enumerate(packet['subMeshes']):
        for triangle in np.asarray(sub,int).reshape(-1,3):
            if not np.all(roles[triangle]>=17):continue
            key=(slot,*uv[triangle[0],2:])
            curves[key].append(triangle)
    for triangles in curves.values():
        triangles=np.asarray(triangles,int);ids=np.unique(triangles)
        if len(ids)%2 or len(ids)<4:raise ValueError('A retained fine curve requires its paired longitudinal samples.')
        records=[copy.deepcopy(binding['vertices'][int(i)])for i in ids]
        # A continuous curve repeats one physical knot at its source UV seam.
        # Keep that geometric driver shared rather than integrate its tiny UV0
        # quantization discrepancy as a new short, reversing curve segment.
        knots=collections.defaultdict(list)
        for pair in range(len(ids)//2):knots[float(uv[ids[pair*2],1])].append(pair)
        seam_valid=True
        for aliases in knots.values():
            if len(aliases)<2:continue
            first=aliases[0];reference=mapped[int(ids[first*2])]
            sample=lambda surface:np.einsum('i,pij->pj',surface['barycentric'],core_p[:,surface['triangle']])
            expected_point=sample(reference)
            for pair in aliases:
                if np.max(np.linalg.norm(sample(mapped[int(ids[pair*2])])-expected_point,axis=1))>2e-6:
                    seam_valid=False;break
                for side in range(2):
                    identifier=int(ids[pair*2+side]);donor=int(ids[first*2+side])
                    mapped[identifier]=reference
                    target_p[0,identifier]=target_p[0,donor]
                    target_n[0,identifier]=target_n[0,donor];target_t[0,identifier]=target_t[0,donor]
        if not seam_valid:
            rejected.update(map(int,ids));rejections['retained-physical-seam']+=1;continue
        for identifier,record in zip(ids,records):
            anchor=record['anchor'];record['surface']=mapped[int(identifier)];record['geometrySurface']=None
            basis=np.asarray((core_t[0,anchor],core_b[0,anchor],core_n[0,anchor]))
            record.update(offset=(basis@(target_p[0,identifier]-core_p[0,anchor])).tolist(),
                normal=(basis@target_n[0,identifier]).tolist(),tangent=(basis@target_t[0,identifier]).tolist())
        local=np.searchsorted(ids,triangles)
        framed=source_ribbon_frames(records,local,core_p,core_n,core_t,core_b)
        if framed is None:
            rejected.update(map(int,ids));rejections['retained-full-fit-frame']+=1;continue
        pp,nn,tt,facts=framed;target_p[:,ids]=pp;target_n[:,ids]=nn;target_t[:,ids]=tt
        widths[str(facts['widthScale'])]+=1;minimum=min(minimum,facts['minimumSignedArea'])
    transform=np.asarray(binding['matrix']).reshape(4,4).T;linear=transform[:3,:3];inverse=np.linalg.inv(linear)
    positions=(target_p-transform[:3,3])@inverse.T;normals=unit(target_n@linear);tangents=target_t@inverse.T
    tangents=unit(tangents-normals*np.sum(normals*tangents,axis=2)[:,:,None])
    result=copy.deepcopy(packet)
    selected=np.asarray(sorted(expected),int)
    for name,values in(('vertices',positions),('normals',normals),('tangents',tangents)):
        base=np.asarray(result[name]).reshape(-1,4 if name=='tangents'else 3);base[selected,:3]=values[0,selected]
        result[name]=base.ravel().tolist()
        for frame,absolute in zip((f for s in result['shapes']for f in s['frames']),values[1:]):
            delta=np.asarray(frame[name]).reshape(-1,3);delta[selected]=absolute[selected]-values[0,selected]
            frame[name]=delta.ravel().tolist()
    retained=np.asarray([i for i in range(result['vertexCount'])if i not in rejected],int)
    low=np.minimum(positions[:,retained].min(axis=(0,1)),np.asarray(result['bounds'][:3])-np.asarray(result['bounds'][3:])*.5)
    high=np.maximum(positions[:,retained].max(axis=(0,1)),np.asarray(result['bounds'][:3])+np.asarray(result['bounds'][3:])*.5)
    result['bounds']=np.r_[(low+high)*.5,high-low].tolist()
    result['sourceFingerprint']=fingerprint(result)[0]
    if rejected:remove_fine_vertices(result,binding,native['vertexCount'],rejected)
    retained_fine=np.asarray(sorted(expected-rejected),int)
    delta=np.linalg.norm(target_p[:,retained_fine]-fine_p[:,retained_fine],axis=2)
    return result,binding,dict(proof,fineVertices=len(expected),storedFits=len(core_p),reconstructedCarrier=carrier_proof,
        retainedFineVertices=len(retained_fine),
        maximumFineTranslationMetres=float(delta.max())if len(retained_fine)else 0.,
        maximumRestFineTranslationMetres=float(delta[0].max())if len(retained_fine)else 0.,
        nativeSamplingAndCarrierUnchanged=True,
        continuousCurves=len(curves)-sum(rejections.values()),rejectedCurves=dict(rejections),removedFineVertices=len(rejected),
        widthScales=dict(widths),minimumFullFitSignedArea=minimum if np.isfinite(minimum)else None)


def curved_core_groups(native):
    """Geometry identity owns skin and every position frame, independently of UV normals."""
    arrays=[np.asarray(native[k],dtype='<f4').reshape(-1,w)for k,w in (('vertices',3),('weights',4))]
    arrays.append(np.asarray(native['boneIndices'],dtype='<i4').reshape(-1,4))
    frames=[frame for shape in native['shapes']for frame in shape['frames']]
    arrays.extend(np.asarray(frame['vertices'],dtype='<f4').reshape(-1,3)for frame in frames)
    lookup={};groups=[]
    for index in range(native['vertexCount']):
        key=b''.join(value[index].tobytes()for value in arrays)
        groups.append(lookup.setdefault(key,len(lookup)))
    return np.asarray(groups,dtype=int)


def prepare_geometry_frames(plan,positions,drivers=None):
    """Use one well-conditioned native position frame for each physical sample.

    Texture provenance retains its original triangle. An edge or corner shared
    by UV charts instead uses the same geometric driver on both sides.
    """
    physical=np.asarray(plan['physicalGroups'],dtype=int)
    poses=np.asarray(positions);poses=poses[None]if poses.ndim==2 else poses
    if drivers is None:
        drivers={}
        for source in plan['sourceTrianglesBySlot']:
            for row in source:
                tri=np.asarray(row,dtype=int);groups=physical[tri]
                if len(set(map(int,groups)))!=3:continue
                p=poses[:,tri];a,b=p[:,1]-p[:,0],p[:,2]-p[:,0]
                area=np.linalg.norm(np.cross(a,b),axis=1)
                length=np.maximum.reduce((np.linalg.norm(a,axis=1),np.linalg.norm(b,axis=1),np.linalg.norm(p[:,2]-p[:,1],axis=1)))
                if np.any(area<=0)or np.any(area/np.maximum(length*length,1e-30)<=64*np.finfo(np.float32).eps):continue
                quality=float(np.min(area/np.maximum(length,1e-30)))
                for mask in range(1,8):
                    key=tuple(sorted(int(groups[i])for i in range(3)if mask&(1<<i)))
                    if key not in drivers or quality>drivers[key][0]:drivers[key]=(quality,tri.tolist())
    for record in plan['vertices']:
        sample=record.get('surface')or record
        weights=collections.defaultdict(float)
        for vertex,weight in zip(sample['triangle'],sample['barycentric']):
            if weight>1e-10:weights[int(physical[vertex])]+=weight
        driver=drivers[tuple(sorted(weights))][1]
        record['geometrySurface']=dict(triangle=driver,barycentric=[weights.get(int(physical[i]),0.)for i in driver])
    return drivers


def share_core_normals(plan,normals):
    """A texture cut retains its tangent but shares its physical surface normal."""
    groups=collections.defaultdict(list)
    for index,record in enumerate(plan['vertices']):
        surface=record['geometrySurface']
        key=tuple(surface['triangle'])+tuple(np.asarray(surface['barycentric'],dtype='<f4'))
        groups[key].append(index)
    result=normals.copy()
    for indices in groups.values():
        if len(indices)>1:result[indices]=unit(normals[indices].sum(axis=0))
    return result


def validate_retained_groom_source(native,current,*,source_bones=None,target_bones=None):
    """Separate native sampling identity from an already authored fitting."""
    count=native['vertexCount']
    if current['vertexCount']<count:raise ValueError('A retained groom omits native correspondence vertices.')
    if (source_bones is None)!=(target_bones is None):raise ValueError('A retained groom needs both renderer bone palettes.')
    for key,width in(('weights',4),('boneIndices',4)):
        if not np.array_equal(np.asarray(current[key]).reshape(-1,width)[:count],np.asarray(native[key]).reshape(-1,width)):
            raise ValueError('A retained groom changed native skin correspondence: '+key)
    if not np.array_equal(np.asarray(current['bindPoses'],dtype='<f4'),np.asarray(native['bindPoses'],dtype='<f4')):
        raise ValueError('A retained groom changed its native bind palette.')
    if source_bones is not None:
        from mesh_recipe_codec import skin_palette_frame
        frame,mapping,_=skin_palette_frame(current,native,target_bones,source_bones,tolerance=1e-6)
        if mapping!={i:i for i in range(len(source_bones))}or not np.allclose(frame,np.eye(4),rtol=0,atol=1e-6):
            raise ValueError('A retained groom changed the renderer bone order.')
    if bool(current['colors'])!=bool(native['colors'])or(native['colors']and not np.array_equal(
            np.asarray(current['colors'],dtype='<f4').reshape(-1,4)[:count],np.asarray(native['colors'],dtype='<f4').reshape(-1,4))):
        raise ValueError('A retained groom changed native vertex color correspondence.')
    for original in native['uv']:
        channel=next((u for u in current['uv']if u['channel']==original['channel']),None)
        dimension=original['dimension']
        if channel is None or channel['dimension']<dimension or not np.array_equal(
                np.asarray(channel['values'],dtype='<f4').reshape(-1,4)[:count,:dimension],np.asarray(original['values'],dtype='<f4').reshape(-1,4)[:,:dimension]):
            raise ValueError('A retained groom changed native texture correspondence.')
    if len(current['subMeshes'])<len(native['subMeshes']):raise ValueError('A retained groom changed native material slots.')
    if len(current['subMeshes'])>len(native['subMeshes']):
        role_channel=next((u for u in current['uv']if u['channel']==2),None)
        if role_channel is None:raise ValueError('Additional material slots have no owned fine domain.')
        roles=np.asarray(role_channel['values']).reshape(-1,4)[:,2]
        for sub in current['subMeshes'][len(native['subMeshes']):]:
            if sub and not np.all((roles[np.asarray(sub,int)]>=17)&(roles[np.asarray(sub,int)]<64.5)):
                raise ValueError('An additional material slot contains unowned source support.')
    def oriented(tri):
        values=tuple(map(int,tri))
        return min(values,values[1:]+values[:1],values[2:]+values[:2])
    native_faces=0;retained_native_faces=0
    for original,retained in zip(native['subMeshes'],current['subMeshes']):
        triangles=np.asarray(retained,int).reshape(-1,3)
        actual=collections.Counter(oriented(row)for row in triangles[np.all(triangles<count,axis=1)])
        expected=collections.Counter(oriented(row)for row in np.asarray(original,int).reshape(-1,3))
        if actual-expected:raise ValueError('A retained groom changed native face correspondence or orientation.')
        native_faces+=sum(expected.values());retained_native_faces+=sum(actual.values())
    identities=lambda value:[(s['name'],[f['weight']for f in s['frames']])for s in value['shapes']]
    if identities(native)!=identities(current):raise ValueError('A retained groom changed stored fitting identities.')
    maxima={}
    for key,width in(('vertices',3),('normals',3),('tangents',4)):
        old=np.asarray(native[key],dtype='<f4').reshape(-1,width)[:,:3].astype(float)
        new=np.asarray(current[key],dtype='<f4').reshape(-1,width)[:count,:3].astype(float)
        maximum=float(np.linalg.norm(new-old,axis=1).max())
        for source_shape,target_shape in zip(native['shapes'],current['shapes']):
            for source_frame,target_frame in zip(source_shape['frames'],target_shape['frames']):
                a=old+np.asarray(source_frame[key],dtype='<f4').reshape(-1,3)
                b=new+np.asarray(target_frame[key],dtype='<f4').reshape(-1,3)[:count]
                maximum=max(maximum,float(np.linalg.norm(b-a,axis=1).max()))
        if not np.isfinite(maximum):raise ValueError('A retained groom has non-finite fitting channels.')
        maxima[key]=maximum
    return dict(nativeCorrespondenceVertices=count,nativeUvSkinPaletteAndFittingIdentitiesExact=True,
        retainedNativeFaceSubsetAndOrientationValid=True,nativeReferenceFaces=native_faces,retainedNativeFaces=retained_native_faces,
        maximumRetainedPositionDeltaMetres=maxima['vertices'],maximumRetainedNormalDelta=maxima['normals'],
        maximumRetainedTangentDelta=maxima['tangents'],nativeSamplingReferenceUnchanged=True)


def retain_source_fitting(packet,native,binding,current):
    """Carry later source fitting through the same native triangle correspondence."""
    validate_retained_groom_source(native,current)
    count=native['vertexCount'];result=copy.deepcopy(packet)
    identities=lambda value:[(s['name'],[f['weight']for f in s['frames']])for s in value['shapes']]
    assert identities(native)==identities(current)==identities(packet)
    triangles=[];bary=[]
    for row in binding['vertices']:
        surface=row.get('geometrySurface')or row.get('surface')
        triangles.append(surface['triangle']if surface else[row['anchor']]*3)
        bary.append(surface['barycentric']if surface else[1.,0.,0.])
    triangles=np.asarray(triangles,int);bary=np.asarray(bary,float)
    def frames(value):return[None]+[f for s in value['shapes']for f in s['frames']]
    old_frames,new_frames,authored_frames=frames(native),frames(current),frames(packet)
    output_frames=frames(result);base=None;low=None;high=None
    for index,(old,new,authored,output)in enumerate(zip(old_frames,new_frames,authored_frames,output_frames)):
        absolute=[]
        for source,frame in((native,old),(current,new),(packet,authored)):
            points=np.asarray(source['vertices']).reshape(-1,3).copy()
            if frame is not None:points+=np.asarray(frame['vertices']).reshape(-1,3)
            absolute.append(points)
        original,retained,target=absolute
        sample=lambda value:np.einsum('vi,vij->vj',bary,value[:count][triangles])
        target+=sample(retained[:count]-original[:count]);target[:count]=retained[:count]
        low=target.min(axis=0)if low is None else np.minimum(low,target.min(axis=0))
        high=target.max(axis=0)if high is None else np.maximum(high,target.max(axis=0))
        if index==0:base=target.copy();result['vertices']=target.ravel().tolist()
        else:
            assert base is not None and output is not None
            output['vertices']=(target-base).ravel().tolist()
    if 'bounds'in result:
        assert low is not None and high is not None
        bounds=np.asarray(result['bounds']);low=np.minimum(low,bounds[:3]-bounds[3:]*.5);high=np.maximum(high,bounds[:3]+bounds[3:]*.5)
        low=np.nextafter(low.astype('<f4'),np.float32(-np.inf)).astype(float);high=np.nextafter(high.astype('<f4'),np.float32(np.inf)).astype(float)
        result['bounds']=np.r_[(low+high)*.5,high-low].tolist()
    result['sourceFingerprint']=fingerprint(result)[0]
    return result


def validate_surface_skin(native,packet,declaration,*,source_fingerprint=None,binding=None):
    """Check a compiled center stencil against the exact saved four-slot stream."""
    if set(declaration)!={'sourceVertexCount','sourceFingerprint','vertices'}or \
            type(declaration['sourceVertexCount'])is not int or not 0<declaration['sourceVertexCount']<=native['vertexCount']or \
            not isinstance(declaration['sourceFingerprint'],str)or re.fullmatch('[a-f0-9]{64}',declaration['sourceFingerprint'])is None:
        raise ValueError('A compiled surface skin lost its native source owner.')
    count=declaration['sourceVertexCount']
    if source_fingerprint is not None and declaration['sourceFingerprint']!=source_fingerprint:
        raise ValueError('A compiled surface skin changed its native source fingerprint.')
    weights=np.asarray(packet['weights'],dtype='<f4').reshape(-1,4);indices=np.asarray(packet['boneIndices'],dtype='<i4').reshape(-1,4)
    result={}
    if not isinstance(declaration['vertices'],list):raise ValueError('A compiled surface skin has no vertex table.')
    for row in declaration['vertices']:
        if set(row)!={'vertex','surface'}or type(row['vertex'])is not int or not count<=row['vertex']<packet['vertexCount']or row['vertex']in result:
            raise ValueError('A compiled surface skin has a repeated or unowned target vertex.')
        sample=row['surface']
        if not isinstance(sample,dict)or set(sample)!={'triangle','barycentric'}or not isinstance(sample['triangle'],list):
            raise ValueError('A compiled surface skin has an invalid native sample.')
        if any(type(index)is not int or not 0<=index<count for index in sample['triangle']):
            raise ValueError('A compiled surface skin escaped its exact native prefix.')
        target_weights,target_indices=source_surface_skin(native,sample);index=row['vertex']
        if not np.array_equal(weights[index],target_weights)or not np.array_equal(indices[index],target_indices):
            raise ValueError('A compiled surface changed its declared center skin stencil.')
        result[index]=sample
    if not result:raise ValueError('A compiled surface skin declaration is empty.')
    if binding is not None:
        expected={index for index,vertex in enumerate(binding['vertices'])if index>=count and
            not vertex['native']and vertex['role']!='retained-source'}
        if set(result)!=expected:raise ValueError('A compiled surface skin omitted or acquired an owned vertex.')
    return result


def standalone_cloth_attachment(packet,native,binding,attachment_source=None,*,retained_support=None):
    """Compile geometric samples to the native prefab's anchor attachment format.

    Frame offsets retain the legacy anchor encoding. Newly owned samples carry
    one center surface stencil for both skin and serialized cloth preparation.
    """
    if attachment_source is not None:packet=retain_source_fitting(packet,native,binding,attachment_source)
    support_proof=None
    if retained_support is not None:
        if attachment_source is None:raise ValueError('Retained support coupling requires the actual source prefab.')
        packet,binding,support_proof=couple_retained_groom_support(packet,native,binding,attachment_source,
            retained_support['materials'],retained_support['maximumSupportOffsetMetres'],carrier_surface=retained_support.get('carrierSurface'))
    result=copy.deepcopy(packet);attachment=copy.deepcopy(binding)
    domain=next((u for u in result['uv']if u['channel']==2),None)
    fitting_proof=None
    if domain is not None and np.any(np.asarray(domain['values']).reshape(-1,4)[:,2]>=17):
        fitting_proof=discard_invalid_serialized_curves(result,attachment,native['vertexCount'])
        tested,rejected=fitting_proof['testedCurves'],fitting_proof['rejectedCurves']
        assert isinstance(tested,int)and isinstance(rejected,int)and tested>rejected,'The fitted groom has no valid fine source curves.'
        packet=result
    rebase=attachment_source is not None
    p,n,t,b=native_frames(attachment_source if rebase else native,binding['matrix'])
    matrix=np.asarray(binding['matrix']).reshape(4,4).T;linear=matrix[:3,:3]
    points=np.asarray(packet['vertices']).reshape(-1,3)@linear.T+matrix[:3,3]
    normals=unit(np.asarray(packet['normals']).reshape(-1,3)@np.linalg.inv(linear))
    tangents=np.asarray(packet['tangents']).reshape(-1,4)[:,:3]@linear.T
    tangents=unit(tangents-normals*np.sum(normals*tangents,axis=1)[:,None])
    source_weights=np.asarray(native['weights']).reshape(-1,4)
    source_indices=np.asarray(native['boneIndices']).reshape(-1,4)
    weights=np.asarray(packet['weights']).reshape(-1,4).copy()
    indices=np.asarray(packet['boneIndices']).reshape(-1,4).copy()
    changed=0;compiled=0;rebased_prefix=0;skin_rows=[]
    for index,vertex in enumerate(attachment['vertices']):
        geometry=vertex.pop('geometrySurface',None)
        surface=vertex.pop('surface',None)
        if geometry is None and surface is None and not rebase:continue
        anchor=vertex['anchor']
        if surface is not None and index>=native['vertexCount']:
            assert not vertex['native'] and index>=native['vertexCount']
            anchor=int(surface['triangle'][int(np.argmax(np.asarray(surface['barycentric'],dtype='<f4')))])
            target_weights,target_indices=source_surface_skin(native,surface)
            changed+=int(not np.array_equal(weights[index],target_weights)or not np.array_equal(indices[index],target_indices))
            weights[index]=target_weights;indices[index]=target_indices
            skin_rows.append(dict(vertex=index,surface=dict(triangle=list(map(int,surface['triangle'])),
                barycentric=np.asarray(surface['barycentric'],dtype='<f4').tolist())))
        elif geometry is not None:
            anchor=int(geometry['triangle'][int(np.argmax(np.asarray(geometry['barycentric'],dtype='<f4')))])
            changed+=int(not np.array_equal(weights[index],source_weights[anchor])or not np.array_equal(indices[index],source_indices[anchor]))
            weights[index]=source_weights[anchor];indices[index]=source_indices[anchor]
        if rebase and vertex['native']:
            vertex['native']=False;rebased_prefix+=1
        basis=np.asarray((t[0,anchor],b[0,anchor],n[0,anchor]))
        vertex.update(anchor=anchor,offset=(basis@(points[index]-p[0,anchor])).tolist(),
                      normal=(basis@normals[index]).tolist(),tangent=(basis@tangents[index]).tolist())
        compiled+=1
    result['weights']=weights.ravel().tolist();result['boneIndices']=indices.ravel().tolist()
    attachment.pop('schemaVersion',None);attachment.pop('sourceVertexCount',None)
    result['sourceFingerprint']=fingerprint(result)[0]
    facts=dict(compiledVertices=compiled,changedSkinVertices=changed,
        nativeClothFrameRowsRebased=rebased_prefix,
        canonicalSkinAndClothAnchorShared=not bool(skin_rows),nativePrefixUnchanged=True,
        positionsNormalsTangentsUvColorsAndAllStoredFramesUnchanged=not rebase,
        subsequentSourceFittingRetained=rebase,fittedCurveProof=fitting_proof,runtimeSourceGraphRequired=False)
    if skin_rows:
        facts['surfaceSkin']=dict(sourceVertexCount=native['vertexCount'],sourceFingerprint=native['sourceFingerprint'],vertices=skin_rows)
        validate_surface_skin(native,result,facts['surfaceSkin'],source_fingerprint=native['sourceFingerprint'],binding=attachment)
    if support_proof is not None:facts['retainedRenderSupport']=support_proof
    if retained_support is not None and retained_support.get('carrierSurface')is not None:
        # Invalid fine curves are compacted before the carrier, so its source
        # partition is unchanged but its final contiguous vertex range moves.
        surface=copy.deepcopy(retained_support['carrierSurface']);old_first=surface['firstVertex'];surface['firstVertex']=result['vertexCount']-surface['vertices']
        primary=surface.get('flowCarrierCells',{}).get('model')=='primary-loft'
        values=np.asarray(domain['values']).reshape(-1,4)[surface['firstVertex']:,2]
        if surface['firstVertex']<native['vertexCount']or(primary and(len(values)!=surface['vertices']or np.any((values<48.5)|(values>=64.5)))or not primary and np.any(values>=.5)):
            raise ValueError('Compiled carrier vertices lost their contiguous surface ownership.')
        if primary:
            if sum(row.get('role')=='primary-lock'for row in attachment['vertices'])!=surface['vertices']:
                raise ValueError('Fitting rejected an explicitly owned primary curve; regenerate its legal source cells.')
            for curve in surface['flowCarrierCells']['curves']:curve['firstVertex']+=surface['firstVertex']-old_first
        facts['compiledCarrierSurface']=surface
    return result,attachment,facts


def compose_finished_source_groom(source,finished,attachment,*,vertex_capacity=None,authored_support=None,fine_material_slots=None,primary_material_slots=None,carrier_replacement=None,surface_skin=None):
    """Replace declared owned render surfaces while retaining source drivers and materials."""
    def roles(packet):
        channel=next(u for u in packet['uv']if u['channel']==2)
        assert channel['dimension']==4
        return np.asarray(channel['values']).reshape(-1,4)[:,2]
    owned_fine=lambda value:(value>=17)&(value<64.5)
    old_fine=owned_fine(roles(source));new_fine=owned_fine(roles(finished))
    new_primary=np.asarray([row.get('role')=='primary-lock'for row in attachment['vertices']],bool)
    if new_primary.shape!=new_fine.shape or np.any(new_primary&~((roles(finished)>=48.5)&(roles(finished)<64.5))):
        raise ValueError('A primary surface lost its explicit atlas-card vertex owner.')
    if new_primary.any()and primary_material_slots is None:
        raise ValueError('A primary surface requires its dedicated material purpose.')
    dedicated=fine_material_slots is not None or primary_material_slots is not None
    replacement_triangles=set();carrier_ids=np.zeros(finished['vertexCount'],bool);native_count=0;carrier_counts=None;carrier_facts=None
    if carrier_replacement is not None:
        if authored_support is not None:raise ValueError('A reconstructed carrier cannot also apply a retained-support deformation.')
        native=carrier_replacement['native'];surface=carrier_replacement['surface'];native_count=native['vertexCount']
        if surface.get('nativeFingerprint')!=native['sourceFingerprint']or not surface.get('flowCarrierCells'):
            raise ValueError('Carrier replacement lost its proven physical source partition.')
        carrier_budget,keep,replacement_triangles,carrier_facts=retained_carrier_allocation(native,source,attachment['matrix'],carrier_replacement['materials'],
            carrier_render_faces(surface),carrier_replacement['maximumSupportOffsetMetres'])
        first,count=surface['firstVertex'],surface['vertices']
        if type(first)is not int or type(count)is not int or first<native_count or count<=0 or first+count!=finished['vertexCount']:
            raise ValueError('Carrier replacement lost its finished vertex allocation.')
        carrier_ids[first:first+count]=True;carrier_counts=surface['triangleCountsBySlot']
        primary=surface['flowCarrierCells'].get('model')=='primary-loft'
        if len(carrier_counts)!=len(native['subMeshes'])or(primary and not np.array_equal(carrier_ids,new_primary)or not primary and np.any(new_fine[carrier_ids])):
            raise ValueError('Carrier replacement aliases its fine or material domain.')
        added=np.flatnonzero(new_fine|carrier_ids)
    else:keep=np.flatnonzero(~old_fine);added=np.flatnonzero(new_fine)
    assert len(added)and len(keep)and len(attachment['vertices'])==finished['vertexCount']
    assert source['bindPoses']==finished['bindPoses']
    identities=lambda value:[(s['name'],[f['weight']for f in s['frames']])for s in value['shapes']]
    assert identities(source)==identities(finished)
    support=source if authored_support is None else authored_support
    if authored_support is not None:
        validate_retained_groom_source(source,support)
        if support['vertexCount']!=source['vertexCount']or support['subMeshes']!=source['subMeshes']:
            raise ValueError('Authored support changed its retained source topology.')
    result=copy.deepcopy(support);count=len(keep)+len(added)
    assert count<65536
    if vertex_capacity is not None and(type(vertex_capacity)is not int or count>vertex_capacity):
        raise ValueError('The composed support and fine groom exceed their retained vertex capacity.')
    def combine(a,b,width):
        assert bool(a)==bool(b)
        return np.concatenate((np.asarray(a).reshape(-1,width)[keep],np.asarray(b).reshape(-1,width)[added])).ravel().tolist()if a else[]
    for key,width in(('vertices',3),('normals',3),('tangents',4),('weights',4),('boneIndices',4),('colors',4)):
        result[key]=combine(support[key],finished[key],width)
    assert[(u['channel'],u['dimension'])for u in source['uv']]==[(u['channel'],u['dimension'])for u in finished['uv']]
    for output,a,b in zip(result['uv'],support['uv'],finished['uv']):output['values']=combine(a['values'],b['values'],4)
    for output,a,b in zip(result['shapes'],support['shapes'],finished['shapes']):
        for out,x,y in zip(output['frames'],a['frames'],b['frames']):
            for key in('vertices','normals','tangents'):out[key]=combine(x[key],y[key],3)
    if not dedicated:assert len(source['subMeshes'])==len(finished['subMeshes'])
    elif len(source['subMeshes'])<len(finished['subMeshes']):raise ValueError('Dedicated fine materials omit a retained source material slot.')
    old_map=np.full(source['vertexCount'],-1,int);old_map[keep]=np.arange(len(keep))
    new_map=np.full(finished['vertexCount'],-1,int);new_map[added]=np.arange(len(added))+len(keep)
    if carrier_replacement is not None:new_map[:native_count]=old_map[:native_count]
    slots=[];retained_triangles=0;fine_triangles=0;primary_triangles=0
    fine_slots={};primary_slots={};targets=set()
    if dedicated:
        for purpose,declaration,lookup in(('fine',fine_material_slots,fine_slots),('primary',primary_material_slots,primary_slots)):
            for row in declaration or():
                a,b=row['sourceSlot'],row['targetSlot']
                if set(row)!={'sourceSlot','targetSlot'}or type(a)is not int or type(b)is not int or a in lookup or a<0 or a>=len(finished['subMeshes'])or b<len(finished['subMeshes']):
                    raise ValueError('A dedicated '+purpose+' material slot has another source domain.')
                if b in targets:raise ValueError('Dedicated material purposes repeat a target owner.')
                lookup[a]=b;targets.add(b)
        slots=[[]for _ in range(max([len(source['subMeshes']),*[b+1 for b in targets]]))]
    for slot,(a,b)in enumerate(zip(source['subMeshes'],finished['subMeshes'])):
        old_tri=np.asarray(a,int).reshape(-1,3);new_tri=np.asarray(b,int).reshape(-1,3)
        for tri,mask in((old_tri,old_fine),(new_tri,new_fine)):
            assert np.all(np.all(mask[tri],axis=1)|np.all(~mask[tri],axis=1)),'A source triangle mixes owned fine and retained support.'
        primary_mask=new_primary[new_tri]
        if np.any(np.any(primary_mask,axis=1)&~np.all(primary_mask,axis=1)):
            raise ValueError('A source triangle mixes primary and fine material purposes.')
        core=old_tri[np.all(~old_fine[old_tri],axis=1)]
        fine=new_tri[np.all(new_fine[new_tri]&~primary_mask,axis=1)]
        primary=new_tri[np.all(primary_mask,axis=1)]
        if carrier_replacement is not None:
            core=np.asarray([tri for tri in core if(slot,tuple(sorted(map(int,tri))))not in replacement_triangles],int).reshape(-1,3)
            amount=carrier_counts[slot]
            if type(amount)is not int or not 0<=amount<=len(new_tri):raise ValueError('Carrier replacement lost its rendered triangle range.')
            carrier=new_tri[-amount:]if amount else np.empty((0,3),int)
            if len(carrier)and not np.all(carrier_ids[carrier]|(carrier<native_count)):raise ValueError('Carrier triangles reference an unowned finished surface.')
            rendered_core=old_map[core]if surface['flowCarrierCells'].get('model')=='primary-loft'else np.concatenate((old_map[core],new_map[carrier]))
        else:rendered_core=old_map[core]
        if np.any(rendered_core<0):raise ValueError('Carrier composition omitted a retained driver or render vertex.')
        if not dedicated:slots.append(np.concatenate((rendered_core,new_map[fine])).ravel().tolist())
        else:
            slots[slot]=rendered_core.ravel().tolist()
            for purpose,triangles,lookup in(('fine',fine,fine_slots),('primary',primary,primary_slots)):
                if not len(triangles):continue
                if slot not in lookup:raise ValueError('A new '+purpose+' surface has no dedicated material slot.')
                slots[lookup[slot]]=new_map[triangles].ravel().tolist()
        retained_triangles+=len(core);fine_triangles+=len(fine)+len(primary);primary_triangles+=len(primary)
    if dedicated:
        for sub in source['subMeshes'][len(finished['subMeshes']):]:
            if sub and not np.all(old_fine[np.asarray(sub,int)]):raise ValueError('An additional source material slot contains unowned carrier geometry.')
    result.update(vertexCount=count,subMeshes=slots,indexFormat=0)
    low=np.minimum(np.asarray(support['bounds'][:3])-np.asarray(support['bounds'][3:])*.5,
                   np.asarray(finished['bounds'][:3])-np.asarray(finished['bounds'][3:])*.5)
    high=np.maximum(np.asarray(support['bounds'][:3])+np.asarray(support['bounds'][3:])*.5,
                    np.asarray(finished['bounds'][:3])+np.asarray(finished['bounds'][3:])*.5)
    result['bounds']=np.r_[(low+high)*.5,high-low].tolist()
    binding=copy.deepcopy(attachment);rows=[]
    uv0=np.asarray(next(u['values']for u in source['uv']if u['channel']==0)).reshape(-1,4)
    for index in keep:rows.append(dict(anchor=int(index),native=True,role='retained-source',slot=-1,uv0=uv0[index,:2].tolist(),
        offset=[0.,0.,0.],normal=[0.,0.,1.],tangent=[1.,0.,0.],tangentW=float(source['tangents'][int(index)*4+3])))
    p,n,t,b=native_frames(source,binding['matrix'])
    matrix=np.asarray(binding['matrix']).reshape(4,4).T;linear=matrix[:3,:3]
    support_p,support_n,support_t,_=native_frames(support,binding['matrix'])
    changed_support=np.zeros(source['vertexCount'],bool)
    for old,new in((p,support_p),(n,support_n),(t,support_t)):changed_support|=np.any(old!=new,axis=(0,2))
    for row,index in zip(rows,keep):
        if not changed_support[index]:continue
        basis=np.asarray((t[0,index],b[0,index],n[0,index]))
        row.update(native=False,offset=(basis@(support_p[0,index]-p[0,index])).tolist(),
            normal=(basis@support_n[0,index]).tolist(),tangent=(basis@support_t[0,index]).tolist())
    points=np.asarray(finished['vertices']).reshape(-1,3)@linear.T+matrix[:3,3]
    normals=unit(np.asarray(finished['normals']).reshape(-1,3)@np.linalg.inv(linear))
    tangents=np.asarray(finished['tangents']).reshape(-1,4)[:,:3]@linear.T
    tangents=unit(tangents-normals*np.sum(normals*tangents,axis=1)[:,None])
    old_weights=np.asarray(source['weights']).reshape(-1,4);old_indices=np.asarray(source['boneIndices']).reshape(-1,4)
    new_weights=np.asarray(finished['weights']).reshape(-1,4);new_indices=np.asarray(finished['boneIndices']).reshape(-1,4)
    skin_rows={}if surface_skin is None else validate_surface_skin(source,finished,surface_skin,binding=attachment)
    for index in added:
        row=copy.deepcopy(attachment['vertices'][int(index)]);anchor=row['anchor']
        assert 0<=anchor<source['vertexCount']and not old_fine[anchor]
        if int(index)not in skin_rows:
            assert np.array_equal(old_weights[anchor],new_weights[index])and np.array_equal(old_indices[anchor],new_indices[index])
        basis=np.asarray((t[0,anchor],b[0,anchor],n[0,anchor]))
        row.update(native=False,offset=(basis@(points[index]-p[0,anchor])).tolist(),normal=(basis@normals[index]).tolist(),tangent=(basis@tangents[index]).tolist())
        row.pop('surface',None);row.pop('geometrySurface',None);rows.append(row)
    binding['vertices']=rows;binding['sourceFingerprint']=source['sourceFingerprint'];binding.pop('schemaVersion',None);binding.pop('sourceVertexCount',None)
    result['sourceFingerprint']=fingerprint(result)[0]
    proof=dict(retainedSourceVertices=len(keep),retainedSourceTriangles=retained_triangles,
        addedFineVertices=int(new_fine.sum()),addedFineTriangles=fine_triangles,sourceSupportAndAllStoredFramesExact=not bool(np.any(changed_support[keep])),
        finishedFineChannelsAndAllStoredFramesExact=True,sourceCoreTopologyExact=not replacement_triangles,sourceClothRowsRetained=True,
        replacedOwnedFineVertices=int(old_fine.sum()),composedVertices=count,retainedVertexCapacity=vertex_capacity,
        authoredSupportVertices=int(changed_support[keep].sum()),sourceReferenceFingerprint=source['sourceFingerprint'])
    if fine_material_slots is not None:proof['fineMaterialSlots']=copy.deepcopy(fine_material_slots)
    if primary_material_slots is not None:
        proof.update(primaryMaterialSlots=copy.deepcopy(primary_material_slots),addedPrimaryVertices=int(new_primary.sum()),
            addedPrimaryTriangles=primary_triangles,materialPurposesRetainSourcePrefix=True)
    if surface_skin is not None:
        if carrier_replacement is not None and surface_skin['sourceFingerprint']!=carrier_replacement['native']['sourceFingerprint']:
            raise ValueError('A composed surface skin changed its original native authority.')
        if any(new_map[index]<0 for index in skin_rows):raise ValueError('Composition omitted an explicitly skinned owned surface.')
        proof['surfaceSkin']=dict(sourceVertexCount=surface_skin['sourceVertexCount'],sourceFingerprint=surface_skin['sourceFingerprint'],
            vertices=[dict(vertex=int(new_map[index]),surface=copy.deepcopy(skin_rows[index]))for index in sorted(skin_rows)])
    if carrier_replacement is not None:
        budget=carrier_budget;stride=budget['vertexAndPhysicalCostPerVertexBytes']
        source_bytes=source['vertexCount']*stride+sum(map(len,source['subMeshes']))*(2 if source['indexFormat']==0 else 4)
        target_bytes=result['vertexCount']*stride+sum(map(len,result['subMeshes']))*2
        if result['vertexCount']>budget['maximumFinalVertices']or target_bytes>source_bytes:
            raise ValueError('Carrier composition exceeds its actual source vertex/index/all-frame bytes.')
        proof.update(addedCarrierVertices=int(np.sum(carrier_ids&~new_primary)),replacedSourceCarrierTriangles=len(replacement_triangles),
            sourceDriverPrefixVertices=native_count,sourceDriverPrefixAndAllFramesExact=True,
            carrierSourceDomain=carrier_facts,carrierVertexAndAllFrameBytes=stride,sourceLogicalBytes=source_bytes,targetLogicalBytes=target_bytes,
            finishedCarrierChannelsAndAllStoredFramesExact=True,physicalCarrierReplacement=copy.deepcopy(surface))
        declared=surface.get('sourceFlow')
        if declared is not None:
            directions=np.asarray(declared['directions'],float);ids=np.arange(surface['firstVertex'],surface['firstVertex']+surface['vertices'])
            if directions.shape!=(len(ids),3)or not np.isfinite(directions).all()or np.any(np.linalg.norm(directions,axis=1)<=1e-20):
                raise ValueError('Carrier source flow lost its complete reconstructed vertex domain.')
            if np.any(new_map[ids]<0)or not np.all(carrier_ids[ids]):
                raise ValueError('Carrier source flow references an unowned composed vertex.')
            proof['declaredFlow']=dict(vertices=new_map[ids].tolist(),directions=directions.tolist(),authority=copy.deepcopy(declared['authority']))
    return result,binding,proof


def curved_vertex_capacity(native_count,current_count,added_count,added_limit,*,allow_reallocation=False):
    """Account for retained source support separately from new groom allocation."""
    retained_support=current_count-native_count-added_count
    assert retained_support>=0
    maximum_count=min(65535,native_count+retained_support+added_limit)
    assert allow_reallocation or current_count<=maximum_count
    return retained_support,maximum_count,maximum_count-current_count


def serializable_core_faces(source_p,inverse,triangles):
    """Keep native faces whose complete fan cannot survive binary32 morph storage."""
    triangles=np.asarray(triangles,dtype=int).reshape(-1,3)
    native=np.asarray(source_p)@np.asarray(inverse).T
    # Use the actual wire barycentrics and the same base + float delta stores
    # as the final Mesh. Testing all edge splits also covers unsplit fans.
    bary=np.asarray([[1/3,1/3,1/3],[1,0,0],[.5,.5,0],[0,1,0],[0,.5,.5],[0,0,1],[.5,0,.5]],dtype='<f4').astype(float)
    points=np.einsum('pvij,ki->pvkj',np.asarray(source_p)[:,triangles],bary)@np.asarray(inverse).T
    base=points[0].astype('<f4')
    children=np.asarray([[0,i,1 if i==6 else i+1]for i in range(1,7)])
    driver_points=np.asarray(source_p)[:,triangles]
    a,b=driver_points[:,:,1]-driver_points[:,:,0],driver_points[:,:,2]-driver_points[:,:,0]
    area=np.linalg.norm(np.cross(a,b),axis=2)
    length=np.maximum.reduce((np.linalg.norm(a,axis=2),np.linalg.norm(b,axis=2),np.linalg.norm(driver_points[:,:,2]-driver_points[:,:,1],axis=2)))
    accepted=np.all(area/np.maximum(length*length,1e-30)>64*np.finfo(np.float32).eps,axis=0)
    for pose in range(len(native)):
        stored=base if pose==0 else(base+(points[pose]-points[0]).astype('<f4')).astype('<f4')
        stored=stored.astype(float)
        stored[:,[1,3,5]]=native[pose,triangles].astype('<f4')
        parent=native[pose,triangles].astype('<f4').astype(float)
        cross=np.cross(parent[:,1]-parent[:,0],parent[:,2]-parent[:,0])
        p=stored[:,children]
        orientation=np.einsum('vij,vj->vi',np.cross(p[:,:,1]-p[:,:,0],p[:,:,2]-p[:,:,0]),cross)
        accepted&=np.all(orientation>0,axis=1)
    return accepted


def curved_core_budget(headroom, fine_group_sizes, fraction):
    """Reserve whole fine sheets before allocating curved support samples."""
    assert 0. < fraction < 1.
    assert all(size > 0 for size in fine_group_sizes)
    total=sum(fine_group_sizes);mandatory=max(0,-headroom)
    if mandatory>total:raise ValueError('Whole fine sheets cannot recover the retained carrier allocation.')
    limit = mandatory+int((total-mandatory)*fraction)
    donated = 0
    groups = []
    for index, size in enumerate(fine_group_sizes):
        if donated + size <= limit or donated<mandatory:
            groups.append(index)
            donated += size
    return headroom + donated, groups




def primary_loft_guides(packet,binding,positions,native_count,seam_tolerance):
    """Recover paired rows and actual longitudinal connections, without cuts."""
    uv=np.asarray(next(u['values']for u in packet['uv']if u['channel']==2),dtype='<f4').reshape(-1,4)
    p=np.asarray(positions,float)
    if p.ndim!=3 or p.shape[1:]!=(packet['vertexCount'],3)or len(binding['vertices'])!=len(uv):
        raise ValueError('Primary guides lost their complete stored-pose operands.')
    if not np.isfinite(p).all()or not 0<native_count<=len(uv)or seam_tolerance<=0:
        raise ValueError('Primary guides require their native prefix and physical seam limit.')
    groups=collections.defaultdict(list)
    for slot,flat in enumerate(packet['subMeshes']):
        for face,tri in enumerate(np.asarray(flat,int).reshape(-1,3)):
            roles=uv[tri,2];medium=(roles>=48.5)&(roles<64.5)
            if not medium.any():continue
            if not medium.all()or np.any(tri<native_count):
                raise ValueError('One triangle mixes primary guide and native material owners.')
            key=(slot,int(roles[0]),struct.pack('<f',float(uv[tri[0],3])))
            if np.any(roles!=roles[0])or any(struct.pack('<f',float(uv[i,3]))!=key[2]for i in tri):
                raise ValueError('A primary guide triangle mixes distinct flow walks.')
            groups[key].append((face,tri))
    output=[]
    for key,faces in sorted(groups.items()):
        indices=sorted(set(int(i)for _,tri in faces for i in tri));neighbors=collections.defaultdict(set)
        for _,tri in faces:
            for a,b in zip(tri,np.roll(tri,-1)):
                neighbors[int(a)].add(int(b));neighbors[int(b)].add(int(a))
        centers=[i for i in indices if uv[i,0]==.5]
        if len(centers)<2:raise ValueError('An admitted primary guide lost its real three-column section.')
        sections=[];lookup={}
        for center in sorted(centers,key=lambda i:(float(uv[i,1]),i)):
            transverse=[i for i in neighbors[center]if uv[i,1]==uv[center,1]]
            left=[i for i in transverse if uv[i,0]==0];right=[i for i in transverse if uv[i,0]==1]
            if len(left)!=1 or len(right)!=1:raise ValueError('A primary section has ambiguous transverse topology.')
            ids=[left[0],center,right[0]];rows=[binding['vertices'][i]for i in ids]
            if any(row['native']or row['slot']!=key[0]for row in rows)or any(row.get('surface')!=rows[0].get('surface')or row.get('geometrySurface')!=rows[0].get('geometrySurface')for row in rows):
                raise ValueError('A primary section lost its one canonical source center stencil.')
            if rows[0].get('surface')is None:raise ValueError('A primary section has no native center surface.')
            lookup[center]=len(sections)
            sections.append(dict(vertices=ids,v=float(uv[center,1]),skinSurface=copy.deepcopy(rows[0]['surface']),geometrySurface=copy.deepcopy(rows[0].get('geometrySurface'))))
        connections=[]
        for center in centers:
            for other in sorted(neighbors[center]):
                if other not in lookup or uv[other,1]<=uv[center,1]:continue
                connections.append((lookup[center],lookup[other]))
        if not connections:raise ValueError('An admitted primary guide has no longitudinal span.')
        degree=collections.Counter(i for edge in connections for i in edge)
        if set(degree)!=set(range(len(sections)))or max(degree.values())>2:
            raise ValueError('A primary guide requires explicit branch ownership before lofting.')
        # Exact render topology, including the two triangles per section side.
        expected=[]
        for first,last in connections:
            a=sections[first]['vertices'];b=sections[last]['vertices']
            for side in range(2):expected.extend(((a[side],a[side+1],b[side]),(a[side+1],b[side+1],b[side])))
        canonical=lambda tri:min(tuple(tri[i:])+tuple(tri[:i])for i in range(3))
        if collections.Counter(canonical(list(tri))for _,tri in faces)!=collections.Counter(canonical(list(tri))for tri in expected):
            raise ValueError('A primary guide differs from its admitted oriented ribbon cells.')
        aliases=[]
        for i,first in enumerate(sections):
            for j in range(i+1,len(sections)):
                last=sections[j]
                if first['v']!=last['v']:continue
                if np.max(np.linalg.norm(p[:,first['vertices']]-p[:,last['vertices']],axis=2))<=seam_tolerance:
                    aliases.append((i,j))
        output.append(dict(slot=key[0],sourceRole=key[1],rank=struct.unpack('<f',key[2])[0],sections=sections,
            connections=connections,physicalUvAliases=aliases,sourceFaces=[face for face,_ in faces],
            chartEndsAreNotPhysicalTips=True))
    return output


def _unit(values):
    values=np.asarray(values,float)
    length=np.linalg.norm(values,axis=-1,keepdims=True)
    if not np.isfinite(values).all() or np.any(length<=1e-20):
        raise ValueError('The declared all-pose loft frame became singular.')
    return values/length


def primary_taper(parameters, arc_metres, *, free_start, free_end, taper_metres):
    """Only proven physical free ends taper, never a UV or sampling boundary."""
    v=np.asarray(parameters,dtype='<f4').astype(float)
    arc=np.asarray(arc_metres,float)
    if v.ndim!=1 or len(v)<2 or arc.shape!=v.shape or not np.isfinite(arc).all():
        raise ValueError('A primary taper requires the complete physical walk.')
    if np.any(np.diff(v)<0) or np.any(np.diff(arc)<0) or arc[-1]<=arc[0]:
        raise ValueError('A primary taper reversed its physical curve.')
    if taper_metres<=0 or not np.isfinite(taper_metres):
        raise ValueError('A primary taper requires its declared physical length.')
    scale=np.ones(len(v))
    for is_free,distance in ((free_start,arc-arc[0]),(free_end,arc[-1]-arc)):
        if is_free:
            t=np.clip(distance/taper_metres,0.,1.)
            scale*=t*t*(3.-2.*t)
    return scale


def primary_loft_geometry(centers, transverse, widths, parameters, arc_metres,
        connections, *, free_start=False, free_end=False, taper_metres,
        height_fraction, reference_sections, lift=None, physical_uv_aliases=()):
    """Create an actual arch and a nondegenerate single-vertex physical tip.

    Each input section has one canonical source stencil. That same stencil is
    copied to all output section vertices; neither nearest side anchors nor
    averaged skin coefficients are introduced. The caller owns UV0 sampling
    and original material, Fixed/contact/hardware retention, and capacity.
    """
    center=np.asarray(centers,float)
    if center.ndim!=3 or center.shape[2]!=3 or not np.isfinite(center).all():
        raise ValueError('A primary loft requires every stored physical center.')
    poses,count,_=center.shape
    reference=np.asarray(reference_sections,float)
    if reference.shape!=(poses,count,3,3) or not np.isfinite(reference).all():
        raise ValueError('A primary loft requires its admitted source section geometry in every stored fit.')
    across=_unit(transverse)
    if across.shape!=center.shape:
        raise ValueError('Primary width lost its all-pose source correspondence.')
    widths=np.asarray(widths,float);v=np.asarray(parameters,dtype='<f4').astype(float)
    arc=np.asarray(arc_metres,float)
    if widths.shape!=(count,) or not np.isfinite(widths).all() or np.any(widths<=0):
        raise ValueError('A primary loft requires the measured positive source width.')
    if not 0<height_fraction<=.25:
        raise ValueError('A primary loft requires its declared physical section.')
    taper=primary_taper(v,arc,free_start=free_start,free_end=free_end,taper_metres=taper_metres)
    if len(taper)!=count:
        raise ValueError('A primary loft lost its physical parameter rows.')
    edges=[];degree=np.zeros(count,int);along=np.zeros_like(center)
    for first,last in connections:
        if not 0<=first<count or not 0<=last<count or v[last]<=v[first]:
            raise ValueError('A primary span reversed its full-curve coordinate.')
        delta=center[:,last]-center[:,first]
        if np.any(np.linalg.norm(delta,axis=1)<=1e-20):
            raise ValueError('A primary span collapsed in one stored fit.')
        along[:,first]+=delta;along[:,last]+=delta
        degree[first]+=1;degree[last]+=1;edges.append((first,last))
    if not edges or np.any(degree==0) or np.any(degree>2):
        raise ValueError('A primary branch requires explicit source ownership.')
    # Only the caller's proven physical UV aliases may share derivatives.
    # Geometric coincidence alone does not connect different render sheets.
    unjoined=along.copy();aliased=set()
    for first,last in physical_uv_aliases:
        if first in aliased or last in aliased or first==last or not 0<=first<count or not 0<=last<count:
            raise ValueError('A physical UV alias has ambiguous ownership.')
        if degree[first]!=1 or degree[last]!=1 or v[first]!=v[last] or arc[first]!=arc[last] or not np.array_equal(center[:,first],center[:,last]) or not np.array_equal(across[:,first],across[:,last]):
            raise ValueError('A physical UV alias lost its full-pose shared edge.')
        total=unjoined[:,first]+unjoined[:,last]
        along[:,first]=total;along[:,last]=total;aliased.update((first,last))
    normal=_unit(np.cross(across,_unit(along)))
    displacement=np.zeros(count) if lift is None else np.asarray(lift,float)
    if displacement.shape!=(count,) or not np.isfinite(displacement).all():
        raise ValueError('A primary loft lift lost its declared physical rows.')
    base=center+normal*(displacement*taper)[None,:,None]
    rows=[];section_owner=[];columns=[];points=[];normals=[];tangents=[]
    for index in range(count):
        width=float(widths[index]*taper[index])
        if taper[index]==0:
            # A true physical tip has one position, rather than three equal
            # points making two zero-area terminal quads.
            if index not in (0,count-1) or degree[index]!=1:
                raise ValueError('Only a declared physical end may own an apex.')
            rows.append([len(points)]);section_owner.append(index);columns.append(.5)
            points.append(base[:,index]);normals.append(normal[:,index]);tangents.append(across[:,index])
            continue
        ids=[]
        for side in range(3):
            ids.append(len(points));section_owner.append(index);columns.append(side*.5)
            point=base[:,index]+across[:,index]*((side-1)*width*.5)
            if side==1:point=point+normal[:,index]*(width*height_fraction)
            slope=(-1,0,1)[side]*4*height_fraction
            points.append(point);normals.append(_unit(normal[:,index]+across[:,index]*slope))
            tangents.append(across[:,index])
        rows.append(ids)
    triangles=[]
    for first,last in edges:
        a,b=rows[first],rows[last]
        if len(a)==len(b)==1:
            raise ValueError('Two free tips cannot replace a positive-area source span.')
        if len(a)==1:
            triangles.extend(((a[0],b[1],b[0]),(a[0],b[2],b[1])))
        elif len(b)==1:
            triangles.extend(((a[0],a[1],b[0]),(a[1],a[2],b[0])))
        else:
            for side in range(2):
                triangles.extend(((a[side],a[side+1],b[side]),(a[side+1],b[side+1],b[side])))
    p=np.stack(points,axis=1);n=np.stack(normals,axis=1);t=np.stack(tangents,axis=1)
    triangles=np.asarray(triangles,int)
    q=p[:,triangles]
    signed=np.sum(np.cross(q[:,:,1]-q[:,:,0],q[:,:,2]-q[:,:,0])*n[:,triangles].mean(axis=2),axis=2)
    if not np.isfinite(signed).all() or np.any(signed<=1e-14):
        raise ValueError('A primary loft inverted or collapsed in one stored fit.')
    owner=np.asarray(section_owner,int);column=np.rint(np.asarray(columns)*2).astype(int)
    before=reference[:,owner,column][:,triangles]
    before_cross=np.cross(before[:,:,1]-before[:,:,0],before[:,:,2]-before[:,:,0])
    after_cross=np.cross(q[:,:,1]-q[:,:,0],q[:,:,2]-q[:,:,0])
    orientation=np.sum(before_cross*after_cross,axis=2)
    if not np.isfinite(orientation).all() or np.any(orientation<=0):
        raise ValueError('A primary loft reversed its admitted source geometric driver.')
    return dict(positions=p,normals=n,tangents=t,triangles=triangles,
        sections=rows,sectionOwner=np.asarray(section_owner,int),
        transverseUv=np.asarray(columns,float),globalV=v[np.asarray(section_owner,int)],
        taper=taper,minimumSignedArea=float(signed.min()),
        physicalFreeEnds=(bool(free_start),bool(free_end)),connections=edges,
        sourceCenterStencilShared=True,minimumSourceDriverOrientation=float(orientation.min()),
        driverPrefixIsValidatedSeparately=True)



def primary_source_end_owner(native,slot,surface,direction,points,groups,*,root_vertices=(),stop_reason,away_from_root,edge_owners=None):
    """Classify a real native endpoint; UV cuts and trace budgets are not tips."""
    triangles=[np.asarray(flat,int).reshape(-1,3)for flat in native['subMeshes']]
    tri=np.asarray(surface['triangle']);bary=np.asarray(surface['barycentric'],dtype='<f4').astype(float)
    if not 0<=slot<len(triangles)or tri.shape!=(3,)or not np.issubdtype(tri.dtype,np.integer)or tuple(sorted(tri.tolist()))not in {tuple(sorted(t.tolist()))for t in triangles[slot]}or bary.shape!=(3,)or not np.isfinite(bary).all()or np.any(bary<0)or abs(bary.sum()-1)>1e-6:
        raise ValueError('A primary endpoint lost its actual native slot/surface.')
    count=native['vertexCount'];groups=np.asarray(groups)
    if groups.shape!=(count,)or np.asarray(points).shape!=(count,3):
        raise ValueError('A primary endpoint lost its physical topology.')
    roots=set(map(int,root_vertices))
    if any(not 0<=i<count for i in roots):raise ValueError('A primary root has another native owner.')
    support=tri[bary>1e-6]
    if len(support)and set(map(int,support))<=roots:
        return dict(kind='held-root',trueFreeEnd=False,nativeTriangle=tri.tolist(),sourceBarycentric=bary.tolist(),
            rootBasis='resolved source-terminal contact/Fixed root groups')
    if edge_owners is None:
        edge_owners=collections.defaultdict(set)
        for owner_slot,faces in enumerate(triangles):
            for face,t in enumerate(faces):
                for a,b in ((0,1),(1,2),(2,0)):
                    key=tuple(sorted((int(groups[t[a]]),int(groups[t[b]]))))
                    if key[0]!=key[1]:edge_owners[key].add((owner_slot,face))
    zero=np.flatnonzero(bary<=1e-6)
    if len(zero)!=1:
        return dict(kind='trace-budget-or-unresolved-vertex-end',trueFreeEnd=False)
    opposite=int(zero[0]);a,b=[i for i in range(3)if i!=opposite]
    key=tuple(sorted((int(groups[tri[a]]),int(groups[tri[b]]))))
    owners=edge_owners[key]
    if len(owners)>1:
        kind='native-chart-or-hard-material-boundary'
        return dict(kind=kind,trueFreeEnd=False,physicalEdge=list(key),nativeConsumers=len(owners))
    if stop_reason!='boundary-or-ambiguous-forward-edge'or not away_from_root:
        return dict(kind='non-free-walk-termination',trueFreeEnd=False)
    q=np.asarray(points)[tri];edge=np.stack((q[1]-q[0],q[2]-q[0]),axis=1)
    tangent=np.array(direction,dtype=float,copy=True);normal=np.cross(edge[:,0],edge[:,1]);length=np.linalg.norm(normal)
    if length<=1e-20:raise ValueError('A primary endpoint has no local geometric driver.')
    normal/=length;tangent-=normal*np.dot(tangent,normal)
    if np.linalg.norm(tangent)<=1e-20:raise ValueError('A primary endpoint has no local source flow.')
    delta=np.linalg.lstsq(edge,tangent,rcond=None)[0];delta=np.r_[-delta.sum(),delta]
    if delta[opposite]>=-1e-10:
        return dict(kind='native-side-edge-not-an-outward-flow-tip',trueFreeEnd=False)
    return dict(kind='native-open-flow-terminal',trueFreeEnd=True,physicalEdge=list(key),
        nativeTriangle=tri.tolist(),sourceBarycentric=bary.tolist(),sourceDirection=tangent.tolist())



def primary_end_walk(native,triangles,start,direction,flow,positions,normals,uv,neighbors,charts,domain,terminal,settings,*,away):
    """Extend a source curve to an owned root/terminal, never to a UV cut tip."""
    current=copy.deepcopy(start);route=[];travelled=0.;stops=collections.Counter()
    previous_tip=source_root_potential(current['bary'],terminal['terminalDistance'][triangles[current['triangle']]])
    sign=np.asarray(direction,float);step=settings['stepMetres']
    if not 0<step<=settings['maximumLengthMetres']or not 0<settings['terminalStopMetres']<=settings['terminalRegionMetres']:
        raise ValueError('A primary terminal walk needs its declared source lengths.')
    def result(reason,free=False):
        return dict(nodes=route,reason=reason,trueFreeEnd=free,travelledMetres=float(travelled),
            lastSurface=dict(triangle=triangles[current['triangle']].tolist(),barycentric=np.asarray(current['bary'],dtype='<f4').tolist()),
            terminalRegionMetres=settings['terminalRegionMetres'],terminalStopMetres=settings['terminalStopMetres'],stops=dict(stops))
    for _ in range(int(math.ceil(settings['maximumLengthMetres']/step))+1):
        tri=triangles[current['triangle']]
        root=source_root_potential(current['bary'],terminal['rootDistance'][tri])
        tip=source_root_potential(current['bary'],terminal['terminalDistance'][tri])
        if root is None:return result('unresolved-source-root')
        if not away and root<=1e-8:return result('confirmed-source-root')
        if away and settings['allowClosedTerminals']and tip is not None and previous_tip is not None and tip<=settings['terminalStopMetres']and tip<previous_tip-1e-10 and root>=settings['minimumRootDistanceMetres']:
            return result('authored-free-end-in-native-terminal-envelope',True)
        axis=flow(current['point'],current['normal'],sign,current['triangle'])
        if axis is None:return result('unresolved-native-Main-flow')
        outcome={}
        routed=source_surface_walk(current,current['point']+axis*min(step,settings['maximumLengthMetres']-travelled),
            flow,positions,normals,uv,neighbors,charts,eligible=lambda value:domain.surface_hair([value]),
            domain=domain,surface_domain=True,outcome=outcome,stops=stops)
        if not routed:return result(outcome.get('reason','no-forward-source-step'))
        previous_root=root;accepted=[]
        for node in routed:
            native_tri=triangles[node['triangle']]
            following=source_root_potential(node['bary'],terminal['rootDistance'][native_tri])
            if following is None:return result('unresolved-source-root')
            if (following-previous_root)*(1 if away else-1)<-1e-8:return result('source-root-order-boundary')
            try:source_surface_skin(native,dict(triangle=native_tri.tolist(),barycentric=np.asarray(node['bary'],dtype='<f4').tolist()))
            except SurfaceSkinSupportError:return result('retained-native-five-support-region')
            distance=float(np.linalg.norm(node['point']-current['point']))
            travelled+=distance;previous_root=following;current=node;accepted.append(copy.deepcopy(node));route.append(copy.deepcopy(node))
        if not accepted:return result('no-forward-source-step')
        sign=np.asarray(outcome.get('direction',axis))
        after_root=source_root_potential(current['bary'],terminal['rootDistance'][triangles[current['triangle']]])
        after_tip=source_root_potential(current['bary'],terminal['terminalDistance'][triangles[current['triangle']]])
        if not away and after_root is not None and after_root<=1e-8:return result('confirmed-source-root')
        if away and settings['allowClosedTerminals']and after_tip is not None and tip is not None and after_tip<=settings['terminalStopMetres']and after_tip<tip-1e-10 and after_root is not None and after_root>=settings['minimumRootDistanceMetres']:
            return result('authored-free-end-in-native-terminal-envelope',True)
        if tip is not None:previous_tip=tip
        if outcome.get('reason')!='step-complete':
            # A real native boundary or hard/material branch is carried back
            # as evidence. Its tip classification uses primary_source_end_owner.
            return result(outcome.get('reason','unresolved-source-boundary'))
        if travelled>=settings['maximumLengthMetres']-1e-10:return result('trace-length-budget')
    return result('bounded-trace-step-budget')



def plan_primary_source_lofts(entry,packet,binding,native,settings):
    """Plan owned primary arrays without writing packets or changing drivers."""
    native_p,native_n,_,_=native_frames(native,entry['rendererToPrefab'])
    p,_,_,_=native_frames(packet,entry['rendererToPrefab'])
    contact=entry['sourceContactDomain'];protected=np.zeros(native['vertexCount'],bool);protected[contact['protectedVertexIds']]=True
    fixed=contact['fixedVertexIds']
    if fixed and entry.get('nativeClothSelection')is None:raise ValueError('Primary fixed root seeds have no resolved native selection receipt.')
    terminal=source_terminal_domain(native,entry['rendererToPrefab'],entry['materials'],protected,contact['settings'],contact_vertices=contact['contactVertexIds'],fixed_vertices=fixed)
    native_p=native_position_frames(native,entry['rendererToPrefab'])
    native_uv=np.asarray(next(u['values']for u in native['uv']if u['channel']==0),dtype='<f4').reshape(-1,4)[:,:2]
    groom=np.asarray(next(u['values']for u in packet['uv']if u['channel']==2),dtype='<f4').reshape(-1,4)
    groups=curved_core_groups(native);guides=primary_loft_guides(packet,binding,p,native['vertexCount'],PROFILE['geometricSeamToleranceMetres'])
    resolved_roots=np.flatnonzero(np.isin(groups,terminal['contactRootGroups']+terminal['fixedRootGroups'])).tolist()
    driver_plan=dict(physicalGroups=groups,sourceTrianglesBySlot=[np.asarray(flat,int).reshape(-1,3)for flat in native['subMeshes']],vertices=[])
    native_drivers=prepare_geometry_frames(driver_plan,native_p)
    edge_owners=collections.defaultdict(set)
    for owner_slot,flat in enumerate(native['subMeshes']):
        for face,tri in enumerate(np.asarray(flat,int).reshape(-1,3)):
            for a,b in((0,1),(1,2),(2,0)):
                key=tuple(sorted((int(groups[tri[a]]),int(groups[tri[b]]))))
                if key[0]!=key[1]:edge_owners[key].add((owner_slot,face))
    slot_data={}
    for slot in {guide['slot']for guide in guides}:
        triangles=np.asarray(native['subMeshes'][slot],int).reshape(-1,3)
        material=entry['materials'][slot];domain=SourceHairDomain(material)
        lookup={tuple(sorted(map(int,tri))):face for face,tri in enumerate(triangles)}
        physical=collections.defaultdict(list)
        for face,tri in enumerate(triangles):
            for a,b in ((0,1),(1,2),(2,0)):
                key=tuple(sorted((int(groups[tri[a]]),int(groups[tri[b]]))))
                if key[0]!=key[1]:physical[key].append((face,a,b))
        neighbors=collections.defaultdict(list);charts=np.arange(len(triangles))
        for linked in physical.values():
            if len(linked)!=2:continue
            for (face,a,b),(other,oa,ob)in (linked,linked[::-1]):
                if groups[triangles[face,a]]!=groups[triangles[other,oa]]:oa,ob=ob,oa
                if any(first!=last and np.min(np.sum(native_n[:,first]*native_n[:,last],axis=1))<PROFILE['geometricSeamMinimumNormalDot']for first,last in zip(triangles[face,[a,b]],triangles[other,[oa,ob]])):continue
                continuous=np.max(np.abs(native_uv[triangles[face,[a,b]]]-native_uv[triangles[other,[oa,ob]]]))<=PROFILE['nativeUvSeamTolerance']
                neighbors[face].append((other,a,b,oa,ob,continuous))
        source_flow,source_quality,source_flow_proof=_carrier_face_flow(native,native_p[0],material,
            radius=PROFILE.get('carrierSurface',{}).get('sourceSurface',{}).get('patternRadiusPixels',2))
        slot_data[slot]=(triangles,domain,lookup,neighbors,charts,source_flow,source_quality,source_flow_proof)
    output=[];failures=collections.Counter();unresolved_tips=0;colour_seam_spans=0;end_owners=collections.Counter();diagnostics=[]
    for guide in guides:
        slot=guide['slot'];triangles,domain,lookup,neighbors,charts,source_flow,source_quality,source_flow_proof=slot_data[slot]
        section_sources=[];section_drivers=[];reference=[];centers=[];widths=[];across=[];samples=[];rejected=False
        sections=copy.deepcopy(guide['sections']);end_walks=[]
        for section in sections:
            ids=section['vertices'];axis=p[0,ids[2]]-p[0,ids[0]]
            old_taper=.025+.975*max(0.,math.sin(math.pi*section['v']))**(.55+.35*(abs(guide['rank'])-1.))
            section['requestedWidthMetres']=float(np.linalg.norm(axis))/old_taper
            section['sourceAxis']=axis
        # Extend the admitted trace through the same native physical walk.
        # Its old budget/chart endpoints do not acquire taper ownership.
        for end,neighbor in ((0,1),(len(sections)-1,len(sections)-2)):
            source=sections[end]['skinSurface'];tri=np.asarray(source['triangle'],int)
            bary=np.asarray(source['barycentric'],dtype='<f4').astype(float)
            face=lookup[tuple(sorted(tri.tolist()))];corners=triangles[face]
            weights=dict(zip(tri.tolist(),bary.tolist()));own=np.asarray([weights.get(int(i),0.)for i in corners])
            other=sections[neighbor]['skinSurface'];other_p=np.asarray(other['barycentric'])@native_p[0,other['triangle']]
            point=own@native_p[0,corners];axis=point-other_p
            if np.linalg.norm(axis)<=1e-10:
                following=[b if a==end else a for a,b in guide['connections']if end in(a,b)]
                if len(following)!=1:
                    end_walks.append(dict(nodes=[],trueFreeEnd=False,reason='ambiguous-admitted-end'));continue
                other=sections[following[0]]['skinSurface'];axis=point-np.asarray(other['barycentric'])@native_p[0,other['triangle']]
            root=source_root_potential(own,terminal['rootDistance'][corners])
            other_root=source_root_potential(other['barycentric'],terminal['rootDistance'][other['triangle']])
            if root is None or other_root is None or abs(root-other_root)<=1e-10 or np.linalg.norm(axis)<=1e-10:
                end_walks.append(dict(nodes=[],trueFreeEnd=False,reason='unresolved-end-root-order'));continue
            away=root>other_root
            node=dict(point=point,normal=unit(own@native_n[0,corners]),uv=own@native_uv[corners],triangle=face,bary=own,chart=face)
            def flow(point,normal,sign=None,index=None):
                value=source_flow[index]if source_quality[index]>1e-6 else sign
                if value is None:return None
                value=np.array(value,dtype=float,copy=True)
                if sign is not None and np.dot(value,sign)<0:value=-value
                value-=normal*np.dot(value,normal)
                return unit(value)if np.linalg.norm(value)>1e-12 else None
            walk_settings=dict(stepMetres=PROFILE['traceStepMetres'],maximumLengthMetres=PROFILE['targetLengthLimitsMetres'][1],
                terminalRegionMetres=contact['settings']['terminalRegionLengthMetres'],
                terminalStopMetres=min(PROFILE['traceStepMetres'],contact['settings']['terminalRegionLengthMetres']*.25),
                minimumRootDistanceMetres=contact['settings']['minimumRootDistanceMetres'],allowClosedTerminals=True)
            walked=primary_end_walk(native,triangles,node,unit(axis),flow,native_p[0,triangles],native_n[0,triangles],
                native_uv[triangles],neighbors,charts,domain,terminal,walk_settings,away=away)
            if not walked['trueFreeEnd']:
                terminal_node=walked['lastSurface'];terminal_face=lookup[tuple(sorted(terminal_node['triangle']))]
                terminal_bary=np.asarray(terminal_node['barycentric'],dtype='<f4').astype(float)
                target=triangles[terminal_face];values=dict(zip(terminal_node['triangle'],terminal_bary))
                target_bary=np.asarray([values.get(int(i),0.)for i in target])
                final_axis=walked['nodes'][-1]['point']-walked['nodes'][-2]['point']if len(walked['nodes'])>1 else axis
                owner=primary_source_end_owner(native,slot,dict(triangle=target.tolist(),barycentric=target_bary.tolist()),
                    final_axis,native_p[0],groups,root_vertices=resolved_roots,stop_reason=walked['reason'],away_from_root=away,edge_owners=edge_owners)
                walked['trueFreeEnd']=owner['trueFreeEnd'];walked['endpointOwner']=owner
            walked['awayFromResolvedRoot']=away;end_walks.append(walked)
        extended=[]
        for end,walked in zip((0,len(sections)-1),end_walks):
            added=[]
            for node in walked['nodes']:
                source=dict(triangle=triangles[node['triangle']].tolist(),barycentric=np.asarray(node['bary'],dtype='<f4').tolist())
                added.append(dict(skinSurface=source,geometrySurface=None,
                    requestedWidthMetres=sections[end]['requestedWidthMetres'],sourceAxis=sections[end]['sourceAxis']))
            extended.append(added)
        sections=list(reversed(extended[0]))+sections+extended[1]
        records=[dict(surface=section['skinSurface'])for section in sections]
        try:prepare_geometry_frames(dict(driver_plan,vertices=records),native_p,native_drivers)
        except KeyError:failures['retained-source-without-stable-all-fit-driver']+=1;continue
        for section,record in zip(sections,records):section['geometrySurface']=record['geometrySurface']
        points=np.asarray([np.asarray(row['geometrySurface']['barycentric'])@native_p[0,row['geometrySurface']['triangle']]for row in sections])
        lengths=np.linalg.norm(np.diff(points,axis=0),axis=1);arc=np.r_[0,np.cumsum(lengths)]
        if arc[-1]<=1e-10:failures['zero-complete-source-curve']+=1;continue
        for section,value in zip(sections,arc/arc[-1]):section['v']=float(value)
        connections=[(i,i+1)for i,distance in enumerate(lengths)if distance>1e-10]
        aliases=[(i,i+1)for i,distance in enumerate(lengths)if distance<=1e-10]
        # A final chart-side sample may own no positive-length render span.
        # Remove only such unused samples with identical physical barycentric
        # support and four-slot skin, retaining each actual rendered chart side.
        used=set(i for edge in connections for i in edge);omitted=[]
        for index in sorted(set(range(len(sections)))-used):
            source=sections[index]['skinSurface'];support=collections.defaultdict(float)
            for vertex,weight in zip(source['triangle'],np.asarray(source['barycentric'],dtype='<f4')):
                if weight:support[int(groups[vertex])]+=float(weight)
            candidates=[other for other in used if arc[other]==arc[index]]
            equivalent=False
            for other in candidates:
                sample=sections[other]['skinSurface'];other_support=collections.defaultdict(float)
                for vertex,weight in zip(sample['triangle'],np.asarray(sample['barycentric'],dtype='<f4')):
                    if weight:other_support[int(groups[vertex])]+=float(weight)
                if support!=other_support:continue
                try:
                    a=source_surface_skin(native,source);b=source_surface_skin(native,sample)
                except SurfaceSkinSupportError:continue
                if all(np.array_equal(x,y)for x,y in zip(a,b)):equivalent=True;break
            if not equivalent:rejected=True;break
            omitted.append(index)
        if rejected:failures['unresolved-isolated-source-chart-section']+=1;continue
        retained=sorted(used);remap={old:new for new,old in enumerate(retained)}
        sections=[sections[i]for i in retained]
        connections=[(remap[a],remap[b])for a,b in connections]
        aliases=[(remap[a],remap[b])for a,b in aliases if a in used and b in used]
        guide=dict(guide,sections=sections,connections=connections,physicalUvAliases=aliases)
        path_centers=np.asarray([np.asarray(section['geometrySurface']['barycentric'],dtype='<f4').astype(float)@native_p[0,np.asarray(section['geometrySurface']['triangle'],int)]for section in guide['sections']])
        face_normal=unit(np.cross(native_p[0,triangles[:,1]]-native_p[0,triangles[:,0]],native_p[0,triangles[:,2]]-native_p[0,triangles[:,0]]))
        walk_normals=np.repeat(face_normal[:,None,:],3,axis=1)
        for section_index,section in enumerate(guide['sections']):
            source=section['skinSurface'];driver=section['geometrySurface']
            tri=np.asarray(driver['triangle'],int);bary=np.asarray(driver['barycentric'],dtype='<f4').astype(float)
            if tuple(sorted(tri.tolist()))not in lookup:
                failures['retained-source-driver-outside-hair-material-slot']+=1;rejected=True;break
            face=lookup[tuple(sorted(tri.tolist()))];corners=triangles[face]
            weights={int(i):float(w)for i,w in zip(tri,bary)}
            own_bary=np.asarray([weights.get(int(i),0.)for i in corners])
            center=np.einsum('i,pij->pj',bary,native_p[:,tri]);normal=unit(bary@native_n[0,tri])
            old_axis=section['sourceAxis'];width=section['requestedWidthMetres']
            before=max(0,section_index-1);after=min(len(path_centers)-1,section_index+1)
            while before>0 and np.linalg.norm(path_centers[before]-path_centers[section_index])<1e-8:before-=1
            while after<len(path_centers)-1 and np.linalg.norm(path_centers[after]-path_centers[section_index])<1e-8:after+=1
            along=path_centers[after]-path_centers[before]
            axis=np.cross(along,face_normal[face])
            if np.linalg.norm(axis)<=1e-12:failures['unresolved-geometric-source-transverse-axis']+=1;rejected=True;break
            if np.dot(axis,old_axis)<0:axis=-axis
            if width<=1e-10:failures['zero-admitted-width']+=1;rejected=True;break
            axis=unit(axis)
            node=dict(point=own_bary@native_p[0,corners],normal=normal,uv=own_bary@native_uv[corners],triangle=face,bary=own_bary,chart=face)
            try:
                bounded,transverse=source_ribbon_width(node,axis,width,settings['maximumSourceWidthFraction'],
                    native_p[0,triangles],walk_normals,native_uv[triangles],neighbors,charts,
                    eligible=lambda value:domain.surface_hair([value]),domain=domain,return_samples=True,surface_domain=True)
            except (ValueError,AssertionError):
                failures['unresolved-transverse-source-walk']+=1;rejected=True;break
            if bounded<=1e-10:
                failures['unresolved-zero-source-width']+=1;rejected=True
                if len(diagnostics)<12:
                    diagnostics.append(dict(kind='zero-transverse-width',slot=slot,rank=guide['rank'],section=section_index,
                        nativeFace=face,triangle=corners.tolist(),barycentric=own_bary.tolist(),requestedWidthMetres=width,
                        axis=axis.tolist(),position=node['point'].tolist(),nativeNormal=node['normal'].tolist(),
                        geometricNormal=face_normal[face].tolist(),normalDotGeometry=float(node['normal']@face_normal[face]),
                        adjacentEdges=[[*map(int,value[:5]),bool(value[5])]for value in neighbors.get(face,())],endWalkReasons=[walk['reason']for walk in end_walks]))
                break
            pair=[];uv_samples=[]
            for sample in transverse:
                native_tri=triangles[sample['triangle']]
                wire=np.asarray(sample['bary'],dtype='<f4').astype(float)
                pair.append(np.einsum('i,pij->pj',wire,native_p[:,native_tri]))
                uv_samples.append(dict(triangle=native_tri.tolist(),barycentric=wire.tolist()))
            values=np.stack((pair[0],center,pair[1]),axis=1)
            vector=pair[1]-pair[0]
            if np.any(np.linalg.norm(vector,axis=1)<=1e-12):
                failures['collapsed-native-width-in-stored-fit']+=1;rejected=True;break
            reference.append(values);centers.append(center);across.append(unit(vector));widths.append(float(np.linalg.norm(vector[0])))
            samples.append([uv_samples[0],copy.deepcopy(source),uv_samples[1]]);section_sources.append(copy.deepcopy(source));section_drivers.append(copy.deepcopy(section.get('geometrySurface')))
        if rejected:continue
        if np.quantile(widths,.9)<settings['minimumPrimaryWidthMetres']:
            failures['retained-source-narrow-lock']+=1;continue
        reference=np.stack(reference,axis=1);centers=np.stack(centers,axis=1);across=np.stack(across,axis=1)
        v=np.asarray([section['v']for section in guide['sections']],dtype='<f4')
        arc=np.r_[0,np.cumsum(np.linalg.norm(np.diff(centers[0],axis=0),axis=1))]
        # Existing raw trace endpoints have no proof of true physical-tip status.
        # Keep them non-tapered and expose that missing authority as a result.
        free_ends=[bool(walk['trueFreeEnd'])for walk in end_walks]
        for end in (int(np.argmin(v)),int(np.argmax(v))):
            if free_ends[0 if end==0 else 1]:end_owners[end_walks[0 if end==0 else 1]['reason']]+=1;continue
            connected=[b if a==end else a for a,b in guide['connections']if end in(a,b)]
            if len(connected)!=1:end_owners['ambiguous-guide-end']+=1;continue
            source=section_sources[end];tri=np.asarray(source['triangle'],int);bary=np.asarray(source['barycentric'],dtype='<f4').astype(float)
            face=lookup[tuple(sorted(tri.tolist()))];corners=triangles[face];weights=dict(zip(tri.tolist(),bary.tolist()))
            own_bary=np.asarray([weights.get(int(i),0.)for i in corners]);direction=centers[0,end]-centers[0,connected[0]]
            direction=unit(direction);node=dict(point=own_bary@native_p[0,corners],normal=unit(own_bary@native_n[0,corners]),
                uv=own_bary@native_uv[corners],triangle=face,bary=own_bary,chart=face)
            outcome={}
            def follow(point,normal,sign=None,index=None):
                value=direction if sign is None else sign
                return unit(value-normal*np.dot(value,normal))
            source_surface_walk(node,node['point']+direction*PROFILE['traceStepMetres'],follow,
                native_p[0,triangles],native_n[0,triangles],native_uv[triangles],neighbors,charts,
                eligible=lambda value:domain.surface_hair([value]),domain=domain,surface_domain=True,outcome=outcome)
            target_face=outcome.get('sourceFace',face);target=triangles[target_face]
            target_bary=np.asarray(outcome.get('barycentric',own_bary),dtype='<f4').astype(float)
            root=terminal['rootDistance'][target]
            away=False
            if np.isfinite(root).all():
                edge=native_p[0,target[1:]]-native_p[0,target[0]]
                gradient=np.linalg.lstsq(edge,root[1:]-root[0],rcond=None)[0];away=float(np.dot(gradient,direction))>1e-10
            owner=primary_source_end_owner(native,slot,dict(triangle=target.tolist(),barycentric=target_bary.tolist()),
                np.asarray(outcome.get('direction',direction)),native_p[0],groups,
                root_vertices=resolved_roots,stop_reason=outcome.get('reason','no-progress'),away_from_root=away,edge_owners=edge_owners)
            # A probe proves the reached native endpoint. Moving/extending the
            # original guide to it is a separate required geometric operation.
            is_at_endpoint=np.linalg.norm(node['point']-target_bary@native_p[0,target])<=PROFILE['geometricSeamToleranceMetres']
            free_ends[0 if end==0 else 1]=bool(owner['trueFreeEnd']and is_at_endpoint);end_owners[owner['kind']]+=1
        unresolved_tips+=sum(not value for value in free_ends)
        try:
            geometry=primary_loft_geometry(centers,across,np.asarray(widths),v,arc,guide['connections'],
                taper_metres=settings['taperMetres'],height_fraction=settings['heightFraction'],reference_sections=reference,
                free_start=free_ends[0],free_end=free_ends[1],physical_uv_aliases=guide['physicalUvAliases'])
            geometry=reduce_primary_loft_sections(geometry,reference,PROFILE['maximumChordErrorMetres'])
        except ValueError as error:
            failures['all-fit-primary-geometry: '+str(error)]+=1
            if len(diagnostics)<12:
                degree=collections.Counter(i for edge in guide['connections']for i in edge)
                diagnostics.append(dict(kind='geometry-rejection',reason=str(error),slot=slot,rank=guide['rank'],
                    sectionCount=len(guide['sections']),isolatedSections=[i for i in range(len(v))if degree[i]==0],
                    zeroPhysicalSpans=[dict(sections=[i,i+1],left=guide['sections'][i]['skinSurface'],right=guide['sections'][i+1]['skinSurface'])
                        for i,distance in enumerate(np.linalg.norm(np.diff(centers[0],axis=0),axis=1))if distance<=1e-10],
                    endWalkReasons=[walk['reason']for walk in end_walks]))
            continue
        vertex_samples=[];vertex_surfaces=[];vertex_drivers=[]
        for index,column in zip(geometry['sectionOwner'],geometry['transverseUv']):
            vertex_samples.append(copy.deepcopy(samples[int(index)][int(round(column*2))]))
            vertex_surfaces.append(copy.deepcopy(section_sources[int(index)]));vertex_drivers.append(copy.deepcopy(section_drivers[int(index)]))
        # Color-chart discontinuities require an actual clipping/remap solution;
        # declaring endpoint UV samples alone must not silently bridge atlases.
        colors=np.asarray([np.asarray(sample['barycentric'])@native_uv[sample['triangle']]for sample in vertex_surfaces])
        seam_triangles=0
        for a,b in guide['connections']:
            for col in range(2):
                keys=[tuple(sorted(samples[row][side]['triangle']))for row,side in ((a,col),(a,col+1),(b,col))]
                seam_triangles+=int(len(set(keys))>1)
        colour_seam_spans+=seam_triangles
        output.append(dict(slot=slot,rank=guide['rank'],geometry=geometry,transverseSourceReferenceSamples=samples,
            centerSurfaces=vertex_surfaces,geometrySurfaces=vertex_drivers,uv0=colors,referenceSections=reference,
            endWalks=[{key:value for key,value in walk.items()if key!='nodes'}for walk in end_walks],
            sourceMainFlow=source_flow_proof,omittedNonRenderChartAliases=len(omitted),sourceReferenceFaceTransitions=seam_triangles))
    return dict(lofts=output,admittedGuideCount=len(guides),plannedGuideCount=len(output),rejections=dict(failures),
        plannedVertices=sum(len(row['geometry']['sectionOwner'])for row in output),
        plannedIndices=sum(row['geometry']['triangles'].size for row in output),
        unresolvedPhysicalTipEnds=unresolved_tips,sourceReferenceFaceTransitions=colour_seam_spans,
        nativePrefixUnchanged=True,sourceSkinAndVirtualCenterUnchanged=True,
        endOwners=dict(end_owners),fixedRootGroups=len(terminal['fixedRootGroups']),diagnostics=diagnostics,complete=False,atlasOwnedPrimary=True,actualSourceCoverageNotProven=True,artAccepted=False)



def reduce_primary_loft_sections(geometry,reference_sections,chord_limit):
    """Reduce redundant physical rows with an all-pose surface chord bound.

    No P/N/T sample is moved or averaged. The conservative bound includes the
    piecewise-triangle/bilinear twist on both old and candidate strips.
    """
    if not np.isfinite(chord_limit)or chord_limit<=0:raise ValueError('A primary curve needs its declared geometric chord limit.')
    p,n,t=(geometry[name]for name in('positions','normals','tangents'))
    rows=geometry['sections'];v=np.asarray([geometry['globalV'][row[0]]for row in rows])
    reference=np.asarray(reference_sections);edges=geometry['connections'];following={};preceding={}
    for a,b in edges:
        if a in following or b in preceding:raise ValueError('Primary reduction requires an explicitly unbranched source curve.')
        following[a]=b;preceding[b]=a
    paths=[]
    for start in sorted(set(following)-set(preceding)):
        path=[start]
        while path[-1]in following:path.append(following[path[-1]])
        paths.append(path)
    if sum(len(path)-1 for path in paths)!=len(edges):raise ValueError('Primary reduction lost a physical source span.')
    def span(a,b):
        first,last=rows[a],rows[b]
        if len(first)==1:return [(first[0],last[1],last[0]),(first[0],last[2],last[1])]
        if len(last)==1:return [(first[0],first[1],last[0]),(first[1],first[2],last[0])]
        return [(first[c],first[c+1],last[c])for c in range(2)]+[(first[c+1],last[c+1],last[c])for c in range(2)]
    sections=np.stack([p[:,row if len(row)==3 else row*3]for row in rows],axis=0)
    # sections: (source rows, poses, transverse columns, xyz)
    kept=set();spans=[];maximum_bound=0.;considered=0
    for path in paths:
        pending=[(0,len(path)-1)]
        while pending:
            a,b=pending.pop();first,last=path[a],path[b]
            if b==a+1:kept.update((first,last));spans.append((first,last));continue
            ids=np.asarray(path[a:b+1],int);q=sections[ids]
            weight=(v[ids]-v[first])/(v[last]-v[first])
            expected=(1-weight[:,None,None,None])*q[0]+weight[:,None,None,None]*q[-1]
            error=np.max(np.linalg.norm(q-expected,axis=3),axis=(1,2))
            old_twist=q[:-1,:,0]+q[1:,:,1]-q[:-1,:,1]-q[1:,:,0]
            other_twist=q[:-1,:,1]+q[1:,:,2]-q[:-1,:,2]-q[1:,:,1]
            new_twist=q[0,:,0]+q[-1,:,1]-q[0,:,1]-q[-1,:,0]
            new_other=q[0,:,1]+q[-1,:,2]-q[0,:,2]-q[-1,:,1]
            bound=float(error.max()+.25*max(np.linalg.norm(old_twist,axis=2).max(),np.linalg.norm(other_twist,axis=2).max())+
                .25*max(np.linalg.norm(new_twist,axis=1).max(),np.linalg.norm(new_other,axis=1).max()))
            tri=np.asarray(span(first,last),int);vertices=p[:,tri]
            cross=np.cross(vertices[:,:,1]-vertices[:,:,0],vertices[:,:,2]-vertices[:,:,0])
            signed=np.sum(cross*n[:,tri].mean(axis=2),axis=2)
            owner=geometry['sectionOwner'];column=np.rint(geometry['transverseUv']*2).astype(int)
            before=reference[:,owner,column][:,tri]
            driver=np.sum(cross*np.cross(before[:,:,1]-before[:,:,0],before[:,:,2]-before[:,:,0]),axis=2)
            considered+=1
            if bound<=chord_limit and np.all(signed>1e-14)and np.all(driver>0):
                maximum_bound=max(maximum_bound,bound);kept.update((first,last));spans.append((first,last));continue
            error[[0,-1]]=0.;split=int(np.argmax(error))
            if not 0<split<len(ids)-1:split=(len(ids)-1)//2
            pending.extend(((a,a+split),(a+split,b)))
    selected=np.asarray([index for row,indices in enumerate(rows)if row in kept for index in indices],int)
    mapping=np.full(p.shape[1],-1,int);mapping[selected]=np.arange(len(selected))
    triangles=mapping[np.asarray([tri for a,b in sorted(spans)for tri in span(a,b)],int)]
    if np.any(triangles<0):raise ValueError('Primary reduction lost a referenced render sample.')
    result=dict(geometry,positions=p[:,selected],normals=n[:,selected],tangents=t[:,selected],triangles=triangles,
        sectionOwner=geometry['sectionOwner'][selected],transverseUv=geometry['transverseUv'][selected],globalV=geometry['globalV'][selected],
        sections=[mapping[row].tolist()if index in kept else[]for index,row in enumerate(rows)],connections=sorted(spans),
        sectionReduction=dict(sourceSections=len(rows),renderSections=len(kept),sourceVertices=p.shape[1],renderVertices=len(selected),
            maximumGeometricChordBoundMetres=maximum_bound,declaredChordLimitMetres=float(chord_limit),
            consideredSpans=considered,nativePntSamplesMovedOrAveraged=False,physicalUvChartEndsRetained=True))
    q=result['positions'][:,triangles];cross=np.cross(q[:,:,1]-q[:,:,0],q[:,:,2]-q[:,:,0])
    signed=np.sum(cross*result['normals'][:,triangles].mean(axis=2),axis=2)
    before=reference[:,result['sectionOwner'],np.rint(result['transverseUv']*2).astype(int)][:,triangles]
    driver=np.sum(cross*np.cross(before[:,:,1]-before[:,:,0],before[:,:,2]-before[:,:,0]),axis=2)
    if np.any(signed<=1e-14)or np.any(driver<=0):raise ValueError('Primary reduction changed an all-fit physical orientation.')
    result.update(minimumSignedArea=float(signed.min()),minimumSourceDriverOrientation=float(driver.min()))
    return result


def admit_primary_lofts(lofts,vertex_capacity):
    """Spend available source capacity on complete locks with broad coverage."""
    if type(vertex_capacity)is not int or vertex_capacity<0:raise ValueError('Primary capacity must be a nonnegative vertex count.')
    candidates=[]
    for ordinal,loft in enumerate(lofts):
        geometry=loft['geometry'];positions=np.asarray(geometry['positions'],float);triangles=np.asarray(geometry['triangles'],int)
        if positions.ndim!=3 or positions.shape[2]!=3 or positions.shape[1]<=0 or not np.isfinite(positions).all()or triangles.ndim!=2 or triangles.shape[1]!=3 or not len(triangles)or np.any(triangles<0)or np.any(triangles>=positions.shape[1]):
            raise ValueError('Primary capacity selection requires complete finite all-fit geometry.')
        points=positions[:,triangles];areas=np.linalg.norm(np.cross(points[:,:,1]-points[:,:,0],points[:,:,2]-points[:,:,0]),axis=2).sum(axis=1)*.5
        area=float(areas.min());vertices=positions.shape[1]
        if area<=0:raise ValueError('Primary capacity selection requires noncollapsed coverage in every stored fit.')
        candidates.append(dict(ordinal=ordinal,vertices=vertices,minimumAreaSquareMetres=area,areaPerVertex=area/vertices))
    selected=set();used=0
    for row in sorted(candidates,key=lambda row:(-row['areaPerVertex'],-row['minimumAreaSquareMetres'],row['ordinal'])):
        if used+row['vertices']<=vertex_capacity:selected.add(row['ordinal']);used+=row['vertices']
    return ([loft for index,loft in enumerate(lofts)if index in selected],
        dict(vertexCapacity=vertex_capacity,admittedVertices=used,admittedLocks=len(selected),heldLocks=len(lofts)-len(selected),
            heldSourceCoverageRetained=True,priority='minimum all-fit physical area per complete-lock vertex',
            locks=[dict(row,admitted=row['ordinal']in selected)for row in candidates]))


def primary_core_packet(entry,current,native,binding,settings):
    """Use the existing carrier allocation for visible primary lofts and fine.

    Native prefix vertices remain physical drivers. The primary render cells
    are not a barycentric partition of their center skin samples.
    """
    planned=plan_primary_source_lofts(entry,current,binding,native,settings)
    if not planned['lofts']:raise ValueError('The declared primary source has no legal all-fit loft.')
    count=native['vertexCount'];reference=retained_groom_reference(entry,native)
    matrix=np.asarray(entry['rendererToPrefab']).reshape(4,4).T;linear=matrix[:3,:3];inverse=np.linalg.inv(linear)
    source_p,source_n,source_t,source_b=native_frames(native,entry['rendererToPrefab'])
    native_hand=np.asarray(native['tangents']).reshape(-1,4)[:,3]
    contact=entry['sourceContactDomain'];protected=np.zeros(count,bool);protected[contact['protectedVertexIds']]=True
    masks,domain_proof=retained_carrier_face_mask(native,reference,entry['rendererToPrefab'],entry['materials'],contact['settings']['maximumSupportOffsetMetres'])
    source_triangles=[np.asarray(flat,int).reshape(-1,3)for flat in native['subMeshes']]
    lookups=[{tuple(sorted(map(int,tri))):face for face,tri in enumerate(triangles)}for triangles in source_triangles]
    old_uv=np.asarray(next(u['values']for u in current['uv']if u['channel']==2)).reshape(-1,4)
    keep=np.flatnonzero((np.arange(current['vertexCount'])<count)|((old_uv[:,2]>=17)&(old_uv[:,2]<48.5)))
    # Alpha-cut primary locks add strand detail over the existing coverage.
    # Their stencils do not prove opaque coverage of a whole native triangle.
    render_replacement=[[]for _ in source_triangles]
    budget,source_keep,replaced,_=retained_carrier_allocation(native,reference,entry['rendererToPrefab'],entry['materials'],
        render_replacement,contact['settings']['maximumSupportOffsetMetres'])
    available=max(0,int(budget['maximumAddedVertices'])-(len(keep)-count))
    lofts,capacity=admit_primary_lofts(planned['lofts'],available)
    if not lofts:raise ValueError('The retained source has no capacity for a complete primary lock beside its coverage and fine fibers.')
    selected=[set()for _ in source_triangles];held_support=collections.Counter()
    for loft in lofts:
        slot=loft['slot']
        samples=[sample for row in loft['transverseSourceReferenceSamples']for sample in row]
        for sample in samples:
            face=lookups[slot][tuple(sorted(sample['triangle']))];tri=source_triangles[slot][face]
            if not masks[slot][face]:held_support['unresolved-retained-render-owner']+=1;continue
            if np.all(protected[tri]):held_support['held-root-contact-or-fixed-face']+=1;continue
            try:source_surface_skin(native,dict(triangle=tri.tolist(),barycentric=[1/3]*3))
            except SurfaceSkinSupportError:held_support['native-five-support-face']+=1;continue
            selected[slot].add(face)
    selected=[sorted(values)for values in selected]
    if not any(selected):raise ValueError('Primary lofts have no replaceable source-owned render surface.')
    # The declared medium layer takes over the interior atlas-card allocation.
    # Keep the actual edge-fiber tier; rejected primary cells retain their source
    # render faces. Primary alpha does not own their coverage.
    if not np.array_equal(keep[:count],np.arange(count)):raise ValueError('A primary surface changed its native driver prefix.')
    remap=np.full(current['vertexCount'],-1,int);remap[keep]=np.arange(len(keep))
    packet=copy.deepcopy(current);attachment=copy.deepcopy(binding)
    for key,width in(('vertices',3),('normals',3),('tangents',4),('weights',4),('boneIndices',4),('colors',4)):
        if packet[key]:packet[key]=np.asarray(packet[key]).reshape(-1,width)[keep].ravel().tolist()
    for channel in packet['uv']:channel['values']=np.asarray(channel['values']).reshape(-1,4)[keep].ravel().tolist()
    for shape in packet['shapes']:
        for frame in shape['frames']:
            for key in('vertices','normals','tangents'):frame[key]=np.asarray(frame[key]).reshape(-1,3)[keep].ravel().tolist()
    attachment['vertices']=[copy.deepcopy(binding['vertices'][int(i)])for i in keep]
    for slot,flat in enumerate(current['subMeshes']):
        retained=[]
        for tri in np.asarray(flat,int).reshape(-1,3):
            if np.any(remap[tri]<0):
                if not np.all(remap[tri]<0):raise ValueError('The medium allocation split an existing fine sheet.')
                continue
            retained.extend(remap[tri].tolist())
        packet['subMeshes'][slot]=retained
    first=len(keep);curves=[];directions=[];core_counts=[0]*len(source_triangles)
    low=np.asarray(packet['bounds'][:3])-np.asarray(packet['bounds'][3:])*.5
    high=np.asarray(packet['bounds'][:3])+np.asarray(packet['bounds'][3:])*.5
    for identifier,loft in enumerate(lofts):
        geometry=loft['geometry'];p=geometry['positions'];n=geometry['normals'];t=geometry['tangents'];amount=p.shape[1]
        start=len(attachment['vertices']);slot=loft['slot'];free_start,free_end=geometry['physicalFreeEnds']
        role=(57 if free_end else 49)+identifier%8
        local_p=(p-matrix[:3,3])@inverse.T;local_n=unit(n@linear);local_t=unit(t@inverse.T)
        anchors=[];rows=[];weights=[];indices=[];uv0=[]
        for offset,(surface,driver)in enumerate(zip(loft['centerSurfaces'],loft['geometrySurfaces'])):
            skin,parents=source_surface_skin(native,surface);anchor=int(surface['triangle'][int(np.argmax(surface['barycentric']))])
            anchors.append(anchor);weights.append(skin);indices.append(parents)
            basis=np.asarray((source_t[0,anchor],source_b[0,anchor],source_n[0,anchor]))
            rows.append(dict(anchor=anchor,native=False,role='primary-lock',slot=slot,uv0=loft['uv0'][offset].tolist(),
                offset=(basis@(p[0,offset]-source_p[0,anchor])).tolist(),normal=(basis@n[0,offset]).tolist(),
                tangent=(basis@t[0,offset]).tolist(),tangentW=-float(np.sign(np.linalg.det(linear))),
                surface=copy.deepcopy(surface),geometrySurface=copy.deepcopy(driver)))
            uv0.append([*loft['uv0'][offset],0.,0.])
        anchors=np.asarray(anchors,int)
        for key,values in(('vertices',local_p[0]),('normals',local_n[0]),
            ('tangents',np.column_stack((local_t[0],np.full(amount,-1.)))),('weights',weights),('boneIndices',indices)):
            packet[key]+=np.asarray(values).ravel().tolist()
        if packet['colors']:packet['colors']+=np.asarray(current['colors']).reshape(-1,4)[anchors].ravel().tolist()
        for channel in packet['uv']:
            if channel['channel']==0:values=uv0
            elif channel['channel']==2:values=np.column_stack((geometry['transverseUv'],geometry['globalV'],np.full(amount,role),np.full(amount,loft['rank'])))
            else:values=np.asarray(next(u['values']for u in current['uv']if u['channel']==channel['channel'])).reshape(-1,4)[anchors]
            channel['values']+=np.asarray(values).ravel().tolist()
        for pose,frame in enumerate((f for shape in packet['shapes']for f in shape['frames']),1):
            for key,values in(('vertices',local_p),('normals',local_n),('tangents',local_t)):
                frame[key]+=(values[pose]-values[0]).ravel().tolist()
        first_triangle=core_counts[slot]
        attachment['vertices'].extend(rows);packet['subMeshes'][slot]+=(geometry['triangles']+start).ravel().tolist()
        core_counts[slot]+=len(geometry['triangles'])
        directions.extend(unit(np.cross(n[0],t[0])@inverse.T).tolist())
        source_sections=sorted(set(map(int,geometry['sectionOwner'])));section_remap={old:new for new,old in enumerate(source_sections)}
        curves.append(dict(slot=slot,role=role,rank=loft['rank'],firstVertex=start,vertices=amount,
            firstTriangle=first_triangle,triangles=len(geometry['triangles']),
            sectionOwners=[section_remap[int(index)]for index in geometry['sectionOwner']],physicalFreeEnds=list(geometry['physicalFreeEnds']),
            sectionReduction=copy.deepcopy(geometry['sectionReduction']),
            sourceReferenceSections=[copy.deepcopy(loft['transverseSourceReferenceSamples'][index])for index in source_sections],endWalks=copy.deepcopy(loft['endWalks'])))
        low=np.minimum(low,local_p.min(axis=(0,1)));high=np.maximum(high,local_p.max(axis=(0,1)))
    packet['vertexCount']=len(attachment['vertices']);packet['bounds']=np.r_[(low+high)*.5,high-low].tolist()
    source_stride=budget['vertexAndPhysicalCostPerVertexBytes']
    added=packet['vertexCount']-count
    fine_indices=sum(len(flat)for flat in packet['subMeshes'])-sum(3 for slot,flat in enumerate(packet['subMeshes'])
        for tri in np.asarray(flat,int).reshape(-1,3)if np.all(tri<count))
    final_count=len(source_keep)+added
    final_indices=budget['retainedSourceIndexCount']+fine_indices
    final_bytes=final_count*source_stride+final_indices*2
    source_bytes=reference['vertexCount']*source_stride+sum(map(len,reference['subMeshes']))*(2 if reference['indexFormat']==0 else 4)
    if added>budget['maximumAddedVertices']or final_count>budget['maximumFinalVertices']or final_count>=65536 or final_bytes>source_bytes:
        raise OverflowError('Primary and retained fine surfaces exceed the actual source vertex/index/all-frame allocation: '+json.dumps(dict(
            retainedSourceVertices=len(source_keep),fineVertices=len(keep)-count,primaryVertices=packet['vertexCount']-first,
            targetVertices=final_count,maximumFinalVertices=budget['maximumFinalVertices'],addedVertices=added,maximumAddedVertices=budget['maximumAddedVertices'],
            targetIndices=final_indices,targetLogicalBytes=final_bytes,sourceLogicalBytes=source_bytes)))
    packet['sourceFingerprint']=fingerprint(packet)[0]
    surface=dict(firstVertex=first,vertices=packet['vertexCount']-first,nativeFingerprint=native['sourceFingerprint'],
        sourceFacesBySlot=selected,renderReplacementFacesBySlot=render_replacement,
        triangleCountsBySlot=core_counts,flowCarrierCells=dict(model='primary-loft',curves=curves,
            physicalCoverage='complete-native-prefix-triangles',centerSkinAndVirtualStencilUnchanged=True),
        protectedNativeVertexIds=contact['protectedVertexIds'],nativePrefixImmutable=True,
        removedWholeFineVertices=current['vertexCount']-len(keep),retainedFineVertices=len(keep)-count,
        sourceFlow=dict(directions=directions,authority=dict(
            source=[dict(slot=slot,**entry['materials'][slot]['maps']['_MainTex'])for slot in range(len(selected))if selected[slot]],uvChannel=0,
            scale={str(slot):entry['materials'][slot]['maps']['_MainTex']['scale']for slot in range(len(selected))if selected[slot]},
            offset={str(slot):entry['materials'][slot]['maps']['_MainTex']['offset']for slot in range(len(selected))if selected[slot]},
            provenance=dict(nativeFingerprint=native['sourceFingerprint'],sourceFacesBySlot=selected,
                model='native-Main-source-walk-primary-loft',samplers=[material.get('sourceTextureSamplers',{})for material in entry['materials']]))),
        curvatureProof=dict(heldSourceDomain=domain_proof,primaryPlanRejections=planned['rejections'],heldSupportReasons=dict(held_support),
            originalNativeTrianglesRemainPhysicalDrivers=True,primaryRenderCoverageIsNotCenterUvArea=True,
            sourceRenderCoverageRetained=True,primaryCapacitySelection=capacity,
            sourceLogicalBytes=source_bytes,targetLogicalBytes=final_bytes,sourceFitAppliedInAuthoring=False,
            allStoredSourcePoses=len(source_p),artAccepted=False))
    attachment['schemaVersion']=4
    return packet,attachment,surface,budget


def validate_primary_core(row,packet,binding,native,*,prefix_source=None):
    """Validate rendered cells against independent native source references."""
    surface=row['curvedCoreSurface'];cells=surface['flowCarrierCells'];count=native['vertexCount']
    first,amount=surface['firstVertex'],surface['vertices']
    if cells.get('model')!='primary-loft'or cells.get('physicalCoverage')!='complete-native-prefix-triangles'or surface['nativeFingerprint']!=native['sourceFingerprint']or first<count or amount<=0 or first+amount!=packet['vertexCount']:
        raise ValueError('Primary lofts lost their explicit physical/render owners.')
    prefix=native if prefix_source is None else prefix_source
    if len(binding['vertices'])!=packet['vertexCount']:raise ValueError('A primary loft lost its binding vertex owner.')
    for key,width in(('vertices',3),('normals',3),('tangents',4),('weights',4),('boneIndices',4)):
        dtype='<i4'if key=='boneIndices'else'<f4'
        target=prefix if key=='vertices'else native
        if not np.array_equal(np.asarray(packet[key],dtype=dtype).reshape(-1,width)[:count],np.asarray(target[key],dtype=dtype).reshape(-1,width)[:count]):
            raise ValueError('A primary loft changed its native prefix: '+key)
    if packet['bindPoses']!=native['bindPoses']:raise ValueError('A primary loft changed its native bind palette.')
    for target,original in zip(packet['shapes'],native['shapes']):
        if target['name']!=original['name']or len(target['frames'])!=len(original['frames']):raise ValueError('A primary loft changed its stored fit identities.')
        for frame,source in zip(target['frames'],original['frames']):
            if frame['weight']!=source['weight']:raise ValueError('A primary loft changed a stored fit weight.')
            for key in('vertices','normals','tangents'):
                authority=source
                if key=='vertices'and prefix_source is not None:
                    authority=next(f for s in prefix['shapes']if s['name']==original['name']for f in s['frames']if f['weight']==source['weight'])
                if not np.array_equal(np.asarray(frame[key],dtype='<f4').reshape(-1,3)[:count],np.asarray(authority[key],dtype='<f4').reshape(-1,3)[:count]):
                    raise ValueError('A primary loft changed its native stored frame: '+key)
    p=native_position_frames(packet,binding['matrix']);source_p=native_position_frames(prefix,binding['matrix'])
    # Normals are checked as rendered vectors; physical direction additionally
    # comes from the native source stencils rather than these same new normals.
    normal=native_frames(packet,binding['matrix'])[1]
    uv=np.asarray(next(u['values']for u in packet['uv']if u['channel']==2),dtype='<f4').reshape(-1,4)
    seen=set();triangles=0;minimum=float('inf');minimum_driver=float('inf')
    core=[]
    for slot,flat in enumerate(packet['subMeshes']):
        actual=np.asarray(flat,int).reshape(-1,3);n=surface['triangleCountsBySlot'][slot]
        owned=actual[-n:]if n else np.empty((0,3),int);retained=actual[:-n]if n else actual
        native_faces=np.asarray(native['subMeshes'][slot],int).reshape(-1,3)
        expected=native_faces[[i not in set(carrier_render_faces(surface)[slot])for i in range(len(native_faces))]]
        if not np.array_equal(retained[np.all(retained<count,axis=1)],expected):raise ValueError('Primary rendering changed an undeclared native source face.')
        if len(owned)and not np.all((owned>=first)&(owned<first+amount)):raise ValueError('A primary triangle escaped its explicit vertex range.')
        core.append(owned)
    for curve in cells['curves']:
        start,n=curve['firstVertex'],curve['vertices'];ids=np.arange(start,start+n)
        if start<first or start+n>first+amount or seen.intersection(map(int,ids)):raise ValueError('Primary curves repeat or escape their material owner.')
        seen.update(map(int,ids));owners=np.asarray(curve['sectionOwners'],int)
        if owners.shape!=(n,)or np.any(uv[ids,2]!=curve['role'])or np.any(uv[ids,3]!=np.float32(curve['rank'])):
            raise ValueError('A primary curve lost its whole-curve field.')
        if any(binding['vertices'][int(i)]['native']or binding['vertices'][int(i)]['role']!='primary-lock'for i in ids):
            raise ValueError('A primary curve lost its binding material purpose.')
        samples=curve['sourceReferenceSections'];reference=[]
        for section in samples:
            values=[]
            for sample in section:
                tri=np.asarray(sample['triangle'],int);bary=np.asarray(sample['barycentric'],dtype='<f4').astype(float)
                if tri.shape!=(3,)or np.any(tri<0)or np.any(tri>=count)or bary.shape!=(3,)or np.any(bary<0)or abs(bary.sum()-1)>1e-6:
                    raise ValueError('A primary reference escaped its native source stencil.')
                values.append(np.einsum('i,pij->pj',bary,source_p[:,tri]))
            reference.append(np.stack(values,axis=1))
        reference=np.stack(reference,axis=1)
        columns=np.rint(uv[ids,0]*2).astype(int)
        if np.any(columns<0)or np.any(columns>2)or np.any(owners<0)or np.any(owners>=len(samples)):
            raise ValueError('A primary reference lost its transverse section owner.')
        for identifier,section in zip(ids,owners):
            sample=binding['vertices'][int(identifier)].get('surface')
            if sample is not None and sample!=samples[int(section)][1]:raise ValueError('A primary cross section changed its canonical center skin stencil.')
        local=core[curve['slot']][curve['firstTriangle']:curve['firstTriangle']+curve['triangles']]
        if len(local)!=curve['triangles']or np.any(local<start)or np.any(local>=start+n):raise ValueError('A primary curve lost its actual render triangles.')
        q=p[:,local];cross=np.cross(q[:,:,1]-q[:,:,0],q[:,:,2]-q[:,:,0])
        signed=np.sum(cross*normal[:,local].mean(axis=2),axis=2)
        if not np.isfinite(signed).all()or np.any(signed<=0):raise ValueError('A serialized primary loft collapsed or inverted in a stored fit.')
        before=reference[:,owners,columns][:,local-start]
        driver=np.sum(cross*np.cross(before[:,:,1]-before[:,:,0],before[:,:,2]-before[:,:,0]),axis=2)
        if not np.isfinite(driver).all()or np.any(driver<=0):raise ValueError('A primary loft reversed its independent source geometry in a stored fit.')
        minimum=min(minimum,float(signed.min()));minimum_driver=min(minimum_driver,float(driver.min()));triangles+=len(local)
    if seen!=set(range(first,first+amount))or triangles!=sum(surface['triangleCountsBySlot']):
        raise ValueError('Primary cells omit or acquire render vertices/triangles.')
    return dict(status='PASS_PRIMARY_RENDER_CELLS_AND_NATIVE_PHYSICAL_PREFIX',children=triangles,storedFits=len(p),
        minimumSignedArea=minimum,minimumIndependentSourceOrientation=minimum_driver,nativeDriverPrefixVertices=count,
        originalNativeReferencePrefixRetained=True,physicalDriverCoverageUsesCompleteNativePrefix=True,
        primaryAlphaCoverageIsSeparate=True,sourceRenderCoverageRetained=not any(carrier_render_faces(surface)),renderedArtAccepted=False)


def curved_core_row(entry, output):
    """Replace core render faces without replacing native deformation samples."""
    assert entry['category'] in ('Hair', 'Beard', 'IntegratedHair'), 'Curved hair support requires a declared groom renderer.'
    assert sha(entry['generatedPacket']) == entry['generatedSha256']
    assert sha(entry['nativePacketPath']) == entry['nativePacketSha256']
    assert sha(entry['bindingPath']) == entry['bindingSha256']
    current, native = read(entry['generatedPacket']), read(entry['nativePacketPath'])
    binding = decode_binding(Path(entry['bindingPath']))
    provenance=validate_surface_provenance(binding, native)
    assert provenance.get('sourceVertexCount')==native['vertexCount'], 'Explicit existing source-triangle provenance is required.'
    assert fingerprint(current)[0] == entry['authoredFingerprint']
    primary=PROFILE.get('carrierSurface',{}).get('primaryLoft')
    if primary is not None:
        validate_carrier_settings(PROFILE['carrierSurface'])
        packet,new_binding,surface,budget=primary_core_packet(entry,current,native,binding,primary)
        prepare_surface_binding(new_binding,native)
        proof=validate_primary_core(dict(curvedCoreSurface=surface),packet,new_binding,native)
        packet_path=save_groom_packet(output,entry,packet);binding_path=output/Path(entry['bindingPath']).name
        encode_binding(new_binding,binding_path)
        added=packet['vertexCount']-native['vertexCount']
        result={key:value for key,value in entry.items()if key not in('serializedWinding','zeroFineReason','slots','coreCoordinateStatistics','nativeCoreRepair','nativeUvSeamsSplitAtSharedSourceEdges','sourceUvChordErrorTexels')}
        return dict(result,generatedPacket=str(packet_path),generatedSha256=sha(packet_path),bindingPath=str(binding_path),bindingSha256=sha(binding_path),
            authoredFingerprint=packet['sourceFingerprint'],compactedVertices=packet['vertexCount'],addedVertices=added,
            addedVertexBytes=added*entry['packedVertexStrideBytes'],addedPhysicalDeltaBytes=added*entry['physicalFrameCostPerNewVertexBytes'],
            finalTriangles=sum(len(sub)//3 for sub in packet['subMeshes']),curvedCoreSurface=surface,primaryNumericProof=proof,
            broadNativeOuterSheetReplaced=False,nativeReferenceVertices=native['vertexCount'],sourceCurrentVertices=current['vertexCount'],
            nativeReferencePrefixAndOrnamentFacesRetained=True,nativeCoreRepair=dict(nativeChannelAuthority=True,nativeRenderFacesRestored=True,explicitPrimaryCells=True),
            retainedBudget=budget,addedVertexLimit=budget['maximumAddedVertices'],renderedArtAccepted=False,bindingRuntimeValidationPending=True)
    count = native['vertexCount']
    groom = np.asarray(next(u['values'] for u in current['uv'] if u['channel'] == 2)).reshape(-1, 4)
    coordinates = groom[:count, :2] * .01
    source_coordinates = coordinates.copy()
    source_uv = np.asarray(next(u['values'] for u in native['uv'] if u['channel'] == 0)).reshape(-1, 4)[:, :2]
    frame_p, frame_n, frame_t, frame_b = native_frames(native, entry['rendererToPrefab'])
    matrix = np.asarray(entry['rendererToPrefab']).reshape(4, 4).T
    frame_p = native_position_frames(native,entry['rendererToPrefab'])-matrix[:3,3]
    linear, inverse = matrix[:3, :3], np.linalg.inv(matrix[:3, :3])
    native_hand = np.asarray(native['tangents']).reshape(-1, 4)[:, 3]
    physical_groups=curved_core_groups(native)
    carrier_settings=PROFILE.get('carrierSurface');carrier_terminal=None;carrier_domain_proof=None;retained_face_mask=None;carrier_guides=None;prepared_source=None
    source_surface=carrier_settings.get('sourceSurface')if carrier_settings is not None else None
    if carrier_settings is not None:
        validate_carrier_settings(carrier_settings)
        contact=entry.get('sourceContactDomain');guide_source=entry.get('carrierGuides')
        if not contact or contact['nativeFingerprint']!=native['sourceFingerprint']or(not source_surface and(not guide_source or guide_source['nativeFingerprint']!=native['sourceFingerprint'])):
            raise ValueError('Carrier reconstruction lost its complete source walks or contact authority.')
        if 'contactVertexIds'not in contact:raise ValueError('Carrier reconstruction requires separate confirmed contact and protected vertex owners.')
        if source_surface and('fixedVertexIds'not in contact or entry.get('nativeClothSelection')is None):
            raise ValueError('Source-pattern carrier reconstruction requires its resolved native cloth fixed-point authority.')
        protected=np.zeros(count,bool);protected[np.asarray(contact['protectedVertexIds'],int)]=True
        carrier_terminal=source_terminal_domain(native,entry['rendererToPrefab'],entry['materials'],protected,contact['settings'],
            contact_vertices=contact['contactVertexIds'])
        carrier_guides=guide_source['guides']if guide_source is not None else None
        if not source_surface and not carrier_guides:raise ValueError('Carrier reconstruction has no admitted complete physical guide.')
        reference=retained_groom_reference(entry,native)
        retained_face_mask,carrier_domain_proof=retained_carrier_face_mask(native,reference,entry['rendererToPrefab'],entry['materials'],contact['settings']['maximumSupportOffsetMetres'])
        if not source_surface:
            preferred_width=float(PROFILE['widthMetres'][1])*2**int(entry['lod'])*carrier_settings['widthMultiplier']
            settings=dict(carrier_settings,terminalRegionMetres=contact['settings']['terminalRegionLengthMetres'])
            carrier_fields,_=source_carrier_fields(carrier_guides,frame_p,native['subMeshes'],carrier_terminal,preferred_width,settings,PROFILE['maximumChordErrorMetres'],
                physical_groups=physical_groups,eligible_faces=retained_face_mask)
    slot_triangles, selected_faces, selected_keys = [], [], []
    unrepresentable_native_faces=0
    for slot, indices in enumerate(native['subMeshes']):
        triangles = np.asarray(indices, dtype=int).reshape(-1, 3)
        representable=serializable_core_faces(frame_p,inverse,triangles)
        domain = SourceHairDomain(entry['materials'][slot]) if entry['materials'][slot]['shader']=='Custom/FakeAnisoHair' else None
        selected = np.zeros(len(triangles), dtype=bool)
        for face, triangle in enumerate(triangles):
            if domain is None:continue
            if not all(binding['vertices'][int(i)]['role'] in ('volume-support', 'thin-root') for i in triangle):
                continue
            if not np.all(groom[triangle, 2] == 0) or not np.all(native_hand[triangle] == native_hand[triangle[0]]):
                continue
            edges = np.stack((coordinates[triangle[1]] - coordinates[triangle[0]], coordinates[triangle[2]] - coordinates[triangle[0]]), axis=1)
            if not source_surface and(abs(np.linalg.det(edges)) <= 1e-16 or np.linalg.cond(edges) >= 1e5):
                continue
            points = frame_p[:, triangle]
            if np.any(np.linalg.norm(np.cross(points[:, 1] - points[:, 0], points[:, 2] - points[:, 0]), axis=1) <= 1e-14):
                continue
            # Support keeps the native alpha sample. Transparent native pixels
            # remain transparent; only the metal/uncertain domain is excluded.
            if not domain.surface_hair(source_uv[triangle]):
                continue
            if not representable[face]:
                unrepresentable_native_faces+=1
                continue
            if carrier_settings is not None and(not retained_face_mask[slot][face]or(not source_surface and(slot,face)not in carrier_fields)):
                continue
            selected[face] = True
        slot_triangles.append(triangles)
        selected_faces.append(selected)
        selected_keys.append({tuple(sorted(map(int, tri))) for tri in triangles[selected]})
    assert any(np.any(s) for s in selected_faces), 'No complete native hair-only core domain.'
    if source_surface:
        fitted_p=native_position_frames(reference,entry['rendererToPrefab'])[:,:count]-matrix[:3,3]
        prepared_source=prepare_source_curved_core(native,fitted_p,frame_n,frame_t,entry['materials'],selected_faces,physical_groups,contact,source_surface,native_positions=frame_p)
        selected_keys=[{tuple(sorted(map(int,tri)))for tri in triangles[selected]}for triangles,selected in zip(slot_triangles,selected_faces)]
        metric_report=[dict(mode='source-Main-pattern-local-edge-phase',sourceVertices=count)]
    else:coordinates, metric_report = physical_core_coordinates(frame_p[0], coordinates, slot_triangles, selected_faces)
    groups = collections.defaultdict(list)
    interior_fine=((groom[:,2]>=17)&(groom[:,2]<33))|((groom[:,2]>=49)&(groom[:,2]<=64))
    for index in np.flatnonzero(interior_fine):
        vertex = binding['vertices'][int(index)]
        groups[(vertex['slot'], int(groom[index, 2]), struct.pack('<f', float(groom[index, 3])))].append(int(index))
    removable = []
    for key, indices in groups.items():
        if all(tuple(sorted(binding['vertices'][i]['surface']['triangle'])) in selected_keys[key[0]] for i in indices):
            removable.append((float(abs(groom[indices[0], 3]) - 1.), key, indices))
    # Small hierarchy ranks remain visible at reduced fine density.
    removable.sort(key=lambda item: (-item[0], item[1]))
    fraction = float(carrier_settings['fineDonationFraction']if carrier_settings is not None else PROFILE.get('coreSurfaceFineAllocationFraction', .4))
    assert 0. < fraction < 1.
    carrier_budget=None
    if carrier_settings is not None:
        carrier_budget=retained_carrier_allocation(native,reference,entry['rendererToPrefab'],entry['materials'],
            [np.flatnonzero(selected).tolist()for selected in selected_faces],contact['settings']['maximumSupportOffsetMetres'])[0]
    added_limit=int(carrier_budget['maximumAddedVertices']if carrier_budget is not None else entry['addedVertexLimit'])
    retained_support,maximum_count,headroom=curved_vertex_capacity(
        count,current['vertexCount'],int(entry['addedVertices']),added_limit,allow_reallocation=carrier_settings is not None)
    budget, donation_groups = curved_core_budget(headroom, [len(indices) for _, _, indices in removable], fraction)
    removable = [removable[index] for index in donation_groups]
    # A zero-edge-split partition still owns every selected source corner and
    # one centroid per face. Count both before distributing the fine budget.
    minimum_core=sum(int(np.sum(selected))+len(np.unique(triangles[selected]))
                     for triangles,selected in zip(slot_triangles,selected_faces))
    adaptive_resolution=minimum_core>budget
    shared_native_corners=False
    if source_surface:
        adaptive_resolution=False;minimum_core=sum(len(np.unique(triangles[selected]))for triangles,selected in zip(slot_triangles,selected_faces))
    elif adaptive_resolution:
        # Existing small source faces need no extra centroid. Refine only
        # above the solved physical width while retaining every source face.
        minimum_core=sum(len(np.unique(triangles[selected]))for triangles,selected in zip(slot_triangles,selected_faces))
        if minimum_core>budget:
            # Dense source LODs already own their corner channels. Keep their
            # exact native groom field when sharing that prefix, then allocate
            # only the samples required above its existing spatial resolution.
            shared_native_corners=True;coordinates=source_coordinates.copy();minimum_core=0
            metric_report=[dict(mode='native-source-field-and-corners-shared-without-channel-mutation',sourceVertices=count)]
    assert budget > 0, 'No source-owned interior fine allocation can be reassigned.'
    lod = int(entry['lod'])
    preferred = float(PROFILE['widthMetres'][1]) * 2 ** lod
    maximum_width=None
    if source_surface:
        preferred=source_surface['widthMetres']
    elif carrier_settings is not None:
        maximum_width=preferred*carrier_settings['widthMultiplier']
        preferred=float(PROFILE['widthMetres'][0])*2**lod*carrier_settings['widthMultiplier']

    def partition(width):
        if source_surface:
            return plan_source_curved_core(prepared_source,fitted_p,frame_n,frame_t,physical_groups,budget,width,
                slot_triangles=slot_triangles,inverse=inverse,native_positions=frame_p)
        if carrier_settings is not None:
            fields,guide_proof=source_carrier_fields(carrier_guides,frame_p,native['subMeshes'],carrier_terminal,width,settings,PROFILE['maximumChordErrorMetres'],
                physical_groups=physical_groups,eligible_faces=retained_face_mask)
            plan=plan_curved_core(coordinates,slot_triangles,selected_faces,width,budget,
                reusable_vertices=np.flatnonzero(protected),positions=frame_p[0],physical_groups=physical_groups,
                flow_fields=fields,protected_vertices=np.flatnonzero(protected))
            plan['flowGuideProof']=guide_proof
            return plan
        return plan_curved_core(coordinates, slot_triangles, selected_faces, width, budget,
                                reusable_vertices=range(count)if shared_native_corners else(),
                                positions=frame_p[0],physical_groups=physical_groups,subdivide_small_faces=not adaptive_resolution)

    try:
        plan = partition(preferred)
    except OverflowError:
        if source_surface:raise
        low, high = preferred, preferred
        for _ in range(8):
            high = min(high*2.,maximum_width)if maximum_width is not None else high*2.
            try:
                plan = partition(high)
                break
            except OverflowError:
                low = high
                if maximum_width is not None and high>=maximum_width:
                    raise ValueError('The declared thin carrier width cannot fit its retained all-frame allocation.')
        else:
            raise ValueError('Curved support cannot retain the declared fine allocation.')
        # A bounded numerical budget solve, not repeated visual candidates.
        for _ in range(8):
            middle = (low + high) * .5
            try:
                candidate = partition(middle)
                high, plan = middle, candidate
            except OverflowError:
                low = middle
    allocated_indices = [i for i, record in enumerate(plan['vertices']) if 'nativeVertex' not in record]
    prepare_geometry_frames(plan,frame_p)
    allocated_records = [plan['vertices'][i] for i in allocated_indices]
    evaluation = dict(plan, vertices=allocated_records)
    if source_surface:
        values=plan['sourceQuadratic'];pp=values['points'].copy();nn=values['normals'];tt=values['tangents']
        fitting_delta=fitted_p-frame_p
        for index,record in enumerate(allocated_records):
            owner=record['geometrySurface'];pp[:,index]-=np.einsum('i,pij->pj',owner['barycentric'],fitting_delta[:,owner['triangle']])
        positions,normals,tangents=pp[0],nn[0],tt[0]
        curvature_proof=dict(allStoredPosesAndBinary32ChannelsChecked=True,sourcePatternQuadratic=True,sourceFittingAppliedInAuthoringOnce=True,
            prefixDeltaSubtractedForExistingFittingOwner=True,normalApproximationAccepted=False,slots=values['proofs'])
        evaluation['tuftDerivativeProof']=dict(mode='source-quadratic',sourceDomainCoverageExact=True,sourceFittingOwner='retain_source_fitting')
    else:
        positions, normals, tangents = evaluate_curved_core(evaluation, frame_p[0], frame_n[0], frame_t[0], coordinates)
        normals=share_core_normals(evaluation,normals)
        tangents=unit(tangents-normals*np.sum(tangents*normals,axis=1)[:,None])
        pp, nn, tt, curvature_proof = bound_curved_core(plan, positions, normals, tangents, frame_p, frame_n, frame_t, frame_b, inverse)
    curvature_proof['unrepresentableNativeFacesRetained']=unrepresentable_native_faces
    if carrier_domain_proof is not None:curvature_proof['retainedSourceDomain']=carrier_domain_proof
    positions, normals, tangents = pp[0], nn[0], tt[0]
    remove = set()
    required_donation=max(0,len(positions)-headroom)
    for _, _, indices in removable:
        if len(remove) >= required_donation:
            break
        remove.update(indices)
    assert len(remove)+headroom >= len(positions)
    keep = np.asarray([i for i in range(current['vertexCount']) if i not in remove], dtype=int)
    assert np.array_equal(keep[:count], np.arange(count))
    mapping = np.full(current['vertexCount'], -1, dtype=int)
    mapping[keep] = np.arange(len(keep))
    packet = copy.deepcopy(current)
    for key, size in (('vertices', 3), ('normals', 3), ('tangents', 4), ('weights', 4), ('boneIndices', 4), ('colors', 4)):
        if packet[key]:
            packet[key] = np.asarray(packet[key]).reshape(-1, size)[keep].ravel().tolist()
    for channel in packet['uv']:
        channel['values'] = np.asarray(channel['values']).reshape(-1, 4)[keep].ravel().tolist()
    for shape in packet['shapes']:
        for frame in shape['frames']:
            for key in ('vertices', 'normals', 'tangents'):
                frame[key] = np.asarray(frame[key]).reshape(-1, 3)[keep].ravel().tolist()
    new_binding = copy.deepcopy(binding)
    new_binding['schemaVersion']=4
    new_binding['vertices'] = [new_binding['vertices'][int(i)] for i in keep]
    first = len(keep)
    allocated_lookup = {index: offset for offset, index in enumerate(allocated_indices)}
    # Trace geometry keeps its source curve. Sample the actual child surface
    # in every stored pose, then translate complete sheets above that surface.
    fine_ids=np.flatnonzero(np.asarray(next(u['values']for u in packet['uv']if u['channel']==2)).reshape(-1,4)[:,2]>=17)
    fine_records=[new_binding['vertices'][int(i)]for i in fine_ids]
    for record in fine_records:record['geometrySurface']=None
    local_matrix=matrix.copy();local_matrix[:3,3]=0
    original_p,original_n,original_t,_=native_frames(packet,local_matrix.T.ravel().tolist())
    target_p=original_p[:,fine_ids].copy();target_n=original_n[:,fine_ids].copy();target_t=original_t[:,fine_ids].copy()
    lookup=np.full(packet['vertexCount'],-1,dtype=int);lookup[fine_ids]=np.arange(len(fine_ids))
    core_p=np.empty((len(frame_p),len(plan['vertices']),3));core_n=np.empty_like(core_p);core_t=np.empty_like(core_p)
    for index,record in enumerate(plan['vertices']):
        if 'nativeVertex'in record:
            source_index=record['nativeVertex'];core_p[:,index]=frame_p[:,source_index];core_n[:,index]=frame_n[:,source_index];core_t[:,index]=frame_t[:,source_index]
        else:
            offset=allocated_lookup[index];core_p[:,index]=pp[:,offset];core_n[:,index]=nn[:,offset];core_t[:,index]=tt[:,offset]
    owners=fine_core_owners(plan)
    for pose in range(len(frame_p)):
        moved,points_,normals_,tangents_=couple_fine_to_core(plan,core_p[pose],core_n[pose],core_t[pose],
            original_p[pose],original_n[pose],original_t[pose],new_binding['vertices'],
            frame_p[pose],frame_n[pose],frame_t[pose],frame_b[pose],owners,require_complete=carrier_settings is None)
        selected=lookup[moved]
        assert np.all(selected>=0),'A curved attachment selected non-fine geometry.'
        target_p[pose,selected]=points_;target_n[pose,selected]=normals_;target_t[pose,selected]=tangents_
    attachment_packet=dict(packet,subMeshes=[[
        int(mapping[index])for triangle in np.asarray(sub,dtype=int).reshape(-1,3)
        if np.all(groom[triangle,2]>=17)and not any(int(i)in remove for i in triangle)
        for index in triangle]for sub in current['subMeshes']])
    fine_p,fine_n,fine_t,fine_frames,fine_bound=bound_fine_attachment(
        attachment_packet,fine_ids,fine_records,target_p[0],target_n[0],target_t[0],
        frame_p,frame_n,frame_t,frame_b,linear,(target_p,target_n,target_t))
    fine_pp,fine_nn,fine_tt=fine_frames
    for index, record, point, normal, tangent in zip(fine_ids, fine_records, fine_p, fine_n, fine_t):
        anchor = record['anchor']
        basis = np.asarray((frame_t[0, anchor], frame_b[0, anchor], frame_n[0, anchor]))
        record.update(offset=(basis @ (point-frame_p[0, anchor])).tolist(), normal=(basis @ normal).tolist(), tangent=(basis @ tangent).tolist())
    for key, values in (('vertices', fine_pp[0]), ('normals', fine_nn[0]), ('tangents', fine_tt[0])):
        size = 4 if key == 'tangents' else 3
        channel = np.asarray(packet[key]).reshape(-1, size)
        channel[fine_ids, :3] = values
        packet[key] = channel.ravel().tolist()
    frame_index = 1
    for shape in packet['shapes']:
        for frame in shape['frames']:
            for key, values in (('vertices', fine_pp), ('normals', fine_nn), ('tangents', fine_tt)):
                channel = np.asarray(frame[key]).reshape(-1, 3)
                channel[fine_ids] = values[frame_index] - values[0]
                frame[key] = channel.ravel().tolist()
            frame_index += 1
    fine_attachment = dict(vertices=len(fine_ids), allPhysicalFrames=len(frame_p),
        maximumRestDisplacementMetres=float(np.linalg.norm(fine_p-original_p[0,fine_ids], axis=1).max()) if len(fine_ids) else 0.,
        sourceTriangleProvenanceUnchanged=True, sourceUvAndGroomPhaseUnchanged=True,
        runtimeTriangleFrameTransport=False,storedPoseTransport="whole-sheet-rigid-clearance",windingBound=fine_bound)
    core_rows = []
    for record, point, normal, tangent in zip(allocated_records, positions, normals, tangents):
        triangle = np.asarray(record['triangle'], dtype=int)
        bary = np.asarray(record['barycentric'], dtype='<f4').astype(float)
        anchor = int(triangle[np.argmax(bary)])
        delta = point - frame_p[0, anchor]
        basis = np.asarray([frame_t[0, anchor], frame_b[0, anchor], frame_n[0, anchor]])
        core_rows.append(dict(anchor=anchor, native=False, role='volume-support', slot=record['slot'],
                              uv0=np.asarray(bary @ source_uv[triangle], dtype='<f4').tolist(),
                              offset=(basis @ delta).tolist(), normal=(basis @ normal).tolist(), tangent=(basis @ tangent).tolist(),
                              tangentW=float(native_hand[anchor]*np.sign(np.linalg.det(linear))),
                              surface=dict(triangle=triangle.tolist(), barycentric=bary.tolist()),
                              geometrySurface=record['geometrySurface']))
    anchors = np.asarray([r['anchor'] for r in core_rows], dtype=int)
    pp, nn, tt = pp @ inverse.T, unit(nn @ linear), unit(tt @ inverse.T)
    tt = unit(tt - nn * np.sum(tt * nn, axis=2)[:, :, None])
    for key, values in (('vertices', pp[0]), ('normals', nn[0]), ('tangents', np.c_[tt[0], native_hand[anchors]])):
        packet[key] += values.ravel().tolist()
    skin=[source_surface_skin(native,row['surface'])for row in core_rows]
    for key in ('weights', 'boneIndices', 'colors'):
        if native[key]:
            values=([row[0 if key=='weights'else 1]for row in skin]if key!='colors'else np.asarray(native[key]).reshape(-1,4)[anchors])
            packet[key]+=np.asarray(values).ravel().tolist()
    for channel in packet['uv']:
        original = next((u for u in native['uv'] if u['channel'] == channel['channel']), None)
        values = np.asarray(original['values']).reshape(-1, 4)[anchors].copy() if original else np.zeros((len(anchors), 4))
        if channel['channel'] == 0:
            values[:, :2] = [r['uv0'] for r in core_rows]
        if channel['channel'] == 2:
            values[:, :2] = [np.asarray(r['barycentric']) @ source_coordinates[np.asarray(r['triangle'])] / .01 for r in allocated_records]
            values[:, 2], values[:, 3] = 0., groom[anchors,3]
        channel['values'] += values.ravel().tolist()
    frame_index = 1
    for shape in packet['shapes']:
        for frame in shape['frames']:
            for key, values in (('vertices', pp[frame_index] - pp[0]), ('normals', nn[frame_index] - nn[0]), ('tangents', tt[frame_index] - tt[0])):
                frame[key] += values.ravel().tolist()
            frame_index += 1
    new_binding['vertices'] += core_rows
    core_counts = []
    render_map = np.empty(len(plan['vertices']), dtype=int)
    for index, record in enumerate(plan['vertices']):
        if 'nativeVertex' in record:
            render_map[index] = record['nativeVertex']
    render_map[allocated_indices] = np.arange(first, first + len(anchors))
    for slot, source_indices in enumerate(current['subMeshes']):
        retained = []
        for triangle in np.asarray(source_indices, dtype=int).reshape(-1, 3):
            if any(int(i) in remove for i in triangle):
                assert all(int(i) in remove for i in triangle), 'Only complete fine curves can be reassigned.'
                continue
            if np.all(triangle < count) and tuple(sorted(map(int, triangle))) in selected_keys[slot]:
                continue
            retained.extend(mapping[triangle].tolist())
        core = render_map[np.asarray(plan['triangles'][slot], dtype=int).reshape(-1, 3)]
        packet['subMeshes'][slot] = retained + core.ravel().tolist()
        core_counts.append(len(core))
    packet['vertexCount'] = len(keep) + len(anchors)
    assert packet['vertexCount'] <= maximum_count < 65536
    before = np.asarray(current['bounds'])
    low, high = before[:3] - before[3:] * .5, before[:3] + before[3:] * .5
    if len(anchors):
        low, high = np.minimum(low, pp.min(axis=(0, 1))), np.maximum(high, pp.max(axis=(0, 1)))
    if len(fine_ids):
        low, high = np.minimum(low, fine_pp.min(axis=(0, 1))), np.maximum(high, fine_pp.max(axis=(0, 1)))
    packet['bounds'] = np.r_[(low + high) * .5, high - low].tolist()
    winding = discard_invalid_serialized_curves(packet, new_binding, count)
    first = packet['vertexCount'] - len(anchors)
    packet['sourceFingerprint'] = fingerprint(packet)[0]
    packet_path = save_groom_packet(output, entry, packet)
    binding_path = output / Path(entry['bindingPath']).name
    prepare_surface_binding(new_binding, native)
    encode_binding(new_binding, binding_path)
    fine_count = int(np.sum(np.asarray(next(u['values'] for u in packet['uv'] if u['channel'] == 2)).reshape(-1, 4)[:, 2] >= 17))
    replacement = dict(firstVertex=first, vertices=len(anchors), sourceFacesBySlot=[np.flatnonzero(s).tolist() for s in selected_faces],
                       parentFacesBySlot=plan['parents'], triangleCountsBySlot=core_counts,
                       widthMetres=plan['widthMetres'], maximumSourceAreaError=plan['maximumSourceAreaError'],
                       reusedNativeVertices=plan['reusedNativeVertices'],
                       curvatureProof=curvature_proof, frameTransport='runtime-triangle-interpolated-orthonormal-basis',
                       tuftDerivativeProof=evaluation['tuftDerivativeProof'],
                       physicalMetricCharts=metric_report,
                       removedWholeFineVertices=len(remove), retainedFineVertices=fine_count,
                       fineSurfaceAttachment=fine_attachment,
                       serializedFineWinding=winding,
                       geometryAdjacency='equal-skin-and-all-physical-position-frames',
                       geometryGroups=int(physical_groups.max()+1),physicalCurvatureGroups=plan['curvatureGroupCount'],
                       preferredFineAllocationFraction=fraction,unusedAddedVertexCapacity=headroom,minimumCoreVertexBudget=minimum_core,
                       retainedSourceSupportVertices=retained_support,maximumStoredVertices=maximum_count,
                       smallFacesKeepSourceResolutionForCapacity=adaptive_resolution,
                       nativeSourceCornersSharedForCapacity=shared_native_corners,
                       sourceGroomCoordinatesRetained=True,
                       nativePrefixImmutable=True, nativeRenderFacesRestored=False,
                       nativeAlphaMetalAndPaletteControlUnchanged=True)
    if carrier_settings is not None:
        replacement.update(flowCarrierCells=plan['flowCarrierCells'],flowGuideProof=plan['flowGuideProof'],
            protectedNativeVertexIds=np.flatnonzero(protected).tolist(),nativeFingerprint=native['sourceFingerprint'])
        if source_surface:
            replacement['frameTransport']='source-correspondence-stored-fits'
            replacement['sourceFlow']=dict(directions=unit(plan['sourceQuadratic']['flow']@inverse.T).tolist(),authority=dict(
                source=[dict(slot=value['slot'],**value['sourceMain'])for value in plan['sourceQuadratic']['proofs']],uvChannel=0,
                scale={str(value['slot']):value['sourceMain']['scale']for value in plan['sourceQuadratic']['proofs']},
                offset={str(value['slot']):value['sourceMain']['offset']for value in plan['sourceQuadratic']['proofs']},
                provenance=dict(nativeFingerprint=native['sourceFingerprint'],sourceFacesBySlot=replacement['sourceFacesBySlot'],
                    model='source-Main-luminance-structure-tensor',samplers=[material.get('sourceTextureSamplers',{})for material in entry['materials']])))
        else:
            replacement.update(carrierGuideIds=[record['flowCarrier']['guide']if record['flowCarrier']is not None else-1 for record in allocated_records],
                carrierTerminalDistanceMetres=[float(np.asarray(record['barycentric'])@carrier_terminal['terminalDistance'][record['triangle']])for record in allocated_records],
                terminalRegionMetres=contact['settings']['terminalRegionLengthMetres'])
    # Keep input authorities, not obsolete per-slot counts or earlier winding
    # results from the fine-ribbon generation that donated this allocation.
    result = {key: value for key, value in entry.items() if key not in
              ('serializedWinding', 'zeroFineReason', 'slots', 'coreCoordinateStatistics', 'nativeCoreRepair',
               'nativeUvSeamsSplitAtSharedSourceEdges', 'sourceUvChordErrorTexels')}
    return dict(result, generatedPacket=str(packet_path), generatedSha256=sha(packet_path), bindingPath=str(binding_path), bindingSha256=sha(binding_path),
                authoredFingerprint=packet['sourceFingerprint'], compactedVertices=packet['vertexCount'], addedVertices=fine_count + len(anchors),
                addedVertexBytes=(fine_count + len(anchors)) * entry['packedVertexStrideBytes'],
                addedPhysicalDeltaBytes=(fine_count + len(anchors)) * entry['physicalFrameCostPerNewVertexBytes'],
                finalTriangles=sum(len(s) // 3 for s in packet['subMeshes']), curvedCoreSurface=replacement,
                broadNativeOuterSheetReplaced=True, nativeReferenceVertices=count, sourceCurrentVertices=current['vertexCount'],
                nativeRootCoreAndOrnamentsRetained=False, nativeReferencePrefixAndOrnamentFacesRetained=True,
                nativeCoreRepair=dict(nativeChannelAuthority=True, nativeRenderFacesRestored=False, explicitCurvedPartition=True),
                retainedBudget=carrier_budget if carrier_budget is not None else entry.get('retainedBudget'),
                addedVertexLimit=added_limit,renderedArtAccepted=False, bindingRuntimeValidationPending=True)


def validate_curved_core(row, packet, binding, native):
    replacement = row['curvedCoreSurface']
    if replacement.get('flowCarrierCells',{}).get('model')=='primary-loft':
        return validate_primary_core(row,packet,binding,native)
    first, count = replacement['firstVertex'], replacement['vertices']
    assert first + count == packet['vertexCount']
    assert all(v['role'] == 'volume-support' and not v['native'] for v in binding['vertices'][first:])
    maximum_area_error, children, minimum_orientation = 0., 0, float('inf')
    for slot, native_indices in enumerate(native['subMeshes']):
        source = np.asarray(native_indices, dtype=int).reshape(-1, 3)
        selected = set(replacement['sourceFacesBySlot'][slot])
        indices = np.asarray(packet['subMeshes'][slot], dtype=int).reshape(-1, 3)
        core_count = replacement['triangleCountsBySlot'][slot]
        retained = indices[:-core_count] if core_count else indices
        prefix = retained[np.all(retained < native['vertexCount'], axis=1)]
        expected = source[[face not in selected for face in range(len(source))]]
        assert np.array_equal(prefix, expected), 'Only declared native core render faces may be replaced.'
        core = indices[-core_count:] if core_count else np.empty((0, 3), dtype=int)
        parents = np.asarray(replacement['parentFacesBySlot'][slot], dtype=int)
        assert len(core) == len(parents) == replacement['triangleCountsBySlot'][slot]
        assert set(map(int, parents)) == selected
        area = collections.defaultdict(float)
        edges = collections.defaultdict(collections.Counter)
        for triangle, parent in zip(core, parents):
            corners = source[parent]
            bary = []
            for index in triangle:
                vertex = binding['vertices'][int(index)]
                sample = {int(index): 1.} if index < native['vertexCount'] else dict(zip(vertex['surface']['triangle'], vertex['surface']['barycentric']))
                assert all(abs(value) <= 1e-6 for key, value in sample.items() if key not in corners)
                weights = np.asarray([sample.get(int(corner), 0.) for corner in corners])
                assert abs(weights.sum() - 1.) <= 1e-6 and np.all(weights >= -1e-6)
                bary.append(weights)
            bary = np.asarray(bary)
            signed = float(np.linalg.det(np.stack((bary[1, 1:] - bary[0, 1:], bary[2, 1:] - bary[0, 1:])))) * .5
            assert signed > 0., 'Core partition reversed its native parameter domain.'
            area[int(parent)] += signed
            # Separate terminal locks may share a source parameter sample while
            # owning distinct positions and shading vertices. Pair the source
            # partition in that domain; all-fit physical orientation is checked
            # independently below.
            keys=[tuple(np.asarray(weights,dtype='<f4'))for weights in bary]if replacement.get('flowCarrierCells')else list(map(int,triangle))
            for a,b in zip(keys,keys[1:]+keys[:1]):
                edges[int(parent)][tuple(sorted((a,b)))] += 1
        for parent in selected:
            error = abs(area[parent] - .5)
            assert error <= 1e-5, 'Wire-precision core partition lost native coverage.'
            maximum_area_error = max(maximum_area_error, error)
            assert all(n in (1, 2) for n in edges[parent].values()), 'Nonmanifold core partition.'
            corners = source[parent]
            for edge, frequency in edges[parent].items():
                if frequency == 2:
                    continue
                samples = []
                for index in edge:
                    if replacement.get('flowCarrierCells'):
                        samples.append(index);continue
                    sample = binding['vertices'][index]['surface']
                    weights = {int(index): 1.} if index < native['vertexCount'] else dict(zip(sample['triangle'], sample['barycentric']))
                    samples.append([weights.get(int(corner), 0.) for corner in corners])
                assert np.any(np.max(np.abs(samples), axis=0) <= 1e-6), 'An unpaired edge is not on the source boundary.'
        poses = [(None, None)]
        native_frames_by_key = [(s['name'], f) for s in native['shapes'] for f in s['frames']]
        authored_frames = [(s['name'], f) for s in packet['shapes'] for f in s['frames']]
        assert len(native_frames_by_key) == len(authored_frames)
        for (native_name, native_frame), (authored_name, authored_frame) in zip(native_frames_by_key, authored_frames):
            assert native_name == authored_name and native_frame['weight'] == authored_frame['weight']
            poses.append((native_frame, authored_frame))
        for native_frame, authored_frame in poses:
            source_points = np.asarray(native['vertices'], dtype='<f4').reshape(-1, 3)
            points = np.asarray(packet['vertices'], dtype='<f4').reshape(-1, 3)
            if native_frame is not None:
                assert authored_frame is not None
                source_points = (source_points + np.asarray(native_frame['vertices'], dtype='<f4').reshape(-1, 3)).astype('<f4')
                points = (points + np.asarray(authored_frame['vertices'], dtype='<f4').reshape(-1, 3)).astype('<f4')
            source_points, points = source_points.astype(float), points.astype(float)
            parent_points = source_points[source[parents]]
            child_points = points[core]
            before = np.cross(parent_points[:, 1] - parent_points[:, 0], parent_points[:, 2] - parent_points[:, 0])
            after = np.cross(child_points[:, 1] - child_points[:, 0], child_points[:, 2] - child_points[:, 0])
            orientation = np.sum(before * after, axis=1)
            assert np.all(orientation > 0.), 'Serialized curved core inverted or collapsed a physical source face.'
            if len(orientation):minimum_orientation = min(minimum_orientation, float(orientation.min()))
        children += len(core)
    return dict(status='PASS_EXPLICIT_CORE_PARTITION_AND_ALL_STORED_PHYSICAL_FRAMES',
                children=children, maximumWireSourceAreaError=maximum_area_error,
                minimumChildParentOrientation=minimum_orientation,
                sourceBoundariesPaired=True, originalNativeReferencePrefixRetained=True)


def restore_native_prefix_indices(packet,native):
    """Restore original oriented native faces only after multiset identity."""
    count=native['vertexCount'];rows=[]
    assert len(packet['subMeshes'])==len(native['subMeshes'])
    for slot,(target,source)in enumerate(zip(packet['subMeshes'],native['subMeshes'])):
        triangles=np.asarray(target,dtype=int).reshape(-1,3);original=np.asarray(source,dtype=int).reshape(-1,3);mask=np.all(triangles<count,axis=1);current=triangles[mask]
        target_multiset=collections.Counter(tuple(sorted(map(int,t)))for t in current)
        original_multiset=collections.Counter(tuple(sorted(map(int,t)))for t in original)
        assert len(current)==len(original)and target_multiset==original_multiset,('Native prefix face count/multiplicity differs; no restoration is allowed.',slot,len(current),len(original))
        changed=int(np.any(current!=original,axis=1).sum());rows.append(dict(slot=slot,nativeTriangles=len(original),changedOrientedFaces=changed,unorientedMultiplicityExact=True,ownedFacesUnchanged=True))
        if changed:
            triangles[mask]=original;packet['subMeshes'][slot]=triangles.ravel().tolist()
    return dict(status='PASS_NATIVE_ORIGINAL_INDEX_SEQUENCE_RESTORED',changedOrientedFaces=sum(r['changedOrientedFaces']for r in rows),rows=rows,sourcePntUvSkinPhysicalFramesUntouched=True,currentProductOrientationPreserved=all(r['changedOrientedFaces']==0 for r in rows))

def restore_native_core(packet,native,binding,restore_geometry):
    """Restore actual source samples; do not treat native hair cards as a volume.

    The distinct basis control changes core tangent W/UV2 W only. The geometry
    control additionally reuses native position/skin/all physical samples and
    honestly declares those vertices native in the existing v1 attachment.
    New fibers, source UV0 and the repaired UV2 XY field stay byte-exact.
    """
    assert len(binding['vertices'])==packet['vertexCount']
    groom=next(u for u in packet['uv']if u['channel']==2)
    values=np.asarray(groom['values'],dtype='<f4').reshape(-1,4).copy()
    # Z0 can also belong to an untouched native armor/body material slot.
    # Only the existing authored hair-support responsibility is repaired.
    support=np.array([v['role']in('volume-support','thin-root')for v in binding['vertices']])
    indices=np.flatnonzero((values[:,2]==0)&support)
    assert len(indices)
    anchors=np.asarray([binding['vertices'][int(i)]['anchor']for i in indices],dtype=np.intp)
    source_tangents=np.asarray(native['tangents'],dtype='<f4').reshape(-1,4)
    assert np.isin(source_tangents[anchors,3],[-1,1]).all()
    tangent=np.asarray(packet['tangents'],dtype='<f4').reshape(-1,4).copy()
    assert np.array_equal(tangent[indices,:3],source_tangents[anchors,:3])
    before_w=tangent[indices,3].copy();before_groom=values[indices,3].copy()
    # Owned cloth vertices must keep the worker's target tangentW=-1. The
    # shader's owned handedness ratio is the correction, not a frame mutation.
    if restore_geometry:
        tangent[indices,3]=source_tangents[anchors,3]
        values[indices,3]=1
        packet['tangents']=tangent.ravel().tolist()
    else:
        assert np.isin(tangent[indices,3],[-1,1]).all()
        values[indices,3]=source_tangents[anchors,3]/tangent[indices,3]
    groom['values']=values.ravel().tolist()
    sign=float(np.sign(np.linalg.det(np.asarray(binding['matrix']).reshape(4,4).T[:3,:3])))
    if restore_geometry:
        for index,anchor in zip(indices,anchors):
            vertex=binding['vertices'][int(index)]
            vertex['tangentW']=float(source_tangents[anchor,3])*sign
    if restore_geometry:
        assert np.array_equal(np.asarray(packet['bindPoses'],dtype='<f4'),np.asarray(native['bindPoses'],dtype='<f4'))
        for key,dimension in(('vertices',3),('normals',3),('tangents',4),('weights',4),('boneIndices',4),('colors',4)):
            if not packet[key] and not native[key]:continue
            assert packet[key] and native[key],key
            target=np.asarray(packet[key],dtype='<f4'if key!='boneIndices'else'<i4').reshape(-1,dimension).copy()
            source=np.asarray(native[key],dtype=target.dtype).reshape(-1,dimension)
            target[indices]=source[anchors];packet[key]=target.ravel().tolist()
        # Source UV0 is already owned by the exact native anchor. Do not replace
        # it silently if a different projection entered the authoring pipeline.
        for channel in packet['uv']:
            if channel['channel']==2:continue
            original=next((u for u in native['uv']if u['channel']==channel['channel']),None)
            if original is None:continue
            a=np.asarray(channel['values'],dtype='<f4').reshape(-1,4)[indices]
            b=np.asarray(original['values'],dtype='<f4').reshape(-1,4)[anchors]
            assert np.array_equal(a,b),('native-core-uv',channel['channel'])
        assert len(packet['shapes'])==len(native['shapes'])
        for target_shape,source_shape in zip(packet['shapes'],native['shapes']):
            assert target_shape['name']==source_shape['name']and len(target_shape['frames'])==len(source_shape['frames'])
            for target_frame,source_frame in zip(target_shape['frames'],source_shape['frames']):
                assert np.float32(target_frame['weight'])==np.float32(source_frame['weight'])
                for key in('vertices','normals','tangents'):
                    target=np.asarray(target_frame[key],dtype='<f4').reshape(-1,3).copy()
                    source=np.asarray(source_frame[key],dtype='<f4').reshape(-1,3)
                    target[indices]=source[anchors];target_frame[key]=target.ravel().tolist()
        for index in indices:
            binding['vertices'][int(index)].update(native=True,offset=[0.,0.,0.],normal=[0.,0.,1.],tangent=[1.,0.,0.])
    assert np.array_equal(tangent[indices,3]*values[indices,3],source_tangents[anchors,3])
    native_index_repair=restore_native_prefix_indices(packet,native)if restore_geometry else None
    return dict(nativeIndexRepair=native_index_repair,coreVertices=len(indices),changedCoreTangentW=int(np.sum(before_w!=source_tangents[anchors,3])),
        changedCoreGroomW=int(np.sum(before_groom!=values[indices,3])),restoreGeometry=restore_geometry,
        coreNativeChannelAuthority=bool(restore_geometry),nativeFlagDeclaredOnlyForExactRestoredSamples=bool(restore_geometry),
        fieldUv2XYAndNativeUv0Preserved=True,allFineGeometryAndPhysicalSamplesUnchanged=True)


def core_repair_from(generation_path,output,geometry):
    source=read(generation_path);rows=[]
    assert source['rows'] and len({r['manifestEntry']['sourceFingerprint']for r in source['rows']})==len(source['rows'])
    for entry in source['rows']:
        assert sha(entry['generatedPacket'])==entry['generatedSha256']and sha(entry['nativePacketPath'])==entry['nativePacketSha256']and sha(entry['bindingPath'])==entry['bindingSha256']
        original=read(entry['generatedPacket']);packet=copy.deepcopy(original);native=read(entry['nativePacketPath']);binding=decode_binding(entry['bindingPath'])
        report=restore_native_core(packet,native,binding,geometry)
        points=np.asarray(packet['vertices']).reshape(-1,3);low=points.min(axis=0);high=points.max(axis=0)
        for shape in packet['shapes']:
            for frame in shape['frames']:
                value=points+np.asarray(frame['vertices']).reshape(-1,3);low=np.minimum(low,value.min(axis=0));high=np.maximum(high,value.max(axis=0))
        packet['bounds']=np.r_[(low+high)*.5,high-low].tolist()
        packet['sourceFingerprint']=fingerprint(packet)[0]
        path=output/Path(entry['generatedPacket']).name;path.write_text(json.dumps(packet,separators=(',',':')),encoding='utf-8')
        surface_provenance=prepare_surface_binding(binding,native)
        binding_path=output/Path(entry['bindingPath']).name;encode_binding(binding,binding_path)
        row=dict(entry,generatedPacket=str(path),generatedSha256=sha(path),bindingPath=str(binding_path),bindingSha256=sha(binding_path),
            authoredFingerprint=packet['sourceFingerprint'],nativeCoreRepair=report,surfaceProvenance=surface_provenance,renderedArtAccepted=False)
        rows.append(row);print(entry['asset'],entry['lod'],report['changedCoreTangentW'],flush=True)
    result=dict(status='PRIVATE_NATIVE_CORE_GEOMETRY_AND_BASIS_REPAIR_NOT_RENDERED'if geometry else'PRIVATE_NATIVE_CORE_BASIS_ONLY_REPAIR_NOT_RENDERED',rows=rows,
        sourceGeneration=str(generation_path),sourceGenerationSha256=sha(generation_path),generatorPath=str(Path(__file__).resolve()),generatorSha256=sha(Path(__file__)),
        densityChanged=False,uvChartPilotMixed=False,uv2CoordinateFieldRetained=True,productChanged=False,artAccepted=False)
    write_generation(output,result)

def remove_extra_native_core_triangles(packet,native,binding):
    """Keep the exact original prefix topology; remove only redundant core copies.

    Source UV seams, hard normals, winding, submesh boundaries and intentional
    source duplicate faces are retained by their actual native index sequence.
    No vertex, UV, skin, physical frame, bounds or binding data changes here.
    """
    count=native['vertexCount'];g=np.asarray(next(u for u in packet['uv']if u['channel']==2)['values'],dtype='<f4').reshape(-1,4)
    assert len(g)==packet['vertexCount']==len(binding['vertices'])
    assert np.all(g[:count,2]==0)and np.all(g[:count,3]==1)
    assert all(v['native']and v['anchor']==i for i,v in enumerate(binding['vertices'][:count]))
    rows=[];result=[]
    for slot,(source,indices)in enumerate(zip(native['subMeshes'],packet['subMeshes'])):
        triangles=np.asarray(indices,dtype=np.int64).reshape(-1,3);roles=g[triangles,2];assert np.all(roles==roles[:,:1])
        core=roles[:,0]==0;prefix=np.all(triangles<count,axis=1)
        original=triangles[core&prefix].ravel().tolist()
        assert original==source,('Original native prefix triangle order/multiplicity differs',slot)
        remove=core&~prefix
        # The independently supplied all-stream proof has already established
        # these copies share native anchors, PNT/UV0/skin and every frame.
        source_keys=collections.Counter(tuple(sorted(t))for t in np.asarray(source).reshape(-1,3))
        for t in triangles[remove]:
            assert tuple(sorted(binding['vertices'][int(i)]['anchor']for i in t))in source_keys
        kept=triangles[~remove].ravel().tolist();result.append(kept)
        assert [int(i)for t in triangles[~remove]if np.all(g[t,2]==0)for i in t]==source
        rows.append(dict(slot=slot,originalNativeCoreTriangles=len(source)//3,priorCoreTriangles=int(core.sum()),removedExtraCoreTriangles=int(remove.sum()),retainedCoreTriangles=len(source)//3,nativeOriginalIndicesOrderAndMultiplicityExact=True,allNonCoreIndicesOrderExact=True))
    assert len(native['subMeshes'])==len(packet['subMeshes'])
    packet['subMeshes']=result
    return rows

def core_multiplicity_from(generation_path,proof_path,output):
    source=read(generation_path);proof=read(proof_path)
    assert proof['status']=='PASS_ALL18_EXTRA_CORE_COPIES_IDENTICAL_ALL_DEFORMATION_STREAMS'
    assert proof['generation']['sha256']==sha(generation_path)
    authority={(r['asset'],r['lod']):r for r in proof['rows']};rows=[]
    for entry in source['rows']:
        evidence=authority[(entry['asset'],entry['lod'])]
        assert evidence['allDeformationStreamsExact']and evidence['sourcePointer']['authoredSha256']==entry['generatedSha256']
        for key,digest in(('generatedPacket','generatedSha256'),('nativePacketPath','nativePacketSha256'),('bindingPath','bindingSha256')):assert sha(entry[key])==entry[digest]
        original=read(entry['generatedPacket']);packet=copy.deepcopy(original);native=read(entry['nativePacketPath']);binding=decode_binding(entry['bindingPath'])
        audit=remove_extra_native_core_triangles(packet,native,binding)
        assert all(packet[k]==original[k]for k in original if k not in('subMeshes','sourceFingerprint'))
        packet['sourceFingerprint']=fingerprint(packet)[0]
        path=output/Path(entry['generatedPacket']).name;path.write_text(json.dumps(packet,separators=(',',':')),encoding='utf-8')
        binding_path=output/Path(entry['bindingPath']).name;binding_path.write_bytes(Path(entry['bindingPath']).read_bytes());assert sha(binding_path)==entry['bindingSha256']
        row=dict(entry,generatedPacket=str(path),generatedSha256=sha(path),bindingPath=str(binding_path),authoredFingerprint=packet['sourceFingerprint'],coreMultiplicityRepair=audit,verticesFramesUvSkinAndBindingUnchanged=True,geometryChangeScope='Only excess native-oriented core triangle index copies removed; all vertices and all fine-role indices retain exact bytes.',renderedArtAccepted=False)
        rows.append(row);print(entry['asset'],entry['lod'],sum(r['removedExtraCoreTriangles']for r in audit),flush=True)
    result=dict(status='PRIVATE_NATIVE_CORE_MULTIPLICITY_ONLY_CONTROL_NOT_RENDERED',rows=rows,sourceGeneration=str(generation_path),sourceGenerationSha256=sha(generation_path),deformationProof=dict(path=str(proof_path),sha256=sha(proof_path)),generatorPath=str(Path(__file__).resolve()),generatorSha256=sha(Path(__file__)),shaderChanged=False,alphaChanged=False,densityChanged=False,uvChartPilotMixed=False,unusedVertexCompactionApplied=False,allVertexUvSkinPhysicalFrameAndBindingBytesUnchanged=True,productChanged=False,artAccepted=False)
    write_generation(output,result)



def remove_fine_vertices(packet,binding,native_count,rejected_vertices):
    """Remove complete generated curves without altering retained source data."""
    assert rejected_vertices and min(rejected_vertices)>=native_count
    for slot,sub in enumerate(packet['subMeshes']):
        tri=np.asarray(sub,dtype=int).reshape(-1,3);keep=np.array([not any(int(i)in rejected_vertices for i in t)for t in tri])
        packet['subMeshes'][slot]=tri[keep].ravel().tolist()
    used=np.union1d(np.arange(native_count),np.unique(np.concatenate(packet['subMeshes'])));mapping=np.full(packet['vertexCount'],-1,int);mapping[used]=np.arange(len(used))
    for key,width in(('vertices',3),('normals',3),('tangents',4),('weights',4),('boneIndices',4),('colors',4)):
        if packet[key]:packet[key]=np.asarray(packet[key]).reshape(-1,width)[used].ravel().tolist()
    for channel in packet['uv']:channel['values']=np.asarray(channel['values']).reshape(-1,4)[used].ravel().tolist()
    for shape in packet['shapes']:
        for frame in shape['frames']:
            for key in('vertices','normals','tangents'):frame[key]=np.asarray(frame[key]).reshape(-1,3)[used].ravel().tolist()
    packet['subMeshes']=[mapping[np.asarray(sub,int)].tolist()for sub in packet['subMeshes']];binding['vertices']=[binding['vertices'][int(i)]for i in used]
    removed=packet['vertexCount']-len(used);packet['vertexCount']=len(used);packet['sourceFingerprint']=fingerprint(packet)[0]
    return removed


def discard_invalid_serialized_curves(packet,binding,native_count,reports=None):
    """Apply the existing prover to exactly the final local binary32 streams.

    Source indexed core/legacy geometry is untouched. A generated seed owns
    one slot/role/rank tuple, including any disconnected UV-chart pieces.
    Only a complete invalid generated curve is removed; no face is flipped.
    """
    uv=np.asarray(next(u['values']for u in packet['uv']if u['channel']==2),dtype='<f4').reshape(-1,4)
    groups={};triangles=[];triangle_groups=[];slots=[]
    for slot,sub in enumerate(packet['subMeshes']):
        for triangle in np.asarray(sub,dtype=int).reshape(-1,3):
            if not np.all(uv[triangle,2]>=17):continue
            assert np.all(uv[triangle,2:]==uv[triangle[0],2:])
            key=(slot,struct.pack('<f',float(uv[triangle[0],2])),struct.pack('<f',float(uv[triangle[0],3])))
            groups.setdefault(key,set()).update(map(int,triangle));triangles.append(triangle);triangle_groups.append(key);slots.append(slot)
    if reports is not None:
        assert len(groups)==sum(r.get('acceptedCurvedRibbons',0)for r in reports),'Generated seed role/rank provenance is ambiguous.'
    owned=np.asarray(triangles,dtype=int).reshape(-1,3);bad=set();minimum=float('inf')
    if not len(owned):
        assert not groups and not np.any(uv[:,2]>=17)
        return dict(predicate='Existing prove_row on declared empty fine set only.',testedCurves=0,rejectedCurves=0,minimumBefore=None,signedAreaSampleCount=0,thresholdRelaxed=False,indexFlips=0)

    for frame in [None]+[f for s in packet['shapes']for f in s['frames']]:
        p=np.asarray(packet['vertices'],dtype='<f4').reshape(-1,3).astype(float);n=np.asarray(packet['normals'],dtype='<f4').reshape(-1,3).astype(float)
        if frame is not None:p+=np.asarray(frame['vertices'],dtype='<f4').reshape(-1,3);n+=np.asarray(frame['normals'],dtype='<f4').reshape(-1,3)
        sign=np.sum(np.cross(p[owned[:,1]]-p[owned[:,0]],p[owned[:,2]]-p[owned[:,0]])*n[owned].mean(axis=1),axis=1)
        minimum=min(minimum,float(sign.min()))
        bad.update(triangle_groups[int(i)]for i in np.flatnonzero(sign<=0))
    result=dict(predicate='Existing prove_row: local binary32 base converted to double plus binary32 delta, signed area dot mean normal >0.',testedCurves=len(groups),rejectedCurves=len(bad),minimumBefore=minimum,thresholdRelaxed=False,indexFlips=0)
    if not bad:return result
    rejected_vertices=set().union(*(groups[key]for key in bad))
    result['removedFineVertices']=remove_fine_vertices(packet,binding,native_count,rejected_vertices)
    for report in reports or []:
        rejected=sum(key[0]==report['slot']for key in bad)
        if not rejected:continue
        report['preSerializationSamplingStatistics']={key:report[key]for key in('acceptedCurvedRibbons','meanAcceptedCurveLengthMetres','maximumAcceptedCurveLengthMetres','meanAcceptedNodes')}
        report['acceptedCurvedRibbons']-=rejected;report['rejected']['serialized-full-physical-frame-winding']=rejected
        for key in('meanAcceptedCurveLengthMetres','maximumAcceptedCurveLengthMetres','meanAcceptedNodes'):report[key]=None
    return result

def retained_groom_reference(entry,native):
    """Read the actual fitting/carrier from its existing pinned source owner."""
    import character_resources as resources
    from build_models import export_current_mesh
    capacity=entry['retainedCapacityAuthority']
    if sha(capacity['path'])!=capacity['sha256']:raise ValueError('The retained groom capacity source changed.')
    _,file=resources.load_serialized_bundle(Path(capacity['path']),in_memory=True)
    packet=export_current_mesh(file.objects[int(capacity['meshPathId'])])
    if sha(capacity['path'])!=capacity['sha256']:raise ValueError('The retained groom source changed while it was read.')
    if packet['vertexCount']!=capacity['maximumStoredVertices']:raise ValueError('The retained groom capacity declaration differs from its actual source mesh.')
    validate_retained_groom_source(native,packet)
    return packet


def retained_groom_capacity(entry,native,*,fine_only=False,reference=None):
    reference=retained_groom_reference(entry,native)if reference is None else reference
    if fine_only:
        roles=np.asarray(next(row['values']for row in reference['uv']if row['channel']==2)).reshape(-1,4)[:,2]
        return native['vertexCount']+int(np.sum((roles>=17)&(roles<64.5)))
    return reference['vertexCount']


def retained_groom_budget(native,reference,*,fine_only):
    """Reserve the source LOD's real vertex/frame/index allocation for its replacement."""
    count=reference['vertexCount'];index_format=reference['indexFormat']
    if index_format not in(0,1)or count< native['vertexCount']:
        raise ValueError('The retained groom has an unsupported source index allocation.')
    roles=np.asarray(next(row['values']for row in reference['uv']if row['channel']==2)).reshape(-1,4)[:,2]
    fine=(roles>=17)&(roles<64.5)
    if fine_only:
        vertices=int(fine.sum());kept=count-vertices;indices=0
        for sub in reference['subMeshes']:
            triangles=np.asarray(sub,int).reshape(-1,3)
            mixed=np.any(fine[triangles],axis=1)&~np.all(fine[triangles],axis=1)
            if mixed.any():raise ValueError('A source index allocation mixes fine and retained carrier ownership.')
            indices+=int(np.all(fine[triangles],axis=1).sum())*3
    else:
        kept=native['vertexCount'];vertices=count-kept
        indices=max(0,sum(map(len,reference['subMeshes']))-sum(map(len,native['subMeshes'])))
    frames=sum(len(shape['frames'])for shape in reference['shapes'])
    stride=(3+3+4+4+sum(row['dimension']for row in reference['uv'])+(4 if reference['colors']else 0))*4+16
    vertex_cost=stride+frames*36;source_index_bytes=2 if index_format==0 else 4
    # The recipe/composition path uses UInt16. A retained UInt32 source may
    # narrow, but neither its total nor the target's index range may expand.
    target_index_bytes=2;target_vertex_limit=min(count,65535)
    if kept>target_vertex_limit:raise ValueError('The retained carrier cannot fit the target UInt16 index representation.')
    logical_bytes=vertices*vertex_cost+indices*source_index_bytes
    indices_per_vertex=3 if fine_only else 6
    maximum=min(vertices,target_vertex_limit-kept,logical_bytes//max(1,vertex_cost+indices_per_vertex*target_index_bytes))
    return dict(owner='retained-source-lod',fineOnly=bool(fine_only),sourceVertices=count,keptVertices=kept,
        sourceReplaceableVertices=vertices,sourceReplaceableIndices=indices,sourceIndexFormat=index_format,targetIndexFormat=0,
        physicalFrames=frames,finalVertexStrideBytes=stride,vertexAndPhysicalCostPerVertexBytes=vertex_cost,
        sourceVertexAndPhysicalBytes=vertices*vertex_cost,sourceIndexBytes=indices*source_index_bytes,
        maximumAddedLogicalBytes=logical_bytes,maximumAddedVertices=maximum,maximumFinalVertices=target_vertex_limit,
        maximumCurves=maximum//4,byteRepresentation='Uncompressed Float32 vertex/skin/UV and full position-normal-tangent fit channels; source and target index widths included.')


def retained_source_shell(native,reference,matrix,materials,maximum_offset):
    """Use the retained render carrier as a conservative exterior seed field."""
    owners,proof=retained_core_owners(native,reference,matrix,materials,maximum_offset)
    p,n,t,b=native_frames(dict(reference,shapes=[]),matrix)
    roles=np.asarray(next(row['values']for row in reference['uv']if row['channel']==2)).reshape(-1,4)[:,2]
    triangles=np.concatenate([np.asarray(row,int).reshape(-1,3)for row in reference['subMeshes']])
    triangles=triangles[np.all(roles[triangles]<16.5,axis=1)]
    if not len(triangles):raise ValueError('The retained groom has no render carrier for exterior seeds.')
    positions=p[0,triangles];center=(positions.min(axis=(0,1))+positions.max(axis=(0,1)))*.5
    directions=unit(np.asarray([(x,y,z)for x in(-1,0,1)for y in(-1,0,1)for z in(-1,0,1)
        if (x,y,z)!=(0,0,0)and sum(v!=0 for v in(x,y,z))in(1,3)],float))
    return dict(owners=owners,p=p[0],n=n[0],t=t[0],b=b[0],bvh=SourceShellBvh(positions),center=center,directions=directions,
        proof=dict(proof,carrierTriangles=len(triangles),carrierVertexCount=reference['vertexCount'],
            method='Retained carrier geometry BVH; same native UV/physical owner, normal-lifted fine seed, outward axis and diagonal directions. Opaque conservative heuristic, not alpha/raster coverage.'))


def retained_exterior_seed(shell,slot,triangle,bary,lift,*,fine_normal=None):
    """Admit the lifted seed only if its actual retained carrier has an exit ray."""
    source_p,source_n,source_t,source_b=(shell[key]for key in('p','n','t','b'))
    p=np.asarray(bary)@source_p[triangle];n=unit(np.asarray(bary)@source_n[triangle]);t=unit(np.asarray(bary)@source_t[triangle])
    record=dict(native=False,slot=slot,surface=dict(triangle=list(map(int,triangle)),barycentric=list(map(float,bary))))
    moved,points,_,_=couple_fine_to_core(None,source_p,source_n,source_t,np.asarray([p]),np.asarray([n]),np.asarray([t]),
        [record],source_p,source_n,source_t,source_b,shell['owners'],transport_frame=False,require_complete=False)
    if not len(moved):return None
    lift_axis=n if fine_normal is None else unit(np.asarray(fine_normal))
    point=points[0]+lift_axis*lift;radial=unit(point-shell['center']);directions=shell['directions'][shell['directions']@radial>.2]
    if not len(directions):return False
    return bool((~shell['bvh'].blocked(np.repeat(point[None,:],len(directions),axis=0),directions)).any())


def generate(entry,output) -> 'dict[str, Any]':
    soft_coverage=bool(PROFILE.get('softCoveragePilot',False))
    exterior_coverage=bool(PROFILE.get('exteriorCoverageRefinement',False))
    atlas_cards=bool(PROFILE.get('layeredAtlasCards',False))
    carrier_settings=PROFILE.get('carrierSurface')
    lock_section=PROFILE.get('lockSection')
    if lock_section is not None and(not atlas_cards or not carrier_settings or not carrier_settings.get('sourceSurface')):
        raise ValueError('Medium lock sections require their source-pattern carrier owner.')
    if carrier_settings is not None:
        validate_carrier_settings(carrier_settings)
        if not atlas_cards or not PROFILE.get('retainedSourceBudget')or not PROFILE.get('supportTerminals'):
            raise ValueError('Carrier reconstruction requires its retained native flow and contact domain.')
    assert sha(entry['packetPath'])==entry['packetSha256']and sha(entry['nativePacketPath'])==entry['nativePacketSha256']
    original,native=read(entry['packetPath']),read(entry['nativePacketPath'])
    if carrier_settings is not None and carrier_settings.get('sourceSurface')and entry.get('nativeClothSelection')is None:
        raise ValueError('Source-pattern carrier generation requires resolved native cloth fixed-point authority.')
    assert fingerprint(native)[0]==entry['manifestEntry']['sourceFingerprint']
    binding_path=Path(entry.get('bindingPath',PUBLIC/entry['manifestEntry']['bindingFile']))
    assert sha(binding_path)==entry['manifestEntry']['bindingSha256']
    binding=decode_binding(binding_path)
    assert binding['sourceFingerprint']==native['sourceFingerprint']and len(binding['vertices'])==original['vertexCount']
    assert [(s['name'],[f['weight']for f in s['frames']])for s in original['shapes']]==[(s['name'],[f['weight']for f in s['frames']])for s in native['shapes']]
    match=re.search(r'_LOD([012])(?:_|\b)',entry['nativeRenderer'],re.I)
    assert match,entry['nativeRenderer']
    lod=int(match.group(1))
    m=np.asarray(entry['rendererToPrefab']).reshape(4,4).T
    # Work relative to the renderer origin. Translation owns no source metric,
    # surface UV, derivative, barycentric anchor or physical delta. Removing it
    # avoids different last-bit output for the same native mesh nested beneath
    # an additional prefab wrapper; native/runtime transforms are unchanged.
    m=m.copy();m[:3,3]=0
    linear=m[:3,:3]
    inverse=np.linalg.inv(linear)
    np_frames,nn_frames,nt_frames,nb_frames=native_frames(native,m.T.ravel().tolist())
    terminal_settings=PROFILE.get('supportTerminals');terminal_domain=None;contact_proof=None;terminal_field=None;fixed_vertices=[]
    if terminal_settings is not None:
        validate_terminal_settings(terminal_settings)
        contact_sources=entry.get('contactSources')or{};heads=contact_sources.get('heads')
        if not heads:raise ValueError('Terminal groom authoring requires its declared compatible head sources.')
        authority=contact_sources['sourceCensus']
        if sha(authority['path'])!=authority['sha256']:raise ValueError('The compatible groom contact census changed.')
        protected,contact_proof=source_contact_vertices(native,entry['rendererToPrefab'],heads,terminal_settings['contactMarginMetres'])
        cloth=entry.get('nativeClothSelection')
        if cloth is not None:
            fixed_vertices=cloth['fixedVertexIds']
            if cloth['nativeFingerprint']!=native['sourceFingerprint']or cloth['sourceVertexCount']!=native['vertexCount']or \
                    any(type(index)is not int or not 0<=index<native['vertexCount']for index in fixed_vertices):
                raise ValueError('A native cloth fixed sample differs from its render vertex authority.')
            index=cloth['nativeIndex']
            if sha(index['path'])!=index['sha256']:raise ValueError('The native cloth selection index changed.')
            protected[np.asarray(fixed_vertices,int)]=True
            contact_proof['authoredClothFixedVertexIds']=list(fixed_vertices)
        terminal_domain=source_terminal_domain(native,entry['rendererToPrefab'],entry['materials'],protected,terminal_settings,
            contact_vertices=contact_proof['confirmedContactVertexIds'])
        if not terminal_domain['tipGroups']:raise ValueError('The protected source groom has no resolved terminal domain.')
    world_p=np.asarray(original['vertices']).reshape(-1,3)@linear.T+m[:3,3]
    world_n=unit(np.asarray(original['normals']).reshape(-1,3)@np.linalg.inv(linear))
    uv={u['channel']:np.asarray(u['values']).reshape(-1,4)for u in original['uv']}
    groom=uv[2]
    source_uv=uv[0][:,:2]
    native_uv=np.asarray(next(u for u in native['uv']if u['channel']==0)['values']).reshape(-1,4)[:,:2]
    random=np.random.default_rng(int(native['sourceFingerprint'][:16],16))
    packet=copy.deepcopy(original)
    native_surface=native_flow_surface(original,native,binding,groom)
    if atlas_cards:assert native_surface, 'Layered cards require their declared native flow surface.'
    if terminal_domain is not None and carrier_settings is None:
        if not native_surface:raise ValueError('Terminal groom authoring requires its declared native sampling surface.')
        terminal_field=source_terminal_field(native,original,entry['rendererToPrefab'],entry['materials'],terminal_domain,terminal_settings)
        if not terminal_field['vertices']:raise ValueError('No source-owned support retreat survived the fitting checks.')
    if native_surface:
        assert not any(channel['channel']==2 for channel in native['uv']), 'Native UV2 is already occupied.'
    geometry_plan=None;geometry_drivers=None
    if atlas_cards:
        geometry_plan=dict(physicalGroups=curved_core_groups(native),
            sourceTrianglesBySlot=[np.asarray(slot,int).reshape(-1,3)for slot in native['subMeshes']],vertices=[])
        geometry_drivers=prepare_geometry_frames(geometry_plan,np_frames)
        binding['schemaVersion']=4
    retained_indices=[]
    added=[]
    posed_curves=[];carrier_guides=[]
    new_indices=[[]for _ in original['subMeshes']]
    reports=[]
    physical=sum(len(s['frames'])for s in native['shapes'])
    uv_floats=sum(u['dimension']for u in original['uv'])
    vertex_stride=(3+3+4+4+uv_floats+(4 if original['colors']else 0))*4+16
    retained_capacity=None;retained_shell=None;retained_budget=None
    if PROFILE.get('retainedSourceBudget'):
        retained_reference=retained_groom_reference(entry,native)
        fine_only=atlas_cards and terminal_settings is not None and carrier_settings is None
        retained_capacity=retained_groom_capacity(entry,native,fine_only=fine_only,reference=retained_reference)
        retained_budget=retained_groom_budget(native,retained_reference,fine_only=fine_only)
        physical_budget=retained_budget['sourceVertexAndPhysicalBytes'];maximum_vertices=retained_budget['maximumAddedVertices']
        if atlas_cards and terminal_settings is not None and carrier_settings is None:
            retained_shell=retained_source_shell(native,retained_reference,m.T.ravel().tolist(),entry['materials'],terminal_settings['maximumSupportOffsetMetres'])
        if maximum_vertices<4:raise ValueError('The retained groom has no allocation for source-owned fine geometry.')
    else:
        physical_budget=PROFILE.get('maximumAddedPhysicalAndVertexBytesByLod',[PROFILE['maximumAddedPhysicalAndVertexBytes']]*3)[lod]
        maximum_vertices=min(PROFILE['maximumAddedVerticesByLod'][lod],physical_budget//max(1,physical*36+vertex_stride))
    if exterior_coverage:maximum_vertices=min(maximum_vertices,65535-native['vertexCount'])
    vertex_budget_ribbons=max(1,maximum_vertices//8)
    # This budget is derived from real physical-frame bytes, not hairstyle names.
    ribbon_budget=retained_budget['maximumCurves']if retained_budget is not None else min(PROFILE['maximumRibbonsByLod'][lod],vertex_budget_ribbons)
    for slot,flat in enumerate(original['subMeshes']):
        triangles=np.asarray(flat).reshape(-1,3)
        material=entry['materials'][slot]
        color=material_sampler(material,'_MainTex')
        metal=material_sampler(material,'_Mask')
        if PROFILE.get('nativeHairDomain')and material['shader']!='Custom/FakeAnisoHair':
            retained_indices.append(flat[:]);reports.append(dict(slot=slot,status='PRESERVED_NON_GROOM_MATERIAL_SLOT',oldBroadExteriorTriangles=0,removedBroadExteriorTriangles=0));continue
        domain=SourceHairDomain(material)if PROFILE.get('nativeHairDomain')else None
        def eligible(uv0):return domain.hair([uv0])if domain is not None else(color is None or color(uv0)[3]>=.51)and(metal is None or metal(uv0)[0]<=.01)
        core=np.asarray([tri for tri in triangles if all(binding['vertices'][int(i)]['role']in ('volume-support','thin-root')for i in tri)],int).reshape(-1,3)
        outer=triangles[np.all((groom[triangles,2]>.5)&(groom[triangles,2]<16.5),axis=1)]
        if native_surface:
            outer=core
        if not len(core)or not len(outer):
            retained_indices.append(flat[:]);reports.append(dict(slot=slot,status='PRESERVED_NO_SOURCE_FLOW_SURFACE',oldBroadExteriorTriangles=len(outer),removedBroadExteriorTriangles=0))
            continue
        old_points=world_p[outer]
        q=groom[outer,:2]
        duv=np.stack((q[:,1]-q[:,0],q[:,2]-q[:,0]),axis=2)
        valid=np.abs(np.linalg.det(duv))>1e-12
        derivative=np.stack((old_points[valid,1]-old_points[valid,0],old_points[valid,2]-old_points[valid,0]),axis=2)@np.linalg.inv(duv[valid])
        flows=unit(derivative[:,:,1])
        flow_centers=old_points[valid].mean(axis=1)
        flow_normals=unit(world_n[outer][valid].mean(axis=1))
        assert len(flows)
        # Old broad exterior geometry is removed only when all material samples
        # are hair. Mixed/ornament triangles retain their original exact channels.
        keep=[];removed=0
        for tri in triangles:
            is_outer=all(.5<groom[i,2]<16.5 for i in tri)
            if soft_coverage:
                # These are copied native exterior faces, including unassigned
                # role0 charts. The complete native support remains separate.
                is_outer=all(binding['vertices'][int(i)]['role']=='source-ribbon' for i in tri)
            samples=[source_uv[i]for i in tri]+[source_uv[tri].mean(axis=0)]
            all_hair=domain.hair(samples)if domain is not None else all(eligible(value)for value in samples)
            if is_outer and all_hair:removed+=1
            else:keep.extend(int(i)for i in tri)
        retained_indices.append(keep)
        native_tri=np.asarray([[binding['vertices'][int(i)]['anchor']for i in tri]for tri in core],int)
        native_keys={tuple(sorted(map(int,tri))):i for i,tri in enumerate(native_tri)}
        flow_native=np.asarray([native_keys.get(tuple(sorted(binding['vertices'][int(i)]['anchor']for i in tri)),-1)for tri in outer[valid]],int)
        positions=np_frames[0,native_tri]
        normals=nn_frames[0,native_tri]
        uv0=native_uv[native_tri]
        pattern_proof=None
        if lock_section is not None:
            pattern,quality,pattern_proof=_carrier_face_flow(native,np_frames[0],material,
                radius=carrier_settings['sourceSurface']['patternRadiusPixels'])
            native_keys_by_face={tuple(sorted(map(int,triangle))):index for index,triangle in enumerate(np.asarray(native['subMeshes'][slot]).reshape(-1,3))}
            source_faces=np.asarray([native_keys_by_face[tuple(sorted(map(int,triangle)))]for triangle in native_tri],int)
            for index,face in enumerate(flow_native):
                if face>=0 and quality[source_faces[face]]>1e-6:flows[index]=pattern[source_faces[face]]
            pattern_proof.update(qualifiedSourceFaces=int(np.sum(quality[source_faces]>1e-6)),
                fallbackSourceFaces=int(np.sum(quality[source_faces]<=1e-6)),fallbackAuthority='retained C8 flow only where original Main has no resolved line tensor')
        cross=np.cross(positions[:,1]-positions[:,0],positions[:,2]-positions[:,0])
        areas=np.linalg.norm(cross,axis=1)*.5
        eligible_tri=np.array([eligible(value.mean(axis=0))for value in uv0])
        skin_eligible=np.ones(len(native_tri),bool)
        if native_surface:
            for index,triangle in enumerate(native_tri):
                try:source_surface_skin(native,dict(triangle=triangle.tolist(),barycentric=[1/3]*3))
                except SurfaceSkinSupportError:skin_eligible[index]=False
        excluded_skin_area=float(areas[eligible_tri&~skin_eligible].sum());eligible_tri&=skin_eligible
        area=areas*eligible_tri
        assert area.sum()>0
        shell_report=None
        exterior_source=source_shell_stratum(positions,eligible_tri) if exterior_coverage else None
        if exterior_source is not None:
            shell_bvh,shell_directions,shell_clear,shell_tri,shell_fraction,shell_report=exterior_source
            native_area=float(area.sum());area=area*shell_tri*np.sqrt(shell_fraction)
            shell_report.update(allEligibleSourceAreaSquareMetres=native_area,exteriorStratumAreaSquareMetres=float(areas[shell_tri].sum()),weightedExteriorSamplingAreaSquareMetres=float(area.sum()))
            assert area.sum()>0
        owners=collections.defaultdict(set)
        for i,tri in enumerate(native_tri):
            for anchor in tri:owners[int(anchor)].add(i)
        physical_keys=collections.defaultdict(list)
        for anchor in owners:
            key=tuple(np.rint(np_frames[0,anchor]/PROFILE['geometricSeamToleranceMetres']).astype(np.int64))
            physical_keys[key].append(anchor)
        physical_owners={};physical_equivalents={}
        seam_connections=0
        for group in physical_keys.values():
            for anchor in group:
                compatible=[other for other in group if
                    np.max(np.linalg.norm(np_frames[:,other]-np_frames[:,anchor],axis=1))<=PROFILE['geometricSeamToleranceMetres']
                    and np.min(np.sum(nn_frames[:,other]*nn_frames[:,anchor],axis=1))>=PROFILE['geometricSeamMinimumNormalDot']]
                physical_owners[anchor]=set().union(*(owners[other]for other in compatible))
                physical_equivalents[anchor]=set(compatible)
                seam_connections+=len(compatible)-1
        # Only true shared source edges can carry a trace between triangles.
        # Vertex-only neighborhood jumps used to bypass UV chart boundaries.
        edge_neighbors=collections.defaultdict(list)
        chart_parent=list(range(len(native_tri)))
        def chart_root(index):
            while chart_parent[index]!=index:
                chart_parent[index]=chart_parent[chart_parent[index]];index=chart_parent[index]
            return index
        for index,tri in enumerate(native_tri):
            for a,b in ((0,1),(1,2),(2,0)):
                candidates=(physical_owners[int(tri[a])]&physical_owners[int(tri[b])])-{index}
                for other in sorted(candidates):
                    oa=[j for j,anchor in enumerate(native_tri[other])if int(anchor)in physical_equivalents[int(tri[a])]]
                    ob=[j for j,anchor in enumerate(native_tri[other])if int(anchor)in physical_equivalents[int(tri[b])]]
                    if len(oa)!=1 or len(ob)!=1 or oa[0]==ob[0]:continue
                    continuous=np.max(np.abs(uv0[index,[a,b]]-uv0[other,[oa[0],ob[0]]]))<=PROFILE['nativeUvSeamTolerance']
                    edge_neighbors[index].append((other,a,b,oa[0],ob[0],continuous))
                    if continuous:chart_parent[chart_root(index)]=chart_root(other)
        chart_ids=[chart_root(index)for index in range(len(native_tri))]
        neighborhoods={}
        def neighborhood(index):
            if index not in neighborhoods:
                group={int(index)};front=set(group)
                for _ in range(PROFILE['neighborhoodRings']):
                    next_={j for i in front for anchor in native_tri[i]for j in physical_owners[int(anchor)]}
                    next_-=group
                    group|=next_;front=next_
                neighborhoods[index]=np.array(sorted(group),int)
            return neighborhoods[index]
        def flow(point,normal,sign=None,index=None):
            field_facing=flow_normals@normal>.1
            distances=np.sum((flow_centers-point)**2,axis=1)
            distances[~field_facing]=np.inf
            if soft_coverage and index is not None:
                # Smooth the source guide line only within its native surface
                # neighborhood. Never average arbitrary coincident cards.
                local=np.isin(flow_native,neighborhood(index))&(flow_normals@normal>.45)
                candidates=np.flatnonzero(local)
                if len(candidates):
                    candidates=candidates[np.argsort(distances[candidates])[:8]]
                    directions=flows[candidates]-normal*np.sum(flows[candidates]*normal,axis=1)[:,None]
                    directions=unit(directions)
                    weights=1/np.maximum(distances[candidates]+.000004,1e-12)
                    tensor=np.einsum('n,ni,nj->ij',weights,directions,directions)
                    eigen,vectors=np.linalg.eigh(tensor);value=vectors[:,-1]
                    value=unit(value-normal*np.dot(normal,value))
                    if sign is not None and np.dot(value,sign)<0:value=-value
                    return value
            if not np.isfinite(distances).any():return None
            nearest=int(np.argmin(distances))
            value=flows[nearest]-normal*np.dot(flows[nearest],normal)
            if np.linalg.norm(value)<1e-10:return None
            value=unit(value)
            if sign is not None and np.dot(value,sign)<0:value=-value
            return value
        trace_stops=collections.Counter();unresolved_root_vertices=set();unresolved_root_strands=set();unresolved_root_accepted=set();unowned_retained_triangles=set()
        extent=float(np.linalg.norm(positions.max(axis=(0,1))-positions.min(axis=(0,1))))
        target_length=float(np.clip(extent*PROFILE['targetLengthExtentFraction'],PROFILE['targetLengthLimitsMetres'][0],PROFILE['targetLengthLimitsMetres'][1]))
        length_limits=[target_length*.65,target_length]
        mean_l=np.mean(length_limits);mean_w=np.mean(PROFILE['widthMetres'])
        visible_area=mean_l*mean_w*.72*PROFILE['assumedMeasuredMaskCoverage']
        requested=int(math.ceil(-math.log(1-PROFILE['targetPoissonCoverage'])*area.sum()/visible_area))
        count=min(ribbon_budget,max(1,requested))
        attempt_factor=PROFILE.get('candidateAttemptFactor',1);attempt_limit=count*attempt_factor
        seed_weights=area/area.sum()
        if terminal_domain is not None:
            near=np.mean(terminal_domain['terminalDistance'][native_tri],axis=1)<=terminal_settings['terminalRegionLengthMetres']
            terminal_area=area*near
            if terminal_area.sum()>0:
                fraction=terminal_settings['terminalSeedFraction']
                seed_weights=seed_weights*(1-fraction)+terminal_area/terminal_area.sum()*fraction
        selected=random.choice(len(core),size=attempt_limit,p=seed_weights,replace=not soft_coverage or attempt_limit>int(np.count_nonzero(area)))
        boundary=[]
        if soft_coverage:
            for ti in range(len(core)):
                if not eligible_tri[ti]or(exterior_source is not None and not exterior_source[3][ti]):continue
                for a,b in ((0,1),(1,2),(2,0)):
                    if not any(edge[1]==a and edge[2]==b for edge in edge_neighbors[ti]):
                        length=float(np.linalg.norm(positions[ti,a]-positions[ti,b]))
                        if length>1e-6:boundary.append((ti,a,b,length))
        fringe_count=min(int(count*PROFILE.get('fringeFraction',.16)),len(boundary))
        accepted=0;fringe_accepted=0;fringe_attempted=0;attempted=0;failed=collections.Counter();accepted_lengths=[];accepted_nodes=[]
        redistributed_curves=0;redistributed_metres=0.;accepted_widths=[];section_proofs=[]
        def reduce_path(nodes):
            if len(nodes)<=2:return nodes
            points=np.asarray([n['point']for n in nodes]);normals_=np.asarray([n['normal']for n in nodes])
            chord=points[-1]-points[0];denominator=float(chord@chord)
            if denominator<1e-16:return nodes
            along_=np.clip((points-points[0])@chord/denominator,0,1)
            error=np.linalg.norm(points-(points[0]+along_[:,None]*chord),axis=1)/PROFILE['maximumChordErrorMetres']
            interpolated=unit((1-along_[:,None])*normals_[0]+along_[:,None]*normals_[-1])
            normal_error=np.degrees(np.arccos(np.clip(np.sum(interpolated*normals_,axis=1),-1,1)))/PROFILE['maximumNormalChordDegrees']
            coordinates=np.asarray([n['uv']for n in nodes])
            reference=(1-along_[:,None])*coordinates[0]+along_[:,None]*coordinates[-1]
            main=material['maps'].get('_MainTex',{})
            resolution=np.asarray([main.get('width',2048),main.get('height',2048)])
            uv_error=np.max(np.abs(coordinates-reference)*resolution,axis=1)/PROFILE['maximumSourceUvChordErrorTexels']
            # Reduction must preserve the geometry emitted later, including
            # the nonlinear width/lift envelope. Base P/N/UV alone can flatten
            # an entire curve into its two tip samples and leave a long wire.
            if soft_coverage:
                v=np.asarray([node['_sweptProfileV']for node in nodes])
                rank=(seed*.6180339887498949)%1
                sine=np.maximum(0,np.sin(math.pi*v))
                taper=.025+.975*sine**(.55+.35*rank)
                height=lift*(.12+.88*sine**.9)
                direction=np.empty_like(points)
                direction[0]=points[1]-points[0];direction[-1]=points[-1]-points[-2]
                if len(points)>2:direction[1:-1]=points[2:]-points[:-2]
                direction=unit(direction-normals_*np.sum(normals_*direction,axis=1)[:,None])
                across=unit(np.cross(direction,normals_))
                widths=np.asarray([node.get('_sweptProfileWidth',width)for node in nodes])
                swept=points[:,None,:]+normals_[:,None,:]*height[:,None,None]+across[:,None,:]*(np.array([-1.,1.])[None,:,None]*widths[:,None,None]*.5*taper[:,None,None])
                reference=(1-along_[:,None,None])*swept[0]+along_[:,None,None]*swept[-1]
                swept_error=np.max(np.linalg.norm(swept-reference,axis=2),axis=1)/PROFILE['maximumChordErrorMetres']
                error=np.maximum(error,swept_error)
            metric=np.maximum(np.maximum(error,normal_error),uv_error);metric[[0,-1]]=0;index=int(np.argmax(metric))
            if metric[index]<=1:return [nodes[0],nodes[-1]]
            return reduce_path(nodes[:index+1])[:-1]+reduce_path(nodes[index:])
        for seed,start_tri in enumerate(selected):
            if accepted>=count:break
            attempted+=1
            if maximum_vertices-len(added)<4:
                failed['bounded-physical-and-vertex-budget']+=attempt_limit-seed;break
            bary=-np.log(np.maximum(random.random(3),1e-12));bary/=bary.sum()
            is_fringe=soft_coverage and(seed<fringe_count if attempt_factor==1 else fringe_accepted<fringe_count and fringe_attempted<fringe_count*attempt_factor)
            if is_fringe:
                fringe_attempted+=1
                edge=boundary[int(random.choice(len(boundary),p=np.asarray([e[3]for e in boundary])/sum(e[3]for e in boundary)))];start_tri,a,b=edge[:3]
                fraction=float(random.uniform(.05,.95));bary=np.full(3,.01);bary[a]=.99*(1-fraction);bary[b]=.99*fraction
            center=bary@positions[start_tri];normal=unit(bary@normals[start_tri]);u=bary@uv0[start_tri]
            if not eligible(u):failed['native-alpha-or-metal']+=1;continue
            if exterior_source is not None:
                shell_bvh,shell_directions,shell_clear=exterior_source[:3]
                directions=shell_directions[shell_clear[start_tri]]
                if not len(directions)or shell_bvh.blocked(np.repeat(center[None,:],len(directions),axis=0),directions).all():failed['seed-buried-by-native-source']+=1;continue
            if retained_shell is not None:
                exposed=retained_exterior_seed(retained_shell,slot,native_tri[start_tri],bary,float(np.mean(PROFILE['liftMetres'])),fine_normal=normal)
                if exposed is None:
                    failed['seed-without-retained-surface-owner']+=1;unowned_retained_triangles.add(tuple(sorted(map(int,native_tri[start_tri]))));continue
                if not exposed:failed['seed-buried-by-retained-render-carrier']+=1;continue
            axis=flow(center,normal,index=int(start_tri))
            if axis is None:failed['source-flow-degenerate']+=1;continue
            if terminal_domain is not None:
                field=terminal_domain['rootDistance'][native_tri[start_tri]]
                if not np.isfinite(field).all():
                    failed['unknown-source-root-domain']+=1;unresolved_root_strands.add(seed)
                    unresolved_root_vertices.update(map(int,native_tri[start_tri][~np.isfinite(field)]));continue
                edges=positions[start_tri,1:]-positions[start_tri,0]
                gradient=np.linalg.lstsq(edges,field[1:]-field[0],rcond=None)[0]
                if abs(float(np.dot(axis,gradient)))<1e-10:failed['source-flow-without-root-order']+=1;continue
                if np.dot(axis,gradient)<0:axis=-axis
            length=float(random.uniform(length_limits[0],length_limits[1]))
            width=float(random.uniform(PROFILE['widthMetres'][0],PROFILE['widthMetres'][1]))
            lift=float(random.uniform(PROFILE['liftMetres'][0],PROFILE['liftMetres'][1]))
            if atlas_cards and not is_fringe:
                layer=(seed%3)/2
                lift=float((1-layer)*PROFILE['liftMetres'][0]+layer*PROFILE['liftMetres'][1])
            if is_fringe:
                length=float(random.uniform(*PROFILE.get('fringeLengthMetres',[.007,.017])));width=float(random.uniform(*PROFILE.get('fringeWidthMetres',[.00045,.0009])));lift=float(random.uniform(*PROFILE.get('fringeLiftMetres',[.003,.007])))
            center_node=dict(point=center,normal=normal,uv=u,triangle=int(start_tri),bary=bary,chart=chart_ids[int(start_tri)])
            def trace(sign,span):
                path=[];current=center_node;direction=axis*sign
                steps=max(2,int(math.ceil(span/PROFILE['traceStepMetres'])))
                for _ in range(steps):
                    direction=flow(current['point'],current['normal'],direction,current['triangle'])
                    if direction is None:trace_stops['source-flow-undefined']+=1;break
                    routed=source_surface_walk(current,current['point']+direction*(span/steps),flow,
                        positions,normals,uv0,edge_neighbors,chart_ids,eligible=eligible,domain=domain,stops=trace_stops)
                    if not routed:break
                    stop_at_contact_or_tip=False
                    if terminal_domain is not None:
                        ordered=[];previous=source_root_potential(current['bary'],terminal_domain['rootDistance'][native_tri[current['triangle']]])
                        if previous is None:raise AssertionError('An admitted source curve lost its resolved root domain.')
                        for node in routed:
                            values=terminal_domain['rootDistance'][native_tri[node['triangle']]]
                            following=source_root_potential(node['bary'],values)
                            if following is None:
                                trace_stops['unresolved-source-root-domain']+=1;unresolved_root_strands.add(seed)
                                unresolved_root_vertices.update(map(int,native_tri[node['triangle']][~np.isfinite(values)]))
                                stop_at_contact_or_tip=True;break
                            if sign*(following-previous)<-1e-8:
                                stop_at_contact_or_tip=True;break
                            ordered.append(node);previous=following
                            if sign<0 and following<=1e-8:stop_at_contact_or_tip=True;break
                        routed=ordered
                        if not routed:trace_stops['source-root-order-termination']+=1;break
                    candidate=routed[-1]
                    displacement=candidate['point']-current['point']
                    if np.linalg.norm(displacement)<.0003 or np.dot(unit(displacement),direction)<.35:trace_stops['source-projection-no-forward-progress']+=1;break
                    if any(np.linalg.norm(candidate['point']-n['point'])<.001 for n in path[:-2]):trace_stops['source-flow-cycle']+=1;break
                    if domain is not None:
                        previous_node=current;safe=True
                        for next_node in routed:
                            if np.linalg.norm(next_node['point']-previous_node['point'])<=PROFILE['geometricSeamToleranceMetres']*1.01:
                                qualified=domain.hair([previous_node['uv']])and domain.hair([next_node['uv']])
                            else:qualified=domain.hair([previous_node['uv'],next_node['uv']])
                            if not qualified:safe=False;break
                            previous_node=next_node
                        if not safe:trace_stops['native-hair-domain-segment-footprint']+=1;break
                    path.extend(routed);current=candidate
                    if stop_at_contact_or_tip:trace_stops['source-contact-or-closed-tip']+=1;break
                return path
            first_sign=-1
            if terminal_domain is not None:
                root=source_root_potential(center_node['bary'],terminal_domain['rootDistance'][native_tri[center_node['triangle']]])
                tip=source_root_potential(center_node['bary'],terminal_domain['terminalDistance'][native_tri[center_node['triangle']]])
                if root is not None and tip is not None and tip<root:first_sign=1
            paths,span_proof=source_curve_paths(center_node,length,trace,first_sign)
            if span_proof['redistributedMetres']>0:
                redistributed_curves+=1;redistributed_metres+=span_proof['redistributedMetres']
            nodes=list(reversed(paths[-1]))+[center_node]+paths[1]
            # The unreduced trace owns the envelope parameter while deciding
            # which source samples must survive. This is diagnostic metadata
            # only; unchanged reduction paths retain their original output v.
            if soft_coverage and len(nodes)>1:
                original_lengths=np.linalg.norm(np.diff(np.asarray([n['point']for n in nodes]),axis=0),axis=1)
                original_along=np.r_[0,np.cumsum(original_lengths)]/max(float(original_lengths.sum()),1e-20)
                for node,value in zip(nodes,original_along):node['_sweptProfileV']=float(value)
                if lock_section is not None and not is_fringe:
                    for index,node in enumerate(nodes):
                        before=max(index-1,0);after=min(index+1,len(nodes)-1)
                        while before>0 and np.linalg.norm(nodes[before]['point']-node['point'])<1e-8:before-=1
                        while after<len(nodes)-1 and np.linalg.norm(nodes[after]['point']-node['point'])<1e-8:after+=1
                        direction=nodes[after]['point']-nodes[before]['point'];normal=node['normal']
                        across=unit(np.cross(direction,normal))
                        node['_sweptProfileWidth']=source_ribbon_width(node,across,width,lock_section['maximumSourceWidthFraction'],
                            positions,normals,uv0,edge_neighbors,chart_ids,eligible=eligible,domain=domain)
                    if any(not np.isfinite(node['_sweptProfileWidth'])or node['_sweptProfileWidth']<=1e-8 for node in nodes):
                        failed['source-medium-width-unresolved']+=1;continue
            source_walk=copy.deepcopy(nodes)if carrier_settings is not None and not carrier_settings.get('sourceSurface')and not is_fringe else None
            pieces=[];piece=[]
            for node in nodes:
                if piece:
                    distance=np.linalg.norm(node['point']-piece[-1]['point'])
                    same_uv=np.max(np.abs(node['uv']-piece[-1]['uv']))<=PROFILE['nativeUvSeamTolerance']
                    if distance<=PROFILE['geometricSeamToleranceMetres']*1.01 and not same_uv:
                        if atlas_cards:
                            shared_normal=unit(node['normal']+piece[-1]['normal'])
                            node['normal']=shared_normal;piece[-1]['normal']=shared_normal
                        if len(piece)>=2:pieces.append(reduce_path(piece))
                        piece=[]
                    elif distance<=PROFILE['geometricSeamToleranceMetres']*1.01 and same_uv:
                        piece[-1]=node;continue
                    elif node['chart']!=piece[-1]['chart']:
                        raise AssertionError('A chart transition lacks its exact duplicated source edge.')
                piece.append(node)
            if len(piece)>=2:pieces.append(reduce_path(piece))
            nodes=[];connections=[]
            for piece in pieces:
                if domain is not None and any(not domain.hair([a['uv'],b['uv']])for a,b in zip(piece,piece[1:])):
                    failed['reduced-native-hair-domain-footprint']+=1
                    continue
                first_node=len(nodes);nodes.extend(piece)
                connections.extend((first_node+j,first_node+j+1)for j in range(len(piece)-1))
            if len(added)+2*len(nodes)>maximum_vertices:failed['bounded-physical-and-vertex-budget']+=1;continue
            if len(nodes)<2 or not connections:failed['short-source-curve']+=1;continue
            segments=np.array([np.linalg.norm(b['point']-a['point'])for a,b in zip(nodes,nodes[1:])])
            curve_length=float(segments.sum())
            if curve_length<PROFILE['minimumCurveLengthMetres']:failed['source-curve-length']+=1;continue
            along=np.r_[0,np.cumsum(segments)]/curve_length
            first=len(added)
            projected=[]
            source_index=int(core[int(start_tri)][np.argmax(bary)])
            hand=-1 if groom[source_index,3]<0 else 1
            rank=(seed*.6180339887498949)%1
            for j,node in enumerate(nodes):
                v=float(np.clip(along[j],0,1));n=node['normal']
                left=j-1;right=j+1
                while left>=0 and np.linalg.norm(nodes[left]['point']-node['point'])<1e-8:left-=1
                while right<len(nodes)and np.linalg.norm(nodes[right]['point']-node['point'])<1e-8:right+=1
                direction=nodes[min(right,len(nodes)-1)]['point']-nodes[max(0,left)]['point']
                direction=unit(direction-n*np.dot(n,direction));across=unit(np.cross(direction,n))
                taper=.04+.96*max(0,math.sin(math.pi*v))**.7
                if soft_coverage:taper=.025+.975*max(0,math.sin(math.pi*v))**(.55+.35*rank)
                center_point=node['point']+n*lift*(.2+.8*math.sin(math.pi*v))
                if soft_coverage:center_point=node['point']+n*lift*(.12+.88*max(0,math.sin(math.pi*v))**.9)
                for side in (-1,1):
                    point=center_point+across*(side*node.get('_sweptProfileWidth',width)*.5*taper)
                    parents=native_tri[node['triangle']]
                    anchor=int(parents[np.argmin(np.sum((np_frames[0,parents]-point)**2,axis=1))])
                    basis=np.column_stack((nt_frames[0,anchor],nb_frames[0,anchor],nn_frames[0,anchor]))
                    offset=basis.T@(point-np_frames[0,anchor])
                    if np.linalg.norm(offset)>.04:projected=[];break
                    # The new sheet is curved along the source envelope, and its
                    # normal follows that curvature instead of the broad polygon.
                    transverse_angle=math.radians(18 if atlas_cards and not is_fringe else PROFILE['transverseNormalDegrees'])
                    strand_normal=unit(n*math.cos(transverse_angle)+across*(side*math.sin(transverse_angle)))
                    ln=basis.T@strand_normal;lt=basis.T@across
                    projected.append(dict(surface=dict(triangle=parents.tolist(),barycentric=node['bary'].tolist()),anchor=anchor,native=False,role='airy-edge-fringe'if is_fringe else'source-continuous-flow-ribbon',slot=slot,
                        uv0=node['uv'].tolist(),offset=offset.tolist(),normal=ln.tolist(),tangent=lt.tolist(),
                        tangentW=-float(np.sign(np.linalg.det(linear))),groom=[(side+1)*.5,v,(33 if is_fringe else 49 if atlas_cards else 17)+(seed%16),hand*(1+rank)]))
                if not projected:break
            if len(projected)!=2*len(nodes):failed['source-span']+=1;continue
            try:
                for record in projected[::2]:source_surface_skin(native,record['surface'])
            except SurfaceSkinSupportError:
                failed['native-skin-support-exceeds-four']+=1;continue
            if geometry_plan is not None:
                geometry_plan['vertices']=projected;prepare_geometry_frames(geometry_plan,np_frames,geometry_drivers)
            pieces=source_ribbon_pieces(projected,connections,np_frames,nn_frames,nt_frames,nb_frames,soft_coverage=soft_coverage,
                preserve_curve_coordinates=atlas_cards and not is_fringe)
            if pieces is None:failed['full-physical-frame-winding']+=1;continue
            projected,connections,frames,_=pieces
            section_triangles=None
            if lock_section is not None and not is_fringe:
                section=arched_ribbon_sections(projected,connections,frames,np_frames,nn_frames,nt_frames,nb_frames,lock_section['heightFraction'])
                if section is None:failed['arched-full-physical-frame-winding']+=1;continue
                projected,frames,section_triangles,section_proof=section
            if retained_shell is not None:
                p,n,t,b=(retained_shell[key]for key in('p','n','t','b'))
                moved,_,_,_=couple_fine_to_core(None,p,n,t,frames[0][0],frames[1][0],frames[2][0],projected,p,n,t,b,
                    retained_shell['owners'],transport_frame=False,require_complete=False)
                if len(moved)!=len(projected):
                    failed['curve-without-retained-surface-owner']+=1
                    for index in set(range(len(projected)))-set(map(int,moved)):
                        unowned_retained_triangles.add(tuple(sorted(map(int,projected[index]['surface']['triangle']))))
                    continue
            if len(added)+len(projected)>maximum_vertices:failed['bounded-physical-and-vertex-budget']+=1;continue
            posed_curves.append(frames)
            if source_walk is not None:
                distances=np.r_[0.,np.cumsum(np.linalg.norm(np.diff(np.asarray([node['point']for node in source_walk]),axis=0),axis=1))]
                if distances[-1]<=0:raise ValueError('An accepted source carrier guide lost its physical span.')
                carrier_guides.append(dict(slot=slot,guide=seed,lengthMetres=float(distances[-1]),samples=[
                    dict(triangle=native_tri[node['triangle']].tolist(),barycentric=node['bary'].tolist(),v=float(distance/distances[-1]))
                    for node,distance in zip(source_walk,distances)]))
            added.extend(projected)
            start=original['vertexCount']+first
            if section_triangles is not None:
                new_indices[slot].extend((section_triangles+start).ravel().tolist());section_proofs.append(section_proof)
            else:
                for a,b in connections:
                    a=start+a*2;b=start+b*2;new_indices[slot].extend((a,a+1,b,a+1,b+1,b))
            accepted+=1;fringe_accepted+=int(is_fringe);accepted_lengths.append(curve_length);accepted_nodes.append(len(nodes))
            if lock_section is not None and not is_fringe:accepted_widths.extend(float(node['_sweptProfileWidth'])for node in nodes)
            if seed in unresolved_root_strands:unresolved_root_accepted.add(seed)
        reports.append(dict(slot=slot,nativeHairDomain=domain is not None,sourceDomainScope='Native R<=.5 hair; conservative mip0 GPU UNorm8 interval and full bilinear footprint, coarse mixed/uncertain faces retained.'if domain is not None else'Legacy .01 point sampling retained for historical reproducibility.',sourceAreaSquareMetres=float(area.sum()),removedBroadExteriorTriangles=removed,
            requestedRibbons=requested,budgetedRibbons=count,acceptedCurvedRibbons=accepted,rejected=dict(failed),
            candidateAttempts=attempted,candidateLimit=attempt_limit,acceptedFringe=fringe_accepted,
            oldBroadExteriorTriangles=len(outer),sourceTopologyPreservedAsCore=True,
            coverageEstimateIsNotGpuProof=True,sourceExtentMetres=extent,targetCurveLengthMetres=length_limits,
            meanAcceptedCurveLengthMetres=float(np.mean(accepted_lengths))if accepted_lengths else 0,
            maximumAcceptedCurveLengthMetres=max(accepted_lengths,default=0),
            meanAcceptedNodes=float(np.mean(accepted_nodes))if accepted_nodes else 0,
            adaptiveChordErrorMetres=PROFILE['maximumChordErrorMetres'],traceStops=dict(trace_stops),
            redistributedTraceCandidates=redistributed_curves,redistributedTraceMetres=redistributed_metres,
            allPhysicalFrameGeometricSeamConnections=seam_connections,
            physicalCurveFrameOwner='deformed-source-centerline'))
        reports[-1]['nativeSkinSupport']=dict(maximumPositiveBones=4,excludedSourceAreaSquareMetres=excluded_skin_area,
            retainedSourceTriangles=native_tri[~skin_eligible].tolist(),positiveInfluencesDiscarded=0,
            rejectedCandidates=failed['native-skin-support-exceeds-four'],requestedCurves=count,acceptedCurves=accepted)
        if lock_section is not None:
            reports[-1]['lockSection']=dict(settings=copy.deepcopy(lock_section),sourceMainFlow=pattern_proof,
                acceptedMediumLocks=len(section_proofs),acceptedEdgeFibers=fringe_accepted,
                sourceBoundWidthMetres=(dict(minimum=min(accepted_widths),median=float(np.median(accepted_widths)),maximum=max(accepted_widths))if accepted_widths else None),
                minimumSignedArea=min((row['minimumSignedArea']for row in section_proofs),default=None),
                atlasStrandsPerPhysicalCard=1,geometryAndTextureCoverageRequireActualRender=True)
        if retained_shell is not None:
            reports[-1]['retainedRenderCarrierSeedField']=retained_shell['proof']
            reports[-1]['unresolvedRetainedCarrierDomain']=dict(sourceTriangles=sorted(unowned_retained_triangles),
                sourceVertices=sorted({i for tri in unowned_retained_triangles for i in tri}),
                seedCandidates=failed['seed-without-retained-surface-owner'],curveCandidates=failed['curve-without-retained-surface-owner'],
                acceptedCurvesWithUnknownOwner=0)
        if soft_coverage:reports[-1].update(seedPlacement='native area stratified without replacement; physical boundary tranche',physicalBoundaryEdges=len(boundary),boundarySeedCurves=fringe_count,sourceFlow='local native-neighborhood sign-independent line tensor; curve geometry owns final direction')
        if exterior_coverage:reports[-1].update(seedPlacement='BVH exterior-stratum area weighted; actual barycentric seed must retain at least one unoccluded exterior ray; physical boundaries restricted to the same stratum',sourceShell=shell_report)
        if terminal_domain is not None:reports[-1].update(seedPlacement='native exterior area with contact-ordered terminal priority',terminalSeedFraction=terminal_settings['terminalSeedFraction'],resolvedClosedTipGroups=len(terminal_domain['tipGroups']),
            unresolvedRootDomain=dict(sourceVertices=sorted(unresolved_root_vertices),candidateStrands=len(unresolved_root_strands),
                acceptedStrandsWithResolvedPrefix=len(unresolved_root_accepted),rejectedStrands=len(unresolved_root_strands-unresolved_root_accepted),
                sourceComponents=terminal_domain['unresolvedComponents']))
    removed_broad=sum(r.get('removedBroadExteriorTriangles',0)for r in reports)
    assert added or removed_broad==0,'No valid fine curve may replace removed coarse coverage.'
    anchors=np.asarray([a['anchor']for a in added],dtype=int)
    if posed_curves:
        pp,nn,tt=(np.concatenate([curve[index]for curve in posed_curves],axis=1)for index in range(3))
    else:pp,nn,tt=source_fiber_frames(added,np_frames,nn_frames,nt_frames,nb_frames)
    pp=(pp-m[:3,3])@inverse.T;nn=unit(nn@linear);tt=unit(tt@inverse.T);tt=unit(tt-nn*np.sum(tt*nn,axis=2)[:,:,None])
    for key,value in (('vertices',pp[0]),('normals',nn[0]),('tangents',np.c_[tt[0],np.full(len(added),-1)])):packet[key]+=value.ravel().tolist()
    skin=[source_surface_skin(native,row['surface'])for row in added]
    for key in ('weights','boneIndices','colors'):
        if packet[key]:
            values=([row[0 if key=='weights'else 1]for row in skin]if key!='colors'else np.asarray(native[key]).reshape(-1,4)[anchors])
            packet[key]+=np.asarray(values).ravel().tolist()
    for channel in packet['uv']:
        before=next((u for u in native['uv']if u['channel']==channel['channel']),None)
        values=np.asarray(before['values']).reshape(-1,4)[anchors].copy()if before else np.zeros((len(added),4))
        if channel['channel']==0 and added:values[:,:2]=[a['uv0']for a in added]
        if channel['channel']==2 and added:values[:]=[a['groom']for a in added]
        channel['values']+=values.ravel().tolist()
    frame_index=1
    for shape in packet['shapes']:
        for frame in shape['frames']:
            for key,value in (('vertices',pp[frame_index]-pp[0]),('normals',nn[frame_index]-nn[0]),('tangents',tt[frame_index]-tt[0])):frame[key]+=value.ravel().tolist()
            frame_index+=1
    packet['vertexCount']+=len(added)
    binding['vertices']+= [{k:v for k,v in a.items()if k!='groom'}for a in added]
    packet['subMeshes']=[a+b for a,b in zip(retained_indices,new_indices)]
    used=np.unique(np.concatenate(packet['subMeshes']))if added else np.arange(packet['vertexCount'])
    mapping=np.full(packet['vertexCount'],-1,int);mapping[used]=np.arange(len(used))
    for key,size in (('vertices',3),('normals',3),('tangents',4),('weights',4),('boneIndices',4),('colors',4)):
        if packet[key]:packet[key]=np.asarray(packet[key]).reshape(-1,size)[used].ravel().tolist()
    for channel in packet['uv']:channel['values']=np.asarray(channel['values']).reshape(-1,4)[used].ravel().tolist()
    for shape in packet['shapes']:
        for frame in shape['frames']:
            for key in ('vertices','normals','tangents'):frame[key]=np.asarray(frame[key]).reshape(-1,3)[used].ravel().tolist()
    packet['subMeshes']=[mapping[np.asarray(sub,int)].tolist()for sub in packet['subMeshes']]
    binding['vertices']=[binding['vertices'][int(i)]for i in used]
    packet['vertexCount']=len(used)
    assert not exterior_coverage or len(used)<65536,'Declared compact fine candidate exceeds the UInt16 source budget.'
    packet['indexFormat']=1 if len(used)>65535 else 0
    native_core_repair=restore_native_core(packet,native,binding,True)
    serialized_winding=discard_invalid_serialized_curves(packet,binding,native['vertexCount'],reports)
    final_added=int(np.sum(np.asarray(next(u['values']for u in packet['uv']if u['channel']==2)).reshape(-1,4)[:,2]>=17))
    assert final_added or removed_broad==0,'Serialized fine rejection cannot leave removed coarse coverage.'
    zero_fine_reason=None
    if not final_added:
        packet=copy.deepcopy(original);binding=decode_binding(binding_path)
        assert not np.any(np.asarray(next(u['values']for u in packet['uv']if u['channel']==2)).reshape(-1,4)[:,2]>=17),'A zero-fine fallback cannot discard existing fine coverage.'
        native_core_repair=restore_native_core(packet,native,binding,True)
        zero_fine_reason=dict(reason='NO_VALID_CURVE_AT_DECLARED_LOD_WITH_ALL_CURRENT_FACES_RETAINED',lod=lod,removedBroadTriangles=0,allCurrentFacesRetained=True,oldOwnedBroadTriangles=sum(r.get('oldBroadExteriorTriangles',0)for r in reports),sourceSlotEvidence=reports)

    points=np.asarray(packet['vertices']).reshape(-1,3);low=points.min(axis=0);high=points.max(axis=0)
    for shape in packet['shapes']:
        for frame in shape['frames']:
            value=points+np.asarray(frame['vertices']).reshape(-1,3);low=np.minimum(low,value.min(axis=0));high=np.maximum(high,value.max(axis=0))
    packet['bounds']=np.r_[(low+high)*.5,high-low].tolist()
    assert all(np.isfinite(packet[k]).all()for k in ('vertices','normals','tangents','weights'))
    assert [(s['name'],[f['weight']for f in s['frames']])for s in packet['shapes']]==[(s['name'],[f['weight']for f in s['frames']])for s in native['shapes']]
    for key in('vertices','normals','tangents','colors','bindPoses','weights'):
        bad=[(i,type(v).__name__)for i,v in enumerate(packet[key])if type(v)not in(int,float)or not math.isfinite(v)]
        assert not bad,(key,len(bad),bad[:4])
    core_coordinate_statistics=initialize_core_coordinates(packet,entry['rendererToPrefab'])
    packet['sourceFingerprint']=fingerprint(packet)[0]
    path=save_groom_packet(output,entry,packet)
    binding_out=output/entry['manifestEntry']['bindingFile']
    surface_provenance=prepare_surface_binding(binding,native)
    encode_binding(binding,binding_out)
    return dict(entry,generatedPacket=str(path),generatedSha256=sha(path),bindingPath=str(binding_out),bindingSha256=sha(binding_out),
        authoredFingerprint=packet['sourceFingerprint'],originalVertices=original['vertexCount'],addedVertices=final_added,compactedVertices=packet['vertexCount'],
        removedUnusedVertices=original['vertexCount']+final_added-packet['vertexCount'],physicalFrames=physical,
        addedPhysicalDeltaBytes=final_added*physical*36,addedVertexBytes=final_added*vertex_stride,serializedWinding=serialized_winding,zeroFineReason=zero_fine_reason,
        addedVertexLimit=maximum_vertices,addedVertexAndPhysicalByteBudget=physical_budget,retainedBudget=retained_budget,
        nativeVertexCount=native['vertexCount'],intrinsicNativePhysicalFrameBytes=native['vertexCount']*physical*36,
        physicalFrameCostPerNewVertexBytes=physical*36,packedVertexStrideBytes=vertex_stride,
        finalTriangles=sum(len(s)//3 for s in packet['subMeshes']),lod=lod,slots=reports,
        nativeRootCoreAndOrnamentsRetained=True,broadNativeOuterSheetReplaced=bool(removed_broad),nativeFrameNamesWeightsOrderPreserved=True,
        coreCoordinateStatistics=core_coordinate_statistics,nativeCoreRepair=native_core_repair,nativeUvSeamsSplitAtSharedSourceEdges=True,
        sheetLayout='layered-atlas-cards' if atlas_cards else 'individual-fibers',
        sourceUvChordErrorTexels=PROFILE['maximumSourceUvChordErrorTexels'],
        surfaceProvenance=surface_provenance,bindingRuntimeValidationPending=True,renderedArtAccepted=False,
        contactProtection=contact_proof,sourceTerminalField=terminal_field,
        sourceContactDomain=(dict(nativeFingerprint=native['sourceFingerprint'],protectedVertexIds=np.flatnonzero(protected).tolist(),
            contactVertexIds=contact_proof['confirmedContactVertexIds'],fixedVertexIds=list(fixed_vertices),
            distanceBasis=terminal_domain['distanceBasis'],settings=copy.deepcopy(terminal_settings))
                             if carrier_settings is not None else None),
        carrierGuides=(dict(nativeFingerprint=native['sourceFingerprint'],guides=carrier_guides,
            fullPhysicalSurfaceWalkBeforeReduction=True,sourceUvCutsDoNotRestartV=True)
            if carrier_settings is not None and not carrier_settings.get('sourceSurface')else None))



def strand_atlas_arrays(settings=None,*,purpose='fine'):
    """Evaluate the existing atlas recipe in memory before owned serialization."""
    if purpose not in('fine','primary'):raise ValueError('A groom atlas requires its declared material purpose.')
    default=(dict(strandsPerTile=20,halfWidthPixels=[.9,1.6],startRange=[.025,.18],endRange=[.82,.975],rootTaper=.07,tipTaper=.12)
        if purpose=='fine'else dict(strandsPerTile=6,halfWidthPixels=[8.5,9.5],startRange=[.005,.015],endRange=[.94,.96],rootTaper=.025,tipTaper=.035))
    settings=default if settings is None else copy.deepcopy(settings)
    if set(settings)!={'strandsPerTile','halfWidthPixels','startRange','endRange','rootTaper','tipTaper'}or type(settings['strandsPerTile'])is not int or not 1<=settings['strandsPerTile']<=128:
        raise ValueError('A strand atlas needs its declared strand count and physical sampling settings.')
    for name in('halfWidthPixels','startRange','endRange'):
        pair=settings[name]
        if not isinstance(pair,list)or len(pair)!=2 or not all(type(v)in(int,float)and np.isfinite(v)for v in pair)or not 0<pair[0]<=pair[1]:raise ValueError('Invalid strand atlas interval: '+name)
    if not(settings['startRange'][1]<settings['endRange'][0]<=settings['endRange'][1]<=1)or not all(type(settings[k])in(int,float)and np.isfinite(settings[k])and 0<settings[k]<1 for k in('rootTaper','tipTaper')):raise ValueError('Invalid strand atlas end coverage.')
    width,height=512,1024
    tile_width,tile_height=width//4,height//4
    color=np.full((height,width,4),255,dtype=np.uint8)
    normals=np.empty((height,width,3),dtype=np.uint8)
    rows=[]
    for tile in range(16):
        random=np.random.default_rng(98117+tile)
        pigment_random=np.random.default_rng(44173+tile)
        y,x=np.mgrid[:tile_height,:tile_width]
        v=(y+.5)/tile_height
        alpha=np.zeros((tile_height,tile_width))
        pigment=np.ones((tile_height,tile_width))
        nearest=np.full((tile_height,tile_width),np.inf)
        normal=np.zeros((tile_height,tile_width,3));normal[:,:,2]=1
        for strand in range(settings['strandsPerTile']):
            phase=float(random.uniform(0,2*math.pi))
            amplitude=float(random.uniform(.3,1.2))
            center=12+(strand+.5)*(tile_width-24)/settings['strandsPerTile']+float(random.uniform(-.6,.6))
            center=center+amplitude*np.sin(2*math.pi*v+phase)
            start=float(random.uniform(*settings['startRange']));end=float(random.uniform(*settings['endRange']))
            if purpose=='primary':
                start=0.
                if tile<8:end=1.;taper=np.ones_like(v)
                else:taper=np.clip((end-v)/settings['tipTaper'],0,1)
            else:taper=np.minimum(np.clip((v-start)/settings['rootTaper'],0,1),np.clip((end-v)/settings['tipTaper'],0,1))
            half_width=float(random.uniform(*settings['halfWidthPixels']))*taper
            distance=x+.5-center
            # Separate achromatic pigment per strand. Its independent seed
            # preserves the authored alpha, normal and strand placement.
            intensity=float(pigment_random.uniform(.65,1.))
            pigment_phase=float(pigment_random.uniform(0,2*math.pi))
            strand_pigment=np.clip(intensity+.025*np.sin(2*math.pi*v+pigment_phase),.6,1.)
            closer=np.abs(distance)<nearest
            pigment[closer]=strand_pigment[closer]
            nearest=np.minimum(nearest,np.abs(distance))
            coverage=np.clip(half_width-np.abs(distance)+.5,0,1)
            if purpose=='fine':coverage*=np.clip((v-start)*tile_height+.5,0,1)*np.clip((end-v)*tile_height+.5,0,1)
            elif tile>=8:coverage*=np.clip((end-v)*tile_height+.5,0,1)
            selected=(coverage>alpha)|((coverage==alpha)&closer)
            slope=np.clip(distance/np.maximum(half_width,.01),-.95,.95)*.4
            along=-slope*(amplitude*2*math.pi/tile_height)*np.cos(2*math.pi*v+phase)
            vector=unit(np.stack((slope,along,np.ones_like(slope)),axis=2))
            normal[selected]=vector[selected]
            alpha=np.maximum(alpha,coverage)
        left,top=(tile%4)*tile_width,(tile//4)*tile_height
        color[top:top+tile_height,left:left+tile_width,:3]=np.rint(pigment[:,:,None]*255).astype(np.uint8)
        color[top:top+tile_height,left:left+tile_width,3]=np.rint(alpha*255).astype(np.uint8)
        normals[top:top+tile_height,left:left+tile_width]=np.rint((normal*.5+.5)*255).astype(np.uint8)
        sampled=alpha[int(tile_height*.025):int(tile_height*.975),int(tile_width*.04):int(tile_width*.96)]
        rows.append(dict(tile=tile,strands=settings['strandsPerTile'],coverageAtCutoff=float(np.mean(alpha>=.35)),
            centralCoverageAtCutoff=float(np.mean(alpha[tile_height//3:2*tile_height//3]>=.35)),
            sampledCoverageByCutoff={str(cutoff):float(np.mean(sampled>=cutoff))for cutoff in(.35,.5,.7)},
            pigmentMinimum=float(pigment.min()),pigmentMaximum=float(pigment.max())))
    return color,normals,rows,settings


def generate_strand_atlas(output,settings=None,*,purpose='fine'):
    """Build an owned fine or primary atlas through the same recipe writer."""
    color,normals,rows,settings=strand_atlas_arrays(settings,purpose=purpose)
    width,height=color.shape[1],color.shape[0]
    primary=purpose=='primary';prefix='Primary'if primary else'Fine'
    textures=[];material_properties={}
    texture_inputs=[('_GroomColor',prefix+'GroomMask',color),('_GroomNormal',prefix+'GroomNormalRGB',normals)]
    if primary:texture_inputs.append(('_MainTex',prefix+'GroomMainGate',np.full((1,1,4),255,np.uint8)))
    for property,name,pixels in texture_inputs:
        # Primary arrays use shader/GPU bottom-left coordinates. PNG import
        # flips rows; preserve the old fine PNG bytes and orient primary's
        # explicit body/free-tip tile roles without changing that old path.
        path=output/(name+'.png');Image.fromarray(pixels[::-1]if primary else pixels).save(path)
        declaration=dict(name=name,path=path.name,sha256=sha(path),normalMap=property=='_GroomNormal',sRGB=False,mipmaps=True,compressionQuality=100)
        textures.append(declaration);material_properties[property]=declaration['name']
    recipe=dict(schemaVersion=1,bundleName='source_groom_primary.bundle'if primary else'source_groom_fine.bundle',meshes=[],textures=textures)
    recipe_path=output/'MeshRecipe.json';recipe_path.write_text(json.dumps(recipe,indent=2)+'\n',encoding='utf-8')
    receipt=dict(status='PRIMARY_LOCK_ATLAS_PREPARED_RENDER_REVIEW_PENDING'if primary else'STRAND_ATLAS_PREPARED_RENDER_REVIEW_PENDING',width=width,height=height,rows=rows,
        layout='4x4 tiles; separate continuous strand alpha and matching tangent normal; achromatic per-strand pigment',
        generatorSha256=sha(Path(__file__)),recipeSha256=sha(recipe_path),settings=settings)
    if primary:receipt.update(materialPurpose='primary-lock',tileEndModes=['continuous-body']*8+['owned-free-tip']*8,
        materialProperties=material_properties,
        recipe=dict(path=str(recipe_path.resolve()),sha256=receipt['recipeSha256']),
        sourceMainGate='owned neutral Main; coverage belongs to primary atlas alpha',
        nativeRootHardwareAndThinSourcesUseOriginalMaterial=True)
    (output/'Atlas.json').write_text(json.dumps(receipt,indent=2)+'\n',encoding='utf-8')
    return receipt


def main():
    global HERE
    parser=argparse.ArgumentParser();parser.add_argument('--workspace',type=Path,required=True);parser.add_argument('--asset',action='append');parser.add_argument('--lod',type=int);parser.add_argument('--input');parser.add_argument('--output',required=True)
    parser.add_argument('--strand-atlas',action='store_true')
    parser.add_argument('--atlas-profile',type=Path)
    parser.add_argument('--atlas-purpose',choices=('fine','primary'),default='fine')
    parser.add_argument('--core-repair-from');parser.add_argument('--restore-core-geometry',action='store_true')
    parser.add_argument('--core-multiplicity-from');parser.add_argument('--multiplicity-proof')
    parser.add_argument('--soft-coverage-pilot',action='store_true')
    parser.add_argument('--visible-shell-refinement',action='store_true')
    parser.add_argument('--native-hair-domain',action='store_true')
    parser.add_argument('--domain-contract')
    parser.add_argument('--curved-core',action='store_true')
    parser.add_argument('--layered-atlas-cards',action='store_true')
    parser.add_argument('--designs',type=Path)
    args=parser.parse_args();HERE=args.workspace.resolve();assert any(HERE.is_relative_to(ROOT/p)for p in('tools/game-test/artifacts','tools/asset-authoring/workspace')),'Use an ignored authoring workspace.';output=Path(args.output).resolve()
    assert output.is_relative_to(HERE)and output!=HERE
    source=(args.atlas_profile or Path(__file__))if args.strand_atlas else(args.core_multiplicity_from or args.core_repair_from or args.input)
    if source is None:parser.error('Geometry generation requires --input.')
    source=Path(source).resolve(strict=True)
    sys.path.insert(0,str(ROOT/'tools/asset-authoring'))
    from resource_storage import resource_operation,owned_resource_work
    metadata=[output/name for name in(('Atlas.json','MeshRecipe.json')if args.strand_atlas else('Generation.json',))]
    with resource_operation(),owned_resource_work('generate-groom-surfaces',source,output,source_role='receipt',metadata_outputs=metadata):
        return generate_owned(args,output,parser)


def generate_owned(args,output,parser):
    def argument_error(message):
        if parser is None:raise ValueError(message)
        parser.error(message)
    if args.strand_atlas:
        if any((args.input,args.asset,args.lod is not None,args.core_repair_from,args.restore_core_geometry,
                args.core_multiplicity_from,args.multiplicity_proof,args.soft_coverage_pilot,args.visible_shell_refinement,
                args.native_hair_domain,args.domain_contract,args.curved_core,args.layered_atlas_cards,args.designs)):
            argument_error('--strand-atlas cannot be combined with geometry generation options.')
        assert output.is_relative_to(HERE)and output!=HERE
        output.mkdir(exist_ok=True)
        assert not any(output.iterdir()),'Use a fresh strand atlas directory.'
        settings=read(args.atlas_profile)['primaryAtlas'if args.atlas_purpose=='primary'else'strandAtlas']if args.atlas_profile else None
        print(json.dumps(generate_strand_atlas(output,settings,purpose=args.atlas_purpose)));return
    if args.atlas_profile or args.atlas_purpose!='fine':argument_error('An atlas profile/purpose belongs to --strand-atlas.')
    if not args.input:argument_error('Geometry generation requires --input.')
    if args.native_hair_domain:
        assert args.domain_contract,'Native material/default/GPU classifier authority is required.'
        contract_path=Path(args.domain_contract).resolve();contract=read(contract_path)
        assert contract_path.is_relative_to(HERE.resolve())and contract['status']=='PASS_NATIVE_MASK_GPU_AND_SOURCE_DOMAIN_POLICY'
        for authority in contract['authorities']:assert sha(authority['path'])==authority['sha256']
        PROFILE.update(nativeHairDomain=True,domainContract=dict(path=str(contract_path),sha256=sha(contract_path)))
    else:assert not args.domain_contract
    if args.soft_coverage_pilot:
        PROFILE.update(softCoveragePilot=True,widthMetres=[.0011,.0022],liftMetres=[.0012,.004],maximumRibbonsByLod=[3200,1400,500],maximumAddedVerticesByLod=[30000,12000,4000],targetPoissonCoverage=.97,targetLengthExtentFraction=.12,targetLengthLimitsMetres=[.012,.028],traceStepMetres=.0025,maximumChordErrorMetres=.00035)
    if args.visible_shell_refinement:
        assert args.soft_coverage_pilot,'Exterior refinement belongs to the existing soft-coverage owner.'
        PROFILE.update(exteriorCoverageRefinement=True,maximumRibbonsByLod=[6200,1400,500],maximumAddedVerticesByLod=[50000,12000,4000],maximumAddedPhysicalAndVertexBytesByLod=[8*1024*1024,4*1024*1024,1024*1024],targetLengthLimitsMetres=[.02,.04],minimumCurveLengthMetres=.008)
    if args.layered_atlas_cards:
        assert args.native_hair_domain and args.soft_coverage_pilot and not args.curved_core
        assert not args.core_repair_from and not args.core_multiplicity_from
        PROFILE.update(layeredAtlasCards=True,widthMetres=[.0035,.007],liftMetres=[.0007,.002],
            maximumRibbonsByLod=[600,200,60],maximumAddedVerticesByLod=[16000,5000,2000],
            maximumAddedPhysicalAndVertexBytesByLod=[8*1024*1024,4*1024*1024,1024*1024],
            targetLengthLimitsMetres=[.025,.05],targetLengthExtentFraction=.18)
    design_pin=None
    if args.designs:
        if not args.soft_coverage_pilot or not args.native_hair_domain or len(args.asset or [])!=1:
            argument_error('Source designs require one declared asset with native-domain fine coverage.')
        design=read(args.designs);values=design['layeredAtlasCards']if args.layered_atlas_cards else design['assets'][args.asset[0]]
        fields={'widthMetres','liftMetres','targetLengthLimitsMetres'}
        if args.layered_atlas_cards:
            fields|={'targetLengthExtentFraction','targetPoissonCoverage','assumedMeasuredMaskCoverage','fringeFraction','fringeWidthMetres','fringeLengthMetres','fringeLiftMetres','retainedSourceBudget','exteriorCoverageRefinement','candidateAttemptFactor'}
            if 'supportTerminals'in values:fields.add('supportTerminals');validate_terminal_settings(values['supportTerminals'])
            if 'carrierSurface'in values:
                fields.add('carrierSurface');validate_carrier_settings(values['carrierSurface'])
                if 'supportTerminals'not in values:raise ValueError('Carrier reconstruction needs its contact and terminal source domain.')
            if 'lockSection'in values:
                fields.add('lockSection');section=values['lockSection']
                if not isinstance(section,dict)or set(section)!={'heightFraction','maximumSourceWidthFraction'}or any(type(value)not in(int,float)or not np.isfinite(value)for value in section.values())or not 0<section['heightFraction']<=.25 or not 0<section['maximumSourceWidthFraction']<=1:
                    raise ValueError('A medium lock requires its finite cross section and source-width bound.')
                if not values.get('carrierSurface',{}).get('sourceSurface'):raise ValueError('A medium lock lost its original Main source-pattern owner.')
        if design.get('schemaVersion')!=1 or set(values)!=fields:
            raise ValueError('Unknown source groom design.')
        for key in('widthMetres','liftMetres','targetLengthLimitsMetres',*(['fringeWidthMetres','fringeLengthMetres','fringeLiftMetres']if args.layered_atlas_cards else[])):
            pair=values[key]
            if not isinstance(pair,list)or len(pair)!=2 or not all(type(v)in(int,float)and np.isfinite(v)and 0<v<=.15 for v in pair)or pair[0]>pair[1]:
                raise ValueError('Invalid physical groom range: '+key)
        if args.layered_atlas_cards:
            if not all(type(values[k])in(int,float)and np.isfinite(values[k])and 0<values[k]<1 for k in('targetLengthExtentFraction','targetPoissonCoverage','assumedMeasuredMaskCoverage','fringeFraction'))or values['retainedSourceBudget']is not True or values['exteriorCoverageRefinement']is not True or type(values['candidateAttemptFactor'])is not int or not 1<=values['candidateAttemptFactor']<=8:raise ValueError('Invalid layered groom coverage or retained capacity policy.')
        PROFILE.update(values)
        if PROFILE.get('retainedSourceBudget'):
            for key in('maximumRibbonsByLod','maximumAddedVerticesByLod','maximumAddedPhysicalAndVertexBytesByLod','maximumAddedPhysicalAndVertexBytes'):
                PROFILE.pop(key,None)
        if not args.layered_atlas_cards:
            PROFILE.update(maximumRibbonsByLod=[900,300,90],maximumAddedVerticesByLod=[12000,4500,1500],
                maximumAddedPhysicalAndVertexBytesByLod=[4*1024*1024,2*1024*1024,512*1024],targetPoissonCoverage=.85)
        design_pin=dict(path=str(args.designs.resolve()),sha256=sha(args.designs),asset=args.asset[0])
    assert output.is_relative_to(HERE.resolve())and output!=HERE.resolve()
    output.mkdir(exist_ok=True)
    assert not any(output.iterdir()),'Use a fresh numeric candidate directory.'
    if args.core_multiplicity_from:
        assert not args.core_repair_from and not args.restore_core_geometry and args.multiplicity_proof
        source=Path(args.core_multiplicity_from).resolve();proof=Path(args.multiplicity_proof).resolve()
        assert source.is_file()and proof.is_file()and source.is_relative_to(HERE.resolve())and proof.is_relative_to(HERE.resolve())
        core_multiplicity_from(source,proof,output);return
    if args.core_repair_from:
        source=Path(args.core_repair_from).resolve();assert source.is_file()and source.is_relative_to(HERE.resolve())
        core_repair_from(source,output,args.restore_core_geometry);return
    input_path=Path(args.input).resolve();assert input_path.is_file()and input_path.is_relative_to(HERE.resolve())
    inputs=read(input_path)
    if args.curved_core:
        assert not args.core_repair_from and not args.core_multiplicity_from
        assert not args.soft_coverage_pilot and not args.visible_shell_refinement and not args.native_hair_domain
        baseline=inputs.get('baselineProfile',inputs.get('profile'));assert baseline and baseline.get('nativeHairDomain')
        contract_path=Path(baseline['domainContract']['path']);assert sha(contract_path)==baseline['domainContract']['sha256']
        contract=read(contract_path);assert contract['status']=='PASS_NATIVE_MASK_GPU_AND_SOURCE_DOMAIN_POLICY'
        for authority in contract['authorities']:assert sha(authority['path'])==authority['sha256']
        PROFILE.update(copy.deepcopy(baseline))
        if PROFILE.get('carrierSurface'):validate_carrier_settings(PROFILE['carrierSurface'])
    selected=[r for r in inputs['rows']if (not args.asset or r['asset']in args.asset)and(args.lod is None or re.search(r'_LOD'+str(args.lod)+r'(?:_|\b)',r['nativeRenderer'],re.I))]
    assert selected
    rows=[]
    result: dict[str,Any]=dict(status='PRIVATE_SOURCE_FLOW_COVERAGE_NUMERIC_CANDIDATE_NOT_RENDERED',profile=PROFILE,rows=rows,
        generatorPath=str(Path(__file__).resolve()),generatorSha256=sha(Path(__file__)),
        sourceInput=str(input_path),sourceInputSha256=sha(input_path),
        removedOldBroadOuterGeometry=False,allNativeSourceFramesKept=True,productChanged=False,artAccepted=False)
    if design_pin is not None:result['sourceDesign']=design_pin
    stage=dict(operation='generate-row')
    try:
        for entry in selected:
            stage=dict(operation='generate-row',asset=entry['asset'],lod=entry.get('lod'),nativeRenderer=entry['nativeRenderer'])
            row=curved_core_row(entry,output)if args.curved_core else generate(entry,output);rows.append(row)
            result['removedOldBroadOuterGeometry']|=bool(row['broadNativeOuterSheetReplaced'])
            print(entry['asset'],entry['nativeRenderer'],row['originalVertices'],row['compactedVertices'],row['addedPhysicalDeltaBytes'],flush=True)
        if args.curved_core or design_pin is not None:
            source_design=design_pin is not None
            if args.curved_core:
                result['geometryMode']='curved-core-native-domain-v1'
                result['allNativeRenderFacesKept']=False
            result['status']='PENDING_SOURCE_GROOM_NUMERIC_PROOFS'if source_design else'PENDING_CURVED_CORE_NUMERIC_PROOFS'
            result['numericProofs']=[]
            for row in rows:
                stage=dict(operation='prove-row',asset=row['asset'],lod=row['lod'],nativeRenderer=row['nativeRenderer'])
                result['numericProofs'].append(prove_row(row,PROFILE))
            result['status']='PASS_SOURCE_GROOM_NUMERIC_PROOFS_RENDER_PENDING'if source_design else'PASS_CURVED_CORE_NUMERIC_PROOFS_RENDER_PENDING'
        stage=dict(operation='write-generation');write_generation(output,result)
    except BaseException as error:
        result.update(status='FAILED_SOURCE_GROOM_GENERATION',failedStage=stage,
            generationError=dict(type=type(error).__name__,message=str(error)),allNativeSourceFramesKept=None)
        if stage['operation']=='prove-row':
            result.update(status='FAILED_SOURCE_GROOM_NUMERIC_PROOFS'if design_pin is not None else'FAILED_CURVED_CORE_NUMERIC_PROOFS',numericError=str(error))
        try:
            from resource_storage import work_files
            created=work_files(output,all_created=True)-{output/'Generation.json'}
            write_generation(output,result,state='failed',created_payloads=created)
        except BaseException as recording:
            error.add_note('Generation failure recording also failed: '+type(recording).__name__+': '+str(recording))
        raise

if __name__=='__main__':main()
