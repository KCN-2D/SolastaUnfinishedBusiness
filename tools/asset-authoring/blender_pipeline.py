"""Small, reproducible UB OBJ and skinned-FBX samples for Blender 4.5.9 LTS.

Run each stage in a separate --background --factory-startup Blender process.
This script writes only below --output; it never installs assets into a game.
"""

import argparse
from array import array
from collections import defaultdict
import hashlib
import json
import math
from numbers import Real
from pathlib import Path
import re
import struct
import sys
import traceback
import time
import zlib

import bpy
from mathutils import Vector
from mathutils.kdtree import KDTree


VERSION = (4, 5, 9)
FPS = 24
FRAMES = tuple(range(1, 26))
PREVIEW_FRAMES = (1, 7, 13, 19, 25)
BONES = {"Root": None, "Stem": "Root", "Tip": "Stem"}
TOLERANCE = 0.0002  # metres; allows FBX floating-point/rotation round-trip error.
UV_TOLERANCE = 0.00001
NORMAL_TOLERANCE = 0.0001
MATERIAL_TOLERANCE = 0.00001


def require(condition, message):
    if not condition:
        raise RuntimeError(message)


CYCLES_GPU_BACKENDS=('CUDA','OPTIX','HIP','METAL','ONEAPI')

def _prevent_cycles_preferences_save():
    import bpy
    require(bpy.app.background, 'Transient device selection requires a background authoring process.')
    preferences=bpy.context.preferences
    before=dict(autoSave=bool(preferences.use_preferences_save),
                skipSaveOnExit=getattr(bpy.app,'use_userpref_skip_save_on_exit',None))
    # Device enumeration itself updates preference entries. Disable automatic
    # saving before that first update, and leave it disabled until process exit.
    preferences.use_preferences_save=False
    require(not preferences.use_preferences_save,'Transient preference saving was not disabled.')
    return before

def inspect_cycles_devices(backend):
    import bpy
    import _cycles
    require(tuple(bpy.app.version)==VERSION,'Cycles device selection requires Blender 4.5.9.')
    require(backend in CYCLES_GPU_BACKENDS,'An explicit supported GPU backend is required.')
    save_guard=_prevent_cycles_preferences_save()
    native_flags=_cycles.get_device_types()
    require(len(native_flags)==6,'The target Cycles backend capability signature differs.')
    available=dict(zip(('CUDA','OPTIX','HIP','METAL','ONEAPI','HIPRT'),map(bool,native_flags)))
    require(available[backend],'The installed Cycles build does not report the requested backend.')
    addon=bpy.context.preferences.addons.get('cycles')
    require(addon is not None,'The native Cycles addon is unavailable in this process.')
    preferences=addon.preferences
    # One selected-backend query. get_devices() returns None and refreshes all
    # backends; neither behavior is useful or necessary here.
    native_list=preferences.get_device_list(backend)
    entries=preferences.get_devices_for_type(backend,device_list=native_list)
    devices=[dict(id=e.id,name=e.name,type=e.type,use=bool(e.use)) for e in entries]
    require(len({(d['id'],d['type']) for d in devices})==len(devices),'Native device identity is ambiguous.')
    return dict(status='OBSERVED_PROCESS_DEVICE_METADATA_ONLY',blenderVersion=bpy.app.version_string,
                requestedBackend=backend,compiledBackendCapabilities=available,devices=devices,
                actualRenderVerified=False,preferencesSaved=False,preferenceSaveGuard=save_guard)

def configure_cycles_device(spec,receipt_path):
    import bpy
    import _cycles
    require(isinstance(spec,dict) and set(spec)=={'backend','deviceId'},'The device request must explicitly own backend and native persistent device ID.')
    backend=spec['backend'];device_id=spec['deviceId']
    require(isinstance(device_id,str) and bool(device_id),'Use a captured nonempty native device ID; names or invented IDs are not a substitute.')
    observation=inspect_cycles_devices(backend)
    selected=[d for d in observation['devices'] if d['id']==device_id and d['type']==backend]
    require(len(selected)==1,'The captured requested GPU is not uniquely available; no silent CPU fallback.')
    preferences=bpy.context.preferences.addons['cycles'].preferences
    before=dict(backend=preferences.compute_device_type,devices=[dict(id=d.id,type=d.type,use=bool(d.use)) for d in preferences.devices])
    preferences.compute_device_type=backend
    for d in preferences.devices:d.use=d.type==backend and d.id==device_id
    active=[d for d in preferences.devices if d.use]
    require(len(active)==1 and active[0].id==device_id and active[0].type==backend,'The transient device selection is not exclusive.')
    # Native documented process override also covers producers which set their
    # newly created scene.cycles.device='CPU'. It does not add unknown CLI args
    # to the producer's own strict argparse input or mutate the saved .blend.
    require(_cycles.set_device_override(backend),'Native Cycles rejected the process override.')
    receipt=dict(status='PROCESS_DEVICE_SELECTION_CONFIGURED_RENDER_PROOF_PENDING',request=spec,
                 observed=observation,originalProcessPreferences=before,
                 selectedDevices=[dict(id=d.id,name=d.name,type=d.type,use=bool(d.use)) for d in active],
                 processOverride=backend,cpuHybridRequested=False,sceneSettingsModified=False,
                 userPreferencesSaveCalled=False,driverSettingsChanged=False,
                 actualGpuRenderVerified=False,imageEquivalenceToCpuUnproven=True)
    path=Path(receipt_path)
    with path.open('x',encoding='utf-8') as stream:stream.write(json.dumps(receipt,ensure_ascii=False,indent=2)+'\n')
    return receipt


def safe_output(path, owned_paths=()):
    require(path.is_absolute(), "--output must be an absolute path")
    output = path.resolve()
    require(output.parent != output, "A filesystem root cannot be an output directory")
    reserved = {"mods", "solasta_data", "solastafiles", "unitymodmanager", "modelscache"}
    for ancestor in (output, *output.parents):
        require(ancestor.name.casefold() not in reserved,
                "Refusing an output inside a game/mod/cache directory: " + str(ancestor))
        require(not (ancestor / "Solasta.exe").is_file()
                and not (ancestor / "Solasta_Data").is_dir()
                and not (ancestor / "Mods").is_dir(),
                "Refusing an output inside a game installation: " + str(ancestor))
    # A rerun must not follow a pre-existing child junction or file symlink out of output.
    owned = ("reports", "sources", "previews", *owned_paths)
    require(all((output / relative).resolve().is_relative_to(output) for relative in owned),
            "An output child junction/symlink escapes the output directory")
    return output


