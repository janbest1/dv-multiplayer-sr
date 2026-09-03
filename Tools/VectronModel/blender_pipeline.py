#!/usr/bin/env python3
"""Build the Vectron directly in Blender and export it for Derail Valley.

Run either as a Blender script or, with the `bpy` pip module installed, as a
plain Python script:

    blender --background --python blender_pipeline.py -- --out dist
    python3 blender_pipeline.py --out dist

It creates the object hierarchy Custom Car Loader expects

    Vectron_193
      Vectron_Body / _Glass / _Handrails / _RoofEquipment / _Underframe ...
      BogieF > bogie_car > [axle] > wheels          (same for BogieR)
      [coupler_rig_front] / [coupler_rig_rear]      (empties at Y = 1.05)
      [colliders] > [collision] / [walkable] / [bogies]

with the origins in the right places, assigns Principled BSDF materials,
builds the LOD chain with Decimate and writes .blend, .fbx and .glb.
"""

from __future__ import annotations

import math
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import bpy  # noqa: E402  (only available inside Blender / the bpy module)

from vectron import dims as D  # noqa: E402
from vectron.build import build_model  # noqa: E402
from vectron.mesh import corner_normals  # noqa: E402

# Model space has +X forward and +Z up; Blender's FBX exporter maps -Y to
# Unity's forward, so the whole model is turned a quarter turn on export.
ROT = -math.pi / 2.0
CREASE_ANGLE = 38.0


def to_blender(p):
    return (p[1], -p[0], p[2])


def clear_scene() -> None:
    bpy.ops.wm.read_factory_settings(use_empty=True)


def make_material(name: str, spec) -> bpy.types.Material:
    mat = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    mat.use_nodes = True
    bsdf = mat.node_tree.nodes.get("Principled BSDF")
    r, g, b = spec.color
    bsdf.inputs["Base Color"].default_value = (r, g, b, 1.0)
    bsdf.inputs["Roughness"].default_value = spec.roughness
    bsdf.inputs["Metallic"].default_value = spec.metallic
    if "Alpha" in bsdf.inputs:
        bsdf.inputs["Alpha"].default_value = spec.alpha
    if spec.alpha < 0.999:
        mat.blend_method = "BLEND"
    if spec.emission > 0.0 and "Emission Color" in bsdf.inputs:
        bsdf.inputs["Emission Color"].default_value = (r, g, b, 1.0)
        bsdf.inputs["Emission Strength"].default_value = spec.emission
    return mat


def add_mesh_object(part, materials) -> bpy.types.Object:
    src = part.mesh
    pivot = part.pivot
    verts = [to_blender((v[0] - pivot[0], v[1] - pivot[1], v[2] - pivot[2]))
             for v in src.verts]
    faces = [tuple(f) for f in src.faces]
    me = bpy.data.meshes.new(part.name + "_mesh")
    me.from_pydata(verts, [], faces)

    slots: dict[str, int] = {}
    for name in dict.fromkeys(src.mats):
        me.materials.append(make_material(name, materials[name]))
        slots[name] = len(slots)
    # Material indices must be written before validate() prunes anything,
    # otherwise polygon order and the source face list drift apart.
    for i, poly in enumerate(me.polygons):
        poly.material_index = slots[src.mats[i]]
        poly.use_smooth = True
    me.update()
    me.validate(verbose=False)

    obj = bpy.data.objects.new(part.name, me)
    obj.location = to_blender(pivot)
    bpy.context.collection.objects.link(obj)
    mark_sharp_edges(obj, CREASE_ANGLE)
    return obj


def mark_sharp_edges(obj: bpy.types.Object, angle_deg: float) -> None:
    """Blender 4.1+ honours sharp edges directly instead of auto smooth."""
    prev = bpy.context.view_layer.objects.active
    bpy.context.view_layer.objects.active = obj
    obj.select_set(True)
    bpy.ops.object.mode_set(mode="EDIT")
    bpy.ops.mesh.select_all(action="DESELECT")
    bpy.ops.mesh.select_mode(type="EDGE")
    bpy.ops.mesh.edges_select_sharp(sharpness=math.radians(angle_deg))
    bpy.ops.mesh.mark_sharp()
    bpy.ops.mesh.select_all(action="DESELECT")
    bpy.ops.object.mode_set(mode="OBJECT")
    obj.select_set(False)
    if prev is not None:
        bpy.context.view_layer.objects.active = prev


def add_empty(name: str, loc, parent=None) -> bpy.types.Object:
    e = bpy.data.objects.new(name, None)
    e.empty_display_size = 0.35
    e.location = to_blender(loc)
    bpy.context.collection.objects.link(e)
    if parent is not None:
        e.parent = parent
        e.matrix_parent_inverse = parent.matrix_world.inverted()
    return e


def add_collider_box(name: str, centre, size, parent) -> bpy.types.Object:
    bpy.ops.mesh.primitive_cube_add(size=1.0, location=to_blender(centre))
    obj = bpy.context.active_object
    obj.name = name
    sx, sy, sz = size
    obj.scale = (sy, sx, sz)          # sizes follow the same quarter turn
    obj.display_type = "WIRE"
    obj.hide_render = True          # proxy only: shape for the Unity collider
    obj.parent = parent
    obj.matrix_parent_inverse = parent.matrix_world.inverted()
    return obj


