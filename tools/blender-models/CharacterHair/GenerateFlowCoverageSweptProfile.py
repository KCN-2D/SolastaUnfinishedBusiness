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
read=lambda p:json.loads(Path(p).read_text(encoding='utf-8-sig'))
sha=lambda p:hashlib.sha256(Path(p).read_bytes()).hexdigest()
unit=lambda value:np.asarray(value)/np.maximum(np.linalg.norm(value,axis=-1,keepdims=True),1e-20)
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

def texture_sampler(row):
    if row is None:return None
    assert sha(row['path'])==row['sha256']
    pixels=np.asarray(Image.open(row['path']).convert('RGBA'))[::-1].astype(float)/255
    def sample(uv):
        uv=np.asarray(uv)*row['scale']+row['offset'];height,width=pixels.shape[:2]
        xy=uv*[width,height]-.5;low=np.floor(xy).astype(int);f=xy-low
        value=np.zeros(4)
        for dx,dy,weight in ((0,0,(1-f[0])*(1-f[1])),(1,0,f[0]*(1-f[1])),(0,1,(1-f[0])*f[1]),(1,1,f[0]*f[1])):
            value+=pixels[(low[1]+dy)%height,(low[0]+dx)%width]*weight
        return value
    return sample

def closest_triangle(point,triangles):
    p=np.asarray(point);a=triangles[:,0];v=triangles[:,1]-a;w=triangles[:,2]-a;d=p-a
    vv=np.sum(v*v,axis=1);vw=np.sum(v*w,axis=1);ww=np.sum(w*w,axis=1);dv=np.sum(d*v,axis=1);dw=np.sum(d*w,axis=1);det=vv*ww-vw*vw
    valid=abs(det)>1e-18;u=np.zeros(len(a));z=np.zeros(len(a));u[valid]=(dv[valid]*ww[valid]-dw[valid]*vw[valid])/det[valid];z[valid]=(dw[valid]*vv[valid]-dv[valid]*vw[valid])/det[valid]
    bary=np.stack((1-u-z,u,z),axis=1);inside=valid&(bary.min(axis=1)>=0);candidates=[]
    for ids in [(0,1),(1,2),(2,0)]:
        start=triangles[:,ids[0]];edge=triangles[:,ids[1]]-start;f=np.clip(np.sum((p-start)*edge,axis=1)/np.maximum(np.sum(edge*edge,axis=1),1e-18),0,1);weights=np.zeros((len(a),3));weights[:,ids[0]]=1-f;weights[:,ids[1]]=f;candidates.append(weights)
    bary[~inside]=candidates[0][~inside];best=np.sum((np.einsum('ij,ijk->ik',bary,triangles)-p)**2,axis=1)
    for weights in candidates:
        dist=np.sum((np.einsum('ij,ijk->ik',weights,triangles)-p)**2,axis=1);use=(~inside)&(dist<best);bary[use]=weights[use];best[use]=dist[use]
    index=int(np.argmin(best));return index,bary[index],float(math.sqrt(best[index]))

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
    core_count = curved['vertices'] if curved else 0
    assert fine.sum() + core_count == row['addedVertices'] <= profile['maximumAddedVerticesByLod'][row['lod']]
    assert packet['vertexCount'] < 65536
    assert np.all((uv[fine, :2] >= 0) & (uv[fine, :2] <= 1)) and np.all((abs(uv[fine, 3]) >= 1) & (abs(uv[fine, 3]) < 2))
    assert np.all(uv[fine, 2] <= 48) and np.all(uv[fine, 2] == np.floor(uv[fine, 2]))
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
                physicalFrames=row['physicalFrames'], newFineVertices=int(fine.sum()),
                fineSurfaceVertices=int(((uv[:, 2] >= 17) & (uv[:, 2] < 33)).sum()), fineFringeVertices=int((uv[:, 2] >= 33).sum()),
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

def material_sampler(material,name):
    return texture_sampler(material['maps'].get(name))

class SourceHairDomain:
    """Conservative mip0 hair ownership from the native linear GPU export.

    The shader classifies R <= .5 as hair.  Exported PNG channels have an
    additional half-UNorm8 rounding interval; uncertainty is retained rather
    than silently calling a rounded boundary value hair.  UV rectangles cover
    every bilinear tap of an emitted source-UV segment/triangle.  Native coarse
    faces containing metal/uncertainty therefore remain intact.
    """
    def __init__(self,material):
        assert material['shader']=='Custom/FakeAnisoHair'
        assert '_MainTex'in material['maps'],'An explicit observed source Main texture is required.'
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


def plan_curved_core(coordinates, slot_triangles, selected_faces, width, vertex_budget, reusable_vertices=(),
                     *, positions=None, physical_groups=None, subdivide_small_faces=True) -> 'dict[str, Any]':
    """Partition source faces with shared physical-edge samples and face centers.

    Cells cover their source triangle, including its boundary. Shared source
    edges reuse one vertex; UV/material cuts retain independent vertices. The
    native vertex prefix remains deformation authority, not render topology.
    Coordinates are the existing physical groom field converted to metres.
    """
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


