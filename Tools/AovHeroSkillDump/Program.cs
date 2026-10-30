using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AssetStudio;

namespace AovHeroSkillDump
{
    internal static class Program
    {
        private const int TargetWidth = 128;
        private const int TargetHeight = 128;
        private const int WrapClamp = 1;
        private const int FilterBilinear = 1;

        private static int Main(string[] args)
        {
            var inputFolder = args.Length > 0
                ? args[0]
                : @"F:\AOV-Hack\MyGame\IQOO-Z10\2022.V3\assetbundle\battle\hero";
            var outputFolder = args.Length > 1
                ? args[1]
                : @"F:\AOV-Hack\MyDumpHeroSkills";
            var bundlePattern = args.Length > 2 && !string.IsNullOrWhiteSpace(args[2])
                ? args[2]
                : "*.assetbundle";

            if (!Directory.Exists(inputFolder))
            {
                Console.Error.WriteLine("Input folder not found: " + inputFolder);
                return 1;
            }

            Directory.CreateDirectory(outputFolder);

            var bundles = Directory.GetFiles(inputFolder, bundlePattern, SearchOption.TopDirectoryOnly)
                .Where(x => x.EndsWith(".assetbundle", StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            Console.WriteLine("Input  : " + inputFolder);
            Console.WriteLine("Output : " + outputFolder);
            Console.WriteLine("Pattern: " + bundlePattern);
            Console.WriteLine("Bundles: " + bundles.Length);
            Console.WriteLine();

            var totalMatched = 0;
            var totalWritten = 0;
            var failedBundles = 0;
            var processed = 0;

            foreach (var bundlePath in bundles)
            {
                processed++;
                var bundleName = Path.GetFileNameWithoutExtension(bundlePath);

                try
                {
                    var manager = new AssetsManager();
                    manager.SetAssetFilter(ClassIDType.Texture2D);
                    manager.LoadFilesAndFolders(bundlePath);

                    var matched = new List<Texture2D>();
                    foreach (var file in manager.AssetsFileList)
                    {
                        if (!IsSamePath(file.originalPath, bundlePath))
                        {
                            continue;
                        }

                        foreach (var obj in file.Objects)
                        {
                            if (obj is not Texture2D texture)
                            {
                                continue;
                            }

                            var settings = texture.m_TextureSettings;
                            if (texture.m_Width != TargetWidth || texture.m_Height != TargetHeight)
                            {
                                continue;
                            }
                            if (texture.m_TextureFormat != TextureFormat.ETC_RGB4)
                            {
                                continue;
                            }
                            if (settings == null ||
                                settings.m_WrapMode != WrapClamp ||
                                settings.m_FilterMode != FilterBilinear)
                            {
                                continue;
                            }

                            matched.Add(texture);
                        }
                    }

                    totalMatched += matched.Count;
                    if (matched.Count > 0)
                    {
                        var bundleOutput = Path.Combine(outputFolder, bundleName);
                        Directory.CreateDirectory(bundleOutput);
                        var writtenInBundle = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                        foreach (var texture in matched)
                        {
                            var fileName = SafeFileName(texture.m_Name);
                            var exportPath = Path.Combine(bundleOutput, fileName + ".png");
                            if (!writtenInBundle.Add(exportPath))
                            {
                                exportPath = Path.Combine(bundleOutput, fileName + "_" + texture.m_PathID + ".png");
                                writtenInBundle.Add(exportPath);
                            }

                            try
                            {
                                using (var stream = texture.ConvertToStream(ImageFormat.Png, true))
                                {
                                    if (stream == null)
                                    {
                                        Console.WriteLine($"  [WARN] decode failed: {bundleName}/{texture.m_Name}");
                                        continue;
                                    }
                                    File.WriteAllBytes(exportPath, stream.ToArray());
                                    totalWritten++;
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"  [WARN] export failed: {bundleName}/{texture.m_Name}: {ex.Message}");
                            }
                        }

                        Console.WriteLine($"[{processed}/{bundles.Length}] {bundleName}: {matched.Count} image(s)");
                    }

                    manager.Clear();
                }
                catch (Exception ex)
                {
                    failedBundles++;
                    Console.WriteLine($"[{processed}/{bundles.Length}] {bundleName}: ERROR {ex.Message}");
                }

                if (processed % 50 == 0)
                {
                    Console.WriteLine($"... progress {processed}/{bundles.Length}");
                }
            }

            Console.WriteLine();
            Console.WriteLine("Done.");
            Console.WriteLine("Matched textures : " + totalMatched);
            Console.WriteLine("Written PNGs     : " + totalWritten);
            Console.WriteLine("Failed bundles   : " + failedBundles);
            return 0;
        }

        private static bool IsSamePath(string left, string right)
        {
            if (string.IsNullOrEmpty(left))
            {
                return false;
            }

            try
            {
                return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
            }
        }

        private static string SafeFileName(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "texture";
            }

            var invalid = Path.GetInvalidFileNameChars();
            var chars = value.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
            var result = new string(chars).Trim().TrimEnd('.');
            return string.IsNullOrEmpty(result) ? "texture" : result;
        }
    }
}
