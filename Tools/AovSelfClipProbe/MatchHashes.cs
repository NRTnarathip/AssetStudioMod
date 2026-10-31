using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AssetStudio;

namespace AovSelfClipProbe
{
    // Matches AnimationClip binding path hashes against every rig hierarchy
    // (topmost transform of any SkinnedMeshRenderer owner).
    //
    // Usage: AovSelfClipProbe matchhashes <input folder> <bindings file>
    internal static class MatchHashes
    {
        public static int Run(string[] args)
        {
            var input = args.Length > 1 ? args[1] : @"F:\AOV-Hack\hero_work_test";
            var bindingsFile = args.Length > 2;

            var manager = new AssetsManager();
            manager.LoadFilesAndFolders(input);

            var objects = manager.AssetsFileList
                .SelectMany(f => f.Objects)
                .Where(o => o != null)
                .ToArray();

            // rig roots
            var roots = new Dictionary<GameObject, string>();
            foreach (var smr in objects.Where(o => o.type == ClassIDType.SkinnedMeshRenderer).Cast<SkinnedMeshRenderer>())
            {
                if (!smr.m_GameObject.TryGet(out var go) || go.m_Transform == null)
                    continue;
                var top = go.m_Transform;
                while (top.m_Father.TryGet(out var father))
                    top = father;
                if (top.m_GameObject.TryGet(out var rootGo) && !roots.ContainsKey(rootGo))
                    roots[rootGo] = rootGo.m_Name;
            }

            // hash -> (root, path) for every rig hierarchy: full paths plus every
            // suffix after a "/" (ModelConverter's CreateBonePathHash behavior)
            var hashToPath = new Dictionary<uint, List<string>>();
            void AddHash(uint crc, string desc)
            {
                if (!hashToPath.TryGetValue(crc, out var list))
                    hashToPath[crc] = list = new List<string>();
                list.Add(desc);
            }
            foreach (var kv in roots)
            {
                void Walk(Transform t, string path)
                {
                    var go2 = t.m_GameObject.TryGet(out var g) ? g.m_Name : "?";
                    var full = path.Length == 0 ? go2 : path + "/" + go2;
                    var name = full;
                    while (true)
                    {
                        var bytes = Encoding.UTF8.GetBytes(name);
                        var crc = SevenZip.CRC.CalculateDigest(bytes, 0, (uint)bytes.Length);
                        AddHash(crc, $"{kv.Value}/{full}" + (name == full ? "" : $" [suffix:{name}]"));
                        var idx = name.IndexOf('/');
                        if (idx < 0) break;
                        name = name.Substring(idx + 1);
                    }
                    foreach (var ch in t.m_Children)
                        if (ch.TryGet(out var ct))
                            Walk(ct, full);
                }
                Walk(kv.Key.m_Transform, "");
            }
            Console.WriteLine($"rigs: {roots.Count}, hashed paths: {hashToPath.Count}");

            if (!bindingsFile) return 0;
            var bpath = args[2];
            var hashes = new List<uint>();
            foreach (var line in File.ReadAllLines(bpath))
            {
                var i = line.IndexOf("path=0x", StringComparison.Ordinal);
                if (i < 0) continue;
                hashes.Add(Convert.ToUInt32(line.Substring(i + 7).Trim(), 16));
            }
            var uniq = hashes.Distinct().ToList();
            var matched = uniq.Where(h => hashToPath.ContainsKey(h)).ToList();
            Console.WriteLine($"binding hashes: {uniq.Count}, matched: {matched.Count}");
            foreach (var h in matched.Take(10))
                Console.WriteLine($"  0x{h:X8} -> {hashToPath[h][0]}");
            var missing = uniq.Where(h => !hashToPath.ContainsKey(h)).ToList();
            Console.WriteLine("missing hashes (first 20):");
            foreach (var h in missing.Take(20))
                Console.WriteLine($"  0x{h:X8}");
            return 0;
        }
    }
}
