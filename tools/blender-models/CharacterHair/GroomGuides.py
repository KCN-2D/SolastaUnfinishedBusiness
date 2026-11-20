"""Build layered hairstyles from guides in a measured native scalp frame."""
from dataclasses import dataclass

import numpy as np


CARD_ROLE = 65
UNDERCOAT_ROLE = 81
SOURCE_ROOT_ROLE = 97


def unit(values):
    return values / np.maximum(np.linalg.norm(values, axis=-1, keepdims=True), 1e-12)


def ease(value):
    bounded = np.clip(value, 0., 1.)
    return bounded * bounded * (3. - 2. * bounded)


def radial(theta, elevation):
    theta, elevation = np.broadcast_arrays(theta, elevation)
    return np.stack((np.sin(theta) * np.cos(elevation), np.sin(elevation),
                     np.cos(theta) * np.cos(elevation)), axis=-1)


@dataclass
class Surface:
    points: np.ndarray
    normals: np.ndarray
    across: np.ndarray
    uv: np.ndarray
    triangles: np.ndarray

    @classmethod
    def join(cls, pieces):
        counts = [len(piece.points) for piece in pieces]
        triangles = np.concatenate([piece.triangles + offset for piece, offset
                                    in zip(pieces, np.r_[0, np.cumsum(counts[:-1])])])
        return cls(*(np.concatenate([getattr(piece, key) for piece in pieces])
                     for key in ("points", "normals", "across", "uv")), triangles)


def strip_grid(rows, columns, *, reverse=False):
    vertices = np.arange(rows * columns).reshape(rows, columns)
    a, b = vertices[:-1, :-1], vertices[:-1, 1:]
    c, d = vertices[1:, :-1], vertices[1:, 1:]
    faces = np.stack((a, b, c, b, d, c), axis=-1).reshape(-1, 3)
    return faces[:, ::-1] if reverse else faces


def ribbon(centers, outward, width, curl, seed, *, rank=0.):
    """The longitudinal direction follows the guide. Transverse curvature gives each card volume."""
    progress = np.linspace(0., 1., len(centers))
    axis = unit(np.gradient(centers, axis=0))
    normal = unit(outward - axis * np.sum(outward * axis, axis=1, keepdims=True))
    transverse = unit(np.cross(axis, normal))
    normal = unit(np.cross(transverse, axis))
    sections = np.array([-1., -0.48, 0., 0.48, 1.])
    crown = (1. - np.square(sections))[None, :, None]
    taper = (0.48 + 0.52 * ease(progress / 0.29)) * (1. - 0.83 * ease((progress - 0.53) / 0.47))
    offsets = np.asarray(width) * taper
    points = (centers[:, None] + transverse[:, None] * (offsets[:, None] * sections)[..., None]
              + normal[:, None] * (offsets[:, None, None] * curl) * crown)
    across = unit(np.gradient(points, axis=1))
    along = unit(np.gradient(points, axis=0))
    normals = unit(np.cross(across, along))
    flip = np.sum(normals * normal[:, None], axis=-1) < 0
    normals[flip] *= -1
    u = (sections + 1.) * 0.5
    uu, vv = np.broadcast_arrays(u, progress[:, None])
    role = CARD_ROLE + seed % 16
    uv = np.stack((uu, vv, np.full(uu.shape, role),
                   np.full(uu.shape, 1. + rank)), axis=-1)
    return Surface(points.reshape(-1, 3), normals.reshape(-1, 3), across.reshape(-1, 3),
                   uv.reshape(-1, 4), strip_grid(len(progress), len(sections)))


class Scalp:
    def __init__(self, center, scale, measure):
        self.center = np.asarray(center, dtype=float)
        self.scale = np.asarray(scale, dtype=float)
        self.measure = measure

    def sample(self, theta, elevation, offset=0.):
        outward = radial(theta, elevation)
        shape = outward.shape
        direction = outward.reshape(-1, 3)
        surface = self.measure(self.center + direction)
        return (self.center + direction * (surface + np.broadcast_to(offset, shape[:-1]).ravel())[:, None]).reshape(shape)

    def outside(self, points, distance):
        vector = points - self.center
        r = np.linalg.norm(vector, axis=-1)
        valid = vector[:, 1] > -0.48
        minimum = self.measure(points) + distance
        outward = np.maximum(r, np.where(valid, minimum, r))
        return self.center + unit(vector) * outward[:, None]

    def physical(self, points):
        return points * self.scale


