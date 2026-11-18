"""Retire declared resource outputs after adoption or verified publication."""
from __future__ import annotations

from array import array
import argparse
import contextlib
import contextvars
import collections
import ctypes
import datetime
import functools
import hashlib
import json
import os
import re
import subprocess
import sys
import time
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


_hash_scope=contextvars.ContextVar('resource_payload_hash_scope',default=None)


class _FileBasicInfo(ctypes.Structure):
    _fields_=[(name,ctypes.c_int64)for name in('creation','access','write','change')]+[('attributes',ctypes.c_uint32)]


class _FileStandardInfo(ctypes.Structure):
    _fields_=[('allocation',ctypes.c_int64),('size',ctypes.c_int64),('links',ctypes.c_uint32),
              ('deletePending',ctypes.c_ubyte),('directory',ctypes.c_ubyte)]


class _FileIdInfo(ctypes.Structure):
    _fields_=[('volume',ctypes.c_uint64),('identifier',ctypes.c_ubyte*16)]


@functools.lru_cache(maxsize=1)
def _file_info_query():
    query=ctypes.WinDLL('kernel32',use_last_error=True).GetFileInformationByHandleEx
    query.argtypes=[ctypes.c_void_p,ctypes.c_int,ctypes.c_void_p,ctypes.c_uint32];query.restype=ctypes.c_int
    return query


def file_hash_identity(stream):
    """Use the actual NTFS change timestamp; Windows st_ctime is creation time."""
    if os.name!='nt':
        value=os.fstat(stream.fileno())
        return(value.st_dev,value.st_ino,value.st_size,value.st_mtime_ns,value.st_ctime_ns)if value.st_ino else None
    import msvcrt
    query=_file_info_query()
    handle=msvcrt.get_osfhandle(stream.fileno());basic=_FileBasicInfo();standard=_FileStandardInfo();identity=_FileIdInfo()
    for kind,value in((0,basic),(1,standard),(18,identity)):
        if not query(handle,kind,ctypes.byref(value),ctypes.sizeof(value)):return None
    if standard.directory or standard.deletePending or not basic.change or not any(identity.identifier):return None
    return(identity.volume,bytes(identity.identifier),standard.size,basic.creation,basic.write,basic.change)


def _portable_hash_identity(value):
    # Windows stat/fstat expose creation time differently after an overwrite;
    # neither is the NTFS ChangeTime used by the payload cache.
    return(value.st_dev,value.st_ino,value.st_size,value.st_mtime_ns,None if os.name=='nt'else value.st_ctime_ns)


@contextlib.contextmanager
def payload_hash_operation(*,progress_seconds=30,progress_bytes=16*1024**3):
    previous=_hash_scope.get()
    if previous is not None:
        yield previous;return
    scope=dict(digests={},metadataReadPaths={},payloadCacheHits=0,verifiedReferenceReuses=0,inputLeaseAcquisitions=0,inputLeaseEvictions=0,maximumInputLeases=0,inputLeaseReuses=0,inputLeaseFallbacks=0,payloadHashes=0,metadataHashes=0,
               identityUnavailableFallbacks=0,payloadShaReadBytes=0,metadataShaReadBytes=0)
    scope['progress']=dict(stage='resource-hash-validation',lastTime=time.monotonic(),lastBytes=0,
                           seconds=progress_seconds,bytes=progress_bytes)
    token=_hash_scope.set(scope)
    try:yield scope
    finally:
        scope['digests'].clear();scope['metadataReadPaths'].clear();scope['progress'].clear();_hash_scope.reset(token)


def hash_statistics(scope):
    repeated=[dict(path=path,reads=row['reads'],logicalReadBytes=row['bytes'],
                   subsequentReadBytes=row['bytes']-row['firstBytes'])
              for path,row in scope['metadataReadPaths'].items()if row['reads']>1]
    return dict({key:value for key,value in scope.items()if key not in('digests','metadataReadPaths','progress')},
        repeatedMetadataInputs=len(repeated),repeatedMetadataReadBytes=sum(row['subsequentReadBytes']for row in repeated),
        topRepeatedMetadataInputs=sorted(repeated,key=lambda row:(-row['subsequentReadBytes'],row['path']))[:12])


def hash_progress(scope,*,stage=None,force=False):
    progress=scope['progress']
    if stage is not None:progress['stage']=stage
    now=time.monotonic();total=scope['payloadShaReadBytes']+scope['metadataShaReadBytes']
    if not force and now-progress['lastTime']<progress['seconds']and total-progress['lastBytes']<progress['bytes']:return
    statistics=hash_statistics(scope)
    print('Resource hash progress: '+json.dumps(dict(stage=progress['stage'],shaReadBytes=total,
        payloadCacheHits=statistics['payloadCacheHits'],payloadHashes=statistics['payloadHashes'],metadataHashes=statistics['metadataHashes'],
        verifiedReferenceReuses=statistics['verifiedReferenceReuses'],
        inputLeaseReuses=statistics['inputLeaseReuses'],maximumInputLeases=statistics['maximumInputLeases'],
        topRepeatedMetadataInputs=statistics['topRepeatedMetadataInputs'][:3])),flush=True)
    progress.update(lastTime=now,lastBytes=total)


def sha(path,*,payload=False,identity_out=None):
    path=Path(path);scope=_hash_scope.get()
    candidate=scope is not None and payload
    observe_identity=candidate or identity_out is not None
    with path.open('rb')as stream:
        before=_portable_hash_identity(os.fstat(stream.fileno()));identity=file_hash_identity(stream)if observe_identity else None
        use_cache=candidate and identity is not None
        cached=scope['digests'].get(identity)if use_cache else None
        digest=cached if cached is not None else hashlib.file_digest(stream,'sha256').hexdigest()
        after_identity=file_hash_identity(stream)if observe_identity else None
        after=_portable_hash_identity(os.fstat(stream.fileno()))
        current=_portable_hash_identity(path.stat())
        if before!=after or after!=current or(identity is not None and after_identity is not None and identity!=after_identity):
            raise ValueError('A resource hash input changed while its identity was checked')
        if cached is not None and after_identity is None:
            # Identity loss cannot authorize an old digest, even on a cache hit.
            scope['digests'].pop(identity,None)
            stream.seek(0);digest=hashlib.file_digest(stream,'sha256').hexdigest();cached=None;use_cache=False
            if after!=_portable_hash_identity(os.fstat(stream.fileno()))or after!=_portable_hash_identity(path.stat()):
                raise ValueError('A resource hash input changed during fresh fallback')
        if scope is not None:
            if cached is not None:scope['payloadCacheHits']+=1
            else:
                scope['payloadHashes'if payload else'metadataHashes']+=1
                scope['payloadShaReadBytes'if payload else'metadataShaReadBytes']+=before[2]
                if not payload:
                    row=scope['metadataReadPaths'].setdefault(str(path),dict(reads=0,bytes=0,firstBytes=before[2]))
                    row['reads']+=1;row['bytes']+=before[2]
            if observe_identity and(identity is None or after_identity is None):scope['identityUnavailableFallbacks']+=1
            if use_cache and after_identity==identity:scope['digests'][identity]=digest
            hash_progress(scope)
        if identity_out is not None and identity is not None and identity==after_identity:identity_out.append(identity)
        return digest


@functools.lru_cache(maxsize=1)
def _lease_file_api():
    kernel=ctypes.WinDLL('kernel32',use_last_error=True)
    create=kernel.CreateFileW;create.argtypes=[ctypes.c_wchar_p,ctypes.c_uint32,ctypes.c_uint32,ctypes.c_void_p,ctypes.c_uint32,ctypes.c_uint32,ctypes.c_void_p];create.restype=ctypes.c_void_p
    close=kernel.CloseHandle;close.argtypes=[ctypes.c_void_p];close.restype=ctypes.c_int
    return create,close


def _open_read_lease(path):
    if os.name!='nt':return None
    import msvcrt
    raw=None;descriptor=None
    try:
        create,close=_lease_file_api()
        raw=create(str(path),0x80000000,1,None,3,0x00200000,None)
        if raw==ctypes.c_void_p(-1).value:raw=None;return None
        descriptor=msvcrt.open_osfhandle(raw,os.O_RDONLY|os.O_BINARY);raw=None
        stream=os.fdopen(descriptor,'rb');descriptor=None
        return stream
    except OSError:return None
    finally:
        if descriptor is not None:
            with contextlib.suppress(OSError):os.close(descriptor)
        if raw is not None:close(raw)


class ReferenceVersions(dict):
    """One closure owns its verified pins and a bounded number of read leases."""
    def __init__(self,maximum_leases=32):
        super().__init__();self.maximum_leases=maximum_leases;self.held_leases=0;self.leased_order=collections.OrderedDict()
    def make_space(self):
        if self.held_leases<self.maximum_leases:return
        key=next(iter(self.leased_order));entry=self[key]
        entry['lease'].close();entry.pop('lease');self.leased_order.pop(key);self.held_leases-=1
        scope=_hash_scope.get()
        if scope is not None:scope['inputLeaseEvictions']+=1
    def close(self):
        failure=None
        try:
            for entry in self.values():
                if isinstance(entry,dict)and entry.get('lease')is not None:
                    try:entry['lease'].close()
                    except BaseException as error:
                        if failure is None:failure=error
        finally:self.clear();self.leased_order.clear();self.held_leases=0
        if failure is not None:raise failure


def leased_reference(path,digest,declared_role,versions):
    key=Path(path),digest.lower(),declared_role;entry=versions.get(key)
    if isinstance(entry,dict)and entry.get('lease')is not None and not entry['lease'].closed:
        if isinstance(versions,ReferenceVersions):versions.leased_order.move_to_end(key)
        return entry['path']
    return None


