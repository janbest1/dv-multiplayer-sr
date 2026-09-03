"""Roof: hatches, high voltage equipment and the single arm pantographs."""

from __future__ import annotations

import math

from . import dims as D
from .body import (I_CHAMFER, I_ROOF_EDGE, Station, mirror_index, ring3d,
                   surface_patch)
from .mesh import (Mesh, box, box_c, chamfered_box, cylinder, revolve,
                   tube_along)

ROOF_END = 6.90        # grey machine room roof ends here, cab roof is painted


def build_roof(stations: list[Station], lod: int = 0, variant: str = "ac",
               panto_raise: float = 0.0) -> dict[str, Mesh]:
    parts: dict[str, Mesh] = {}
    fixed = Mesh("Vectron_Roof_Equipment")
    seg = {0: 14, 1: 10}.get(lod, 8)

    i0, i1 = I_ROOF_EDGE, mirror_index(I_ROOF_EDGE)
    # Removable machine room roof hatches - visible as panel seams from above.
    hatches = ((-6.45, -4.65), (-4.45, -2.65), (-2.45, -0.65),
               (0.65, 2.45), (2.65, 4.45), (4.65, 6.45))
    for a, b in hatches:
        fixed.extend(surface_patch(stations, a, b, i0 + 1, i1 - 1, 0.016,
                                   "vec_roof_equip", "hatch"))
    # Raised rib along both roof edges.
    for i in (I_ROOF_EDGE, mirror_index(I_ROOF_EDGE)):
        fixed.extend(surface_patch(stations, -ROOF_END, ROOF_END, i - 1, i + 1,
                                   0.022, "vec_roof_equip", "roof_rib"))

    if variant == "de":
        # Diesel: exhaust stack and a big radiator grille instead of HV gear.
        fixed.extend(chamfered_box(2.60, 0.0, 4.40, 1.30, 1.10, 0.36, 0.06,
                                   "vec_roof_equip", "exhaust_box"))
        fixed.extend(cylinder(0.185, 0.30, seg, "vec_frame", "stack")
                     .translate(2.60, 0.0, 4.55))
        for cx in (-3.20, -1.60):
            fixed.extend(surface_patch(stations, cx - 0.70, cx + 0.70, i0 + 1, i1 - 1,
                                       0.020, "vec_grille", "radiator"))
        parts["roof"] = fixed
        return parts

    # --- high voltage equipment -----------------------------------------
    # Main circuit breaker with its two support insulators.
    fixed.extend(chamfered_box(0.0, -0.42, 4.46, 1.05, 0.52, 0.34, 0.05,
                               "vec_roof_equip", "breaker"))
    for dx in (-0.34, 0.34):
        fixed.extend(_insulator(seg).translate(dx, 0.42, 4.20))
    fixed.extend(cylinder(0.055, 0.90, seg, "vec_steel", "breaker_tube", axis="x")
                 .translate(-0.45, 0.42, 4.62))
    # Surge arresters near each pantograph.
    for sgn in (1, -1):
        fixed.extend(_insulator(seg, h=0.42).translate(sgn * 1.55, -0.55, 4.18))

    # HV bus bar tube linking both pantographs over the roof.
    bus_z = D.HV_BUS_Z + 0.30
    fixed.extend(cylinder(0.042, 2 * D.PANTO_X, seg, "vec_steel", "hv_bus", axis="x")
                 .translate(-D.PANTO_X, 0.55, bus_z))
    for bx in (-2.2, -0.9, 0.9, 2.2):
        fixed.extend(_insulator(seg, h=0.34, r=0.062).translate(bx, 0.55, 4.20))

    # Roof mounted cooling grilles at both ends of the machine room.
    for sgn in (1, -1):
        fixed.extend(surface_patch(stations, min(sgn * 5.10, sgn * 6.35),
                                   max(sgn * 5.10, sgn * 6.35), i0 + 1, i1 - 1,
                                   0.020, "vec_grille", "roof_vent"))
    # Cable ducts running along the roof beside the equipment.
    for dy in (-0.95, 0.95):
        fixed.extend(box(-6.30, 6.30, dy - 0.075, dy + 0.075, 4.185, 4.265,
                         "vec_roof_equip", "cable_duct"))
    # Antennas.
    for ax in (-5.6, 5.6):
        fixed.extend(cylinder(0.030, 0.26, 6, "vec_frame", "antenna")
                     .translate(ax, -0.75, 4.20))

    parts["roof"] = fixed
    for sgn, tag in ((1, "F"), (-1, "R")):
        p = build_pantograph(panto_raise, lod, seg)
        if sgn < 0:
            p.scale(-1, 1, 1)
        p.translate(sgn * D.PANTO_X, 0.0, 0.0)
        p.name = f"Pantograph_{tag}"
        parts[f"panto_{tag}"] = p
    return parts


