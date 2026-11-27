// Checks for Fallback.cs: the refresh rate a size is switched at, what is switched to when the stretched resolution or
// the rate isn't there (and what the user is told), the notices said once per start of a game, how setting NVIDIA's
// No scaling went, what to do with the setting to put back – and where a stretch is aimed (Resolution.AimAtNative).

using System;
using System.Collections.Generic;
using System.Linq;

namespace HdrSwitch.Tests
{
    static partial class Checks
    {
        // ---- fallbacks ----

        static List<int> Hz(params int[] rates) { return rates.ToList(); }
        static readonly Resolution.Mode AreaSize = M(2368, 1332, 0), StretchSize = M(1776, 1332, 0);

        static string ModeText(Resolution.Mode m) { return m == null ? "none" : m.Width + "x" + m.Height + "@" + m.Hz; }

        public static void FallbackRateExactThenNear()
        {
            Run.Equal(240, Fallback.Rate(240, true, 240, Hz(60, 144, 240)), "the wanted rate exists");
            Run.Equal(239, Fallback.Rate(240, true, 240, Hz(60, 239)), "one Hz below: the same rate, rounded down");
            Run.Equal(240, Fallback.Rate(239, false, 239, Hz(60, 240)), "one Hz above");
            Run.Equal(144, Fallback.Rate(143, true, 240, Hz(144)), "143 / 144");
            Run.Equal(240, Fallback.Rate(240, false, 240, Hz(239, 240, 241)), "exact wins over its neighbours");
            Run.Equal(241, Fallback.Rate(240, true, 240, Hz(239, 241)), "both neighbours: the upper one");
            Run.Equal(0, Fallback.Rate(240, true, 240, Hz(238, 242)), "two Hz off is another rate");
        }

        public static void FallbackRateLowerOnlyWithoutExplicitHz()
        {
            Run.Equal(144, Fallback.Rate(240, false, 240, Hz(60, 120, 144)), "no @Hz: the highest rate up to the desktop's");
            Run.Equal(0, Fallback.Rate(240, true, 240, Hz(60, 120, 144)), "@240 in the profile: no other rate");
            Run.Equal(60, Fallback.Rate(144, false, 144, Hz(60, 165, 240)), "never above the desktop's rate");
            Run.Equal(0, Fallback.Rate(60, false, 60, Hz(144, 240)), "only higher rates: none");
            Run.Equal(0, Fallback.Rate(240, false, 240, Hz()), "the size doesn't exist at all");
            Run.Equal(0, Fallback.Rate(240, false, 240, null), "no rates known");
            Run.Equal(0, Fallback.Rate(0, false, 0, Hz(60)), "no rate wanted and none on the desktop");
        }

        public static void FallbackPlanPlainArea()
        {
            var p = Fallback.Choose(AreaSize, null, 240, false, 240, Hz(60, 240), null, "Counter-Strike 2");
            Run.Equal(Fallback.Kind.AsWanted, p.Kind, "the area exists at the rate");
            Run.Equal("2368x1332@240", ModeText(p.Mode), "the area at the rate");
            Run.True(p.Area == null && p.Message == null && p.Reason == null, "shown 1:1, nothing to say");

            p = Fallback.Choose(AreaSize, null, 240, true, 240, Hz(60, 239), null, "Counter-Strike 2");
            Run.Equal(Fallback.Kind.NearRate, p.Kind, "239 for 240");
            Run.Equal("2368x1332@239", ModeText(p.Mode), "switched at 239");
            Run.Equal(null, p.Message, "one Hz off is logged, not notified");
            Run.True(p.Why.Contains("239") && p.Why.Contains("240"), "the log names both rates: " + p.Why);

            p = Fallback.Choose(AreaSize, null, 240, false, 240, Hz(60, 144), null, "Counter-Strike 2");
            Run.Equal(Fallback.Kind.OtherRate, p.Kind, "no @Hz and only lower rates");
            Run.Equal("2368x1332@144", ModeText(p.Mode), "the highest up to the desktop's");
            Run.Equal("rate", p.Reason, "its notice key");
            Run.Equal("2368 × 1332 doesn't exist at 240 Hz: playing at 144 Hz.", p.Message, "the user is told the rate");

            p = Fallback.Choose(AreaSize, null, 240, true, 240, Hz(60, 144), null, "Counter-Strike 2");
            Run.Equal(Fallback.Kind.Nothing, p.Kind, "@240 in the profile and no 240");
            Run.True(p.Mode == null, "nothing to switch to");
            Run.Equal("missing", p.Reason, "its notice key");
            Run.Equal("No 2368 × 1332 at 240 Hz for Counter-Strike 2. Create it as a custom resolution in NVIDIA Control Panel.", p.Message, "what to create");
            Run.True(p.Why.Contains("60,144"), "the log says where it does exist: " + p.Why);

            p = Fallback.Choose(AreaSize, null, 60, false, 60, Hz(144, 240), null, "Counter-Strike 2");
            Run.Equal(Fallback.Kind.Nothing, p.Kind, "only above the desktop's rate: nothing");
        }

