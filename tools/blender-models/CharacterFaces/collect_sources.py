"""Validate immutable native source packets and build a provenance-preserving census."""
from __future__ import annotations
import argparse, copy, gzip, hashlib, json, math, re, struct, sys
from collections import Counter
from pathlib import Path
from typing import Any

REPO = Path(__file__).resolve().parents[3]
DEFAULT_EXPORTER = REPO / 'tools/game-test/Scenarios/FacePresentation/ExportFaceSources.cs'
ALLOWED_OUTPUT = REPO / 'tools/asset-authoring/workspace/FaceRefinement/NativeSources'

class SourceError(ValueError): pass

def need(value, message):
    if not value: raise SourceError(message)

def digest(data): return hashlib.sha256(data).hexdigest()
def read_json(data): return json.loads(data.decode('utf-8-sig'))
def integer(value): return type(value) is int and -2147483648 <= value <= 2147483647

def number(value):
    need(type(value) in (int, float) and math.isfinite(value), 'Nonfinite or nonnumeric float channel.')
    try: value = struct.unpack('<f', struct.pack('<f', value))[0]
    except (OverflowError, struct.error): raise SourceError('Float channel exceeds float32.')
    need(math.isfinite(value), 'Float32 conversion is nonfinite.')
    return 0.0 if value == 0 else value

def floats(values, count=None, name='channel'):
    need(type(values) is list and (count is None or len(values) == count), f'Invalid {name} length/type.')
    # Validate once; canonical encoding independently rounds every component to float32.
    for value in values: number(value)
    return values

def ints(values, count=None):
    need(type(values) is list and (count is None or len(values) == count) and all(integer(v) for v in values), 'Invalid int channel length/type.')
    return values

def fingerprint(packet):
    out = bytearray()
    def i(value): out.extend(struct.pack('<i', value))
    def f(value): out.extend(struct.pack('<f', number(value)))
    def fs(values):
        i(0 if values is None else len(values))
        if values is not None:
            for value in values: f(value)
    def ii(values):
        i(0 if values is None else len(values))
        if values is not None:
            for value in values: i(value)
    def text(value):
        data = value.encode('utf-8'); length = len(data)
        while length >= 128: out.append((length & 127) | 128); length >>= 7
        out.append(length); out.extend(data)
    text('Solasta.MeshSource.1'); i(packet['vertexCount']); i(packet['indexFormat'])
    for key in ('vertices', 'normals', 'tangents', 'colors', 'bindPoses', 'weights'): fs(packet[key])
    ii(packet['boneIndices']); i(len(packet['subMeshes']))
    for indices in packet['subMeshes']: ii(indices)
    i(len(packet['uv']))
    for uv in packet['uv']:
        i(uv['channel']); i(uv['dimension']); i(packet['vertexCount'] * uv['dimension'])
        for vertex in range(packet['vertexCount']):
            for component in range(uv['dimension']): f(uv['values'][vertex * 4 + component])
    i(len(packet['shapes']))
    for shape in packet['shapes']:
        text(shape['name']); i(len(shape['frames']))
        for frame in shape['frames']:
            f(frame['weight'])
            for key in ('vertices', 'normals', 'tangents'): fs(frame[key])
    return digest(out), hashlib.sha512(out).hexdigest(), len(out)

