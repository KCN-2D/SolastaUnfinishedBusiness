"""Common surface attachment transport contracts; no model-specific inputs."""
import copy
import gzip
import importlib.util
import io
from pathlib import Path
import struct
import tempfile
import unittest

path = Path(__file__).resolve().parents[1] / 'UnityAssetBuild/mesh_recipe_codec.py'
spec = importlib.util.spec_from_file_location('mesh_recipe_codec', path)
codec = importlib.util.module_from_spec(spec)
spec.loader.exec_module(codec)


class SurfaceBindingContracts(unittest.TestCase):
    def setUp(self):
        self.binding = dict(schemaVersion=1, sourceFingerprint='ab' * 32,
                            rendererToPrefab=[1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1],
                            clothBindingGenerated=True, vertices=[
            dict(anchor=0, native=True, role='source', slot=0, uv0=[0, 0],
                 offset=[0, 0, 0], normal=[0, 0, 1], tangent=[1, 0, 0], tangentW=-1),
            dict(anchor=0, native=False, role='attached surface', slot=1, uv0=[.25, .75],
                 offset=[1e-20, -.001, .002], normal=[0, 0, 1], tangent=[1, 0, 0], tangentW=1)])

    def test_independent_wire_layout_and_runtime_float_bits(self):
        original = copy.deepcopy(self.binding)
        with tempfile.TemporaryDirectory() as tmp:
            target = Path(tmp) / 'surface.binding.bytes'
            result = codec.encode_surface_binding(self.binding, target)
            packed = target.read_bytes()
        self.assertEqual(self.binding, original)
        raw = gzip.decompress(packed)
        self.assertEqual(result['decodedBytes'], len(raw))
        stream = io.BytesIO(raw)
        self.assertEqual(stream.read(8), b'UBSFB001')
        self.assertEqual(stream.read(32), bytes.fromhex(original['sourceFingerprint']))
        self.assertEqual(stream.read(1), b'\x01')
        self.assertEqual(stream.read(64), struct.pack('<16f', *original['rendererToPrefab']))
        integer = lambda: struct.unpack('<i', stream.read(4))[0]
        role_count = integer()
        roles = [stream.read(integer()).decode('utf-8') for _ in range(role_count)]
        self.assertEqual(roles, ['source', 'attached surface'])
        self.assertEqual(integer(), len(original['vertices']))
        for vertex in original['vertices']:
            self.assertEqual(integer(), vertex['anchor'])
            self.assertEqual(stream.read(1), bytes([int(vertex['native'])]))
            self.assertEqual(roles[integer()], vertex['role'])
            self.assertEqual(integer(), vertex['slot'])
            self.assertEqual(stream.read(48), struct.pack('<12f', *(vertex['uv0'] + vertex['offset']
                + vertex['normal'] + vertex['tangent'] + [vertex['tangentW']])))
        self.assertEqual(stream.read(), b'')

    def test_deterministic_and_existing_output_is_preserved(self):
        with tempfile.TemporaryDirectory() as tmp:
            left, right = [Path(tmp) / name for name in ('left.bytes', 'right.bytes')]
            codec.encode_surface_binding(self.binding, left)
            codec.encode_surface_binding(self.binding, right)
            before = left.read_bytes()
            self.assertEqual(before, right.read_bytes())
            with self.assertRaises(FileExistsError):
                codec.encode_surface_binding(self.binding, left)
            self.assertEqual(before, left.read_bytes())

    def test_invalid_input_never_publishes_payload(self):
        variants = []
        for field, value in [('sourceFingerprint', 'wrong'), ('schemaVersion', 2),
                             ('clothBindingGenerated', 1), ('vertices', []),
                             ('rendererToPrefab', [0] * 16)]:
            item = copy.deepcopy(self.binding); item[field] = value; variants.append(item)
        for field, value in [('anchor', -1), ('slot', -1), ('native', 1), ('role', ' '),
                             ('tangentW', 0), ('uv0', [1]), ('offset', [float('nan'), 0, 0]),
                             ('offset', [1e39, 0, 0]), ('offset', [0, 0, .01])]:
            item = copy.deepcopy(self.binding); item['vertices'][0][field] = value; variants.append(item)
        item = copy.deepcopy(self.binding); item['unexpected'] = True; variants.append(item)
        with tempfile.TemporaryDirectory() as tmp:
            for index, binding in enumerate(variants):
                target = Path(tmp) / ('invalid-' + str(index) + '.bytes')
                with self.subTest(index=index), self.assertRaises((ValueError, OverflowError)):
                    codec.encode_surface_binding(binding, target)
                self.assertFalse(target.exists())


if __name__ == '__main__':
    unittest.main()
