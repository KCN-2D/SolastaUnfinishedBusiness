"""Pure naming/path contracts; no Blender, Unity, files, or game scenarios."""
import importlib.util
import copy
import hashlib
import json
import fnmatch
from pathlib import Path
from typing import Any
import sys
import subprocess
import unittest
import tempfile
from types import SimpleNamespace
from unittest import mock

_PATH = Path(__file__).resolve().parents[1] / 'asset_names.py'
_SPEC = importlib.util.spec_from_file_location('asset_names', _PATH)
assert _SPEC is not None and _SPEC.loader is not None
names = importlib.util.module_from_spec(_SPEC)
sys.modules[_SPEC.name] = names
_SPEC.loader.exec_module(names)


class ResourceNameContracts(unittest.TestCase):
    def test_managed_python_sources_cannot_escape_project_diagnostics(self):
        root=Path(__file__).resolve().parents[3]
        configuration=json.loads((root/'pyrightconfig.json').read_text('utf-8-sig'))
        sources=subprocess.check_output(['git','ls-files','--cached','--others','--exclude-standard','--','*.py'],cwd=root,text=True,encoding='utf-8').splitlines()
        self.assertTrue(sources)
        for source in set(sources):
            self.assertTrue(any(Path(source).match(pattern)for pattern in configuration['include']),source)
            self.assertFalse(any(Path(source).match(pattern)for pattern in configuration['exclude']),source)

    def test_public_collision_rejects_without_ordinal_fallback(self):
        rows=[names.ResourceName('a'*64,'Source'),names.ResourceName('b'*64,'source')]
        with self.assertRaisesRegex(ValueError,'Semantic resource collision'):
            names.resource_names(rows,'.bundle',lowercase=True,strict=True,maximum=128)
        self.assertEqual(names.resource_names(rows,'.bundle',lowercase=True),
                         {'a'*64:'source_variant01.bundle','b'*64:'source_variant02.bundle'})

    def test_public_budget_and_semantic_detail(self):
        self.assertEqual(names.resource_names([names.ResourceName('key','Model','Hands','LOD0')],
                         '.bundle',strict=True,maximum=128),{'key':'Model_LOD0.bundle'})
        with self.assertRaises(ValueError):
            names.resource_names([names.ResourceName('key','x'*122)],'.bundle',strict=True,maximum=128)
    def test_native_label_and_opaque_identity_are_separate(self):
        key = 'a' * 64
        result = names.resource_names([names.ResourceName(key, 'Assets/Characters/Source Style.prefab', suffix='Color')], '.png')
        self.assertEqual(result[key], 'Source_Style_Color.png')
        self.assertNotIn(key, result[key])

    def test_semantic_mesh_parts_resolve_a_shared_style(self):
        rows = [names.ResourceName('source-b', 'Source_Style', 'Source_PartB', 'LOD0'),
                names.ResourceName('source-a', 'Source_Style', 'Source_PartA', 'LOD0')]
        expected: dict[str, Any] = {'source-a': 'Source_Style_PartA_LOD0.binding.json',
                    'source-b': 'Source_Style_PartB_LOD0.binding.json'}
        self.assertEqual(names.resource_names(rows, '.binding.json'), expected)
        self.assertEqual(names.resource_names(reversed(rows), '.binding.json'), expected)

    def test_casefold_collision_uses_semantic_details(self):
        rows = [names.ResourceName('one', 'Style', 'PartA'), names.ResourceName('two', 'style', 'PartB')]
        result = names.resource_names(rows, '.bundle', lowercase=True)
        self.assertEqual(result, {'one': 'style_parta.bundle', 'two': 'style_partb.bundle'})

    def test_remaining_variants_are_deterministic(self):
        rows = [names.ResourceName('b', 'Style'), names.ResourceName('a', 'Style')]
        expected: dict[str, Any] = {'a': 'Style_Variant01.png', 'b': 'Style_Variant02.png'}
        self.assertEqual(names.resource_names(rows, '.png'), expected)
        self.assertEqual(names.resource_names(reversed(rows), '.png'), expected)

    def test_variants_do_not_take_a_declared_singleton(self):
        rows = [names.ResourceName('a', 'Style'), names.ResourceName('b', 'Style'),
                names.ResourceName('c', 'Style_Variant01')]
        result = names.resource_names(rows, '.png')
        self.assertEqual(len({value.casefold() for value in result.values()}), 3)
        self.assertEqual(result['c'], 'Style_Variant01.png')

    def test_duplicate_keys_and_invalid_labels_reject(self):
        with self.assertRaises(ValueError):
            names.resource_names([names.ResourceName('one', 'A'), names.ResourceName('one', 'B')])
        for value in ('', '..', 'CON', 'LPT1'):
            with self.subTest(value=value), self.assertRaises(ValueError):
                names.source_label(value)

    def test_component_limit_includes_extension(self):
        self.assertEqual(len(names.resource_names([names.ResourceName('a', 'x' * 251)], '.png')['a']), 255)
        with self.assertRaises(ValueError):
            names.resource_names([names.ResourceName('a', 'x' * 252)], '.png')

    def test_path_budget_counts_meta_and_utf16(self):
        root = str(Path(__file__).resolve().parent)
        value = root + '/' + 'x' * (254 - len(root) - 1)
        self.assertEqual(names.assert_path_budget(value, 259, '.meta'), value)
        with self.assertRaises(ValueError):
            names.assert_path_budget(value + 'x', 259, '.meta')
        with self.assertRaises(ValueError):
            names.assert_path_budget(value + '\U0001F600', 260, '.meta')
        self.assertFalse(Path(value).exists())

    def test_invalid_extension_or_relative_budget_reject(self):
        with self.assertRaises(ValueError):
            names.resource_names([names.ResourceName('a', 'Style')], '/../file')
        with self.assertRaises(ValueError):
            names.assert_path_budget('relative/file.png', 259)


_BUILD_PATH=Path(__file__).resolve().parents[2]/'blender-models/CharacterFaces/build_models.py'
_BUILD_SPEC=importlib.util.spec_from_file_location('character_model_publication',_BUILD_PATH)
assert _BUILD_SPEC is not None and _BUILD_SPEC.loader is not None
publisher=importlib.util.module_from_spec(_BUILD_SPEC)
_BUILD_SPEC.loader.exec_module(publisher)
setattr(publisher,'asset_names',names)