def validate_packet(p):
    required = ('vertexCount','indexFormat','vertices','normals','tangents','colors','bindPoses','weights','boneIndices','subMeshes','uv','shapes','sourceFingerprint')
    need(isinstance(p,dict) and all(k in p for k in required), 'Missing mesh channel or source fingerprint.')
    n = p['vertexCount']; need(integer(n) and n > 0 and integer(p['indexFormat']) and p['indexFormat'] in (0,1), 'Invalid vertex count/index format.')
    need(type(p.get('name')) is str and type(p.get('nativeReadable')) is bool, 'Missing source provenance fields.')
    floats(p.get('bounds'),6,'source bounds')
    floats(p['vertices'], n*3, 'vertices'); floats(p['normals'], n*3, 'normals')
    for key in ('tangents','colors'):
        floats(p[key],name=key); need(len(p[key]) in (0,n*4), f'Invalid {key} channel size.')
    floats(p['bindPoses'],name='bindPoses'); need(len(p['bindPoses']) % 16 == 0, 'Incomplete bindpose matrix.')
    bones = len(p['bindPoses']) // 16
    floats(p['weights'], 4*n if bones else 0, 'weights'); ints(p['boneIndices'], 4*n if bones else 0)
    need(all(0 <= index < bones for index in p['boneIndices']), 'Bone index outside native bind mapping.')
    for vertex in range(n if bones else 0):
        w = p['weights'][vertex*4:vertex*4+4]
        need(all(0 <= value <= 1 for value in w) and abs(sum(w)-1) <= 1e-4, 'Invalid original skin influence weights.')
    need(type(p['subMeshes']) is list and len(p['subMeshes'])>0, 'Missing submesh topology.')
    for indices in p['subMeshes']:
        ints(indices); need(len(indices)%3==0 and all(0<=v<n for v in indices), 'Invalid native triangle topology.')
    need(type(p['uv']) is list, 'Missing UV list.')
    channels = set()
    for uv in p['uv']:
        need(isinstance(uv,dict) and all(k in uv for k in ('channel','dimension','values')), 'Missing UV member.')
        need(integer(uv['channel']) and 0<=uv['channel']<=7 and uv['channel'] not in channels and integer(uv['dimension']) and uv['dimension'] in (2,3,4), 'Invalid/duplicate UV channel or dimension.')
        channels.add(uv['channel']); floats(uv['values'], n*4, 'UV Vector4 storage')
    need(type(p['shapes']) is list, 'Missing native blendshape list.'); names = set()
    for index,shape in enumerate(p['shapes']):
        need(isinstance(shape,dict) and type(shape.get('name')) is str and shape['name'] not in names and integer(shape.get('index')) and shape.get('index')==index and type(shape.get('frames')) is list and len(shape['frames'])>0, 'Invalid native blendshape identity/order.')
        names.add(shape['name']); prior = -math.inf
        for frame in shape['frames']:
            need(isinstance(frame,dict) and all(k in frame for k in ('weight','vertices','normals','tangents')), 'Missing native blend frame channel.')
            weight = number(frame['weight']); need(weight > prior, 'Unordered native blend frames.'); prior = weight
            for key in ('vertices','normals','tangents'): floats(frame[key], n*3, 'blend '+key)
    actual, secondary, length = fingerprint(p)
    need(type(p['sourceFingerprint']) is str and re.fullmatch('[a-f0-9]{64}',p['sourceFingerprint']) and actual==p['sourceFingerprint'], 'Native canonical mesh fingerprint differs.')
    return {'fingerprint':actual,'canonicalSha512':secondary,'canonicalBytes':length,'vertexCount':n,'indexFormat':p['indexFormat'],'boneCount':bones,'skinning':'PRESENT' if bones else 'ABSENT_IN_SOURCE','normals':len(p['normals']),'tangents':len(p['tangents']),'colors':len(p['colors']),'submeshIndexCounts':[len(v) for v in p['subMeshes']],'uv':[{'channel':v['channel'],'dimension':v['dimension']} for v in p['uv']],'blendShapes':[{'name':v['name'],'frameWeights':[x['weight'] for x in v['frames']]} for v in p['shapes']]}

def exporter_contract(text):
    start=text.index('    static string Fingerprint(Packet p)')
    end=text.index('    static string Hash(byte[] bytes)', start)
    return text[start:end].replace('\r\n','\n')

class Files:
    def __init__(self): self.rows={}
    def read(self,path):
        path=Path(path).resolve();data=path.read_bytes();h=digest(data)
        if path in self.rows: need(self.rows[path]['sha256']==h,'An input changed while being collected: '+str(path))
        else: self.rows[path]={'path':str(path),'sha256':h,'bytes':len(data)}
        return data
    def verify(self):
        for path,row in self.rows.items(): need(digest(path.read_bytes())==row['sha256'],'Original input changed: '+str(path))

