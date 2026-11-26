// The flyout's colours: Windows 11's own flyout look. Solid, in the Windows mode (light or dark, the mode the taskbar
// and the system flyouts use, not the apps' mode), with the user's accent colour for what is on, and Windows'
// high-contrast colours when high contrast is on. The values are WinUI's theme resources (the names in the comments).
//
// Amber keeps one meaning: HDR. It marks HDR only (the status card's marker), never something that is merely on.
//
// GDI text (TextRenderer, for ClearType) has no alpha: a translucent text colour comes out at full strength. So text is
// drawn in the opaque colour the translucent one makes over what it sits on (Over), worked out once per surface.
//
// The island (Island.cs) is drawn in the same colours: the flyout's surface and stroke, its text, the accent for what
// is on, amber for HDR.

using System;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Win32;

namespace HdrSwitch
{
    sealed class Palette
    {
        public enum Kind { Dark, Light, HighContrast }

        public Kind Mode;
        public bool HighContrast { get { return Mode == Kind.HighContrast; } }

        public Color Surface;        // the flyout itself (the solid fallback of its acrylic)
        public Color Stroke;         // SurfaceStrokeColorFlyout: its 1 px border where Windows doesn't draw one
        public Color Text1, Text2, Text3, TextDisabled;   // TextFillColorPrimary … Disabled
        public Color Card, CardStroke;                    // CardBackgroundFillColorDefault, CardStrokeColorDefault
        public Color ControlFill, ControlFill2, ControlFill3;   // ControlFillColor Default, Secondary (hover), Tertiary (pressed)
        public Color ControlStroke, ControlStrong;        // ControlStrokeColorDefault, ControlStrongStrokeColorDefault
        public Color SubtleHover, SubtlePress;            // SubtleFillColor Secondary, Tertiary
        public Color Divider;                             // DividerStrokeColorDefault
        public Color FocusOuter, FocusInner;              // FocusStrokeColor Outer, Inner
        public Color Success, Caution, Critical;          // SystemFillColor …
        public Color OnAccent;                            // TextOnAccentFillColorPrimary
        public Color Accent, AccentText;                  // AccentFillColorDefault, AccentTextFillColorPrimary
        public Color HdrDot, HdrText, HdrSoft;            // the HDR marker: dot, text, and the fill behind the text
        public Color HoverStroke;                         // high contrast only: what hover draws instead of a fill
        public float StrokeWidth;                         // 1, or 2 in high contrast

        // Opaque, for text: a card on the surface, and the footer bar on it.
        public Color CardSolid, FootSolid;

        public Color AccentHover { get { return Theme.Fade(Accent, 0.9); } }
        public Color AccentPress { get { return Theme.Fade(Accent, 0.8); } }

        /// <summary>A text token as an opaque colour over this (opaque) background.</summary>
        public Color Ink(Color token, Color under) { return Over(token, under); }

        // ---- the current one ----

        static Palette current;   // until the first Load: Windows' default, light

        /// <summary>The palette in use. Load reads it afresh from Windows.</summary>
        public static Palette Current
        {
            get { return current ?? (current = For(Kind.Light, DefaultAccent)); }
            set { current = value; }
        }

