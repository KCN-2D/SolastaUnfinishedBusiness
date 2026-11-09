"""Skin color continuity, palette-independent source fields and persisted calibration."""
import importlib.util
import json
import copy
import math
import ast
import hashlib
import inspect
import tempfile
from pathlib import Path
from typing import Any
import unittest
from unittest import mock
import numpy as np

path=Path(__file__).resolve().parents[2]/'blender-models/CharacterFaces/material_fields.py'
spec=importlib.util.spec_from_file_location('material_fields_contracts',path)
assert spec and spec.loader
fields=importlib.util.module_from_spec(spec);spec.loader.exec_module(fields)


class SkinCutCoverageContracts(unittest.TestCase):
    def test_saved_lod_bake_uses_its_actual_uv_and_retains_face_rows_and_alpha(self):
        vertices=np.array([[0,0,0],[.2,0,0],[.2,.2,0],[0,.2,0.]])
        source=dict(vertices=vertices,normal=np.tile([0.,0.,1.],(4,1)),uv=np.array([[0.,0.],[1,0],[1,1],[0,1]]),triangles=np.array([[0,1,2],[0,2,3]]))
        target=dict(source,uv=source['uv']*[-1,1]+[1,0])
        pigment=np.full((16,16,4),[160,110,80,210],np.uint8);pigment[:,:,0]=np.arange(16)*5+100
        current=np.full((8,8,4),[90,80,70,123],np.uint8);ownership=np.zeros((8,8));ownership[:4]=1
        saved,owned,proof=fields.bake_skin_lod_pigment(source,target,current,pigment,ownership)
        np.testing.assert_array_equal(saved[4:],current[4:])
        np.testing.assert_array_equal(saved[:,:,3],current[:,:,3])
        self.assertGreater(int(saved[0,0,0]),int(saved[0,-1,0]))
        self.assertTrue(owned[:4].any());self.assertFalse(owned[4:].any())
        self.assertLess(proof['maximumProjectionMetres'],1e-7)
        unchanged,owned,_=fields.bake_skin_lod_pigment(source,target,current,pigment,np.ones((8,8)))
        np.testing.assert_array_equal(unchanged,current);self.assertFalse(owned.any())

    def test_saved_lod_projection_rejects_wrong_surface_and_follows_triangle_uv(self):
        surface=dict(vertices=np.array([[0.,0.,0.],[1,0,0],[0,1,0]]),uv=np.array([[.1,.2],[.8,.2],[.1,.9]]),triangles=np.array([[0,1,2]]))
        uv,distance=fields.project_skin_surface_uv(surface,np.array([[.25,.25,.001]]),np.array([[0.,0.,1.]]),.01)
        np.testing.assert_allclose(uv,[[.275,.375]]);np.testing.assert_allclose(distance,[.001])
        with self.assertRaises(ValueError):fields.project_skin_surface_uv(surface,np.array([[.25,.25,1.]]),np.array([[0.,0.,1.]]),.01)
        with self.assertRaises(ValueError):fields.project_skin_surface_uv(surface,np.array([[.25,.25,0.]]),np.array([[0.,0.,-1.]]),.01)

    def test_owned_skin_codec_keeps_all_outside_blocks_and_material_alpha_budget(self):
        import etcpak
        from PIL import Image
        original=np.full((4,8,4),[150,120,90,127],np.uint8)
        payload=etcpak.compress_bc7(original.tobytes(),8,4,None)
        before=np.asarray(Image.frombytes('RGBA',(8,4),payload,'bcn',7));target=before.copy();target[:,:4,:3]=[130,105,85]
        encoded,proof=fields.encode_owned_bc7_rgb(payload,target,np.array([[True,False]]))
        self.assertEqual(encoded[16:],payload[16:]);self.assertLessEqual(proof['rgbRms'],3);self.assertLessEqual(proof['alphaRms'],2)
        encoded,proof=fields.encode_owned_bc7_rgb(payload,before,np.zeros((1,2),bool))
        self.assertEqual(encoded,payload);self.assertEqual(proof['blocks'],0)

    def test_small_skin_mips_keep_exact_material_alpha_when_bc7_cannot_fit(self):
        import etcpak
        from PIL import Image
        images=[];payload=[]
        for width in(8,4,2,1):
            value=np.full((width,width,4),[145,115,90,170],np.uint8)
            padded=np.pad(value,((0,(-width)%4),(0,(-width)%4),(0,0)),mode='edge')
            raw=etcpak.compress_bc7(padded.tobytes(),max(4,width),max(4,width),None)
            payload.append(raw);images.append(np.asarray(Image.frombytes('RGBA',(width,width),raw,'bcn',7)))
        target=images[0].copy();target[:,:4,:3]=np.random.default_rng(42).integers(20,235,(8,4,3),dtype=np.uint8)
        owned=np.zeros((8,8),bool);owned[:,:4]=True
        raw,storage,levels=fields.encode_skin_lod_mips(images,b''.join(payload),target,owned)
        self.assertEqual(storage,4)
        decoded=np.frombuffer(raw,np.uint8,count=8*8*4).reshape(8,8,4)
        np.testing.assert_array_equal(decoded,target)
        offset=0
        for before,level in zip(images,levels):
            length=before.size;decoded=np.frombuffer(raw[offset:offset+length],np.uint8).reshape(before.shape)
            np.testing.assert_array_equal(decoded[:,:,3],before[:,:,3]);self.assertEqual(level['codec']['rgbRms'],0.)
            offset+=length
        self.assertEqual(offset,len(raw))

    def test_face_reference_excludes_dark_features_and_material_alpha(self):
        source=np.array([[[20,12,10,0],[40,25,21,40],[155,115,100,120],[180,140,120,240]]],np.uint8)
        target=source.copy();target[:,:,:3]=np.rint(target[:,:,:3]*.8).astype(np.uint8)
        mass=np.ones((1,4))
        coefficient=fields.head_pigment_coefficient(source,target,mass)
        changed=target.copy();changed[:,:2,:3]=[250,0,250];changed[:,:,3]=255
        self.assertEqual(coefficient,fields.head_pigment_coefficient(source,changed,mass))
        self.assertTrue(coefficient['sharedAcrossSourceLods'])
        self.assertTrue(all(0<value<1 for value in coefficient['gain']))
        self.assertEqual(coefficient['pairedSkinSamples'],2)

    def test_face_pigment_codec_keeps_the_current_material_alpha_and_collar(self):
        import etcpak
        from PIL import Image
        baseline=np.full((4,8,4),[150,110,90,180],np.uint8)
        baseline[:,:4,3]=np.array([30,90,150,210])[np.arange(4)[:,None]]
        payload=etcpak.compress_bc7(baseline.tobytes(),8,4,None)
        current=np.asarray(Image.frombytes('RGBA',(8,4),payload,'bcn',7))
        target=current.copy();target[:,:4,:3]=[125,100,95]
        fraction=np.zeros((4,8));fraction[:,:4]=1
        encoded,_,_,proof=fields.native_face_bc7_mip(payload,target,fraction,pigment_policy='calibrated')
        saved=np.asarray(Image.frombytes('RGBA',(8,4),encoded,'bcn',7))
        np.testing.assert_array_equal(saved[:,:,3],current[:,:,3])
        self.assertEqual(encoded[16:],payload[16:])
        self.assertLessEqual(proof['nativeFaceRgb']['rms'],2)

    def test_calibrated_face_keeps_current_alpha_collar_and_feature_contrast(self):
        calibrated=np.array([[[95,72,68,8],[35,24,22,9],[150,128,121,10]],
                             [[80,60,56,11],[48,31,29,12],[140,116,109,13]]],np.uint8)
        current=np.full((2,3,4),[180,96,83,170],np.uint8)
        current[:,:,3]=np.array([[17,40,90],[110,160,240]],np.uint8)
        fraction=np.array([[0.,np.nan,0.],[1.,1.,.5]])
        result,proof=fields.compose_head_material_mip(calibrated,current,fraction)
        np.testing.assert_array_equal(result[0,:2,:3],calibrated[0,:2,:3])
        np.testing.assert_array_equal(result[:,:,3],current[:,:,3])
        np.testing.assert_array_equal(result[1],current[1])
        self.assertGreater(int(result[0,0,0])-int(result[0,1,0]),50)
        expected=(fields.lookup[calibrated[0,2,:3]]+fields.lookup[current[0,2,:3]])*.5
        np.testing.assert_allclose(fields.lookup[result[0,2,:3]],expected,atol=.004)
        self.assertTrue(proof['faceSourceRgbExact']and proof['collarRgbaExact']and proof['alphaExact'])

    def test_bc7_mode4_keeps_independent_material_alpha_and_pigment_palettes(self):
        from PIL import Image
        blocks=np.empty((2,16,4),np.uint8)
        blocks[0,:,:3]=128;blocks[1,:,:3]=np.array([[80,90,110],[100,100,120],[120,110,130],[140,120,140]])[np.arange(16)%4]
        blocks[:,:,3]=np.array([0,36,72,109,145,182,218,255])[np.arange(16)%8]
        encoded=fields.bc7_rgba_mode4_blocks(blocks)
        decoded=np.asarray(Image.frombytes('RGBA',(8,4),encoded.tobytes(),'bcn',7)).reshape(4,2,4,4).transpose(1,0,2,3).reshape(2,16,4)
        self.assertTrue(np.all((encoded[:,0]&31)==16))
        self.assertLessEqual(np.abs(decoded.astype(int)-blocks).max(),4)
        self.assertLessEqual(np.sqrt(np.mean((decoded.astype(float)-blocks)**2)),2)

    def test_native_face_reencoding_retains_the_calibrated_bc7_collar(self):
        import etcpak
        from PIL import Image
        baseline=np.full((4,8,4),[40,60,80,190],np.uint8)
        payload=etcpak.compress_bc7(baseline.tobytes(),8,4,None)
        decoded=np.asarray(Image.frombytes('RGBA',(8,4),payload,'bcn',7))
        target=decoded.copy();target[:,:4]=[110,90,85,255]
        mass=np.zeros((4,8));mass[:,:4]=1
        result,face,calibrated,proof=fields.native_face_bc7_mip(payload,target,mass)
        self.assertEqual(result[16:],payload[16:]);self.assertNotEqual(result[:16],payload[:16])
        np.testing.assert_array_equal(face,[[True,False]]);np.testing.assert_array_equal(calibrated,[[False,True]])
        saved=np.asarray(Image.frombytes('RGBA',(8,4),result,'bcn',7))
        np.testing.assert_array_equal(saved[:,4:],decoded[:,4:])
        self.assertLessEqual(np.abs(saved[:,:4].astype(int)-target[:,:4]).max(),2)
        self.assertEqual(proof['calibratedBlocksExact'],1)
        with self.assertRaisesRegex(ValueError,'truncated'):
            fields.native_face_bc7_mip(payload[:16],target,mass)

    def test_native_face_blocks_survive_compression_and_the_neck_uses_its_calibrated_color(self):
        from PIL import Image
        import struct
        first=bytes([255,0,0,0,0,0,0,0])+struct.pack('<HHI',0xf800,0,0)
        second=bytes([128,0,0,0,0,0,0,0])+struct.pack('<HHI',0x07e0,0,0)
        target=np.full((4,8,4),[40,60,80,190],np.uint8)
        target[:,:4]=np.asarray(Image.frombytes('RGBA',(4,4),first,'bcn',3))
        fraction=np.zeros((4,8));fraction[:,:4]=1
        payload,face,proof=fields.native_face_bc3_mip(first+second,12,target,fraction)
        self.assertEqual(payload[:16],first);self.assertNotEqual(payload[16:],second)
        np.testing.assert_array_equal(face,[[True,False]])
        decoded=np.asarray(Image.frombytes('RGBA',(8,4),payload,'bcn',3))
        np.testing.assert_array_equal(decoded[:,:4],target[:,:4])
        self.assertLessEqual(np.abs(decoded[:,4:].astype(int)-target[:,4:].astype(int)).max(),4)
        self.assertEqual(proof['nativeFaceBlocksExact'],1)
        with self.assertRaisesRegex(ValueError,'truncated'):
            fields.native_face_bc3_mip(first,12,target,fraction)

    def test_face_material_alpha_follows_the_same_anatomical_transition_as_rgb(self):
        native=np.full((1,3,4),[80,40,30,17],np.uint8);authored=np.full((1,3,4),[120,90,70,197],np.uint8)
        result,proof=fields.preserve_head_pigment_mip(native,authored,np.array([[1.,.5,0.]]),native_alpha=True)
        np.testing.assert_array_equal(result[0,0],native[0,0]);np.testing.assert_array_equal(result[0,2],authored[0,2])
        self.assertEqual(int(result[0,1,3]),107);self.assertTrue(proof['nativeFaceRgbaExact']);self.assertFalse(proof['alphaExact'])

    def test_anatomical_uv_ownership_does_not_require_a_spatial_surface_fit(self):
        packet=dict(vertexCount=4,uv=[dict(channel=0,values=[.1,.1,0,0,.9,.1,0,0,.9,.9,0,0,.1,.9,0,0])],subMeshes=[[0,1,2,0,2,3]])
        atlas,proof=fields.regional_fraction_uv(packet,np.array([0.,0.,1.,1.]),8,8)
        known=np.isfinite(atlas);self.assertTrue(known.any());self.assertTrue(np.all((atlas[known]>=0)&(atlas[known]<=1)))
        self.assertGreater(proof['occupiedTexels'],0)

    def test_shared_uv_footprint_protects_the_collar_independently_of_triangle_order(self):
        coords=[.1,.1,0,0,.9,.1,0,0,.1,.9,0,0]
        packet=dict(vertexCount=6,uv=[dict(channel=0,values=coords*2)],subMeshes=[[0,1,2,3,4,5]])
        mass=np.array([1.,1.,1.,0.,0.,0.])
        with self.assertRaisesRegex(ValueError,'UV-overlapping'):
            fields.regional_fraction_uv(packet,mass,8,8)
        atlas,proof=fields.regional_fraction_uv(packet,mass,8,8,overlap='protect-complement')
        packet['subMeshes']=[[3,4,5,0,1,2]]
        reverse,_=fields.regional_fraction_uv(packet,mass,8,8,overlap='protect-complement')
        np.testing.assert_array_equal(atlas,reverse)
        self.assertTrue(np.all(atlas[np.isfinite(atlas)]==0))
        self.assertEqual(proof['maximumUvOverlapMassDifference'],1.)

    def test_face_pigment_preservation_retains_color_and_dark_details_without_touching_neck_or_alpha(self):
        native=np.array([[[20,3,14,17],[80,60,40,33],[170,130,110,49],[200,180,170,65]]],np.uint8)
        authored=np.tile(np.array([210,150,120,197],np.uint8),(1,4,1));mass=np.array([[1.,.5,0.,np.nan]])
        result,proof=fields.preserve_head_pigment_mip(native,authored,mass)
        np.testing.assert_array_equal(result[0,0,:3],native[0,0,:3])
        np.testing.assert_array_equal(result[:,:,3],authored[:,:,3]);np.testing.assert_array_equal(result[:,2:],authored[:,2:])
        expected=(fields.lookup[native[0,1,:3]]+fields.lookup[authored[0,1,:3]])*.5
        np.testing.assert_allclose(fields.lookup[result[0,1,:3]],expected,atol=.004)
        self.assertEqual((proof['nativeFaceTexels'],proof['calibratedNeckTexels'],proof['transitionTexels']),(1,2,1))
        with self.assertRaisesRegex(ValueError,'unit interval'):
            fields.preserve_head_pigment_mip(native,authored,np.array([[1.,-1.,0.,0.]]))

    def test_serialized_skin_context_uses_the_saved_complete_cut_without_mutating_inputs(self):
        def tube(levels,offset) -> dict[str,Any]:
            ring=np.array([[1.,0.,0.],[0.,0.,1.],[-1.,0.,0.],[0.,0.,-1.]])
            vertices=np.concatenate([ring+[0.,y,0.]for y in levels])+offset
            triangles=[[i,(i+1)%4,i+4]for i in range(4)]+[[(i+1)%4,(i+1)%4+4,i+4]for i in range(4)]
            pose=np.eye(4);pose[:3,3]=-np.asarray(offset)
            return dict(vertexCount=8,vertices=vertices.ravel().tolist(),normals=np.tile([0.,0.,1.],(8,1)).ravel().tolist(),
                tangents=np.tile([1.,0.,0.,1.],(8,1)).ravel().tolist(),bindPoses=pose.T.ravel().tolist(),
                subMeshes=[np.asarray(triangles).ravel().tolist()],uv=[dict(channel=0,values=np.tile([.2,.3,0.,0.],(8,1)).ravel().tolist())])
        head=tube([0.,1.],[.25,0.,0.]);body=tube([-1.,0.],[0.,0.,0.]);before=copy.deepcopy((head,body))
        h,b,he,be,hc,bc,proof=fields.serialized_skin_context(head,body,['Neck'],['Neck'])
        self.assertEqual((len(he),len(be)),(4,4))
        np.testing.assert_allclose(h['vertices'][np.unique(he)],b['vertices'][np.unique(be)])
        self.assertAlmostEqual(hc.perimeter,bc.perimeter)
        self.assertEqual((head,body),before)
        self.assertTrue(proof['sourceVertexChannelsUnchanged'])
        aliased=copy.deepcopy(head);poses=np.asarray(head['bindPoses']).reshape(4,4).T
        wrong=poses.copy();wrong[0,3]+=2.
        aliased['bindPoses']=np.stack((wrong,poses,poses)).transpose(0,2,1).ravel().tolist()
        donor=copy.deepcopy(body);donor['bindPoses']=body['bindPoses']*2
        h,b,he,be,hc,bc,_=fields.serialized_skin_context(aliased,donor,['Neck','Neck','Chest'],['Neck','Chest'])
        np.testing.assert_allclose(h['vertices'][np.unique(he)],b['vertices'][np.unique(be)])
        self.assertAlmostEqual(hc.perimeter,bc.perimeter)
        broken=copy.deepcopy(head);broken['vertices'][0]+=.003
        with self.assertRaisesRegex(ValueError,'complete physical cut'):
            fields.serialized_skin_context(broken,body,['Neck'],['Neck'])

    def test_face_chart_correction_is_not_discarded_by_the_collar_mip_mask(self):
        original=np.full((2,2,4),128,np.uint8);original[:,:,3]=255
        candidate=original.copy();candidate[0,:,:3]=160
        owned=np.array([[True,True],[False,False]])
        protected=np.array([[False,True],[False,False]])
        buffers=dict(weight=np.zeros((2,2)),protected=np.ones((2,2),bool),mainOwned=owned[::-1],mainProtected=protected[::-1],
            pigmentAlignment=dict(gain=[1.,1.,1.]))
        with mock.patch.object(fields,'encode_main_rgba',return_value=candidate):
            result,*_=next(fields.head_main_mips(np.zeros((2,2,4)),buffers,[original],None,None))
        np.testing.assert_array_equal(result[0,0],candidate[0,0])
        np.testing.assert_array_equal(result[0,1],original[0,1])
        np.testing.assert_array_equal(result[1],original[1])

    def test_independent_skin_pigment_alignment_preserves_alpha_and_dark_detail(self):
        from types import SimpleNamespace
        image=np.array([[[.8,.6,.4,.25],[.08,.06,.04,.75]]],float)
        body=np.array([[[.4,.3,.2,1.]]])
        boundary=SimpleNamespace(edges=np.array([[0,1]]),length=np.array([1.]),
            evaluate=lambda edges,t:(np.zeros((len(t),3)),np.tile([.25,.5],(len(t),1)),None),
            nearest=lambda p:(np.zeros(len(p),int),np.zeros(len(p)),np.zeros(len(p))))
        result,proof=fields.align_head_main(image,body,boundary,boundary)
        np.testing.assert_allclose(result[0,0,:3],body[0,0,:3],rtol=0,atol=1e-12)
        np.testing.assert_allclose(result[0,0,:3]/result[0,1,:3],10.,rtol=0,atol=1e-12)
        np.testing.assert_array_equal(result[:,:,3],image[:,:,3])
        self.assertEqual(proof['clippedTexels'],0)

    def test_collapsed_native_uv_triangle_does_not_become_a_chart_curve(self):
        positions=np.array([[0,0,0],[1,0,0],[0,1,0],[1,0,0],[0,0,0],[1,-1,0]],float)
        uv=np.array([[.1,.1],[.3,.1],[.1,.3],[.7,.1],[.7,.1],[.8,.2]])
        packet=dict(vertices=positions.ravel().tolist(),weights=np.tile([1.,0,0,0],(6,1)).ravel().tolist(),boneIndices=[0]*24,shapes=[])
        head=dict(packet=packet,vertices=positions,uv=uv,normal=np.tile([0.,0,1],(6,1)),triangles=np.array([[0,1,2],[3,4,5]]))
        edges,_,counts,_=fields.native_alias_edges(head,['Root'],np.zeros(6,bool))
        self.assertEqual(edges,[])
        self.assertEqual(counts['collapsedNativeUvSupportEdges'],1)
        self.assertEqual(counts['eligibleSmoothBodySupportAliasEdges'],0)

    def test_skin_rgb_alias_gain_preserves_dark_source_detail(self):
        from types import SimpleNamespace
        image=np.array([[[.4,.3,.2,.25],[.8,.6,.4,.75]]],float)
        curve=SimpleNamespace(perimeter=1.,node_delta_main=np.zeros((1,2,3)),
            nearest=lambda p:(np.zeros(len(p),int),np.full(len(p),.5),np.zeros(len(p))),
            arc=lambda i,t:t,at_arc=lambda a:(np.zeros(len(a),int),a),
            evaluate=lambda i,t,opposite=False:(np.tile([.75 if opposite else .25,.5],(len(i),1)),None))
        points=np.zeros((2,3));queries=np.tile([.25,.5],(2,1))
        gain,proof=fields.continuous_alias_main(queries,points,np.ones(2),curve,image,preserve_local_contrast=True)
        np.testing.assert_allclose(gain,.5,rtol=0,atol=1e-12)
        native=np.array([[.4,.3,.2],[.04,.03,.02]])
        corrected=native*(1+gain)
        np.testing.assert_allclose(corrected[0],[.6,.45,.3],rtol=0,atol=1e-12)
        np.testing.assert_allclose(corrected[0]/corrected[1],10.,rtol=0,atol=1e-12)
        self.assertTrue(proof['mainAlphaDeltaZero'])
        self.assertFalse(proof['normalImageConsumed'])

    def test_joint_geometric_normal_does_not_invert_a_cancelling_cut_frame(self):
        positions=np.array([[-.02,-.02,0.],[.02,-.02,0.],[.02,.02,0.],[-.02,.02,0.]])
        uv=np.array([[.1,.1],[.9,.1],[.9,.9],[.1,.9]])
        normals=np.tile([0.,0.,1.],(4,1));tangents=np.tile([1.,0.,0.,1.],(4,1));tangents[1,3]=-1
        curve=fields.BoundaryAtlas(positions,uv,normals,tangents,np.array([[0,1],[1,2],[2,3],[3,0]]))
        ii=np.array([0,1,2,3]);tt=np.full(4,.5);points,queries,frames=curve.evaluate(ii,tt)
        self.assertAlmostEqual(float(np.linalg.det(frames[0])),0.)
        source=np.tile([1.,.4,.7,.65],(8,8,1))
        actual,clipped=fields.continuous_geometric_normal(queries,points,np.array([1.,1.,1.,0.]),curve,source)
        xy=np.column_stack((actual[:,0]*actual[:,3],actual[:,1]))*2-1
        np.testing.assert_allclose(xy[:3],0.,rtol=0,atol=1e-12)
        np.testing.assert_array_equal(actual[3],source[0,0])
        self.assertEqual(clipped,0)

    def test_physical_cut_ties_use_the_actual_repeat_uv_branch(self):
        from types import SimpleNamespace
        curve=SimpleNamespace(first=np.zeros((3,3)),delta=np.tile([1.,0.,0.],(3,1)),square=np.ones(3),
            edges=np.arange(6).reshape(3,2),uv=np.array([[0.,.1],[1.,.1],[0.,.6],[1.,.6],[0.,.9],[1.,.9]]))
        points=np.tile([.5,.01,0.],(4,1));uv=np.array([[.5,.1],[.5,.6],[1.5,.9],[.5,1.1]])
        edge,amount,error=fields.nearest_raw_chart(curve,points,uv)
        np.testing.assert_array_equal(edge,[0,1,2,0])
        np.testing.assert_array_equal(amount,[.5]*4)
        np.testing.assert_allclose(error,[.01]*4,rtol=0,atol=1e-12)

    def test_bilinear_cut_fits_repeat_queries_without_changing_locked_texels(self):
        source=np.full((8,8,4),.25)
        ownership=np.zeros((8,8),bool);ownership[2:5,[7,0]]=True
        uv=np.asarray([[0.,.375],[1.,.375],[0.,.4375],[0.,.5]])
        target=np.tile([.5,.4,.3,.6],(4,1))
        result,proof=fields.fit_bilinear_cut(source,uv,target,ownership)
        np.testing.assert_array_equal(result[~ownership],source[~ownership])
        np.testing.assert_allclose(fields.bilinear_gpu_rows(result,uv),target,rtol=0,atol=1/4096)
        self.assertLess(proof['maximumErrorAfter'],proof['maximumErrorBefore'])
        self.assertTrue(proof['unownedTexelsExact'])
        self.assertLessEqual(proof['iterations'],96)

    def test_bilinear_cut_reports_unsatisfied_constraints_when_every_tap_is_locked(self):
        source=np.full((4,4,4),.25)
        result,proof=fields.fit_bilinear_cut(source,np.asarray([[.5,.5]]),np.full((1,4),.75),np.zeros((4,4),bool))
        np.testing.assert_array_equal(result,source)
        self.assertEqual(proof['maximumErrorAfter'],.5)
        self.assertEqual(proof['changedTexels'],0)

    def test_shared_cut_queries_at_unorm_limits_do_not_worsen_the_existing_fit(self):
        # The unconstrained solution is (0,2). Clipping that to (0,1) would be
        # worse than the source (0.2,1), which is already the bounded optimum.
        source=np.asarray([[[.2],[1.]]]);uv=np.asarray([[.25,.5],[.5,.5]]);target=np.asarray([[0.],[1.]])
        result,proof=fields.fit_bilinear_cut(source,uv,target,np.ones((1,2),bool))
        before=np.mean((fields.bilinear_gpu_rows(source,uv)-target)**2)
        after=np.mean((fields.bilinear_gpu_rows(result,uv)-target)**2)
        self.assertLessEqual(after,before+1e-12)
        np.testing.assert_allclose(result,source,rtol=0,atol=1e-9)
        self.assertGreater(proof['projectedIterations'],0)
        self.assertTrue(np.all((result>=0)&(result<=1)))

    def test_cut_transports_donor_tangent_rotation_when_normals_already_agree(self):
        source=path.with_name('generate.py')
        scope: dict[str,Any]=dict(np=np,copy=copy,math=math)
        nodes: list[ast.stmt]=[node for node in ast.parse(source.read_text(encoding='utf-8')).body if isinstance(node,ast.FunctionDef)]
        exec(compile(ast.Module(body=nodes,type_ignores=[]),str(source),'exec'),scope)
        def packet(y,tangent) -> dict[str,Any]:
            return dict(vertexCount=3,vertices=[-1.,0.,0.,1.,0.,0.,0.,y,0.],normals=[0.,0.,1.]*3,
                tangents=list(tangent)*3,weights=[1.,0.,0.,0.]*3,boneIndices=[0,0,0,0]*3,
                bindPoses=np.eye(4).ravel().tolist(),uv=[],subMeshes=[[0,1,2]],
                shapes=[dict(name='Musculature',frames=[dict(weight=100.,vertices=[0.]*9,normals=[0.]*9,tangents=[0.]*9)])],
                bounds=[0.,0.,0.,2.,2.,1.])
        # Distinct chart orientation and handedness must remain native at rest.
        receiver=packet(1.,[.6,.8,0.,-1.]);donor=packet(-1.,[1.,0.,0.,1.])
        donor['shapes'][0]['frames'][0]['tangents']=[-1.,1.,0.]*3
        result,proof=scope['overlap_skin_boundary'](receiver,donor,[(0,0),(1,1)],np.eye(4),follow_donor_shading=True)
        frame=result['shapes'][0]['frames'][0]
        rest=np.asarray(receiver['tangents']).reshape(-1,4)
        posed=rest[:,:3]+np.asarray(frame['tangents']).reshape(-1,3)
        np.testing.assert_allclose(posed[:2],[[-.8,.6,0.]]*2,rtol=0,atol=1e-7)
        np.testing.assert_array_equal(posed[2],rest[2,:3])
        np.testing.assert_array_equal(frame['normals'],receiver['shapes'][0]['frames'][0]['normals'])
        for key in ('normals','tangents','uv','weights','boneIndices','bindPoses','subMeshes'):
            self.assertEqual(result[key],receiver[key])
        self.assertEqual(frame['weight'],100.)
        self.assertTrue(proof['posedTangentsFollowDonor'])
        self.assertEqual(proof['changedShadingFrameVertices'],2)

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


