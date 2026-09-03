"""Car body: lofted shell, cab front, glazing, doors, vents and lights.

The shell is a single closed loft through parametric cross sections. Every
section has the same vertex layout, so any longitudinal band of the surface
can be re-generated with a small outward offset to place a window, a paint
band or a vent onto the shell without boolean operations.

The section follows the real Vectron: vertical side wall, a straight inward
chamfer at the shoulder, then a narrow, almost flat roof.
"""

from __future__ import annotations

import math
from dataclasses import dataclass, replace

from . import dims as D
from .mesh import Mesh, Vec3, box, catmull_rom, cylinder, loft, quad_strip, tube_along

# Index layout of a half section (0 = keel centre ... HALF_N-1 = roof centre).
I_KEEL = 0
I_SIDE_BOT = 4          # bottom of the vertical side wall
I_SIDE_TOP = 9          # shoulder break
I_CHAMFER = 10          # first point on the shoulder chamfer
I_ROOF_EDGE = 16        # outer edge of the flat roof
ROOF_ARC = 4            # points from the roof edge to the crown
HALF_N = I_ROOF_EDGE + ROOF_ARC + 1      # 21
RING_N = HALF_N * 2 - 2                  # 40
SIDE_STEPS = (0.30, 0.55, 0.78, 0.94)    # points up the vertical side wall
CHAMFER_STEPS = (0.06, 0.22, 0.45, 0.68, 0.86, 0.95)
NOSE_FILLET = 0.155     # radius rolling the front face into the body sides


def mirror_index(i: int) -> int:
    """Ring index of the -Y twin of half-section index i (0 < i < HALF_N-1)."""
    return RING_N - i


@dataclass(frozen=True)
class Station:
    """One cross section of the body shell."""
    x: float
    hw: float            # half width of the side wall
    z_floor: float       # underside of the frame
    z_side_bot: float    # where the skirt reaches full width
    z_side_top: float    # top of the vertical side wall (shoulder break)
    roof_hw: float       # half width of the flat roof
    z_roof_edge: float   # height of the roof edge
    z_roof: float        # roof crown height
    lean: float = 0.0    # how far the bottom edge is pulled back (front rake)


# Key sections for the front half. The machine room has the narrow chamfered
# roof; over the cab the roof widens and lifts slightly, then drops steeply
# into the raked windscreen.
KEY_STATIONS: tuple[Station, ...] = (
    Station(0.000, 1.506, 1.300, 1.520, 3.240, 1.190, 3.980, 4.220, 0.000),
    Station(4.400, 1.506, 1.300, 1.520, 3.240, 1.190, 3.980, 4.220, 0.000),
    Station(6.150, 1.506, 1.300, 1.520, 3.250, 1.210, 3.985, 4.220, 0.000),
    Station(6.750, 1.506, 1.300, 1.520, 3.330, 1.280, 4.020, 4.215, 0.002),
    Station(7.400, 1.506, 1.300, 1.520, 3.420, 1.330, 4.060, 4.200, 0.006),
    Station(8.060, 1.501, 1.302, 1.522, 3.450, 1.340, 4.055, 4.180, 0.014),
    Station(8.230, 1.496, 1.303, 1.523, 3.410, 1.335, 4.010, 4.120, 0.024),
    # From the brow the windscreen falls on a straight line to the front face.
    Station(8.330, 1.489, 1.304, 1.524, 3.330, 1.352, 3.840, 3.910, 0.034),
    Station(8.500, 1.474, 1.306, 1.526, 3.160, 1.360, 3.530, 3.580, 0.048),
    Station(8.660, 1.452, 1.309, 1.529, 3.000, 1.352, 3.240, 3.280, 0.062),
    Station(8.760, 1.442, 1.313, 1.533, 2.900, 1.372, 3.070, 3.100, 0.082),
    Station(8.820, 1.430, 1.324, 1.544, 2.840, 1.366, 2.960, 2.980, 0.102),
)

SUBDIV = 5          # interpolation steps per key-station span


# --------------------------------------------------------------------------
# section geometry
# --------------------------------------------------------------------------