def file_hash(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def write_packed_rgba(path, width, height, rgba):
    """Write bottom-up RGBA8 channels without transparency or colour transforms."""
    require(type(width) is int and type(height) is int and 0 < width <= 16384 and 0 < height <= 16384,
            "Invalid packed texture dimensions")
    require(isinstance(rgba, bytes) and len(rgba) == width * height * 4,
            "Packed texture requires exactly four bytes per pixel")
    path = Path(path)
    require(path.suffix.casefold() == ".png" and not path.is_symlink(), "Invalid packed PNG output")
    path.parent.mkdir(parents=True, exist_ok=True)
    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xffffffff)
    stride = width * 4
    rows = b"".join(b"\0" + rgba[row * stride:(row + 1) * stride] for row in range(height - 1, -1, -1))
    encoded = b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0))
    encoded += chunk(b"IDAT", zlib.compress(rows)) + chunk(b"IEND", b"")
    path.write_bytes(encoded)
    return {"path": str(path), "width": width, "height": height, "sha256": file_hash(path)}


def normal_map_rgb(rgba, source_encoding):
    """Convert RGBA8 normals to normalized XYZ RGB with opaque alpha for Unity import.

    RGB stores XYZ; RG reconstructs positive Z. AG uses Unity's packed R*A/G
    decoding, accepting both alpha-packed X and red-packed X. Pixel order is kept.
    """
    require(source_encoding in ("RGB", "RG", "AG"), "Normal source encoding must be RGB, RG or AG")
    require(isinstance(rgba, bytes) and len(rgba) > 0 and len(rgba) % 4 == 0,
            "Normal source requires complete RGBA8 pixels")
    import numpy as np
    source = np.frombuffer(rgba, dtype=np.uint8).reshape(-1, 4)
    result = np.empty(source.shape, dtype=np.uint8)
    # Bound temporary float storage independently of the source texture dimensions.
    for start in range(0, len(source), 256 * 1024):
        stop = min(start + 256 * 1024, len(source))
        pixels = source[start:stop]
        x = pixels[:, 0].astype(np.float64) / 255 * 2 - 1
        y = pixels[:, 1].astype(np.float64) / 255 * 2 - 1
        if source_encoding == "AG":
            x = pixels[:, 0].astype(np.float64) / 255 * (pixels[:, 3].astype(np.float64) / 255) * 2 - 1
        z = pixels[:, 2].astype(np.float64) / 255 * 2 - 1 if source_encoding == "RGB" else np.sqrt(np.maximum(0, 1 - x * x - y * y))
        length = np.sqrt(x * x + y * y + z * z)
        require(np.all(length > 0), "Normal source contains a zero-length direction")
        normal = np.column_stack((x, y, z)) / length[:, None]
        result[start:stop, :3] = np.clip(np.rint((normal * .5 + .5) * 255), 0, 255).astype(np.uint8)
        result[start:stop, 3] = 255
    return result.tobytes()


def inputs_report(output, paths):
    return {str(path.relative_to(output)).replace("\\", "/"): file_hash(path) for path in paths}


def empty_scene(name):
    bpy.ops.wm.read_factory_settings(use_empty=True)
    scene = bpy.context.scene
    scene.name = name
    scene.unit_settings.system = "METRIC"
    scene.unit_settings.scale_length = 1.0
    scene.unit_settings.length_unit = "METERS"
    scene.render.fps = FPS
    scene.render.fps_base = 1.0
    scene.frame_start, scene.frame_end = FRAMES[0], FRAMES[-1]
    bpy.context.preferences.filepaths.save_version = 0
    return scene


def material(name, rgb):
    item = bpy.data.materials.new(name)
    item.diffuse_color = (*rgb, 1.0)
    item.use_nodes = True
    bsdf = item.node_tree.nodes.get("Principled BSDF")
    require(bsdf is not None, "Blender Principled BSDF node is unavailable")
    bsdf.inputs["Base Color"].default_value = (*rgb, 1.0)
    bsdf.inputs["Roughness"].default_value = 0.65
    return item


def set_mesh_custom_normals(mesh, normals):
    values = [tuple(normal) for normal in normals]
    require(len(values) == len(mesh.vertices), "Custom normal count differs from mesh vertices")
    require(all(len(normal) == 3 and all(isinstance(value, Real) and not isinstance(value, bool)
                                       and math.isfinite(value) for value in normal) for normal in values),
            "Custom normals require three finite numeric components")
    for polygon in mesh.polygons:
        polygon.use_smooth = True
    mesh.normals_split_custom_set_from_vertices(values)


def mesh_object(name, vertices, faces, materials, face_materials):
    mesh = bpy.data.meshes.new(name + "Mesh")
    mesh.from_pydata(vertices, [], faces)
    mesh.update()
    require(not mesh.validate(), "Generated sample unexpectedly required mesh repair")
    item = bpy.data.objects.new(name, mesh)
    bpy.context.collection.objects.link(item)
    for mat in materials:
        mesh.materials.append(mat)
    uv = mesh.uv_layers.new(name="UVMap")
    for polygon, mat_index in zip(mesh.polygons, face_materials, strict=True):
        polygon.material_index = mat_index
        require(len(polygon.loop_indices) == 4, "Sample generator expects quads")
        for loop, coordinates in zip(polygon.loop_indices, ((0, 0), (1, 0), (1, 1), (0, 1)), strict=True):
            uv.data[loop].uv = coordinates
    # Share explicit tessellation between source evaluation and both exporters.
    triangulation = item.modifiers.new("UBExportTriangulation", "TRIANGULATE")
    triangulation.quad_method = "FIXED"
    return item


def add_box(vertices, faces, face_materials, low, high, mat_index):
    x0, y0, z0 = low
    x1, y1, z1 = high
    first = len(vertices)
    vertices.extend(((x0, y0, z0), (x1, y0, z0), (x1, y1, z0), (x0, y1, z0),
                     (x0, y0, z1), (x1, y0, z1), (x1, y1, z1), (x0, y1, z1)))
    for face in ((0, 3, 2, 1), (0, 1, 5, 4), (1, 2, 6, 5),
                 (2, 3, 7, 6), (3, 0, 4, 7), (4, 5, 6, 7)):
        faces.append(tuple(first + index for index in face))
        face_materials.append(mat_index)


def select_only(*objects):
    bpy.ops.object.select_all(action="DESELECT")
    for item in objects:
        item.select_set(True)
    bpy.context.view_layer.objects.active = objects[0]


def save_source(path):
    bpy.context.scene.frame_set(FRAMES[0])
    bpy.ops.wm.save_as_mainfile(filepath=str(path), check_existing=False)
    require(path.is_file(), "Blender did not save " + str(path))


