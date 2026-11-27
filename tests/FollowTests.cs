// Checks for Follow.cs: which display a game's competitive resolution goes on and when it follows the game's window,
// how often the process list is looked at, how old a process is when its start time can't be read, when "restart
// the game" is said, which game ends Steam's pre-launch wait, and the budget for HDR switches Windows refuses.

using System;
using System.Collections.Generic;
using System.Linq;

namespace HdrSwitch.Tests
{
    static partial class Checks
    {
        // ---- the right screen, faster detection ----

        const string D1 = @"\\.\DISPLAY1", D2 = @"\\.\DISPLAY2", D3 = @"\\.\DISPLAY3";
        static readonly string[] TwoScreens = { D1, D2 };

        public static void TargetDisplayIsTheGamesScreen()
        {
            string why;
            Run.Equal(D2, Follow.TargetDisplay(D2, D1, TwoScreens, out why), "the screen the game was on last time");
            Run.Equal("games-screen(remembered)", why, "why: remembered");
            Run.Equal(D1, Follow.TargetDisplay(D1, D1, TwoScreens, out why), "remembered, and it is the main one");
            Run.Equal("games-screen(remembered,primary)", why, "why: remembered and primary");
        }

        public static void TargetDisplayFallsBackToTheMainDisplay()
        {
            string why;
            Run.Equal(D1, Follow.TargetDisplay(null, D1, TwoScreens, out why), "never seen: the main display");
            Run.Equal("primary(no-remembered-screen)", why, "why: nothing remembered");
            Run.Equal(D1, Follow.TargetDisplay("", D1, TwoScreens, out why), "an empty memory is none");
            Run.Equal(D1, Follow.TargetDisplay(D3, D1, TwoScreens, out why), "its screen is unplugged: the main display");
            Run.Equal(@"primary(remembered=\\.\DISPLAY3,not-connected)", why, "why: not connected");
            Run.Equal(D2, Follow.TargetDisplay(D3, D2, TwoScreens, out why), "the main display is whichever Windows says, not the first");
            Run.Equal(D1, Follow.TargetDisplay(D2, D1, null, out why), "no list of screens: the main display");
            Run.Equal(D1, Follow.TargetDisplay(D2, D1, new string[0], out why), "no screens: the main display");
        }

        public static void ModeFollowsTheGamesWindow()
        {
            Run.Equal(Follow.Move.Stay, Follow.MoveMode(D1, null, TwoScreens, 5, 0), "no window yet");
            Run.Equal(Follow.Move.Stay, Follow.MoveMode(D1, D1, TwoScreens, 5, 0), "the window is where the mode is");
            Run.Equal(Follow.Move.Stay, Follow.MoveMode(D1, D3, TwoScreens, 5, 0), "a screen that isn't connected");
            Run.Equal(Follow.Move.Stay, Follow.MoveMode(D1, D2, null, 5, 0), "no list of screens");
            Run.Equal(Follow.Move.Wait, Follow.MoveMode(D1, D2, TwoScreens, 1, 0), "on another screen for one look: not yet");
            Run.Equal(Follow.Move.Go, Follow.MoveMode(D1, D2, TwoScreens, 2, 0), "still there at the next look: move");
            Run.Equal(Follow.Move.Go, Follow.MoveMode(D1, D2, TwoScreens, 9, Follow.Moves - 1), "the last move of the budget");
            Run.Equal(Follow.Move.Spent, Follow.MoveMode(D1, D2, TwoScreens, 9, Follow.Moves), "moved three times since the game started: stays");
            Run.Equal(Follow.Move.Go, Follow.MoveMode(null, D2, TwoScreens, 2, 0), "nothing held, tried nowhere it could be: goes to the window");
        }

