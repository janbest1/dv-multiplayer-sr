"""Under-frame equipment, head stocks, buffers, couplers and ploughs."""

from __future__ import annotations

from . import dims as D
from .body import KEY_STATIONS, front_x
from .mesh import (Mesh, box, chamfered_box, cylinder, revolve, tube_along)


def build_underframe(lod: int = 0) -> Mesh:
    m = Mesh("Vectron_Underframe")
    seg = {0: 16, 1: 12}.get(lod, 8)

    # Longitudinal frame edge beams below the body skirt.
    for side in (1, -1):
        y = side * 1.455
        m.extend(box(-8.60, 8.60, y - 0.055, y + 0.055, 1.150, 1.310,
                     "vec_frame", "solebar"))

    # Apparatus / battery boxes between the bogies.
    for cx, sx in ((-2.55, 2.30), (0.00, 1.90), (2.55, 2.30)):
        for side in (1, -1):
            m.extend(chamfered_box(cx, side * 1.12, 0.930, sx, 0.60, 0.62, 0.05,
                                   "vec_frame", "apparatus_box"))
    # Main air reservoirs.
    for side in (1, -1):
        m.extend(cylinder(0.215, 2.30, seg, "vec_steel", "air_tank", axis="x")
                 .translate(-1.15, side * 0.42, 0.90))
    # Transformer block in the middle (AC machines carry it below the floor).
    m.extend(chamfered_box(0.0, 0.0, 0.99, 3.60, 1.10, 0.42, 0.06,
                           "vec_frame", "transformer"))

    for sgn in (1, -1):
        m.extend(_end_gear(sgn, lod, seg))
    return m


def _end_gear(sgn: int, lod: int, seg: int) -> Mesh:
    """Head stock, buffers, screw coupler, plough and hoses at one end."""
    m = Mesh("end")
    st = KEY_STATIONS[-1]
    x_face = front_x(1.30, st)          # front face at buffer beam height
    hs = sgn * x_face

    # Head stock (buffer beam).
    m.extend(box(min(hs, hs - sgn * 0.16), max(hs, hs - sgn * 0.16),
                 -1.34, 1.34, 0.845, 1.290, "vec_frame", "headstock"))

    # Buffers: rectangular flat pads, as fitted to most Vectrons.
    for side in (1, -1):
        y = side * D.BUFFER_HALF
        base_x = hs
        face_x = sgn * D.BUFFER_FACE_X
        stem_len = abs(face_x - base_x)
        m.extend(box(min(base_x, face_x - sgn * 0.075), max(base_x, face_x - sgn * 0.075),
                     y - 0.115, y + 0.115, D.COUPLER_HEIGHT - 0.115,
                     D.COUPLER_HEIGHT + 0.115, "vec_frame", "buffer_housing"))
        m.extend(box(min(face_x, face_x - sgn * 0.075), max(face_x, face_x - sgn * 0.075),
                     y - 0.235, y + 0.235, D.COUPLER_HEIGHT - 0.180,
                     D.COUPLER_HEIGHT + 0.180, "vec_buffer", "buffer_pad"))
        _ = stem_len

    # Screw coupler: hook, shackle and a couple of links.
    hook_x = hs + sgn * 0.10
    m.extend(chamfered_box(hook_x, 0.0, D.COUPLER_HEIGHT, 0.34, 0.16, 0.24, 0.04,
                           "vec_buffer", "coupler_hook"))
    if lod <= 1:
        for i, t in enumerate((0.30, 0.62)):
            lx = hs + sgn * (0.26 + 0.20 * i)
            link = revolve([(0.055, 0.0), (0.075, 0.03), (0.055, 0.06)], 8,
                           "vec_steel", "link", axis="x")
            m.extend(link.translate(lx, 0.0, D.COUPLER_HEIGHT - 0.10 * t))
    # Air hoses either side of the coupler.
    if lod == 0:
        for side, dy in ((1, 0.46), (-1, 0.62)):
            y = side * dy
            m.extend(tube_along([(hs, y, 1.02), (hs + sgn * 0.16, y, 0.90),
                                 (hs + sgn * 0.10, y, 0.70)],
                                0.036, 6, "vec_rubber", "hose"))

    # Obstacle deflector: a wide, angular plate assembly under the buffers.
    px = hs - sgn * 0.06
    fx = hs + sgn * 0.30
    plough = Mesh("plough")
    top = [(px, -1.16, 0.820), (px, 1.16, 0.820)]
    mid = [(fx, -1.02, 0.440), (fx, 1.02, 0.440)]
    bot = [(fx - sgn * 0.09, -0.90, 0.115), (fx - sgn * 0.09, 0.90, 0.115)]
    for a, b in ((top, mid), (mid, bot)):
        i = plough.add_ring([a[0], a[1], b[1], b[0]])
        plough.add_face(i, "vec_plough", False)
        plough.add_face(tuple(reversed(i)), "vec_plough", False)
    for side in (1, -1):
        i = plough.add_ring([(px, side * 1.16, 0.820), (fx, side * 1.02, 0.440),
                             (fx - sgn * 0.09, side * 0.90, 0.115),
                             (px, side * 1.16, 0.115)])
        plough.add_face(i, "vec_plough", False)
        plough.add_face(tuple(reversed(i)), "vec_plough", False)
    m.extend(plough)
    # Vertical stiffener ribs on the plough face.
    for dy in (-0.62, 0.0, 0.62):
        m.extend(box(min(fx, fx + sgn * 0.05), max(fx, fx + sgn * 0.05),
                     dy - 0.045, dy + 0.045, 0.150, 0.740, "vec_plough", "plough_rib"))

    # Steps under the cab doors.
    for side in (1, -1):
        cx = sgn * (D.DOOR_X[0] + D.DOOR_X[1]) / 2.0
        m.extend(box(cx - 0.34, cx + 0.34, side * 1.30, side * 1.52,
                     0.980, 1.050, "vec_frame", "step"))
        m.extend(box(cx - 0.34, cx + 0.34, side * 1.34, side * 1.50,
                     1.290, 1.350, "vec_frame", "step2"))
    return m