def couple_fine_to_core(plan, core_p, core_n, core_t, fine_p, fine_n, fine_t, records,
                        source_p, source_n, source_t, source_b):
    """Keep attached fibers above the actual rendered child surface.

    Source triangle provenance selects the child in barycentric coordinates;
    ornaments and fibers on unmodified source faces retain their own ownership.
    The caller transports the resulting rest attachment through every native
    physical frame using the same triangle basis as the runtime surface plan.
    """
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
    groups = collections.defaultdict(list)
    for index, record in enumerate(records):
        if not record['native'] and record.get('surface'):
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
            assert np.all(inside.any(axis=1)), ('Attached fiber is outside its source-owned curved partition.',
                key, ids[~inside.any(axis=1)].tolist(), child_bary.min(axis=2).max(axis=1)[~inside.any(axis=1)].tolist())
            child = inside.argmax(axis=1)
            cb = child_bary[np.arange(len(ids)), child]
            cb = np.maximum(cb, 0.); cb /= cb.sum(axis=1, keepdims=True)
            corners = children[child]
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
            transfer = lambda values: np.einsum('nij,nj->ni', rotation, values)
            moved.extend(ids.tolist())
            positions.append(new_p + transfer(fine_p[ids] - old_p))
            normals.append(unit(transfer(fine_n[ids])))
            tangents.append(unit(transfer(fine_t[ids])))
    if not moved:
        empty = np.empty((0, 3))
        return np.empty(0, dtype=int), empty.copy(), empty.copy(), empty.copy()
    return np.asarray(moved, dtype=int), np.concatenate(positions), np.concatenate(normals), np.concatenate(tangents)


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


def prepare_geometry_frames(plan,positions):
    """Use one well-conditioned native position frame for each physical sample.

    Texture provenance retains its original triangle. An edge or corner shared
    by UV charts instead uses the same geometric driver on both sides.
    """
    physical=np.asarray(plan['physicalGroups'],dtype=int);drivers={}
    poses=np.asarray(positions);poses=poses[None]if poses.ndim==2 else poses
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


def standalone_cloth_attachment(packet,native,binding):
    """Compile geometric samples to the native prefab's anchor attachment format.

    Both skin and serialized cloth use the same canonical physical anchor at a
    UV alias. Geometry, texture coordinates and stored morphs remain unchanged.
    The returned single-anchor binding is an authoring input for prefab baking.
    """
    result=copy.deepcopy(packet);attachment=copy.deepcopy(binding)
    p,n,t,b=native_frames(native,binding['matrix'])
    matrix=np.asarray(binding['matrix']).reshape(4,4).T;linear=matrix[:3,:3]
    points=np.asarray(packet['vertices']).reshape(-1,3)@linear.T+matrix[:3,3]
    normals=unit(np.asarray(packet['normals']).reshape(-1,3)@np.linalg.inv(linear))
    tangents=np.asarray(packet['tangents']).reshape(-1,4)[:,:3]@linear.T
    tangents=unit(tangents-normals*np.sum(normals*tangents,axis=1)[:,None])
    source_weights=np.asarray(native['weights']).reshape(-1,4)
    source_indices=np.asarray(native['boneIndices']).reshape(-1,4)
    weights=np.asarray(packet['weights']).reshape(-1,4).copy()
    indices=np.asarray(packet['boneIndices']).reshape(-1,4).copy()
    changed=0;compiled=0
    for index,vertex in enumerate(attachment['vertices']):
        geometry=vertex.pop('geometrySurface',None)
        vertex.pop('surface',None)
        if geometry is None:continue
        assert not vertex['native'] and index>=native['vertexCount']
        anchor=int(geometry['triangle'][int(np.argmax(np.asarray(geometry['barycentric'],dtype='<f4')))])
        changed+=int(not np.array_equal(weights[index],source_weights[anchor])or not np.array_equal(indices[index],source_indices[anchor]))
        weights[index]=source_weights[anchor];indices[index]=source_indices[anchor]
        basis=np.asarray((t[0,anchor],b[0,anchor],n[0,anchor]))
        vertex.update(anchor=anchor,offset=(basis@(points[index]-p[0,anchor])).tolist(),
                      normal=(basis@normals[index]).tolist(),tangent=(basis@tangents[index]).tolist())
        compiled+=1
    result['weights']=weights.ravel().tolist();result['boneIndices']=indices.ravel().tolist()
    attachment.pop('schemaVersion',None);attachment.pop('sourceVertexCount',None)
    result['sourceFingerprint']=fingerprint(result)[0]
    return result,attachment,dict(compiledVertices=compiled,changedSkinVertices=changed,
        canonicalSkinAndClothAnchorShared=True,nativePrefixUnchanged=True,
        positionsNormalsTangentsUvColorsAndAllStoredFramesUnchanged=True,runtimeSourceGraphRequired=False)


def curved_vertex_capacity(native_count,current_count,added_count,added_limit):
    """Account for retained source support separately from new groom allocation."""
    retained_support=current_count-native_count-added_count
    assert retained_support>=0
    maximum_count=min(65535,native_count+retained_support+added_limit)
    assert current_count<=maximum_count
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


