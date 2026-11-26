// Checks for the flyout's decisions that don't need a window: which monitor it stays on and where it sits there, how
// many list rows fit above the taskbar, what Enter and Space do after the mouse took over or while held, how far the
// wheel and a touchpad scroll, the way back out of a view, and which file the pencil opens.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;

namespace HdrSwitch.Tests
{
    static partial class Checks
    {
        // ---- the flyout ----

        public static void FlyoutStaysOnItsMonitor()
        {
            var both = new List<string> { @"\\.\DISPLAY1", @"\\.\DISPLAY2" };
            Run.Equal(@"\\.\DISPLAY1", TrayApp.FlyoutScreen(@"\\.\DISPLAY1", both, @"\\.\DISPLAY2"), "the mouse on another monitor doesn't move it");
            Run.Equal(@"\\.\DISPLAY2", TrayApp.FlyoutScreen(@"\\.\DISPLAY1", new List<string> { @"\\.\DISPLAY2" }, @"\\.\DISPLAY2"), "its monitor unplugged: the one under the mouse");
            Run.Equal(@"\\.\DISPLAY2", TrayApp.FlyoutScreen(null, both, @"\\.\DISPLAY2"), "not opened yet: the one under the mouse");
        }

        public static void FlyoutCornerByTheTaskbar()
        {
            Point slide;
            var size = new Size(376, 600);
            var p = TrayApp.FlyoutCorner(new Rectangle(0, 0, 1920, 1032), new Rectangle(0, 0, 1920, 1080), size, 12, 24, out slide);
            Run.Equal(new Point(1920 - 12 - 376, 1032 - 12 - 600), p, "taskbar at the bottom: bottom right");
            Run.Equal(new Point(0, 24), slide, "slides up from the taskbar");
            p = TrayApp.FlyoutCorner(new Rectangle(1920, 0, 2560, 1392), new Rectangle(1920, 0, 2560, 1440), size, 18, 36, out slide);
            Run.Equal(new Point(1920 + 2560 - 18 - 376, 1392 - 18 - 600), p, "on the second monitor: in that monitor's corner");
            p = TrayApp.FlyoutCorner(new Rectangle(0, 48, 1920, 1032), new Rectangle(0, 0, 1920, 1080), size, 12, 24, out slide);
            Run.Equal(new Point(1920 - 12 - 376, 48 + 12), p, "taskbar at the top: top right");
            Run.Equal(new Point(0, -24), slide, "slides down from it");
            p = TrayApp.FlyoutCorner(new Rectangle(62, 0, 1858, 1080), new Rectangle(0, 0, 1920, 1080), size, 12, 24, out slide);
            Run.Equal(new Point(62 + 12, 1080 - 12 - 600), p, "taskbar on the left: bottom left");
            Run.Equal(new Point(-24, 0), slide, "slides in from the left");
        }

        /// <summary>The flyout's S(): a length at scale k, rounded as the flyout rounds it.</summary>
        static int Px(double v, double k) { return (int)Math.Round(v * k); }

        /// <summary>
        /// Everything in the flyout but its list rows, built from the same parts as Rebuild: margin, status card (with the
        /// update button and a notice of noticeH logical pixels), the group of three toggle rows, the switch-back row,
        /// the list's header and foot, and the footer bar.
        /// </summary>
        static int FlyoutFixed(double k, bool choosing, bool update, int noticeH)
        {
            int M = Px(12, k), G = Px(8, k), CP = Px(12, k), row = Px(52, k);
            int notice = noticeH > 0 ? Px(8, k) + Px(noticeH, k) : 0;
            int updateY = CP + Px(28, k) + Px(16, k) + notice + Px(12, k);
            int h1 = (update ? updateY + Px(32, k) : updateY - Px(12, k)) + CP;
            int y = M + h1 + G + 3 * row + G + row + G;
            return y + Px(40, k) + (choosing ? Px(52, k) : Px(4, k)) + G + Px(48, k);
        }

