using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using SsmsPgExplorer.Model;

namespace SsmsPgExplorer.Connection
{
    /// <summary>Read-only catalog queries that drive Object Explorer. All pg_catalog, no information_schema (faster, sees everything the role can).</summary>
    internal static class PgCatalog
    {
        public sealed class Item
        {
            public string Name;
            public string Detail;   // shown in brackets, SSMS-style: "(int4, not null)"
            public string Kind;     // relkind / prokind / etc.
            public long Oid;
            public Item(string name, string detail = null, string kind = null, long oid = 0) { Name = name; Detail = detail; Kind = kind; Oid = oid; }
        }

        private const string UserSchemaFilter = "n.nspname !~ '^pg_' AND n.nspname <> 'information_schema'";

        public static Task<string> ServerVersionAsync(ServerRegistration reg, CancellationToken ct) =>
            ScalarAsync(reg, null, "SELECT current_setting('server_version') || ' - ' || current_user", ct);

        public static Task<List<Item>> DatabasesAsync(ServerRegistration reg, CancellationToken ct) =>
            ListAsync(reg, null, @"
SELECT d.datname, pg_size_pretty(pg_database_size(d.datname)) AS detail
FROM pg_database d
WHERE NOT d.datistemplate AND d.datallowconn AND has_database_privilege(d.datname, 'CONNECT')
ORDER BY d.datname", null, ct, r => new Item(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1)));

        public static Task<List<Item>> SchemasAsync(ServerRegistration reg, string db, bool system, CancellationToken ct) =>
            ListAsync(reg, db, $@"
SELECT n.nspname, pg_get_userbyid(n.nspowner)
FROM pg_namespace n
WHERE {(system ? "NOT (" + UserSchemaFilter + ")" : UserSchemaFilter)} AND n.nspname !~ '^pg_(toast|temp_)'
ORDER BY n.nspname", null, ct, r => new Item(r.GetString(0), "owner: " + r.GetString(1)));

        /// <param name="relkinds">e.g. "rpf" tables, "v" views, "m" materialized views, "S" sequences.</param>
        public static Task<List<Item>> RelationsAsync(ServerRegistration reg, string db, string schema, string relkinds, CancellationToken ct) =>
            ListAsync(reg, db, @"
SELECT c.relname,
       CASE WHEN c.relkind IN ('r','p','m') THEN to_char(GREATEST(c.reltuples, 0)::bigint, 'FM999,999,999,999') || ' rows est.' END,
       c.relkind::text, c.oid::bigint
FROM pg_class c
JOIN pg_namespace n ON n.oid = c.relnamespace
WHERE n.nspname = @schema AND c.relkind::text = ANY(@kinds) AND NOT c.relispartition
ORDER BY c.relname",
                cmd =>
                {
                    cmd.Parameters.AddWithValue("schema", schema);
                    cmd.Parameters.AddWithValue("kinds", ToCharArray(relkinds));
                }, ct,
                r => new Item(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetString(2), r.GetInt64(3)));

        public static Task<List<Item>> FunctionsAsync(ServerRegistration reg, string db, string schema, CancellationToken ct) =>
            ListAsync(reg, db, @"
SELECT p.proname || '(' || pg_get_function_identity_arguments(p.oid) || ')',
       pg_get_function_result(p.oid),
       p.prokind::text, p.oid::bigint
FROM pg_proc p
JOIN pg_namespace n ON n.oid = p.pronamespace
WHERE n.nspname = @schema AND p.prokind IN ('f','p','w')
ORDER BY 1",
                cmd => cmd.Parameters.AddWithValue("schema", schema), ct,
                r => new Item(r.GetString(0), r.IsDBNull(1) ? "procedure" : "returns " + r.GetString(1), r.GetString(2), r.GetInt64(3)));

        public static Task<List<Item>> ColumnsAsync(ServerRegistration reg, string db, long relOid, CancellationToken ct) =>
            ListAsync(reg, db, @"
SELECT a.attname,
       CASE WHEN pk.attnum IS NOT NULL THEN 'PK, ' ELSE '' END
         || format_type(a.atttypid, a.atttypmod)
         || CASE WHEN a.attnotnull THEN ', not null' ELSE ', null' END
         || CASE WHEN a.attidentity <> '' THEN ', identity' WHEN a.attgenerated <> '' THEN ', generated' ELSE '' END
FROM pg_attribute a
LEFT JOIN LATERAL (
    SELECT a.attnum FROM pg_index i
    WHERE i.indrelid = a.attrelid AND i.indisprimary AND a.attnum = ANY(i.indkey)
) pk ON true
WHERE a.attrelid = @oid AND a.attnum > 0 AND NOT a.attisdropped
ORDER BY a.attnum",
                cmd => cmd.Parameters.AddWithValue("oid", NpgsqlTypes.NpgsqlDbType.Oid, (uint)relOid), ct,
                r => new Item(r.GetString(0), r.GetString(1)));

        public static Task<List<Item>> IndexesAsync(ServerRegistration reg, string db, long relOid, CancellationToken ct) =>
            ListAsync(reg, db, @"
SELECT ic.relname,
       CASE WHEN i.indisprimary THEN 'primary key' WHEN i.indisunique THEN 'unique' ELSE am.amname END
         || CASE WHEN NOT i.indisvalid THEN ', INVALID' ELSE '' END
         || ', ' || pg_size_pretty(pg_relation_size(ic.oid)),
       pg_get_indexdef(i.indexrelid)
FROM pg_index i
JOIN pg_class ic ON ic.oid = i.indexrelid
JOIN pg_am am ON am.oid = ic.relam
WHERE i.indrelid = @oid
ORDER BY ic.relname",
                cmd => cmd.Parameters.AddWithValue("oid", NpgsqlTypes.NpgsqlDbType.Oid, (uint)relOid), ct,
                r => new Item(r.GetString(0), r.GetString(1), r.GetString(2)));

        public static Task<string> ViewDefinitionAsync(ServerRegistration reg, string db, string schema, string name, string relkind, CancellationToken ct) =>
            ScalarAsync(reg, db,
                $"SELECT 'CREATE OR REPLACE {(relkind == "m" ? "MATERIALIZED " : "")}VIEW ' || quote_ident(@s) || '.' || quote_ident(@n) || ' AS' || chr(10) || pg_get_viewdef((quote_ident(@s) || '.' || quote_ident(@n))::regclass, true)",
                ct, cmd => { cmd.Parameters.AddWithValue("s", schema); cmd.Parameters.AddWithValue("n", name); });

        public static Task<string> FunctionDefinitionAsync(ServerRegistration reg, string db, long oid, CancellationToken ct) =>
            ScalarAsync(reg, db, "SELECT pg_get_functiondef(@oid)", ct,
                cmd => cmd.Parameters.AddWithValue("oid", NpgsqlTypes.NpgsqlDbType.Oid, (uint)oid));

        public static string QuoteIdent(string ident) => "\"" + ident.Replace("\"", "\"\"") + "\"";

        private static string[] ToCharArray(string kinds)
        {
            var a = new string[kinds.Length];
            for (var i = 0; i < kinds.Length; i++) a[i] = kinds[i].ToString();
            return a;
        }

        private static async Task<List<Item>> ListAsync(ServerRegistration reg, string db, string sql,
            System.Action<NpgsqlCommand> bind, CancellationToken ct, System.Func<NpgsqlDataReader, Item> map)
        {
            var list = new List<Item>();
            using (var conn = await ConnectionFactory.OpenAsync(reg, db, readOnly: true, ct).ConfigureAwait(false))
            using (var cmd = new NpgsqlCommand(sql, conn) { CommandTimeout = 60 })
            {
                bind?.Invoke(cmd);
                using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
                {
                    while (await r.ReadAsync(ct).ConfigureAwait(false)) list.Add(map(r));
                }
            }
            return list;
        }

        private static async Task<string> ScalarAsync(ServerRegistration reg, string db, string sql, CancellationToken ct, System.Action<NpgsqlCommand> bind = null)
        {
            using (var conn = await ConnectionFactory.OpenAsync(reg, db, readOnly: true, ct).ConfigureAwait(false))
            using (var cmd = new NpgsqlCommand(sql, conn) { CommandTimeout = 60 })
            {
                bind?.Invoke(cmd);
                return (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))?.ToString();
            }
        }
    }
}
