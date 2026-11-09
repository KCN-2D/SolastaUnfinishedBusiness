"""Skin color and normal continuity on source-owned physical cuts and UV charts."""
import collections
import functools
import hashlib
import heapq
import json
import struct
import time
from typing import Any, TypedDict, cast
import numpy as np
from pathlib import Path
import sys
_ROOT=next(path for path in Path(__file__).resolve().parents if(path/'SolastaUnfinishedBusiness.sln').is_file())
sys.path.insert(0,str(_ROOT/'tools/asset-authoring/UnityAssetBuild'))
from mesh_recipe_codec import skin_palette_frame

# These three source laws intentionally use different dtypes/normalization bounds.
arr=lambda p,k,w:np.asarray(p[k],np.float32).reshape(-1,w)
array=lambda p,k,n:np.asarray(p[k],float).reshape(-1,n)
lookup=np.arange(256,dtype=float)/255
lookup=np.where(lookup<=.04045,lookup/12.92,((lookup+.055)/1.055)**2.4)


def normalize(v):
 return v/np.maximum(np.linalg.norm(v,axis=-1,keepdims=True),1e-30)

def bilinear_gpu_rows(pixels,uv):
 """GPU V0 row order, Repeat; source values are measured hardware codebook values."""
 h,w=pixels.shape[:2];xy=np.asarray(uv,np.float32)*np.asarray([w,h],np.float32)-np.float32(.5);lo=np.floor(xy).astype(int);f=xy-lo
 return(pixels[lo[:,1]%h,lo[:,0]%w]*(1-f[:,0,None])*(1-f[:,1,None])+pixels[lo[:,1]%h,(lo[:,0]+1)%w]*f[:,0,None]*(1-f[:,1,None])+pixels[(lo[:,1]+1)%h,lo[:,0]%w]*(1-f[:,0,None])*f[:,1,None]+pixels[(lo[:,1]+1)%h,(lo[:,0]+1)%w]*f[:,0,None]*f[:,1,None])


def fit_bilinear_cut(pixels,uv,target,editable,regularization=1e-4,maximum_iterations=96):
 """Fit the actual Repeat/bilinear cut footprint with all other texels locked."""
 pixels=np.asarray(pixels,float);uv=np.asarray(uv,np.float32);target=np.asarray(target,float);editable=np.asarray(editable,bool)
 if pixels.ndim!=3 or editable.shape!=pixels.shape[:2]or uv.ndim!=2 or uv.shape[1]!=2 or target.shape!=(len(uv),pixels.shape[2]):
  raise ValueError('A bilinear cut fit has inconsistent image, query or ownership dimensions.')
 if not all(np.isfinite(value).all()for value in (pixels,uv,target))or not 0<regularization<=1 or not 1<=maximum_iterations<=256:
  raise ValueError('A bilinear cut fit needs finite inputs and bounded positive solver settings.')
 h,w,channels=pixels.shape;xy=uv*np.asarray([w,h],np.float32)-np.float32(.5);lo=np.floor(xy).astype(int);f=xy-lo
 ids=np.stack(((lo[:,1]%h)*w+lo[:,0]%w,(lo[:,1]%h)*w+(lo[:,0]+1)%w,
               ((lo[:,1]+1)%h)*w+lo[:,0]%w,((lo[:,1]+1)%h)*w+(lo[:,0]+1)%w),axis=1)
 weights=np.stack(((1-f[:,0])*(1-f[:,1]),f[:,0]*(1-f[:,1]),(1-f[:,0])*f[:,1],f[:,0]*f[:,1]),axis=1)
 allowed=editable.ravel()[ids]&(weights>0);owned=np.unique(ids[allowed]);result=pixels.copy();before=bilinear_gpu_rows(pixels,uv)-target
 if not len(owned):return result,dict(queries=len(uv),changedTexels=0,unownedTexelsExact=True,iterations=0,
     maximumErrorBefore=float(np.abs(before).max(initial=0)),maximumErrorAfter=float(np.abs(before).max(initial=0)),rootMeanSquareErrorAfter=float(np.sqrt(np.mean(before**2)))if before.size else 0.)
 slots=np.searchsorted(owned,ids).clip(0,len(owned)-1);weights=weights*allowed
 def forward(value):return np.sum(value[slots]*weights[:,:,None],axis=1)
 def transpose(value):
  out=np.zeros((len(owned),channels));np.add.at(out,slots.ravel(),(value[:,None,:]*weights[:,:,None]).reshape(-1,channels));return out
 diagonal=np.full(len(owned),regularization);np.add.at(diagonal,slots.ravel(),(weights**2).ravel())
 rhs=transpose(-before);delta=np.zeros_like(rhs);residual=rhs.copy();z=residual/diagonal[:,None];direction=z.copy();rz=np.sum(residual*z,axis=0);iterations=0
 for step in range(maximum_iterations):
  if np.max(np.abs(residual),initial=0)<1e-10:break
  action=transpose(forward(direction))+regularization*direction;denominator=np.sum(direction*action,axis=0)
  alpha=np.divide(rz,denominator,out=np.zeros_like(rz),where=denominator>1e-30)
  delta+=direction*alpha;residual-=action*alpha;z=residual/diagonal[:,None];next_rz=np.sum(residual*z,axis=0)
  beta=np.divide(next_rz,rz,out=np.zeros_like(rz),where=rz>1e-30);direction=z+direction*beta;rz=next_rz;iterations=step+1
 initial=pixels.reshape(-1,channels)[owned]
 candidate=np.clip(initial+delta,0,1);projected_iterations=0
 if np.mean((before+forward(candidate-initial))**2)>np.mean(before**2)+1e-12:
  # Clipping an unconstrained least-squares solution can increase error when
  # several queries share texels at the UNorm limits. Solve that bounded case
  # with a projected descent whose step is bounded by A' A's row-sum norm.
  delta=np.zeros_like(initial);mass=transpose(np.ones_like(before))[:,0]
  step_size=1/(mass.max(initial=0)+regularization)
  for step in range(maximum_iterations):
   gradient=transpose(before+forward(delta))+regularization*delta
   changed=np.clip(initial+delta-step_size*gradient,0,1)-initial
   projected_iterations=step+1
   if np.max(np.abs(changed-delta),initial=0)<1e-10:delta=changed;break
   delta=changed
  candidate=initial+delta
 result.reshape(-1,channels)[owned]=candidate
 after=bilinear_gpu_rows(result,uv)-target
 if not np.array_equal(result[~editable],pixels[~editable]):raise ValueError('A bilinear cut fit changed an unowned texel.')
 if np.mean(after**2)>np.mean(before**2)+1e-12:raise ValueError('A bounded bilinear cut fit worsened its source error.')
 return result,dict(queries=len(uv),changedTexels=int(np.any(result!=pixels,axis=2).sum()),unownedTexelsExact=True,iterations=iterations,projectedIterations=projected_iterations,
     maximumErrorBefore=float(np.abs(before).max(initial=0)),maximumErrorAfter=float(np.abs(after).max(initial=0)),rootMeanSquareErrorAfter=float(np.sqrt(np.mean(after**2))))

def boundary_frames(normals,tangents,edges,t):
 """Installed Lux VS: normalize per vertex, interpolate T/B/N, PS normalize final N.

 No fragment Gram-Schmidt and no triangle-constant UV derivative basis.
 Object-to-common-frame transport is already source-authoritative at the caller.
 """
 normals=np.asarray(normals);tangents=np.asarray(tangents);edges=np.asarray(edges,int);t=np.asarray(t)
 n=normalize(normals);v=normalize(tangents[:,:3]);hand=tangents[:,3]
 if np.any(np.abs(hand[edges])<1e-12):raise ValueError('The source vertex tangent has zero handedness.')
 frames=np.stack((v,np.cross(n,v)*hand[:,None],n),axis=-1)
 return frames[edges[:,0]]*(1-t[:,None,None])+frames[edges[:,1]]*t[:,None,None]

class BoundaryAtlas:
 def __init__(self,vertices,uv,normals,tangents,edges):
  self.vertices=np.asarray(vertices);self.uv=np.asarray(uv);self.normals=np.asarray(normals);self.tangents=np.asarray(tangents);self.edges=np.asarray(edges,int)
  if len(self.edges)<3:raise ValueError('A source boundary needs at least three actual native edges.')
  self.first=self.vertices[self.edges[:,0]];self.delta=self.vertices[self.edges[:,1]]-self.first;self.square=np.sum(self.delta*self.delta,axis=1)
  if np.any(self.square<=1e-24):raise ValueError('A source boundary has a zero-length physical edge.')
  # Order actual geometric edges while retaining every edge's original UV and
  # tangent alias. This is a checked physical loop, not a resampled color ring.
  nodes=[];node_edges={};edge_nodes=[]
  for index,edge in enumerate(self.edges):
   pair=[]
   for vertex in self.vertices[edge]:
    matches=[i for i,p in enumerate(nodes)if np.linalg.norm(vertex-p)<=1e-7]
    if len(matches)>1:raise ValueError('The physical cut has ambiguous coincident nodes.')
    node=matches[0]if matches else len(nodes)
    if not matches:nodes.append(vertex.copy())
    pair.append(node);node_edges.setdefault(node,[]).append(index)
   edge_nodes.append(pair)
  if any(len(v)!=2 for v in node_edges.values()):raise ValueError('The actual physical cut is not a degree-two closed boundary.')
  order=[];reverse=[];node=edge_nodes[0][0];first_node=node;visited=set()
  while len(visited)<len(self.edges):
   choices=[e for e in node_edges[node]if e not in visited]
   if not choices:raise ValueError('The physical boundary closes before all native edges are visited.')
   edge=choices[0];back=edge_nodes[edge][1]==node;order.append(edge);reverse.append(back);visited.add(edge);node=edge_nodes[edge][0 if back else 1]
  if node!=first_node:raise ValueError('The ordered native boundary is open.')
  self.order=np.asarray(order,int);self.reverse=np.asarray(reverse,bool);self.length=np.sqrt(self.square);self.cumulative=np.r_[0,np.cumsum(self.length[self.order])];self.perimeter=float(self.cumulative[-1]);self.order_slot=np.empty(len(self.edges),int);self.order_slot[self.order]=np.arange(len(self.edges))
 def nearest(self,points):
  indices=[];amounts=[];distances=[]
  for start in range(0,len(points),256):
   p=np.asarray(points[start:start+256]);t=np.clip(np.sum((p[:,None]-self.first)*self.delta,axis=-1)/self.square,0,1);d=np.sum((self.first+t[:,:,None]*self.delta-p[:,None])**2,axis=-1);i=d.argmin(axis=1);indices.extend(i);amounts.extend(t[np.arange(len(p)),i]);distances.extend(np.sqrt(d[np.arange(len(p)),i]))
  return np.asarray(indices,int),np.asarray(amounts),np.asarray(distances)
 def evaluate(self,indices,amounts):
  edges=self.edges[indices];p=self.vertices[edges[:,0]]*(1-amounts[:,None])+self.vertices[edges[:,1]]*amounts[:,None];uv=self.uv[edges[:,0]]*(1-amounts[:,None])+self.uv[edges[:,1]]*amounts[:,None];frames=boundary_frames(self.normals,self.tangents,edges,amounts)
  return p,uv,frames
 def arc(self,indices,amounts):
  slots=self.order_slot[indices];t=np.where(self.reverse[slots],1-amounts,amounts)
  return self.cumulative[slots]+t*self.length[indices]
 def at_arc(self,distance):
  d=np.mod(distance,self.perimeter);slots=np.minimum(np.searchsorted(self.cumulative,d,side='right')-1,len(self.order)-1);indices=self.order[slots];t=(d-self.cumulative[slots])/self.length[indices];t=np.where(self.reverse[slots],1-t,t)
  return indices,t

def physical_groups(p):
    positions = array(p, 'vertices', 3)
    weights = array(p, 'weights', 4)
    indices = array(p, 'boneIndices', 4).astype(int)
    frames = [array(frame, 'vertices', 3) for shape in p['shapes'] for frame in shape['frames']]
    lookup, groups = {}, []
    pack = lambda value: struct.pack('<f', 0 if value == 0 else value)
    for index, position in enumerate(positions):
        skin = collections.defaultdict(float)
        for bone, weight in zip(indices[index], weights[index]):
            if weight != 0:
                skin[int(bone)] += float(weight)
        key = b''.join(pack(v) for v in position)
        key += b''.join(struct.pack('<i', bone) + pack(weight) for bone, weight in sorted(skin.items()))
        key += b''.join(pack(v) for frame in frames for v in frame[index])
        groups.append(lookup.setdefault(key, index))
    return np.asarray(groups, int)

def native_alias_edges(head,bone_paths,protected_vertices=None):
    """Retain raw chart corners on two incident, physically equivalent faces."""
    packet=head['packet'];groups=physical_groups(packet)
    triangles=np.asarray(head['triangles'],int);uv=np.asarray(head['uv']);normals=np.asarray(head['normal'])
    weights=np.asarray(packet['weights'],float).reshape(-1,4);bones=np.asarray(packet['boneIndices'],int).reshape(-1,4)
    if protected_vertices is None:
        protected_bones=np.asarray([any(v=='Bip001 Head' for v in p.split('/')) for p in bone_paths],bool)
        protected=np.any((weights>0)&protected_bones[bones],axis=1)
    else:
        protected=np.asarray(protected_vertices,bool)
        if protected.shape!=(len(groups),):raise ValueError('The native source part protection differs from its vertex count.')
    parent=list(range(len(triangles)))
    def find(i):
        while parent[i]!=i:parent[i]=parent[parent[i]];i=parent[i]
        return i
    def union(a,b):
        a,b=find(a),find(b)
        if a!=b:parent[b]=a
    edges=collections.defaultdict(list)
    for face,tri in enumerate(triangles):
        for a,b in zip(tri,np.roll(tri,-1)):
            ga,gb=int(groups[a]),int(groups[b])
            if ga==gb:continue
            edges[tuple(sorted((ga,gb)))].append((face,int(a),int(b)))
    aliases=collections.defaultdict(list)
    for i,g in enumerate(groups):aliases[int(g)].append(i)
    seams=[];counts=collections.Counter()
    for physical,rows in edges.items():
        if len(rows)!=2:counts['nonTwoIncidentNativeEdges']+=1;continue
        fa,a,b=rows[0];fb,c,d=rows[1]
        if groups[a]!=groups[d]:c,d=d,c
        assert groups[a]==groups[d] and groups[b]==groups[c]
        aa,bb=np.asarray([a,b],dtype=np.intp),np.asarray([d,c],dtype=np.intp)
        same=np.array_equal(np.asarray(uv[aa],'<f4'),np.asarray(uv[bb],'<f4'))
        if same:union(fa,fb);counts['continuousUvEdges']+=1;continue
        if any(float(np.dot(uv[edge[1]]-uv[edge[0]],uv[edge[1]]-uv[edge[0]]))<=1e-20 for edge in (aa,bb)):
            counts['collapsedNativeUvSupportEdges']+=1;continue
        if protected[np.r_[aa,bb]].any():counts['nativeHeadOrDescendantProtectedEdges']+=1;continue
        if np.max(np.linalg.norm(normals[aa]-normals[bb],axis=1))>1e-6:
            counts['intentionalOrUnresolvedHardNormalEdges']+=1;continue
        frame_ok=True
        for shape in packet['shapes']:
            for frame in shape['frames']:
                delta=np.asarray(frame['normals'],float).reshape(-1,3)
                if np.max(np.linalg.norm(delta[aa]-delta[bb],axis=1))>1e-6:frame_ok=False
        if not frame_ok:counts['physicalNormalFrameDiscontinuityEdges']+=1;continue
        seams.append(dict(physical=physical,orderedPhysical=(int(groups[a]),int(groups[b])),faces=(fa,fb),a=aa,b=bb,nodeAliases=[aliases[int(groups[a])],aliases[int(groups[b])]]))
    charts=np.asarray([find(i) for i in range(len(triangles))],int)
    for row in seams:row['charts']=(int(charts[row['faces'][0]]),int(charts[row['faces'][1]]))
    counts['eligibleSmoothBodySupportAliasEdges']=len(seams)
    return seams,charts,dict(counts),protected

def split_alias_paths(rows,head=None):
    """Split a source network at actual junctions, retaining every raw edge."""
    adjacent=collections.defaultdict(list)
    for i,row in enumerate(rows):
        for node in row['physical']:adjacent[node].append(i)
    # Keep both RAW half-edge tracks. At a source corner fan with a merge,
    # split, hard-normal branch, or no equivalent continuation, stop this
    # analytic path instead of guessing a local representative side.
    stops=set()
    if head is not None:
        uv=np.asarray(head['uv']);normal=np.asarray(head['normal']);tangent=np.asarray(head['tangent'])
        frames=[np.asarray(f['normals'],float).reshape(-1,3) for s in head['packet']['shapes'] for f in s['frames']]
        for node,incident in adjacent.items():
            if len(incident)!=2:continue
            matrix=np.zeros((2,2),bool)
            for a,sa in enumerate(('a','b')):
                ra=rows[incident[0]];ca=0 if ra['orderedPhysical'][0]==node else 1;ia=int(ra[sa][ca])
                for b,sb in enumerate(('a','b')):
                    rb=rows[incident[1]];cb=0 if rb['orderedPhysical'][0]==node else 1;ib=int(rb[sb][cb]);delta=uv[ia]-uv[ib];delta-=np.rint(delta)
                    matrix[a,b]=np.abs(delta).max()<=2e-6 and np.linalg.norm(normal[ia]-normal[ib])<=1e-6 and np.linalg.norm(tangent[ia]-tangent[ib])<=2e-6 and all(np.linalg.norm(f[ia]-f[ib])<=1e-6 for f in frames)
            if not(np.all(matrix.sum(axis=0)==1) and np.all(matrix.sum(axis=1)==1)):stops.add(node)
    unused=set(range(len(rows)))
    def take(start,edge):
        result=[];node=start
        while edge in unused:
            unused.remove(edge);result.append(rows[edge]);a,b=rows[edge]['physical'];node=b if node==a else a
            if len(adjacent[node])!=2 or node in stops:break
            choices=[e for e in adjacent[node] if e in unused]
            if not choices:break
            edge=choices[0]
        return result
    for node,edges in adjacent.items():
        if len(edges)==2:continue
        for edge in edges:
            if edge in unused:yield take(node,edge)
    while unused:
        candidates=[(node,edge) for node in sorted(stops) for edge in adjacent[node] if edge in unused]
        if candidates:node,edge=candidates[0]
        else:
            ends=[(node,edge) for node,edges in adjacent.items() if len(edges)!=2 for edge in edges if edge in unused]
            if ends:node,edge=min(ends)
            else:edge=min(unused);node=rows[edge]['physical'][0]
        yield take(node,edge)

class AliasCurve:
    def __init__(self,head,rows,own_chart,opposite=False):
        self.source_packet=head['packet'];self.vertices=np.asarray(head['vertices']);self.uv=np.asarray(head['uv']);self.normals=np.asarray(head['normal']);self.tangents=np.asarray(head['tangent'])
        edges=[];peers=[]
        for row in rows:
            side=0 if row['charts'][0]==own_chart else 1
            edges.append(row['a'] if side==0 else row['b']);peers.append(row['b'] if side==0 else row['a'])
        self.edges=np.asarray(edges,int);self.peers=np.asarray(peers,int);self.chart=own_chart;self.rows=rows
        # Raw peer corners were aligned using the exact physical groups above;
        # do not collapse distinct skin/frame ownership by position alone.
        edge_nodes=[row['orderedPhysical'] for row in rows]
        adjacency=collections.defaultdict(list)
        for i,pair in enumerate(edge_nodes):
            for v in pair:adjacency[v].append(i)
        if any(len(v)>2 for v in adjacency.values()):raise ValueError('A multi-chart source junction needs an explicit consensus constraint.')
        ends=[v for v,e in adjacency.items() if len(e)==1]
        if len(ends) not in (0,2):raise ValueError('The source alias path is not one closed loop or an open path.')
        self.closed=not ends;node=ends[0] if ends else edge_nodes[0][0];start=node;order=[];reverse=[];used=set()
        while len(used)<len(rows):
            choices=[e for e in adjacency[node] if e not in used]
            if not choices:raise ValueError('Source alias component closes before consuming all edges.')
            at=choices[0];back=edge_nodes[at][1]==node;order.append(at);reverse.append(back);used.add(at);node=edge_nodes[at][0 if back else 1]
        if self.closed and node!=start:raise ValueError('A closed source alias path ends at another node.')
        self.order=np.asarray(order,int);self.reverse=np.asarray(reverse,bool)
        if rows[0]['charts'][0]==rows[0]['charts'][1]:
            # A UV cut may live within one globally connected chart. Preserve
            # its LOCAL raw corner side instead of using the global chart ID.
            selected={};previous=None
            for slot,at in enumerate(order):
                entry=1 if reverse[slot] else 0;exit_corner=0 if reverse[slot] else 1
                if previous is None:side=0
                else:
                    choices=[]
                    for candidate in (0,1):
                        vertex=int(rows[at]['a' if candidate==0 else 'b'][entry]);duv=self.uv[vertex]-self.uv[previous];duv-=np.rint(duv)
                        if np.max(np.abs(duv))<=2e-6 and np.linalg.norm(self.normals[vertex]-self.normals[previous])<=1e-6 and np.linalg.norm(self.tangents[vertex]-self.tangents[previous])<=2e-6:choices.append((vertex!=previous,vertex,candidate))
                    if not choices:raise ValueError('A source path crosses a distinct local UV/TBN corner fan; split this native junction before filtering.')
                    side=min(choices)[2]
                selected[at]=side;previous=int(rows[at]['a' if side==0 else 'b'][exit_corner])
            self.edges=np.asarray([r['a' if (selected[i]^(1 if opposite else 0))==0 else 'b'] for i,r in enumerate(rows)],int)
            self.peers=np.asarray([r['b' if (selected[i]^(1 if opposite else 0))==0 else 'a'] for i,r in enumerate(rows)],int)
        self.first=self.vertices[self.edges[:,0]];self.delta=self.vertices[self.edges[:,1]]-self.first;self.square=np.sum(self.delta*self.delta,axis=1)
        if np.any(self.square<=1e-24):raise ValueError('Zero-length source alias edge.')
        self.length=np.sqrt(self.square);self.cumulative=np.r_[0,np.cumsum(self.length[self.order])];self.perimeter=float(self.cumulative[-1]);self.order_slot=np.empty(len(rows),int);self.order_slot[self.order]=np.arange(len(rows))
    def nearest(self,points):
        result=[]
        for start in range(0,len(points),256):
            p=np.asarray(points[start:start+256]);t=np.clip(np.sum((p[:,None]-self.first)*self.delta,axis=-1)/self.square,0,1);distance=np.sum((self.first+t[:,:,None]*self.delta-p[:,None])**2,axis=-1);i=distance.argmin(axis=1);row=np.arange(len(i));result.append((i,t[row,i],np.sqrt(distance[row,i])))
        return tuple(np.concatenate([r[c] for r in result]) for c in range(3))
    def arc(self,i,t):
        slots=self.order_slot[i];return self.cumulative[slots]+np.where(self.reverse[slots],1-t,t)*self.length[i]
    def at_arc(self,distance):
        d=np.mod(distance,self.perimeter) if self.closed else np.clip(distance,0,self.perimeter)
        slots=np.clip(np.searchsorted(self.cumulative,d,side='right')-1,0,len(self.order)-1);i=self.order[slots];t=(d-self.cumulative[slots])/self.length[i];return i,np.where(self.reverse[slots],1-t,t)
    def evaluate(self,i,t,peer=False):
        edges=(self.peers if peer else self.edges)[i];uv=self.uv[edges[:,0]]*(1-t[:,None])+self.uv[edges[:,1]]*t[:,None]
        return uv,boundary_frames(self.normals,self.tangents,edges,t)