def generate(output):
    source_dir, static_dir, rig_dir = output / "sources", output / "static" / "UBSampleProp", output / "rig"
    for directory in (source_dir, static_dir, rig_dir):
        directory.mkdir(parents=True, exist_ok=True)

    empty_scene("UBSampleProp")
    vertices, faces, indices = [], [], []
    # -Y is the authoring front. The yellow nose and orange +X arm prove the axes.
    add_box(vertices, faces, indices, (-0.30, -0.22, 0.00), (0.30, 0.22, 0.20), 0)
    add_box(vertices, faces, indices, (-0.12, -0.05, 0.20), (0.10, 0.12, 0.65), 0)
    add_box(vertices, faces, indices, (-0.15, -0.45, 0.10), (0.15, -0.22, 0.20), 1)
    add_box(vertices, faces, indices, (0.10, 0.00, 0.30), (0.42, 0.12, 0.42), 2)
    mats = [material("UBTeal", (0.06, 0.43, 0.47)), material("UBFrontYellow", (0.95, 0.70, 0.08)),
            material("UBRightOrange", (0.90, 0.22, 0.05))]
    prop = mesh_object("UBSampleProp", vertices, faces, mats, indices)
    select_only(prop)
    static_blend = source_dir / "UBSampleProp.blend"
    save_source(static_blend)
    obj_path = static_dir / "UBSampleProp.obj"
    bpy.ops.wm.obj_export(filepath=str(obj_path), check_existing=False, export_selected_objects=True,
                          forward_axis="NEGATIVE_Z", up_axis="Y", global_scale=1.0,
                          apply_modifiers=True,
                          export_uv=True, export_normals=True, export_materials=True,
                          export_triangulated_mesh=True, export_pbr_extensions=False,
                          path_mode="RELATIVE")

    empty_scene("UBWave1s")
    mats = [material("UBRigTeal", (0.07, 0.46, 0.51)), material("UBRigGold", (0.93, 0.57, 0.08))]
    vertices, faces, indices = [], [], []
    # Thirteen rings make the interpolation of neighbouring bone weights visible.
    for ring in range(13):
        z = ring / 10
        radius = 0.09 - 0.03 * (z / 1.2)
        vertices.extend(((-radius, -radius, z), (radius, -radius, z),
                         (radius, radius, z), (-radius, radius, z)))
    faces.append((3, 2, 1, 0))
    indices.append(0)
    for ring in range(12):
        for corner in range(4):
            a, b = ring * 4 + corner, ring * 4 + (corner + 1) % 4
            faces.append((a, b, b + 4, a + 4))
            indices.append(1 if ring >= 9 else 0)
    faces.append((48, 49, 50, 51))
    indices.append(1)
    skin = mesh_object("UBSampleRigMesh", vertices, faces, mats, indices)
    data = bpy.data.armatures.new("UBSampleRigSkeleton")
    rig = bpy.data.objects.new("UBSampleRig", data)
    bpy.context.collection.objects.link(rig)
    select_only(rig)
    bpy.ops.object.mode_set(mode="EDIT")
    previous = None
    for index, name in enumerate(BONES):
        bone = data.edit_bones.new(name)
        bone.head = (0, 0, index * 0.4)
        bone.tail = (0, 0, (index + 1) * 0.4)
        bone.parent = previous
        bone.use_connect = previous is not None
        previous = bone
    bpy.ops.object.mode_set(mode="OBJECT")
    modifier = skin.modifiers.new("UBSkin", "ARMATURE")
    modifier.object = rig
    skin.parent = rig
    groups = {name: skin.vertex_groups.new(name=name) for name in BONES}
    for vertex in skin.data.vertices:
        blend = max(0.0, min(2.0, (vertex.co.z - 0.2) / 0.4))
        lower, upper = math.floor(blend), math.ceil(blend)
        if lower == upper:
            groups[tuple(BONES)[lower]].add([vertex.index], 1.0, "REPLACE")
        else:
            groups[tuple(BONES)[lower]].add([vertex.index], upper - blend, "REPLACE")
            groups[tuple(BONES)[upper]].add([vertex.index], blend - lower, "REPLACE")
    for frame in FRAMES:
        phase = 2.0 * math.pi * (frame - 1) / FPS
        for name, degrees in (("Root", 4.0), ("Stem", 22.0), ("Tip", -28.0)):
            bone = rig.pose.bones[name]
            bone.rotation_mode = "XYZ"
            bone.rotation_euler = (0, 0, math.radians(degrees) * math.sin(phase))
            bone.keyframe_insert(data_path="rotation_euler", frame=frame, group=name)
    rig.animation_data.action.name = "UBWave1s"
    rig.animation_data.action.use_fake_user = True
    select_only(rig, skin)
    rig_blend = source_dir / "UBSampleRig.blend"
    save_source(rig_blend)
    fbx_path = rig_dir / "UBSampleRig.fbx"
    bpy.ops.export_scene.fbx(filepath=str(fbx_path), check_existing=False, use_selection=True,
                             object_types={"ARMATURE", "MESH"}, global_scale=1.0,
                             apply_unit_scale=True, apply_scale_options="FBX_SCALE_UNITS",
                             axis_forward="-Z", axis_up="Y", bake_space_transform=False,
                             use_mesh_modifiers=True, use_triangles=True, add_leaf_bones=False,
                             primary_bone_axis="Y", secondary_bone_axis="X",
                             use_armature_deform_only=True, armature_nodetype="NULL",
                             bake_anim=True, bake_anim_use_all_bones=True,
                             bake_anim_use_all_actions=False, bake_anim_use_nla_strips=False,
                             bake_anim_force_startend_keying=True, bake_anim_step=1.0,
                             bake_anim_simplify_factor=0.0, path_mode="RELATIVE", embed_textures=False)
    files = (static_blend, rig_blend, obj_path, obj_path.with_suffix(".mtl"), fbx_path)
    require(all(path.is_file() for path in files), "One or more exported files are missing")
    return {"artifacts_sha256": inputs_report(output, files), "unit_metres": 1.0,
            "authoring_axes": "+Z up, -Y front", "obj_axes": "+Y up, +Z front; (x,y,z)->(x,z,-y)",
            "fbx_scale_options": "FBX_SCALE_UNITS", "bone_parents": BONES,
            "action": "UBWave1s", "frames": [1, 25], "fps": FPS, "duration_seconds": 1.0}


def world_points(item):
    evaluated = item.evaluated_get(bpy.context.evaluated_depsgraph_get())
    mesh = evaluated.to_mesh()
    try:
        return [tuple(evaluated.matrix_world @ vertex.co) for vertex in mesh.vertices]
    finally:
        evaluated.to_mesh_clear()


def distance(a, b):
    return math.sqrt(sum((x - y) ** 2 for x, y in zip(a, b, strict=True)))


def surface_error(actual, expected):
    require(actual and expected, "Cannot compare an empty surface")
    require(all(math.isfinite(value) for points in (actual, expected) for point in points for value in point),
            "Cannot compare nonfinite positions")
    def directed(points, reference):
        tree = KDTree(len(reference))
        for index, point in enumerate(reference):
            tree.insert(point, index)
        tree.balance()
        return max(tree.find(point)[2] for point in points)
    return max(directed(actual, expected), directed(expected, actual))


