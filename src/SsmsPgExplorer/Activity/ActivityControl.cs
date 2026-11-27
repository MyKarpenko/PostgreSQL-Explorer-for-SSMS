using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Npgsql;
using SsmsPgExplorer.Connection;
using SsmsPgExplorer.Model;
using SsmsPgExplorer.UI;
using MessageBox = System.Windows.Forms.MessageBox;

namespace SsmsPgExplorer.Activity
{
    /// <summary>
    /// Live pg_stat_activity: sessions, state, waits, blocking (pg_blocking_pids), transaction / query age.
    /// Right-click: Cancel Query (pg_cancel_backend) or Kill Session (pg_terminate_backend), both confirmed.
    /// </summary>
    internal sealed class ActivityControl : UserControl
    {
        private const string Sql = @"
SELECT a.pid,
       a.usename                                   AS ""User"",
       a.datname                                   AS ""Database"",
       a.application_name                          AS ""Application"",
       host(a.client_addr)                         AS ""Client"",
       a.state                                     AS ""State"",
       COALESCE(a.wait_event_type || ': ' || a.wait_event, '') AS ""Wait"",
       array_to_string(pg_blocking_pids(a.pid), ', ') AS ""Blocked by"",
       to_char(now() - a.xact_start,  'HH24:MI:SS') AS ""Xact age"",
       to_char(now() - a.query_start, 'HH24:MI:SS') AS ""Query age"",
       to_char(now() - a.backend_start, 'DD ""d"" HH24:MI') AS ""Connected"",
       a.backend_type                              AS ""Backend type"",
       COALESCE(a.backend_xmin::text, '')          AS ""xmin"",
       a.query                                     AS ""Query"",
       (a.pid = pg_backend_pid())::text            AS is_self,
       EXTRACT(EPOCH FROM now() - a.xact_start)::bigint AS xact_secs
FROM pg_stat_activity a
WHERE (@all_backends OR a.backend_type = 'client backend')
  AND (NOT @hide_idle OR a.state IS DISTINCT FROM 'idle')
  AND (@db IS NULL OR a.datname = @db)
ORDER BY (a.state = 'idle'), a.xact_start NULLS LAST, a.pid";

        private readonly ServerRegistration _server;
        private readonly string _database;

        private readonly ToolStrip _bar = new ClickThroughToolStrip { GripStyle = ToolStripGripStyle.Hidden, Dock = DockStyle.Top };
        private readonly ToolStripButton _refresh = new ToolStripButton("Refresh") { ToolTipText = "Refresh now (F5)" };
        private readonly ToolStripComboBox _interval = new ToolStripComboBox { DropDownStyle = ComboBoxStyle.DropDownList, AutoSize = false, Width = 90 };
        private readonly ToolStripButton _hideIdle = new ToolStripButton("Hide idle") { CheckOnClick = true, Checked = true };
        private readonly ToolStripButton _thisDb = new ToolStripButton("This database only") { CheckOnClick = true, Checked = false };
        private readonly ToolStripButton _allBackends = new ToolStripButton("System backends") { CheckOnClick = true, Checked = false, ToolTipText = "Include autovacuum, WAL writer, replication ..." };
        private readonly ToolStripTextBox _filter = new ToolStripTextBox { Width = 160, ToolTipText = "Filter by user, application, client, state or query text" };
        private readonly DataGridView _grid = new DataGridView
        {
            Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, RowHeadersVisible = false, BorderStyle = BorderStyle.None,
            AutoGenerateColumns = true, ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableWithAutoHeaderText,
        };
        private readonly TextBox _queryText = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = true, Font = new Font("Consolas", 9.5f), BorderStyle = BorderStyle.None };
        private readonly StatusStrip _status = new StatusStrip { SizingGrip = false };
        private readonly ToolStripStatusLabel _state = new ToolStripStatusLabel("") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        private readonly System.Windows.Forms.Timer _timer = new System.Windows.Forms.Timer();
        private bool _loading;
        private DataTable _data;
        private int? _selectedPid;

