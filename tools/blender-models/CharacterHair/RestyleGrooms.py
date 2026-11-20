"""Sculpt complete hair and beard resources in the fitted native head frame."""
import argparse
from concurrent.futures import ProcessPoolExecutor, as_completed
import gzip
import json
import os
from pathlib import Path
import re
import sys

for thread_setting in ("OPENBLAS_NUM_THREADS", "OMP_NUM_THREADS", "MKL_NUM_THREADS"):
    os.environ[thread_setting] = "1"
import numpy as np

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent / "CharacterFaces"))
import build_models as models
import character_resources as resources
from resource_storage import resource_work


def pin(path):
    path = models.checked_source_path(path)
    return {"path": str(path), "sha256": models.sha(path)}


def unit(vectors):
    lengths = np.linalg.norm(vectors, axis=-1, keepdims=True)
    return vectors / np.maximum(lengths, 1e-20)


def smooth(values, low, high):
    phase = np.clip((values - low) / (high - low), 0., 1.)
    return phase * phase * (3. - 2. * phase)


def native_matrix(skin):
    return np.asarray(skin["rendererToPrefab"], dtype=float).reshape(4, 4).T


def apply_frame(points, matrix):
    return points @ matrix[:3, :3].T + matrix[:3, 3]


def head_pivot(skin):
    index = [i for i, bone in enumerate(skin["bones"]) if bone.rsplit("/", 1)[-1] == "Bip001 Head"]
    if len(index) != 1:
        raise ValueError("A native groom requires its unique head pivot.")
    return np.asarray(skin["bonesToPrefab"], dtype=float).reshape(-1, 4, 4)[index[0], 3, :3]


def head_surface(skin):
    path = models.checked_source_path(Path(skin["meshFile"]))
    with gzip.open(path, "rt", encoding="utf-8-sig") as stream:
        packet = json.load(stream)
    if models.fingerprint(packet)[0] != skin["sourceFingerprint"]:
        raise ValueError("The declared head surface has different mesh channels.")
    positions = np.asarray(packet["vertices"], dtype=float).reshape(-1, 3)
    triangles = np.concatenate(packet["subMeshes"]).astype(int).reshape(-1, 3)
    return apply_frame(positions, native_matrix(skin)) - head_pivot(skin), triangles


def crown_skin(face):
    skins = (skin for skin in face["skins"] if re.search(r"(?:^|[/_])FaceShape.*_LOD0$", skin["path"], re.I))
    crown = max(skins, key=lambda skin: skin["vertexCount"], default=None)
    if crown is None:
        raise ValueError(f"{face['internalId']} has no native face skin at LOD0.")
    return crown


SCALP_ROWS = 40
SCALP_COLUMNS = 96
SCALP_LOW = -.55
SCALP_HIGH = np.pi / 2
SCALP_CENTER = (0., .43, 0.)


def scalp_coordinates(points):
    x, y, z = (points - SCALP_CENTER).T
    horizontal = np.hypot(x, z)
    elevation = np.arctan2(y, horizontal)
    azimuth = np.arctan2(x, z)
    row = (elevation - SCALP_LOW) * ((SCALP_ROWS - 1) / (SCALP_HIGH - SCALP_LOW))
    column = (azimuth + np.pi) * (SCALP_COLUMNS / (2 * np.pi))
    return row, column, np.hypot(y, horizontal)


