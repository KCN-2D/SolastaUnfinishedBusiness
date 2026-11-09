"""Lossless binary32/int32 transport for the Unity mesh recipe input owner."""
import argparse
import gzip
import hashlib
import io
import json
import math
import re
import struct
import zlib
from pathlib import Path

FORMAT = 'Solasta.MeshNumeric.1'
MAGIC = b'UBMSH001'
LIMIT = 64 * 1024 * 1024
SURFACE_MAGIC = b'UBSFB001'
SURFACE_MULTI_SOURCE_MAGIC = b'UBSFB002'
SURFACE_EXPLICIT_SOURCE_MAGIC = b'UBSFB003'
SURFACE_GEOMETRY_FRAME_MAGIC = b'UBSFB004'


def skin_bone_order(bone_paths: list[str], bone_hashes: list[int]) -> list[int]:
    """Resolve serialized mesh slots against full prefab-relative bone paths."""
    if any(not isinstance(path,str)or not path or path.startswith('/')or path.endswith('/')or '//'in path for path in bone_paths):
        raise ValueError('A skin palette has an invalid prefab-relative bone path.')
    if not bone_hashes:return list(range(len(bone_paths)))
    if len(bone_hashes)!=len(bone_paths)or any(type(value)is not int or not 0<=value<=0xffffffff for value in bone_hashes):
        raise ValueError('Serialized bone name hashes do not cover the native skin palette.')
    parts=[path.split('/')for path in bone_paths];orders=set()
    for prefix in range(min(map(len,parts))):
        if any(path[:prefix]!=parts[0][:prefix]for path in parts):break
        owners: dict[int,dict[str,int]]={}
        for index,path in enumerate(parts):
            relative='/'.join(path[prefix:]);owners.setdefault(zlib.crc32(relative.encode('utf-8')),{}).setdefault(relative,index)
        matches=[owners.get(value,{})for value in bone_hashes]
        if all(len(found)==1 for found in matches):orders.add(tuple(next(iter(found.values()))for found in matches))
    if len(orders)!=1:raise ValueError('A serialized bone name hash has missing or ambiguous transform ownership.')
    return list(next(iter(orders)))


def skin_palette_frame(packet,donor,bone_paths,donor_bone_paths,tolerance=1e-4):
    """Match bone slots by path and bind pose without merging native aliases."""
    import numpy as np
    if not math.isfinite(tolerance)or tolerance<=0:raise ValueError('A skin palette needs a finite positive frame tolerance.')
    if any(not isinstance(path,str)or not path for path in (*bone_paths,*donor_bone_paths)):raise ValueError('A skin palette has an invalid bone path.')
    def canonical(path):
        parts=path.split('/');return '/'.join(parts[parts.index('Bip001'):]if'Bip001'in parts else parts)
    bone_paths=[canonical(path)for path in bone_paths];donor_bone_paths=[canonical(path)for path in donor_bone_paths]
    receiver_poses=np.asarray(packet['bindPoses'],float).reshape(-1,4,4).transpose(0,2,1)
    donor_poses=np.asarray(donor['bindPoses'],float).reshape(-1,4,4).transpose(0,2,1)
    if len(receiver_poses)!=len(bone_paths)or len(donor_poses)!=len(donor_bone_paths):
        raise ValueError('A skin palette has incomplete native bone slots.')
    if not np.isfinite(receiver_poses).all()or not np.isfinite(donor_poses).all():raise ValueError('A skin palette has nonfinite native bind poses.')
    common=[(i,j,name)for i,name in enumerate(donor_bone_paths)for j,other in enumerate(bone_paths)if name==other]
    if not common:raise ValueError('Skin cuts have no shared native bind frame.')
    names={name for _,_,name in common};candidates=[]
    for first,second,_ in common:
        try:frame=np.linalg.inv(donor_poses[first])@receiver_poses[second]
        except np.linalg.LinAlgError:continue
        errors=[(name,float(np.abs(receiver_poses[j]-donor_poses[i]@frame).max()))for i,j,name in common]
        if not np.isfinite(frame).all()or any(min(error for path,error in errors if path==name)>tolerance for name in names):continue
        if not any(float(np.abs(frame-old).max())<=tolerance for old in candidates):candidates.append(frame)
    if len(candidates)!=1:raise ValueError('Skin cut parts have incompatible or ambiguous native bind frames.')
    frame=candidates[0];mapped={};errors=[]
    for index,name in enumerate(donor_bone_paths):
        matches=[(float(np.abs(receiver_poses[j]-donor_poses[index]@frame).max()),j)for j,other in enumerate(bone_paths)if name==other]
        compatible=[(error,j)for error,j in matches if error<=tolerance]
        if compatible:
            error,j=min(compatible);mapped[index]=j;errors.append(error)
    return frame,mapped,max(errors,default=0.)