def circular_margin(theta, line, taper):
    front = np.maximum(np.cos(theta), 0)
    rear = np.maximum(-np.cos(theta), 0)
    left = ease((np.sin(theta) - 0.05) / 0.9)
    return (line[1] + (line[0] - line[1]) * front**2 + (line[2] - line[1]) * rear**1.4
            + taper * left * (1 - rear) * (1 - front))


def scalp_shell(scalp, theta, lower, crown_height, part, seed):
    end = np.linspace(0., 1., 10)
    progress = end[:, None]
    angles = theta[None, :] + (1. - progress) * part * 0.3
    elevation = 1.565 * (1. - progress) + lower[None, :] * progress
    offset = (0.012 + crown_height * 0.36 * np.sin(progress * np.pi))
    offset = offset * (1 - 0.58 * ease((progress - 0.82) / 0.18))
    # Leave room for the coarse scalp triangles over the rounded head.
    # At the visible hairline, taper the extra clearance into the skin.
    rear = np.maximum(-np.cos(angles), 0.)
    clearance = (0.0014 + 0.0018 * rear) * (1 - (1 - rear) * ease((progress - .78) / .22))
    offset = offset + clearance / np.linalg.norm(radial(angles, elevation) * scalp.scale, axis=-1)
    normalized = scalp.sample(angles, elevation, offset)
    shell = scalp.physical(normalized)
    normal = unit((normalized - scalp.center) / scalp.scale)
    reference = np.broadcast_to(np.stack((np.cos(angles), np.zeros_like(angles), -np.sin(angles)), axis=-1), shell.shape)
    u = np.broadcast_to(np.linspace(0., 1., len(theta)), shell.shape[:2])
    uv = np.stack((u, np.broadcast_to(progress, u.shape), np.full(u.shape, UNDERCOAT_ROLE + seed % 16), np.ones(u.shape)), axis=-1)
    return Surface(shell.reshape(-1, 3), normal.reshape(-1, 3), reference.reshape(-1, 3),
                   uv.reshape(-1, 4), strip_grid(*u.shape, reverse=True))


def side_layers(scalp, settings, angles, steps, rng):
    progress = np.linspace(0., 1., steps)
    line, taper, lift = settings["hairline"], settings["tuck"], settings["volume"]
    turn, fall, variation = settings["sweep"], np.asarray(settings["fall"]), settings["wave"]
    run = []
    for ring, offset in enumerate((0.027, 0.067, 0.093)):
        for index, angle in enumerate(angles):
            if ring == 1 and index % 3 == 2 or ring == 2 and index % 2:
                continue
            direction = angle + rng.uniform(-0.07, 0.07) + ring * 0.055
            front = (1 + np.cos(direction)) * 0.5
            side = abs(np.sin(direction))
            drop = np.interp(front, [0., 0.5, 1.], fall[::-1])
            descent = (0.92 + rng.uniform(-0.26, 0.285) - 0.15 * ring) * drop
            root = 1.19 + 0.35 * rng.random()
            fringe_region = ease((front - 0.70) / 0.30) if settings["fringe"] else 0.
            end = circular_margin(direction, line, taper) + 0.19 * fringe_region - (0.08 + 0.175 * rng.random())
            root_turn = turn * (0.61 + 0.70 * front) + settings["part"] * 0.49
            curve = np.sin(progress * np.pi) * (0.58 * root_turn + variation * np.sin(progress * 2 * np.pi + rng.uniform(-np.pi,np.pi)))
            yaw = direction + (1 - progress)**1.28 * root_turn + curve
            elevation = root * (1 - progress) + end * progress
            lift_factor = 0.58 + 0.57 * rng.random()
            body = scalp.sample(yaw, elevation, offset + lift * lift_factor * (0.12 + 0.81 * np.sin(progress * np.pi) ** 1.5))
            lower = ease((progress - 0.59) / 0.41) * descent
            body[:, 1] -= lower
            body[:, 0] += lower * np.sin(direction) * (0.11 + 0.20 * variation)
            body[:, 2] += lower * (0.23 * np.cos(direction) - 0.14)
            body = scalp.outside(body, 0.013 + ring * 0.026)
            width = (0.009 + 0.16 / len(angles)) * (0.62 + 0.72 * rng.random()) * (0.95 + 0.14 * side)
            width *= settings["spread"] * (1.05,0.81,0.45)[ring]
            normal = unit((body - scalp.center) / scalp.scale)
            run.append(ribbon(scalp.physical(body), normal, width, 0.14 + 0.17 * rng.random(),
                              index * 3 + ring * 43, rank=0.13 * ring))
    return run