def scalp_envelope(points, triangles, fit):
    """Sample the outer head surface in the same scaled frame as the hair."""
    steps = 8
    barycentric = np.asarray([(a, b, steps - a - b) for a in range(steps + 1)
                             for b in range(steps + 1 - a)], float) / steps
    normalized = (points - [0., 0., fit["depthCenter"]]) / fit["scale"]
    scalp = normalized[triangles]
    scalp = scalp[scalp[:, :, 1].max(axis=1) > .05]
    sampled = np.einsum("bi,tij->tbj", barycentric, scalp).reshape(-1, 3)
    row, column, radius = scalp_coordinates(sampled)
    valid = (row >= -.5) & (radius >= .30)
    rows = np.clip(np.rint(row[valid]).astype(int), 0, SCALP_ROWS - 1)
    cols = np.rint(column[valid]).astype(int) % SCALP_COLUMNS
    envelope = np.zeros((SCALP_ROWS, SCALP_COLUMNS), dtype=np.float32)
    np.maximum.at(envelope, (rows, cols), radius[valid])
    envelope[-1] = envelope[-1].max()
    for _ in range(6):
        padded = np.pad(envelope, ((1, 1), (0, 0)), mode="edge")
        left = np.roll(envelope, -1, axis=1)
        right = np.roll(envelope, 1, axis=1)
        neighbours = np.stack((left, right, padded[:-2], padded[2:]))
        weights = (neighbours > 0).sum(axis=0)
        infill = neighbours.sum(axis=0) / np.maximum(weights, 1)
        envelope = np.where(envelope > 0, envelope, infill)
    return envelope


def smooth_envelope(envelope):
    for _ in range(3):
        padded = np.pad(envelope, ((1, 1), (0, 0)), mode="edge")
        envelope = (.60 * envelope + .10 * (np.roll(envelope, 1, axis=1) +
                     np.roll(envelope, -1, axis=1) + padded[:-2] + padded[2:]))
    return envelope


def scalp_tables(heads, fit):
    measured = {name: scalp_envelope(*surface, fit) for name, surface in heads.items()}
    surfaces = np.stack(tuple(measured.values()))
    shared = np.ma.median(np.ma.masked_equal(surfaces, 0), axis=0).filled(1.)
    return {name: smooth_envelope(np.where(table > 0, table, shared)) for name, table in measured.items()}


def scalp_query(points, envelope):
    row, col, radius = scalp_coordinates(points)
    row = np.clip(row, 0., SCALP_ROWS - 1.00001)
    i, j = np.floor(row).astype(int), np.floor(col).astype(int)
    vertical, horizontal = row - i, col - j
    j %= SCALP_COLUMNS
    next_j = (j + 1) % SCALP_COLUMNS
    low = envelope[i, j] * (1. - horizontal) + envelope[i, next_j] * horizontal
    high = envelope[i + 1, j] * (1. - horizontal) + envelope[i + 1, next_j] * horizontal
    return radius, low * (1. - vertical) + high * vertical


def scalp_limit(points, envelope):
    radial = points - SCALP_CENTER
    radius, surface = scalp_query(points, envelope)
    region = smooth(radial[:, 1] / np.maximum(radius, 1e-10), -.40, -.04)
    return radius, np.maximum(surface, .57), region


def seat_hair(points, styled, envelope):
    """Keep the groom outside the scalp without a discontinuous surface clamp."""
    center = np.asarray(SCALP_CENTER)
    original, original_surface, prior_region = scalp_limit(points, envelope)
    result, result_surface, next_region = scalp_limit(styled, envelope)
    coverage = prior_region * next_region
    start = original / original_surface
    delta = result / result_surface - start
    positive = np.maximum(delta, 0.)
    inward = np.minimum(delta, 0.)
    root = np.maximum(start - .985, 0.)
    retained = root * np.expm1(inward / np.maximum(.28, root))
    target = start + positive + retained
    target += .024 * np.exp(-np.square((start - 1.) / .12))
    fitted = result + coverage * (result_surface * target - result)
    direction = (styled - center) / np.maximum(result, 1e-10)[:, None]
    rear = smooth(-direction[:, 2], -.10, .64)
    region = np.maximum(next_region, rear * smooth(direction[:, 1], -.72, -.22))
    inner = smooth(fitted, .43, .79)
    outer = 1. - smooth(fitted, 1.09, 1.82)
    occipital = rear * (1. - smooth(direction[:, 1], .38, .82))
    fitted += (.038 + .198 * occipital) * region * inner * outer
    return center + (styled - center) * (fitted / np.maximum(result, 1e-10))[:, None]


def scalp_mobility(points, scale, envelope):
    """Keep the contact edge fixed as each step moves the outer hair."""
    radius, surface, _ = scalp_limit(points, envelope)
    direction = (points - SCALP_CENTER) / np.maximum(radius, 1e-10)[:, None]
    clearance = (radius - surface) * np.linalg.norm(direction * scale, axis=1)
    return smooth(clearance, .011, .034)


