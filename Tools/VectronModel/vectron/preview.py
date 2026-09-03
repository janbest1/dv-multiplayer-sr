"""A tiny software renderer, so the generator can be inspected without any
external tools: z-buffered triangle rasteriser with Gouraud shading, a
back-to-front pass for glass, and a stdlib-only PNG writer.
"""

from __future__ import annotations

import math
import struct
import zlib
from typing import Sequence

from .materials import Material
from .mesh import Mesh, corner_normals, vcross, vdot, vnorm, vsub

KEY_DIR = vnorm((0.45, -0.62, 0.64))
FILL_DIR = vnorm((-0.70, 0.38, 0.25))
RIM_DIR = vnorm((-0.30, 0.85, 0.20))


def _shade(n, base, rough, metal, emis):
    lam = max(0.0, vdot(n, KEY_DIR))
    fil = max(0.0, vdot(n, FILL_DIR)) * 0.30
    rim = max(0.0, vdot(n, RIM_DIR)) ** 2 * 0.18
    amb = 0.20 + 0.14 * max(0.0, n[2])
    spec = (lam ** max(2.0, 60.0 * (1.0 - rough))) * (0.05 + 0.45 * (1.0 - rough))
    out = []
    for c in base:
        v = c * (amb + lam * 0.92 + fil + rim) + spec * (0.35 + 0.65 * metal) * (
            0.4 + 0.6 * c if metal > 0.5 else 1.0) + c * emis * 1.4
        out.append(v)
    return out


def _collect(objects: Sequence[Mesh], materials: dict[str, Material], crease: float):
    opaque, glass = [], []
    for mesh in objects:
        cn = corner_normals(mesh, crease)
        for fi, face in enumerate(mesh.faces):
            mat = materials.get(mesh.mats[fi])
            base = mat.color if mat else (0.8, 0.8, 0.8)
            rough = mat.roughness if mat else 0.6
            metal = mat.metallic if mat else 0.0
            alpha = mat.alpha if mat else 1.0
            emis = mat.emission if mat else 0.0
            for k in range(1, len(face) - 1):
                tri = []
                for idx in (0, k, k + 1):
                    v = mesh.verts[face[idx]]
                    n = cn[fi][idx]
                    tri.append((v, n))
                item = (tri, base, rough, metal, alpha, emis)
                (glass if alpha < 0.99 else opaque).append(item)
    return opaque, glass


