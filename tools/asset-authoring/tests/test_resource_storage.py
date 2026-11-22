import importlib.util
import contextlib
import datetime
import os
import ntpath
import posixpath
import types
import json
import sys
import tempfile
import unittest
from pathlib import Path, PureWindowsPath, PurePosixPath
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("resource_storage", Path(__file__).parents[1] / "resource_storage.py")
storage = importlib.util.module_from_spec(spec)
spec.loader.exec_module(storage)
PACKAGE_TEST_VERSION=datetime.date.min.isoformat().replace('-','.')+'.1'


class ResourceStorageTest(unittest.TestCase):
    def test_catalog_archive_excludes_retained_and_unplanned_bundle_paths(self):
        path = Path(__file__).parents[1] / 'catalog_archive.py'
        archive_spec = importlib.util.spec_from_file_location('catalog_archive', path)
        archive = importlib.util.module_from_spec(archive_spec)
        with patch.dict(sys.modules, resource_storage=storage):
            archive_spec.loader.exec_module(archive)
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory).resolve()
            owners = [root / name for name in ('First', 'Second', 'Retained', 'Unplanned')]
            content = b'one original bundle'
            leaves = []
            for owner in owners:
                folder = owner / 'Catalog'
                folder.mkdir(parents=True)
                (owner / 'ResourceAssembly.json').touch()
                leaf = folder / 'original.bundle'
                leaf.write_bytes(content)
                leaves.append(leaf)
            digest = storage.sha(leaves[0])
            report = root / 'ResourceLifecycle.json'
            plan = root / 'ResourcePayloads-0.json'
            storage.write(root / 'StorageReferences.json', dict(schemaVersion=1, paths=[str(leaves[2])]))
            storage.write(plan, dict(schemaVersion=1, artifactRoot=str(root), files=[
                dict(path=str(leaf), sha256=digest, bytes=len(content)) for leaf in leaves[:3]]))
            storage.write(report, dict(status='references-closed-payloads-planned', adoptedManifestSha256='public',
                explicitRetainedDirectories=[], retainedCatalogHashes=[], retiredCatalogs=list(map(str, owners)),
                plans=[dict(root=str(root), plan=str(plan), files=3)]))
            with (patch.object(archive, 'ARTIFACTS', root), patch.object(archive, 'distribution_catalog', return_value=('public', {})),
                  patch.object(archive, 'checked_catalog_path', side_effect=lambda value: Path(value).resolve()),
                  patch.object(archive, 'checked_source_path', side_effect=lambda value: Path(value).resolve(strict=True)),
                  patch.object(archive, 'owner_catalog', return_value=('retired', {'original.bundle': digest}))):
                groups, archived = archive.archive_plan(report, (), 0)
            self.assertEqual(set(archived), set(owners[:2]))
            self.assertEqual(groups[digest], tuple((owner, 'original.bundle', len(content)) for owner in owners[:2]))

    def test_packed_distribution_pins_both_manifest_owners(self):
        with self.owned_workspace() as (_, source, output):
            fallback = source / 'Catalog' / storage.RESOURCE_FILES['manifest']
            packs = fallback.with_name(storage.RESOURCE_FILES['packs'])
            packed_manifest = output / storage.RESOURCE_FILES['manifest']
            storage.write(packed_manifest, dict(resources=[]))
            fallback_digest = storage.sha(fallback)
            packed_digest = storage.sha(packed_manifest)
            with patch.object(storage, 'PUBLIC', fallback):
                self.assertEqual(storage.distribution_catalog(), (fallback_digest, {fallback: fallback_digest}))
                storage.write(packs, dict(schemaVersion=1, catalogSha256=packed_digest))
                self.assertEqual(storage.distribution_catalog(), (
                    packed_digest, {fallback: fallback_digest, packs: storage.sha(packs)}))

    def test_payload_hash_scope_reuses_hardlinked_bytes_but_never_aliases_distinct_files(self):
        with tempfile.TemporaryDirectory()as directory:
            root=Path(directory);first=root/'packet.json';first.write_bytes(b'1111')
            alias=root/'linked.json';os.link(first,alias)
            other=root/'other.json';other.write_bytes(b'2222')
            stamp=first.stat();os.utime(other,ns=(stamp.st_atime_ns,stamp.st_mtime_ns))
            with storage.payload_hash_operation()as scope:
                with storage.payload_hash_operation()as nested:self.assertIs(scope,nested)
                self.assertEqual(storage.sha(first,payload=True),storage.sha(alias,payload=True))
                self.assertNotEqual(storage.sha(other,payload=True),storage.sha(first,payload=True))
                self.assertEqual(scope['payloadHashes'],2);self.assertEqual(scope['payloadCacheHits'],2)
                self.assertEqual(scope['payloadShaReadBytes'],8)
            self.assertIsNone(storage._hash_scope.get());self.assertEqual(scope['digests'],{})
            with storage.payload_hash_operation()as next_scope:
                storage.sha(alias,payload=True)
                self.assertEqual(next_scope['payloadHashes'],1);self.assertEqual(next_scope['payloadCacheHits'],0)

    def test_payload_hash_identity_detects_same_size_edits_with_preserved_mtime(self):
        with tempfile.TemporaryDirectory()as directory:
            path=Path(directory)/'packet.json';path.write_bytes(b'first')
            stamp=path.stat()
            with storage.payload_hash_operation()as scope:
                before=storage.sha(path,payload=True)
                with path.open('r+b')as stream:
                    stream.write(b'other');stream.flush();os.fsync(stream.fileno())
                os.utime(path,ns=(stamp.st_atime_ns,stamp.st_mtime_ns))
                self.assertEqual(path.stat().st_ino,stamp.st_ino)
                self.assertEqual(path.stat().st_size,stamp.st_size)
                self.assertEqual(path.stat().st_mtime_ns,stamp.st_mtime_ns)
                self.assertNotEqual(storage.sha(path,payload=True),before)
                self.assertEqual(scope['payloadHashes'],2);self.assertEqual(scope['payloadCacheHits'],0)

    def test_metadata_and_generation_hashes_are_fresh_without_native_identity_queries(self):
        with tempfile.TemporaryDirectory()as directory:
            path=Path(directory)/'recipe.json';path.write_bytes(b'first')
            with storage.payload_hash_operation()as scope,patch.object(storage,'file_hash_identity',side_effect=AssertionError('No payload memo lookup')):
                before=storage.sha(path);path.write_bytes(b'other')
                self.assertNotEqual(storage.sha(path),before)
                self.assertEqual(scope['metadataHashes'],2);self.assertEqual(scope['payloadCacheHits'],0)
                statistics=storage.hash_statistics(scope)
                self.assertEqual(statistics['repeatedMetadataInputs'],1)
                self.assertEqual(statistics['repeatedMetadataReadBytes'],5)
                self.assertEqual(statistics['topRepeatedMetadataInputs'],[dict(path=str(path),reads=2,logicalReadBytes=10,subsequentReadBytes=5)])
            self.assertEqual(scope['metadataReadPaths'],{})
            with patch.object(storage,'file_hash_identity',side_effect=AssertionError('No generation memo lookup')):
                storage.sha(path,payload=True)

    def test_payload_hash_fallback_reads_fresh_and_discards_a_hit_when_identity_is_lost(self):
        with tempfile.TemporaryDirectory()as directory:
            path=Path(directory)/'packet.json';path.write_bytes(b'owned')
            with path.open('rb')as stream:identity=storage.file_hash_identity(stream)
            self.assertIsNotNone(identity)
            with storage.payload_hash_operation()as scope,patch.object(storage,'file_hash_identity',side_effect=[identity,identity,identity,None]):
                first=storage.sha(path,payload=True);self.assertEqual(storage.sha(path,payload=True),first)
                self.assertEqual(scope['payloadHashes'],2);self.assertEqual(scope['payloadCacheHits'],0)
                self.assertEqual(scope['identityUnavailableFallbacks'],1);self.assertEqual(scope['digests'],{})
            with storage.payload_hash_operation()as scope,patch.object(storage,'file_hash_identity',return_value=None):
                storage.sha(path,payload=True);storage.sha(path,payload=True)
                self.assertEqual(scope['payloadHashes'],2);self.assertEqual(scope['payloadCacheHits'],0)
                self.assertEqual(scope['identityUnavailableFallbacks'],2)

    def test_hash_progress_flushes_bounded_summaries_at_elapsed_or_read_byte_thresholds(self):
        with tempfile.TemporaryDirectory()as directory:
            path=Path(directory)/'packet.json';path.write_bytes(b'x'*32)
            for trigger in('bytes','elapsed'):
                with self.subTest(trigger=trigger):
                    tick=[100.]
                    with patch.object(storage.time,'monotonic',side_effect=lambda:tick[0]),patch('builtins.print')as output, \
                            storage.payload_hash_operation(progress_seconds=10,progress_bytes=64 if trigger=='bytes'else 1024**4)as scope:
                        storage.sha(path,payload=True);storage.sha(path,payload=True)
                        self.assertEqual(output.call_count,0)
                        if trigger=='elapsed':tick[0]=111.
                        storage.sha(path)
                        self.assertEqual(output.call_count,1);self.assertTrue(output.call_args.kwargs['flush'])
                        progress=json.loads(output.call_args.args[0].split(': ',1)[1])
                        self.assertEqual(progress['shaReadBytes'],64);self.assertEqual(progress['payloadCacheHits'],1)
                        storage.sha(path)
                        self.assertEqual(output.call_count,1)
                        storage.hash_progress(scope,stage='retained-input-closure',force=True)
                        self.assertEqual(output.call_count,2)
                        self.assertEqual(json.loads(output.call_args.args[0].split(': ',1)[1])['stage'],'retained-input-closure')
                        self.assertNotIn('progress',storage.hash_statistics(scope))
                    self.assertEqual(scope['progress'],{})

    def test_payload_hash_rejects_mutation_during_read_and_clears_scope_on_exception(self):
        with tempfile.TemporaryDirectory()as directory:
            path=Path(directory)/'packet.json';path.write_bytes(b'first')
            digest=storage.hashlib.file_digest
            def changed(stream,*args):
                result=digest(stream,*args);stamp=path.stat()
                with path.open('r+b')as writer:
                    writer.write(b'other');writer.flush();os.fsync(writer.fileno())
                os.utime(path,ns=(stamp.st_atime_ns,stamp.st_mtime_ns))
                return result
            with self.assertRaisesRegex(ValueError,'hash input changed'):
                with storage.payload_hash_operation()as scope,patch.object(storage.hashlib,'file_digest',side_effect=changed):
                    storage.sha(path,payload=True)
            self.assertIsNone(storage._hash_scope.get());self.assertEqual(scope['digests'],{})
            with patch.object(storage,'producer_processes',return_value=[]),patch.object(storage,'ARTIFACTS',Path(directory)), \
                    patch.object(storage,'discover',side_effect=ValueError('invalid receipt')):
                with self.assertRaisesRegex(ValueError,'invalid receipt'):storage.plan(Path(directory)/'Plan')
            self.assertIsNone(storage._hash_scope.get())

    def test_metadata_is_revalidated_and_retraversed_when_a_second_pin_contains_new_dependencies(self):
        with self.owned_workspace()as(_,source,output):
            output.mkdir();first=source/'first.bytes';first.write_bytes(b'first')
            second=source/'second.bytes';second.write_bytes(b'second')
            recipe=source/'recipe.json';storage.write(recipe,dict(input=dict(path=str(first),sha256=storage.sha(first),role='payload')))
            old_bytes=recipe.read_bytes();old_digest=storage.sha(recipe)
            storage.write(recipe,dict(input=dict(path=str(second),sha256=storage.sha(second),role='payload')))
            new_bytes=recipe.read_bytes();new_digest=storage.sha(recipe);recipe.write_bytes(old_bytes)
            owner=output/'ResourceAssembly.json';storage.write(owner,dict(inputs=[
                dict(path=str(recipe),sha256=old_digest,role='metadata'),dict(path=str(recipe),sha256=new_digest,role='metadata')]))
            reader=storage.read;versions=[]
            def changing(path):
                value=reader(path)
                if path==recipe:
                    versions.append(value)
                    if len(versions)==1:recipe.write_bytes(new_bytes)
                return value
            with storage.payload_hash_operation(),patch.object(storage,'read',side_effect=changing):
                issues=[];live=storage.receipt_references([owner],issues)[0]
            self.assertTrue({first,second}.issubset(live));self.assertEqual(issues,[]);self.assertEqual(len(versions),2)

    def test_generation_producer_identity_stays_fresh_across_receipt_seeds(self):
        with self.owned_workspace()as(_,source,output):
            output.mkdir();producer=source/'producer.py';producer.write_bytes(b'# producer')
            generation=output/'generation.json';storage.write(generation,dict(kind='CharacterResourceGeneration',schemaVersion=1,state='completed',outputs=[],
                generatorPath=str(producer),generatorSha256=storage.sha(producer)))
            owner=output/'ResourceAssembly.json';storage.write(owner,dict(input=dict(path=str(generation),sha256=storage.sha(generation),role='metadata')))
            digest=storage.hashlib.file_digest
            with storage.payload_hash_operation(),patch.object(storage.hashlib,'file_digest',wraps=digest)as hasher:
                for _ in range(2):storage.declared_live_inputs([dict(owner=owner)])
                self.assertEqual(sum(Path(call.args[0].name)==producer for call in hasher.call_args_list),2)

    def test_untyped_numeric_payload_is_promoted_once_across_seeds_while_metadata_keeps_fresh_dependencies(self):
        with self.owned_workspace()as(_,source,output):
            _,packet,_,_=self.legacy_generation(source,output);cache={};reader=storage.read
            first=source/'first.bytes';first.write_bytes(b'first');second=source/'second.bytes';second.write_bytes(b'second')
            metadata=source/'recipe.json';storage.write(metadata,dict(input=dict(path=str(first),sha256=storage.sha(first))))
            owner=output/'first-owner.json';other=output/'second-owner.json'
            document=lambda:dict(rawPacket=dict(path=str(packet),sha256=storage.sha(packet)),recipe=dict(path=str(metadata),sha256=storage.sha(metadata)))
            storage.write(owner,document())
            from collect_sources import validate_packet
            with storage.payload_hash_operation()as scope,patch.object(storage,'read',wraps=reader)as reads,patch('collect_sources.validate_packet',wraps=validate_packet)as validator:
                live,_=storage.receipt_references([owner],generation_cache=cache)
                storage.write(metadata,dict(input=dict(path=str(second),sha256=storage.sha(second))));storage.write(other,document())
                changed,_=storage.receipt_references([other],generation_cache=cache)
                self.assertEqual(sum(call.args[0]==packet for call in reads.call_args_list),1)
                self.assertEqual(sum(call.args[0]==metadata for call in reads.call_args_list),2)
                self.assertEqual(validator.call_count,1);self.assertGreaterEqual(scope['payloadCacheHits'],1)
            self.assertIn(first,live);self.assertIn(second,changed);self.assertNotIn(first,changed)
            self.assertIn('packetValidation',cache[storage.sha(packet)])

    def test_untyped_numeric_payload_revalidates_a_new_digest_and_preserves_a_mismatched_old_pin(self):
        with self.owned_workspace()as(_,source,output):
            _,packet,_,_=self.legacy_generation(source,output);cache={};old=storage.sha(packet)
            owner=output/'owner.json';storage.write(owner,dict(rawPacket=dict(path=str(packet),sha256=old)))
            from collect_sources import fingerprint,validate_packet
            with storage.payload_hash_operation(),patch('collect_sources.validate_packet',wraps=validate_packet)as validator:
                storage.receipt_references([owner],generation_cache=cache)
                changed=storage.read(packet);changed['vertices'][0]=.125;changed['sourceFingerprint']=fingerprint(changed)[0];storage.write(packet,changed)
                new=storage.sha(packet);storage.write(owner,dict(rawPacket=dict(path=str(packet),sha256=new)))
                issues=[];live,_=storage.receipt_references([owner],issues,generation_cache=cache)
                self.assertIn(packet,live);self.assertEqual(issues,[]);self.assertEqual(validator.call_count,2)
                storage.write(owner,dict(rawPacket=dict(path=str(packet),sha256=old)))
                issues=[];live,_=storage.receipt_references([owner],issues,generation_cache=cache)
                self.assertIn(packet.parent,live);self.assertEqual(issues[0]['reason'],'changed-retained-input')
                self.assertEqual(validator.call_count,2)
            self.assertTrue(cache[old]['packetValidation']);self.assertTrue(cache[new]['packetValidation'])

    def test_local_numeric_payload_is_owned_as_data_and_explicit_metadata_is_rejected_before_or_after_promotion(self):
        with self.owned_workspace()as(_,source,output):
            _,packet,_,_=self.legacy_generation(source,output);digest=storage.sha(packet);cache={}
            pin=dict(path=str(packet),sha256=digest)
            for existing in({},cache):
                with self.assertRaisesRegex(ValueError,'declared as reference metadata'):
                    storage.declared_local_payloads(output,dict(input=dict(pin,role='metadata')),generation_cache=existing)
                if not cache:
                    owned,guards=storage.declared_local_payloads(output,dict(input=pin),generation_cache=cache)
                    self.assertEqual(owned,{packet:digest});self.assertEqual(guards,[])
            owner=output/'owner.json';storage.write(owner,dict(input=dict(pin,role='metadata')))
            for existing in({},cache):
                with self.assertRaisesRegex(ValueError,'declared as reference metadata'):
                    storage.receipt_references([owner],generation_cache=existing,strict=True)

    def test_verified_typed_payload_authority_is_reused_for_untyped_binding_json_without_parsing_it(self):
        with self.owned_workspace()as(_,source,output):
            output.mkdir();trap=source/'not-a-dependency.bytes';trap.write_bytes(b'unowned')
            binding=output/'binding.json';storage.write(binding,dict(embeddedPath=str(trap)))
            digest=storage.sha(binding);generation=output/'generation.json'
            storage.write(generation,dict(kind='CharacterResourceGeneration',schemaVersion=1,state='completed',outputs=[dict(path=str(binding),sha256=digest,role='payload')]))
            first=output/'first-owner.json';storage.write(first,dict(input=dict(path=str(generation),sha256=storage.sha(generation),role='metadata')))
            other=output/'other-owner.json';storage.write(other,dict(input=dict(path=str(binding),sha256=digest)))
            cache={};reader=storage.read
            with storage.payload_hash_operation(),patch.object(storage,'read',wraps=reader)as reads:
                first_live,_=storage.receipt_references([first],generation_cache=cache)
                other_live,_=storage.receipt_references([other],generation_cache=cache)
                payloads,guards=storage.declared_local_payloads(output,storage.read(other),generation_cache=cache)
                self.assertFalse(any(call.args[0]==binding for call in reads.call_args_list))
            self.assertIn(binding,first_live);self.assertIn(binding,other_live);self.assertNotIn(trap,other_live)
            self.assertEqual(payloads,{binding:digest});self.assertEqual(guards,[])
            self.assertEqual(cache[digest],dict(typedPayloadAuthority=True))

    def test_typed_binding_authority_does_not_bypass_actual_numeric_packet_validation(self):
        with self.owned_workspace()as(_,source,output):
            generation,_,_,document=self.legacy_generation(source,output)
            binding=output/'binding.json';storage.write(binding,dict(bindingData=[1,2,3]));digest=storage.sha(binding)
            cache={digest:dict(typedPayloadAuthority=True)}
            invalid=dict(document,rows=[dict(document['rows'][0],generatedPacket=str(binding),generatedSha256=digest)])
            with self.assertRaisesRegex(ValueError,'Missing mesh channel'):
                storage.normalize_generation(generation,invalid,cache)
            self.assertNotIn('packetValidation',cache[digest])

    def test_typed_output_path_does_not_grant_payload_role_to_a_new_untyped_digest(self):
        with self.owned_workspace()as(_,source,output):
            output.mkdir();dependency=source/'replay.bytes';dependency.write_bytes(b'input')
            target=output/'document.json';storage.write(target,dict(binding=[1,2,3]));old=storage.sha(target)
            old_bytes=target.read_bytes();storage.write(target,dict(input=dict(path=str(dependency),sha256=storage.sha(dependency))))
            new=storage.sha(target);new_bytes=target.read_bytes();target.write_bytes(old_bytes)
            generation=output/'generation.json';storage.write(generation,dict(kind='CharacterResourceGeneration',schemaVersion=1,state='completed',outputs=[dict(path=str(target),sha256=old,role='payload')]))
            transition=output/'transition.json';storage.write(transition,{})
            owner=output/'owner.json';storage.write(owner,dict(first=dict(path=str(generation),sha256=storage.sha(generation)),
                transition=dict(path=str(transition),sha256=storage.sha(transition)),later=dict(path=str(target),sha256=new)))
            reader=storage.read
            def replace(path):
                value=reader(path)
                if path==transition:target.write_bytes(new_bytes)
                return value
            with storage.payload_hash_operation(),patch.object(storage,'read',side_effect=replace):
                issues=[];live,_=storage.receipt_references([owner],issues,generation_cache={})
            self.assertEqual(issues,[]);self.assertIn(dependency,live)

    def test_repeated_metadata_pin_is_read_once_per_unchanged_closure_with_fresh_role_and_scope_boundaries(self):
        with self.owned_workspace()as(_,source,output):
            output.mkdir();recipe=output/'geometry.json'
            storage.write(recipe,dict(policy='geometry-material',textures=[dict(packet=dict(vertexCount=1,weights=[1.]))]))
            pin=dict(path=str(recipe),sha256=storage.sha(recipe));document=dict(inputs=[pin]*132)
            owner=output/'owner.json';storage.write(owner,document);reader=storage.read
            digest=storage.hashlib.file_digest
            with storage.payload_hash_operation()as scope,patch.object(storage,'read',wraps=reader)as reads,patch.object(storage.hashlib,'file_digest',wraps=digest)as hashes:
                _,guards=storage.declared_local_payloads(output,document)
                self.assertEqual(guards,[pin])
                self.assertEqual(sum(Path(call.args[0].name)==recipe for call in hashes.call_args_list),1)
                self.assertEqual(sum(call.args[0]==recipe for call in reads.call_args_list),1)
                storage.receipt_references([owner])
                self.assertEqual(sum(Path(call.args[0].name)==recipe for call in hashes.call_args_list),2)
                self.assertEqual(sum(call.args[0]==recipe for call in reads.call_args_list),2)
                self.assertEqual(scope['verifiedReferenceReuses']+scope['inputLeaseReuses'],262)
                changed_role=dict(inputs=[pin,dict(pin,role='metadata')]);storage.write(owner,changed_role)
                storage.receipt_references([owner])
                self.assertEqual(sum(Path(call.args[0].name)==recipe for call in hashes.call_args_list),4)
                self.assertEqual(sum(call.args[0]==recipe for call in reads.call_args_list),4)
            with patch.object(storage,'file_hash_identity',return_value=None),patch.object(storage.hashlib,'file_digest',wraps=digest)as hashes:
                storage.declared_local_payloads(output,dict(inputs=[pin]*3))
                self.assertEqual(sum(Path(call.args[0].name)==recipe for call in hashes.call_args_list),3)

    def test_repeated_metadata_pin_detects_same_inode_size_mtime_preserving_edits_inside_each_closure(self):
        with self.owned_workspace()as(_,_,output):
            output.mkdir();recipe=output/'recipe.json';owner=output/'owner.json';reader=storage.read
            for collector in('local','retained'):
                storage.write(recipe,dict(value='first'));pin=dict(path=str(recipe),sha256=storage.sha(recipe))
                stamp=recipe.stat();document=dict(inputs=[pin,pin]);storage.write(owner,document)
                def changing(path):
                    result=reader(path)
                    if path==recipe:
                        with recipe.open('r+b')as stream:
                            content=recipe.read_bytes().replace(b'first',b'other');stream.write(content);stream.flush();os.fsync(stream.fileno())
                        os.utime(recipe,ns=(stamp.st_atime_ns,stamp.st_mtime_ns))
                    return result
                with storage.payload_hash_operation()as scope,patch.object(storage,'read',side_effect=changing):
                    if collector=='local':
                        with self.assertRaisesRegex(ValueError,'producer input changed'):storage.declared_local_payloads(output,document)
                    else:
                        issues=[];live,_=storage.receipt_references([owner],issues)
                        self.assertIn(output,live);self.assertEqual(issues[0]['reason'],'changed-retained-recipe')
                    self.assertEqual(scope['metadataHashes'],2);self.assertEqual(scope['verifiedReferenceReuses'],0)
                self.assertEqual(recipe.stat().st_ino,stamp.st_ino);self.assertEqual(recipe.stat().st_size,stamp.st_size)
                self.assertEqual(recipe.stat().st_mtime_ns,stamp.st_mtime_ns)

    def test_owned_output_walk_keeps_all_declared_child_names_and_preserves_reparse_boundaries(self):
        with self.owned_workspace()as(_,_,output):
            output.mkdir();(output/'SolastaUnfinishedBusiness.sln').write_text('generated project fixture','utf-8')
            packets=[]
            for name in('Tools','Python','Library','.native'):
                folder=output/name;folder.mkdir();packet=folder/'mesh.bin';packet.write_bytes(b'created mesh');packets.append(packet)
            numeric=output/'Library/ordinary.json';storage.write(numeric,dict(vertices=[1,2,3]))
            self.assertEqual(storage.work_files(output),set(packets))
            self.assertEqual(storage.work_files(output,all_created=True),{*packets,numeric,output/'SolastaUnfinishedBusiness.sln'})
            self.assertEqual(list(storage.walk(output)),[])
            original=Path.is_symlink
            with patch.object(Path,'is_symlink',lambda path:path==output/'Library'or original(path)):
                self.assertNotIn(packets[2],storage.work_files(output))
                self.assertNotIn(numeric,storage.work_files(output,all_created=True))

    def test_typed_numeric_json_is_a_terminal_payload_and_typed_metadata_keeps_its_references(self):
        with self.owned_workspace()as(_,source,output):
            output.mkdir();trap=source/'not-an-input.bytes';trap.write_bytes(b'unowned')
            packet=output/'ordinary-mesh.json';storage.write(packet,dict(fakeDiagnostic=str(trap)))
            metadata=output/'arbitrary.receipt';storage.write(metadata,dict(input=dict(path=str(packet),sha256=storage.sha(packet),role='payload')))
            owner=output/'ResourceAssembly.json';storage.write(owner,dict(proof=dict(path=str(metadata),sha256=storage.sha(metadata),role='metadata')))
            live=storage.declared_live_inputs([dict(owner=owner)])
            self.assertIn(packet,live);self.assertIn(metadata,live);self.assertNotIn(trap,live)
            payloads,guards=storage.declared_local_payloads(output,dict(input=dict(path=str(packet),sha256=storage.sha(packet),role='payload')))
            self.assertEqual(set(payloads),{packet});self.assertEqual(guards,[])

    def test_receipt_producer_records_numeric_json_failure_and_interrupted_outputs_without_claiming_baseline(self):
        with self.owned_workspace()as(root,source,output):
            input_path=source/'input.json';storage.write(input_path,dict(source=dict(path=str(source/'Catalog/hero.bundle'),sha256=storage.sha(source/'Catalog/hero.bundle'),role='payload')))
            output.mkdir();existing=output/'user.json';storage.write(existing,dict(keep=True))
            metadata=output/'result.json';packet=output/'named-packet.json'
            with patch.object(storage,'resource_operation',contextlib.nullcontext):
                with self.assertRaisesRegex(ValueError,'failed proof'):
                    with storage.owned_resource_work('receipt-test',input_path,output,source_role='receipt',metadata_outputs=[metadata]):
                        storage.write(packet,dict(vertices=[1,2,3]));storage.write(metadata,dict(status='failed'))
                        raise ValueError('failed proof')
            marker=storage.work_marker(output);record=storage.read(marker)
            self.assertEqual(record['state'],'failed');self.assertNotIn('sourceManifest',record)
            self.assertEqual({Path(pin['path']):pin['role']for pin in record['files']},{packet:'payload',metadata:'metadata'})
            self.assertIn(source/'Catalog/hero.bundle',storage.work_source_inputs(record))
            record.update(state='running',creator=dict(pid=2147483647,start=1),files=[]);storage.write(marker,record)
            with patch.object(storage,'producer_processes',return_value=[]):
                records,_,_,_=storage.discover_work([root],dict(modified=marker.stat().st_mtime_ns+1),set(),False)
            self.assertEqual(set(records[0]['owned_payloads']),{packet})
            self.assertIn(metadata,{Path(pin['path'])for pin in records[0]['guards']})
            self.assertTrue(existing.is_file())
            with self.assertRaisesRegex(ValueError,'metadata output escaped'):
                with storage.owned_resource_work('receipt-test',input_path,output,source_role='receipt',metadata_outputs=[output/'..'/'outside.json']):
                    self.fail('An escaped metadata owner reached its producer.')

    def test_standalone_groom_registers_before_the_first_write_and_records_failure(self):
        generator_path=Path(__file__).resolve().parents[2]/'blender-models/CharacterHair/GenerateFlowCoverageSweptProfile.py'
        generator_spec=importlib.util.spec_from_file_location('owned_groom_entrypoint',generator_path)
        generator=importlib.util.module_from_spec(generator_spec);generator_spec.loader.exec_module(generator)
        with tempfile.TemporaryDirectory()as temporary:
            root=Path(temporary).resolve();workspace=root/'tools/game-test/artifacts';workspace.mkdir(parents=True)
            input_path=workspace/'input.json';storage.write(input_path,{})
            output=workspace/'candidate'
            def fail_after_write(*_):
                self.assertTrue(storage.work_active(storage.read(storage.work_marker(output))))
                output.mkdir();storage.write(output/'packet.json',dict(vertices=[1,2,3]))
                raise ValueError('fixture failure')
            with patch.object(generator,'ROOT',root),patch.object(storage,'ROOT',root),patch.object(storage,'ARTIFACTS',workspace), \
                    patch.object(storage,'WORKSPACE',root/'tools/asset-authoring/workspace'),patch.dict(storage.checked_catalog_path.__globals__,ROOT=root), \
                    patch.object(storage,'resource_operation',contextlib.nullcontext),patch.object(generator,'generate_owned',side_effect=fail_after_write), \
                    patch.dict(storage.sys.modules,resource_storage=storage),patch.object(storage.sys,'argv',[str(generator_path),'--workspace',str(workspace),'--input',str(input_path),'--output',str(output)]):
                with self.assertRaisesRegex(ValueError,'fixture failure'):generator.main()
                record=storage.read(storage.work_marker(output))
                self.assertEqual(record['state'],'failed');self.assertEqual(record['sourceRole'],'receipt')
                self.assertEqual(record['files'][0]['role'],'payload');self.assertEqual(Path(record['files'][0]['path']),output/'packet.json')

    @staticmethod
    def legacy_generation(source,output):
        from collect_sources import fingerprint
        output.mkdir()
        packet=dict(name='OwnedMesh',nativeReadable=True,vertexCount=3,indexFormat=0,
                    vertices=[0.,0.,0.,1.,0.,0.,0.,1.,0.],normals=[0.,0.,1.]*3,
                    tangents=[],colors=[],bindPoses=[],weights=[],boneIndices=[],subMeshes=[[0,1,2]],uv=[],shapes=[],bounds=[.5,.5,0.,1.,1.,0.])
        packet['sourceFingerprint']=fingerprint(packet)[0]
        mesh=output/'source-derived.json';storage.write(mesh,packet)
        binding=output/'surface.bytes';binding.write_bytes(b'owned surface')
        producer=source/'producer.py';producer.write_text('# preserved producer snapshot','utf-8')
        input_path=source/'input.json';storage.write(input_path,{})
        owner=output/'numeric-result.json'
        document=dict(generatorPath=str(producer),generatorSha256=storage.sha(producer),sourceInput=str(input_path),sourceInputSha256=storage.sha(input_path),
                      rows=[dict(generatedPacket=str(mesh),generatedSha256=storage.sha(mesh),bindingPath=str(binding),bindingSha256=storage.sha(binding))])
        storage.write(owner,document)
        return owner,mesh,binding,document

    def test_pinned_recipe_discovers_legacy_numeric_owner_with_actual_mesh_validation_and_shared_hash_cache(self):
        with self.owned_workspace()as(root,source,output):
            generation,mesh,binding,document=self.legacy_generation(source,output)
            catalog=source/'ResourceAssembly.json';storage.write(catalog,dict(generation=dict(path=str(generation),sha256=storage.sha(generation))))
            from collect_sources import validate_packet
            cache={}
            with patch('collect_sources.validate_packet',wraps=validate_packet)as validator:
                records,retained=storage.discover_generations({source},[root],generation_cache=cache)
                live=storage.declared_live_inputs([dict(owner=catalog)],generation_cache=cache)
            self.assertEqual(retained,[]);self.assertEqual(len(records),1)
            self.assertEqual(set(records[0]['owned_payloads']),{mesh,binding});self.assertEqual(validator.call_count,1)
            self.assertTrue({generation,mesh,binding}.issubset(live))
            storage.write(source/'unlinked.json',document)
            records,retained=storage.discover_generations(set(),[root])
            self.assertEqual(records,[]);self.assertEqual(retained,[])
            outside=source/'outside.json';outside.write_bytes(mesh.read_bytes())
            escaped=dict(document,rows=[dict(document['rows'][0],generatedPacket=str(output/'..'/source.name/outside.name))])
            with self.assertRaises(ValueError):
                storage.normalize_generation(generation,escaped)

    def test_legacy_numeric_owner_keeps_changed_producer_or_invalid_packet(self):
        with self.owned_workspace()as(root,source,output):
            generation,mesh,_,document=self.legacy_generation(source,output)
            catalog=source/'ResourceAssembly.json';storage.write(catalog,dict(generation=dict(path=str(generation),sha256=storage.sha(generation))))
            Path(document['generatorPath']).write_text('# changed','utf-8')
            records,retained=storage.discover_generations({source},[root])
            self.assertEqual(records,[]);self.assertIn('producer or source changed',retained[0]['reason'])
            issues=[];live=storage.declared_live_inputs([dict(owner=catalog)],issues)
            self.assertIn(output,live);self.assertEqual(issues[0]['reason'],'unverified-retained-recipe')
            document['generatorSha256']=storage.sha(document['generatorPath']);storage.write(mesh,dict(vertices=[1,2,3]))
            document['rows'][0]['generatedSha256']=storage.sha(mesh);storage.write(generation,document)
            storage.write(catalog,dict(generation=dict(path=str(generation),sha256=storage.sha(generation))))
            records,retained=storage.discover_generations({source},[root])
            self.assertEqual(records,[]);self.assertIn('Missing mesh channel',retained[0]['reason'])

    def test_changed_retained_recipe_directory_protects_all_nested_payloads_in_retirement_plan(self):
        with self.owned_workspace()as(root,source,output):
            old=source/'Old';old.mkdir();payload=old/'mesh.json';storage.write(payload,dict(numeric=[1,2,3]))
            changed=old/'recipe.json';storage.write(changed,dict(changed=True))
            output.mkdir();owner=output/'ResourceAssembly.json';storage.write(owner,dict(input=dict(path=str(changed),sha256='0'*64)))
            manifest=source/'Catalog'/storage.RESOURCE_FILES['manifest']
            current=dict(directory=output,owner=owner,sha256=storage.sha(manifest),accepted=True,modified=2)
            retired=dict(directory=old,root=storage.ARTIFACTS,modified=1,owned_payloads={payload:storage.sha(payload)},guards=[],kind='generation')
            with patch.object(storage,'PUBLIC',manifest),patch.object(storage,'producer_processes',return_value=[]), \
                    patch.object(storage,'discover',return_value=([current],[])),patch.object(storage,'adopted_source_hashes',return_value=set()), \
                    patch.object(storage,'discover_work',return_value=([retired],[],[],set())), \
                    patch.object(storage,'create_declared_payload_plan',side_effect=lambda payloads,*_,**__:dict(files=list(payloads),logicalBytes=0)):
                result=storage.plan(storage.ARTIFACTS/'Plan')
            self.assertEqual(result['plans'][0]['files'],0)

    def test_retained_replay_follows_nested_receipts_and_recipe_snapshots(self):
        with self.owned_workspace()as(_,source,output):
            output.mkdir();payload=source/'unusually-named-source.bundle';payload.write_bytes(b'raw input')
            recipe=output/'recipe.json.source';storage.write(recipe,dict(textures=[dict(detailSource=dict(path=str(payload),sha256=storage.sha(payload)))]))
            receipt=output/'result.json';storage.write(receipt,dict(computationInputs=[dict(path=str(recipe),sha256=storage.sha(recipe))]))
            owner=output/'ResourceAssembly.json';storage.write(owner,dict(proof=dict(path=str(receipt),sha256=storage.sha(receipt))))
            issues=[];live=storage.declared_live_inputs([dict(owner=owner)],issues)
            self.assertTrue({payload,recipe,receipt}.issubset(live));self.assertEqual(issues,[])

    def test_retired_dependencies_require_matching_bytes_and_no_retained_owner(self):
        with self.owned_workspace(canonical=True)as(_,source,output):
            output.mkdir();owner=output/'ResourceAssembly.json';storage.write(owner,{})
            stage=source/'Discarded';stage.mkdir()
            first=stage/'intermediate.json';storage.write(first,dict(samples=[1.,2.,3.]))
            second=stage/'finished.bin';second.write_bytes(b'finished')
            first_sha=storage.sha(first)
            manifest=source/'Catalog'/storage.RESOURCE_FILES['manifest']
            current=dict(directory=output,owner=owner,sha256=storage.sha(manifest),accepted=True,modified=2)
            retired=dict(directory=stage,root=storage.ARTIFACTS,modified=3,
                owned_payloads={first:first_sha,second:storage.sha(second)},
                guards=[dict(path=str(first),sha256=first_sha)],kind='work')
            with patch.object(storage,'PUBLIC',manifest),patch.object(storage,'producer_processes',return_value=[]), \
                    patch.object(storage,'discover',return_value=([current],[])),patch.object(storage,'adopted_source_hashes',return_value=set()), \
                    patch.object(storage,'discover_work',return_value=([retired],[],[],set())), \
                    patch.object(storage,'declared_live_inputs')as live_inputs:
                for index,kept in enumerate(([],[str(first)])):
                    live_inputs.return_value=set(map(Path,kept))
                    result=storage.plan(storage.ARTIFACTS/('Retirement'+str(index)))
                    plan=storage.read(result['plans'][0]['plan'])
                    self.assertEqual({Path(row['path'])for row in plan['files']},{first,second}-set(map(Path,kept)))
                    self.assertTrue(all(row['role']=='payload'for row in plan['files']))
                live_inputs.return_value=set()
                retired['guards'][0]['sha256']='0'*64
                with self.assertRaisesRegex(ValueError,'conflicting producer input and output hashes'):
                    storage.plan(storage.ARTIFACTS/'Conflict')
            self.assertEqual(storage.read(first),dict(samples=[1.,2.,3.]))
            self.assertEqual(second.read_bytes(),b'finished')
            self.assertEqual((source/'Catalog/hero.bundle').read_bytes(),b'original')

    def test_retained_replay_reports_missing_data_and_preserves_changed_recipe_owner(self):
        with self.owned_workspace()as(_,source,output):
            output.mkdir();recipe=source/'recipe.json';storage.write(recipe,{})
            digest=storage.sha(recipe);storage.write(recipe,dict(changed=True))
            missing=source/'missing.bytes';owner=output/'ResourceAssembly.json'
            storage.write(owner,dict(inputs=[dict(path=str(recipe),sha256=digest),dict(path=str(missing),sha256='0'*64)]))
            issues=[];live=storage.declared_live_inputs([dict(owner=owner)],issues)
            self.assertIn(source,live);self.assertNotIn(missing,live)
            self.assertEqual({i['reason']for i in issues},{'changed-retained-recipe','missing-retained-input'})

    def test_retained_replay_resolves_relative_files_from_their_declaring_recipe(self):
        with self.owned_workspace()as(_,source,output):
            output.mkdir();packet=source/('a'*64+'.json');storage.write(packet,dict(vertices=[1,2,3]))
            recipe=source/'census.json';storage.write(recipe,dict(meshes=[dict(file=packet.name,sha256=storage.sha(packet))]))
            owner=output/'ResourceAssembly.json';storage.write(owner,dict(input=dict(path=str(recipe),sha256=storage.sha(recipe))))
            self.assertIn(packet,storage.declared_live_inputs([dict(owner=owner)]))

    def test_retained_replay_does_not_treat_bundle_asset_names_as_files(self):
        with self.owned_workspace()as(_,source,output):
            output.mkdir();owner=output/'ResourceAssembly.json'
            storage.write(owner,dict(assets=['Assets/Characters/Skin.png','Textures/Detail.bundle']))
            issues=[]
            self.assertEqual(storage.declared_live_inputs([dict(owner=owner)],issues),set())
            self.assertEqual(issues,[])

    def test_retained_replay_does_not_expand_unpinned_diagnostic_documents(self):
        with self.owned_workspace()as(_,source,output):
            output.mkdir();data=source/'diagnostic-only.bytes';data.write_bytes(b'not a replay input')
            diagnostic=source/'render.json';storage.write(diagnostic,dict(output=str(data)))
            owner=output/'ResourceAssembly.json';storage.write(owner,dict(diagnostic=str(diagnostic)))
            live=storage.declared_live_inputs([dict(owner=owner)])
            self.assertIn(diagnostic,live);self.assertNotIn(data,live)

    def test_retained_replay_expands_shared_pinned_recipe_after_a_plain_reference(self):
        with self.owned_workspace()as(_,source,output):
            output.mkdir();payload=source/'replay.bytes';payload.write_bytes(b'input')
            recipe=source/'recipe.json';storage.write(recipe,dict(source=str(payload)))
            owners=[output/'current.json',output/'previous.json']
            for owner in owners:storage.write(owner,dict(diagnostic=str(recipe),input=dict(path=str(recipe),sha256=storage.sha(recipe))))
            original_read=storage.read
            with patch.object(storage,'read',wraps=original_read)as reader:
                self.assertIn(payload,storage.declared_live_inputs([dict(owner=owner)for owner in owners]))
            self.assertEqual(sum(call.args[0]==recipe for call in reader.call_args_list),1)

    def test_retained_replay_rechecks_a_conflicting_recipe_pin(self):
        with self.owned_workspace()as(_,source,output):
            output.mkdir();recipe=source/'recipe.json';storage.write(recipe,{})
            owner=output/'ResourceAssembly.json';storage.write(owner,dict(inputs=[
                dict(path=str(recipe),sha256=storage.sha(recipe)),dict(path=str(recipe),sha256='0'*64)]))
            issues=[];live=storage.declared_live_inputs([dict(owner=owner)],issues)
            self.assertIn(source,live);self.assertEqual([i['reason']for i in issues],['changed-retained-recipe'])

    def test_whole_tool_inventory_counts_root_files_and_shared_payloads(self):
        with tempfile.TemporaryDirectory()as directory:
            root=Path(directory);(root/'A').mkdir();(root/'B').mkdir()
            (root/'entry.txt').write_bytes(b'abc');original=root/'A/payload.bundle';original.write_bytes(b'12345')
            os.link(original,root/'B/payload.bundle')
            with patch.object(storage,'_stored_size',side_effect=lambda _,info:info.st_size-1):
                result=storage.inventory_tools(root)
            self.assertEqual(result['files'],3)
            self.assertEqual(result['directories'],3)
            self.assertEqual(result['logicalBytes'],13)
            self.assertEqual(result['uniqueFileBytes'],8)
            types={row['extension']:row for row in result['extensions']}
            self.assertEqual(result['storedFileBytes'],6)
            self.assertEqual(result['exclusiveStoredFileBytes'],6)
            self.assertEqual(types['.bundle'],dict(extension='.bundle',files=2,logicalBytes=10,uniqueFileBytes=5,
                                                  storedFileBytes=4,exclusiveStoredFileBytes=4))
            self.assertEqual(types['.txt'],dict(extension='.txt',files=1,logicalBytes=3,uniqueFileBytes=3,
                                               storedFileBytes=2,exclusiveStoredFileBytes=2))
            self.assertTrue(all(row['exclusiveStoredFileBytes']==0 for row in result['groups']))

    @contextlib.contextmanager
    def owned_workspace(self,*,canonical=False):
        with tempfile.TemporaryDirectory()as directory:
            root=Path(directory).resolve();artifacts=root/('tools/game-test/artifacts'if canonical else'artifacts');source=artifacts/'Source';output=artifacts/'Candidate'
            workspace=root/('tools/asset-authoring/workspace'if canonical else'workspace')
            (source/'Catalog').mkdir(parents=True);original=source/'Catalog/hero.bundle';original.write_bytes(b'original')
            storage.write(source/(Path('Catalog')/storage.RESOURCE_FILES['manifest']),dict(bundles=[dict(file=original.name,sha256=storage.sha(original))],meshes=[]))
            with patch.object(storage,'ROOT',root),patch.object(storage,'ARTIFACTS',artifacts),patch.object(storage,'WORKSPACE',workspace), \
                    patch.dict(storage.checked_catalog_path.__globals__,ROOT=root):
                yield root,source,output

    def test_workspace_output_is_claimed_when_the_command_has_no_output_argument(self):
        with self.owned_workspace()as(_,source,output):
            with storage.resource_work('workspace-command',['--source',str(source)],default_output=output):
                output.mkdir();(output/'generated.bin').write_bytes(b'workspace result')
                self.assertTrue(storage.work_active(storage.read(storage.work_marker(output))))
            record=storage.read(storage.work_marker(output))
            self.assertEqual(record['state'],'completed');self.assertEqual(Path(record['output']),output)
            self.assertEqual([Path(row['path']).name for row in record['files']],['generated.bin'])
            self.assertEqual(record['sourceRole'],'catalog')
            reference=source/'numeric.json';storage.write(reference,dict(kind='SourceMeshInput'))
            numeric=output.parent/'Numeric'
            with storage.resource_work('numeric-command',['--input',str(reference)],default_output=numeric):
                numeric.mkdir();(numeric/'packet.json').write_text('{"vertices":[0,0,0]}',encoding='utf-8')
            receipt=storage.read(storage.work_marker(numeric))
            self.assertEqual(receipt['sourceRole'],'receipt')
            self.assertEqual([Path(row['path']).name for row in receipt['files']],['packet.json'])
            guides=output.parent/'Guides'
            with storage.resource_work('guide-command',['--census',str(reference),'--output',str(guides)],
                    receipt_flag='--census',metadata_names=('Preparation.json',)):
                guides.mkdir()
                storage.write(guides/'Preparation.json',dict(status='complete'))
                storage.write(guides/'Profiles.json',dict(profiles=[]))
            declared=storage.read(storage.work_marker(guides))
            self.assertEqual({Path(row['path']).name:row['role'] for row in declared['files']},
                {'Preparation.json':'metadata','Profiles.json':'payload'})

    def test_explicit_output_wins_and_read_only_commands_do_not_claim_the_default_workspace(self):
        with self.owned_workspace()as(_,source,output):
            explicit=output.parent/'Explicit'
            with storage.resource_work('explicit-command',['--source',str(source),'--output',str(explicit)],default_output=output):
                explicit.mkdir();(explicit/'result.bin').write_bytes(b'explicit result')
            self.assertEqual(Path(storage.read(storage.work_marker(explicit))['output']),explicit)
            self.assertFalse(storage.work_marker(output).exists());self.assertFalse(output.exists())
            with storage.resource_work('inspect',[],default_output=output):pass
            self.assertFalse(storage.work_marker(output).exists());self.assertFalse(output.exists())

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

    def test_completed_work_does_not_keep_its_inputs_without_an_explicit_retained_owner(self):
        with self.owned_workspace()as(root,source,output):
            output.mkdir()
            with storage.resource_work('test',['--source',str(source),'--output',str(output)]):
                generated=output/'candidate.bin';generated.write_bytes(b'candidate')
            current=dict(modified=0,retainedDirectories=set())
            with patch.object(storage,'producer_processes',return_value=[]):
                records,_,_,live=storage.discover_work([root],current,set(),False)
            self.assertIn(generated,{path for record in records for path in record['owned_payloads']})
            self.assertNotIn(source/'Catalog/hero.bundle',live)
            current['retainedDirectories']={output}
            with patch.object(storage,'producer_processes',return_value=[]):
                _,_,_,live=storage.discover_work([root],current,set(),False)
            self.assertIn(source/'Catalog/hero.bundle',live)
            self.assertEqual(generated.read_bytes(),b'candidate')

    def test_running_work_keeps_its_declared_source_closure(self):
        with self.owned_workspace()as(root,source,output):
            with storage.resource_work('test',['--source',str(source),'--output',str(output)]):
                with patch.object(storage,'producer_processes',return_value=[]):
                    records,_,retained,live=storage.discover_work([root],dict(modified=0,retainedDirectories=set()),set(),False)
                self.assertEqual(records,[])
                self.assertIn(source/'Catalog/hero.bundle',live)
                self.assertEqual(retained[0]['reason'],'active-resource-producer')

    def test_retired_generation_without_catalog_owns_only_its_declared_payload(self):
        with self.owned_workspace()as(root,_,output):
            output.mkdir()
            data=output/'render.bin'
            neighbor=output/'original.bin'
            data.write_bytes(b'generated')
            neighbor.write_bytes(b'original')
            receipt=output/'Generation.json'
            storage.write(receipt,dict(kind='CharacterResourceGeneration',schemaVersion=1,state='completed',
                outputs=[dict(path=str(data),sha256=storage.sha(data),role='payload')]))
            current=dict(modified=0,retainedDirectories=set())
            with patch.object(storage,'producer_processes',return_value=[]):
                records,_,_,_=storage.discover_work([root],current,set(),False)
                self.assertEqual([row['owned_payloads'] for row in records],[{data:storage.sha(data)}])
                current['retainedDirectories']={output}
                records,_,_,_=storage.discover_work([root],current,set(),False)
                self.assertEqual(records,[])
            self.assertEqual(neighbor.read_bytes(),b'original')

    def test_unchanged_catalog_link_uses_the_same_pinned_manifest(self):
        with self.owned_workspace()as(_,source,output):
            src=source/'Catalog/hero.bundle'
            manifest=source/'Catalog'/storage.RESOURCE_FILES['manifest']
            target=output/'Catalog/hero.bundle'
            target.parent.mkdir(parents=True)
            target.hardlink_to(src)
            with patch.object(storage,'sha',side_effect=AssertionError('payload reading was redundant')):
                matches=storage.shared_catalog_payloads(manifest,output,{target},storage.hashlib.sha256(manifest.read_bytes()).hexdigest())
            self.assertEqual(matches,{target:storage.sha(src)})

    def test_retention_rechecks_changed_inputs_in_directories_with_retirement(self):
        with self.owned_workspace() as (_, source, output):
            unaffected = source / 'Catalog/hero.bundle'
            retiring = output / 'retiring'
            retiring.mkdir(parents=True)
            neighbor = retiring / 'neighbor.bundle'
            neighbor.write_bytes(b'original')
            owner = output / 'ResourceAssembly.json'
            storage.write(owner, [dict(path=str(path), sha256=storage.sha(path), role='payload')
                                  for path in (unaffected, neighbor)])
            neighbor.write_bytes(b'changed')
            issues = []
            with storage.payload_hash_operation() as scope:
                retained = storage.declared_live_inputs([dict(owner=owner)], issues,
                                                        retirement_ancestors={retiring})
                self.assertEqual(scope['payloadHashes'], 1)
            self.assertEqual(retained, {unaffected, retiring})
            self.assertEqual(issues, [dict(path=str(neighbor), reason='changed-retained-input')])

    def test_repeated_payloads_outside_retirement_share_path_validation(self):
        with self.owned_workspace() as (_, source, output):
            payload = source / 'Catalog/hero.bundle'
            other = source / 'Catalog/unique.bin'
            other.write_bytes(b'unique')
            owner = output / 'ResourceAssembly.json'
            pin = dict(path=str(payload), sha256=storage.sha(payload), role='payload')
            isolated_pin = dict(path=str(other), sha256=storage.sha(other), role='payload')
            storage.write(owner, {'inputs': [pin] * 200 + [isolated_pin]})
            with patch.object(storage, 'checked_source_path', wraps=storage.checked_source_path) as checked:
                retained = storage.declared_live_inputs([dict(owner=owner)], retirement_ancestors={output})
            self.assertEqual(retained, {payload, other})
            self.assertEqual(sum(call.args == (payload,) for call in checked.call_args_list), 2)
            self.assertEqual(sum(call.args == (other,) for call in checked.call_args_list), 1)

    def test_retained_payload_path_is_checked_again_before_retirement(self):
        with self.owned_workspace() as (_, source, output):
            payload = source / 'Catalog/hero.bundle'
            owner = output / 'ResourceAssembly.json'
            pin = dict(path=str(payload), sha256=storage.sha(payload), role='payload')
            storage.write(owner, {'inputs': [pin, pin]})
            original_check = storage.checked_source_path
            opened = False

            def remove_after_first_check(path):
                nonlocal opened
                if Path(path) == payload:
                    if opened:
                        payload.unlink()
                    opened = True
                return original_check(path)

            with patch.object(storage, 'checked_source_path', side_effect=remove_after_first_check):
                with self.assertRaisesRegex(storage._ReceiptBatchChanged, 'retained payload path changed'):
                    storage.declared_live_inputs([dict(owner=owner)], retirement_ancestors={output})

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

    def test_partial_catalog_shared_parse_cache_rechecks_metadata_and_follows_changed_dependencies(self):
        with self.owned_workspace()as(_,source,output):
            (output/'Catalog').mkdir(parents=True);copied=output/'Catalog/hero.bundle';copied.write_bytes(b'copied')
            extra=output/'Catalog/another.bundle';extra.write_bytes(b'other copied source')
            source_extra=source/'Catalog/another.bundle';source_extra.write_bytes(b'other native source')
            manifest=source/'Catalog'/storage.RESOURCE_FILES['manifest']
            storage.write(output/'Inputs/job.json',dict(source=str(source/'Catalog/hero.bundle'),sha256=storage.sha(source/'Catalog/hero.bundle'),target=str(copied)))
            cache={};reader=storage.read
            with storage.payload_hash_operation(),patch.object(storage,'read',wraps=reader)as reads:
                self.assertEqual(set(storage.partial_catalog(output,cache)['owned_payloads']),{copied})
                self.assertEqual(set(storage.partial_catalog(output,cache)['owned_payloads']),{copied})
                self.assertEqual(sum(call.args[0]==manifest for call in reads.call_args_list),1)
                changed=reader(manifest);changed['bundles'].append(dict(file=source_extra.name,sha256=storage.sha(source_extra)))
                storage.write(manifest,changed)
                self.assertEqual(set(storage.partial_catalog(output,cache)['owned_payloads']),{copied,extra})
                self.assertEqual(sum(call.args[0]==manifest for call in reads.call_args_list),2)
            self.assertEqual(len(cache),2)

    def test_partial_catalog_shared_parse_cache_rejects_changed_pinned_source(self):
        with self.owned_workspace()as(_,source,output):
            (output/'Catalog').mkdir(parents=True);(output/'Catalog/hero.bundle').write_bytes(b'copied')
            manifest=source/'Catalog'/storage.RESOURCE_FILES['manifest'];expected=storage.sha(manifest)
            storage.write(output/'WorkingDirectory.json',dict(kind='OwnedFacePreparation',source=str(source),catalogSha256=expected,inputs=[]))
            cache={};storage.partial_catalog(output,cache)
            changed=storage.read(manifest);changed['bundles']=[];storage.write(manifest,changed)
            with self.assertRaisesRegex(ValueError,'source manifest changed'):storage.partial_catalog(output,cache)
            self.assertEqual(len(cache),1)

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

    @unittest.skipUnless(os.name=='nt','Windows process census')
    def test_process_census_uses_the_interpreter_entrypoint_not_script_valued_options(self):
        own=os.getpid();producer=str(storage.ROOT/'tools'/'resource-job.py')
        outside=str(Path(storage.ROOT.anchor)/'editor'/'server.py')
        cases=[
            ('-m unittest discover -p "'+producer+'"',False),
            ('-munittest discover -p "'+producer+'"',False),
            ('-X utf8 -W ignore "'+producer+'" --output report',True),
            ('-Xutf8 -Wignore "'+producer+'"',True),
            ('--check-hash-based-pycs default "'+producer+'"',True),
            ('-- "'+str(storage.ROOT/'tools'/'extensionless-job')+'"',True),
            ('"'+outside+'" --reference "'+producer+'"',False),
            ('"'+outside+'" -c --reference "'+producer+'"',False),
            ('-c "pass"',True),
            ('-cpass',True),
            ('-',True),
            ('-W ignore',False)]
        rows=[dict(ProcessId=own,ParentProcessId=0,CommandLine='python.exe -m unittest')]
        rows.extend(dict(ProcessId=own+index+1,ParentProcessId=0,CommandLine='python.exe '+command)
            for index,(command,_)in enumerate(cases))
        with patch.object(storage.subprocess,'run',return_value=type('Result',(),dict(stdout=json.dumps(rows)))()):
            actual=storage.producer_processes()
        self.assertEqual(actual,[own+index+1 for index,(_,active)in enumerate(cases)if active])

    def test_copied_art_flag_does_not_accept_a_changed_catalog(self):
        with tempfile.TemporaryDirectory() as directory:
            path=Path(directory).resolve()/"ArtAcceptance.json"
            storage.write(path,dict(status="PASS_NATIVE_APPEARANCE_REVIEW",candidate=dict(sha256="reviewed")))
            assembly=dict(artAccepted=True,artAcceptance=dict(path=str(path),sha256=storage.sha(path)))
            self.assertTrue(storage.resource_art_acceptance(assembly,"reviewed"))
            self.assertFalse(storage.resource_art_acceptance(assembly,"changed-candidate"))
            path.write_text("changed")
            self.assertFalse(storage.resource_art_acceptance(assembly,"reviewed"))

    def test_adoption_protects_current_and_previous_without_retaining_newer_disposable_work(self):
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
        self.assertEqual(retired, [old, rejected, new])

    def test_retained_work_uses_explicit_owner_boundaries_instead_of_creation_time(self):
        root=Path(tempfile.gettempdir())/'resource-retention'
        current=dict(modified=30,retainedDirectories={root/'ongoing'})
        self.assertTrue(storage.retained_work(current,root/'ongoing',1))
        self.assertTrue(storage.retained_work(current,root/'ongoing'/'fields',1))
        self.assertFalse(storage.retained_work(current,root,1))
        self.assertFalse(storage.retained_work(current,root/'ongoing-copy',100))
        self.assertFalse(storage.retained_work(current,root/'completed-candidate',100))

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

    @staticmethod
    def evict_reference_versions(batch):
        for key in list(batch.versions):batch.versions.pop(key,None)