def nearest_raw_chart(curve,points,query_uv):
    """Break physical-knot ties with the query's actual Repeat UV branch."""
    indices=[];amounts=[];errors=[]
    for at in range(0,len(points),256):
        p=np.asarray(points[at:at+256]);own=np.asarray(query_uv[at:at+256]);t=np.clip(np.sum((p[:,None]-curve.first)*curve.delta,axis=-1)/curve.square,0,1)
        distance=np.linalg.norm(curve.first+t[:,:,None]*curve.delta-p[:,None],axis=-1);minimum=distance.min(axis=1)
        # Only physical ties can win. Evaluate their UV branches without
        # allocating a query-by-all-edges UV field for every filtered sample.
        eligible=distance<=np.maximum(minimum[:,None]+1e-9,1e-7)
        rows,columns=np.nonzero(eligible);e=curve.edges[columns];amount=t[rows,columns,None]
        candidate_uv=curve.uv[e[:,0]]*(1-amount)+curve.uv[e[:,1]]*amount
        delta=candidate_uv-own[rows];delta-=np.rint(delta)
        score=np.full(distance.shape,np.inf);score[rows,columns]=np.sum(delta*delta,axis=-1)
        i=score.argmin(axis=1);r=np.arange(len(i))
        indices.extend(i);amounts.extend(t[r,i]);errors.extend(distance[r,i])
    return np.asarray(indices,int),np.asarray(amounts),np.asarray(errors)

def consensus_main(curve,main_pixels):
    """Exact Main terms of AliasCurve.consensus; native hard-N keys retained."""
    nodes={};main=[]
    normal_frames=[np.asarray(f['normals'],float).reshape(-1,3)for s in curve.source_packet['shapes']for f in s['frames']]
    for row in curve.rows:
        rm=[]
        for node,aliases in zip(row['orderedPhysical'],row['nodeAliases']):
            corner=0 if row['orderedPhysical'][0]==node else 1;own=int(row['a'][corner])
            key=(node,tuple(np.asarray(curve.normals[own],'<f4')),tuple(tuple(np.asarray(f[own],'<f4'))for f in normal_frames))
            if key not in nodes:
                aliases=[i for i in aliases if np.linalg.norm(curve.normals[i]-curve.normals[own])<=1e-6 and all(np.linalg.norm(f[i]-f[own])<=1e-6 for f in normal_frames)]
                unique={tuple(np.asarray(np.r_[curve.uv[i],curve.normals[i],curve.tangents[i]],'<f4')):i for i in aliases}
                ids=np.asarray(list(unique.values()),int);nodes[key]=bilinear_gpu_rows(main_pixels,curve.uv[ids])[:,:3].mean(axis=0)
            rm.append(nodes[key])
        main.append(rm)
    curve.node_main=np.asarray(main);ii=np.arange(len(curve.edges));endpoint=[]
    for amount in (0.,1.):
        t=np.full(len(ii),amount);u0,_=curve.evaluate(ii,t);u1,_=curve.evaluate(ii,t,True)
        endpoint.append((bilinear_gpu_rows(main_pixels,u0)[:,:3]+bilinear_gpu_rows(main_pixels,u1)[:,:3])*.5)
    curve.node_delta_main=curve.node_main-np.stack(endpoint,axis=1)
    return curve

def continuous_alias_main(own_uv,positions,weight,curve,main_pixels,*,preserve_local_contrast=False):
    """Unpacked RGB delta, accumulated by the existing common denominator."""
    i,t,distance=curve.nearest(positions);arc=curve.arc(i,t)
    def difference(ii,tt):
        auv,_=curve.evaluate(ii,tt);buv,_=curve.evaluate(ii,tt,True)
        a=bilinear_gpu_rows(main_pixels,auv);b=bilinear_gpu_rows(main_pixels,buv)
        common=(a[:,:3]+b[:,:3])*.5;s=(tt*tt*(3-2*tt))[:,None]
        common+=curve.node_delta_main[ii,0]*(1-s)+curve.node_delta_main[ii,1]*s
        delta=common-a[:,:3]
        return delta/np.maximum(a[:,:3],1/3294.6)if preserve_local_contrast else delta
    m=difference(i,t);filtered=np.zeros_like(m);sigma=np.minimum(distance*.75,curve.perimeter/8)
    nodes=np.linspace(-2,2,9);kernel=np.exp(-.5*nodes*nodes);kernel/=kernel.sum()
    for offset,k in zip(nodes,kernel):
        ii,tt=curve.at_arc(arc+offset*sigma);dm=m if offset==0 else difference(ii,tt);filtered+=k*dm
    exact=sigma<=1e-12;filtered[exact]=m[exact]
    return filtered*np.asarray(weight)[:,None],dict(physicalDistance=distance,mainAlphaDeltaZero=True,normalImageConsumed=False)

def continuous_cut_main(own_uv,positions,weight,head_curve,body_curve,head_main,body_main):
    """Exact four-component C1 transfer, once, before alias RGB and tone RGB."""
    bi,bt,distance=body_curve.nearest(positions);arc=body_curve.arc(bi,bt)
    def difference(ii,tt):
        bp,buv,_=body_curve.evaluate(ii,tt);hi,ht,error=nearest_raw_chart(head_curve,bp,own_uv);_,huv,_=head_curve.evaluate(hi,ht)
        if np.any(error>1e-5):raise ValueError('The source-owned Head/Body cut exceeds its physical weld: '+str(float(error.max())))
        return bilinear_gpu_rows(body_main,buv)-bilinear_gpu_rows(head_main,huv),error
    delta,pair_error=difference(bi,bt);filtered=np.zeros_like(delta);sigma=np.minimum(distance*.75,body_curve.perimeter/8)
    nodes=np.linspace(-2,2,9);kernel=np.exp(-.5*nodes*nodes);kernel/=kernel.sum()
    for offset,k in zip(nodes,kernel):
        if offset==0:m=delta
        else:
            ii,tt=body_curve.at_arc(arc+offset*sigma);m,error=difference(ii,tt);pair_error=np.maximum(pair_error,error)
        filtered+=k*m
    exact=sigma<=1e-12;filtered[exact]=delta[exact]
    main=bilinear_gpu_rows(head_main,own_uv).copy();main[:,:]=np.clip(main[:,:]+filtered*np.asarray(weight)[:,None],0,1)
    return main,dict(maximumPhysicalPairError=float(pair_error.max()),mainRgbaC1Once=True,normalImageConsumed=False)


def unpack_skin_normal(packed,scale):
    xy=(np.column_stack((packed[:,0]*packed[:,3],packed[:,1]))*2-1)*scale
    return np.column_stack((xy,np.sqrt(np.maximum(1-np.sum(xy*xy,axis=1),0))))


def fit_bilinear_normal_cut(pixels,uv,target_xy,ownership):
    """Fit Lux's sampled R*A and G, keeping every unowned RGBA texel intact."""
    pixels=np.asarray(pixels,float);ownership=np.asarray(ownership,float);target_xy=np.asarray(target_xy,float)
    if pixels.ndim!=3 or pixels.shape[2]!=4 or ownership.shape!=pixels.shape[:2]or target_xy.shape!=(len(uv),2):
        raise ValueError('A normal cut fit has inconsistent source, query or ownership dimensions.')
    if not np.isfinite(pixels).all()or np.any((pixels<0)|(pixels>1))or not np.isfinite(ownership).all()or np.any((ownership<0)|(ownership>1))or not np.isfinite(target_xy).all()or np.any(np.abs(target_xy)>1+1e-6):
        raise ValueError('A normal cut fit needs finite UNorm ownership and representable normal components.')
    if len(uv)==0:return pixels.copy(),dict(queries=0,changedTexels=0,unownedRgbaExact=True,unrepresentableQueries=0,
        componentMaximumBefore=0.,componentMaximumAfter=0.,componentRmsAfter=0.,fit=None)
    owned=ownership>0;result=pixels.copy();before=unpack_skin_normal(bilinear_gpu_rows(pixels,uv),1.)[:,:2]-target_xy
    # Near the cut, use a linear alpha representation of X. Fade that packing
    # change through the existing physical band; R*A stays exact at texel centres.
    x=result[:,:,0]*result[:,:,3]
    result[:,:,0]+= (1-result[:,:,0])*ownership
    np.divide(x,result[:,:,0],out=result[:,:,3],where=owned&(result[:,:,0]>0))
    sampled=bilinear_gpu_rows(result,uv);desired=(target_xy+1)*.5
    alpha=np.divide(desired[:,0],sampled[:,0],out=np.zeros(len(sampled)),where=sampled[:,0]>0)
    unrepresentable=(alpha<0)|(alpha>1)|(sampled[:,0]<=0)
    target=np.column_stack((np.clip(alpha,0,1),desired[:,1]))
    fitted,proof=fit_bilinear_cut(result[:,:,[3,1]],uv,target,owned)
    result[:,:,3]=fitted[:,:,0];result[:,:,1]=fitted[:,:,1]
    if not np.array_equal(result[~owned],pixels[~owned]):raise ValueError('A bilinear normal fit changed an unowned RGBA texel.')
    after=unpack_skin_normal(bilinear_gpu_rows(result,uv),1.)[:,:2]-target_xy
    if np.mean(after**2)>np.mean(before**2)+1e-12:raise ValueError('A bilinear normal fit worsened its sampled normal components.')
    return result,dict(queries=len(uv),changedTexels=int(np.any(result!=pixels,axis=2).sum()),unownedRgbaExact=True,
        unrepresentableQueries=int(unrepresentable.sum()),componentMaximumBefore=float(np.abs(before).max(initial=0)),
        componentMaximumAfter=float(np.abs(after).max(initial=0)),componentRmsAfter=float(np.sqrt(np.mean(after**2)))if after.size else 0.,fit=proof)


def joint_normal_domain(surface,edges,width,height,fade=.006):
    groups,_,adj=domain_graph(surface['packet'],surface['vertices'])
    distances,_=domain_distances(adj,np.unique(groups[np.asarray(edges).ravel()]))
    vertex_distance=distances[groups]
    weight=np.zeros((height,width),np.float32)
    positions=np.zeros((height,width,3),np.float32)
    nearest=np.full((height,width),np.inf,np.float32)
    greatest=np.zeros((height,width),np.float32)
    unrelated=np.zeros((height,width),bool)
    protected=np.zeros((height,width),bool)
    for triangle in surface['triangles']:
        pixels=periodic_pixels(surface['uv'][triangle],width,height,pixels_for)
        if pixels is None:continue
        yy,xx,bary=pixels
        valid=np.isfinite(vertex_distance[triangle])
        distance=bary@np.where(valid,vertex_distance[triangle],0)
        distance[np.any((bary>0)&~valid[None,:],axis=1)]=np.inf
        np.logical_or.at(unrelated,(yy,xx),~np.isfinite(distance))
        np.maximum.at(greatest,(yy,xx),np.where(np.isfinite(distance),distance,0))
        take=np.isfinite(distance)&(distance<nearest[yy,xx])
        positions[yy[take],xx[take]]=bary[take]@surface['vertices'][triangle]
        np.minimum.at(nearest,(yy,xx),distance)
    protected|=unrelated
    _,footprint=analytic_boundary_stencil(surface['uv'],surface['vertices'],edges,weight,positions,protected,unrelated,width,height,apply=False)
    radius=float(greatest[footprint&~protected].max(initial=0))
    t=np.clip((greatest-radius)/fade,0,1)
    weight[:]=1-t*t*(3-2*t)
    weight[~np.isfinite(nearest)|protected]=0
    envelope=protected_cut_envelope(protected,footprint)[0]if protected.any()and footprint.any()else np.ones((height,width),np.float32)
    weight*=envelope
    analytic_boundary_stencil(surface['uv'],surface['vertices'],edges,weight,positions,protected,unrelated,width,height)
    yy,xx=np.nonzero(weight>0)
    return dict(weight=weight,positions=positions,protected=protected,yy=yy,xx=xx)


def continuous_geometric_normal(uv,positions,weight,curve,source):
    edge,amount,distance=nearest_raw_chart(curve,positions,uv)
    arc=curve.arc(edge,amount)
    def difference(ii,tt):
        _,cut_uv,_=curve.evaluate(ii,tt)
        sampled=bilinear_gpu_rows(source,cut_uv)
        return -np.column_stack((sampled[:,0]*sampled[:,3]*2-1,sampled[:,1]*2-1))
    delta=difference(edge,amount)
    filtered=np.zeros_like(delta)
    sigma=np.minimum(distance*.75,curve.perimeter/8)
    nodes=np.linspace(-2,2,9);kernel=np.exp(-.5*nodes*nodes);kernel/=kernel.sum()
    for offset,k in zip(nodes,kernel):
        ii,tt=curve.at_arc(arc+offset*sigma)
        filtered+=k*(delta if offset==0 else difference(ii,tt))
    filtered[sigma<=1e-12]=delta[sigma<=1e-12]
    result=bilinear_gpu_rows(source,uv).copy()
    xy=np.column_stack((result[:,0]*result[:,3],result[:,1]))+filtered*np.asarray(weight)[:,None]*.5
    clipped=int(np.any((xy<0)|(xy>1),axis=1).sum())
    xy=np.clip(xy,0,1)
    red=np.maximum(result[:,0],xy[:,0]);alpha=np.divide(xy[:,0],red,out=np.zeros(len(red)),where=red>0)
    result[:,0]=red;result[:,1]=xy[:,1];result[:,3]=alpha
    return result,clipped


def c1_geometric_normal(surface,curve,source,domain):
    height,width=domain['weight'].shape
    yy,xx=domain['yy'],domain['xx']
    uv=np.column_stack(((xx+.5)/width,1-(yy+.5)/height))
    positions=domain['positions'][yy,xx].astype(float)
    face,_,_,_=raster(surface,width,height,domain['protected'],RasterMemo())
    gutter=face[yy,xx]<0
    if gutter.any():
        edge,amount,_=nearest_raw_chart(curve,positions[gutter],uv[gutter])
        _,uv[gutter],_=curve.evaluate(edge,amount)
    result=source.copy();clipped=0
    for start in range(0,len(yy),4096):
        take=slice(start,start+4096)
        rgba,count=continuous_geometric_normal(uv[take],positions[take],domain['weight'][yy[take],xx[take]],curve,source)
        result[height-1-yy[take],xx[take]]=rgba;clipped+=count
    _,cut_uv,_=curve.evaluate(np.repeat(np.arange(len(curve.edges)),33),np.tile(np.linspace(0,1,33),len(curve.edges)))
    result,fit=fit_bilinear_normal_cut(result,cut_uv,np.zeros((len(cut_uv),2)),domain['weight'][::-1])
    owned=domain['weight'][::-1]>0
    if not np.array_equal(result[~owned],source[~owned]):raise ValueError('A joint normal changed unowned source texels.')
    return result,dict(queries=len(yy),gutterQueries=int(gutter.sum()),clippedQueries=clipped,
        sharedReference='common-geometric-cut-normal-on-both-sides',unownedRgbaExact=True,bilinearNormalFit=fit)



def continuous_cut_normal(own_uv,positions,weight,head_curve,body_curve,head_normal,body_normal,head_scale,body_scale):
    """Join Lux normals in the physical cut frame, retaining source detail away from it."""
    if not np.isfinite([head_scale,body_scale]).all()or head_scale<=0 or body_scale<=0:
        raise ValueError('A skin normal cut requires positive finite material scales.')
    bi,bt,distance=body_curve.nearest(positions);arc=body_curve.arc(bi,bt)
    def difference(ii,tt):
        bp,buv,bframe=body_curve.evaluate(ii,tt);hi,ht,error=nearest_raw_chart(head_curve,bp,own_uv);_,huv,hframe=head_curve.evaluate(hi,ht)
        if np.any(error>1e-5):raise ValueError('The source-owned Head/Body normal cut exceeds ten micrometres.')
        body_world=normalize(np.einsum('nij,nj->ni',bframe,unpack_skin_normal(bilinear_gpu_rows(body_normal,buv),body_scale)))
        target=normalize(np.linalg.solve(hframe,body_world[:,:,None])[:,:,0])
        if np.any(target[:,2]<=0)or np.any(np.abs(target[:,:2]/head_scale)>1+1e-6):
            raise ValueError('The Body cut normal cannot be represented in the declared Head material frame.')
        source=bilinear_gpu_rows(head_normal,huv)
        return target[:,:2]/head_scale-(np.column_stack((source[:,0]*source[:,3],source[:,1]))*2-1),error
    delta,pair_error=difference(bi,bt);filtered=np.zeros_like(delta);sigma=np.minimum(distance*.75,body_curve.perimeter/8)
    nodes=np.linspace(-2,2,9);kernel=np.exp(-.5*nodes*nodes);kernel/=kernel.sum()
    for offset,k in zip(nodes,kernel):
        if offset==0:value=delta
        else:
            ii,tt=body_curve.at_arc(arc+offset*sigma);value,error=difference(ii,tt);pair_error=np.maximum(pair_error,error)
        filtered+=k*value
    exact=sigma<=1e-12;filtered[exact]=delta[exact]
    result=bilinear_gpu_rows(head_normal,own_uv).copy()
    xy=np.column_stack((result[:,0]*result[:,3],result[:,1]))+filtered*np.asarray(weight)[:,None]*.5
    clipped=np.count_nonzero(np.any((xy<0)|(xy>1),axis=1));xy=np.clip(xy,0,1)
    # Keep the existing R/A representation wherever possible. Converting every
    # unowned texel to R=1 would change the bilinear R*A response at chart cuts.
    red=np.maximum(result[:,0],xy[:,0]);alpha=np.divide(xy[:,0],red,out=np.zeros(len(red)),where=red>0)
    result[:,0]=red;result[:,1]=xy[:,1];result[:,3]=alpha
    return result,dict(maximumPhysicalPairError=float(pair_error.max()),physicalFrameNormalTransfer=True,clippedQueries=int(clipped))


def c1_normal(head,hc,bc,source,body,memo,domain,head_scale,body_scale):
    if not np.isfinite([head_scale,body_scale]).all()or head_scale<=0 or body_scale<=0:raise ValueError('A skin normal cut requires positive finite material scales.')
    h,w=domain['weight'].shape;y,x=domain['yy'],domain['xx'];sample_uv=np.column_stack(((x+.5)/w,1-(y+.5)/h))
    positions=domain['positions'][y,x].astype(float);face,_,_,_=raster(head,w,h,domain['protected'],memo);gutter=face[y,x]<0
    if gutter.any():i,t,_=nearest_raw_chart(hc,positions[gutter],sample_uv[gutter]);_,sample_uv[gutter],_=hc.evaluate(i,t)
    result=source.copy();maximum=0.;clipped=0
    for at in range(0,len(y),4096):
        q=slice(at,at+4096);rgba,proof=continuous_cut_normal(sample_uv[q],positions[q],domain['weight'][y[q],x[q]],hc,bc,source,body,head_scale,body_scale)
        result[h-1-y[q],x[q]]=rgba;maximum=max(maximum,proof['maximumPhysicalPairError']);clipped+=proof['clippedQueries']
    hi=np.repeat(np.arange(len(hc.edges)),33);ht=np.tile(np.linspace(0,1,33),len(hc.edges));positions,uv,hframe=hc.evaluate(hi,ht)
    bi,bt,distance=bc.nearest(positions);_,buv,bframe=bc.evaluate(bi,bt)
    if np.any(distance>1e-5):raise ValueError('The sampled skin normal cut exceeds ten micrometres.')
    body_world=normalize(np.einsum('nij,nj->ni',bframe,unpack_skin_normal(bilinear_gpu_rows(body,buv),body_scale)))
    target=normalize(np.linalg.solve(hframe,body_world[:,:,None])[:,:,0])
    if np.any(target[:,2]<=0)or np.any(np.abs(target[:,:2]/head_scale)>1+1e-6):raise ValueError('The sampled Body cut normal cannot be represented in the Head material frame.')
    result,fit=fit_bilinear_normal_cut(result,uv,target[:,:2]/head_scale,domain['weight'][::-1])
    owned=(domain['weight']>0)[::-1]
    if not np.array_equal(result[~owned],source[~owned]):raise ValueError('A skin normal cut changed unowned pixels.')
    return result,dict(queries=len(y),gutterQueries=int(gutter.sum()),maximumPhysicalPairError=maximum,physicalFrameNormalTransfer=True,clippedQueries=clipped,unownedRgbaExact=True,bilinearNormalFit=fit)

def periodic_pixels(triangle,width,height,raster):
    triangle=np.asarray(triangle);lower=np.floor(triangle.min(axis=0)).astype(int);upper=np.floor(triangle.max(axis=0)).astype(int)
    if np.any(upper-lower>8):raise ValueError('Unbounded repeating UV chart requires an explicit source authoring contract.')
    rows=[]
    for x in range(lower[0],upper[0]+1):
        for y in range(lower[1],upper[1]+1):
            row=raster(triangle-np.array([x,y]),width,height)
            if row is not None:rows.append(row)
    if not rows:return None
    return tuple(np.concatenate([row[index]for row in rows],axis=0)for index in range(3))

