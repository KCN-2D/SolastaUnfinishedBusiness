"""Publish game-verified model packs and verify the resulting distribution."""

import argparse
from concurrent.futures import ThreadPoolExecutor
import hashlib
import http.client
import json
from pathlib import Path
import re
import subprocess
import time
from urllib.parse import quote, urlsplit
from urllib.request import Request, urlopen

from build_models import (
    PUBLIC, RESOURCE_FILES, RESOURCE_PACKAGE, ROOT, checked_catalog_path, checked_source_path,
    create_resource_packs, read, resource_release_metadata, sha, validate_resource_release, write,
)
from resource_storage import ARTIFACTS, WORKSPACE, resource_operation


class GitHubReleases:
    def __init__(self, repository):
        self.repository = repository
        credential = subprocess.run(
            ['git', '-c', 'credential.interactive=false', 'credential', 'fill'], cwd=ROOT,
            input=f'protocol=https\nhost=github.com\npath={repository}.git\n\n', text=True,
            capture_output=True, check=True, timeout=30,
        )
        fields = dict(line.split('=', 1) for line in credential.stdout.splitlines() if '=' in line)
        if not fields.get('password'):
            raise ValueError('The Git credential helper did not return a GitHub credential.')
        self.headers = {
            'Authorization': 'Bearer ' + fields['password'],
            'Accept': 'application/vnd.github+json',
            'X-GitHub-Api-Version': '2026-03-10',
            'User-Agent': 'SolastaUB-CharacterModels',
        }

    def request(self, path, method='GET', data=None):
        request = Request(f'https://api.github.com/repos/{self.repository}{path}', method=method,
                          headers=self.headers, data=None if data is None else json.dumps(data).encode())
        if data is not None:
            request.add_header('Content-Type', 'application/json')
        with urlopen(request, timeout=90) as response:
            return None if response.status == 204 else json.load(response)

    def assets(self, release_id):
        assets = {}
        for page in (1, 2):
            rows = self.request(f'/releases/{release_id}/assets?per_page=100&page={page}')
            for row in rows:
                if row['name'] in assets:
                    raise ValueError('The release contains duplicate asset names.')
                assets[row['name']] = row
            if len(rows) < 100:
                return assets
        raise ValueError('The release exceeds the model pack asset limit.')

    def upload(self, release_id, row, local_file):
        route = f'/repos/{self.repository}/releases/{release_id}/assets?name={quote(row["name"], safe="")}'
        headers = dict(self.headers, **{'Content-Type': 'application/octet-stream', 'Content-Length': str(row['bytes'])})
        connection = http.client.HTTPSConnection('uploads.github.com', timeout=300)
        try:
            with local_file.open('rb') as stream:
                connection.request('POST', route, body=stream, headers=headers)
                response = connection.getresponse()
                value = response.read()
            if response.status != 201:
                raise OSError(f'GitHub returned HTTP {response.status} for {row["name"]}.')
            return json.loads(value)
        finally:
            connection.close()


def asset_record(path):
    return dict(name=path.name, bytes=path.stat().st_size, sha256=sha(path))


def verify_asset(asset, row):
    if (asset['name'] != row['name'] or asset['state'] != 'uploaded' or asset['size'] != row['bytes']
            or asset.get('digest') != 'sha256:' + row['sha256']):
        raise ValueError('GitHub asset differs from the verified release file: ' + row['name'])