def need(value, message):
    if not value:
        raise ValueError(message)


def four_slot_skin(weights, indices, count):
    """Expand compact serialized skin streams to Unity's BoneWeight API slots."""
    def expand(rows, zero):
        if rows is None or len(rows)==0:return []
        need(len(rows)==count,'A skin stream has a different vertex count.')
        result=[]
        for row in rows:
            need(len(row)<=4,'A mesh has more than four serialized skin influences.')
            result.extend((*row,*([zero]*(4-len(row)))))
        return result
    expanded_weights=expand(weights,0.);expanded_indices=expand(indices,0)
    if expanded_indices and not expanded_weights:
        expanded_weights=[value for _ in range(count)for value in (1.,0.,0.,0.)]
    if expanded_weights and not expanded_indices:expanded_indices=[0]*(count*4)
    return expanded_weights,expanded_indices


def fields(value, required, optional=()):
    need(isinstance(value, dict) and set(required) <= set(value)
         and set(value) <= set(required) | set(optional), 'Unknown or missing mesh recipe field.')


class Writer:
    def __init__(self, path):
        self.stream = Path(path).open('xb')
        self.hash = hashlib.sha256()
        self.length = 0

    def write(self, data):
        need(self.length + len(data) <= LIMIT, 'Numeric payload exceeds 64 MiB.')
        self.stream.write(data)
        self.hash.update(data)
        self.length += len(data)

    def integer(self, value):
        need(type(value) is int and -2147483648 <= value <= 2147483647, 'Expected an int32 value.')
        self.write(struct.pack('<i', value))

    def text(self, value):
        need(type(value) is str, 'Expected a UTF8 name.')
        data = value.encode('utf-8', errors='strict')
        self.integer(len(data))
        self.write(data)

    def floats(self, values):
        need(isinstance(values, (list, tuple)), 'Expected a numeric array.')
        self.integer(len(values))
        for first in range(0, len(values), 4096):
            chunk = values[first:first + 4096]
            need(all(type(value) in (int, float) and math.isfinite(value) for value in chunk), 'Nonfinite or nonnumeric float32 data.')
            data = struct.pack('<' + 'f' * len(chunk), *chunk)
            need(all(math.isfinite(value[0]) for value in struct.iter_unpack('<f', data)), 'Float32 overflow.')
            self.write(data)

    def integers(self, values):
        need(isinstance(values, (list, tuple)), 'Expected an int32 array.')
        self.integer(len(values))
        for first in range(0, len(values), 4096):
            chunk = values[first:first + 4096]
            need(all(type(value) is int and -2147483648 <= value <= 2147483647 for value in chunk), 'Invalid int32 data.')
            self.write(struct.pack('<' + 'i' * len(chunk), *chunk))

    def close(self):
        self.stream.close()


