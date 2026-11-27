// Keeping the competitive resolution while its game runs. Windows and the driver put the desktop back at the mode
// saved in display settings for reasons of their own – HDR switched, the PC or the monitor waking, a monitor plugged
// in again, a secure-desktop prompt, Alt+Tab out of an exclusive-fullscreen game – and the game then plays on the
// whole panel. ModeKeeper.Decide is the one place that says what to do about a desktop that isn't in our mode: it
// only looks at what it is given, so every rule is checked in tests\KeepModeTests.cs. The half of TrayApp below it
// gathers what it needs from Windows, carries the verdict out and writes each one to the switch log.
//
// The rules, in order (the first that fits decides):
//   1. Nothing of ours, paused, or no game with a resolution runs                        -> None (not judged here:
//      the grace period and RevertResolution deal with a mode nobody needs any more).
//   2. Asked by the once-a-second scan less than QuietMs after Windows last reported a
//      display change, or Windows gives no current mode (the monitor is off)             -> Wait (a burst of changes
//      gets one decision, once it is over).
//   3. The desktop is in our mode, and its picture is as we left it                      -> Keep.
//   4. The desktop is in our mode, but a stretched mode isn't stretched over its area
//      any more, or a 1:1 mode that was centred after we set it no longer is             -> Reapply (see 7).
//   5. The mode saved in display settings is no longer the one it was when we switched
//      (and isn't ours): somebody changed the display settings on purpose                -> UserChanged (ours is let
//      go of: nothing more is switched or put back for it).
//   6. The desktop is in a third mode, neither ours nor the saved one: the game set its
//      own in exclusive fullscreen                                                       -> GameOwnMode (no fight:
//      the game puts its own back after every change from outside).
//   7. The desktop is back at the saved mode: Windows or the driver reset it             -> Reapply – unless that
//      was already done Budget times in the last 60 s, or was given up and nothing new
//      happened since (only the scan or a retry asks)                                    -> GiveUp (said once).
//      A re-apply right after this app's own HDR switch (which always resets the mode) doesn't count.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using Microsoft.Win32;

namespace HdrSwitch
{
    static class ModeKeeper
    {
        internal enum Verdict { None, Wait, Keep, Reapply, GameOwnMode, UserChanged, GiveUp }

        /// <summary>What made us look, where that is known.</summary>
        internal enum How
        {
            DisplayChange,   // Windows reported a display change; asked once the burst was over
            Resume,          // the PC woke up
            Session,         // unlocked, or the console session came back
            Scan,            // the once-a-second scan found the desktop out of our mode
            OwnSwitch,       // right after this app switched HDR, which resets the mode
            HdrVerify,       // a moment after an HDR switch: some drivers reset the mode late
            GameStart,       // a game with a resolution started (or Steam is starting one) while a mode is held
            Retry            // a re-apply that Windows refused, tried once more
        }

        /// <summary>Re-applies allowed per game in any 60 seconds.</summary>
        internal const int Budget = 3;
        internal const int BudgetMs = 60000;
        /// <summary>How long after the last reported display change the scan leaves the deciding to the display-change check.</summary>
        internal const int QuietMs = 1000;

        /// <summary>Everything a decision is made from.</summary>
        internal sealed class Seen
        {
            public Resolution.Mode Want;       // the mode this app set – null: none
            public Resolution.Mode Area;       // the area Want is stretched over – null: Want is shown 1:1
            public Resolution.Mode Back;       // the saved mode when it was set
            public Resolution.Mode Current;    // the desktop now – null: Windows gives none
            public Resolution.Mode Saved;      // the mode saved in display settings now
            public Resolution.Picture Picture; // the display path now – null: not readable
            public bool Stretched;             // a stretched mode: the desktop is stretched over Area right now
            public bool WasCentred;            // a 1:1 mode: it was centred right after this app set it
            public bool GameRunning;           // a game with a resolution runs (or is just starting)
            public bool Paused;
            public int Retries;                // re-applies in the last 60 s
            public bool GaveUp;                // the budget ran out earlier and the desktop hasn't been in our mode since
            public int SinceChangeMs = -1;     // since Windows last reported a display change; -1: not known, or already waited for
            public How How;
        }

