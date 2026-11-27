// Checks for ModeKeeper.cs: the one decision about a desktop that is no longer in the competitive mode this app set –
// keep, set again, leave the game's own mode alone, let go after a change on purpose, or give up – with its budget of
// re-applies and its wait after a display change. Also what a mode taken over after a crash goes back to, and the
// mode to go back to in resolution.state.

using System;
using System.Collections.Generic;
using System.Linq;

namespace HdrSwitch.Tests
{
    static partial class Checks
    {
        // ---- keeping the competitive mode ----

        static Resolution.Mode M(int w, int h, int hz) { return new Resolution.Mode { Width = w, Height = h, Hz = hz }; }
        static readonly Resolution.Mode Native = M(2560, 1440, 240), Comp = M(2368, 1332, 240), Own43 = M(1776, 1332, 240);

        static Resolution.Picture Pic(int w, int h, int scaling) { return new Resolution.Picture { SourceW = w, SourceH = h, TargetW = 2560, TargetH = 1440, Scaling = scaling }; }

        /// <summary>A 1:1 competitive mode held for a running game, the desktop still in it and centred.</summary>
        static ModeKeeper.Seen Held()
        {
            return new ModeKeeper.Seen { Want = Comp, Back = Native, Current = M(2368, 1332, 240), Saved = M(2560, 1440, 240), Picture = Pic(2368, 1332, 2),
                WasCentred = true, GameRunning = true, How = ModeKeeper.How.DisplayChange };
        }

        /// <summary>A stretched mode held for a running game, the desktop still stretched over its area.</summary>
        static ModeKeeper.Seen HeldStretched()
        {
            var s = Held();
            s.Want = Own43; s.Area = Comp; s.Current = M(1776, 1332, 240); s.Picture = Pic(1776, 1332, 1); s.Stretched = true; s.WasCentred = false;
            return s;
        }

        public static void KeepModeNothingToJudge()
        {
            Run.Equal(ModeKeeper.Verdict.None, ModeKeeper.Decide(null), "nothing seen");
            var s = Held(); s.Want = null;
            Run.Equal(ModeKeeper.Verdict.None, ModeKeeper.Decide(s), "no mode of ours");
            s = Held(); s.Current = Native; s.Paused = true;
            Run.Equal(ModeKeeper.Verdict.None, ModeKeeper.Decide(s), "paused: nothing is set again");
            s = Held(); s.Current = Native; s.GameRunning = false;
            Run.Equal(ModeKeeper.Verdict.None, ModeKeeper.Decide(s), "no game with a resolution runs: the grace period decides, not this");
        }

        public static void KeepModeStillOurs()
        {
            Run.Equal(ModeKeeper.Verdict.Keep, ModeKeeper.Decide(Held()), "in our mode, centred");
            Run.Equal(ModeKeeper.Verdict.Keep, ModeKeeper.Decide(HeldStretched()), "in our mode, stretched over its area");
            var s = Held(); s.Picture = null;
            Run.Equal(ModeKeeper.Verdict.Keep, ModeKeeper.Decide(s), "the display path can't be read: the mode alone says it is ours");
            s = Held(); s.Picture = Pic(2368, 1332, 3); s.WasCentred = false;
            Run.Equal(ModeKeeper.Verdict.Keep, ModeKeeper.Decide(s), "a driver that never centred it: not a reset, nothing to fight");
            s = Held(); s.Saved = M(2368, 1332, 240);
            Run.Equal(ModeKeeper.Verdict.Keep, ModeKeeper.Decide(s), "our temporary mode got saved (Control Panel mid-game): still ours, undone at the game's exit");
            s = Held(); s.Retries = 3; s.GaveUp = true;
            Run.Equal(ModeKeeper.Verdict.Keep, ModeKeeper.Decide(s), "back in our mode after giving up");
            foreach (ModeKeeper.How how in Enum.GetValues(typeof(ModeKeeper.How)))
            {
                s = Held(); s.How = how;
                Run.Equal(ModeKeeper.Verdict.Keep, ModeKeeper.Decide(s), "in our mode, whatever made us look: " + how);
            }
        }

