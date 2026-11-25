"""Coalesce duplicated bundles in retired catalogs with an exact restore receipt."""
import argparse
from collections import Counter, defaultdict
import contextlib
import json
import os
from pathlib import Path
import re
import shutil
import stat
import tempfile


from resource_storage import (
    ARTIFACTS, ROOT, WORKSPACE, RESOURCE_FILES, _stored_size, catalog_payloads, checked_catalog_path,
    checked_source_path, distribution_catalog, payload_hash_operation, producer_processes,
    read, resource_operation, sha, write,
)


ARCHIVE_NAME = 'CatalogArchive.json'
ARCHIVE_KIND = 'SolastaUBRetiredCharacterCatalog'
BLOBS = WORKSPACE / 'CatalogArchive'
EXTERNAL_LOCATION = 'ExternalStore.json'
DIGEST = re.compile(r'[a-f0-9]{64}')


def owner_path(value):
    path = checked_catalog_path(value)
    if not any(path != folder and path.is_relative_to(folder) for folder in (ARTIFACTS, WORKSPACE)):
        raise ValueError(f'A model archive owner escaped the tool workspaces: {path}')
    return path


def bundle_path(directory, name):
    path = checked_catalog_path(directory, name)
    if path.suffix != '.bundle':
        raise ValueError(f'A model archive can contain only bundle leaves: {name}')
    return path


def blob_path(digest):
    if DIGEST.fullmatch(digest) is None:
        raise ValueError('A model archive blob requires a SHA-256 identity.')
    return bundle_path(BLOBS, digest + '.bundle')


def external_path(path):
    path = Path(path)
    if not path.is_absolute() or path == Path(path.anchor) or path.resolve() != path or path.is_relative_to(BLOBS):
        raise ValueError('The external archive requires an absolute folder outside the local archive.')
    for member in (path, *path.parents):
        if member.is_symlink() or member.exists() and getattr(member.lstat(), 'st_file_attributes', 0) & stat.FILE_ATTRIBUTE_REPARSE_POINT:
            raise ValueError(f'The external archive traverses a link: {member}')
    return path


def external_store():
    locator = checked_catalog_path(BLOBS, EXTERNAL_LOCATION)
    if not locator.is_file():
        return None
    location = read(locator)
    if set(location) != {'schemaVersion', 'directory'} or location['schemaVersion'] != 1:
        raise ValueError('The external archive locator has an unknown format.')
    return external_path(location['directory'])


def verify_blob(path, digest, size):
    identity = file_stamp(path)
    if identity[2] != size or sha(path, payload=True) != digest or file_stamp(path) != identity:
        raise ValueError(f'The archive object differs from its SHA256 identity: {path}')


def copy_blob(source, target, digest, size):
    if target.exists():
        verify_blob(target, digest, size)
        return
    temporary = None
    try:
        with tempfile.NamedTemporaryFile(dir=target.parent, prefix='.catalog-', delete=False) as destination:
            temporary = Path(destination.name)
            with source.open('rb') as data:
                shutil.copyfileobj(data, destination, length=1024 * 1024)
            destination.flush()
            os.fsync(destination.fileno())
        verify_blob(temporary, digest, size)
        if not target.exists():
            try:
                temporary.rename(target)
            except FileExistsError:
                pass
        verify_blob(target, digest, size)
    finally:
        if temporary is not None and temporary.exists():
            temporary.unlink()