        /// <summary>Reads the Windows mode, the accent colour and high contrast. Never throws; what can't be read is Windows' default.</summary>
        public static void Load()
        {
            bool hc = false;
            object light = null;
            byte[] accent = null;
            try { hc = SystemInformation.HighContrast; } catch { }
            try { light = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", null); } catch { }
            try { accent = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Accent", "AccentPalette", null) as byte[]; } catch { }
            current = For(ModeFor(hc, light), ParseAccent(accent));
        }

        /// <summary>
        /// The render harnesses' made-up Windows (--render-flyout, --render-island): "dark", "light" or "hc" (a contrast
        /// theme like Windows' "Night sky"), with the default accent.
        /// </summary>
        internal static Palette Sample(string theme)
        {
            return theme == "hc"
                ? HighContrastFrom(Color.Black, Color.White, Color.FromArgb(0x1A, 0xEB, 0xFF), Color.Black, Color.FromArgb(0x3F, 0xF2, 0x3F), Color.Black, Color.White)
                : For(theme == "light" ? Kind.Light : Kind.Dark, DefaultAccent);
        }

        // ---- the pure parts (tested) ----

        /// <summary>
        /// Windows 11's default blue, as AccentPalette holds it: Light3, Light2, Light1, Accent, Dark1, Dark2, Dark3.
        /// </summary>
        public static readonly Color[] DefaultAccent =
        {
            Color.FromArgb(0x99, 0xEB, 0xFF), Color.FromArgb(0x4C, 0xC2, 0xFF), Color.FromArgb(0x00, 0x91, 0xF8), Color.FromArgb(0x00, 0x78, 0xD4),
            Color.FromArgb(0x00, 0x5F, 0xB8), Color.FromArgb(0x00, 0x42, 0x75), Color.FromArgb(0x00, 0x26, 0x42)
        };

        /// <summary>
        /// The 7 shades in AccentPalette (32 bytes: 8 entries of R, G, B, A; the last one unused), opaque. Missing or
        /// short data: the default blue.
        /// </summary>
        public static Color[] ParseAccent(byte[] data)
        {
            if (data == null || data.Length < 28) return (Color[])DefaultAccent.Clone();
            var c = new Color[7];
            for (int i = 0; i < 7; i++) c[i] = Color.FromArgb(data[4 * i], data[4 * i + 1], data[4 * i + 2]);
            return c;
        }

        /// <summary>High contrast wins; otherwise SystemUsesLightTheme (a DWORD, 1 light, 0 dark). Missing: light, Windows' default.</summary>
        public static Kind ModeFor(bool highContrast, object systemUsesLightTheme)
        {
            if (highContrast) return Kind.HighContrast;
            if (systemUsesLightTheme is int) return (int)systemUsesLightTheme != 0 ? Kind.Light : Kind.Dark;
            return Kind.Light;
        }

        /// <summary>A translucent colour over an opaque one: the opaque colour that shows.</summary>
        public static Color Over(Color top, Color under)
        {
            double a = top.A / 255.0;
            return Color.FromArgb(255,
                (int)Math.Round(top.R * a + under.R * (1 - a)),
                (int)Math.Round(top.G * a + under.G * (1 - a)),
                (int)Math.Round(top.B * a + under.B * (1 - a)));
        }

        static Color H(uint argb) { return Color.FromArgb(unchecked((int)argb)); }

        /// <summary>The tokens for a mode. accent: the 7 shades (ParseAccent); high contrast takes Windows' system colours instead.</summary>
        public static Palette For(Kind mode, Color[] accent)
        {
            if (accent == null || accent.Length < 7) accent = DefaultAccent;
            var p = new Palette { Mode = mode, StrokeWidth = 1, HoverStroke = Color.Empty };
            if (mode == Kind.Dark)
            {
                p.Surface = H(0xFF2C2C2C); p.Stroke = H(0x33000000);
                p.Text1 = H(0xFFFFFFFF); p.Text2 = H(0xC5FFFFFF); p.Text3 = H(0x87FFFFFF); p.TextDisabled = H(0x5DFFFFFF);
                p.Card = H(0x0DFFFFFF); p.CardStroke = H(0x19000000);
                p.ControlFill = H(0x0FFFFFFF); p.ControlFill2 = H(0x15FFFFFF); p.ControlFill3 = H(0x08FFFFFF);
                p.ControlStroke = H(0x12FFFFFF); p.ControlStrong = H(0x8BFFFFFF);
                p.SubtleHover = H(0x0FFFFFFF); p.SubtlePress = H(0x0AFFFFFF);
                p.Divider = H(0x15FFFFFF);
                p.FocusOuter = H(0xFFFFFFFF); p.FocusInner = H(0xB3000000);
                p.Success = H(0xFF6CCB5F); p.Caution = H(0xFFFCE100); p.Critical = H(0xFFFF99A4);
                p.OnAccent = H(0xFF000000);
                p.Accent = accent[1]; p.AccentText = accent[0];   // Light2, Light3
                p.HdrDot = H(0xFFF0A020); p.HdrText = H(0xFFFFC35C); p.HdrSoft = H(0x24F0A020);
            }
            else if (mode == Kind.Light)
            {
                p.Surface = H(0xFFF9F9F9); p.Stroke = H(0x0F000000);
                p.Text1 = H(0xE4000000); p.Text2 = H(0x9E000000); p.Text3 = H(0x72000000); p.TextDisabled = H(0x5C000000);
                p.Card = H(0xB3FFFFFF); p.CardStroke = H(0x0F000000);
                p.ControlFill = H(0xB3FFFFFF); p.ControlFill2 = H(0x80F9F9F9); p.ControlFill3 = H(0x4DF9F9F9);
                p.ControlStroke = H(0x0F000000); p.ControlStrong = H(0x72000000);
                p.SubtleHover = H(0x09000000); p.SubtlePress = H(0x06000000);
                p.Divider = H(0x0F000000);
                p.FocusOuter = H(0xE4000000); p.FocusInner = H(0xB3FFFFFF);
                p.Success = H(0xFF0F7B0F); p.Caution = H(0xFF9D5D00); p.Critical = H(0xFFC42B1C);
                p.OnAccent = H(0xFFFFFFFF);
                p.Accent = accent[4]; p.AccentText = accent[5];   // Dark1, Dark2
                p.HdrDot = H(0xFFF0A020); p.HdrText = H(0xFF9D5D00); p.HdrSoft = H(0x2EF0A020);
            }
            else
                return HighContrastFrom(SystemColors.Window, SystemColors.WindowText, SystemColors.Highlight, SystemColors.HighlightText,
                    SystemColors.GrayText, SystemColors.ButtonFace, SystemColors.ControlText);
            p.CardSolid = Over(p.Card, p.Surface);
            p.FootSolid = Over(p.ControlFill2, p.Surface);
            return p;
        }

        /// <summary>
        /// High contrast: Windows' system colours only (these, which For takes from SystemColors), 2 px borders, no
        /// accent and no amber: the highlight colour marks what is on, words mark HDR.
        /// </summary>
        public static Palette HighContrastFrom(Color win, Color text, Color hi, Color hiText, Color gray, Color face, Color faceText)
        {
            var p = new Palette { Mode = Kind.HighContrast, StrokeWidth = 2 };
            p.Surface = win; p.Stroke = text;
            p.Text1 = text; p.Text2 = text; p.Text3 = text; p.TextDisabled = gray;
            p.Card = win; p.CardStroke = text;
            p.ControlFill = face; p.ControlFill2 = face; p.ControlFill3 = face;
            p.ControlStroke = faceText; p.ControlStrong = text;
            p.SubtleHover = Color.Transparent; p.SubtlePress = Color.Transparent; p.HoverStroke = hi;
            p.Divider = text;
            p.FocusOuter = text; p.FocusInner = win;
            p.Success = text; p.Caution = text; p.Critical = text;
            p.OnAccent = hiText; p.Accent = hi; p.AccentText = hi;
            p.HdrDot = text; p.HdrText = text; p.HdrSoft = Color.Transparent;   // HDR says so in words ("HDR ON")
            p.CardSolid = Over(p.Card, p.Surface);
            p.FootSolid = Over(p.ControlFill2, p.Surface);
            return p;
        }
    }
}
