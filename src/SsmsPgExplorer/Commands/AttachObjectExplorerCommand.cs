using System.Windows.Forms;
using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Shell;
using SsmsPgExplorer.ObjectExplorer;
using Task = System.Threading.Tasks.Task;
using MessageBox = System.Windows.Forms.MessageBox;

namespace SsmsPgExplorer.Commands
{
    /// <summary>Manual retry when SSMS created Object Explorer after the auto-attach gave up, or OE was rebuilt.</summary>
    [Command(PackageGuids.guidSsmsPgExplorerCmdSetString, PackageIds.cmdidAttachObjectExplorer)]
    internal sealed class AttachObjectExplorerCommand : BaseCommand<AttachObjectExplorerCommand>
    {
        protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var result = ObjectExplorerBridge.TryAttach(force: true);
            MessageBox.Show(result, Vsix.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
