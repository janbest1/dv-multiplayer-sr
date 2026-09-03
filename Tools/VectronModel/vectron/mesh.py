"""Dependency-free polygon mesh toolkit for the procedural Vectron generator.

Everything is built from a handful of primitives (boxes, revolved profiles,
lofted section rings). Meshes carry a material name and a smoothing flag per
face; normals and UVs are derived at export time.

Model space used by the builders:
    +X  towards the front of the locomotive (loco is symmetric around X=0)
    +Y  to the left
    +Z  up, Z=0 is top of rail
Exporters convert to whatever the target engine expects.
"""

from __future__ import annotations

import math
from typing import Callable, Iterable, Sequence

Vec3 = tuple[float, float, float]
Vec2 = tuple[float, float]

TAU = math.pi * 2.0


# --------------------------------------------------------------------------
# vector helpers
# --------------------------------------------------------------------------

def vadd(a: Vec3, b: Vec3) -> Vec3:
    return (a[0] + b[0], a[1] + b[1], a[2] + b[2])


def vsub(a: Vec3, b: Vec3) -> Vec3:
    return (a[0] - b[0], a[1] - b[1], a[2] - b[2])


def vmul(a: Vec3, s: float) -> Vec3:
    return (a[0] * s, a[1] * s, a[2] * s)


def vcross(a: Vec3, b: Vec3) -> Vec3:
    return (a[1] * b[2] - a[2] * b[1],
            a[2] * b[0] - a[0] * b[2],
            a[0] * b[1] - a[1] * b[0])


def vdot(a: Vec3, b: Vec3) -> float:
    return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]


def vlen(a: Vec3) -> float:
    return math.sqrt(vdot(a, a))


def vnorm(a: Vec3) -> Vec3:
    n = vlen(a)
    if n < 1e-12:
        return (0.0, 0.0, 1.0)
    return (a[0] / n, a[1] / n, a[2] / n)


def lerp(a: float, b: float, t: float) -> float:
    return a + (b - a) * t


def catmull_rom(p0: float, p1: float, p2: float, p3: float, t: float) -> float:
    """Uniform Catmull-Rom interpolation between p1 and p2."""
    t2 = t * t
    t3 = t2 * t
    return 0.5 * ((2.0 * p1)
                  + (-p0 + p2) * t
                  + (2.0 * p0 - 5.0 * p1 + 4.0 * p2 - p3) * t2
                  + (-p0 + 3.0 * p1 - 3.0 * p2 + p3) * t3)


# --------------------------------------------------------------------------
# mesh
# --------------------------------------------------------------------------

