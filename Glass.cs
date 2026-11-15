// The glass that the island and the flyout are made of. Analogy: a frosted shower window. The colours and shapes
// behind it come through, the detail does not, and light catches its edge. Five layers, from the back to the front:
//
//  1. What is behind: the desktop or a game. The app never reads these pixels.
//  2. Blur: Windows makes it (the accent blur of SetWindowCompositionAttribute, on Windows 10 and 11), so the cost is
//     the compositor's. The blur fills the whole window rectangle, so each window's region is set to its shape.
//  3. Veil: one thin dark fill (Theme.Veil), just enough that white text reads on a bright desktop.
//  4. Glow: a soft white light from the top-left, so the panel looks thick instead of flat.
//  5. Rim: a 1 px edge, bright at the top-left and at the bottom-right and almost gone between. It gives the liquid look.
//
// The window's own alpha (a fade in or out) does not fade the blur, so the blur comes on half-way through a fade in
// and goes half-way through a fade out. With "Transparency effects" off in Windows there is no blur and the veil is solid.
//
// Text: GDI text (TextRenderer) writes no alpha, so on a transparent surface it would make holes. Here it is drawn
// white on black into a small bitmap, that bitmap becomes a coverage mask, and the mask is drawn in the text's colour.
// That keeps exactly the sizes TextRenderer measures, which the flyout's layout and Roll.cs use. Masks are kept, so a
// frame only copies them.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace HdrSwitch
{
    static class Glass
    {
        static bool transparency = true;

        /// <summary>False when "Transparency effects" is off in Windows: no blur, a solid veil.</summary>
        public static bool Transparency { get { return transparency; } }

        /// <summary>Reads the Windows setting again. Called when a window shows, not per frame.</summary>
        public static void Refresh()
        {
            try
            {
                object v = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", 1);
                transparency = !(v is int) || (int)v != 0;
            }
            catch { transparency = true; }
        }

        // ---- the window ----

        /// <summary>Turns the blur behind a window on or off.</summary>
        public static void SetBlur(IntPtr hwnd, bool on)
        {
            var accent = new Native.AccentPolicy { AccentState = on ? 3 : 0 };   // ACCENT_ENABLE_BLURBEHIND, ACCENT_DISABLED
            int size = Marshal.SizeOf(typeof(Native.AccentPolicy));
            IntPtr p = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(accent, p, false);
                var data = new Native.CompositionData { Attribute = 19, Data = p, Size = size };   // WCA_ACCENT_POLICY
                Native.SetWindowCompositionAttribute(hwnd, ref data);
            }
            catch (EntryPointNotFoundException) { }
            finally { Marshal.FreeHGlobal(p); }
        }

        /// <summary>Clips the window (and so the blur behind it) to this shape, in window coordinates.</summary>
        public static void SetShape(IntPtr hwnd, Graphics g, GraphicsPath shape)
        {
            using (var region = new Region(shape))
            {
                IntPtr h = region.GetHrgn(g);
                if (Native.SetWindowRgn(hwnd, h, true) == 0) Native.DeleteObject(h);   // on success Windows owns the region
            }
        }

        /// <summary>
        /// A top-down 32-bit premultiplied DIB that GDI+ draws into directly and UpdateLayeredWindow reads from: no copy
        /// per frame.
        /// </summary>
        public sealed class Surface : IDisposable
        {
            public readonly Size Size;
            public readonly Graphics G;
            readonly Bitmap bitmap;
            IntPtr dc, dib, old;

            public Surface(Size size)
            {
                Size = size;
                IntPtr bits;
                var bi = new Native.BitmapInfo { Size = 40, Width = size.Width, Height = -size.Height, Planes = 1, BitCount = 32 };
                dc = Native.CreateCompatibleDC(IntPtr.Zero);
                dib = Native.CreateDIBSection(dc, ref bi, 0, out bits, IntPtr.Zero, 0);
                old = Native.SelectObject(dc, dib);
                bitmap = new Bitmap(size.Width, size.Height, size.Width * 4, PixelFormat.Format32bppPArgb, bits);
                G = Graphics.FromImage(bitmap);
                G.SmoothingMode = SmoothingMode.AntiAlias;
                G.PixelOffsetMode = PixelOffsetMode.Half;
                G.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;   // ClearType has no meaning on a transparent surface
            }

            /// <summary>Shows what was drawn, at this screen point, faded by alpha (0 → 1).</summary>
            public void Present(IntPtr hwnd, Point at, double alpha)
            {
                G.Flush(FlushIntention.Sync);
                var blend = new Native.BlendFunction { SourceConstantAlpha = (byte)Math.Round(255 * Math.Max(0, Math.Min(1, alpha))), AlphaFormat = 1 };   // AC_SRC_ALPHA
                Point src = Point.Empty;
                Size sz = Size;
                Native.UpdateLayeredWindow(hwnd, IntPtr.Zero, ref at, ref sz, dc, ref src, 0, ref blend, 2);   // ULW_ALPHA
            }

            public void Dispose()
            {
                G.Dispose();
                bitmap.Dispose();
                if (dc != IntPtr.Zero)
                {
                    Native.SelectObject(dc, old);
                    Native.DeleteObject(dib);
                    Native.DeleteDC(dc);
                    dc = dib = old = IntPtr.Zero;
                }
            }
        }

        // ---- the material ----

        static Color W(int a) { return Color.FromArgb(Math.Max(0, Math.Min(255, a)), 255, 255, 255); }

        /// <summary>Layers 3 and 4: the veil and the glow. Draw the content on it, then the Rim.</summary>
        public static void Panel(Graphics g, RectangleF r, float radius)
        {
            if (r.Width < 2 || r.Height < 2) return;
            Theme.Fill(g, r, radius, transparency ? Theme.Veil : Theme.Solid);
            using (var p = Theme.Round(r, radius))
            using (var glow = new LinearGradientBrush(r, W(0), W(0), 70f, true))
            {
                glow.WrapMode = WrapMode.TileFlipX;
                glow.InterpolationColors = new ColorBlend { Colors = new[] { W(34), W(0), W(0) }, Positions = new[] { 0f, 0.5f, 1f } };
                g.FillPath(glow, p);
            }
        }

        /// <summary>Layer 5: the light on the edge.</summary>
        public static void Rim(Graphics g, RectangleF r, float radius)
        {
            if (r.Width < 2 || r.Height < 2) return;
            using (var b = new LinearGradientBrush(r, W(0), W(0), 60f, true))
            {
                b.WrapMode = WrapMode.TileFlipX;
                b.InterpolationColors = new ColorBlend
                {
                    Colors = new[] { W(191), W(36), W(0), W(20), W(115) },
                    Positions = new[] { 0f, 0.28f, 0.5f, 0.72f, 1f }
                };
                using (var pen = new Pen(b, 1f))
                using (var p = Theme.Round(RectangleF.Inflate(r, -0.5f, -0.5f), Math.Max(0, radius - 0.5f))) g.DrawPath(pen, p);
            }
        }

        /// <summary>A card on the glass: a little white, with light on its top edge.</summary>
        public static void Chip(Graphics g, RectangleF r, float radius, Color fill)
        {
            Theme.Fill(g, r, radius, fill);
            Edge(g, r, radius, 1);
        }

        /// <summary>The light on the top edge of something that sits on the glass. strength 0 → 1.</summary>
        public static void Edge(Graphics g, RectangleF r, float radius, double strength)
        {
            strength = Math.Max(0, Math.Min(1, strength));
            if (strength < 0.01 || r.Width < 2 || r.Height < 2) return;
            using (var b = new LinearGradientBrush(new RectangleF(r.X, r.Y - 1, r.Width, r.Height + 2), W((int)(60 * strength)), W((int)(8 * strength)), 90f))
            using (var pen = new Pen(b, 1f))
            using (var p = Theme.Round(RectangleF.Inflate(r, -0.5f, -0.5f), Math.Max(0, radius - 0.5f))) g.DrawPath(pen, p);
        }

        // ---- text ----

        const int Pad = 2;   // room round a mask for anti-aliasing that spills past the measured box
        static readonly Dictionary<string, Bitmap> masks = new Dictionary<string, Bitmap>();

        /// <summary>TextRenderer.DrawText in a box, with real alpha. Light text gets a faint shadow, so it reads on a bright backdrop.</summary>
        public static void Text(Graphics g, string s, Font f, Rectangle r, Color c, TextFormatFlags flags)
        {
            if (string.IsNullOrEmpty(s) || c.A == 0 || r.Width <= 0 || r.Height <= 0) return;
            Blit(g, Mask(s, f, r.Size, flags), r.X - Pad, r.Y - Pad, c);
        }

        /// <summary>TextRenderer.DrawText from a point, with real alpha.</summary>
        public static void TextAt(Graphics g, string s, Font f, Point at, Color c, TextFormatFlags flags)
        {
            if (string.IsNullOrEmpty(s) || c.A == 0) return;
            Blit(g, Mask(s, f, Size.Empty, flags), at.X - Pad, at.Y - Pad, c);
        }

        /// <summary>Lets the kept masks go: the flyout closed.</summary>
        public static void ClearText()
        {
            foreach (var m in masks.Values) m.Dispose();
            masks.Clear();
        }

        /// <summary>White where the text is, with its coverage as alpha. box empty: measured from a point.</summary>
        static Bitmap Mask(string s, Font f, Size box, TextFormatFlags flags)
        {
            string key = s + "\u0001" + f.Name + "\u0001" + f.Size + "\u0001" + (int)f.Style + "\u0001" + box.Width + "x" + box.Height + "\u0001" + (int)flags;
            Bitmap m;
            if (masks.TryGetValue(key, out m)) return m;
            if (masks.Count > 600) ClearText();
            bool atPoint = box.IsEmpty;
            Size sz = atPoint ? TextRenderer.MeasureText(s, f, Size.Empty, flags) : box;
            int w = Math.Max(1, sz.Width + 2 * Pad), h = Math.Max(1, sz.Height + 2 * Pad);
            var all = new Rectangle(0, 0, w, h);
            m = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var src = new Bitmap(w, h, PixelFormat.Format32bppRgb))
            {
                using (var sg = Graphics.FromImage(src))
                {
                    sg.Clear(Color.Black);
                    sg.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;   // asks TextRenderer for grey anti-aliasing
                    if (atPoint) TextRenderer.DrawText(sg, s, f, new Point(Pad, Pad), Color.White, Color.Black, flags);
                    else TextRenderer.DrawText(sg, s, f, new Rectangle(Pad, Pad, box.Width, box.Height), Color.White, Color.Black, flags);
                }
                var a = src.LockBits(all, ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
                var b = m.LockBits(all, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                int n = a.Stride * h;
                var px = new byte[n];
                Marshal.Copy(a.Scan0, px, 0, n);
                for (int i = 0; i < n; i += 4)
                {
                    int cov = (px[i] + px[i + 1] + px[i + 2]) / 3;   // grey text has equal channels; ClearType's coloured edges average out
                    px[i] = px[i + 1] = px[i + 2] = 255;
                    px[i + 3] = (byte)cov;
                }
                Marshal.Copy(px, 0, b.Scan0, n);
                src.UnlockBits(a);
                m.UnlockBits(b);
            }
            masks[key] = m;
            return m;
        }

        static void Blit(Graphics g, Bitmap mask, int x, int y, Color c)
        {
            var mode = g.InterpolationMode;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;   // 1:1, so an exact copy
            double lum = (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255;
            double shadow = 0.32 * Math.Max(0, Math.Min(1, (lum - 0.45) / 0.35));   // light text only: none on white glass
            if (shadow > 0.01) Tinted(g, mask, x, y + 1, Color.FromArgb((int)Math.Round(c.A * shadow), 0, 0, 0));
            Tinted(g, mask, x, y, c);
            g.InterpolationMode = mode;
        }

        static void Tinted(Graphics g, Bitmap mask, int x, int y, Color c)
        {
            var cm = new ColorMatrix(new[]
            {
                new[] { c.R / 255f, 0, 0, 0, 0 },
                new[] { 0, c.G / 255f, 0, 0, 0 },
                new[] { 0, 0, c.B / 255f, 0, 0 },
                new[] { 0, 0, 0, c.A / 255f, 0 },
                new[] { 0f, 0, 0, 0, 1 }
            });
            using (var ia = new ImageAttributes())
            {
                ia.SetColorMatrix(cm);
                g.DrawImage(mask, new Rectangle(x, y, mask.Width, mask.Height), 0, 0, mask.Width, mask.Height, GraphicsUnit.Pixel, ia);
            }
        }
    }
}
