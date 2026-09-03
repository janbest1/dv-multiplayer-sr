"""Writers for OBJ/MTL and for the compact payload used by the web viewer."""

from __future__ import annotations

import base64
import json
import struct
from typing import Iterable, Sequence

from .materials import Material
from .mesh import Mesh, Vec3, box_uvs, corner_normals, face_normal

# Axis conversions from model space (X=length, Y=left, Z=up, right handed).
AXES = {
    # Blender / generic: keep as is.
    "zup": (lambda p: (p[0], p[1], p[2]), False),
    # Unity / Derail Valley: Y up, Z along the car, left handed.
    "yup": (lambda p: (-p[1], p[2], p[0]), True),
}


def convert(p: Vec3, axes: str) -> Vec3:
    return AXES[axes][0](p)


def write_obj(objects: Sequence[Mesh], path: str, mtl_name: str,
              axes: str = "zup", crease: float = 38.0, uv_scale: float = 0.5) -> None:
    fn, flip = AXES[axes]
    lines: list[str] = [
        "# Siemens Vectron style locomotive - procedurally generated",
        f"mtllib {mtl_name}",
    ]
    voff = vtoff = vnoff = 1
    for mesh in objects:
        if not mesh.faces:
            continue
        cn = corner_normals(mesh, crease)
        lines.append(f"o {mesh.name}")
        for v in mesh.verts:
            x, y, z = fn(v)
            lines.append(f"v {x:.5f} {y:.5f} {z:.5f}")
        uvs: list[tuple[float, float]] = []
        nrm: list[Vec3] = []
        order = sorted(range(len(mesh.faces)), key=lambda i: mesh.mats[i])
        face_rows: list[tuple[str, list[str]]] = []
        for fi in order:
            face = mesh.faces[fi]
            fnormal = face_normal(mesh.verts, face)
            fuv = box_uvs(mesh.verts, face, fnormal, uv_scale)
            refs: list[str] = []
            for k, vi in enumerate(face):
                uvs.append(fuv[k])
                nx, ny, nz = fn(cn[fi][k])
                nrm.append((nx, ny, nz))
                refs.append(f"{vi + voff}/{len(uvs) + vtoff - 1}/{len(nrm) + vnoff - 1}")
            if flip:
                refs.reverse()
            face_rows.append((mesh.mats[fi], refs))
        for u, v in uvs:
            lines.append(f"vt {u:.5f} {v:.5f}")
        for n in nrm:
            lines.append(f"vn {n[0]:.5f} {n[1]:.5f} {n[2]:.5f}")
        cur = None
        for mat, refs in face_rows:
            if mat != cur:
                lines.append(f"usemtl {mat}")
                cur = mat
            lines.append("f " + " ".join(refs))
        voff += len(mesh.verts)
        vtoff += len(uvs)
        vnoff += len(nrm)
    with open(path, "w", encoding="utf-8") as fh:
        fh.write("\n".join(lines) + "\n")


def write_mtl(materials: dict[str, Material], used: Iterable[str], path: str) -> None:
    lines: list[str] = ["# Vectron material library"]
    for name in sorted(set(used)):
        m = materials.get(name)
        if m is None:
            continue
        r, g, b = m.color
        lines += [
            f"newmtl {name}",
            f"Kd {r:.4f} {g:.4f} {b:.4f}",
            f"Ka {r * 0.12:.4f} {g * 0.12:.4f} {b * 0.12:.4f}",
            f"Ks {0.04 + 0.55 * m.metallic:.4f} {0.04 + 0.55 * m.metallic:.4f} "
            f"{0.04 + 0.55 * m.metallic:.4f}",
            f"Ns {m.specular_exponent:.2f}",
            f"d {m.alpha:.3f}",
            f"Pr {m.roughness:.3f}",
            f"Pm {m.metallic:.3f}",
            "illum 2",
            "",
        ]
        if m.emission > 0:
            lines.insert(len(lines) - 1,
                         f"Ke {r * m.emission:.4f} {g * m.emission:.4f} {b * m.emission:.4f}")
    with open(path, "w", encoding="utf-8") as fh:
        fh.write("\n".join(lines) + "\n")


def write_viewer_payload(objects: Sequence[Mesh], materials: dict[str, Material],
                         path: str, crease: float = 38.0,
                         extra: Sequence[Mesh] = ()) -> dict:
    """Quantised geometry for the web viewer.

    Positions become 16 bit integers inside the model's bounding box (0.3 mm
    resolution over a 19 m locomotive) and normals 8 bit, which cuts the
    payload to roughly a third of raw float32 with no visible difference.
    Groups are keyed "<object>|<material>" so the viewer can toggle parts and
    recolour a livery without reloading anything.
    """
    meshes = list(objects) + list(extra)
    lo = [1e30] * 3
    hi = [-1e30] * 3
    for mesh in meshes:
        for v in mesh.verts:
            for c in range(3):
                lo[c] = min(lo[c], v[c])
                hi[c] = max(hi[c], v[c])
    span = [max(1e-6, hi[c] - lo[c]) for c in range(3)]

    groups: dict[str, tuple[list[int], list[int]]] = {}
    for mesh in meshes:
        cn = corner_normals(mesh, crease)
        for fi, face in enumerate(mesh.faces):
            key = f"{mesh.name}|{mesh.mats[fi]}"
            pos, nrm = groups.setdefault(key, ([], []))
            for k in range(1, len(face) - 1):
                for idx in (0, k, k + 1):
                    v = mesh.verts[face[idx]]
                    n = cn[fi][idx]
                    for c in range(3):
                        pos.append(int(round((v[c] - lo[c]) / span[c] * 65535.0)))
                        nrm.append(max(-127, min(127, int(round(n[c] * 127.0)))))

    payload = {
        "bounds": {"lo": [round(v, 5) for v in lo], "span": [round(v, 5) for v in span]},
        "materials": {}, "groups": {},
    }
    for key, (pos, nrm) in groups.items():
        payload["groups"][key] = {
            "p": base64.b64encode(struct.pack(f"<{len(pos)}H", *pos)).decode("ascii"),
            "n": base64.b64encode(struct.pack(f"<{len(nrm)}b", *nrm)).decode("ascii"),
        }
    for name, m in materials.items():
        payload["materials"][name] = {
            "color": [round(c, 4) for c in m.color],
            "roughness": round(m.roughness, 3), "metallic": round(m.metallic, 3),
            "alpha": round(m.alpha, 3), "emission": round(m.emission, 3),
        }
    with open(path, "w", encoding="utf-8") as fh:
        json.dump(payload, fh, separators=(",", ":"))
    return payload