class Mesh:
    """An indexed polygon mesh. Faces may be triangles, quads or n-gons."""

    __slots__ = ("name", "verts", "faces", "mats", "smooth")

    def __init__(self, name: str = "mesh") -> None:
        self.name = name
        self.verts: list[Vec3] = []
        self.faces: list[tuple[int, ...]] = []
        self.mats: list[str] = []
        self.smooth: list[bool] = []

    # -- construction ------------------------------------------------------

    def add_vert(self, p: Vec3) -> int:
        self.verts.append((float(p[0]), float(p[1]), float(p[2])))
        return len(self.verts) - 1

    def add_ring(self, pts: Iterable[Vec3]) -> list[int]:
        return [self.add_vert(p) for p in pts]

    def add_face(self, idx: Sequence[int], mat: str, smooth: bool = False) -> None:
        if len(idx) < 3:
            return
        # drop degenerate faces (repeated indices are common on collapsed rings)
        clean: list[int] = []
        for i in idx:
            if not clean or clean[-1] != i:
                clean.append(i)
        if len(clean) > 2 and clean[0] == clean[-1]:
            clean.pop()
        if len(clean) < 3:
            return
        self.faces.append(tuple(clean))
        self.mats.append(mat)
        self.smooth.append(smooth)

    def add_quad(self, a: int, b: int, c: int, d: int, mat: str, smooth: bool = False) -> None:
        self.add_face((a, b, c, d), mat, smooth)

    def extend(self, other: "Mesh") -> "Mesh":
        off = len(self.verts)
        self.verts.extend(other.verts)
        for f, m, s in zip(other.faces, other.mats, other.smooth):
            self.faces.append(tuple(i + off for i in f))
            self.mats.append(m)
            self.smooth.append(s)
        return self

    # -- transforms (in place) --------------------------------------------

    def apply(self, fn: Callable[[Vec3], Vec3]) -> "Mesh":
        self.verts = [fn(v) for v in self.verts]
        return self

    def translate(self, dx: float = 0.0, dy: float = 0.0, dz: float = 0.0) -> "Mesh":
        return self.apply(lambda v: (v[0] + dx, v[1] + dy, v[2] + dz))

    def scale(self, sx: float = 1.0, sy: float = 1.0, sz: float = 1.0) -> "Mesh":
        m = self.apply(lambda v: (v[0] * sx, v[1] * sy, v[2] * sz))
        if sx * sy * sz < 0:
            m.flip()
        return m

    def rotate_x(self, ang: float) -> "Mesh":
        c, s = math.cos(ang), math.sin(ang)
        return self.apply(lambda v: (v[0], v[1] * c - v[2] * s, v[1] * s + v[2] * c))

    def rotate_y(self, ang: float) -> "Mesh":
        c, s = math.cos(ang), math.sin(ang)
        return self.apply(lambda v: (v[0] * c + v[2] * s, v[1], -v[0] * s + v[2] * c))

    def rotate_z(self, ang: float) -> "Mesh":
        c, s = math.cos(ang), math.sin(ang)
        return self.apply(lambda v: (v[0] * c - v[1] * s, v[0] * s + v[1] * c, v[2]))

    def flip(self) -> "Mesh":
        self.faces = [tuple(reversed(f)) for f in self.faces]
        return self

    def mirrored_x(self, name: str | None = None) -> "Mesh":
        """Copy mirrored across the X=0 plane (front/rear symmetry)."""
        out = self.copy(name or self.name)
        out.apply(lambda v: (-v[0], v[1], v[2]))
        out.flip()
        return out

    def mirrored_y(self, name: str | None = None) -> "Mesh":
        """Copy mirrored across the Y=0 plane (left/right symmetry)."""
        out = self.copy(name or self.name)
        out.apply(lambda v: (v[0], -v[1], v[2]))
        out.flip()
        return out

    def copy(self, name: str | None = None) -> "Mesh":
        out = Mesh(name or self.name)
        out.verts = list(self.verts)
        out.faces = list(self.faces)
        out.mats = list(self.mats)
        out.smooth = list(self.smooth)
        return out

    def set_material(self, mat: str) -> "Mesh":
        self.mats = [mat] * len(self.faces)
        return self

    # -- analysis ----------------------------------------------------------

    def triangles(self) -> list[tuple[int, int, int]]:
        tris: list[tuple[int, int, int]] = []
        for f in self.faces:
            for k in range(1, len(f) - 1):
                tris.append((f[0], f[k], f[k + 1]))
        return tris

    def signed_volume(self) -> float:
        v = self.verts
        total = 0.0
        for a, b, c in self.triangles():
            pa, pb, pc = v[a], v[b], v[c]
            total += vdot(pa, vcross(pb, pc))
        return total / 6.0

    def ensure_outward(self) -> "Mesh":
        """Flip the whole mesh if it is a closed solid wound inside-out."""
        if self.signed_volume() < 0.0:
            self.flip()
        return self

    def bounds(self) -> tuple[Vec3, Vec3]:
        if not self.verts:
            return ((0.0, 0.0, 0.0), (0.0, 0.0, 0.0))
        xs = [v[0] for v in self.verts]
        ys = [v[1] for v in self.verts]
        zs = [v[2] for v in self.verts]
        return ((min(xs), min(ys), min(zs)), (max(xs), max(ys), max(zs)))

    def stats(self) -> tuple[int, int, int]:
        return len(self.verts), len(self.faces), len(self.triangles())

    def __repr__(self) -> str:
        v, f, t = self.stats()
        return f"<Mesh {self.name}: {v} verts, {f} faces, {t} tris>"


# --------------------------------------------------------------------------
# primitives
# --------------------------------------------------------------------------

def box(x0: float, x1: float, y0: float, y1: float, z0: float, z1: float,
        mat: str, name: str = "box", smooth: bool = False) -> Mesh:
    m = Mesh(name)
    p = [(x0, y0, z0), (x1, y0, z0), (x1, y1, z0), (x0, y1, z0),
         (x0, y0, z1), (x1, y0, z1), (x1, y1, z1), (x0, y1, z1)]
    m.add_ring(p)
    for f in ((0, 3, 2, 1), (4, 5, 6, 7), (0, 1, 5, 4),
              (1, 2, 6, 5), (2, 3, 7, 6), (3, 0, 4, 7)):
        m.add_face(f, mat, smooth)
    return m.ensure_outward()


def box_c(cx: float, cy: float, cz: float, sx: float, sy: float, sz: float,
          mat: str, name: str = "box") -> Mesh:
    """Box from centre + size."""
    return box(cx - sx / 2, cx + sx / 2, cy - sy / 2, cy + sy / 2,
               cz - sz / 2, cz + sz / 2, mat, name)