class CharacterPublicNameContracts(unittest.TestCase):
    def test_resource_retirement_plan_keeps_referenced_and_current_catalog_data(self):
        with tempfile.TemporaryDirectory()as temporary:
            root=Path(temporary).resolve();artifacts=root/'tools/game-test/artifacts';old=artifacts/'Old';catalog=old/'Catalog'
            catalog.mkdir(parents=True)
            files=[]
            for name in ('retired.bundle','retained.bundle'):
                path=catalog/name;path.write_bytes(name.encode())
                files.append(dict(file=name,sha256=publisher.sha(path)))
            manifest=catalog/'CharacterModels.manifest.json'
            manifest.write_text(json.dumps(dict(bundles=files,meshes=[])),encoding='utf8')
            (old/'ResourceAssembly.json').write_text(json.dumps(dict(status='PASS_STANDALONE_CHARACTER_RESOURCES',catalogSha256=publisher.sha(manifest))),encoding='utf8')
            reference=artifacts/'References.json';reference.write_text(json.dumps(dict(schemaVersion=1,paths=[str(catalog/'retained.bundle')])),encoding='utf8')
            output=artifacts/'PayloadRetirementPlan.json'
            with mock.patch.object(publisher,'ROOT',root):
                plan=publisher.create_resource_retirement_plan([old,old],reference,output)
                self.assertEqual([Path(row['path']).name for row in plan['files']],['retired.bundle'])
                self.assertEqual(len(plan['roots']),1)
                self.assertEqual(plan['files'][0]['sha256'],publisher.sha(catalog/'retired.bundle'))
                self.assertTrue((catalog/'retired.bundle').is_file())
                reference.write_text(json.dumps(dict(schemaVersion=1,paths=[str(catalog)])),encoding='utf8')
                plan=publisher.create_resource_retirement_plan([old],reference,output)
                self.assertEqual(plan['files'],[])
                with self.assertRaisesRegex(ValueError,'below artifacts'):
                    publisher.create_resource_retirement_plan([root],reference,output)
                (old/'ResourceAssembly.json').write_text(json.dumps(dict(status='INCOMPLETE',catalogSha256=publisher.sha(manifest))),encoding='utf8')
                with self.assertRaisesRegex(ValueError,'completed resource owner'):
                    publisher.create_resource_retirement_plan([old],reference,output)

    def document(self,second=False) -> dict[str, Any]:
        files=['pn_'+'b'*20+'.bundle']+(['pn_'+'c'*20+'.bundle'] if second else [])
        meshes=[dict(sourceFingerprint=chr(97+i)*64,mode='authored',authoredFingerprint=chr(99+i)*64,
                     authoredMeshAsset='assets/mesh'+str(i)+'.asset',bundleFile=file)
                for i,file in enumerate(files)]
        return dict(schemaVersion=2,recipeVersion='contract',bundles=[dict(file=file,sha256='d'*64)for file in files],
                    meshes=meshes,materials=[],textureRemaps=[])

    def provenance(self,document) -> dict[str, Any]:
        return dict(schemaVersion=1,manifestSha256='e'*64,authorities=[],payloads=[
            dict(file=mesh['bundleFile'],purpose='pn',sources=[dict(sourceFingerprint=mesh['sourceFingerprint'],
                sourceModel='Example_Male_Shape_CA_Hands',part='Hands',lod=0)])for mesh in document['meshes']])

    def test_roundtrip_preserves_all_nested_references_and_shas(self):
        before=self.document();old=before['bundles'][0]['file']
        before['meshes'][0]['jointAttachments']=[dict(bundleFile=old,bodySourceFingerprint='f'*64,
            authoredMeshAsset='assets/joint.asset')]
        before['materials']=[dict(sourceFingerprint='a'*64,materialSlot=0,sourceShader='Skin',
            bundleFile=old,authoredProfile=dict(bundleFile=old,materialAsset='assets/skin.mat'),
            properties=[dict(name='_MainTex',kind='texture-remap',bundleFile=old,textureAsset='assets/color.asset')])]
        before['textureRemaps']=[dict(sourceShader='Skin',name='_BumpMap',sourceTextureFingerprint='f'*64,
            bundleFile=old,textureAsset='assets/normal.asset')]
        mapping,after=publisher.public_name_plan(before,self.provenance(before),before)
        self.assertEqual(mapping[old],'example_male_ca_hands_lod0_pn.bundle')
        self.assertEqual(publisher.rewrite_catalog_files(after,{v:k for k,v in mapping.items()}),before)
        self.assertEqual(after['bundles'][0]['sha256'],before['bundles'][0]['sha256'])
        self.assertEqual(after['materials'][0]['properties'][0]['textureAsset'],'assets/color.asset')

    def test_existing_semantic_owner_survives_new_alias_and_new_bytes(self):
        candidate=self.document();previous=publisher.rewrite_catalog_files(candidate,
            {candidate['bundles'][0]['file']:'existing_shared_hands_lod0_pn.bundle'})
        candidate['bundles'][0]['sha256']='f'*64
        proof=self.provenance(candidate);proof['payloads'][0]['sources'].append(dict(
            sourceFingerprint='a'*64,sourceModel='Example_Male_Shape_AF_Hands',part='Hands',lod=0))
        mapping,after=publisher.public_name_plan(candidate,proof,previous)
        self.assertEqual(next(iter(mapping.values())),'existing_shared_hands_lod0_pn.bundle')
        self.assertEqual(after['bundles'][0]['sha256'],'f'*64)

    def test_hashless_candidate_job_name_needs_provenance_and_reuses_owner(self):
        previous=self.document();old=previous['bundles'][0]['file']
        previous=publisher.rewrite_catalog_files(previous,{old:'existing_shared_hands_lod0_pn.bundle'})
        candidate=publisher.rewrite_catalog_files(previous,{'existing_shared_hands_lod0_pn.bundle':'field_only_v4_retry1.bundle'})
        proof=self.provenance(candidate);empty=copy.deepcopy(proof);empty['payloads']=[]
        with self.assertRaisesRegex(ValueError,'candidate-only'):
            publisher.public_name_plan(candidate,empty,previous)
        mapping,_=publisher.public_name_plan(candidate,proof,previous)
        self.assertEqual(mapping['field_only_v4_retry1.bundle'],'existing_shared_hands_lod0_pn.bundle')

    def test_model_lod_associations_do_not_collapse_to_cartesian_sets(self):
        def source(model,lod):return dict(sourceFingerprint='a'*64,sourceModel=model,part='Body',lod=lod)
        a=[source('Source_A_Body',0),source('Source_B_Body',1)]
        b=[source('Source_A_Body',1),source('Source_B_Body',0)]
        self.assertNotEqual(publisher.public_source_label(a),publisher.public_source_label(b))
        self.assertEqual(publisher.public_source_label(a),publisher.public_source_label(list(reversed(a))))

    def test_casefold_semantic_collision_contains_source_provenance(self):
        candidate=self.document(second=True)
        with self.assertRaisesRegex(ValueError,'sourceFingerprint'):
            publisher.public_name_plan(candidate,self.provenance(candidate),candidate)

    def test_real_native_model_from_another_consumer_cannot_label_payload(self):
        candidate=self.document();proof=self.provenance(candidate)
        proof['payloads'][0]['sources'][0]['sourceFingerprint']='f'*64
        with self.assertRaisesRegex(ValueError,'another payload consumer'):
            publisher.public_name_plan(candidate,proof,candidate)

    def test_missing_provenance_and_untyped_payload_reject(self):
        candidate=self.document();proof=self.provenance(candidate);proof['payloads']=[]
        with self.assertRaisesRegex(ValueError,'lacks actual source'):
            publisher.public_name_plan(candidate,proof,candidate)
        candidate['bundles'].append(dict(file='Unowned.bundle',sha256='a'*64))
        proof=self.provenance(candidate);proof['payloads'].append(dict(file='Unowned.bundle',purpose='pn',
            sources=copy.deepcopy(proof['payloads'][0]['sources'])))
        with self.assertRaisesRegex(ValueError,'no typed manifest consumer'):
            publisher.public_name_plan(candidate,proof,candidate)

    def test_safe_leaf_and_casefold_duplicate_validation(self):
        for bad in ('../outside.bundle','C:outside.bundle','..','folder\\file.bundle','NUL.bundle','A?.bundle','trailing.bundle.'):
            candidate=self.document();candidate['bundles'][0]['file']=bad
            with self.subTest(bad=bad),self.assertRaises(ValueError):publisher.catalog_payloads(candidate)
        candidate=self.document();candidate['bundles'] += [dict(file=candidate['bundles'][0]['file'].upper(),sha256='d'*64)]
        with self.assertRaisesRegex(ValueError,'Case-insensitive'):publisher.catalog_payloads(candidate)

    def fixture(self,root):
        current=root/'SolastaUnfinishedBusiness/BlenderModels/CharacterModels';snapshot=root/'snapshot';candidate=root/'candidate'
        for directory in (current,snapshot,candidate):directory.mkdir(parents=True)
        original=self.document();old=original['bundles'][0]['file'];original['bundles'][0]['sha256']=hashlib.sha256(b'old').hexdigest()
        changed=copy.deepcopy(original);changed['bundles'][0]['sha256']=hashlib.sha256(b'new').hexdigest()
        for directory,document,data in ((current,original,b'old'),(snapshot,original,b'old'),(candidate,changed,b'new')):
            (directory/old).write_bytes(data);(directory/'CharacterModels.manifest.json').write_text(json.dumps(document),encoding='utf-8')
        (snapshot/'SourceCatalog.json').write_text('{}',encoding='utf-8')
        quality: dict[str, Any]=dict(status='PASS_COMPLETE_HUMANOID_CATALOG',completeHumanlikeScope=True,
            candidateManifestSha256=publisher.sha(candidate/'CharacterModels.manifest.json'),sourceCatalog=str(snapshot),
            sourceManifestSha256=publisher.sha(snapshot/'CharacterModels.manifest.json'))
        (candidate/'CatalogQuality.json').write_text(json.dumps(quality),encoding='utf-8')
        metadata=root/'NativeSourceInventory.json';metadata.write_text(json.dumps(dict(rows=[dict(sourceFingerprint='a'*64,
            part='Hands',lod=0,sourceAliases=[dict(actualFactoryPath='Model/Prefabs/Example_Male_Shape_CA_Hands.prefab')])])),encoding='utf-8')
        return current,snapshot,candidate,metadata,old

    def publish(self,root,candidate,metadata,current):
        args=['build_models.py','--catalog',str(candidate),'--expected-current-sha256',publisher.sha(current/'CharacterModels.manifest.json'),
              '--name-source-metadata',str(metadata)]
        with mock.patch.object(publisher,'ROOT',root),mock.patch.object(publisher,'HERE',root/'evidence'),mock.patch.object(sys,'argv',args):
            publisher.publish_catalog()

    def test_atomic_rename_and_immutable_source_snapshot(self):
        with tempfile.TemporaryDirectory()as folder:
            root=Path(folder).resolve();current,snapshot,candidate,metadata,old=self.fixture(root)
            self.publish(root,candidate,metadata,current)
            published=json.loads((current/'CharacterModels.manifest.json').read_text(encoding='utf-8'))
            new=published['bundles'][0]['file'];self.assertEqual(new,'example_male_ca_hands_lod0_pn.bundle')
            self.assertEqual((current/new).read_bytes(),b'new');self.assertFalse((current/old).exists())
            self.assertEqual((snapshot/old).read_bytes(),b'old');self.assertEqual((candidate/old).read_bytes(),b'new')

    def test_post_manifest_failure_rolls_back_all_public_changes(self):
        with tempfile.TemporaryDirectory()as folder:
            root=Path(folder).resolve();current,snapshot,candidate,metadata,old=self.fixture(root)
            before=(current/'CharacterModels.manifest.json').read_bytes();native_unlink=Path.unlink
            def unlink(path,*args,**kwargs):
                if path==current/old:raise OSError('injected retirement failure')
                return native_unlink(path,*args,**kwargs)
            with mock.patch.object(Path,'unlink',unlink),self.assertRaisesRegex(OSError,'injected'):
                self.publish(root,candidate,metadata,current)
            self.assertEqual((current/'CharacterModels.manifest.json').read_bytes(),before)
            self.assertEqual((current/old).read_bytes(),b'old')
            self.assertEqual(sorted(p.name for p in current.iterdir()),sorted([old,'CharacterModels.manifest.json']))

    def test_already_named_candidate_preserves_original_rename_receipt(self):
        with tempfile.TemporaryDirectory()as folder:
            root=Path(folder).resolve();current,snapshot,candidate,metadata,old=self.fixture(root)
            new='example_male_ca_hands_lod0_pn.bundle'
            changed=json.loads((candidate/'CharacterModels.manifest.json').read_text(encoding='utf-8'))
            changed=publisher.rewrite_catalog_files(changed,{old:new})
            (candidate/old).rename(candidate/new)
            (candidate/'CharacterModels.manifest.json').write_text(json.dumps(changed),encoding='utf-8')
            quality=json.loads((candidate/'CatalogQuality.json').read_text(encoding='utf-8'))
            quality['candidateManifestSha256']=publisher.sha(candidate/'CharacterModels.manifest.json')
            (candidate/'CatalogQuality.json').write_text(json.dumps(quality),encoding='utf-8')
            self.publish(root,candidate,metadata,current)
            receipt=json.loads((root/'evidence/Publication.json').read_text(encoding='utf-8'))
            self.assertEqual(receipt['renames'],{old:new})
            self.assertEqual((current/new).read_bytes(),b'new')
            self.assertFalse((current/old).exists())
            self.assertEqual((snapshot/old).read_bytes(),b'old')

    def test_standalone_bundle_rename_preserves_typed_resource_dependencies(self):
        document: dict[str, Any]=dict(schemaVersion=3,bundles=[dict(file='body_parts.bundle',sha256='d'*64)],
            meshes=[],materials=[],textureRemaps=[],resources=[dict(source='Body/Body.prefab',type='GameObject',
                asset='assets/body.prefab',bundles=[dict(file='body_parts.bundle',name='body_parts.bundle')],parameters=[])])
        renamed=publisher.rewrite_catalog_files(document,{'body_parts.bundle':'human_male_body_parts.bundle'})
        self.assertEqual(renamed['resources'][0]['bundles'],[dict(file='human_male_body_parts.bundle',name='human_male_body_parts.bundle')])
        self.assertEqual(publisher.rewrite_catalog_files(renamed,{'human_male_body_parts.bundle':'body_parts.bundle'}),document)

    def resource_fixture(self,root):
        current,snapshot,candidate,metadata,old=self.fixture(root)
        file='example_male_body_parts.bundle';data=b'prepared standalone part'
        (candidate/file).write_bytes(data)
        document: dict[str, Any]=dict(schemaVersion=3,recipeVersion='standalone-character-resources-v1',
            bundles=[dict(file=file,sha256=publisher.sha(candidate/file))],meshes=[],materials=[],textureRemaps=[],
            resources=[dict(source='Body/Body.prefab',asset='assets/body.prefab',type='GameObject',
                bundles=[dict(file=file,name=file)],parameters=[])])
        (candidate/'CharacterModels.manifest.json').write_text(json.dumps(document),encoding='utf-8')
        proof=dict(status='PASS_STANDALONE_CHARACTER_RESOURCES',originalModelDependenciesInherited=False,
            resources=1,bundles=1,bytes=len(data),inputs=[],proofs=[dict(file=file,sha256=publisher.sha(candidate/file),bytes=len(data),
                source=str(current/old),sourceSha256=publisher.sha(snapshot/old),allRetainedObjectFieldsExactAfterReferenceRebasing=True)])
        proof_path=candidate/'ResourceAssembly.json';proof_path.write_text(json.dumps(proof),encoding='utf-8')
        quality=json.loads((candidate/'CatalogQuality.json').read_text(encoding='utf-8'))
        quality.update(candidateManifestSha256=publisher.sha(candidate/'CharacterModels.manifest.json'),
            resourceAssembly=dict(path=str(proof_path),sha256=publisher.sha(proof_path)))
        (candidate/'CatalogQuality.json').write_text(json.dumps(quality),encoding='utf-8')
        return current,snapshot,candidate,metadata,old,file

    def test_standalone_publication_retains_original_authoring_payload(self):
        with tempfile.TemporaryDirectory()as folder:
            root=Path(folder).resolve();current,snapshot,candidate,metadata,old,file=self.resource_fixture(root)
            self.publish(root,candidate,metadata,current)
            self.assertFalse((current/old).exists())
            self.assertEqual((snapshot/old).read_bytes(),b'old')
            self.assertEqual((current/file).read_bytes(),b'prepared standalone part')
            self.assertEqual(json.loads((current/'CharacterModels.manifest.json').read_text(encoding='utf-8'))['schemaVersion'],3)

    def prepared_resource_fixture(self,root):
        current,snapshot,candidate,metadata,old,file=self.resource_fixture(root)
        prior=root/'prior-part.bundle';prior.write_bytes((candidate/file).read_bytes())
        (candidate/file).write_bytes(b'prepared part with an owned material update')
        manifest=json.loads((candidate/'CharacterModels.manifest.json').read_text('utf-8'))
        manifest['bundles'][0]['sha256']=publisher.sha(candidate/file)
        (candidate/'CharacterModels.manifest.json').write_text(json.dumps(manifest),'utf-8')
        assembly=json.loads((candidate/'ResourceAssembly.json').read_text('utf-8'))
        row=assembly['proofs'][0];row.update(sha256=publisher.sha(candidate/file),bytes=(candidate/file).stat().st_size,
            allRetainedObjectFieldsExactAfterReferenceRebasing=False,
            declaredPreparedMutations=[dict(kind='owned-fiber-anisotropy',proof=dict(
                source=dict(path=str(prior),sha256=publisher.sha(prior)),target=dict(path=str(candidate/file),sha256=publisher.sha(candidate/file)),
                allOtherObjectBytesExact=True,resourcePayloadsExact=True,cabAndExternalReferencesUnchanged=True,savedObjectBytesExact=True))])
        assembly['bytes']=row['bytes']
        return current,snapshot,candidate,metadata,old,file,assembly

    def write_resource_proof(self,candidate,assembly):
        proof_path=candidate/'ResourceAssembly.json';proof_path.write_text(json.dumps(assembly),'utf-8')
        quality=json.loads((candidate/'CatalogQuality.json').read_text('utf-8'))
        quality.update(candidateManifestSha256=publisher.sha(candidate/'CharacterModels.manifest.json'),
            resourceAssembly=dict(path=str(proof_path),sha256=publisher.sha(proof_path)))
        (candidate/'CatalogQuality.json').write_text(json.dumps(quality),'utf-8')

    def test_prepared_material_publication_preserves_its_immutable_source(self):
        with tempfile.TemporaryDirectory()as folder:
            root=Path(folder).resolve();current,snapshot,candidate,metadata,old,file,assembly=self.prepared_resource_fixture(root)
            self.write_resource_proof(candidate,assembly);self.publish(root,candidate,metadata,current)
            self.assertEqual((current/file).read_bytes(),b'prepared part with an owned material update')
            self.assertEqual((root/'prior-part.bundle').read_bytes(),b'prepared standalone part')

    def test_prepared_publication_rejects_unowned_or_unverified_bytes_before_write(self):
        for mode in ('missing-proof','different-target','changed-source','unowned-field','unknown-kind'):
            with self.subTest(mode=mode),tempfile.TemporaryDirectory()as folder:
                root=Path(folder).resolve();current,snapshot,candidate,metadata,old,file,assembly=self.prepared_resource_fixture(root)
                row=assembly['proofs'][0];mutation=row['declaredPreparedMutations'][0];proof=mutation['proof']
                if mode=='missing-proof':row['declaredPreparedMutations']=[]
                elif mode=='different-target':proof['target']['sha256']='0'*64
                elif mode=='changed-source':(root/'prior-part.bundle').write_bytes(b'changed after preparation')
                elif mode=='unowned-field':proof['cabAndExternalReferencesUnchanged']=False
                else:mutation['kind']='unverified-change'
                self.write_resource_proof(candidate,assembly);before=(current/'CharacterModels.manifest.json').read_bytes()
                with self.assertRaises(ValueError):self.publish(root,candidate,metadata,current)
                self.assertEqual((current/'CharacterModels.manifest.json').read_bytes(),before)
                self.assertEqual((current/old).read_bytes(),b'old')
                self.assertFalse((current/file).exists())

    def test_prepared_texture_publication_requires_its_own_saved_gpu_bytes(self):
        for linked in (True,False):
            with self.subTest(linked=linked),tempfile.TemporaryDirectory()as folder:
                root=Path(folder).resolve();current,snapshot,candidate,metadata,old,file,assembly=self.prepared_resource_fixture(root)
                mutation=assembly['proofs'][0]['declaredPreparedMutations'][0];mutation['kind']='Main-encoded-spans';proof=mutation['proof']
                proof.update(status='PASS_CPU_BC7_RESOURCE_SPAN_REPLACEMENT',allCabBytesExact=True,memberNamesOrderFlagsExact=True,
                    allUnownedResourceBytesExact=True,allTextureMetadataAndAliasesExact=True,sourceUnchanged=True,reopenedEncodedBytesExact=True,
                    textures=[dict(patchedSha256=publisher.sha(candidate/file))])
                reports={
                    'input':{},'report':dict(completed=True,savedReloadGpuExact=True,saved=[dict(variant='FieldBC7',encodedSha256=publisher.sha(candidate/file)if linked else'0'*64)]),
                    'verification':dict(status='PRODUCTION_FIELD_ONLY_BC7_PAYLOAD_AND_ALL_GPU_MIPS_EXACT')}
                references={}
                for key,value in reports.items():
                    path=root/(key+'.json');path.write_text(json.dumps(value),'utf-8');references[key]=dict(path=str(path),sha256=publisher.sha(path))
                assembly['surfaceFinishing']=dict(codecs=[references]);self.write_resource_proof(candidate,assembly)
                if linked:self.publish(root,candidate,metadata,current)
                else:
                    with self.assertRaisesRegex(ValueError,'saved GPU evidence'):self.publish(root,candidate,metadata,current)
                    self.assertEqual((current/old).read_bytes(),b'old')

    def test_standalone_publication_failure_restores_legacy_catalog(self):
        with tempfile.TemporaryDirectory()as folder:
            root=Path(folder).resolve();current,snapshot,candidate,metadata,old,file=self.resource_fixture(root)
            before=(current/'CharacterModels.manifest.json').read_bytes();native_unlink=Path.unlink
            def unlink(path,*args,**kwargs):
                if path==current/old:raise OSError('injected resource retirement failure')
                return native_unlink(path,*args,**kwargs)
            with mock.patch.object(Path,'unlink',unlink),self.assertRaisesRegex(OSError,'injected resource'):
                self.publish(root,candidate,metadata,current)
            self.assertEqual((current/'CharacterModels.manifest.json').read_bytes(),before)
            self.assertEqual((current/old).read_bytes(),b'old')
            self.assertFalse((current/file).exists())

    def test_reparse_and_unowned_root_reject_before_write(self):
        with tempfile.TemporaryDirectory()as folder:
            root=Path(folder).resolve();owned=root/'owned';owned.mkdir();native_lstat=Path.lstat
            def lstat(path):return SimpleNamespace(st_file_attributes=0x400,st_mode=native_lstat(path).st_mode)if path==owned else native_lstat(path)
            with mock.patch.object(publisher,'ROOT',root),mock.patch.object(Path,'lstat',lstat),self.assertRaisesRegex(ValueError,'reparse'):
                publisher.checked_catalog_path(owned)
            with mock.patch.object(publisher,'ROOT',owned),self.assertRaisesRegex(ValueError,'outside'):
                publisher.checked_catalog_path(root)