def offload_blobs(directory, evidence, apply, *, copy_only=False):
    source = checked_catalog_path(BLOBS)
    directory = external_path(directory)
    previous = external_store()
    if previous is not None and previous != directory:
        raise ValueError('The local archive already refers to another external store.')
    objects = {path.stem: file_stamp(checked_source_path(path)) for path in source.glob('*.bundle')
               if DIGEST.fullmatch(path.stem)}
    result = dict(status='PASS_ARCHIVE_OFFLOAD_PREVIEW', directory=str(directory), files=len(objects),
                  logicalBytes=sum(identity[2] for identity in objects.values()), removed=0,
                  availableBytesBefore=shutil.disk_usage(ROOT).free)
    report = evidence / 'ArchiveOffload.json'
    write(report, result)
    if not (apply or copy_only) or not objects:
        return result
    directory.mkdir(parents=True, exist_ok=True)
    for digest, stamp in objects.items():
        local = blob_path(digest)
        target = external_path(directory / local.name)
        verify_blob(local, digest, stamp[2])
        copy_blob(local, target, digest, stamp[2])
        if file_stamp(local) != stamp:
            raise ValueError(f'A local archive object changed while offloading: {local}')
    sample = min(objects, key=lambda digest: objects[digest][2])
    with tempfile.TemporaryDirectory(dir=evidence) as temporary:
        copy_blob(external_path(directory / (sample + '.bundle')), Path(temporary) / (sample + '.bundle'),
                  sample, objects[sample][2])
    result['sampleRestored'] = sample
    if copy_only:
        result['status'] = 'PASS_ARCHIVE_COPIED'
        write(report, result)
        return result
    write(checked_catalog_path(BLOBS, EXTERNAL_LOCATION), dict(schemaVersion=1, directory=str(directory)))
    for digest, stamp in objects.items():
        local = blob_path(digest)
        if file_stamp(local) != stamp:
            raise ValueError(f'A local archive object changed before removal: {local}')
        target = external_path(directory / local.name)
        verify_blob(target, digest, stamp[2])
        local.unlink()
        result['removed'] += 1
    result.update(status='PASS_ARCHIVE_OFFLOADED', availableBytesAfter=shutil.disk_usage(ROOT).free)
    write(report, result)
    return result


def owner_catalog(owner):
    assembly = read(checked_source_path(owner / 'ResourceAssembly.json'))
    manifest = checked_source_path(owner / 'Catalog' / RESOURCE_FILES['manifest'])
    digest = sha(manifest)
    if assembly.get('status') != 'PASS_STANDALONE_CHARACTER_RESOURCES' or assembly.get('catalogSha256') != digest:
        raise ValueError(f'The catalog differs from its completed owner: {owner}')
    catalog = catalog_payloads(read(manifest))
    return digest, catalog


def retirement_payloads(proof, *, verify_inputs):
    """Read the exact unreferenced payloads selected by the storage planner."""
    payloads = {}
    checked_inputs = set()
    for entry in proof['plans']:
        root = checked_catalog_path(entry['root'])
        if root not in (ARTIFACTS, WORKSPACE):
            raise ValueError(f'A catalog retirement plan escaped the tool workspaces: {root}')
        plan = read(checked_source_path(entry['plan']))
        if (plan['schemaVersion'] != 1 or checked_catalog_path(plan['artifactRoot']) != root
                or len(plan['files']) != entry['files'] or plan.get('previewOnly')):
            raise ValueError('Archive inputs must belong to an applied storage plan.')
        if verify_inputs:
            for pin in [plan['reference'], *plan.get('guardInputs', ())]:
                path = checked_source_path(pin['path'])
                identity = path, pin['sha256']
                if identity not in checked_inputs and sha(path) != pin['sha256']:
                    raise ValueError(f'A retirement plan input changed: {path}')
                checked_inputs.add(identity)
        for row in plan['files']:
            leaf = checked_catalog_path(row['path'])
            if not leaf.is_relative_to(root):
                raise ValueError(f'A retirement payload escaped its workspace: {leaf}')
            if leaf.suffix == '.bundle' and leaf.parent.name == 'Catalog':
                payloads[leaf] = row['sha256'], row['bytes']
    return payloads


def registered_archive_paths():
    path = ARTIFACTS / 'StorageReferences.json'
    if not path.is_file():
        return set()
    references = read(checked_source_path(path))
    if references.get('schemaVersion') != 1 or not isinstance(references.get('paths'), list):
        raise ValueError('The catalog archive cannot read the registered storage references.')
    return {((ARTIFACTS / value) if not Path(value).is_absolute() else Path(value)).resolve()
            for value in references['paths']}


