import importlib.util
import contextlib
import datetime
import os
import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("resource_storage", Path(__file__).parents[1] / "resource_storage.py")
storage = importlib.util.module_from_spec(spec)
spec.loader.exec_module(storage)
PACKAGE_TEST_VERSION=datetime.date.min.isoformat().replace('-','.')+'.1'


class ResourceStorageTest(unittest.TestCase):
    def test_whole_tool_inventory_counts_root_files_and_shared_payloads(self):
        with tempfile.TemporaryDirectory()as directory:
            root=Path(directory);(root/'A').mkdir();(root/'B').mkdir()
            (root/'entry.txt').write_bytes(b'abc');original=root/'A/payload.bundle';original.write_bytes(b'12345')
            os.link(original,root/'B/payload.bundle')
            result=storage.inventory_tools(root)
            self.assertEqual(result['files'],3)
            self.assertEqual(result['directories'],3)
            self.assertEqual(result['logicalBytes'],13)
            self.assertEqual(result['uniqueFileBytes'],8)

    @contextlib.contextmanager
    def owned_workspace(self):
        with tempfile.TemporaryDirectory()as directory:
            root=Path(directory).resolve();artifacts=root/'artifacts';source=artifacts/'Source';output=artifacts/'Candidate'
            (source/'Catalog').mkdir(parents=True);original=source/'Catalog/hero.bundle';original.write_bytes(b'original')
            storage.write(source/(Path('Catalog')/storage.RESOURCE_FILES['manifest']),dict(bundles=[dict(file=original.name,sha256=storage.sha(original))],meshes=[]))
            with patch.object(storage,'ROOT',root),patch.object(storage,'ARTIFACTS',artifacts),patch.object(storage,'WORKSPACE',root/'workspace'), \
                    patch.dict(storage.checked_catalog_path.__globals__,ROOT=root):
                yield root,source,output

    def test_failed_producer_claims_new_data_and_preserves_existing_user_files(self):
        with self.owned_workspace()as(_,source,output):
            output.mkdir();unrelated=output/'unrelated.bin';unrelated.write_bytes(b'keep')
            with self.assertRaisesRegex(ValueError,'generation failed'):
                with storage.resource_work('test',['--source',str(source),'--output',str(output)]):
                    self.assertTrue(storage.work_active(storage.read(storage.work_marker(output))))
                    (output/'generated.bin').write_bytes(b'generated')
                    raise ValueError('generation failed')
            record=storage.read(storage.work_marker(output))
            self.assertEqual(record['state'],'failed')
            self.assertEqual([Path(row['path']).name for row in record['files']],['generated.bin'])
            self.assertEqual(unrelated.read_bytes(),b'keep')
            references=storage.read(storage.ARTIFACTS/'StorageReferences.json')['paths']
            self.assertIn(str(output),references)
            self.assertIn(str(source/'Catalog'),references)

    def test_completed_receipt_closure_keeps_local_intermediates_in_the_same_owner(self):
        with self.owned_workspace()as(_,source,output):
            intermediate=output/'AnyIntermediateFolder/mesh.bytes';intermediate.parent.mkdir(parents=True)
            intermediate.write_bytes(b'generated intermediate')
            receipt=output/'AnyReceiptFolder/result.json'
            storage.write(receipt,dict(source=dict(path=str(intermediate),sha256=storage.sha(intermediate)),
                external=dict(path=str(source/'Catalog/hero.bundle'),sha256=storage.sha(source/'Catalog/hero.bundle'))))
            pin=dict(path=str(receipt),sha256=storage.sha(receipt))
            payloads,guards=storage.declared_local_payloads(output,dict(proof=pin,repeated=pin))
            self.assertEqual(payloads,{intermediate:storage.sha(intermediate)})
            self.assertEqual(guards,[pin])
            self.assertTrue(storage.has_payloads(output))

    def test_completed_receipt_closure_rejects_changed_local_inputs(self):
        with self.owned_workspace()as(_,_,output):
            output.mkdir();payload=output/'texture.mips';payload.write_bytes(b'generated')
            pin=dict(path=str(payload),sha256=storage.sha(payload));payload.write_bytes(b'changed')
            with self.assertRaisesRegex(ValueError,'declared local producer input changed'):
                storage.declared_local_payloads(output,dict(payload=pin))

    def test_completed_receipt_closure_does_not_claim_unlisted_files_or_external_inputs(self):
        with self.owned_workspace()as(_,source,output):
            output.mkdir();unlisted=output/'manual.bin';unlisted.write_bytes(b'keep')
            payloads,guards=storage.declared_local_payloads(output,
                dict(source=dict(path=str(source/'Catalog/hero.bundle'),sha256=storage.sha(source/'Catalog/hero.bundle'))))
            self.assertEqual(payloads,{})
            self.assertEqual(guards,[])

    def test_discovery_finds_intermediate_data_after_final_bundles_were_retired(self):
        with self.owned_workspace()as(_,source,output):
            catalog=output/'Catalog';catalog.mkdir(parents=True)
            storage.write(catalog/storage.RESOURCE_FILES['manifest'],storage.read(source/(Path('Catalog')/storage.RESOURCE_FILES['manifest'])))
            intermediate=output/'ArbitraryStage/texture.mips';intermediate.parent.mkdir()
            intermediate.write_bytes(b'intermediate')
            receipt=output/'Stages/result.json'
            storage.write(receipt,dict(source=dict(path=str(intermediate),sha256=storage.sha(intermediate))))
            owner=dict(status='PASS_STANDALONE_CHARACTER_RESOURCES',catalogSha256=storage.sha(catalog/storage.RESOURCE_FILES['manifest']),
                stage=dict(path=str(receipt),sha256=storage.sha(receipt)))
            storage.write(output/'ResourceAssembly.json',owner)
            catalogs,retained=storage.discover([storage.ARTIFACTS])
            self.assertEqual(retained,[])
            self.assertEqual(len(catalogs),1)
            self.assertIn(intermediate,catalogs[0]['owned_payloads'])

    def test_receipt_closure_does_not_reopen_an_owners_historical_self_reference(self):
        with self.owned_workspace()as(_,_,output):
            output.mkdir();payload=output/'generated.mips';payload.write_bytes(b'generated')
            document=dict(history=dict(path=str(output/'ResourceAssembly.json'),sha256='0'*64),
                generated=dict(path=str(payload),sha256=storage.sha(payload)))
            storage.write(output/'ResourceAssembly.json',document)
            payloads,_=storage.declared_local_payloads(output,document)
            self.assertEqual(payloads,{payload:storage.sha(payload)})

    @unittest.skipUnless(os.name=='nt','Windows shared tool-operation lock')
    def test_resource_production_blocks_the_actual_powershell_cleanup_lock(self):
        helper=Path(__file__).parents[2]/'game-test/GameTest.Common.ps1'
        script="$ErrorActionPreference='Stop'\n. '"+str(helper).replace("'","''")+"'\ntry {$lock=Enter-GtLock;$lock.ReleaseMutex();$lock.Dispose();exit 9} catch {if($_.Exception.Message -eq 'Another build or game-test operation owns the deployment lock.'){exit 0};exit 12}\n"
        def probe():return storage.subprocess.run(['powershell.exe','-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-Command','-'],input=script,text=True,capture_output=True).returncode
        with storage.resource_operation():self.assertEqual(probe(),0)
        self.assertEqual(probe(),9)

    def test_work_identity_checks_process_birth_time_instead_of_pid_alone(self):
        record=dict(state='running',creator=dict(pid=42,start=100))
        with patch.object(storage,'process_start',return_value=100):self.assertTrue(storage.work_active(record))
        with patch.object(storage,'process_start',return_value=200):self.assertFalse(storage.work_active(record))
        with patch.object(storage,'process_start',return_value=None):self.assertFalse(storage.work_active(record))

    def test_interrupted_producer_keeps_baseline_and_releases_only_its_new_files(self):
        with self.owned_workspace()as(root,source,output):
            output.mkdir();existing=output/'unrelated.bin';existing.write_bytes(b'keep')
            with storage.resource_work('test',['--source',str(source),'--output',str(output)]):
                generated=output/'generated.bin';generated.write_bytes(b'generated')
            marker=storage.work_marker(output);record=storage.read(marker)
            record.update(state='running',creator=dict(pid=2147483647,start=1),files=[])
            storage.write(marker,record)
            with patch.object(storage,'producer_processes',return_value=[]):
                records,packages,retained,live=storage.discover_work([root],dict(modified=marker.stat().st_mtime_ns+1),set(),False)
            self.assertEqual(len(records),1)
            self.assertEqual(set(records[0]['owned_payloads']),{generated})
            self.assertEqual(existing.read_bytes(),b'keep')

    def test_running_work_keeps_its_declared_source_closure(self):
        with self.owned_workspace()as(root,source,output):
            with storage.resource_work('test',['--source',str(source),'--output',str(output)]):
                with patch.object(storage,'producer_processes',return_value=[]):
                    records,_,retained,live=storage.discover_work([root],dict(modified=0),set(),False)
                self.assertEqual(records,[])
                self.assertIn(source/'Catalog/hero.bundle',live)
                self.assertEqual(retained[0]['reason'],'active-resource-producer')

    @unittest.skipUnless(os.name=='nt','Windows producer-input sharing contract')
    def test_cleanup_read_lease_blocks_producer_resume_before_output_changes(self):
        with self.owned_workspace()as(_,source,output):
            with storage.resource_work('test',['--source',str(source),'--output',str(output)]):
                output.mkdir();(output/'generated.bin').write_bytes(b'original')
            with storage.work_marker(output).open('rb'):
                with self.assertRaises(PermissionError):
                    with storage.resource_work('test',['--source',str(source),'--output',str(output)]):
                        (output/'generated.bin').write_bytes(b'changed')
            self.assertEqual((output/'generated.bin').read_bytes(),b'original')

    def test_partial_work_claims_only_source_declared_catalog_names(self):
        with self.owned_workspace()as(_,source,output):
            (output/'Catalog').mkdir(parents=True)
            (output/'Catalog/hero.bundle').write_bytes(b'partly modified')
            (output/'Catalog/unrelated.bundle').write_bytes(b'keep')
            storage.write(output/'WorkingDirectory.json',dict(kind='OwnedFacePreparation',source=str(source),catalogSha256=storage.sha(source/(Path('Catalog')/storage.RESOURCE_FILES['manifest'])),inputs=[]))
            record=storage.partial_catalog(output)
            self.assertEqual(set(record['owned_payloads']),{output/'Catalog/hero.bundle'})
            self.assertEqual(record['owned_payloads'][output/'Catalog/hero.bundle'],storage.sha(output/'Catalog/hero.bundle'))
            (source/(Path('Catalog')/storage.RESOURCE_FILES['manifest'])).write_text('{}')
            with self.assertRaisesRegex(ValueError,'source manifest changed'):storage.partial_catalog(output)

    def test_failed_job_input_authorizes_its_catalog_and_rejects_other_targets(self):
        with self.owned_workspace()as(_,source,output):
            (output/'Catalog').mkdir(parents=True);(output/'Catalog/hero.bundle').write_bytes(b'copied')
            job=dict(source=str(source/'Catalog/hero.bundle'),sha256=storage.sha(source/'Catalog/hero.bundle'),target=str(output/'Catalog/hero.bundle'))
            storage.write(output/'Inputs/job.json',job)
            self.assertEqual(set(storage.partial_catalog(output)['owned_payloads']),{output/'Catalog/hero.bundle'})
            job['target']=str(output/'Other/hero.bundle');storage.write(output/'Inputs/job.json',job)
            self.assertIsNone(storage.partial_catalog(output))

    def test_retained_working_copy_requires_exact_installed_catalog_bytes(self):
        with self.owned_workspace()as(_,source,output):
            (output/'Catalog').mkdir(parents=True);installed=output/'Catalog/hero.bundle';installed.write_bytes(b'finished')
            target=output/'Working/Sources/hero.bundle';target.parent.mkdir(parents=True);target.write_bytes(installed.read_bytes())
            job=dict(source=dict(path=str(source/'Catalog/hero.bundle'),sha256=storage.sha(source/'Catalog/hero.bundle')),
                target=dict(path=str(target),sha256=storage.sha(target)))
            storage.write(output/'Receipts/job.json',job)
            self.assertEqual(set(storage.partial_catalog(output)['owned_payloads']),{installed,target})
            installed.write_bytes(b'changed')
            self.assertIsNone(storage.partial_catalog(output))

    def test_nested_producer_tasks_own_their_outer_catalog_without_claiming_child_owners(self):
        with self.owned_workspace()as(_,source,output):
            (output/'Catalog').mkdir(parents=True);bundle=output/'Catalog/hero.bundle';bundle.write_bytes(b'cloned')
            job=dict(source=str(source/'Catalog/hero.bundle'),sha256=storage.sha(source/'Catalog/hero.bundle'),target=str(bundle))
            storage.write(output/'Finishing/Inputs/job.json',job)
            cache={}
            self.assertEqual(set(storage.partial_catalog(output,cache)['owned_payloads']),{bundle})
            self.assertEqual(len(cache),1)
            (output/'Finishing/Inputs/job.json').unlink()
            (output/'Child/Catalog').mkdir(parents=True)
            storage.write(output/'Child/Inputs/job.json',job)
            self.assertIsNone(storage.partial_catalog(output))

    def test_published_package_retirement_requires_matching_remote_receipt(self):
        with self.owned_workspace()as(root,source,output):
            output.mkdir();archive=output/(output.name+'.zip');archive.write_bytes(b'published bytes')
            (output/'unrelated.zip').write_bytes(b'keep')
            fingerprint=storage.sha(source/(Path('Catalog')/storage.RESOURCE_FILES['manifest']))
            row=dict(file=archive.name,bytes=archive.stat().st_size,sha256=storage.sha(archive))
            storage.write(output/storage.RESOURCE_FILES['packageInputs'],dict(schemaVersion=1,kind='CharacterResourcePacks',sourceSha256=fingerprint,version=PACKAGE_TEST_VERSION))
            storage.write(output/storage.RESOURCE_FILES['packs'],dict(version=PACKAGE_TEST_VERSION,catalogSha256=fingerprint,packs=[row]))
            self.assertIsNone(storage.published_package(output))
            receipt=dict(status='PASS_PUBLISHED_MODEL_RESOURCES',catalogSha256=fingerprint,tag=storage.resource_release_metadata(PACKAGE_TEST_VERSION)['tag_name'],assets=[dict(name=row['file'],bytes=row['bytes'],sha256=row['sha256'])])
            storage.write(output.parent/'PublicationReceipt.json',receipt)
            self.assertEqual(set(storage.published_package(output)['owned_payloads']),{archive})
            receipt['catalogSha256']='a later material-only catalog'
            storage.write(output.parent/'PublicationReceipt.json',receipt)
            self.assertEqual(set(storage.published_package(output)['owned_payloads']),{archive})
            archive.write_bytes(b'changed')
            with self.assertRaisesRegex(ValueError,'local published package changed'):storage.published_package(output)

    def test_superseded_complete_package_needs_an_older_unretained_source(self):
        with self.owned_workspace()as(_,source,output):
            output.mkdir();archive=output/(output.name+'.zip');archive.write_bytes(b'obsolete package')
            fingerprint=storage.sha(source/(Path('Catalog')/storage.RESOURCE_FILES['manifest']))
            storage.write(output/storage.RESOURCE_FILES['packageInputs'],dict(schemaVersion=1,kind='CharacterResourcePacks',sourceSha256=fingerprint,version=PACKAGE_TEST_VERSION))
            storage.write(output/storage.RESOURCE_FILES['packs'],dict(version=PACKAGE_TEST_VERSION,catalogSha256=fingerprint,packs=[dict(file=archive.name,bytes=archive.stat().st_size,sha256=storage.sha(archive))]))
            current=dict(modified=(output/storage.RESOURCE_FILES['packs']).stat().st_mtime_ns+1)
            self.assertIsNone(storage.published_package(output))
            self.assertIsNone(storage.published_package(output,current,{fingerprint}))
            self.assertEqual(storage.published_package(output,current,set())['kind'],'superseded-package')

    @unittest.skipUnless(os.name=='nt','Windows process census')
    def test_process_census_ignores_own_redirector_and_editor_language_server(self):
        own=os.getpid();parent=own+1;producer=own+2;lsp=own+3
        script=str(storage.ROOT/'tools/asset-authoring/resource_storage.py')
        rows=[dict(ProcessId=own,ParentProcessId=parent,CommandLine='python.exe "'+script+'" --output x'),
            dict(ProcessId=parent,ParentProcessId=0,CommandLine='python.exe "'+script+'" --output x'),
            dict(ProcessId=producer,ParentProcessId=0,CommandLine='python.exe "'+str(storage.ROOT/'tools/build.py')+'"'),
            dict(ProcessId=lsp,ParentProcessId=0,CommandLine='python.exe "C:\\editor-extension\\lsp_server.py"')]
        with patch.object(storage.subprocess,'run',return_value=type('Result',(),dict(stdout=json.dumps(rows)))()):
            self.assertEqual(storage.producer_processes(),[producer])

    def test_copied_art_flag_does_not_accept_a_changed_catalog(self):
        with tempfile.TemporaryDirectory() as directory:
            path=Path(directory).resolve()/"ArtAcceptance.json"
            storage.write(path,dict(status="PASS_NATIVE_APPEARANCE_REVIEW",candidate=dict(sha256="reviewed")))
            assembly=dict(artAccepted=True,artAcceptance=dict(path=str(path),sha256=storage.sha(path)))
            self.assertTrue(storage.resource_art_acceptance(assembly,"reviewed"))
            self.assertFalse(storage.resource_art_acceptance(assembly,"changed-candidate"))
            path.write_text("changed")
            self.assertFalse(storage.resource_art_acceptance(assembly,"reviewed"))

    def test_adoption_protects_current_previous_and_unadopted_new_work(self):
        def row(name, modified, accepted=False):
            return {"sha256": name, "modified": modified, "accepted": accepted}
        current = row("current", 30, True)
        previous = row("previous", 20, True)
        old = row("old", 10, True)
        rejected = row("candidate", 25)
        new = row("in-review", 40)
        adopted, retained, retired = storage.select_retired([current, previous, old, rejected, new], "current", 2)
        self.assertIs(adopted, current)
        self.assertEqual(retained, {"current", "previous"})
        self.assertEqual(retired, [old, rejected])

    def test_no_matching_accepted_owner_retains_everything(self):
        with self.assertRaisesRegex(ValueError, "No accepted"):
            storage.select_retired([{"sha256": "current", "accepted": False, "modified": 10}], "current", 2)

    def test_adopted_source_survives_newer_review_timestamps(self):
        current = dict(sha256="current", modified=30, accepted=True)
        review = dict(sha256="review", modified=20, accepted=True)
        source = dict(sha256="published-source", modified=10, accepted=False)
        obsolete = dict(sha256="obsolete", modified=5, accepted=False)
        _, retained, retired = storage.select_retired([current, review, source, obsolete], "current", 2, {"published-source"})
        self.assertEqual(retained, {"current", "review", "published-source"})
        self.assertEqual(retired, [obsolete])

    def test_source_retention_requires_the_exact_adopted_quality_and_source(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory).resolve()
            candidate = root / "Current"
            source = root / (Path('Source')/storage.RESOURCE_FILES['manifest'])
            storage.write(source, {"bundles": [], "meshes": []})
            quality = dict(candidateManifestSha256="candidate", sourceCatalog=str(source.parent), sourceManifestSha256=storage.sha(source))
            path = candidate / "Catalog/CatalogQuality.json"
            storage.write(path, quality)
            current = dict(directory=candidate, sha256="candidate")
            with patch.object(storage, "ARTIFACTS", root), patch.object(storage, "WORKSPACE", root / "Workspace"):
                self.assertEqual(storage.adopted_source_hashes(current), {storage.sha(source)})
                quality["candidateManifestSha256"] = "different"
                storage.write(path, quality)
                with self.assertRaisesRegex(ValueError, "does not match"):
                    storage.adopted_source_hashes(current)
                quality["candidateManifestSha256"] = "candidate"
                storage.write(path, quality)
                source.write_text("changed")
                with self.assertRaisesRegex(ValueError, "source catalog changed"):
                    storage.adopted_source_hashes(current)

    def test_directory_reference_compaction_removes_only_declared_payload(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory).resolve()
            (root / "Candidate/Catalog").mkdir(parents=True)
            (root / "Candidate/Sources").mkdir()
            retired = root / "Candidate/Catalog/old.bundle"
            retired.write_bytes(b"old")
            metadata = root / (Path('Candidate/Catalog')/storage.RESOURCE_FILES['manifest'])
            metadata.write_text("{}")
            other = root / "unrelated.bundle"
            other.write_bytes(b"keep")
            with patch.object(storage, "ARTIFACTS", root), patch.object(storage, "WORKSPACE", root / "Workspace"):
                result = storage.compact_references([str(root)], [retired])
                self.assertNotIn(str(retired), result)
                self.assertIn(str(metadata), result)
                self.assertIn(str(other), result)
                self.assertIn(str(root / "Candidate/Sources"), result)

    def test_invalid_adopted_source_stops_retirement_before_reference_changes(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory).resolve()
            candidate = root / "Current"
            manifest = candidate / (Path('Catalog')/storage.RESOURCE_FILES['manifest'])
            source = root / (Path('Source')/storage.RESOURCE_FILES['manifest'])
            storage.write(manifest, {"bundles": [], "meshes": []})
            storage.write(source, {"bundles": [], "meshes": []})
            fingerprint = storage.sha(manifest)
            storage.write(candidate / "Catalog/CatalogQuality.json", dict(candidateManifestSha256=fingerprint,
                sourceCatalog=str(source.parent), sourceManifestSha256="0" * 64))
            references = root / "StorageReferences.json"
            storage.write(references, {"schemaVersion": 1, "paths": [str(source.parent)]})
            before = references.read_bytes()
            current = dict(directory=candidate, sha256=fingerprint, accepted=True, modified=1)
            with patch.object(storage, "ARTIFACTS", root), patch.object(storage, "WORKSPACE", root / "Workspace"), \
                    patch.object(storage, "PUBLIC", manifest), patch.object(storage, "discover", return_value=([current], [])):
                with self.assertRaisesRegex(ValueError, "source catalog changed"):
                    storage.plan(root / "Output", apply=True)
            self.assertEqual(references.read_bytes(), before)
            self.assertFalse((root / "Output/ResourceLifecycle.json").exists())

    def test_changed_completed_catalog_is_retained(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory).resolve()
            (root / "Catalog").mkdir()
            (root / "GeometryCatalog").mkdir()
            (root / "GeometryCatalog/remaining.bundle").write_bytes(b"remaining intermediate payload")
            storage.write(root / (Path('Catalog')/storage.RESOURCE_FILES['manifest']), {"bundles": [], "meshes": []})
            storage.write(root / "ResourceAssembly.json", {"status": "PASS_STANDALONE_CHARACTER_RESOURCES", "catalogSha256": "0" * 64})
            catalogs, retained = storage.discover([root])
            self.assertEqual(catalogs, [])
            self.assertEqual(len(retained), 1)

    def test_fully_retired_catalog_does_not_reparse_its_producer_receipt(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory).resolve()
            (root / "Catalog").mkdir()
            storage.write(root / (Path('Catalog')/storage.RESOURCE_FILES['manifest']), {"bundles": [], "meshes": []})
            storage.write(root / "ResourceAssembly.json", {"status": "PASS_STANDALONE_CHARACTER_RESOURCES"})
            with patch.object(storage, "read", side_effect=AssertionError("No payload requires no producer reparse")):
                self.assertEqual(storage.discover([root]), ([], []))

    def test_already_removed_payloads_do_not_resolve_every_leaf(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory).resolve()
            (root / "Catalog").mkdir()
            source = root / "source.json"
            source.write_text("{}")
            removed = [root / "Catalog" / f"{index}.bundle" for index in range(1000)]
            resolve = Path.resolve
            with patch.object(storage, "ARTIFACTS", root), patch.object(storage, "WORKSPACE", root / "Workspace"), \
                    patch.object(Path, "resolve", autospec=True, side_effect=resolve) as calls:
                result = storage.compact_references([str(root)], removed)
                self.assertEqual(result, [str(source)])
                self.assertLess(calls.call_count, 20)

    def test_reparse_reference_is_rejected_before_resolving_it(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory).resolve()
            with patch.object(storage, "ARTIFACTS", root), patch.object(storage, "WORKSPACE", root / "Workspace"), \
                    patch.object(Path, "is_symlink", return_value=True):
                with self.assertRaisesRegex(ValueError, "reparse point"):
                    storage.compact_references([str(root)], [])


if __name__ == "__main__":
    unittest.main()