def render(objects: Sequence[Mesh], materials: dict[str, Material],
           width: int, height: int, eye, target, up=(0.0, 0.0, 1.0),
           fov: float = 32.0, ss: int = 2, crease: float = 38.0,
           bg: tuple[float, float, float] = (0.92, 0.93, 0.95)) -> bytearray:
    """Render to an RGB byte buffer (width*height*3)."""
    w, h = width * ss, height * ss
    fwd = vnorm(vsub(target, eye))
    right = vnorm(vcross(fwd, up))
    upv = vcross(right, fwd)
    tanf = math.tan(math.radians(fov) / 2.0)
    aspect = w / h

    # Vertical background gradient with a soft horizon.
    color = [0.0] * (w * h * 3)
    for y in range(h):
        t = y / (h - 1)
        f = 0.80 + 0.34 * (1.0 - t) ** 1.5
        row = (bg[0] * f, bg[1] * f, bg[2] * f)
        base = y * w * 3
        for x in range(w):
            i = base + x * 3
            color[i] = row[0]
            color[i + 1] = row[1]
            color[i + 2] = row[2]
    zbuf = [1e30] * (w * h)

    def project(p):
        d = vsub(p, eye)
        zv = vdot(d, fwd)
        if zv < 0.05:
            return None
        xv = vdot(d, right)
        yv = vdot(d, upv)
        sx = (0.5 + 0.5 * (xv / (zv * tanf * aspect))) * w
        sy = (0.5 - 0.5 * (yv / (zv * tanf))) * h
        return sx, sy, zv

    opaque, glass = _collect(objects, materials, crease)

    def draw(items, blend: bool):
        for tri, base, rough, metal, alpha, emis in items:
            pr = [project(v) for v, _ in tri]
            if any(p is None for p in pr):
                continue
            (x0, y0, z0), (x1, y1, z1), (x2, y2, z2) = pr
            area = (x1 - x0) * (y2 - y0) - (x2 - x0) * (y1 - y0)
            if abs(area) < 1e-9:
                continue
            # Two sided: light the visible face regardless of winding.
            flip = -1.0 if area > 0 else 1.0
            cols = []
            for _, n in tri:
                nn = (n[0] * flip, n[1] * flip, n[2] * flip)
                cols.append(_shade(nn, base, rough, metal, emis))
            minx = max(0, int(min(x0, x1, x2)))
            maxx = min(w - 1, int(max(x0, x1, x2)) + 1)
            miny = max(0, int(min(y0, y1, y2)))
            maxy = min(h - 1, int(max(y0, y1, y2)) + 1)
            if minx > maxx or miny > maxy:
                continue
            inv_area = 1.0 / area
            iz0, iz1, iz2 = 1.0 / z0, 1.0 / z1, 1.0 / z2
            for py in range(miny, maxy + 1):
                yc = py + 0.5
                rowoff = py * w
                for px in range(minx, maxx + 1):
                    xc = px + 0.5
                    w0 = ((x1 - xc) * (y2 - yc) - (x2 - xc) * (y1 - yc)) * inv_area
                    if w0 < 0.0:
                        continue
                    w1 = ((x2 - xc) * (y0 - yc) - (x0 - xc) * (y2 - yc)) * inv_area
                    if w1 < 0.0:
                        continue
                    w2 = 1.0 - w0 - w1
                    if w2 < 0.0:
                        continue
                    iz = w0 * iz0 + w1 * iz1 + w2 * iz2
                    z = 1.0 / iz
                    pi = rowoff + px
                    if z >= zbuf[pi]:
                        continue
                    ci = pi * 3
                    for c in range(3):
                        v = w0 * cols[0][c] + w1 * cols[1][c] + w2 * cols[2][c]
                        if blend:
                            color[ci + c] = color[ci + c] * (1.0 - alpha) + v * alpha
                        else:
                            color[ci + c] = v
                    if not blend:
                        zbuf[pi] = z

    draw(opaque, False)
    glass.sort(key=lambda it: -sum(vdot(vsub(v, eye), fwd) for v, _ in it[0]))
    draw(glass, True)

    # Downsample + gamma
    out = bytearray(width * height * 3)
    inv = 1.0 / (ss * ss)
    for y in range(height):
        for x in range(width):
            acc = [0.0, 0.0, 0.0]
            for sy in range(ss):
                base = ((y * ss + sy) * w + x * ss) * 3
                for sx in range(ss):
                    i = base + sx * 3
                    acc[0] += color[i]
                    acc[1] += color[i + 1]
                    acc[2] += color[i + 2]
            o = (y * width + x) * 3
            for c in range(3):
                v = acc[c] * inv
                v = max(0.0, min(1.0, v)) ** (1.0 / 2.2)
                out[o + c] = int(v * 255 + 0.5)
    return out


def write_png(path: str, width: int, height: int, rgb: bytes) -> None:
    raw = b"".join(b"\x00" + bytes(rgb[y * width * 3:(y + 1) * width * 3])
                   for y in range(height))
    def chunk(tag: bytes, data: bytes) -> bytes:
        return (struct.pack(">I", len(data)) + tag + data
                + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF))
    png = (b"\x89PNG\r\n\x1a\n"
           + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0))
           + chunk(b"IDAT", zlib.compress(raw, 6))
           + chunk(b"IEND", b""))
    with open(path, "wb") as fh:
        fh.write(png)


def paste(dst: bytearray, dw: int, src: bytes, sw: int, sh: int, ox: int, oy: int) -> None:
    for y in range(sh):
        d = ((oy + y) * dw + ox) * 3
        s = y * sw * 3
        dst[d:d + sw * 3] = src[s:s + sw * 3]


def contact_sheet(objects, materials, path, views, tile=(560, 360), cols=2,
                  ss: int = 2) -> None:
    """Render several camera setups into one image."""
    tw, th = tile
    rows = (len(views) + cols - 1) // cols
    sheet = bytearray(b"\x1a\x1c\x20" * (tw * cols * th * rows))
    for i, (eye, target, fov) in enumerate(views):
        buf = render(objects, materials, tw - 8, th - 8, eye, target, fov=fov, ss=ss)
        paste(sheet, tw * cols, bytes(buf), tw - 8, th - 8,
              (i % cols) * tw + 4, (i // cols) * th + 4)
    write_png(path, tw * cols, th * rows, sheet)