class SizeWriter(Writer):
    """Count the wire layout before creating files or converting numeric arrays."""
    def __init__(self):
        self.length=0

    def write(self,data):
        self.account(len(data))

    def account(self,length):
        need(self.length+length<=LIMIT,'Numeric payload exceeds 64 MiB.')
        self.length+=length

    def floats(self,values):
        need(isinstance(values,(list,tuple)),'Expected a numeric array.')
        self.account(4+4*len(values))

    def integers(self,values):
        need(isinstance(values,(list,tuple)),'Expected an int32 array.')
        self.account(4+4*len(values))


def write_mesh(writer, mesh):
    fields(mesh, ('name', 'vertices', 'normals', 'tangents', 'uvChannels', 'subMeshes', 'bounds', 'indexFormat'),
           ('colors', 'skin', 'blendShapes'))
    writer.text(mesh['name'])
    for channel in ('vertices', 'normals', 'tangents'):
        writer.floats(mesh[channel])
    writer.floats(mesh.get('colors', []))
    need(isinstance(mesh['uvChannels'], list) and len(mesh['uvChannels']) <= 8, 'Invalid UV count.')
    writer.integer(len(mesh['uvChannels']))
    for uv in mesh['uvChannels']:
        fields(uv, ('channel', 'dimension', 'values'))
        writer.integer(uv['channel'])
        writer.integer(uv['dimension'])
        writer.floats(uv['values'])
    need(isinstance(mesh['subMeshes'], list) and 1 <= len(mesh['subMeshes']) <= 256, 'Invalid submesh count.')
    writer.integer(len(mesh['subMeshes']))
    for submesh in mesh['subMeshes']:
        fields(submesh, ('topology', 'indices'))
        writer.text(submesh['topology'])
        writer.integers(submesh['indices'])
    fields(mesh['bounds'], ('center', 'size'))
    writer.floats(mesh['bounds']['center'])
    writer.floats(mesh['bounds']['size'])
    writer.text(mesh['indexFormat'])
    skin = mesh.get('skin')
    writer.integer(0 if skin is None else 1)
    if skin is not None:
        fields(skin, ('bindPoses', 'boneWeights', 'boneIndices'))
        writer.floats(skin['bindPoses'])
        writer.floats(skin['boneWeights'])
        writer.integers(skin['boneIndices'])
    shapes = mesh.get('blendShapes') or []
    need(isinstance(shapes, list), 'Invalid physical shape list.')
    writer.integer(len(shapes))
    for shape in shapes:
        fields(shape, ('name', 'frames'))
        writer.text(shape['name'])
        need(isinstance(shape['frames'], list) and shape['frames'], 'Missing physical frames.')
        writer.integer(len(shape['frames']))
        for frame in shape['frames']:
            fields(frame, ('weight', 'vertices', 'normals', 'tangents'))
            writer.floats([frame['weight']])
            for channel in ('vertices', 'normals', 'tangents'):
                writer.floats(frame[channel])