def scalp_fit(heads):
    """Measure the crown and upper-head envelope in a shared provider frame."""
    if not heads:
        raise ValueError("A groom provider has no native face family.")
    sample_indices = np.linspace(0, len(heads) - 1, min(6, len(heads)), dtype=int)
    points = np.concatenate([heads[index][0] for index in sample_indices])
    height = float(np.quantile(points[:, 1], .997))
    scalp = points[(points[:, 1] >= height * .56) & (points[:, 1] <= height * .84)]
    low, high = np.quantile(scalp[:, 2], [.02, .98])
    width = float(np.quantile(np.abs(scalp[:, 0]), .98))
    depth = float((high - low) / 2)
    if not all(.035 < value < .27 for value in (width, height, depth)):
        raise ValueError("The native head measurement does not describe a scalp.")
    return {"scale": [width, height, depth], "depthCenter": float((high + low) / 2)}


def hair_field(points, design):
    x, y, z = points.T
    front = smooth(z, .12, 1.0)
    rear = smooth(-z, .25, 1.16)
    crown = smooth(y, .47, 1.13)
    forehead = front * smooth(y, .24, .60) * (1. - smooth(y, .82, 1.26))
    temple = smooth(np.abs(x), .46, 1.52) * smooth(y, .12, .71) * (1. - .4 * crown)
    tips = smooth(.62 - y, .08, 1.38)
    distance_from_part = x - design["part"]
    part = np.exp(-np.square(distance_from_part / .36))
    side = np.tanh(distance_from_part * 2.3)
    wave = design["wave"] * tips * np.sin((.48 - y) * 4.3 + x * 1.65 + design["part"] * 3.)
    shift_x = design["sweep"] * (forehead + .33 * crown) * (1. - .32 * part)
    shift_x -= design["tuck"] * temple * np.tanh(x * 2.)
    shift_y = design["lift"] * crown * (.75 - .28 * side) * (.58 + .42 * front)
    shift_y += design["fringe"] * forehead * (.43 - .57 * side)
    # Different lengths at the temples and back keep the front pieces apart
    # and let longer layers fall behind the jaw instead of across it.
    layer_phase = distance_from_part * 5.2 - design["sweep"] * y * 3.1 + z * 1.65
    stagger = np.sin(layer_phase) + .32 * np.sin(1.9 * layer_phase + .8)
    shift_z = -design["nape"] * rear * smooth(y, .12, .76)
    shift_z += design["layers"] * .33 * stagger * forehead
    # Blend bulk changes outside the scalp so the roots and part stay seated.
    radial = np.column_stack((x, (y - .43) / .56, z))
    radius = np.linalg.norm(radial, axis=1)
    support = smooth(radius, .66, 1.45)
    excess = .76 * smooth(radius, 1.04, 1.80) * (1. - .72 * tips)
    shift_x -= design["bulk"] * excess * x
    shift_y -= design["bulk"] * excess * (y - .43) * .75
    shift_z -= design["bulk"] * excess * z
    roots = np.column_stack((shift_x, shift_y, shift_z)) * support[:, None]
    free_tips = smooth(np.hypot(x, z), .35, 1.27)
    free_tips += (1. - free_tips) * (1. - smooth(y, -.92, .15))
    lengths = np.column_stack((
        wave * (.27 + .73 * rear) + design["sweep"] * .19 * tips,
        (design["hem"] * (.72 + .24 * side * front) + .72 * design["layers"] * stagger) * tips,
        wave * .52 * (front - rear) + design["layers"] * .26 * side * tips * front,
    ))
    return roots + lengths * free_tips[:, None]


