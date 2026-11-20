"""Sculpt complete hair and beard resources in the fitted native head frame."""
import argparse
from concurrent.futures import ProcessPoolExecutor, as_completed
from functools import lru_cache
import gzip
import json
import os
from pathlib import Path
import re
import sys
from zlib import crc32

for thread_setting in ("OPENBLAS_NUM_THREADS", "OMP_NUM_THREADS", "MKL_NUM_THREADS"):
    os.environ[thread_setting] = "1"
import numpy as np
from PIL import Image

from GroomGuides import (CARD_ROLE, SOURCE_ROOT_ROLE, UNDERCOAT_ROLE, Scalp, Surface,
                         beard_drape, beard_guides, hair_guides)

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


def head_bone_index(skin):
    index = [i for i, bone in enumerate(skin["bones"]) if bone.rsplit("/", 1)[-1] == "Bip001 Head"]
    if len(index) != 1:
        raise ValueError("A native groom requires its unique head pivot.")
    return index[0]


def head_pivot(skin):
    return np.asarray(skin["bonesToPrefab"], dtype=float).reshape(-1, 4, 4)[head_bone_index(skin), 3, :3]


def native_packet(skin):
    path = models.checked_source_path(Path(skin["meshFile"]))
    with gzip.open(path, "rt", encoding="utf-8-sig") as stream:
        packet = json.load(stream)
    if models.fingerprint(packet)[0] != skin["sourceFingerprint"]:
        raise ValueError("The declared native surface has different mesh channels.")
    return packet


def head_surface(skin):
    packet = native_packet(skin)
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


def scalp_table(heads, fit, alternate_heads=None):
    surfaces = np.stack([scalp_envelope(*surface, fit) for surface in heads])
    shared = np.ma.median(np.ma.masked_equal(surfaces, 0), axis=0).filled(1.)
    # Shared meshes have one set of attachment positions. Keep the visible
    # contact line near the actual selection, even when NPC faces differ.
    fitted = smooth_envelope(np.where(surfaces[0] > 0, .72*surfaces[0] + .28*shared, shared))
    if alternate_heads is None:
        return fitted
    outer = surfaces.max(axis=0)
    for scalp in alternate_heads:
        np.maximum(outer, scalp_envelope(*scalp, fit), out=outer)
    outer = smooth_envelope(np.where(outer > 0, outer, fitted))
    elevation = np.linspace(SCALP_LOW, SCALP_HIGH, SCALP_ROWS)[:, None]
    # Upper clearance follows the head shapes that use this hair resource.
    # Taper the extra volume by the ears so it cannot lift the hairline.
    clearance = np.maximum(outer - fitted, 0.) * smooth(elevation, -.30, .40)
    return fitted + clearance


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


def cloth_attachment(source, target, matrix, anchors=None, retained=0):
    linear = matrix[:3, :3]
    if anchors is None:
        anchors = np.arange(source["vertexCount"])
    source_points = np.asarray(source["vertices"]).reshape(-1, 3)[anchors]
    changes = np.asarray(target["vertices"]).reshape(-1, 3) - source_points
    delta = changes @ linear.T
    normals = unit(np.asarray(source["normals"]).reshape(-1, 3)[anchors] @ np.linalg.inv(linear))
    native_tangent = np.asarray(source["tangents"]).reshape(-1, 4)[anchors]
    tangents = native_tangent[:, :3] @ linear.T
    tangents = unit(tangents - normals * (normals * tangents).sum(axis=1, keepdims=True))
    bitangents = np.cross(normals, tangents) * native_tangent[:, 3, None] * np.sign(np.linalg.det(linear))
    basis = np.stack((tangents, bitangents, normals), axis=1)
    changed_normals = unit(np.asarray(target["normals"]).reshape(-1, 3) @ np.linalg.inv(linear))
    changed_tangents = np.asarray(target["tangents"]).reshape(-1, 4)[:, :3] @ linear.T
    changed_tangents = unit(changed_tangents - changed_normals * (changed_normals * changed_tangents).sum(axis=1, keepdims=True))
    offset, normal, tangent = [np.einsum("nij,nj->ni", basis, vector).tolist()
                               for vector in (delta, changed_normals, changed_tangents)]
    vertices = [dict(anchor=int(anchor), native=bool(index < retained and not np.any(changes[index])),
                     offset=point, normal=n, tangent=t)
                for index, (anchor, point, n, t) in enumerate(zip(anchors, offset, normal, tangent))]
    return {"vertices": vertices, "matrix": matrix.T.ravel().tolist()}


