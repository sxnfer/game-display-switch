// The glass that the island is made of (the flyout is a solid Windows 11 flyout since: Palette.cs). Analogy: a frosted shower window. The colours and shapes
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
            public void Present(IntPtr hwnd, Point at, double alpha) { Present(hwnd, at, Size, alpha); }

            /// <summary>Shows only its top-left part, this size: a surface made for the tallest the window gets serves every height below it.</summary>
            public void Present(IntPtr hwnd, Point at, Size size, double alpha)
            {
                G.Flush(FlushIntention.Sync);
                var blend = new Native.BlendFunction { SourceConstantAlpha = (byte)Math.Round(255 * Math.Max(0, Math.Min(1, alpha))), AlphaFormat = 1 };   // AC_SRC_ALPHA
                Point src = Point.Empty;
                Size sz = new Size(Math.Min(size.Width, Size.Width), Math.Min(size.Height, Size.Height));
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
    }
}