def archive_plan(report, preserve, limit, *, verify_inputs=False):
    proof = read(checked_source_path(report))
    adopted, _ = distribution_catalog()
    if proof.get('status') != 'references-closed-payloads-planned' or proof.get('adoptedManifestSha256') != adopted:
        raise ValueError('Archive only catalogs retired against the currently distributed models.')
    protected = {owner_path(path) for path in (*proof['explicitRetainedDirectories'], *preserve)}
    references = registered_archive_paths()
    payloads = retirement_payloads(proof, verify_inputs=verify_inputs)
    published = set(proof['retainedCatalogHashes']) | {adopted}
    copies = defaultdict(list)
    owners = {}
    for value in sorted(set(proof['retiredCatalogs'])):
        owner = owner_path(value)
        if any(owner.is_relative_to(folder) or folder.is_relative_to(owner) for folder in protected):
            continue
        if not (owner / 'ResourceAssembly.json').is_file() or not (owner / 'Catalog').is_dir():
            continue
        digest, catalog = owner_catalog(owner)
        if digest in published:
            continue
        owners[owner] = digest
        for name, expected in catalog.items():
            leaf = owner / 'Catalog' / name
            selection = payloads.get(leaf)
            if selection is not None and leaf not in references and not references.intersection(leaf.parents) and leaf.is_file():
                info = leaf.stat(follow_symlinks=False)
                if selection != (expected, info.st_size):
                    raise ValueError(f'A retired payload differs from its plan: {leaf}')
                if not stat.S_ISREG(info.st_mode) or getattr(info, 'st_file_attributes', 0) & 0x400:
                    raise ValueError(f'A retired bundle is not a regular file: {leaf}')
                copies[expected].append((owner, name, info.st_size))
    if limit:
        sizes = Counter()
        for rows in copies.values():
            if len(rows) > 1:
                for owner, _, size in rows:
                    sizes[owner] += size
        selected = {owner for owner, _ in sizes.most_common(limit)}
        copies = {digest: [row for row in rows if row[0] in selected] for digest, rows in copies.items()}
    groups = {key: tuple(rows) for key, rows in copies.items() if len(rows) > 1}
    if any(len({row[2] for row in rows}) != 1 for rows in groups.values()):
        raise ValueError('A declared bundle identity has different file lengths.')
    used = {row[0] for rows in groups.values() for row in rows}
    return groups, {owner: owners[owner] for owner in used}


def file_stamp(path):
    info = path.stat()
    return info.st_dev, info.st_ino, info.st_size, info.st_mtime_ns


def archive_receipt(owner, manifest):
    path = checked_catalog_path(owner, ARCHIVE_NAME)
    receipt = read(checked_source_path(path)) if path.is_file() else dict(
        kind=ARCHIVE_KIND, schemaVersion=1, catalogSha256=manifest, entries={})
    if (receipt.get('kind') != ARCHIVE_KIND or receipt.get('schemaVersion') != 1
            or receipt.get('catalogSha256') != manifest or not isinstance(receipt.get('entries'), dict)):
        raise ValueError(f'The archive receipt does not match the catalog: {owner}')
    return path, receipt


def retired_catalog_snapshots(report, selected, preserve):
    proof = read(checked_source_path(report))
    adopted, _ = distribution_catalog()
    if proof.get('status') != 'references-closed-payloads-planned' or proof.get('adoptedManifestSha256') != adopted:
        raise ValueError('Use a storage receipt for the currently distributed catalog.')
    retired = {Path(value) for value in proof['retiredCatalogs']}
    retained = set(proof['retainedCatalogHashes']) | {adopted}
    registry = checked_catalog_path(WORKSPACE / 'ModelReleaseReceipts')
    for path in registry.glob('*.json'):
        release = read(checked_source_path(path))
        if release.get('status') == 'PASS_PUBLISHED_MODEL_RESOURCES':
            retained.add(release['catalogSha256'])
    protected = {owner_path(value) for value in (*proof['explicitRetainedDirectories'], *preserve)}
    references = registered_archive_paths()
    snapshots = {}
    for value in selected:
        owner = owner_path(value)
        if (owner not in retired or owner in snapshots
                or any(owner.is_relative_to(path) or path.is_relative_to(owner) for path in protected)):
            raise ValueError(f'The catalog is not a distinct retired snapshot: {owner}')
        manifest, declared = owner_catalog(owner)
        if manifest in retained:
            raise ValueError(f'The catalog is still an adopted or rollback snapshot: {owner}')
        receipt_path, receipt = archive_receipt(owner, manifest)
        files = {}
        directory = checked_catalog_path(owner / 'Catalog')
        if directory in references:
            raise ValueError(f'The selected bundle catalog is registered as a replay input: {directory}')
        with os.scandir(directory) as entries:
            for entry in entries:
                digest = declared.get(entry.name)
                if digest is None:
                    continue
                leaf = Path(entry.path)
                if leaf in references:
                    continue
                info = regular_bundle(leaf)
                identity = info.st_dev, info.st_ino, info.st_size, info.st_mtime_ns
                files[entry.name] = leaf, digest, identity
        snapshots[owner] = manifest, receipt_path, receipt, files
    return snapshots