@lru_cache(maxsize=40)
def mask_pixels(path, digest):
    path = models.checked_source_path(Path(path))
    if models.sha(path) != digest:
        raise ValueError("The native groom decoration mask changed.")
    with Image.open(path) as image:
        return np.asarray(image.convert("RGB"))[:, :, 0].copy()


def decoration_faces(uv, items, slots):
    masks = {row["slot"]: row["maps"].get("_Mask") for row in items}
    fallback = masks.get(0) if len(masks) == 1 else None
    weights = np.array([[1,0,0],[0,1,0],[0,0,1],[.5,.5,0],[.5,0,.5],[0,.5,.5],[1/3]*3])
    retained = []
    for slot, faces in enumerate(slots):
        mask = masks.get(slot, fallback)
        if not mask:
            retained.append(np.zeros(len(faces), dtype=bool))
            continue
        pixels = mask_pixels(mask["path"], mask["sha256"])
        coordinates = np.einsum("st,ftd->fsd", weights, uv[faces, :2])
        coordinates = np.mod(coordinates * mask["scale"] + mask["offset"], 1.)
        x = np.minimum((coordinates[:, :, 0] * pixels.shape[1]).astype(int), pixels.shape[1]-1)
        y = np.maximum(pixels.shape[0]-1-(coordinates[:, :, 1]*pixels.shape[0]).astype(int), 0)
        retained.append(np.any(pixels[y, x] > 110, axis=1))
    return retained


def closest_vertices(points, source, candidates):
    native = source[candidates]
    radii = np.einsum("ni,ni->n", native, native)
    near = []
    for start in range(0, len(points), 192):
        part = points[start:start+192]
        distance = radii[None, :] - 2 * (part @ native.T)
        near.append(candidates[distance.argmin(axis=1)])
    return np.concatenate(near)


def placed_decoration(positions, surface, slots, decoration):
    """Keep only native metal geometry that is seated in the rebuilt hairstyle."""
    candidates = np.unique(np.concatenate([faces[mask] for faces,mask in zip(slots,decoration)]))
    nearby = np.zeros(len(positions),dtype=bool)
    if len(candidates):
        points = positions[candidates]
        anchors = closest_vertices(points,surface.points,np.arange(len(surface.points)))
        nearby[candidates] = np.linalg.norm(points-surface.points[anchors],axis=1) < 0.012
    return [mask & np.all(nearby[faces],axis=1) for faces,mask in zip(slots,decoration)]


def beard_support(positions, normals, tangents, candidates, style):
    bottom, root = np.quantile(positions[candidates,1], [0.035,0.92])
    height = max(root-bottom,0.023)

    def fit(points):
        output = points.copy()
        depth = smooth(root-points[:,1], 0.20*height, 0.94*height)
        sides = smooth(np.abs(points[:,0]), 0.005, 0.052)
        output[:,0] *= 1. - style["taper"] * depth * (0.74 + 0.60*sides)
        output[:,1] += style["trim"] * min(height,0.102) * depth * (0.47 + 0.29*sides)
        output[:,2] -= min(0.011,0.13*height) * depth * (0.32+0.51*sides)
        return output

    return transformed_frames(positions,normals,tangents,fit,0.15)