        public static void FallbackPlanStretch()
        {
            var p = Fallback.Choose(AreaSize, StretchSize, 240, false, 240, Hz(240), Hz(240), "VALORANT");
            Run.Equal(Fallback.Kind.AsWanted, p.Kind, "both exist");
            Run.Equal("1776x1332@240", ModeText(p.Mode), "the desktop goes to the stretched resolution");
            Run.Equal("2368x1332@240", ModeText(p.Area), "stretched over the area");
            Run.Equal(null, p.Message, "nothing to say");

            p = Fallback.Choose(AreaSize, StretchSize, 240, false, 240, Hz(), Hz(240), "VALORANT");
            Run.Equal(Fallback.Kind.AsWanted, p.Kind, "the stretch only needs its own size to exist: the area is a rectangle on the panel");

            p = Fallback.Choose(AreaSize, StretchSize, 240, true, 240, Hz(240), Hz(239), "VALORANT");
            Run.Equal(Fallback.Kind.NearRate, p.Kind, "the stretch at 239 for 240");
            Run.Equal("1776x1332@239", ModeText(p.Mode), "at 239");
            Run.Equal("2368x1332@239", ModeText(p.Area), "the area carries the same rate");
            Run.Equal(null, p.Message, "not notified");
        }

        public static void FallbackPlanStretchMissing()
        {
            var p = Fallback.Choose(AreaSize, StretchSize, 240, false, 240, Hz(60, 240), Hz(60), "VALORANT");
            Run.Equal(Fallback.Kind.NoStretch, p.Kind, "no stretch at the rate, but the area: the plain area");
            Run.Equal("2368x1332@240", ModeText(p.Mode), "the area at the wanted rate, before the stretch at a lower one");
            Run.True(p.Area == null, "shown 1:1");
            Run.Equal("no-stretch", p.Reason, "its notice key");
            Run.Equal("1776 × 1332 at 240 Hz doesn't exist yet: playing at 2368 × 1332 without the stretch.", p.Message, "the user is told which size is missing");

            p = Fallback.Choose(AreaSize, StretchSize, 240, true, 240, Hz(239), Hz(), "VALORANT");
            Run.Equal(Fallback.Kind.NoStretch, p.Kind, "the area one Hz off still beats nothing");
            Run.Equal("2368x1332@239", ModeText(p.Mode), "at 239");

            p = Fallback.Choose(AreaSize, StretchSize, 240, false, 240, Hz(60, 120), Hz(60, 144), "VALORANT");
            Run.Equal(Fallback.Kind.OtherRate, p.Kind, "neither at 240, no @Hz: the one with the higher rate");
            Run.Equal("1776x1332@144", ModeText(p.Mode), "the stretch at 144");
            Run.Equal("2368x1332@144", ModeText(p.Area), "still stretched");
            Run.Equal("1776 × 1332 doesn't exist at 240 Hz: playing at 144 Hz.", p.Message, "told the rate");

            p = Fallback.Choose(AreaSize, StretchSize, 240, false, 240, Hz(60, 144), Hz(60, 144), "VALORANT");
            Run.True(p.Area != null && p.Mode.Hz == 144, "the same lower rate: the stretch is kept");

            p = Fallback.Choose(AreaSize, StretchSize, 240, false, 240, Hz(60, 144), Hz(60), "VALORANT");
            Run.Equal(Fallback.Kind.NoStretch, p.Kind, "the area has the higher rate: the plain area");
            Run.Equal("2368x1332@144", ModeText(p.Mode), "at 144");
            Run.Equal("Neither 1776 × 1332 nor 2368 × 1332 exists at 240 Hz: playing at 2368 × 1332 at 144 Hz without the stretch.", p.Message, "told both");

            p = Fallback.Choose(AreaSize, StretchSize, 240, true, 240, Hz(60, 144), Hz(60, 144), "VALORANT");
            Run.Equal(Fallback.Kind.Nothing, p.Kind, "@240 in the profile: no lower rate, nothing");
            Run.Equal("No 1776 × 1332 or 2368 × 1332 at 240 Hz for VALORANT. Create it as a custom resolution in NVIDIA Control Panel.", p.Message, "what to create");
            Run.Equal("missing", p.Reason, "its notice key");

            p = Fallback.Choose(AreaSize, StretchSize, 240, false, 240, Hz(), null, "VALORANT");
            Run.Equal(Fallback.Kind.Nothing, p.Kind, "neither exists at all");
            Run.True(p.Mode == null && p.Area == null, "nothing to switch to");
        }

