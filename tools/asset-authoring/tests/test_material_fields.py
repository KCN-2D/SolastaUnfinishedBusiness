"""Skin color continuity, palette-independent source fields and persisted calibration."""
import importlib.util
import json
import copy
import math
import ast
from pathlib import Path
from typing import Any
import unittest
import numpy as np

path=Path(__file__).resolve().parents[2]/'blender-models/CharacterFaces/material_fields.py'
spec=importlib.util.spec_from_file_location('material_fields_contracts',path)
assert spec and spec.loader
fields=importlib.util.module_from_spec(spec);spec.loader.exec_module(fields)


class SkinCutCoverageContracts(unittest.TestCase):
    def test_overlap_closes_the_cut_and_retains_unowned_vertices_and_channels(self):
        source=path.with_name('generate.py')
        scope: dict[str,Any]=dict(np=np,copy=copy,math=math)
        nodes: list[ast.stmt]=[node for node in ast.parse(source.read_text(encoding='utf-8')).body if isinstance(node,ast.FunctionDef)]
        exec(compile(ast.Module(body=nodes,type_ignores=[]),str(source),'exec'),scope)
        def packet(y):
            return dict(vertexCount=3,vertices=[-1.,0.,0.,1.,0.,0.,0.,y,0.],normals=[0.,0.,1.]*3,tangents=[1.,0.,0.,1.]*3,
                        weights=[1.,0.,0.,0.]*3,boneIndices=[0,0,0,0]*3,bindPoses=np.eye(4).ravel().tolist(),uv=[],
                        subMeshes=[[0,1,2]],shapes=[],bounds=[0.,0.,0.,2.,2.,1.])
        receiver,donor=packet(1.),packet(-1.)
        result,proof=scope['overlap_skin_boundary'](receiver,donor,[(0,0),(1,1)],np.eye(4),overlap_metres=.00005)
        positions=np.array(result['vertices']).reshape(-1,3)
        self.assertTrue(np.all(positions[:2,1]<0))
        np.testing.assert_array_equal(positions[2],np.array(receiver['vertices']).reshape(-1,3)[2])
        for key in ('vertexCount','uv','weights','boneIndices','bindPoses','subMeshes'):
            self.assertEqual(result[key],receiver[key])
        self.assertEqual(proof['donorInteriorOverlapMetres'],.00005)