@unittest.skipUnless(os.name=='nt','Windows file-sharing read leases')
class ResourceReadLeaseTest(unittest.TestCase):
    @staticmethod
    def native_handle_count():
        import ctypes
        kernel=ctypes.WinDLL('kernel32',use_last_error=True)
        kernel.GetCurrentProcess.restype=ctypes.c_void_p
        kernel.GetProcessHandleCount.argtypes=[ctypes.c_void_p,ctypes.POINTER(ctypes.c_uint32)]
        count=ctypes.c_uint32()
        if not kernel.GetProcessHandleCount(kernel.GetCurrentProcess(),ctypes.byref(count)):raise ctypes.WinError(ctypes.get_last_error())
        return count.value

    @contextlib.contextmanager
    def workspace(self):
        with ResourceStorageTest.owned_workspace(self)as(_,_,output):
            output.mkdir();yield output
    def test_actual_collectors_hold_snapshot_skip_path_rechecks_and_release_after_success(self):
        with self.workspace()as root:
            recipe=root/'recipe.json';storage.write(recipe,dict(value='first'));pin=dict(path=str(recipe),sha256=storage.sha(recipe))
            owner=root/'owner.json';document=dict(inputs=[pin]*132);storage.write(owner,document)
            for mode in('local','retained'):
                probes=[];original_leased=storage.leased_reference;original_reference=storage.reference_hash;original_checked=storage.checked_source_path;original_is_file=Path.is_file;reader=storage.read
                def referenced(*args,**kwargs):
                    result=original_reference(*args,**kwargs);value=original_leased(*args[:4])
                    if value is not None and not probes:
                        with self.assertRaises(OSError):recipe.write_bytes(b'changed')
                        with self.assertRaises(OSError):recipe.rename(root/'renamed.json')
                        probes.append(True)
                    return result
                def checked(path):
                    if Path(path)==recipe and probes:raise AssertionError('A held reference repeated full path validation')
                    return original_checked(path)
                def is_file(path):
                    if path==recipe and probes:raise AssertionError('A held reference repeated file existence validation')
                    return original_is_file(path)
                with storage.payload_hash_operation()as scope,patch.object(storage,'reference_hash',side_effect=referenced),patch.object(storage,'checked_source_path',side_effect=checked),patch.object(Path,'is_file',is_file),patch.object(storage,'read',wraps=reader)as reads:
                    if mode=='local':storage.declared_local_payloads(root,document)
                    else:storage.receipt_references([owner],strict=True)
                    self.assertEqual(sum(Path(call.args[0])==recipe for call in reads.call_args_list),1)
                    self.assertEqual(scope['inputLeaseAcquisitions'],1);self.assertEqual(scope['inputLeaseReuses'],131)
                self.assertTrue(probes);storage.write(recipe,dict(value='first'))
    def test_actual_collectors_release_snapshots_when_a_later_reference_raises(self):
        with self.workspace()as root:
            recipe=root/'recipe.json';storage.write(recipe,dict(value='first'));pin=dict(path=str(recipe),sha256=storage.sha(recipe))
            owner=root/'owner.json';document=dict(inputs=[pin]*3);storage.write(owner,document)
            original=storage.reference_hash
            def fail(*args,**kwargs):
                result=original(*args,**kwargs);value=storage.leased_reference(*args[:4])
                if value is not None:raise ValueError('later reference failed')
                return result
            for mode in('local','retained'):
                with patch.object(storage,'reference_hash',side_effect=fail):
                    with self.assertRaisesRegex(ValueError,'later reference failed'):
                        if mode=='local':storage.declared_local_payloads(root,document)
                        else:storage.receipt_references([owner],strict=True)
                storage.write(recipe,dict(value='first'))
    def test_completed_receipt_edges_skip_local_path_work_only_under_the_same_read_lease(self):
        with self.workspace()as root:
            dependency=root/'dependency.bytes';dependency.write_bytes(b'input')
            recipe=root/'recipe.json';storage.write(recipe,dict(input=dict(path=str(dependency),sha256=storage.sha(dependency),role='payload')))
            owner=root/'owner.json';storage.write(owner,dict(inputs=[dict(path=str(recipe),sha256=storage.sha(recipe),role='metadata')]*132))
            is_absolute=storage.os.path.isabs;reference=storage.reference_hash;reader=storage.read;seen=[]
            def referenced(*args,**kwargs):
                result=reference(*args,**kwargs);value=storage.leased_reference(*args[:4])
                if value is not None and Path(args[0])==recipe:seen.append(True)
                return result
            def classify(value):
                if value==str(recipe)and seen:raise AssertionError('A completed leased edge repeated local path classification')
                return is_absolute(value)
            with storage.payload_hash_operation()as scope,patch.object(storage,'reference_hash',side_effect=referenced), \
                    patch.object(storage.os.path,'isabs',side_effect=classify),patch.object(storage,'read',wraps=reader)as reads:
                live,_=storage.receipt_references([owner],strict=True)
                self.assertEqual(live,{recipe,dependency});self.assertTrue(seen)
                self.assertEqual(sum(Path(call.args[0])==recipe for call in reads.call_args_list),1)
                self.assertEqual(scope['inputLeaseAcquisitions'],1);self.assertEqual(scope['inputLeaseReuses'],131)
            recipe.write_bytes(b'closed lease permits writes')

    def test_receipt_edge_eviction_rechecks_changed_metadata_and_preserves_its_previous_closure(self):
        with self.workspace()as root:
            first=root/'first.bytes';first.write_bytes(b'first');second=root/'second.bytes';second.write_bytes(b'second')
            recipe=root/'recipe.json';storage.write(recipe,dict(input=dict(path=str(first),sha256=storage.sha(first),role='payload')))
            other=root/'other.json';storage.write(other,dict(value='other'))
            last=root/'last.json';storage.write(last,dict(value='last'))
            pin=lambda path:dict(path=str(path),sha256=storage.sha(path),role='metadata')
            original=pin(recipe);owner=root/'owner.json'
            storage.write(owner,dict(inputs=[original]*3+[pin(other)]*3+[pin(last)]*3+[original]))
            reader=storage.read;versions=storage.ReferenceVersions
            class OneLease(versions):
                def __init__(self):super().__init__(1)
            def changing(path):
                value=reader(path)
                if path==last:storage.write(recipe,dict(input=dict(path=str(second),sha256=storage.sha(second),role='payload')))
                return value
            with storage.payload_hash_operation()as scope,patch.object(storage,'ReferenceVersions',OneLease),patch.object(storage,'read',side_effect=changing):
                issues=[];live,_=storage.receipt_references([owner],issues,strict=True)
            self.assertIn(first,live);self.assertNotIn(second,live);self.assertIn(root,live)
            self.assertEqual(issues,[dict(path=str(recipe),reason='changed-retained-recipe')])
            self.assertGreaterEqual(scope['inputLeaseEvictions'],2)

    def test_receipt_raw_edges_distinguish_relative_bases_and_reset_between_closures(self):
        with self.workspace()as root:
            owners=[];inputs=[]
            for name in('first','second'):
                base=root/name;base.mkdir();path=base/'input.bytes';path.write_bytes(b'input')
                pin=dict(path='input.bytes',sha256=storage.sha(path),role='payload')
                owner=base/'owner.json';storage.write(owner,dict(inputs=[pin]*132));owners.append(owner);inputs.append(path)
            with storage.payload_hash_operation():
                live,_=storage.receipt_references(owners,strict=True)
                self.assertEqual(live,set(inputs))
                inputs[0].write_bytes(b'other');issues=[]
                changed,_=storage.receipt_references(owners,issues,strict=True)
            self.assertEqual(issues,[dict(path=str(inputs[0]),reason='changed-retained-input')])
            self.assertIn(inputs[0].parent,changed);self.assertIn(inputs[1],changed)

    def test_receipt_raw_edges_reclassify_untyped_json_after_typed_payload_promotion(self):
        with self.workspace()as root:
            dependency=root/'dependency.bytes';dependency.write_bytes(b'input')
            recipe=root/'recipe.json';storage.write(recipe,dict(input=dict(path=str(dependency),sha256=storage.sha(dependency),role='payload')))
            plain=dict(path=str(recipe),sha256=storage.sha(recipe));payload=dict(plain,role='payload');metadata=dict(plain,role='metadata')
            owner=root/'owner.json';storage.write(owner,dict(inputs=[plain]*4+[payload]*4+[plain]*4+[metadata]*4))
            selector=storage.pin_role;selections=[];reader=storage.read
            def selected(path,pin=None,**kwargs):
                value=selector(path,pin,**kwargs)
                if Path(path)==recipe:selections.append(value)
                return value
            with storage.payload_hash_operation(),patch.object(storage,'pin_role',side_effect=selected),patch.object(storage,'read',wraps=reader)as reads:
                live,_=storage.receipt_references([owner],strict=True)
            self.assertEqual(live,{recipe,dependency});self.assertIn('metadata',selections);self.assertIn('payload',selections)
            self.assertEqual(sum(Path(call.args[0])==recipe for call in reads.call_args_list),2)
    def test_handle_and_descriptor_transfer_failures_are_closed(self):
        import msvcrt
        with self.workspace()as root:
            path=root/'source.json';path.write_bytes(b'input');storage._lease_file_api()
            for target in('handle','descriptor'):
                with self.subTest(target=target):
                    before=self.native_handle_count()
                    owner=msvcrt if target=='handle'else storage.os;name='open_osfhandle'if target=='handle'else'fdopen'
                    with patch.object(owner,name,side_effect=OSError('transfer failure')):self.assertIsNone(storage._open_read_lease(path))
                    self.assertEqual(self.native_handle_count(),before)
            before=self.native_handle_count()
            with patch.object(storage.os,'fdopen',side_effect=MemoryError('stream allocation')):
                with self.assertRaises(MemoryError):storage._open_read_lease(path)
            self.assertEqual(self.native_handle_count(),before)
    def test_identity_query_failure_closes_unadopted_lease_and_falls_back(self):
        with self.workspace()as root:
            path=root/'source.json';path.write_bytes(b'input');digest=storage.sha(path);versions=storage.ReferenceVersions()
            try:
                storage.checked_reference_hash(path,digest,None,versions)
                before=self.native_handle_count();original=storage.file_hash_identity;calls=[0]
                def fail_once(stream):
                    calls[0]+=1
                    if calls[0]==1:raise OSError('identity query failure')
                    return original(stream)
                with patch.object(storage,'file_hash_identity',side_effect=fail_once):self.assertEqual(storage.checked_reference_hash(path,digest,None,versions)[1],digest)
                self.assertEqual(self.native_handle_count(),before);self.assertEqual(versions.held_leases,0)
            finally:versions.close()
    def test_closure_close_attempts_every_owned_stream_after_one_close_fails(self):
        class Stream:
            def __init__(self,failure=False):self.closed=False;self.failure=failure
            def close(self):
                self.closed=True
                if self.failure:raise OSError('close failed')
        versions=storage.ReferenceVersions();streams=[Stream(True),Stream()]
        for index,stream in enumerate(streams):versions[index]=dict(lease=stream)
        with self.assertRaisesRegex(OSError,'close failed'):versions.close()
        self.assertTrue(all(stream.closed for stream in streams));self.assertEqual(versions,{})
    def test_existing_writer_fallback_still_detects_a_later_metadata_change(self):
        with self.workspace()as root:
            recipe=root/'recipe.json';storage.write(recipe,dict(value='first'));pin=dict(path=str(recipe),sha256=storage.sha(recipe))
            owner=root/'owner.json';document=dict(inputs=[pin]*3);storage.write(owner,document)
            original=storage.reference_hash;calls=[0]
            with recipe.open('r+b')as writer:
                def observed(*args,**kwargs):
                    result=original(*args,**kwargs);calls[0]+=1
                    if calls[0]==2:
                        writer.seek(0);writer.write(recipe.read_bytes().replace(b'first',b'other'));writer.flush();os.fsync(writer.fileno())
                    return result
                with storage.payload_hash_operation()as scope,patch.object(storage,'reference_hash',side_effect=observed):
                    issues=[];live,_=storage.receipt_references([owner],issues)
                    self.assertIn(root,live);self.assertEqual(issues[0]['reason'],'changed-retained-recipe')
                    self.assertEqual(scope['inputLeaseAcquisitions'],0);self.assertEqual(scope['metadataHashes'],2)
    def test_collector_lease_capacity_falls_back_and_local_aliases_do_not_read_outside_owner(self):
        with self.workspace()as root:
            output=root/'output';output.mkdir();paths=[output/'one.json',output/'two.json']
            for path in paths:storage.write(path,dict(value='input'))
            pins=[dict(path=str(path),sha256=storage.sha(path))for path in paths]
            original=storage.ReferenceVersions
            class OneLease(original):
                def __init__(self):super().__init__(1)
            with patch.object(storage,'ReferenceVersions',OneLease),storage.payload_hash_operation()as scope:
                storage.declared_local_payloads(output,dict(inputs=[pins[0],pins[0],pins[1],pins[1]]))
                self.assertEqual(scope['inputLeaseAcquisitions'],2);self.assertEqual(scope['maximumInputLeases'],1)
            outside=root/'outside.json';storage.write(outside,{})
            outside_digest=storage.sha(outside)
            with patch.object(storage,'sha',wraps=storage.sha)as hashes:
                with self.assertRaisesRegex(ValueError,'regular local file'):
                    storage.declared_local_payloads(output,dict(input=dict(path=str(output/'..'/outside.name),sha256=outside_digest)))
                self.assertFalse(any(Path(call.args[0])==outside for call in hashes.call_args_list))
    def test_lru_rotates_after_35_metadata_keys_keeps_late_hot_keys_and_revalidates_evicted_changes(self):
        with self.workspace()as root:
            paths=[root/f'fixture-{index}.json'for index in range(35)]
            for path in paths:storage.write(path,dict(value='first'))
            digests=[storage.sha(path)for path in paths];versions=storage.ReferenceVersions(32)
            try:
                for index,path in enumerate(paths):
                    for _ in range(2):self.assertEqual(storage.checked_reference_hash(path,digests[index],None,versions)[1],digests[index])
                    if index>0:self.assertEqual(storage.checked_reference_hash(paths[0],digests[0],None,versions)[1],digests[0])
                self.assertEqual(versions.held_leases,32)
                self.assertIsNotNone(storage.leased_reference(paths[-1],digests[-1],None,versions))
                self.assertIsNotNone(storage.leased_reference(paths[0],digests[0],None,versions))
                with patch.object(storage,'checked_source_path',side_effect=AssertionError('Hot leased key repeated path validation')),patch.object(storage,'file_hash_identity',side_effect=AssertionError('Hot leased key repeated identity query')):
                    for _ in range(100):self.assertEqual(storage.checked_reference_hash(paths[-1],digests[-1],None,versions)[1],digests[-1])
                evicted=next(index for index,path in enumerate(paths)if storage.leased_reference(path,digests[index],None,versions)is None)
                storage.write(paths[evicted],dict(value='other'))
                self.assertNotEqual(storage.checked_reference_hash(paths[evicted],digests[evicted],None,versions)[1],digests[evicted])
                held=[entry['lease']for entry in versions.values()if entry.get('lease')is not None]
            finally:versions.close()
            self.assertTrue(all(stream.closed for stream in held))
            for path in paths:storage.write(path,dict(value='after'))


    def test_generic_input_leases_cover_png_binary_and_both_json_roles_without_changing_traversal(self):
        from PIL import Image
        with self.workspace()as root:
            trap=root/'dependency.bytes';trap.write_bytes(b'dependency')
            cases=[(None,'.png'),('payload','.png'),('payload','.custom'),('payload','.json'),('metadata','.json')]
            for index,(role,suffix)in enumerate(cases):
                path=root/f'input-{index}{suffix}'
                if suffix=='.png':Image.new('RGB',(2,2),(31,71,97)).save(path)
                elif suffix=='.json':storage.write(path,dict(input=dict(path=str(trap),sha256=storage.sha(trap),role='payload')))
                else:path.write_bytes(b'typed arbitrary binary')
                original_bytes=path.read_bytes();pin=dict(path=str(path),sha256=storage.sha(path))
                if role is not None:pin['role']=role
                document=dict(inputs=[pin]*132);owner=root/'owner.json';storage.write(owner,document)
                for mode in('local','retained'):
                    seen=[];reference=storage.reference_hash;checked=storage.checked_source_path;reader=storage.read
                    def probe(*args,**kwargs):
                        result=reference(*args,**kwargs);value=storage.leased_reference(*args[:4])
                        if value is not None and Path(args[0])==path and not seen:
                            with self.assertRaises(OSError):path.write_bytes(b'changed')
                            with self.assertRaises(OSError):path.rename(root/'renamed-input')
                            seen.append(True)
                        return result
                    def verify(target):
                        if Path(target)==path and seen:raise AssertionError('Generic leased input repeated path validation')
                        return checked(target)
                    with storage.payload_hash_operation()as scope,patch.object(storage,'reference_hash',side_effect=probe),patch.object(storage,'checked_source_path',side_effect=verify),patch.object(storage,'read',wraps=reader)as reads:
                        if mode=='local':
                            payloads,guards=storage.declared_local_payloads(root,document)
                            self.assertEqual(path in payloads,role!='metadata');self.assertEqual(trap in payloads,role=='metadata')
                        else:
                            live,_=storage.receipt_references([owner],strict=True)
                            self.assertIn(path,live);self.assertEqual(trap in live,role=='metadata')
                        self.assertEqual(sum(Path(call.args[0])==path for call in reads.call_args_list),int(role=='metadata'))
                        self.assertGreaterEqual(scope['inputLeaseReuses'],131);self.assertTrue(seen)
                    path.write_bytes(b'after lease release');path.write_bytes(original_bytes)
            png=root/'input-0.png';wrong=dict(path=str(png),sha256=storage.sha(png),role='metadata')
            with self.assertRaises(ValueError):storage.declared_local_payloads(root,dict(input=wrong))

    def test_generic_payload_lru_checks_evicted_changes_and_closes_all_file_types(self):
        with self.workspace()as root:
            paths=[root/f'payload-{index}.blob'for index in range(35)]
            for path in paths:path.write_bytes(b'first')
            pins=[dict(path=str(path),sha256=storage.sha(path),role='payload')for path in paths]
            versions=storage.ReferenceVersions(32)
            try:
                for pin in pins:
                    for _ in range(2):self.assertEqual(storage.checked_reference_hash(pin['path'],pin['sha256'],pin['role'],versions,payload=True)[1],pin['sha256'])
                self.assertEqual(versions.held_leases,32)
                with patch.object(storage,'checked_source_path',side_effect=AssertionError('Hot data pin repeated path validation')),patch.object(storage,'file_hash_identity',side_effect=AssertionError('Hot data pin repeated identity query')):
                    for _ in range(100):self.assertEqual(storage.checked_reference_hash(paths[-1],pins[-1]['sha256'],'payload',versions,payload=True)[1],pins[-1]['sha256'])
                paths[0].write_bytes(b'other')
                self.assertNotEqual(storage.checked_reference_hash(paths[0],pins[0]['sha256'],'payload',versions,payload=True)[1],pins[0]['sha256'])
                held=[entry['lease']for entry in versions.values()if entry.get('lease')is not None]
            finally:versions.close()
            self.assertTrue(all(stream.closed for stream in held))
            for path in paths:path.write_bytes(b'after')


