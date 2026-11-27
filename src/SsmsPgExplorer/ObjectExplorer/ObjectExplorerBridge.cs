using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Community.VisualStudio.Toolkit;
using Microsoft.SqlServer.Management.UI.VSIntegration.ObjectExplorer;
using Microsoft.VisualStudio.Shell;
using SsmsPgExplorer.Connection;
using SsmsPgExplorer.Model;
using MessageBox = System.Windows.Forms.MessageBox;

namespace SsmsPgExplorer.ObjectExplorer
{
    /// <summary>
    /// Finds SSMS's Object Explorer tree and mounts PostgreSQL server nodes into it.
    ///
    /// UNSUPPORTED SURFACE: SSMS has no public API for third-party server types in Object Explorer
    /// (ConnectionDialogServerType is a closed enum). We locate the LazyTreeView behind
    /// IObjectExplorerService by reflection and add HierarchyTreeNode subclasses to it.
    /// Expect this to need adjusting after SSMS updates — everything here fails soft.
    /// </summary>
    internal static class ObjectExplorerBridge
    {
        private static AsyncPackage _package;
        // SSMS's LazyTreeView is internal: hold it as TreeView and call its helpers by reflection.
        private static TreeView _tree;
        private static MethodInfo _addRootNode, _addNodes, _addIcon;
        private static Timer _retry;
        private static int _attempts;
        private static readonly Dictionary<string, PgServerNode> Connected = new Dictionary<string, PgServerNode>();

        public static string LastStatus { get; private set; } = "not attached yet";

        public static bool IsAttached => _tree != null && !_tree.IsDisposed;

        public static PgNode SelectedPgNode
        {
            get
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                return IsAttached ? _tree.SelectedNode as PgNode : null;
            }
        }

