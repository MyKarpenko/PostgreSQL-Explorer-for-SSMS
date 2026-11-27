using System.Linq;
using System.Windows.Forms;
using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Shell;
using SsmsPgExplorer.Model;
using SsmsPgExplorer.ObjectExplorer;
using SsmsPgExplorer.Query;
using SsmsPgExplorer.RegisteredServers;
using Task = System.Threading.Tasks.Task;
using MessageBox = System.Windows.Forms.MessageBox;

namespace SsmsPgExplorer.Commands
{
    /// <summary>
    /// Opens a query window for the Postgres node selected in Object Explorer; otherwise asks which registered server.
    /// </summary>
    [Command(PackageGuids.guidSsmsPgExplorerCmdSetString, PackageIds.cmdidNewPgQuery)]
    internal sealed class NewPgQueryCommand : BaseCommand<NewPgQueryCommand>
    {
        protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (ObjectExplorerBridge.SelectedPgNode is PgNode node)
            {
                await QueryWindow.OpenAsync(node.Server, node.Database);
                return;
            }

            var servers = ServerStore.Load();
            if (servers.Count == 0)
            {
                MessageBox.Show("No PostgreSQL servers are registered yet. Opening PostgreSQL Registered Servers.",
                    Vsix.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
                await RegisteredServersWindow.ShowAsync();
                return;
            }

            using (var picker = new ServerPicker(servers))
            {
                if (picker.ShowDialog() == DialogResult.OK && picker.Selected != null)
                    await QueryWindow.OpenAsync(picker.Selected, picker.Selected.Database);
            }
        }

        private sealed class ServerPicker : Form
        {
            private readonly ListBox _list = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
            public ServerRegistration Selected => _list.SelectedItem as ServerRegistration;

            public ServerPicker(System.Collections.Generic.IList<ServerRegistration> servers)
            {
                Text = "Connect to PostgreSQL";
                Width = 420; Height = 360;
                StartPosition = FormStartPosition.CenterParent;
                FormBorderStyle = FormBorderStyle.SizableToolWindow;
                _list.Items.AddRange(servers.OrderBy(s => s.Group).ThenBy(s => s.Name).Cast<object>().ToArray());
                _list.SelectedIndex = 0;
                _list.DoubleClick += (s, a) => { DialogResult = DialogResult.OK; };
                var ok = new Button { Text = "Connect", DialogResult = DialogResult.OK, Dock = DockStyle.Bottom, Height = 30 };
                Controls.Add(_list);
                Controls.Add(ok);
                AcceptButton = ok;
            }
        }
    }
}
