// Checks for the crash-recovery notes in %AppData%\HdrSwitch: hdr.state, brightness.state and resolution.state are
// read back after a crash, a kill or a reboot, so each is checked for a round trip, the formats older versions wrote,
// lines that are no such note, and an empty file. Also the rule that a brightness slider moved by hand mid-game wins,
// and the size of the struct the display's stable Id is read with.

using System;
using System.Collections.Generic;
using System.Linq;

namespace HdrSwitch.Tests
{
    static partial class Checks
    {
        // ---- crash recovery ----

        const string PathA = @"\\?\DISPLAY#DEL41B8#5&2b9f1c3&0&UID4352#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
        const string PathB = @"\\?\DISPLAY#GSM5B7F#5&2b9f1c3&0&UID4353#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";

        public static void TargetNameStructSize()
        {
            Run.Equal(420, Hdr.TargetNameSize, "DISPLAYCONFIG_TARGET_DEVICE_NAME is 420 bytes, as in the Windows SDK");
        }

        public static void HdrStateRoundTrip()
        {
            string text = TrayApp.FormatHdrState(new[] { PathA, PathB });
            Run.Equal(PathA + "\r\n" + PathB + "\r\n", text, "one Id per line");
            Run.Equal(PathA + "," + PathB, string.Join(",", TrayApp.ParseHdrState(text)), "read back as written");
            Run.Equal(PathA, string.Join(",", TrayApp.ParseHdrState(TrayApp.FormatHdrState(new[] { PathA, PathA.ToLowerInvariant(), null }))),
                "each display once");
            Run.Equal(@"\\.\DISPLAY1", string.Join(",", TrayApp.ParseHdrState(TrayApp.FormatHdrState(new[] { @"\\.\DISPLAY1" }))),
                "a GDI name, the Id when the path can't be read");
        }

        public static void HdrStateEmptyAndGarbage()
        {
            Run.Equal(0, TrayApp.ParseHdrState("").Count, "an empty file owns nothing");
            Run.Equal(0, TrayApp.ParseHdrState(null).Count, "no file owns nothing");
            Run.Equal(0, TrayApp.ParseHdrState("\r\n  \r\n").Count, "blank lines own nothing");
            Run.Equal("", TrayApp.FormatHdrState(new string[0]), "nothing owned: an empty text (the file is deleted instead)");
            var ids = TrayApp.ParseHdrState("# note\r\n  " + PathA + "  \r\nbad\u0001line\r\n" + new string('x', 600) + "\r\n" + PathB);
            Run.Equal(PathA + "," + PathB, string.Join(",", ids), "notes, control characters and overlong lines are skipped, spaces trimmed");
        }

        /// <summary>Only a display listed in hdr.state is taken back after a crash: HDR turned on elsewhere isn't.</summary>
        public static void HdrStateOnlyListedDisplays()
        {
            var listed = new Hdr.Display { Target = 1, Gdi = @"\\.\DISPLAY1", Supported = true, On = true, id = PathA };
            var users = new Hdr.Display { Target = 2, Gdi = @"\\.\DISPLAY2", Supported = true, On = true, id = PathB };
            var ids = TrayApp.ParseHdrState(TrayApp.FormatHdrState(new[] { PathA }));
            Run.True(ids.Any(listed.Is), "the display this app switched is found again by its Id");
            Run.True(!ids.Any(users.Is), "a display it didn't switch is not");
            var oldKey = new Hdr.Display { Target = 7, Gdi = "", Supported = true, On = true, id = PathA };
            Run.True(oldKey.Is(oldKey.Key) && !oldKey.Is("") && !oldKey.Is(null), "by its Key within the same boot; never by nothing");
        }

        public static void BrightStateRoundTrip()
        {
            string text = TrayApp.FormatBrightState(PathA, 2500, 4000);
            Run.Equal(PathA + "|2500|4000", text, "Id, level before, level set");
            string d;
            int back, set;
            Run.True(TrayApp.ParseBrightState(text + "\r\n", out d, out back, out set), "read back");
            Run.Equal(PathA, d, "the display's Id");
            Run.Equal(2500, back, "the level before");
            Run.Equal(4000, set, "the level set");
        }

        public static void BrightStateOldFormatAndGarbage()
        {
            string d;
            int back, set;
            // Written by an older version: the adapter's LUID and target id, which only hold until Windows restarts.
            Run.True(TrayApp.ParseBrightState("0:84123:4352|2000|3500", out d, out back, out set), "an older file is read");
            Run.Equal("0:84123:4352", d, "with its boot-only key, matched against Display.Key or dropped");
            var x = new Hdr.Display { Target = 4352, Gdi = @"\\.\DISPLAY1", id = PathA };
            Run.True(!x.Is("0:84123:4352"), "after a reboot the old key finds no display (its LUID changed), so the level is dropped");

            Run.True(!TrayApp.ParseBrightState("", out d, out back, out set), "empty");
            Run.True(!TrayApp.ParseBrightState(null, out d, out back, out set), "no file");
            Run.True(!TrayApp.ParseBrightState("garbage", out d, out back, out set), "one field");
            Run.True(!TrayApp.ParseBrightState(PathA + "|x|4000", out d, out back, out set), "not a number");
            Run.True(!TrayApp.ParseBrightState(PathA + "|-1|4000", out d, out back, out set), "nothing to put back");
            Run.True(!TrayApp.ParseBrightState("|2000|4000", out d, out back, out set), "no display");
            Run.True(!TrayApp.ParseBrightState(PathA + "|2000|4000|9", out d, out back, out set), "too many fields");
            Run.True(d == null && back == -1 && set == -1, "a failed read leaves nothing behind");
        }