def geometry_snapshot(item):
    """Evaluated world geometry, preserving face-corner UV/normal/material associations."""
    evaluated = item.evaluated_get(bpy.context.evaluated_depsgraph_get())
    mesh = evaluated.to_mesh(preserve_all_data_layers=True, depsgraph=bpy.context.evaluated_depsgraph_get())
    try:
        mesh.calc_loop_triangles()
        require(mesh.uv_layers.active is not None, "Mesh has no active UV layer")
        require(len(mesh.corner_normals) == len(mesh.loops), "Mesh corner normals are unavailable")
        matrix = evaluated.matrix_world
        normal_matrix = matrix.to_3x3().inverted().transposed()
        points = [tuple(matrix @ vertex.co) for vertex in mesh.vertices]
        triangles = []
        for triangle in mesh.loop_triangles:
            require(triangle.area > 1e-10, "Mesh contains a degenerate triangle")
            corners = []
            for loop in triangle.loops:
                normal = tuple((normal_matrix @ mesh.corner_normals[loop].vector).normalized())
                uv = tuple(mesh.uv_layers.active.data[loop].uv)
                require(all(math.isfinite(value) for value in (*normal, *uv)), "Nonfinite face-corner normal/UV")
                require(abs(Vector(normal).length - 1.0) < NORMAL_TOLERANCE, "Non-unit face-corner normal")
                corners.append({"position": points[mesh.loops[loop].vertex_index], "normal": normal, "uv": uv})
            require(triangle.material_index < len(mesh.materials), "Triangle has no assigned material")
            triangles.append({"corners": corners, "material": mesh.materials[triangle.material_index].name})
        materials = {}
        for mat in mesh.materials:
            require(mat is not None, "Mesh has an empty material slot")
            node = mat.node_tree.nodes.get("Principled BSDF") if mat.use_nodes else None
            colour = tuple(node.inputs["Base Color"].default_value) if node else tuple(mat.diffuse_color)
            require(all(math.isfinite(value) for value in colour), "Nonfinite material colour")
            materials[mat.name] = colour
        require(all(math.isfinite(value) for point in points for value in point), "Nonfinite mesh position")
        return {"points": points, "triangles": triangles, "materials": materials, "bounds": bounds(points)}
    finally:
        evaluated.to_mesh_clear()


def compare_geometry(actual, expected, tolerance=TOLERANCE):
    """Reject changed faces even when their vertex sets and bounds still agree."""
    require(len(actual["triangles"]) == len(expected["triangles"]), "Triangle count changed")
    require(set(actual["materials"]) == set(expected["materials"]), "Material names changed")
    material_error = max(abs(a - b) for name in expected["materials"]
                         for a, b in zip(actual["materials"][name], expected["materials"][name], strict=True))
    require(material_error < MATERIAL_TOLERANCE, "Material base colours changed")
    tree = KDTree(len(expected["points"]))
    for index, point in enumerate(expected["points"]):
        tree.insert(point, index)
    tree.balance()
    # Use source coordinates as IDs; nearest-point mapping tolerates exporter rounding.
    def point_key(point):
        _, index, error = tree.find(point)
        require(error < tolerance, "A triangle corner has no source position")
        return tuple(expected["points"][index])
    def triangle_key(triangle):
        return tuple(sorted(point_key(corner["position"]) for corner in triangle["corners"]))
    remaining = defaultdict(list)
    for triangle in expected["triangles"]:
        remaining[triangle_key(triangle)].append(triangle)
    uv_error, normal_error, corner_error = 0.0, 0.0, 0.0
    for triangle in actual["triangles"]:
        key = triangle_key(triangle)
        require(remaining[key], "Triangle connectivity changed")
        reference = remaining[key].pop()
        require(triangle["material"] == reference["material"], "Triangle material assignment changed")
        indices = []
        for corner in triangle["corners"]:
            index = min(range(3), key=lambda i: distance(corner["position"], reference["corners"][i]["position"]))
            indices.append(index)
            source = reference["corners"][index]
            corner_error = max(corner_error, distance(corner["position"], source["position"]))
            uv_error = max(uv_error, *(abs(a - b) for a, b in zip(corner["uv"], source["uv"], strict=True)))
            normal_error = max(normal_error, distance(corner["normal"], source["normal"]))
        require(len(set(indices)) == 3 and all((indices[(i + 1) % 3] - indices[i]) % 3 == 1 for i in range(3)),
                "Triangle winding changed")
    require(all(not entries for entries in remaining.values()), "A source triangle is missing")
    bounds_error = max(abs(a - b) for side in ("min", "max")
                       for a, b in zip(actual["bounds"][side], expected["bounds"][side], strict=True))
    require(corner_error < tolerance and bounds_error < tolerance, "Mesh corner positions/bounds changed")
    require(uv_error < UV_TOLERANCE, "Face-corner UV coordinates changed")
    require(normal_error < NORMAL_TOLERANCE, "Face-corner normals changed")
    return {"triangles_checked": len(actual["triangles"]), "maximum_corner_error_metres": corner_error,
            "maximum_bounds_error_metres": bounds_error, "maximum_uv_error": uv_error,
            "maximum_normal_error": normal_error, "maximum_material_colour_error": material_error}


def bounds(points):
    return {"min": [min(p[axis] for p in points) for axis in range(3)],
            "max": [max(p[axis] for p in points) for axis in range(3)]}


def single_object(kind):
    objects = [item for item in bpy.context.scene.objects if item.type == kind]
    require(len(objects) == 1, f"Expected one {kind}, got {len(objects)}")
    return objects[0]