def half_profile(st: Station) -> list[tuple[float, float]]:
    """(y, z) points from the keel centre up to the roof crown."""
    hw = st.hw
    side_h = st.z_side_top - st.z_side_bot
    pts: list[tuple[float, float]] = [
        (0.0, st.z_floor),                       # 0 keel centre
        (hw * 0.55, st.z_floor),                 # 1
        (hw - 0.175, st.z_floor),                # 2 bottom corner
        (hw - 0.020, st.z_side_bot),             # 3 skirt
        (hw, st.z_side_bot + 0.105),             # 4 full width
    ]
    for k in SIDE_STEPS:                         # 5..8 up the vertical wall
        pts.append((hw, st.z_side_bot + 0.105 + (side_h - 0.105) * k))
    pts.append((hw, st.z_side_top))              # 9 shoulder break

    # Straight chamfer from the shoulder to the roof edge, with a small
    # fillet at each end so the highlight breaks the way sheet metal does.
    ey, ez = st.roof_hw, st.z_roof_edge
    dy, dz = ey - hw, ez - st.z_side_top
    for t in CHAMFER_STEPS:                      # 10..15
        pts.append((hw + dy * t, st.z_side_top + dz * t))
    pts.append((ey, ez))                         # 16 roof edge

    # Shallow, almost flat roof crown.
    crown = st.z_roof - ez
    n = 3.4
    for k in range(1, ROOF_ARC + 1):             # 17..20
        t = (math.pi / 2.0) * k / ROOF_ARC
        y = ey * (math.cos(t) ** (2.0 / n))
        z = ez + crown * (math.sin(t) ** (2.0 / n))
        pts.append((y if k < ROOF_ARC else 0.0, z))
    return pts


def ring2d(st: Station) -> list[tuple[float, float]]:
    """Closed (y, z) ring: +Y side bottom-to-top, then the -Y side back down."""
    half = half_profile(st)
    ring = list(half)
    for i in range(HALF_N - 2, 0, -1):
        y, z = half[i]
        ring.append((-y, z))
    return ring


def ring_normals(ring: list[tuple[float, float]]) -> list[tuple[float, float]]:
    """Outward 2D normals of a closed ring, used to offset surface patches."""
    n = len(ring)
    cy = sum(p[0] for p in ring) / n
    cz = sum(p[1] for p in ring) / n
    out: list[tuple[float, float]] = []
    for i in range(n):
        py, pz = ring[(i - 1) % n]
        ny, nz = ring[(i + 1) % n]
        ty, tz = ny - py, nz - pz
        nrm = math.hypot(ty, tz) or 1.0
        cand = (tz / nrm, -ty / nrm)
        if (ring[i][0] - cy) * cand[0] + (ring[i][1] - cz) * cand[1] < 0.0:
            cand = (-cand[0], -cand[1])
        out.append(cand)
    return out


def ring3d(st: Station, offset: float = 0.0) -> list[Vec3]:
    """World-space ring, optionally pushed out along the section normal."""
    ring = ring2d(st)
    nrm = ring_normals(ring) if offset else None
    sgn = 1.0 if st.x >= 0.0 else -1.0
    pts: list[Vec3] = []
    for i, (y, z) in enumerate(ring):
        if nrm:
            y += nrm[i][0] * offset
            z += nrm[i][1] * offset
        x = st.x - sgn * st.lean * max(0.0, st.z_roof - z)
        pts.append((x, y, z))
    return pts


def _interp(a: Station, b: Station, c: Station, d: Station, t: float) -> Station:
    f = lambda p0, p1, p2, p3: catmull_rom(p0, p1, p2, p3, t)
    return Station(
        f(a.x, b.x, c.x, d.x),
        f(a.hw, b.hw, c.hw, d.hw),
        f(a.z_floor, b.z_floor, c.z_floor, d.z_floor),
        f(a.z_side_bot, b.z_side_bot, c.z_side_bot, d.z_side_bot),
        f(a.z_side_top, b.z_side_top, c.z_side_top, d.z_side_top),
        f(a.roof_hw, b.roof_hw, c.roof_hw, d.roof_hw),
        f(a.z_roof_edge, b.z_roof_edge, c.z_roof_edge, d.z_roof_edge),
        f(a.z_roof, b.z_roof, c.z_roof, d.z_roof),
        f(a.lean, b.lean, c.lean, d.lean),
    )


