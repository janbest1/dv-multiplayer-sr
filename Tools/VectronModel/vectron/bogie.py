"""Bo'Bo' bogie: frame, wheelsets, suspension, drive and brakes.

The parts are returned separately so the Blender pipeline can build the
hierarchy Derail Valley expects (BogieF > bogie_car > [axle] > wheels) with
the pivots in the right places.
"""

from __future__ import annotations

import math

from . import dims as D
from .mesh import (Mesh, box, box_c, chamfered_box, coil_spring, cylinder,
                   revolve, tube_along)

WHEEL_Y = 0.755          # centre plane of a wheel
FRAME_Y = 0.985          # centre of a bogie side beam
FRAME_TOP = 0.980
FRAME_BOT = 0.700


def wheel(lod: int = 0) -> Mesh:
    """Wheel with tread, flange and web, built as a revolved profile.
    Returned lying in the XZ plane with the axle along Y."""
    r = D.WHEEL_RADIUS
    prof = [
        (0.000, 0.000), (0.170, 0.000), (0.190, 0.022), (0.300, 0.030),
        (0.560, 0.020), (0.610, 0.006), (r + 0.040, 0.030),   # flange
        (r + 0.003, 0.058), (r - 0.004, 0.148), (r - 0.055, 0.168),
        (0.520, 0.150), (0.300, 0.140), (0.190, 0.132), (0.170, 0.155),
        (0.000, 0.155),
    ]
    seg = {0: 28, 1: 20, 2: 12, 3: 8}.get(lod, 8)
    m = revolve(prof, seg, "vec_wheel", "wheel", axis="y")
    # Only the running surface and the flange stay bright steel.
    for i, f in enumerate(m.faces):
        rad = max(math.hypot(m.verts[v][0], m.verts[v][2]) for v in f)
        if rad > 0.585:
            m.mats[i] = "vec_tyre"
    return m


def wheelset(lod: int = 0) -> Mesh:
    """One axle: two wheels, the axle shaft, journal boxes and brake discs.
    Origin is the axle centre so it can spin around Y in the engine."""
    m = Mesh("axle")
    seg = {0: 20, 1: 14, 2: 10}.get(lod, 8)
    for side in (1, -1):
        w = wheel(lod)
        if side > 0:
            w.translate(0.0, WHEEL_Y - 0.077, 0.0)
        else:
            w.scale(1, -1, 1).translate(0.0, -(WHEEL_Y - 0.077), 0.0)
        m.extend(w)
        # brake disc pair inboard of each wheel
        if lod <= 1:
            disc = revolve([(0.0, 0.0), (0.42, 0.0), (0.42, 0.048), (0.0, 0.048)],
                           seg, "vec_brake_disc", "disc", axis="y")
            m.extend(disc.translate(0.0, side * 0.30, 0.0))
        # journal box on the axle end
        m.extend(chamfered_box(0.0, side * 0.94, 0.0, 0.30, 0.24, 0.30, 0.05,
                               "vec_bogie", "journal"))
    shaft = cylinder(0.092, 2 * 0.94, seg, "vec_steel", "shaft", axis="y")
    m.extend(shaft.translate(0.0, -0.94, 0.0))
    return m