        public ActivityControl(ServerRegistration server, string database)
        {
            _server = server;
            _database = database;

            _interval.Items.AddRange(new object[] { "Manual", "2 sec", "5 sec", "10 sec", "30 sec" });
            _interval.SelectedIndex = 2;
            _bar.Items.AddRange(new ToolStripItem[]
            {
                _refresh, new ToolStripLabel("Auto:"), _interval, new ToolStripSeparator(),
                _hideIdle, _thisDb, _allBackends, new ToolStripSeparator(), new ToolStripLabel("Filter:"), _filter,
            });
            _refresh.Click += (s, e) => RefreshNow();
            _interval.SelectedIndexChanged += (s, e) => ApplyInterval();
            _hideIdle.CheckedChanged += (s, e) => RefreshNow();
            _thisDb.CheckedChanged += (s, e) => RefreshNow();
            _allBackends.CheckedChanged += (s, e) => RefreshNow();
            _filter.TextChanged += (s, e) => ApplyFilter();

            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterWidth = 5 };
            split.Panel1.Controls.Add(_grid);
            split.Panel2.Controls.Add(_queryText);
            Load += (s, e) => { try { split.SplitterDistance = Math.Max(120, (int)(split.Height * 0.72)); } catch { } };

            _status.Items.Add(_state);
            Controls.Add(split);
            Controls.Add(_bar);
            Controls.Add(_status);

