using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using SsmsPgExplorer.UI;

namespace SsmsPgExplorer.Query
{
    internal enum Casing { Preserve, Uppercase, Lowercase, PascalCase }

    /// <summary>
    /// SSMS 22.7+ SQL Formatter options (Tools > Options > SQL Formatter), read live from the user's SSMS
    /// Unified Settings (sqlFormatter.*). Only the options meaningful for PostgreSQL are used.
    /// </summary>
    internal sealed class FormatOptions
    {
        public Casing KeywordCasing = Casing.Uppercase;
        public Casing FunctionCasing = Casing.Preserve;
        public Casing IdentifierCasing = Casing.Preserve;
        public bool LeadingCommas;
        public int LeadingCommaSpaces = 1;
        public bool UseTabs;
        public int IndentSize = 4;
        public bool Aligned = true;
        public bool MultiSelect = true, MultiWhere = true, MultiHaving = true, MultiGroupBy = true, MultiOrderBy = true, MultiSet = true, MultiInsertSources = true;
        public bool NlBeforeFrom = true, NlBeforeWhere = true, NlBeforeGroupBy = true, NlBeforeHaving = true, NlBeforeOrderBy = true, NlBeforeOffset = true,
                    NlBeforeWindow = true, NlBeforeJoin = true, NlBeforeOn = true, NlAfterJoin = true, NlBeforeCloseParen = true;
        public int NewlinesAfterStatement = 1;
        public bool AsOnOwnLine = true, IndentViewBody, IndentSetClause;
        public bool SpaceTypeParen = true;

        private const string P = "sqlFormatter.";

        public static FormatOptions FromSsms()
        {
            var o = new FormatOptions();
            try
            {
                o.KeywordCasing = ParseCasing(SsmsSettings.String(P + "formatting.keywordCasing", "Uppercase"), Casing.Uppercase);
                o.FunctionCasing = ParseCasing(SsmsSettings.String(P + "formatting.builtInFunctionCasing", "Preserve"), Casing.Preserve);
                o.IdentifierCasing = ParseCasing(SsmsSettings.String(P + "formatting.identifierCasing", "Preserve"), Casing.Preserve);
                o.LeadingCommas = SsmsSettings.String(P + "formatting.commaPlacement", "Trailing").Equals("Leading", StringComparison.OrdinalIgnoreCase);
                o.LeadingCommaSpaces = Math.Max(0, Math.Min(1, SsmsSettings.Int(P + "formatting.leadingCommaSpaceCount", 1)));
                o.UseTabs = SsmsSettings.String(P + "indentation.indentationMode", "Spaces").Equals("Tabs", StringComparison.OrdinalIgnoreCase);
                o.IndentSize = Math.Max(1, Math.Min(8, SsmsSettings.Int(P + "indentation.indentationSize", 4)));
                o.IndentViewBody = SsmsSettings.Bool(P + "indentation.indentViewBody", false);
                o.IndentSetClause = SsmsSettings.Bool(P + "indentation.indentSetClause", false);
                o.Aligned = SsmsSettings.Bool(P + "alignment.alignClauseBodies", true)
                            && SsmsSettings.String(P + "alignment.clauseBodyAlignment", "Aligned").Equals("Aligned", StringComparison.OrdinalIgnoreCase);
                o.MultiSelect = SsmsSettings.Bool(P + "multiline.multilineSelectElementsList", true);
                o.MultiWhere = SsmsSettings.Bool(P + "multiline.multilineWherePredicatesList", true);
                o.MultiHaving = SsmsSettings.Bool(P + "multiline.multilineHavingPredicatesList", true);
                o.MultiGroupBy = SsmsSettings.Bool(P + "multiline.multilineGroupByElementsList", true);
                o.MultiOrderBy = SsmsSettings.Bool(P + "multiline.multilineOrderByElementsList", true);
                o.MultiSet = SsmsSettings.Bool(P + "multiline.multilineSetClauseItems", true);
                o.MultiInsertSources = SsmsSettings.Bool(P + "multiline.multilineInsertSourcesList", true);
                o.NlBeforeFrom = SsmsSettings.Bool(P + "newLine.newLineBeforeFromClause", true);
                o.NlBeforeWhere = SsmsSettings.Bool(P + "newLine.newLineBeforeWhereClause", true);
                o.NlBeforeGroupBy = SsmsSettings.Bool(P + "newLine.newLineBeforeGroupByClause", true);
                o.NlBeforeHaving = SsmsSettings.Bool(P + "newLine.newLineBeforeHavingClause", true);
                o.NlBeforeOrderBy = SsmsSettings.Bool(P + "newLine.newLineBeforeOrderByClause", true);
                o.NlBeforeOffset = SsmsSettings.Bool(P + "newLine.newLineBeforeOffsetClause", true);
                o.NlBeforeWindow = SsmsSettings.Bool(P + "newLine.newLineBeforeWindowClause", true);
                o.NlBeforeJoin = SsmsSettings.Bool(P + "newLine.newLineBeforeJoinClause", true);
                o.NlBeforeOn = SsmsSettings.Bool(P + "newLine.newLineBeforeOnClause", true);
                o.NlAfterJoin = SsmsSettings.Bool(P + "newLine.newLineAfterJoinKeyword", true);
                o.NlBeforeCloseParen = SsmsSettings.Bool(P + "newLine.newLineBeforeCloseParenthesisInMultilineList", true);
                o.NewlinesAfterStatement = Math.Max(1, Math.Min(10, SsmsSettings.Int(P + "newLine.numNewlinesAfterStatement", 1)));
                o.AsOnOwnLine = SsmsSettings.Bool(P + "formatting.asKeywordOnOwnLine", true);
                o.SpaceTypeParen = SsmsSettings.Bool(P + "spacing.spaceBetweenDataTypeAndParameters", true);
            }
            catch (Exception ex) { Diagnostics.Log.Error("format options", ex); }
            return o;
        }

