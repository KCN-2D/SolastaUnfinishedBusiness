"""Publish the reviewed material bundle without modifying linked baselines."""
import argparse
import copy
import hashlib
import json
import os
from pathlib import Path
import shutil
import uuid

ROOT = next(p for p in Path(__file__).resolve().parents if (p / 'SolastaUnfinishedBusiness.sln').is_file())


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def atomic(path, content):
    temporary = path.with_name(path.name + '.' + uuid.uuid4().hex + '.tmp')
    try:
        temporary.write_bytes(content)
        os.replace(temporary, path)
    finally:
        if temporary.exists():
            temporary.unlink()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--candidate', required=True, type=Path)
    parser.add_argument('--evidence', required=True, type=Path)
    parser.add_argument('--catalog-directory', type=Path)
    parser.add_argument('--apply', action='store_true')
    args = parser.parse_args()
    candidate = json.loads(args.candidate.read_text(encoding='utf-8-sig'))
    assert candidate['status'] == 'PASS_SHADER_MATERIAL_AND_BRDF_CONTRACTS'
    source = Path(candidate['bundle']['path'])
    assert sha(source) == candidate['bundle']['sha256']
    directory = ROOT / 'SolastaUnfinishedBusiness/BlenderModels/CharacterModels'
    if args.catalog_directory is not None:
        directory = args.catalog_directory.resolve()
        assert directory.is_relative_to(ROOT / 'tools/game-test/artifacts'), 'Private catalogs belong to verification artifacts.'
    manifest_path = directory / 'CharacterModels.manifest.json'
    bundle_name = Path(candidate['baselineBundle']['path']).name
    destination = directory / bundle_name
    before_manifest_bytes = manifest_path.read_bytes()
    before_bundle_bytes = destination.read_bytes()
    assert sha(destination) == candidate['baselineBundle']['sha256']
    text = before_manifest_bytes.decode('utf-8-sig')
    before = json.loads(text)
    after = copy.deepcopy(before)
    bundles = [item for item in after['bundles'] if item['file'] == bundle_name]
    profiles = [item['authoredProfile'] for item in after['materials'] if item.get('authoredProfile', {}).get('bundleFile') == bundle_name]
    assert len(bundles) == 1 and profiles, "The selected material bundle must have one declaration and nonempty profile owners."
    assert {item['shader'] for item in profiles} == {'KCN/SourceGroom/NativeHair'}
    old_asset = profiles[0]['materialAsset']
    assert {item['materialAsset'] for item in profiles} == {old_asset}
    old_hash = bundles[0]['sha256']
    assert old_hash == candidate['baselineBundle']['sha256']
    bundles[0]['sha256'] = candidate['bundle']['sha256']
    for profile in profiles:
        profile['materialAsset'] = candidate['materialAsset']
    old_asset_literal, new_asset_literal = json.dumps(old_asset), json.dumps(candidate['materialAsset'])
    old_hash_literal, new_hash_literal = json.dumps(old_hash), json.dumps(candidate['bundle']['sha256'])
    assert text.count(old_asset_literal) == len(profiles) and text.count(old_hash_literal) == 1
    changed = text.replace(old_asset_literal, new_asset_literal).replace(old_hash_literal, new_hash_literal)
    assert json.loads(changed) == after
    changed_bytes = changed.encode('utf-8')
    if before_manifest_bytes.startswith(b'\xef\xbb\xbf'):
        changed_bytes = b'\xef\xbb\xbf' + changed_bytes
    receipt = {'status': 'READY_FOR_REVIEW_PUBLICATION', 'applied': False,
               'catalogDirectory': str(directory),
               'profilesUpdated': len(profiles), 'beforeManifestSha256': sha(manifest_path),
               'beforeBundleSha256': sha(destination), 'afterBundleSha256': candidate['bundle']['sha256'],
               'afterManifestSha256': hashlib.sha256(changed_bytes).hexdigest(),
               'materialAsset': candidate['materialAsset'], 'sourceBundle': str(source),
               'scope': 'Hair material bundle identity and its profile asset paths only. All mesh entries, UV layout, texture remaps and other material fields remain exact.'}
    if args.apply:
        args.evidence.mkdir(parents=True)
        shutil.copy2(destination, args.evidence / bundle_name)
        (args.evidence / manifest_path.name).write_bytes(before_manifest_bytes)
        try:
            atomic(destination, source.read_bytes())
            atomic(manifest_path, changed_bytes)
            assert sha(destination) == candidate['bundle']['sha256']
            assert manifest_path.read_bytes() == changed_bytes
        except BaseException:
            atomic(destination, before_bundle_bytes)
            atomic(manifest_path, before_manifest_bytes)
            raise
        receipt['status'] = 'PASS_REVIEW_PUBLICATION'
        receipt['applied'] = True
        (args.evidence / 'Publication.json').write_text(json.dumps(receipt, indent=2)+'\n', encoding='utf-8')
    print(json.dumps(receipt, indent=2))


if __name__ == '__main__':
    main()
