using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace SsmsPgExplorer.Query
{
    /// <summary>Executes a batch on a dedicated session and materialises up to MaxRows per result set.</summary>
    internal sealed class QueryRunner
    {
        public sealed class Result
        {
            public List<DataTable> Tables { get; } = new List<DataTable>();
            public List<string> Messages { get; } = new List<string>();
            public bool Failed;
            public TimeSpan Elapsed;
            public long TotalRows;
        }

        public int MaxRows { get; set; } = 10000;

        public const string ServerNameColumn = "Server Name";

        /// <summary>
        /// Combines per-server results the way SSMS multi-server queries do: optionally a leading "Server Name"
        /// column, and result set N from every server unioned into one grid when the column lists match.
        /// Servers whose result set N has a different shape get their own grid instead of failing the merge.
        /// </summary>
        public static Result Combine(IList<(Model.ServerRegistration Server, Result Result)> perServer, bool merge, bool addServerName)
        {
            var combined = new Result();
            var ok = perServer.Count(p => !p.Result.Failed);
            foreach (var (server, r) in perServer)
            {
                combined.TotalRows += r.TotalRows;
                if (r.Elapsed > combined.Elapsed) combined.Elapsed = r.Elapsed;
                combined.Failed |= r.Failed;
                foreach (var m in r.Messages) combined.Messages.Add($"{server.DisplayName}: {m}");
            }
            combined.Messages.Add($"Completed on {ok} of {perServer.Count} server{(perServer.Count == 1 ? "" : "s")}.");

            var maxSets = perServer.Count == 0 ? 0 : perServer.Max(p => p.Result.Tables.Count);
            for (var i = 0; i < maxSets; i++)
            {
                if (!merge)
                {
                    foreach (var (server, r) in perServer.Where(p => p.Result.Tables.Count > i))
                        combined.Tables.Add(WithServer(r.Tables[i], server.DisplayName, addServerName, $"{server.DisplayName} - Result {i + 1}"));
                    continue;
                }

                var groups = perServer.Where(p => p.Result.Tables.Count > i)
                    .GroupBy(p => string.Join("\u001f", p.Result.Tables[i].Columns.Cast<DataColumn>().Select(c => c.Caption)));
                var first = true;
                foreach (var g in groups)
                {
                    DataTable target = null;
                    foreach (var (server, r) in g)
                    {
                        var part = WithServer(r.Tables[i], server.DisplayName, addServerName, null);
                        if (target == null) { target = part; continue; }
                        foreach (DataRow row in part.Rows) target.ImportRow(row);
                    }
                    target.TableName = first ? $"Result {i + 1}" : $"Result {i + 1} ({string.Join(", ", g.Select(p => p.Server.DisplayName))} - different columns)";
                    if (!first) combined.Messages.Add($"Result {i + 1}: column list differs on {string.Join(", ", g.Select(p => p.Server.DisplayName))}; shown separately.");
                    combined.Tables.Add(target);
                    first = false;
                }
            }
            return combined;
        }

        private static DataTable WithServer(DataTable source, string server, bool addServerName, string name)
        {
            var t = source.Copy();
            if (name != null) t.TableName = name;
            if (!addServerName) return t;
            var col = new DataColumn(ServerNameColumn, typeof(string)) { Caption = ServerNameColumn, DefaultValue = server };
            col.ExtendedProperties["pgtype"] = "server";
            // Avoid clashing with a user column of the same name.
            if (t.Columns.Contains(ServerNameColumn)) { col.ColumnName = ServerNameColumn + "_"; }
            t.Columns.Add(col);
            col.SetOrdinal(0);
            foreach (DataRow row in t.Rows) row[col] = server;
            return t;
        }

        public async Task<Result> ExecuteAsync(NpgsqlConnection conn, string sql, CancellationToken ct)
        {
            var result = new Result();
            var sw = Stopwatch.StartNew();

            void OnNotice(object s, NpgsqlNoticeEventArgs e) =>
                result.Messages.Add($"{e.Notice.Severity}: {e.Notice.MessageText}");
            conn.Notice += OnNotice;

            try
            {
                using (var cmd = new NpgsqlCommand(sql, conn) { CommandTimeout = 0 })
                {
                    // Every column comes back as text: no client-side type mapping surprises (arrays, ranges,
                    // geometry, custom enums all render like psql).
                    cmd.AllResultTypesAreUnknown = true;
                    using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
                    {
                        var set = 0;
                        do
                        {
                            if (reader.FieldCount == 0) continue;
                            set++;
                            var table = new DataTable($"Result {set}");
                            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            for (var i = 0; i < reader.FieldCount; i++)
                            {
                                var name = reader.GetName(i);
                                if (string.IsNullOrEmpty(name) || name == "?column?") name = $"(No column name)";
                                var unique = name;
                                for (var n = 2; !names.Add(unique); n++) unique = $"{name}_{n}";
                                var col = table.Columns.Add(unique, typeof(string));
                                col.Caption = name;
                                col.ExtendedProperties["pgtype"] = reader.GetDataTypeName(i);
                            }

                            var rows = 0;
                            var values = new object[reader.FieldCount];
                            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                            {
                                if (rows >= MaxRows) { rows++; continue; } // drain to get the true count
                                reader.GetValues(values);
                                table.Rows.Add(values);
                                rows++;
                            }
                            result.TotalRows += rows;
                            result.Tables.Add(table);
                            result.Messages.Add(rows > MaxRows
                                ? $"({rows:N0} rows; showing first {MaxRows:N0})"
                                : $"({rows:N0} row{(rows == 1 ? "" : "s")})");
                        } while (await reader.NextResultAsync(ct).ConfigureAwait(false));

                        if (reader.RecordsAffected > 0)
                            result.Messages.Add($"({reader.RecordsAffected:N0} row{(reader.RecordsAffected == 1 ? "" : "s")} affected)");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                result.Failed = true;
                result.Messages.Add("Query was cancelled by user.");
            }
            catch (Exception ex)
            {
                result.Failed = true;
                result.Messages.Add(Connection.ConnectionFactory.Describe(ex));
            }
            finally
            {
                conn.Notice -= OnNotice;
                sw.Stop();
                result.Elapsed = sw.Elapsed;
            }
            return result;
        }
    }
}
