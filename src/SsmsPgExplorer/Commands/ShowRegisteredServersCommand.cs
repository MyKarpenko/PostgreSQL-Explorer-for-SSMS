using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Shell;
using SsmsPgExplorer.RegisteredServers;
using Task = System.Threading.Tasks.Task;

namespace SsmsPgExplorer.Commands
{
    [Command(PackageGuids.guidSsmsPgExplorerCmdSetString, PackageIds.cmdidShowRegisteredServers)]
    internal sealed class ShowRegisteredServersCommand : BaseCommand<ShowRegisteredServersCommand>
    {
        protected override Task ExecuteAsync(OleMenuCmdEventArgs e) => RegisteredServersWindow.ShowAsync();
    }
}
