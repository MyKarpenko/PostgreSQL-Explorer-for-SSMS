using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using SsmsPgExplorer.UI;

namespace SsmsPgExplorer.Plans
{
    /// <summary>
    /// "Execution plan" tab: statement picker with "Query N: cost relative to the batch", graph + Properties,
    /// and Text / JSON views. Copy JSON / Open in PEV2 (local pev2.html, nothing uploaded).
    /// </summary>
    internal sealed class PlanView : UserControl
    {
        private readonly ComboBox _statements = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        private readonly Label _summary = new Label { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6, 4, 6, 4) };
        private readonly TabControl _views = new TabControl { Dock = DockStyle.Fill, Alignment = TabAlignment.Top };
        private readonly PlanGraph _graph = new PlanGraph { Dock = DockStyle.Fill };
        private readonly ListView _props = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable, ShowItemToolTips = true };
        private readonly Label _propsTitle = new Label { Dock = DockStyle.Top, Height = 22, Padding = new Padding(4, 4, 4, 0), Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold), Text = "Properties" };
        private readonly TextBox _text = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new Font("Consolas", 9.5f), BorderStyle = BorderStyle.None };
        private readonly TextBox _json = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new Font("Consolas", 9.5f), BorderStyle = BorderStyle.None };
        private List<StatementPlan> _plans = new List<StatementPlan>();

        private static string Pev2PathFile => Path.Combine(Model.ServerStore.Folder, "pev2-path.txt");

        public PlanView()
        {
            var top = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 7, Padding = new Padding(4, 4, 4, 0) };
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (var i = 0; i < 6; i++) top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            Button B(string text, string tip, EventHandler click)
            {
                var b = new Button { Text = text, AutoSize = true, Margin = new Padding(3, 0, 0, 0) };
                new ToolTip().SetToolTip(b, tip);
                b.Click += click;
                return b;
            }
            top.Controls.Add(_statements, 0, 0);
            top.Controls.Add(B("-", "Zoom out (Ctrl+wheel)", (s, e) => _graph.Zoom /= 1.15f), 1, 0);
            top.Controls.Add(B("+", "Zoom in (Ctrl+wheel)", (s, e) => _graph.Zoom *= 1.15f), 2, 0);
            top.Controls.Add(B("Fit", "Zoom to fit", (s, e) => _graph.ZoomToFit()), 3, 0);
            top.Controls.Add(B("100%", "Actual size", (s, e) => _graph.Zoom = 1f), 4, 0);
            top.Controls.Add(B("Copy JSON", "Copy this statement's EXPLAIN JSON", (s, e) => CopyJson()), 5, 0);
            top.Controls.Add(B("Open in PEV2...", "Open in a local pev2.html (runs in your browser; nothing is uploaded)", (s, e) => OpenInPev2()), 6, 0);
            _statements.SelectedIndexChanged += (s, e) => ShowStatement(_statements.SelectedIndex);

            _props.Columns.Add("Property", 170);
            _props.Columns.Add("Value", 420);
            var propsPanel = new Panel { Dock = DockStyle.Fill };
            propsPanel.Controls.Add(_props);
            propsPanel.Controls.Add(_propsTitle);

            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterWidth = 5, FixedPanel = FixedPanel.Panel2 };
            split.Panel1.Controls.Add(_graph);
            split.Panel2.Controls.Add(propsPanel);
            Load += (s, e) => { try { split.SplitterDistance = Math.Max(200, split.Width - (int)(380 * DeviceDpi / 96f)); } catch { } };

            var graphTab = new TabPage("Graph");
            graphTab.Controls.Add(split);
            var textTab = new TabPage("Text output");
            textTab.Controls.Add(_text);
            var jsonTab = new TabPage("JSON (original)");
            jsonTab.Controls.Add(_json);
            _views.TabPages.AddRange(new[] { graphTab, textTab, jsonTab });

            Controls.Add(_views);
            Controls.Add(_summary);
            Controls.Add(top);

            _graph.NodeSelected += (s, n) => ShowProperties(n);
            Theme.Attach(this);
        }

        public void SetPlans(IList<StatementPlan> plans)
        {
            _plans = plans.ToList();
            var totalCost = _plans.Where(p => p.Root != null).Sum(p => p.RootCost);
            foreach (var p in _plans) p.BatchCostPercent = totalCost > 0 && p.Root != null ? p.RootCost / totalCost * 100 : 0;

            _statements.BeginUpdate();
            _statements.Items.Clear();
            foreach (var p in _plans)
            {
                var label = p.Root != null
                    ? $"Query {p.Index}: Query cost (relative to the batch): {p.BatchCostPercent:0} %    {Query.SqlSplitter.OneLine(p.Sql, 90)}"
                    : $"Query {p.Index}: {(p.Error != null ? "ERROR" : "skipped")}    {Query.SqlSplitter.OneLine(p.Sql, 90)}";
                _statements.Items.Add(label);
            }
            _statements.EndUpdate();
            var first = _plans.FindIndex(p => p.Root != null);
            if (_statements.Items.Count > 0) _statements.SelectedIndex = Math.Max(0, first);
            else ShowStatement(-1);
        }

        private void ShowStatement(int i)
        {
            var p = i >= 0 && i < _plans.Count ? _plans[i] : null;
            _graph.Plan = p?.Root != null ? p : null;
            if (p == null) { _summary.Text = "No plan."; _text.Text = _json.Text = ""; return; }

            var kind = p.IsActual ? "Actual execution plan" : "Estimated execution plan";
            if (p.Root == null)
            {
                _summary.Text = $"Query {p.Index}: {(p.Error != null ? "EXPLAIN failed: " + p.Error : p.Skipped)}";
                _summary.ForeColor = p.Error != null ? Color.Firebrick : Theme.GrayText;
                _text.Text = p.Sql;
                _json.Text = "";
                _props.Items.Clear();
                return;
            }
            var parts = new List<string> { kind, $"total cost {p.RootCost:N2}" };
            if (p.PlanningTimeMs.HasValue) parts.Add($"planning {PlanGraph.FormatMs(p.PlanningTimeMs.Value)}");
            if (p.ExecutionTimeMs.HasValue) parts.Add($"execution {PlanGraph.FormatMs(p.ExecutionTimeMs.Value)}");
            var warnings = p.Root.SelfAndDescendants().Sum(n => n.NeverExecuted ? 0 : n.Warnings.Count);
            if (warnings > 0) parts.Add($"{warnings} warning(s) - hover the ! operators");
            _summary.Text = string.Join("   |   ", parts) + Environment.NewLine + Query.SqlSplitter.OneLine(p.Sql, 400);
            _summary.ForeColor = warnings > 0 ? Color.DarkOrange : Theme.Text;
            _text.Text = PlanParser.ToText(p).Replace("\n", "\r\n").Replace("\r\r\n", "\r\n");
            _json.Text = PrettyJson(p.RawJson);
        }

        private void ShowProperties(PlanNode n)
        {
            _props.BeginUpdate();
            _props.Items.Clear();
            if (n != null)
            {
                _propsTitle.Text = "Properties - " + n.Title;
                void Add(string k, string v, Color? color = null)
                {
                    var item = new ListViewItem(new[] { k, v }) { ToolTipText = $"{k}: {v}" };
                    if (color.HasValue) item.ForeColor = color.Value;
                    _props.Items.Add(item);
                }
                foreach (var w in n.Warnings) Add("Warning", w, Color.DarkOrange);
                Add("Operator cost", $"{n.ExclusiveCost:N2} ({n.CostPercent:0.#} %)");
                if (n.ExclusiveMs.HasValue) Add("Operator time", $"{PlanGraph.FormatMs(n.ExclusiveMs.Value)} ({n.TimePercent ?? 0:0.#} %)");
                if (n.RowsFactor.HasValue) Add("Row estimate error", $"{n.RowsFactor:N1}x ({(n.RowsUnderestimated ? "under" : "over")})");
                foreach (var kv in n.Properties) Add(kv.Key, kv.Value);
            }
            _props.EndUpdate();
        }

        private StatementPlan Current => _statements.SelectedIndex >= 0 && _statements.SelectedIndex < _plans.Count ? _plans[_statements.SelectedIndex] : null;

        private void CopyJson()
        {
            var p = Current;
            if (p?.RawJson != null) Clipboard.SetText(p.RawJson);
        }

        /// <summary>
        /// pev2 (github.com/dalibo/pev2, PostgreSQL licence) is a single static HTML page that runs locally in the
        /// browser. We never fetch it or upload plans: the user downloads pev2.html once and we open it with the
        /// plan on the clipboard (paste into "Plan", query into "Query", then Submit).
        /// </summary>
        private void OpenInPev2()
        {
            var p = Current;
            if (p?.RawJson == null) return;
            var path = File.Exists(Pev2PathFile) ? File.ReadAllText(Pev2PathFile).Trim() : null;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                var answer = MessageBox.Show(this,
                    "PEV2 is a free, open-source plan visualizer that runs entirely in your browser (nothing is uploaded).\r\n\r\n" +
                    "1. Download pev2.html from https://github.com/dalibo/pev2/releases/latest\r\n" +
                    "2. Click OK and select the downloaded file.\r\n\r\nThe location is remembered.",
                    "Open in PEV2", MessageBoxButtons.OKCancel, MessageBoxIcon.Information);
                if (answer != DialogResult.OK) return;
                using (var ofd = new OpenFileDialog { Filter = "pev2.html|pev2*.html;*.html", Title = "Select the downloaded pev2.html" })
                {
                    if (ofd.ShowDialog(this) != DialogResult.OK) return;
                    path = ofd.FileName;
                    Directory.CreateDirectory(Path.GetDirectoryName(Pev2PathFile));
                    File.WriteAllText(Pev2PathFile, path);
                }
            }
            Clipboard.SetText(p.RawJson);
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show(this, "Could not open " + path + ": " + ex.Message, Vsix.Name); return; }
            MessageBox.Show(this, "The plan JSON is on the clipboard: paste it into PEV2's \"Plan\" box (Ctrl+V) and click Submit.", "Open in PEV2",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private static string PrettyJson(string json)
        {
            try
            {
                using (var doc = System.Text.Json.JsonDocument.Parse(json))
                using (var ms = new MemoryStream())
                {
                    using (var w = new System.Text.Json.Utf8JsonWriter(ms, new System.Text.Json.JsonWriterOptions { Indented = true }))
                        doc.WriteTo(w);
                    return System.Text.Encoding.UTF8.GetString(ms.ToArray()).Replace("\n", "\r\n");
                }
            }
            catch { return json; }
        }
    }
}
