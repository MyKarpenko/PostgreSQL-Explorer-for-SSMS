using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Microsoft.VisualStudio.Shell;
using SsmsPgExplorer.Connection;
using SsmsPgExplorer.Model;
using SsmsPgExplorer.ObjectExplorer;
using SsmsPgExplorer.Query;
using SsmsPgExplorer.UI;

namespace SsmsPgExplorer.RegisteredServers
{
    /// <summary>
    /// Mirrors SSMS Registered Servers: PostgreSQL > Local Server Groups > [groups] > servers.
    /// Groups are slash-separated paths on each registration ("Prod/us-west-2").
    /// </summary>
    internal sealed class RegisteredServersControl : UserControl
    {
        private readonly TreeView _tree = new TreeView { Dock = DockStyle.Fill, HideSelection = false, ShowNodeToolTips = true, BorderStyle = BorderStyle.None };
        private readonly ToolStrip _bar = new UI.ClickThroughToolStrip { GripStyle = ToolStripGripStyle.Hidden, Dock = DockStyle.Top };
        private readonly ImageList _images = new ImageList { ColorDepth = ColorDepth.Depth32Bit, ImageSize = new Size(16, 16) };
        private TreeNode _root;

        public RegisteredServersControl()
        {
            _images.Images.Add("root", Icons.Server);
            _images.Images.Add("group", Icons.Folder);
            _images.Images.Add("server", Icons.Database);
            _tree.ImageList = _images;

            // Short labels: the tool window is usually docked narrow and overflowed items are easy to miss.
            _bar.Items.Add(new ToolStripButton("New", null, (s, e) => NewRegistration(SelectedGroup)) { DisplayStyle = ToolStripItemDisplayStyle.Text, ToolTipText = "New Server Registration" });
            _bar.Items.Add(new ToolStripButton("Edit", null, (s, e) => { if (Selected != null) EditRegistration(Selected); }) { DisplayStyle = ToolStripItemDisplayStyle.Text, ToolTipText = "Edit Registration (F2)" });
            _bar.Items.Add(new ToolStripButton("Delete", null, (s, e) => DeleteSelected()) { DisplayStyle = ToolStripItemDisplayStyle.Text });
            _bar.Items.Add(new ToolStripSeparator());
            _bar.Items.Add(new ToolStripButton("Connect", null, (s, e) => ConnectSelected()) { DisplayStyle = ToolStripItemDisplayStyle.Text, ToolTipText = "Connect in Object Explorer (Enter / double-click)" });
            _bar.Items.Add(new ToolStripButton("Query", null, (s, e) => QuerySelected()) { DisplayStyle = ToolStripItemDisplayStyle.Text, ToolTipText = "New Query. On a group: multi-server query against every server in it." });
            _bar.Items.Add(new ToolStripSeparator());
            var more = new ToolStripDropDownButton("More") { DisplayStyle = ToolStripItemDisplayStyle.Text };
            more.DropDownItems.Add(ImportMenu());
            more.DropDownItems.Add("Export...", null, (s, e) => Export());
            more.DropDownItems.Add(new ToolStripSeparator());
            more.DropDownItems.Add("Open storage folder", null, (s, e) => System.Diagnostics.Process.Start("explorer.exe", ServerStore.Folder));
            more.DropDownItems.Add("Open diagnostic log", null, (s, e) =>
            {
                if (System.IO.File.Exists(Diagnostics.Log.FilePath)) System.Diagnostics.Process.Start("notepad.exe", "\"" + Diagnostics.Log.FilePath + "\"");
            });
            _bar.Items.Add(more);

            _tree.NodeMouseDoubleClick += (s, e) => { if (e.Node.Tag is ServerRegistration) ConnectSelected(); };
            _tree.NodeMouseClick += (s, e) =>
            {
                if (e.Button != MouseButtons.Right) return;
                _tree.SelectedNode = e.Node;
                var menu = ContextMenuFor(e.Node);
                Theme.ApplyMenu(menu);
                menu.Show(_tree, e.Location);
            };
            _tree.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Delete) DeleteSelected();
                else if (e.KeyCode == Keys.Enter) ConnectSelected();
                else if (e.KeyCode == Keys.F2 && Selected != null) EditRegistration(Selected);
                else if (e.KeyCode == Keys.F2 && SelectedGroup.Length > 0) RenameGroup(SelectedGroup);
            };