def curved_core_row(entry, output):
    """Replace core render faces without replacing native deformation samples."""
    assert entry['category'] in ('Hair', 'Beard', 'IntegratedHair'), 'Curved hair support requires a declared groom renderer.'
    assert sha(entry['generatedPacket']) == entry['generatedSha256']
    assert sha(entry['nativePacketPath']) == entry['nativePacketSha256']
    assert sha(entry['bindingPath']) == entry['bindingSha256']
    current, native = read(entry['generatedPacket']), read(entry['nativePacketPath'])
    binding = decode_binding(Path(entry['bindingPath']))
    validate_surface_provenance(binding, native)
    assert binding.get('schemaVersion') == 3, 'Explicit existing source-triangle provenance is required.'
    assert fingerprint(current)[0] == entry['authoredFingerprint']
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
    slot_triangles, selected_faces, selected_keys = [], [], []
    unrepresentable_native_faces=0
    for slot, indices in enumerate(native['subMeshes']):
        triangles = np.asarray(indices, dtype=int).reshape(-1, 3)
        representable=serializable_core_faces(frame_p,inverse,triangles)
        domain = SourceHairDomain(entry['materials'][slot])
        selected = np.zeros(len(triangles), dtype=bool)
        for face, triangle in enumerate(triangles):
            if not all(binding['vertices'][int(i)]['role'] in ('volume-support', 'thin-root') for i in triangle):
                continue
            if not np.all(groom[triangle, 2] == 0) or not np.all(native_hand[triangle] == native_hand[triangle[0]]):
                continue
            edges = np.stack((coordinates[triangle[1]] - coordinates[triangle[0]], coordinates[triangle[2]] - coordinates[triangle[0]]), axis=1)
            if abs(np.linalg.det(edges)) <= 1e-16 or np.linalg.cond(edges) >= 1e5:
                continue
            points = frame_p[:, triangle]
            if np.any(np.linalg.norm(np.cross(points[:, 1] - points[:, 0], points[:, 2] - points[:, 0]), axis=1) <= 1e-14):
                continue
            # Support keeps the native alpha sample. Transparent native pixels
            # remain transparent; only the metal/uncertain domain is excluded.
            if domain.range('_Mask', source_uv[triangle])[1] > .5:
                continue
            if not representable[face]:
                unrepresentable_native_faces+=1
                continue
            selected[face] = True
        slot_triangles.append(triangles)
        selected_faces.append(selected)
        selected_keys.append({tuple(sorted(map(int, tri))) for tri in triangles[selected]})
    assert any(np.any(s) for s in selected_faces), 'No complete native hair-only core domain.'
    coordinates, metric_report = physical_core_coordinates(frame_p[0], coordinates, slot_triangles, selected_faces)
    groups = collections.defaultdict(list)
    for index in np.flatnonzero((groom[:, 2] >= 17) & (groom[:, 2] < 33)):
        vertex = binding['vertices'][int(index)]
        groups[(vertex['slot'], int(groom[index, 2]), struct.pack('<f', float(groom[index, 3])))].append(int(index))
    removable = []
    for key, indices in groups.items():
        if all(tuple(sorted(binding['vertices'][i]['surface']['triangle'])) in selected_keys[key[0]] for i in indices):
            removable.append((float(abs(groom[indices[0], 3]) - 1.), key, indices))
    # Small hierarchy ranks remain visible at reduced fine density.
    removable.sort(key=lambda item: (-item[0], item[1]))
    available = sum(len(indices) for _, _, indices in removable)
    fraction = float(PROFILE.get('coreSurfaceFineAllocationFraction', .18))
    assert 0. < fraction < 1.
    retained_support,maximum_count,headroom=curved_vertex_capacity(
        count,current['vertexCount'],int(entry['addedVertices']),int(entry['addedVertexLimit']))
    # A zero-edge-split partition still owns every selected source corner and
    # one centroid per face. Count both before distributing the fine budget.
    minimum_core=sum(int(np.sum(selected))+len(np.unique(triangles[selected]))
                     for triangles,selected in zip(slot_triangles,selected_faces))
    adaptive_resolution=minimum_core>headroom+available
    shared_native_corners=False
    if adaptive_resolution:
        # Existing small source faces need no extra centroid. Refine only
        # above the solved physical width while retaining every source face.
        minimum_core=sum(len(np.unique(triangles[selected]))for triangles,selected in zip(slot_triangles,selected_faces))
        if minimum_core>headroom+available:
            # Dense source LODs already own their corner channels. Keep their
            # exact native groom field when sharing that prefix, then allocate
            # only the samples required above its existing spatial resolution.
            shared_native_corners=True;coordinates=source_coordinates.copy();minimum_core=0
            metric_report=[dict(mode='native-source-field-and-corners-shared-without-channel-mutation',sourceVertices=count)]
    budget=min(headroom+available,max(headroom+int(available*fraction),minimum_core))
    assert budget > 0, 'No source-owned interior fine allocation can be reassigned.'
    lod = int(entry['lod'])
    preferred = float(PROFILE['widthMetres'][1]) * 2 ** lod

    def partition(width):
        return plan_curved_core(coordinates, slot_triangles, selected_faces, width, budget,
                                reusable_vertices=range(count)if shared_native_corners else(),
                                positions=frame_p[0],physical_groups=physical_groups,subdivide_small_faces=not adaptive_resolution)

    try:
        plan = partition(preferred)
    except OverflowError:
        low, high = preferred, preferred
        for _ in range(8):
            high *= 2.
            try:
                plan = partition(high)
                break
            except OverflowError:
                low = high
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
    positions, normals, tangents = evaluate_curved_core(evaluation, frame_p[0], frame_n[0], frame_t[0], coordinates)
    normals=share_core_normals(evaluation,normals)
    tangents=unit(tangents-normals*np.sum(tangents*normals,axis=1)[:,None])
    pp, nn, tt, curvature_proof = bound_curved_core(plan, positions, normals, tangents, frame_p, frame_n, frame_t, frame_b, inverse)
    curvature_proof['unrepresentableNativeFacesRetained']=unrepresentable_native_faces
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
    surface_p, surface_n, surface_t = [], [], []
    allocated_lookup = {index: offset for offset, index in enumerate(allocated_indices)}
    for index, record in enumerate(plan['vertices']):
        if 'nativeVertex' in record:
            native_index = record['nativeVertex']
            surface_p.append(frame_p[0, native_index]); surface_n.append(frame_n[0, native_index]); surface_t.append(frame_t[0, native_index])
        else:
            offset = allocated_lookup[index]
            surface_p.append(pp[0, offset]); surface_n.append(nn[0, offset]); surface_t.append(tt[0, offset])
    retained_p = np.asarray(packet['vertices']).reshape(-1, 3) @ linear.T
    retained_n = unit(np.asarray(packet['normals']).reshape(-1, 3) @ inverse)
    retained_t = np.asarray(packet['tangents']).reshape(-1, 4)[:, :3] @ linear.T
    retained_t = unit(retained_t - retained_n * np.sum(retained_t * retained_n, axis=1)[:, None])
    fine_ids, fine_p, fine_n, fine_t = couple_fine_to_core(
        plan, np.asarray(surface_p), np.asarray(surface_n), np.asarray(surface_t), retained_p, retained_n, retained_t,
        new_binding['vertices'], frame_p[0], frame_n[0], frame_t[0], frame_b[0])
    fine_records = [new_binding['vertices'][int(i)] for i in fine_ids]
    prepare_geometry_frames(dict(plan,vertices=fine_records),frame_p)
    fine_frames = transport_curved_core(fine_p, fine_n, fine_t,
        [dict(r['surface'],geometrySurface=r['geometrySurface']) for r in fine_records], frame_p, frame_n, frame_t, frame_b)
    fine_pp = fine_frames[0] @ inverse.T
    fine_nn = unit(fine_frames[1] @ linear)
    fine_tt = unit(fine_frames[2] @ inverse.T)
    fine_tt = unit(fine_tt - fine_nn * np.sum(fine_nn * fine_tt, axis=2)[:, :, None])
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
        maximumRestDisplacementMetres=float(np.linalg.norm(fine_p-retained_p[fine_ids], axis=1).max()) if len(fine_ids) else 0.,
        sourceTriangleProvenanceUnchanged=True, sourceUvAndGroomPhaseUnchanged=True,
        runtimeTriangleFrameTransport=True)
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
    for key in ('weights', 'boneIndices', 'colors'):
        if native[key]:
            packet[key] += np.asarray(native[key]).reshape(-1, 4)[anchors].ravel().tolist()
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
    packet_path = output / (entry['asset'] + '_LOD' + str(lod) + '.json')
    packet_path.write_text(json.dumps(packet, separators=(',', ':')), encoding='utf-8')
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
                renderedArtAccepted=False, bindingRuntimeValidationPending=True)


