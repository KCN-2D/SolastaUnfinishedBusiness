"""Inventory sprite sources, pin native references, and review artwork in its UI contracts."""
from __future__ import annotations

import argparse
import hashlib
import json
import re
import shutil
from pathlib import Path

from PIL import Image, ImageChops, ImageDraw, ImageOps

REPO = Path(__file__).resolve().parents[2]
BLUEPRINTS = REPO / "Diagnostics/OfficialBlueprints"


def digest(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def write_json(path: Path, value: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def read_json(path: Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8-sig"))


def sprite_paths(value, path=()):
    """Discover the serialized sprite contract, including nested and non-GUI properties."""
    if isinstance(value, dict):
        if "AssetReferenceSprite" in value.get("$type", ""):
            yield list(path), value
        for key, child in value.items():
            yield from sprite_paths(child, (*path, key))
    elif isinstance(value, list):
        for index, child in enumerate(value):
            yield from sprite_paths(child, (*path, index))


def native_reference(name: str, definition_type: str = "SpellDefinition", *,
                     sprite_path=None, blueprint_root: Path | None = None) -> dict:
    root = (blueprint_root or BLUEPRINTS).resolve(strict=True)
    # Definition names are filenames, not C# identifiers (e.g. magic items contain '+').
    if any(not part or part in (".", "..") or any(c in part for c in '/\\:')
           for part in (name, definition_type)):
        raise ValueError(f"Invalid icon definition: {definition_type}/{name}")
    blueprint = (root / definition_type / f"{name}.json").resolve(strict=True)
    if not blueprint.is_relative_to(root):
        raise ValueError("Blueprint is outside its source root")
    data = read_json(blueprint)
    if data.get("$type", "").split(",")[0] != definition_type:
        raise ValueError("Blueprint definition type mismatch")
    path = sprite_path if sprite_path is not None else ["guiPresentation", "spriteReference"]
    sprite = data
    if not path or any(type(token) not in (str, int) for token in path):
        raise ValueError("A sprite property path is required")
    for token in path:
        sprite = sprite[token]
    if not isinstance(sprite, dict) or "AssetReferenceSprite" not in sprite.get("$type", ""):
        raise ValueError("Property is not an AssetReferenceSprite")
    guid, sub = sprite.get("m_AssetGUID", ""), sprite.get("m_SubObjectName", "")
    if not re.fullmatch(r"[0-9a-fA-F]{32}|[0-9a-fA-F-]{36}", guid):
        raise ValueError(f"No valid sprite at {definition_type}/{name}: {path}")
    identity = f"{definition_type}/{name}:" + json.dumps(path, separators=(",", ":"))
    return {"id": identity, "definition": name, "definitionType": definition_type, "spritePath": path,
            "assetGuid": guid, "subObject": sub, "blueprintRoot": str(root),
            "blueprint": str(blueprint), "blueprintSha256": digest(blueprint)}


def resolve_reference(row: dict) -> dict:
    return native_reference(row["definition"], row["definitionType"], sprite_path=row.get("spritePath"),
                            blueprint_root=Path(row["blueprintRoot"]) if row.get("blueprintRoot") else None)


def catalog(root: Path, output: Path) -> None:
    references, empty, identities = [], 0, {}
    for path in sorted(root.glob("*/*.json")):
        data = read_json(path)
        for location, sprite in sprite_paths(data):
            if not sprite.get("m_AssetGUID"):
                empty += 1
                continue
            row = native_reference(path.stem, path.parent.name, sprite_path=location, blueprint_root=root)
            references.append(row)
            identities.setdefault((row["assetGuid"], row["subObject"]), []).append(row["id"])
    write_json(output, {"schemaVersion": 2, "references": references, "emptySpriteCount": empty,
                        "sharedSprites": [ids for ids in identities.values() if len(ids) > 1]})


def capture_source(spec: Path, output: Path) -> None:
    refs = [resolve_reference(row) for row in read_json(spec)["references"]]
    if not refs or len({r["id"] for r in refs}) != len(refs):
        raise ValueError("Select distinct sprite properties for capture")
    # This source is a hashed input of the existing runtime verification runner.
    # Run resolves artwork only; Observe records final Images after their normal binders run.
    source = r'''using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
public static class NativeIconReferences
{
 static readonly JArray observations=new JArray();
 public static IEnumerator Run(Action<string> log)
 {
  var refs=JArray.Parse(@"__ICON_SOURCE_ROWS__");var rows=new JArray();
  var ub=AppDomain.CurrentDomain.GetAssemblies().Single(a=>a.GetName().Name=="SolastaUnfinishedBusiness");
  var custom=ub.GetType("SolastaUnfinishedBusiness.CustomUI.Sprites").GetMethod("GetSpriteByGuid",BindingFlags.Static|BindingFlags.NonPublic|BindingFlags.Public);
  foreach(JObject reference in refs)
  {
   var guid=(string)reference["assetGuid"];var sub=(string)reference["subObject"];
   var sprite=(Sprite)custom.Invoke(null,new object[]{guid});
   var handle=default(AsyncOperationHandle<Sprite>);var loaded=false;
   if(!sprite){handle=Addressables.LoadAssetAsync<Sprite>(guid+(string.IsNullOrEmpty(sub)?"":"["+sub+"]"));loaded=true;yield return handle;if(handle.Status==AsyncOperationStatus.Succeeded)sprite=handle.Result;}
   if(!sprite){if(loaded)Addressables.Release(handle);throw new InvalidOperationException((string)reference["id"]);}
   var original=RenderTexture.active;RenderTexture render=null;Texture2D copy=null;
   try
   {
    if(sprite.packed&&sprite.packingRotation!=SpritePackingRotation.None)throw new InvalidOperationException("Rotated atlas requires explicit unpacking: "+sprite.name);
    var rect=sprite.textureRect;var offset=sprite.textureRectOffset;
    render=RenderTexture.GetTemporary(sprite.texture.width,sprite.texture.height,0,RenderTextureFormat.ARGB32,RenderTextureReadWrite.sRGB);
    Graphics.Blit(sprite.texture,render);RenderTexture.active=render;
    copy=new Texture2D((int)sprite.rect.width,(int)sprite.rect.height,TextureFormat.RGBA32,false);
    copy.SetPixels(new Color[copy.width*copy.height]);copy.ReadPixels(rect,(int)offset.x,(int)offset.y);copy.Apply();
    var path=Path.Combine(RuntimeVerificationConfiguration.EvidenceDirectory,"NativeIcon-"+rows.Count+".png");File.WriteAllBytes(path,copy.EncodeToPNG());
    var row=(JObject)reference.DeepClone();row["path"]=path;row["width"]=copy.width;row["height"]=copy.height;row["resolvedSprite"]=sprite.name;rows.Add(row);
    log("PASS: captured native sprite "+reference["id"]+" "+copy.width+"x"+copy.height);
   }
   finally{RenderTexture.active=original;if(render)RenderTexture.ReleaseTemporary(render);if(copy)UnityEngine.Object.Destroy(copy);if(loaded)Addressables.Release(handle);}
  }
  File.WriteAllText(Path.Combine(RuntimeVerificationConfiguration.EvidenceDirectory,"NativeIconReferences.json"),new JObject{["schemaVersion"]=2,["status"]="PASS_NATIVE_ICON_REFERENCES",["images"]=rows}.ToString());
 }
 public static void Observe(Image image,string usage,string state,string screenshot)
 {
  if(!image||!image.sprite||!image.isActiveAndEnabled||!File.Exists(screenshot))throw new InvalidOperationException("Visible native Image and its screenshot required: "+usage);
  var corners=new Vector3[4];image.rectTransform.GetWorldCorners(corners);var canvas=image.canvas;
  var nodes=new System.Collections.Generic.List<string>();for(var t=image.transform;t!=null;t=t.parent)nodes.Insert(0,t.name);
  observations.Add(new JObject{["usage"]=usage,["state"]=state,["hierarchy"]=string.Join("/",nodes),["sprite"]=image.sprite.name,["spriteSize"]=new JArray(image.sprite.rect.width,image.sprite.rect.height),["size"]=new JArray(image.rectTransform.rect.width,image.rectTransform.rect.height),["tint"]=new JArray(image.color.r,image.color.g,image.color.b,image.color.a),["preserveAspect"]=image.preserveAspect,["imageType"]=image.type.ToString(),["canvasScale"]=canvas?canvas.scaleFactor:1,["canvasAlpha"]=image.canvasRenderer.GetInheritedAlpha(),["worldCorners"]=JArray.FromObject(corners.Select(v=>new[]{v.x,v.y,v.z})),["screenshot"]=screenshot});
  File.WriteAllText(Path.Combine(RuntimeVerificationConfiguration.EvidenceDirectory,"NativeIconUsages.json"),new JObject{["observations"]=observations}.ToString());
 }
}
'''.replace("__ICON_SOURCE_ROWS__", json.dumps(refs, ensure_ascii=True).replace('"', '""'))
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(source, encoding="utf-8")
    write_json(output.with_suffix(".references.json"), {"references": refs})


def archive(path: Path, directory: Path) -> dict:
    path = path.resolve(strict=True)
    sha = digest(path)
    target = directory / (sha[:16] + path.suffix.lower())
    target.parent.mkdir(parents=True, exist_ok=True)
    if target.exists() and digest(target) != sha:
        raise ValueError("An archived source changed")
    if not target.exists():
        shutil.copyfile(path, target)
    return {"sourcePath": str(path), "path": str(target.resolve()), "sha256": sha}


def dimensions(value) -> tuple[int, int]:
    if not isinstance(value, list) or len(value) != 2 or any(type(v) is not int or not 1 <= v <= 4096 for v in value):
        raise ValueError("Image size must be [width, height], from 1 to 4096 pixels")
    return tuple(value)


def rgba(value) -> tuple[int, int, int, int]:
    if not isinstance(value, list) or len(value) != 4 or any(type(v) is not int or not 0 <= v <= 255 for v in value):
        raise ValueError("RGBA must contain four byte values")
    return tuple(value)


def prepare(manifest: Path, contract_path: Path, output: Path) -> dict:
    contract, capture = read_json(contract_path), read_json(manifest)
    if capture.get("status") != "PASS_NATIVE_ICON_REFERENCES":
        raise ValueError("A successful native sprite capture is required")
    for key in ("subject", "composition", "artDirection"):
        if not isinstance(contract.get(key), str) or not contract[key].strip():
            raise ValueError(f"Specify {key} from the target's visual role and native references")
    dimensions(contract["outputSize"])
    if type(contract.get("transparentBackground")) is not bool or not contract.get("usages"):
        raise ValueError("Specify output transparency and actual UI usages")
    usage_ids = set()
    for usage in contract["usages"]:
        for key in ("id", "role", "screen", "binding"):
            if not isinstance(usage.get(key), str) or not usage[key].strip():
                raise ValueError(f"UI usage requires {key}")
        if usage["id"] in usage_ids or not usage.get("states"):
            raise ValueError("UI usage identities must be distinct and have display states")
        usage_ids.add(usage["id"])
        dimensions(usage["size"])
        if type(usage.get("preserveAspect")) is not bool:
            raise ValueError("Specify the native Image preserveAspect setting")
        states = set()
        for state in usage["states"]:
            if not state.get("name") or state["name"] in states:
                raise ValueError("Display states must have distinct names")
            states.add(state["name"])
            rgba(state["tint"])
            rgba(state["background"])
        if not usage.get("evidence"):
            raise ValueError("Trace each UI binding to source or native evidence")
    rows = {row["id"]: row for row in capture["images"]}
    refs = []
    for selector in contract["references"]:
        native = resolve_reference(selector)
        row = rows[native["id"]]
        if any(row[key] != native[key] for key in ("assetGuid", "subObject", "blueprintSha256")):
            raise ValueError("Native sprite identity mismatch: " + native["id"])
        path = Path(row["path"])
        if not path.is_absolute():
            path = manifest.parent / path
        with Image.open(path) as image:
            if image.mode != "RGBA" or image.size != (row["width"], row["height"]) or not image.getbbox():
                raise ValueError("Native sprite pixels or dimensions do not match the capture")
        refs.append(native | archive(path, output.parent / "References") |
                    {"blueprintCopy": archive(Path(native["blueprint"]), output.parent / "Sources"),
                     "size": [row["width"], row["height"]]})
    if not refs:
        raise ValueError("Select native references with the same visual role")
    for usage in contract["usages"]:
        usage["evidence"] = [archive((contract_path.parent / p), output.parent / "Sources")
                             for p in usage["evidence"]]
    # No definition kind implies a palette, silhouette style, shape, or resolution.
    prompt = (f"Create one game icon. Meaning: {contract['subject']} Composition: {contract['composition']} "
              f"Art direction from the attached native references: {contract['artDirection']} "
              f"Output aspect {contract['outputSize'][0]}:{contract['outputSize'][1]}; "
              f"transparent background: {contract['transparentBackground']}. "
              "Preserve the specified composition and visual language. Keep contours and spacing legible "
              "at every listed UI size. Do not add text or an interface frame unless explicitly requested.")
    prompt += " Display uses: " + "; ".join(
        f"{u['role']}: {u['size'][0]}x{u['size'][1]} pixels, " + ", ".join(
            f"{s['name']} tint RGBA {s['tint']} on background RGBA {s['background']}" for s in u["states"])
        for u in contract["usages"])
    brief = {"schemaVersion": 2, "status": "generation-brief", "contract": contract,
             "contractSource": archive(contract_path, output.parent / "Sources"),
             "references": refs, "prompt": prompt,
             "imageGeneration": {"prompt": prompt, "transparent_background": contract["transparentBackground"]}}
    write_json(output, brief)
    return brief


def checked_inputs(brief: dict) -> list[Path]:
    records = [brief["contractSource"]]
    for ref in brief["references"]:
        records.extend((ref, ref["blueprintCopy"]))
    for usage in brief["contract"]["usages"]:
        records.extend(usage["evidence"])
    for record in records:
        if digest(Path(record["path"])) != record["sha256"]:
            raise ValueError("A pinned reference changed; prepare a new brief")
    return [Path(record[key]).resolve() for record in records for key in ("path", "sourcePath")]


def preview(image: Image.Image, usage: dict, state: dict) -> Image.Image:
    size = dimensions(usage["size"])
    tile = Image.new("RGBA", size, rgba(state["background"]))
    layer = ImageOps.contain(image, size, Image.Resampling.LANCZOS) if usage["preserveAspect"] else image.resize(size, Image.Resampling.LANCZOS)
    layer = ImageChops.multiply(layer, Image.new("RGBA", layer.size, rgba(state["tint"])))
    tile.alpha_composite(layer, ((size[0] - layer.width) // 2, (size[1] - layer.height) // 2))
    return tile


def finish(brief_path: Path, source: Path, output: Path, review: Path) -> None:
    brief = read_json(brief_path)
    if brief.get("schemaVersion") != 2:
        raise ValueError("Prepare a brief with explicit UI contracts")
    inputs = checked_inputs(brief) + [brief_path.resolve(), source.resolve()]
    outputs = [output.resolve(), review.resolve(), review.with_suffix(".json").resolve()]
    if any(path in inputs for path in outputs) or len(set(outputs)) != len(outputs):
        raise ValueError("Preserve the generated source and pinned inputs")
    if output.suffix.lower() != ".png" or review.suffix.lower() != ".png":
        raise ValueError("Candidate and review must be PNG files")
    size = dimensions(brief["contract"]["outputSize"])
    with Image.open(source) as generated:
        if generated.width * size[1] != generated.height * size[0]:
            raise ValueError("Generated aspect must match the output; do not stretch or crop artwork")
        if brief["contract"]["transparentBackground"] and (
                "A" not in generated.getbands() or generated.getchannel("A").getextrema()[0] == 255):
            raise ValueError("This UI contract requires a transparent generation")
        icon = generated.convert("RGBA").resize(size, Image.Resampling.LANCZOS)
    if not icon.getbbox():
        raise ValueError("The candidate is empty")
    output.parent.mkdir(parents=True, exist_ok=True)
    icon.save(output)
    images = [("candidate", icon)]
    for ref in brief["references"]:
        with Image.open(ref["path"]) as image:
            images.append((ref["definitionType"] + "/" + ref["definition"], image.convert("RGBA")))
    columns = [(usage, state) for usage in brief["contract"]["usages"] for state in usage["states"]]
    measure = ImageDraw.Draw(Image.new("RGB", (1, 1)))
    label_width = max(250, *(int(measure.textlength(label)) + 24 for label, _ in images))
    widths = [max(160, dimensions(u["size"])[0] + 16,
                  int(measure.textlength(u["id"] + "/" + s["name"])) + 24) for u, s in columns]
    row_height = max(110, max(dimensions(u["size"])[1] for u, _ in columns) + 45)
    sheet = Image.new("RGB", (label_width + sum(widths), 40 + len(images) * row_height), "#202329")
    draw = ImageDraw.Draw(sheet)
    x = label_width
    for (usage, state), width in zip(columns, widths):
        draw.text((x, 8), usage["id"] + "/" + state["name"], fill="white")
        x += width
    for index, (label, image) in enumerate(images):
        y, x = 40 + index * row_height, label_width
        draw.text((8, y + 8), label, fill="white")
        for (usage, state), width in zip(columns, widths):
            tile = preview(image, usage, state)
            sheet.paste(tile.convert("RGB"), (x + 8, y + 25))
            draw.text((x + 8, y + 7), f"{tile.width}x{tile.height}", fill="white")
            x += width
    review.parent.mkdir(parents=True, exist_ok=True)
    sheet.save(review)
    write_json(review.with_suffix(".json"), {"status": "candidate-requires-visual-and-native-ui-review",
        "brief": str(brief_path.resolve()), "briefSha256": digest(brief_path),
        "source": str(source.resolve()), "sourceSha256": digest(source),
        "output": str(output.resolve()), "outputSha256": digest(output),
        "usages": brief["contract"]["usages"]})


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    inventory = commands.add_parser("catalog")
    inventory.add_argument("--blueprint-root", type=Path, default=BLUEPRINTS)
    inventory.add_argument("--output", type=Path, required=True)
    capture = commands.add_parser("capture-source")
    capture.add_argument("--spec", type=Path, required=True)
    capture.add_argument("--output", type=Path, required=True)
    brief = commands.add_parser("prepare")
    for name in ("references", "contract", "output"):
        brief.add_argument("--" + name, type=Path, required=True)
    final = commands.add_parser("finish")
    for name in ("brief", "source", "output", "review"):
        final.add_argument("--" + name, type=Path, required=True)
    args = parser.parse_args()
    if args.command == "catalog":
        catalog(args.blueprint_root, args.output)
    elif args.command == "capture-source":
        capture_source(args.spec, args.output)
    elif args.command == "prepare":
        prepare(args.references, args.contract, args.output)
    else:
        finish(args.brief, args.source, args.output, args.review)


if __name__ == "__main__":
    main()
