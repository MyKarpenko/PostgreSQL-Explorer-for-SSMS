using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace SsmsPgExplorer.Import
{
    /// <summary>
    /// Preview of what an importer found. Nothing is ticked by default; the user ticks rows (or just
    /// highlights them), optionally narrowing the list with the filter. The Import button shows the count.
    /// </summary>
    internal sealed class ImportDialog : Form
    {
        private readonly ListView _list = new ListView
        {
            Dock = DockStyle.Fill, View = View.Details, CheckBoxes = true, FullRowSelect = true, GridLines = true, HideSelection = false,
        };
        private readonly TextBox _filter = new TextBox { Dock = DockStyle.Fill };
        private readonly Button _import = new Button { Text = "Import (0)", DialogResult = DialogResult.OK, AutoSize = true, Enabled = false };
        private readonly List<ImportCandidate> _all;
        private readonly HashSet<string> _checked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly ISet<string> _existingIds;
        private bool _rebuilding;

        /// <summary>Ticked rows (including ticked rows hidden by the filter); if none are ticked, the highlighted rows.</summary>
        public List<ImportCandidate> Selected
        {
            get
            {
                if (_checked.Count > 0) return _all.Where(c => _checked.Contains(c.Registration.Id)).ToList();
                return _list.SelectedItems.Cast<ListViewItem>().Select(i => (ImportCandidate)i.Tag).ToList();
            }
        }

        public ImportDialog(string tool, IList<ImportCandidate> candidates, IEnumerable<string> sources, ISet<string> existingIds)
        {
            _all = candidates.OrderBy(c => c.Registration.Group).ThenBy(c => c.Registration.DisplayName).ToList();
            _existingIds = existingIds;
            var scale = DeviceDpi / 96f;
            Text = $"Import Registered Servers from {tool}";
            Width = (int)(1040 * scale); Height = (int)(620 * scale);
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false; ShowInTaskbar = false;

            var header = new Label
            {
                Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8),
                MaximumSize = new Size((int)(1020 * scale), 0),
                Text = $"Found {_all.Count} PostgreSQL connection(s) in:\r\n  " + string.Join("\r\n  ", sources) +
                       "\r\n\r\nTick the servers to import (or highlight rows with Ctrl/Shift+click). Nothing is selected by default. " +
                       "Passwords are never imported: Secrets Manager / IAM entries connect with your AWS profile; password entries prompt on first connect. " +
                       "Existing entries (same source id) are updated in place.",
            };

            var filterRow = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(8, 0, 8, 4) };
            filterRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            filterRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            filterRow.Controls.Add(new Label { Text = "Filter:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 6, 0) }, 0, 0);
            filterRow.Controls.Add(_filter, 1, 0);
            new ToolTip().SetToolTip(_filter, "Matches name, group, host, auth or profile (e.g. 'orders', 'Secrets Manager', 'prod-readonly')");
            _filter.TextChanged += (s, e) => Rebuild();

            foreach (var (name, width) in new[] { ("Name", 240), ("Group", 240), ("Host:Port/DB", 260), ("Auth", 170), ("Status", 60), ("Notes", 240) })
                _list.Columns.Add(name, (int)(width * scale));
            _list.ShowItemToolTips = true;
            _list.ItemChecked += (s, e) =>
            {
                if (_rebuilding) return;
                var id = ((ImportCandidate)e.Item.Tag).Registration.Id;
                if (e.Item.Checked) _checked.Add(id); else _checked.Remove(id);
                UpdateCount();
            };
            _list.SelectedIndexChanged += (s, e) => UpdateCount();

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(8), WrapContents = false };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
            var all = new Button { Text = "Tick all shown", AutoSize = true };
            var none = new Button { Text = "Untick all", AutoSize = true };
            var tickHighlighted = new Button { Text = "Tick highlighted", AutoSize = true };
            all.Click += (s, e) => SetChecked(_list.Items.Cast<ListViewItem>(), true);
            none.Click += (s, e) => { _checked.Clear(); Rebuild(); };
            tickHighlighted.Click += (s, e) => SetChecked(_list.SelectedItems.Cast<ListViewItem>(), true);
            buttons.Controls.AddRange(new Control[] { cancel, _import, none, all, tickHighlighted });
            AcceptButton = _import; CancelButton = cancel;

            Controls.Add(_list);
            Controls.Add(filterRow);
            Controls.Add(header);
            Controls.Add(buttons);
            Rebuild();
        }

        private void Rebuild()
        {
            var terms = _filter.Text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            _rebuilding = true;
            _list.BeginUpdate();
            try
            {
                _list.Items.Clear();
                foreach (var c in _all)
                {
                    var r = c.Registration;
                    var auth = r.Auth == Model.AuthMode.AwsSecretsManager ? $"Secrets Manager ({(string.IsNullOrEmpty(r.AwsProfile) ? "default" : r.AwsProfile)})"
                             : r.Auth == Model.AuthMode.RdsIam ? $"RDS IAM ({(string.IsNullOrEmpty(r.AwsProfile) ? "default" : r.AwsProfile)})"
                             : $"Password ({r.Username})";
                    var hostText = string.IsNullOrEmpty(r.Host) ? "(from secret)" : $"{r.Host}:{r.Port}/{r.Database}";
                    var haystack = $"{r.DisplayName} {r.Group} {hostText} {auth}";
                    if (terms.Any(t => haystack.IndexOf(t, StringComparison.OrdinalIgnoreCase) < 0)) continue;

                    var item = new ListViewItem(new[] { r.DisplayName, r.Group, hostText, auth, _existingIds.Contains(r.Id) ? "update" : "new", c.Note ?? "" })
                    {
                        Tag = c,
                        Checked = _checked.Contains(r.Id),
                        ToolTipText = string.IsNullOrEmpty(r.SecretId) ? r.DisplayName : "Secret: " + r.SecretId,
                    };
                    if (!string.IsNullOrEmpty(c.Note)) item.ForeColor = Color.DarkGoldenrod;
                    _list.Items.Add(item);
                }
            }
            finally
            {
                _list.EndUpdate();
                _rebuilding = false;
            }
            UpdateCount();
        }

        private void SetChecked(IEnumerable<ListViewItem> items, bool value)
        {
            _rebuilding = true;
            foreach (var i in items.ToList())
            {
                i.Checked = value;
                var id = ((ImportCandidate)i.Tag).Registration.Id;
                if (value) _checked.Add(id); else _checked.Remove(id);
            }
            _rebuilding = false;
            UpdateCount();
        }

        private void UpdateCount()
        {
            var n = _checked.Count > 0 ? _checked.Count : _list.SelectedItems.Count;
            var what = _checked.Count > 0 ? "" : n > 0 ? " highlighted" : "";
            _import.Text = $"Import {n}{what}";
            _import.Enabled = n > 0;
            Text = Text.Split(new[] { "  -  " }, StringSplitOptions.None)[0] + $"  -  {_list.Items.Count} shown, {_checked.Count} ticked";
        }
    }
}