class ResourceReceiptBatchTest(unittest.TestCase):
    @contextlib.contextmanager
    def workspace(self):
        with ResourceStorageTest.owned_workspace(self)as(_,_,output):
            output.mkdir();yield output
    @staticmethod
    def pin(path,**values):return dict(path=str(path),sha256=storage.sha(path),**values)
    @staticmethod
    def generation(root,*,name='generation'):
        output=root/name;output.mkdir();payload=output/'output.bytes';payload.write_bytes(b'output')
        producer=root/'producer.py';producer.write_bytes(b'# source')
        owner=output/'generation.json'
        storage.write(owner,dict(kind='CharacterResourceGeneration',schemaVersion=1,state='completed',
            generatorPath=str(producer),generatorSha256=storage.sha(producer),outputs=[ResourceReceiptBatchTest.pin(payload,role='payload')]))
        return owner
    def seed(self,root,name,document):
        directory=root/name;directory.mkdir();owner=directory/'ResourceAssembly.json';storage.write(owner,document);return owner
    def compare(self,root,seeds):
        directories={path.parent for path in seeds};expected_cache={};actual_cache={}
        expected=storage._discover_generations(directories,[root],generation_cache=expected_cache)
        actual=storage.discover_generations(directories,[root],generation_cache=actual_cache)
        self.assertEqual(expected,actual);self.assertEqual(expected_cache,actual_cache)
        return actual
    def lexical_receipt_admitted(self,value,base,roots,*,explicit=True,batched=True,path_type=Path,platform=None):
        # Isolate local admission at the next existing role boundary. Parsing is
        # stubbed; no hash/identity/retention success is claimed by this fixture.
        class ReachedRole(RuntimeError):pass
        owner=base/'lexical-owner.json';batch=storage._ReceiptBatch()if batched else None
        document=dict(input=dict(path=value,sha256='0'*64))if explicit else dict(input=value)
        def reached(path,*args,**kwargs):raise ReachedRole(path)
        try:
            with contextlib.ExitStack()as patches:
                for name,value in(('Path',path_type),('ARTIFACTS',roots[0]),('WORKSPACE',roots[1]),('pin_role',reached)):
                    patches.enter_context(patch.object(storage,name,value))
                if platform is not None:patches.enter_context(patch.object(storage,'os',platform))
                for name in('sha','checked_source_path','checked_reference_hash'):
                    patches.enter_context(patch.object(storage,name,side_effect=AssertionError('Lexical admission must not touch the filesystem')))
                if batch is None:patches.enter_context(patch.object(storage,'read',return_value=document))
                else:
                    patches.enter_context(patch.object(batch,'document',return_value=document))
                    patches.enter_context(patch.object(batch,'snapshot_key',return_value=(owner,'0'*64)))
                    patches.enter_context(patch.object(batch,'edges',return_value=tuple(storage._reference_edges(document))))
                    patches.enter_context(patch.object(batch,'release'))
                try:storage.receipt_references({owner},strict=True,_batch=batch)
                except ReachedRole:return True
                return False
        finally:
            if batch is not None:batch.close()
    def compare_lexical_receipt(self,roots,cases,*,path_type=Path,platform=None,batched=True):
        platform=os if platform is None else platform
        for value,base,explicit in cases:
            expected=None
            if isinstance(value,str)and(platform.path.isabs(value)or explicit):
                expected=path_type(value)
                if not expected.is_absolute():expected=path_type(platform.path.abspath(base/expected))
                expected=expected if expected.is_absolute()and any(expected.is_relative_to(root)for root in roots)else None
            with self.subTest(value=value,base=str(base),explicit=explicit):
                self.assertEqual(expected is not None,self.lexical_receipt_admitted(value,base,roots,explicit=explicit,
                    batched=batched,path_type=path_type,platform=platform))
    @staticmethod
    def windows_lexical_platform():return types.SimpleNamespace(name='nt',sep='\\',path=ntpath)
    def test_local_batch_component_boundaries_self_and_sibling_roots(self):
        roots=(PureWindowsPath('C:/Owned/Artifacts'),PureWindowsPath('C:/Owned/Workspace'))
        values=[str(root)+suffix for root in roots for suffix in('','\\','\\a.json','\\nested\\b.json','2\\escape.json','-other\\x','.bak\\x')]
        self.compare_lexical_receipt(roots,[(value,roots[0],True)for value in values],path_type=PureWindowsPath,platform=self.windows_lexical_platform())
    def test_local_batch_windows_unicode_uses_lower_not_casefold(self):
        roots=(PureWindowsPath('C:/Owned/Straße'),PureWindowsPath('C:/Owned/Σ'))
        values=['c:/owned/STRAßE/a.json','C:/OWNED/STRASSE/x','C:/Owned/Straße-sibling/x','c:/owned/σ/x','c:/owned/ς/x','C:/Owned/Σ/İ/x','C:/Owned/Σ-extra/x']
        self.compare_lexical_receipt(roots,[(value,roots[0],True)for value in values],path_type=PureWindowsPath,platform=self.windows_lexical_platform())
    def test_local_batch_dotdot_keeps_lexical_admission_separate_from_fs_authority(self):
        roots=(PureWindowsPath('C:/Owned/Artifacts'),PureWindowsPath('C:/Owned/Workspace'))
        values=['C:/Owned/Artifacts/../outside.json','C:/Owned/Artifacts/./x','C:/Owned/Workspace/child/../x','C:/Owned/Artifacts/junction/../x']
        self.compare_lexical_receipt(roots,[(value,roots[0],True)for value in values],path_type=PureWindowsPath,platform=self.windows_lexical_platform())
    def test_local_batch_relative_inputs_keep_explicit_and_base_boundaries(self):
        roots=(PureWindowsPath('C:/Owned/Artifacts'),PureWindowsPath('C:/Owned/Workspace'))
        values=['a.json','../outside.json','./a.json','one/../a.json','\\Owned\\Artifacts\\x','C:relative.json',3,None]
        cases=[(value,base,explicit)for value in values for base in(roots[0]/'one',roots[1]/'two')for explicit in(False,True)]
        self.compare_lexical_receipt(roots,cases,path_type=PureWindowsPath,platform=self.windows_lexical_platform())
    def test_local_batch_drive_unc_and_device_anchors_match_pathlib(self):
        sets=[(PureWindowsPath('C:/'),PureWindowsPath('D:/')),(PureWindowsPath('//server/share/'),PureWindowsPath('//?/C:/Owned'))]
        values=['C:/x','D:/x','E:/x','//server/share/x','//server/share-other/x','//?/C:/Owned/x','//?/C:/Owned2/x']
        for roots in sets:self.compare_lexical_receipt(roots,[(value,roots[0],True)for value in values],path_type=PureWindowsPath,platform=self.windows_lexical_platform())
    def test_local_batch_ordinary_roots_do_not_promote_unc_or_device_names(self):
        roots=(PureWindowsPath('E:/repo/artifacts'),PureWindowsPath('E:/repo/workspace'))
        values=['//server/share/repo/artifacts/x','//?/E:/repo/artifacts/x','//./E:/repo/artifacts/x','//?/UNC/server/share/repo/artifacts/x']
        self.compare_lexical_receipt(roots,[(value,roots[0],True)for value in values],path_type=PureWindowsPath,platform=self.windows_lexical_platform())
    def test_local_without_batch_preserves_the_original_pathlib_branch(self):
        roots=(PureWindowsPath('C:/Owned/A'),PureWindowsPath('C:/Owned/B'))
        self.compare_lexical_receipt(roots,[(str(root/'a'/'..'/'b.json'),root,True)for root in roots],
            path_type=PureWindowsPath,platform=self.windows_lexical_platform(),batched=False)
    def test_local_batch_posix_case_and_double_slash_anchors_match_pathlib(self):
        platform=types.SimpleNamespace(name='posix',sep='/',path=posixpath)
        sets=[(PurePosixPath('/owned/A'),PurePosixPath('/owned/ß')),(PurePosixPath('/'),PurePosixPath('//server/share'))]
        values=['/owned/A','/owned/A/x','/owned/a/x','/owned/A/../outside','/owned/A-sibling/x','/owned/SS/x','/owned/ß/x','//server/share/x','/server/share/x','relative.json','../outside','backslash\\name']
        for roots in sets:self.compare_lexical_receipt(roots,[(value,roots[0],explicit)for value in values for explicit in(False,True)],path_type=PurePosixPath,platform=platform)
    def test_local_batch_filter_does_not_touch_filesystem_guards(self):
        roots=(PureWindowsPath('C:/Owned/A'),PureWindowsPath('C:/Owned/B'))
        self.assertTrue(self.lexical_receipt_admitted('C:/Owned/A/recipe.json',roots[1],roots,path_type=PureWindowsPath,platform=self.windows_lexical_platform()))
    def test_local_batch_filter_avoids_ancestor_path_expansion(self):
        roots=(PureWindowsPath('C:/Owned/A'),PureWindowsPath('C:/Owned/B'))
        with patch.object(PureWindowsPath,'is_relative_to',side_effect=AssertionError('No ancestor expansion in batch local')):
            self.assertTrue(self.lexical_receipt_admitted('C:/Owned/A/one/two.json',roots[1],roots,path_type=PureWindowsPath,platform=self.windows_lexical_platform()))
    def test_local_batch_deep_paths_keep_exact_admission_without_a_path_cache(self):
        roots=(PureWindowsPath('C:/Owned/Artifacts'),PureWindowsPath('C:/Owned/Workspace'))
        values=[str(root.joinpath(*(['deep']*depth),'receipt'+str(index)+'.json'))for index in range(4)for depth in(2,8,20)for root in roots]
        self.compare_lexical_receipt(roots,[(value,roots[0],True)for value in values],path_type=PureWindowsPath,platform=self.windows_lexical_platform())
    def test_local_batch_preserves_per_seed_results_roles_and_final_guards(self):
        with self.workspace()as root:
            leaf=root/'leaf.bin';leaf.write_bytes(b'unchanged input');recipe=root/'recipe.json'
            storage.write(recipe,dict(input=self.pin(leaf,role='payload'),outside=str(root.parent.parent/'artifactsSibling'/'not-owned.json')))
            seeds=[self.seed(root,'lexical-'+str(index),dict(inputs=[self.pin(recipe,**({'role':role}if role else{}))]*2,plain=str(recipe)))for index,role in enumerate((None,'metadata',None))]
            expected=[];expected_cache={};expected_unresolved=[]
            for seed in seeds:expected.append(storage.receipt_references({seed},expected_unresolved,generation_cache=expected_cache,strict=True))
            actual=[];actual_cache={};actual_unresolved=[];batch=storage._ReceiptBatch()
            try:
                for seed in seeds:actual.append(storage.receipt_references({seed},actual_unresolved,generation_cache=actual_cache,strict=True,_batch=batch))
                batch.finish()
                self.assertEqual(actual,expected);self.assertEqual(actual_cache,expected_cache);self.assertEqual(actual_unresolved,expected_unresolved)
                self.assertLessEqual(batch.retained_bytes(),batch.maximum_bytes);self.assertLessEqual(batch.snapshot_bytes,batch.maximum_snapshot_bytes)
            finally:batch.close()
            self.assertEqual(batch.versions.held_leases,0)
    def test_local_batch_snapshot_preserves_original_edge_order_and_pin_fields(self):
        document=dict(inputs=[dict(path='one.json',sha256='a'*64,role='metadata'),dict(file='two.bin',sha256='b'*64,role='payload'),dict(path='one.json',sha256='a'*64,role='metadata')],plain='C:/Owned/Artifacts/diagnostic.json',values=[False,None,13])
        expected=[('pin','one.json','a'*64,'metadata'),('pin','two.bin','b'*64,'payload'),('pin','one.json','a'*64,'metadata')]
        if os.path.isabs(document['plain']):expected.append(('plain',document['plain']))
        snapshot=storage._ReceiptSnapshot({},document)
        self.assertEqual(list(snapshot),expected);self.assertEqual(list(snapshot),list(storage._reference_edges(document)))

    def test_shared_metadata_is_parsed_once_without_sharing_seed_roles(self):
        with self.workspace()as root:
            generation=self.generation(root);recipe=root/'common.json';storage.write(recipe,dict(input=self.pin(generation),samples=list(range(2000))))
            seeds=[self.seed(root,'A',dict(recipe=self.pin(recipe))),self.seed(root,'B',dict(recipe=self.pin(recipe,role='metadata')))]
            expected=storage._discover_generations({seed.parent for seed in seeds},[root]);reader=storage.read
            with patch.object(storage,'read',wraps=reader)as reads,patch.object(storage,'normalize_generation',wraps=storage.normalize_generation)as normalized:
                actual=storage.discover_generations({seed.parent for seed in seeds},[root])
                self.assertEqual(expected,actual);self.assertEqual(sum(call.args[0]==recipe for call in reads.call_args_list),1)
                self.assertEqual(sum(call.args[0]==recipe for call in normalized.call_args_list),2)
            second=generation.parent/'conflict.json';document=storage.read(generation);document['outputs'][0]['role']='metadata';storage.write(second,document)
            bad=self.seed(root,'Bad',dict(first=self.pin(generation),second=self.pin(second)))
            self.compare(root,[bad,*seeds])
    def test_owner_and_role_variants_share_syntax_but_replay_each_closure(self):
        with self.workspace()as root:
            leaf=root/'leaf.bytes';leaf.write_bytes(b'leaf');recipe=root/'recipe.json'
            document=dict(inputs=[self.pin(leaf,role='payload'),self.pin(leaf,role='payload')],samples=list(range(1000)))
            storage.write(recipe,document);digest=storage.sha(recipe)
            first=self.seed(root,'A',dict(input=self.pin(recipe)))
            second=self.seed(root,'B',dict(input=self.pin(recipe,role='metadata')))
            owners=(recipe,first,second,recipe);expected=[]
            for owner in owners:
                issues=[];result=storage.receipt_references([owner],issues,strict=True);expected.append((result,issues))
            batch=storage._ReceiptBatch()
            try:
                actual=[]
                with patch.object(storage,'read',wraps=storage.read)as reads:
                    for owner in owners:
                        issues=[];result=storage.receipt_references([owner],issues,strict=True,_batch=batch);actual.append((result,issues))
                self.assertEqual(actual,expected)
                self.assertEqual(sum(call.args[0]==recipe for call in reads.call_args_list),1)
                snapshot=batch.snapshots[recipe,digest]
                self.assertEqual(list(snapshot),list(storage._reference_edges(document)))
                self.assertEqual(len(snapshot.order),2);self.assertEqual(len(snapshot.unique),1)
                self.assertEqual(snapshot.header,{})
                self.assertEqual(batch.snapshot_bytes,sum(value.retained_bytes for value in batch.snapshots.values()))
                self.assertLessEqual(batch.snapshot_bytes,batch.maximum_snapshot_bytes)
                self.assertLessEqual(batch.retained_bytes(),batch.maximum_bytes)
                self.assertFalse(batch.active);self.assertFalse(batch.pending);batch.finish()
            finally:batch.close()
            self.assertEqual(batch.versions.held_leases,0)
    def test_fresh_cross_role_snapshot_still_enters_an_unseeded_read_lease(self):
        for owner_first in(False,True):
            with self.subTest(owner_first=owner_first),self.workspace()as root:
                recipe=root/'recipe.json';storage.write(recipe,dict(value='source'));digest=storage.sha(recipe)
                batch=storage._ReceiptBatch()
                try:
                    document=batch.document(recipe,None if owner_first else digest,None)
                    snapshot=batch.edges(recipe,None if owner_first else digest,None,storage.normalize_generation(recipe,document,{}));batch.release(snapshot)
                    with patch.object(batch,'read_lease',wraps=batch.read_lease)as leases:
                        actual=batch.document(recipe,digest,'metadata',fresh=True)
                    self.assertEqual(actual,snapshot.header);self.assertEqual(leases.call_count,1)
                    self.assertIs(leases.call_args.kwargs['reuse_guard'],False);batch.finish()
                finally:batch.close()
                self.assertEqual(batch.versions.held_leases,0)
    def test_fresh_lease_failure_is_not_hidden_by_another_role_snapshot(self):
        with self.workspace()as root:
            recipe=root/'recipe.json';storage.write(recipe,dict(value='source'));digest=storage.sha(recipe)
            batch=storage._ReceiptBatch()
            try:
                document=batch.document(recipe,None,None);snapshot=batch.edges(recipe,None,None,document);batch.release(snapshot)
                failure=storage._ReceiptBatchUnavailable('Fresh metadata read lease unavailable')
                with patch.object(batch,'read_lease',side_effect=failure):
                    with self.assertRaises(storage._ReceiptBatchUnavailable)as caught:batch.document(recipe,digest,'metadata',fresh=True)
                self.assertIs(caught.exception,failure)
            finally:batch.close()
            self.assertEqual(batch.versions.held_leases,0)
    def test_owner_failure_before_a_guard_keeps_the_original_validation_error(self):
        for mode in('missing','identity-unavailable','hash-failure'):
            with self.subTest(mode=mode),self.workspace()as root:
                recipe=root/'recipe.json';batch=storage._ReceiptBatch();failure=OSError('Synthetic owner hash failure')
                if mode!='missing':storage.write(recipe,dict(value='source'))
                try:
                    with contextlib.ExitStack()as stack:
                        if mode=='identity-unavailable':stack.enter_context(patch.object(storage,'sha',return_value='a'*64))
                        elif mode=='hash-failure':stack.enter_context(patch.object(storage,'sha',side_effect=failure))
                        if mode=='missing':
                            with self.assertRaisesRegex(ValueError,'not a regular local file or traverses a link'):
                                storage.receipt_references([recipe],strict=True,_batch=batch)
                        elif mode=='identity-unavailable':
                            with self.assertRaisesRegex(storage._ReceiptBatchUnavailable,'owner has no stable file identity'):
                                storage.receipt_references([recipe],strict=True,_batch=batch)
                        else:
                            with self.assertRaises(OSError)as caught:storage.receipt_references([recipe],strict=True,_batch=batch)
                            self.assertIs(caught.exception,failure)
                    self.assertFalse(batch.guards);self.assertFalse(batch.pending)
                finally:batch.close()
                self.assertEqual(batch.versions.held_leases,0)
    def test_wrong_digest_seed_cannot_authorize_a_later_owner_root(self):
        with self.workspace()as root:
            leaf=root/'leaf.bytes';leaf.write_bytes(b'leaf');recipe=root/'recipe.json';storage.write(recipe,dict(input=self.pin(leaf,role='payload')))
            bad=self.seed(root,'Bad',dict(input=dict(path=str(recipe),sha256='0'*64,role='metadata')));batch=storage._ReceiptBatch()
            try:
                issues=[];storage.receipt_references([bad],issues,strict=True,_batch=batch)
                self.assertTrue(issues);self.assertIsNone(batch.guard_for(recipe))
                actual=storage.receipt_references([recipe],strict=True,_batch=batch)
                self.assertIn(leaf,actual[0]);self.assertEqual(batch.guard_for(recipe)[0],storage.sha(recipe));batch.finish()
            finally:batch.close()
            self.assertEqual(batch.versions.held_leases,0)
    def test_a_peers_historical_owner_pin_is_still_checked(self):
        with self.workspace()as root:
            generation=self.generation(root);peer=self.seed(root,'B',dict(input=self.pin(generation)))
            common=root/'common.json';storage.write(common,dict(historical=dict(path=str(peer),sha256='0'*64),input=self.pin(generation)))
            storage.write(peer,dict(recipe=self.pin(common)));first=self.seed(root,'A',dict(recipe=self.pin(common)))
            rows=[];batch=storage._ReceiptBatch()
            try:
                for scope in(None,batch):
                    values=[];cache={}
                    for seed in(first,peer):
                        issues=[];live,generations=storage.receipt_references([seed],issues,generation_cache=cache,strict=True,_batch=scope)
                        values.append((live,generations,issues))
                    rows.append(values)
                batch.finish()
            finally:batch.close()
            self.assertEqual(rows[0],rows[1]);self.assertEqual(rows[1][0][2][0]['reason'],'changed-retained-recipe');self.assertEqual(rows[1][1][2],[])
    def test_equal_recipe_bytes_keep_each_relative_base(self):
        with self.workspace()as root:
            seeds=[]
            for name in('A','B'):
                seed=self.seed(root,name,{});payload=seed.parent/'input.bytes';payload.write_bytes(b'input')
                recipe=seed.parent/'recipe.json';storage.write(recipe,dict(path='input.bytes',sha256=storage.sha(payload),role='payload'))
                storage.write(seed,dict(recipe=self.pin(recipe)));seeds.append(seed)
            batch=storage._ReceiptBatch()
            try:
                for seed in seeds:
                    live,_=storage.receipt_references([seed],strict=True,_batch=batch)
                    self.assertIn(seed.parent/'input.bytes',live)
                    self.assertNotIn(next(path for path in seeds if path!=seed).parent/'input.bytes',live)
                batch.finish()
            finally:batch.close()
    def test_unavailable_snapshot_retries_original_validation_and_rolls_back_cache(self):
        for failure in('file_hash_identity','_open_read_lease','budget'):
            with self.subTest(failure=failure),self.workspace()as root:
                generation=self.generation(root);seed=self.seed(root,'A',dict(input=self.pin(generation)));expected_cache={}
                expected=storage._discover_generations({seed.parent},[root],generation_cache=expected_cache)
                unavailable=patch.object(storage._ReceiptBatch,'maximum_bytes',1)if failure=='budget'else patch.object(storage,failure,return_value=None)
                actual_cache={}
                with unavailable:self.assertEqual(expected,storage.discover_generations({seed.parent},[root],generation_cache=actual_cache))
                self.assertEqual(expected_cache,actual_cache)
    def test_external_edit_aborts_without_retry_or_publishing_role_authority(self):
        with self.workspace()as root:
            generation=self.generation(root);seed=self.seed(root,'A',dict(input=self.pin(generation)));cache={}
            with patch.object(storage._ReceiptBatch,'finish',side_effect=storage._ReceiptBatchChanged('changed final input')),patch.object(storage,'_discover_generations',wraps=storage._discover_generations)as discoveries:
                with self.assertRaises(storage._ReceiptBatchChanged):storage.discover_generations({seed.parent},[root],generation_cache=cache)
                self.assertEqual(discoveries.call_count,1)
            self.assertEqual(cache,{})
    def test_growing_generation_cache_obeys_the_snapshot_budget(self):
        with self.workspace()as root:
            generation=self.generation(root);seed=self.seed(root,'A',dict(input=self.pin(generation)));expected_cache={}
            expected=storage._discover_generations({seed.parent},[root],generation_cache=expected_cache)
            charge=storage._ReceiptBatch.charge;exhaustions=[]
            def limited(batch,name,key,value):
                charge(batch,name,key,value)
                if name=='generation-cache'and value:
                    exhaustions.append(True);raise storage._ReceiptBatchUnavailable('staged cache exceeded its allowance')
            cache={}
            with patch.object(storage._ReceiptBatch,'charge',limited):self.assertEqual(expected,storage.discover_generations({seed.parent},[root],generation_cache=cache))
            self.assertEqual(expected_cache,cache);self.assertEqual(exhaustions,[True])
    def test_final_snapshot_rejects_evicted_same_size_preserved_mtime_edits(self):
        with self.workspace()as root:
            generation=self.generation(root);seed=self.seed(root,'A',dict(input=self.pin(generation)));batch=storage._ReceiptBatch();batch.versions.maximum_leases=1
            try:
                storage.receipt_references([seed],strict=True,_batch=batch);batch.versions.make_space()
                producer=root/'producer.py';stamp=producer.stat();producer.write_bytes(b'# SOURCE');os.utime(producer,ns=(stamp.st_atime_ns,stamp.st_mtime_ns))
                with self.assertRaises(storage._ReceiptBatchChanged):batch.finish()
            finally:batch.close()
            self.assertEqual(batch.versions,{})
            producer.write_bytes(b'# released')
    def test_compact_snapshot_preserves_order_and_drops_nonreference_arrays(self):
        document=dict(samples=list(range(20000)),rows=[dict(path='a',sha256='a'*64),dict(path='b',sha256='b'*64),dict(path='a',sha256='a'*64)])
        snapshot=storage._ReceiptSnapshot({},document)
        self.assertEqual(list(storage._reference_edges(document)),list(snapshot));self.assertEqual(len(snapshot.unique),2)
        self.assertLess(snapshot.retained_bytes,4096)

    def test_legacy_named_diagnostic_keeps_its_edges_without_caching_numeric_rows_as_a_producer(self):
        with self.workspace()as root:
            leaf=root/'leaf.bytes';leaf.write_bytes(b'leaf')
            producer=root/'producer.py';producer.write_text('# producer','utf-8')
            input_path=root/'input.json';storage.write(input_path,dict(value=1))
            diagnostic=root/'diagnostic.json'
            document=dict(generatorPath=str(producer),generatorSha256=storage.sha(producer),
                sourceInput=str(input_path),sourceInputSha256=storage.sha(input_path),
                rows=[dict(samples=list(range(20000)),dependency=self.pin(leaf,role='payload'))])
            storage.write(diagnostic,document)
            owners=[self.seed(root,name,dict(input=self.pin(diagnostic,role='metadata')))for name in('A','B')]
            expected=[storage.receipt_references([owner],strict=True)for owner in owners]
            batch=self.batch();batch.maximum_snapshot_bytes=16384
            try:
                observed=[storage.receipt_references([owner],strict=True,_batch=batch)for owner in owners]
                self.assertEqual(observed,expected)
                snapshot=batch.snapshots[diagnostic,storage.sha(diagnostic)]
                self.assertEqual(set(snapshot.header),{'generatorPath','generatorSha256'})
                self.assertEqual(list(snapshot),list(storage._reference_edges(document)))
                self.assertEqual(batch.uncached_snapshots,0)
                self.assertIn(leaf,observed[-1][0]);self.assertFalse(observed[-1][1])
                self.assertLessEqual(batch.snapshot_bytes,batch.maximum_snapshot_bytes)
                batch.finish()
            finally:batch.close()

    def test_a_large_current_producer_is_uncached_and_preserves_batch_guards_and_other_snapshots(self):
        for changed in(False,True):
            with self.subTest(changed=changed),self.workspace()as root:
                leaf=root/'leaf.bytes';leaf.write_bytes(b'leaf')
                work=root/'work.json'
                storage.write(work,dict(schemaVersion=1,kind='CharacterResourceWork',state='completed',
                    output=str(root),sourceInput=self.pin(leaf,role='payload'),files=[],samples=list(range(2000))))
                owners=[self.seed(root,name,dict(input=self.pin(work,role='metadata')))for name in('A','B')]
                expected=[storage.receipt_references([owner],strict=True)for owner in owners]
                batch=self.batch();batch.maximum_snapshot_bytes=8192
                try:
                    with patch('builtins.print'):
                        observed=[storage.receipt_references([owner],strict=True,_batch=batch)for owner in owners]
                    self.assertEqual(observed,expected)
                    self.assertEqual(batch.uncached_snapshots,2)
                    self.assertGreater(batch.largest_uncached_snapshot_bytes,batch.maximum_snapshot_bytes)
                    self.assertNotIn((work,storage.sha(work)),batch.snapshots)
                    self.assertTrue(batch.snapshots)
                    self.assertTrue(batch.guard_for(work));self.assertTrue(batch.guard_for(leaf))
                    self.assertFalse(batch.active);self.assertEqual(batch.active_bytes,0)
                    self.assertLessEqual(batch.snapshot_bytes,batch.maximum_snapshot_bytes)
                    self.assertLessEqual(batch.peak_bytes,batch.maximum_bytes)
                    if changed:
                        ResourceStorageTest.evict_reference_versions(batch)
                        stamp=leaf.stat();leaf.write_bytes(b'edit');os.utime(leaf,ns=(stamp.st_atime_ns,stamp.st_mtime_ns))
                        with self.assertRaises(storage._ReceiptBatchChanged):batch.finish()
                    else:batch.finish()
                finally:batch.close()

    def test_total_byte_pressure_evicts_optional_snapshots_before_falling_back(self):
        batch=storage._ReceiptBatch()
        try:
            snapshot=self.add_receipt_snapshot(batch,1)
            batch.maximum_bytes=batch.versions.maximum_leases*8192+snapshot.retained_bytes//2
            batch.check_budget()
            self.assertFalse(batch.snapshots);self.assertEqual(batch.snapshot_bytes,0)
            self.assertEqual(batch.evictions['snapshots'],1)
            self.assertLessEqual(batch.retained_bytes(),batch.maximum_bytes)
        finally:batch.close()
    @unittest.skipUnless(os.name=='nt','Actual Windows raw lookup contract')
    def test_snapshot_rechecks_junction_segments_before_dotdot(self):
        with self.workspace()as root:
            generation=self.generation(root);plain=root/'plain';plain.mkdir();recipe=plain/'recipe.json';storage.write(recipe,dict(input=self.pin(generation)))
            sibling=root/'sibling';sibling.mkdir();outside=root/'unrelated';outside.mkdir();marker=outside/'keep.bytes';marker.write_bytes(b'keep')
            seed=self.seed(root,'A',dict(recipe=dict(path='../sibling/../plain/recipe.json',sha256=storage.sha(recipe))));batch=storage._ReceiptBatch()
            try:
                storage.receipt_references([seed],strict=True,_batch=batch)
                self.assertTrue(sibling.resolve().is_relative_to(root));sibling.rmdir()
                link=storage.subprocess.run(['cmd.exe','/d','/c','mklink','/J',str(sibling),str(outside)],capture_output=True)
                if link.returncode:self.skipTest('Junction creation unavailable in this environment')
                with self.assertRaisesRegex(storage._ReceiptBatchChanged,'reparse'):batch.finish()
                self.assertEqual(marker.read_bytes(),b'keep')
            finally:
                batch.close()
                if sibling.exists()or sibling.is_junction():self.assertEqual(sibling.parent.resolve(),root.resolve());os.rmdir(sibling)


    def inputs(self,root,count,role):
        paths=[]
        for index in range(count):
            path=root/f'input-{index}.json'
            storage.write(path,dict(value='first'))
            paths.append(path)
        seed=self.seed(root,'A',dict(inputs=[self.pin(path,role=role)for path in paths]))
        return paths,seed
    def batch(self):
        batch=storage._ReceiptBatch();batch.maximum_pin_entries=8;batch.maximum_version_entries=8
        return batch
    def test_canonical_payloads_keep_one_guard_without_duplicate_pin_or_lookup_tables(self):
        with self.workspace()as root:
            paths,seed=self.inputs(root,48,'payload');batch=self.batch()
            try:
                actual=storage.receipt_references([seed],strict=True,_batch=batch)[0]
                self.assertTrue(set(paths).issubset(actual));self.assertEqual(len(batch.guards),49)
                self.assertEqual(len(batch.lookups),0);self.assertEqual(len(batch.pins),0)
                self.assertLessEqual(len(batch.versions),8);self.assertGreater(batch.evictions['versions'],0)
                second=self.seed(root,'B',storage.read(seed))
                self.assertEqual(storage.receipt_references([second],strict=True,_batch=batch)[0],actual)
                self.assertGreaterEqual(batch.guard_payload_reuses,48);batch.finish()
            finally:batch.close()
    def test_optional_metadata_lru_is_bounded_and_the_hot_key_moves_to_the_end(self):
        with self.workspace() as root:
            paths, seed = self.inputs(root, 48, 'metadata')
            batch = self.batch()
            alias = root / 'alias'
            alias.mkdir()
            document = storage.read(seed)
            for pin, path in zip(document['inputs'], paths):
                pin['path'] = str(Path('..') / alias.name / '..' / path.name)
            storage.write(seed, document)
            try:
                self.assertTrue(set(paths).issubset(storage.receipt_references([seed], strict=True, _batch=batch)[0]))
                self.assertLessEqual(len(batch.pins), 8)
                self.assertLessEqual(len(batch.versions), 8)
                hot = paths[-1]
                hot_directory = root / 'Hot'
                hot_directory.mkdir()
                relative_hot = Path('..') / alias.name / '..' / hot.name
                raw_hot = hot_directory / relative_hot
                pin = dict(self.pin(hot, role='metadata'), path=str(relative_hot))
                for index in range(5):
                    hot_seed = hot_directory / ('owner-%d.json' % index)
                    storage.write(hot_seed, dict(input=pin))
                    storage.receipt_references([hot_seed], strict=True, _batch=batch)
                self.assertEqual(next(reversed(batch.pins))[0], raw_hot)
                self.assertIn((hot, pin['sha256'], 'metadata'), batch.versions)
                self.assertEqual(len(batch.guards), 54)
                batch.finish()
            finally:
                batch.close()
    def test_optional_cache_pressure_evicts_before_rejecting_required_guards(self):
        with self.workspace()as root:
            paths,seed=self.inputs(root,48,'metadata');batch=storage._ReceiptBatch()
            try:
                storage.receipt_references([seed],strict=True,_batch=batch)
                initial=batch.statistics();optional=sum(batch.category_bytes[name]for name in('pins','versions'))
                self.assertGreater(optional,0)
                batch.maximum_bytes=batch.retained_bytes()-optional//2
                batch.check_budget();batch.finish()
                final=batch.statistics();self.assertLessEqual(final['retainedBytes'],batch.maximum_bytes)
                self.assertEqual(len(batch.guards),49);self.assertGreater(sum(batch.evictions.values()),0)
                self.assertEqual(batch.ledger_bytes,sum(batch.category_bytes.values()))
            finally:batch.close()
    def test_evicted_canonical_payload_change_with_same_size_and_mtime_is_rejected_at_finish(self):
        with self.workspace()as root:
            paths,seed=self.inputs(root,12,'payload');batch=self.batch();batch.maximum_version_entries=2
            try:
                storage.receipt_references([seed],strict=True,_batch=batch)
                path=paths[0];old=path.read_bytes();stamp=path.stat()
                self.assertFalse(any(key[0]==path for key in batch.versions));self.assertIsNotNone(batch.guard_for(path))
                path.write_bytes(old.replace(b'first',b'other'));os.utime(path,ns=(stamp.st_atime_ns,stamp.st_mtime_ns))
                self.assertEqual(path.stat().st_size,stamp.st_size);self.assertEqual(path.stat().st_mtime_ns,stamp.st_mtime_ns)
                with self.assertRaises(storage._ReceiptBatchChanged):batch.finish()
            finally:batch.close()
            for path in paths:path.write_bytes(b'after-close')
    def test_changed_digest_and_metadata_role_cannot_reuse_a_payload_guard(self):
        with self.workspace()as root:
            dep=root/'dependency.bytes';dep.write_bytes(b'keep')
            path=root/'receipt.json';storage.write(path,dict(input=self.pin(dep,role='payload')))
            first=self.seed(root,'A',dict(input=self.pin(path,role='payload')))
            second=self.seed(root,'B',dict(input=self.pin(path,role='metadata')))
            batch=self.batch()
            try:
                first_live=storage.receipt_references([first],strict=True,_batch=batch)[0]
                second_live=storage.receipt_references([second],strict=True,_batch=batch)[0]
                self.assertNotIn(dep,first_live);self.assertIn(dep,second_live)
                wrong=self.seed(root,'C',dict(input=dict(path=str(path),sha256='0'*64,role='payload')))
                issues=[];storage.receipt_references([wrong],issues,strict=True,_batch=batch)
                self.assertEqual(issues[0]['reason'],'changed-retained-input')
                wrong_metadata=self.seed(root,'D',dict(input=dict(path=str(path),sha256='0'*64,role='metadata')))
                issues=[];storage.receipt_references([wrong_metadata],issues,strict=True,_batch=batch)
                self.assertEqual(issues[0]['reason'],'changed-retained-recipe');batch.finish()
            finally:batch.close()
    def test_raw_dotdot_lookup_is_kept_and_a_junction_swap_is_detected(self):
        with self.workspace()as root:
            sibling=root/'sibling';sibling.mkdir();plain=root/'plain';plain.mkdir()
            path=plain/'input.json';storage.write(path,dict(value=1))
            seed=self.seed(root,'A',dict(input=dict(path='../sibling/../plain/input.json',sha256=storage.sha(path),role='payload')))
            batch=self.batch()
            try:
                storage.receipt_references([seed],strict=True,_batch=batch)
                self.assertEqual(len(batch.lookups),1);self.assertEqual(next(iter(batch.lookups.values())),path)
                sibling.rmdir();outside=root/'outside';outside.mkdir()
                result=storage.subprocess.run(['cmd.exe','/d','/c','mklink','/J',str(sibling),str(outside)],capture_output=True)
                self.assertEqual(result.returncode,0,result.stderr)
                with self.assertRaisesRegex(storage._ReceiptBatchChanged,'reparse'):batch.finish()
            finally:
                batch.close()
                if sibling.is_junction():os.rmdir(sibling)
    def test_new_pin_eviction_and_finally_close_release_all_handles(self):
        with self.workspace()as root:
            paths,seed=self.inputs(root,48,'metadata');batch=self.batch();held=[]
            try:
                storage.receipt_references([seed],strict=True,_batch=batch)
                held=[entry['lease']for entry in batch.versions.values()if entry.get('lease')is not None]
                self.assertTrue(held)
                with self.assertRaises(OSError):paths[-1].write_bytes(b'blocked')
                raise RuntimeError('expected fixture interruption')
            except RuntimeError as error:self.assertEqual(str(error),'expected fixture interruption')
            finally:batch.close()
            self.assertTrue(all(stream.closed for stream in held));self.assertFalse(batch.versions)
            self.assertEqual(batch.ledger_bytes,0);self.assertFalse(batch.protected_versions)
            for path in paths:path.write_bytes(b'after-close')
    def test_fresh_producer_does_not_use_the_canonical_payload_guard_shortcut(self):
        with self.workspace()as root:
            producer=root/'producer.py';producer.write_bytes(b'# producer')
            generation=self.generation(root)
            generation_document=storage.read(generation)
            generation_document['generatorPath']=str(producer);generation_document['generatorSha256']=storage.sha(producer)
            storage.write(generation,generation_document)
            first=self.seed(root,'A',dict(input=self.pin(generation)))
            second=self.seed(root,'B',dict(input=self.pin(generation)))
            batch=self.batch();original=storage.reference_hash;observed=[]
            def reference(path,*args,**kwargs):
                if Path(path)==producer:observed.append(kwargs.get('payload'))
                return original(path,*args,**kwargs)
            try:
                with patch.object(storage,'reference_hash',side_effect=reference):
                    storage.receipt_references([first],strict=True,_batch=batch)
                    storage.receipt_references([second],strict=True,_batch=batch)
                self.assertEqual(observed,[False,False])
                producer_keys=[key for key in batch.pins if key[0]==producer]
                self.assertTrue(producer_keys);self.assertTrue(all(key[-1]is True for key in producer_keys))
                # Cross-seed syntax reuse is legal under the final identity guard;
                # it must never enter the ordinary canonical payload shortcut.
                batch.finish()
            finally:batch.close()
    def test_all_required_guards_remain_an_honest_budget_limit(self):
        with self.workspace()as root:
            _,seed=self.inputs(root,12,'payload');batch=self.batch()
            try:
                storage.receipt_references([seed],strict=True,_batch=batch)
                guard_count=len(batch.guards)
                batch.maximum_bytes=batch.versions.maximum_leases*8192+batch.category_bytes['guards']-1
                with self.assertRaisesRegex(storage._ReceiptBatchUnavailable,'memory budget')as raised:batch.check_budget()
                self.assertEqual(len(batch.guards),guard_count)
                self.assertFalse(batch.versions);self.assertFalse(batch.pins)
                diagnostic=json.loads(str(raised.exception).split(': ',1)[1])
                self.assertEqual(diagnostic['categories']['guards']['entries'],guard_count)
                self.assertGreater(diagnostic['peakActiveBytes'],0)
            finally:batch.close()


    def test_metadata_pin_outlives_its_version_and_reacquires_the_lease_after_snapshot_eviction(self):
        for evict_snapshot in (False, True):
            with self.subTest(evict_snapshot=evict_snapshot), self.workspace() as root:
                payload = root / 'leaf.bytes'
                payload.write_bytes(b'leaf')
                recipe = root / 'recipe.json'
                storage.write(recipe, dict(input=self.pin(payload, role='payload')))
                alias = root / 'alias'
                alias.mkdir()
                pin = dict(self.pin(recipe, role='metadata'), path=str(Path('..') / alias.name / '..' / recipe.name))
                owner = self.seed(root, 'A', dict(input=pin))
                batch = storage._ReceiptBatch()
                batch.maximum_version_entries = 2
                batch.maximum_pin_entries = 64
                batch.maximum_snapshot_bytes = 10000 if evict_snapshot else 1024**2
                reader = storage.read
                reads = []
                def read(path):
                    result = reader(path)
                    if path == recipe:
                        reads.append(True)
                        with self.assertRaises(OSError):
                            recipe.write_bytes(b'blocked while reading')
                    return result
                try:
                    with patch.object(storage, 'read', side_effect=read):
                        storage.receipt_references([owner], strict=True, _batch=batch)
                        for index in range(12):
                            metadata = root / ('noise-%d.json' % index)
                            storage.write(metadata, dict(value=index))
                            seed = self.seed(root, 'Noise-%d' % index, dict(input=self.pin(metadata, role='metadata')))
                            storage.receipt_references([seed], strict=True, _batch=batch)
                        self.assertFalse(any(key[0] == recipe for key in batch.versions))
                        self.assertTrue(any(key[0] == owner.parent / '..' / alias.name / '..' / recipe.name for key in batch.pins))
                        self.assertEqual(any(key[0] == recipe for key in batch.snapshots), not evict_snapshot)
                        self.assertIn(payload, storage.receipt_references([owner], strict=True, _batch=batch)[0])
                        batch.finish()
                    self.assertEqual(len(reads), 2 if evict_snapshot else 1)
                finally:
                    batch.close()
                recipe.write_bytes(b'after close')
    def test_retain_pin_can_start_with_a_fresh_version_and_owns_its_read_lease(self):
        with self.workspace()as root:
            recipe=root/'metadata.json';storage.write(recipe,dict(value='first'));digest=storage.sha(recipe)
            batch=self.batch()
            try:
                batch.retain_pin(str(recipe),root,recipe,digest,'metadata','metadata',(None,False,False),False)
                self.assertIsNotNone(storage.leased_reference(recipe,digest,'metadata',batch.versions))
                self.assertIsNotNone(batch.guard_for(recipe))
                with self.assertRaises(OSError):recipe.write_bytes(b'blocked')
                batch.finish()
            finally:batch.close()
            recipe.write_bytes(b'after close')
    def test_change_between_fresh_hash_and_lease_acquisition_aborts_even_if_bytes_are_identical(self):
        for same_bytes in(False,True):
            with self.subTest(same_bytes=same_bytes),self.workspace()as root:
                recipe=root/'metadata.json';storage.write(recipe,dict(value='first'))
                original=recipe.read_bytes();stamp=recipe.stat();digest=storage.sha(recipe);batch=self.batch()
                reference=storage.reference_hash;calls=[]
                def changing(path,*args,**kwargs):
                    value=reference(path,*args,**kwargs)
                    if path==recipe and not calls:
                        calls.append(True)
                        if same_bytes:
                            recipe.rename(root/'previous.json');recipe.write_bytes(original)
                        else:recipe.write_bytes(original.replace(b'first',b'other'))
                        os.utime(recipe,ns=(stamp.st_atime_ns,stamp.st_mtime_ns))
                    return value
                try:
                    with patch.object(storage,'reference_hash',side_effect=changing),self.assertRaisesRegex(storage._ReceiptBatchChanged,'receipt read.*metadata.json'):
                        batch.document(recipe,digest,'metadata')
                finally:batch.close()
                self.assertFalse(batch.protected_versions);self.assertFalse(batch.versions)
                recipe.write_bytes(b'after close')
    def test_reacquisition_unavailable_diagnostic_names_stage_and_path_without_leaking_handles(self):
        for stage in('receipt read','input pin'):
            with self.subTest(stage=stage),self.workspace()as root:
                recipe=root/'metadata.json';storage.write(recipe,dict(value='first'));digest=storage.sha(recipe)
                batch=self.batch()
                try:
                    with patch.object(storage,'_open_read_lease',return_value=None),self.assertRaises(storage._ReceiptBatchUnavailable)as raised:
                        if stage=='receipt read':batch.document(recipe,digest,'metadata')
                        else:batch.retain_pin(str(recipe),root,recipe,digest,'metadata','metadata',(None,False,False),False)
                    self.assertIn(stage,str(raised.exception));self.assertIn(str(recipe),str(raised.exception))
                    self.assertFalse(batch.protected_versions);self.assertEqual(batch.versions.held_leases,0)
                finally:batch.close()
                recipe.write_bytes(b'after close')

    def test_canonical_metadata_reuses_guards_after_pin_and_version_eviction(self):
        with self.workspace()as root:
            paths,first=self.inputs(root,48,'metadata');second=self.seed(root,'B',storage.read(first));batch=self.batch()
            try:
                expected=storage.receipt_references([first],strict=True)[0]
                self.assertEqual(storage.receipt_references([first],strict=True,_batch=batch)[0],expected)
                ResourceStorageTest.evict_reference_versions(batch);before=batch.guard_metadata_reuses;reader=storage.sha
                with patch.object(storage,'sha',wraps=reader)as hashes:
                    actual=storage.receipt_references([second],strict=True,_batch=batch)[0]
                self.assertEqual(actual,expected)
                self.assertEqual(sum(Path(call.args[0])in paths for call in hashes.call_args_list),0)
                self.assertGreaterEqual(batch.guard_metadata_reuses-before,len(paths))
                self.assertFalse(any(key[0]in paths for key in batch.pins))
                self.assertLessEqual(len(batch.versions),batch.maximum_version_entries)
                self.assertLessEqual(batch.versions.held_leases,batch.versions.maximum_leases);batch.finish()
            finally:batch.close()

    def test_role_promotion_changes_traversal_even_with_a_metadata_guard(self):
        with self.workspace()as root:
            leaf=root/'leaf.bytes';leaf.write_bytes(b'leaf');recipe=root/'shared.json';storage.write(recipe,dict(input=self.pin(leaf,role='payload')))
            pin=self.pin(recipe);first=self.seed(root,'A',dict(input=pin));second=self.seed(root,'B',dict(input=pin));batch=self.batch();cache={}
            try:
                before=storage.receipt_references([first],strict=True,generation_cache=cache,_batch=batch)[0]
                self.assertIn(leaf,before);self.assertIn(recipe,before)
                cache.setdefault(pin['sha256'],{})['typedPayloadAuthority']=True;ResourceStorageTest.evict_reference_versions(batch)
                after=storage.receipt_references([second],strict=True,generation_cache=cache,_batch=batch)[0]
                expected=storage.receipt_references([second],strict=True,generation_cache={key:dict(value)for key,value in cache.items()})[0]
                self.assertEqual(after,expected);self.assertIn(recipe,after);self.assertNotIn(leaf,after);batch.finish()
            finally:batch.close()

    def test_numeric_payload_explicit_metadata_is_rejected_after_guard_reuse(self):
        with self.workspace()as root:
            source=root/'legacy-source';source.mkdir()
            _,mesh,_,_=ResourceStorageTest.legacy_generation(source,root/'legacy-output')
            first=self.seed(root,'A',dict(input=self.pin(mesh,role='payload')))
            second=self.seed(root,'B',dict(input=self.pin(mesh,role='metadata')));batch=self.batch();cache={}
            try:
                storage.receipt_references([first],strict=True,generation_cache=cache,_batch=batch);ResourceStorageTest.evict_reference_versions(batch)
                with self.assertRaisesRegex(ValueError,'numeric mesh packet.*metadata'):
                    storage.receipt_references([second],strict=True,generation_cache=cache,_batch=batch)
                batch.finish()
            finally:batch.close()

    def test_fresh_metadata_document_does_not_opt_into_guard_seeding(self):
        with self.workspace()as root:
            recipe=root/'generator.json';storage.write(recipe,dict(value='source'))
            pin=self.pin(recipe,role='metadata');first=self.seed(root,'A',dict(input=pin))
            second=self.seed(root,'B',dict(generatorPath=str(recipe),generatorSha256=pin['sha256'],input=pin));batch=self.batch()
            try:
                storage.receipt_references([first],strict=True,_batch=batch);ResourceStorageTest.evict_reference_versions(batch)
                batch.snapshots.clear();batch.snapshot_bytes=0
                original=batch.read_lease;calls=[]
                def lease(path,*args,**kwargs):
                    if Path(path)==recipe:calls.append(dict(kwargs))
                    return original(path,*args,**kwargs)
                with patch.object(batch,'read_lease',side_effect=lease):storage.receipt_references([second],strict=True,_batch=batch)
                reads=[row for row in calls if row.get('stage')=='receipt read']
                self.assertTrue(reads);self.assertTrue(all(row.get('reuse_guard')is False for row in reads));batch.finish()
            finally:batch.close()

    def test_owner_history_exclusion_is_not_shared_between_seeds(self):
        with self.workspace()as root:
            leaf=root/'leaf.bytes';leaf.write_bytes(b'leaf');first=self.seed(root,'A',{})
            shared=root/'shared.json';storage.write(shared,dict(history=dict(path=str(first),sha256='0'*64,role='metadata'),leaf=self.pin(leaf,role='payload')))
            storage.write(first,dict(input=self.pin(shared,role='metadata')));second=self.seed(root,'B',dict(input=self.pin(shared,role='metadata')));batch=self.batch()
            try:
                issues=[];before=storage.receipt_references([first],issues,strict=True,_batch=batch)[0]
                self.assertFalse(issues);self.assertIn(leaf,before);ResourceStorageTest.evict_reference_versions(batch)
                after_issues=[];after=storage.receipt_references([second],after_issues,strict=True,_batch=batch)[0]
                self.assertIn(leaf,after)
                self.assertTrue(any(row['path']==str(first)and row['reason']=='changed-retained-recipe'for row in after_issues));batch.finish()
            finally:batch.close()

    def test_changed_bytes_after_guard_shortcut_are_rejected_before_publication(self):
        with self.workspace()as root:
            paths,first=self.inputs(root,1,'metadata');second=self.seed(root,'B',storage.read(first));batch=self.batch();target=paths[0]
            try:
                storage.receipt_references([first],strict=True,_batch=batch);ResourceStorageTest.evict_reference_versions(batch)
                stamp=target.stat();raw=target.read_bytes();target.write_bytes(raw.replace(b'first',b'other'));os.utime(target,ns=(stamp.st_atime_ns,stamp.st_mtime_ns))
                # Syntax reuse is provisional; finish owns the publication boundary.
                storage.receipt_references([second],strict=True,_batch=batch)
                with self.assertRaises(storage._ReceiptBatchChanged):batch.finish()
            finally:batch.close()

    def test_identical_bytes_new_identity_is_rejected_after_metadata_replay(self):
        with self.workspace()as root:
            paths,first=self.inputs(root,1,'metadata');second=self.seed(root,'B',storage.read(first));batch=self.batch();target=paths[0]
            try:
                storage.receipt_references([first],strict=True,_batch=batch);ResourceStorageTest.evict_reference_versions(batch)
                stamp=target.stat();temporary=target.with_suffix('.replacement');temporary.write_bytes(target.read_bytes());os.replace(temporary,target)
                os.utime(target,ns=(stamp.st_atime_ns,stamp.st_mtime_ns));storage.receipt_references([second],strict=True,_batch=batch)
                with self.assertRaises(storage._ReceiptBatchChanged):batch.finish()
            finally:batch.close()

    def test_snapshot_miss_without_read_lease_is_unavailable_not_a_guard_success(self):
        with self.workspace()as root:
            _,first=self.inputs(root,1,'metadata');second=self.seed(root,'B',storage.read(first));batch=self.batch()
            try:
                storage.receipt_references([first],strict=True,_batch=batch);ResourceStorageTest.evict_reference_versions(batch);batch.snapshots.clear();batch.snapshot_bytes=0
                with patch.object(storage,'_open_read_lease',return_value=None):
                    with self.assertRaises(storage._ReceiptBatchUnavailable):storage.receipt_references([second],strict=True,_batch=batch)
            finally:batch.close()

    def add_receipt_snapshot(self, batch, index, *, release=True):
        path = Path('synthetic-snapshot-%08d.json' % index)
        document = dict(value=index)
        digest = '%064x' % index
        key = path, digest
        snapshot = batch.edges(path, digest, None, document)
        expected = storage._retained_size((snapshot.header, snapshot.unique, snapshot.order)) + storage._retained_size(key) + 384
        self.assertEqual(snapshot.retained_bytes, expected)
        self.assertEqual(batch.snapshot_bytes, sum(item.retained_bytes for item in batch.snapshots.values()))
        self.assertLessEqual(batch.snapshot_bytes, batch.maximum_snapshot_bytes)
        self.assertLessEqual(batch.retained_bytes(), batch.maximum_bytes)
        if release:
            batch.release(snapshot)
        return snapshot

    def test_more_than_the_old_count_limit_retains_each_fully_accounted_tiny_snapshot(self):
        batch = storage._ReceiptBatch()
        try:
            count = batch.maximum_version_entries + 104
            for index in range(count):
                self.add_receipt_snapshot(batch, index)
            self.assertEqual(len(batch.snapshots), count)
            self.assertEqual(batch.evictions['snapshots'], 0)
            self.assertEqual(batch.snapshot_bytes, sum(item.retained_bytes for item in batch.snapshots.values()))
            self.assertFalse(batch.active)
            self.assertEqual(batch.active_bytes, 0)
        finally:
            batch.close()
        self.assertEqual(batch.retained_bytes(), batch.versions.maximum_leases * 8192)

    def test_byte_pressure_evicts_lru_and_counts_every_eviction(self):
        batch = storage._ReceiptBatch()
        try:
            first = self.add_receipt_snapshot(batch, 0)
            batch.maximum_snapshot_bytes = first.retained_bytes * 2
            self.add_receipt_snapshot(batch, 1)
            self.assertEqual(batch.evictions['snapshots'], 0)
            self.add_receipt_snapshot(batch, 2)
            self.assertEqual(batch.evictions['snapshots'], 1)
            self.assertEqual(len(batch.snapshots), 2)
            self.assertNotIn((Path('synthetic-snapshot-%08d.json' % 0), '%064x' % 0), batch.snapshots)
        finally:
            batch.close()

    def test_evicted_active_snapshot_still_counts_against_total_budget(self):
        batch = storage._ReceiptBatch()
        try:
            active = self.add_receipt_snapshot(batch, 0, release=False)
            batch.maximum_snapshot_bytes = active.retained_bytes * 2
            self.add_receipt_snapshot(batch, 1)
            self.add_receipt_snapshot(batch, 2)
            self.assertNotIn(active, batch.snapshots.values())
            self.assertEqual(batch.active_bytes, active.retained_bytes)
            self.assertGreaterEqual(batch.retained_bytes(), batch.snapshot_bytes + batch.active_bytes)
            batch.maximum_bytes = batch.versions.maximum_leases * 8192 + active.retained_bytes - 1
            with self.assertRaises(storage._ReceiptBatchUnavailable):
                batch.check_budget()
            batch.release(active)
        finally:
            batch.close()


    def replay_generation(self,root,name='generation',child=None):
        owner=self.generation(root,name=name);document=storage.read(owner)
        leaf=Path(document['outputs'][0]['path']);extra=root/(name+'-metadata.json')
        storage.write(extra,dict(plainDiagnostic=True))
        document['profile']=dict(extra=self.pin(extra,role='metadata'),numeric=list(range(3000)))
        document['rows']=[dict(nativePacketPath=str(leaf),nativePacketSha256=storage.sha(leaf),
            packetPath=str(leaf),packetSha256=storage.sha(leaf),numeric=list(range(1000)))]
        if child is not None:
            document['sourceGeneration']=str(child);document['sourceGenerationSha256']=storage.sha(child)
            document['inputs']=[self.pin(child,role='metadata'),self.pin(child,role='metadata')]
        storage.write(owner,document);return owner,document

    def prime_replay_snapshot(self,batch,path,role=None):
        digest=storage.sha(path);document=batch.document(path,None,role)
        snapshot=batch.edges(path,None,role,storage.normalize_generation(path,document,{}))
        batch.release(snapshot);return digest,snapshot

    def close_replay_batch(self,batch):
        batch.close()
        self.assertFalse(batch.pending);self.assertFalse(batch.active)
        self.assertEqual(batch.active_bytes,0);self.assertEqual(batch.versions.held_leases,0)

    def test_typed_header_keeps_every_normalizer_input_and_original_stream(self):
        with self.workspace() as root:
            child, _ = self.replay_generation(root, 'child')
            owner, original = self.replay_generation(root, 'parent', child)
            batch = storage._ReceiptBatch()
            try:
                _, snapshot = self.prime_replay_snapshot(batch, owner)
                expected = storage.normalize_generation(owner, original, {})
                replay = storage.normalize_generation(owner, snapshot.header, {})
                self.assertEqual(expected['inputs'], replay['inputs'])
                self.assertEqual(expected['outputs'], replay['outputs'])
                self.assertEqual(list(snapshot), list(storage._reference_edges(expected)))
                self.assertNotIn('profile', snapshot.header)
                self.assertNotIn('numeric', snapshot.header['rows'][0])
                self.assertLess(snapshot.retained_bytes, storage._retained_size(original))
                batch.finish()
            finally:
                self.close_replay_batch(batch)


    def test_read_lease_lru_eviction_transfers_original_snapshot_once(self):
        with self.workspace() as root:
            owner, original = self.replay_generation(root)
            batch = storage._ReceiptBatch()
            try:
                digest, snapshot = self.prime_replay_snapshot(batch, owner)
                key = batch.snapshot_key(owner, digest)
                lease = batch.read_lease
                @contextlib.contextmanager
                def evict_inside_lease(*args, **kwargs):
                    with lease(*args, **kwargs) as identity:
                        while batch.evict_snapshot():
                            pass
                        self.assertNotIn(key, batch.snapshots)
                        self.assertIs(batch.pending[key], snapshot)
                        self.assertEqual(batch.active_bytes, snapshot.retained_bytes)
                        self.assertLessEqual(batch.retained_bytes(), batch.maximum_bytes)
                        yield identity
                with patch.object(batch, 'read_lease', side_effect=evict_inside_lease):
                    header = batch.document(owner, digest, 'metadata', fresh=True)
                expected = storage.normalize_generation(owner, original, {})
                transferred = batch.edges(owner, digest, 'metadata', storage.normalize_generation(owner, header, {}))
                self.assertIs(transferred, snapshot)
                self.assertEqual(list(transferred), list(storage._reference_edges(expected)))
                self.assertFalse(batch.pending)
                self.assertEqual(batch.active[id(snapshot)][0], 1)
                batch.release(transferred)
                self.assertFalse(batch.active)
                self.assertEqual(batch.active_bytes, 0)
                batch.finish()
            finally:
                self.close_replay_batch(batch)


    def test_role_and_owner_variants_still_enter_fresh_lease(self):
        with self.workspace() as root:
            owner, _ = self.replay_generation(root)
            batch = storage._ReceiptBatch()
            try:
                digest, snapshot = self.prime_replay_snapshot(batch, owner)
                for role in (None, 'metadata'):
                    with patch.object(batch, 'read_lease', wraps=batch.read_lease) as leases:
                        header = batch.document(owner, digest, role, fresh=True)
                    self.assertEqual(leases.call_count, 1)
                    self.assertIs(leases.call_args.kwargs['reuse_guard'], False)
                    self.assertIs(header, snapshot.header)
                    transferred = batch.edges(owner, digest, role, storage.normalize_generation(owner, header, {}))
                    self.assertIs(transferred, snapshot)
                    batch.release(transferred)
                self.assertFalse(batch.active)
                batch.finish()
            finally:
                self.close_replay_batch(batch)


    def test_normalize_failure_releases_pending_handoff_for_next_owner(self):
        with self.workspace() as root:
            owner, _ = self.replay_generation(root)
            batch = storage._ReceiptBatch()
            try:
                self.prime_replay_snapshot(batch, owner)
                failure = ValueError('controlled original normalize failure')
                with patch.object(storage, 'normalize_generation', side_effect=failure):
                    with self.assertRaises(ValueError) as caught:
                        storage.receipt_references([owner], strict=True, _batch=batch)
                self.assertIs(caught.exception, failure)
                self.assertFalse(batch.pending)
                self.assertFalse(batch.active)
                self.assertEqual(batch.active_bytes, 0)
                live, records = storage.receipt_references([owner], strict=True, _batch=batch)
                self.assertTrue(live)
                self.assertEqual(set(records[owner]), {'kind', 'schemaVersion', 'state', 'outputs'})
                batch.finish()
            finally:
                self.close_replay_batch(batch)


    def test_read_lease_failure_releases_held_snapshot_without_overwriting_error(self):
        with self.workspace() as root:
            owner, _ = self.replay_generation(root)
            batch = storage._ReceiptBatch()
            try:
                digest, _ = self.prime_replay_snapshot(batch, owner)
                failure = storage._ReceiptBatchUnavailable('controlled lease failure')
                with patch.object(batch, 'read_lease', side_effect=failure):
                    with self.assertRaises(storage._ReceiptBatchUnavailable) as caught:
                        batch.document(owner, digest, 'metadata', fresh=True)
                self.assertIs(caught.exception, failure)
                self.assertFalse(batch.pending)
                self.assertFalse(batch.active)
                self.assertEqual(batch.active_bytes, 0)
            finally:
                self.close_replay_batch(batch)


    def test_nonstrict_failed_child_releases_its_hold_before_next_child(self):
        with self.workspace() as root:
            first, _ = self.replay_generation(root, 'first')
            second, _ = self.replay_generation(root, 'second')
            owner = root / 'owner.json'
            storage.write(owner, dict(inputs=[self.pin(first, role='metadata'), self.pin(second, role='metadata')]))
            batch = storage._ReceiptBatch()
            try:
                _, first_snapshot = self.prime_replay_snapshot(batch, first)
                normalizer = storage.normalize_generation
                visits = []
                def fail_first(path, document, cache):
                    visits.append(path)
                    if path == first:
                        raise ValueError('controlled failed child')
                    if path == second:
                        self.assertNotIn(id(first_snapshot), batch.active)
                        self.assertNotIn(batch.snapshot_key(first, storage.sha(first)), batch.pending)
                    return normalizer(path, document, cache)
                issues = []
                with patch.object(storage, 'normalize_generation', side_effect=fail_first):
                    live, records = storage.receipt_references([owner], issues, _batch=batch)
                self.assertIn(second, visits)
                self.assertIn(second, records)
                self.assertNotIn(first, records)
                self.assertEqual(len(issues), 1)
                self.assertTrue(live)
                self.assertFalse(batch.pending)
                self.assertFalse(batch.active)
            finally:
                self.close_replay_batch(batch)


    def test_pending_replacement_and_double_document_do_not_leak_holds(self):
        with self.workspace() as root:
            owner, original = self.replay_generation(root)
            batch = storage._ReceiptBatch()
            try:
                digest, first = self.prime_replay_snapshot(batch, owner)
                key = batch.snapshot_key(owner, digest)
                batch.document(owner, digest, None)
                batch.document(owner, digest, 'metadata')
                self.assertEqual(batch.active[id(first)][0], 1)
                second = storage._ReceiptSnapshot(first.header, storage.normalize_generation(owner, original, {}))
                batch.pend_snapshot(key, second)
                self.assertNotIn(id(first), batch.active)
                self.assertEqual(batch.active[id(second)][0], 1)
                transferred = batch.edges(owner, digest, None, storage.normalize_generation(owner, second.header, {}))
                self.assertIs(transferred, second)
                batch.release(transferred)
                self.assertFalse(batch.active)
                self.assertFalse(batch.pending)
            finally:
                self.close_replay_batch(batch)


    def test_pending_hold_budget_rejection_rolls_back_its_active_charge(self):
        with self.workspace() as root:
            owner, _ = self.replay_generation(root)
            batch = storage._ReceiptBatch()
            try:
                digest, snapshot = self.prime_replay_snapshot(batch, owner)
                maximum = batch.maximum_bytes
                batch.maximum_bytes = batch.versions.maximum_leases*8192 + snapshot.retained_bytes - 1
                with patch('builtins.print'):
                    with self.assertRaises(storage._ReceiptBatchUnavailable):
                        batch.document(owner, digest, 'metadata')
                self.assertFalse(batch.active)
                self.assertFalse(batch.pending)
                self.assertEqual(batch.active_bytes, 0)
                batch.maximum_bytes = maximum
                batch.finish()
            finally:
                self.close_replay_batch(batch)


    def test_close_attempts_pending_cleanup_after_version_close_error(self):
        with self.workspace() as root:
            owner, _ = self.replay_generation(root)
            batch = storage._ReceiptBatch()
            digest, _ = self.prime_replay_snapshot(batch, owner)
            batch.document(owner, digest, 'metadata')
            close = batch.versions.close
            failure = OSError('controlled version close failure')
            def fail_after_close():
                close()
                raise failure
            with patch.object(batch.versions, 'close', side_effect=fail_after_close):
                with self.assertRaises(OSError) as caught:
                    batch.close()
            self.assertIs(caught.exception, failure)
            self.assertFalse(batch.pending)
            self.assertFalse(batch.active)
            self.assertEqual(batch.active_bytes, 0)
            self.assertEqual(batch.versions.held_leases, 0)


    def test_legacy_cached_header_preserves_protocol_and_revalidates_payload(self):
        with self.workspace() as root:
            source = root / 'source'
            source.mkdir()
            owner, mesh, _, original = ResourceStorageTest.legacy_generation(source, root / 'legacy')
            original['profile'] = dict(numbers=list(range(4000)))
            original['rows'][0].update(nativePacketPath=str(mesh), nativePacketSha256=storage.sha(mesh),
                                       packetPath=str(mesh), packetSha256=storage.sha(mesh),
                                       diagnosticValues=list(range(1000)))
            storage.write(owner, original)
            batch = storage._ReceiptBatch()
            cache = {}
            try:
                _, records = storage.receipt_references([owner], strict=True, generation_cache=cache, _batch=batch)
                key = batch.snapshot_key(owner, storage.sha(owner))
                snapshot = batch.snapshots[key]
                self.assertNotIn('kind', snapshot.header)
                self.assertNotIn('profile', snapshot.header)
                for name in ('generatorPath', 'generatorSha256', 'sourceInput', 'sourceInputSha256'):
                    self.assertEqual(snapshot.header[name], original[name])
                self.assertEqual(snapshot.header['rows'][0], {name:value for name,value in original['rows'][0].items()
                                                            if name != 'diagnosticValues'})
                before_normalized = storage.normalize_generation(owner, original, cache)
                after_normalized = storage.normalize_generation(owner, snapshot.header, cache)
                self.assertEqual(before_normalized['inputs'], after_normalized['inputs'])
                self.assertEqual(before_normalized['outputs'], after_normalized['outputs'])
                with patch.object(storage, 'sha', wraps=storage.sha) as hashes:
                    _, repeated = storage.receipt_references([owner], strict=True, generation_cache=cache, _batch=batch)
                self.assertTrue(any(Path(call.args[0]) == Path(original['generatorPath']) for call in hashes.call_args_list))
                self.assertEqual(records, repeated)
                ResourceStorageTest.evict_reference_versions(batch)
                packet = storage.read(mesh)
                packet['vertices'][0] = .125
                storage.write(mesh, packet)
                with self.assertRaisesRegex(ValueError, 'legacy generation output changed'):
                    storage.receipt_references([owner], strict=True, generation_cache=cache, _batch=batch)
                self.assertFalse(batch.active)
                self.assertFalse(batch.pending)
            finally:
                self.close_replay_batch(batch)


    def test_compact_replay_keeps_independent_generation_records_and_guards(self):
        with self.workspace() as root:
            child, _ = self.replay_generation(root, 'child')
            owner, _ = self.replay_generation(root, 'parent', child)
            seed = root / 'seed'
            seed.mkdir()
            storage.write(seed / 'ResourceAssembly.json', dict(generation=self.pin(owner, role='metadata')))
            before_cache, after_cache = {}, {}
            before = storage._discover_generations({seed}, [root], generation_cache=before_cache)
            after = storage.discover_generations({seed}, [root], generation_cache=after_cache)
            self.assertEqual(before, after)
            self.assertEqual(before_cache, after_cache)




