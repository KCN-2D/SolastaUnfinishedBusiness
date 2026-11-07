"""Reuse source-exact prefix/winding proof and the standard lossless recipe codec."""
import os
for key in ('OMP_NUM_THREADS','OPENBLAS_NUM_THREADS','MKL_NUM_THREADS'):os.environ[key]='1'
from pathlib import Path
import argparse,ast,collections,gzip,hashlib,json,struct,sys
import numpy as np
H=Path(__file__).resolve().parent
REPO=next(p for p in H.parents if (p/'SolastaUnfinishedBusiness.sln').exists())
read=lambda p:json.loads(Path(p).read_text('utf-8-sig'))
sha=lambda p:hashlib.sha256(Path(p).read_bytes()).hexdigest()
sys.path.insert(0,str(REPO/'tools/blender-models/CharacterFaces'))
from collect_sources import fingerprint
sys.path.insert(0,str(REPO/'tools/asset-authoring/UnityAssetBuild'))
from mesh_recipe_codec import encode_recipe
def functions(path,names,namespace):
    nodes: list[ast.stmt]=[n for n in ast.parse(path.read_text('utf-8-sig')).body if isinstance(n,ast.FunctionDef) and n.name in names]
    assert len(nodes)==len(names)
    exec(compile(ast.Module(body=nodes,type_ignores=[]),str(path),'exec'),namespace)
    return {name:namespace[name] for name in names}
scope=dict(Path=Path,gzip=gzip,struct=struct,np=np)
decoder=H/'GenerateFlowCoverageSweptProfile.py';binding_functions=functions(decoder,['decode_binding','validate_surface_provenance'],scope)
prover=decoder;decode_binding=binding_functions['decode_binding'];validate_surface_provenance=binding_functions['validate_surface_provenance']
proof_functions=functions(prover,['prove_row','validate_curved_core'],dict(globals()))
recipe_owner=REPO/'tools/blender-models/CharacterFaces/build_models.py';recipe_functions=functions(recipe_owner,['recipe_mesh'],dict(globals()))
parser=argparse.ArgumentParser();parser.add_argument('--workspace',type=Path,required=True);parser.add_argument('directory');args=parser.parse_args();ROLLOUT=args.workspace.resolve();assert any(ROLLOUT.is_relative_to(REPO/p)for p in('tools/game-test/artifacts','tools/asset-authoring/workspace')),'Use an ignored authoring workspace.'
out=Path(args.directory).resolve();assert out.is_relative_to(ROLLOUT/'Postprocess')
assert not (out/'Prepared.json').exists()
group=read(out/'CompletedGroup.json');container=read(out/'Container.json')
assert sha(out/'CompletedGroup.json')==container['groupSnapshotSha256']
meshes=[];rows=[];proofs=[]
(out/'Bindings').mkdir();(out/'Proofs').mkdir()
for source in group['sources']:
    for authority in [source['sourceDescriptor'],source['exportInput'],source['generation']]:
        assert sha(authority['path'])==authority['sha256'].lower()
    g=read(source['generation']['path']);assert len(g['rows'])==1
    row=g['rows'][0];fp=source['sourceFingerprint']
    assert row['manifestEntry']['sourceFingerprint']==fp and row['lod']==source['strictestLod']
    assert sha(row['generatedPacket'])==row['generatedSha256'].lower()
    assert sha(row['bindingPath'])==row['bindingSha256'].lower()
    proof=proof_functions['prove_row'](row,g['profile'])
    packet=read(row['generatedPacket']);actual=fingerprint(packet)[0]
    assert actual==row['authoredFingerprint']
    meshes.append(recipe_functions['recipe_mesh'](packet))
    binding=out/'Bindings'/(fp+'.bytes');os.link(row['bindingPath'],binding)
    proof_path=out/'Proofs'/(fp+'.json')
    proof_path.write_text(json.dumps(dict(proof,originalGeneration=source['generation'],sourceDescriptor=source['sourceDescriptor'],
        prover=dict(path=str(prover),sha256=sha(prover)),decoder=dict(path=str(decoder),sha256=sha(decoder))),indent=2)+'\n')
    rows.append(dict(sourceFingerprint=fp,authoredFingerprint=actual,name=packet['name'],lod=row['lod'],
        generationSha256=source['generation']['sha256'],binding=dict(path=str(binding),sha256=sha(binding)),
        numericProof=dict(path=str(proof_path),sha256=sha(proof_path)),manifestEntry=row['manifestEntry'],
        originalVertexCount=row['originalVertices'],vertexCount=packet['vertexCount'],physicalFrames=row['physicalFrames']))
    proofs.append(proof)
recipe=dict(schemaVersion=1,bundleName=group['desiredBundleFile'].lower(),meshes=meshes)
if container['retained']:
    assert sha(out/'Source.bundle')==container['sourceBundle']['sha256']
    recipe['sourceBundles']=[dict(path='Source.bundle',sha256=container['sourceBundle']['sha256'],assets=container['retained'])]
recipe_path=out/'MeshRecipe.json';encode_recipe(recipe,recipe_path,'Data/m.bin')
prepared=dict(status='PASS_NUMERIC_PREFIX_WINDING_AND_RECIPE_READY',groupKey=group['groupKey'],
    planSha256=group['planSha256'],completedGroupSnapshot=dict(path=str(out/'CompletedGroup.json'),sha256=sha(out/'CompletedGroup.json')),
    completedGroupSource=container['groupSource'],desiredBundleFile=group['desiredBundleFile'],
    recipe=dict(path=str(recipe_path),sha256=sha(recipe_path)),sources=rows,
    numericProofsPassed=len(proofs),engineReadbackPending=True,
    recipeOwner=dict(path=str(recipe_owner),sha256=sha(recipe_owner)),codec=dict(path=str(REPO/'tools/asset-authoring/UnityAssetBuild/mesh_recipe_codec.py'),sha256=sha(REPO/'tools/asset-authoring/UnityAssetBuild/mesh_recipe_codec.py')))
(out/'Prepared.json').write_text(json.dumps(prepared,indent=2)+'\n')
print(json.dumps(dict(groupKey=group['groupKey'],resources=len(rows),recipePath=str(recipe_path))))
