// The right screen, and finding a game quickly. The rules live in Follow, which only looks at what it is given, so
// each one is checked in tests\FollowTests.cs. The half of TrayApp below it gathers what they need from Windows,
// carries them out and writes each decision to the switch log.
//
// The rules:
//   - Which display a game's competitive resolution goes on (TargetDisplay): the screen the game was on last time, if
//     it is connected, else the main display. Never "the first display that can do HDR": that is HDR's business.
//   - Moving it (MoveMode): once the game's window has been on another connected screen for two looks in a row, the
//     mode is put back on the old screen and set on the new one. At most Moves times per start of the game.
//   - How often the process list is looked at (ScanInterval): four times a second while a game is on the lists and
//     none is running, so a game not started through Steam is caught sooner; once a second otherwise. Everything else
//     the scan does (windows, the mode, the island) stays once a second (ChoresDue).
//   - How old a process is when it is found (AgeMs): its start time if Windows gives it; else the time since the last
//     look that didn't have it (games that run elevated or behind anti-cheat can't be opened); else unknown.
//   - Whether to say "restart the game" (Late): the switch came more than LateMs after the game started, or nobody
//     knows when it started – unless Steam's Play had switched before the game started.
//   - Which game ends Steam's pre-launch wait (EndsSteamWindow): the one Steam is starting, not any other.
//   - HDR that Windows refuses (Tries, AfterOff): switching on is tried HdrTries times per game and screen, then said
//     once; a display that wouldn't go back to SDR stays this app's and is tried again by the scan, HdrTries times
//     after each attempt that had a reason of its own (a game closing, a recorder, pause, exit).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace HdrSwitch
{
    static class Follow
    {
        internal const int FastScanMs = 250;   // no game runs yet: look for one four times a second
        internal const int ScanMs = 1000;
        internal const int LateMs = 2000;      // a game older than this when the switch came may have missed it
        internal const int HdrTries = 3;
        internal const int StableScans = 2;    // looks in a row a window must be on another screen before the mode follows
        internal const int Moves = 3;          // per start of a game

        /// <summary>
        /// The display a game's competitive resolution goes on: the screen it was on last time if that is connected, else
        /// the main display.
        /// </summary>
        internal static string TargetDisplay(string remembered, string primary, ICollection<string> connected, out string why)
        {
            if (string.IsNullOrEmpty(remembered))
            {
                why = "primary(no-remembered-screen)";
                return primary;
            }
            if (connected != null && connected.Contains(remembered))
            {
                why = remembered == primary ? "games-screen(remembered,primary)" : "games-screen(remembered)";
                return remembered;
            }
            why = "primary(remembered=" + remembered + ",not-connected)";
            return primary;
        }

        internal enum Move
        {
            Stay,    // no window yet, the window is where the mode is, or it is on a screen that isn't connected
            Wait,    // on another screen, but not for long enough
            Go,
            Spent    // it would move, but it already did Moves times since the game started
        }

        /// <summary>Whether a game's mode follows its window from the screen it is held on (or was last tried on) to another.</summary>
        internal static Move MoveMode(string heldOn, string windowOn, ICollection<string> connected, int scansThere, int movesSoFar)
        {
            if (string.IsNullOrEmpty(windowOn) || windowOn == heldOn) return Move.Stay;
            if (connected == null || !connected.Contains(windowOn)) return Move.Stay;
            if (movesSoFar >= Moves) return Move.Spent;
            return scansThere >= StableScans ? Move.Go : Move.Wait;
        }

        /// <summary>Milliseconds between two looks at the process list.</summary>
        internal static int ScanInterval(int gamesOnLists, int gamesRunning, bool paused)
        {
            return gamesOnLists > 0 && gamesRunning <= 0 && !paused ? FastScanMs : ScanMs;
        }

        /// <summary>The once-a-second part of the scan is due (a timer tick may come a little early).</summary>
        internal static bool ChoresDue(long nowMs, long lastMs)
        {
            return lastMs < 0 || nowMs - lastMs >= ScanMs - 50;
        }

        /// <summary>
        /// A process's age in ms. realSeconds: from its start time, below 0 if Windows wouldn't say. absentAtMs: when the
        /// process list was last looked at without it in it, below 0 if it has been there since the first look. Then the
        /// process is at most as old as the time since that look. -1: unknown.
        /// </summary>
        internal static long AgeMs(double realSeconds, long nowMs, long absentAtMs)
        {
            if (realSeconds >= 0) return (long)(realSeconds * 1000);
            if (absentAtMs >= 0) return Math.Max(0, nowMs - absentAtMs);
            return -1;
        }

        /// <summary>Where AgeMs has its answer from, for the log.</summary>
        internal static string AgeSource(double realSeconds, long absentAtMs)
        {
            return realSeconds >= 0 ? "start-time" : absentAtMs >= 0 ? "first-seen(at-most)" : "none(running-before-the-first-look)";
        }

        internal static string AgeText(long ageMs) { return ageMs < 0 ? "unknown" : ageMs.ToString(); }

        /// <summary>
        /// The switch may have come too late for the game to see it: it had been running for more than LateMs, or for
        /// nobody knows how long. Not when Steam's Play had switched before the game started.
        /// </summary>
        internal static bool Late(long ageMs, bool prelaunched)
        {
            return !prelaunched && (ageMs < 0 || ageMs > LateMs);
        }

        /// <summary>
        /// A game was found running while Steam is starting steamGame (null: it isn't starting one). True if that ends the
        /// wait for Steam's game: it is that game, or another exe with the same label.
        /// </summary>
        internal static bool EndsSteamWindow(string steamGame, string steamLabel, string found, string foundLabel)
        {
            if (steamGame == null) return true;
            if (string.Equals(steamGame, found, StringComparison.OrdinalIgnoreCase)) return true;
            return !string.IsNullOrEmpty(steamLabel) && string.Equals(steamLabel, foundLabel, StringComparison.OrdinalIgnoreCase);
        }

        internal enum Off
        {
            Done,    // Windows switched it to SDR: no longer ours
            Gone,    // refused, but the display is gone or its HDR is off already: nothing left to switch, no longer ours
            Retry,   // refused, HDR still on: stays ours, the scan tries again
            Rest     // refused HdrTries times: stays ours, the scan stops; tried again when something next sends it back
        }

        /// <summary>What a try at switching one of our displays back to SDR comes to. failures: refusals so far, this one included.</summary>
        internal static Off AfterOff(bool ok, bool stillOn, int failures)
        {
            if (ok) return Off.Done;
            if (!stillOn) return Off.Gone;
            return failures >= HdrTries ? Off.Rest : Off.Retry;
        }
    }

    /// <summary>Counts refusals per key, against a budget.</summary>
    sealed class Tries
    {
        readonly Dictionary<string, int> failed = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        readonly int budget;

        public Tries(int budget) { this.budget = budget; }

        public int Count(string key)
        {
            int n;
            return failed.TryGetValue(key, out n) ? n : 0;
        }

        /// <summary>Another try is allowed.</summary>
        public bool May(string key) { return Count(key) < budget; }

        /// <summary>A try failed. Returns how many have now.</summary>
        public int Fail(string key)
        {
            int n = Count(key) + 1;
            failed[key] = n;
            return n;
        }

        public void Clear(string key) { failed.Remove(key); }

        public void ClearPrefix(string prefix)
        {
            foreach (string k in failed.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList()) failed.Remove(k);
        }

        /// <summary>Some key with this prefix has failed and may be tried again.</summary>
        public bool AnyLeft(string prefix)
        {
            foreach (var kv in failed)
                if (kv.Value < budget && kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }

    sealed partial class TrayApp
    {
        readonly Tries tries = new Tries(Follow.HdrTries);    // "on|game|display", "off|display": HDR that Windows refused
        readonly Tries moves = new Tries(Follow.Moves);       // game: how often its mode followed it to another screen since it started
        readonly Dictionary<string, string> modeTried = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // game → the display its resolution was last tried on
        readonly Dictionary<string, string> windowOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);    // running game with a resolution → its window's screen, this look
        string followGame, followGdi;   // the game whose window is on another screen than its mode, and that screen
        int followScans;                // … for this many looks in a row
        string steamGame, steamLabel;   // the game Steam is starting, while steamTimeout runs
        long lastChores = -1;
        // The process scan: what it needs is kept between looks, so four looks a second allocate next to nothing.
        readonly List<KeyValuePair<int, string>> scanList = new List<KeyValuePair<int, string>>(512);
        readonly HashSet<int> scanAlive = new HashSet<int>();
        readonly List<int> scanDead = new List<int>();
        readonly Dictionary<int, long> absentAt = new Dictionary<int, long>();   // pid → when the list was last looked at without it (-1: there since the first look)
        long lastScanAt = -1;
        bool inScan;

        static List<string> ScreenNames()
        {
            var names = new List<string>();
            foreach (var s in Screen.AllScreens) names.Add(s.DeviceName);
            return names;
        }

        /// <summary>The display a game's competitive resolution goes on, and why (see Follow.TargetDisplay).</summary>
        string TargetGdiFor(string game, out string why)
        {
            string remembered = null;
            if (game != null) screens.TryGetValue(game, out remembered);
            return Follow.TargetDisplay(remembered, Screen.PrimaryScreen.DeviceName, ScreenNames(), out why);
        }

        /// <summary>The timer's tick: the process list every time, the rest once a second.</summary>
        void ScanTick()
        {
            Rescan();
            long now = keepClock.ElapsedMilliseconds;
            if (Follow.ChoresDue(now, lastChores))
            {
                lastChores = now;
                FollowGameScreens();
                RetrySdr();
                ScanMode();
                island.Poll();
                if (updateExe != null) FinishUpdate();
            }
            SetScanRate();
        }

        void SetScanRate()
        {
            int ms = Follow.ScanInterval(games.Count + profiles.Count, running.Count, paused);
            if (scanTimer.Interval == ms) return;
            SwitchLog.Write("scan-rate", "everyMs", ms, "was", scanTimer.Interval, "gamesOnLists", games.Count + profiles.Count, "running", running.Count, "paused", paused, SwitchLog.Then,
                ms == Follow.FastScanMs ? "no game runs: looking for one four times a second" : "once a second (a game runs, the app is paused, or the lists are empty)");
            scanTimer.Interval = ms;
        }

        /// <summary>When the process list was last looked at without this process in it; -1 if that isn't known.</summary>
        long AbsentAt(int pid)
        {
            long at;
            return absentAt.TryGetValue(pid, out at) ? at : -1;
        }

        /// <summary>
        /// A game was found running: Steam's pre-launch wait ends if it is the game Steam is starting. Any other game
        /// leaves it running, so what was switched for Steam's game goes back if that one never shows up.
        /// </summary>
        void EndSteamWindow(string name)
        {
            if (!steamTimeout.Enabled)
            {
                steamGame = steamLabel = null;
                return;
            }
            if (Follow.EndsSteamWindow(steamGame, steamLabel, name, LabelOf(name)))
            {
                SwitchLog.Write("steam-window", "steamGame", steamGame, "found", name, SwitchLog.Then, "ended (the game Steam was starting is running)");
                steamTimeout.Stop();
                steamGame = steamLabel = null;
            }
            else SwitchLog.Write("steam-window", "steamGame", steamGame, "found", name, SwitchLog.Then, "kept (another game than the one Steam is starting: still waiting for that one, up to 60 s after Play)");
        }

        /// <summary>
        /// One of our displays back to SDR. True if it is no longer ours: switched, or nothing left to switch. False: Windows
        /// refused and HDR is still on – it stays ours. fromScan: a retry by the scan, which has a budget.
        /// </summary>
        bool ToSdr(Hdr.Display d, string why, bool fromScan)
        {
            string key = "off|" + d.Key;
            if (fromScan && !tries.May(key)) return false;
            if (!fromScan) tries.Clear(key);   // something new sends it back: the scan's budget starts again
            bool ok = Hdr.Set(d, false);
            bool stillOn = false;
            if (!ok)
            {
                var live = Hdr.All().FirstOrDefault(x => x.Key == d.Key);
                stillOn = live != null && live.On;
            }
            int n = ok || !stillOn ? 0 : tries.Fail(key);
            var outcome = Follow.AfterOff(ok, stillOn, n);
            if (outcome == Follow.Off.Done || outcome == Follow.Off.Gone) tries.Clear(key);
            SwitchLog.Write("hdr-off", "display", d.Gdi, "why", why, "by", fromScan ? "scan-retry" : "event", "try", ok ? null : n + "/" + Follow.HdrTries, SwitchLog.Then,
                outcome == Follow.Off.Done ? "off"
                : outcome == Follow.Off.Gone ? "refused, but the display is gone or its HDR is off already: nothing left to switch, let go of"
                : outcome == Follow.Off.Retry ? "FAILED (Windows refused, HDR is still on): still this app's, tried again by the scan in a second"
                : "FAILED (Windows refused " + Follow.HdrTries + " times, HDR is still on): still this app's; the scan stops trying, the next game closing, pause or exit tries again");
            return outcome == Follow.Off.Done || outcome == Follow.Off.Gone;
        }

        /// <summary>The scan: a display that wouldn't go back to SDR is tried again, while nothing wants HDR on it.</summary>
        void RetrySdr()
        {
            if (owned.Count == 0 || !tries.AnyLeft("off|")) return;
            bool wanted = !paused && recorder == null && (HdrRunning() || sdrTimer.Enabled || steamTimeout.Enabled);
            if (wanted) return;
            bool ours = InOurMode();
            int before = owned.Count;
            AllToSdr(true);
            if (owned.Count == before) return;
            if (ours) KeepMode();
            UpdateTray();
        }

        /// <summary>
        /// Once a second: the competitive resolution follows its game to the screen the game's window is on – put back on
        /// the old screen (scaling included), set on the new one. A game whose resolution couldn't be set where it was last
        /// tried gets another try on the screen it moved to.
        /// </summary>
        void FollowModeScreen()
        {
            string game = null, gdi = null, held = null;
            if (!paused && res != null && res.Game.Length > 0)
            {
                game = res.Game;
                held = res.Gdi;
                windowOf.TryGetValue(game, out gdi);
            }
            else if (!paused && res == null)
                foreach (var kv in windowOf)
                {
                    string tried;
                    if (kv.Value == null || !modeTried.TryGetValue(kv.Key, out tried) || tried == kv.Value) continue;
                    game = kv.Key;
                    gdi = kv.Value;
                    held = tried;
                    break;
                }
            if (game == null || gdi == null || gdi == held)
            {
                followGame = followGdi = null;
                followScans = 0;
                return;
            }
            if (followGame == game && followGdi == gdi) followScans++;
            else
            {
                followGame = game;
                followGdi = gdi;
                followScans = 1;
            }
            var verdict = Follow.MoveMode(held, gdi, ScreenNames(), followScans, moves.Count(game));
            if (verdict != Follow.Move.Go)
            {
                SwitchLog.Periodic("mode-follow:" + game, "mode-follow", "game", game, "from", held, "windowOn", gdi, "ours", OursText(res), "moves", moves.Count(game) + "/" + Follow.Moves, SwitchLog.Then,
                    verdict == Follow.Move.Wait ? "the game's window is on another screen: the resolution follows if it is still there at the next look"
                    : verdict == Follow.Move.Spent ? "the game's window is on another screen, but the resolution already followed it " + Follow.Moves + " times since the game started: staying"
                    : "staying (that screen isn't connected)");
                return;
            }
            moves.Fail(game);
            followGame = followGdi = null;
            followScans = 0;
            bool had = res != null;
            SwitchLog.Write("mode-follow", "game", game, "from", held, "to", gdi, "ours", OursText(res), "moves", moves.Count(game) + "/" + Follow.Moves, SwitchLog.Then,
                had ? "the game's window is on another screen: putting the resolution back here, then setting it there"
                    : "the game's window is on another screen than the one its resolution was last tried on: trying there");
            if (had) RevertResolution();
            if (leftover != null && leftover.Gdi == held)
            {
                // Windows wouldn't put the old screen back. It stays remembered as ours (see RevertResolution); setting the
                // mode on the other screen now would drop that.
                modeTried[game] = gdi;
                SwitchLog.Write("mode-follow", "game", game, "from", held, "to", gdi, SwitchLog.Then, "NOT moved (the old screen couldn't be put back: it stays remembered as ours, nothing is set on the new one)");
                UpdateTray();
                return;
            }
            bool applied = ApplyResolution(game);   // the window's screen is the remembered one by now: that is where it goes
            SwitchLog.Write("mode-follow", "game", game, "from", held, "to", gdi, "ours", OursText(res), SwitchLog.Then,
                applied ? "moved" : res != null ? "not switched there (see the apply line above)" : "NOT set on the new screen (see the apply line above): no competitive resolution is active");
            if (applied && res != null) AnnounceGame(game, false);
            UpdateTray();
        }
    }
}
