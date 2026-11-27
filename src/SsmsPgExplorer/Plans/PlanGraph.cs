using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using SsmsPgExplorer.UI;

namespace SsmsPgExplorer.Plans
{
    /// <summary>
    /// SSMS-style graphical plan: root on the left, inputs to the right, arrows pointing toward the consumer
    /// with thickness by row count. Click selects (Properties), hover shows a tooltip, Ctrl+wheel zooms.
    /// </summary>
    internal sealed class PlanGraph : ScrollableControl
    {
        private const int W = 200, H = 92, HGap = 64, VGap = 20, Margin0 = 16;

        private StatementPlan _plan;
        private readonly Dictionary<PlanNode, RectangleF> _layout = new Dictionary<PlanNode, RectangleF>();
        private float _zoom = 1f;
        private readonly float _dpi;
        private PlanNode _selected, _hover;
        private readonly ToolTip _tip = new ToolTip { AutoPopDelay = 30000, InitialDelay = 400, ReshowDelay = 100, UseFading = false };

        public event EventHandler<PlanNode> NodeSelected;

        public PlanGraph()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            AutoScroll = true;
            _dpi = DeviceDpi / 96f;
            TabStop = true;
        }

        public StatementPlan Plan
        {
            get => _plan;
            set { _plan = value; _selected = value?.Root; Relayout(); NodeSelected?.Invoke(this, _selected); }
        }

        public float Zoom
        {
            get => _zoom;
            set { _zoom = Math.Max(0.3f, Math.Min(2.5f, value)); Relayout(); }
        }

        public void ZoomToFit()
        {
            if (_plan?.Root == null) return;
            _zoom = 1f;
            Relayout();
            var size = AutoScrollMinSize;
            if (size.Width == 0 || size.Height == 0) return;
            var f = Math.Min((ClientSize.Width - 4) / (float)size.Width, (ClientSize.Height - 4) / (float)size.Height);
            Zoom = Math.Min(1f, f);
        }

        private float S => _zoom * _dpi;

        private void Relayout()
        {
            _layout.Clear();
            if (_plan?.Root == null) { AutoScrollMinSize = Size.Empty; Invalidate(); return; }
            var row = 0;
            Place(_plan.Root, 0, ref row);
            var maxX = _layout.Values.Max(r => r.Right) + Margin0;
            var maxY = _layout.Values.Max(r => r.Bottom) + Margin0;
            AutoScrollMinSize = new Size((int)(maxX * S), (int)(maxY * S));
            Invalidate();
        }

        // Parent aligned with its first input (as SSMS draws it); each leaf takes the next row.
        private float Place(PlanNode n, int depth, ref int row)
        {
            float y;
            if (n.Children.Count == 0) { y = Margin0 + row * (H + VGap); row++; }
            else
            {
                y = float.MaxValue;
                foreach (var c in n.Children) y = Math.Min(y, Place(c, depth + 1, ref row));
            }
            _layout[n] = new RectangleF(Margin0 + depth * (W + HGap), y, W, H);
            return y;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Background);
            if (_plan?.Root == null)
            {
                TextRenderer.DrawText(g, "No plan.", Font, new Point(12, 12), Theme.GrayText);
                return;
            }
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.TranslateTransform(AutoScrollPosition.X, AutoScrollPosition.Y);
            g.ScaleTransform(S, S);