def regular_bundle(path):
    info = path.lstat()
    if (not stat.S_ISREG(info.st_mode) or not info.st_ino
            or getattr(info, 'st_file_attributes', 0) & stat.FILE_ATTRIBUTE_REPARSE_POINT):
        raise ValueError(f'An archive bundle is not a regular file: {path}')
    return info


def check_snapshot_file(path, identity):
    info = regular_bundle(path)
    current = info.st_dev, info.st_ino, info.st_size, info.st_mtime_ns
    if current != identity:
        raise ValueError(f'The retired snapshot changed during the archive operation: {path}')
    return info


def offload_retired_catalogs(report, selected, preserve, evidence, apply, *, copy_only=False):
    snapshots = retired_catalog_snapshots(report, selected, preserve)
    store = external_store()
    if store is None or not store.is_dir():
        raise ValueError('Configure the external catalog store before offloading snapshots.')
    objects = {}
    identities = {}
    links = Counter()
    for _, _, _, files in snapshots.values():
        for path, digest, identity in files.values():
            info = check_snapshot_file(path, identity)
            if digest in objects and objects[digest][1][2] != identity[2]:
                raise ValueError(f'A declared bundle identity has different file lengths: {path}')
            objects.setdefault(digest, (path, identity))
            links[identity[:2]] += 1
            identities[identity[:2]] = path, info
    result = dict(
        status='PASS_RETIRED_CATALOG_OFFLOAD_PREVIEW', directory=str(store), owners=list(map(str, snapshots)),
        paths=sum(links.values()), logicalBytes=sum(identities[key][1].st_size * count for key, count in links.items()),
        potentialStoredBytes=sum(_stored_size(path, info) for key, (path, info) in identities.items()
                                 if info.st_nlink == links[key]),
        distinctObjects=len(objects), removed=0, availableBytesBefore=shutil.disk_usage(ROOT).free)
    record_path = evidence / 'RetiredCatalogOffload.json'
    write(record_path, result)
    if not (apply or copy_only) or not objects:
        return result

    verified = set()
    for owner, (_, _, _, files) in snapshots.items():
        for source, digest, identity in files.values():
            key = identity, digest
            if key not in verified:
                check_snapshot_file(source, identity)
                verify_blob(source, digest, identity[2])
                verified.add(key)
        print(f'Verified {len(files)} local bundle paths in {owner.name}', flush=True)
    external_versions = {}
    for index, (digest, (source, identity)) in enumerate(objects.items(), 1):
        target = store / (digest + '.bundle')
        if target.exists() or target.is_symlink():
            regular_bundle(target)
        copy_blob(source, target, digest, identity[2])
        info = regular_bundle(target)
        external_versions[digest] = info.st_dev, info.st_ino, info.st_size, info.st_mtime_ns
        if index % 250 == 0:
            print(f'Verified external objects: {index}/{len(objects)}', flush=True)

    samples = {}
    with tempfile.TemporaryDirectory(dir=evidence) as scratch:
        for owner, (_, _, _, files) in snapshots.items():
            if not files:
                continue
            name, (_, digest, identity) = min(files.items(), key=lambda row: row[1][2][2])
            restored = Path(scratch) / (digest + '.bundle')
            copy_blob(store / restored.name, restored, digest, identity[2])
            samples[str(owner)] = dict(file=name, sha256=digest, bytes=identity[2])
    result['restoredSamples'] = samples
    result['status'] = 'PASS_RETIRED_CATALOG_EXTERNAL_COPIES'
    write(record_path, result)
    if copy_only:
        return result

    references = registered_archive_paths()
    adopted, _ = distribution_catalog()
    if any(manifest == adopted or path.parent in references or path in references
           for manifest, _, _, files in snapshots.values() for path, _, _ in files.values()):
        raise ValueError('The selected archive bundles gained a distribution or replay reference.')
    # A durable index is installed before the corresponding local filenames are removed.
    for owner, (manifest, receipt_path, receipt, files) in snapshots.items():
        if not files:
            continue
        if owner_catalog(owner)[0] != manifest:
            raise ValueError(f'The model catalog changed before offloading: {owner}')
        checked_catalog_path(owner / 'Catalog')
        receipt['entries'].update({name: dict(sha256=digest, bytes=identity[2])
                                   for name, (_, digest, identity) in files.items()})
        write(receipt_path, receipt)
        for source, digest, identity in files.values():
            check_snapshot_file(source, identity)
            check_snapshot_file(store / (digest + '.bundle'), external_versions[digest])
            source.unlink()
            result['removed'] += 1
        write(record_path, result)
        print(f'Offloaded {len(files)} bundle paths in {owner.name}', flush=True)
    result.update(status='PASS_RETIRED_CATALOGS_OFFLOADED', availableBytesAfter=shutil.disk_usage(ROOT).free)
    write(record_path, result)
    return result