        public static void NoticesOncePerLaunch()
        {
            var n = new Notices();
            Run.True(n.Tell("cs2.exe", "missing", "No 2368 × 1332"), "the first time: notify");
            Run.True(!n.Tell("cs2.exe", "missing", "No 2368 × 1332"), "the same reason again in this launch: quiet");
            Run.True(n.Tell("cs2.exe", "refused", "Couldn't switch"), "another reason isn't silenced by the first (they used to share one key)");
            Run.True(n.Tell("valorant.exe", "missing", "No 1776 × 1332"), "another game has its own");
            n.Launch("cs2.exe");
            Run.True(n.Tell("cs2.exe", "missing", "No 2368 × 1332"), "the game was started again: said again");
            Run.True(n.Tell("cs2.exe", "refused", "Couldn't switch"), "every reason of that game");
            Run.True(!n.Tell("valorant.exe", "missing", "No 1776 × 1332"), "the other game wasn't started again: still quiet");
            n.Launch("CS2.EXE");
            Run.True(n.Tell("cs2.exe", "missing", "x"), "names are compared without case");
            n.Launch("cs2");
            Run.True(!n.Tell("cs2.exe", "missing", "x"), "a name that only starts the same is another game");
        }

        public static void NoticesLatestStaysUntilNextSwitch()
        {
            var n = new Notices();
            Run.Equal(null, n.Latest("cs2.exe"), "nothing said yet");
            n.Tell("cs2.exe", "missing", "first");
            n.Tell("cs2.exe", "missing", "second");
            Run.Equal("second", n.Latest("cs2.exe"), "the latest is kept even when it wasn't notified");
            n.Launch("cs2.exe");
            Run.Equal("second", n.Latest("cs2.exe"), "a new start alone doesn't remove it: the switch may fail again");
            n.Clear("cs2.exe");
            Run.Equal(null, n.Latest("cs2.exe"), "a new switch begins: gone, unless it comes up again");
            n.Tell("cs2.exe", "missing", "third");
            Run.Equal("third", n.Latest("cs2.exe"), "and kept again");
            n.Forget("cs2.exe");
            Run.Equal(null, n.Latest("cs2.exe"), "the profile changed: nothing kept");
            Run.True(n.Tell("cs2.exe", "missing", "x"), "and everything is said again");
            Run.Equal(null, n.Latest("valorant.exe"), "another game's page shows nothing of it");
        }

        public static void NoticesNewLaunch()
        {
            Run.True(Notices.NewLaunch(false, 0), "a process appears, nothing of the game ran: a new start");
            Run.True(!Notices.NewLaunch(true, 0), "Steam's Play was the start: its process isn't another");
            Run.True(!Notices.NewLaunch(false, 1), "a second process of a running game");
        }

        public static void GamePageShowsTheGamesProblem()
        {
            var s = Game(true, -1, M(2368, 1332), M(1776, 1332));
            string problem = "1776 × 1332 at 240 Hz doesn't exist yet: playing at 2368 × 1332 without the stretch.";
            Run.Equal(problem, TrayApp.SetupHint(s, null, true, false, 240, problem), "the game's own latest problem, though it doesn't name the game");
            Run.Equal(problem, TrayApp.SetupHint(s, "VALORANT started.", true, true, 240, problem), "before a general notice");
            Run.True(TrayApp.SetupHint(s, null, true, false, 240, null).StartsWith("Create 1776 × 1332"), "none: what to create, as before");
            Run.Equal(null, TrayApp.SetupHint(s, null, true, true, 240, ""), "none and nothing missing: no bar");
        }

        public static void ScalingReadBack()
        {
            Run.Equal(Fallback.Take.Taken, Fallback.Took(true, Nvidia.NoScaling), "set, and it reads No scaling");
            Run.Equal(Fallback.Take.Taken, Fallback.Took(true, 7), "the other setting that centres counts");
            Run.Equal(Fallback.Take.NotStuck, Fallback.Took(true, Nvidia.FullScreen), "the driver said yes, but it still reads Full screen");
            Run.Equal(Fallback.Take.Unverified, Fallback.Took(true, -1), "the driver said yes and it can't be read back: taken for set");
            Run.Equal(Fallback.Take.Refused, Fallback.Took(false, Nvidia.FullScreen), "refused");
            Run.Equal(Fallback.Take.Refused, Fallback.Took(false, -1), "refused and unreadable");
            Run.Equal(Fallback.Take.Taken, Fallback.Took(false, Nvidia.NoScaling), "what it reads decides: it is there, so it is to put back");
            Run.True(Fallback.Changed(Fallback.Take.Taken) && Fallback.Changed(Fallback.Take.Unverified), "these have a setting to put back");
            Run.True(!Fallback.Changed(Fallback.Take.NotStuck) && !Fallback.Changed(Fallback.Take.Refused) && !Fallback.Changed(Fallback.Take.NotAtSaved)
                && !Fallback.Changed(Fallback.Take.AlreadyCentres) && !Fallback.Changed(Fallback.Take.Unreadable), "these don't");
        }