class SkinNormalCutContracts(unittest.TestCase):
    def test_bilinear_normal_cut_fits_shader_product_and_keeps_locked_rgba(self):
        source=np.tile([.5,.5,1.,1.],(8,8,1));source[:,::2,0]=1.;source[:,::2,3]=.5
        ownership=np.zeros((8,8));ownership[2:5,[7,0]]=1.;ownership[2:5,[6,1]]=.5
        uv=np.asarray([[0.,.375],[1.,.375],[0.,.4375],[0.,.5]])
        target=np.tile([.13,-.21],(len(uv),1))
        result,proof=fields.fit_bilinear_normal_cut(source,uv,target,ownership)
        np.testing.assert_array_equal(result[ownership==0],source[ownership==0])
        sampled=fields.unpack_skin_normal(fields.bilinear_gpu_rows(result,uv),1.)
        np.testing.assert_allclose(sampled[:,:2],target,rtol=0,atol=1/4096)
        self.assertLess(proof['componentMaximumAfter'],proof['componentMaximumBefore'])
        self.assertEqual(proof['unrepresentableQueries'],0)
        self.assertTrue(proof['unownedRgbaExact'])

    def test_locked_normal_cut_reports_unrepresentable_alpha_without_editing(self):
        source=np.tile([.5,.5,1.,1.],(4,4,1));uv=np.asarray([[.5,.5]])
        result,proof=fields.fit_bilinear_normal_cut(source,uv,np.asarray([[.3,-.2]]),np.zeros((4,4)))
        np.testing.assert_array_equal(result,source)
        self.assertEqual(proof['unrepresentableQueries'],1)
        self.assertEqual(proof['changedTexels'],0)
        self.assertEqual(proof['componentMaximumAfter'],.3)

    def test_empty_normal_cut_preserves_its_original_packing(self):
        source=np.tile([.5,.5,1.,1.],(4,4,1))
        result,proof=fields.fit_bilinear_normal_cut(source,np.empty((0,2)),np.empty((0,2)),np.ones((4,4)))
        np.testing.assert_array_equal(result,source)
        self.assertEqual(proof['queries'],0)
        self.assertEqual(proof['changedTexels'],0)

    def test_normal_cut_respects_each_uv_tangent_frame_and_native_handedness(self):
        points=np.asarray([[-.02,-.02,0.],[.02,-.02,0.],[.02,.02,0.],[-.02,.02,0.]])
        uv=np.asarray([[.1,.1],[.9,.1],[.9,.9],[.1,.9]])
        edges=np.asarray([[0,1],[1,2],[2,3],[3,0]]);normals=np.tile([0.,0.,1.],(4,1))
        head=fields.BoundaryAtlas(points,uv,normals,np.tile([1.,0.,0.,1.],(4,1)),edges)
        flat=np.tile([1.,.5,1.,.5],(4,4,1));donor=np.tile([1.,.55,1.,.6],(4,4,1))
        queries=(points+np.roll(points,-1,axis=0))*.5
        for hand in (-1.,1.):
            body=fields.BoundaryAtlas(points,uv,normals,np.tile([0.,1.,0.,hand],(4,1)),edges)
            result,proof=fields.continuous_cut_normal(np.tile([.5,.5],(4,1)),queries,np.asarray([1.,1.,1.,0.]),head,body,flat,donor,1.,1.)
            xy=np.column_stack((result[:,0]*result[:,3],result[:,1]))*2-1
            expected=np.tile([-.1*hand,.2],(3,1))
            np.testing.assert_allclose(xy[:3],expected,rtol=0,atol=1e-12)
            np.testing.assert_array_equal(result[3],flat[0,0])
            self.assertEqual(proof['clippedQueries'],0)
            self.assertLessEqual(proof['maximumPhysicalPairError'],1e-12)

    def test_normal_cut_rejects_unrepresentable_material_scales(self):
        for scale in (0.,-1.,float('nan'),float('inf')):
            with self.subTest(scale=scale),self.assertRaisesRegex(ValueError,'positive finite material scales'):
                fields.continuous_cut_normal(np.empty((0,2)),np.empty((0,3)),np.empty(0),None,None,None,None,scale,1.)


