using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AssetStudio;

namespace AovSelfClipProbe
{
    // Dumps the rig hierarchy rest pose: path, CRC32 hash (same as ModelConverter),
    // localRotation, localPosition — for validating decoded animation values.
    //
    // Usage: AovSelfClipProbe restpose <input folder> <rig name>
    internal static class RestPose
    {
        public static int Run(string[] args)
        {
            var input = args.Length > 1 ? args[1] : @"F:\AOV-Hack\hero_work_test";
            var rigName = args.Length > 2 ? args[2] : "5281_DaMo_LOD1";

            var manager = new AssetsManager();
            manager.LoadFilesAndFolders(input);

            var objects = manager.AssetsFileList
                .SelectMany(f => f.Objects)
                .Where(o => o != null)
                .ToArray();

            GameObject root = null;
            foreach (var go in objects.Where(o => o.type == ClassIDType.GameObject).Cast<GameObject>())
            {
                if (go.m_Name == rigName) { root = go; break; }
            }
            if (root == null)
            {
                Console.WriteLine($"rig '{rigName}' not found");
                return 1;
            }

            var lines = new List<string>();
            void Walk(Transform t, string path)
            {
                var go2 = t.m_GameObject.TryGet(out var g) ? g.m_Name : "?";
                var full = path.Length == 0 ? go2 : path + "/" + go2;
                // ModelConverter's CreateBonePathHash registers the full path plus
                // every suffix after a "/", so clips baked for a differently named
                // rig root still resolve. Emit the same set.
                var name = full;
                var emitted = new HashSet<string>();
                while (true)
                {
                    var bytes = Encoding.UTF8.GetBytes(name);
                    var crc = SevenZip.CRC.CalculateDigest(bytes, 0, (uint)bytes.Length);
                    if (emitted.Add(name))
                    {
                        var q = t.m_LocalRotation;
                        var p = t.m_LocalPosition;
                        lines.Add($"{full}\t0x{crc:X8}\t{name}\t{q.X:R}\t{q.Y:R}\t{q.Z:R}\t{q.W:R}\t{p.X:R}\t{p.Y:R}\t{p.Z:R}");
                    }
                    var idx = name.IndexOf('/');
                    if (idx < 0) break;
                    name = name.Substring(idx + 1);
                }
                foreach (var ch in t.m_Children)
                    if (ch.TryGet(out var ct))
                        Walk(ct, full);
            }
            Walk(root.m_Transform, "");

            var outPath = Path.Combine(input, $"{rigName}.restpose.txt");
            File.WriteAllLines(outPath, lines);
            Console.WriteLine($"wrote {lines.Count} bones -> {outPath}");
            return 0;
        }
    }
}