def validate_curved_core(row, packet, binding, native):
    replacement = row['curvedCoreSurface']
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
            for a, b in zip(triangle, np.roll(triangle, -1)):
                edges[int(parent)][tuple(sorted((int(a), int(b))))] += 1
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
            minimum_orientation = min(minimum_orientation, float(orientation.min()))
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
    (output/'Generation.json').write_text(json.dumps(result,indent=2),encoding='utf-8')

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
    (output/'Generation.json').write_text(json.dumps(result,indent=2),encoding='utf-8')



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
    rejected_vertices=set().union(*(groups[key]for key in bad));assert min(rejected_vertices)>=native_count
    for slot,sub in enumerate(packet['subMeshes']):
        tri=np.asarray(sub,dtype=int).reshape(-1,3);keep=np.array([not any(int(i)in rejected_vertices for i in t)for t in tri]);packet['subMeshes'][slot]=tri[keep].ravel().tolist()
    used=np.union1d(np.arange(native_count),np.unique(np.concatenate(packet['subMeshes'])));mapping=np.full(packet['vertexCount'],-1,int);mapping[used]=np.arange(len(used))
    for key,width in(('vertices',3),('normals',3),('tangents',4),('weights',4),('boneIndices',4),('colors',4)):
        if packet[key]:packet[key]=np.asarray(packet[key]).reshape(-1,width)[used].ravel().tolist()
    for channel in packet['uv']:channel['values']=np.asarray(channel['values']).reshape(-1,4)[used].ravel().tolist()
    for shape in packet['shapes']:
        for frame in shape['frames']:
            for key in('vertices','normals','tangents'):frame[key]=np.asarray(frame[key]).reshape(-1,3)[used].ravel().tolist()
    packet['subMeshes']=[mapping[np.asarray(sub,int)].tolist()for sub in packet['subMeshes']];binding['vertices']=[binding['vertices'][int(i)]for i in used]
    removed=packet['vertexCount']-len(used);packet['vertexCount']=len(used);result['removedFineVertices']=removed
    for report in reports or []:
        rejected=sum(key[0]==report['slot']for key in bad)
        if not rejected:continue
        report['preSerializationSamplingStatistics']={key:report[key]for key in('acceptedCurvedRibbons','meanAcceptedCurveLengthMetres','maximumAcceptedCurveLengthMetres','meanAcceptedNodes')}
        report['acceptedCurvedRibbons']-=rejected;report['rejected']['serialized-full-physical-frame-winding']=rejected
        for key in('meanAcceptedCurveLengthMetres','maximumAcceptedCurveLengthMetres','meanAcceptedNodes'):report[key]=None
    return result

