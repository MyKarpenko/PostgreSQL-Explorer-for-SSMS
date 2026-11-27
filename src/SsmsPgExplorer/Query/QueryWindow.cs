using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms.Integration;
using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Shell;
using SsmsPgExplorer.Model;

namespace SsmsPgExplorer.Query
{
    /// <summary>Multi-instance document-well tool window: one per query tab, like SSMS's SQLQueryN.sql.</summary>
    public sealed class QueryWindow : BaseToolWindow<QueryWindow>
    {
        internal sealed class Context
        {
            /// <summary>One server = normal query window; several = multi-server query (SSMS group query).</summary>
            public List<ServerRegistration> Servers;
            /// <summary>Single: database name. Multi: null = each registration's initial database.</summary>
            public string Database;
            public string GroupLabel;
            public string InitialText;
            public bool Execute;
            public int Number;

            public bool IsMulti => Servers.Count > 1 || GroupLabel != null;
        }

        private static int _next;
        private static readonly ConcurrentDictionary<int, Context> Pending = new ConcurrentDictionary<int, Context>();

        internal static Task OpenAsync(ServerRegistration server, string database, string text = null, bool execute = false) =>
            OpenAsync(new Context
            {
                Servers = new List<ServerRegistration> { server },
                Database = string.IsNullOrWhiteSpace(database) ? server.Database : database,
                InitialText = text,
                Execute = execute,
            });

        /// <summary>Opens a query window that runs every batch against all <paramref name="servers"/> in parallel.</summary>
        internal static Task OpenMultiAsync(IEnumerable<ServerRegistration> servers, string groupLabel, string text = null) =>
            OpenAsync(new Context
            {
                Servers = servers.ToList(),
                GroupLabel = string.IsNullOrWhiteSpace(groupLabel) ? "Local Server Groups" : groupLabel,
                InitialText = text,
            });

        private static async Task OpenAsync(Context ctx)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (ctx.Servers.Count == 0) return;
            // Tool window ids must be unique per instance; start high so they never collide with restored ids.
            var id = 1000 + Interlocked.Increment(ref _next);
            ctx.Number = _next;
            Pending[id] = ctx;
            await ShowAsync(id, create: true);
        }

        public override string GetTitle(int toolWindowId)
        {
            if (!Pending.TryGetValue(toolWindowId, out var c)) return "PostgreSQL Query";
            return c.IsMulti
                ? $"PgQuery{c.Number} - {c.GroupLabel} ({c.Servers.Count} servers)"
                : $"PgQuery{c.Number} - {c.Servers[0].DisplayName}.{c.Database}";
        }

        public override Type PaneType => typeof(Pane);

        public override Task<FrameworkElement> CreateAsync(int toolWindowId, CancellationToken cancellationToken)
        {
            if (!Pending.TryRemove(toolWindowId, out var ctx))
                return Task.FromResult<FrameworkElement>(new System.Windows.Controls.TextBlock
                {
                    Text = "This query window was restored from a previous session. Open a new one from PostgreSQL Registered Servers or Object Explorer.",
                    Margin = new Thickness(12),
                    TextWrapping = TextWrapping.Wrap,
                });

            return Task.FromResult<FrameworkElement>(new WindowsFormsHost { Child = new QueryControl(ctx) });
        }

        [Guid("C47E9A12-8B5D-4F36-A0E1-3D6B2C9F8E47")]
        internal sealed class Pane : ToolkitToolWindowPane
        {
            public Pane()
            {
                BitmapImageMoniker = KnownMonikers.NewQuery;
            }
        }
    }
}