def encode_recipe(recipe, json_path, payload_relative='Data/mesh-numeric.bin'):
    """Write a new external recipe without changing input geometry or input objects."""
    fields(recipe, ('schemaVersion', 'bundleName', 'meshes'), ('textures', 'sourceBundles'))
    need(recipe['schemaVersion'] == 1 and isinstance(recipe['meshes'], list)
         and 1 <= len(recipe['meshes']) <= 128, 'Invalid mesh recipe version/count.')
    need(isinstance(recipe['bundleName'], str) and
         re.fullmatch(r'[a-z0-9][a-z0-9_.-]{0,127}', recipe['bundleName']) is not None,
         'Invalid mesh recipe bundle name.')
    need(re.fullmatch(r'[A-Za-z0-9_./-]+', payload_relative) is not None
         and not payload_relative.startswith('/') and all(p not in ('', '.', '..') for p in payload_relative.split('/'))
         and payload_relative.endswith('.bin'), 'Invalid relative numeric payload path.')
    counter=SizeWriter();counter.write(MAGIC);counter.integer(1);counter.integer(len(recipe['meshes']))
    for mesh in recipe['meshes']:write_mesh(counter,mesh)
    json_path = Path(json_path).resolve()
    payload_path = json_path.parent / payload_relative
    need(not json_path.exists() and not payload_path.exists(), 'Output recipe and payload must be new.')
    json_path.parent.mkdir(parents=True, exist_ok=True)
    payload_path.parent.mkdir(parents=True, exist_ok=True)
    need(payload_path.resolve().is_relative_to(json_path.parent), 'Numeric payload escaped its recipe directory.')
    writer = Writer(payload_path)
    json_created=False
    try:
        try:
            writer.write(MAGIC)
            writer.integer(1)
            writer.integer(len(recipe['meshes']))
            for mesh in recipe['meshes']:write_mesh(writer, mesh)
        finally:writer.close()
        external = {key: value for key, value in recipe.items() if key != 'meshes'}
        external['numericPayload'] = dict(format=FORMAT, path=payload_relative,
                                          sha256=writer.hash.hexdigest(), byteLength=writer.length)
        data = json.dumps(external, ensure_ascii=False, separators=(',', ':'), allow_nan=False).encode('utf-8') + b'\n'
        need(len(data) <= LIMIT, 'Mesh recipe JSON exceeds 64 MiB.')
        with json_path.open('xb') as output:
            json_created=True;output.write(data)
    except BaseException:
        payload_path.unlink(missing_ok=True)
        if json_created:json_path.unlink(missing_ok=True)
        raise
    return dict(recipe=str(json_path), payload=str(payload_path), bytes=writer.length,
                sha256=writer.hash.hexdigest(), meshes=len(recipe['meshes']))