        public static void BrightnessUserTakesOver()
        {
            // The game wants 4000; the level before was 2500; this app set 4000.
            Run.True(!TrayApp.UserMovedBrightness(4000, 4000, 2500, 4000), "still the game's level: carry on");
            Run.True(TrayApp.UserMovedBrightness(3100, 4000, 2500, 4000), "slider moved by hand: the user's from now on");
            Run.True(!TrayApp.UserMovedBrightness(2500, 4000, 2500, 4000), "back at the level from before: set the game's again");
            Run.True(!TrayApp.UserMovedBrightness(4000, 4500, 2500, 4000), "the game's level changed in the flyout: not the user's slider");
            Run.True(!TrayApp.UserMovedBrightness(4500, 4500, 2500, 4000), "already at the game's new level");
            Run.True(!TrayApp.UserMovedBrightness(3100, 4000, -1, -1), "nothing set yet: the first level is what was there before");
            Run.True(!TrayApp.UserMovedBrightness(-1, 4000, 2500, 4000), "not readable: decide nothing");
        }

        public static void ModeStateRoundTrip()
        {
            var m = new TrayApp.ModeState { Gdi = @"\\.\DISPLAY1", Id = PathA, Width = 1776, Height = 1332, Hz = 240, ScalingBack = 7, AreaWidth = 2368, AreaHeight = 1332 };
            string text = TrayApp.FormatModeState(m);
            Run.Equal(@"\\.\DISPLAY1|1776|1332|240|7|2368|1332|" + PathA, text, "all fields, the Id last");
            var r = TrayApp.ParseModeState(text);
            Run.True(r != null, "read back");
            Run.Equal(@"\\.\DISPLAY1", r.Gdi, "GDI name");
            Run.Equal(PathA, r.Id, "Id");
            Run.Equal("1776x1332@240", r.Width + "x" + r.Height + "@" + r.Hz, "mode");
            Run.Equal(7, r.ScalingBack, "scaling");
            Run.Equal("2368x1332", r.AreaWidth + "x" + r.AreaHeight, "area");
        }

        /// <summary>The mode is gone after a crash but the scaling setting isn't: it stays in the file, with its display, until it is back.</summary>
        public static void ModeStateScalingOnly()
        {
            string text = TrayApp.FormatModeState(TrayApp.ModeState.ScalingOnly(@"\\.\DISPLAY2", 5, PathB));
            Run.Equal(@"\\.\DISPLAY2|0|0|0|5|0|0|" + PathB, text, "a scaling-only record");
            var r = TrayApp.ParseModeState(text);
            Run.True(r != null && r.Width == 0 && r.ScalingBack == 5, "read back after a second crash, scaling still to put back");
            Run.Equal(PathB, r.Id, "on the display it came from");
            // A second crash rewrites the same record: nothing is lost on the way.
            Run.Equal(text, TrayApp.FormatModeState(TrayApp.ModeState.ScalingOnly(r.Gdi, r.ScalingBack, r.Id ?? r.Gdi)), "rewritten unchanged");
        }

        public static void ModeStateOldFormats()
        {
            var a = TrayApp.ParseModeState(@"\\.\DISPLAY1|2368|1332|240");
            Run.True(a != null && a.ScalingBack == -1 && a.Id == null && a.AreaWidth == 0, "mode only (oldest)");
            var b = TrayApp.ParseModeState(@"\\.\DISPLAY1|2368|1332|240|3");
            Run.True(b != null && b.ScalingBack == 3 && b.Id == null, "with scaling");
            var c = TrayApp.ParseModeState(@"\\.\DISPLAY1|1776|1332|240|-1|2368|1332");
            Run.True(c != null && c.ScalingBack == -1 && c.AreaWidth == 2368 && c.Id == null, "stretched");
            var d = TrayApp.ParseModeState(@"\\.\DISPLAY1|0|0|0|5" + "\r\n");
            Run.True(d != null && d.Width == 0 && d.ScalingBack == 5 && d.Id == null, "scaling only, as older versions wrote it");
        }

        public static void ModeStateGarbage()
        {
            Run.True(TrayApp.ParseModeState("") == null, "empty");
            Run.True(TrayApp.ParseModeState(null) == null, "no file");
            Run.True(TrayApp.ParseModeState("hello") == null, "one field");
            Run.True(TrayApp.ParseModeState(@"\\.\DISPLAY1|x|1332|240") == null, "not a number");
            Run.True(TrayApp.ParseModeState(@"|2368|1332|240") == null, "no display");
            Run.True(TrayApp.ParseModeState(@"\\.\DISPLAY1|2368|1332|240|3|a|b") == null, "a broken area");
            Run.True(TrayApp.ParseModeState(@"\\.\DISPLAY1|-5|1332|240") == null, "a negative size");
        }
    }
}