        public static void Initialize(AsyncPackage package)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _package = package;
            // OE's control is created lazily by SSMS; poll for ~2 minutes, then wait for a manual re-attach.
            _retry = new Timer { Interval = 2000 };
            _retry.Tick += (s, e) =>
            {
                if (TryAttach(force: false, allowCreate: false) == null || ++_attempts > 60) { _retry.Stop(); }
            };
            _retry.Start();
        }

        /// <summary>Returns null when attached; otherwise a human-readable reason.</summary>
        /// <param name="force">Re-resolve even when attached, and report success as text.</param>
        /// <param name="allowCreate">May read ObjectExplorerService.Tree, which can create/show the OE window. The startup poll passes false.</param>
        public static string TryAttach(bool force, bool allowCreate = true)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (IsAttached && !force) return null;

            try
            {
                var svc = Package.GetGlobalService(typeof(IObjectExplorerService));
                if (svc == null) return LastStatus = "IObjectExplorerService is not available (is this SSMS?).";

                var tree = ResolveTree(svc, allowCreate, out var trail);
                if (tree == null) return LastStatus = "Object Explorer tree not found (" + trail + "). Open Object Explorer (F8), then use Tools > Re-attach PostgreSQL to Object Explorer.";

                if (!ReferenceEquals(tree, _tree))
                {
                    Detach();
                    _tree = tree;
                    const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                    var tt = tree.GetType();
                    _addRootNode = tt.GetMethod("AddRootNode", inst, null, new[] { typeof(TreeNode) }, null);
                    _addNodes = tt.GetMethod("AddNode", inst, null, new[] { typeof(TreeNode), typeof(TreeNode[]) }, null);
                    _addIcon = tt.GetMethod("AddIconToImageMap", inst, null, new[] { typeof(TreeNode) }, null);
                    _tree.BeforeExpand += OnBeforeExpand;
                    _tree.NodeMouseClick += OnNodeMouseClick;
                    _tree.Disposed += (s, e) => { _tree = null; Connected.Clear(); };
                }
                LastStatus = "attached";
                return force ? "PostgreSQL is attached to Object Explorer." : null;
            }
            catch (Exception ex)
            {
                return LastStatus = "Attach failed: " + ex.Message;
            }
        }

        private static void Detach()
        {
            if (_tree == null) return;
            _tree.BeforeExpand -= OnBeforeExpand;
            _tree.NodeMouseClick -= OnNodeMouseClick;
            _tree = null;
        }

        /// <summary>
        /// SSMS 22 layout (Microsoft.SqlServer.Management.SqlStudio.Explorer):
        ///   GetGlobalService(IObjectExplorerService) -> ObjectExplorerServiceDisp (automation proxy) .owner
        ///   -> ObjectExplorerService { ObjectExplorerControl Tree { get; } }  (backing field objectExplorerTree is lazy, often null)
        ///   -> or GetObjectExplorerWindowFrame() -> ObjectExplorerToolWindow.Control
        /// </summary>
        private static TreeView ResolveTree(object svc, bool allowCreate, out string trail)
        {
            const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var steps = new List<string> { svc.GetType().Name };

            // 1. Unwrap the automation proxy.
            var real = svc;
            for (var i = 0; i < 3; i++)
            {
                var owner = real.GetType().GetField("owner", inst)?.GetValue(real);
                if (owner == null || ReferenceEquals(owner, real)) break;
                real = owner;
                steps.Add("owner=" + real.GetType().Name);
            }

            // 2. Backing field (no side effects).
            if (real.GetType().GetField("objectExplorerTree", inst)?.GetValue(real) is TreeView f && IsLazyTreeView(f.GetType()))
            { trail = string.Join(" > ", steps); return f; }
            steps.Add("field=null");

            if (allowCreate)
            {
                // 3. Tree property: SSMS resolves (and if needed creates) the OE tool window.
                try
                {
                    if (real.GetType().GetProperty("Tree", inst)?.GetValue(real) is TreeView p && IsLazyTreeView(p.GetType()))
                    { trail = string.Join(" > ", steps); return p; }
                    steps.Add("Tree=null");
                }
                catch (Exception ex) { steps.Add("Tree threw " + (ex.InnerException ?? ex).GetType().Name); }

                // 4. Window frame -> ObjectExplorerToolWindow.Control.
                try
                {
                    var frame = real.GetType().GetMethod("GetObjectExplorerWindowFrame", inst, null, Type.EmptyTypes, null)?.Invoke(real, null)
                        as Microsoft.VisualStudio.Shell.Interop.IVsWindowFrame;
                    if (frame != null)
                    {
                        frame.Show();
                        frame.GetProperty((int)Microsoft.VisualStudio.Shell.Interop.__VSFPROPID.VSFPROPID_DocView, out var pane);
                        if (pane?.GetType().GetProperty("Control", inst)?.GetValue(pane) is TreeView c && IsLazyTreeView(c.GetType()))
                        { trail = string.Join(" > ", steps); return c; }
                        steps.Add("frame pane=" + (pane?.GetType().Name ?? "null"));
                    }
                    else steps.Add("no frame");
                }
                catch (Exception ex) { steps.Add("frame threw " + (ex.InnerException ?? ex).GetType().Name); }
            }

            // 5. Last resort: walk the object graph.
            var walked = FindTree(real, depth: 3, seen: new HashSet<object>());
            if (walked == null) steps.Add("walk=none");
            trail = string.Join(" > ", steps);
            return walked;
        }

        private static bool IsLazyTreeView(Type t)
        {
            for (; t != null; t = t.BaseType)
                if (t.FullName == "Microsoft.SqlServer.Management.UI.VSIntegration.ObjectExplorer.LazyTreeView") return true;
            return false;
        }

        private static TreeView FindTree(object o, int depth, HashSet<object> seen)
        {
            if (o == null || !seen.Add(o)) return null;
            if (o is TreeView t && IsLazyTreeView(t.GetType())) return t;
            if (depth == 0) return null;

            for (var type = o.GetType(); type != null && type != typeof(object); type = type.BaseType)
            {
                foreach (var f in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (f.FieldType.IsValueType || f.FieldType == typeof(string)) continue;
                    var ns = f.FieldType.Namespace ?? "";
                    // Only walk SSMS's own object graph (and anything typed as a TreeView).
                    if (!typeof(TreeView).IsAssignableFrom(f.FieldType) && !ns.StartsWith("Microsoft.SqlServer", StringComparison.Ordinal) && f.FieldType != typeof(object))
                        continue;
                    object v;
                    try { v = f.GetValue(o); } catch { continue; }
                    var hit = FindTree(v, depth - 1, seen);
                    if (hit != null) return hit;
                }
            }
            return null;
        }

        // ---------- connect / disconnect ----------

        public static async System.Threading.Tasks.Task ConnectAsync(ServerRegistration reg)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            // Bring Object Explorer up first so its tree exists.
            try { await VS.Commands.ExecuteAsync("View.ObjectExplorer"); } catch { /* command name differs / not present */ }

            var status = TryAttach(force: false);
            if (status != null)
            {
                var open = MessageBox.Show("Could not reach Object Explorer: " + status + Environment.NewLine + Environment.NewLine +
                                "Open a query window for this server instead?",
                    Vsix.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;
                if (open) await Query.QueryWindow.OpenAsync(reg, reg.Database);
                return;
            }

            if (Connected.TryGetValue(reg.Id, out var existing) && existing.TreeView == _tree)
            {
                _tree.SelectedNode = existing;
                existing.EnsureVisible();
                return;
            }

            var node = new PgServerNode(reg);
            Connected[reg.Id] = node;
            if (!Invoke(_addRootNode, node)) _tree.Nodes.Add(node);
            SafeAddIcon(node);
            _tree.SelectedNode = node;
            node.Expand();
            node.EnumerateChildren();
        }

        public static void Disconnect(PgServerNode node)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            node.CancelExpand();
            Connected.Remove(node.Server.Id);
            node.Remove();
            ConnectionFactory.Forget(node.Server.Id);
            Npgsql.NpgsqlConnection.ClearAllPools();
        }

        public static void AddChildren(PgNode parent, IList<PgNode> children)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var arr = children.Cast<TreeNode>().ToArray();
            if (!(IsAttached && parent.TreeView == _tree && Invoke(_addNodes, parent, arr)))
                parent.Nodes.AddRange(arr);
            foreach (var c in children) SafeAddIcon(c);
        }

        public static void RefreshIcon(PgNode node)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            SafeAddIcon(node);
        }

        private static void SafeAddIcon(PgNode node)
        {
            if (!IsAttached || node.TreeView != _tree) return;
            Invoke(_addIcon, node); // icon is cosmetic; failure is fine
        }

        private static bool Invoke(MethodInfo m, params object[] args)
        {
            if (m == null) return false;
            try { m.Invoke(_tree, args); return true; }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"SsmsPgExplorer: {m.Name} failed: {ex.InnerException?.Message ?? ex.Message}");
                return false;
            }
        }

        // ---------- fallbacks in case OE ignores our node interfaces ----------

        private static void OnBeforeExpand(object sender, TreeViewCancelEventArgs e)
        {
            if (e.Node is PgNode pg) pg.EnumerateChildren();
        }

        private static void OnNodeMouseClick(object sender, TreeNodeMouseClickEventArgs e)
        {
            if (e.Button != MouseButtons.Right || !(e.Node is PgNode pg)) return;
            _tree.SelectedNode = pg;
            pg.ShowContextMenu(_tree.PointToScreen(e.Location));
        }
    }
}
