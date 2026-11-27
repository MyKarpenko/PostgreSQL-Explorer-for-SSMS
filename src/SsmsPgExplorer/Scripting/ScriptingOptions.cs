using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Windows.Forms;
using SsmsPgExplorer.UI;

namespace SsmsPgExplorer.Scripting
{
    /// <summary>
    /// Script Table as options. With FollowSsms (default) every option that has an SSMS equivalent is read live from
    /// the user's SSMS Unified Settings (Tools > Options > SQL Server Object Explorer > Scripting,
    /// keys sqlServerObjectExplorer.scriptingOptions.*). PostgreSQL-only options are ours, stored in
    /// %APPDATA%\SsmsPgExplorer\scripting-options.json.
    /// </summary>
    [DataContract]
    internal sealed class ScriptingOptions
    {
        private const string P = "sqlServerObjectExplorer.scriptingOptions.";

        [DataMember] public bool FollowSsms { get; set; } = true;

        // General
        [DataMember, Option("General", "Include descriptive headers", "-- header with object, server, database, date", P + "general.includeHeaders")]
        public bool IncludeHeaders { get; set; } = true;
        [DataMember, Option("General", "Schema qualify object names", "schema.table everywhere (incl. FK targets, types)", P + "object.schemaQualify")]
        public bool SchemaQualify { get; set; } = true;
        [DataMember, Option("General", "Check for object existence", "IF NOT EXISTS / IF EXISTS", P + "object.includeIfNotExists")]
        public bool CheckExistence { get; set; } = false;
        [DataMember, Option("General", "Script extended properties", "COMMENT ON TABLE / COLUMN / INDEX / CONSTRAINT", P + "object.scriptExtendedProperties")]
        public bool Comments { get; set; } = true;
        [DataMember, Option("General", "Script owner", "ALTER TABLE ... OWNER TO", P + "object.scriptOwner")]
        public bool Owner { get; set; } = false;
        [DataMember, Option("General", "Script permissions", "GRANT statements from the table ACL", P + "object.scriptPermissions")]
        public bool Permissions { get; set; } = false;

        // Table and view
        [DataMember, Option("Table and view", "Script primary keys", null, P + "tableView.scriptPrimaryKeys")]
        public bool PrimaryKeys { get; set; } = true;
        [DataMember, Option("Table and view", "Script unique keys", null, P + "tableView.scriptUniqueKeys")]
        public bool UniqueKeys { get; set; } = true;
        [DataMember, Option("Table and view", "Script check constraints", "CHECK and EXCLUDE", P + "tableView.scriptCheckConstraints")]
        public bool CheckConstraints { get; set; } = true;
        [DataMember, Option("Table and view", "Script foreign keys", "ALTER TABLE ... ADD CONSTRAINT after the CREATE", P + "tableView.scriptForeignKeys")]
        public bool ForeignKeys { get; set; } = true;
        [DataMember, Option("Table and view", "Script indexes", "non-constraint indexes (pg_get_indexdef)", P + "tableView.scriptIndexes")]
        public bool Indexes { get; set; } = false;
        [DataMember, Option("Table and view", "Script triggers", "pg_get_triggerdef", P + "tableView.scriptTriggers")]
        public bool Triggers { get; set; } = false;
        [DataMember, Option("Table and view", "Script statistics", "extended statistics (CREATE STATISTICS)", P + "tableView.scriptStatistics")]
        public bool Statistics { get; set; } = false;
        [DataMember, Option("Table and view", "Script defaults", "column DEFAULT expressions", P + "tableView.scriptDefaults")]
        public bool Defaults { get; set; } = true;
        [DataMember, Option("Table and view", "Include identity property", "GENERATED ... AS IDENTITY", P + "tableView.includeIdentity")]
        public bool Identity { get; set; } = true;
        [DataMember, Option("Table and view", "Include collation", "COLLATE on columns with a non-default collation", P + "tableView.includeCollation")]
        public bool Collation { get; set; } = false;
        [DataMember, Option("Table and view", "Script partition schemes", "PARTITION OF ... for each partition", P + "tableView.scriptPartitionSchemes")]
        public bool Partitions { get; set; } = true;
        [DataMember, Option("Table and view", "Script file groups", "TABLESPACE", P + "tableView.scriptFileGroups")]
        public bool Tablespace { get; set; } = true;
        [DataMember, Option("Table and view", "Script data compression options", "WITH (fillfactor, autovacuum_*, ...), USING <access method>", P + "object.scriptDataCompressionOptions")]
        public bool StorageOptions { get; set; } = true;

        // PostgreSQL only (no SSMS equivalent)
        [DataMember, Option("PostgreSQL", "Script row-level security", "ENABLE ROW LEVEL SECURITY and CREATE POLICY", null)]
        public bool RowLevelSecurity { get; set; } = false;

        private static string PathOnDisk => System.IO.Path.Combine(Model.ServerStore.Folder, "scripting-options.json");

        /// <summary>Saved options with SSMS values applied on top when FollowSsms is on.</summary>
        public static ScriptingOptions Load()
        {
            var o = LoadSaved();
            if (o.FollowSsms) o.ApplySsms();
            return o;
        }

        private static ScriptingOptions LoadSaved()
        {
            try
            {
                if (File.Exists(PathOnDisk))
                    using (var fs = File.OpenRead(PathOnDisk))
                        return (ScriptingOptions)new DataContractJsonSerializer(typeof(ScriptingOptions)).ReadObject(fs) ?? new ScriptingOptions();
            }
            catch (Exception ex) { Diagnostics.Log.Error("scripting options load", ex); }
            return new ScriptingOptions();
        }

