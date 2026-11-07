"""Check the authored hair material against its native property contract."""
import argparse
import hashlib
import json
import math
from pathlib import Path
import random
import UnityPy


def read(path):
    return json.loads(Path(path).read_text(encoding="utf-8-sig"))


def reference(path):
    path = Path(path).resolve()
    return {"path": str(path), "sha256": hashlib.sha256(path.read_bytes()).hexdigest()}


def material(path):
    environment = UnityPy.load(str(path))
    values = [obj.read() for obj in environment.objects if obj.type.name == "Material"]
    assert len(values) == 1
    value = values[0]
    shader = value.m_Shader.deref().read()
    properties = {prop.m_Name: int(prop.m_Type) for prop in shader.m_ParsedForm.m_PropInfo.m_Props}
    return value, shader, properties


def check_distribution(smoothness, anisotropy):
    roughness = max((1 - smoothness) ** 2, .002)
    aspect = math.sqrt(max(1 - anisotropy, .05))
    at, ab = max(roughness * aspect, .002), max(roughness / aspect, .002)

    def distribution(x, y, z):
        denominator = (x / at) ** 2 + (y / ab) ** 2 + z * z
        return 1 / (math.pi * at * ab * denominator ** 2)

    # Integrate the normalized NDF over solid angle; expected projected area is 1.
    polar_steps, azimuth_steps = 512, 256
    total = 0
    for ti in range(polar_steps):
        theta = (ti + .5) * math.pi / (2 * polar_steps)
        st, ct = math.sin(theta), math.cos(theta)
        for pi in range(azimuth_steps):
            phi = (pi + .5) * 2 * math.pi / azimuth_steps
            total += distribution(st * math.cos(phi), st * math.sin(phi), ct) * ct * st
    integral = total * math.pi / (2 * polar_steps) * 2 * math.pi / azimuth_steps
    assert abs(integral - 1) < .005, integral
    # Isotropic GGX is rotation invariant. Anisotropic axes remain reciprocal.
    rng = random.Random(20261101)
    maximum_reciprocity_error = 0
    maximum_isotropic_error = 0
    for _ in range(4096):
        phi = rng.uniform(0, 2 * math.pi)
        z = rng.uniform(1e-5, 1)
        r = math.sqrt(1 - z * z)
        x, y = r * math.cos(phi), r * math.sin(phi)
        if anisotropy == 0:
            maximum_isotropic_error = max(maximum_isotropic_error, abs(distribution(x, y, z) - distribution(y, x, z)))
        light_z, view_z = rng.uniform(1e-5, 1), rng.uniform(1e-5, 1)
        light_phi, view_phi = rng.uniform(0, 2 * math.pi), rng.uniform(0, 2 * math.pi)
        light_r, view_r = math.sqrt(1-light_z*light_z), math.sqrt(1-view_z*view_z)
        lx, ly = light_r*math.cos(light_phi), light_r*math.sin(light_phi)
        vx, vy = view_r*math.cos(view_phi), view_r*math.sin(view_phi)
        lambda_l = math.sqrt((at*lx)**2+(ab*ly)**2+light_z**2)
        lambda_v = math.sqrt((at*vx)**2+(ab*vy)**2+view_z**2)
        visibility = .5 / (light_z*lambda_v+view_z*lambda_l)
        reverse = .5 / (view_z*lambda_l+light_z*lambda_v)
        assert math.isfinite(visibility) and visibility > 0
        maximum_reciprocity_error = max(maximum_reciprocity_error, abs(visibility-reverse))
    assert maximum_isotropic_error < 1e-10 and maximum_reciprocity_error == 0
    return {"smoothness": smoothness, "anisotropy": anisotropy, "ndfProjectedArea": integral,
            "directionPairs": 4096, "maximumReciprocityError": maximum_reciprocity_error,
            "maximumIsotropicRotationError": maximum_isotropic_error}


def compiled_passes(shader):
    result=[]
    for subshader in shader.m_ParsedForm.m_SubShaders:
        for shader_pass in subshader.m_Passes:
            state=shader_pass.m_State;tags=dict(state.m_Tags.tags);blend=state.rtBlend0
            result.append(dict(name=state.m_Name,lightMode=tags.get('LIGHTMODE'),cull=float(state.culling.val),zWrite=float(state.zWrite.val),zTest=float(state.zTest.val),alphaToMask=float(state.alphaToMask.val),blend=[float(getattr(blend,k).val)for k in ('srcBlend','destBlend','srcBlendAlpha','destBlendAlpha','blendOp','blendOpAlpha','colMask')]))
    return result