        internal static Verdict Decide(Seen s)
        {
            if (s == null || s.Want == null || s.Paused || !s.GameRunning) return Verdict.None;
            if (s.Current == null) return Verdict.Wait;
            if (s.How == How.Scan && s.SinceChangeMs >= 0 && s.SinceChangeMs < QuietMs) return Verdict.Wait;
            bool reset;
            if (s.Want.Same(s.Current))
            {
                reset = s.Area != null ? !s.Stretched : s.WasCentred && s.Picture != null && !s.Picture.Centered;
                if (!reset) return Verdict.Keep;
            }
            else
            {
                if (s.Saved != null && s.Back != null && !s.Saved.Same(s.Back) && !s.Saved.Same(s.Want)) return Verdict.UserChanged;
                if (!s.Current.Same(s.Saved)) return Verdict.GameOwnMode;
            }
            if (s.How == How.OwnSwitch) return Verdict.Reapply;
            if (s.Retries >= Budget) return Verdict.GiveUp;
            if (s.GaveUp && (s.How == How.Scan || s.How == How.Retry)) return Verdict.GiveUp;
            return Verdict.Reapply;
        }

        /// <summary>The times (ms on any steady clock) of the re-applies that still count against the budget.</summary>
        internal static int Recent(List<long> at, long now)
        {
            at.RemoveAll(t => now - t >= BudgetMs || t > now);
            return at.Count;
        }

        /// <summary>
        /// What a mode taken over after a crash goes back to. Normally the saved mode – but if our temporary mode is what got
        /// saved (a Control Panel change mid-game does that), it is the mode resolution.state recorded, or else the
        /// size of the signal the panel still gets (its native mode) at our rate. Null if none of them is known.
        /// </summary>
        internal static Resolution.Mode RecoveredBack(Resolution.Mode ours, Resolution.Mode saved, Resolution.Mode recorded, Resolution.Picture picture, out string why)
        {
            if (saved != null && !saved.Same(ours)) { why = "the saved mode"; return saved; }
            if (recorded != null && !recorded.Same(ours)) { why = "the mode resolution.state recorded (our temporary mode is what is saved in display settings)"; return recorded; }
            if (ours != null && picture != null && picture.TargetW > 0 && picture.TargetH > 0 && (picture.TargetW != ours.Width || picture.TargetH != ours.Height))
            {
                why = "the size of the signal to the panel, its native mode (our temporary mode is what is saved in display settings, and resolution.state recorded no other)";
                return new Resolution.Mode { Width = picture.TargetW, Height = picture.TargetH, Hz = ours.Hz };
            }
            why = saved == null ? "nothing (Windows gave no saved mode)" : "the saved mode, which is our own (no other is known)";
            return saved;
        }

        /// <summary>The verdict in the log's words.</summary>
        internal static string Text(Verdict v, Seen s)
        {
            switch (v)
            {
                case Verdict.None: return s != null && s.Paused ? "none (paused)" : s == null || s.Want == null ? "none (no mode of ours)" : "none (no game with a resolution runs)";
                case Verdict.Wait: return s.Current == null ? "wait (Windows gives no current mode: the display is off or gone)" : "wait (the display changed " + s.SinceChangeMs + " ms ago: decided once that is over)";
                case Verdict.Keep: return "keep (still in our mode)";
                case Verdict.Reapply:
                    return "reapply (" + (s.Want.Same(s.Current) ? (s.Area != null ? "our mode is set but no longer stretched over its area" : "our mode is set but no longer centred")
                        : s.How == How.OwnSwitch ? "our own HDR switch reset the mode" : "Windows or the driver reset it to the saved mode") + ")";
                case Verdict.GameOwnMode: return "game-own-mode (neither ours nor the saved mode: the game set its own, left alone)";
                case Verdict.UserChanged: return "user-changed (the saved mode itself changed: display settings were changed on purpose, our mode is let go of)";
                case Verdict.GiveUp: return "give-up (" + (s.Retries >= Budget ? "re-applied " + s.Retries + " times in the last 60 s" : "given up earlier, and nothing new happened") + ": not re-applied)";
                default: return v.ToString();
            }
        }
    }