class SurfaceToneContracts(unittest.TestCase):
    def test_calibration_blends_continuously_from_the_actual_c1_source(self):
        native=np.tile(np.array([70,50,40,17],np.uint8),(1,5,1))
        baseline=np.tile(np.array([180,130,105,197],np.uint8),(1,5,1));weight=np.array([[1.,.999,.99,0.,0.]])
        atlas=np.tile([.2,.15,.1],(1,5,1));atlas[0,4]=np.nan
        coefficient=dict(policy='surface-local-v4',bodyTarget=[.5,.35,.24])
        result,proof=fields.surface_tone_main_mip(native,weight,coefficient,atlas,c1_source=baseline)
        np.testing.assert_array_equal(result[:,0],baseline[:,0]);np.testing.assert_array_equal(result[:,4],baseline[:,4])
        np.testing.assert_array_equal(result[:,:,3],baseline[:,:,3])
        self.assertLessEqual(int(np.abs(result[0,1,:3].astype(int)-baseline[0,1,:3]).max()),1)
        full,_=fields.surface_tone_main_mip(native,np.zeros((1,5)),coefficient,atlas)
        np.testing.assert_array_equal(result[0,3,:3],full[0,3,:3]);self.assertTrue(proof['sourceC1BaselineUsed'])

    def test_coupled_endpoint_search_crosses_a_scalar_fit_plateau(self):
        from PIL import Image
        block=bytes.fromhex('20a99ee6f4e0e08455eacaab3bb5e595')
        target=np.array([[111,64,41,69],[115,71,48,84],[92,55,35,97],[84,44,24,56],
            [111,64,41,69],[111,64,41,69],[121,77,55,97],[88,49,29,84],
            [111,64,41,69],[111,64,41,69],[115,71,48,84],[121,77,55,97],
            [111,64,41,69],[111,64,41,69],[111,64,41,69],[121,77,56,84]],np.uint8)
        def decoded(payload):return np.asarray(Image.frombytes('RGBA',(4,4),payload,'bcn',7)).reshape(16,4)
        scalar=decoded(fields.refine_bc7_rgb_endpoints(block,target))
        self.assertGreater(int(np.abs(scalar[:,:3].astype(int)-target[:,:3]).max()),16)
        fitted=decoded(fields.refine_bc7_rgb_endpoints(block,target,maximum_rgb_error=16))
        self.assertLessEqual(int(np.abs(fitted[:,:3].astype(int)-target[:,:3]).max()),16)
        np.testing.assert_array_equal(fitted[:,3],decoded(block)[:,3])

    def test_skin_calibration_preserves_dark_pigment_contrast(self):
        raw=np.array([[[80,80,80,255],[160,160,160,255]]],dtype=np.uint8)
        reference=np.tile(fields.lookup[raw[0,1,:3]],(1,2,1))
        target=[.6,.6,.6]
        result,_=fields.surface_tone_main_mip(raw,np.zeros((1,2)),dict(policy='surface-local-v4',bodyTarget=target),reference)
        actual=fields.lookup[result[0,0,0]]/fields.lookup[result[0,1,0]]
        expected=fields.lookup[80]/fields.lookup[160]
        self.assertAlmostEqual(actual,expected,delta=.004)


    def test_legacy_pigment_detail_uses_owned_source_contrast_and_preserves_alpha(self):
        native=np.full((32,32,4),[180,140,110,255],dtype=np.uint8)
        native[14:18,8:24,:3]=[60,45,35]
        authored=np.full_like(native,[200,160,130,255]);authored[14:18,8:24,:3]=[130,100,80]
        authored[:,:,3]=197
        ownership=np.zeros((32,32));ownership[12:20,6:26]=1
        repaired,proof=fields.restore_pigment_contrast(native,authored,ownership)
        self.assertLess(int(repaired[15,16,0]),int(authored[15,16,0]))
        np.testing.assert_array_equal(repaired[ownership==0],authored[ownership==0])
        np.testing.assert_array_equal(repaired[:,:,3],authored[:,:,3])
        self.assertGreater(proof['changedTexels'],0)

    def plane(self) -> dict[str, Any]:
        packet: dict[str,Any]=dict(vertices=np.array([[0,0,0],[.02,0,0],[0,.02,0],[.02,.02,0],[0,0,0.]]),
                    normal=np.tile([0.,0.,1.],(5,1)),uv=np.array([[.1,.1],[.9,.1],[.1,.9],[.9,.9],[.1,.1]]),
                    triangles=np.array([[0,1,2],[4,1,2],[1,3,2]]))
        edges={}
        for face,tri in enumerate(packet['triangles']):
            for a,b in zip(tri,np.roll(tri,-1)):edges.setdefault(tuple(sorted((int(a),int(b)))),[]).append(face)
        packet['edges']=edges
        return packet

    def test_calibration_survives_json_and_physical_uv_aliases(self):
        head=self.plane();main=np.tile([.32,.22,.15,1.],(16,16,1));target=[.5,.35,.24]
        coefficient=fields.surface_tone_references(head,main,target)
        persisted=json.loads(json.dumps(coefficient,allow_nan=False))
        atlas,proof=fields.surface_tone_atlas(head,persisted,16,16)
        known=np.isfinite(atlas[:,:,0]);self.assertTrue(known.any())
        np.testing.assert_allclose(atlas[known],np.tile(main[0,0,:3],(int(known.sum()),1)),rtol=0,atol=1e-7)
        self.assertTrue(proof['sharedLod0Calibration'])

    def test_mip_reference_reuse_matches_fresh_and_invalidates_mutated_inputs(self):
        head=self.plane();main=np.tile([.32,.22,.15,1.],(16,16,1))
        coefficient=fields.surface_tone_references(head,main,[.5,.35,.24]);memo=fields.RasterMemo()
        fields.surface_tone_atlas(head,coefficient,16,16,memo=memo,padding_texels=4.5)
        cached=memo.tone_vertices
        for size in (8,4,2):
            actual,proof=fields.surface_tone_atlas(head,coefficient,size,size,memo=memo,padding_texels=4.5)
            expected,fresh=fields.surface_tone_atlas(head,coefficient,size,size,padding_texels=4.5)
            np.testing.assert_array_equal(actual,expected);self.assertEqual(proof,fresh)
            self.assertIs(memo.tone_vertices,cached)
        for mutate in ('references','vertices'):
            if mutate=='references':coefficient['references'][0]=[.7,.6,.5]
            else:head['vertices'][0]+=[.001,0,0]
            actual,proof=fields.surface_tone_atlas(head,coefficient,8,8,memo=memo)
            expected,fresh=fields.surface_tone_atlas(head,coefficient,8,8)
            np.testing.assert_array_equal(actual,expected);self.assertEqual(proof,fresh)
            self.assertIsNot(memo.tone_vertices,cached);cached=memo.tone_vertices

    def test_local_transform_preserves_alpha_cut_and_unknown_values(self):
        raw=np.array([[[123,95,75,17],[90,75,60,83]],[[110,85,66,144],[84,68,52,245]]],dtype=np.uint8)
        source=fields.lookup[raw[:,:,:3]]
        atlas=source[::-1].copy();atlas[0,0]=np.nan
        weight=np.array([[0.,0.],[0.,1.]])
        coefficient=dict(policy='surface-local-v4',bodyTarget=[.5,.35,.24])
        result,proof=fields.surface_tone_main_mip(raw,weight,coefficient,atlas)
        np.testing.assert_array_equal(result[:,:,3],raw[:,:,3])
        np.testing.assert_array_equal(result[0,1],raw[0,1])
        np.testing.assert_array_equal(result[1,0],raw[1,0])
        np.testing.assert_allclose(fields.lookup[result[0,0,:3]],[.5,.35,.24],atol=.004)
        self.assertTrue(proof['c1WeightOneRgbaExact'])

    def test_lower_lod_uses_the_persisted_source_target(self):
        high=self.plane();main=np.tile([.31,.21,.14,1.],(16,16,1));coefficient=fields.surface_tone_references(high,main,[.5,.34,.22])
        low=dict(high,vertices=high['vertices'][:3],normal=high['normal'][:3],uv=high['uv'][:3],triangles=np.array([[0,1,2]]),edges={(0,1):[0],(1,2):[0],(0,2):[0]})
        atlas,proof=fields.surface_tone_atlas(low,json.loads(json.dumps(coefficient)),8,8)
        np.testing.assert_allclose(atlas[np.isfinite(atlas[:,:,0])],np.tile([.31,.21,.14],(int(np.isfinite(atlas[:,:,0]).sum()),1)),atol=1e-7)
        self.assertEqual(proof['maximumNearestSourceDistanceMetres'],0.)

    def test_invalid_source_reference_is_rejected(self):
        with self.assertRaises(ValueError):fields.surface_tone_references(self.plane(),np.zeros((8,8,4)),[.5,.35,.24])

    def test_dark_eye_surface_is_not_used_as_the_skin_reference(self):
        head=self.plane()
        head['vertices']=np.concatenate((head['vertices'],[[.1,0,0],[.102,0,0],[.1,.002,0]]))
        head['normal']=np.tile([0.,0.,1.],(8,1))
        head['uv']=np.concatenate((head['uv'],[[.46,.46],[.54,.46],[.46,.54]]))
        head['triangles']=np.concatenate((head['triangles'],[[5,6,7]]))
        main=np.full((32,32,4),[.45,.35,.25,1.]);main[13:19,13:19,:3]=[.03,.02,.01]
        coefficient=fields.surface_tone_references(head,main,[.6,.45,.32])
        references=np.asarray(coefficient['references'])
        self.assertGreater(references[:,0].min(),.4)
        self.assertGreater(coefficient['globalFallbackNodes'],0)

    def test_bc7_rgb_field_retains_alpha_and_matches_each_scalar_mode(self):
        from PIL import Image
        rng=np.random.default_rng(1209);raw=rng.integers(0,256,(512,16),np.uint8)
        for index in range(len(raw)):
            mode=index%8;raw[index,0]=(int(raw[index,0])&~((1<<(mode+1))-1))|(1<<mode)
        payload=raw.tobytes();width,height=128,64
        original=np.asarray(Image.frombytes('RGBA',(width,height),payload,'bcn',7))
        target=original.copy();target[:,:,:3]=np.rint(target[:,:,:3]*.7+8).astype(np.uint8)
        encoded,proof=fields.fit_bc7_rgb_field(payload,width,height,target)
        saved=np.asarray(Image.frombytes('RGBA',(width,height),encoded,'bcn',7))
        np.testing.assert_array_equal(saved[:,:,3],original[:,:,3])
        for index in range(512):
            x=index%(width//4)*4;y=index//(width//4)*4
            expected=fields.bc7_rgb_endpoints(payload[index*16:(index+1)*16],original[y:y+4,x:x+4],target[y:y+4,x:x+4])
            self.assertEqual(encoded[index*16:(index+1)*16],expected)
        self.assertTrue(proof['partitionsAndIndicesRetained'])

    def test_bc7_field_preserves_whole_blocks_that_touch_the_collar_or_unknown_uv(self):
        from PIL import Image
        rng=np.random.default_rng(18);blocks=rng.integers(0,256,(4,16),np.uint8)
        blocks[:,0]=(blocks[:,0]&128)|64
        payload=blocks.tobytes();source=np.asarray(Image.frombytes('RGBA',(8,8),payload,'bcn',7))
        target=source.copy();target[:,:,:3]//=2
        editable=np.ones((8,8),bool);editable[0,0]=False;editable[7,7]=False
        encoded,proof=fields.fit_bc7_rgb_field(payload,8,8,target,editable)
        self.assertEqual(encoded[:16],payload[:16]);self.assertEqual(encoded[48:],payload[48:])
        self.assertEqual(proof['retainedBoundaryBlocks'],2)

    def test_rgb_endpoint_refinement_meets_the_bound_without_changing_nonuniform_alpha(self):
        from PIL import Image
        bits=1<<6;position=7
        for pair in ((36,220),(64,200),(80,180),(200,240)):
            for value in pair:bits|=(value//2)<<position;position+=7
        for index in range(1,16):bits|=index<<(68+4*(index-1))
        payload=bits.to_bytes(16,'little');original=np.asarray(Image.frombytes('RGBA',(4,4),payload,'bcn',7))
        self.assertGreater(len(np.unique(original[:,:,3])),4)
        target=original.copy();target[0,0,0]+=30
        _,initial=fields.fit_bc7_rgb_field(payload,4,4,target)
        encoded,proof=fields.fit_bc7_rgb_field(payload,4,4,target,maximum_rgb_error=16)
        saved=np.asarray(Image.frombytes('RGBA',(4,4),encoded,'bcn',7))
        np.testing.assert_array_equal(saved[:,:,3],original[:,:,3])
        self.assertGreater(initial['maximumRgbError'],16)
        self.assertLessEqual(proof['maximumRgbError'],16)
        self.assertEqual(proof['refinedEndpointBlocks'],1)


if __name__=='__main__':unittest.main()
