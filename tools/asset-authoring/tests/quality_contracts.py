"""Exercise quality-profile acceptance and rejection using real Blender exports/renders."""
import argparse
import copy
import json
from pathlib import Path
import sys
import traceback

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import blender_pipeline as pipeline


def archived_pipeline_contract(output, checks):
    source = Path(pipeline.__file__).resolve()
    run = json.loads((output / "run.json").read_text(encoding="utf-8-sig"))
    entries = [entry for entry in run["input_archive"]
               if Path(entry["source"]).resolve() == source]
    pipeline.require(len(entries) == 1, "Imported common pipeline is not archived")
    entry = entries[0]
    archive = Path(entry["archive"]).resolve()
    pipeline.require(archive.is_relative_to(output.resolve()) and archive.is_file() and
                     pipeline.file_hash(source).lower() == entry["sha256"].lower() and
                     pipeline.file_hash(archive).lower() == entry["sha256"].lower(),
                     "Imported common pipeline differs from its archived execution input")
    checks.append({"name": "imported common pipeline matches archived source", "passed": True,
                   "source": str(source), "sha256": pipeline.file_hash(source)})


def custom_normal_contracts(output, checks):
    archived_pipeline_contract(output, checks)
    pipeline.empty_scene("CustomNormalContract")
    mesh = pipeline.bpy.data.meshes.new("NormalContractMesh")
    mesh.from_pydata([(0, 0, 0), (1, 0, 0), (0, 1, 0), (0, 0, 1)], [],
                     [(0, 2, 1), (0, 1, 3), (0, 3, 2), (1, 2, 3)])
    mesh.update()
    item = pipeline.bpy.data.objects.new("NormalContract", mesh)
    pipeline.bpy.context.collection.objects.link(item)
    normals = [tuple(pipeline.Vector(value).normalized()) for value in
               ((-1, -2, -1), (3, -1, -1), (-1, 3, -1), (-1, -1, 3))]
    positions = [tuple(vertex.co) for vertex in mesh.vertices]
    triangles = [tuple(polygon.vertices) for polygon in mesh.polygons]

    def error_degrees(current):
        errors = []
        for loop, actual in zip(current.loops, current.corner_normals, strict=True):
            expected = pipeline.Vector(normals[loop.vertex_index])
            errors.append(pipeline.math.degrees(expected.angle(actual.vector)))
        return max(errors)

    pipeline.set_mesh_custom_normals(mesh, normals)
    pipeline.require(error_degrees(mesh) < .1, "Custom normals changed after smooth setup")
    pipeline.require(positions == [tuple(vertex.co) for vertex in mesh.vertices] and
                     triangles == [tuple(polygon.vertices) for polygon in mesh.polygons],
                     "Normal assignment changed source geometry")
    checks.append({"name": "custom normal direction and source geometry", "passed": True})
    for name, invalid in (("count", normals[:-1]), ("dimension", [(0, 1)] + normals[1:]),
                          ("nonfinite", [(float("nan"), 0, 1)] + normals[1:]),
                          ("boolean", [(True, 0, 1)] + normals[1:]),
                          ("numeric text", [("1", 0, 1)] + normals[1:])):
        before = [tuple(normal.vector) for normal in mesh.corner_normals]
        rejected = False
        try:
            pipeline.set_mesh_custom_normals(mesh, invalid)
        except RuntimeError:
            rejected = True
        pipeline.require(rejected and before == [tuple(normal.vector) for normal in mesh.corner_normals],
                         "Invalid normal data changed the mesh: " + name)
        checks.append({"name": "custom normal rejects " + name, "passed": True})
    for polygon in mesh.polygons:
        polygon.use_smooth = False
    mesh.normals_split_custom_set_from_vertices(normals)
    for polygon in mesh.polygons:
        polygon.use_smooth = True
    pipeline.require(error_degrees(mesh) > 1, "Wrong-order regression control did not fail")
    checks.append({"name": "wrong normal setup order is detected", "passed": True})
    pipeline.set_mesh_custom_normals(mesh, normals)
    source = output / "sources" / "custom-normal-contract.blend"
    source.parent.mkdir(parents=True, exist_ok=True)
    pipeline.save_source(source)
    pipeline.bpy.ops.wm.open_mainfile(filepath=str(source))
    restored = pipeline.bpy.data.objects["NormalContract"].data
    maximum = error_degrees(restored)
    pipeline.require(maximum < .1, "Saved custom normals differ after native Blender reload")
    checks.append({"name": "custom normals survive blend reload", "passed": True,
                   "maximumAngleDegrees": maximum})