def checked_reference_hash(path,digest,declared_role,versions,*,payload=False,within=None):
    retained=leased_reference(path,digest,declared_role,versions)
    path=retained if retained is not None else checked_source_path(path)
    if within is not None and not path.is_relative_to(within):return path,None
    return path,reference_hash(path,digest,declared_role,versions,payload=payload)


def reference_hash(path,digest,declared_role,versions,*,payload=False):
    """Reuse one closure's checked reference only while its actual file is unchanged."""
    path=Path(path);key=path,digest.lower(),declared_role
    entry=versions.get(key);previous=entry.get('identity')if isinstance(entry,dict)else entry
    scope=_hash_scope.get()
    if leased_reference(path,digest,declared_role,versions)is not None:
        if scope is not None:scope['inputLeaseReuses']+=1;hash_progress(scope)
        return digest.lower()
    if previous is not None and isinstance(versions,ReferenceVersions)and versions.maximum_leases>0:
        versions.make_space()
        stream=_open_read_lease(path)
        if stream is not None:
            try:
                if file_hash_identity(stream)==previous:
                    entry['lease']=stream;versions.leased_order[key]=None;versions.held_leases+=1
                    if scope is not None:
                        scope['inputLeaseAcquisitions']+=1;scope['inputLeaseReuses']+=1
                        scope['maximumInputLeases']=max(scope['maximumInputLeases'],versions.held_leases);hash_progress(scope)
                    return digest.lower()
            except OSError:pass
            finally:
                if entry.get('lease')is not stream:stream.close()
        if scope is not None:scope['inputLeaseFallbacks']+=1
    if previous is not None:
        with path.open('rb')as stream:
            before=_portable_hash_identity(os.fstat(stream.fileno()));identity=file_hash_identity(stream)
            after=_portable_hash_identity(os.fstat(stream.fileno()));current=_portable_hash_identity(path.stat())
            final_identity=file_hash_identity(stream)
            if identity==previous and final_identity==previous and before==after==current:
                if scope is not None:scope['verifiedReferenceReuses']+=1;hash_progress(scope)
                return digest.lower()
    observed=[];actual=sha(path,payload=payload,identity_out=observed)
    if observed and actual==digest.lower():
        versions[key]=dict(identity=observed[0],path=path)if isinstance(versions,ReferenceVersions)else observed[0]
    else:versions.pop(key,None)
    return actual


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
    root=Path(root).resolve();identities={}
    summary=lambda:dict(files=0,logicalBytes=0,identities=set())
    groups=collections.defaultdict(summary);extensions=collections.defaultdict(summary)
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
                extension=extensions[path.suffix.lower()]
                extension['files']+=1;extension['logicalBytes']+=info.st_size;extension['identities'].add(identity)
                for depth in range(1,min(4,len(relative.parts))):
                    group=groups['/'.join(relative.parts[:depth])]
                    group['files']+=1;group['logicalBytes']+=info.st_size;group['identities'].add(identity)
    rows=[dict(path=path,files=row['files'],logicalBytes=row['logicalBytes'],uniqueFileBytes=sum(identities[key]for key in row['identities']))for path,row in groups.items()]
    types=[dict(extension=extension,files=row['files'],logicalBytes=row['logicalBytes'],uniqueFileBytes=sum(identities[key]for key in row['identities']))for extension,row in extensions.items()]
    return dict(status='PASS_TOOL_STORAGE_INVENTORY',root=str(root),files=files,directories=directories,logicalBytes=logical,
        uniqueFileBytes=sum(identities.values()),groups=sorted(rows,key=lambda row:row['logicalBytes'],reverse=True),
        extensions=sorted(types,key=lambda row:row['uniqueFileBytes'],reverse=True),skippedReparsePoints=links)


DATA_EXTENSIONS={'.bundle','.bin','.bytes','.bits','.gz','.rgba8','.cpu-rgba8','.bc3','.bc7','.dxt5','.mips','.u8','.encoded','.png','.zip'}


def data_payload(path):
    return path.suffix.lower()in DATA_EXTENSIONS or bool(re.fullmatch(r'[a-f0-9]{64}\.json',path.name))


def pin_role(path,pin=None,*,generation_cache=None):
    """Typed producer declarations override the legacy file-format fallback."""
    role=(pin or {}).get('role')
    if role is not None:
        if role not in ('payload','metadata'):raise ValueError('Unknown producer output role')
        return role
    digest=(pin or {}).get('sha256','')
    digest=digest.lower()if isinstance(digest,str)else''
    verified=(generation_cache or {}).get(digest,{})
    if verified.get('packetValidation')or verified.get('typedPayloadAuthority'):return 'payload'
    return 'payload'if data_payload(path)else'metadata'if path.suffix.lower()=='.json'or path.name.lower().endswith('.json.source')else None


def numeric_payload(path,document,digest,cache,*,declared_role=None):
    """Classify an actual validated mesh without treating arbitrary JSON as data."""
    if not isinstance(document,dict)or not all(key in document for key in('vertexCount','subMeshes','sourceFingerprint')):return False
    if declared_role=='metadata':raise ValueError('A numeric mesh packet was declared as reference metadata')
    from collect_sources import validate_packet
    entry=cache.get(digest,{})
    validation=entry.get('packetValidation')or validate_packet(document)
    # Promote only after a content/identity check through the payload hash owner.
    if sha(path,payload=True)!=digest:raise ValueError('A numeric payload changed during classification')
    cache.setdefault(digest,{})['packetValidation']=validation
    return True


def generation_inputs(document):
    pins=[]
    for path_key,hash_key,role in (('generatorPath','generatorSha256','payload'),
                                  ('sourceInput','sourceInputSha256','metadata'),
                                  ('sourceGeneration','sourceGenerationSha256','metadata')):
        if path_key in document and hash_key in document:
            pins.append(dict(path=document[path_key],sha256=document[hash_key],role=role))
    for row in document.get('rows',[]):
        for path_key,hash_key in (('nativePacketPath','nativePacketSha256'),('packetPath','packetSha256')):
            if path_key in row and hash_key in row:pins.append(dict(path=row[path_key],sha256=row[hash_key],role='payload'))
    return pins


def normalize_generation(path,document,cache=None):
    """Adapt verified old mesh producers to the same typed output contract."""
    cache={}if cache is None else cache
    if not isinstance(document,dict):return document
    if document.get('kind')=='CharacterResourceGeneration':
        if document.get('schemaVersion')!=1 or document.get('state')not in ('completed','failed')or not isinstance(document.get('outputs'),list):
            raise ValueError('Invalid typed resource generation')
        for pin in document['outputs']:
            if pin_role(Path(pin['path']),pin)not in ('payload','metadata')or not re.fullmatch('[a-fA-F0-9]{64}',pin.get('sha256','')):
                raise ValueError('Invalid typed generation output')
        return dict(document,inputs=[*document.get('inputs',[]),*generation_inputs(document)])
    if not all(key in document for key in ('generatorPath','generatorSha256','sourceInput','sourceInputSha256','rows')):
        return document
    rows=document['rows']
    if not isinstance(rows,list)or not rows or not all(isinstance(row,dict)and all(key in row for key in ('generatedPacket','generatedSha256','bindingPath','bindingSha256'))for row in rows):
        return document
    for path_key,hash_key in (('generatorPath','generatorSha256'),('sourceInput','sourceInputSha256')):
        source=Path(document[path_key]);digest=document[hash_key]
        if not source.is_absolute():raise ValueError('A legacy producer input has no absolute identity')
        source=checked_source_path(source)
        if not re.fullmatch('[a-fA-F0-9]{64}',digest)or sha(source)!=digest.lower():
            raise ValueError('A legacy generation producer or source changed')
    owner=checked_catalog_path(Path(path).parent);outputs={}
    for row in rows:
        for path_key,hash_key in (('generatedPacket','generatedSha256'),('bindingPath','bindingSha256')):
            target=Path(row[path_key]);digest=row[hash_key]
            if not target.is_absolute()or not target.is_relative_to(owner)or not re.fullmatch('[a-fA-F0-9]{64}',digest):
                raise ValueError('A legacy generation output escaped its owner')
            target=checked_source_path(target);digest=digest.lower()
            if not target.is_relative_to(owner):raise ValueError('A legacy generation output escaped its owner')
            if target in outputs and outputs[target]!=digest:raise ValueError('Conflicting legacy generation output hashes')
            if sha(target,payload=True)!=digest:raise ValueError('A legacy generation output changed')
            if path_key=='generatedPacket'and not cache.get(digest,{}).get('packetValidation'):
                packet=read(target)
                if not numeric_payload(target,packet,digest,cache):
                    from collect_sources import validate_packet
                    validate_packet(packet)
                    raise ValueError('A generated mesh has no numeric packet identity')
            cache.setdefault(digest,{})['typedPayloadAuthority']=True
            outputs[target]=digest
    return dict(document,kind='CharacterResourceGeneration',schemaVersion=1,state='completed',
        outputs=[dict(path=str(target),sha256=digest,role='payload')for target,digest in outputs.items()],
        inputs=[*document.get('inputs',[]),*generation_inputs(document)])