def encode_surface_binding(binding, output_path):
    """Keep attachment fields in compressed binary32 form, as consumed at runtime."""
    fields(binding, ('schemaVersion', 'sourceFingerprint', 'rendererToPrefab',
                     'vertices', 'clothBindingGenerated'), ('sourceVertexCount',))
    version = binding['schemaVersion']
    need(type(version) is int and version in (1, 2, 3, 4) and type(binding['clothBindingGenerated']) is bool,
         'Invalid surface binding version or cloth flag.')
    need(('sourceVertexCount' in binding) == (version >= 3), 'Unexpected or missing native source count.')
    need(isinstance(binding['sourceFingerprint'], str)
         and re.fullmatch('[0-9a-f]{64}', binding['sourceFingerprint']), 'Invalid surface source identity.')
    need(isinstance(binding['vertices'], list) and binding['vertices'], 'Missing surface vertices.')
    matrix = binding['rendererToPrefab']
    need(isinstance(matrix, list) and len(matrix) == 16, 'Invalid surface transform.')
    need(all(type(v) in (int, float) and math.isfinite(v) for v in matrix), 'Invalid surface transform values.')
    need(matrix[3] == matrix[7] == matrix[11] == 0 and matrix[15] == 1, 'Non-affine surface transform.')
    determinant = (matrix[0] * (matrix[5] * matrix[10] - matrix[9] * matrix[6])
                   - matrix[4] * (matrix[1] * matrix[10] - matrix[9] * matrix[2])
                   + matrix[8] * (matrix[1] * matrix[6] - matrix[5] * matrix[2]))
    need(math.isfinite(determinant) and determinant != 0, 'Singular surface transform.')
    roles = {}
    source_count = 0
    if version == 2:
        need(not binding['clothBindingGenerated'],
             'Multi-source skin provenance requires a noncloth source contract.')
        for vertex in binding['vertices']:
            need(isinstance(vertex, dict), 'Invalid surface vertex.')
            if vertex.get('native') is not True:
                break
            source_count += 1
        need(source_count > 0, 'Multi-source attachments require an exact native prefix.')
    elif version >= 3:
        source_count = binding['sourceVertexCount']
        need(type(source_count) is int and 0 < source_count <= len(binding['vertices']),
             'Invalid explicit surface source count.')
    for index, vertex in enumerate(binding['vertices']):
        fields(vertex, ('anchor', 'native', 'role', 'slot', 'uv0', 'offset',
                        'normal', 'tangent', 'tangentW') +
               (('sources',) if version == 2 else ('surface', 'geometrySurface') if version == 4 else ('surface',) if version == 3 else ()))
        need(type(vertex['anchor']) is int and 0 <= vertex['anchor'] <= 2147483647
             and type(vertex['slot']) is int and 0 <= vertex['slot'] <= 2147483647,
             'Invalid surface anchor or slot.')
        need(type(vertex['native']) is bool and isinstance(vertex['role'], str)
             and vertex['role'].strip(), 'Invalid surface role or native flag.')
        roles.setdefault(vertex['role'], len(roles))
        need(vertex['tangentW'] in (-1, 1), 'Invalid tangent handedness.')
        for field, length in (('uv0', 2), ('offset', 3), ('normal', 3), ('tangent', 3)):
            need(isinstance(vertex[field], list) and len(vertex[field]) == length,
                 'Invalid surface coordinate dimensions.')
        if vertex['native']:
            need(vertex['offset'] == [0, 0, 0] and vertex['normal'] == [0, 0, 1]
                 and vertex['tangent'] == [1, 0, 0], 'Native surface correspondence changed.')
        if version >= 3:
            need(vertex['anchor'] < source_count and
                 (index >= source_count or vertex['native'] and vertex['anchor'] == index),
                 'Explicit surface native prefix changed.')
            surface = vertex['surface']
            present = not vertex['native'] and index >= source_count
            need((surface is not None) == present, 'Explicit surface provenance is missing or unexpected.')
            if present:
                fields(surface, ('triangle', 'barycentric'))
                triangle, bary = surface['triangle'], surface['barycentric']
                need(isinstance(triangle, list) and len(triangle) == 3 and
                     all(type(anchor) is int and 0 <= anchor < source_count for anchor in triangle) and
                     len(set(triangle)) == 3 and vertex['anchor'] in triangle,
                     'Invalid explicit native triangle.')
                need(isinstance(bary, list) and len(bary) == 3 and
                     all(type(weight) in (int, float) and math.isfinite(weight) and 0 <= weight <= 1
                         for weight in bary), 'Invalid explicit barycentric coordinates.')
                actual = struct.unpack('<3f', struct.pack('<3f', *bary))
                need(abs(sum(actual) - 1) <= 1e-6, 'Explicit barycentric weights do not sum to one.')
            if version == 4:
                geometry = vertex['geometrySurface']
                need(geometry is None or present, 'A native reference cannot own a geometry frame.')
                if geometry is not None:
                    fields(geometry, ('triangle', 'barycentric'))
                    triangle, bary = geometry['triangle'], geometry['barycentric']
                    need(isinstance(triangle, list) and len(triangle) == len(set(triangle)) == 3 and
                         all(type(i) is int and 0 <= i < source_count for i in triangle),
                         'Invalid geometry-frame native triangle.')
                    need(isinstance(bary, list) and len(bary) == 3 and
                         all(type(w) in (int, float) and math.isfinite(w) and 0 <= w <= 1 for w in bary),
                         'Invalid geometry-frame barycentric coordinates.')
                    actual = struct.unpack('<3f', struct.pack('<3f', *bary))
                    need(abs(sum(actual)-1) <= 1e-6, 'Geometry-frame weights do not sum to one.')
        if version == 2:
            need(vertex['native'] == (index < source_count)
                 and (not vertex['native'] or vertex['anchor'] == index),
                 'Multi-source native prefix changed.')
            sources = vertex['sources']
            need(isinstance(sources, list) and 1 <= len(sources) <= 4,
                 'Invalid native surface contribution count.')
            anchors = set()
            total = 0
            for source in sources:
                fields(source, ('anchor', 'weight'))
                need(type(source['anchor']) is int and 0 <= source['anchor'] < source_count
                     and source['anchor'] not in anchors, 'Invalid or duplicate native contribution anchor.')
                need(type(source['weight']) in (int, float) and math.isfinite(source['weight'])
                     and 0 < source['weight'] <= 1, 'Invalid native contribution weight.')
                packed = struct.pack('<f', source['weight'])
                actual = struct.unpack('<f', packed)[0]
                need(math.isfinite(actual) and actual > 0, 'Native contribution float32 overflow or underflow.')
                total += actual
                anchors.add(source['anchor'])
            need(vertex['anchor'] == sources[0]['anchor'] and abs(total - 1) <= 1e-6,
                 'Native contribution weights or primary anchor differ.')
            if vertex['native']:
                need(sources == [dict(anchor=index, weight=1)], 'Native prefix contribution changed.')
    stream = io.BytesIO()

    def write(data):
        need(stream.tell() + len(data) <= LIMIT, 'Surface payload exceeds 64 MiB.')
        stream.write(data)

    def integer(value):
        write(struct.pack('<i', value))

    def floats(values):
        need(all(type(v) in (int, float) and math.isfinite(v) for v in values),
             'Nonfinite surface coordinate.')
        data = struct.pack('<' + 'f' * len(values), *values)
        need(all(math.isfinite(v[0]) for v in struct.iter_unpack('<f', data)), 'Surface float32 overflow.')
        write(data)

    write({1: SURFACE_MAGIC, 2: SURFACE_MULTI_SOURCE_MAGIC, 3: SURFACE_EXPLICIT_SOURCE_MAGIC,
           4: SURFACE_GEOMETRY_FRAME_MAGIC}[version])
    write(bytes.fromhex(binding['sourceFingerprint']))
    write(bytes([int(binding['clothBindingGenerated'])]))
    floats(matrix)
    if version >= 3:
        integer(source_count)
    integer(len(roles))
    for role in roles:
        data = role.encode('utf-8', errors='strict')
        integer(len(data))
        write(data)
    integer(len(binding['vertices']))
    for vertex in binding['vertices']:
        integer(vertex['anchor'])
        write(bytes([int(vertex['native'])]))
        integer(roles[vertex['role']])
        integer(vertex['slot'])
        floats(vertex['uv0'] + vertex['offset'] + vertex['normal'] + vertex['tangent']
               + [vertex['tangentW']])
        if version == 2:
            integer(len(vertex['sources']))
            for source in vertex['sources']:
                integer(source['anchor'])
                floats([source['weight']])
        elif version >= 3:
            surface = vertex['surface']
            write(bytes([int(surface is not None)]))
            if surface is not None:
                for anchor in surface['triangle']:
                    integer(anchor)
                floats(surface['barycentric'])
            if version == 4:
                geometry = vertex['geometrySurface']
                write(bytes([int(geometry is not None)]))
                if geometry is not None:
                    for anchor in geometry['triangle']:
                        integer(anchor)
                    floats(geometry['barycentric'])
    raw = stream.getvalue()
    compressed = gzip.compress(raw, compresslevel=6, mtime=0)
    output_path = Path(output_path)
    output_path.parent.mkdir(parents=True, exist_ok=True)
    with output_path.open('xb') as output:
        output.write(compressed)
    return dict(payload=str(output_path), bytes=len(compressed), decodedBytes=len(raw),
                sha256=hashlib.sha256(compressed).hexdigest(), vertices=len(binding['vertices']))


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--input', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--payload-path', default='Data/mesh-numeric.bin')
    parser.add_argument('--surface-binding', action='store_true')
    args = parser.parse_args()
    with args.input.open('r', encoding='utf-8-sig') as source:
        original = json.load(source)
    print(json.dumps(encode_surface_binding(original, args.output) if args.surface_binding
                     else encode_recipe(original, args.output, args.payload_path)))