            typeof(DataGridView).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.SetValue(_grid, true);
            _grid.SelectionChanged += (s, e) => OnSelection();
            _grid.CellFormatting += OnCellFormatting;
            _grid.DataBindingComplete += (s, e) =>
            {
                foreach (var hidden in new[] { "is_self", "xact_secs" }) if (_grid.Columns.Contains(hidden)) _grid.Columns[hidden].Visible = false;
                if (_grid.Columns.Contains("Query")) _grid.Columns["Query"].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
                foreach (DataGridViewColumn c in _grid.Columns) if (c.Name != "Query") c.AutoSizeMode = DataGridViewAutoSizeColumnMode.DisplayedCells;
                RestoreSelection();
            };
            _grid.MouseDown += (s, e) =>
            {
                if (e.Button != MouseButtons.Right) return;
                var hit = _grid.HitTest(e.X, e.Y);
                if (hit.RowIndex >= 0) _grid.Rows[hit.RowIndex].Selected = true;
            };
            _grid.ContextMenuStrip = BuildMenu();
            _grid.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.F5) { RefreshNow(); e.Handled = true; }
                else if (e.KeyCode == Keys.Delete) { Kill(terminate: true); e.Handled = true; }
            };

            _timer.Tick += (s, e) => RefreshNow();
            Theme.Attach(this);
            ApplyInterval();
            BeginInvokeWhenReady(RefreshNow);
        }

        private void BeginInvokeWhenReady(Action a)
        {
            if (IsHandleCreated) BeginInvoke(a); else HandleCreated += (s, e) => BeginInvoke(a);
        }

        private void ApplyInterval()
        {
            var secs = new[] { 0, 2, 5, 10, 30 }[Math.Max(0, _interval.SelectedIndex)];
            _timer.Stop();
            if (secs > 0) { _timer.Interval = secs * 1000; _timer.Start(); }
        }

        private void RefreshNow()
        {
            if (_loading || IsDisposed) return;
            _loading = true;
            var hideIdle = _hideIdle.Checked;
            var db = _thisDb.Checked ? _database : null;
            var all = _allBackends.Checked;
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                DataTable table = null;
                Exception error = null;
                try
                {
                    await TaskScheduler.Default;
                    using (var conn = await ConnectionFactory.OpenAsync(_server, _database, readOnly: true, CancellationToken.None).ConfigureAwait(false))
                    using (var cmd = new NpgsqlCommand(Sql, conn) { CommandTimeout = 30, AllResultTypesAreUnknown = true })
                    {
                        cmd.Parameters.AddWithValue("all_backends", all);
                        cmd.Parameters.AddWithValue("hide_idle", hideIdle);
                        cmd.Parameters.Add(new NpgsqlParameter("db", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object)db ?? DBNull.Value });
                        using (var r = await cmd.ExecuteReaderAsync().ConfigureAwait(false))
                        {
                            table = new DataTable();
                            for (var i = 0; i < r.FieldCount; i++) table.Columns.Add(r.GetName(i), typeof(string));
                            var values = new object[r.FieldCount];
                            while (await r.ReadAsync().ConfigureAwait(false)) { r.GetValues(values); table.Rows.Add(values); }
                        }
                    }
                }
                catch (Exception ex) { error = ex; }

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                _loading = false;
                if (IsDisposed) return;
                if (error != null)
                {
                    _state.Text = "Refresh failed: " + ConnectionFactory.Describe(error);
                    _state.ForeColor = Color.Firebrick;
                    return;
                }
                _data = table;
                ApplyFilter();
                var blocked = table.Rows.Cast<DataRow>().Count(r => !string.IsNullOrEmpty(r["Blocked by"] as string));
                _state.ForeColor = blocked > 0 ? Color.DarkOrange : Theme.BarText;
                _state.Text = $"{table.Rows.Count} session(s){(blocked > 0 ? $", {blocked} blocked" : "")}   |   {_server.DisplayName}   |   refreshed {DateTime.Now:HH:mm:ss}" +
                              (_timer.Enabled ? $"   |   every {_timer.Interval / 1000}s" : "");
            }).FileAndForget("SsmsPgExplorer/Activity");
        }

        private void ApplyFilter()
        {
            if (_data == null) return;
            if (_grid.CurrentRow?.DataBoundItem is DataRowView cur && int.TryParse(cur["pid"] as string, out var pid)) _selectedPid = pid;
            var f = _filter.Text.Trim().Replace("'", "''").Replace("[", "[[]").Replace("%", "[%]").Replace("*", "[*]");
            var view = _data.DefaultView;
            view.RowFilter = f.Length == 0 ? "" :
                string.Join(" OR ", new[] { "User", "Application", "Client", "State", "Query", "Database", "Wait" }.Select(c => $"[{c}] LIKE '%{f}%'")) + $" OR pid LIKE '%{f}%'";
            var scroll = _grid.FirstDisplayedScrollingRowIndex;
            _grid.DataSource = view;
            if (scroll >= 0 && scroll < _grid.RowCount) _grid.FirstDisplayedScrollingRowIndex = scroll;
        }

        private void RestoreSelection()
        {
            if (_selectedPid == null) return;
            foreach (DataGridViewRow row in _grid.Rows)
                if ((row.DataBoundItem as DataRowView)?["pid"] as string == _selectedPid.ToString())
                {
                    row.Selected = true;
                    _grid.CurrentCell = row.Cells.Cast<DataGridViewCell>().FirstOrDefault(c => c.Visible);
                    return;
                }
        }

        private void OnSelection()
        {
            var row = Selected;
            _queryText.Text = row == null ? "" : (row["Query"] as string ?? "").Replace("\n", "\r\n").Replace("\r\r\n", "\r\n");
            if (row != null && int.TryParse(row["pid"] as string, out var pid)) _selectedPid = pid;
        }

        private DataRowView Selected => _grid.CurrentRow?.DataBoundItem as DataRowView;

        private void OnCellFormatting(object sender, DataGridViewCellFormattingEventArgs e)
        {
            if (e.RowIndex < 0 || !(_grid.Rows[e.RowIndex].DataBoundItem is DataRowView row)) return;
            var blockedBy = row["Blocked by"] as string;
            var pid = row["pid"] as string;
            var isBlocker = _data != null && !string.IsNullOrEmpty(pid) &&
                            _data.Rows.Cast<DataRow>().Any(r => ((r["Blocked by"] as string) ?? "").Split(',').Select(x => x.Trim()).Contains(pid));
            if (!string.IsNullOrEmpty(blockedBy)) { e.CellStyle.BackColor = Theme.IsDark ? Color.FromArgb(90, 60, 20) : Color.FromArgb(255, 230, 190); }
            else if (isBlocker) { e.CellStyle.BackColor = Theme.IsDark ? Color.FromArgb(100, 30, 30) : Color.FromArgb(255, 205, 205); }
            if (row["is_self"] as string == "true") e.CellStyle.ForeColor = Theme.GrayText;
            if (long.TryParse(row["xact_secs"] as string, out var secs) && secs > 300 && _grid.Columns[e.ColumnIndex].Name == "Xact age")
                e.CellStyle.ForeColor = Color.Firebrick;
        }

        private ContextMenuStrip BuildMenu()
        {
            var m = new ContextMenuStrip();
            var cancel = new ToolStripMenuItem("Cancel Query  (pg_cancel_backend)", null, (s, e) => Kill(terminate: false));
            var kill = new ToolStripMenuItem("Kill Session  (pg_terminate_backend)", null, (s, e) => Kill(terminate: true)) { ShortcutKeyDisplayString = "Del" };
            var copy = new ToolStripMenuItem("Copy Query", null, (s, e) => { var r = Selected; if (r != null) Clipboard.SetText((r["Query"] as string) ?? ""); });
            var open = new ToolStripMenuItem("Open Query in New Window", null, (s, e) =>
            {
                var r = Selected;
                if (r == null) return;
                ThreadHelper.JoinableTaskFactory.RunAsync(() => Query.QueryWindow.OpenAsync(_server, (r["Database"] as string) ?? _database, (r["Query"] as string) ?? "")).FileAndForget("SsmsPgExplorer/ActivityOpen");
            });
            var blockers = new ToolStripMenuItem("Show Blocking Sessions", null, (s, e) => ShowBlockers());
            m.Items.AddRange(new ToolStripItem[] { cancel, kill, new ToolStripSeparator(), copy, open, blockers, new ToolStripSeparator(),
                                                    new ToolStripMenuItem("Refresh", null, (s, e) => RefreshNow()) { ShortcutKeyDisplayString = "F5" } });
            m.Opening += (s, e) =>
            {
                var r = Selected;
                var self = r != null && r["is_self"] as string == "true";
                cancel.Enabled = kill.Enabled = r != null && !self;
                copy.Enabled = open.Enabled = r != null;
                blockers.Enabled = r != null && !string.IsNullOrEmpty(r["Blocked by"] as string);
                Theme.ApplyMenu(m);
            };
            return m;
        }

        private void ShowBlockers()
        {
            var r = Selected;
            var ids = ((r?["Blocked by"] as string) ?? "").Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
            if (ids.Count == 0) return;
            _filter.Text = "";
            foreach (DataGridViewRow row in _grid.Rows)
                if (ids.Contains((row.DataBoundItem as DataRowView)?["pid"] as string)) { _grid.ClearSelection(); row.Selected = true; _grid.CurrentCell = row.Cells.Cast<DataGridViewCell>().First(c => c.Visible); return; }
            MessageBox.Show(this, $"Blocking pid(s) {string.Join(", ", ids)} are not in the current view (try turning off Hide idle / This database only).", Vsix.Name);
        }

        /// <summary>pg_cancel_backend (cancel the running query) or pg_terminate_backend (end the session), after confirmation.</summary>
        private void Kill(bool terminate)
        {
            var r = Selected;
            if (r == null || r["is_self"] as string == "true") return;
            if (!int.TryParse(r["pid"] as string, out var pid)) return;
            var fn = terminate ? "pg_terminate_backend" : "pg_cancel_backend";
            var action = terminate ? "KILL SESSION" : "CANCEL QUERY";
            var query = (r["Query"] as string) ?? "";
            if (query.Length > 400) query = query.Substring(0, 400) + "...";
            var msg = $"{action} on {_server.DisplayName}?\r\n\r\n" +
                      $"SELECT {fn}({pid});\r\n\r\n" +
                      $"pid: {pid}\r\nuser: {r["User"]}\r\ndatabase: {r["Database"]}\r\napplication: {r["Application"]}\r\nclient: {r["Client"]}\r\nstate: {r["State"]}   xact age: {r["Xact age"]}\r\n\r\n" +
                      $"{query}\r\n\r\n" +
                      (terminate
                          ? "The session's connection is closed and any open transaction is rolled back. Follow your change-management process for production."
                          : "Only the currently running statement is cancelled; the session stays connected.");
            if (MessageBox.Show(this, msg, Vsix.Name, MessageBoxButtons.YesNo, terminate ? MessageBoxIcon.Warning : MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;

            Diagnostics.Log.Info($"activity: {fn}({pid}) on '{_server.DisplayName}' user={r["User"]} app={r["Application"]}");
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                string result;
                try
                {
                    await TaskScheduler.Default;
                    using (var conn = await ConnectionFactory.OpenAsync(_server, _database, readOnly: true, CancellationToken.None).ConfigureAwait(false))
                    using (var cmd = new NpgsqlCommand($"SELECT {fn}(@pid)", conn) { CommandTimeout = 30 })
                    {
                        cmd.Parameters.AddWithValue("pid", pid);
                        var ok = (await cmd.ExecuteScalarAsync().ConfigureAwait(false)) as bool? == true;
                        result = ok ? $"{fn}({pid}) succeeded." : $"{fn}({pid}) returned false: the session is gone, or it belongs to another user and you lack pg_signal_backend.";
                    }
                }
                catch (Exception ex) { result = $"{fn}({pid}) failed: {ConnectionFactory.Describe(ex)}"; }
                Diagnostics.Log.Info("activity: " + result);
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (IsDisposed) return;
                _state.Text = result;
                RefreshNow();
            }).FileAndForget("SsmsPgExplorer/ActivityKill");
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.F5) { RefreshNow(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _timer.Stop(); _timer.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