def face_framing(scalp, settings, steps, rng):
    runs = []
    progress = np.linspace(0., 1., steps)
    cut = settings["fringe"]
    for level, (reach, side_sweep, count) in enumerate(cut):
        for index in range(count):
            strip = (index + 0.5) / count
            side = np.sign(side_sweep) or 1.
            root = settings["part"] * 0.77 - side * (strip - 0.40) * 0.69
            target = side_sweep * (0.64 + 0.53 * strip) + (strip - 0.41) * 0.31
            rise = 1.12 - strip * 0.35 + level * 0.14 + rng.uniform(-0.054, .05)
            fall = reach + (0.5 - strip) * 0.28 + rng.uniform(-0.045,0.055)
            turn = ease(progress)**0.85
            angle = root*(1-turn) + target*turn + side*0.27*np.sin(np.pi*progress)
            vertical = ease(np.clip(progress*1.14-0.04,0,1))
            elevation = rise * (1 - vertical) + fall * vertical
            height = (0.14+0.035*level+0.023*rng.random())*np.sin(np.pi*progress)**1.2 + 0.027
            contour = scalp.sample(angle, elevation, height)
            runs.append(ribbon(scalp.physical(contour), unit((contour-scalp.center)/scalp.scale),
                               (0.007+0.0031*rng.random())*settings["spread"], 0.23, 49 + level*12 + index, rank=0.28))
    return runs


def ridge_groom(scalp, settings, steps, rng):
    """Layer the exposed strip over the scalp without filling the shaved sides."""
    progress = np.linspace(0., 1., steps)
    swell = np.sin(np.pi * progress)
    front = settings["hairline"][0]
    back = np.pi - settings["hairline"][2]

    def contact(lane, start, end, lift):
        length, width = np.broadcast_arrays(progress[:, None], lane)
        arc = start + (end - start) * length + width * 0.12
        offset = settings["sweep"] * (0.035 + 0.13 * swell[:, None])
        side = width * (0.25 + 0.14 * swell[:, None]) + offset
        direction = unit(np.stack((side, np.sin(arc), np.cos(arc)), axis=-1))
        radius = scalp.measure((scalp.center + direction).reshape(-1, 3)).reshape(side.shape)
        return scalp.physical(scalp.center + direction * (radius + lift)[..., None]), direction

    lanes = np.linspace(-1., 1., 17)
    bed = 0.034 + 0.017 * swell[:, None] - 0.088 * np.abs(lanes)**6
    shell, shell_out = contact(lanes, front - .045, back + .025, bed)
    shell_normal = unit(shell_out / scalp.scale)
    across = unit(np.gradient(shell, axis=1))
    u, v = np.broadcast_arrays((lanes + 1) * .5, progress[:, None])
    shell_uv = np.stack((u, v, np.full_like(u, UNDERCOAT_ROLE), np.ones_like(u)), axis=-1)
    runs = [Surface(shell.reshape(-1, 3), shell_normal.reshape(-1, 3), across.reshape(-1, 3),
                    shell_uv.reshape(-1, 4), strip_grid(steps, len(lanes)))]
    for index, lane in enumerate(np.linspace(-0.82, 0.82, 23)):
        root = front - rng.uniform(0.025, 0.085)
        tip = back - rng.uniform(-0.04, .21)
        lift = (0.022 + settings["volume"] * (0.41 + 0.42*rng.random()) * swell**1.4)[:, None]
        curve, outward = contact(lane, root, tip, lift)
        runs.append(ribbon(curve[:, 0], unit(outward[:, 0]/scalp.scale),
                           0.0035+0.0031*rng.random(), 0.18, index+77, rank=.12))
    return runs


