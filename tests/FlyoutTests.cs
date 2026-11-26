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

        /// <summary>The rows the Games tab shows on a monitor of this height at scale k, taskbar 48 px (at 100 %) tall.</summary>
        static int FlyoutRowsOn(int screenH, double k, bool choosing, int count)
        {
            int workArea = screenH - Px(48, k);
            int fixedH = TrayApp.GamesFixed((float)k, choosing);
            int rows = TrayApp.FlyoutRows(workArea, Px(12, k), fixedH, Px(44, k), count);
            if (rows > 1) Run.True(fixedH + rows * Px(44, k) <= workArea - 2 * Px(12, k), "fits above the taskbar at " + k * 100 + " % on " + screenH + "p");
            return rows;
        }

        public static void FlyoutRowsFitTheWorkingArea()
        {
            // The parts around the list, as Rebuild lays them out: tab bar 48, 4 above the list, a view's note 52 (else
            // 4), 8, the footer bar 48. The status card and the settings rows live in their own tabs now.
            Run.Equal(112, TrayApp.GamesFixed(1, false), "the games list's frame at 100 %");
            Run.Equal(160, TrayApp.GamesFixed(1, true), "a game's resolution view's frame at 100 %");
            Run.Equal(320, TrayApp.GamesFixed(2, true), "… at 200 %");
            // A game's resolution view (the tallest list), 20 resolutions. Before U1: 5, 5, 3, 1; U1: 5, 4, 1, 1.
            Run.Equal(5, FlyoutRowsOn(1080, 1.00, true, 20), "1080p at 100 %: five rows");
            Run.Equal(5, FlyoutRowsOn(1080, 1.50, true, 20), "1080p at 150 %: five rows (U1: four)");
            Run.Equal(5, FlyoutRowsOn(1080, 1.75, true, 20), "1080p at 175 %: five rows (U1: one)");
            Run.Equal(5, FlyoutRowsOn(1080, 2.00, true, 20), "1080p at 200 %: five rows (U1: one)");
            Run.Equal(5, FlyoutRowsOn(1440, 1.75, true, 20), "1440p at 175 %: five rows");
            Run.Equal(5, FlyoutRowsOn(1440, 2.00, true, 20), "1440p at 200 %: five rows (U1: four)");
            Run.Equal(2, FlyoutRowsOn(720, 2.00, true, 20), "720p at 200 %: two rows, the list scrolls");
            // The games list. Before U1: 4 at 175 %, 2 at 200 %; U1: 3 and 1.
            Run.Equal(5, FlyoutRowsOn(1080, 1.75, false, 20), "games at 1080p 175 %: five rows (U1: three)");
            Run.Equal(5, FlyoutRowsOn(1080, 2.00, false, 20), "games at 1080p 200 %: five rows (U1: one)");
            // Never more than the list has, never fewer than one.
            Run.Equal(2, FlyoutRowsOn(1080, 1.00, false, 2), "two games: two rows");
            Run.Equal(1, FlyoutRowsOn(1080, 1.00, false, 0), "no games: one row for the message");
            Run.Equal(1, TrayApp.FlyoutRows(300, 12, 800, 44, 20), "nothing fits: still one row");
        }

        /// <summary>
        /// Live's height outside its scrolling body at scale k, as Rebuild lays it out: tab bar 48, the status (title 28,
        /// line 16) and 8 below it, then 8 and the update's footer bar (52) – or, without an update, the 12 px margin.
        /// </summary>
        static int LiveFixed(double k, bool update)
        {
            return Px(48, k) + Px(28, k) + Px(16, k) + Px(8, k) + (update ? Px(8, k) + Px(52, k) : Px(12, k));
        }

        public static void FlyoutLiveAndSettingsScrollWhenTooTall()
        {
            // Live's body: five layer rows, 8, the recording card, 4 below: 52 × 6 + 12 at 100 %.
            int body1 = 6 * 52 + 12, body2 = 6 * 104 + 24;
            Run.Equal(body1, TrayApp.FlyoutBody(1032, 12, LiveFixed(1, true), body1, 52), "1080p at 100 %: all of it");
            int body15 = 6 * 78 + 18;
            Run.Equal(body15, TrayApp.FlyoutBody(1080 - 72, 18, LiveFixed(1.5, true), body15, 78), "1080p at 150 % with an update: all of it");
            int shown = TrayApp.FlyoutBody(1080 - 96, 24, LiveFixed(2, true), body2, 104);
            Run.True(shown < body2, "1080p at 200 % with an update: the body scrolls");
            Run.Equal(984 - 48 - LiveFixed(2, true), shown, "… and fills the working area");
            Run.Equal(104, TrayApp.FlyoutBody(500, 24, 600, body2, 104), "nothing fits: still one row");
            Run.Equal(40, TrayApp.FlyoutBody(500, 24, 600, 40, 104), "never more than there is");
        }

        // ---- the tabs ----

        public static void FlyoutTabsCycle()
        {
            Run.Equal(FlyoutTab.Games, TrayApp.NextTab(FlyoutTab.Live, 1), "Ctrl+Tab on Live: Games");
            Run.Equal(FlyoutTab.Settings, TrayApp.NextTab(FlyoutTab.Games, 1), "Ctrl+Tab on Games: Settings");
            Run.Equal(FlyoutTab.Live, TrayApp.NextTab(FlyoutTab.Settings, 1), "Ctrl+Tab on Settings: round to Live");
            Run.Equal(FlyoutTab.Settings, TrayApp.NextTab(FlyoutTab.Live, -1), "Ctrl+Shift+Tab on Live: round to Settings");
            Run.Equal(FlyoutTab.Live, TrayApp.NextTab(FlyoutTab.Games, -1), "Left on Games: Live");
            var t = FlyoutTab.Games;
            for (int i = 0; i < 3; i++) t = TrayApp.NextTab(t, 1);
            Run.Equal(FlyoutTab.Games, t, "three steps: back where it started");
        }

        public static void FlyoutOpensOnLiveWhenSomethingRuns()
        {
            Run.Equal(FlyoutTab.Live, TrayApp.OpeningTab(true, false, FlyoutTab.Settings), "a game runs: Live, whatever was used last");
            Run.Equal(FlyoutTab.Live, TrayApp.OpeningTab(false, true, FlyoutTab.Games), "paused, counting down or held for a recorder: Live");
            Run.Equal(FlyoutTab.Games, TrayApp.OpeningTab(false, false, FlyoutTab.Games), "nothing going on: the tab used last");
            Run.Equal(FlyoutTab.Settings, TrayApp.OpeningTab(false, false, FlyoutTab.Settings), "… Settings too");
            Run.Equal(FlyoutTab.Live, TrayApp.OpeningTab(false, false, FlyoutTab.Live), "first time: Live");
        }

        // ---- Live: the layer stack and the status line ----

        static Resolution.Mode Mode(int w, int h) { return new Resolution.Mode { Width = w, Height = h, Hz = 240 }; }

        /// <summary>"hdr=ON*" …: each layer's value, a star when it is in effect.</summary>
        static string Layers(TrayApp.LiveState s)
        {
            var parts = new List<string>();
            foreach (var l in TrayApp.LiveLayers(s)) parts.Add(l.Key + "=" + l.Value + (l.Active ? "*" : ""));
            return string.Join(" ", parts.ToArray());
        }

        static TrayApp.LiveLayer Layer(TrayApp.LiveState s, string key) { return TrayApp.LiveLayers(s).Find(l => l.Key == key); }

        static string Status(TrayApp.LiveState s)
        {
            string title, sub;
            TrayApp.LiveStatus(s, out title, out sub);
            return title + " | " + sub;
        }

        public static void LiveIdle()
        {
            var s = new TrayApp.LiveState();
            Run.Equal("hdr=— bright=— area=— stretch=— scaling=—", Layers(s), "no game: every layer dimmed, a dash");
            Run.Equal("Waiting for a game | HDR off", Status(s), "the status");
            Run.Equal("HDR,SDR brightness,Competitive area,Stretch,NVIDIA scaling", string.Join(",", TrayApp.LiveLayers(s).ConvertAll(l => l.Title).ToArray()), "the order of the stack");
            s.HdrElsewhere = true;
            Run.Equal("hdr=On", Layers(s).Split(' ')[0], "HDR turned on in Windows: On, but not lit");
            Run.Equal("Waiting for a game | HDR is on, turned on outside Display Switch", Status(s), "… and said so");
            Run.Equal("No HDR display | Check Windows display settings", Status(new TrayApp.LiveState { HdrSupported = false }), "no HDR display");
        }

        public static void LiveHdrGameWithBrightness()
        {
            var s = new TrayApp.LiveState { Game = "Cyberpunk 2077", GameHdr = true, GameBrightness = true, HdrOurs = true, Brightness = 40, BackTo = "SDR", Screen = "LG 27GR95QE" };
            Run.Equal("hdr=ON* bright=40 of 100* area=Off stretch=Off scaling=Unchanged", Layers(s), "HDR and the game's SDR brightness");
            Run.Equal("Turned on by Display Switch", Layer(s, "hdr").Desc, "who turned HDR on");
            Run.Equal("Not set for this game", Layer(s, "area").Desc, "no competitive area for it");
            Run.Equal("Cyberpunk 2077 | LG 27GR95QE · back to SDR 3 s after it closes", Status(s), "the game, its screen and the wait");
            s.Delay = 30;
            Run.Equal("Cyberpunk 2077 | LG 27GR95QE · back to SDR 30 s after it closes", Status(s), "the wait chosen in Settings");
            s.Brightness = -1;
            s.BrightnessLeft = true;
            Run.Equal("bright=Windows'", Layers(s).Split(' ')[1], "the slider moved by hand: Windows' own");
            Run.Equal("Your level, left as you set it", Layer(s, "bright").Desc, "… and why");
        }

        public static void LiveCompetitiveAndStretch()
        {
            var s = new TrayApp.LiveState { Game = "VALORANT", GameHdr = true, GameArea = true, GameStretch = true, HdrOurs = true, BackTo = "SDR",
                Area = Mode(2368, 1332), Stretch = Mode(1776, 1332), AreaDetail = "92.5 % · 24.5″" };
            Run.Equal("hdr=ON* bright=Windows' area=2368 × 1332* stretch=1776 × 1332* scaling=Unchanged", Layers(s), "area and stretch in effect, scaling left alone");
            Run.Equal("Centred, 92.5 % · 24.5″", Layer(s, "area").Desc, "how much of the panel");
            Run.Equal("Stretched over 2368 × 1332", Layer(s, "stretch").Desc, "stretched over the area");
            Run.Equal("Left alone: a stretched game doesn't need it", Layer(s, "scaling").Desc, "why scaling is unchanged");
            s.StretchLost = true;
            Run.Equal("The game set its own mode since", Layer(s, "stretch").Desc, "the game's own mode took over");
            var area = new TrayApp.LiveState { Game = "cs2.exe", GameArea = true, Area = Mode(2368, 1332), BackTo = "2560 × 1440" };
            Run.Equal("hdr=Off bright=Windows' area=2368 × 1332* stretch=Off scaling=Unchanged", Layers(area), "an area alone, no HDR for the game");
            Run.Equal("cs2.exe | back to 2560 × 1440 3 s after it closes", Status(area), "the desktop's resolution comes back");
        }

        public static void LiveScalingTaken()
        {
            var s = new TrayApp.LiveState { Game = "Counter-Strike 2", GameArea = true, Area = Mode(2368, 1332), ScalingTaken = true, BackTo = "2560 × 1440" };
            Run.True(Layer(s, "scaling").Active && Layer(s, "scaling").Value == "No scaling", "No scaling, set by this app");
            Run.Equal("Set automatically, put back after", Layer(s, "scaling").Desc, "… and put back");
            var stray = new TrayApp.LiveState { ScalingStray = true };
            Run.True(Layer(stray, "scaling").Active && Layer(stray, "scaling").Value == "No scaling", "still to put back, no game");
            Run.Equal("Still to be put back", Layer(stray, "scaling").Desc, "… said so");
        }

        public static void LiveGraceCountdown()
        {
            var s = new TrayApp.LiveState { HdrOurs = true, GraceLeft = 2, BackTo = "SDR", Screen = "LG 27GR95QE", Area = Mode(2368, 1332), ScalingTaken = true };
            Run.Equal("Switching back | LG 27GR95QE · back to SDR in 2s", Status(s), "the countdown");
            Run.Equal("hdr=ON* bright=— area=2368 × 1332* stretch=— scaling=No scaling*", Layers(s), "what is still switched, until it goes back");
            s.GraceLeft = 0;
            Run.Equal("Switching back | LG 27GR95QE · back to SDR in 1s", Status(s), "never says 0s");
            var mode = new TrayApp.LiveState { GraceLeft = 10, BackTo = "2560 × 1440", Area = Mode(2368, 1332) };
            Run.Equal("Switching back | back to 2560 × 1440 in 10s", Status(mode), "a resolution alone: the desktop's comes back");
        }

        public static void LivePaused()
        {
            var s = new TrayApp.LiveState { Paused = true, Game = "Counter-Strike 2", GameHdr = true, GameArea = true, GameBrightness = true };
            Run.Equal("Paused | Nothing switches · Ctrl+Alt+H resumes", Status(s), "paused, a game running");
            Run.Equal("hdr=Off bright=Windows' area=Off stretch=Off scaling=Unchanged", Layers(s), "everything went back");
            Run.Equal("Paused", Layer(s, "hdr").Desc, "the reason on each layer");
            Run.Equal("Paused | Nothing switches · Ctrl+Alt+H resumes", Status(new TrayApp.LiveState { Paused = true }), "paused, no game");
        }

        public static void LiveRecordingHold()
        {
            var s = new TrayApp.LiveState { Game = "Counter-Strike 2", GameHdr = true, GameArea = true, Area = Mode(2368, 1332), ScalingTaken = true,
                RecordingOn = true, Recorder = "OBS Studio", BackTo = "2560 × 1440" };
            Run.Equal("hdr=Off bright=Windows' area=2368 × 1332* stretch=Off scaling=No scaling*", Layers(s), "HDR held off, the area still on");
            Run.Equal("Held off: OBS Studio is running", Layer(s, "hdr").Desc, "who holds HDR off");
            Run.Equal("Counter-Strike 2 | back to 2560 × 1440 3 s after it closes", Status(s), "the status");
            s.Area = null;
            s.ScalingTaken = false;
            Run.Equal("Counter-Strike 2 | HDR held off while recording", Status(s), "nothing switched at all: why");
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