def within_file(base,value):
    need(type(value) is str and value and not Path(value).is_absolute(), 'Native census must reference a source-relative file.')
    path=(base/value).resolve();need(path.is_relative_to(base.resolve()) and path.is_file(),'Missing or escaped source file: '+value)
    return path

def canonical_material(material: dict[str, Any] | None, files, base) -> tuple[dict[str, Any] | None, dict[str, Any] | None]:
    if material is None:return None,None
    need(type(material) is dict and type(material.get('properties')) is list and type(material.get('shader')) is str,'Missing material shader/property channel.')
    output=copy.deepcopy(material)
    semantic: dict[str, Any]={'shader':material['shader'],'keywords':material['keywords'],'renderQueue':material['renderQueue'],'properties':[]}
    for prop,export in zip(material['properties'],output['properties']):
        need(prop['type'] in ('Texture','Float','Range','Color','Vector'),'Unknown source shader property type.')
        row={k:prop[k] for k in ('name','type','value','vector','scale','offset','width','height','mipmaps','format','colorSpace')}
        if prop.get('textureFile'):
            path=within_file(base,prop['textureFile']);data=files.read(path);need(path.stem==digest(data),'Texture image file hash differs from its native content filename.')
            export['textureFile']=str(path);row['imageFileSha256']=digest(data)
        else:row['imageFileSha256']=None
        semantic['properties'].append(row)
    return output,semantic

def merge_asset(combined, asset, content, provenance):
    key=asset['internalId']
    if key in combined:
        need(combined[key]['sourceContentKey']==content,'Conflicting repeated internalId: '+key)
        combined[key]['sourceProvenance'].append(provenance)
    else:
        asset['sourceContentKey']=content
        combined[key]=asset