def parse_ub_obj(path, expected_object_name=None, *, minimum_normal_dot=0.0, require_unit_uv=False):
    """Audit the shared UB OBJ/MTL contract; sample-only constraints stay at the caller."""
    expected_object_name = expected_object_name or path.stem
    require(path.with_suffix(".mtl").is_file(), "UB requires a same-stem MTL file")
    vertices, uvs, normals, faces, used_materials = [], [], [], [], set()
    current_material = None
    mtllib = None
    object_names = []
    for line in path.read_text(encoding="utf-8").splitlines():
        if not line or line.startswith("#"):
            continue
        tokens = line.split(" ")
        require(all(tokens), "UB splits on a single space; empty tokens are unsupported")
        key, values = tokens[0], tokens[1:]
        if key == "v":
            require(len(values) == 3, "OBJ vertex must have three coordinates")
            vertices.append(tuple(map(float, values)))
        elif key == "vt":
            require(len(values) == 2, "OBJ UV must have two coordinates")
            uvs.append(tuple(map(float, values)))
        elif key == "vn":
            require(len(values) == 3, "OBJ normal must have three coordinates")
            normals.append(tuple(map(float, values)))
        elif key in ("o", "mtllib", "usemtl"):
            require(len(values) == 1 and values[0].isascii() and not any(c.isspace() for c in values[0]),
                    "UB OBJ object/material/file names must be single ASCII tokens")
            if key == "o":
                object_names.append(values[0])
            elif key == "mtllib":
                mtllib = values[0]
            else:
                current_material = values[0]
                used_materials.add(current_material)
        elif key == "f":
            require(current_material is not None, "UB requires usemtl before the first face")
            require(len(values) == 3, "UB reads only the first three corners: triangulation is required")
            face = []
            for value in values:
                indices = value.split("/")
                require(len(indices) == 3 and all(indices), "Every OBJ corner needs v/vt/vn")
                triplet = tuple(map(int, indices))
                require(all(index > 0 for index in triplet), "UB needs positive absolute OBJ indices")
                face.append(triplet)
            faces.append(face)
    require(object_names == [expected_object_name], "OBJ must have exactly one object named " + expected_object_name)
    require(mtllib == path.with_suffix(".mtl").name, "OBJ mtllib must name the same-stem local MTL")
    require(vertices and uvs and normals and faces, "OBJ needs nonempty vertices, UVs, normals and triangles")
    require(all(math.isfinite(n) for points in (vertices, uvs, normals) for p in points for n in p),
            "OBJ contains nonfinite coordinates")
    require(all(abs(math.sqrt(sum(n * n for n in normal)) - 1.0) < 0.001 for normal in normals),
            "OBJ normals must be unit length")
    if require_unit_uv:
        require(all(0 <= value <= 1 for uv in uvs for value in uv), "Sample UV is outside [0,1]")
    for face in faces:
        for triplet in face:
            require(all(index <= count for index, count in zip(triplet, (len(vertices), len(uvs), len(normals)), strict=True)),
                    "OBJ has an out-of-range index")
        a, b, c = [Vector(vertices[corner[0] - 1]) for corner in face]
        geometric_normal = (b - a).cross(c - a)
        require(geometric_normal.length > 0.00000001, "OBJ contains a degenerate triangle")
        geometric_normal.normalize()
        require(all(geometric_normal.dot(Vector(normals[corner[2] - 1])) > minimum_normal_dot for corner in face),
                "OBJ corner normals disagree with triangle winding")
    declared = set()
    textures = {}
    for line in path.with_suffix(".mtl").read_text(encoding="utf-8").splitlines():
        if line.startswith("newmtl "):
            tokens = line.split(" ")
            require(len(tokens) == 2 and tokens[1] and tokens[1].isascii()
                    and not any(c.isspace() for c in tokens[1]), "MTL name contains whitespace")
            require(tokens[1] not in declared, "MTL declares a material more than once")
            declared.add(tokens[1])
        elif line.startswith("map_"):
            tokens = line.split(" ")
            require(len(tokens) == 2 and tokens[0] in ("map_Kd", "map_Bump"),
                    "UB texture maps need one relative path, without MTL options")
            relative = tokens[1]
            require(relative and relative.isascii() and not any(c.isspace() for c in relative)
                    and not Path(relative).is_absolute(), "Invalid MTL texture path")
            texture_path = (path.parent / relative).resolve()
            require(texture_path.is_relative_to(path.parent.parent.resolve()),
                    "MTL texture escapes the model library")
            require(texture_path.is_file(), "MTL texture is missing: " + relative)
            if relative not in textures:
                texture = bpy.data.images.load(str(texture_path), check_existing=True)
                require(texture.size[0] > 0 and texture.size[1] > 0, "MTL texture could not be decoded")
                textures[relative] = {"size": list(texture.size), "sha256": file_hash(texture_path)}
    require(used_materials <= declared, "OBJ uses a material missing from its MTL")
    # UB expands each face corner into a separate Unity vertex and uses 16-bit indices.
    expanded_vertices = len(faces) * 3
    require(expanded_vertices < 65536, "UB OBJ expanded vertices exceed the 16-bit mesh limit")
    return vertices, {"vertices": len(vertices), "uvs": len(uvs), "normals": len(normals),
                      "triangles": len(faces), "expanded_vertices": expanded_vertices,
                      "materials": sorted(used_materials), "textures": textures,
                      "bounds_metres_y_up": bounds(vertices)}


def output_file(output, relative):
    require(isinstance(relative, str) and relative and not Path(relative).is_absolute(),
            "Quality paths must be relative to the run output")
    path = (output / relative).resolve()
    require(path.is_relative_to(output.resolve()), "Quality path escapes the run output: " + relative)
    require(path.is_file(), "Quality input is missing: " + relative)
    return path


def finite_vector(value, length, label):
    require(isinstance(value, list) and len(value) == length and
            all(type(number) in (int, float) and math.isfinite(number) for number in value),
            label + " must contain finite numbers")
    return value


def quality_fields(value, allowed, required, label):
    require(isinstance(value, dict), label + " must be an object")
    require(not (set(value) - set(allowed)), label + " contains unknown fields: " + str(set(value) - set(allowed)))
    require(set(required) <= set(value), label + " is missing required fields: " + str(set(required) - set(value)))


def preview_receipt(path, *, view):
    """Record the camera and renderer that produced an existing PNG."""
    path = Path(path)
    require(path.is_file(), "Cannot capture a missing preview")
    scene = bpy.context.scene
    require(scene.camera is not None and isinstance(view, str) and view, "Preview needs a camera and view name")
    bpy.context.view_layer.update()
    image = bpy.data.images.load(str(path), check_existing=False)
    try:
        dimensions = list(image.size)
    finally:
        bpy.data.images.remove(image)
    direction = (scene.camera.matrix_world.to_3x3() @ Vector((0, 0, 1))).normalized()
    receipt = {"schema_version": 1, "image": path.name, "sha256": file_hash(path),
               "view": view, "width": dimensions[0], "height": dimensions[1],
               "engine": scene.render.engine, "camera_direction": list(direction),
               "camera_projection": scene.camera.data.type, "frame": scene.frame_current}
    path.with_suffix(path.suffix + ".capture.json").write_text(
        json.dumps(receipt, indent=2) + "\n", encoding="utf-8")
    return receipt


