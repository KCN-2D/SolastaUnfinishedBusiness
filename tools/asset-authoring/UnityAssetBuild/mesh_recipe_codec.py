"""Lossless binary32/int32 transport for the Unity mesh recipe input owner."""
import argparse
import hashlib
import json
import math
import re
import struct
from pathlib import Path

FORMAT = 'Solasta.MeshNumeric.1'
MAGIC = b'UBMSH001'
LIMIT = 64 * 1024 * 1024


def need(value, message):
    if not value:
        raise ValueError(message)


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
    fields(recipe, ('schemaVersion', 'bundleName', 'meshes'), ('textures',))
    need(recipe['schemaVersion'] == 1 and isinstance(recipe['meshes'], list)
         and 1 <= len(recipe['meshes']) <= 128, 'Invalid mesh recipe version/count.')
    need(re.fullmatch(r'[A-Za-z0-9_./-]+', payload_relative) is not None
         and not payload_relative.startswith('/') and all(p not in ('', '.', '..') for p in payload_relative.split('/'))
         and payload_relative.endswith('.bin'), 'Invalid relative numeric payload path.')
    json_path = Path(json_path).resolve()
    payload_path = json_path.parent / payload_relative
    need(not json_path.exists() and not payload_path.exists(), 'Output recipe and payload must be new.')
    json_path.parent.mkdir(parents=True, exist_ok=True)
    payload_path.parent.mkdir(parents=True, exist_ok=True)
    need(payload_path.resolve().is_relative_to(json_path.parent), 'Numeric payload escaped its recipe directory.')
    writer = Writer(payload_path)
    try:
        writer.write(MAGIC)
        writer.integer(1)
        writer.integer(len(recipe['meshes']))
        for mesh in recipe['meshes']:
            write_mesh(writer, mesh)
    finally:
        writer.close()
    external = {key: value for key, value in recipe.items() if key != 'meshes'}
    external['numericPayload'] = dict(format=FORMAT, path=payload_relative,
                                      sha256=writer.hash.hexdigest(), byteLength=writer.length)
    data = json.dumps(external, ensure_ascii=False, separators=(',', ':'), allow_nan=False).encode('utf-8') + b'\n'
    need(len(data) <= LIMIT, 'Mesh recipe JSON exceeds 64 MiB.')
    with json_path.open('xb') as output:
        output.write(data)
    return dict(recipe=str(json_path), payload=str(payload_path), bytes=writer.length,
                sha256=writer.hash.hexdigest(), meshes=len(recipe['meshes']))


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--input', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--payload-path', default='Data/mesh-numeric.bin')
    args = parser.parse_args()
    with args.input.open('r', encoding='utf-8-sig') as source:
        original = json.load(source)
    print(json.dumps(encode_recipe(original, args.output, args.payload_path)))