        public static void KeepModeReappliesAfterAReset()
        {
            foreach (ModeKeeper.How how in Enum.GetValues(typeof(ModeKeeper.How)))
            {
                var s = Held(); s.Current = M(2560, 1440, 240); s.Picture = Pic(2560, 1440, 1); s.How = how;
                Run.Equal(ModeKeeper.Verdict.Reapply, ModeKeeper.Decide(s), "back at the saved mode under a running game – Windows reset it: " + how);
            }
            var t = HeldStretched(); t.Current = M(2560, 1440, 240); t.Stretched = false;
            Run.Equal(ModeKeeper.Verdict.Reapply, ModeKeeper.Decide(t), "a stretched mode reset to the saved mode");
            t = HeldStretched(); t.Stretched = false;
            Run.Equal(ModeKeeper.Verdict.Reapply, ModeKeeper.Decide(t), "the size is ours but it is no longer stretched over its area");
            t = Held(); t.Picture = Pic(2368, 1332, 3);
            Run.Equal(ModeKeeper.Verdict.Reapply, ModeKeeper.Decide(t), "the size is ours but it is no longer centred, and it was");
            t = Held(); t.Current = M(2560, 1440, 240); t.Retries = 2;
            Run.Equal(ModeKeeper.Verdict.Reapply, ModeKeeper.Decide(t), "the third time in a minute is still within the budget");
            t = Held(); t.Current = M(2560, 1440, 240); t.Back = null;
            Run.Equal(ModeKeeper.Verdict.Reapply, ModeKeeper.Decide(t), "no record of the saved mode at the start: at the saved mode is a reset all the same");
        }

        public static void KeepModeLeavesTheGamesOwnMode()
        {
            var s = Held(); s.Current = M(1280, 960, 240);
            Run.Equal(ModeKeeper.Verdict.GameOwnMode, ModeKeeper.Decide(s), "a third mode, neither ours nor the saved one: the game's own");
            s = Held(); s.Current = M(2368, 1332, 144);
            Run.Equal(ModeKeeper.Verdict.GameOwnMode, ModeKeeper.Decide(s), "our size at another rate is the game's own mode too");
            s = HeldStretched(); s.Current = M(1920, 1080, 240); s.Stretched = false;
            Run.Equal(ModeKeeper.Verdict.GameOwnMode, ModeKeeper.Decide(s), "a stretched profile, and the game set its own mode");
            s = Held(); s.Current = M(1280, 960, 240); s.Retries = 3; s.GaveUp = true;
            Run.Equal(ModeKeeper.Verdict.GameOwnMode, ModeKeeper.Decide(s), "never fought, so the budget doesn't come into it");
            foreach (ModeKeeper.How how in Enum.GetValues(typeof(ModeKeeper.How)))
            {
                s = Held(); s.Current = M(1280, 960, 240); s.How = how;
                Run.Equal(ModeKeeper.Verdict.GameOwnMode, ModeKeeper.Decide(s), "the game's own mode is left alone, whatever made us look: " + how);
            }
            s = Held(); s.Current = M(1280, 960, 240); s.Saved = null;
            Run.Equal(ModeKeeper.Verdict.GameOwnMode, ModeKeeper.Decide(s), "no saved mode readable: not known to be a reset, so no fight");
        }

