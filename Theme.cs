// One look for the island, the flyout and the tray icon: colours, fonts, corner shapes and the shared controls.
// Liquid glass (Glass.cs) with white on it at a few strengths, and one colour with a meaning: amber is HDR (red only
// marks a recording app). The Windows accent colour is not used, so amber never has to compete with it.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;

namespace HdrSwitch
{
    static class Theme
    {
        // Everything sits on glass (Glass.cs), so the surfaces are white at a few small strengths: they take their
        // colour from what is behind. Text is white at three strengths; dark text (Ink) only on white glass.
        public static readonly Color Veil = Color.FromArgb(72, 18, 18, 24);      // the glass itself: just enough for white text on a bright desktop
        public static readonly Color Solid = Color.FromArgb(255, 30, 30, 34);    // the glass with "Transparency effects" off
        public static readonly Color None = Color.FromArgb(0, 255, 255, 255);    // no fill; mixes towards white
        public static readonly Color Chip = Color.FromArgb(18, 255, 255, 255);   // a card, an unlit tile
        public static readonly Color ChipHover = Color.FromArgb(33, 255, 255, 255);
        public static readonly Color Raised = Color.FromArgb(31, 255, 255, 255); // buttons
        public static readonly Color Hover = Color.FromArgb(51, 255, 255, 255);
        public static readonly Color Press = Color.FromArgb(20, 255, 255, 255);
        public static readonly Color Lit = Color.FromArgb(230, 255, 255, 255);   // white glass: a lit tile, the update button
        public static readonly Color LitHover = Color.FromArgb(242, 255, 255, 255);
        public static readonly Color LitPress = Color.FromArgb(205, 255, 255, 255);
        public static readonly Color Ink = Color.FromArgb(18, 18, 20);           // text on white glass
        public static readonly Color InkSoft = Color.FromArgb(160, 18, 18, 20);
        public static readonly Color Text1 = Color.White;
        public static readonly Color Text2 = Color.FromArgb(196, 255, 255, 255);
        public static readonly Color Text3 = Color.FromArgb(160, 255, 255, 255);
        public static readonly Color Amber = Color.FromArgb(255, 170, 0);    // HDR on
        public static readonly Color Grey = Color.FromArgb(142, 142, 147);   // SDR
        public static readonly Color TrackOff = Color.FromArgb(41, 255, 255, 255);
        public static readonly Color Rec = Color.FromArgb(255, 69, 58);      // a recording app

        public static readonly bool Win11 = Environment.OSVersion.Version.Build >= 22000;
        public static readonly string TextFamily, StrongFamily, DisplayFamily, IconFamily;