def upload_asset(api, release_id, row, local_file, assets):
    for attempt in range(4):
        saved = assets.get(row['name'])
        if saved is not None and saved['state'] == 'uploaded':
            verify_asset(saved, row)
            return
        if local_file is None:
            raise ValueError('The consumed pack is missing from GitHub: ' + row['name'])
        if saved is not None:
            if saved['state'] != 'starter' or saved['size'] != 0:
                raise ValueError('The draft contains an unverified asset: ' + row['name'])
            api.request(f'/releases/assets/{saved["id"]}', method='DELETE')
            del assets[row['name']]
        try:
            uploaded = api.upload(release_id, row, local_file)
        except (OSError, http.client.HTTPException):
            current = api.assets(release_id)
            assets.clear()
            assets.update(current)
            saved = assets.get(row['name'])
            if saved is not None and saved['state'] == 'uploaded':
                verify_asset(saved, row)
                return
            if attempt == 3:
                raise
            time.sleep(attempt + 1)
            continue
        verify_asset(uploaded, row)
        assets[row['name']] = uploaded
        return


def verify_release(api, release, expected, files):
    if any(release[key] != expected[key] for key in ('tag_name', 'name', 'body', 'prerelease')):
        raise ValueError('GitHub release metadata differs from the package configuration.')
    saved = api.assets(release['id'])
    if saved.keys() != {row['name'] for row in files}:
        raise ValueError('The release asset set differs from the verified package list.')
    for row in files:
        verify_asset(saved[row['name']], row)


def reused_pack_assets(packs):
    return [dict(name=pack['file'], bytes=pack['bytes'], sha256=pack['sha256'], sourceVersion=pack['sourceVersion'])
            for pack in packs['packs'] if 'sourceVersion' in pack]


def verify_reused_pack_assets(api, rows):
    sources = {}
    for row in rows:
        version = row['sourceVersion']
        if version not in sources:
            tag = resource_release_metadata(version)['tag_name']
            release = api.request('/releases/tags/' + quote(tag, safe=''))
            if release['draft'] or release['tag_name'] != tag:
                raise ValueError('A reused model archive is not published: ' + tag)
            sources[version] = api.assets(release['id'])
        asset = sources[version].get(row['name'])
        if asset is None:
            raise ValueError('A reused model archive is missing: ' + row['name'])
        verify_asset(asset, row)


def verify_native_catalog(result_path, manifest):
    result = read(checked_source_path(result_path))
    selected = read(checked_source_path(Path(result['runtimeResult']).with_name('SelectedResourceCatalog.json')))
    catalog = selected['catalog']
    if (result['status'] != 'PASS' or result.get('nativeVerificationStatus') != 'PASS' or result['restored'] is not True
            or selected['completed'] is not True or selected['optionRestored'] is not True
            or Path(catalog['path']).resolve() != manifest or catalog['sha256'].lower() != sha(manifest)):
        raise ValueError('A restored native game run must have verified this exact model catalog.')