        public static void KeepModeLetsGoWhenSettingsChange()
        {
            var s = Held(); s.Current = M(1920, 1080, 240); s.Saved = M(1920, 1080, 240);
            Run.Equal(ModeKeeper.Verdict.UserChanged, ModeKeeper.Decide(s), "the saved mode itself is another one now: changed in display settings");
            s = Held(); s.Current = M(2560, 1440, 144); s.Saved = M(2560, 1440, 144);
            Run.Equal(ModeKeeper.Verdict.UserChanged, ModeKeeper.Decide(s), "only the refresh rate was changed: still on purpose");
            s = Held(); s.Current = M(1280, 960, 240); s.Saved = M(1920, 1080, 240);
            Run.Equal(ModeKeeper.Verdict.UserChanged, ModeKeeper.Decide(s), "changed settings come before a game's own mode");
            s = Held(); s.Current = M(1920, 1080, 240); s.Saved = M(1920, 1080, 240); s.How = ModeKeeper.How.OwnSwitch;
            Run.Equal(ModeKeeper.Verdict.UserChanged, ModeKeeper.Decide(s), "even right after our own HDR switch");
            s = Held(); s.Current = M(1920, 1080, 240); s.Saved = M(1920, 1080, 240); s.Retries = 0; s.How = ModeKeeper.How.Scan; s.SinceChangeMs = 5000;
            Run.Equal(ModeKeeper.Verdict.UserChanged, ModeKeeper.Decide(s), "found by the scan as well");
            s = Held(); s.Current = M(2560, 1440, 240); s.Saved = M(2368, 1332, 240);
            Run.Equal(ModeKeeper.Verdict.GameOwnMode, ModeKeeper.Decide(s), "our own mode saved is no change on purpose; the desktop elsewhere is left alone");
        }

        public static void KeepModeBudget()
        {
            var s = Held(); s.Current = M(2560, 1440, 240); s.Retries = ModeKeeper.Budget;
            Run.Equal(3, ModeKeeper.Budget, "three re-applies a minute");
            Run.Equal(ModeKeeper.Verdict.GiveUp, ModeKeeper.Decide(s), "reset a fourth time within a minute: given up");
            s.Retries = 7;
            Run.Equal(ModeKeeper.Verdict.GiveUp, ModeKeeper.Decide(s), "and beyond");
            s = HeldStretched(); s.Stretched = false; s.Retries = 3;
            Run.Equal(ModeKeeper.Verdict.GiveUp, ModeKeeper.Decide(s), "a stretch lost over and over is given up too");
            s = Held(); s.Current = M(2560, 1440, 240); s.Retries = 3; s.How = ModeKeeper.How.OwnSwitch;
            Run.Equal(ModeKeeper.Verdict.Reapply, ModeKeeper.Decide(s), "our own HDR switch always resets the mode: set again regardless");

            // Given up: only something new brings it back, not the scan asking every second once the minute is over.
            s = Held(); s.Current = M(2560, 1440, 240); s.Retries = 0; s.GaveUp = true; s.How = ModeKeeper.How.Scan; s.SinceChangeMs = 90000;
            Run.Equal(ModeKeeper.Verdict.GiveUp, ModeKeeper.Decide(s), "given up, and only the scan asks");
            s.How = ModeKeeper.How.Retry;
            Run.Equal(ModeKeeper.Verdict.GiveUp, ModeKeeper.Decide(s), "nor a retry");
            foreach (var how in new[] { ModeKeeper.How.DisplayChange, ModeKeeper.How.Resume, ModeKeeper.How.Session, ModeKeeper.How.GameStart, ModeKeeper.How.HdrVerify })
            {
                s.How = how;
                Run.Equal(ModeKeeper.Verdict.Reapply, ModeKeeper.Decide(s), "given up, the minute is over and something new happened: " + how);
                s.Retries = 3;
                Run.Equal(ModeKeeper.Verdict.GiveUp, ModeKeeper.Decide(s), "but never past the budget: " + how);
                s.Retries = 0;
            }

            // The budget's window.
            var at = new List<long> { 1000, 30000, 59000 };
            Run.Equal(3, ModeKeeper.Recent(at, 60000), "three in the last minute");
            Run.Equal(2, ModeKeeper.Recent(at, 61000), "the oldest is over a minute ago");
            Run.Equal(0, ModeKeeper.Recent(at, 200000), "all long ago");
            Run.Equal(0, at.Count, "and forgotten");
            at = new List<long> { 5000 };
            Run.Equal(0, ModeKeeper.Recent(at, 4000), "a time from the future (the clock started again) doesn't count");
        }