def build(livery: str, variant: str, panto: float, lods: int) -> dict:
    model = build_model(livery, variant, 0, panto)
    clear_scene()
    root = add_empty("Vectron_193", (0.0, 0.0, 0.0))

    bogie_roots: dict[str, bpy.types.Object] = {}
    for tag, sgn in (("BogieF", 1), ("BogieR", -1)):
        bx = sgn * D.BOGIE_PIVOT_DIST / 2.0
        b = add_empty(tag, (bx, 0.0, 0.0), root)          # must stay at Y = 0
        car = add_empty("bogie_car", (bx, 0.0, 0.0), b)
        bogie_roots[tag] = car

    body_objs: list[bpy.types.Object] = []
    for part in model.parts:
        obj = add_mesh_object(part, model.materials)
        if part.parent and part.parent.startswith(("BogieF", "BogieR")):
            tag = part.parent.split("/")[0]
            car = bogie_roots[tag]
            if part.parent.endswith("[axle]"):
                axle = add_empty("[axle]", part.pivot, car)
                obj.parent = axle
                obj.matrix_parent_inverse = axle.matrix_world.inverted()
            else:
                obj.parent = car
                obj.matrix_parent_inverse = car.matrix_world.inverted()
        else:
            obj.parent = root
            obj.matrix_parent_inverse = root.matrix_world.inverted()
            body_objs.append(obj)

    # Coupler rigs: CCL expects these at X = 0, Y = 1.05 in Unity space.
    for tag, sgn in (("[coupler_rig_front]", 1), ("[coupler_rig_rear]", -1)):
        rig = add_empty(tag, (sgn * D.BUFFER_FACE_X, 0.0, D.COUPLER_HEIGHT), root)
        add_empty("BuffersAndChainRig", (sgn * D.BUFFER_FACE_X, 0.0,
                                         D.COUPLER_HEIGHT), rig)
        for side, nm in ((1, "buffer anchor left"), (-1, "buffer anchor right")):
            add_empty(nm, (sgn * D.BUFFER_FACE_X, side * D.BUFFER_HALF,
                           D.COUPLER_HEIGHT), rig)

    # Collider proxies, so the Unity side only has to be checked, not measured.
    col = add_empty("[colliders]", (0.0, 0.0, 0.0), root)
    add_collider_box("[collision]", (0.0, 0.0, 2.70),
                     (D.LENGTH_OVER_BUFFERS, D.WIDTH, 3.30), col)
    add_collider_box("[walkable]", (0.0, 0.0, 2.68),
                     (2 * D.BODY_HALF_LEN, D.WIDTH - 0.02, 3.20), col)
    add_collider_box("[items]", (0.0, 0.0, 2.68),
                     (2 * D.BODY_HALF_LEN - 0.2, D.WIDTH - 0.10, 3.10), col)
    bog = add_empty("[bogies]", (0.0, 0.0, 0.0), col)
    for sgn in (1, -1):
        add_empty(f"bogie_capsule_{'F' if sgn > 0 else 'R'}",
                  (sgn * D.BOGIE_PIVOT_DIST / 2.0, 0.0, D.WHEEL_RADIUS), bog)

    # LOD chain: halve the triangle count per level, as CCL recommends.
    lod_objs: list[list[bpy.types.Object]] = []
    for level in range(1, lods + 1):
        copies = []
        for obj in body_objs:
            dup = obj.copy()
            dup.data = obj.data.copy()
            dup.name = f"{obj.name}_LOD{level}"
            bpy.context.collection.objects.link(dup)
            dup.parent = root
            mod = dup.modifiers.new("decimate", "DECIMATE")
            mod.ratio = 0.5 ** level
            copies.append(dup)
        lod_objs.append(copies)
    return {"root": root, "model": model, "lods": lod_objs}


def export(out: str, stem: str) -> None:
    os.makedirs(out, exist_ok=True)
    bpy.ops.wm.save_as_mainfile(filepath=os.path.join(out, stem + ".blend"))
    bpy.ops.export_scene.fbx(
        filepath=os.path.join(out, stem + ".fbx"),
        use_selection=False, apply_unit_scale=True, global_scale=1.0,
        axis_forward="-Z", axis_up="Y", object_types={"EMPTY", "MESH"},
        use_mesh_modifiers=True, mesh_smooth_type="FACE",
        add_leaf_bones=False, bake_anim=False, path_mode="COPY")
    try:
        bpy.ops.export_scene.gltf(filepath=os.path.join(out, stem + ".glb"),
                                  export_format="GLB", export_apply=True)
    except Exception as exc:                                   # noqa: BLE001
        print("glTF export skipped:", exc)


def main() -> None:
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else sys.argv[1:]
    import argparse
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", default="dist")
    ap.add_argument("--livery", default="white")
    ap.add_argument("--variant", default="ac")
    ap.add_argument("--panto-raise", type=float, default=0.0)
    ap.add_argument("--lods", type=int, default=3)
    args = ap.parse_args(argv)

    res = build(args.livery, args.variant, args.panto_raise, args.lods)
    verts, tris = res["model"].stats()
    print(f"built {verts} verts / {tris} tris, {len(res['lods'])} extra LODs")
    export(args.out, f"vectron_{args.livery}")
    print("exported to", os.path.abspath(args.out))


if __name__ == "__main__":
    main()