    sealed partial class TrayApp
    {
        static readonly System.Diagnostics.Stopwatch keepClock = System.Diagnostics.Stopwatch.StartNew();
        ModeKeeper.How checkHow = ModeKeeper.How.DisplayChange;   // what the pending modeCheck was started for
        long checkSince = -1;      // when the pending modeCheck was first started: a stream of changes can't put it off for ever
        long lastChange = -1;      // when Windows last reported a display change
        long lastCheck = -1;       // when the last decision was made
        ActiveMode leftover;       // a mode of ours that couldn't be put back (Windows refused): still ours if a game wants it again

        /// <summary>Decide in a moment – after ms without another call – rather than now: a burst of changes gets one decision.</summary>
        void CheckSoon(ModeKeeper.How how, int ms)
        {
            long now = keepClock.ElapsedMilliseconds;
            if (modeCheck.Enabled && checkSince >= 0 && now - checkSince > 5000) return;   // already put off for 5 s: let it fire
            if (!modeCheck.Enabled) checkSince = now;
            // A display change waited for is checked as one: it also covers the case that no game needs the mode any more.
            if (!(modeCheck.Enabled && checkHow == ModeKeeper.How.DisplayChange)) checkHow = how;
            modeCheck.Stop();
            modeCheck.Interval = ms;
            modeCheck.Start();
        }

        /// <summary>The pending check fires: what it was started for.</summary>
        ModeKeeper.How TakeCheck()
        {
            var how = checkHow;
            checkHow = ModeKeeper.How.DisplayChange;
            checkSince = -1;
            return how;
        }

        /// <summary>What Decide needs, read from Windows now.</summary>
        ModeKeeper.Seen See(ActiveMode r, ModeKeeper.How how)
        {
            long now = keepClock.ElapsedMilliseconds;
            return new ModeKeeper.Seen
            {
                Want = r.Mode, Area = r.Area, Back = r.Back, Current = Resolution.Current(r.Gdi), Saved = Resolution.Saved(r.Gdi), Picture = Resolution.Read(r.Gdi),
                Stretched = r.Area != null && Resolution.Stretched(r.Gdi, r.Area.Width, r.Area.Height), WasCentred = r.Centred,
                // A game that is just starting counts (Steam's Play comes before its process), and so does our own HDR
                // switch, which is only followed up if the desktop was in our mode just before it.
                GameRunning = ProfiledRunning() || how == ModeKeeper.How.GameStart || how == ModeKeeper.How.OwnSwitch,
                Paused = paused, Retries = ModeKeeper.Recent(r.Reapplied, now), GaveUp = r.GaveUp,
                SinceChangeMs = how == ModeKeeper.How.Scan && lastChange >= 0 ? (int)Math.Min(int.MaxValue, now - lastChange) : -1, How = how
            };
        }