def beard_base(skin, matrix, origin, design, lod, seed, head_scale):
    """Fit the smooth source jaw surface, then add short tapered fibers at its outer edge."""
    native = native_packet(skin)
    linear = matrix[:3, :3]
    positions = apply_frame(np.asarray(native["vertices"]).reshape(-1, 3), matrix) - origin
    raw_normal = unit(np.asarray(native["normals"]).reshape(-1, 3) @ np.linalg.inv(linear))
    raw_tangent = np.asarray(native["tangents"]).reshape(-1, 4)
    raw_across = unit(raw_tangent[:, :3] @ linear.T)
    slots = [np.asarray(slot, dtype=int).reshape(-1, 3) for slot in native["subMeshes"]]
    faces = np.concatenate(slots)
    uv = next(np.asarray(channel["values"]).reshape(-1, 4) for channel in native["uv"] if channel["channel"] == 0)
    decoration = decoration_faces(uv, skin["groomMaterialCapture"], slots)
    exposed = np.concatenate([slot[~metal] for slot, metal in zip(slots, decoration)])
    fibers = np.unique(exposed)
    if not len(fibers):
        raise ValueError("A native beard has no fiber surface.")
    organic, normals, across = beard_support(positions, raw_normal, raw_across, fibers, design)

    # Equal source positions at UV seams must receive the same displacement.
    _, first, weld = np.unique(np.rint(positions * 32000), axis=0, return_index=True, return_inverse=True)
    weld = weld.reshape(-1)
    welded = weld[exposed]
    edges = np.concatenate([welded[:, [0, 1]], welded[:, [1, 2]], welded[:, [2, 0]],
                            welded[:, [1, 0]], welded[:, [2, 1]], welded[:, [0, 2]]])
    original_surface = organic[first]
    lower, upper = np.quantile(organic[fibers, 1], [.04, .92])
    height = upper - lower
    depth = smooth(upper - original_surface[:, 1], height * .20, height * .74)
    lower_weight = depth * smooth(height, .07, .17)
    # Allow adjacent lower locks to share a rounded surface across their UV islands.
    # Upper cheek and lip outlines have their own fitted vertices.
    lower_locks = np.flatnonzero(lower_weight > .15)
    adjacent = original_surface[lower_locks]
    gap = np.linalg.norm(adjacent[:, None] - adjacent, axis=-1)
    facing = normals[first[lower_locks]]
    neighboring = (gap > 0) & (gap < .014) & ((facing @ facing.T) > .25)
    close = np.column_stack(np.where(neighboring))
    edges = np.unique(np.concatenate([edges, lower_locks[close]]), axis=0)
    degree = np.bincount(edges[:, 0], minlength=len(first))
    refined = original_surface.copy()
    influence = (.15 + .81 * lower_weight)[:, None]
    for strength in (.45, -.23, .45, -.23, .45, -.23, .45, -.23):
        totals = np.zeros_like(refined)
        np.add.at(totals, edges[:, 0], refined[edges[:, 1]])
        correction = totals / np.maximum(degree[:, None], 1) - refined
        refined += correction * (degree > 0)[:, None] * influence * strength
    delta = refined - original_surface
    maximum = .0014 + .009 * lower_weight
    delta *= np.minimum(1., maximum / np.maximum(np.linalg.norm(delta, axis=1), 1e-12))[:, None]
    organic += delta[weld]

    patches = organic[exposed]
    area_normal = np.cross(patches[:, 1] - patches[:, 0], patches[:, 2] - patches[:, 0])
    opposed = np.sum(area_normal * normals[exposed].sum(axis=1), axis=1) < 0
    area_normal[opposed] *= -1
    smooth_normals = np.zeros_like(original_surface)
    for corner in range(3):
        np.add.at(smooth_normals, welded[:, corner], area_normal)
    smooth_normals = unit(smooth_normals)[weld]
    normal_weight = (.18 + .70 * lower_weight[weld])[:, None]
    normals = unit(normals * (1 - normal_weight) + smooth_normals * normal_weight)
    across = unit(across - normals * np.sum(across * normals, axis=1, keepdims=True))

    lower, upper = np.quantile(organic[fibers, 1], [.01, .98])
    x, y, z = organic.T
    across_strand = np.arctan2(x, z + .095) * 3.5 - y * design["flow"] * 5.
    length = np.clip((upper - y) / max(upper - lower, 1e-5), 0., 1.)
    role = np.full_like(x, SOURCE_ROOT_ROLE + seed % 16)
    flow_uv = np.column_stack((across_strand, length, role, raw_tangent[:, 3]))
    head_width, head_height, _ = head_scale
    organic_height = upper - lower
    draped = organic_height > head_height*.91
    if draped:
        seam = min(upper - organic_height*.24, -head_height*.22)
        side = smooth(np.abs(organic[:,0]), head_height*.24, head_height*.56)
        weight = side * smooth(seam+.043-organic[:,1], 0., .043)
        excess = np.maximum(np.abs(organic[:,0]) - .89 * head_width, 0.)
        organic[:,0] -= np.sign(organic[:,0]) * excess * .70 * weight
        organic[:,2] -= head_height * .053 * weight
    base = Surface(organic, normals, across, flow_uv, faces)
    if draped:
        # Keep the lip, attachments and upper sideburns, with an overlap into
        # the drape so its root is hidden under the directional layers.
        slots = [slot[metal | (organic[slot,1].mean(axis=1) > seam - .009)]
                 for slot,metal in zip(slots,decoration)]
        base.triangles = np.concatenate(slots)
        envelope, locks = beard_drape(organic[fibers], seam+.012, design, lod, seed)
        base = Surface.join([base,envelope,locks])
        exposed = np.concatenate([exposed[organic[exposed,1].mean(axis=1) > seam-.009],
                                  envelope.triangles+len(positions)])
    details = beard_guides(base.points, base.normals, exposed, design, lod, seed)
    return Surface.join([base, details]), slots, uv, len(positions), draped