def archive_catalogs(groups, owners):
    checked_catalog_path(BLOBS).mkdir(parents=True, exist_ok=True)
    records = defaultdict(dict)
    for digest, copies in groups.items():
        for owner, name, count in copies:
            records[owner][name] = dict(sha256=digest, bytes=count)
    verified = {}
    removed = 0
    for owner, files in records.items():
        manifest, _ = owner_catalog(owner)
        if manifest != owners[owner]:
            raise ValueError(f'The model catalog changed before archiving: {owner}')
        path, previous = archive_receipt(owner, manifest)
        sources = {}
        for name, record in files.items():
            digest, count = record['sha256'], record['bytes']
            blob = blob_path(digest)
            source = checked_source_path(bundle_path(owner / 'Catalog', name))
            stamp = file_stamp(source)
            if stamp[2] != count:
                raise ValueError(f'A retired bundle changed size: {source}')
            if not blob.is_file():
                if sha(source, payload=True) != digest:
                    raise ValueError(f'A retired bundle differs from its catalog: {source}')
                blob.hardlink_to(source)
                verified[digest] = file_stamp(blob)
            if verified.get(digest) != file_stamp(blob):
                if sha(checked_source_path(blob), payload=True) != digest:
                    raise ValueError(f'The archive blob differs from its identity: {blob}')
                verified[digest] = file_stamp(blob)
            if stamp[:2] != verified[digest][:2] and sha(source, payload=True) != digest:
                raise ValueError(f'A retired bundle differs from its catalog: {source}')
            sources[name] = source, stamp
        previous['entries'].update(files)
        write(path, previous)
        for name, (leaf, stamp) in sources.items():
            digest = files[name]['sha256']
            if file_stamp(leaf) != stamp or file_stamp(blob_path(digest)) != verified[digest]:
                raise ValueError(f'A retired payload changed before release: {leaf}')
            leaf.unlink()
            removed += 1
        print(f'Archived {len(files)} bundle paths in {owner.name}', flush=True)
    return removed