def generate(entry,output) -> 'dict[str, Any]':
    soft_coverage=bool(PROFILE.get('softCoveragePilot',False))
    exterior_coverage=bool(PROFILE.get('exteriorCoverageRefinement',False))
    assert sha(entry['packetPath'])==entry['packetSha256']and sha(entry['nativePacketPath'])==entry['nativePacketSha256']
    original,native=read(entry['packetPath']),read(entry['nativePacketPath'])
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
    np_frames,nn_frames,nt_frames,nb_frames=native_frames(native,entry['rendererToPrefab'])
    world_p=np.asarray(original['vertices']).reshape(-1,3)@linear.T+m[:3,3]
    world_n=unit(np.asarray(original['normals']).reshape(-1,3)@np.linalg.inv(linear))
    uv={u['channel']:np.asarray(u['values']).reshape(-1,4)for u in original['uv']}
    groom=uv[2]
    source_uv=uv[0][:,:2]
    native_uv=np.asarray(next(u for u in native['uv']if u['channel']==0)['values']).reshape(-1,4)[:,:2]
    random=np.random.default_rng(int(native['sourceFingerprint'][:16],16))
    packet=copy.deepcopy(original)
    retained_indices=[]
    added=[]
    new_indices=[[]for _ in original['subMeshes']]
    reports=[]
    physical=sum(len(s['frames'])for s in native['shapes'])
    uv_floats=sum(u['dimension']for u in original['uv'])
    vertex_stride=(3+3+4+4+uv_floats+(4 if original['colors']else 0))*4+16
    physical_budget=PROFILE.get('maximumAddedPhysicalAndVertexBytesByLod',[PROFILE['maximumAddedPhysicalAndVertexBytes']]*3)[lod]
    maximum_vertices=min(PROFILE['maximumAddedVerticesByLod'][lod],physical_budget//max(1,physical*36+vertex_stride))
    if exterior_coverage:maximum_vertices=min(maximum_vertices,65535-native['vertexCount'])
    vertex_budget_ribbons=max(1,maximum_vertices//8)
    # This budget is derived from real physical-frame bytes, not hairstyle names.
    ribbon_budget=min(PROFILE['maximumRibbonsByLod'][lod],vertex_budget_ribbons)
    for slot,flat in enumerate(original['subMeshes']):
        triangles=np.asarray(flat).reshape(-1,3)
        material=entry['materials'][slot]
        color=material_sampler(material,'_MainTex')
        metal=material_sampler(material,'_Mask')
        domain=SourceHairDomain(material)if PROFILE.get('nativeHairDomain')else None
        def eligible(uv0):return domain.hair([uv0])if domain is not None else(color is None or color(uv0)[3]>=.51)and(metal is None or metal(uv0)[0]<=.01)
        core=np.asarray([tri for tri in triangles if all(binding['vertices'][int(i)]['role']in ('volume-support','thin-root')for i in tri)],int).reshape(-1,3)
        outer=triangles[np.all((groom[triangles,2]>.5)&(groom[triangles,2]<16.5),axis=1)]
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
        cross=np.cross(positions[:,1]-positions[:,0],positions[:,2]-positions[:,0])
        areas=np.linalg.norm(cross,axis=1)*.5
        eligible_tri=np.array([eligible(value.mean(axis=0))for value in uv0])
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
        trace_stops=collections.Counter()
        def project(point,normal,index):
            choices=neighborhood(index)
            facing=unit(normals[choices].mean(axis=1))@normal>PROFILE['minimumFacing']
            choices=choices[facing]
            if not len(choices):trace_stops['no-facing-neighbor']+=1;return None
            local,bary,distance=closest_triangle(point,positions[choices])
            if distance>PROFILE['sourceProjectionLimitMetres']:trace_stops['source-projection-distance']+=1;return None
            chosen=int(choices[local]);p=bary@positions[chosen];n=unit(bary@normals[chosen]);u=bary@uv0[chosen]
            if not eligible(u):trace_stops['source-alpha-or-metal']+=1;return None
            return dict(point=p,normal=n,uv=u,triangle=chosen,bary=bary,chart=chart_ids[chosen])
        def source_edge_route(start,end):
            """Insert both exact UV values at every physical source seam.

            Each intermediate segment lies in one real native triangle. RDP
            later preserves its UV interpolation, instead of connecting a pair
            of unrelated chart samples through the atlas.
            """
            first,last=start['triangle'],end['triangle']
            if first==last:return [end]
            allowed=set(neighborhood(first).tolist());previous: dict[int, tuple | None]={first:None};queue=collections.deque([first])
            while queue and last not in previous:
                current=queue.popleft()
                for edge in edge_neighbors[current]:
                    other=edge[0]
                    if other in previous or other not in allowed:continue
                    if np.dot(unit(normals[other].mean(axis=0)),start['normal'])<=PROFILE['minimumFacing']:continue
                    previous[other]=(current,edge);queue.append(other)
            if last not in previous:
                trace_stops['no-shared-source-edge-route']+=1;return None
            route=[];cursor=last
            while cursor!=first:
                step=previous[cursor];assert step is not None
                parent,edge=step;route.append((parent,edge));cursor=parent
            route.reverse();result=[];current_point=start['point']
            for index,(other,a,b,oa,ob,continuous)in route:
                pa,pb=positions[index,[a,b]];edge=pb-pa
                delta=end['point']-current_point;length=np.linalg.norm(delta)
                axis=delta/max(length,1e-20)
                perpendicular=edge-axis*np.dot(edge,axis)
                origin=pa-current_point;origin-=axis*np.dot(origin,axis)
                denominator=float(perpendicular@perpendicular)
                alpha=float(np.clip(-np.dot(origin,perpendicular)/denominator,0,1))if denominator>1e-18 else float(np.clip(np.dot(end['point']-pa,edge)/max(edge@edge,1e-20),0,1))
                def edge_node(ti,ai,bi):
                    bary=np.zeros(3);bary[ai]=1-alpha;bary[bi]=alpha
                    return dict(point=bary@positions[ti],normal=unit(bary@normals[ti]),uv=bary@uv0[ti],triangle=ti,bary=bary,chart=chart_ids[ti])
                outgoing=edge_node(index,a,b);incoming=edge_node(other,oa,ob)
                assert np.linalg.norm(outgoing['point']-incoming['point'])<=PROFILE['geometricSeamToleranceMetres']*1.01
                if not continuous:result.append(outgoing)
                result.append(incoming);current_point=incoming['point']
            result.append(end);return result
        extent=float(np.linalg.norm(positions.max(axis=(0,1))-positions.min(axis=(0,1))))
        target_length=float(np.clip(extent*PROFILE['targetLengthExtentFraction'],PROFILE['targetLengthLimitsMetres'][0],PROFILE['targetLengthLimitsMetres'][1]))
        length_limits=[target_length*.65,target_length]
        mean_l=np.mean(length_limits);mean_w=np.mean(PROFILE['widthMetres'])
        visible_area=mean_l*mean_w*.72*PROFILE['assumedMeasuredMaskCoverage']
        requested=int(math.ceil(-math.log(1-PROFILE['targetPoissonCoverage'])*area.sum()/visible_area))
        count=min(ribbon_budget,max(1,requested))
        selected=random.choice(len(core),size=count,p=area/area.sum(),replace=not soft_coverage or count>int(np.count_nonzero(area)))
        boundary=[]
        if soft_coverage:
            for ti in range(len(core)):
                if not eligible_tri[ti]or(exterior_source is not None and not exterior_source[3][ti]):continue
                for a,b in ((0,1),(1,2),(2,0)):
                    if not any(edge[1]==a and edge[2]==b for edge in edge_neighbors[ti]):
                        length=float(np.linalg.norm(positions[ti,a]-positions[ti,b]))
                        if length>1e-6:boundary.append((ti,a,b,length))
        fringe_count=min(int(count*.16),len(boundary))
        accepted=0;failed=collections.Counter();accepted_lengths=[];accepted_nodes=[]
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
                swept=points[:,None,:]+normals_[:,None,:]*height[:,None,None]+across[:,None,:]*(np.array([-1.,1.])[None,:,None]*width*.5*taper[:,None,None])
                reference=(1-along_[:,None,None])*swept[0]+along_[:,None,None]*swept[-1]
                swept_error=np.max(np.linalg.norm(swept-reference,axis=2),axis=1)/PROFILE['maximumChordErrorMetres']
                error=np.maximum(error,swept_error)
            metric=np.maximum(np.maximum(error,normal_error),uv_error);metric[[0,-1]]=0;index=int(np.argmax(metric))
            if metric[index]<=1:return [nodes[0],nodes[-1]]
            return reduce_path(nodes[:index+1])[:-1]+reduce_path(nodes[index:])
        for seed,start_tri in enumerate(selected):
            if maximum_vertices-len(added)<4:
                failed['bounded-physical-and-vertex-budget']+=count-seed;break
            bary=-np.log(np.maximum(random.random(3),1e-12));bary/=bary.sum()
            is_fringe=soft_coverage and seed<fringe_count
            if is_fringe:
                edge=boundary[int(random.choice(len(boundary),p=np.asarray([e[3]for e in boundary])/sum(e[3]for e in boundary)))];start_tri,a,b=edge[:3]
                fraction=float(random.uniform(.05,.95));bary=np.full(3,.01);bary[a]=.99*(1-fraction);bary[b]=.99*fraction
            center=bary@positions[start_tri];normal=unit(bary@normals[start_tri]);u=bary@uv0[start_tri]
            if not eligible(u):failed['native-alpha-or-metal']+=1;continue
            if exterior_source is not None:
                shell_bvh,shell_directions,shell_clear=exterior_source[:3]
                directions=shell_directions[shell_clear[start_tri]]
                if not len(directions)or shell_bvh.blocked(np.repeat(center[None,:],len(directions),axis=0),directions).all():failed['seed-buried-by-native-source']+=1;continue
            axis=flow(center,normal,index=int(start_tri))
            if axis is None:failed['source-flow-degenerate']+=1;continue
            length=float(random.uniform(length_limits[0],length_limits[1]))
            width=float(random.uniform(PROFILE['widthMetres'][0],PROFILE['widthMetres'][1]))
            lift=float(random.uniform(PROFILE['liftMetres'][0],PROFILE['liftMetres'][1]))
            if is_fringe:
                length=float(random.uniform(.007,.017));width=float(random.uniform(.00045,.0009));lift=float(random.uniform(.003,.007))
            center_node=dict(point=center,normal=normal,uv=u,triangle=int(start_tri),bary=bary,chart=chart_ids[int(start_tri)])
            paths=[]
            for sign in (-1,1):
                path=[];current=center_node;direction=axis*sign
                steps=max(2,int(math.ceil(length*.5/PROFILE['traceStepMetres'])))
                for _ in range(steps):
                    direction=flow(current['point'],current['normal'],direction,current['triangle'])
                    if direction is None:trace_stops['source-flow-undefined']+=1;break
                    candidate=project(current['point']+direction*(length*.5/steps),current['normal'],current['triangle'])
                    if candidate is None:break
                    displacement=candidate['point']-current['point']
                    if np.linalg.norm(displacement)<.0003 or np.dot(unit(displacement),direction)<.35:trace_stops['source-projection-no-forward-progress']+=1;break
                    if any(np.linalg.norm(candidate['point']-n['point'])<.001 for n in path[:-2]):trace_stops['source-flow-cycle']+=1;break
                    routed=source_edge_route(current,candidate)
                    if routed is None:break
                    if domain is not None:
                        previous_node=current;safe=True
                        for next_node in routed:
                            if not domain.hair([previous_node['uv'],next_node['uv']]):safe=False;break
                            previous_node=next_node
                        if not safe:trace_stops['native-hair-domain-segment-footprint']+=1;break
                    path.extend(routed);current=candidate
                paths.append(path)
            nodes=list(reversed(paths[0]))+[center_node]+paths[1]
            # The unreduced trace owns the envelope parameter while deciding
            # which source samples must survive. This is diagnostic metadata
            # only; unchanged reduction paths retain their original output v.
            if soft_coverage and len(nodes)>1:
                original_lengths=np.linalg.norm(np.diff(np.asarray([n['point']for n in nodes]),axis=0),axis=1)
                original_along=np.r_[0,np.cumsum(original_lengths)]/max(float(original_lengths.sum()),1e-20)
                for node,value in zip(nodes,original_along):node['_sweptProfileV']=float(value)
            pieces=[];piece=[]
            for node in nodes:
                if piece:
                    distance=np.linalg.norm(node['point']-piece[-1]['point'])
                    same_uv=np.max(np.abs(node['uv']-piece[-1]['uv']))<=PROFILE['nativeUvSeamTolerance']
                    if distance<=PROFILE['geometricSeamToleranceMetres']*1.01 and not same_uv:
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
                    point=center_point+across*(side*width*.5*taper)
                    parents=native_tri[node['triangle']]
                    anchor=int(parents[np.argmin(np.sum((np_frames[0,parents]-point)**2,axis=1))])
                    basis=np.column_stack((nt_frames[0,anchor],nb_frames[0,anchor],nn_frames[0,anchor]))
                    offset=basis.T@(point-np_frames[0,anchor])
                    if np.linalg.norm(offset)>.04:projected=[];break
                    # The new sheet is curved along the source envelope, and its
                    # normal follows that curvature instead of the broad polygon.
                    transverse_angle=math.radians(PROFILE['transverseNormalDegrees'])
                    strand_normal=unit(n*math.cos(transverse_angle)+across*(side*math.sin(transverse_angle)))
                    ln=basis.T@strand_normal;lt=basis.T@across
                    projected.append(dict(surface=dict(triangle=parents.tolist(),barycentric=node['bary'].tolist()),anchor=anchor,native=False,role='airy-edge-fringe'if is_fringe else'source-continuous-flow-ribbon',slot=slot,
                        uv0=node['uv'].tolist(),offset=offset.tolist(),normal=ln.tolist(),tangent=lt.tolist(),
                        tangentW=-float(np.sign(np.linalg.det(linear))),groom=[(side+1)*.5,v,(33 if is_fringe else 17)+(seed%16),hand*(1+rank)]))
                if not projected:break
            if len(projected)!=2*len(nodes):failed['source-span']+=1;continue
            candidate_anchors=np.array([a['anchor']for a in projected])
            candidate_offsets=np.array([a['offset']for a in projected])
            candidate_normals=np.array([a['normal']for a in projected])
            frame_points=np_frames[:,candidate_anchors]+nt_frames[:,candidate_anchors]*candidate_offsets[None,:,0,None]+nb_frames[:,candidate_anchors]*candidate_offsets[None,:,1,None]+nn_frames[:,candidate_anchors]*candidate_offsets[None,:,2,None]
            frame_normals=unit(nt_frames[:,candidate_anchors]*candidate_normals[None,:,0,None]+nb_frames[:,candidate_anchors]*candidate_normals[None,:,1,None]+nn_frames[:,candidate_anchors]*candidate_normals[None,:,2,None])
            local_tri=np.array([[a*2,a*2+1,b*2]for a,b in connections]+[[a*2+1,b*2+1,b*2]for a,b in connections])
            fp=frame_points[:,local_tri]
            face=np.cross(fp[:,:,1]-fp[:,:,0],fp[:,:,2]-fp[:,:,0])
            expected_normals=frame_normals[:,local_tri].mean(axis=2)
            if np.any(np.sum(face*expected_normals,axis=2)<=1e-14):failed['full-physical-frame-winding']+=1;continue
            added.extend(projected)
            start=original['vertexCount']+first
            for a,b in connections:
                a=start+a*2;b=start+b*2;new_indices[slot].extend((a,a+1,b,a+1,b+1,b))
            accepted+=1;accepted_lengths.append(curve_length);accepted_nodes.append(len(nodes))
        reports.append(dict(slot=slot,nativeHairDomain=domain is not None,sourceDomainScope='Native R<=.5 hair; conservative mip0 GPU UNorm8 interval and full bilinear footprint, coarse mixed/uncertain faces retained.'if domain is not None else'Legacy .01 point sampling retained for historical reproducibility.',sourceAreaSquareMetres=float(area.sum()),removedBroadExteriorTriangles=removed,
            requestedRibbons=requested,budgetedRibbons=count,acceptedCurvedRibbons=accepted,rejected=dict(failed),
            oldBroadExteriorTriangles=len(outer),sourceTopologyPreservedAsCore=True,
            coverageEstimateIsNotGpuProof=True,sourceExtentMetres=extent,targetCurveLengthMetres=length_limits,
            meanAcceptedCurveLengthMetres=float(np.mean(accepted_lengths))if accepted_lengths else 0,
            maximumAcceptedCurveLengthMetres=max(accepted_lengths,default=0),
            meanAcceptedNodes=float(np.mean(accepted_nodes))if accepted_nodes else 0,
            adaptiveChordErrorMetres=PROFILE['maximumChordErrorMetres'],traceStops=dict(trace_stops),
            allPhysicalFrameGeometricSeamConnections=seam_connections))
        if soft_coverage:reports[-1].update(seedPlacement='native area stratified without replacement; physical boundary tranche',physicalBoundaryEdges=len(boundary),boundarySeedCurves=fringe_count,sourceFlow='local native-neighborhood sign-independent line tensor; curve geometry owns final direction')
        if exterior_coverage:reports[-1].update(seedPlacement='BVH exterior-stratum area weighted; actual barycentric seed must retain at least one unoccluded exterior ray; physical boundaries restricted to the same stratum',sourceShell=shell_report)
    removed_broad=sum(r.get('removedBroadExteriorTriangles',0)for r in reports)
    assert added or removed_broad==0,'No valid fine curve may replace removed coarse coverage.'
    anchors=np.asarray([a['anchor']for a in added],dtype=int);offset=np.asarray([a['offset']for a in added]).reshape(-1,3);ln=np.asarray([a['normal']for a in added]).reshape(-1,3);lt=np.asarray([a['tangent']for a in added]).reshape(-1,3)
    pp=np_frames[:,anchors]+nt_frames[:,anchors]*offset[None,:,0,None]+nb_frames[:,anchors]*offset[None,:,1,None]+nn_frames[:,anchors]*offset[None,:,2,None]
    nn=unit(nt_frames[:,anchors]*ln[None,:,0,None]+nb_frames[:,anchors]*ln[None,:,1,None]+nn_frames[:,anchors]*ln[None,:,2,None])
    tt=unit(nt_frames[:,anchors]*lt[None,:,0,None]+nb_frames[:,anchors]*lt[None,:,1,None]+nn_frames[:,anchors]*lt[None,:,2,None])
    pp=(pp-m[:3,3])@inverse.T;nn=unit(nn@linear);tt=unit(tt@inverse.T);tt=unit(tt-nn*np.sum(tt*nn,axis=2)[:,:,None])
    for key,value in (('vertices',pp[0]),('normals',nn[0]),('tangents',np.c_[tt[0],np.full(len(added),-1)])):packet[key]+=value.ravel().tolist()
    for key in ('weights','boneIndices','colors'):
        if packet[key]:packet[key]+=np.asarray(native[key]).reshape(-1,4)[anchors].ravel().tolist()
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
    path=output/(native['sourceFingerprint']+'.json')
    path.write_text(json.dumps(packet,separators=(',',':')),encoding='utf-8')
    binding_out=output/entry['manifestEntry']['bindingFile']
    surface_provenance=prepare_surface_binding(binding,native)
    encode_binding(binding,binding_out)
    return dict(entry,generatedPacket=str(path),generatedSha256=sha(path),bindingPath=str(binding_out),bindingSha256=sha(binding_out),
        authoredFingerprint=packet['sourceFingerprint'],originalVertices=original['vertexCount'],addedVertices=final_added,compactedVertices=packet['vertexCount'],
        removedUnusedVertices=original['vertexCount']+final_added-packet['vertexCount'],physicalFrames=physical,
        addedPhysicalDeltaBytes=final_added*physical*36,addedVertexBytes=final_added*vertex_stride,serializedWinding=serialized_winding,zeroFineReason=zero_fine_reason,
        addedVertexLimit=maximum_vertices,addedVertexAndPhysicalByteBudget=physical_budget,
        nativeVertexCount=native['vertexCount'],intrinsicNativePhysicalFrameBytes=native['vertexCount']*physical*36,
        physicalFrameCostPerNewVertexBytes=physical*36,packedVertexStrideBytes=vertex_stride,
        finalTriangles=sum(len(s)//3 for s in packet['subMeshes']),lod=lod,slots=reports,
        nativeRootCoreAndOrnamentsRetained=True,broadNativeOuterSheetReplaced=bool(removed_broad),nativeFrameNamesWeightsOrderPreserved=True,
        coreCoordinateStatistics=core_coordinate_statistics,nativeCoreRepair=native_core_repair,nativeUvSeamsSplitAtSharedSourceEdges=True,
        sourceUvChordErrorTexels=PROFILE['maximumSourceUvChordErrorTexels'],
        surfaceProvenance=surface_provenance,bindingRuntimeValidationPending=True,renderedArtAccepted=False)



def main():
    global HERE
    parser=argparse.ArgumentParser();parser.add_argument('--workspace',type=Path,required=True);parser.add_argument('--asset',action='append');parser.add_argument('--lod',type=int);parser.add_argument('--input',required=True);parser.add_argument('--output',required=True)
    parser.add_argument('--core-repair-from');parser.add_argument('--restore-core-geometry',action='store_true')
    parser.add_argument('--core-multiplicity-from');parser.add_argument('--multiplicity-proof')
    parser.add_argument('--soft-coverage-pilot',action='store_true')
    parser.add_argument('--visible-shell-refinement',action='store_true')
    parser.add_argument('--native-hair-domain',action='store_true')
    parser.add_argument('--domain-contract')
    parser.add_argument('--curved-core',action='store_true')
    args=parser.parse_args();HERE=args.workspace.resolve();assert any(HERE.is_relative_to(ROOT/p)for p in('tools/game-test/artifacts','tools/asset-authoring/workspace')),'Use an ignored authoring workspace.';output=Path(args.output).resolve()
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
        baseline=inputs['baselineProfile'];assert baseline.get('nativeHairDomain')
        contract_path=Path(baseline['domainContract']['path']);assert sha(contract_path)==baseline['domainContract']['sha256']
        contract=read(contract_path);assert contract['status']=='PASS_NATIVE_MASK_GPU_AND_SOURCE_DOMAIN_POLICY'
        for authority in contract['authorities']:assert sha(authority['path'])==authority['sha256']
        PROFILE.update(copy.deepcopy(baseline))
    selected=[r for r in inputs['rows']if (not args.asset or r['asset']in args.asset)and(args.lod is None or re.search(r'_LOD'+str(args.lod)+r'(?:_|\b)',r['nativeRenderer'],re.I))]
    assert selected
    rows=[]
    for entry in selected:
        row=curved_core_row(entry,output)if args.curved_core else generate(entry,output);rows.append(row)
        print(entry['asset'],entry['nativeRenderer'],row['originalVertices'],row['compactedVertices'],row['addedPhysicalDeltaBytes'],flush=True)
    result=dict(status='PRIVATE_SOURCE_FLOW_COVERAGE_NUMERIC_CANDIDATE_NOT_RENDERED',profile=PROFILE,rows=rows,
        generatorPath=str(Path(__file__).resolve()),generatorSha256=sha(Path(__file__)),
        sourceInput=str(input_path),sourceInputSha256=sha(input_path),
        removedOldBroadOuterGeometry=any(row['broadNativeOuterSheetReplaced']for row in rows),allNativeSourceFramesKept=True,productChanged=False,artAccepted=False)
    if args.curved_core:
        result['geometryMode']='curved-core-native-domain-v1'
        result['allNativeRenderFacesKept']=False
        result['status']='PENDING_CURVED_CORE_NUMERIC_PROOFS'
        (output/'Generation.json').write_text(json.dumps(result,indent=2),encoding='utf-8')
        try:
            result['numericProofs']=[prove_row(row, PROFILE)for row in rows]
        except Exception as error:
            result.update(status='FAILED_CURVED_CORE_NUMERIC_PROOFS',numericError=str(error))
            (output/'Generation.json').write_text(json.dumps(result,indent=2),encoding='utf-8')
            raise
        result['status']='PASS_CURVED_CORE_NUMERIC_PROOFS_RENDER_PENDING'
    (output/'Generation.json').write_text(json.dumps(result,indent=2),encoding='utf-8')

if __name__=='__main__':main()
