using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AssetStudio;

namespace AovHeroScan
{
    // Scans hero bundles and reports:
    //  - object type counts per file
    //  - every Animator with its GameObject, Avatar and controller resolution state
    //  - every AnimationClip / Avatar / RuntimeAnimatorController name + source
    //  - CAB id -> source bundle mapping
    internal static class Program
    {
        private static int Main(string[] args)
        {
            var input = args.Length > 0
                ? args[0]
                : @"F:\AOV-Hack\damo_work";

            var manager = new AssetsManager();
            manager.LoadFilesAndFolders(input);

            // Map serialized-file name (CAB-xxxx) back to the physical bundle it came from.
            var cabToBundle = new Dictionary<string, string>();
            foreach (var f in manager.AssetsFileList)
            {
                var parent = f.originalPath ?? f.reader?.FullPath ?? f.fullName;
                if (!cabToBundle.ContainsKey(f.fileName))
                    cabToBundle[f.fileName] = Path.GetFileName(parent);
            }
            string Src(SerializedFile f) => cabToBundle.TryGetValue(f.fileName, out var n) ? n : f.fileName;

            var objects = manager.AssetsFileList
                .SelectMany(f => f.Objects)
                .Where(o => o != null)
                .ToArray();

            Console.WriteLine("=== Avatars ===");
            foreach (var avatar in objects.Where(o => o.type == ClassIDType.Avatar).Cast<Avatar>())
                Console.WriteLine($"{avatar.m_Name}  [{Src(avatar.assetsFile)}]");

            Console.WriteLine();
            Console.WriteLine("=== Animators with resolved Avatar (character rigs) ===");
            foreach (var animator in objects.Where(o => o.type == ClassIDType.Animator).Cast<Animator>())
            {
                if (!animator.m_Avatar.TryGet(out var avatar))
                {
                    var go0 = animator.m_GameObject.TryGet(out var g0) ? g0.m_Name : "?";
                    Console.WriteLine($"GO='{go0}'  Avatar=UNRESOLVED (path_id={animator.m_Avatar.m_PathID})  HasTransformHierarchy={animator.m_HasTransformHierarchy}  [{Src(animator.assetsFile)}]");
                    continue;
                }

                var goName = animator.m_GameObject.TryGet(out var go) ? go.m_Name : "?";
                var ctrlState = animator.m_Controller.TryGet(out var ctrl)
                    ? $"{ctrl.m_Name} ({ctrl.type})"
                    : animator.m_Controller.IsNull ? "null" : "UNRESOLVED";

                Console.WriteLine($"GO='{goName}'  Avatar={avatar.m_Name}  Controller={ctrlState}  HasTransformHierarchy={animator.m_HasTransformHierarchy}  [{Src(animator.assetsFile)}]");
            }

            Console.WriteLine();
            Console.WriteLine("=== GameObjects with SkinnedMeshRenderer (per source bundle) ===");
            foreach (var smr in objects.Where(o => o.type == ClassIDType.SkinnedMeshRenderer).Cast<SkinnedMeshRenderer>())
            {
                var goName = smr.m_GameObject.TryGet(out var go) ? go.m_Name : "?";
                Console.WriteLine($"{goName}  [{Src(smr.assetsFile)}]");
            }

            Console.WriteLine();
            Console.WriteLine("=== AnimationClip type tree fields (base bundle clip) ===");
            {
                var probe = objects.Where(o => o.type == ClassIDType.AnimationClip).Cast<AnimationClip>()
                    .FirstOrDefault(c => c.m_Name == "Atk4"
                        && Src(c.assetsFile) == "528_damo_battle_base_low_raw_h.assetbundle");
                if (probe == null)
                    probe = objects.Where(o => o.type == ClassIDType.AnimationClip).Cast<AnimationClip>()
                        .FirstOrDefault(c => Src(c.assetsFile).StartsWith("528_damo_battle_base"));
                var probe2 = objects.Where(o => o.type == ClassIDType.AnimationClip).Cast<AnimationClip>()
                    .FirstOrDefault(c => c.m_Name == "Atk4"
                        && Src(c.assetsFile) == "52810_damo_battle_low_raw_h.assetbundle"
                        && (c.m_MuscleClip?.m_Clip?.data?.m_StreamedClip?.data?.Length ?? 0) > 1000);
                if (probe == null)
                    probe = objects.Where(o => o.type == ClassIDType.AnimationClip).Cast<AnimationClip>().FirstOrDefault();
                if (probe != null)
                {
                    Console.WriteLine($"probe: {(probe as NamedObject)?.m_Name} file={probe.assetsFile.fileName} version={probe.assetsFile.version} isTuanjie={probe.assetsFile.version.IsTuanjie}");
                    var tt = probe.serializedType?.m_Type;
                    if (tt?.m_Nodes != null)
                    {
                        foreach (var n in tt.m_Nodes.Take(30))
                            Console.WriteLine($"  lvl{n.m_Level} {n.m_Type} {n.m_Name}");
                        var hasAnimData = tt.m_Nodes.Any(n => n.m_Name == "m_AnimData");
                        Console.WriteLine($"  has m_AnimData: {hasAnimData}");
                    }
                    else
                        Console.WriteLine("  (no type tree / stripped)");
                }
                if (probe2 != null)
                {
                    Console.WriteLine($"probe2 (skin full Atk4): {(probe2 as NamedObject)?.m_Name} file={probe2.assetsFile.fileName} version={probe2.assetsFile.version} isTuanjie={probe2.assetsFile.version.IsTuanjie}");
                    var tt2 = probe2.serializedType?.m_Type;
                    if (tt2?.m_Nodes != null)
                    {
                        var names = tt2.m_Nodes.Where(n => n.m_Level == 1).Select(n => n.m_Name).ToList();
                        Console.WriteLine("  lvl1 fields: " + string.Join(", ", names));
                    }
                }
            }

            Console.WriteLine();
            Console.WriteLine("=== Conversion stats (one full clip) ===");
            {
                var idle = objects.Where(o => o.type == ClassIDType.AnimationClip).Cast<AnimationClip>()
                    .Where(c => c.m_Name == "Idle")
                    .OrderByDescending(c => c.m_MuscleClip?.m_Clip?.data?.m_StreamedClip?.data?.Length ?? 0)
                    .FirstOrDefault();
                if (idle != null)
                {
                    var cd = idle.m_MuscleClip.m_Clip.data;
                    var frames = cd.m_StreamedClip.ReadData();
                    int total = 0, nullBind = 0, transform = 0, other = 0;
                    var pathHashSet = new HashSet<uint>();
                    foreach (var f in frames)
                        total += f.keyList.Count;
                    foreach (var f in frames)
                        foreach (var k in f.keyList)
                        {
                            var b = idle.m_ClipBindingConstant?.FindBinding(k.index);
                            if (b == null) nullBind++;
                            else if (b.typeID == ClassIDType.Transform) { transform++; pathHashSet.Add(b.path); }
                            else other++;
                        }
                    int capacity = 0;
                    if (idle.m_ClipBindingConstant != null)
                        foreach (var b in idle.m_ClipBindingConstant.genericBindings)
                            capacity += b.typeID == ClassIDType.Transform
                                ? (b.attribute == 2 ? 4 : b.attribute == 1 || b.attribute == 3 || b.attribute == 4 ? 3 : 1)
                                : 1;
                    Console.WriteLine($"clip=Idle streamedFrames={frames.Count} keys={total} null={nullBind} transform={transform} other={other} " +
                                      $"curveCount={cd.m_StreamedClip.curveCount} denseFrames={cd.m_DenseClip.m_FrameCount} denseCurves={cd.m_DenseClip.m_CurveCount} " +
                                      $"denseArray={cd.m_DenseClip.m_SampleArray?.Length ?? 0} constVals={cd.m_ConstantClip.data.Length} bindings={idle.m_ClipBindingConstant.genericBindings.Count} capacity={capacity} uniquePaths={pathHashSet.Count}");
                }
            }

            Console.WriteLine();
            Console.WriteLine("=== Clip data layout (character clips) ===");
            foreach (var clip in objects.Where(o => o.type == ClassIDType.AnimationClip).Cast<AnimationClip>()
                         .Where(c => c.m_Name.StartsWith("Atk") || c.m_Name == "Idle" || c.m_Name == "Run"
                                     || c.m_Name.StartsWith("Spell") || c.m_Name == "Dead" || c.m_Name == "Stun"))
            {
                var muscle = clip.m_MuscleClip;
                var cd = muscle?.m_Clip.data;
                var streamed = cd?.m_StreamedClip;
                var dense = cd?.m_DenseClip;
                var constant = cd?.m_ConstantClip;
                int frames = -1;
                try { frames = streamed != null ? streamed.ReadData().Count : -1; } catch (Exception e) { frames = -2; }
                Console.WriteLine($"{clip.m_Name,-24}[{Src(clip.assetsFile)}] legacy={clip.m_Legacy} sampleRate={clip.m_SampleRate:F1} " +
                                  $"streamedRaw={(streamed?.data?.Length ?? -1)} curveCount={(streamed != null ? streamed.curveCount : 0)} frames={frames} " +
                                  $"denseFrames={(dense != null ? dense.m_FrameCount : 0)} denseCurves={(dense?.m_SampleArray?.Length ?? 0)} " +
                                  $"constVals={(constant?.data?.Length ?? -1)} bindings={(clip.m_ClipBindingConstant?.genericBindings?.Count ?? -1)} " +
                                  $"aclTracks={(cd?.m_ACLClip?.m_Tracks?.Length ?? -1)} aclFrames={(cd?.m_ACLClip?.m_FrameCount ?? 0)} aclBones={(cd?.m_ACLClip?.m_BoneCount ?? 0)} aclCurves={(cd?.m_ACLClip?.m_CurveCount ?? 0)} aclMap={(cd?.m_ACLClip?.m_ACLDecoderMap?.Length ?? 0)} fastSample={(cd?.m_ACLClip != null && cd.m_ACLClip.m_UseACLFastSampleMode)}");
            }

            Console.WriteLine();
            Console.WriteLine("=== Character animation clips (per source bundle) ===");
            foreach (var clip in objects.Where(o => o.type == ClassIDType.AnimationClip).Cast<AnimationClip>()
                         .OrderBy(c => Src(c.assetsFile), StringComparer.OrdinalIgnoreCase)
                         .ThenBy(c => c.m_Name, StringComparer.OrdinalIgnoreCase))
                Console.WriteLine($"{clip.m_Name}  [{Src(clip.assetsFile)}]");

            return 0;
        }
    }
}
