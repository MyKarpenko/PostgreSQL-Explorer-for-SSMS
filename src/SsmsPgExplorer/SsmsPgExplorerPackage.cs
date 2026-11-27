using System;
using System.Runtime.InteropServices;
using System.Threading;
using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using SsmsPgExplorer.ObjectExplorer;
using SsmsPgExplorer.Query;
using SsmsPgExplorer.RegisteredServers;
using Task = System.Threading.Tasks.Task;

namespace SsmsPgExplorer
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [InstalledProductRegistration(Vsix.Name, Vsix.Description, Vsix.Version)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideToolWindow(typeof(RegisteredServersWindow.Pane), Style = VsDockStyle.Tabbed, Window = WindowGuids.SolutionExplorer)]
    [ProvideToolWindow(typeof(QueryWindow.Pane), MultiInstances = true, Style = VsDockStyle.MDI, Transient = true)]
    [ProvideToolWindow(typeof(Activity.ActivityWindow.Pane), MultiInstances = true, Style = VsDockStyle.MDI, Transient = true)]
    // Load at startup so connected Postgres servers can be (re)attached to Object Explorer without a click.
    [ProvideAutoLoad(VSConstants.UICONTEXT.ShellInitialized_string, PackageAutoLoadFlags.BackgroundLoad)]
    [Guid(PackageGuids.guidSsmsPgExplorerPackageString)]
    public sealed class SsmsPgExplorerPackage : ToolkitPackage
    {
        internal static SsmsPgExplorerPackage Instance { get; private set; }

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            Instance = this;
            await this.RegisterCommandsAsync();
            this.RegisterToolWindows();

            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            ObjectExplorerBridge.Initialize(this);
        }
    }
}