        private static Casing ParseCasing(string s, Casing fallback) => Enum.TryParse<Casing>(s, true, out var c) ? c : fallback;

        public string Describe() =>
            $"keywords {KeywordCasing}, {(Aligned ? "aligned" : "indented")} clause bodies, indent {IndentSize}{(UseTabs ? " tabs" : "")}, {(LeadingCommas ? "leading" : "trailing")} commas";
    }

    /// <summary>
    /// PostgreSQL formatter for Format SQL (Ctrl+K, Ctrl+Q). Token-based and meaning-preserving: strings, E'' strings,
    /// $tag$ bodies, "quoted identifiers" and comments are copied verbatim; only whitespace and the case of
    /// keywords / built-in functions / unquoted identifiers change (unquoted identifiers are case-insensitive in PostgreSQL).
    /// </summary>
    internal static class SqlFormatter
    {
        private enum Kind { Word, QuotedIdent, String, Number, LineComment, BlockComment, Punct, Op }

        private sealed class Token
        {
            public Kind Kind;
            public string Text;
            public bool NewlineBefore;
            public string Upper => Text.ToUpperInvariant();
            public override string ToString() => Text;
        }

        private const int AlignWidth = 9; // "ORDER BY " - clause bodies start in this column when Aligned

        private static readonly HashSet<string> Keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "SELECT","FROM","WHERE","AND","OR","NOT","NULL","IS","IN","EXISTS","BETWEEN","LIKE","ILIKE","SIMILAR","AS","ON","JOIN","LEFT","RIGHT",
            "FULL","INNER","OUTER","CROSS","NATURAL","GROUP","BY","ORDER","HAVING","LIMIT","OFFSET","UNION","ALL","DISTINCT","INTERSECT","EXCEPT",
            "INSERT","INTO","VALUES","UPDATE","SET","DELETE","RETURNING","WITH","RECURSIVE","CASE","WHEN","THEN","ELSE","END","ASC","DESC","NULLS",
            "FIRST","LAST","CREATE","TABLE","INDEX","UNIQUE","PRIMARY","KEY","FOREIGN","REFERENCES","CONSTRAINT","DEFAULT","CHECK","ALTER","ADD",
            "DROP","COLUMN","IF","CASCADE","RESTRICT","TRUE","FALSE","OVER","PARTITION","WINDOW","FILTER","USING","LATERAL","ONLY","FETCH","NEXT",
            "ROWS","ROW","ANY","SOME","CAST","COLLATE","CONFLICT","DO","NOTHING","TRUNCATE","VIEW","MATERIALIZED","SEQUENCE","TRIGGER","FUNCTION",
            "PROCEDURE","RETURNS","LANGUAGE","BEGIN","COMMIT","ROLLBACK","SAVEPOINT","EXPLAIN","ANALYZE","VERBOSE","GENERATED","ALWAYS","IDENTITY",
            "STORED","TEMPORARY","TEMP","UNLOGGED","INHERITS","TABLESPACE","COMMENT","GRANT","REVOKE","TO","OWNER","SCHEMA","EXTENSION","TYPE",
            "INTERVAL","TIMESTAMP","TIME","ZONE","ARRAY","FOR","OF","NO","ACTION","DEFERRABLE","INITIALLY","DEFERRED","IMMEDIATE","MATCH",
            "PARTIAL","SIMPLE","CONCURRENTLY","TABLESAMPLE","ORDINALITY","PRECEDING","FOLLOWING","UNBOUNDED","CURRENT","RANGE","GROUPS","EXCLUDE",
            "TIES","OTHERS","LOCK","SHARE","NOWAIT","SKIP","LOCKED","COPY","VACUUM","REINDEX","CLUSTER","REFRESH","RENAME","ENABLE","DISABLE",
            "POLICY","SECURITY","LEVEL","FORCE","STATISTICS","ATTACH","DETACH","OWNED","PUBLIC","ROLE","USER","PRIVILEGES","EXECUTE","USAGE",
            "BOOLEAN","INTEGER","INT","BIGINT","SMALLINT","TEXT","VARCHAR","CHAR","CHARACTER","VARYING","NUMERIC","DECIMAL","REAL","DOUBLE",
            "PRECISION","SERIAL","BIGSERIAL","UUID","JSON","JSONB","BYTEA","DATE","CURRENT_DATE","CURRENT_TIMESTAMP","CURRENT_USER","SESSION_USER",
            "LOCAL","SHOW","RESET","DECLARE","CURSOR","CLOSE","MOVE","PREPARE","DEALLOCATE","LISTEN","NOTIFY","MERGE","MATCHED","ESCAPE",
            "SYMMETRIC","ASYMMETRIC","WITHIN","WITHOUT","INCLUDE",
        };

