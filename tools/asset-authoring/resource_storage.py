"""Close obsolete completed resource catalogs after a newer catalog has been adopted."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools/blender-models/CharacterFaces"))
from build_models import catalog_payloads, create_resource_retirement_plan

ARTIFACTS = ROOT / "tools/game-test/artifacts"
WORKSPACE = ROOT / "tools/asset-authoring/workspace"
PUBLIC = ROOT / "SolastaUnfinishedBusiness/BlenderModels/CharacterModels/CharacterModels.manifest.json"


def read(path):
    return json.loads(Path(path).read_text(encoding="utf-8-sig"))


def sha(path):
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def write(path, data):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(data, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def walk(root):
    for folder, children, names in os.walk(root, followlinks=False):
        if "SolastaUnfinishedBusiness.sln" in names:
            children[:] = []
            continue
        children[:] = [name for name in children if not (Path(folder) / name).is_junction()
                       and not (Path(folder) / name).is_symlink() and name not in ("Tools", "Python", "Library", ".native")]
        yield Path(folder), names


def discover(roots):
    catalogs, retained = [], []
    for root in roots:
        for folder, names in walk(root):
            if "ResourceAssembly.json" not in names:
                continue
            owner = folder / "ResourceAssembly.json"
            manifest = folder / "Catalog/CharacterModels.manifest.json"
            if not manifest.is_file():
                continue
            print("Inspecting resource owner: " + str(folder), flush=True)
            try:
                assembly = read(owner)
                if assembly.get("status") != "PASS_STANDALONE_CHARACTER_RESOURCES" or not manifest.is_file():
                    continue
                fingerprint = sha(manifest)
                if fingerprint != assembly.get("catalogSha256"):
                    raise ValueError("Completed owner does not match its catalog")
                payloads = catalog_payloads(read(manifest))
                catalogs.append(dict(root=root, directory=folder, owner=owner, manifest=manifest,
                    sha256=fingerprint, accepted=assembly.get("artAccepted") is True,
                    modified=owner.stat().st_mtime_ns, payloads=payloads))
            except (ValueError, KeyError, OSError) as error:
                retained.append(dict(path=str(folder), reason=str(error)))
    return catalogs, retained


def compact_references(paths, retired):
    """Expand only directory pins which contain retired payloads; preserve all other data."""
    retired = {Path(path) for path in retired}
    # Retirement-plan validation rejects reparse-point payloads before references are replaced.
    # Resolve each bundle directory once, including when its payloads were already removed.
    parents = {parent: parent.resolve() for parent in {path.parent for path in retired}}
    retired = {parents[path.parent] / path.name for path in retired}
    ancestors = set()
    for directory in set(parents.values()):
        ancestors.add(directory)
        ancestors.update(directory.parents)
    result = set()

    def retain(path):
        if path.is_symlink() or path.is_junction():
            raise ValueError("A storage reference became a reparse point")
        path = path.resolve()
        if path in retired:
            return
        if path in ancestors and path.is_dir():
            for child in path.iterdir():
                retain(child)
        else:
            result.add(str(path))

    for value in paths:
        path = Path(value)
        if not path.is_absolute():
            path = ARTIFACTS / path
        if not any(path.resolve().is_relative_to(root) for root in (ARTIFACTS, WORKSPACE)):
            raise ValueError("A storage reference escaped the owned workspaces")
        retain(path)
    return sorted(result, key=str.casefold)


def select_retired(catalogs, public_sha, keep_accepted):
    adopted = [row for row in catalogs if row["sha256"] == public_sha and row["accepted"]]
    if not adopted:
        raise ValueError("No accepted local catalog matches the adopted product manifest")
    current = max(adopted, key=lambda row: row["modified"])
    previous = sorted((row for row in catalogs if row["accepted"] and row["sha256"] != public_sha),
                      key=lambda row: row["modified"], reverse=True)
    keep = {public_sha}
    keep.update(row["sha256"] for row in previous[:max(0, keep_accepted - 1)])
    old = [row for row in catalogs if row["sha256"] not in keep and row["modified"] < current["modified"]]
    return current, keep, old


def plan(output, apply=False, keep_accepted=2):
    output = output.resolve()
    if not output.is_relative_to(ARTIFACTS):
        raise ValueError("Storage evidence belongs below game-test/artifacts")
    roots = (ARTIFACTS, WORKSPACE)
    catalogs, retained = discover(roots)
    public_sha = sha(PUBLIC)
    try:
        current, keep, old = select_retired(catalogs, public_sha, keep_accepted)
    except ValueError as error:
        record = {"schemaVersion": 1, "status": "retained-no-adopted-owner", "reason": str(error),
                  "plans": [], "completedCatalogs": len(catalogs), "unverifiedRetained": retained}
        write(output / "ResourceLifecycle.json", record)
        print(json.dumps(record))
        return record
    obsolete = {row["directory"] / "Catalog" / name for row in old for name in row["payloads"]
                if Path(name).suffix == ".bundle"}
    references_path = ARTIFACTS / "StorageReferences.json"
    before = read(references_path) if references_path.is_file() else {"schemaVersion": 1, "paths": []}
    if set(before) != {"schemaVersion", "paths"} or before["schemaVersion"] != 1:
        raise ValueError("Unsupported storage references")
    original_sha = sha(references_path) if references_path.exists() else None
    after = {"schemaVersion": 1, "paths": compact_references(before["paths"], obsolete)}
    # Protect the accepted owner and bytes even when a legacy reference file omitted them.
    after["paths"] = sorted(set(after["paths"]) | {str(row["directory"]) for row in catalogs if row["sha256"] in keep})
    preview_reference = output / "RetainedInputs.json"
    write(preview_reference, after)
    plans = []
    for index, root in enumerate(roots):
        owners = [row["directory"] for row in old if row["root"] == root]
        if not owners:
            continue
        destination = output / f"ResourcePayloads-{index}.json"
        result = create_resource_retirement_plan(owners, preview_reference, destination, storage_root=root)
        plans.append({"root": str(root), "plan": str(destination), "files": len(result["files"]),
                      "logicalBytes": result["logicalBytes"]})
    record = {"schemaVersion": 1, "status": "planned", "adopted": str(current["directory"]),
              "adoptedManifestSha256": public_sha, "keepAcceptedCatalogs": keep_accepted,
              "retainedCatalogHashes": sorted(keep), "completedCatalogs": len(catalogs),
              "retiredCatalogs": [str(row["directory"]) for row in old], "unverifiedRetained": retained,
              "referencesBeforeSha256": original_sha, "referencesBeforeCount": len(before["paths"]),
              "referencesAfterCount": len(after["paths"]), "plans": plans,
              "scope": "Declared bundle payloads of completed catalogs older than the adopted catalog. Sources, recipes and proofs remain."}
    if apply:
        if sha(PUBLIC) != public_sha or (sha(references_path) if references_path.exists() else None) != original_sha:
            raise ValueError("Product or references changed during storage planning")
        write(output / "ReferencesBefore.json", before)
        # Replace only the references proved obsolete by completed catalog ownership.
        temporary = references_path.with_suffix(".new.json")
        write(temporary, after)
        os.replace(temporary, references_path)
        record["status"] = "references-closed-payloads-planned"
    write(output / "ResourceLifecycle.json", record)
    print(json.dumps({k: record[k] for k in ("status", "completedCatalogs", "referencesBeforeCount", "referencesAfterCount", "plans")}))
    return record


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--apply", action="store_true")
    parser.add_argument("--keep-accepted", type=int, default=2, choices=range(1, 11))
    args = parser.parse_args()
    plan(args.output, args.apply, args.keep_accepted)


if __name__ == "__main__":
    main()
