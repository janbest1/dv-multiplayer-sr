"""Assembles the complete locomotive from its modules."""

from __future__ import annotations

from dataclasses import dataclass, field

from . import dims as D
from . import body as B
from . import bogie as BG
from . import interior as I
from . import roof as R
from . import underframe as U
from .materials import build_table
from .mesh import Mesh


@dataclass
class Part:
    """One exported object, with the parenting Derail Valley expects."""
    name: str
    mesh: Mesh
    parent: str | None = None
    pivot: tuple[float, float, float] = (0.0, 0.0, 0.0)


@dataclass
class Model:
    parts: list[Part] = field(default_factory=list)
    materials: dict = field(default_factory=dict)
    meta: dict = field(default_factory=dict)

    @property
    def meshes(self) -> list[Mesh]:
        return [p.mesh for p in self.parts]

    def stats(self) -> tuple[int, int]:
        return (sum(len(p.mesh.verts) for p in self.parts),
                sum(len(p.mesh.triangles()) for p in self.parts))


def build_model(livery: str = "white", variant: str = "ac", lod: int = 0,
                panto_raise: float = 0.0) -> Model:
    stations = B.sample_stations(max(2, B.SUBDIV - lod))
    parts: list[Part] = []

    bp = B.build_body(stations, lod)
    body = bp["body"]
    body.extend(bp["front"])
    body.name = "Vectron_Body"
    parts.append(Part("Vectron_Body", body))
    parts.append(Part("Vectron_Glass", bp["glass"]))
    parts.append(Part("Vectron_Handrails", bp["rails"]))

    rp = R.build_roof(stations, lod, variant, panto_raise)
    parts.append(Part("Vectron_RoofEquipment", rp["roof"]))
    for key, tag in (("panto_F", "F"), ("panto_R", "R")):
        if key in rp:
            parts.append(Part(f"Pantograph_{tag}", rp[key]))

    parts.append(Part("Vectron_Underframe", U.build_underframe(lod)))
    inter = I.build_interiors(lod)
    if inter.faces:
        parts.append(Part("Vectron_CabInterior", inter))

    # Bogies, in the hierarchy CCL expects: BogieF > bogie_car > [axle].
    for tag, sgn in (("BogieF", 1), ("BogieR", -1)):
        bx = sgn * D.BOGIE_PIVOT_DIST / 2.0
        bg = BG.build_bogie(lod)
        frame = bg["frame"].copy(f"{tag}_frame")
        if sgn < 0:
            frame.scale(-1, 1, 1)
        frame.translate(bx, 0.0, 0.0)
        parts.append(Part(f"{tag}_frame", frame, parent=f"{tag}/bogie_car",
                          pivot=(bx, 0.0, 0.0)))
        for i in (1, 2):
            ax = bg[f"axle{i}_x"] * (1 if sgn > 0 else -1)
            ws = bg[f"axle{i}"].copy(f"{tag}_axle{i}")
            ws.translate(bx + ax, 0.0, D.WHEEL_RADIUS)
            parts.append(Part(f"{tag}_axle{i}", ws,
                              parent=f"{tag}/bogie_car/[axle]",
                              pivot=(bx + ax, 0.0, D.WHEEL_RADIUS)))

    model = Model(parts, build_table(livery))
    verts, tris = model.stats()
    model.meta = {
        "livery": livery, "variant": variant, "lod": lod,
        "panto_raise": panto_raise, "vertices": verts, "triangles": tris,
        "length_over_buffers": D.LENGTH_OVER_BUFFERS, "width": D.WIDTH,
        "height": D.HEIGHT_ROOF, "coupler_height": D.COUPLER_HEIGHT,
        "bogie_pivot_distance": D.BOGIE_PIVOT_DIST,
    }
    return model
