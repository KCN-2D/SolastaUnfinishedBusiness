import importlib.util
import ast
import copy
import json
from pathlib import Path
import tempfile
import sys
from types import SimpleNamespace
import unittest
from unittest import mock
from typing import Any

import numpy as np


class GroomAttachmentTests(unittest.TestCase):
    @staticmethod
    def build_owner():
        path=Path(__file__).resolve().parents[2]/'blender-models/CharacterFaces/build_models.py'
        spec=importlib.util.spec_from_file_location('groom_build_contract_owner',path)
        assert spec is not None and spec.loader is not None
        owner=importlib.util.module_from_spec(spec);spec.loader.exec_module(owner)
        return owner

    def test_groom_producer_ledger_keeps_versions_and_rejects_unproven_archives(self):
        owner=self.build_owner()
        with tempfile.TemporaryDirectory()as directory:
            root=Path(directory);sources=[]
            for index in range(9):
                path=root/str(index)/'producer.py';path.parent.mkdir();path.write_text('version='+str(index),'utf-8');sources.append(path)
            records=owner.resource_producer_archives(root/'first',sources)
            self.assertEqual(len(records),9);self.assertEqual(len({row['archive']['path']for row in records}),9)
            saved=copy.deepcopy(records);sources[0].write_text('updated live producer','utf-8')
            inherited=owner.resource_producer_archives(root/'next',retained=[*records,*records])
            self.assertEqual(inherited,saved);self.assertEqual(records,saved)
            updated=owner.resource_producer_archives(root/'second',[sources[0]],records)
            self.assertEqual(len(updated),10)
            self.assertNotEqual(updated[0]['original']['sha256'],updated[-1]['original']['sha256'])
            bad=copy.deepcopy(records);bad[0]['archive']['sha256']='a'*64
            with self.assertRaisesRegex(ValueError,'content authority'):owner.resource_producer_archives(root/'bad',retained=bad)
            bad=copy.deepcopy(records);bad[0]['archive']['sha256']=bad[0]['original']['sha256']='a'*64
            with self.assertRaisesRegex(ValueError,'archive changed'):owner.resource_producer_archives(root/'bad',retained=bad)

    def test_groom_archive_ledger_replays_old_pin_without_accepting_unknown_versions(self):
        owner=self.build_owner()
        with tempfile.TemporaryDirectory()as directory:
            root=Path(directory);live=root/'helper.py';live.write_text('old producer','utf-8')
            retained=owner.resource_producer_archives(root/'prepared',[live]);old=retained[0]['original']
            live.write_text('new producer','utf-8')
            def document(name,value):
                path=root/name;owner.write(path,value);return dict(path=str(path),sha256=owner.sha(path))
            before='1'*64;after='2'*64
            original=dict(file='part.bundle',objects=8,sha256=before,allRetainedObjectFieldsExactAfterReferenceRebasing=True)
            source=dict(assembly=document('baseline.json',dict(proofs=[original])),
                catalog=document('catalog.json',dict(bundles=[dict(file=original['file'],sha256=before)])))
            receipt=dict(source=dict(sha256=before),target=dict(sha256=after),file=original['file'])
            operation=dict(kind='source-owned-groom-surface',beforeSha256=before,sha256=after,
                proof=document('receipt.json',receipt),authority=dict(inputs=[old]))
            row=dict(original,sha256=after,allRetainedObjectFieldsExactAfterReferenceRebasing=False,appearanceFinishing=[operation])
            assembly=dict(preparedSource=source,retainedSourceInputs=retained);verified={}
            with mock.patch.object(owner,'ROOT',root),mock.patch.object(owner,'validate_owned_groom_surface')as replay:
                owner.validate_prepared_resource(row,assembly,verified);replay.assert_called_once()
                self.assertEqual(verified[('producer-archive',str(live.resolve()),old['sha256'])],Path(retained[0]['archive']['path']))
                broken=copy.deepcopy(row);broken['appearanceFinishing'][0]['authority']['inputs'][0]['sha256']='a'*64
                with self.assertRaisesRegex(ValueError,'prepared resource input changed'):
                    owner.validate_prepared_resource(broken,assembly,{})

    def test_native_lod_uses_its_renderer_family_when_only_some_lods_bind_custom_materials(self):
        self.build_owner()
        import character_resources as resources

        def pointer(identifier, file_id=0):
            return dict(m_FileID=file_id, m_PathID=identifier)

        def obj(identifier, kind, tree, name=''):
            return SimpleNamespace(path_id=identifier, type=SimpleNamespace(name=kind),
                                   read_typetree=lambda: tree, peek_name=lambda: name)

        def level(*renderers):
            return dict(renderers=[dict(renderer=pointer(renderer)) for renderer in renderers])

        file = SimpleNamespace(objects={
            1: obj(1, 'Material', dict(m_SavedProperties=dict(m_Floats=[('_CoreResponse', .4)]))),
            10: obj(10, 'Mesh', {}, 'HairShape_H_LOD0'),
            11: obj(11, 'Mesh', {}, 'HairShape_H_LOD1'),
            12: obj(12, 'Mesh', {}, 'FaceShape_LOD1'),
            13: obj(13, 'Mesh', {}, 'HairShape_H_LOD2'),
            20: obj(20, 'SkinnedMeshRenderer', dict(m_Mesh=pointer(10), m_Materials=[pointer(1)])),
            21: obj(21, 'SkinnedMeshRenderer', dict(m_Mesh=pointer(11), m_Materials=[pointer(30, 4)])),
            22: obj(22, 'SkinnedMeshRenderer', dict(m_Mesh=pointer(12), m_Materials=[pointer(31, 4)])),
            23: obj(23, 'SkinnedMeshRenderer', dict(m_Mesh=pointer(13), m_Materials=[pointer(30, 4)])),
            25: obj(25, 'LODGroup', dict(m_LODs=[level(20), level(21, 22)])),
        })
        self.assertEqual(resources.groom_mesh_identifiers(file), {10})
        self.assertEqual(resources.groom_mesh_identifiers(file, include_native_lods=True), {10, 11})

    def test_native_cloth_fixed_points_follow_render_child_hashes_not_virtual_vertex_numbers(self):
        owner=self.build_owner();file=SimpleNamespace(name='native',objects={})
        pointer=lambda identifier:dict(m_FileID=0,m_PathID=identifier)
        def obj(identifier,kind,tree):
            value=SimpleNamespace(path_id=identifier,type=SimpleNamespace(name=kind),read_typetree=lambda:copy.deepcopy(tree))
            file.objects[identifier]=value
        obj(1,'MonoBehaviour',dict(clothSelection=pointer(2),virtualDeformer=pointer(3)))
        record=dict(selectData=[2,1,1],vertexHashList=[10,20,30])
        obj(2,'MonoBehaviour',dict(selectionList=[record,record]))
        obj(3,'MonoBehaviour',dict(deformer=dict(renderDeformerList=[pointer(4),pointer(5)])))
        obj(4,'MonoBehaviour',dict(deformer=dict(sharedMesh=pointer(20),meshData=pointer(6))))
        obj(5,'MonoBehaviour',dict(deformer=dict(sharedMesh=pointer(21),meshData=pointer(7))))
        obj(6,'MonoBehaviour',dict(vertexHashList=[30,10,20]));obj(7,'MonoBehaviour',dict(vertexHashList=[10,20,30]))
        obj(20,'Mesh',{});obj(21,'Mesh',{})
        resolve=lambda current,p:(file,file.objects[p['m_PathID']])
        saved=copy.deepcopy(record);proof=owner.native_groom_fixed_vertices(file,20,3,resolve)
        self.assertEqual(proof['fixedVertexIds'],[1]);self.assertEqual(len(proof['selectionOwners']),1)
        self.assertEqual(proof['selectionOwners'][0]['renderDeformer']['object'],4)
        self.assertEqual(proof['selectionOwners'][0]['hashRemappedVertices'],3)
        self.assertEqual(owner.native_groom_fixed_vertices(file,21,3,resolve)['fixedVertexIds'],[0])
        self.assertEqual(record,saved)
        with self.assertRaisesRegex(ValueError,'render vertex domain'):owner.native_groom_fixed_vertices(file,20,4,resolve)

    def test_carrier_fitting_requires_the_completed_declared_domain_and_keeps_the_raw_pin(self):
        owner=self.build_owner();surface=dict(flowCarrierCells=dict(sourceDomainCoverageExact=True),firstVertex=30,vertices=20)
        row=dict(materials=[dict(shader='Custom/FakeAnisoHair')],curvedCoreSurface=surface)
        profile=dict(layeredAtlasCards=True,carrierSurface=dict(widthMultiplier=1.25),supportTerminals=dict(maximumSupportOffsetMetres=.008))
        expected=dict(materials=row['materials'],maximumSupportOffsetMetres=.008,carrierSurface=surface)
        saved=copy.deepcopy((row,profile));actual=owner.groom_retained_support(row,profile)
        self.assertEqual(actual,expected);actual['carrierSurface']['firstVertex']=26
        self.assertEqual((row,profile),saved)
        for change,message in((dict(curvedCoreSurface=None),'completed physical'),(dict(curvedCoreSurface={}),'completed physical')):
            with self.subTest(change=change),self.assertRaisesRegex(ValueError,message):owner.groom_retained_support(dict(row,**change),profile)
        with self.assertRaisesRegex(ValueError,'production profile'):
            owner.groom_retained_support(row,dict(profile,carrierSurface=None))
        with self.assertRaisesRegex(ValueError,'layered surface owner'):
            owner.groom_retained_support(row,dict(profile,layeredAtlasCards=False))
        old=dict(profile);old.pop('carrierSurface')
        self.assertEqual(owner.groom_retained_support(dict(row,curvedCoreSurface=None),old),
            dict(materials=row['materials'],maximumSupportOffsetMetres=.008))

    def test_group_loader_keeps_full_producer_helpers_and_separates_frozen_epochs(self):
        path=Path(__file__).resolve().parents[2]/'blender-models/CharacterHair/PrepareGroup.py'
        node=next(row for row in ast.parse(path.read_text('utf-8-sig')).body if isinstance(row,ast.FunctionDef)and row.name=='load_producer')
        scope=dict(Path=Path,sha=self.generator_owner().sha,importlib=importlib)
        exec(compile(ast.Module(body=[node],type_ignores=[]),str(path),'exec'),scope)
        actual=scope['load_producer'](path.with_name('GenerateFlowCoverageSweptProfile.py'))
        self.assertIs(actual.prove_row.__globals__['retained_carrier_allocation'],actual.retained_carrier_allocation)
        self.assertEqual(actual.__file__,str(path.with_name('GenerateFlowCoverageSweptProfile.py')))
        with tempfile.TemporaryDirectory()as directory:
            first=Path(directory)/'first/producer.py';first.parent.mkdir()
            second=Path(directory)/'second/producer.py';second.parent.mkdir()
            template="def nested_helper():return {value}\ndef prove_row():return nested_helper()\nif __name__=='__main__':raise RuntimeError('main must not run')\n"
            first.write_text(template.format(value=13),'utf-8');second.write_text(template.format(value=29),'utf-8')
            a=scope['load_producer'](first);b=scope['load_producer'](second)
            self.assertEqual((a.prove_row(),b.prove_row()),(13,29));self.assertNotEqual(a.__name__,b.__name__)
            self.assertTrue(Path(a.__file__).samefile(first));self.assertTrue(Path(b.__file__).samefile(second))
            original=scope['sha'];calls=0
            def changed(value):
                nonlocal calls
                calls+=1;return original(value)if calls==1 else'0'*64
            scope['sha']=changed
            with self.assertRaisesRegex(ValueError,'changed during loading'):scope['load_producer'](first)

    def test_composed_groom_replays_material_texture_union_and_manifest_slot_owners(self):
        owner=self.build_owner();import character_resources as resources
        with tempfile.TemporaryDirectory()as directory:
            root=Path(directory);catalog=root/'catalog';catalog.mkdir()
            def payload(name,data):
                path=root/name;path.parent.mkdir(parents=True,exist_ok=True)
                path.write_bytes(data if isinstance(data,bytes)else json.dumps(data).encode())
                return dict(path=str(path),sha256=owner.sha(path))
            source=payload('catalog/source.bundle',b'source');target=payload('saved.bundle',b'saved')
            geometry=payload('geometry.bundle',b'geometry');fine=payload('fine.bundle',b'fine');compiled=payload('compiled.bundle',b'compiled')
            producer=payload('GenerateFlowCoverageSweptProfile.py',b'# pinned producer')
            packet=dict(sourceFingerprint='finished');attachment=dict(rows=[1,2]);facts=dict(sourceSupportRetained=True)
            binding=payload('binding.bytes',b'input binding');cloth=payload('cloth.bytes',b'encoded binding')
            row=dict(mesh=3,asset='fixture',source=source,fibers=fine,binding=binding,clothBinding=cloth,
                packet=payload('packet.json',packet),compiledBundle=compiled,compiledName='Compiled',composition=facts,
                materialSlots=[dict(sourceSlot=0,targetSlot=1)])
            source_manifest=dict(bundles=[dict(file='source.bundle',sha256=source['sha256'])],resources=[
                dict(source='actor',type='GameObject',bundles=[dict(file='source.bundle')],parameters=[
                    dict(renderer='Rig/Hair',slot=0,set='Groom',property='_Color'),
                    dict(renderer='Rig/Hair',slot=0,set='Skin',property='_Other')]),
                dict(source='fixed',type='GameObject',bundles=[dict(file='source.bundle')]),
                dict(source='controller',type='RuntimeAnimatorController',bundles=[dict(file='source.bundle')])])
            source_catalog=payload('catalog/manifest.json',source_manifest)
            assembly=payload('assembly.json',dict(catalogSha256=source_catalog['sha256'],proofs=[dict(file='source.bundle',newCab='CAB_source',sha256=source['sha256'])]))
            fibers=payload('fibers.json',dict(proofs=[dict(file='fine.bundle',sha256=fine['sha256'])]))
            plan=payload('plan.json',dict(source=assembly,producer=producer,rows=[row]))
            final=copy.deepcopy(source_manifest);final['bundles'][0]['sha256']=target['sha256']
            before=copy.deepcopy(final['resources'][0]['parameters'])
            final['resources'][0]['parameters'] += [dict(value,slot=1)for value in before]
            final_catalog=payload('final.json',final)
            parameter_updates=[dict(source='actor',before=before,after=copy.deepcopy(final['resources'][0]['parameters']))]
            material_updates=[dict(renderer=2,addedSlots=[dict(materialPathId=9)])]
            texsource=payload('atlas.bundle',b'atlas')
            texture_receipt=dict(textures=[dict(object=12,source=dict(texsource,object=100)),dict(object=13,source=dict(texsource,object=101))],
                materialUpdates=[dict(pathId=9,property=key,before=dict(m_FileID=1,m_PathID=42),after=dict(m_FileID=0,m_PathID=value))
                                 for key,value in(('_GroomColor',12),('_GroomNormal',13))])
            receipt=dict(source=source,target=target,file='source.bundle',rows=[row],composition=dict(plan=plan,fibers=fibers),
                engineReport=payload('report.json',dict(success=True)),materialFloatUpdates=[],
                parameterCatalogs=dict(source=source_catalog,target=final_catalog,files=['source.bundle']),
                rendererParameterUpdates=parameter_updates,rendererMaterialUpdates=material_updates,
                materialSlotUpdates=[dict(meshPathId=3,slots=row['materialSlots'])],geometryTarget=geometry,
                materialTextures=texture_receipt,objectsAdded=[9,12,13],changedObjects=[1,2,3,9,12,13])
            mesh=lambda name:SimpleNamespace(packet=dict(sourceFingerprint=name),read_typetree=lambda:dict(name=name),peek_name=lambda:'Compiled')
            sf=SimpleNamespace(objects={3:mesh('source')});df=SimpleNamespace(objects={3:mesh('donor')});cf=SimpleNamespace(objects={7:mesh('finished')})
            cf.objects[7].type=SimpleNamespace(name='Mesh')
            skin=SimpleNamespace(assets_file=sf,read_typetree=lambda:dict(m_Mesh=dict(m_FileID=0,m_PathID=3)))
            converter=SimpleNamespace(decode_binding=lambda _:attachment,read=lambda path:json.loads(Path(path).read_text()),
                compose_finished_source_groom=lambda *_,**__:(packet,attachment,facts),
                encode_legacy_binding=lambda _,path:Path(path).write_bytes(b'encoded binding'))
            files={Path(source['path']):sf,Path(fine['path']):df,Path(compiled['path']):cf}
            def verified():return {('groom-surface-owner',producer['sha256']):converter,('validated-resource',fibers['sha256'],'fine.bundle'):True}
            with mock.patch.object(resources,'load_serialized_bundle',side_effect=lambda path,**_:(None,files[Path(path)])),\
                    mock.patch.object(resources,'groom_mesh_identifiers',return_value={3}),\
                    mock.patch.object(owner,'export_current_mesh',side_effect=lambda obj:copy.deepcopy(obj.packet)),\
                    mock.patch.object(owner,'resource_prefab_skins',return_value=([('Rig/Hair',skin)],[])),\
                    mock.patch.object(resources,'prepare_standalone_part',return_value=dict(target=geometry,rendererMaterialUpdates=material_updates,objectsAdded=[9],changedObjects=[1,2,3,9]))as prepare,\
                    mock.patch.object(resources,'validate_owned_material_textures'),\
                    mock.patch.object(resources,'replace_owned_material_textures',return_value=dict(target=target,objectsAdded=[12,13],materialUpdates=[dict(pathId=9)],metadataObject=1)):
                owner.validate_owned_groom_surface(receipt,[producer],verified())
                for field,value,message in(('objectsAdded',[],'object ledger'),('objectsAdded',[9,12],'object ledger'),
                        ('changedObjects',[2,3,9,12,13],'object ledger'),('rendererMaterialUpdates',[],'clone ownership'),
                        ('rendererParameterUpdates',[],'parameter ledger')):
                    broken=dict(receipt,**{field:value})
                    with self.subTest(field=field,value=value),self.assertRaisesRegex(ValueError,message):
                        owner.validate_owned_groom_surface(broken,[producer],verified())
                for mutate in(lambda doc:doc['resources'][0]['parameters'].pop(),
                              lambda doc:doc['resources'][2].update(parameters=before)):
                    broken=copy.deepcopy(final);mutate(broken);pin=payload('broken.json',broken)
                    bad=dict(receipt,parameterCatalogs=dict(receipt['parameterCatalogs'],target=pin))
                    with self.assertRaisesRegex(ValueError,'unowned resources'):
                        owner.validate_owned_groom_surface(bad,[producer],verified())
                cache=verified();owner.validate_owned_groom_surface(receipt,[producer],cache)
                Path(final_catalog['path']).write_text('{}')
                with self.assertRaisesRegex(ValueError,'input changed'):
                    owner.validate_owned_groom_surface(receipt,[producer],cache)
                # Geometry-only composition keeps its cloned material without requiring an atlas.
                geometry_manifest=copy.deepcopy(final);geometry_manifest['bundles'][0]['sha256']=geometry['sha256']
                geometry_catalog=payload('geometry-manifest.json',geometry_manifest)
                geometry_only={key:value for key,value in receipt.items()if key not in('geometryTarget','materialTextures')}
                geometry_only.update(target=geometry,objectsAdded=[9],changedObjects=[1,2,3,9],
                    parameterCatalogs=dict(receipt['parameterCatalogs'],target=geometry_catalog))
                owner.validate_owned_groom_surface(geometry_only,[producer],verified())
                # Historical composition with no new slots or objects has no additions field.
                legacy_row={key:value for key,value in row.items()if key!='materialSlots'}
                legacy={key:value for key,value in geometry_only.items()if key not in
                    ('objectsAdded','materialSlotUpdates','parameterCatalogs','rendererParameterUpdates','rendererMaterialUpdates')}
                legacy.update(rows=[legacy_row],changedObjects=[3])
                prepare.return_value=dict(target=geometry,changedObjects=[3])
                owner.validate_owned_groom_surface(legacy,[producer],verified())

    def test_groom_added_objects_are_counted_in_the_existing_resource_ledger(self):
        owner=self.build_owner();before='1'*64;after='2'*64
        original=dict(file='part.bundle',objects=8,sha256=before,allRetainedObjectFieldsExactAfterReferenceRebasing=True)
        source=dict(assembly=dict(path='source.json',sha256='3'*64),catalog=dict(path='catalog.json',sha256='4'*64))
        proof=dict(path='receipt.json',sha256='5'*64)
        receipt=dict(source=dict(sha256=before),target=dict(sha256=after),file=original['file'],objectsAdded=[9,12,13])
        documents={'source.json':dict(proofs=[original]),'catalog.json':dict(bundles=[dict(file=original['file'],sha256=before)]),'receipt.json':receipt}
        hashes={pin['path']:pin['sha256']for pin in[*source.values(),proof]}
        operation=dict(kind='source-owned-groom-surface',beforeSha256=before,sha256=after,proof=proof,authority=dict(inputs=[]))
        row=dict(original,objects=11,sha256=after,allRetainedObjectFieldsExactAfterReferenceRebasing=False,appearanceFinishing=[operation])
        with mock.patch.object(owner,'checked_source_path'),mock.patch.object(owner,'sha',side_effect=lambda path:hashes[str(path)]),\
                mock.patch.object(owner,'read',side_effect=lambda path:copy.deepcopy(documents[str(path)])),\
                mock.patch.object(owner,'validate_owned_groom_surface')as replay:
            owner.validate_prepared_resource(row,dict(preparedSource=source),{});replay.assert_called_once()
            with self.assertRaisesRegex(ValueError,'object or dependency ownership'):
                owner.validate_prepared_resource(dict(row,objects=10),dict(preparedSource=source),{})
            # Receipts from unchanged geometry-only transactions predate the addition ledger.
            del receipt['objectsAdded'];row['objects']=8
            owner.validate_prepared_resource(row,dict(preparedSource=source),{})

    def test_fine_material_slots_match_each_renderer_without_requiring_shared_material_ids(self):
        owner=self.build_owner()
        pointer=lambda key:dict(m_FileID=0,m_PathID=key)
        def material(name,color,texture):
            return dict(m_Name=name,m_Shader=dict(m_FileID=1,m_PathID=9),
                m_SavedProperties=dict(m_Floats=[['_Strength',1.]],m_Colors=[['_Color',color]],
                    m_TexEnvs=[['_GroomColor',dict(m_Texture=pointer(texture))]]))
        trees={10:material('First core',.2,100),11:material('First fine',.2,101),
               20:material('Second core',.7,200),21:material('Second fine',.7,201)}
        renderer=lambda keys:dict(m_Mesh=pointer(3),m_Materials=[pointer(key)for key in keys])
        trees.update({30:renderer([10,11]),31:renderer([20,21])})
        objects={key:SimpleNamespace(type=SimpleNamespace(name='Material'if key<30 else'SkinnedMeshRenderer'),
                    read_typetree=lambda key=key:copy.deepcopy(trees[key]))for key in trees}
        objects[3]=SimpleNamespace(type=SimpleNamespace(name='Mesh'))
        source=dict(vertexCount=6,subMeshes=[[0,1,2],[3,4,5]],uv=[dict(channel=2,
            values=[value for role in(0,0,0,17,17,17)for value in(0,0,role,0)])])
        finished=dict(source,subMeshes=[[0,1,2,3,4,5]])
        original=copy.deepcopy(trees)
        with mock.patch.object(owner,'export_current_mesh',return_value=source):
            self.assertEqual(owner.groom_fine_material_slots(SimpleNamespace(objects=objects),3,finished),
                             [dict(sourceSlot=0,targetSlot=1)])
            self.assertEqual(trees,original)
            trees[31]['m_Materials'][1]=pointer(20)
            self.assertEqual(owner.groom_fine_material_slots(SimpleNamespace(objects=objects),3,finished),
                             [dict(sourceSlot=0,targetSlot=2)])
            trees[31]=copy.deepcopy(original[31])
            # Another LOD may share the fine object, but a carrier consumer prevents reuse.
            other=dict(vertexCount=3,subMeshes=[[0,1,2]],uv=[dict(channel=2,values=[0.,0.,0.,0.]*3)])
            objects[41]=SimpleNamespace(type=SimpleNamespace(name='Mesh'))
            trees[40]=dict(m_Mesh=pointer(41),m_Materials=[pointer(11)])
            objects[40]=SimpleNamespace(type=SimpleNamespace(name='SkinnedMeshRenderer'),read_typetree=lambda:copy.deepcopy(trees[40]))
            with mock.patch.object(owner,'export_current_mesh',side_effect=lambda obj:source if obj is objects[3]else other):
                self.assertEqual(owner.groom_fine_material_slots(SimpleNamespace(objects=objects),3,finished),
                                 [dict(sourceSlot=0,targetSlot=2)])
                other['uv'][0]['values']=[0.,0.,49.,0.]*3
                self.assertEqual(owner.groom_fine_material_slots(SimpleNamespace(objects=objects),3,finished),
                                 [dict(sourceSlot=0,targetSlot=1)])
            del objects[40],objects[41],trees[40]
            trees[21]['m_SavedProperties']['m_Floats'][0][1]=.5
            self.assertEqual(owner.groom_fine_material_slots(SimpleNamespace(objects=objects),3,finished),
                             [dict(sourceSlot=0,targetSlot=2)])
            trees[31]['m_Materials'].append(pointer(21))
            with self.assertRaisesRegex(ValueError,'overlays'):
                owner.groom_fine_material_slots(SimpleNamespace(objects=objects),3,finished)
            trees[31]['m_Materials'].pop();trees[20]=copy.deepcopy(original[20])
            trees[31]['m_Materials'][0]['m_FileID']=1
            with self.assertRaisesRegex(ValueError,'local source material'):
                owner.groom_fine_material_slots(SimpleNamespace(objects=objects),3,finished)

    @staticmethod
    def generator_owner():
        path=Path(__file__).resolve().parents[2]/'blender-models/CharacterHair/GenerateFlowCoverageSweptProfile.py'
        spec=importlib.util.spec_from_file_location('groom_generation_owner',path)
        assert spec is not None and spec.loader is not None
        owner=importlib.util.module_from_spec(spec);spec.loader.exec_module(owner)
        return owner

    def test_source_material_surface_does_not_use_fine_seed_alpha_or_uv_rectangle(self):
        owner=self.generator_owner()
        from PIL import Image
        with tempfile.TemporaryDirectory()as directory:
            root=Path(directory);main=root/'main.png';mask=root/'mask.png'
            rgba=np.full((8,8,4),255,np.uint8);rgba[:,:,3]=0;Image.fromarray(rgba).save(main)
            mask_pixels=np.zeros((8,8,4),np.uint8);mask_pixels[0,7,0]=255;Image.fromarray(mask_pixels).save(mask)
            def pin(path):return dict(path=str(path),sha256=owner.sha(path),width=8,height=8,scale=[1.,1.],offset=[0.,0.],
                encoding='Existing immutable ExportTexture linear-GPU ARGB32 PNG, mip0 only; synthetic fixture')
            sampler=dict(m_FilterMode=1,m_WrapU=0,m_WrapV=0)
            material=dict(shader='Custom/FakeAnisoHair',maps=dict(_MainTex=pin(main)),sourceShaderTextureDefaults=dict(_Mask='black'))
            triangle=[[.125,.125],[.875,.125],[.125,.875]]
            domain=owner.SourceHairDomain(material);self.assertFalse(domain.hair(triangle));self.assertTrue(domain.surface_hair(triangle))
            material['maps']['_Mask']=pin(mask);material['sourceTextureSamplers']=dict(_Mask=sampler)
            domain=owner.SourceHairDomain(material);self.assertGreater(domain.range('_Mask',triangle)[1],.5)
            self.assertTrue(domain.surface_hair(triangle));self.assertFalse(domain.surface_hair([[.75,.75],[1.,.75],[.75,1.]]))
            del material['sourceTextureSamplers']
            with self.assertRaisesRegex(ValueError,'actual native sampler'):owner.SourceHairDomain(material).surface_hair(triangle)
            del material['maps']['_Mask'];del material['sourceShaderTextureDefaults']
            with self.assertRaisesRegex(ValueError,'actual shader authority'):owner.SourceHairDomain(material).surface_hair(triangle)

    def test_source_curve_geometry_refines_positions_without_refining_authored_normal_errors(self):
        owner=self.generator_owner();source=np.asarray([[0,1,2]]);p=np.tile([[[0.,0.,0.],[.03,0.,0.],[0.,.03,0.]]],(2,1,1));p[1,:,0]*=1.1
        n=np.tile([0.,0.,1.],(2,3,1));t=np.tile([1.,0.,0.],(2,3,1));control=np.zeros((2,1,6,3));control[:,:,3:,2]=.002
        normal=np.tile([0.,0.,1.],(2,1,6,1));material=dict(path='source-main',sha256='a'*64,scale=[1.,1.],offset=[0.,0.])
        prepared=[dict(triangles=source,sourceFaces=np.asarray([0]),controls=control,normalControls=normal,heldEdges=set(),flow=np.asarray([[1.,0.,0.]]),
            pattern={},phaseProof={},sharing={},fit={},normalProof={},sourceMain=material)]
        plan=owner.plan_source_curved_core(prepared,p,n,t,np.arange(3),200,.012,slot_triangles=[source],inverse=np.eye(3),native_positions=p)
        self.assertGreater(len(plan['vertices']),3);self.assertTrue(plan['flowCarrierCells']['sourceDomainCoverageExact'])
        values=plan['sourceQuadratic'];self.assertTrue(np.isfinite(values['points']).all());self.assertEqual(values['points'].shape[0],2)
        final=values['proofs'][0]['geometryRefinement'][-1];self.assertEqual(final['invalidChildPosePairs'],0);self.assertFalse(final['normalErrorUsedToRefine'])
        self.assertLessEqual(final['maximumChordBoundMetres'],owner.PROFILE['maximumChordErrorMetres'])
        area=sum(np.linalg.det(np.asarray([np.asarray(plan['vertices'][v]['barycentric'])[1:]for v in tri])[1:]-np.asarray(plan['vertices'][tri[0]]['barycentric'])[1:])*.5 for tri in plan['triangles'][0])
        self.assertAlmostEqual(area,.5)
        with self.assertRaises(OverflowError):owner.plan_source_curved_core(prepared,p,n,t,np.arange(3),2,.012,slot_triangles=[source],inverse=np.eye(3),native_positions=p)

    def test_source_curve_compiler_uses_main_pattern_and_complete_source_poses(self):
        owner=self.generator_owner()
        from PIL import Image
        with tempfile.TemporaryDirectory()as directory:
            path=Path(directory)/'main.png';image=np.full((16,16,4),255,np.uint8)
            image[:,:,:3]=np.broadcast_to((np.arange(16)*15).astype(np.uint8)[None,:,None],(16,16,3));Image.fromarray(image).save(path)
            material=dict(slot=0,shader='Custom/FakeAnisoHair',maps=dict(_MainTex=dict(path=str(path),sha256=owner.sha(path),width=16,height=16,scale=[1.,1.],offset=[0.,0.])),
                sourceTextureSamplers=dict(_MainTex=dict(m_FilterMode=1,m_WrapU=0,m_WrapV=0)))
            p=np.tile([[[0.,0.,0.],[.03,0.,0.],[0.,.03,0.],[.03,.03,0.]]],(2,1,1));p[1,:,0]*=1.05
            n=np.tile([0.,0.,1.],(2,4,1));t=np.tile([1.,0.,0.],(2,4,1));source=np.asarray([[0,1,2],[1,3,2]])
            native=dict(vertexCount=4,subMeshes=[source.ravel().tolist()],tangents=[1.,0.,0.,1.]*4,
                uv=[dict(channel=0,values=[0.,0.,0.,0.,1.,0.,0.,0.,0.,1.,0.,0.,1.,1.,0.,0.])])
            selected=[np.ones(2,bool)];settings=dict(widthMetres=.012,heightMetres=.0009,patternRadiusPixels=2)
            prepared=owner.prepare_source_curved_core(native,p,n,t,[material],selected,np.arange(4),dict(protectedVertexIds=[],contactVertexIds=[]),settings,native_positions=p)
            self.assertTrue(selected[0].any());self.assertTrue(prepared[0]['normalProof']['shaderSourceInterpolationStrengthRetained'])
            self.assertTrue(prepared[0]['fit']['beforeAndAfterSourceFittingChecked'])
            plan=owner.plan_source_curved_core(prepared,p,n,t,np.arange(4),1000,.012,slot_triangles=[source],inverse=np.eye(3),native_positions=p)
            self.assertEqual(plan['sourceQuadratic']['points'].shape[0],2);self.assertTrue(plan['sourceQuadratic']['proofs'][0]['sourceNormalOwner']['shaderSourceInterpolationStrengthRetained'])
            self.assertTrue(np.allclose(abs(plan['sourceQuadratic']['flow'][:,1]),1.,atol=1e-8))
            selected=[np.ones(2,bool)]
            fixed=owner.prepare_source_curved_core(native,p,n,t,[material],selected,np.arange(4),
                dict(protectedVertexIds=[],contactVertexIds=[],fixedVertexIds=[0,1,2,3]),settings,native_positions=p)
            self.assertTrue(selected[0].any(),'Fixed samples must not freeze the broad faces between them.')
            np.testing.assert_array_equal(fixed[0]['controls'][:,:,:3],0.)
            self.assertGreater(float(np.abs(fixed[0]['controls'][:,:,3:]).max()),0.)

    def test_declared_source_flow_survives_fine_nearest_and_preserves_unowned_alias(self):
        owner=self.resource_owner();count=7;channels=[dict(stream=0,offset=0,format=0,dimension=0)for _ in range(9)];channels[4]['dimension']=4
        source=np.asarray([[i/10,.2,0.,0.]for i in range(count)],dtype='<f4')
        tree=dict(m_BindPose=[],m_VertexData=dict(m_VertexCount=count,m_Channels=channels,m_DataSize=source.tobytes()))
        positions=[[0.,0.,0.],[1.,0.,0.],[0.,1.,0.],[0.,0.,.001],[1.,0.,.001],[0.,1.,.001],[0.,0.,0.]]
        uv=[[0.,0.,0.,1.],[1.,0.,0.,1.],[0.,1.,0.,1.],[0.,0.,17.,1.],[1.,0.,17.,1.],[0.,1.,17.,1.],[0.,0.,0.,1.]]
        mesh=SimpleNamespace(m_Vertices=positions,m_UV2=uv,m_Normals=[[0.,0.,1.]]*count,m_Tangents=[[1.,0.,0.,1.]]*count,
            get_triangles=lambda:[[[0,1,2],[3,4,5]]],src=SimpleNamespace(m_VertexData=SimpleNamespace(m_Channels=channels)),get_streams=lambda *_:[SimpleNamespace(offset=0,stride=16)])
        declaration=dict(vertices=[0,1,2],directions=[[1.,0.,0.]]*3,authority=dict(source=dict(path='fixture-main',sha256='a'*64),
            uvChannel=0,scale=[1.,1.],offset=[0.,0.],provenance=dict(sourceTriangles=[[0,1,2]])))
        original=copy.deepcopy((tree,declaration));proof=owner.bake_groom_vertex_flow(tree,mesh,declared_flow=declaration)
        result=np.frombuffer(tree['m_VertexData']['m_DataSize'],dtype='<f4').reshape(count,6)
        np.testing.assert_array_equal(result[:,:4],source);np.testing.assert_allclose(abs(result[:3,4:]),[[1.,0.]]*3,atol=1e-7)
        np.testing.assert_allclose(abs(result[3:,4:]),[[0.,1.]]*4,atol=1e-7)
        self.assertEqual(declaration,original[1]);self.assertEqual(proof['declaredSourceFlowVertices'],3);self.assertEqual(proof['sourceFlowAuthority'],declaration['authority'])
        for change in(dict(vertices=[0,0,2]),dict(directions=[[0.,0.,1.]]*3),dict(authority={})):
            bad=copy.deepcopy(original[0])
            with self.subTest(change=change),self.assertRaises(ValueError):owner.bake_groom_vertex_flow(bad,mesh,declared_flow=dict(declaration,**change))
            self.assertEqual(bad,original[0])

    def test_generation_payload_ownership_keeps_geometry_rows_and_excludes_inputs(self):
        owner=self.generator_owner()
        with tempfile.TemporaryDirectory()as directory:
            root=Path(directory);output=root/'generated';output.mkdir()
            packet=output/'mesh-packet.json';packet.write_text('{"vertices":[0,1,2]}','utf-8')
            binding=output/'surface.bytes';binding.write_bytes(b'physical surface samples')
            source=root/'native.json';source.write_text('{"vertices":[3,4,5]}','utf-8')
            row=dict(generatedPacket=str(packet),generatedSha256=owner.sha(packet),
                     bindingPath=str(binding),bindingSha256=owner.sha(binding),
                     nativePacketPath=str(source),nativePacketSha256=owner.sha(source),
                     geometry=dict(vertices=3,physicalFrames=20))
            result=dict(status='PASS_TEST_NUMERIC_PROOF',rows=[row,copy.deepcopy(row)])
            original=copy.deepcopy(result)
            document=owner.write_generation(output,result)
            self.assertEqual(result,original)
            self.assertEqual(document['rows'],original['rows'])
            self.assertEqual((document['kind'],document['schemaVersion'],document['state']),
                             ('CharacterResourceGeneration',1,'completed'))
            self.assertEqual({Path(pin['path'])for pin in document['outputs']},{packet,binding})
            self.assertEqual(len(document['outputs']),2)
            self.assertTrue(all(pin['role']=='payload'and owner.sha(pin['path'])==pin['sha256']for pin in document['outputs']))
            self.assertEqual(json.loads((output/'Generation.json').read_text('utf-8')),document)

    def test_failed_generation_keeps_created_payloads_owned_and_refuses_sources_or_changed_bytes(self):
        owner=self.generator_owner()
        with tempfile.TemporaryDirectory()as directory:
            root=Path(directory);output=root/'generated';output.mkdir()
            packet=output/'mesh.json';packet.write_text('{}','utf-8');binding=output/'binding.bytes';binding.write_bytes(b'frame')
            row=dict(generatedPacket=str(packet),generatedSha256=owner.sha(packet),bindingPath=str(binding),bindingSha256=owner.sha(binding))
            result=dict(status='FAILED_TEST_NUMERIC_PROOF',rows=[row])
            failed=owner.write_generation(output,result,state='failed')
            self.assertEqual(failed['state'],'failed');self.assertEqual(len(failed['outputs']),2)
            with self.assertRaisesRegex(ValueError,'terminal'):
                owner.write_generation(output,result,state='running')
            source=root/'source.json';source.write_text('{}','utf-8')
            with self.assertRaisesRegex(ValueError,'escaped'):
                owner.write_generation(output,result,state='failed',created_payloads=[source])
            with self.assertRaisesRegex(ValueError,'failed generation'):
                owner.write_generation(output,result,created_payloads=[packet])
            with self.assertRaisesRegex(ValueError,'escaped'):
                owner.write_generation(output,result,state='failed',created_payloads=[output/'Generation.json'])
            escaped=dict(row,generatedPacket=str(source),generatedSha256=owner.sha(source))
            with self.assertRaisesRegex(ValueError,'escaped'):
                owner.write_generation(output,dict(result,rows=[escaped]))
            conflict=dict(row,generatedSha256='0'*64)
            with self.assertRaisesRegex(ValueError,'conflicting'):
                owner.write_generation(output,dict(result,rows=[row,conflict]))
            packet.write_text('{"changed":true}','utf-8')
            with self.assertRaisesRegex(ValueError,'changed'):
                owner.write_generation(output,result)

    def test_strand_atlas_accepts_its_profile_and_purpose_and_reports_conflicting_geometry_options(self):
        import io
        class Arguments(SimpleNamespace):
            def __getattr__(self,name):return False
        owner=self.generator_owner();message='--strand-atlas cannot be combined with geometry generation options.'
        fine=dict(strandsPerTile=2,halfWidthPixels=[2.,3.],startRange=[.01,.02],endRange=[.9,.95],rootTaper=.05,tipTaper=.05)
        profiles=dict(strandAtlas=fine,primaryAtlas=dict(fine,strandsPerTile=4))
        with tempfile.TemporaryDirectory()as folder:
            root=Path(folder)
            with mock.patch.object(owner,'HERE',root),mock.patch.object(owner,'read',return_value=profiles)as read,\
                    mock.patch.object(owner,'generate_strand_atlas',return_value=dict(status='fixture'))as generate,\
                    mock.patch('builtins.print'):
                for purpose,key in(('fine','strandAtlas'),('primary','primaryAtlas')):
                    output=root/purpose;args=Arguments(strand_atlas=True,atlas_profile='fixture-profile.json',atlas_purpose=purpose,lod=None)
                    owner.generate_owned(args,output,None)
                    read.assert_called_with('fixture-profile.json');generate.assert_called_with(output,profiles[key],purpose=purpose)
                self.assertEqual(generate.call_count,2)
                for options in(dict(input='fixture-packet.json'),dict(curved_core=True)):
                    output=root/'rejected';args=Arguments(strand_atlas=True,atlas_profile='fixture-profile.json',atlas_purpose='primary',lod=None,**options)
                    with self.subTest(options=options):
                        with self.assertRaisesRegex(ValueError,'cannot be combined with geometry generation options'):
                            owner.generate_owned(args,output,None)
                        stderr=io.StringIO();parser=owner.argparse.ArgumentParser(prog='groom-fixture')
                        with mock.patch('sys.stderr',stderr),self.assertRaises(SystemExit)as raised:
                            owner.generate_owned(args,output,parser)
                        self.assertEqual(raised.exception.code,2);self.assertIn(message,stderr.getvalue())
                        self.assertFalse(output.exists())
                self.assertEqual(generate.call_count,2);self.assertEqual(read.call_count,2)

    def test_mid_lod_failure_and_interruption_save_completed_rows_partial_outputs_and_the_original_error(self):
        class Arguments(SimpleNamespace):
            def __getattr__(self,name):return False
        for failure,recording_fails in((ValueError('mid-lod failure'),False),(KeyboardInterrupt('interrupted'),False),
                                        (ValueError('original generation failure'),True)):
            with self.subTest(failure=type(failure).__name__,recording_fails=recording_fails),tempfile.TemporaryDirectory()as folder:
                owner=self.generator_owner();root=Path(folder);output=root/'generated';source=root/'input.json'
                entries=[dict(asset='Fixture',lod=lod,nativeRenderer='Fixture_LOD'+str(lod))for lod in(0,1)]
                source.write_text(json.dumps(dict(rows=entries)),'utf-8');args=Arguments(input=str(source),asset=[],lod=None,atlas_purpose='fine')
                def generate(entry,directory):
                    if entry['lod']==1:
                        (directory/'partial.packet').write_bytes(b'partly written mesh')
                        raise failure
                    packet=directory/'complete.packet';packet.write_bytes(b'complete mesh')
                    binding=directory/'complete.binding';binding.write_bytes(b'complete binding')
                    return dict(entry,generatedPacket=str(packet),generatedSha256=owner.sha(packet),bindingPath=str(binding),
                        bindingSha256=owner.sha(binding),broadNativeOuterSheetReplaced=False,originalVertices=3,compactedVertices=6,addedPhysicalDeltaBytes=0)
                actual_writer=owner.write_generation
                with mock.patch.object(owner,'HERE',root),mock.patch.object(owner,'generate',side_effect=generate),\
                        mock.patch.object(owner,'write_generation',side_effect=OSError('recorder refused')if recording_fails else actual_writer):
                    with self.assertRaises(type(failure))as raised:owner.generate_owned(args,output,None)
                self.assertIs(raised.exception,failure)
                if recording_fails:
                    self.assertIn('Generation failure recording also failed: OSError: recorder refused',failure.__notes__)
                    continue
                receipt=json.loads((output/'Generation.json').read_text('utf-8'))
                self.assertEqual(receipt['state'],'failed');self.assertEqual(receipt['status'],'FAILED_SOURCE_GROOM_GENERATION')
                self.assertIsNone(receipt['allNativeSourceFramesKept'])
                self.assertEqual([row['lod']for row in receipt['rows']],[0])
                self.assertEqual(receipt['failedStage'],dict(operation='generate-row',asset='Fixture',lod=1,nativeRenderer='Fixture_LOD1'))
                self.assertEqual(receipt['sourceInputSha256'],owner.sha(source));self.assertEqual(receipt['generatorSha256'],owner.sha(owner.__file__))
                self.assertEqual({Path(pin['path']).name for pin in receipt['outputs']},{'complete.packet','complete.binding','partial.packet'})
                self.assertTrue(all(pin['role']=='payload'and owner.sha(pin['path'])==pin['sha256']for pin in receipt['outputs']))
                self.assertEqual(receipt['generationError']['type'],type(failure).__name__)
                self.assertNotIn(source,{Path(pin['path'])for pin in receipt['outputs']})
        # A malformed library invocation must report its input error even without argparse.
        owner=self.generator_owner()
        with tempfile.TemporaryDirectory()as folder:
            root=Path(folder);output=root/'generated';args=Arguments(input='unused.json',atlas_purpose='invalid')
            with self.assertRaisesRegex(ValueError,'atlas profile/purpose'):
                owner.generate_owned(args,output,None)
            parser=SimpleNamespace(error=mock.Mock(side_effect=SystemExit('CLI input error')))
            with self.assertRaisesRegex(SystemExit,'CLI input error'):owner.generate_owned(args,output,parser)
            parser.error.assert_called_once_with('An atlas profile/purpose belongs to --strand-atlas.')
            self.assertFalse(output.exists())

    def test_proof_and_receipt_failures_preserve_their_terminal_stage_and_completed_payloads(self):
        class Arguments(SimpleNamespace):
            def __getattr__(self,name):return False
        for operation in('prove-row','write-generation'):
            with self.subTest(operation=operation),tempfile.TemporaryDirectory()as folder:
                owner=self.generator_owner();root=Path(folder);output=root/'generated';source=root/'input.json'
                contract=root/'domain.json';contract.write_text(json.dumps(dict(status='PASS_NATIVE_MASK_GPU_AND_SOURCE_DOMAIN_POLICY',authorities=[])),'utf-8')
                entries=[dict(asset='Fixture',lod=lod,nativeRenderer='Fixture_LOD'+str(lod))for lod in(0,1)]
                source.write_text(json.dumps(dict(rows=entries,baselineProfile=dict(nativeHairDomain=True,
                    domainContract=dict(path=str(contract),sha256=owner.sha(contract))))),'utf-8')
                args=Arguments(input=str(source),asset=[],lod=None,curved_core=operation=='prove-row',atlas_purpose='fine');failure=OSError('stage failure')
                def generate(entry,directory):
                    packet=directory/('mesh'+str(entry['lod']));packet.write_bytes(b'complete mesh')
                    binding=directory/('binding'+str(entry['lod']));binding.write_bytes(b'complete binding')
                    return dict(entry,generatedPacket=str(packet),generatedSha256=owner.sha(packet),bindingPath=str(binding),
                        bindingSha256=owner.sha(binding),broadNativeOuterSheetReplaced=False,originalVertices=3,compactedVertices=6,addedPhysicalDeltaBytes=0)
                actual_writer=owner.write_generation
                def write(directory,document,**options):
                    if operation=='write-generation'and options.get('state','completed')=='completed':raise failure
                    return actual_writer(directory,document,**options)
                def prove(row,profile):
                    if row['lod']==1:raise failure
                    return dict(lod=row['lod'],status='PASS_FIXTURE')
                with mock.patch.object(owner,'HERE',root),mock.patch.object(owner,'generate',side_effect=generate),\
                        mock.patch.object(owner,'curved_core_row',side_effect=generate),mock.patch.object(owner,'prove_row',side_effect=prove),\
                        mock.patch.object(owner,'write_generation',side_effect=write):
                    with self.assertRaises(OSError)as raised:owner.generate_owned(args,output,None)
                self.assertIs(raised.exception,failure);receipt=json.loads((output/'Generation.json').read_text('utf-8'))
                self.assertEqual(receipt['state'],'failed');self.assertEqual(receipt['failedStage']['operation'],operation)
                self.assertEqual([row['lod']for row in receipt['rows']],[0,1]);self.assertEqual(len(receipt['outputs']),4)
                self.assertEqual(receipt['sourceInputSha256'],owner.sha(source))
                if operation=='prove-row':
                    self.assertEqual(receipt['status'],'FAILED_CURVED_CORE_NUMERIC_PROOFS')
                    self.assertEqual(receipt['numericProofs'],[dict(lod=0,status='PASS_FIXTURE')])
                    self.assertEqual(receipt['numericError'],str(failure))
                else:self.assertEqual(receipt['status'],'FAILED_SOURCE_GROOM_GENERATION')

    @staticmethod
    def resource_owner():
        folder=Path(__file__).resolve().parents[1]/'UnityAssetBuild'
        sys.path.insert(0,str(folder))
        try:
            spec=importlib.util.spec_from_file_location('groom_serialized_contract_owner',folder/'character_resources.py')
            assert spec is not None and spec.loader is not None
            owner=importlib.util.module_from_spec(spec);spec.loader.exec_module(owner)
        finally:sys.path.pop(0)
        return owner

    def test_refining_cloth_tether_preserves_authored_anchors_and_tighter_limits(self):
        owner = self.resource_owner()
        virtual = {'childDataList': [{'weights': [0.65, 0.35]}], 'vertexToTriangleInfoList': [16777216, 0]}
        cloth = {
            'm_Enabled': 1, 'virtualDeformer': {'m_FileID': 0, 'm_PathID': 2},
            'clothData': {'m_FileID': 0, 'm_PathID': 3}, 'clothParams': {
                'useClampPositionLength': 1,
                'clampPositionLength': {'startValue': .0035, 'endValue': .018, 'useEndValue': 1,
                                        'curveValue': 0., 'useCurveValue': 0},
                'clampPositionVelocityInfluence': .4
            }
        }
        trees = {
            1: cloth, 2: {'m_Enabled': 1, 'deformer': {
                'renderDeformerList': [{'m_FileID': 0, 'm_PathID': 4}],
                'meshData': {'m_FileID': 0, 'm_PathID': 5}}},
            3: {'useVertexList': [0, 1], 'selectionData': [2, 1]},
            4: {'deformer': {'sharedMesh': {'m_FileID': 0, 'm_PathID': 10}}}, 5: virtual,
        }
        before = copy.deepcopy(trees)
        changes = owner.limit_groom_cloth_displacement(trees, None, {10}, preserve_attachments=True)
        self.assertEqual(set(changes), {1})
        self.assertEqual(changes[1]['movingPoints'], 1)
        self.assertEqual(changes[1]['after']['curve']['startValue'], .003)
        self.assertEqual(changes[1]['after']['curve']['endValue'], .013)
        for identifier in (2, 3, 4, 5):
            self.assertEqual(trees[identifier], before[identifier])
        cloth['clothParams']['clampPositionLength'].update(startValue=.002, endValue=.010)
        tighter = copy.deepcopy(trees)
        self.assertEqual(owner.limit_groom_cloth_displacement(trees, None, {10}, preserve_attachments=True), {})
        self.assertEqual(trees, tighter)

    def test_groom_edge_continuity_accounts_for_attachment_leverage(self):
        owner = self.resource_owner()
        positions = np.array([[0., 0., 0.], [.020, 0., 0.], [.020, .006, 0.]])
        parents = np.array([[0, 1, 2], [0, 1, 2], [3, 4, 5]])
        weights = np.array([[.40, .50, .10], [.45, .18, .37], [.7, .2, .1]])
        previous = weights.copy()
        radii = np.array([[.017, .018, .022], [.013, .024, .020], [.017, .021, .022]])
        changed = owner.cohere_simulated_groom_edges(positions, parents, weights, radii,
                                                    np.arange(3), np.array([[0, 1], [1, 2]]),
                                                    np.array([1., 1., 0.]), .013)
        np.testing.assert_array_equal(changed, [True, True, False])
        self.assertLess(np.abs(weights[0] - weights[1]).sum(), np.abs(previous[0] - previous[1]).sum())
        np.testing.assert_allclose(weights.sum(axis=1), 1.)
        np.testing.assert_array_equal(weights[2], previous[2])

    def test_existing_shader_reuse_checks_bundle_bytes_and_actual_material_object_reference(self):
        owner=self.resource_owner()
        with tempfile.TemporaryDirectory()as directory:
            root=Path(directory);shader=root/'shader.bundle';consumer=root/'consumer.bundle';shader.write_bytes(b'shader');consumer.write_bytes(b'groom')
            program=SimpleNamespace(type=SimpleNamespace(name='Shader'),path_id=7)
            retained=SimpleNamespace(name='shader-cab',objects={7:program})
            material=SimpleNamespace(read_typetree=lambda:dict(m_Shader=dict(m_FileID=1,m_PathID=7)))
            source=SimpleNamespace(name='consumer-cab',objects={1:material},externals=[SimpleNamespace(name='shader-cab')])
            pin=lambda path:dict(path=str(path),sha256=owner.digest(path))
            shader_pin=pin(shader);consumer_pin=pin(consumer)
            with mock.patch.object(owner,'load_serialized_bundle',side_effect=lambda path,**_:(None,retained if Path(path)==shader else source)),mock.patch.object(owner,'groom_material_identifiers',return_value={1}),mock.patch.object(owner,'serialized_object_bytes',return_value=b'serialized-program'):
                receipt=owner.verify_groom_shader_reuse(shader_pin,[consumer_pin])
                self.assertEqual(receipt['object'],7);self.assertTrue(receipt['sourceShaderBytesAndReferencesRetained'])
                source.externals[0].name='another-cab'
                with self.assertRaisesRegex(ValueError,'another shader object'):
                    owner.verify_groom_shader_reuse(shader_pin,[consumer_pin])
                source.externals[0].name='shader-cab';shader.write_bytes(b'changed')
                with self.assertRaisesRegex(ValueError,'bundle changed'):
                    owner.verify_groom_shader_reuse(shader_pin,[consumer_pin])

    def test_composed_atlas_uses_actual_groom_bindings_and_preserves_sampler_identity(self):
        owner=self.resource_owner()
        with tempfile.TemporaryDirectory()as directory:
            root=Path(directory);source=root/'source.bundle';target=root/'target.bundle';consumer=root/'groom.bundle'
            source.write_bytes(b'old atlas');target.write_bytes(b'new atlas');consumer.write_bytes(b'groom')
            before=dict(m_Name='Strands',m_TextureSettings=dict(m_WrapU=0,m_WrapV=0),m_Width=64,m_ImageCount=1,m_TextureDimension=2,**{'image data':b'old'})
            after=copy.deepcopy(before);after['image data']=b'new';after['m_Width']=128
            texture=lambda tree:SimpleNamespace(type=SimpleNamespace(name='Texture2D'),read_typetree=lambda:tree)
            old=SimpleNamespace(name='atlas-cab',externals=[],objects={7:texture(before)})
            updated=SimpleNamespace(name='atlas-cab',externals=[],objects={7:texture(after)})
            pointer=dict(m_FileID=1,m_PathID=7)
            material=SimpleNamespace(read_typetree=lambda:dict(m_SavedProperties=dict(m_TexEnvs=[('_GroomColor',dict(m_Texture=pointer))])))
            groom=SimpleNamespace(name='groom-cab',externals=[SimpleNamespace(name='atlas-cab')],objects={1:material})
            pin=lambda path:dict(path=str(path),sha256=owner.digest(path))
            with mock.patch.object(owner,'load_serialized_bundle',side_effect=lambda path,**_:(None,old if Path(path)==source else updated if Path(path)==target else groom)),mock.patch.object(owner,'groom_material_identifiers',return_value={1}):
                proof=owner.verify_groom_texture_replacement(pin(source),pin(target),[pin(consumer)])
                self.assertTrue(proof['samplerAndResourceReferencesExact']);self.assertEqual(proof['materials'][0]['texture'],7)
                after['m_TextureSettings']['m_WrapU']=1
                with self.assertRaisesRegex(ValueError,'sampler'):
                    owner.verify_groom_texture_replacement(pin(source),pin(target),[pin(consumer)])
                after['m_TextureSettings']['m_WrapU']=0;pointer['m_PathID']=8
                with self.assertRaisesRegex(ValueError,'missing texture'):
                    owner.verify_groom_texture_replacement(pin(source),pin(target),[pin(consumer)])

    def test_root_potential_terminates_on_unknown_source_without_zero_times_infinity(self):
        owner=self.generator_owner()
        with np.errstate(invalid='raise',over='raise'):
            self.assertIsNone(owner.source_root_potential([1.,0.,0.],[.02,float('inf'),.03]))
            self.assertIsNone(owner.source_root_potential([.2,.3,.5],[.02,float('nan'),.03]))
            self.assertAlmostEqual(owner.source_root_potential([.2,.3,.5],[.02,.04,.06]),.046)

    def test_authored_strand_normals_keep_zero_recovery_carry_and_native_texture_coordinates(self):
        owner=self.resource_owner();count=3
        coordinates=np.asarray([[.2,.3],[.4,.5],[.6,.7]],dtype='<f4');raw=coordinates.tobytes()
        channels=[dict(stream=0,offset=0,format=0,dimension=0)for _ in range(9)];channels[4]['dimension']=2
        tree=dict(m_BindPose=[],m_VertexData=dict(m_VertexCount=count,m_Channels=channels,m_DataSize=raw))
        mesh=SimpleNamespace(m_Vertices=[[0.,0.,0.],[1.,0.,0.],[0.,1.,0.]],m_Normals=[[-.3,0.,.95],[.3,0.,.95],[0.,.3,.95]],
            m_Tangents=[[1.,0.,0.,1.]]*count,m_UV2=[[0.,0.,0.,1.]]*count,get_triangles=lambda:[[[0,1,2]]],
            src=SimpleNamespace(m_VertexData=SimpleNamespace(m_Channels=channels)),get_streams=lambda *_:[SimpleNamespace(offset=0,stride=8)])
        proof=owner.smooth_groom_support(tree,mesh,preserve_authored=True)
        result=np.frombuffer(tree['m_VertexData']['m_DataSize'],dtype='<f4').reshape(count,4)
        np.testing.assert_array_equal(result[:,:2],coordinates);np.testing.assert_allclose(result[:,2:],0,rtol=0,atol=1e-7)
        self.assertTrue(proof['authoredSupportNormalsRetained']);self.assertEqual(proof['smoothedVertices'],0)

    def test_dense_strand_atlas_meets_its_declared_coverage_at_the_material_cutoff(self):
        folder=Path(__file__).resolve().parents[2]/'blender-models/CharacterHair'
        spec=importlib.util.spec_from_file_location('groom_dense_atlas_source',folder/'GenerateFlowCoverageSweptProfile.py')
        assert spec is not None and spec.loader is not None
        owner=importlib.util.module_from_spec(spec);spec.loader.exec_module(owner)
        design=json.loads((folder/'groom-designs.json').read_text('utf-8-sig'))
        material=json.loads((folder/'materials.json').read_text('utf-8-sig'))
        cutoff=next(p['value']for p in material['materials'][0]['properties']if p['name']=='_FineAlphaCutoff')
        with tempfile.TemporaryDirectory()as directory:
            path=Path(directory);receipt=owner.generate_strand_atlas(path,design['strandAtlas'])
            from PIL import Image
            pixels=np.asarray(Image.open(path/'FineGroomMask.png'));height,width=pixels.shape[:2];tw,th=width//4,height//4
            coverage=[]
            for tile in range(len(receipt['rows'])):
                x,y=(tile%4)*tw,(tile//4)*th
                alpha=pixels[y+int(th*.025):y+int(th*.975),x+int(tw*.04):x+int(tw*.96),3]/255
                coverage.append(float(np.mean(alpha>=cutoff)))
            self.assertGreaterEqual(min(coverage),design['layeredAtlasCards']['assumedMeasuredMaskCoverage'])
            np.testing.assert_array_equal(pixels[:,:,0],pixels[:,:,1]);np.testing.assert_array_equal(pixels[:,:,1],pixels[:,:,2])
            self.assertGreater(float(np.std(pixels[pixels[:,:,3]>=cutoff*255,0]/255)),.02)

    def test_one_lock_atlas_retains_filtered_alpha_without_replacing_transparent_edges(self):
        owner=self.generator_owner();folder=Path(__file__).resolve().parents[2]/'blender-models/CharacterHair'
        settings=json.loads((folder/'groom-designs.json').read_text('utf-8-sig'))['strandAtlas']
        with tempfile.TemporaryDirectory()as directory:
            path=Path(directory);owner.generate_strand_atlas(path,settings)
            from PIL import Image
            with Image.open(path/'FineGroomMask.png')as image:
                alpha=image.getchannel('A');base=np.asarray(alpha)
                self.assertEqual(settings['strandsPerTile'],1)
                self.assertGreater(float(np.mean(base==0)),.05)
                self.assertGreater(float(np.mean(base==255)),.6)
                levels=[]
                while True:
                    values=np.asarray(alpha);levels.append(float(values.max())/255)
                    if alpha.size==(1,1):break
                    alpha=alpha.resize(tuple(max(1,size//2)for size in alpha.size),Image.Resampling.BOX)
                self.assertGreaterEqual(min(levels),.7)
                self.assertLess(levels[-1],.95)

    def test_medium_lock_has_real_cross_section_with_one_surface_stencil_in_all_fits(self):
        owner=self.generator_owner();records=[];rest=[]
        for v in(0.,.5,1.):
            for side in(-1,1):
                point=[side*.001,v*.02,0.];rest.append(point)
                records.append(dict(anchor=0,surface=dict(triangle=[0,1,2],barycentric=[.2,.3,.5]),
                    offset=point.copy(),normal=[0.,0.,1.],tangent=[1.,0.,0.],groom=[(side+1)*.5,v,49.,1.2]))
        rest=np.asarray(rest);rotation=np.asarray([[0.,0.,1.],[0.,1.,0.],[-1.,0.,0.]])
        p=np.stack((rest,rest@rotation.T+[.03,.02,.01]));n=np.tile([0.,0.,1.],(2,6,1));n[1]=n[1]@rotation.T
        t=np.tile([1.,0.,0.],(2,6,1));t[1]=t[1]@rotation.T
        source_p=np.zeros((2,3,3));source_n=np.tile([0.,0.,1.],(2,3,1));source_t=np.tile([1.,0.,0.],(2,3,1));source_b=np.tile([0.,1.,0.],(2,3,1))
        before=copy.deepcopy(records);updated,frames,triangles,proof=owner.arched_ribbon_sections(records,[(0,1),(1,2)],(p,n,t),source_p,source_n,source_t,source_b,.12)
        self.assertEqual(len(updated),9);self.assertEqual(len(triangles),8)
        shaped=frames[0].reshape(2,3,3,3)
        np.testing.assert_array_equal(shaped[:,:,0],p.reshape(2,3,2,3)[:,:,0]);np.testing.assert_array_equal(shaped[:,:,2],p.reshape(2,3,2,3)[:,:,1])
        np.testing.assert_allclose(np.linalg.norm(shaped[:,:,1]-p.reshape(2,3,2,3).mean(axis=2),axis=2),.00024,rtol=0,atol=1e-16)
        self.assertGreater(proof['minimumSignedArea'],0.);self.assertEqual(proof['physicalFits'],2)
        for section in range(3):
            self.assertEqual([row['groom'][0]for row in updated[section*3:section*3+3]],[0.,.5,1.])
            self.assertEqual(updated[section*3]['surface'],updated[section*3+1]['surface'])
        self.assertEqual(records,before)
        collapsed=n.copy();collapsed[1]=0
        geometry_owned=owner.arched_ribbon_sections(records,[(0,1),(1,2)],(p,collapsed,t),source_p,source_n,source_t,source_b,.12)
        np.testing.assert_array_equal(geometry_owned[1][0],frames[0])
        np.testing.assert_array_equal(geometry_owned[1][1],frames[1])

    def test_medium_width_follows_actual_source_strip_across_a_native_uv_seam(self):
        owner=self.generator_owner();p,n,u,neighbors,start=self.walk_surface()
        start['bary']=np.asarray([.25,.5,.25]);start.update(point=start['bary']@p[0],uv=start['bary']@u[0])
        options=dict(positions=p,normals=n,uv=u,neighbors=neighbors,charts=[0,1],eligible=lambda _:True)
        self.assertAlmostEqual(owner.source_ribbon_width(start,[1.,0.,0.],.6,.85,**options),.6)
        self.assertAlmostEqual(owner.source_ribbon_width(start,[1.,0.,0.],1.5,.85,**options),.85)
        options['neighbors']={}
        self.assertAlmostEqual(owner.source_ribbon_width(start,[1.,0.,0.],.6,.85,**options),.425)
        p=p*np.asarray([.001,1.,1.]);start['point']=start['bary']@p[0];options.update(positions=p,neighbors=neighbors)
        self.assertAlmostEqual(owner.source_ribbon_width(start,[1.,0.,0.],.0042,.85,**options),.00085)

    def test_primary_loft_owns_geometry_and_center_uv_without_changing_center_skin(self):
        owner=self.generator_owner();records=[];rest=[]
        for v in(0.,.5,1.):
            for side in(-1,1):
                point=[side*.002,v*.025,0.];rest.append(point)
                records.append(dict(anchor=0,native=False,role='source-continuous-flow-ribbon',slot=0,
                    surface=dict(triangle=[0,1,2],barycentric=[.5-v*.3,.3,.2+v*.3]),uv0=[.3,.2+v*.3],
                    offset=point.copy(),normal=[0.,0.,1.],tangent=[1.,0.,0.],tangentW=-1.,groom=[(side+1)*.5,v,49.,1.2]))
        p=np.stack((rest,np.asarray(rest)+[.02,.01,.03]));n=np.tile([0.,0.,1.],(2,6,1));t=np.tile([1.,0.,0.],(2,6,1))
        triangle=np.asarray([[-.01,-.01,0.],[.01,-.01,0.],[-.01,.04,0.]])
        source_p=np.stack((triangle,triangle+[.02,.01,.03]));source_n=np.tile([0.,0.,1.],(2,3,1));source_t=np.tile([1.,0.,0.],(2,3,1));source_b=np.tile([0.,1.,0.],(2,3,1))
        before=copy.deepcopy(records);source_before=source_p.copy()
        result=owner.arched_ribbon_sections(records,[(0,1),(1,2)],(p,n,t),source_p,source_n,source_t,source_b,.12)
        updated,frames,triangles,proof=result
        self.assertTrue(proof['sourceCenterStencilShared']);self.assertEqual(proof['physicalFits'],2)
        self.assertGreater(proof['minimumSignedArea'],0)
        source_uv=np.asarray([[0.,0.],[1.,0.],[0.,1.]])
        for knot,v in enumerate((0.,.5,1.)):
            section=updated[knot*3:knot*3+3]
            self.assertEqual([row['uv0']for row in section],[[.3,.2+v*.3]]*3)
            self.assertTrue(all(row['role']=='source-continuous-flow-ribbon'and row['groom'][2]==49. for row in section))
            self.assertTrue(all(row['surface']==records[knot*2]['surface']and row['tangentW']==-1. for row in section))
            self.assertEqual([row['groom'][1]for row in section],[v]*3)
            for row in section:np.testing.assert_allclose(row['uv0'],np.asarray(row['surface']['barycentric'])@source_uv,atol=1e-12)
            np.testing.assert_array_equal(frames[0][:,knot*3],p[:,knot*2])
            np.testing.assert_array_equal(frames[0][:,knot*3+2],p[:,knot*2+1])
        self.assertEqual(records,before);np.testing.assert_array_equal(source_p,source_before)
        self.assertTrue(np.isfinite(np.concatenate(frames,axis=-1)).all())
        bad=copy.deepcopy(records);bad[1]['surface']['barycentric']=[.1,.4,.5]
        with self.assertRaisesRegex(ValueError,'unrelated source surfaces'):
            owner.arched_ribbon_sections(bad,[(0,1),(1,2)],(p,n,t),source_p,source_n,source_t,source_b,.12)

    def test_primary_capacity_preserves_whole_locks_and_prioritizes_all_fit_coverage(self):
        owner=self.generator_owner()
        def loft(vertices,area):
            points=np.zeros((2,vertices,3));points[:,1,0]=2*area;points[:,2,1]=1
            points[1]+=np.array([.2,.3,.4])
            return dict(geometry=dict(positions=points.tolist(),triangles=[[0,1,2]]))
        oversized=loft(9,20.);small=loft(3,1.);broad=loft(4,4.)
        source=[oversized,small,broad];before=copy.deepcopy(source)
        selected,proof=owner.admit_primary_lofts(source,7)
        self.assertEqual(selected,[small,broad]);self.assertEqual(proof['admittedVertices'],7)
        self.assertEqual(proof['heldLocks'],1);self.assertTrue(proof['heldSourceCoverageRetained'])
        self.assertEqual([row['ordinal']for row in proof['locks']if row['admitted']],[1,2])
        selected,_=owner.admit_primary_lofts(source,4);self.assertEqual(selected,[broad])
        selected,_=owner.admit_primary_lofts(source,2);self.assertEqual(selected,[])
        collapsed=copy.deepcopy(broad);collapsed['geometry']['positions'][1]=[[0.,0.,0.]]*4
        with self.assertRaisesRegex(ValueError,'every stored fit'):owner.admit_primary_lofts([collapsed],4)
        invalid=copy.deepcopy(broad);invalid['geometry']['triangles'][0][0]=4
        with self.assertRaisesRegex(ValueError,'all-fit geometry'):owner.admit_primary_lofts([invalid],4)
        for capacity in(-1,3.5,True):
            with self.subTest(capacity=capacity),self.assertRaisesRegex(ValueError,'nonnegative vertex count'):
                owner.admit_primary_lofts(source,capacity)
        self.assertEqual(source,before)

    def test_transverse_primary_uv_samples_follow_the_real_source_path(self):
        owner=self.generator_owner();p,n,u,neighbors,start=self.walk_surface()
        start['bary']=np.asarray([.25,.5,.25]);start.update(point=start['bary']@p[0],uv=start['bary']@u[0])
        before=copy.deepcopy(start)
        width,samples=owner.source_ribbon_width(start,[1.,0.,0.],.6,.85,p,n,u,neighbors,[0,1],eligible=lambda _:True,return_samples=True)
        self.assertAlmostEqual(width,.6)
        for sign,sample in zip((-1,1),samples):
            self.assertAlmostEqual(np.linalg.norm(sample['point']-start['point']),width*.5)
            np.testing.assert_allclose(sample['point'],sample['bary']@p[sample['triangle']],atol=1e-12)
            np.testing.assert_allclose(sample['uv'],sample['bary']@u[sample['triangle']],atol=1e-12)
            self.assertGreaterEqual(float(sample['bary'].min()),-1e-12)
        for key in('bary','point','uv'):np.testing.assert_array_equal(start[key],before[key])

    def test_continuous_lock_keeps_one_taper_and_atlas_v_across_texture_chart_cuts(self):
        path=Path(__file__).resolve().parents[2]/'blender-models/CharacterHair/GenerateFlowCoverageSweptProfile.py'
        spec=importlib.util.spec_from_file_location('continuous_groom_coordinates',path)
        owner=importlib.util.module_from_spec(spec);spec.loader.exec_module(owner)
        values=[]
        for y,v in zip((0.,.02,.04,.06),(.2,.4,.6,.8)):
            for x in (-.0005,.0005):values.append(dict(offset=[x,y,.001],groom=[0. if x<0 else 1.,v,49.,1.2]))
        def frames(records,*_):
            points=np.asarray([row['offset']for row in records])[None]
            return points,np.tile([0.,0.,1.],(*points.shape[:-1],1)),np.tile([1.,0.,0.],(*points.shape[:-1],1))
        source=np.zeros((1,3,3))
        with mock.patch.object(owner,'source_fiber_frames',side_effect=frames):
            result=owner.source_ribbon_pieces(values,[(0,1),(2,3)],source,source,source,source,
                soft_coverage=True,preserve_curve_coordinates=True)
        self.assertIsNotNone(result)
        self.assertEqual([row['groom'][1]for row in result[0]],[row['groom'][1]for row in values])
        np.testing.assert_array_equal([row['offset']for row in result[0]],[row['offset']for row in values])

    @staticmethod
    def walk_surface(folded=False):
        positions=np.asarray([[[0.,0.,0.],[1.,0.,0.],[0.,1.,0.]],
                              [[1.,0.,0.],[1.,1.,1. if folded else 0.],[0.,1.,0.]]])
        face_n=np.cross(positions[:,1]-positions[:,0],positions[:,2]-positions[:,0])
        face_n/=np.linalg.norm(face_n,axis=1)[:,None]
        normals=np.repeat(face_n[:,None,:],3,axis=1)
        uv=positions[:,:,:2].copy();uv[1]+=[4.,2.]
        neighbors={0:[(1,1,2,0,2,False)],1:[(0,0,2,1,2,False)]}
        bary=np.asarray([.6,.2,.2])
        start=dict(point=bary@positions[0],normal=normals[0,0],uv=bary@uv[0],triangle=0,bary=bary,chart=0)
        return positions,normals,uv,neighbors,start

    def test_surface_walk_carries_forward_distance_and_keeps_both_native_uv_seam_values(self):
        owner=self.generator_owner()
        for folded in(False,True):
            with self.subTest(folded=folded):
                p,n,u,neighbors,start=self.walk_surface(folded)
                rows=owner.source_surface_walk(start,start['point']+[.7,0.,0.],lambda point,normal,hint,face:hint,
                    p,n,u,neighbors,[0,1],eligible=lambda _:True)
                lengths=np.linalg.norm(np.diff(np.asarray([start['point']]+[row['point']for row in rows]),axis=0),axis=1)
                self.assertAlmostEqual(float(lengths.sum()),.7,places=12)
                self.assertEqual([row['triangle']for row in rows],[0,1,1])
                np.testing.assert_array_equal(rows[0]['point'],rows[1]['point'])
                self.assertGreater(float(np.linalg.norm(rows[0]['uv']-rows[1]['uv'])),1.)
                for row in rows:
                    self.assertTrue(np.all(row['bary']>=0));self.assertAlmostEqual(float(row['bary'].sum()),1.)
                    np.testing.assert_allclose(row['point'],row['bary']@p[row['triangle']],rtol=0,atol=1e-14)
                self.assertTrue(all(length>0. for length in lengths[lengths>1e-12]))

    def test_surface_walk_stops_at_ambiguous_neighbor_and_protected_domain_without_jumps(self):
        owner=self.generator_owner();p,n,u,neighbors,start=self.walk_surface()
        options=dict(flow=lambda point,normal,hint,face:hint,positions=p,normals=n,uv=u,charts=[0,1],eligible=lambda _:True)
        ambiguous={0:neighbors[0]*2}
        rows=owner.source_surface_walk(start,start['point']+[.9,0.,0.],neighbors=ambiguous,**options)
        self.assertEqual(len(rows),1);self.assertEqual(rows[-1]['triangle'],0)
        self.assertAlmostEqual(rows[-1]['point'][0],.8)
        class ProtectedDomain:
            @staticmethod
            def hair(coordinates):return bool(np.all(np.asarray(coordinates)[:,0]<2.))
        rows=owner.source_surface_walk(start,start['point']+[.9,0.,0.],neighbors=neighbors,domain=ProtectedDomain(),**options)
        self.assertEqual(len(rows),1);self.assertEqual(rows[-1]['triangle'],0)

    def test_closed_source_end_returns_its_unused_length_to_the_opposite_continuous_flow(self):
        owner=self.generator_owner();p,n,u,neighbors,_=self.walk_surface()
        for first_sign,triangle,bary in((1,1,[.6,.3,.1]),(-1,0,[.5,.1,.4])):
            with self.subTest(first_sign=first_sign):
                bary=np.asarray(bary);center=dict(point=bary@p[triangle],normal=n[triangle,0],
                    uv=bary@u[triangle],triangle=triangle,bary=bary,chart=triangle)
                def trace(sign,length):
                    return owner.source_surface_walk(center,center['point']+[sign*length,0.,0.],
                        lambda point,normal,hint,face:hint,p,n,u,neighbors,[0,1],eligible=lambda _:True)
                paths,proof=owner.source_curve_paths(center,.6,trace,first_sign)
                points=np.asarray([row['point']for row in reversed(paths[-1])]+[center['point']]+[row['point']for row in paths[1]])
                self.assertAlmostEqual(float(np.linalg.norm(np.diff(points,axis=0),axis=1).sum()),.6,places=12)
                self.assertAlmostEqual(proof['redistributedMetres'],.2,places=12)
                for node in paths[-1]+paths[1]:
                    self.assertTrue(np.all(node['bary']>=0))
                    np.testing.assert_allclose(node['point'],node['bary']@p[node['triangle']],rtol=0,atol=1e-14)
                    np.testing.assert_allclose(node['uv'],node['bary']@u[node['triangle']],rtol=0,atol=1e-14)
                with self.assertRaisesRegex(ValueError,'travel budget'):
                    owner.source_curve_paths(center,.6,lambda sign,length:[dict(point=center['point']+[sign,0.,0.])])

    def test_continuous_ribbon_adapts_to_all_stored_fits_without_restarting_taper(self):
        owner=self.generator_owner()
        source=np.array([[[0.,0.,0.],[1.,0.,0.],[0.,1.,0.]],[[0.,0.,0.],[0.,1.,0.],[-1.,0.,0.]]])
        saved=source.copy();normals=np.tile([0.,0.,1.],(2,3,1));tangents=np.tile([1.,0.,0.],(2,3,1));records=[]
        for y,v in zip((.2,.4,.6),(.2,.4,.6)):
            for side in(-1,1):
                records.append(dict(anchor=0,surface=dict(triangle=[0,1,2],barycentric=[.8-y,.2,y]),
                    offset=[.2+side*.05,y,.01],normal=[side*np.sin(.2),0.,np.cos(.2)],tangent=[1.,0.,0.],
                    groom=[(side+1)*.5,v,49.,1.2]))
        before=copy.deepcopy(records)
        result=owner.source_ribbon_pieces(records,[(0,1),(1,2)],source,normals,tangents,np.cross(normals,tangents),
            soft_coverage=True,preserve_curve_coordinates=True)
        self.assertIsNotNone(result);values,links,frames,count=result
        self.assertEqual(count,1);self.assertEqual(records,before)
        self.assertEqual([row['groom']for row in values],[row['groom']for row in before])
        self.assertEqual([row['surface']for row in values],[row['surface']for row in before])
        triangles=np.asarray([[a*2,a*2+1,b*2]for a,b in links]+[[a*2+1,b*2+1,b*2]for a,b in links])
        q=frames[0][:,triangles]
        signed=np.sum(np.cross(q[:,:,1]-q[:,:,0],q[:,:,2]-q[:,:,0])*frames[1][:,triangles].mean(axis=2),axis=2)
        self.assertTrue(np.all(signed>1e-14));np.testing.assert_array_equal(source,saved)

    def test_ribbon_preserves_its_complete_transported_centerline_in_every_fit(self):
        owner=self.generator_owner();angle=.4
        rotation=np.array([[np.cos(angle),0.,np.sin(angle)],[0.,1.,0.],[-np.sin(angle),0.,np.cos(angle)]])
        rest=np.array([[0.,0.,0.],[.04,0.,0.],[0.,.04,0.]])
        source=np.array([rest,rest@rotation.T+[.001,.002,.004]])
        normals=np.array([np.tile([0.,0.,1.],(3,1)),np.tile(rotation@[0.,0.,1.],(3,1))])
        tangents=np.array([np.tile([1.,0.,0.],(3,1)),np.tile(rotation@[1.,0.,0.],(3,1))])
        records=[]
        for x,v in zip((.005,.015,.025),(0.,.5,1.)):
            bary=[1-(x+.005)/.04,x/.04,.005/.04]
            center=np.array([x+.004*v,.005+.001*np.sin(np.pi*v),.0007])
            for side in(-1,1):
                point=center+[0.,-side*.00035,0.]
                records.append(dict(anchor=0,surface=dict(triangle=[0,1,2],barycentric=bary),
                    offset=point.tolist(),normal=[0.,0.,1.],tangent=[0.,-1.,0.],groom=[(side+1)*.5,v,49.,1.3]))
        bitangents=np.cross(normals,tangents)
        expected=owner.source_fiber_frames(records,source,normals,tangents,bitangents)[0].reshape(2,3,2,3).mean(axis=2)
        saved=source.copy();triangles=np.array([[0,1,2],[1,3,2],[2,3,4],[3,5,4]])
        framed=owner.source_ribbon_frames(records,triangles,source,normals,tangents,bitangents)
        self.assertIsNotNone(framed)
        positions,directions,_,proof=framed
        np.testing.assert_allclose(positions.reshape(2,3,2,3).mean(axis=2),expected,rtol=0,atol=1e-14)
        np.testing.assert_allclose(expected[1],expected[0]@rotation.T+[.001,.002,.004],rtol=0,atol=1e-14)
        self.assertGreater(expected[0,-1,0]-.025,.0039)
        points=positions[:,triangles]
        signed=np.sum(np.cross(points[:,:,1]-points[:,:,0],points[:,:,2]-points[:,:,0])*directions[:,triangles].mean(axis=2),axis=2)
        self.assertTrue(np.all(signed>0));self.assertEqual(proof['widthScale'],1.)
        np.testing.assert_array_equal(source,saved)

    def test_ribbon_frame_does_not_swap_sides_when_a_stored_normal_turns_through_the_flow(self):
        owner=self.generator_owner()
        source=np.asarray([[[0.,0.,0.],[1.,0.,0.],[0.,1.,0.]]]*2)
        normals=np.tile([0.,0.,1.],(2,3,1));normals[1,2]=[0.,0.,-1.]
        tangents=np.tile([1.,0.,0.],(2,3,1));records=[]
        for y in(.1,.3,.7,.9):
            for side in(-1,1):
                records.append(dict(anchor=0,surface=dict(triangle=[0,1,2],barycentric=[.95-y,.05,y]),
                    offset=[.05+side*.01,y,0.],normal=[0.,0.,1.],tangent=[1.,0.,0.]))
        links=[(i,i+1)for i in range(3)]
        tri=np.asarray([[a*2,a*2+1,b*2]for a,b in links]+[[a*2+1,b*2+1,b*2]for a,b in links])
        framed=owner.source_ribbon_frames(records,tri,source,normals,tangents,np.cross(normals,tangents))
        self.assertIsNotNone(framed)
        points,directions,_,proof=framed
        self.assertGreater(proof['minimumSignedArea'],0.)
        width=points.reshape(2,4,2,3)[:,:,1]-points.reshape(2,4,2,3)[:,:,0]
        self.assertTrue(np.all(np.sum(width[:,:-1]*width[:,1:],axis=2)>0.))
        self.assertTrue(np.all(directions[:,:,2]>0.))

    def test_contact_guard_includes_stored_head_shapes_and_rejects_changed_source_bytes(self):
        owner=self.generator_owner()
        head=dict(vertexCount=3,indexFormat=0,vertices=[0.,0.,0.,.001,0.,0.,0.,.001,0.],
            normals=[0.,0.,1.]*3,tangents=[1.,0.,0.,1.]*3,colors=[],weights=[1.,0.,0.,0.]*3,boneIndices=[0]*12,
            bindPoses=np.eye(4).ravel().tolist(),subMeshes=[[0,1,2]],uv=[],
            shapes=[dict(name='BodyFit',frames=[dict(weight=100.,vertices=[0.,0.,.01]*3,normals=[0.]*9,tangents=[0.]*9)])])
        native=copy.deepcopy(head);native['vertices']=[.0001,.0001,.01,.0003,.0001,.01,.0001,.0003,.01];native['shapes']=[]
        matrix=np.eye(4).ravel().tolist()
        with tempfile.TemporaryDirectory()as directory:
            path=Path(directory)/'head.json';path.write_text(json.dumps(head),'utf-8')
            source=dict(identity='compatible head',packet=dict(path=str(path),sha256=owner.sha(path)),
                sourceFingerprint=owner.fingerprint(head)[0],rendererToPrefab=matrix)
            protected,proof=owner.source_contact_vertices(native,matrix,[source],.0005)
            self.assertTrue(protected.all());self.assertTrue(proof['allStoredHeadFramesIncluded'])
            self.assertEqual(proof['testedHeadPosePairs'],2)
            self.assertEqual(proof['confirmedContactVertexIds'],[0,1,2])
            head['shapes']=[];path.write_text(json.dumps(head),'utf-8')
            with self.assertRaisesRegex(ValueError,'head source changed'):
                owner.source_contact_vertices(native,matrix,[source],.0005)
            source['packet']['sha256']=owner.sha(path);source['sourceFingerprint']=owner.fingerprint(head)[0]
            protected,_=owner.source_contact_vertices(native,matrix,[source],.0005)
            self.assertFalse(protected.any())

    def test_terminal_order_keeps_coincident_skin_owners_separate_and_holds_rootless_components(self):
        owner=self.generator_owner();p=[0.,0.,0.,0.,.03,0.,.02,.02,0.]
        native=dict(vertexCount=6,vertices=p*2,normals=[0.,0.,1.]*6,tangents=[1.,0.,0.,1.]*6,
            weights=[1.,0.,0.,0.]*6,boneIndices=[0]*12+[1,0,0,0]*3,shapes=[],
            subMeshes=[[0,1,2,3,4,5]],uv=[dict(channel=0,dimension=2,values=[0.,0.,0.,0.,0.,1.,0.,0.,1.,1.,0.,0.]*2)])
        domain=SimpleNamespace(hair=lambda _:True,surface_hair=lambda _:True)
        with mock.patch.object(owner,'SourceHairDomain',return_value=domain):
            result=owner.source_terminal_domain(native,np.eye(4).ravel().tolist(),[dict(shader='Custom/FakeAnisoHair')],
                np.asarray([True,False,False,False,False,False]),dict(minimumRootDistanceMetres=.01),contact_vertices=[0])
        self.assertEqual(result['unknownGroups'],3)
        self.assertTrue(result['protected'][3:].all());self.assertFalse(result['protected'][1:3].any())
        self.assertEqual(result['tipGroups'],[int(result['groups'][1])])
        self.assertAlmostEqual(result['rootDistance'][1],.03)

    def test_contact_spheres_preserve_far_points_without_declaring_them_triangle_contacts(self):
        owner=self.generator_owner();matrix=np.eye(4).ravel().tolist()
        head=dict(vertexCount=3,indexFormat=0,vertices=[0.,0.,0.,1.,0.,0.,0.,1.,0.],
            normals=[0.,0.,1.]*3,tangents=[1.,0.,0.,1.]*3,colors=[],weights=[1.,0.,0.,0.]*3,boneIndices=[0]*12,
            bindPoses=np.eye(4).ravel().tolist(),subMeshes=[[0,1,2]],uv=[],shapes=[])
        native=copy.deepcopy(head);native['vertices']=[.7,.7,0.,.1,.1,0.,.2,.1,0.]
        with tempfile.TemporaryDirectory()as directory:
            path=Path(directory)/'head.json';path.write_text(json.dumps(head),'utf-8')
            reference=dict(identity='native head',packet=dict(path=str(path),sha256=owner.sha(path)),
                sourceFingerprint=owner.fingerprint(head)[0],rendererToPrefab=matrix)
            protected,proof=owner.source_contact_vertices(native,matrix,[reference],.001)
            self.assertTrue(protected.all())
            self.assertEqual(proof['confirmedContactVertexIds'],[1,2]);self.assertEqual(proof['narrowPhaseQueries'],3)

    def test_protected_nonhair_boundary_does_not_become_a_contact_distance_origin(self):
        owner=self.generator_owner();p=np.asarray([[0.,0.,0.],[1.,0.,0.],[0.,1.,0.],[1.,1.,0.],[2.,1.,0.],[2.,2.,0.]])
        native=dict(vertexCount=6,vertices=p.ravel().tolist(),normals=[0.,0.,1.]*6,tangents=[1.,0.,0.,1.]*6,
            weights=[1.,0.,0.,0.]*6,boneIndices=[0]*24,shapes=[],subMeshes=[[0,1,2,1,3,2],[3,4,5]],
            uv=[dict(channel=0,dimension=2,values=np.c_[p[:,:2],np.zeros((6,2))].ravel().tolist())])
        protected=np.asarray([True,False,False,False,False,False]);settings=dict(minimumRootDistanceMetres=.01)
        with mock.patch.object(owner,'SourceHairDomain',return_value=SimpleNamespace(hair=lambda _:True,surface_hair=lambda _:True)):
            result=owner.source_terminal_domain(native,np.eye(4).ravel().tolist(),
                [dict(shader='Custom/FakeAnisoHair'),dict(shader='Standard')],protected,settings,contact_vertices=[0])
            self.assertEqual(result['contactRootGroups'],[0]);self.assertEqual(result['rootDistance'][3],2.)
            self.assertTrue(result['protected'][3:].all());self.assertEqual(result['rootDistance'][4],3.)
            self.assertAlmostEqual(result['rootDistance'][5],2.+2.**.5)
            with self.assertRaisesRegex(ValueError,'protected source contact'):
                owner.source_terminal_domain(native,np.eye(4).ravel().tolist(),[dict(shader='Custom/FakeAnisoHair')]*2,
                    protected,settings,contact_vertices=[1])
            for invalid in([.5],[True],[-1],[6],[0,0]):
                with self.subTest(contact_vertices=invalid),self.assertRaisesRegex(ValueError,'vertex owner'):
                    owner.source_terminal_domain(native,np.eye(4).ravel().tolist(),[dict(shader='Custom/FakeAnisoHair')]*2,
                        protected,settings,contact_vertices=invalid)

    def test_terminal_field_reaches_subdivided_render_vertices_and_preserves_reference_and_frames(self):
        owner=self.generator_owner()
        source_points=[[0.,0.,0.],[1.,0.,0.],[0.,1.,0.],[1.,1.,0.]]
        source_uv=[v for point in source_points for v in(*point[:2],0.,0.)]
        native=dict(vertexCount=4,vertices=np.asarray(source_points).ravel().tolist(),normals=[0.,0.,1.]*4,
            tangents=[1.,0.,0.,1.]*4,weights=[1.,0.,0.,0.]*4,boneIndices=[0]*16,colors=[],bindPoses=np.eye(4).ravel().tolist(),
            subMeshes=[[0,1,2,1,3,2]],uv=[dict(channel=0,dimension=2,values=source_uv)],
            shapes=[dict(name='Fitting',frames=[dict(weight=100.,vertices=[.001,0.,0.]*4,normals=[0.]*12,tangents=[0.]*12)])])
        current=copy.deepcopy(native);count=7
        for key,width in(('normals',3),('tangents',4),('weights',4),('boneIndices',4)):
            current[key]=(current[key]+current[key][:width]*3)
        actual=[[.8,.6,0.],[.7,.8,0.],[.9,.9,0.]]
        current['vertices']+=np.asarray(actual).ravel().tolist();current['vertexCount']=count;current['subMeshes']=[[4,5,6]]
        current['uv'][0]['values']+=[v for point in actual for v in(*point[:2],0.,0.)]
        current['uv'].append(dict(channel=2,dimension=4,values=[0.,0.,0.,1.]*count))
        frame=current['shapes'][0]['frames'][0]
        for key in('vertices','normals','tangents'):frame[key]+=frame[key][:3]*3
        delta=np.zeros((2,4,3));delta[:,1:,2]=-.006;original=copy.deepcopy(current)
        with mock.patch.object(owner,'SourceHairDomain',return_value=SimpleNamespace(hair=lambda _:True,surface_hair=lambda _:True)):
            proof,positions=owner.transfer_terminal_support_field(native,current,np.eye(4).ravel().tolist(),delta,
                [dict(shader='Custom/FakeAnisoHair')],np.asarray([True,False,False,False]),
                dict(maximumSupportOffsetMetres=.008,minimumRetainedAreaFraction=.25))
        self.assertEqual(current,original);self.assertTrue(proof['serializedBinary32FramesChecked'])
        self.assertTrue(proof['retainedSubdividedGeometryActuallyReached'])
        self.assertEqual(proof['modifiedRenderedSupportVertices'],3)
        self.assertAlmostEqual(proof['maximumRenderedRestDisplacementMetres'],.006,places=8)
        np.testing.assert_array_equal(positions[:,0],np.asarray([[0.,0.,0.],[.001,0.,0.]],dtype='<f4').astype(float))
        expected=np.asarray(current['vertices'],dtype='<f4').astype(float).reshape(-1,3)[:,0][None]+np.asarray([[0.],[.001]],dtype='<f4').astype(float)
        np.testing.assert_array_equal(positions[:,:,0],expected)

    def test_authored_quadratic_fine_keeps_free_length_outside_a_partial_retained_render_surface(self):
        owner=self.generator_owner();matrix=np.eye(4).ravel().tolist()
        native=dict(vertexCount=3,vertices=[0.,0.,0.,1.,0.,0.,0.,1.,0.],normals=[0.,0.,1.]*3,tangents=[1.,0.,0.,1.]*3,
            weights=[1.,0.,0.,0.]*3,boneIndices=[0]*12,colors=[],bindPoses=matrix,indexFormat=0,subMeshes=[[0,1,2]],
            uv=[dict(channel=0,dimension=2,values=[0.,0.,0.,0.,1.,0.,0.,0.,0.,1.,0.,0.])],
            shapes=[dict(name='Fit',frames=[dict(weight=100.,vertices=[.005628,0.,0.]*3,normals=[0.]*9,tangents=[0.]*9)])])
        current=copy.deepcopy(native);current['vertexCount']=4;current['vertices']+=[1/3,1/3,.003]
        for key,width in(('normals',3),('tangents',4),('weights',4),('boneIndices',4)):current[key]+=current[key][:width]
        current['uv'][0]['values']+=[1/3,1/3,0.,0.];current['uv'].append(dict(channel=2,dimension=4,values=[0.,0.,0.,1.]*4))
        current['subMeshes']=[[0,1,3]]
        for key in('vertices','normals','tangents'):current['shapes'][0]['frames'][0][key]+=current['shapes'][0]['frames'][0][key][:3]
        packet=copy.deepcopy(native);points=np.asarray([[.1,.1,.01],[.11,.1,.01],[.6,.3,.02],[.61,.3,.02],
            [0.,0.,.002],[1.,0.,.002],[0.,1.,.002]])
        packet['vertexCount']=10;packet['vertices']+=points.ravel().tolist()
        for key,width in(('normals',3),('tangents',4),('weights',4),('boneIndices',4)):packet[key]+=packet[key][:width]*7
        packet['uv'][0]['values']+=[float(v)for p in points for v in(*p[:2],0.,0.)]
        packet['uv'].append(dict(channel=2,dimension=4,values=[0.,0.,0.,1.]*3+
            [0.,0.,49.,1.,1.,0.,49.,1.,0.,1.,49.,1.,1.,1.,49.,1.]+[0.,0.,0.,1.]*3))
        packet['subMeshes']=[[3,4,5,4,6,5,7,8,9]]
        for key in('vertices','normals','tangents'):packet['shapes'][0]['frames'][0][key]+=packet['shapes'][0]['frames'][0][key][:3]*7
        records=[dict(native=True,anchor=i)for i in range(3)]+[
            dict(native=False,anchor=0,slot=0,surface=dict(triangle=[0,1,2],barycentric=[1-p[0]-p[1],p[0],p[1]]))for p in points]
        binding=dict(matrix=matrix,vertices=records);surface=dict(flowCarrierCells=dict(model='source-quadratic'),
            triangleCountsBySlot=[1],parentFacesBySlot=[[0]])
        saved=copy.deepcopy((packet,binding,current))
        with mock.patch.object(owner,'fit_retained_carrier',return_value=(copy.deepcopy(packet),dict(validated=True))):
            result,attached,proof=owner.couple_retained_groom_support(packet,native,binding,current,[dict(shader='Custom/FakeAnisoHair')],.008,carrier_surface=surface)
            self.assertEqual(result,packet);self.assertEqual(attached,binding)
            self.assertTrue(proof['authoredFinePntAndAllStoredFramesExact']);self.assertFalse(proof['retainedRenderSurfaceDefinesPhysicalCoverage'])
            self.assertEqual(proof['fineVertices'],4);self.assertEqual(proof['storedFits'],2);self.assertEqual(proof['maximumPhysicalPartitionAreaError'],0.)
            bad=copy.deepcopy(binding);bad['vertices'][5]['surface']['barycentric']=[0.,-.1,1.1]
            with self.assertRaisesRegex(AssertionError,'outside its source-owned'):
                owner.couple_retained_groom_support(packet,native,bad,current,[dict(shader='Custom/FakeAnisoHair')],.008,carrier_surface=surface)
        self.assertEqual((packet,binding,current),saved)

    def test_retained_fine_follows_actual_curved_children_and_every_saved_fit(self):
        owner=self.generator_owner();matrix=np.eye(4).ravel().tolist()
        points=np.asarray([[0.,0.,0.],[1.,0.,0.],[0.,1.,0.]])
        native=dict(vertexCount=3,vertices=points.ravel().tolist(),normals=[0.,0.,1.]*3,tangents=[1.,0.,0.,1.]*3,
            weights=[1.,0.,0.,0.]*3,boneIndices=[0]*12,colors=[],bindPoses=matrix,subMeshes=[[0,1,2]],indexFormat=0,
            bounds=[.5,.5,0.,1.,1.,.02],uv=[dict(channel=0,dimension=2,values=[float(v)for p in points for v in(*p[:2],0.,0.)])],
            shapes=[dict(name='Fit',index=0,frames=[dict(weight=100.,vertices=[.001,0.,0.]*3,normals=[0.]*9,tangents=[0.]*9)])])
        current=copy.deepcopy(native);current['vertexCount']=4;current['vertices']+=[1/3,1/3,.003]
        for key,width in(('normals',3),('tangents',4),('weights',4),('boneIndices',4)):current[key]+=current[key][:width]
        current['uv'][0]['values']+=[1/3,1/3,0.,0.];current['uv'].append(dict(channel=2,dimension=4,values=[0.,0.,0.,1.]*4))
        current['subMeshes']=[[0,1,3,1,2,3,2,0,3]]
        for key in('vertices','normals','tangents'):current['shapes'][0]['frames'][0][key]+=([.001,0.,.002]if key=='vertices'else[0.]*3)
        packet=copy.deepcopy(native);packet['vertexCount']=9
        added=np.asarray([[x,y,.001]for x in(.2,.3,.4)for y in(.2005,.1995)])
        packet['vertices']+=added.ravel().tolist()
        for key,width in(('normals',3),('tangents',4),('weights',4),('boneIndices',4)):packet[key]+=packet[key][:width]*6
        packet['uv'][0]['values']+=[float(v)for p in added for v in(p[0],.2,0.,0.)]
        packet['uv'].append(dict(channel=2,dimension=4,values=[0.,0.,0.,1.]*3+[v for y in(0.,.5,1.)for x in(0.,1.)for v in(x,y,49.,1.)]))
        packet['subMeshes']=[[0,1,2,3,4,5,4,6,5,5,6,7,6,8,7]]
        for key in('vertices','normals','tangents'):packet['shapes'][0]['frames'][0][key]+=packet['shapes'][0]['frames'][0][key][:3]*6
        records=[dict(native=True,anchor=i)for i in range(3)]
        records += [dict(native=False,slot=0,anchor=0,surface=dict(triangle=[0,1,2],barycentric=[1-p[0]-.2,p[0],.2]))for p in added]
        binding=dict(matrix=matrix,vertices=records);saved=copy.deepcopy(packet);source_saved=copy.deepcopy(current)
        result,retained_binding,proof=owner.couple_retained_groom_support(packet,native,binding,current,[dict(shader='Custom/FakeAnisoHair')],.008)
        self.assertEqual(packet,saved);self.assertEqual(current,source_saved)
        self.assertEqual(result['vertices'][:9],packet['vertices'][:9])
        self.assertEqual(result['shapes'][0]['frames'][0]['vertices'][:9],packet['shapes'][0]['frames'][0]['vertices'][:9])
        p=np.asarray(result['vertices']).reshape(-1,3)
        fraction=float(np.float32(.2))*3
        np.testing.assert_allclose(p[3:,2],.001+.003*fraction,rtol=0,atol=1e-12)
        delta=np.asarray(result['shapes'][0]['frames'][0]['vertices']).reshape(-1,3)
        np.testing.assert_allclose(delta[3:,2],.002*float(np.float32(fraction)),rtol=0,atol=1e-12)
        self.assertEqual(proof['fineVertices'],6);self.assertEqual(proof['storedFits'],2)
        self.assertEqual(binding,retained_binding);self.assertEqual(proof['continuousCurves'],1)
        bad=copy.deepcopy(current);bad['vertices'][-1]=.02
        with self.assertRaisesRegex((ValueError,AssertionError),'outside its source-owned|unambiguous retained'):
            owner.couple_retained_groom_support(packet,native,binding,bad,[dict(shader='Custom/FakeAnisoHair')],.008)

    def test_source_uv_aliases_share_the_physical_driver_with_different_native_normal_frames(self):
        path=Path(__file__).resolve().parents[2]/'blender-models/CharacterHair/GenerateFlowCoverageSweptProfile.py'
        spec=importlib.util.spec_from_file_location('continuous_groom_geometry',path)
        owner=importlib.util.module_from_spec(spec);spec.loader.exec_module(owner)
        points=np.asarray([[0.,0.,0.],[1.,0.,0.],[0.,1.,0.],[1.,0.,0.],[0.,0.,0.],[1.,-1.,0.]])
        posed=points.copy();posed[2,2]=.08;posed[5,2]=-.03
        source_p=np.stack((points,posed));source_n=np.tile([0.,0.,1.],(2,6,1))
        source_n[:,3:]=owner.unit(np.asarray([0.,.2,1.]))
        source_t=np.tile([1.,0.,0.],(2,6,1));source_b=np.cross(source_n,source_t)
        records=[]
        desired=np.asarray([.5,0.,.001]);normal=np.asarray([0.,0.,1.]);tangent=np.asarray([1.,0.,0.])
        for anchor,triangle in ((0,[0,1,2]),(3,[3,4,5])):
            basis=np.asarray([source_t[0,anchor],source_b[0,anchor],source_n[0,anchor]])
            records.append(dict(anchor=anchor,offset=(basis@(desired-points[anchor])).tolist(),
                normal=(basis@normal).tolist(),tangent=(basis@tangent).tolist(),
                surface=dict(triangle=triangle,barycentric=[.5,.5,0.])))
        plan=dict(physicalGroups=[0,1,2,1,0,3],sourceTrianglesBySlot=[[[0,1,2],[3,4,5]]],vertices=records)
        drivers=owner.prepare_geometry_frames(plan,source_p)
        self.assertEqual(records[0]['geometrySurface'],records[1]['geometrySurface'])
        self.assertTrue(drivers)
        frames=owner.source_fiber_frames(records,source_p,source_n,source_t,source_b)
        for values in frames:np.testing.assert_allclose(values[:,0],values[:,1],rtol=0,atol=1e-12)

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
        with self.assertRaisesRegex(ValueError,'retained vertex capacity'):
            owner.compose_finished_source_groom(source,fine,binding,vertex_capacity=source['vertexCount'])
        old_atlas=copy.deepcopy(source);new_atlas=copy.deepcopy(fine)
        old_atlas['subMeshes']=[[0,1,2,3,4,5]]
        for value in(old_atlas,new_atlas):
            value['uv'][1]['values']=[0.,0.,0.,1.]*3+[0.,0.,49.,1.]*3
        packet,_,proof=owner.compose_finished_source_groom(old_atlas,new_atlas,binding,vertex_capacity=source['vertexCount'])
        self.assertEqual(packet['vertexCount'],source['vertexCount'])
        self.assertEqual(proof['replacedOwnedFineVertices'],3);self.assertEqual(proof['addedFineVertices'],3)
        self.assertEqual(proof['composedVertices'],proof['retainedVertexCapacity'])
        material_slots=[dict(sourceSlot=0,targetSlot=1)]
        separated,_,proof=owner.compose_finished_source_groom(source,fine,binding,fine_material_slots=material_slots)
        self.assertEqual(separated['subMeshes'],[[3,4,5],[6,7,8]])
        self.assertEqual(proof['fineMaterialSlots'],material_slots)
        repeated,_,_=owner.compose_finished_source_groom(separated,fine,binding,fine_material_slots=material_slots)
        self.assertEqual(repeated['subMeshes'],separated['subMeshes'])
        self.assertEqual(repeated['vertexCount'],separated['vertexCount'])
        with self.assertRaisesRegex(ValueError,'unowned carrier'):
            bad=copy.deepcopy(separated);bad['subMeshes'][1]=[0,1,2]
            owner.compose_finished_source_groom(bad,fine,binding,fine_material_slots=material_slots)

    def test_dedicated_fine_material_reuses_its_owned_slot_and_rejects_overlays(self):
        owner=self.build_owner()
        pointer=lambda i:dict(m_FileID=0,m_PathID=i)
        material=dict(m_Name='Native',m_Shader=pointer(11),m_SavedProperties=dict(m_Floats=[('_ColorStrength',1.)],m_TexEnvs=[
            ('_MainTex',dict(m_Texture=pointer(21))),('_GroomColor',dict(m_Texture=pointer(22))),('_GroomNormal',dict(m_Texture=pointer(23)))]))
        fine_material=copy.deepcopy(material);fine_material['m_SavedProperties']['m_TexEnvs'][1][1]['m_Texture']=pointer(24)
        renderer=dict(m_Mesh=pointer(1),m_Materials=[pointer(2),pointer(3)])
        obj=lambda kind,tree:SimpleNamespace(type=SimpleNamespace(name=kind),read_typetree=lambda:tree)
        file=SimpleNamespace(objects={1:obj('Mesh',{}),2:obj('Material',material),3:obj('Material',fine_material),4:obj('SkinnedMeshRenderer',renderer)})
        packet=dict(vertexCount=6,subMeshes=[[0,1,2],[3,4,5]],uv=[dict(channel=2,values=[0.,0.,0.,1.]*3+[0.,0.,49.,1.]*3)])
        finished=copy.deepcopy(packet);finished['subMeshes']=[[0,1,2,3,4,5]]
        with mock.patch.object(owner,'export_current_mesh',return_value=packet):
            self.assertEqual(owner.groom_fine_material_slots(file,1,finished),[dict(sourceSlot=0,targetSlot=1)])
            fine_material['m_SavedProperties']['m_TexEnvs'][0][1]['m_Texture']=pointer(25)
            self.assertEqual(owner.groom_fine_material_slots(file,1,finished),[dict(sourceSlot=0,targetSlot=2)])
            renderer['m_Materials'].append(pointer(3))
            with self.assertRaisesRegex(ValueError,'without overlays'):owner.groom_fine_material_slots(file,1,finished)

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
        source_uv=dict(channel=0,dimension=2,values=[0.,0.,0.,0.,1.,0.,0.,0.,0.,1.,0.,0.])
        native['uv'].append(copy.deepcopy(source_uv))
        for target in(packet,current):target['uv'].insert(0,dict(source_uv,values=source_uv['values']*2))
        binding=dict(vertices=[dict(anchor=i,native=True,surface=None)for i in range(3)]+[
            dict(anchor=0,native=False,surface=dict(triangle=[0,1,2],barycentric=[.7,.2,.1]))for _ in range(3)])
        proof=owner.validate_retained_groom_source(native,current,source_bones=['head'],target_bones=['head'])
        self.assertTrue(proof['nativeUvSkinPaletteAndFittingIdentitiesExact'])
        self.assertAlmostEqual(proof['maximumRetainedPositionDeltaMetres'],.004,places=8)
        for field,value in(('weights',[0.,1.,0.,0.]+current['weights'][4:]),
                            ('bindPoses',np.eye(4).ravel().tolist()*2),
                            ('subMeshes',[[0,2,1,3,4,5]]),
                            ('shapes',[dict(name='AnotherFit',frames=current['shapes'][0]['frames'])])):
            changed=dict(current,**{field:value})
            with self.subTest(field=field),self.assertRaisesRegex(ValueError,'retained groom'):
                owner.validate_retained_groom_source(native,changed)
        with self.assertRaisesRegex(ValueError,'bone order'):
            owner.validate_retained_groom_source(dict(native,bindPoses=native['bindPoses']*2),
                dict(current,bindPoses=current['bindPoses']*2),source_bones=['head','jaw'],target_bones=['jaw','head'])
        changed=copy.deepcopy(current);changed['uv'][0]['values'][0]=.1
        with self.assertRaisesRegex(ValueError,'texture correspondence'):
            owner.validate_retained_groom_source(native,changed)
        authored=copy.deepcopy(current);authored['subMeshes']=[[3,4,5]]
        authored['uv'][0]['dimension']=4;authored['uv'][0]['values'][2:4]=[.4,.8]
        proof=owner.validate_retained_groom_source(native,authored,
            source_bones=['Root/Bip001/Head'],target_bones=['Bip001/Head'])
        self.assertEqual(proof['retainedNativeFaces'],0)
        self.assertTrue(proof['retainedNativeFaceSubsetAndOrientationValid'])
        saved=copy.deepcopy(packet);result=owner.retain_source_fitting(packet,native,binding,current)
        np.testing.assert_allclose(np.asarray(result['vertices']).reshape(-1,3)-np.asarray(packet['vertices']).reshape(-1,3),[[0.,0.,.003]]*6,rtol=0,atol=1e-15)
        np.testing.assert_allclose(np.asarray(result['shapes'][0]['frames'][0]['vertices']).reshape(-1,3),[[0.,0.,.002]]*6,rtol=0,atol=1e-15)
        self.assertEqual(result['normals'],packet['normals']);self.assertEqual(result['tangents'],packet['tangents']);self.assertEqual(packet,saved)
        bounds=np.asarray(result['bounds']);low=bounds[:3]-bounds[3:]*.5;high=bounds[:3]+bounds[3:]*.5
        positions=np.asarray(result['vertices']).reshape(-1,3)
        for points in(positions,positions+np.asarray(result['shapes'][0]['frames'][0]['vertices']).reshape(-1,3)):
            self.assertTrue(np.all(points>=low));self.assertTrue(np.all(points<=high))

    @staticmethod
    def retained_shell_fixture(owner):
        points=np.array([[0.,0.,0.],[.04,0.,0.],[0.,.04,0.],[0.,0.,.04]])
        normals=owner.unit(points-points.mean(axis=0));tangents=owner.unit(np.cross(normals,[0.,1.,0.]))
        native=dict(name='NativeTetra',vertexCount=4,indexFormat=0,vertices=points.ravel().tolist(),
            normals=normals.ravel().tolist(),tangents=np.c_[tangents,np.ones(4)].ravel().tolist(),colors=[],
            weights=[1.,0.,0.,0.]*4,boneIndices=[0]*16,bindPoses=np.eye(4).ravel().tolist(),
            uv=[dict(channel=0,dimension=2,values=[0.,0.,0.,0.,1.,0.,0.,0.,0.,1.,0.,0.,1.,1.,0.,0.])],
            subMeshes=[[0,2,1,0,1,3,0,3,2,1,2,3]],shapes=[],bounds=[.02,.02,.02,.04,.04,.04])
        native['sourceFingerprint']=owner.fingerprint(native)[0]
        current=copy.deepcopy(native);current['uv'].append(dict(channel=2,dimension=4,values=[0.,0.,0.,1.]*4))
        current['sourceFingerprint']=owner.fingerprint(current)[0]
        return native,current

    def test_retained_exterior_seed_checks_the_real_carrier_and_never_invents_a_surface_owner(self):
        owner=self.generator_owner();native,current=self.retained_shell_fixture(owner)
        saved_native=copy.deepcopy(native);saved_current=copy.deepcopy(current)
        shell=owner.retained_source_shell(native,current,np.eye(4).ravel(),[dict(shader='Custom/FakeAnisoHair')],.008)
        triangle=np.array([0,2,1]);bary=np.array([.34,.33,.33])
        self.assertTrue(owner.retained_exterior_seed(shell,0,triangle,bary,.001,fine_normal=[0.,0.,-1.]))
        self.assertFalse(owner.retained_exterior_seed(shell,0,triangle,bary,.001,fine_normal=[0.,0.,1.]))
        shell['owners'].clear()
        self.assertIsNone(owner.retained_exterior_seed(shell,0,triangle,bary,.001,fine_normal=[0.,0.,-1.]))
        self.assertEqual(native,saved_native);self.assertEqual(current,saved_current)

    def test_retained_reference_capacity_and_seed_geometry_share_the_same_verified_source(self):
        owner=self.generator_owner();native,current=self.retained_shell_fixture(owner)
        with tempfile.TemporaryDirectory()as directory:
            path=Path(directory)/'input.bundle';path.write_bytes(b'input')
            entry=dict(retainedCapacityAuthority=dict(path=str(path),sha256=owner.sha(path),meshPathId='7',maximumStoredVertices=4))
            resources=SimpleNamespace(load_serialized_bundle=lambda *args,**kwargs:(None,SimpleNamespace(objects={7:object()})))
            builder=SimpleNamespace(export_current_mesh=lambda obj:copy.deepcopy(current))
            with mock.patch.dict(sys.modules,character_resources=resources,build_models=builder):
                reference=owner.retained_groom_reference(entry,native)
                self.assertEqual(owner.retained_groom_capacity(entry,native,reference=reference),4)
                self.assertEqual(owner.retained_groom_capacity(entry,native,fine_only=True,reference=reference),4)
                path.write_bytes(b'other')
                with self.assertRaisesRegex(ValueError,'source changed'):owner.retained_groom_reference(entry,native)

    def test_candidate_lookup_rejects_partial_carrier_cells_without_relaxing_final_attachment(self):
        owner=self.generator_owner();p=np.array([[0.,0.,0.],[1.,0.,0.],[0.,1.,0.]])
        n=np.tile([0.,0.,1.],(3,1));t=np.tile([1.,0.,0.],(3,1));b=np.cross(n,t)
        triangle=np.array([0,1,2]);key=(0,(0,1,2))
        owners={key:(triangle,np.array([[0,1,2]]),np.array([[0.,0.]]),np.array([np.eye(2)*2]))}
        barys=[[.8,.1,.1],[.2,.4,.4]]
        records=[dict(native=False,slot=0,surface=dict(triangle=triangle.tolist(),barycentric=weights))for weights in barys]
        points=np.asarray(barys)@p;normals=n[:2];tangents=t[:2];before=copy.deepcopy(records)
        with self.assertRaisesRegex(AssertionError,'outside its source-owned curved partition'):
            owner.couple_fine_to_core(None,p,n,t,points,normals,tangents,records,p,n,t,b,owners)
        mapped={};moved,*_=owner.couple_fine_to_core(None,p,n,t,points,normals,tangents,records,p,n,t,b,owners,
            mapped_surfaces=mapped,require_complete=False)
        np.testing.assert_array_equal(moved,[0]);self.assertEqual(set(mapped),{0});self.assertEqual(records,before)
        shell=dict(p=p,n=n,t=t,b=b,owners=owners)
        self.assertIsNone(owner.retained_exterior_seed(shell,0,triangle,barys[1],.001))
        # The normal attachment caller remains strict after a candidate query.
        with self.assertRaises(AssertionError):
            owner.couple_fine_to_core(None,p,n,t,points,normals,tangents,records,p,n,t,b,owners)

    def test_retained_budget_uses_each_source_allocation_and_keeps_its_complete_frame_and_index_cost(self):
        owner=self.generator_owner();native=dict(vertexCount=4,subMeshes=[[0,1,2]])
        def reference(fine,frames=0,index_format=0):
            return dict(vertexCount=4+fine,indexFormat=index_format,colors=[],
                uv=[dict(channel=0,dimension=4,values=[]),dict(channel=2,dimension=4,values=[0.,0.,0.,1.]*4+[0.,0.,49.,1.2]*fine)],
                shapes=[dict(name='Fit',frames=[dict(weight=i+1)for i in range(frames)])]if frames else[],
                subMeshes=[[0,1,2]+list(range(4,4+fine-fine%3))])
        values=[]
        for count in(12,42000):
            packet=reference(count,frames=3);budget=owner.retained_groom_budget(native,packet,fine_only=True);values.append(budget)
            self.assertEqual(budget['sourceReplaceableVertices'],count)
            self.assertEqual(budget['physicalFrames'],3)
            self.assertEqual(budget['sourceIndexFormat'],packet['indexFormat'])
            self.assertLessEqual(budget['keptVertices']+budget['maximumAddedVertices'],packet['vertexCount'])
            self.assertLessEqual(budget['keptVertices']+budget['maximumAddedVertices'],65535)
            maximum_cost=budget['maximumAddedVertices']*(budget['vertexAndPhysicalCostPerVertexBytes']+6)
            self.assertLessEqual(maximum_cost,budget['maximumAddedLogicalBytes'])
        self.assertGreater(values[1]['maximumAddedVertices'],20000)
        self.assertGreater(values[1]['maximumCurves'],1400)
        self.assertLessEqual(values[0]['maximumAddedVertices'],12)
        with_fit=owner.retained_groom_budget(native,reference(12,frames=3),fine_only=True)
        without_fit=owner.retained_groom_budget(native,reference(12),fine_only=True)
        self.assertEqual(with_fit['sourceVertexAndPhysicalBytes']-without_fit['sourceVertexAndPhysicalBytes'],12*3*36)
        wide=owner.retained_groom_budget(native,reference(70000,index_format=1),fine_only=True)
        self.assertLessEqual(wide['maximumFinalVertices'],65535)
        self.assertEqual(wide['sourceIndexFormat'],1);self.assertEqual(wide['targetIndexFormat'],0)
        mixed=reference(12);mixed['subMeshes']=[[0,4,5]]
        with self.assertRaisesRegex(ValueError,'mixes fine'):owner.retained_groom_budget(native,mixed,fine_only=True)
        with self.assertRaisesRegex(ValueError,'unsupported source index'):owner.retained_groom_budget(native,reference(12,index_format=2),fine_only=True)

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
            variants=[]
            for name in ('Fixture_Hair_LOD0','Fixture_Beard_LOD0'):
                variants.append(owner.save_groom_packet(Path(directory),dict(asset='Combined_Fixture',lod=0,
                    manifestEntry=dict(authoredMeshAsset=name)),dict(name=name)))
            self.assertNotEqual(variants[0],variants[1])
            self.assertEqual([json.loads(path.read_text())['name']for path in variants],['Fixture_Hair_LOD0','Fixture_Beard_LOD0'])
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


    @staticmethod
    def primary_loft_fixture(seam=False):
        y=np.array([0.,.02,.02,.04,.06]) if seam else np.array([0.,.02,.04,.06])
        v=(y/.06).astype('<f4');base=np.column_stack((np.zeros(len(y)),y,np.zeros(len(y))))
        rotation=np.array([[0.,0.,1.],[0.,1.,0.],[-1.,0.,0.]])
        centers=np.stack((base,base@rotation.T+np.array([.02,.03,.04])))
        across=np.tile([1.,0.,0.],(2,len(y),1));across[1]=across[1]@rotation.T
        connections=[(0,1),(2,3),(3,4)] if seam else [(0,1),(1,2),(2,3)]
        return centers,across,np.full(len(y),.004),v,y,connections

    @staticmethod
    def primary_loft_reference(c,a,w):
        along=np.zeros_like(c);along[:,0]=c[:,1]-c[:,0];along[:,-1]=c[:,-1]-c[:,-2]
        along[:,1:-1]=c[:,2:]-c[:,:-2]
        normal=np.cross(a,along);normal/=np.linalg.norm(normal,axis=2,keepdims=True)
        reference=np.repeat(c[:,:,None,:],3,axis=2)
        reference[:,:,0]-=a*w[None,:,None]*.5;reference[:,:,2]+=a*w[None,:,None]*.5
        reference[:,:,1]+=normal*w[None,:,None]*.12
        return reference

    def test_primary_envelope_true_free_end_owns_one_apex_and_all_pose_positive_triangles(self):
        c,a,w,v,y,links=self.primary_loft_fixture();source=copy.deepcopy((c,a,w,v,y,links))
        result=self.generator_owner().primary_loft_geometry(c,a,w,v,y,links,free_end=True,taper_metres=.04,height_fraction=.12,reference_sections=self.primary_loft_reference(c,a,w))
        self.assertEqual(len(result['sections'][-1]),1)
        self.assertEqual(len(result['positions'][0]),10)
        self.assertEqual(len(result['triangles']),10)
        self.assertGreater(result['minimumSignedArea'],0)
        for before,after in zip(source[:5],(c,a,w,v,y)):np.testing.assert_array_equal(before,after)
        np.testing.assert_array_equal(result['positions'][:,-1],c[:,-1])
        # Every output transverse vertex names exactly one center row.
        for row,indices in enumerate(result['sections']):
            self.assertTrue(np.all(result['sectionOwner'][indices]==row))

    def test_primary_envelope_uv_cut_is_not_a_free_tip_and_does_not_restart_taper(self):
        c,a,w,v,y,links=self.primary_loft_fixture(seam=True)
        result=self.generator_owner().primary_loft_geometry(c,a,w,v,y,links,free_end=True,taper_metres=.06,height_fraction=.12,reference_sections=self.primary_loft_reference(c,a,w),physical_uv_aliases=[(1,2)])
        left,right=result['sections'][1:3]
        np.testing.assert_array_equal(result['positions'][:,left],result['positions'][:,right])
        np.testing.assert_array_equal(result['normals'][:,left],result['normals'][:,right])
        self.assertEqual(result['taper'][1],result['taper'][2])
        self.assertEqual(result['globalV'][left[0]],result['globalV'][right[0]])
        self.assertGreater(result['taper'][1],0)
        self.assertNotIn((left[0],right[0]),map(tuple,result['triangles']))

    def test_primary_envelope_trace_budget_ends_keep_full_width_without_a_physical_free_authority(self):
        c,a,w,v,y,links=self.primary_loft_fixture()
        result=self.generator_owner().primary_loft_geometry(c,a,w,v,y,links,taper_metres=.04,height_fraction=.12,reference_sections=self.primary_loft_reference(c,a,w))
        self.assertEqual(result['physicalFreeEnds'],(False,False))
        np.testing.assert_array_equal(result['taper'],np.ones(len(v)))
        self.assertEqual([len(row)for row in result['sections']],[3]*len(v))
        np.testing.assert_allclose(np.linalg.norm(result['positions'][:,0]-result['positions'][:,2],axis=1),.004,rtol=0,atol=1e-16)
        owner=self.generator_owner();reduced=owner.reduce_primary_loft_sections(result,self.primary_loft_reference(c,a,w),.00024)
        self.assertEqual(reduced['positions'].shape,(2,6,3));self.assertEqual(reduced['physicalFreeEnds'],(False,False))
        self.assertEqual(reduced['sectionReduction']['renderSections'],2)
        self.assertLessEqual(reduced['sectionReduction']['maximumGeometricChordBoundMetres'],.00024)
        # A bend present only in the other stored pose must remain represented.
        c[1,1,1]+=.002
        reference=self.primary_loft_reference(c,a,w)
        bowed=owner.primary_loft_geometry(c,a,w,v,y,links,taper_metres=.04,height_fraction=.12,reference_sections=reference)
        kept=owner.reduce_primary_loft_sections(bowed,reference,.00024)
        self.assertGreater(kept['sectionReduction']['renderSections'],2)
        self.assertGreater(kept['minimumSourceDriverOrientation'],0.)

    def test_primary_envelope_second_pose_fold_is_rejected_instead_of_changing_a_guard(self):
        c,a,w,v,y,links=self.primary_loft_fixture()
        # Width reverses only at one stored section; endpoint authority cannot
        # silently omit this fit or turn its mirrored cell into a valid span.
        reference=self.primary_loft_reference(c,a,w);a[1,1]*=-1
        with self.assertRaisesRegex(ValueError,'inverted|collapsed|singular|geometric driver'):
            self.generator_owner().primary_loft_geometry(c,a,w,v,y,links,free_end=True,taper_metres=.04,height_fraction=.12,reference_sections=reference)

    def test_primary_envelope_exact_mixed_center_coefficients_are_copied_without_side_anchor_search(self):
        c,a,w,v,y,links=self.primary_loft_fixture()
        result=self.generator_owner().primary_loft_geometry(c,a,w,v,y,links,free_end=True,taper_metres=.04,height_fraction=.12,reference_sections=self.primary_loft_reference(c,a,w))
        canonical=np.array([[1.,0,0,0],[.5,.5,0,0],[.75,.25,1e-20,0],[1.,0,0,0]],dtype='<f4')
        copied=canonical[result['sectionOwner']]
        for row,ids in enumerate(result['sections']):
            np.testing.assert_array_equal(copied[ids],np.tile(canonical[row],(len(ids),1)))
        self.assertGreater(copied[result['sections'][2][0],2],0)

    def test_primary_envelope_strong_continuous_curl_and_all_fit_uv_seam_remain_legal(self):
        angle=np.linspace(0.,np.pi,9);v=(angle/np.pi).astype('<f4')
        first=np.column_stack((.02*np.cos(angle),.02*np.sin(angle),np.zeros(len(angle))))
        second=np.column_stack((.015*np.cos(angle),.015*np.sin(angle),.002*np.sin(angle)))
        centers=np.stack((first,second))
        transverse=np.stack((np.column_stack((np.cos(angle),np.sin(angle),np.zeros(len(angle)))),)*2)
        # An actual color chart cut duplicates a physical row, not an endpoint.
        insertion=4;centers=np.insert(centers,insertion,centers[:,insertion],axis=1)
        transverse=np.insert(transverse,insertion,transverse[:,insertion],axis=1)
        v=np.insert(v,insertion,v[insertion]);arc=np.insert(angle*.02,insertion,angle[insertion]*.02)
        widths=np.full(len(v),.002)
        links=[(i,i+1)for i in range(len(v)-1)if i!=insertion]
        reference=self.primary_loft_reference(centers,transverse,widths)
        result=self.generator_owner().primary_loft_geometry(centers,transverse,widths,v,arc,links,
            free_end=True,taper_metres=.02,height_fraction=.12,reference_sections=reference,
            physical_uv_aliases=[(insertion,insertion+1)])
        self.assertGreater(result['minimumSignedArea'],0)
        self.assertGreater(result['minimumSourceDriverOrientation'],0)
        np.testing.assert_array_equal(result['positions'][:,result['sections'][insertion]],
                                      result['positions'][:,result['sections'][insertion+1]])
        self.assertEqual(result['physicalFreeEnds'],(False,True))

    def test_primary_envelope_distinct_coincident_sheets_are_not_inferred_as_uv_aliases(self):
        c,a,w,v,y,links=self.primary_loft_fixture(seam=True)
        # The same center position with a different transverse source branch
        # must not be treated as an alias merely because it touches a seam.
        a[:,2]=[0.,0.,1.]
        with self.assertRaisesRegex(ValueError,'physical UV alias'):
            self.generator_owner().primary_loft_geometry(c,a,w,v,y,links,free_end=True,taper_metres=.04,
                height_fraction=.12,reference_sections=self.primary_loft_reference(c,a,w),physical_uv_aliases=[(1,2)])

    def test_primary_surface_walk_keeps_native_geometry_domain_separate_from_fine_seed_margin(self):
        owner=self.generator_owner()
        points=np.asarray([[[0.,0.,0.],[.04,0.,0.],[0.,.04,0.]]])
        normals=np.tile([0.,0.,1.],(1,3,1));uv=points[:,:,:2]/.04
        bary=np.asarray([.6,.2,.2]);start=dict(point=bary@points[0],normal=np.asarray([0.,0.,1.]),
            uv=bary@uv[0],triangle=0,bary=bary,chart=0)
        domain=SimpleNamespace(hair=lambda values:False,surface_hair=lambda values:True)
        options=dict(flow=lambda *args:np.asarray([1.,0.,0.]),positions=points,normals=normals,uv=uv,
            neighbors={},charts=[0],eligible=lambda value:True,domain=domain)
        target=start['point']+np.asarray([.004,0.,0.])
        self.assertEqual(owner.source_surface_walk(start,target,**options),[])
        result=owner.source_surface_walk(start,target,**options,surface_domain=True)
        self.assertGreater(len(result),0)
        np.testing.assert_allclose(result[-1]['point'],target,rtol=0,atol=1e-12)
        domain.surface_hair=lambda values:False
        self.assertEqual(owner.source_surface_walk(start,target,**options,surface_domain=True),[])

    def test_primary_carrier_index_budget_uses_the_declared_source_key_without_double_counting(self):
        owner=self.generator_owner()
        native=dict(subMeshes=[[0,1,2,0,2,1]])
        packet=dict(subMeshes=[[0,1,2,3,4,5,6,7,8]])
        fine=np.asarray([False]*3+[True]*3+[False]*3);carrier=dict(triangleCountsBySlot=[1])
        self.assertEqual(owner.groom_added_indices(packet,native,fine,dict(fineOnly=False,sourceCarrierFacesBySlot=[[0]]),carrier),6)
        self.assertEqual(owner.groom_added_indices(packet,native,fine,dict(fineOnly=True),None),3)
        self.assertEqual(owner.groom_added_indices(packet,native,fine,dict(fineOnly=False),None),3)
        with self.assertRaisesRegex(ValueError,'explicit render owner'):
            owner.groom_added_indices(packet,native,fine,dict(fineOnly=False,sourceCarrierFacesBySlot=[[0]]),None)
        with self.assertRaisesRegex(ValueError,'mixes fine'):
            owner.groom_added_indices(dict(subMeshes=[[0,1,3]]),native,fine,dict(fineOnly=True),None)

    def test_primary_tip_owner_distinguishes_physical_free_boundary_from_uv_and_budget(self):
        owner=self.generator_owner();p=np.asarray([[0.,0.,0.],[.04,0.,0.],[0.,.04,0.],
            [.04,0.,0.],[0.,.04,0.],[.04,.04,0.]])
        native=dict(vertexCount=6,subMeshes=[[0,1,2]]);groups=np.asarray([0,1,2,1,2,3])
        source=dict(triangle=[0,1,2],barycentric=[0.,.5,.5])
        options=dict(root_vertices=[0],stop_reason='boundary-or-ambiguous-forward-edge',away_from_root=True)
        result=owner.primary_source_end_owner(native,0,source,np.asarray([1.,1.,0.]),p,groups,**options)
        self.assertTrue(result['trueFreeEnd'])
        native['subMeshes']=[[0,1,2,3,5,4]]
        seam=owner.primary_source_end_owner(native,0,source,np.asarray([1.,1.,0.]),p,groups,**options)
        self.assertFalse(seam['trueFreeEnd']);self.assertEqual(seam['nativeConsumers'],2)
        native['subMeshes']=[[0,1,2]]
        options['stop_reason']='step-complete'
        self.assertFalse(owner.primary_source_end_owner(native,0,source,np.asarray([1.,1.,0.]),p,groups,**options)['trueFreeEnd'])
        options['stop_reason']='boundary-or-ambiguous-forward-edge'
        root=dict(triangle=[0,1,2],barycentric=[1.,0.,0.])
        self.assertEqual(owner.primary_source_end_owner(native,0,root,np.asarray([1.,1.,0.]),p,groups,**options)['kind'],'held-root')
        self.assertFalse(owner.primary_source_end_owner(native,0,source,np.asarray([-1.,-1.,0.]),p,groups,**options)['trueFreeEnd'])

    def test_primary_carrier_index_and_vertex_prover_counts_the_primary_subset_once(self):
        owner=self.generator_owner();native=dict(vertexCount=3,sourceFingerprint='native',shapes=[],
            vertices=[0.,0.,0.,1.,0.,0.,0.,1.,0.],normals=[0.,0.,1.]*3,tangents=[1.,0.,0.,1.]*3,
            weights=[1.,0.,0.,0.]*3,boneIndices=[0]*12,bindPoses=np.eye(4).ravel().tolist(),subMeshes=[[0,1,2]],
            uv=[dict(channel=0,dimension=2,values=[0.]*12)])
        packet=copy.deepcopy(native);packet.update(vertexCount=6,indexFormat=0,subMeshes=[[0,1,2,3,4,5]])
        for key in('vertices','normals','tangents','weights','boneIndices'):packet[key]+=native[key]
        packet['uv'][0]['values']*=2
        packet['uv'].append(dict(channel=2,dimension=4,values=[0.,0.,0.,1.]*3+[.5,.5,49.,1.]*3))
        surface=dict(triangle=[0,1,2],barycentric=[1.,0.,0.])
        binding=dict(matrix=np.eye(4).ravel().tolist(),schemaVersion=4,sourceVertexCount=3,sourceFingerprint='native',
            vertices=[dict(native=True,anchor=i,surface=None,geometrySurface=None)for i in range(3)]+
                [dict(native=False,anchor=0,surface=surface,geometrySurface=surface,slot=0,uv0=[0.,0.],tangentW=1.,role='primary-lock')for _ in range(3)])
        row=dict(nativePacketPath='native',generatedPacket='packet',bindingPath='binding',manifestEntry=dict(sourceFingerprint='native'),
            curvedCoreSurface=dict(vertices=3,flowCarrierCells=dict(model='primary-loft')),addedVertices=3,asset='fixture',lod=0,physicalFrames=0)
        with mock.patch.object(owner,'read',side_effect=lambda path:native if str(path)=='native'else packet),\
                mock.patch.object(owner,'decode_binding',return_value=binding),mock.patch.object(owner,'validate_curved_core',return_value=dict(children=1)):
            proof=owner.prove_row(row,dict(layeredAtlasCards=True,maximumAddedVerticesByLod=[3,3,3]))
        self.assertEqual(proof['newPrimaryVertices'],3);self.assertEqual(proof['newFineVertices'],0)
        self.assertEqual(proof['fineFringeVertices'],0);self.assertGreater(proof['minimumFineSignedAreaAfterFloat32'],0)

    def test_primary_tip_owner_keeps_fixed_root_and_other_material_or_nonmanifold_edges(self):
        owner=self.generator_owner();p=np.asarray([[0.,0.,0.],[.04,0.,0.],[0.,.04,0.],
            [.04,0.,0.],[0.,.04,0.],[.04,.04,0.]])
        native=dict(vertexCount=6,subMeshes=[[0,1,2],[3,5,4]]);groups=np.asarray([0,1,2,1,2,3])
        source=dict(triangle=[0,1,2],barycentric=[0.,.5,.5])
        options=dict(root_vertices=[0],stop_reason='boundary-or-ambiguous-forward-edge',away_from_root=True)
        self.assertFalse(owner.primary_source_end_owner(native,0,source,np.asarray([1.,1.,0.]),p,groups,**options)['trueFreeEnd'])
        root=dict(triangle=[0,1,2],barycentric=[1.,0.,0.])
        self.assertEqual(owner.primary_source_end_owner(native,0,root,np.asarray([1.,1.,0.]),p,groups,**options)['kind'],'held-root')

    def test_primary_fixed_root_seeds_preserve_contact_identity_and_do_not_invent_unresolved_roots(self):
        owner=self.generator_owner();p=np.asarray([[0.,0.,0.],[.02,0.,0.],[0.,.02,0.],
            [.1,0.,0.],[.12,0.,0.],[.1,.02,0.]])
        native=dict(vertexCount=6,vertices=p.ravel().tolist(),normals=[0.,0.,1.]*6,tangents=[1.,0.,0.,1.]*6,
            weights=[1.,0.,0.,0.]*6,boneIndices=[0,0,0,0]*6,subMeshes=[[0,1,2,3,4,5]],shapes=[],
            uv=[dict(channel=0,dimension=4,values=np.zeros((6,4)).ravel().tolist())])
        protected=np.asarray([True,False,True,True,False,False]);matrix=np.eye(4).ravel().tolist()
        material=[dict(shader='Custom/FakeAnisoHair')];settings=dict(minimumRootDistanceMetres=.001)
        with mock.patch.object(owner,'SourceHairDomain')as domain:
            domain.return_value.surface_hair.return_value=True
            old=owner.source_terminal_domain(native,matrix,material,protected,settings,contact_vertices=[0])
            new=owner.source_terminal_domain(native,matrix,material,protected,settings,contact_vertices=[0],fixed_vertices=[2,3])
        self.assertTrue(np.isinf(old['rootDistance'][3:]).all())
        self.assertTrue(np.isfinite(new['rootDistance']).all())
        self.assertEqual(new['contactRootGroups'],old['contactRootGroups'])
        self.assertEqual(len(new['fixedRootGroups']),1)
        resolved=np.flatnonzero(np.isin(new['groups'],new['contactRootGroups']+new['fixedRootGroups'])).tolist()
        self.assertIn(0,resolved);self.assertIn(3,resolved);self.assertNotIn(2,resolved)
        end=owner.primary_source_end_owner(native,0,dict(triangle=[0,1,2],barycentric=[0.,0.,1.]),
            np.asarray([0.,1.,0.]),p,new['groups'],root_vertices=resolved,
            stop_reason='step-complete',away_from_root=True)
        self.assertNotEqual(end['kind'],'held-root')
        self.assertTrue(new['protected'][2])
        with self.assertRaisesRegex(ValueError,'protected native selection'):
            owner.source_terminal_domain(native,matrix,material,np.zeros(6,bool),settings,contact_vertices=[],fixed_vertices=[3])

    def test_primary_surface_walk_outcome_distinguishes_step_budget_and_native_boundary(self):
        owner=self.generator_owner();p=np.asarray([[[0.,0.,0.],[.04,0.,0.],[0.,.04,0.]]])
        n=np.tile([0.,0.,1.],(1,3,1));uv=p[:,:,:2]/.04;bary=np.asarray([.6,.2,.2])
        start=dict(point=bary@p[0],normal=np.asarray([0.,0.,1.]),uv=bary@uv[0],triangle=0,bary=bary,chart=0)
        options=dict(flow=lambda *args:np.asarray([1.,0.,0.]),positions=p,normals=n,uv=uv,neighbors={},charts=[0],eligible=lambda value:True)
        outcome={};owner.source_surface_walk(start,start['point']+np.asarray([.002,0.,0.]),**options,outcome=outcome)
        self.assertEqual(outcome['reason'],'step-complete');self.assertEqual(outcome['remainingMetres'],0)
        owner.source_surface_walk(start,start['point']+np.asarray([.08,0.,0.]),**options,outcome=outcome)
        self.assertEqual(outcome['reason'],'boundary-or-ambiguous-forward-edge')
        self.assertGreater(outcome['remainingMetres'],0);self.assertAlmostEqual(outcome['barycentric'][0],0,places=6)
        # A continuous native sheet can fold beyond 90 degrees while its
        # shared edge, UV and skin owner remain exact. Width follows the
        # physical transport, rather than returning to a world-space ray.
        hinge=np.asarray([-1.,1.,0.])/np.sqrt(2);origin=np.asarray([.04,0.,0.]);tip=np.asarray([.04,.04,0.])-origin
        angle=np.deg2rad(112.);folded=origin+tip*np.cos(angle)+np.cross(hinge,tip)*np.sin(angle)+hinge*np.dot(hinge,tip)*(1-np.cos(angle))
        physical=np.asarray([p[0],[p[0,1],folded,p[0,2]]])
        n=g_normals=owner.unit(np.cross(physical[:,1]-physical[:,0],physical[:,2]-physical[:,0]))
        n=np.repeat(n[:,None,:],3,axis=1)
        q=np.asarray([uv[0],[[1.,0.],[1.,1.],[0.,1.]]]);axis=np.asarray([1.,1.,0.])/np.sqrt(2)
        node=dict(point=np.asarray([.02,.02,0.]),normal=n[0,0],uv=np.asarray([.5,.5]),triangle=0,bary=np.asarray([0.,.5,.5]),chart=0)
        links={0:[(1,1,2,0,2,True)],1:[(0,0,2,1,2,True)]}
        for posed in(physical,physical+np.asarray([.003,.001,-.002])):
            source=dict(node,point=node['point']+posed[0,0]-physical[0,0])
            for direction in(axis,-axis):
                measured=owner.source_ribbon_width(source,direction,.006,.85,posed,n,q,links,[0,0],eligible=lambda value:True)
                self.assertAlmostEqual(measured,.006,places=8)
            self.assertEqual(owner.source_ribbon_width(source,axis,.006,.85,posed,n,q,{},[0,0],eligible=lambda value:True),0.)
        self.assertLess(float(g_normals[0]@g_normals[1]),0.)

    @staticmethod
    def primary_walk_fixture(seam=False):
        p=np.asarray([[0.,0.,0.],[.04,0.,0.],[0.,.04,0.],
            [.04,0.,0.],[.04,.04,0.],[0.,.04,0.]])
        tri=np.asarray([[0,1,2],[3,4,5]])if seam else np.asarray([[0,1,2]])
        count=6 if seam else 3;native=dict(vertexCount=count,weights=[1.,0.,0.,0.]*count,boneIndices=[0]*4*count,bindPoses=np.eye(4).ravel().tolist())
        uv=np.asarray([[[0.,0.],[1.,0.],[0.,1.]],[[.4,.5],[.9,.9],[.1,.7]]])[:len(tri)]
        n=np.tile([0.,0.,1.],(len(tri),3,1));bary=np.asarray([.5,.25,.25])
        start=dict(point=bary@p[tri[0]],normal=np.asarray([0.,0.,1.]),uv=bary@uv[0],triangle=0,bary=bary,chart=0)
        neighbors={0:[(1,1,2,0,2,False)],1:[(0,0,2,1,2,False)]}if seam else{}
        root=p[:count,0]+p[:count,1];terminal=dict(rootDistance=root,terminalDistance=root.max()-root)
        settings=dict(stepMetres=.002,maximumLengthMetres=.12,terminalRegionMetres=.01,terminalStopMetres=.004,
            minimumRootDistanceMetres=.001,allowClosedTerminals=True)
        domain=SimpleNamespace(surface_hair=lambda values:True,hair=lambda values:False)
        return native,tri,start,p[tri],n,uv,neighbors,list(range(len(tri))),domain,terminal,settings

    def test_primary_terminal_walk_authors_free_end_only_inside_the_native_terminal_envelope(self):
        owner=self.generator_owner();native,tri,start,p,n,uv,neighbors,charts,domain,terminal,settings=self.primary_walk_fixture()
        direction=np.asarray([1.,1.,0.])/np.sqrt(2)
        result=owner.primary_end_walk(native,tri,start,direction,lambda point,normal,sign,index:direction,
            p,n,uv,neighbors,charts,domain,terminal,settings,away=True)
        self.assertTrue(result['trueFreeEnd']);self.assertEqual(result['reason'],'authored-free-end-in-native-terminal-envelope')
        source=result['lastSurface'];distance=np.asarray(source['barycentric'])@terminal['terminalDistance'][source['triangle']]
        self.assertLessEqual(distance,settings['terminalStopMetres'])
        settings['maximumLengthMetres']=.003
        bounded=owner.primary_end_walk(native,tri,start,direction,lambda point,normal,sign,index:direction,
            p,n,uv,neighbors,charts,domain,terminal,settings,away=True)
        self.assertFalse(bounded['trueFreeEnd']);self.assertEqual(bounded['reason'],'trace-length-budget')

    def test_primary_terminal_walk_crosses_uv_cut_without_creating_an_intermediate_tip(self):
        owner=self.generator_owner();native,tri,start,p,n,uv,neighbors,charts,domain,terminal,settings=self.primary_walk_fixture(True)
        direction=np.asarray([1.,1.,0.])/np.sqrt(2)
        result=owner.primary_end_walk(native,tri,start,direction,lambda point,normal,sign,index:direction,
            p,n,uv,neighbors,charts,domain,terminal,settings,away=True)
        self.assertTrue(result['trueFreeEnd'])
        aliases=[(a,b)for a,b in zip(result['nodes'],result['nodes'][1:])if np.linalg.norm(a['point']-b['point'])<1e-10 and np.linalg.norm(a['uv']-b['uv'])>1e-5]
        self.assertEqual(len(aliases),1);self.assertEqual(result['lastSurface']['triangle'],[3,4,5])

    def test_primary_terminal_walk_reaches_a_real_contact_root_without_an_atlas_tip(self):
        owner=self.generator_owner();native,tri,start,p,n,uv,neighbors,charts,domain,terminal,settings=self.primary_walk_fixture()
        direction=np.asarray([-1.,-1.,0.])/np.sqrt(2)
        result=owner.primary_end_walk(native,tri,start,direction,lambda point,normal,sign,index:direction,
            p,n,uv,neighbors,charts,domain,terminal,settings,away=False)
        self.assertFalse(result['trueFreeEnd']);self.assertEqual(result['reason'],'confirmed-source-root')
        np.testing.assert_allclose(result['lastSurface']['barycentric'],[1.,0.,0.],rtol=0,atol=1e-6)

    def test_primary_terminal_walk_keeps_five_support_source_native_without_dropping_a_bone(self):
        owner=self.generator_owner();native,tri,start,p,n,uv,neighbors,charts,domain,terminal,settings=self.primary_walk_fixture()
        native['bindPoses']=np.tile(np.eye(4).ravel(),5).tolist()
        native['weights']=[1.,0,0,0,.5,.5,0,0,.5,.5,0,0]
        native['boneIndices']=[0,0,0,0,1,2,0,0,3,4,0,0]
        before=copy.deepcopy(native);direction=np.asarray([1.,1.,0.])/np.sqrt(2)
        result=owner.primary_end_walk(native,tri,start,direction,lambda point,normal,sign,index:direction,
            p,n,uv,neighbors,charts,domain,terminal,settings,away=True)
        self.assertFalse(result['trueFreeEnd']);self.assertEqual(result['reason'],'retained-native-five-support-region')
        self.assertEqual(native,before)

    def test_primary_atlas_refactor_preserves_existing_fine_rgba_and_normal_bytes(self):
        import hashlib
        owner=self.generator_owner()
        cases=[(None,'a95d3d5811b09aeeeb723bb18abb5193689f5f77bd27612a3774655a1cc26b29',
            '2d13c11eec10d73c343e50507f3dab6758ed9c9b4dd0d68a499a36262b1c73e4'),
            (dict(strandsPerTile=1,halfWidthPixels=[50.,54.],startRange=[.005,.025],endRange=[.975,.995],rootTaper=.02,tipTaper=.04),
            '8f983f44a6f2f2f31ff07dfd167d97d879d9359aafcf59df51fbb31999ff6c4a',
            'd635b2945adf9970a0e22d1731c82af5974dbac8b33564bee00d7fa75c971750')]
        for settings,rgba_digest,normal_digest in cases:
            rgba,normal,rows,_=owner.strand_atlas_arrays(settings)
            self.assertEqual(hashlib.sha256(rgba.tobytes()).hexdigest(),rgba_digest)
            self.assertEqual(hashlib.sha256(normal.tobytes()).hexdigest(),normal_digest)
            self.assertEqual((rgba.shape,normal.shape),((1024,512,4),(1024,512,3)))
            self.assertEqual(len(rows),16)

    def test_primary_atlas_has_body_and_true_tip_tiles_without_forcing_opaque_pixels(self):
        owner=self.generator_owner();rgba,normal,rows,settings=owner.strand_atlas_arrays(purpose='primary')
        alpha=rgba[:,:,3];tile_width,tile_height=128,256
        for tile in range(16):
            x,y=(tile%4)*tile_width,(tile//4)*tile_height
            data=alpha[y:y+tile_height,x:x+tile_width]
            self.assertGreater(int(data[tile_height//2].max()),178)
            self.assertLess(int(data[tile_height//2].min()),178)
            if tile<8:
                self.assertGreater(int(data[0].max()),178);self.assertGreater(int(data[-1].max()),178)
            else:self.assertEqual(int(data[-1].max()),0)
        self.assertGreater(normal[:,:,0].ptp()if hasattr(normal[:,:,0],'ptp')else np.ptp(normal[:,:,0]),0)
        with self.assertRaisesRegex(ValueError,'material purpose'):owner.strand_atlas_arrays(purpose='unowned')
        # Ordinary linear box mips are measured, not filled to pass cutoff.
        mip=alpha.astype(float)/255.;levels=[]
        while True:
            levels.append(dict(width=mip.shape[1],height=mip.shape[0],minimum=float(mip.min()),maximum=float(mip.max()),
                fractionAtNativeCutoff=float(np.mean(mip>=.7))))
            if mip.shape==(1,1):break
            h,w=mip.shape
            if h>1 and w>1:mip=mip.reshape(h//2,2,w//2,2).mean(axis=(1,3))
            elif h>1:mip=mip.reshape(h//2,2,w).mean(axis=1)
            else:mip=mip.reshape(h,w//2,2).mean(axis=2)
        self.assertEqual((levels[-1]['width'],levels[-1]['height']),(1,1))
        self.assertGreater(levels[-1]['minimum'],0)
        self.assertLess(levels[-1]['maximum'],1)

    def test_primary_atlas_recipe_owns_neutral_main_separately_from_fine_outputs(self):
        owner=self.generator_owner();captured={};texts={};written=[]
        class Sink:
            def __init__(self,name='root'):self.name=name
            def __truediv__(self,name):return Sink(name)
            def write_text(self,value,**kwargs):texts[self.name]=value
            def resolve(self):return Path('C:/primary-atlas-fixture')/self.name
        def pixels(array):
            copied=array.copy()
            return SimpleNamespace(save=lambda path:captured.update({path.name:copied}))
        def declare(*args,**kwargs):
            value=dict(*args,**kwargs)
            if {'name','path','normalMap','sha256'}<=value.keys():
                value['name']='fixture-texture-'+str(len(written));written.append(copy.deepcopy(value))
            return value
        # Vary only names in the actual declaration constructor, without a
        # production naming override or duplicated default artifact strings.
        with mock.patch.object(owner.Image,'fromarray',side_effect=pixels),mock.patch.object(owner,'sha',return_value='a'*64),\
                mock.patch.object(owner,'dict',side_effect=declare,create=True):
            result=owner.generate_strand_atlas(Sink(),purpose='primary')
        recipe=json.loads(texts['MeshRecipe.json'])
        self.assertEqual(len(recipe['textures']),3);self.assertEqual(recipe['meshes'],[])
        self.assertEqual(result['materialPurpose'],'primary-lock')
        properties=result['materialProperties'];declarations={row['name']:row for row in recipe['textures']}
        self.assertEqual(recipe['textures'],written)
        self.assertEqual(set(properties),{'_GroomColor','_GroomNormal','_MainTex'})
        self.assertEqual(set(properties.values()),set(declarations))
        self.assertEqual(set(captured),{row['path']for row in declarations.values()})
        for property,name in properties.items():
            self.assertEqual(declarations[name]['normalMap'],property=='_GroomNormal')
        np.testing.assert_array_equal(captured[declarations[properties['_MainTex']]['path']],np.full((1,1,4),255,np.uint8))
        self.assertEqual(result['recipe']['sha256'],result['recipeSha256'])
        self.assertEqual(result['tileEndModes'],['continuous-body']*8+['owned-free-tip']*8)
        arrays=owner.strand_atlas_arrays(purpose='primary')
        np.testing.assert_array_equal(captured[declarations[properties['_GroomColor']]['path']][::-1],arrays[0])
        np.testing.assert_array_equal(captured[declarations[properties['_GroomNormal']]['path']][::-1],arrays[1])

    def test_primary_material_purposes_require_actual_roles_and_exclusive_renderer_objects(self):
        owner=self.build_owner();pointer=lambda key:dict(m_FileID=0,m_PathID=key)
        def material(name,color,main,groom):
            return dict(m_Name=name,m_Shader=pointer(90),m_SavedProperties=dict(m_Floats=[['_Strength',1.]],
                m_Colors=[['_Color',color]],m_TexEnvs=[['_MainTex',dict(m_Texture=pointer(main))],
                    ['_GroomColor',dict(m_Texture=pointer(groom))],['_GroomNormal',dict(m_Texture=pointer(groom+1))]]))
        def packet(roles,submeshes,dimension=4):
            return dict(vertexCount=len(roles),subMeshes=submeshes,uv=[dict(channel=2,dimension=dimension,
                values=[x for role in roles for x in(0.,0.,float(role),1.)])])
        old=packet([0]*3+[49]*3+[57]*3,[[0,1,2],[3,4,5],[6,7,8]],3)
        finished=packet([0]*3+[33]*3+[49]*3,[[0,1,2,3,4,5,6,7,8]])
        trees={10:material('core A',.2,100,101),11:material('fine A',.2,100,201),12:material('primary A',.2,300,301),
               20:material('core B',.8,110,111),21:material('fine B',.8,110,211),22:material('primary B',.8,310,311),
               30:dict(m_Mesh=pointer(3),m_Materials=[pointer(i)for i in(10,11,12)]),
               31:dict(m_Mesh=pointer(3),m_Materials=[pointer(i)for i in(20,21,22)])}
        obj=lambda name,key:SimpleNamespace(type=SimpleNamespace(name=name),read_typetree=lambda:copy.deepcopy(trees[key]))
        objects={key:obj('Material'if key<30 else'SkinnedMeshRenderer',key)for key in trees};objects[3]=SimpleNamespace(type=SimpleNamespace(name='Mesh'))
        file=SimpleNamespace(objects=objects);before=copy.deepcopy(trees)
        purposes=[dict(mesh=3,sourceSlots=1,slots=[dict(sourceSlot=0,targetSlot=1,purpose='fine'),dict(sourceSlot=0,targetSlot=2,purpose='primary')])]
        options=dict(primary_vertices=[6,7,8],source_purposes=purposes)
        expected=[dict(sourceSlot=0,targetSlot=1,purpose='fine'),dict(sourceSlot=0,targetSlot=2,purpose='primary')]
        with mock.patch.object(owner,'export_current_mesh',return_value=old):
            self.assertEqual(owner.groom_fine_material_slots(file,3,finished,**options),expected)
            self.assertEqual(trees,before)
            # A primary declaration must describe actual atlas-card geometry.
            old['uv'][0]['values'][6*4+2]=33.
            with self.assertRaisesRegex(ValueError,'non-atlas-card'):owner.groom_fine_material_slots(file,3,finished,**options)
            old['uv'][0]['values'][6*4+2]=57.
            # Primary material sharing with a fine consumer is not exclusive.
            trees[30]['m_Materials'][2]=pointer(11)
            self.assertEqual(owner.groom_fine_material_slots(file,3,finished,**options),[
                dict(sourceSlot=0,targetSlot=3,purpose='fine'),dict(sourceSlot=0,targetSlot=4,purpose='primary')])
            trees[30]=copy.deepcopy(before[30])
            # Another LOD has padded four-lane storage even with dimension=3.
            other=packet([0]*3,[[0,1,2]],3);objects[40]=SimpleNamespace(type=SimpleNamespace(name='Mesh'))
            trees[41]=dict(m_Mesh=pointer(40),m_Materials=[pointer(12)]);objects[41]=obj('SkinnedMeshRenderer',41)
            with mock.patch.object(owner,'export_current_mesh',side_effect=lambda obj:old if obj is objects[3]else other):
                self.assertEqual(owner.groom_fine_material_slots(file,3,finished,**options),[
                    dict(sourceSlot=0,targetSlot=1,purpose='fine'),dict(sourceSlot=0,targetSlot=3,purpose='primary')])
                other['vertexCount']=4
                with self.assertRaisesRegex(ValueError,'vertex layout'):owner.groom_fine_material_slots(file,3,finished,**options)
            del objects[40],objects[41],trees[41]
            bad=copy.deepcopy(finished);bad['subMeshes'][0][-1]=3
            with self.assertRaisesRegex(ValueError,'mixes fine and primary'):owner.groom_fine_material_slots(file,3,bad,**options)
            trees[31]['m_Materials'].append(pointer(22))
            with self.assertRaisesRegex(ValueError,'overlays'):owner.groom_fine_material_slots(file,3,finished,**options)
        self.assertEqual(old['uv'][0]['dimension'],3)

    def test_primary_material_composition_retains_prefix_and_reuses_both_purpose_slots(self):
        owner=self.generator_owner();points=np.asarray([[0.,0.,0.],[1.,0.,0.],[0.,1.,0.],
            [.1,.1,.01],[.2,.1,.01],[.1,.2,.01],[.4,.4,.02],[.5,.4,.02],[.4,.5,.02]])
        source=dict(name='Native',vertexCount=9,vertices=points.ravel().tolist(),normals=[0.,0.,1.]*9,
            tangents=[1.,0.,0.,1.]*9,weights=[1.,0.,0.,0.]*9,boneIndices=[0]*36,colors=[],
            uv=[dict(channel=0,dimension=2,values=[0.,0.,0.,0.]*9),
                dict(channel=2,dimension=4,values=[0.,0.,0.,1.]*6+[0.,0.,49.,1.]*3)],
            subMeshes=[[3,4,5],[6,7,8]],bindPoses=np.eye(4).ravel().tolist(),indexFormat=0,
            bounds=[.5,.5,0.,1.,1.,.1],sourceFingerprint='0'*64,
            shapes=[dict(name='Fit',index=0,frames=[dict(weight=100.,vertices=[0.,0.,.001]*9,normals=[0.]*27,tangents=[0.]*27)])])
        finished=copy.deepcopy(source);finished['subMeshes']=[[0,1,2,3,4,5,6,7,8]]
        finished['uv'][1]['values']=[0.,0.,0.,1.]*3+[0.,0.,33.,1.]*3+[0.,0.,57.,1.]*3
        binding=dict(matrix=np.eye(4).ravel().tolist(),vertices=[dict(anchor=0,native=False,tangentW=1.,
            role='primary-lock'if i>=6 else'source-continuous-flow-ribbon')for i in range(9)])
        fine_slots=[dict(sourceSlot=0,targetSlot=1)];primary_slots=[dict(sourceSlot=0,targetSlot=2)]
        original=copy.deepcopy((source,finished,binding))
        packet,attachment,proof=owner.compose_finished_source_groom(source,finished,binding,
            fine_material_slots=fine_slots,primary_material_slots=primary_slots)
        self.assertEqual(packet['subMeshes'],[[3,4,5],[6,7,8],[9,10,11]])
        self.assertEqual(proof['primaryMaterialSlots'],primary_slots);self.assertEqual(proof['addedPrimaryVertices'],3)
        self.assertEqual(proof['addedFineVertices'],6);self.assertEqual(proof['addedPrimaryTriangles'],1)
        for key,width in(('vertices',3),('normals',3),('tangents',4),('weights',4),('boneIndices',4)):
            self.assertEqual(packet[key][:6*width],source[key][:6*width])
        self.assertEqual(packet['shapes'][0]['frames'][0]['vertices'][:18],source['shapes'][0]['frames'][0]['vertices'][:18])
        repeated,_,_=owner.compose_finished_source_groom(packet,finished,binding,
            fine_material_slots=fine_slots,primary_material_slots=primary_slots)
        self.assertEqual(repeated,packet);self.assertEqual((source,finished,binding),original)
        with self.assertRaisesRegex(ValueError,'dedicated material purpose'):
            owner.compose_finished_source_groom(source,finished,binding,fine_material_slots=fine_slots)
        with self.assertRaisesRegex(ValueError,'repeat a target'):
            owner.compose_finished_source_groom(source,finished,binding,fine_material_slots=fine_slots,primary_material_slots=fine_slots)
        bad=copy.deepcopy(finished);bad['subMeshes'][0][-1]=3
        with self.assertRaisesRegex(ValueError,'mixes primary and fine'):
            owner.compose_finished_source_groom(source,bad,binding,fine_material_slots=fine_slots,primary_material_slots=primary_slots)

    def test_primary_atlas_saved_recipe_and_every_mip_keep_their_owned_properties(self):
        owner=self.build_owner();import character_resources as resources
        with tempfile.TemporaryDirectory()as directory:
            root=Path(directory)
            def payload(name,data):
                path=root/name;path.write_bytes(data if isinstance(data,bytes)else json.dumps(data).encode())
                return dict(path=str(path),sha256=owner.sha(path))
            properties={property:'TextureFixture'+str(index)for index,property in enumerate(('_MainTex','_GroomNormal','_GroomColor'),1)}
            declarations=[];saved=[];objects={};pixels={};blocks={};ids_by_property={}
            for identifier,(property,name)in enumerate(properties.items(),37):
                ids_by_property[property]=identifier;gate=property=='_MainTex';width,height=(1,1)if gate else(4,8)
                image=payload('input-'+str(identifier)+'.png',name.encode());normal=property=='_GroomNormal'
                declarations.append(dict(name=name,path=Path(image['path']).name,sha256=image['sha256'],normalMap=normal,sRGB=False,mipmaps=True,compressionQuality=100))
                levels=[np.full((max(1,height>>i),max(1,width>>i),4),255,np.uint8)for i in range(max(width,height).bit_length())]
                pixels[identifier]=levels;blocks[identifier]=[v.tobytes()for v in levels]
                tree=dict(m_Width=width,m_Height=height,m_MipCount=len(levels),m_TextureSettings=dict(m_FilterMode=2,m_WrapU=1,m_WrapV=1,m_Aniso=2,m_MipBias=0.))
                objects[identifier]=SimpleNamespace(type=SimpleNamespace(name='Texture2D'),path_id=identifier,
                    peek_name=lambda value=name:value,read_typetree=lambda value=tree:copy.deepcopy(value))
                saved.append(dict(declarations[-1],inputSha256=image['sha256'],exact=True,importSettingsExact=True,crunched=False,readable=True,
                    width=width,height=height,mipCount=len(levels),rawBytes=sum(map(len,blocks[identifier])),rawSha256=owner.hashlib.sha256(b''.join(blocks[identifier])).hexdigest()))
            compiled_name='fixture-atlas.payload.bundle'
            recipe=payload('MeshRecipe.json',dict(schemaVersion=1,bundleName=compiled_name,meshes=[],textures=declarations))
            atlas=payload('Atlas.json',dict(status='PRIMARY_LOCK_ATLAS_PREPARED_RENDER_REVIEW_PENDING',materialPurpose='primary-lock',
                materialProperties=properties,tileEndModes=['continuous-body']*8+['owned-free-tip']*8,
                recipe=recipe,recipeSha256=recipe['sha256'],width=4,height=8))
            bundle=payload(compiled_name,b'compiled texture fixture')
            child=dict(success=True,stage='complete',validationScope='mesh_templates',actualColorSpace='Linear',inputSha256=recipe['sha256'],
                bundlePath=bundle['path'],bundleSha256=bundle['sha256'],createdTextures=copy.deepcopy(saved),savedTextures=copy.deepcopy(saved),
                inputAssets=[dict(path=v['path'],sha256=v['sha256'])for v in declarations])
            report=payload('unity-report.json',dict(success=True,stage='complete',bundles=[child]))
            with mock.patch.object(resources,'load_serialized_bundle',return_value=(None,SimpleNamespace(objects=objects))),\
                    mock.patch.object(resources,'texture_mip_payloads',side_effect=lambda obj:(4,blocks[obj.path_id])),\
                    mock.patch.object(resources,'texture_mip_images',side_effect=lambda obj:pixels[obj.path_id]):
                result=owner.groom_primary_atlas(atlas['path'],report['path'])
                self.assertEqual(set(result['materialProperties']),set(properties));self.assertFalse(result['artAccepted'])
                self.assertEqual(result['materialProperties']['_MainTex']['object'],ids_by_property['_MainTex'])
                self.assertEqual(len(result['textureReports'][properties['_GroomColor']]['mips']),4)
                for key,change,message in(('inputSha256','0'*64,'compilation authority'),('actualColorSpace','Gamma','compilation authority'),
                        ('bundleSha256','0'*64,'bundle changed')):
                    broken=copy.deepcopy(child);broken[key]=change
                    trial=payload('broken-report.json',dict(success=True,stage='complete',bundles=[broken]))
                    with self.subTest(key=key),self.assertRaisesRegex(ValueError,message):owner.groom_primary_atlas(atlas['path'],trial['path'])
                # A saved payload can match its report and still lose all body alpha at a coarse mip.
                mask_id=ids_by_property['_GroomColor'];gate_id=ids_by_property['_MainTex']
                pixels[mask_id][-1][:,:,3]=0;blocks[mask_id][-1]=pixels[mask_id][-1].tobytes()
                broken=copy.deepcopy(child)
                for field in('createdTextures','savedTextures'):
                    next(value for value in broken[field]if value['name']==properties['_GroomColor'])['rawSha256']=owner.hashlib.sha256(b''.join(blocks[mask_id])).hexdigest()
                trial=payload('coarse-report.json',dict(success=True,stage='complete',bundles=[broken]))
                with self.assertRaisesRegex(ValueError,'collapses'):owner.groom_primary_atlas(atlas['path'],trial['path'])
                pixels[mask_id][-1][:,:,3]=255;blocks[mask_id][-1]=pixels[mask_id][-1].tobytes()
                pixels[gate_id][0][0,0,0]=254
                with self.assertRaisesRegex(ValueError,'not neutral'):owner.groom_primary_atlas(atlas['path'],report['path'])
                pixels[gate_id][0][0,0,0]=255
                doc=json.loads(Path(atlas['path']).read_bytes());doc['materialProperties']['_MainTex']=properties['_GroomColor']
                wrong=payload('wrong-atlas.json',doc)
                with self.assertRaisesRegex(ValueError,'property roles'):owner.groom_primary_atlas(wrong['path'],report['path'])
                with self.assertRaisesRegex(ValueError,'both'):owner.groom_primary_atlas(atlas['path'],None)
                # Texture and file names come only from the declared mapping;
                # shader property roles remain fixed by the rendering contract.
                renamed=[];renamed_saved=[];renamed_properties={}
                for index,(property,name)in enumerate(properties.items(),1):
                    identifier=ids_by_property[property];object_name='RenamedFixture'+str(index);image=payload('arbitrary-file-'+str(index)+'.png',object_name.encode())
                    declaration=dict(next(value for value in declarations if value['name']==name),name=object_name,path=Path(image['path']).name,sha256=image['sha256'])
                    renamed.append(declaration);renamed_properties[property]=object_name
                    renamed_saved.append(dict(next(value for value in saved if value['name']==name),name=object_name,path=declaration['path'],sha256=image['sha256'],inputSha256=image['sha256']))
                    objects[identifier].peek_name=lambda value=object_name:value
                renamed_recipe=payload('RenamedRecipe.json',dict(schemaVersion=1,bundleName=compiled_name,meshes=[],textures=renamed))
                renamed_atlas=payload('RenamedAtlas.json',dict(json.loads(Path(atlas['path']).read_bytes()),
                    materialProperties=renamed_properties,recipe=renamed_recipe,recipeSha256=renamed_recipe['sha256']))
                renamed_child=dict(child,inputSha256=renamed_recipe['sha256'],createdTextures=renamed_saved,savedTextures=copy.deepcopy(renamed_saved),
                    inputAssets=[dict(path=value['path'],sha256=value['sha256'])for value in renamed])
                renamed_report=payload('RenamedReport.json',dict(success=True,stage='complete',bundles=[renamed_child]))
                result=owner.groom_primary_atlas(renamed_atlas['path'],renamed_report['path'])
                self.assertEqual(result['materialProperties']['_MainTex']['name'],renamed_properties['_MainTex'])
                swapped=json.loads(Path(renamed_atlas['path']).read_bytes())
                swapped['materialProperties']['_GroomColor'],swapped['materialProperties']['_GroomNormal']=swapped['materialProperties']['_GroomNormal'],swapped['materialProperties']['_GroomColor']
                bad=payload('SwappedRoleAtlas.json',swapped)
                with self.assertRaisesRegex(ValueError,'sampling role'):owner.groom_primary_atlas(bad['path'],renamed_report['path'])

    def test_primary_material_ledger_replays_selected_files_and_keeps_unrelated_owners(self):
        owner=self.build_owner()
        with tempfile.TemporaryDirectory()as directory:
            root=Path(directory)
            def document(name,value):
                path=root/name;owner.write(path,value);return dict(path=str(path),sha256=owner.sha(path))
            original=dict(file='part.bundle',objects=8,sha256='1'*64,allRetainedObjectFieldsExactAfterReferenceRebasing=True)
            held=dict(file='other.bundle',mesh=77,sourceSlots=1,slots=[dict(sourceSlot=0,targetSlot=1,purpose='fine')])
            old=dict(file='part.bundle',mesh=3,sourceSlots=1,slots=[dict(sourceSlot=0,targetSlot=1,purpose='fine')])
            source=dict(assembly=document('baseline.json',dict(proofs=[original],groomSourceDesigns=dict(materialPurposes=[old,held]))),
                catalog=document('catalog.json',dict(bundles=[dict(file='part.bundle',sha256='1'*64)])))
            slots=[dict(sourceSlot=0,targetSlot=1,purpose='fine'),dict(sourceSlot=0,targetSlot=2,purpose='primary')]
            fibers=document('fibers.json',{})
            planned=[dict(source=dict(path='part.bundle'),mesh=3,sourceSlots=1,materialSlots=slots)]
            design=document('plan.json',dict(mode='retained-source-support',source=source['assembly'],fibers=fibers,rows=planned))
            receipt=dict(source=dict(sha256='1'*64),target=dict(sha256='2'*64),file='part.bundle',designPlan=design,designFiles=['part.bundle'],
                rows=copy.deepcopy(planned),composition=dict(plan=design,fibers=fibers))
            operation=dict(kind='source-owned-groom-surface',beforeSha256='1'*64,sha256='2'*64,proof=document('receipt.json',receipt),authority=dict(inputs=[]))
            row=dict(original,sha256='2'*64,allRetainedObjectFieldsExactAfterReferenceRebasing=False,appearanceFinishing=[operation])
            expected=[held,dict(file='part.bundle',mesh=3,sourceSlots=1,slots=slots)]
            assembly=dict(preparedSource=source,groomSourceDesigns=dict(plan=design,materialPurposes=expected))
            with mock.patch.object(owner,'ROOT',root),mock.patch.object(owner,'validate_owned_groom_surface')as replay:
                owner.validate_prepared_resource(row,assembly,{});replay.assert_called_once()
                for change in(lambda v:v.pop(),lambda v:v[1]['slots'].reverse(),lambda v:v[0].update(mesh=3)):
                    bad=copy.deepcopy(assembly);change(bad['groomSourceDesigns']['materialPurposes'])
                    with self.assertRaisesRegex(ValueError,'exact source and composition'):owner.validate_prepared_resource(row,bad,{})
                bad=copy.deepcopy(assembly);bad['groomSourceDesigns']['plan']=source['catalog']
                with self.assertRaisesRegex(ValueError,'prepared source or design'):owner.validate_prepared_resource(row,bad,{})
                # A new non-composed intermediate no longer claims the previous dedicated slots.
                plan=json.loads(Path(design['path']).read_bytes());plan.pop('mode')
                fresh=document('first-stage.json',plan);receipt['designPlan']=fresh;receipt.pop('composition')
                operation['proof']=document('receipt-stage.json',receipt)
                row['appearanceFinishing']=[operation];assembly['groomSourceDesigns']=dict(plan=fresh,materialPurposes=[held])
                owner.validate_prepared_resource(row,assembly,{})

    def test_primary_material_ledger_inherits_exactly_for_unchanged_and_frame_only_assemblies(self):
        owner=self.build_owner()
        with tempfile.TemporaryDirectory()as directory:
            root=Path(directory)
            def document(name,value):
                path=root/name;owner.write(path,value);return dict(path=str(path),sha256=owner.sha(path))
            def payload(name,value):
                path=root/name;path.write_bytes(value);return dict(path=str(path),sha256=owner.sha(path))
            before=payload('source.bundle',b'synthetic source');after=payload('frame.bundle',b'synthetic frame')
            original=dict(file='part.bundle',objects=8,sha256=before['sha256'],allRetainedObjectFieldsExactAfterReferenceRebasing=True)
            ledger=[dict(file='part.bundle',mesh=3,sourceSlots=1,slots=[dict(sourceSlot=0,targetSlot=1,purpose='fine')])]
            source=dict(assembly=document('baseline.json',dict(proofs=[original],groomSourceDesigns=dict(materialPurposes=ledger))),
                catalog=document('catalog.json',dict(bundles=[dict(file='part.bundle',sha256=before['sha256'])])))
            row=copy.deepcopy(original);assembly=dict(preparedSource=source,proofs=[row],groomSourceDesigns=dict(materialPurposes=copy.deepcopy(ledger)))
            with mock.patch.object(owner,'ROOT',root),mock.patch.object(owner,'validate_owned_groom_surface')as groom,\
                    mock.patch.object(owner,'validate_groom_vertex_frames')as frame:
                cache={};owner.validate_prepared_resource(row,assembly,cache);groom.assert_not_called()
                for change in(lambda value:value['groomSourceDesigns']['materialPurposes'][0]['slots'][0].update(purpose='primary'),
                        lambda value:value['groomSourceDesigns'].pop('materialPurposes')):
                    bad=copy.deepcopy(assembly);change(bad)
                    with self.assertRaisesRegex(ValueError,'exact source and composition'):owner.validate_prepared_resource(bad['proofs'][0],bad,cache)
                operation=dict(kind='groom-vertex-normal-frame',beforeSha256=before['sha256'],sha256=after['sha256'],source=before,target=after,authority=dict(inputs=[]))
                row.update(sha256=after['sha256'],appearanceFinishing=[operation],allRetainedObjectFieldsExactAfterReferenceRebasing=False)
                owner.validate_prepared_resource(row,assembly,cache);frame.assert_called_once();groom.assert_not_called()
                bad=copy.deepcopy(assembly);bad['groomSourceDesigns']['materialPurposes'][0]['slots'][0]['purpose']='primary'
                with self.assertRaisesRegex(ValueError,'exact source and composition'):owner.validate_prepared_resource(bad['proofs'][0],bad,cache)

    def test_primary_material_ledger_caches_one_global_replay_and_rechecks_changed_provenance(self):
        owner=self.build_owner()
        with tempfile.TemporaryDirectory()as directory:
            root=Path(directory)
            def document(name,value):
                path=root/name;owner.write(path,value);return dict(path=str(path),sha256=owner.sha(path))
            originals=[dict(file='part.bundle'if index==0 else'other-'+str(index)+'.bundle',objects=8,sha256='1'*64,
                allRetainedObjectFieldsExactAfterReferenceRebasing=True)for index in range(17)]
            source=dict(assembly=document('baseline.json',dict(proofs=originals)),catalog=document('catalog.json',dict(bundles=[dict(file=value['file'],sha256=value['sha256'])for value in originals])))
            slots=[dict(sourceSlot=0,targetSlot=1,purpose='fine'),dict(sourceSlot=0,targetSlot=2,purpose='primary')]
            planned=dict(source=dict(path='part.bundle'),mesh=3,sourceSlots=1,materialSlots=slots)
            fibers=document('fibers.json',{});plan=dict(mode='retained-source-support',source=source['assembly'],fibers=fibers,rows=[planned])
            design=document('plan.json',plan)
            receipt=dict(source=dict(sha256='1'*64),target=dict(sha256='2'*64),file='part.bundle',designPlan=design,designFiles=['part.bundle'],
                rows=[copy.deepcopy(planned)],composition=dict(plan=design,fibers=fibers))
            receipt_pin=document('receipt.json',receipt)
            operation=dict(kind='source-owned-groom-surface',beforeSha256='1'*64,sha256='2'*64,proof=receipt_pin,authority=dict(inputs=[]))
            rows=copy.deepcopy(originals);rows[0].update(sha256='2'*64,appearanceFinishing=[operation],allRetainedObjectFieldsExactAfterReferenceRebasing=False)
            ledger=[dict(file='part.bundle',mesh=3,sourceSlots=1,slots=copy.deepcopy(slots))]
            assembly=dict(preparedSource=source,proofs=rows,groomSourceDesigns=dict(plan=design,materialPurposes=ledger));reads={}
            real_read=owner.read
            def read(path):
                key=str(Path(path));reads[key]=reads.get(key,0)+1;return real_read(path)
            with mock.patch.object(owner,'ROOT',root),mock.patch.object(owner,'read',side_effect=read),\
                    mock.patch.object(owner,'validate_owned_groom_surface')as replay:
                cache={}
                # Even unchanged resources validate the global table before returning.
                for value in reversed(rows):owner.validate_prepared_resource(value,assembly,cache)
                replay.assert_called_once();self.assertEqual(reads[receipt_pin['path']],1);self.assertEqual(reads[design['path']],1)
                bad=copy.deepcopy(assembly);bad['groomSourceDesigns']['materialPurposes'][0]['slots'][1]['purpose']='fine'
                with self.assertRaisesRegex(ValueError,'exact source and composition'):owner.validate_prepared_resource(bad['proofs'][-1],bad,cache)
                self.assertEqual(replay.call_count,1)
                helper=root/'producer.txt';helper.write_text('another owner','utf-8')
                operation['authority']['inputs']=[dict(path=str(helper),sha256=owner.sha(helper))]
                owner.validate_prepared_resource(rows[-1],assembly,cache);self.assertEqual(replay.call_count,2)
                self.assertEqual(reads[receipt_pin['path']],1);self.assertEqual(reads[design['path']],1)
                good_keys=len([key for key in cache if isinstance(key,tuple)and key[0]=='groom-material-purposes'])
                for change in(lambda value:value.update(rows=[]),lambda value:value['rows'].append(copy.deepcopy(value['rows'][0]))):
                    broken=copy.deepcopy(receipt);change(broken);broken_pin=document('broken-'+str(len(reads))+'.json',broken)
                    bad=copy.deepcopy(assembly);bad['proofs'][0]['appearanceFinishing'][0]['proof']=broken_pin
                    with self.assertRaisesRegex(ValueError,'actual receipt rows'):owner.validate_prepared_resource(bad['proofs'][-1],bad,cache)
                    self.assertEqual(len([key for key in cache if isinstance(key,tuple)and key[0]=='groom-material-purposes']),good_keys)
                # A declaration cannot rewrite the ledger for a second file whose receipt is missing.
                expanded=copy.deepcopy(plan);expanded['rows'].append(dict(planned,source=dict(path=rows[1]['file']),mesh=33))
                expanded_pin=document('expanded-plan.json',expanded);broken=copy.deepcopy(receipt)
                broken.update(designPlan=expanded_pin,designFiles=sorted(['part.bundle',rows[1]['file']]),composition=dict(plan=expanded_pin,fibers=fibers))
                bad=copy.deepcopy(assembly);bad['groomSourceDesigns']['plan']=expanded_pin
                bad['proofs'][0]['appearanceFinishing'][0]['proof']=document('missing-receipt.json',broken)
                with self.assertRaisesRegex(ValueError,'omitted an actual resource receipt'):owner.validate_prepared_resource(bad['proofs'][-1],bad,cache)
                self.assertEqual(replay.call_count,2)

    def test_primary_material_replay_rejects_core_edits_and_cross_purpose_atlas_properties(self):
        owner=self.build_owner();import character_resources as resources
        with tempfile.TemporaryDirectory()as directory:
            root=Path(directory)
            def payload(name,data):
                path=root/name;path.write_bytes(data if isinstance(data,bytes)else json.dumps(data).encode())
                return dict(path=str(path),sha256=owner.sha(path))
            source=payload('part.bundle',b'source');target=payload('target.bundle',b'target');geometry=payload('geometry.bundle',b'geometry')
            fibers_bundle=payload('fibers.bundle',b'fibers');compiled=payload('compiled.bundle',b'compiled');producer=payload('GenerateFlowCoverageSweptProfile.py',b'#fixture')
            original=payload('assembly.json',dict(groomSourceDesigns=dict(materialPurposes=[])))
            fibers=payload('fibers.json',dict(proofs=[dict(file='fibers.bundle',sha256=fibers_bundle['sha256'])]))
            atlas_bundle=payload('atlas.bundle',b'atlas');atlas_plan=payload('atlas.json',{});atlas_report=payload('atlas-report.json',{})
            properties={key:dict(atlas_bundle,object=i,name='AtlasFixture'+str(i))for i,key in enumerate(('_GroomColor','_GroomNormal','_MainTex'),101)}
            primary=dict(plan=atlas_plan,report=atlas_report,materialProperties=properties)
            slots=[dict(sourceSlot=0,targetSlot=1,purpose='primary')]
            plan=payload('plan.json',dict(source=original,producer=producer,primaryAtlas=primary))
            packet=dict(sourceFingerprint='finished');binding=dict(vertices=[dict(role='primary-lock')]);facts=dict(sourceSupportRetained=True)
            row=dict(mesh=3,asset='fixture',source=source,fibers=fibers_bundle,binding=payload('binding.bytes',b'binding'),
                clothBinding=payload('cloth.bytes',b'encoded'),packet=payload('packet.json',packet),compiledBundle=compiled,compiledName='Compiled',composition=facts,materialSlots=slots)
            texture_receipt=dict(textures=[dict(object=i,source=value)for i,value in enumerate(properties.values(),201)],
                materialUpdates=[dict(pathId=20,property=key,before=dict(m_FileID=0,m_PathID=0),after=dict(m_FileID=0,m_PathID=i))for i,key in enumerate(properties,201)])
            first=payload('source-catalog.json',{});last=payload('target-catalog.json',{})
            receipt=dict(source=source,target=target,file='part.bundle',rows=[row],composition=dict(plan=plan,fibers=fibers),
                parameterCatalogs=dict(source=first,target=last,files=['part.bundle']),engineReport=payload('report.json',dict(success=True)),
                materialFloatUpdates=[],rendererMaterialUpdates=[],geometryTarget=geometry,materialTextures=texture_receipt,
                objectsAdded=[20,201,202,203],changedObjects=[1,3,20,201,202,203])
            mesh=lambda name:SimpleNamespace(type=SimpleNamespace(name='Mesh'),packet=dict(sourceFingerprint=name),read_typetree=lambda:{},peek_name=lambda:'Compiled')
            sf=SimpleNamespace(objects={3:mesh('source')});df=SimpleNamespace(objects={3:mesh('donor')});cf=SimpleNamespace(objects={7:mesh('finished')})
            renderer=SimpleNamespace(type=SimpleNamespace(name='SkinnedMeshRenderer'),read_typetree=lambda:dict(m_Mesh=dict(m_FileID=0,m_PathID=3),m_Materials=[dict(m_FileID=0,m_PathID=i)for i in(10,20)]))
            gf=SimpleNamespace(objects={3:mesh('finished'),8:renderer});files={Path(source['path']):sf,Path(fibers_bundle['path']):df,Path(compiled['path']):cf,Path(geometry['path']):gf}
            converter=SimpleNamespace(decode_binding=lambda _:binding,read=lambda path:json.loads(Path(path).read_bytes()),
                compose_finished_source_groom=lambda *_,**options:(packet,binding,facts),encode_legacy_binding=lambda _,path:Path(path).write_bytes(b'encoded'))
            key=('groom-parameter-catalogs',plan['sha256'],original['sha256'],first['sha256'],last['sha256'],('part.bundle',))
            def verified():return {key:{'part.bundle':[]},('groom-surface-owner',producer['sha256']):converter,('validated-resource',fibers['sha256'],'fibers.bundle'):True}
            with mock.patch.object(resources,'load_serialized_bundle',side_effect=lambda path,**_:(None,files[Path(path)])),\
                    mock.patch.object(resources,'groom_mesh_identifiers',return_value={3}),mock.patch.object(owner,'groom_primary_atlas',return_value=primary),\
                    mock.patch.object(owner,'groom_fine_material_slots',return_value=slots),mock.patch.object(owner,'export_current_mesh',side_effect=lambda obj:copy.deepcopy(obj.packet)),\
                    mock.patch.object(resources,'prepare_standalone_part',return_value=dict(target=geometry,objectsAdded=[20],changedObjects=[3,20])),\
                    mock.patch.object(resources,'validate_owned_material_textures'),\
                    mock.patch.object(resources,'replace_owned_material_textures',return_value=dict(target=target,objectsAdded=[201,202,203],materialUpdates=[dict(pathId=20)],metadataObject=1)):
                owner.validate_owned_groom_surface(receipt,[producer],verified())
                for change in(lambda v:v['materialUpdates'][0].update(pathId=10),lambda v:v['materialUpdates'].pop(),
                        lambda v:v['textures'][0]['source'].update(name=properties['_GroomNormal']['name']),lambda v:v['materialUpdates'].append(copy.deepcopy(v['materialUpdates'][0]))):
                    bad=copy.deepcopy(receipt);change(bad['materialTextures'])
                    with self.assertRaisesRegex(ValueError,'material purpose|local material texture owner'):
                        owner.validate_owned_groom_surface(bad,[producer],verified())


    def test_primary_material_fingerprint_revalidates_scalar_types_and_rejects_nonfinite_state(self):
        owner=self.build_owner();real_dumps=json.dumps;checks=[]
        def dumps(value,*args,**kwargs):
            if isinstance(value,dict)and set(value)=={'source','baseline','target','operations'}:checks.append(1)
            return real_dumps(value,*args,**kwargs)
        with tempfile.TemporaryDirectory()as directory:
            root=Path(directory)
            def document(name,value):
                path=root/name;owner.write(path,value);return dict(path=str(path),sha256=owner.sha(path))
            row=dict(file='fixture.bundle',objects=8,sha256='1'*64,allRetainedObjectFieldsExactAfterReferenceRebasing=True)
            prepared=dict(assembly=document('baseline.json',dict(proofs=[row])),
                catalog=document('catalog.json',dict(bundles=[dict(file=row['file'],sha256=row['sha256'])])))
            assembly=dict(preparedSource=prepared,proofs=[row],groomSourceDesigns=dict(probe=1));cache={}
            with mock.patch.object(owner,'ROOT',root),mock.patch.object(owner.json,'dumps',side_effect=dumps):
                owner.validate_prepared_resource(row,assembly,cache);self.assertEqual(len(checks),1)
                owner.validate_prepared_resource(row,assembly,cache);self.assertEqual(len(checks),1)
                # Python value equality must not merge distinct serialized scalar types.
                assembly['groomSourceDesigns']['probe']=True
                owner.validate_prepared_resource(row,assembly,cache);self.assertEqual(len(checks),2)
                good_keys={key for key in cache if isinstance(key,tuple)and key[0]=='groom-material-purposes'}
                self.assertEqual(len(good_keys),2)
                assembly['groomSourceDesigns']['nonfinite']=float('nan')
                for expected in(3,4):
                    with self.assertRaises(ValueError):owner.validate_prepared_resource(row,assembly,cache)
                    self.assertEqual(len(checks),expected)
                    self.assertEqual({key for key in cache if isinstance(key,tuple)and key[0]=='groom-material-purposes'},good_keys)
                assembly['groomSourceDesigns'].pop('nonfinite')
                owner.validate_prepared_resource(row,assembly,cache);self.assertEqual(len(checks),4)
                assembly['groomSourceDesigns']['probe']=1
                owner.validate_prepared_resource(row,assembly,cache);self.assertEqual(len(checks),4)


class SourceCarrierCellTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):cls.owner=GroomAttachmentTests.generator_owner()
    @classmethod
    def packet_fixture(cls):
        p=np.array([[-.01,0.,0.],[.01,0.,0.],[0.,.04,0.],[1.,0.,0.],[1.01,0.,0.],[1.,.01,0.]])
        matrix=np.eye(4).ravel().tolist()
        native=dict(name='Fixture',vertexCount=6,vertices=p.ravel().tolist(),normals=[0.,0.,1.]*6,
            tangents=[1.,0.,0.,1.]*6,colors=[],weights=[1.,0.,0.,0.]*6,boneIndices=[0]*24,
            bindPoses=matrix,subMeshes=[[0,1,2],[3,4,5]],indexFormat=0,bounds=[.5,.02,0.,1.04,.04,.02],
            uv=[dict(channel=0,dimension=2,values=[value for x,y in((0.,0.),(1.,0.),(.5,1.),(0.,0.),(1.,0.),(0.,1.))for value in(x,y,0.,0.)])],
            shapes=[dict(name='Wind',index=0,frames=[])])
        frame=dict(weight=100.,vertices=np.c_[np.zeros(6),np.zeros(6),p[:,1]*.025].ravel().tolist(),normals=[0.]*18,tangents=[0.]*18)
        native['shapes'][0]['frames']=[frame]
        native['shapes'].append(dict(name='BodyFit',index=1,frames=[dict(weight=100.,vertices=np.c_[p[:,0]*.05,np.zeros(6),np.zeros(6)].ravel().tolist(),normals=[0.]*18,tangents=[0.]*18)]))
        native['sourceFingerprint']=cls.owner.fingerprint(native)[0]
        def expanded(points,roles,submeshes):
            value=copy.deepcopy(native);points=np.asarray(points);added=len(points)-6;value.update(vertexCount=len(points),vertices=points.ravel().tolist(),subMeshes=submeshes)
            for key,width in(('normals',3),('tangents',4),('weights',4),('boneIndices',4)):value[key]+=value[key][:width]*added
            value['uv'][0]['values'] += [float(v)for q in points[6:]for v in((q[0]+.01)/.02,q[1]/.04,0.,0.)]
            value['uv'].append(dict(channel=2,dimension=4,values=np.asarray(roles).ravel().tolist()))
            value['shapes'][0]['frames'][0].update(vertices=np.c_[np.zeros(len(points)),np.zeros(len(points)),points[:,1]*.025].ravel().tolist(),normals=[0.]*(len(points)*3),tangents=[0.]*(len(points)*3))
            value['shapes'][1]['frames'][0].update(vertices=np.c_[points[:,0]*.05,np.zeros(len(points)),np.zeros(len(points))].ravel().tolist(),normals=[0.]*(len(points)*3),tangents=[0.]*(len(points)*3))
            value['sourceFingerprint']=cls.owner.fingerprint(value)[0];return value
        centre=p[:3].mean(axis=0)+[0.,0.,.0003]
        oldfine=np.asarray([[x,y,.001]for y in(.005,.02,.035)for x in(-.001,.001)]*12)
        points=np.concatenate((p,[centre],oldfine));oldtri=[]
        for index in range(7,len(points)-3,4):oldtri.extend([index,index+1,index+2,index+1,index+3,index+2])
        current=expanded(points,[[0.,0.,0.,1.]]*7+[[0.,0.,49.,1.]]*len(oldfine),[[0,1,6,1,2,6,2,0,6]+oldtri,[3,4,5]])
        field=dict(guide=0,uMetres=[-.01,.01,0.],v=[0.,0.,1.],halfWidthMetres=.012,heightMetres=.0003,
            lengthMetres=.04,terminalDistanceMetres=[.04,.04,0.],terminalRegionMetres=.009,tipWidthFraction=.04)
        plan=cls.owner.plan_curved_core(p[:,:2],[np.array([[0,1,2]]),np.array([[3,4,5]])],[np.ones(1,bool),np.zeros(1,bool)],.024,500,
            reusable_vertices=[0,1],positions=p,physical_groups=np.arange(6),flow_fields={(0,0):[field]},protected_vertices=[0,1])
        source_p=np.stack([p]+[p+np.asarray(shape['frames'][0]['vertices']).reshape(-1,3)for shape in native['shapes']]);source_n=np.tile([0.,0.,1.],(3,6,1));source_t=np.tile([1.,0.,0.],(3,6,1))
        cls.owner.prepare_geometry_frames(plan,source_p);allocated=[row for row in plan['vertices']if 'nativeVertex'not in row]
        cp,cn,ct=cls.owner.evaluate_curved_core(dict(plan,vertices=allocated),p,source_n[0],source_t[0],p[:,:2])
        pp,nn,tt,_=cls.owner.bound_curved_core(plan,cp,cn,ct,source_p,source_n,source_t,np.cross(source_n,source_t),np.eye(3))
        fine=np.asarray([[-.0005,.005,.0008],[.0005,.005,.0008],[-.0005,.025,.0008],[.0005,.025,.0008]])
        mapping=[];next_index=10
        for record in plan['vertices']:
            mapping.append(record['nativeVertex']if 'nativeVertex'in record else next_index)
            next_index+=int('nativeVertex'not in record)
        core=[[mapping[index]for index in tri]for tri in plan['triangles'][0]]
        packet=expanded(np.concatenate((p,fine,pp[0])),[[0.,0.,0.,1.]]*6+[[x,y,49.,1.2]for y in(0.,1.)for x in(0.,1.)]+[[0.,0.,0.,1.]]*len(cp),
            [[6,7,8,7,9,8]+np.asarray(core).ravel().tolist(),[3,4,5]])
        packet['normals'][30:]=nn[0].ravel().tolist();packet['tangents'][40:]=np.c_[tt[0],np.ones(len(ct))].ravel().tolist()
        for pose,shape in enumerate(packet['shapes'],1):
            outframe=shape['frames'][0]
            for key,values in(('vertices',pp),('normals',nn),('tangents',tt)):outframe[key][30:]=(values[pose]-values[0]).ravel().tolist()
        records=[dict(native=True,anchor=i,role='native',slot=-1,uv0=[0.,0.],tangentW=1.)for i in range(6)]
        for q in fine:
            bary=[(.04-q[1])/.08,(.04-q[1])/.08,q[1]/.04]
            records.append(dict(native=False,anchor=0,role='thin-root',slot=0,uv0=[.5,q[1]/.04],tangentW=1.,
                surface=dict(triangle=[0,1,2],barycentric=bary),geometrySurface=dict(triangle=[0,1,2],barycentric=bary)))
        distances=[]
        for record in allocated:
            bary=np.asarray(record['barycentric']);triangle=record['triangle'];anchor=int(triangle[int(bary.argmax())]);q=bary@p[triangle]
            records.append(dict(native=False,anchor=anchor,role='volume-support',slot=0,uv0=[(q[0]+.01)/.02,q[1]/.04],tangentW=1.,
                surface=dict(triangle=list(triangle),barycentric=bary.tolist()),geometrySurface=copy.deepcopy(record['geometrySurface'])))
            distances.append(float(bary@np.asarray([.04,.04,0.])))
        surface=dict(nativeFingerprint=native['sourceFingerprint'],firstVertex=10,vertices=len(cp),sourceFacesBySlot=[[0],[]],
            parentFacesBySlot=[plan['parents'][0],[]],triangleCountsBySlot=[len(core),0],flowCarrierCells=plan['flowCarrierCells'],
            carrierGuideIds=[0]*len(cp),carrierTerminalDistanceMetres=distances,terminalRegionMetres=.009)
        return native,current,packet,dict(matrix=matrix,vertices=records),surface

    def test_retained_carrier_fit_and_composition_replace_only_owned_faces_with_all_frames(self):
        native,current,packet,binding,surface=self.packet_fixture();saved=copy.deepcopy((native,current,packet,binding))
        materials=[dict(shader='Custom/FakeAnisoHair'),dict(shader='Other')]
        fitted,fit=self.owner.fit_retained_carrier(packet,native,binding,current,materials,.008,surface)
        self.assertTrue(fit['finalGeometryNormalsAndSourceUvTangents']);self.assertGreater(fit['minimumSerializedOrientation'],0.)
        self.assertEqual(fit['storedFits'],3)
        self.assertEqual(fitted['vertices'][:18],native['vertices']);self.assertEqual(fitted['shapes'][0]['frames'][0]['vertices'][:18],native['shapes'][0]['frames'][0]['vertices'])
        self.owner.validate_curved_core(dict(curvedCoreSurface=surface),fitted,binding,native)
        replacement=dict(native=native,surface=surface,materials=materials,maximumSupportOffsetMetres=.008)
        with mock.patch.object(self.owner,'SourceHairDomain',return_value=SimpleNamespace(hair=lambda _:True,surface_hair=lambda _:True)):
            final,attachment,proof=self.owner.compose_finished_source_groom(current,fitted,binding,vertex_capacity=current['vertexCount'],
                fine_material_slots=[dict(sourceSlot=0,targetSlot=2)],carrier_replacement=replacement)
            self.assertLessEqual(proof['targetLogicalBytes'],proof['sourceLogicalBytes'])
            self.assertEqual(proof['replacedSourceCarrierTriangles'],3);self.assertEqual(final['subMeshes'][1],[3,4,5])
            self.assertEqual(len(final['subMeshes'][2]),6);self.assertEqual(final['vertices'][:18],native['vertices'])
            self.assertEqual(len(attachment['vertices']),final['vertexCount'])
            with self.assertRaisesRegex(ValueError,'vertex capacity'):
                self.owner.compose_finished_source_groom(current,fitted,binding,vertex_capacity=6,carrier_replacement=replacement)
            invalid=copy.deepcopy(replacement);invalid['surface']['sourceFacesBySlot'][1]=[0]
            with self.assertRaisesRegex(ValueError,'source domain'):
                self.owner.compose_finished_source_groom(current,fitted,binding,carrier_replacement=invalid)
        self.assertEqual((native,current,packet,binding),saved)

    def test_partial_render_layer_keeps_its_source_coverage_and_all_physical_frames(self):
        native,current,packet,binding,surface=self.packet_fixture();saved=copy.deepcopy((native,current,packet,binding,surface))
        materials=[dict(shader='Custom/FakeAnisoHair'),dict(shader='Other')]
        layer=dict(surface,renderReplacementFacesBySlot=[[],[]])
        replacement=dict(native=native,surface=layer,materials=materials,maximumSupportOffsetMetres=.008)
        with mock.patch.object(self.owner,'SourceHairDomain',return_value=SimpleNamespace(hair=lambda _:True,surface_hair=lambda _:True)):
            final,_,proof=self.owner.compose_finished_source_groom(current,packet,binding,vertex_capacity=current['vertexCount'],
                fine_material_slots=[dict(sourceSlot=0,targetSlot=2)],carrier_replacement=replacement)
        self.assertEqual(proof['replacedSourceCarrierTriangles'],0)
        self.assertTrue(proof['sourceCoreTopologyExact'])
        retained=np.asarray(final['subMeshes'][0]).reshape(-1,3)
        source=np.asarray(current['subMeshes'][0]).reshape(-1,3)
        # The source's centre vertex is retained in addition to its complete
        # native prefix. Added detail must not erase its three covering faces.
        np.testing.assert_array_equal(retained[np.all(retained<7,axis=1)],source[:3])
        for key,width in(('vertices',3),('normals',3),('tangents',4),('weights',4),('boneIndices',4)):
            self.assertEqual(final[key][:7*width],current[key][:7*width])
        for actual,original in zip(final['shapes'],current['shapes']):
            for frame,before in zip(actual['frames'],original['frames']):
                for key in('vertices','normals','tangents'):self.assertEqual(frame[key][:21],before[key][:21])
        self.assertLessEqual(proof['targetLogicalBytes'],proof['sourceLogicalBytes'])
        for invalid in([[0],[0]],[[0,0],[]],[[],[],[]],[[True],[]]):
            with self.subTest(invalid=invalid),self.assertRaisesRegex(ValueError,'render replacement'):
                self.owner.carrier_render_faces(dict(surface,renderReplacementFacesBySlot=invalid))
        self.assertEqual(self.owner.carrier_render_faces(surface),surface['sourceFacesBySlot'])
        self.assertEqual((native,current,packet,binding,surface),saved)

    def test_composed_source_flow_uses_the_final_vertex_remap_and_keeps_its_authority(self):
        native,current,packet,binding,surface=self.packet_fixture()
        surface['sourceFlow']=dict(directions=[[1.,0.,0.]]*surface['vertices'],authority=dict(source='original material Main pin',uvChannel=0,
            scale=[1.,1.],offset=[0.,0.],provenance=dict(sourceFacesBySlot=surface['sourceFacesBySlot'])))
        original=copy.deepcopy((packet,binding,surface));materials=[dict(shader='Custom/FakeAnisoHair'),dict(shader='Other')]
        replacement=dict(native=native,surface=surface,materials=materials,maximumSupportOffsetMetres=.008)
        with mock.patch.object(self.owner,'SourceHairDomain',return_value=SimpleNamespace(hair=lambda _:True,surface_hair=lambda _:True)):
            final,attachment,proof=self.owner.compose_finished_source_groom(current,packet,binding,vertex_capacity=current['vertexCount'],
                fine_material_slots=[dict(sourceSlot=0,targetSlot=2)],carrier_replacement=replacement)
        flow=proof['declaredFlow'];self.assertEqual(flow['vertices'],list(range(final['vertexCount']-surface['vertices'],final['vertexCount'])))
        self.assertEqual(flow['directions'],surface['sourceFlow']['directions']);self.assertEqual(flow['authority'],surface['sourceFlow']['authority'])
        self.assertEqual(len(attachment['vertices']),final['vertexCount']);self.assertEqual((packet,binding,surface),original)

    def test_standalone_attachment_replays_the_final_carrier_domain_and_compiles_native_anchors(self):
        native,current,packet,binding,surface=self.packet_fixture()
        support=dict(materials=[dict(shader='Custom/FakeAnisoHair'),dict(shader='Other')],maximumSupportOffsetMetres=.008,carrierSurface=surface)
        saved=copy.deepcopy((current,packet,binding,support))
        result,compiled,facts=self.owner.standalone_cloth_attachment(packet,native,binding,current,retained_support=support)
        self.assertEqual(facts['compiledCarrierSurface']['firstVertex']+surface['vertices'],result['vertexCount'])
        self.assertEqual(facts['compiledCarrierSurface']['sourceFacesBySlot'],[[0],[]])
        self.assertTrue(all('surface'not in row and 'geometrySurface'not in row for row in compiled['vertices']))
        self.assertTrue(all(0<=row['anchor']<native['vertexCount']for row in compiled['vertices']))
        self.assertEqual((current,packet,binding,support),saved)

    def test_curved_entry_uses_validated_explicit_provenance_for_both_source_binding_formats(self):
        native,_,packet,binding,_=self.packet_fixture();owner=self.owner
        entry=dict(category='Hair',generatedPacket='authored',generatedSha256='hash',nativePacketPath='native',nativePacketSha256='hash',
            bindingPath='binding',bindingSha256='hash',authoredFingerprint=owner.fingerprint(packet)[0],rendererToPrefab=np.eye(4).ravel().tolist())
        for row in binding['vertices']:
            if row['native']:row.update(surface=None,geometrySurface=None)
        binding.update(sourceFingerprint=native['sourceFingerprint'],sourceVertexCount=native['vertexCount'])
        with mock.patch.object(owner,'sha',return_value='hash'),mock.patch.object(owner,'read',side_effect=lambda path:copy.deepcopy(native if str(path)=='native'else packet)),\
                mock.patch.object(owner,'decode_binding',side_effect=lambda _:copy.deepcopy(binding)),\
                mock.patch.object(owner,'native_frames',side_effect=ValueError('accepted explicit source')):
            for version in(3,4):
                binding['schemaVersion']=version
                with self.subTest(version=version),self.assertRaisesRegex(ValueError,'accepted explicit source'):
                    owner.curved_core_row(entry,Path('unused'))
            binding['schemaVersion']=1;binding.pop('sourceVertexCount')
            for row in binding['vertices']:row.pop('surface',None);row.pop('geometrySurface',None)
            with self.assertRaisesRegex(AssertionError,'Explicit existing source-triangle'):
                owner.curved_core_row(entry,Path('unused'))
    def fixture(self):
        p=np.array([[-.005,0.,0.],[.005,0.,0.],[-.005,.04,0.],[.005,0.,0.],[.005,.04,0.],[-.005,.04,0.]])
        triangles=[np.array([[0,1,2],[3,4,5]])];physical=np.array([0,1,2,1,3,2])
        poses=np.stack((p,p*[.8,1.1,1.],p+[0.,0.,.01]))
        n=np.broadcast_to([0.,0.,1.],poses.shape).copy();t=np.broadcast_to([1.,0.,0.],poses.shape).copy();b=np.cross(n,t)
        settings=dict(heightFraction=.1,terminalRegionMetres=.009,tipWidthFraction=.04)
        terminal=dict(terminalDistance=np.array([.04,.04,0.,.04,0.,0.]))
        guides=[]
        for identifier,x in enumerate((-.002,0.,.002)):
            f=(x+.005)/.01;cross_v=1-f
            guides.append(dict(slot=0,guide=identifier,lengthMetres=.04,samples=[
                dict(triangle=[0,1,2],barycentric=[1-f,f,0.],v=0.),
                dict(triangle=[0,1,2],barycentric=[0.,f,1-f],v=cross_v),
                dict(triangle=[3,4,5],barycentric=[f,0.,1-f],v=cross_v),
                dict(triangle=[3,4,5],barycentric=[0.,f,1-f],v=1.)]))
        return p,triangles,physical,poses,n,t,b,settings,terminal,guides

    def plan(self,budget=200):
        p,triangles,physical,poses,n,t,b,settings,terminal,guides=self.fixture()
        fields,facts=self.owner.source_carrier_fields(guides,poses,triangles,terminal,.004,settings,.00024)
        plan=self.owner.plan_curved_core(p[:,:2],triangles,[np.ones(2,bool)],.004,budget,range(len(p)),positions=p,
            physical_groups=physical,flow_fields=fields,protected_vertices=[0,1,3])
        return plan,fields,facts

    def test_complete_physical_guide_keeps_one_coordinate_across_texture_aliases(self):
        plan,fields,facts=self.plan();self.assertEqual(facts['mappedNativeGuideSpans'],6)
        self.assertEqual(facts['unresolvedGuideSpans'],[])
        for identifier in range(3):
            a=next(field for field in fields[0,0]if field['guide']==identifier)
            b=next(field for field in fields[0,1]if field['guide']==identifier)
            np.testing.assert_allclose(np.asarray(a['uMetres'])[[1,2]],np.asarray(b['uMetres'])[[0,2]],rtol=0,atol=1e-12)
            np.testing.assert_allclose(np.asarray(a['v'])[[1,2]],np.asarray(b['v'])[[0,2]],rtol=0,atol=1e-12)
        self.assertLess(plan['maximumSourceAreaError'],1e-12)
        self.assertAlmostEqual(plan['flowCarrierCells']['physicalGuideAreaSquareMetres'],.00032,places=12)
        self.assertAlmostEqual(plan['flowCarrierCells']['unmodulatedAreaSquareMetres'],.00008,places=12)

    def test_chart_endpoint_restart_and_missing_physical_crossing_are_rejected(self):
        _,tri,_,poses,_,_,_,settings,terminal,guides=self.fixture()
        broken=copy.deepcopy(guides);broken[0]['samples'][2]['v']=0.
        with self.assertRaisesRegex(ValueError,'restarted'):
            self.owner.source_carrier_fields(broken,poses,tri,terminal,.004,settings,.00024)
        broken=copy.deepcopy(guides);broken[0]['samples'].pop(1)
        with self.assertRaisesRegex(ValueError,'physical edge crossing'):
            self.owner.source_carrier_fields(broken,poses,tri,terminal,.004,settings,.00024)

    def test_source_coverage_and_all_stored_fit_winding_use_the_existing_bound(self):
        plan,_,_=self.plan();p,tri,_,poses,n,t,b,_,_,_=self.fixture()
        before=copy.deepcopy(plan);self.owner.prepare_geometry_frames(plan,poses)
        allocated=[row for row in plan['vertices']if 'nativeVertex'not in row]
        evaluation=dict(plan,vertices=allocated)
        pp,nn,tt=self.owner.evaluate_curved_core(evaluation,p,n[0],t[0],p[:,:2])
        self.assertGreater(float(np.max(pp[:,2])),0.)
        world_p,world_n,world_t,facts=self.owner.bound_curved_core(plan,pp,nn,tt,poses,n,t,b,np.eye(3))
        self.assertEqual(facts['finalFailedSamples'],0)
        self.assertTrue(facts['allStoredPosesAndBinary32ChannelsChecked'])
        self.assertEqual(world_p.shape,(3,len(allocated),3))
        self.assertTrue(np.isfinite(world_n).all()and np.isfinite(world_t).all())
        self.assertEqual(before['triangles'],plan['triangles'])

    def test_three_protected_corners_do_not_freeze_their_entire_parent(self):
        p=np.array([[-.01,0.,0.],[.01,0.,0.],[0.,.04,0.]])
        field=dict(guide=0,uMetres=[-.01,.01,0.],v=[0.,0.,1.],halfWidthMetres=.02,
            heightMetres=.0003,lengthMetres=.04,terminalDistanceMetres=[.04,.04,0.],terminalRegionMetres=.009,tipWidthFraction=.04)
        plan=self.owner.plan_curved_core(p[:,:2],[np.array([[0,1,2]])],[np.ones(1,bool)],.004,100,range(3),positions=p,
            physical_groups=np.arange(3),flow_fields={(0,0):[field]},protected_vertices=[0,1,2])
        # Include an owned interior sample on the same guide; it is not protected
        # merely because all three barycentric source corners are contacts.
        row=copy.deepcopy(plan['vertices'][0]);row.pop('nativeVertex',None);row['barycentric']=[1/3]*3
        points,_,_=self.owner.evaluate_curved_core(dict(plan,vertices=[row]),p,np.tile([0.,0.,1.],(3,1)),np.tile([1.,0.,0.],(3,1)),p[:,:2])
        self.assertGreater(points[0,2],0.)
        all_points,_,_=self.owner.evaluate_curved_core(plan,p,np.tile([0.,0.,1.],(3,1)),np.tile([1.,0.,0.],(3,1)),p[:,:2])
        for index,record in enumerate(plan['vertices']):
            if 'nativeVertex'in record:np.testing.assert_array_equal(all_points[index],p[record['nativeVertex']])

    def test_actual_allocation_is_a_hard_boundary(self):
        with self.assertRaises(OverflowError):self.plan(budget=1)

    def test_lock_width_crosses_a_physical_edge_without_a_centerline_crossing(self):
        p,triangles,physical,poses,_,_,_,settings,terminal,_=self.fixture()
        first=np.array([-.004,.01,0.]);last=np.array([-.004,.03,0.]);tri=p[triangles[0][0]]
        def bary(point):
            e=np.stack((tri[1]-tri[0],tri[2]-tri[0]),axis=1);v=np.linalg.solve(e.T@e,e.T@(point-tri[0]));return np.r_[1-v.sum(),v].tolist()
        guides=[dict(slot=0,guide=0,lengthMetres=.02,samples=[
            dict(triangle=[0,1,2],barycentric=bary(first),v=0.),dict(triangle=[0,1,2],barycentric=bary(last),v=1.)])]
        fields,facts=self.owner.source_carrier_fields(guides,poses,triangles,terminal,.006,settings,.00024,
            physical_groups=physical,eligible_faces=[np.ones(2,bool)])
        self.assertIn((0,1),fields);self.assertGreater(facts['propagatedNativeGuideSpans'],0)
        self.assertEqual(facts['rejectedCompleteGuides'],0)
        a=fields[0,0][0];b=fields[0,1][0]
        np.testing.assert_array_equal(np.asarray(a['uMetres'])[[1,2]],np.asarray(b['uMetres'])[[0,2]])
        np.testing.assert_array_equal(np.asarray(a['v'])[[1,2]],np.asarray(b['v'])[[0,2]])

    def test_alias_edge_partition_is_conforming_and_keeps_one_physical_position(self):
        p,triangles,physical,poses,n,t,_,settings,terminal,guides=self.fixture()
        # Only one complete guide enters each chart; its width and the next
        # guide's overlap must produce identical edge cuts on both charts.
        fields,_=self.owner.source_carrier_fields(guides,poses,triangles,terminal,.004,settings,.00024,
            physical_groups=physical,eligible_faces=[np.ones(2,bool)])
        plan=self.owner.plan_curved_core(p[:,:2],triangles,[np.ones(2,bool)],.004,200,range(len(p)),positions=p,
            physical_groups=physical,flow_fields=fields,protected_vertices=[0,1,3])
        self.owner.prepare_geometry_frames(plan,poses)
        points,_,_=self.owner.evaluate_curved_core(plan,p,n[0],t[0],p[:,:2]);cuts={0:set(),1:set()};aliases={};branch_points={}
        for index,row in enumerate(plan['vertices']):
            groups=physical[np.asarray(row['triangle'])];bary=np.asarray(row['barycentric'])
            if any(weight>1e-10 and group not in(1,2)for group,weight in zip(groups,bary)):continue
            fraction=float(sum(weight for group,weight in zip(groups,bary)if group==2))
            cuts[row['face']].add(round(fraction,9))
            driver=row['geometrySurface'];key=tuple(driver['triangle'])+tuple(np.asarray(driver['barycentric'],dtype='<f4'))
            distance=float(bary@terminal['terminalDistance'][row['triangle']])
            if distance<settings['terminalRegionMetres']:
                field=row['flowCarrier'];owner=field['guide']if field else-1
                branch_points.setdefault(key,[]).append(points[index]);key=(*key,owner)
            aliases.setdefault(key,[]).append(points[index])
        self.assertEqual(cuts[0],cuts[1])
        for values in aliases.values():np.testing.assert_allclose(values,np.repeat([values[0]],len(values),axis=0),rtol=0,atol=1e-12)
        self.assertTrue(any(np.max(np.linalg.norm(np.asarray(values)-values[0],axis=1))>.0001 for values in branch_points.values()))

    def test_a_physical_tip_is_independent_of_a_random_guide_endpoint(self):
        p=np.array([[-.01,0.,0.],[.01,0.,0.],[0.,.04,0.]])
        base=dict(guide=0,uMetres=[-.01,.01,0.],v=[.25,.25,.75],halfWidthMetres=.02,
            heightMetres=0.,lengthMetres=.04,terminalDistanceMetres=[.03,.03,.03],terminalRegionMetres=.009,tipWidthFraction=.04)
        plan=self.owner.plan_curved_core(p[:,:2],[np.array([[0,1,2]])],[np.ones(1,bool)],.004,100,positions=p,
            physical_groups=np.arange(3),flow_fields={(0,0):[base]})
        points,_,_=self.owner.evaluate_curved_core(plan,p,np.tile([0.,0.,1.],(3,1)),np.tile([1.,0.,0.],(3,1)),p[:,:2])
        expected=np.asarray([np.asarray(row['barycentric'])@p for row in plan['vertices']])
        np.testing.assert_allclose(points,expected,rtol=0,atol=1e-12)
        tip=copy.deepcopy(base);tip['terminalDistanceMetres']=[.003,.003,.003]
        tapered=self.owner.plan_curved_core(p[:,:2],[np.array([[0,1,2]])],[np.ones(1,bool)],.004,100,positions=p,
            physical_groups=np.arange(3),flow_fields={(0,0):[tip]})
        narrow,_,_=self.owner.evaluate_curved_core(tapered,p,np.tile([0.,0.,1.],(3,1)),np.tile([1.,0.,0.],(3,1)),p[:,:2])
        self.assertLess(float(np.ptp(narrow[:,0])),float(np.ptp(expected[:,0])))


class SourceSurfaceSkinTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        root=next(path for path in Path(__file__).resolve().parents if(path/'SolastaUnfinishedBusiness.sln').is_file())
        path=root/'tools/blender-models/CharacterHair/GenerateFlowCoverageSweptProfile.py'
        spec=importlib.util.spec_from_file_location('center_skin_generator_contract',path)
        cls.groom=importlib.util.module_from_spec(spec);spec.loader.exec_module(cls.groom)
        import character_resources
        import mesh_recipe_codec
        cls.resources=character_resources;cls.codec=mesh_recipe_codec

    def native_cloth_source_fixture(self,root):
        import hashlib
        pointer=lambda key:dict(m_FileID=0,m_PathID=key)
        weight=lambda parent,value:dict(parentIndex=parent,weight=value,localPos=dict(x=0.,y=0.,z=0.),localNor=dict(x=0.,y=0.,z=1.),localTan=dict(x=1.,y=0.,z=0.))
        trees={10:dict(deformer=dict(sharedMesh=pointer(1),meshData=pointer(20),targetObject=pointer(80),
                      boneList=[pointer(90)],dataHash=7),dataHash=7,deformerHash=7),
            20:dict(vertexCount=3,triangleCount=1,vertexHashList=[10,20,30],dataHash=7),
            30:dict(deformer=dict(meshData=pointer(40),renderDeformerList=[pointer(10)],renderDeformerHashList=[7],
                                 boneList=[pointer(90)],dataHash=9),dataHash=9,deformerHash=9),
            40:dict(vertexCount=2,dataHash=9,childDataList=[dict(vertexCount=3,childDataHash=7,vertexInfoList=[2<<28,(2<<28)|2,(2<<28)|4],
                vertexWeightList=[weight(parent,value)for _ in range(3)for parent,value in((0,.25),(1,.75))],parentIndexList=[0,0,1])]),
            50:dict(virtualDeformer=pointer(30),clothData=pointer(60),dataHash=11,virtualDeformerHash=9,m_Enabled=1),
            60:dict(dataHash=11)}
        packets={1:dict(vertexCount=3,subMeshes=[[0,1,2]],sourceFingerprint='a'*64)}
        def obj(kind,tree=None,packet=None):return SimpleNamespace(type=SimpleNamespace(name=kind),read_typetree=lambda:copy.deepcopy(tree),packet=packet)
        def make(values):
            objects={key:obj('MonoBehaviour',tree)for key,tree in values.items()}
            objects.update({1:obj('Mesh',packet=packets[1]),70:obj('SkinnedMeshRenderer',dict(m_GameObject=pointer(80),m_Mesh=pointer(1),m_Bones=[pointer(90)],m_RootBone=pointer(90))),
                80:obj('GameObject',dict(m_Name='Owned renderer')),
                90:obj('Transform',dict(m_LocalPosition=dict(x=0,y=0,z=0),m_Father=pointer(0)))})
            return SimpleNamespace(name='CAB-authority',externals=[],objects=objects)
        native=make(trees);current_trees=copy.deepcopy(trees);current_trees[20]['vertexHashList']=[1,2,3]
        current_trees[40]['childDataList'][0]['vertexWeightList'][0]['weight']=.5
        current=make(current_trees);path=root/'native.bundle';path.write_bytes(b'Pinned native fixture')
        reference=dict(path=str(path),sha256=hashlib.sha256(path.read_bytes()).hexdigest(),pathId=1)
        return current,current_trees,native,trees,reference

    def restore_native_cloth_source_fixture(self,current,trees,native,reference):
        with mock.patch.object(self.resources,'load_serialized_bundle',return_value=(object(),native)):
            return self.resources.restore_native_cloth_source(current,trees,1,reference,export_mesh=lambda obj:copy.deepcopy(obj.packet))

    def test_native_anatomy_restore_keeps_all_parent_weights_and_only_two_owned_fields(self):
        with tempfile.TemporaryDirectory()as directory:
            current,trees,native,original,reference=self.native_cloth_source_fixture(Path(directory));before=copy.deepcopy(trees)
            packet,proof=self.restore_native_cloth_source_fixture(current,trees,native,reference)
            self.assertEqual(packet['sourceFingerprint'],'a'*64)
            self.assertEqual(trees,original)
            self.assertEqual([(row['object'],row['field'])for row in proof['data']],[(20,'vertexHashList'),(40,'childDataList')])
            self.assertEqual(Path(proof['source']['path']),Path(reference['path']))
            self.assertEqual(proof['source']['sha256'],reference['sha256'])
            self.assertEqual(proof['source']['pathId'],reference['pathId'])
            self.assertEqual(len(trees[40]['childDataList'][0]['vertexWeightList']),6)
            self.assertEqual([row['weight']for row in trees[40]['childDataList'][0]['vertexWeightList'][:2]],[.25,.75])
            self.assertNotEqual(before,trees)
            self.assertEqual(native.objects[40].read_typetree(),original[40])

    def test_native_groom_restore_preserves_an_armor_child_and_excludes_other_prefabs(self):
        with tempfile.TemporaryDirectory() as directory:
            current, trees, native, original, reference = self.native_cloth_source_fixture(Path(directory))

            def pointer(key):
                return dict(m_FileID=0, m_PathID=key)

            def add_object(asset, key, kind, tree):
                asset.objects[key] = SimpleNamespace(type=SimpleNamespace(name=kind),
                    read_typetree=lambda: copy.deepcopy(tree))

            for asset, values in ((current, trees), (native, original)):
                values[11] = dict(deformer=dict(sharedMesh=pointer(2), meshData=pointer(21), dataHash=16),
                                  dataHash=16, deformerHash=16)
                values[21] = dict(vertexCount=5, triangleCount=1, vertexHashList=list(range(5)), dataHash=16)
                values[30]['deformer']['renderDeformerList'].insert(0, pointer(11))
                values[30]['deformer']['renderDeformerHashList'].insert(0, 16)
                values[40]['childDataList'].insert(0, dict(vertexCount=5, childDataHash=18))
                add_object(asset, 2, 'Mesh', dict(m_Name='Armor'))
                add_object(asset, 11, 'MonoBehaviour', values[11])
                add_object(asset, 21, 'MonoBehaviour', values[21])
            add_object(native, 12, 'MonoBehaviour', dict(deformer=dict(sharedMesh=pointer(1))))

            self.restore_native_cloth_source_fixture(current, trees, native, reference)
            self.assertEqual(trees, original)

            trees[40]['childDataList'][0]['childDataHash'] += 1
            before = copy.deepcopy(trees)
            with self.assertRaisesRegex(ValueError, 'attached sibling surface'):
                self.restore_native_cloth_source_fixture(current, trees, native, reference)
            self.assertEqual(trees, before)

    def test_native_cloth_rejects_reference_owner_palette_count_and_unknown_field_drift_atomically(self):
        for defect in('digest','object','external-reference','missing-reference','missing-render','extra-virtual','mixed-render','component','palette','bone','count','field','additional-data-consumer','cloth'):
            with self.subTest(defect=defect),tempfile.TemporaryDirectory()as directory:
                current,trees,native,_,reference=self.native_cloth_source_fixture(Path(directory))
                if defect=='digest':reference['sha256']='0'*64
                elif defect=='object':reference['pathId']=99
                elif defect=='external-reference':
                    trees[10]['m_Script']=dict(m_FileID=1,m_PathID=99)
                    native.objects[10].read_typetree=lambda:copy.deepcopy(trees[10])
                elif defect=='missing-reference':
                    trees[10]['m_Script']=dict(m_FileID=0,m_PathID=99)
                    native.objects[10].read_typetree=lambda:copy.deepcopy(trees[10])
                elif defect=='missing-render':del trees[10]
                elif defect=='extra-virtual':trees[31]=copy.deepcopy(trees[30])
                elif defect=='mixed-render':trees[30]['deformer']['renderDeformerList'].append(dict(m_FileID=0,m_PathID=11))
                elif defect=='component':trees[10]['dataHash']=8
                elif defect=='palette':current.objects[70].read_typetree=lambda:dict(m_GameObject=dict(m_FileID=0,m_PathID=80),m_Mesh=dict(m_FileID=0,m_PathID=1),m_Bones=[],m_RootBone=dict(m_FileID=0,m_PathID=90))
                elif defect=='bone':current.objects[90].read_typetree=lambda:dict(m_LocalPosition=dict(x=1,y=0,z=0),m_Father=dict(m_FileID=0,m_PathID=0))
                elif defect=='count':trees[40]['childDataList'][0]['vertexCount']=4
                elif defect=='field':trees[40]['vertexCount']=12
                elif defect=='additional-data-consumer':trees[32]=dict(deformer=dict(meshData=dict(m_FileID=0,m_PathID=40)))
                elif defect=='cloth':trees[50]['dataHash']=99
                before=copy.deepcopy(trees)
                with self.assertRaises(ValueError):self.restore_native_cloth_source_fixture(current,trees,native,reference)
                self.assertEqual(trees,before)

    def test_native_cloth_noop_still_proves_source_and_null_native_root_is_retained(self):
        with tempfile.TemporaryDirectory()as directory:
            current,trees,native,original,reference=self.native_cloth_source_fixture(Path(directory));trees.clear();trees.update(copy.deepcopy(original))
            for asset in(current,native):asset.objects[70].read_typetree=lambda:dict(m_GameObject=dict(m_FileID=0,m_PathID=80),m_Mesh=dict(m_FileID=0,m_PathID=1),m_Bones=[dict(m_FileID=0,m_PathID=90)],m_RootBone=dict(m_FileID=0,m_PathID=0))
            packet,proof=self.restore_native_cloth_source_fixture(current,trees,native,reference)
            self.assertTrue(all(not row['changed']for row in proof['data']))
            self.assertEqual(trees,original)

    def test_repacked_cab_and_unconsumed_external_table_do_not_change_local_authority(self):
        with tempfile.TemporaryDirectory()as directory:
            current,trees,native,original,reference=self.native_cloth_source_fixture(Path(directory))
            native.name='CAB-true-native';current.name='CAB-complete-part'
            native.externals=[SimpleNamespace(path='archive:/native-unconsumed')]
            current.externals=[SimpleNamespace(path='archive:/current-unconsumed')]
            _,proof=self.restore_native_cloth_source_fixture(current,trees,native,reference)
            self.assertEqual(trees,original)
            self.assertEqual(proof['nativeCab'],'CAB-true-native')
            self.assertEqual(proof['currentCab'],'CAB-complete-part')

    def test_native_source_mutation_before_commit_keeps_staged_owners_unchanged(self):
        with tempfile.TemporaryDirectory()as directory:
            current,trees,native,_,reference=self.native_cloth_source_fixture(Path(directory));before=copy.deepcopy(trees)
            with mock.patch.object(self.resources,'digest',side_effect=[reference['sha256'],'0'*64]):
                with self.assertRaisesRegex(ValueError,'during restoration'):self.restore_native_cloth_source_fixture(current,trees,native,reference)
            self.assertEqual(trees,before)



    @classmethod
    def fixture(cls):
        vector=lambda value:dict(zip(('x','y','z'),map(float,value)))
        pointer=lambda value:dict(m_FileID=0,m_PathID=value)
        points=np.array([[0.,0.,0.],[.02,0.,0.],[0.,.04,0.]])
        identity=np.eye(4).ravel().tolist()
        native=dict(vertexCount=3,vertices=points.ravel().tolist(),normals=[0.,0.,1.]*3,tangents=[1.,0.,0.,1.]*3,
            weights=[1.,0.,0.,0.]*3,boneIndices=[0,0,0,0,1,0,0,0,2,0,0,0],bindPoses=identity*3,
            subMeshes=[[0,1,2]],sourceFingerprint='a'*64,nativeReadable=True,
            shapes=[dict(name='Fit',index=0,frames=[dict(weight=100.,vertices=[0.,0.,.0001]*3,normals=[0.]*9,tangents=[0.]*9)])])
        packet=copy.deepcopy(native);fine=np.array([[.0094,0.,.001],[.0106,0.,.001],[.0044,.02,.001],[.0056,.02,.001]])
        packet.update(vertexCount=7,vertices=np.concatenate((points,fine)).ravel().tolist(),normals=[0.,0.,1.]*7,
            tangents=[1.,0.,0.,1.]*7,subMeshes=[[0,1,2,3,4,5,4,6,5]],sourceFingerprint='b'*64)
        packet['uv']=[dict(channel=2,dimension=4,values=[0.,0.,0.,1.]*3+[v for y in(0.,1.)for x in(0.,1.)for v in(x,y,49.,1.2)])]
        frame=packet['shapes'][0]['frames'][0];frame.update(vertices=[0.,0.,.0001]*7,normals=[0.]*21,tangents=[0.]*21)
        records=[dict(anchor=i,native=True,role='native',slot=0,uv0=[0.,0.],offset=[0.,0.,0.],normal=[0.,0.,1.],tangent=[1.,0.,0.],tangentW=1.)for i in range(3)]
        samples=[]
        for offset,point in enumerate(fine):
            sample=dict(triangle=[0,1,2],barycentric=[.5,.5,0.]if offset<2 else[.25,.25,.5])
            anchor=0 if offset<2 else 2
            records.append(dict(anchor=anchor,native=False,role='source-continuous-flow-ribbon',slot=0,uv0=[0.,0.],
                offset=(point-points[anchor]).tolist(),normal=[0.,0.,1.],tangent=[1.,0.,0.],tangentW=1.))
            samples.append(dict(vertex=offset+3,surface=sample))
            weights,indices=cls.codec.source_surface_skin(native,sample)
            packet['weights']+=weights;packet['boneIndices']+=indices
        binding=dict(sourceFingerprint=native['sourceFingerprint'],cloth=True,matrix=identity,vertices=records,
            surfaceSkin=dict(sourceVertexCount=3,sourceFingerprint=native['sourceFingerprint'],vertices=samples))
        source_weights=[dict(weight=1.,parentIndex=i,localPos=vector([0.,0.,0.]),localNor=vector([0.,0.,1.]),
            localTan=vector([0.,-1.,0.]if i==1 else[1.,0.,0.]))for i in range(3)]
        child=dict(vertexCount=3,vertexInfoList=[(1<<28)|i for i in range(3)],vertexWeightList=source_weights,
            parentIndexList=[0,1,2],childDataHash=201)
        render_data=dict(vertexCount=3,triangleCount=1,vertexHashList=[1,2,3],baseScale=vector([1.,1.,1.]),dataHash=101,
            **{key:[]for key in('vertexInfoList','vertexWeightList','uvList','lineList','triangleList','vertexToTriangleInfoList','vertexToTriangleIndexList','childDataList')})
        trees={10:dict(deformer=dict(sharedMesh=pointer(1),meshData=pointer(20),dataHash=201),deformerHash=201,dataHash=211),
            20:render_data,30:dict(deformer=dict(renderDeformerList=[pointer(10)],meshData=pointer(40),
                renderDeformerHashList=[211],dataHash=301),deformerHash=301,dataHash=311),
            40:dict(vertexCount=3,dataHash=401,childDataList=[child]),
            50:dict(virtualDeformer=pointer(30),virtualDeformerHash=311,dataHash=501)}
        replacements={1:dict(native=copy.deepcopy(native),raw=copy.deepcopy(native),packet=packet,binding=binding)}
        rotation=np.array([[0.,-1.,0.],[1.,0.,0.],[0.,0.,1.]])
        parents=[(np.eye(3),points[0]),(rotation,points[1]),(np.eye(3),points[2])]
        return trees,replacements,parents

    @staticmethod
    def weights(child,index):
        packed=child['vertexInfoList'][index];count,start=packed>>28,packed&0x0fffffff
        return child['vertexWeightList'][start:start+count]

    @classmethod
    def evaluate(cls,child,index,parents):
        return sum(weight['weight']*(parents[weight['parentIndex']][0]@np.array([weight['localPos'][axis]for axis in('x','y','z')])+parents[weight['parentIndex']][1])for weight in cls.weights(child,index))

    def test_center_surface_skin_and_virtual_children_share_a_continuous_cross_section(self):
        trees,replacements,parents=self.fixture();original=copy.deepcopy(replacements);source_child=copy.deepcopy(trees[40]['childDataList'][0])
        proof=self.resources.prepare_native_cloth(trees,replacements);child=trees[40]['childDataList'][0]
        target=np.asarray(replacements[1]['packet']['vertices']).reshape(-1,3)
        for index in range(7):np.testing.assert_allclose(self.evaluate(child,index,parents),target[index],atol=2e-9)
        for a,b in((3,4),(5,6)):
            self.assertEqual([(w['parentIndex'],w['weight'])for w in self.weights(child,a)],[(w['parentIndex'],w['weight'])for w in self.weights(child,b)])
            self.assertAlmostEqual(sum(w['weight']for w in self.weights(child,a)),1.,places=6)
        for index in range(3):self.assertEqual(self.weights(child,index),self.weights(source_child,index))
        for field in('native','raw','packet','binding'):self.assertEqual(replacements[1][field],original[1][field])
        moved=[(np.eye(3),np.array([-.02,0.,0.])),(np.eye(3),np.array([.06,.01,0.])),parents[2]]
        for a,b in((3,4),(5,6)):
            width=np.linalg.norm(self.evaluate(child,a,moved)-self.evaluate(child,b,moved))
            self.assertGreater(width,.0008);self.assertLessEqual(width,.00120001)
        self.assertEqual(proof['changes'][1]['kind'],'surface-skin')
        self.assertEqual(proof['changes'][1]['positiveInfluencesDiscarded'],0)
        self.assertEqual(json.loads(json.dumps(child)),child)

    def test_collar_keeps_body_skin_and_rebuilds_the_head_simulation_for_every_render_vertex(self):
        trees, replacements, parents = self.fixture()
        row = replacements[1]
        source, target = row['native'], row['packet']
        samples = row['binding']['surfaceSkin']['vertices']
        target['bindPoses'] += np.eye(4).ravel().tolist()
        target['boneIndices'] = [3, 0, 0, 0] * target['vertexCount']
        target['weights'] = [1., 0., 0., 0.] * target['vertexCount']
        target['uv'][0]['channel'] = 0
        skin = copy.deepcopy((target['bindPoses'], target['boneIndices'], target['weights']))
        row['binding'] = self.codec.source_cloth_binding(source, target, np.eye(4).ravel().tolist(), samples)
        self.resources.prepare_native_cloth(trees, replacements)
        child = trees[40]['childDataList'][0]
        self.assertEqual(trees[20]['vertexCount'], target['vertexCount'])
        self.assertEqual(child['vertexCount'], target['vertexCount'])
        solved = [self.evaluate(child, i, parents) for i in range(target['vertexCount'])]
        np.testing.assert_allclose(solved, np.asarray(target['vertices']).reshape(-1, 3), atol=2e-9)
        self.assertEqual((target['bindPoses'], target['boneIndices'], target['weights']), skin)

    def test_center_stencil_reuses_shared_virtual_parents_without_duplicate_support(self):
        trees,replacements,_=self.fixture();child=trees[40]['childDataList'][0]
        child['vertexWeightList'][1].update(parentIndex=0,localPos=dict(x=.02,y=0.,z=0.),localTan=dict(x=1.,y=0.,z=0.));child['parentIndexList'][1]=0
        self.resources.prepare_native_cloth(trees,replacements);child=trees[40]['childDataList'][0]
        self.assertEqual(len(self.weights(child,3)),1)
        self.assertEqual(self.weights(child,3)[0]['weight'],1.)
        self.assertEqual(len({w['parentIndex']for w in self.weights(child,5)}),2)

    def test_tiny_positive_virtual_support_keeps_its_unit_frame_and_float32_weight(self):
        trees,replacements,parents=self.fixture();row=replacements[1];original=copy.deepcopy(trees[40]['childDataList'][0])
        for declaration in row['binding']['surfaceSkin']['vertices']:
            index=declaration['vertex'];declaration['surface']['barycentric']=[1e-18,.5,.5]
            weights,bones=self.codec.source_surface_skin(row['native'],declaration['surface'])
            row['packet']['weights'][index*4:index*4+4]=weights;row['packet']['boneIndices'][index*4:index*4+4]=bones
        self.resources.prepare_native_cloth(trees,replacements);child=trees[40]['childDataList'][0]
        target=np.asarray(row['packet']['vertices']).reshape(-1,3)
        for index in range(3,7):
            weights=self.weights(child,index);self.assertEqual(len(weights),3)
            tiny=next(weight for weight in weights if weight['parentIndex']==0)
            self.assertGreater(tiny['weight'],0.);self.assertAlmostEqual(tiny['weight']/1e-18,1.,places=6)
            np.testing.assert_allclose(self.evaluate(child,index,parents),target[index],rtol=0,atol=2e-9)
            for weight in weights:
                normal=np.asarray([weight['localNor'][key]for key in('x','y','z')]);tangent=np.asarray([weight['localTan'][key]for key in('x','y','z')])
                self.assertAlmostEqual(np.linalg.norm(normal),1.,places=6);self.assertAlmostEqual(np.linalg.norm(tangent),1.,places=6)
                self.assertAlmostEqual(float(normal@tangent),0.,places=6)
        for index in range(3):self.assertEqual(self.weights(child,index),self.weights(original,index))
        self.assertEqual(json.loads(json.dumps(child)),child)

    def test_true_virtual_normal_or_tangent_cancellation_still_rejects_the_mean_frame(self):
        for component in('normal','tangent'):
            with self.subTest(component=component):
                trees,replacements,_=self.fixture();child=trees[40]['childDataList'][0]
                weight=child['vertexWeightList'][1];weight['parentIndex']=0;child['parentIndexList'][1]=0
                weight['localNor']=dict(x=0.,y=0.,z=-1. if component=='normal'else 1.)
                weight['localTan']=dict(x=1. if component=='normal'else-1.,y=0.,z=0.)
                with self.assertRaisesRegex(ValueError,'singular or nonfinite frame'):
                    self.resources.prepare_native_cloth(trees,replacements)

    def test_merged_virtual_parent_preserves_the_unmerged_linear_pnt_stencil(self):
        trees,replacements,_=self.fixture();row=replacements[1];child=trees[40]['childDataList'][0]
        axis=np.ones(3)/np.sqrt(3);angle=np.pi/3;cross=np.array([[0.,-axis[2],axis[1]],[axis[2],0.,-axis[0]],[-axis[1],axis[0],0.]])
        rotation=np.eye(3)*np.cos(angle)+(1-np.cos(angle))*np.outer(axis,axis)+np.sin(angle)*cross
        child['vertexWeightList'][1].update(parentIndex=0,localPos=dict(x=.02,y=0.,z=0.),
            localNor=dict(zip(('x','y','z'),rotation[:,2])),localTan=dict(zip(('x','y','z'),rotation[:,0])))
        child['parentIndexList'][1]=0;original=copy.deepcopy(child)
        self.resources.prepare_native_cloth(trees,replacements);child=trees[40]['childDataList'][0]
        points=np.asarray(row['native']['vertices']).reshape(-1,3);target=np.asarray(row['packet']['vertices']).reshape(-1,3)
        for declaration in row['binding']['surfaceSkin']['vertices']:
            index=declaration['vertex'];bary=np.asarray(declaration['surface']['barycentric']);residual=target[index]-bary@points
            expected={};source_rotations=(np.eye(3),rotation,np.eye(3))
            for anchor,factor in enumerate(bary):
                if factor==0:continue
                source=original['vertexWeightList'][anchor];parent=source['parentIndex'];basis=source_rotations[anchor]
                position=np.asarray([source['localPos'][key]for key in('x','y','z')])+basis@residual
                value=expected.setdefault(parent,[0.,np.zeros(3),np.zeros(3),np.zeros(3)])
                value[0]+=factor;value[1]+=factor*position;value[2]+=factor*basis[:,2];value[3]+=factor*basis[:,0]
            for weight in self.weights(child,index):
                value=expected[weight['parentIndex']];self.assertAlmostEqual(weight['weight'],value[0],places=7)
                for key,part in(('localPos',1),('localNor',2),('localTan',3)):
                    actual=weight['weight']*np.asarray([weight[key][axis]for axis in('x','y','z')])
                    np.testing.assert_allclose(actual,value[part],rtol=0,atol=3e-8)
        weight=self.weights(child,3)[0];normal=np.asarray(list(weight['localNor'].values()));tangent=np.asarray(list(weight['localTan'].values()))
        self.assertLess(np.linalg.norm(normal),.99);self.assertLess(np.linalg.norm(tangent),.99)
        self.assertGreater(abs(float(normal@tangent)),.05)

    def test_center_skin_does_not_infer_native_fingerprint_from_equal_vertex_counts(self):
        trees,replacements,_=self.fixture();row=replacements[1]
        row['native']['sourceFingerprint']='c'*64
        self.resources.prepare_native_cloth(trees,replacements)
        self.groom.validate_surface_skin(row['native'],row['packet'],row['binding']['surfaceSkin'],source_fingerprint='a'*64,binding=row['binding'])
        with self.assertRaisesRegex(ValueError,'fingerprint'):
            self.groom.validate_surface_skin(row['native'],row['packet'],row['binding']['surfaceSkin'],source_fingerprint='d'*64)

    def test_center_skin_rejects_omitted_samples_changed_prefix_and_wrong_bones(self):
        for defect in('missing','duplicate','prefix','palette','skin','parent','span','singular','nonfinite'):
            trees,replacements,_=self.fixture();row=replacements[1];child=trees[40]['childDataList'][0]
            if defect=='missing':row['binding']['surfaceSkin']['vertices'].pop()
            elif defect=='duplicate':row['binding']['surfaceSkin']['vertices'][1]=copy.deepcopy(row['binding']['surfaceSkin']['vertices'][0])
            elif defect=='prefix':row['packet']['boneIndices'][0]=1
            elif defect=='palette':row['packet']['bindPoses'][0]=2.
            elif defect=='skin':row['packet']['weights'][12]=1.
            elif defect=='parent':child['vertexWeightList'][1]['parentIndex']=3
            elif defect=='span':child['vertexInfoList'][1]=(1<<28)|999
            elif defect=='singular':child['vertexWeightList'][1]['localNor']=dict(x=0.,y=0.,z=0.)
            else:child['vertexWeightList'][1]['weight']=float('nan')
            with self.subTest(defect=defect),self.assertRaises(ValueError):self.resources.prepare_native_cloth(trees,replacements)

    def test_center_skin_enforces_the_existing_fifteen_parent_and_four_bone_limits(self):
        for second_count in(7,8):
            trees,replacements,_=self.fixture();child=trees[40]['childDataList'][0]
            template=copy.deepcopy(child['vertexWeightList'][0]);weights=[]
            for count,start in((8,0),(second_count,8),(1,0)):
                for parent in range(start,start+count):weights.append(dict(copy.deepcopy(template),weight=1./count,parentIndex=parent))
            child.update(vertexInfoList=[8<<28,(second_count<<28)|8,(1<<28)|(8+second_count)],vertexWeightList=weights,parentIndexList=[0,8,0])
            trees[40]['vertexCount']=8+second_count
            if second_count==8:
                with self.assertRaisesRegex(ValueError,'packed virtual parent'):self.resources.prepare_native_cloth(trees,replacements)
            else:
                self.resources.prepare_native_cloth(trees,replacements)
                self.assertEqual(len(self.weights(child,3)),15)
        trees,replacements,_=self.fixture();native=replacements[1]['native'];packet=replacements[1]['packet']
        native.update(weights=[.5,.5,0.,0.,.5,.5,0.,0.,1.,0.,0.,0.],boneIndices=[0,1,0,0,2,3,0,0,4,0,0,0],bindPoses=np.eye(4).ravel().tolist()*5)
        packet['weights'][:12]=native['weights'];packet['boneIndices'][:12]=native['boneIndices'];packet['bindPoses']=native['bindPoses'].copy()
        for index in(3,4):
            weights,indices=self.codec.source_surface_skin(native,replacements[1]['binding']['surfaceSkin']['vertices'][index-3]['surface'])
            packet['weights'][index*4:index*4+4]=weights;packet['boneIndices'][index*4:index*4+4]=indices
        with self.assertRaises(self.codec.SurfaceSkinSupportError):self.resources.prepare_native_cloth(trees,replacements)

    def test_center_skin_preserves_the_existing_native_hash_link_chain(self):
        trees,replacements,_=self.fixture();before=copy.deepcopy(trees);result=self.resources.prepare_native_cloth(trees,replacements)
        delta=result['renderDeltas'][10];virtual=result['virtualDeltas'][30]
        self.assertEqual(trees[40]['childDataList'][0]['childDataHash'],before[40]['childDataList'][0]['childDataHash']+delta)
        self.assertEqual(trees[30]['deformer']['renderDeformerHashList'][0],trees[10]['dataHash'])
        self.assertEqual(trees[50]['dataHash'],before[50]['dataHash']+virtual)
        self.assertEqual(trees[50]['virtualDeformerHash'],before[50]['virtualDeformerHash']+virtual)
        self.assertEqual(trees[40]['dataHash'],before[40]['dataHash']+delta+4)

    def test_empty_renderer_slots_preserve_native_cloth_mapping(self):
        trees, replacements, _ = self.fixture()
        before = copy.deepcopy(trees[40]['childDataList'][0])
        replacement = replacements[1]
        replacement['binding'] = None
        replacement['packet'] = copy.deepcopy(replacement['native'])
        replacement['packet']['subMeshes'].append([])

        proof = self.resources.prepare_native_cloth(trees, replacements)

        after = trees[40]['childDataList'][0]
        self.assertEqual(trees[20]['triangleCount'], 1)
        self.assertEqual(proof['renderDeltas'][10], 1)
        self.assertEqual(trees[30]['deformer']['renderDeformerHashList'], [trees[10]['dataHash']])
        self.assertEqual(after['parentIndexList'], before['parentIndexList'])
        self.assertEqual(after['vertexWeightList'], before['vertexWeightList'])
        self.assertEqual(after['vertexInfoList'], before['vertexInfoList'])

    def test_legacy_rows_keep_single_anchor_virtual_transfer_without_a_new_declaration(self):
        trees,replacements,_=self.fixture();replacements[1]['binding'].pop('surfaceSkin')
        before=copy.deepcopy(replacements[1]['packet']);proof=self.resources.prepare_native_cloth(trees,replacements)
        child=trees[40]['childDataList'][0]
        self.assertEqual([len(self.weights(child,index))for index in range(7)],[1]*7)
        self.assertEqual([child['parentIndexList'][index]for index in range(7)],[0,1,2,0,0,2,2])
        self.assertFalse(any(row['kind']=='surface-skin'for row in proof['changes']))
        self.assertEqual(replacements[1]['packet'],before)

    def test_canonical_center_skin_stays_continuous_at_an_anchor_switch_and_preserves_alias_order(self):
        _,replacements,_=self.fixture();native=replacements[1]['native'];saved=copy.deepcopy(native)
        native['weights']=[.5,.3,.2,0.,.4,.35,.25,0.,1.,0.,0.,0.]
        native['boneIndices']=[2,0,3,0,0,2,1,0,1,0,0,0];native['bindPoses']+=np.eye(4).ravel().tolist()
        matrices=np.tile(np.eye(4),(4,1,1));matrices[:,0,3]=[0.,.03,-.05,.09];centers=[]
        points=np.array([[-.0006,.02,1.,1.],[.0006,.02,1.,1.]])
        for fraction in np.linspace(.499,.501,11):
            sample=dict(triangle=[0,1,2],barycentric=[float(fraction),float(1-fraction),0.])
            weights,indices=self.codec.source_surface_skin(native,sample)
            alias=self.codec.source_surface_skin(native,dict(triangle=[2,1,0],barycentric=[0.,float(1-fraction),float(fraction)]))
            self.assertEqual((weights,indices),alias)
            world=sum((points@matrices[bone].T)[:,:3]*weight for bone,weight in zip(indices,weights))
            self.assertAlmostEqual(np.linalg.norm(world[1]-world[0]),.0012,places=9);centers.append(world.mean(axis=0))
        self.assertLess(np.linalg.norm(np.diff(centers,axis=0),axis=1).max(),.00002)
        for sample in (dict(triangle=[0,1,2],barycentric=[True,0.,0.]),dict(triangle=[0,1,2],barycentric=[-.1,.6,.5]),
                       dict(triangle=[0,0,2],barycentric=[.5,.5,0.]),dict(triangle=[0,1,2],barycentric=[.4,.4,.1])):
            with self.subTest(sample=sample),self.assertRaises(ValueError):self.codec.source_surface_skin(native,sample)
        self.assertEqual(native['shapes'],saved['shapes'])

    def test_composed_center_skin_remaps_its_entire_owner_table_and_replays_native_frames(self):
        _,replacements,_=self.fixture();row=replacements[1];source=copy.deepcopy(row['native']);finished=copy.deepcopy(row['packet'])
        source.update(vertexCount=4,vertices=source['vertices']+[.002,.002,0.],normals=source['normals']+[0.,0.,1.],
            tangents=source['tangents']+[1.,0.,0.,1.],weights=source['weights']+[1.,0.,0.,0.],boneIndices=source['boneIndices']+[0,0,0,0],
            sourceFingerprint='c'*64)
        for packet in(source,finished):
            packet.update(colors=[],indexFormat=0,bounds=[.01,.02,.0005,.02,.04,.002])
            packet.setdefault('uv',[dict(channel=2,dimension=4,values=[0.,0.,0.,1.]*packet['vertexCount'])])
            packet['uv'].insert(0,dict(channel=0,dimension=2,values=[0.,0.,0.,0.]*packet['vertexCount']))
        source['shapes'][0]['frames'][0].update(vertices=[0.,0.,.0001]*4,normals=[0.]*12,tangents=[0.]*12)
        binding=copy.deepcopy(row['binding']);skin=binding.pop('surfaceSkin');saved=copy.deepcopy((source,finished,binding,skin))
        result,attachment,proof=self.groom.compose_finished_source_groom(source,finished,binding,surface_skin=skin,
            fine_material_slots=[dict(sourceSlot=0,targetSlot=1)])
        self.assertEqual([value['vertex']for value in proof['surfaceSkin']['vertices']],[4,5,6,7])
        self.assertEqual(proof['surfaceSkin']['sourceFingerprint'],skin['sourceFingerprint'])
        self.groom.validate_surface_skin(source,result,proof['surfaceSkin'],source_fingerprint=skin['sourceFingerprint'],binding=attachment)
        for key,width in(('vertices',3),('normals',3),('tangents',4),('weights',4),('boneIndices',4)):
            self.assertEqual(result[key][:4*width],source[key])
        frame=result['shapes'][0]['frames'][0];original_frame=source['shapes'][0]['frames'][0]
        for key in('vertices','normals','tangents'):self.assertEqual(frame[key][:12],original_frame[key])
        for key in('vertices','normals','tangents'):
            self.assertEqual(frame[key][12:],finished['shapes'][0]['frames'][0][key][9:])
        with tempfile.TemporaryDirectory()as directory:
            path=Path(directory)/'binding.bytes';self.groom.encode_legacy_binding(attachment,path)
            self.assertEqual(self.groom.decode_binding(path),attachment)
            self.assertEqual(json.loads(json.dumps(proof['surfaceSkin'])),proof['surfaceSkin'])
        broken=copy.deepcopy(proof['surfaceSkin']);broken['vertices'].pop()
        with self.assertRaisesRegex(ValueError,'omitted'):
            self.groom.validate_surface_skin(source,result,broken,binding=attachment)
        self.assertEqual((source,finished,binding,skin),saved)

    def test_five_support_native_surface_is_held_without_changing_any_stored_fit(self):
        _,replacements,_=self.fixture();native=copy.deepcopy(replacements[1]['native'])
        native.update(weights=[.5,.5,0.,0.,.5,.5,0.,0.,1.,0.,0.,0.],boneIndices=[0,1,0,0,2,3,0,0,4,0,0,0],
            bindPoses=np.eye(4).ravel().tolist()*5,uv=[dict(channel=0,dimension=2,values=[0.,0.,0.,0.]*3)])
        current=copy.deepcopy(native);before=copy.deepcopy((native,current))
        owners={(0,(0,1,2)):(np.array([0,1,2]),np.array([[0,1,2]]),np.array([[0.,0.]]),np.eye(2)[None])}
        domain=mock.Mock();domain.surface_hair.return_value=True
        with mock.patch.object(self.groom,'retained_core_owners',return_value=(owners,{})),mock.patch.object(self.groom,'SourceHairDomain',return_value=domain):
            masks,facts=self.groom.retained_carrier_face_mask(native,current,np.eye(4).ravel().tolist(),[dict(shader='Custom/FakeAnisoHair')],.008)
        self.assertFalse(masks[0][0]);self.assertEqual(facts['heldNativeFaces']['native-skin-support-exceeds-four'],1)
        self.assertEqual((native,current),before)

    def test_skin_only_surfaces_do_not_acquire_an_unneeded_virtual_frame_requirement(self):
        _,replacements,_=self.fixture();row=replacements[1]
        row['native']['normals']=[0.]*9;row['native']['tangents']=[0.,0.,0.,1.]*3
        saved=copy.deepcopy(row)
        self.assertEqual(self.resources.prepare_native_cloth({},replacements)['changes'],[])
        self.assertEqual(row,saved)
        row['binding']['surfaceSkin']['vertices'].pop()
        with self.assertRaisesRegex(ValueError,'omitted'):
            self.resources.prepare_native_cloth({},replacements)
        trees,replacements,_=self.fixture();row=replacements[1]
        row['native']['normals'][6:9]=[0.,0.,0.];row['native']['tangents'][8:11]=[0.,0.,0.]
        for declaration in row['binding']['surfaceSkin']['vertices']:
            index=declaration['vertex'];declaration['surface']['barycentric']=[.5,.5,0.]
            row['binding']['vertices'][index]['anchor']=0
            weights,indices=self.codec.source_surface_skin(row['native'],declaration['surface'])
            row['packet']['weights'][index*4:index*4+4]=weights;row['packet']['boneIndices'][index*4:index*4+4]=indices
        self.resources.prepare_native_cloth(trees,replacements)


if __name__=='__main__':
    unittest.main()