            foreach (var n in _layout.Keys) foreach (var c in n.Children) DrawArrow(g, c, n);
            foreach (var n in _layout.Keys) DrawNode(g, n);
        }

        private double RowsOut(PlanNode n) =>
            n.ActualRows.HasValue ? n.ActualRows.Value * Math.Max(1, n.ActualLoops ?? 1) : n.PlanRows;

        private void DrawArrow(Graphics g, PlanNode child, PlanNode parent)
        {
            var c = _layout[child];
            var p = _layout[parent];
            var rows = RowsOut(child);
            var width = (float)Math.Max(1.2, Math.Min(12, 1 + Math.Log10(rows + 1) * 1.7));
            var y1 = c.Top + 26;
            // Spread multiple inputs along the parent's right edge so their arrows don't overlap.
            var count = Math.Max(1, parent.Children.Count);
            var y2 = count == 1 ? p.Top + 26 : p.Top + 18 + (H - 36) * parent.Children.IndexOf(child) / (float)(count - 1);
            var x1 = c.Left;
            var x2 = p.Right;
            var mid = x2 + (x1 - x2) / 2;
            var color = child.NeverExecuted ? Theme.GrayText : Blend(Theme.Text, Theme.Background, 0.45f);
            using (var pen = new Pen(color, width) { LineJoin = LineJoin.Round })
            {
                g.DrawLines(pen, new[] { new PointF(x1, y1), new PointF(mid, y1), new PointF(mid, y2), new PointF(x2 + 9, y2) });
            }
            using (var b = new SolidBrush(color))
                g.FillPolygon(b, new[] { new PointF(x2 + 1, y2), new PointF(x2 + 11, y2 - 5 - width / 2), new PointF(x2 + 11, y2 + 5 + width / 2) });

            // Row count over the arrow (actual if we have it).
            var label = FormatRows(rows);
            using (var f = Px(10f))
            using (var b = new SolidBrush(Theme.GrayText))
                g.DrawString(label, f, b, mid + 3, y1 - 14);
        }

        private void DrawNode(Graphics g, PlanNode n)
        {
            var r = _layout[n];
            var selected = ReferenceEquals(n, _selected);
            var warn = n.Warnings.Count > 0 && !n.NeverExecuted;
            using (var path = Rounded(r, 6))
            {
                using (var bg = new SolidBrush(ReferenceEquals(n, _hover) ? Blend(Theme.InputBackground, Theme.Highlight, 0.12f) : Theme.InputBackground))
                    g.FillPath(bg, path);
                using (var pen = new Pen(selected ? Theme.Highlight : warn ? Color.DarkOrange : Theme.Border, selected ? 2.5f : warn ? 1.8f : 1f))
                    g.DrawPath(pen, path);
            }

            // Operator glyph
            var glyph = new RectangleF(r.Left + 8, r.Top + 8, 36, 36);
            using (var gp = Rounded(glyph, 5))
            using (var gb = new SolidBrush(CategoryColor(n)))
                g.FillPath(gb, gp);
            using (var f = Px(10f, FontStyle.Bold))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString(Abbrev(n), f, Brushes.White, glyph, sf);
            if (warn) DrawWarning(g, new PointF(glyph.Right - 4, glyph.Bottom - 4));

            var textX = glyph.Right + 6;
            var textW = r.Right - textX - 6;
            using (var bold = Px(11.5f, FontStyle.Bold))
            using (var normal = Px(10.5f))
            using (var small = Px(10f))
            using (var text = new SolidBrush(n.NeverExecuted ? Theme.GrayText : Theme.Text))
            using (var gray = new SolidBrush(Theme.GrayText))
            using (var hot = new SolidBrush(Color.FromArgb(220, 90, 30)))
            using (var sfTrim = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
            {
                g.DrawString(n.Title, bold, text, new RectangleF(textX, r.Top + 7, textW, 16), sfTrim);
                g.DrawString(n.Subtitle, normal, gray, new RectangleF(textX, r.Top + 24, textW, 15), sfTrim);

                var costBrush = n.CostPercent >= 25 ? hot : text;
                g.DrawString($"Cost: {n.CostPercent:0} %", normal, costBrush, new RectangleF(r.Left + 8, r.Top + 48, W - 16, 15), sfTrim);

                if (n.NeverExecuted)
                    g.DrawString("never executed", small, gray, new RectangleF(r.Left + 8, r.Top + 64, W - 16, 14), sfTrim);
                else if (n.ExclusiveMs.HasValue)
                {
                    var tBrush = (n.TimePercent ?? 0) >= 25 ? hot : text;
                    g.DrawString($"{FormatMs(n.ExclusiveMs.Value)} ({n.TimePercent ?? 0:0} %)", small, tBrush, new RectangleF(r.Left + 8, r.Top + 63, W - 16, 14), sfTrim);
                    var est = n.PlanRows * Math.Max(1, n.ActualLoops ?? 1);
                    var act = RowsOut(n);
                    var pct = est > 0 ? act / est * 100 : 0;
                    var rBrush = n.RowsFactor >= 10 ? hot : text;
                    g.DrawString($"{FormatRows(act)} of {FormatRows(est)} ({pct:0} %)", small, rBrush, new RectangleF(r.Left + 8, r.Top + 76, W - 16, 14), sfTrim);
                }
                else
                {
                    g.DrawString($"Est. rows: {FormatRows(n.PlanRows)}   width: {n.PlanWidth:0}", small, gray, new RectangleF(r.Left + 8, r.Top + 64, W - 16, 14), sfTrim);
                }
            }
        }

        /// <summary>Font sized in pixels at 100% zoom/DPI; the Graphics ScaleTransform does all scaling.</summary>
        private Font Px(float px, FontStyle style = FontStyle.Regular) => new Font(Font.FontFamily, px, style, GraphicsUnit.Pixel);

        private void DrawWarning(Graphics g, PointF at)
        {
            var tri = new[] { new PointF(at.X, at.Y - 13), new PointF(at.X + 8, at.Y + 1), new PointF(at.X - 8, at.Y + 1) };
            g.FillPolygon(Brushes.Gold, tri);
            g.DrawPolygon(Pens.DarkGoldenrod, tri);
            using (var f = Px(9.5f, FontStyle.Bold))
                g.DrawString("!", f, Brushes.Black, at.X - 3, at.Y - 11);
        }

        private static GraphicsPath Rounded(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            var d = radius * 2;
            p.AddArc(r.Left, r.Top, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        internal static string Abbrev(PlanNode n)
        {
            switch (n.NodeType)
            {
                case "Seq Scan": return "SEQ";
                case "Index Scan": return "IDX";
                case "Index Only Scan": return "IOS";
                case "Bitmap Heap Scan": return "BHS";
                case "Bitmap Index Scan": return "BIS";
                case "BitmapAnd": return "AND";
                case "BitmapOr": return "OR";
                case "Nested Loop": return "NL";
                case "Hash Join": return "HJ";
                case "Merge Join": return "MJ";
                case "Hash": return "HASH";
                case "Sort": return "SORT";
                case "Incremental Sort": return "ISRT";
                case "Aggregate": return "AGG";
                case "GroupAggregate": return "AGG";
                case "HashAggregate": return "AGG";
                case "WindowAgg": return "WIN";
                case "Limit": return "LIM";
                case "Gather": return "GTH";
                case "Gather Merge": return "GTM";
                case "Materialize": return "MAT";
                case "Memoize": return "MEMO";
                case "CTE Scan": return "CTE";
                case "Subquery Scan": return "SUB";
                case "Function Scan": return "FN";
                case "Values Scan": return "VAL";
                case "Result": return "RES";
                case "Append": return "APP";
                case "Merge Append": return "MAPP";
                case "Unique": return "UNQ";
                case "SetOp": return "SET";
                case "ModifyTable": return "MOD";
                case "LockRows": return "LOCK";
                case "Tid Scan": return "TID";
                case "Foreign Scan": return "FDW";
                default: return new string(n.NodeType.Where(char.IsUpper).Take(4).ToArray());
            }
        }

        private static Color CategoryColor(PlanNode n)
        {
            var t = n.NodeType;
            if (t == "Seq Scan") return Color.FromArgb(196, 90, 50);
            if (t.Contains("Index") || t.StartsWith("Bitmap")) return Color.FromArgb(46, 125, 50);
            if (t.Contains("Join") || t == "Nested Loop") return Color.FromArgb(40, 100, 180);
            if (t.Contains("Sort") || t == "Hash" || t == "Materialize" || t == "Memoize") return Color.FromArgb(140, 90, 170);
            if (t.Contains("Aggregate") || t == "WindowAgg" || t == "Unique" || t == "SetOp") return Color.FromArgb(180, 130, 20);
            if (t.StartsWith("Gather")) return Color.FromArgb(0, 140, 150);
            if (t == "ModifyTable" || t == "LockRows") return Color.FromArgb(170, 40, 60);
            return Color.FromArgb(100, 110, 125);
        }

        internal static string FormatRows(double rows)
        {
            if (rows >= 1e9) return (rows / 1e9).ToString("0.#", CultureInfo.InvariantCulture) + "B";
            if (rows >= 1e6) return (rows / 1e6).ToString("0.#", CultureInfo.InvariantCulture) + "M";
            if (rows >= 1e4) return (rows / 1e3).ToString("0.#", CultureInfo.InvariantCulture) + "K";
            return rows.ToString("N0", CultureInfo.InvariantCulture);
        }

        internal static string FormatMs(double ms) =>
            ms >= 60000 ? $"{ms / 60000:0.0} min" : ms >= 1000 ? $"{ms / 1000:0.00} s" : $"{ms:0.###} ms";

        private static Color Blend(Color a, Color b, float t) =>
            Color.FromArgb(255, (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

        // ---------- input ----------

        private PlanNode HitTest(Point client)
        {
            var x = (client.X - AutoScrollPosition.X) / S;
            var y = (client.Y - AutoScrollPosition.Y) / S;
            return _layout.FirstOrDefault(kv => kv.Value.Contains(x, y)).Key;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            var hit = HitTest(e.Location);
            if (hit == null) return;
            _selected = hit;
            Invalidate();
            NodeSelected?.Invoke(this, hit);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var hit = HitTest(e.Location);
            if (ReferenceEquals(hit, _hover)) return;
            _hover = hit;
            Invalidate();
            _tip.SetToolTip(this, hit == null ? null : TooltipFor(hit));
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hover = null;
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if ((ModifierKeys & Keys.Control) == Keys.Control) { Zoom *= e.Delta > 0 ? 1.1f : 1 / 1.1f; return; }
            base.OnMouseWheel(e);
        }

        private static string TooltipFor(PlanNode n)
        {
            var lines = new List<string> { n.Title };
            if (n.Subtitle.Length > 0) lines.Add(n.Subtitle);
            lines.Add("");
            lines.Add($"Estimated operator cost: {n.ExclusiveCost:N2} ({n.CostPercent:0.#} %)");
            lines.Add($"Estimated subtree cost:  {n.StartupCost:N2} .. {n.TotalCost:N2}");
            lines.Add($"Estimated rows: {n.PlanRows:N0}   width: {n.PlanWidth:N0} B");
            if (n.ActualTotalMs.HasValue && !n.NeverExecuted)
            {
                lines.Add($"Actual rows: {n.ActualRows:N0} x {n.ActualLoops:N0} loop(s)");
                lines.Add($"Actual time: {n.ActualStartupMs:N3} .. {n.ActualTotalMs:N3} ms per loop");
                if (n.ExclusiveMs.HasValue) lines.Add($"Operator time: {FormatMs(n.ExclusiveMs.Value)} ({n.TimePercent ?? 0:0.#} %)");
            }
            foreach (var key in new[] { "Index Cond", "Recheck Cond", "Hash Cond", "Merge Cond", "Join Filter", "Filter", "Rows Removed by Filter", "Sort Key", "Sort Method", "Group Key",
                                         "Shared Hit Blocks", "Shared Read Blocks", "Temp Written Blocks" })
            {
                var v = n.Get(key);
                if (!string.IsNullOrEmpty(v) && v != "0") lines.Add($"{key}: {(v.Length > 160 ? v.Substring(0, 157) + "..." : v)}");
            }
            if (n.Warnings.Count > 0) { lines.Add(""); lines.AddRange(n.Warnings.Select(w => "! " + w)); }
            return string.Join(Environment.NewLine, lines);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _tip.Dispose();
            base.Dispose(disposing);
        }
    }
}
