using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace SsmsPgExplorer.Query
{
    /// <summary>
    /// Splits a PostgreSQL batch into statements on top-level semicolons, respecting
    /// 'strings', E'strings', "identifiers", $tag$ dollar quotes $tag$, -- line and /* nested */ comments.
    /// </summary>
    internal static class SqlSplitter
    {
        public static List<string> Split(string sql)
        {
            var result = new List<string>();
            var start = 0;
            var i = 0;
            var n = sql.Length;
            var parens = 0;
            while (i < n)
            {
                var c = sql[i];
                if (c == '-' && i + 1 < n && sql[i + 1] == '-') { i = sql.IndexOf('\n', i); if (i < 0) i = n; continue; }
                if (c == '/' && i + 1 < n && sql[i + 1] == '*')
                {
                    var depth = 1; i += 2;
                    while (i < n && depth > 0)
                    {
                        if (sql[i] == '/' && i + 1 < n && sql[i + 1] == '*') { depth++; i += 2; }
                        else if (sql[i] == '*' && i + 1 < n && sql[i + 1] == '/') { depth--; i += 2; }
                        else i++;
                    }
                    continue;
                }
                if (c == '\'' || c == '"')
                {
                    var escapes = c == '\'' && i > 0 && (sql[i - 1] == 'E' || sql[i - 1] == 'e');
                    i++;
                    while (i < n)
                    {
                        if (escapes && sql[i] == '\\') { i += 2; continue; }
                        if (sql[i] == c) { if (i + 1 < n && sql[i + 1] == c) { i += 2; continue; } i++; break; }
                        i++;
                    }
                    continue;
                }
                if (c == '$')
                {
                    var m = Regex.Match(sql.Substring(i, Math.Min(64, n - i)), @"^\$([A-Za-z_][A-Za-z0-9_]*)?\$");
                    if (m.Success && (i == 0 || !char.IsLetterOrDigit(sql[i - 1])))
                    {
                        var tag = m.Value;
                        var end = sql.IndexOf(tag, i + tag.Length, StringComparison.Ordinal);
                        i = end < 0 ? n : end + tag.Length;
                        continue;
                    }
                }
                if (c == '(') parens++;
                else if (c == ')' && parens > 0) parens--;
                else if (c == ';' && parens == 0)
                {
                    Add(result, sql.Substring(start, i - start));
                    start = i + 1;
                }
                i++;
            }
            Add(result, sql.Substring(start));
            return result;
        }

        private static void Add(List<string> list, string stmt)
        {
            if (!string.IsNullOrWhiteSpace(StripComments(stmt))) list.Add(stmt.Trim());
        }

        public static string StripComments(string sql) =>
            Regex.Replace(Regex.Replace(sql, @"/\*.*?\*/", " ", RegexOptions.Singleline), @"--[^\n]*", " ");

        /// <summary>First keyword of the statement (lower case), skipping comments and parentheses.</summary>
        public static string FirstKeyword(string stmt)
        {
            var m = Regex.Match(StripComments(stmt).TrimStart().TrimStart('('), @"^[A-Za-z]+");
            return m.Success ? m.Value.ToLowerInvariant() : "";
        }

        private static readonly HashSet<string> Explainable = new HashSet<string>
        {
            "select", "with", "insert", "update", "delete", "merge", "values", "table", "execute",
        };

        /// <summary>Statements EXPLAIN accepts (CREATE TABLE AS / DECLARE are left out on purpose: they'd create objects).</summary>
        public static bool IsExplainable(string stmt) => Explainable.Contains(FirstKeyword(stmt));

        /// <summary>True for statements that can change data; EXPLAIN ANALYZE of these runs inside BEGIN ... ROLLBACK.</summary>
        public static bool MayWrite(string stmt)
        {
            var kw = FirstKeyword(stmt);
            if (kw == "insert" || kw == "update" || kw == "delete" || kw == "merge") return true;
            // WITH ... (data-modifying CTE) or SELECT calling volatile functions can write too; be conservative for WITH.
            return kw == "with" && Regex.IsMatch(StripComments(stmt), @"\b(insert|update|delete|merge)\b", RegexOptions.IgnoreCase);
        }

        public static string OneLine(string stmt, int max = 120)
        {
            var s = Regex.Replace(StripComments(stmt), @"\s+", " ").Trim();
            return s.Length <= max ? s : s.Substring(0, max - 3) + "...";
        }
    }
}