def chamfered_box(cx: float, cy: float, cz: float, sx: float, sy: float, sz: float,
                  cham: float, mat: str, name: str = "cbox") -> Mesh:
    """Box with the four vertical edges chamfered - reads much better than a
    plain cube on equipment cabinets at almost no extra cost."""
    m = Mesh(name)
    hx, hy = sx / 2, sy / 2
    c = min(cham, hx * 0.9, hy * 0.9)
    ring = [(hx - c, -hy, 0), (hx, -hy + c, 0), (hx, hy - c, 0), (hx - c, hy, 0),
            (-hx + c, hy, 0), (-hx, hy - c, 0), (-hx, -hy + c, 0), (-hx + c, -hy, 0)]
    lo = m.add_ring([(p[0], p[1], -sz / 2) for p in ring])
    hi = m.add_ring([(p[0], p[1], sz / 2) for p in ring])
    n = len(ring)
    for i in range(n):
        j = (i + 1) % n
        m.add_quad(lo[i], lo[j], hi[j], hi[i], mat, False)
    m.add_face(tuple(reversed(lo)), mat, False)
    m.add_face(tuple(hi), mat, False)
    m.ensure_outward()
    return m.translate(cx, cy, cz)


def revolve(profile: Sequence[Vec2], segments: int, mat: str, name: str = "rev",
            axis: str = "z", smooth: bool = True, close: bool = True) -> Mesh:
    """Revolve a 2D profile of (radius, height) pairs around an axis.

    The profile is given in the (r, h) plane; r must be >= 0. Caps are added
    automatically where the profile does not already reach r = 0.
    """
    m = Mesh(name)
    rings: list[list[int]] = []
    for r, h in profile:
        ring: list[int] = []
        if r <= 1e-9:
            idx = m.add_vert((0.0, 0.0, h))
            ring = [idx] * segments
        else:
            for s in range(segments):
                a = TAU * s / segments
                ring.append(m.add_vert((r * math.cos(a), r * math.sin(a), h)))
        rings.append(ring)
    for i in range(len(rings) - 1):
        a, b = rings[i], rings[i + 1]
        flat = abs(profile[i][1] - profile[i + 1][1]) < 1e-9  # a disc, keep flat
        for s in range(segments):
            t = (s + 1) % segments
            m.add_face((a[s], a[t], b[t], b[s]), mat, smooth and not flat)
    if close:
        if profile[0][0] > 1e-9:
            m.add_face(tuple(rings[0]), mat, False)
        if profile[-1][0] > 1e-9:
            m.add_face(tuple(reversed(rings[-1])), mat, False)
    m.ensure_outward()
    if axis == "x":
        m.rotate_y(math.pi / 2)
    elif axis == "y":
        m.rotate_x(-math.pi / 2)
    return m


def cylinder(radius: float, length: float, segments: int, mat: str,
             name: str = "cyl", axis: str = "z", caps: bool = True) -> Mesh:
    prof = [(0.0, 0.0)] if caps else []
    prof += [(radius, 0.0), (radius, length)]
    if caps:
        prof += [(0.0, length)]
    return revolve(prof, segments, mat, name, axis=axis)


def tube_along(points: Sequence[Vec3], radius: float, segments: int, mat: str,
               name: str = "tube", caps: bool = True) -> Mesh:
    """Sweep a circle along a polyline - used for handrails, pipes and coils."""
    m = Mesh(name)
    if len(points) < 2:
        return m
    rings: list[list[int]] = []
    prev_up: Vec3 = (0.0, 0.0, 1.0)
    for i, p in enumerate(points):
        if i == 0:
            tan = vnorm(vsub(points[1], points[0]))
        elif i == len(points) - 1:
            tan = vnorm(vsub(points[-1], points[-2]))
        else:
            tan = vnorm(vadd(vnorm(vsub(points[i], points[i - 1])),
                             vnorm(vsub(points[i + 1], points[i]))))
        ref = prev_up
        if abs(vdot(ref, tan)) > 0.95:
            ref = (1.0, 0.0, 0.0) if abs(tan[0]) < 0.9 else (0.0, 1.0, 0.0)
        side = vnorm(vcross(tan, ref))
        up = vnorm(vcross(side, tan))
        prev_up = up
        ring = []
        for s in range(segments):
            a = TAU * s / segments
            off = vadd(vmul(side, radius * math.cos(a)), vmul(up, radius * math.sin(a)))
            ring.append(m.add_vert(vadd(p, off)))
        rings.append(ring)
    for i in range(len(rings) - 1):
        a, b = rings[i], rings[i + 1]
        for s in range(segments):
            t = (s + 1) % segments
            m.add_face((a[s], a[t], b[t], b[s]), mat, True)
    if caps:
        m.add_face(tuple(reversed(rings[0])), mat, False)
        m.add_face(tuple(rings[-1]), mat, False)
    return m.ensure_outward()


