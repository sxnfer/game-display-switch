// What to do when the competitive resolution can't be had exactly as the profile asks. Everything that decides is a
// pure function here, checked in tests\FallbackTests.cs; the half of TrayApp below carries the decisions out and
// writes each one to the switch log.
//
// The refresh rate (Fallback.Rate). Windows reports whole Hz, and the same 239.76 Hz shows up as 239 on one size and
// 240 on another, so a size is looked for
//   1. at the wanted rate (the profile's @Hz, or the desktop's current rate),
//   2. at one Hz above or below it – the same rate, rounded the other way (written to the log, no notification),
//   3. without an @Hz in the profile: at the highest rate it has that isn't above the desktop's current one (said),
//   4. otherwise not at all.
//
// The mode (Fallback.Choose), the first that exists:
//   1. the stretched resolution over the area, at the wanted rate (or one Hz off),
//   2. the plain area, 1:1 in the middle, at the wanted rate (or one Hz off) – the stretch is given up before the rate,
//   3. without an @Hz: whichever of the two has the higher rate below the desktop's (the stretch if they are equal),
//   4. nothing: the display stays as it is and a notification says what to create.
//
// NVIDIA's No scaling (Fallback.Took, Fallback.Restore): what was set is read back, and a setting that can't be read is
// never taken for one the user changed.
//
// Notices (Notices): each reason is said once per start of a game, and the latest one stays on the game's page until
// the next switch for that game.

using System;
using System.Collections.Generic;
using System.Linq;

namespace HdrSwitch
{
    static class Fallback
    {
        internal enum Kind
        {
            AsWanted,    // what the profile asks for, at the wanted rate
            NearRate,    // what the profile asks for, one Hz off: the same rate as Windows rounds it for this size
            OtherRate,   // what the profile asks for, at a lower rate
            NoStretch,   // the plain area instead of the stretched resolution (Hz says at which rate)
            Nothing      // neither exists: nothing is switched
        }

        internal sealed class Plan
        {
            public Kind Kind;
            public Resolution.Mode Mode;   // the desktop mode to set – null: nothing
            public Resolution.Mode Area;   // the area Mode is stretched over – null: Mode is shown 1:1
            public string Reason;          // the notice's key: "missing", "no-stretch", "rate" – null: nothing to say
            public string Message;         // what the user is told – null: nothing
            public string Why;             // the choice in the log's words
        }

        /// <summary>The wanted rate if the size has it, else one Hz above or below it, else 0.</summary>
        internal static int Near(int wanted, IEnumerable<int> rates)
        {
            if (rates == null || wanted <= 0) return 0;
            var all = rates as ICollection<int> ?? rates.ToList();
            return all.Contains(wanted) ? wanted : all.Contains(wanted + 1) ? wanted + 1 : all.Contains(wanted - 1) ? wanted - 1 : 0;
        }

        /// <summary>The highest of the rates that isn't above the desktop's, or 0.</summary>
        internal static int Lower(int desktopHz, IEnumerable<int> rates)
        {
            int best = 0;
            if (rates != null) foreach (int r in rates) if (r > 0 && r <= desktopHz && r > best) best = r;
            return best;
        }

        /// <summary>
        /// The rate to switch a size at: the wanted one, one Hz off it, or – only if the profile names no rate – the
        /// highest the size has up to the desktop's current rate. 0: none.
        /// </summary>
        internal static int Rate(int wanted, bool explicitHz, int desktopHz, IEnumerable<int> rates)
        {
            int near = Near(wanted, rates);
            return near > 0 || explicitHz ? near : Lower(desktopHz, rates);
        }

