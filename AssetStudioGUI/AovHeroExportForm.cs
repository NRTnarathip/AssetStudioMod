using AssetStudio;
using AssetStudio.FbxInterop;
using System;
using System.Collections.Generic;
using System.Drawing;
using WinColor = System.Drawing.Color;
using WinFont = System.Drawing.Font;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AssetStudioGUI
{
    // AOV/RoV hero FBX exporter.
    //
    // The game splits a character across several bundles (rig in
    // "*_battle_base", clips in "*_low_raw_h" of the hero or any of its skins)
    // and creates the character Animator at runtime, so no Animator asset can
    // be exported directly. This dialog instead loads every bundle of the
    // selected hero, finds the rig roots (topmost hierarchy owning a
    // SkinnedMeshRenderer), binds the richest duplicate of every clip, and
    // converts the chosen rig to FBX with all animations.
    public class AovHeroExportForm : Form
    {
        private static readonly Regex HeroKeyRegex = new Regex(@"^(\d+)_", RegexOptions.Compiled);

        private TextBox txtGameFolder;
        private TextBox txtHeroId;
        private ComboBox cboClipSource;
        private TextBox txtRigFilter;
        private TextBox txtClipRegex;
        private TextBox txtOutputFolder;
        private NumericUpDown numScaleFactor;
        private NumericUpDown numBoneSize;
        private CheckBox chkAnimations;
        private ListView lvRigs;
        private TextBox txtLog;
        private ProgressBar progressBar;
        private Button btnScan;
        private Button btnExport;

        private readonly List<GameObject> _rigRoots = new List<GameObject>();
        private AssetsManager _manager;

        public AovHeroExportForm()
        {
            InitializeUi();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
        }

        private void InitializeUi()
        {
            Text = "AOV Hero FBX Exporter";
            Font = new WinFont("Segoe UI", 9F);
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(760, 640);
            Size = new Size(880, 720);

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 5,
                Padding = new Padding(10),
            };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(root);

            root.Controls.Add(BuildOptionsPanel());
            root.Controls.Add(BuildButtonsPanel());

            var midSplit = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
            };
            lvRigs = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = false,
                HideSelection = false,
            };
            lvRigs.Columns.Add("Rig root", 230);
            lvRigs.Columns.Add("Source bundle", 330);
            lvRigs.Columns.Add("Clips", 70);
            lvRigs.SelectedIndexChanged += (s, e) =>
            {
                if (lvRigs.SelectedItems.Count > 0)
                    txtRigFilter.Text = lvRigs.SelectedItems[0].Text;
            };
            txtLog = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = SystemColors.Window,
                WordWrap = false,
            };
            midSplit.Panel1.Controls.Add(lvRigs);
            midSplit.Panel2.Controls.Add(txtLog);
            midSplit.SplitterDistance = 380;
            root.Controls.Add(midSplit);

            progressBar = new ProgressBar { Dock = DockStyle.Top, Height = 18 };
            root.Controls.Add(progressBar);

            var closePanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true,
            };
            var btnClose = new Button { Text = "Close", AutoSize = true };
            btnClose.Click += (s, e) => Close();
            closePanel.Controls.Add(btnClose);
            root.Controls.Add(closePanel);
        }

        private Control BuildOptionsPanel()
        {
            var grid = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 3,
                Padding = new Padding(0, 0, 0, 6),
            };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            txtGameFolder = AddBrowseRow(grid, 0, "Game folder",
                "F:\\AOV-Hack\\MyGame\\IQOO-Z10\\2022.V3\\assetbundle\\battle", false);
            txtOutputFolder = AddBrowseRow(grid, 1, "Output folder",
                "F:\\AOV-Hack\\hero_fbx", true);
            txtHeroId = AddTextRow(grid, 2, "Hero id (prefix)",
                "528", 120);
            txtRigFilter = AddTextRow(grid, 3, "Rig filter",
                "5281_DaMo_LOD1", 220);
            txtClipRegex = AddTextRow(grid, 4, "Clip regex (optional)",
                "", 220);

            var lblSource = new Label { Text = "Clip source", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 3, 3) };
            cboClipSource = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 120,
                Anchor = AnchorStyles.Left,
            };
            cboClipSource.Items.AddRange(new object[] { "all", "hero", "bundle" });
            cboClipSource.SelectedIndex = 0;
            var tip = new ToolTip();
            tip.SetToolTip(cboClipSource,
                "all = every loaded clip (richest duplicate wins; recommended)\n" +
                "hero = clips from bundles with the same numeric prefix\n" +
                "bundle = clips from the same physical bundle as the rig");
            grid.Controls.Add(lblSource, 0, 5);
            grid.Controls.Add(cboClipSource, 1, 5);

            var lblScale = new Label { Text = "FBX scale factor", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 3, 3) };
            numScaleFactor = new NumericUpDown { Minimum = 0.01M, Maximum = 1000M, DecimalPlaces = 2, Value = 1M, Width = 80 };
            var lblBone = new Label { Text = "Bone size", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 3, 3) };
            numBoneSize = new NumericUpDown { Minimum = 0, Maximum = 100, Value = 10, Width = 60 };
            chkAnimations = new CheckBox { Text = "Export animations", AutoSize = true, Checked = true, Anchor = AnchorStyles.Left };

            var optRow = new FlowLayoutPanel { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 3, 0, 0) };
            optRow.Controls.Add(lblScale);
            optRow.Controls.Add(numScaleFactor);
            optRow.Controls.Add(lblBone);
            optRow.Controls.Add(numBoneSize);
            optRow.Controls.Add(chkAnimations);
            grid.Controls.Add(optRow, 1, 6);
            grid.RowCount = 7;

            return grid;
        }

        private Control BuildButtonsPanel()
        {
            var panel = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                Padding = new Padding(0, 4, 0, 6),
            };
            btnScan = new Button { Text = "Scan rigs", AutoSize = true };
            btnScan.Click += async (s, e) => await ScanRigsAsync();
            btnExport = new Button { Text = "Export FBX", AutoSize = true };
            btnExport.Click += async (s, e) => await ExportSelectedAsync();
            panel.Controls.Add(btnScan);
            panel.Controls.Add(btnExport);
            return panel;
        }

        private TextBox AddBrowseRow(TableLayoutPanel grid, int row, string label, string defaultValue, bool save)
        {
            var lbl = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 3, 3) };
            var txt = new TextBox { Dock = DockStyle.Fill, Text = defaultValue, Anchor = AnchorStyles.Left };
            var btn = new Button { Text = "...", Width = 32 };
            btn.Click += (s, e) =>
            {
                if (save)
                {
                    using (var dlg = new FolderBrowserDialog { SelectedPath = txt.Text })
                    {
                        if (dlg.ShowDialog(this) == DialogResult.OK)
                            txt.Text = dlg.SelectedPath;
                    }
                }
                else
                {
                    using (var dlg = new FolderBrowserDialog { SelectedPath = txt.Text, ShowNewFolderButton = false })
                    {
                        if (dlg.ShowDialog(this) == DialogResult.OK)
                            txt.Text = dlg.SelectedPath;
                    }
                }
            };
            grid.Controls.Add(lbl, 0, row);
            grid.Controls.Add(txt, 1, row);
            grid.Controls.Add(btn, 2, row);
            if (grid.RowCount < row + 1)
                grid.RowCount = row + 1;
            return txt;
        }

        private TextBox AddTextRow(TableLayoutPanel grid, int row, string label, string defaultValue, int width)
        {
            var lbl = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 3, 3) };
            var txt = new TextBox { Text = defaultValue, Width = width, Anchor = AnchorStyles.Left };
            grid.Controls.Add(lbl, 0, row);
            grid.Controls.Add(txt, 1, row);
            if (grid.RowCount < row + 1)
                grid.RowCount = row + 1;
            return txt;
        }

        // Continuations after awaits only return to the UI thread when a
        // WinForms synchronization context exists (the main form's message
        // loop). Route every UI mutation through here so the dialog stays
        // correct even when driven headlessly.
        private void RunOnUi(Action action)
        {
            if (InvokeRequired)
                BeginInvoke(action);
            else
                action();
        }

        private void Log(string message)
        {
            if (InvokeRequired)
            {
                BeginInvoke((Action)(() => Log(message)));
                return;
            }
            txtLog.AppendText(message + Environment.NewLine);
        }

        private void SetBusy(bool busy)
        {
            if (InvokeRequired)
            {
                BeginInvoke((Action)(() => SetBusy(busy)));
                return;
            }
            btnScan.Enabled = !busy;
            btnExport.Enabled = !busy;
            UseWaitCursor = busy;
            if (!busy)
                progressBar.Value = 0;
        }

        private List<string> CollectBundlePaths()
        {
            var gameFolder = txtGameFolder.Text.Trim();
            if (!Directory.Exists(gameFolder))
                throw new DirectoryNotFoundException("Game folder not found: " + gameFolder);

            var heroId = txtHeroId.Text.Trim();
            if (string.IsNullOrEmpty(heroId))
                throw new ArgumentException("Hero id (prefix) is empty.");

            // prefix match (528*) also covers the hero's skins (5282_, 52810_, ...);
            // clips with full data usually live in the skin bundles
            return Directory.GetFiles(gameFolder, heroId + "*.assetbundle", SearchOption.AllDirectories)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private AssetStudio.Object[] CollectObjects()
        {
            return _manager.AssetsFileList
                .SelectMany(f => f.Objects)
                .Where(o => o != null)
                .ToArray();
        }

        private async Task ScanRigsAsync()
        {
            List<string> paths;
            try
            {
                paths = CollectBundlePaths();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Scan failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            Log($"Found {paths.Count} bundle(s) for hero '{txtHeroId.Text.Trim()}'. Loading...");
            SetBusy(true);
            progressBar.Style = ProgressBarStyle.Marquee;

            var manager = new AssetsManager();
            Exception loadError = null;
            await Task.Run(() =>
            {
                try
                {
                    manager.LoadFilesAndFolders(paths.ToArray());
                }
                catch (Exception ex)
                {
                    loadError = ex;
                }
            });

            RunOnUi(() =>
            {
                try
                {
                    progressBar.Style = ProgressBarStyle.Blocks;
                    SetBusy(false);
                    if (loadError != null)
                    {
                        MessageBox.Show(this, loadError.Message, "Scan failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }
                    _manager = manager;
                    var objects = CollectObjects();
                    Log($"Loaded {paths.Count} bundle(s), {objects.Length} objects.");
                    ScanRigsIntoList();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.ToString(), "Scan failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            });
        }

        private void ScanRigsIntoList()
        {
            var objects = CollectObjects();
            var roots = DiscoverRigRoots(objects);
            var cabToBundle = BuildCabToBundle();
            string BundleName(SerializedFile f) => cabToBundle.TryGetValue(f.fileName, out var b) ? b : f.fileName;
            var clipMap = BuildClipMap(objects, roots, cboClipSource.SelectedItem as string ?? "all", txtClipRegex.Text);

            _rigRoots.Clear();
            lvRigs.Items.Clear();

            var filter = txtRigFilter.Text.Trim();
            foreach (var root in roots.OrderBy(r => r.m_Name, StringComparer.OrdinalIgnoreCase))
            {
                var bundle = BundleName(root.assetsFile);
                var clipCount = clipMap.TryGetValue(root, out var c) ? c.Count : 0;
                if (filter.Length > 0 &&
                    root.m_Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                var item = new ListViewItem(root.m_Name);
                item.Tag = root;
                item.SubItems.Add(bundle);
                item.SubItems.Add(clipCount.ToString());
                if (clipCount == 0)
                    item.ForeColor = WinColor.Gray;
                lvRigs.Items.Add(item);
                _rigRoots.Add(root);
            }
            Log($"Scanned: {lvRigs.Items.Count} rig root(s) matching '{filter}'.");
        }

        private static List<GameObject> DiscoverRigRoots(AssetStudio.Object[] objects)
        {
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
            return roots;
        }

        private Dictionary<string, string> BuildCabToBundle()
        {
            var cabToBundle = new Dictionary<string, string>();
            foreach (var f in _manager.AssetsFileList)
            {
                var parent = f.originalPath ?? f.reader?.FullPath ?? f.fullName;
                if (!cabToBundle.ContainsKey(f.fileName))
                    cabToBundle[f.fileName] = Path.GetFileName(parent);
            }
            return cabToBundle;
        }

        // Keyed by the rig GameObject itself: rig root NAMES can repeat
        // across bundles (e.g. "DaMo_attack01b_spell04_HD" in several LOD
        // bundles), so name keys would throw on ToDictionary.
        private Dictionary<GameObject, List<AnimationClip>> BuildClipMap(AssetStudio.Object[] objects, List<GameObject> rigRoots, string clipSource, string clipRegex)
        {
            var allClips = objects.Where(o => o.type == ClassIDType.AnimationClip).Cast<AnimationClip>().ToList();
            Regex regex = null;
            if (!string.IsNullOrWhiteSpace(clipRegex))
                regex = new Regex(clipRegex, RegexOptions.IgnoreCase);

            var cabToBundle = BuildCabToBundle();
            string BundleName(SerializedFile f) => cabToBundle.TryGetValue(f.fileName, out var b) ? b : f.fileName;

            if (clipSource == "bundle")
            {
                var byBundle = allClips
                    .GroupBy(c => BundleName(c.assetsFile), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        g => g.Key,
                        g => Dedupe(g, regex).ToList(),
                        StringComparer.OrdinalIgnoreCase);
                return rigRoots.ToDictionary(
                    r => r,
                    r => byBundle.TryGetValue(BundleName(r.assetsFile), out var c) ? c : new List<AnimationClip>());
            }

            if (clipSource == "hero")
            {
                var byHero = allClips
                    .GroupBy(c => HeroKey(BundleName(c.assetsFile)), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        g => g.Key,
                        g => Dedupe(g, regex).ToList(),
                        StringComparer.OrdinalIgnoreCase);
                return rigRoots.ToDictionary(
                    r => r,
                    r => byHero.TryGetValue(HeroKey(BundleName(r.assetsFile)), out var c) ? c : new List<AnimationClip>());
            }

            var all = Dedupe(allClips, regex).ToList();
            return rigRoots.ToDictionary(r => r, r => all);
        }

        private static string HeroKey(string bundle)
        {
            var m = HeroKeyRegex.Match(bundle ?? "");
            return m.Success ? m.Groups[1].Value : bundle ?? "";
        }

        private static IEnumerable<AnimationClip> Dedupe(IEnumerable<AnimationClip> clips, Regex regex)
        {
            var best = new Dictionary<string, AnimationClip>(StringComparer.OrdinalIgnoreCase);
            foreach (var clip in clips)
            {
                if (regex != null && !regex.IsMatch(clip.m_Name))
                    continue;
                if (best.TryGetValue(clip.m_Name, out var cur))
                {
                    if (ClipRichness(clip) > ClipRichness(cur))
                        best[clip.m_Name] = clip;
                }
                else
                {
                    best[clip.m_Name] = clip;
                }
            }
            return best.Values;
        }

        // AOV ships several variants of the same clip (full data in skin
        // bundles, stripped/empty ones elsewhere). Prefer the variant whose
        // muscle clip actually carries curve data.
        private static int ClipRichness(AnimationClip c)
        {
            var data = c.m_MuscleClip?.m_Clip?.data;
            if (data == null)
                return 0;
            var streamed = data.m_StreamedClip?.data?.Length ?? 0;
            var dense = data.m_DenseClip?.m_SampleArray?.Length ?? 0;
            var constant = data.m_ConstantClip?.data?.Length ?? 0;
            var bindings = c.m_ClipBindingConstant?.genericBindings?.Count ?? 0;
            return streamed + dense + constant + bindings * 4;
        }

        private async Task ExportSelectedAsync()
        {
            if (_manager == null || _manager.AssetsFileList.Count == 0)
            {
                MessageBox.Show(this, "Load bundles first (Scan rigs).", "Not ready",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var rigRoots = lvRigs.SelectedItems
                .Cast<ListViewItem>()
                .Select(i => (GameObject)i.Tag)
                .ToList();

            if (rigRoots.Count == 0)
            {
                MessageBox.Show(this, "Select a rig in the list first.", "Not ready",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            await ExportRigsAsync(rigRoots);
        }

        // Exports the given rigs; split from the click handler so headless
        // drivers (smoke test) can run it without ListView selection quirks.
        internal async Task ExportRigsAsync(List<GameObject> rigRoots)
        {
            var output = txtOutputFolder.Text.Trim();
            try
            {
                Directory.CreateDirectory(output);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Invalid output folder", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            var clipSource = cboClipSource.SelectedItem as string ?? "all";
            Regex regex = null;
            if (!string.IsNullOrWhiteSpace(txtClipRegex.Text))
                regex = new Regex(txtClipRegex.Text, RegexOptions.IgnoreCase);

            var aliasRoots = _rigRoots.Select(r => r.m_Name).Distinct().ToList();
            var objects = CollectObjects();
            var fbxSettings = new Fbx.Settings
            {
                BoneSize = (int)numBoneSize.Value,
                ScaleFactor = (float)numScaleFactor.Value,
                ExportAllUvsAsDiffuseMaps = false,
                ExportAnimations = chkAnimations.Checked,
            };

            SetBusy(true);
            progressBar.Style = ProgressBarStyle.Blocks;
            var failed = 0;
            await Task.Run(() =>
            {
                    for (var i = 0; i < rigRoots.Count; i++)
                    {
                        var root = rigRoots[i];
                        try
                        {
                            var clips = SelectClipsForRoot(objects, root, clipSource, regex);
                            if (clips.Count == 0)
                            {
                                Log($"[skip] '{root.m_Name}' (no clips selected)");
                                continue;
                            }

                            Invoke((Action)(() => progressBar.Value = Math.Min(100, (int)((float)i / rigRoots.Count * 100))));
                            Log($"Converting '{root.m_Name}' with {clips.Count} clip(s)...");
                            var convert = new ModelConverter(root, ImageFormat.Png, clips, aliasRoots);
                            var exportPath = Path.Combine(output, SafeName(root.m_Name) + ".fbx");
                            ModelExporter.ExportFbx(exportPath, convert, fbxSettings);
                            Log($"[done] '{root.m_Name}' -> {exportPath}");
                        }
                        catch (Exception ex)
                        {
                            failed++;
                            Log($"[ERROR] '{root.m_Name}': {ex.Message}");
                        }
                    }
                });
            RunOnUi(() =>
            {
                SetBusy(false);
                Log(failed == 0
                    ? $"Export finished. Output: {output}"
                    : $"Export finished with {failed} error(s). Output: {output}");
            });
        }

        private List<AnimationClip> SelectClipsForRoot(AssetStudio.Object[] objects, GameObject root, string clipSource, Regex regex)
        {
            var allClips = objects.Where(o => o.type == ClassIDType.AnimationClip).Cast<AnimationClip>();
            var cabToBundle = BuildCabToBundle();
            string BundleName(SerializedFile f) => cabToBundle.TryGetValue(f.fileName, out var b) ? b : f.fileName;

            IEnumerable<AnimationClip> clips;
            if (clipSource == "bundle")
            {
                var rigBundle = BundleName(root.assetsFile);
                clips = allClips.Where(c => BundleName(c.assetsFile) == rigBundle);
            }
            else if (clipSource == "hero")
            {
                var key = HeroKey(BundleName(root.assetsFile));
                clips = allClips.Where(c => HeroKey(BundleName(c.assetsFile)) == key);
            }
            else
            {
                clips = allClips;
            }
            return Dedupe(clips, regex).ToList();
        }

        private static string SafeName(string name)
        {
            return name.Length >= 200 ? "model" : name;
        }
    }
}