def validate_quality_profile(output, profile_path):
    """Check model-owned requirements against exported assets and captured renders."""
    profile = json.loads(Path(profile_path).read_text(encoding="utf-8-sig"))
    quality_fields(profile, ("schema_version", "required_stages", "obj_assets", "previews"),
                   ("schema_version", "required_stages"), "Quality profile")
    require(type(profile["schema_version"]) is int and profile["schema_version"] == 1,
            "Unsupported quality profile version")
    stages = profile["required_stages"]
    require(isinstance(stages, list) and stages and
            all(isinstance(stage, str) and re.fullmatch(r"[a-z][a-z0-9_-]*", stage) for stage in stages) and
            len(stages) == len(set(stages)) and "validate-quality" not in stages,
            "Quality required_stages must contain distinct pipeline stage names")
    artifacts = []
    for stage in stages:
        report_path = output_file(output, "reports/" + stage + ".json")
        report = json.loads(report_path.read_text(encoding="utf-8-sig"))
        require(report.get("stage") == stage and report.get("status") == "passed",
                "Required stage did not pass: " + stage)
        artifacts.append(report_path)
    assets, previews = profile.get("obj_assets", []), profile.get("previews", [])
    require(isinstance(assets, list) and isinstance(previews, list) and (assets or previews),
            "Quality profile must require assets or previews")
    asset_results, preview_results, seen = [], [], set()
    for asset in assets:
        quality_fields(asset, ("path", "bounds_metres_y_up", "materials", "max_triangles"),
                       ("path", "bounds_metres_y_up", "materials", "max_triangles"), "OBJ requirement")
        path = output_file(output, asset["path"])
        require(path.suffix.lower() == ".obj" and path not in seen, "Duplicate or unsupported quality asset")
        seen.add(path)
        _, measured = parse_ub_obj(path)
        budget = asset["max_triangles"]
        require(type(budget) is int and budget > 0 and measured["triangles"] <= budget,
                "OBJ exceeds the required triangle budget: " + asset["path"])
        materials = asset["materials"]
        require(isinstance(materials, list) and materials and
                all(isinstance(name, str) and name for name in materials) and len(set(materials)) == len(materials),
                "Quality materials must be distinct names")
        require(set(materials) == set(measured["materials"]), "OBJ materials differ from the quality profile")
        box = asset["bounds_metres_y_up"]
        quality_fields(box, ("min", "max", "tolerance_metres"), ("min", "max"), "Quality bounds")
        low, high = finite_vector(box["min"], 3, "Bounds min"), finite_vector(box["max"], 3, "Bounds max")
        require(all(a <= b for a, b in zip(low, high, strict=True)), "Quality bounds min exceeds max")
        tolerance = box.get("tolerance_metres", TOLERANCE)
        require(type(tolerance) in (int, float) and math.isfinite(tolerance) and tolerance >= 0,
                "Bounds tolerance must be finite and nonnegative")
        error = max(abs(actual - expected) for side in ("min", "max")
                    for actual, expected in zip(measured["bounds_metres_y_up"][side], box[side], strict=True))
        require(error <= tolerance, "OBJ dimensions differ from the quality profile")
        asset_results.append({"path": asset["path"], "bounds_error_metres": error, "measurements": measured})
        artifacts.extend((path, path.with_suffix(".mtl")))
    seen.clear()
    for preview in previews:
        quality_fields(preview, ("path", "view", "width", "height", "engine", "camera_direction", "angle_tolerance_degrees"),
                       ("path", "view", "width", "height", "engine", "camera_direction"), "Preview requirement")
        path = output_file(output, preview["path"])
        require(path not in seen and path.read_bytes()[:8] == b"\x89PNG\r\n\x1a\n", "Duplicate or non-PNG preview")
        seen.add(path)
        receipt_path = output_file(output, preview["path"] + ".capture.json")
        receipt = json.loads(receipt_path.read_text(encoding="utf-8-sig"))
        require(receipt.get("schema_version") == 1 and receipt.get("image") == path.name and
                receipt.get("sha256") == file_hash(path), "Preview capture does not match the actual PNG")
        image = bpy.data.images.load(str(path), check_existing=False)
        try:
            dimensions = tuple(image.size)
        finally:
            bpy.data.images.remove(image)
        require(all(type(preview[axis]) is int and preview[axis] > 0 for axis in ("width", "height")),
                "Preview dimensions must be positive integers")
        require(dimensions == (preview["width"], preview["height"]) == (receipt.get("width"), receipt.get("height")),
                "Preview dimensions differ from the quality profile")
        require(all(isinstance(preview[key], str) and preview[key] for key in ("view", "engine")),
                "Preview view and engine must be named")
        require(receipt.get("view") == preview["view"] and receipt.get("engine") == preview["engine"],
                "Preview view or renderer differs from the quality profile")
        expected = Vector(finite_vector(preview["camera_direction"], 3, "Required camera direction"))
        actual = Vector(finite_vector(receipt.get("camera_direction"), 3, "Captured camera direction"))
        tolerance = preview.get("angle_tolerance_degrees", 0.5)
        require(expected.length > 0 and actual.length > 0 and type(tolerance) in (int, float) and
                math.isfinite(tolerance) and 0 <= tolerance < 180, "Invalid preview camera tolerance/direction")
        error = math.degrees(expected.angle(actual))
        require(error <= tolerance, "Preview camera direction differs from the quality profile")
        preview_results.append({"path": preview["path"], "view": preview["view"], "engine": receipt["engine"],
                                "width": dimensions[0], "height": dimensions[1], "camera_error_degrees": error})
        artifacts.extend((path, receipt_path))
    return {"scope": "declared Blender OBJ geometry, material names and captured rendered views",
            "required_stages": stages, "obj_assets_checked": asset_results, "previews_checked": preview_results,
            "profile_sha256": file_hash(Path(profile_path)), "input_sha256": inputs_report(output, artifacts),
            "unity": "not_run", "game": "not_run"}


def render_preview(path, target=(0.0, 0.0, 0.55), *, direction=(2.4, -4.0, 2.3), ortho_scale=1.70,
                   view="perspective"):
    scene = bpy.context.scene
    scene.render.engine = "BLENDER_WORKBENCH"
    scene.render.resolution_x, scene.render.resolution_y = 384, 384
    scene.render.resolution_percentage = 100
    scene.render.image_settings.file_format = "PNG"
    scene.render.image_settings.color_mode = "RGBA"
    scene.render.film_transparent = False
    shading = scene.display.shading
    shading.light = "STUDIO"
    shading.color_type = "MATERIAL"
    shading.show_shadows = True
    shading.show_cavity = True
    shading.cavity_type = "BOTH"
    shading.background_type = "WORLD"
    scene.world = scene.world or bpy.data.worlds.new("UBPreviewWorld")
    scene.world.color = (0.055, 0.065, 0.085)
    if scene.camera is None:
        data = bpy.data.cameras.new("UBPreviewCamera")
        camera = bpy.data.objects.new("UBPreviewCamera", data)
        scene.collection.objects.link(camera)
        scene.camera = camera
    camera = scene.camera
    camera.location = Vector(target) + Vector(direction).normalized() * max(ortho_scale * 3, 1.0)
    camera.rotation_euler = (Vector(target) - camera.location).to_track_quat("-Z", "Y").to_euler()
    camera.data.type = "ORTHO"
    camera.data.ortho_scale = ortho_scale
    path.parent.mkdir(parents=True, exist_ok=True)
    scene.render.filepath = str(path)
    bpy.ops.render.render(write_still=True)
    require(path.is_file() and path.stat().st_size > 1000, "Preview PNG was not written")
    preview_receipt(path, view=view)


def render_views(output, prefix, points):
    box = bounds(points)
    target = tuple((low + high) / 2 for low, high in zip(box["min"], box["max"], strict=True))
    # The bounding sphere fits every orientation, including the diagonal view.
    scale = max(distance(box["min"], box["max"]) * 1.12, 0.05)
    views = {"front": (0, -1, 0), "side": (1, 0, 0), "top": (0, 0, 1), "perspective": (2.4, -4, 2.3)}
    paths = []
    for name, direction in views.items():
        path = output / "previews" / f"{prefix}_{name}.png"
        render_preview(path, target, direction=direction, ortho_scale=scale, view=name)
        paths.append(path)
    return paths