        /// <summary>
        /// What to switch to. area and stretch give the sizes (stretch null: none wanted); areaRates and stretchRates
        /// the refresh rates at which the display offers each; game is the name used in the notification.
        /// </summary>
        internal static Plan Choose(Resolution.Mode area, Resolution.Mode stretch, int wantedHz, bool explicitHz, int desktopHz,
            IList<int> areaRates, IList<int> stretchRates, string game)
        {
            int aNear = Near(wantedHz, areaRates);
            if (stretch != null)
            {
                int sNear = Near(wantedHz, stretchRates);
                if (sNear > 0) return Stretched(area, stretch, sNear, wantedHz);
                if (aNear > 0)
                    return new Plan { Kind = Kind.NoStretch, Mode = At(area, aNear), Reason = "no-stretch",
                        Message = stretch.Text + " at " + wantedHz + " Hz doesn't exist yet: playing at " + area.Text + " without the stretch.",
                        Why = "no-stretch (no " + Size(stretch) + " at " + wantedHz + " Hz" + Has(stretchRates) + ": the plain area " + Size(area) + " at " + aNear + " Hz, 1:1)" };
                int sLow = explicitHz ? 0 : Lower(desktopHz, stretchRates), aLow = explicitHz ? 0 : Lower(desktopHz, areaRates);
                if (sLow > 0 && sLow >= aLow) return Stretched(area, stretch, sLow, wantedHz);
                if (aLow > 0)
                    return new Plan { Kind = Kind.NoStretch, Mode = At(area, aLow), Reason = "no-stretch",
                        Message = "Neither " + stretch.Text + " nor " + area.Text + " exists at " + wantedHz + " Hz: playing at " + area.Text + " at " + aLow + " Hz without the stretch.",
                        Why = "no-stretch (no " + Size(stretch) + " at " + wantedHz + " Hz" + Has(stretchRates) + ", no " + Size(area) + " at " + wantedHz + " Hz" + Has(areaRates)
                            + ": the plain area at " + aLow + " Hz, the highest it has up to the desktop's " + desktopHz + " Hz)" };
                return new Plan { Kind = Kind.Nothing, Reason = "missing",
                    Message = "No " + stretch.Text + " or " + area.Text + " at " + wantedHz + " Hz for " + game + ". Create it as a custom resolution in NVIDIA Control Panel.",
                    Why = "nothing (no " + Size(stretch) + " at " + wantedHz + " Hz" + Has(stretchRates) + ", no " + Size(area) + " at " + wantedHz + " Hz" + Has(areaRates) + NoLower(explicitHz, desktopHz) + ")" };
            }
            int hz = aNear > 0 || explicitHz ? aNear : Lower(desktopHz, areaRates);
            if (hz == 0)
                return new Plan { Kind = Kind.Nothing, Reason = "missing",
                    Message = "No " + area.Text + " at " + wantedHz + " Hz for " + game + ". Create it as a custom resolution in NVIDIA Control Panel.",
                    Why = "nothing (no " + Size(area) + " at " + wantedHz + " Hz" + Has(areaRates) + NoLower(explicitHz, desktopHz) + ")" };
            var plan = new Plan { Kind = KindOf(hz, wantedHz), Mode = At(area, hz), Why = RateWhy(Size(area), hz, wantedHz) };
            if (plan.Kind == Kind.OtherRate)
            {
                plan.Reason = "rate";
                plan.Message = area.Text + " doesn't exist at " + wantedHz + " Hz: playing at " + hz + " Hz.";
            }
            return plan;
        }

        static Plan Stretched(Resolution.Mode area, Resolution.Mode stretch, int hz, int wantedHz)
        {
            var plan = new Plan { Kind = KindOf(hz, wantedHz), Mode = At(stretch, hz), Area = At(area, hz), Why = RateWhy(Size(stretch) + " stretched over " + Size(area), hz, wantedHz) };
            if (plan.Kind == Kind.OtherRate)
            {
                plan.Reason = "rate";
                plan.Message = stretch.Text + " doesn't exist at " + wantedHz + " Hz: playing at " + hz + " Hz.";
            }
            return plan;
        }

        static Kind KindOf(int hz, int wantedHz) { return hz == wantedHz ? Kind.AsWanted : Math.Abs(hz - wantedHz) == 1 ? Kind.NearRate : Kind.OtherRate; }

        static string RateWhy(string what, int hz, int wantedHz)
        {
            return hz == wantedHz ? "as-wanted (" + what + " at " + hz + " Hz)"
                : Math.Abs(hz - wantedHz) == 1 ? "near-rate (" + what + " at " + hz + " Hz: the wanted " + wantedHz + " Hz as Windows rounds it for this size)"
                : "other-rate (" + what + " at " + hz + " Hz: not at " + wantedHz + " Hz, this is the highest it has up to the desktop's rate)";
        }

        static Resolution.Mode At(Resolution.Mode m, int hz) { return new Resolution.Mode { Width = m.Width, Height = m.Height, Hz = hz }; }
        static string Size(Resolution.Mode m) { return m.Width + "x" + m.Height; }
        static string Has(IList<int> rates) { return rates == null || rates.Count == 0 ? " (at no rate)" : " (only at " + string.Join(",", rates) + " Hz)"; }
        static string NoLower(bool explicitHz, int desktopHz) { return explicitHz ? "; the profile names the rate, so no other is used" : "; none at or below the desktop's " + desktopHz + " Hz"; }

