"""Exercise quality-profile acceptance and rejection using real Blender exports/renders."""
import argparse
import copy
import json
from pathlib import Path
import sys
import tempfile
import traceback
from types import ModuleType, SimpleNamespace

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import blender_pipeline as pipeline


def device_contracts(output, checks):
    """Test the common device boundary without querying a native driver."""
    def check(name, condition):
        pipeline.require(condition, name)
        checks.append({"name": "cycles device " + name, "passed": True})

    def fixture(background=True, version=(4, 5, 9), flags=(True, True, True, False, True, True),
                backend_accept=True, device_mode="normal"):
        calls = []
        devices = [SimpleNamespace(id="gpu-0", name="Example GPU", type="OPTIX", use=False),
                   SimpleNamespace(id="gpu-1", name="Another GPU", type="OPTIX", use=True),
                   SimpleNamespace(id="CPU", name="Example CPU", type="CPU", use=True)]
        if device_mode == "duplicate":
            devices.append(SimpleNamespace(**vars(devices[0])))

        class Preferences:
            compute_device_type = "NONE"

            def get_device_list(self, backend):
                pipeline.require(not bpy.context.preferences.use_preferences_save,
                                 "Automatic preference saving was not disabled before enumeration")
                calls.append(("list", backend))
                return [("native",)]

            def get_devices_for_type(self, backend, device_list):
                pipeline.require(device_list == [("native",)], "Selected backend results were not reused")
                calls.append(("entries", backend))
                return self.devices

        preferences = Preferences()
        preferences.devices = devices
        bpy = ModuleType("bpy")
        bpy.app = SimpleNamespace(background=background, version=version, version_string="4.5.9 LTS",
                                  use_userpref_skip_save_on_exit=True)
        bpy.context = SimpleNamespace(preferences=SimpleNamespace(
            use_preferences_save=True, addons={"cycles": SimpleNamespace(preferences=preferences)}))
        cycles = ModuleType("_cycles")
        cycles.get_device_types = lambda: flags

        def override(backend):
            calls.append(("override", backend))
            return backend_accept

        cycles.set_device_override = override
        sys.modules["bpy"] = bpy
        sys.modules["_cycles"] = cycles
        return bpy, preferences, calls

    def rejected(name, request, path, **kwargs):
        fixture(**kwargs)
        try:
            pipeline.configure_cycles_device(request, path)
        except (RuntimeError, FileExistsError):
            pass
        else:
            raise RuntimeError(name + " did not reject")
        check(name, not path.exists())

    missing = object()
    originals = {name: sys.modules.get(name, missing) for name in ("bpy", "_cycles")}
    try:
        with tempfile.TemporaryDirectory(prefix="cycles-device-contract-", dir=output) as temporary:
            tmp = Path(temporary)
            pipeline.require(tmp.resolve().is_relative_to(output.resolve()), "Device test directory escaped output")
            bpy, preferences, calls = fixture()
            path = tmp / "accepted.json"
            result = pipeline.configure_cycles_device({"backend": "OPTIX", "deviceId": "gpu-0"}, path)
            check("only the explicit native GPU remains enabled", [(d.id, d.use) for d in preferences.devices] ==
                  [("gpu-0", True), ("gpu-1", False), ("CPU", False)])
            check("only the requested backend was queried", calls ==
                  [("list", "OPTIX"), ("entries", "OPTIX"), ("override", "OPTIX")])
            check("automatic saving stays disabled until process exit", not bpy.context.preferences.use_preferences_save)
            check("scene API not required or modified", not hasattr(bpy.context, "scene"))
            check("receipt owns exact request and observed ID", result["request"] ==
                  {"backend": "OPTIX", "deviceId": "gpu-0"} and result["selectedDevices"][0]["id"] == "gpu-0")
            check("configuration does not claim completed GPU rendering", result["actualGpuRenderVerified"] is False)
            original = path.read_bytes()
            try:
                pipeline.configure_cycles_device({"backend": "OPTIX", "deviceId": "gpu-0"}, path)
            except FileExistsError:
                pass
            else:
                raise RuntimeError("Existing receipt was overwritten")
            check("existing evidence is never overwritten", path.read_bytes() == original)
            request = {"backend": "OPTIX", "deviceId": "gpu-0"}
            cases = [
                ("missing backend", {"deviceId": "gpu-0"}, {}),
                ("missing device ID", {"backend": "OPTIX"}, {}),
                ("unknown fields", dict(request, allowFallback=True), {}),
                ("empty ID", dict(request, deviceId=""), {}),
                ("non-string ID", dict(request, deviceId=1), {}),
                ("unavailable ID", dict(request, deviceId="unavailable"), {}),
                ("CPU ID cannot be selected as GPU", dict(request, deviceId="CPU"), {}),
                ("hybrid backend is not accepted", dict(request, backend="OPTIX+CPU"), {}),
                ("unknown backend", dict(request, backend="UNKNOWN"), {}),
                ("non-background process", request, {"background": False}),
                ("unexpected Blender version", request, {"version": (4, 4, 0)}),
                ("backend not compiled", request, {"flags": (True, False, True, False, True, True)}),
                ("unexpected native capability signature", request, {"flags": (True, True)}),
                ("duplicate native identity", request, {"device_mode": "duplicate"}),
                ("native override rejection", request, {"backend_accept": False}),
            ]
            for index, (name, value, kwargs) in enumerate(cases):
                rejected(name, value, tmp / ("rejected-" + str(index) + ".json"), **kwargs)
    finally:
        for name, original in originals.items():
            if original is missing:
                sys.modules.pop(name, None)
            else:
                sys.modules[name] = original
        pipeline.require(all((name not in sys.modules) if original is missing else
                             sys.modules.get(name) is original for name, original in originals.items()),
                         "Device tests did not restore imported native module ownership")


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


