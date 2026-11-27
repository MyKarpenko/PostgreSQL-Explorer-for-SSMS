using System;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using SsmsPgExplorer.UI;

namespace SsmsPgExplorer.Query
{
    /// <summary>
    /// SSMS-style results grid behaviour: right-click Copy / Copy with Headers / Select All / Save Results As...,
    /// plus View Value (double-click) for long or JSON values. Save honours SSMS's
    /// "Include column headers when saving the results" setting.
    /// </summary>
    internal static class ResultGrid
    {
        private const string SaveHeadersKey = "executionResults.sqlServer.gridResults.includeColumnHeadersWhileSavingGridResults";

        public static void Attach(DataGridView grid, DataTable table)
        {
            grid.ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableWithoutHeaderText;

            var menu = new ContextMenuStrip();
            var copy = new ToolStripMenuItem("Copy", null, (s, e) => Copy(grid, headers: false)) { ShortcutKeyDisplayString = "Ctrl+C" };
            var copyHeaders = new ToolStripMenuItem("Copy with Headers", null, (s, e) => Copy(grid, headers: true)) { ShortcutKeyDisplayString = "Ctrl+Shift+C" };
            var copyHeaderNames = new ToolStripMenuItem("Copy Column Names", null, (s, e) =>
                SetClipboard(string.Join("\t", SelectedColumns(grid).Select(c => c.HeaderText))));
            var selectAll = new ToolStripMenuItem("Select All", null, (s, e) => grid.SelectAll()) { ShortcutKeyDisplayString = "Ctrl+A" };
            var view = new ToolStripMenuItem("View Value...", null, (s, e) => ViewValue(grid)) { ShortcutKeyDisplayString = "Double-click" };
            var save = new ToolStripMenuItem("Save Results As...", null, (s, e) => SaveAs(grid, table));
            menu.Items.AddRange(new ToolStripItem[] { copy, copyHeaders, copyHeaderNames, new ToolStripSeparator(), selectAll, new ToolStripSeparator(), view, new ToolStripSeparator(), save });
            menu.Opening += (s, e) =>
            {
                var any = grid.GetCellCount(DataGridViewElementStates.Selected) > 0;
                copy.Enabled = copyHeaders.Enabled = copyHeaderNames.Enabled = any;
                view.Enabled = grid.CurrentCell != null;
                Theme.ApplyMenu(menu);
            };
            grid.ContextMenuStrip = menu;

            // Right-click on a cell outside the selection selects that cell first (as in SSMS).
            grid.CellMouseDown += (s, e) =>
            {
                if (e.Button != MouseButtons.Right || e.RowIndex < 0 || e.ColumnIndex < 0) return;
                var cell = grid.Rows[e.RowIndex].Cells[e.ColumnIndex];
                if (cell.Selected) return;
                grid.ClearSelection();
                cell.Selected = true;
                grid.CurrentCell = cell;
            };
            grid.CellDoubleClick += (s, e) => { if (e.RowIndex >= 0 && e.ColumnIndex >= 0) ViewValue(grid); };
            grid.KeyDown += (s, e) =>
            {
                if (e.Control && e.Shift && e.KeyCode == Keys.C) { Copy(grid, headers: true); e.Handled = true; }
                else if (e.Control && e.KeyCode == Keys.C) { Copy(grid, headers: false); e.Handled = true; }
                else if (e.Control && e.KeyCode == Keys.A) { grid.SelectAll(); e.Handled = true; }
            };
        }