        private static readonly HashSet<string> DataTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "VARCHAR","CHAR","CHARACTER","VARYING","NUMERIC","DECIMAL","TIMESTAMP","TIME","INTERVAL","BIT","VARBIT","FLOAT" };

        private static readonly HashSet<string> BuiltInFunctions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "count","sum","avg","min","max","now","coalesce","nullif","greatest","least","lower","upper","length","char_length","substring","substr",
            "trim","btrim","ltrim","rtrim","replace","position","concat","concat_ws","format","left","right","lpad","rpad","split_part","string_agg",
            "array_agg","array_length","unnest","to_char","to_date","to_timestamp","to_number","date_trunc","date_part","extract","age","abs",
            "round","floor","ceil","ceiling","trunc","mod","power","sqrt","random","row_number","rank","dense_rank","percent_rank","cume_dist",
            "ntile","lag","lead","first_value","last_value","nth_value","json_agg","jsonb_agg","json_build_object","jsonb_build_object",
            "json_object_agg","jsonb_object_agg","jsonb_set","to_json","to_jsonb","generate_series","exists","cast","nextval","currval","setval",
            "pg_size_pretty","pg_total_relation_size","pg_relation_size","md5","encode","decode","regexp_replace","regexp_matches","bool_and",
            "bool_or","every","percentile_cont","percentile_disc","mode","clock_timestamp","statement_timestamp","transaction_timestamp","gen_random_uuid",
        };

        private static readonly HashSet<string> SpaceBeforeParen = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "IN","EXISTS","ANY","SOME","ALL","VALUES","AS","ON","USING","OVER","FILTER","FROM","JOIN","SELECT","WHERE","AND","OR","NOT","TABLE",
            "INTO","WITH","KEY","REFERENCES","CHECK","UNIQUE","PRIMARY","INCLUDE","BY","THEN","ELSE","WHEN","CASE","RETURNS","INHERITS","SET",
            "EXCLUDE","LATERAL","CONFLICT","PARTITION","IF","IS","LIKE","ILIKE","BETWEEN","RETURNING","UNION","EXCEPT","INTERSECT","DO","ROW",
        };

        private static readonly HashSet<string> QueryStatements = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "SELECT", "WITH", "INSERT", "UPDATE", "DELETE", "VALUES", "MERGE", "TABLE", "EXPLAIN" };

        private static readonly HashSet<string> JoinWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "JOIN", "LEFT", "RIGHT", "FULL", "INNER", "CROSS", "NATURAL" };

        public static string Format(string sql) => Format(sql, FormatOptions.FromSsms());

        public static string Format(string sql, FormatOptions o)
        {
            var tokens = Tokenize(sql);
            var f = new Writer(o);
            var frames = new Stack<Frame>();
            var frame = new Frame { Base = 0 };
            string stmtKeyword = null;
            var queryMode = false;
            var createTable = false;
            var tableBlockOpened = false;
            var createView = false;
            var pendingJoinBreak = false;

            int Body(Frame fr) => fr.Base + (o.Aligned ? AlignWidth : o.IndentSize);

            bool Multi(string clause)
            {
                switch (clause)
                {
                    case "SELECT": return o.MultiSelect;
                    case "GROUP BY": return o.MultiGroupBy;
                    case "ORDER BY": return o.MultiOrderBy;
                    case "SET": return o.MultiSet;
                    case "VALUES": return o.MultiInsertSources;
                    case "RETURNING": return o.MultiSelect;
                    default: return false;
                }
            }

            bool MultiCond(string clause) =>
                clause == "WHERE" ? o.MultiWhere : clause == "HAVING" ? o.MultiHaving : clause == "ON" ? o.MultiWhere : false;

            // Start a clause: keyword on its own line (or inline when that newline option is off), body aligned or indented.
            void Clause(string keyword, bool newlineBefore, bool bodyOnNextLine = false)
            {
                if (!f.Empty && newlineBefore) f.NewLine(frame.Base);
                f.Write(Kw(keyword, o), spaceBefore: !f.AtLineStart);
                if (o.Aligned && !bodyOnNextLine) f.PadTo(Body(frame), minSpaces: 1);
                else f.NewLine(Body(frame));
                frame.Clause = keyword;
            }

            for (var i = 0; i < tokens.Count; i++)
            {
                var t = tokens[i];
                var prev = PrevSig(tokens, i);
                var next = NextSig(tokens, i);

                if (t.Kind == Kind.LineComment)
                {
                    if (t.NewlineBefore && !f.AtLineStart) f.NewLine(f.CurrentIndent);
                    f.Write(t.Text, spaceBefore: !f.AtLineStart);
                    f.NewLine(frame.Clause != null && frame.Depth == 0 ? Body(frame) : frame.Base);
                    continue;
                }
                if (t.Kind == Kind.BlockComment)
                {
                    if (t.NewlineBefore && !f.AtLineStart) f.NewLine(frame.Base);
                    f.Write(t.Text, spaceBefore: !f.AtLineStart);
                    if (t.Text.Contains("\n")) f.NewLine(frame.Base);
                    continue;
                }

                if (t.Kind == Kind.Punct && t.Text == ";")
                {
                    f.Write(";", spaceBefore: false);
                    f.EndStatement(o.NewlinesAfterStatement);
                    frames.Clear();
                    frame = new Frame { Base = 0 };
                    stmtKeyword = null; queryMode = false; createTable = false; tableBlockOpened = false; createView = false; pendingJoinBreak = false;
                    continue;
                }

                var isWord = t.Kind == Kind.Word;
                var afterDot = prev != null && (prev.Text == "." || prev.Text == "::");
                var beforeDot = next != null && next.Text == ".";
                var upper = isWord ? t.Upper : null;
                var isKeyword = isWord && !afterDot && !beforeDot && Keywords.Contains(upper);

                if (pendingJoinBreak) { pendingJoinBreak = false; f.NewLine(Body(frame)); }

                if (stmtKeyword == null && isWord)
                {
                    stmtKeyword = upper;
                    queryMode = QueryStatements.Contains(upper);
                    createTable = upper == "CREATE";
                }
                if (stmtKeyword == "CREATE" && isKeyword && frame.Depth == 0)
                {
                    if (upper == "VIEW") { createView = true; createTable = false; }
                    if (upper == "INDEX" || upper == "FUNCTION" || upper == "PROCEDURE" || upper == "TRIGGER" || upper == "POLICY" || upper == "SEQUENCE") createTable = false;
                }

                // CREATE VIEW / CREATE TABLE ... AS SELECT: "AS" on its own line (asKeywordOnOwnLine), body optionally indented.
                if (isKeyword && upper == "AS" && frame.Depth == 0 && next != null && (next.Upper == "SELECT" || next.Upper == "WITH") && !queryMode)
                {
                    createTable = false;
                    if (o.AsOnOwnLine || createView) { f.NewLine(0); f.Write(Kw("AS", o), false); }
                    else f.Write(Kw("AS", o), true);
                    frame.Base = createView && o.IndentViewBody ? o.IndentSize : 0;
                    queryMode = true;
                    f.NewLine(frame.Base);
                    continue;
                }

                // ---------- clause handling (query mode, top level of the current frame) ----------
                if (queryMode && isKeyword && frame.Depth == 0)
                {
                    switch (upper)
                    {
                        case "SELECT":
                            Clause("SELECT", newlineBefore: true, bodyOnNextLine: !o.Aligned);
                            if (ConsumeSelectModifiers(tokens, ref i, f, o)) { if (o.Aligned) f.NewLine(Body(frame)); }
                            continue;
                        case "FROM":
                            if (prev != null && (prev.Upper == "DELETE" || prev.Upper == "DISTINCT" || prev.Upper == "IS")) break;
                            Clause("FROM", o.NlBeforeFrom); continue;
                        case "WHERE":
                            if (prev != null && prev.Upper == "CURRENT") break;
                            Clause("WHERE", o.NlBeforeWhere); continue;
                        case "HAVING": Clause("HAVING", o.NlBeforeHaving); continue;
                        case "WINDOW": Clause("WINDOW", o.NlBeforeWindow); continue;
                        case "LIMIT": Clause("LIMIT", true); continue;
                        case "OFFSET": Clause("OFFSET", o.NlBeforeOffset); continue;
                        case "FETCH":
                            if (prev != null && prev.Upper == "FOR") break;
                            Clause("FETCH", o.NlBeforeOffset); continue;
                        case "RETURNING": Clause("RETURNING", true); continue;
                        case "GROUP":
                        case "ORDER":
                            if (next != null && next.Upper == "BY")
                            {
                                SkipTo(tokens, ref i, next);
                                Clause(upper + " BY", upper == "GROUP" ? o.NlBeforeGroupBy : o.NlBeforeOrderBy);
                                continue;
                            }
                            break;
                        case "UNION":
                        case "INTERSECT":
                        case "EXCEPT":
                            f.NewLine(frame.Base);
                            f.Write(Kw(upper, o), false);
                            if (next != null && (next.Upper == "ALL" || next.Upper == "DISTINCT")) { f.Write(Kw(next.Upper, o), true); SkipTo(tokens, ref i, next); }
                            frame.Clause = null;
                            continue;
                        case "VALUES":
                            if (prev != null && prev.Text == "(") break;
                            Clause("VALUES", true, bodyOnNextLine: true); continue;
                        case "SET":
                            if (stmtKeyword != "UPDATE" && !(prev != null && prev.Upper == "UPDATE")) break;
                            Clause("SET", true, bodyOnNextLine: o.IndentSetClause); continue;
                        case "INSERT":
                        case "UPDATE":
                        case "DELETE":
                        case "MERGE":
                            if (prev != null && (prev.Upper == "ON" || prev.Upper == "FOR" || prev.Upper == "DO" || prev.Upper == "THEN" || prev.Upper == "OF" || prev.Text == ",")) break;
                            if (!f.Empty) f.NewLine(frame.Base);
                            f.Write(Kw(upper, o), !f.AtLineStart);
                            frame.Clause = upper;
                            continue;
                        case "WITH":
                            if (next != null && (next.Upper == "TIME" || next.Upper == "ORDINALITY" || next.Upper == "CHECK" || next.Upper == "NO" || next.Upper == "HOLD" || next.Upper == "GRANT" || next.Upper == "TIES" || next.Upper == "LOCAL" || next.Upper == "CASCADED")) break;
                            Clause("WITH", true);
                            if (next != null && next.Upper == "RECURSIVE") { f.Write(Kw("RECURSIVE", o), false); f.Write("", true); SkipTo(tokens, ref i, next); }
                            continue;
                        case "AND":
                        case "OR":
                            if (MultiCond(frame.Clause) && !(prev != null && prev.Upper == "BETWEEN") && !InBetween(tokens, i))
                            {
                                f.NewLine(Body(frame));
                                f.Write(Kw(upper, o), false);
                                continue;
                            }
                            break;
                        case "ON":
                            if (frame.Clause == "JOIN")
                            {
                                if (o.NlBeforeOn) f.NewLine(Body(frame));
                                f.Write(Kw("ON", o), !f.AtLineStart);
                                frame.Clause = "ON";
                                continue;
                            }
                            if (next != null && next.Upper == "CONFLICT")
                            {
                                SkipTo(tokens, ref i, next);
                                f.NewLine(frame.Base);
                                f.Write(Kw("ON", o) + " " + Kw("CONFLICT", o), false);
                                frame.Clause = "ON CONFLICT";
                                continue;
                            }
                            break;
                        default:
                            if (JoinWords.Contains(upper) && (frame.Clause == "FROM" || frame.Clause == "JOIN" || frame.Clause == "ON") &&
                                !(prev != null && JoinWords.Contains(prev.Upper) && prev.Upper != "JOIN") && prev?.Upper != "OUTER")
                            {
                                if (o.NlBeforeJoin) f.NewLine(Body(frame));
                                // write the whole join phrase (LEFT OUTER JOIN) here
                                var phrase = new List<string> { Kw(upper, o) };
                                while (upper != "JOIN" && next != null && (JoinWords.Contains(next.Upper) || next.Upper == "OUTER"))
                                {
                                    SkipTo(tokens, ref i, next);
                                    phrase.Add(Kw(next.Upper, o));
                                    var done = next.Upper == "JOIN";
                                    next = NextSig(tokens, i);
                                    if (done) break;
                                }
                                f.Write(string.Join(" ", phrase), !f.AtLineStart);
                                frame.Clause = "JOIN";
                                pendingJoinBreak = o.NlAfterJoin;
                                continue;
                            }
                            break;
                    }
                }

                // ---------- punctuation ----------
                if (t.Kind == Kind.Punct)
                {
                    switch (t.Text)
                    {
                        case "(":
                        {
                            var subquery = next != null && next.Kind == Kind.Word && (next.Upper == "SELECT" || next.Upper == "WITH" || next.Upper == "VALUES");
                            var tableBlock = createTable && !tableBlockOpened && frame.Depth == 0;
                            var typeParen = prev != null && prev.Kind == Kind.Word && DataTypes.Contains(prev.Text);
                            var call = prev != null && (prev.Kind == Kind.Word || prev.Kind == Kind.QuotedIdent) && !SpaceBeforeParen.Contains(prev.Text) && !tableBlock && !(typeParen && o.SpaceTypeParen);
                            var openIndent = f.CurrentIndent;
                            f.Write("(", spaceBefore: !call && !f.AtLineStart && f.LastChar != '(');
                            frames.Push(frame);
                            if (subquery)
                            {
                                frame = new Frame { Base = Body(frames.Peek()), Subquery = true, OpenIndent = openIndent };
                                f.NewLine(frame.Base);
                            }
                            else if (tableBlock)
                            {
                                tableBlockOpened = true;
                                frame = new Frame { Base = frame.Base, TableBlock = true, OpenIndent = openIndent };
                                f.NewLine(frame.Base + o.IndentSize);
                            }
                            else
                            {
                                var outer = frame;
                                frame = new Frame { Base = outer.Base, Depth = outer.Depth + 1, Clause = outer.Clause, Inline = true, OpenIndent = openIndent };
                            }
                            continue;
                        }
                        case ")":
                        {
                            var closing = frame;
                            frame = frames.Count > 0 ? frames.Pop() : new Frame();
                            // newLineBeforeCloseParenthesisInMultilineList: ")" on its own line, aligned with the subquery body.
                            if ((closing.Subquery && o.NlBeforeCloseParen) || closing.TableBlock) f.NewLine(closing.Subquery ? closing.Base : frame.Base);
                            f.Write(")", spaceBefore: false);
                            continue;
                        }
                        case ",":
                            var listBreak = frame.TableBlock || (queryMode && frame.Depth == 0 && frame.Clause != null && Multi(frame.Clause));
                            var cteBreak = queryMode && frame.Depth == 0 && frame.Clause == "WITH";
                            if (!listBreak && !cteBreak) { f.Write(",", spaceBefore: false); continue; }
                            var indent = frame.TableBlock ? frame.Base + o.IndentSize : cteBreak ? Body(frame) : Body(frame);
                            if (o.LeadingCommas && !frame.TableBlock)
                            {
                                f.NewLine(Math.Max(frame.Base, indent - 1 - o.LeadingCommaSpaces));
                                f.Write("," + new string(' ', o.LeadingCommaSpaces), spaceBefore: false);
                                f.Glue();
                            }
                            else
                            {
                                f.Write(",", spaceBefore: false);
                                f.NewLine(indent);
                            }
                            continue;
                        case ".":
                        case "::":
                            f.Write(t.Text, spaceBefore: false);
                            continue;
                        case "[":
                        case "]":
                            f.Write(t.Text, spaceBefore: false);
                            continue;
                    }
                }

                // In a CREATE TABLE column list the first word of each entry is a column name: never re-case it.
                var columnName = frame.TableBlock && prev != null && (prev.Text == "(" || prev.Text == ",") && upper != "CONSTRAINT" && upper != "PRIMARY"
                                 && upper != "UNIQUE" && upper != "CHECK" && upper != "FOREIGN" && upper != "EXCLUDE" && upper != "LIKE";
                string text;
                if (isKeyword && !columnName) text = Kw(t.Text, o);
                else if (isWord && !afterDot && next != null && next.Text == "(" && BuiltInFunctions.Contains(t.Text)) text = ApplyCase(t.Text, o.FunctionCasing);
                else if (isWord && !columnName && !t.Text.StartsWith("$")) text = ApplyCase(t.Text, o.IdentifierCasing);
                else text = t.Text;

                var noSpace = prev != null && (prev.Text == "(" || prev.Text == "." || prev.Text == "::" || prev.Text == "[") || f.Glued;
                f.Write(text, spaceBefore: !noSpace && !f.AtLineStart);
            }
            return f.ToString();
        }

        private static string Kw(string word, FormatOptions o) => ApplyCase(word, o.KeywordCasing == Casing.Preserve ? Casing.Uppercase : o.KeywordCasing);

        internal static string ApplyCase(string word, Casing casing)
        {
            switch (casing)
            {
                case Casing.Uppercase: return word.ToUpperInvariant();
                case Casing.Lowercase: return word.ToLowerInvariant();
                case Casing.PascalCase:
                    return string.Join("_", word.Split('_').Select(p => p.Length == 0 ? p : char.ToUpperInvariant(p[0]) + p.Substring(1).ToLowerInvariant()));
                default: return word;
            }
        }

        /// <summary>SELECT DISTINCT [ON (...)] / SELECT ALL stay with SELECT. Returns true if something was written.</summary>
        private static bool ConsumeSelectModifiers(List<Token> tokens, ref int i, Writer f, FormatOptions o)
        {
            var next = NextSig(tokens, i);
            if (next == null || !(next.Upper == "DISTINCT" || next.Upper == "ALL")) return false;
            SkipTo(tokens, ref i, next);
            f.Write(Kw(next.Upper, o), !f.AtLineStart);
            var on = NextSig(tokens, i);
            if (next.Upper == "DISTINCT" && on != null && on.Upper == "ON")
            {
                SkipTo(tokens, ref i, on);
                f.Write(Kw("ON", o), true);
                var open = NextSig(tokens, i);
                if (open != null && open.Text == "(")
                {
                    var depth = 0;
                    var first = true;
                    do
                    {
                        i++;
                        var t = tokens[i];
                        if (t.Text == "(") depth++;
                        if (t.Text == ")") depth--;
                        if (t.Kind == Kind.LineComment || t.Kind == Kind.BlockComment) continue;
                        var noSpace = t.Text == ")" || t.Text == "," || t.Text == "." || tokens[i - 1].Text == "(" || tokens[i - 1].Text == ".";
                        f.Write(t.Kind == Kind.Word && Keywords.Contains(t.Text) ? Kw(t.Text, o) : t.Text, spaceBefore: first || !noSpace);
                        first = false;
                    } while (depth > 0 && i < tokens.Count - 1);
                }
            }
            return true;
        }

        private static bool InBetween(List<Token> tokens, int i)
        {
            var depth = 0;
            for (var j = i - 1; j >= 0; j--)
            {
                var t = tokens[j];
                if (t.Text == ")") depth++;
                else if (t.Text == "(") { if (depth == 0) return false; depth--; }
                if (depth > 0 || t.Kind != Kind.Word) continue;
                var u = t.Upper;
                if (u == "BETWEEN") return true;
                if (u == "AND" || u == "OR" || u == "WHERE" || u == "ON" || u == "HAVING" || u == "WHEN" || u == "THEN") return false;
            }
            return false;
        }

        private static void SkipTo(List<Token> tokens, ref int i, Token target)
        {
            while (i < tokens.Count - 1 && !ReferenceEquals(tokens[i], target)) i++;
        }

        private static Token PrevSig(List<Token> tokens, int i)
        {
            for (var j = i - 1; j >= 0; j--) if (tokens[j].Kind != Kind.LineComment && tokens[j].Kind != Kind.BlockComment) return tokens[j];
            return null;
        }

        private static Token NextSig(List<Token> tokens, int i)
        {
            for (var j = i + 1; j < tokens.Count; j++) if (tokens[j].Kind != Kind.LineComment && tokens[j].Kind != Kind.BlockComment) return tokens[j];
            return null;
        }

        private sealed class Frame
        {
            public int Base;
            public int Depth;
            public string Clause;
            public bool Subquery, TableBlock, Inline;
            public int OpenIndent;
        }

        private sealed class Writer
        {
            private readonly StringBuilder _sb = new StringBuilder();
            private readonly FormatOptions _o;
            public Writer(FormatOptions o) { _o = o; }
            public bool AtLineStart { get; private set; } = true;
            public bool Empty => _sb.Length == 0;
            public bool Glued { get; private set; }
            public int CurrentIndent { get; private set; }
            public char LastChar => _sb.Length == 0 ? '\0' : _sb[_sb.Length - 1];

            private int Column
            {
                get
                {
                    var nl = LastNewline();
                    var col = 0;
                    for (var k = nl + 1; k < _sb.Length; k++) col += _sb[k] == '\t' ? _o.IndentSize - col % _o.IndentSize : 1;
                    return col;
                }
            }

            private int LastNewline()
            {
                for (var k = _sb.Length - 1; k >= 0; k--) if (_sb[k] == '\n') return k;
                return -1;
            }

            public void Glue() => Glued = true;

            public void Write(string s, bool spaceBefore)
            {
                if (s.Length == 0) { if (spaceBefore && !AtLineStart && LastChar != ' ') _sb.Append(' '); return; }
                if (!AtLineStart && spaceBefore && !Glued && LastChar != ' ') _sb.Append(' ');
                _sb.Append(s);
                AtLineStart = false;
                Glued = false;
            }

            /// <summary>Pad with spaces so the next text starts at <paramref name="col"/> (aligned clause bodies).</summary>
            public void PadTo(int col, int minSpaces)
            {
                var pad = Math.Max(minSpaces, col - Column);
                _sb.Append(' ', pad);
                Glued = true; // body follows without another space
            }

            public void NewLine(int indent)
            {
                TrimTrailingSpaces();
                if (_sb.Length > 0 && !AtLineStart) _sb.Append("\r\n");
                else if (_sb.Length > 0) _sb.Length = LastNewline() + 1;
                AppendIndent(indent);
                CurrentIndent = indent;
                AtLineStart = true;
                Glued = false;
            }

            private void AppendIndent(int cols)
            {
                if (_o.UseTabs) { _sb.Append('\t', cols / _o.IndentSize); _sb.Append(' ', cols % _o.IndentSize); }
                else _sb.Append(' ', Math.Max(0, cols));
            }

            public void EndStatement(int newlines)
            {
                TrimTrailingSpaces();
                for (var k = 0; k < Math.Max(1, newlines); k++) _sb.Append("\r\n");
                AtLineStart = true;
                CurrentIndent = 0;
                Glued = false;
            }

            private void TrimTrailingSpaces()
            {
                while (_sb.Length > 0 && (_sb[_sb.Length - 1] == ' ' || _sb[_sb.Length - 1] == '\t') && !AtLineStartIndentOnly()) _sb.Length--;
            }

            private bool AtLineStartIndentOnly() => false;

            public override string ToString()
            {
                var s = _sb.ToString();
                s = Regex.Replace(s, @"[ \t]+(\r?\n)", "$1");
                return s.TrimEnd() + "\r\n";
            }
        }

        // ---------- tokenizer ----------

        private static List<Token> Tokenize(string sql)
        {
            var list = new List<Token>();
            var i = 0;
            var n = sql.Length;
            var newline = false;
            while (i < n)
            {
                var c = sql[i];
                if (char.IsWhiteSpace(c)) { if (c == '\n') newline = true; i++; continue; }
                var start = i;
                Kind kind;
                if (c == '-' && i + 1 < n && sql[i + 1] == '-')
                {
                    var end = sql.IndexOf('\n', i); if (end < 0) end = n;
                    i = end;
                    list.Add(new Token { Kind = Kind.LineComment, Text = sql.Substring(start, i - start).TrimEnd('\r'), NewlineBefore = newline });
                    newline = false;
                    continue;
                }
                if (c == '/' && i + 1 < n && sql[i + 1] == '*')
                {
                    var depth = 1; i += 2;
                    while (i < n && depth > 0)
                    {
                        if (sql[i] == '/' && i + 1 < n && sql[i + 1] == '*') { depth++; i += 2; }
                        else if (sql[i] == '*' && i + 1 < n && sql[i + 1] == '/') { depth--; i += 2; }
                        else i++;
                    }
                    kind = Kind.BlockComment;
                }
                else if (c == '\'' || ((c == 'E' || c == 'e' || c == 'B' || c == 'b' || c == 'X' || c == 'x' || c == 'U' || c == 'u') && i + 1 < n && sql[i + 1] == '\''
                                       && (i == 0 || !(char.IsLetterOrDigit(sql[i - 1]) || sql[i - 1] == '_'))))
                {
                    var escapes = c == 'E' || c == 'e';
                    if (c != '\'') i++;
                    i++;
                    while (i < n)
                    {
                        if (escapes && sql[i] == '\\') { i += 2; continue; }
                        if (sql[i] == '\'') { if (i + 1 < n && sql[i + 1] == '\'') { i += 2; continue; } i++; break; }
                        i++;
                    }
                    kind = Kind.String;
                }
                else if (c == '"')
                {
                    i++;
                    while (i < n) { if (sql[i] == '"') { if (i + 1 < n && sql[i + 1] == '"') { i += 2; continue; } i++; break; } i++; }
                    kind = Kind.QuotedIdent;
                }
                else if (c == '$' && Regex.IsMatch(sql.Substring(i, Math.Min(64, n - i)), @"^\$([A-Za-z_][A-Za-z0-9_]*)?\$"))
                {
                    var tag = Regex.Match(sql.Substring(i, Math.Min(64, n - i)), @"^\$([A-Za-z_][A-Za-z0-9_]*)?\$").Value;
                    var end = sql.IndexOf(tag, i + tag.Length, StringComparison.Ordinal);
                    i = end < 0 ? n : end + tag.Length;
                    kind = Kind.String;
                }
                else if (c == '$' && i + 1 < n && char.IsDigit(sql[i + 1]))
                {
                    i++; while (i < n && char.IsDigit(sql[i])) i++;
                    kind = Kind.Word;
                }
                else if (char.IsLetter(c) || c == '_')
                {
                    while (i < n && (char.IsLetterOrDigit(sql[i]) || sql[i] == '_' || sql[i] == '$')) i++;
                    kind = Kind.Word;
                }
                else if (char.IsDigit(c) || (c == '.' && i + 1 < n && char.IsDigit(sql[i + 1])))
                {
                    while (i < n && (char.IsLetterOrDigit(sql[i]) || sql[i] == '.' || ((sql[i] == '+' || sql[i] == '-') && (sql[i - 1] == 'e' || sql[i - 1] == 'E')))) i++;
                    kind = Kind.Number;
                }
                else if (c == ':' && i + 1 < n && sql[i + 1] == ':') { i += 2; kind = Kind.Punct; }
                else if ("(),;.[]".IndexOf(c) >= 0) { i++; kind = Kind.Punct; }
                else
                {
                    while (i < n && "+-*/<>=~!@#%^&|`?:".IndexOf(sql[i]) >= 0)
                    {
                        if (sql[i] == '-' && i + 1 < n && sql[i + 1] == '-') break;
                        if (sql[i] == '/' && i + 1 < n && sql[i + 1] == '*') break;
                        if (sql[i] == ':' && i + 1 < n && sql[i + 1] == ':') break;
                        i++;
                    }
                    if (i == start) i++;
                    kind = Kind.Op;
                }
                list.Add(new Token { Kind = kind, Text = sql.Substring(start, i - start), NewlineBefore = newline });
                newline = false;
            }
            return list;
        }
    }
}