class ResourceWorkReceiptBatchTest(unittest.TestCase):
    @contextlib.contextmanager
    def workspace(self):
        with ResourceStorageTest.owned_workspace(self)as(root,source,output):
            yield root,source
    def marker(self,root,name,input_pin):
        output=root/'artifacts'/name;output.mkdir()
        marker=storage.work_marker(output)
        storage.write(marker,dict(schemaVersion=1,kind='CharacterResourceWork',output=str(output),state='completed',
            modifiedNs=1,sourceInput=input_pin,files=[]))
        return marker
    def input(self,root):
        payload=root/'artifacts'/'payload.bytes';payload.write_bytes(b'payload')
        recipe=root/'artifacts'/'recipe.json'
        storage.write(recipe,dict(input=dict(path=str(payload),sha256=storage.sha(payload),role='payload'),values=list(range(100))))
        return payload,recipe,dict(path=str(recipe),sha256=storage.sha(recipe),role='metadata')
    def test_registered_work_reuses_syntax_and_keeps_immutable_source_hashes(self):
        with self.workspace()as(root,_):
            payload,recipe,pin=self.input(root)
            first=self.marker(root,'A',pin);second=self.marker(root,'B',pin)
            with patch.object(storage,'producer_processes',return_value=[]):
                expected=storage._discover_registered_work([root/'artifacts'],dict(modified=0),())
                batch=storage._ReceiptBatch()
                try:
                    reader=storage.read;original_inputs=storage.work_source_inputs
                    def inputs(*args,**kwargs):
                        self.assertTrue(kwargs['_batch'].active)
                        self.assertGreater(kwargs['_batch'].active_bytes,0)
                        return original_inputs(*args,**kwargs)
                    with patch.object(storage,'read',wraps=reader)as reads,patch.object(storage,'work_source_inputs',side_effect=inputs):
                        actual=storage._discover_registered_work([root/'artifacts'],dict(modified=0),(),generation_cache={},_batch=batch)
                        batch.finish()
                        self.assertEqual(sum(Path(call.args[0])==recipe for call in reads.call_args_list),1)
                    self.assertEqual(actual,expected);self.assertIn(payload,actual[-1])
                    self.assertIsNotNone(batch.guard_for(first));self.assertIsNotNone(batch.guard_for(second))
                    self.assertFalse(batch.active);self.assertFalse(batch.pending)
                finally:batch.close()
                bad=self.marker(root,'C',dict(pin,sha256='0'*64))
                batch=storage._ReceiptBatch()
                try:
                    result=storage._discover_registered_work([root/'artifacts'],dict(modified=0),(),generation_cache={},_batch=batch)
                    batch.finish()
                    self.assertTrue(any(row['path']==str(bad)and row['reason']=='A resource producer input changed'for row in result[1]))
                finally:batch.close()
    def test_work_marker_change_after_eviction_aborts_before_publication(self):
        with self.workspace()as(root,_):
            _,_,pin=self.input(root);first=self.marker(root,'A',pin);self.marker(root,'B',pin)
            batch=storage._ReceiptBatch();batch.versions.maximum_leases=1
            try:
                with patch.object(storage,'producer_processes',return_value=[]):
                    storage._discover_registered_work([root/'artifacts'],dict(modified=0),(),generation_cache={},_batch=batch)
                batch.versions.close()
                stamp=first.stat();value=first.read_bytes().replace(b'"modifiedNs": 1',b'"modifiedNs": 2')
                first.write_bytes(value);os.utime(first,ns=(stamp.st_atime_ns,stamp.st_mtime_ns))
                with self.assertRaises(storage._ReceiptBatchChanged):batch.finish()
            finally:batch.close()
    def test_unavailable_snapshot_discards_provisional_cache_before_later_pass(self):
        cache={'initial':{'value':True}};calls=[]
        def collect(*args,**kwargs):
            calls.append(kwargs.get('_batch'))
            if kwargs.get('_batch')is not None:
                kwargs['generation_cache']['discarded']={'value':False}
                raise storage._ReceiptBatchUnavailable('injected unavailable API')
            self.assertEqual(kwargs['generation_cache'],{'initial':{'value':True}})
            return [],[],[],set(),set()
        with patch.object(storage,'_discover_registered_work',side_effect=collect),\
                patch.object(storage,'producer_processes',return_value=[]),\
                patch.object(storage,'walk',return_value=iter(())),\
                patch.object(storage,'discover_generations',return_value=([],[])),\
                patch('builtins.print'):
            result=storage.discover_work([],dict(modified=0),set(),True,generation_cache=cache)
        self.assertEqual(len(calls),2);self.assertEqual(cache,{'initial':{'value':True}})
        self.assertEqual(result,([],[],[],set()))
    def test_changed_final_guard_aborts_before_write_walk_and_cache_commit(self):
        cache={'initial':{'value':True}}
        def collect(*args,**kwargs):
            kwargs['generation_cache']['discarded']={'value':False}
            return [],[],[],set(),set()
        with patch.object(storage,'_discover_registered_work',side_effect=collect),\
                patch.object(storage._ReceiptBatch,'finish',side_effect=storage._ReceiptBatchChanged('changed marker')),\
                patch.object(storage,'walk')as walk,patch.object(storage,'write')as write:
            with self.assertRaises(storage._ReceiptBatchChanged):
                storage.discover_work([],dict(modified=0),set(),True,generation_cache=cache)
            walk.assert_not_called();write.assert_not_called()
        self.assertEqual(cache,{'initial':{'value':True}})
    def test_partial_marker_write_occurs_after_checkpoint_and_snapshot_handles_are_closed(self):
        with self.workspace()as(root,source):
            partial=self.partial_source_work(root,source);legacy=partial['directory']
            instances,Batch=self.tracked_receipt_batches()
            def collect(*args,**kwargs):return [],[],[],set(),set()
            writer=storage.write
            def write(path,value):
                self.assertEqual(instances[0].finishes,1)
                self.assertFalse(instances[0].closed);self.assertEqual(instances[0].versions.held_leases,0)
                return writer(path,value)
            with patch.object(storage,'_ReceiptBatch',Batch),patch.object(storage,'_discover_registered_work',side_effect=collect), \
                    patch.object(storage,'producer_processes',return_value=[]),patch.object(storage,'walk',return_value=[(legacy,[])]), \
                    patch.object(storage,'has_payloads',return_value=True),patch.object(storage,'partial_catalog',return_value=partial),patch.object(storage,'write',side_effect=write):
                records,_,_,_=storage.discover_work([root/'artifacts'],dict(modified=10),set(),True)
            self.assertEqual(len(records),1);self.assertTrue(storage.work_marker(legacy).is_file())
            self.assertEqual(instances[0].finishes,2);self.assertTrue(instances[0].closed)

    def test_same_metadata_root_reacquires_identity_without_rehash_after_version_eviction(self):
        with self.workspace()as(root,_):
            payload,recipe,pin=self.input(root)
            record=dict(sourceInput=pin);cache={};batch=storage._ReceiptBatch()
            try:
                expected=storage.work_source_inputs(record,generation_cache={})
                first=storage.work_source_inputs(record,generation_cache=cache,_batch=batch)
                ResourceStorageTest.evict_reference_versions(batch)
                self.assertIsNotNone(batch.guard_for(recipe))
                original_sha=storage.sha
                with patch.object(storage,'sha',wraps=original_sha)as hashes,patch.object(batch,'read_lease',wraps=batch.read_lease)as leases:
                    second=storage.work_source_inputs(record,generation_cache=cache,_batch=batch)
                self.assertEqual(first,expected);self.assertEqual(second,expected);self.assertIn(payload,second)
                self.assertEqual(sum(Path(call.args[0])==recipe for call in hashes.call_args_list),0)
                self.assertTrue(any(call.kwargs.get('reuse_guard')is True for call in leases.call_args_list))
                self.assertLessEqual(batch.versions.held_leases,batch.versions.maximum_leases)
                batch.finish()
            finally:batch.close()

    def test_same_size_same_mtime_changed_metadata_root_invalidates_the_batch(self):
        with self.workspace()as(root,_):
            _,recipe,pin=self.input(root);batch=storage._ReceiptBatch()
            try:
                storage.work_source_inputs(dict(sourceInput=pin),generation_cache={},_batch=batch)
                ResourceStorageTest.evict_reference_versions(batch);stamp=recipe.stat()
                raw=recipe.read_bytes();changed=raw.replace(b'"values"',b'"Values"',1)
                self.assertEqual(len(raw),len(changed));self.assertNotEqual(raw,changed)
                recipe.write_bytes(changed);os.utime(recipe,ns=(stamp.st_atime_ns,stamp.st_mtime_ns))
                with self.assertRaises(storage._ReceiptBatchChanged):
                    storage.work_source_inputs(dict(sourceInput=pin),generation_cache={},_batch=batch)
            finally:batch.close()

    def test_identical_bytes_replacement_keeps_the_original_identity_guard(self):
        with self.workspace()as(root,_):
            _,recipe,pin=self.input(root);batch=storage._ReceiptBatch()
            try:
                storage.work_source_inputs(dict(sourceInput=pin),generation_cache={},_batch=batch)
                ResourceStorageTest.evict_reference_versions(batch);stamp=recipe.stat();raw=recipe.read_bytes()
                replacement=recipe.with_suffix('.replacement');replacement.write_bytes(raw);os.replace(replacement,recipe)
                os.utime(recipe,ns=(stamp.st_atime_ns,stamp.st_mtime_ns))
                with self.assertRaises(storage._ReceiptBatchChanged):
                    storage.work_source_inputs(dict(sourceInput=pin),generation_cache={},_batch=batch)
            finally:batch.close()

    def test_wrong_requested_digest_stays_an_independent_work_input_failure(self):
        with self.workspace()as(root,_):
            _,recipe,pin=self.input(root);batch=storage._ReceiptBatch()
            try:
                expected=storage.work_source_inputs(dict(sourceInput=pin),generation_cache={},_batch=batch)
                ResourceStorageTest.evict_reference_versions(batch)
                with self.assertRaisesRegex(ValueError,'producer input changed'):
                    storage.work_source_inputs(dict(sourceInput=dict(pin,sha256='0'*64)),generation_cache={},_batch=batch)
                actual=storage.work_source_inputs(dict(sourceInput=pin),generation_cache={},_batch=batch)
                self.assertEqual(actual,expected);self.assertEqual(batch.guard_for(recipe)[0],pin['sha256']);batch.finish()
            finally:batch.close()

    def test_payload_root_keeps_the_original_non_opt_in_path(self):
        with self.workspace()as(root,_):
            payload,_,_=self.input(root);pin=dict(path=str(payload),sha256=storage.sha(payload),role='payload');batch=storage._ReceiptBatch()
            try:
                storage.work_source_inputs(dict(sourceInput=pin),generation_cache={},_batch=batch)
                ResourceStorageTest.evict_reference_versions(batch)
                with patch.object(batch,'read_lease',side_effect=AssertionError('A payload work root must not opt into metadata root reuse')):
                    self.assertEqual(storage.work_source_inputs(dict(sourceInput=pin),generation_cache={},_batch=batch),{payload})
                batch.finish()
            finally:batch.close()

    def test_guard_without_coherent_read_lease_is_not_success(self):
        with self.workspace()as(root,_):
            _,_,pin=self.input(root);batch=storage._ReceiptBatch()
            try:
                storage.work_source_inputs(dict(sourceInput=pin),generation_cache={},_batch=batch);ResourceStorageTest.evict_reference_versions(batch)
                with patch.object(storage,'_open_read_lease',return_value=None):
                    with self.assertRaises(storage._ReceiptBatchUnavailable):
                        storage.work_source_inputs(dict(sourceInput=pin),generation_cache={},_batch=batch)
            finally:batch.close()

    def test_dependency_edit_after_root_reuse_is_still_rejected_at_finish(self):
        with self.workspace()as(root,_):
            payload,_,pin=self.input(root);batch=storage._ReceiptBatch()
            try:
                storage.work_source_inputs(dict(sourceInput=pin),generation_cache={},_batch=batch);ResourceStorageTest.evict_reference_versions(batch)
                storage.work_source_inputs(dict(sourceInput=pin),generation_cache={},_batch=batch);ResourceStorageTest.evict_reference_versions(batch)
                stamp=payload.stat();raw=payload.read_bytes();payload.write_bytes(bytes([raw[0]^1])+raw[1:]);os.utime(payload,ns=(stamp.st_atime_ns,stamp.st_mtime_ns))
                with self.assertRaises(storage._ReceiptBatchChanged):batch.finish()
            finally:batch.close()

    def registered_source_work(self,root):
        payload,recipe,pin=self.input(root)
        shared=root/'artifacts'/'shared-recipe.json';storage.write(shared,dict(input=dict(path=str(payload),sha256=storage.sha(payload),role='payload')))
        document=storage.read(recipe);document['shared']=dict(path=str(shared),sha256=storage.sha(shared),role='metadata');storage.write(recipe,document);pin['sha256']=storage.sha(recipe)
        marker=self.marker(root,'A',pin);document=storage.read(marker);document['modifiedNs']=20;storage.write(marker,document)
        owner=marker.parent.parent/'A'/'ResourceAssembly.json';storage.write(owner,dict(input=pin))
        return payload,recipe,pin,marker,owner

    def partial_source_work(self,root,source):
        output=root/'artifacts'/'Legacy';(output/'Catalog').mkdir(parents=True);payload=output/'Catalog'/'data.bundle';payload.write_bytes(b'owned')
        source_path=source/'Catalog'/storage.RESOURCE_FILES['manifest']
        return dict(directory=output,modified=1,sourceManifest=dict(path=str(source_path),sha256=storage.sha(source_path)),guards=[],owned_payloads={payload:storage.sha(payload)})

    def tracked_receipt_batches(self):
        instances=[];base=storage._ReceiptBatch
        class Batch(base):
            def __init__(self):
                super().__init__();self.finishes=0;self.finish_attempts=0;self.closed=False;self.guarded_at_close=set();instances.append(self)
            def finish(self):
                self.finish_attempts+=1;super().finish();self.finishes+=1
            def close(self):
                self.guarded_at_close=set(self.guards);super().close();self.closed=True
        return instances,Batch

    def test_same_batch_syntax_reuse_keeps_two_finish_checkpoints(self):
        with self.workspace()as(root,source):
            payload,recipe,pin,marker,owner=self.registered_source_work(root);instances,Batch=self.tracked_receipt_batches();reader=storage.read;cache={}
            with patch.object(storage,'producer_processes',return_value=[]),patch.object(storage,'_ReceiptBatch',Batch),patch.object(storage,'read',wraps=reader)as reads:
                result=storage.discover_work([root/'artifacts'],dict(modified=0),{source},False,generation_cache=cache)
            self.assertEqual(len(instances),1);self.assertEqual(instances[0].finishes,2);self.assertTrue(instances[0].closed)
            self.assertIn(payload,result[-1]);self.assertEqual(sum(Path(call.args[0])==root/'artifacts'/'shared-recipe.json'for call in reads.call_args_list),1)
            self.assertTrue(cache.get(pin['sha256'],{}).get('typedPayloadAuthority')is None)
            self.assertTrue(all(not batch.versions and batch.ledger_bytes==0 for batch in instances))

    def test_first_finish_failure_has_no_partial_walk_write_or_cache_commit(self):
        cache={'initial':{'value':True}}
        def collect(*args,**kwargs):kwargs['generation_cache']['discarded']={'value':False};return [],[],[],set(),set()
        with patch.object(storage,'_discover_registered_work',side_effect=collect),patch.object(storage._ReceiptBatch,'finish',side_effect=storage._ReceiptBatchChanged('changed input')),\
                patch.object(storage,'walk')as walk,patch.object(storage,'write')as write:
            with self.assertRaises(storage._ReceiptBatchChanged):storage.discover_work([],dict(modified=0),set(),True,generation_cache=cache)
            walk.assert_not_called();write.assert_not_called()
        self.assertEqual(cache,{'initial':{'value':True}})

    def test_guarded_failed_marker_discards_reuse_before_own_write(self):
        with self.workspace()as(root,source):
            partial=self.partial_source_work(root,source);marker=storage.work_marker(partial['directory']);storage.write(marker,dict(kind='failed-invalid-marker'))
            instances,Batch=self.tracked_receipt_batches();writer=storage.write;writes=[]
            def write(path,value):
                self.assertEqual(path,marker);self.assertIn(storage._ReceiptBatch.guard_key(marker),instances[0].guarded_at_close)
                self.assertTrue(instances[0].closed);self.assertEqual(instances[0].versions.held_leases,0);writes.append(path);return writer(path,value)
            with patch.object(storage,'producer_processes',return_value=[]),patch.object(storage,'_ReceiptBatch',Batch),\
                    patch.object(storage,'partial_catalog',side_effect=lambda path,*args:partial if path==partial['directory']else None),patch.object(storage,'write',side_effect=write):
                result=storage.discover_work([root/'artifacts'],dict(modified=10),{source},True,generation_cache={})
            self.assertEqual(writes,[marker]);self.assertEqual(instances[0].finishes,1);self.assertEqual(len(instances),2)
            self.assertTrue(all(batch.closed for batch in instances));self.assertEqual(storage.read(marker)['state'],'superseded')
            self.assertTrue(any(row['path']==str(marker)and 'Unknown resource work ownership'in row['reason']for row in result[2]))

    def test_unguarded_marker_write_drains_leases_but_preserves_the_input_guard(self):
        with self.workspace()as(root,source):
            _,recipe,_,_,_=self.registered_source_work(root);partial=self.partial_source_work(root,source);marker=storage.work_marker(partial['directory'])
            instances,Batch=self.tracked_receipt_batches();writer=storage.write;writes=[]
            def write(path,value):
                self.assertEqual(path,marker);self.assertEqual(instances[0].finishes,1);self.assertFalse(instances[0].closed)
                self.assertEqual(instances[0].versions.held_leases,0);self.assertIsNotNone(instances[0].guard_for(recipe));self.assertTrue(instances[0].snapshots)
                writes.append(path);return writer(path,value)
            with patch.object(storage,'producer_processes',return_value=[]),patch.object(storage,'_ReceiptBatch',Batch),\
                    patch.object(storage,'partial_catalog',side_effect=lambda path,*args:partial if path==partial['directory']else None),patch.object(storage,'write',side_effect=write):
                storage.discover_work([root/'artifacts'],dict(modified=10),{source},True,generation_cache={})
            self.assertEqual(writes,[marker]);self.assertEqual(len(instances),1);self.assertEqual(instances[0].finishes,2)

    def test_phase_one_input_changed_between_phases_aborts_union_finish(self):
        import os
        with self.workspace()as(root,source):
            self.registered_source_work(root);recipe=root/'artifacts'/'shared-recipe.json';partial=self.partial_source_work(root,source);marker=storage.work_marker(partial['directory'])
            instances,Batch=self.tracked_receipt_batches()
            writer=storage.write;cache={};generations=storage._discover_generations;calls=[]
            def write(path,value):
                result=writer(path,value);stamp=recipe.stat();raw=recipe.read_bytes();changed=raw.replace(b'"input"',b'"Input"');self.assertNotEqual(raw,changed);recipe.write_bytes(changed);os.utime(recipe,ns=(stamp.st_atime_ns,stamp.st_mtime_ns));return result
            def generation(*args,**kwargs):
                calls.append(kwargs.get('_batch'));kwargs['generation_cache']['second-provisional']={'value':True};return generations(*args,**kwargs)
            with patch.object(storage,'producer_processes',return_value=[]),patch.object(storage,'_ReceiptBatch',Batch),patch.object(storage,'partial_catalog',side_effect=lambda path,*args:partial if path==partial['directory']else None),\
                    patch.object(storage,'write',side_effect=write),patch.object(storage,'_discover_generations',side_effect=generation):
                with self.assertRaises(storage._ReceiptBatchChanged):storage.discover_work([root/'artifacts'],dict(modified=10),{source},True,generation_cache=cache)
            self.assertEqual(len(calls),1);self.assertIsNotNone(calls[0]);self.assertNotIn('second-provisional',cache)
            self.assertEqual(instances[0].finish_attempts,2);self.assertEqual(instances[0].finishes,1)
            self.assertTrue(marker.is_file());self.assertFalse(calls[0].versions);self.assertEqual(calls[0].ledger_bytes,0)

    def test_second_stage_unavailable_rolls_back_only_its_cache_without_rewriting_marker(self):
        with self.workspace()as(root,source):
            self.registered_source_work(root);partial=self.partial_source_work(root,source);marker=storage.work_marker(partial['directory']);cache={}
            original=storage._discover_generations;writer=storage.write;calls=[];writes=[]
            def generations(*args,**kwargs):
                calls.append(kwargs.get('_batch'))
                if len(calls)==1:
                    kwargs['generation_cache']['discarded-second']={'value':False};raise storage._ReceiptBatchUnavailable('second phase optimization unavailable')
                self.assertNotIn('discarded-second',kwargs['generation_cache']);return original(*args,**kwargs)
            def write(path,value):writes.append(path);return writer(path,value)
            with patch.object(storage,'producer_processes',return_value=[]),patch.object(storage,'partial_catalog',side_effect=lambda path,*args:partial if path==partial['directory']else None),\
                    patch.object(storage,'_discover_generations',side_effect=generations),patch.object(storage,'write',side_effect=write):
                storage.discover_work([root/'artifacts'],dict(modified=10),{source},True,generation_cache=cache)
            self.assertEqual(writes,[marker]);self.assertEqual(len(calls),2);self.assertIsNot(calls[0],calls[1]);self.assertNotIn('discarded-second',cache)

    def test_default_none_cache_still_begins_a_fresh_generation_stage(self):
        def collect(*args,**kwargs):kwargs['generation_cache']['work-only']={'value':True};return [],[],[],set(),set()
        def generation(*args,**kwargs):self.assertEqual(kwargs['generation_cache'],{});return [],[]
        with patch.object(storage,'_discover_registered_work',side_effect=collect),patch.object(storage,'_discover_generations',side_effect=generation),\
                patch.object(storage,'producer_processes',return_value=[]),patch.object(storage,'walk',return_value=iter(())):
            self.assertEqual(storage.discover_work([],dict(modified=0),set(),False),([],[],[],set()))

    def test_union_budget_exhaustion_retains_the_original_independent_stage_fallback(self):
        with self.workspace()as(root,source):
            self.registered_source_work(root);cache={};original=storage._discover_generations;calls=[]
            def generations(*args,**kwargs):
                batch=kwargs.get('_batch');calls.append(batch)
                if len(calls)==1:batch.maximum_bytes=1
                return original(*args,**kwargs)
            with patch.object(storage,'producer_processes',return_value=[]),patch.object(storage,'_discover_generations',side_effect=generations):
                result=storage.discover_work([root/'artifacts'],dict(modified=10),{source},False,generation_cache=cache)
            self.assertEqual(len(calls),2);self.assertIsNot(calls[0],calls[1]);self.assertFalse(calls[0].versions)
            self.assertEqual(calls[1].maximum_bytes,storage._ReceiptBatch.maximum_bytes);self.assertTrue(result[-1])

    def test_public_generation_collector_still_owns_one_independent_batch(self):
        with self.workspace()as(root,source):
            _,_,_,_,owner=self.registered_source_work(root);instances,Batch=self.tracked_receipt_batches()
            with patch.object(storage,'_ReceiptBatch',Batch):storage.discover_generations({owner.parent},[root/'artifacts'],generation_cache={})
            self.assertEqual(len(instances),1);self.assertEqual(instances[0].finishes,1);self.assertTrue(instances[0].closed)

    def test_combined_results_and_cache_match_independent_validation_fallback(self):
        with self.workspace()as(root,source):
            _,recipe,pin,marker,owner=self.registered_source_work(root)
            generation=ResourceReceiptBatchTest.generation(root/'artifacts')
            document=storage.read(recipe);document['generation']=dict(path=str(generation),sha256=storage.sha(generation),role='metadata');storage.write(recipe,document);pin['sha256']=storage.sha(recipe)
            document=storage.read(marker);document['sourceInput']=pin;storage.write(marker,document);storage.write(owner,dict(input=pin))
            expected_cache={};actual_cache={}
            with patch.object(storage,'producer_processes',return_value=[]):
                with patch.object(storage._ReceiptBatch,'maximum_bytes',1),patch('builtins.print'):
                    expected=storage.discover_work([root/'artifacts'],dict(modified=10),{source},False,generation_cache=expected_cache)
                actual=storage.discover_work([root/'artifacts'],dict(modified=10),{source},False,generation_cache=actual_cache)
            self.assertEqual(actual,expected);self.assertEqual(actual_cache,expected_cache)
            self.assertTrue(any(row.get('kind')=='generation'for row in actual[0]))


