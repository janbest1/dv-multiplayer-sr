#!/usr/bin/env python3
"""Build the Vectron model.

    python3 build_vectron.py --out dist --livery red --lod all --preview

Writes an OBJ/MTL pair per LOD plus, optionally, preview renders and the
payload for the web viewer. Pure standard library - no Blender needed.
"""

from __future__ import annotations

import argparse
import json
import os
import time

from vectron.build import build_model
from vectron.exporters import write_mtl, write_obj, write_viewer_payload
from vectron.materials import LIVERIES
from vectron import preview

VIEWS = {
    "three_quarter": ((21.0, -13.5, 7.2), (0.4, 0.0, 2.2), 26),
    "side": ((0.6, -26.0, 2.6), (0.6, 0.0, 2.4), 22),
    "cab": ((13.0, -6.4, 3.6), (7.6, 0.0, 2.3), 32),
    "front": ((17.5, 0.0, 2.6), (0.0, 0.0, 2.4), 24),
    "roof": ((10.5, -9.0, 9.0), (2.0, 0.0, 2.6), 32),
}


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--out", default="dist", help="output directory")
    ap.add_argument("--livery", default="white", choices=sorted(LIVERIES))
    ap.add_argument("--variant", default="ac", choices=("ac", "ms", "de"),
                    help="ac/ms = electric with pantographs, de = diesel")
    ap.add_argument("--lod", default="0",
                    help="LOD level 0-3, or 'all' for the full LOD chain")
    ap.add_argument("--axes", default="yup", choices=("yup", "zup"),
                    help="yup = Unity/Derail Valley, zup = Blender/generic")
    ap.add_argument("--panto-raise", type=float, default=0.0,
                    help="0 = pantographs folded, 1 = raised")
    ap.add_argument("--preview", action="store_true", help="render preview PNGs")
    ap.add_argument("--viewer", action="store_true",
                    help="also write the viewer payload (JSON)")
    ap.add_argument("--preview-size", type=int, default=560)
    args = ap.parse_args()

    os.makedirs(args.out, exist_ok=True)
    levels = range(4) if args.lod == "all" else [int(args.lod)]
    summary = []

    for lod in levels:
        t0 = time.time()
        model = build_model(args.livery, args.variant, lod, args.panto_raise)
        verts, tris = model.stats()
        stem = f"vectron_{args.livery}_lod{lod}"
        obj_path = os.path.join(args.out, stem + ".obj")
        mtl_name = f"vectron_{args.livery}.mtl"
        used = {m for p in model.parts for m in p.mesh.mats}
        write_obj(model.meshes, obj_path, mtl_name, axes=args.axes)
        write_mtl(model.materials, used, os.path.join(args.out, mtl_name))
        print(f"LOD{lod}: {verts:6d} verts  {tris:6d} tris  "
              f"{time.time() - t0:4.1f}s  -> {obj_path}")
        summary.append({"lod": lod, "file": os.path.basename(obj_path), **model.meta})

        if lod == 0:
            with open(os.path.join(args.out, "hierarchy.json"), "w") as fh:
                json.dump([{"name": p.name, "parent": p.parent, "pivot": p.pivot}
                           for p in model.parts], fh, indent=2)
            if args.viewer:
                write_viewer_payload(model.meshes, model.materials,
                                     os.path.join(args.out, "viewer_payload.json"))
            if args.preview:
                for name, (eye, target, fov) in VIEWS.items():
                    buf = preview.render(model.meshes, model.materials,
                                         args.preview_size,
                                         int(args.preview_size * 0.64),
                                         eye, target, fov=fov, ss=2)
                    preview.write_png(os.path.join(args.out, f"preview_{name}.png"),
                                      args.preview_size,
                                      int(args.preview_size * 0.64), buf)
                print(f"       previews -> {args.out}/preview_*.png")

    with open(os.path.join(args.out, "model_info.json"), "w") as fh:
        json.dump(summary, fh, indent=2)


if __name__ == "__main__":
    main()