def contact_sheet(paths, output, *, columns=3):
    images = [bpy.data.images.load(str(path), check_existing=False) for path in paths]
    width, height = tuple(images[0].size)
    require(all(tuple(item.size) == (width, height) for item in images), "Preview sizes disagree")
    rows = math.ceil(len(images) / columns)
    sheet_width, sheet_height = width * columns, height * rows
    pixels = array("f", [0.055, 0.065, 0.085, 1.0]) * (sheet_width * sheet_height)
    for index, item in enumerate(images):
        cell = array("f", [0.0]) * (width * height * 4)
        item.pixels.foreach_get(cell)
        column, row = index % columns, rows - 1 - index // columns
        for y in range(height):
            start = ((row * height + y) * sheet_width + column * width) * 4
            pixels[start:start + width * 4] = cell[y * width * 4:(y + 1) * width * 4]
    sheet = bpy.data.images.new("UBRigContactSheet", width=sheet_width, height=sheet_height, alpha=True)
    sheet.pixels.foreach_set(pixels)
    sheet.filepath_raw = str(output)
    sheet.file_format = "PNG"
    sheet.save()
    for item in images:
        bpy.data.images.remove(item)
    bpy.data.images.remove(sheet)


def verify_static(output):
    source = output / "sources" / "UBSampleProp.blend"
    path = output / "static" / "UBSampleProp" / "UBSampleProp.obj"
    bpy.ops.wm.open_mainfile(filepath=str(source), load_ui=False)
    source_geometry = geometry_snapshot(single_object("MESH"))
    expected = source_geometry["points"]
    require(bpy.context.scene.unit_settings.scale_length == 1.0, "Source metre scale changed")
    vertices, report = parse_ub_obj(path, "UBSampleProp", minimum_normal_dot=0.99, require_unit_uv=True)
    require(not report["textures"], "The static sample should use self-contained colour materials")
    exported_expected = [(x, z, -y) for x, y, z in expected]
    export_error = surface_error(vertices, exported_expected)
    require(export_error < 0.000002, "OBJ axis/scale mapping disagrees with reopened .blend")
    require(abs(report["bounds_metres_y_up"]["max"][1] - 0.65) < 0.000002
            and abs(report["bounds_metres_y_up"]["max"][2] - 0.45) < 0.000002
            and abs(report["bounds_metres_y_up"]["max"][0] - 0.42) < 0.000002,
            "Asymmetric +Y-up/+Z-front/+X-right orientation markers are incorrect")
    empty_scene("VerifyStatic")
    bpy.ops.wm.obj_import(filepath=str(path), forward_axis="NEGATIVE_Z", up_axis="Y",
                          global_scale=1.0, use_split_objects=True, use_split_groups=False,
                          validate_meshes=True)
    imported = single_object("MESH")
    require(imported.data.uv_layers.active is not None, "Reimport lost the UV layer")
    require(len(imported.data.materials) == 3, "Reimport lost materials")
    import_error = surface_error(world_points(imported), expected)
    require(import_error < TOLERANCE, "OBJ reimport surface differs from reopened source")
    geometry_report = compare_geometry(geometry_snapshot(imported), source_geometry)
    preview = output / "previews" / "static.png"
    render_preview(preview, (0.02, -0.07, 0.32))
    views = render_views(output, "static", expected)
    contact_sheet(views, output / "previews" / "static_views.png", columns=2)
    report.update({"source_blend_reopened": True, "export_axis_surface_error_metres": export_error,
                   "reimport_surface_error_metres": import_error, "tolerance_metres": TOLERANCE,
                   "geometry_round_trip": geometry_report,
                   "multi_view_previews": [str(p.relative_to(output)).replace("\\", "/") for p in views],
                   "preview": "previews/static.png", "preview_camera": "Blender +Z up, looking from +X/-Y",
                   "input_sha256": inputs_report(output, (source, path, path.with_suffix(".mtl")))})
    return report


def rig_contract(rig, skin):
    parents = {bone.name: bone.parent.name if bone.parent else None for bone in rig.data.bones}
    require(parents == BONES, "Bone names/parent chain changed: " + str(parents))
    modifiers = [modifier for modifier in skin.modifiers if modifier.type == "ARMATURE"]
    require(len(modifiers) == 1 and modifiers[0].object == rig, "Skin must use its one imported armature")
    require({group.name for group in skin.vertex_groups} == set(BONES), "Skin weight groups changed")
    require(rig.animation_data and rig.animation_data.action is not None, "No armature Action imported")
    require(len(bpy.data.actions) == 1, "Expected exactly one Action/take")
    require(not rig.animation_data.nla_tracks, "Sample unexpectedly has NLA tracks")
    require(skin.data.uv_layers.active is not None, "Rig UV layer is missing")
    skin.data.calc_loop_triangles()
    max_weight_error = 0.0
    for vertex in skin.data.vertices:
        values = [group.weight for group in vertex.groups]
        require(values and all(0 < value <= 1 for value in values), "Invalid or unweighted skin vertex")
        max_weight_error = max(max_weight_error, abs(sum(values) - 1.0))
    require(max_weight_error < 0.00001, "Skin weights are not normalized")
    return {"bone_parents": parents, "vertices": len(skin.data.vertices),
            "triangles": len(skin.data.loop_triangles),
            "action": rig.animation_data.action.name,
            "action_frame_range": list(rig.animation_data.action.frame_range),
            "maximum_weight_sum_error": max_weight_error}


def sample_animation(skin):
    samples = {}
    for frame in FRAMES:
        bpy.context.scene.frame_set(frame)
        bpy.context.view_layer.update()
        samples[frame] = world_points(skin)
    return samples


def sample_bone_deformation(rig):
    samples = {}
    for frame in FRAMES:
        bpy.context.scene.frame_set(frame)
        bpy.context.view_layer.update()
        samples[frame] = {bone.name: [value for row in
            (rig.matrix_world @ bone.matrix @ bone.bone.matrix_local.inverted()) for value in row]
            for bone in rig.pose.bones}
        require(all(math.isfinite(value) for values in samples[frame].values() for value in values),
                "Nonfinite bone deformation matrix")
    return samples


def skin_weights(skin):
    return [(tuple(skin.matrix_world @ vertex.co), {skin.vertex_groups[group.group].name: group.weight
             for group in vertex.groups}) for vertex in skin.data.vertices]


def compare_weights(actual, expected):
    require(len(actual) == len(expected), "Rest-position weight count changed")
    tree = KDTree(len(expected))
    for index, (point, _) in enumerate(expected):
        tree.insert(point, index)
    tree.balance()
    maximum = 0.0
    used = set()
    for point, weights in actual:
        candidates = [(index, expected[index][1]) for _, index, _ in tree.find_range(point, TOLERANCE)
                      if index not in used and set(weights) == set(expected[index][1])]
        require(candidates, "Skin rest positions/influencing bones lost a vertex correspondence")
        index, reference = min(candidates, key=lambda pair: max(abs(value - pair[1][name])
                               for name, value in weights.items()))
        used.add(index)
        maximum = max(maximum, *(abs(value - reference[name]) for name, value in weights.items()))
    require(maximum < 0.00001, "Skin weight values changed")
    return maximum


