using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using SsmsPgExplorer.Connection;
using SsmsPgExplorer.Model;
using SsmsPgExplorer.Query;
using SsmsPgExplorer.RegisteredServers;
using static SsmsPgExplorer.Connection.PgCatalog;

namespace SsmsPgExplorer.ObjectExplorer
{
    // Tree shape (mirrors SSMS for SQL Server):
    //   Server (PostgreSQL 16.4 - user)
    //     Databases
    //       db
    //         Schemas / System Schemas
    //           schema
    //             Tables / Views / Materialized Views / Functions / Sequences
    //               table
    //                 Columns / Indexes

    [Serializable]
    internal sealed class PgServerNode : PgNode
    {
        private bool _failed;

        public PgServerNode(ServerRegistration reg) : base(reg, reg.Database, reg.DisplayName)
        {
            ToolTipText = $"{reg.Host}:{reg.Port}  ({reg.Auth})";
        }

        private PgServerNode(SerializationInfo info, StreamingContext context) : base(info, context) { }

        protected override Icon NodeIcon => _failed ? Icons.ServerError : Icons.Server;

        protected override async Task<IEnumerable<PgNode>> LoadChildrenAsync(CancellationToken ct)
        {
            var version = await ServerVersionAsync(Server, ct).ConfigureAwait(false);
            await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
            _failed = false;
            Text = $"{Server.DisplayName} (PostgreSQL {version}){(Server.AllowWrites ? "" : " [read-only]")}";
            ObjectExplorerBridge.RefreshIcon(this);
            return new PgNode[]
            {
                new PgFolderNode(Server, null, "Databases", Icons.Folder, async c =>
                    (await DatabasesAsync(Server, c).ConfigureAwait(false)).Select(d => (PgNode)new PgDatabaseNode(Server, d))),
            };
        }

        protected override void OnLoadFailed(Exception error)
        {
            _failed = true;
            ObjectExplorerBridge.RefreshIcon(this);
        }

        protected override void BuildMenu(ToolStripItemCollection items)
        {
            items.Add("New Query", null, (s, e) => Run(() => QueryWindow.OpenAsync(Server, Server.Database)));
            items.Add("Activity Monitor", null, (s, e) => Run(() => Activity.ActivityWindow.OpenAsync(Server, Server.Database)));
            items.Add(new ToolStripSeparator());
            items.Add("Disconnect", null, (s, e) => ObjectExplorerBridge.Disconnect(this));
            items.Add("Edit Registration...", null, (s, e) => RegisteredServersControl.EditRegistration(Server));
        }
    }

    /// <summary>Generic folder ("Databases", "Tables", ...) whose children come from a loader.</summary>
    [Serializable]
    internal sealed class PgFolderNode : PgNode
    {
        [NonSerialized] private readonly Func<CancellationToken, Task<IEnumerable<PgNode>>> _loader;
        private readonly Icon _icon;

        public PgFolderNode(ServerRegistration reg, string db, string text, Icon icon, Func<CancellationToken, Task<IEnumerable<PgNode>>> loader)
            : base(reg, db, text)
        {
            _icon = icon;
            _loader = loader;
        }

        private PgFolderNode(SerializationInfo info, StreamingContext context) : base(info, context) { }

        protected override Icon NodeIcon => _icon ?? Icons.Folder;
        protected override Task<IEnumerable<PgNode>> LoadChildrenAsync(CancellationToken ct) => _loader(ct);
    }

    [Serializable]
    internal sealed class PgDatabaseNode : PgNode
    {
        public PgDatabaseNode(ServerRegistration reg, Item db) : base(reg, db.Name, db.Name)
        {
            ToolTipText = db.Detail;
        }

        private PgDatabaseNode(SerializationInfo info, StreamingContext context) : base(info, context) { }

        protected override Icon NodeIcon => Icons.Database;