def packed_texture_contracts(output, checks):
    path = output / "textures" / "packed.png"
    rgba = bytes((29, 107, 219, 0, 244, 15, 37, 255, 63, 182, 95, 7, 146, 51, 204, 128))
    receipt = pipeline.write_packed_rgba(path, 2, 2, rgba)
    image = pipeline.bpy.data.images.load(str(path), check_existing=False)
    image.colorspace_settings.name = "Non-Color"
    image.alpha_mode = "CHANNEL_PACKED"
    pipeline.require(tuple(image.size) == (2, 2) and receipt["sha256"] == pipeline.file_hash(path),
                     "Packed PNG dimensions or receipt differ")
    expected = [value / 255 for value in rgba]
    pipeline.require(max(abs(a - b) for a, b in zip(image.pixels[:], expected)) <= 1 / 255,
                     "Packed PNG changed channel values or row orientation")
    checks.append({"name": "packed RGBA and row orientation survive native Blender decode", "passed": True})
    for name, width, height, data, destination in (
        ("incomplete channels", 2, 2, rgba[:-1], path),
        ("invalid dimensions", 0, 2, b"", path),
        ("boolean dimensions", True, 2, rgba[:8], path),
        ("unsupported output", 2, 2, rgba, path.with_suffix(".jpg")),
    ):
        try:
            pipeline.write_packed_rgba(destination, width, height, data)
        except RuntimeError:
            checks.append({"name": "packed texture rejects " + name, "passed": True})
        else:
            raise RuntimeError("Invalid packed texture accepted: " + name)