            // Drag a server or group onto a group (or "Local Server Groups" for top level), as in SSMS.
            _tree.AllowDrop = true;
            _tree.ItemDrag += (s, e) =>
            {
                if (e.Item is TreeNode n && (n.Tag is ServerRegistration || (n.Tag is string g && g.Length > 0)))
                    _tree.DoDragDrop(n, DragDropEffects.Move);
            };
            _tree.DragOver += (s, e) =>
            {
                var target = DropTarget(e);
                var source = e.Data.GetData(typeof(TreeNode)) as TreeNode;
                e.Effect = target != null && source != null && CanDrop(source, target) ? DragDropEffects.Move : DragDropEffects.None;
                if (target != null) _tree.SelectedNode = target;
            };
            _tree.DragDrop += (s, e) =>
            {
                var target = DropTarget(e);
                if (!(e.Data.GetData(typeof(TreeNode)) is TreeNode source) || target == null || !CanDrop(source, target)) return;
                MoveNodeTo(source, (string)target.Tag);
            };

            Controls.Add(_tree);
            Controls.Add(_bar);

            ServerStore.Changed += OnStoreChanged;
            Disposed += (s, e) => ServerStore.Changed -= OnStoreChanged;
            Theme.Attach(this);
            Reload();
        }

        private ServerRegistration Selected => _tree.SelectedNode?.Tag as ServerRegistration;

        private string SelectedGroup
        {
            get
            {
                var n = _tree.SelectedNode;
                if (n?.Tag is ServerRegistration r) return r.GroupPath;
                return n?.Tag as string ?? "";
            }
        }

        private void OnStoreChanged(object sender, EventArgs e)
        {
            if (IsDisposed) return;
            if (InvokeRequired) BeginInvoke((Action)Reload); else Reload();
        }

        private void Reload()
        {
            var selectedId = Selected?.Id;
            var expanded = new HashSet<string>(AllNodes(_tree.Nodes).Where(n => n.IsExpanded && n.Tag is string).Select(n => (string)n.Tag));

            _tree.BeginUpdate();
            try
            {
                _tree.Nodes.Clear();
                _root = new TreeNode("PostgreSQL") { ImageKey = "root", SelectedImageKey = "root", Tag = "" };
                var local = new TreeNode("Local Server Groups") { ImageKey = "group", SelectedImageKey = "group", Tag = "" };
                _root.Nodes.Add(local);
                _tree.Nodes.Add(_root);

                List<ServerRegistration> regs;
                try { regs = ServerStore.Load(); }
                catch (Exception ex)
                {
                    local.Nodes.Add(new TreeNode("Could not read registrations: " + ex.Message) { ForeColor = Color.Firebrick, ToolTipText = ServerStore.FilePath });
                    _root.Expand(); local.Expand();
                    return;
                }
                TreeNode GroupNode(string groupPath)
                {
                    var parent = local;
                    var path = "";
                    foreach (var part in groupPath.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        path = path.Length == 0 ? part.Trim() : path + "/" + part.Trim();
                        var next = parent.Nodes.Cast<TreeNode>().FirstOrDefault(n => n.Tag is string t && string.Equals(t, path, StringComparison.OrdinalIgnoreCase));
                        if (next == null)
                        {
                            next = new TreeNode(part.Trim()) { ImageKey = "group", SelectedImageKey = "group", Tag = path };
                            parent.Nodes.Add(next);
                        }
                        parent = next;
                    }
                    return parent;
                }

                // Groups first (including empty ones), alphabetically, so they sit above servers like in SSMS.
                List<string> groupPaths;
                try { groupPaths = ServerStore.AllGroupPaths(); } catch { groupPaths = new List<string>(); }
                foreach (var g in groupPaths) GroupNode(g);

                foreach (var reg in regs.OrderBy(r => r.GroupPath, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase))
                {
                    var parent = GroupNode(reg.GroupPath);
                    var node = new TreeNode(reg.DisplayName)
                    {
                        ImageKey = "server",
                        SelectedImageKey = "server",
                        Tag = reg,
                        ToolTipText = $"{reg.Host}:{reg.Port}/{reg.Database}  user={reg.Username}  auth={reg.Auth}{(reg.AllowWrites ? "  WRITES ALLOWED" : "")}"
                            + (string.IsNullOrWhiteSpace(reg.Description) ? "" : Environment.NewLine + reg.Description),
                    };
                    if (reg.AllowWrites) node.ForeColor = Color.Firebrick;
                    parent.Nodes.Add(node);
                    if (reg.Id == selectedId) _tree.SelectedNode = node;
                }

            }
            finally { _tree.EndUpdate(); }

            // Expand AFTER EndUpdate. WinForms TreeView bug: nodes expanded inside BeginUpdate/EndUpdate leave
            // the vertical scroll range stale, so the scrollbar disappears even when nodes run past the bottom.
            // (Toggling Scrollable would also fix it, but that recreates the window handle.)
            _root.Expand();
            _root.Nodes[0].Expand();
            foreach (var n in AllNodes(_tree.Nodes).Where(n => n.Tag is string t && t.Length > 0 && expanded.Contains(t)).ToList()) n.Expand();
            _tree.SelectedNode?.EnsureVisible();
        }

        private static IEnumerable<TreeNode> AllNodes(TreeNodeCollection nodes)
        {
            foreach (TreeNode n in nodes)
            {
                yield return n;
                foreach (var c in AllNodes(n.Nodes)) yield return c;
            }
        }

        private ContextMenuStrip ContextMenuFor(TreeNode node)
        {
            var m = new ContextMenuStrip();
            if (node.Tag is ServerRegistration reg)
            {
                var connect = new ToolStripMenuItem("Connect");
                connect.DropDownItems.Add("Object Explorer", null, (s, e) => ConnectSelected());
                connect.DropDownItems.Add("New Query", null, (s, e) => QuerySelected());
                connect.DropDownItems.Add("Activity Monitor", null, (s, e) =>
                    ThreadHelper.JoinableTaskFactory.RunAsync(() => Activity.ActivityWindow.OpenAsync(reg, reg.Database)).FileAndForget("SsmsPgExplorer/Activity"));
                m.Items.Add(connect);
                m.Items.Add("Test Connection", null, (s, e) => TestConnection(reg));
                m.Items.Add(new ToolStripSeparator());
                m.Items.Add(MoveToMenu(node));
                m.Items.Add("Edit Registration...", null, (s, e) => EditRegistration(reg));
                m.Items.Add("Duplicate", null, (s, e) =>
                {
                    var copy = reg.Clone();
                    copy.Id = Guid.NewGuid().ToString("N");
                    copy.Name = reg.DisplayName + " (copy)";
                    copy.RememberPassword = false;
                    ServerStore.Upsert(copy);
                });
                m.Items.Add("Forget Saved Password", null, (s, e) =>
                {
                    CredentialStore.Delete(reg.Id);
                    ConnectionFactory.Forget(reg.Id);
                    reg.RememberPassword = false;
                    ServerStore.Upsert(reg);
                }).Enabled = reg.Auth == AuthMode.Password && reg.RememberPassword;
                m.Items.Add("Delete", null, (s, e) => DeleteSelected());
            }
            else
            {
                var count = ServersUnder(SelectedGroup).Count;
                m.Items.Add($"New Query ({count} server{(count == 1 ? "" : "s")})", null, (s, e) => QuerySelected()).Enabled = count > 0;
                m.Items.Add(new ToolStripSeparator());
                m.Items.Add("New Server Registration...", null, (s, e) => NewRegistration(SelectedGroup));
                m.Items.Add("New Server Group...", null, (s, e) => NewGroup(SelectedGroup));
                if (SelectedGroup.Length > 0) // a real group, not "PostgreSQL" / "Local Server Groups"
                {
                    m.Items.Add(new ToolStripSeparator());
                    m.Items.Add(MoveToMenu(node));
                    m.Items.Add("Rename Group...", null, (s, e) => RenameGroup(SelectedGroup));
                    m.Items.Add($"Delete Group ({count} server{(count == 1 ? "" : "s")})...", null, (s, e) => DeleteGroup(SelectedGroup));
                }
                m.Items.Add(new ToolStripSeparator());
                m.Items.Add(ImportMenu());
                m.Items.Add("Export...", null, (s, e) => Export());
            }
            return m;
        }

        // ---------- actions ----------

        private void NewRegistration(string group)
        {
            var reg = new ServerRegistration { Group = group ?? "" };
            using (var dlg = new ServerDialog(reg, isNew: true))
            {
                if (dlg.ShowDialog(this) == DialogResult.OK) ServerStore.Upsert(dlg.Result);
            }
        }

        public static void EditRegistration(ServerRegistration reg)
        {
            using (var dlg = new ServerDialog(reg.Clone(), isNew: false))
            {
                if (dlg.ShowDialog() != DialogResult.OK) return;
                ConnectionFactory.Forget(reg.Id);
                ServerStore.Upsert(dlg.Result);
            }
        }

        private void DeleteSelected()
        {
            var reg = Selected;
            if (reg == null)
            {
                // Toolbar Delete / Del key on a group node deletes the group.
                if (_tree.SelectedNode != null && SelectedGroup.Length > 0) DeleteGroup(SelectedGroup);
                return;
            }
            if (MessageBox.Show(this, $"Delete registration '{reg.DisplayName}'?", Vsix.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            try { CredentialStore.Delete(reg.Id); } catch { /* nothing saved */ }
            ConnectionFactory.ForgetAll(reg.Id);
            ServerStore.Delete(reg.Id);
        }

        /// <summary>Creates an empty group under <paramref name="parent"/> (SSMS: right-click a group > New Server Group).</summary>
        private void NewGroup(string parent)
        {
            var name = Prompt("New Server Group", parent.Length == 0 ? "Group name (use / to nest further):" : $"New group under '{parent}' (use / to nest further):", "");
            if (string.IsNullOrWhiteSpace(name)) return;
            var path = ServerStore.NormalizeGroup(parent.Length == 0 ? name : parent + "/" + name);
            try
            {
                ServerStore.AddGroup(path);
                var node = FindGroupNode(path);
                if (node != null) { _tree.SelectedNode = node; node.EnsureVisible(); }
            }
            catch (Exception ex)
            {
                Diagnostics.Log.Error("new group", ex);
                MessageBox.Show(this, "Could not create the group: " + ex.Message, Vsix.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private TreeNode FindGroupNode(string path) =>
            AllNodes(_tree.Nodes).FirstOrDefault(n => n.Tag is string t && string.Equals(t, path, StringComparison.OrdinalIgnoreCase) && t.Length > 0);

        /// <summary>Deletes the group, its subgroups (empty or not) and every registration in them.</summary>
        private void DeleteGroup(string group)
        {
            var doomed = ServersUnder(group);
            var msg = doomed.Count == 0
                ? $"Delete empty group '{group}'?"
                : $"Delete group '{group}' and the {doomed.Count} registration{(doomed.Count == 1 ? "" : "s")} in it (including subgroups)?\r\n\r\n" +
                  "Saved passwords for them are removed from Windows Credential Manager. This only affects PostgreSQL Explorer; " +
                  "re-importing from DataGrip/pgAdmin/DBeaver brings them back.";
            if (MessageBox.Show(this, msg, Vsix.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;

            try
            {
                foreach (var id in ServerStore.DeleteGroup(group))
                {
                    try { CredentialStore.Delete(id); } catch { /* nothing saved */ }
                    ConnectionFactory.ForgetAll(id);
                }
            }
            catch (Exception ex)
            {
                Diagnostics.Log.Error("delete group", ex);
                MessageBox.Show(this, "Could not delete the group: " + ex.Message, Vsix.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        /// <summary>Renames a group (and everything under it, including empty subgroups).</summary>
        private void RenameGroup(string group)
        {
            var name = Prompt("Rename Group", $"New name for '{group}' (use / for nesting):", group);
            if (name == null) return;
            var target = ServerStore.NormalizeGroup(name);
            if (target.Length == 0 || string.Equals(target, group, StringComparison.Ordinal)) return;
            try { ServerStore.RepathGroup(group, target); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, Vsix.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        private void ConnectSelected()
        {
            var reg = Selected;
            if (reg == null) return;
            ThreadHelper.JoinableTaskFactory.RunAsync(() => ObjectExplorerBridge.ConnectAsync(reg)).FileAndForget("SsmsPgExplorer/Connect");
        }

        private void QuerySelected()
        {
            var reg = Selected;
            if (reg != null)
            {
                ThreadHelper.JoinableTaskFactory.RunAsync(() => QueryWindow.OpenAsync(reg, reg.Database)).FileAndForget("SsmsPgExplorer/NewQuery");
                return;
            }
            if (_tree.SelectedNode == null) return;

            // Group (or Local Server Groups / root): SSMS-style multi-server query over every server beneath it.
            var group = SelectedGroup;
            var servers = ServersUnder(group);
            if (servers.Count == 0) return;
            var label = group.Length == 0 ? "Local Server Groups" : group;
            ThreadHelper.JoinableTaskFactory.RunAsync(() => QueryWindow.OpenMultiAsync(servers, label)).FileAndForget("SsmsPgExplorer/NewMultiQuery");
        }

        // ---------- move (Move To menu + drag & drop) ----------

        /// <summary>"Move To" submenu listing every group (+ top level, + new group) for a server or group node.</summary>
        private ToolStripMenuItem MoveToMenu(TreeNode node)
        {
            var move = new ToolStripMenuItem("Move To");
            List<string> groups;
            try { groups = ServerStore.AllGroupPaths(); } catch { groups = new List<string>(); }

            void Add(string label, string group, bool enabled = true)
            {
                var item = new ToolStripMenuItem(label, null, (s, e) => MoveNodeTo(node, group)) { Enabled = enabled };
                move.DropDownItems.Add(item);
            }

            var current = node.Tag is ServerRegistration r ? r.GroupPath : ParentGroupOf((string)node.Tag);
            Add("Local Server Groups (top level)", "", current.Length > 0);
            if (groups.Count > 0) move.DropDownItems.Add(new ToolStripSeparator());
            foreach (var g in groups)
            {
                var depth = g.Count(ch => ch == '/');
                var label = new string(' ', depth * 3) + ServerStore.LeafName(g);
                var valid = CanMoveTo(node, g);
                Add(label, g, valid && !string.Equals(g, current, StringComparison.OrdinalIgnoreCase));
            }
            move.DropDownItems.Add(new ToolStripSeparator());
            move.DropDownItems.Add("New Group...", null, (s, e) =>
            {
                var name = Prompt("Move To New Group", "New group path (use / for nesting):", current.Length == 0 ? "" : current + "/");
                if (!string.IsNullOrWhiteSpace(name)) MoveNodeTo(node, name);
            });
            // Long group lists (e.g. after a DataGrip import) scroll instead of running off the screen.
            if (move.DropDown is ToolStripDropDownMenu dd) dd.MaximumSize = new Size(0, Screen.FromControl(this).WorkingArea.Height * 2 / 3);
            return move;
        }

        private static string ParentGroupOf(string group)
        {
            var g = ServerStore.NormalizeGroup(group);
            var i = g.LastIndexOf('/');
            return i < 0 ? "" : g.Substring(0, i);
        }

        /// <summary>A group can't move into itself or its own subgroups; servers can go anywhere.</summary>
        private static bool CanMoveTo(TreeNode node, string targetGroup)
        {
            if (!(node.Tag is string g) || g.Length == 0) return true;
            return !string.Equals(targetGroup, g, StringComparison.OrdinalIgnoreCase)
                   && !targetGroup.StartsWith(g + "/", StringComparison.OrdinalIgnoreCase);
        }

        private void MoveNodeTo(TreeNode node, string targetGroup)
        {
            try
            {
                var target = ServerStore.NormalizeGroup(targetGroup);
                if (node.Tag is ServerRegistration reg)
                {
                    ServerStore.MoveServers(new[] { reg.Id }, target);
                }
                else if (node.Tag is string group && group.Length > 0)
                {
                    var leaf = ServerStore.LeafName(group);
                    ServerStore.RepathGroup(group, target.Length == 0 ? leaf : target + "/" + leaf);
                }
            }
            catch (Exception ex)
            {
                Diagnostics.Log.Error("move", ex);
                MessageBox.Show(this, ex.Message, Vsix.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private TreeNode DropTarget(DragEventArgs e)
        {
            var n = _tree.GetNodeAt(_tree.PointToClient(new Point(e.X, e.Y)));
            if (n == null) return null;
            if (n.Tag is ServerRegistration) n = n.Parent;   // dropping on a server = its group
            return n != null && n.Tag is string ? n : null;
        }

        private static bool CanDrop(TreeNode source, TreeNode target)
        {
            if (ReferenceEquals(source, target)) return false;
            var targetGroup = (string)target.Tag;
            if (source.Tag is ServerRegistration r) return !string.Equals(r.GroupPath, targetGroup, StringComparison.OrdinalIgnoreCase);
            return CanMoveTo(source, targetGroup) && !string.Equals(ParentGroupOf((string)source.Tag), targetGroup, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Registrations in <paramref name="group"/> and all nested groups ("" = everything).</summary>
        private static List<ServerRegistration> ServersUnder(string group) =>
            ServerStore.Load()
                .Where(r => group.Length == 0
                            || string.Equals(r.GroupPath, group, StringComparison.OrdinalIgnoreCase)
                            || r.GroupPath.StartsWith(group + "/", StringComparison.OrdinalIgnoreCase))
                .OrderBy(r => r.GroupPath, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();

        internal static void TestConnection(ServerRegistration reg, IWin32Window owner = null)
        {
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                string result;
                var icon = MessageBoxIcon.Information;
                try
                {
                    var v = await PgCatalog.ServerVersionAsync(reg, System.Threading.CancellationToken.None);
                    result = $"Connected: PostgreSQL {v}";
                }
                catch (Exception ex)
                {
                    result = "Connection failed:" + Environment.NewLine + ConnectionFactory.Describe(ex);
                    icon = MessageBoxIcon.Error;
                }
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                MessageBox.Show(owner, result, reg.DisplayName, MessageBoxButtons.OK, icon);
            }).FileAndForget("SsmsPgExplorer/Test");
        }

        // ---------- import ----------

        private ToolStripMenuItem ImportMenu()
        {
            var import = new ToolStripMenuItem("Import Registered Servers");
            import.DropDownItems.Add("pgAdmin 4 (servers.json)...", null, (s, e) => ImportPgAdmin());
            import.DropDownItems.Add("DataGrip...", null, (s, e) => ImportDataGrip());
            import.DropDownItems.Add("DBeaver...", null, (s, e) => ImportDBeaver());
            import.DropDownItems.Add(new ToolStripSeparator());
            import.DropDownItems.Add("PostgreSQL Explorer file (.json)...", null, (s, e) => ImportOwnFile());
            return import;
        }

        private void ImportPgAdmin()
        {
            using (var ofd = new OpenFileDialog
            {
                Filter = "pgAdmin servers export (*.json)|*.json|All files (*.*)|*.*",
                Title = "pgAdmin 4: Tools > Import/Export Servers > Export, then pick the file",
            })
            {
                if (ofd.ShowDialog(this) != DialogResult.OK) return;
                RunImport("pgAdmin", new[] { ofd.FileName }, Import.Importers.ReadPgAdmin);
            }
        }

        private void ImportDataGrip()
        {
            var files = Import.Importers.FindDataGripSources();
            if (files.Count == 0)
            {
                using (var ofd = new OpenFileDialog { Filter = "DataGrip dataSources.xml|dataSources.xml", Title = "No DataGrip project found - pick <project>\\.idea\\dataSources.xml" })
                {
                    if (ofd.ShowDialog(this) != DialogResult.OK) return;
                    files.Add(ofd.FileName);
                }
            }
            RunImport("DataGrip", files, Import.Importers.ReadDataGrip);
        }

        private void ImportDBeaver()
        {
            var files = Import.Importers.FindDBeaverSources();
            if (files.Count == 0)
            {
                using (var ofd = new OpenFileDialog { Filter = "DBeaver data-sources*.json|data-sources*.json", Title = "No DBeaver workspace found - pick <project>\\.dbeaver\\data-sources.json" })
                {
                    if (ofd.ShowDialog(this) != DialogResult.OK) return;
                    files.Add(ofd.FileName);
                }
            }
            RunImport("DBeaver", files, Import.Importers.ReadDBeaver);
        }

        private void ImportOwnFile()
        {
            using (var ofd = new OpenFileDialog { Filter = "PostgreSQL Explorer servers (*.json)|*.json", Title = "Import registered servers" })
            {
                if (ofd.ShowDialog(this) != DialogResult.OK) return;
                RunImport("file", new[] { ofd.FileName }, f => ServerStore.Read(f)
                    .Select(r => new Import.ImportCandidate { Registration = r, Source = f, Note = "" }).ToList());
            }
        }

        private void RunImport(string tool, IEnumerable<string> files, Func<string, List<Import.ImportCandidate>> reader)
        {
            var candidates = new List<Import.ImportCandidate>();
            var errors = new List<string>();
            var sources = files.ToList();
            foreach (var f in sources)
            {
                try { candidates.AddRange(reader(f)); }
                catch (Exception ex) { errors.Add($"{f}: {ex.Message}"); Diagnostics.Log.Error($"import {tool} {f}", ex); }
            }
            // The same data source can appear in several projects; keep the first.
            candidates = candidates.GroupBy(c => c.Registration.Id).Select(g => g.First()).ToList();

            if (candidates.Count == 0)
            {
                MessageBox.Show(this, $"No PostgreSQL connections found in {tool}." +
                    (errors.Count > 0 ? Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, errors) : ""),
                    Vsix.Name, MessageBoxButtons.OK, errors.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
                return;
            }

            try
            {
                var existing = new HashSet<string>(ServerStore.Load().Select(r => r.Id), StringComparer.OrdinalIgnoreCase);
                using (var dlg = new Import.ImportDialog(tool, candidates, sources, existing))
                {
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;
                    var picked = dlg.Selected;
                    if (picked.Count == 0) return;
                    var (added, updated) = Import.Importers.Merge(picked.Select(c => c.Registration));
                    MessageBox.Show(this, $"{tool}: {added} added, {updated} updated." +
                        (errors.Count > 0 ? Environment.NewLine + "Skipped: " + string.Join("; ", errors) : ""), Vsix.Name);
                }
            }
            catch (Exception ex)
            {
                Diagnostics.Log.Error($"import {tool}", ex);
                MessageBox.Show(this, "Import failed: " + ex.Message, Vsix.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void Export()
        {
            using (var sfd = new SaveFileDialog { Filter = "PostgreSQL Explorer servers (*.json)|*.json", FileName = "pg-registered-servers.json", Title = "Export registered servers (no passwords)" })
            {
                if (sfd.ShowDialog(this) != DialogResult.OK) return;
                var list = ServerStore.Load().Select(r => { var c = r.Clone(); c.RememberPassword = false; return c; });
                ServerStore.Write(sfd.FileName, list);
            }
        }

        internal static string Prompt(string title, string label, string value)
        {
            using (var f = new Form { Text = title, Width = 380, Height = 140, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false })
            {
                var l = new Label { Text = label, Left = 10, Top = 10, Width = 340 };
                var t = new TextBox { Text = value, Left = 10, Top = 32, Width = 340 };
                var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 194, Top = 64, Width = 75 };
                var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 275, Top = 64, Width = 75 };
                f.Controls.AddRange(new Control[] { l, t, ok, cancel });
                f.AcceptButton = ok; f.CancelButton = cancel;
                return f.ShowDialog() == DialogResult.OK ? t.Text : null;
            }
        }
    }
}
