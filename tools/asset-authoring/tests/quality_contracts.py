"""Exercise quality-profile acceptance and rejection using real Blender exports/renders."""
import argparse
import copy
import json
from pathlib import Path
import sys
import traceback

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import blender_pipeline as pipeline


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
    parser.add_argument("--stage", choices=("verify-contract",), required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args(sys.argv[sys.argv.index("--") + 1:])
    output = pipeline.safe_output(args.output)
    report = {"stage": args.stage, "status": "failed"}
    try:
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