        public static void KeepModeWaitsOutABurst()
        {
            var s = Held(); s.Current = M(2560, 1440, 240); s.How = ModeKeeper.How.Scan; s.SinceChangeMs = 0;
            Run.Equal(ModeKeeper.Verdict.Wait, ModeKeeper.Decide(s), "the display changed this instant: the scan leaves it to the display-change check");
            s.SinceChangeMs = ModeKeeper.QuietMs - 1;
            Run.Equal(ModeKeeper.Verdict.Wait, ModeKeeper.Decide(s), "still within the quiet time");
            s.SinceChangeMs = ModeKeeper.QuietMs;
            Run.Equal(ModeKeeper.Verdict.Reapply, ModeKeeper.Decide(s), "quiet for long enough");
            s.SinceChangeMs = -1;
            Run.Equal(ModeKeeper.Verdict.Reapply, ModeKeeper.Decide(s), "no display change was ever reported (a reset without a word from Windows)");
            s = Held(); s.Current = M(2560, 1440, 240); s.How = ModeKeeper.How.DisplayChange; s.SinceChangeMs = 10;
            Run.Equal(ModeKeeper.Verdict.Reapply, ModeKeeper.Decide(s), "the display-change check has done its own waiting");
            s = Held(); s.How = ModeKeeper.How.Scan; s.SinceChangeMs = 10; s.Current = M(1280, 960, 240);
            Run.Equal(ModeKeeper.Verdict.Wait, ModeKeeper.Decide(s), "mid-burst nothing is called the game's own mode either");
            s = Held(); s.Current = null;
            Run.Equal(ModeKeeper.Verdict.Wait, ModeKeeper.Decide(s), "Windows gives no mode (the monitor is off): asked again later");
            s.Retries = 3;
            Run.Equal(ModeKeeper.Verdict.Wait, ModeKeeper.Decide(s), "nothing to give up on while the display is off");
        }

        /// <summary>Every verdict has words for the log, and none of them throws on what Decide can be given.</summary>
        public static void KeepModeLogText()
        {
            Run.Equal("none (no mode of ours)", ModeKeeper.Text(ModeKeeper.Verdict.None, null), "nothing seen");
            var s = Held(); s.Current = M(2560, 1440, 240);
            Run.Equal("reapply (Windows or the driver reset it to the saved mode)", ModeKeeper.Text(ModeKeeper.Decide(s), s), "a reset");
            s.How = ModeKeeper.How.OwnSwitch;
            Run.Equal("reapply (our own HDR switch reset the mode)", ModeKeeper.Text(ModeKeeper.Decide(s), s), "our own HDR switch");
            s = Held(); s.Current = M(2560, 1440, 240); s.Retries = 3;
            Run.Equal("give-up (re-applied 3 times in the last 60 s: not re-applied)", ModeKeeper.Text(ModeKeeper.Decide(s), s), "the budget");
            s = HeldStretched(); s.Stretched = false;
            Run.Equal("reapply (our mode is set but no longer stretched over its area)", ModeKeeper.Text(ModeKeeper.Decide(s), s), "a lost stretch");
            s = Held(); s.Current = null;
            Run.True(ModeKeeper.Text(ModeKeeper.Decide(s), s).StartsWith("wait ("), "the display off");
            Run.True(ModeKeeper.Text(ModeKeeper.Decide(Held()), Held()).StartsWith("keep ("), "still ours");
            s = Held(); s.Current = M(1280, 960, 240);
            Run.True(ModeKeeper.Text(ModeKeeper.Decide(s), s).StartsWith("game-own-mode ("), "the game's own mode");
            s.Saved = M(1920, 1080, 240);
            Run.True(ModeKeeper.Text(ModeKeeper.Decide(s), s).StartsWith("user-changed ("), "display settings changed");
        }

