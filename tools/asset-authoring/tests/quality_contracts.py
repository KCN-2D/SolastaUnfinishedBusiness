"""Exercise quality-profile acceptance and rejection using real Blender exports/renders."""
import argparse
import copy
import json
from pathlib import Path
from typing import Any
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
            devices: list[SimpleNamespace]

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
        bpy: Any = ModuleType("bpy")
        bpy.app = SimpleNamespace(background=background, version=version, version_string="4.5.9 LTS",
                                  use_userpref_skip_save_on_exit=True)
        bpy.context = SimpleNamespace(preferences=SimpleNamespace(
            use_preferences_save=True, addons={"cycles": SimpleNamespace(preferences=preferences)}))
        cycles: Any = ModuleType("_cycles")
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
    originals: dict[str, Any] = {name: sys.modules.get(name, missing) for name in ("bpy", "_cycles")}
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
            request: dict[str, Any] = {"backend": "OPTIX", "deviceId": "gpu-0"}
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
    native_surface_contracts(checks)
    hair_core_contracts(checks)


def native_surface_contracts(checks):
    """Keep physical cuts, source frames and packed slopes under one numeric contract."""
    import numpy as np

    points=[(x / 2, y / 2, 0.) for y in range(3) for x in range(3)]
    triangles=[]
    for y in range(2):
        for x in range(2):
            a=y * 3 + x
            triangles.extend((a, a+1, a+4, a, a+4, a+3))
    packet: dict[str, Any]=dict(vertices=[v for point in points for v in point],
                weights=[v for _ in points for v in (1., 0., 0., 0.)],
                boneIndices=[0] * (len(points) * 4),
                uv=[dict(channel=0, values=[v for x,y,_ in points for v in (x,y,0.,0.)])],
                subMeshes=[triangles], shapes=[dict(name="Native", frames=[dict(vertices=[0.] * (len(points) * 3))])])
    skin: dict[str, Any]=dict(rendererToPrefab=np.eye(4).reshape(-1).tolist(),
              materials=[dict(properties=[dict(name="_BumpMap", scale=[1.,1.], offset=[0.,0.])])])
    sampler: dict[str, Any]=dict(wrapU="Repeat", wrapV="Repeat")
    before=copy.deepcopy(packet)
    ownership,_,report,covered,edges=pipeline.native_surface_uv_ownership(packet,skin,0,(64,64),.2,sampler)
    pipeline.require(packet == before and report["trueOpenBoundaryGroups"] == 8 and
                     ownership[31:33,31:33].min() > .9 and covered.any(),
                     "Native surface ownership changed its source or lost the interior")
    checks.append(dict(name="native UV ownership keeps source data and physical open cuts",passed=True))

    split=copy.deepcopy(packet)
    for index in (1,4,7):
        split["vertices"].extend(packet["vertices"][index*3:index*3+3])
        split["weights"].extend(packet["weights"][index*4:index*4+4])
        split["boneIndices"].extend(packet["boneIndices"][index*4:index*4+4])
        split["uv"][0]["values"].extend(packet["uv"][0]["values"][index*4:index*4+4])
        split["shapes"][0]["frames"][0]["vertices"].extend((0.,0.,0.))
    aliases={1:9,4:10,7:11}
    for face in range(0,len(triangles),3):
        selected=triangles[face:face+3]
        if max(points[v][0] for v in selected) <= .5:
            continue
        split["subMeshes"][0][face:face+3]=[aliases.get(v,v) for v in selected]
    seamed=pipeline.native_surface_uv_ownership(split,skin,0,(64,64),.2,sampler)[0]
    pipeline.require(np.array_equal(ownership,seamed), "A native duplicate UV seam became a false open cut")
    changed_frame=copy.deepcopy(split)
    changed_frame["shapes"][0]["frames"][0]["vertices"][10*3+2]=.01
    disconnected=pipeline.native_surface_uv_ownership(changed_frame,skin,0,(64,64),.2,sampler)[0]
    pipeline.require(disconnected[31:33,31:33].max() < .1,
                     "Different native physical frames were incorrectly welded")
    checks.append(dict(name="native UV seam weld requires all physical position frames",passed=True))

    repeated=copy.deepcopy(packet)
    for index in range(0,len(repeated["uv"][0]["values"]),4):
        repeated["uv"][0]["values"][index]+=1
    periodic=pipeline.native_surface_uv_ownership(repeated,skin,0,(64,64),.2,sampler)[0]
    pipeline.require(np.array_equal(ownership,periodic), "Declared Repeat UV ownership changed after one full tile")
    try:
        pipeline.native_surface_uv_ownership(repeated,skin,0,(64,64),.2,dict(wrapU="Clamp",wrapV="Clamp"))
    except RuntimeError:
        pass
    else:
        raise RuntimeError("Out-of-chart native UV was accepted without its Repeat sampler")
    checks.append(dict(name="native UV ownership honours the actual periodic sampler",passed=True))

    protected=np.zeros((64,64),dtype=bool)
    pipeline.protect_bilinear_uv_edges(protected,edges)
    refined=ownership.copy();refined[protected]=0
    pipeline.require(refined[0].max() == refined[-1].max() == 0 and
                     refined[:,0].max() == refined[:,-1].max() == 0 and refined.max() > .9,
                     "Continuous native cut bilinear taps were not protected")
    checks.append(dict(name="continuous native cut edges protect all bilinear endpoint taps",passed=True))

    packed=bytes((255,128,255,64,255,150,255,200,255,128,255,255))
    weights=np.array([[0.,.5,1.]])
    result=np.frombuffer(pipeline.refine_native_normal_slopes(packed,weights,1.),dtype=np.uint8).reshape(-1,4)
    red=bytearray(packed)
    for index in range(0,len(red),4):
        red[index],red[index+3]=red[index+3],255
    pipeline.require(result[0,0] == 64 and result[0,1] == 128 and
                     tuple(result[2]) == (128,128,255,255) and
                     pipeline.refine_native_normal_slopes(bytes(red),weights,1.) == result.tobytes(),
                     "Source-owned normal refinement lost a protected pixel, packed equivalence or analytic flat target")
    checks.append(dict(name="native normal refinement preserves protected RGorAG and flattens only owned slopes",passed=True))
    for name,mask,reduction in (("wrong dimensions",np.zeros((2,2)),.35),
                                ("nonfinite ownership",np.array([[0.,float("nan"),1.]]),.35),
                                ("out-of-range ownership",np.array([[0.,2.,1.]]),.35),
                                ("Boolean reduction",weights,True),
                                ("out-of-range reduction",weights,1.1)):
        try:
            pipeline.refine_native_normal_slopes(packed,mask,reduction)
        except RuntimeError:
            checks.append(dict(name="native normal refinement rejects " + name,passed=True))
        else:
            raise RuntimeError("Invalid native normal refinement accepted: " + name)