        /// <summary>The rows shown on a monitor of this height at scale k, taskbar 48 px (at 100 %) tall.</summary>
        static int FlyoutRowsOn(int screenH, double k, bool choosing, bool update, int noticeH, int count)
        {
            int workArea = screenH - Px(48, k);
            int rows = TrayApp.FlyoutRows(workArea, Px(12, k), FlyoutFixed(k, choosing, update, noticeH), Px(44, k), count);
            int fixedH = FlyoutFixed(k, choosing, update, noticeH);
            if (rows > 1) Run.True(fixedH + rows * Px(44, k) <= workArea - 2 * Px(12, k), "fits above the taskbar at " + k * 100 + " % on " + screenH + "p");
            return rows;
        }

        public static void FlyoutRowsFitTheWorkingArea()
        {
            // A game's resolution view (the tallest list), 20 resolutions.
            Run.Equal(5, FlyoutRowsOn(1080, 1.00, true, false, 0, 20), "1080p at 100 %: five rows");
            Run.Equal(4, FlyoutRowsOn(1080, 1.50, true, false, 0, 20), "1080p at 150 %: four rows");
            Run.Equal(1, FlyoutRowsOn(1080, 1.75, true, false, 0, 20), "1080p at 175 %: one row (five ran off the top), the list scrolls");
            Run.Equal(1, FlyoutRowsOn(1080, 2.00, true, false, 0, 20), "1080p at 200 %: one row, the list scrolls");
            Run.Equal(2, FlyoutRowsOn(1080, 1.50, true, true, 34, 20), "1080p at 150 % with an update button and a notice: two rows");
            Run.Equal(5, FlyoutRowsOn(1440, 1.75, true, false, 0, 20), "1440p at 175 %: five rows");
            Run.Equal(4, FlyoutRowsOn(1440, 2.00, true, false, 0, 20), "1440p at 200 %: four rows");
            Run.Equal(2, FlyoutRowsOn(1440, 2.00, true, true, 34, 20), "1440p at 200 % with an update button and a notice: two rows");
            // The games list: a shorter foot.
            Run.Equal(3, FlyoutRowsOn(1080, 1.75, false, false, 0, 20), "games at 1080p 175 %: three rows");
            Run.Equal(1, FlyoutRowsOn(1080, 2.00, false, false, 0, 20), "games at 1080p 200 %: one row");
            // Never more than the list has, never fewer than one.
            Run.Equal(2, FlyoutRowsOn(1080, 1.00, false, false, 0, 2), "two games: two rows");
            Run.Equal(1, FlyoutRowsOn(1080, 1.00, false, false, 0, 0), "no games: one row for the message");
            Run.Equal(1, TrayApp.FlyoutRows(300, 12, 800, 44, 20), "nothing fits: still one row");
        }

        public static void FlyoutEnterAfterTheMouse()
        {
            Run.Equal(TrayApp.FlyoutKey.Click, TrayApp.FlyoutKeyAction(true, false, true), "ring shown: Enter presses");
            Run.Equal(TrayApp.FlyoutKey.Ring, TrayApp.FlyoutKeyAction(false, false, true), "the mouse hid the ring (Tab to Exit, then the mouse): Enter only shows it");
            Run.Equal(TrayApp.FlyoutKey.Nothing, TrayApp.FlyoutKeyAction(true, true, true), "held down: the repeats press nothing");
            Run.Equal(TrayApp.FlyoutKey.Nothing, TrayApp.FlyoutKeyAction(false, true, true), "held down with the ring hidden: nothing");
            Run.Equal(TrayApp.FlyoutKey.Nothing, TrayApp.FlyoutKeyAction(true, false, false), "nothing focused: nothing");
            // Held Space on a game's switch: the first press flips it, every repeat after it does nothing.
            int flips = 0;
            bool ring = true;
            foreach (bool held in new[] { false, true, true, true, true })
                if (TrayApp.FlyoutKeyAction(ring, held, true) == TrayApp.FlyoutKey.Click) flips++;
            Run.Equal(1, flips, "a held key flips a switch once");
        }