def sample_stations(subdiv: int = SUBDIV) -> list[Station]:
    """Smoothly interpolated stations for the whole loco, rear to front."""
    half = list(KEY_STATIONS)
    full = [replace(s, x=-s.x) for s in reversed(half[1:])] + half
    out: list[Station] = []
    for i in range(len(full) - 1):
        p0 = full[max(0, i - 1)]
        p1, p2 = full[i], full[i + 1]
        p3 = full[min(len(full) - 1, i + 2)]
        for s in range(subdiv):
            out.append(_interp(p0, p1, p2, p3, s / subdiv))
    out.append(full[-1])
    for i in range(1, len(out)):
        if out[i].x <= out[i - 1].x:
            out[i] = replace(out[i], x=out[i - 1].x + 1e-4)
    return out


def hw_at(stations: list[Station], x: float) -> float:
    """Half width of the side wall at a longitudinal position."""
    if x <= stations[0].x:
        return stations[0].hw
    for a, b in zip(stations, stations[1:]):
        if a.x <= x <= b.x:
            t = (x - a.x) / max(1e-6, b.x - a.x)
            return a.hw + (b.hw - a.hw) * t
    return stations[-1].hw


def front_x(z: float, st: Station | None = None) -> float:
    """X of the flat cab front face at height z.

    The shell ends with a fillet, so the flat part of the face sits one
    fillet radius ahead of the last station.
    """
    st = st or KEY_STATIONS[-1]
    return st.x + NOSE_FILLET - st.lean * max(0.0, st.z_roof - z)


# --------------------------------------------------------------------------
# shell + surface patches
# --------------------------------------------------------------------------

def _nose_fillet(st: Station, sgn: float, radius: float,
                 steps: int = 3) -> list[list[Vec3]]:
    """Rings that roll the flat front face into the body sides with a radius.

    Without this the cab front meets the side walls in a hard 90 degree edge,
    which is the single thing that makes a boxy loco read as 'modelled'
    rather than 'built'.
    """
    rings: list[list[Vec3]] = []
    for k in range(1, steps + 1):
        t = (math.pi / 2.0) * k / steps
        inset = -radius * (1.0 - math.cos(t))
        dx = sgn * radius * math.sin(t)
        rings.append([(p[0] + dx, p[1], p[2]) for p in ring3d(st, inset)])
    return rings


def body_shell(stations: list[Station]) -> Mesh:
    rings = [ring3d(s) for s in stations]
    rings = (list(reversed(_nose_fillet(stations[0], -1.0, NOSE_FILLET)))
             + rings + _nose_fillet(stations[-1], 1.0, NOSE_FILLET))
    return loft(rings, "vec_body", "Vectron_Shell", closed_ring=True,
                cap_start=True, cap_end=True, smooth=True)


def surface_patch(stations: list[Station], x0: float, x1: float,
                  i0: int, i1: int, offset: float, mat: str,
                  name: str = "patch", smooth: bool = True) -> Mesh:
    """Re-skin a band of the shell (station range x ring index range)."""
    sel = [s for s in stations if x0 - 1e-6 <= s.x <= x1 + 1e-6]
    if len(sel) < 2:
        return Mesh(name)
    m = Mesh(name)
    rows = [ring3d(s, offset)[i0:i1 + 1] for s in sel]
    idx = [m.add_ring(r) for r in rows]
    for a, b in zip(idx, idx[1:]):
        for k in range(len(a) - 1):
            m.add_face((a[k], a[k + 1], b[k + 1], b[k]), mat, smooth)
    return m


def side_rect(stations: list[Station], x0: float, x1: float, z0: float, z1: float,
              side: int, out: float, mat: str, name: str = "sidepanel",
              nx: int = 3) -> Mesh:
    """Flat panel following the side wall at y = side * (halfwidth + out)."""
    m = Mesh(name)
    xs = [x0 + (x1 - x0) * i / nx for i in range(nx + 1)]
    lo = m.add_ring([(x, side * (hw_at(stations, x) + out), z0) for x in xs])
    hi = m.add_ring([(x, side * (hw_at(stations, x) + out), z1) for x in xs])
    for k in range(nx):
        f = (lo[k], lo[k + 1], hi[k + 1], hi[k])
        m.add_face(f if side > 0 else tuple(reversed(f)), mat, False)
    return m