def declared_local_payloads(directory, document,*,generation_cache=None):
    """Follow hash-pinned producer receipts, keeping their declared local data only."""
    directory=checked_catalog_path(directory);payloads={};guards={}
    generation_cache={}if generation_cache is None else generation_cache
    # The owner may cite an earlier revision of itself in its production history.
    # Its current bytes are already guarded by discovery; do not reopen that revision.
    visited=set();fresh_inputs=set();reference_versions=ReferenceVersions()
    def visit(value):
        if isinstance(value,list):
            for child in value:visit(child)
            return
        if not isinstance(value,dict):return
        if isinstance(value.get('generatorPath'),str)and 'generatorSha256'in value:
            fresh_inputs.add(Path(value['generatorPath']))
        path,digest=value.get('path'),value.get('sha256')
        if isinstance(path,str)and isinstance(digest,str)and re.fullmatch(r'[a-fA-F0-9]{64}',digest):
            path=Path(path)
            if path==directory/'ResourceAssembly.json':return
            retained=leased_reference(path,digest,value.get('role'),reference_versions)
            if path.is_absolute()and path.is_relative_to(directory)and(retained is not None or path.is_file()):
                path,actual=checked_reference_hash(path,digest,value.get('role'),reference_versions,payload=pin_role(path,value,generation_cache=generation_cache)=='payload'and path not in fresh_inputs,within=directory)
                digest=digest.lower()
                if not path.is_relative_to(directory):return
                previous=guards.get(path)or payloads.get(path)
                if previous is not None and previous!=digest:raise ValueError('A producer declares conflicting local payload hashes')
                selected_role=pin_role(path,value,generation_cache=generation_cache)
                if path==directory/'ResourceAssembly.json':return
                if actual!=digest:
                    raise ValueError('A declared local producer input changed: '+str(path))
                if selected_role=='metadata'and value.get('role')=='metadata'and generation_cache.get(digest,{}).get('packetValidation'):
                    raise ValueError('A numeric mesh packet was declared as reference metadata')
                if value.get('role')=='payload':generation_cache.setdefault(digest,{})['typedPayloadAuthority']=True
                version=path,digest,selected_role,value.get('role')
                if version not in visited:
                    visited.add(version)
                    if selected_role=='payload':payloads[path]=digest
                    elif selected_role=='metadata':
                        child=read(path)
                        if numeric_payload(path,child,digest,generation_cache,declared_role=value.get('role')):payloads[path]=digest
                        else:
                            guards[path]=digest;visit(normalize_generation(path,child,generation_cache))
        for child in value.values():visit(child)
    try:
        visit(document)
        return payloads,[dict(path=str(path),sha256=digest)for path,digest in guards.items()]
    finally:reference_versions.close()


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


def work_files(output,*,all_created=False):
    if not output.is_dir():return set()
    return {path for folder,names in walk(output,owned_output=True)for name in names for path in(folder/name,)
            if (all_created or data_payload(path))and path.is_file()}


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
def resource_work(command,arguments,*,default_output=None,receipt_flag='--input',metadata_names=()):
    """Claim outputs before a producer writes; terminal records include failed work."""
    parser=argparse.ArgumentParser(add_help=False)
    parser.add_argument('--source',type=Path);parser.add_argument('--manifest',type=Path)
    parser.add_argument(receipt_flag,type=Path,dest='receipt');parser.add_argument('--output',type=Path)
    options,_=parser.parse_known_args(arguments)
    source=options.manifest or (options.source/(Path('Catalog')/RESOURCE_FILES['manifest'])if options.source else options.receipt)
    source_role='catalog'if options.manifest or options.source else'receipt'
    output=options.output or default_output
    if output is not None:output=Path(output)
    if source is None or output is None or not source.is_file()or output.suffix.lower()in ('.json','.zip','.png'):
        yield;return
    metadata=[checked_catalog_path(output,name) for name in metadata_names]
    with resource_operation():
        with owned_resource_work(command,source,output,source_role=source_role,metadata_outputs=metadata):yield


@contextlib.contextmanager
def owned_resource_work(command,source,output,*,source_role='catalog',metadata_outputs=()):
    if source_role not in ('catalog','receipt'):raise ValueError('Unknown resource producer source role')
    source=checked_source_path(source);output=checked_catalog_path(output)
    metadata={Path(path)for path in metadata_outputs}
    if any(not path.is_absolute()for path in metadata):
        raise ValueError('A producer metadata output escaped its owner')
    metadata={path.resolve()for path in metadata}
    if any(not path.is_relative_to(output)for path in metadata):
        raise ValueError('A producer metadata output escaped its owner')
    marker=work_marker(output);prior=read(marker)if marker.exists()else None
    if prior and (prior.get('kind')!='CharacterResourceWork'or Path(prior['output'])!=output or
                  prior.get('sourceRole','catalog')!=source_role or work_active(prior)):
        raise ValueError('Resource output is already owned by another active producer')
    initial=work_files(output,all_created=source_role=='receipt')
    old_files={Path(row['path'])for row in prior.get('files',[])}if prior else set()
    record=dict(schemaVersion=1,kind='CharacterResourceWork',output=str(output),command=command,
        state='running',sourceRole=source_role,metadataOutputs=[str(path)for path in sorted(metadata)],
        creator=dict(pid=os.getpid(),start=process_start(os.getpid())),startedUtc=datetime.datetime.now(datetime.timezone.utc).isoformat(),
        baselineFiles=[str(path)for path in sorted(initial-old_files)],files=[row for row in prior.get('files',[])if Path(row['path']).exists()]if prior else [])
    record['sourceManifest'if source_role=='catalog'else'sourceInput']=dict(path=str(source),sha256=sha(source),role=pin_role(source)or'payload')
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
            current=work_files(output,all_created=source_role=='receipt')
            owned=(current-initial)|(current&old_files)
            with payload_hash_operation():
                files=[dict(path=str(checked_source_path(path)),sha256=sha(path,payload=path not in metadata),
                    role='metadata'if path in metadata else'payload')for path in sorted(owned)]
            record.update(state=state,completedUtc=datetime.datetime.now(datetime.timezone.utc).isoformat(),files=files)
            write(marker,record)
        except BaseException as error:
            if failure is None:raise
            failure.add_note('Resource output recording also failed: '+type(error).__name__)


def source_definition(path,expected=None,*,cache=None):
    path=checked_source_path(path)
    digest=sha(path)
    if expected is not None and digest!=expected:raise ValueError('A resource work source manifest changed')
    key=(path,digest)
    if cache is not None and key in cache:return cache[key]
    result=path,digest,catalog_payloads(read(path))
    if cache is not None:cache[key]=result
    return result


