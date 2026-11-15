import importlib.util
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("resource_storage", Path(__file__).parents[1] / "resource_storage.py")
storage = importlib.util.module_from_spec(spec)
spec.loader.exec_module(storage)


class ResourceStorageTest(unittest.TestCase):
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
            source = root / "Source/CharacterModels.manifest.json"
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
            metadata = root / "Candidate/Catalog/CharacterModels.manifest.json"
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
            manifest = candidate / "Catalog/CharacterModels.manifest.json"
            source = root / "Source/CharacterModels.manifest.json"
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
            storage.write(root / "Catalog/CharacterModels.manifest.json", {"bundles": [], "meshes": []})
            storage.write(root / "ResourceAssembly.json", {"status": "PASS_STANDALONE_CHARACTER_RESOURCES", "catalogSha256": "0" * 64})
            catalogs, retained = storage.discover([root])
            self.assertEqual(catalogs, [])
            self.assertEqual(len(retained), 1)

    def test_fully_retired_catalog_does_not_reparse_its_producer_receipt(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory).resolve()
            (root / "Catalog").mkdir()
            storage.write(root / "Catalog/CharacterModels.manifest.json", {"bundles": [], "meshes": []})
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
