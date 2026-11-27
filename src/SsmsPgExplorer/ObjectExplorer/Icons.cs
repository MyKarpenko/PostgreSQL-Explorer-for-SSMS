using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace SsmsPgExplorer.ObjectExplorer
{
    /// <summary>16x16 icons drawn at runtime, so the VSIX ships no image assets.</summary>
    internal static class Icons
    {
        private static readonly Dictionary<string, Icon> Cache = new Dictionary<string, Icon>();
        private static readonly Color PgBlue = Color.FromArgb(51, 103, 145);

        public static Icon Server => Get("server", g => { Cylinder(g, PgBlue); Badge(g, Color.FromArgb(46, 160, 67)); });
        public static Icon ServerError => Get("server-error", g => { Cylinder(g, PgBlue); Badge(g, Color.FromArgb(207, 34, 46)); });
        public static Icon Database => Get("database", g => Cylinder(g, Color.FromArgb(214, 160, 40)));
        public static Icon Folder => Get("folder", g =>
        {
            using (var b = new SolidBrush(Color.FromArgb(220, 182, 92)))
            {
                g.FillRectangle(b, 1, 4, 14, 10);
                g.FillRectangle(b, 1, 3, 6, 2);
            }
        });
        public static Icon Schema => Get("schema", g =>
        {
            using (var p = new Pen(Color.FromArgb(120, 90, 160), 2)) g.DrawRectangle(p, 2, 2, 11, 11);
        });
        public static Icon Table => Get("table", g => Grid(g, Color.FromArgb(70, 120, 180)));
        public static Icon View => Get("view", g => Grid(g, Color.FromArgb(70, 160, 140)));
        public static Icon Function => Get("function", g =>
        {
            using (var f = new Font("Consolas", 8f, FontStyle.Bold))
            using (var b = new SolidBrush(Color.FromArgb(160, 90, 40)))
                g.DrawString("fx", f, b, -1, 1);
        });
        public static Icon Column => Get("column", g =>
        {
            using (var b = new SolidBrush(Color.FromArgb(110, 110, 110))) g.FillRectangle(b, 5, 2, 6, 12);
        });
        public static Icon Key => Get("key", g =>
        {
            using (var p = new Pen(Color.FromArgb(200, 160, 0), 2))
            {
                g.DrawEllipse(p, 1, 4, 6, 6);
                g.DrawLine(p, 7, 7, 15, 7);
                g.DrawLine(p, 12, 7, 12, 11);
            }
        });
        public static Icon Index => Get("index", g =>
        {
            using (var p = new Pen(Color.FromArgb(90, 120, 90), 2))
                for (var y = 3; y <= 13; y += 5) g.DrawLine(p, 2, y, 14, y);
        });
        public static Icon Sequence => Get("sequence", g =>
        {
            using (var f = new Font("Consolas", 7f, FontStyle.Bold))
            using (var b = new SolidBrush(Color.FromArgb(90, 90, 160)))
                g.DrawString("123", f, b, -2, 2);
        });
        public static Icon Message => Get("message", g =>
        {
            using (var b = new SolidBrush(Color.Gray)) g.FillEllipse(b, 5, 5, 6, 6);
        });

        private static void Cylinder(Graphics g, Color c)
        {
            using (var b = new SolidBrush(c))
            using (var top = new SolidBrush(ControlPaint.Light(c)))
            {
                g.FillRectangle(b, 2, 4, 12, 9);
                g.FillEllipse(b, 2, 10, 12, 5);
                g.FillEllipse(top, 2, 1, 12, 5);
            }
        }

        private static void Badge(Graphics g, Color c)
        {
            using (var b = new SolidBrush(c)) g.FillEllipse(b, 9, 9, 7, 7);
        }

        private static void Grid(Graphics g, Color c)
        {
            using (var head = new SolidBrush(c))
            using (var p = new Pen(c))
            {
                g.FillRectangle(Brushes.White, 1, 2, 14, 12);
                g.FillRectangle(head, 1, 2, 14, 4);
                g.DrawRectangle(p, 1, 2, 14, 12);
                g.DrawLine(p, 1, 10, 15, 10);
                g.DrawLine(p, 8, 2, 8, 14);
            }
        }

        private static Icon Get(string key, Action<Graphics> draw)
        {
            lock (Cache)
            {
                if (Cache.TryGetValue(key, out var icon)) return icon;
                using (var bmp = new Bitmap(16, 16))
                {
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.Clear(Color.Transparent);
                        draw(g);
                    }
                    var h = bmp.GetHicon();
                    // Clone so the Icon owns its handle, then release the GDI one.
                    icon = (Icon)Icon.FromHandle(h).Clone();
                    DestroyIcon(h);
                }
                Cache[key] = icon;
                return icon;
            }
        }

        private static class ControlPaint
        {
            public static Color Light(Color c) => Color.FromArgb(c.A, Math.Min(255, c.R + 60), Math.Min(255, c.G + 60), Math.Min(255, c.B + 60));
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);
    }
}