class ResourceGuardKeyTest(unittest.TestCase):
    workspace = ResourceReceiptBatchTest.workspace
    seed = ResourceReceiptBatchTest.seed
    pin = staticmethod(ResourceReceiptBatchTest.pin)

    def test_installed_path_comparison_unicode_and_dotdot_contract(self):
        pairs = [('A', 'a'), ('ß', 'ss'), ('ẞ', 'ß'), ('ς', 'σ'), ('µ', 'μ'),
                 ('K', 'k'), ('İ', 'i\u0307'), ('é', 'e\u0301'), ('x/../a', 'a')]
        for first, second in pairs:
            a, b = PureWindowsPath('C:/Root') / first, PureWindowsPath('c:/root') / second
            self.assertEqual(a == b, storage._ReceiptBatch.guard_key(a) == storage._ReceiptBatch.guard_key(b))

    def test_case_variants_keep_first_spelling_and_do_not_merge_distinct_unicode(self):
        batch = storage._ReceiptBatch()
        try:
            first = Path('C:/Root/OriginalName.JSON')
            batch.guard(first, 'a' * 64, (1, 2, 3))
            batch.guard(Path(str(first).upper()), 'A' * 64, (1, 2, 3))
            self.assertEqual(len(batch.guards), 1)
            self.assertEqual(batch.guard_for(first), ('a' * 64, (1, 2, 3), str(first)))
            for name in ('ß', 'ss', 'ς', 'σ', 'µ', 'μ', 'é', 'e\u0301'):
                batch.guard(Path('C:/Root') / name, 'a' * 64, (1, 2, 3))
            self.assertEqual(len(batch.guards), 9)
            with self.assertRaises(storage._ReceiptBatchChanged):
                batch.guard(first, 'a' * 64, (1, 2, 4))
            with self.assertRaises(storage._ReceiptBatchChanged):
                batch.guard(first, 'b' * 64, (1, 2, 3))
        finally:
            batch.close()

    def test_finish_reopens_first_spelling_and_checks_real_identity(self):
        with self.workspace() as root:
            file = root / 'RetainedOriginal.JSON'
            file.write_bytes(b'{"value":1}')
            batch = storage._ReceiptBatch()
            try:
                with storage.payload_hash_operation():
                    with batch.read_lease(file, storage.sha(file), 'metadata', payload=False, stage='private test'):
                        pass
                batch.guard(Path(str(file).upper()), *batch.guard_for(file)[:2])
                reader = storage.checked_source_path
                with patch.object(storage, 'checked_source_path', wraps=reader) as checks:
                    batch.finish()
                self.assertEqual(checks.call_args_list[-1].args[0], file)
                self.assertEqual(str(checks.call_args_list[-1].args[0]), str(file))
            finally:
                batch.close()

    def test_relative_lookup_reuse_keeps_base_role_fresh_and_absolute_dotdot_validation(self):
        with self.workspace() as root:
            folder = root / 'input'; folder.mkdir()
            file = folder / 'recipe.json'; file.write_bytes(b'{"value":1}')
            sibling = root / 'sibling'; sibling.mkdir()
            raw = '../sibling/../input/recipe.json'
            base = root / 'seed'; base.mkdir()
            batch = storage._ReceiptBatch(); digest = storage.sha(file)
            try:
                batch.retain_pin(raw, base, file, digest, 'metadata', 'metadata', None, False)
                batch.pins.clear()
                self.assertEqual(batch.pin(raw, base, digest, 'metadata', 'metadata', None, False), (file, digest))
                self.assertIsNone(batch.pin(raw, folder, digest, 'metadata', 'metadata', None, False))
                self.assertIsNone(batch.pin(raw, base, digest, 'metadata', 'metadata', None, True))
                self.assertIsNone(batch.pin(raw, base, 'b' * 64, 'metadata', 'metadata', None, False))
                self.assertIsNone(batch.pin(raw, base, digest, 'metadata', 'other', None, False))
                absolute = str(root / 'sibling/../input/recipe.json')
                batch.pins[batch.pin_key(absolute, base, digest, 'metadata', 'metadata', None, False)[1]] = file, (1,)
                self.assertIsNone(batch.pin(absolute, base, digest, 'metadata', 'metadata', None, False))
                batch.finish()
            finally:
                batch.close()

    def test_required_string_guards_still_exhaust_same_budget_without_eviction(self):
        batch = storage._ReceiptBatch()
        try:
            for index in range(20):
                batch.guard(Path(f'C:/Root/source-{index}.json'), 'a' * 64, (index, 1, 2))
            count = len(batch.guards)
            batch.maximum_bytes = batch.retained_bytes() - 1
            with self.assertRaises(storage._ReceiptBatchUnavailable):
                batch.check_budget()
            self.assertEqual(len(batch.guards), count)
            self.assertEqual(batch.evictions['guards'], 0)
        finally:
            batch.close()