        public static void FlyoutWheelStep()
        {
            Run.Equal(66, TrayApp.WheelStep(3, 44, 5), "Windows' default (3 lines): one and a half rows a notch");
            Run.Equal(22, TrayApp.WheelStep(1, 44, 5), "1 line: half a row");
            Run.Equal(176, TrayApp.WheelStep(20, 44, 5), "many lines: the list less a row");
            Run.Equal(176, TrayApp.WheelStep(-1, 44, 5), "one screen at a time: the list less a row");
            Run.Equal(44, TrayApp.WheelStep(3, 44, 1), "a one-row list: a row");
            Run.Equal(0, TrayApp.WheelStep(0, 44, 5), "scrolling turned off: nothing");
        }

        static int Wheel(int step, params int[] deltas)
        {
            int carry = 0, px = 0;
            foreach (int d in deltas) px += TrayApp.WheelPixels(ref carry, d, step);
            return px;
        }

        public static void FlyoutTouchpadScrollsLikeANotch()
        {
            int notch = Wheel(66, 120);
            Run.Equal(66, notch, "one notch");
            Run.Equal(notch, Wheel(66, 10, 20, 30, 60), "touchpad deltas that add up to 120 scroll as far as a notch");
            var tiny = new int[120];
            for (int i = 0; i < tiny.Length; i++) tiny[i] = 1;
            Run.Equal(notch, Wheel(66, tiny), "120 deltas of 1: the same");
            Run.Equal(notch, Wheel(66, 7, 13, 29, 31, 40), "uneven deltas: the same");
            Run.Equal(-2 * notch, Wheel(66, -15, -45, -60, -30, -90), "two notches down, in pieces");
            Run.Equal(0, Wheel(66, 30, -30, 60, -60), "back and forth: back where it was");
            Run.Equal(Wheel(66, 120, 120, 120), Wheel(66, 25, 25, 25, 25, 25, 25, 25, 25, 25, 25, 25, 25, 25, 25, 10), "three notches' worth");
        }

        public static void FlyoutBackRestoresTheRow()
        {
            var trail = new TrayApp.FlyoutTrail();
            string from;
            double at;
            trail.Push("mon:doom", 132);       // the monitor symbol of a game scrolled down to, into its resolutions
            trail.Push("mode:bright", 0);      // its SDR brightness row, into the levels
            Run.True(trail.Pop(1, out from, out at), "back from the levels");
            Run.Equal("mode:bright", from, "on the SDR brightness row again");
            Run.Equal(0.0, at, "the resolution list as it was");
            Run.True(trail.Pop(0, out from, out at), "back from the resolutions");
            Run.Equal("mon:doom", from, "on the game's monitor symbol again");
            Run.Equal(132.0, at, "the games list scrolled where it was");
            Run.True(!trail.Pop(0, out from, out at), "nothing more to go back to");

            trail.Push("mon:doom", 132);
            trail.Push("mode:stretch", 44);
            Run.True(trail.Pop(0, out from, out at), "a stretched resolution chosen: two views close at once");
            Run.Equal("mon:doom", from, "on the game's row, not the view in between");
            Run.Equal(132.0, at, "scrolled where the games list was");
            Run.Equal(0, trail.Count, "nothing left behind");
        }

        public static void FlyoutPencilFile()
        {
            Run.Equal("games.txt", Path.GetFileName(TrayApp.FlyoutFile(false, false)), "the games list: games.txt");
            Run.Equal("resolutions.txt", Path.GetFileName(TrayApp.FlyoutFile(true, false)), "a game's resolutions: resolutions.txt");
            Run.Equal("brightness.txt", Path.GetFileName(TrayApp.FlyoutFile(true, true)), "its SDR brightness levels: brightness.txt");
        }
    }
}