        /// <summary>
        /// Is the desktop still in the mode this app set, and if not, why not and what now? Decides (ModeKeeper.Decide),
        /// writes the decision to the switch log and carries it out. Every path that can find the mode lost ends up here.
        /// </summary>
        ModeKeeper.Verdict CheckMode(ModeKeeper.How how)
        {
            if (res == null) return ModeKeeper.Verdict.None;
            var r = res;
            var s = See(r, how);
            var v = ModeKeeper.Decide(s);
            lastCheck = keepClock.ElapsedMilliseconds;
            r.Last = v;
            r.LastCurrent = s.Current;
            var line = new object[] { "how", how, "game", r.Game.Length == 0 ? "?" : r.Game, "display", r.Gdi, "ours", OursText(r), "current", s.Current, "saved", s.Saved, "back", s.Back,
                "picture", s.Picture, "stretched", r.Area == null ? "-" : s.Stretched ? "yes" : "no", "wasCentred", r.Area != null ? "-" : r.Centred ? "yes" : "no",
                "retries", s.Retries + "/" + ModeKeeper.Budget, "gaveUp", s.GaveUp, "sinceChangeMs", s.SinceChangeMs, SwitchLog.Then, ModeKeeper.Text(v, s) };
            // The scan asks every second while the desktop is out of our mode: the same answer is written once a minute.
            if (how == ModeKeeper.How.Scan && v != ModeKeeper.Verdict.Reapply) SwitchLog.Periodic("mode-check", "mode-check", line);
            else SwitchLog.Write("mode-check", line);
            switch (v)
            {
                case ModeKeeper.Verdict.Keep:
                    r.GaveUp = false;
                    break;
                case ModeKeeper.Verdict.Reapply:
                    Reapply(r, how, s);
                    break;
                case ModeKeeper.Verdict.GameOwnMode:
                    if (resFailed.Add(r.Game + "/own"))
                        Hint(r.Area != null
                            ? DisplayName(r.Game) + " set its own display mode, so " + r.Mode.Text + " is no longer stretched. Switch the game to borderless and restart it."
                            : DisplayName(r.Game) + " set its own display mode (" + s.Current.Text + "), so the screen isn't at " + r.Mode.Text + " while it shows. Display Switch leaves the game's mode alone: set the game to "
                                + r.Mode.Text + " or to borderless for the whole area.");
                    break;
                case ModeKeeper.Verdict.UserChanged:
                    res = null;
                    DeleteState();
                    SwitchLog.Write("mode-let-go", "game", r.Game, "display", r.Gdi, "ours", OursText(r), "saved", s.Saved, "was", s.Back, SwitchLog.Then,
                        "no longer ours: nothing more is switched or put back for it but the NVIDIA scaling");
                    RestoreScaling(r.Gdi, r.ScalingBack);   // the mode isn't ours any more, but the scaling setting still is
                    if (resFailed.Add(r.Game + "/user"))
                        Hint("The display settings were changed while " + DisplayName(r.Game) + " runs, so Display Switch has let go of " + r.Text + ". Restart the game to switch again.");
                    break;
                case ModeKeeper.Verdict.GiveUp:
                    r.GaveUp = true;
                    if (resFailed.Add(r.Game + "/reset"))
                        Hint("Windows keeps resetting the resolution, so " + r.Text + " for " + DisplayName(r.Game) + " isn't set again for now. If the game is in exclusive fullscreen at "
                            + (s.Current != null ? s.Current.Text : "another resolution") + ", set it to " + r.Mode.Text + " or to borderless.");
                    break;
            }
            return v;
        }

        /// <summary>
        /// Sets our mode again, the way it was set the first time: SetMode, after taking NVIDIA's No scaling back if that was
        /// ours and has been reset. Windows refusing it gets one more try a second later, through the same decision.
        /// </summary>
        bool Reapply(ActiveMode r, ModeKeeper.How how, ModeKeeper.Seen s)
        {
            if (how != ModeKeeper.How.OwnSwitch) r.Reapplied.Add(keepClock.ElapsedMilliseconds);   // our own HDR switch always resets the mode: no fight
            string scaling = r.Area == null ? RetakeScaling(r) : "-";
            bool ok = SetMode(r);
            string trace = SetModeTrace(r);
            if (ok && KeepHdr())   // the mode change took HDR down, and putting HDR back resets the mode
            {
                ok = SetMode(r);
                trace += ",hdr-put-back," + SetModeTrace(r);
            }
            bool retry = !ok && how != ModeKeeper.How.Retry;
            SwitchLog.Write("reapply", "how", how, "game", r.Game.Length == 0 ? "?" : r.Game, "display", r.Gdi, "ours", OursText(r), "was", s.Current, "saved", s.Saved, "scaling", scaling, "trace", trace,
                "now", Resolution.Current(r.Gdi), "picture", Resolution.Read(r.Gdi), "stretched", r.Area == null ? "-" : Resolution.Stretched(r.Gdi, r.Area.Width, r.Area.Height) ? "yes" : "no",
                "retries", ModeKeeper.Recent(r.Reapplied, keepClock.ElapsedMilliseconds) + "/" + ModeKeeper.Budget, SwitchLog.Then,
                ok ? "re-applied" : retry ? "re-apply FAILED (Windows refused: " + trace + "): trying once more in 1 s" : "re-apply FAILED again (Windows refused: " + trace + "): left to the next check");
            if (retry) CheckSoon(ModeKeeper.How.Retry, 1000);
            return ok;
        }