def stage(api, workspace, args, expected, receipt_path):
    manifest = checked_source_path(args.manifest)
    if manifest.name != RESOURCE_FILES['manifest']:
        raise ValueError('The tested model catalog must use the configured manifest filename.')
    verify_native_catalog(args.native_result, manifest)
    inputs = dict(repository=api.repository, version=args.release_version, tag=expected['tag_name'],
                  manifest=str(manifest), catalogSha256=sha(manifest))
    receipt = read(receipt_path) if receipt_path.exists() else None
    if receipt is not None and any(receipt[key] != value for key, value in inputs.items()):
        raise ValueError('This release workspace belongs to a different catalog or tag.')
    checkout = subprocess.check_output(['git', 'branch', '--show-current'], cwd=ROOT, text=True).strip()
    archive_folder = checked_catalog_path(workspace / 'Packs')
    live_assets = {}
    release = None

    def ensure_draft():
        nonlocal release, receipt, live_assets
        if release is None:
            if receipt is None:
                if any(row['tag_name'] == inputs['tag'] for row in api.request('/releases?per_page=100')):
                    raise ValueError('This tag already has a release outside the local workspace.')
                release = api.request('/releases', method='POST', data=dict(expected, target_commitish=checkout))
                receipt = dict(inputs, status='DRAFT_CREATED', releaseId=release['id'], url=release['html_url'])
                write(receipt_path, receipt)
            else:
                release = api.request(f'/releases/{receipt["releaseId"]}')
            if not release['draft'] or any(release[key] != expected[key] for key in ('tag_name', 'name', 'body', 'prerelease')):
                raise ValueError('The selected draft no longer matches the package configuration.')
            live_assets = api.assets(release['id'])

    def consume(pack, path):
        ensure_draft()
        row = dict(name=pack['file'], bytes=pack['bytes'], sha256=pack['sha256'])
        upload_asset(api, release['id'], row, path, live_assets)
        print('Verified draft pack', row['name'], flush=True)

    packs = create_resource_packs(manifest, archive_folder, args.release_version,
                                  previous_manifest=args.previous_manifest, previous_packs=args.previous_packs,
                                  mesh_authoring=args.mesh_authoring,
                                  consume_pack=consume)
    packs_path = checked_source_path(archive_folder / RESOURCE_FILES['packs'])
    if packs['catalogSha256'] != inputs['catalogSha256'] or sha(manifest) != inputs['catalogSha256']:
        raise ValueError('The tested catalog changed during model pack staging.')
    reused = reused_pack_assets(packs)
    verify_reused_pack_assets(api, reused)
    ensure_draft()
    metadata = [asset_record(manifest), asset_record(packs_path)]
    for row, path in zip(metadata, (manifest, packs_path), strict=True):
        upload_asset(api, release['id'], row, path, live_assets)
    rows = [dict(name=pack['file'], bytes=pack['bytes'], sha256=pack['sha256'])
            for pack in packs['packs'] if 'sourceVersion' not in pack]
    rows.extend(metadata)
    verify_release(api, release, expected, rows)
    write(receipt_path, dict(receipt, status='DRAFT_ASSETS_VERIFIED', assets=rows, inheritedPacks=reused))
    print('DRAFT_ASSETS_VERIFIED', release['id'], len(packs['packs']) - len(reused), 'uploaded packs,', len(reused), 'reused packs', flush=True)


def verify_public_asset(url, row):
    headers = {'User-Agent': 'SolastaUB-CharacterModels'}
    if row['name'].endswith('.zip'):
        headers['Range'] = 'bytes=0-3'
    with urlopen(Request(url + quote(row['name'], safe=''), headers=headers), timeout=90) as response:
        if 'Range' in headers:
            if response.status != 206 or response.headers.get('Content-Range') != f'bytes 0-3/{row["bytes"]}' \
                    or response.read(4) != b'PK\x03\x04':
                raise ValueError('The public pack cannot be read: ' + row['name'])
        else:
            contents = response.read(row['bytes'] + 1)
            if len(contents) != row['bytes'] or hashlib.sha256(contents).hexdigest() != row['sha256']:
                raise ValueError('The public distribution metadata changed: ' + row['name'])
    return row['name']