class PythonConfigurationContracts(unittest.TestCase):
    def test_type_check_scope_covers_every_managed_python_source(self):
        root=_PATH.parents[2]
        result=subprocess.run(['git','ls-files','--','*.py'],cwd=root,check=True,capture_output=True,text=True,encoding='utf-8',timeout=10)
        sources=result.stdout.splitlines()
        config=json.loads((root/'pyrightconfig.json').read_text(encoding='utf-8-sig'))
        included=config['include']
        missing=[source for source in sources if not any(source==pattern or source.startswith(pattern.rstrip('/')+'/')or fnmatch.fnmatchcase(source,pattern)for pattern in included)]
        self.assertTrue(sources)
        self.assertEqual(missing,[],'Managed Python sources are missing from the project type check.')


class CharacterResourceContracts(unittest.TestCase):
    def test_invalid_cloth_is_disabled_without_resigning_guard_identities(self):
        api=self.resource_module()
        trees: dict[int,dict[str,Any]]={
            1:dict(m_Enabled=1,dataHash=77,clothData=dict(m_FileID=0,m_PathID=2),clothDataHash=12,clothDataVersion=5,
                   clothSelection=dict(m_FileID=0,m_PathID=3),clothSelectionHash=99,clothSelectionVersion=1),
            2:dict(dataHash=12,dataVersion=5,vertices=[1,2]),
            3:dict(dataHash=-17,dataVersion=2,selection=[1,0,1])}
        original=copy.deepcopy(trees)
        repairs=api.native_cloth_reference_mismatches(trees)
        self.assertEqual({row['field']for row in repairs},{'clothSelectionHash','clothSelectionVersion'})
        self.assertEqual(trees[1]['clothSelectionHash'],99)
        self.assertEqual(trees[1]['clothSelectionVersion'],1)
        self.assertEqual(trees[1]['dataHash'],77)
        self.assertEqual(trees[2],original[2]);self.assertEqual(trees[3],original[3])
        self.assertEqual(len(api.prepare_native_cloth_state(trees)),1)
        self.assertEqual(trees[1]['m_Enabled'],0)
        self.assertEqual(api.prepare_native_cloth_state(trees),[])

    def test_cloth_reference_cache_cannot_repair_invalid_share_data(self):
        api=self.resource_module()
        trees: dict[int,dict[str,Any]]={1:dict(clothData=dict(m_FileID=0,m_PathID=2),clothDataHash=9,
                                           clothSelection=dict(m_FileID=0,m_PathID=0)),
                                      2:dict(dataHash=0,dataVersion=5)}
        with self.assertRaisesRegex(ValueError,'saved identity'):
            api.native_cloth_reference_mismatches(trees)

    def test_empty_cloth_chain_is_disabled_and_valid_chain_is_retained(self):
        api=self.resource_module()
        trees: dict[int,dict[str,Any]]={
            1:dict(m_Enabled=1,deformer=dict(renderDeformerList=[dict(m_FileID=0,m_PathID=0)])),
            2:dict(m_Enabled=1,virtualDeformer=dict(m_FileID=0,m_PathID=1)),
            3:dict(m_Enabled=1,deformer=dict(renderDeformerList=[dict(m_FileID=0,m_PathID=4)])),
            4:dict(m_Enabled=1,sharedMesh=dict(m_FileID=0,m_PathID=5))}
        original=copy.deepcopy(trees)
        self.assertEqual({row['component']for row in api.prepare_native_cloth_state(trees)},{1,2})
        self.assertEqual(trees[3],original[3]);self.assertEqual(trees[4],original[4])
        self.assertEqual(trees[1]['deformer'],original[1]['deformer'])

    def test_serialized_bytes_include_staged_and_empty_payloads(self):
        owner=self.resource_module()
        original=SimpleNamespace(data=None,get_raw_data=lambda:b'original')
        staged=SimpleNamespace(data=b'edited',get_raw_data=lambda:b'original')
        empty=SimpleNamespace(data=b'',get_raw_data=lambda:b'original')
        self.assertEqual(owner.serialized_object_bytes(original),b'original')
        self.assertEqual(owner.serialized_object_bytes(staged),b'edited')
        self.assertEqual(owner.serialized_object_bytes(empty),b'')

    @staticmethod
    def resource_module():
        directory=_PATH.parent/'UnityAssetBuild'
        if str(directory)not in sys.path:sys.path.insert(0,str(directory))
        import character_resources
        return character_resources

    def test_groom_lod_coverage_includes_untextured_lower_lod_consumers(self):
        api=self.resource_module()
        pointer=lambda key:dict(m_FileID=0,m_PathID=key)
        def item(kind,key,tree):
            return SimpleNamespace(type=SimpleNamespace(name=kind),path_id=key,read_typetree=lambda:tree)
        def material(groom,texture):
            return dict(m_SavedProperties=dict(m_Floats=[('_CoreResponse',1.),('_SupportInVertexFrame',1.),('_FlowInVertexFrame',2.)]if groom else[('_Metallic',0.)],m_TexEnvs=[('_MainTex',dict(m_Texture=pointer(texture)))]))
        objects={1:item('Material',1,material(True,90)),2:item('Material',2,material(False,0))}
        for key,mesh,mat in((10,100,1),(11,101,2),(12,102,2)):
            objects[key]=item('SkinnedMeshRenderer',key,dict(m_Mesh=pointer(mesh),m_Materials=[pointer(mat)]))
        objects[20]=item('LODGroup',20,dict(m_LODs=[dict(renderers=[dict(renderer=pointer(key))])for key in(10,11,12)]))
        plan=api.groom_lod_plan(SimpleNamespace(objects=objects))
        self.assertEqual(plan['meshes'],{100,101,102})
        self.assertEqual(plan['donors'],{101:100,102:100})
        self.assertEqual(plan['materials'],{2:1})
        with self.assertRaisesRegex(ValueError,'untextured native placeholder'):
            api.validate_groom_frame_coverage(SimpleNamespace(objects=objects))
        # A real textured accessory remains owned by its original material.
        objects[2]=item('Material',2,material(False,91))
        self.assertEqual(api.groom_lod_plan(SimpleNamespace(objects=objects))['meshes'],{100})
        self.assertEqual(api.groom_lod_plan(SimpleNamespace(objects=objects))['materials'],{})

    def test_groom_lod_placeholder_shared_outside_its_group_is_rejected(self):
        api=self.resource_module()
        pointer=lambda key:dict(m_FileID=0,m_PathID=key)
        def item(kind,key,tree):
            return SimpleNamespace(type=SimpleNamespace(name=kind),path_id=key,read_typetree=lambda:tree)
        materials={1:item('Material',1,dict(m_SavedProperties=dict(m_Floats=[('_CoreResponse',1.),('_SupportInVertexFrame',1.),('_FlowInVertexFrame',2.)],m_TexEnvs=[]))),2:item('Material',2,dict(m_SavedProperties=dict(m_Floats=[],m_TexEnvs=[])))}
        for key,mesh,mat in((10,100,1),(11,101,2),(12,999,2)):
            materials[key]=item('SkinnedMeshRenderer',key,dict(m_Mesh=pointer(mesh),m_Materials=[pointer(mat)]))
        materials[20]=item('LODGroup',20,dict(m_LODs=[dict(renderers=[dict(renderer=pointer(key))])for key in(10,11)]))
        with self.assertRaisesRegex(ValueError,'shared with another material responsibility'):
            api.groom_lod_plan(SimpleNamespace(objects=materials))

    def test_prefab_address_selects_the_game_object_among_script_subassets(self):
        api=self.resource_module();asset='Assets/Hero.prefab'
        prefab=SimpleNamespace(type=SimpleNamespace(name='GameObject'),path_id=17)
        script=SimpleNamespace(type=SimpleNamespace(name='MonoBehaviour'),path_id=29)
        owner=SimpleNamespace(objects={},container=SimpleNamespace(items=lambda:[(asset,script),(asset,prefab)]))
        environment=SimpleNamespace(file=SimpleNamespace(files={'owner':owner}))
        self.assertIs(api.typed_bundle_asset(environment,asset,'GameObject'),prefab)

    def test_same_typed_address_conflict_is_rejected(self):
        api=self.resource_module();asset='Assets/Hero.prefab'
        objects=[SimpleNamespace(type=SimpleNamespace(name='GameObject'),path_id=index)for index in (17,29)]
        owner=SimpleNamespace(objects={},container=SimpleNamespace(items=lambda:[(asset,obj)for obj in objects]))
        environment=SimpleNamespace(file=SimpleNamespace(files={'owner':owner}))
        with self.assertRaisesRegex(ValueError,'ambiguous'):api.typed_bundle_asset(environment,asset,'GameObject')

    def test_closure_resolves_prepared_owners_and_preserves_cyclic_references(self):
        api=self.resource_module();closure=api.ResourceClosure({'prepared':{},'shared':{}},{'native':'prepared'},rebase_archive=lambda *args:None)
        pointer=closure.pointer(SimpleNamespace(name='native',externals=[]),dict(m_FileID=0,m_PathID=17))
        self.assertEqual(pointer,('prepared',17))
        closure.scripts={'prepared':[],'shared':[]}
        closure.adjacency={('prepared',17):[('shared',29)],('shared',29):[('prepared',17)]}
        with mock.patch.object(closure,'load',lambda cab:(None,None)):
            nodes=closure.collect([pointer])
        self.assertEqual(nodes,{('prepared',17),('shared',29)})
        self.assertEqual(dict(closure.kept),{'prepared':{17},'shared':{29}})

    def test_sibling_prefab_and_controller_hold_the_same_serialized_file_lifetime(self):
        api=self.resource_module()
        prefab=dict(source='hero.prefab',bundles=[dict(file='part.bundle',name='part.bundle')])
        controller=dict(source='hero.controller',bundles=[dict(file=name,name=name)for name in ('part.bundle','base.bundle')])
        resolved=api.complete_resource_dependencies([prefab,controller],{'part.bundle':{'base.bundle'},'base.bundle':{'part.bundle'}})
        self.assertEqual([r['file']for r in resolved[0]['bundles']],['part.bundle','base.bundle'])
        self.assertEqual([r['file']for r in resolved[1]['bundles']],['part.bundle','base.bundle'])
        self.assertEqual(len(prefab['bundles']),1)

    def test_missing_serialized_bundle_dependency_is_rejected(self):
        api=self.resource_module()
        resources=[dict(source='hero.prefab',bundles=[dict(file='part.bundle',name='part.bundle')])]
        with self.assertRaisesRegex(ValueError,'dependency is missing'):
            api.complete_resource_dependencies(resources,{'part.bundle':{'missing.bundle'}})

    def test_shader_fallbacks_remain_in_the_typed_object_closure(self):
        api=self.resource_module();closure=api.ResourceClosure({'shader':{},'builtin':{}},rebase_archive=None)
        shader=SimpleNamespace(type=SimpleNamespace(name='Shader'),read_typetree=lambda:dict(m_Dependencies=[dict(m_FileID=1,m_PathID=29)]))
        owner=SimpleNamespace(name='shader',externals=[SimpleNamespace(name='builtin')],objects={17:shader})
        with mock.patch.object(closure,'load',lambda cab:(None,owner)):
            self.assertEqual(closure.neighbors(('shader',17)),[('builtin',29)])

    def test_face_lod_budget_reuses_exact_compressed_mip_tail(self):
        api=self.resource_module();payload=bytes(range(112))
        texture=dict(m_TextureFormat=25,m_ImageCount=1,m_TextureDimension=2,m_Width=8,m_Height=8,m_MipCount=4,m_CompleteImageSize=112,m_ColorSpace=0)
        reduced,data=api.texture_mip_tail(texture,payload,4)
        self.assertEqual((reduced['m_Width'],reduced['m_Height'],reduced['m_MipCount'],reduced['m_CompleteImageSize']),(4,4,3,48))
        self.assertEqual(data,payload[64:]);self.assertEqual(reduced['m_ColorSpace'],texture['m_ColorSpace'])
        self.assertEqual(texture['m_Width'],8)
        with self.assertRaises(ValueError):api.texture_mip_tail(texture,payload[:-1],4)
        with self.assertRaises(ValueError):api.texture_mip_tail(texture,payload,3)

    def test_exact_main_storage_preserves_sampler_palette_and_complete_mips(self):
        api=self.resource_module();payload=bytes(340)
        texture=dict(m_TextureFormat=25,m_ImageCount=1,m_TextureDimension=2,m_Width=8,m_Height=8,m_MipCount=4,m_CompleteImageSize=112,
            m_ColorSpace=0,m_TextureSettings=dict(m_FilterMode=1,m_Aniso=1,m_WrapU=0),m_StreamData=dict(path='archive:/cab/cab.resS',offset=16,size=112))
        original=copy.deepcopy(texture);result=api.texture_rgba32_storage(texture,payload,128)
        self.assertEqual(texture,original)
        self.assertEqual(result['m_TextureFormat'],4);self.assertEqual(result['m_CompleteImageSize'],340)
        self.assertEqual(result['m_StreamData'],dict(path='archive:/cab/cab.resS',offset=128,size=340))
        for key in texture.keys()-{'m_TextureFormat','m_CompleteImageSize','m_StreamData'}:self.assertEqual(result[key],texture[key])
        with self.assertRaises(ValueError):api.texture_rgba32_storage(texture,payload[:-1],128)
        with self.assertRaises(ValueError):api.texture_rgba32_storage(texture,payload,129)
        compressed=api.texture_main_storage(result,bytes(112),480,25)
        self.assertEqual(compressed['m_TextureFormat'],25)
        self.assertEqual(compressed['m_CompleteImageSize'],112)
        for key in texture.keys()-{'m_TextureFormat','m_CompleteImageSize','m_StreamData'}:self.assertEqual(compressed[key],texture[key])
        with self.assertRaises(ValueError):api.texture_main_storage(result,bytes(111),480,25)
        with self.assertRaises(ValueError):api.texture_main_storage(dict(result,m_CompleteImageSize=339),bytes(112),480,25)

    def test_complete_skin_mips_support_inline_and_legacy_normal_storage(self):
        api=self.resource_module()
        texture=dict(m_TextureFormat=12,m_ImageCount=1,m_TextureDimension=2,m_Width=4,m_Height=4,m_MipCount=3,
            m_CompleteImageSize=48,m_ColorSpace=0,m_IsReadable=False,
            m_TextureSettings=dict(m_FilterMode=2,m_Aniso=4,m_WrapU=0),
            m_StreamData=dict(path='',offset=0,size=0),**{'image data':bytes(48)})
        original=copy.deepcopy(texture);raw=bytes(84)
        actual=api.texture_rgba32_storage(texture,raw,0)
        self.assertEqual(texture,original)
        self.assertEqual(actual['image data'],raw)
        self.assertEqual(actual['m_StreamData'],texture['m_StreamData'])
        self.assertEqual(actual['m_TextureFormat'],4)
        for key in texture.keys()-{'m_TextureFormat','m_CompleteImageSize','image data'}:
            self.assertEqual(actual[key],texture[key])
        with self.assertRaises(ValueError):api.texture_rgba32_storage(texture,raw[:-1],0)

    def test_shared_normal_quantization_is_order_independent_and_rejects_real_conflicts(self):
        api=self.resource_module();a=bytes([0,20,31,255]);b=bytes([1,21,30,254])
        first,proof=api.reconcile_shared_normal_mips([a,b]);second,_=api.reconcile_shared_normal_mips([b,a])
        self.assertEqual(first,second);self.assertEqual(first,bytes([0,20,30,254]))
        self.assertEqual(proof['reconciledComponents'],4)
        with self.assertRaisesRegex(ValueError,'beyond a one-step'):
            api.reconcile_shared_normal_mips([a,bytes([0,22,31,255])])

    def test_fiber_storage_preserves_native_uv1_and_runtime_projection_uv3(self):
        import numpy as np
        api=self.resource_module();count=3;stride=88
        channels=[dict(stream=0,offset=0,format=0,dimension=0)for _ in range(14)]
        for index,offset,width in ((0,0,3),(1,12,3),(2,24,4),(4,40,4),(5,56,2),(6,64,4),(7,80,2)):
            channels[index].update(offset=offset,dimension=width)
        source=np.zeros((count,stride//4),dtype='<f4');source[:,:3]=[[0,0,0],[1,0,0],[0,1,0]]
        source[:,3:6]=[0,0,1];source[:,6:10]=[1,0,0,1]
        source[:,14:16]=[[.125,.25],[.375,.5],[.625,.75]]
        source[:,20:22]=[[1,0],[2,0],[3,0]]
        roles=np.array([[0,0,0,1],[1,0,0,1],[0,1,0,1]],dtype='<f4');source[:,16:20]=roles
        tree: dict[str,Any]=dict(m_VertexData=dict(m_VertexCount=count,m_Channels=channels,m_DataSize=source.tobytes()),m_BindPose=[])
        mesh=SimpleNamespace(m_Vertices=source[:,:3],m_UV2=roles,m_Normals=source[:,3:6],m_Tangents=source[:,6:10],
            src=SimpleNamespace(m_VertexData=SimpleNamespace(m_Channels=channels)),
            get_triangles=lambda:[[0,1,2]],get_streams=lambda *args:{0:SimpleNamespace(offset=0,stride=stride)})
        proof=api.bake_groom_vertex_flow(tree,mesh)
        self.assertEqual(proof['coordinateChannel'],'UV4.xy')
        result=tree['m_VertexData']['m_DataSize'];new_stride=stride+8
        original_uv1=np.ndarray((count,2),'<f4',buffer=result,offset=channels[5]['offset'],strides=(new_stride,4))
        projection=np.ndarray((count,2),'<f4',buffer=result,offset=channels[7]['offset'],strides=(new_stride,4))
        fiber=np.ndarray((count,2),'<f4',buffer=result,offset=channels[8]['offset'],strides=(new_stride,4))
        np.testing.assert_array_equal(original_uv1,source[:,14:16])
        np.testing.assert_array_equal(projection,source[:,20:22])
        np.testing.assert_array_equal(fiber,np.tile([0,1],(count,1)))
        with self.assertRaisesRegex(ValueError,'unowned UV4'):api.bake_groom_vertex_flow(tree,mesh)

    def test_unoriented_fiber_axis_does_not_depend_on_first_incident_triangle(self):
        import numpy as np
        api=self.resource_module();count=7;stride=88
        source=np.zeros((count,stride//4),dtype='<f4');source[:,3:6]=[0,0,1];source[:,6:10]=[1,0,0,1]
        roles=np.tile([0,0,0,1],(count,1)).astype('<f4');triangles=[]
        for index,degrees in enumerate((0,80,100)):
            angle=np.deg2rad(degrees);direction=np.array([np.cos(angle),np.sin(angle),0])
            a,b=2*index+1,2*index+2
            source[a,:3]=direction;source[b,:3]=[-direction[1],direction[0],0]
            roles[a,:2]=[0,1];roles[b,:2]=[1,0];triangles.append([0,a,b])
        source[:,16:20]=roles
        results=[]
        for order,hand in (([0,1,2],1),([2,1,0],1),([1,0,2],-1)):
            channels=[dict(stream=0,offset=0,format=0,dimension=0)for _ in range(14)]
            for index,offset,width in ((0,0,3),(1,12,3),(2,24,4),(4,40,4),(5,56,2),(6,64,4),(7,80,2)):
                channels[index].update(offset=offset,dimension=width)
            values=source.copy();values[:,9]=hand
            tree: dict[str,Any]=dict(m_VertexData=dict(m_VertexCount=count,m_Channels=channels,m_DataSize=values.tobytes()),m_BindPose=[])
            mesh=SimpleNamespace(m_Vertices=values[:,:3],m_UV2=roles,m_Normals=values[:,3:6],m_Tangents=values[:,6:10],
                src=SimpleNamespace(m_VertexData=SimpleNamespace(m_Channels=channels)),
                get_triangles=lambda:[[v for index in order for v in triangles[index]]],get_streams=lambda *args:{0:SimpleNamespace(offset=0,stride=stride)})
            api.bake_groom_vertex_flow(tree,mesh)
            axis=np.ndarray((count,2),'<f4',buffer=tree['m_VertexData']['m_DataSize'],offset=channels[8]['offset'],strides=(stride+8,4))[0]
            results.append(np.array([axis[0],axis[1]*hand]))
        for axis in results:
            self.assertAlmostEqual(abs(float(axis@np.array([0,1]))),1.,places=6)

    def test_gpu_frame_bridge_preserves_all_other_vertex_bytes_and_bounds_quantization(self):
        import numpy as np
        api=self.resource_module();count=4;stride=64
        channels=[dict(stream=0,offset=0,format=0,dimension=0)for _ in range(14)]
        channels[4].update(offset=32,dimension=4);channels[8].update(offset=48,dimension=2)
        raw=bytearray(bytes(range(64))*count)
        source=np.ndarray((count,4),'<f4',buffer=raw,offset=32,strides=(stride,4));source[:]=[[.1,.2,0,0],[.2,.3,.5,-.5],[.3,.4,-1,0],[.4,.5,.25,.75]]
        flow=np.ndarray((count,2),'<f4',buffer=raw,offset=48,strides=(stride,4));flow[:]=[[0,1],[1,0],[-.6,.8],[.6,-.8]]
        native=bytes(raw);carry=source[:,2:4].copy();axes=flow.copy()
        tree: dict[str,Any]=dict(m_VertexData=dict(m_VertexCount=count,m_Channels=channels,m_DataSize=native))
        mesh=SimpleNamespace(src=SimpleNamespace(m_VertexData=SimpleNamespace(m_Channels=channels)),get_streams=lambda *args:{0:SimpleNamespace(offset=0,stride=stride)})
        proof=api.pack_groom_gpu_frame(tree,mesh)
        saved=tree['m_VertexData']['m_DataSize'];allowed=np.zeros(len(saved),bool)
        for row in range(count):allowed[row*stride+40:row*stride+48]=True
        np.testing.assert_array_equal(np.frombuffer(saved,np.uint8)[~allowed],np.frombuffer(native,np.uint8)[~allowed])
        words=np.ndarray((count,4),'<f4',buffer=saved,offset=32,strides=(stride,4))[:,2:4]
        def decode(word):
            high=np.floor(word.astype(float)/4096)
            return np.c_[word-high*4096,high]*(2/4095)-1
        np.testing.assert_allclose(decode(words[:,0]),carry,rtol=0,atol=1/4095+1e-7)
        np.testing.assert_allclose(decode(words[:,1]),axes,rtol=0,atol=1/4095+1e-7)
        self.assertEqual(proof['format'],2)
        api.pack_groom_gpu_frame(tree,mesh,source_format=2);self.assertEqual(tree['m_VertexData']['m_DataSize'],saved)
        with self.assertRaisesRegex(ValueError,'Unknown source'):api.pack_groom_gpu_frame(tree,mesh,source_format=3)

    def test_authored_curves_guide_support_flow_independently_of_support_uv_rotation(self):
        import numpy as np
        api=self.resource_module();count=6;stride=88
        channels=[dict(stream=0,offset=0,format=0,dimension=0)for _ in range(14)]
        for index,offset,width in ((0,0,3),(1,12,3),(2,24,4),(4,40,4),(5,56,2),(6,64,4),(7,80,2)):
            channels[index].update(offset=offset,dimension=width)
        source=np.zeros((count,stride//4),dtype='<f4')
        source[:3,:3]=[[0,0,0],[1,0,0],[0,1,0]];source[3:,:3]=source[:3,:3]+[0,0,.01]
        source[:,3:6]=[0,0,1];source[:,6:10]=[1,0,0,1]
        roles=np.array([[0,0,0,1],[0,1,0,1],[1,0,0,1],
                        [0,0,1,1],[1,0,1,1],[0,1,1,1]],dtype='<f4')
        source[:,16:20]=roles
        tree: dict[str,Any]=dict(m_VertexData=dict(m_VertexCount=count,m_Channels=channels,m_DataSize=source.tobytes()),m_BindPose=[])
        mesh=SimpleNamespace(m_Vertices=source[:,:3],m_UV2=roles,m_Normals=source[:,3:6],m_Tangents=source[:,6:10],
            src=SimpleNamespace(m_VertexData=SimpleNamespace(m_Channels=channels)),
            get_triangles=lambda:[[0,1,2,3,4,5]],get_streams=lambda *args:{0:SimpleNamespace(offset=0,stride=stride)})
        proof=api.bake_groom_vertex_flow(tree,mesh)
        axes=np.ndarray((count,2),'<f4',buffer=tree['m_VertexData']['m_DataSize'],offset=channels[8]['offset'],strides=(stride+8,4))
        np.testing.assert_allclose(np.abs(axes),np.tile([0,1],(count,1)),rtol=0,atol=1e-7)
        self.assertEqual(proof['curveGuidedSupportGroups'],3)

    def test_owned_skin_reload_rejects_sampler_and_unselected_texture_changes(self):
        api=self.resource_module()
        with tempfile.TemporaryDirectory()as folder:
            source=Path(folder)/'source.bundle';target=Path(folder)/'target.bundle';raw=Path(folder)/'normal.bc7'
            source.write_bytes(b'source');target.write_bytes(b'target');raw.write_bytes(bytes(16))
            tree=dict(m_Name='Normal',m_Width=4,m_Height=4,m_MipCount=1,m_TextureFormat=4,
                m_CompleteImageSize=64,m_ColorSpace=0,m_TextureSettings=dict(filter=2),
                m_StreamData=dict(path='skin.resS',offset=0,size=64))
            new=dict(tree,m_TextureFormat=25,m_CompleteImageSize=16,m_StreamData=dict(path='skin.resS',offset=0,size=16))
            class Texture:
                type=SimpleNamespace(name='Texture2D')
                def __init__(self,metadata,payload):self.metadata=metadata;self.payload=payload
                def read_typetree(self):return self.metadata
                def read(self):return SimpleNamespace(get_image_data=lambda:self.payload)
            first=SimpleNamespace(name='CAB',externals=[],objects={1:Texture(tree,bytes(64)),2:Texture(tree,b'unchanged')})
            second=SimpleNamespace(name='CAB',externals=[],objects={1:Texture(new,raw.read_bytes()),2:Texture(tree,b'unchanged')})
            pin=lambda path:dict(path=str(path),sha256=api.digest(path))
            receipt=dict(source=pin(source),target=pin(target),beforeSha256=api.digest(source),sha256=api.digest(target),textures=[
                dict(object=1,name='Normal',width=4,height=4,mipCount=1,beforeFormat=4,format=25,bytes=16,raw=pin(raw),
                    sourcePayloadSha256=hashlib.sha256(bytes(64)).hexdigest(),payloadSha256=api.digest(raw))])
            with mock.patch.object(api,'load_serialized_bundle',side_effect=lambda path,**kwargs:(None,first if Path(path)==source else second)):
                api.validate_owned_texture_mips(receipt)
                new['m_ColorSpace']=1
                with self.assertRaisesRegex(ValueError,'colour space'):api.validate_owned_texture_mips(receipt)
                new['m_ColorSpace']=0
                second.objects[2].payload=b'changed'
                with self.assertRaisesRegex(ValueError,'unselected texels'):api.validate_owned_texture_mips(receipt)

    def test_equal_material_names_do_not_confer_a_groom_role(self):
        api=self.resource_module()
        def material(values):return SimpleNamespace(type=SimpleNamespace(name='Material'),peek_name=lambda:'M_HairShape_S',
            read_typetree=lambda:dict(m_SavedProperties=dict(m_Floats=values)))
        file=SimpleNamespace(objects={11:material([('_CoreResponse',1.)]),12:material([('_Glossiness',.5)])})
        self.assertEqual(api.groom_material_identifiers(file),{11})

    def test_skin_palette_extension_uses_the_renderers_own_rig(self):
        api=self.resource_module();pointer=lambda value:dict(m_FileID=0,m_PathID=value)
        objects={}
        def game(identifier,name):
            objects[identifier]=SimpleNamespace(path_id=identifier,type=SimpleNamespace(name='GameObject'),peek_name=lambda:name)
        def transform(identifier,game_id,parent,children):
            tree=dict(m_GameObject=pointer(game_id),m_Father=pointer(parent),m_Children=[pointer(i)for i in children])
            objects[identifier]=SimpleNamespace(path_id=identifier,type=SimpleNamespace(name='Transform'),read_typetree=lambda:tree)
        for offset in (0,10):
            game(offset+1,'Bip001');game(offset+3,'Pelvis');game(offset+5,'Head')
            transform(offset+2,offset+1,0,[offset+4]);transform(offset+4,offset+3,offset+2,[offset+6]);transform(offset+6,offset+5,offset+4,[])
        file=SimpleNamespace(name='CAB-own-rig',objects=objects)
        renderer=dict(m_Bones=[pointer(6)],m_Mesh=pointer(22),m_Name='Face')
        source=['Root/Bip001/Pelvis/Head'];target=[*source,'Root/Bip001/Pelvis']
        result,proof=api.extend_skin_bone_palette(file,renderer,source,target)
        self.assertEqual(result['m_Bones'],[pointer(6),pointer(4)])
        self.assertEqual(renderer['m_Bones'],[pointer(6)])
        self.assertTrue(proof['nativeBonePrefixExact'])
        repeated=dict(renderer,m_Bones=[pointer(6),pointer(6)])
        slots=[source[0],source[0]]
        extended,alias_proof=api.extend_skin_bone_palette(file,repeated,slots,[*slots,target[-1],source[0]])
        self.assertEqual(extended['m_Bones'],[pointer(6),pointer(6),pointer(4),pointer(6)])
        self.assertTrue(alias_proof['nativeBonePrefixExact'])
        with self.assertRaisesRegex(ValueError,'native bone prefix'):
            api.extend_skin_bone_palette(file,renderer,source,list(reversed(target)))

    def test_crunch_mips_decode_their_own_declared_level(self):
        from PIL import Image
        api=self.resource_module()
        texture=SimpleNamespace(m_TextureFormat=29,m_Width=4,m_Height=4,m_MipCount=3,get_image_data=lambda:b'crunched')
        obj=SimpleNamespace(read=lambda:texture,version=(2019,4,37,1),platform=19)
        with mock.patch('texture2ddecoder.unpack_unity_crunch',side_effect=lambda data,level:bytes([level])*16)as unpack, \
             mock.patch('UnityPy.export.Texture2DConverter.parse_image_data',side_effect=lambda data,w,h,*args,**kwargs:Image.new('RGBA',(w,h),(data[0],2,3,255))):
            images=api.texture_mip_images(obj)
        self.assertEqual(unpack.call_args_list,[mock.call(b'crunched',level)for level in range(3)])
        self.assertEqual([image.shape[:2]for image in images],[(4,4),(2,2),(1,1)])
        self.assertEqual([int(image[0,0,0])for image in images],[0,1,2])

    def test_groom_precision_preserves_native_fitting_records(self):
        api=self.resource_module()
        record=lambda i,v:dict(index=i,vertex=dict(zip('xyz',v)),normal=dict(x=.001234,y=0.,z=0.),tangent=dict(x=0.,y=.004321,z=0.))
        tree: dict[str,Any]=dict(m_VertexData=dict(m_VertexCount=3),m_LocalAABB=dict(m_Extent=dict(x=1.,y=1.,z=1.)),m_Shapes=dict(
            vertices=[record(0,[0.,0.,0.]),record(1,[.000012345,0.,0.])],shapes=[dict(firstVertex=0,vertexCount=2)]))
        original=copy.deepcopy(tree['m_Shapes']['vertices'][0])
        proof=api.quantize_generated_morphs(tree,1,[[0.,0.,0.],[1.,0.,0.],[0.,1.,0.]],[[0,1,2]])
        self.assertEqual(tree['m_Shapes']['vertices'][0],original)
        self.assertLessEqual(proof['maximumPositionComponentErrorMetres'],2**-23)
        self.assertLessEqual(proof['maximumDirectionComponentError'],2**-13)
        self.assertEqual(proof['changedRecords'],1)

    def test_groom_precision_retains_full_precision_for_thin_fitting_triangles(self):
        api=self.resource_module();row=dict(index=2,vertex=dict(x=0.,y=-1.01e-6,z=0.),normal=dict(x=0.,y=0.,z=0.),tangent=dict(x=0.,y=0.,z=0.))
        tree: dict[str,Any]=dict(m_VertexData=dict(m_VertexCount=3),m_Shapes=dict(vertices=[copy.deepcopy(row)],shapes=[dict(firstVertex=0,vertexCount=1)]))
        proof=api.quantize_generated_morphs(tree,1,[[0.,0.,0.],[1e-6,0.,0.],[0.,1e-6,0.]],[[0,1,2]])
        self.assertEqual(tree['m_Shapes']['vertices'][0],row)
        self.assertEqual(proof['fullPrecisionThinPoseRecords'],1)
        self.assertEqual(proof['changedRecords'],0)

    @staticmethod
    def groom_shading_fixture(fine=False) -> tuple[dict[str,Any],Any,Any]:
        import numpy as np
        positions=np.asarray([[0,0,0],[1,0,0],[0,1,0],[0,0,0],[1,0,0],[0,-1,0]],dtype=np.float32)
        normals=np.asarray([[.2,0,1]]*3+[[-.2,0,1]]*3,dtype=np.float32)
        if fine:normals[:]=[0,0,1]
        tangents=np.tile([1,0,0,1],(6,1)).astype(np.float32)
        deform=np.column_stack((positions,normals,tangents)).astype('<f4').tobytes()
        native=np.arange(48,dtype='<f4').reshape(6,8)/64
        native[:,4]=17 if fine else 0
        channels=[dict(stream=0,offset=0,format=0,dimension=0)for _ in range(14)]
        for slot,stream,offset,width in ((0,0,0,3),(1,0,12,3),(2,0,24,4),(4,1,0,2),(6,1,8,4),(7,1,24,2)):
            channels[slot]=dict(stream=stream,offset=offset,format=0,dimension=width)
        raw=deform+native.tobytes()
        tree: dict[str,Any]=dict(m_VertexData=dict(m_VertexCount=6,m_Channels=channels,m_DataSize=raw),m_BindPose=[],
                  m_Shapes=dict(retained='fitting records'),m_LocalAABB=dict(retained='bounds'))
        mesh=SimpleNamespace(m_Vertices=positions,m_Normals=normals,m_Tangents=tangents,m_UV2=native[:,2:6],
            src=SimpleNamespace(m_VertexData=SimpleNamespace(m_Channels=copy.deepcopy(channels))),
            get_triangles=lambda:[[[0,1,2],[3,4,5]]],
            get_streams=lambda *_:[SimpleNamespace(offset=0,stride=40),SimpleNamespace(offset=240,stride=32)])
        return tree,mesh,native

    def test_groom_shading_preserves_native_uvs_fitting_and_vertex_values(self):
        import numpy as np
        api=self.resource_module();tree,mesh,native=self.groom_shading_fixture();before=copy.deepcopy(tree)
        proof=api.smooth_groom_support(tree,mesh)
        self.assertEqual((proof['normalCarryUv'],proof['normalCarryComponents']),(0,'zw'))
        self.assertGreater(proof['smoothedVertices'],0)
        self.assertEqual(tree['m_VertexData']['m_Channels'][4]['dimension'],4)
        self.assertEqual(tree['m_VertexData']['m_DataSize'][:240],before['m_VertexData']['m_DataSize'][:240])
        actual=np.frombuffer(tree['m_VertexData']['m_DataSize'],dtype='<f4',offset=240).reshape(6,10)
        self.assertTrue(np.array_equal(np.column_stack((actual[:,:2],actual[:,4:])),native))
        self.assertTrue(np.any(actual[:,2:4]!=0))
        for key in ('m_Shapes','m_BindPose','m_LocalAABB'):self.assertEqual(tree[key],before[key])
        self.assertEqual(tree['m_VertexData']['m_Channels'][8]['dimension'],0)
        shader=(_PATH.parent.parent/'blender-models/CharacterHair/SourceGroomProgram.cginc').read_text('utf-8-sig')
        self.assertIn('SourceGroomUnpackSignedPair(v.texcoord.z) : v.texcoord.zw;',shader)

    def test_groom_without_core_smoothing_still_declares_zero_carry(self):
        import numpy as np
        tree,mesh,native=self.groom_shading_fixture(fine=True)
        proof=self.resource_module().smooth_groom_support(tree,mesh)
        self.assertEqual(proof['smoothedVertices'],0)
        actual=np.frombuffer(tree['m_VertexData']['m_DataSize'],dtype='<f4',offset=240).reshape(6,10)
        self.assertTrue(np.array_equal(actual[:,2:4],np.zeros((6,2))))
        self.assertTrue(np.array_equal(np.column_stack((actual[:,:2],actual[:,4:])),native))

    def test_groom_carry_rejects_an_already_owned_texture_coordinate(self):
        tree,mesh,_=self.groom_shading_fixture()
        tree['m_VertexData']['m_Channels'][4]['dimension']=4
        with self.assertRaisesRegex(ValueError,'unowned'):
            self.resource_module().smooth_groom_support(tree,mesh)


class CharacterResourcePackContracts(unittest.TestCase):
    @staticmethod
    def fixture(root):
        source=root/'Catalog';source.mkdir();rows=[]
        for index,label in enumerate(('a','b','c')):
            path=source/f'human_female_faceshape_{label}_parts.bundle';path.write_bytes(bytes([index])*400)
            rows.append(dict(file=path.name,sha256=hashlib.sha256(path.read_bytes()).hexdigest()))
        manifest=source/'CharacterModels.manifest.json'
        manifest.write_text(json.dumps(dict(schemaVersion=3,bundles=rows,meshes=[],materials=[],textureRemaps=[])),encoding='utf8')
        return manifest,root/'Packs'

    def test_resource_packs_bound_the_archive_size_and_preserve_each_declared_payload(self):
        import zipfile
        with tempfile.TemporaryDirectory()as temporary:
            root=Path(temporary);manifest,output=self.fixture(root)
            with mock.patch.object(publisher,'ROOT',root):
                result=publisher.create_resource_packs(manifest,output,'2026.11.07.1',1024)
                self.assertTrue(all(pack['bytes']<=1024 for pack in result['packs']))
                self.assertEqual(publisher.create_resource_packs(manifest,output,'2026.11.07.1',1024),result)
            unpacked={}
            for pack in result['packs']:
                with zipfile.ZipFile(output/pack['file'])as archive:
                    for name in archive.namelist():unpacked[name]=hashlib.sha256(archive.read(name)).hexdigest()
            self.assertEqual(unpacked,{row['file']:row['sha256']for row in result['files']})

    def test_resource_pack_reuse_rejects_a_corrupted_archive(self):
        with tempfile.TemporaryDirectory()as temporary:
            root=Path(temporary);manifest,output=self.fixture(root)
            with mock.patch.object(publisher,'ROOT',root):
                result=publisher.create_resource_packs(manifest,output,'2026.11.07.1',1024)
                (output/result['packs'][0]['file']).write_bytes(b'Corrupted fixture')
                with self.assertRaisesRegex(ValueError,'pack changed'):
                    publisher.create_resource_packs(manifest,output,'2026.11.07.1',1024)


class CharacterGroomContracts(unittest.TestCase):
    def test_fiber_reflection_is_normalized_and_reciprocal_across_roughness(self):
        owner=Path(__file__).resolve().parents[2]/'blender-models/CharacterHair/validate_material.py'
        spec=importlib.util.spec_from_file_location('groom_material_validation',owner)
        assert spec is not None and spec.loader is not None
        module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)
        for smoothness in (0.,.1,.25,.42,.55,.7,.85,.95):
            with self.subTest(smoothness=smoothness):
                proof=module.check_fiber_distribution(smoothness)
                self.assertLess(abs(proof['fiberProjectedArea']-1),.0002)
                self.assertLess(proof['maximumReciprocityError'],1e-8)

    def test_shared_groom_atlas_has_separate_interior_strands_and_neutral_dye(self):
        import numpy as np
        from PIL import Image
        owner=Path(__file__).resolve().parents[2]/'blender-models/CharacterHair/GenerateFlowCoverageSweptProfile.py'
        spec=importlib.util.spec_from_file_location('groom_atlas_owner',owner)
        assert spec is not None and spec.loader is not None
        groom=importlib.util.module_from_spec(spec);spec.loader.exec_module(groom)
        with tempfile.TemporaryDirectory() as temporary:
            output=Path(temporary)
            groom.generate_strand_atlas(output)
            pixels=np.asarray(Image.open(output/'FineGroomMask.png'))
            np.testing.assert_array_equal(pixels[:,:,0],pixels[:,:,1])
            np.testing.assert_array_equal(pixels[:,:,1],pixels[:,:,2])
            self.assertGreaterEqual(int(pixels[:,:,:3].min()),int(.6*255))
            for y in range(4):
                for x in range(4):
                    tile=pixels[y*256:(y+1)*256,x*128:(x+1)*128,3]>=.35*255
                    centre=tile[128].astype(int)
                    separate_runs=int(np.count_nonzero(np.diff(np.r_[0,centre,0])==1))
                    self.assertGreaterEqual(separate_runs,12)
                    self.assertGreater(float(tile[85:170].mean()),.25)
                    self.assertLess(float(tile[85:170].mean()),.65)
                    self.assertFalse(np.any(tile[:,0]) or np.any(tile[:,-1]))
                    pigment=pixels[y*256:(y+1)*256,x*128:(x+1)*128,0][tile]/255.
                    self.assertGreater(float(np.std(pigment)),.05)
                    # Achromatic modulation preserves the native palette hue.
                    for dye in ([.03,.01,.02],[.8,.65,.4],[.9,.9,.9]):
                        dyed=pigment[:,None]*np.asarray(dye)
                        np.testing.assert_allclose(dyed/np.sum(dyed,axis=1)[:,None],
                            np.broadcast_to(np.asarray(dye)/sum(dye),dyed.shape),rtol=1e-6)
            recipe=json.loads((output/'MeshRecipe.json').read_text('utf-8'))
            for texture in recipe['textures']:
                self.assertTrue(texture['mipmaps'])
                self.assertFalse(texture['sRGB'])
                self.assertEqual(texture['sha256'],hashlib.sha256((output/texture['path']).read_bytes()).hexdigest())

    def test_face_contact_strength_preserves_uv_aliases_and_source_proportions(self):
        import numpy as np
        owner,_=publisher.source_owner()
        packet=dict(vertices=[0,0,0,1,0,0,1,0,0,2,0,0,3,0,0],
            weights=[1.,0,0,0]*5,boneIndices=[0,0,0,0,1,0,0,0,0,0,0,0,3,0,0,0,2,0,0,0])
        strength=owner.face_surface_strength(packet,dict(bones=['Bip001 Head','b_eyelid_upper_l','b_lip_upper_l','b_cheek_l']))
        np.testing.assert_allclose(strength,[.7,0.,0.,.7,0.],rtol=0,atol=1e-12)
        native=owner.face_surface_strength(dict(packet,boneIndices=[0]*20),dict(bones=['Bip001 Head']))
        np.testing.assert_allclose(native,np.full(5,.7),rtol=0,atol=1e-12)

    def test_combined_body_does_not_turn_into_an_eye_only_source(self):
        owner,_=publisher.source_owner()
        skin=dict(mesh='CombinedBody_LOD0',path='Root/CombinedBody_LOD0',bones=['Bip001 Head','b_eyelid_upper_l'],
            materials=[dict(shader='Lux/Human/Skin'),dict(shader='Custom/CharacterEye')])
        self.assertEqual(owner.role(skin),'skin')
        self.assertEqual(owner.role(dict(skin,mesh='Eyes_LOD0',materials=[dict(shader='Custom/CharacterEye')])),'native')

    def test_face_finishing_bounds_one_strength_across_stored_poses_and_keeps_the_body(self):
        import numpy as np
        owner,_=publisher.source_owner()
        native: dict[str,Any]=dict(name='Face',nativeReadable=True,vertexCount=4,indexFormat=0,
            vertices=[0.,0.,0.,1.,0.,0.,0.,1.,0.,3.,3.,3.],normals=[0.,0.,1.]*4,tangents=[1.,0.,0.,1.]*4,
            colors=[],bindPoses=np.eye(4).ravel().tolist(),weights=[1.,0.,0.,0.]*4,boneIndices=[0]*16,
            subMeshes=[[0,1,2]],uv=[],bounds=[1.5,1.5,1.5,3.,3.,3.],shapes=[dict(index=0,name='Expression',frames=[dict(weight=100.,
                vertices=[0.,0.,.1]*4,normals=[0.]*12,tangents=[0.]*12)])])
        native['sourceFingerprint']=publisher.fingerprint(native)[0]
        authored=copy.deepcopy(native);authored['vertices'][3]=-2.;authored['vertices'][9:]=[4.,5.,6.]
        authored['shapes'].append(dict(index=1,name='Muscular',frames=[dict(weight=100.,vertices=[0.]*9+[0.,0.,1.],normals=[0.]*12,tangents=[0.]*12)]))
        authored['sourceFingerprint']=publisher.fingerprint(authored)[0]
        result,proof=owner.retain_source_proportions(native,authored,dict(bones=['Bip001 Head']),[0.,.7,.7,1.])
        self.assertLess(proof['commonStoredPoseStrengthLimit'],.5)
        self.assertEqual(result['vertices'][:3],native['vertices'][:3]);self.assertEqual(result['vertices'][9:],authored['vertices'][9:])
        self.assertEqual(proof['inheritedBodyFrames'],1)
        self.assertEqual(result['shapes'][1]['frames'][0]['vertices'],authored['shapes'][1]['frames'][0]['vertices'])
        for delta in (np.zeros((4,3)),np.asarray(result['shapes'][0]['frames'][0]['vertices']).reshape(4,3)):
            posed=np.asarray(result['vertices']).reshape(4,3)+delta
            self.assertGreater(np.cross(posed[1]-posed[0],posed[2]-posed[0])[2],0)

    def test_face_ownership_follows_the_native_head_hierarchy_through_the_jaw(self):
        import numpy as np
        owner,_=publisher.source_owner()
        packet=dict(vertices=[0.,0.,0.,1.,0.,0.,2.,0.,0.],weights=[1.,0.,0.,0.]*3,
            boneIndices=[0]*4+[1]*4+[2]*4)
        skin=dict(bones=['Root/Bip001 Neck/Bip001 Head/Bip001 Jaw','Root/Bip001 Spine1',
            'Root/Bip001 Neck/Bip001 Head/EarTip'])
        np.testing.assert_allclose(owner.face_surface_strength(packet,skin),[.7,1.,.7],rtol=0,atol=1e-12)

    def test_core_subdivision_requires_every_serialized_morph_pose(self):
        import numpy as np
        owner=Path(__file__).resolve().parents[2]/'blender-models/CharacterHair/GenerateFlowCoverageSweptProfile.py'
        spec=importlib.util.spec_from_file_location('groom_partition_owner',owner)
        assert spec is not None and spec.loader is not None
        groom=importlib.util.module_from_spec(spec);spec.loader.exec_module(groom)
        rest=np.asarray([[0.,0.,1.],[.01,0.,1.],[0.,.01,1.]])
        adjacent=float(np.nextafter(np.float32(1.),np.float32(2.)))
        narrow=np.asarray([[1.,1.,1.],[adjacent,1.,1.],[1.,adjacent,1.]])
        self.assertTrue(groom.serializable_core_faces(rest[None],np.eye(3),[[0,1,2]])[0])
        self.assertFalse(groom.serializable_core_faces(np.stack((rest,narrow)),np.eye(3),[[0,1,2]])[0])
        self.assertGreater(np.linalg.norm(np.cross(narrow[1]-narrow[0],narrow[2]-narrow[0])),0.)

    def test_curved_capacity_preserves_source_support_and_uint16_limit(self):
        import ast
        path=_BUILD_PATH.parent.parent/'CharacterHair/GenerateFlowCoverageSweptProfile.py'
        nodes: list[ast.stmt]=[node for node in ast.parse(path.read_text('utf-8-sig')).body
            if isinstance(node,ast.FunctionDef)and node.name=='curved_vertex_capacity']
        scope: dict[str, Any]={}
        exec(compile(ast.Module(body=nodes,type_ignores=[]),str(path),'exec'),scope)
        capacity=scope['curved_vertex_capacity']
        self.assertEqual(capacity(1426,51845,49998,50000),(421,51847,2))
        self.assertEqual(capacity(60000,65000,4000,10000),(1000,65535,535))
        with self.assertRaises(AssertionError):
            capacity(100,120,30,50)
        with self.assertRaises(AssertionError):
            capacity(100,200,100,99)

    def test_curved_support_reserves_whole_fine_sheets(self):
        import ast
        path=_BUILD_PATH.parent.parent/'CharacterHair/GenerateFlowCoverageSweptProfile.py'
        nodes: list[ast.stmt]=[node for node in ast.parse(path.read_text('utf-8-sig')).body
            if isinstance(node,ast.FunctionDef)and node.name=='curved_core_budget']
        scope: dict[str, Any]={}
        exec(compile(ast.Module(body=nodes,type_ignores=[]),str(path),'exec'),scope)
        allocate=scope['curved_core_budget']
        self.assertEqual(allocate(4,[80,10,10],.18),(14,[1]))
        self.assertEqual(allocate(0,[100],.18),(0,[]))
        self.assertEqual(allocate(6,[],.18),(6,[]))
        budget, donated=allocate(0,[8]*100,.18)
        self.assertEqual(budget,144)
        self.assertEqual(len(donated),18)
        with self.assertRaises(AssertionError):
            allocate(0,[0],.18)

    def test_fiber_morphs_follow_the_declared_triangle_frame(self):
        import numpy as np
        path=_BUILD_PATH.parent.parent/'CharacterHair/GenerateFlowCoverageSweptProfile.py'
        spec=importlib.util.spec_from_file_location('fiber_surface_frames',path)
        assert spec and spec.loader
        groom=importlib.util.module_from_spec(spec);spec.loader.exec_module(groom)
        source=np.tile([[0.,0.,0.],[1.,0.,0.],[0.,1.,0.]],(2,1,1))
        normal=np.tile([0.,0.,1.],(2,3,1));normal[1,1:]=[0.,-.8,.6]
        tangent=np.tile([1.,0.,0.],(2,3,1));bitangent=np.cross(normal,tangent)
        record=dict(anchor=0,offset=[1/3,1/3,.1],normal=[0.,0.,1.],tangent=[1.,0.,0.],
                    surface=dict(triangle=[0,1,2],barycentric=[1/3]*3))
        points,normals,_=groom.source_fiber_frames([record],source,normal,tangent,bitangent)
        expected=groom.unit(normal[1].sum(axis=0))
        np.testing.assert_allclose(points[0,0],[1/3,1/3,.1],atol=1e-8)
        np.testing.assert_allclose(points[1,0],np.array([1/3,1/3,0.])+.1*expected,atol=1e-8)
        np.testing.assert_allclose(normals[1,0],expected,atol=1e-8)
        self.assertGreater(np.linalg.norm(points[1,0]-points[0,0]),.01)

    def test_curvature_bound_uses_each_physical_parent_inside_a_uv_component(self):
        import numpy as np
        path=_BUILD_PATH.parent.parent/'CharacterHair/GenerateFlowCoverageSweptProfile.py'
        spec=importlib.util.spec_from_file_location('physical_parent_groom',path)
        assert spec and spec.loader
        groom=importlib.util.module_from_spec(spec);spec.loader.exec_module(groom)
        source=np.array([[0.,0.,0.],[1.,0.,0.],[0.,1.,0.],[2.,0.,0.],[3.,0.,0.],[2.,1.,0.]])
        normal=np.tile([0.,0.,1.],(1,6,1));tangent=np.tile([1.,0.,0.],(1,6,1));bitangent=np.tile([0.,1.,0.],(1,6,1))
        vertices: list[dict[str, Any]]=[dict(nativeVertex=i)for i in range(6)]
        for face,triangle in enumerate(([0,1,2],[3,4,5])):
            vertices.append(dict(slot=0,face=face,triangle=triangle,barycentric=[1/3]*3,component=0,curvatureGroup=face))
        plan=dict(vertices=vertices,components=[dict(slot=0,faces=[0,1],curvatureGroup=0)],curvatureGroupCount=2,
                  curvatureGroupsBySlot=[[0,1]],triangles=[[[6,0,1],[6,1,2],[6,2,0],[7,3,4],[7,4,5],[7,5,3]]],
                  parents=[[0,0,0,1,1,1]],sourceTrianglesBySlot=[[[0,1,2],[3,4,5]]])
        points=np.array([[1/3,1/3,0.],[4.,2.,0.]])
        _,_,_,proof=groom.bound_curved_core(plan,points,np.tile([0.,0.,1.],(2,1)),np.tile([1.,0.,0.],(2,1)),source[None],normal,tangent,bitangent,np.eye(3))
        self.assertEqual(proof['finalFailedSamples'],0)
        self.assertEqual(proof['reducedCharts'],1)
        self.assertGreater(proof['minimumSerializedOrientation'],0.)

    def test_subresolution_core_does_not_require_a_redundant_centroid(self):
        import numpy as np
        path=_BUILD_PATH.parent.parent/'CharacterHair/GenerateFlowCoverageSweptProfile.py'
        spec=importlib.util.spec_from_file_location('subresolution_groom',path)
        assert spec and spec.loader
        groom=importlib.util.module_from_spec(spec);spec.loader.exec_module(groom)
        coordinates=np.array([[0.,0.],[.001,0.],[0.,.001]])
        plan=groom.plan_curved_core(coordinates,[[[0,1,2]]],[[True]],.01,3,subdivide_small_faces=False)
        self.assertEqual(plan['allocatedVertices'],3)
        self.assertEqual(len(plan['triangles'][0]),1)
        self.assertEqual(plan['parents'],[[0]])
        self.assertLessEqual(plan['maximumSourceAreaError'],1e-9)
        shared=groom.plan_curved_core(coordinates,[[[0,1,2]]],[[True]],.0002,1,reusable_vertices=range(3))
        self.assertEqual(shared['allocatedVertices'],1)
        self.assertEqual(shared['reusedNativeVertices'],3)
        self.assertEqual({r['nativeVertex']for r in shared['vertices']if 'nativeVertex'in r},{0,1,2})
        self.assertLessEqual(shared['maximumSourceAreaError'],1e-9)

    def test_standalone_uv_aliases_share_skin_and_cloth_anchor(self):
        import numpy as np
        owner=Path(__file__).resolve().parents[2]/'blender-models/CharacterHair/GenerateFlowCoverageSweptProfile.py'
        spec=importlib.util.spec_from_file_location('groom_attachment_owner',owner)
        assert spec is not None and spec.loader is not None
        groom=importlib.util.module_from_spec(spec);spec.loader.exec_module(groom)
        native: dict[str, Any]=dict(vertexCount=3,vertices=[0.,0.,0.,1.,0.,0.,0.,1.,0.],
            normals=[0.,0.,1.]*3,tangents=[1.,0.,0.,1.]*3,
            weights=[1.,0.,0.,0.]*3,boneIndices=[0,0,0,0,1,0,0,0,0,0,0,0],shapes=[])
        packet=dict(native,indexFormat=0,vertexCount=5,colors=[],bindPoses=np.tile(np.eye(4).ravel(),2).tolist(),
            vertices=native['vertices']+[.5,0.,.1]*2,normals=[0.,0.,1.]*5,tangents=[1.,0.,0.,1.]*5,
            weights=[1.,0.,0.,0.]*5,boneIndices=native['boneIndices']+[0,0,0,0,1,0,0,0],subMeshes=[[0,1,2]],uv=[])
        vertices: list[dict[str, Any]]=[dict(anchor=i,native=True,offset=[0.,0.,0.],normal=[0.,0.,1.],tangent=[1.,0.,0.])for i in range(3)]
        vertices.extend(dict(anchor=i,native=False,geometrySurface=dict(triangle=[0,1,2],barycentric=[.5,.5,0.]))for i in (0,1))
        binding=dict(schemaVersion=4,sourceVertexCount=3,matrix=np.eye(4).ravel().tolist(),vertices=vertices)
        before=copy.deepcopy((packet,native,binding))
        result,attachment,proof=groom.standalone_cloth_attachment(packet,native,binding)
        self.assertEqual([v['anchor']for v in attachment['vertices'][3:]],[0,0])
        self.assertEqual(attachment['vertices'][3]['offset'],attachment['vertices'][4]['offset'])
        self.assertEqual(result['boneIndices'][12:16],result['boneIndices'][16:20])
        for field in ('vertices','normals','tangents','colors','bindPoses','uv','subMeshes','shapes'):
            self.assertEqual(result[field],packet[field])
        self.assertEqual(result['boneIndices'][:12],native['boneIndices'])
        self.assertEqual((packet,native,binding),before)
        self.assertTrue(proof['canonicalSkinAndClothAnchorShared'])


class CharacterArchiveContracts(unittest.TestCase):
    def test_rebased_stream_resolves_the_original_payload(self):
        previous, current = 'CAB-' + 'a' * 32, 'CAB-' + 'b' * 32
        payload = b'unchanged compressed texture mip payload'
        trees = {
            1: dict(m_Name='source.bundle', m_AssetBundleName='source.bundle'),
            2: dict(m_Name='Hair', m_StreamData=dict(path=f'archive:/{previous}/{previous}.resS', offset=0, size=len(payload))),
        }
        def object_reader(key, kind):
            return SimpleNamespace(path_id=key, type=SimpleNamespace(name=kind),
                read_typetree=lambda:copy.deepcopy(trees[key]),
                save_typetree=lambda value:trees.__setitem__(key,copy.deepcopy(value)))
        external=SimpleNamespace(path=f'archive:/{previous}/{previous}.resS')
        file=SimpleNamespace(name=previous,objects={1:object_reader(1,'AssetBundle'),2:object_reader(2,'Texture2D')},externals=[external])
        archive=SimpleNamespace(files={previous:file,previous+'.resS':payload})
        result=publisher.rebase_bundle_archive(SimpleNamespace(file=archive),'human_male_hairshape_f.surface.bundle',current)
        stream=trees[2]['m_StreamData'];member=stream['path'].split('/')[-1]
        self.assertEqual(archive.files[member][stream['offset']:stream['offset']+stream['size']],payload)
        self.assertEqual(external.path,stream['path'])
        self.assertNotIn(previous,archive.files)
        self.assertEqual(result['streams'][0]['bytes'],len(payload))
        self.assertEqual(trees[1]['m_AssetBundleName'],'human_male_hairshape_f.surface.bundle')

    def test_rebase_rejects_a_missing_streamed_payload(self):
        previous='CAB-'+'a'*32
        tree=dict(m_StreamData=dict(path=f'archive:/{previous}/{previous}.resS',offset=0,size=16))
        texture=SimpleNamespace(path_id=1,type=SimpleNamespace(name='Texture2D'),read_typetree=lambda:tree,save_typetree=lambda value:None)
        file=SimpleNamespace(name=previous,objects={1:texture},externals=[])
        with self.assertRaisesRegex(ValueError,'payload is absent'):
            publisher.rebase_bundle_archive(SimpleNamespace(file=SimpleNamespace(files={previous:file})),'hair.part.bundle','CAB-'+'b'*32)

    @staticmethod
    def mesh_fixture(influences: int | None=1) -> tuple[dict[str, Any], SimpleNamespace, SimpleNamespace]:
        pose={f'e{row}{column}':float(row==column)for row in range(4)for column in range(4)}
        tree: dict[str, Any]=dict(m_Name='Native',m_IsReadable=True,m_IndexFormat=0,m_VertexData=dict(m_Channels=[dict(dimension=0)for _ in range(12)]),
            m_BindPose=[pose],m_Shapes=dict(channels=[]),m_LocalAABB=dict(m_Center=dict(x=0.,y=0.,z=0.),m_Extent=dict(x=1.,y=1.,z=1.)))
        obj=SimpleNamespace(read_typetree=lambda:tree,read=lambda:None)
        handler=SimpleNamespace(process=lambda:None,m_VertexCount=3,
            m_Vertices=[(0.,0.,0.),(1.,0.,0.),(0.,1.,0.)],m_Normals=[(0.,0.,1.)]*3,
            m_Tangents=[(1.,0.,0.,1.)]*3,m_Colors=None,
            m_BoneWeights=None if influences is None else [(1.,)+(0.,)*(influences-1)]*3,
            m_BoneIndices=[(0,)*(influences or 1)]*3,
            get_triangles=lambda:[[(0,1,2)]])
        return tree,obj,handler

    def test_compact_skin_stream_has_the_native_four_slot_identity(self):
        packets=[]
        for influences in (None,1,2,4):
            _,obj,handler=self.mesh_fixture(influences)
            with mock.patch('UnityPy.helpers.MeshHelper.MeshHandler',lambda value:handler):
                packets.append(publisher.export_current_mesh(obj))
        self.assertEqual({packet['sourceFingerprint']for packet in packets},{packets[0]['sourceFingerprint']})
        self.assertTrue(all(len(packet['weights'])==len(packet['boneIndices'])==12 for packet in packets))

    def test_normalized_color_storage_matches_the_native_color_api(self):
        tree,obj,values=self.mesh_fixture()
        tree['m_VertexData']['m_Channels'][3] = dict(dimension=4, format=2)
        values.m_Colors=[(0,96,255,128)]*3
        with mock.patch('UnityPy.helpers.MeshHelper.MeshHandler',lambda value:values):
            colored=publisher.export_current_mesh(obj)
        self.assertEqual(colored['colors'],[0.,96/255.,1.,128/255.]*3)

    def test_disabled_blendshape_channels_ignore_serialized_residue(self):
        tree,obj,handler=self.mesh_fixture()
        frame: dict[str, Any]=dict(firstVertex=0,vertexCount=1,hasNormals=False,hasTangents=False)
        tree['m_Shapes']=dict(channels=[dict(name='PhysicalFrame',frameIndex=0,frameCount=1)],
            shapes=[frame],fullWeights=[100.],
            vertices=[dict(index=1,vertex=dict(x=.25,y=.5,z=.75),normal=dict(x=1.,y=2.,z=3.),tangent=dict(x=4.,y=5.,z=6.))])
        with mock.patch('UnityPy.helpers.MeshHelper.MeshHandler',lambda value:handler):
            disabled=publisher.export_current_mesh(obj)['shapes'][0]['frames'][0]
            frame.update(hasNormals=True,hasTangents=True)
            enabled=publisher.export_current_mesh(obj)['shapes'][0]['frames'][0]
        self.assertEqual(disabled['vertices'],[0.,0.,0.,.25,.5,.75,0.,0.,0.])
        self.assertEqual(disabled['normals'],[0.]*9)
        self.assertEqual(disabled['tangents'],[0.]*9)
        self.assertEqual(enabled['normals'],[0.,0.,0.,1.,2.,3.,0.,0.,0.])
        self.assertEqual(enabled['tangents'],[0.,0.,0.,4.,5.,6.,0.,0.,0.])


class CharacterBoundaryContracts(unittest.TestCase):
    def test_cut_overlap_stays_on_the_donor_triangle_in_rest_and_physical_frames(self):
        import numpy as np
        _,owner=publisher.source_owner()
        receiver: dict[str, Any]=dict(vertexCount=3,vertices=[0.,0.,0.,1.,0.,0.,0.,-1.,0.],
            normals=[0.,0.,1.]*3,tangents=[1.,0.,0.,1.]*3,bounds=[.5,0.,0.,1.,2.,1.],
            weights=[1.,0.,0.,0.]*3,boneIndices=[0,0,0,0]*3,bindPoses=np.eye(4).ravel().tolist(),
            uv=[],subMeshes=[[0,1,2]],shapes=[dict(name='Muscular',frames=[dict(weight=100.,
                vertices=[0.]*9,normals=[0.]*9,tangents=[0.]*9)])])
        donor=copy.deepcopy(receiver)
        donor['vertices']=[0.,0.,0.,1.,0.,0.,0.,1.,.3]
        donor['shapes'][0]['frames'][0]['vertices']=[0.]*6+[.1,0.,.2]
        before=copy.deepcopy((receiver,donor))
        result,_=owner.overlap_skin_boundary(receiver,donor,[(0,0),(1,1)],np.eye(4))
        for posed in (False,True):
            points=np.asarray(result['vertices']).reshape(-1,3)
            triangle=np.asarray(donor['vertices']).reshape(-1,3)
            if posed:
                points=points+np.asarray(result['shapes'][0]['frames'][0]['vertices']).reshape(-1,3)
                triangle=triangle+np.asarray(donor['shapes'][0]['frames'][0]['vertices']).reshape(-1,3)
            normal=np.cross(triangle[1]-triangle[0],triangle[2]-triangle[0])
            # Artist normals deliberately disagree with the triangle plane.
            # Each cut vertex must be inside the actual donor, including its morph.
            np.testing.assert_allclose((points[:2]-triangle[0])@normal,0.,atol=1e-9,rtol=0.)
            barycentric=np.linalg.lstsq((triangle[1:]-triangle[0]).T,(points[:2]-triangle[0]).T,rcond=None)[0]
            self.assertTrue(np.all(barycentric>0.))
            self.assertTrue(np.all(barycentric.sum(axis=0)<1.))
        for key in ('normals','tangents','weights','boneIndices','bindPoses','uv','subMeshes'):
            self.assertEqual(result[key],receiver[key])
        self.assertEqual(result['vertices'][6:],receiver['vertices'][6:])
        self.assertEqual((receiver,donor),before)
        repeated,_=owner.overlap_skin_boundary(result,donor,[(0,0),(1,1)],np.eye(4))
        self.assertEqual(repeated,result)
        # Updating shading for one physical frame must not replace the position
        # function needed by the next frame.
        donor['shapes'][0]['frames'][0]['normals']=[.1,0.,-.005]*3
        for packet in (receiver,donor):
            frame=copy.deepcopy(packet['shapes'][0]['frames'][0]);frame['weight']=200.
            packet['shapes'][0]['frames'].append(frame)
        multiple,proof=owner.overlap_skin_boundary(receiver,donor,[(0,0),(1,1)],np.eye(4),follow_donor_shading=True)
        self.assertEqual(len(multiple['shapes'][0]['frames']),2)
        self.assertGreater(proof['changedShadingFrameVertices'],0)

    def test_physical_frames_keep_smooth_uv_alias_normals_coherent(self):
        import numpy as np
        owner,_=publisher.source_owner()
        packet: dict[str,Any]=dict(vertexCount=6,vertices=[-1.,0.,0.,0.,0.,0.,0.,1.,0.,0.,0.,0.,1.,0.,0.,0.,1.,0.],
            normals=[0.,0.,1.]*6,tangents=[1.,0.,0.,1.]*6,weights=[1.,0.,0.,0.]*6,boneIndices=[0,0,0,0]*6,
            uv=[dict(channel=0,dimension=2,values=[0.]*24)],subMeshes=[[0,1,2,3,4,5]],
            shapes=[dict(name='Muscular',frames=[dict(weight=100.,vertices=[0.]*18,normals=[0.]*9+[0.,.5,-.134]+[.1,0.,-.005]+[0.]*3,tangents=[0.]*18)])])
        original=copy.deepcopy(packet);result,proof=owner.cohere_skin_frame_normals(packet)
        values=np.asarray(result['normals']).reshape(-1,3)+np.asarray(result['shapes'][0]['frames'][0]['normals']).reshape(-1,3)
        np.testing.assert_allclose(values[[1,3]],[[0.,0.,1.]]*2,atol=1e-7)
        self.assertGreater(proof['changedFrameVertices'],0)
        self.assertEqual(result['shapes'][0]['frames'][0]['normals'][12:15],original['shapes'][0]['frames'][0]['normals'][12:15])
        self.assertEqual(result['shapes'][0]['frames'][0]['vertices'],original['shapes'][0]['frames'][0]['vertices'])
        for key in original:
            if key!='shapes':self.assertEqual(result[key],original[key])
        hard=copy.deepcopy(packet);hard['normals'][9:12]=[0.,1.,0.]
        unchanged,proof=owner.cohere_skin_frame_normals(hard)
        self.assertEqual(unchanged,hard);self.assertEqual(proof['changedFrameVertices'],0)

    @staticmethod
    def boundary_owner() -> Any:
        import ast
        import numpy as np
        path=_BUILD_PATH.with_name('generate.py')
        nodes: list[ast.stmt]=[node for node in ast.parse(path.read_text(encoding='utf-8')).body
               if isinstance(node,ast.FunctionDef)and node.name in ('points','skin_palette_frame','conform_skin_boundary')]
        scope: dict[str, Any]={'np':np,'copy':copy,'skin_palette_frame':publisher.skin_palette_frame}
        exec(compile(ast.Module(body=nodes,type_ignores=[]),str(path),'exec'),scope)
        return scope['conform_skin_boundary']

    @staticmethod
    def packet() -> dict[str, Any]:
        return dict(vertexCount=2,vertices=[0.,0.,0.,2.,0.,0.],normals=[0.,0.,1.]*2,tangents=[1.,0.,0.,1.]*2,bounds=[1.,0.,0.,2.,0.,0.],
                    bindPoses=[1.,0.,0.,0.,0.,1.,0.,0.,0.,0.,1.,0.,0.,0.,0.,1.]*2,
                    weights=[1.,0.,0.,0.]*2,boneIndices=[0,0,0,0]*2,shapes=[],
                    uv=[dict(channel=0,dimension=2,values=[0.,0.,0.,0.,1.,1.,0.,0.])],subMeshes=[[0,1,1]])

    def test_shared_cut_follows_donor_bone_motion_and_musculature(self):
        receiver=self.packet();donor=self.packet();original=copy.deepcopy(receiver)
        donor['boneIndices']=[1,0,0,0]*2
        donor['shapes']=[dict(index=0,name='Muscular',frames=[dict(weight=100.,vertices=[0.,1.,0.,0.,2.,0.],normals=[0.]*6,tangents=[0.]*6)])]
        result,proof=self.boundary_owner()(receiver,donor,['Neck','Chest'],['Neck','Chest'],[(0,0)])
        # Independent two-bone pose: Chest translates X by two, Neck stays still.
        posed_x=result['vertices'][0]+2*result['weights'][0]*(result['boneIndices'][0]==1)
        self.assertEqual(posed_x,2.)
        self.assertEqual(result['shapes'][0]['frames'][0]['vertices'],[0.,1.,0.,0.,0.,0.])
        self.assertEqual(result['vertices'][3:],receiver['vertices'][3:])
        self.assertEqual(result['uv'],receiver['uv']);self.assertEqual(result['subMeshes'],receiver['subMeshes'])
        self.assertEqual(receiver,original);self.assertEqual(proof['sharedVertices'],1)

    def test_missing_positive_donor_support_extends_the_receiver_palette(self):
        receiver=self.packet();donor=self.packet();donor['boneIndices']=[1,0,0,0]*2
        receiver['bindPoses']=receiver['bindPoses'][:16]
        result,proof=self.boundary_owner()(receiver,donor,['Neck'],['Neck','Chest'],[(0,0)])
        self.assertEqual(result['boneIndices'][0],1);self.assertEqual(len(result['bindPoses']),32)
        self.assertEqual(proof['appendedBones'],[dict(donor=1,index=1,path='Chest')])

    def test_incompatible_shared_bind_frames_are_rejected(self):
        receiver=self.packet();donor=self.packet();donor['bindPoses'][28]=1.
        with self.assertRaisesRegex(ValueError,'incompatible.*bind frames'):
            self.boundary_owner()(receiver,donor,['Neck','Chest'],['Neck','Chest'],[(0,0)])

    def test_receiver_only_morph_keeps_the_cut_on_the_unmorphed_donor(self):
        receiver=self.packet();donor=self.packet()
        receiver['shapes']=[dict(index=0,name='Muscular',frames=[dict(weight=100.,vertices=[0.,1.,0.,0.,2.,0.],normals=[0.]*6,tangents=[0.]*6)])]
        result,_=self.boundary_owner()(receiver,donor,['Neck','Chest'],['Neck','Chest'],[(0,0)])
        self.assertEqual(result['shapes'][0]['frames'][0]['vertices'],[0.,0.,0.,0.,2.,0.])

    def test_existing_normal_atlas_frame_survives_the_shared_physical_cut(self):
        import numpy as np
        receiver=self.packet();donor=self.packet()
        angle=np.deg2rad(30.);pose=np.deg2rad(20.)
        receiver['normals'][:3]=[np.sin(angle),0.,np.cos(angle)]
        receiver['tangents'][:3]=[np.cos(angle),0.,-np.sin(angle)]
        donor['shapes']=[dict(index=0,name='Muscular',frames=[dict(weight=100.,vertices=[0.,1.,0.,0.,0.,0.],
            normals=[0.,-np.sin(pose),np.cos(pose)-1.,0.,0.,0.],tangents=[0.]*6)])]
        result,proof=self.boundary_owner()(receiver,donor,['Neck','Chest'],['Neck','Chest'],[(0,0)],shading_frame_owner='receiver')
        np.testing.assert_array_equal(np.asarray(result['normals'],np.float32),np.asarray(receiver['normals'],np.float32))
        np.testing.assert_array_equal(np.asarray(result['tangents'],np.float32),np.asarray(receiver['tangents'],np.float32))
        # This tangent-space atlas sample produces the donor normal at rest.
        # The same sample must still produce the donor's posed normal after
        # the cut follows its body morph, with no second rest-frame rotation.
        atlas=np.array([-np.sin(angle),0.,np.cos(angle)])
        frame=result['shapes'][0]['frames'][0]
        normal=np.asarray(result['normals'][:3])+np.asarray(frame['normals'][:3])
        tangent=np.asarray(result['tangents'][:3])+np.asarray(frame['tangents'][:3])
        surface=np.stack((tangent,np.cross(normal,tangent),normal),axis=1)@atlas
        np.testing.assert_allclose(surface,[0.,-np.sin(pose),np.cos(pose)],atol=1e-7,rtol=0.)
        self.assertEqual(frame['vertices'][:3],[0.,1.,0.])
        self.assertEqual(proof['shadingFrameOwner'],'receiver')

    def test_unknown_shading_frame_owner_is_rejected(self):
        with self.assertRaisesRegex(ValueError,'shading-frame owner'):
            self.boundary_owner()(self.packet(),self.packet(),['Neck','Chest'],['Neck','Chest'],[(0,0)],shading_frame_owner='unknown')


if __name__ == '__main__':
    unittest.main()
