using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms.Integration;
using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Imaging;

namespace SsmsPgExplorer.RegisteredServers
{
    /// <summary>Tool window equivalent of SSMS's View > Registered Servers, for PostgreSQL.</summary>
    public sealed class RegisteredServersWindow : BaseToolWindow<RegisteredServersWindow>
    {
        public override string GetTitle(int toolWindowId) => "PostgreSQL Registered Servers";

        public override Type PaneType => typeof(Pane);

        public override Task<FrameworkElement> CreateAsync(int toolWindowId, CancellationToken cancellationToken) =>
            Task.FromResult<FrameworkElement>(new WindowsFormsHost { Child = new RegisteredServersControl() });

        [Guid("8D2B6F14-5E3A-4C71-9B08-6A4F2E1D7C95")]
        internal sealed class Pane : ToolkitToolWindowPane
        {
            public Pane()
            {
                BitmapImageMoniker = KnownMonikers.Database;
            }
        }
    }
}
