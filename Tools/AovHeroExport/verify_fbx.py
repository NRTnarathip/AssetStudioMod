import bpy
import sys

argv = sys.argv
fbx_path = argv[argv.index("--") + 1]

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.fbx(filepath=fbx_path)


def fcurves_of(act):
    out = []
    for layer in act.layers:
        for strip in layer.strips:
            for cbag in strip.channelbags:
                out.extend(cbag.fcurves)
    return out


print("FBX:", fbx_path)
armatures = [o for o in bpy.data.objects if o.type == 'ARMATURE']
meshes = [o for o in bpy.data.objects if o.type == 'MESH']

print("ARMATURES:", len(armatures))
for a in armatures:
    print("  armature:", a.name, "bones:", len(a.data.bones))
    bones = {b.name for b in a.data.bones}
    print("  sample bones:", sorted(list(bones))[:8])

print("MESHES:", len(meshes))
for m in meshes:
    print("  mesh:", m.name, "verts:", len(m.data.vertices),
          "vgroups:", len(m.vertex_groups),
          "armature_mod:", any(md.type == 'ARMATURE' for md in m.modifiers))

actions = list(bpy.data.actions)
print("ACTIONS:", len(actions))
total_keys = 0
for act in sorted(actions, key=lambda x: x.name):
    fcs = fcurves_of(act)
    keys = sum(len(fc.keyframe_points) for fc in fcs)
    total_keys += keys
    bones_touched = {fc.data_path.split('"')[1] for fc in fcs if '"' in fc.data_path}
    print(f"  action: {act.name}  fcurves={len(fcs)} keys={keys} bones={len(bones_touched)} "
          f"range={tuple(round(v, 2) for v in act.frame_range)}")
print("TOTAL KEYFRAMES:", total_keys)