def framed_side_panel(stations: list[Station], x0: float, x1: float,
                      z0: float, z1: float, side: int, mat_face: str,
                      mat_frame: str = "vec_body", face_out: float = 0.006,
                      frame_out: float = 0.040, frame_w: float = 0.055,
                      name: str = "framed") -> Mesh:
    """A panel with a raised bezel around it, so the face reads as recessed."""
    m = Mesh(name)
    m.extend(side_rect(stations, x0, x1, z0, z1, side, face_out, mat_face, name + "_face"))
    ox0, ox1 = x0 - frame_w, x1 + frame_w
    oz0, oz1 = z0 - frame_w, z1 + frame_w

    def y(x: float, o: float) -> float:
        return side * (hw_at(stations, x) + o)

    def band(ax0, az0, ax1, az1, o_in, o_out):
        """Quad from (ax0,az0)-(ax1,az1) at offset o_in to the same at o_out."""
        p = [(ax0, y(ax0, o_in), az0), (ax1, y(ax1, o_in), az1),
             (ax1, y(ax1, o_out), az1), (ax0, y(ax0, o_out), az0)]
        i = m.add_ring(p)
        f = (i[0], i[1], i[2], i[3])
        m.add_face(f if side > 0 else tuple(reversed(f)), mat_frame, False)

    # inner wall (face -> bezel top), bezel top ring, outer wall (bezel -> shell)
    for (ax0, az0, ax1, az1) in ((x0, z0, x1, z0), (x1, z1, x0, z1),
                                 (x0, z1, x0, z0), (x1, z0, x1, z1)):
        band(ax0, az0, ax1, az1, face_out, frame_out)
    for (ax0, az0, ax1, az1) in ((ox0, oz0, ox1, oz0), (ox1, oz1, ox0, oz1),
                                 (ox0, oz1, ox0, oz0), (ox1, oz0, ox1, oz1)):
        band(ax0, az0, ax1, az1, frame_out, 0.0)
    for (ax0, az0, ax1, az1, bx0, bz0, bx1, bz1) in (
            (x0, z0, x1, z0, ox0, oz0, ox1, oz0),
            (x1, z1, x0, z1, ox1, oz1, ox0, oz1),
            (x0, z1, x0, z0, ox0, oz1, ox0, oz0),
            (x1, z0, x1, z1, ox1, oz0, ox1, oz1)):
        p = [(ax0, y(ax0, frame_out), az0), (ax1, y(ax1, frame_out), az1),
             (bx1, y(bx1, frame_out), bz1), (bx0, y(bx0, frame_out), bz0)]
        i = m.add_ring(p)
        f = (i[0], i[1], i[2], i[3])
        m.add_face(f if side > 0 else tuple(reversed(f)), mat_frame, False)
    return m


def louvres(stations: list[Station], x0: float, x1: float, z0: float, z1: float,
            side: int, count: int, name: str = "louvres") -> Mesh:
    """Horizontal slats inside a vent bay - cheap but reads well up close."""
    m = Mesh(name)
    for k in range(count):
        z = z0 + (z1 - z0) * (k + 0.5) / count
        h = (z1 - z0) / count * 0.45
        m.extend(side_rect(stations, x0 + 0.02, x1 - 0.02, z - h / 2, z + h / 2,
                           side, 0.022, "vec_grille", f"{name}_{k}", nx=2))
    return m


# --------------------------------------------------------------------------
# assembled body parts
# --------------------------------------------------------------------------


def side_quad(stations: list[Station], corners, side: int, out: float,
              mat: str, name: str = "quad") -> Mesh:
    """Free-form flat panel on the side wall from four (x, z) corners."""
    m = Mesh(name)
    idx = m.add_ring([(x, side * (hw_at(stations, x) + out), z) for x, z in corners])
    f = tuple(idx)
    m.add_face(f if side > 0 else tuple(reversed(f)), mat, False)
    return m


def front_plate(sgn: int, y0: float, y1: float, z0: float, z1: float,
                corner: float, depth: float, mat: str, name: str = "plate") -> Mesh:
    """Rounded rectangle extruded off the cab front face."""
    pts: list[tuple[float, float]] = []
    for (cy, cz, a0) in ((y1 - corner, z1 - corner, 0.0), (y0 + corner, z1 - corner, 90.0),
                         (y0 + corner, z0 + corner, 180.0), (y1 - corner, z0 + corner, 270.0)):
        for k in range(4):
            a = math.radians(a0 + 30.0 * k)
            pts.append((cy + corner * math.cos(a), cz + corner * math.sin(a)))
    rings = []
    for d in (0.0, depth):
        rings.append([(sgn * (front_x(z) + d), y, z) for y, z in pts])
    m = loft(rings, mat, name, closed_ring=True, cap_start=True, cap_end=True,
             smooth=False)
    return m


