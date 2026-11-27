using System;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;

namespace SsmsPgExplorer.UI
{
    /// <summary>
    /// Applies the SSMS/VS color theme (light, dark, blue) to hosted WinForms controls and re-applies on theme change.
    /// </summary>
    internal static class Theme
    {
        public static Color Background => Get(EnvironmentColors.ToolWindowBackgroundColorKey, SystemColors.Window);
        public static Color Text => Get(EnvironmentColors.ToolWindowTextColorKey, SystemColors.WindowText);
        public static Color Border => Get(EnvironmentColors.ToolWindowBorderColorKey, SystemColors.ControlDark);
        public static Color Highlight => Get(EnvironmentColors.SystemHighlightColorKey, SystemColors.Highlight);
        public static Color HighlightText => Get(EnvironmentColors.SystemHighlightTextColorKey, SystemColors.HighlightText);
        public static Color InputBackground => Get(EnvironmentColors.ComboBoxBackgroundColorKey, SystemColors.Window);
        public static Color InputText => Get(EnvironmentColors.ComboBoxTextColorKey, SystemColors.WindowText);
        public static Color BarBackground => Get(EnvironmentColors.CommandBarGradientBeginColorKey, SystemColors.Control);
        public static Color BarText => Get(EnvironmentColors.CommandBarTextActiveColorKey, SystemColors.ControlText);
        public static Color BarHover => Get(EnvironmentColors.CommandBarMouseOverBackgroundBeginColorKey, SystemColors.ControlLight);
        public static Color GrayText => Get(EnvironmentColors.SystemGrayTextColorKey, SystemColors.GrayText);

        public static bool IsDark => Background.GetBrightness() < 0.4f;

        /// <summary>Theme a control tree now and whenever the user switches theme.</summary>
        public static void Attach(Control root)
        {
            Apply(root);
            void OnChanged(ThemeChangedEventArgs e) { if (!root.IsDisposed) root.BeginInvoke((Action)(() => Apply(root))); }
            VSColorTheme.ThemeChanged += OnChanged;
            root.Disposed += (s, e) => VSColorTheme.ThemeChanged -= OnChanged;
        }

        /// <summary>Set a control's Tag to this to keep its own colors (e.g. the green Execute button).</summary>
        public const string SkipTag = "theme:skip";

        public static void Apply(Control c)
        {
            if (c.Tag as string == SkipTag) return;
            switch (c)
            {
                case ToolStrip strip:
                    strip.Renderer = Renderer.Instance;
                    strip.BackColor = BarBackground;
                    strip.ForeColor = BarText;
                    foreach (ToolStripItem item in strip.Items) ApplyItem(item);
                    break;
                case DataGridView grid:
                    grid.BackgroundColor = Background;
                    grid.GridColor = Border;
                    grid.EnableHeadersVisualStyles = false;
                    grid.DefaultCellStyle.BackColor = InputBackground;
                    grid.DefaultCellStyle.ForeColor = InputText;
                    grid.DefaultCellStyle.SelectionBackColor = Highlight;
                    grid.DefaultCellStyle.SelectionForeColor = HighlightText;
                    grid.ColumnHeadersDefaultCellStyle.BackColor = BarBackground;
                    grid.ColumnHeadersDefaultCellStyle.ForeColor = BarText;
                    grid.RowHeadersDefaultCellStyle.BackColor = BarBackground;
                    grid.RowHeadersDefaultCellStyle.ForeColor = BarText;
                    break;
                case TextBox _:
                case ComboBox _:
                case NumericUpDown _:
                case TreeView _:
                case ListBox _:
                    c.BackColor = InputBackground;
                    c.ForeColor = InputText;
                    if (c is TreeView tv) tv.LineColor = GrayText;
                    break;
                case Button b:
                    b.FlatStyle = FlatStyle.Flat;
                    b.FlatAppearance.BorderColor = Border;
                    b.BackColor = BarBackground;
                    b.ForeColor = BarText;
                    break;
                default:
                    // Keep explicit accent colors (e.g. Firebrick warnings) when they are not the default.
                    c.BackColor = Background;
                    if (c.ForeColor == SystemColors.ControlText || c.ForeColor == SystemColors.WindowText || c.ForeColor == Text || IsThemeText(c.ForeColor))
                        c.ForeColor = Text;
                    break;
            }
            foreach (Control child in c.Controls) Apply(child);
        }

        private static bool IsThemeText(Color color) => color.ToArgb() == Color.Black.ToArgb() || color.ToArgb() == Color.White.ToArgb();