        /// <summary>Called from the query window's ProcessCmdKey: the SSMS shell may claim Ctrl+C before the grid sees it.</summary>
        public static bool HandleKey(DataGridView grid, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.Shift | Keys.C)) { Copy(grid, true); return true; }
            if (keyData == (Keys.Control | Keys.C)) { Copy(grid, false); return true; }
            if (keyData == (Keys.Control | Keys.A)) { grid.SelectAll(); return true; }
            return false;
        }

        private static void Copy(DataGridView grid, bool headers)
        {
            if (grid.GetCellCount(DataGridViewElementStates.Selected) == 0) return;
            grid.ClipboardCopyMode = headers ? DataGridViewClipboardCopyMode.EnableAlwaysIncludeHeaderText : DataGridViewClipboardCopyMode.EnableWithoutHeaderText;
            try
            {
                var content = grid.GetClipboardContent();
                if (content != null) Clipboard.SetDataObject(content, true);
            }
            catch (Exception ex) { Diagnostics.Log.Error("grid copy", ex); }
            finally { grid.ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableWithoutHeaderText; }
        }

        private static void SetClipboard(string text)
        {
            if (!string.IsNullOrEmpty(text)) Clipboard.SetText(text);
        }

        private static DataGridViewColumn[] SelectedColumns(DataGridView grid) =>
            grid.SelectedCells.Cast<DataGridViewCell>().Select(c => grid.Columns[c.ColumnIndex]).Distinct().OrderBy(c => c.DisplayIndex).ToArray();

        // ---------- View Value ----------

        private static void ViewValue(DataGridView grid)
        {
            var cell = grid.CurrentCell;
            if (cell == null) return;
            var raw = cell.Value == null || cell.Value == DBNull.Value ? null : cell.Value.ToString();
            var column = grid.Columns[cell.ColumnIndex].HeaderText;
            using (var f = new ValueViewer(column, raw)) f.ShowDialog(grid.FindForm());
        }

        private sealed class ValueViewer : Form
        {
            public ValueViewer(string column, string value)
            {
                var scale = DeviceDpi / 96f;
                Text = $"{column} - Value";
                Width = (int)(820 * scale); Height = (int)(600 * scale);
                StartPosition = FormStartPosition.CenterParent;
                MinimizeBox = false; ShowInTaskbar = false;

                var pretty = Pretty(value, out var isJson);
                var box = new TextBox
                {
                    Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = !isJson,
                    Font = new Font("Consolas", 10f), Text = value == null ? "NULL" : pretty,
                };
                var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(6), WrapContents = false };
                var close = new Button { Text = "Close", DialogResult = DialogResult.Cancel, AutoSize = true };
                var copyBtn = new Button { Text = "Copy", AutoSize = true };
                var wrap = new CheckBox { Text = "Word wrap", AutoSize = true, Checked = box.WordWrap, Margin = new Padding(3, 7, 12, 3) };
                var info = new Label { AutoSize = true, Margin = new Padding(3, 8, 12, 3), ForeColor = SystemColors.GrayText,
                                       Text = value == null ? "NULL" : $"{value.Length:N0} chars{(isJson ? ", JSON (pretty-printed)" : "")}" };
                copyBtn.Click += (s, e) => { if (value != null) Clipboard.SetText(box.Text); };
                wrap.CheckedChanged += (s, e) => box.WordWrap = wrap.Checked;
                bar.Controls.AddRange(new Control[] { close, copyBtn, wrap, info });
                CancelButton = close;
                Controls.Add(box);
                Controls.Add(bar);
                Shown += (s, e) => { box.SelectionStart = 0; box.SelectionLength = 0; };
            }

            private static string Pretty(string value, out bool isJson)
            {
                isJson = false;
                if (string.IsNullOrEmpty(value)) return value ?? "";
                var t = value.TrimStart();
                if (t.StartsWith("{") || t.StartsWith("["))
                {
                    try
                    {
                        using (var doc = JsonDocument.Parse(value))
                        using (var ms = new MemoryStream())
                        {
                            using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true })) doc.WriteTo(w);
                            isJson = true;
                            return Encoding.UTF8.GetString(ms.ToArray()).Replace("\n", "\r\n");
                        }
                    }
                    catch { /* not JSON */ }
                }
                return value.Replace("\r\n", "\n").Replace("\n", "\r\n");
            }
        }

        // ---------- Save Results As ----------

        private static void SaveAs(DataGridView grid, DataTable table)
        {
            using (var sfd = new SaveFileDialog
            {
                Title = "Save Grid Results",
                Filter = "CSV (Comma delimited) (*.csv)|*.csv|Text (Tab delimited) (*.txt)|*.txt|JSON (*.json)|*.json|All files (*.*)|*.*",
                FileName = "Results.csv",
                OverwritePrompt = true,
            })
            {
                if (sfd.ShowDialog(grid.FindForm()) != DialogResult.OK) return;
                try
                {
                    var headers = SsmsSettings.Bool(SaveHeadersKey, true);
                    var rows = (table.DefaultView.Count == table.Rows.Count ? table.DefaultView.Cast<DataRowView>().Select(v => v.Row) : table.Rows.Cast<DataRow>()).ToList();
                    var cols = grid.Columns.Cast<DataGridViewColumn>().Where(c => c.Visible).OrderBy(c => c.DisplayIndex)
                                   .Select(c => (Header: c.HeaderText, Column: table.Columns[c.DataPropertyName])).Where(c => c.Column != null).ToList();
                    var ext = Path.GetExtension(sfd.FileName).ToLowerInvariant();
                    if (sfd.FilterIndex == 3 || ext == ".json") WriteJson(sfd.FileName, rows, cols.Select(c => (c.Header, c.Column)).ToList());
                    else WriteDelimited(sfd.FileName, rows, cols.Select(c => (c.Header, c.Column)).ToList(), sfd.FilterIndex == 2 || ext == ".txt" || ext == ".tsv" ? '\t' : ',', headers);
                    Diagnostics.Log.Info($"grid: saved {rows.Count} row(s) to {sfd.FileName}");
                }
                catch (Exception ex)
                {
                    Diagnostics.Log.Error("grid save", ex);
                    MessageBox.Show(grid.FindForm(), "Save failed: " + ex.Message, Vsix.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private static void WriteDelimited(string path, System.Collections.Generic.List<DataRow> rows,
            System.Collections.Generic.List<(string Header, DataColumn Column)> cols, char sep, bool headers)
        {
            string Field(string v)
            {
                if (v == null) return "";
                var needsQuotes = v.IndexOf(sep) >= 0 || v.IndexOf('"') >= 0 || v.IndexOf('\n') >= 0 || v.IndexOf('\r') >= 0;
                return needsQuotes ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
            }
            using (var w = new StreamWriter(path, false, new UTF8Encoding(true)))
            {
                if (headers) w.WriteLine(string.Join(sep.ToString(), cols.Select(c => Field(c.Header))));
                foreach (var r in rows)
                    w.WriteLine(string.Join(sep.ToString(), cols.Select(c => Field(r[c.Column] == DBNull.Value ? null : r[c.Column]?.ToString()))));
            }
        }

        private static void WriteJson(string path, System.Collections.Generic.List<DataRow> rows,
            System.Collections.Generic.List<(string Header, DataColumn Column)> cols)
        {
            using (var fs = File.Create(path))
            using (var w = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = true }))
            {
                w.WriteStartArray();
                foreach (var r in rows)
                {
                    w.WriteStartObject();
                    foreach (var c in cols)
                    {
                        var v = r[c.Column];
                        if (v == null || v == DBNull.Value) w.WriteNull(c.Header);
                        else w.WriteString(c.Header, v.ToString());
                    }
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
        }
    }
}