def collect(runtime_roots,output,exporter,expected_assets=None):
    files=Files();live=files.read(exporter);contract=exporter_contract(live.decode('utf-8-sig'));inputs=[];combined={};packets={};unique={};material_keys={};textures=set();shaders=Counter();lods=Counter();bone_names=Counter();all_scanned=set();errors=[]
    for runtime in runtime_roots:
        runtime=Path(runtime).resolve();result_path=runtime/'Result.json';r=read_json(files.read(result_path));harness=read_json(files.read(runtime/'HarnessResult.json'))
        need(r.get('status')=='success' and r.get('compilationSucceeded') is True and r.get('failed')==0 and r.get('incomplete')==0 and r.get('completedUtc'),'Native run was not completely successful: '+str(runtime))
        for flag in ('settingsRestored','displaySettingsRestored','probeRemoved','originalNativeFilesUnchanged'):need(r.get(flag) is True,'Native restoration was not verified: '+flag)
        need(r.get('processCleanup',{}).get('completed') is True and r.get('loadedUb',{}).get('matches') is True and r.get('nativeUserDataIsolation',{}).get('isolated') is True,'Native ownership/assembly/isolation gate failed.')
        need(harness.get('status')=='success' and harness.get('failed')==0 and harness.get('incomplete')==0,'Native harness was not successful.')
        source_receipts=[]
        for source in r['sources']:
            archived=files.read(source['archivePath']);need(digest(archived).upper()==source['sha256'].upper(),'Archived probe source differs from Result source identity.')
            if 'Fingerprint(Packet p)' in archived.decode('utf-8-sig'):
                need(exporter_contract(archived.decode('utf-8-sig'))==contract,'Archived/current canonical binary contract differs.')
                source_receipts.append({'path':source['path'],'archivePath':source['archivePath'],'recordedSha256':source['sha256'],'archiveVerified':True,'currentFullFileSha256':digest(live),'currentFullFileMatchesArchived':digest(live).upper()==source['sha256'].upper(),'currentCanonicalSignatureContractMatches':True})
        need(source_receipts,'No archived native canonical source exporter found.')
        game=Path(r['processCleanup']['expectedPath']);native=game.parent/(game.stem+'_Data')/'Managed/Assembly-CSharp.dll'
        need(digest(files.read(native)).upper()==r['hashes']['gameAssembly'].upper(),'Current native game assembly differs from captured source assembly.')
        base=runtime/'FaceSources';census_path=base/'Census.json';census_data=files.read(census_path);c=read_json(census_data)
        need(c.get('sourceOnly') is True and c.get('unityVersion')=='2019.4.37f1' and c.get('errors')==[] and type(c.get('assets')) is list,'Invalid native census completion/source flags.')
        if c.get('referenceCensusPath'):need(digest(files.read(c['referenceCensusPath']))==c['referenceCensusSha256'],'Reference census hash differs.')
        provenance={'runtime':str(runtime),'resultSha256':digest(files.read(result_path)),'census':str(census_path),'censusSha256':digest(census_data),'exporterSources':source_receipts}
        inputs.append({'runtime':str(runtime),'assets':len(c['assets']),'skins':sum(len(a['skins']) for a in c['assets']),'nativeAssemblySha256':r['hashes']['gameAssembly'],'recordedUbSha256':r['expectedUb']['sha256'],'provenance':provenance})
        all_scanned.update(c.get('scannedWithoutFace',[]))
        for asset in c['assets']:
            need(type(asset.get('internalId')) is str and asset['internalId'] and type(asset.get('skins')) is list and len(asset['skins'])>0,'Census contains a missing/empty skinned asset.')
            a=copy.deepcopy(asset);a['sourceProvenance']=[provenance];semantic=[]
            for skin,out in zip(asset['skins'],a['skins']):
                path=within_file(base,skin['meshFile'])
                if path not in packets:
                    compressed=files.read(path);raw=gzip.decompress(compressed);need(path.name.split('.')[0]==digest(raw),'Mesh JSON filename content hash differs.')
                    packet=read_json(raw);meta=validate_packet(packet)
                    previous=unique.get(meta['fingerprint']);need(previous is None or previous['canonicalSha512']==meta['canonicalSha512'] and previous['canonicalBytes']==meta['canonicalBytes'],'Same source fingerprint has different canonical content.')
                    if previous is None:unique[meta['fingerprint']]=dict(meta,packetFiles=[],names=[],nativeReadability=[])
                    entry=unique[meta['fingerprint']];entry['packetFiles'].append(str(path))
                    for key,value in [('names',packet.get('name')),('nativeReadability',packet.get('nativeReadable'))]:
                        if value not in entry[key]:entry[key].append(value)
                    packets[path]=meta
                meta=packets[path];need(skin['vertexCount']==meta['vertexCount'] and len(skin['bones'])==meta['boneCount'],'Renderer vertex/bone mapping differs from its source packet.')
                floats(skin['rendererToPrefab'],16,'renderer matrix');floats(skin['bonesToPrefab'],len(skin['bones'])*16,'bone matrices')
                out['meshFile']=str(path);out['sourceFingerprint']=meta['fingerprint'];out['sourceProvenance']={'census':str(census_path),'runtime':str(runtime)}
                materials=[]
                for material_index,material in enumerate(skin['materials']):
                    exported,m=canonical_material(material,files,base);out['materials'][material_index]=exported;materials.append(m)
                    key=json.dumps(m,sort_keys=True,separators=(',',':'));material_keys.setdefault(digest(key.encode()),m)
                    if m is not None:
                        assert exported is not None
                        shaders[m['shader']]+=1
                        for prop in exported['properties']:
                            if prop.get('textureFile'):textures.add(prop['textureFile'])
                semantic.append({'path':skin['path'],'sourceFingerprint':meta['fingerprint'],'rootBone':skin['rootBone'],'bones':skin['bones'],'rendererToPrefab':skin['rendererToPrefab'],'bonesToPrefab':skin['bonesToPrefab'],'materials':materials})
                match=re.search(r'(?:^|[_/])LOD(\d+)(?:$|[_/])',skin['path']+'/'+skin['mesh'],re.I);lods[match.group(1) if match else 'UNLABELLED']+=1
                bone_names.update(p.rsplit('/',1)[-1] if p else '<UNRESOLVED>' for p in skin['bones'])
            content=digest(json.dumps(semantic,sort_keys=True,separators=(',',':')).encode());key=asset['internalId']
            merge_asset(combined,a,content,provenance)
        print(json.dumps({'validatedRuntime':str(runtime),'assets':len(c['assets']),'uniquePacketsSoFar':len(packets),'uniqueMeshesSoFar':len(unique)}),flush=True)
    if expected_assets is not None:need(len(combined)==expected_assets,'Combined asset count differs from explicit expectation.')
    files.verify();assets=[combined[k] for k in sorted(combined)];census={'schemaVersion':1,'sourceOnly':True,'unityVersion':'2019.4.37f1','assets':assets,'errors':[],'inputs':inputs,'scannedWithoutFace':sorted(all_scanned-set(combined)),'signatureContract':{'name':'Solasta.MeshSource.1','canonicalExporterMethodSha256':digest(contract.encode()),'excludedProvenance':['mesh name','bounds','native readability','asset address/name','input runtime/exporter hash','unused UV Vector4 padding outside declared dimension']}}
    inventory={'schemaVersion':1,'status':'PASS','scope':'Immutable native census/packet/image-file inventory only; no refinement, mode or artistic acceptance. Source rest channels are not posed actor geometry.','counts':{'assets':len(assets),'skins':sum(len(a['skins']) for a in assets),'physicalPacketFiles':len(packets),'uniqueMeshFingerprints':len(unique),'uniqueMaterials':len(material_keys),'uniqueImageFiles':len(textures),'uniqueShaders':len(shaders),'remainingNotExportedInternalIds':len(census['scannedWithoutFace'])},'shaderUsage':dict(sorted(shaders.items())),'lodUsage':dict(sorted(lods.items())),'boneNameUsage':dict(sorted(bone_names.items())),'clothObservation':{'status':'NOT_OBSERVED_BY_NATIVE_EXPORT_SCHEMA','clothOwnerCount':None},'texturePixelFingerprint':{'status':'NOT_EVALUATED_IMAGE_FILE_SHA_ONLY'},'meshes':[unique[k] for k in sorted(unique)],'materials':[{'sourceContentKey':key,'content':material_keys[key]} for key in sorted(material_keys)],'images':[files.rows[Path(path)] for path in sorted(textures)],'inputs':inputs,'originalsUnchanged':True,'sourceFiles':list(files.rows.values()),'collectorSha256':digest(Path(__file__).read_bytes())}
    output=Path(output).resolve();need(output.is_relative_to(ALLOWED_OUTPUT.resolve()),'Output must stay under the NativeSources authoring workspace.')
    need(not any(output.is_relative_to(Path(r).resolve()) or Path(r).resolve().is_relative_to(output) for r in runtime_roots),'Output overlaps a native input runtime.')
    output.mkdir(parents=True,exist_ok=True)
    for name in ('Census.json','Inventory.json'):need(not (output/name).exists(),'Output exists; choose a new NativeSources subdirectory instead of overwriting evidence.')
    for name,value in [('Census.json',census),('Inventory.json',inventory)]:
        with (output/name).open('x',encoding='utf-8') as stream:json.dump(value,stream,ensure_ascii=False,separators=(',',':'))
    return inventory