def restore_catalog(owner, names=()):
    owner = owner_path(owner)
    manifest, catalog = owner_catalog(owner)
    path, receipt = archive_receipt(owner, manifest)
    if not path.is_file():
        raise FileNotFoundError(f'The catalog has no archive receipt: {path}')
    entries = receipt['entries']
    if unknown := set(names) - entries.keys():
        raise ValueError(f'The receipt does not own these bundles: {sorted(unknown)}')
    missing = []
    store = None
    for name in sorted(names or entries):
        record = entries[name]
        digest = record['sha256']
        source = blob_path(digest)
        local = source.is_file()
        if local:
            source = checked_source_path(source)
        else:
            store = store or external_store()
            if store is None:
                raise FileNotFoundError(f'The archived object is missing: {digest}')
            source = external_path(store / source.name)
        leaf = bundle_path(owner / 'Catalog', name)
        if catalog.get(name) != digest:
            raise ValueError(f'The stored payload differs from its verified catalog: {leaf}')
        verify_blob(source, digest, record['bytes'])
        if not leaf.exists():
            missing.append((leaf, source, local, digest, record['bytes']))
        elif not leaf.samefile(source) and sha(checked_source_path(leaf), payload=True) != digest:
            raise ValueError(f'A different bundle already exists in the restored catalog: {leaf}')
    for leaf, source, local, digest, size in missing:
        if local:
            leaf.hardlink_to(source)
        else:
            copy_blob(source, leaf, digest, size)
    return len(missing)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    task = parser.add_mutually_exclusive_group(required=True)
    task.add_argument('--report', type=Path)
    task.add_argument('--restore', type=Path)
    task.add_argument('--offload', type=Path, metavar='PROJECT_STORAGE',
                      help='Move archived objects under the project storage directory and verify a restore.')
    parser.add_argument('--preserve', action='append', type=Path, default=[])
    parser.add_argument('--offload-retired', action='append', type=Path, default=[], metavar='CATALOG_OWNER',
                        help='Archive the explicitly selected retired catalog snapshot to the configured external store.')
    parser.add_argument('--output', type=Path)
    parser.add_argument('--max-catalogs', type=int, default=0)
    parser.add_argument('--apply', action='store_true')
    parser.add_argument('--copy-only', action='store_true', help='Verify external copies without retiring the local archive.')
    parser.add_argument('--file', action='append', default=[], help='Restore only this receipt-owned bundle.')
    args = parser.parse_args()
    if args.report and args.output is None:
        parser.error('--report requires --output.')
    if args.offload and (args.output is None or args.preserve or args.max_catalogs or args.file):
        parser.error('--offload requires --output and accepts --apply or --copy-only.')
    if args.offload_retired and (not args.report or args.max_catalogs):
        parser.error('--offload-retired requires --report and selects exact catalog owners.')
    if args.copy_only and (not (args.offload or args.offload_retired) or args.apply):
        parser.error('--copy-only is available with external archiving in place of --apply.')
    if args.max_catalogs < 0 or (args.report and args.file):
        parser.error('Use a nonnegative catalog limit and --file only with --restore.')
    if args.restore and (args.apply or args.preserve or args.output or args.max_catalogs):
        parser.error('Use --restore with optional --file arguments.')
    with contextlib.ExitStack() as stack:
        stack.enter_context(payload_hash_operation())
        if args.restore or args.apply or args.offload_retired and args.copy_only:
            stack.enter_context(resource_operation())
            if producer_processes():
                raise RuntimeError('Stop active model producers before changing archived catalogs.')
        if args.restore:
            print(f'Restored {restore_catalog(args.restore, args.file)} exact bundle paths.', flush=True)
            return
        output = owner_path(args.output)
        if not output.is_relative_to(ARTIFACTS):
            raise ValueError('The catalog archive report belongs in game-test/artifacts.')
        if args.offload:
            directory = external_path(args.offload) / 'ColdStorage' / 'CatalogObjects'
            print(json.dumps(offload_blobs(directory, output, args.apply, copy_only=args.copy_only),
                             ensure_ascii=False), flush=True)
            return
        if args.offload_retired:
            print(json.dumps(offload_retired_catalogs(args.report, args.offload_retired, args.preserve,
                                                       output, args.apply, copy_only=args.copy_only),
                             ensure_ascii=False), flush=True)
            return
        groups, owners = archive_plan(args.report, args.preserve, args.max_catalogs, verify_inputs=args.apply)
        stored = sum(rows[0][2] for rows in groups.values())
        expanded = sum(row[2] for rows in groups.values() for row in rows)
        result = dict(status='PASS_CATALOG_ARCHIVE_PREVIEW', owners=[str(owner) for owner in sorted(owners)],
                      paths=sum(map(len, groups.values())), blobs=len(groups), apparentBytesBefore=expanded,
                      apparentBytesAfter=stored, availableBytesBefore=shutil.disk_usage(ROOT).free)
        write(output / 'CatalogArchivePlan.json', result)
        print(f'{result["paths"]} catalog paths use {result["blobs"]} distinct payloads. '
              f'Potential visible size reduction: {expanded - stored} bytes.', flush=True)
        if args.apply:
            result.update(status='PASS_ARCHIVED_RETIRING_CATALOGS', archived=archive_catalogs(groups, owners),
                          availableBytesAfter=shutil.disk_usage(ROOT).free)
            write(output / 'CatalogArchiveResult.json', result)


if __name__ == '__main__':
    main()