def run(output):
    reports = output / "reports"
    reports.mkdir(parents=True, exist_ok=True)
    for stage, action in (("generate", pipeline.generate), ("verify-static", pipeline.verify_static)):
        result = {"stage": stage, "status": "passed", **action(output)}
        (reports / (stage + ".json")).write_text(json.dumps(result), encoding="utf-8")
    profile = {"schema_version": 1, "required_stages": ["generate", "verify-static"],
               "obj_assets": [{"path": "static/UBSampleProp/UBSampleProp.obj", "max_triangles": 100,
                               "materials": ["UBTeal", "UBFrontYellow", "UBRightOrange"],
                               "bounds_metres_y_up": {"min": [-0.30, 0, -0.22], "max": [0.42, 0.65, 0.45]}}],
               "previews": [{"path": "previews/static_" + view + ".png", "view": view,
                             "width": 384, "height": 384, "engine": "BLENDER_WORKBENCH",
                             "camera_direction": direction}
                            for view, direction in (("front", [0, -1, 0]), ("side", [1, 0, 0]),
                                                    ("top", [0, 0, 1]), ("perspective", [2.4, -4, 2.3]))]}
    profile_path = output / "quality.json"
    checks = []
    packed_texture_contracts(output, checks)
    custom_normal_contracts(output, checks)

    def audit(candidate):
        profile_path.write_text(json.dumps(candidate), encoding="utf-8")
        return pipeline.validate_quality_profile(output, profile_path)

    def rejected(name, candidate, reason):
        try:
            audit(candidate)
        except RuntimeError as error:
            pipeline.require(reason in str(error), "Unexpected rejection for " + name + ": " + str(error))
            checks.append({"name": name, "passed": True, "reason": str(error)})
        else:
            raise RuntimeError("Incomplete quality requirements were accepted: " + name)

    measured = audit(profile)
    pipeline.require(len(measured["obj_assets_checked"]) == 1 and len(measured["previews_checked"]) == 4,
                     "Positive profile did not check the actual asset and every view")
    checks.append({"name": "real exported geometry/materials and four captured views", "passed": True})
    for name, edit, reason in (
        ("missing stage", lambda p: p["required_stages"].append("absent"), "missing"),
        ("dimension mismatch", lambda p: p["obj_assets"][0]["bounds_metres_y_up"]["max"].__setitem__(1, 0.5), "dimensions differ"),
        ("material mismatch", lambda p: p["obj_assets"][0]["materials"].__setitem__(0, "AbsentMaterial"), "materials differ"),
        ("triangle budget", lambda p: p["obj_assets"][0].__setitem__("max_triangles", 1), "triangle budget"),
        ("missing view", lambda p: p["previews"][0].__setitem__("path", "previews/absent.png"), "missing"),
        ("wrong view label", lambda p: p["previews"][0].__setitem__("view", "back"), "view or renderer"),
        ("wrong renderer", lambda p: p["previews"][0].__setitem__("engine", "CYCLES"), "view or renderer"),
        ("wrong camera", lambda p: p["previews"][0].__setitem__("camera_direction", [1, 0, 0]), "camera direction differs"),
        ("wrong PNG dimensions", lambda p: p["previews"][0].__setitem__("width", 385), "dimensions differ"),
        ("duplicate view file", lambda p: p["previews"].append(copy.deepcopy(p["previews"][0])), "Duplicate"),
        ("escaping path", lambda p: p["previews"][0].__setitem__("path", "../outside.png"), "escapes"),
        ("nonfinite bounds", lambda p: p["obj_assets"][0]["bounds_metres_y_up"].__setitem__("tolerance_metres", float("nan")), "finite"),
        ("unknown requirement", lambda p: p["obj_assets"][0].__setitem__("triangle_count", 1), "unknown fields"),
    ):
        candidate = copy.deepcopy(profile)
        edit(candidate)
        rejected(name, candidate, reason)
    stage_path = reports / "verify-static.json"
    original_stage = stage_path.read_bytes()
    try:
        stage_path.write_text(json.dumps({"stage": "verify-static", "status": "failed"}), encoding="utf-8")
        rejected("failed required stage", profile, "did not pass")
    finally:
        stage_path.write_bytes(original_stage)
    preview_path = output / profile["previews"][0]["path"]
    original_preview = preview_path.read_bytes()
    try:
        preview_path.write_bytes(original_preview + b"changed")
        rejected("stale captured PNG", profile, "does not match")
    finally:
        preview_path.write_bytes(original_preview)
    receipt_path = preview_path.with_suffix(".png.capture.json")
    original_receipt = receipt_path.read_bytes()
    try:
        receipt_path.unlink()
        rejected("missing capture receipt", profile, "missing")
    finally:
        receipt_path.write_bytes(original_receipt)
    for name, field in (("geometry/material-only requirements", "previews"), ("render-only requirements", "obj_assets")):
        candidate = copy.deepcopy(profile)
        candidate.pop(field)
        audit(candidate)
        checks.append({"name": name, "passed": True})
    audit(profile)
    return {"checks": checks, "passed": len(checks), "failed": 0,
            "profile": "quality.json", "positive_measurements": measured,
            "unity": "not_run", "game": "not_run"}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--stage", choices=("verify-contract", "verify-normals"), required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args(sys.argv[sys.argv.index("--") + 1:])
    output = pipeline.safe_output(args.output)
    report = {"stage": args.stage, "status": "failed"}
    try:
        if args.stage == "verify-normals":
            checks = []
            custom_normal_contracts(output, checks)
            report.update(checks=checks, passed=len(checks), failed=0)
        else:
            report.update(run(output))
        report["status"] = "passed"
    except Exception as error:
        report.update(error=str(error), traceback=traceback.format_exc())
        raise
    finally:
        (output / "reports").mkdir(parents=True, exist_ok=True)
        (output / "reports" / (args.stage + ".json")).write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