class SurfaceToneContracts(unittest.TestCase):



    def test_bc7_reencoding_changes_rgb_indices_without_changing_alpha_or_unowned_blocks(self):
        from PIL import Image
        bits=(1<<5)|(255<<58)
        for i,value in enumerate([0,1,2,3]*4):bits|=value<<(97+(2*i-1 if i else 0))
        block=bits.to_bytes(16,'little');payload=block*2
        original=np.asarray(Image.frombytes('RGBA',(8,4),payload,'bcn',7))
        self.assertEqual(len(np.unique(original[:,:,3])),4)
        target=original.copy()
        for y in range(4):
            for x in range(4):target[y,x,:3]=[202,162,122]if(x+y)%2==0 else[30,60,90]
        mask=np.zeros((4,8),bool);mask[:,:4]=True
        fixed,_=fields.fit_bc7_rgb_field(payload,8,4,target,mask,maximum_rgb_error=16)
        fixed_pixels=np.asarray(Image.frombytes('RGBA',(8,4),fixed,'bcn',7))
        self.assertGreater(np.abs(fixed_pixels[:,:,:3].astype(int)-target[:,:,:3]).max(),16)
        encoded,proof=fields.fit_bc7_rgb_field(payload,8,4,target,mask,maximum_rgb_error=16,allow_reencoding=True)
        actual=np.asarray(Image.frombytes('RGBA',(8,4),encoded,'bcn',7))
        np.testing.assert_array_equal(actual[:,:,3],original[:,:,3])
        self.assertEqual(encoded[16:],block)
        self.assertLessEqual(np.abs(actual[:,:,:3].astype(int)-target[:,:,:3]).max(),2)
        self.assertEqual(proof['reencodedRgbBlocks'],1)
        self.assertFalse(proof['partitionsAndIndicesRetained'])

    def test_bc7_rgb_reencoding_retains_an_unrepresentable_source_alpha_codebook(self):
        from PIL import Image
        bits=(1<<6)|(127<<56)|(1<<64)
        for i in range(16):bits|=i<<(65+(4*i-1 if i else 0))
        block=bits.to_bytes(16,'little');source=np.asarray(Image.frombytes('RGBA',(4,4),block,'bcn',7))
        self.assertEqual(len(np.unique(source[:,:,3])),16)
        target=np.tile([180,100,40,255],(16,1))
        self.assertEqual(fields.reencode_bc7_rgb_block(block,target),block)

    def test_source_calibration_reuses_only_the_same_producer_contract(self):
        builder_spec=importlib.util.spec_from_file_location('face_calibration_builder',path.with_name('build_models.py'))
        assert builder_spec and builder_spec.loader
        builder=importlib.util.module_from_spec(builder_spec);builder_spec.loader.exec_module(builder)
        with tempfile.TemporaryDirectory() as directory:
            root=Path(directory);raw=root/'Main.rgba8';raw.write_bytes(bytes([100,80,70,255]))
            old=root/'Body.json';old.write_text(json.dumps(dict(bodyTarget=[.4,.3,.2])))
            contexts=root/'Contexts.json';contexts.write_text(json.dumps(dict(rows=[])))
            preparation=root/'Preparation.json'
            preparation.write_text(json.dumps(dict(head='SourceFace',task=dict(nativeRaw=str(raw),nativeRawSha256=builder.sha(raw)),
                sourceInput=dict(coefficient=dict(path=str(old),sha256=builder.sha(old))),
                sourceContext=dict(nativeHeadFingerprint='head',nativeBodyFingerprint='body',lod=0,samplerSourceInputKey='main'))))
            output=root/'Calibration.json';job=dict(preparation=str(preparation),contexts=str(contexts),output=output)
            inputs=[dict(path=str(p),sha256=builder.sha(p))for p in (preparation,contexts,raw,old,path)]
            signature=hashlib.sha256('\n'.join(inspect.getsource(function)for function in
                (builder.prepare_resource_tone_coefficient,builder.resource_tone_context,builder.material_main_inputs)).encode('utf8')).hexdigest()
            cached=dict(status='PREPARED_SOURCE_CALIBRATION',inputs=inputs,coefficient=dict(policy='surface-local-v4'),codeSignature=signature)
            output.write_text(json.dumps(cached))
            self.assertTrue(builder.prepare_resource_tone_coefficient(job)['reused'])
            cached['codeSignature']='previous-producer';output.write_text(json.dumps(cached))
            with self.assertRaisesRegex(ValueError,'unique native face context'):
                builder.prepare_resource_tone_coefficient(job)

    def test_explicit_skin_reference_keeps_a_dark_skin_region_distinct_from_dark_pigment(self):
        positions=np.asarray([[0.,0.,0.],[.02,0.,0.],[.02,.04,0.],[0.,.04,0.],
                              [.20,0.,0.],[.22,0.,0.],[.22,.02,0.],[.20,.02,0.]])
        uv=np.asarray([[.1,.1],[.4,.1],[.4,.9],[.1,.9],[.6,.1],[.9,.1],[.9,.9],[.6,.9]])
        head=dict(vertices=positions,uv=uv,normal=np.tile([0.,0.,1.],(8,1)),
                  triangles=np.asarray([[0,1,2],[0,2,3],[4,5,6],[4,6,7]]))
        pixels=np.ones((16,16,4));pixels[:,:8,:3]=[.4,.22,.18];pixels[:,8:,:3]=[.2,.15,.14]
        baseline=fields.surface_tone_references(head,pixels,[.42,.24,.2])
        selected=fields.surface_tone_references(head,pixels,[.42,.24,.2],reference_eligibility=np.r_[np.zeros(4),np.ones(4)])
        np.testing.assert_allclose(np.asarray(selected['references'])[:4],np.asarray(baseline['references'])[:4],rtol=0,atol=0)
        np.testing.assert_allclose(np.asarray(selected['references'])[4:],np.tile([.2,.15,.14],(4,1)),rtol=0,atol=1e-12)
        self.assertGreater(np.asarray(baseline['references'])[4:,0].mean(),.3)
        self.assertEqual(selected['explicitSkinReferenceSamples'],8)
        self.assertIsNotNone(selected['referenceEligibilitySha256'])

    def test_skin_reference_eligibility_rejects_foreign_vertex_domains(self):
        head=dict(vertices=np.asarray([[0.,0.,0.],[.02,0.,0.],[0.,.02,0.]]),
                  uv=np.asarray([[.1,.1],[.9,.1],[.1,.9]]),normal=np.tile([0.,0.,1.],(3,1)),
                  triangles=np.asarray([[0,1,2]]))
        for invalid in ([1.,1.],[1.,-1.,0.],[1.,float('nan'),0.],[1.,1.01,0.]):
            with self.subTest(eligibility=invalid),self.assertRaisesRegex(ValueError,'one finite weight per vertex'):
                fields.surface_tone_references(head,np.ones((4,4,4))*.5,[.4,.3,.2],reference_eligibility=invalid)

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