def rebuilt_groom(packet, skin, fit, design, category, lod, seed, fixed_cloth=None):
    matrix = native_matrix(skin)
    inverse = np.linalg.inv(matrix)
    linear = matrix[:3, :3]
    origin = head_pivot(skin) + np.array([0., 0., fit["depthCenter"]])
    slots = [np.asarray(values, dtype=int).reshape(-1, 3) for values in packet["subMeshes"]]
    original = np.asarray(packet["vertices"], dtype=float).reshape(-1, 3)
    source_n = np.asarray(packet["normals"],dtype=float).reshape(-1,3)
    source_t = np.asarray(packet["tangents"],dtype=float).reshape(-1,4)
    physical = apply_frame(original, matrix) - origin
    uv = {item["channel"]: np.asarray(item["values"], dtype="<f4").reshape(-1,4) for item in packet["uv"]}
    roles = uv[2][:, 2]
    recreated = roles >= CARD_ROLE
    supports = (roles < 0.5) | recreated
    procedural = recreated & (roles < SOURCE_ROOT_ROLE)
    decoration = [
        mask & ~np.any(procedural[faces], axis=1)
        for faces, mask in zip(slots, decoration_faces(uv[0], skin["groomMaterialCapture"], slots))
    ]
    support_faces = [np.all(supports[faces], axis=1) & ~metal for faces, metal in zip(slots, decoration)]
    source_faces = np.concatenate([faces[keep] for faces,keep in zip(slots,support_faces)])
    if not len(source_faces):
        raise ValueError("A groom has no native support surface for new guides.")
    candidates = np.unique(source_faces)
    rigid_guides = None
    if category == "Hair":
        envelope = fit["scalp"]
        scalp = Scalp(SCALP_CENTER, fit["scale"], lambda p: scalp_query(p,envelope)[1])
        surface = hair_guides(scalp, design, lod, seed)
        decoration = placed_decoration(physical,surface,slots,decoration)
        keep_triangles = [faces[keep] for faces,keep in zip(slots,decoration)]
        surface_slots = {}
        matched_uv = None
        if fit.get("coverScalp"):
            rigid_guides = (surface.uv[:, 2] >= UNDERCOAT_ROLE) & (surface.uv[:, 2] < SOURCE_ROOT_ROLE)
    else:
        surface, beard_slots, matched_uv, base_vertices, draped = beard_base(
            skin, matrix, origin, design, lod, seed, fit["scale"])
        if draped:
            rigid_guides = np.arange(len(surface.points)) >= base_vertices
        if len(beard_slots) > len(slots):
            raise ValueError("A native beard lost a material slot.")
        keep_triangles = [faces[:0] for faces in slots]
        surface_slots = dict(enumerate(beard_slots))
    retained = np.unique(np.concatenate(keep_triangles))
    indexed = np.zeros(packet["vertexCount"],dtype=int)
    indexed[retained] = np.arange(len(retained))
    attached = closest_vertices(surface.points, physical, candidates)
    weights = np.asarray(packet["weights"]).reshape(-1, 4)
    bone_indices = np.asarray(packet["boneIndices"], dtype=int).reshape(-1, 4)
    if rigid_guides is not None:
        head_weight = (weights * (bone_indices == head_bone_index(skin))).sum(axis=1)
        rigid = candidates[head_weight[candidates] >= .995]
        if not len(rigid):
            raise ValueError("The scalp has no rigid head attachment in the source groom.")
        if fixed_cloth is not None:
            fixed = np.intersect1d(rigid, fixed_cloth, assume_unique=True)
            if len(fixed):
                rigid = fixed
        attached[rigid_guides] = closest_vertices(surface.points[rigid_guides], physical, rigid)
    anchors = np.concatenate((retained, attached))
    count = len(anchors)
    core_slots = [int(np.count_nonzero(keep)) for keep in support_faces]
    owning_slot = int(np.argmax(core_slots))
    output_faces = [indexed[faces].reshape(-1) for faces in keep_triangles]
    for slot, faces in surface_slots.items():
        output_faces[slot] = faces.reshape(-1)
    guide_faces = surface.triangles[sum(map(len, surface_slots.values())):]
    output_faces[owning_slot] = np.r_[output_faces[owning_slot],
                                     (guide_faces + len(retained)).reshape(-1)]

    guide_positions = apply_frame(surface.points + origin, inverse)
    guide_normals = unit(surface.normals @ linear)
    guide_tangents = surface.across @ inverse[:3,:3].T
    guide_tangents = unit(guide_tangents - guide_normals*np.sum(guide_tangents*guide_normals,axis=1,keepdims=True))
    hand = np.sign(np.linalg.det(linear))
    handedness = np.full(len(guide_tangents), hand)
    if matched_uv is not None:
        handedness[:base_vertices] = surface.uv[:base_vertices, 3]
    tangent = np.column_stack((guide_tangents, handedness))
    point = np.concatenate((original[retained], guide_positions))
    channel_values = {k: channels[anchors].copy() for k,channels in uv.items()}
    channel_values[0][len(retained):, 2:] = (2048 + 4096 * 2048, 2048 + 4096 * 4095)
    if matched_uv is not None:
        channel_values[0][:base_vertices, :2] = matched_uv[:, :2]
    channel_values[2][len(retained):, :] = surface.uv
    if 3 in channel_values:
        channel_values[3][len(retained):] = 0.
    if 4 in channel_values:
        channel_values[4][len(retained):, :2] = (0.,1.)

    def floats(rows):
        return np.asarray(rows,dtype="<f4").reshape(-1).tolist()

    shapes = []
    minima, maxima = point.min(axis=0), point.max(axis=0)
    for shape in packet["shapes"]:
        native_head_fit = shape["name"].casefold() in fit.get("nativeHeadFitShapes", ())
        frames = []
        for frame in shape["frames"]:
            position_delta = np.asarray(frame["vertices"],dtype="<f4").reshape(-1,3)[anchors]
            # The new scalp and locks already fit the measured head families.
            # Keep the original fit morph on retained decorations only.
            if native_head_fit:
                authored = position_delta[len(retained):]
                if category == "Hair":
                    authored[:] = 0
                elif rigid_guides is not None:
                    authored[rigid_guides] = 0
            posed = point + position_delta
            minima, maxima = np.minimum(minima, posed.min(axis=0)), np.maximum(maxima, posed.max(axis=0))
            directions = {name:np.asarray(frame[name],dtype="<f4").reshape(-1,3)[anchors].copy()
                          for name in ("normals","tangents")}
            for values in directions.values():
                values[len(retained):] = 0
            frames.append(dict(weight=frame["weight"], vertices=floats(position_delta),
                               **{name:floats(values) for name,values in directions.items()}))
        shapes.append(dict(shape,frames=frames))
    result = dict(packet, vertexCount=count, vertices=floats(point),
                  normals=floats(np.concatenate((source_n[retained],guide_normals))),
                  tangents=floats(np.concatenate((source_t[retained],tangent))),
                  weights=floats(weights[anchors]),
                  boneIndices=bone_indices[anchors].ravel().tolist(),
                  colors=floats(np.asarray(packet["colors"]).reshape(packet["vertexCount"],-1)[anchors]),
                  uv=[dict(item,values=floats(channel_values[item["channel"]])) for item in packet["uv"]],
                  subMeshes=[indices.tolist() for indices in output_faces], shapes=shapes,
                  bounds=floats(np.r_[(minima+maxima)*0.5,maxima-minima]))
    result["sourceFingerprint"] = models.fingerprint(result)[0]
    move = float(np.linalg.norm(guide_positions-original[attached],axis=1).max())
    return result, matrix, move, anchors, len(retained)