        public static void ScalingHintOnlyWhenItMatters()
        {
            Run.Equal(null, Fallback.ScalingHint(Fallback.Take.Taken), "taken: nothing to say");
            Run.Equal(null, Fallback.ScalingHint(Fallback.Take.Unverified), "taken, unverified: nothing to say");
            Run.Equal(null, Fallback.ScalingHint(Fallback.Take.AlreadyCentres), "it already centres");
            Run.Equal(null, Fallback.ScalingHint(Fallback.Take.Unreadable), "no NVIDIA driver: nothing to say on AMD or Intel");
            foreach (var t in new[] { Fallback.Take.NotStuck, Fallback.Take.Refused, Fallback.Take.NotAtSaved })
                Run.True(Fallback.ScalingHint(t).StartsWith("NVIDIA's scaling couldn't be set to No scaling: a game in exclusive fullscreen may fill the screen"), t + ": said");
            Run.True(Fallback.ScalingHint(Fallback.Take.NotAtSaved).Contains("tried again"), "not at the saved mode: it is tried again");
        }

        public static void RestoreScalingDecision()
        {
            Run.Equal(Fallback.Restore.Nothing, Fallback.RestoreScaling(Nvidia.NoScaling, -1, true), "this app didn't change it");
            Run.Equal(Fallback.Restore.PutBack, Fallback.RestoreScaling(Nvidia.NoScaling, Nvidia.FullScreen, true), "still our No scaling, at the saved mode: put back");
            Run.Equal(Fallback.Restore.Defer, Fallback.RestoreScaling(Nvidia.NoScaling, Nvidia.FullScreen, false), "still ours, not at the saved mode: later");
            Run.Equal(Fallback.Restore.Unreadable, Fallback.RestoreScaling(-1, Nvidia.FullScreen, true), "unreadable is not changed: kept for later");
            Run.Equal(Fallback.Restore.Unreadable, Fallback.RestoreScaling(-1, Nvidia.FullScreen, false), "unreadable, wherever the desktop is");
            Run.Equal(Fallback.Restore.UserChanged, Fallback.RestoreScaling(1, Nvidia.FullScreen, true), "it reads as another setting: the user changed it");
            Run.Equal(Fallback.Restore.UserChanged, Fallback.RestoreScaling(Nvidia.FullScreen, Nvidia.FullScreen, false), "already what it was: nothing of ours left");
            Run.Equal(Fallback.Restore.UserChanged, Fallback.RestoreScaling(7, Nvidia.FullScreen, true), "another centring setting isn't the one this app set");
            Run.Equal(Fallback.Restore.Nothing, Fallback.RestoreScaling(-1, -1, false), "nothing to put back, unreadable or not");
        }

        public static void StretchAimsAtTheNativeMode()
        {
            Run.True(!Resolution.AimAtNative(2560, 1440, 2560, 1440, 240, 240), "the signal is native: nothing to change");
            Run.True(!Resolution.AimAtNative(2560, 1440, 2560, 1440, 60, 240), "native size, whatever rate the preferred mode has");
            Run.True(Resolution.AimAtNative(1920, 1080, 2560, 1440, 240, 240), "the monitor is scaling 1920 × 1080: the area is centred on the native mode");
            Run.True(Resolution.AimAtNative(1776, 1332, 2560, 1440, 240, 239), "one Hz off is the same rate");
            Run.True(!Resolution.AimAtNative(1920, 1080, 2560, 1440, 60, 240), "the preferred mode is at another rate: its timing can't be used, the signal is kept");
            Run.True(!Resolution.AimAtNative(1920, 1080, 0, 0, 0, 240), "Windows gives no preferred mode: the signal is kept");
        }

        public static void StretchFailedSaysWhatIsPlayed()
        {
            Run.Equal("1776 × 1332 couldn't be stretched over 2368 × 1332 (the graphics driver didn't take it): playing at 2368 × 1332 without the stretch.",
                Fallback.StretchFailedText(M(1776, 1332, 240), M(2368, 1332, 240)), "names both sizes");
        }
    }
}