        public static void ScanIsFastOnlyWhileNoGameRuns()
        {
            Run.Equal(250, Follow.ScanInterval(1, 0, false), "a game on the lists, none running: four looks a second");
            Run.Equal(250, Follow.ScanInterval(40, 0, false), "many games, none running");
            Run.Equal(1000, Follow.ScanInterval(1, 1, false), "a game runs: once a second");
            Run.Equal(1000, Follow.ScanInterval(40, 3, false), "games run");
            Run.Equal(1000, Follow.ScanInterval(0, 0, false), "nothing on the lists: once a second (the scan itself does nothing then)");
            Run.Equal(1000, Follow.ScanInterval(5, 0, true), "paused: nothing would switch, once a second");
            Run.Equal(Follow.FastScanMs, Follow.ScanInterval(1, 0, false), "the fast rate is the constant");
            Run.Equal(Follow.ScanMs, Follow.ScanInterval(1, 2, false), "the slow rate is the constant");
        }

        public static void ScanChoresStayOnceASecond()
        {
            Run.True(Follow.ChoresDue(0, -1), "never done: due");
            Run.True(!Follow.ChoresDue(1250, 1000), "250 ms after the last time: not due");
            Run.True(!Follow.ChoresDue(1750, 1000), "750 ms after: not due");
            Run.True(Follow.ChoresDue(2000, 1000), "a second after: due");
            Run.True(Follow.ChoresDue(1985, 1000), "a tick that came a little early still counts");
            int done = 0;
            long last = -1;
            for (long t = 0; t < 10000; t += 250)
                if (Follow.ChoresDue(t, last)) { done++; last = t; }
            Run.Equal(10, done, "ten seconds of fast looks: ten rounds of chores");
        }

        public static void AgeUsesFirstSeenWhenTheStartTimeIsUnknown()
        {
            Run.Equal(3200L, Follow.AgeMs(3.2, 50000, 49900), "the start time, when Windows gives it");
            Run.Equal(0L, Follow.AgeMs(0, 50000, -1), "started this instant is an age of 0, not unknown");
            Run.Equal(250L, Follow.AgeMs(-1, 50000, 49750), "can't be opened, new since the last look: at most the time since that look");
            Run.Equal(7000L, Follow.AgeMs(-1, 50000, 43000), "can't be opened, first seen 7 s ago (put on the list while running)");
            Run.Equal(0L, Follow.AgeMs(-1, 100, 200), "never negative");
            Run.Equal(-1L, Follow.AgeMs(-1, 50000, -1), "can't be opened and there since the first look: unknown");
            Run.Equal("unknown", Follow.AgeText(-1), "unknown is written as a word");
            Run.Equal("0", Follow.AgeText(0), "0 is a number: just started");
            Run.Equal("1234", Follow.AgeText(1234), "a number otherwise");
            Run.Equal("start-time", Follow.AgeSource(1.5, 100), "source: the start time");
            Run.Equal("first-seen(at-most)", Follow.AgeSource(-1, 100), "source: first seen");
            Run.Equal("none(running-before-the-first-look)", Follow.AgeSource(-1, -1), "source: none");
        }

        public static void LateIsSaidAfterTwoSecondsOrAnUnknownTime()
        {
            Run.True(!Follow.Late(0, false), "just started");
            Run.True(!Follow.Late(250, false), "found by the next fast look");
            Run.True(!Follow.Late(2000, false), "exactly two seconds: not yet");
            Run.True(Follow.Late(2001, false), "more than two seconds");
            Run.True(Follow.Late(3500, false), "3.5 s: was silent under the old 5 s rule");
            Run.True(Follow.Late(-1, false), "unknown age (running before the first look): possibly late");
            Run.True(!Follow.Late(60000, true), "Steam's Play switched before it started: never late");
            Run.True(!Follow.Late(-1, true), "pre-launched and unknown: not late");
            // An elevated game caught as it starts: no start time, but new since the last look.
            Run.True(!Follow.Late(Follow.AgeMs(-1, 10250, 10000), false), "can't be opened, new since the look 250 ms ago: not late");
            Run.True(Follow.Late(Follow.AgeMs(-1, 20000, 10000), false), "can't be opened, on the lists only 10 s after it was first seen: late");
        }

