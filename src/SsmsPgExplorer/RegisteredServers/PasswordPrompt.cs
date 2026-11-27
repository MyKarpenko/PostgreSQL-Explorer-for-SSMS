using System.Drawing;
using System.Windows.Forms;
using SsmsPgExplorer.Model;

namespace SsmsPgExplorer.RegisteredServers
{
    internal sealed class PasswordPrompt : Form
    {
        private readonly TextBox _pw = new TextBox { UseSystemPasswordChar = true, Dock = DockStyle.Fill };
        private readonly CheckBox _remember = new CheckBox { Text = "Remember password", AutoSize = true };

        public string Password => _pw.Text;
        public bool Remember => _remember.Checked;

        public PasswordPrompt(ServerRegistration reg)
        {
            Text = "Connect to PostgreSQL";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false; TopMost = true;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            var scale = DeviceDpi / 96f;

            var layout = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Padding = new Padding(12), Dock = DockStyle.Fill };
            layout.Controls.Add(new Label { Text = $"{reg.Username}@{reg.Host}:{reg.Port}  ({reg.DisplayName})", AutoSize = true, MaximumSize = new Size((int)(380 * scale), 0) });
            layout.Controls.Add(new Label { Text = "Password:", AutoSize = true, Margin = new Padding(3, 10, 3, 3) });
            _pw.MinimumSize = new Size((int)(360 * scale), 0);
            layout.Controls.Add(_pw);
            layout.Controls.Add(_remember);
            new ToolTip().SetToolTip(_remember, "Stored in Windows Credential Manager, never in the registrations file.");

            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0, 10, 0, 0) };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
            var ok = new Button { Text = "Connect", DialogResult = DialogResult.OK, AutoSize = true };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);
            layout.Controls.Add(buttons);

            Controls.Add(layout);
            AcceptButton = ok; CancelButton = cancel;
            Shown += (s, e) => _pw.Focus();
        }
    }
}