def publish(api, receipt, expected, receipt_path, commit):
    if receipt['status'] not in ('DRAFT_ASSETS_VERIFIED', 'PASS_PUBLISHED_MODEL_RESOURCES'):
        raise ValueError('The draft assets must be verified before publication.')
    if re.fullmatch('[a-f0-9]{40}', commit) is None:
        raise ValueError('Specify the full Git commit SHA for this model release.')
    if api.request('/git/ref/tags/' + quote(receipt['tag'], safe=''))['object']['sha'] != commit:
        raise ValueError('The remote model tag must point to the distribution commit.')
    files = receipt['assets']
    blobs = {}
    reused = []
    for name in (RESOURCE_FILES['manifest'], RESOURCE_FILES['packs']):
        path = (PUBLIC / name).relative_to(ROOT).as_posix()
        revision = f'{commit}:{path}'
        stored = subprocess.check_output(['git', 'show', revision], cwd=ROOT)
        if hashlib.sha256(stored).hexdigest() != next(row['sha256'] for row in files if row['name'] == name):
            raise ValueError('The tagged source differs from the verified release metadata: ' + name)
        if name == RESOURCE_FILES['packs']:
            reused = reused_pack_assets(json.loads(stored))
        blobs[name] = subprocess.check_output(['git', 'rev-parse', revision], cwd=ROOT, text=True).strip()
    if reused != receipt.get('inheritedPacks', []):
        raise ValueError('The tagged catalog does not use the verified earlier archives.')
    verify_reused_pack_assets(api, reused)
    release = api.request(f'/releases/{receipt["releaseId"]}')
    verify_release(api, release, expected, files)
    if release['draft']:
        release = api.request(f'/releases/{receipt["releaseId"]}', method='PATCH',
                              data=dict(draft=False, make_latest='false', target_commitish=commit))
    if release['draft']:
        raise ValueError('GitHub did not publish the model release.')
    verify_release(api, release, expected, files)
    base_url = RESOURCE_PACKAGE['downloadBaseUrl'].format(tag=receipt['tag'])
    public_files = [(base_url, row) for row in files]
    public_files.extend((RESOURCE_PACKAGE['downloadBaseUrl'].format(tag=resource_release_metadata(row['sourceVersion'])['tag_name']), row)
                        for row in reused)
    for attempt in range(4):
        try:
            with ThreadPoolExecutor(max_workers=4) as pool:
                for name in pool.map(lambda entry: verify_public_asset(*entry), public_files):
                    print('Verified public asset', name, flush=True)
            break
        except (OSError, ValueError):
            if attempt == 3:
                raise
            time.sleep(3 * (attempt + 1))
    verified = dict(receipt, status='PASS_PUBLISHED_MODEL_RESOURCES', sourceCommit=commit,
                    url=release['html_url'], metadataBlobs=blobs)
    write(receipt_path, verified)
    storage = checked_catalog_path(WORKSPACE / 'ModelReleaseReceipts', receipt['tag'] + '.json')
    write(storage, verified)
    print('PASS_PUBLISHED_MODEL_RESOURCES', release['html_url'], flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--workspace', type=Path, required=True)
    commands = parser.add_subparsers(dest='command', required=True)
    draft = commands.add_parser('stage')
    draft.add_argument('--manifest', type=Path, required=True)
    draft.add_argument('--native-result', type=Path, required=True)
    draft.add_argument('--release-version', required=True)
    draft.add_argument('--previous-manifest', type=Path)
    draft.add_argument('--previous-packs', type=Path)
    draft.add_argument('--mesh-authoring', type=Path)
    final = commands.add_parser('publish')
    final.add_argument('--commit', required=True)
    args = parser.parse_args()
    workspace = checked_catalog_path(args.workspace)
    if not any(workspace.is_relative_to(root) and workspace != root for root in (ARTIFACTS, WORKSPACE)):
        raise ValueError('Use a model release workspace under the owned tool directories.')
    receipt_path = checked_catalog_path(workspace, 'PublicationReceipt.json')
    with resource_operation():
        receipt = read(receipt_path) if args.command == 'publish' else None
        version = receipt['version'] if receipt is not None else args.release_version
        expected = resource_release_metadata(version)
        validate_resource_release(expected, version)
        url = urlsplit(RESOURCE_PACKAGE['downloadBaseUrl'].format(tag=expected['tag_name']))
        match = re.fullmatch(r'/([\w.-]+/[\w.-]+)/releases/download/[^/]+/', url.path)
        if url.scheme != 'https' or url.netloc != 'github.com' or not match:
            raise ValueError('The model distribution requires an HTTPS GitHub repository.')
        api = GitHubReleases(match[1])
        if receipt is None:
            stage(api, workspace, args, expected, receipt_path)
        else:
            if receipt['repository'] != api.repository or receipt['tag'] != expected['tag_name']:
                raise ValueError('The release receipt targets a different distribution.')
            publish(api, receipt, expected, receipt_path, args.commit)


if __name__ == '__main__':
    main()