def beard_field(points, design):
    x, y, z = points.T
    jaw = smooth(-y, .02, .48)
    tips = smooth(-y, .21, .92)
    swept = np.sin(-4.2 * y + x * 1.3) * design["wave"] * tips
    shift_x = -design["taper"] * x * jaw + swept + design["flow"] * tips
    layer = np.sin(8.3 * x + 2.3 * y) + .27 * np.sin(13.7 * x - 4.1 * y + .8)
    trim_shape = 1. - design["point"] * (1. - smooth(np.abs(x), .12, .87))
    shift_y = design["trim"] * (.24 * jaw + .76 * tips) * trim_shape
    shift_y += design["layers"] * layer * tips
    shift_z = -design["bulk"] * tips * smooth(z, .54, 1.57) + swept * .51 + design["layers"] * layer * .36 * tips
    return np.column_stack((shift_x, shift_y, shift_z))


def comb_direction(points, design, category, strength):
    """Turn the outer locks around the fitted head while keeping their clearance."""
    x, y, z = points.T
    if category == "Hair":
        turn = design["crownTurn"] * smooth(y, .15, .88)
        turn += design["fallTurn"] * (1. - smooth(y, -.52, .38))
        radius = np.sqrt(x * x + z * z + np.square((y - .43) / .56))
        angle = np.deg2rad(strength * turn * smooth(radius, .87, 1.34))
    else:
        z = z - .54
        angle = np.deg2rad(strength * design["turn"] * smooth(-y, .23, 1.02))
    cosine, sine = np.cos(angle), np.sin(angle)
    swept_x, swept_z = cosine * x + sine * z, cosine * z - sine * x
    if category == "Beard":
        swept_z += .54
    else:
        y = y + strength * design["napeLift"] * smooth(-swept_z, .62, 1.40) * smooth(y, -.65, .48)
    return np.column_stack((swept_x, y, swept_z))


def deformation(points, pivot, fit, design, category, scalp=None, strength=1.):
    scale = np.asarray(fit["scale"])
    center = pivot + [0., 0., fit["depthCenter"]]
    field = hair_field if category == "Hair" else beard_field
    normalized = (points - center) / scale
    if scalp is not None and design.get("anchorScalp"):
        steps = 12
        for _ in range(steps):
            travel = scalp_mobility(normalized, scale, scalp) * (strength / steps)
            normalized += field(normalized, design) * travel[:, None]
            normalized = comb_direction(normalized, design, category, travel)
        return normalized * scale + center
    # Short integration steps yield a smooth deformation even along tightly
    # stacked UV charts and around fast changes in the silhouette.
    before = normalized.copy() if scalp is not None else normalized
    for _ in range(3):
        normalized += strength * field(normalized, design) / 3.
    styled = comb_direction(normalized, design, category, strength)
    if scalp is not None:
        styled = seat_hair(before, styled, scalp)
    return styled * scale + center


class SurfaceFrameCompression(ValueError):
    """The proposed shape needs less displacement to preserve the surface frame."""


def transformed_frames(points, normals, tangents, move, minimum_jacobian):
    positions = move(points)
    steps = np.eye(3) * 0.00012
    jacobian = np.stack([(move(points + step) - move(points - step)) / .00024 for step in steps], axis=2)
    determinant = np.linalg.det(jacobian)
    if not np.isfinite(jacobian).all():
        raise ValueError("A silhouette produced nonfinite surface frames.")
    if determinant.min(initial=1.) <= minimum_jacobian:
        raise SurfaceFrameCompression(f"Silhouette surface frame compressed to {determinant.min():.3f}.")
    mapped_normals = unit(np.einsum("ni,nij->nj", normals, np.linalg.inv(jacobian)))
    along = np.einsum("nij,nj->ni", jacobian, tangents)
    mapped_tangents = unit(along - mapped_normals * (mapped_normals * along).sum(axis=1, keepdims=True))
    return positions, mapped_normals, mapped_tangents


