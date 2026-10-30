using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace AovGuiSmokeTest
{
    // Headless smoke test for AssetStudioGUI.AovHeroExportForm:
    // drives the real form through scan + export via reflection and a
    // DoEvents message pump (Invoke from worker threads must be able to run).
    internal static class Program
    {
        private const string GameFolder = @"F:\AOV-Hack\gui_quick";
        private const string Output = @"F:\AOV-Hack\gui_smoke_out";
        private const string RigName = "5281_DaMo_LOD1";

        private static int Main(string[] args)
        {
            var t = new Thread(Run);
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
            return ExitCode;
        }

        private static int ExitCode = 0;

        private static void Run()
        {
            try
            {
                Directory.CreateDirectory(Output);
                var form = new AssetStudioGUI.AovHeroExportForm();
                var _ = form.Handle; // force handle creation so Invoke works

                var flags = BindingFlags.NonPublic | BindingFlags.Instance;
                var fGame = (TextBox)form.GetType().GetField("txtGameFolder", flags).GetValue(form);
                var fHero = (TextBox)form.GetType().GetField("txtHeroId", flags).GetValue(form);
                var fOut = (TextBox)form.GetType().GetField("txtOutputFolder", flags).GetValue(form);
                var fRig = (TextBox)form.GetType().GetField("txtRigFilter", flags).GetValue(form);
                var fSrc = (System.Windows.Forms.ComboBox)form.GetType().GetField("cboClipSource", flags).GetValue(form);
                var lv = (ListView)form.GetType().GetField("lvRigs", flags).GetValue(form);

                fGame.Text = GameFolder;
                fHero.Text = "528";
                fOut.Text = Output;
                fRig.Text = RigName;
                fSrc.SelectedIndex = 0; // all

                var gameDir = GameFolder;
                var bundleCount = Directory.GetFiles(gameDir, "528*.assetbundle", System.IO.SearchOption.AllDirectories).Length;
                Console.WriteLine($"[smoke] bundles matching 528*: {bundleCount}");
                InvokeAsync(form, flags, "ScanRigsAsync");
                Console.WriteLine($"[smoke] rigs listed: {lv.Items.Count}");
                foreach (ListViewItem it in lv.Items)
                    Console.WriteLine("[smoke]   rig: " + it.Text + "  clips=" + it.SubItems[2].Text);
                var lvLog = (TextBox)form.GetType().GetField("txtLog", flags).GetValue(form);
                Console.WriteLine("[smoke] form log tail: " + lvLog.Text.Replace("\r\n", " | "));
                if (lv.Items.Count == 0)
                    Fail("scan produced no rigs");

                var row = lv.Items.Cast<ListViewItem>().FirstOrDefault(i => i.Text == RigName)
                          ?? throw new Exception($"rig '{RigName}' not in list");
                var rigRootsField = (System.Collections.Generic.List<AssetStudio.GameObject>)form
                    .GetType().GetField("_rigRoots", flags).GetValue(form);
                var rigObj = rigRootsField.FirstOrDefault(r => r.m_Name == RigName)
                          ?? throw new Exception("rig object not found in _rigRoots");

                var before = Directory.GetFiles(Output, "*.fbx").Length;
                InvokeAsync(form, flags, "ExportRigsAsync", new object[] { new System.Collections.Generic.List<AssetStudio.GameObject> { rigObj } });
                Console.WriteLine("[smoke] form log tail after export: " + lvLog.Text.Replace("\r\n", " | "));
                var after = Directory.GetFiles(Output, "*.fbx");

                Console.WriteLine($"[smoke] fbx files before={before} after={after.Length}");
                foreach (var f in after)
                    Console.WriteLine($"[smoke]   {Path.GetFileName(f)}: {new FileInfo(f).Length} bytes");

                if (!after.Any(f => Path.GetFileName(f).StartsWith(RigName)))
                    Fail($"'{RigName}.fbx' was not produced");
                if (after.Max(f => new FileInfo(f).Length) < 100_000)
                    Fail("exported FBX suspiciously small (<100KB) — animations likely missing");

                Console.WriteLine("[smoke] PASS");
                ExitCode = 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[smoke] FAIL: " + ex);
                ExitCode = 1;
            }
        }

        private static void Fail(string reason)
        {
            throw new Exception("smoke test failed: " + reason);
        }

        private static void InvokeAsync(Form form, BindingFlags flags, string methodName, params object[] args)
        {
            var mi = form.GetType().GetMethod(methodName, flags);
            var task = (System.Threading.Tasks.Task)mi.Invoke(form, args);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!task.IsCompleted)
            {
                Application.DoEvents();
                Thread.Sleep(25);
                if (sw.ElapsedMilliseconds > 10 * 60 * 1000)
                    throw new TimeoutException(methodName + " did not finish in 10 minutes");
            }
            task.GetAwaiter().GetResult();
            // the form queues UI updates via BeginInvoke from worker threads;
            // drain them so logs written near completion become visible
            for (var i = 0; i < 40; i++)
            {
                Application.DoEvents();
                Thread.Sleep(25);
            }
        }
    }
}
