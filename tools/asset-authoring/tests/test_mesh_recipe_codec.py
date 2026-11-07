"""Common surface attachment transport contracts; no model-specific inputs."""
import copy
import gzip
import importlib.util
import io
from pathlib import Path
from typing import Any
import struct
import tempfile
import unittest
from unittest import mock

path = Path(__file__).resolve().parents[1] / 'UnityAssetBuild/mesh_recipe_codec.py'
spec = importlib.util.spec_from_file_location('mesh_recipe_codec', path)
assert spec is not None and spec.loader is not None
codec = importlib.util.module_from_spec(spec)
spec.loader.exec_module(codec)


class RecipeFailureContracts(unittest.TestCase):
    @staticmethod
    def recipe() -> dict[str, Any]:
        return dict(schemaVersion=1,bundleName='owned_mesh.bundle',meshes=[dict(name='Owned',vertices=[0.,0.,0.,1.,0.,0.,0.,1.,0.],
                    normals=[0.,0.,1.]*3,tangents=[1.,0.,0.,1.]*3,uvChannels=[],subMeshes=[dict(topology='Triangles',indices=[0,1,2])],bounds=dict(center=[.5,.5,0.],size=[1.,1.,0.]),indexFormat='UInt16')])

    def test_size_limit_is_checked_before_output_creation(self):
        with tempfile.TemporaryDirectory()as folder,mock.patch.object(codec,'LIMIT',64):
            target=Path(folder)/'Recipe.json'
            with self.assertRaisesRegex(ValueError,'Numeric payload exceeds'):
                codec.encode_recipe(self.recipe(),target)
            self.assertFalse(target.exists())
            self.assertFalse((target.parent/'Data/mesh-numeric.bin').exists())

    def test_invalid_numeric_and_json_data_release_owned_partial_files(self):
        for fault in ('numeric','json'):
            recipe=self.recipe()
            if fault=='numeric':recipe['meshes'][0]['vertices'][4]=float('nan')
            else:recipe['textures']=[float('nan')]
            with self.subTest(fault=fault),tempfile.TemporaryDirectory()as folder:
                target=Path(folder)/'Recipe.json'
                with self.assertRaises(ValueError):codec.encode_recipe(recipe,target)
                self.assertFalse(target.exists())
                self.assertFalse((target.parent/'Data/mesh-numeric.bin').exists())

    def test_preexisting_payload_is_preserved(self):
        with tempfile.TemporaryDirectory()as folder:
            target=Path(folder)/'Recipe.json';payload=target.parent/'Data/mesh-numeric.bin';payload.parent.mkdir();payload.write_bytes(b'foreign bytes')
            with self.assertRaises(ValueError):codec.encode_recipe(self.recipe(),target)
            self.assertEqual(payload.read_bytes(),b'foreign bytes')