def self_test(runtime):
    base=Path(runtime)/'FaceSources';c=read_json((base/'Census.json').read_bytes());path=within_file(base,c['assets'][0]['skins'][0]['meshFile']);original=read_json(gzip.decompress(path.read_bytes()));checks=[]
    def check(name,fn,reject=False):
        error=None
        try:fn();accepted=True
        except SourceError as e:error=str(e);accepted=False
        need(accepted != reject,'Self-test failed: '+name);checks.append({'name':name,'status':'PASS','rejection':error})
    check('actual-native-packet-positive',lambda:validate_packet(original))
    for key in ('vertices','normals','tangents','bindPoses','weights','boneIndices','subMeshes','uv','shapes'):
        def missing(k=key):
            p=copy.deepcopy(original);del p[k];validate_packet(p)
        check('reject-missing-'+key,missing,True)
    def mutation():
        p=copy.deepcopy(original);p['vertices'][0]+=.25;validate_packet(p)
    check('reject-signed-content-mutation',mutation,True)
    def metadata():
        p=copy.deepcopy(original);p['name']='Renamed source';p['bounds']=[999]*6;p['nativeReadable']=not p['nativeReadable'];need(fingerprint(p)==fingerprint(original),'Metadata entered deformation key.')
    check('name-bounds-readability-excluded',metadata)
    def bad_index():
        p=copy.deepcopy(original);p['boneIndices'][0]=len(p['bindPoses'])//16;validate_packet(p)
    check('reject-bone-range',bad_index,True)
    def bad_uv():
        p=copy.deepcopy(original);p['uv'][0]['dimension']=5;validate_packet(p)
    check('reject-UV-dimension',bad_uv,True)
    def zero():
        p=copy.deepcopy(original);p['vertices']=[-0.0 if v==0 else v for v in p['vertices']];need(fingerprint(p)==fingerprint(original),'Signed zero was not normalized.')
    check('signed-zero-normalization',zero)
    def escape():within_file(base,'../Result.json')
    check('reject-source-path-escape',escape,True)
    blend=None
    for asset in c['assets']:
        for skin in asset['skins']:
            candidate=read_json(gzip.decompress(within_file(base,skin['meshFile']).read_bytes()))
            if candidate['shapes']:blend=candidate;break
        if blend:break
    if blend is None: raise SourceError('A native blendshape packet is required for contracts.')
    check('actual-native-all-blend-channels-positive',lambda:validate_packet(blend))
    for channel in ('vertices','normals','tangents'):
        def frame_mutation(key=channel):
            packet=copy.deepcopy(blend);packet['shapes'][0]['frames'][0][key][0]+=.125;validate_packet(packet)
        check('reject-native-blend-'+channel+'-mutation',frame_mutation,True)
    def duplicate_frame():
        packet=copy.deepcopy(blend);packet['shapes'][0]['frames'].append(copy.deepcopy(packet['shapes'][0]['frames'][-1]));validate_packet(packet)
    check('reject-native-frame-order',duplicate_frame,True)
    items={};entry={'internalId':'native-resource','sourceProvenance':[{'runtime':'original'}]};merge_asset(items,entry,'content',{})
    check('same-internalId-content-accepts-extra-provenance',lambda:merge_asset(items,copy.deepcopy(entry),'content',{'runtime':'repeat'}))
    check('reject-internalId-content-conflict',lambda:merge_asset(items,copy.deepcopy(entry),'other-content',{}),True)
    return {'status':'PASS','scope':'Actual native packet plus small channel/signature/provenance/path mutations. No original writes.','checks':checks}

def main():
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('--runtime-root',type=Path,action='append',required=True);p.add_argument('--output',type=Path,default=ALLOWED_OUTPUT);p.add_argument('--exporter-source',type=Path,default=DEFAULT_EXPORTER);p.add_argument('--expected-assets',type=int);p.add_argument('--self-test',action='store_true');a=p.parse_args()
    try:
        tests=self_test(a.runtime_root[0]) if a.self_test else None
        result=collect(a.runtime_root,a.output,a.exporter_source,a.expected_assets)
        if tests:
            with (a.output/'Collector-Contracts.json').open('x',encoding='utf-8') as stream:json.dump(tests,stream,indent=2)
        print(json.dumps({'status':result['status'],'counts':result['counts'],'selfTests':len(tests['checks']) if tests else None,'output':str(a.output)}))
        return 0
    except (SourceError,OSError,KeyError,ValueError,TypeError) as e:
        print(json.dumps({'status':'FAIL','error':str(e)}),file=sys.stderr);return 1
if __name__=='__main__':raise SystemExit(main())
