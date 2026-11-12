"""Facial source ownership and the required authored-guide boundary."""
import contextlib
import copy
import gzip
import importlib.util
import io
import json
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


class FaceProductionContracts(unittest.TestCase):
    def test_first_publication_link_failure_preserves_the_original_error_and_catalog(self):
        with tempfile.TemporaryDirectory()as folder:
            root=Path(folder);original=root/'original';candidate=root/'candidate';current=root/'public'
            for path in(original,candidate,current):path.mkdir()
            for path in(original,current):(path/'CharacterModels.manifest.json').write_bytes(b'original manifest')
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
            self.assertEqual((current/'CharacterModels.manifest.json').read_bytes(),b'original manifest')
            self.assertEqual((current/'actor.bundle').read_bytes(),b'original resource')
            self.assertFalse(list(current.glob('*.new-*')))

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
                textures=[dict(object=1,raw=dict(path=str(raw),sha256=build.sha(raw)))])
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
            for path in(original,candidate):build.write(path/'CharacterModels.manifest.json',manifest)
            row=dict(file='actor.bundle',bytes=len(b'accepted source'),sha256=digest,source=str(original/'actor.bundle'),sourceSha256=digest,
                allRetainedObjectFieldsExactAfterReferenceRebasing=False)
            row['source']=str(root/'retired/actor.bundle')
            approved=original.parent/'ResourceAssembly.json'
            build.write(approved,dict(status='PASS_STANDALONE_CHARACTER_RESOURCES',originalModelDependenciesInherited=False,artAccepted=True,catalogSha256=build.sha(original/'CharacterModels.manifest.json'),proofs=[row]))
            proof=dict(status='PASS_STANDALONE_CHARACTER_RESOURCES',originalModelDependenciesInherited=False,resources=1,bundles=1,
                bytes=row['bytes'],inputs=[],proofs=[row],preparedSource=dict(assembly=dict(path=str(approved),sha256=build.sha(approved)),
                    catalog=dict(path=str(original/'CharacterModels.manifest.json'),sha256=build.sha(original/'CharacterModels.manifest.json'))))
            build.write(candidate/'ResourceAssembly.json',proof)
            quality=dict(resourceAssembly=dict(path=str(candidate/'ResourceAssembly.json'),sha256=build.sha(candidate/'ResourceAssembly.json')),
                sourceManifestSha256=build.sha(original/'CharacterModels.manifest.json'),candidateManifestSha256=build.sha(candidate/'CharacterModels.manifest.json'))
            with mock.patch.object(build,'checked_catalog_path',side_effect=lambda path,name=None:Path(path)/name if name else Path(path)),\
                    mock.patch.object(build,'checked_source_path',side_effect=Path),mock.patch.object(build,'atomic_publish_catalog'),contextlib.redirect_stdout(io.StringIO()):
                build.publish_resource_catalog(candidate,current,original,manifest,quality)
                (candidate/'actor.bundle').write_bytes(b'unverified different bytes')
                row.update(sha256=build.sha(candidate/'actor.bundle'),bytes=(candidate/'actor.bundle').stat().st_size,
                    allRetainedObjectFieldsExactAfterReferenceRebasing=True)
                proof['bytes']=row['bytes'];manifest['bundles'][0]['sha256']=row['sha256']
                build.write(candidate/'CharacterModels.manifest.json',manifest);build.write(candidate/'ResourceAssembly.json',proof)
                quality['candidateManifestSha256']=build.sha(candidate/'CharacterModels.manifest.json')
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
            build.write(catalog/'CharacterModels.manifest.json',dict(schemaVersion=3,meshes=[],materials=[],textureRemaps=[],bundles=[]))
            output=root/'Packs'
            with mock.patch.object(build,'checked_catalog_path',side_effect=lambda path,name=None:Path(path)/name if name else Path(path)):
                with self.assertRaisesRegex(ValueError,'retained source support'):
                    build.create_resource_packs(catalog/'CharacterModels.manifest.json',output,'2026.11.12.1')
            self.assertFalse(output.exists())

    def test_resource_publication_preserves_the_verified_catalog_digest(self):
        with tempfile.TemporaryDirectory()as folder:
            root=Path(folder);original=root/'original';candidate=root/'candidate';current=root/'public'
            for path in(original,candidate,current):path.mkdir()
            payload=b'unchanged-owned-resource'
            for path in(original,candidate,current):(path/'actor.bundle').write_bytes(payload)
            manifest=dict(schemaVersion=3,recipeVersion='revision',meshes=[],materials=[],textureRemaps=[],
                bundles=[dict(file='actor.bundle',sha256=build.sha(candidate/'actor.bundle'))],
                resources=[dict(source='Actor/Prefab.prefab',type='GameObject',bundles=[dict(file='actor.bundle')])])
            build.write(original/'CharacterModels.manifest.json',manifest)
            proof=dict(status='PASS_STANDALONE_CHARACTER_RESOURCES',originalModelDependenciesInherited=False,
                resources=1,bundles=1,bytes=len(payload),inputs=[],proofs=[dict(file='actor.bundle',bytes=len(payload),
                    source=str(original/'actor.bundle'),sourceSha256=build.sha(original/'actor.bundle'),
                    sha256=build.sha(candidate/'actor.bundle'),allRetainedObjectFieldsExactAfterReferenceRebasing=True)])
            build.write(candidate/'ResourceAssembly.json',proof)
            quality: dict[str,Any]=dict(resourceAssembly=dict(path=str(candidate/'ResourceAssembly.json'),sha256=build.sha(candidate/'ResourceAssembly.json')))
            for raw in(json.dumps(manifest).encode(),(json.dumps(manifest,indent=2)+'\r\n').encode()):
                (candidate/'CharacterModels.manifest.json').write_bytes(raw)
                quality['candidateManifestSha256']=build.sha(candidate/'CharacterModels.manifest.json')
                with mock.patch.object(build,'checked_catalog_path',side_effect=lambda path,name=None:Path(path)/name if name else Path(path)),\
                        mock.patch.object(build,'atomic_publish_catalog')as publish,contextlib.redirect_stdout(io.StringIO()):
                    build.publish_resource_catalog(candidate,current,original,manifest,quality)
                    self.assertEqual(publish.call_args.args[3],raw)
                    self.assertEqual(publish.call_args.args[-1]['publishedManifestSha256'],quality['candidateManifestSha256'])

    def test_short_physical_gaussian_matches_its_finite_zero_padded_kernel(self):
        production,_=build.source_owner()
        values=np.random.default_rng(412).random((17,19))
        for sigma in(.2,.75,1.5,4.,8.):
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
        duplicate=dict(bindPoses=native[:2].transpose(0,2,1).ravel().tolist())
        with self.assertRaisesRegex(ValueError,'incompatible or ambiguous'):
            art.skin_palette_frame(duplicate,dict(bindPoses=np.eye(4).ravel().tolist()),['Neck','Neck'],['Neck'])

    def test_material_texture_preloads_follow_each_actual_prefab_owner(self):
        import character_resources as resources
        pointer=lambda key:dict(m_FileID=0,m_PathID=key)
        tree=dict(m_PreloadTable=[pointer(3),pointer(7),pointer(9)],m_Container=[
            ('first',dict(asset=pointer(3),preloadIndex=0,preloadSize=2)),
            ('second',dict(asset=pointer(9),preloadIndex=2,preloadSize=1))],m_Dependencies=['retained.bundle'])
        original=copy.deepcopy(tree);changed=resources.material_texture_preloads(tree,[(7,15),(7,15),(9,16)])
        self.assertEqual(tree,original)
        self.assertEqual(changed['m_PreloadTable'][:3],tree['m_PreloadTable'])
        for (name,entry),wanted in zip(changed['m_Container'],([3,7,15],[9,16])):
            self.assertEqual([p['m_PathID']for p in changed['m_PreloadTable'][entry['preloadIndex']:entry['preloadIndex']+entry['preloadSize']]],wanted)
        self.assertEqual(changed['m_Dependencies'],tree['m_Dependencies'])
        with self.assertRaisesRegex(ValueError,'outside every declared prefab'):
            resources.material_texture_preloads(tree,[(33,15)])

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
        skin=dict(bones=['Root/b_lip_upper_c','Root/b_cheek_l'])
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

    def test_adaptive_skin_shares_uv_edge_motion_and_keeps_original_cut_edges(self):
        production,art=build.source_owner()
        points=np.asarray([[-.1,0.,0.],[.1,0.,0.],[0.,0.,.2],[.1,0.,0.],[-.1,0.,0.],[0.,0.,-.02],[.2,0.,.2]],np.float32)
        normals=np.asarray([[.2,.98,0.],[-.2,.98,0.],[0.,.98,.2],[-.2,.98,0.],[.2,.98,0.],[0.,1.,0.],[0.,.98,.2]])
        normals/=np.linalg.norm(normals,axis=1)[:,None]
        tangents=np.asarray([[1.,0.,0.,1.]]*7);tangents[3:6]=[-1.,0.,0.,-1.]
        uv=np.asarray([[0.,0.,0.,0.],[1.,0.,0.,0.],[.5,1.,0.,0.],[.2,.3,0.,0.],[.8,.3,0.,0.],[.4,1.,0.,0.],[1.,1.,0.,0.]])
        packet: dict[str,Any]=dict(name='UvChartSkin',nativeReadable=True,vertexCount=7,indexFormat=0,vertices=points.ravel().tolist(),
            normals=normals.ravel().tolist(),tangents=tangents.ravel().tolist(),colors=[],weights=[1.,0.,0.,0.]*7,
            boneIndices=[0,0,0,0,1,0,0,0,0,0,0,0,1,0,0,0,0,0,0,0,0,0,0,0,1,0,0,0],bindPoses=np.tile(np.eye(4).ravel(),2).tolist(),
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
                    bones=['Root/Bip001 Head'], materials=[dict(shader='Lux/Human/Skin')])
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
            for mode in (None, 'skinned-bind-proxy', 'serialized-mesh-data'):
                with self.subTest(sourceReadMode=mode):
                    current = dict(packet)
                    if mode is not None:
                        current['sourceReadMode'] = mode
                    with gzip.open(path, 'wt', encoding='utf-8') as stream:
                        json.dump(current, stream)
                    if mode == 'serialized-mesh-data':
                        self.assertEqual(production.native_packet(dict(meshFile=str(path))), current)
                    else:
                        with self.assertRaisesRegex(ValueError, 'not a native rest source'):
                            production.native_packet(dict(meshFile=str(path)))

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


if __name__ == '__main__':
    unittest.main()
