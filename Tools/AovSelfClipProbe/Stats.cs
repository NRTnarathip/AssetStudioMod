using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using AssetStudio;

namespace AovSelfClipProbe
{
    // Scans every AnimationClip and prints the quantized-dense layout stats:
    // denseFrameCount * x == optArraySize, curveInfo groups, bit-depth mix...
    //
    // Usage: AovSelfClipProbe stats <input folder>
    internal static class Stats
    {
        public static int Run(string[] args)
        {
            var input = args.Length > 1 ? args[1] : @"F:\AOV-Hack\hero_work_test";

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
                .ToList();

            Console.WriteLine($"clip total: {clips.Count}");
            Console.WriteLine($"{"clip",-28}{"bundle",-42}{"sr",5} {"stFrm",6} {"stCv",6} {"dnFrm",6} {"dnCv",6} {"floatArr",9} {"optArr",9} {"ciCnt",6} {"const",7} {"bind",6} {"cap",6} {"opt/fr",7} types");

            foreach (var clip in clips.OrderBy(c => Src(c.assetsFile)).ThenBy(c => c.m_Name))
            {
                var data = clip.m_MuscleClip?.m_Clip?.data;
                if (data == null)
                    continue;
                var dense = data.m_DenseClip;
                var streamed = data.m_StreamedClip;
                if (dense == null || (dense.m_FrameCount == 0 && streamed.data.Length == 0))
                    continue;

                // Re-read raw dict for the extended fields
                ushort[] opt = null;
                object[] infos = null;
                try
                {
                    if (clip.ToType() is OrderedDictionary dict &&
                        dict["m_MuscleClip"] is OrderedDictionary muscle &&
                        muscle["m_Clip"] is OrderedDictionary mptr &&
                        mptr["data"] is OrderedDictionary mdata &&
                        mdata["m_DenseClip"] is OrderedDictionary denseDict)
                    {
                        if (denseDict["m_SampleOptArray"] is System.Collections.IList l)
                        {
                            opt = new ushort[l.Count];
                            for (int i = 0; i < l.Count; i++) opt[i] = Convert.ToUInt16(l[i]);
                        }
                        infos = denseDict["m_SampleCurveInfoArray"] as object[] ?? (denseDict["m_SampleCurveInfoArray"] as System.Collections.IList)?.Cast<object>().ToArray();
                    }
                }
                catch { }

                int cap = 0;
                if (clip.m_ClipBindingConstant != null)
                    foreach (var b in clip.m_ClipBindingConstant.genericBindings)
                        cap += b.typeID == ClassIDType.Transform
                            ? (b.attribute == 2 ? 4 : (b.attribute == 1 || b.attribute == 3 || b.attribute == 4) ? 3 : 1)
                            : 1;

                string types = "";
                if (infos != null)
                {
                    var byType = new SortedDictionary<int, int>();
                    foreach (var o in infos)
                    {
                        int t;
                        if (o is OrderedDictionary od)
                            t = Convert.ToInt32(od["m_CurveType"]);
                        else
                            t = Convert.ToInt32(o.GetType().GetField("m_CurveType")?.GetValue(o)
                                ?? o.GetType().GetProperty("m_CurveType")?.GetValue(o));
                        byType.TryGetValue(t, out var n);
                        byType[t] = n + 1;
                    }
                    types = string.Join(",", byType.Select(kv => $"t{kv.Key}x{kv.Value}"));
                }

                float optPerFrame = dense.m_FrameCount > 0 && opt != null ? (float)opt.Length / dense.m_FrameCount : 0;
                Console.WriteLine($"{clip.m_Name,-28}{Src(clip.assetsFile),-42}{clip.m_SampleRate,5:F0} {streamed.data.Length,6} {streamed.curveCount,6} {dense.m_FrameCount,6} {dense.m_CurveCount,6} {(dense.m_SampleArray?.Length ?? 0),9} {(opt?.Length ?? 0),9} {(infos?.Length ?? 0),6} {(data.m_ConstantClip?.data?.Length ?? 0),7} {(clip.m_ClipBindingConstant?.genericBindings?.Count ?? 0),6} {cap,6} {optPerFrame,7:F2} {types}");
            }
            return 0;
        }

        private static object ReflectGet(object o, string field)
        {
            return o?.GetType().GetField(field) != null
                ? o.GetType().GetField(field).GetValue(o)
                : o?.GetType().GetProperty(field)?.GetValue(o);
        }
    }
}