        static Theme()
        {
            HashSet<string> installed;
            using (var fc = new InstalledFontCollection()) installed = new HashSet<string>(fc.Families.Select(f => f.Name));
            TextFamily = installed.Contains("Segoe UI Variable Text") ? "Segoe UI Variable Text" : "Segoe UI";
            StrongFamily = installed.Contains("Segoe UI Variable Text Semibold") ? "Segoe UI Variable Text Semibold" : "Segoe UI Semibold";
            DisplayFamily = installed.Contains("Segoe UI Variable Display Semib") ? "Segoe UI Variable Display Semib" : "Segoe UI Semibold";
            IconFamily = installed.Contains("Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
        }

        /// <summary>The fonts at one DPI scale. A window makes its own set and replaces it when its monitor's scale changes.</summary>
        public sealed class Fonts : IDisposable
        {
            public readonly float K;
            public readonly Font Body, Strong, Caption, Label, Title, Glyph, GlyphSmall, IslandTitle, IslandSub, IslandText;

            public Fonts(float k)
            {
                K = k;
                Body = Px(TextFamily, 14);
                Strong = Px(StrongFamily, 14);
                Caption = Px(TextFamily, 12);
                Label = Px(StrongFamily, 12);        // inside a tile
                Title = Px(DisplayFamily, 20);
                Glyph = Px(IconFamily, 16);
                GlyphSmall = Px(IconFamily, 13);
                IslandTitle = Px(StrongFamily, 15);
                IslandSub = Px(TextFamily, 12.5f);
                IslandText = Px(StrongFamily, 13.5f);
            }

            Font Px(string family, float px) { return new Font(family, px * K, GraphicsUnit.Pixel); }

            public void Dispose()
            {
                foreach (var f in new[] { Body, Strong, Caption, Label, Title, Glyph, GlyphSmall, IslandTitle, IslandSub, IslandText }) f.Dispose();
            }
        }

        /// <summary>The DPI scale (1 = 100 %) of the monitor this screen point is on.</summary>
        public static float Scale(Point p)
        {
            try
            {
                uint x, y;
                IntPtr mon = Native.MonitorFromPoint(p, 2);   // MONITOR_DEFAULTTONEAREST
                if (Native.GetDpiForMonitor(mon, 0, out x, out y) == 0 && x > 0) return x / 96f;
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            using (var g = Graphics.FromHwnd(IntPtr.Zero)) return g.DpiX / 96f;
        }

        /// <summary>a → b as t goes 0 → 1, alpha included.</summary>
        public static Color Mix(Color a, Color b, double t)
        {
            t = Math.Max(0, Math.Min(1, t));
            return Color.FromArgb((int)Math.Round(a.A + (b.A - a.A) * t), (int)Math.Round(a.R + (b.R - a.R) * t),
                (int)Math.Round(a.G + (b.G - a.G) * t), (int)Math.Round(a.B + (b.B - a.B) * t));
        }

        public static Color Fade(Color c, double alpha)
        {
            return Color.FromArgb((int)Math.Round(c.A * Math.Max(0, Math.Min(1, alpha))), c);
        }

        /// <summary>
        /// A rounded rectangle. With the radius at half the height it is a true capsule (circular ends); below that the
        /// corners are "continuous" ones, as on Apple's hardware: one Bézier per corner that starts 1.28 radii out and
        /// passes through the same apex as the circle would, so the curve eases in instead of starting abruptly.
        /// </summary>
        public static GraphicsPath Round(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float max = Math.Min(r.Width, r.Height) / 2f;
            if (radius < 0.01f || max < 0.01f) { p.AddRectangle(r); return p; }
            if (radius >= max - 0.01f)
            {
                float d = max * 2;
                p.AddArc(r.X, r.Y, d, d, 180, 90);
                p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            }
            else
            {
                float e = Math.Min(radius * 1.28f, max), c = e * 0.2766f;
                float l = r.X, t = r.Y, rt = r.Right, b = r.Bottom;
                p.AddBezier(l, t + e, l, t + c, l + c, t, l + e, t);
                p.AddBezier(rt - e, t, rt - c, t, rt, t + c, rt, t + e);
                p.AddBezier(rt, b - e, rt, b - c, rt - c, b, rt - e, b);
                p.AddBezier(l + e, b, l + c, b, l, b - c, l, b - e);
            }
            p.CloseFigure();
            return p;
        }

        public static void Fill(Graphics g, RectangleF r, float radius, Color c)
        {
            if (c.A == 0 || r.Width <= 0 || r.Height <= 0) return;
            using (var p = Round(r, radius))
            using (var b = new SolidBrush(c)) g.FillPath(b, p);
        }

        /// <summary>A one-pixel line just inside the shape.</summary>
        public static void Stroke(Graphics g, RectangleF r, float radius, Color c)
        {
            if (c.A == 0 || r.Width <= 1 || r.Height <= 1) return;
            using (var p = Round(RectangleF.Inflate(r, -0.5f, -0.5f), Math.Max(0, radius - 0.5f)))
            using (var pen = new Pen(c)) g.DrawPath(pen, p);
        }

        public static void Dot(Graphics g, float cx, float cy, float d, Color c)
        {
            if (c.A == 0 || d <= 0) return;
            using (var b = new SolidBrush(c)) g.FillEllipse(b, cx - d / 2, cy - d / 2, d, d);
        }

        /// <summary>
        /// A switch: a pill-shaped track and a white pill for a thumb. on runs 0 → 1 as the thumb slides and the track
        /// crossfades to amber, with a faint amber glow round it; lift (hover, press) brightens it a little.
        /// </summary>
        public static void Switch(Graphics g, RectangleF r, double on, double lift)
        {
            double t = Math.Max(0, Math.Min(1, on));
            if (t > 0.01) Fill(g, RectangleF.Inflate(r, 2, 2), r.Height / 2 + 2, Fade(Amber, 0.2 * t));
            Fill(g, r, r.Height / 2, Mix(Mix(TrackOff, Amber, on), Color.White, lift * 0.12));
            float pad = r.Height * 3 / 26f, th = r.Height - 2 * pad, tw = th * 1.4f;
            float x = r.X + pad + (float)((r.Width - 2 * pad - tw) * Math.Max(-0.05, Math.Min(1.05, on)));
            var thumb = new RectangleF(x, r.Y + pad, tw, th);
            Fill(g, new RectangleF(x, thumb.Y + r.Height * 1.5f / 26f, tw, th), th / 2, Color.FromArgb(70, 0, 0, 0));   // a soft shadow under the thumb
            Fill(g, thumb, th / 2, Color.White);
        }

        /// <summary>The app's mark: an H on a soft square, amber for HDR and grey for SDR (on: 0 → 1).</summary>
        public static void Badge(Graphics g, RectangleF r, double on)
        {
            Fill(g, r, r.Width * 0.3f, Mix(Grey, Amber, on));
            using (var b = new SolidBrush(Mix(Color.White, Color.Black, on)))
            using (var p = H(r)) g.FillPath(b, p);
        }

        static readonly System.Diagnostics.Stopwatch spinClock = System.Diagnostics.Stopwatch.StartNew();

        /// <summary>
        /// Busy: a faint ring with a quarter arc that turns once every 0.85 s, centred on c, d across. alpha fades it in
        /// and out. It turns by the clock, so whoever draws it keeps redrawing while it shows.
        /// </summary>
        public static void Spinner(Graphics g, PointF c, float d, float stroke, Color fg, double alpha)
        {
            if (alpha <= 0.01 || d < 1) return;
            double turn = spinClock.Elapsed.TotalSeconds / 0.85 % 1;
            var r = new RectangleF(c.X - d / 2, c.Y - d / 2, d, d);
            using (var track = new Pen(Fade(fg, 0.2 * alpha), stroke))
            using (var arc = new Pen(Fade(fg, alpha), stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawEllipse(track, r);
                g.DrawArc(arc, r, (float)(turn * 360 - 90), 90);
            }
        }

        /// <summary>The H as three bars, on the 16-unit grid the tray icon uses.</summary>
        static GraphicsPath H(RectangleF r)
        {
            float u = r.Width / 16f;
            var p = new GraphicsPath(FillMode.Winding);
            p.AddRectangle(new RectangleF(r.X + 4 * u, r.Y + 3.5f * u, 2.2f * u, 9 * u));
            p.AddRectangle(new RectangleF(r.X + 9.8f * u, r.Y + 3.5f * u, 2.2f * u, 9 * u));
            p.AddRectangle(new RectangleF(r.X + 4 * u, r.Y + 6.9f * u, 8 * u, 2.2f * u));
            return p;
        }
    }
}
