using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AssetStudio;

namespace AovHeroExport
{
    // Exports a hero character rig (GameObject hierarchy containing SkinnedMeshRenderers)
    // as a single FBX, with animation clips bound to it.
    //
    // Usage:
    //   AovHeroExport <input folder> <output folder> <name filter> [list|export] [bundle|all] [clip name regex]
    //
    //   list   - print candidate rig roots and their animation clips (no export)
    //   export - build and write the FBX files (default)
    //   clip source:
    //     hero   - all AnimationClips from bundles of the same hero (default).
    //              AOV splits character data across 528_damo_battle_base (rig)
    //              and 528_damo_battle_base_low_raw_h (clips + avatar).
    //     bundle - all AnimationClips from the same physical bundle as the rig
    //     all    - every AnimationClip loaded (FBX writer matches clips to the
    //              skeleton by bone path; clips from other rigs are ignored)
    //   The optional regex further restricts which clips get exported.
    internal static class Program
    {
        private static Dictionary<string, string> _cabToBundle = new Dictionary<string, string>();

        private static int Main(string[] args)
        {
            if (args.Length < 3)
            {
                Console.Error.WriteLine("Usage: AovHeroExport <input folder> <output folder> <name filter> [list|export] [bundle|all] [clip name regex]");
                return 1;
            }

            var input = args[0];
            var output = args[1];
            var filter = args[2];
            var mode = args.Length > 3 ? args[3].ToLowerInvariant() : "export";
            var clipSource = args.Length > 4 ? args[4].ToLowerInvariant() : "hero";
            var clipRegex = args.Length > 5 ? new System.Text.RegularExpressions.Regex(args[5], System.Text.RegularExpressions.RegexOptions.IgnoreCase) : null;

            var manager = new AssetsManager();
            manager.LoadFilesAndFolders(input);

            var objects = manager.AssetsFileList
                .SelectMany(f => f.Objects)
                .Where(o => o != null)
                .ToArray();

            // Map serialized-file name (CAB-xxxx) back to the physical bundle it came from.
            var cabToBundle = new Dictionary<string, string>();
            foreach (var f in manager.AssetsFileList)
            {
                var parent = f.originalPath ?? f.reader?.FullPath ?? f.fullName;
                if (!cabToBundle.ContainsKey(f.fileName))
                    cabToBundle[f.fileName] = Path.GetFileName(parent);
            }
            _cabToBundle = cabToBundle;
            string Src(SerializedFile f) => cabToBundle.TryGetValue(f.fileName, out var n) ? n : f.fileName;

            var clipsByBundle = objects
                .Where(o => o.type == ClassIDType.AnimationClip)
                .Cast<AnimationClip>()
                .GroupBy(c => Src(c.assetsFile), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => g.GroupBy(c => c.m_Name, StringComparer.OrdinalIgnoreCase)
                          .Select(grp => grp.ToList())
                          .ToList());

            var allClips = objects
                .Where(o => o.type == ClassIDType.AnimationClip)
                .Cast<AnimationClip>()
                .GroupBy(c => c.m_Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.ToList())
                .ToList();

            var clipsByHero = objects
                .Where(o => o.type == ClassIDType.AnimationClip)
                .Cast<AnimationClip>()
                .GroupBy(c => HeroKey(Src(c.assetsFile)), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => g.GroupBy(c => c.m_Name, StringComparer.OrdinalIgnoreCase)
                          .Select(g2 => g2.ToList())
                          .ToList());

            // Topmost transform of every hierarchy that owns a SkinnedMeshRenderer = rig root candidate.
            var roots = new List<GameObject>();
            foreach (var smr in objects.Where(o => o.type == ClassIDType.SkinnedMeshRenderer).Cast<SkinnedMeshRenderer>())
            {
                if (!smr.m_GameObject.TryGet(out var go) || go.m_Transform == null)
                    continue;

                var top = go.m_Transform;
                while (top.m_Father.TryGet(out var father))
                    top = father;

                if (top.m_GameObject.TryGet(out var rootGo) && !roots.Contains(rootGo))
                    roots.Add(rootGo);
            }

            // AOV bakes the rig root's name into bone path hashes, so pass every
            // rig root we know as an alias for cross-variant clip binding.
            var aliasRoots = roots.Select(r => r.m_Name).Distinct().ToList();

            var matched = roots
                .Where(r => r.m_Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0
                            || SubtreeMatches(r, filter))
                .OrderBy(r => r.m_Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Per-rig path-hash sets (full path + every suffix, same as
            // ModelConverter.CreateBonePathHash) used to pick the clip variant
            // whose bindings actually resolve against this rig.
            var rootHashes = new Dictionary<GameObject, HashSet<uint>>();
            foreach (var root in matched)
            {
                var set = new HashSet<uint>();
                void Walk(Transform t, string path)
                {
                    var goName = t.m_GameObject.TryGet(out var g) ? g.m_Name : "?";
                    var full = path.Length == 0 ? goName : path + "/" + goName;
                    var name = full;
                    while (true)
                    {
                        var bytes = Encoding.UTF8.GetBytes(name);
                        set.Add(SevenZip.CRC.CalculateDigest(bytes, 0, (uint)bytes.Length));
                        var idx = name.IndexOf('/');
                        if (idx < 0) break;
                        name = name.Substring(idx + 1);
                    }
                    foreach (var child in t.m_Children)
                        if (child.TryGet(out var ct))
                            Walk(ct, full);
                }
                Walk(root.m_Transform, "");
                rootHashes[root] = set;
            }

            Console.WriteLine($"Rig roots matching '{filter}': {matched.Count}");
            foreach (var root in matched)
            {
                var clips = ClipsForRoot(objects, root, rootHashes[root], clipsByBundle, clipsByHero, allClips, clipSource, clipRegex);
                Console.WriteLine($"  Root '{root.m_Name}'  [{Src(root.assetsFile)}]  clips={clips.Count}");
                if (mode == "list")
                {
                    foreach (var c in clips)
                        Console.WriteLine($"      clip: {c.m_Name}");
                }
            }

            if (mode == "list")
                return 0;
            var diagOnly = mode == "diag";
            if (diagOnly)
                matched = matched.Where(r => !IsOptimizedRig(r)).ToList();

            Directory.CreateDirectory(output);
            var failed = 0;
            foreach (var root in matched)
            {
                if (diagOnly && root != matched.First()) break;
                var clips = ClipsForRoot(objects, root, rootHashes[root], clipsByBundle, clipsByHero, allClips, clipSource, clipRegex);
                if (clips.Count == 0)
                {
                    Console.WriteLine($"Skipping '{root.m_Name}' [{Src(root.assetsFile)}] (no clips in source bundle)");
                    continue;
                }
                try
                {
                    var convert = new ModelConverter(root, ImageFormat.Png, clips, aliasRoots);
                    if (diagOnly)
                    {
                        Console.WriteLine($"[diag] root='{root.m_Name}' anims={convert.AnimationList.Count} rootFrame={convert.RootFrame?.Name} children={convert.RootFrame?.Count ?? -1}");
                        foreach (var anim in convert.AnimationList)
                        {
                            var resolvedCount = anim.TrackList.Count(t => t.Path != null && convert.RootFrame.FindFrameByPath(t.Path) != null);
                            Console.WriteLine($"[diag] clip '{anim.Name}': tracks={anim.TrackList.Count} resolved={resolvedCount} sampleRate={anim.SampleRate}");
                            foreach (var t in anim.TrackList.Take(6))
                            {
                                var ok = t.Path != null && convert.RootFrame.FindFrameByPath(t.Path) != null;
                                Console.WriteLine($"[diag]    path='{t.Path}' (match={ok}) keys T/R/S={t.Translations.Count}/{t.Rotations.Count}/{t.Scalings.Count}");
                            }
                        }
                        continue;
                    }
                    var modelName = root.m_Name.Length >= 260 ? "model" : root.m_Name;
                    var exportFullPath = Path.Combine(output, modelName + ".fbx");
                    ExportFbx(convert, exportFullPath);
                    Console.WriteLine($"Exported '{root.m_Name}' with {clips.Count} clip(s) -> {exportFullPath}");
                }
                catch (Exception ex)
                {
                    failed++;
                    Console.Error.WriteLine($"[ERROR] Root '{root.m_Name}': {ex.Message}");
                }
            }

            Console.WriteLine(failed == 0 ? "Done." : $"Finished with {failed} error(s).");
            return failed == 0 ? 0 : 2;
        }

        // Hero key = numeric id before the first underscore of the bundle name
        // ("528_damo_battle_base_low_raw_h" -> "528", "52810_damo_battle" -> "52810").
        // AOV ships several variants of the same clip (full data in the skin
        // bundle, stripped/empty ones elsewhere). Prefer the variant whose
        // muscle clip actually carries curve data.
        private static int ClipRichness(AnimationClip c)
        {
            var data = c.m_MuscleClip?.m_Clip?.data;
            if (data == null)
                return 0;
            var streamed = data.m_StreamedClip?.data?.Length ?? 0;
            var dense = data.m_DenseClip?.m_SampleArray?.Length ?? 0;
            var denseOpt = data.m_DenseClip?.m_SampleOptArray?.Length ?? 0;
            var constant = data.m_ConstantClip?.data?.Length ?? 0;
            var bindings = c.m_ClipBindingConstant?.genericBindings?.Count ?? 0;
            return streamed + dense + denseOpt + constant + bindings * 4;
        }

        private static AnimationClip PickRicher(AnimationClip a, AnimationClip b)
            => ClipRichness(a) >= ClipRichness(b) ? a : b;

        private static string HeroKey(string bundle)
        {
            if (string.IsNullOrEmpty(bundle))
                return "";
            var i = bundle.IndexOf('_');
            return i > 0 ? bundle.Substring(0, i) : bundle;
        }

        private static string BundleOf(GameObject root)
        {
            return _cabToBundle.TryGetValue(root.assetsFile.fileName, out var b) ? b : root.assetsFile.fileName;
        }

        private static bool SubtreeMatches(GameObject root, string filter)
        {
            var stack = new Stack<Transform>();
            stack.Push(root.m_Transform);
            while (stack.Count > 0)
            {
                var t = stack.Pop();
                if (t.m_GameObject.TryGet(out var go) &&
                    go.m_Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
                foreach (var child in t.m_Children)
                    if (child.TryGet(out var ct))
                        stack.Push(ct);
            }
            return false;
        }

        // Optimized-hierarchy rigs need an Avatar to deoptimize; AOV strips it,
        // so these can only be exported via their raw_h twin.
        private static bool IsOptimizedRig(GameObject root)
        {
            return root.m_Animator != null && !root.m_Animator.m_HasTransformHierarchy
                && !root.m_Animator.m_Avatar.TryGet(out _);
        }

        private static List<AnimationClip> ClipsForRoot(
            AssetStudio.Object[] objects,
            GameObject root,
            HashSet<uint> rootHashes,
            Dictionary<string, List<List<AnimationClip>>> clipsByBundle,
            Dictionary<string, List<List<AnimationClip>>> clipsByHero,
            List<List<AnimationClip>> allClips,
            string clipSource,
            System.Text.RegularExpressions.Regex clipRegex)
        {
            List<List<AnimationClip>> variants;
            if (clipSource == "hero")
            {
                // AOV splits a character across several bundles sharing the same
                // numeric prefix: rig in "*_base", clips in "*_base_low_raw_h".
                variants = clipsByHero.TryGetValue(HeroKey(BundleOf(root)), out var c) ? c : new List<List<AnimationClip>>();
            }
            else if (clipSource == "bundle")
            {
                // All clips from the same physical bundle as the rig root (a bundle can
                // contain several serialized files, clips may live in a different CAB
                // than the rig).
                variants = clipsByBundle.TryGetValue(BundleOf(root), out var c) ? c : new List<List<AnimationClip>>();
            }
            else
            {
                variants = allClips;
            }

            var clips = variants
                .Select(group => PickBestVariant(group, rootHashes))
                .Where(c => c != null)
                .ToList();

            if (clipRegex != null)
                clips = clips.Where(c => clipRegex.IsMatch(c.m_Name)).ToList();
            return clips;
        }

        // Same clip name ships in several variants bound to different rigs (bone
        // path hashes differ per skin/LOD). Pick the variant whose bindings most
        // fully resolve against THIS rig's path hashes; fall back to raw data size.
        private static AnimationClip PickBestVariant(List<AnimationClip> variants, HashSet<uint> rootHashes)
        {
            AnimationClip best = null;
            var bestScore = -1L;
            foreach (var clip in variants)
            {
                long resolved = 0;
                var seen = new HashSet<uint>();
                var bindings = clip.m_ClipBindingConstant?.genericBindings;
                if (bindings != null)
                {
                    foreach (var b in bindings)
                    {
                        if (!seen.Add(b.path))
                            continue;
                        if (rootHashes.Contains(b.path))
                            resolved++;
                    }
                }
                var score = resolved * 1_000_000_000L + ClipRichness(clip);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = clip;
                }
            }
            return best;
        }

        private static void ExportFbx(IImported convert, string exportPath)
        {
            var fbxSettings = new Fbx.Settings
            {
                BoneSize = 10,
                ScaleFactor = 1,
                ExportAllUvsAsDiffuseMaps = false,
                ExportAnimations = true,
            };
            ModelExporter.ExportFbx(exportPath, convert, fbxSettings);
        }
    }
}