        /// <summary>What to tell the user when a stretch that exists couldn't be had and the plain area is used instead.</summary>
        internal static string StretchFailedText(Resolution.Mode stretch, Resolution.Mode area)
        {
            return stretch.Text + " couldn't be stretched over " + area.Text + " (the graphics driver didn't take it): playing at " + area.Text + " without the stretch.";
        }

        // ---- NVIDIA's No scaling ----

        internal enum Take
        {
            None,
            Unreadable,       // no NVIDIA driver, another GPU's display, or an error: nothing to do, nothing to say
            AlreadyCentres,   // it already keeps a smaller mode in the middle
            NotAtSaved,       // the desktop wasn't at its saved mode: tried again when our mode is next set from the saved one
            Refused,          // the driver refused it
            NotStuck,         // the driver took it, and reads back something that doesn't centre
            Unverified,       // the driver took it, and it can't be read back: taken for set
            Taken
        }

        /// <summary>How setting No scaling went: what the driver answered, and what the setting reads back as.</summary>
        internal static Take Took(bool set, int after)
        {
            if (after >= 0) return Nvidia.Centres(after) ? Take.Taken : set ? Take.NotStuck : Take.Refused;
            return set ? Take.Unverified : Take.Refused;
        }

        /// <summary>This app changed the setting and has one to put back.</summary>
        internal static bool Changed(Take t) { return t == Take.Taken || t == Take.Unverified; }

        /// <summary>What to tell the user when No scaling isn't set, or null: it is, or there is no NVIDIA driver to set it.</summary>
        internal static string ScalingHint(Take t)
        {
            const string What = "NVIDIA's scaling couldn't be set to No scaling: a game in exclusive fullscreen may fill the screen instead of staying in the middle. ";
            switch (t)
            {
                case Take.NotAtSaved: return What + "The desktop wasn't at its normal resolution when the game started; it is tried again when the resolution is next set.";
                case Take.Refused:
                case Take.NotStuck: return What + "In NVIDIA Control Panel › Adjust desktop size and position, choose No scaling, or play in borderless mode.";
                default: return null;
            }
        }

        internal enum Restore
        {
            Nothing,       // this app didn't change it
            PutBack,       // still the No scaling this app set, and the desktop is at its saved mode: put back now
            Defer,         // still ours, but the desktop isn't at its saved mode: kept for the next opportunity
            Unreadable,    // can't be read right now: kept (in resolution.state too) for the next opportunity
            UserChanged    // reads as something else: the user changed it, theirs is left alone and ours forgotten
        }

        /// <summary>
        /// What to do with the scaling setting this app has to put back (back; below 0: none), given what the driver says
        /// now (below 0: unreadable). Only a readable, different value means the user changed it.
        /// </summary>
        internal static Restore RestoreScaling(int now, int back, bool atSaved)
        {
            if (back < 0) return Restore.Nothing;
            if (now < 0) return Restore.Unreadable;
            if (now != Nvidia.NoScaling) return Restore.UserChanged;
            return atSaved ? Restore.PutBack : Restore.Defer;
        }
    }

    /// <summary>
    /// The notices about a game's resolution: each reason is said once per start of the game, and the latest one is kept
    /// for the game's page in the flyout until the next switch for that game.
    /// </summary>
    sealed class Notices
    {
        readonly HashSet<string> told = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, string> latest = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Keeps the text as the game's latest. True if this reason hasn't been said since the game was started: notify.</summary>
        public bool Tell(string game, string reason, string text)
        {
            latest[game ?? ""] = text;
            return told.Add((game ?? "") + "/" + reason);
        }

