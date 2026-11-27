using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms.Integration;
using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Shell;
using SsmsPgExplorer.Model;

namespace SsmsPgExplorer.Activity
{
    /// <summary>One Activity Monitor document per server (like SSMS's Activity Monitor).</summary>
    public sealed class ActivityWindow : BaseToolWindow<ActivityWindow>
    {
        internal sealed class Context { public ServerRegistration Server; public string Database; }

        private static int _next;
        private static readonly ConcurrentDictionary<int, Context> Pending = new ConcurrentDictionary<int, Context>();

        internal static async Task OpenAsync(ServerRegistration server, string database)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var id = 5000 + Interlocked.Increment(ref _next);
            Pending[id] = new Context { Server = server, Database = string.IsNullOrWhiteSpace(database) ? server.Database : database };
            await ShowAsync(id, create: true);
        }

        public override string GetTitle(int toolWindowId) =>
            Pending.TryGetValue(toolWindowId, out var c) ? $"Activity Monitor - {c.Server.DisplayName}" : "PostgreSQL Activity Monitor";

        public override Type PaneType => typeof(Pane);

        public override Task<FrameworkElement> CreateAsync(int toolWindowId, CancellationToken cancellationToken)
        {
            if (!Pending.TryRemove(toolWindowId, out var ctx))
                return Task.FromResult<FrameworkElement>(new System.Windows.Controls.TextBlock
                {
                    Text = "Restored from a previous session. Open Activity Monitor again from Registered Servers or Object Explorer.",
                    Margin = new Thickness(12), TextWrapping = TextWrapping.Wrap,
                });
            return Task.FromResult<FrameworkElement>(new WindowsFormsHost { Child = new ActivityControl(ctx.Server, ctx.Database) });
        }

        [Guid("E5A1C7D3-4B92-4F18-9C6E-2D8B7A3F5E19")]
        internal sealed class Pane : ToolkitToolWindowPane
        {
            public Pane() { BitmapImageMoniker = KnownMonikers.Activity; }
        }
    }
}