def normal_map_contracts(checks):
    def direction(rgb):
        values = [channel / 255 * 2 - 1 for channel in rgb[:3]]
        length = pipeline.math.sqrt(sum(value * value for value in values))
        return [value / length for value in values]

    def matches(name, rgba, encoding, expected):
        before = bytes(rgba)
        actual = pipeline.normal_map_rgb(rgba, encoding)
        length = pipeline.math.sqrt(sum(value * value for value in expected))
        expected = [value / length for value in expected]
        pipeline.require(rgba == before and len(actual) == 4 and actual[3] == 255 and
                         max(abs(a - b) for a, b in zip(direction(actual), expected)) < .01,
                         "Normal conversion changed direction or alpha: " + name)
        checks.append({"name": "normal conversion " + name, "passed": True})

    for name, rgba, expected in (
        ("positive X", bytes((255, 128, 128, 0)), (1, 0, 0)),
        ("negative X", bytes((0, 128, 128, 77)), (-1, 0, 0)),
        ("positive Y", bytes((128, 255, 128, 128)), (0, 1, 0)),
        ("negative Y", bytes((128, 0, 128, 255)), (0, -1, 0)),
        ("positive Z", bytes((128, 128, 255, 0)), (0, 0, 1)),
        ("negative Z", bytes((128, 128, 0, 255)), (0, 0, -1)),
        ("RGB normalization", bytes((220, 180, 150, 0)), (185, 105, 45)),
    ):
        matches(name, rgba, "RGB", expected)
    x, y = 230 / 255 * 2 - 1, 51 / 255 * 2 - 1
    expected = (x, y, pipeline.math.sqrt(max(0, 1 - x * x - y * y)))
    matches("RG reconstruction", bytes((230, 51, 77, 0)), "RG", expected)
    matches("alpha-packed X", bytes((255, 51, 77, 230)), "AG", expected)
    matches("red-packed X", bytes((230, 51, 77, 255)), "AG", expected)
    matches("packed red-alpha product", bytes((230, 51, 77, 200)), "AG",
            (230 / 255 * (200 / 255) * 2 - 1, y,
             pipeline.math.sqrt(max(0, 1 - (230 / 255 * (200 / 255) * 2 - 1) ** 2 - y * y))))
    ordered = bytes((255, 128, 128, 0, 128, 0, 128, 255))
    pipeline.require(pipeline.normal_map_rgb(ordered, "RGB") ==
                     pipeline.normal_map_rgb(ordered[:4], "RGB") + pipeline.normal_map_rgb(ordered[4:], "RGB"),
                     "Normal conversion changed pixel order")
    checks.append({"name": "normal conversion preserves pixel order", "passed": True})
    packed = bytes((255, 128, 128, 128))
    correct = direction(pipeline.normal_map_rgb(packed, "AG"))
    wrong = direction(pipeline.normal_map_rgb(packed, "RGB"))
    pipeline.require(abs(sum(a * b for a, b in zip(correct, wrong))) < .05,
                     "Packed-normal-as-RGB regression control did not fail")
    checks.append({"name": "packed normal mislabelled RGB changes direction", "passed": True})
    for name, rgba, encoding in (
        ("unknown encoding", packed, "AUTO"), ("missing encoding", packed, None),
        ("empty pixels", b"", "RGB"), ("incomplete pixel", packed[:3], "RGB"),
        ("numeric text", "255,128,128,255", "RGB"), ("mutable pixels", bytearray(packed), "RGB"),
    ):
        try:
            pipeline.normal_map_rgb(rgba, encoding)
        except RuntimeError:
            checks.append({"name": "normal conversion rejects " + name, "passed": True})
        else:
            raise RuntimeError("Invalid normal input accepted: " + name)


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
    device_contracts(output, checks)
    normal_map_contracts(checks)
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
    parser.add_argument("--stage", choices=("verify-contract", "verify-normals", "verify-devices"), required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args(sys.argv[sys.argv.index("--") + 1:])
    output = pipeline.safe_output(args.output)
    report = {"stage": args.stage, "status": "failed"}
    try:
        if args.stage == "verify-devices":
            checks = []
            archived_pipeline_contract(output, checks)
            device_contracts(output, checks)
            report.update(checks=checks, passed=len(checks), failed=0,
                          native_driver_queries=0, native_gpu_render="not_run")
        elif args.stage == "verify-normals":
            checks = []
            normal_map_contracts(checks)
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