def analytic_boundary_stencil(uv,vertices,edges,weight,positions,protected,unowned_occupied,width,height,radius=1.5,apply=True):
    """Own the actual Repeat cut footprint, preserving every unowned occupant.

    Analytic UV segment distance resolves adjacent edge footprints. An owned
    collar pixel can supply a boundary gutter; an unrelated/native outside-band
    occupant remains immutable even when another cut chart shares its texel.
    """
    best=np.full((height,width),np.inf,np.float32);footprint=np.zeros((height,width),bool)
    locked=np.asarray(protected,bool)|np.asarray(unowned_occupied,bool)
    for edge in edges:
        ua,ub=uv[edge];pa,pb=vertices[edge];extent=np.asarray([width,height]);lo=np.floor(np.minimum(ua,ub)*extent-radius-1).astype(int);hi=np.ceil(np.maximum(ua,ub)*extent+radius+1).astype(int)
        x,y=np.meshgrid(np.arange(lo[0],hi[0]+1),np.arange(lo[1],hi[1]+1));pixels=np.column_stack((x.ravel(),y.ravel()));query=(pixels+.5)/extent;delta=(ub-ua)*extent;square=float(np.dot(delta,delta))
        if square<=1e-20:raise ValueError('A physical cut edge has a singular UV segment.')
        t=np.clip(np.sum((query-ua)*extent*delta,axis=1)/square,0,1);distance=np.linalg.norm((query-ua-t[:,None]*(ub-ua))*extent,axis=1);xx=pixels[:,0]%width;yy=height-1-pixels[:,1]%height;inside=distance<=radius
        footprint[yy[inside],xx[inside]]=True;take=inside&(distance<best[yy,xx])&~locked[yy,xx]
        if take.any():
            py,px=yy[take],xx[take];best[py,px]=distance[take]
            if apply:weight[py,px]=1;positions[py,px]=pa*(1-t[take,None])+pb*t[take,None]
    assert not np.any((weight>0)&locked),'The field changed an unowned occupied or protected texel.'
    return dict(analyticFootprintTexels=int(footprint.sum()),ownedStencilTexels=int(np.isfinite(best).sum()),faceProtectedFootprintTexels=int((footprint&protected).sum()),unownedOccupiedFootprintTexels=int((footprint&unowned_occupied).sum()),allUnownedOccupiedExact=True,wholeCutFootprintEligibleOnlyWithinOwnedBandOrUnusedGutter=True,nearestActualUvEdgeOwnership=True),footprint

def periodic_mask_distance(mask):
    """Exact Repeat-addressed L1 distance to a binary native atlas region."""
    mask=np.asarray(mask,bool);h,w=mask.shape
    if not mask.any():raise ValueError('Distance needs a nonempty owned atlas region.')
    x=np.arange(w,dtype=np.int32)[None,:];last=np.max(np.where(mask,x,-2*w),axis=1);first=np.min(np.where(mask,x,3*w),axis=1)
    left=np.maximum.accumulate(np.where(mask,x,last[:,None]-w),axis=1);right=np.minimum.accumulate(np.where(mask,x,first[:,None]+w)[:,::-1],axis=1)[:,::-1]
    distance=np.minimum(x-left,right-x).astype(np.int32)
    # Two sweeps across each vertical wrap suffice after the horizontal exact
    # min-plus pass; all edge costs are one and sources are immutable zeros.
    for _ in range(2):
        distance[0]=np.minimum(distance[0],distance[-1]+1)
        for y in range(1,h):distance[y]=np.minimum(distance[y],distance[y-1]+1)
        distance[-1]=np.minimum(distance[-1],distance[0]+1)
        for y in range(h-2,-1,-1):distance[y]=np.minimum(distance[y],distance[y+1]+1)
    return distance.astype(np.float32)

def protected_cut_envelope(protected,cut_footprint):
    """C1 boundary conditions: zero at native protection, one at owned cut."""
    dp=periodic_mask_distance(protected);dc=periodic_mask_distance(cut_footprint);q=dp/np.maximum(dp+dc,1e-20);value=q*q*(3-2*q);value[protected]=0
    return value,dict(distanceMetric='Exact periodic atlas L1',protectedBoundaryValue=0,protectedBoundaryDerivative=0,cutFootprintValue=1,noArbitraryFaceTaperWidth=True,cutFootprintProtectedOverlap=int(np.count_nonzero(protected&cut_footprint)))

def unit(v):return v/np.maximum(np.linalg.norm(v,axis=-1,keepdims=True),np.float32(1e-30))

def uv(p):return np.asarray(next(v for v in p['uv'] if v['channel']==0)['values'],np.float32).reshape(-1,4)[:,:2]

def matrix(v):return np.asarray(v,np.float32).reshape(4,4).T

def points(v,m):return v@m[:3,:3].T+m[:3,3]

def rotate(v,a,b):
    a,b=unit(a),unit(b);dot=np.sum(a*b,axis=-1)
    if np.any(dot<-.999999):raise ValueError('Opposite native normals require the native quaternion owner witness.')
    xyz=np.cross(a,b);w=(1+dot)[:,None];q=np.concatenate((xyz,w),axis=1);q=unit(q);xyz,w=q[:,:3],q[:,3,None]
    return v+np.float32(2)*np.cross(xyz,np.cross(xyz,v)+w*v)

def raw_edges(triangles):
    result=collections.defaultdict(list)
    for index,t in enumerate(triangles):
        for a,b in zip(t,np.roll(t,-1)):result[tuple(sorted((int(a),int(b))))].append(index)
    return result

def cut_edges(geometry,allowed):
    candidates=[e for e,v in geometry['edges'].items() if len(v)==1 and set(e)<=allowed];chosen={};duplicates=0
    for edge in candidates:
        a,b=edge;pa,pb=map(tuple,geometry['vertices'][list(edge)]);key=tuple(sorted((pa,pb)));ordered=edge if pa<=pb else (b,a)
        if key in chosen:
            old=chosen[key];delta=geometry['uv'][list(ordered)]-geometry['uv'][list(old)]
            if np.max(np.abs(delta-np.rint(delta)))>2e-6 or np.max(np.abs(geometry['normal'][list(ordered)]-geometry['normal'][list(old)]))>2e-6 or np.max(np.abs(geometry['tangent'][list(ordered)]-geometry['tangent'][list(old)]))>2e-6:raise ValueError('A coincident cut edge has distinct non-periodic source UV/TBN ownership.')
            duplicates+=1
        else:chosen[key]=ordered
    if not chosen:raise ValueError('The source-selected cut has no native boundary edges.')
    return np.asarray(list(chosen.values()),int),duplicates

class Geometry(TypedDict):
    vertices: np.ndarray
    normal: np.ndarray
    tangent: np.ndarray
    uv: np.ndarray
    triangles: np.ndarray
    edges: dict[tuple[int, int], list[int]]
    packet: dict[str, Any]


def geometry(p,v,n,t) -> 'Geometry':
    triangles=np.asarray(p['subMeshes'][0],int).reshape(-1,3);return {'vertices':v,'normal':n,'tangent':t,'uv':uv(p),'triangles':triangles,'edges':raw_edges(triangles),'packet':p}

def serialized_skin_context(head_packet,body_packet,head_bones,body_bones):
    """Measure the saved surfaces without transporting a preceding source mesh."""
    frame,_,error=skin_palette_frame(head_packet,body_packet,head_bones,body_bones)
    frame=np.asarray(frame,np.float32)
    n=unit(arr(head_packet,'normals',3)@np.linalg.inv(frame[:3,:3]));t=arr(head_packet,'tangents',4).copy()
    transformed=t[:,:3]@frame[:3,:3].T;t[:,:3]=unit(transformed-n*np.sum(transformed*n,axis=1)[:,None]);t[:,3]*=np.sign(np.linalg.det(frame[:3,:3]))
    head=geometry(head_packet,points(arr(head_packet,'vertices',3),frame),n,t)
    body=geometry(body_packet,arr(body_packet,'vertices',3),unit(arr(body_packet,'normals',3)),arr(body_packet,'tangents',4))
    def boundary(surface):
        _,groups=np.unique(surface['vertices'],axis=0,return_inverse=True);incidence=collections.defaultdict(list)
        for face in surface['triangles']:
            for a,z in zip(face,np.roll(face,-1)):
                if groups[a]!=groups[z]:incidence[tuple(sorted((int(groups[a]),int(groups[z]))))].append((int(a),int(z)))
        edges=np.asarray([value[0]for value in incidence.values()if len(value)==1],int).reshape(-1,2)
        if not len(edges):raise ValueError('Serialized skin surfaces have no physical open boundary.')
        return edges
    he=boundary(head);be=boundary(body)
    def matching(surface,edges,other,other_edges):
        p=surface['vertices'][edges];samples=np.stack((p[:,0],p.mean(axis=1),p[:,1]),axis=1)
        q=samples.reshape(-1,3);a=other['vertices'][other_edges[:,0]];d=other['vertices'][other_edges[:,1]]-a
        square=np.sum(d*d,axis=1)
        if np.any(square<=1e-24):raise ValueError('A serialized skin boundary has a collapsed physical edge.')
        t=np.clip(np.sum((q[:,None]-a)*d,axis=2)/square,0,1)
        distance=np.linalg.norm(q[:,None]-a-t[:,:,None]*d,axis=2).min(axis=1)
        return edges[np.all(distance.reshape(-1,3)<=1e-5,axis=1)]
    head_edges,body_edges=he,be;he=matching(head,head_edges,body,body_edges);be=matching(body,body_edges,head,head_edges)
    if min(len(he),len(be))<3:raise ValueError('Serialized skin surfaces do not share a complete physical cut.')
    hc=BoundaryAtlas(head['vertices'],head['uv'],head['normal'],head['tangent'],he)
    bc=BoundaryAtlas(body['vertices'],body['uv'],body['normal'],body['tangent'],be)
    return head,body,he,be,hc,bc,dict(source='saved-serialized-skin-surfaces',sourceVertexChannelsUnchanged=True,commonHeadToBody=frame.T.ravel().tolist(),allSharedBindMaximumAbs=error)


def source_context(row,head_packet,body_packet):
    h=head_packet;b=body_packet;hp=arr(h,'vertices',3).copy();hn=arr(h,'normals',3).copy();ht=arr(h,'tangents',4).copy();bp=arr(b,'vertices',3);bn=arr(b,'normals',3);bt=arr(b,'tangents',4);proof={};body_members={int(v) for aliases in row['bodyCut']['vertexAliases'] for v in aliases}
    if row['bodyCut'].get('expandExactNativePositionAliases'):
        selected={tuple(bp[i]) for i in body_members};body_members={i for i,p in enumerate(bp) if tuple(p) in selected}
    if row['lod']:
        j=row['headSource']['joint'];A=matrix(j['headToBody']);head_members={i for i,o in enumerate(j['origins']) if o<0 and -o-1 in body_members};proof['source']='saved-coupled-canonical'
    else:
        hb=np.asarray(h['bindPoses'],np.float32).reshape(-1,4,4).transpose(0,2,1);bb=np.asarray(b['bindPoses'],np.float32).reshape(-1,4,4).transpose(0,2,1);hpaths=row['headNativeBonePaths'];bpaths=row['bodyNativeBonePaths'];weights=arr(b,'weights',4);indices=arr(b,'boneIndices',4).astype(int);cutids=sorted(body_members);required=sorted(set(indices[cutids][weights[cutids]>0].tolist()));shared={i:hpaths.index(p) for i,p in enumerate(bpaths) if p in hpaths};candidates=[]
        for anchor in required:
            if anchor not in shared:continue
            frame=(np.linalg.inv(bb[anchor].astype(float))@hb[shared[anchor]]).astype(np.float32);errors=[float(np.abs(hb[z]-bb[i]@frame).max()) for i,z in shared.items()];candidates.append((max(errors),anchor,frame))
        if not candidates:raise ValueError('No positive donor-support common native palette anchor.')
        # Match the existing static owner: its first shared native palette anchor
        # is accepted only after checking EVERY shared bind pose at weld=0.0001.
        anchor=min(shared);A=(np.linalg.inv(bb[anchor].astype(float))@hb[shared[anchor]]).astype(np.float32);error=max(float(np.abs(hb[z]-bb[i]@A).max()) for i,z in shared.items())
        if error>0.0001:raise ValueError('Existing static owner rejects the shared native palette frame.')
        toHead=np.linalg.inv(A.astype(float)).astype(np.float32);head_members=set()
        effectiveWeights=arr(h,'weights',4).copy();effectiveIndices=arr(h,'boneIndices',4).astype(int).copy()
        for c in row['headSource']['correspondence']['sourceCorrespondences']:
            first=min(c['bodyVertices']);ids=np.asarray(c['headVertices'],int);head_members.update(map(int,ids));normal=bn[first]@A[:3,:3];desired=np.broadcast_to(normal,(len(ids),3));ht[ids,:3]=rotate(ht[ids,:3],hn[ids],desired);hn[ids]=desired;hp[ids]=points(bp[[first]],toHead)[0]
            sourceIndices=indices[first];sourceWeights=weights[first];mapped=[hpaths.index(bpaths[i]) if w>0 and bpaths[i] in hpaths else 0 if w<=0 else -1 for i,w in zip(sourceIndices,sourceWeights)]
            if min(mapped)>=0:effectiveWeights[ids]=sourceWeights;effectiveIndices[ids]=mapped
        h=dict(h,vertices=hp.ravel(),weights=effectiveWeights.ravel(),boneIndices=effectiveIndices.ravel())
        proof.update(source='current-initial-plus-existing-static-boundary-transport',portableQuaternionBoundary=True,existingStaticOwnerAnchor=anchor,allSharedBindMaximumAbs=error,existingStaticOwnerWeld=0.0001)
    M=A[:3,:3];worldp=points(hp,A);worldn=unit(hn@np.linalg.inv(M));worldt=ht[:,:3]@M.T;worldt=unit(worldt-worldn*np.sum(worldt*worldn,axis=1)[:,None]);worldt=np.column_stack((worldt,ht[:,3]*np.sign(np.linalg.det(M))))
    head=geometry(h,worldp,worldn,worldt);body=geometry(b,bp,unit(bn),bt);he,hd=cut_edges(head,head_members);be,bd=cut_edges(body,body_members);hc=BoundaryAtlas(head['vertices'],head['uv'],head['normal'],head['tangent'],he);bc=BoundaryAtlas(body['vertices'],body['uv'],body['normal'],body['tangent'],be);proof.update(headPeriodicAliasesCollapsed=hd,bodyPeriodicAliasesCollapsed=bd)
    proof['commonHeadToBody']=A.T.ravel().tolist()
    return head,body,he,be,hc,bc,proof

def domain_graph(packet,positions):
    bone_indices=np.asarray(packet['boneIndices']).reshape(-1,4)
    weights=np.asarray(packet['weights'],dtype=np.float32).reshape(-1,4)
    keys={};groups=[]
    for p,bi,bw in zip(np.asarray(packet['vertices'],dtype=np.float32).reshape(-1,3),bone_indices,weights):
        key=(tuple(p.tolist()),tuple(sorted((int(i),float(w)) for i,w in zip(bi,bw) if w)))
        groups.append(keys.setdefault(key,len(keys)))
    groups=np.asarray(groups,dtype=np.intp)
    group_positions=np.zeros((len(keys),3));group_positions[groups]=positions
    adjacency=[{} for _ in keys]
    for triangle in np.concatenate([np.asarray(t).reshape(-1,3) for t in packet['subMeshes']]):
        for a,b in zip(groups[triangle],np.roll(groups[triangle],-1)):
            if a==b:continue
            length=float(np.linalg.norm(group_positions[a]-group_positions[b]))
            adjacency[a][b]=length;adjacency[b][a]=length
    return groups,group_positions,adjacency

def domain_distances(adjacency,seeds):
    result=np.full(len(adjacency),np.inf);nearest=np.full(len(adjacency),-1,dtype=int)
    queue=[]
    for seed in seeds:result[seed]=0;nearest[seed]=seed;heapq.heappush(queue,(0,int(seed)))
    while queue:
        old,i=heapq.heappop(queue)
        if result[i]!=old:continue
        for j,length in adjacency[i].items():
            candidate=old+length
            if candidate<result[j]:result[j]=candidate;nearest[j]=nearest[i];heapq.heappush(queue,(candidate,j))
    return result,nearest

def pixels_for(uv, width, height):
    # Deliberately no implicit averaging: every actual source triangle is visited.
    tex = np.column_stack((uv[:, 0] * width - .5, (1. - uv[:, 1]) * height - .5))
    delta = np.stack((tex[1] - tex[0], tex[2] - tex[0]), axis=1)
    determinant = float(np.linalg.det(delta))
    if abs(determinant) < 1e-12:
        return None
    low = np.maximum(0, np.ceil(tex.min(axis=0)).astype(int))
    high = np.minimum((width - 1, height - 1), np.floor(tex.max(axis=0)).astype(int))
    if np.any(high < low):
        return None
    yy, xx = np.mgrid[low[1]:high[1] + 1, low[0]:high[0] + 1]
    ab = np.stack((xx - tex[0, 0], yy - tex[0, 1]), axis=-1) @ np.linalg.inv(delta).T
    bary = np.dstack((1. - ab.sum(axis=2), ab))
    inside = np.all(bary >= -1e-7, axis=2)
    if not inside.any():
        return None
    return yy[inside], xx[inside], bary[inside]

def alias_distances(adj,seeds):
    out=np.full(len(adj),np.inf);queue=[]
    for i in seeds:out[i]=0;heapq.heappush(queue,(0.,int(i)))
    while queue:
        value,i=heapq.heappop(queue)
        if value!=out[i]:continue
        for j,cost in adj[i].items():
            trial=value+cost
            if trial<out[j]:out[j]=trial;heapq.heappush(queue,(trial,j))
    return out

def digest(*arrays):
    s=hashlib.sha256()
    for a in arrays:
        a=np.ascontiguousarray(a);s.update(str((a.dtype.str,a.shape)).encode());s.update(a.tobytes())
    return s.hexdigest()

def key(v):return hashlib.sha256(json.dumps(v,sort_keys=True,separators=(',',':')).encode()).hexdigest()

def head_protection(family_rows,packets,width,height):
    mask=np.zeros((height,width),bool);triangles=0
    for row in family_rows:
        p=packets[row['headSource']['initialCanonical']['path']];source_uv=uv(p);weights=arr(p,'weights',4);bones=arr(p,'boneIndices',4).astype(int);head=np.asarray([any(part=='Bip001 Head' for part in path.split('/')) for path in row['headNativeBonePaths']]);flags=np.sum(weights*head[bones],axis=1)>0
        for tri in np.asarray(p['subMeshes'][0],int).reshape(-1,3):
            if not flags[tri].any():continue
            pix=periodic_pixels(source_uv[tri],width,height,pixels_for)
            if pix is not None:mask[pix[0],pix[1]]=True
            triangles+=1
    return mask,dict(protectedTexels=int(mask.sum()),headOrDescendantInfluenceTriangles=triangles,allThreeCurrentInitialLodsUnion=True,positiveNativeHeadInfluenceProtected=True,maskSha256=digest(mask))

def domain_buffers(row,head_packet,body_packet,family_rows,packets,source_epoch) -> dict[str, Any]:
    started=time.monotonic();head,body,he,be,hc,bc,context=source_context(row,head_packet,body_packet);sources=row['sampler'];native=sources['textureInputs']['headMain'];width,height=native['width'],native['height'];assert width>0 and height>0
    protected,pinfo=head_protection(family_rows,packets,width,height);protected=protected.copy();groups,gp,adj=domain_graph(head['packet'],head['vertices']);distances,_=domain_distances(adj,np.unique(groups[he.ravel()]));vd=distances[groups];weight=np.zeros((height,width),np.float32);positions=np.zeros((height,width,3),np.float32);surface_distance=np.full((height,width),np.inf,np.float32);greatest_distance=np.zeros((height,width),np.float32);unrelated=np.zeros((height,width),bool)
    for tri in head['triangles']:
        pix=periodic_pixels(head['uv'][tri],width,height,pixels_for)
        if pix is None:continue
        yy,xx,bary=pix;valid=np.isfinite(vd[tri]);d=bary@np.where(valid,vd[tri],0);d[np.any((bary>0)&~valid[None,:],axis=1)]=np.inf;np.logical_or.at(unrelated,(yy,xx),~np.isfinite(d));np.maximum.at(greatest_distance,(yy,xx),np.where(np.isfinite(d),d,0));take=np.isfinite(d)&(d<surface_distance[yy,xx])&~protected[yy,xx]
        if take.any():y,x=yy[take],xx[take];positions[y,x]=bary[take]@head['vertices'][tri]
        np.minimum.at(surface_distance,(yy,xx),d)
    native_face_protected=protected.copy();protected|=unrelated;_,footprint=analytic_boundary_stencil(head['uv'],head['vertices'],he,weight,positions,native_face_protected,unrelated,width,height,apply=False);eligible=footprint&~protected;radius=float(greatest_distance[eligible].max()) if eligible.any() else 0.;t=np.clip((surface_distance-radius)/.060,0,1);weight=1-t*t*(3-2*t);weight[protected]=0;envelope,envelope_proof=protected_cut_envelope(protected,footprint);weight*=envelope;stencil,footprint=analytic_boundary_stencil(head['uv'],head['vertices'],he,weight,positions,native_face_protected,unrelated,width,height);owned=weight>0;assert not np.any(owned&protected)
    yy,xx=np.nonzero(owned);domain_key=digest(weight,owned,protected,footprint,positions[yy,xx]);base=dict(domain=domain_key,headBoundary=digest(hc.first,hc.delta,hc.uv[hc.edges]),bodyBoundary=digest(bc.first,bc.delta,bc.uv[bc.edges]),fieldSha256=source_epoch['fieldSha256'],domainSha256=source_epoch['domainSha256'],fade=.060,distanceAverage=.75,quadrature=9);textures=sources['textureInputs'];st=sources['capturedNativeMainST'];main=key(dict(base,sourceInputs=[textures['headMain'],textures['bodyMain']],st=[st['headMainScale'],st['headMainOffset'],st['bodyMainScale'],st['bodyMainOffset']]))
    report=dict(head=row['head'],lod=row['lod'],domainKey=domain_key,domainTextureRole='Main',width=width,height=height,mips=native['mips'],mainOperatorKey=main,ownedTexels=len(yy),nativeFaceProtection=pinfo,cutStencil=stencil,protectedC1Envelope=envelope_proof,fullWeightRadiusMetres=radius,outerFadeRadiusMetres=radius+.060,contextProof=context,seconds=time.monotonic()-started,transientLogicalBufferBytes=sum(a.nbytes for a in (protected,native_face_protected,weight,positions,surface_distance,greatest_distance,unrelated,footprint,envelope,owned)),pixelsGenerated=False,domainBuffersPersisted=False,sourceProtectionPassed=True)
    return dict(report=report,weight=weight,positions=positions,protected=protected,hc=hc,bc=bc,yy=yy,xx=xx)