def verify_rig(output):
    source, path = output / "sources" / "UBSampleRig.blend", output / "rig" / "UBSampleRig.fbx"
    bpy.ops.wm.open_mainfile(filepath=str(source), load_ui=False)
    scene = bpy.context.scene
    require(scene.render.fps == FPS and scene.render.fps_base == 1.0, "Source FPS changed")
    require((scene.frame_start, scene.frame_end) == (1, 25), "Source duration changed")
    require(scene.unit_settings.scale_length == 1.0, "Source metre scale changed")
    source_rig, source_skin = single_object("ARMATURE"), single_object("MESH")
    source_contract = rig_contract(source_rig, source_skin)
    source_geometry = geometry_snapshot(source_skin)
    expected_weights = skin_weights(source_skin)
    expected_bones = sample_bone_deformation(source_rig)
    expected = sample_animation(single_object("MESH"))
    empty_scene("VerifyRig")
    # Scene-take export preserves absolute frames. Default FBX anim_offset=1 would add a frame.
    bpy.ops.import_scene.fbx(filepath=str(path), use_anim=True, anim_offset=0.0, global_scale=1.0,
                             ignore_leaf_bones=False, automatic_bone_orientation=False,
                             use_custom_normals=True, use_image_search=False)
    rig, skin = single_object("ARMATURE"), single_object("MESH")
    report = rig_contract(rig, skin)
    require(report["vertices"] == source_contract["vertices"], "FBX changed the sample vertex count")
    require(all(abs(actual - expected_frame) < 0.001
                for actual, expected_frame in zip(report["action_frame_range"], (1, 25), strict=True)),
            "FBX Action range is not 1..25")
    require(bpy.context.scene.render.fps == FPS, "FBX FPS is not 24")
    bpy.context.scene.frame_set(FRAMES[0])
    bpy.context.view_layer.update()
    geometry_report = compare_geometry(geometry_snapshot(skin), source_geometry)
    weight_error = compare_weights(skin_weights(skin), expected_weights)
    bone_samples = sample_bone_deformation(rig)
    bone_errors = {str(frame): max(abs(a - b) for name in BONES
                   for a, b in zip(bone_samples[frame][name], expected_bones[frame][name], strict=True))
                   for frame in FRAMES}
    require(max(bone_errors.values()) < TOLERANCE, "FBX per-bone deformation matrices changed")
    actual = sample_animation(skin)
    errors = {str(frame): surface_error(actual[frame], expected[frame]) for frame in FRAMES}
    require(max(errors.values()) < TOLERANCE, "FBX evaluated skin differs from reopened .blend")
    loop_error = surface_error(actual[1], actual[25])
    require(loop_error < TOLERANCE, "Animation endpoints do not form a closed loop")
    displacement = max(surface_error(actual[frame], actual[1]) for frame in FRAMES)
    require(displacement > 0.05, "The evaluated skinned mesh did not visibly deform")
    previews = []
    for frame in PREVIEW_FRAMES:
        bpy.context.scene.frame_set(frame)
        image_path = output / "previews" / f"rig_frame_{frame:03d}.png"
        render_preview(image_path)
        previews.append(image_path)
    sheet = output / "previews" / "contact_sheet.png"
    contact_sheet(previews, sheet)
    bpy.context.scene.frame_set(7)
    views = render_views(output, "rig_frame_007", actual[7])
    contact_sheet(views, output / "previews" / "rig_views.png", columns=2)
    report.update({"source_blend_reopened": True, "source_contract": source_contract,
                   "frames_checked": list(FRAMES), "fps": FPS, "duration_seconds": 1.0,
                   "maximum_surface_error_metres": max(errors.values()), "surface_errors_by_frame_metres": errors,
                   "geometry_round_trip": geometry_report, "maximum_weight_round_trip_error": weight_error,
                   "bone_matrix_errors_by_frame": bone_errors, "maximum_bone_matrix_error": max(bone_errors.values()),
                   "multi_view_previews": [str(p.relative_to(output)).replace("\\", "/") for p in views],
                   "loop_endpoint_error_metres": loop_error, "maximum_deformation_metres": displacement,
                   "tolerance_metres": TOLERANCE, "rest_bounds_metres_z_up": bounds(actual[1]),
                   "previews": [str(p.relative_to(output)).replace("\\", "/") for p in previews] + ["previews/contact_sheet.png"],
                   "contact_sheet_order": "left to right, top to bottom: frames 1,7,13,19,25; last cell empty",
                   "input_sha256": inputs_report(output, (source, path))})
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--stage", choices=("generate", "verify-static", "verify-rig", "validate-quality"), required=True)
    parser.add_argument("--quality-profile", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args(sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else [])
    owned_paths = ("static", "static/UBSampleProp", "rig",
                   "sources/UBSampleProp.blend", "sources/UBSampleRig.blend",
                   "static/UBSampleProp/UBSampleProp.obj", "static/UBSampleProp/UBSampleProp.mtl",
                   "rig/UBSampleRig.fbx", "previews/static.png", "previews/contact_sheet.png",
                   "reports/generate.json", "reports/verify-static.json", "reports/verify-rig.json",
                   "reports/validate-quality.json")
    owned_paths += tuple(f"previews/rig_frame_{frame:03d}.png" for frame in PREVIEW_FRAMES)
    owned_paths += tuple(f"previews/{prefix}_{view}.png" for prefix in ("static", "rig_frame_007")
                         for view in ("front", "side", "top", "perspective"))
    owned_paths += ("previews/static_views.png", "previews/rig_views.png", "failures",
                    f"failures/{args.stage}.blend")
    output = safe_output(args.output, owned_paths)
    reports = output / "reports"
    reports.mkdir(parents=True, exist_ok=True)
    report = {"stage": args.stage, "status": "failed", "blender_version": bpy.app.version_string,
              "blender_build_hash": bpy.app.build_hash.decode("ascii"), "script_sha256": file_hash(Path(__file__)),
              "game_runtime_tested": False, "tolerances": {"position_metres": TOLERANCE,
              "uv": UV_TOLERANCE, "unit_normal_distance": NORMAL_TOLERANCE,
              "material_rgba": MATERIAL_TOLERANCE, "bone_matrix_elements": TOLERANCE}}
    started = time.perf_counter()
    try:
        require(tuple(bpy.app.version) == VERSION, "This pipeline is verified with Blender 4.5.9 LTS only")
        if args.stage == "validate-quality":
            require(args.quality_profile is not None, "validate-quality requires --quality-profile")
            report.update(validate_quality_profile(output, args.quality_profile))
        else:
            stage = {"generate": generate, "verify-static": verify_static, "verify-rig": verify_rig}[args.stage]
            report.update(stage(output))
        report["status"] = "passed"
    except Exception as error:
        report["error"] = str(error)
        report["traceback"] = traceback.format_exc()
        failure_scene = output / "failures" / f"{args.stage}.blend"
        failure_scene.parent.mkdir(parents=True, exist_ok=True)
        try:
            bpy.ops.wm.save_as_mainfile(filepath=str(failure_scene), check_existing=False)
            report["failure_scene"] = str(failure_scene.relative_to(output)).replace("\\", "/")
        except Exception as save_error:
            report["failure_scene_error"] = str(save_error)
        raise
    finally:
        report["elapsed_seconds"] = time.perf_counter() - started
        (reports / (args.stage + ".json")).write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        print(json.dumps({"stage": args.stage, "status": report["status"]}))


if __name__ == "__main__":
    main()
