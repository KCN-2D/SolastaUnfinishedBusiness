"""Facial source ownership and the required authored-guide boundary."""
import contextlib
import copy
import datetime
import gzip
import hashlib
import importlib.util
import io
import json
import shutil
import sys
from pathlib import Path
import tempfile
from types import SimpleNamespace
from typing import Any
import unittest
import zlib
from unittest import mock

import numpy as np


source = Path(__file__).resolve().parents[2] / 'blender-models/CharacterFaces/build_models.py'
spec = importlib.util.spec_from_file_location('face_build_contracts', source)
assert spec is not None and spec.loader is not None
build = importlib.util.module_from_spec(spec)
spec.loader.exec_module(build)
PACKAGE_TEST_VERSION=datetime.date.min.isoformat().replace('-','.')+'.1'


class FaceProductionContracts(unittest.TestCase):
    def test_character_scene_header_accepts_blender_compression(self):
        from compression import zstd

        with tempfile.TemporaryDirectory() as folder:
            scene = Path(folder) / 'Skin.blend'
            for encode in (bytes, gzip.compress, zstd.compress):
                scene.write_bytes(encode(b'BLENDER-v405'))
                self.assertTrue(build.is_blender_scene(scene))
                scene.write_bytes(encode(b'Another file'))
                self.assertFalse(build.is_blender_scene(scene))

    def test_sculpted_face_transports_stationary_corners_and_original_pose_shading(self):
        from character_mesh_authoring import update_retained_mesh

        surface, _ = build.source_owner()
        base = np.array([[0, 0, 0], [1, 0, 0], [0, 1, 0], [0, 0, 0],
                         [0, -1, 0], [1, 0, 0], [2, 0, 0], [3, 0, 0], [2, 1, 0]], dtype=np.float32)
        triangles = np.array([[0, 1, 2], [3, 4, 5], [6, 7, 8]])
        bent = base.copy()
        bent[2, 2] = .25
        frame = dict(weight=100, vertices=[0., .25, 0.] * len(base),
                     normals=[0., .25, 0.] * len(base), tangents=[0., .125, 0.] * len(base))
        packet = dict(vertices=base.ravel().tolist(), subMeshes=[triangles.ravel().tolist()],
                      normals=[0., 0., 1.] * len(base), tangents=[1., 0., 0., 1.] * len(base),
                      shapes=[dict(name='Expression', frames=[frame])])
        result = update_retained_mesh(packet, bent, triangles, surface)
        normals = np.asarray(result['normals']).reshape(-1, 3)
        pose = result['shapes'][0]['frames'][0]
        pose_normals = np.asarray(pose['normals']).reshape(-1, 3)
        self.assertLess(normals[0, 1], -.05)
        np.testing.assert_array_equal(normals[0], normals[3])
        np.testing.assert_array_equal(pose_normals[0], pose_normals[3])
        self.assertNotEqual(pose_normals[0, 2], 0.)
        np.testing.assert_array_equal(normals[6:], [[0, 0, 1]] * 3)
        self.assertEqual(pose['vertices'], frame['vertices'])
        self.assertEqual(packet['shapes'][0]['frames'][0]['normals'], [0., .25, 0.] * len(base))

    def test_a_new_skin_chart_preserves_pose_normals_and_transfers_tangent_twist(self):
        from character_mesh_authoring import update_face_uv

        surface, _ = build.source_owner()
        base = np.array([[0, 0, 0], [1, 0, 0], [0, 1, 0]], dtype=np.float32)
        previous = [1., 0., 0., 1.] * 3
        frame = dict(weight=100, vertices=[0., .125, 0.] * 3, normals=[.25, 0., 0.] * 3,
                     tangents=[0., .25, 0.] * 3)
        packet = dict(vertexCount=3, vertices=base.ravel().tolist(), subMeshes=[[0, 1, 2]],
                      normals=[0., 0., 1.] * 3, tangents=previous, weights=[], boneIndices=[], colors=[], uv=[],
                      shapes=[dict(name='Expression', frames=[frame])])
        aliases = np.array([0, 1, 2, 0])
        art = dict(source_vertices=aliases, positions=base[aliases], triangles=np.array([[3, 1, 2]]),
                   material_slots=np.array([1]), tangents=np.array(previous + [0., 1., 0., -1.]).reshape(-1, 4))
        result = update_face_uv(packet, art, surface, 0)
        pose = result['shapes'][0]['frames'][0]
        self.assertEqual(pose['normals'], [.25, 0., 0.] * 4)
        self.assertEqual(pose['vertices'], [0., .125, 0.] * 4)
        self.assertEqual(pose['tangents'], [0., .25, 0.] * 3 + [-.25, 0., 0.])

    def test_body_size_frame_uses_face_geometry_and_preserves_skin_interfaces(self):
        surface, _ = build.source_owner()
        base = np.array([[0, 0, 0], [1, 0, 0], [0, 1, 0], [0, 0, 0],
                         [0, -1, 0], [1, 0, 0]], dtype=np.float32)
        offsets = np.zeros_like(base)
        offsets[2, 2] = .25
        frame = dict(weight=100, vertices=offsets.ravel().tolist(),
                     normals=[0., 0., -2.] * 6, tangents=[-.5, 0., 0.] * 6)
        expression = dict(index=1, name='Expression', frames=[copy.deepcopy(frame)])
        packet = dict(vertexCount=6, indexFormat=0, vertices=base.ravel().tolist(), colors=[],
                      bindPoses=[], subMeshes=[[0, 1, 2, 3, 4, 5]], uv=[],
                      normals=[0., 0., 1.] * 6, tangents=[1., 0., 0., 1.] * 6,
                      boneIndices=[1, 0, 0, 0] * 4 + [0] * 4 + [1, 0, 0, 0],
                      weights=[1., 0., 0., 0.] * 6,
                      shapes=[dict(index=0, name='Muscular', frames=[frame]), expression])
        bones = ('Bip001/Bip001 Neck', 'Bip001/Bip001 Neck/Bip001 Head')
        result, facts = surface.repair_head_body_frame_shading(packet, bones, protected_vertices=(1,))
        actual = result['shapes'][0]['frames'][0]
        pose_normals = np.asarray(actual['normals']).reshape(-1, 3) + [0, 0, 1]
        self.assertEqual(facts[0]['vertices'], 3)
        self.assertGreater(pose_normals[0, 2], .96)
        self.assertLess(pose_normals[0, 1], -.08)
        np.testing.assert_array_equal(pose_normals[0], pose_normals[3])
        np.testing.assert_array_equal(pose_normals[[1, 4, 5]], [[0, 0, -1]] * 3)
        self.assertEqual(actual['vertices'], frame['vertices'])
        self.assertIs(result['shapes'][1], expression)
        repeated, changes = surface.repair_head_body_frame_shading(result, bones, protected_vertices=(1,))
        self.assertIs(repeated, result)
        self.assertEqual(changes, [])
        for key in ('vertices', 'normals', 'tangents', 'weights', 'boneIndices', 'subMeshes'):
            self.assertIs(result[key], packet[key])

    def test_surface_bounds_keep_every_touching_candidate(self):
        production, _ = build.source_owner()
        rng = np.random.default_rng(216)
        skin_low = rng.uniform(-1, 1, (250, 3))
        skin_high = skin_low + rng.uniform(0, .6, skin_low.shape)
        low = np.r_[skin_high[[16]], rng.uniform(-1, 1, (70, 3))]
        high = low + rng.uniform(0, .5, low.shape)
        chosen = production.overlapping_surface_faces(low, high, skin_low, skin_high)
        self.assertIn(16, chosen[0])
        for a, b, hits in zip(low, high, chosen, strict=True):
            np.testing.assert_array_equal(hits, np.flatnonzero(((skin_high >= a) & (skin_low <= b)).all(axis=1)))

    def test_matching_skin_charts_share_color_without_touching_unowned_texels(self):
        surface, _ = build.source_owner()
        points = [[0, 0, 0], [1, 0, 0], [0, 1, 0], [1, 0, 0], [0, 0, 0], [0, -1, 0]]
        texels = [[.10, .30], [.44, .30], [.31, .45], [.91, .70], [.57, .70], [.70, .52]]
        uv = [coordinate for x, y in texels for coordinate in (x, y, 0., 0.)]
        packet = dict(vertices=np.asarray(points).ravel().tolist(), normals=[0, 0, 1] * 6,
                      subMeshes=[[0, 1, 2, 3, 4, 5]], uv=[dict(channel=0, values=uv)])
        pairs = surface.skin_chart_boundary_pairs(packet, 0)
        self.assertEqual(pairs.shape, (1, 2, 3, 2))
        pixels = np.full((64, 64, 4), 130, np.uint8)
        pixels[:, 32:, :3] = 162
        pixels[:, :, 3] = np.arange(64)
        owned = np.ones((64, 64), bool)
        owned[:18, :] = False
        result, proof = surface.reconcile_skin_chart_color(pixels, owned, pairs)
        self.assertGreater(proof['samples'], 15)
        self.assertGreater(result[20, 14, 0], 140)
        self.assertLess(result[43, 45, 0], 152)
        np.testing.assert_array_equal(result[:, :, 3], pixels[:, :, 3])
        np.testing.assert_array_equal(result[~owned], pixels[~owned])
        height, width = 61, 63
        odd_pixels = pixels[:height, :width]
        window = surface.skin_chart_raster_window(pairs, (height, width))[::-1]
        full, _ = surface.reconcile_skin_chart_color(odd_pixels, owned[:height, :width], pairs)
        restricted, _ = surface.reconcile_skin_chart_color(odd_pixels, owned[:height, :width] & window, pairs)
        np.testing.assert_array_equal(full, restricted)

    def test_scalp_fit_updates_pinned_rest_with_clearance_to_the_head(self):
        surface, _ = build.source_owner()
        frame = np.eye(4)
        points = np.array([[.002, .002, .003], [.012, .002, .003], [.002, .012, .003]], dtype=np.float32)
        head = np.array([[[0., 0., 0.], [.03, 0., 0.], [0., .03, 0.]]])
        packet = dict(vertexCount=3, indexFormat=0, vertices=points.ravel().tolist(), subMeshes=[[0, 1, 2]],
                      normals=[0., 0., 1.] * 3, tangents=[1., 0., 0., 1.] * 3, uv=[], colors=[],
                      bindPoses=frame.ravel().tolist(), weights=[1., 0., 0., 0.] * 3,
                      boneIndices=[0] * 12, shapes=[], bounds=[.007, .007, .003, .01, .01, 0.])
        observed=dict(vertices=packet['vertices'], submeshes=packet['subMeshes'], skinMaterials=[False],
                      localToWorld=frame.ravel().tolist(),
                      bones=[dict(name='Head', localToWorld=frame.ravel().tolist())])
        guide=dict(vertexCount=3, vertices=[0], offsets=[[0., 0., -.006]])
        equipment, count = surface.fit_posed_garment(packet, observed, head, ['Rig/Head'], frame,
            clearance=.001, maximum_offset=.02, head_surface=head, fixed_vertices=(0,), groom_guide=guide)
        self.assertIs(equipment, packet)
        self.assertEqual(count, 0)
        fitted, count = surface.fit_posed_garment(packet, observed, head, ['Rig/Head'], frame,
            clearance=.001, maximum_offset=.02, head_surface=head, fixed_vertices=(0,),
            groom_guide=dict(guide, contactSurface='head'))
        self.assertEqual(count, 1)
        np.testing.assert_allclose(np.asarray(fitted['vertices']).reshape(-1, 3)[:, 2], [.001, .003, .003], atol=2e-9)
        self.assertEqual(fitted['weights'], packet['weights'])

    def test_face_curvature_retains_fixed_neighbours_in_the_displacement_energy(self):
        production, _ = build.source_owner()
        edges = np.array([[0, 1], [1, 2], [2, 3], [2, 5], [3, 4], [4, 5], [5, 6]])
        lengths = np.linspace(.2, .9, len(edges))
        mass = np.linspace(.3, .7, 7)
        free = np.array([2, 3])
        anchored = np.array([0, 1, 4, 5, 6])
        modes = np.array([0, 0, 1, 2, 0])
        laplace = np.zeros((7, 7))
        for (a, b), weight in zip(edges, lengths, strict=True):
            laplace[a, a] += weight
            laplace[b, b] += weight
            laplace[a, b] -= weight
            laplace[b, a] -= weight
        energy = laplace.T @ (laplace / mass[:, None])
        expected = np.linalg.solve(energy[np.ix_(free, free)],
            -energy[np.ix_(free, anchored)] @ np.eye(3)[modes])
        actual = production.handle_curvature_coordinates(edges, lengths, mass, free, anchored, modes, 3)
        np.testing.assert_allclose(actual, expected, atol=1e-14)

    def test_garment_fit_catches_a_skin_triangle_crossing_between_vertices(self):
        production, _ = build.source_owner()
        cloth = np.array([[0., 0., 0.], [1., 0., 0.], [0., 1., 0.],
                         [1., 0., 0.], [2., 0., 0.], [1., 1., 0.]])
        triangles = np.array([[0, 1, 2], [3, 4, 5]])
        skin = np.array([[[-1., .2, -.004], [2., .2, .004], [.5, 2., .004]]])
        fit = production.fit_garment_crossings(cloth, triangles, skin, clearance=.0006, maximum_offset=.006)
        np.testing.assert_array_equal(fit[1], fit[3])
        self.assertGreater(fit[0, 2], 0.)
        for indices in triangles:
            face = (cloth + fit)[indices]
            normal = production.normalise(np.cross(face[1] - face[0], face[2] - face[0]))
            self.assertFalse(any(low < 0 < high for low, high in production.garment_crossing_depth(face, skin, normal)))
        separate = skin.copy()
        separate[:, :, 2] -= .1
        np.testing.assert_array_equal(production.fit_garment_crossings(cloth, triangles, separate,
            clearance=.0006, maximum_offset=.006), np.zeros_like(cloth))

    def test_garment_clearance_continues_across_seams_on_the_same_piece(self):
        production, _ = build.source_owner()
        points = np.array([[0., 0., 0.], [.01, 0., 0.], [.02, 0., 0.],
                           [0., .001, 0.], [.01, .001, 0.]])
        edges = np.array([[0, 1], [1, 2], [3, 4]])
        required = np.array([.011, 0., .001, 0., 0.])
        result = production.continue_garment_clearance(required, points, edges)
        np.testing.assert_allclose(result, [.011, .0074, .0038, 0., 0.], atol=1e-12)
        np.testing.assert_array_equal(required, [.011, 0., .001, 0., 0.])

    def test_garment_coverage_uses_the_nearest_fold_and_preserves_the_open_hem(self):
        production, _ = build.source_owner()
        lining = np.array([[0., 0., 0.], [.1, 0., 0.], [0., .1, 0.]])
        shell = np.array([lining + [0., 0., .004], lining + [0., 0., -.012]])
        faces = np.array([lining, lining + [.12, 0., 0.], lining + [0., 0., .008]])
        np.testing.assert_array_equal(production.garment_inside_faces(shell, faces, .03), [True, False, False])

    def test_appearance_capture_pairs_gear_with_its_own_skin_detail_level(self):
        def part(key, detail, height, skin):
            return dict(renderer=f'Mesh_LOD{detail}', mesh=dict(id=key, name=f'Mesh_LOD{detail}', bindPoses=[]), forceRenderingOff=False,
                        vertices=[0, 0, height, 1, 0, height, 0, 1, height], submeshes=[[0, 1, 2]], skinMaterials=[skin],
                        localToWorld=np.eye(4).ravel().tolist())
        drawn = [part(1, 0, 0, False), part(2, 1, 0, False), part(3, 0, 1, True), part(4, 1, 2, True)]
        slots = [dict(shape=False, source='gear', meshes=[dict(meshId=1), dict(meshId=2)]),
                 dict(shape=True, source='body', meshes=[dict(meshId=3), dict(meshId=4)])]
        observations = list(build.observed_garment_surfaces(dict(actor=dict(nativeParts=slots), meshes=drawn), {'gear'}))
        self.assertEqual([part['mesh'] for _, part, _ in observations], ['Mesh_LOD0', 'Mesh_LOD1'])
        for level, (_, _, skin) in enumerate(observations, 1):
            np.testing.assert_array_equal(skin[:, :, 2], [[level, level, level]])
        lining = part(5, 0, .003, False)
        lining['renderer'] = 'Lining_LOD0'
        # The same renderer supplies a lining and a belt that belongs outside.
        lining['vertices'] += [.1, .1, .006, .4, .1, .006, .1, .4, .006]
        lining['submeshes'] = [[0, 1, 2, 3, 4, 5]]
        reference_lining = dict(lining, vertices=[*part(5, 0, -.003, False)['vertices'], *lining['vertices'][9:]])
        slots[0]['meshes'].append(dict(meshId=5))
        observations = build.observed_garment_surfaces(dict(actor=dict(nativeParts=slots), meshes=[*drawn, lining]),
                                                       {'gear'}, {('Lining', 'Mesh')},
                                                       layer_reference=dict(meshes=[*drawn, reference_lining]))
        _, outer, surfaces = next(observations)
        self.assertEqual(outer['fittedLayers'], [('Lining', 'Mesh')])
        np.testing.assert_array_equal(surfaces[:, :, 2], [[1, 1, 1], [.003, .003, .003]])

    def test_garment_fit_tracks_rotation_of_simulated_cloth_after_skinning(self):
        production, _ = build.source_owner()
        points = np.eye(3)
        identity = np.eye(4).ravel().tolist()
        packet = dict(bindPoses=identity, shapes=[], weights=[1., 0., 0., 0.] * 3, boneIndices=[0] * 12)
        observed = dict(bones=[dict(name='Spine', localToWorld=identity)], observedBindPoses=[identity])
        rotation = np.array([[0., -1., 0.], [1., 0., 0.], [0., 0., 1.]])
        posed = points @ rotation.T + [.1, .2, .3]
        matrices, unbound = production.garment_skinning_frame(
            packet, observed, points, posed, np.array([[0, 1, 2]]), ['Rig/Spine'], .0006, np.eye(4))
        self.assertFalse(unbound.any())
        np.testing.assert_allclose(matrices, np.broadcast_to(rotation, (3, 3, 3)), atol=1e-12)

    def test_skin_job_binds_the_highest_detail_physical_owner_and_requires_native_coverage_without_metric_exclusion(self):
        production,_=build.source_owner();profile=dict(pigmentRetain=.2,coherenceSigmaMetres=.01,featherMetres=.03)
        owner=dict(rendererToPrefab=np.eye(4).T.ravel().tolist())
        first=dict(name='Head_LOD0',sourceFingerprint='a'*64);lower=dict(name='Head_LOD1',sourceFingerprint='b'*64)
        surfaces=[(owner,lower,['Rig/Head']),(owner,first,['Rig/Head'])]
        shape=(4,8);ownership=np.ones(shape);metric=np.full(shape,.001)
        sampler=dict(filterMode=2,wrapU=1,wrapV=2,mipLevel=3,anisoLevel=1)
        with mock.patch.object(production,'prepare_source_neighbourhoods',return_value=([],{}))as prepare:
            _,proof=build.skin_filter_neighbourhoods(production,surfaces,shape,sampler,profile,[],ownership,metric)
            args,kwargs=prepare.call_args;self.assertIs(args[0],first);self.assertEqual(args[2],0)
            self.assertEqual(args[1]['materials'][0]['properties'][0]['scale'],[1,1])
            self.assertEqual(args[4],sampler);self.assertFalse(kwargs['require_complete'])
            self.assertEqual(proof['highestDetailLod'],0)
            metric[:]=1e-6
            build.skin_filter_neighbourhoods(production,surfaces,shape,sampler,profile,[],ownership,metric)
            self.assertTrue(prepare.call_args.kwargs['require_complete'])
            np.testing.assert_array_equal(prepare.call_args.args[-1],ownership>0)
        conflict=(owner,dict(first,sourceFingerprint='c'*64),['Rig/Head'])
        with self.assertRaisesRegex(ValueError,'multiple highest-detail physical owners'):
            build.skin_filter_neighbourhoods(production,[*surfaces,conflict],shape,sampler,profile,[],ownership,metric)
        np.testing.assert_array_equal(ownership,np.ones(shape));self.assertEqual(sampler['mipLevel'],3)

    def test_native_triangle_protection_uses_sampled_texels_without_bleeding_from_shared_corners(self):
        production,_=build.source_owner()
        packet=dict(vertices=[0.,0.,0.,.01,0.,0.,.01,.01,0.,0.,.01,0.],subMeshes=[[0,1,2,0,2,3]],
            uv=[dict(channel=0,values=[.1,.1,0.,0.,.9,.1,0.,0.,.9,.9,0.,0.,.1,.9,0.,0.])])
        skin=dict(rendererToPrefab=np.eye(4).ravel().tolist(),materials=[dict(properties=[dict(name='_MainTex',scale=[1.,1.],offset=[0.,0.])])])
        for size in(32,8):
            for mode in(0,1,2):
                sampler=dict(filterMode=mode,wrapU=0,wrapV=0,anisoLevel=1)
                ownership,_,_,covered=production.indexed_surface_uv_metrics(packet,skin,0,(size,size),.001,sampler,triangle_field=[0.,1.])
                self.assertEqual(ownership[size//4,size//4],1.)
                self.assertEqual(ownership[size*3//4,size*3//4],0.)
                self.assertTrue(covered[size//4,size//4]);self.assertTrue(np.any(covered&(ownership==0)))
        with self.assertRaisesRegex(ValueError,'triangle field'):
            production.indexed_surface_uv_metrics(packet,skin,0,(8,8),.001,dict(wrapU=0,wrapV=0),triangle_field=[1.])
        with self.assertRaisesRegex(ValueError,'one ownership evaluator'):
            production.indexed_surface_uv_metrics(packet,skin,0,(8,8),.001,dict(wrapU=0,wrapV=0),triangle_field=[0.,1.],vertex_field=np.ones(4))

    def test_source_chart_recovery_uses_native_indexed_ownership_across_refinement_and_reordered_added_vertices(self):
        production,_=build.source_owner();bones=['Rig/Bip001 Head']
        native=dict(vertexCount=6,sourceFingerprint='c'*64,vertices=[0.,0.,0.,1.,0.,0.,0.,1.,0.,0.,0.,.01,1.,0.,.01,0.,1.,.01],
            weights=[1.,0.,0.,0.]*6,boneIndices=[0]*24,uv=[dict(channel=0,dimension=2,values=[0.,0.,0.,0.,1.,0.,0.,0.,0.,1.,0.,0.]*2)],
            subMeshes=[[0,1,2,3,4,5]])
        source=copy.deepcopy(native);source.update(vertexCount=7,sourceFingerprint='a'*64)
        source['vertices']+=[.5,0.,0.];source['weights']+=[1.,0.,0.,0.];source['boneIndices']+=[0]*4
        source['uv'][0]['values']+=[.5,0.,0.,0.];source['subMeshes']=[[0,6,2,6,1,2,3,4,5]]
        target=copy.deepcopy(native);target.update(vertexCount=8,sourceFingerprint='b'*64)
        target['vertices']+=[.5,0.,.01,.5,0.,0.];target['weights']+=[1.,0.,0.,0.]*2;target['boneIndices']+=[0]*8
        target['uv'][0]['values']+=[.5,0.,0.,0.]*2;target['subMeshes']=[[0,7,2,7,1,2,3,6,5,6,4,5]]
        declaration=dict(includeBranches=['Bip001 Head'],protectPrefixes=['b_lip_'],protectSourceCharts={source['sourceFingerprint']:[
            dict(type='sourceTriangle',subMesh=0,triangleOrdinal=0,barycentric=[.2,.3,.5])]})
        before=copy.deepcopy((native,source,target,declaration));prepared={}
        actual=production.source_material_ownership(target,bones,declaration,_prepared=prepared,chart_source=dict(source=source,native=native))
        np.testing.assert_array_equal(actual[[0,1,2,7]],np.zeros(4));np.testing.assert_array_equal(actual[[3,4,5,6]],np.ones(4))
        resolved=prepared['resolvedDeclaration'];self.assertIn(target['sourceFingerprint'],resolved['protectSourceCharts'])
        self.assertTrue(prepared['chartRecovery']['indexedComponentsExact'])
        np.testing.assert_array_equal(production.source_material_ownership(target,bones,resolved),actual)
        self.assertEqual((native,source,target,declaration),before)
        changed=copy.deepcopy(target);changed['uv'][0]['values'][0]+=.001
        with self.assertRaisesRegex(ValueError,'native UV prefix'):
            production.source_material_ownership(changed,bones,declaration,chart_source=dict(source=source,native=native))
        merged=copy.deepcopy(target);merged['subMeshes'][0]+=[2,3,7]
        with self.assertRaisesRegex(ValueError,'unique recovered indexed component'):
            production.source_material_ownership(merged,bones,declaration,chart_source=dict(source=source,native=native))
        with self.assertRaisesRegex(ValueError,'input mesh identity'):
            production.source_material_ownership(target,bones,declaration)

    def test_skin_payload_cache_reuses_only_matching_surface_sampler_and_mip_contracts(self):
        production, _ = build.source_owner()
        import character_resources as resources
        import material_fields as fields

        bones = ['Rig/Tissue']
        packet = dict(sourceFingerprint='c' * 64,
            vertices=[0., 0., 0., .004, 0., 0., .004, .004, 0., 0., .004, 0.],
            weights=[1., 0., 0., 0.] * 4, boneIndices=[0] * 16,
            bindPoses=np.eye(4).ravel().tolist(), subMeshes=[[0, 1, 2, 0, 2, 3]],
            uv=[dict(channel=0, values=[.1, .1, 0., 0., .9, .1, 0., 0.,
                .9, .9, 0., 0., .1, .9, 0., 0.])])
        domain = dict(includeBranches=['Tissue'], protectPrefixes=['Other'])
        region = lambda centre: dict(coordinateDomain='native-rest-prefab-dcc-metres',
            ellipsoids=[dict(center=centre, radii=[.005, .002, .005], innerFraction=.2)])
        declaration = dict(target=region([.0005, 0., .0005]), reference=region([.0035, 0., .0035]),
            protected=None, sourceLowFrequencySigmaMetres=.0005, referenceSigmaMetres=.001,
            strength=.5, maximumLinearRgbDelta=.1, reconstruction='adjacent-low-frequency-rgb')
        profile = dict(pigmentRetain=.25, normalRetain=.25, normalRadiusMetres=.001,
            fineSigmaMetres=.0001, grainSigmaMetres=.00075, coherenceSigmaMetres=.001,
            featherMetres=.003)
        surface = dict(resource=dict(path='synthetic', sha256='a' * 64), renderer=31,
            meshResource=dict(path='synthetic', sha256='a' * 64), mesh=41,
            materialResource=dict(path='synthetic', sha256='a' * 64), material=51,
            materialSlot=0, bones=bones, rendererToPrefab=np.eye(4).ravel().tolist())
        real_metrics = production.indexed_surface_uv_metrics

        # A change in sampled UV includes the saved material's ST. The real
        # saved_skin_surface resolves that ST before this raster stage.
        changes = {
            'matching': lambda texture, data: None,
            'renderer': lambda texture, data: texture['surfaces'][0].update(renderer=32),
            'material-slot': lambda texture, data: texture['surfaces'][0].update(materialSlot=1),
            'complete-mesh-slot': lambda texture, data: texture['surfaces'][0].update(materialSlot=1,completeMesh=True),
            'material-resource': lambda texture, data: texture['surfaces'][0]['materialResource'].update(sha256='b' * 64),
            'sampled-ST': lambda texture, data: texture['surfaces'][0].update(sampledScale=[.8, .8], sampledOffset=[.1, .1]),
            'filter': lambda texture, data: setattr(data.m_TextureSettings, 'm_FilterMode', 0),
            'wrap': lambda texture, data: setattr(data.m_TextureSettings, 'm_WrapU', 1),
            'mip-bias': lambda texture, data: setattr(data.m_TextureSettings, 'm_MipBias', .25),
            'resolution': lambda texture, data: setattr(data, 'm_Width', 16),
        }

        for name, change in changes.items():
            with self.subTest(contract=name), tempfile.TemporaryDirectory() as folder:
                source = Path(folder) / 'synthetic.bundle'
                source.write_bytes(b'synthetic source identity')
                textures = [dict(object=7, channel='color', surfaces=[dict(copy.deepcopy(surface), property='_MainTex')],
                    domain=domain, profile=profile, normalEncoding=None, luminanceRegions=[declaration]),
                    dict(object=8, channel='normal', surfaces=[dict(copy.deepcopy(surface), property='_BumpMap')],
                        domain=domain, profile=profile, normalEncoding='packed-rgorag')]
                data = {identifier: SimpleNamespace(m_Name='Synthetic' + str(identifier), m_ColorSpace=1 if identifier == 7 else 0,
                    m_Width=8, m_Height=8, m_MipCount=2,
                    m_TextureSettings=SimpleNamespace(m_WrapU=0, m_WrapV=0, m_FilterMode=1, m_Aniso=1, m_MipBias=0.))
                    for identifier in (7, 8)}
                change(textures[1], data[8])
                objects = {identifier: SimpleNamespace(path_id=identifier, read=lambda identifier=identifier: data[identifier])
                    for identifier in (7, 8)}
                serialized = SimpleNamespace(name='synthetic', objects=objects)
                images = {identifier: [np.full((8 // 2 ** level, data[identifier].m_Width // 2 ** level, 4), 128, np.uint8)
                    for level in range(2)] for identifier in (7, 8)}
                source_pixels = {identifier: [image.tobytes() for image in value] for identifier, value in images.items()}
                calls = []

                def saved(reference_json):
                    reference = json.loads(reference_json)
                    result = copy.deepcopy(packet)
                    coords = np.asarray(result['uv'][0]['values']).reshape(-1, 4)
                    coords[:, :2] = coords[:, :2] * reference.get('sampledScale', [1., 1.]) + reference.get('sampledOffset', [0., 0.])
                    result['uv'][0]['values'] = coords.ravel().tolist()
                    if reference.get('completeMesh',False):result['subMeshes']=[[],result['subMeshes'][0]]
                    return result, bones

                def metrics(*args, **kwargs):
                    calls.append((args[3], copy.deepcopy(args[5])))
                    return real_metrics(*args, **kwargs)

                def refine(pixels, ownership, metric, options, channel, **kwargs):
                    texture = textures[0 if channel == 'color' else 1]
                    identifier = texture['object']
                    size = pixels.shape[:2]
                    level = 0 if size == images[identifier][0].shape[:2] else 1
                    sample = dict(wrapU=data[identifier].m_TextureSettings.m_WrapU,
                        wrapV=data[identifier].m_TextureSettings.m_WrapV,
                        filterMode=data[identifier].m_TextureSettings.m_FilterMode, anisoLevel=1,
                        mipBias=data[identifier].m_TextureSettings.m_MipBias, mipLevel=level)
                    reference=texture['surfaces'][0];restored, paths = saved(json.dumps(reference))
                    slot=reference['materialSlot']if reference.get('completeMesh',False)else 0
                    materials=[None]*(slot+1);materials[slot]=dict(properties=[dict(name='_MainTex',scale=[1.,1.],offset=[0.,0.])])
                    skin = dict(rendererToPrefab=surface['rendererToPrefab'],
                        materials=materials)
                    expected = real_metrics(restored, skin, slot, size, .001, sample,
                        vertex_field=production.source_material_ownership(restored, paths, domain))
                    np.testing.assert_array_equal(ownership, expected[0])
                    # The job assigns uncovered density from the covered median;
                    # covered values must remain exactly the independent raster.
                    np.testing.assert_array_equal(metric[expected[3]], expected[1][expected[3]])
                    if channel == 'color':
                        self.assertEqual(len(kwargs['luminance_regions']), 1)
                        observed = kwargs['luminance_regions'][0]
                        for kind in ('target', 'reference'):
                            field = real_metrics(restored, skin, slot, size, .001, sample,
                                bone_domain=dict(bones=paths, declaration=dict(domain, surfaceRegion=declaration[kind])))[0]
                            ratio = np.clip(np.divide(field, expected[0], out=np.zeros_like(field), where=expected[0] > 0), 0, 1)
                            np.testing.assert_array_equal(observed[kind], ratio)
                    return pixels.copy(), {}

                with mock.patch.object(build, 'source_owner', return_value=(production, None)), \
                        mock.patch.object(resources, 'load_serialized_bundle', return_value=(None, serialized)), \
                        mock.patch.object(resources, 'texture_mip_images', side_effect=lambda obj: images[obj.path_id]), \
                        mock.patch.object(resources, 'texture_mip_payloads', side_effect=lambda obj: (4, source_pixels[obj.path_id])), \
                        mock.patch.object(build, 'saved_skin_surface', side_effect=saved), \
                        mock.patch.object(production, 'indexed_surface_uv_metrics', side_effect=metrics), \
                        mock.patch.object(production, 'refine_source_skin_material', side_effect=refine), \
                        mock.patch.object(build,'skin_filter_neighbourhoods',return_value=([],{})), \
                        mock.patch.object(fields, 'whole_block_pixels', side_effect=lambda mask: (None, mask)), \
                        contextlib.redirect_stdout(io.StringIO()):
                    result = build.source_skin_payloads(dict(source=str(source), sha256=build.sha(source), textures=textures))
                self.assertEqual(len(calls), 2 if name == 'matching' else 4)
                self.assertEqual([value['payload'] for value in result],
                    [b''.join(source_pixels[identifier]) for identifier in (7, 8)])
                self.assertEqual([[entry['level'] for entry in value['mips']] for value in result], [[0, 1], [0, 1]])


    def test_native_packet_must_match_its_census_owner_as_well_as_its_own_fingerprint(self):
        production,_=build.source_owner()
        packet=dict(name='Synthetic',nativeReadable=True,vertexCount=1,indexFormat=0,
            vertices=[0.,0.,0.],normals=[0.,0.,1.],tangents=[1.,0.,0.,1.],colors=[],
            weights=[1.,0.,0.,0.],boneIndices=[0,0,0,0],bindPoses=np.eye(4).ravel().tolist(),
            subMeshes=[[]],uv=[],shapes=[],bounds=[0.]*6)
        packet['sourceFingerprint']=build.fingerprint(packet)[0]
        with tempfile.TemporaryDirectory()as directory:
            path=Path(directory)/'source.json.gz'
            def save(value):
                with gzip.open(path,'wt',encoding='utf-8')as stream:json.dump(value,stream)
            save(packet);skin=dict(meshFile=str(path),sourceFingerprint=packet['sourceFingerprint'])
            self.assertEqual(production.native_packet(skin),packet)
            changed=copy.deepcopy(packet);changed['vertices'][0]=.1
            changed['sourceFingerprint']=build.fingerprint(changed)[0];save(changed)
            with self.assertRaisesRegex(ValueError,'declared source fingerprint'):production.native_packet(skin)
            save(packet)
            with self.assertRaisesRegex(ValueError,'declared source fingerprint'):production.native_packet(dict(meshFile=str(path)))
            changed=copy.deepcopy(packet);changed['vertices'][0]=.1;save(changed)
            with self.assertRaisesRegex(ValueError,'Native packet fingerprint mismatch'):production.native_packet(skin)

    def _native_head_recovery_fixture(self,root,*,alias=False,orphan=False,native_alias_bones=False,alias_frame=False,extra_weight=False,missing_donor=False,wrong_lod=False,mixed_neck=False,hash_order=False,broken_frame=False,invalid_donor=False):
        import character_resources as resources
        production,_=build.source_owner()
        native_path=root/'original.bundle';native_path.write_bytes(b'isolated native fixture')
        current_path=root/'source.bundle';current_path.write_bytes(b'isolated current fixture')
        pointer=lambda value:dict(m_FileID=0,m_PathID=value)
        head='Bip001/Bip001 Head'
        bones=[head]+(['Bip001/Bip001 Neck']if mixed_neck else[]);ocular=[head,head+'/b_eyelid_upper_l',head+'/b_eyelid_upper_r']
        points=np.asarray([[.02,.02,.02],[-.02,-.02,.02],[-.02,.02,-.02],[.02,-.02,-.02]],np.float32)
        normals=points/np.linalg.norm(points,axis=1,keepdims=True)
        tangent=np.stack((-normals[:,1],normals[:,0],np.zeros(4)),axis=1);tangent/=np.linalg.norm(tangent,axis=1,keepdims=True)
        native=dict(name='Face_LOD1',nativeReadable=True,vertexCount=4,indexFormat=0,
            vertices=points.ravel().tolist(),normals=normals.ravel().tolist(),
            tangents=np.column_stack((tangent,np.ones(4))).ravel().tolist(),colors=[],
            weights=[1.,0.,0.,0.]*4,boneIndices=[0]*16,bindPoses=np.eye(4).T.ravel().tolist(),
            subMeshes=[[0,2,1,0,1,3,0,3,2,1,2,3]],uv=[dict(channel=0,dimension=2,values=[0.]*16)],
            shapes=[dict(index=0,name='Muscular',frames=[dict(weight=100.,vertices=[0.]*12,normals=[0.]*12,tangents=[0.]*12)])],
            bounds=[0.,0.,0.,.04,.04,.04])
        if mixed_neck:
            native['weights'][4:8]=[.25,.75,0.,0.];native['boneIndices'][4:8]=[0,1,0,0]
            native['bindPoses']=np.tile(np.eye(4).T.ravel(),2).tolist()
        native['sourceFingerprint']=build.fingerprint(native)[0]
        current=copy.deepcopy(native);current['vertices'][0]=float(np.float32(current['vertices'][0]+.0005))
        if mixed_neck:
            current['vertices'][3]=float(np.float32(current['vertices'][3]+.03))
            current['normals'][3:6]=[0.,0.,1.];current['tangents'][4:8]=[0.,1.,0.,1.]
            current['weights'][4:8]=[.8,.2,0.,0.]
            for key,value in(('vertices',.03),('normals',.02),('tangents',.01)):
                current['shapes'][0]['frames'][0][key][3]=float(np.float32(value))
        if extra_weight:current['shapes'][0]['frames'].append(dict(weight=50.,vertices=[0.]*12,normals=[0.]*12,tangents=[0.]*12))
        current['sourceFingerprint']=build.fingerprint(current)[0]
        donor=copy.deepcopy(native);donor['name']='Face_LOD0'
        if not broken_frame:donor['shapes']=[]
        else:
            native['shapes'][0]['frames'][0]['vertices'][2]=1e14
            native['sourceFingerprint']=build.fingerprint(native)[0]
            if invalid_donor:donor['shapes'][0]['frames'][0]['vertices'][2]=1e14
        donor['bindPoses']=np.tile(np.eye(4).T.ravel(),3).tolist()
        donor['boneIndices']=[1,0,0,0,2,0,0,0,0,0,0,0,0,0,0,0]
        donor['sourceFingerprint']=build.fingerprint(donor)[0]
        files={name:SimpleNamespace(name=name,objects={})for name in('CAB-native','CAB-current')}
        def obj(file,identifier,kind,tree=None,packet=None):
            value=SimpleNamespace(path_id=identifier,type=SimpleNamespace(name=kind),assets_file=file,
                read_typetree=lambda:tree,peek_name=lambda:packet['name']if packet else kind)
            if packet is not None:value.packet=packet
            file.objects[identifier]=value;return value
        def renderer(file,identifier,mesh,paths,offset=0.):
            game=1000+identifier;transform=2000+identifier
            obj(file,game,'GameObject',dict(m_Component=[dict(component=pointer(transform))]))
            obj(file,transform,'Transform',dict(m_Father=pointer(100),m_LocalRotation=dict(x=0.,y=0.,z=0.,w=1.),
                m_LocalScale=dict(x=1.,y=1.,z=1.),m_LocalPosition=dict(x=offset,y=0.,z=0.)))
            return obj(file,identifier,'SkinnedMeshRenderer',dict(m_Mesh=pointer(mesh),m_GameObject=pointer(game),
                m_Materials=[pointer(300)],_bones=paths))
        native_rows=[];current_rows=[]
        for name,file in files.items():
            obj(file,100,'Transform',dict(m_Father=pointer(0)))
            obj(file,300,'Material',dict(m_Shader=pointer(301)))
            obj(file,301,'Shader',dict(m_ParsedForm=dict(m_Name='Lux/Human/Skin')))
            obj(file,7,'Mesh',packet=native if name=='CAB-native'else current)
            obj(file,8,'Mesh',packet=donor)
            rows=native_rows if name=='CAB-native'else current_rows
            rows.extend([('Face_LOD0',renderer(file,19,8,ocular)),('Face_LOD1',renderer(file,20,7,bones))])
            if alias:
                paths=[head,head+'/Foreign']if native_alias_bones and name=='CAB-native'else bones
                rows.append(('FaceAlias_LOD1',renderer(file,21,7,paths,.001 if alias_frame and name=='CAB-current'else 0.)))
            if orphan and name=='CAB-current':renderer(file,22,7,bones)
        native_ref=dict(path=str(native_path),sha256=build.sha(native_path),pathId=7)
        if missing_donor:native_rows[:]=[(path,value)for path,value in native_rows if path!='Face_LOD0']
        authorities=[]
        for path,value in native_rows:
            tree=value.read_typetree();ordered=list(tree['_bones']);permutation=list(range(len(ordered)))
            if hash_order:
                if path=='Face_LOD0':tree['_bones']=[ordered[2],ordered[0],ordered[1]]
                elif path!='FaceAlias_LOD1'and len(ordered)==2:tree['_bones']=ordered[::-1]
                permutation=[tree['_bones'].index(bone)for bone in ordered]
            authorities.append(dict(asset='Assets/Source.prefab',renderer=value.path_id,
                nativeMesh=dict(native_ref,pathId=8 if path=='Face_LOD0'else 7),
                sourceBonePaths=tree['_bones'],permutation=permutation))
        retained=root/'retained.json.gz'
        with gzip.open(retained,'wt',encoding='utf-8')as stream:json.dump(current,stream)
        profile=production.extremity_refinement_profile();profile.update(protectCutVertices=False,protectedOpenCutRings=0,
            minimumNormalAngleDegrees=0,minimumEdgeMillimetres=[0,0,0],minimumBowMillimetres=.0001,maximumBowMillimetres=.5)
        declaration=dict(retainedFacePacket=dict(path=str(retained),sha256=build.sha(retained)),nativeHeadBonePath=head,
            discardNonNativeChannels=[],refinement=dict(lod=1,passes=1,profile=profile),producerSources={
                'production':dict(path=production.__file__,sha256=build.sha(production.__file__)),
                'art':dict(path=production.art.__file__,sha256=build.sha(production.art.__file__))})
        if wrong_lod:declaration['refinement']['lod']=0
        row=dict(resource='Source/Prefabs/Source.prefab',renderer='Face_LOD1',meshPathId=7,
            nativeMesh=native_ref,currentFingerprint=current['sourceFingerprint'],faceRecovery=declaration)
        manifest=dict(resources=[dict(source=row['resource'],asset='Assets/Source.prefab')])
        index=dict(bundles=[dict(file='original.bundle',cab='CAB-native',source=str(native_path),sha256=build.sha(native_path),
            parts=[dict(sourceKey=row['resource'],asset='Assets/Source.prefab')])])
        def resolve(file,pointer):return file,file.objects[pointer['m_PathID']]
        def prefabs(manifest,proofs,resource,load):
            return (native_rows if 'original.bundle'in proofs else current_rows),resolve
        guard=production.humanoid_face_contact_guard
        observed=[]
        def contacts(packet,skin,regions=(),**kwargs):
            result=guard(packet,skin,regions,**kwargs);observed.append((copy.deepcopy(regions),result.copy()));return result
        patches=[mock.patch.object(build,'resource_prefab_skins',side_effect=prefabs),
            mock.patch.object(build,'export_current_mesh',side_effect=lambda value:copy.deepcopy(value.packet)),
            mock.patch.object(build,'source_owner',return_value=(production,None)),
            mock.patch.object(resources,'audit_native_bone_orders',return_value=dict(status='PASS_NATIVE_SKIN_SLOT_AUDIT',rows=authorities)),
            mock.patch.object(resources,'skin_bone_paths',side_effect=lambda file,tree,resolve:tree['_bones']),
            mock.patch.object(production,'humanoid_face_contact_guard',side_effect=contacts)]
        return build.native_surface_restoration_sources,(index,manifest,{'source.bundle':{}},[row],files['CAB-current'],lambda _:None),patches,observed,current

    def _run_native_head_recovery_fixture(self,**options):
        from contextlib import ExitStack
        with tempfile.TemporaryDirectory()as folder:
            function,args,patches,observed,current=self._native_head_recovery_fixture(Path(folder),**options);targets={}
            with ExitStack()as stack:
                for patch in patches:stack.enter_context(patch)
                function(*args,target_packets=targets)
            return targets,observed,current

    def test_low_lod_refinement_inherits_actual_lod0_eye_contact_regions(self):
        targets,observed,current=self._run_native_head_recovery_fixture()
        self.assertEqual(len(observed),1);regions,guard=observed[0]
        self.assertEqual(len(regions),2);self.assertTrue(np.any(guard==0));self.assertTrue(np.any(guard>0))
        target=targets[7]
        self.assertEqual(target['vertices'][:12],current['vertices'])
        self.assertEqual(target['weights'][:16],current['weights'])
        self.assertEqual(target['uv'][0]['values'][:16],current['uv'][0]['values'])

    def test_same_named_channel_cannot_silently_discard_an_extra_frame_weight(self):
        with self.assertRaisesRegex(ValueError,'native physical frame weight set'):self._run_native_head_recovery_fixture(extra_weight=True)

    def test_native_recovery_uses_validated_physical_reference_without_replacing_raw_source_authority(self):
        from contextlib import ExitStack
        for mixed,ordered in((False,False),(True,False),(True,True)):
            with self.subTest(mixed=mixed,ordered=ordered),tempfile.TemporaryDirectory()as folder:
                function,args,patches,_,current=self._native_head_recovery_fixture(Path(folder),
                    broken_frame=True,mixed_neck=mixed,hash_order=ordered,alias=True)
                targets={}
                with ExitStack()as stack:
                    for patch in patches:stack.enter_context(patch)
                    raw=function(*args,target_packets=targets)
                self.assertEqual(raw[7]['shapes'][0]['frames'][0]['vertices'][2],1e14)
                target=targets[7]
                for frame in target['shapes'][0]['frames']:
                    np.testing.assert_array_equal(frame['vertices'],np.zeros(target['vertexCount']*3))
                self.assertEqual(target['shapes'][0]['name'],current['shapes'][0]['name'])
                self.assertEqual(target['shapes'][0]['frames'][0]['weight'],100.)
                self.assertTrue(np.isfinite(target['vertices']).all())

    def test_native_recovery_rejects_a_corrupt_source_donor_instead_of_weakening_facing_checks(self):
        with self.assertRaisesRegex(ValueError,'LOD0 physical source is also outside'):
            self._run_native_head_recovery_fixture(broken_frame=True,invalid_donor=True)

    def test_native_hash_slot_order_is_shared_by_recovery_consumers_and_lower_lod_contact_donor(self):
        baseline,contacts,current=self._run_native_head_recovery_fixture(mixed_neck=True,alias=True)
        corrected,actual,retained=self._run_native_head_recovery_fixture(mixed_neck=True,alias=True,hash_order=True)
        self.assertEqual(corrected,baseline);self.assertEqual(retained,current)
        self.assertEqual(len(actual),len(contacts))
        for (expected_regions,expected_guard),(regions,guard)in zip(contacts,actual,strict=True):
            self.assertEqual(regions,expected_regions);np.testing.assert_array_equal(guard,expected_guard)

    def test_all_local_mesh_consumers_require_matching_native_authority(self):
        self._run_native_head_recovery_fixture(alias=True)
        with self.assertRaisesRegex(ValueError,'native consumer palettes'):self._run_native_head_recovery_fixture(alias=True,native_alias_bones=True)
        with self.assertRaisesRegex(ValueError,'renderer coordinate frames'):self._run_native_head_recovery_fixture(alias=True,alias_frame=True)
        with self.assertRaisesRegex(ValueError,'undeclared local renderer consumer'):self._run_native_head_recovery_fixture(orphan=True)

    def test_lower_lod_contact_ownership_cannot_be_omitted_or_bypassed_by_the_declared_lod(self):
        with self.assertRaisesRegex(ValueError,'unique LOD0 contact owner'):self._run_native_head_recovery_fixture(missing_donor=True)
        with self.assertRaisesRegex(ValueError,'bounded native surface refinement'):self._run_native_head_recovery_fixture(wrong_lod=True)

    def test_mixed_native_neck_rest_skin_and_all_stored_frame_samples_are_restored_exactly(self):
        targets,observed,current=self._run_native_head_recovery_fixture(mixed_neck=True)
        target=targets[7];points=np.asarray([[.02,.02,.02],[-.02,-.02,.02],[-.02,.02,-.02],[.02,-.02,-.02]],np.float32)
        normals=points/np.linalg.norm(points,axis=1,keepdims=True)
        tangent=np.array([-normals[1,1],normals[1,0],0.]);tangent/=np.linalg.norm(tangent)
        self.assertEqual(target['vertices'][3:6],points[1].tolist())
        self.assertEqual(target['normals'][3:6],normals[1].tolist())
        np.testing.assert_array_equal(np.asarray(target['tangents'][4:8],np.float32),np.asarray([*tangent,1.],np.float32))
        self.assertEqual(target['weights'][4:8],[.25,.75,0.,0.])
        self.assertEqual(target['boneIndices'][4:8],[0,1,0,0])
        for before,after in zip(current['uv'],target['uv']):
            self.assertEqual((after['channel'],after['dimension'],after['values'][:len(before['values'])]),
                (before['channel'],before['dimension'],before['values']))
        for key in('vertices','normals','tangents'):
            self.assertEqual(target['shapes'][0]['frames'][0][key][3:6],[0.,0.,0.])
        self.assertNotEqual(target['vertices'][3:6],current['vertices'][3:6])


    def test_native_face_recovery_preparation_derives_typed_authority_and_requires_declared_channel_removal(self):
        import character_resources as resources
        import resource_storage
        import mesh_recipe_codec as codec
        source_owner=build.source_owner()
        with tempfile.TemporaryDirectory()as folder:
            root=Path(folder);source=root/'source';catalog=source/'Catalog';catalog.mkdir(parents=True)
            current_path=catalog/'head.bundle';current_path.write_bytes(b'current source')
            native_path=root/'native.bundle';native_path.write_bytes(b'native source')
            address='Face/Prefabs/Face.prefab';asset='Assets/Face.prefab';name='DeclaredFace';renderer_path='Face_LOD0'
            current=dict(name='Head',vertexCount=3,sourceFingerprint='current',indexFormat=0,bounds=[0.,0.,0.,1.,1.,1.],
                vertices=[0.,0.,0.,1.,0.,0.,0.,1.,0.],normals=[0.,0.,1.]*3,tangents=[1.,0.,0.,1.]*3,
                weights=[1.,0.,0.,0.]*3,boneIndices=[0]*12,colors=[],bindPoses=np.eye(4).ravel().tolist(),
                uv=[dict(channel=0,dimension=2,values=[0.,0.,0.,0.,1.,0.,0.,0.,0.,1.,0.,0.])],subMeshes=[[0,1,2]],
                shapes=[dict(name='AuthoredPhysicalChannel',index=0,frames=[dict(weight=100.,vertices=[0.]*9,normals=[0.]*9,tangents=[0.]*9)])])
            native=dict(copy.deepcopy(current),sourceFingerprint='native',shapes=[])
            recovered=dict(copy.deepcopy(native),sourceFingerprint='recovered')
            current_mesh=SimpleNamespace(path_id=7,type=SimpleNamespace(name='Mesh'))
            native_mesh=SimpleNamespace(path_id=17,type=SimpleNamespace(name='Mesh'))
            current_file=SimpleNamespace(name='CurrentCAB',objects={7:current_mesh});native_file=SimpleNamespace(name='NativeCAB',objects={17:native_mesh})
            current_renderer=SimpleNamespace(assets_file=current_file,read_typetree=lambda:dict(m_Mesh=dict(m_PathID=7)))
            native_renderer=SimpleNamespace(assets_file=native_file,read_typetree=lambda:dict(m_Mesh=dict(m_PathID=17),m_Materials=[dict(m_PathID=23)]))
            material=SimpleNamespace(read_typetree=lambda:dict(m_Shader=dict(m_PathID=29)))
            shader=SimpleNamespace(read_typetree=lambda:dict(m_ParsedForm=dict(m_Name='DeclaredSkinShader')))
            def resolve(file,pointer):
                return (file,{7:current_mesh,17:native_mesh,23:material,29:shader}[pointer['m_PathID']])
            build.write(catalog/build.RESOURCE_FILES['manifest'],dict(resources=[dict(source=address,asset=asset,bundles=[dict(file=current_path.name)])],
                bundles=[dict(file=current_path.name,sha256=build.sha(current_path))]))
            build.write(source/'ResourceAssembly.json',dict(catalogSha256=build.sha(catalog/build.RESOURCE_FILES['manifest']),
                proofs=[dict(file=current_path.name,newCab=current_file.name,sha256=build.sha(current_path))]))
            index=root/'index.json';build.write(index,dict(bundles=[dict(file='native.bundle',cab=native_file.name,source=str(native_path),sha256=build.sha(native_path),
                parts=[dict(sourceKey=address,asset=asset)])]))
            census=root/'Census.json';build.write(census,dict(assets=[dict(name=name,internalId=asset,sourceContentKey='identity')]))
            designs=root/'designs.json';design=dict(schemaVersion=1,surfaces={name:dict(sourceContentKey='identity',
                renderers=[dict(path=renderer_path,slots=[0],shader='DeclaredSkinShader')])})
            build.write(designs,design)
            calls=[]
            def authority(index,manifest,proofs,rows,owner,load,*,target_packets):
                self.assertIs(owner,current_file);self.assertEqual(rows[0]['nativeMesh'],dict(path=str(native_path),sha256=build.sha(native_path),pathId=17))
                declaration=rows[0]['faceRecovery'];self.assertEqual(declaration['discardNonNativeChannels'],
                    sorted({value['name']for value in current['shapes']}-{value['name']for value in native['shapes']}))
                self.assertEqual(declaration['nativeHeadBonePath'],'Bip001/Bip001 Head')
                for reference in declaration['producerSources'].values():self.assertEqual(build.sha(reference['path']),reference['sha256'])
                target_packets[7]=recovered;calls.append(copy.deepcopy(rows));return {7:native}
            def prefabs(manifest,*args):
                original=manifest['resources'][0]['bundles'][0]['file']=='native.bundle'
                return [(renderer_path,native_renderer if original else current_renderer)],resolve
            output=root/'tools/game-test/artifacts/first';workspace=root/'tools/game-test/artifacts/work'
            argv=['build_models','--workspace',str(workspace),'restore-native-surfaces','--source',str(source),'--census',str(census),
                '--designs',str(designs),'--native-index',str(index),'--assets',name,'--output',str(output)]
            with mock.patch.object(build,'ROOT',root),mock.patch.object(build,'HERE',None),mock.patch.object(sys,'argv',argv),\
                    mock.patch.object(build,'source_owner',return_value=source_owner),\
                    mock.patch.object(resource_storage,'resource_work',side_effect=lambda *args,**kwargs:contextlib.nullcontext()),\
                    mock.patch.object(resources,'load_serialized_bundle',side_effect=lambda path,**kwargs:(None,native_file if Path(path)==native_path else current_file)),\
                    mock.patch.object(build,'resource_prefab_skins',side_effect=prefabs),\
                    mock.patch.object(resources,'skin_bone_paths',return_value=['Rig/Bip001/Bip001 Head']),\
                    mock.patch.object(build,'export_current_mesh',side_effect=lambda value:native if value is native_mesh else current),\
                    mock.patch.object(build,'native_surface_restoration_sources',side_effect=authority),contextlib.redirect_stdout(io.StringIO()):
                with self.assertRaisesRegex(ValueError,'undeclared non-native physical channels'):build.main()
                self.assertEqual(calls,[])
                design['surfaces'][name]['nativeRecovery']=dict(passes=2,discardNonNativeChannels={renderer_path:['AuthoredPhysicalChannel']})
                build.write(designs,design);output=root/'tools/game-test/artifacts/second';sys.argv=argv[:-1]+[str(output)]
                build.main()
                # The design permits a known surplus channel, while an already
                # recovered Mesh records only removals which actually occurred.
                retained_shapes=current['shapes'];current['shapes']=[]
                repeated=root/'tools/game-test/artifacts/recovered';sys.argv=argv[:-1]+[str(repeated)]
                build.main();current['shapes']=retained_shapes
                repeated_plan=build.read(repeated/'Plan.json')
                self.assertEqual(repeated_plan['rows'][0]['meshes'][0]['faceRecovery']['discardNonNativeChannels'],[])
                repeated_recipe=build.read(repeated/'BuildPlan.json')['jobs'][0]['recipe']
                self.assertEqual(build.sha(repeated_recipe['path']),repeated_recipe['sha256'])
            self.assertEqual(len(calls),2);plan=build.read(output/'Plan.json');compiled=build.read(output/'BuildPlan.json')
            row=plan['rows'][0]['meshes'][0]
            for field,expected in(('nativePacket',native),('packet',recovered),('retainedFacePacket',current)):
                reference=row['faceRecovery'][field]if field=='retainedFacePacket'else row[field]
                self.assertEqual(build.sha(reference['path']),reference['sha256'])
                with gzip.open(reference['path'],'rt',encoding='utf-8')as stream:self.assertEqual(json.load(stream),expected)
            self.assertEqual(plan['sourceAssembly']['sha256'],build.sha(source/'ResourceAssembly.json'))
            self.assertEqual(plan['nativeIndex']['sha256'],build.sha(index))
            recipe_reference=compiled['jobs'][0]['recipe'];self.assertEqual(build.sha(recipe_reference['path']),recipe_reference['sha256'])
            decoded=build.read(recipe_reference['path']);payload=Path(recipe_reference['path']).parent/decoded['numericPayload']['path']
            self.assertEqual(build.sha(payload),decoded['numericPayload']['sha256'])
            self.assertEqual(payload.stat().st_size,decoded['numericPayload']['byteLength'])
            self.assertTrue(payload.read_bytes().startswith(codec.MAGIC))
            self.assertEqual(current_path.read_bytes(),b'current source');self.assertEqual(native_path.read_bytes(),b'native source')

    def test_native_face_recovery_cli_finishes_receipts_and_retains_source_and_recipe_pins(self):
        import character_resources as resources
        import resource_storage
        with tempfile.TemporaryDirectory()as folder:
            root=Path(folder);source=root/'source';catalog=source/'Catalog';catalog.mkdir(parents=True)
            original=catalog/'head.bundle';original.write_bytes(b'original head source')
            other=catalog/'body.bundle';other.write_bytes(b'unchanged body source')
            compiled=root/'compiled.bundle';compiled.write_bytes(b'validated compiler payload')
            pin=lambda path:dict(path=str(Path(path).resolve()),sha256=build.sha(path))
            address='Source/Prefabs/Source.prefab';asset='Assets/Source.prefab';native_reference=dict(**pin(compiled),pathId=17)
            native=dict(sourceFingerprint='native',vertexCount=3);target=dict(sourceFingerprint='recovered',vertexCount=4)
            native_path=root/'native.json.gz';packet_path=root/'recovered.json.gz'
            for path,packet in((native_path,native),(packet_path,target)):
                with gzip.open(path,'wt',encoding='utf-8')as stream:json.dump(packet,stream)
            manifest_path=catalog/build.RESOURCE_FILES['manifest']
            build.write(manifest_path,dict(resources=[dict(source=address,asset=asset,bundles=[dict(file=original.name)])],
                bundles=[dict(file=path.name,sha256=build.sha(path))for path in(original,other)]))
            build.write(source/'ResourceAssembly.json',dict(catalogSha256=build.sha(manifest_path),proofs=[
                dict(file=path.name,sha256=build.sha(path),bytes=path.stat().st_size,newCab='CAB_'+path.stem)for path in(original,other)]))
            native_index=root/'index.json';build.write(native_index,dict(bundles=[]))
            mesh=dict(meshPathId=7,name='HeadMesh',resource=address,renderer='HeadRenderer',nativeMesh=native_reference,
                currentFingerprint='current',nativePacket=pin(native_path),packet=pin(packet_path),
                faceRecovery=dict(retainedFacePacket=pin(native_path)))
            plan=root/'plan.json';build.write(plan,dict(kind='NativeSurfaceRestoration',schemaVersion=1,
                sourceAssembly=pin(source/'ResourceAssembly.json'),nativeIndex=pin(native_index),
                rows=[dict(file=original.name,sourceSha256=build.sha(original),compiledFile=compiled.name,meshes=[mesh])]))
            report=root/'report.json';build.write(report,dict(success=True,bundles=[dict(success=True,
                bundlePath=str(compiled),bundleSha256=build.sha(compiled))]))
            obj=SimpleNamespace(type=SimpleNamespace(name='Mesh'),peek_name=lambda:'HeadMesh',read_typetree=lambda:{})
            before=SimpleNamespace(objects={7:obj});compiler=SimpleNamespace(objects={17:obj})
            observed=[]
            def authorities(*args,target_packets=None,target_bindings=None,target_palettes=None):
                self.assertIsNotNone(target_packets);target_packets[7]=target
                self.assertIsNotNone(target_bindings);self.assertEqual(target_bindings,{})
                self.assertIsNotNone(target_palettes)
                target_palettes[7]=dict(sourceBonePaths=['Rig/Head','Rig/Neck'],bonePaths=['Rig/Head','Rig/Neck'])
                return {7:native}
            def save(path,digest,destination,replacements,**kwargs):
                self.assertEqual(digest,build.sha(path));self.assertEqual(replacements[0]['packet'],target)
                self.assertEqual(replacements[0]['sourceBonePaths'],['Rig/Head','Rig/Neck'])
                self.assertEqual(replacements[0]['bonePaths'],['Rig/Head','Rig/Neck'])
                destination.write_bytes(b'written recovered head')
                return dict(source=pin(path),target=pin(destination),changedObjects=[7],
                    allOtherObjectBytesExact=True,resourcePayloadsExact=True,cabAndExternalReferencesUnchanged=True,savedObjectBytesExact=True)
            output=root/'tools/game-test/artifacts/candidate';workspace=root/'tools/game-test/artifacts/work'
            argv=['build_models','--workspace',str(workspace),'restore-native-surfaces','--source',str(source),
                '--plan',str(plan),'--report',str(report),'--output',str(output)]
            with mock.patch.object(build,'ROOT',root),mock.patch.object(build,'HERE',None),mock.patch.object(sys,'argv',argv),\
                    mock.patch.object(resource_storage,'resource_work',side_effect=lambda *args,**kwargs:contextlib.nullcontext()),\
                    mock.patch.object(resources,'load_serialized_bundle',side_effect=lambda path,**kwargs:(None,compiler if Path(path)==compiled else before)),\
                    mock.patch.object(build,'native_surface_restoration_sources',side_effect=authorities),\
                    mock.patch.object(build,'export_current_mesh',side_effect=lambda value:target if value is compiler.objects[17]else dict(sourceFingerprint='current')),\
                    mock.patch.object(resources,'prepare_standalone_part',side_effect=save),\
                    mock.patch.object(build,'validate_native_surface_restoration',side_effect=lambda receipt:observed.append(copy.deepcopy(receipt))),\
                    contextlib.redirect_stdout(io.StringIO()):
                # The source and compiler fixture intentionally use separate
                # Mesh objects so the transaction must select the right owner.
                compiler.objects[17]=SimpleNamespace(type=SimpleNamespace(name='Mesh'),peek_name=lambda:'HeadMesh',read_typetree=lambda:{})
                build.main()
                failed=root/'tools/game-test/artifacts/rejected-candidate'
                sys.argv=[*argv[:-1],str(failed)]
                with mock.patch.object(build,'validate_native_surface_restoration',side_effect=ValueError('rejected source transaction')):
                    with self.assertRaisesRegex(ValueError,'rejected source transaction'):
                        build.main()
                rejected=build.read(failed/'Receipts/head.bundle.json')
                self.assertEqual(rejected['target']['sha256'],build.sha(failed/'Catalog/head.bundle'))
                self.assertEqual(rejected['restoredSurfaces'],[mesh])
                self.assertFalse((failed/'ResourceAssembly.json').exists())
            self.assertEqual(len(observed),1)
            receipt=build.read(output/'Receipts/head.bundle.json');assembly=build.read(output/'ResourceAssembly.json')
            self.assertEqual(receipt,observed[0]);self.assertEqual(receipt['compiledBundle'],pin(compiled))
            self.assertEqual(receipt['sourceCatalog']['manifest'],pin(manifest_path));self.assertEqual(receipt['nativeIndex'],pin(native_index))
            self.assertEqual(receipt['restoredSurfaces'][0]['packet'],pin(packet_path))
            self.assertEqual(receipt['restoredSurfaces'][0]['nativePacket'],pin(native_path))
            self.assertEqual(assembly['nativeSkinSurfaces']['inputs'][0],pin(plan))
            self.assertEqual(assembly['catalogSha256'],build.sha(output/'Catalog'/build.RESOURCE_FILES['manifest']))
            self.assertEqual((output/'Catalog/body.bundle').read_bytes(),other.read_bytes())
            self.assertEqual(original.read_bytes(),b'original head source');self.assertEqual(other.read_bytes(),b'unchanged body source')

    def test_native_surface_restoration_keeps_complete_topology_and_other_prefab_owners(self):
        import character_resources as resources
        native=dict(vertexCount=3,sourceFingerprint='native',bindPoses=[1.]*16,vertices=[0.,0.,0.,1.,0.,0.,0.,1.,0.],
            normals=[0.,0.,1.]*3,tangents=[1.,0.,0.,1.]*3,weights=[1.,0.,0.,0.]*3,boneIndices=[0]*12,
            colors=[],uv=[dict(channel=0,dimension=2,values=[0.]*12)],subMeshes=[[0,1,2]],
            shapes=[dict(index=0,name='Shape',frames=[dict(weight=100.,vertices=[0.]*9,normals=[0.]*9,tangents=[0.]*9)])])
        refined=copy.deepcopy(native);refined.update(vertexCount=4,sourceFingerprint='refined',subMeshes=[[0,3,2,3,1,2]])
        for key,width in(('vertices',3),('normals',3),('tangents',4),('weights',4),('boneIndices',4)):
            refined[key]+=native[key][:width]
        refined['uv'][0]['values']+=[0.]*4
        for key in('vertices','normals','tangents'):refined['shapes'][0]['frames'][0][key]+=[0.]*3
        # Restoration intentionally replaces modified prefix/color/frame values.
        refined['vertices'][0]=1.e-7;refined['colors']=[.5]*16
        refined['shapes'][0]['frames'][0]['vertices'][0]=.01
        def typed(value):
            if type(value)is float:return float(np.float32(value))
            if isinstance(value,dict):return {key:typed(item)for key,item in value.items()}
            if isinstance(value,list):return [typed(item)for item in value]
            return value
        class SerializedObject:
            def __init__(self,kind,packet=None,raw=b'original',key=0,tree=None):
                self.type=SimpleNamespace(name=kind);self.packet=packet;self.path_id=key;self.data=None
                self.tree=typed(tree)if kind=='SkinnedMeshRenderer'else tree
                self.raw=json.dumps(self.tree).encode()if kind=='SkinnedMeshRenderer'and tree is not None else raw
            def get_raw_data(self):return self.raw
            def peek_name(self):return self.packet.get('name','Surface')if self.packet else'Surface'
            def read_typetree(self):return self.tree
            def save_typetree(self,value):
                self.tree=typed(copy.deepcopy(value));self.data=json.dumps(self.tree).encode()
        obj=SerializedObject
        first=copy.deepcopy(native);first['name']='First'
        second=copy.deepcopy(native);second.update(name='Second',sourceFingerprint='native-second')
        current_first=copy.deepcopy(refined);current_first['name']='First'
        current_second=copy.deepcopy(refined);current_second.update(name='Second',sourceFingerprint='refined-second')
        before=SimpleNamespace(name='CAB_fixture',externals=[],objects={1:obj('Mesh',current_first),2:obj('Material'),3:obj('Mesh',current_second)})
        after=SimpleNamespace(name='CAB_fixture',externals=[],objects={1:obj('Mesh',first),2:obj('Material'),3:obj('Mesh',second)})
        compiled=SimpleNamespace(name='CAB_compiled',objects={7:obj('Mesh',first),8:obj('Mesh',second)})
        game_a=SimpleNamespace(name='CAB_game_a',objects={9:obj('Mesh',first)})
        game_b=SimpleNamespace(name='CAB_game_b',objects={19:obj('Mesh',second)})
        game_prefab=SimpleNamespace(name='CAB_game_prefab',objects={})
        for file in(before,after,compiled,game_a,game_b):
            for key,value in file.objects.items():value.path_id=key
        bones=['Bip001/Head'];asset='Assets/Native/Prefab.prefab';address='Shape/Prefabs/Shape.prefab'
        def renderer(owner,key,mesh):
            value=obj('SkinnedMeshRenderer',key=key,tree=dict(m_Mesh=dict(m_FileID=0,m_PathID=mesh),bones=bones.copy()))
            value.assets_file=owner;return value
        current_renderers=[('First',renderer(before,101,1)),('Second',renderer(before,103,3))]
        native_renderers=[('First',renderer(game_prefab,201,9)),('Second',renderer(game_prefab,203,19))]
        def resolve_current(_,pointer):return before,before.objects[pointer['m_PathID']]
        def resolve_native(_,pointer):
            owner=game_a if pointer['m_PathID']==9 else game_b
            return owner,owner.objects[pointer['m_PathID']]
        with tempfile.TemporaryDirectory()as folder:
            root=Path(folder);paths=[root/name for name in('source.bundle','target.bundle','compiled.bundle','external-a.bundle','external-b.bundle','prefab.bundle')]
            for path in paths:path.write_bytes(path.name.encode())
            pin=lambda path:dict(path=str(path),sha256=build.sha(path))
            manifest=root/'manifest.json';manifest.write_text(json.dumps(dict(resources=[dict(source=address,asset=asset,bundles=[dict(file=paths[0].name)])])))
            assembly=root/'assembly.json';assembly.write_text(json.dumps(dict(catalogSha256=build.sha(manifest),proofs=[dict(file=paths[0].name,newCab=before.name,sha256=build.sha(paths[0]))])))
            index=root/'native-index.json';index.write_text(json.dumps(dict(bundles=[dict(cab=owner.name,source=str(path),sha256=build.sha(path),file=path.name,
                parts=[dict(sourceKey=address,asset=asset)]if owner is game_prefab else[])for owner,path in((game_a,paths[3]),(game_b,paths[4]),(game_prefab,paths[5]))])))
            rows=[];audit=[]
            for key,renderer_id,packet,path_id,path in((1,201,first,9,paths[3]),(3,203,second,19,paths[4])):
                packet_path=root/(packet['name']+'.json.gz')
                with gzip.open(packet_path,'wt',encoding='utf-8')as stream:json.dump(packet,stream)
                reference=dict(**pin(path),pathId=path_id)
                rows.append(dict(meshPathId=key,name=packet['name'],resource=address,renderer=packet['name'],
                    currentFingerprint=before.objects[key].packet['sourceFingerprint'],nativePacket=pin(packet_path),nativeMesh=reference))
                audit.append(dict(asset=asset,renderer=renderer_id,nativeMesh=reference,sourceBonePaths=bones.copy(),permutation=[0]))
            receipt=dict(source=pin(paths[0]),target=pin(paths[1]),compiledBundle=pin(paths[2]),nativeIndex=pin(index),
                sourceCatalog=dict(manifest=pin(manifest),assembly=pin(assembly)),changedObjects=[1,3],restoredSurfaces=rows,
                allOtherObjectBytesExact=True,resourcePayloadsExact=True,cabAndExternalReferencesUnchanged=True,savedObjectBytesExact=True)
            files=dict(zip(paths,(before,after,compiled,game_a,game_b,game_prefab)))
            environments={path:SimpleNamespace(file=SimpleNamespace(files={file.name:file,'retained.resS':b'original stream'}))for path,file in files.items()}
            def skins(_,proofs,address,load):
                return (current_renderers,resolve_current)if any(row['newCab']==before.name for row in proofs.values())else(native_renderers,resolve_native)
            with mock.patch.object(resources,'load_serialized_bundle',side_effect=lambda path,**_:(environments[Path(path)],files[Path(path)])), \
                    mock.patch.object(resources,'audit_native_bone_orders',return_value=dict(status='PASS_NATIVE_SKIN_SLOT_AUDIT',rows=audit)), \
                    mock.patch.object(resources,'skin_bone_paths',side_effect=lambda file,tree,resolve:tree['bones']), \
                    mock.patch.object(build,'resource_prefab_skins',side_effect=skins), \
                    mock.patch.object(build,'export_current_mesh',side_effect=lambda obj:copy.deepcopy(obj.packet)), \
                    mock.patch.object(build,'validate_retained_skin_prefix',side_effect=AssertionError('Restoration must use original full mesh authority.')):
                build.validate_native_surface_restoration(receipt)
                # The nonphysical path must retain every raw archive member;
                # a receipt flag or the new target bundle hash is not proof.
                for value in(bytearray(b'original stream'),SimpleNamespace(bytes=b'original stream')):
                    environments[paths[1]].file.files['retained.resS']=value
                    build.validate_native_surface_restoration(receipt)
                for defect in('changed','missing','extra','renamed'):
                    members=environments[paths[1]].file.files
                    members.pop('retained.resS',None);members.pop('extra.resS',None);members.pop('renamed.resS',None)
                    if defect=='changed':members['retained.resS']=b'altered stream'
                    elif defect=='extra':members.update({'retained.resS':b'original stream','extra.resS':b'extra'})
                    elif defect=='renamed':members['renamed.resS']=b'original stream'
                    with self.subTest(stream=defect),self.assertRaisesRegex(ValueError,'retained resource payload'):
                        build.validate_native_surface_restoration(receipt)
                environments[paths[1]].file.files.pop('renamed.resS',None)
                environments[paths[1]].file.files['retained.resS']=b'original stream'
                build.validate_native_surface_restoration(receipt)

                # Exercise the real validator's palette closure independently
                # of the separately checked typed native-authority resolver.
                source_paths=['Bip001/Head','Bip001/Extra'];target_paths=['Bip001/Head']
                old_bindposes=current_first['bindPoses'][:]
                current_first['bindPoses']=old_bindposes*2
                source_tree=dict(m_Mesh=dict(m_FileID=0,m_PathID=1),bones=source_paths)
                target_tree=dict(m_Mesh=dict(m_FileID=0,m_PathID=1),bones=target_paths,
                    m_AABB=dict(m_Center=dict(x=.1,y=.2,z=.3),m_Extent=dict(x=.4,y=.5,z=.6)))
                proof=dict(sourceBonePaths=source_paths,targetBonePaths=target_paths)
                for identifier in(101,111):
                    before.objects[identifier]=obj('SkinnedMeshRenderer',key=identifier,tree=copy.deepcopy(source_tree),raw=json.dumps(source_tree).encode())
                    after.objects[identifier]=obj('SkinnedMeshRenderer',key=identifier,tree=copy.deepcopy(target_tree),raw=json.dumps(target_tree).encode())
                updates=[dict(proof,renderer=identifier,mesh=1)for identifier in(101,111)]
                receipt.update(rendererBoneUpdates=updates,changedObjects=[1,3,101,111])
                rows[0].update(faceRecovery={},packet=rows[0]['nativePacket'])
                def resolved_native(*args,target_packets=None,target_bindings=None,target_palettes=None):
                    target_packets.update({1:first,3:second})
                    target_palettes[1]=dict(sourceBonePaths=source_paths,bonePaths=target_paths)
                    return {1:first,3:second}
                try:
                    with mock.patch.object(build,'native_surface_restoration_sources',side_effect=resolved_native), \
                            mock.patch.object(resources,'extend_skin_bone_palette',return_value=(target_tree,proof)):
                        build.validate_native_surface_restoration(receipt)
                        for declared in([],updates[:1],[dict(updates[0],targetBonePaths=['Other']),updates[1]]):
                            receipt['rendererBoneUpdates']=declared
                            with self.subTest(palette=declared),self.assertRaisesRegex(ValueError,'palette'):
                                build.validate_native_surface_restoration(receipt)
                        # The old omission hole also left both real SMRs at
                        # their source palette, so source-byte equality passed.
                        receipt['rendererBoneUpdates']=[]
                        for identifier in(101,111):after.objects[identifier]=before.objects[identifier]
                        with self.assertRaisesRegex(ValueError,'palette'):
                            build.validate_native_surface_restoration(receipt)
                        for identifier in(101,111):
                            after.objects[identifier]=obj('SkinnedMeshRenderer',key=identifier,tree=copy.deepcopy(target_tree),raw=json.dumps(target_tree).encode())
                        receipt['rendererBoneUpdates']=updates
                        build.validate_native_surface_restoration(receipt)
                        # Saved culling values use the source float32 schema.
                        # Bone references and any changed encoded byte remain exact.
                        self.assertNotEqual(target_tree,after.objects[101].read_typetree())
                        for defect in('bounds','mesh','bones','encoded-bytes'):
                            altered=copy.deepcopy(target_tree)
                            if defect=='bounds':altered['m_AABB']['m_Center']['x']+=.001
                            elif defect=='mesh':altered['m_Mesh']['m_PathID']=99
                            elif defect=='bones':altered['bones']=['Other']
                            bad=obj('SkinnedMeshRenderer',key=101,tree=altered)
                            if defect=='encoded-bytes':bad.data=bad.raw+b'corrupt'
                            after.objects[101]=bad
                            with self.subTest(renderer=defect),self.assertRaisesRegex(ValueError,'palette'):
                                build.validate_native_surface_restoration(receipt)
                        after.objects[101]=obj('SkinnedMeshRenderer',key=101,tree=copy.deepcopy(target_tree))
                        build.validate_native_surface_restoration(receipt)
                    retained_renderer=after.objects.pop(111)
                    try:
                        with self.assertRaisesRegex(ValueError,'prefab ownership'):
                            build.validate_native_surface_restoration(receipt)
                    finally:
                        after.objects[111]=retained_renderer
                finally:
                    for identifier in(101,111):before.objects.pop(identifier);after.objects.pop(identifier)
                    receipt.pop('rendererBoneUpdates');receipt['changedObjects']=[1,3]
                    rows[0].pop('faceRecovery');rows[0].pop('packet')
                    current_first['bindPoses']=old_bindposes
                build.validate_native_surface_restoration(receipt)

                # Replay the declared operation order, independently of the
                # order in which Unity stored the selected Mesh objects.
                original_receipt=dict(receipt);original_order=rows[:]
                rows.reverse()
                replay_fields=dict(meshes=[dict(pathId=row['meshPathId'],fingerprint={1:first,3:second}[row['meshPathId']]['sourceFingerprint'])for row in rows],
                    cloth=dict(renderDeltas={17:4},virtualDeltas={19:5},changes=[]),nativeClothSources=[],
                    changedObjects=[1,3],objectsAdded=[],rendererBoneUpdates=[],rendererMaterialUpdates=[],materialFloatUpdates=[])
                receipt.update(copy.deepcopy(replay_fields));orders=[]
                def physical_authority(*args,target_packets=None,target_bindings=None,target_palettes=None):
                    target_packets.update({1:first,3:second});target_bindings[1]={}
                    return {1:first,3:second}
                def replay_writer(source,expected,target,replacements,**kwargs):
                    order=[row['sourcePathIds'][0]for row in replacements];orders.append(order)
                    result=copy.deepcopy(replay_fields)
                    result['meshes']=[dict(pathId=key,fingerprint={1:first,3:second}[key]['sourceFingerprint'])for key in order]
                    return dict(result,target=copy.deepcopy(receipt['target']))
                try:
                    with mock.patch.object(build,'native_surface_restoration_sources',side_effect=physical_authority), \
                            mock.patch.object(resources,'prepare_standalone_part',side_effect=replay_writer)as writer:
                        build.validate_native_surface_restoration(receipt)
                        self.assertEqual(orders,[[3,1]])
                        build.validate_native_surface_restoration(json.loads(json.dumps(receipt)))
                        self.assertEqual(orders,[[3,1],[3,1]])
                        def reordered(*args,**kwargs):
                            result=replay_writer(*args,**kwargs);result['meshes'].reverse();return result
                        writer.side_effect=reordered
                        with self.assertRaisesRegex(ValueError,'source deformation transaction: meshes'):
                            build.validate_native_surface_restoration(receipt)
                finally:
                    rows[:]=original_order;receipt.clear();receipt.update(original_receipt)

                source_metadata=json.loads(assembly.read_text());source_metadata['proofs'][0]['sha256']='0'*64
                saved_assembly=assembly.read_bytes();assembly.write_text(json.dumps(source_metadata));receipt['sourceCatalog']['assembly']=pin(assembly)
                with self.assertRaisesRegex(ValueError,'source catalog bundle'):build.validate_native_surface_restoration(receipt)
                assembly.write_bytes(saved_assembly);receipt['sourceCatalog']['assembly']=pin(assembly)
                packet_path=Path(rows[0]['nativePacket']['path']);saved_packet=packet_path.read_bytes()
                altered=copy.deepcopy(first);altered['shapes'][0]['frames'][0]['vertices'][0]=.5
                with gzip.open(packet_path,'wt',encoding='utf-8')as stream:json.dump(altered,stream)
                rows[0]['nativePacket']=pin(packet_path)
                with self.assertRaisesRegex(ValueError,'original source bundle mesh'):build.validate_native_surface_restoration(receipt)
                packet_path.write_bytes(saved_packet);rows[0]['nativePacket']=pin(packet_path)
                rows[0]['nativeMesh']=rows[1]['nativeMesh']
                with self.assertRaisesRegex(ValueError,'prefab mesh authority'):build.validate_native_surface_restoration(receipt)
                rows[0]['nativeMesh']=audit[0]['nativeMesh']
                current_renderers[0][1].read_typetree()['bones']=['Other']
                with self.assertRaisesRegex(ValueError,'incompatible renderer bones'):build.validate_native_surface_restoration(receipt)
                current_renderers[0][1].read_typetree()['bones']=bones.copy()
                current_first['bindPoses'][0]=2.
                with self.assertRaisesRegex(ValueError,'incompatible renderer bones'):build.validate_native_surface_restoration(receipt)
                current_first['bindPoses'][0]=1.
                game_a.objects[9]=obj('Mesh',current_first,key=9)
                with self.assertRaisesRegex(ValueError,'original source bundle mesh'):build.validate_native_surface_restoration(receipt)
                game_a.objects[9]=obj('Mesh',first,key=9)
                rows[0]['currentFingerprint']='other-current'
                with self.assertRaisesRegex(ValueError,'another current source'):build.validate_native_surface_restoration(receipt)
                rows[0]['currentFingerprint']='refined'
                after.objects[2]=obj('Material',raw=b'changed')
                with self.assertRaisesRegex(ValueError,'unrelated object'):build.validate_native_surface_restoration(receipt)
                after.objects[2]=obj('Material')
                wrong=copy.deepcopy(first);wrong['sourceFingerprint']='other topology';wrong['subMeshes']=[[0,2,1]]
                after.objects[1]=obj('Mesh',wrong)
                with self.assertRaisesRegex(ValueError,'complete native numeric'):build.validate_native_surface_restoration(receipt)
                after.objects[1]=obj('Mesh',first)
                packet_path=Path(rows[0]['nativePacket']['path']);packet_path.write_bytes(b'changed')
                with self.assertRaisesRegex(ValueError,'native surface packet changed'):build.validate_native_surface_restoration(receipt)

    def test_standalone_material_slots_keep_existing_owners_and_close_only_declared_clones(self):
        import character_resources as resources
        class Object:
            def __init__(self,key,kind,tree):
                self.path_id=key;self.type=SimpleNamespace(name=kind);self.tree=copy.deepcopy(tree);self.data=None
                self.class_id=28 if kind=='Texture2D'else 0;self.serialized_type=SimpleNamespace(class_id=self.class_id)
                if kind=='Texture2D'and'image data'in self.tree:self.tree['image data']=bytes(self.tree['image data'])
                if kind=='AssetBundle':self.tree['m_Container']=[tuple(value)for value in self.tree['m_Container']]
            def read_typetree(self):return copy.deepcopy(self.tree)
            def save_typetree(self,tree):
                self.tree=copy.deepcopy(tree);self.data=self.get_raw_data()
            def get_raw_data(self):return json.dumps(self.tree,sort_keys=True,separators=(',',':'),default=list).encode()
            def peek_name(self):return self.tree.get('m_Name','')
            def read(self):return SimpleNamespace(get_image_data=lambda:bytes(self.tree.get('image data',[])))
        def encode(file):
            return json.dumps({str(key):dict(kind=obj.type.name,tree=obj.tree)for key,obj in file.objects.items()},sort_keys=True,default=list).encode()
        def load(path,**kwargs):
            rows=json.loads(Path(path).read_bytes())
            file=SimpleNamespace(name='CAB_fixture',externals=[],header=SimpleNamespace(version=22),target_platform=19,
                types=[SimpleNamespace(class_id=28)],objects={int(key):Object(int(key),row['kind'],row['tree'])for key,row in rows.items()})
            archive=SimpleNamespace(files={'CAB_fixture':file,'retained.resS':b'unchanged stream'},save=lambda **_:encode(file))
            return SimpleNamespace(file=archive),file
        pointer=lambda key:dict(m_FileID=0,m_PathID=key)
        material=dict(m_Name='Hair',m_Shader=dict(m_FileID=1,m_PathID=9),m_ShaderKeywords='SOURCE',
            m_SavedProperties=dict(m_Floats=[['_Strength',.65]],m_Colors=[['_HairColor',dict(r=.2,g=.3,b=.4,a=1)]],
                m_TexEnvs=[['_GroomColor',dict(m_Texture=dict(m_FileID=2,m_PathID=42),m_Scale=dict(x=1,y=1),m_Offset=dict(x=0,y=0))]]))
        rows={1:Object(1,'AssetBundle',dict(m_PreloadTable=[pointer(key)for key in(2,3,4,5)],
                    m_Container=[['actor',dict(preloadIndex=0,preloadSize=4,asset=pointer(7))]])),
              2:Object(2,'SkinnedMeshRenderer',dict(m_Mesh=pointer(3),m_Materials=[pointer(4)])),
              3:Object(3,'Mesh',dict(m_Name='Hair',m_SubMeshes=[{}],_fingerprint='before')),
              4:Object(4,'Material',material),
              5:Object(5,'SkinnedMeshRenderer',dict(m_Mesh=pointer(3),m_Materials=[pointer(4)])),
              6:Object(6,'Texture2D',dict(m_Name='Unselected',pixels='untouched')),
              7:Object(7,'GameObject',dict(m_Component=[dict(component=pointer(key))for key in(2,5)]))}
        with tempfile.TemporaryDirectory()as folder:
            source=Path(folder)/'source.bundle';target=Path(folder)/'target.bundle'
            original=encode(SimpleNamespace(objects=rows));source.write_bytes(original)
            replacement=dict(sourceFingerprint='before',packet=dict(sourceFingerprint='after',vertexCount=6),binding=None,
                compiledTree=dict(m_Name='Compiled',m_SubMeshes=[{},{}],m_StreamData=dict(size=0),_fingerprint='after'))
            export=lambda obj:dict(sourceFingerprint=obj.tree['_fingerprint'],subMeshes=[[0,1,2]]*len(obj.tree['m_SubMeshes']))
            update=dict(meshPathId=3,slots=[dict(sourceSlot=0,targetSlot=1)])
            with mock.patch.object(resources,'load_serialized_bundle',side_effect=load),\
                    mock.patch.object(resources,'prepare_native_cloth',return_value=dict(changes=[])),\
                    mock.patch.object(resources,'prepare_native_cloth_state',return_value=[]):
                result=resources.prepare_standalone_part(source,build.sha(source),target,[replacement],export_mesh=export,material_slot_updates=[update])
                _,saved=load(target);proofs=result['rendererMaterialUpdates']
                self.assertEqual(len(proofs),2)
                clone=proofs[0]['addedSlots'][0]['materialPathId']
                self.assertEqual(proofs[1]['addedSlots'][0]['materialPathId'],clone)
                self.assertNotIn(clone,rows)
                self.assertEqual(result['objectsAdded'],[clone])
                self.assertEqual(len(saved.objects),len(rows)+len(result['objectsAdded']))
                self.assertTrue({1,2,3,5,clone}<=set(result['changedObjects']))
                self.assertEqual(saved.objects[clone].read_typetree(),material)
                for key in(2,5):self.assertEqual(saved.objects[key].tree['m_Materials'],[pointer(4),pointer(clone)])
                for key in(4,6):self.assertEqual(saved.objects[key].get_raw_data(),rows[key].get_raw_data())
                meta=saved.objects[1].tree;entry=meta['m_Container'][0][1]
                self.assertIn(pointer(clone),meta['m_PreloadTable'][entry['preloadIndex']:entry['preloadIndex']+entry['preloadSize']])
                self.assertEqual(source.read_bytes(),original)
                # A recursive source can use the same material object in core and fine slots.
                # Isolate that material before the real texture transaction updates the fine atlas.
                alias_source=Path(folder)/'alias.bundle';isolated=Path(folder)/'isolated.bundle';textured=Path(folder)/'textured.bundle'
                for key in(2,5):saved.objects[key].tree['m_Materials']=[pointer(4),pointer(4)]
                alias_source.write_bytes(encode(saved));alias_bytes=alias_source.read_bytes()
                old_packet=dict(vertexCount=6,subMeshes=[[0,1,2],[3,4,5]],uv=[dict(channel=2,dimension=4,
                    values=[0.,0.,0.,1.]*3+[0.,0.,49.,1.]*3)])
                finished=dict(old_packet,subMeshes=[[0,1,2,3,4,5]])
                with mock.patch.object(build,'export_current_mesh',return_value=old_packet):
                    slots=build.groom_fine_material_slots(saved,3,finished)
                self.assertEqual(slots,[dict(sourceSlot=0,targetSlot=2)])
                repeated=dict(replacement,sourceFingerprint='after',packet=dict(sourceFingerprint='recursive',vertexCount=9),
                    compiledTree=dict(m_Name='Compiled',m_SubMeshes=[{},{},{}],m_StreamData=dict(size=0),_fingerprint='recursive'))
                repeated_receipt=resources.prepare_standalone_part(alias_source,build.sha(alias_source),isolated,[repeated],
                    export_mesh=export,material_slot_updates=[dict(meshPathId=3,slots=slots)])
                _,prepared=load(isolated);fine_id=repeated_receipt['rendererMaterialUpdates'][0]['addedSlots'][0]['materialPathId']
                self.assertNotIn(fine_id,saved.objects)
                atlas=Path(folder)/'atlas.bundle'
                atlas.write_bytes(encode(SimpleNamespace(objects={100:Object(100,'Texture2D',{'m_Name':'FineAtlas',
                    'm_StreamData':dict(offset=0,size=0,path=''),'image data':[13,29,47,255]})})))
                maps=dict(prepared.objects[fine_id].tree['m_SavedProperties']['m_TexEnvs'])
                texture_receipt=resources.replace_owned_material_textures(isolated,build.sha(isolated),textured,[dict(pathId=fine_id,
                    property='_GroomColor',before=maps['_GroomColor']['m_Texture'],
                    texture=dict(path=str(atlas),sha256=build.sha(atlas),object=100,name='FineAtlas'))])
                _,finished_file=load(textured)
                for key in(4,6):self.assertEqual(finished_file.objects[key].get_raw_data(),saved.objects[key].get_raw_data())
                for key in(2,5):self.assertEqual(finished_file.objects[key].tree['m_Materials'],[pointer(4),pointer(4),pointer(fine_id)])
                fine_map=dict(finished_file.objects[fine_id].tree['m_SavedProperties']['m_TexEnvs'])['_GroomColor']['m_Texture']
                self.assertEqual(fine_map,pointer(texture_receipt['textures'][0]['object']))
                self.assertNotEqual(fine_map,dict(saved.objects[4].tree['m_SavedProperties']['m_TexEnvs'])['_GroomColor']['m_Texture'])
                self.assertEqual(len(finished_file.objects),len(saved.objects)+len(repeated_receipt['objectsAdded'])+len(texture_receipt['objectsAdded']))
                self.assertEqual(alias_source.read_bytes(),alias_bytes)
                for bad in(dict(meshPathId=99,slots=update['slots']),dict(meshPathId=3,slots=[dict(sourceSlot=1,targetSlot=1)]),
                           dict(meshPathId=3,slots=[dict(sourceSlot=0,targetSlot=2)]),dict(meshPathId=3,slots=update['slots'],undeclared=True)):
                    with self.subTest(bad=bad),self.assertRaises(ValueError):
                        resources.prepare_standalone_part(source,build.sha(source),target,[replacement],export_mesh=export,material_slot_updates=[bad])
                self.assertEqual(source.read_bytes(),original)
                overlay=copy.deepcopy(rows);overlay[2].tree['m_Materials'].append(pointer(4));source.write_bytes(encode(SimpleNamespace(objects=overlay)))
                with self.assertRaisesRegex(ValueError,'overlay'):
                    resources.prepare_standalone_part(source,build.sha(source),target,[replacement],export_mesh=export,material_slot_updates=[update])

    def test_source_curvature_can_relax_interior_creases_while_retaining_declared_endpoints(self):
        production,_=build.source_owner();side=21
        yy,xx=np.mgrid[-.02:.020001:complex(side),-.02:.020001:complex(side)]
        depth=-.002*np.exp(-(xx/.003)**2-(yy/.007)**2)
        points=np.column_stack((xx.ravel(),yy.ravel(),depth.ravel()))
        normals=np.tile([0.,0.,1.],(len(points),1));graph=[set()for _ in points]
        for y in range(side):
            for x in range(side):
                vertex=y*side+x
                for dx,dy in((1,0),(-1,0),(0,1),(0,-1)):
                    if 0<=x+dx<side and 0<=y+dy<side:graph[vertex].add((y+dy)*side+x+dx)
        anchors=[5*side+10,10*side+10,15*side+10]
        old,_,old_proof=production.anatomy_curvature_field(points,normals,graph,anchors,.012,{})
        corrected,_,proof=production.anatomy_curvature_field(points,normals,graph,anchors,.012,{},
            preserved_knots=[anchors[0],anchors[-1]])
        np.testing.assert_array_equal(old[anchors],0.)
        np.testing.assert_array_equal(corrected[[anchors[0],anchors[-1]]],0.)
        self.assertGreater(corrected[anchors[1],2],.001)
        self.assertLess(corrected[anchors[1],2],.002)
        self.assertEqual(old_proof['preservedKnotVertices'],anchors)
        self.assertEqual(proof['preservedKnotVertices'],[anchors[0],anchors[-1]])
        with self.assertRaisesRegex(ValueError,'anatomical endpoints'):
            production.anatomy_curvature_field(points,normals,graph,anchors,.012,{},preserved_knots=anchors[:2])

        triangles=[]
        for row in range(side-1):
            for column in range(side-1):
                vertex=row*side+column;triangles.extend([vertex,vertex+1,vertex+side,vertex+1,vertex+side+1,vertex+side])
        packet=dict(vertexCount=len(points),vertices=points.ravel().tolist(),normals=normals.ravel().tolist(),
            subMeshes=[triangles],weights=[1.,0,0,0]*len(points),boneIndices=[0,0,0,0]*len(points),bindPoses=np.eye(4).ravel().tolist())
        skin=dict(bones=['Rig/Bip001 Head'],mesh='Skin',rendererToPrefab=np.eye(4).ravel().tolist(),materials=[dict(shader='Lux/Human/Skin')])
        controls=[(points[index],points[index],.1)for index in anchors]
        reports=[dict(name=name,sourceVertex=index)for name,index in zip(('Start','Interior','End'),anchors)]
        unchanged=production.interpolate_source_handles(packet,skin,None,points,controls,copy.deepcopy(reports),np.ones(len(points)),np.eye(4))
        np.testing.assert_allclose(unchanged,points,rtol=0,atol=1e-18)
        candidate=production.interpolate_source_handles(packet,skin,None,points,controls,reports,np.ones(len(points)),np.eye(4),
            sections=[dict(id='OwnedFold',anchors=['Start','Interior','End'],preservedKnots=['Start','End'],radiusMetres=.012,retain=.4)])
        np.testing.assert_array_equal(candidate[[anchors[0],anchors[-1]]],points[[anchors[0],anchors[-1]]])
        self.assertGreater(candidate[anchors[1],2]-points[anchors[1],2],.0006)
        self.assertEqual(reports[0]['curvatureSections'][0]['id'],'OwnedFold')

    def test_declared_contour_handles_share_the_design_compiler_and_leave_other_sources_unchanged(self):
        production,art=build.source_owner()
        points=np.array([[-.04,-.02,1.],[.04,-.02,1.],[0.,-.035,.98]])
        packet=dict(vertices=art.points(points.ravel().tolist(),np.linalg.inv(art.CONVERT)).ravel().tolist(),subMeshes=[[0,1,2]])
        locations={'b_eye_l':[-.03,0,1.02],'b_eye_r':[.03,0,1.02],
            'b_lip_upper_c':[0,0,.942],'b_lip_lower_c':[0,0,.938],
            'b_nostril_l':[-.01,0,.98],'b_nostril_r':[.01,0,.98]}
        for side,sign in(('l',-1),('r',1)):
            locations.update({f'b_cheek_upper_{side}':[sign*.04,0,1.],f'b_cheek_lower_{side}':[sign*.04,0,.96],
                f'b_eyebrow_{side}_01':[sign*.03,0,1.04],f'b_lip_corner_{side}':[sign*.02,0,.94]})
        worlds={}
        for name,point in locations.items():
            matrix=np.eye(4);matrix[:3,3]=art.points(point,np.linalg.inv(art.CONVERT))[0]
            worlds[name]=matrix.T.ravel().tolist()
        names=['SourceX','SourceY'];skin=dict(path='FaceShape_LOD0')
        assets=[dict(name=name,skins=[skin])for name in names]
        fixtures=[dict(name=name,id=name,race='NewShape',sex='Female',origin='Native',faceDefinition='FaceShape_A')for name in names]
        poses=[dict(name=name,neutralPose=dict(worldMatrices=worlds))for name in names]
        columns=['jawIn','zygomaIn','zygomaBack','lowerCheekOut','nasalWingIn','nasalTipBack','browBack','lipBack','chinUp']
        plans=dict(columns=columns,cohorts={'NewShape/Female/Native':{'A':[0.]*len(columns)}},
            contours={'SourceX':dict(values=[0.]*len(columns))},surfaces={name:dict(contourRenderer='FaceShape_LOD0')for name in names})
        custom=dict(name='UpperCheek_l',at=points[0].tolist(),moveMm=[0.,-2.,0.],radiusMetres=.025,
            maximumAnchorDistanceMetres=.001,surfaceNormal=[0.,-1.,0.])
        extra=dict(custom,name='AuthoredMidSurface',at=points[2].tolist())
        with tempfile.TemporaryDirectory()as directory:
            root=Path(directory);workspace=root/'tools';workspace.mkdir()
            paths={name:workspace/(name+'.json')for name in('census','fixtures','poses','plans')}
            for key,value in(('census',dict(assets=assets)),('fixtures',fixtures),('poses',dict(assets=poses))):build.write(paths[key],value)
            outputs=[]
            cases=[dict(handles=[]),dict(handles=[custom,extra]),dict(handles=[],overrideAll=True),
                dict(handles=[],missingReference=True,error='no neutral reference'),
                dict(handles=[],unusedDefault=True,error='no source consumer'),
                dict(handles=[],unknownSource=True,error='no source consumer')]
            for iteration,case in enumerate(cases):
                plans['contours']={'SourceX':dict(values=[0.]*len(columns),handles=case['handles'])}
                if case.get('overrideAll'):plans['contours']['SourceY']=dict(values=[0.]*len(columns))
                if case.get('unknownSource'):plans['contours']['UnknownSource']=dict(values=[0.]*len(columns))
                plans['cohorts']['NewShape/Female/Native']={'A':[0.]*len(columns)}
                if case.get('unusedDefault'):plans['cohorts']['NewShape/Female/Native']['Z']=[0.]*len(columns)
                build.write(paths['plans'],plans)
                build.write(paths['poses'],dict(assets=poses[:1]if case.get('missingReference')else poses))
                compiled={}
                def compile_existing(args,sources):
                    design=build.read(args.design);compiled[design['asset']]=design
                    document=dict(profiles=[dict(id=design['asset'])]);build.write(args.output,document)
                    return document
                argv=['face-build','--output',str(workspace/str(iteration)),'--census',str(paths['census']),
                    '--reference-index',str(paths['poses']),'--fixtures',str(paths['fixtures']),'--plans',str(paths['plans'])]
                with mock.patch.object(build,'ROOT',root),mock.patch.object(sys,'argv',argv),\
                    mock.patch.object(build,'source_owner',return_value=(production,art)),\
                    mock.patch.object(production,'native_packet',return_value=packet),\
                    mock.patch.object(production,'apply_native_reference_pose',return_value=packet),\
                    mock.patch.object(production,'facial_material_slots',return_value=[0]),\
                    mock.patch.object(production,'anatomy_surface_pair',return_value=([0,1],{})),\
                    mock.patch.object(art,'native_normals',return_value=np.tile([0.,-1.,0.],(3,1))),\
                    mock.patch.object(build,'compile_source_face_design',side_effect=compile_existing)as common,contextlib.redirect_stdout(io.StringIO()):
                    if case.get('error'):
                        with self.assertRaisesRegex(ValueError,case['error']):build.compile_face_designs()
                    else:build.compile_face_designs()
                self.assertEqual(common.call_count,0 if case.get('error')else 2)
                if not case.get('error'):outputs.append(compiled)
            for value in outputs[1].values():value.pop('referencePose')
            for value in outputs[0].values():value.pop('referencePose')
            self.assertEqual(outputs[0]['SourceY'],outputs[1]['SourceY'])
            changed=outputs[1]['SourceX']['handles'];self.assertEqual(next(row for row in changed if row['name']==custom['name']),custom)
            self.assertEqual(changed[-1],extra)
            baseline=[row for row in outputs[0]['SourceX']['handles']if row['name']!=custom['name']]
            retained=[row for row in changed if row['name']not in(custom['name'],extra['name'])]
            self.assertEqual(baseline,retained)

    def test_retained_producer_reads_its_pinned_package_configuration_instead_of_live_names(self):
        with tempfile.TemporaryDirectory()as folder:
            root=Path(folder);configuration=copy.deepcopy(build.RESOURCE_PACKAGE)
            configuration['releaseMetadata']['name']='Retained configuration'
            build.write(root/'resource-package.json.source',configuration)
            live=copy.deepcopy(configuration);live['releaseMetadata']['name']='Other configuration'
            build.write(root/'resource-package.json',live)
            with mock.patch.object(build,'__file__',str(root/'build_models.py.source')):
                resolved=build.resource_package_configuration()
            self.assertEqual(resolved['releaseMetadata']['name'],configuration['releaseMetadata']['name'])

    def test_missing_skin_source_resolves_only_identical_declared_catalog_content_without_rewriting_history(self):
        with tempfile.TemporaryDirectory()as folder:
            root=Path(folder);original=root/'retired-resource';retained=root/'retained-resource'
            retained.write_bytes(b'complete original resource identity');digest=build.sha(retained)
            recipe=root/'calculation.json'
            build.write(recipe,dict(source=str(original),sha256=digest,textures=[dict(object=17,channel='color',profile=dict(pigmentRetain=.7))]))
            before=recipe.read_bytes();proof=dict(appearanceFinishing=[dict(kind='source-skin-detail',inputPlan=dict(path=str(recipe),sha256=build.sha(recipe)))])
            with self.assertRaisesRegex(ValueError,'absent from the retained catalog'):
                build.source_skin_detail_authority(proof,17,'color')
            result=build.source_skin_detail_authority(proof,17,'color',retained_sources={digest:retained})
            self.assertEqual(result['source'],dict(path=str(retained.resolve()),sha256=digest,object=17))
            self.assertEqual(result['recovery']['originalLocation'],str(original))
            self.assertEqual(result['recovery']['originalSha256'],digest)
            self.assertEqual(recipe.read_bytes(),before)
            retained.write_bytes(b'changed retained resource')
            with self.assertRaisesRegex(ValueError,'differs from its original authority'):
                build.source_skin_detail_authority(proof,17,'color',retained_sources={digest:retained})
            original.write_bytes(b'changed original resource')
            with self.assertRaisesRegex(ValueError,'original skin-detail source changed'):
                build.source_skin_detail_authority(proof,17,'color',retained_sources={digest:retained})

    def test_paired_cheek_anchors_match_source_anatomical_heights_in_rotated_scaled_frames(self):
        production,_=build.source_owner()
        points=np.array([[-.060,0.,-.002],[-.059,0.,.002],[.065,0.,.006],[.060,0.,.002],[-.080,0.,.002],[.080,0.,.002]])
        normals=np.array([[-1.,0.,0.],[-1.,0.,0.],[1.,0.,0.],[1.,0.,0.],[-1.,0.,0.],[1.,0.,0.]])
        origins=np.array([[-.045,0.,.002],[.045,0.,.002]])
        ownership=np.array([1.,1.,1.,1.,0.,0.])
        ids,proof=production.anatomy_surface_pair(points,normals,ownership,origins,np.zeros(3),np.eye(3),.06)
        self.assertEqual(ids,(1,3))
        self.assertAlmostEqual(proof['bilateralSourceHeightResidualMetres'],0.)
        self.assertAlmostEqual(proof['surfaceBreadthMetres'],.119)
        rotation=np.array([[0.,-1.,0.],[1.,0.,0.],[0.,0.,1.]])
        translation=np.array([.4,-.7,1.8]);scale=1.7
        moved,scaled=production.anatomy_surface_pair(points@rotation.T*scale+translation,normals@rotation.T,ownership,
            origins@rotation.T*scale+translation,translation,rotation,.06*scale)
        self.assertEqual(moved,ids)
        self.assertAlmostEqual(scaled['surfaceBreadthMetres'],proof['surfaceBreadthMetres']*scale)
        with self.assertRaisesRegex(ValueError,'paired zygomatic'):
            production.anatomy_surface_pair(points,normals,np.zeros(len(points)),origins,np.zeros(3),np.eye(3),.06)

    def test_skin_resume_compares_complete_selected_declarations_and_keeps_other_sources_independent(self):
        with tempfile.TemporaryDirectory()as folder:
            source=Path(folder)/'plans.json.source'
            plans={'A':dict(profile=dict(futureParameter=1),renderers=[dict(path='Surface',slots=[0],shader='Skin')]),'B':dict(profile={})}
            source.write_text(json.dumps(dict(surfaces=plans)),encoding='utf-8')
            pin=dict(path=str(source),sha256=build.sha(source));current=copy.deepcopy(plans);current['B']['profile']['unrelated']=2
            build.validate_skin_plan_snapshot(current,{'A'},pin)
            current['A']['renderers'][0]['slots']=[1]
            with self.assertRaisesRegex(ValueError,'declarations changed'):build.validate_skin_plan_snapshot(current,{'A'},pin)
            current=copy.deepcopy(plans);current['A']['profile']['futureParameter']=2
            with self.assertRaisesRegex(ValueError,'declarations changed'):build.validate_skin_plan_snapshot(current,{'A'},pin)
            source.write_text('{}',encoding='utf-8')
            with self.assertRaisesRegex(ValueError,'retained source'):build.validate_skin_plan_snapshot(plans,{'A'},pin)

    def test_observed_skin_interface_requires_the_actual_visible_body_and_clothing(self):
        with tempfile.TemporaryDirectory()as folder:
            path=Path(folder)/'display.json'
            actor=dict(fixture=dict(id='A'),completed=True,nativeNpc={},nativeClothingRetained=True,
                parts=[dict(slot='Head',key='UB/CharacterResources/Head.prefab',present=True),dict(slot='Body',key='',present=False)])
            def context():
                path.write_text(json.dumps(dict(actors=[actor])),encoding='utf-8')
                return dict(kind='actual-npc-presentation',fixture='A',head='Head.prefab',body='Body.prefab',display=dict(path=str(path),sha256=build.sha(path)))
            self.assertIsNone(build.observed_skin_interface(context(),{}))
            actor['armorSources']=[dict(slot='Body',source='UB/CharacterResources/Body.prefab',present=True)]
            self.assertEqual(build.observed_skin_interface(context(),{}),'armor')
            actor['parts'][1].update(key='UB/CharacterResources/Body.prefab',present=True)
            row=context();self.assertEqual(build.observed_skin_interface(row,{}),'shape')
            row['body']='Other.prefab'
            with self.assertRaisesRegex(ValueError,'visible body'):build.observed_skin_interface(row,{})
            actor['nativeClothingRetained']=False
            with self.assertRaisesRegex(ValueError,'actual native clothing'):build.observed_skin_interface(context(),{})
            with self.assertRaisesRegex(ValueError,'clothing observation'):build.observed_skin_interface(dict(kind='actual-npc-presentation'),{})

    def test_attachment_roots_fit_every_support_and_retain_native_channels(self):
        production,_=build.source_owner()
        count=8;angle=np.arange(count)*2*np.pi/count
        ring=np.column_stack((np.cos(angle),np.sin(angle),np.zeros(count)))
        positions=np.concatenate((ring*.02+[0,0,.002],ring*.02+[0,0,.012],ring*.005+[0,0,.05],[[0,0,.05]]))
        triangles=[]
        for level in range(2):
            for i in range(count):
                a=level*count+i;b=level*count+(i+1)%count
                triangles.extend((a,b,a+count,b,b+count,a+count))
        for i in range(count):triangles.extend((count*2+i,count*2+(i+1)%count,count*3))
        total=len(positions);normals=np.concatenate((ring,ring,ring,[[0.,0.,1.]]))
        packet=dict(name='Attachment',nativeReadable=True,indexFormat=0,vertexCount=total,vertices=positions.ravel().tolist(),normals=normals.ravel().tolist(),
            tangents=np.tile([0.,0.,1.,1.],(total,1)).ravel().tolist(),weights=[1.,0.,0.,0.]*total,boneIndices=[0,0,0,0]*total,
            bindPoses=np.eye(4).ravel().tolist(),subMeshes=[triangles],uv=[dict(channel=0,dimension=2,values=[0.,0.,0.,0.]*total)],colors=[],
            shapes=[dict(index=0,name='NativeFit',frames=[dict(weight=100.,vertices=[1e-7,0.,0.]*total,normals=[0.,0.,0.]*total,tangents=[0.,0.,0.]*total)])],
            bounds=[0.,0.,.025,.04,.04,.05])
        supports=[dict(vertices=[[-1.,-1.,z],[1.,-1.,z],[1.,1.,z],[-1.,1.,z]],normals=[[0.,0.,1.]]*4,triangles=[0,1,2,0,2,3])for z in(0.,-.001)]
        source=copy.deepcopy(packet)
        candidate,proof=production.fit_rigid_attachment_roots(packet,np.eye(4),supports,depth_metres=.00035,falloff_radii=.85,maximum_shift_metres=.012)
        result=np.asarray(candidate['vertices']).reshape(-1,3)
        np.testing.assert_allclose(result[:count,2],-.00135,atol=1e-8)
        np.testing.assert_array_equal(result[2*count:],positions[2*count:])
        self.assertEqual(packet,source)
        for key in('uv','weights','boneIndices','bindPoses','shapes','subMeshes'):self.assertEqual(candidate[key],source[key])
        self.assertEqual((proof['rootLoops'],proof['supportSurfaces']),(1,2))
        self.assertTrue(np.isfinite(candidate['normals']).all());self.assertTrue(np.isfinite(candidate['tangents']).all())
        trimmed,cut=production.trim_attachment_collar(packet,np.eye(4),.4)
        self.assertEqual(cut['removedTriangles'],16);self.assertEqual(cut['rootLoops'],1)
        self.assertTrue(all(index>=count for slot in trimmed['subMeshes']for index in slot))
        for key in('vertices','normals','tangents','uv','weights','boneIndices','bindPoses','shapes'):
            self.assertEqual(trimmed[key],source[key])
        with self.assertRaisesRegex(ValueError,'support envelope'):
            production.fit_rigid_attachment_roots(packet,np.eye(4),supports,depth_metres=.00035,falloff_radii=.85,maximum_shift_metres=.0001)


    def test_dxt1_skin_field_preserves_unowned_blocks_and_transparency(self):
        import etcpak
        from PIL import Image
        build.source_owner();import material_fields as fields
        source=np.full((8,8,4),128,np.uint8);source[:,:,3]=255
        original=etcpak.compress_bc1(source.tobytes(),8,8)
        target=np.asarray(Image.frombytes('RGBA',(8,8),original,'bcn',1)).copy();target[:4,:4,:3]=[148,132,116]
        owned=np.zeros((8,8),bool);owned[:4,:4]=True
        payload,proof=fields.native_face_bc1_mip(original,target,owned)
        self.assertEqual(len(payload),len(original));self.assertEqual(payload[8:],original[8:])
        self.assertTrue(proof['alphaExact']);self.assertTrue(proof['unownedBlocksExact'])
        self.assertLessEqual(proof['maximumRgbError'],16)
        target[0,0,3]=128
        with self.assertRaisesRegex(ValueError,'native transparency'):fields.native_face_bc1_mip(original,target,owned)
        target[0,0,3]=255;owned[0,0]=False
        unchanged,_=fields.native_face_bc1_mip(original,target,owned)
        self.assertEqual(unchanged,original)
        target[:,:,3]=255;target[:4,:4,3]=120;owned[:4,:4]=True
        upgraded,_,upgrade=fields.native_face_bc3_mip(original,10,target,(~owned)[::-1].astype(float))
        saved=np.asarray(Image.frombytes('RGBA',(8,8),upgraded,'bcn',3))
        self.assertEqual(len(upgraded),len(original)*2)
        self.assertEqual(upgrade['nativeSourceFormat'],10)
        self.assertLessEqual(upgrade['nativeFaceMaximumRgbError'],16)
        self.assertTrue(np.all(saved[4:,:,3]==255))
        self.assertTrue(np.all(saved[:4,:4,3]==120))
        target[4,0,3]=135
        protected,_,_=fields.native_face_bc3_mip(original,10,target,np.ones((8,8)))
        self.assertTrue(np.all(np.asarray(Image.frombytes('RGBA',(8,8),protected,'bcn',3))[:,:,3]==255))

    def test_serialized_cut_has_explicit_measured_overlap_tolerance(self):
        build.source_owner();import material_fields as fields
        packet=dict(vertices=[0.,0.,0.,1.,0.,0.,1.,1.,0.,0.,1.,0.],normals=[0.,0.,1.]*4,
            tangents=[1.,0.,0.,1.]*4,subMeshes=[[0,1,2,0,2,3]],bindPoses=np.eye(4).ravel().tolist(),
            uv=[dict(channel=0,values=[0.,0.,0.,0.,1.,0.,0.,0.,1.,1.,0.,0.,0.,1.,0.,0.])])
        head=copy.deepcopy(packet);head['vertices'][2::3]=[.00005]*4
        with self.assertRaisesRegex(ValueError,'complete physical cut'):
            fields.serialized_skin_context(head,packet,['Head'],['Head'])
        *_,hc,bc,proof=fields.serialized_skin_context(head,packet,['Head'],['Head'],cut_tolerance_metres=.0001)
        self.assertEqual((len(hc.edges),len(bc.edges)),(4,4))
        self.assertEqual(proof['cutToleranceMetres'],.0001)
        self.assertEqual(head['vertices'][2::3],[.00005]*4)
        for invalid in (0.,-1.,float('nan'),float('inf')):
            with self.subTest(tolerance=invalid),self.assertRaisesRegex(ValueError,'finite positive'):
                fields.serialized_skin_context(head,packet,['Head'],['Head'],cut_tolerance_metres=invalid)

    def test_skin_resource_selects_typed_prefab_among_same_path_subassets(self):
        import character_resources as resources
        prefab=SimpleNamespace(type=SimpleNamespace(name='GameObject'))
        cloth=SimpleNamespace(type=SimpleNamespace(name='MonoBehaviour'))
        container=[('Assets/Face.prefab',SimpleNamespace(asset=SimpleNamespace(path_id=i,file_id=0)))for i in (1,2)]
        bundle=SimpleNamespace(type=SimpleNamespace(name='AssetBundle'),read=lambda:SimpleNamespace(m_Container=container))
        file=SimpleNamespace(name='source',objects={1:prefab,2:cloth,3:bundle})
        manifest=dict(resources=[dict(source='Face.prefab',asset='Assets/Face.prefab',bundles=[dict(file='face.bundle')])])
        with mock.patch.object(resources,'prefab_skin_renderers',return_value=[])as renderers:
            self.assertEqual(build.resource_skin_surfaces(None,manifest,{'face.bundle':dict(newCab='source')},'Face.prefab',lambda _:file),[])
            self.assertIs(renderers.call_args.args[0],prefab)
        cloth.type.name='GameObject'
        with self.assertRaisesRegex(ValueError,'unique prefab root'):
            build.resource_skin_surfaces(None,manifest,{'face.bundle':dict(newCab='source')},'Face.prefab',lambda _:file)

    def test_skin_material_reads_declared_samplers_and_preserves_shader_defaults(self):
        import character_resources as resources
        pointer=lambda key:dict(m_FileID=0,m_PathID=key)
        def obj(key,tree,**values):
            value=SimpleNamespace(path_id=key,read_typetree=lambda:tree,**values);objects[key]=value;return value
        objects={};file=SimpleNamespace(name='skin',objects=objects)
        renderer=obj(1,dict(m_Mesh=pointer(2),m_Materials=[pointer(3)]),assets_file=file)
        obj(2,{})
        sampler=lambda key:dict(m_Texture=pointer(key),m_Scale=dict(x=1.,y=1.),m_Offset=dict(x=0.,y=0.))
        obj(3,dict(m_Shader=pointer(4),m_SavedProperties=dict(m_TexEnvs=[('_MainTex',sampler(5)),
            ('_BumpMap',sampler(6)),('_ColorMaskMap',sampler(0)),('_SpecTex',sampler(0))],
            m_Floats=[('_BumpMapScale',.75),('_BumpScale',2.)])))
        obj(4,dict(m_ParsedForm=dict(m_Name='Lux/Human/SkinDragonborn',m_PropInfo=dict(m_Props=[
            dict(m_Name=name,m_DefTexture=dict(m_DefaultName=default))for name,default in
            [('_MainTex','white'),('_BumpMap','bump'),('_ColorMaskMap','white'),('_BumpMapScale','')]]))))
        for key in (5,6):obj(key,{},read=lambda:SimpleNamespace(m_Name='texture',m_Width=4,m_Height=4,m_MipCount=1,m_ColorSpace=0))
        resolve=lambda owner,p:(file,objects[p['m_PathID']])
        with mock.patch.object(build,'resource_prefab_skins',return_value=([('Jaw_LOD0',renderer)],resolve)), \
                mock.patch.object(build,'export_current_mesh',return_value=dict(vertexCount=3)), \
                mock.patch.object(resources,'skin_bone_paths',return_value=['Rig/Head']), \
                mock.patch.object(resources,'renderer_frame',return_value=np.eye(4)):
            surface=build.resource_skin_surfaces(None,None,None,None,None)[0]
        self.assertEqual(set(surface['textures']),{'_MainTex','_BumpMap'})
        self.assertEqual(surface['defaultTextures'],dict(_ColorMaskMap=dict(name='white',rgba=[1.,1.,1.,1.],scale=[1.,1.],offset=[0.,0.])))
        self.assertEqual(surface['normalScale'],.75)

    def test_metadata_projection_retains_exact_input_identity_and_missing_properties(self):
        with tempfile.TemporaryDirectory()as folder:
            path=Path(folder)/'metadata.json'
            payload=b'\xef\xbb\xbf'+json.dumps(dict(status='complete',details=[dict(value='unchanged')],label='\u539f\u672c'),ensure_ascii=False).encode('utf-8')
            path.write_bytes(payload)
            actual=build.resource_metadata_summary(path,['label','status','optional'])
            self.assertEqual(actual['metadata'],dict(label='\u539f\u672c',status='complete'))
            self.assertEqual(actual['missingFields'],['optional'])
            self.assertEqual(actual['source'],dict(path=str(path),bytes=len(payload),sha256=hashlib.sha256(payload).hexdigest()))
            path.write_text('[]',encoding='utf-8')
            with self.assertRaisesRegex(ValueError,'JSON object'):build.resource_metadata_summary(path,['status'])
            with self.assertRaisesRegex(ValueError,'explicit top-level'):build.resource_metadata_summary(path,[])

    def test_guide_ownership_belongs_to_each_resource_and_its_latest_geometry(self):
        recovered=dict(kind='source-owned-face',facts=[dict(operation='native-neutral-recovery')])
        guided=dict(kind='source-owned-face',facts=[dict(sourceProfileId='DeclaredGuide')])
        self.assertFalse(build.resource_has_face_guide(dict(appearanceFinishing=[recovered])))
        self.assertTrue(build.resource_has_face_guide(dict(appearanceFinishing=[recovered,guided])))
        self.assertFalse(build.resource_has_face_guide(dict(appearanceFinishing=[guided,recovered])))
        self.assertTrue(build.resource_has_face_guide(dict(appearanceFinishing=[guided,dict(kind='source-skin-interface')])))

    def test_rebuilt_collar_serialization_keeps_native_uvs_and_appends_complete_streams(self):
        import character_resources as resources
        count=3;channels=[dict(stream=0,offset=0,format=0,dimension=0)for _ in range(14)]
        for channel,stream,offset,fmt,dimension in ((0,0,0,0,3),(1,0,12,0,3),(2,0,24,0,4),(4,1,0,0,2),(12,2,0,0,4),(13,2,16,10,4)):
            channels[channel]=dict(stream=stream,offset=offset,format=fmt,dimension=dimension)
        def streams(_channels,n):
            uv=(n*40+15)//16*16;skin=(uv+n*8+15)//16*16
            return {0:SimpleNamespace(offset=0,stride=40),1:SimpleNamespace(offset=uv,stride=8),2:SimpleNamespace(offset=skin,stride=32)}
        raw=bytearray(256);uv=np.array([[0.,0.],[1.,0.],[0.,1.]],dtype='<f4');raw[128:152]=uv.tobytes()
        original=dict(m_VertexData=dict(m_VertexCount=count,m_Channels=channels,m_DataSize=bytes(raw)),
            m_BindPose=[{}],m_BoneNameHashes=[],m_BonesAABB=[],m_VariableBoneCountWeights=dict(m_Data=[]),
            m_SubMeshes=[dict()],m_IndexFormat=0,m_Shapes=dict(vertices=[],shapes=[],channels=[],fullWeights=[]))
        packet=dict(vertexCount=4,indexFormat=0,vertices=[0.,0.,0.,1.,0.,0.,0.,1.,0.,1.,1.,0.],normals=[0.,0.,1.]*4,
            tangents=[1.,0.,0.,1.]*4,weights=[1.,0.,0.,0.]*3+[.8,.2,0.,0.],boneIndices=[0]*12+[0,1,0,0],
            bindPoses=np.eye(4).ravel().tolist()*2,bounds=[.5,.5,0.,1.,1.,0.],subMeshes=[[0,1,3,1,2,3]],colors=[],
            uv=[dict(channel=0,dimension=2,values=np.c_[np.r_[uv,[[.4,.6]]],np.zeros((4,2))].ravel().tolist())],
            shapes=[dict(name='Pose',frames=[dict(weight=100.,vertices=[0.]*9+[0.,0.,.2],normals=[0.]*12,tangents=[0.]*12)])])
        obj=SimpleNamespace(read_typetree=lambda:copy.deepcopy(original),read=lambda:None)
        helper=SimpleNamespace(process=lambda:None,src=SimpleNamespace(m_VertexData=SimpleNamespace(m_Channels=channels)),get_streams=streams)
        with mock.patch('UnityPy.helpers.MeshHelper.MeshHandler',return_value=helper):
            result=resources.write_mesh_frame(obj,packet,source_bones=['Head'],target_bones=['Head','Neck'],collar_topology=True)
        payload=result['m_VertexData']['m_DataSize'];actual_uv=np.ndarray((4,2),'<f4',buffer=payload,offset=160,strides=(8,4))
        np.testing.assert_array_equal(actual_uv[:3],uv);np.testing.assert_allclose(actual_uv[3],[.4,.6],rtol=0,atol=3e-8)
        weights=np.ndarray((4,4),'<f4',buffer=payload,offset=192,strides=(32,4))
        np.testing.assert_allclose(weights[-1],[.8,.2,0.,0.],rtol=0,atol=2e-8)
        self.assertEqual(result['m_VertexData']['m_VertexCount'],4)
        np.testing.assert_array_equal(np.frombuffer(result['m_IndexBuffer'],'<u2'),[0,1,3,1,2,3])
        self.assertEqual(result['m_Shapes']['vertices'][0]['index'],3)
        self.assertAlmostEqual(result['m_Shapes']['vertices'][0]['vertex']['z'],.2)

    def test_dxt5_skin_fields_retain_alpha_carriers_and_unowned_blocks(self):
        import etcpak
        from PIL import Image
        _,_=build.source_owner();import material_fields as fields
        source=np.full((8,8,4),128,np.uint8);source[:,:,0]=255
        source[:,:,1]=np.arange(8,dtype=np.uint8)[None,:]*3+116
        payload=etcpak.compress_bc3(source.tobytes(),8,8)
        original=np.asarray(Image.frombytes('RGBA',(8,8),payload,'bcn',3))
        owned=np.zeros((8,8),bool);owned[:4,:4]=True
        normal=original.copy();normal[:4,:4,1]+=8;normal[:4,:4,3]+=4
        result,proof=fields.fit_bc3_skin_field(payload,8,8,normal,owned,'normal')
        actual=np.asarray(Image.frombytes('RGBA',(8,8),result,'bcn',3))
        np.testing.assert_array_equal(actual[:,:,[0,2]],original[:,:,[0,2]])
        np.testing.assert_array_equal(actual[~owned],original[~owned])
        self.assertNotEqual(payload,result);self.assertGreater(proof['acceptedBlocks'],0)
        color=original.copy();color[:4,:4,:3]=np.clip(color[:4,:4,:3].astype(int)-8,0,255)
        result,_=fields.fit_bc3_skin_field(payload,8,8,color,owned,'color')
        actual=np.asarray(Image.frombytes('RGBA',(8,8),result,'bcn',3))
        np.testing.assert_array_equal(actual[:,:,3],original[:,:,3]);np.testing.assert_array_equal(actual[~owned],original[~owned])
        self.assertNotEqual(payload,result)
        owned[0,0]=False
        result,_=fields.fit_bc3_skin_field(payload,8,8,color,owned,'color')
        self.assertEqual(result,payload)

    def test_private_texture_reuse_requires_all_consumers_and_the_same_source_object(self):
        import character_resources as resources
        pointer=lambda identifier:dict(m_FileID=0,m_PathID=identifier)
        def material():return SimpleNamespace(type=SimpleNamespace(name='Material'),read_typetree=lambda:dict(m_SavedProperties=dict(m_TexEnvs=[('_MainTex',dict(m_Texture=pointer(1)))])))
        texture=SimpleNamespace(type=SimpleNamespace(name='Texture2D'))
        file=SimpleNamespace(objects={1:texture,2:material(),3:material()})
        updates=[dict(pathId=i,property='_MainTex',texture=dict(sha256='a'*64,name='Normal',object=11))for i in (2,3)]
        self.assertEqual(resources.private_material_texture_targets(file,updates[:1]),set())
        self.assertEqual(resources.private_material_texture_targets(file,updates),{1})
        updates[1]['texture']['object']=12
        self.assertEqual(resources.private_material_texture_targets(file,updates),set())
        updates[1]['texture']['object']=11
        updates[0]['texture']['mips']=dict(sha256='b'*64,format=4,bytes=64)
        self.assertEqual(resources.private_material_texture_targets(file,updates),set())
        updates[1]['texture']['mips']=dict(updates[0]['texture']['mips'])
        self.assertEqual(resources.private_material_texture_targets(file,updates),{1})
        updates[1]['texture']['mips']['sha256']='c'*64
        self.assertEqual(resources.private_material_texture_targets(file,updates),set())
        updates[1]['texture']['mips']=dict(updates[0]['texture']['mips'])
        file.objects[4]=SimpleNamespace(type=SimpleNamespace(name='MonoBehaviour'),read_typetree=lambda:dict(input=pointer(1)))
        self.assertEqual(resources.private_material_texture_targets(file,updates),set())
        del file.objects[4]
        file.objects[4]=SimpleNamespace(type=SimpleNamespace(name='AssetBundle'),read_typetree=lambda:dict(m_Container=[('field',dict(asset=pointer(1)))]))
        self.assertEqual(resources.private_material_texture_targets(file,updates),set())

    def test_pixel_workers_execute_the_retained_producer_epoch(self):
        with tempfile.TemporaryDirectory()as folder:
            root=Path(folder);producer=root/'build_models.py.source'
            producer.write_text('def write_source_skin_bundle(job): return {"owner":"saved-skin"}\ndef write_skin_interface_bundle(job): return {"owner":"saved-interface"}\n',encoding='utf-8')
            paths=[producer]
            for name in ('production.py','generate.py','material_fields.py','character_resources.py'):
                path=root/(name+'.source');path.write_text('# immutable producer\n',encoding='utf-8');paths.append(path)
            job=dict(receipt=str(root/'result.json'),inputs=[dict(path=str(path),sha256=build.sha(path))for path in paths])
            with mock.patch.object(build,'write_source_skin_bundle',side_effect=AssertionError('Live skin producer executed')),\
                    mock.patch.object(build,'write_skin_interface_bundle',side_effect=AssertionError('Live interface producer executed')):
                self.assertEqual(build.finish_source_skin_bundle(job),dict(owner='saved-skin'))
                self.assertEqual(build.finish_skin_interface_bundle(job),dict(owner='saved-interface'))

    def test_skin_replay_binds_retained_transitive_helpers_before_builder_import_and_restores_them(self):
        names=('collect_sources','asset_names','mesh_recipe_codec','material_fields','character_resources')
        previous={name:sys.modules.get(name)for name in names};search_paths=sys.path[:]
        with tempfile.TemporaryDirectory()as folder:
            root=Path(folder);files={
                'collect_sources.py':'def fingerprint(value): return "saved-fingerprint"\n',
                'asset_names.py':'owner="saved-names"\n',
                'mesh_recipe_codec.py':'def four_slot_skin(value): return "saved-skin"\n',
                'material_fields.py':'import sys\nsys.path.insert(0,"saved-search-path")\nfrom mesh_recipe_codec import four_slot_skin\n',
                'character_resources.py':'from mesh_recipe_codec import four_slot_skin\n',
                'production.py':'# retained geometry\n','generate.py':'# retained art\n',
                'build_models.py':(
                    'from collect_sources import fingerprint\nimport asset_names\nimport mesh_recipe_codec\nimport character_resources\n'
                    'def source_owner(**paths): return fingerprint,asset_names.owner,mesh_recipe_codec.four_slot_skin,paths\n'
                    'def write_source_skin_bundle(job): return fingerprint(job),asset_names.owner,character_resources.four_slot_skin(job)\n')}
            for name,code in files.items():(root/(name+'.source')).write_text(code,encoding='utf-8')
            pins=[dict(path=str(root/(name+'.source')),sha256=build.sha(root/(name+'.source')))for name in files]
            with build.retained_skin_producer(pins)as owner:
                self.assertIs(owner.fingerprint,sys.modules['collect_sources'].fingerprint)
                self.assertIs(owner.character_resources.four_slot_skin,sys.modules['mesh_recipe_codec'].four_slot_skin)
                self.assertIs(sys.modules['material_fields'].four_slot_skin,owner.character_resources.four_slot_skin)
                self.assertEqual(owner.write_source_skin_bundle({}),('saved-fingerprint','saved-names','saved-skin'))
                fingerprint,naming,skin,paths=owner.source_owner()
                self.assertIs(fingerprint,owner.fingerprint);self.assertIs(skin,owner.character_resources.four_slot_skin)
                self.assertEqual(naming,'saved-names')
                self.assertEqual(paths,dict(production_path=str(root/'production.py.source'),art_path=str(root/'generate.py.source')))
            self.assertEqual(sys.path,search_paths)
            for name,value in previous.items():self.assertIs(sys.modules.get(name),value)
            broken=root/'character_resources.py.source';broken.write_text('raise RuntimeError("saved helper import failed")\n',encoding='utf-8')
            with self.assertRaisesRegex(ValueError,'retained skin producer changed'):
                with build.retained_skin_producer(pins):self.fail('A changed retained helper was accepted.')
            for pin in pins:pin['sha256']=build.sha(pin['path'])
            with self.assertRaisesRegex(RuntimeError,'saved helper import failed'):
                with build.retained_skin_producer(pins):self.fail('An incomplete helper import was accepted.')
            self.assertEqual(sys.path,search_paths)
            for name,value in previous.items():self.assertIs(sys.modules.get(name),value)

    def test_collar_fade_crosses_uv_aliases_before_transporting_added_curve_vertices(self):
        production,_=build.source_owner()
        points=np.array([[-1.,0.,0.],[0.,0.,0.],[1.,0.,0.],[2.,0.,0.],[1.,0.,0.],[2.,1.,0.]])*.01
        count=len(points);identity=np.eye(4).ravel().tolist()
        native=dict(name='Face',nativeReadable=True,vertexCount=count,indexFormat=0,vertices=points.ravel().tolist(),
            normals=[0.,0.,1.]*count,tangents=[1.,0.,0.,1.]*count,weights=[1.,0.,0.,0.]*count,
            boneIndices=[0]*count*4,bindPoses=identity,subMeshes=[[0,1,5,1,2,5,4,3,5]],uv=[],colors=[],shapes=[])
        native['sourceFingerprint']=build.fingerprint(native)[0]
        authored=copy.deepcopy(native);extra=np.array([[.015,.005,0.],[0.,.005,0.]])
        authored.update(vertexCount=8,vertices=np.r_[points,extra].ravel().tolist(),subMeshes=[[0,1,7,1,2,7,4,3,6,3,5,6]])
        for field,width in (('normals',3),('tangents',4),('weights',4),('boneIndices',4)):
            authored[field]+=authored[field][:width]*2
        authored['sourceFingerprint']=build.fingerprint(authored)[0]
        target=copy.deepcopy(native);target['vertices']=(points+[0.,0.,.001]).ravel().tolist();target['sourceFingerprint']=build.fingerprint(target)[0]
        revised,_=production.refine_native_face_contours(native,authored,dict(rendererToPrefab=identity),
            collar_vertices=[0],refined=target,surface_weight=np.ones(count))
        actual=np.asarray(revised['vertices']).reshape(-1,3)
        np.testing.assert_array_equal(actual[2],actual[4]);np.testing.assert_array_equal(actual[0],points.astype(np.float32)[0])
        self.assertGreater(actual[6,2],0.)

    def test_prepared_curve_vertices_follow_native_controls_and_keep_the_curve_offset(self):
        production,_=build.source_owner()
        points=np.array([[-1.,-1.,0.],[1.,-1.,0.],[1.,1.,0.],[-1.,1.,0.],[0.,0.,.1]])
        faces=np.array([[0,1,4],[1,2,4],[2,3,4],[3,0,4]])
        target=points.copy();target[2:4,2]+=.002
        result,proof,graph=production.transport_prepared_surface(points,target,4,faces,np.array([],int))
        np.testing.assert_array_equal(result[:4],target[:4])
        self.assertAlmostEqual(result[4,2],.101,places=11)
        self.assertEqual(proof['transportedAddedVertices'],1)
        translated=points.copy();translated[:4]+=[.002,.003,-.001]
        moved,_,_=production.transport_prepared_surface(points,translated,4,faces,np.array([],int),graph)
        np.testing.assert_allclose(moved-points,np.tile([.002,.003,-.001],(5,1)),rtol=0,atol=1e-11)
        locked,_,_=production.transport_prepared_surface(points,target,4,faces,np.array([4]))
        np.testing.assert_array_equal(locked[4],points[4])

    def test_source_contours_share_the_cohort_contract_without_definition_branches(self):
        plans=dict(columns=['jawIn','chinForward'],cohorts={'Human/Female/Origin_Stone':{'A':[1.,.5]}},
            surfaces={'Player':dict(contourRenderer='Face_LOD0')},contours={'Source':dict(renderer='Face_LOD0',values=[.8,.4])})
        fixture=dict(name='Source',race='Elf',sex='Male',origin='Origin_Forest',faceDefinition='FaceShape_NPC_Example')
        self.assertEqual(build.source_face_contour(plans,fixture),('Face_LOD0',dict(jawIn=.8,chinForward=.4),('source','Source')))
        fixture.update(name='Player',race='Human',sex='Female',origin='Origin_Stone',faceDefinition='FaceShape_A')
        self.assertEqual(build.source_face_contour(plans,fixture)[1],dict(jawIn=1.,chinForward=.5))
        fixture['name']='Unowned';fixture['faceDefinition']='FaceShape_Z'
        self.assertIsNone(build.source_face_contour(plans,fixture))

    def test_skin_detail_revisions_recover_the_original_input_through_private_bindings(self):
        with tempfile.TemporaryDirectory()as directory:
            root=Path(directory);original=root/'original.bundle';original.write_bytes(b'original detail')
            before=root/'filtered.bundle';before.write_bytes(b'filtered detail')
            pin=lambda p:dict(path=str(p),sha256=build.sha(p))
            plan=root/'Plan.json';field=root/'Field.json';binding=root/'Binding.json'
            profile=dict(normalRetain=.4,normalRadiusMetres=.002)
            build.write(plan,dict(source=str(original),sha256=build.sha(original),textures=[dict(object=7,channel='normal',profile=profile)]))
            build.write(field,dict(target=pin(before),sha256=build.sha(before),inputPlan=pin(plan)))
            build.write(binding,dict(textures=[dict(object=21,source=dict(**pin(before),name='Normal'),sourceObject=7)]))
            proof=dict(appearanceFinishing=[dict(kind='source-skin-material-bindings',proof=pin(binding),fields=[pin(field)])])
            result=build.source_skin_detail_authority(proof,21,'normal')
            self.assertEqual(result,dict(source=dict(**pin(original),object=7),profile=profile))
            self.assertIsNone(build.source_skin_detail_authority(proof,22,'normal'))
            # A later private field still points to the same absolute source,
            # rather than applying another blur to the already-filtered pixels.
            next_plan=build.read(plan);next_plan.update(source=str(before),sha256=build.sha(before))
            next_plan['textures'][0]['detailSource']=result['source'];build.write(plan,next_plan)
            field_value=build.read(field);field_value['inputPlan']=pin(plan);build.write(field,field_value)
            proof['appearanceFinishing'][0]['fields']=[pin(field)]
            self.assertEqual(build.source_skin_detail_authority(proof,21,'normal')['source'],result['source'])
            plan.write_text('{}')
            with self.assertRaisesRegex(ValueError,'authority changed'):build.source_skin_detail_authority(proof,21,'normal')

    def test_partial_contour_revisions_retain_each_resources_actual_guide(self):
        with tempfile.TemporaryDirectory()as directory:
            root=Path(directory);older=root/'Older.json';newer=root/'Newer.json';native=root/'Index.json'
            build.write(native,dict(bundles=[]))
            for path,target in ((older,1),(newer,2)):
                build.write(path,dict(schemaVersion=1,profiles=[dict(id='HeadGuide',parameters=dict(target=target))]))
            pin=lambda p:dict(path=str(p),sha256=build.sha(p))
            def receipt(inputs):return dict(kind='source-owned-face',facts=[dict(sourceProfileId='HeadGuide')],authority=dict(inputs=inputs))
            untouched=dict(appearanceFinishing=[receipt([pin(native),pin(older)])])
            revised=dict(appearanceFinishing=[receipt([pin(older)]),receipt([pin(native),pin(newer),pin(older)])])
            self.assertEqual(build.face_resource_profile_authority(untouched),pin(older))
            self.assertEqual(build.face_resource_profile_authority(revised),pin(newer))
            explicit=dict(appearanceFinishing=[dict(kind='source-owned-face',authority=dict(sourceProfiles=pin(newer)))])
            self.assertEqual(build.face_resource_profile_authority(explicit),pin(newer))
            newer.write_text('{}')
            with self.assertRaisesRegex(ValueError,'guide changed'):build.face_resource_profile_authority(explicit)

    def test_shared_skin_frame_serialization_transports_palette_and_frames_without_uv_changes(self):
        import character_resources as resources
        count=3
        channels=[dict(stream=0,offset=0,format=0,dimension=0)for _ in range(14)]
        for channel,stream,offset,fmt,dimension in ((0,0,0,0,3),(1,0,12,0,3),(2,0,24,0,4),(4,1,0,0,2),(12,2,0,0,4),(13,2,16,10,4)):
            channels[channel]=dict(stream=stream,offset=offset,format=fmt,dimension=dimension)
        streams={0:SimpleNamespace(offset=0,stride=40),1:SimpleNamespace(offset=120,stride=8),2:SimpleNamespace(offset=144,stride=32)}
        raw=bytearray(240);raw[120:144]=bytes(range(24))
        pose=np.eye(4,dtype=np.float32).T.ravel().tolist()
        original=dict(m_VertexData=dict(m_VertexCount=count,m_Channels=channels,m_DataSize=bytes(raw)),
            m_BindPose=[{}],m_BoneNameHashes=[],m_BonesAABB=[],m_VariableBoneCountWeights=dict(m_Data=[]),
            m_Shapes=dict(vertices=[],shapes=[],channels=[],fullWeights=[]))
        packet=dict(vertexCount=count,vertices=[0.,0.,.1,1.,0.,0.,0.,1.,0.],normals=[0.,0.,1.]*count,
            tangents=[1.,0.,0.,1.]*count,weights=[.8,.2,0.,0.,1.,0.,0.,0.,1.,0.,0.,0.],
            boneIndices=[0,1,0,0]+[0]*8,bindPoses=pose*2,bounds=[.5,.5,.05,1.,1.,.1],
            shapes=[dict(name='Muscular',frames=[dict(weight=100.,vertices=[0.,0.,.2]+[0.]*6,normals=[0.]*9,tangents=[0.]*9)])])
        obj=SimpleNamespace(read_typetree=lambda:copy.deepcopy(original),read=lambda:None)
        helper=SimpleNamespace(process=lambda:None,src=SimpleNamespace(m_VertexData=SimpleNamespace(m_Channels=channels)),get_streams=lambda *_:streams)
        with mock.patch('UnityPy.helpers.MeshHelper.MeshHandler',return_value=helper):
            result=resources.write_mesh_frame(obj,packet,source_bones=['Neck'],target_bones=['Neck','Chest'])
            with self.assertRaisesRegex(ValueError,'bone prefix'):
                resources.write_mesh_frame(obj,packet,source_bones=['Neck'],target_bones=['Chest','Neck'])
        self.assertEqual(result['m_VertexData']['m_DataSize'][120:144],bytes(range(24)))
        self.assertEqual(len(result['m_BindPose']),2);self.assertEqual(len(result['m_BonesAABB']),2)
        weights=np.ndarray((count,4),'<f4',buffer=result['m_VertexData']['m_DataSize'],offset=144,strides=(32,4))
        indices=np.ndarray((count,4),'<u4',buffer=result['m_VertexData']['m_DataSize'],offset=160,strides=(32,4))
        np.testing.assert_allclose(weights[0],[.8,.2,0,0]);np.testing.assert_array_equal(indices[0],[0,1,0,0])
        self.assertEqual(result['m_Shapes']['channels'][0]['name'],'Muscular')
        self.assertEqual(result['m_Shapes']['vertices'][0]['vertex']['z'],.2)
        self.assertEqual(original['m_Shapes']['channels'],[])

    def test_publication_validation_checks_both_catalogs_without_replacing_payloads(self):
        with tempfile.TemporaryDirectory()as folder:
            root=Path(folder);original=root/'original';candidate=root/'candidate';current=root/'public'
            for path in(original,candidate,current):path.mkdir()
            for path in(original,current):(path/build.RESOURCE_FILES['manifest']).write_bytes(b'original manifest')
            for path in(original,current):(path/'actor.bundle').write_bytes(b'original resource')
            (candidate/'actor.bundle').write_bytes(b'revised resource');receipt=dict(changed=['actor.bundle'])
            payloads={'actor.bundle':build.sha(candidate/'actor.bundle')};rollback={'actor.bundle':build.sha(original/'actor.bundle')}
            with mock.patch.object(build,'HERE',root/'Validation'),\
                    mock.patch.object(build,'checked_catalog_path',side_effect=lambda path,name=None:Path(path)/name if name else Path(path)),\
                    mock.patch.object(build.os,'link')as link:
                build.atomic_publish_catalog(candidate,current,original,b'revised manifest',payloads,{'actor.bundle':'actor.bundle'},rollback,receipt,validate_only=True)
                self.assertEqual(receipt['status'],'PASS_PUBLICATION_VALIDATION');link.assert_not_called()
                self.assertEqual((current/build.RESOURCE_FILES['manifest']).read_bytes(),b'original manifest')
                self.assertEqual((current/'actor.bundle').read_bytes(),b'original resource')
                (candidate/'actor.bundle').write_bytes(b'changed')
                with self.assertRaisesRegex(ValueError,'candidate payload'):
                    build.atomic_publish_catalog(candidate,current,original,b'revised manifest',payloads,{'actor.bundle':'actor.bundle'},rollback,receipt,validate_only=True)

    def test_first_publication_link_failure_preserves_the_original_error_and_catalog(self):
        with tempfile.TemporaryDirectory()as folder:
            root=Path(folder);original=root/'original';candidate=root/'candidate';current=root/'public'
            for path in(original,candidate,current):path.mkdir()
            for path in(original,current):(path/build.RESOURCE_FILES['manifest']).write_bytes(b'original manifest')
            (original/'actor.bundle').write_bytes(b'original resource')
            (current/'actor.bundle').write_bytes(b'original resource')
            (candidate/'actor.bundle').write_bytes(b'revised resource')
            receipt=dict(changed=['actor.bundle'])
            denied=PermissionError('publication link denied')
            with mock.patch.object(build,'HERE',root/'Validation'),\
                    mock.patch.object(build,'checked_catalog_path',side_effect=lambda path,name=None:Path(path)/name if name else Path(path)),\
                    mock.patch.object(build.os,'link',side_effect=denied)as link:
                with self.assertRaises(PermissionError)as failure:
                    build.atomic_publish_catalog(candidate,current,original,b'revised manifest',
                        {'actor.bundle':build.sha(candidate/'actor.bundle')},{'actor.bundle':'actor.bundle'},
                        {'actor.bundle':build.sha(original/'actor.bundle')},receipt)
            self.assertIs(failure.exception,denied)
            self.assertEqual(link.call_count,1)
            self.assertEqual(receipt['status'],'FAIL_NO_PUBLICATION')
            self.assertEqual((current/build.RESOURCE_FILES['manifest']).read_bytes(),b'original manifest')
            self.assertEqual((current/'actor.bundle').read_bytes(),b'original resource')
            self.assertFalse(list(current.glob('*.new-*')))

    def test_source_skin_coarse_validation_retains_native_mip_order_and_stops_before_full_resolution(self):
        production,_=build.source_owner()
        import character_resources as resources
        import material_fields as fields
        with tempfile.TemporaryDirectory()as folder:
            source=Path(folder)/'skin.bundle';source.write_bytes(b'source')
            images=[np.full((size,size,4),level+1,dtype=np.uint8)for level,size in enumerate((16,8,4,2,1))]
            native=[pixels.tobytes()for pixels in images]
            data=SimpleNamespace(m_Name='Skin',m_ColorSpace=1,m_Width=16,m_Height=16,m_MipCount=len(images),
                m_TextureSettings=SimpleNamespace(m_WrapU=0,m_WrapV=0))
            obj=SimpleNamespace(path_id=7,read=lambda:data)
            serialized=SimpleNamespace(objects={7:obj},name='skin')
            texture=dict(object=7,channel='color',surfaces=[dict(rendererToPrefab=np.eye(4).ravel().tolist())],
                domain={},normalEncoding=None,profile=dict(coherenceSigmaMetres=.001,featherMetres=.003))
            job=dict(source=str(source),sha256=build.sha(source),textures=[texture])
            visited=[];evaluators=[];samplers=[];protected=False;whole_blocks=fields.whole_block_pixels
            def metrics(packet,skin,slot,shape,*args,**kwargs):
                evaluators.append(kwargs);samplers.append(args[1]);ownership=np.ones(shape)
                if protected:ownership[0,0]=0
                return ownership,np.full(shape,.001),dict(shape=list(shape)),np.ones(shape,dtype=bool)
            def refine(pixels,*args,**kwargs):
                visited.append(pixels.shape[:2])
                return pixels.copy(),dict(shape=list(pixels.shape[:2]))
            with mock.patch.object(build,'source_owner',return_value=(production,None)),\
                    mock.patch.object(resources,'load_serialized_bundle',return_value=(None,serialized)),\
                    mock.patch.object(resources,'texture_mip_images',return_value=images),\
                    mock.patch.object(resources,'texture_mip_payloads',return_value=(4,native)),\
                    mock.patch.object(build,'saved_skin_surface',return_value=(dict(uv=[]),[])),\
                    mock.patch.object(production,'source_material_ownership',return_value=np.ones(1))as vertex_owner,\
                    mock.patch.object(production,'indexed_surface_uv_metrics',side_effect=metrics),\
                    mock.patch.object(production,'refine_source_skin_material',side_effect=refine)as bake,\
                    mock.patch.object(build,'skin_filter_neighbourhoods',return_value=([],{})),\
                    mock.patch.object(fields,'whole_block_pixels',side_effect=lambda mask:(None,mask))as blocks,\
                    contextlib.redirect_stdout(io.StringIO()):
                result=build.source_skin_payloads(job)[0]
                self.assertEqual(visited,[(4,4),(8,8),(16,16)])
                self.assertEqual(result['payload'],b''.join(native))
                self.assertEqual([mip['level']for mip in result['mips']],[0,1,2])
                self.assertTrue(all('vertex_field'in value for value in evaluators))
                # Chart ownership belongs to the indexed triangle, including
                # charts touching at one vertex. Do not interpolate it away.
                texture['domain']={'protectSourceCharts':{'a'*64:[dict(type='sourceTriangle',subMesh=0,triangleOrdinal=0,barycentric=[1/3]*3)]}}
                vertex_owner.reset_mock();evaluators.clear();visited.clear()
                result=build.source_skin_payloads(job)[0]
                self.assertEqual(result['payload'],b''.join(native))
                self.assertTrue(all('bone_domain'in value and 'vertex_field'not in value for value in evaluators))
                vertex_owner.assert_not_called()
                # Use the saved sampler for both ownership and region caching.
                # A codec block that also serves protected source tissue keeps
                # its current bytes, including when the desired image differs.
                data.m_TextureSettings.m_FilterMode=1;data.m_TextureSettings.m_Aniso=1
                protected=True;samplers.clear();blocks.side_effect=whole_blocks
                def changed(pixels,*args,**kwargs):
                    result=pixels.copy();result[:,:,:3]+=10;return result,{}
                bake.side_effect=changed
                result=build.source_skin_payloads(job)[0]
                self.assertEqual([value['mipLevel']for value in samplers],[2,1,0])
                self.assertTrue(all({key:entry for key,entry in value.items()if key!='mipLevel'}==
                    dict(wrapU=0,wrapV=0,filterMode=1,anisoLevel=1)for value in samplers))
                offset=0
                for level,pixels in enumerate(images):
                    size=pixels.shape[0];length=pixels.nbytes
                    actual=np.frombuffer(result['payload'][offset:offset+length],np.uint8).reshape(pixels.shape);offset+=length
                    if size<4:np.testing.assert_array_equal(actual,pixels);continue
                    np.testing.assert_array_equal(actual[-4:,:4],pixels[-4:,:4])
                    np.testing.assert_array_equal(actual[:,:,3],pixels[:,:,3])
                    if size>4:np.testing.assert_array_equal(actual[0,4,:3],pixels[0,4,:3]+10)
                bake.reset_mock();visited.clear()
                bake.side_effect=ValueError('unsupported coarse reference')
                with self.assertRaisesRegex(ValueError,'unsupported coarse reference'):
                    build.source_skin_payloads(job)
                self.assertEqual(bake.call_count,1)
                self.assertEqual(bake.call_args.args[0].shape,(4,4,4))

    def test_skin_replay_cache_checks_source_and_output_bytes_and_restores_module_owners(self):
        import character_resources as resources
        with tempfile.TemporaryDirectory()as folder:
            root=Path(folder);counter=root/'count';raw=root/'field.mips';raw.write_bytes(b'owned field')
            producer=root/'build_models.py';producer.write_text(
                'from pathlib import Path\n'
                'def source_skin_payloads(job):\n'
                ' p=Path(job["counter"]);p.write_text(p.read_text()+"x" if p.exists()else "x")\n'
                ' return [dict(object=1,payload=b"owned field")]\n',encoding='utf-8')
            files=[producer]
            for name in('production.py','generate.py','material_fields.py','character_resources.py'):
                path=root/name;path.write_text('',encoding='utf-8');files.append(path)
            inputs=[dict(path=str(p),sha256=build.sha(p))for p in files]
            job=root/'Job.json';build.write(job,dict(sha256='a'*64,inputs=inputs,counter=str(counter)))
            operation=dict(beforeSha256='a'*64,inputPlan=dict(path=str(job),sha256=build.sha(job)),
                textures=[dict(object=1,raw=dict(path=str(raw),sha256=build.sha(raw)),payloadSha256=build.sha(raw))])
            def check_saved_payload(value):
                for row in value['textures']:
                    if build.sha(row['raw']['path'])!=row['raw']['sha256']:raise ValueError('Saved raw bytes changed.')
            previous={name:sys.modules.get(name)for name in('material_fields','character_resources')}
            with mock.patch.object(build,'HERE',root/'Validation'),\
                    mock.patch.object(build,'checked_catalog_path',side_effect=lambda path,name=None:Path(path)/name if name else Path(path)),\
                    mock.patch.object(resources,'validate_owned_texture_mips',side_effect=check_saved_payload)as check:
                build.validate_source_skin_finish(operation);build.validate_source_skin_finish(operation)
                build.validate_source_skin_finish(dict(kind='source-skin-detail',**operation))
                self.assertEqual(counter.read_text(),'x')
                self.assertEqual(check.call_count,3)
                for name,module in previous.items():self.assertIs(sys.modules.get(name),module)
                raw.write_bytes(b'changed field')
                with self.assertRaisesRegex(ValueError,'Saved raw bytes changed'):build.validate_source_skin_finish(operation)
                raw.write_bytes(b'owned field');files[1].write_text('# changed source',encoding='utf-8')
                with self.assertRaisesRegex(ValueError,'retained skin computation input changed'):build.validate_source_skin_finish(operation)
                self.assertEqual(counter.read_text(),'x')

    def test_approved_resource_reuse_still_requires_exact_source_payloads(self):
        with tempfile.TemporaryDirectory()as folder:
            root=Path(folder);original=root/'approved/Catalog';candidate=root/'candidate';current=root/'public'
            for path in(original,candidate,current):path.mkdir(parents=True)
            for path in(original,candidate,current):(path/'actor.bundle').write_bytes(b'accepted source')
            digest=build.sha(original/'actor.bundle')
            manifest=dict(schemaVersion=3,meshes=[],materials=[],textureRemaps=[],bundles=[dict(file='actor.bundle',sha256=digest)],
                resources=[dict(source='Actor.prefab',type='GameObject',bundles=[dict(file='actor.bundle')])])
            for path in(original,candidate):build.write(path/build.RESOURCE_FILES['manifest'],manifest)
            row=dict(file='actor.bundle',bytes=len(b'accepted source'),sha256=digest,source=str(original/'actor.bundle'),sourceSha256=digest,
                allRetainedObjectFieldsExactAfterReferenceRebasing=False)
            row['source']=str(root/'retired/actor.bundle')
            approved=original.parent/'ResourceAssembly.json'
            review=original.parent/'ArtAcceptance.json'
            build.write(review,dict(status='PASS_NATIVE_APPEARANCE_REVIEW',candidate=dict(sha256=build.sha(original/build.RESOURCE_FILES['manifest']))))
            build.write(approved,dict(status='PASS_STANDALONE_CHARACTER_RESOURCES',originalModelDependenciesInherited=False,artAccepted=True,
                artAcceptance=dict(path=str(review),sha256=build.sha(review)),catalogSha256=build.sha(original/build.RESOURCE_FILES['manifest']),proofs=[row]))
            proof=dict(status='PASS_STANDALONE_CHARACTER_RESOURCES',originalModelDependenciesInherited=False,resources=1,bundles=1,
                bytes=row['bytes'],inputs=[],proofs=[row],preparedSource=dict(assembly=dict(path=str(approved),sha256=build.sha(approved)),
                    catalog=dict(path=str(original/build.RESOURCE_FILES['manifest']),sha256=build.sha(original/build.RESOURCE_FILES['manifest']))))
            build.write(candidate/'ResourceAssembly.json',proof)
            quality=dict(resourceAssembly=dict(path=str(candidate/'ResourceAssembly.json'),sha256=build.sha(candidate/'ResourceAssembly.json')),
                sourceManifestSha256=build.sha(original/build.RESOURCE_FILES['manifest']),candidateManifestSha256=build.sha(candidate/build.RESOURCE_FILES['manifest']))
            with mock.patch.object(build,'checked_catalog_path',side_effect=lambda path,name=None:Path(path)/name if name else Path(path)),\
                    mock.patch.object(build,'checked_source_path',side_effect=Path),mock.patch.object(build,'atomic_publish_catalog'),contextlib.redirect_stdout(io.StringIO()):
                build.publish_resource_catalog(candidate,current,original,manifest,quality)
                (candidate/'actor.bundle').write_bytes(b'unverified different bytes')
                row.update(sha256=build.sha(candidate/'actor.bundle'),bytes=(candidate/'actor.bundle').stat().st_size,
                    allRetainedObjectFieldsExactAfterReferenceRebasing=True)
                proof['bytes']=row['bytes'];manifest['bundles'][0]['sha256']=row['sha256']
                build.write(candidate/build.RESOURCE_FILES['manifest'],manifest);build.write(candidate/'ResourceAssembly.json',proof)
                quality['candidateManifestSha256']=build.sha(candidate/build.RESOURCE_FILES['manifest'])
                quality['resourceAssembly']['sha256']=build.sha(candidate/'ResourceAssembly.json')
                with self.assertRaisesRegex(ValueError,'unverified final bytes'):
                    build.publish_resource_catalog(candidate,current,original,manifest,quality)
                (original/'actor.bundle').write_bytes(b'changed source')
                with self.assertRaisesRegex(ValueError,'accepted source payload changed'):
                    build.publish_resource_catalog(candidate,current,original,manifest,quality)

    def test_source_fibre_intermediates_cannot_be_packaged_as_complete_grooms(self):
        with tempfile.TemporaryDirectory()as folder:
            root=Path(folder);catalog=root/'Catalog';catalog.mkdir();plan=root/'Plan.json'
            build.write(plan,dict(mode='source-fibers-only'))
            build.write(root/'ResourceAssembly.json',dict(groomSourceDesigns=dict(plan=dict(path=str(plan),sha256=build.sha(plan)))))
            build.write(catalog/build.RESOURCE_FILES['manifest'],dict(schemaVersion=3,meshes=[],materials=[],textureRemaps=[],bundles=[]))
            output=root/'Packs'
            with mock.patch.object(build,'checked_catalog_path',side_effect=lambda path,name=None:Path(path)/name if name else Path(path)):
                with self.assertRaisesRegex(ValueError,'retained source support'):
                    build.create_resource_packs(catalog/build.RESOURCE_FILES['manifest'],output,PACKAGE_TEST_VERSION)
            self.assertFalse(output.exists())

    def test_resource_releases_enforce_the_declared_metadata_independently_of_version(self):
        configurations=[build.RESOURCE_PACKAGE,dict(build.RESOURCE_PACKAGE,versionPattern=r'revision-\d+',
            tagName='resources-{version}',releaseMetadata=dict(name='共有資源',body='選択した資源を自動で取得します。',generate_release_notes=False))]
        for configuration,versions in zip(configurations,[(PACKAGE_TEST_VERSION,PACKAGE_TEST_VERSION+'0'),('revision-1','revision-23')]):
            for version in versions:
                with self.subTest(version=version):
                    metadata=build.resource_release_metadata(version,configuration)
                    self.assertEqual({key:metadata[key]for key in configuration['releaseMetadata']},configuration['releaseMetadata'])
                    self.assertEqual(metadata['tag_name'],configuration['tagName'].format(version=version))
                    build.validate_resource_release(metadata,version,configuration)
                    for key in (*configuration['releaseMetadata'],'tag_name'):
                        changed=dict(metadata);changed[key]=not metadata[key]if type(metadata[key])is bool else metadata[key]+version
                        with self.assertRaisesRegex(ValueError,'package configuration'):
                            build.validate_resource_release(changed,version,configuration)
                    for body in ('','\u200b','<!-- -->'):
                        with self.assertRaisesRegex(ValueError,'package configuration'):
                            build.validate_resource_release(dict(metadata,body=body),version,configuration)
                    with self.assertRaisesRegex(ValueError,'package configuration'):
                        build.validate_resource_release(dict(metadata,generate_release_notes=True),version,configuration)

    def test_resource_package_names_distribution_and_resume_follow_the_same_configuration(self):
        with tempfile.TemporaryDirectory()as folder:
            root=Path(folder);source=root/'source';source.mkdir();output=root/'output'
            configuration=copy.deepcopy(build.RESOURCE_PACKAGE)
            configuration.update(files={role:role+'.fixture.json'for role in configuration['files']},
                versionPattern=r'\d+',tagName='fixture-{version}',archiveName='part-{ordinal:03}.zip',
                downloadBaseUrl='https://resources.example.test/{tag}/')
            config_path=root/'configuration.json';build.write(config_path,configuration)
            configuration=build.resource_package_configuration(config_path)
            payload=source/'actor.bundle';payload.write_bytes(b'owned-resource')
            manifest=source/configuration['files']['manifest']
            build.write(manifest,dict(schemaVersion=3,meshes=[],materials=[],textureRemaps=[],bundles=[dict(file=payload.name,sha256=build.sha(payload))]))
            with mock.patch.object(build,'checked_catalog_path',side_effect=lambda path,name=None:Path(path)/name if name else Path(path)):
                result=build.create_resource_packs(manifest,output,'1',configuration=configuration)
                release_path=output/configuration['files']['release']
                self.assertEqual(result['packs'][0]['file'],configuration['archiveName'].format(ordinal=1))
                self.assertEqual(result['baseUrl'],configuration['downloadBaseUrl'].format(tag=configuration['tagName'].format(version='1')))
                self.assertEqual(build.read(output/configuration['files']['packs']),result)
                self.assertTrue((output/configuration['files']['packageInputs']).is_file())
                release_path.unlink()
                self.assertEqual(build.create_resource_packs(manifest,output,'1',configuration=configuration),result)
                build.validate_resource_release(build.read(release_path),'1',configuration)
                with self.assertRaisesRegex(ValueError,'distribution configuration'):
                    build.create_resource_packs(manifest,output,'1',configuration=dict(configuration,downloadBaseUrl='https://changed.example.test/{tag}/'))

    def test_changed_appearance_cannot_be_packaged_without_blender_and_unity(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            distributed = root / 'distributed'
            distributed.mkdir()
            name = 'groom.bundle'
            old = dict(file=name, sha256='a' * 64)
            new = dict(file=name, sha256='b' * 64)
            build.write(distributed / build.RESOURCE_FILES['packs'], dict(files=[old]))
            entry = dict(type='GameObject', asset='Human_Female_HairShape_B.prefab', bundles=[dict(file=name)])
            manifest = root / build.RESOURCE_FILES['manifest']
            template = dict(schemaVersion=3, resources=[entry], meshes=[], materials=[], textureRemaps=[])
            output = root / 'packs'
            with mock.patch.object(build, 'PUBLIC', distributed), mock.patch.object(build, 'checked_catalog_path',
                    side_effect=lambda path, name=None: Path(path) / name if name else Path(path)):
                for asset, parameters in (
                    ('Human_Female_HairShape_B.prefab', []), ('Human_Male_BeardShape_I.prefab', []),
                    ('Elf_Female_FaceShape_C.prefab', []), ('NPC.prefab', [dict(set='Groom')]),
                ):
                    with self.subTest(asset=asset):
                        entry.update(asset=asset, parameters=parameters)
                        build.verify_character_mesh_authoring(manifest, dict(template, bundles=[old]), {})
                        build.write(manifest, dict(template, bundles=[new]))
                        with self.assertRaisesRegex(ValueError, 'Blender export and Unity mesh readback'):
                            build.create_resource_packs(manifest, output, PACKAGE_TEST_VERSION)
                        self.assertFalse(output.exists())

    def test_resource_upgrade_packs_unchanged_bundles_separately(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            source = root / 'source'
            source.mkdir()
            older = root / 'previous.json'
            catalog = source / build.RESOURCE_FILES['manifest']
            template = dict(schemaVersion=3, meshes=[], materials=[], textureRemaps=[])
            cached = source / 'cached.bundle'
            revised = source / 'revised.bundle'
            cached.write_bytes(b'unchanged')
            revised.write_bytes(b'improved')
            reuse = dict(file=cached.name, sha256=build.sha(cached))
            upgrade = dict(file=revised.name, sha256=build.sha(revised))
            build.write(older, dict(template, bundles=[reuse]))
            build.write(catalog, dict(template, bundles=[reuse, upgrade]))
            with mock.patch.object(build, 'checked_catalog_path',
                                   side_effect=lambda path, name=None: Path(path) / name if name else Path(path)):
                result = build.create_resource_packs(catalog, root / 'packs', PACKAGE_TEST_VERSION, previous_manifest=older)
                self.assertEqual([part['files'] for part in result['packs']], [[revised.name], [cached.name]])
                build.write(older, dict(template, bundles=[reuse, upgrade]))
                with self.assertRaisesRegex(ValueError, 'other inputs'):
                    build.create_resource_packs(catalog, root / 'packs', PACKAGE_TEST_VERSION, previous_manifest=older)

    def test_resource_publication_preserves_the_verified_catalog_digest(self):
        with tempfile.TemporaryDirectory()as folder:
            root=Path(folder);original=root/'original';candidate=root/'candidate';current=root/'public'
            for path in(original,candidate,current):path.mkdir()
            payload=b'unchanged-owned-resource'
            for path in(original,candidate,current):(path/'actor.bundle').write_bytes(payload)
            manifest=dict(schemaVersion=3,recipeVersion='revision',meshes=[],materials=[],textureRemaps=[],
                bundles=[dict(file='actor.bundle',sha256=build.sha(candidate/'actor.bundle'))],
                resources=[dict(source='Actor/Prefab.prefab',type='GameObject',bundles=[dict(file='actor.bundle')])])
            build.write(original/build.RESOURCE_FILES['manifest'],manifest)
            proof=dict(status='PASS_STANDALONE_CHARACTER_RESOURCES',originalModelDependenciesInherited=False,
                resources=1,bundles=1,bytes=len(payload),inputs=[],proofs=[dict(file='actor.bundle',bytes=len(payload),
                    source=str(original/'actor.bundle'),sourceSha256=build.sha(original/'actor.bundle'),
                    sha256=build.sha(candidate/'actor.bundle'),allRetainedObjectFieldsExactAfterReferenceRebasing=True)])
            build.write(candidate/'ResourceAssembly.json',proof)
            quality: dict[str,Any]=dict(resourceAssembly=dict(path=str(candidate/'ResourceAssembly.json'),sha256=build.sha(candidate/'ResourceAssembly.json')))
            for raw in(json.dumps(manifest).encode(),(json.dumps(manifest,indent=2)+'\r\n').encode()):
                (candidate/build.RESOURCE_FILES['manifest']).write_bytes(raw)
                quality['candidateManifestSha256']=build.sha(candidate/build.RESOURCE_FILES['manifest'])
                with mock.patch.object(build,'checked_catalog_path',side_effect=lambda path,name=None:Path(path)/name if name else Path(path)),\
                        mock.patch.object(build,'atomic_publish_catalog')as publish,contextlib.redirect_stdout(io.StringIO()):
                    build.publish_resource_catalog(candidate,current,original,manifest,quality)
                    self.assertEqual(publish.call_args.args[3],raw)
                    self.assertEqual(publish.call_args.args[-1]['publishedManifestSha256'],quality['candidateManifestSha256'])

    def test_physical_gaussian_matches_its_finite_zero_padded_kernel(self):
        production,_=build.source_owner()
        random=np.random.default_rng(412)
        for shape in((1,1),(2,3),(17,19),(17,19,3)):
            values=random.random(shape)
            for sigma in(.2,.75,1.5,4.,5.25,8.):
                with self.subTest(shape=shape,sigma=sigma):
                    offsets=np.arange(-int(np.ceil(sigma*3)),int(np.ceil(sigma*3))+1)
                    kernel=np.exp(-.5*(offsets/sigma)**2);kernel/=kernel.sum();expected=values.copy()
                    for axis in(0,1):
                        expected=np.apply_along_axis(lambda row:np.convolve(np.pad(row,(len(kernel)//2,)*2),kernel,mode='valid'),axis,expected)
                    np.testing.assert_allclose(production.rgb_gaussian(values,sigma),expected,rtol=0,atol=1e-15)

    def test_source_material_domains_follow_declared_anatomy_without_species_or_shader_fallbacks(self):
        production,_=build.source_owner()
        packet=dict(weights=[1.,0.,0.,0.]*4,boneIndices=[0]*4+[1,0,0,0]+[2,0,0,0]+[3,0,0,0])
        bones=['Rig/Carapace','Rig/Carapace/Plate','Rig/Carapace/MouthContact','Rig/Leg']
        declaration=dict(includeBranches=['Carapace'],protectPrefixes=['Mouth'])
        np.testing.assert_array_equal(production.source_material_ownership(packet,bones,declaration),[1.,1.,0.,0.])
        with self.assertRaisesRegex(ValueError,'absent'):
            production.source_material_ownership(packet,bones,dict(declaration,includeBranches=['Bip001 Head']))

    def test_source_chart_protection_uses_mesh_identity_and_indexed_edges_without_spatial_welds(self):
        production,_=build.source_owner();identity='a'*64
        vertices=[0.,0.,0.,.004,0.,0.,.004,.004,0.,0.,.004,0.]*2
        packet=dict(sourceFingerprint=identity,vertices=vertices,weights=[1.,0.,0.,0.]*8,boneIndices=[0]*32,
            subMeshes=[[0,1,2,0,2,3,4,5,6,4,6,7]],uv=[dict(channel=0,values=[
                .05,.05,0.,0.,.45,.05,0.,0.,.45,.45,0.,0.,.05,.45,0.,0.,
                .55,.55,0.,0.,.95,.55,0.,0.,.95,.95,0.,0.,.55,.95,0.,0.])])
        original=copy.deepcopy(packet);bones=['Rig/Tissue']
        base=dict(includeBranches=['Tissue'],protectPrefixes=['Unowned'])
        anchor=dict(type='sourceTriangle',subMesh=0,triangleOrdinal=0,barycentric=[.25,.25,.5])
        declaration=dict(base,protectSourceCharts={identity:[anchor]})
        np.testing.assert_array_equal(production.source_material_ownership(packet,bones,base),np.ones(8))
        np.testing.assert_array_equal(production.source_material_ownership(packet,bones,declaration),[0.,0.,0.,0.,1.,1.,1.,1.])
        prepared={}
        for triangle,expected in [([0,1,2],0.),([0,2,3],0.),([4,5,6],1.),([4,6,7],1.)]:
            values=production.source_material_ownership(packet,bones,declaration,sample_triangle=triangle,
                sample_barycentric=[[.25,.25,.5],[1.,0.,0.]],_prepared=prepared)
            np.testing.assert_array_equal(values,[expected,expected])
        self.assertEqual(packet,original)
        for changed,error in [
            (dict(declaration,protectSourceCharts={'b'*64:[anchor]}),'mesh identity'),
            (dict(declaration,protectSourceCharts={identity:[anchor,dict(anchor,triangleOrdinal=1)]}),'repeats'),
            (dict(declaration,protectSourceCharts={identity:[dict(anchor,triangleOrdinal=99)]}),'outside'),
            (dict(declaration,protectSourceCharts={identity:[dict(anchor,barycentric=[0.,0.,0.])]}),'barycentric'),
            (dict(declaration,protectSourceCharts={identity:[]}),'fingerprints')]:
            with self.assertRaisesRegex(ValueError,error):production.source_material_ownership(packet,bones,changed)
        with self.assertRaisesRegex(ValueError,'native UV'):
            production.source_material_ownership(dict(packet,uv=[]),bones,declaration)
        collapsed=copy.deepcopy(packet);collapsed['uv'][0]['values']=[0.]*32
        with self.assertRaisesRegex(ValueError,'collapsed UV'):
            production.source_material_ownership(collapsed,bones,declaration)

    def test_source_chart_protection_resolves_each_mesh_lod_instead_of_reusing_triangle_ordinals(self):
        production,_=build.source_owner();bones=['Rig/Tissue'];first='a'*64;second='b'*64
        packet=dict(sourceFingerprint=first,vertices=[0.,0.,0.,1.,0.,0.,0.,1.,0.]*2,
            weights=[1.,0.,0.,0.]*6,boneIndices=[0]*24,subMeshes=[[0,1,2,3,4,5]],
            uv=[dict(channel=0,values=[0.,0.,0.,0.,.4,0.,0.,0.,0.,.4,0.,0.,.6,.6,0.,0.,1.,.6,0.,0.,.6,1.,0.,0.])])
        anchor=dict(type='sourceTriangle',subMesh=0,triangleOrdinal=0,barycentric=[1/3]*3)
        declaration=dict(includeBranches=['Tissue'],protectPrefixes=['Unowned'],protectSourceCharts={
            first:[anchor],second:[dict(anchor,triangleOrdinal=1)]})
        reordered=dict(packet,sourceFingerprint=second,subMeshes=[[3,4,5,0,1,2]])
        expected=[0.,0.,0.,1.,1.,1.]
        np.testing.assert_array_equal(production.source_material_ownership(packet,bones,declaration),expected)
        np.testing.assert_array_equal(production.source_material_ownership(reordered,bones,declaration),expected)
        with self.assertRaisesRegex(ValueError,'mesh identity'):
            production.source_material_ownership(dict(packet,sourceFingerprint='c'*64),bones,declaration)

    def test_source_chart_identity_is_checked_before_skin_authority_reuse(self):
        import character_resources as resources
        production,art=build.source_owner()
        with tempfile.TemporaryDirectory()as directory:
            root=Path(directory);source=root/'source';catalog=source/'Catalog';catalog.mkdir(parents=True)
            output=root/'tools/game-test/artifacts/candidate';census_path=root/'census.json';plans_path=root/'plans.json'
            bundle=catalog/'input.bundle';bundle.write_bytes(b'owned source fixture')
            manifest=dict(bundles=[dict(file=bundle.name,sha256=build.sha(bundle))],
                resources=[dict(asset='source-prefab',bundles=[dict(file=bundle.name)])])
            build.write(catalog/build.RESOURCE_FILES['manifest'],manifest)
            build.write(source/'ResourceAssembly.json',dict(catalogSha256=build.sha(catalog/build.RESOURCE_FILES['manifest']),
                proofs=[dict(file=bundle.name,sha256=build.sha(bundle),newCab='source-cab')]))
            build.write(census_path,dict(assets=[dict(name='Source',sourceContentKey='source-key',internalId='source-prefab',
                skins=[dict(path='Head',sourceProvenance=dict(rendererPathId=1),rendererToPrefab=np.eye(4).ravel().tolist())])]))
            domain=dict(includeBranches=['Tissue'],protectPrefixes=['Unowned'],protectSourceCharts={'a'*64:[
                dict(type='sourceTriangle',subMesh=0,triangleOrdinal=0,barycentric=[1/3]*3)]})
            build.write(plans_path,dict(surfaces=dict(Source=dict(sourceContentKey='source-key',domain=domain,
                profile=dict(pigmentRetain=.5,normalRetain=1.),renderers=[dict(path='Head',slots=[0],shader='source-shader')]))))
            pointer=lambda value:dict(m_FileID=0,m_PathID=value)
            objects={1:SimpleNamespace(read_typetree=lambda:dict(m_Mesh=pointer(2),m_Materials=[pointer(3)])),
                2:object(),3:SimpleNamespace(read_typetree=lambda:dict(m_Shader=pointer(4),
                    m_SavedProperties=dict(m_TexEnvs=[('_MainTex',dict(m_Texture=pointer(5)))]))),
                4:SimpleNamespace(read_typetree=lambda:dict(m_ParsedForm=dict(m_Name='source-shader'))),5:SimpleNamespace(path_id=5)}
            file=SimpleNamespace(name='source-cab',objects=objects)
            packet=dict(sourceFingerprint='b'*64,vertices=[0.,0.,0.]*3,weights=[1.,0.,0.,0.]*3,boneIndices=[0]*12)
            arguments=['skin','--source',str(source),'--census',str(census_path),'--plans',str(plans_path),'--output',str(output)]
            with mock.patch.object(build,'ROOT',root),mock.patch.object(build,'checked_catalog_path',side_effect=lambda path:Path(path).resolve()),\
                    mock.patch.object(build,'source_owner',return_value=(production,art)),\
                    mock.patch.object(build,'export_current_mesh',return_value=packet),\
                    mock.patch.object(resources,'load_serialized_bundle',return_value=(None,file)),\
                    mock.patch.object(resources,'skin_bone_paths',return_value=['Rig/Tissue']),\
                    mock.patch.object(build,'source_skin_detail_authority',side_effect=AssertionError('Authority reuse must follow identity validation.'))as authority,\
                    mock.patch.object(sys,'argv',arguments):
                with self.assertRaisesRegex(ValueError,'mesh identity'):build.prepare_skin_surface_resources()
                authority.assert_not_called()

    def test_source_chart_pixels_keep_native_color_alpha_and_normal_channels_at_each_mip(self):
        production,_=build.source_owner();identity='a'*64;bones=['Rig/Tissue']
        packet=dict(sourceFingerprint=identity,vertices=[0.,0.,0.,.004,0.,0.,.004,.004,0.,0.,.004,0.]*2,
            weights=[1.,0.,0.,0.]*8,boneIndices=[0]*32,subMeshes=[[0,1,2,0,2,3,4,5,6,4,6,7]],
            uv=[dict(channel=0,values=[.05,.05,0.,0.,.45,.05,0.,0.,.45,.45,0.,0.,.05,.45,0.,0.,
                .55,.55,0.,0.,.95,.55,0.,0.,.95,.95,0.,0.,.55,.95,0.,0.])])
        declaration=dict(includeBranches=['Tissue'],protectPrefixes=['Unowned'],protectSourceCharts={identity:[
            dict(type='sourceTriangle',subMesh=0,triangleOrdinal=0,barycentric=[1/3]*3)]})
        skin=dict(rendererToPrefab=np.eye(4).ravel().tolist(),materials=[dict(properties=[dict(name='_MainTex',scale=[1,1],offset=[0,0])])])
        profile=dict(pigmentRetain=.25,normalRetain=.25,normalRadiusMetres=.001,
            fineSigmaMetres=.0001,grainSigmaMetres=.00075,coherenceSigmaMetres=.001,featherMetres=.003)
        for size in (32,16,8,4):
            ownership,metric,_,covered=production.indexed_surface_uv_metrics(packet,skin,0,(size,size),.001,
                dict(wrapU=1,wrapV=1),bone_domain=dict(bones=bones,declaration=declaration))
            protected=covered&(ownership==0)
            self.assertGreater(np.count_nonzero(protected),0);self.assertGreater(np.count_nonzero(ownership),0)
            y,x=np.mgrid[:size,:size];pixels=np.full((size,size,4),255,np.uint8)
            pixels[:,:,:3]=np.where((x%2)[:,:,None]==0,80,150);pixels[:,:,3]=(x+y+100).astype(np.uint8)
            color,_=production.refine_source_skin_material(pixels,ownership,metric,profile,'color')
            np.testing.assert_array_equal(color[protected],pixels[protected]);np.testing.assert_array_equal(color[:,:,3],pixels[:,:,3])
            normal=np.full((size,size,4),255,np.uint8);normal[:,:,1]=np.where(x%2,100,156);normal[:,:,3]=np.where(y%2,105,151)
            result,proof=production.refine_source_skin_material(normal,ownership,metric,profile,'normal',normal_encoding='packed-rgorag')
            np.testing.assert_array_equal(result[protected],normal[protected])
            self.assertTrue(proof['sourceCarriersExact'])

    def test_multiple_source_domains_share_sampler_witnesses_without_changing_any_field_or_saved_pixel(self):
        production,_=build.source_owner();identity='c'*64;bones=['Rig/Tissue']
        packet=dict(sourceFingerprint=identity,vertices=[0.,0.,0.,.004,0.,0.,.004,.004,0.,0.,.004,0.]*2,
            weights=[1.,0.,0.,0.]*8,boneIndices=[0]*32,bindPoses=np.eye(4).ravel().tolist(),
            subMeshes=[[0,1,2,0,2,3,4,5,6,4,6,7]],uv=[dict(channel=0,values=[
                .05,.05,0.,0.,.45,.05,0.,0.,.45,.45,0.,0.,.05,.45,0.,0.,
                .55,.55,0.,0.,.95,.55,0.,0.,.95,.95,0.,0.,.55,.95,0.,0.])])
        base=dict(includeBranches=['Tissue'],protectPrefixes=['Other'],protectSourceCharts={identity:[
            dict(type='sourceTriangle',subMesh=0,triangleOrdinal=0,barycentric=[1/3]*3)]})
        domains={name:dict(bones=bones,declaration=dict(base,surfaceRegion=dict(
            coordinateDomain='native-rest-prefab-dcc-metres',ellipsoids=[dict(center=center,radii=[.004,.003,.002],innerFraction=.3)])))
            for name,center in [('target',[.001,.001,0.]),('reference',[.003,.003,0.])]}
        profile=dict(pigmentRetain=.25,normalRetain=.25,normalRadiusMetres=.001,
            fineSigmaMetres=.0001,grainSigmaMetres=.00075,coherenceSigmaMetres=.001,featherMetres=.003)
        for size in(32,16,8,4):
            for mode in(0,1,2):
                for wrap in(0,1,2,3):
                    for scale,offset in(([1.,1.],[0.,0.]),([.8,.8],[.1,.1])):
                        skin=dict(rendererToPrefab=np.eye(4).ravel().tolist(),materials=[dict(properties=[dict(name='_MainTex',scale=scale,offset=offset)])])
                        sampler=dict(filterMode=mode,anisoLevel=1,wrapU=wrap,wrapV=wrap,mipLevel=int(np.log2(32//size)))
                        with self.subTest(size=size,filter=mode,wrap=wrap,scale=scale):
                            before=copy.deepcopy(packet)
                            reference=production.indexed_surface_uv_metrics(packet,skin,0,(size,size),.001,sampler,bone_domain=dict(bones=bones,declaration=base))
                            combined=production.indexed_surface_uv_metrics(packet,skin,0,(size,size),.001,sampler,
                                bone_domain=dict(bones=bones,declaration=base),additional_domains=domains)
                            for expected,actual in zip(reference[:2],combined[:2]):np.testing.assert_array_equal(actual,expected)
                            self.assertEqual(combined[2],reference[2]);np.testing.assert_array_equal(combined[3],reference[3])
                            y,x=np.mgrid[:size,:size];pixels=np.full((size,size,4),255,np.uint8)
                            pixels[:,:,:3]=np.where((x%2)[:,:,None]==0,80,150);pixels[:,:,3]=(x+y+100).astype(np.uint8)
                            normal=np.full_like(pixels,255);normal[:,:,1]=np.where(x%2,100,156);normal[:,:,3]=np.where(y%2,105,151)
                            for name,domain in domains.items():
                                independent=production.indexed_surface_uv_metrics(packet,skin,0,(size,size),.001,sampler,bone_domain=domain)
                                observed=combined[4][name]
                                np.testing.assert_array_equal(observed['ownership'],independent[0]);self.assertEqual(observed['report'],independent[2])
                                np.testing.assert_array_equal(combined[1],independent[1]);np.testing.assert_array_equal(combined[3],independent[3])
                                for channel,image,options in(('color',pixels,{}),('normal',normal,dict(normal_encoding='packed-rgorag'))):
                                    expected,_=production.refine_source_skin_material(image,independent[0],independent[1],profile,channel,**options)
                                    actual,_=production.refine_source_skin_material(image,observed['ownership'],combined[1],profile,channel,**options)
                                    self.assertEqual(actual.tobytes(),expected.tobytes())
                            self.assertEqual(packet,before)

    def test_native_sampler_footprints_keep_source_witnesses_at_cuts_subpixel_triangles_and_each_mip(self):
        production,_=build.source_owner()
        def address(value,size,mode):
            if mode==0:return value%size
            if mode==1:return max(0,min(size-1,value))
            if mode==2:
                value%=2*size;return value if value<size else 2*size-1-value
            return min(size-1,value if value>=0 else -value-1)
        def fetch(query,shape,mode,wrap):
            h,w=shape;result=set()
            if mode==0:points=[np.floor(query+.5).astype(int)]
            else:
                base=np.floor(query).astype(int);fraction=query-base
                points=[base+[x,y]for x in(0,1)for y in(0,1)
                    if (fraction[0]if x else 1-fraction[0])*(fraction[1]if y else 1-fraction[1])>0]
            for x,y in points:result.add((address(int(y),h,wrap),address(int(x),w,wrap)))
            return result
        def reference(triangle,shape,mode,wrap):
            # Independent Sutherland-Hodgman intersection, not the production
            # raster's batched polygon-vertex construction.
            radius=.5 if mode==0 else 1.;result=set()
            lo=np.floor(triangle.min(0)-radius).astype(int);hi=np.ceil(triangle.max(0)+radius).astype(int)
            for y in range(lo[1],hi[1]+1):
                for x in range(lo[0],hi[0]+1):
                    polygon=list(triangle)
                    for axis,bound,direction in((0,x-radius,1),(0,x+radius,-1),(1,y-radius,1),(1,y+radius,-1)):
                        next_polygon=[]
                        for first,last in zip(polygon,polygon[1:]+polygon[:1]):
                            a=(first[axis]-bound)*direction;b=(last[axis]-bound)*direction
                            if a>=0:next_polygon.append(first)
                            if (a>=0)!=(b>=0):next_polygon.append(first+(last-first)*(a/(a-b)))
                        polygon=next_polygon
                        if not polygon:break
                    if polygon:
                        point=np.mean(polygon,axis=0)
                        valid=(np.array_equal(np.floor(point+.5).astype(int),[x,y])if mode==0 else np.all(np.abs(point-[x,y])<1))
                        if valid:result.add((address(y,shape[0],wrap),address(x,shape[1],wrap)))
            return result
        skin=dict(rendererToPrefab=np.eye(4).ravel().tolist(),materials=[dict(properties=[dict(name='_MainTex',scale=[1,1],offset=[0,0])])])
        for size in(16,8,4):
            examples=[np.array([[.25,.25],[.75,.25],[.25,.75]]),
                np.array([[1.,1.],[3.,1.],[1.,3.]]),np.array([[-.5,.25],[.25,.25],[-.5,1.25]])]
            for triangle in examples:
                points=np.column_stack((triangle*.001,np.zeros(3)))
                uv=np.column_stack(((triangle[:,0]+.5)/size,1-(triangle[:,1]+.5)/size,np.zeros((3,2))))
                packet=dict(vertices=points.ravel().tolist(),uv=[dict(channel=0,values=uv.ravel().tolist())],subMeshes=[[0,1,2]])
                for mode in(0,1,2):
                    for wrap in(0,1,2,3):
                        with self.subTest(size=size,triangle=triangle.tolist(),mode=mode,wrap=wrap):
                            own,metric,proof,covered=production.indexed_surface_uv_metrics(packet,skin,0,(size,size),.001,
                                dict(wrapU=wrap,wrapV=wrap,filterMode=mode,anisoLevel=1),vertex_field=np.ones(3))
                            actual=set(zip(*np.nonzero(covered)));expected=reference(triangle,(size,size),mode,wrap)
                            self.assertEqual(actual,expected);np.testing.assert_allclose(own[covered],np.ones(len(actual)),rtol=0,atol=2e-15)
                            self.assertTrue(np.all(metric[covered]>0));self.assertNotEqual(proof['sampledSupport'],'legacy texel centres')
                            for first in range(13):
                                for second in range(13-first):
                                    bary=np.array([first,second,12-first-second])/12
                                    self.assertTrue(fetch(bary@triangle,(size,size),mode,wrap)<=actual)

    def test_sampled_source_charts_protect_nearby_texels_without_dilating_unrelated_texture(self):
        production,_=build.source_owner();size=12
        triangles=np.array([[[.25,.25],[3.75,.25],[.25,3.75]],
            [[3.25,.25],[3.75,.25],[3.25,.75]],[[7.25,7.25],[9.75,7.25],[7.25,9.75]]])
        points=triangles.reshape(-1,2);uv=np.column_stack(((points[:,0]+.5)/size,1-(points[:,1]+.5)/size,np.zeros((9,2))))
        packet=dict(vertices=np.column_stack((points*.001,np.zeros(9))).ravel().tolist(),
            uv=[dict(channel=0,values=uv.ravel().tolist())],subMeshes=[list(range(9))])
        skin=dict(rendererToPrefab=np.eye(4).ravel().tolist(),materials=[dict(properties=[dict(name='_MainTex',scale=[1,1],offset=[0,0])])])
        sampler=dict(wrapU=1,wrapV=1,filterMode=1,anisoLevel=1)
        own,_,_,covered=production.indexed_surface_uv_metrics(packet,skin,0,(size,size),.001,sampler,
            vertex_field=np.array([1.]*3+[0.]*3+[1.]*3))
        self.assertTrue(covered[0,3]);self.assertEqual(own[0,3],0.)
        self.assertTrue(covered[0,0]);self.assertEqual(own[0,0],1.)
        self.assertFalse(covered[6,6]);self.assertEqual(own[6,6],0.)
        requested=np.zeros((size,size),dtype=bool)
        requested[0,:5]=True
        cropped=production.indexed_surface_uv_metrics(packet,skin,0,(size,size),.001,sampler,
            vertex_field=np.array([1.]*3+[0.]*3+[1.]*3),requested_pixels=requested)
        np.testing.assert_array_equal(cropped[0][requested],own[requested])
        np.testing.assert_array_equal(cropped[3],covered&requested)
        # Wrapped aliases of one source triangle also use the minimum, rather
        # than the last witness in a repeated indexed assignment.
        packet['subMeshes']=[list(reversed(range(9)))]
        reordered=production.indexed_surface_uv_metrics(packet,skin,0,(size,size),.001,sampler,
            vertex_field=np.array([1.]*3+[0.]*3+[1.]*3))
        np.testing.assert_allclose(reordered[0],own,rtol=0,atol=2e-15);np.testing.assert_array_equal(reordered[3],covered)

    def test_sampler_footprint_is_explicit_and_undeclared_center_raster_is_unchanged(self):
        production,_=build.source_owner()
        packet=dict(vertices=[0.,0.,0.,.001,0.,0.,0.,.001,0.],subMeshes=[[0,1,2]],
            uv=[dict(channel=0,values=[.1875,.8125,0.,0.,.3125,.8125,0.,0.,.1875,.6875,0.,0.])])
        skin=dict(rendererToPrefab=np.eye(4).ravel().tolist(),materials=[dict(properties=[dict(name='_MainTex',scale=[1,1],offset=[0,0])])])
        call=lambda sampler:production.indexed_surface_uv_metrics(packet,skin,0,(4,4),.001,sampler,vertex_field=np.ones(3))
        # q is the subpixel triangle [.25,.25] / [.75,.25] / [.25,.75].
        with self.assertRaisesRegex(ValueError,'rasterizable'):call(dict(wrapU=1,wrapV=1))
        own,_,_,covered=call(dict(wrapU=1,wrapV=1,filterMode=1,anisoLevel=1))
        self.assertEqual(set(zip(*np.nonzero(covered))),{(0,0),(0,1),(1,0),(1,1)})
        for mode in(-1,3,True):
            with self.assertRaisesRegex(ValueError,'filter mode'):call(dict(wrapU=1,wrapV=1,filterMode=mode))
        with self.assertRaisesRegex(ValueError,'anisotropic'):call(dict(wrapU=1,wrapV=1,filterMode=1,anisoLevel=2))

    def test_source_feature_protection_uses_its_physical_surface_band_instead_of_the_full_skin_weight(self):
        production,_=build.source_owner()
        bones=['Rig/Tissue/Contact','Rig/Tissue/Lip','Rig/Other','Rig/Tissue']
        points=np.array([[.001,0,0],[.0105,0,0],[.03,0,0],[.001,0,0],[.009,0,0],[.03,0,0],[.03,0,0]])
        packet=dict(vertices=points.ravel().tolist(),weights=[1.,0.,0.,0.]*7,
            boneIndices=[0,0,0,0]*3+[1,0,0,0]*2+[2,0,0,0]+[3,0,0,0],
            bindPoses=list(np.tile(np.eye(4).ravel(),4)))
        base=dict(includeBranches=['Tissue'],protectPrefixes=['Contact','Lip'])
        np.testing.assert_array_equal(production.source_material_ownership(packet,bones,base),[0.,0.,0.,0.,0.,0.,1.])
        limited=dict(base,protectSurfaceRadiusMetres={'Contact':[.006,.012],'Lip':[.002,.004]})
        actual=production.source_material_ownership(packet,bones,limited,renderer_to_prefab=np.eye(4).ravel().tolist())
        self.assertEqual(actual[0],0.)
        self.assertGreater(actual[1],0.)
        np.testing.assert_array_equal(actual[[2,3,4,5,6]],[1.,0.,1.,0.,1.])
        # Native mesh coordinates can differ from prefab metres. Preserve the
        # physical band when the mesh/bind data is re-expressed at another scale.
        scaled=dict(packet,vertices=(points*100).ravel().tolist())
        frame=np.eye(4);frame[:3,:3]*=.01
        np.testing.assert_allclose(production.source_material_ownership(scaled,bones,limited,
            renderer_to_prefab=frame.T.ravel().tolist()),actual,rtol=0,atol=1e-15)
        with self.assertRaisesRegex(ValueError,'renderer frame'):
            production.source_material_ownership(packet,bones,limited)
        for limits in({'Contact':[.012,.006]},{'Unknown':[.001,.002]},{'Contact':[0,float('nan')]}):
            with self.assertRaisesRegex(ValueError,'physical radii'):
                production.source_material_ownership(packet,bones,dict(base,protectSurfaceRadiusMetres=limits),
                    renderer_to_prefab=np.eye(4).ravel().tolist())

        # A coarse native triangle may surround a protected feature even when
        # its corner vertices all lie beyond the feature radius. Evaluate the
        # actual sampled surface instead of interpolating that missing island.
        coarse=dict(vertices=[-.02,-.02,0,.02,-.02,0,0,.02,0],
            weights=[1.,0,0,0]*3,boneIndices=[0,0,0,0]*3,
            bindPoses=np.tile(np.eye(4).ravel(),4).tolist())
        declaration=dict(base,protectSurfaceRadiusMetres={'Contact':[.002,.004]})
        np.testing.assert_array_equal(production.source_material_ownership(coarse,bones,declaration,
            renderer_to_prefab=np.eye(4).ravel().tolist()),np.ones(3))
        sampled=production.source_material_ownership(coarse,bones,declaration,
            renderer_to_prefab=np.eye(4).ravel().tolist(),sample_triangle=[0,1,2],
            sample_barycentric=[[.25,.25,.5],[1.,0,0]])
        np.testing.assert_array_equal(sampled,[0.,1.])
        skin=dict(rendererToPrefab=np.eye(4).ravel().tolist(),materials=[dict(properties=[dict(name='_MainTex',scale=[1,1],offset=[0,0])])])
        coarse.update(subMeshes=[[0,1,2]],uv=[dict(channel=0,values=[0,0,0,0,1,0,0,0,.5,1,0,0])])
        owned,_,_,coverage=production.indexed_surface_uv_metrics(coarse,skin,0,(64,64),.001,dict(wrapU=1,wrapV=1),
            bone_domain=dict(bones=bones,declaration=declaration))
        self.assertEqual(owned[31,31],0.)
        self.assertGreater(np.count_nonzero(owned[coverage]==1),100)
        # Blended source bone origins form one continuous feature band instead
        # of leaving circular holes between its separately animated owners.
        left=np.eye(4);left[0,3]=.01;right=np.eye(4);right[0,3]=-.01
        blended=dict(vertices=[0,0,0,.1,0,0],weights=[.5,.5,0,0]*2,boneIndices=[0,1,0,0]*2,
            bindPoses=np.concatenate((left.T.ravel(),right.T.ravel())).tolist())
        np.testing.assert_array_equal(production.source_material_ownership(blended,
            ['Rig/Tissue/ContactA','Rig/Tissue/ContactB'],dict(includeBranches=['Tissue'],protectPrefixes=['Contact'],
                protectSurfaceRadiusMetres={'Contact':[.002,.004]}),renderer_to_prefab=np.eye(4).ravel().tolist()),[0.,1.])
        # A shared texture may also cover an LOD/body surface whose native bone
        # palette omits the protected facial feature. Keep its existing domain.
        no_feature=dict(packet,boneIndices=[0,0,0,0]*6+[1,0,0,0],bindPoses=np.tile(np.eye(4).ravel(),2).tolist())
        other_palette=['Rig/Tissue','Rig/Other']
        np.testing.assert_array_equal(production.source_material_ownership(no_feature,other_palette,limited,
            renderer_to_prefab=np.eye(4).ravel().tolist()),production.source_material_ownership(no_feature,other_palette,base))

    def test_source_skin_material_refinement_keeps_features_alpha_and_physical_normal_contract(self):
        production,_=build.source_owner()
        y,x=np.mgrid[:16,:16]
        pixels=np.full((16,16,4),255,np.uint8);pixels[:,:,:3]=np.where((x%2)[:,:,None]==0,80,150)
        pixels[:,:,3]=np.arange(16,dtype=np.uint8)[None,:]+100
        mask=np.ones((16,16));mask[:3]=0;metric=np.full((16,16),.00025)
        profile=dict(pigmentRetain=.5,normalRetain=.5,normalRadiusMetres=.001,
            fineSigmaMetres=.0001,grainSigmaMetres=.00075,coherenceSigmaMetres=.001,featherMetres=.003)
        color,proof=production.refine_source_skin_material(pixels,mask,metric,profile,'color')
        np.testing.assert_array_equal(color[:,:,3],pixels[:,:,3]);np.testing.assert_array_equal(color[:3],pixels[:3])
        self.assertTrue(proof['unownedPixelsExact'])
        normal=np.full((16,16,4),255,np.uint8);normal[:,:,1]=np.where(x%2,100,156);normal[:,:,3]=np.where(y%2,105,151)
        softened,proof=production.refine_source_skin_material(normal,mask,metric,profile,'normal',normal_encoding='packed-rgorag')
        np.testing.assert_array_equal(softened[:,:,[0,2]],normal[:,:,[0,2]])
        np.testing.assert_array_equal(softened[:3],normal[:3])
        self.assertLess(np.std(softened[3:,:,1]),np.std(normal[3:,:,1]))
        self.assertTrue(proof['sourceCarriersExact'])
        unchanged,_=production.refine_source_skin_material(normal,mask,metric,dict(profile,normalRetain=1.),'normal',normal_encoding='packed-rgorag')
        np.testing.assert_array_equal(unchanged,normal)
        with self.assertRaisesRegex(ValueError,'decoding contract'):
            production.refine_source_skin_material(normal,mask,metric,profile,'normal',normal_encoding='guessed')

    def test_local_source_regions_use_prefab_metres_and_actual_triangle_samples(self):
        production,_=build.source_owner();points=np.array([[-.02,-.02,0],[.02,-.02,0],[0,.02,0],[0,0,0]])
        packet=dict(vertices=points.ravel().tolist(),weights=[1.,0,0,0]*4,boneIndices=[0,0,0,0]*4)
        declaration=dict(includeBranches=['Tissue'],protectPrefixes=['Unowned'],surfaceRegion=dict(
            coordinateDomain='native-rest-prefab-dcc-metres',ellipsoids=[dict(center=[0.,0.,0.],radii=[.01,.01,.01],innerFraction=.25)]))
        frame=np.eye(4);expected=production.source_material_ownership(packet,['Rig/Tissue'],declaration,renderer_to_prefab=frame.T.ravel().tolist())
        np.testing.assert_array_equal(expected,[0.,0.,0.,1.])
        sampled=production.source_material_ownership(packet,['Rig/Tissue'],declaration,renderer_to_prefab=frame.T.ravel().tolist(),
            sample_triangle=[0,1,2],sample_barycentric=[[.25,.25,.5],[1,0,0]])
        np.testing.assert_array_equal(sampled,[1.,0.])
        frame[:3,:3]*=.01
        np.testing.assert_array_equal(production.source_material_ownership(dict(packet,vertices=(points*100).ravel().tolist()),
            ['Rig/Tissue'],declaration,renderer_to_prefab=frame.T.ravel().tolist()),expected)
        with self.assertRaisesRegex(ValueError,'renderer frame'):
            production.source_material_ownership(packet,['Rig/Tissue'],declaration)

    def test_directed_source_regions_separate_coincident_surfaces_with_their_actual_normals(self):
        production,_=build.source_owner();frame=np.eye(4).ravel().tolist()
        packet=dict(vertices=[0.,0.,0.]*3,normals=[0.,0.,1.,0.,0.,-1.,0.,0.,1.],
            weights=[1.,0,0,0]*3,boneIndices=[0,0,0,0]*3)
        region=dict(coordinateDomain='native-rest-prefab-dcc-metres',ellipsoids=[dict(center=[0.,0.,0.],radii=[.01,.01,.01],innerFraction=.25)])
        domain=dict(includeBranches=['Tissue'],protectPrefixes=['Unowned'],surfaceRegion=region)
        original=production.source_material_ownership(packet,['Rig/Tissue'],domain,renderer_to_prefab=frame)
        np.testing.assert_array_equal(original,1.)
        region['ellipsoids'][0].update(surfaceNormal=[0.,-1.,0.],normalRange=[0.,.5])
        directed=production.source_material_ownership(packet,['Rig/Tissue'],domain,renderer_to_prefab=frame)
        np.testing.assert_array_equal(directed,[1.,0.,1.])
        samples=production.source_material_ownership(packet,['Rig/Tissue'],domain,renderer_to_prefab=frame,
            sample_triangle=[0,1,2],sample_barycentric=[[.75,.25,0],[.25,.75,0]])
        np.testing.assert_array_equal(samples,[1.,0.])
        transformed=np.array([[0.,0.,4.,0.],[0.,3.,0.,0.],[-2.,0.,0.,0.],[0.,0.,0.,1.]])
        region['ellipsoids'][0]['surfaceNormal']=[1.,0.,0.]
        rotated=production.source_material_ownership(packet,['Rig/Tissue'],domain,renderer_to_prefab=transformed.T.ravel().tolist())
        np.testing.assert_array_equal(rotated,directed)
        for value in(dict(surfaceNormal=[0.,0.,0.],normalRange=[0.,.5]),dict(surfaceNormal=[0.,-1.,0.],normalRange=[.5,0.])):
            region['ellipsoids'][0].update(value)
            with self.assertRaisesRegex(ValueError,'finite normal and ordered cosine'):
                production.source_material_ownership(packet,['Rig/Tissue'],domain,renderer_to_prefab=frame)

    def test_local_luminance_uses_adjacent_native_skin_and_preserves_marks_pores_alpha_and_other_regions(self):
        production,_=build.source_owner();y,x=np.mgrid[:64,:64]
        source=145*(1-.4*np.exp(-((x-25)/9)**2));source+=np.where(x%2,3.,-3.)
        pixels=np.empty((64,64,4),np.uint8);pixels[:,:,:3]=np.rint(source[:,:,None]*[1.,.8,.6]).astype(np.uint8);pixels[:,:,3]=y+100
        protected=np.zeros((64,64));protected[26:31,23:29]=1;pixels[protected>0,:3]=[14,11,8]
        ownership=np.ones((64,64));ownership[:4]=0;metric=np.full((64,64),.0002)
        target=np.exp(-((x-25)/11)**4);target[:,45:]=0;target[:4]=0;reference=(x>46).astype(float)
        profile=dict(pigmentRetain=1.,normalRetain=1.,normalRadiusMetres=.003,
            fineSigmaMetres=.0001,grainSigmaMetres=.00075,coherenceSigmaMetres=.001,featherMetres=.003)
        region=dict(target=target,reference=reference,protected=protected,sourceLowFrequencySigmaMetres=.0006,
            referenceSigmaMetres=.008,strength=.8,maximumLogGain=1.)
        candidate,proof=production.refine_source_skin_material(pixels,ownership,metric,profile,'color',luminance_regions=[region])
        self.assertGreater(float(candidate[10:20,23:28,0].mean()),float(pixels[10:20,23:28,0].mean())+15)
        np.testing.assert_array_equal(candidate[protected>0],pixels[protected>0]);np.testing.assert_array_equal(candidate[:,:,3],pixels[:,:,3])
        np.testing.assert_array_equal(candidate[target==0],pixels[target==0])
        self.assertGreater(abs(float(candidate[15,25,0])-float(candidate[15,26,0])),2.)
        self.assertTrue(proof['luminanceRegions'][0]['protectedRegionExact'])
        other=(pixels.astype(float)*np.array([.6,.6,.6,1.])).astype(np.uint8)
        darker,_=production.refine_source_skin_material(other,ownership,metric,profile,'color',luminance_regions=[region])
        self.assertLess(float(darker[10:20,23:28,0].mean()),float(candidate[10:20,23:28,0].mean())*.7)
        with self.assertRaisesRegex(ValueError,'color channel'):
            production.refine_source_skin_material(pixels,ownership,metric,profile,'normal',luminance_regions=[region])

    def test_local_rgb_reconstruction_uses_native_adjacent_color_and_keeps_fine_source_detail(self):
        production,_=build.source_owner();y,x=np.mgrid[:64,:64]
        pixels=np.empty((64,64,4),np.uint8);pixels[:,:,:3]=[185,137,112];pixels[:,:,3]=y+100
        pixels[12:52,16:40,:3]=np.where((x[12:52,16:40]%2)[:,:,None],40,15)
        pixels[34:38,24:28,:3]=0
        ownership=np.ones((64,64));ownership[:4]=0;metric=np.full((64,64),.0002)
        target=np.zeros((64,64));target[12:52,16:40]=1;reference=(x>46).astype(float)
        protected=np.zeros((64,64));protected[28:32,24:28]=1;pixels[protected>0,:3]=[6,5,4]
        profile=dict(pigmentRetain=1.,normalRetain=1.,normalRadiusMetres=.003,
            fineSigmaMetres=.0001,grainSigmaMetres=.00075,coherenceSigmaMetres=.001,featherMetres=.003)
        region=dict(target=target,reference=reference,protected=protected,sourceLowFrequencySigmaMetres=.0006,
            referenceSigmaMetres=.008,strength=.8,maximumLinearRgbDelta=.75,reconstruction='adjacent-low-frequency-rgb')
        candidate,proof=production.refine_source_skin_material(pixels,ownership,metric,profile,'color',luminance_regions=[region])
        self.assertGreater(float(candidate[18:25,22:34,0].mean()),130.)
        self.assertGreater(float(candidate[18:25,22:34,0].mean()),float(candidate[18:25,22:34,1].mean())+20.)
        np.testing.assert_array_equal(candidate[protected>0],pixels[protected>0]);np.testing.assert_array_equal(candidate[:,:,3],pixels[:,:,3])
        np.testing.assert_array_equal(candidate[target==0],pixels[target==0])
        self.assertGreater(abs(float(candidate[20,26,0])-float(candidate[20,27,0])),0.)
        self.assertGreater(float(candidate[35,25,0]),100.)
        cooler=pixels.copy();cooler[x>46,:3]=[112,137,185]
        shifted,_=production.refine_source_skin_material(cooler,ownership,metric,profile,'color',luminance_regions=[region])
        self.assertGreater(float(shifted[18:25,22:34,2].mean()),float(shifted[18:25,22:34,0].mean())+20.)
        self.assertTrue(proof['luminanceRegions'][0]['protectedRegionExact'])
        with self.assertRaisesRegex(ValueError,'linear LDR'):
            production.refine_source_skin_material(pixels,ownership,metric,profile,'color',
                luminance_regions=[dict(region,maximumLinearRgbDelta=1.1)])

    def test_regional_color_requires_reference_support_over_the_complete_target(self):
        production,_=build.source_owner();shape=(32,64)
        pixels=np.full((*shape,4),140,np.uint8);pixels[:,:,3]=np.arange(shape[0])[:,None]+80
        ownership=np.ones(shape);metric=np.full(shape,.001)
        target=np.zeros(shape);target[8:24,52:60]=1
        reference=np.zeros(shape);reference[:,2:6]=1
        protected=np.zeros(shape)
        profile=dict(pigmentRetain=1.,normalRetain=1.,normalRadiusMetres=.003,
            fineSigmaMetres=.0001,grainSigmaMetres=.00075,coherenceSigmaMetres=.001,featherMetres=.003)
        for mode,limit in(('luminance-gain','maximumLogGain'),('adjacent-low-frequency-rgb','maximumLinearRgbDelta')):
            region=dict(target=target,reference=reference,protected=protected,sourceLowFrequencySigmaMetres=.0005,
                referenceSigmaMetres=.002,strength=1.,reconstruction=mode,**{limit:.5})
            with self.subTest(mode=mode),self.assertRaisesRegex(ValueError,'target samples without filter support'):
                production.refine_source_skin_material(pixels,ownership,metric,profile,'color',luminance_regions=[region])
        supported=target.copy();supported[8:24,50:62]=1
        region.update(reference=supported)
        unchanged,_=production.refine_source_skin_material(pixels,ownership,metric,profile,'color',luminance_regions=[region])
        np.testing.assert_array_equal(unchanged,pixels)

    def test_nonlocal_rgb_reference_preserves_marks_detail_alpha_and_other_tissue(self):
        production,_=build.source_owner();y,x=np.mgrid[:32,:96]
        pixels=np.full((32,96,4),80,np.uint8);pixels[:,:,3]=y+120
        pixels[:,:8,:3]=[185,137,112]
        pixels[8:24,64:88,:3]=np.where((x[8:24,64:88]%2)[:,:,None],42,28)
        ownership=np.ones((32,96));ownership[:4]=0;metric=np.full(ownership.shape,.001)
        target=np.zeros_like(ownership);target[8:24,64:88]=1
        reference=np.zeros_like(ownership);reference[4:,:8]=1
        protected=np.zeros_like(ownership);protected[12:16,70:74]=1;pixels[protected>0,:3]=[6,5,4]
        profile=dict(pigmentRetain=1.,normalRetain=1.,normalRadiusMetres=.003,
            fineSigmaMetres=.0001,grainSigmaMetres=.00075,coherenceSigmaMetres=.001,featherMetres=.003)
        region=dict(target=target,reference=reference,protected=protected,sourceLowFrequencySigmaMetres=.002,
            strength=1.,maximumLinearRgbDelta=1.,reconstruction='reference-mean-rgb')
        candidate,proof=production.refine_source_skin_material(pixels,ownership,metric,profile,'color',luminance_regions=[region])
        self.assertGreater(float(candidate[18:22,78:84,0].mean()),175.)
        self.assertGreater(float(candidate[18:22,78:84,0].mean()),float(candidate[18:22,78:84,2].mean())+60.)
        self.assertGreater(abs(int(candidate[20,80,0])-int(candidate[20,81,0])),0)
        np.testing.assert_array_equal(candidate[protected>0],pixels[protected>0])
        np.testing.assert_array_equal(candidate[target==0],pixels[target==0])
        np.testing.assert_array_equal(candidate[:,:,3],pixels[:,:,3])
        evidence=proof['luminanceRegions'][0]
        self.assertNotIn('referenceSigmaMetres',evidence);self.assertTrue(evidence['protectedRegionExact'])
        cooler=pixels.copy();cooler[:,:8,:3]=[112,137,185]
        shifted,_=production.refine_source_skin_material(cooler,ownership,metric,profile,'color',luminance_regions=[region])
        self.assertGreater(float(shifted[18:22,78:84,2].mean()),float(shifted[18:22,78:84,0].mean())+60.)
        unchanged,_=production.refine_source_skin_material(pixels,ownership,metric,profile,'color',luminance_regions=[dict(region,strength=0.)])
        np.testing.assert_array_equal(unchanged,pixels)
        bounded,_=production.refine_source_skin_material(pixels,ownership,metric,profile,'color',luminance_regions=[dict(region,maximumLinearRgbDelta=.01)])
        self.assertLess(float(bounded[18:22,78:84,0].mean()),float(candidate[18:22,78:84,0].mean())-80.)

    def test_nonlocal_rgb_reference_is_invariant_to_reference_uv_sampling_density(self):
        production,_=build.source_owner();shape=(16,64)
        pixels=np.full((*shape,4),[32,32,32,223],np.uint8)
        pixels[:,1:2,:3]=[190,120,80];pixels[:,4:5,:3]=[80,120,190]
        ownership=np.ones(shape);metric=np.full(shape,.001)
        reference=np.zeros(shape);reference[:,[1,4]]=1;metric[:,4]=.002
        target=np.zeros(shape);target[4:12,48:60]=1
        profile=dict(pigmentRetain=1.,normalRetain=1.,normalRadiusMetres=.003,
            fineSigmaMetres=.0001,grainSigmaMetres=.00075,coherenceSigmaMetres=.001,featherMetres=.003)
        region=dict(target=target,reference=reference,protected=np.zeros(shape),sourceLowFrequencySigmaMetres=.001,
            strength=1.,maximumLinearRgbDelta=1.,detailRetain=0.,reconstruction='reference-mean-rgb')
        before,first=production.refine_source_skin_material(pixels,ownership,metric,profile,'color',luminance_regions=[region])
        # Four times the texels cover the same physical blue tissue at half
        # the linear sampling distance, leaving its physical contribution exact.
        dense=pixels.copy();dense[:,4:8,:3]=[80,120,190]
        dense_reference=reference.copy();dense_reference[:,4:8]=1
        dense_metric=metric.copy();dense_metric[:,4:8]=.001
        after,second=production.refine_source_skin_material(dense,ownership,dense_metric,profile,'color',
            luminance_regions=[dict(region,reference=dense_reference)])
        np.testing.assert_array_equal(before[target>0],after[target>0])
        np.testing.assert_allclose(first['luminanceRegions'][0]['referenceLinearRgb'],
            second['luminanceRegions'][0]['referenceLinearRgb'],rtol=0,atol=1e-15)
        self.assertAlmostEqual(first['luminanceRegions'][0]['referenceAreaMetresSquared'],
            second['luminanceRegions'][0]['referenceAreaMetresSquared'],places=15)
        self.assertGreater(float(before[target>0,2].mean()),float(before[target>0,0].mean())+40.)

    def test_regional_color_schema_distinguishes_nonlocal_reference_from_local_filter_support(self):
        production,_=build.source_owner()
        region=dict(target={},reference={},protected=None,sourceLowFrequencySigmaMetres=.001,
            strength=.5,maximumLinearRgbDelta=.2,reconstruction='reference-mean-rgb')
        self.assertEqual(production.source_skin_region_mode(region),'reference-mean-rgb')
        for change in(dict(referenceSigmaMetres=.01),dict(strength=True),dict(detailRetain=-.1),
                       dict(sourceLowFrequencySigmaMetres=0.),dict(maximumLinearRgbDelta=float('nan')),
                       dict(reconstruction='unknown'),dict(reconstruction='adjacent-low-frequency-rgb')):
            with self.subTest(change=change),self.assertRaises(ValueError):
                production.source_skin_region_mode(dict(region,**change))
        self.assertEqual(production.source_skin_region_mode(dict(region,reconstruction='adjacent-low-frequency-rgb',
            referenceSigmaMetres=.01)),'adjacent-low-frequency-rgb')
        profile=dict(fineSigmaMetres=.0001,grainSigmaMetres=.00075,coherenceSigmaMetres=.001,featherMetres=.003)
        support=production.source_uniform_support(profile,.001,[region])
        self.assertEqual(support['regionalRadiusPixels'][0]['reference'],0)
        self.assertEqual(support['filterRadiusPixels'],production.source_uniform_support(profile,.001,[])['filterRadiusPixels'])

    def test_paired_skin_color_keeps_texture_detail_and_needs_no_cross_chart_filter(self):
        production, _ = build.source_owner()
        pixels = np.full((12, 18, 4), [98, 85, 89, 213], np.uint8)
        pixels[:, 2:5, :3] = [162, 119, 100]
        pixels[:, 12::2, :3] += 7
        target, reference, protected = (np.zeros(pixels.shape[:2]) for _ in range(3))
        target[:, 12:] = 1
        reference[:, 2:5] = 1
        protected[:2, :] = 1
        ownership = np.ones(target.shape)
        metric = np.full(target.shape, .001)
        profile = dict(pigmentRetain=1., normalRetain=1., normalRadiusMetres=.003, fineSigmaMetres=.0001,
                       grainSigmaMetres=.00075, coherenceSigmaMetres=.001, featherMetres=.003)
        region = dict(target=target, reference=reference, protected=protected, strength=.7,
                      maximumLinearRgbDelta=.06, reconstruction='paired-mean-rgb')
        fields, _ = build.skin_filter_neighbourhoods(production, [], target.shape, {}, profile,
                                                     [region], ownership, metric)
        self.assertEqual(fields, [])
        result, proof = production.refine_source_skin_material(pixels, ownership, metric, profile, 'color',
                                                              luminance_regions=[region])
        np.testing.assert_array_equal(result[protected > 0], pixels[protected > 0])
        np.testing.assert_array_equal(result[target == 0], pixels[target == 0])
        np.testing.assert_array_equal(result[:, :, 3], pixels[:, :, 3])
        self.assertGreater(result[5, 12, 0], result[5, 13, 0])
        self.assertGreater(result[5, 13, 0], pixels[5, 13, 0])
        self.assertLessEqual(max(proof['luminanceRegions'][0]['linearRgbDelta']), .06)

    def test_required_reference_support_includes_both_interpolated_filter_scales(self):
        production,_=build.source_owner();shape=(24,48)
        values=np.full(shape,.4);reference=np.zeros(shape);reference[:,1:3]=1
        target=np.zeros(shape,bool);target[10:14,16:18]=True
        metric=np.full(shape,.001)
        legacy=production.rgb_physical_average(values,reference,metric,.003,evaluation=target)
        with self.assertRaisesRegex(ValueError,'without filter support'):
            production.rgb_physical_average(values,reference,metric,.003,evaluation=target,require_coverage=True)
        self.assertTrue(np.isfinite(legacy).all())
        full=np.ones(shape)
        checked=production.rgb_physical_average(values,full,metric,.003,evaluation=target,require_coverage=True)
        np.testing.assert_allclose(checked[target],.4,rtol=0,atol=1e-14)

    def test_skin_normal_bands_soften_creases_without_erasing_pores(self):
        production,_=build.source_owner()
        x=np.arange(64);crease=np.sin(2*np.pi*x/32);pores=(-1.)**x
        normal=np.full((64,64,4),255,np.uint8);normal[:,:,2]=173;normal[:,:,3]=128
        normal[:,:,1]=np.rint((.18*crease+.04*pores+1)*127.5).astype(np.uint8)
        mask=np.ones((64,64));mask[:4]=0;metric=np.full(mask.shape,.0001)
        profile=dict(pigmentRetain=1.,normalRetain=.1,normalRadiusMetres=.003,
            fineSigmaMetres=.0001,grainSigmaMetres=.00075,coherenceSigmaMetres=.001,featherMetres=.003)
        legacy,_=production.refine_source_skin_material(normal,mask,metric,profile,'normal',normal_encoding='packed-rgorag')
        split,_=production.refine_source_skin_material(normal,mask,metric,
            dict(profile,microNormalRetain=1.,microNormalRadiusMetres=.00015),'normal',normal_encoding='packed-rgorag')
        def amplitude(image,band):return abs(np.mean((image[32,:,1].astype(float)/127.5-1)*band))
        self.assertLess(amplitude(split,crease),amplitude(normal,crease)*.5)
        self.assertGreater(amplitude(split,pores),amplitude(normal,pores)*.7)
        self.assertLess(amplitude(legacy,pores),amplitude(split,pores)*.4)
        np.testing.assert_array_equal(split[:4],normal[:4])
        np.testing.assert_array_equal(split[:,:,[0,2]],normal[:,:,[0,2]])

    def test_skin_macro_normal_band_softens_broad_source_sculpt_while_preserving_pores(self):
        production,_=build.source_owner()
        x=np.arange(256);macro=np.cos(2*np.pi*x/256);pores=(-1.)**x
        normal=np.full((64,256,4),255,np.uint8);normal[:,:,2]=173;normal[:,:,3]=128
        normal[:,:,1]=np.rint((.2*macro+.04*pores+1)*127.5).astype(np.uint8)
        mask=np.ones(normal.shape[:2]);mask[:4]=0;metric=np.full(mask.shape,.00025)
        profile=dict(pigmentRetain=1.,normalRetain=1.,normalRadiusMetres=.0015,
            fineSigmaMetres=.0001,grainSigmaMetres=.00075,coherenceSigmaMetres=.001,featherMetres=.003,
            microNormalRetain=1.,microNormalRadiusMetres=.0003)
        before,_=production.refine_source_skin_material(normal,mask,metric,profile,'normal',normal_encoding='packed-rgorag')
        after,proof=production.refine_source_skin_material(normal,mask,metric,
            dict(profile,macroNormalRetain=.2,macroNormalRadiusMetres=.004),'normal',normal_encoding='packed-rgorag')
        amplitude=lambda image,band:abs(np.mean((image[32,:,1].astype(float)/127.5-1)*band))
        np.testing.assert_array_equal(before,normal)
        self.assertLess(amplitude(after,macro),amplitude(before,macro)*.5)
        self.assertGreater(amplitude(after,pores),amplitude(before,pores)*.85)
        np.testing.assert_array_equal(after[:4],normal[:4])
        np.testing.assert_array_equal(after[:,:,[0,2]],normal[:,:,[0,2]])
        self.assertTrue(proof['sourceCarriersExact']);self.assertTrue(proof['unownedPixelsExact'])
        self.assertEqual(proof['macroNormalRetain'],.2)
        unchanged,_=production.refine_source_skin_material(normal,mask,metric,
            dict(profile,macroNormalRetain=1.,macroNormalRadiusMetres=.004),'normal',normal_encoding='packed-rgorag')
        np.testing.assert_array_equal(unchanged,normal)

    def test_skin_macro_normal_band_requires_a_paired_larger_physical_scale_and_bounded_retention(self):
        production,_=build.source_owner()
        pixels=np.full((8,8,4),128,np.uint8);mask=np.ones((8,8));metric=mask*.001
        profile=dict(pigmentRetain=1.,normalRetain=1.,normalRadiusMetres=.001,
            fineSigmaMetres=.0001,grainSigmaMetres=.00075,coherenceSigmaMetres=.001,featherMetres=.003)
        for change in(dict(macroNormalRetain=.2),dict(macroNormalRadiusMetres=.004),
                dict(macroNormalRetain=-.1,macroNormalRadiusMetres=.004),
                dict(macroNormalRetain=1.1,macroNormalRadiusMetres=.004),
                dict(macroNormalRetain=.2,macroNormalRadiusMetres=.001),
                dict(macroNormalRetain=.2,macroNormalRadiusMetres=float('nan'))):
            with self.subTest(change=change),self.assertRaises(ValueError):
                production.refine_source_skin_material(pixels,mask,metric,dict(profile,**change),'normal',normal_encoding='packed-rgorag')

    def test_skin_directional_pigment_band_uses_its_own_retention_and_preserves_anatomy(self):
        production,_=build.source_owner()
        y,x=np.mgrid[:64,:64]
        pattern=110+30*np.cos(2*np.pi*x/16)
        pixels=np.empty((64,64,4),np.uint8)
        pixels[:,:,:3]=np.rint(pattern[:,:,None]).astype(np.uint8)
        pixels[:,:,3]=(y+120).astype(np.uint8)
        ownership=np.ones((64,64));ownership[:8]=0;metric=np.full(ownership.shape,.0002)
        profile=dict(pigmentRetain=.2,normalRetain=1.,normalRadiusMetres=.003,
            fineSigmaMetres=.0001,grainSigmaMetres=.003,coherenceSigmaMetres=.004,featherMetres=.008)
        legacy,_=production.refine_source_skin_material(pixels,ownership,metric,profile,'color')
        explicit,_=production.refine_source_skin_material(pixels,ownership,metric,dict(profile,pigmentCoherenceRetain=1.),'color')
        softened,proof=production.refine_source_skin_material(pixels,ownership,metric,dict(profile,pigmentCoherenceRetain=.2),'color')
        np.testing.assert_array_equal(legacy,explicit)
        np.testing.assert_array_equal(softened[:8],pixels[:8])
        np.testing.assert_array_equal(softened[:,:,3],pixels[:,:,3])
        self.assertEqual(proof['coherenceRetain'],.2)
        self.assertLess(np.std(softened[16:48,8:56,0]),np.std(legacy[16:48,8:56,0])*.7)
        for invalid in(-.1,1.1):
            with self.assertRaisesRegex(ValueError,'coherence retention'):
                production.refine_source_skin_material(pixels,ownership,metric,dict(profile,pigmentCoherenceRetain=invalid),'color')

    def test_fine_directional_skin_lines_have_independent_retention_from_pore_residuals(self):
        production,_=build.source_owner();y,x=np.mgrid[:64,:64]
        line=np.cos(2*np.pi*x/6);pores=np.random.default_rng(17).normal(0,.012,(64,64))
        pixels=np.empty((64,64,4),np.uint8);pixels[:,:,:3]=np.rint((150+24*line+150*pores)[:,:,None]).astype(np.uint8);pixels[:,:,3]=y+120
        ownership=np.ones((64,64));ownership[:4]=0;metric=np.full(ownership.shape,.0001)
        profile=dict(pigmentRetain=.1,normalRetain=1.,normalRadiusMetres=.003,
            fineSigmaMetres=.00025,grainSigmaMetres=.001,coherenceSigmaMetres=.002,featherMetres=.006,
            pigmentCoherenceRetain=1.)
        legacy,_=production.refine_source_skin_material(pixels,ownership,metric,profile,'color')
        explicit,_=production.refine_source_skin_material(pixels,ownership,metric,dict(profile,pigmentFineCoherenceRetain=1.),'color')
        refined,proof=production.refine_source_skin_material(pixels,ownership,metric,dict(profile,pigmentFineCoherenceRetain=.15),'color')
        np.testing.assert_array_equal(legacy,explicit);np.testing.assert_array_equal(refined[:4],pixels[:4])
        np.testing.assert_array_equal(refined[:,:,3],pixels[:,:,3])
        amplitude=lambda image,band:abs(np.mean((image[12:52,8:56,0].astype(float)-150)*band[12:52,8:56]))
        self.assertLess(amplitude(refined,line),amplitude(legacy,line)*.65)
        self.assertGreater(amplitude(refined,pores),amplitude(legacy,pores)*.6)
        self.assertEqual(proof['fineCoherenceRetain'],.15)
        for invalid in(-.1,1.1):
            with self.assertRaisesRegex(ValueError,'fine coherence retention'):
                production.refine_source_skin_material(pixels,ownership,metric,dict(profile,pigmentFineCoherenceRetain=invalid),'color')

    def test_skin_microdetail_has_independent_retention_and_validated_physical_scale(self):
        production,_=build.source_owner()
        normal=np.full((16,16,4),255,np.uint8);normal[:,:,3]=128
        normal[:,:,1]=np.where(np.arange(16)%2,110,145)
        mask=np.ones((16,16));metric=np.full(mask.shape,.0001)
        profile=dict(pigmentRetain=1.,normalRetain=1.,normalRadiusMetres=.003,
            fineSigmaMetres=.0001,grainSigmaMetres=.00075,coherenceSigmaMetres=.001,featherMetres=.003,
            microNormalRetain=.2,microNormalRadiusMetres=.00015)
        result,_=production.refine_source_skin_material(normal,mask,metric,profile,'normal',normal_encoding='packed-rgorag')
        self.assertLess(np.std(result[:,:,1]),np.std(normal[:,:,1])*.5)
        for invalid in (dict(profile,microNormalRetain=1.1),dict(profile,microNormalRadiusMetres=.004),
                {key:value for key,value in profile.items() if key!='microNormalRetain'}):
            with self.assertRaises(ValueError):
                production.refine_source_skin_material(normal,mask,metric,invalid,'normal',normal_encoding='packed-rgorag')

    def test_skin_directional_detail_accepts_zero_metric_outside_owned_uv(self):
        production,_=build.source_owner();y,x=np.mgrid[:48,:48]
        pixels=np.empty((48,48,4),np.uint8);pixels[:,:,:3]=(145+20*np.cos(2*np.pi*x/6))[:,:,None].astype(np.uint8);pixels[:,:,3]=y+100
        ownership=np.zeros((48,48));ownership[8:40,8:40]=1
        metric=np.full(ownership.shape,.0001);metric[ownership==0]=0
        profile=dict(pigmentRetain=.1,normalRetain=1.,normalRadiusMetres=.003,
            fineSigmaMetres=.00025,grainSigmaMetres=.001,coherenceSigmaMetres=.002,featherMetres=.006,
            pigmentFineCoherenceRetain=.1)
        with np.errstate(divide='raise',invalid='raise',over='raise'):
            refined,_=production.refine_source_skin_material(pixels,ownership,metric,profile,'color')
        np.testing.assert_array_equal(refined[ownership==0],pixels[ownership==0])
        np.testing.assert_array_equal(refined[:,:,3],pixels[:,:,3])
        self.assertTrue(np.any(refined[ownership>0]!=pixels[ownership>0]))

    def test_regional_skin_detail_retention_excludes_protected_pigment_and_other_tissue(self):
        production,_=build.source_owner();y,x=np.mgrid[:64,:64]
        pixels=np.empty((64,64,4),np.uint8);pixels[:,:,:3]=[160,120,95];pixels[:,:,3]=y+80
        target=np.zeros((64,64));target[20:44,18:32]=1
        grain=np.where((x+y)%2,-26,26)
        pixels[target>0,:3]=(pixels[target>0,:3].astype(int)+grain[target>0,None]).astype(np.uint8)
        protected=np.zeros_like(target);protected[16:48,32:36]=1;pixels[protected>0,:3]=[4,2,1]
        reference=np.zeros_like(target);reference[8:56,2:16]=1
        ownership=np.ones_like(target);ownership[:4]=0;metric=np.full(target.shape,.0001);metric[ownership==0]=0
        profile=dict(pigmentRetain=1.,normalRetain=1.,normalRadiusMetres=.003,
            fineSigmaMetres=.0001,grainSigmaMetres=.00075,coherenceSigmaMetres=.001,featherMetres=.003)
        region=dict(target=target,reference=reference,protected=protected,sourceLowFrequencySigmaMetres=.0008,
            referenceSigmaMetres=.0016,strength=1.,reconstruction='adjacent-low-frequency-rgb',maximumLinearRgbDelta=1.)
        legacy,_=production.refine_source_skin_material(pixels,ownership,metric,profile,'color',luminance_regions=[region])
        explicit,_=production.refine_source_skin_material(pixels,ownership,metric,profile,'color',luminance_regions=[dict(region,detailRetain=1.)])
        result,proof=production.refine_source_skin_material(pixels,ownership,metric,profile,'color',luminance_regions=[dict(region,detailRetain=.1)])
        np.testing.assert_array_equal(legacy,explicit)
        np.testing.assert_array_equal(result[target==0],pixels[target==0])
        np.testing.assert_array_equal(result[:,:,3],pixels[:,:,3])
        self.assertLess(np.std(result[24:40,20:30,0]),np.std(legacy[24:40,20:30,0])*.2)
        np.testing.assert_allclose(result[24:40,20:30,:3].mean(axis=(0,1)),[160,120,95],atol=2)
        other=pixels.copy();other[protected>0,:3]=[230,70,190]
        second,_=production.refine_source_skin_material(other,ownership,metric,profile,'color',luminance_regions=[dict(region,detailRetain=.1)])
        np.testing.assert_array_equal(second[target>0],result[target>0])
        self.assertEqual(proof['luminanceRegions'][0]['detailRetain'],.1)
        self.assertTrue(proof['luminanceRegions'][0]['protectedFeaturesExcludedFromSupport'])
        for invalid in(-.1,1.1,float('nan')):
            with self.assertRaisesRegex(ValueError,'detail retention'):
                production.refine_source_skin_material(pixels,ownership,metric,profile,'color',luminance_regions=[dict(region,detailRetain=invalid)])

    def test_regional_detail_retention_composes_after_global_skin_refinement(self):
        production,_=build.source_owner();y,x=np.mgrid[:64,:64]
        pixels=np.empty((64,64,4),np.uint8)
        pixels[:,:,:3]=np.rint((.56+.10*np.sin(x*2*np.pi/10))[:,:,None]*255).astype(np.uint8);pixels[:,:,3]=y+100
        ownership=np.ones((64,64));ownership[:4]=0;ownership[28:32,28:32]=0
        protected=np.zeros((64,64));protected[28:32,28:32]=1;pixels[protected>0,:3]=[4,2,1]
        target=np.zeros((64,64));target[16:48,16:48]=1;metric=np.full((64,64),.0002)
        profile=dict(pigmentRetain=.2,normalRetain=1.,normalRadiusMetres=.005,
            fineSigmaMetres=.0005,grainSigmaMetres=.008,coherenceSigmaMetres=.010,featherMetres=.030,
            pigmentCoherenceRetain=.1,pigmentFineCoherenceRetain=.15)
        region=dict(target=target,reference=np.ones((64,64)),protected=protected,
            sourceLowFrequencySigmaMetres=.002,referenceSigmaMetres=.010,strength=1.,
            reconstruction='adjacent-low-frequency-rgb',maximumLinearRgbDelta=1.)
        baseline,_=production.refine_source_skin_material(pixels,ownership,metric,profile,'color')
        zero,_=production.refine_source_skin_material(pixels,ownership,metric,profile,'color',luminance_regions=[dict(region,detailRetain=0.)])
        legacy,_=production.refine_source_skin_material(pixels,ownership,metric,profile,'color',luminance_regions=[region])
        explicit,_=production.refine_source_skin_material(pixels,ownership,metric,profile,'color',luminance_regions=[dict(region,detailRetain=1.)])
        # A zero residual has no sinusoidal ridge, including when the global
        # pass already removed most of that same ridge.
        self.assertLess(float(np.std(zero[18:26,18:46,0])),1.5)
        np.testing.assert_array_equal(zero[target==0],baseline[target==0])
        np.testing.assert_array_equal(zero[protected==1],pixels[protected==1])
        np.testing.assert_array_equal(zero[:,:,3],pixels[:,:,3])
        np.testing.assert_array_equal(legacy,explicit)

    def test_source_selection_uses_resource_ownership_without_species_names(self):
        editable=dict(name='PreviouslyUnknownFace',internalId='Assets/Characters/EditableCharacters/NewForm/Head.prefab')
        npc=dict(name='AnotherFace',internalId='Assets/Characters/Monsters/NPC/Person.prefab')
        creature=dict(name='HumanLookingMonster',internalId='Assets/Characters/Monsters/Creature/Head.prefab')
        census=dict(assets=[editable,npc,creature])
        self.assertEqual(build.humanoid_sources(census),[editable,npc])
        self.assertEqual(build.humanoid_sources(census,'editable'),[editable])
        self.assertEqual(build.humanoid_sources(census,'npc'),[npc])

    def test_guide_revision_preserves_collar_and_matches_absolute_target_for_all_frames(self):
        production,_=build.source_owner()
        vertices=np.array([[x,y,0.]for y in(-.10,-.04,0.,.03,.06,.09,.12)for x in(-.05,0.,.05)])
        triangles=[]
        for row in range(6):
            for column in range(2):
                a=row*3+column;triangles.extend([a,a+1,a+3,a+1,a+4,a+3])
        count=len(vertices)
        native: dict[str,Any]=dict(name='NativeFace',nativeReadable=True,vertexCount=count,indexFormat=0,
            vertices=vertices.ravel().tolist(),normals=[0.,0.,-1.]*count,tangents=[1.,0.,0.,1.]*count,
            colors=[],bindPoses=np.eye(4).ravel().tolist(),weights=[1.,0.,0.,0.]*count,boneIndices=[0]*count*4,
            subMeshes=[triangles],uv=[],shapes=[dict(index=0,name='Speak',frames=[dict(weight=100.,vertices=[0.,.001,0.]*count,normals=[0.]*count*3,tangents=[0.]*count*3)])])
        native['sourceFingerprint']=build.fingerprint(native)[0]
        skin=dict(rendererToPrefab=np.eye(4).ravel().tolist())
        marks=dict(left=np.array([-.03,0.,.08]),right=np.array([.03,0.,.08]),mouth=np.array([0.,-.03,.02]),
                   nose=np.array([0.,-.03,.05]),head=np.array([0.,0.,.04]))
        baseline=copy.deepcopy(native);points=vertices.copy();points[:,0]+=.0004;baseline['vertices']=points.ravel().tolist()
        baseline['sourceFingerprint']=build.fingerprint(baseline)[0]
        previous=copy.deepcopy(native);points=vertices.copy();points[:,0]+=.0008;previous['vertices']=points.ravel().tolist()
        previous['sourceFingerprint']=build.fingerprint(previous)[0]
        next_target=copy.deepcopy(native);points=vertices.copy();points[:,0]+=.0013;next_target['vertices']=points.ravel().tolist()
        next_target['sourceFingerprint']=build.fingerprint(next_target)[0]
        current,_=production.refine_native_face_contours(native,baseline,skin,marks,refined=previous)
        revised,proof=production.refine_native_face_contours(native,current,skin,marks,refined=next_target,previous_refined=previous)
        expected,_=production.refine_native_face_contours(native,baseline,skin,marks,refined=next_target)
        np.testing.assert_allclose(revised['vertices'],expected['vertices'],rtol=0,atol=5e-9)
        np.testing.assert_array_equal(np.asarray(revised['vertices']).reshape(-1,3)[:3],np.asarray(current['vertices']).reshape(-1,3)[:3])
        self.assertEqual(revised['shapes'],expected['shapes'])
        for key in('uv','weights','boneIndices','bindPoses','subMeshes','vertexCount','indexFormat'):
            self.assertEqual(revised[key],current[key])
        unchanged,_=production.refine_native_face_contours(native,revised,skin,marks,refined=next_target,previous_refined=next_target)
        self.assertEqual(unchanged,revised)
        self.assertTrue(proof['subsequentSourceRefinementsRetained'])
        residual=copy.deepcopy(current);residual['vertices'][-1]+=.0001
        retained,_=production.refine_native_face_contours(native,residual,skin,marks,refined=next_target,previous_refined=previous)
        self.assertAlmostEqual(retained['vertices'][-1]-revised['vertices'][-1],.0001,places=7)
        wrong=copy.deepcopy(previous);wrong['uv']=[dict(channel=0,values=[0.]*count*4)]
        with self.assertRaisesRegex(ValueError,'native correspondence'):
            production.refine_native_face_contours(native,current,skin,marks,refined=next_target,previous_refined=wrong)

    def test_current_material_ownership_uses_each_saved_lod_and_sampler(self):
        import character_resources as resources
        pointer=lambda identifier:dict(m_FileID=0,m_PathID=identifier)
        packets={11:dict(vertexCount=3,weights=[1.,0.,0.,0.]*3,boneIndices=[0,0,0,0]*3,
                         uv=[dict(channel=0,values=[0.,0.,0.,0.,1.,0.,0.,0.,0.,1.,0.,0.])],subMeshes=[[0,1,2]]),
                 12:dict(vertexCount=3,weights=[1.,0.,0.,0.]*3,boneIndices=[1,0,0,0]*3,
                         uv=[dict(channel=0,values=[0.,0.,0.,0.,1.,0.,0.,0.,0.,1.,0.,0.])],subMeshes=[[0,1,2]])}
        objects={}
        for lod in (0,1):
            mesh_id=11+lod;material_id=21+lod;texture_id=31+lod;renderer_id=41+lod
            objects[mesh_id]=SimpleNamespace(path_id=mesh_id,type=SimpleNamespace(name='Mesh'),peek_name=lambda lod=lod:'FaceShape_A_LOD'+str(lod))
            tree=dict(m_Mesh=pointer(mesh_id),m_Materials=[pointer(material_id)])
            objects[renderer_id]=SimpleNamespace(path_id=renderer_id,type=SimpleNamespace(name='SkinnedMeshRenderer'),read_typetree=lambda tree=tree:tree)
            main=dict(m_Texture=pointer(texture_id),m_Scale=dict(x=.5,y=1.),m_Offset=dict(x=.25,y=0.))
            tree=dict(m_SavedProperties=dict(m_TexEnvs=[('_MainTex',main)]))
            objects[material_id]=SimpleNamespace(path_id=material_id,type=SimpleNamespace(name='Material'),read_typetree=lambda tree=tree:tree)
            objects[texture_id]=SimpleNamespace(path_id=texture_id,type=SimpleNamespace(name='Texture2D'))
        file=SimpleNamespace(name='owned-cab',objects=objects)
        templates: list[dict[str,Any]]=[dict(file='owned.bundle',identifier=31+lod,owners=['owned.bundle'],
                        pigmentSources=[dict(nativeTexture=dict(path='native.bundle',sha256='native',identifier=51+lod))])for lod in (0,1)]
        with mock.patch.object(build,'sha',return_value='owned'), \
                mock.patch.object(resources,'load_serialized_bundle',return_value=(None,file)), \
                mock.patch.object(build,'export_current_mesh',side_effect=lambda mesh:packets[mesh.path_id]), \
                mock.patch.object(resources,'skin_bone_paths',return_value=['Bip001/Bip001 Head','Bip001/Neck']):
            result=build.current_head_pigment_geometry(Path('.'),dict(proofs=[dict(file='owned.bundle',newCab='owned-cab',sha256='owned')]),templates)
        import material_fields as fields
        first,last=result['textures'];a=first['pigmentSources'][0];z=last['pigmentSources'][0]
        np.testing.assert_array_equal(fields.regional_bone_fraction(a['packet'],a['skin']['bones']),[1.,1.,1.])
        np.testing.assert_array_equal(fields.regional_bone_fraction(z['packet'],z['skin']['bones']),[0.,0.,0.])
        self.assertEqual(z['skin']['materials'][0]['properties'][0]['offset'],[.25,0.])
        self.assertEqual(last['nativePigmentSources'][0]['identifier'],52)
        self.assertEqual(z['currentSource']['mesh'],12)
        self.assertIn('nativeTexture',templates[1]['pigmentSources'][0])

    def test_serialized_mesh_hash_order_owns_the_skin_slots(self):
        paths=['Root/Neck','Root/Left','Root/Right']
        hashes=[zlib.crc32(paths[i].encode())for i in (2,0,1)]
        self.assertEqual(build.skin_bone_order(paths,hashes),[2,0,1])
        self.assertEqual(build.skin_bone_order(['Prefab/'+path for path in paths],hashes),[2,0,1])
        self.assertEqual(build.skin_bone_order(paths,[]),[0,1,2])
        duplicate=['Root/Neck','Root/Neck']
        self.assertEqual(build.skin_bone_order(duplicate,[zlib.crc32(duplicate[0].encode())]*2),[0,0])
        for invalid in ([True,1,2],[1],['1',2,3],[1,2,2**32]):
            with self.assertRaises(ValueError):build.skin_bone_order(paths,invalid)
        with self.assertRaisesRegex(ValueError,'missing or ambiguous'):
            build.skin_bone_order(paths,[0,1,2])
        with mock.patch('mesh_recipe_codec.zlib.crc32',return_value=7):
            with self.assertRaisesRegex(ValueError,'missing or ambiguous'):
                build.skin_bone_order(paths,[7,7,7])

    def test_bone_hash_repair_retains_added_skin_slots_and_rejects_another_bind_frame(self):
        import character_resources as resources
        pointer=lambda key:dict(m_FileID=0,m_PathID=key)
        paths=['Root/Bip001/Head','Root/Bip001/Neck','Root/Bip001/Left']
        poses=[dict(slot=index)for index in range(4)]
        mesh=dict(m_BindPose=poses[:3],m_BoneNameHashes=[zlib.crc32(paths[i].encode())for i in (2,0,1)])
        native=SimpleNamespace(objects={21:SimpleNamespace(read_typetree=lambda:mesh)})
        target=dict(m_BindPose=copy.deepcopy(poses))
        owner=SimpleNamespace(objects={22:SimpleNamespace(read_typetree=lambda:target)})
        renderer=dict(m_Mesh=pointer(22),m_Bones=[pointer(i)for i in (31,32,33,34)])
        update=dict(renderer=7,nativeMesh=dict(path='native.bundle',sha256='native',pathId=21),sourceBonePaths=paths)
        before=copy.deepcopy(renderer)
        def bone_paths(file,tree,resolve):
            names={31:'Bip001/Head',32:'Bip001/Neck',33:'Bip001/Left',34:'Bip001/Added'}
            return[names[p['m_PathID']]for p in tree['m_Bones']]
        with mock.patch.object(resources,'digest',return_value='native'),mock.patch.object(resources,'load_serialized_bundle',return_value=(None,native)), \
                mock.patch.object(resources,'skin_bone_paths',side_effect=bone_paths):
            repaired,proof=resources.renderer_bone_order(owner,renderer,update)
            self.assertEqual(repaired['m_Bones'],[pointer(i)for i in (33,31,32,34)])
            self.assertEqual(renderer,before);self.assertEqual(target['m_BindPose'],poses)
            self.assertTrue(proof['appendedBoneSlotsExact'])
            target['m_BindPose'][0]=dict(slot=9)
            with self.assertRaisesRegex(ValueError,'bind-pose prefix'):
                resources.renderer_bone_order(owner,renderer,update)


    def test_skin_frames_match_native_slots_without_collapsing_repeated_paths(self):
        _,art=build.source_owner()
        native=np.tile(np.eye(4),(5,1,1));native[0,0,3]=.3;native[1,2,3]=2.
        body=np.tile(np.eye(4),(3,1,1))
        packet=dict(bindPoses=native.transpose(0,2,1).ravel().tolist())
        donor=dict(bindPoses=body.transpose(0,2,1).ravel().tolist())
        paths=['Right','Left','Spine','Right','Left'];donor_paths=['Spine','Left','Right']
        before=copy.deepcopy(packet)
        frame,slots,error=art.skin_palette_frame(packet,donor,paths,donor_paths)
        np.testing.assert_allclose(frame,np.eye(4),rtol=0,atol=1e-12)
        self.assertEqual(slots,{0:2,1:4,2:3});self.assertEqual(error,0.)
        self.assertEqual(packet,before)
        changed=body.copy();changed[0,1,3]=1.;donor['bindPoses']=changed.transpose(0,2,1).ravel().tolist()
        with self.assertRaisesRegex(ValueError,'incompatible or ambiguous'):
            art.skin_palette_frame(packet,donor,paths,donor_paths)
        frame,slots,error=art.skin_palette_frame(packet,donor,paths,donor_paths,common_frame=np.eye(4))
        np.testing.assert_array_equal(frame,np.eye(4))
        self.assertNotIn(0,slots)
        self.assertEqual(slots,{1:4,2:3})
        invalid=np.eye(4);invalid[3,0]=1
        with self.assertRaisesRegex(ValueError,'affine'):
            art.skin_palette_frame(packet,donor,paths,donor_paths,common_frame=invalid)
        duplicate=dict(bindPoses=native[:2].transpose(0,2,1).ravel().tolist())
        with self.assertRaisesRegex(ValueError,'incompatible or ambiguous'):
            art.skin_palette_frame(duplicate,dict(bindPoses=np.eye(4).ravel().tolist()),['Neck','Neck'],['Neck'])

    def test_resource_preloads_follow_each_actual_prefab_owner(self):
        import character_resources as resources
        pointer=lambda key:dict(m_FileID=0,m_PathID=key)
        def obj(kind,tree):return SimpleNamespace(type=SimpleNamespace(name=kind),read_typetree=lambda:tree)
        file=SimpleNamespace(objects={3:obj('GameObject',dict(m_Component=[dict(component=pointer(7))])),
            7:obj('SkinnedMeshRenderer',{}),9:obj('Material',{}),33:obj('Material',{})})
        tree=dict(m_PreloadTable=[pointer(3),pointer(7),pointer(9)],m_Container=[
            ('first',dict(asset=pointer(3),preloadIndex=0,preloadSize=2)),
            ('second',dict(asset=pointer(9),preloadIndex=2,preloadSize=1))],m_Dependencies=['retained.bundle'])
        original=copy.deepcopy(tree);changed=resources.resource_asset_preloads(file,tree,[(7,15),(7,15),(9,16)])
        self.assertEqual(tree,original)
        self.assertEqual(changed['m_PreloadTable'][:3],tree['m_PreloadTable'])
        for (name,entry),wanted in zip(changed['m_Container'],([3,7,15],[9,16])):
            self.assertEqual([p['m_PathID']for p in changed['m_PreloadTable'][entry['preloadIndex']:entry['preloadIndex']+entry['preloadSize']]],wanted)
        self.assertEqual(changed['m_Dependencies'],tree['m_Dependencies'])
        with self.assertRaisesRegex(ValueError,'outside every declared prefab'):
            resources.resource_asset_preloads(file,tree,[(33,15)])

    def test_private_face_mesh_bindings_pass_their_serialized_owner_to_preload_closure(self):
        import ast
        import inspect
        import character_resources as resources
        pointer=lambda value:dict(m_FileID=0,m_PathID=value)
        tree=dict(m_PreloadTable=[pointer(3)],m_Container=[('face',dict(asset=pointer(2),preloadIndex=0,preloadSize=1))])
        original=copy.deepcopy(tree)
        def obj(identifier,kind,value):
            return SimpleNamespace(path_id=identifier,type=SimpleNamespace(name=kind),read_typetree=lambda:copy.deepcopy(value))
        serialized=SimpleNamespace(objects={
            2:obj(2,'GameObject',dict(m_Component=[dict(component=pointer(7))])),
            3:obj(3,'Mesh',{}),7:obj(7,'SkinnedMeshRenderer',dict(m_GameObject=pointer(2),m_Mesh=pointer(3))),
            99:obj(99,'AssetBundle',tree)})
        # Execute the real private-binding boundary in isolation; mesh sculpting
        # is covered separately. A nearby path named "file" is not this owner.
        syntax=ast.parse(inspect.getsource(build.prepare_resource_faces))
        boundaries=[node for node in ast.walk(syntax)if isinstance(node,ast.If)and any(
            isinstance(call,ast.Call)and isinstance(call.func,ast.Attribute)and call.func.attr=='resource_asset_preloads'
            for call in ast.walk(node))]
        branch=min(boundaries,key=lambda node:sum(1 for _ in ast.walk(node)))
        scope=dict(cf=serialized,file=Path('an-unrelated-bundle-path'),private_bindings=[(7,15)],edits={},resources=resources)
        exec(compile(ast.Module(body=[branch],type_ignores=[]),'<actual-private-face-binding-boundary>','exec'),scope)
        result=scope['edits'][99];entry=result['m_Container'][0][1]
        self.assertEqual(result['m_PreloadTable'][entry['preloadIndex']:entry['preloadIndex']+entry['preloadSize']],[pointer(3),pointer(15)])
        self.assertEqual(tree,original);self.assertEqual(set(serialized.objects),{2,3,7,99})

    def test_resource_preloads_use_saved_root_reachability_without_claiming_sibling_or_external_owners(self):
        import character_resources as resources
        pointer=lambda key:dict(m_FileID=0,m_PathID=key)
        def obj(kind,tree):return SimpleNamespace(type=SimpleNamespace(name=kind),read_typetree=lambda:copy.deepcopy(tree))
        file=SimpleNamespace(objects={
            1:obj('GameObject',dict(m_Component=[dict(component=pointer(2))])),
            2:obj('Transform',dict(m_GameObject=pointer(1),m_Children=[pointer(3)])),
            3:obj('Transform',dict(m_GameObject=pointer(4),m_Father=pointer(2))),
            4:obj('GameObject',dict(m_Component=[dict(component=pointer(3)),dict(component=pointer(5))])),
            5:obj('SkinnedMeshRenderer',dict(m_GameObject=pointer(4),m_Materials=[pointer(6)])),
            6:obj('Material',dict(shader=dict(m_FileID=1,m_PathID=8))),
            7:obj('MonoBehaviour',dict(external=dict(m_FileID=2,m_PathID=5))),
            8:obj('Material',{})})
        # Distinct subassets share an address. Neither preload lists the renderer.
        tree=dict(m_PreloadTable=[pointer(7),dict(m_FileID=2,m_PathID=5)],m_Container=[
            ('same.prefab',dict(asset=pointer(1),preloadIndex=0,preloadSize=1)),
            ('same.prefab',dict(asset=pointer(7),preloadIndex=1,preloadSize=1))])
        original=copy.deepcopy(tree)
        changed=resources.resource_asset_preloads(file,tree,[(5,12),(6,13)])
        self.assertEqual(tree,original)
        first,second=[row[1]for row in changed['m_Container']]
        self.assertEqual(changed['m_PreloadTable'][first['preloadIndex']:first['preloadIndex']+first['preloadSize']],
            [pointer(7),pointer(12),pointer(13)])
        self.assertEqual(second,tree['m_Container'][1][1])
        with self.assertRaisesRegex(ValueError,'outside every declared prefab'):
            resources.resource_asset_preloads(file,tree,[(8,12)])
        missing=copy.deepcopy(tree);missing['m_Container'][0][1]['asset']=pointer(99)
        with self.assertRaisesRegex(ValueError,'missing local reference'):
            resources.resource_asset_preloads(file,missing,[(5,12)])
        invalid=copy.deepcopy(tree);invalid['m_Container'][0][1]['preloadSize']=9
        with self.assertRaisesRegex(ValueError,'invalid prefab preload span'):
            resources.resource_asset_preloads(file,invalid,[(5,12)])

    def test_native_read_inputs_do_not_grant_catalog_write_ownership(self):
        with tempfile.TemporaryDirectory()as folder:
            root=Path(folder).resolve();owned=root/'owned';owned.mkdir()
            native=root/'installed-game';native.mkdir();bundle=native/'native-pigment.bundle';bundle.write_bytes(b'native')
            with mock.patch.object(build,'ROOT',owned):
                self.assertEqual(build.checked_source_path(bundle),bundle)
                with self.assertRaisesRegex(ValueError,'outside'):
                    build.checked_catalog_path(native,bundle.name)
                with self.assertRaisesRegex(ValueError,'regular local file'):
                    build.checked_source_path(native)
                native_lstat=Path.lstat
                def lstat(path):
                    return SimpleNamespace(st_file_attributes=0x400,st_mode=native_lstat(path).st_mode)if path==native else native_lstat(path)
                with mock.patch.object(Path,'lstat',lstat),self.assertRaisesRegex(ValueError,'reparse'):
                    build.checked_source_path(bundle)

    def test_skin_chart_aliases_require_all_actual_samplers_to_repeat(self):
        import character_resources as resources
        file=SimpleNamespace();pointer=lambda index:dict(m_FileID=0,m_PathID=index)
        settings={2:dict(m_WrapU=0,m_WrapV=0),3:dict(m_WrapU=0,m_WrapV=0)}
        properties: list[tuple[str,dict[str,Any]]]=[('_MainTex',dict(m_Texture=pointer(2),m_Scale=dict(x=1.,y=1.))),
                    ('_SpecTex',dict(m_Texture=pointer(3),m_Scale=dict(x=1.,y=1.)))]
        material=SimpleNamespace(assets_file=file,read_typetree=lambda:dict(m_SavedProperties=dict(m_TexEnvs=properties)))
        objects={1:material,**{index:SimpleNamespace(read_typetree=lambda index=index:dict(m_TextureSettings=settings[index]))for index in settings}}
        resolve=lambda source,reference:(source,objects[reference['m_PathID']])
        renderer=dict(m_Materials=[pointer(1)])
        self.assertTrue(resources.renderer_repeat_uv(file,renderer,resolve))
        settings[3]['m_WrapV']=1
        self.assertFalse(resources.renderer_repeat_uv(file,renderer,resolve))
        settings[3]['m_WrapV']=0;properties[0][1]['m_Scale']['x']=.5
        self.assertFalse(resources.renderer_repeat_uv(file,renderer,resolve))
        with self.assertRaisesRegex(ValueError,'no actual material sampler'):
            resources.renderer_repeat_uv(file,dict(m_Materials=[]),resolve)

    def test_native_neck_frame_follows_body_without_changing_face_or_expression_channels(self):
        _,art=build.source_owner();head='Bip001/Neck/Bip001 Head';bones=[head,'Bip001/Neck']
        frame=lambda delta:dict(weight=100.,vertices=delta,normals=[0.]*9,tangents=[0.]*9)
        packet=dict(vertexCount=3,vertices=[0.,0.,0.,.1,0.,0.,0.,.1,0.],
            weights=[1.,0.,0.,0.,1.,0.,0.,0.,.5,.5,0.,0.],boneIndices=[1,0,0,0,0,0,0,0,0,1,0,0],
            bindPoses=np.tile(np.eye(4).ravel(),2).tolist(),subMeshes=[[0,1,2]],
            shapes=[dict(index=0,name='NativeBody',frames=[frame([0.,0.,.04]*3)]),
                dict(index=1,name='Expression',frames=[frame([0.,.01,0.]*3)])])
        donor=copy.deepcopy(packet);donor.update(weights=[1.,0.,0.,0.]*3,boneIndices=[0,0,0,0]*3,
            bindPoses=np.eye(4).ravel().tolist(),shapes=[])
        before=copy.deepcopy(packet)
        corrected,proof=art.continue_skin_body_frame(packet,donor,bones,['Bip001/Neck'])
        values=np.asarray(corrected['shapes'][0]['frames'][0]['vertices']).reshape(-1,3)
        np.testing.assert_allclose(values[:,2],[0.,.04,.02])
        self.assertEqual(corrected['vertices'],before['vertices'])
        self.assertEqual(corrected['weights'],before['weights'])
        self.assertEqual(corrected['shapes'][1],before['shapes'][1])
        self.assertEqual(packet,before);self.assertEqual(proof['exclusiveHeadVerticesRetained'],1)
        donor['shapes']=[dict(index=0,name='DonorBody',frames=[frame([0.,0.,.02]*3)])]
        continued,_=art.continue_skin_body_frame(packet,donor,bones,['Bip001/Neck'])
        np.testing.assert_allclose(np.asarray(continued['shapes'][0]['frames'][0]['vertices']).reshape(-1,3)[:,2],[.02,.04,.03])
        missing=copy.deepcopy(packet);missing['shapes']=[]
        added,created=art.continue_skin_body_frame(missing,donor,bones,['Bip001/Neck'])
        self.assertTrue(created['nativeChannelCreated']);self.assertEqual(len(added['shapes']),1)
        np.testing.assert_allclose(np.asarray(added['shapes'][0]['frames'][0]['vertices']).reshape(-1,3)[:,2],[.02,0.,.01])

    def test_unequal_native_cuts_share_donor_vertices_and_every_physical_frame(self):
        _,art=build.source_owner()
        def cylinder(count,levels,bones) -> dict[str,Any]:
            angles=np.arange(count)*2*np.pi/count
            radial=np.c_[np.cos(angles),np.sin(angles),np.zeros(count)]
            positions=np.concatenate([radial+[0.,0.,height]for height in levels]).astype(np.float32)
            triangles=[]
            for row in range(len(levels)-1):
                for column in range(count):
                    a=row*count+column;z=row*count+(column+1)%count
                    triangles.extend([a,z,a+count,z,z+count,a+count])
            normals=np.tile(radial,(len(levels),1)).astype(np.float32)
            tangents=np.c_[-normals[:,1],normals[:,0],np.zeros(len(positions)),np.ones(len(positions))]
            uv=np.concatenate([np.c_[np.arange(count)/count,np.full(count,(height+1)/2)]for height in levels])
            return dict(vertexCount=len(positions),vertices=positions.ravel().tolist(),normals=normals.ravel().tolist(),
                tangents=tangents.ravel().tolist(),weights=[1.,0.,0.,0.]*len(positions),boneIndices=[bones-1,0,0,0]*len(positions),
                bindPoses=np.tile(np.eye(4).ravel(),bones).tolist(),bounds=[0.,0.,0.,2.,2.,2.],subMeshes=[triangles],
                uv=[dict(channel=0,dimension=2,values=uv.ravel().tolist())],colors=[.3,.4,.5,1.]*len(positions),shapes=[])
        head=cylinder(8,[0.,.2,.4],1);body=cylinder(4,[-.4,-.2,0.],2)
        muscular: dict[str,Any]=dict(index=0,name='Muscular',frames=[dict(weight=100.,
            vertices=(np.asarray(body['normals'])*.05).tolist(),normals=[0.]*len(body['vertices']),tangents=[0.]*len(body['vertices']))])
        body['shapes']=[muscular]
        head['shapes']=[dict(index=0,name='Smile',frames=[dict(weight=100.,vertices=[0.,0.,.01]*head['vertexCount'],
            normals=[0.]*len(head['vertices']),tangents=[0.]*len(head['vertices']))])]
        saved=copy.deepcopy(head)
        result,proof=art.rebuild_skin_boundary(head,body,['Neck'],['Neck','Chest'],repeat_uv=True)
        self.assertEqual(head,saved);self.assertEqual(proof['depth'],1)
        for key,width in [('vertices',3),('normals',3),('tangents',4),('weights',4),('boneIndices',4),('colors',4)]:
            np.testing.assert_array_equal(np.asarray(result[key],np.float32)[:head['vertexCount']*width],np.asarray(head[key],np.float32))
        self.assertEqual(proof['appendedBones'],[dict(donor=1,index=1,path='Chest')])
        aliases=copy.deepcopy(head);poses=np.tile(np.eye(4),(3,1,1));poses[0,0,3]=.3
        aliases['bindPoses']=poses.astype(np.float32).transpose(0,2,1).ravel().tolist();aliases['boneIndices']=[1,0,0,0]*aliases['vertexCount']
        stitched,alias_proof=art.rebuild_skin_boundary(aliases,body,['Neck','Neck','Chest'],['Neck','Chest'],repeat_uv=True)
        self.assertEqual(alias_proof['appendedBones'],[])
        np.testing.assert_array_equal(stitched['bindPoses'],aliases['bindPoses'])
        for index,origin in enumerate(alias_proof['origins'][head['vertexCount']:],start=head['vertexCount']):
            if origin>=head['vertexCount']:self.assertEqual(stitched['boneIndices'][4*index],2)
        target=np.asarray(result['vertices']).reshape(-1,3);donor=np.asarray(body['vertices']).reshape(-1,3)
        morph=next(s for s in result['shapes']if s['name']=='Muscular')['frames'][0]
        deltas=np.asarray(morph['vertices']).reshape(-1,3);body_delta=np.asarray(body['shapes'][0]['frames'][0]['vertices'],np.float32).reshape(-1,3)
        for index,origin in enumerate(proof['origins'][head['vertexCount']:],start=head['vertexCount']):
            if origin<head['vertexCount']:continue
            source=proof['bodyCut'][origin-head['vertexCount']]
            np.testing.assert_array_equal(target[index],donor[source])
            np.testing.assert_array_equal(deltas[index],body_delta[source])
            self.assertEqual(result['boneIndices'][index*4],1)
            # Independent Chest translation is inherited exactly by both parts.
            np.testing.assert_array_equal(target[index]+[.2,0.,0.]+deltas[index],donor[source]+[.2,0.,0.]+body_delta[source])
        displaced=copy.deepcopy(body);displaced['vertices']=(np.asarray(body['vertices']).reshape(-1,3)+[0.,0.,3.]).ravel().tolist()
        with self.assertRaisesRegex(ValueError,'matching native physical cut anchors'):
            art.rebuild_skin_boundary(head,displaced,['Neck'],['Neck','Chest'])
        with self.assertRaisesRegex(ValueError,'matching native physical cut anchors'):
            art.rebuild_skin_boundary(head,displaced,['Neck'],['Neck','Chest'],allow_unshared_cuts=True)
        offset=copy.deepcopy(body);offset['vertices']=(np.asarray(body['vertices']).reshape(-1,3)+[0.,0.,.01]).ravel().tolist()
        with self.assertRaisesRegex(ValueError,'matching native physical cut anchors'):
            art.rebuild_skin_boundary(head,offset,['Neck'],['Neck','Chest'])
        joined,matching=art.rebuild_skin_boundary(head,offset,['Neck'],['Neck','Chest'],allow_unshared_cuts=True)
        self.assertGreater(matching['maximumNativeCutGap'],.009)
        self.assertLess(matching['maximumNativeCutGap'],.3)
        self.assertTrue(art.shared_skin_boundary_pairs(joined,offset,['Neck','Chest'],['Neck','Chest']))
        common_body=copy.deepcopy(body);common_body['boneIndices']=[0,0,0,0]*common_body['vertexCount']
        for receiver,donor,receiver_bones,donor_bones in((head,common_body,['Neck'],['Neck','Chest']),
                (common_body,head,['Neck','Chest'],['Neck'])):
            retargeted,closed=art.retarget_skin_boundary(receiver,donor,receiver_bones,donor_bones)
            bones=receiver_bones+[row['path']for row in closed['appendedBones']]
            import material_fields as fields
            fields.serialized_skin_context(retargeted,donor,bones,donor_bones)
            cut=set(closed['sourceCut']);interior=[i for i in range(receiver['vertexCount'])if i not in cut]
            np.testing.assert_array_equal(np.asarray(retargeted['vertices']).reshape(-1,3)[interior],
                np.asarray(receiver['vertices'],np.float32).reshape(-1,3)[interior])
            self.assertGreaterEqual(retargeted['vertexCount'],receiver['vertexCount'])
            np.testing.assert_array_equal(np.asarray(retargeted['uv'][0]['values'])[:len(receiver['uv'][0]['values'])],
                np.asarray(receiver['uv'][0]['values'],np.float32))

    def test_collar_samples_the_owning_uv_chart_at_a_duplicated_wrap(self):
        _,art=build.source_owner()
        def tube(count,levels):
            angle=np.arange(count)*2*np.pi/count
            radial=np.c_[np.cos(angle),np.sin(angle),np.zeros(count)]
            if count==16:radial[1::2]=(radial[::2]+np.roll(radial[::2],-1,axis=0))*.5
            radial=np.r_[radial,radial[:1]];width=count+1
            positions=np.concatenate([radial+[0.,0.,height]for height in levels]).astype(np.float32)
            normals=np.tile(radial,(len(levels),1));normals/=np.linalg.norm(normals,axis=1)[:,None]
            triangles=[]
            for row in range(len(levels)-1):
                for column in range(count):
                    a=row*width+column;z=a+1
                    triangles.extend([a,z,a+width,z,z+width,a+width])
            coords=np.concatenate([np.c_[np.arange(width)/count,np.full(width,(height+1)/2)]for height in levels])
            return dict(vertexCount=len(positions),vertices=positions.ravel().tolist(),normals=normals.ravel().tolist(),
                tangents=np.c_[-normals[:,1],normals[:,0],np.zeros(len(positions)),np.ones(len(positions))].ravel().tolist(),
                weights=[1.,0.,0.,0.]*len(positions),boneIndices=[0,0,0,0]*len(positions),bindPoses=np.eye(4).ravel().tolist(),
                bounds=[0.,0.,0.,2.,2.,2.],subMeshes=[triangles],uv=[dict(channel=0,dimension=2,values=coords.ravel().tolist())],colors=[],shapes=[])
        head=tube(8,[0.,.2,.4]);body=tube(16,[-.4,-.2,0.])
        result,proof=art.rebuild_skin_boundary(head,body,['Neck'],['Neck'],repeat_uv=True)
        uv=np.asarray(head['uv'][0]['values']).reshape(-1,2)
        source=np.asarray(proof['sourceUvEdges']);fraction=np.asarray(proof['sourceUvFractions'])
        sampled=uv[source[:,0]]*(1-fraction[:,None])+uv[source[:,1]]*fraction[:,None]
        wrap=(source[:,0]%9==7)&(source[:,1]%9==8)
        self.assertTrue(np.any(wrap&(fraction>.4)&(fraction<.6)))
        self.assertTrue(np.all(sampled[wrap,0]>.874))
        self.assertTrue(np.all(np.abs(np.diff(uv[source][:,:,0],axis=1))<.126))
        # All native attribute aliases survive unchanged, including u=0 and u=1.
        np.testing.assert_array_equal(np.asarray(result['uv'][0]['values'])[:len(head['uv'][0]['values'])],np.asarray(head['uv'][0]['values'],np.float32))
        islands=copy.deepcopy(head);native_count=head['vertexCount'];seams=np.arange(3)*9+4
        for key,width in [('vertices',3),('normals',3),('tangents',4),('weights',4),('boneIndices',4)]:
            values=np.asarray(head[key]).reshape(-1,width);islands[key]=np.r_[values,values[seams]].ravel().tolist()
        island_uv=np.r_[uv,uv[seams]];island_uv[np.flatnonzero(np.arange(native_count)%9>=5),1]+=.3;island_uv[native_count:,1]+=.3
        islands['vertexCount']+=3;islands['uv'][0]['values']=island_uv.ravel().tolist()
        triangles=np.asarray(islands['subMeshes'][0]).reshape(-1,3)
        for triangle in triangles:
            if np.any(triangle%9>=5):
                for corner,index in enumerate(triangle):
                    if index%9==4:triangle[corner]=native_count+index//9
        islands['subMeshes']=[triangles.ravel().tolist()]
        stitched,charts=art.rebuild_skin_boundary(islands,body,['Neck'],['Neck'],repeat_uv=False)
        strip_uv=np.asarray(stitched['uv'][0]['values']).reshape(-1,2)[charts['strip']]
        self.assertLess(np.ptp(strip_uv[:,:,1],axis=1).max(),.101)
        np.testing.assert_array_equal(np.asarray(stitched['uv'][0]['values'])[:islands['vertexCount']*2],island_uv.astype(np.float32).ravel())

    def test_dense_collar_expands_to_a_valid_physical_band_below_the_neck(self):
        _,art=build.source_owner()
        def tube(angles,levels,crest=None) -> dict[str,Any]:
            radial=np.c_[np.cos(angles),np.sin(angles),np.zeros(len(angles))]
            positions=np.concatenate([radial+[0.,0.,z]for z in levels]).astype(np.float32)
            if crest is not None:positions[-len(angles):,2]+=crest
            n=len(angles);triangles=[]
            for row in range(len(levels)-1):
                for col in range(n):
                    a=row*n+col;z=row*n+(col+1)%n;triangles.extend([a,z,a+n,z,z+n,a+n])
            normals=np.tile(radial,(len(levels),1)).astype(np.float32);count=len(positions)
            poses=np.tile(np.eye(4),(2,1,1));poses[0,2,3]=-.018;poses[1,2,3]=-.024
            tex=np.concatenate([np.c_[angles/(2*np.pi),np.full(n,(z+1)/2),np.zeros((n,2))]for z in levels])
            return dict(vertexCount=count,vertices=positions.ravel().tolist(),normals=normals.ravel().tolist(),
                tangents=np.c_[-normals[:,1],normals[:,0],np.zeros(count),np.ones(count)].ravel().tolist(),
                weights=[1.,0.,0.,0.]*count,boneIndices=[0,0,0,0]*count,bindPoses=poses.transpose(0,2,1).ravel().tolist(),
                bounds=[0.,0.,0.,2.,2.,2.],subMeshes=[triangles],uv=[dict(channel=0,dimension=2,values=tex.ravel().tolist())],colors=[],shapes=[])
        head=tube(np.arange(8)*np.pi/4,np.arange(12)*.002)
        source=np.asarray(head['vertices']).reshape(-1,3);source[:8:2,2]=.014;source[8:,:2]*=.95;head['vertices']=source.astype(np.float32).ravel().tolist()
        body=tube(np.arange(8)*np.pi/4,[-.02,.014])
        before=copy.deepcopy(head)
        result,proof=art.rebuild_skin_boundary(head,body,['Bip001 Neck','Bip001 Head'],['Bip001 Neck','Bip001 Head'],repeat_uv=True)
        self.assertGreater(proof['depth'],3);self.assertEqual(head,before)
        np.testing.assert_array_equal(np.asarray(result['vertices'])[:head['vertexCount']*3],np.asarray(head['vertices']))
        removed=np.asarray(head['subMeshes'][0]).reshape(-1,3)[proof['removedTriangles']]
        self.assertLess(np.asarray(head['vertices']).reshape(-1,3)[removed,2].max(),.018)


    def test_neutral_contact_unit_retains_closed_rim_under_nearby_handles(self):
        production,_=build.source_owner()
        positions=np.array([[-.01,0.,0.],[0.,0.,0.],[.01,0.,0.],[0.,.02,0.],[.01,.03,0.]])
        packet=dict(vertices=positions.ravel().tolist(),subMeshes=[[0,1,3,1,2,3,2,4,3]],
            boneIndices=[0,0,0,0]*3+[1,0,0,0]*2,weights=[1.,0.,0.,0.]*5)
        skin=dict(bones=['Root/b_lip_upper_c','Root/b_cheek_l'],materials=[dict(shader='Lux/Human/Skin')])
        offset=np.array([.002,.001,0.])
        controls=[(positions[1],positions[1]+offset,.04),
                  (positions[3],positions[3]+[-.001,.003,0.],.04)]
        reports=[dict(name='Seal',sourceVertex=1),dict(name='Cheek',sourceVertex=3)]
        result=production.interpolate_source_handles(packet,skin,None,positions,controls,reports,
            np.array([1.,1.,1.,1.,0.]),np.eye(4),[dict(handle='Seal',bonePrefixes=['b_lip_'])])
        np.testing.assert_allclose(result[:3]-positions[:3],np.tile(offset,(3,1)),rtol=0,atol=1e-12)
        np.testing.assert_array_equal(result[4],positions[4])
        np.testing.assert_allclose(result[3]-positions[3],[-.001,.003,0.],rtol=0,atol=1e-12)
        self.assertEqual(reports[0]['contactUnitVertices'],3)
        with self.assertRaisesRegex(ValueError,'no owned source tissue'):
            production.interpolate_source_handles(packet,skin,None,positions,controls,reports,
                np.ones(5),np.eye(4),[dict(handle='Seal',bonePrefixes=['missing_'])])

    def test_native_contact_rim_frees_adjacent_skin_with_minor_oral_bone_influence(self):
        production,_=build.source_owner()
        positions=np.array([[x*.01,y*.01,0.]for y in range(3)for x in range(3)])
        triangles=[]
        for y in range(2):
            for x in range(2):
                i=y*3+x;triangles.extend([i,i+1,i+3,i+1,i+4,i+3])
        weights=np.tile([1.,0,0,0],(9,1));indices=np.tile([1,0,0,0],(9,1))
        indices[1]=[0,1,0,0];indices[4]=[0,1,0,0];weights[4]=[.2,.8,0,0]
        packet=dict(vertexCount=9,vertices=positions.ravel().tolist(),subMeshes=[triangles],
            boneIndices=indices.ravel().tolist(),weights=weights.ravel().tolist(),
            bindPoses=np.tile(np.eye(4).ravel(),2).tolist())
        skin=dict(mesh='NativeFace',path='Face',bones=['Root/b_lip_upper_c','Root/b_cheek_l'],materials=[dict(shader='Lux/Human/Skin')])
        seal=np.array([.002,.001,0.]);cheek=np.array([-.001,.003,0.])
        controls=[(positions[1],positions[1]+seal,.04),(positions[4],positions[4]+cheek,.04)]
        ownership=np.ones(9);ownership[8]=0
        outputs=[]
        for mode in('bone-influence','native-rim'):
            reports=[dict(name='Seal',sourceVertex=1),dict(name='Cheek',sourceVertex=4)]
            result=production.interpolate_source_handles(packet,skin,None,positions,controls,reports,ownership,np.eye(4),
                [dict(handle='Seal',bonePrefixes=['b_lip_'],ownership=mode)])
            np.testing.assert_allclose(result[1]-positions[1],seal,rtol=0,atol=1e-12)
            np.testing.assert_array_equal(result[8],positions[8])
            self.assertEqual(reports[0]['contactUnitOwnership'],mode);outputs.append(result)
        np.testing.assert_allclose(outputs[0][4]-positions[4],seal,rtol=0,atol=1e-12)
        np.testing.assert_allclose(outputs[1][4]-positions[4],cheek,rtol=0,atol=1e-12)

    def test_resource_mips_retain_wire_rows_and_reject_truncated_levels(self):
        import character_resources as resources
        raw=bytes(range(20))
        texture=SimpleNamespace(m_TextureFormat=4,m_Width=2,m_Height=2,m_MipCount=2,get_image_data=lambda:raw)
        obj=SimpleNamespace(read=lambda:texture,version=(2019,4,37,1),platform=19)
        images=resources.texture_mip_images(obj)
        self.assertEqual([image.shape for image in images],[(2,2,4),(1,1,4)])
        self.assertEqual(b''.join(image.tobytes()for image in images),raw)
        raw=raw[:-1]
        with self.assertRaisesRegex(ValueError,'Incomplete source texture mip'):resources.texture_mip_images(obj)

    def test_invalid_lower_lod_morph_uses_its_own_native_lod0(self):
        production,_=build.source_owner();identity=np.eye(4).ravel().tolist()
        skin=dict(path='Face_LOD0',rendererToPrefab=identity)
        packet: dict[str,Any]=dict(sourceFingerprint='1'*64,vertexCount=3,vertices=[0.,0.,0.,.01,0.,0.,0.,.01,0.],
            normals=[0.,0.,1.]*3,tangents=[1.,0.,0.,1.]*3,subMeshes=[[0,1,2]],
            shapes=[dict(index=0,name='Muscular',frames=[dict(weight=100.,vertices=[0.,0.,.001]*3,normals=[0.]*9,tangents=[0.]*9)])])
        lower=copy.deepcopy(packet);lower['sourceFingerprint']='2'*64
        lower['shapes'][0]['frames'][0]['vertices'][2]=1e14
        lower_skin=dict(skin,path='Face_LOD1')
        fixed,reports=production.recover_source_lod_frames(lower,lower_skin,packet,skin)
        self.assertEqual(len(reports),1)
        np.testing.assert_allclose(fixed['shapes'][0]['frames'][0]['vertices'],packet['shapes'][0]['frames'][0]['vertices'],rtol=0,atol=1e-9)
        self.assertEqual(lower['shapes'][0]['frames'][0]['vertices'][2],1e14)
        self.assertEqual(fixed['vertices'],lower['vertices'])
        self.assertEqual(fixed['shapes'][0]['index'],0)
        retained,none=production.recover_source_lod_frames(packet,lower_skin,packet,skin)
        self.assertIs(retained,packet);self.assertEqual(none,[])
        with self.assertRaisesRegex(ValueError,'own valid LOD0'):
            production.recover_source_lod_frames(lower,skin,packet,skin)
        fixed['_sourceFrameReference']=copy.deepcopy(fixed['shapes'])
        protected=2;lower['shapes'][0]['frames'][0]['vertices'][8]=.002
        prepared,proof=production.recover_source_lod_frames(lower,lower_skin,fixed,lower_skin,protected_vertices={protected})
        self.assertEqual(len(proof),1)
        self.assertAlmostEqual(prepared['shapes'][0]['frames'][0]['vertices'][2],.001,places=8)
        self.assertAlmostEqual(prepared['shapes'][0]['frames'][0]['vertices'][8],.002,places=8)
        self.assertEqual(proof[0]['preservedPreparedVertices'],2)

    def test_explicit_lod_guides_keep_independent_geometry_and_share_native_frame_owner(self):
        production,_=build.source_owner()
        high=dict(path='Face_LOD0',mesh='Face_LOD0',bones=[],materials=[dict(shader='Lux/Human/Skin')],_faceProfile=dict(operation='source-guide'))
        low=copy.deepcopy(high);low['path']=low['mesh']='Face_LOD1';profile=low['_faceProfile']
        packet=dict(sourceFingerprint='1'*64)
        with mock.patch.object(production,'native_packet',return_value=packet), \
             mock.patch.object(production,'sculpt_surface',return_value=(None,None,np.ones(1),None)), \
             mock.patch.object(production,'facial_material_slots',return_value=[0]), \
             mock.patch.object(production,'material_identity',return_value='same-native-material'):
            production.bind_source_lod_families(dict(skins=[high,low]))
        self.assertEqual(low['_nativeLodDonor'],packet['sourceFingerprint'])
        self.assertIs(low['_faceProfile'],profile)
        self.assertNotIn('_faceLodDonor',low)

    def test_thin_physical_tissue_keeps_contacts_and_local_sculpt(self):
        production,_=build.source_owner()
        reference=np.array([[0.,0.,0.],[.002,0.,0.],[0.,.0001,0.],[.002,0.,0.],[1.,1.,1.]])
        target=reference.copy();target[2,1]=-.000002;target[4]+=[.001,.002,.003]
        packet=dict(subMeshes=[[0,1,2]])
        repaired,report=production.retain_local_surface_areas(packet,reference,target,np.array([0.,1.,1.,1.,1.]))
        self.assertEqual(report['repairedTriangles'],1)
        self.assertLess(report['maximumCorrectionMm'],.025)
        np.testing.assert_array_equal(repaired[0],target[0])
        np.testing.assert_array_equal(repaired[4],target[4])
        np.testing.assert_array_equal(repaired[1],repaired[3])
        with self.assertRaisesRegex(ValueError,'no editable source'):
            production.retain_local_surface_areas(packet,reference,target,np.zeros(5))
        target[2,1]=-.001
        with self.assertRaisesRegex(ValueError,'0.25 mm'):
            production.retain_local_surface_areas(packet,reference,target,np.array([0.,0.,1.,0.,0.]))

    def test_resource_refinement_rejects_lost_source_and_ocular_ownership(self):
        before='1'*64;after='2'*64
        base=dict(file='owned.bundle',sha256=before,allRetainedObjectFieldsExactAfterReferenceRebasing=True)
        source=dict(assembly=dict(path='source-assembly.json',sha256='3'*64),catalog=dict(path='source-catalog.json',sha256='4'*64))
        documents={'source-assembly.json':dict(proofs=[base]),'source-catalog.json':dict(bundles=[dict(file='owned.bundle',sha256=before)])}
        face: dict[str,Any]=dict(kind='source-owned-face',beforeSha256=before,sha256=after,objects=[7],authority=dict(inputs=[]),
            facts=[dict(sourceSkinUvAndFrameIdentityExact=True,nativeOcularChannelsWithinTolerance=True)],
            unrelatedObjectBytesExact=True,savedObjectBytesExact=True,cabAndExternalReferencesUnchanged=True)
        row=dict(base,sha256=after,allRetainedObjectFieldsExactAfterReferenceRebasing=False,appearanceFinishing=[face])
        hashes={pin['path']:pin['sha256']for pin in source.values()}
        with mock.patch.object(build,'checked_source_path'),mock.patch.object(build,'sha',side_effect=lambda path:hashes[str(path)]),mock.patch.object(build,'read',side_effect=lambda path:copy.deepcopy(documents[str(path)])):
            build.validate_prepared_resource(row,dict(preparedSource=source),{})
            face['facts'][0]['nativeOcularChannelsWithinTolerance']=False
            with self.assertRaisesRegex(ValueError,'protected contact'):
                build.validate_prepared_resource(row,dict(preparedSource=source),{})
            face['facts'][0]['nativeOcularChannelsWithinTolerance']=True;face['beforeSha256']='0'*64
            with self.assertRaisesRegex(ValueError,'preceding resource'):
                build.validate_prepared_resource(row,dict(preparedSource=source),{})

            hashes['native-pigment.bundle']='5'*64
            texture: dict[str,Any]=dict(nativePigmentSource=dict(path='native-pigment.bundle',sha256='5'*64),
                alphaExact=True,bc7PartitionsIndicesAndPbitsExact=True,unownedDecodedTexelsExact=True)
            pigment=dict(kind='native-eyebrow-contrast',file='owned.bundle',sha256=after,
                identity=dict(sourceSha256=before,inputs=[]),unrelatedObjectsExact=True,alphaExact=True,textures=[texture])
            row['appearanceFinishing']=[pigment]
            build.validate_prepared_resource(row,dict(preparedSource=source),{})
            texture['unownedDecodedTexelsExact']=False
            with self.assertRaisesRegex(ValueError,'unowned texture'):build.validate_prepared_resource(row,dict(preparedSource=source),{})
            texture['unownedDecodedTexelsExact']=True;texture['bc7PartitionsIndicesAndPbitsExact']=False;texture['unownedRgbaExact']=True
            with self.assertRaisesRegex(ValueError,'unowned texture'):build.validate_prepared_resource(row,dict(preparedSource=source),{})

    def test_retained_producer_uses_exact_archived_bytes_after_source_edits(self):
        before='1'*64;after='2'*64
        base=dict(file='owned.bundle',sha256=before,allRetainedObjectFieldsExactAfterReferenceRebasing=True)
        source=dict(assembly=dict(path='source-assembly.json',sha256='3'*64),catalog=dict(path='source-catalog.json',sha256='4'*64))
        documents={'source-assembly.json':dict(proofs=[base]),'source-catalog.json':dict(bundles=[dict(file='owned.bundle',sha256=before)])}
        producer=dict(path='producer.py',sha256='5'*64);archive=dict(path='retained-producer.py',sha256='5'*64)
        face=dict(kind='source-owned-face',beforeSha256=before,sha256=after,objects=[7],authority=dict(inputs=[producer]),
            facts=[dict(sourceSkinUvAndFrameIdentityExact=True,nativeOcularChannelsWithinTolerance=True)],
            unrelatedObjectBytesExact=True,savedObjectBytesExact=True,cabAndExternalReferencesUnchanged=True)
        row=dict(base,sha256=after,allRetainedObjectFieldsExactAfterReferenceRebasing=False,appearanceFinishing=[face])
        hashes={**{pin['path']:pin['sha256']for pin in source.values()},'producer.py':'6'*64,'retained-producer.py':'5'*64}
        assembly=dict(preparedSource=source,retainedSourceInputs=[dict(original=producer,archive=archive)])
        with mock.patch.object(build,'checked_source_path'),mock.patch.object(build,'sha',side_effect=lambda path:hashes[str(path)]),mock.patch.object(build,'read',side_effect=lambda path:copy.deepcopy(documents[str(path)])):
            with self.assertRaisesRegex(ValueError,'input changed'):build.validate_prepared_resource(row,dict(preparedSource=source),{})
            build.validate_prepared_resource(row,assembly,{})
            hashes['retained-producer.py']='7'*64
            with self.assertRaisesRegex(ValueError,'archive changed'):build.validate_prepared_resource(row,assembly,{})

    def test_archive_ledger_registration_is_scoped_while_consumed_paths_and_changed_declarations_are_rechecked(self):
        before='1'*64;after='2'*64
        base=dict(file='owned.bundle',sha256=before,allRetainedObjectFieldsExactAfterReferenceRebasing=True)
        source=dict(assembly=dict(path='source-assembly.json',sha256='3'*64),catalog=dict(path='source-catalog.json',sha256='4'*64))
        documents={'source-assembly.json':dict(proofs=[base]),'source-catalog.json':dict(bundles=[dict(file='owned.bundle',sha256=before)])}
        producer=dict(path='producer.py',sha256='5'*64);archive=dict(path='retained-producer.py',sha256='5'*64)
        operation=dict(kind='source-owned-face',beforeSha256=before,sha256=after,objects=[7],authority=dict(inputs=[producer]),
            facts=[dict(sourceSkinUvAndFrameIdentityExact=True,nativeOcularChannelsWithinTolerance=True)],
            unrelatedObjectBytesExact=True,savedObjectBytesExact=True,cabAndExternalReferencesUnchanged=True)
        row=dict(base,sha256=after,allRetainedObjectFieldsExactAfterReferenceRebasing=False,appearanceFinishing=[operation])
        assembly=dict(preparedSource=source,retainedSourceInputs=[dict(original=producer,archive=archive)])
        hashes={**{pin['path']:pin['sha256']for pin in source.values()},'producer.py':'6'*64,'retained-producer.py':'5'*64}
        blocked=set();paths=[]
        def checked(path):
            paths.append(str(path))
            if str(path)in blocked:raise ValueError('A consumed input acquired an invalid path.')
            return Path(path)
        with mock.patch.object(build,'checked_source_path',side_effect=checked),\
                mock.patch.object(build,'checked_catalog_path',side_effect=lambda parent,name=None:Path(parent)/name if name else Path(parent))as boundary,\
                mock.patch.object(build,'sha',side_effect=lambda path:hashes[str(path)]),\
                mock.patch.object(build,'read',side_effect=lambda path:copy.deepcopy(documents[str(path)])):
            verified={}
            build.validate_prepared_resource(row,assembly,verified)
            build.validate_prepared_resource(row,copy.deepcopy(assembly),verified)
            self.assertEqual(boundary.call_count,1)
            self.assertEqual(paths.count(archive['path']),2)
            blocked.add(archive['path'])
            with self.assertRaisesRegex(ValueError,'consumed input'):
                build.validate_prepared_resource(row,assembly,verified)
            blocked.clear()
            addition=dict(original=dict(path='next-producer.py',sha256='7'*64),
                archive=dict(path='next-retained-producer.py',sha256='7'*64))
            assembly['retainedSourceInputs'].append(addition);hashes['next-retained-producer.py']='0'*64
            with self.assertRaisesRegex(ValueError,'archive changed'):
                build.validate_prepared_resource(row,assembly,verified)
            hashes['next-retained-producer.py']='7'*64
            build.validate_prepared_resource(row,assembly,verified)
            # A new operation has its own verification state and validates again.
            hashes[archive['path']]='8'*64
            with self.assertRaisesRegex(ValueError,'archive changed'):
                build.validate_prepared_resource(row,assembly,{})

    def test_registered_archive_still_requires_a_regular_consumed_file(self):
        with tempfile.TemporaryDirectory()as folder:
            root=Path(folder).resolve();original=root/'producer.py';archive=root/'producer.py.source';target=root/'saved.bundle'
            original.write_bytes(b'old producer');archive.write_bytes(original.read_bytes());expected=build.sha(original)
            target.write_bytes(b'prepared bytes');original.write_bytes(b'edited producer')
            proof=dict(source=dict(path=str(original),sha256=expected),target=dict(path=str(target),sha256=build.sha(target)),
                allOtherObjectBytesExact=True,resourcePayloadsExact=True,cabAndExternalReferencesUnchanged=True,savedObjectBytesExact=True)
            row=dict(sha256=build.sha(target),declaredPreparedMutations=[dict(kind='owned-cloth-state',proof=proof)])
            assembly=dict(retainedSourceInputs=[dict(original=dict(path=str(original),sha256=expected),
                archive=dict(path=str(archive),sha256=expected))])
            with mock.patch.object(build,'ROOT',root):
                verified={}
                build.validate_prepared_resource(row,assembly,verified)
                build.validate_prepared_resource(row,assembly,verified)
                archive.unlink()
                with self.assertRaisesRegex(ValueError,'regular local file'):
                    build.validate_prepared_resource(row,assembly,verified)

    def test_compiled_skin_rejects_changed_neighbours_and_retained_prefix(self):
        import character_resources as resources
        native: dict[str,Any]=dict(name='Body_LOD0',vertexCount=3,sourceFingerprint='1'*64,vertices=[0.,0.,0.,1.,0.,0.,0.,1.,0.],
            normals=[0.,0.,1.]*3,tangents=[1.,0.,0.,1.]*3,weights=[1.,0.,0.,0.]*3,boneIndices=[0]*12,colors=[],
            bindPoses=np.eye(4).ravel().tolist(),uv=[],shapes=[])
        packet=copy.deepcopy(native);packet.update(vertexCount=4,sourceFingerprint='2'*64)
        for key,extra in [('vertices',[.5,0.,0.]),('normals',[0.,0.,1.]),('tangents',[1.,0.,0.,1.]),('weights',[1.,0.,0.,0.]),('boneIndices',[0]*4)]:packet[key]+=extra
        def mesh(value):return SimpleNamespace(type=SimpleNamespace(name='Mesh'),packet=value,peek_name=lambda:'Body_LOD0')
        neighbour=SimpleNamespace(type=SimpleNamespace(name='Material'),data=b'retained')
        before=SimpleNamespace(name='CAB-source',externals=[],objects={7:mesh(native),8:neighbour})
        after=SimpleNamespace(name='CAB-source',externals=[],objects={7:mesh(packet),8:neighbour})
        compiled=SimpleNamespace(objects={9:mesh(packet)})
        with tempfile.TemporaryDirectory()as temporary:
            paths=[Path(temporary)/(name+'.json.gz')for name in ('native','authored')]
            def save():
                for path,value in zip(paths,(native,packet)):
                    with gzip.open(path,'wt',encoding='utf-8')as stream:json.dump(value,stream)
            save()
            receipt=dict(source=dict(path='source'),target=dict(path='target'),compiledBundle=dict(path='compiled'),changedObjects=[7],
                meshes=[dict(meshPathId=7,name='Body_LOD0',nativeVertices=3,authoredVertices=4,
                    nativePacket=dict(path=str(paths[0])),packet=dict(path=str(paths[1])))])
            with mock.patch.object(resources,'load_serialized_bundle',side_effect=lambda path,**_:(None,{'source':before,'target':after,'compiled':compiled}[path])), \
                 mock.patch.object(resources,'serialized_object_bytes',side_effect=lambda obj:obj.data), \
                 mock.patch.object(build,'export_current_mesh',side_effect=lambda obj:obj.packet):
                build.validate_compiled_skin_surface(receipt)
                after.objects[8]=SimpleNamespace(type=SimpleNamespace(name='Material'),data=b'changed')
                with self.assertRaisesRegex(ValueError,'unrelated object'):build.validate_compiled_skin_surface(receipt)
                after.objects[8]=neighbour;packet['vertices'][0]=.001;save()
                with self.assertRaisesRegex(ValueError,'retained vertex channel'):build.validate_compiled_skin_surface(receipt)

    def test_independent_resources_keep_distinct_guides_for_the_same_native_mesh(self):
        production,art=build.source_owner();identity=np.eye(4).ravel().tolist()
        packet: dict[str,Any]=dict(name='Face_LOD0',nativeReadable=True,vertexCount=3,indexFormat=0,
            vertices=[0.,0.,0.,.01,0.,0.,0.,.01,0.],normals=[0.,0.,1.]*3,tangents=[1.,0.,0.,1.]*3,
            colors=[],bindPoses=identity,weights=[1.,0.,0.,0.]*3,boneIndices=[0]*12,subMeshes=[[0,1,2]],uv=[],shapes=[])
        packet['sourceFingerprint']=build.fingerprint(packet)[0]
        assets=[];profiles=[]
        with tempfile.TemporaryDirectory()as temporary:
            mesh_path=Path(temporary)/'shared-native.json.gz'
            with gzip.open(mesh_path,'wt',encoding='utf-8')as stream:json.dump(packet,stream)
            for i in (1,2):
                skin=dict(path='Face_LOD0',mesh='Face_LOD0',rendererToPrefab=identity,bones=['Root/Bip001 Head'],
                    materials=[dict(shader='Lux/Human/Skin')],sourceFingerprint=packet['sourceFingerprint'],meshFile=str(mesh_path))
                asset=dict(name='Source'+str(i),sourceContentKey=str(i)*64,skins=[skin]);assets.append(asset)
                target=art.points(packet['vertices'],art.CONVERT)+[0.,-.001*i,0.]
                profile,_=production.prepare_source_surface_guide(asset,skin,packet,target,[0,1,2],[0]);profiles.append(profile)
            path=Path(temporary)/'profiles.json';path.write_text(json.dumps(dict(schemaVersion=1,profiles=profiles)),encoding='utf-8')
            with self.assertRaisesRegex(ValueError,'Shared native source'):
                production.load_profiles(dict(assets=assets),path)
            resolved=production.load_profiles(dict(assets=assets),path,resource_owners=True)
            first,second=[a['skins'][0]['_faceProfile']['guideControls'][0]['target']for a in resolved['assets']]
            self.assertNotEqual(first,second)

    def test_source_junction_uses_two_dimensional_texel_depth_and_preserves_winding(self):
        production,_=build.source_owner()
        packet=dict(vertices=[0.,0.,0.,.1,0.,0.,0.,.1,0.],bindPoses=np.eye(4).ravel().tolist(),
            subMeshes=[[0,1,2]],uv=[dict(channel=0,values=[.1,.2,0.,0.,.6,.2,0.,0.,.1,.7,0.,0.])])
        skin=dict(bones=['Rig/Bip001 Head'],materials=[dict(properties=[dict(name='_MainTex',scale=[1.,1.],offset=[0.,0.])])])
        original=copy.deepcopy(packet)
        with mock.patch.object(production.art,'skin_palette_frame',return_value=(np.eye(4),{0:0},None)):
            result=production.source_surface_junction(packet,skin,0,packet,skin,0,(100,200),(100,200))
            reverse=production.source_surface_junction(dict(packet,subMeshes=[[1,0,2]]),skin,0,packet,skin,0,(100,200),(100,200))
        expected=np.array([[[.1+.5*t*(1-depth/50),.2+.5*depth/50]for depth in(.5,1.5,2.5)]for t in(.25,.5,.75)])
        forward=next(row for row in result['pairs']if row['firstEdge']==[0,1,2])
        backward=next(row for row in reverse['pairs']if row['firstEdge']==[1,0,2])
        np.testing.assert_allclose(forward['firstUv'],expected,rtol=0,atol=1e-15)
        np.testing.assert_allclose(backward['firstUv'],expected[::-1],rtol=0,atol=1e-15)
        self.assertEqual(result['maximumContactGapMetres'],0.)
        self.assertEqual(packet,original)

    def test_adaptive_skin_retains_the_coarse_envelope_at_native_bone_transitions(self):
        production,_=build.source_owner()
        points=np.asarray([[.02,.02,.02],[-.02,-.02,.02],[-.02,.02,-.02],[.02,-.02,-.02]],np.float32)
        normals=points/np.linalg.norm(points,axis=1,keepdims=True)
        tangent=np.stack((-normals[:,1],normals[:,0],np.zeros(4)),axis=1);tangent/=np.linalg.norm(tangent,axis=1,keepdims=True)
        packet=dict(name='SkinTransition',nativeReadable=True,vertexCount=4,indexFormat=0,vertices=points.ravel().tolist(),
            normals=normals.ravel().tolist(),tangents=np.column_stack((tangent,np.ones(4))).ravel().tolist(),colors=[],
            weights=[1.,0.,0.,0.,.5,.5,0.,0.,1.,0.,0.,0.,1.,0.,0.,0.],boneIndices=[0,0,0,0,0,1,0,0,0,0,0,0,0,0,0,0],
            bindPoses=np.tile(np.eye(4).ravel(),2).tolist(),subMeshes=[[0,2,1,0,1,3,0,3,2,1,2,3]],
            uv=[dict(channel=0,dimension=2,values=[0.]*16)],shapes=[],bounds=[0.,0.,0.,.04,.04,.04])
        settings=production.extremity_refinement_profile()
        settings.update(protectCutVertices=False,protectedOpenCutRings=0,minimumNormalAngleDegrees=0)
        result,added,proof=production.refine_skin_packet(packet,np.eye(4).ravel().tolist(),0,profile=settings)
        self.assertGreater(len(added),0);self.assertFalse(any(1 in row['edge']for row in added))
        self.assertGreater(proof['rejectedEdges'].get('different-native-skin-influences',0),0)
        for key in('vertices','weights','boneIndices'):self.assertEqual(result[key][:len(packet[key])],packet[key])
        # Even a linear added point leaves the coarse edge when unequal source
        # skin is averaged. A rest-only PN bow limit cannot bound this error.
        p=np.array([-.1,0.,0.,1.]);q=np.array([.1,0.,0.,1.]);first=np.eye(4)
        for angle,translation in((.8,.03),(-1.1,-.02)):
            c,s=np.cos(angle),np.sin(angle);last=np.array([[c,-s,0.,0.],[s,c,0.,translation],[0.,0.,1.,0.],[0.,0.,0.,1.]])
            difference=(first+last)/2@((p+q)/2)-(first@p+last@q)/2
            np.testing.assert_allclose(difference,.25*(first-last)@(q-p),rtol=0,atol=1e-15)
            self.assertGreater(np.linalg.norm(difference[:3]),.02)

    def test_adaptive_skin_preserves_native_raw_influences_and_bounds_each_frame(self):
        production,_=build.source_owner()
        points=np.asarray([[.02,.02,.02],[-.02,-.02,.02],[-.02,.02,-.02],[.02,-.02,-.02]],np.float32)
        normals=points/np.linalg.norm(points,axis=1,keepdims=True)
        tangent=np.stack((-normals[:,1],normals[:,0],np.zeros(4)),axis=1);tangent/=np.linalg.norm(tangent,axis=1,keepdims=True)
        packet=dict(name='ConstantNativeSkin',nativeReadable=True,vertexCount=4,indexFormat=0,vertices=points.ravel().tolist(),
            normals=normals.ravel().tolist(),tangents=np.column_stack((tangent,np.ones(4))).ravel().tolist(),colors=[],
            weights=np.asarray([.4,.3,.2,.1]*4,np.float32).tolist(),boneIndices=[0,1,2,3]*4,bindPoses=np.tile(np.eye(4).ravel(),4).tolist(),
            subMeshes=[[0,2,1,0,1,3,0,3,2,1,2,3]],uv=[dict(channel=0,dimension=2,values=[0.]*16)],shapes=[],bounds=[0.,0.,0.,.04,.04,.04])
        settings=production.extremity_refinement_profile()
        settings.update(protectCutVertices=False,protectedOpenCutRings=0,minimumNormalAngleDegrees=0,
            minimumEdgeMillimetres=[0,0,0],minimumBowMillimetres=.0001,maximumBowMillimetres=.5)
        for has_native_frame in(False,True):
            packet['shapes']=[dict(index=0,name='Muscular',frames=[dict(weight=100.,vertices=(points*3).ravel().tolist(),normals=[0.]*12,tangents=[0.]*12)])]if has_native_frame else[]
            result,added,proof=production.refine_skin_packet(packet,np.eye(4).ravel().tolist(),0,profile=settings)
            self.assertGreater(len(added),0)
            weights=np.asarray(result['weights']).reshape(-1,4);indices=np.asarray(result['boneIndices']).reshape(-1,4)
            for row in added:
                np.testing.assert_array_equal(weights[row['index']],weights[row['edge'][0]])
                np.testing.assert_array_equal(indices[row['index']],indices[row['edge'][0]])
            self.assertEqual(len(result['shapes']),len(packet['shapes']))
            for old,new in zip(packet['shapes'],result['shapes']):
                self.assertEqual((old['name'],old['index']),(new['name'],new['index']))
                for first,last in zip(old['frames'],new['frames']):
                    self.assertEqual(first['weight'],last['weight'])
                    source_pose=points+np.asarray(first['vertices']).reshape(-1,3)
                    target_pose=np.asarray(result['vertices']).reshape(-1,3)+np.asarray(last['vertices']).reshape(-1,3)
                    for row in added:
                        a,b=row['edge'];linear=(source_pose[a]+source_pose[b])/2
                        maximum=min(settings['maximumBowMillimetres']/1000,np.linalg.norm(source_pose[b]-source_pose[a])*settings['maximumBowEdgeFraction'])
                        self.assertLessEqual(np.linalg.norm(target_pose[row['index']]-linear),maximum+1e-8)
                    for key in('vertices','normals','tangents'):self.assertEqual(last[key][:12],first[key])

    def test_adaptive_skin_shares_uv_edge_motion_and_keeps_original_cut_edges(self):
        production,art=build.source_owner()
        points=np.asarray([[-.1,0.,0.],[.1,0.,0.],[0.,0.,.2],[.1,0.,0.],[-.1,0.,0.],[0.,0.,-.02],[.2,0.,.2]],np.float32)
        normals=np.asarray([[.2,.98,0.],[-.2,.98,0.],[0.,.98,.2],[-.2,.98,0.],[.2,.98,0.],[0.,1.,0.],[0.,.98,.2]])
        normals/=np.linalg.norm(normals,axis=1)[:,None]
        tangents=np.asarray([[1.,0.,0.,1.]]*7);tangents[3:6]=[-1.,0.,0.,-1.]
        uv=np.asarray([[0.,0.,0.,0.],[1.,0.,0.,0.],[.5,1.,0.,0.],[.2,.3,0.,0.],[.8,.3,0.,0.],[.4,1.,0.,0.],[1.,1.,0.,0.]])
        packet: dict[str,Any]=dict(name='UvChartSkin',nativeReadable=True,vertexCount=7,indexFormat=0,vertices=points.ravel().tolist(),
            normals=normals.ravel().tolist(),tangents=tangents.ravel().tolist(),colors=[],weights=[.5,.5,0.,0.]*7,
            boneIndices=[0,1,0,0]*7,bindPoses=np.tile(np.eye(4).ravel(),2).tolist(),
            subMeshes=[[0,1,2,3,4,5,1,6,2]],uv=[dict(channel=0,dimension=2,values=uv.ravel().tolist())],
            shapes=[dict(index=0,name='Motion',frames=[dict(weight=100.,vertices=[0.,.001,0.]*7,normals=[0.]*21,tangents=[0.]*21)])],
            bounds=[.05,0.,.09,.3,0.,.22])
        profile=production.extremity_refinement_profile()
        profile.update(protectCutVertices=False,protectedOpenCutRings=0,minimumNormalAngleDegrees=0)
        changed,added,proof=production.refine_skin_packet(packet,np.eye(4).ravel().tolist(),0,profile=profile)
        twins=[row['index']for row in added if set(row['edge'])in({0,1},{3,4})]
        self.assertEqual(len(twins),2)
        p=np.asarray(changed['vertices']).reshape(-1,3);frame=np.asarray(changed['shapes'][0]['frames'][0]['vertices']).reshape(-1,3)
        np.testing.assert_array_equal(p[twins[0]],p[twins[1]])
        np.testing.assert_array_equal(frame[twins[0]],frame[twins[1]])
        np.testing.assert_array_equal(frame[:7],np.asarray(packet['shapes'][0]['frames'][0]['vertices']).reshape(-1,3))
        charts=np.asarray(changed['uv'][0]['values']).reshape(-1,4)
        self.assertGreater(np.linalg.norm(charts[twins[0]]-charts[twins[1]]),.1)
        weights=np.asarray(changed['weights']).reshape(-1,4);bones=np.asarray(changed['boneIndices']).reshape(-1,4)
        for angle in (-.8,.4):
            c=np.cos(angle);s=np.sin(angle);matrices=[np.eye(3),np.array([[c,-s,0],[s,c,0],[0,0,1]])]
            posed=[sum(matrices[bone]@(p[index]+frame[index]) * weight for bone,weight in zip(bones[index],weights[index]))for index in twins]
            np.testing.assert_array_equal(posed[0],posed[1])
        groups=art.weld_groups(p);faces=groups[np.asarray(changed['subMeshes'][0]).reshape(-1,3)]
        edges=np.sort(np.concatenate((faces[:,[0,1]],faces[:,[1,2]],faces[:,[2,0]])),axis=1)
        unique,counts=np.unique(edges,axis=0,return_counts=True)
        source_groups=set(groups[:7]);self.assertTrue(all(set(edge)<=source_groups for edge in unique[counts==1]))
        self.assertTrue(proof['originalBoundaryLoopSamplesAndEdgesExact'])
        held,unused,_=production.refine_skin_packet(packet,np.eye(4).ravel().tolist(),0,profile=profile,vertex_ownership=np.zeros(7))
        self.assertEqual(unused,[])
        for channel in ('vertices','weights','boneIndices','subMeshes','uv','shapes'):self.assertEqual(held[channel],packet[channel])
        packet['shapes'][0]['frames'][0]['vertices'][3*3+1]+=.001
        separate,added,proof=production.refine_skin_packet(packet,np.eye(4).ravel().tolist(),0,profile=profile)
        self.assertGreater(proof['protectedMotionSeamVertices'],0)
        self.assertFalse(any(set(row['edge'])in({0,1},{3,4})for row in added))
        for channel in ('vertices','normals','tangents','weights','boneIndices'):
            self.assertEqual(separate[channel][:len(packet[channel])],packet[channel])
        self.assertEqual(separate['shapes'][0]['frames'][0]['vertices'][:21],packet['shapes'][0]['frames'][0]['vertices'])

    def test_adaptive_skin_keeps_five_bone_transitions_without_dropping_influences(self):
        production,_=build.source_owner()
        points=np.asarray([[.015,.015,.015],[-.015,-.015,.015],[-.015,.015,-.015],[.015,-.015,-.015]],np.float32)
        normals=points/np.linalg.norm(points,axis=1)[:,None]
        tangent=np.stack((-normals[:,1],normals[:,0],np.zeros(4)),axis=1);tangent/=np.linalg.norm(tangent,axis=1)[:,None]
        packet: dict[str,Any]=dict(name='FiveBoneSkinTransition',nativeReadable=True,vertexCount=4,indexFormat=0,
            vertices=points.ravel().tolist(),normals=normals.ravel().tolist(),tangents=np.column_stack((tangent,np.ones(4))).ravel().tolist(),
            colors=[],weights=[1.,0.,0.,0., .25,.25,.25,.25, 1.,0.,0.,0., 1.,0.,0.,0.],
            boneIndices=[0,0,0,0, 1,2,3,4, 0,0,0,0, 0,0,0,0],bindPoses=np.tile(np.eye(4).ravel(),5).tolist(),
            subMeshes=[[0,2,1,0,1,3,0,3,2,1,2,3]],uv=[dict(channel=0,dimension=2,values=[0.]*16)],shapes=[],bounds=[0.,0.,0.,.03,.03,.03])
        profile=production.extremity_refinement_profile()
        profile.update(minimumEdgeMillimetres=[8,16,32],minimumBowMillimetres=.24,maximumBowMillimetres=8.,
            maximumBowEdgeFraction=.35,minimumNormalAngleDegrees=0,protectCutVertices=False,protectedOpenCutRings=0,minimumChildParentNormalCosine=.3)
        changed,added,proof=production.refine_skin_packet(packet,np.eye(4).ravel().tolist(),0,profile=profile)
        self.assertGreater(len(added),0)
        self.assertGreater(proof['rejectedEdges']['more-than-four-merged-native-influences'],0)
        self.assertEqual(changed['weights'][:16],packet['weights'])
        self.assertEqual(changed['boneIndices'][:16],packet['boneIndices'])
        self.assertTrue(proof['originalBoundaryLoopSamplesAndEdgesExact'])
        faces=np.asarray(changed['subMeshes'][0]).reshape(-1,3)
        edges=np.sort(np.concatenate((faces[:,[0,1]],faces[:,[1,2]],faces[:,[2,0]])),axis=1)
        _,counts=np.unique(edges,axis=0,return_counts=True)
        np.testing.assert_array_equal(counts,np.full(len(counts),2))





    def test_shader_proof_rejects_unrelated_validation_and_neighbour_changes(self):
        import character_resources as resources
        source_pin=dict(path='before.bundle',sha256='1'*64)
        target_pin=dict(path='after.bundle',sha256='2'*64)
        compiler_pin=dict(path='compiler.bundle',sha256='3'*64)
        proof=dict(source=source_pin,target=target_pin,compiledSource=compiler_pin,changedObject=7)
        tree=dict(m_ParsedForm=dict(m_Name='Owned/Shader'),m_Dependencies=[],program='validated')
        def obj(kind,data):return SimpleNamespace(type=SimpleNamespace(name=kind),read_typetree=lambda:copy.deepcopy(data),data=data)
        before=SimpleNamespace(name='CAB-owned',objects={7:obj('Shader',dict(tree,program='previous')),8:obj('Material','retained')})
        after=SimpleNamespace(name='CAB-owned',objects={7:obj('Shader',tree),8:obj('Material','retained')})
        compiler=SimpleNamespace(name='CAB-compiler',objects={11:obj('Shader',tree)})
        inputs={pin['path']:pin['sha256']for pin in (source_pin,target_pin,compiler_pin)}
        with mock.patch.object(build,'sha',side_effect=lambda path:inputs[str(path)]), \
             mock.patch.object(resources,'load_serialized_bundle',side_effect=lambda path,**_: (None,{'before.bundle':before,'after.bundle':after,'compiler.bundle':compiler}[str(path)])), \
             mock.patch.object(resources,'serialized_object_bytes',side_effect=lambda value:str(value.data).encode()):
            build.validate_compiled_shader_proof(proof,dict(bundle=compiler_pin))
            with self.assertRaisesRegex(ValueError,'validated compiler output'):
                build.validate_compiled_shader_proof(proof,dict(bundle=target_pin))
            after.objects[8]=obj('Material','changed')
            with self.assertRaisesRegex(ValueError,'another serialized object'):
                build.validate_compiled_shader_proof(proof,dict(bundle=compiler_pin))
            after.objects[8]=obj('Material','retained');after.objects[7]=obj('Shader',dict(tree,program='unvalidated'))
            with self.assertRaisesRegex(ValueError,'validated compiler output'):
                build.validate_compiled_shader_proof(proof,dict(bundle=compiler_pin))

    def test_groom_shading_proof_requires_its_complete_native_channel_contract(self):
        before='1'*64;after='2'*64;file='native_groom.bundle'
        mesh=dict(normalCarryUv=0,normalCarryComponents='zw',nativeVertexValuesExact=True,
            originalMorphsAndSkinningUnchanged=True,nativeUv0XYExact=True,metallicPixelsUseNativeFrame=True)
        proof: dict[str,Any]=dict(status='PASS_SAVED_GROOM_SHADING_ONLY',file=file,sourceSha256=before,sha256=after,
            nativeTextureCoordinatesExact=True,nativeGeometryFittingAndSkinningExact=True,
            unrelatedObjectsExact=True,serializedObjectOwnershipExact=True,groomSupport=[mesh])
        build.validate_groom_shading(proof,file,before,after)
        for field in ('nativeTextureCoordinatesExact','nativeGeometryFittingAndSkinningExact',
                      'unrelatedObjectsExact','serializedObjectOwnershipExact'):
            invalid=copy.deepcopy(proof);invalid[field]=False
            with self.subTest(field=field),self.assertRaises(ValueError):build.validate_groom_shading(invalid,file,before,after)
        for field,value in (('normalCarryUv',4),('normalCarryComponents','xy'),('nativeUv0XYExact',False),
                            ('originalMorphsAndSkinningUnchanged',False)):
            invalid=copy.deepcopy(proof);invalid['groomSupport'][0][field]=value
            with self.subTest(field=field),self.assertRaises(ValueError):build.validate_groom_shading(invalid,file,before,after)
        with self.assertRaises(ValueError):build.validate_groom_shading(proof,file,'3'*64,after)
        with self.assertRaises(ValueError):build.validate_groom_shading(proof,file,before,'3'*64)

    def test_main_calibration_accepts_exact_alpha_reencoding_and_rejects_ownership_drift(self):
        tone=dict(policy='surface-local-v4',preToneAlphaExact=True,c1WeightOneRgbaExact=True,
            unmappedRawRgbaExact=True,finiteRange=True,sourceC1BaselineUsed=True)
        codec=dict(alphaExact=True,unownedBlocksExact=True,partitionsAndIndicesRetained=False,
            reencodedRgbBlocks=1,changedBlocks=1,blocks=4,maximumRgbError=20,rootMeanSquareRgbError=1.)
        texture: dict[str,Any]=dict(preCalibrationAlphaAllMipsExact=True,allMipTexelWeightedRgbRms=1.,
            mips=[dict(width=8,height=8,tone=tone,codec=codec)])
        build.validate_main_calibration(texture,None)
        for field,value in (('alphaExact',False),('unownedBlocksExact',False),('changedBlocks',0),('rootMeanSquareRgbError',3.)):
            invalid=copy.deepcopy(texture);invalid['mips'][0]['codec'][field]=value
            with self.subTest(field=field),self.assertRaises(ValueError):build.validate_main_calibration(invalid,None)
        before=dict(m_TextureFormat=4,m_CompleteImageSize=256,m_StreamData=dict(path='cab.resS',size=256,offset=16))
        after: dict[str,Any]=dict(m_TextureFormat=25,m_CompleteImageSize=64,m_StreamData=dict(path='cab.resS',size=64,offset=272))
        change=dict(before=before,after=after,additionalBytes=64,exactRgbaMipChain=False)
        build.validate_main_calibration(texture,change)
        after['m_StreamData']['path']='other.resS'
        with self.assertRaises(ValueError):build.validate_main_calibration(texture,change)

    def test_surface_anchor_respects_front_and_side_tissue_ownership(self):
        production, _ = build.source_owner()
        positions = np.asarray([[0., 0., 0.], [.001, 0., 0.], [.002, 0., 0.]])
        normals = np.asarray([[0., 1., 0.], [0., -1., 0.], [1., 0., 0.]])
        ownership = np.ones(3)
        candidates = np.ones(3, dtype=bool)
        self.assertEqual(production.anatomy_surface_anchor(positions, normals, ownership, positions[0], candidates), 1)
        self.assertEqual(production.anatomy_surface_anchor(positions, normals, ownership, positions[0], candidates,
            outward_direction=(1., 0., 0.)), 2)
        with self.assertRaisesRegex(ValueError, 'finite nonzero vector'):
            production.anatomy_surface_anchor(positions, normals, ownership, positions[0], candidates,
                outward_direction=(0., 0., 0.))

    def test_player_and_npc_generation_require_guides_before_creating_outputs(self):
        for generate in (build.generate_humanoids, build.generate_npcs):
            with self.subTest(entry=generate.__name__), tempfile.TemporaryDirectory() as temporary:
                workspace = Path(temporary)
                errors = io.StringIO()
                with mock.patch.object(build, 'HERE', workspace), mock.patch.object(build.sys, 'argv', ['build_models.py']), contextlib.redirect_stderr(errors):
                    with self.assertRaises(SystemExit) as error:
                        generate()
                self.assertEqual(error.exception.code, 2)
                self.assertIn('--profiles', errors.getvalue())
                self.assertEqual(list(workspace.iterdir()), [])

    def test_missing_anatomical_guide_stops_before_surface_generation(self):
        production, _ = build.source_owner()
        with mock.patch.object(production, 'sculpt_surface') as sculpt:
            with self.assertRaisesRegex(ValueError, 'explicit native LOD0 anatomy guide'):
                production.refine_source_asset(dict(sourceContentKey='unconfigured-source', skins=[]))
            sculpt.assert_not_called()

    def test_source_root_changes_are_used_by_the_actual_packet_loader(self):
        _, art = build.source_owner()
        with tempfile.TemporaryDirectory() as temporary:
            roots = [Path(temporary) / name for name in ('first', 'second')]
            for index, root in enumerate(roots):
                root.mkdir()
                packet = dict(sourceFingerprint=str(index + 1) * 64, vertexCount=1,
                              vertices=[float(index), 0., 0.], weights=[1., 0., 0., 0.],
                              uv=[dict(values=[0., 0., 0., 0.])])
                with gzip.open(root / 'mesh.json.gz', 'wt', encoding='utf-8') as stream:
                    json.dump(packet, stream)
            for index, root in enumerate(roots):
                art.SOURCE = root
                self.assertEqual(art.load_packet(dict(meshFile='mesh.json.gz'))['vertices'][0], float(index))

    def test_authored_guide_target_is_not_attenuated_by_procedural_face_strength(self):
        production, art = build.source_owner()
        identity = np.eye(4).ravel().tolist()
        packet = dict(sourceFingerprint='1' * 64, vertexCount=3,
                      vertices=[0., 0., 0., .01, 0., 0., 0., .01, 0.],
                      weights=[1., 0., 0., 0.] * 3, boneIndices=[0, 0, 0, 0] * 3,
                      bindPoses=identity, subMeshes=[[0, 1, 2]])
        control = dict(source=[0., 0., 0.], target=[0., 0., .003],
                       radiusMetres=.02, sourceWeight=1.)
        skin = dict(rendererToPrefab=identity, bones=['Root/Bip001 Head'],
                    materials=[dict(shader='Lux/Human/Skin')],
                    _faceProfile=dict(id='AuthoredTarget', operation='source-guide',
                        restLandmarks={}, guideControls=[control], mask=[1., 1., 0.]))
        self.assertAlmostEqual(float(production.face_surface_strength(packet, skin)[0]), .7)
        surface, _, mask, _ = production.sculpt_surface(packet, skin, 'skin')
        np.testing.assert_allclose(surface[0], [0., -.003, 0.], rtol=0, atol=1e-12)
        np.testing.assert_array_equal(mask, [1., 1., 0.])
        np.testing.assert_array_equal(surface[2], art.points(packet['vertices'], art.CONVERT)[2])

    def test_explicit_low_detail_sculpt_is_retained_instead_of_automatic_transfer(self):
        production, art = build.source_owner()
        identity = np.eye(4).ravel().tolist()
        packet = dict(name='LowDetailFace', nativeReadable=True, vertexCount=3, indexFormat=0,
            vertices=[0., 0., 0., .01, 0., 0., 0., .01, 0.], normals=[0., 0., 1.] * 3,
            tangents=[1., 0., 0., 1.] * 3, colors=[], bindPoses=identity,
            weights=[1., 0., 0., 0.] * 3, boneIndices=[0] * 12, subMeshes=[[0, 1, 2]], uv=[], shapes=[])
        packet['sourceFingerprint'] = build.fingerprint(packet)[0]
        skin = dict(path='Face_LOD1', mesh='Face_LOD1', rendererToPrefab=identity,
                    bones=['Root/Bip001 Head'], materials=[dict(shader='Lux/Human/Skin')],sourceFingerprint=packet['sourceFingerprint'])
        asset = dict(name='Fixture', sourceContentKey='1' * 64, skins=[skin])
        original = art.points(packet['vertices'], art.CONVERT)
        target = original + np.asarray([0., -.001, 0.])
        profile, _ = production.prepare_source_surface_guide(asset, skin, packet, target, [0, 1, 2], [0])
        with tempfile.TemporaryDirectory() as temporary:
            source_path = Path(temporary) / 'mesh.json.gz'
            with gzip.open(source_path, 'wt', encoding='utf-8') as stream:
                json.dump(packet, stream)
            skin['meshFile'] = str(source_path)
            profiles_path = Path(temporary) / 'profiles.json'
            profiles_path.write_text(json.dumps(dict(schemaVersion=1, profiles=[profile])), encoding='utf-8')
            bound = production.load_profiles(dict(assets=[asset]), profiles_path)['assets'][0]['skins'][0]
            automatic_donor = (original, np.full((3, 3), .004), np.ones(3))
            authored, _, _ = production.refine_packet(packet, bound, 1, automatic_donor)
        actual = art.points(authored['vertices'], art.CONVERT)
        np.testing.assert_allclose(actual, target, rtol=0, atol=1e-9)

    def test_skinned_proxy_cannot_be_used_as_an_original_rest_source(self):
        production, _ = build.source_owner()
        packet = dict(name='NativeTriangle', nativeReadable=False, vertexCount=3, indexFormat=0,
            vertices=[0., 0., 0., .01, 0., 0., 0., .01, 0.], normals=[0., 0., 1.] * 3,
            tangents=[1., 0., 0., 1.] * 3, colors=[], bindPoses=[], weights=[0.] * 12,
            boneIndices=[0] * 12, subMeshes=[[0, 1, 2]], uv=[], shapes=[])
        packet['sourceFingerprint'] = build.fingerprint(packet)[0]
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / 'mesh.json.gz'
            skin=dict(meshFile=str(path),sourceFingerprint=packet['sourceFingerprint'])
            for mode in (None, 'skinned-bind-proxy', 'serialized-mesh-data'):
                with self.subTest(sourceReadMode=mode):
                    current = dict(packet)
                    if mode is not None:
                        current['sourceReadMode'] = mode
                    with gzip.open(path, 'wt', encoding='utf-8') as stream:
                        json.dump(current, stream)
                    if mode == 'serialized-mesh-data':
                        self.assertEqual(production.native_packet(skin), current)
                    else:
                        with self.assertRaisesRegex(ValueError, 'not a native rest source'):
                            production.native_packet(skin)

    def test_source_content_key_uses_geometry_and_material_content_across_aliases(self):
        import collect_sources
        asset: dict[str, Any] = dict(name='FirstAlias', internalId='First.prefab', skins=[dict(path='Face',
            sourceFingerprint='1' * 64, meshFile='first/path.json.gz', rootBone='Head', bones=['Head'],
            rendererToPrefab=np.eye(4).ravel().tolist(), bonesToPrefab=np.eye(4).ravel().tolist(), materials=[None])])
        signature = collect_sources.source_content_key(asset, lambda path: self.fail('No image should be read.'))
        alias = copy.deepcopy(asset)
        alias.update(name='SecondAlias', internalId='Second.prefab')
        alias['skins'][0]['meshFile'] = 'moved/path.json.gz'
        self.assertEqual(signature, collect_sources.source_content_key(alias, lambda path: ''))
        alias['skins'][0]['sourceFingerprint'] = '2' * 64
        self.assertNotEqual(signature, collect_sources.source_content_key(alias, lambda path: ''))

    def test_declared_eye_and_mouth_attachments_follow_their_guided_surface(self):
        production, _ = build.source_owner()
        for name, shader in (('Eyelashes_LOD0', 'Lux/Eyelashes'),
                             ('EyeOccluder_LOD0', 'Custom/CharacterEye'),
                             ('JawTeeth_LOD0', 'Lux/Human/Teeth')):
            skin: dict[str, Any] = dict(mesh=name, path=name, materials=[dict(shader=shader)], bones=[])
            with self.subTest(mesh=name):
                self.assertEqual(production.role(skin), 'native')
                skin['_faceProfile'] = dict(operation='source-guide-follow')
                self.assertEqual(production.role(skin), 'attached')
                skin['_faceProfile'] = dict(operation='source-guide')
                self.assertEqual(production.role(skin), 'skin')
                skin.pop('_faceProfile')
                self.assertEqual(production.role(skin), 'native')

    def test_closed_eyelid_tissue_with_partial_bone_weights_is_preserved(self):
        production, _ = build.source_owner()
        bind=np.tile(np.eye(4),(3,1,1));bind[1,0,3]=.03;bind[2,0,3]=-.03
        packet=dict(vertexCount=5,vertices=[0.,0.,0., .005,0.,0., 0.,.005,0., 0.,0.,.005, .1,.1,.1],
            subMeshes=[[0,1,2,0,3,1,0,2,3,1,3,2]],bindPoses=bind.transpose(0,2,1).ravel().tolist(),
            weights=[.8,.2,0.,0.]*3+[1.,0.,0.,0.]*2,boneIndices=[0,1,0,0]*5)
        skin=dict(mesh='Face_LOD0',path='Face_LOD0',rendererToPrefab=np.eye(4).ravel().tolist(),
            bones=['Root/Bip001 Head','Root/b_eyelid_upper_L','Root/b_eyelid_upper_R'],
            materials=[dict(shader='Lux/Human/Skin')])
        guard=production.humanoid_face_contact_guard(packet,skin)
        np.testing.assert_array_equal(guard[:3],np.zeros(3))
        self.assertEqual(guard[4],1.)
        self.assertTrue(np.isfinite(guard).all())
        np.testing.assert_array_equal(production.humanoid_face_contact_guard(packet,skin,
            contact_bone_prefixes=('b_lip_',)),np.ones(5))

    def test_neutral_sculpt_is_inverse_skinned_through_its_own_weighted_rig(self):
        production, art = build.source_owner()
        first = np.eye(4); first[0, 3] = 2.
        second = np.array([[0., -1., 0., 0.], [1., 0., 0., 4.],
                           [0., 0., 1., 0.], [0., 0., 0., 1.]])
        renderer = np.eye(4); renderer[:3, 3] = [.3, .2, .1]
        packet = dict(vertexCount=3, bindPoses=np.tile(np.eye(4).T.ravel(), 2).tolist(),
                      boneIndices=[0, 1, 0, 0] * 3,
                      weights=[1., 0., 0., 0., .5, .5, 0., 0., 0., 1., 0., 0.])
        skin = dict(bones=['First', 'Second'], rendererToPrefab=renderer.T.ravel().tolist())
        pose = dict(worldMatrices=dict(First=first.T.ravel().tolist(), Second=second.T.ravel().tolist()),
                    allNativeMorphWeightsZero=True, currentMorphBySource=[])
        neutral = art.points([3.2, 0., 0., 0., 3.1, 0., 0., 4.1, 3.], art.CONVERT)
        expected = art.points([1.2, 0., 0., .1, 2.1, 0., .1, 0., 3.], art.CONVERT @ renderer)
        result = production.reference_pose_surface_to_rest(packet, skin, pose, [0, 1, 2], neutral)
        np.testing.assert_allclose(result, expected, rtol=0, atol=1e-12)
        pose['allNativeMorphWeightsZero'] = False
        with self.assertRaisesRegex(ValueError, 'zero-morph native reference pose'):
            production.reference_pose_surface_to_rest(packet, skin, pose, [0, 1, 2], neutral)

    def test_measured_neutral_requires_its_complete_native_head_and_bone_owners(self):
        production, _ = build.source_owner()
        skin: dict[str, Any] = dict(path='Face_LOD0', mesh='Face_LOD0', vertexCount=3, bones=['Head', 'Jaw'])
        low = dict(skin, path='Face_LOD1', mesh='Face_LOD1', vertexCount=1)
        asset = dict(address='Head.prefab', skins=[skin, low])
        capture: dict[str, Any] = dict(name='Neutral', headSource='Head.prefab', coordinateDomain='runtime-part-instance-metres',
            worldMatrices={name: np.eye(4).T.ravel().tolist() for name in skin['bones']},
            renderers=[dict(renderer='Face_LOD0', vertexCount=3, bonePathsOrdered=['Head', 'Jaw'],
                morphs=[dict(name='Muscular', weight=0.)])])
        pose = production.captured_neutral_pose(asset, capture)
        self.assertTrue(pose['allNativeMorphWeightsZero'])
        pose['worldMatrices']['Head'][0] = 2.
        self.assertEqual(capture['worldMatrices']['Head'][0], 1.)
        cases = []
        other = copy.deepcopy(capture); other['headSource'] = 'Other.prefab'; cases.append(other)
        other = copy.deepcopy(capture); other['renderers'][0]['bonePathsOrdered'].reverse(); cases.append(other)
        other = copy.deepcopy(capture); other['renderers'][0]['vertexCount'] = 4; cases.append(other)
        other = copy.deepcopy(capture); other['renderers'].append(copy.deepcopy(other['renderers'][0])); cases.append(other)
        other = copy.deepcopy(capture); other['renderers'][0]['morphs'][0]['weight'] = 20.; cases.append(other)
        other = copy.deepcopy(capture); del other['worldMatrices']['Jaw']; cases.append(other)
        other = copy.deepcopy(capture); other['worldMatrices']['Head'][3] = 1.; cases.append(other)
        for invalid in cases:
            with self.subTest(capture=invalid), self.assertRaises(ValueError):
                production.captured_neutral_pose(asset, invalid)


class PhysicalFilterInputContracts(unittest.TestCase):
    def native_lattice_fixture(self):
        points=np.array([[-1.,-1.,0.],[1.,-1.,0.],[0.,1.,0.]])
        return SimpleNamespace(points=points,triangles=np.array([[0,1,2]]),
            uv=np.array([[0.,0.],[1.,0.],[.5,1.]]),initial=points[:,:2].copy(),centre=points[:,:2].mean(0),
            sampler=dict(filterMode=1,wrapU=0,wrapV=0,mipLevel=0),shape=(65,65),half=(32,32),spacing=.001)

    def test_streamed_sampler_changes_retain_bounded_native_geometry_without_repeating_inversions(self):
        production,_=build.source_owner()
        points=np.array([[-.02,0.,0.],[0.,0.,0.],[0.,.02,0.],[0.,0.,0.],[.02,0.,.004],[0.,.02,0.]])
        triangles=np.array([[0,1,2],[3,4,5]])
        pair=dict(firstTriangle=0,secondTriangle=1,firstEdge=[1,2,0],secondEdge=[3,5,4])
        graph=production.source_native_closure(triangles,[pair]);initial=points[triangles[0],:2].copy()
        uv=np.array([[.1,.1],[.4,.1],[.4,.7],[.6,.1],[.9,.1],[.6,.7]])
        centre=np.array([-.005,.01]);values=np.column_stack((-.0035+np.arange(4)*.002,np.full(4,.004)))
        budget=dict(production.source_filter_budget(),nativeQueries=16,nativeCacheBytes=65536)
        expected=production.source_native_rays(points,triangles,graph,0,initial,centre,values)
        stats={}
        with mock.patch.object(production,'source_filter_budget',return_value=budget):
            operation=production.source_native_sampler(points,triangles,uv,graph,report=stats)
            with mock.patch.object(np.linalg,'inv',wraps=np.linalg.inv)as inverse:
                first=operation.queries(0,initial,centre,values)
                inverse_count=inverse.call_count
                self.assertGreater(inverse_count,0)
                for index in range(100):
                    sampler=dict(filterMode=1,wrapU=0,wrapV=0,mipLevel=index%5,scale=[1.+index*.001,1.])
                    source=operation(0,initial,centre,.002,(1,4),(0,0),sampler,(16,16))
                    list(source((0,1,0,4)))
                self.assertEqual(inverse.call_count,inverse_count)
                again=operation.queries(0,initial,centre,values)
            for name in('states','triangles','bary','hops'):
                np.testing.assert_array_equal(first[name],expected[name])
                np.testing.assert_array_equal(again[name],expected[name])
            self.assertFalse(first['failures']);self.assertFalse(again['failures'])
            self.assertGreater(stats['cacheEvictions'],0)
            self.assertEqual(stats['geometryCacheEvictions'],0)
            self.assertGreater(stats['frameCacheHits'],0);self.assertGreater(stats['unfoldCacheHits'],0)
            self.assertLessEqual(stats['peakCacheBytes'],budget['nativeCacheBytes'])
            self.assertLessEqual(stats['peakGeometryCacheBytes'],budget['nativeCacheBytes']//4)


    def test_discovery_cold_hit_consumer_changes_and_mip_st_or_frame_misses(self):
        production, _ = build.source_owner()
        fixture = self.native_lattice_fixture()
        stats = {}
        operation = production.source_native_sampler(fixture.points, fixture.triangles, fixture.uv, {}, report=stats)
        args = (0, fixture.initial, fixture.centre, fixture.spacing, fixture.shape, fixture.half, fixture.sampler, (16, 16), {0})
        first, proof = operation.lattice(*args)
        self.assertEqual(set(first), {0})
        self.assertEqual(proof['allOriginalLatticePoints'], 4225)
        first[0][0][:] = 0
        again, proof = operation.lattice(*args)
        np.testing.assert_array_equal(again[0][0], fixture.initial)
        self.assertTrue(proof['cacheHit'])
        self.assertEqual(stats['latticeBuilds'], 1)
        source = operation(*args[:8])
        blocks = list(source((0, 65, 0, 65)))
        self.assertGreater(stats['latticeLookupQueries'], 0)
        self.assertEqual(stats['nativeWalkQueries'], 0)
        native = production.native_source_blocks(fixture.points, fixture.triangles, fixture.uv, {}, 0, fixture.initial, fixture.centre, fixture.spacing, fixture.shape, fixture.half, fixture.sampler, (16, 16))
        with mock.patch.object(production, 'source_filter_budget', return_value=dict(production.source_filter_budget(), nativeCacheBytes=0)):
            expected = list(native((0, 65, 0, 65)))
        np.testing.assert_equal(blocks, expected)
        for sampler, frame in ((dict(fixture.sampler, mipLevel=1), fixture.initial), (dict(fixture.sampler, scale=[0.8, 1.0]), fixture.initial), (fixture.sampler, fixture.initial + 0.001)):
            operation.lattice(0, frame, fixture.centre, fixture.spacing, fixture.shape, fixture.half, sampler, (16, 16), {0})
        self.assertEqual(stats['latticeBuilds'], 4)
        self.assertLessEqual(stats['peakCacheBytes'], production.source_filter_budget()['nativeCacheBytes'])


    def test_lattice_work_falls_back_to_every_original_query_with_exact_native_taps(self):
        production,_=build.source_owner();fixture=self.native_lattice_fixture()
        centre=np.array([0.,0.]);shape=(1,2);half=(0,0);spacing=1.
        budget=dict(production.source_filter_budget(),nativeQueries=1)
        with mock.patch.object(production,'source_filter_budget',return_value=budget):
            entry,layouts,proof=production.source_native_lattice(fixture.points,fixture.triangles,{},0,
                fixture.initial,centre,spacing,shape,half,{0})
            self.assertIsNone(entry);self.assertIsNone(layouts)
            self.assertTrue(proof['cacheUnavailable']);self.assertEqual(proof['allOriginalLatticePoints'],2)
            self.assertIn('query work allowance',proof['reason'])
            stats={};operation=production.source_native_sampler(fixture.points,fixture.triangles,fixture.uv,{},report=stats)
            frames,discovery=operation.lattice(0,fixture.initial,centre,spacing,shape,half,fixture.sampler,(16,16),{0})
            self.assertEqual(set(frames),{0});self.assertTrue(discovery['cacheUnavailable'])
            blocks=list(operation(0,fixture.initial,centre,spacing,shape,half,fixture.sampler,(16,16))((0,1,0,2)))
            native=production.native_source_blocks(fixture.points,fixture.triangles,fixture.uv,{},0,
                fixture.initial,centre,spacing,shape,half,fixture.sampler,(16,16))
            with mock.patch.object(production,'source_filter_budget',return_value=dict(budget,nativeCacheBytes=0)):
                expected=list(native((0,1,0,2)))
        np.testing.assert_equal(blocks,expected)
        self.assertGreater(stats['latticeBudgetFallbacks'],0);self.assertGreater(stats['nativeWalkQueries'],0)

    def test_outer_grid_points_use_native_resolution_and_not_black_or_unowned_fill(self):
        production, _ = build.source_owner()
        fixture = self.native_lattice_fixture()
        stats = {}
        operation = production.source_native_sampler(fixture.points, fixture.triangles, fixture.uv, {}, report=stats)
        operation.lattice(0, fixture.initial, fixture.centre, fixture.spacing, fixture.shape, fixture.half, fixture.sampler, (16, 16), {0})
        shape = (69, 69)
        half = (34, 34)
        source = operation(0, fixture.initial, fixture.centre, fixture.spacing, shape, half, fixture.sampler, (16, 16))
        actual = list(source((0, 69, 0, 69)))
        with mock.patch.object(production, 'source_filter_budget', return_value=dict(production.source_filter_budget(), nativeCacheBytes=0)):
            expected = list(production.native_source_blocks(fixture.points, fixture.triangles, fixture.uv, {}, 0, fixture.initial, fixture.centre, fixture.spacing, shape, half, fixture.sampler, (16, 16))((0, 69, 0, 69)))
        np.testing.assert_equal(actual, expected)
        self.assertEqual(stats['latticeOuterMissQueries'], 69 * 69 - 65 * 65)
        self.assertEqual(stats['nativeWalkQueries'], 69 * 69 - 65 * 65)


    def test_same_operation_discovery_and_filter_keep_resolved_and_unresolved_closures_separate(self):
        production, _ = build.source_owner()
        fixture = self.native_lattice_fixture()
        points = np.array([[-0.02, 0.0, 0.0], [0.0, 0.0, 0.0], [0.0, 0.02, 0.0], [0.0, 0.0, 0.0], [0.02, 0.0, 0.004], [0.0, 0.02, 0.0]])
        triangles = np.array([[0, 1, 2], [3, 4, 5]])
        uv = np.array([[0.0, 0.0], [0.5, 0.0], [0.5, 1.0], [0.5, 0.0], [1.0, 0.0], [0.5, 1.0]])
        pair = dict(firstTriangle=0, secondTriangle=1, firstEdge=[1, 2], secondEdge=[3, 5])
        graph = production.source_native_closure(triangles, [pair])
        initial = points[:3, :2].copy()
        centre = np.array([-0.005031, 0.005079])
        spacing = 0.00051
        missing = (0, frozenset((1, 2)))
        args = (0, initial, centre, spacing, fixture.shape, fixture.half, fixture.sampler, (16, 16))
        indices = np.arange(65 * 65)
        query = np.stack(((indices % 65 - 32) * spacing + centre[0], (indices // 65 - 32) * spacing + centre[1]), 1)
        for blocked_first in (False, True):
            stats = {}
            operation = production.source_native_sampler(points, triangles, uv, graph, report=stats)
            for blocked in (blocked_first, not blocked_first):
                edges = [missing] if blocked else []
                reference = operation.queries(0, initial, centre, query, unresolved_edges=edges)
                layouts, proof = operation.lattice(*args, {0, 1}, unresolved_edges=edges)
                self.assertEqual(proof['unknownQueries'], len(reference['failures']))
                self.assertEqual(proof['unknownQueries'] > 0, blocked)
                self.assertEqual(1 in layouts, not blocked)
                _, again = operation.lattice(*args, {0, 1}, unresolved_edges=iter(edges + edges))
                self.assertTrue(again['cacheHit'])
                supplier = operation(*args, unresolved_edges=edges)
                if blocked:
                    with self.assertRaisesRegex(ValueError, 'unresolved'):
                        list(supplier((0, 65, 0, 65)))
                else:
                    actual = list(supplier((0, 65, 0, 65)))
                    self.assertTrue(all((block['resolved'].all() for block in actual)))
            self.assertEqual(stats['latticeBuilds'], 2)
            stats = {}
            operation = production.source_native_sampler(points, triangles, uv, graph, report=stats)
            for blocked in (blocked_first, not blocked_first):
                supplier = operation(*args, unresolved_edges=[missing] if blocked else [])
                if blocked:
                    with self.assertRaisesRegex(ValueError, 'unresolved'):
                        list(supplier((0, 65, 0, 65)))
                else:
                    self.assertTrue(all((block['resolved'].all() for block in supplier((0, 65, 0, 65)))))
            self.assertEqual(stats['latticeBuilds'], 2)


    def test_unavailable_lattice_fallback_stops_before_accumulating_unbounded_required_layouts(self):
        production, _ = build.source_owner()
        fixture = self.native_lattice_fixture()
        budget = dict(production.source_filter_budget(), workBytes=4096, nativeQueries=1)
        calls = []

        def walked(*args, **kwargs):
            calls.append(len(calls))
            layout = fixture.initial + len(calls) * 0.001
            return dict(failures={}, layouts={0: [layout]})
        with mock.patch.object(production, 'source_filter_budget', return_value=budget), mock.patch.object(production, 'source_native_lattice', return_value=(None, None, dict(cacheUnavailable=True))), mock.patch.object(production, 'source_native_rays', side_effect=walked):
            operation = production.source_native_sampler(fixture.points, fixture.triangles, fixture.uv, {})
            with self.assertRaisesRegex(ValueError, 'Inverse-layout candidates.*bounded metadata'):
                operation.lattice(0, fixture.initial, fixture.centre, fixture.spacing, (1, 20), (0, 0), fixture.sampler, (16, 16), {0})
        self.assertEqual(len(calls), budget['workBytes'] // 8 // fixture.initial.nbytes + 1)
        self.assertLess(len(calls), 20)


    def test_eviction_keeps_only_an_active_generator_lease_then_releases_the_table(self):
        import gc
        import weakref
        production, _ = build.source_owner()
        fixture = self.native_lattice_fixture()
        budget = dict(production.source_filter_budget(), nativeCacheBytes=64000)
        references = []
        builder = production.source_native_lattice

        def captured(*args, **kwargs):
            result = builder(*args, **kwargs)
            if result[0] is not None:
                references.append(weakref.ref(next(iter(result[0]['rows'].values()))))
            return result
        with mock.patch.object(production, 'source_filter_budget', return_value=budget), mock.patch.object(production, 'source_native_lattice', side_effect=captured):
            stats = {}
            operation = production.source_native_sampler(fixture.points, fixture.triangles, fixture.uv, {}, report=stats)
            operation.lattice(0, fixture.initial, fixture.centre, fixture.spacing, fixture.shape, fixture.half, fixture.sampler, (16, 16), {0})
            original = references[0]
            source = operation(0, fixture.initial, fixture.centre, fixture.spacing, fixture.shape, fixture.half, fixture.sampler, (16, 16))
            stream = source((0, 65, 0, 65))
            raw = next(stream)
            for index in range(1, 9):
                operation.lattice(0, fixture.initial, fixture.centre + [index * 1e-05, 0], fixture.spacing, fixture.shape, fixture.half, fixture.sampler, (16, 16), {0})
            gc.collect()
            self.assertIsNotNone(original())
            stream.close()
            for index in range(9, 18):
                operation.lattice(0, fixture.initial, fixture.centre + [index * 1e-05, 0], fixture.spacing, fixture.shape, fixture.half, fixture.sampler, (16, 16), {0})
            gc.collect()
            self.assertIsNone(original())
            self.assertTrue(raw['resolved'].all())
            self.assertGreater(stats['cacheEvictions'], 0)
            self.assertLessEqual(stats['peakCacheBytes'], budget['nativeCacheBytes'])


    def test_native_uniform_replaces_array_unsupported_outputs_only_with_complete_required_coverage(self):
        production,_=build.source_owner();evaluation=np.zeros(self.domain.shape,bool);evaluation[64,[128,320]]=True
        tiny_metric=self.metric*.01
        with self.assertRaisesRegex(ValueError,'prepared image support'):
            production.rgb_physical_average(self.values,self.domain,tiny_metric,.005,evaluation)
        expected=production.rgb_physical_average(self.values,self.domain,self.metric,.005,evaluation,
            source_neighbourhoods=[self.patch])
        actual=production.rgb_physical_average(self.values,self.domain,tiny_metric,.005,evaluation,
            source_neighbourhoods=[self.patch])
        np.testing.assert_array_equal(actual[evaluation],expected[evaluation])
        incomplete=evaluation.copy();incomplete[64,100]=True
        with self.assertRaisesRegex(ValueError,'array-unsupported targets unresolved'):
            production.rgb_physical_average(self.values,self.domain,tiny_metric,.005,incomplete,
                source_neighbourhoods=[self.patch])
        rgba=np.ones(self.domain.shape+(4,));rgba[:,:,:3]=self.values[:,:,None]*[1.,.7,.5]
        settings=dict(fineSigmaMetres=.0005,grainSigmaMetres=.005,coherenceSigmaMetres=.007,
            featherMetres=.02,coherenceRetain=0.,fineCoherenceRetain=.2)
        owner=evaluation.astype(float)
        result,_=production.refine_rgb_structure(rgba,owner,owner,1-owner,tiny_metric,settings,
            source_neighbourhoods=[self.patch])
        np.testing.assert_array_equal(result[~evaluation],rgba[~evaluation])
        np.testing.assert_array_equal(result[:,:,3],rgba[:,:,3])
        with self.assertRaisesRegex(ValueError,'skin feather leaves required targets unresolved'):
            production.refine_rgb_structure(rgba,self.domain,self.domain,1-self.domain,tiny_metric,settings,
                source_neighbourhoods=[self.patch])

    def test_canonical_complete_evaluation_keeps_native_receivers_even_without_a_split_uv_cut(self):
        production,_=build.source_owner()
        packet=dict(name='Face_LOD0',sourceFingerprint='a'*64,vertexCount=3,
            vertices=[0.,0.,0.,.1,0.,0.,0.,.1,0.],normals=[0.,0.,1.]*3,
            weights=[1.,0.,0.,0.]*3,boneIndices=[0]*12,bindPoses=np.eye(4).T.ravel().tolist(),
            subMeshes=[[0,1,2]],uv=[dict(channel=0,dimension=2,values=[0.,0.,0.,0.,1.,0.,0.,0.,0.,1.,0.,0.])],shapes=[])
        skin=dict(bones=['Rig/Bip001 Head'],rendererToPrefab=np.eye(4).T.ravel().tolist(),
            materials=[dict(properties=[dict(name='_MainTex',scale=[1.,1.],offset=[0.,0.])])])
        selected=np.zeros((8,8),bool);selected[5,2]=True
        profile=dict(fineSigmaMetres=.0002,grainSigmaMetres=.0008,coherenceSigmaMetres=.001,
            featherMetres=.003,pigmentFineCoherenceRetain=1.)
        patches,proof=production.prepare_source_neighbourhoods(packet,skin,0,(8,8),dict(filterMode=1,wrapU=1,wrapV=1),
            profile,[],selected,require_complete=True)
        self.assertTrue(patches);self.assertEqual(proof['coveredWitnesses'],proof['requiredWitnesses'])
        self.assertTrue(proof['completeEvaluationRequested']);self.assertEqual(proof['pairs'],0)
        for patch in patches:
            positive=patch['patchWeights']>0;radius=production.source_uniform_support(profile,patch['metresPerTexel'],[])['filterRadiusPixels']
            self.assertTrue(np.all(patch['patchRows'][positive]>=radius));self.assertTrue(np.all(patch['patchCols'][positive]>=radius))

    def test_native_block_supplier_keeps_source_geometry_sampler_and_global_lattice_across_chunks(self):
        production,_=build.source_owner();import material_fields as fields
        points=np.array([[-.02,0.,0.],[0.,0.,0.],[0.,.02,0.],[0.,0.,0.],[.02,0.,.004],[0.,.02,0.]])
        triangles=np.array([[0,1,2],[3,4,5]])
        pair=dict(firstTriangle=0,secondTriangle=1,firstEdge=[1,2,0],secondEdge=[3,5,4])
        graph=production.source_native_closure(triangles,[pair]);initial=points[triangles[0],:2].copy()
        uv=np.array([[.1,.1],[.4,.1],[.4,.7],[.6,.1],[.9,.1],[.6,.7]])
        centre=np.array([-.005,.01]);spacing=.002;shape=(5,7);half=(3,2)
        sampler=dict(filterMode=1,wrapU=0,wrapV=0,anisoLevel=1,scale=[1.2,.8],offset=[-.1,.07])
        inputs=copy.deepcopy((points,triangles,uv,graph,initial,centre,sampler))
        supplier=production.native_source_blocks(points,triangles,uv,graph,0,initial,centre,spacing,shape,half,sampler,(8,16))
        points[:]=999;uv[:]=999;initial[:]=999;centre[:]=999;sampler['scale'][0]=99;graph.clear()
        limits=dict(production.source_filter_budget(),nativeQueries=3)
        blocks=[]
        with mock.patch.object(production,'source_filter_budget',return_value=limits):
            for bounds in((0,5,0,7),(1,4,2,6)):
                actual=list(supplier(bounds));self.assertTrue(all(len(value['rows'])<=3 for value in actual))
                y0,y1,x0,x1=bounds;y,x=np.indices((y1-y0,x1-x0));x+=x0;y+=y0
                queries=np.stack(((x.ravel()-half[0])*spacing+inputs[5][0],(y.ravel()-half[1])*spacing+inputs[5][1]),1)
                walked=production.source_native_rays(inputs[0],inputs[1],inputs[3],0,inputs[4],inputs[5],queries)
                self.assertFalse(walked['failures']);ordinary=walked['states']==1
                self.assertTrue(np.all(np.isin(walked['states'],[1,2])))
                coordinates=np.einsum('ni,nij->nj',walked['bary'][ordinary],inputs[2][inputs[1][walked['triangles'][ordinary]]])
                image=np.arange(8*16*3,dtype=float).reshape(8,16,3)/384
                expected=np.zeros((len(queries),3))
                expected[ordinary]=fields.bilinear_gpu_rows(image,coordinates*inputs[6]['scale']+inputs[6]['offset'])
                rows=np.concatenate([value['rows']for value in actual]);cols=np.concatenate([value['cols']for value in actual])
                weights=np.concatenate([value['weights']for value in actual])
                fetched=np.sum(image[::-1][rows,cols]*weights[:,:,None],1)
                np.testing.assert_allclose(fetched,expected,rtol=0,atol=2*np.finfo(float).eps)
                np.testing.assert_array_equal(np.rint(fetched*255),np.rint(expected*255))
                self.assertEqual([value['firstIndex']for value in actual],list(range(0,len(queries),3)))
                self.assertTrue(all(value['resolved'].all()for value in actual))
                np.testing.assert_array_equal(np.concatenate([value['outside']for value in actual]),~ordinary)
                blocks.append(actual)
        with self.assertRaisesRegex(ValueError,'bounds'):
            list(supplier((0,6,0,7)))

    def test_native_sampler_addresses_all_mips_wrap_modes_and_st_without_overflow(self):
        build.source_owner();import material_fields as fields
        queries=np.array([[-1.25,-.01],[-.5,0],[0.,.5],[.125,.75],[.5,1.],[.99,1.01],[1.25,2.]])
        for shape in((8,16),(4,8),(2,4),(1,2)):
            image=np.arange(np.prod(shape),dtype=float).reshape(shape)
            for mode in(0,1,2):
                for wrap in((0,0),(1,1),(2,2),(3,3),(0,1),(2,3)):
                    scale=np.array([1.2,.75]);offset=np.array([-.125,.25])
                    rows,cols,weights=fields.texture_sampler_taps(queries,shape,mode,wrap,scale=scale,offset=offset)
                    # Fetch independently in GPU V0 orientation, one query/tap at a time.
                    expected=[];uv=np.asarray(queries*scale+offset,np.float32)
                    def address(index,size,kind):
                        if kind==0:return index%size
                        if kind==1:return min(max(index,0),size-1)
                        if kind==2:return size-1-index%size if(index//size)%2 else index%size
                        return min(size-1,index if index>=0 else -index-1)
                    for u,v in uv:
                        x=float(np.float32(u*np.float32(shape[1])-np.float32(.5)))
                        y=float(np.float32(v*np.float32(shape[0])-np.float32(.5)))
                        if mode==0:
                            expected.append(image[address(int(np.floor(y+.5)),shape[0],wrap[1]),address(int(np.floor(x+.5)),shape[1],wrap[0])])
                        else:
                            x0=int(np.floor(x));y0=int(np.floor(y));fx=x-x0;fy=y-y0;value=0.
                            for dx,wx in((0,1-fx),(1,fx)):
                                for dy,wy in((0,1-fy),(1,fy)):
                                    value+=image[address(y0+dy,shape[0],wrap[1]),address(x0+dx,shape[1],wrap[0])]*wx*wy
                            expected.append(value)
                    np.testing.assert_allclose(np.sum(image[::-1][rows,cols]*weights,1),expected,rtol=0,atol=2e-14)
                    np.testing.assert_allclose(weights.sum(1),1.,rtol=0,atol=1e-15)
                    bottom_rows,_,_=fields.texture_sampler_taps(queries,shape,mode,wrap,scale=scale,offset=offset,top_row=False)
                    np.testing.assert_array_equal(bottom_rows,shape[0]-1-rows)
        for mode,wrap in((True,(0,0)),(1,(False,0)),(3,(0,0))):
            with self.assertRaises(ValueError):fields.texture_sampler_taps(queries,(8,16),mode,wrap)
        with self.assertRaisesRegex(ValueError,'address range'):
            fields.texture_sampler_taps(np.array([[1e300,0.]]),(8,16),1,(0,0))

    def setUp(self):
        side=129;self.domain=np.zeros((side,512));self.values=np.zeros_like(self.domain)
        self.domain[:,64:129]=1;self.domain[:,320:385]=1
        self.values[:,64:129]=.5+np.arange(-64,1)[None,:]*.001
        self.values[:,320:385]=.5+np.arange(65)[None,:]*.001
        self.metric=np.full_like(self.domain,.001)
        y,x=np.indices((side,side));physical=x-side//2
        columns=np.where(physical<=0,128+physical,320+physical)
        rows=np.stack((y,y),axis=2);cols=np.stack((columns,np.where(physical==0,320,columns)),axis=2)
        weights=np.zeros_like(rows,dtype=float);weights[:,:,0]=1
        weights[physical==0]=[.5,.5]
        self.patch=dict(rows=rows,cols=cols,weights=weights,metresPerTexel=.001,
            targetRows=np.array([64,64]),targetCols=np.array([128,320]),
            patchRows=np.array([[64],[64]]),patchCols=np.array([[64],[64]]),
            patchWeights=np.ones((2,1)),blend=np.ones(2),resolved=np.ones((side,side),bool),
            outside=np.zeros((side,side),bool),requiredSupport=np.ones((side,side),bool))

    def streamed_patch(self,mutate=None):
        calls=[]
        def blocks(bounds):
            calls.append(bounds);y0,y1,x0,x1=bounds;width=x1-x0;count=(y1-y0)*width
            for first in range(0,count,128):
                indices=np.arange(first,min(count,first+128));y=indices//width+y0;x=indices%width+x0
                block=dict(firstIndex=first,rows=self.patch['rows'][y,x],cols=self.patch['cols'][y,x],
                    weights=self.patch['weights'][y,x],resolved=self.patch['resolved'][y,x],outside=self.patch['outside'][y,x])
                yield mutate(block)if mutate is not None else block
        keys=('metresPerTexel','targetRows','targetCols','patchRows','patchCols','patchWeights','blend')
        return dict({key:self.patch[key]for key in keys},gridShape=list(self.patch['rows'].shape[:2]),sourceBlocks=blocks),calls

    def test_native_source_blocks_filter_the_complete_stage_halo_without_retaining_a_tap_table(self):
        production,_=build.source_owner();stream,calls=self.streamed_patch()
        expected=production.rgb_physical_average(self.values,self.domain,self.metric,.005,source_neighbourhoods=[self.patch])
        actual=production.rgb_physical_average(self.values,self.domain,self.metric,.005,source_neighbourhoods=[stream])
        np.testing.assert_allclose(actual,expected,rtol=0,atol=8e-15)
        self.assertEqual(calls,[(49,80,49,80)])
        rgba=np.ones(self.domain.shape+(4,));rgba[:,:,:3]=self.values[:,:,None]*np.array([1.,.7,.5])
        settings=dict(fineSigmaMetres=.0005,grainSigmaMetres=.005,coherenceSigmaMetres=.007,
            featherMetres=.02,coherenceRetain=0.,fineCoherenceRetain=.2)
        expected,_=production.refine_rgb_structure(rgba,self.domain,self.domain,1-self.domain,self.metric,settings,
            source_neighbourhoods=[self.patch])
        actual,_=production.refine_rgb_structure(rgba,self.domain,self.domain,1-self.domain,self.metric,settings,
            source_neighbourhoods=[stream])
        np.testing.assert_allclose(actual,expected,rtol=0,atol=8e-15)
        np.testing.assert_array_equal(np.rint(actual*255),np.rint(expected*255))
        np.testing.assert_array_equal(actual[:,:,3],rgba[:,:,3])

    def test_streamed_native_halo_rejects_unknown_geometry_or_missing_and_repeated_blocks(self):
        production,_=build.source_owner()
        def unresolved(block):
            block['resolved']=block['resolved'].copy();block['resolved'][0]=False;return block
        def hidden_exterior(block):
            block['outside']=np.ones_like(block['outside']);return block
        def repeated(block):
            block['firstIndex']=0;return block
        for mutation,error in((unresolved,'finite filter support unresolved'),
                               (hidden_exterior,'native boundary classification'),(repeated,'omit, repeat')):
            stream,_=self.streamed_patch(mutation)
            with self.subTest(error=error),self.assertRaisesRegex(ValueError,error):
                production.rgb_physical_average(self.values,self.domain,self.metric,.005,source_neighbourhoods=[stream])
        stream,_=self.streamed_patch();source=stream['sourceBlocks']
        stream['sourceBlocks']=lambda bounds:iter([next(source(bounds))])
        with self.assertRaisesRegex(ValueError,'omit their required lattice'):
            production.rgb_physical_average(self.values,self.domain,self.metric,.005,source_neighbourhoods=[stream])

    def test_streamed_source_work_budget_is_checked_before_reading_native_blocks(self):
        production,_=build.source_owner();stream,calls=self.streamed_patch()
        limits=dict(production.source_filter_budget(),workBytes=1)
        with mock.patch.object(production,'source_filter_budget',return_value=limits),\
                self.assertRaisesRegex(ValueError,'work budget'):
            production.filter_source_neighbourhoods(self.values,self.domain,self.domain>0,[stream],
                lambda values,domain,spacing,selected:values,self.values,support_radius=lambda spacing:15)
        self.assertEqual(calls,[])

    def test_tiled_responses_keep_original_receiver_order_and_reject_mixed_or_repeated_indices(self):
        production,_=build.source_owner();random=np.random.default_rng(490)
        count=31;patch=copy.deepcopy(self.patch)
        patch.update(targetRows=np.full(count,64),targetCols=np.full(count,128),
            patchRows=random.integers(0,129,(count,1)),patchCols=random.integers(0,129,(count,1)),
            patchWeights=np.ones((count,1)),blend=random.uniform(.01,.99,count))
        evaluation=self.domain>0;operation=lambda values,domain,spacing,selected:values
        expected=production.filter_source_neighbourhoods(self.values,self.domain,evaluation,[patch],operation,self.values)
        tiles=[]
        for indices in np.array_split(random.permutation(count),5):
            tile=dict(patch,targetOrder=indices)
            for key in('targetRows','targetCols','patchRows','patchCols','patchWeights','blend'):
                tile[key]=patch[key][indices]
            tiles.append(tile)
        actual=production.filter_source_neighbourhoods(self.values,self.domain,evaluation,tiles,operation,self.values)
        for first,last in zip(expected,actual,strict=True):np.testing.assert_array_equal(first,last)
        with self.assertRaisesRegex(ValueError,'mix original receiver'):
            production.filter_source_neighbourhoods(self.values,self.domain,evaluation,[tiles[0],patch],operation,self.values)
        with self.assertRaisesRegex(ValueError,'repeat an original'):
            production.filter_source_neighbourhoods(self.values,self.domain,evaluation,[tiles[0],tiles[0]],operation,self.values)
        invalid=dict(tiles[0],targetOrder=np.full_like(tiles[0]['targetOrder'],-1))
        with self.assertRaisesRegex(ValueError,'invalid original'):
            production.filter_source_neighbourhoods(self.values,self.domain,np.zeros_like(evaluation),[invalid],operation,self.values)

    def test_source_sampler_row_chunks_keep_scalar_rgb_and_rgba_fields_exact(self):
        production,_=build.source_owner();random=np.random.default_rng(520)
        operation=lambda values,domain,spacing,selected:values
        for channels in(None,3,4):
            values=random.uniform(.1,.8,self.domain.shape+(()if channels is None else(channels,)))
            expected=production.filter_source_neighbourhoods(values,self.domain,self.domain>0,[self.patch],operation,values)
            for work_bytes in(1,4096):
                limits=dict(production.source_filter_budget(),gatherBytes=work_bytes)
                with mock.patch.object(production,'source_filter_budget',return_value=limits):
                    actual=production.filter_source_neighbourhoods(values,self.domain,self.domain>0,[self.patch],operation,values)
                for first,last in zip(expected,actual,strict=True):np.testing.assert_array_equal(first,last)

    def test_both_source_uv_aliases_use_the_same_physical_neighbours_without_touching_other_pixels(self):
        production,_=build.source_owner()
        baseline=production.rgb_physical_average(self.values,self.domain,self.metric,.005)
        actual=production.rgb_physical_average(self.values,self.domain,self.metric,.005,source_neighbourhoods=[self.patch])
        self.assertGreater(abs(baseline[64,128]-baseline[64,320]),.005)
        np.testing.assert_allclose(actual[64,[128,320]],[.5,.5],rtol=0,atol=1e-14)
        unchanged=np.ones(self.domain.shape,bool);unchanged[64,[128,320]]=False
        np.testing.assert_array_equal(actual[unchanged],baseline[unchanged])

    def test_a_peer_reference_can_resolve_the_declared_physical_target_but_not_an_unrelated_hole(self):
        production,_=build.source_owner()
        reference=self.domain.copy();reference[:,:256]=0
        target=np.zeros_like(self.domain,bool);target[64,128]=True
        with self.assertRaisesRegex(ValueError,'without filter support'):
            production.rgb_physical_average(self.values,reference,self.metric,.005,target,require_coverage=True)
        actual=production.rgb_physical_average(self.values,reference,self.metric,.005,target,
            require_coverage=True,source_neighbourhoods=[self.patch])
        self.assertGreater(actual[64,128],.5)
        target[64,511]=True
        with self.assertRaisesRegex(ValueError,'without filter support'):
            production.rgb_physical_average(self.values,reference,self.metric,.005,target,
                require_coverage=True,source_neighbourhoods=[self.patch])

    def test_protected_source_samples_are_excluded_from_the_shared_normalised_support(self):
        production,_=build.source_owner()
        values=self.values.copy();domain=self.domain.copy()
        values[:,64:129]=10.;domain[:,64:129]=0
        target=np.zeros_like(domain,bool);target[64,320]=True
        actual=production.rgb_physical_average(values,domain,self.metric,.005,target,
            require_coverage=True,source_neighbourhoods=[self.patch])
        self.assertLess(actual[64,320],.51)

    def test_a_neighbourhood_cannot_address_another_resource_or_an_unresolved_point(self):
        production,_=build.source_owner()
        bad=copy.deepcopy(self.patch);bad['cols'][0,0,0]=512
        with self.assertRaisesRegex(ValueError,'exceed their source image'):
            production.rgb_physical_average(self.values,self.domain,self.metric,.005,source_neighbourhoods=[bad])
        bad=copy.deepcopy(self.patch);bad['weights'][:]=0
        with self.assertRaisesRegex(ValueError,'native boundary classification'):
            production.rgb_physical_average(self.values,self.domain,self.metric,.005,source_neighbourhoods=[bad])

    def test_unresolved_halo_is_rejected_before_filter_even_when_output_points_have_geometry(self):
        production,_=build.source_owner()
        bad=copy.deepcopy(self.patch);bad['weights'][50,64]=0;bad['resolved'][50,64]=False
        with self.assertRaisesRegex(ValueError,'finite filter support unresolved'):
            production.rgb_physical_average(self.values,self.domain,self.metric,.005,source_neighbourhoods=[bad])

    def test_explicit_native_exterior_differs_from_missing_transport_and_unneeded_storage(self):
        production,_=build.source_owner()
        exterior=copy.deepcopy(self.patch);exterior['weights'][50,64]=0;exterior['outside'][50,64]=True
        actual=production.rgb_physical_average(self.values,self.domain,self.metric,.005,source_neighbourhoods=[exterior])
        self.assertTrue(np.isfinite(actual).all())
        unused=copy.deepcopy(self.patch);unused['weights'][0,0]=0;unused['resolved'][0,0]=False;unused['requiredSupport'][0,0]=False
        expected=production.rgb_physical_average(self.values,self.domain,self.metric,.005,source_neighbourhoods=[self.patch])
        same=production.rgb_physical_average(self.values,self.domain,self.metric,.005,source_neighbourhoods=[unused])
        np.testing.assert_array_equal(same,expected)

    def test_exterior_must_be_resolved_by_native_boundary_and_cannot_keep_a_hidden_colour_tap(self):
        production,_=build.source_owner()
        bad=copy.deepcopy(self.patch);bad['outside'][50,64]=True
        with self.assertRaisesRegex(ValueError,'native boundary classification'):
            production.rgb_physical_average(self.values,self.domain,self.metric,.005,source_neighbourhoods=[bad])

    def test_one_positive_evaluation_tap_cannot_be_hidden_outside_the_required_halo(self):
        production,_=build.source_owner()
        bad=copy.deepcopy(self.patch)
        bad['patchRows']=np.array([[64,64],[64,64]])
        bad['patchCols']=np.array([[64,65],[64,65]])
        bad['patchWeights']=np.full((2,2),.5)
        bad['requiredSupport'][64,65]=False;bad['resolved'][64,65]=False;bad['weights'][64,65]=0
        with self.assertRaisesRegex(ValueError,'positive evaluation tap unresolved'):
            production.rgb_physical_average(self.values,self.domain,self.metric,.005,source_neighbourhoods=[bad])

    def test_frequency_tensor_and_feather_share_the_physical_input_without_a_second_colour_projection(self):
        production,_=build.source_owner()
        rgba=np.ones(self.domain.shape+(4,));rgba[:,:,:3]=self.values[:,:,None]*np.array([1.,.7,.5])
        rgba[:,:,3]=np.arange(self.domain.shape[1])[None,:]/self.domain.shape[1]
        settings=dict(fineSigmaMetres=.0005,grainSigmaMetres=.005,coherenceSigmaMetres=.007,
            featherMetres=.02,coherenceRetain=0.,fineCoherenceRetain=.2)
        before,_=production.refine_rgb_structure(rgba,self.domain,self.domain,1-self.domain,self.metric,settings)
        actual,_=production.refine_rgb_structure(rgba,self.domain,self.domain,1-self.domain,self.metric,settings,
            source_neighbourhoods=[self.patch])
        self.assertGreater(abs(before[64,128,0]-before[64,320,0]),.0001)
        np.testing.assert_allclose(actual[64,128,:3],actual[64,320,:3],rtol=0,atol=1e-14)
        np.testing.assert_allclose(actual[:,:,:3].sum((0,1)),rgba[:,:,:3].sum((0,1)),rtol=0,atol=2e-10)
        np.testing.assert_array_equal(actual[:,:,3],rgba[:,:,3])
        np.testing.assert_array_equal(actual[self.domain==0],rgba[self.domain==0])

    def test_regional_rgb_reference_uses_the_same_neighbourhood_and_normal_channel_is_unchanged(self):
        production,_=build.source_owner()
        rgba=np.ones(self.domain.shape+(4,));rgba[:,:,:3]=self.values[:,:,None]*np.array([1.,.7,.5])
        encoded=np.empty(rgba.shape,np.uint8);encoded[:,:,:3]=np.rint(np.where(rgba[:,:,:3]<=.0031308,
            rgba[:,:,:3]*12.92,1.055*rgba[:,:,:3]**(1/2.4)-.055)*255).astype(np.uint8);encoded[:,:,3]=211
        profile=dict(pigmentRetain=1.,normalRetain=.5,normalRadiusMetres=.001,
            fineSigmaMetres=.0005,grainSigmaMetres=.005,coherenceSigmaMetres=.007,featherMetres=.02)
        target=np.zeros_like(self.domain);target[64,[128,320]]=1
        reference=self.domain.copy();reference[:,:256]=0
        region=dict(target=target,reference=reference,protected=np.zeros_like(self.domain),
            sourceLowFrequencySigmaMetres=.001,referenceSigmaMetres=.005,strength=.8,
            maximumLinearRgbDelta=.1,reconstruction='adjacent-low-frequency-rgb',detailRetain=.5)
        with self.assertRaisesRegex(ValueError,'without filter support'):
            production.refine_source_skin_material(encoded,self.domain,self.metric,profile,'color',luminance_regions=[region])
        result,_=production.refine_source_skin_material(encoded,self.domain,self.metric,profile,'color',
            luminance_regions=[region],source_neighbourhoods=[self.patch])
        np.testing.assert_array_equal(result[:,:,3],encoded[:,:,3])
        np.testing.assert_array_equal(result[target==0],encoded[target==0])
        normal=np.tile([255,128,255,128],self.domain.shape+(1,)).astype(np.uint8)
        first,_=production.refine_source_skin_material(normal,self.domain,self.metric,profile,'normal',normal_encoding='packed-rgorag')
        second,_=production.refine_source_skin_material(normal,self.domain,self.metric,profile,'normal',normal_encoding='packed-rgorag',source_neighbourhoods=[self.patch])
        np.testing.assert_array_equal(first,second)



class UniformNativeSpacing(unittest.TestCase):
    def test_scalar_spacing_matches_an_independent_finite_normalised_gaussian(self):
        production,_=build.source_owner()
        random=np.random.default_rng(314);values=random.uniform(.1,.8,(71,83));domain=np.ones_like(values)
        domain[:5]=0;domain[22:25,31:36]=0
        for sigma in(0.,.23,.5,1.,2.7,5.3,160.,378.0472863850705,579.3114477043933):
            radius=int(np.ceil(3*sigma))
            if radius:
                offsets=np.arange(-radius,radius+1);kernel=np.exp(-.5*(offsets/sigma)**2);kernel/=kernel.sum()
                def blur(value):
                    for axis in(0,1):
                        value=np.apply_along_axis(lambda row:np.convolve(np.pad(row,(radius,radius)),kernel,'valid'),axis,value)
                    return value
            else:blur=lambda value:value.copy()
            expected=blur(values*domain)/np.maximum(blur(domain),1e-100)
            actual=production.rgb_physical_average(values,domain,.001,sigma*.001,domain>0,require_coverage=True)
            np.testing.assert_allclose(actual[domain>0],expected[domain>0],rtol=0,atol=8e-15)
            np.testing.assert_array_equal(actual[domain==0],np.zeros(np.count_nonzero(domain==0)))


    def test_scalar_spacing_rejects_unresolved_reference_and_checks_work_before_allocating(self):
        production,_=build.source_owner()
        values=np.ones((31,31));domain=np.zeros_like(values);evaluation=values.astype(bool)
        with self.assertRaisesRegex(ValueError,'without filter support'):
            production.rgb_physical_average(values,domain,.001,.003,evaluation,require_coverage=True)
        for spacing,sigma in((0.,.001),(-1.,.001),(.001,-.001),(.001,float('inf'))):
            with self.assertRaises(ValueError):production.rgb_physical_average(values,values,spacing,sigma)
        limits=dict(production.source_filter_budget(),workBytes=1)
        with mock.patch.object(production,'source_filter_budget',return_value=limits),\
                mock.patch.object(np.fft,'rfft')as fft,\
                self.assertRaisesRegex(ValueError,'temporary work budget'):
            production.rgb_physical_average(values,values,.001,.129)
        fft.assert_not_called()

    def test_sparse_uniform_tensor_matches_independent_finite_convolution_and_gradient(self):
        production,_=build.source_owner();random=np.random.default_rng(560)
        values=random.uniform(-1.5,-.3,(111,149));domain=np.ones_like(values);domain[14:21,33:44]=0
        selected=np.zeros(values.shape,bool);selected[[0,13,17,34,67,110],[0,29,37,50,79,148]]=True
        settings=dict(fineSigmaMetres=.0006,grainSigmaMetres=.0018,coherenceSigmaMetres=.004,
            featherMetres=.008,coherenceRetain=.35,fineCoherenceRetain=1.)
        def average(source,sigma):
            radius=int(np.ceil(3*sigma));offsets=np.arange(-radius,radius+1)
            kernel=np.exp(-.5*(offsets/sigma)**2);kernel/=kernel.sum()
            def blur(field):
                for axis in(0,1):field=np.apply_along_axis(
                    lambda row:np.convolve(row,kernel,'full')[radius:radius+len(row)],axis,field)
                return field
            numerator=blur(source*domain);denominator=blur(domain)
            result=np.divide(numerator,denominator,out=np.zeros_like(numerator),where=denominator>1e-14)
            result[domain==0]=0;return result
        fine=average(values,.6);coarse=average(values,1.8);dy,dx=np.gradient(fine)
        valid_x=np.zeros_like(selected);valid_y=np.zeros_like(selected)
        valid_x[:,1:-1]=(domain[:,:-2]>0)&(domain[:,2:]>0)
        valid_y[1:-1]=(domain[:-2]>0)&(domain[2:]>0);dx*=valid_x;dy*=valid_y
        jxx=average(dx*dx,4.);jyy=average(dy*dy,4.);jxy=average(dx*dy,4.);energy=jxx+jyy
        coherence=np.divide(np.sqrt(np.maximum(0,(jxx-jyy)**2+4*jxy*jxy)),energy,
            out=np.ones_like(energy),where=energy>1e-16);coherence=np.clip(coherence,0,1)
        expected=np.dstack(((fine-coarse)*(1-coherence*.35),coherence))
        report={};actual=production.source_uniform_structure(values,domain,.001,settings,selected,report=report)
        np.testing.assert_allclose(actual[selected],expected[selected],rtol=0,atol=8e-15)
        np.testing.assert_array_equal(actual[~selected],np.zeros((np.count_nonzero(~selected),2)))
        self.assertLessEqual(report['maximumEstimatedWorkBytes'],production.source_filter_budget()['workBytes'])


class NativeSplitUvContracts(unittest.TestCase):
    def setUp(self):
        self.production,_=build.source_owner()
        points=np.array([[-.02,0,0],[0,0,0],[0,.02,0],[-.02,.02,0],
                         [0,0,0],[.02,0,0],[.02,.02,0],[0,.02,0]])
        uv=np.array([[.05,.05],[.45,.05],[.45,.95],[.05,.95],[.55,.05],[.95,.05],[.95,.95],[.55,.95]])
        self.packet=dict(vertices=points.ravel().tolist(),normals=[0.,0.,1.]*8,vertexCount=8,
            weights=[1.,0.,0.,0.]*8,boneIndices=[0]*32,bindPoses=np.tile(np.eye(4).ravel(),2).tolist(),
            uv=[dict(channel=0,values=np.c_[uv,np.zeros((8,2))].ravel().tolist())],
            subMeshes=[[0,1,2,0,2,3,4,5,6,4,6,7]],shapes=[])
        self.skin=dict(bones=['Rig/Head','Rig/Head/Ear'],rendererToPrefab=np.eye(4).ravel().tolist(),
            materials=[dict(properties=[dict(name='_MainTex',scale=[1.,1.],offset=[0.,0.])])])

    def test_only_native_skin_all_stored_motion_and_normal_peers_share_filter_neighbours(self):
        original=copy.deepcopy(self.packet)
        matched=self.production.source_surface_junction(self.packet,self.skin,0,self.packet,self.skin,0,(64,64),(64,64),split_uv=True)
        self.assertEqual(len(matched['pairs']),1)
        self.assertEqual((matched['pairs'][0]['firstTriangle'],matched['pairs'][0]['secondTriangle']),(0,3))
        for mutation in('skin','stored-frame','normal','stored-normal'):
            changed=copy.deepcopy(self.packet)
            if mutation=='skin':changed['weights'][16:20]=[.5,.5,0.,0.];changed['boneIndices'][16:20]=[0,1,0,0]
            if mutation in('stored-frame','stored-normal'):
                vertices=[0.]*24;normals=[0.]*24
                if mutation=='stored-frame':vertices[12]=.002
                else:normals[12]=1.
                changed['shapes']=[dict(name='Native',frames=[dict(weight=100.,vertices=vertices,normals=normals)])]
            if mutation=='normal':changed['normals'][12:15]=[1.,0.,0.]
            with self.subTest(mutation=mutation):
                result=self.production.source_surface_junction(changed,self.skin,0,changed,self.skin,0,(64,64),(64,64),split_uv=True)
                self.assertEqual(result['pairs'],[])
        self.assertEqual(original,self.packet)

    def test_native_prefab_scale_and_shear_define_cut_metres_and_material_sampling_is_exact(self):
        frame=np.eye(4);frame[:3,:3]=[[1.5,.2,0],[0,.7,.1],[0,0,1.2]];frame[:3,3]=[3.,-2.,1.]
        self.skin['rendererToPrefab']=frame.T.ravel().tolist()
        result=self.production.source_surface_junction(self.packet,self.skin,0,self.packet,self.skin,0,(64,64),(64,64),split_uv=True)
        self.assertEqual(len(result['pairs']),1)
        self.assertAlmostEqual(result['pairs'][0]['lengthMetres'],np.linalg.norm(frame[:3,:3]@np.array([0,.02,0])),places=8)
        changed=copy.deepcopy(self.skin);changed['rendererToPrefab'][0]*=2
        with self.assertRaisesRegex(ValueError,'native renderer frame'):
            self.production.source_surface_junction(self.packet,self.skin,0,self.packet,changed,0,(64,64),(64,64),split_uv=True)
        changed=copy.deepcopy(self.skin);changed['materials'][0]['properties'][0]['offset']=[.1,0.]
        with self.assertRaisesRegex(ValueError,'sampled material coordinates'):
            self.production.source_surface_junction(self.packet,self.skin,0,self.packet,changed,0,(64,64),(64,64),split_uv=True)

    def test_raster_receivers_retain_actual_triangle_witnesses_and_do_not_change_the_surface_fields(self):
        for size in(32,8,4):
            for mode in(0,1,2):
                with self.subTest(size=size,mode=mode):
                    packet=copy.deepcopy(self.packet);coords=np.asarray(packet['uv'][0]['values']).reshape(-1,4)
                    coords[:,:2]=coords[:,:2]*[1.3,.8]+[-.2,.15];packet['uv'][0]['values']=coords.ravel().tolist()
                    sampler=dict(wrapU=0,wrapV=0,filterMode=mode,anisoLevel=1,mipLevel=0)
                    domain=dict(vertex_field=np.r_[np.ones(4),np.zeros(4)])
                    baseline=self.production.indexed_surface_uv_metrics(packet,self.skin,0,(size,size),.001,sampler,**domain)
                    observed=[]
                    def receive(ordinal,rows,cols,bary,ownership,density):
                        self.assertTrue(np.all((rows>=0)&(rows<size)&(cols>=0)&(cols<size)))
                        np.testing.assert_allclose(bary.sum(1),1.,rtol=0,atol=1e-12)
                        self.assertGreaterEqual(bary.min(),0.);self.assertGreater(density,0.)
                        triangle=np.asarray(packet['subMeshes'][0]).reshape(-1,3)[ordinal]
                        np.testing.assert_allclose(ownership,np.full(len(rows),float(ordinal<2)),rtol=0,atol=4*np.finfo(float).eps)
                        # Independent positive sampler-footprint test, allowing
                        # the actual Repeat address of a UV outside the atlas.
                        uv=bary@coords[triangle,:2];query=np.c_[uv[:,0]*size-.5,(1-uv[:,1])*size-.5]
                        if mode==0:
                            point=np.floor(query+.5).astype(int)%size
                            np.testing.assert_array_equal(point[:,0],cols);np.testing.assert_array_equal(point[:,1],rows)
                        else:
                            for axis,addresses in((0,cols),(1,rows)):
                                distance=np.abs(query[:,axis]-addresses)
                                self.assertTrue(np.all(np.minimum(distance,np.abs(distance-size))<1.+1e-10))
                        observed.extend(zip(rows.tolist(),cols.tolist()))
                        # Callback inputs are owned copies, not mutable raster state.
                        rows[:]=-100;cols[:]=-100;bary[:]=0;ownership[:]=0
                    actual=self.production.indexed_surface_uv_metrics(packet,self.skin,0,(size,size),.001,sampler,sample_receiver=receive,**domain)
                    self.assertTrue(observed)
                    for index in(0,1,3):np.testing.assert_array_equal(actual[index],baseline[index])
                    self.assertEqual(actual[2],baseline[2])
        with self.assertRaisesRegex(ValueError,'sample receiver must be callable'):
            self.production.indexed_surface_uv_metrics(self.packet,self.skin,0,(8,8),.001,dict(wrapU=0,wrapV=0),sample_receiver=object())


class SkinCodecCandidateBudget(unittest.TestCase):
    @staticmethod
    def four_alpha_block():
        bits=(1<<5)|(255<<58)
        for index,value in enumerate([0,1,2,3]*4):bits|=value<<(97+(2*index-1 if index else 0))
        return bits.to_bytes(16,'little')

    @staticmethod
    def sixteen_alpha_block():
        bits=(1<<6)|(127<<56)|(1<<64)
        for index in range(16):bits|=index<<(65+(4*index-1 if index else 0))
        return bits.to_bytes(16,'little')

    def run_fixture(self,block,width,height,protected_right=False,coarse_mip=False):
        import character_resources as resources
        import material_fields as fields
        from PIL import Image
        production,_=build.source_owner()
        payload=block*(width//4)
        images=[np.asarray(Image.frombytes('RGBA',(width,height),payload,'bcn',7))]
        native=[payload]
        if coarse_mip:
            native.append(block);images.append(np.asarray(Image.frombytes('RGBA',(width//2,height//2),block,'bcn',7)))
        desired=images[0].copy()
        for y in range(height):
            for x in range(4):desired[y,x,:3]=[202,162,122] if (x+y)%2==0 else [30,60,90]
        texture=SimpleNamespace(m_Name='SyntheticSkin',m_ColorSpace=1,m_Width=width,m_Height=height,m_MipCount=len(images),
            m_TextureSettings=SimpleNamespace(m_WrapU=1,m_WrapV=1,m_FilterMode=1,m_Aniso=1,m_MipBias=0.))
        obj=SimpleNamespace(path_id=7,read=lambda:texture)
        file=SimpleNamespace(name='synthetic',objects={7:obj})
        def metrics(packet,skin,slot,shape,*args,**kwargs):
            owner=np.ones(shape)
            if protected_right:owner[:,4:]=0
            return owner,np.full(shape,.001),{},np.ones(shape,dtype=bool)
        with tempfile.TemporaryDirectory() as directory:
            source=Path(directory)/'source.bundle';source.write_bytes(b'synthetic fixture')
            job=dict(source=str(source),sha256=build.sha(source),textures=[dict(object=7,channel='color',
                surfaces=[dict(rendererToPrefab=np.eye(4).ravel().tolist())],domain={},normalEncoding=None,
                profile=dict(coherenceSigmaMetres=.001,featherMetres=.003))])
            with mock.patch.object(build,'source_owner',return_value=(production,None)),\
                 mock.patch.object(build,'saved_skin_surface',return_value=(dict(uv=[]),[])),\
                 mock.patch.object(resources,'load_serialized_bundle',return_value=(None,file)),\
                 mock.patch.object(resources,'texture_mip_images',return_value=images),\
                 mock.patch.object(resources,'texture_mip_payloads',return_value=(25,native)),\
                 mock.patch.object(production,'source_material_ownership',return_value=np.ones(1)),\
                 mock.patch.object(production,'indexed_surface_uv_metrics',side_effect=metrics),\
                 mock.patch.object(production,'refine_source_skin_material',side_effect=lambda pixels,*args,**kwargs:(desired[::-1].copy(),{})),\
                 mock.patch.object(build,'skin_filter_neighbourhoods',return_value=([],{})),\
                 mock.patch.object(production,'source_surface_junction',return_value=dict(pairs=[])),\
                 contextlib.redirect_stdout(io.StringIO()):
                result=build.source_skin_payloads(job)[0]
            self.assertEqual(source.read_bytes(),b'synthetic fixture')
        return result,images[0],desired,native

    def test_qualified_rgb_reencoding_keeps_alpha_unowned_block_and_tiny_mip(self):
        from PIL import Image
        result,source,desired,native=self.run_fixture(self.four_alpha_block(),8,4,True,True)
        actual=np.asarray(Image.frombytes('RGBA',(8,4),result['payload'][:32],'bcn',7))
        np.testing.assert_array_equal(actual[:,:,3],source[:,:,3])
        np.testing.assert_array_equal(actual[:,4:],source[:,4:])
        self.assertEqual(result['payload'][16:32],native[0][16:32])
        self.assertEqual(result['payload'][32:],native[1])
        self.assertLessEqual(int(np.abs(actual[:,:,:3].astype(int)-desired[:,:,:3]).max()),2)
        self.assertGreater(result['mips'][0]['codec']['reencodedRgbBlocks'],0)

    def test_source_candidate_stops_when_exact_native_alpha_prevents_the_bound(self):
        with self.assertRaisesRegex(ValueError,'exceeds the declared error bound'):
            self.run_fixture(self.sixteen_alpha_block(),4,4)

    def test_the_same_declared_budget_is_used_for_fit_and_candidate_acceptance(self):
        with mock.patch.object(build,'MAIN_RGB_BLOCK_REFINEMENT_ERROR',0),\
             self.assertRaisesRegex(ValueError,'exceeds the declared error bound'):
            self.run_fixture(self.four_alpha_block(),8,4,True,True)



class SkinCalculationReuse(unittest.TestCase):
    def setUp(self):
        self.temporary=tempfile.TemporaryDirectory();self.addCleanup(self.temporary.cleanup)
        self.root=Path(self.temporary.name)
        self.pin=lambda path:dict(path=str(path),sha256=build.sha(path))
        self.original=self.root/'original.bundle';self.original.write_bytes(b'original input detail')
        self.target=self.root/'target.bundle';self.target.write_bytes(b'encoded texture checkpoint')
        self.raw=self.root/'raw.mips';self.raw.write_bytes(b'large mip|coarse mip|tiny mip')
        self.inputs=[];self.requested_inputs=[]
        for name in('build_models.py','production.py','resource-package.json'):
            old=self.root/(name+'.source');old.write_bytes(('fixed '+name).encode());self.inputs.append(self.pin(old))
            other=self.root/'relocated'/(name+'.source');other.parent.mkdir(exist_ok=True);other.write_bytes(old.read_bytes())
            self.requested_inputs.append(self.pin(other))
        self.surface=dict(resource=dict(path='old',sha256='a'),meshResource=dict(path='old',sha256='a'),
            materialResource=dict(path='old',sha256='a'),renderer=1,mesh=2,material=3,materialSlot=0,completeMesh=True,
            rendererToPrefab=[1.,0.,0.,0.,0.,1.,0.,0.,0.,0.,1.,0.,0.,0.,0.,1.],bones=['Rig/Head'])
        self.requested_surface=copy.deepcopy(self.surface);self.requested_surface['resource']['path']='relocated'
        self.plan=self.root/'Plan.json'
        build.write(self.plan,dict(source=str(self.original),sha256=build.sha(self.original),inputs=self.inputs,
            textures=[dict(object=7,channel='color',profile=dict(pigmentRetain=.4),surfaces=[self.surface],domain={})]))
        self.operation=dict(kind='source-skin-detail',inputPlan=self.pin(self.plan),target=self.pin(self.target),
            computationInputs=self.inputs,computationEnvironment=build.source_skin_computation_environment(),
            textures=[dict(object=7,raw=self.pin(self.raw),bytes=self.raw.stat().st_size,payloadSha256=build.sha(self.raw))])
        self.proof=dict(appearanceFinishing=[self.operation])
        self.metadata=dict(m_Name='Source',m_Width=8,m_Height=8,m_MipCount=4,m_TextureFormat=25,m_ColorSpace=1,
            m_TextureSettings=dict(m_FilterMode=1,m_WrapU=0,m_WrapV=0),m_StreamData=dict(path='old'),**{'image data':b''})
        self.current_metadata=copy.deepcopy(self.metadata);self.current_metadata['m_Name']='PrivateClone'
        self.current_metadata['m_StreamData']['path']='moved'
        self.current_payload=self.raw.read_bytes()
        self.saved=SimpleNamespace(read_typetree=lambda:copy.deepcopy(self.metadata),read=lambda:SimpleNamespace(get_image_data=self.raw.read_bytes))
        self.current=SimpleNamespace(read_typetree=lambda:copy.deepcopy(self.current_metadata),read=lambda:SimpleNamespace(get_image_data=lambda:self.current_payload))
        self.requested=dict(inputs=self.requested_inputs,surfaces=[self.requested_surface],texture=self.current,normalEncoding=None,
            environment=build.source_skin_computation_environment())
        self.packet=dict(vertices=[0.,0.,0.,1.,0.,0.,0.,1.,0.],uv=[dict(channel=0,values=[0.,0.,0.,0.]*3)],skin=[1.,0.,0.,0.])
        self.packet_changes={}
        def surface(reference):
            value=json.loads(reference);packet=copy.deepcopy(self.packet)
            packet.update(self.packet_changes.get(value['resource']['path'],{}))
            return packet,value['bones']
        import character_resources as resources
        self.patches=[mock.patch.object(build,'saved_skin_surface',side_effect=surface),
            mock.patch.object(resources,'load_serialized_bundle',return_value=(None,SimpleNamespace(objects={7:self.saved})))]
        for patch in self.patches:patch.start();self.addCleanup(patch.stop)

    def authority(self):
        return build.source_skin_detail_authority(self.proof,7,'color',requested=self.requested)

    def test_relocation_and_unrelated_stream_placement_reuse_the_same_calculation(self):
        result=self.authority();self.assertTrue(result['reusable'])
        self.assertEqual(result['source'],dict(**self.pin(self.original),object=7))
        # The legacy authority API still recovers the same original detail.
        self.assertNotIn('reusable',build.source_skin_detail_authority(self.proof,7,'color'))

    def test_bundle_mip_authority_can_be_reused_without_a_second_file(self):
        texture = self.operation['textures'][0]
        del texture['raw']
        texture['payloadInBundle'] = True
        saved_payload = self.raw.read_bytes()
        self.saved.read = lambda: SimpleNamespace(get_image_data=lambda: saved_payload)
        self.raw.unlink()
        self.assertTrue(self.authority()['reusable'])
        saved_payload = b'changed mip payload'.ljust(texture['bytes'], b'!')
        with self.assertRaisesRegex(ValueError,'retained skin mip payload changed'):
            self.authority()

    def test_legacy_mip_authority_uses_bundle_when_the_identical_sidecar_is_archived(self):
        saved_payload = self.raw.read_bytes()
        self.saved.read = lambda: SimpleNamespace(get_image_data=lambda: saved_payload)
        self.raw.write_bytes(b'wrong legacy copy'.ljust(len(saved_payload), b'!'))
        with self.assertRaisesRegex(ValueError,'retained skin mip payload changed'):
            self.authority()
        self.raw.unlink()
        self.assertTrue(self.authority()['reusable'])
        self.operation['textures'][0]['raw']['sha256'] = 'f' * 64
        with self.assertRaisesRegex(ValueError,'retained skin mip payload changed'):
            self.authority()

    def test_code_normal_encoding_and_unknown_epoch_require_recalculation_but_release_metadata_does_not(self):
        for index in range(2):
            with self.subTest(input=index):
                reference=self.requested_inputs[index];path=Path(reference['path']);old=path.read_bytes()
                path.write_bytes(old+b' changed');self.requested_inputs[index]=self.pin(path)
                self.assertFalse(self.authority()['reusable'])
                path.write_bytes(old);self.requested_inputs[index]=self.pin(path)
        reference=self.requested_inputs[-1];path=Path(reference['path']);path.write_bytes(b'public name / release copy / distribution URL only')
        self.requested_inputs[-1]=self.pin(path)
        self.assertTrue(self.authority()['reusable'])
        self.requested['normalEncoding']='DXT5nm';self.assertFalse(self.authority()['reusable'])
        self.requested['normalEncoding']=None
        self.operation['computationInputs']=[];self.assertFalse(self.authority()['reusable'])

    def test_pnt_uv_skin_frame_bones_slot_and_all_shared_consumers_are_inputs(self):
        for key,value in(('vertices',[.001,0.,0.]),('uv',[]),('skin',[.5,.5,0.,0.])):
            with self.subTest(field=key):
                self.packet_changes['relocated']={key:value};self.assertFalse(self.authority()['reusable'])
                self.packet_changes.clear()
        for name,value in(('rendererToPrefab',[1.]*16),('materialSlot',1),('completeMesh',False),('bones',['Rig/Neck'])):
            old=self.requested_surface[name];self.requested_surface[name]=value
            self.assertFalse(self.authority()['reusable']);self.requested_surface[name]=old
        self.requested['surfaces'].append(copy.deepcopy(self.requested_surface));self.assertFalse(self.authority()['reusable'])

    def test_sampler_layout_and_any_mip_change_require_recalculation(self):
        for name,value in(('m_Width',16),('m_Height',16),('m_MipCount',3),('m_TextureFormat',12),('m_ColorSpace',0)):
            old=self.current_metadata[name];self.current_metadata[name]=value
            self.assertFalse(self.authority()['reusable']);self.current_metadata[name]=old
        for name,value in(('m_FilterMode',0),('m_WrapU',1),('m_WrapV',2)):
            old=self.current_metadata['m_TextureSettings'][name];self.current_metadata['m_TextureSettings'][name]=value
            self.assertFalse(self.authority()['reusable']);self.current_metadata['m_TextureSettings'][name]=old
        self.current_payload=self.current_payload[:-1]+b'X';self.assertFalse(self.authority()['reusable'])

    def test_changed_producer_or_encoded_checkpoint_is_an_error(self):
        producer=Path(self.inputs[1]['path']);producer.write_bytes(b'corrupt archive')
        with self.assertRaisesRegex(ValueError,'producer changed'):self.authority()
        producer.write_bytes(b'fixed production.py')
        self.raw.write_bytes(b'corrupt mips')
        with self.assertRaisesRegex(ValueError,'mip payload changed'):self.authority()

    def test_python_and_each_package_version_or_unknown_environment_prevent_reuse(self):
        environment=copy.deepcopy(self.requested['environment'])
        self.requested['environment']['python'][-1]+=1;self.assertFalse(self.authority()['reusable'])
        self.requested['environment']=copy.deepcopy(environment)
        for name in environment['packages']:
            with self.subTest(package=name):
                self.requested['environment']['packages'][name]+='.changed'
                self.assertFalse(self.authority()['reusable'])
                self.requested['environment']=copy.deepcopy(environment)
        del self.operation['computationEnvironment'];self.assertFalse(self.authority()['reusable'])

    def test_producer_receipt_and_replay_use_the_same_runtime_environment_owner(self):
        import character_resources as resources
        environment=dict(python=[3,99,1],packages=dict(numpy='synthetic'))
        job=dict(source=str(self.original),sha256=build.sha(self.original),target=str(self.target),
            receipt=str(self.root/'Receipts/Result.json'),inputs=self.inputs,inputPlan=self.pin(self.plan))
        row=dict(object=7,name='Texture',payload=b'encoded all mips',mips=[])
        with mock.patch.object(build,'source_skin_computation_environment',return_value=environment),\
                mock.patch.object(build,'source_skin_payloads',return_value=[row]),\
                mock.patch.object(resources,'replace_owned_texture_mips',return_value={}):
            result=build.write_source_skin_bundle(job);before=build.source_skin_replay_key({},self.inputs)
        self.assertEqual(result['computationEnvironment'],environment)
        self.assertEqual(build.read(job['receipt'])['computationEnvironment'],environment)
        self.assertFalse((self.root/'Fields').exists())
        with mock.patch.object(build,'source_skin_computation_environment',return_value=dict(environment,python=[3,99,2])):
            self.assertNotEqual(before,build.source_skin_replay_key({},self.inputs))

    def test_prepare_uses_all_shared_consumers_and_actual_profile_domain_or_region_changes_recalculate(self):
        import character_resources as resources
        production,art=build.source_owner();source=self.root/'source';catalog=source/'Catalog';catalog.mkdir(parents=True)
        bundle=catalog/'input.bundle';bundle.write_bytes(b'current catalog owner')
        manifest=dict(bundles=[dict(file=bundle.name,sha256=build.sha(bundle))],resources=[dict(asset='native-prefab',bundles=[dict(file=bundle.name)])])
        build.write(catalog/build.RESOURCE_FILES['manifest'],manifest)
        build.write(source/'ResourceAssembly.json',dict(catalogSha256=build.sha(catalog/build.RESOURCE_FILES['manifest']),
            proofs=[dict(file=bundle.name,sha256=build.sha(bundle),newCab='cab')]))
        census=self.root/'census.json';plans=self.root/'plans.json';paths=['Head_LOD0','Head_LOD1']
        build.write(census,dict(assets=[dict(name='Source',sourceContentKey='native-key',internalId='native-prefab',skins=[
            dict(path=path,sourceProvenance=dict(rendererPathId=identifier),rendererToPrefab=self.surface['rendererToPrefab'])for path,identifier in zip(paths,(1,6))])]))
        pointer=lambda value:dict(m_FileID=0,m_PathID=value)
        objects={identifier:SimpleNamespace(read_typetree=lambda:dict(m_Mesh=pointer(2),m_Materials=[pointer(3)]))for identifier in(1,6)}
        objects.update({2:SimpleNamespace(path_id=2),3:SimpleNamespace(path_id=3,read_typetree=lambda:dict(m_Shader=pointer(4),m_SavedProperties=dict(m_TexEnvs=[('_MainTex',dict(m_Texture=pointer(5))),('_BumpMap',dict(m_Texture=pointer(7)))]))),
            4:SimpleNamespace(read_typetree=lambda:dict(m_ParsedForm=dict(m_Name='skin-shader'))),5:SimpleNamespace(path_id=5),7:SimpleNamespace(path_id=7)})
        file=SimpleNamespace(name='cab',objects=objects)
        profile=dict(pigmentRetain=.5,normalRetain=1.,normalRadiusMetres=.001,microNormalRetain=1.,microNormalRadiusMetres=.0002,
            fineSigmaMetres=.0001,grainSigmaMetres=.00075,coherenceSigmaMetres=.001,featherMetres=.003)
        domain=dict(includeBranches=['Head'],protectPrefixes=['Other'])
        region=lambda center:dict(coordinateDomain='native-rest-prefab-dcc-metres',ellipsoids=[dict(center=center,radii=[.005,.002,.005],innerFraction=.2)])
        regional=dict(target=region([.0005,0.,.0005]),reference=region([.0035,0.,.0035]),protected=None,
            sourceLowFrequencySigmaMetres=.0005,referenceSigmaMetres=.001,strength=.5,maximumLinearRgbDelta=.1,reconstruction='adjacent-low-frequency-rgb')
        base=dict(sourceContentKey='native-key',profile=profile,domain=domain,renderers=[dict(path=path,slots=[0],shader='skin-shader',normalEncoding=None)for path in paths])
        normal_change=dict(profile=dict(profile,macroNormalRetain=.2,macroNormalRadiusMetres=.004))
        cases=[({},None,set(),{'color'}),
            (dict(profile=dict(profile,pigmentRetain=.4)),None,{'color'},set()),
            (dict(domain=dict(domain,includeBranches=['Neck'])),None,{'color'},set()),
            (dict(luminanceRegions=[regional]),None,{'color'},set()),
            (normal_change,None,{'normal'},{'color'}),
            (normal_change,['normal'],{'normal'},set()),
            (normal_change,['color'],set(),{'color'})]
        resume_output=None
        for index,(change,channels,expected_channels,expected_reuses) in enumerate(cases):
            requested=copy.deepcopy(base);requested.update(change);build.write(plans,dict(surfaces=dict(Source=requested)))
            output=self.root/'tools/game-test/artifacts'/('candidate'+str(index));reuse_requests=[]
            def authority(*args,**kwargs):
                value=dict(source=dict(**self.pin(self.original),object=7),profile=profile,domain=domain)
                if kwargs.get('requested')is not None:
                    request=kwargs['requested'];self.assertEqual(len(request['surfaces']),2)
                    self.assertEqual({row['renderer']for row in request['surfaces']},{1,6});reuse_requests.append(args[2])
                    value['reusable']=True
                return value
            arguments=['skin','--source',str(source),'--census',str(census),'--plans',str(plans),'--output',str(output)]
            if channels:arguments+=['--channels',*channels]
            with mock.patch.object(build,'ROOT',self.root),mock.patch.object(build,'checked_catalog_path',side_effect=lambda path:Path(path).resolve()),\
                    mock.patch.object(build,'source_owner',return_value=(production,art)),\
                    mock.patch.object(resources,'load_serialized_bundle',return_value=(None,file)),\
                    mock.patch.object(resources,'skin_bone_paths',return_value=['Rig/Head']),\
                    mock.patch.object(build,'source_skin_detail_authority',side_effect=authority),\
                    mock.patch.object(build,'complete_source_skin_jobs')as complete,\
                    mock.patch.object(Path,'hardlink_to',lambda path,target:shutil.copyfile(target,path)),\
                    mock.patch.object(sys,'argv',arguments):
                build.prepare_skin_surface_resources()
            tasks=complete.call_args.args[4]
            self.assertEqual({texture['channel']for task in tasks for texture in task['textures']},expected_channels)
            self.assertEqual(set(reuse_requests),expected_reuses);self.assertEqual(len(reuse_requests),len(expected_reuses))
            self.assertEqual(complete.call_args.kwargs['channels'],sorted(channels or ['color','normal']))
            if expected_channels:
                self.assertEqual(len(tasks),1)
                self.assertEqual(len(tasks[0]['textures'][0]['surfaces']),2)
                self.assertEqual(tasks[0]['channels'],sorted(channels or ['color','normal']))
            else:self.assertFalse(tasks)
            if channels==['normal']:resume_output=output
        for options,error in ((['--channels','normal','normal'],'distinct source skin channels'),
                (['--channels','color','--resume'],'resumed skin channel selection')):
            arguments=['skin','--source',str(source),'--census',str(census),'--plans',str(plans),
                '--output',str(resume_output),*options]
            with mock.patch.object(build,'ROOT',self.root),mock.patch.object(build,'checked_catalog_path',side_effect=lambda path:Path(path).resolve()),\
                    mock.patch.object(sys,'argv',arguments),self.assertRaisesRegex(ValueError,error):
                build.prepare_skin_surface_resources()


if __name__ == '__main__':
    unittest.main()
