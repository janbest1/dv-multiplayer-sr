"""A simple cab interior, so the windscreen shows a cab and not an empty box."""

from __future__ import annotations

from . import dims as D
from .mesh import Mesh, box, box_c, chamfered_box, cylinder
from .body import front_x

FLOOR_Z = 1.560


def build_interiors(lod: int = 0) -> Mesh:
    m = Mesh("Vectron_CabInterior")
    if lod >= 2:
        return m
    for sgn in (1, -1):
        m.extend(_cab(sgn, lod))
    return m


def _cab(sgn: int, lod: int) -> Mesh:
    m = Mesh("cab")
    x_bulk = sgn * 6.95            # rear wall of the cab
    x_front = sgn * 8.60
    a, b = sorted((x_bulk, x_front))
    # Floor and rear bulkhead.
    m.extend(box(a, b, -1.36, 1.36, FLOOR_Z - 0.05, FLOOR_Z, "vec_interior", "floor"))
    m.extend(box(min(x_bulk, x_bulk - sgn * 0.06), max(x_bulk, x_bulk - sgn * 0.06),
                 -1.36, 1.36, FLOOR_Z, 3.30, "vec_interior", "bulkhead"))
    # Driver's desk: a low console with a slanted top, offset to one side.
    dx = sgn * 8.18
    m.extend(chamfered_box(dx, sgn * 0.62, 2.02, 0.72, 1.30, 0.62, 0.05,
                           "vec_interior", "desk"))
    m.extend(box(min(dx, dx + sgn * 0.55), max(dx, dx + sgn * 0.55),
                 sgn * 0.62 - 0.60, sgn * 0.62 + 0.60, 2.30, 2.36,
                 "vec_frame", "desk_top"))
    # Two seats.
    for sy in (0.70, -0.72):
        cy = sgn * sy
        m.extend(box_c(sgn * 7.62, cy, 1.98, 0.52, 0.52, 0.14, "vec_interior", "seat"))
        m.extend(box_c(sgn * 7.40, cy, 2.28, 0.12, 0.50, 0.56, "vec_interior", "back"))
        m.extend(cylinder(0.055, 0.36, 6, "vec_frame", "seat_leg")
                 .translate(sgn * 7.62, cy, 1.60))
    # Ceiling panel just under the cab roof.
    m.extend(box(a + 0.10, b - 0.10, -1.20, 1.20, 3.62, 3.68, "vec_interior", "ceiling"))
    return m