def validate_float_ownership(original_properties, properties, original_floats, floats, owned):
    assert all(properties.get(name) == kind for name, kind in original_properties.items()), "A native shader property or type changed."
    assert set(properties)-set(original_properties)==set(owned)-set(original_properties), "A new shader property was not explicitly declared by the recipe."
    assert set(floats)-set(original_floats)<=set(owned), "An undeclared saved scalar was added."
    for name,value in original_floats.items():
        if name not in owned:
            assert name in floats and floats[name]==value, "An unowned native scalar default changed."
    for name,value in owned.items():
        assert properties[name] in (2,3) and abs(floats[name]-value)<1e-6, "A declared float differs from its intended recipe value."


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--report", required=True, type=Path)
    parser.add_argument("--baseline", required=True, type=Path)
    parser.add_argument("--recipe", type=Path, default=Path(__file__).with_name('materials.json'))
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    report = read(args.report)
    assert report["success"] and report["stage"] == "complete"
    assert report["validationScope"] == "materials"
    assert report["requestedColorSpace"] == report["actualColorSpace"] == "Linear"
    bundle = Path(report["bundlePath"])
    recipe = read(args.recipe)
    assert report['inputSha256'] == reference(args.recipe)['sha256']
    assert report["bundleSha256"] == reference(bundle)["sha256"]
    original, original_shader, original_properties = material(args.baseline)
    current, shader, properties = material(bundle)
    assert current.m_Name == original.m_Name == "SourceGroomNativeHair"
    assert shader.m_ParsedForm.m_Name == original_shader.m_ParsedForm.m_Name == "KCN/SourceGroom/NativeHair"
    colors=lambda m:{k:(v.r,v.g,v.b,v.a) for k,v in m.m_SavedProperties.m_Colors}
    assert colors(current)==colors(original), "Material color defaults changed."
    textures=lambda m:{k:(v.m_Texture.m_FileID,v.m_Texture.m_PathID,v.m_Scale.x,v.m_Scale.y,v.m_Offset.x,v.m_Offset.y) for k,v in m.m_SavedProperties.m_TexEnvs}
    assert textures(current)==textures(original), "Material texture defaults or sampler ST changed."
    authored_floats = {item['name']: item['value'] for item in recipe['materials'][0]['properties'] if item['kind'] == 'float'}
    floats = dict(current.m_SavedProperties.m_Floats)
    validate_float_ownership(original_properties, properties, dict(original.m_SavedProperties.m_Floats), floats, authored_floats)
    assert report["materials"][0]["passes"] == ["FORWARD", "FORWARD", "ShadowCaster"]
    # The native shader renders both sides independently of its saved _CullMode.
    # Checking only property preservation admitted a scalp-coverage regression.
    baseline_passes=compiled_passes(original_shader);actual_passes=compiled_passes(shader)
    required={'FORWARDBASE','FORWARDADD','SHADOWCASTER'}
    assert required <= {p['lightMode'] for p in baseline_passes}
    native_count=len(baseline_passes)
    assert native_count and len(actual_passes)%native_count==0
    variants=len(actual_passes)//native_count
    assert variants in (1,2) and all(actual_passes[i*native_count:(i+1)*native_count]==baseline_passes
                                   for i in range(variants)), 'The saved shader changed adopted pass/state ownership.'
    if variants==2:
        tags=dict(shader.m_ParsedForm.m_SubShaders[0].m_Tags.tags)
        assert tags.get('UBNativeSurface','').lower()=='true' and tags.get('UBGeometrySurface','').lower()=='true'
        assert authored_floats.get('_UBSurfaceProjection')==0, 'Projection must start disabled and be owned by the recipe.'
    culling=[(p['name'],'<compiled>',p['cull']) for p in actual_passes]
    assert all(p['cull']==0 and p['alphaToMask']==0 for p in actual_passes)
    assert report["materials"][0]["shaderSupported"] and report["materials"][0]["renderQueue"] == 2450
    asset = next(name for name in report["assetNames"] if name.endswith("/sourcegroomnativehair.mat"))
    result = {"status": "PASS_SHADER_MATERIAL_AND_BRDF_CONTRACTS", "bundle": reference(bundle),
              "baselineBundle": reference(args.baseline), "report": reference(args.report),
              "recipe": reference(args.recipe),
              "materialAsset": asset, "shader": shader.m_ParsedForm.m_Name,
              "nativePropertiesRetained": original_properties, "newProperties": {name: properties[name] for name in properties.keys()-original_properties.keys()},
              "candidateFloats": {name: floats[name] for name in authored_floats},
              "passes": report["materials"][0]["passes"],
              "culling": culling, "compiledPasses": actual_passes, "baselineCompiledPasses": baseline_passes,
              "numericalContracts": [check_distribution(authored_floats['_FiberSmoothness'], authored_floats['_FiberAnisotropy']), check_distribution(authored_floats['_FiberSmoothness'], 0)],
              "nativeRenderedAcceptance": "pending"}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    assert not args.output.exists()
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2)+"\n", encoding="utf-8")
    print(json.dumps({"status": result["status"], "output": str(args.output), "bundle": result["bundle"], "materialAsset": asset}, indent=2))


if __name__ == "__main__":
    main()