def _insulator(seg: int, h: float = 0.30, r: float = 0.075) -> Mesh:
    """Ribbed ceramic insulator."""
    prof = [(0.0, 0.0), (r * 1.5, 0.0), (r * 1.5, 0.035)]
    ribs = 4
    for i in range(ribs):
        z0 = 0.045 + (h - 0.09) * i / ribs
        z1 = 0.045 + (h - 0.09) * (i + 0.55) / ribs
        prof += [(r, z0), (r * 1.55, z0 + 0.012), (r * 1.55, z1 - 0.012), (r, z1)]
    prof += [(r, h - 0.035), (r * 1.5, h - 0.030), (r * 1.5, h), (0.0, h)]
    return revolve(prof, seg, "vec_insulator", "insulator")


def build_pantograph(raise_t: float = 0.0, lod: int = 0, seg: int = 10) -> Mesh:
    """Single arm pantograph, local origin at the roof under its centre.

    raise_t 0 = folded down onto the roof, 1 = raised to working height.
    """
    m = Mesh("Pantograph")
    base_z = 4.16
    # Insulators and base frame.
    for dx in (-0.98, 0.98):
        for dy in (-0.62, 0.62):
            m.extend(_insulator(seg, h=0.28, r=0.070).translate(dx, dy, base_z))
    for dy in (-0.62, 0.62):
        m.extend(box(-1.12, 1.12, dy - 0.038, dy + 0.038, base_z + 0.28,
                     base_z + 0.35, "vec_panto", "panto_beam"))
    for dx in (-1.06, 1.06):
        m.extend(box(dx - 0.038, dx + 0.038, -0.62, 0.62, base_z + 0.285,
                     base_z + 0.345, "vec_panto", "panto_xbeam"))
    pivot = (-0.72, 0.0, base_z + 0.34)

    # Two link geometry: angles interpolate between folded and working height.
    a = math.radians(-1.0 + (36.0 + 1.0) * raise_t)       # lower arm
    c = math.radians(179.0 - (179.0 - 144.0) * raise_t)   # upper arm
    l1, l2 = 1.50, 1.90
    knee = (pivot[0] + l1 * math.cos(a), 0.0, pivot[2] + l1 * math.sin(a))
    head = (knee[0] + l2 * math.cos(c), 0.0, knee[2] + l2 * math.sin(c))

    # Lower arm: an A frame of two tubes plus a diagonal brace.
    for dy in (-0.30, 0.30):
        m.extend(tube_along([(pivot[0], dy, pivot[2]), knee], 0.048, seg // 2 + 3,
                            "vec_panto", "lower_arm"))
    m.extend(tube_along([(pivot[0] + 0.32 * math.cos(a), -0.30,
                          pivot[2] + 0.32 * math.sin(a)),
                         (pivot[0] + 0.32 * math.cos(a), 0.30,
                          pivot[2] + 0.32 * math.sin(a))], 0.030, 6,
                        "vec_panto", "arm_brace"))
    # Upper arm and the pull rod that keeps the head level.
    m.extend(tube_along([knee, head], 0.040, seg // 2 + 3, "vec_panto", "upper_arm"))
    rod_a = (pivot[0] + 0.30, 0.16, pivot[2] + 0.10)
    rod_b = (knee[0] - 0.22 * math.cos(c), 0.16, knee[2] - 0.22 * math.sin(c))
    m.extend(tube_along([rod_a, rod_b], 0.020, 5, "vec_steel", "pull_rod"))

    # Pan head: frame, two carbon contact strips and the down-turned horns.
    hw = 0.975
    hz = head[2] + 0.09
    m.extend(box(head[0] - 0.05, head[0] + 0.05, -hw + 0.10, hw - 0.10,
                 head[2], head[2] + 0.06, "vec_panto", "head_frame"))
    for dx in (-0.115, 0.115):
        strip = box(head[0] + dx - 0.032, head[0] + dx + 0.032, -hw + 0.13, hw - 0.13,
                    hz - 0.028, hz + 0.028, "vec_carbon", "contact_strip")
        m.extend(strip)
        for side in (1, -1):
            horn = tube_along([(head[0] + dx, side * (hw - 0.13), hz),
                               (head[0] + dx, side * (hw + 0.03), hz - 0.03),
                               (head[0] + dx + 0.02, side * (hw + 0.11), hz - 0.13)],
                              0.026, 5, "vec_panto", "horn")
            m.extend(horn)
    for side in (1, -1):
        m.extend(tube_along([(head[0], side * 0.28, head[2] - 0.02),
                             (head[0], side * 0.72, hz - 0.02)], 0.022, 5,
                            "vec_panto", "head_support"))
    return m