def coil_spring(radius: float, wire: float, height: float, turns: float,
                segments: int = 8, steps_per_turn: int = 10,
                mat: str = "steel", name: str = "spring") -> Mesh:
    """A real helical coil - cheap enough for LOD0 bogie springs."""
    pts: list[Vec3] = []
    total = max(4, int(turns * steps_per_turn))
    for i in range(total + 1):
        t = i / total
        a = TAU * turns * t
        pts.append((radius * math.cos(a), radius * math.sin(a), height * t))
    return tube_along(pts, wire, segments, mat, name)


def loft(rings: Sequence[Sequence[Vec3]], mat: str, name: str = "loft",
         closed_ring: bool = True, cap_start: bool = True, cap_end: bool = True,
         smooth: bool = True) -> Mesh:
    """Skin a sequence of equally sized point rings."""
    m = Mesh(name)
    idx = [m.add_ring(r) for r in rings]
    n = len(rings[0])
    for i in range(len(idx) - 1):
        a, b = idx[i], idx[i + 1]
        last = n if closed_ring else n - 1
        for s in range(last):
            t = (s + 1) % n
            m.add_face((a[s], a[t], b[t], b[s]), mat, smooth)
    if cap_start:
        m.add_face(tuple(reversed(idx[0])), mat, False)
    if cap_end:
        m.add_face(tuple(idx[-1]), mat, False)
    return m.ensure_outward()


def quad_strip(row_a: Sequence[Vec3], row_b: Sequence[Vec3], mat: str,
               name: str = "strip", smooth: bool = True) -> Mesh:
    """Open surface between two equally long point rows (single sided)."""
    m = Mesh(name)
    a = m.add_ring(row_a)
    b = m.add_ring(row_b)
    for i in range(len(a) - 1):
        m.add_face((a[i], a[i + 1], b[i + 1], b[i]), mat, smooth)
    return m


def panel(pts_lo: Sequence[Vec3], pts_hi: Sequence[Vec3], mat: str,
          name: str = "panel", smooth: bool = False) -> Mesh:
    """Two-sided flat-ish panel (front and back), used for thin plates."""
    m = quad_strip(pts_lo, pts_hi, mat, name, smooth)
    back = m.copy()
    back.flip()
    return m.extend(back)


# --------------------------------------------------------------------------
# shading helpers
# --------------------------------------------------------------------------

def face_normal(verts: Sequence[Vec3], face: Sequence[int]) -> Vec3:
    """Newell's method - robust for n-gons and slightly non-planar quads."""
    nx = ny = nz = 0.0
    n = len(face)
    for i in range(n):
        a = verts[face[i]]
        b = verts[face[(i + 1) % n]]
        nx += (a[1] - b[1]) * (a[2] + b[2])
        ny += (a[2] - b[2]) * (a[0] + b[0])
        nz += (a[0] - b[0]) * (a[1] + b[1])
    return vnorm((nx, ny, nz))


def corner_normals(mesh: Mesh, angle_deg: float = 38.0) -> list[list[Vec3]]:
    """Per-face-corner normals, averaged only across edges below the crease
    angle. Faces flagged as flat always keep their own normal."""
    cos_lim = math.cos(math.radians(angle_deg))
    fn = [face_normal(mesh.verts, f) for f in mesh.faces]
    incident: dict[int, list[int]] = {}
    for fi, f in enumerate(mesh.faces):
        if not mesh.smooth[fi]:
            continue
        for vi in f:
            incident.setdefault(vi, []).append(fi)
    out: list[list[Vec3]] = []
    for fi, f in enumerate(mesh.faces):
        if not mesh.smooth[fi]:
            out.append([fn[fi]] * len(f))
            continue
        row: list[Vec3] = []
        for vi in f:
            acc = (0.0, 0.0, 0.0)
            for oj in incident.get(vi, ()):  # noqa: PLC0206
                if vdot(fn[oj], fn[fi]) >= cos_lim:
                    acc = vadd(acc, fn[oj])
            row.append(vnorm(acc) if vlen(acc) > 1e-9 else fn[fi])
        out.append(row)
    return out


def box_uvs(verts: Sequence[Vec3], face: Sequence[int], normal: Vec3,
            scale: float = 0.5) -> list[Vec2]:
    """World-space box projection - a usable starting point for texturing."""
    ax, ay, az = abs(normal[0]), abs(normal[1]), abs(normal[2])
    out: list[Vec2] = []
    for vi in face:
        x, y, z = verts[vi]
        if az >= ax and az >= ay:
            u, v = x, y
        elif ax >= ay:
            u, v = y, z
        else:
            u, v = x, z
        out.append((u * scale, v * scale))
    return out
