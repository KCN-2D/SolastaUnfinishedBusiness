import argparse
import copy
import gzip
import hashlib
import json
import math
import sys
from pathlib import Path
from typing import Any

import bpy
import numpy as np

REPO = next(p for p in Path(__file__).resolve().parents if (p / 'SolastaUnfinishedBusiness.sln').is_file())
sys.path.insert(0, str(REPO / 'tools/asset-authoring'))
from blender_pipeline import Matrix, Vector, preview_receipt
import asset_names as resource_naming

SOURCE = None
CONVERT = np.array([[1, 0, 0, 0], [0, 0, -1, 0], [0, 1, 0, 0], [0, 0, 0, 1]], dtype=np.float64)


def read_json(path) -> 'Any':
    return json.loads(path.read_text(encoding='utf-8-sig'))


def hash_file(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def matrix(values):
    return np.array(values, dtype=np.float64).reshape(4, 4).T


def points(values, transform):
    p = np.asarray(values, dtype=np.float64).reshape(-1, 3)
    return p @ transform[:3, :3].T + transform[:3, 3]


def smooth(value):
    t = np.clip(value, 0.0, 1.0)
    return t * t * (3.0 - 2.0 * t)


def source_path(value):
    path = Path(value)
    if path.is_absolute():
        return path
    if SOURCE is None:
        raise ValueError('A relative native source path requires its declared sourceRoot.')
    return SOURCE / path


def load_packet(skin):
    path = source_path(skin['meshFile'])
    with gzip.open(path, 'rt', encoding='utf-8') as stream:
        packet = json.load(stream)
    if not packet.get('sourceFingerprint'):
        raise ValueError('A complete signed native source packet is required: ' + str(path))
    count = packet['vertexCount']
    assert len(packet['vertices']) == count * 3 and len(packet['weights']) == count * 4
    assert packet['uv'] and all(len(uv['values']) == count * 4 for uv in packet['uv'])
    return packet


def landmarks(packet, skin, transform):
    matrices = np.asarray(packet['bindPoses'], dtype=np.float64).reshape(-1, 4, 4).transpose(0, 2, 1)
    bones = [path.rsplit('/', 1)[-1].lower() if path else '' for path in skin['bones']]
    if len(matrices) != len(bones):
        raise ValueError('A native bindpose has no corresponding renderer bone.')
    origins = np.linalg.inv(matrices)[:, :3, 3]
    origins = origins @ transform[:3, :3].T + transform[:3, 3]
    positions = {name: origins[i] for i, name in enumerate(bones)}

    def mean(names):
        selected = [positions[name] for name in names if name in positions]
        return np.mean(selected, axis=0) if selected else None

    left = mean(['b_eyelid_upper_l', 'b_eyelid_lower_l'])
    right = mean(['b_eyelid_upper_r', 'b_eyelid_lower_r'])
    mouth = mean(['b_lip_upper_c', 'b_lip_lower_c'])
    nose = mean(['b_nostril_l', 'b_nostril_r'])
    head = mean(['bip001 head'])
    return bones, origins, dict(left=left, right=right, mouth=mouth, nose=nose, head=head)


def native_normals(packet, transform):
    n = np.asarray(packet['normals'], dtype=np.float64).reshape(-1, 3) @ np.linalg.inv(transform[:3, :3])
    return n / np.maximum(np.linalg.norm(n, axis=1, keepdims=True), 1e-12)


def weld_groups(positions):
    _, groups = np.unique(np.round(positions / 1e-6).astype(np.int64), axis=0, return_inverse=True)
    return groups


def coherent_displacements(original, delta, mask):
    groups = weld_groups(original)
    size = int(groups.max()) + 1
    total = np.zeros((size, 3)); count = np.bincount(groups)
    np.add.at(total, groups, delta)
    protected = np.zeros(size, dtype=bool)
    np.logical_or.at(protected, groups, mask == 0)
    total /= count[:, None]
    total[protected] = 0
    mask[protected[groups]] = 0
    return total[groups], groups


def shared_skin_boundary_pairs(packet, donor, bone_paths, donor_bone_paths, tolerance=1e-4, common_frame=None):
    """Match physical open edges, excluding duplicated UV chart edges."""
    receiver_poses=np.asarray(packet['bindPoses'],float).reshape(-1,4,4).transpose(0,2,1)
    donor_poses=np.asarray(donor['bindPoses'],float).reshape(-1,4,4).transpose(0,2,1)
    common=[(i,bone_paths.index(name))for i,name in enumerate(donor_bone_paths)if name in bone_paths]
    if not common:raise ValueError('Skin cuts have no shared native bind frame.')
    first,second=common[0];frame=np.linalg.inv(donor_poses[first])@receiver_poses[second]if common_frame is None else np.asarray(common_frame,float)
    errors=[(bone_paths[j],float(np.abs(receiver_poses[j]-donor_poses[i]@frame).max()))for i,j in common]
    if common_frame is None and max(error for _,error in errors)>1e-4:
        raise ValueError('Skin cut parts have incompatible native bind frames: '+str(sorted(errors,key=lambda item:item[1],reverse=True)[:4]))
    positions=np.asarray(packet['vertices'],float).reshape(-1,3)
    _,groups=np.unique(positions,axis=0,return_inverse=True);edges={}
    for submesh in packet['subMeshes']:
        for triangle in groups[np.asarray(submesh,int).reshape(-1,3)]:
            for a,b in zip(triangle,np.roll(triangle,-1)):
                if a!=b:
                    key=tuple(sorted((int(a),int(b))));edges[key]=edges.get(key,0)+1
    boundary={i for edge,count in edges.items()if count==1 for i in edge}
    donor_positions=np.asarray(donor['vertices'],float).reshape(-1,3)
    pairs=[]
    for index in np.nonzero(np.isin(groups,list(boundary)))[0]:
        point=points(positions[index:index+1],frame)[0]
        distances=np.linalg.norm(donor_positions-point,axis=1);nearest=int(distances.argmin())
        if distances[nearest]<=tolerance:pairs.append((int(index),nearest))
    return pairs


def overlap_skin_boundary(packet,donor,correspondences,common_frame,overlap_metres=.00005,tolerance=1e-4,follow_donor_shading=False):
    """Place a shared cut inside a donor triangle in every physical frame."""
    if not math.isfinite(overlap_metres)or not 0<overlap_metres<=tolerance:raise ValueError('Invalid skin-cut overlap.')
    frame=np.asarray(common_frame,float);inverse=np.linalg.inv(frame);pairs=list(correspondences)
    if not pairs or len({i for i,_ in pairs})!=len(pairs):raise ValueError('Skin-cut correspondences are empty or ambiguous.')
    donor_positions=np.asarray(donor['vertices'],float).reshape(-1,3)
    _,physical=np.unique(donor_positions,axis=0,return_inverse=True);cut={int(physical[j])for _,j in pairs};neighbors={}
    for submesh in donor['subMeshes']:
        for triangle in np.asarray(submesh,int).reshape(-1,3):
            p=donor_positions[triangle]
            if np.linalg.norm(np.cross(p[1]-p[0],p[2]-p[0]))<=1e-15:continue
            for group in set(map(int,physical[triangle]))&cut:neighbors.setdefault(group,[]).append(triangle)
    if any(not neighbors.get(group)for group in cut):raise ValueError('A skin cut has no donor interior for overlap.')
    receiver=np.asarray(packet['vertices'],float).reshape(-1,3);indices=np.asarray([i for i,_ in pairs],int);donors=np.asarray([j for _,j in pairs],int)
    gaps=np.linalg.norm(points(receiver[indices],frame)-donor_positions[donors],axis=1)
    if gaps.max()>tolerance:raise ValueError('The authored skin cut no longer matches its donor.')
    triangles=[]
    for j in donors:
        candidates=neighbors[int(physical[j])]
        def rank(triangle):
            p=donor_positions[triangle]
            return (sum(int(physical[k])not in cut for k in triangle),float(np.linalg.norm(np.cross(p[1]-p[0],p[2]-p[0]))),tuple(sorted(map(int,triangle))))
        triangles.append(max(candidates,key=rank))
    triangles=np.asarray(triangles,int)
    direction=donor_positions[triangles].mean(axis=1)-donor_positions[donors]
    length=np.linalg.norm(direction,axis=1)
    if np.any(length<1e-12):raise ValueError('The donor interior direction is degenerate.')
    fraction=overlap_metres/length
    if np.any(fraction>=1):raise ValueError('The skin-cut overlap exceeds its donor triangle.')
    def boundary_positions(positions):
        # Interpolate actual triangles. Projecting onto an artist normal's
        # tangent plane can move the edge outside the donor's raster surface.
        interior=positions[donors]+fraction[:,None]*(positions[triangles].mean(axis=1)-positions[donors])
        return points(interior,inverse)
    normals=np.asarray(donor['normals'],float).reshape(-1,3)
    result=copy.deepcopy(packet);positions=receiver.copy();positions[indices]=boundary_positions(donor_positions);result['vertices']=positions.astype(np.float32).ravel().tolist()
    shading_changes=0
    def unit(value):return value/np.maximum(np.linalg.norm(value,axis=1)[:,None],1e-30)
    def rotate(value,before,after):
        axis=np.cross(before,after);cosine=np.sum(before*after,axis=1)
        if np.any(cosine<-.95):raise ValueError('A shared skin shading frame reverses its source normal.')
        return value+np.cross(axis,value)+np.cross(axis,np.cross(axis,value))/np.maximum(1+cosine[:,None],1e-8)
    base_normals=np.asarray(packet['normals']).reshape(-1,3)
    base_tangents=np.asarray(packet['tangents']).reshape(-1,4)[:,:3]
    for shape in result['shapes']:
        source=next((s for s in donor['shapes']if s['name']==shape['name']),None)
        for physical_frame in shape['frames']:
            vertices=np.asarray(physical_frame['vertices'],float).reshape(-1,3)
            if source is not None:
                source_frame,=[f for f in source['frames']if f['weight']==physical_frame['weight']]
                posed=donor_positions+np.asarray(source_frame['vertices'],float).reshape(-1,3)
                posed_normals=normals+np.asarray(source_frame['normals'],float).reshape(-1,3)
                vertices[indices]=boundary_positions(posed)-positions[indices]
                if follow_donor_shading:
                    before=unit(normals[donors]@frame[:3,:3]);after=unit(posed_normals[donors]@frame[:3,:3])
                    target=unit(rotate(unit(base_normals[indices]),before,after))
                    delta_normal=np.asarray(physical_frame['normals']).reshape(-1,3).copy()
                    delta_tangent=np.asarray(physical_frame['tangents']).reshape(-1,3).copy()
                    old=unit(base_normals[indices]+delta_normal[indices]);changed=np.linalg.norm(old-target,axis=1)>1e-6
                    if changed.any():
                        selected=indices[changed];tangent=unit(base_tangents[selected]+delta_tangent[selected])
                        delta_tangent[selected]=unit(rotate(tangent,old[changed],target[changed]))-base_tangents[selected]
                        delta_normal[selected]=target[changed]-base_normals[selected]
                        physical_frame['normals']=delta_normal.astype(np.float32).ravel().tolist()
                        physical_frame['tangents']=delta_tangent.astype(np.float32).ravel().tolist();shading_changes+=int(changed.sum())
            physical_frame['vertices']=vertices.astype(np.float32).ravel().tolist()
    minimum=positions.min(axis=0);maximum=positions.max(axis=0)
    for shape in result['shapes']:
        for physical_frame in shape['frames']:
            posed=positions+np.asarray(physical_frame['vertices']).reshape(-1,3);minimum=np.minimum(minimum,posed.min(axis=0));maximum=np.maximum(maximum,posed.max(axis=0))
    old=np.asarray(packet['bounds']);minimum=np.minimum(minimum,old[:3]-old[3:]*.5);maximum=np.maximum(maximum,old[:3]+old[3:]*.5)
    result['bounds']=np.r_[(minimum+maximum)*.5,maximum-minimum].tolist()
    return result,dict(sharedVertices=len(pairs),maximumSourceGap=float(gaps.max()),donorInteriorOverlapMetres=overlap_metres,
        skinAndShadingChannelsExact=not bool(shading_changes),skinAndRestShadingChannelsExact=True,posedShadingFollowsDonor=follow_donor_shading,
        changedShadingFrameVertices=shading_changes,physicalChannelIdentityExact=True,frameCount=sum(len(s['frames'])for s in result['shapes']))


def conform_skin_boundary(packet, donor, bone_paths, donor_bone_paths, correspondences, maximum_source_gap=1e-4, shading_frame_owner='donor'):
    """Transport one shared physical cut, including skinning and physical frames."""
    if shading_frame_owner not in ('receiver','donor'):
        raise ValueError('A shared skin boundary needs an explicit shading-frame owner.')
    if len(set(bone_paths))!=len(bone_paths) or len(set(donor_bone_paths))!=len(donor_bone_paths):
        raise ValueError('A shared skin boundary needs unique native bone paths.')
    target={name:index for index,name in enumerate(bone_paths)}
    common=[(index,target[name])for index,name in enumerate(donor_bone_paths)if name in target]
    if not common:raise ValueError('A shared skin boundary has no common native palette.')
    receiver_poses=np.asarray(packet['bindPoses'],np.float64).reshape(-1,4,4).transpose(0,2,1)
    donor_poses=np.asarray(donor['bindPoses'],np.float64).reshape(-1,4,4).transpose(0,2,1)
    first,second=common[0];frame=np.linalg.inv(donor_poses[first])@receiver_poses[second]
    bind_error=max(float(np.abs(receiver_poses[j]-donor_poses[i]@frame).max())for i,j in common)
    if bind_error>1e-4:raise ValueError('The shared native palettes have incompatible renderer frames.')
    inverse=np.linalg.inv(frame);normal_frame=frame[:3,:3]
    output=copy.deepcopy(packet)
    pairs=list(correspondences)
    if not pairs or len({i for i,_ in pairs})!=len(pairs):raise ValueError('Shared cut correspondences are empty or ambiguous.')
    def vectors(value,key,width=3):return np.asarray(value[key],np.float64).reshape(-1,width).copy()
    def unit(value):return value/max(float(np.linalg.norm(value)),1e-30)
    def rotate(value,a,b):
        a,b=unit(a),unit(b);axis=np.cross(a,b);cosine=float(a@b)
        if cosine<-.999999:raise ValueError('A shared skin boundary has opposite source normals.')
        return value+np.cross(axis,value)+np.cross(axis,np.cross(axis,value))/(1+cosine)
    def basis(normal,tangent):
        normal=unit(normal);tangent=unit(tangent-normal*float(tangent@normal))
        if float(np.linalg.norm(tangent))<.5:raise ValueError('A shared skin boundary has a degenerate tangent frame.')
        return np.stack((tangent,np.cross(normal,tangent),normal),axis=1)
    positions=vectors(output,'vertices');normals=vectors(output,'normals');tangents=vectors(output,'tangents',4)
    weights=vectors(output,'weights',4);indices=np.asarray(output['boneIndices'],np.int64).reshape(-1,4).copy()
    donor_positions=vectors(donor,'vertices');donor_normals=vectors(donor,'normals')
    donor_tangents=vectors(donor,'tangents',4)if shading_frame_owner=='receiver'else None
    donor_weights=vectors(donor,'weights',4);donor_indices=np.asarray(donor['boneIndices'],np.int64).reshape(-1,4)
    required=sorted({int(bone)for _,source_index in pairs for bone,weight in zip(donor_indices[source_index],donor_weights[source_index])if weight>0})
    appended=[];poses=list(receiver_poses)
    for bone in required:
        name=donor_bone_paths[bone]
        if name in target:continue
        index=len(poses);target[name]=index;poses.append(donor_poses[bone]@frame)
        appended.append(dict(donor=bone,index=index,path=name))
    output['bindPoses']=np.asarray(poses,np.float32).transpose(0,2,1).ravel().tolist()
    shapes={shape['name']:shape for shape in output['shapes']}
    for source in donor['shapes']:
        if source['name'] not in shapes:
            shape=dict(index=len(output['shapes']),name=source['name'],frames=[dict(weight=f['weight'],**{key:[0.]*(packet['vertexCount']*3)for key in ('vertices','normals','tangents')})for f in source['frames']])
            output['shapes'].append(shape);shapes[shape['name']]=shape
    shape_arrays={}
    for name,shape in shapes.items():
        source=next((s for s in donor['shapes']if s['name']==name),None)
        if source is None:
            source=dict(frames=[dict(weight=f['weight'],**{key:[0.]*(donor['vertexCount']*3)for key in ('vertices','normals','tangents')})for f in shape['frames']])
        if [f['weight']for f in shape['frames']]!=[f['weight']for f in source['frames']]:raise ValueError('Shared physical frame weights differ.')
        shape_arrays[name]=[(frame,source_frame,{key:vectors(frame,key)for key in ('vertices','normals','tangents')},{key:vectors(source_frame,key)for key in ('vertices','normals','tangents')})for frame,source_frame in zip(shape['frames'],source['frames'])]
    maximum_gap=0.;changed_weights=0
    for index,source_index in pairs:
        before=positions[index].copy();old_normal=normals[index].copy();old_tangent=tangents[index,:3].copy()
        desired=points(donor_positions[[source_index]],inverse)[0]
        maximum_gap=max(maximum_gap,float(np.linalg.norm(points(before[None],frame)[0]-donor_positions[source_index])))
        if maximum_gap>maximum_source_gap:raise ValueError('A declared shared cut no longer matches its source position.')
        mapped=[target[donor_bone_paths[bone]]if weight>0 and donor_bone_paths[bone]in target else 0 if weight<=0 else -1 for bone,weight in zip(donor_indices[source_index],donor_weights[source_index])]
        if min(mapped)<0:raise ValueError('A positive donor influence is missing from the receiver palette.')
        changed_weights+=int(not(np.array_equal(weights[index],donor_weights[source_index])and np.array_equal(indices[index],mapped)))
        donor_normal=unit(donor_normals[source_index]@normal_frame)
        desired_normal=old_normal if shading_frame_owner=='receiver'else donor_normal
        desired_tangent=old_tangent if shading_frame_owner=='receiver'else unit(rotate(old_tangent,old_normal,desired_normal))
        rest_basis=None
        if donor_tangents is not None:
            rest_basis=basis(donor_normal,donor_tangents[source_index,:3]@inverse[:3,:3].T)
        positions[index]=desired;normals[index]=desired_normal;tangents[index,:3]=desired_tangent
        weights[index]=donor_weights[source_index];indices[index]=mapped
        for frames in shape_arrays.values():
            for _,_,values,source_values in frames:
                final_position=points((donor_positions[source_index]+source_values['vertices'][source_index])[None],inverse)[0]
                final_normal=unit((donor_normals[source_index]+source_values['normals'][source_index])@normal_frame)
                if rest_basis is not None and donor_tangents is not None:
                    # The existing normal atlas was baked in the receiver's
                    # rest TBN. Follow the donor's physical frame changes
                    # without rotating that atlas a second time at rest.
                    pose_basis=basis(final_normal,(donor_tangents[source_index,:3]+source_values['tangents'][source_index])@inverse[:3,:3].T)
                    rotation=pose_basis@rest_basis.T
                    final_normal=rotation@old_normal;final_tangent=rotation@old_tangent
                else:
                    final_tangent=unit(rotate(old_tangent+values['tangents'][index],old_normal+values['normals'][index],final_normal))
                values['vertices'][index]=final_position-desired;values['normals'][index]=final_normal-desired_normal;values['tangents'][index]=final_tangent-desired_tangent
    for key,values in [('vertices',positions),('normals',normals),('tangents',tangents),('weights',weights),('boneIndices',indices)]:
        output[key]=values.astype(np.int64 if key=='boneIndices'else np.float32).ravel().tolist()
    for frames in shape_arrays.values():
        for destination,_,values,_ in frames:
            for key,value in values.items():destination[key]=value.astype(np.float32).ravel().tolist()
    minimum=positions.min(axis=0);maximum=positions.max(axis=0)
    for shape in output['shapes']:
        for physical in shape['frames']:
            posed=positions+vectors(physical,'vertices');minimum=np.minimum(minimum,posed.min(axis=0));maximum=np.maximum(maximum,posed.max(axis=0))
    old=np.asarray(packet['bounds'],np.float64);minimum=np.minimum(minimum,old[:3]-old[3:]*.5);maximum=np.maximum(maximum,old[:3]+old[3:]*.5)
    center=np.asarray((minimum+maximum)*.5,np.float32)
    extent=np.nextafter(np.asarray(np.maximum(maximum-center,center-minimum),np.float32),np.float32(np.inf))
    output['bounds']=np.r_[center,extent*2].tolist()
    return output,dict(sharedVertices=len(pairs),skinWeightsTransported=changed_weights,appendedBones=appended,maximumSourceGap=maximum_gap,maximumSharedBindError=bind_error,
                       frames=sum(len(s['frames'])for s in output['shapes']),sourceUvAndTopologyUnchanged=True,shadingFrameOwner=shading_frame_owner)


def native_boundary_vertices(packet, slots=None):
    """Open native edges, sharing vertices only for identical skin ownership."""
    positions=np.asarray(packet['vertices'],dtype=np.float32).reshape(-1,3)
    indices=np.asarray(packet['boneIndices']).reshape(-1,4)
    weights=np.asarray(packet['weights'],dtype=np.float32).reshape(-1,4)
    owners={};groups=[]
    for point,bindings,values in zip(positions,indices,weights):
        key=(tuple(point),tuple(sorted((int(i),float(w)) for i,w in zip(bindings,values) if w!=0)))
        groups.append(owners.setdefault(key,len(owners)))
    groups=np.asarray(groups,dtype=np.int64)
    selected=range(len(packet['subMeshes'])) if slots is None else slots
    triangles=np.concatenate([np.asarray(packet['subMeshes'][slot],dtype=np.int64).reshape(-1,3) for slot in selected])
    edges=np.sort(np.concatenate((groups[triangles[:,[0,1]]],groups[triangles[:,[1,2]]],groups[triangles[:,[2,0]]])),axis=1)
    edges=edges[edges[:,0]!=edges[:,1]]
    unique,counts=np.unique(edges,axis=0,return_counts=True)
    return np.isin(groups,np.unique(unique[counts==1]))


def sculpt_normals(original, positions, triangles, source_normals, mask):
    area = np.cross(positions[triangles[:, 1]] - positions[triangles[:, 0]], positions[triangles[:, 2]] - positions[triangles[:, 0]])
    accumulated = np.zeros_like(positions)
    for corner in range(3):
        np.add.at(accumulated, triangles[:, corner], area)
    groups = weld_groups(original)
    welded = np.zeros((int(groups.max()) + 1, 3)); np.add.at(welded, groups, accumulated)
    combined = welded[groups]
    combined /= np.maximum(np.linalg.norm(combined, axis=1, keepdims=True), 1e-12)
    blended = source_normals + (combined - source_normals) * mask[:, None]
    return blended / np.maximum(np.linalg.norm(blended, axis=1, keepdims=True), 1e-12)


def guide_kernel(points, centers, radii):
    distance = np.linalg.norm(points[:, None, :] - centers[None, :, :], axis=2)
    ratio = distance / radii[None, :]
    remaining = np.maximum(0., 1. - ratio)
    return remaining ** 4 * (4. * ratio + 1.)


def compile_guide(profile, transform):
    controls = profile['guideControls']
    centers = points([c['source'] for c in controls], transform)
    targets = points([c['target'] for c in controls], transform)
    radii = np.asarray([c['radiusMetres'] for c in controls], dtype=np.float64)
    weights = np.asarray([c['sourceWeight'] for c in controls], dtype=np.float64)
    if 'compiledGuide' in profile:
        field=profile['compiledGuide']
        centers=np.asarray(field['centers'],dtype=np.float64);radii=np.asarray(field['radii'],dtype=np.float64);coefficients=np.asarray(field['coefficients'],dtype=np.float64)
        for control,target,source in zip(controls,targets,points([c['source'] for c in controls],transform)):
            samples=points(control['sourceTriangleVertices'],transform)
            moved=(guide_kernel(samples,centers,radii)@coefficients)*np.asarray(control['sourceTriangleWeights'])[:,None]
            actual=np.sum(moved*np.asarray(control['sourceTriangleBarycentric'])[:,None],axis=0)
            if np.linalg.norm(actual-(target-source))>.00001:raise ValueError('Constrained source guide misses its declared target by more than 0.01 mm.')
        return centers,radii,coefficients
    kernel = guide_kernel(centers, centers, radii)
    if not np.isfinite(kernel).all() or np.linalg.cond(kernel) > 1e10:
        raise ValueError('Source guide controls are coincident or numerically dependent.')
    coefficients = np.linalg.solve(kernel, (targets - centers) / weights[:, None])
    residual = (kernel @ coefficients) * weights[:, None] - (targets - centers)
    bound = np.finfo(np.float64).eps * np.linalg.cond(kernel) * max(1., np.linalg.norm(targets - centers)) * len(controls) * 8
    if np.max(np.abs(residual)) > bound:
        raise ValueError('Source guide did not interpolate its authored target controls.')
    return centers, radii, coefficients


def evaluate_guide(positions, ownership, compiled):
    centers, radii, coefficients = compiled
    positions = np.asarray(positions, dtype=np.float64)
    ownership = np.asarray(ownership, dtype=np.float64)
    kernel = guide_kernel(positions, centers, radii)
    result = positions + ownership[:, None] * (kernel @ coefficients)
    if not np.isfinite(result).all():raise ValueError('A source guide invalidated its rest deformation field.')
    return result


def sculpt(packet, skin, role, profile=None):
    transform = CONVERT @ matrix(skin['rendererToPrefab'])
    p = points(packet['vertices'], transform)
    bones, origins, marks = landmarks(packet, skin, transform)
    weights = np.asarray(packet['weights']).reshape(-1, 4)
    indices = np.asarray(packet['boneIndices']).reshape(-1, 4)
    delta = np.zeros_like(p)
    report = {'sourceFingerprint': packet['sourceFingerprint'], 'role': role, 'sourceVertices': len(p), 'coordinateDomain': 'native-renderer-rest-via-rendererToPrefab', 'landmarkBoneDomain': 'inverse-native-bindpose', 'currentBonesToPrefabUsedAsRest': False}
    if role in ('eye','lashes'):
        report.update(mode='native-contact-surface',maxDisplacementMm=0.)
        return p,transform,np.zeros(len(p)),report
    if profile is not None:
        report.update(sourceProfileId=profile['id'], coordinateDomain='source-rest-mesh',
                      profileOperation=profile['operation'])
        marks.update({key: points(value, transform)[0] for key, value in profile['restLandmarks'].items()})
        if profile['operation'] == 'source-guide':
            mask = np.asarray(profile['mask'], dtype=np.float64).copy()
            compiled = compile_guide(profile, transform)
            result = evaluate_guide(p, mask, compiled)
            report.update(mode='source-profile-guide-sculpt', guideControls=len(profile['guideControls']),
                          maxDisplacementMm=float(np.max(np.linalg.norm(result-p, axis=1))*1000),
                          normalTransport='actual native/authored triangle surface basis',
                          protectedVertices=int(np.count_nonzero(mask == 0)))
            return result, transform, mask, report
        if profile['operation'] == 'source-lod-transfer':
            report['mode'] = 'profile-awaiting-source-lod-donor'
            return p, transform, np.zeros(len(p)), report
        if profile['operation'] == 'surface-fairing':
            mask = np.asarray(profile['mask'], dtype=np.float64).copy()
            triangles = np.concatenate([np.asarray(x, dtype=np.int64).reshape(-1, 3) for x in packet['subMeshes']])
            edges = np.unique(np.sort(np.concatenate((triangles[:, [0, 1]], triangles[:, [1, 2]], triangles[:, [2, 0]])), axis=1), axis=0)
            degree = np.bincount(edges.ravel(), minlength=len(p)).astype(float)
            normal = native_normals(packet, transform)
            parameters = profile['parameters']
            for _ in range(parameters['iterations']):
                current = p + delta
                average = np.zeros_like(p)
                np.add.at(average, edges[:, 0], current[edges[:, 1]])
                np.add.at(average, edges[:, 1], current[edges[:, 0]])
                average /= np.maximum(degree[:, None], 1)
                movement = average - current
                movement -= np.sum(movement * normal, axis=1)[:, None] * normal
                movement *= np.minimum(1., parameters['maximumStepMm'] * .001 / np.maximum(np.linalg.norm(movement, axis=1), 1e-12))[:, None]
                delta += movement * (mask * parameters['strength'])[:, None]
            delta, groups = coherent_displacements(p, delta, mask)
            report.update(mode='source-profile-surface-fairing', maxDisplacementMm=float(np.max(np.linalg.norm(delta, axis=1))*1000),
                          protectedVertices=int(np.count_nonzero(mask == 0)), weldedSourceGroups=int(groups.max())+1)
            return p + delta, transform, mask, report
    if not all(marks[k] is not None for k in ('left', 'right', 'mouth', 'nose', 'head')):
        report['mode'] = 'native-nonstandard-face-pending-individual-profile'
        return p, transform, np.zeros(len(p)), report
    marks = {key: np.asarray(value, dtype=np.float64) for key, value in marks.items() if value is not None}
    left, right, mouth, nose = (marks[k] for k in ('left', 'right', 'mouth', 'nose'))
    eye = (left + right) * .5
    span = float(np.linalg.norm(left - right))
    height = abs(float(eye[2] - mouth[2]))
    if (profile is None and (not .015 < span < .15 or not .012 < height < .14)) or (profile is not None and (span <= 1e-8 or height <= 1e-8)):
        raise ValueError('Facial anatomy scale is inconsistent with the source: ' + skin['mesh'])

    def bone_weight(contains):
        selected = np.array([any(word in name for word in contains) for name in bones], dtype=np.float64)
        return np.sum(weights * selected[indices], axis=1)

    head_weights = np.array([('bip001 head' in (skin['bones'][i] or '').lower()) and
                             not any(x in name for x in ('neck', 'spine', 'clavicle')) for i, name in enumerate(bones)], dtype=float)
    head_influence = np.sum(weights * head_weights[indices], axis=1)
    front = smooth((eye[1] + span * .75 - p[:, 1]) / max(span * .8, .001))
    neck = smooth((p[:, 2] - (mouth[2] - height * 1.4)) / max(height * .6, .001))
    side = 1.0 - smooth((np.abs(p[:, 0] - eye[0]) - span * 1.2) / max(span * .35, .001))
    mask = smooth((head_influence - .65) / .35) * front * neck * side
    if profile is not None:
        mask = np.asarray(profile['mask'], dtype=np.float64).copy()
    skin_role = role == 'skin'
    eyelids = bone_weight(('eyelid',))
    lips_upper = bone_weight(('lip_upper',))
    lips_lower = bone_weight(('lip_lower',))
    corners = bone_weight(('lip_corner',))
    nostrils = bone_weight(('nostril',))
    cheeks = bone_weight(('cheek_upper', 'cheek_lower'))
    scale = min(1.25, max(.75, span / .064))

    # Per-source proportions determine restrained edits; the original skull,
    # age, ancestry and facial asymmetry are not replaced by a common face.
    if profile is None:
        nose_width = np.linalg.norm(origins[bones.index('b_nostril_l')] - origins[bones.index('b_nostril_r')]) if 'b_nostril_l' in bones and 'b_nostril_r' in bones else span * .35
        bridge_mm = float(np.clip((nose_width / span) * 1.1, .28, .62))
        lip_mm = float(np.clip(height / span * .25, .14, .26))
        eye_factor = float(np.clip((height / span) * .018, .009, .016))
        style = dict(bridgeProjectionMm=bridge_mm, lipReliefMm=lip_mm,
                       eyelidWidthFactor=eye_factor, lidRimMm=.14 * scale,
                       cheekContinuityMm=.28 * scale, planeFairingIterations=2)
    else:
        style = profile['parameters']
        bridge_mm, lip_mm, eye_factor = style['bridgeProjectionMm'], style['lipReliefMm'], style['eyelidWidthFactor']

    if skin_role:
        # Fair each region tangentially. This removes uneven spacing on the
        # forehead/cheek/jaw planes without shrinking the skull silhouette.
        triangles = np.concatenate([np.asarray(t, dtype=np.int64).reshape(-1, 3) for t in packet['subMeshes']])
        edges = np.vstack((triangles[:, [0, 1]], triangles[:, [1, 2]], triangles[:, [2, 0]]))
        edges = np.unique(np.sort(edges, axis=1), axis=0)
        degree = np.bincount(edges.ravel(), minlength=len(p)).astype(float)
        normal = native_normals(packet, transform)
        protection = np.clip(1.0 - np.maximum.reduce((eyelids, lips_upper, lips_lower, corners, nostrils)) * 1.6, 0, 1)
        for _ in range(2):
            working = p + delta
            average = np.zeros_like(p)
            np.add.at(average, edges[:, 0], working[edges[:, 1]])
            np.add.at(average, edges[:, 1], working[edges[:, 0]])
            average /= np.maximum(degree[:, None], 1)
            movement = average - working
            movement -= np.sum(movement * normal, axis=1)[:, None] * normal
            movement *= np.minimum(1.0, .0006 / np.maximum(np.linalg.norm(movement, axis=1), 1e-12))[:, None]
            movement *= (mask * protection * .18)[:, None]
            delta += movement

        # Connected cheek planes and the bridge retain their native width.
        delta[:, 1] -= cheeks * (.00028 * scale if profile is None else style['cheekContinuityMm'] * .001)
        bridge_center = (eye + nose) * .5
        q = (p - bridge_center) / np.array([span * .16, span * .8, height * .55])
        bridge = np.exp(-np.sum(q * q, axis=1) * 1.8)
        delta[:, 1] -= bridge * bridge_mm * .001
        delta[:, 1] -= (lips_upper + lips_lower) * lip_mm * .001
        delta[:, 2] += (lips_upper - lips_lower) * (.00009 * scale if profile is None else style['lipLiftMm'] * .001)
        delta[:, 2] += corners * (.00008 * scale if profile is None else style['cornerLiftMm'] * .001)
        delta[:, 1] -= nostrils * (.00010 * scale if profile is None else style['nostrilProjectionMm'] * .001)

    if role in ('skin', 'lashes'):
        for center, sign in ((left, 1), (right, -1)):
            near = np.exp(-np.sum(((p - center) / np.array([span * .43, span * .55, height * .6])) ** 2, axis=1) * 1.4)
            delta[:, 0] += (p[:, 0] - center[0]) * eye_factor * near * eyelids
        delta[:, 1] -= eyelids * (.00014 * scale if profile is None else style['lidRimMm'] * .001)
    delta *= mask[:, None]
    delta, groups = coherent_displacements(p, delta, mask)
    p = p + delta
    report.update(mode='native-landmark-sculpt-prototype' if profile is None else 'source-profile-feature-sculpt', profile=style,
                  maxDisplacementMm=float(np.max(np.linalg.norm(delta, axis=1)) * 1000),
                  protectedVertices=int(np.count_nonzero(mask == 0)),
                  weldedSourceGroups=int(groups.max()) + 1,
                  protectedDeltaMaximum=float(np.max(np.linalg.norm(delta[mask == 0], axis=1))) if np.any(mask == 0) else 0)
    return p, transform, mask, report


IMAGE_NAMES = {}


def configure_image_names(assets):
    """Allocate Blender image IDs from the selected native texture owners."""
    by_path = {}
    for asset in assets:
        for skin in asset['skins']:
            for material in skin['materials']:
                if not material:
                    continue
                for prop in material['properties']:
                    if not prop.get('textureFile'):
                        continue
                    key = str((source_path(prop['textureFile'])).resolve())
                    label = prop.get('textureName') or material['name'] + '_' + prop['name'].lstrip('_')
                    detail = material['name'] + '_' + prop['name'].lstrip('_')
                    by_path.setdefault(key, set()).add((label, detail))
    entries = []
    for path, owners in sorted(by_path.items()):
        label, detail = min(owners)
        entries.append(resource_naming.ResourceName(path, label, detail=detail))
    names = resource_naming.resource_names(entries)
    if any(len(name.encode('utf-8')) > 63 for name in names.values()):
        raise ValueError('A native image label exceeds this Blender ID name budget.')
    IMAGE_NAMES.clear()
    IMAGE_NAMES.update(names)
    return dict(names)


def source_image(path):
    path = source_path(path).resolve()
    key = str(path)
    if key not in IMAGE_NAMES:
        raise ValueError('The selected native material does not own this image: ' + key)
    image = bpy.data.images.load(str(path), check_existing=True)
    image.name = IMAGE_NAMES[key]
    if image.name != IMAGE_NAMES[key]:
        raise ValueError('A Blender image ID name collision changed its source label.')
    return image


def image_node(material, path, generated_label=None):
    if generated_label is None:
        image = source_image(path)
    else:
        label = resource_naming.source_label(generated_label)
        if len(label.encode('utf-8')) > 63:
            raise ValueError('A generated image label exceeds this Blender ID name budget.')
        image = bpy.data.images.load(str(path), check_existing=True)
        image.name = label
        if image.name != label:
            raise ValueError('A generated image ID name collision changed its declared label.')
    image.colorspace_settings.name = 'Non-Color'
    node = material.node_tree.nodes.new('ShaderNodeTexImage')
    node.image = image
    node.interpolation = 'Linear'
    return node


def math_node(nodes, links, operation, a, b=None):
    node = nodes.new('ShaderNodeMath'); node.operation = operation
    for index, value in enumerate((a, b)):
        if value is None:
            continue
        if isinstance(value, (int, float)):
            node.inputs[index].default_value = value
        else:
            links.new(value, node.inputs[index])
    return node.outputs[0]


def material(spec, improved):
    result = bpy.data.materials.new(spec['name'] + ('_sculpt' if improved else '_native'))
    result.use_nodes = True
    nodes = result.node_tree.nodes
    links = result.node_tree.links
    bsdf = nodes.get('Principled BSDF')
    properties = {p['name']: p for p in spec['properties']}
    base = properties.get('_MainTex')
    if base and base.get('textureFile'):
        color = image_node(result, source_path(base['textureFile']))
        links.new(color.outputs['Color'], bsdf.inputs['Base Color'])
        if 'Unlit' in spec['shader']:
            links.new(color.outputs['Alpha'], bsdf.inputs['Alpha'])
            result.surface_render_method = 'DITHERED'
    else:
        bsdf.inputs['Base Color'].default_value = (.47, .31, .22, 1)
    if spec['shader'] == 'Custom/CharacterEye':
        bsdf.inputs['Roughness'].default_value = .16 if improved else .3
        bsdf.inputs['Coat Weight'].default_value = .3
        bsdf.inputs['Coat Roughness'].default_value = .08
    else:
        bsdf.inputs['Roughness'].default_value = .48 if improved else .5
        bsdf.inputs['Subsurface Weight'].default_value = .055
        bsdf.inputs['Subsurface Radius'].default_value = (1, .4, .2)
    normal = properties.get('_BumpMap')
    if normal and normal.get('textureFile'):
        tex = image_node(result, source_path(normal['textureFile']))
        separate = nodes.new('ShaderNodeSeparateColor')
        links.new(tex.outputs['Color'], separate.inputs['Color'])
        multiply = nodes.new('ShaderNodeMath'); multiply.operation = 'MULTIPLY'
        links.new(separate.outputs['Red'], multiply.inputs[0]); links.new(tex.outputs['Alpha'], multiply.inputs[1])
        combine = nodes.new('ShaderNodeCombineColor')
        links.new(multiply.outputs[0], combine.inputs['Red']); links.new(separate.outputs['Green'], combine.inputs['Green'])
        nx = math_node(nodes, links, 'SUBTRACT', math_node(nodes, links, 'MULTIPLY', multiply.outputs[0], 2), 1)
        ny = math_node(nodes, links, 'SUBTRACT', math_node(nodes, links, 'MULTIPLY', separate.outputs['Green'], 2), 1)
        length = math_node(nodes, links, 'ADD', math_node(nodes, links, 'MULTIPLY', nx, nx), math_node(nodes, links, 'MULTIPLY', ny, ny))
        nz = math_node(nodes, links, 'SQRT', math_node(nodes, links, 'MAXIMUM', math_node(nodes, links, 'SUBTRACT', 1, length), 0))
        blue = math_node(nodes, links, 'ADD', math_node(nodes, links, 'MULTIPLY', nz, .5), .5)
        links.new(blue, combine.inputs['Blue'])
        normal_map = nodes.new('ShaderNodeNormalMap'); normal_map.inputs['Strength'].default_value = .65 if improved else 1
        links.new(combine.outputs['Color'], normal_map.inputs['Color']); links.new(normal_map.outputs[0], bsdf.inputs['Normal'])
    return result


def make_object(asset, skin, improved):
    packet = load_packet(skin)
    presentation = skin.get('_sourcePresentation')
    if presentation is None or presentation['sourceFingerprint'] != packet['sourceFingerprint']:
        raise ValueError('A source-owned anatomical presentation contract is required.')
    role = presentation['role']
    transformed, transform, mask, report = sculpt(packet, skin, role, skin.get('_faceProfile'))
    if not improved:
        transformed = points(packet['vertices'], transform)
    triangles = np.concatenate([np.array(t).reshape(-1, 3) for t in packet['subMeshes']])
    ns = native_normals(packet, transform)
    cross = np.cross(transformed[triangles[:, 1]] - transformed[triangles[:, 0]], transformed[triangles[:, 2]] - transformed[triangles[:, 0]])
    dot = np.sum(cross * ns[triangles].mean(axis=1), axis=1)
    reverse = np.median(dot) < 0
    if reverse:
        triangles = triangles[:, ::-1]
    mesh = bpy.data.meshes.new(skin['mesh'])
    mesh.from_pydata(transformed.tolist(), [], triangles.tolist()); mesh.update()
    uv = np.array(next(u for u in packet['uv'] if u['channel'] == 0)['values']).reshape(-1, 4)[:, :2]
    layer = mesh.uv_layers.new(name='UVMap')
    layer.data.foreach_set('uv', uv[np.array([loop.vertex_index for loop in mesh.loops])].ravel())
    for polygon in mesh.polygons:
        polygon.use_smooth = True
    if not improved:
        mesh.normals_split_custom_set_from_vertices(ns.tolist())
    else:
        original = points(packet['vertices'], transform)
        normal = sculpt_normals(original, transformed, triangles, ns, mask)
        mesh.normals_split_custom_set_from_vertices(normal.tolist())
    obj = bpy.data.objects.new(skin['path'], mesh); bpy.context.collection.objects.link(obj)
    for item in skin['materials']:
        if item:
            mesh.materials.append(material(item, improved))
    material_index = np.concatenate([np.full(len(t) // 3, i) for i, t in enumerate(packet['subMeshes'])])
    mesh.polygons.foreach_set('material_index', material_index)
    obj['native_source_fingerprint'] = packet['sourceFingerprint']
    obj['role'] = role
    return obj, report


def setup_scene():
    bpy.ops.object.select_all(action='SELECT'); bpy.ops.object.delete(use_global=False)
    scene = bpy.context.scene
    scene.render.engine = 'CYCLES'; scene.cycles.samples = 8
    scene.cycles.use_denoising = True; scene.render.threads_mode = 'FIXED'; scene.render.threads = 2
    scene.render.resolution_x = 512; scene.render.resolution_y = 512; scene.render.resolution_percentage = 100
    scene.render.image_settings.file_format = 'PNG'; scene.render.film_transparent = False
    scene.world.color = (.12, .12, .12)
    scene.view_settings.view_transform = 'AgX'
    scene.view_settings.look = 'AgX - Medium High Contrast'
    return scene


def source_face_frame(face_scope):
    if not isinstance(face_scope, dict) or face_scope.get('state') != 'SOURCE_OWNED_FACE' or not face_scope.get('sources'):
        raise ValueError('Face review is pending: no source-owned face scope.')
    coords = np.asarray(face_scope.get('nativeCoordinates', []), dtype=np.float64)
    if coords.ndim != 2 or coords.shape[1:] != (3,) or len(coords) < 3 or not np.all(np.isfinite(coords)):
        raise ValueError('Face review requires finite selected native face coordinates.')
    minimum, maximum = coords.min(axis=0), coords.max(axis=0)
    center = (minimum + maximum) * .5
    scale = 0.
    camera = face_scope['camera']
    if [v['name'] for v in camera['views']] != ['front','oblique','side'] or not 1<camera['margin']<=2 or type(camera['resolution']) is not int or camera['resolution']<512:
        raise ValueError('Invalid source-owned presentation camera.')
    for view in camera['views']:
        direction = view['direction']
        forward = np.asarray(direction, dtype=float); forward /= np.linalg.norm(forward)
        right = np.cross(forward, [0., 0., 1.]); right /= np.linalg.norm(right)
        up = np.cross(right, forward)
        relative = coords - center
        scale = max(scale, 2*float(np.max(np.abs(relative@right))), 2*float(np.max(np.abs(relative@up))))
    if not np.isfinite(scale) or scale <= 1e-8:
        raise ValueError('Face review requires a nondegenerate source-owned region.')
    return dict(target=center.tolist(), scale=scale*camera['margin'], minimum=minimum.tolist(), maximum=maximum.tolist(),
                coordinateDomain=face_scope['coordinateDomain'], scope='full-native-head-and-attachments-fixed-across-phases', resolution=camera['resolution'], views=camera['views'])


def render_asset(asset, destination, improved, face_scope=None, variant=None):
    if face_scope is None: raise ValueError('A source face presentation is required.')
    frame = source_face_frame(face_scope)
    scene = setup_scene(); objects = []; reports = []
    scene.render.resolution_x = frame['resolution']; scene.render.resolution_y = frame['resolution']
    presentations = {p['path']: p for p in face_scope['presentations']}
    for skin in asset['skins']:
        presentation = presentations.get(skin['path'])
        if presentation is None:
            continue
        if skin.get('_sourcePresentation') != presentation:
            raise ValueError('Preview renderer does not match its source-owned presentation scope.')
        obj, report = make_object(asset, skin, improved); objects.append(obj); reports.append(report)
    if not any(o['role'] == 'skin' for o in objects):
        raise ValueError('Face review has no source-owned face renderer in this asset.')
    target = Vector(frame['target'])
    scale = frame['scale']
    for name, position, energy, size in [('Key', (-1.2, -2, 2.8), 160, 1.8), ('Fill', (1.6, -1, 2), 100, 1.6), ('Rim', (0, 1, 2.5), 180, 1.2)]:
        data = bpy.data.lights.new(name, 'AREA'); data.energy = energy; data.shape = 'DISK'; data.size = size
        lamp = bpy.data.objects.new(name, data); bpy.context.collection.objects.link(lamp); lamp.location = position
        lamp.rotation_euler = (target - lamp.location).to_track_quat('-Z', 'Y').to_euler()
    camera_data = bpy.data.cameras.new('SourceMatchedCamera'); camera_data.type = 'ORTHO'; camera_data.ortho_scale = scale
    camera = bpy.data.objects.new('SourceMatchedCamera', camera_data); bpy.context.collection.objects.link(camera); scene.camera = camera
    variant = ('sculpt' if improved else 'native') if variant is None else variant
    destination.mkdir(parents=True, exist_ok=True)
    for view_spec in frame['views']:
        view, direction = view_spec['name'], view_spec['direction']
        camera.location = target + Vector(direction).normalized() * 2
        camera.rotation_euler = (target - camera.location).to_track_quat('-Z', 'Y').to_euler()
        path = destination / (asset['name'] + '-' + variant + '-' + view + '.png')
        scene.render.filepath = str(path); bpy.ops.render.render(write_still=True); preview_receipt(path, view=view)
    blend = destination / (asset['name'] + '-' + variant + '.blend')
    bpy.ops.wm.save_as_mainfile(filepath=str(blend), check_existing=False)
    return reports


def main():
    # The model-owned production entry owns source selection and authoring.
    # This module remains the geometry/render helper, not a parallel pilot list.
    import production
    production.main()


if __name__ == '__main__':
    main()