        protected override Task<IEnumerable<PgNode>> LoadChildrenAsync(CancellationToken ct) =>
            Task.FromResult<IEnumerable<PgNode>>(new PgNode[]
            {
                new PgFolderNode(Server, Database, "Schemas", Icons.Folder, async c =>
                    (await SchemasAsync(Server, Database, false, c).ConfigureAwait(false)).Select(s => (PgNode)new PgSchemaNode(Server, Database, s))),
                new PgFolderNode(Server, Database, "System Schemas", Icons.Folder, async c =>
                    (await SchemasAsync(Server, Database, true, c).ConfigureAwait(false)).Select(s => (PgNode)new PgSchemaNode(Server, Database, s))),
            });

        protected override void BuildMenu(ToolStripItemCollection items)
        {
            items.Add("New Query", null, (s, e) => Run(() => QueryWindow.OpenAsync(Server, Database)));
            items.Add("Activity Monitor", null, (s, e) => Run(() => Activity.ActivityWindow.OpenAsync(Server, Database)));
        }
    }

    [Serializable]
    internal sealed class PgSchemaNode : PgNode
    {
        public string Schema { get; }

        public PgSchemaNode(ServerRegistration reg, string db, Item schema) : base(reg, db, schema.Name)
        {
            Schema = schema.Name;
            ToolTipText = schema.Detail;
        }

        private PgSchemaNode(SerializationInfo info, StreamingContext context) : base(info, context) { }

        protected override Icon NodeIcon => Icons.Schema;

        protected override Task<IEnumerable<PgNode>> LoadChildrenAsync(CancellationToken ct)
        {
            PgFolderNode Rel(string text, string kinds, Icon icon) =>
                new PgFolderNode(Server, Database, text, Icons.Folder, async c =>
                    (await RelationsAsync(Server, Database, Schema, kinds, c).ConfigureAwait(false))
                        .Select(r => (PgNode)new PgRelationNode(Server, Database, Schema, r)));

            return Task.FromResult<IEnumerable<PgNode>>(new PgNode[]
            {
                Rel("Tables", "rpf", Icons.Table),
                Rel("Views", "v", Icons.View),
                Rel("Materialized Views", "m", Icons.View),
                new PgFolderNode(Server, Database, "Functions", Icons.Folder, async c =>
                    (await FunctionsAsync(Server, Database, Schema, c).ConfigureAwait(false))
                        .Select(f => (PgNode)new PgFunctionNode(Server, Database, Schema, f))),
                Rel("Sequences", "S", Icons.Sequence),
            });
        }

        protected override void BuildMenu(ToolStripItemCollection items)
        {
            items.Add("New Query", null, (s, e) => Run(() =>
                QueryWindow.OpenAsync(Server, Database, $"SET search_path TO {QuoteIdent(Schema)}, public;\r\n\r\n")));
        }
    }

    /// <summary>Table, partitioned table, foreign table, view, materialized view or sequence.</summary>
    [Serializable]
    internal sealed class PgRelationNode : PgNode
    {
        public string Schema { get; }
        public string Relation { get; }
        public string RelKind { get; }
        public long Oid { get; }

        public PgRelationNode(ServerRegistration reg, string db, string schema, Item rel)
            : base(reg, db, BuildText(schema, rel))
        {
            Schema = schema;
            Relation = rel.Name;
            RelKind = rel.Kind;
            Oid = rel.Oid;
            ToolTipText = rel.Detail;
        }

        private PgRelationNode(SerializationInfo info, StreamingContext context) : base(info, context) { }

        // SSMS shows "schema.table"; partitioned / foreign get a suffix.
        private static string BuildText(string schema, Item rel) =>
            $"{schema}.{rel.Name}" + (rel.Kind == "p" ? " (partitioned)" : rel.Kind == "f" ? " (foreign)" : "");

        private bool IsSequence => RelKind == "S";
        private bool IsView => RelKind == "v" || RelKind == "m";
        private string QualifiedName => $"{QuoteIdent(Schema)}.{QuoteIdent(Relation)}";

