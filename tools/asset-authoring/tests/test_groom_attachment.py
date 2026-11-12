import importlib.util
import copy
import json
from pathlib import Path
import tempfile
import unittest
from typing import Any

import numpy as np


class GroomAttachmentTests(unittest.TestCase):
    def test_finished_fibres_keep_authored_curved_support_instead_of_restoring_native_planes(self):
        path=Path(__file__).resolve().parents[2]/'blender-models/CharacterHair/GenerateFlowCoverageSweptProfile.py'
        spec=importlib.util.spec_from_file_location('composed_groom_support',path)
        assert spec is not None and spec.loader is not None
        owner=importlib.util.module_from_spec(spec);spec.loader.exec_module(owner)
        source: dict[str,Any]=dict(name='Curved',vertexCount=6,vertices=[0.,0.,0.,1.,0.,0.,0.,1.,0.,.1,.1,.01,.2,.1,.02,.1,.2,.01],
            normals=[0.,0.,1.]*6,tangents=[1.,0.,0.,1.]*6,weights=[1.,0.,0.,0.]*6,boneIndices=[0]*24,colors=[],
            uv=[dict(channel=0,dimension=2,values=[0.,0.,0.,0.]*6),dict(channel=2,dimension=4,values=[0.,0.,0.,1.]*6)],bindPoses=np.eye(4).ravel().tolist(),sourceFingerprint='0'*64,
            subMeshes=[[3,4,5]],indexFormat=0,bounds=[.5,.5,.02,1.,1.,.1],
            shapes=[dict(name='Wind',index=0,frames=[dict(weight=100.,vertices=[0.,0.,.001]*6,normals=[0.]*18,tangents=[0.]*18)])])
        fine=copy.deepcopy(source);fine['subMeshes']=[[0,1,2,3,4,5]]
        fine['uv'][1]['values']=[0.,0.,0.,1.]*3+[0.,0.,17.,1.]*3
        binding=dict(matrix=np.eye(4).ravel().tolist(),vertices=[dict(anchor=0,native=False,tangentW=1.)for _ in range(6)])
        saved=copy.deepcopy(source);packet,attachment,proof=owner.compose_finished_source_groom(source,fine,binding)
        self.assertEqual(source,saved);self.assertEqual(packet['subMeshes'],[[3,4,5,6,7,8]])
        self.assertEqual(packet['vertices'][:18],source['vertices'])
        self.assertEqual(packet['shapes'][0]['frames'][0]['vertices'][:18],source['shapes'][0]['frames'][0]['vertices'])
        self.assertEqual([r['anchor']for r in attachment['vertices'][:6]],list(range(6)))
        self.assertTrue(all(r['native']for r in attachment['vertices'][:6]))
        self.assertTrue(proof['sourceCoreTopologyExact']);self.assertEqual(proof['addedFineVertices'],3)

    def test_new_fibers_keep_later_source_fitting_in_every_physical_frame(self):
        path=Path(__file__).resolve().parents[2]/'blender-models/CharacterHair/GenerateFlowCoverageSweptProfile.py'
        spec=importlib.util.spec_from_file_location('retained_groom_fitting',path)
        assert spec is not None and spec.loader is not None
        owner=importlib.util.module_from_spec(spec);spec.loader.exec_module(owner)
        native: dict[str,Any]=dict(name='Native',vertexCount=3,vertices=[0.,0.,0.,1.,0.,0.,0.,1.,0.],normals=[0.,0.,1.]*3,
            tangents=[1.,0.,0.,1.]*3,weights=[1.,0.,0.,0.]*3,boneIndices=[0]*12,colors=[],uv=[],
            bindPoses=np.eye(4).ravel().tolist(),subMeshes=[[0,1,2]],indexFormat=0,bounds=[.5,.5,0.,1.,1.,.002],
            shapes=[dict(name='Wind',index=0,frames=[dict(weight=100.,vertices=[0.,0.,.001]*3,normals=[0.]*9,tangents=[0.]*9)])])
        packet=copy.deepcopy(native);packet.update(vertexCount=6,vertices=native['vertices']+[.1,.1,.01,.2,.1,.01,.1,.2,.01],
            normals=[0.,0.,1.]*6,tangents=[1.,0.,0.,1.]*6,weights=[1.,0.,0.,0.]*6,boneIndices=[0]*24,
            subMeshes=[[0,1,2,3,4,5]],uv=[dict(channel=2,dimension=4,values=[0.,0.,0.,1.]*3+[0.,0.,17.,1.1]*3)])
        for frame in packet['shapes'][0]['frames']:
            for key in('vertices','normals','tangents'):frame[key]*=2
        current=copy.deepcopy(packet);current['vertices']=(np.asarray(current['vertices']).reshape(-1,3)+[0.,0.,.003]).ravel().tolist()
        current['normals']=[0.,-1.,0.]*6
        current['shapes'][0]['frames'][0]['vertices']=[0.,0.,.002]*6
        binding=dict(vertices=[dict(anchor=i,native=True,surface=None)for i in range(3)]+[
            dict(anchor=0,native=False,surface=dict(triangle=[0,1,2],barycentric=[.7,.2,.1]))for _ in range(3)])
        saved=copy.deepcopy(packet);result=owner.retain_source_fitting(packet,native,binding,current)
        np.testing.assert_allclose(np.asarray(result['vertices']).reshape(-1,3)-np.asarray(packet['vertices']).reshape(-1,3),[[0.,0.,.003]]*6,rtol=0,atol=1e-15)
        np.testing.assert_allclose(np.asarray(result['shapes'][0]['frames'][0]['vertices']).reshape(-1,3),[[0.,0.,.002]]*6,rtol=0,atol=1e-15)
        self.assertEqual(result['normals'],packet['normals']);self.assertEqual(result['tangents'],packet['tangents']);self.assertEqual(packet,saved)
        bounds=np.asarray(result['bounds']);low=bounds[:3]-bounds[3:]*.5;high=bounds[:3]+bounds[3:]*.5
        positions=np.asarray(result['vertices']).reshape(-1,3)
        for points in(positions,positions+np.asarray(result['shapes'][0]['frames'][0]['vertices']).reshape(-1,3)):
            self.assertTrue(np.all(points>=low));self.assertTrue(np.all(points<=high))

    def test_native_groom_without_generated_uvs_is_measured_as_core(self):
        path=Path(__file__).resolve().parents[2]/'blender-models/CharacterHair/GenerateFlowCoverageSweptProfile.py'
        spec=importlib.util.spec_from_file_location('native_core_crown_owner',path)
        assert spec is not None and spec.loader is not None
        owner=importlib.util.module_from_spec(spec);spec.loader.exec_module(owner)
        packet=dict(vertices=[-.01,.999,-.01,.01,.999,-.01,0.,.999,.01],subMeshes=[[0,1,2]],
                    uv=[dict(channel=0,values=[0.,0.,1.,0.,.5,1.])])
        scalp=np.array([[[-.02,1.,-.02],[.02,1.,-.02],[0.,1.,.02]]])
        with np.errstate(invalid='raise'):
            proof=owner.measure_source_crown(packet,np.eye(4).ravel(),scalp,[None])
        self.assertTrue(proof['nativeCoreRoleImplicit'])
        self.assertGreater(proof['maximumLift'],0)

    def test_crown_position_field_preserves_shading_skin_and_shape_channels(self):
        path=Path(__file__).resolve().parents[2]/'blender-models/CharacterHair/GenerateFlowCoverageSweptProfile.py'
        spec=importlib.util.spec_from_file_location('groom_position_owner',path)
        assert spec is not None and spec.loader is not None
        owner=importlib.util.module_from_spec(spec);spec.loader.exec_module(owner)
        packet=dict(vertexCount=3,indexFormat=0,vertices=[-.01,.999,-.01,.01,.999,-.01,0.,.999,.01],
            normals=[0.,1.,0.]*3,tangents=[1.,0.,0.,1.]*3,weights=[1.,0.,0.,0.]*3,
            boneIndices=[0]*12,colors=[],bindPoses=np.eye(4).ravel().tolist(),subMeshes=[[0,1,2]],
            uv=[dict(channel=0,dimension=2,values=[0.,0.,0.,0.]*3)],shapes=[],bounds=[0.,.999,0.,.02,.002,.02])
        before=copy.deepcopy(packet)
        result=owner.apply_crown_field(packet,np.eye(4).ravel(),.003,.98,1.)
        self.assertEqual(packet,before)
        for key in ('normals','tangents','weights','boneIndices','colors','bindPoses','uv','subMeshes','shapes'):
            self.assertEqual(result[key],packet[key])
        self.assertGreater(min(result['vertices'][1::3]),1.001)
        self.assertEqual(owner.apply_crown_field(packet,np.eye(4).ravel(),0.,.98,1.),packet)
        with self.assertRaisesRegex(ValueError,'bounded'):
            owner.apply_crown_field(packet,np.eye(4).ravel(),.009,.98,1.)

    def test_transparent_native_roots_do_not_drive_the_crown_fit(self):
        from PIL import Image
        path=Path(__file__).resolve().parents[2]/'blender-models/CharacterHair/GenerateFlowCoverageSweptProfile.py'
        spec=importlib.util.spec_from_file_location('groom_alpha_owner',path)
        assert spec is not None and spec.loader is not None
        owner=importlib.util.module_from_spec(spec);spec.loader.exec_module(owner)
        with tempfile.TemporaryDirectory()as directory:
            source=Path(directory)/'source.png';pixels=np.full((2,2,4),255,np.uint8);pixels[:,:,3]=0
            Image.fromarray(pixels).save(source)
            row=dict(path=str(source),sha256=owner.sha(source),scale=[1,1],offset=[0,0],cutoff=.5)
            sampler=owner.texture_sampler(row)
            self.assertIsNotNone(sampler)
            assert sampler is not None
            self.assertEqual(sampler(np.zeros((2,3,2))).shape,(2,3,4))
            uv=np.full((3,2),.25);faces=np.array([[0,1,2]])
            visible=owner.native_core_visibility(uv,faces,[0],[row])
            scalp=np.array([[[-.02,1.,-.02],[.02,1.,-.02],[0.,1.,.02]]])
            points=np.array([[-.01,.999,-.01],[.01,.999,-.01],[0.,.999,.01]])
            fitted,_,proof=owner.fit_crown_clearance(points,faces,scalp,core_visibility=visible)
            np.testing.assert_array_equal(fitted,points)
            self.assertEqual(proof['maximumLift'],0)

    def test_crown_fit_clears_the_scalp_without_moving_the_lower_hairline(self):
        path=Path(__file__).resolve().parents[2]/'blender-models/CharacterHair/GenerateFlowCoverageSweptProfile.py'
        spec=importlib.util.spec_from_file_location('groom_crown_owner',path)
        assert spec is not None and spec.loader is not None
        owner=importlib.util.module_from_spec(spec);spec.loader.exec_module(owner)
        scalp=np.array([[[-.02,1.,-.02],[.02,1.,-.02],[0.,1.,.02]]])
        points=np.array([[-.01,.999,-.01],[.01,.999,-.01],[0.,.999,.01],[0.,.95,0.]])
        fitted,derivative,proof=owner.fit_crown_clearance(points,[[0,1,2]],scalp)
        self.assertGreaterEqual(float(fitted[:3,1].min()),1.0015-1e-9)
        np.testing.assert_array_equal(fitted[3],points[3])
        np.testing.assert_array_equal(fitted[:,[0,2]],points[:,[0,2]])
        self.assertTrue(np.all(derivative>=0))
        self.assertEqual(proof['addedVertices'],0)
        clear=points.copy();clear[:3,1]=1.01
        unchanged,_,clear_proof=owner.fit_crown_clearance(clear,[[0,1,2]],scalp)
        np.testing.assert_array_equal(unchanged,clear)
        self.assertEqual(clear_proof['maximumLift'],0)
        layered=np.concatenate((clear[:3],points[:3]))
        outer,_,layer_proof=owner.fit_crown_clearance(layered,[[0,1,2],[3,4,5]],scalp)
        np.testing.assert_array_equal(outer,layered)
        self.assertTrue(layer_proof['outerSurfaceOnly'])
        deep=points.copy();deep[:3,1]=.991
        rooted,_,root_proof=owner.fit_crown_clearance(deep,[[0,1,2]],scalp)
        np.testing.assert_array_equal(rooted,deep)
        self.assertGreater(root_proof['deepRootSamplesPreserved'],0)

    def test_native_source_ownership_is_independent_of_the_fine_render_layout(self):
        path=Path(__file__).resolve().parents[2]/'blender-models/CharacterHair/GenerateFlowCoverageSweptProfile.py'
        spec=importlib.util.spec_from_file_location('native_groom_surface_owner',path)
        assert spec is not None and spec.loader is not None
        owner=importlib.util.module_from_spec(spec);spec.loader.exec_module(owner)
        native=dict(vertexCount=3,vertices=[0.,0.,0.,1.,0.,0.,0.,1.,0.],normals=[0.,0.,1.]*3,
                    tangents=[1.,0.,0.,1.]*3,colors=[],weights=[1.,0.,0.,0.]*3,
                    boneIndices=[0]*12,bindPoses=np.eye(4).ravel().tolist(),subMeshes=[[0,1,2]],shapes=[])
        packet=copy.deepcopy(native);binding=dict(vertices=[dict(native=True)]*3);field=np.zeros((3,4))
        self.assertTrue(owner.native_flow_surface(packet,native,binding,field))
        packet['vertices']=[.001,0.,0.,1.,0.,0.,0.,1.,0.]
        with self.assertRaises(ValueError):owner.native_flow_surface(packet,native,binding,field)
        packet=copy.deepcopy(native);field[:,2]=17
        self.assertFalse(owner.native_flow_surface(packet,native,binding,field))

    def test_shared_native_mesh_keeps_each_lod_packet_after_save_and_reload(self):
        path=Path(__file__).resolve().parents[2]/'blender-models/CharacterHair/GenerateFlowCoverageSweptProfile.py'
        spec=importlib.util.spec_from_file_location('groom_packet_ownership',path)
        assert spec is not None and spec.loader is not None
        owner=importlib.util.module_from_spec(spec);spec.loader.exec_module(owner)
        with tempfile.TemporaryDirectory() as directory:
            saved=[]
            for lod in (1,2):
                packet=dict(nativeFingerprint='shared-native-mesh',sourceFingerprint='authored-lod-'+str(lod),
                            vertices=[0.,0.,float(lod)])
                saved.append(owner.save_groom_packet(Path(directory),dict(asset='Gnome_Male_HairShape_H',lod=lod),packet))
            self.assertNotEqual(saved[0],saved[1])
            for path,lod in zip(saved,(1,2)):
                packet=json.loads(path.read_text(encoding='utf-8'))
                self.assertEqual(packet['sourceFingerprint'],'authored-lod-'+str(lod))
                self.assertEqual(packet['vertices'],[0.,0.,float(lod)])
            with self.assertRaises(ValueError):
                owner.save_groom_packet(Path(directory),dict(asset='../other',lod=1),{})

    def test_source_uv_chart_pieces_keep_their_own_taper_and_physical_orientation(self):
        path=Path(__file__).resolve().parents[2]/'blender-models/CharacterHair/GenerateFlowCoverageSweptProfile.py'
        spec=importlib.util.spec_from_file_location('source_ribbon_chart_pieces',path)
        assert spec is not None and spec.loader is not None
        owner=importlib.util.module_from_spec(spec);spec.loader.exec_module(owner)
        source=np.array([[[0.,0.,0.],[1.,0.,0.],[0.,1.,0.]],[[0.,0.,0.],[0.,1.,0.],[-1.,0.,0.]]])
        normals=np.tile([0.,0.,1.],(2,3,1));tangents=np.tile([1.,0.,0.],(2,3,1));records=[]
        for index,y in enumerate((.1,.2,.3,.5,.6,.7)):
            v=index/5;taper=.025+.975*max(0,np.sin(np.pi*v))**.62
            for side in (-1,1):
                records.append(dict(anchor=0,surface=dict(triangle=[0,1,2],barycentric=[.8-y,.2,y]),
                    offset=[.2+side*.02*taper,y,.01],normal=[side*np.sin(.2),0.,np.cos(.2)],tangent=[1.,0.,0.],
                    groom=[(side+1)*.5,v,49.,1.2]))
        before=copy.deepcopy(records)
        result=owner.source_ribbon_pieces(records,[(0,1),(1,2),(3,4),(4,5)],source,normals,tangents,np.cross(normals,tangents),soft_coverage=True)
        assert result is not None
        values,links,frames,pieces=result
        self.assertEqual(pieces,2);self.assertEqual(records,before)
        self.assertEqual([record['surface']for record in values],[record['surface']for record in before])
        for first in (0,6):
            self.assertEqual(values[first]['groom'][1],0.)
            self.assertEqual(values[first+4]['groom'][1],1.)
        triangles=np.array([[a*2,a*2+1,b*2]for a,b in links]+[[a*2+1,b*2+1,b*2]for a,b in links])
        points=frames[0][:,triangles]
        signed=np.sum(np.cross(points[:,:,1]-points[:,:,0],points[:,:,2]-points[:,:,0])*frames[1][:,triangles].mean(axis=2),axis=2)
        self.assertTrue(np.all(signed>1e-14))

    def test_ribbon_width_follows_a_morphed_centerline_with_unchanged_source_tangents(self):
        path=Path(__file__).resolve().parents[2]/'blender-models/CharacterHair/GenerateFlowCoverageSweptProfile.py'
        spec=importlib.util.spec_from_file_location('deformed_ribbon_frame',path)
        assert spec is not None and spec.loader is not None
        owner=importlib.util.module_from_spec(spec);spec.loader.exec_module(owner)
        source=np.array([[[0.,0.,0.],[1.,0.,0.],[0.,1.,0.]],[[0.,0.,0.],[0.,1.,0.],[-1.,0.,0.]]])
        saved=source.copy();normals=np.tile([0.,0.,1.],(2,3,1));tangents=np.tile([1.,0.,0.],(2,3,1))
        records=[]
        angle=.2
        for y in (.2,.4,.6):
            for side in (-1,1):
                records.append(dict(anchor=0,surface=dict(triangle=[0,1,2],barycentric=[.8-y,.2,y]),
                    offset=[.2+side*.05,y,.01],normal=[side*np.sin(angle),0.,np.cos(angle)],tangent=[1.,0.,0.]))
        triangles=np.array([[0,1,2],[1,3,2],[2,3,4],[3,5,4]])
        old=owner.source_fiber_frames(records,source,normals,tangents,np.cross(normals,tangents))
        points=old[0][1,triangles]
        self.assertLess(float(np.linalg.norm(np.cross(points[:,1]-points[:,0],points[:,2]-points[:,0]),axis=1).max()),1e-8)
        framed=owner.source_ribbon_frames(records,triangles,source,normals,tangents,np.cross(normals,tangents))
        assert framed is not None
        positions,directions,_,proof=framed
        self.assertEqual(proof['widthScale'],1.)
        self.assertGreater(proof['minimumSignedArea'],.01)
        np.testing.assert_allclose(np.linalg.norm(directions,axis=2),1.,atol=1e-12)
        width=positions.reshape(2,3,2,3)
        np.testing.assert_allclose(np.linalg.norm(width[:,:,1]-width[:,:,0],axis=2),.1,atol=1e-12)
        np.testing.assert_array_equal(source,saved)

    def test_replacing_a_transported_mesh_rebases_its_cloth_frame(self):
        path=Path(__file__).resolve().parents[2]/'blender-models/CharacterHair/GenerateFlowCoverageSweptProfile.py'
        spec=importlib.util.spec_from_file_location('rebased_fine_attachment',path)
        assert spec is not None and spec.loader is not None
        owner=importlib.util.module_from_spec(spec);spec.loader.exec_module(owner)
        packet=dict(name='Native',vertexCount=3,vertices=[0.,0.,0.,1.,0.,0.,0.,1.,0.],
            normals=[0.,0.,1.]*3,tangents=[1.,0.,0.,1.]*3,weights=[1.,0.,0.,0.]*3,
            boneIndices=[0]*12,colors=[],uv=[],bindPoses=np.eye(4).ravel().tolist(),
            subMeshes=[[0,1,2]],shapes=[],indexFormat=0,bounds=[.5,.5,0.,1.,1.,0.])
        packet['sourceFingerprint']=owner.fingerprint(packet)[0]
        binding=dict(schemaVersion=3,sourceVertexCount=3,sourceFingerprint=packet['sourceFingerprint'],
            matrix=np.eye(4).ravel().tolist(),cloth=True,
            vertices=[dict(native=True,anchor=i,role='volume-support',slot=0,uv0=[0.,0.],offset=[0.,0.,0.],
                normal=[0.,0.,1.],tangent=[1.,0.,0.],tangentW=1.,surface=None)for i in range(3)])
        source=dict(packet,normals=[0.,-1.,0.]*3)
        result,attachment,proof=owner.standalone_cloth_attachment(packet,packet,binding,source)
        self.assertEqual(result['sourceFingerprint'],packet['sourceFingerprint'])
        self.assertEqual(proof['nativeClothFrameRowsRebased'],3)
        self.assertTrue(all(not vertex['native']for vertex in attachment['vertices']))
        p,n,t,b=owner.native_frames(source,binding['matrix'])
        for index,vertex in enumerate(attachment['vertices']):
            frame=np.array([t[0,index],b[0,index],n[0,index]])
            np.testing.assert_allclose(np.array(vertex['normal'])@frame,[0.,0.,1.],atol=1e-12)

    def test_curved_attachment_reduces_displacement_before_losing_a_sheet(self):
        path=Path(__file__).resolve().parents[2]/'blender-models/CharacterHair/GenerateFlowCoverageSweptProfile.py'
        spec=importlib.util.spec_from_file_location('bounded_fine_attachment',path)
        assert spec is not None and spec.loader is not None
        owner=importlib.util.module_from_spec(spec);spec.loader.exec_module(owner)
        points=np.array([[.1,.1,0.],[.3,.1,0.],[.1,.3,0.],[.3,.3,0.]])
        normals=np.tile([0.,0.,1.],(4,1));tangents=np.tile([1.,0.,0.],(4,1))
        packet=dict(vertices=points.ravel().tolist(),normals=normals.ravel().tolist(),
            tangents=np.c_[tangents,np.ones(4)].ravel().tolist(),shapes=[],
            subMeshes=[[0,1,2,1,3,2]],uv=[dict(channel=2,values=[0.,0.,49.,1.1]*4)])
        source=np.array([[[0.,0.,0.],[1.,0.,0.],[0.,1.,0.]]])
        source_n=np.tile([0.,0.,1.],(1,3,1));source_t=np.tile([1.,0.,0.],(1,3,1))
        records=[dict(surface=dict(triangle=[0,1,2],barycentric=[1-p[0]-p[1],p[0],p[1]]))for p in points]
        proposed=points.copy();proposed[0]=[.8,.8,0.]
        _,_,_,posed,proof=owner.bound_fine_attachment(packet,np.arange(4),records,proposed,normals,tangents,
            source,source_n,source_t,np.cross(source_n,source_t),np.eye(3))
        self.assertEqual(proof['initialInvalidSheets'],1)
        self.assertEqual(proof['finalInvalidSheets'],0)
        self.assertEqual(proof['reducedSheets'],1)
        self.assertTrue(proof['allFineGeometryRetained'])
        self.assertGreater(proof['minimumSignedArea'],0.)
        self.assertEqual(posed[0].shape,(1,4,3))


if __name__=='__main__':
    unittest.main()
