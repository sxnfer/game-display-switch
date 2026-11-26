// Checks for the flyout's colours (Palette.cs): the WinUI tokens for dark and light, the accent colour from the
// registry's bytes, which palette a Windows setting picks, and the opaque colours that GDI text is drawn in.

using System.Drawing;

namespace HdrSwitch.Tests
{
    static partial class Checks
    {
        static string Hex(Color c) { return c.A.ToString("X2") + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2"); }

        public static void PaletteDarkTokens()
        {
            var p = Palette.For(Palette.Kind.Dark, Palette.DefaultAccent);
            Run.Equal("FF2C2C2C", Hex(p.Surface), "surface");
            Run.Equal("33000000", Hex(p.Stroke), "flyout stroke");
            Run.Equal("FFFFFFFF", Hex(p.Text1), "text primary");
            Run.Equal("C5FFFFFF", Hex(p.Text2), "text secondary");
            Run.Equal("87FFFFFF", Hex(p.Text3), "text tertiary");
            Run.Equal("5DFFFFFF", Hex(p.TextDisabled), "text disabled");
            Run.Equal("0DFFFFFF", Hex(p.Card), "card");
            Run.Equal("19000000", Hex(p.CardStroke), "card stroke");
            Run.Equal("0FFFFFFF", Hex(p.ControlFill), "control fill");
            Run.Equal("15FFFFFF", Hex(p.ControlFill2), "control fill secondary");
            Run.Equal("12FFFFFF", Hex(p.ControlStroke), "control stroke");
            Run.Equal("8BFFFFFF", Hex(p.ControlStrong), "control strong stroke");
            Run.Equal("0FFFFFFF", Hex(p.SubtleHover), "subtle hover");
            Run.Equal("0AFFFFFF", Hex(p.SubtlePress), "subtle pressed");
            Run.Equal("15FFFFFF", Hex(p.Divider), "divider");
            Run.Equal("FFFFFFFF", Hex(p.FocusOuter), "focus outer");
            Run.Equal("B3000000", Hex(p.FocusInner), "focus inner");
            Run.Equal("FF6CCB5F", Hex(p.Success), "success");
            Run.Equal("FFFCE100", Hex(p.Caution), "caution");
            Run.Equal("FFFF99A4", Hex(p.Critical), "critical");
            Run.Equal("FF000000", Hex(p.OnAccent), "text on accent");
            Run.Equal("FF4CC2FF", Hex(p.Accent), "accent fill: Light2");
            Run.Equal("FF99EBFF", Hex(p.AccentText), "accent text: Light3");
            Run.Equal("E64CC2FF", Hex(p.AccentHover), "accent hover: 90 %");
            Run.Equal("CC4CC2FF", Hex(p.AccentPress), "accent pressed: 80 %");
            Run.Equal("FFF0A020", Hex(p.HdrDot), "HDR dot");
            Run.Equal("FFFFC35C", Hex(p.HdrText), "HDR text");
            Run.Equal("24F0A020", Hex(p.HdrSoft), "HDR fill");
            Run.Equal(1f, p.StrokeWidth, "1 px strokes");
        }

        public static void PaletteLightTokens()
        {
            var p = Palette.For(Palette.Kind.Light, Palette.DefaultAccent);
            Run.Equal("FFF9F9F9", Hex(p.Surface), "surface");
            Run.Equal("0F000000", Hex(p.Stroke), "flyout stroke");
            Run.Equal("E4000000", Hex(p.Text1), "text primary");
            Run.Equal("9E000000", Hex(p.Text2), "text secondary");
            Run.Equal("72000000", Hex(p.Text3), "text tertiary");
            Run.Equal("5C000000", Hex(p.TextDisabled), "text disabled");
            Run.Equal("B3FFFFFF", Hex(p.Card), "card");
            Run.Equal("0F000000", Hex(p.CardStroke), "card stroke");
            Run.Equal("B3FFFFFF", Hex(p.ControlFill), "control fill");
            Run.Equal("80F9F9F9", Hex(p.ControlFill2), "control fill secondary");
            Run.Equal("0F000000", Hex(p.ControlStroke), "control stroke");
            Run.Equal("72000000", Hex(p.ControlStrong), "control strong stroke");
            Run.Equal("09000000", Hex(p.SubtleHover), "subtle hover");
            Run.Equal("06000000", Hex(p.SubtlePress), "subtle pressed");
            Run.Equal("0F000000", Hex(p.Divider), "divider");
            Run.Equal("E4000000", Hex(p.FocusOuter), "focus outer");
            Run.Equal("B3FFFFFF", Hex(p.FocusInner), "focus inner");
            Run.Equal("FF0F7B0F", Hex(p.Success), "success");
            Run.Equal("FF9D5D00", Hex(p.Caution), "caution");
            Run.Equal("FFC42B1C", Hex(p.Critical), "critical");
            Run.Equal("FFFFFFFF", Hex(p.OnAccent), "text on accent");
            Run.Equal("FF005FB8", Hex(p.Accent), "accent fill: Dark1");
            Run.Equal("FF004275", Hex(p.AccentText), "accent text: Dark2");
            Run.Equal("FFF0A020", Hex(p.HdrDot), "HDR dot");
            Run.Equal("FF9D5D00", Hex(p.HdrText), "HDR text");
            Run.Equal("2EF0A020", Hex(p.HdrSoft), "HDR fill");
        }

        public static void PaletteOpaqueText()
        {
            // GDI text has no alpha: a translucent token becomes the colour it shows over its background.
            Run.Equal("FFCFCFCF", Hex(Palette.Over(Color.FromArgb(0xC5, 255, 255, 255), Color.FromArgb(0x2C, 0x2C, 0x2C))), "dark secondary text on the surface");
            Run.Equal("FF1A1A1A", Hex(Palette.Over(Color.FromArgb(0xE4, 0, 0, 0), Color.FromArgb(0xF9, 0xF9, 0xF9))), "light primary text on the surface");
            Run.Equal("FF00FF00", Hex(Palette.Over(Color.FromArgb(255, 0, 255, 0), Color.Red)), "opaque stays itself");
            Run.Equal("FFFF0000", Hex(Palette.Over(Color.FromArgb(0, 0, 255, 0), Color.Red)), "transparent shows what is under it");
            var d = Palette.For(Palette.Kind.Dark, Palette.DefaultAccent);
            Run.Equal("FF373737", Hex(d.CardSolid), "dark card on the surface");
            Run.Equal("FFD2D2D2", Hex(d.Ink(d.Text2, d.CardSolid)), "dark secondary text on a card");
            var l = Palette.For(Palette.Kind.Light, Palette.DefaultAccent);
            Run.Equal("FFFDFDFD", Hex(l.CardSolid), "light card on the surface");
            Run.Equal(255, l.Ink(l.Text3, l.CardSolid).A, "always opaque");
        }

        public static void PaletteAccentFromRegistry()
        {
            var bytes = new byte[32];
            for (int i = 0; i < 8; i++) { bytes[4 * i] = (byte)(10 * i); bytes[4 * i + 1] = (byte)(10 * i + 1); bytes[4 * i + 2] = (byte)(10 * i + 2); bytes[4 * i + 3] = 0; }
            var c = Palette.ParseAccent(bytes);
            Run.Equal(7, c.Length, "seven shades");
            Run.Equal("FF000102", Hex(c[0]), "Light3, opaque whatever its alpha byte");
            Run.Equal("FF1E1F20", Hex(c[3]), "Accent");
            Run.Equal("FF3C3D3E", Hex(c[6]), "Dark3");
            var p = Palette.For(Palette.Kind.Dark, c);
            Run.Equal("FF0A0B0C", Hex(p.Accent), "dark uses Light2");
            Run.Equal("FF000102", Hex(p.AccentText), "dark text uses Light3");
            p = Palette.For(Palette.Kind.Light, c);
            Run.Equal("FF28292A", Hex(p.Accent), "light uses Dark1");
            Run.Equal("FF323334", Hex(p.AccentText), "light text uses Dark2");

            string[] blue = { "FF99EBFF", "FF4CC2FF", "FF0091F8", "FF0078D4", "FF005FB8", "FF004275", "FF002642" };
            foreach (var bad in new[] { null, new byte[0], new byte[27] })
            {
                var d = Palette.ParseAccent(bad);
                Run.Equal(7, d.Length, "missing or short: seven shades");
                for (int i = 0; i < 7; i++) Run.Equal(blue[i], Hex(d[i]), "missing or short: default blue " + i);
            }
            Run.Equal("FF4CC2FF", Hex(Palette.For(Palette.Kind.Dark, null).Accent), "no shades: default blue");
        }

        public static void PaletteFollowsWindows()
        {
            Run.Equal(Palette.Kind.Light, Palette.ModeFor(false, 1), "SystemUsesLightTheme 1: light");
            Run.Equal(Palette.Kind.Dark, Palette.ModeFor(false, 0), "SystemUsesLightTheme 0: dark");
            Run.Equal(Palette.Kind.Light, Palette.ModeFor(false, null), "missing: light, Windows' default");
            Run.Equal(Palette.Kind.Light, Palette.ModeFor(false, "1"), "not a DWORD: light");
            Run.Equal(Palette.Kind.HighContrast, Palette.ModeFor(true, 0), "high contrast wins over dark");
            Run.Equal(Palette.Kind.HighContrast, Palette.ModeFor(true, 1), "high contrast wins over light");

            var hc = Palette.For(Palette.Kind.HighContrast, Palette.DefaultAccent);
            Run.True(hc.HighContrast, "high contrast palette");
            Run.Equal(SystemColors.Window.ToArgb(), hc.Surface.ToArgb(), "surface: Window");
            Run.Equal(SystemColors.WindowText.ToArgb(), hc.Text1.ToArgb(), "text: WindowText");
            Run.Equal(SystemColors.WindowText.ToArgb(), hc.Text2.ToArgb(), "secondary text: WindowText too");
            Run.Equal(SystemColors.GrayText.ToArgb(), hc.TextDisabled.ToArgb(), "disabled: GrayText");
            Run.Equal(SystemColors.Highlight.ToArgb(), hc.Accent.ToArgb(), "on: Highlight, not the accent");
            Run.Equal(SystemColors.HighlightText.ToArgb(), hc.OnAccent.ToArgb(), "on it: HighlightText");
            Run.Equal(SystemColors.ButtonFace.ToArgb(), hc.ControlFill.ToArgb(), "buttons: ButtonFace");
            Run.Equal(2f, hc.StrokeWidth, "2 px borders");
            Run.True(hc.HdrSoft.A == 0 && hc.HdrText.ToArgb() == SystemColors.WindowText.ToArgb(), "no amber: HDR in words");
        }
    }
}