def packet_frame(packet, skin, fit, design, category, physical_shapes, strength=1.):
    matrix = native_matrix(skin)
    inverse = np.linalg.inv(matrix)
    pivot = head_pivot(skin)

    def move(points, scalp):
        world = apply_frame(points, matrix)
        return apply_frame(deformation(world, pivot, fit, design, category, scalp, strength), inverse)

    points = np.asarray(packet["vertices"], dtype=float).reshape(-1, 3)
    normals = np.asarray(packet["normals"], dtype=float).reshape(-1, 3)
    tangents = np.asarray(packet["tangents"], dtype=float).reshape(-1, 4)
    profiles = fit.get("scalps", {})
    fallback = next(iter(profiles.values()), None)
    minimum_jacobian = .12 if profiles else .23
    base = transformed_frames(points, normals, tangents[:, :3], lambda p: move(p, fallback), minimum_jacobian)
    new_points, new_normals, new_tangents = base
    all_points = [new_points]
    authored_shapes = []
    displacement = np.linalg.norm(new_points - points, axis=1)
    ordinal = 0
    for shape in packet["shapes"]:
        frames = []
        scalp = profiles.get(shape["name"], fallback)
        for frame in shape["frames"]:
            posed = tuple(source + np.asarray(frame[key], dtype=float).reshape(-1, 3)
                          for source, key in zip((points, normals, tangents[:, :3]), ("vertices", "normals", "tangents")))
            mapped = transformed_frames(*posed, lambda p: move(p, scalp), minimum_jacobian)
            all_points.append(mapped[0])
            flags = physical_shapes[ordinal]
            deltas = [after - reference for after, reference in zip(mapped, base)]
            if not flags["hasNormals"]:
                deltas[1].fill(0.)
            if not flags["hasTangents"]:
                deltas[2].fill(0.)
            frames.append(dict(weight=frame["weight"], **{
                key: value.astype("<f4").ravel().tolist()
                for key, value in zip(("vertices", "normals", "tangents"), deltas)
            }))
            displacement = np.maximum(displacement, np.linalg.norm(mapped[0] - posed[0], axis=1))
            ordinal += 1
        authored_shapes.append(dict(shape, frames=frames))
    if ordinal != len(physical_shapes):
        raise ValueError("A silhouette did not retain every source physical frame.")
    positions = np.concatenate(all_points)
    minimum = positions.min(axis=0)
    maximum = positions.max(axis=0)
    tangent_values = np.column_stack((new_tangents, tangents[:, 3]))
    bounds = np.r_[(minimum + maximum) / 2, maximum - minimum].astype("<f4")
    updated = dict(packet, vertices=new_points.astype("<f4").ravel().tolist(),
                   normals=new_normals.astype("<f4").ravel().tolist(),
                   tangents=tangent_values.astype("<f4").ravel().tolist(),
                   bounds=bounds.tolist(), shapes=authored_shapes)
    updated["sourceFingerprint"] = models.fingerprint(updated)[0]
    return updated, matrix, float(displacement.max(initial=0.))


def cloth_attachment(source, target, matrix):
    linear = matrix[:3, :3]
    delta = (np.asarray(target["vertices"]) - np.asarray(source["vertices"])).reshape(-1, 3) @ linear.T
    normals = unit(np.asarray(source["normals"]).reshape(-1, 3) @ np.linalg.inv(linear))
    native_tangent = np.asarray(source["tangents"]).reshape(-1, 4)
    tangents = native_tangent[:, :3] @ linear.T
    tangents = unit(tangents - normals * (normals * tangents).sum(axis=1, keepdims=True))
    bitangents = np.cross(normals, tangents) * native_tangent[:, 3, None] * np.sign(np.linalg.det(linear))
    basis = np.stack((tangents, bitangents, normals), axis=1)
    changed_normals = unit(np.asarray(target["normals"]).reshape(-1, 3) @ np.linalg.inv(linear))
    changed_tangents = np.asarray(target["tangents"]).reshape(-1, 4)[:, :3] @ linear.T
    changed_tangents = unit(changed_tangents - changed_normals * (changed_normals * changed_tangents).sum(axis=1, keepdims=True))
    offset, normal, tangent = [np.einsum("nij,nj->ni", basis, vector).tolist()
                               for vector in (delta, changed_normals, changed_tangents)]
    vertices = [dict(anchor=index, native=False, offset=point, normal=n, tangent=t)
                for index, (point, n, t) in enumerate(zip(offset, normal, tangent))]
    return {"vertices": vertices, "matrix": matrix.T.ravel().tolist()}


