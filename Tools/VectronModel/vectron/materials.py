"""Material table and liveries.

Each material carries an OBJ/MTL description plus PBR hints that the Blender
pipeline turns into a Principled BSDF (and that Unity's standard shader maps
onto one to one).
"""

from __future__ import annotations

from dataclasses import dataclass


@dataclass(frozen=True)
class Material:
    name: str
    color: tuple[float, float, float]
    roughness: float = 0.55
    metallic: float = 0.0
    alpha: float = 1.0
    emission: float = 0.0

    @property
    def specular_exponent(self) -> float:
        # MTL Ns from roughness, roughly matching the Blender/Unity look
        return max(2.0, 900.0 * (1.0 - self.roughness) ** 2)


def _m(name, color, rough=0.55, metal=0.0, alpha=1.0, emis=0.0) -> Material:
    return Material(name, color, rough, metal, alpha, emis)


# Colours that do not depend on the livery ---------------------------------
BASE_MATERIALS: dict[str, Material] = {
    "vec_roof":       _m("vec_roof", (0.29, 0.30, 0.32), 0.62),
    "vec_roof_equip": _m("vec_roof_equip", (0.36, 0.37, 0.39), 0.50, 0.6),
    "vec_glass":      _m("vec_glass", (0.055, 0.075, 0.095), 0.09, 0.0, 0.62),
    "vec_grille":     _m("vec_grille", (0.085, 0.090, 0.095), 0.70),
    "vec_rubber":     _m("vec_rubber", (0.045, 0.045, 0.050), 0.85),
    "vec_frame":      _m("vec_frame", (0.115, 0.120, 0.125), 0.72),
    "vec_bogie":      _m("vec_bogie", (0.135, 0.140, 0.145), 0.66, 0.35),
    "vec_steel":      _m("vec_steel", (0.44, 0.45, 0.47), 0.38, 0.90),
    "vec_wheel":      _m("vec_wheel", (0.29, 0.29, 0.30), 0.45, 0.85),
    "vec_tyre":       _m("vec_tyre", (0.56, 0.57, 0.59), 0.26, 0.95),
    "vec_brake_disc": _m("vec_brake_disc", (0.33, 0.30, 0.28), 0.42, 0.80),
    "vec_insulator":  _m("vec_insulator", (0.72, 0.71, 0.66), 0.30),
    "vec_copper":     _m("vec_copper", (0.42, 0.26, 0.16), 0.40, 0.85),
    "vec_panto":      _m("vec_panto", (0.44, 0.075, 0.065), 0.45, 0.35),
    "vec_carbon":     _m("vec_carbon", (0.13, 0.13, 0.14), 0.55, 0.20),
    "vec_light":      _m("vec_light", (0.92, 0.93, 0.88), 0.12, 0.0, 1.0, 0.85),
    "vec_light_red":  _m("vec_light_red", (0.62, 0.07, 0.07), 0.15, 0.0, 1.0, 0.55),
    "vec_handrail":   _m("vec_handrail", (0.80, 0.72, 0.14), 0.42, 0.30),
    "vec_plough":     _m("vec_plough", (0.17, 0.18, 0.19), 0.62),
    "vec_buffer":     _m("vec_buffer", (0.24, 0.25, 0.26), 0.48, 0.60),
    "vec_interior":   _m("vec_interior", (0.20, 0.21, 0.23), 0.75),
}

# Livery = body / accent / dark mask colours --------------------------------
LIVERIES: dict[str, dict[str, Material]] = {
    # Neutral Siemens-style white demonstrator, without any branding
    "white": {
        "vec_body":   _m("vec_body", (0.855, 0.860, 0.865), 0.42),
        "vec_accent": _m("vec_accent", (0.15, 0.16, 0.18), 0.45),
        "vec_mask":   _m("vec_mask", (0.075, 0.080, 0.085), 0.40),
    },
    "black": {
        "vec_body":   _m("vec_body", (0.115, 0.120, 0.130), 0.40),
        "vec_accent": _m("vec_accent", (0.72, 0.14, 0.10), 0.45),
        "vec_mask":   _m("vec_mask", (0.055, 0.058, 0.062), 0.40),
    },
    "red": {
        "vec_body":   _m("vec_body", (0.560, 0.075, 0.075), 0.42),
        "vec_accent": _m("vec_accent", (0.83, 0.84, 0.85), 0.45),
        "vec_mask":   _m("vec_mask", (0.075, 0.080, 0.085), 0.40),
    },
    "blue": {
        "vec_body":   _m("vec_body", (0.085, 0.215, 0.400), 0.42),
        "vec_accent": _m("vec_accent", (0.86, 0.72, 0.10), 0.45),
        "vec_mask":   _m("vec_mask", (0.060, 0.065, 0.075), 0.40),
    },
    # Muted industrial green that sits well next to Derail Valley's own stock
    "dv": {
        "vec_body":   _m("vec_body", (0.180, 0.290, 0.235), 0.50),
        "vec_accent": _m("vec_accent", (0.78, 0.66, 0.16), 0.48),
        "vec_mask":   _m("vec_mask", (0.070, 0.075, 0.072), 0.45),
    },
}


def build_table(livery: str = "white") -> dict[str, Material]:
    if livery not in LIVERIES:
        raise SystemExit(f"unknown livery '{livery}', pick one of {sorted(LIVERIES)}")
    table = dict(BASE_MATERIALS)
    table.update(LIVERIES[livery])
    return table