        /// <summary>The game was started: every reason is said again if it comes up again. Its latest notice stays.</summary>
        public void Launch(string game)
        {
            string prefix = (game ?? "") + "/";
            told.RemoveWhere(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>A new switch for the game begins: what was wrong last time is only kept if it comes up again.</summary>
        public void Clear(string game) { latest.Remove(game ?? ""); }

        /// <summary>The game's profile changed: as if nothing had been said.</summary>
        public void Forget(string game)
        {
            Launch(game);
            Clear(game);
        }

        /// <summary>The latest notice about the game, or null.</summary>
        public string Latest(string game)
        {
            string text;
            return latest.TryGetValue(game ?? "", out text) ? text : null;
        }

        /// <summary>
        /// Whether a process of the game showing up is a new start of it: not when Steam's Play already counted as the
        /// start (the switch and its notices came then), and not a second process of a game that is running.
        /// </summary>
        public static bool NewLaunch(bool steamStartedIt, int othersRunning) { return !steamStartedIt && othersRunning <= 0; }
    }

    sealed partial class TrayApp
    {
        readonly Notices notices = new Notices();
        readonly System.Windows.Forms.Timer stretchVerify = new System.Windows.Forms.Timer { Interval = 500 };   // a stretch that didn't show right after it was set: looked at once more
        Fallback.Take lastTake;    // how the last TakeScaling went
        string prelaunched;        // the game Steam is starting: its process showing up isn't another start
        long prelaunchedAt;

        /// <summary>
        /// Something is wrong with a game's resolution: a notification the first time this reason comes up since the game
        /// was started, and the text kept for the game's page either way.
        /// </summary>
        void Problem(string game, string reason, string text)
        {
            if (text == null) return;
            if (notices.Tell(game, reason, text)) Hint(text);
            else SwitchLog.Write("hint-kept", "game", game, "reason", reason, "text", text, SwitchLog.Then, "not notified again (said once since this game was started): kept on the game's page");
        }

        /// <summary>The latest notice about this game's resolution, for its page. Null: none.</summary>
        internal string ProblemOf(string game) { return notices.Latest(game); }

        /// <summary>A game's process appeared, or Steam is starting it: a new start gets every notice again.</summary>
        void NoteLaunch(string name, bool fromSteam)
        {
            long now = keepClock.ElapsedMilliseconds;
            bool steamStartedIt = !fromSteam && prelaunched != null && string.Equals(prelaunched, name, StringComparison.OrdinalIgnoreCase) && now - prelaunchedAt < 90000;
            int others = running.Values.Count(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)) - (fromSteam ? 0 : 1);   // Track has added this one already
            bool fresh = Notices.NewLaunch(steamStartedIt, others);
            if (fresh) notices.Launch(name);
            SwitchLog.Write("launch", "game", name, "from", fromSteam ? "steam" : "process", "steamStartedIt", steamStartedIt, "othersRunning", others, SwitchLog.Then,
                fresh ? "a new start: its notices are shown again if they come up" : "not a new start: notices already shown stay quiet");
            if (fromSteam) { prelaunched = name; prelaunchedAt = now; }
            else if (steamStartedIt) prelaunched = null;
        }

        /// <summary>After TakeScaling: say so if No scaling isn't set (and there is an NVIDIA driver to set it).</summary>
        void ScalingProblem(string game)
        {
            Problem(game, "scaling", Fallback.ScalingHint(lastTake));
        }

        /// <summary>
        /// Gives up a mode's stretch: the desktop goes to the plain area, 1:1 in the middle, at the same rate (or one Hz
        /// off it). True if Windows took it; the mode is then a plain one. False: nothing was changed in it.
        /// </summary>
        bool FallBackToArea(ActiveMode a)
        {
            if (a.Area == null) return false;
            var rates = Resolution.Rates(a.Gdi, a.Area.Width, a.Area.Height);
            int hz = Fallback.Near(a.Mode.Hz, rates);
            if (hz == 0)
            {
                SwitchLog.Write("fallback", "game", a.Game, "display", a.Gdi, "area", a.Area, "areaRates", rates.Count == 0 ? "none" : string.Join(",", rates), SwitchLog.Then,
                    "the plain area doesn't exist at " + a.Mode.Hz + " Hz: not switched to");
                return false;
            }
            bool ok = Resolution.Apply(a.Gdi, a.Area.Width, a.Area.Height, hz);
            SwitchLog.Write("fallback", "game", a.Game, "display", a.Gdi, "from", OursText(a), "to", a.Area.Width + "x" + a.Area.Height + "@" + hz, "cds", SwitchLog.CdsName(Resolution.LastCds),
                "now", Resolution.Current(a.Gdi), SwitchLog.Then, ok ? "the plain area is set, 1:1, without the stretch" : "FAILED (Windows refused the plain area too)");
            if (!ok) return false;
            a.FellBack = a.Mode;
            a.Mode = new Resolution.Mode { Width = a.Area.Width, Height = a.Area.Height, Hz = hz };
            a.Area = null;
            a.Unverified = false;
            return true;
        }

        /// <summary>
        /// 500 ms after a stretched mode was set and didn't show as stretched: looked at again. Still not: set once more,
        /// through the same decision as every lost mode (CheckMode); and if that doesn't stretch it either, the plain
        /// area is used and the user told.
        /// </summary>
        void VerifyStretch()
        {
            stretchVerify.Stop();
            var r = res;
            if (r == null || r.Area == null || !r.Unverified)
            {
                SwitchLog.Write("stretch-verify", "ours", OursText(r), SwitchLog.Then, "nothing to verify any more (the mode was put back, replaced or let go of meanwhile)");
                return;
            }
            r.Unverified = false;
            var stretch = r.Mode;
            var area = r.Area;
            if (InOurMode())
            {
                SwitchLog.Write("stretch-verify", "game", r.Game, "display", r.Gdi, "ours", OursText(r), "picture", Resolution.Read(r.Gdi), "stretched", true, SwitchLog.Then, "stretched over the area now: nothing to do");
                return;
            }
            SwitchLog.Write("stretch-verify", "game", r.Game, "display", r.Gdi, "ours", OursText(r), "now", Resolution.Current(r.Gdi), "picture", Resolution.Read(r.Gdi), "stretched", false, SwitchLog.Then,
                "still not stretched over the area 500 ms after the switch: deciding like any lost mode, once");
            var v = CheckMode(ModeKeeper.How.StretchVerify);
            if (res != r || r.Area == null || (v != ModeKeeper.Verdict.Reapply && v != ModeKeeper.Verdict.GiveUp))
            {
                SwitchLog.Write("stretch-verify", "game", r.Game, "decided", v, "ours", OursText(res), SwitchLog.Then, "left at that decision (not a stretch the driver failed to carry out)");
                UpdateTray();
                return;
            }
            if (InOurMode())
            {
                SwitchLog.Write("stretch-verify", "game", r.Game, "display", r.Gdi, "ours", OursText(r), "picture", Resolution.Read(r.Gdi), "stretched", true, SwitchLog.Then, "stretched over the area after setting it once more");
                UpdateTray();
                return;
            }
            SwitchLog.Write("stretch-verify", "game", r.Game, "display", r.Gdi, "ours", OursText(r), "now", Resolution.Current(r.Gdi), "picture", Resolution.Read(r.Gdi), "stretched", false,
                "trace", Resolution.LastStretch, SwitchLog.Then, "NOT stretched after setting it once more: falling back to the plain area");
            if (FallBackToArea(r))
            {
                r.GaveUp = false;
                SaveState();
                CheckCentred(r, r.Game, null);
                Problem(r.Game, "stretch-failed", Fallback.StretchFailedText(stretch, area));
            }
            else
            {
                Problem(r.Game, "refused", "Couldn't switch to " + r.Text + " at " + r.Mode.Hz + " Hz for " + DisplayName(r.Game) + ", nor to " + area.Text + " without the stretch.");
                RevertResolution();
            }
            UpdateTray();
        }

        /// <summary>
        /// A 1:1 mode was just set: the panel should still get its own signal, with the smaller desktop centred in it.
        /// Remembers whether it is (only a mode that was centred can lose that later, see ModeKeeper.cs) and says so if not.
        /// before: the display path before the switch, or null if it isn't known.
        /// </summary>
        void CheckCentred(ActiveMode active, string name, Resolution.Picture before)
        {
            var after = Resolution.Read(active.Gdi);
            active.Centred = Centred(after);
            bool resized = before != null && after != null && (after.TargetW != before.TargetW || after.TargetH != before.TargetH);
            SwitchLog.Write("picture", "game", name, "display", active.Gdi, "before", before, "after", after, "now", Resolution.Current(active.Gdi), "nvidia", SwitchLog.NvidiaName(Nvidia.Scaling(active.Gdi)), SwitchLog.Then,
                after == null ? "the display path couldn't be read: not checked"
                : resized ? "the signal to the panel changed size: the monitor is scaling"
                : !after.Centered ? "not centred: the driver stretches it"
                : before == null ? "centred 1:1 (the signal before the switch isn't known: not compared)"
                : "centred 1:1, the panel keeps its signal");
            if (after == null) return;
            if (resized)
                Problem(name, "monitor-scales", "The monitor is scaling " + active.Mode.Text + ". In NVIDIA Control Panel › Adjust desktop size and position, choose No scaling on the GPU.");
            else if (!after.Centered)
                Problem(name, "not-centred", active.Mode.Text + " is being stretched. In NVIDIA Control Panel › Adjust desktop size and position, untick Override the scaling mode.");
        }
    }
}