def style_selector(asset):
    style = asset["internalId"].split("/Graphics/", 1)[1].split("/", 1)[0]
    code = style.rsplit("_", 1)[-1]
    if asset["groomCategory"] == "Hair":
        original = asset["skins"][0]["groomMaterialCapture"][0]["name"]
        match = re.fullmatch(r"M_(Female|Male)_HairShape_([A-Z]+)", original)
        if match:
            code = "_".join(match.groups())
    return style, code


def model_jobs(manifest, census, head_census, settings, selected):
    faces = {}
    for face in head_census["assets"]:
        if "/Graphics/FaceShape_" in face["internalId"]:
            family = face["internalId"].split("/Graphics/", 1)[0]
            faces.setdefault(family, []).append(face)
    assets = {row["asset"]: row for row in manifest["resources"]}
    owners = {}
    head_surfaces = {}
    fits = {}
    scalp_profiles = {}
    used = {"Hair": set(), "Beard": set()}
    for asset in census["assets"]:
        category = asset["groomCategory"]
        if category not in used:
            continue
        style, code = style_selector(asset)
        design = settings[category.lower()][code]
        used[category].add(code)
        if selected and asset["name"] not in selected:
            continue
        entry = assets[asset["internalId"]]
        name = entry["bundles"][0]["file"]
        if entry["type"] != "GameObject" or name in owners:
            raise ValueError("A hairstyle needs one owning prefab and bundle.")
        family = asset["internalId"].split("/Graphics/", 1)[0]
        if family not in fits:
            heads = {face["name"]: head_surface(crown_skin(face)) for face in faces[family]}
            head_surfaces[family] = heads
            fits[family] = scalp_fit(tuple(heads.values()))
        fit = fits[family]
        if category == "Hair":
            if family not in scalp_profiles:
                scalp_profiles[family] = scalp_tables(head_surfaces[family], fit)
            fit = dict(fit, scalps=scalp_profiles[family])
        finish = dict(settings["finish"]["shared"], **settings["finish"][category.lower()])
        owners[name] = dict(asset=asset["name"], prefab=entry["asset"], category=category, style=style,
                            profile=design, fit=fit, skins=asset["skins"], finish=finish)
    if not owners or selected - {job["asset"] for job in owners.values()}:
        raise ValueError("A requested groom is outside the installed resource catalog.")
    for category, keys in used.items():
        if keys != set(settings[category.lower()]):
            raise ValueError("The silhouette profiles no longer match their native design families.")
    return owners