def style_selector(asset):
    style = asset["internalId"].split("/Graphics/", 1)[1].split("/", 1)[0]
    code = style.rsplit("_", 1)[-1]
    if asset["groomCategory"] == "Hair":
        original = asset["skins"][0]["groomMaterialCapture"][0]["name"]
        match = re.fullmatch(r"M_(Female|Male)_HairShape_([A-Z]+)", original)
        if match:
            code = "_".join(match.groups())
    return style, code


def model_jobs(manifest, census, head_census, settings, selected, fitted_heads=None):
    faces = {}
    families_by_head = {}
    for face in head_census["assets"]:
        if "/Graphics/FaceShape_" in face["internalId"]:
            family = face["internalId"].split("/Graphics/", 1)[0]
            faces.setdefault(family, []).append(face)
            families_by_head[face["name"].casefold()] = family
    head_fit_shapes = frozenset(families_by_head) | {
        f"{family.rsplit('/', 1)[-1]}_FaceShape".casefold() for family in faces
    }

    @lru_cache(maxsize=None)
    def family_surfaces(family):
        return tuple(head_surface(crown_skin(face)) for face in faces[family])

    assets = {row["asset"]: row for row in manifest["resources"]}
    owners = {}
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
            fits[family] = scalp_fit(family_surfaces(family))
        fit = fits[family]
        if category == "Hair":
            cover = not design.get("ridge")
            wearers = fitted_heads.get(asset["name"], []) if fitted_heads and cover else []
            alternate = tuple(sorted({families_by_head[head.casefold()] for head in wearers} - {family}))
            profile_key = family, cover, alternate
            if profile_key not in scalp_profiles:
                shapes = (head for owner in alternate for head in family_surfaces(owner)) if cover else None
                scalp_profiles[profile_key] = scalp_table(family_surfaces(family), fit, shapes)
            fit = dict(fit, scalp=scalp_profiles[profile_key], coverScalp=cover)
        fit = dict(fit, nativeHeadFitShapes=head_fit_shapes)
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
    components = {obj.path_id: obj.read_typetree() for obj in file.objects.values() if obj.type.name == "MonoBehaviour"}
    cloth_meshes = set()
    for component in components.values():
        deformer = component.get("deformer")
        if isinstance(deformer, dict) and deformer.get("sharedMesh", {}).get("m_FileID") == 0:
            cloth_meshes.add(deformer["sharedMesh"]["m_PathID"])
    originals = []
    for skin in job["skins"]:
        renderer = matched[skin["path"]]
        _, obj = resolve(file, renderer.read_typetree()["m_Mesh"])
        current = models.export_current_mesh(obj)
        if not np.allclose(resources.renderer_frame(renderer, resolve), native_matrix(skin), rtol=0., atol=1e-5):
            raise ValueError("The adopted groom has another renderer-to-head frame.")
        originals.append((skin, obj, current))
    replacements = []
    meshes = []
    for skin, obj, current in originals:
        lod = int(skin["path"].rsplit("LOD", 1)[-1])
        fixed_cloth = resources.fixed_cloth_render_vertices(components, obj.path_id) if obj.path_id in cloth_meshes else None
        updated, matrix, motion, anchors, retained = rebuilt_groom(
            current, skin, job["fit"], job["profile"], job["category"], lod,
            crc32(job["style"].encode("utf-8")), fixed_cloth)
        if not 0. <= motion < .32:
            raise ValueError("The authored guide falls outside the head and shoulder fitting range.")
        binding = cloth_attachment(current, updated, matrix, anchors, retained) if obj.path_id in cloth_meshes else None
        compiled = resources.write_mesh_frame(obj, updated, vertex_sources=anchors)
        replacements.append(dict(sourceFingerprint=current["sourceFingerprint"], sourcePathIds=[obj.path_id],
                                 compiledTree=compiled, packet=updated, binding=binding,
                                 rendererBoundsPadding=motion))
        meshes.append(dict(renderer=skin["path"], vertices=updated["vertexCount"], sourceVertices=current["vertexCount"],
                           guideVertices=len(anchors)-retained, retainedVertices=retained, maximumDisplacementMetres=motion,
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
                   sourceMeshes=meshes, saved=result)
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
    fitted_heads = models.read(args.fit_heads)["grooms"] if args.fit_heads else None
    owners = model_jobs(manifest, census, heads, profiles, selected, fitted_heads)
    producer = pin(Path(__file__))
    guide_producer = pin(HERE / "GroomGuides.py")
    serializer = pin(Path(resources.__file__))
    inputs = [pin(path) for path in (source / manifest_path, args.designs, args.groom_census, args.head_census)]
    if args.fit_heads:
        inputs.append(pin(args.fit_heads))
    source_assembly = pin(assembly_path)
    if assembly["catalogSha256"] != inputs[0]["sha256"]:
        raise ValueError("The fitted source catalog does not match its assembly.")
    stage = dict(source=inputs[0], inputs=inputs, producers=[producer, guide_producer, serializer],
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
            identity = dict(source=row["sha256"], generator=producer["sha256"], guideGenerator=guide_producer["sha256"],
                            serializer=serializer["sha256"],
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
                authority=dict(inputs=inputs[1:] + [producer, guide_producer, serializer])))
    if any(pin(row["path"]) != row for row in inputs + [source_assembly, producer, guide_producer, serializer]):
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
        output, (Path(__file__), HERE / "GroomGuides.py", Path(resources.__file__), args.designs),
        assembly.get("retainedSourceInputs", []))
    assembly["groomSilhouettes"] = pin(output / "Silhouettes.json")
    models.write(output / "ResourceAssembly.json", assembly)
    print(summary["assets"], "groom providers,", summary["renderers"], "renderers:", summary["catalog"]["path"])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("source", "groom-census", "head-census", "output"):
        parser.add_argument("--" + name, required=True, type=Path)
    parser.add_argument("--designs", type=Path, default=HERE / "groom-designs.json")
    parser.add_argument("--fit-heads", type=Path)
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