        /// <summary>NVIDIA's No scaling, if this app had set it and something reset it: set again before the mode, as at the start.</summary>
        string RetakeScaling(ActiveMode r)
        {
            if (r.ScalingBack < 0) return "not-ours";
            int now = Nvidia.Scaling(r.Gdi);
            if (now < 0) return SwitchLog.NvidiaName(now);
            if (Nvidia.Centres(now)) return "still:" + SwitchLog.NvidiaName(now);
            // As in TakeScaling: only at the saved mode, because the driver saves the current mode with the setting.
            if (!Resolution.AtSaved(r.Gdi)) return "reset-to:" + SwitchLog.NvidiaName(now) + ",not-retaken(not-at-saved-mode)";
            bool set = Nvidia.SetScaling(r.Gdi, Nvidia.NoScaling);
            return "reset-to:" + SwitchLog.NvidiaName(now) + (set ? ",retaken:" : ",retake-FAILED:") + SwitchLog.NvidiaName(Nvidia.Scaling(r.Gdi));
        }

        /// <summary>
        /// The once-a-second scan: while a game with a resolution runs, is the desktop still in our mode? One call to
        /// Windows when it is. Covers a reset that came without a display change being reported, and one that came while
        /// this app wasn't being told (asleep, on the secure desktop).
        /// </summary>
        void ScanMode()
        {
            if (res == null || paused || modeCheck.Enabled || !ProfiledRunning()) return;
            long now = keepClock.ElapsedMilliseconds;
            if (lastCheck >= 0 && now - lastCheck < 1000) return;   // decided a moment ago
            if (InOurMode())
            {
                res.GaveUp = false;
                res.Last = ModeKeeper.Verdict.Keep;
                return;
            }
            // Out of our mode and nothing to do about it (the game's own mode, given up, the display off): asked again
            // only once the mode is another one.
            var current = Resolution.Current(res.Gdi);
            bool settled = res.Last == ModeKeeper.Verdict.GameOwnMode || res.Last == ModeKeeper.Verdict.GiveUp || (res.Last == ModeKeeper.Verdict.Wait && current == null);
            if (settled && (current == null ? res.LastCurrent == null : current.Same(res.LastCurrent))) return;
            if (CheckMode(ModeKeeper.How.Scan) != ModeKeeper.Verdict.Wait) UpdateTray();
        }

        /// <summary>Waking up and unlocking: the display often comes back at the saved mode, sometimes without a word from Windows.</summary>
        void OnPower(PowerModes mode)
        {
            SwitchLog.Write("power", "event", mode.ToString(), ChangeState(), SwitchLog.Then,
                mode != PowerModes.Resume ? "noted" : res == null ? "noted (no mode of ours)" : "checking our mode in 2 s");
            if (mode != PowerModes.Resume || res == null) return;
            res.GaveUp = false;   // a new reason for a reset: the budget alone decides
            CheckSoon(ModeKeeper.How.Resume, 2000);
        }

        void OnSession(SessionSwitchReason reason)
        {
            bool back = reason == SessionSwitchReason.SessionUnlock || reason == SessionSwitchReason.ConsoleConnect;
            SwitchLog.Write("session", "event", reason.ToString(), ChangeState(), SwitchLog.Then, !back ? "noted" : res == null ? "noted (no mode of ours)" : "checking our mode in 1 s");
            if (!back || res == null) return;
            res.GaveUp = false;
            CheckSoon(ModeKeeper.How.Session, 1000);
        }
    }
}