def update_asset(job):
    source, target, receipt_path = map(Path, (job["source"], job["target"], job["receipt"]))
    identity = job["identity"]
    for previous in (receipt_path.parent.parent, *map(Path, job["reuse"])):
        receipt = previous / "Receipts" / receipt_path.name
        payload = previous / "Catalog" / target.name
        if receipt.is_file() and payload.is_file():
            candidate = models.read(receipt)
            if candidate["identity"] == identity and models.sha(payload) == candidate["sha256"]:
                if payload == target or not target.exists() or target.samefile(payload):
                    if not target.exists():
                        target.hardlink_to(payload)
                    candidate["saved"]["target"]["path"] = str(target)
                    models.write(receipt_path, candidate)
                    return candidate, True
    env, file = resources.load_serialized_bundle(source, in_memory=True)

    def resolve(owner, pointer):
        if pointer["m_FileID"] != 0:
            raise ValueError("A groom renderer was resolved outside its typed prefab.")
        return owner, owner.objects[pointer["m_PathID"]]

    root = resources.typed_bundle_asset(env, job["prefab"], "GameObject").deref()
    matched = dict(resources.prefab_skin_renderers(root, resolve))
    cloth_meshes = set()
    for obj in file.objects.values():
        if obj.type.name == "MonoBehaviour":
            deformer = obj.read_typetree().get("deformer")
            if isinstance(deformer, dict) and deformer.get("sharedMesh", {}).get("m_FileID") == 0:
                cloth_meshes.add(deformer["sharedMesh"]["m_PathID"])
    originals = []
    for skin in job["skins"]:
        renderer = matched[skin["path"]]
        _, obj = resolve(file, renderer.read_typetree()["m_Mesh"])
        current = models.export_current_mesh(obj)
        if not np.allclose(resources.renderer_frame(renderer, resolve), native_matrix(skin), rtol=0., atol=1e-5):
            raise ValueError("The adopted groom has another renderer-to-head frame.")
        frames = obj.read_typetree()["m_Shapes"]["shapes"]
        originals.append((skin, obj, current, frames))
    # The same amount of styling applies to every LOD and face pose for a provider.
    for strength in (1., .88, .75, .62):
        try:
            authored = [packet_frame(packet, skin, job["fit"], job["profile"], job["category"], frames, strength)
                        for skin, _, packet, frames in originals]
        except SurfaceFrameCompression:
            if strength == .62:
                raise
        else:
            break
    replacements = []
    meshes = []
    for (skin, obj, current, _), (updated, matrix, motion) in zip(originals, authored):
        if not .001 < motion < .18:
            raise ValueError("The authored silhouette falls outside its centimetre-scale fit.")
        binding = cloth_attachment(current, updated, matrix) if obj.path_id in cloth_meshes else None
        compiled = resources.write_mesh_frame(obj, updated)
        replacements.append(dict(sourceFingerprint=current["sourceFingerprint"], sourcePathIds=[obj.path_id],
                                 compiledTree=compiled, packet=updated, binding=binding,
                                 rendererBoundsPadding=motion))
        meshes.append(dict(renderer=skin["path"], vertices=current["vertexCount"], maximumDisplacementMetres=motion,
                           physicalFrames=sum(len(shape["frames"]) for shape in current["shapes"])))
    updates = []
    for key in sorted(resources.groom_material_identifiers(file)):
        material = file.objects[key].read_typetree()
        values = dict(material["m_SavedProperties"]["m_Floats"])
        updates.append({"pathId": key, "values": {
            field: dict(before=values[field], after=value) for field, value in job["finish"].items()
        }})
    if not updates:
        raise ValueError("The adopted groom has no owned fiber material.")
    result = resources.prepare_standalone_part(source, identity["source"], target, replacements,
                                              export_mesh=models.export_current_mesh, material_updates=updates)
    receipt = dict(file=target.name, identity=identity, sha256=result["target"]["sha256"], bytes=target.stat().st_size,
                   asset=job["asset"], style=job["style"], category=job["category"], name=job["profile"]["name"],
                   silhouetteStrength=strength, sourceMeshes=meshes, saved=result)
    models.write(receipt_path, receipt)
    return receipt, False