        protected override bool CanHaveChildren => RelKind != "S";
        protected override Icon NodeIcon => IsSequence ? Icons.Sequence : IsView ? Icons.View : Icons.Table;
        protected override string CopyText => QualifiedName;

        protected override Task<IEnumerable<PgNode>> LoadChildrenAsync(CancellationToken ct)
        {
            var list = new List<PgNode>
            {
                new PgFolderNode(Server, Database, "Columns", Icons.Folder, async c =>
                    (await ColumnsAsync(Server, Database, Oid, c).ConfigureAwait(false))
                        .Select(col => (PgNode)new PgLeafNode(Server, Database, $"{col.Name} ({col.Detail})",
                            col.Detail.StartsWith("PK,") ? Icons.Key : Icons.Column, col.Name))),
            };
            if (RelKind != "v" && RelKind != "f")
            {
                list.Add(new PgFolderNode(Server, Database, "Indexes", Icons.Folder, async c =>
                    (await IndexesAsync(Server, Database, Oid, c).ConfigureAwait(false))
                        .Select(ix => (PgNode)new PgLeafNode(Server, Database, $"{ix.Name} ({ix.Detail})", Icons.Index, ix.Name, ix.Kind))));
            }
            return Task.FromResult<IEnumerable<PgNode>>(list);
        }

        protected override void BuildMenu(ToolStripItemCollection items)
        {
            if (IsSequence)
            {
                items.Add("Select current value", null, (s, e) => Run(() =>
                    QueryWindow.OpenAsync(Server, Database, $"SELECT last_value, is_called FROM {QualifiedName};", execute: true)));
                return;
            }

            items.Add("Select Top 1000 Rows", null, (s, e) => Run(() =>
                QueryWindow.OpenAsync(Server, Database, $"SELECT *\r\nFROM {QualifiedName}\r\nLIMIT 1000;", execute: true)));
            items.Add("Count Rows (exact)", null, (s, e) => Run(() =>
                QueryWindow.OpenAsync(Server, Database, $"SELECT count(*) FROM {QualifiedName};", execute: true)));
            items.Add("Size / Stats", null, (s, e) => Run(() =>
                QueryWindow.OpenAsync(Server, Database, StatsSql(), execute: true)));

            if (IsView)
            {
                var script = new ToolStripMenuItem("Script View as");
                script.DropDownItems.Add("CREATE To New Query Window", null, (s, e) => Run(() => ScriptViewAsync()));
                script.DropDownItems.Add("SELECT To New Query Window", null, (s, e) => Run(() => ScriptSelectAsync()));
                items.Add(script);
                return;
            }

            // SSMS: Script Table as > CREATE To / DROP To / DROP And CREATE To / SELECT To > New Query Window / Clipboard
            var table = new ToolStripMenuItem("Script Table as");
            ToolStripMenuItem Target(string label, Scripting.ScriptKind kind)
            {
                var item = new ToolStripMenuItem(label);
                item.DropDownItems.Add("New Query Editor Window", null, (s, e) => Run(() => ScriptTableAsync(kind, toClipboard: false)));
                item.DropDownItems.Add("Clipboard", null, (s, e) => Run(() => ScriptTableAsync(kind, toClipboard: true)));
                return item;
            }
            table.DropDownItems.Add(Target("CREATE To", Scripting.ScriptKind.Create));
            table.DropDownItems.Add(Target("DROP To", Scripting.ScriptKind.Drop));
            table.DropDownItems.Add(Target("DROP And CREATE To", Scripting.ScriptKind.DropAndCreate));
            var select = new ToolStripMenuItem("SELECT To");
            select.DropDownItems.Add("New Query Editor Window", null, (s, e) => Run(() => ScriptSelectAsync()));
            table.DropDownItems.Add(select);
            table.DropDownItems.Add(new ToolStripSeparator());
            table.DropDownItems.Add("Scripting Options...", null, (s, e) =>
            {
                using (var dlg = new Scripting.ScriptingOptionsDialog(Scripting.ScriptingOptions.Load())) dlg.ShowDialog();
            });
            items.Add(table);
        }

