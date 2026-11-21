"""Coalesce duplicated bundles in retired catalogs with an exact restore receipt."""
import argparse
from collections import Counter, defaultdict
import contextlib
from pathlib import Path
import re
import shutil
import stat


from resource_storage import (
    ARTIFACTS, ROOT, WORKSPACE, RESOURCE_FILES, catalog_payloads, checked_catalog_path,
    checked_source_path, distribution_catalog, payload_hash_operation, producer_processes,
    read, resource_operation, sha, write,
)


ARCHIVE_NAME = 'CatalogArchive.json'
ARCHIVE_KIND = 'SolastaUBRetiredCharacterCatalog'
BLOBS = WORKSPACE / 'CatalogArchive'
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


def archive_plan(report, preserve, limit, *, verify_inputs=False):
    proof = read(checked_source_path(report))
    adopted, _ = distribution_catalog()
    if proof.get('status') != 'references-closed-payloads-planned' or proof.get('adoptedManifestSha256') != adopted:
        raise ValueError('Archive only catalogs retired against the currently distributed models.')
    protected = {owner_path(path) for path in (*proof['explicitRetainedDirectories'], *preserve)}
    references_file = ARTIFACTS / 'StorageReferences.json'
    references = set()
    if references_file.is_file():
        references = {Path(path).resolve() if Path(path).is_absolute() else (ARTIFACTS / path).resolve()
                      for path in read(checked_source_path(references_file))['paths']}
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


def archive_catalogs(groups, owners):
    checked_catalog_path(BLOBS).mkdir(parents=True, exist_ok=True)
    records = defaultdict(dict)
    for digest, copies in groups.items():
        for owner, name, count in copies:
            records[owner][name] = dict(sha256=digest, bytes=count)
    verified = {}
    removed = 0
    for owner, files in records.items():
        path = checked_catalog_path(owner) / ARCHIVE_NAME
        manifest, _ = owner_catalog(owner)
        if manifest != owners[owner]:
            raise ValueError(f'The model catalog changed before archiving: {owner}')
        previous = read(checked_source_path(path)) if path.is_file() else dict(
            kind=ARCHIVE_KIND, schemaVersion=1, catalogSha256=manifest, entries={})
        if previous['kind'] != ARCHIVE_KIND or previous['schemaVersion'] != 1 or previous['catalogSha256'] != manifest:
            raise ValueError(f'An existing archive receipt belongs to another catalog: {path}')
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
    receipt = read(checked_source_path(owner / ARCHIVE_NAME))
    if receipt.get('kind') != ARCHIVE_KIND or receipt.get('schemaVersion') != 1 or receipt.get('catalogSha256') != manifest:
        raise ValueError(f'The archive receipt does not match the catalog: {owner}')
    entries = receipt['entries']
    if unknown := set(names) - entries.keys():
        raise ValueError(f'The receipt does not own these bundles: {sorted(unknown)}')
    missing = []
    for name in sorted(names or entries):
        record = entries[name]
        digest = record['sha256']
        source = checked_source_path(blob_path(digest))
        leaf = bundle_path(owner / 'Catalog', name)
        if catalog.get(name) != digest or source.stat().st_size != record['bytes'] or sha(source, payload=True) != digest:
            raise ValueError(f'The stored payload differs from its verified catalog: {leaf}')
        if not leaf.exists():
            missing.append((leaf, source))
        elif not leaf.samefile(source) and sha(checked_source_path(leaf), payload=True) != digest:
            raise ValueError(f'A different bundle already exists in the restored catalog: {leaf}')
    for leaf, source in missing:
        leaf.hardlink_to(source)
    return len(missing)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    task = parser.add_mutually_exclusive_group(required=True)
    task.add_argument('--report', type=Path)
    task.add_argument('--restore', type=Path)
    parser.add_argument('--preserve', action='append', type=Path, default=[])
    parser.add_argument('--output', type=Path)
    parser.add_argument('--max-catalogs', type=int, default=0)
    parser.add_argument('--apply', action='store_true')
    parser.add_argument('--file', action='append', default=[], help='Restore only this receipt-owned bundle.')
    args = parser.parse_args()
    if args.report and args.output is None:
        parser.error('--report requires --output.')
    if args.max_catalogs < 0 or (args.report and args.file):
        parser.error('Use a nonnegative catalog limit and --file only with --restore.')
    if args.restore and (args.apply or args.preserve or args.output or args.max_catalogs):
        parser.error('Use --restore with optional --file arguments.')
    with contextlib.ExitStack() as stack:
        stack.enter_context(payload_hash_operation())
        if args.restore or args.apply:
            stack.enter_context(resource_operation())
            if producer_processes():
                raise RuntimeError('Stop active model producers before changing archived catalogs.')
        if args.restore:
            print(f'Restored {restore_catalog(args.restore, args.file)} exact bundle paths.', flush=True)
            return
        output = owner_path(args.output)
        if not output.is_relative_to(ARTIFACTS):
            raise ValueError('The catalog archive report belongs in game-test/artifacts.')
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