        public static void OnlySteamsGameEndsItsWindow()
        {
            Run.True(Follow.EndsSteamWindow(null, null, "cs2", "Steam: Counter-Strike 2"), "Steam isn't starting anything: nothing to keep");
            Run.True(Follow.EndsSteamWindow("cs2", "Steam: Counter-Strike 2", "cs2", "Steam: Counter-Strike 2"), "the game Steam is starting");
            Run.True(Follow.EndsSteamWindow("cs2", "Steam: Counter-Strike 2", "CS2", null), "the same exe, whatever the case");
            Run.True(!Follow.EndsSteamWindow("cs2", "Steam: Counter-Strike 2", "valorant", "VALORANT"), "another game: the window stays");
            Run.True(!Follow.EndsSteamWindow("cs2", "Steam: Counter-Strike 2", "valorant", null), "another game without a label");
            Run.True(Follow.EndsSteamWindow("game", "Steam: Some Game", "game-win64-shipping", "Steam: Some Game"), "another exe of the same Steam game");
            Run.True(!Follow.EndsSteamWindow("cs2", null, "valorant", null), "no labels: only the name counts");
            Run.True(!Follow.EndsSteamWindow("cs2", "", "valorant", ""), "empty labels don't match each other");
        }

        public static void HdrTriesHaveABudget()
        {
            var t = new Tries(Follow.HdrTries);
            string key = "on|cs2|1:2:3";
            Run.True(t.May(key), "never tried: allowed");
            Run.Equal(1, t.Fail(key), "first refusal");
            Run.True(t.May(key), "one refusal: again");
            Run.Equal(2, t.Fail(key), "second refusal");
            Run.True(t.May(key), "two refusals: once more");
            Run.Equal(3, t.Fail(key), "third refusal");
            Run.True(!t.May(key), "three refusals: no more");
            Run.True(t.May("on|cs2|9:9:9"), "another screen has its own budget");
            Run.True(t.May("on|valorant|1:2:3"), "another game has its own budget");
            Run.True(t.May("ON|CS2|9:9:9") && !t.May("ON|CS2|1:2:3"), "keys ignore case");
            t.Fail("on|cs2x|1:2:3"); t.Fail("on|cs2x|1:2:3"); t.Fail("on|cs2x|1:2:3");
            t.ClearPrefix("on|cs2|");
            Run.True(t.May(key), "the game was started again: its tries come back");
            Run.True(!t.May("on|cs2x|1:2:3"), "… and only its own");
            t.Fail(key);
            t.Clear(key);
            Run.Equal(0, t.Count(key), "a success clears the count");
        }

        public static void HdrTriesKnowWhatIsLeftForTheScan()
        {
            var t = new Tries(3);
            Run.True(!t.AnyLeft("off|"), "nothing failed: nothing for the scan to retry");
            t.Fail("off|1:2:3");
            Run.True(t.AnyLeft("off|"), "one refusal: the scan retries");
            Run.True(!t.AnyLeft("on|"), "other keys aren't its business");
            t.Fail("off|1:2:3"); t.Fail("off|1:2:3");
            Run.True(!t.AnyLeft("off|"), "three refusals: the scan stops");
            t.Fail("off|4:5:6");
            Run.True(t.AnyLeft("off|"), "another display still has tries");
        }

        public static void HdrStaysOursUntilItIsReallyOff()
        {
            Run.Equal(Follow.Off.Done, Follow.AfterOff(true, false, 0), "Windows switched it: no longer ours");
            Run.Equal(Follow.Off.Done, Follow.AfterOff(true, true, 2), "a success is a success, whatever came before");
            Run.Equal(Follow.Off.Retry, Follow.AfterOff(false, true, 1), "refused, HDR still on: ours, tried again");
            Run.Equal(Follow.Off.Retry, Follow.AfterOff(false, true, 2), "refused twice: once more");
            Run.Equal(Follow.Off.Rest, Follow.AfterOff(false, true, Follow.HdrTries), "refused three times: ours, the scan stops");
            Run.Equal(Follow.Off.Gone, Follow.AfterOff(false, false, 0), "refused, but the display is gone or HDR is off: nothing left to own");
        }
    }
}
