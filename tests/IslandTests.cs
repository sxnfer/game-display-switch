// Checks for the island's colours (Island.cs): which colour each kind of dot and its text take in dark, light and a
// contrast theme, and the render harness's sample acts.

using System.Drawing;

namespace HdrSwitch.Tests
{
    static partial class Checks
    {
        static Palette NightSky()
        {
            // Every system colour different, so a check can tell which one was taken.
            return Palette.HighContrastFrom(Color.Black, Color.White, Color.FromArgb(0x1A, 0xEB, 0xFF), Color.FromArgb(1, 2, 3),
                Color.FromArgb(0x3F, 0xF2, 0x3F), Color.FromArgb(4, 5, 6), Color.FromArgb(7, 8, 9));
        }

        public static void IslandDotsDark()
        {
            var p = Palette.For(Palette.Kind.Dark, Palette.DefaultAccent);
            Run.Equal("FFF0A020", Hex(Island.DotColor(Island.Mark.Hdr, p)), "HDR: amber");
            Run.Equal("FFFF99A4", Hex(Island.DotColor(Island.Mark.Critical, p)), "recording, failure: critical");
            Run.Equal("FFFCE100", Hex(Island.DotColor(Island.Mark.Paused, p)), "paused: caution");
            Run.Equal("87FFFFFF", Hex(Island.DotColor(Island.Mark.Off, p)), "SDR: tertiary text");
            Run.Equal("FF4CC2FF", Hex(Island.DotColor(Island.Mark.On, p)), "on (a resolution, an update): the accent");
        }

        public static void IslandDotsLight()
        {
            var p = Palette.For(Palette.Kind.Light, Palette.DefaultAccent);
            Run.Equal("FFF0A020", Hex(Island.DotColor(Island.Mark.Hdr, p)), "HDR: amber");
            Run.Equal("FFC42B1C", Hex(Island.DotColor(Island.Mark.Critical, p)), "recording, failure: critical");
            Run.Equal("FF9D5D00", Hex(Island.DotColor(Island.Mark.Paused, p)), "paused: caution");
            Run.Equal("72000000", Hex(Island.DotColor(Island.Mark.Off, p)), "SDR: tertiary text");
            Run.Equal("FF005FB8", Hex(Island.DotColor(Island.Mark.On, p)), "on: the accent (Dark1)");
        }

        public static void IslandDotFollowsAccent()
        {
            var green = new[] { Color.FromArgb(1, 1, 1), Color.FromArgb(0x10, 0xA0, 0x10), Color.FromArgb(3, 3, 3), Color.FromArgb(4, 4, 4),
                Color.FromArgb(0x05, 0x60, 0x05), Color.FromArgb(6, 6, 6), Color.FromArgb(7, 7, 7) };
            Run.Equal("FF10A010", Hex(Island.DotColor(Island.Mark.On, Palette.For(Palette.Kind.Dark, green))), "dark: the user's Light2");
            Run.Equal("FF056005", Hex(Island.DotColor(Island.Mark.On, Palette.For(Palette.Kind.Light, green))), "light: the user's Dark1");
            Run.Equal("FFF0A020", Hex(Island.DotColor(Island.Mark.Hdr, Palette.For(Palette.Kind.Dark, green))), "HDR stays amber");
        }

        public static void IslandTextColours()
        {
            var d = Palette.For(Palette.Kind.Dark, Palette.DefaultAccent);
            var l = Palette.For(Palette.Kind.Light, Palette.DefaultAccent);
            Run.Equal("FFFFC35C", Hex(Island.TextColor(Island.Mark.Hdr, d)), "dark: HDR's text tint");
            Run.Equal("FF9D5D00", Hex(Island.TextColor(Island.Mark.Hdr, l)), "light: HDR's text tint");
            Run.Equal("FFFFC35C", Hex(Island.SubColor(Island.Mark.Hdr, d)), "dark: the second line of an HDR act is tinted too");
            foreach (var m in new[] { Island.Mark.On, Island.Mark.Off, Island.Mark.Paused, Island.Mark.Critical })
            {
                Run.Equal("FFFFFFFF", Hex(Island.TextColor(m, d)), "dark: primary text (" + m + ")");
                Run.Equal("E4000000", Hex(Island.TextColor(m, l)), "light: primary text (" + m + ")");
                Run.Equal("C5FFFFFF", Hex(Island.SubColor(m, d)), "dark: secondary text (" + m + ")");
                Run.Equal("9E000000", Hex(Island.SubColor(m, l)), "light: secondary text (" + m + ")");
            }
        }

        public static void IslandHighContrast()
        {
            var p = NightSky();
            Run.Equal("FF1AEBFF", Hex(Island.DotColor(Island.Mark.Hdr, p)), "HDR: the highlight colour, no amber");
            Run.Equal("FF1AEBFF", Hex(Island.DotColor(Island.Mark.On, p)), "on: the highlight colour");
            foreach (var m in new[] { Island.Mark.Off, Island.Mark.Paused, Island.Mark.Critical })
                Run.Equal("FFFFFFFF", Hex(Island.DotColor(m, p)), "the text colour (" + m + ")");
            foreach (Island.Mark m in System.Enum.GetValues(typeof(Island.Mark)))
            {
                Run.Equal("FFFFFFFF", Hex(Island.TextColor(m, p)), "text: the window text colour (" + m + ")");
                Run.Equal("FFFFFFFF", Hex(Island.SubColor(m, p)), "second line: the window text colour (" + m + ")");
                Run.True(Island.DotColor(m, p).ToArgb() != Color.FromArgb(0xF0, 0xA0, 0x20).ToArgb(), "no amber (" + m + ")");
            }
            Run.Equal("FF000000", Hex(p.Surface), "the pill: the window colour");
            Run.Equal("FFFFFFFF", Hex(p.Stroke), "its border: the text colour");
            Run.Equal(2f, p.StrokeWidth, "2 px");
        }

        public static void IslandSampleActs()
        {
            var hdr = Island.SampleAct("hdr", false);
            Run.True(hdr.Big && hdr.Dot == Island.Mark.Hdr && hdr.Sub.StartsWith("HDR"), "hdr: expanded, amber, says HDR in words");
            Run.True(!Island.SampleAct("hdr", true).Big, "--compact: the compact shape");
            var rec = Island.SampleAct("recording", false);
            Run.True(rec.Split && rec.Dot == Island.Mark.Off && rec.Bubble == Island.Mark.Critical, "recording: SDR dot, red bubble");
            Run.Equal(Island.Mark.Hdr, Island.SampleAct("sdr-countdown", false).Dot, "the countdown while HDR is still on");
            Run.Equal(Island.Mark.Paused, Island.SampleAct("paused", false).Dot, "paused");
            Run.Equal(Island.Mark.On, Island.SampleAct("update", false).Dot, "update: the accent");
            Run.Equal(Island.Mark.On, Island.SampleAct("resolution", false).Dot, "resolution only: the accent");
            Run.True(Island.SampleAct("nope", false) == null, "an unknown act");
            Run.True(Palette.Sample("hc").HighContrast && Palette.Sample("light").Mode == Palette.Kind.Light && Palette.Sample("dark").Mode == Palette.Kind.Dark, "the harness's palettes");
        }

        public static void IslandDefaultMarks()
        {
            var a = new Island.Act();
            Run.Equal(Island.Mark.On, a.Dot, "news is on: the accent");
            Run.Equal(Island.Mark.Off, a.Bubble, "a bubble is grey unless said otherwise");
        }
    }
}