def fastened_fall(scalp, settings, steps, rng):
    """Sweep the loose lengths around a tie as narrow, independently tapered locks."""
    tie = settings.get("tie")
    if tie is None:
        return []
    side, elevation, length, bend, twist = tie
    pivot = scalp.sample(np.pi + side, elevation, -0.035 if settings.get("ridge") else 0.)
    progress = np.linspace(0., 1., max(steps, 11))
    if length < 0.35:
        outward = radial(np.pi + side, elevation)
        curve = np.sin(np.pi * progress)
        axis_center = (pivot + outward[None, :] * (0.18*curve)[:,None]
                       + np.column_stack((progress * side * 0.13, 0.055 * np.sin(np.pi*progress), -0.032*progress)))
    else:
        outward = (0.35+0.12*length)*ease(progress/.43)
        axis_center = pivot + np.column_stack((side*progress + bend*progress**2, -length*progress, -outward-length*0.10*progress))
    axis_center = scalp.physical(axis_center)
    axis = unit(np.gradient(axis_center, axis=0))
    origin = np.broadcast_to([0.,0.,-1.], axis.shape)
    side_axis = unit(np.cross(axis, origin))
    near_axis = unit(np.cross(side_axis, axis))
    runs = []
    for index in range(21):
        theta = 2 * np.pi * index / 21
        bundle = index % 3
        phase = theta + twist * progress * np.pi * 2
        amount = (0.011 + 0.011 * rng.random()) * (0.33 + 0.79 * np.sin(progress * np.pi)) * (1.-0.59 * ease(progress))
        if twist:
            amount *= 0.72 + 0.24*np.cos((2*progress*twist-bundle/3)*2*np.pi)
        outward = side_axis * np.cos(phase)[:, None] + near_axis * np.sin(phase)[:, None]
        points = axis_center + outward * amount[:, None]
        runs.append(ribbon(points, outward, 0.007 + 0.004*rng.random(), 0.24, index+117, rank=0.17))
    return runs


def hair_guides(scalp, settings, lod, seed):
    rng = np.random.default_rng(seed)
    sections = (57, 38, 23)[lod]
    steps = (14, 10, 7)[lod]
    angles = np.linspace(-np.pi, np.pi, sections, endpoint=False)
    if settings.get("ridge"):
        pieces = ridge_groom(scalp, settings, steps, rng)
    else:
        boundary = circular_margin(angles, settings["hairline"], settings["tuck"])
        facing = np.cos(angles)
        fall = np.interp(facing, [-1., 0., 1.], settings["fall"][::-1])
        # The nape stays covered when overlying long strands move apart.
        nape = ease((-facing - .15) / .85) * ease((fall - .15) / .48)
        boundary = np.maximum(boundary - .23 * nape, -.51)
        theta = np.r_[angles, np.pi]
        pieces = [scalp_shell(scalp, theta, np.r_[boundary, boundary[0]], settings["volume"], settings["part"], seed)]
        pieces.extend(side_layers(scalp, settings, angles, steps, rng))
        pieces.extend(face_framing(scalp, settings, steps, rng))
    pieces.extend(fastened_fall(scalp, settings, steps, rng))
    return Surface.join(pieces)