        public void ApplySsms()
        {
            foreach (var p in GetType().GetProperties())
            {
                var a = p.GetCustomAttribute<OptionAttribute>();
                if (a?.SsmsKey != null && SsmsSettings.Has(a.SsmsKey)) p.SetValue(this, SsmsSettings.Bool(a.SsmsKey, (bool)p.GetValue(this)));
            }
        }

        public void Save()
        {
            Directory.CreateDirectory(Model.ServerStore.Folder);
            using (var fs = File.Create(PathOnDisk))
            using (var w = JsonReaderWriterFactory.CreateJsonWriter(fs, Encoding.UTF8, false, true))
                new DataContractJsonSerializer(typeof(ScriptingOptions)).WriteObject(w, this);
        }

        // Missing members in an older file deserialize to false; re-apply defaults first.
        [OnDeserializing]
        private void OnDeserializing(StreamingContext c)
        {
            var d = new ScriptingOptions();
            foreach (var p in GetType().GetProperties()) if (p.CanWrite) p.SetValue(this, p.GetValue(d));
        }

        public ScriptingOptions Clone() => (ScriptingOptions)MemberwiseClone();
    }

    [AttributeUsage(AttributeTargets.Property)]
    internal sealed class OptionAttribute : Attribute
    {
        public string Category { get; }
        public string Label { get; }
        public string Hint { get; }
        public string SsmsKey { get; }
        public OptionAttribute(string category, string label, string hint, string ssmsKey) { Category = category; Label = label; Hint = hint; SsmsKey = ssmsKey; }
    }

    /// <summary>Options page: SSMS-backed options are shown read-only while "Follow SSMS" is on.</summary>
    internal sealed class ScriptingOptionsDialog : Form
    {
        private readonly ScriptingOptions _saved;
        private readonly ListView _list = new ListView { Dock = DockStyle.Fill, View = View.Details, CheckBoxes = true, FullRowSelect = true, ShowGroups = true, HeaderStyle = ColumnHeaderStyle.Nonclickable };
        private readonly CheckBox _follow = new CheckBox { Text = "Follow my SSMS scripting options (Tools > Options > SQL Server Object Explorer > Scripting)", AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(8, 8, 8, 4) };
        private bool _refreshing;

        public ScriptingOptionsDialog(ScriptingOptions ignored)
        {
            _saved = LoadSavedForEdit();
            var scale = DeviceDpi / 96f;
            Text = "Scripting Options - PostgreSQL";
            Width = (int)(720 * scale); Height = (int)(640 * scale);
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false;

            _list.Columns.Add("Option", (int)(260 * scale));
            _list.Columns.Add("PostgreSQL output", (int)(300 * scale));
            _list.Columns.Add("Source", (int)(110 * scale));
            var groups = new Dictionary<string, ListViewGroup>();
            foreach (var p in typeof(ScriptingOptions).GetProperties())
            {
                var a = p.GetCustomAttribute<OptionAttribute>();
                if (a == null) continue;
                if (!groups.TryGetValue(a.Category, out var g)) { g = new ListViewGroup(a.Category + " scripting options"); groups[a.Category] = g; _list.Groups.Add(g); }
                _list.Items.Add(new ListViewItem(new[] { a.Label, a.Hint ?? "", "" }, g) { Tag = p });
            }
            _follow.Checked = _saved.FollowSsms;
            _follow.CheckedChanged += (s, e) => Refresh(_follow.Checked);
            _list.ItemCheck += (s, e) =>
            {
                if (_refreshing) return;
                var a = ((PropertyInfo)_list.Items[e.Index].Tag).GetCustomAttribute<OptionAttribute>();
                if (_follow.Checked && a.SsmsKey != null) e.NewValue = e.CurrentValue; // read-only while following SSMS
            };

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(8), WrapContents = false };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
            var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
            buttons.Controls.AddRange(new Control[] { cancel, ok });
            AcceptButton = ok; CancelButton = cancel;
            FormClosing += (s, e) =>
            {
                if (DialogResult != DialogResult.OK) return;
                _saved.FollowSsms = _follow.Checked;
                foreach (ListViewItem i in _list.Items)
                {
                    var p = (PropertyInfo)i.Tag;
                    var a = p.GetCustomAttribute<OptionAttribute>();
                    if (!_follow.Checked || a.SsmsKey == null) p.SetValue(_saved, i.Checked);
                }
                _saved.Save();
            };

            Controls.Add(_list);
            Controls.Add(new Label
            {
                Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8, 0, 8, 8), ForeColor = SystemColors.GrayText,
                Text = "While following SSMS, options marked \"SSMS\" mirror your SSMS settings live (change them in SSMS). \"PostgreSQL\" options are set here.",
            });
            Controls.Add(_follow);
            Controls.Add(buttons);
            Refresh(_follow.Checked);
        }

        private static ScriptingOptions LoadSavedForEdit()
        {
            var o = ScriptingOptions.Load();
            return o;
        }

        private void Refresh(bool follow)
        {
            _refreshing = true;
            var view = _saved.Clone();
            if (follow) view.ApplySsms();
            foreach (ListViewItem i in _list.Items)
            {
                var p = (PropertyInfo)i.Tag;
                var a = p.GetCustomAttribute<OptionAttribute>();
                i.Checked = (bool)p.GetValue(view);
                var fromSsms = follow && a.SsmsKey != null;
                i.SubItems[2].Text = a.SsmsKey == null ? "PostgreSQL" : fromSsms ? "SSMS" : "this dialog";
                i.ForeColor = fromSsms ? SystemColors.GrayText : SystemColors.WindowText;
                i.ToolTipText = a.SsmsKey;
            }
            _list.ShowItemToolTips = true;
            _refreshing = false;
        }
    }
}
