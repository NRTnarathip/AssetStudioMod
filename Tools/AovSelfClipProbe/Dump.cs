using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using AssetStudio;

namespace AovSelfClipProbe
{
    // Dumps DenseCurveInfo (min/scale/type) rows and raw opt-array samples for one
    // clip variant, so the quantized dense layout can be reverse engineered.
    //
    // Usage: AovSelfClipProbe dump <input folder> <clip name> <bundle filter> [variantIndex]
    internal static class Dump
    {
        public static int Run(string[] args)
        {
            var input = args.Length > 1 ? args[1] : @"F:\AOV-Hack\hero_work_test";
            var clipName = args.Length > 2 ? args[2] : "Atk2";
            var bundleFilter = args.Length > 3 ? args[3] : "52810_damo_battle_low_raw_h";
            var variant = args.Length > 4 ? int.Parse(args[4]) : 0;

            var manager = new AssetsManager();
            manager.LoadFilesAndFolders(input);

            var cabToBundle = new Dictionary<string, string>();
            foreach (var f in manager.AssetsFileList)
            {
                var parent = f.originalPath ?? f.reader?.FullPath ?? f.fullName;
                if (!cabToBundle.ContainsKey(f.fileName))
                    cabToBundle[f.fileName] = Path.GetFileName(parent);
            }
            string Src(SerializedFile f) => cabToBundle.TryGetValue(f.fileName, out var n) ? n : f.fileName;

            var clips = manager.AssetsFileList
                .SelectMany(f => f.Objects)
                .OfType<AnimationClip>()
                .Where(c => c.m_Name == clipName && Src(c.assetsFile).Contains(bundleFilter))
                .ToList();

            var clip = clips[Math.Min(variant, clips.Count - 1)];
            Console.WriteLine($"dump: '{clip.m_Name}' [{Src(clip.assetsFile)}] variant {variant}/{clips.Count}");

            var dict = clip.ToType();
            var muscle = dict["m_MuscleClip"] as OrderedDictionary;
            var mdata = (muscle["m_Clip"] as OrderedDictionary)["data"] as OrderedDictionary;
            var denseDict = mdata["m_DenseClip"] as OrderedDictionary;

            int frames = Convert.ToInt32(denseDict["m_FrameCount"]);
            uint curveCount = Convert.ToUInt32(denseDict["m_CurveCount"]);
            float sampleRate = Convert.ToSingle(denseDict["m_SampleRate"]);
            float beginTime = Convert.ToSingle(denseDict["m_BeginTime"]);
            Console.WriteLine($"dense: frames={frames} curveCount={curveCount} sampleRate={sampleRate} beginTime={beginTime}");

            var infos = (denseDict["m_SampleCurveInfoArray"] as IList).Cast<object>().ToList();
            var opt = (denseDict["m_SampleOptArray"] as IList).Cast<object>().Select(Convert.ToUInt32).ToArray();
            var floatArr = (denseDict["m_SampleArray"] as IList).Cast<object>().Select(Convert.ToSingle).ToArray();
            Console.WriteLine($"optArray={opt.Length} uint16, floatArray={floatArr.Length}, infos={infos.Count}");
            Console.WriteLine();

            Console.WriteLine("--- DenseCurveInfo rows ---");
            int totalComps = 0;
            for (int i = 0; i < infos.Count; i++)
            {
                var od = (OrderedDictionary)infos[i];
                float min = Convert.ToSingle(od["m_CurveMinimum"]);
                float scale = Convert.ToSingle(od["m_CurveScale"]);
                int type = Convert.ToInt32(od["m_CurveType"]);
                int comps = (type & 0xFF) == 1 ? 4 : (type & 0xFF) == 2 ? 3 : (type & 0xFF) == 3 ? 1 : 0;
                totalComps += comps;
                Console.WriteLine($"  [{i,3}] type={type} (kind={type & 0xFF}, hi={type >> 8}) comps={comps} min={min:R} scale={scale:R}");
            }
            Console.WriteLine($"  total components from infos: {totalComps} (dense curveCount={curveCount})");
            Console.WriteLine();

            Console.WriteLine("--- opt array head (dec) ---");
            for (int i = 0; i < Math.Min(96, opt.Length); i += 8)
                Console.WriteLine($"  [{i,5}] " + string.Join(" ", opt.Skip(i).Take(8).Select(v => $"{v,6}")));

            // Save raw files for offline analysis
            var bytes = new byte[opt.Length * 2];
            for (int i = 0; i < opt.Length; i++)
            {
                bytes[i * 2] = (byte)(opt[i] & 0xFF);
                bytes[i * 2 + 1] = (byte)(opt[i] >> 8);
            }
            var outPath = Path.Combine(input, $"{clip.m_Name}_{variant}.optarray.bin");
            File.WriteAllBytes(outPath, bytes);
            Console.WriteLine($"saved: {outPath}");

            var infoLines = new List<string>();
            for (int i = 0; i < infos.Count; i++)
            {
                var od = (OrderedDictionary)infos[i];
                infoLines.Add($"{Convert.ToSingle(od["m_CurveMinimum"]):R}\t{Convert.ToSingle(od["m_CurveScale"]):R}\t{Convert.ToInt32(od["m_CurveType"])}");
            }
            File.WriteAllLines(Path.Combine(input, $"{clip.m_Name}_{variant}.curveinfo.txt"), infoLines);

            // Bindings: map dense curve indices to transform paths for later sanity checks
            var bc = clip.m_ClipBindingConstant;
            if (bc != null)
            {
                var lines = new List<string>();
                int idx = 0;
                foreach (var b in bc.genericBindings)
                {
                    int w = b.typeID == ClassIDType.Transform
                        ? (b.attribute == 2 ? 4 : (b.attribute == 1 || b.attribute == 3 || b.attribute == 4) ? 3 : 1)
                        : 1;
                    lines.Add($"{idx}\t{w}\t{b.typeID}\tattr={b.attribute}\tpath=0x{b.path:X8}");
                    idx += w;
                }
                File.WriteAllLines(Path.Combine(input, $"{clip.m_Name}_{variant}.bindings.txt"), lines);
                Console.WriteLine($"saved bindings ({bc.genericBindings.Count} rows, {idx} curve slots)");
            }

            // Streamed clip decoded frames: ground-truth per-curve values.
            var muscleClip = clip.m_MuscleClip;
            var cd = muscleClip?.m_Clip?.data;
            if (cd != null)
            {
                var streamedPath = Path.Combine(input, $"{clip.m_Name}_{variant}.streamed.txt");
                var sLines = new List<string>();
                var sFrames = cd.m_StreamedClip.ReadData();
                foreach (var fr in sFrames)
                {
                    foreach (var k in fr.keyList)
                        sLines.Add($"{k.index.ToString()}\t{fr.time.ToString("R")}\t{k.value.ToString("R")}");
                }
                File.WriteAllLines(streamedPath, sLines);
                Console.WriteLine($"saved streamed keys ({sLines.Count} rows, {sFrames.Count} frames)");
                // Per-curve layout: how many curve slots streamed/dense/constant occupy.
                var layoutLines = new List<string>
                {
                    $"{cd.m_StreamedClip.curveCount}\t{cd.m_DenseClip.m_CurveCount}\t{(cd.m_ConstantClip?.data?.Length ?? 0)}"
                };
                File.WriteAllLines(Path.Combine(input, $"{clip.m_Name}_{variant}.layout.txt"), layoutLines);

                var constPath = Path.Combine(input, $"{clip.m_Name}_{variant}.constant.txt");
                if (cd.m_ConstantClip?.data != null)
                    File.WriteAllLines(constPath, cd.m_ConstantClip.data.Select(v => v.ToString("R")));
                var musclePath = Path.Combine(input, $"{clip.m_Name}_{variant}.muscle.txt");
                File.WriteAllLines(musclePath, new[] { $"{muscleClip.m_StartTime.ToString("R")}\t{muscleClip.m_StopTime.ToString("R")}\t{clip.m_SampleRate.ToString("R")}" });
            }
            return 0;
        }
    }
}
