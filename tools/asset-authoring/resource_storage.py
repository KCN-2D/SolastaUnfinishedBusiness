"""Retire declared resource outputs after adoption or verified publication."""
from __future__ import annotations

import argparse
import contextlib
import collections
import ctypes
import datetime
import hashlib
import json
import os
import re
import subprocess
import sys
import uuid
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools/blender-models/CharacterFaces"))
from build_models import PUBLIC_RELATIVE_PATH, RESOURCE_FILES, resource_release_metadata, catalog_payloads, checked_catalog_path, checked_source_path, completed_resource_payloads, create_declared_payload_plan, resource_art_acceptance

ARTIFACTS = ROOT / "tools/game-test/artifacts"
WORKSPACE = ROOT / "tools/asset-authoring/workspace"
PUBLIC = ROOT / PUBLIC_RELATIVE_PATH / RESOURCE_FILES['manifest']


def read(path):
    return json.loads(Path(path).read_text(encoding="utf-8-sig"))


def sha(path):
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def write(path, data):
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary=path.with_name(path.name+'.writing-'+uuid.uuid4().hex)
    try:
        temporary.write_text(json.dumps(data, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        temporary.replace(path)
    finally:
        if temporary.exists():temporary.unlink()


def inventory_tools(root):
    """Count the entire tree, distinguishing directory size from hard-link reuse."""
    root=Path(root).resolve();identities={};groups=collections.defaultdict(lambda:dict(files=0,logicalBytes=0,identities=set()))
    files=0;directories=0;logical=0;links=[];stack=[root]
    while stack:
        folder=stack.pop();directories+=1
        with os.scandir(folder)as entries:
            for entry in entries:
                path=Path(entry.path);info=entry.stat(follow_symlinks=False)
                if getattr(info,'st_file_attributes',0)&0x400 or path.is_symlink():
                    links.append(str(path.relative_to(root)));continue
                if entry.is_dir(follow_symlinks=False):stack.append(path);continue
                if not entry.is_file(follow_symlinks=False):continue
                # Windows DirEntry caches st_ino=0. Path.stat supplies the stable
                # file identity needed to count hard-linked data once.
                info=path.stat(follow_symlinks=False)
                if not info.st_ino:raise ValueError('Tool storage inventory requires stable file identities')
                identity=(info.st_dev,info.st_ino);identities[identity]=info.st_size
                files+=1;logical+=info.st_size;relative=path.relative_to(root)
                for depth in range(1,min(4,len(relative.parts))):
                    group=groups['/'.join(relative.parts[:depth])]
                    group['files']+=1;group['logicalBytes']+=info.st_size;group['identities'].add(identity)
    rows=[dict(path=path,files=row['files'],logicalBytes=row['logicalBytes'],uniqueFileBytes=sum(identities[key]for key in row['identities']))for path,row in groups.items()]
    return dict(status='PASS_TOOL_STORAGE_INVENTORY',root=str(root),files=files,directories=directories,logicalBytes=logical,
        uniqueFileBytes=sum(identities.values()),groups=sorted(rows,key=lambda row:row['logicalBytes'],reverse=True),skippedReparsePoints=links)


DATA_EXTENSIONS={'.bundle','.bin','.bytes','.gz','.rgba8','.cpu-rgba8','.bc3','.bc7','.mips','.u8','.encoded','.png','.zip'}


def data_payload(path):
    return path.suffix.lower()in DATA_EXTENSIONS or bool(re.fullmatch(r'[a-f0-9]{64}\.json',path.name))


def declared_local_payloads(directory, document):
    """Follow hash-pinned producer receipts, keeping their declared local data only."""
    directory=checked_catalog_path(directory);payloads={};guards={}
    # The owner may cite an earlier revision of itself in its production history.
    # Its current bytes are already guarded by discovery; do not reopen that revision.
    visited={directory/'ResourceAssembly.json'}
    def visit(value):
        if isinstance(value,list):
            for child in value:visit(child)
            return
        if not isinstance(value,dict):return
        path,digest=value.get('path'),value.get('sha256')
        if isinstance(path,str)and isinstance(digest,str)and re.fullmatch(r'[a-fA-F0-9]{64}',digest):
            path=Path(path)
            if path.is_absolute()and path.is_relative_to(directory)and path.is_file():
                path=checked_source_path(path);digest=digest.lower()
                previous=guards.get(path)or payloads.get(path)
                if previous is not None and previous!=digest:raise ValueError('A producer declares conflicting local payload hashes')
                if path not in visited:
                    visited.add(path)
                    if sha(path)!=digest:raise ValueError('A declared local producer input changed: '+str(path))
                    if data_payload(path):payloads[path]=digest
                    elif path.suffix.lower()=='.json':
                        guards[path]=digest;visit(read(path))
        for child in value.values():visit(child)
    visit(document)
    return payloads,[dict(path=str(path),sha256=digest)for path,digest in guards.items()]


def process_start(pid):
    if os.name!='nt':
        try:os.kill(pid,0)
        except ProcessLookupError:return None
        return 'running'
    from ctypes import wintypes
    kernel=ctypes.WinDLL('kernel32',use_last_error=True)
    kernel.OpenProcess.argtypes=[wintypes.DWORD,wintypes.BOOL,wintypes.DWORD];kernel.OpenProcess.restype=wintypes.HANDLE
    kernel.GetProcessTimes.argtypes=[wintypes.HANDLE,*([ctypes.POINTER(wintypes.FILETIME)]*4)]
    kernel.CloseHandle.argtypes=[wintypes.HANDLE]
    handle=kernel.OpenProcess(0x1000,False,pid)
    if not handle:
        if ctypes.get_last_error()==87:return None
        raise OSError(ctypes.get_last_error(),'Process identity could not be verified')
    try:
        times=[wintypes.FILETIME()for _ in range(4)]
        if not kernel.GetProcessTimes(handle,*[ctypes.byref(value)for value in times]):raise ctypes.WinError(ctypes.get_last_error())
        return (times[0].dwHighDateTime<<32)|times[0].dwLowDateTime
    finally:kernel.CloseHandle(handle)


def work_active(record):
    if record.get('state')!='running':return False
    creator=record.get('creator',{})
    if not isinstance(creator.get('pid'),int)or creator['pid']<=0:return True
    try:return process_start(creator['pid'])==creator.get('start')
    except OSError:return True


def work_marker(output):
    output=checked_catalog_path(output)
    if not any(output.is_relative_to(root)and output!=root for root in (ARTIFACTS,WORKSPACE)):
        raise ValueError('Resource work must belong to an owned tool workspace')
    return output.parent/'.resource-work'/(output.name+'.json')


def work_files(output):
    if not output.is_dir():return set()
    return {path for folder,names in walk(output)for name in names
            if data_payload(path:=folder/name)and path.is_file()}


@contextlib.contextmanager
def resource_operation():
    """Share the existing tool-storage/deployment mutex with the cleaner."""
    if os.name!='nt':yield;return
    from ctypes import wintypes
    name='Local\\SolastaUBGameTest'
    kernel=ctypes.WinDLL('kernel32',use_last_error=True)
    kernel.CreateMutexW.argtypes=[ctypes.c_void_p,wintypes.BOOL,wintypes.LPCWSTR];kernel.CreateMutexW.restype=wintypes.HANDLE
    kernel.WaitForSingleObject.argtypes=[wintypes.HANDLE,wintypes.DWORD];kernel.WaitForSingleObject.restype=wintypes.DWORD
    kernel.ReleaseMutex.argtypes=[wintypes.HANDLE];kernel.CloseHandle.argtypes=[wintypes.HANDLE]
    handle=kernel.CreateMutexW(None,False,name)
    if not handle:raise ctypes.WinError(ctypes.get_last_error())
    acquired=False
    try:
        state=kernel.WaitForSingleObject(handle,0)
        if state not in (0,128):raise RuntimeError('Another tool operation owns resource storage')
        acquired=True;yield
    finally:
        if acquired:kernel.ReleaseMutex(handle)
        kernel.CloseHandle(handle)


def register_work_inputs(source,output):
    path=ARTIFACTS/'StorageReferences.json'
    record=read(path)if path.is_file()else dict(schemaVersion=1,paths=[])
    if set(record)!={'schemaVersion','paths'}or record['schemaVersion']!=1 or not isinstance(record['paths'],list):raise ValueError('Unsupported storage references')
    paths=set(record['paths']);paths.add(str(output))
    if any(source.is_relative_to(root)for root in (ARTIFACTS,WORKSPACE)):paths.add(str(source.parent))
    write(path,dict(schemaVersion=1,paths=sorted(paths,key=str.casefold)))


@contextlib.contextmanager
def resource_work(command,arguments):
    """Claim outputs before a producer writes; terminal records include failed work."""
    parser=argparse.ArgumentParser(add_help=False)
    parser.add_argument('--source',type=Path);parser.add_argument('--manifest',type=Path);parser.add_argument('--output',type=Path)
    options,_=parser.parse_known_args(arguments)
    source=options.manifest or (options.source/(Path('Catalog')/RESOURCE_FILES['manifest'])if options.source else None)
    output=options.output
    if source is None or output is None or not source.is_file()or output.suffix.lower()in ('.json','.zip','.png'):
        yield;return
    with resource_operation():
        with owned_resource_work(command,source,output):yield


@contextlib.contextmanager
def owned_resource_work(command,source,output):
    source=checked_source_path(source);output=checked_catalog_path(output)
    marker=work_marker(output);prior=read(marker)if marker.exists()else None
    if prior and (prior.get('kind')!='CharacterResourceWork'or Path(prior['output'])!=output or work_active(prior)):
        raise ValueError('Resource output is already owned by another active producer')
    initial=work_files(output)
    old_files={Path(row['path'])for row in prior.get('files',[])}if prior else set()
    record=dict(schemaVersion=1,kind='CharacterResourceWork',output=str(output),command=command,
        sourceManifest=dict(path=str(source),sha256=sha(source)),state='running',
        creator=dict(pid=os.getpid(),start=process_start(os.getpid())),startedUtc=datetime.datetime.now(datetime.timezone.utc).isoformat(),
        baselineFiles=[str(path)for path in sorted(initial-old_files)],files=[row for row in prior.get('files',[])if Path(row['path']).exists()]if prior else [])
    # Replacement is intentional even on resume. Cleanup holds a read lease on
    # this identity, so a producer cannot resume a selected output behind it.
    register_work_inputs(source,output)
    write(marker,record)
    state='completed';failure=None
    try:yield
    except BaseException as error:
        state='failed';failure=error;raise
    finally:
        try:
            current=work_files(output)
            owned=(current-initial)|(current&old_files)
            record.update(state=state,completedUtc=datetime.datetime.now(datetime.timezone.utc).isoformat(),files=[
                dict(path=str(checked_source_path(path)),sha256=sha(path))for path in sorted(owned)])
            write(marker,record)
        except BaseException as error:
            if failure is None:raise
            failure.add_note('Resource output recording also failed: '+type(error).__name__)


def source_definition(path,expected=None):
    path=checked_source_path(path)
    digest=sha(path)
    if expected is not None and digest!=expected:raise ValueError('A resource work source manifest changed')
    return path,digest,catalog_payloads(read(path))


def partial_catalog(directory,source_cache=None):
    """Recover only payload names declared by the original producer's inputs."""
    directory=checked_catalog_path(directory);catalog=directory/'Catalog'
    if not catalog.is_dir():return None
    declarations=[];guards=[];explicit_payloads={};source_cache={}if source_cache is None else source_cache
    def definition(path,expected=None):
        path=Path(path)
        if path not in source_cache:source_cache[path]=source_definition(path,expected)
        result=source_cache[path]
        if expected is not None and result[1]!=expected:raise ValueError('A resource work source manifest changed')
        return result
    marker=directory/'WorkingDirectory.json'
    if marker.is_file():
        document=read(checked_source_path(marker))
        if document.get('kind')=='OwnedFacePreparation':
            declarations.append(definition(Path(document['source'])/(Path('Catalog')/RESOURCE_FILES['manifest']),document['catalogSha256']))
            guards.append(marker)
    if not declarations:
        nested_owners=[]
        for folder,_ in walk(directory):
            if any(folder.is_relative_to(owner)for owner in nested_owners):continue
            if folder!=directory and(folder/'Catalog').is_dir():nested_owners.append(folder);continue
            if folder.name not in ('Inputs','Receipts'):continue
            checked_catalog_path(folder)
            for path in folder.glob('*.json'):
                document=read(checked_source_path(path))
                if not isinstance(document,dict):continue
                target=document.get('target');source=document.get('source')
                target_sha=target.get('sha256')if isinstance(target,dict)else None
                target=target.get('path')if isinstance(target,dict)else target
                expected=source.get('sha256')if isinstance(source,dict)else document.get('sha256')
                source=source.get('path')if isinstance(source,dict)else source
                if not isinstance(target,str)or not isinstance(source,str):continue
                target=Path(target);source=Path(source)
                if not target.is_relative_to(directory)or target.name!=source.name or source.parent.name!='Catalog':continue
                if target.parent!=catalog:
                    # Some producers bind through a retained working copy, then
                    # install exactly those bytes into their cloned catalog.
                    alias=catalog/target.name
                    if target.suffix!='.bundle'or not target_sha or not alias.is_file()or sha(checked_source_path(alias))!=target_sha:continue
                    if target.is_file()and sha(checked_source_path(target))==target_sha:explicit_payloads[target]=target_sha
                declaration=definition(source.parent/RESOURCE_FILES['manifest'])
                if declaration[2].get(source.name)!=expected:continue
                declarations.append(declaration);guards.append(path)
    if not declarations:return None
    names=set.intersection(*[set(row[2])for row in declarations])
    payloads={catalog/name:sha(checked_source_path(catalog/name))for name in names
              if Path(name).suffix=='.bundle'and(catalog/name).is_file()}
    payloads.update(explicit_payloads)
    return dict(directory=directory,sha256=None,accepted=False,modified=max(path.stat().st_mtime_ns for path in guards),
        owned_payloads=payloads,guards=[dict(path=str(path),sha256=sha(path))for path in set(guards)|{row[0]for row in declarations}],
        kind='partial',sourceManifest=dict(path=str(declarations[0][0]),sha256=declarations[0][1]))


def published_package(directory,adopted=None,retained_hashes=()):
    marker=directory/RESOURCE_FILES['packageInputs'];metadata=directory/RESOURCE_FILES['packs']
    receipt=directory.parent/'PublicationReceipt.json'
    if not(marker.is_file()and metadata.is_file()):return None
    owner=read(checked_source_path(marker));specification=read(checked_source_path(metadata))
    if owner.get('kind')!='CharacterResourcePacks'or owner.get('schemaVersion')!=1:return None
    if owner['sourceSha256']!=specification['catalogSha256']:raise ValueError('Published package ownership differs')
    if owner['version']!=specification['version']:raise ValueError('Published package version differs')
    # Material-only catalog updates can reuse a released ZIP. The remote asset
    # digest, rather than the receipt's later manifest digest, owns those bytes.
    publication=read(checked_source_path(receipt))if receipt.is_file()else {}
    assets={row['name']:row for row in publication.get('assets',[])};payloads={}
    released=publication.get('status')=='PASS_PUBLISHED_MODEL_RESOURCES'and publication.get('tag')==resource_release_metadata(owner['version'])['tag_name']
    for row in specification['packs']:
        name=row['file']
        if not isinstance(name,str)or Path(name).suffix.lower()!='.zip':raise ValueError('A package has an unowned archive declaration')
        released=released and name in assets and all(row[key]==assets[name][key]for key in ('bytes','sha256'))
        path=checked_catalog_path(directory,name)
        if path.is_file():
            if path.stat().st_size!=row['bytes']or sha(checked_source_path(path))!=row['sha256']:raise ValueError('A local published package changed')
            payloads[path]=row['sha256']
    superseded=adopted and owner['sourceSha256']not in retained_hashes and max(path.stat().st_mtime_ns for path in (marker,metadata))<adopted['modified']
    if not released and not superseded:return None
    guards=(marker,metadata,receipt)if released else(marker,metadata)
    return dict(directory=directory,owned_payloads=payloads,kind='published-package'if released else'superseded-package',
        guards=[dict(path=str(path),sha256=sha(path))for path in guards])


def walk(root):
    for folder, children, names in os.walk(root, followlinks=False):
        if "SolastaUnfinishedBusiness.sln" in names:
            children[:] = []
            continue
        children[:] = [name for name in children if not (Path(folder) / name).is_junction()
                       and not (Path(folder) / name).is_symlink() and name not in ("Tools", "Python", "Library", ".native")]
        yield Path(folder), names


def has_payloads(directory):
    """Avoid reparsing large producer receipts once all their payloads were retired."""
    for name in ("Catalog", "GeometryCatalog"):
        folder = directory / name
        if folder.is_symlink() or folder.is_junction():
            return True  # Keep linked paths on the full ownership-validation route.
        if not folder.is_dir():
            continue
        with os.scandir(folder) as entries:
            if any(entry.name.lower().endswith(".bundle") and entry.is_file(follow_symlinks=False)
                   for entry in entries):
                return True
    # Completed producers also retain local texture fields and material sources.
    # These have the same receipt ownership as final bundles, regardless of folder name.
    return any(data_payload(folder/name)for folder,names in walk(directory)for name in names)


def discover(roots):
    catalogs, retained = [], []
    for root in roots:
        for folder, names in walk(root):
            if "ResourceAssembly.json" not in names:
                continue
            owner = folder / "ResourceAssembly.json"
            manifest = folder / (Path('Catalog')/RESOURCE_FILES['manifest'])
            if not manifest.is_file() or not has_payloads(folder):
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
                owned_payloads = completed_resource_payloads(folder, assembly)
                local_payloads,local_guards=declared_local_payloads(folder,assembly)
                owned_payloads.update(local_payloads)
                catalogs.append(dict(root=root, directory=folder, owner=owner, manifest=manifest,
                    sha256=fingerprint, accepted=resource_art_acceptance(assembly, fingerprint),
                    modified=owner.stat().st_mtime_ns, payloads=payloads, owned_payloads=owned_payloads,
                    guards=[dict(path=str(owner),sha256=sha(owner)),dict(path=str(manifest),sha256=fingerprint),*local_guards],kind='completed'))
            except (ValueError, KeyError, OSError) as error:
                retained.append(dict(path=str(folder), reason=str(error)))
    return catalogs, retained


def producer_processes():
    """Only inspect identities; command lines are neither stored nor printed."""
    if os.name!='nt':return []
    script="[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); @(Get-CimInstance Win32_Process -Filter \"Name = 'python.exe' OR Name = 'pythonw.exe'\" | Select-Object ProcessId,ParentProcessId,CommandLine) | ConvertTo-Json -Compress"
    result=subprocess.run(['powershell.exe','-NoLogo','-NoProfile','-NonInteractive','-Command',script],capture_output=True,encoding='utf-8-sig',check=True)
    rows=json.loads(result.stdout or '[]');rows=rows if isinstance(rows,list)else[rows]
    def arguments(row):return [a or b for a,b in re.findall(r'"([^"]*)"|(\S+)',row.get('CommandLine')or'')]
    by_id={row['ProcessId']:row for row in rows};own_arguments=arguments(by_id.get(os.getpid(),{}))[1:]
    redirectors=set();cursor=os.getpid()
    while cursor in by_id and cursor not in redirectors:
        row=by_id[cursor]
        if cursor!=os.getpid()and arguments(row)[1:]!=own_arguments:break
        redirectors.add(cursor);cursor=row.get('ParentProcessId')
    active=[]
    for row in rows:
        if row['ProcessId']in redirectors:continue
        tokens=arguments(row)
        scripts=[Path(value).absolute()for value in tokens[1:]if value.lower().endswith('.py')]
        if any(path.is_relative_to(ROOT)for path in scripts)or '-c'in tokens:
            active.append(row['ProcessId'])
    return active


def declared_live_inputs(current):
    """Keep retained source bytes used by the adopted proof, including raw inputs."""
    result=set()
    def visit(value):
        if isinstance(value,dict):
            for child in value.values():visit(child)
        elif isinstance(value,list):
            for child in value:visit(child)
        elif isinstance(value,str):
            path=Path(value)
            if path.is_absolute()and data_payload(path)and any(path.is_relative_to(root)for root in (ARTIFACTS,WORKSPACE)):
                if path.is_file():result.add(path)
    visit(read(current['owner']))
    return result


def work_source_inputs(record):
    """An unfinished or newer producer keeps the source closure it declared."""
    pin=record.get('sourceManifest')
    if not pin:return set()
    path,_,payloads=source_definition(Path(pin['path']),pin['sha256'])
    return {path.parent/name for name in payloads}


def discover_work(roots,current,known,apply,retained_hashes=()):
    records=[];retained=[];packages=[];registered=set();active=producer_processes();live=set();source_cache={}
    for root in roots:
        for folder,names in walk(root):
            if folder.name=='.resource-work':
                for name in names:
                    if not name.endswith('.json'):continue
                    marker=folder/name
                    try:
                        document=read(checked_source_path(marker))
                        if document.get('kind')!='CharacterResourceWork'or document.get('schemaVersion')!=1:raise ValueError('Unknown resource work ownership')
                        output=checked_catalog_path(document['output'])
                        if marker!=work_marker(output):raise ValueError('Resource work identity does not match its output')
                        registered.add(output)
                        if work_active(document):
                            live.update(work_source_inputs(document))
                            retained.append(dict(path=str(output),reason='active-resource-producer'));continue
                        payloads={}
                        for pin in document['files']:
                            path=Path(pin['path'])
                            if not path.is_relative_to(output)or not data_payload(path):raise ValueError('A resource work payload escaped its owner')
                            if path.exists():
                                if sha(checked_source_path(path))!=pin['sha256']:raise ValueError('A resource work payload changed')
                                payloads[path]=pin['sha256']
                        if document.get('state')=='running'and 'baselineFiles'in document:
                            baseline={Path(value)for value in document['baselineFiles']}
                            if any(not path.is_relative_to(output)or not data_payload(path)for path in baseline):raise ValueError('An interrupted resource baseline escaped its owner')
                            for path in work_files(output)-baseline:
                                payloads[path]=sha(checked_source_path(path))
                        if document.get('modifiedNs',marker.stat().st_mtime_ns)>=current['modified']:
                            live.update(work_source_inputs(document))
                        if not payloads:continue
                        guards=[dict(path=str(marker),sha256=sha(marker))]+document.get('guards',[])
                        records.append(dict(root=root,directory=output,sha256=None,accepted=False,
                            modified=document.get('modifiedNs',marker.stat().st_mtime_ns),owned_payloads=payloads,guards=guards,kind='work'))
                    except (OSError,ValueError,KeyError,TypeError)as error:retained.append(dict(path=str(marker),reason=str(error)))
            if RESOURCE_FILES['packageInputs']in names:
                try:
                    package=published_package(folder,current,retained_hashes)
                    if package and package['owned_payloads']:
                        if active:retained.append(dict(path=str(folder),reason='active-resource-producer'))
                        else:package['root']=root;packages.append(package)
                except (OSError,ValueError,KeyError,TypeError)as error:retained.append(dict(path=str(folder),reason=str(error)))
    for root in roots:
        for folder,_ in walk(root):
            if folder in known or folder in registered or not(folder/'Catalog').is_dir()or not has_payloads(folder):continue
            try:
                partial=partial_catalog(folder,source_cache)
                if partial is None:continue
                if active or partial['modified']>=current['modified']:
                    retained.append(dict(path=str(folder),reason='active-or-newer-resource-work'));continue
                partial['root']=root;partial['previewOnly']=not apply
                if apply:
                    marker=work_marker(folder)
                    document=dict(schemaVersion=1,kind='CharacterResourceWork',output=str(folder),command='retained-producer-inputs',
                        state='superseded',creator=None,sourceManifest=partial['sourceManifest'],modifiedNs=partial['modified'],
                        guards=partial['guards'],files=[dict(path=str(path),sha256=digest)for path,digest in partial['owned_payloads'].items()])
                    write(marker,document);partial['guards'].append(dict(path=str(marker),sha256=sha(marker)))
                records.append(partial)
            except (OSError,ValueError,KeyError,TypeError)as error:retained.append(dict(path=str(folder),reason=str(error)))
    return records,packages,retained,live


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


def select_retired(catalogs, public_sha, keep_accepted, source_hashes=()):
    adopted = [row for row in catalogs if row["sha256"] == public_sha and row["accepted"]]
    if not adopted:
        raise ValueError("No accepted local catalog matches the adopted product manifest")
    current = max(adopted, key=lambda row: row["modified"])
    previous = sorted((row for row in catalogs if row["accepted"] and row["sha256"] != public_sha),
                      key=lambda row: row["modified"], reverse=True)
    keep = {public_sha}
    keep.update(row["sha256"] for row in previous[:max(0, keep_accepted - 1)])
    keep.update(source_hashes)
    old = [row for row in catalogs if row["sha256"] not in keep and row["modified"] < current["modified"]]
    return current, keep, old


def adopted_source_hashes(current):
    """The immutable adopted source is an input, independent of review timestamps."""
    quality_path = current["directory"] / "Catalog/CatalogQuality.json"
    if not quality_path.is_file():
        return set()
    quality = read(checked_source_path(quality_path))
    if quality.get("candidateManifestSha256") != current["sha256"]:
        raise ValueError("The adopted quality receipt does not match its catalog")
    source = checked_source_path(Path(quality["sourceCatalog"]) / RESOURCE_FILES['manifest'])
    if not any(source.is_relative_to(root) for root in (ARTIFACTS, WORKSPACE)):
        raise ValueError("The adopted source is outside the owned tool workspaces")
    fingerprint = sha(source)
    if fingerprint != quality["sourceManifestSha256"]:
        raise ValueError("The immutable adopted source catalog changed")
    return {fingerprint}


def plan(output, apply=False, keep_accepted=2):
    output = output.resolve()
    if not output.is_relative_to(ARTIFACTS):
        raise ValueError("Storage evidence belongs below game-test/artifacts")
    roots = (ARTIFACTS, WORKSPACE)
    active=producer_processes()
    if active:
        record=dict(schemaVersion=1,status='retained-active-resource-producer',activeProcesses=active,plans=[])
        write(output/'ResourceLifecycle.json',record);print(json.dumps(record));return record
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
    source_hashes = adopted_source_hashes(current)
    current, keep, old = select_retired(catalogs, public_sha, keep_accepted, source_hashes)
    protected={row['directory']for row in catalogs if row['sha256']in keep}
    if source_hashes:
        protected.add(checked_catalog_path(Path(read(current['directory']/'Catalog/CatalogQuality.json')['sourceCatalog'])).parent)
    work,packages,work_retained,work_inputs=discover_work(roots,current,{row['directory']for row in catalogs}|protected,apply,keep)
    # A completed catalog keeps its source/geometry ownership; the work record
    # adds other explicitly generated payloads without replacing that authority.
    by_directory={row['directory']:row for row in catalogs}
    for row in work:
        if row['directory']in by_directory:
            known=by_directory[row['directory']];known['owned_payloads'].update(row['owned_payloads']);known['guards']+=row['guards']
        elif row['modified']<current['modified']:old.append(row)
    retired=[*old,*packages]
    live=declared_live_inputs(current)|work_inputs
    obsolete = {path for row in retired for path in row["owned_payloads"]if path not in live}
    references_path = ARTIFACTS / "StorageReferences.json"
    before = read(references_path) if references_path.is_file() else {"schemaVersion": 1, "paths": []}
    if set(before) != {"schemaVersion", "paths"} or before["schemaVersion"] != 1:
        raise ValueError("Unsupported storage references")
    original_sha = sha(references_path) if references_path.exists() else None
    after = {"schemaVersion": 1, "paths": compact_references(before["paths"], obsolete)}
    # Protect the accepted owner and bytes even when a legacy reference file omitted them.
    after["paths"] = sorted(set(after["paths"]) | {str(path)for path in protected})
    after['paths']=sorted(set(after['paths'])|{str(path)for path in live},key=str.casefold)
    preview_reference = output / "RetainedInputs.json"
    write(preview_reference, after)
    plans = []
    for index, root in enumerate(roots):
        owners = [row for row in retired if row["root"] == root]
        if not owners:
            continue
        destination = output / f"ResourcePayloads-{index}.json"
        payloads={path:digest for row in owners for path,digest in row['owned_payloads'].items()if path in obsolete}
        guards={pin['path']:pin for row in owners for pin in row.get('guards',[])}
        guards[str(PUBLIC)]=dict(path=str(PUBLIC),sha256=public_sha)
        result = create_declared_payload_plan(payloads,preview_reference,destination,storage_root=root,guard_inputs=guards.values())
        if any(row.get('previewOnly')for row in owners):
            result['previewOnly']=True;write(destination,result)
        plans.append({"root": str(root), "plan": str(destination), "files": len(result["files"]),
                      "logicalBytes": result["logicalBytes"]})
    record = {"schemaVersion": 1, "status": "planned", "adopted": str(current["directory"]),
              "adoptedManifestSha256": public_sha, "keepAcceptedCatalogs": keep_accepted,
              "retainedCatalogHashes": sorted(keep), "completedCatalogs": len(catalogs),
              "retainedSourceCatalogHashes": sorted(source_hashes),
              "retiredCatalogs": [str(row["directory"]) for row in old], "unverifiedRetained": retained,
              "retiredPublishedPackages": [str(row['directory'])for row in packages if row['kind']=='published-package'],
              "retiredSupersededPackages": [str(row['directory'])for row in packages if row['kind']=='superseded-package'],"retainedWork":work_retained,
              "referencesBeforeSha256": original_sha, "referencesBeforeCount": len(before["paths"]),
              "referencesAfterCount": len(after["paths"]), "plans": plans,
              "scope": "Declared completed, failed and interrupted resource outputs older than adoption, and packages with matching publication receipts. Sources and retained proof inputs remain."}
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
    parser.add_argument('--inventory-only',action='store_true')
    args = parser.parse_args()
    if args.inventory_only:
        result=inventory_tools(ROOT/'tools');write(args.output/'StorageInventory.json',result)
        print(json.dumps({key:value for key,value in result.items()if key not in ('groups','skippedReparsePoints')}));return
    plan(args.output, args.apply, args.keep_accepted)


if __name__ == "__main__":
    main()