def build_body(stations: list[Station], lod: int = 0) -> dict[str, Mesh]:
    """Named meshes making up the car body (shell plus all cladding)."""
    parts: dict[str, Mesh] = {}
    body = body_shell(stations)
    glass = Mesh("Vectron_Glass")
    rails = Mesh("Vectron_Handrails")

    roof_i0, roof_i1 = I_ROOF_EDGE, mirror_index(I_ROOF_EDGE)
    cham_i0, cham_i1 = I_CHAMFER + 1, mirror_index(I_CHAMFER + 1)
    screen_i0, screen_i1 = I_CHAMFER + 1, mirror_index(I_CHAMFER + 1)
    ROOF_END = 6.90

    # Grey machine room roof; the cab roofs stay in body colour.
    body.extend(surface_patch(stations, -ROOF_END, ROOF_END, roof_i0, roof_i1,
                              0.004, "vec_roof", "roof_paint"))
    # Grey shoulder chamfer with the vent louvre groups sitting on it.
    for i0, i1 in ((I_CHAMFER, I_ROOF_EDGE), (mirror_index(I_ROOF_EDGE),
                                              mirror_index(I_CHAMFER))):
        body.extend(surface_patch(stations, -7.30, 7.30, i0, i1,
                                  0.004, "vec_roof", "chamfer_paint"))
    for k in range(8):
        a = -6.40 + k * 1.63
        for i0, i1 in ((I_CHAMFER + 1, I_ROOF_EDGE - 1),
                       (mirror_index(I_ROOF_EDGE - 1), mirror_index(I_CHAMFER + 1))):
            body.extend(surface_patch(stations, a, a + 1.10, i0, i1, 0.010,
                                      "vec_grille", "shoulder_vent"))
            if lod == 0:
                for s in range(3):
                    x0 = a + 0.10 + s * 0.33
                    body.extend(surface_patch(stations, x0, x0 + 0.20, i0, i1,
                                              0.026, "vec_roof", "louvre"))

    # Dark grey band along the bottom of the body sides.
    for i0, i1 in ((1, I_SIDE_BOT), (mirror_index(I_SIDE_BOT), RING_N - 1)):
        body.extend(surface_patch(stations, -8.30, 8.30, i0, i1, 0.004,
                                  "vec_mask", "lower_band"))

    for sgn in (1, -1):
        # Black surround around the windscreen.
        body.extend(surface_patch(stations, min(sgn * 8.24, sgn * 8.975),
                                  max(sgn * 8.24, sgn * 8.975),
                                  screen_i0, screen_i1, 0.006, "vec_mask", "mask"))
        for side in (1, -1):
            wx0, wx1 = D.CAB_WINDOW_X
            z0, z1 = D.CAB_WINDOW_Z
            a0, a1 = sorted((sgn * wx0, sgn * wx1))
            # Cab side window: front edge follows the A pillar, so it slants.
            slant = 0.16
            if sgn > 0:
                corners = ((a0, z0), (a1 - slant, z0), (a1, z1), (a0, z1))
            else:
                corners = ((a0, z1), (a1, z1), (a1, z0), (a0 + slant, z0))
            body.extend(_slanted_window_frame(stations, corners, side))
            inner = _shrink(corners, 0.055)
            glass.extend(side_quad(stations, inner, side, 0.016, "vec_glass",
                                   "cab_glass"))

            # Cab door with a window in the upper half.
            dx0, dx1 = sorted((sgn * D.DOOR_X[0], sgn * D.DOOR_X[1]))
            dz0, dz1 = D.DOOR_Z
            body.extend(framed_side_panel(stations, dx0, dx1, dz0, dz1, side,
                                          "vec_body", "vec_body", face_out=0.005,
                                          frame_out=0.028, frame_w=0.026,
                                          name="door"))
            body.extend(framed_side_panel(stations, dx0 + 0.11, dx1 - 0.11,
                                          dz1 - 0.66, dz1 - 0.13, side, "vec_mask",
                                          "vec_body", face_out=0.011,
                                          frame_out=0.026, frame_w=0.020,
                                          name="door_win_frame"))
            glass.extend(side_rect(stations, dx0 + 0.145, dx1 - 0.145,
                                   dz1 - 0.625, dz1 - 0.165, side, 0.019,
                                   "vec_glass", "door_glass", nx=1))
            hy = side * (hw_at(stations, (dx0 + dx1) / 2) + 0.055)
            hx = dx1 - 0.16 if sgn > 0 else dx0 + 0.16
            body.extend(box(hx - 0.04, hx + 0.04, min(hy, hy - side * 0.05),
                            max(hy, hy - side * 0.05), 2.16, 2.30,
                            "vec_steel", "handle"))
            for rx in (dx0 - 0.085, dx1 + 0.085):
                y = side * (hw_at(stations, rx) + 0.060)
                rails.extend(tube_along([(rx, y, dz0 + 0.06), (rx, y, dz1 - 0.10)],
                                        0.020, 6 if lod else 8, "vec_handrail", "grab"))

            # Small equipment hatches on the otherwise plain side wall.
            for hx0, hz0, w, h in ((sgn * 5.55, 2.62, 0.52, 0.62),
                                   (sgn * 2.90, 1.62, 0.62, 0.30),
                                   (sgn * 0.30, 1.62, 0.62, 0.30)):
                bx0, bx1 = sorted((hx0, hx0 + sgn * w))
                body.extend(framed_side_panel(stations, bx0, bx1, hz0, hz0 + h,
                                              side, "vec_body", "vec_body",
                                              face_out=0.004, frame_out=0.020,
                                              frame_w=0.018, name="hatch"))
            # Sand filler hatch and a vent right behind the cab.
            vx0, vx1 = sorted((sgn * 6.05, sgn * 6.02 - sgn * 0.42))
            body.extend(framed_side_panel(stations, vx0, vx1, 2.62, 3.16, side,
                                          "vec_grille", "vec_body", name="side_vent"))

    parts["body"] = body
    parts["glass"] = glass
    parts["rails"] = rails
    parts["front"] = build_cab_fronts(stations, lod, glass)
    return parts


