using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Text;
using AssetStudio;

namespace AovSelfClipProbe
{
    // Dumps the Tencent-specific AnimationClip fields (m_SelfClip / m_CompressedBindings /
    // m_CompressByDenseAndConstCurves ...) that stock AssetStudio ignores.
    //
    // Usage: AovSelfClipProbe <input folder> [clip name] [bundle filter]
    internal static class Program
    {
        private static Dictionary<string, string> _cabToBundle = new Dictionary<string, string>();

        private static int Main(string[] args)
        {
            var input = args.Length > 0 ? args[0] : @"F:\AOV-Hack\hero_work_test";
            if (input == "stats")
            {
                return Stats.Run(args);
            }
            if (input == "dump")
            {
                return Dump.Run(args);
            }
            if (input == "restpose")
            {
                return RestPose.Run(args);
            }
            if (input == "matchhashes")
            {
                return MatchHashes.Run(args);
            }
            var clipName = args.Length > 1 ? args[1] : "Atk2";
            var bundleFilter = args.Length > 2 ? args[2] : "52810_damo_battle_low_raw_h";

            var manager = new AssetsManager();
            manager.LoadFilesAndFolders(input);

            var cabToBundle = new Dictionary<string, string>();
            foreach (var f in manager.AssetsFileList)
            {
                var parent = f.originalPath ?? f.reader?.FullPath ?? f.fullName;
                if (!cabToBundle.ContainsKey(f.fileName))
                    cabToBundle[f.fileName] = Path.GetFileName(parent);
            }
            _cabToBundle = cabToBundle;
            string Src(SerializedFile f) => cabToBundle.TryGetValue(f.fileName, out var n) ? n : f.fileName;

            var clips = manager.AssetsFileList
                .SelectMany(f => f.Objects)
                .Where(o => o is AnimationClip c && c.m_Name == clipName)
                .Cast<AnimationClip>()
                .Where(c => Src(c.assetsFile).Contains(bundleFilter))
                .ToList();

            Console.WriteLine($"probe: {clips.Count} clip(s) named '{clipName}' in bundles like '{bundleFilter}'");

            foreach (var clip in clips)
            {
                Console.WriteLine();
                Console.WriteLine($"=== clip '{clip.m_Name}'  [{Src(clip.assetsFile)}]  CAB={clip.assetsFile.fileName} ===");

                var tt = clip.serializedType?.m_Type;
                if (tt?.m_Nodes != null)
                {
                    Console.WriteLine("--- type tree (levels 0-2, plus full subtree of m_SelfClip / m_CompressedBindings) ---");
                    int inSubtree = 0;
                    foreach (var n in tt.m_Nodes)
                    {
                        bool interesting = n.m_Name == "m_SelfClip" || n.m_Name == "m_CompressedBindings"
                            || n.m_Name == "m_SelfClipSize" || n.m_Name == "m_CompressByDenseAndConstCurves"
                            || n.m_Name == "m_DontCompressLargeErrorCurves" || n.m_Name == "m_CompressWithRotationQuantize";
                        if (n.m_Level <= 2)
                        {
                            Console.WriteLine($"  lvl{n.m_Level} {n.m_Type} {n.m_Name} (size={n.m_ByteSize} flags=0x{n.m_MetaFlag:X})");
                            if (interesting && n.m_Level == 1)
                                inSubtree = 1;
                            else if (n.m_Level == 1)
                                inSubtree = 0;
                        }
                        else if (inSubtree > 0 || interesting)
                        {
                            Console.WriteLine($"    .lvl{n.m_Level} {n.m_Type} {n.m_Name} (size={n.m_ByteSize} flags=0x{n.m_MetaFlag:X})");
                        }
                    }
                }

                OrderedDictionary dict;
                try
                {
                    dict = clip.ToType();
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[!] ToType failed: {e.Message}");
                    continue;
                }
                if (dict == null)
                {
                    Console.WriteLine("[!] no type tree");
                    continue;
                }

                Console.WriteLine("--- parsed values ---");
                foreach (string key in new[] { "m_SampleRate", "m_CompressByDenseAndConstCurves", "m_DontCompressLargeErrorCurves", "m_CompressWithRotationQuantize", "m_SelfClipSize" })
                    Console.WriteLine($"  {key} = {dict[key] ?? "<absent>"}");

                // The muscle clip's DenseClip in Tencent builds carries quantized
                // samples in extra fields stock AssetStudio drops; dump them raw.
                if (dict["m_MuscleClip"] is OrderedDictionary muscle &&
                    muscle["m_Clip"] is OrderedDictionary mptr &&
                    mptr["data"] is OrderedDictionary mdata &&
                    mdata["m_DenseClip"] is OrderedDictionary denseDict)
                {
                    Console.WriteLine("--- m_MuscleClip.m_Clip.data.m_DenseClip (raw dict) ---");
                    DumpDict(denseDict, "  ", 6);
                    if (mdata["m_StreamedClip"] is OrderedDictionary streamedDict)
                    {
                        Console.WriteLine("--- m_MuscleClip.m_Clip.data.m_StreamedClip (raw dict) ---");
                        DumpDict(streamedDict, "  ", 6);
                    }
                }

                if (dict["m_SelfClip"] is byte[] selfClip)
                {
                    Console.WriteLine($"  m_SelfClip: {selfClip.Length} bytes");
                    Console.WriteLine("  m_SelfClip head (hex):");
                    Console.WriteLine("    " + BitConverter.ToString(selfClip, 0, Math.Min(256, selfClip.Length)).Replace("-", " "));
                    File.WriteAllBytes(Path.Combine(input, $"{clip.m_Name}_{Src(clip.assetsFile)}.selfclip.bin"), selfClip);
                    Console.WriteLine($"    -> saved to {clip.m_Name}_{Src(clip.assetsFile)}.selfclip.bin");
                }
                else
                {
                    Console.WriteLine($"  m_SelfClip = {dict["m_SelfClip"]?.GetType().Name ?? "<absent>"}");
                    if (dict["m_SelfClip"] is OrderedDictionary sd)
                        DumpDict(sd, "    ");
                }

                Console.WriteLine("--- m_CompressedBindings ---");
                if (dict["m_CompressedBindings"] is OrderedDictionary cb)
                    DumpDict(cb, "  ", 12);
                else
                    Console.WriteLine($"  = {dict["m_CompressedBindings"]?.GetType().Name ?? "<absent>"}");
            }

            return 0;
        }

        private static void DumpDict(OrderedDictionary dict, string indent, int maxList = 8)
        {
            foreach (DictionaryEntry e in dict)
            {
                switch (e.Value)
                {
                    case byte[] b:
                        Console.WriteLine($"{indent}{e.Key}: byte[{b.Length}] {BitConverter.ToString(b, 0, Math.Min(32, b.Length)).Replace("-", " ")}{(b.Length > 32 ? " ..." : "")}");
                        break;
                    case IList list when list.Count > maxList:
                        Console.WriteLine($"{indent}{e.Key}: {list.GetType().Name}[{list.Count}] first=({string.Join(", ", list.Cast<object>().Take(4))})");
                        break;
                    case IList list:
                        Console.WriteLine($"{indent}{e.Key}: {list.GetType().Name}[{list.Count}] ({string.Join(", ", list.Cast<object>().Take(maxList))})");
                        break;
                    case OrderedDictionary d:
                        Console.WriteLine($"{indent}{e.Key}:");
                        DumpDict(d, indent + "  ", maxList);
                        break;
                    default:
                        Console.WriteLine($"{indent}{e.Key}: {e.Value ?? "null"}");
                        break;
                }
            }
        }
    }
}
