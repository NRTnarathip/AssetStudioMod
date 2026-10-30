# AovHeroExport — AOV/RoV hero FBX + animation exporter

Exports a hero character rig (skeleton hierarchy + skinned mesh) as a single
FBX with every animation clip bound as an action. Built on the AssetStudioMod
model converter, so the output FBX opens directly in Blender / Unity.

```
dotnet Tools/AovHeroExport/bin/Release/net8.0/AovHeroExport.dll <workDir> <outDir> <rigFilter> <mode> [clipSource] [clipRegex]
```

- `rigFilter` — substring match against the rig root name (e.g. `5281_DaMo_LOD1`, `52810_DaMo_Low`).
- `mode` — `list` (print candidate rigs + clip count, no export), `export`, `diag` (dump per-clip track binding).
- `clipSource` —
  - `all` (default) — every loaded clip, richest duplicate wins (recommended).
  - `hero` — clips from bundles sharing the hero numeric prefix (`528_damo_*`).
  - `bundle` — clips from the same physical bundle as the rig.
- `clipRegex` — optional regex to keep only matching clip names (e.g. `^Atk|^Spell`).

Run `ExportAovHeroFbx.bat` in the repo root for a one-click Damo export.

## How AOV splits a character (important)

For hero 528 (Damo), the content is spread across files like:

| bundle | contents |
|---|---|
| `528_damo_battle_base.assetbundle` | rig hierarchy + meshes (LOD1/2/3) |
| `528_damo_battle_base_low_raw_h.assetbundle` | clips (often **empty/stripped**) |
| `52810_damo_battle_low_raw_h.assetbundle` | skin-10 rig + **full clips** (Atk/Spell/Idle/Run/Dead/...) |

Key facts learned while debugging:

1. **There is no usable Animator for the character.** The game creates it at
   runtime, so `-m animator` fails with "Transform hierarchy has been
   optimized, but can't find Avatar". Exporting the *GameObject* hierarchy
   (what this tool does) works because `ModelConverter` builds bone path
   hashes from the hierarchy itself.
2. **AOV bakes the rig root name into bone path hashes** — skin clips hash
   `crc("52810_DaMo_Low/Bip001/...")` and cannot bind to the base rig
   (`5281_DaMo_LOD1/...`). `ModelConverter` was patched to seed alias roots
   (`bonePathAliasRoots`) so cross-variant clips resolve where bones match.
3. **Base-hero clips in this build are placeholders** (`m_Legacy=true`,
   empty muscle clip — only constant keys). The full motion data for the
   same clip names lives in the skin bundles. The type tree also carries
   AOV-custom fields (`m_SelfClip`, `m_CompressedBindings`,
   `m_CompressWithRotationQuantize`) that AssetStudio does not decode, so
   some clips export with reduced keyframes.
4. Base rig and skin rig skeletons genuinely differ (cosmetic bones:
   `Bone_Ribbon*`/`Bone_Ears*` on base vs `Bone_Hair_F*`/`Bone_Chest*` on
   skin 10; 50 vs 58 bones, 28 shared). Full-body actions therefore export
   best from the *skin* rig; the base rig exports all 35 actions but many
   bind only their common bones.

## Verified results (Damo, hero 528)

- `52810_DaMo_Low.fbx` — 1 armature (58 bones), 1 skinned mesh (1316 verts,
  52 vgroups), **33 actions / 182,520 keyframes** (Atk1-9, Spell1-3/6,
  Idle family, Run family, Dead, Stun, Revival, Gohome/Home, Dance01).
- `5281_DaMo_LOD1.fbx` — base rig (50 bones), mesh (1450 verts),
  **35 actions / 44,180 keyframes**.

## Helper: AovHeroScan

`Tools/AovHeroScan` prints everything the exporter relies on: avatars,
animators with resolved/unresolved Avatars, rig roots, per-bundle clip
inventory, clip data layout (streamed/dense/constant/ACL) and conversion
statistics. Use it when a new hero exports with missing animations.

## GUI: "AOV → Hero FBX Exporter"

The same pipeline ships inside AssetStudioGUI as a dialog:

- Menu: **AOV → Hero FBX Exporter...** (inserted into the main menu strip by `AssetStudioGUIForm.AddAovMenu`).
- Form: `AssetStudioGUI/AovHeroExportForm.cs` (no Designer file; layout built in code).
- Fields: game folder, output folder, hero id prefix, rig filter (auto-filled when you click a rig in the list), clip regex, clip source (`all`/`hero`/`bundle`), FBX scale factor / bone size, export-animations toggle.
- **Scan rigs** loads every bundle matching `<heroId>*.assetbundle` under the game folder (prefix match includes skins), then lists rig roots with clip counts (gray = no clips).
- **Export FBX** converts the selected rig — richest clip duplicates win, cross-rig bone path aliases are seeded, output `<rig>.fbx` lands in the output folder.
- Build: `dotnet build AssetStudioGUI -c Debug -f net8.0-windows -p:SolutionDir="<repo>\"` (only Debug native builds exist; Release needs the native projects built first).
- Headless smoke test: `Tools/AovGuiSmokeTest` drives the real form (scan + export) via reflection; expects bundles in `F:\AOV-Hack\gui_quick` and writes to `F:\AOV-Hack\gui_smoke_out` (verified: 33 actions / 36,620 keyframes from the base Damo rig).