        /// <summary>After a crash: never go "back" to our own temporary mode because it happened to be saved.</summary>
        public static void RecoveredBackMode()
        {
            string why;
            var back = ModeKeeper.RecoveredBack(Comp, M(2560, 1440, 240), null, Pic(2368, 1332, 2), out why);
            Run.True(Native.Same(back), "normally the saved mode");
            back = ModeKeeper.RecoveredBack(Comp, M(2560, 1440, 240), M(1920, 1080, 60), Pic(2368, 1332, 2), out why);
            Run.True(Native.Same(back), "the saved mode wins over an older record: the user may have changed it since");
            back = ModeKeeper.RecoveredBack(Comp, M(2368, 1332, 240), M(2560, 1440, 240), Pic(2368, 1332, 2), out why);
            Run.True(Native.Same(back), "our own mode is what is saved: the mode the state file recorded");
            Run.True(why.Contains("resolution.state"), "and the log says where it came from");
            back = ModeKeeper.RecoveredBack(Comp, M(2368, 1332, 240), null, Pic(2368, 1332, 2), out why);
            Run.True(Native.Same(back), "nothing recorded (an older file): the size of the signal to the panel, at our rate");
            back = ModeKeeper.RecoveredBack(Comp, M(2368, 1332, 240), M(2368, 1332, 240), Pic(2368, 1332, 2), out why);
            Run.True(Native.Same(back), "a record that is our own mode is no better than none");
            back = ModeKeeper.RecoveredBack(Comp, M(2368, 1332, 240), null, null, out why);
            Run.True(Comp.Same(back), "nothing else known: the saved mode as it is");
            back = ModeKeeper.RecoveredBack(Comp, M(2368, 1332, 240), null, new Resolution.Picture { SourceW = 2368, SourceH = 1332, TargetW = 2368, TargetH = 1332, Scaling = 1 }, out why);
            Run.True(Comp.Same(back), "the panel really gets our size as its signal: no native mode to be had from it");
            Run.True(ModeKeeper.RecoveredBack(Comp, null, null, null, out why) == null, "no saved mode at all");
        }

        /// <summary>resolution.state carries the mode to go back to; files without it, and older versions reading it, still work.</summary>
        public static void ModeStateBackMode()
        {
            var m = new TrayApp.ModeState { Gdi = @"\\.\DISPLAY1", Id = PathA, Width = 2368, Height = 1332, Hz = 240, ScalingBack = 2, BackWidth = 2560, BackHeight = 1440, BackHz = 240 };
            string text = TrayApp.FormatModeState(m);
            Run.Equal(@"\\.\DISPLAY1|2368|1332|240|2|0|0|" + PathA + "|2560|1440|240", text, "the mode to go back to after the Id");
            var r = TrayApp.ParseModeState(text);
            Run.True(r != null && Native.Same(r.Back()), "read back");
            Run.Equal(PathA, r.Id, "the Id is still the eighth field");
            m.Id = null;
            text = TrayApp.FormatModeState(m);
            Run.Equal(@"\\.\DISPLAY1|2368|1332|240|2|0|0||2560|1440|240", text, "no Id: its field stays, empty");
            r = TrayApp.ParseModeState(text);
            Run.True(r != null && r.Id == null && Native.Same(r.Back()), "read back without an Id");
            Run.True(TrayApp.ParseModeState(@"\\.\DISPLAY1|2368|1332|240|2|0|0|" + PathA).Back() == null, "a file from before: none recorded");
            Run.True(TrayApp.ParseModeState(@"\\.\DISPLAY1|2368|1332|240").Back() == null, "the oldest format");
            r = TrayApp.ParseModeState(@"\\.\DISPLAY1|2368|1332|240|2|0|0|" + PathA + "|2560|x|240");
            Run.True(r != null && r.Back() == null && r.Width == 2368, "a broken mode to go back to costs only itself");
            r = TrayApp.ParseModeState(@"\\.\DISPLAY1|2368|1332|240|2|0|0|" + PathA + "|2560|1440");
            Run.True(r != null && r.Back() == null, "half of one is none");
            Run.True(TrayApp.FormatModeState(TrayApp.ModeState.ScalingOnly(@"\\.\DISPLAY2", 5, PathB)).EndsWith("|" + PathB), "a scaling-only record has none");
        }
    }
}