class SurfaceBindingContracts(unittest.TestCase):
    def setUp(self):
        self.binding: dict[str, Any] = dict(schemaVersion=1, sourceFingerprint='ab' * 32,
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

    def multi_source_binding(self) -> dict[str, Any]:
        binding = copy.deepcopy(self.binding)
        binding['schemaVersion'] = 2
        binding['clothBindingGenerated'] = False
        first = binding['vertices'][0]
        first['sources'] = [dict(anchor=0, weight=1)]
        second = copy.deepcopy(first)
        second['anchor'] = 1
        second['sources'] = [dict(anchor=1, weight=1)]
        added = binding['vertices'][1]
        added['sources'] = [dict(anchor=0, weight=.5), dict(anchor=1, weight=.5)]
        binding['vertices'] = [first, second, added]
        return binding

    def test_multi_source_wire_preserves_native_prefix_and_midpoint_provenance(self):
        binding = self.multi_source_binding()
        with tempfile.TemporaryDirectory() as tmp:
            target = Path(tmp) / 'midpoint.bytes'
            codec.encode_surface_binding(binding, target)
            stream = io.BytesIO(gzip.decompress(target.read_bytes()))
        self.assertEqual(stream.read(8), b'UBSFB002')
        self.assertEqual(stream.read(32), bytes.fromhex(binding['sourceFingerprint']))
        self.assertEqual(stream.read(1), b'\0')
        self.assertEqual(stream.read(64), struct.pack('<16f', *binding['rendererToPrefab']))
        integer = lambda: struct.unpack('<i', stream.read(4))[0]
        roles = [stream.read(integer()).decode('utf-8') for _ in range(integer())]
        self.assertEqual(integer(), 3)
        for vertex in binding['vertices']:
            self.assertEqual(integer(), vertex['anchor'])
            self.assertEqual(stream.read(1), bytes([int(vertex['native'])]))
            self.assertEqual(roles[integer()], vertex['role'])
            self.assertEqual(integer(), vertex['slot'])
            self.assertEqual(stream.read(48), struct.pack('<12f', *(vertex['uv0'] + vertex['offset']
                + vertex['normal'] + vertex['tangent'] + [vertex['tangentW']])))
            self.assertEqual(integer(), len(vertex['sources']))
            for source in vertex['sources']:
                self.assertEqual(integer(), source['anchor'])
                self.assertEqual(stream.read(4), struct.pack('<f', source['weight']))
        self.assertEqual(stream.read(), b'')

    def test_invalid_multi_source_provenance_does_not_publish_payload(self):
        base = self.multi_source_binding()
        variants = []
        for sources in ([], [dict(anchor=0, weight=1), dict(anchor=0, weight=1)],
                        [dict(anchor=2, weight=1)], [dict(anchor=-1, weight=1)],
                        [dict(anchor=0, weight=0)], [dict(anchor=0, weight=float('nan'))],
                        [dict(anchor=0, weight=1), dict(anchor=1, weight=1e-46)],
                        [dict(anchor=0, weight=.25)], [dict(anchor=0, weight=1, extra=True)]):
            item = copy.deepcopy(base); item['vertices'][2]['sources'] = sources; variants.append(item)
        for change in ('missing', 'nonprefix', 'prefixanchor', 'prefixmix', 'cloth'):
            item = copy.deepcopy(base)
            if change == 'missing': del item['vertices'][2]['sources']
            elif change == 'nonprefix': item['vertices'].append(copy.deepcopy(item['vertices'][0]))
            elif change == 'prefixanchor': item['vertices'][0]['anchor'] = 1
            elif change == 'prefixmix': item['vertices'][0]['sources'] = [dict(anchor=0, weight=.5)]
            else: item['clothBindingGenerated'] = True
            variants.append(item)
        with tempfile.TemporaryDirectory() as tmp:
            for index, binding in enumerate(variants):
                target = Path(tmp) / ('invalid-multi-' + str(index) + '.bytes')
                with self.subTest(index=index), self.assertRaises((ValueError, OverflowError)):
                    codec.encode_surface_binding(binding, target)
                self.assertFalse(target.exists())

    def explicit_surface_binding(self) -> dict[str, Any]:
        binding = copy.deepcopy(self.binding)
        binding['schemaVersion'] = 3
        binding['sourceVertexCount'] = 3
        first = binding['vertices'][0]
        first['surface'] = None
        native = [copy.deepcopy(first) for _ in range(3)]
        for index, vertex in enumerate(native):
            vertex['anchor'] = index
        added = binding['vertices'][1]
        added['surface'] = dict(triangle=[2, 0, 1], barycentric=[.25, .5, .25])
        binding['vertices'] = native + [added]
        return binding

    def test_explicit_surface_wire_retains_triangle_order_and_float_bits(self):
        binding = self.explicit_surface_binding()
        before = copy.deepcopy(binding)
        with tempfile.TemporaryDirectory() as tmp:
            target = Path(tmp) / 'explicit.bytes'
            codec.encode_surface_binding(binding, target)
            stream = io.BytesIO(gzip.decompress(target.read_bytes()))
        self.assertEqual(binding, before)
        self.assertEqual(stream.read(8), b'UBSFB003')
        self.assertEqual(stream.read(32), bytes.fromhex(binding['sourceFingerprint']))
        self.assertEqual(stream.read(1), b'\x01')
        self.assertEqual(stream.read(64), struct.pack('<16f', *binding['rendererToPrefab']))
        integer = lambda: struct.unpack('<i', stream.read(4))[0]
        self.assertEqual(integer(), 3)
        roles = [stream.read(integer()).decode('utf-8') for _ in range(integer())]
        self.assertEqual(integer(), 4)
        for vertex in binding['vertices']:
            self.assertEqual(integer(), vertex['anchor'])
            self.assertEqual(stream.read(1), bytes([int(vertex['native'])]))
            self.assertEqual(roles[integer()], vertex['role'])
            self.assertEqual(integer(), vertex['slot'])
            self.assertEqual(stream.read(48), struct.pack('<12f', *(vertex['uv0'] + vertex['offset']
                + vertex['normal'] + vertex['tangent'] + [vertex['tangentW']])))
            surface = vertex['surface']
            self.assertEqual(stream.read(1), bytes([int(surface is not None)]))
            if surface is not None:
                self.assertEqual([integer() for _ in range(3)], surface['triangle'])
                self.assertEqual(stream.read(12), struct.pack('<3f', *surface['barycentric']))
        self.assertEqual(stream.read(), b'')

    def test_geometry_frame_keeps_texture_provenance_and_rejects_native_or_invalid_frames(self):
        binding=self.explicit_surface_binding()
        binding['schemaVersion']=4
        for vertex in binding['vertices']:vertex['geometrySurface']=None
        geometry=dict(triangle=[0,1,2],barycentric=[.5,.25,.25])
        binding['vertices'][3]['geometrySurface']=geometry
        before=copy.deepcopy(binding)
        with tempfile.TemporaryDirectory() as tmp:
            target=Path(tmp)/'geometry.bytes'
            codec.encode_surface_binding(binding,target)
            data=gzip.decompress(target.read_bytes())
            self.assertEqual(data[:8],b'UBSFB004')
            self.assertTrue(data.endswith(b'\x01'+struct.pack('<3i3f',0,1,2,.5,.25,.25)))
            for change in ('native','duplicate','range','sum','nan'):
                bad=copy.deepcopy(binding)
                if change=='native':bad['vertices'][0]['geometrySurface']=geometry
                elif change=='duplicate':bad['vertices'][3]['geometrySurface']['triangle']=[0,0,2]
                elif change=='range':bad['vertices'][3]['geometrySurface']['triangle']=[0,1,3]
                elif change=='sum':bad['vertices'][3]['geometrySurface']['barycentric']=[0,0,0]
                else:bad['vertices'][3]['geometrySurface']['barycentric']=[float('nan'),.5,.5]
                with self.subTest(change=change),self.assertRaises(ValueError):
                    codec.encode_surface_binding(bad,Path(tmp)/(change+'.bytes'))
        self.assertEqual(binding,before)

    def test_explicit_vertex_attachment_permits_zero_corner_weights(self):
        binding = self.explicit_surface_binding()
        binding['vertices'][3]['surface']['barycentric'] = [0, 1, 0]
        with tempfile.TemporaryDirectory() as tmp:
            result = codec.encode_surface_binding(binding, Path(tmp) / 'vertex.bytes')
        self.assertEqual(result['vertices'], 4)

    def test_invalid_explicit_provenance_does_not_publish_payload(self):
        base = self.explicit_surface_binding()
        variants = []
        for field, value in [('triangle', [0, 0, 1]), ('triangle', [0, 1, 3]),
                             ('triangle', [0, 1]), ('triangle', [True, 1, 2]),
                             ('barycentric', [-.1, .6, .5]), ('barycentric', [.2, .2, .2]),
                             ('barycentric', [float('nan'), 0, 1]), ('barycentric', [0, 1]),
                             ('unexpected', True)]:
            item = copy.deepcopy(base)
            item['vertices'][3]['surface'][field] = value
            variants.append(item)
        for change in ('countMissing', 'countBool', 'countTooSmall', 'countTooLarge',
                       'nativeSurface', 'addedMissing', 'prefixAnchor', 'prefixFlag', 'unownedAnchor'):
            item = copy.deepcopy(base)
            if change == 'countMissing': del item['sourceVertexCount']
            elif change == 'countBool': item['sourceVertexCount'] = True
            elif change == 'countTooSmall': item['sourceVertexCount'] = 0
            elif change == 'countTooLarge': item['sourceVertexCount'] = 5
            elif change == 'nativeSurface': item['vertices'][0]['surface'] = item['vertices'][3]['surface']
            elif change == 'addedMissing': item['vertices'][3]['surface'] = None
            elif change == 'prefixAnchor': item['vertices'][1]['anchor'] = 0
            elif change == 'prefixFlag': item['vertices'][0]['native'] = False
            else:
                item['sourceVertexCount'] = 4
                native = copy.deepcopy(item['vertices'][0]); native['anchor'] = 3
                item['vertices'].insert(3, native)
                item['vertices'][4]['anchor'] = 3
            variants.append(item)
        item = copy.deepcopy(self.binding); item['sourceVertexCount'] = 1; variants.append(item)
        with tempfile.TemporaryDirectory() as tmp:
            for index, binding in enumerate(variants):
                target = Path(tmp) / ('invalid-explicit-' + str(index) + '.bytes')
                with self.subTest(index=index), self.assertRaises((ValueError, OverflowError)):
                    codec.encode_surface_binding(binding, target)
                self.assertFalse(target.exists())


if __name__ == '__main__':
    unittest.main()