def beard_drape(points, seam, settings, lod, seed):
    """Use the measured beard cross sections for a continuous tapered drape."""
    sections, columns = ((22, 37), (16, 27), (12, 19))[lod]
    progress = np.linspace(0., 1., sections)
    tip = np.quantile(points[:, 1], .012)
    length = seam - tip
    heights = seam - length * progress
    # Sample neighboring material at each height, including sparse ends of the source.
    distance = np.abs(heights[:, None] - points[:, 1])
    count = min(len(points), max(15, len(points) // 18))
    near = points[np.argpartition(distance, count-1, axis=1)[:, :count]]
    left, right = np.quantile(near[:, :, 0], [.075, .925], axis=1)
    back, front = np.quantile(near[:, :, 2], [.15, .90], axis=1)
    profiles = np.column_stack(((left + right) * .5, (left - right) * -.5,
                                (back + front) * .5, (back - front) * -.5))
    for _ in range(4):
        profiles[1:-1] = .24 * profiles[:-2] + .52 * profiles[1:-1] + .24 * profiles[2:]
    x, width, z, depth = profiles.T
    root = ease(progress / .16)
    taper = 1 - .97 * ease((progress - .74) / .26)
    width = (0.83 + .14 * root) * np.maximum(width,.006) * taper
    depth = np.maximum(depth,.004) * (0.68 + .14*root) * taper
    x += settings['flow'] * length * progress**1.6
    theta = np.linspace(-np.pi,np.pi,columns)
    heights, angles = np.broadcast_arrays(heights[:, None], theta)
    rounding = 1 + .038 * np.cos(angles*3 + .75 * np.sin(progress[:, None]*np.pi))
    positions = np.stack((x[:,None] + width[:,None]*np.sin(angles)*rounding, heights,
                          z[:,None] + depth[:,None]*np.cos(angles)*rounding),axis=-1)
    across = unit(np.gradient(positions, axis=1))
    normal = unit(np.cross(np.gradient(positions, axis=0), across))
    across[:, -1] = across[:, 0] = unit(across[:, -1] + across[:, 0])
    normal[:, -1] = normal[:, 0] = unit(normal[:, -1] + normal[:, 0])
    u,v = np.broadcast_arrays(np.linspace(0,1,columns),progress[:,None])
    uv = np.stack((u,v,np.full_like(u,UNDERCOAT_ROLE + seed%16),np.ones_like(u)),axis=-1)
    base = Surface(positions.reshape(-1,3),normal.reshape(-1,3),across.reshape(-1,3),
                   uv.reshape(-1,4),strip_grid(sections,columns,reverse=True))
    rng = np.random.default_rng(seed+137)
    locks = []
    rows = np.arange(sections)
    for index, lane in enumerate(np.linspace(-1.89,1.89,(45,28,17)[lod])):
        turn = settings['flow'] * 3.5 * ease(progress) + .17 * np.sin(progress*np.pi + rng.uniform(-1., 1.))
        needle = (lane + turn + np.pi) * (columns-1) / (2*np.pi)
        column = np.floor(needle).astype(int)
        mixture = (needle-column)[:, None]
        trace = positions[rows,column]*(1-mixture) + positions[rows,column+1]*mixture
        outline = unit(normal[rows,column]*(1-mixture) + normal[rows,column+1]*mixture)
        trace += outline * (.0005 + (.001 + .0017*rng.random()) * np.sin(progress*np.pi))[:, None]
        locks.append(ribbon(trace,outline,(.00165 + .00225*rng.random())*(1+settings['bulk']),
                            .23,index+242,rank=.14))
    return base, Surface.join(locks)


def beard_guides(points, normals, faces, settings, lod, seed):
    """Keep each beard's native cheek, chin and lip fitting and feather its perimeter."""
    rng = np.random.default_rng(seed)
    positions = points[faces]
    area = np.linalg.norm(np.cross(positions[:,1]-positions[:,0],positions[:,2]-positions[:,0]),axis=1)
    probability = area/area.sum()
    density = (0.000025, 0.000058, 0.00012)[lod]
    population = int(np.clip(area.sum() * 0.5 / density, (140, 65, 36)[lod], (570, 260, 120)[lod]))
    chosen = faces[rng.choice(len(faces), size=population, p=probability)]
    blend = rng.dirichlet([1.2,1.2,1.2], size=population)
    roots = np.einsum("ni,nij->nj", blend, points[chosen])
    roots_n = unit(np.einsum("ni,nij->nj", blend, normals[chosen]))
    length = np.linspace(0., 1., (7, 5, 4)[lod])[:, None]
    lower, upper = np.quantile(points[faces.ravel(), 1], [.10, .91])
    pieces = []
    for index, (root, normal) in enumerate(zip(roots, roots_n)):
        moustache = ease((root[1] - lower) / (upper-lower) * 3.5 - 2.48) * ease((normal[2] - .16) / .55)
        side = np.tanh(root[0] * 69)
        path = np.array([settings["flow"] - .19*side + .93*moustache*side, -1. + .69*moustache, 0.14])
        flow = unit(path - normal*np.dot(path, normal))
        curl = np.sin(length*np.pi*.76) * (0.0006 + settings["wave"] * 0.009)
        size = (0.0045 + rng.random()*.012) * (0.54+settings["point"])
        size += min(.016, (upper-lower)*.11) * ease((upper-root[1])/(upper-lower))
        center = root + normal*(0.00048 + curl) + flow*length*size
        pieces.append(ribbon(center, np.broadcast_to(normal,center.shape),
                             (0.00042+0.00056*rng.random())*(1+settings["bulk"]),0.13,index,rank=0.08))
    return Surface.join(pieces)