def partial_catalog(directory,source_cache=None):
    """Recover only payload names declared by the original producer's inputs."""
    directory=checked_catalog_path(directory);catalog=directory/'Catalog'
    if not catalog.is_dir():return None
    declarations=[];guards=[];explicit_payloads={};source_cache={}if source_cache is None else source_cache
    def definition(path,expected=None):
        return source_definition(Path(path),expected,cache=source_cache)
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
                    if target.suffix!='.bundle'or not target_sha or not alias.is_file()or sha(checked_source_path(alias),payload=True)!=target_sha:continue
                    if target.is_file()and sha(checked_source_path(target),payload=True)==target_sha:explicit_payloads[target]=target_sha
                declaration=definition(source.parent/RESOURCE_FILES['manifest'])
                if declaration[2].get(source.name)!=expected:continue
                declarations.append(declaration);guards.append(path)
    if not declarations:return None
    names=set.intersection(*[set(row[2])for row in declarations])
    payloads={catalog/name:sha(checked_source_path(catalog/name),payload=True)for name in names
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
            if path.stat().st_size!=row['bytes']or sha(checked_source_path(path),payload=pin_role(path)=='payload')!=row['sha256']:raise ValueError('A local published package changed')
            payloads[path]=row['sha256']
    superseded=adopted and owner['sourceSha256']not in retained_hashes and max(path.stat().st_mtime_ns for path in (marker,metadata))<adopted['modified']
    if not released and not superseded:return None
    guards=(marker,metadata,receipt)if released else(marker,metadata)
    return dict(directory=directory,owned_payloads=payloads,kind='published-package'if released else'superseded-package',
        guards=[dict(path=str(path),sha256=sha(path))for path in guards])


def walk(root,*,owned_output=False):
    for folder, children, names in os.walk(root, followlinks=False):
        if not owned_output and "SolastaUnfinishedBusiness.sln" in names:
            children[:] = []
            continue
        children[:] = [name for name in children if not (Path(folder) / name).is_junction()
                       and not (Path(folder) / name).is_symlink() and (owned_output or name not in ("Tools", "Python", "Library", ".native"))]
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


def discover(roots,*,generation_cache=None):
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
                local_payloads,local_guards=({},[])if work_marker(folder).is_file()else declared_local_payloads(folder,assembly,generation_cache=generation_cache)
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
    def entrypoint(tokens):
        # Only the interpreter's entry point executes. Later .py arguments can
        # be test patterns or editor references, not resource producers.
        values=iter(tokens[1:])
        for value in values:
            if value=='--':return next(values,None),False
            if value=='-':return None,True
            if value.startswith('-c'):return None,True
            if value.startswith('-m'):return None,False
            if value in ('-W','-X','--check-hash-based-pycs'):
                next(values,None);continue
            if value.startswith('-'):continue
            return value,False
        return None,False
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
        source,inline=entrypoint(tokens)
        if inline or source is not None and Path(source).absolute().is_relative_to(ROOT):
            active.append(row['ProcessId'])
    return active


def declared_live_inputs(catalogs,unresolved=None,*,generation_cache=None):
    """Follow retained recipes and receipts to their complete replay input closure."""
    owners=[Path(record['owner'])for record in catalogs]
    cache={}if generation_cache is None else generation_cache
    provisional={key:dict(value)if isinstance(value,dict)else value for key,value in cache.items()}
    issues=[];batch=_ReceiptBatch()
    try:
        batch.charge('generation-cache','staged',provisional)
        result=receipt_references(owners,issues,generation_cache=provisional,_batch=batch)[0]
        batch.charge('generation-cache','staged',provisional);batch.finish()
        cache.clear();cache.update(provisional)
        if unresolved is not None:unresolved.extend(issues)
        return result
    except _ReceiptBatchUnavailable as error:
        print('Retained input snapshot unavailable; repeating independent validation: '+str(error),flush=True)
        batch.close();provisional=None
        return receipt_references(owners,unresolved,generation_cache=cache)[0]
    finally:batch.close()


class _ReceiptBatchUnavailable(RuntimeError):
    """Discard provisional discovery when its bounded snapshot cannot be held."""


class _ReceiptBatchChanged(RuntimeError):
    """An external edit invalidated the complete provisional discovery."""


def _retained_size(value):
    seen=set();stack=[value];amount=0
    while stack:
        current=stack.pop();identity=id(current)
        if identity in seen:continue
        seen.add(identity);amount+=sys.getsizeof(current)
        if isinstance(current,Path):amount+=3*sys.getsizeof(str(current));stack.append(current.parts)
        elif isinstance(current,dict):stack.extend(current.keys());stack.extend(current.values())
        elif isinstance(current,(tuple,list,set,frozenset)):stack.extend(current)
    return amount


def _reference_edges(value):
    if isinstance(value,dict):
        pin=value.get('path',value.get('file'));digest=value.get('sha256')
        if isinstance(pin,str)and isinstance(digest,str)and re.fullmatch(r'[a-fA-F0-9]{64}',digest):
            yield 'pin',pin,digest,value.get('role')
        for key,child in value.items():
            if isinstance(child,(dict,list,str))and(key not in ('path','file')or not isinstance(digest,str)):
                yield from _reference_edges(child)
    elif isinstance(value,list):
        for child in value:
            if isinstance(child,(dict,list,str)):yield from _reference_edges(child)
    elif isinstance(value,str)and os.path.isabs(value):yield 'plain',value


class _ReceiptSnapshot:
    """Keep a replay header and its ordered edge stream in one cache entry."""
    def __init__(self,header,document):
        self.header=header;unique=[];order=array('I');lookup={}
        for edge in _reference_edges(document):
            index=lookup.get(edge)
            if index is None:index=len(unique);lookup[edge]=index;unique.append(edge)
            order.append(index)
        self.unique=tuple(unique);self.order=order
        self.retained_bytes=_retained_size((header,self.unique,order))
    def __iter__(self):return(self.unique[index]for index in self.order)


class _BatchReferenceVersions(ReferenceVersions):
    """Bound optional read versions without discarding final input guards."""
    def __init__(self,owner):
        super().__init__();self.owner=owner;self.order=collections.OrderedDict()
    def __setitem__(self,key,value):
        super().__setitem__(key,value);self.order[key]=None;self.order.move_to_end(key)
        while len(self)>self.owner.maximum_version_entries:
            if not self.evict(protected=key):
                raise _ReceiptBatchUnavailable('Required receipt read versions exceed their bounded cache')
        self.owner.charge('versions',key,value)
    def __getitem__(self,key):
        value=super().__getitem__(key)
        if key in self.order:self.order.move_to_end(key)
        return value
    def get(self,key,default=None):
        return self[key]if key in self else default
    def pop(self,key,default=None):
        if key not in self:return default
        value=super().pop(key);self.order.pop(key,None)
        stream=value.get('lease')if isinstance(value,dict)else None
        if stream is not None:
            try:stream.close()
            finally:self.leased_order.pop(key,None);self.held_leases-=1
        self.owner.uncharge('versions',key)
        return value
    def evict(self,protected=None):
        key=next((key for key in self.order if key!=protected and key not in self.owner.protected_versions),None)
        if key is None:return False
        self.pop(key);self.owner.evictions['versions']+=1;return True
    def close(self):
        try:super().close()
        finally:self.order.clear()


class _ReceiptBatch:
    """Share immutable syntax across seeds; role and owner traversal stay local."""
    maximum_bytes=256*1024*1024
    maximum_snapshot_bytes=32*1024*1024
    maximum_pin_entries=4096
    maximum_version_entries=4096
    def __init__(self):
        self.versions=_BatchReferenceVersions(self);self.pins=collections.OrderedDict();self.guards={};self.lookups={}
        self.snapshots=collections.OrderedDict();self.pending={};self.charges={}
        self.ledger_bytes=0;self.snapshot_bytes=0;self.active={};self.active_bytes=0
        self.evictions=collections.Counter();self.category_bytes=collections.Counter();self.peak_category_bytes=collections.Counter()
        self.peak_bytes=0;self.peak_ledger_bytes=0;self.peak_snapshot_bytes=0;self.peak_active_bytes=0
        self.guard_payload_reuses=0;self.guard_metadata_reuses=0;self.protected_versions=set()
        self.uncached_snapshots=0;self.largest_uncached_snapshot_bytes=0
    def retained_bytes(self):
        return self.ledger_bytes+self.snapshot_bytes+self.active_bytes+len(self.active)*384+self.versions.maximum_leases*8192
    def statistics(self):
        categories={name:dict(bytes=self.category_bytes[name],peakBytes=self.peak_category_bytes[name],entries=sum(token[0]==name for token in self.charges))for name in self.category_bytes.keys()|self.peak_category_bytes.keys()}
        return dict(retainedBytes=self.retained_bytes(),peakRetainedBytes=self.peak_bytes,maximumBytes=self.maximum_bytes,
            ledgerBytes=self.ledger_bytes,snapshotBytes=self.snapshot_bytes,activeBytes=self.active_bytes,
            peakLedgerBytes=self.peak_ledger_bytes,peakSnapshotBytes=self.peak_snapshot_bytes,peakActiveBytes=self.peak_active_bytes,
            categories=categories,guards=len(self.guards),lookups=len(self.lookups),pins=len(self.pins),versions=len(self.versions),
            heldLeases=self.versions.held_leases,evictions=dict(self.evictions),canonicalPayloadGuardReuses=self.guard_payload_reuses,
            canonicalMetadataGuardReuses=self.guard_metadata_reuses,uncachedSnapshots=self.uncached_snapshots,
            largestUncachedSnapshotBytes=self.largest_uncached_snapshot_bytes)
    def uncharge(self,name,key):
        amount=self.charges.pop((name,key),0);self.ledger_bytes-=amount;self.category_bytes[name]-=amount
    def evict_pin(self):
        if not self.pins:return False
        key,_=self.pins.popitem(last=False);self.uncharge('pins',key);self.evictions['pins']+=1;return True
    def evict_snapshot(self):
        if not self.snapshots:return False
        _,removed=self.snapshots.popitem(last=False);self.snapshot_bytes-=removed.retained_bytes
        self.evictions['snapshots']+=1;return True
    def check_budget(self,protected=None):
        self.peak_ledger_bytes=max(self.peak_ledger_bytes,self.ledger_bytes)
        self.peak_snapshot_bytes=max(self.peak_snapshot_bytes,self.snapshot_bytes)
        self.peak_active_bytes=max(self.peak_active_bytes,self.active_bytes)
        while self.retained_bytes()>self.maximum_bytes:
            if self.evict_pin():continue
            if self.versions.evict(protected):continue
            if self.evict_snapshot():continue
            raise _ReceiptBatchUnavailable('Required receipt snapshot exceeds its memory budget: '+json.dumps(self.statistics()))
        self.peak_bytes=max(self.peak_bytes,self.retained_bytes())
    def charge(self,name,key,value):
        if name=='versions'and key not in self.versions:return
        token=name,key;amount=_retained_size(key)+_retained_size(value)+384
        difference=amount-self.charges.get(token,0);self.ledger_bytes+=difference;self.charges[token]=amount
        self.category_bytes[name]+=difference;self.peak_category_bytes[name]=max(self.peak_category_bytes[name],self.category_bytes[name])
        self.check_budget(key if name=='versions'else None)
    @staticmethod
    def guard_key(path):
        # Match WindowsPath equality, not ntpath.normcase or Unicode casefold.
        return str(path).lower()if os.name=='nt'else str(path)
    def guard_for(self,path):
        return self.guards.get(self.guard_key(path))
    def guard(self,path,digest,identity):
        if identity is None:raise _ReceiptBatchUnavailable('A receipt input has no stable file identity')
        key=self.guard_key(path);value=digest.lower(),identity
        previous=self.guards.get(key)
        if previous is not None:
            if previous[:2]!=value:raise _ReceiptBatchChanged('A receipt input changed during discovery: '+str(path))
            return
        value=(*value,str(path))
        self.guards[key]=value;self.charge('guards',key,value)
    def lookup(self,raw,target):
        # Validate segments before normalization: a junction followed by '..'
        # must not disappear from the path checked before destructive cleanup.
        cursor=Path(raw.anchor)
        for part in raw.parts[1:]:
            if part=='..':cursor=cursor.parent;continue
            if part=='.':continue
            cursor/=part
            if cursor.exists()or cursor.is_symlink():
                info=cursor.lstat()
                if cursor.is_symlink()or getattr(info,'st_file_attributes',0)&0x400:
                    raise _ReceiptBatchChanged('A receipt lookup contains a reparse point: '+str(raw))
        if checked_source_path(Path(os.path.abspath(raw)))!=target:
            raise _ReceiptBatchChanged('A receipt lookup changed its target: '+str(raw))
    @staticmethod
    def pin_key(raw,base,digest,role,selected_role,stamp,fresh):
        path=Path(raw)
        original=path if path.is_absolute()else Path(base)/raw
        return original,(original,digest.lower(),role,selected_role,stamp,fresh)
    def snapshot_key(self,path,digest):
        if digest is None:
            guard=self.guard_for(path)
            if guard is None:raise _ReceiptBatchUnavailable('A parsed receipt has no guarded content identity')
            digest=guard[0]
        return path,digest.lower()
    @contextlib.contextmanager
    def read_lease(self,path,digest,role,*,payload,stage,reuse_guard=False):
        """Reopen an evicted version through the existing verified hash owner."""
        digest=digest.lower();key=path,digest,role;self.protected_versions.add(key)
        try:
            guard=self.guard_for(path)if reuse_guard else None
            if key not in self.versions and guard is not None and guard[0]==digest:
                self.versions[key]=dict(path=path,identity=guard[1])
            if reference_hash(path,digest,role,self.versions,payload=payload)!=digest:
                raise _ReceiptBatchChanged('A receipt input changed during '+stage+': '+str(path))
            entry=self.versions.get(key);identity=entry.get('identity')if isinstance(entry,dict)else None
            if identity is None:
                raise _ReceiptBatchUnavailable('A receipt input has no stable identity during '+stage+': '+str(path))
            self.guard(path,digest,identity)
            if leased_reference(path,digest,role,self.versions)is None:
                # A fresh SHA establishes its version, while the next call can
                # hold that exact version under the normal read-lease owner.
                if reference_hash(path,digest,role,self.versions,payload=payload)!=digest:
                    raise _ReceiptBatchChanged('A receipt input changed while reacquiring '+stage+': '+str(path))
                current=self.versions.get(key)
                if not isinstance(current,dict)or current.get('identity')!=identity:
                    raise _ReceiptBatchChanged('A receipt input identity changed while reacquiring '+stage+': '+str(path))
            if leased_reference(path,digest,role,self.versions)is None:
                raise _ReceiptBatchUnavailable('A receipt input cannot be held under a read lease during '+stage+': '+str(path))
            yield identity
        finally:self.protected_versions.discard(key)
    def pin(self,raw,base,digest,role,selected_role,stamp,fresh):
        original,key=self.pin_key(raw,base,digest,role,selected_role,stamp,fresh)
        absolute=Path(raw).is_absolute()
        if absolute and original!=Path(os.path.abspath(original)):return None
        canonical=self.lookups.get(original,original)if not absolute else original
        guard=self.guard_for(canonical)
        if selected_role in('payload','metadata')and not fresh and guard is not None and guard[0]==digest.lower():
            if selected_role=='payload':self.guard_payload_reuses+=1
            else:self.guard_metadata_reuses+=1
            return canonical,digest.lower()
        value=self.pins.get(key)
        if value is None:return None
        # Results remain private until finish() checks every path and identity.
        # The bounded lease LRU may evict this input without losing that guard.
        self.pins.move_to_end(key);return value[0],digest.lower()
    def retain_pin(self,raw,base,path,digest,role,selected_role,stamp,fresh):
        original,key=self.pin_key(raw,base,digest,role,selected_role,stamp,fresh)
        guard=self.guard_for(path)
        if original==path and selected_role in('payload','metadata')and not fresh and guard is not None and guard[0]==digest.lower():return
        if key in self.pins:
            if self.pins[key][0]!=path:raise _ReceiptBatchChanged('A receipt pin changed its target')
            return
        with self.read_lease(path,digest,role,payload=selected_role=='payload'and not fresh,stage='input pin',
                             reuse_guard=selected_role=='metadata'and not fresh)as identity:
            if original!=path and original not in self.lookups:
                self.lookup(original,path);self.lookups[original]=path;self.charge('lookups',original,path)
            if original==path and selected_role in('payload','metadata')and not fresh:return
            self.pins[key]=path,identity;self.charge('pins',key,(path,identity))
            while len(self.pins)>self.maximum_pin_entries:self.evict_pin()
            self.charge('versions',(path,digest.lower(),role),dict(path=path,identity=identity))
    def document(self,path,digest,role,*,fresh=False):
        requested=digest
        if digest is None:
            path=checked_source_path(path);guard=self.guard_for(path)
            if guard is None:
                observed=[];digest_for_read=sha(path,identity_out=observed)
                if not observed:raise _ReceiptBatchUnavailable('A receipt owner has no stable file identity')
                identity=observed[0];self.guard(path,digest_for_read,identity)
            else:digest_for_read,identity=guard[:2]
            if(path,digest_for_read,role)not in self.versions:
                self.versions[path,digest_for_read,role]=dict(path=path,identity=identity)
                self.charge('versions',(path,digest_for_read,role),dict(path=path,identity=identity))
        else:digest_for_read=digest.lower()
        key=self.snapshot_key(path,digest_for_read);snapshot=self.snapshots.get(key)
        pending=self.pending.get(key)
        if snapshot is None and isinstance(pending,_ReceiptSnapshot):snapshot=pending
        if snapshot is not None:self.pend_snapshot(key,snapshot)
        try:
            if not fresh and requested is not None and snapshot is not None and(self.guard_for(path)or(None,))[0]==digest_for_read:
                if key in self.snapshots:self.snapshots.move_to_end(key)
                return snapshot.header
            with self.read_lease(path,digest_for_read,role,payload=False,stage='receipt read',reuse_guard=not fresh):
                if snapshot is not None:
                    if key in self.snapshots:self.snapshots.move_to_end(key)
                    return snapshot.header
                document=read(path);self.pending[key]=document;return document
        except BaseException:
            self.clear_pending(key);raise
    def edges(self,path,digest,role,document):
        key=self.snapshot_key(path,digest);pending=self.pending.pop(key,None)
        held=isinstance(pending,_ReceiptSnapshot)
        snapshot=pending if held else self.snapshots.get(key)
        if snapshot is None:
            original=pending if pending is not None else document
            # Replay only the fields read by the existing generation normalizer.
            # Legacy documents keep their original protocol and are revalidated;
            # the original normalized ordered stream belongs to the snapshot.
            special=isinstance(document,dict)and document.get('kind')in('CharacterResourceGeneration','CharacterResourceWork')
            if special and document.get('kind')=='CharacterResourceGeneration'and original.get('kind')!='CharacterResourceWork':
                header={name:original[name]for name in('kind','schemaVersion','state','outputs','inputs',
                    'generatorPath','generatorSha256','sourceInput','sourceInputSha256',
                    'sourceGeneration','sourceGenerationSha256')if name in original}
                if 'rows'in original:
                    fields=('generatedPacket','generatedSha256','bindingPath','bindingSha256',
                        'nativePacketPath','nativePacketSha256','packetPath','packetSha256')
                    header['rows']=[{name:row[name]for name in fields if name in row}if isinstance(row,dict)else row for row in original['rows']]
            else:header=original if special else({name:original[name]for name in('generatorPath','generatorSha256')if name in original}if isinstance(original,dict)else{})
            snapshot=_ReceiptSnapshot(header,document)
            snapshot.retained_bytes+=_retained_size(key)+384
            if snapshot.retained_bytes<=self.maximum_snapshot_bytes:
                self.snapshots[key]=snapshot;self.snapshot_bytes+=snapshot.retained_bytes
                while self.snapshot_bytes>self.maximum_snapshot_bytes:self.evict_snapshot()
            else:
                # A large current receipt still has its normal input lease and
                # final guards. Only optional reuse is omitted; do not discard
                # the verified batch or enlarge either memory budget.
                self.uncached_snapshots+=1
                if snapshot.retained_bytes>self.largest_uncached_snapshot_bytes:
                    self.largest_uncached_snapshot_bytes=snapshot.retained_bytes
                    print('Processing a receipt outside the optional snapshot cache: '+json.dumps(
                        dict(path=str(path),retainedBytes=snapshot.retained_bytes,cacheBytes=self.maximum_snapshot_bytes)),flush=True)
        if not held:self.hold_snapshot(snapshot)
        return snapshot
    def hold_snapshot(self,snapshot):
        state=self.active.get(id(snapshot))
        if state is None:self.active[id(snapshot)]=[1,snapshot.retained_bytes];self.active_bytes+=snapshot.retained_bytes
        else:state[0]+=1
        try:self.check_budget()
        except BaseException:self.release(snapshot);raise
    def pend_snapshot(self,key,snapshot):
        previous=self.pending.get(key)
        if previous is snapshot:return
        if isinstance(previous,_ReceiptSnapshot):self.pending.pop(key);self.release(previous)
        self.hold_snapshot(snapshot);self.pending[key]=snapshot
    def clear_pending(self,key=None):
        values=[self.pending.pop(key,None)]if key is not None else list(self.pending.values())
        if key is None:self.pending.clear()
        for value in values:
            if isinstance(value,_ReceiptSnapshot):self.release(value)
    def release(self,snapshot):
        state=self.active[id(snapshot)];state[0]-=1
        if state[0]==0:self.active_bytes-=state[1];self.active.pop(id(snapshot))
    def finish(self):
        for raw,target in self.lookups.items():self.lookup(raw,target)
        for _,expected,original in self.guards.values():
            path=checked_source_path(Path(original));stream=_open_read_lease(path)
            if stream is None:
                with path.open('rb')as probe:identity=file_hash_identity(probe)
                if identity is not None and identity!=expected:raise _ReceiptBatchChanged('A receipt input changed during discovery: '+str(path))
                raise _ReceiptBatchUnavailable('A receipt snapshot cannot be coherently checked')
            try:identity=file_hash_identity(stream)
            finally:stream.close()
            if identity is None:raise _ReceiptBatchUnavailable('A receipt input has no stable final identity')
            if identity!=expected:raise _ReceiptBatchChanged('A receipt input changed during discovery: '+str(path))
    def close(self):
        try:self.versions.close()
        finally:
            self.clear_pending()
            for mapping in(self.pins,self.guards,self.lookups,self.snapshots,self.pending,self.charges,self.active,self.protected_versions):mapping.clear()
            self.ledger_bytes=self.snapshot_bytes=self.active_bytes=0
            self.category_bytes.clear()


def receipt_references(owners,unresolved=None,*,generation_cache=None,strict=False,_batch=None):
    """Resolve typed payload leaves and hash-pinned reference-bearing receipts."""
    owners=set(owners);result=set();visited=set(owners);plain=set();hashes={};exists={};reported=set();roles={};generations={};fresh_inputs=set();reference_versions=ReferenceVersions()
    generation_cache={}if generation_cache is None else generation_cache
    unresolved=[]if unresolved is None else unresolved
    completed=set();leased_edges=collections.OrderedDict()
    # Match native Path equality and component boundaries without constructing
    # every ancestor for each edge. This is lexical only; FS guards stay below.
    owned_roots=tuple((key,key if key.endswith(os.sep)else key+os.sep,_batch.guard_key(root.anchor))
        for root in(ARTIFACTS,WORKSPACE)for key in(_batch.guard_key(root),))if _batch is not None else()
    if _batch is not None:reference_versions=_batch.versions
    def classification(path,digest):
        authority=generation_cache.get(digest.lower(),{})
        return roles.get(path),bool(authority.get('packetValidation')),bool(authority.get('typedPayloadAuthority'))
    def retain_edge(raw_key,lookup_path,path,digest,role,stamp):
        if stamp!=classification(lookup_path,digest):return
        key=path,digest.lower(),role;entry=reference_versions.get(key)
        lease=entry.get('lease')if isinstance(entry,dict)else None
        if lease is None or lease.closed:return
        leased_edges[raw_key]=(lookup_path,stamp,key,entry,lease)
        leased_edges.move_to_end(raw_key)
        while len(leased_edges)>reference_versions.maximum_leases:leased_edges.popitem(last=False)
    def issue(path,reason):
        if (path,reason)not in reported:
            reported.add((path,reason));unresolved.append(dict(path=str(path),reason=reason))
    def local(value,base,explicit=False):
        if not isinstance(value,str):return None
        # Relative asset names inside a bundle are not filesystem inputs. Relative
        # files are followed only through an explicit hash-pinned declaration.
        if not os.path.isabs(value)and not explicit:return None
        path=Path(value)
        if not path.is_absolute():
            path=Path(os.path.abspath(base/path))
        if _batch is not None and path.is_absolute():
            key=_batch.guard_key(path);anchor=_batch.guard_key(path.anchor)
            return path if any(anchor==root_anchor and(key==root or key.startswith(prefix))for root,prefix,root_anchor in owned_roots)else None
        return path if path.is_absolute()and any(path.is_relative_to(root)for root in (ARTIFACTS,WORKSPACE))else None
    def source(value,base,digest=None,explicit=False,role=None):
        raw_key=(value,base,digest,role)if explicit else None
        cached=leased_edges.get(raw_key)if explicit else None
        if cached is not None:
            lookup_path,stamp,key,entry,lease=cached
            # The completed edge remains fixed only under its original read lease
            # and classification. Eviction or a new declaration takes the full path.
            if reference_versions.get(key)is entry and entry.get('lease')is lease and not lease.closed and stamp==classification(lookup_path,digest):
                reference_versions.leased_order.move_to_end(key);leased_edges.move_to_end(raw_key)
                scope=_hash_scope.get()
                if scope is not None:scope['inputLeaseReuses']+=1;hash_progress(scope)
                return
            leased_edges.pop(raw_key,None)
        path=local(value,base,explicit)
        if path is None:return
        lookup_path=path;stamp=classification(path,digest)if explicit else None
        owner_pin=roles.get(path)
        inherited_role=owner_pin[0]if owner_pin and(digest is None or owner_pin[1]==digest.lower())else None
        selected_role=role or inherited_role or pin_role(path,dict(sha256=digest)if digest else None,generation_cache=generation_cache)or('payload'if explicit else None)
        payload=selected_role=='payload';metadata=selected_role=='metadata'
        if not payload and not metadata:return
        if path in owners:return
        if metadata and not explicit and path in plain:return
        cached_pin=_batch.pin(value,base,digest,role,selected_role,stamp,lookup_path in fresh_inputs)if explicit and _batch is not None else None
        if cached_pin is not None:exists[path]=True
        if path not in exists:exists[path]=path.is_file()
        if not exists[path]:
            # A relative file in a copied receipt may name its output in another
            # catalog. Only a pinned absolute location establishes a missing input.
            if explicit and os.path.isabs(value):
                issue(path,'missing-retained-input')
            return
        if explicit:
            path,actual=cached_pin if cached_pin is not None else checked_reference_hash(path,digest,role,reference_versions,payload=payload and path not in fresh_inputs)
            hashes[path]=actual.lower()
            if hashes[path]!=digest.lower():
                result.add(path.parent);issue(path,'changed-retained-input'if payload else'changed-retained-recipe');return
            if _batch is not None:_batch.retain_pin(value,base,path,digest,role,selected_role,stamp,lookup_path in fresh_inputs)
        if payload:
            if explicit and(role=='payload'or inherited_role=='payload'):
                generation_cache.setdefault(digest.lower(),{})['typedPayloadAuthority']=True
            visited.add(path);result.add(path)
            if explicit:retain_edge(raw_key,lookup_path,path,digest,role,stamp)
            return
        # Unpinned metadata paths can be output locations or diagnostic links.
        # Keep the document, but only a declared immutable input grants traversal.
        if not explicit:plain.add(path);result.add(path);return
        version=(path,hashes[path],role)
        if version in visited:
            if version in completed:retain_edge(raw_key,lookup_path,path,digest,role,stamp)
            return
        visited.add(version)
        result.add(path)
        try:
            visit_document(path,declared_role=role,fresh=lookup_path in fresh_inputs)
            completed.add(version);retain_edge(raw_key,lookup_path,path,digest,role,stamp)
        except (OSError,ValueError,KeyError,TypeError):
            if strict:raise
            result.add(path.parent);issue(path,'unverified-retained-recipe')
    def visit_document(path,declared_role=None,*,fresh=False):
        document=read(path)if _batch is None else _batch.document(path,hashes.get(path),declared_role,fresh=fresh)
        key=_batch.snapshot_key(path,hashes.get(path))if _batch is not None else None
        try:
            if isinstance(document,dict)and isinstance(document.get('generatorPath'),str)and 'generatorSha256'in document:
                fresh_inputs.add(Path(document['generatorPath']))
            if isinstance(document,dict)and all(key in document for key in('vertexCount','subMeshes','sourceFingerprint')):
                digest=hashes.get(path)or sha(path)
                numeric_payload(path,document,digest,generation_cache,declared_role=declared_role)
                return
            document=normalize_generation(path,document,generation_cache)
            if isinstance(document,dict)and document.get('kind')=='CharacterResourceGeneration':
                generations[path]={name:document[name]for name in('kind','schemaVersion','state','outputs')}
                for pin in document['outputs']:
                    target=local(pin['path'],path.parent,True)
                    if target is not None:
                        role=pin_role(target,pin)
                        declaration=role,pin['sha256'].lower()
                        if target in roles and roles[target]!=declaration:raise ValueError('Conflicting producer output roles or hashes')
                        roles[target]=declaration
            if _batch is None:visit_edges(_reference_edges(document),path.parent)
            else:
                edges=_batch.edges(path,hashes.get(path),declared_role,document)
                document=None
                try:visit_edges(edges,path.parent)
                finally:_batch.release(edges)
        finally:
            if _batch is not None:_batch.clear_pending(key)
    def visit_edges(edges,base):
        for edge in edges:
            if edge[0]=='pin':
                _,pin,digest,role=edge
                if role is not None:pin_role(Path(pin),dict(role=role))
                source(pin,base,digest,True,role)
            else:source(edge[1],base)
    try:
        for owner in sorted(owners):
            try:visit_document(owner)
            except (OSError,ValueError,KeyError,TypeError):
                if strict:raise
                result.add(owner.parent);issue(owner,'unverified-retained-recipe')
        return result,generations
    finally:
        if _batch is None:reference_versions.close()
        else:_batch.clear_pending()


def work_source_inputs(record,*,generation_cache=None,unresolved=None,_batch=None):
    """An unfinished or newer producer keeps the source closure it declared."""
    pin=record.get('sourceManifest')
    if pin:
        path,_,payloads=source_definition(Path(pin['path']),pin['sha256'])
        return {path.parent/name for name in payloads
                if any((path.parent/name).is_relative_to(root)for root in (ARTIFACTS,WORKSPACE))}
    pin=record.get('sourceInput')
    if not pin:return set()
    path=checked_source_path(Path(pin['path']));role=pin.get('role')
    payload=pin_role(path,pin)=='payload'
    if _batch is None:actual=sha(path)
    else:
        expected=pin['sha256'].lower();guard=_batch.guard_for(path)
        if not payload and guard is not None and guard[0]==expected:
            with _batch.read_lease(path,expected,role,payload=False,stage='work receipt input',reuse_guard=True):
                actual=expected
        else:
            path,actual=checked_reference_hash(path,pin['sha256'],role,_batch.versions,payload=payload)
        if actual==pin['sha256'].lower():
            key=path,pin['sha256'].lower(),role;version=_batch.versions.get(key)
            if not isinstance(version,dict):raise _ReceiptBatchUnavailable('A work input has no stable file identity')
            _batch.guard(path,actual,version['identity']);_batch.charge('versions',key,version)
    if actual!=pin['sha256'].lower():raise ValueError('A resource producer input changed')
    # Proof inputs can live in source directories; storage references only
    # protect resources inside the cleanup owner's existing workspaces.
    retained={path}if any(path.is_relative_to(root)for root in (ARTIFACTS,WORKSPACE))else set()
    if payload:return retained
    return retained|receipt_references([path],unresolved,generation_cache=generation_cache,_batch=_batch)[0]


def retained_work(current,directory,modified):
    directories=current.get('retainedDirectories')
    if directories is None:return modified>=current['modified']
    return any(directory.is_relative_to(folder)for folder in directories)


def _discover_registered_work(roots,current,retained_hashes,*,generation_cache=None,_batch=None):
    # This first walk only reads ownership; the later walk owns marker creation.
    records=[];retained=[];packages=[];registered=set();active=producer_processes();live=set()
    input_owners=dict(current,retainedDirectories=set())if current.get('retainedInputsSnapshot')else current
    for root in roots:
        for folder,names in walk(root):
            if folder.name=='.resource-work':
                for name in names:
                    if not name.endswith('.json'):continue
                    marker=folder/name;snapshot=None
                    try:
                        marker=checked_source_path(marker)
                        if _batch is None:document=read(marker)
                        else:
                            document=_batch.document(marker,None,'metadata')
                            snapshot=_batch.edges(marker,None,'metadata',document)
                        if document.get('kind')!='CharacterResourceWork'or document.get('schemaVersion')!=1:raise ValueError('Unknown resource work ownership')
                        output=checked_catalog_path(document['output'])
                        if marker!=work_marker(output):raise ValueError('Resource work identity does not match its output')
                        registered.add(output)
                        if work_active(document):
                            issues=[]
                            live.update(work_source_inputs(document,generation_cache=generation_cache,unresolved=issues,_batch=_batch))
                            retained.extend(issues)
                            if _batch is not None:_batch.charge('generation-cache','staged',generation_cache)
                            retained.append(dict(path=str(output),reason='active-resource-producer'));continue
                        payloads={};metadata_guards=[];all_created=document.get('sourceRole')=='receipt'
                        metadata={Path(value)for value in document.get('metadataOutputs',[])}
                        if any(not path.is_absolute()or not path.resolve().is_relative_to(output)for path in metadata):raise ValueError('A resource metadata output escaped its owner')
                        for pin in document['files']:
                            path=Path(pin['path'])
                            role=pin_role(path,pin)
                            if not path.is_absolute()or not path.is_relative_to(output)or role is None:raise ValueError('A resource work payload escaped its owner')
                            if path.exists():
                                actual=checked_source_path(path)
                                if not actual.is_relative_to(output):raise ValueError('A resource work payload escaped its owner')
                                if sha(actual,payload=role=='payload')!=pin['sha256']:raise ValueError('A resource work payload changed')
                                if role=='payload':payloads[path]=pin['sha256']
                                else:metadata_guards.append(pin)
                        if document.get('state')=='running'and 'baselineFiles'in document:
                            baseline={Path(value)for value in document['baselineFiles']}
                            if any(not path.is_absolute()or not path.resolve().is_relative_to(output)or(not all_created and not data_payload(path))for path in baseline):raise ValueError('An interrupted resource baseline escaped its owner')
                            for path in work_files(output,all_created=all_created)-baseline:
                                digest=sha(checked_source_path(path),payload=path not in metadata)
                                if path in metadata:metadata_guards.append(dict(path=str(path),sha256=digest,role='metadata'))
                                else:payloads[path]=digest
                        if retained_work(input_owners,output,document.get('modifiedNs',marker.stat().st_mtime_ns))or(
                                'retainedDirectories'in input_owners and any(retained_work(input_owners,path.parent,0)for path in payloads)):
                            issues=[]
                            live.update(work_source_inputs(document,generation_cache=generation_cache,unresolved=issues,_batch=_batch))
                            retained.extend(issues)
                            if _batch is not None:_batch.charge('generation-cache','staged',generation_cache)
                        if not payloads:continue
                        marker_sha=sha(marker)if _batch is None else _batch.guard_for(marker)[0]
                        guards=[dict(path=str(marker),sha256=marker_sha),*metadata_guards]+document.get('guards',[])
                        records.append(dict(root=root,directory=output,sha256=None,accepted=False,
                            modified=document.get('modifiedNs',marker.stat().st_mtime_ns),owned_payloads=payloads,guards=guards,kind='work'))
                    except (OSError,ValueError,KeyError,TypeError)as error:retained.append(dict(path=str(marker),reason=str(error)))
                    finally:
                        if snapshot is not None:_batch.release(snapshot)
            if RESOURCE_FILES['packageInputs']in names:
                try:
                    package=published_package(folder,current,retained_hashes)
                    if package and package['owned_payloads']:
                        if active:retained.append(dict(path=str(folder),reason='active-resource-producer'))
                        else:package['root']=root;packages.append(package)
                except (OSError,ValueError,KeyError,TypeError)as error:retained.append(dict(path=str(folder),reason=str(error)))
    return records,retained,packages,registered,live


def discover_work(roots,current,known,apply,retained_hashes=(),*,generation_cache=None):
    cache={}if generation_cache is None else generation_cache
    provisional={key:dict(value)if isinstance(value,dict)else value for key,value in cache.items()}
    batch=_ReceiptBatch()
    try:
        try:
            batch.charge('generation-cache','staged',provisional)
            result=_discover_registered_work(roots,current,retained_hashes,generation_cache=provisional,_batch=batch)
            batch.charge('generation-cache','staged',provisional);batch.finish()
            cache.clear();cache.update(provisional)
        except _ReceiptBatchUnavailable as error:
            print('Work receipt snapshot unavailable; repeating independent validation: '+str(error),flush=True)
            batch.close();batch=None;provisional=None
            result=_discover_registered_work(roots,current,retained_hashes,generation_cache=cache)
        records,retained,packages,registered,live=result
        active=producer_processes();source_cache={}
        for root in roots:
            for folder,_ in walk(root):
                if folder in known or folder in registered or not(folder/'Catalog').is_dir()or not has_payloads(folder):continue
                try:
                    partial=partial_catalog(folder,source_cache)
                    if partial is None:continue
                    if active or retained_work(current,folder,partial['modified']):
                        retained.append(dict(path=str(folder),reason='active-or-retained-resource-work'));continue
                    partial['root']=root;partial['previewOnly']=not apply
                    if apply:
                        marker=work_marker(folder)
                        if batch is not None:
                            # A failed marker can be guarded without being registered.
                            # Own writes must never replace an earlier input's identity.
                            conflict=batch.guard_for(marker)is not None or any(key[0]==marker and entry.get('lease')is not None and
                                not entry['lease'].closed for key,entry in batch.versions.items())
                            if conflict:batch.close();batch=None
                            else:
                                for key in list(batch.versions):batch.versions.pop(key,None)
                        document=dict(schemaVersion=1,kind='CharacterResourceWork',output=str(folder),command='retained-producer-inputs',
                            state='superseded',creator=None,sourceManifest=partial['sourceManifest'],modifiedNs=partial['modified'],
                            guards=partial['guards'],files=[dict(path=str(path),sha256=digest)for path,digest in partial['owned_payloads'].items()])
                        write(marker,document);partial['guards'].append(dict(path=str(marker),sha256=sha(marker)))
                    records.append(partial)
                except (OSError,ValueError,KeyError,TypeError)as error:retained.append(dict(path=str(folder),reason=str(error)))
        if 'retainedDirectories'in current:
            # Retirement uses the validated output declarations above. Replaying
            # discarded owners' input histories does not establish more ownership.
            # Required replay inputs are resolved separately from retained owners.
            return records,packages,retained,live
        generation_directories=known|registered
        if batch is None:
            generation_records,generation_retained=discover_generations(generation_directories,roots,generation_cache=generation_cache)
        else:
            # Keep the work checkpoint's cache semantics, including a caller that
            # did not request a cache shared with the subsequent generation pass.
            stage_cache={}if generation_cache is None else cache
            provisional={key:dict(value)if isinstance(value,dict)else value for key,value in stage_cache.items()}
            try:
                batch.charge('generation-cache','staged',provisional)
                generation_records,generation_retained=_discover_generations(generation_directories,roots,generation_cache=provisional,_batch=batch)
                batch.finish();stage_cache.clear();stage_cache.update(provisional)
            except _ReceiptBatchUnavailable as error:
                print('Shared work/generation snapshot unavailable; repeating independent generation validation: '+str(error),flush=True)
                batch.close();batch=None;provisional=None
                generation_records,generation_retained=discover_generations(generation_directories,roots,generation_cache=generation_cache)
        records.extend(generation_records);retained.extend(generation_retained)
        return records,packages,retained,live
    finally:
        if batch is not None:batch.close()


def discover_generations(directories,roots,*,generation_cache=None):
    """Publish a coherent batch only after every deferred input guard passes."""
    cache={}if generation_cache is None else generation_cache
    provisional={key:dict(value)if isinstance(value,dict)else value for key,value in cache.items()}
    batch=_ReceiptBatch()
    try:
        batch.charge('generation-cache','staged',provisional)
        result=_discover_generations(directories,roots,generation_cache=provisional,_batch=batch)
        batch.finish()
        cache.clear();cache.update(provisional)
        return result
    except _ReceiptBatchUnavailable as error:
        # Unsupported file identity/read sharing or an exhausted optimization
        # budget retains the original independent, freshly validated traversal.
        print('Receipt snapshot unavailable; repeating independent validation: '+str(error),flush=True)
        batch.close();provisional=None
        return _discover_generations(directories,roots,generation_cache=cache)
    finally:batch.close()


def _discover_generations(directories,roots,*,generation_cache=None,_batch=None):
    """Recover only producer records reached through pinned resource recipes."""
    records={};retained=[];cache={}if generation_cache is None else generation_cache
    seeds={directory/'ResourceAssembly.json'for directory in directories if(directory/'ResourceAssembly.json').is_file()}
    for directory in directories:
        if not any(directory.is_relative_to(root)and directory!=root for root in(ARTIFACTS,WORKSPACE)):continue
        marker=work_marker(directory)
        if marker.is_file():seeds.add(marker)
    for seed in sorted(seeds):
        try:
            _,generations=receipt_references([seed],generation_cache=cache,strict=True,_batch=_batch)
            if _batch is not None:_batch.charge('generation-cache','staged',cache)
            for owner,document in generations.items():
                directory=checked_catalog_path(owner.parent)
                root=next((root for root in roots if directory.is_relative_to(root)),None)
                if root is None:continue
                payloads={};guards=[dict(path=str(owner),sha256=sha(owner))]
                for pin in document['outputs']:
                    path=Path(pin['path'])
                    if not path.is_absolute()or not path.is_relative_to(directory):raise ValueError('A typed generation output escaped its owner')
                    if not path.exists():continue
                    path=checked_source_path(path)
                    if not path.is_relative_to(directory):raise ValueError('A typed generation output escaped its owner')
                    if sha(path,payload=pin['role']=='payload')!=pin['sha256']:raise ValueError('A generation output changed')
                    if pin['role']=='payload':payloads[path]=pin['sha256']
                    else:guards.append(pin)
                if payloads:
                    records[owner]=dict(root=root,directory=directory,owner=owner,sha256=None,accepted=False,
                        modified=owner.stat().st_mtime_ns,owned_payloads=payloads,guards=guards,kind='generation')
        except (OSError,ValueError,KeyError,TypeError)as error:
            retained.append(dict(path=str(seed),reason=str(error)))
    return list(records.values()),retained


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
    old = [row for row in catalogs if row["sha256"] not in keep]
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


def plan(output, apply=False, keep_accepted=2,retain_directories=()):
    with payload_hash_operation()as scope:
        return _plan(output,apply,keep_accepted,scope,retain_directories)


def _plan(output,apply,keep_accepted,scope,retain_directories=()):
    output = output.resolve()
    if not output.is_relative_to(ARTIFACTS):
        raise ValueError("Storage evidence belongs below game-test/artifacts")
    roots = (ARTIFACTS, WORKSPACE)
    requested=set()
    for directory in retain_directories:
        directory=checked_catalog_path(directory)
        if not any(directory!=root and directory.is_relative_to(root)for root in roots):
            raise ValueError('A retained work directory must belong to an owned tool workspace')
        if not directory.is_dir():raise ValueError('A retained work directory does not exist: '+str(directory))
        requested.add(directory)
    active=producer_processes()
    if active:
        record=dict(schemaVersion=1,status='retained-active-resource-producer',activeProcesses=active,plans=[],hashing=hash_statistics(scope))
        write(output/'ResourceLifecycle.json',record);print(json.dumps(record));return record
    generation_cache={}
    hash_progress(scope,stage='catalog-owners',force=True)
    catalogs, retained = discover(roots,generation_cache=generation_cache)
    public_sha = sha(PUBLIC)
    try:
        current, keep, old = select_retired(catalogs, public_sha, keep_accepted)
    except ValueError as error:
        record = {"schemaVersion": 1, "status": "retained-no-adopted-owner", "reason": str(error),
                  "plans": [], "completedCatalogs": len(catalogs), "unverifiedRetained": retained,
                  "hashing":hash_statistics(scope)}
        write(output / "ResourceLifecycle.json", record)
        print(json.dumps(record))
        return record
    source_hashes = adopted_source_hashes(current)
    current, keep, old = select_retired(catalogs, public_sha, keep_accepted, source_hashes)
    protected={row['directory']for row in catalogs if row['sha256']in keep}
    protected.update(requested)
    if source_hashes:
        protected.add(checked_catalog_path(Path(read(current['directory']/'Catalog/CatalogQuality.json')['sourceCatalog'])).parent)
    current=dict(current,retainedDirectories=protected)
    old=[row for row in old if not retained_work(current,row['directory'],row['modified'])]
    hash_progress(scope,stage='work-and-generation-closure',force=True)
    work,packages,work_retained,work_inputs=discover_work(roots,current,{row['directory']for row in catalogs}|protected,apply,keep,generation_cache=generation_cache)
    # A completed catalog keeps its source/geometry ownership; the work record
    # adds other explicitly generated payloads without replacing that authority.
    by_directory={row['directory']:row for row in catalogs}
    for row in work:
        if row['directory']in by_directory:
            known=by_directory[row['directory']];known['owned_payloads'].update(row['owned_payloads']);known['guards']+=row['guards']
        elif not retained_work(current,row['directory'],row['modified']):old.append(row)
    retired=[*old,*packages]
    unresolved=[];live=set(work_inputs)
    hash_progress(scope,stage='retained-input-closure',force=True)
    live.update(declared_live_inputs([row for row in catalogs if row['directory']in protected],unresolved,generation_cache=generation_cache))
    live_directories={path for path in live if path.is_dir()}
    retained_roots=live_directories|protected
    obsolete = {path for row in retired for path in row["owned_payloads"]
                if path not in live and not any(parent in retained_roots for parent in (path,*path.parents))}
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
    hash_progress(scope,stage='payload-retirement-plans',force=True)
    for index, root in enumerate(roots):
        owners = [row for row in retired if row["root"] == root]
        if not owners:
            continue
        destination = output / f"ResourcePayloads-{index}.json"
        payloads={path:digest for row in owners for path,digest in row['owned_payloads'].items()if path in obsolete}
        guards={pin['path']:pin for row in owners for pin in row.get('guards',[])}
        guards[str(PUBLIC)]=dict(path=str(PUBLIC),sha256=public_sha)
        for path,digest in payloads.items():
            guard=guards.get(str(path))
            if guard is None:continue
            if guard['sha256'].lower()!=digest.lower():raise ValueError('A retired output has conflicting producer input and output hashes: '+str(path))
            # Another discarded producer may have consumed this same output.
            # Its payload check already verifies the identical bytes before removal.
            del guards[str(path)]
        result = create_declared_payload_plan(payloads,preview_reference,destination,storage_root=root,guard_inputs=guards.values())
        if any(row.get('previewOnly')for row in owners):
            result['previewOnly']=True;write(destination,result)
        plans.append({"root": str(root), "plan": str(destination), "files": len(result["files"]),
                      "logicalBytes": result["logicalBytes"]})
    record = {"schemaVersion": 1, "status": "planned", "adopted": str(current["directory"]),
              "adoptedManifestSha256": public_sha, "keepAcceptedCatalogs": keep_accepted,
              "retainedCatalogHashes": sorted(keep), "completedCatalogs": len(catalogs),
              "retainedSourceCatalogHashes": sorted(source_hashes),
              "explicitRetainedDirectories":sorted(map(str,requested)),
              "unresolvedRetainedInputs":unresolved,
              "retiredCatalogs": [str(row["directory"]) for row in old], "unverifiedRetained": retained,
              "retiredPublishedPackages": [str(row['directory'])for row in packages if row['kind']=='published-package'],
              "retiredSupersededPackages": [str(row['directory'])for row in packages if row['kind']=='superseded-package'],"retainedWork":work_retained,
              "referencesBeforeSha256": original_sha, "referencesBeforeCount": len(before["paths"]),
              "referencesAfterCount": len(after["paths"]), "plans": plans,
              "scope": "Declared inactive outputs outside adopted, previous and explicitly retained work. Replay inputs are retained from those owners; creation time does not keep disposable candidates."}
    if apply:
        if sha(PUBLIC) != public_sha or (sha(references_path) if references_path.exists() else None) != original_sha:
            raise ValueError("Product or references changed during storage planning")
        write(output / "ReferencesBefore.json", before)
        # Replace only the references proved obsolete by completed catalog ownership.
        temporary = references_path.with_suffix(".new.json")
        write(temporary, after)
        os.replace(temporary, references_path)
        record["status"] = "references-closed-payloads-planned"
    hash_progress(scope,stage='completed',force=True)
    record['hashing']=hash_statistics(scope)
    write(output / "ResourceLifecycle.json", record)
    print(json.dumps({k: record[k] for k in ("status", "completedCatalogs", "referencesBeforeCount", "referencesAfterCount", "plans")}))
    return record


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--apply", action="store_true")
    parser.add_argument("--keep-accepted", type=int, default=2, choices=range(1, 11))
    parser.add_argument('--inventory-only',action='store_true')
    parser.add_argument('--retain-directory',type=Path,action='append',default=[])
    args = parser.parse_args()
    if args.inventory_only:
        result=inventory_tools(ROOT/'tools');write(args.output/'StorageInventory.json',result)
        print(json.dumps({key:value for key,value in result.items()if key not in ('groups','extensions','skippedReparsePoints')}));return
    plan(args.output,args.apply,args.keep_accepted,args.retain_directory)


if __name__ == "__main__":
    main()
