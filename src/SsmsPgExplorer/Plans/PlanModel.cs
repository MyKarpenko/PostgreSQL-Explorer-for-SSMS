using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace SsmsPgExplorer.Plans
{
    /// <summary>One statement's plan from EXPLAIN (FORMAT JSON).</summary>
    internal sealed class StatementPlan
    {
        public int Index;                 // 1-based within the batch
        public string Sql;
        public bool IsActual;
        public PlanNode Root;
        public double? PlanningTimeMs;
        public double? ExecutionTimeMs;
        public string RawJson;
        public string Error;              // set when EXPLAIN failed for this statement
        public string Skipped;            // set when the statement isn't explainable
        public List<KeyValuePair<string, string>> Extra = new List<KeyValuePair<string, string>>(); // Triggers, JIT, Settings...
        public double BatchCostPercent;   // this statement's root cost / sum of root costs in the batch

        public double RootCost => Root?.TotalCost ?? 0;
    }

    internal sealed class PlanNode
    {
        public string NodeType = "";
        public PlanNode Parent;
        public List<PlanNode> Children = new List<PlanNode>();
        public List<KeyValuePair<string, string>> Properties = new List<KeyValuePair<string, string>>();
        public string ParentRelationship;   // Outer / Inner / InitPlan / SubPlan / Member
        public string SubplanName;

        public double StartupCost, TotalCost, PlanRows, PlanWidth;
        public double? ActualStartupMs, ActualTotalMs, ActualRows, ActualLoops;
        public bool NeverExecuted;

        // Derived
        public double ExclusiveCost;
        public double CostPercent;       // exclusive cost / root total cost
        public double? InclusiveMs;      // actual total * loops
        public double? ExclusiveMs;
        public double? TimePercent;      // exclusive / execution time
        public double? RowsFactor;       // max(actual/est, est/actual)
        public bool RowsUnderestimated;
        public List<string> Warnings = new List<string>();
        public int Depth;

        public string Get(string key) => Properties.FirstOrDefault(p => p.Key == key).Value;

        /// <summary>"Index Scan" + qualifier: "using idx on tbl t", "on tbl", join type, strategy...</summary>
        public string Title
        {
            get
            {
                var t = NodeType;
                var join = Get("Join Type");
                var strategy = Get("Strategy");
                if (!string.IsNullOrEmpty(join) && join != "Inner" && (t.Contains("Join") || t == "Nested Loop")) t += $" ({join})";
                if (t == "Aggregate" && !string.IsNullOrEmpty(strategy) && strategy != "Plain") t = strategy + " Aggregate";
                if (t == "ModifyTable") t = Get("Operation") ?? t;
                if (Get("Parallel Aware") == "true") t = "Parallel " + t;
                return t;
            }
        }

        public string Subtitle
        {
            get
            {
                var rel = Get("Relation Name");
                var schema = Get("Schema");
                var alias = Get("Alias");
                var index = Get("Index Name");
                var cte = Get("CTE Name");
                var fn = Get("Function Name");
                var sb = new StringBuilder();
                if (!string.IsNullOrEmpty(index)) sb.Append(index);
                if (!string.IsNullOrEmpty(rel))
                {
                    if (sb.Length > 0) sb.Append(" on ");
                    if (!string.IsNullOrEmpty(schema) && schema != "public") sb.Append(schema).Append('.');
                    sb.Append(rel);
                    if (!string.IsNullOrEmpty(alias) && alias != rel) sb.Append(' ').Append(alias);
                }
                else if (!string.IsNullOrEmpty(cte)) sb.Append(cte);
                else if (!string.IsNullOrEmpty(fn)) sb.Append(fn).Append("()");
                if (!string.IsNullOrEmpty(SubplanName)) { if (sb.Length > 0) sb.Append("  "); sb.Append(SubplanName); }
                return sb.ToString();
            }
        }

        public IEnumerable<PlanNode> SelfAndDescendants()
        {
            yield return this;
            foreach (var c in Children)
                foreach (var d in c.SelfAndDescendants())
                    yield return d;
        }
    }

    internal static class PlanParser
    {
        /// <summary>Parses the single text value EXPLAIN (FORMAT JSON) returns: [ { "Plan": {...}, "Planning Time": .., ... } ].</summary>
        public static StatementPlan Parse(string json, string sql, int index, bool isActual)
        {
            var sp = new StatementPlan { Index = index, Sql = sql, IsActual = isActual, RawJson = json };
            using (var doc = JsonDocument.Parse(json))
            {
                var top = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement[0] : doc.RootElement;
                foreach (var p in top.EnumerateObject())
                {
                    switch (p.Name)
                    {
                        case "Plan": sp.Root = ParseNode(p.Value, null, 0); break;
                        case "Planning Time": sp.PlanningTimeMs = p.Value.GetDouble(); break;
                        case "Execution Time": sp.ExecutionTimeMs = p.Value.GetDouble(); break;
                        default: sp.Extra.Add(new KeyValuePair<string, string>(p.Name, Flatten(p.Value))); break;
                    }
                }
            }
            if (sp.Root != null) Derive(sp);
            return sp;
        }

        private static PlanNode ParseNode(JsonElement e, PlanNode parent, int depth)
        {
            var n = new PlanNode { Parent = parent, Depth = depth };
            foreach (var p in e.EnumerateObject())
            {
                switch (p.Name)
                {
                    case "Plans":
                        foreach (var child in p.Value.EnumerateArray()) n.Children.Add(ParseNode(child, n, depth + 1));
                        continue;
                    case "Node Type": n.NodeType = p.Value.GetString(); break;
                    case "Startup Cost": n.StartupCost = p.Value.GetDouble(); break;
                    case "Total Cost": n.TotalCost = p.Value.GetDouble(); break;
                    case "Plan Rows": n.PlanRows = p.Value.GetDouble(); break;
                    case "Plan Width": n.PlanWidth = p.Value.GetDouble(); break;
                    case "Actual Startup Time": n.ActualStartupMs = p.Value.GetDouble(); break;
                    case "Actual Total Time": n.ActualTotalMs = p.Value.GetDouble(); break;
                    case "Actual Rows": n.ActualRows = p.Value.GetDouble(); break;
                    case "Actual Loops": n.ActualLoops = p.Value.GetDouble(); break;
                    case "Parent Relationship": n.ParentRelationship = p.Value.GetString(); break;
                    case "Subplan Name": n.SubplanName = p.Value.GetString(); break;
                }
                n.Properties.Add(new KeyValuePair<string, string>(p.Name, Flatten(p.Value)));
            }
            n.NeverExecuted = n.ActualLoops.HasValue && n.ActualLoops.Value == 0;
            return n;
        }

        private static string Flatten(JsonElement v)
        {
            switch (v.ValueKind)
            {
                case JsonValueKind.String: return v.GetString();
                case JsonValueKind.Number: return v.GetRawText();
                case JsonValueKind.True: return "true";
                case JsonValueKind.False: return "false";
                case JsonValueKind.Null: return "";
                case JsonValueKind.Array: return string.Join(", ", v.EnumerateArray().Select(Flatten));
                case JsonValueKind.Object: return string.Join("; ", v.EnumerateObject().Select(o => $"{o.Name}: {Flatten(o.Value)}"));
                default: return v.GetRawText();
            }
        }

        private static void Derive(StatementPlan sp)
        {
            var root = sp.Root;
            var rootCost = Math.Max(root.TotalCost, 0.0001);
            var execMs = sp.ExecutionTimeMs ?? root.ActualTotalMs * (root.ActualLoops ?? 1);

            foreach (var n in root.SelfAndDescendants())
            {
                // Costs are cumulative in PostgreSQL; exclusive = own minus direct children (InitPlans included).
                n.ExclusiveCost = Math.Max(0, n.TotalCost - n.Children.Sum(c => c.TotalCost));
                n.CostPercent = n.ExclusiveCost / rootCost * 100.0;

                if (n.ActualTotalMs.HasValue)
                {
                    var loops = Math.Max(1, n.ActualLoops ?? 1);
                    n.InclusiveMs = n.ActualTotalMs.Value * loops;
                    // Parallel workers report per-worker averages; good enough for relative attribution.
                    var childMs = n.Children.Where(c => c.ActualTotalMs.HasValue && c.ParentRelationship != "InitPlan")
                                            .Sum(c => c.ActualTotalMs.Value * Math.Max(1, c.ActualLoops ?? 1));
                    n.ExclusiveMs = Math.Max(0, n.InclusiveMs.Value - childMs);
                    if (execMs.HasValue && execMs.Value > 0) n.TimePercent = n.ExclusiveMs / execMs.Value * 100.0;
                }

                if (n.ActualRows.HasValue && !n.NeverExecuted)
                {
                    var actual = Math.Max(1, n.ActualRows.Value);
                    var est = Math.Max(1, n.PlanRows);
                    n.RowsFactor = Math.Max(actual / est, est / actual);
                    n.RowsUnderestimated = actual > est;
                }
                AddWarnings(n);
            }

            sp.Root.Properties.Insert(0, new KeyValuePair<string, string>("(Statement cost)", rootCost.ToString("N2", CultureInfo.InvariantCulture)));
        }

        private static void AddWarnings(PlanNode n)
        {
            if (n.RowsFactor >= 10 && Math.Max(n.ActualRows ?? 0, n.PlanRows) >= 100)
                n.Warnings.Add($"Row estimate off by {n.RowsFactor:N0}x ({(n.RowsUnderestimated ? "under" : "over")}estimated): consider ANALYZE / extended statistics.");

            var sortMethod = n.Get("Sort Method");
            if (!string.IsNullOrEmpty(sortMethod) && sortMethod.IndexOf("external", StringComparison.OrdinalIgnoreCase) >= 0)
                n.Warnings.Add($"Sort spilled to disk ({sortMethod}, {n.Get("Sort Space Used")} kB): consider more work_mem.");

            if (double.TryParse(n.Get("Hash Batches"), NumberStyles.Float, CultureInfo.InvariantCulture, out var batches) && batches > 1)
                n.Warnings.Add($"Hash used {batches:N0} batches (spilled to disk): consider more work_mem / hash_mem_multiplier.");

            if (double.TryParse(n.Get("Rows Removed by Filter"), NumberStyles.Float, CultureInfo.InvariantCulture, out var removed) &&
                removed >= 1000 && removed > 10 * Math.Max(1, (n.ActualRows ?? 0) * Math.Max(1, n.ActualLoops ?? 1)))
                n.Warnings.Add($"Filter removed {removed:N0} rows to return {(n.ActualRows ?? 0):N0}: an index on the filter columns may help.");

            if (double.TryParse(n.Get("Temp Written Blocks"), NumberStyles.Float, CultureInfo.InvariantCulture, out var temp) && temp > 0)
                n.Warnings.Add($"Wrote {temp:N0} temp blocks ({temp * 8 / 1024:N1} MB).");

            if (n.NodeType == "Index Only Scan" && double.TryParse(n.Get("Heap Fetches"), NumberStyles.Float, CultureInfo.InvariantCulture, out var hf) && hf > 1000)
                n.Warnings.Add($"Index Only Scan did {hf:N0} heap fetches: the visibility map is stale (VACUUM).");

            if (n.NeverExecuted) n.Warnings.Add("Never executed.");
        }

        /// <summary>EXPLAIN-text-like rendering for the Text tab.</summary>
        public static string ToText(StatementPlan sp)
        {
            var sb = new StringBuilder();
            void Walk(PlanNode n, string indent)
            {
                var head = indent.Length == 0 ? "" : indent + "->  ";
                sb.Append(head).Append(n.Title);
                var sub = n.Subtitle;
                if (sub.Length > 0) sb.Append(" ").Append(sub);
                sb.AppendFormat(CultureInfo.InvariantCulture, "  (cost={0:0.00}..{1:0.00} rows={2:0} width={3:0})", n.StartupCost, n.TotalCost, n.PlanRows, n.PlanWidth);
                if (n.NeverExecuted) sb.Append(" (never executed)");
                else if (n.ActualTotalMs.HasValue)
                    sb.AppendFormat(CultureInfo.InvariantCulture, " (actual time={0:0.000}..{1:0.000} rows={2:0} loops={3:0})", n.ActualStartupMs, n.ActualTotalMs, n.ActualRows, n.ActualLoops);
                sb.AppendLine();
                var pad = new string(' ', indent.Length + (indent.Length == 0 ? 2 : 6));
                foreach (var key in new[] { "Output", "Index Cond", "Recheck Cond", "Hash Cond", "Merge Cond", "Join Filter", "Filter", "Rows Removed by Filter",
                                             "Sort Key", "Sort Method", "Group Key", "Hash Buckets", "Hash Batches", "Peak Memory Usage", "Heap Fetches",
                                             "Shared Hit Blocks", "Shared Read Blocks", "Temp Read Blocks", "Temp Written Blocks", "Workers Planned", "Workers Launched" })
                {
                    var v = n.Get(key);
                    if (!string.IsNullOrEmpty(v) && v != "0") sb.Append(pad).Append(key).Append(": ").AppendLine(v);
                }
                foreach (var c in n.Children) Walk(c, indent.Length == 0 ? "  " : indent + "      ");
            }
            if (sp.Root != null) Walk(sp.Root, "");
            if (sp.PlanningTimeMs.HasValue) sb.AppendLine($"Planning Time: {sp.PlanningTimeMs:0.000} ms");
            if (sp.ExecutionTimeMs.HasValue) sb.AppendLine($"Execution Time: {sp.ExecutionTimeMs:0.000} ms");
            foreach (var kv in sp.Extra.Where(kv => !string.IsNullOrWhiteSpace(kv.Value))) sb.AppendLine($"{kv.Key}: {kv.Value}");
            return sb.ToString();
        }
    }
}