class RasterMemo:
    def __init__(self):
        self.cache=collections.OrderedDict();self.bytes=0;self.peak=0;self.hits=0;self.misses=0;self.seconds=0.;self.original=periodic_pixels
        self.tone_vertices: tuple[str,Any,float] | None=None
    def call(self,uv,w,h,fn):
        key=(np.asarray(uv,'<f8').tobytes(),w,h)
        if key in self.cache:self.hits+=1;self.cache.move_to_end(key);return self.cache[key]
        start=time.monotonic();value=self.original(uv,w,h,fn);self.seconds+=time.monotonic()-start;self.misses+=1
        if value is not None:
            size=sum(a.nbytes for a in value)
            if size<=192*1024**2:
                while self.cache and self.bytes+size>192*1024**2:
                    _,old=self.cache.popitem(last=False);self.bytes-=sum(a.nbytes for a in old)
                for a in value:a.flags.writeable=False
                self.cache[key]=value;self.bytes+=size;self.peak=max(self.peak,self.bytes)
        return value

def raster(g,w,h,protected,memo):
    face=np.full((h,w),-1,np.int32);barymap=np.zeros((h,w,3),np.float32);position=np.zeros((h,w,3),np.float32);ambiguous=np.zeros((h,w),bool)
    for ti,tri in enumerate(g['triangles']):
        pix=memo.call(g['uv'][tri],w,h,pixels_for)
        if pix is None:continue
        y,x,b=pix;p=b@g['vertices'][tri];occupied=face[y,x]>=0;diff=occupied&(np.linalg.norm(position[y,x]-p,axis=1)>1e-5);ambiguous[y[diff],x[diff]]=True;take=~occupied;y,x,b,p=y[take],x[take],b[take],p[take];face[y,x]=ti;barymap[y,x]=b;position[y,x]=p
    return face,barymap,position,ambiguous

def linear_rgba(pixels):
    a=np.asarray(pixels).astype(float)/255;a[:,:,:3]=lookup[np.asarray(pixels)[:,:,:3]];return a

def c1_main(row,head,hc,bc,source,body,memo,domain):
    h,w=domain['weight'].shape;y,x=domain['yy'],domain['xx'];uv=np.column_stack(((x+.5)/w,1-(y+.5)/h));positions=domain['positions'][y,x].astype(float)
    face,_,_,_=raster(head,w,h,domain['protected'],memo);gutter=face[y,x]<0;sample_uv=uv.copy()
    if gutter.any():i,t,_=nearest_raw_chart(hc,positions[gutter],uv[gutter]);_,sample_uv[gutter],_=hc.evaluate(i,t)
    result=source.copy();maximum=0.
    for at in range(0,len(y),4096):
        q=slice(at,at+4096);rgba,proof=continuous_cut_main(sample_uv[q],positions[q],domain['weight'][y[q],x[q]],hc,bc,source,body)
        result[h-1-y[q],x[q]]=rgba;maximum=max(maximum,proof['maximumPhysicalPairError'])
    positions,cut_uv,_=hc.evaluate(np.repeat(np.arange(len(hc.edges)),33),np.tile(np.linspace(0,1,33),len(hc.edges)))
    bi,bt,error=bc.nearest(positions);_,body_uv,_=bc.evaluate(bi,bt)
    if np.any(error>1e-5):raise ValueError('A bilinear Main cut has no matching Body source.')
    result,fit=fit_bilinear_cut(result,cut_uv,bilinear_gpu_rows(body,body_uv),(domain['weight']>0)[::-1])
    assert np.array_equal(result[domain['protected'][::-1]],source[domain['protected'][::-1]])
    return result,domain,dict(queries=len(y),gutterQueries=int(gutter.sum()),maximumPhysicalPairError=maximum,rgbaC1Once=True,
        bilinearCutFit=fit,normalImageConsumed=False)

def alias_main(row,head,he,main,domain,memo,*,protected_vertices=None,preserve_local_contrast=False):
    h,w=main.shape[:2];protected=domain['protected'].copy();face,bary,pmap,ambiguous=raster(head,w,h,protected,memo);zero=np.zeros((h,w),np.float32)
    _,cut=analytic_boundary_stencil(head['uv'],head['vertices'],he,zero,pmap,protected,np.zeros_like(protected),w,h,apply=False)
    paths=list(row['headNativeBonePaths']);joint=row['headSource'].get('joint')
    if joint:paths.extend(row['bodyNativeBonePaths'][i]for i in joint.get('donorBones',[]))
    seams,_,counts,_=native_alias_edges(head,paths,protected_vertices)
    if not seams:return main.copy(),dict(curves=0,queries=0,gutterQueries=0,sourceCounts=counts,c1CutFootprintExact=True,aliasMainAlphaExact=True,normalImageConsumed=False)
    edges=np.asarray([s[k]for s in seams for k in('a','b')],int).reshape(-1,2)
    _,footprint=analytic_boundary_stencil(head['uv'],head['vertices'],edges,zero,pmap,protected,np.zeros_like(protected),w,h,apply=False)
    locked=protected|ambiguous|cut;envelope,_=protected_cut_envelope(locked,footprint);gutter=np.zeros((h,w),bool)
    for seam in seams:
        for side in(0,1):
            edge=seam['a'if side==0 else'b'];ua,ub=head['uv'][edge];extent=np.asarray([w,h]);lo=np.floor(np.minimum(ua,ub)*extent-2.5).astype(int);hi=np.ceil(np.maximum(ua,ub)*extent+2.5).astype(int)
            gx,gy=np.meshgrid(np.arange(lo[0],hi[0]+1),np.arange(lo[1],hi[1]+1));grid=np.column_stack((gx.ravel(),gy.ravel()));query=(grid+.5)/extent;delta=(ub-ua)*extent;square=float(np.dot(delta,delta));t=np.clip(np.sum((query-ua)*extent*delta,axis=1)/square,0,1)
            distance=np.linalg.norm((query-ua-t[:,None]*(ub-ua))*extent,axis=1);xx=grid[:,0]%w;yy=h-1-grid[:,1]%h;take=(distance<=1.5)&~locked[yy,xx]&(face[yy,xx]<0);yy,xx,tt=yy[take],xx[take],t[take]
            ti=seam['faces'][side];tri=head['triangles'][ti];b=np.zeros((len(tt),3));b[:,int(np.nonzero(tri==edge[0])[0][0])]=1-tt;b[:,int(np.nonzero(tri==edge[1])[0][0])]=tt
            face[yy,xx]=ti;bary[yy,xx]=b;pmap[yy,xx]=b@head['vertices'][tri];gutter[yy,xx]=True
    y,x=np.nonzero((face>=0)&~locked);tri=head['triangles'][face[y,x]];b=bary[y,x].astype(float);positions=pmap[y,x].astype(float);uv=np.column_stack(((x+.5)/w,1-(y+.5)/h));sample_uv=uv.copy();g=gutter[y,x];sample_uv[g]=np.sum(head['uv'][tri[g]]*b[g,:,None],axis=1)
    source=bilinear_gpu_rows(main,sample_uv);adj=[{}for _ in head['vertices']]
    for t in head['triangles']:
        for a,z in zip(t,np.roll(t,-1)):cost=float(np.linalg.norm(head['vertices'][a]-head['vertices'][z]));adj[a][int(z)]=cost;adj[z][int(a)]=cost
    accum=np.zeros((len(y),3));den=np.full(len(y),1/.060**2);groups=collections.defaultdict(list);curves=0
    for seam in seams:groups[tuple(sorted(seam['charts']))].append(seam)
    for pair,items in groups.items():
        for component in split_alias_paths(items,head):
            for side,own in enumerate(pair):
                curve=consensus_main(AliasCurve(head,component,own,opposite=bool(side)),main);vd=alias_distances(adj,np.unique(curve.edges));valid=np.isfinite(vd[tri]);distance=np.sum(np.where(valid,vd[tri],0)*b,axis=1);distance[np.any(~valid&(b>0),axis=1)]=np.inf;selected=np.nonzero(distance<.060)[0]
                for at in range(0,len(selected),4096):
                    q=selected[at:at+4096];delta,proof=continuous_alias_main(sample_uv[q],positions[q],np.ones(len(q)),curve,main,preserve_local_contrast=preserve_local_contrast);fade=1-np.clip(distance[q]/.060,0,1)**2;k=(fade/np.maximum(np.maximum(distance[q],proof['physicalDistance']),1e-6))**2*envelope[y[q],x[q]];accum[q]+=delta*k[:,None];den[q]+=k
                curves+=1
    correction=accum/den[:,None]
    result=main.copy();result[h-1-y,x,:3]=np.clip(source[:,:3]*(1+correction)if preserve_local_contrast else source[:,:3]+correction,0,1)
    assert np.array_equal(result[locked[::-1]],main[locked[::-1]])and np.array_equal(result[:,:,3],main[:,:,3])
    return result,dict(curves=curves,queries=len(y),gutterQueries=int(g.sum()),sourceCounts=counts,c1CutFootprintExact=True,aliasMainAlphaExact=True,normalImageConsumed=False,preserveLocalContrast=preserve_local_contrast)

def weighted(values, weights, q=.5):
    result=[]
    for values in np.asarray(values).T:
        order=np.argsort(values)
        at=np.searchsorted(np.cumsum(weights[order]),weights.sum()*q)
        result.append(float(values[order[min(at,len(values)-1)]]))
    return np.asarray(result)

def reference(values, weights):
    luminance=values[:,:3] @ [.2126,.7152,.0722]
    lower=weighted(luminance[:,None],weights,.5)[0]
    selected=luminance>=lower
    return weighted(values[selected,:3],weights[selected]),dict(excludedDarkestPhysicalAreaFraction=.5,
        minimumLinearLuminance=float(lower),samples=int(selected.sum()))

def coefficient(row,preencoded,body,roi_record,roi):
    pixels=linear_rgba(preencoded)
    s,sp=reference(bilinear_gpu_rows(pixels,roi['headUv']),roi['headWeight']);t,tp=reference(bilinear_gpu_rows(body,roi['bodyUv']),roi['bodyWeight'])
    return dict(sourceInputKey=row['samplerSourceInputKey'],calibratedLod=0,sharedAcrossSourceLodsByExplicitPolicy=True,s=s.tolist(),t=t.tolist(),**v3_tone_parameters(s,t),headReference=sp,bodyReference=tp,roi=roi_record['roi'])