        private static void ApplyItem(ToolStripItem item)
        {
            if (item is ToolStripControlHost host) { if (host.Control?.Tag as string == SkipTag) return; host.BackColor = InputBackground; host.ForeColor = InputText; }
            else item.ForeColor = BarText;
            if (item is ToolStripDropDownItem dd) ApplyMenu(dd.DropDown);
        }

        // Menus: lifted off the tool-window background with full-contrast text, so they don't read as a black hole in dark theme.
        public static Color MenuBackground => IsDark ? Blend(Background, Color.White, 0.14f) : Get(EnvironmentColors.CommandBarMenuBackgroundGradientBeginColorKey, SystemColors.Menu);
        public static Color MenuText => IsDark ? Color.FromArgb(241, 241, 241) : Get(EnvironmentColors.CommandBarTextActiveColorKey, SystemColors.MenuText);
        public static Color MenuHover => IsDark ? Blend(Background, Highlight, 0.55f) : Get(EnvironmentColors.CommandBarMenuItemMouseOverColorKey, SystemColors.MenuHighlight);

        public static void ApplyMenu(ToolStripDropDown menu)
        {
            menu.Renderer = Renderer.Instance;
            menu.BackColor = MenuBackground;
            menu.ForeColor = MenuText;
            foreach (ToolStripItem i in menu.Items) ApplyMenuItem(i);
        }

        private static void ApplyMenuItem(ToolStripItem item)
        {
            item.BackColor = MenuBackground;
            item.ForeColor = MenuText;
            if (item is ToolStripDropDownItem dd) ApplyMenu(dd.DropDown);
        }

        private static Color Blend(Color a, Color b, float t) =>
            Color.FromArgb(255, (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

        private static Color Get(ThemeResourceKey key, Color fallback)
        {
            try { return VSColorTheme.GetThemedColor(key); }
            catch { return fallback; }
        }

        /// <summary>Flat renderer that honours BackColor (the Professional renderer paints its own gradients).</summary>
        private sealed class Renderer : ToolStripRenderer
        {
            public static readonly Renderer Instance = new Renderer();

            protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
            {
                using (var b = new SolidBrush(e.ToolStrip.BackColor)) e.Graphics.FillRectangle(b, e.AffectedBounds);
            }

            protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
            {
                if (e.ToolStrip is ToolStripDropDown)
                    using (var p = new Pen(Border)) e.Graphics.DrawRectangle(p, 0, 0, e.AffectedBounds.Width - 1, e.AffectedBounds.Height - 1);
            }

            protected override void OnRenderButtonBackground(ToolStripItemRenderEventArgs e) => Hover(e);
            protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e) => Hover(e);
            protected override void OnRenderDropDownButtonBackground(ToolStripItemRenderEventArgs e) => Hover(e);
            protected override void OnRenderOverflowButtonBackground(ToolStripItemRenderEventArgs e)
            {
                Hover(e);
                var r = e.Item.ContentRectangle;
                using (var b = new SolidBrush(e.Item.ForeColor))
                    e.Graphics.FillPolygon(b, new[] { new Point(r.Left + r.Width / 2 - 3, r.Bottom - 8), new Point(r.Left + r.Width / 2 + 3, r.Bottom - 8), new Point(r.Left + r.Width / 2, r.Bottom - 5) });
            }

            private static void Hover(ToolStripItemRenderEventArgs e)
            {
                var checkedButton = e.Item is ToolStripButton tb && tb.Checked;
                var inMenu = e.Item.Owner is ToolStripDropDown;
                if (!e.Item.Selected && !e.Item.Pressed && !checkedButton) return;
                if (inMenu && !e.Item.Enabled) return;
                using (var b = new SolidBrush(checkedButton && !e.Item.Selected ? Color.FromArgb(80, Highlight) : inMenu ? MenuHover : BarHover))
                    e.Graphics.FillRectangle(b, inMenu ? new Rectangle(2, 0, e.Item.Width - 4, e.Item.Height) : new Rectangle(Point.Empty, e.Item.Size));
            }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                e.TextColor = e.Item.Enabled ? e.Item.ForeColor : (IsDark ? Color.FromArgb(140, 140, 140) : GrayText);
                base.OnRenderItemText(e);
            }

            protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
            {
                e.ArrowColor = e.Item.ForeColor;
                base.OnRenderArrow(e);
            }

            protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
            {
                using (var p = new Pen(Border))
                {
                    if (e.Vertical) e.Graphics.DrawLine(p, e.Item.Width / 2, 3, e.Item.Width / 2, e.Item.Height - 3);
                    else e.Graphics.DrawLine(p, 4, e.Item.Height / 2, e.Item.Width - 4, e.Item.Height / 2);
                }
            }
        }
    }
}
