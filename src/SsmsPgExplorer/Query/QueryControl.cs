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

namespace SsmsPgExplorer.Query
{
    /// <summary>
    /// SSMS-style query tab: editor on top, Results / Messages below, status bar at the bottom.
    /// Holds one dedicated (unpooled) session per server so SET / temp tables / transactions behave like SSMS.
    /// With several servers it behaves like an SSMS multi-server (group) query: the batch runs on every
    /// server in parallel and results are merged with a "Server Name" column.
    /// </summary>
    internal sealed class QueryControl : UserControl
    {
        private const string PerServerDefaultDb = "(each server's initial database)";
        private const int MaxParallelServers = 8;
        private static readonly Color MultiServerStatus = Color.FromArgb(198, 40, 130); // SSMS uses a pink bar for group queries

        private readonly List<ServerRegistration> _servers;
        private readonly bool _multi;
        private readonly string _groupLabel;
        private string _database; // single: db name; multi: null = per-server default

        private readonly TextBox _editor = new TextBox
        {
            Multiline = true, AcceptsTab = true, AcceptsReturn = true, WordWrap = false, MaxLength = 0,
            ScrollBars = ScrollBars.Both, Dock = DockStyle.Fill, Font = new Font("Consolas", 10f), BorderStyle = BorderStyle.None,
            HideSelection = false,
        };
        private readonly SplitContainer _split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterWidth = 5 };
        private readonly TabControl _tabs = new TabControl { Dock = DockStyle.Fill };
        private readonly TabPage _resultsTab = new TabPage("Results");
        private readonly TabPage _messagesTab = new TabPage("Messages");
        private readonly Panel _results = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        private readonly TextBox _messages = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, Dock = DockStyle.Fill, Font = new Font("Consolas", 9.5f), WordWrap = false, BorderStyle = BorderStyle.None };
        private readonly ToolStrip _bar = new UI.ClickThroughToolStrip { GripStyle = ToolStripGripStyle.Hidden, Dock = DockStyle.Top };
        // Real Buttons (own HWND) instead of ToolStripButtons: ToolStrip items hosted in SSMS tool windows
        // can silently drop clicks, while F5 / Ctrl+E always worked. Styled so it reads as the primary action.
        private readonly Button _execute = new Button
        {
            Text = "▶ Execute", AutoSize = true, FlatStyle = FlatStyle.Flat, Margin = new Padding(2, 1, 2, 1),
            BackColor = Color.FromArgb(16, 124, 16), ForeColor = Color.White, Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold),
            Tag = UI.Theme.SkipTag, Cursor = Cursors.Hand, UseVisualStyleBackColor = false,
        };
        private readonly Button _cancel = new Button
        {
            Text = "■ Cancel", AutoSize = true, FlatStyle = FlatStyle.Flat, Margin = new Padding(2, 1, 2, 1), Enabled = false,
            BackColor = Color.FromArgb(164, 38, 44), ForeColor = Color.White, Tag = UI.Theme.SkipTag, Cursor = Cursors.Hand, UseVisualStyleBackColor = false,
        };
        private readonly ToolTip _tips = new ToolTip();
        private readonly Button _estimatedPlan = new Button
        {
            Text = "Estimated Plan", AutoSize = true, FlatStyle = FlatStyle.Flat, Margin = new Padding(2, 1, 2, 1),
            BackColor = Color.FromArgb(90, 90, 96), ForeColor = Color.White, Tag = UI.Theme.SkipTag, Cursor = Cursors.Hand, UseVisualStyleBackColor = false,
        };
        private readonly CheckBox _actualPlan = new CheckBox
        {
            Text = "Include Actual Plan", Appearance = Appearance.Button, AutoSize = true, FlatStyle = FlatStyle.Flat, Margin = new Padding(2, 1, 2, 1),
            BackColor = Color.FromArgb(90, 90, 96), ForeColor = Color.White, Tag = UI.Theme.SkipTag, Cursor = Cursors.Hand, UseVisualStyleBackColor = false,
        };
        private readonly Button _format = new Button
        {
            Text = "Format SQL", AutoSize = true, FlatStyle = FlatStyle.Flat, Margin = new Padding(2, 1, 2, 1),
            BackColor = Color.FromArgb(90, 90, 96), ForeColor = Color.White, Tag = UI.Theme.SkipTag, Cursor = Cursors.Hand, UseVisualStyleBackColor = false,
        };
        private DateTime _chordK = DateTime.MinValue;
        private readonly TabPage _planTab = new TabPage("Execution plan");
        private readonly Plans.PlanView _planView = new Plans.PlanView { Dock = DockStyle.Fill };
        private readonly ToolStripComboBox _dbCombo = new ToolStripComboBox { AutoSize = false, Width = 200 };
        private readonly ToolStripButton _readOnly = new ToolStripButton("Read-only") { CheckOnClick = true, Checked = true };
        private readonly ToolStripButton _merge = new ToolStripButton("Merge results") { CheckOnClick = true, Checked = true, ToolTipText = "Union result set N from every server into one grid" };
        private readonly ToolStripButton _addServer = new ToolStripButton("Server name") { CheckOnClick = true, Checked = true, ToolTipText = "Add a leading Server Name column" };
        private readonly ToolStripTextBox _maxRows = new ToolStripTextBox { Text = "10000", Width = 60, ToolTipText = "Max rows materialised per result set (per server)" };
        private readonly StatusStrip _status = new StatusStrip { SizingGrip = false };
        private readonly ToolStripStatusLabel _state = new ToolStripStatusLabel("Ready") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        private readonly ToolStripStatusLabel _conn = new ToolStripStatusLabel();
        private readonly ToolStripStatusLabel _mode = new ToolStripStatusLabel();
        private readonly ToolStripStatusLabel _elapsed = new ToolStripStatusLabel("00:00:00");
        private readonly ToolStripStatusLabel _rowCount = new ToolStripStatusLabel("0 rows");

        private readonly Dictionary<string, NpgsqlConnection> _sessions = new Dictionary<string, NpgsqlConnection>();
        private CancellationTokenSource _cts;
        private bool _suppressDbChange;

        public QueryControl(QueryWindow.Context ctx)
        {
            _servers = ctx.Servers;
            _multi = ctx.IsMulti;
            _groupLabel = ctx.GroupLabel;
            _database = _multi ? null : ctx.Database;

            _bar.Items.AddRange(new ToolStripItem[]
            {
                new ToolStripControlHost(_execute), new ToolStripControlHost(_cancel), new ToolStripSeparator(),
                new ToolStripLabel("Database:"), _dbCombo, new ToolStripSeparator(),
                _readOnly, new ToolStripSeparator(),
                new ToolStripControlHost(_format), new ToolStripSeparator(),
            });
            _format.Click += (s, e) => FormatSql();
            _tips.SetToolTip(_format, "Format SQL (Ctrl+K, Ctrl+Q) - PostgreSQL-aware: formats the selection, or the whole script");
            if (!_multi)
            {
                _bar.Items.AddRange(new ToolStripItem[] { new ToolStripControlHost(_estimatedPlan), new ToolStripControlHost(_actualPlan), new ToolStripSeparator() });
                _estimatedPlan.Click += (s, e) => EstimatedPlan();
                _actualPlan.CheckedChanged += (s, e) =>
                {
                    _actualPlan.BackColor = _actualPlan.Checked ? Color.FromArgb(0, 102, 184) : Color.FromArgb(90, 90, 96);
                    _state.Text = _actualPlan.Checked ? "Include Actual Plan is ON: Execute runs EXPLAIN ANALYZE (rows are not returned)." : "Ready";
                };
                _tips.SetToolTip(_estimatedPlan, "Display Estimated Execution Plan (Ctrl+L): EXPLAIN (COSTS, VERBOSE, FORMAT JSON) - the query is NOT executed");
                _tips.SetToolTip(_actualPlan, "Include Actual Execution Plan (Ctrl+M): Execute runs EXPLAIN (ANALYZE, COSTS, VERBOSE, BUFFERS, FORMAT JSON). " +
                                              "The query IS executed; data-changing statements run inside BEGIN ... ROLLBACK.");
            }
            if (_multi) _bar.Items.AddRange(new ToolStripItem[] { _merge, _addServer, new ToolStripSeparator() });
            _bar.Items.AddRange(new ToolStripItem[] { new ToolStripLabel("Max rows:"), _maxRows });

            _execute.FlatAppearance.BorderSize = 0;
            _cancel.FlatAppearance.BorderSize = 0;
            _tips.SetToolTip(_execute, "Execute selection or whole script (F5 / Ctrl+E / Alt+X)");
            _tips.SetToolTip(_cancel, "Cancel executing query (Alt+Break)");
            _execute.Click += (s, e) => { Diagnostics.Log.Info("execute: button click"); Execute(); };
            _cancel.Click += (s, e) => _cts?.Cancel();
            _cancel.EnabledChanged += (s, e) => _cancel.BackColor = _cancel.Enabled ? Color.FromArgb(164, 38, 44) : Color.FromArgb(110, 110, 110);
            _execute.EnabledChanged += (s, e) => _execute.BackColor = _execute.Enabled ? Color.FromArgb(16, 124, 16) : Color.FromArgb(110, 110, 110);
            _cancel.BackColor = Color.FromArgb(110, 110, 110);
            _readOnly.CheckedChanged += (s, e) => OnReadOnlyToggled();

            if (!AllAllowWrites)
            {
                _readOnly.Enabled = false;
                _readOnly.ToolTipText = _multi
                    ? "At least one server in this group does not allow writes (Edit Registration to change)."
                    : "This registration does not allow writes (Edit Registration to change).";
            }

            // Single server: pick from the server's databases. Multi: per-server default or a typed name applied to all.
            _dbCombo.DropDownStyle = _multi ? ComboBoxStyle.DropDown : ComboBoxStyle.DropDownList;
            _dbCombo.SelectedIndexChanged += (s, e) => OnDatabaseChanged();
            if (_multi)
            {
                _dbCombo.ToolTipText = "Leave as the default to use each registration's initial database, or type a database name to use on every server.";
                _dbCombo.Leave += (s, e) => OnDatabaseChanged();
                _dbCombo.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { OnDatabaseChanged(); e.SuppressKeyPress = true; } };
            }

            _resultsTab.Controls.Add(_results);
            _messagesTab.Controls.Add(_messages);
            _tabs.TabPages.Add(_resultsTab);
            _tabs.TabPages.Add(_messagesTab);
            _planTab.Controls.Add(_planView); // added to _tabs on first plan, like SSMS's "Execution plan" tab

            _split.Panel1.Controls.Add(_editor);
            _split.Panel2.Controls.Add(_tabs);
            _split.Panel2Collapsed = true;

            _status.Items.AddRange(new ToolStripItem[] { _state, _conn, _mode, _elapsed, _rowCount });

            Controls.Add(_split);
            Controls.Add(_bar);
            Controls.Add(_status);

            Load += (s, e) => _split.SplitterDistance = Math.Max(80, (int)(_split.Height * 0.45));
            Theme.Attach(this);
            _editor.ContextMenuStrip = BuildEditorMenu();

            _suppressDbChange = true;
            _dbCombo.Items.Add(_multi ? PerServerDefaultDb : _database);
            _dbCombo.SelectedIndex = 0;
            _suppressDbChange = false;
            UpdateStatus();
            LoadDatabaseList();

            if (_multi)
            {
                Log($"Multi-server query: {_servers.Count} server{(_servers.Count == 1 ? "" : "s")} in '{_groupLabel}'.");
                foreach (var s in _servers) Log($"  - {s.DisplayName}  ({s.Host}:{s.Port}/{s.Database}, {s.Auth})");
            }

            if (!string.IsNullOrEmpty(ctx.InitialText))
            {
                // Scripts come with "\n"; the WinForms TextBox only breaks lines on "\r\n".
                _editor.Text = NormalizeNewlines(ctx.InitialText);
                _editor.SelectionStart = _editor.TextLength;
            }
            if (ctx.Execute) BeginInvokeWhenReady(Execute);
        }

        /// <summary>Right-click in the editor: Execute is always reachable, even if a docked/auto-hide window covers the toolbar.</summary>
        private ContextMenuStrip BuildEditorMenu()
        {
            var m = new ContextMenuStrip();
            var exec = new ToolStripMenuItem("Execute", null, (s, e) => Execute()) { ShortcutKeyDisplayString = "F5" };
            var execSel = new ToolStripMenuItem("Execute Selection", null, (s, e) => Execute());
            var cancel = new ToolStripMenuItem("Cancel Executing Query", null, (s, e) => _cts?.Cancel()) { ShortcutKeyDisplayString = "Alt+Break" };
            var results = new ToolStripMenuItem("Show/Hide Results Pane", null, (s, e) => _split.Panel2Collapsed = !_split.Panel2Collapsed) { ShortcutKeyDisplayString = "Ctrl+R" };
            var est = new ToolStripMenuItem("Display Estimated Execution Plan", null, (s, e) => EstimatedPlan()) { ShortcutKeyDisplayString = "Ctrl+L", Visible = !_multi };
            var act = new ToolStripMenuItem("Include Actual Execution Plan", null, (s, e) => _actualPlan.Checked = !_actualPlan.Checked) { ShortcutKeyDisplayString = "Ctrl+M", Visible = !_multi };
            m.Opening += (s, e) => act.Checked = _actualPlan.Checked;
            m.Items.AddRange(new ToolStripItem[]
            {
                exec, execSel, cancel, new ToolStripSeparator(), est, act, new ToolStripSeparator(),
                new ToolStripMenuItem("Format SQL", null, (s, e) => FormatSql()) { ShortcutKeyDisplayString = "Ctrl+K, Ctrl+Q" }, new ToolStripSeparator(),
                new ToolStripMenuItem("Cut", null, (s, e) => _editor.Cut()),
                new ToolStripMenuItem("Copy", null, (s, e) => _editor.Copy()),
                new ToolStripMenuItem("Paste", null, (s, e) => _editor.Paste()),
                new ToolStripMenuItem("Select All", null, (s, e) => _editor.SelectAll()),
                new ToolStripSeparator(), results,
            });
            m.Opening += (s, e) =>
            {
                var running = _cts != null;
                exec.Enabled = !running && _editor.TextLength > 0;
                execSel.Visible = _editor.SelectionLength > 0;
                execSel.Enabled = !running;
                cancel.Enabled = running;
                Theme.ApplyMenu(m);
            };
            return m;
        }

        private DataGridView FocusedGrid()
        {
            Control c = this;
            while (c is ContainerControl cc && cc.ActiveControl != null) c = cc.ActiveControl;
            for (var p = c; p != null && p != this; p = p.Parent) if (p is DataGridView g) return g;
            return null;
        }

        private static string NormalizeNewlines(string s) =>
            s == null ? null : s.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");

        /// <summary>Format SQL: the selection if any, else the whole script. Undo with Ctrl+Z.</summary>
        private void FormatSql()
        {
            try
            {
                var hasSelection = _editor.SelectionLength > 0;
                var source = hasSelection ? _editor.SelectedText : _editor.Text;
                if (string.IsNullOrWhiteSpace(source)) return;
                var formatted = SqlFormatter.Format(source);
                if (hasSelection)
                {
                    var start = _editor.SelectionStart;
                    _editor.SelectedText = formatted.TrimEnd('\r', '\n');   // SelectedText keeps a single undo step
                    _editor.Select(start, formatted.TrimEnd('\r', '\n').Length);
                }
                else
                {
                    _editor.SelectAll();
                    _editor.SelectedText = formatted;
                    _editor.Select(0, 0);
                }
                _state.Text = "Formatted (Ctrl+Z to undo).";
            }
            catch (Exception ex)
            {
                Diagnostics.Log.Error("format", ex);
                _state.Text = "Format failed: " + ex.Message;
            }
        }

        private bool AllAllowWrites => _servers.All(s => s.AllowWrites);

        // Index in the group + id, so two registrations that share an id still get separate sessions.
        private string SessionKey(ServerRegistration s) => _servers.IndexOf(s) + ":" + s.Id;

        private bool ReadOnlySession => !AllAllowWrites || _readOnly.Checked;

        private string DatabaseFor(ServerRegistration s) => _multi ? (_database ?? s.Database) : _database;

        private void BeginInvokeWhenReady(Action a)
        {
            if (IsHandleCreated) BeginInvoke(a);
            else HandleCreated += (s, e) => BeginInvoke(a);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            // Best-effort: the SSMS shell may claim these accelerators before the hosted control sees them.
            if (keyData == Keys.F5 || keyData == (Keys.Control | Keys.E) || keyData == (Keys.Alt | Keys.X)) { Execute(); return true; }
            // SSMS: Ctrl+L = Display Estimated Execution Plan, Ctrl+M = Include Actual Execution Plan (toggle).
            if (keyData == (Keys.Control | Keys.L)) { EstimatedPlan(); return true; }
            // SSMS 22.7+: Format SQL = Ctrl+K, Ctrl+Q (Ctrl+K, Ctrl+D = VS Format Document also accepted).
            if (keyData == (Keys.Control | Keys.K)) { _chordK = DateTime.UtcNow; return true; }
            if ((keyData == (Keys.Control | Keys.Q) || keyData == (Keys.Control | Keys.D)) && (DateTime.UtcNow - _chordK).TotalSeconds < 2)
            {
                _chordK = DateTime.MinValue;
                FormatSql();
                return true;
            }
            if (keyData == (Keys.Control | Keys.M)) { if (!_multi) _actualPlan.Checked = !_actualPlan.Checked; return true; }
            if (keyData == (Keys.Alt | Keys.Pause) || keyData == (Keys.Alt | Keys.Cancel)) { _cts?.Cancel(); return true; }
            if (keyData == (Keys.Control | Keys.R)) { _split.Panel2Collapsed = !_split.Panel2Collapsed; return true; }
            if (keyData == (Keys.Control | Keys.A) && _editor.Focused) { _editor.SelectAll(); return true; }
            // The SSMS shell may route Ctrl+C / Ctrl+A to its own commands before a hosted grid sees them.
            if (FocusedGrid() is DataGridView grid && ResultGrid.HandleKey(grid, keyData)) return true;
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void UpdateStatus()
        {
            _conn.Text = _multi
                ? $"{_groupLabel} | {_servers.Count} servers | {_database ?? "initial db"}"
                : $"{_servers[0].DisplayName} | {_database} | {_servers[0].Username}";
            _conn.ToolTipText = string.Join(Environment.NewLine, _servers.Select(s => $"{s.DisplayName}  {s.Host}:{s.Port}"));
            _mode.Text = ReadOnlySession ? "READ ONLY" : "READ WRITE";
            _mode.ForeColor = ReadOnlySession ? Theme.BarText : Color.White;
            _mode.BackColor = ReadOnlySession ? Theme.BarBackground : Color.Firebrick;
            if (_multi)
            {
                _status.BackColor = MultiServerStatus;
                foreach (ToolStripItem i in _status.Items) if (i != _mode || ReadOnlySession) i.ForeColor = Color.White;
            }
        }

        private void LoadDatabaseList()
        {
            if (_multi) return; // per-server lists differ; the user types a name or keeps the default
            var server = _servers[0];
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    var dbs = await PgCatalog.DatabasesAsync(server, CancellationToken.None);
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    if (IsDisposed) return;
                    _suppressDbChange = true;
                    _dbCombo.Items.Clear();
                    foreach (var d in dbs.Select(x => x.Name)) _dbCombo.Items.Add(d);
                    if (!_dbCombo.Items.Contains(_database)) _dbCombo.Items.Insert(0, _database);
                    _dbCombo.SelectedItem = _database;
                    _suppressDbChange = false;
                }
                catch { /* list is a convenience; Execute reports real connection errors */ }
            }).FileAndForget("SsmsPgExplorer/DbList");
        }

        private void OnDatabaseChanged()
        {
            if (_suppressDbChange) return;
            var text = (_dbCombo.SelectedItem as string ?? _dbCombo.Text ?? "").Trim();
            string db = _multi ? (text.Length == 0 || text == PerServerDefaultDb ? null : text) : (text.Length == 0 ? _database : text);
            if (db == _database) return;
            _database = db;
            CloseSessions();
            UpdateStatus();
            Log($"Database changed to {db ?? "each server's initial database"}. New sessions will open on next execute.");
        }

        private void OnReadOnlyToggled()
        {
            if (!_readOnly.Checked && _readOnly.Enabled)
            {
                var target = _multi ? $"ALL {_servers.Count} servers in '{_groupLabel}'" : "this session";
                var ok = MessageBox.Show(this,
                    $"Switch {target} to READ WRITE?\r\n\r\n" +
                    "Follow your change-management process before modifying production data: change ticket, backup / archive of affected rows, and a tested rollback.",
                    Vsix.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;
                if (!ok) { _readOnly.Checked = true; return; }
            }
            UpdateStatus();
            var open = _sessions.Values.Where(c => c.State == ConnectionState.Open).ToList();
            var readOnly = ReadOnlySession;
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                foreach (var session in open)
                {
                    try { await ConnectionFactory.SetReadOnlyAsync(session, readOnly, CancellationToken.None); }
                    catch (Exception ex)
                    {
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                        Log("Could not change session mode: " + ConnectionFactory.Describe(ex));
                    }
                }
            }).FileAndForget("SsmsPgExplorer/ReadOnly");
        }

        private void Execute()
        {
            try { ExecuteCore(); }
            catch (Exception ex)
            {
                // Never let a click handler swallow an error silently.
                Diagnostics.Log.Error("execute", ex);
                _cts = null;
                _execute.Enabled = true;
                _cancel.Enabled = false;
                _split.Panel2Collapsed = false;
                _tabs.SelectedTab = _messagesTab;
                Log("Execute failed: " + ex.Message);
                _state.Text = "Execute failed - see Messages.";
            }
        }

        private void ExecuteCore()
        {
            if (_cts != null) return; // already running
            var sql = _editor.SelectionLength > 0 ? _editor.SelectedText : _editor.Text;
            if (string.IsNullOrWhiteSpace(sql)) return;
            if (_actualPlan.Checked && !_multi) { RunPlans(sql, actual: true); return; }
            if (!int.TryParse(_maxRows.Text, out var maxRows) || maxRows <= 0) maxRows = 10000;
            var merge = _merge.Checked;
            var addServer = _addServer.Checked;
            var readOnly = ReadOnlySession;

            if (_multi && !readOnly &&
                MessageBox.Show(this, $"Execute in READ WRITE mode on {_servers.Count} servers?", Vsix.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _execute.Enabled = false;
            _cancel.Enabled = true;
            _state.Text = _multi ? $"Executing query on {_servers.Count} servers..." : "Executing query...";
            _messages.Clear();
            ClearResults();
            var start = DateTime.Now;
            var ticker = new System.Windows.Forms.Timer { Interval = 500 };
            ticker.Tick += (s, e) => _elapsed.Text = (DateTime.Now - start).ToString(@"hh\:mm\:ss");
            ticker.Start();

            // Resolve sessions on the UI thread's view of the dictionary, run on the thread pool.
            // Keyed by registration object, not Id: tolerant of duplicate ids in a group.
            var existing = new Dictionary<ServerRegistration, NpgsqlConnection>();
            var dbFor = new Dictionary<ServerRegistration, string>();
            foreach (var s in _servers)
            {
                existing[s] = _sessions.TryGetValue(SessionKey(s), out var c) && c.State == ConnectionState.Open ? c : null;
                dbFor[s] = DatabaseFor(s);
            }
            Diagnostics.Log.Info($"execute: {_servers.Count} server(s), readOnly={readOnly}, sql length={sql.Length}");

            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await TaskScheduler.Default;
                var gate = new SemaphoreSlim(MaxParallelServers);
                var opened = new System.Collections.Concurrent.ConcurrentDictionary<string, NpgsqlConnection>();

                async Task<(ServerRegistration, QueryRunner.Result)> RunOne(ServerRegistration server)
                {
                    await gate.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        var session = existing[server];
                        if (session == null)
                        {
                            session = await ConnectionFactory.OpenAsync(server, dbFor[server], readOnly, ct, pooled: false).ConfigureAwait(false);
                            opened[SessionKey(server)] = session;
                        }
                        var r = await new QueryRunner { MaxRows = maxRows }.ExecuteAsync(session, sql, ct).ConfigureAwait(false);
                        return (server, r);
                    }
                    catch (Exception ex)
                    {
                        if (!(ex is OperationCanceledException)) Diagnostics.Log.Error($"execute on {server.DisplayName}", ex);
                        var r = new QueryRunner.Result { Failed = true };
                        r.Messages.Add(ex is OperationCanceledException ? "Cancelled." : ConnectionFactory.Describe(ex));
                        return (server, r);
                    }
                    finally { gate.Release(); }
                }

                var perServer = await Task.WhenAll(_servers.Select(RunOne)).ConfigureAwait(false);
                var result = _multi
                    ? QueryRunner.Combine(perServer, merge, addServer)
                    : perServer[0].Item2;

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                ticker.Stop(); ticker.Dispose();
                _cts.Dispose(); _cts = null;
                foreach (var kv in opened)
                {
                    if (IsDisposed) { kv.Value.Dispose(); continue; }
                    _sessions[kv.Key] = kv.Value;
                }
                if (IsDisposed) return;
                _execute.Enabled = true;
                _cancel.Enabled = false;
                ShowResult(result);

                // Drop sessions that died (network, server restart, FATAL) so the next execute reconnects.
                foreach (var id in _sessions.Where(kv => kv.Value.State != ConnectionState.Open).Select(kv => kv.Key).ToList())
                {
                    try { _sessions[id].Dispose(); } catch { }
                    _sessions.Remove(id);
                }
            }).FileAndForget("SsmsPgExplorer/Execute");
        }

        // ---------- execution plans ----------

        private void EstimatedPlan()
        {
            if (_multi)
            {
                MessageBox.Show(this, "Execution plans are available in single-server query windows.", Vsix.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (_cts != null) return;
            var sql = _editor.SelectionLength > 0 ? _editor.SelectedText : _editor.Text;
            if (string.IsNullOrWhiteSpace(sql)) return;
            try { RunPlans(sql, actual: false); }
            catch (Exception ex) { Diagnostics.Log.Error("estimated plan", ex); Log("Plan failed: " + ex.Message); }
        }

        /// <summary>
        /// Estimated: EXPLAIN (COSTS, VERBOSE, FORMAT JSON) per explainable statement; nothing is executed.
        /// Actual: EXPLAIN (ANALYZE, COSTS, VERBOSE, BUFFERS, FORMAT JSON); non-explainable statements (SET, CREATE TEMP ...)
        /// run normally so later statements see their effect. Data-changing statements are wrapped in
        /// BEGIN ... ROLLBACK (or a SAVEPOINT when the session is already inside a transaction) so getting a plan never commits.
        /// </summary>
        private void RunPlans(string sql, bool actual)
        {
            var statements = SqlSplitter.Split(sql);
            if (statements.Count == 0) return;
            var server = _servers[0];
            var readOnly = ReadOnlySession;

            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _execute.Enabled = false;
            _estimatedPlan.Enabled = false;
            _cancel.Enabled = true;
            _state.Text = actual ? "Executing with actual plan..." : "Estimating plan...";
            _messages.Clear();
            var start = DateTime.Now;
            var ticker = new System.Windows.Forms.Timer { Interval = 500 };
            ticker.Tick += (s, e) => _elapsed.Text = (DateTime.Now - start).ToString(@"hh\:mm\:ss");
            ticker.Start();
            var existing = _sessions.TryGetValue(SessionKey(server), out var cur) && cur.State == ConnectionState.Open ? cur : null;
            var db = DatabaseFor(server);
            Diagnostics.Log.Info($"plan: {(actual ? "actual" : "estimated")}, {statements.Count} statement(s), readOnly={readOnly}");

            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                var plans = new List<Plans.StatementPlan>();
                var messages = new List<string>();
                NpgsqlConnection opened = null;
                try
                {
                    await TaskScheduler.Default;
                    var session = existing;
                    if (session == null) session = opened = await ConnectionFactory.OpenAsync(server, db, readOnly, ct, pooled: false).ConfigureAwait(false);
                    void OnNotice(object s, NpgsqlNoticeEventArgs e) => messages.Add($"{e.Notice.Severity}: {e.Notice.MessageText}");
                    session.Notice += OnNotice;
                    try
                    {
                        for (var i = 0; i < statements.Count; i++)
                        {
                            ct.ThrowIfCancellationRequested();
                            var stmt = statements[i];
                            if (!SqlSplitter.IsExplainable(stmt))
                            {
                                if (actual)
                                {
                                    await ExecNonQueryAsync(session, stmt, ct).ConfigureAwait(false);
                                    plans.Add(new Plans.StatementPlan { Index = i + 1, Sql = stmt, IsActual = true, Skipped = "Not explainable: executed normally." });
                                }
                                else plans.Add(new Plans.StatementPlan { Index = i + 1, Sql = stmt, Skipped = "Not explainable (not executed)." });
                                continue;
                            }

                            var options = actual ? "ANALYZE, COSTS, VERBOSE, BUFFERS, FORMAT JSON" : "COSTS, VERBOSE, FORMAT JSON";
                            var explain = $"EXPLAIN ({options})\n{stmt}";
                            var guard = actual && SqlSplitter.MayWrite(stmt);
                            try
                            {
                                string json;
                                if (guard) json = await ExplainRolledBackAsync(session, explain, ct).ConfigureAwait(false);
                                else json = await ScalarTextAsync(session, explain, ct).ConfigureAwait(false);
                                var plan = Plans.PlanParser.Parse(json, stmt, i + 1, actual);
                                plans.Add(plan);
                                messages.Add($"Query {i + 1}: {(actual ? $"executed in {Plans.PlanGraph.FormatMs(plan.ExecutionTimeMs ?? 0)}" : "plan estimated")}" +
                                             (guard ? " (rolled back)" : ""));
                            }
                            catch (PostgresException pg)
                            {
                                plans.Add(new Plans.StatementPlan { Index = i + 1, Sql = stmt, IsActual = actual, Error = ConnectionFactory.Describe(pg) });
                                messages.Add($"Query {i + 1}: {ConnectionFactory.Describe(pg)}");
                                // Inside an explicit transaction the error aborts it; say so instead of failing every later statement.
                                if (pg.SqlState == "25P02") break;
                            }
                        }
                    }
                    finally { session.Notice -= OnNotice; }
                }
                catch (OperationCanceledException) { messages.Add("Cancelled."); }
                catch (Exception ex)
                {
                    Diagnostics.Log.Error("plan", ex);
                    messages.Add(ConnectionFactory.Describe(ex));
                }

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                ticker.Stop(); ticker.Dispose();
                _cts?.Dispose(); _cts = null;
                if (opened != null) { if (IsDisposed) opened.Dispose(); else _sessions[SessionKey(server)] = opened; }
                if (IsDisposed) return;
                _execute.Enabled = true;
                _estimatedPlan.Enabled = true;
                _cancel.Enabled = false;
                ShowPlans(plans, messages, actual, DateTime.Now - start);
            }).FileAndForget("SsmsPgExplorer/Plan");
        }

        private static async Task<string> ScalarTextAsync(NpgsqlConnection conn, string sql, CancellationToken ct)
        {
            using (var cmd = new NpgsqlCommand(sql, conn) { CommandTimeout = 0, AllResultTypesAreUnknown = true })
                return (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))?.ToString();
        }

        private static async Task ExecNonQueryAsync(NpgsqlConnection conn, string sql, CancellationToken ct)
        {
            using (var cmd = new NpgsqlCommand(sql, conn) { CommandTimeout = 0 })
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        /// <summary>EXPLAIN ANALYZE of a data-changing statement, always undone.</summary>
        private static async Task<string> ExplainRolledBackAsync(NpgsqlConnection conn, string explain, CancellationToken ct)
        {
            // transaction_timestamp() differs from statement_timestamp() only inside an explicit transaction block.
            var inTx = (await ScalarTextAsync(conn, "SELECT (transaction_timestamp() <> statement_timestamp())::text", ct).ConfigureAwait(false)) == "true";
            if (inTx)
            {
                await ExecNonQueryAsync(conn, "SAVEPOINT pgx_explain", ct).ConfigureAwait(false);
                try { return await ScalarTextAsync(conn, explain, ct).ConfigureAwait(false); }
                finally { await ExecNonQueryAsync(conn, "ROLLBACK TO SAVEPOINT pgx_explain; RELEASE SAVEPOINT pgx_explain", CancellationToken.None).ConfigureAwait(false); }
            }
            await ExecNonQueryAsync(conn, "BEGIN", ct).ConfigureAwait(false);
            try { return await ScalarTextAsync(conn, explain, ct).ConfigureAwait(false); }
            finally { await ExecNonQueryAsync(conn, "ROLLBACK", CancellationToken.None).ConfigureAwait(false); }
        }

        private void ShowPlans(List<Plans.StatementPlan> plans, List<string> messages, bool actual, TimeSpan elapsed)
        {
            // Results tab gets the original EXPLAIN output (one "QUERY PLAN" row per statement), as when running EXPLAIN by hand.
            var raw = new QueryRunner.Result { Elapsed = elapsed, Failed = plans.Any(p => p.Error != null) };
            foreach (var p in plans.Where(p => p.RawJson != null))
            {
                var t = new DataTable($"Query {p.Index}");
                var col = t.Columns.Add("QUERY PLAN", typeof(string));
                col.Caption = $"QUERY PLAN (query {p.Index})";
                col.ExtendedProperties["pgtype"] = "json";
                t.Rows.Add(p.RawJson);
                raw.Tables.Add(t);
                raw.TotalRows++;
            }
            raw.Messages.AddRange(messages);
            ShowResult(raw);

            if (!_tabs.TabPages.Contains(_planTab)) _tabs.TabPages.Add(_planTab);
            _planView.SetPlans(plans);
            _elapsed.Text = elapsed.ToString(@"hh\:mm\:ss");
            var failed = plans.Any(p => p.Error != null);
            _state.Text = failed ? "Plan completed with errors - see Messages." : actual ? "Actual plan captured (query executed)." : "Estimated plan ready (query not executed).";
            _state.ForeColor = failed ? Color.Firebrick : Theme.BarText;
            _tabs.SelectedTab = plans.Any(p => p.Root != null) ? _planTab : _messagesTab;
        }

        private void CloseSessions()
        {
            foreach (var s in _sessions.Values) { try { s.Dispose(); } catch { } }
            _sessions.Clear();
        }

        private void ClearResults()
        {
            foreach (Control c in _results.Controls) c.Dispose();
            _results.Controls.Clear();
        }

        private void ShowResult(QueryRunner.Result result)
        {
            _split.Panel2Collapsed = false;

            ClearResults();
            var tables = result.Tables;
            var height = tables.Count <= 1 ? 0 : Math.Max(160, (_results.ClientSize.Height - 8) / Math.Min(tables.Count, 3));
            // Add in reverse so Dock=Top stacks them in result order.
            for (var i = tables.Count - 1; i >= 0; i--)
            {
                var grid = MakeGrid(tables[i]);
                if (tables.Count == 1) grid.Dock = DockStyle.Fill;
                else { grid.Dock = DockStyle.Top; grid.Height = height; }
                _results.Controls.Add(grid);
                if (tables.Count > 1)
                {
                    _results.Controls.Add(new Splitter { Dock = DockStyle.Top, Height = 4 });
                    if (_multi || tables[i].TableName.Contains(" - ")) // label grids when it isn't obvious which is which
                        _results.Controls.Add(new Label { Text = tables[i].TableName, Dock = DockStyle.Top, Height = 18, ForeColor = Theme.GrayText, BackColor = Theme.Background });
                }
            }

            foreach (var m in result.Messages) Log(m);
            Log("");
            Log($"Completion time: {DateTime.Now:yyyy-MM-ddTHH:mm:ss.fffffffzzz}");

            _elapsed.Text = result.Elapsed.ToString(@"hh\:mm\:ss");
            _rowCount.Text = $"{result.TotalRows:N0} rows";
            _state.Text = result.Failed
                ? (_multi ? "Query completed with errors on one or more servers." : "Query completed with errors.")
                : "Query executed successfully.";
            _state.ForeColor = _multi ? Color.White : result.Failed ? Color.Firebrick : Theme.BarText;
            _tabs.SelectedTab = result.Failed && !_multi || tables.Count == 0 ? _messagesTab : _resultsTab;
        }

        private static DataGridView MakeGrid(DataTable table)
        {
            var grid = new DataGridView
            {
                ReadOnly = true,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                RowHeadersWidth = 50,
                SelectionMode = DataGridViewSelectionMode.CellSelect,
                ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableWithAutoHeaderText,
                BorderStyle = BorderStyle.None,
                AutoGenerateColumns = true,
            };
            Theme.Apply(grid);
            typeof(DataGridView).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.SetValue(grid, true);
            grid.DataBindingComplete += (s, e) =>
            {
                foreach (DataGridViewColumn c in grid.Columns)
                {
                    var dc = table.Columns[c.DataPropertyName];
                    c.HeaderText = dc.Caption;
                    c.ToolTipText = dc.ExtendedProperties["pgtype"] as string;
                    c.SortMode = DataGridViewColumnSortMode.Automatic;
                    if (dc.Caption == QueryRunner.ServerNameColumn) c.Frozen = true;
                }
                grid.AutoResizeColumns(DataGridViewAutoSizeColumnsMode.DisplayedCells);
                foreach (DataGridViewColumn c in grid.Columns) if (c.Width > 400) c.Width = 400;
            };
            grid.RowPostPaint += (s, e) =>
            {
                var text = (e.RowIndex + 1).ToString();
                TextRenderer.DrawText(e.Graphics, text, grid.Font, new Rectangle(e.RowBounds.Left, e.RowBounds.Top, grid.RowHeadersWidth - 4, e.RowBounds.Height),
                    Theme.GrayText, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            };
            var nullBack = Theme.IsDark ? Color.FromArgb(70, 66, 40) : Color.FromArgb(255, 255, 225);
            grid.CellFormatting += (s, e) =>
            {
                if (e.Value == null || e.Value == DBNull.Value)
                {
                    e.Value = "NULL";
                    e.CellStyle.BackColor = nullBack;
                    e.FormattingApplied = true;
                }
            };
            grid.DataSource = table;
            ResultGrid.Attach(grid, table);
            return grid;
        }

        private void Log(string line) => _messages.AppendText(line + Environment.NewLine);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _cts?.Cancel(); } catch { }
                CloseSessions();
            }
            base.Dispose(disposing);
        }
    }
}
