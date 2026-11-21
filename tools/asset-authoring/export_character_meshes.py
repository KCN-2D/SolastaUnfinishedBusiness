"""Export evaluated character meshes from Blender in Unity renderer coordinates."""
import argparse
import hashlib
import json
from pathlib import Path
import sys

import bpy
from mathutils import Matrix
import numpy as np


def sha(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def export_mesh(obj, transform, graph, destination, *, retained_topology):
    evaluated = obj.evaluated_get(graph)
    if not evaluated.is_evaluated:
        raise ValueError(f"{obj.name} must be enabled in the active Blender view layer for export.")
    mesh = evaluated.to_mesh(preserve_all_data_layers=True, depsgraph=graph)
    if mesh is None:
        raise ValueError(f"{obj.name} has no evaluated character mesh.")
    try:
        mesh.calc_loop_triangles()
        frame = transform @ evaluated.matrix_world
        positions = np.empty(3 * len(mesh.vertices), dtype="<f4")
        mesh.vertices.foreach_get("co", positions)
        linear = np.asarray(frame.to_3x3(), dtype=float)
        inverse_linear = np.linalg.inv(linear)
        handedness = np.sign(np.linalg.det(linear))
        positions = (positions.reshape(-1, 3) @ linear.T + frame.translation).astype("<f4")
        retained = mesh.attributes.get("retained_vertex")
        if retained is not None:
            saved = mesh.attributes.get("retained_position")
            if (not retained_topology or retained.domain != "POINT" or retained.data_type != "BOOLEAN"
                    or saved is None or saved.domain != "POINT" or saved.data_type != "FLOAT_VECTOR"):
                raise ValueError(f"{obj.name} requires its original renderer coordinates for retained vertices.")
            fixed = np.empty(len(positions), dtype=bool)
            retained.data.foreach_get("value", fixed)
            original = np.empty(3 * len(positions), dtype="<f4")
            saved.data.foreach_get("vector", original)
            original = original.reshape(-1, 3)
            if not np.allclose(positions[fixed], original[fixed], rtol=0., atol=1e-6):
                raise ValueError(f"{obj.name} moved a retained source vertex.")
            positions[fixed] = original[fixed]
        normals = np.empty(3 * len(mesh.vertices), dtype="<f4")
        mesh.vertices.foreach_get("normal", normals)
        normals = normals.reshape(-1, 3) @ inverse_linear
        normals /= np.maximum(np.linalg.norm(normals, axis=1, keepdims=True), 1e-15)
        triangles = np.empty(3 * len(mesh.loop_triangles), dtype="<i4")
        mesh.loop_triangles.foreach_get("vertices", triangles)
        triangles = triangles.reshape(-1, 3)
        if handedness < 0:
            triangles = triangles[:, [0, 2, 1]].copy()
        area = np.cross(positions[triangles[:, 1]] - positions[triangles[:, 0]],
                        positions[triangles[:, 2]] - positions[triangles[:, 0]])
        if (not len(triangles) or not np.isfinite(positions).all() or not np.isfinite(normals).all()
                or (not retained_topology and not np.all(np.linalg.norm(area, axis=1) > 1e-12))):
            raise ValueError(f"{obj.name} has an empty, collapsed or invalid evaluated surface.")
        geometry = dict(positions=positions, normals=normals.astype("<f4"), triangles=triangles)
        source = mesh.attributes.get('source_vertex')
        if source is not None:
            if source.domain != 'POINT' or source.data_type != 'INT' or mesh.uv_layers.active is None:
                raise ValueError(f'{obj.name} requires source vertex indices and a UV map.')
            source_vertices = np.empty(len(positions), dtype='<i4')
            source.data.foreach_get('value', source_vertices)
            loops = np.empty(len(mesh.loops), dtype='<i4')
            mesh.loops.foreach_get('vertex_index', loops)
            uv = np.empty(2 * len(loops), dtype='<f4')
            mesh.uv_layers.active.data.foreach_get('uv', uv)
            uv = uv.reshape(-1, 2)
            coordinates = np.zeros((len(positions), 2), dtype='<f4')
            coordinates[loops] = uv
            if not np.array_equal(coordinates[loops], uv):
                raise ValueError(f'{obj.name} must split vertices at each UV seam.')
            material_slots = np.empty(len(triangles), dtype='<i4')
            mesh.loop_triangles.foreach_get('material_index', material_slots)
            mesh.calc_tangents(uvmap=mesh.uv_layers.active.name)
            loop_tangents = np.empty(3 * len(loops), dtype='<f4')
            loop_signs = np.empty(len(loops), dtype='<f4')
            mesh.loops.foreach_get('tangent', loop_tangents)
            mesh.loops.foreach_get('bitangent_sign', loop_signs)
            tangent = np.zeros((len(positions), 4), dtype='<f4')
            directions = loop_tangents.reshape(-1, 3) @ linear.T
            tangent[loops, :3] = directions/np.maximum(np.linalg.norm(directions, axis=1, keepdims=True), 1e-15)
            tangent[loops, 3] = loop_signs * handedness
            geometry.update(source_vertices=source_vertices, uv0=coordinates, material_slots=material_slots, tangents=tangent)
        skin_normals = mesh.attributes.get('skin_normals')
        if skin_normals is not None:
            if skin_normals.domain != 'POINT' or skin_normals.data_type != 'FLOAT_VECTOR':
                raise ValueError(f'{obj.name} requires one authored skin normal per vertex.')
            values = np.empty(3 * len(positions), dtype='<f4')
            skin_normals.data.foreach_get('vector', values)
            values = values.reshape(-1, 3) @ inverse_linear
            lengths = np.linalg.norm(values, axis=1, keepdims=True)
            if not np.isfinite(values).all() or np.any(lengths <= 1e-8):
                raise ValueError(f'{obj.name} has an invalid authored skin normal.')
            geometry['skin_normals'] = (values / lengths).astype('<f4')
        for name in ("groom_centers", "groom_coordinates", "groom_across"):
            attribute = mesh.attributes.get(name)
            if attribute is None:
                continue
            if attribute.domain != "POINT" or attribute.data_type != "FLOAT_VECTOR":
                raise ValueError(f"{obj.name} {name} must contain a vector per vertex.")
            values = np.empty(3 * len(positions), dtype="<f4")
            attribute.data.foreach_get("vector", values)
            values = values.reshape(-1, 3)
            if name == "groom_centers":
                values = (values @ linear.T + frame.translation).astype("<f4")
            elif name == "groom_across":
                values = (values @ linear.T).astype("<f4")
            if not np.isfinite(values).all():
                raise ValueError(f"{obj.name} has invalid {name} data.")
            geometry[name] = values
        density = mesh.attributes.get('groom_density')
        if density is not None:
            if density.domain != 'POINT' or density.data_type != 'FLOAT':
                raise ValueError(f'{obj.name} groom_density must contain a value per vertex.')
            values = np.empty(len(positions), dtype='<f4')
            density.data.foreach_get('value', values)
            if not np.isfinite(values).all() or np.any((values < 0.) | (values > 1.)):
                raise ValueError(f'{obj.name} has invalid groom density.')
            geometry['groom_density'] = values
        np.savez_compressed(destination, **geometry)
        return dict(vertexCount=len(positions), **{
            f"{name}Sha256": hashlib.sha256(geometry[name].tobytes()).hexdigest()
            for name in ("positions", "normals", "triangles")
        })
    finally:
        evaluated.to_mesh_clear()


def export_request(request, folder, scene, graph):
    if request["schemaVersion"] != 1 or not request["meshes"]:
        raise ValueError("The Blender character export requires a nonempty schema 1 request.")
    ids = {row["pathId"] for row in request["meshes"]}
    names = {row["name"] for row in request["meshes"]}
    if len(ids) != len(request["meshes"]) or len(names) != len(ids):
        raise ValueError("Each Blender object needs one Unity mesh identity.")
    folder.mkdir(parents=True)
    rows = []
    for number, row in enumerate(request["meshes"]):
        target = folder / f"Geometry-{number:03}.npz"
        obj = bpy.context.scene.objects[row["object"]]
        frame = Matrix(row["worldToUnity"])
        if (len(frame) != 4 or len(frame[0]) != 4 or not np.isfinite(np.asarray(frame)).all()
                or tuple(frame[3]) != (0., 0., 0., 1.) or abs(frame.to_3x3().determinant()) < 1e-12):
            raise ValueError(f"{obj.name} has an invalid Unity renderer transform.")
        if type(row["firstVertex"]) is not int or row["firstVertex"] < 0 or type(row["pathId"]) is not int or not row["pathId"]:
            raise ValueError(f"{obj.name} has an invalid renderer identity or vertex offset.")
        result = export_mesh(obj, frame, graph, target, retained_topology=row.get("retainedTopology", False))
        result.update(pathId=row["pathId"], name=row["name"], firstVertex=row["firstVertex"], object=obj.name,
                      worldToUnity=row["worldToUnity"],
                      geometry={"path": str(target), "sha256": sha(target)})
        if "refinement" in row:
            result["refinement"] = row["refinement"]
        if "nativeGroom" in row:
            result["nativeGroom"] = row["nativeGroom"]
        rows.append(result)
    report = {"schemaVersion": 1, "status": "PASS_BLENDER_CHARACTER_MESH_EXPORT", "blenderVersion": bpy.app.version_string,
              "bundle": request["bundle"], "scene": scene, "meshes": rows}
    (folder / "BlenderExport.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--request", type=Path, nargs="+", required=True,
                        help="Request JSON files or directories containing requests.")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args(sys.argv[sys.argv.index("--") + 1:])
    if not bpy.data.filepath:
        raise ValueError("Open the saved Blender character project before exporting.")
    scene_path = Path(bpy.data.filepath).resolve(strict=True)
    root = Path(__file__).resolve().parents[2]
    folder = args.output.resolve()
    allowed = (root / "tools/asset-authoring/workspace", root / "tools/game-test/artifacts")
    if not any(folder.is_relative_to(parent) and folder != parent for parent in allowed):
        raise ValueError("Export character meshes into an authoring workspace.")
    requests = []
    for path in args.request:
        files = sorted(path.glob("*.json")) if path.is_dir() else [path]
        if not files:
            raise ValueError(f"No character mesh requests in {path}.")
        requests.extend(json.loads(file.read_bytes()) for file in files)
    bundle_names = [str(request["bundle"]) for request in requests]
    if (len({name.casefold() for name in bundle_names}) != len(bundle_names)
            or any(not name.endswith(".bundle") or Path(name).name != name for name in bundle_names)):
        raise ValueError("Each request needs a unique character bundle file name.")
    scene = {"path": str(scene_path), "sha256": sha(scene_path)}
    graph = bpy.context.evaluated_depsgraph_get()
    for request, bundle in zip(requests, bundle_names):
        target = folder if len(requests) == 1 else folder / Path(bundle).stem
        if not target.resolve().is_relative_to(folder):
            raise ValueError("A character mesh export escaped its output directory.")
        export_request(request, target, scene, graph)


if __name__ == "__main__":
    main()