class ResourceWorkInputScopeTest(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name).resolve()
        self.artifacts = self.root / 'tools/game-test/artifacts'
        self.workspace = self.root / 'tools/asset-authoring/workspace'
        self.artifacts.mkdir(parents=True)
        self.workspace.mkdir(parents=True)
        self.source = self.root / 'tools/model-authoring'
        self.source.mkdir(parents=True)
        self.patches = [patch.object(storage, 'ARTIFACTS', self.artifacts),
                        patch.object(storage, 'WORKSPACE', self.workspace)]
        for item in self.patches:
            item.start()
            self.addCleanup(item.stop)

    def record(self, path, role):
        return {'sourceInput': {'path': str(path), 'sha256': storage.sha(path), 'role': role}}

    def test_source_recipe_is_validated_without_becoming_a_storage_reference(self):
        payload = self.workspace / 'native.bin'
        payload.write_bytes(b'native source')
        recipe = self.source / 'design.json'
        recipe.write_text(json.dumps({'source': {'path': str(payload), 'sha256': storage.sha(payload), 'role': 'payload'}}))
        record = self.record(recipe, 'metadata')
        self.assertEqual(storage.work_source_inputs(record), {payload})
        recipe.write_text('{}')
        with self.assertRaisesRegex(ValueError, 'producer input changed'):
            storage.work_source_inputs(record)

    def test_owned_recipe_and_its_payload_keep_their_storage_protection(self):
        payload = self.artifacts / 'source.bin'
        payload.write_bytes(b'replay source')
        recipe = self.workspace / 'recipe.json'
        recipe.write_text(json.dumps({'path': str(payload), 'sha256': storage.sha(payload), 'role': 'payload'}))
        self.assertEqual(storage.work_source_inputs(self.record(recipe, 'metadata')), {recipe, payload})

    def test_external_payload_is_hash_checked_but_not_promoted_to_owned_storage(self):
        payload = self.source / 'external.bin'
        payload.write_bytes(b'immutable source')
        record = self.record(payload, 'payload')
        self.assertEqual(storage.work_source_inputs(record), set())
        payload.write_bytes(b'changed')
        with self.assertRaisesRegex(ValueError, 'producer input changed'):
            storage.work_source_inputs(record)


if __name__ == "__main__":
    unittest.main()