def downsample(value):
 h,w=value.shape[:2];nh,nw=max(1,h//2),max(1,w//2)
 if h==1:value=np.repeat(value,2,axis=0)
 if w==1:value=np.repeat(value,2,axis=1)
 return value.reshape(nh,2,nw,2,*value.shape[2:])

def down(value):
    h,w=value.shape[:2]
    if h==1:value=np.repeat(value,2,axis=0)
    if w==1:value=np.repeat(value,2,axis=1)
    return value.reshape(max(1,h//2),2,max(1,w//2),2,*value.shape[2:])

def main_linear(a):
    value=a.astype(float)/255;value[:,:,:3]=np.where(value[:,:,:3]<=.04045,value[:,:,:3]/12.92,((value[:,:,:3]+.055)/1.055)**2.4);return value

def decode_bc3(raw,w,h,mode,codebook):
 block=np.frombuffer(raw,np.uint8).reshape(-1,16);c0=block[:,8].astype(np.uint16)|(block[:,9].astype(np.uint16)<<8);c1=block[:,10].astype(np.uint16)|(block[:,11].astype(np.uint16)<<8);word=cast(np.ndarray,sum(block[:,12+k].astype(np.uint32)<<(k*8)for k in range(4)));ci=(word[:,None]>>(2*np.arange(16,dtype=np.uint32)))&3;aw=cast(np.ndarray,sum(block[:,2+k].astype(np.uint64)<<(k*8)for k in range(6)));ai=(aw[:,None]>>(3*np.arange(16,dtype=np.uint64)))&7;p5=codebook['palettes'][(5,mode)];p6=codebook['palettes'][(6,mode)];result=np.empty((len(block),16,4),np.float32);result[:,:,0]=p5[c0[:,None]>>11,c1[:,None]>>11,ci,0];result[:,:,1]=p6[(c0[:,None]>>5)&63,(c1[:,None]>>5)&63,ci,1];result[:,:,2]=p5[c0[:,None]&31,c1[:,None]&31,ci,2];result[:,:,3]=codebook['alpha'][block[:,0,None],block[:,1,None],ai]
 return result.reshape((h+3)//4,(w+3)//4,4,4,4).transpose(0,2,1,3,4).reshape(((h+3)//4)*4,((w+3)//4)*4,4)[:h,:w].copy()

def encode_main_rgba(pixels,sorted_values,sorted_codes,*,source_float=None,source_rgba=None,source_protected=None,native_alpha=False):
    result=np.clip(np.rint(pixels*255),0,255).astype(np.uint8)
    at=np.clip(np.searchsorted(sorted_values,pixels[:,:,:3]),1,len(sorted_values)-1)
    lo=at-1
    choose=np.abs(sorted_values[at]-pixels[:,:,:3])<np.abs(sorted_values[lo]-pixels[:,:,:3])
    result[:,:,:3]=sorted_codes[np.where(choose,at,lo)]
    if source_float is not None:
        result=restore_native_main_encoding(result,pixels,source_float,source_rgba,source_protected,native_alpha)
    elif source_rgba is not None or source_protected is not None or native_alpha:
        raise ValueError('Native packing needs its explicit source float and native RGBA authority.')
    return result

def restore_native_main_encoding(encoded,pixels,source_float,source_rgba,source_protected=None,native_alpha=False):
    """Restore only native-unmodified RGB/source protection; optional Body A.

    Masks use image row order. A derived Head C1/alias lock is not a source
    protection mask and must never be passed as source_protected.
    """
    values=np.asarray(pixels);source=np.asarray(source_float);native=np.asarray(source_rgba)
    if values.shape!=source.shape or native.shape!=values.shape or values.ndim!=3 or values.shape[2]!=4 or native.dtype!=np.uint8:raise ValueError('Native Main packing authorities have different layouts/types.')
    if values.dtype!=source.dtype or values.dtype.kind!='f' or values.dtype.itemsize not in(4,8):raise ValueError('Native Main packing needs the same declared float precision.')
    bit_type=np.uint32 if values.dtype.itemsize==4 else np.uint64
    unchanged=np.all(values.view(bit_type)==source.view(bit_type),axis=2)
    protected=np.zeros(unchanged.shape,bool)if source_protected is None else np.asarray(source_protected,bool)
    if protected.shape!=unchanged.shape:raise ValueError('The source-protected Main mask has a different layout.')
    if np.any(protected&~unchanged):raise ValueError('The field changed a source-protected float before native packing.')
    if native_alpha and not np.array_equal(values[:,:,3].view(bit_type),source[:,:,3].view(bit_type)):raise ValueError('A Body Main field changed its native float alpha before packing.')
    restore=unchanged|protected;result=np.array(encoded,copy=True);result[restore,:3]=native[restore,:3]
    if native_alpha:result[:,:,3]=native[:,:,3]
    return result


def body_protection(protected_packets,width,height):
    mask=np.zeros((height,width),bool);rows=[]
    for r,packet in protected_packets:
        source_uv=uv(packet)
        before=int(mask.sum())
        for indices in packet['subMeshes']:
            for tri in np.asarray(indices,int).reshape(-1,3):
                pix=periodic_pixels(source_uv[tri],width,height,pixels_for)
                if pix is not None:mask[pix[0],pix[1]]=True
        rows.append(dict(r,newProtectedTexels=int(mask.sum())-before))
    return mask,dict(otherNativePartConsumers=rows,
        protectedTexels=int(mask.sum()),nailAndOtherPartPixelsLocked=True,sourcePart='Body',
        headGeometryOrTextureDependency=False,bodyOperator=None)

def prepare_body_main(packet,bone_paths,protected_packets,main):
    head=geometry(packet,arr(packet,'vertices',3),unit(arr(packet,'normals',3)),arr(packet,'tangents',4))
    h,w=main.shape[:2];he=np.empty((0,2),int)
    seams,charts,counts,vertex_protected=native_alias_edges(head,bone_paths,protected_vertices=np.zeros(len(head['vertices']),bool))
    protected,protection=body_protection(protected_packets,w,h)
    # Raster once at native resolution. Positions distinguish unrelated UV
    # occupants; no alias of an unrelated component is silently overwritten.
    face_id=np.full((h,w),-1,np.int32);bary_map=np.zeros((h,w,3),np.float32);position_map=np.zeros((h,w,3),np.float32);ambiguous=np.zeros((h,w),bool)
    for ti,tri in enumerate(head['triangles']):
        pix=periodic_pixels(head['uv'][tri],w,h,pixels_for)
        if pix is None:continue
        y,x,bary=pix;pos=bary@head['vertices'][tri];occupied=face_id[y,x]>=0
        different=occupied&(np.linalg.norm(position_map[y,x]-pos,axis=1)>1e-5);ambiguous[y[different],x[different]]=True
        take=~protected[y,x]&~occupied
        y,x,bary,pos=y[take],x[take],bary[take],pos[take];face_id[y,x]=ti;bary_map[y,x]=bary;position_map[y,x]=pos
    raster_occupied=face_id>=0
    zeros=np.zeros((h,w),np.float32);unrelated=np.zeros((h,w),bool)
    _,body_cut=analytic_boundary_stencil(head['uv'],head['vertices'],he,zeros,position_map,protected,unrelated,w,h,apply=False)
    alias_edges=np.asarray([r[side] for r in seams for side in ('a','b')],int)
    _,alias_footprint=analytic_boundary_stencil(head['uv'],head['vertices'],alias_edges,zeros,position_map,protected,unrelated,w,h,apply=False)
    locked=protected|ambiguous|body_cut;envelope,envelope_proof=protected_cut_envelope(locked,alias_footprint)
    # Apply the same analytic raw-edge stencil as the existing collar owner.
    # Only unused gutter taps are filled. Occupied foreign/face/cut pixels stay
    # immutable; the source face supplies the exact query tangent frame.
    gutter=0
    for seam in seams:
        for side in (0,1):
            edge=seam['a'] if side==0 else seam['b'];ua,ub=head['uv'][edge];extent=np.asarray([w,h]);lo=np.floor(np.minimum(ua,ub)*extent-2.5).astype(int);hi=np.ceil(np.maximum(ua,ub)*extent+2.5).astype(int)
            gx,gy=np.meshgrid(np.arange(lo[0],hi[0]+1),np.arange(lo[1],hi[1]+1));grid=np.column_stack((gx.ravel(),gy.ravel()));query=(grid+.5)/extent;delta=(ub-ua)*extent;square=float(np.dot(delta,delta));t=np.clip(np.sum((query-ua)*extent*delta,axis=1)/square,0,1);distance=np.linalg.norm((query-ua-t[:,None]*(ub-ua))*extent,axis=1)
            xx=grid[:,0]%w;yy=h-1-grid[:,1]%h;take=(distance<=1.5)&~locked[yy,xx]&(face_id[yy,xx]<0)
            yy,xx,tt=yy[take],xx[take],t[take];ti=seam['faces'][side];tri=head['triangles'][ti];bary=np.zeros((len(tt),3));bary[:,int(np.nonzero(tri==edge[0])[0][0])]=1-tt;bary[:,int(np.nonzero(tri==edge[1])[0][0])]=tt
            face_id[yy,xx]=ti;bary_map[yy,xx]=bary;position_map[yy,xx]=head['vertices'][edge[0]]*(1-tt[:,None])+head['vertices'][edge[1]]*tt[:,None];gutter+=len(tt)
    y,x=np.nonzero((face_id>=0)&~locked);triangles=np.asarray(head['triangles'],int)[face_id[y,x]];bary=bary_map[y,x].astype(float);positions=position_map[y,x].astype(float)
    # Native raw UV topology is the propagation domain, not position-welded
    # topology across the seam being corrected.
    adj=[{} for _ in head['vertices']]
    for tri in head['triangles']:
        for a,b in zip(tri,np.roll(tri,-1)):
            cost=float(np.linalg.norm(head['vertices'][a]-head['vertices'][b]));adj[a][int(b)]=cost;adj[b][int(a)]=cost
    uv=np.column_stack(((x+.5)/w,1-(y+.5)/h));source_sample_uv=uv.copy();unused_gutter=~raster_occupied[y,x]
    source_sample_uv[unused_gutter]=np.sum(head['uv'][triangles[unused_gutter]]*bary[unused_gutter,:,None],axis=1)
    assert np.array_equal(source_sample_uv[~unused_gutter],uv[~unused_gutter])
    source_main=bilinear_gpu_rows(main,source_sample_uv)
    accum_main=np.zeros((len(y),3));denominator=np.full(len(y),1/.060**2);by_pair=collections.defaultdict(list)
    for seam in seams:by_pair[tuple(sorted(seam['charts']))].append(seam)
    curves=[]
    for chart_pair,rows in by_pair.items():
        for component in split_alias_paths(rows,head):
            for side,own in enumerate(chart_pair):
                curve=consensus_main(AliasCurve(head,component,own,opposite=bool(side)),main);curves.append(curve)
                vd=alias_distances(adj,np.unique(curve.edges));valid=np.isfinite(vd[triangles]);distance=np.sum(np.where(valid,vd[triangles],0)*bary,axis=1);distance[np.any(~valid&(bary>0),axis=1)]=np.inf
                selected=np.nonzero(distance<.060)[0]
                for at in range(0,len(selected),4096):
                    q=selected[at:at+4096];delta,proof=continuous_alias_main(source_sample_uv[q],positions[q],np.ones(len(q)),curve,main)
                    fade=1-np.clip(distance[q]/.060,0,1)**2;kernel=(fade/np.maximum(np.maximum(distance[q],proof['physicalDistance']),1e-6))**2*envelope[y[q],x[q]]
                    accum_main[q]+=delta*kernel[:,None]
                    denominator[q]+=kernel
    result_main=main.copy();result_main[h-1-y,x,:3]=np.clip(source_main[:,:3]+accum_main/denominator[:,None],0,1)
    locked_gpu=locked[::-1]
    assert np.array_equal(result_main[:,:,3],main[:,:,3])and np.array_equal(result_main[locked_gpu],main[locked_gpu])
    return result_main,dict(locked=locked,protected=protected,curves=curves,sourceCounts=counts,protection=protection,envelope=envelope_proof,analyticUnusedGutterTexels=gutter)


def contact_pixel_mask(head,protected_vertices,width,height):
    """Keep native ocular and oral pigment outside physical chart correction."""
    protected=np.asarray(protected_vertices,bool)
    if protected.shape!=(len(head['vertices']),):
        raise ValueError('The pigment contact owner differs from the source mesh.')
    mask=np.zeros((height,width),bool)
    for triangle in head['triangles']:
        if not protected[triangle].any():continue
        samples=periodic_pixels(head['uv'][triangle],width,height,pixels_for)
        if samples is not None:mask[samples[0],samples[1]]=True
    return mask


def apply_head_pigment(head_main,gain):
    gain=np.asarray(gain,float)
    if gain.shape!=(3,)or not np.isfinite(gain).all()or np.any(gain<=0):raise ValueError('The source skin pigment gain is invalid.')
    values=head_main[:,:,:3]*gain;result=head_main.copy();result[:,:,:3]=np.clip(values,0,1)
    return result,dict(gain=gain.tolist(),clippedTexels=int(np.any(values>1,axis=2).sum()),
        sourceAlphaExact=bool(np.array_equal(result[:,:,3],head_main[:,:,3])),positiveMultiplicativePigment=True)


def head_pigment_coefficient(current,aligned,fraction):
    """Match a face's skin reference while retaining its native pigment detail."""
    current=np.asarray(current,np.uint8);aligned=np.asarray(aligned,np.uint8);fraction=np.asarray(fraction,float)
    if current.shape!=aligned.shape or current.ndim!=3 or current.shape[2]!=4 or fraction.shape!=current.shape[:2]:
        raise ValueError('Head pigment references have different mip layouts.')
    source=lookup[current[:,:,:3]];target=lookup[aligned[:,:,:3]]
    eligible=(fraction[::-1]>=1-1e-7)&(source.min(axis=2)>1e-8)
    if not eligible.any():raise ValueError('A head pigment reference has no source-owned face skin.')
    a=source[eligible];z=target[eligible];light=a@[.2126,.7152,.0722];selected=light>=np.median(light)
    weight=np.ones(int(selected.sum()));s=weighted(a[selected],weight);t=weighted(z[selected],weight)
    return dict(v3_tone_parameters(s,t),policy='source-face-pigment-v3',source=s.tolist(),target=t.tolist(),
        pairedSkinSamples=int(selected.sum()),darkFeatureReferenceExcluded=True,sharedAcrossSourceLods=True)


def align_head_main(head_main,body_main,head_boundary,body_boundary):
    """Normalize independent skin pigment against the matching physical cut."""
    edges=np.repeat(np.arange(len(head_boundary.edges)),32)
    amount=np.tile((np.arange(32)+.5)/32,len(head_boundary.edges))
    positions,head_uv,_=head_boundary.evaluate(edges,amount)
    other,parameter,distance=body_boundary.nearest(positions)
    if float(distance.max())>1e-4:raise ValueError('Skin pigment alignment requires the source shared physical boundary.')
    _,body_uv,_=body_boundary.evaluate(other,parameter)
    source=bilinear_gpu_rows(head_main,head_uv)[:,:3];target=bilinear_gpu_rows(body_main,body_uv)[:,:3]
    eligible=np.all((source>1/3294.6)&(target>1/3294.6),axis=1)
    if not eligible.any():raise ValueError('The shared skin cut has no positive pigment samples.')
    weights=head_boundary.length[edges][eligible]/32
    gain=weighted(target[eligible]/source[eligible],weights)
    result,proof=apply_head_pigment(head_main,gain)
    return result,dict(proof,queries=len(edges),eligibleQueries=int(eligible.sum()),maximumBoundaryDistance=float(distance.max()))


def prepare_head_main(row,head_packet,body_packet,family_rows,packets,head_main,body_main,source_epoch,memo=None,*,protected_vertices=None,pigment_alignment=None):
    head,body,he,be,hc,bc,context=source_context(row,head_packet,body_packet)
    memo=RasterMemo() if memo is None else memo
    domain=domain_buffers(row,head_packet,body_packet,family_rows,packets,source_epoch)
    if pigment_alignment is None:pigment,pigment_proof=apply_head_pigment(head_main,[1.,1.,1.])
    else:
        pigment,applied=apply_head_pigment(head_main,pigment_alignment['gain'])
        pigment_proof=dict(pigment_alignment,**{k:v for k,v in applied.items()if k!='gain'},reusedSourcePigmentAlignment=True)
    c1,domain,c1_proof=c1_main(row,head,hc,bc,pigment,body_main,memo,domain)
    height,width=c1.shape[:2]
    contact=domain['protected']if protected_vertices is None else contact_pixel_mask(head,protected_vertices,width,height)
    result,alias_proof=alias_main(row,head,he,c1,dict(protected=contact),memo,protected_vertices=protected_vertices,preserve_local_contrast=True)
    domain.update(mainOwned=np.any(result!=pigment,axis=2)[::-1],mainProtected=contact,pigmentAlignment=pigment_proof)
    return result,domain,dict(context=context,pigmentAlignment=pigment_proof,c1=c1_proof,aliases=alias_proof,normalImageConsumed=False)


def tone_main_mip(raw,weight,coeff):
    rgb=lookup[raw[:,:,:3]];corrected=v3_tone_rgb(rgb,coeff)
    value=rgb+(corrected-rgb)*(1-weight[::-1,:,None]);srgb=np.where(value<=.0031308,value*12.92,1.055*np.power(value,1/2.4)-.055)
    result=np.array(raw,copy=True);result[:,:,:3]=np.rint(srgb*255).astype(np.uint8);w1=weight[::-1]==1;result[w1]=raw[w1]
    assert np.array_equal(result[:,:,3],raw[:,:,3])and np.array_equal(result[w1],raw[w1])
    return result,dict(preToneAlphaExact=True,c1WeightOneRgbaExact=True,w1Texels=int(w1.sum()),finiteRange=True,minimum=float(value.min()),maximum=float(value.max()))


def head_main_mips(field,buffers,originals,sorted_values,sorted_codes):
    pixels0=encode_main_rgba(field,sorted_values,sorted_codes);values=pixels0.astype(float)/255;is_main=True
    values[:,:,:3]=np.where(values[:,:,:3]<=.04045,values[:,:,:3]/12.92,((values[:,:,:3]+.055)/1.055)**2.4)
    owned=buffers.get('mainOwned',buffers['weight']>0)[::-1].copy();protected=buffers.get('mainProtected',buffers['protected'])[::-1].copy()
    for level,original in enumerate(originals):
        if level:values=downsample(values).mean(axis=(1,3));owned=downsample(owned).any(axis=(1,3));protected=downsample(protected).any(axis=(1,3))
        pixels=values.copy()
        if is_main:pixels[:,:,:3]=np.where(pixels[:,:,:3]<=.0031308,pixels[:,:,:3]*12.92,1.055*np.power(np.maximum(pixels[:,:,:3],0),1/2.4)-.055)
        encoded=np.clip(np.rint(pixels*255),0,255).astype(np.uint8);mask=owned&~protected;baseline=original
        if 'pigmentAlignment'in buffers:
            linear,_=apply_head_pigment(linear_rgba(original),buffers['pigmentAlignment']['gain'])
            rgb=linear[:,:,:3];linear[:,:,:3]=np.where(rgb<=.0031308,rgb*12.92,1.055*rgb**(1/2.4)-.055)
            baseline=np.rint(np.clip(linear,0,1)*255).astype(np.uint8);baseline[:,:,3]=original[:,:,3]
        result=baseline.copy();result[mask]=encoded[mask]
        assert np.array_equal(result[~mask],baseline[~mask])
        yield result,owned.copy(),protected.copy()


def body_main_mips(candidate,locked,originals):
    native0=originals[0];protect=locked[::-1].copy()
    assert np.array_equal(candidate[protect],native0[protect])and np.array_equal(candidate[:,:,3],native0[:,:,3])
    delta=main_linear(candidate)[:,:,:3]-main_linear(native0)[:,:,:3];owned=np.any(candidate!=native0,axis=2)&~protect
    for level,original in enumerate(originals):
        if level:delta=down(delta).mean(axis=(1,3));owned=down(owned).any(axis=(1,3));protect=down(protect).any(axis=(1,3))
        mask=owned&~protect;values=original.astype(float)/255;rgb=np.clip(main_linear(original)[:,:,:3]+delta,0,1);values[:,:,:3]=np.where(rgb<=.0031308,rgb*12.92,1.055*np.maximum(rgb,0)**(1/2.4)-.055)
        encoded=np.clip(np.rint(values*255),0,255).astype(np.uint8);result=original.copy();result[mask]=encoded[mask];result[:,:,3]=original[:,:,3]
        assert np.array_equal(result[:,:,3],original[:,:,3])and np.array_equal(result[~mask],original[~mask])and np.array_equal(result[protect],original[protect])
        if not level:assert np.array_equal(result,candidate),'The established delta-mip roundtrip changed frozen mip0.'
        yield result,owned.copy(),protect.copy()

def v3_tone_parameters(source,target):
    """The accepted V3 coefficient law, shared by uniform and regional tone."""
    source=np.asarray(source);target=np.asarray(target)
    assert np.isfinite(source).all()and np.isfinite(target).all()and np.all((source>0)&(source<1))and np.all((target>0)&(target<1))
    gain=target/source;E=float(max(1,gain.max()));wb=gain/E;mr=float(target.max()/E);k=float(target.max()*(1-mr)/(mr*(1-target.max())))if gain.max()>1 else 1.
    assert np.isfinite(gain).all()and np.isfinite(k)and k>0
    return dict(gain=gain.tolist(),E=E,whiteBalance=wb.tolist(),k=k,coupled=bool(gain.max()>1))


def v3_tone_rgb(rgb,coeff):
    """Exact accepted V3 operation order; no alternate regional RGB formula."""
    gain=np.asarray(coeff['gain']);wb=np.asarray(coeff['whiteBalance']);k=coeff['k']
    v=rgb*wb if coeff['coupled']else rgb*gain
    corrected=v*(k/((1-v.max(axis=-1))+k*v.max(axis=-1)))[:,:,None]if coeff['coupled']else v
    assert np.isfinite(corrected).all()and corrected.min()>=0 and corrected.max()<=1
    return corrected


def regional_bone_fraction(packet,bone_paths):
    """Semantic Head descendant mass; normalization does not change skinning."""
    weights=arr(packet,'weights',4).astype(float);indices=arr(packet,'boneIndices',4).astype(int)
    flags=np.asarray(['Bip001 Head'in path.split('/')for path in bone_paths],bool)
    if not flags.any()or np.any(indices<0)or np.any(indices>=len(flags))or not np.isfinite(weights).all()or np.any(weights<0):
        raise ValueError('Regional tone needs declared finite native Head/descendant skin weights.')
    total=weights.sum(axis=1)
    if np.any(total<=0):raise ValueError('Regional tone cannot infer a region from a zero native skin-weight sum.')
    fraction=(weights*flags[indices]).sum(axis=1)/total
    assert np.all((fraction>=0)&(fraction<=1))
    return fraction


def regional_head_source(row,head_packet,body_packet):
    head,body,*_=source_context(row,head_packet,body_packet)
    paths=list(row['headNativeBonePaths']);joint=row['headSource'].get('joint')
    if joint:paths.extend(row['bodyNativeBonePaths'][i]for i in joint.get('donorBones',[]))
    fraction=regional_bone_fraction(head['packet'],paths)
    if len(fraction)!=len(head['vertices']):raise ValueError('Regional tone and actual Head UV topology disagree.')
    # Only numerically identical positions are aliases here. A different skin
    # region at that same physical point cannot silently choose a UV-side mask.
    _,inverse=np.unique(head['vertices'],axis=0,return_inverse=True)
    lo=np.full(inverse.max()+1,np.inf);hi=np.full_like(lo,-np.inf)
    np.minimum.at(lo,inverse,fraction);np.maximum.at(hi,inverse,fraction)
    rounding=float(8*np.finfo(np.float32).eps)
    if np.any(hi-lo>rounding):raise ValueError('Physical Head aliases disagree on semantic Head mass; regional tone is ambiguous.')
    return head,fraction,dict(region='sum(Bip001 Head and descendants)/sum(native skin weights)',maximumPhysicalAliasMassDifference=float((hi-lo).max()),roundingBound=rounding,nativeSkinningChanged=False)


def regional_head_roi(row,head_packet,body_packet,existing_roi):
    """All Head front surface; the old positive-Head face ROI is insufficient."""
    head,fraction,proof=regional_head_source(row,head_packet,body_packet)
    front=np.asarray(existing_roi['sourceFront'],float)
    if front.shape!=(3,)or not np.isfinite(front).all()or abs(np.linalg.norm(front)-1)>1e-6:raise ValueError('The pinned source-specific physical front is invalid.')
    tri=head['triangles'];vertices=head['vertices'][tri]
    area=np.linalg.norm(np.cross(vertices[:,1]-vertices[:,0],vertices[:,2]-vertices[:,0]),axis=1)/2
    area*=np.maximum(0,np.mean(head['normal'][tri],axis=1)@front)
    bary=np.asarray([[1/3,1/3,1/3],[.6,.2,.2],[.2,.6,.2],[.2,.2,.6]])
    weights=np.repeat(area/4,4);mass=np.einsum('kj,tj->tk',bary,fraction[tri]).reshape(-1)
    if not np.any(weights*mass>0)or not np.any(weights*(1-mass)>0):raise ValueError('Both semantic regions need measured positive physical area; no regional fallback is declared.')
    roi=dict(headUv=np.einsum('kj,tjd->tkd',bary,head['uv'][tri]).reshape(-1,2),headWeight=weights,headFraction=mass,
        bodyUv=np.asarray(existing_roi['bodyUv']),bodyWeight=np.asarray(existing_roi['bodyWeight']),sourceFront=front,headTriangles=tri)
    proof.update(roiRule='All source-current Head triangles; exact existing four bary points, physical area*front cosine, regional weights H and 1-H; Body ROI unchanged',headSamples=len(weights),headFractionArea=float(np.sum(weights*mass)),complementArea=float(np.sum(weights*(1-mass))),sourceFrameReused=True)
    return roi,proof


def regional_coefficient(row,preencoded,body,roi_record,roi):
    if 'headFraction'not in roi:raise ValueError('A legacy positive-Head ROI cannot calibrate regional tone.')
    pixels=linear_rgba(preencoded);head=bilinear_gpu_rows(pixels,roi['headUv'])
    target,target_proof=reference(bilinear_gpu_rows(body,roi['bodyUv']),roi['bodyWeight'])
    regions={}
    for name,mass in(('face',roi['headFraction']),('neck',1-roi['headFraction'])):
        weights=roi['headWeight']*mass;selected=weights>0
        if not selected.any():raise ValueError('A regional coefficient has no physical source area.')
        source,source_proof=reference(head[selected],weights[selected])
        regions[name]=dict(s=source.tolist(),t=target.tolist(),**v3_tone_parameters(source,target),headReference=source_proof,physicalArea=float(weights.sum()))
    return dict(policy='regional-head-bone-v1',sourceInputKey=row['samplerSourceInputKey'],calibratedLod=0,sharedAcrossSourceLodsByExplicitPolicy=True,
        regions=regions,bodyTarget=target.tolist(),bodyReference=target_proof,roi=roi_record,regionBlend='H*acceptedV3(face)+(1-H)*acceptedV3(neck)',nativeRuntimePigmentChanged=False)


def regional_fraction_atlas(row,head_packet,body_packet,width,height,memo=None):
    """Actual UV occupants first, then the existing 1.5-texel chart gutter law."""
    head,fraction,proof=regional_head_source(row,head_packet,body_packet)
    return regional_fraction_surface(head,fraction,width,height,memo,proof)


def regional_fraction_surface(head,fraction,width,height,memo=None,source_proof=None):
    """Rasterize source-owned anatomical weights on the actual saved surface."""
    return regional_fraction_uv(head['packet'],fraction,width,height,memo,source_proof)


def regional_fraction_uv(packet,fraction,width,height,memo=None,source_proof=None,*,overlap='reject'):
    """Use the native UV and skin-weight owner without requiring spatial fitting."""
    if overlap not in('reject','protect-complement'):raise ValueError('Unknown anatomical UV overlap policy.')
    overlap_policy=overlap
    fraction=np.asarray(fraction,float)
    if fraction.shape!=(packet['vertexCount'],)or not np.isfinite(fraction).all()or np.any((fraction<0)|(fraction>1)):
        raise ValueError('The anatomical region must own one finite fraction per saved vertex.')
    source_uv=uv(packet);triangles=np.asarray(packet['subMeshes'][0],int).reshape(-1,3);edges=raw_edges(triangles)
    proof=dict(source_proof or {})
    memo=RasterMemo()if memo is None else memo
    values=np.full((height,width),np.nan);occupied=np.zeros((height,width),bool);rounding=8*np.finfo(np.float32).eps;maximum_overlap=0.
    for tri in triangles:
        pix=memo.call(source_uv[tri],width,height,pixels_for)
        if pix is None:continue
        y,x,bary=pix;mass=bary@fraction[tri];overlap=occupied[y,x]
        if overlap.any():
            difference=np.abs(values[y[overlap],x[overlap]]-mass[overlap]);maximum_overlap=max(maximum_overlap,float(difference.max()))
            if np.any(difference>rounding)and overlap_policy=='reject':raise ValueError('UV-overlapping physical regions require different tone weights; no first-chart choice is allowed.')
            if overlap_policy=='protect-complement':values[y[overlap],x[overlap]]=np.minimum(values[y[overlap],x[overlap]],mass[overlap])
        take=~overlap;values[y[take],x[take]]=mass[take];occupied[y,x]=True
    distance=np.full((height,width),np.inf);gutter=np.zeros_like(occupied)
    for edge,faces in edges.items():
        if len(faces)!=1:continue
        ua,ub=source_uv[list(edge)];extent=np.asarray([width,height]);lo=np.floor(np.minimum(ua,ub)*extent-2.5).astype(int);hi=np.ceil(np.maximum(ua,ub)*extent+2.5).astype(int)
        gx,gy=np.meshgrid(np.arange(lo[0],hi[0]+1),np.arange(lo[1],hi[1]+1));grid=np.column_stack((gx.ravel(),gy.ravel()));query=(grid+.5)/extent;delta=(ub-ua)*extent;square=float(delta@delta)
        if square==0:continue
        t=np.clip(np.sum((query-ua)*extent*delta,axis=1)/square,0,1);d=np.linalg.norm((query-ua-t[:,None]*(ub-ua))*extent,axis=1);x=grid[:,0]%width;y=height-1-grid[:,1]%height;mass=(1-t)*fraction[edge[0]]+t*fraction[edge[1]]
        valid=(d<=1.5)&~occupied[y,x];tie=valid&(d==distance[y,x])&np.isfinite(values[y,x])
        if np.any(np.abs(values[y[tie],x[tie]]-mass[tie])>rounding)and overlap_policy=='reject':raise ValueError('Equidistant native UV gutters disagree on the regional owner.')
        if overlap_policy=='protect-complement':values[y[tie],x[tie]]=np.minimum(values[y[tie],x[tie]],mass[tie])
        take=valid&(d<distance[y,x]);values[y[take],x[take]]=mass[take];distance[y[take],x[take]]=d[take];gutter[y[take],x[take]]=True
    known=np.isfinite(values);values[known]=np.clip(values[known],0,1)
    proof.update(occupiedTexels=int(occupied.sum()),gutterTexels=int(gutter.sum()),unmappedTexelsPreserved=int((~known).sum()),maximumUvOverlapMassDifference=maximum_overlap,gutterRadiusTexels=1.5,unownedRgbPreserved=True)
    if overlap_policy!='reject':proof['overlapPolicy']=overlap_policy
    return values,proof


def regional_fraction_mip(values):
    known=np.isfinite(values);mass=downsample(np.where(known,values,0)).sum(axis=(1,3));count=downsample(known).sum(axis=(1,3));result=np.full(count.shape,np.nan);np.divide(mass,count,out=result,where=count>0)
    return result


def compose_head_material_mip(face,authored,fraction,*,replace_alpha=False):
    """Combine a source-owned face with the current collar in linear RGB."""
    native=np.asarray(face,np.uint8);authored=np.asarray(authored,np.uint8);fraction=np.asarray(fraction,float)
    if native.shape!=authored.shape or native.ndim!=3 or native.shape[2]!=4 or fraction.shape!=native.shape[:2]:
        raise ValueError('Native pigment and anatomical ownership have different mip layouts.')
    if np.isinf(fraction).any():raise ValueError('Anatomical pigment ownership cannot be infinite.')
    known=np.isfinite(fraction);mass=np.where(known,fraction,0)[::-1]
    if np.any((mass<0)|(mass>1)):raise ValueError('Anatomical pigment ownership must stay in the unit interval.')
    source=lookup[native[:,:,:3]];current=lookup[authored[:,:,:3]]
    value=current+(source-current)*mass[:,:,None]
    encoded=np.where(value<=.0031308,value*12.92,1.055*np.maximum(value,0)**(1/2.4)-.055)
    result=authored.copy();result[:,:,:3]=np.rint(np.clip(encoded,0,1)*255).astype(np.uint8)
    if replace_alpha:result[:,:,3]=np.rint(authored[:,:,3].astype(float)+(native[:,:,3].astype(float)-authored[:,:,3])*mass).astype(np.uint8)
    face=mass==1;neck=mass==0;result[face,:3]=native[face,:3];result[neck]=authored[neck]
    if replace_alpha:result[face]=native[face]
    if not replace_alpha and not np.array_equal(result[:,:,3],authored[:,:,3]):raise ValueError('Face composition changed material alpha.')
    return result,dict(faceSourceRgbExact=True,faceSourceRgbaExact=bool(replace_alpha),collarRgbaExact=True,alphaExact=bool(np.array_equal(result[:,:,3],authored[:,:,3])),
        faceTexels=int(face.sum()),collarTexels=int(neck.sum()),transitionTexels=int((~face&~neck).sum()),anatomicalWeightSha256=digest(fraction))


def preserve_head_pigment_mip(native,authored,fraction,*,native_alpha=False):
    """Restore original pigment for native-reference controls and old receipts."""
    result,proof=compose_head_material_mip(native,authored,fraction,replace_alpha=native_alpha)
    return result,dict(nativeFaceRgbExact=proof['faceSourceRgbExact'],nativeFaceRgbaExact=proof['faceSourceRgbaExact'],
        calibratedNeckRgbaExact=proof['collarRgbaExact'],alphaExact=proof['alphaExact'],nativeFaceTexels=proof['faceTexels'],
        calibratedNeckTexels=proof['collarTexels'],transitionTexels=proof['transitionTexels'],anatomicalWeightSha256=proof['anatomicalWeightSha256'])


def native_face_bc3_mip(native_payload,native_format,target,fraction):
    """Retain whole native face blocks; compress the calibrated skin in DXT5."""
    import etcpak
    from PIL import Image
    target=np.asarray(target,np.uint8);fraction=np.asarray(fraction,float)
    if target.ndim!=3 or target.shape[2]!=4 or fraction.shape!=target.shape[:2]or native_format not in(10,12):
        raise ValueError('Native face compression requires one complete DXT skin mip and its anatomical owner.')
    height,width=target.shape[:2];rows,columns=(height+3)//4,(width+3)//4
    if len(native_payload)!=rows*columns*(8 if native_format==10 else 16):raise ValueError('The original native skin mip is truncated.')
    mass=np.where(np.isfinite(fraction),fraction,0)[::-1]
    if np.isinf(fraction).any()or np.any((mass<0)|(mass>1)):raise ValueError('Native compression has invalid anatomical ownership.')
    face=np.pad(mass>=1-1e-7,((0,(-height)%4),(0,(-width)%4)),mode='edge').reshape(rows,4,columns,4).all(axis=(1,3))
    padded=np.pad(target,((0,(-height)%4),(0,(-width)%4),(0,0)),mode='edge')
    payload=bytearray(etcpak.compress_bc3(padded.tobytes(),columns*4,rows*4))
    blocks=np.frombuffer(payload,np.uint8).reshape(rows,columns,16)
    if native_format==12:
        native_blocks=np.frombuffer(native_payload,np.uint8).reshape(rows,columns,16);blocks[face]=native_blocks[face]
        if not np.array_equal(blocks[face],native_blocks[face]):raise ValueError('A native face DXT5 block changed.')
    decoded=np.asarray(Image.frombytes('RGBA',(width,height),bytes(payload),'bcn',3))
    error=decoded.astype(float)-target
    return bytes(payload),face,dict(format=12,bytes=len(payload),nativeSourceFormat=native_format,nativeFaceBlocksExact=int(face.sum())if native_format==12 else 0,
        nativeFaceBlockOwnershipSha256=digest(face),rgbRms=float(np.sqrt(np.mean(error[:,:,:3]**2))),maximumRgbError=int(np.abs(error[:,:,:3]).max()),
        alphaRms=float(np.sqrt(np.mean(error[:,:,3]**2))),maximumAlphaError=int(np.abs(error[:,:,3]).max()),sourceDimensionsAndMipCountRetained=True)


def bc7_rgba_mode4_blocks(target):
    return bc7_rgba_separate_blocks(target,4)


def bc7_rgba_separate_blocks(target,mode):
    """BC7 modes4/5 with separate RGB and material-alpha index streams."""
    if mode not in(4,5):raise ValueError("An independent RGBA codec must use BC7 mode4 or mode5.")
    rgb_bits,alpha_bits=(5,6)if mode==4 else(7,8)
    rgb_limit=(1<<rgb_bits)-1;alpha_limit=(1<<alpha_bits)-1
    target=np.asarray(target,np.uint8)
    if target.ndim!=3 or target.shape[1:]!=(16,4):raise ValueError('Separate BC7 streams require complete 4x4 RGBA blocks.')
    count=len(target);source=target.astype(float);rgb=source[:,:,:3];axis=np.ptp(rgb,axis=1).argmax(axis=1)
    projection=np.take_along_axis(rgb,axis[:,None,None],axis=2)[:,:,0];batch=np.arange(count)
    endpoints=np.stack((rgb[batch,projection.argmin(axis=1)],rgb[batch,projection.argmax(axis=1)]),axis=1)
    codes=np.rint(endpoints*rgb_limit/255).astype(np.uint64)
    constant=codes[:,0]==codes[:,1];mean=rgb.mean(axis=1)*rgb_limit/255
    codes[:,0]=np.where(constant,np.floor(mean),codes[:,0]).astype(np.uint64)
    codes[:,1]=np.where(constant,np.ceil(mean),codes[:,1]).astype(np.uint64)
    expanded=((codes<<3)|(codes>>2)).astype(np.int32)if mode==4 else((codes<<1)|(codes>>6)).astype(np.int32);weights=np.array([0,21,43,64])
    palette=((64-weights[None,:,None])*expanded[:,0,None]+weights[None,:,None]*expanded[:,1,None]+32)>>6
    color_indices=np.sum((rgb[:,:,None]-palette[:,None])**2,axis=3).argmin(axis=2).astype(np.uint64)
    alpha=source[:,:,3];alpha_ends=np.stack((alpha.min(axis=1),alpha.max(axis=1)),axis=1)
    alpha_codes=np.rint(alpha_ends*alpha_limit/255).astype(np.uint64);same=alpha_codes[:,0]==alpha_codes[:,1];mid=alpha.mean(axis=1)*alpha_limit/255
    alpha_codes[:,0]=np.where(same,np.floor(mid),alpha_codes[:,0]).astype(np.uint64);alpha_codes[:,1]=np.where(same,np.ceil(mid),alpha_codes[:,1]).astype(np.uint64)
    alpha_values=((alpha_codes<<2)|(alpha_codes>>4)).astype(np.int32)if mode==4 else alpha_codes.astype(np.int32);aw=np.array([0,9,18,27,37,46,55,64])if mode==4 else weights
    alpha_palette=((64-aw[None,:])*alpha_values[:,0,None]+aw[None,:]*alpha_values[:,1,None]+32)>>6
    alpha_indices=np.abs(alpha[:,:,None]-alpha_palette[:,None]).argmin(axis=2).astype(np.uint64)
    color_swap=color_indices[:,0]>=2;codes[color_swap]=codes[color_swap,::-1];color_indices[color_swap]=3-color_indices[color_swap]
    alpha_max=7 if mode==4 else 3
    alpha_swap=alpha_indices[:,0]>=(4 if mode==4 else 2);alpha_codes[alpha_swap]=alpha_codes[alpha_swap,::-1];alpha_indices[alpha_swap]=alpha_max-alpha_indices[alpha_swap]
    low=np.zeros(count,np.uint64);high=np.zeros_like(low);position=0
    def put(value,width):
        nonlocal position,low,high
        value=np.asarray(value,np.uint64)
        if position<64:
            low|=value<<np.uint64(position)
            if position+width>64:high|=value>>np.uint64(64-position)
        else:high|=value<<np.uint64(position-64)
        position+=width
    put(1<<mode,mode+1);put(0,2)
    if mode==4:put(0,1)
    for channel in range(3):
        for endpoint in range(2):put(codes[:,endpoint,channel],rgb_bits)
    for endpoint in range(2):put(alpha_codes[:,endpoint],alpha_bits)
    for index in range(16):put(color_indices[:,index],1 if index==0 else 2)
    for index in range(16):put(alpha_indices[:,index],(2 if index==0 else 3)if mode==4 else(1 if index==0 else 2))
    if position!=128:raise ValueError('The independent BC7 bit allocation differs.')
    return np.stack((low,high),axis=1).astype('<u8').view(np.uint8).reshape(count,16)


def native_face_bc7_mip(calibrated_payload,target,fraction,*,pigment_policy='native',_alpha_weight=1):
    """Encode the face while retaining every calibrated BC7 collar block."""
    import etcpak
    from PIL import Image
    if pigment_policy not in ('native','calibrated'):raise ValueError('Unknown face pigment codec policy.')
    if _alpha_weight not in (1,3):raise ValueError('Unknown face material-alpha search weight.')
    target=np.asarray(target);fraction=np.asarray(fraction,float)
    if target.dtype!=np.uint8 or target.ndim!=3 or target.shape[2]!=4 or target.shape[:2]!=fraction.shape:
        raise ValueError('Native face BC7 requires matching RGBA8 and anatomical ownership.')
    height,width=target.shape[:2];rows,columns=(height+3)//4,(width+3)//4
    mass=np.where(np.isfinite(fraction),fraction,0)[::-1]
    if np.isinf(fraction).any()or np.any((mass<0)|(mass>1)):raise ValueError('Native BC7 face ownership is outside the unit interval.')
    padded_mass=np.pad(mass,((0,(-height)%4),(0,(-width)%4)),mode='edge').reshape(rows,4,columns,4)
    face=(padded_mass>=1-1e-7).all(axis=(1,3));calibrated=(padded_mass==0).all(axis=(1,3))
    if calibrated_payload is not None:
        if len(calibrated_payload)!=rows*columns*16:raise ValueError('A calibrated BC7 source mip is truncated.')
        payload=bytearray(calibrated_payload)
    else:payload=bytearray(rows*columns*16);calibrated[:]=False
    if pigment_policy=='calibrated':
        if calibrated_payload is None:raise ValueError('Calibrated pigment requires the current material-alpha source.')
        editable=np.repeat(np.repeat(~calibrated,4,axis=0),4,axis=1)[:height,:width]
        encoded,fit=fit_bc7_rgb_field(calibrated_payload,width,height,target,editable)
        selected=np.repeat(np.repeat(face,4,axis=0),4,axis=1)[:height,:width]
        def rgb_error(value):
            decoded=np.asarray(Image.frombytes('RGBA',(width,height),value,'bcn',7))
            difference=(decoded[:,:,:3].astype(float)-target[:,:,:3])[selected]
            return dict(maximum=int(np.abs(difference).max(initial=0)),rms=float(np.sqrt(np.mean(difference**2)))if difference.size else 0.,
                p999=float(np.percentile(abs(difference),99.9))if difference.size else 0.,p9999=float(np.percentile(abs(difference),99.99))if difference.size else 0.)
        rgb=rgb_error(encoded)
        if rgb['rms']>3 and width*height<=4096:
            encoded,fit=fit_bc7_rgb_field(calibrated_payload,width,height,target,editable,maximum_rgb_error=16,allow_reencoding=True)
            rgb=rgb_error(encoded)
        if rgb['rms']<=3:
            return encoded,face,calibrated,dict(format=25,bytes=len(encoded),nativeFaceRgb=rgb,
                nativeFaceAlpha=dict(maximum=0,rms=0.,p999=0.,p9999=0.),calibratedBlocksExact=int(calibrated.sum()),
                nativeFaceBlocks=int(face.sum()),independentRgbAlphaBlocks=fit['reencodedRgbBlocks'],
                sourceDimensionsAndMipCountRetained=True,exactCurrentAlpha=True,endpointFit=fit)
    blocks=np.frombuffer(payload,np.uint8).reshape(rows,columns,16);selected=~calibrated;count=int(selected.sum())
    if count:
        padded=np.pad(target,((0,(-height)%4),(0,(-width)%4),(0,0)),mode='edge')
        tiles=padded.reshape(rows,4,columns,4,4).transpose(0,2,1,3,4)[selected]
        strip=tiles.transpose(1,0,2,3).reshape(4,count*4,4)
        settings=etcpak.BC7CompressBlockParams();settings.init_linear_weights();settings.m_uber_level=4
        if pigment_policy=='calibrated':settings.m_weights=[1,1,1,_alpha_weight]
        blocks[selected]=np.frombuffer(etcpak.compress_bc7(strip.tobytes(),count*4,4,settings),np.uint8).reshape(count,16)
    # Native DXT5 pigment has independent RGB and material-alpha indices.
    # The fast encoder's coupled modes can discard alpha to improve RGB.
    # Compare an independent mode4 palette on face blocks, using both budgets.
    face_indices=np.flatnonzero(face.ravel());chosen=0
    padded=np.pad(target,((0,(-height)%4),(0,(-width)%4),(0,0)),mode='edge')
    targets=padded.reshape(rows,4,columns,4,4).transpose(0,2,1,3,4).reshape(-1,16,4);flat_blocks=blocks.reshape(-1,16)
    def decode_tiles(data):
        n=len(data);strip=np.asarray(data,np.uint8).reshape(n,16).tobytes()
        return np.asarray(Image.frombytes('RGBA',(n*4,4),strip,'bcn',7)).reshape(4,n,4,4).transpose(1,0,2,3).reshape(n,16,4)
    for start in range(0,len(face_indices),4096):
        indices=face_indices[start:start+4096];desired=targets[indices];candidate=bc7_rgba_mode4_blocks(desired)
        before=decode_tiles(flat_blocks[indices]).astype(float)-desired;after=decode_tiles(candidate).astype(float)-desired
        old_max=np.abs(before).max(axis=(1,2));new_max=np.abs(after).max(axis=(1,2))
        color_budget=9 if pigment_policy=='calibrated'else 1
        alpha_budget=4 if pigment_policy=='calibrated'else 1
        old_error=np.maximum(np.mean(before[:,:,:3]**2,axis=(1,2))/color_budget,np.mean(before[:,:,3]**2,axis=1)/alpha_budget)
        new_error=np.maximum(np.mean(after[:,:,:3]**2,axis=(1,2))/color_budget,np.mean(after[:,:,3]**2,axis=1)/alpha_budget)
        balanced=(new_error<old_error)|((new_error==old_error)&(new_max<old_max))
        better=np.where((old_max>16)!=(new_max>16),new_max<=16,balanced)if pigment_policy=='native'else balanced
        flat_blocks[indices[better]]=candidate[better];chosen+=int(better.sum())
    if calibrated_payload is not None and not np.array_equal(blocks[calibrated],np.frombuffer(calibrated_payload,np.uint8).reshape(rows,columns,16)[calibrated]):raise ValueError('A calibrated BC7 block changed.')
    decoded=np.asarray(Image.frombytes('RGBA',(width,height),bytes(payload),'bcn',7))
    face_pixels=np.repeat(np.repeat(face,4,axis=0),4,axis=1)[:height,:width]
    difference=decoded.astype(float)-target
    def error(channels):
        values=difference[:,:,channels][face_pixels]
        return dict(maximum=int(np.abs(values).max(initial=0)),rms=float(np.sqrt(np.mean(values**2)))if values.size else 0.,
            p999=float(np.percentile(np.abs(values),99.9))if values.size else 0.,p9999=float(np.percentile(np.abs(values),99.99))if values.size else 0.)
    rgb=error(slice(0,3));alpha=error(slice(3,4))
    if pigment_policy=='calibrated'and _alpha_weight==1 and(rgb['rms']>3 or alpha['rms']>2):
        return native_face_bc7_mip(calibrated_payload,target,fraction,pigment_policy=pigment_policy,_alpha_weight=3)
    # Independently authored RGB/alpha do not share the native DXT palettes.
    # Bound calibrated diffuse quantization to 3/255 RMS and material alpha to
    # 2/255 RMS. Original-reference RGB keeps its existing 2/255 + max16 law.
    # Percentiles and maxima remain available for every mip's visual review.
    if rgb['rms']>(3 if pigment_policy=='calibrated'else 2)or alpha['rms']>2 or(pigment_policy=='native'and max(rgb['maximum'],alpha['maximum'])>16):
        raise ValueError('Native BC7 face pigment exceeds its RGB/alpha preservation budget: '+str((rgb,alpha)))
    return bytes(payload),face,calibrated,dict(format=25,bytes=len(payload),nativeFaceRgb=rgb,nativeFaceAlpha=alpha,
        calibratedBlocksExact=int(calibrated.sum()),nativeFaceBlocks=int(face.sum()),independentRgbAlphaBlocks=chosen,sourceDimensionsAndMipCountRetained=True)


def regional_tone_main_mip(raw,weight,coeff,fraction):
    if coeff.get('policy')!='regional-head-bone-v1'or fraction.shape!=weight.shape:raise ValueError('Regional tone policy/UV field is missing or incompatible.')
    rgb=lookup[raw[:,:,:3]];mass=fraction[::-1];known=np.isfinite(mass);h=np.where(known,mass,0)
    corrected=[v3_tone_rgb(rgb,coeff['regions'][name])for name in('face','neck')]
    coupled=corrected[0]*h[:,:,None]+corrected[1]*(1-h[:,:,None]);value=rgb+(coupled-rgb)*(1-weight[::-1,:,None]);value[~known]=rgb[~known]
    if not np.isfinite(value).all()or value.min()<0 or value.max()>1:raise ValueError('Regional V3 RGB mixture escaped finite UNorm domain.')
    srgb=np.where(value<=.0031308,value*12.92,1.055*np.power(value,1/2.4)-.055);result=np.array(raw,copy=True);result[:,:,:3]=np.rint(srgb*255).astype(np.uint8);w1=weight[::-1]==1;result[w1]=raw[w1]
    assert np.array_equal(result[:,:,3],raw[:,:,3])and np.array_equal(result[w1],raw[w1])and np.array_equal(result[~known],raw[~known])
    return result,dict(policy=coeff['policy'],preToneAlphaExact=True,c1WeightOneRgbaExact=True,w1Texels=int(w1.sum()),
        knownRegionTexels=int(known.sum()),unmappedRegionTexels=int((~known).sum()),actuallyChangedTexels=int(np.count_nonzero(np.any(result!=raw,axis=2))),
        unmappedRawRgbaExact=True,unmappedRawReference='Exact pre-tone C1/alias authored RGBA; not an original-native or GPU equality claim',unmappedFreshGpuExactClaimed=False,
        finiteRange=True,minimum=float(value.min()),maximum=float(value.max()),nativeRuntimePigmentChanged=False)




def surface_tone_references(head,linear_main,target,sigma=.030,reference_eligibility=None):
    """Calibrate local skin references in source metres, across physical UV aliases."""
    target=np.asarray(target,float)
    if target.shape!=(3,)or not np.all((target>0)&(target<1))or not np.isfinite(target).all()or sigma<=0:
        raise ValueError('Surface tone needs a finite Body skin target and positive source radius.')
    positions,groups=np.unique(np.asarray(head['vertices']),axis=0,return_inverse=True)
    count=len(positions);triangles=np.asarray(head['triangles'],int);physical=groups[triangles]
    samples=bilinear_gpu_rows(linear_main,head['uv'])[:,:3]
    colors=np.zeros((count,3));aliases=np.zeros(count)
    np.add.at(colors,groups,samples);np.add.at(aliases,groups,1)
    colors/=aliases[:,None]
    corners=np.asarray(head['vertices'])[triangles]
    areas=np.linalg.norm(np.cross(corners[:,1]-corners[:,0],corners[:,2]-corners[:,0]),axis=1)/2
    quadrature=np.asarray([[1/3,1/3,1/3],[.6,.2,.2],[.2,.6,.2],[.2,.2,.6]])
    sample_positions=np.einsum('kj,tjd->tkd',quadrature,corners).reshape(-1,3)
    sample_uv=np.einsum('kj,tjd->tkd',quadrature,np.asarray(head['uv'])[triangles]).reshape(-1,2)
    sample_rgb=bilinear_gpu_rows(linear_main,sample_uv)[:,:3];sample_area=np.repeat(areas/4,4)
    explicit_skin=np.zeros(len(sample_rgb),bool)
    if reference_eligibility is not None:
        ownership=np.asarray(reference_eligibility,float)
        if ownership.shape!=(len(head['vertices']),)or not np.isfinite(ownership).all()or np.any((ownership<0)|(ownership>1)):
            raise ValueError('Source skin-reference eligibility must own one finite weight per vertex.')
        explicit_skin=(quadrature@ownership[triangles].T).T.reshape(-1)>=.5
    sample_corners=np.repeat(physical,4,axis=0)
    corner_distance=np.linalg.norm(positions[sample_corners]-sample_positions[:,None,:],axis=2)
    eligible=np.isfinite(sample_rgb).all(axis=1)&np.all((sample_rgb>0)&(sample_rgb<=1),axis=1)&(sample_area>0)
    if not eligible.any():raise ValueError('No positive source skin reference samples.')
    # Eye sockets, nostrils and oral surfaces carry deliberate dark pigment.
    # A local median confined to those surfaces would normalize that pigment
    # into skin colour. Explicit source skin weights can admit darker skin
    # regions; other samples retain the brighter-half reference filter.
    luminance=sample_rgb@np.asarray([.2126,.7152,.0722])
    skin_threshold=weighted(luminance[eligible,None],sample_area[eligible],.5)[0]
    eligible&=(luminance>=skin_threshold)|explicit_skin
    global_reference,_=reference(sample_rgb[eligible],sample_area[eligible])
    fallback_count=0
    area=np.zeros(count);adj: list[dict[int,float]]=[{}for _ in positions]
    for side in range(3):np.add.at(area,physical[:,side],areas/3)
    for triangle in physical:
        for first,second in zip(triangle,np.roll(triangle,-1)):
            a,b=int(first),int(second)
            if a==b:continue
            distance=float(np.linalg.norm(positions[a]-positions[b]));adj[a][int(b)]=distance;adj[b][int(a)]=distance
    local=colors.copy();minimum_neighbors=count;maximum_neighbors=0
    consumed=area>0
    for raw_index in np.nonzero(consumed)[0]:
        index=int(raw_index);distances: dict[int,float]={index:0.};queue: list[tuple[float,int]]=[(0.,index)]
        while queue:
            distance,a=heapq.heappop(queue)
            if distance!=distances[a]:continue
            for b,length in adj[a].items():
                candidate=distance+length
                if candidate<=3*sigma and candidate<distances.get(b,np.inf):distances[b]=candidate;heapq.heappush(queue,(candidate,b))
        vertex_distance=np.full(count,np.inf)
        for key,value in distances.items():vertex_distance[key]=value
        d=np.min(vertex_distance[sample_corners]+corner_distance,axis=1)
        weights=sample_area*np.exp(-.5*(d/sigma)**2);valid=eligible&(weights>0)&(d<=3*sigma)
        if valid.any():local[index],_=reference(sample_rgb[valid],weights[valid])
        else:
            local[index]=global_reference;fallback_count+=1
        if not np.isfinite(local[index]).all()or not np.all((local[index]>0)&(local[index]<=1)):
            raise ValueError('A local source skin reference cannot be calibrated.')
        minimum_neighbors=min(minimum_neighbors,int(valid.sum()));maximum_neighbors=max(maximum_neighbors,int(valid.sum()))
    used=consumed[groups];source_normals=np.asarray(head['normal'])[used]
    if np.any(np.linalg.norm(source_normals,axis=1)<.5):raise ValueError('A consumed skin source has no valid normal.')
    return dict(policy='surface-local-v4',positions=np.asarray(head['vertices'])[used].tolist(),normals=source_normals.tolist(),references=local[groups[used]].tolist(),
        bodyTarget=target.tolist(),sigmaMetres=float(sigma),sourceNodes=int(consumed.sum()),minimumReferenceNeighbors=minimum_neighbors,
        maximumReferenceNeighbors=maximum_neighbors,referenceRule=('physical-area source-declared skin plus upper-half samples'if reference_eligibility is not None else'physical-area upper-half skin samples')+', geodesic Gaussian, local upper-half median',
        minimumSkinReferenceLuminance=float(skin_threshold),globalFallbackNodes=fallback_count,calibratedLod=0,
        explicitSkinReferenceSamples=int(np.count_nonzero(explicit_skin)),
        referenceEligibilitySha256=digest(np.asarray(reference_eligibility,float))if reference_eligibility is not None else None)


def surface_tone_atlas(head,coefficient,width,height,memo=None,padding_texels=1.5):
    """Evaluate the same LOD0 field on each actual LOD; preserve ambiguous UV occupants."""
    if coefficient.get('policy') not in ('surface-local-v3','surface-local-v4'):raise ValueError('The source surface calibration policy differs.')
    source=np.asarray(coefficient['positions'],float);source_normals=np.asarray(coefficient['normals'],float);references=np.asarray(coefficient['references'],float)
    if source.shape!=source_normals.shape or source.shape!=references.shape or source.ndim!=2 or source.shape[1]!=3 or not len(source):
        raise ValueError('A source surface reference has incompatible point channels.')
    memo=RasterMemo()if memo is None else memo
    vertices=np.asarray(head['vertices']);normals=np.asarray(head['normal']);target=np.asarray(coefficient['bodyTarget'])
    # Vertex references are independent of mip size. Retain one content-keyed
    # result, so mutable packets and coefficients cannot reuse stale values.
    tone_key=digest(source,source_normals,references,vertices,normals,head['triangles'],target)
    if memo.tone_vertices is not None and memo.tone_vertices[0]==tone_key:
        _,local,maximum_distance=memo.tone_vertices
    else:
        local=np.tile(target,(len(vertices),1));maximum_distance=0.
        for index in np.unique(head['triangles']):
            position,normal=vertices[index],normals[index]
            square=np.sum((source-position)**2,axis=1);compatible=np.maximum(0,source_normals@normal)
            candidates=np.nonzero(compatible>0)[0]
            if not len(candidates):raise ValueError('No compatible LOD0 skin surface reference.')
            candidates=candidates[np.argsort(square[candidates])[:4]];nearest=float(square[candidates[0]])
            exact=square[candidates]<=1e-20
            weights=exact.astype(float)if exact.any()else compatible[candidates]/np.maximum(square[candidates],1e-20)
            local[index]=np.sum(references[candidates]*weights[:,None],axis=0)/weights.sum();maximum_distance=max(maximum_distance,float(np.sqrt(nearest)))
        local.flags.writeable=False
        memo.tone_vertices=(tone_key,local,maximum_distance)
    protected=np.zeros((height,width),bool);faces,bary,positions,ambiguous=raster(head,width,height,protected,memo)
    atlas=np.full((height,width,3),np.nan);known=(faces>=0)&~ambiguous;y,x=np.nonzero(known)
    atlas[y,x]=np.einsum('ij,ijk->ik',bary[y,x],local[np.asarray(head['triangles'])[faces[y,x]]])
    distance=np.full((height,width),np.inf);gutter=np.zeros_like(known)
    if not np.isfinite(padding_texels)or padding_texels<1.5:raise ValueError('A tone chart must cover its bilinear sampler footprint.')
    # BC7 finishing requests enough source support for a complete 4x4 block.
    for edge,owners in head['edges'].items():
        if len(owners)!=1:continue
        ua,ub=np.asarray(head['uv'])[list(edge)];extent=np.asarray([width,height]);lo=np.floor(np.minimum(ua,ub)*extent-padding_texels-1).astype(int);hi=np.ceil(np.maximum(ua,ub)*extent+padding_texels+1).astype(int)
        gx,gy=np.meshgrid(np.arange(lo[0],hi[0]+1),np.arange(lo[1],hi[1]+1));grid=np.column_stack((gx.ravel(),gy.ravel()));query=(grid+.5)/extent;delta=(ub-ua)*extent;square=float(delta@delta)
        if square==0:continue
        amount=np.clip(np.sum((query-ua)*extent*delta,axis=1)/square,0,1);d=np.linalg.norm((query-ua-amount[:,None]*(ub-ua))*extent,axis=1)
        x=grid[:,0]%width;y=height-1-grid[:,1]%height;value=local[edge[0]]*(1-amount[:,None])+local[edge[1]]*amount[:,None]
        eligible=(d<=padding_texels)&(faces[y,x]<0)&~ambiguous[y,x];take=eligible&(d<distance[y,x]);atlas[y[take],x[take]]=value[take];distance[y[take],x[take]]=d[take];gutter[y[take],x[take]]=True
    return atlas,dict(knownTexels=int(np.isfinite(atlas[:,:,0]).sum()),ambiguousTexels=int(ambiguous.sum()),gutterTexels=int(gutter.sum()),
        maximumNearestSourceDistanceMetres=maximum_distance,sharedLod0Calibration=True,unmappedAndAmbiguousRawPreserved=True,gutterRadiusTexels=padding_texels)


def surface_tone_mip(atlas):
    known=np.isfinite(atlas[:,:,0]);count=downsample(known).sum(axis=(1,3));total=downsample(np.where(known[:,:,None],atlas,0)).sum(axis=(1,3))
    result=np.full_like(total,np.nan);np.divide(total,count[:,:,None],out=result,where=count[:,:,None]>0)
    return result


def surface_tone_main_mip(raw,weight,coefficient,atlas,c1_source=None):
    """Apply the declared source calibration without changing C1 or source alpha."""
    if coefficient.get('policy')!='surface-local-v4'or atlas.shape!=(*weight.shape,3):raise ValueError('The surface tone field and Main mip disagree.')
    baseline=raw if c1_source is None else np.asarray(c1_source,np.uint8)
    if baseline.shape!=raw.shape:raise ValueError('The preserved C1 source and native pigment mip disagree.')
    target=np.asarray(coefficient['bodyTarget']);source=atlas[::-1];known=np.isfinite(source[:,:,0]);source=np.where(known[:,:,None],source,target)
    rgb=lookup[raw[:,:,:3]];corrected=rgb*(target/source)
    # Skin calibration changes the reference colour. Keep the source's dark
    # pigment contrast; compress only colours that actually exceed the gamut.
    corrected/=np.maximum(1,corrected.max(axis=2))[:,:,None]
    base=lookup[baseline[:,:,:3]];value=base+(corrected-base)*(1-weight[::-1,:,None]);value[~known]=base[~known]
    if not np.isfinite(value).all()or value.min()<0 or value.max()>1:raise ValueError('Surface V3 RGB escaped the finite UNorm domain.')
    srgb=np.where(value<=.0031308,value*12.92,1.055*value**(1/2.4)-.055);result=baseline.copy();result[:,:,:3]=np.rint(srgb*255).astype(np.uint8);fixed=weight[::-1]==1;result[fixed]=baseline[fixed];result[~known]=baseline[~known]
    assert np.array_equal(result[:,:,3],baseline[:,:,3])and np.array_equal(result[fixed],baseline[fixed])and np.array_equal(result[~known],baseline[~known])
    return result,dict(policy='surface-local-v4',rgbResponse='multiplicative-with-gamut-only-compression',preToneAlphaExact=True,c1WeightOneRgbaExact=True,unmappedRawRgbaExact=True,
        sourceC1BaselineUsed=c1_source is not None,knownTexels=int(known.sum()),actuallyChangedTexels=int(np.any(result!=baseline,axis=2).sum()),finiteRange=True)


def restore_pigment_contrast(native,authored,ownership):
    """Recover source dark-pigment contrast in an explicitly owned legacy field."""
    native=np.asarray(native,np.uint8);authored=np.asarray(authored,np.uint8);ownership=np.asarray(ownership,float)
    if native.shape!=authored.shape or native.shape[:2]!=ownership.shape or native.shape[2]!=4:
        raise ValueError('Pigment contrast requires matching RGBA images and source ownership.')
    if not np.isfinite(ownership).all()or ownership.min()<0 or ownership.max()>1:raise ValueError('Invalid pigment ownership.')
    result=authored.copy();owned=ownership>0
    if not owned.any():return result,dict(changedTexels=0,unownedRgbaExact=True,alphaExact=True)
    ys,xs=np.nonzero(owned);radius=max(1,round(native.shape[1]/256));margin=radius*3
    y0=max(0,int(ys.min())-margin);y1=min(native.shape[0],int(ys.max())+margin+1)
    x0=max(0,int(xs.min())-margin);x1=min(native.shape[1],int(xs.max())+margin+1)
    region=(slice(y0,y1),slice(x0,x1));source=lookup[native[region][:,:,:3]];current=lookup[authored[region][:,:,:3]]
    luma=np.asarray([.2126,.7152,.0722]);source_luma=source@luma;current_luma=current@luma
    def low_pass(value):
        value=value.copy()
        for _ in range(3):
            for axis in (0,1):
                padding=[(0,0)]*2;padding[axis]=(radius,radius)
                padded=np.pad(value,padding,mode='edge');prefix=np.cumsum(padded,axis=axis,dtype=float)
                zero=np.zeros_like(np.take(prefix,[0],axis=axis));prefix=np.concatenate((zero,prefix),axis=axis)
                first=[slice(None)]*2;last=[slice(None)]*2;first[axis]=slice(2*radius+1,None);last[axis]=slice(None,-(2*radius+1))
                value=(prefix[tuple(first)]-prefix[tuple(last)])/(2*radius+1)
        return value
    ratio=source_luma/np.maximum(low_pass(source_luma),1e-8)
    desired=low_pass(current_luma)*ratio
    dark=np.clip((1-ratio)/.1,0,1);gain=np.minimum(1,desired/np.maximum(current_luma,1e-8))
    corrected=current*(1+(gain-1)*ownership[region]*dark)[:,:,None]
    srgb=np.where(corrected<=.0031308,corrected*12.92,1.055*corrected**(1/2.4)-.055)
    result[region][:,:,:3]=np.rint(np.clip(srgb,0,1)*255).astype(np.uint8)
    result[~owned]=authored[~owned]
    if not np.array_equal(result[:,:,3],authored[:,:,3]):raise ValueError('Pigment contrast changed source alpha.')
    return result,dict(changedTexels=int(np.count_nonzero(np.any(result!=authored,axis=2))),unownedRgbaExact=True,alphaExact=True,
        nativeDarkContrastRestored=True,multiplicativeRgbGain=True,runtimeTintParametersUnchanged=True)


def brow_ownership(packet,skin,width,height):
    """Rasterize the native eyebrow bones, including conflicting UV aliases."""
    bones=[value.rsplit('/',1)[-1].lower()if value else''for value in skin['bones']]
    indices=np.asarray(packet['boneIndices'],int).reshape(-1,4);weights=np.asarray(packet['weights'],float).reshape(-1,4)
    selected=np.asarray(['eyebrow'in value for value in bones],float)
    influence=np.sum(weights*selected[indices],axis=1)
    influence=np.clip((influence-.02)/.18,0,1);influence=influence*influence*(3-2*influence)
    uv=np.asarray(next(row['values']for row in packet['uv']if row['channel']==0),float).reshape(-1,4)[:,:2]
    ownership=np.ones((height,width));covered=np.zeros((height,width),bool)
    for slot,triangles in enumerate(packet['subMeshes']):
        if slot>=len(skin['materials'])or not skin['materials'][slot]or 'Skin'not in skin['materials'][slot]['shader']:continue
        for triangle in np.asarray(triangles,int).reshape(-1,3):
            sampled=pixels_for(uv[triangle],width,height)
            if sampled is None:continue
            y,x,bary=sampled;value=np.clip(bary@influence[triangle],0,1)
            np.minimum.at(ownership,(y,x),value);covered[y,x]=True
    ownership[~covered]=0
    return ownership


def bc7_rgb_endpoints(block,original,target):
    """Fit RGB endpoints while preserving BC7 partitions, indices, P-bits and alpha.

    Bit allocation follows Microsoft's BC7 Format Mode Reference. Rotated
    scalar channels in modes 4/5 retain the actual alpha endpoint bits too.
    """
    bits=int.from_bytes(block,'little');mode=next((i for i in range(8)if bits&(1<<i)),None)
    if mode is None:raise ValueError('Reserved BC7 mode in an owned pigment block.')
    subsets=(3,2,3,2,1,1,1,2)[mode];partition=(4,6,6,6,0,0,0,6)[mode]
    rgb_bits=(4,6,5,7,5,7,7,5)[mode];alpha_bits=(0,0,0,0,6,8,7,5)[mode]
    start=mode+1+partition;rotation=(bits>>start)&3 if mode in (4,5)else 0
    start+=2 if mode in (4,5)else 0;start+=1 if mode==4 else 0
    endpoints=subsets*2;p_offset=start+endpoints*(rgb_bits*3+alpha_bits)
    has_p=mode in (0,1,3,6,7)
    a=np.asarray(original,float).reshape(-1,4)[:,:3];b=np.asarray(target,float).reshape(-1,4)[:,:3]
    centered=a-a.mean(axis=0);variance=np.sum(centered*centered,axis=0)
    slope=np.ones(3);np.divide(np.sum(centered*(b-b.mean(axis=0)),axis=0),variance,out=slope,where=variance>1e-12)
    slope=np.clip(slope,0,4);bias=b.mean(axis=0)-slope*a.mean(axis=0)
    position=start
    for channel,width in enumerate((rgb_bits,rgb_bits,rgb_bits,alpha_bits)):
        if not width:continue
        actual=3 if rotation and channel==rotation-1 else rotation-1 if rotation and channel==3 else channel
        for endpoint in range(endpoints):
            if actual<3:
                raw=(bits>>position)&((1<<width)-1)
                pbit=(bits>>(p_offset+(endpoint//2 if mode==1 else endpoint)))&1 if has_p else 0
                precision=width+int(has_p)
                def expand(value):
                    value=(value<<1)|pbit if has_p else value
                    return(value<<(8-precision))|(value>>(2*precision-8))
                wanted=np.clip(expand(raw)*slope[actual]+bias[actual],0,255)
                values=np.asarray([expand(v)for v in range(1<<width)])
                replacement=int(np.argmin(np.abs(values-wanted)));mask=((1<<width)-1)<<position
                bits=(bits&~mask)|(replacement<<position)
            position+=width
    return bits.to_bytes(16,'little')


def refine_bc7_rgb_endpoints(block,target,maximum_rgb_error=None):
    """Minimize actual decoded RGB error on the existing alpha/index codebook."""
    from PIL import Image
    bits=int.from_bytes(block,'little');mode=next((i for i in range(8)if bits&(1<<i)),None)
    if mode is None:raise ValueError('Reserved BC7 mode in an RGB endpoint fit.')
    subsets=(3,2,3,2,1,1,1,2)[mode];partition=(4,6,6,6,0,0,0,6)[mode]
    rgb_bits=(4,6,5,7,5,7,7,5)[mode];alpha_bits=(0,0,0,0,6,8,7,5)[mode]
    position=mode+1+partition;rotation=(bits>>position)&3 if mode in(4,5)else 0
    position+=2 if mode in(4,5)else 0;position+=1 if mode==4 else 0;fields=[];pairs=[]
    for channel,width in enumerate((rgb_bits,rgb_bits,rgb_bits,alpha_bits)):
        if not width:continue
        actual=3 if rotation and channel==rotation-1 else rotation-1 if rotation and channel==3 else channel
        if actual<3:pairs.extend((position+subset*2*width,width,actual)for subset in range(subsets))
        for _ in range(subsets*2):
            if actual<3:fields.append((position,width))
            position+=width
    desired=np.asarray(target,np.int16).reshape(1,16,4)[:,:,:3]
    def score(payload,count):
        rgba=np.asarray(Image.frombytes('RGBA',(count*4,4),payload,'bcn',7)).reshape(4,count,4,4).transpose(1,0,2,3).reshape(count,16,4)
        error=rgba[:,:,:3].astype(np.int16)-desired
        return np.max(np.abs(error),axis=(1,2)),np.mean(error.astype(float)**2,axis=(1,2))
    maximum,rms=score(block,1);best=(int(maximum[0]),float(rms[0]))
    for _ in range(2):
        improved=False
        for position,width in fields:
            mask=((1<<width)-1)<<position
            variants=[((bits&~mask)|(value<<position)).to_bytes(16,'little')for value in range(1<<width)]
            maxima,errors=score(b''.join(variants),len(variants));index=int(np.lexsort((errors,maxima))[0]);candidate=(int(maxima[index]),float(errors[index]))
            if candidate<best:bits=int.from_bytes(variants[index],'little');best=candidate;improved=True
        if not improved:break
    if maximum_rgb_error is not None and best[0]>maximum_rgb_error:
        # A joint endpoint move can cross a plateau that scalar coordinate
        # descent cannot. Search only failed channel/subset pairs; alpha,
        # partitions, interpolation indices and P-bits remain unchanged.
        for position,width,channel in pairs:
            mask=((1<<(2*width))-1)<<position;fixed=bits&~mask;limit=1<<(2*width)
            def decode(payload,count):
                return np.asarray(Image.frombytes('RGBA',(count*4,4),payload,'bcn',7)).reshape(4,count,4,4).transpose(1,0,2,3).reshape(count,16,4)
            controls=decode(fixed.to_bytes(16,'little')+(fixed|mask).to_bytes(16,'little'),2)
            owned=controls[0,:,channel]!=controls[1,:,channel]
            if not owned.any():continue
            def pair_score(payload,count):
                error=decode(payload,count)[:,owned,channel].astype(np.int16)-desired[0,owned,channel]
                return np.max(np.abs(error),axis=1),np.mean(error.astype(float)**2,axis=1)
            maximum,rms=pair_score(bits.to_bytes(16,'little'),1);pair_best=(int(maximum[0]),float(rms[0]))
            if pair_best[0]<=maximum_rgb_error:continue
            for at in range(0,limit,1024):
                values=range(at,min(limit,at+1024));payload=b''.join((fixed|(value<<position)).to_bytes(16,'little')for value in values)
                maxima,errors=pair_score(payload,len(values));index=int(np.lexsort((errors,maxima))[0]);candidate=(int(maxima[index]),float(errors[index]))
                if candidate<pair_best:bits=fixed|((at+index)<<position);pair_best=candidate
    return bits.to_bytes(16,'little')


def whole_block_pixels(mask):
    """Expand only fully owned 4x4 blocks, including partial edge blocks."""
    mask=np.asarray(mask,bool)
    if mask.ndim!=2 or min(mask.shape)<1:raise ValueError('A block ownership mask must be a nonempty image.')
    height,width=mask.shape;padded=np.pad(mask,((0,(-height)%4),(0,(-width)%4)),mode='edge')
    active=padded.reshape((height+3)//4,4,(width+3)//4,4).all(axis=(1,3))
    return active.ravel(),np.repeat(np.repeat(active,4,axis=0),4,axis=1)[:height,:width]








@functools.lru_cache(maxsize=512)
def bc7_exact_alpha_palette(values):
    """Find a separate four-level alpha codebook without changing decoded alpha."""
    if not 1<=len(values)<=4:return None
    if len(values)==1:return values[0],values[0],(values[0],)*4
    a,b=np.triu_indices(256);weights=np.asarray([0,21,43,64])
    palette=np.right_shift((64-weights)*a[:,None]+weights*b[:,None]+32,6)
    eligible=np.ones(len(a),bool)
    for value in values:eligible&=np.any(palette==value,axis=1)
    candidates=np.flatnonzero(eligible)
    if not len(candidates):return None
    index=candidates[np.argmin(b[candidates]-a[candidates])]
    return int(a[index]),int(b[index]),tuple(map(int,palette[index]))


def reencode_bc7_rgb_block(block,target):
    """Try BC7 mode5 RGB indices while retaining every decoded source alpha value.

    Mode5 has independent RGB and alpha indices (Microsoft BC7 mode reference).
    It can represent a new pigment pattern which fixed source indices cannot.
    """
    from PIL import Image
    original=np.asarray(Image.frombytes('RGBA',(4,4),block,'bcn',7)).reshape(16,4)
    alpha=bc7_exact_alpha_palette(tuple(map(int,np.unique(original[:,3]))))
    if alpha is None:return block
    desired=np.asarray(target,np.uint8).reshape(16,4)[:,:3].astype(float)
    centered=desired-desired.mean(axis=0);_,axes=np.linalg.eigh(centered.T@centered)
    projection=centered@axes[:,-1];starts=[desired[[projection.argmin(),projection.argmax()]],np.stack((desired.min(axis=0),desired.max(axis=0)))]
    levels=(np.arange(128)<<1)|(np.arange(128)>>6);weights=np.asarray([0,21,43,64])
    def score(values):
        error=values.astype(float)-desired
        return float(np.abs(error).max()),float(np.sum(error*error))
    best_score=score(original[:,:3]);best=None
    for endpoints in starts:
        for _ in range(8):
            codes=np.abs(levels[:,None,None]-endpoints[None,:,:]).argmin(axis=0)
            expanded=levels[codes];palette=((64-weights[:,None])*expanded[0]+weights[:,None]*expanded[1]+32)>>6
            indices=np.sum((palette[None,:,:]-desired[:,None,:])**2,axis=2).argmin(axis=1)
            candidate_score=score(palette[indices])
            if candidate_score<best_score:best_score=candidate_score;best=(codes.copy(),indices.copy(),palette[indices].copy())
            if len(np.unique(indices))<2:break
            fraction=weights[indices]/64
            endpoints=np.clip(np.linalg.lstsq(np.column_stack((1-fraction,fraction)),desired,rcond=None)[0],0,255)
    if best is None:return block
    codes,indices,expected=best;a,b,alpha_palette=alpha
    alpha_indices=np.abs(np.asarray(alpha_palette)[None,:]-original[:,3,None]).argmin(axis=1)
    if indices[0]>=2:codes=codes[::-1].copy();indices=3-indices
    if alpha_indices[0]>=2:a,b=b,a;alpha_indices=3-alpha_indices
    bits=0;position=0
    def put(value,width):
        nonlocal bits,position
        bits|=int(value)<<position;position+=width
    put(1<<5,6);put(0,2)
    for channel in range(3):
        for endpoint in range(2):put(codes[endpoint,channel],7)
    put(a,8);put(b,8)
    for stream in (indices,alpha_indices):
        for index,value in enumerate(stream):put(value,1 if index==0 else 2)
    if position!=128:raise ValueError('The BC7 mode5 bit allocation differs.')
    encoded=bits.to_bytes(16,'little');decoded=np.asarray(Image.frombytes('RGBA',(4,4),encoded,'bcn',7)).reshape(16,4)
    if not np.array_equal(decoded[:,:3],expected)or not np.array_equal(decoded[:,3],original[:,3]):
        raise ValueError('The independent BC7 decoder rejected the exact-alpha RGB codebook.')
    return encoded


def fit_bc7_rgb_field(payload,width,height,target,editable=None,maximum_rgb_error=None,allow_reencoding=False):
    """Fit RGB endpoints, optionally replacing failing blocks with exact-alpha mode5."""
    from PIL import Image
    if width<1 or height<1 or np.shape(target)!=(height,width,4):raise ValueError('BC7 RGB field dimensions differ.')
    target=np.asarray(target,np.uint8)
    length=((width+3)//4)*((height+3)//4)*16
    if len(payload)!=length:raise ValueError('The BC7 field is not one complete mip.')
    installed=np.asarray(Image.frombytes('RGBA',(width,height),payload,'bcn',7))
    def blocks(image):
        padded=np.pad(image,((0,(-height)%4),(0,(-width)%4),(0,0)),mode='edge')
        return padded.reshape((height+3)//4,4,(width+3)//4,4,4).transpose(0,2,1,3,4).reshape(-1,16,4)
    decoded=installed
    original=blocks(decoded);desired=blocks(target)
    words=np.frombuffer(payload,dtype='<u8').reshape(-1,2).copy()
    def read_bits(ids,position,width):
        if position>=64:return (words[ids,1]>>np.uint64(position-64))&np.uint64((1<<width)-1)
        low=min(width,64-position)
        value=(words[ids,0]>>np.uint64(position))&np.uint64((1<<low)-1)
        if low<width:value|=(words[ids,1]&np.uint64((1<<(width-low))-1))<<np.uint64(low)
        return value
    def write_bits(ids,position,width,value):
        word=position//64;position%=64;low=min(width,64-position)
        mask=np.uint64(((1<<low)-1)<<position)
        words[ids,word]=(words[ids,word]&~mask)|((value&np.uint64((1<<low)-1))<<np.uint64(position))
        if low<width:
            mask=np.uint64((1<<(width-low))-1)
            words[ids,1]=(words[ids,1]&~mask)|((value>>np.uint64(low))&mask)
    modes=np.full(len(words),-1)
    for mode in range(8):modes[(words[:,0]&np.uint64((1<<(mode+1))-1))==np.uint64(1<<mode)]=mode
    if np.any(modes<0):raise ValueError('Reserved BC7 mode in the RGB field.')
    for mode in range(8):
        subsets=(3,2,3,2,1,1,1,2)[mode];partition=(4,6,6,6,0,0,0,6)[mode]
        rgb_bits=(4,6,5,7,5,7,7,5)[mode];alpha_bits=(0,0,0,0,6,8,7,5)[mode]
        start=mode+1+partition;has_p=mode in (0,1,3,6,7);endpoints=subsets*2
        all_ids=np.flatnonzero(modes==mode)
        for rotation in range(4 if mode in (4,5)else 1):
            ids=all_ids[read_bits(all_ids,start,2)==rotation]if mode in (4,5)else all_ids
            for at in range(0,len(ids),8192):
                selected=ids[at:at+8192];a=original[selected,:,:3].astype(float);b=desired[selected,:,:3].astype(float)
                centered=a-a.mean(axis=1,keepdims=True);variance=np.sum(centered**2,axis=1);slope=np.ones_like(variance)
                np.divide(np.sum(centered*(b-b.mean(axis=1,keepdims=True)),axis=1),variance,out=slope,where=variance>1e-12)
                slope=np.clip(slope,0,4);bias=b.mean(axis=1)-slope*a.mean(axis=1)
                position=start+(2 if mode in (4,5)else 0)+(1 if mode==4 else 0)
                p_offset=position+endpoints*(3*rgb_bits+alpha_bits)
                for channel,bits in enumerate((rgb_bits,rgb_bits,rgb_bits,alpha_bits)):
                    if not bits:continue
                    actual=3 if rotation and channel==rotation-1 else rotation-1 if rotation and channel==3 else channel
                    for endpoint in range(endpoints):
                        if actual<3:
                            pbit=read_bits(selected,p_offset+(endpoint//2 if mode==1 else endpoint),1)if has_p else np.zeros(len(selected),np.uint64)
                            precision=bits+int(has_p);values=np.arange(1<<bits,dtype=np.uint64)[None,:]
                            values=(values<<np.uint64(1))|pbit[:,None]if has_p else np.broadcast_to(values,(len(selected),1<<bits))
                            expanded=(values<<np.uint64(8-precision))|(values>>np.uint64(2*precision-8))
                            old=read_bits(selected,position,bits);old=(old<<np.uint64(1))|pbit if has_p else old
                            old=(old<<np.uint64(8-precision))|(old>>np.uint64(2*precision-8))
                            wanted=np.clip(old*slope[:,actual]+bias[:,actual],0,255)
                            replacement=np.argmin(np.abs(expanded.astype(float)-wanted[:,None]),axis=1).astype(np.uint64)
                            write_bits(selected,position,bits,replacement)
                        position+=bits
    retained=0;active=np.ones(len(words),bool)
    if editable is not None:
        mask=np.asarray(editable,bool)
        if mask.shape!=(height,width):raise ValueError('BC7 field ownership dimensions differ.')
        active,pixel_active=whole_block_pixels(mask)
        words[~active]=np.frombuffer(payload,dtype='<u8').reshape(-1,2)[~active];retained=int((~active).sum())
        target=target.copy();target[~pixel_active]=decoded[~pixel_active]
    refined=0;reencoded=0
    if maximum_rgb_error is not None:
        if not isinstance(maximum_rgb_error,int)or maximum_rgb_error<0:raise ValueError('Invalid BC7 RGB error bound.')
        encoded=words.tobytes();saved=blocks(np.asarray(Image.frombytes('RGBA',(width,height),encoded,'bcn',7)))
        desired=blocks(target);errors=np.max(np.abs(saved[:,:,:3].astype(np.int16)-desired[:,:,:3].astype(np.int16)),axis=(1,2))
        for index in np.nonzero(errors>maximum_rgb_error)[0]:
            old=words[index].tobytes();replacement=refine_bc7_rgb_endpoints(old,desired[index],maximum_rgb_error)
            if replacement!=old:words[index]=np.frombuffer(replacement,dtype='<u8');refined+=1
            if allow_reencoding:
                actual=np.asarray(Image.frombytes('RGBA',(4,4),replacement,'bcn',7)).reshape(16,4)
                if np.max(np.abs(actual[:,:3].astype(np.int16)-desired[index,:,:3].astype(np.int16)))>maximum_rgb_error:
                    candidate=reencode_bc7_rgb_block(replacement,desired[index])
                    if candidate!=replacement:words[index]=np.frombuffer(candidate,dtype='<u8');reencoded+=1
    encoded=words.tobytes();saved=np.asarray(Image.frombytes('RGBA',(width,height),encoded,'bcn',7))
    if not np.array_equal(saved[:,:,3],installed[:,:,3]):raise ValueError('BC7 RGB field changed decoded source alpha.')
    if not np.array_equal(words[~active],np.frombuffer(payload,dtype='<u8').reshape(-1,2)[~active]):
        raise ValueError('BC7 RGB field changed an unowned block.')
    error=saved[:,:,:3].astype(float)-target[:,:,:3]
    return encoded,dict(blocks=len(words),alphaExact=True,partitionsAndIndicesRetained=reencoded==0,retainedBoundaryBlocks=retained,refinedEndpointBlocks=refined,reencodedRgbBlocks=reencoded,
        unownedBlocksExact=True,
        changedBlocks=int(np.count_nonzero(np.any(words!=np.frombuffer(payload,dtype='<u8').reshape(-1,2),axis=1))),
        maximumRgbError=int(np.abs(error).max()),rootMeanSquareRgbError=float(np.sqrt(np.mean(error**2))))


def restore_bc7_brow_blocks(payload,width,height,mip_count,native_mips,packet,skin,additional_sources=()):
    """Fit fully eyebrow-owned BC7 RGB endpoints; every other block stays exact."""
    from PIL import Image
    result=bytearray(payload);offset=0;proof=[]
    for level in range(mip_count):
        w=max(1,width>>level);h=max(1,height>>level);columns=max(1,(w+3)//4);rows=max(1,(h+3)//4);length=columns*rows*16
        original=payload[offset:offset+length]
        if len(original)!=length:raise ValueError('An eyebrow BC7 mip is truncated.')
        if w<4 or h<4:
            proof.append(dict(level=level,changedBlocks=0,unownedBlocksExact=True));offset+=length;continue
        ownership=brow_ownership(packet,skin,w,h)
        for other_packet,other_skin in additional_sources:
            ownership=np.maximum(ownership,brow_ownership(other_packet,other_skin,w,h))
        allowed=np.all(ownership.reshape(rows,4,columns,4)>0,axis=(1,3))
        if not allowed.any():
            proof.append(dict(level=level,changedBlocks=0,unownedBlocksExact=True));offset+=length;continue
        image=Image.frombytes('RGBA',(w,h),original,'bcn',7).transpose(Image.Transpose.FLIP_TOP_BOTTOM)
        authored=np.asarray(image);native=native_mips[w,h]
        changed,contrast=restore_pigment_contrast(native,authored,ownership)
        blocks=np.any(np.any(changed!=authored,axis=2).reshape(rows,4,columns,4),axis=(1,3))&allowed
        if blocks.any():
            # Encoder order follows the original GPU blocks, before the PIL flip.
            gpu=changed[::-1].copy();blocks=blocks[::-1]
            ys,xs=np.nonzero(blocks);original_gpu=authored[::-1]
            candidate=bytearray(original)
            for y,x in zip(ys,xs):
                start=(int(y)*columns+int(x))*16;region=(slice(y*4,y*4+4),slice(x*4,x*4+4))
                candidate[start:start+16]=bc7_rgb_endpoints(original[start:start+16],original_gpu[region],gpu[region])
            decoded=np.asarray(Image.frombytes('RGBA',(w,h),bytes(candidate),'bcn',7).transpose(Image.Transpose.FLIP_TOP_BOTTOM))
            codec_owned=np.repeat(np.repeat(blocks[::-1],4,axis=0),4,axis=1)
            if not np.array_equal(decoded[~codec_owned],authored[~codec_owned]):raise ValueError('Eyebrow compression changed an unowned texel.')
            if not np.array_equal(decoded[:,:,3],authored[:,:,3]):raise ValueError('Eyebrow compression changed native alpha.')
            result[offset:offset+length]=candidate
        proof.append(dict(contrast,level=level,changedBlocks=int(blocks.sum()),unownedBlocksExact=True,alphaExact=True))
        offset+=length
    if offset!=len(payload):raise ValueError('An eyebrow BC7 payload has undeclared trailing bytes.')
    return bytes(result),dict(changedBlocks=sum(row['changedBlocks']for row in proof),mips=proof,
        nativeEyebrowBoneOwnership=True,unownedDecodedTexelsExact=True,alphaExact=True,runtimeTintParametersUnchanged=True,
        bc7PartitionsIndicesAndPbitsExact=True)


def project_skin_surface_uv(source,positions,normals,maximum_distance):
    """Find exact compatible triangles using bounded expanding spatial cells."""
    if not np.isfinite(maximum_distance)or maximum_distance<=0:raise ValueError('A skin bake needs a positive finite source cage.')
    positions=np.asarray(positions,float);normals=np.asarray(normals,float)
    if positions.shape!=normals.shape or positions.ndim!=2 or positions.shape[1]!=3 or not np.isfinite(positions).all()or not np.isfinite(normals).all():raise ValueError('A skin projection needs finite position/normal pairs.')
    triangles=source['vertices'][source['triangles']];tn=normalize(np.cross(triangles[:,1]-triangles[:,0],triangles[:,2]-triangles[:,0]))
    low=triangles.min(axis=1);high=triangles.max(axis=1);size=maximum_distance/8
    cells=np.floor(positions/size).astype(int);unique,inverse=np.unique(cells,axis=0,return_inverse=True)
    order=np.argsort(inverse,kind='stable');ends=np.cumsum(np.bincount(inverse));starts=np.r_[0,ends[:-1]]
    result=np.empty((len(positions),2));distances=np.empty(len(positions))
    for cell,start,end in zip(unique,starts,ends):
        unresolved=order[start:end];p0=cell*size
        for radius in(maximum_distance/16,maximum_distance/4,maximum_distance):
            ids=np.flatnonzero(np.all(high>=p0-radius,axis=1)&np.all(low<=p0+size+radius,axis=1))
            if not len(ids):continue
            a=triangles[ids,0];ab=triangles[ids,1]-a;ac=triangles[ids,2]-a
            aa=(ab*ab).sum(axis=1);ad=(ab*ac).sum(axis=1);dd=(ac*ac).sum(axis=1);det=aa*dd-ad*ad;remaining=[]
            for offset in range(0,len(unresolved),256):
                q=unresolved[offset:offset+256];p=positions[q,None];ap=p-a
                apb=(ap*ab).sum(axis=2);apc=(ap*ac).sum(axis=2)
                v=np.divide(dd*apb-ad*apc,det,out=np.zeros_like(apb),where=det>1e-20)
                w=np.divide(aa*apc-ad*apb,det,out=np.zeros_like(apb),where=det>1e-20)
                bary=np.stack((1-v-w,v,w),axis=2);point=a+v[:,:,None]*ab+w[:,:,None]*ac
                distance=((point-p)**2).sum(axis=2);distance[(bary<0).any(axis=2)|(det<=1e-20)[None]]=np.inf
                for first,second,columns in[(0,1,(0,1)),(1,2,(1,2)),(2,0,(2,0))]:
                    base=triangles[ids,first];edge=triangles[ids,second]-base;length=(edge*edge).sum(axis=1)
                    amount=np.clip(np.divide(((p-base)*edge).sum(axis=2),length,out=np.zeros_like(distance),where=length>1e-20),0,1)
                    candidate=base+amount[:,:,None]*edge;error=((candidate-p)**2).sum(axis=2);better=error<distance
                    alternative=np.zeros_like(bary);alternative[:,:,columns[0]]=1-amount;alternative[:,:,columns[1]]=amount
                    distance[better]=error[better];bary[better]=alternative[better]
                distance[normals[q]@tn[ids].T<=0]=np.inf
                chosen=np.argmin(distance,axis=1);minimum=distance[np.arange(len(q)),chosen];take=minimum<=radius**2
                accepted=q[take];selected=chosen[take]
                result[accepted]=np.einsum('ij,ijk->ik',bary[np.arange(len(q))[take],selected],source['uv'][source['triangles'][ids[selected]]])
                distances[accepted]=np.sqrt(minimum[take]);remaining.extend(q[~take])
            unresolved=np.asarray(remaining,int)
            if not len(unresolved):break
        if len(unresolved):raise ValueError('A skin LOD has no compatible LOD0 surface inside its declared bake cage.')
    return result,distances


def bake_skin_lod_pigment(source,target,pixels,source_pixels,weight):
    """Bake LOD0 neck pigment into current UVs, preserving face RGB and material A."""
    pixels=np.asarray(pixels,np.uint8);source_pixels=np.asarray(source_pixels,np.uint8);weight=np.asarray(weight,float)
    if pixels.ndim!=3 or pixels.shape[2]!=4 or source_pixels.ndim!=3 or source_pixels.shape[2]!=4 or weight.shape!=pixels.shape[:2]:raise ValueError("A skin LOD bake has incompatible image and anatomical ownership dimensions.")
    cage=float(np.linalg.norm(np.ptp(source["vertices"],axis=0))*.1)
    h,w=pixels.shape[:2];memo=RasterMemo();face,bary,position,ambiguous=raster(target,w,h,np.zeros((h,w),bool),memo)
    known=(face>=0)&~ambiguous&(weight<1-8*np.finfo(np.float32).eps);y,x=np.nonzero(known)
    if not len(y):return pixels.copy(),np.zeros((h,w),bool),dict(surfaceSamples=0,maximumProjectionMetres=0.,p95ProjectionMetres=0.,maximumProjectionCageMetres=cage,materialAlphaExact=True,faceColourSourceUnchanged=True)
    tri=target['triangles'][face[y,x]];normal=normalize(np.einsum('ij,ijk->ik',bary[y,x],target['normal'][tri]))
    uv,distance=project_skin_surface_uv(source,position[y,x].astype(float),normal,cage)
    colour=bilinear_gpu_rows(linear_rgba(source_pixels),uv)[:,:3]
    image=linear_rgba(pixels);fraction=1-weight[y,x]
    image[h-1-y,x,:3]=image[h-1-y,x,:3]+(colour-image[h-1-y,x,:3])*fraction[:,None]
    # Extend the chart into the bilinear and BC7 footprint; never cross an occupied chart.
    owned=np.zeros((h,w),bool);owned[h-1-y,x]=True
    for _ in range(3):
        accum=np.zeros_like(image[:,:,:3]);count=np.zeros((h,w));expanded=np.zeros((h,w),bool)
        for dy,dx in [(0,1),(0,-1),(1,0),(-1,0)]:
            mask=np.roll(owned,(dy,dx),(0,1));value=np.roll(image[:,:,:3],(dy,dx),(0,1));take=mask&~owned&(face[::-1]<0)
            accum[take]+=value[take];count[take]+=1;expanded|=take
        image[expanded,:3]=accum[expanded]/count[expanded,None];owned|=expanded
    result=pixels.copy();rgb=image[:,:,:3];s=np.where(rgb<=.0031308,rgb*12.92,1.055*np.maximum(rgb,0)**(1/2.4)-.055)
    result[:,:,:3]=np.rint(np.clip(s,0,1)*255).astype(np.uint8)
    return result,owned,dict(surfaceSamples=len(y),maximumProjectionMetres=float(distance.max()),p95ProjectionMetres=float(np.percentile(distance,95)),maximumProjectionCageMetres=cage,sourceCageRule='10 percent of the actual source surface diagonal',materialAlphaExact=True,faceColourSourceUnchanged=True)


def encode_owned_bc7_rgb(source,target,selected,*,require_budget=True):
    """Finish complete owned codec blocks with the established RGB 3/A 2 limits."""
    from PIL import Image
    import etcpak
    h,w=target.shape[:2];rows,columns=selected.shape
    if selected.dtype!=bool or (rows,columns)!=((h+3)//4,(w+3)//4)or len(source)!=rows*columns*16:raise ValueError("The owned BC7 source, dimensions and block ownership differ.")
    if not selected.any():return source,dict(blocks=0,unownedBlocksExact=True,rgbRms=0.,alphaRms=0.,maximumRgbError=0.)
    padded=np.pad(target,((0,(-h)%4),(0,(-w)%4),(0,0)),mode='edge')
    tiles=padded.reshape(rows,4,columns,4,4).transpose(0,2,1,3,4)[selected]
    count=len(tiles);strip=tiles.transpose(1,0,2,3).reshape(4,count*4,4)
    params=etcpak.BC7CompressBlockParams();params.init_linear_weights();params.m_uber_level=4;params.m_weights=[1,1,1,3]
    output=np.frombuffer(source,np.uint8).reshape(rows,columns,16).copy()
    if count:
        candidate=np.frombuffer(etcpak.compress_bc7(strip.tobytes(),count*4,4,params),np.uint8).reshape(count,16).copy()
        independent=bc7_rgba_mode4_blocks(tiles.reshape(count,16,4))
        def decode(blocks):
            return np.asarray(Image.frombytes('RGBA',(count*4,4),blocks.tobytes(),'bcn',7)).reshape(4,count,4,4).transpose(1,0,2,3).reshape(count,16,4)
        desired=tiles.reshape(count,16,4).astype(float);a=decode(candidate).astype(float)-desired;z=decode(independent).astype(float)-desired
        cost=lambda delta:np.maximum(np.mean(delta[:,:,:3]**2,axis=(1,2))/9,np.mean(delta[:,:,3]**2,axis=1)/4)
        take=cost(z)<cost(a);candidate[take]=independent[take]
        mode5=bc7_rgba_separate_blocks(tiles.reshape(count,16,4),5)
        a=decode(candidate).astype(float)-desired;z=decode(mode5).astype(float)-desired;take=cost(z)<cost(a);candidate[take]=mode5[take]
        fitted,_=fit_bc7_rgb_field(source,w,h,target,np.repeat(np.repeat(selected,4,axis=0),4,axis=1)[:h,:w])
        for alternative in (np.frombuffer(fitted,np.uint8).reshape(rows,columns,16)[selected],np.frombuffer(source,np.uint8).reshape(rows,columns,16)[selected]):
            a=decode(candidate).astype(float)-desired;z=decode(alternative).astype(float)-desired;take=cost(z)<cost(a);candidate[take]=alternative[take]
        output[selected]=candidate
    encoded=output.tobytes();decoded=np.asarray(Image.frombytes('RGBA',(w,h),encoded,'bcn',7))
    footprint=np.repeat(np.repeat(selected,4,axis=0),4,axis=1)[:h,:w];difference=decoded.astype(float)-target
    rgb_rms=float(np.sqrt(np.mean(difference[:,:,:3][footprint]**2)));alpha_rms=float(np.sqrt(np.mean(difference[:,:,3][footprint]**2)))
    if require_budget and(rgb_rms>3 or alpha_rms>2):raise ValueError('The owned skin field exceeds its established RGB/material-alpha limits: '+str((rgb_rms,alpha_rms)))
    return encoded,dict(blocks=count,unownedBlocksExact=True,rgbRms=rgb_rms,alphaRms=alpha_rms,maximumRgbError=float(np.abs(difference[:,:,:3][footprint]).max(initial=0)))



def encode_skin_lod_mips(images,payload,desired,owned):
    """Preserve RGB/material precision; bounded small textures may retain RGBA8."""
    images=[np.asarray(image,np.uint8)for image in images]
    delta=lookup[desired[:,:,:3]]-lookup[images[0][:,:,:3]]
    offset=0;encoded=[];levels=[];targets=[];fallback=False
    for level,before in enumerate(images):
        if level:delta=downsample(delta).mean(axis=(1,3));owned=downsample(owned).any(axis=(1,3))
        rgb=np.clip(lookup[before[:,:,:3]]+delta,0,1);srgb=np.where(rgb<=.0031308,rgb*12.92,1.055*np.maximum(rgb,0)**(1/2.4)-.055)
        target=before.copy();target[:,:,:3]=np.rint(srgb*255).astype(np.uint8);targets.append(target)
        h,w=before.shape[:2];length=((w+3)//4)*((h+3)//4)*16
        selected=np.pad(owned,((0,(-h)%4),(0,(-w)%4)),mode='edge').reshape((h+3)//4,4,(w+3)//4,4).any(axis=(1,3))
        value,codec=encode_owned_bc7_rgb(payload[offset:offset+length],target,selected,require_budget=False)
        fallback|=codec['rgbRms']>3 or codec['alphaRms']>2
        encoded.append(value);offset+=length;levels.append(dict(level=level,width=w,height=h,codec=codec))
    if offset!=len(payload):raise ValueError('The skin LOD bake omitted a source mip.')
    if fallback:
        if max(images[0].shape[:2])>512:raise ValueError('A skin LOD exceeds the bounded RGBA8 precision policy.')
        encoded=[target.tobytes()for target in targets]
        extra=sum(map(len,encoded))-len(payload)
        if extra>1024**2:raise ValueError('A precise skin LOD exceeds its 1-MiB storage allowance.')
        for row,value in zip(levels,encoded):row.update(codec=dict(format=4,rgbRms=0.,alphaRms=0.,unownedDecodedTexelsExact=True,materialAlphaExact=True,reason='joint BC7 RGB/material precision'),sha256=hashlib.sha256(value).hexdigest())
        return b''.join(encoded),4,levels
    for row,value in zip(levels,encoded):row.update(sha256=hashlib.sha256(value).hexdigest())
    return b''.join(encoded),25,levels