def _shrink(corners, d: float):
    """Inset a quad given as ((x, z), ...) by d, for glass inside a frame."""
    cx = sum(c[0] for c in corners) / 4.0
    cz = sum(c[1] for c in corners) / 4.0
    out = []
    for x, z in corners:
        dx, dz = cx - x, cz - z
        n = math.hypot(dx, dz) or 1.0
        out.append((x + dx / n * d * 1.6, z + dz / n * d))
    return tuple(out)


def _slanted_window_frame(stations, corners, side: int) -> Mesh:
    """Black rubber surround for a non rectangular window."""
    m = Mesh("win_frame")
    outer = _shrink(corners, -0.045)
    inner = _shrink(corners, 0.035)
    m.extend(side_quad(stations, corners, side, 0.008, "vec_mask", "win_mask"))
    for k in range(4):
        a, b = outer[k], outer[(k + 1) % 4]
        c, d = inner[(k + 1) % 4], inner[k]
        m.extend(side_quad(stations, (a, b, c, d), side, 0.020, "vec_mask",
                           "win_bezel"))
    return m


def build_cab_fronts(stations: list[Station], lod: int = 0,
                     glass: Mesh | None = None) -> Mesh:
    """Windscreen, light towers, the V shaped louvre bars and the wipers."""
    m = Mesh("Vectron_Front")
    seg = 12 if lod == 0 else 8
    glass = glass if glass is not None else m

    for sgn in (1, -1):
        # Windscreen glazing over the raked surface band.
        a0, a1 = sorted((sgn * 8.26, sgn * 8.80))
        glass.extend(surface_patch(stations, a0, a1, I_CHAMFER + 2,
                                   mirror_index(I_CHAMFER + 2), 0.016,
                                   "vec_glass", "windscreen"))

        # Drip rail over the windscreen, the small lip the real cab has.
        m.extend(surface_patch(stations, min(sgn * 8.18, sgn * 8.30),
                               max(sgn * 8.18, sgn * 8.30), I_CHAMFER,
                               mirror_index(I_CHAMFER), 0.030, "vec_mask", "brow"))

        # Black face plate carrying lights and louvres.
        m.extend(front_plate(sgn, -1.262, 1.262, 1.480, 2.822, 0.175, 0.016,
                             "vec_mask", "front_mask"))

        for side in (1, -1):
            # Vertical light tower at each outer corner.
            ty0, ty1 = side * 0.930, side * 1.238
            m.extend(front_plate(sgn, min(ty0, ty1), max(ty0, ty1), 1.600, 2.740,
                                 0.125, 0.060, "vec_mask", "light_tower"))
            cy = side * 1.084
            for (lz, lr, mat) in ((2.500, 0.126, "vec_light"),
                                  (2.090, 0.126, "vec_light"),
                                  (1.760, 0.056, "vec_light_red")):
                x = front_x(lz) * sgn
                rim = cylinder(lr + 0.022, 0.045, seg, "vec_frame", "lamp_rim",
                               axis="x")
                lens = cylinder(lr, 0.020, seg, mat, "lamp_lens", axis="x")
                if sgn > 0:
                    rim.translate(x + 0.052, cy, lz)
                    lens.translate(x + 0.082, cy, lz)
                else:
                    rim.scale(-1, 1, 1).translate(x - 0.052, cy, lz)
                    lens.scale(-1, 1, 1).translate(x - 0.082, cy, lz)
                m.extend(rim)
                m.extend(lens)

        # Four horizontal bars in a shallow V between the light towers.
        for k, zc in enumerate((1.760, 1.975, 2.190, 2.405)):
            mat = "vec_accent" if k in (1, 2) else "vec_frame"
            for side in (1, -1):
                x = sgn * (front_x(zc) + 0.010)
                bar = box(min(x, x + sgn * 0.032), max(x, x + sgn * 0.032),
                          0.048, 0.845, -0.052, 0.052, mat, "vbar")
                bar.rotate_x(math.radians(-3.6))
                if side < 0:
                    bar.scale(1, -1, 1)
                m.extend(bar.translate(0.0, 0.0, zc))

        # Dark valance between the face plate and the head stock.
        m.extend(front_plate(sgn, -1.20, 1.20, 1.330, 1.505, 0.09, 0.010,
                             "vec_mask", "front_valance"))
        # UIC sockets and jumper cable dummies below the face.
        for side in (1, -1):
            sx = sgn * (front_x(1.420) + 0.010)
            m.extend(box(min(sx, sx + sgn * 0.075), max(sx, sx + sgn * 0.075),
                         side * 0.46 - 0.075, side * 0.46 + 0.075, 1.345, 1.495,
                         "vec_frame", "uic_socket"))
        # Vertical grab rails at both front corners.
        for side in (1, -1):
            gy = side * 1.262
            gx = sgn * (front_x(2.20) + 0.075)
            m.extend(tube_along([(gx, gy, 1.62), (gx, gy, 2.68)], 0.019, 6,
                                "vec_handrail", "front_grab"))
            for gz in (1.66, 2.64):
                m.extend(tube_along([(sgn * front_x(gz), gy, gz), (gx, gy, gz)],
                                    0.016, 5, "vec_handrail", "grab_foot"))
        # Warning horn under the nose.
        m.extend(box(min(sgn * (front_x(1.34) - 0.10), sgn * front_x(1.34)),
                     max(sgn * (front_x(1.34) - 0.10), sgn * front_x(1.34)),
                     -0.24, 0.24, 1.150, 1.300, "vec_frame", "horn_box"))

        # Wipers parked along the bottom edge of the windscreen.
        if lod <= 1:
            for side in (1, -1):
                bx = sgn * (front_x(2.880) + 0.030)
                base = (bx, side * 0.46, 2.880)
                mid = (sgn * (front_x(3.05) + 0.045), side * 0.58, 3.090)
                tip = (sgn * (front_x(3.34) + 0.050), side * 0.70, 3.390)
                m.extend(tube_along([base, mid, tip], 0.019, 5, "vec_frame", "wiper"))
                m.extend(tube_along([mid, tip], 0.028, 4, "vec_rubber", "blade"))
                m.extend(cylinder(0.045, 0.06, 8, "vec_frame", "wiper_hub", axis="x")
                         .translate(bx - (0.06 if sgn < 0 else 0.0), side * 0.46, 2.780))
    return m