def bogie_frame(lod: int = 0) -> Mesh:
    """Welded H frame plus drives, springs and brake gear (no wheelsets)."""
    m = Mesh("bogie_frame")
    seg = {0: 14, 1: 10}.get(lod, 8)
    ax0, ax1 = -D.BOGIE_WHEELBASE / 2.0, D.BOGIE_WHEELBASE / 2.0

    for side in (1, -1):
        y = side * FRAME_Y
        # Side beam, cranked down between the axles like a real welded frame.
        m.extend(box(ax0 - 0.72, ax1 + 0.72, y - 0.075, y + 0.075,
                     FRAME_BOT + 0.10, FRAME_TOP, "vec_bogie", "sidebeam"))
        m.extend(box(ax0 + 0.30, ax1 - 0.30, y - 0.090, y + 0.090,
                     FRAME_BOT - 0.05, FRAME_BOT + 0.14, "vec_bogie", "sidebeam_lo"))
        for ax in (ax0, ax1):
            # primary suspension over each axle box
            if lod == 0:
                m.extend(coil_spring(0.115, 0.028, 0.24, 3.5, 7, 8, "vec_steel",
                                     "prim").translate(ax, y - 0.02, 0.74))
            else:
                m.extend(cylinder(0.115, 0.24, 8, "vec_steel", "prim")
                         .translate(ax, y - 0.02, 0.74))
            # damper next to it
            m.extend(tube_along([(ax + 0.24, y + 0.10, 0.70), (ax + 0.30, y + 0.10, 1.02)],
                                0.040, 6, "vec_steel", "damper"))
        # secondary (flexicoil) springs carrying the body
        for sx in (-0.95, 0.95):
            if lod == 0:
                m.extend(coil_spring(0.185, 0.040, 0.34, 4.0, 8, 9, "vec_steel",
                                     "flexi").translate(sx, y + side * 0.10, 0.72))
            else:
                m.extend(cylinder(0.185, 0.34, 9, "vec_steel", "flexi")
                         .translate(sx, y + side * 0.10, 0.72))
        # brake cylinders
        for ax in (ax0, ax1):
            m.extend(cylinder(0.105, 0.34, seg, "vec_bogie", "brakecyl", axis="y")
                     .rotate_z(0.0).translate(ax + 0.42, y - side * 0.34, 0.86))

    # Visible frame cut-outs: a lighter web between the beam flanges.
    for side in (1, -1):
        y = side * FRAME_Y
        for cx in (-1.05, 1.05):
            m.extend(box(cx - 0.30, cx + 0.30, y - 0.115, y + 0.115,
                         FRAME_BOT + 0.16, FRAME_TOP - 0.04, "vec_frame", "web"))
        # lifting eye
        m.extend(box(ax1 + 0.42, ax1 + 0.60, y - 0.045, y + 0.045,
                     FRAME_TOP - 0.02, FRAME_TOP + 0.10, "vec_handrail", "lift_eye"))
        m.extend(box(ax0 - 0.60, ax0 - 0.42, y - 0.045, y + 0.045,
                     FRAME_TOP - 0.02, FRAME_TOP + 0.10, "vec_handrail", "lift_eye"))
    # Traction rod towards the body centre.
    m.extend(tube_along([(-1.35, 0.0, 0.66), (1.35, 0.0, 0.66)], 0.055, 8,
                        "vec_steel", "traction_rod"))

    # transverse members
    for tx in (ax0 + 0.55, -0.0, ax1 - 0.55):
        m.extend(box(tx - 0.11, tx + 0.11, -FRAME_Y, FRAME_Y,
                     FRAME_BOT + 0.16, FRAME_TOP - 0.03, "vec_bogie", "transom"))
    # bolster / centre pivot
    m.extend(chamfered_box(0.0, 0.0, 1.00, 0.90, 1.05, 0.22, 0.08,
                           "vec_bogie", "bolster"))
    # traction motors, slung next to each axle
    for ax in (ax0, ax1):
        d = -0.42 if ax < 0 else 0.42
        m.extend(cylinder(0.255, 1.05, seg, "vec_frame", "motor", axis="y")
                 .translate(ax + d, -0.525, 0.66))
        m.extend(chamfered_box(ax + d * 0.30, 0.60, 0.64, 0.46, 0.52, 0.52, 0.06,
                               "vec_frame", "gearbox"))
    # sand pipes towards the rail head
    if lod == 0:
        for ax, side in ((ax0, 1), (ax0, -1), (ax1, 1), (ax1, -1)):
            y = side * (WHEEL_Y - 0.03)
            m.extend(tube_along([(ax - 0.36, y, 0.80), (ax - 0.46, y, 0.16)],
                                0.028, 5, "vec_frame", "sandpipe"))
    return m


def build_bogie(lod: int = 0) -> dict[str, Mesh]:
    """Bogie parts in bogie-local space (origin = pivot centre at rail level)."""
    parts = {"frame": bogie_frame(lod)}
    for i, ax in enumerate((-D.BOGIE_WHEELBASE / 2.0, D.BOGIE_WHEELBASE / 2.0)):
        ws = wheelset(lod)
        ws.name = f"axle{i + 1}"
        parts[f"axle{i + 1}"] = ws
        parts[f"axle{i + 1}_x"] = ax  # remembered for the Unity hierarchy
    return parts