def author(args):
    source = models.checked_catalog_path(args.source)
    output = models.checked_catalog_path(args.output)
    previous = [str(models.checked_catalog_path(path)) for path in args.reuse]
    catalog = output / "Catalog"
    manifest_path = Path("Catalog") / models.RESOURCE_FILES["manifest"]
    manifest = models.read(source / manifest_path)
    assembly_path = source / "ResourceAssembly.json"
    assembly = models.read(assembly_path)
    selected = set(args.assets)
    profiles = models.read(args.designs)["silhouettes"]
    census, heads = models.read(args.groom_census), models.read(args.head_census)
    owners = model_jobs(manifest, census, heads, profiles, selected)
    producer = pin(Path(__file__))
    serializer = pin(Path(resources.__file__))
    inputs = [pin(path) for path in (source / manifest_path, args.designs, args.groom_census, args.head_census)]
    source_assembly = pin(assembly_path)
    if assembly["catalogSha256"] != inputs[0]["sha256"]:
        raise ValueError("The fitted source catalog does not match its assembly.")
    stage = dict(source=inputs[0], inputs=inputs, producers=[producer, serializer],
                 selected=sorted(owners), schemaVersion=1)
    ownership = output / "WorkingDirectory.json"
    if ownership.is_file():
        if models.read(ownership) != stage:
            raise ValueError("The output belongs to a different silhouette build.")
    else:
        models.write(ownership, stage)
    catalog.mkdir(exist_ok=True)
    jobs = []
    for row in manifest["bundles"]:
        name = row["file"]
        input_bundle = models.checked_catalog_path(source / "Catalog", name)
        target = models.checked_catalog_path(catalog, name)
        if name in owners:
            data = owners.pop(name)
            identity = dict(source=row["sha256"], generator=producer["sha256"], serializer=serializer["sha256"],
                            inputs=inputs[2:], profile=data["profile"], finish=data["finish"])
            jobs.append(dict(data, identity=identity, source=str(input_bundle), target=str(target),
                             reuse=previous, receipt=str(output / "Receipts" / (name + ".json"))))
        elif not target.exists():
            target.hardlink_to(input_bundle)
        elif not target.samefile(input_bundle):
            raise ValueError("The output has another unchanged asset at " + name)
    if owners:
        raise ValueError("A typed groom has no declared serialized bundle.")
    results = {}
    with ProcessPoolExecutor(max_workers=args.workers) as pool:
        pending = {pool.submit(update_asset, job): job["asset"] for job in jobs}
        for index, task in enumerate(as_completed(pending), 1):
            try:
                receipt, reused = task.result()
            except Exception as error:
                for item in pending:
                    item.cancel()
                raise ValueError("Failed to sculpt " + pending[task]) from error
            results[receipt["file"]] = receipt
            print(f"{index}/{len(jobs)} {receipt['asset']}{' (reused)' if reused else ''}", flush=True)
    proofs = {entry["file"]: entry for entry in assembly["proofs"]}
    for row in manifest["bundles"]:
        if row["file"] in results:
            receipt = results[row["file"]]
            row["sha256"] = receipt["sha256"]
            proof = proofs[row["file"]]
            proof.update(sha256=receipt["sha256"], bytes=receipt["bytes"], allRetainedObjectFieldsExactAfterReferenceRebasing=False)
            proof.setdefault("appearanceFinishing", []).append(dict(
                kind="native-groom-silhouette", beforeSha256=receipt["identity"]["source"],
                proof=pin(output / "Receipts" / (row["file"] + ".json")), sha256=receipt["sha256"],
                authority=dict(inputs=inputs[1:] + [producer, serializer])))
    if any(pin(row["path"]) != row for row in inputs + [source_assembly, producer, serializer]):
        raise ValueError("A silhouette source or producing program changed during generation.")
    models.write(output / manifest_path, manifest)
    summary = dict(source=inputs[0], catalog=pin(output / manifest_path), inputs=inputs[1:],
                   assets=len(results), renderers=sum(len(item["sourceMeshes"]) for item in results.values()),
                   changed=[{key: item[key] for key in ("asset", "style", "category", "name", "file")}
                            for item in (results[file] for file in sorted(results))])
    models.write(output / "Silhouettes.json", summary)
    assembly["catalogSha256"] = summary["catalog"]["sha256"]
    assembly["bytes"] = sum(entry["bytes"] for entry in proofs.values())
    assembly["artAccepted"] = False
    assembly.pop("artAcceptance", None)
    assembly["preparedSource"] = dict(assembly=source_assembly, catalog=inputs[0])
    assembly["retainedSourceInputs"] = models.resource_producer_archives(
        output, (Path(__file__), Path(resources.__file__), args.designs), assembly.get("retainedSourceInputs", []))
    assembly["groomSilhouettes"] = pin(output / "Silhouettes.json")
    models.write(output / "ResourceAssembly.json", assembly)
    print(summary["assets"], "groom providers,", summary["renderers"], "renderers:", summary["catalog"]["path"])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("source", "groom-census", "head-census", "output"):
        parser.add_argument("--" + name, required=True, type=Path)
    parser.add_argument("--designs", type=Path, default=HERE / "groom-designs.json")
    parser.add_argument("--assets", nargs="+", default=[])
    parser.add_argument("--reuse", type=Path, action="append", default=[])
    parser.add_argument("--workers", type=int, choices=range(1, 17), default=4)
    args = parser.parse_args()
    if not any(args.output.resolve().is_relative_to(models.ROOT / path)
               for path in ("tools/game-test/artifacts", "tools/asset-authoring/workspace")):
        parser.error("Use an owned authoring workspace.")
    with resource_work("restyle-grooms", sys.argv[1:], metadata_names=("Silhouettes.json", "ResourceAssembly.json")):
        author(args)


if __name__ == "__main__":
    main()
