using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.SqlServer.Management.UI.VSIntegration.ObjectExplorer;
using Microsoft.VisualStudio.Shell;
using SsmsPgExplorer.Connection;
using SsmsPgExplorer.Model;
using Microsoft.VisualStudio.Threading;

namespace SsmsPgExplorer.ObjectExplorer
{
    /// <summary>
    /// Base for every PostgreSQL node living inside SSMS's Object Explorer tree.
    /// Subclasses SSMS's own HierarchyTreeNode so OE's tree code treats it like a native node;
    /// children load lazily and off the UI thread.
    /// </summary>
    [Serializable]
    internal abstract class PgNode : HierarchyTreeNode, INodeWithMenu
    {
        private bool _loaded;
        private bool _loading;
        private CancellationTokenSource _cts;

        protected PgNode(ServerRegistration server, string database, string text)
        {
            Server = server;
            Database = database;
            Text = text;
            Name = text;
            if (CanHaveChildren)
            {
                Expandable = true; // SSMS's LazyNode adds its own "expanding..." placeholder for the [+]
                if (Nodes.Count == 0) Nodes.Add(new PgMessageNode(server, database, "Loading..."));
            }
        }

        protected PgNode(SerializationInfo info, StreamingContext context) : base(info, context) { }

        public ServerRegistration Server { get; }
        public string Database { get; }

        protected virtual bool CanHaveChildren => true;

        protected abstract Icon NodeIcon { get; }
        public override Icon Icon => NodeIcon;
        public override Icon SelectedIcon => NodeIcon;
        public override int State => 0;
        public override bool ShowPolicyHealthState { get => false; set { } }

        /// <summary>Called by OE's LazyTreeView on expand (and by our BeforeExpand hook as a fallback). Idempotent.</summary>
        public override void EnumerateChildren()
        {
            if (!CanHaveChildren || _loaded || _loading) return;
            _loading = true;
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;

            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                IList<PgNode> children = null;
                Exception error = null;
                try
                {
                    await TaskScheduler.Default;
                    children = (await LoadChildrenAsync(ct).ConfigureAwait(false))?.ToList() ?? new List<PgNode>();
                }
                catch (Exception ex) { error = ex; }

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                _loading = false;
                if (ct.IsCancellationRequested) return;

                var tree = TreeView;
                tree?.BeginUpdate();
                try
                {
                    Nodes.Clear();
                    if (error != null)
                    {
                        Nodes.Add(new PgMessageNode(Server, Database, "Error: " + ConnectionFactory.Describe(error)));
                        OnLoadFailed(error);
                    }
                    else
                    {
                        _loaded = true;
                        if (children.Count == 0) Nodes.Add(new PgMessageNode(Server, Database, "(empty)"));
                        else ObjectExplorerBridge.AddChildren(this, children);
                    }
                }
                finally { tree?.EndUpdate(); }
                if (!IsExpanded) Expand();
            }).FileAndForget("SsmsPgExplorer/EnumerateChildren");
        }

        public override void CancelExpand()
        {
            _cts?.Cancel();
            _loading = false;
        }

        public void RefreshNode()
        {
            _cts?.Cancel();
            _loaded = false;
            _loading = false;
            if (!CanHaveChildren) return;
            Nodes.Clear();
            Nodes.Add(new PgMessageNode(Server, Database, "Loading..."));
            EnumerateChildren();
        }

        protected virtual Task<IEnumerable<PgNode>> LoadChildrenAsync(CancellationToken ct) =>
            Task.FromResult(Enumerable.Empty<PgNode>());

        protected virtual void OnLoadFailed(Exception error) { }

        // ---------- context menu ----------

        private static DateTime _lastMenuShown;

        public void ShowContextMenu(Point screenPos)
        {
            // OE (INodeWithMenu) and our NodeMouseClick fallback can both fire for one click.
            if ((DateTime.UtcNow - _lastMenuShown).TotalMilliseconds < 400) return;
            _lastMenuShown = DateTime.UtcNow;

            var menu = new ContextMenuStrip();
            BuildMenu(menu.Items);
            if (menu.Items.Count > 0 && !(menu.Items[menu.Items.Count - 1] is ToolStripSeparator))
                menu.Items.Add(new ToolStripSeparator());
            if (CanHaveChildren) menu.Items.Add("Refresh", null, (s, e) => RefreshNode());
            menu.Items.Add("Copy name", null, (s, e) => Clipboard.SetText(CopyText));
            UI.Theme.ApplyMenu(menu);
            menu.Closed += (s, e) => BeginDispose(menu);
            menu.Show(screenPos);
        }

        public virtual void DoDefaultAction()
        {
            if (CanHaveChildren) { if (IsExpanded) Collapse(); else Expand(); }
        }

        protected virtual void BuildMenu(ToolStripItemCollection items) { }

        protected virtual string CopyText => Text;

        protected static void Run(Func<Task> action)
        {
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                try { await action(); }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    MessageBox.Show(ConnectionFactory.Describe(ex), Vsix.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }).FileAndForget("SsmsPgExplorer/MenuAction");
        }

        private static void BeginDispose(ContextMenuStrip menu)
        {
            // Dispose after the click handler has run.
            var t = new System.Windows.Forms.Timer { Interval = 500 };
            t.Tick += (s, e) => { t.Dispose(); menu.Dispose(); };
            t.Start();
        }

        public override object Clone() => MemberwiseClone();
    }

    /// <summary>Leaf used for "Loading...", "(empty)" and error rows.</summary>
    [Serializable]
    internal sealed class PgMessageNode : PgNode
    {
        public PgMessageNode(ServerRegistration server, string database, string text) : base(server, database, text)
        {
            ForeColor = SystemColors.GrayText;
            ToolTipText = text;
        }

        private PgMessageNode(SerializationInfo info, StreamingContext context) : base(info, context) { }

        protected override bool CanHaveChildren => false;
        protected override Icon NodeIcon => Icons.Message;
    }
}
