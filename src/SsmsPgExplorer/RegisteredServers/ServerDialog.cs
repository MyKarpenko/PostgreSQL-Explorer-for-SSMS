using System;
using System.Drawing;
using System.Windows.Forms;
using SsmsPgExplorer.Connection;
using SsmsPgExplorer.Model;

namespace SsmsPgExplorer.RegisteredServers
{
    /// <summary>New / Edit Server Registration dialog (SSMS "General" tab equivalent).</summary>
    internal sealed class ServerDialog : Form
    {
        private readonly ServerRegistration _reg;
        private readonly bool _isNew;

        private readonly TextBox _name = new TextBox();
        // Editable dropdown: pick an existing group (SSMS-style) or type a new path ("a/b").
        private readonly ComboBox _group = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, AutoCompleteMode = AutoCompleteMode.SuggestAppend, AutoCompleteSource = AutoCompleteSource.ListItems, MaxDropDownItems = 20 };
        private readonly TextBox _description = new TextBox();
        private readonly TextBox _host = new TextBox();
        private readonly NumericUpDown _port = new NumericUpDown { Minimum = 0, Maximum = 65535, Width = 90 };
        private readonly TextBox _database = new TextBox();
        private readonly ComboBox _auth = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly TextBox _user = new TextBox();
        private readonly TextBox _password = new TextBox { UseSystemPasswordChar = true };
        private readonly CheckBox _remember = new CheckBox { Text = "Remember password", AutoSize = true };
        private readonly TextBox _secretId = new TextBox();
        private readonly TextBox _profile = new TextBox();
        private readonly TextBox _region = new TextBox();
        private readonly ComboBox _ssl = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly CheckBox _allowWrites = new CheckBox { Text = "Allow write sessions (default is read-only)", AutoSize = true, ForeColor = Color.Firebrick };
        private readonly TableLayoutPanel _grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(10), AutoScroll = true };

        private Control _passwordRow, _rememberRow, _secretRow, _profileRow, _regionRow;

        public ServerRegistration Result => _reg;

        public ServerDialog(ServerRegistration reg, bool isNew)
        {
            _reg = reg;
            _isNew = isNew;

            Text = isNew ? "New Server Registration - PostgreSQL" : "Edit Server Registration - PostgreSQL";
            // Code-built form: scale the initial size by monitor DPI ourselves (no designer AutoScaleDimensions).
            var scale = DeviceDpi / 96f;
            Width = (int)(580 * scale); Height = (int)(700 * scale);
            MinimumSize = new Size((int)(480 * scale), (int)(420 * scale));
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = false; MaximizeBox = false;
            ShowInTaskbar = false;

            _grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            _auth.Items.AddRange(new object[] { "Password", "AWS Secrets Manager", "RDS IAM token" });
            _ssl.Items.AddRange(new object[] { "Disable", "Prefer", "Require", "VerifyCA", "VerifyFull" });

            Section("Connection");
            Row("Host:", _host);
            Row("Port:", _port);
            Row("Initial database:", _database);
            Row("Authentication:", _auth);
            Row("User name:", _user);
            _passwordRow = Row("Password:", _password);
            _rememberRow = Row("", _remember);
            _secretRow = Row("Secret ID / ARN:", _secretId);
            _profileRow = Row("AWS profile:", _profile);
            _regionRow = Row("AWS region:", _region);
            Row("SSL mode:", _ssl);
            Section("Registered server");
            Row("Registered name:", _name);
            Row("Group (a/b):", _group);
            Row("Description:", _description);
            Row("", _allowWrites);
            Row("", new Label
            {
                AutoSize = true,
                MaximumSize = new Size((int)(360 * scale), 0),
                ForeColor = SystemColors.GrayText,
                Text = "AWS Secrets Manager: blank Host/User/Database are filled from the secret (host, port, username, password, dbname). " +
                       "Uses your SSO profile via the AWS CLI; nothing is written to disk.",
            });

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(8), WrapContents = false };
            var save = new Button { Text = "Save", AutoSize = true, MinimumSize = new Size((int)(90 * scale), 0) };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size((int)(90 * scale), 0) };
            var test = new Button { Text = "Test", AutoSize = true, MinimumSize = new Size((int)(90 * scale), 0) };
            new ToolTip().SetToolTip(_remember, "Stored in Windows Credential Manager, never in the registrations file.");
            save.Click += (s, e) => { if (Collect(validate: true)) { DialogResult = DialogResult.OK; Close(); } };
            test.Click += (s, e) =>
            {
                if (!Collect(validate: true)) return;
                ConnectionFactory.Forget(_reg.Id);
                SavePasswordIfAsked();
                RegisteredServersControl.TestConnection(TempForTest(), this);
            };
            buttons.Controls.AddRange(new Control[] { cancel, save, test });
            AcceptButton = save; CancelButton = cancel;

            Controls.Add(_grid);
            Controls.Add(buttons);

            _auth.SelectedIndexChanged += (s, e) => ToggleAuthRows();
            _allowWrites.CheckedChanged += (s, e) =>
            {
                if (_allowWrites.Checked && _allowWrites.Focused &&
                    MessageBox.Show(this,
                        "Write sessions can run INSERT/UPDATE/DELETE/DDL.\r\n\r\n" +
                        "Follow your change-management process before modifying production data: change ticket, backup / archive of affected rows, and a tested rollback.\r\n\r\nAllow writes for this registration?",
                        Vsix.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                    _allowWrites.Checked = false;
            };

            Bind();
        }

        private string _pendingPassword;

        // Every control gets an explicit (column, row) cell. With auto-flow placement, hiding a row's
        // controls made the TableLayoutPanel re-flow and swap labels and inputs between columns.
        private int _nextRow;

        private int NewRow()
        {
            var row = _nextRow++;
            _grid.RowCount = _nextRow;
            _grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            return row;
        }

        private void Section(string title)
        {
            var l = new Label { Text = title, Font = new Font(Font, FontStyle.Bold), AutoSize = true, Margin = new Padding(0, 10, 0, 4) };
            var row = NewRow();
            _grid.Controls.Add(l, 0, row);
            _grid.SetColumnSpan(l, 2);
        }

        private Control Row(string label, Control input)
        {
            var l = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 0) };
            if (!(input is CheckBox) && !(input is Label) && !(input is NumericUpDown)) input.Dock = DockStyle.Fill;
            var row = NewRow();
            _grid.Controls.Add(l, 0, row);
            _grid.Controls.Add(input, 1, row);
            input.Tag = l;
            return input;
        }

        private void Bind()
        {
            _name.Text = _reg.Name;
            _group.Items.Add("");  // top level ("Local Server Groups")
            try { foreach (var g in ServerStore.AllGroupPaths()) _group.Items.Add(g); } catch { /* list is a convenience */ }
            _group.Text = _reg.Group;
            _description.Text = _reg.Description;
            _host.Text = _reg.Host;
            _port.Value = Math.Max(0, Math.Min(65535, _reg.Port));
            _database.Text = _reg.Database;
            _auth.SelectedIndex = (int)_reg.Auth;
            _user.Text = _reg.Username;
            _remember.Checked = _reg.RememberPassword;
            _password.Text = "";
            _password.Enabled = true;
            if (_reg.RememberPassword && !_isNew) { _password.Text = ""; _password.PlaceholderText("(saved - leave blank to keep)"); }
            _secretId.Text = _reg.SecretId;
            _profile.Text = _reg.AwsProfile;
            _region.Text = _reg.AwsRegion;
            _ssl.SelectedItem = _reg.SslMode ?? "Require";
            if (_ssl.SelectedIndex < 0) _ssl.SelectedItem = "Require";
            _allowWrites.Checked = _reg.AllowWrites;
            ToggleAuthRows();
        }

        private void ToggleAuthRows()
        {
            var mode = (AuthMode)Math.Max(0, _auth.SelectedIndex);
            void Show(Control c, bool v) { c.Visible = v; ((Control)c.Tag).Visible = v; }
            Show(_passwordRow, mode == AuthMode.Password);
            Show(_rememberRow, mode == AuthMode.Password);
            Show(_secretRow, mode == AuthMode.AwsSecretsManager);
            Show(_profileRow, mode != AuthMode.Password);
            Show(_regionRow, mode != AuthMode.Password);
        }

        private bool Collect(bool validate)
        {
            var mode = (AuthMode)Math.Max(0, _auth.SelectedIndex);
            string error = null;
            if (validate)
            {
                if (mode != AuthMode.AwsSecretsManager && string.IsNullOrWhiteSpace(_host.Text)) error = "Host is required.";
                else if (mode != AuthMode.AwsSecretsManager && string.IsNullOrWhiteSpace(_user.Text)) error = "User name is required.";
                else if (mode == AuthMode.AwsSecretsManager && string.IsNullOrWhiteSpace(_secretId.Text)) error = "Secret ID is required.";
                else if (mode == AuthMode.RdsIam && string.IsNullOrWhiteSpace(_region.Text)) error = "AWS region is required for IAM tokens.";
            }
            if (error != null)
            {
                MessageBox.Show(this, error, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            _reg.Name = _name.Text.Trim();
            _reg.Group = _group.Text.Trim().Trim('/');
            _reg.Description = _description.Text.Trim();
            _reg.Host = _host.Text.Trim();
            _reg.Port = (int)_port.Value;
            _reg.Database = _database.Text.Trim();
            _reg.Auth = mode;
            _reg.Username = _user.Text.Trim();
            _reg.SecretId = _secretId.Text.Trim();
            _reg.AwsProfile = _profile.Text.Trim();
            _reg.AwsRegion = _region.Text.Trim();
            _reg.SslMode = (string)_ssl.SelectedItem ?? "Require";
            _reg.AllowWrites = _allowWrites.Checked;

            if (mode == AuthMode.Password)
            {
                _reg.RememberPassword = _remember.Checked;
                _pendingPassword = _password.Text.Length > 0 ? _password.Text : null;
            }
            else
            {
                _reg.RememberPassword = false;
                _pendingPassword = null;
            }

            if (DialogResult != DialogResult.Cancel) SavePasswordIfAsked();
            return true;
        }

        private void SavePasswordIfAsked()
        {
            if (_reg.Auth != AuthMode.Password) { try { CredentialStore.Delete(_reg.Id); } catch { } return; }
            if (_reg.RememberPassword && _pendingPassword != null) CredentialStore.Write(_reg.Id, _reg.Username, _pendingPassword);
            if (!_reg.RememberPassword)
            {
                try { CredentialStore.Delete(_reg.Id); } catch { }
                // Typed but not remembered: still use it for this SSMS session instead of prompting again.
                if (_pendingPassword != null) ConnectionFactory.RememberForSession(_reg.Id, _pendingPassword);
            }
        }

        private ServerRegistration TempForTest()
        {
            var t = _reg.Clone();
            if (t.Auth == AuthMode.Password && _pendingPassword != null && !t.RememberPassword)
            {
                // Password typed but not remembered: test with it via a one-off credential entry.
                t.Id = "test-" + Guid.NewGuid().ToString("N");
                t.RememberPassword = true;
                CredentialStore.Write(t.Id, t.Username, _pendingPassword);
                var timer = new Timer { Interval = 120000 };
                timer.Tick += (s, e) => { timer.Dispose(); try { CredentialStore.Delete(t.Id); } catch { } };
                timer.Start();
            }
            return t;
        }
    }

    internal static class TextBoxExtensions
    {
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        /// <summary>Win32 cue banner (WinForms on .NET Framework has no PlaceholderText).</summary>
        public static void PlaceholderText(this TextBox box, string text)
        {
            if (box.IsHandleCreated) SendMessage(box.Handle, 0x1501, (IntPtr)1, text);
            else box.HandleCreated += (s, e) => SendMessage(box.Handle, 0x1501, (IntPtr)1, text);
        }
    }
}