        private async Task ScriptTableAsync(Scripting.ScriptKind kind, bool toClipboard)
        {
            var script = await Scripting.TableScripter.ScriptAsync(Server, Database, Oid, kind, Scripting.ScriptingOptions.Load(), CancellationToken.None).ConfigureAwait(false);
            await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (toClipboard) Clipboard.SetText(script);
            else await QueryWindow.OpenAsync(Server, Database, script);
        }

        private string StatsSql() =>
$@"SELECT c.oid::regclass AS relation,
       pg_size_pretty(pg_total_relation_size(c.oid)) AS total_size,
       pg_size_pretty(pg_relation_size(c.oid))       AS heap_size,
       pg_size_pretty(pg_indexes_size(c.oid))        AS index_size,
       c.reltuples::bigint                           AS rows_estimate,
       s.n_live_tup, s.n_dead_tup,
       s.last_vacuum, s.last_autovacuum, s.last_analyze, s.last_autoanalyze,
       s.seq_scan, s.idx_scan
FROM pg_class c
LEFT JOIN pg_stat_user_tables s ON s.relid = c.oid
WHERE c.oid = {Oid};";

        private async Task ScriptSelectAsync()
        {
            var cols = await ColumnsAsync(Server, Database, Oid, CancellationToken.None).ConfigureAwait(false);
            var list = string.Join(",\r\n       ", cols.Select(c => QuoteIdent(c.Name)));
            await QueryWindow.OpenAsync(Server, Database, $"SELECT {list}\r\nFROM {QualifiedName}\r\nLIMIT 1000;");
        }

        private async Task ScriptViewAsync()
        {
            var def = await ViewDefinitionAsync(Server, Database, Schema, Relation, RelKind, CancellationToken.None).ConfigureAwait(false);
            await QueryWindow.OpenAsync(Server, Database, def);
        }
    }

    [Serializable]
    internal sealed class PgFunctionNode : PgNode
    {
        private readonly long _oid;
        private readonly string _schema;
        private readonly string _signature;

        public PgFunctionNode(ServerRegistration reg, string db, string schema, Item fn) : base(reg, db, $"{schema}.{fn.Name}")
        {
            _oid = fn.Oid;
            _schema = schema;
            _signature = fn.Name;
            ToolTipText = fn.Detail;
        }

        private PgFunctionNode(SerializationInfo info, StreamingContext context) : base(info, context) { }

        protected override bool CanHaveChildren => false;
        protected override Icon NodeIcon => Icons.Function;
        protected override string CopyText => $"{QuoteIdent(_schema)}.{_signature}";

        protected override void BuildMenu(ToolStripItemCollection items)
        {
            items.Add("Script CREATE to New Query Window", null, (s, e) => Run(async () =>
            {
                var def = await FunctionDefinitionAsync(Server, Database, _oid, CancellationToken.None).ConfigureAwait(false);
                await QueryWindow.OpenAsync(Server, Database, def);
            }));
        }
    }

    /// <summary>Column, index, etc.</summary>
    [Serializable]
    internal sealed class PgLeafNode : PgNode
    {
        private readonly Icon _icon;
        private readonly string _copy;
        private readonly string _definition;

        public PgLeafNode(ServerRegistration reg, string db, string text, Icon icon, string copyText, string definition = null)
            : base(reg, db, text)
        {
            _icon = icon;
            _copy = copyText;
            _definition = definition;
            if (definition != null) ToolTipText = definition;
        }

        private PgLeafNode(SerializationInfo info, StreamingContext context) : base(info, context) { }

        protected override bool CanHaveChildren => false;
        protected override Icon NodeIcon => _icon ?? Icons.Column;
        protected override string CopyText => _copy;

        protected override void BuildMenu(ToolStripItemCollection items)
        {
            if (_definition != null)
                items.Add("Script CREATE to New Query Window", null, (s, e) => Run(() => QueryWindow.OpenAsync(Server, Database, _definition + ";")));
        }
    }
}