def hair_core_contracts(checks):
    """Exercise the real hair partition owner without an Editor or asset export."""
    import ast
    import collections
    import math
    import numpy as np

    owner = Path(__file__).resolve().parents[2] / "blender-models/CharacterHair/GenerateFlowCoverageSweptProfile.py"
    tree = ast.parse(owner.read_text(encoding="utf-8-sig"))
    names = {"physical_core_coordinates", "plan_curved_core", "evaluate_curved_core", "transport_curved_core", "bound_curved_core", "couple_fine_to_core", "prepare_geometry_frames", "share_core_normals", "curved_core_groups", "native_position_frames"}
    definitions: list[ast.stmt] = [node for node in tree.body if
                   isinstance(node, ast.FunctionDef) and node.name in names or
                   isinstance(node, ast.Assign) and any(isinstance(t, ast.Name) and t.id == "unit" for t in node.targets)]
    scope: dict[str, Any] = dict(np=np, math=math, collections=collections)
    exec(compile(ast.Module(body=definitions, type_ignores=[]), str(owner), "exec"), scope)
    partition, evaluate = scope["plan_curved_core"], scope["evaluate_curved_core"]

    def check(name, condition):
        pipeline.require(condition, "Curved hair core: " + name)
        checks.append(dict(name="curved hair core " + name, passed=True))

    rounded=scope['native_position_frames'](dict(vertices=[1.+2**-25,0.,0.],
        shapes=[dict(frames=[dict(vertices=[.5,0.,0.])])]),
        [1.,0.,0.,0.,0.,1.,0.,0.,0.,0.,1.,0.,1.,0.,0.,1.])
    check("uses Unity binary32 source coordinates before selecting geometric drivers",
          np.array_equal(rounded[:,0,0],np.array((2.,2.5))))

    q = np.array(((0., 0.), (.012, 0.), (.012, .016), (0., .016)))
    p = np.c_[q, np.zeros(4)]
    n = np.tile((0., 0., 1.), (4, 1))
    t = np.tile((0., 1., 0.), (4, 1))
    faces = np.array(((0, 1, 2), (0, 2, 3)))
    selected = [np.ones(2, dtype=bool)]
    original = q.copy(), faces.copy()
    plan = partition(q, [faces], selected, .004, 2000)
    points, normals, tangents = evaluate(plan, p, n, t, q)
    area = collections.defaultdict(float)
    for triangle, parent in zip(plan["triangles"][0], plan["parents"][0]):
        corners = faces[parent]
        bary = []
        for index in triangle:
            record = plan["vertices"][index]
            weights = dict(zip(record["triangle"], record["barycentric"]))
            bary.append([weights.get(int(v), 0.) for v in corners])
        bary = np.asarray(bary)
        area[parent] += np.linalg.det(np.stack((bary[1, 1:] - bary[0, 1:], bary[2, 1:] - bary[0, 1:]))) * .5
    check("covers every source face without keeping the broad render face",
          set(area) == {0, 1} and max(abs(v - .5) for v in area.values()) < 1e-10 and
          len(plan["triangles"][0]) > len(faces) and plan["maximumSourceAreaError"] < 1e-10)
    coords = np.asarray([np.asarray(v["barycentric"]) @ q[v["triangle"]] for v in plan["vertices"]])
    boundary = np.minimum.reduce((coords[:, 0], .012 - coords[:, 0], coords[:, 1], .016 - coords[:, 1])) < 1e-10
    check("keeps cut positions and makes genuine bounded transverse curvature",
          np.max(np.abs(points[boundary] - np.c_[coords[boundary], np.zeros(boundary.sum())])) < 1e-12 and
          .0002 < points[:, 2].max() <= .00035 and points[:, 2].min() >= -1e-12)
    rendered = np.asarray(plan["triangles"][0])
    cross = np.cross(points[rendered[:, 1]] - points[rendered[:, 0]], points[rendered[:, 2]] - points[rendered[:, 0]])
    check("keeps winding and orthogonal smooth frames",
          np.all(cross[:, 2] > 0.) and np.max(abs(np.linalg.norm(normals, axis=1) - 1.)) < 1e-12 and
          np.max(abs(np.sum(normals * tangents, axis=1))) < 1e-12)
    check("does not mutate authoritative source arrays", np.array_equal(q, original[0]) and np.array_equal(faces, original[1]))

    # A lifted support must carry existing fine fibers, including their source
    # chart orientation. A nearby unselected material face remains independent.
    fiber_bary = np.array(((.4, .35, .25), (.35, .4, .25), (.35, .35, .3)))
    fibers = fiber_bary @ p[faces[0]] + (0., 0., .0001)
    fine_records = [dict(native=False, slot=0, surface=dict(triangle=faces[0].tolist(), barycentric=b.tolist())) for b in fiber_bary]
    fine_records.append(dict(native=False, slot=1, surface=dict(triangle=faces[0].tolist(), barycentric=fiber_bary[0].tolist())))
    all_fibers = np.vstack((fibers, fibers[:1]))
    support = np.c_[coords, np.full(len(coords), .001)]
    ids, attached, attached_n, attached_t = scope["couple_fine_to_core"](
        plan, support, np.tile((0., 0., 1.), (len(coords), 1)), np.tile((0., 1., 0.), (len(coords), 1)),
        all_fibers, np.tile((0., 0., 1.), (4, 1)), np.tile((0., 1., 0.), (4, 1)), fine_records,
        p, n, t, np.cross(n, t))
    check("carries attached fine fibers above lifted support and preserves foreign material ownership",
          np.array_equal(ids, np.arange(3)) and np.max(abs(attached - (fibers + (0., 0., .001)))) < 1e-9 and
          np.max(abs(attached_n-(0., 0., 1.))) < 1e-12 and np.max(abs(attached_t-(0., 1., 0.))) < 1e-12 and
          np.array_equal(all_fibers[:3], fibers))

    reuse = partition(q, [faces], selected, .004, plan["allocatedVertices"] - 4, range(4))
    check("reuses unchanged native cut corners within the fixed vertex budget",
          reuse["allocatedVertices"] == plan["allocatedVertices"] - 4 and reuse["reusedNativeVertices"] == 4 and
          sorted(v["nativeVertex"] for v in reuse["vertices"] if "nativeVertex" in v) == list(range(4)))
    try:
        partition(q, [faces], selected, .004, reuse["allocatedVertices"] - 1, range(4))
    except OverflowError:
        check("rejects an insufficient budget before producing an asset", True)
    else:
        raise RuntimeError("Curved hair core exceeded its vertex budget")

    # Coincident UV-cut vertices and material slots do not share render ownership.
    split_q = np.vstack((q, q))
    separate = partition(split_q, [np.vstack((faces[:1], faces[1:] + 4))], selected, .004, 2000)
    material = partition(q, [faces[:1], faces[1:]], [np.ones(1, bool), np.ones(1, bool)], .004, 2000)
    check("keeps UV and material cuts separate",
          len(separate["components"]) == len(material["components"]) == 2 and
          all(set(v["triangle"]) <= set(range(4)) or set(v["triangle"]) <= set(range(4, 8)) for v in separate["vertices"]))
    # Equivalent geometry on two UV charts must bend at their common edge.
    # The texture corners remain distinct and use exactly matching edge splits.
    alias_ids=np.array((0,1,2,0,2,3))
    alias_p=p[alias_ids];alias_q=q[alias_ids]
    alias_n=scope["unit"](np.array(((-.5,0.,1.),(0.,0.,1.),(.5,0.,1.),(-.5,0.,1.),(.5,0.,1.),(0.,0.,1.))))
    alias_faces=np.array(((0,1,2),(3,4,5)))
    shared=partition(alias_q,[alias_faces],[np.ones(2,bool)],.004,2000,range(6),positions=alias_p,physical_groups=alias_ids)
    shared_p,shared_n,shared_t=evaluate(shared,alias_p,alias_n,np.tile((0.,1.,0.),(6,1)),alias_q)
    mids=[i for i,v in enumerate(shared['vertices'])if sorted(int(alias_ids[j])for j,w in zip(v['triangle'],v['barycentric'])if w>1e-10)==[0,2]]
    check("curves a physical edge across independent texture charts without opening its alias",
          len(mids)==2 and np.linalg.norm(shared_p[mids[0]]-shared_p[mids[1]])<1e-12 and
          shared_p[mids[0],2]>.0001 and len({tuple(shared['vertices'][i]['triangle'])for i in mids})==2)
    scope['prepare_geometry_frames'](shared,alias_p)
    normal_poses=np.stack((alias_n,alias_n.copy()))
    normal_poses[1,3:]*=-1
    tangent_poses=np.tile((0.,1.,0.),(2,6,1))
    transported=scope['transport_curved_core'](shared_p,shared_n,shared_t,shared['vertices'],
        np.stack((alias_p,alias_p)),normal_poses,tangent_poses,np.cross(normal_poses,tangent_poses))
    check("keeps UV aliases closed when a physical morph changes only shading normals",
          np.max(abs(transported[0][1]-transported[0][0]))<1e-12 and
          np.linalg.norm(transported[0][1,mids[0]]-transported[0][1,mids[1]])<1e-12)
    moving=alias_p.copy();moving[[2,4],2]+=.002
    transported=scope['transport_curved_core'](shared_p,shared_n,shared_t,shared['vertices'],
        np.stack((alias_p,moving)),normal_poses,tangent_poses,np.cross(normal_poses,tangent_poses))
    check("shares one geometric driver across a moving physical edge and independent UVs",
          np.linalg.norm(transported[0][1,mids[0]]-transported[0][1,mids[1]])<1e-12 and
          np.linalg.norm(transported[0][1,mids[0]]-transported[0][0,mids[0]])>.0005)
    empty = partition(q, [faces], [np.zeros(2, bool)], .004, 0)
    check("leaves an unselected metal or foreign domain untouched",
          not empty["vertices"] and empty["triangles"] == [[]] and
          all(a.shape == (0, 3) for a in evaluate(empty, p, n, t, q)))

    distorted = q @ np.array(((1000., 120.), (0., 8.))) + (734., -97.)
    fixed, reports = scope["physical_core_coordinates"](p, distorted, [faces], selected)
    physical_edges = p[faces[:, 1]] - p[faces[:, 0]]
    fixed_edges = fixed[faces[:, 1]] - fixed[faces[:, 0]]
    check("derives density from physical size rather than UV offsets or stretch",
          np.max(abs(np.linalg.norm(physical_edges, axis=1) - np.linalg.norm(fixed_edges, axis=1))) < 1e-10 and
          abs(fixed).max() < .02 and reports[0]["strandAxisPreserved"])

    narrow_p = np.array(((0., 0., 0.), (.0005, 0., 0.), (.0005, .02, 0.), (0., .02, 0.)))
    narrow_q = narrow_p[:, :2] * (20., 1.)
    narrow_n = scope["unit"](np.array(((0., 0., 1.), (.9, 0., -.1), (0., 0., 1.), (-.9, 0., .1))))
    narrow_t = np.tile((0., 1., 0.), (4, 1))
    narrow = partition(narrow_q, [faces], selected, .004, 2000)
    curved = evaluate(narrow, narrow_p, narrow_n, narrow_t, narrow_q)[0]
    narrow_indices = np.asarray(narrow["triangles"][0])
    narrow_cross = np.cross(curved[narrow_indices[:, 1]] - curved[narrow_indices[:, 0]],
                            curved[narrow_indices[:, 2]] - curved[narrow_indices[:, 0]])
    check("bounds curvature by narrow physical faces with rapidly turning normals",
          np.all(narrow_cross[:, 2] > 0.) and np.max(abs(curved[:, 2])) <= .00012)
    wide_p=np.array(((0.,0.,0.),(.04,0.,0.),(.04,.04,0.),(0.,.04,0.),(-.00001,.02,0.)))
    wide_faces=np.array(((0,1,2),(0,2,3),(3,0,4)))
    wide=partition(wide_p[:,:2],[wide_faces],[np.ones(3,bool)],.006,2000)
    wide_curved=evaluate(wide,wide_p,np.tile((0.,0.,1.),(5,1)),np.tile((0.,1.,0.),(5,1)),wide_p[:,:2])[0]
    check("keeps broad-face curvature when a neighbouring chart face is very thin",wide_curved[:,2].max()>.0002)

    source_p = np.stack((p, p + np.array(((0., 0., 0.), (0., 0., .002), (0., 0., .004), (0., 0., 0.)))))
    source_n, source_t = np.stack((n, n)), np.stack((t, t))
    source_b = np.cross(source_n, source_t)
    flat = scope["transport_curved_core"](points, normals, tangents, plan["vertices"], source_p, source_n, source_t,
                                         source_b, np.zeros(len(points)))[0]
    exact = np.stack([np.asarray(v["barycentric"]) @ source_p[1, v["triangle"]] for v in plan["vertices"]])
    check("transports morphs from the entire source triangle rather than one anchor",
          np.max(abs(flat[1] - exact)) < 1e-9)
    severe = points.copy()
    severe[~boundary, 0] += .02
    bounded_p, _, _, proof = scope["bound_curved_core"](plan, severe, normals, tangents, source_p, source_n,
                                                        source_t, source_b, np.eye(3))
    check("uses a finite chart bound and checks every serialized morph",
          1 < proof["attempts"] <= proof["maximumAttempts"] == 14 and proof["reducedCharts"] > 0 and
          proof["finalFailedSamples"] == 0 and bounded_p.shape == (2, len(points), 3))


