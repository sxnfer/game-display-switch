// Checks for the parts of Display Switch that are plain logic: parsing the game lists and launcher records, the
// updater's version and checksum handling, the rolling text's character matching and the springs. Nothing here
// touches the display, the registry or the network.
//
// Built and run by test.cmd (and by CI on every push): it prints each check and exits with 1 if any failed.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace HdrSwitch.Tests
{
    static class Run
    {
        static int failed, passed;
        static string current;

        static int Main()
        {
            foreach (var m in typeof(Checks).GetMethods(BindingFlags.Static | BindingFlags.Public).OrderBy(x => x.MetadataToken))
            {
                current = m.Name;
                int before = failed;
                try { m.Invoke(null, null); }
                catch (TargetInvocationException e) { Fail("threw " + e.InnerException.GetType().Name + ": " + e.InnerException.Message); }
                Console.WriteLine((failed == before ? "  ok    " : "  FAIL  ") + m.Name);
            }
            Console.WriteLine();
            Console.WriteLine(passed + " checks passed, " + failed + " failed.");
            return failed == 0 ? 0 : 1;
        }

        public static void Fail(string message)
        {
            failed++;
            Console.WriteLine("        " + current + ": " + message);
        }

        public static void True(bool condition, string what)
        {
            if (condition) passed++;
            else Fail(what);
        }

        public static void Equal<T>(T expected, T actual, string what)
        {
            if (EqualityComparer<T>.Default.Equals(expected, actual)) passed++;
            else Fail(what + ": expected <" + expected + ">, got <" + actual + ">");
        }

        public static void Near(double expected, double actual, double tolerance, string what)
        {
            if (Math.Abs(expected - actual) <= tolerance) passed++;
            else Fail(what + ": expected " + expected + " ± " + tolerance + ", got " + actual);
        }
    }

    static partial class Checks
    {
        // ---- springs and rolling text ----

        /// <summary>Steps until it rests, like the motion driver does, for at most 5 s.</summary>
        static double Settle(Func<double, bool> step)
        {
            double t = 0;
            while (step(1 / 120.0) && t < 5) t += 1 / 120.0;
            return t;
        }

        public static void SpringRestsOnTarget()
        {
            foreach (var s in new[] { Motion.Shape(0, 0.1), Motion.Fade(0), Motion.Control(0), Motion.Thumb(0), Motion.Open(0), Motion.Roll(0) })
            {
                s.To(1);
                double t = Settle(s.Step);
                Run.True(t < 2, "settles within 2 s");
                Run.Equal(1.0, s.Value, "rests exactly on the target");
                Run.True(!s.Moving, "stops moving");
            }
        }

        public static void SpringOvershootStaysSmall()
        {
            var s = Motion.Shape(0, 0.001);
            s.To(1);
            double peak = 0;
            for (int i = 0; i < 600 && s.Step(1 / 240.0); i++) peak = Math.Max(peak, s.Value);
            Run.True(peak > 1 && peak < 1.1, "the island's shape overshoots a little (got " + peak + ")");
        }

        static RollText Roller()
        {
            return new RollText(ch => string.IsNullOrEmpty(ch) ? 0 : ch == "W" ? 20 : 10);
        }

        /// <summary>Every character drawn this frame: (character, alpha).</summary>
        static List<KeyValuePair<string, double>> Drawn(RollText r)
        {
            var list = new List<KeyValuePair<string, double>>();
            r.Draw(0, 0, 20, (ch, x, y, alpha, smear) => list.Add(new KeyValuePair<string, double>(ch, alpha)));
            return list;
        }

        public static void RollSnapShowsText()
        {
            var r = Roller();
            r.Snap("SDR in 3s");
            Run.Equal("SDR in 3s", r.Text, "text");
            Run.Equal(90f, r.Width, "width: nine characters");
            Run.True(!r.Moving, "nothing moves after a snap");
            Run.Equal("SDR in 3s", string.Concat(Drawn(r).Select(d => d.Key)), "draws every character once");
        }

        public static void RollKeepsWhatStaysTheSame()
        {
            var r = Roller();
            r.Snap("SDR in 3s");
            r.Set("SDR in 2s");
            Run.True(r.Moving, "starts moving");
            r.Step(0.05);
            var drawn = Drawn(r);
            // The unchanged characters are drawn at full strength; only the digit has an old and a new copy.
            Run.Equal(10, drawn.Count, "nine characters plus the outgoing digit");
            Run.True(drawn.Where(d => d.Key != "3" && d.Key != "2").All(d => d.Value == 1), "unchanged characters stay put");
            Settle(r.Step);
            Run.True(!r.Moving, "comes to rest");
            Run.Equal("SDR in 2s", string.Concat(Drawn(r).Select(d => d.Key)), "ends on the new text");
        }

        public static void RollWidthFollowsText()
        {
            var r = Roller();
            r.Snap("Downloading");
            r.Set("Verifying");
            Run.Equal(90f, r.TargetWidth, "target width is the new text's");
            Run.True(r.Width > r.TargetWidth, "the current width starts at the old text's");
            Settle(r.Step);
            Run.Near(90, r.Width, 0.2, "width once settled");
        }

        public static void RollSameTextDoesNothing()
        {
            var r = Roller();
            r.Snap("On");
            r.Set("On");
            Run.True(!r.Moving, "the same text again does not roll");
        }

        public static void RollFromEmptySnaps()
        {
            var r = Roller();
            r.Set("Off");
            Run.True(!r.Moving, "the first text just shows");
            Run.Equal("Off", r.Text, "text");
        }

        public static void RollChangeMidRoll()
        {
            var r = Roller();
            r.Snap("3");
            r.Set("2");
            r.Step(0.03);
            r.Set("1");
            Settle(r.Step);
            Run.Equal("1", string.Concat(Drawn(r).Select(d => d.Key)), "a change during a roll ends on the latest text");
        }

        public static void RollSurrogatePairIsOneCharacter()
        {
            var r = Roller();
            r.Snap("a\U0001F3AEb");
            Run.Equal(3, Drawn(r).Count, "a surrogate pair is drawn as one character");
        }

        public static void ColourMix()
        {
            var amber = Theme.Amber;
            Run.Equal(amber.ToArgb(), Theme.Mix(amber, Theme.Grey, 0).ToArgb(), "t = 0 is the first colour");
            Run.Equal(Theme.Grey.ToArgb(), Theme.Mix(amber, Theme.Grey, 1).ToArgb(), "t = 1 is the second");
            Run.Equal(Theme.Grey.ToArgb(), Theme.Mix(amber, Theme.Grey, 1.08).ToArgb(), "a spring's overshoot is clamped");
        }

        // ---- game lists and launcher records ----

        public static void NormalizeExeNames()
        {
            Run.Equal("cyberpunk2077", Proc.Normalize(@"C:\Games\Cyberpunk2077.exe"), "full path");
            Run.Equal("cs2", Proc.Normalize("  \"CS2.EXE\" "), "quotes, spaces and case");
            Run.Equal("game", Proc.Normalize("game"), "no extension");
            Run.Equal<string>(null, Proc.Normalize("   "), "empty");
        }

        public static void ProfileLines()
        {
            var m = TrayApp.ProfileLine.Match("cs2.exe  2368x1332");
            Run.True(m.Success, "plain resolution");
            Run.Equal("2368", m.Groups[2].Value, "width");
            Run.True(!m.Groups[4].Success, "no rate");

            m = TrayApp.ProfileLine.Match("cs2.exe 2368 × 1332 @ 240 Hz");
            Run.True(m.Success, "with × and a rate");
            Run.Equal("240", m.Groups[4].Value, "rate");

            m = TrayApp.ProfileLine.Match("valorant.exe 2368x1332@240 stretch 1776x1332");
            Run.True(m.Success, "with a stretched resolution");
            Run.Equal("1776", m.Groups[5].Value, "stretched width");
            Run.Equal("1332", m.Groups[6].Value, "stretched height");

            Run.True(!TrayApp.ProfileLine.Match("cs2.exe").Success, "no resolution");
        }

        public static void VdfValues()
        {
            string acf = "\"AppState\"\n{\n\t\"appid\"\t\t\"730\"\n\t\"name\"\t\t\"Counter-Strike 2\"\n\t\"installdir\"\t\t\"Counter-Strike Global Offensive\"\n}";
            Run.Equal("Counter-Strike 2", GameScanner.Vdf(acf, "name"), "name");
            Run.Equal("Counter-Strike Global Offensive", GameScanner.Vdf(acf, "installdir"), "installdir");
            Run.Equal<string>(null, GameScanner.Vdf(acf, "missing"), "missing key");
        }

        public static void JsonValues()
        {
            string item = "{ \"DisplayName\": \"Alan Wake 2\", \"InstallLocation\": \"D:\\\\Epic\\\\AlanWake2\", \"Quote\": \"say \\\"hi\\\"\" }";
            Run.Equal("Alan Wake 2", GameScanner.Json(item, "DisplayName"), "plain value");
            Run.Equal(@"D:\Epic\AlanWake2", GameScanner.Json(item, "InstallLocation"), "escaped backslashes");
            Run.Equal("say \"hi\"", GameScanner.Json(item, "Quote"), "escaped quotes");
        }

        public static void RecordersFromFile()
        {
            var r = TrayApp.ParseRecorders(new[] { "# a comment", "", "Medal.exe   # Medal", "C:\\Tools\\Capture.exe", "obs64.exe # OBS (mine)" });
            Run.Equal("Medal", r["medal"], "a line with a name");
            Run.Equal("", r["capture"], "a path without a name");
            Run.Equal("OBS (mine)", r["obs64"], "a built-in one renamed");
            Run.Equal("Streamlabs", r["streamlabs desktop"], "the built-in ones are always there");
            Run.True(!r.ContainsKey("") && !r.ContainsKey("# a comment"), "comments and blank lines are skipped");
        }

        public static void NoBackgroundRecorders()
        {
            // These run all day: on the list they would keep HDR off all day.
            foreach (string n in new[] { "nvidia share", "nvcontainer", "gamebar", "gamebarftserver", "discord", "medal" })
                Run.True(!TrayApp.BuiltInRecorders.ContainsKey(n), n + " is not a built-in recorder");
        }

        public static void InstalledAppLabels()
        {
            Run.Equal("EA: Battlefield 6", GameScanner.InstalledLabel("Electronic Arts", "Battlefield 6"), "an EA app game");
            Run.Equal("Battle.net: Diablo IV", GameScanner.InstalledLabel("Blizzard Entertainment", "Diablo IV"), "a Battle.net game");
            Run.Equal("Riot: VALORANT", GameScanner.InstalledLabel("Riot Games, Inc", "VALORANT"), "a Riot game");
            Run.Equal("Rockstar: Grand Theft Auto V", GameScanner.InstalledLabel("Rockstar Games", "Grand Theft Auto V "), "a Rockstar game");
            foreach (var launcher in new[] { new[] { "Electronic Arts", "EA app" }, new[] { "Blizzard Entertainment", "Battle.net" },
                new[] { "Riot Games, Inc", "Riot Vanguard" }, new[] { "Riot Games, Inc", "Riot Client" }, new[] { "Rockstar Games", "Rockstar Games Launcher" } })
                Run.Equal<string>(null, GameScanner.InstalledLabel(launcher[0], launcher[1]), launcher[1] + " is a launcher, not a game");
            Run.Equal<string>(null, GameScanner.InstalledLabel("Microsoft Corporation", "Microsoft Edge"), "another publisher");
            Run.Equal<string>(null, GameScanner.InstalledLabel(null, "Something"), "no publisher");
        }

        public static void XboxGamingRoot()
        {
            var bytes = new List<byte>(System.Text.Encoding.ASCII.GetBytes("RGBX"));
            bytes.AddRange(BitConverter.GetBytes(2));
            bytes.AddRange(System.Text.Encoding.Unicode.GetBytes("\\XboxGames\0\\Games\\Xbox\0"));
            var roots = GameScanner.GamingRoots(bytes.ToArray());
            Run.Equal(2, roots.Count, "two folders");
            if (roots.Count == 2)
            {
                Run.Equal("\\XboxGames", roots[0], "first folder");
                Run.Equal("\\Games\\Xbox", roots[1], "second folder");
            }
            Run.Equal(0, GameScanner.GamingRoots(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }).Count, "not a .GamingRoot file");
            Run.Equal(0, GameScanner.GamingRoots(null).Count, "no file");
        }

        public static void DelayCycle()
        {
            Run.Equal(10, TrayApp.NextDelay(3), "3 s → 10 s");
            Run.Equal(60, TrayApp.NextDelay(30), "30 s → 60 s");
            Run.Equal(3, TrayApp.NextDelay(60), "60 s goes round to 3 s");
            Run.Equal(10, TrayApp.NextDelay(5), "a value set by hand moves on to the next choice");
            Run.Equal(1, TrayApp.ValidDelay(0), "at least 1 s");
            Run.Equal(600, TrayApp.ValidDelay(100000), "at most 10 minutes");
        }

        public static void BrightnessLines()
        {
            var b = TrayApp.ParseBrightness(new[] { "# note", "Cyberpunk2077.exe  60", "cs2.exe 35%  # Counter-Strike 2", "bad.exe 140", "nolevel.exe", "" });
            Run.Equal(60, b["cyberpunk2077"], "a level");
            Run.Equal(35, b["cs2"], "with % and a note");
            Run.True(!b.ContainsKey("bad"), "above 100 is ignored");
            Run.True(!b.ContainsKey("nolevel"), "a line without a level is ignored");
            Run.Equal(2, b.Count, "nothing else");
        }

        public static void BrightnessLevels()
        {
            Run.Equal(1000, Hdr.LevelOf(0), "0 is 80 nits");
            Run.Equal(6000, Hdr.LevelOf(100), "100 is 480 nits");
            Run.Equal(3000, Hdr.LevelOf(40), "40 is 240 nits");
            Run.Equal(40, Hdr.PercentOf(Hdr.LevelOf(40)), "and back");
            Run.Equal(0, Hdr.PercentOf(500), "below the slider");
            Run.Equal(100, Hdr.LevelOf(250) == 6000 ? 100 : -1, "clamped at 100");
        }

        static Hdr.Display Screen(uint id, bool hdr, bool on)
        {
            return new Hdr.Display { Target = id, Gdi = "\\\\.\\DISPLAY" + id, Supported = hdr, On = on };
        }

        static string Keys(List<Hdr.Display> list) { return string.Join(",", list.Select(d => d.Gdi.Substring(4))); }

        public static void GameScreenMoves()
        {
            // HDR came on on the main screen (1) while the game started; its window is on screen 2.
            var all = new List<Hdr.Display> { Screen(1, true, true), Screen(2, true, false) };
            List<Hdr.Display> on, off;
            TrayApp.PlanScreens(new[] { @"\\.\DISPLAY2" }, all, new[] { all[0].Key }, out on, out off);
            Run.Equal("DISPLAY2", Keys(on), "the game's screen gets HDR");
            Run.Equal("DISPLAY1", Keys(off), "the main screen goes back to SDR");
        }

        public static void GameScreenLeavesOthersAlone()
        {
            var all = new List<Hdr.Display> { Screen(1, true, true), Screen(2, true, false) };
            List<Hdr.Display> on, off;
            TrayApp.PlanScreens(new[] { @"\\.\DISPLAY2" }, all, new string[0], out on, out off);
            Run.Equal("", Keys(off), "HDR this app didn't turn on stays on");

            TrayApp.PlanScreens(new[] { @"\\.\DISPLAY2", null }, all, new[] { all[0].Key }, out on, out off);
            Run.Equal("DISPLAY2", Keys(on), "a game with a window moves");
            Run.Equal("", Keys(off), "but nothing goes off while another game has no window yet");

            TrayApp.PlanScreens(new string[] { null }, all, new[] { all[0].Key }, out on, out off);
            Run.True(on.Count == 0 && off.Count == 0, "no window yet: nothing changes");

            var sdrOnly = new List<Hdr.Display> { Screen(1, true, true), Screen(2, false, false) };
            TrayApp.PlanScreens(new[] { @"\\.\DISPLAY2" }, sdrOnly, new[] { sdrOnly[0].Key }, out on, out off);
            Run.True(on.Count == 0 && off.Count == 0, "a game on a screen without HDR changes nothing");
        }

        public static void TwoGamesTwoScreens()
        {
            var all = new List<Hdr.Display> { Screen(1, true, true), Screen(2, true, false) };
            List<Hdr.Display> on, off;
            TrayApp.PlanScreens(new[] { @"\\.\DISPLAY1", @"\\.\DISPLAY2" }, all, new[] { all[0].Key }, out on, out off);
            Run.Equal("DISPLAY2", Keys(on), "the second screen comes on as well");
            Run.Equal("", Keys(off), "the first stays on for the other game");
        }

        // ---- updates ----

        const string ReleaseJson = "{\"tag_name\":\"v1.7.0\",\"assets\":["
            + "{\"name\":\"HdrSwitch.exe\",\"browser_download_url\":\"https://github.com/sxnfer/game-display-switch/releases/download/v1.7.0/HdrSwitch.exe\"},"
            + "{\"name\":\"SHA256SUMS.txt\",\"browser_download_url\":\"https://github.com/sxnfer/game-display-switch/releases/download/v1.7.0/SHA256SUMS.txt\"}]}";

        public static void UpdateNewerRelease()
        {
            var r = Updater.Parse(ReleaseJson, new Version(1, 6, 1));
            Run.True(r != null, "1.7.0 is newer than 1.6.1");
            if (r == null) return;
            Run.Equal("1.7.0", r.VersionText, "version");
            Run.True(r.ExeUrl.EndsWith("/HdrSwitch.exe"), "exe url");
            Run.True(r.SumsUrl.EndsWith("/SHA256SUMS.txt"), "checksum url");
        }

        public static void UpdateSameOrOlderRelease()
        {
            Run.True(Updater.Parse(ReleaseJson, new Version(1, 7, 0)) == null, "the same version is not an update");
            Run.True(Updater.Parse(ReleaseJson, new Version(1, 10, 0)) == null, "1.7.0 is older than 1.10.0");
        }

        public static void UpdateIgnoresForeignDownloads()
        {
            string json = ReleaseJson.Replace("github.com/sxnfer/game-display-switch", "evil.example.com/x");
            Run.True(Updater.Parse(json, new Version(1, 6, 1)) == null, "files from elsewhere are not accepted");
        }

        public static void UpdateBadTag()
        {
            Run.True(Updater.Parse("{\"tag_name\":\"nightly\",\"assets\":[]}", new Version(1, 0, 0)) == null, "a tag that is no version");
        }

        public static void ChecksumLine()
        {
            string sums = "0123ABCD  HdrSwitch.exe\r\nffff  other.exe\n";
            Run.Equal("0123abcd", Updater.ExpectedHash(sums), "the exe's line, in lower case");
            Run.Equal("beef", Updater.ExpectedHash("beef *HdrSwitch.exe"), "binary-mode marker");
            Run.Equal<string>(null, Updater.ExpectedHash("ffff  other.exe"), "no line for the exe");
        }
    }
}