def extremity_surface_contracts(checks):
    """Exercise native cuts, skin ownership and frame-preserving curvature."""
    import importlib.util
    import numpy as np

    owner = Path(__file__).resolve().parents[2] / "blender-models/CharacterFaces/build_models.py"
    spec = importlib.util.spec_from_file_location("extremity_model_owner", owner)
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    setattr(module, "np", np)
    model, _ = module.source_owner()
    grid = np.array([(x, y) for y in np.linspace(-.06, .06, 7) for x in np.linspace(-.06, .06, 7)])
    points = np.c_[grid, .012 * (1. - np.sum((grid / .06) ** 2, axis=1))]
    normals = np.c_[2. * .012 * grid / .06 ** 2, np.ones(len(grid))]
    normals /= np.linalg.norm(normals, axis=1)[:, None]
    triangles = []
    for y in range(6):
        for x in range(6):
            a = y * 7 + x
            triangles.extend((a, a + 1, a + 7, a + 1, a + 8, a + 7))
    packet: dict[str, Any] = dict(name="CurvedNativeExtremity", nativeReadable=True, vertexCount=len(points), indexFormat=0,
                  vertices=points.ravel().tolist(), normals=normals.ravel().tolist(),
                  tangents=np.tile([1., 0., 0., 1.], (len(points), 1)).ravel().tolist(), colors=[],
                  weights=np.tile([1., 0., 0., 0.], (len(points), 1)).ravel().tolist(),
                  boneIndices=[0] * (len(points) * 4), bindPoses=np.tile(np.eye(4).ravel(), 2).tolist(),
                  subMeshes=[triangles], uv=[dict(channel=0, dimension=2, values=np.c_[grid, np.zeros((len(points), 2))].ravel().tolist())],
                  shapes=[dict(index=0, name="NativeBody", frames=[dict(weight=100., vertices=np.tile([0., 0., .001], (len(points), 1)).ravel().tolist(),
                                                                 normals=[0.] * (len(points) * 3), tangents=[0.] * (len(points) * 3))])],
                  bounds=[0., 0., 0., .12, .12, .12])
    packet["sourceFingerprint"] = module.fingerprint(packet)[0]
    native = copy.deepcopy(packet)
    result, added, metrics = model.refine_extremity_packet(packet, np.eye(4).ravel().tolist(), 0)
    pipeline.require(packet == native and len(added) > 0, "Extremity refinement changed its source or failed to refine curvature")
    checks.append(dict(name="extremity refinement retains immutable source and refines physical curvature", passed=True))
    pipeline.require(all(result[key][:len(packet[key])] == packet[key] for key in
                         ("vertices", "normals", "tangents", "weights", "boneIndices")), "An extremity changed a native channel")
    pipeline.require(metrics["originalBoundaryLoopSamplesAndEdgesExact"] and metrics["allChildFacesRetainParentOrientation"],
                     "An extremity changed an open cut or reversed a source face")
    checks.append(dict(name="extremity native cuts and child orientation survive", passed=True))
    pipeline.require(metrics["physicalFramesOrientationValidated"] == 1 and result["shapes"][0]["frames"][0]["vertices"][:len(points) * 3] == packet["shapes"][0]["frames"][0]["vertices"],
                     "An extremity lost native physical frame samples")
    checks.append(dict(name="extremity physical frame prefix and all-pose orientation survive", passed=True))
    mismatched = copy.deepcopy(packet)
    for key, width in (("vertices", 3), ("normals", 3), ("tangents", 4), ("weights", 4), ("boneIndices", 4)):
        mismatched[key].extend(packet[key][:width])
    mismatched["boneIndices"][-4] = 1
    mismatched["uv"][0]["values"].extend(packet["uv"][0]["values"][:4])
    mismatched["vertexCount"] += 1
    for frame in mismatched["shapes"][0]["frames"]:
        for key in ("vertices", "normals", "tangents"):
            frame[key].extend(frame[key][:3])
    for label, source, matrix, lod in (("coincident cut with different skin", mismatched, np.eye(4).ravel().tolist(), 0),
                                        ("invalid native LOD", packet, np.eye(4).ravel().tolist(), 3),
                                        ("singular renderer transform", packet, [0.] * 16, 0)):
        try:
            model.refine_extremity_packet(source, matrix, lod)
        except ValueError:
            checks.append(dict(name="extremity rejects " + label, passed=True))
        else:
            raise AssertionError("Extremity accepted " + label)


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
    profile: dict[str, Any] = {"schema_version": 1, "required_stages": ["generate", "verify-static"],
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
    native_surface_contracts(checks)
    hair_core_contracts(checks)
    extremity_surface_contracts(checks)
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
    parser.add_argument("--stage", choices=("verify-contract", "verify-normals", "verify-devices", "verify-surfaces"), required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args(sys.argv[sys.argv.index("--") + 1:])
    output = pipeline.safe_output(args.output)
    report: dict[str, Any] = {"stage": args.stage, "status": "failed"}
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
        elif args.stage == "verify-surfaces":
            checks = []
            native_surface_contracts(checks)
            hair_core_contracts(checks)
            extremity_surface_contracts(checks)
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
