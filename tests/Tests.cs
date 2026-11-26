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
            + "{\"name\":\"SHA256SUMS.txt\",\"browser_download_url\":\"https://github.com/sxnfer/game-display-switch/releases/download/v1.7.0/SHA256SUMS.txt\"},"
            + "{\"name\":\"SHA256SUMS.txt.sig\",\"browser_download_url\":\"https://github.com/sxnfer/game-display-switch/releases/download/v1.7.0/SHA256SUMS.txt.sig\"}]}";

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

        public static void UpdateNeedsSignature()
        {
            int sig = ReleaseJson.IndexOf(",{\"name\":\"SHA256SUMS.txt.sig\"");
            string unsigned = ReleaseJson.Substring(0, sig) + "]}";
            Run.True(Updater.Parse(unsigned, new Version(1, 6, 1)) == null, "a release without a signature is not offered");
            var r = Updater.Parse(ReleaseJson, new Version(1, 6, 1));
            Run.True(r != null && r.SigUrl.EndsWith("/SHA256SUMS.txt.sig"), "the signature's url");
        }

        public static void SignatureChecks()
        {
            using (var key = new System.Security.Cryptography.RSACryptoServiceProvider(2048))
            using (var other = new System.Security.Cryptography.RSACryptoServiceProvider(2048))
            {
                key.PersistKeyInCsp = other.PersistKeyInCsp = false;
                byte[] sums = System.Text.Encoding.ASCII.GetBytes("0123abcd  HdrSwitch.exe\r\n");
                // Signed the way the release workflow signs: RSA, SHA256, PKCS#1 v1.5, base64.
                string sig = Convert.ToBase64String(key.SignData(sums, "SHA256"));
                string pub = key.ToXmlString(false);
                Run.True(Updater.Verified(sums, sig, pub), "a good signature");
                Run.True(Updater.Verified(sums, " " + sig + "\n", pub), "spaces and a line break round it");
                byte[] changed = (byte[])sums.Clone();
                changed[0] = (byte)'f';
                Run.True(!Updater.Verified(changed, sig, pub), "a changed checksum file");
                Run.True(!Updater.Verified(sums, Convert.ToBase64String(other.SignData(sums, "SHA256")), pub), "signed by another key");
                Run.True(!Updater.Verified(sums, "not base64!", pub), "garbage");
                Run.True(!Updater.Verified(sums, "", pub), "empty");
            }
        }

        public static void BuiltInKeyLoads()
        {
            // A signature by some other key fails cleanly – which also shows the built-in key itself loads.
            using (var other = new System.Security.Cryptography.RSACryptoServiceProvider(2048))
            {
                other.PersistKeyInCsp = false;
                byte[] data = { 1, 2, 3 };
                Run.True(!Updater.Verified(data, Convert.ToBase64String(other.SignData(data, "SHA256")), Updater.PublicKeyXml), "rejected, no exception");
                Run.True(Updater.PublicKeyXml.Contains("<Modulus>") && !Updater.PublicKeyXml.Contains("<D>"), "a public key only");
            }
        }

        public static void BuiltInKeyMatchesTheReleaseKey()
        {
            // Signed once with the private update key (openssl dgst -sha256 -sign): proves the key built into the app is
            // the public half of the key the release workflow signs with.
            byte[] text = System.Text.Encoding.ASCII.GetBytes("display-switch update key check\n");
            const string Sig = "EGjpLRkJCk5Ehox8d/P+rHbqAcBm59+/ipSPFF9Ki7jS7dqQfUGnUSOhu+D+sOE2kxj8hZqRz9IG+MHo6CUJd28ooJCtWyoY+HXzanspUewHduZfOkRwpt6G4FV4GgbO3L4B3Q/PC1cJs05FlHH+ENh8CmrNOrBJ0hRXLvzD1j6hYWOUKQGiVjJP5Ea7jkotkEc+5OL6X9XXV3bJaw9NceDUvCbNC/8bGl3qLPrRdUwsSRJXicxHamsCEjB9YadHcJ7LeR62zrxPzQviGksEBVoiIwei5+O65H55NtZLLzxuHMmPYxTQH0nwzXQgi1RC7hd8f+igE7dD8CfVJFbwOAbVi0dWPgbz4jHHWXCEZABEYPZNIkqS+Cm+vx6KAoSDwJeJvHBgmxjx8rpmPY84mhRCC9uCdoV8B+Rhq9CQ7W3TrwZmMGd7yd8vjmWUre0NdFpSPtV4L+O94NkmcRkchG07IJfYVGg0mmGkwamJHMEbtPKR8u8E8zXy9RTYzYtK";
            Run.True(Updater.Verified(text, Sig, Updater.PublicKeyXml), "the release key's signature is accepted");
        }

        public static void ChecksumLine()
        {
            string sums = "0123ABCD  HdrSwitch.exe\r\nffff  other.exe\n";
            Run.Equal("0123abcd", Updater.ExpectedHash(sums), "the exe's line, in lower case");
            Run.Equal("beef", Updater.ExpectedHash("beef *HdrSwitch.exe"), "binary-mode marker");
            Run.Equal<string>(null, Updater.ExpectedHash("ffff  other.exe"), "no line for the exe");
        }

        public static void ChecksumLineWithVersion()
        {
            // The format the release workflow writes since the version went into the signed file: a copy from before
            // that reads it as well – it only looks for the line ending in " HdrSwitch.exe".
            string sums = "# version 1.8.0\r\n0123ABCD  HdrSwitch.exe\r\n";
            Run.Equal("0123abcd", Updater.ExpectedHash(sums), "the exe's line, past the version line");
            Run.Equal("0123abcd", Updater.ExpectedHash("0123abcd  HdrSwitch.exe\n# version 1.8.0\n"), "in either order");
        }

        public static void SignedVersionLine()
        {
            Run.Equal(new Version(1, 8, 0), Updater.SignedVersion("# version 1.8.0\r\nabcd  HdrSwitch.exe\r\n"), "the version line");
            Run.Equal(new Version(1, 8, 0), Updater.SignedVersion("abcd  HdrSwitch.exe\n  #   version \t 1.8.0  \r\n"), "spaces, a tab, CRLF, last");
            Run.Equal(new Version(1, 8, 0), Updater.SignedVersion("# version 1.8\n"), "two parts read as three, as the tag is");
            Run.Equal(new Version(1, 8, 0), Updater.SignedVersion("# built by CI\n# version 1.8.0\n"), "other comments are passed over");
            Run.Equal<Version>(null, Updater.SignedVersion("abcd  HdrSwitch.exe\r\n"), "no version line");
            Run.Equal<Version>(null, Updater.SignedVersion(""), "empty");
            Run.Equal<Version>(null, Updater.SignedVersion(null), "nothing");
            Run.Equal<Version>(null, Updater.SignedVersion("# version\n"), "no number");
            Run.Equal<Version>(null, Updater.SignedVersion("# version one.two\n"), "not a number");
            Run.Equal<Version>(null, Updater.SignedVersion("# version 1.8.0 extra\n"), "something after it");
            Run.Equal<Version>(null, Updater.SignedVersion("# version v1.8.0\n"), "the tag's v is not written");
            Run.Equal<Version>(null, Updater.SignedVersion("# version 1.8.0\n# version 9.0.0\n"), "two versions: neither is believed");
            Run.Equal<Version>(null, Updater.SignedVersion("version 1.8.0\n"), "not a comment line");
            Run.Equal<Version>(null, Updater.SignedVersion("# Version 1.8.0\n"), "spelled differently");
        }

        /// <summary>A release as GitHub lists it, with the three files under the tag.</summary>
        static string ReleaseFor(string tag)
        {
            return ReleaseJson.Replace("v1.7.0", tag);
        }

        /// <summary>Signs sums with key as the release workflow does, and serves the files the way Offer fetches them.</summary>
        static Func<string, byte[]> Serve(System.Security.Cryptography.RSACryptoServiceProvider key, string sums, List<string> fetched)
        {
            byte[] bytes = System.Text.Encoding.ASCII.GetBytes(sums);
            string sig = Convert.ToBase64String(key.SignData(bytes, "SHA256"));
            return url =>
            {
                fetched.Add(url.Substring(url.LastIndexOf('/') + 1));
                if (url.EndsWith("/SHA256SUMS.txt")) return bytes;
                if (url.EndsWith("/SHA256SUMS.txt.sig")) return System.Text.Encoding.ASCII.GetBytes(sig);
                throw new InvalidOperationException("only the checksum file and its signature are fetched before offering: " + url);
            };
        }

        public static void UpdateOfferedOnlyWhenSignedForItsTag()
        {
            string dir = TempDir();   // a release that isn't offered is logged with the reason
            try
            {
                using (var key = new System.Security.Cryptography.RSACryptoServiceProvider(2048))
                using (var other = new System.Security.Cryptography.RSACryptoServiceProvider(2048))
                {
                    key.PersistKeyInCsp = other.PersistKeyInCsp = false;
                    string pub = key.ToXmlString(false);
                    var current = new Version(1, 6, 1);
                    const string Sums = "# version 1.7.0\r\n0123abcd  HdrSwitch.exe\r\n";
                    var fetched = new List<string>();

                    var r = Updater.Offer(ReleaseFor("v1.7.0"), current, Serve(key, Sums, fetched), pub);
                    Run.True(r != null && r.VersionText == "1.7.0", "signed, and built as the tag says: offered");
                    Run.Equal("SHA256SUMS.txt,SHA256SUMS.txt.sig", string.Join(",", fetched), "only the two small files are fetched");

                    Run.True(Updater.Offer(ReleaseFor("v99.0.0"), current, Serve(key, Sums, new List<string>()), pub) == null,
                        "an older signed build under a newer tag is not offered");
                    Run.True(LogText().Contains("Update to 99.0.0 not offered: the release is tagged 99.0.0 but was built as 1.7.0"), "and the log says why");
                    Run.True(Updater.Offer(ReleaseFor("v1.7.1"), current, Serve(key, Sums, new List<string>()), pub) == null,
                        "a tag one step off is not offered");
                    Run.True(Updater.Offer(ReleaseFor("v1.7.0"), new Version(1, 8, 0), Serve(key, Sums, new List<string>()), pub) == null,
                        "a downgrade is not offered");
                    Run.True(Updater.Offer(ReleaseFor("v1.7.0"), current, Serve(key, "0123abcd  HdrSwitch.exe\r\n", new List<string>()), pub) == null,
                        "a checksum file without a version (signed before it carried one) is not offered");
                    Run.True(Updater.Offer(ReleaseFor("v1.7.0"), current, Serve(other, Sums, new List<string>()), pub) == null,
                        "signed by another key: not offered");
                    Run.True(Updater.Offer(ReleaseFor("v1.7.0"), current, Serve(key, "# version 1.7.0\r\n0123abcd  other.exe\r\n", new List<string>()), pub) == null,
                        "no checksum for the exe: not offered");

                    // A signature over different contents: the file was changed after signing.
                    var honest = Serve(key, Sums, new List<string>());
                    Func<string, byte[]> changed = url => url.EndsWith("/SHA256SUMS.txt")
                        ? System.Text.Encoding.ASCII.GetBytes("# version 99.0.0\r\n0123abcd  HdrSwitch.exe\r\n") : honest(url);
                    Run.True(Updater.Offer(ReleaseFor("v99.0.0"), current, changed, pub) == null, "a version changed after signing: not offered");
                    Func<string, byte[]> noSig = url => url.EndsWith(".sig") ? new byte[0] : honest(url);
                    Run.True(Updater.Offer(ReleaseFor("v1.7.0"), current, noSig, pub) == null, "an empty signature: not offered");
                }
            }
            finally { Clean(dir); }
        }

        public static void UpdateInstallChecksAgain()
        {
            using (var key = new System.Security.Cryptography.RSACryptoServiceProvider(2048))
            {
                key.PersistKeyInCsp = false;
                string pub = key.ToXmlString(false);
                byte[] exe = { 0x4d, 0x5a, 1, 2, 3 };
                string hash;
                using (var sha = System.Security.Cryptography.SHA256.Create())
                    hash = BitConverter.ToString(sha.ComputeHash(exe)).Replace("-", "").ToLowerInvariant();
                byte[] sums = System.Text.Encoding.ASCII.GetBytes("# version 1.7.0\r\n" + hash + "  HdrSwitch.exe\r\n");
                string sig = Convert.ToBase64String(key.SignData(sums, "SHA256"));
                var current = new Version(1, 6, 1);
                var r = Updater.Parse(ReleaseJson, current);

                Run.True(Updater.Checked(r, sums, sig, exe, pub, current) == exe, "signed, the tag's version, the exe matches");
                Run.True(Throws(() => Updater.Checked(r, sums, sig, new byte[] { 0x4d, 0x5a, 9 }, pub, current)), "an exe that doesn't match");
                Run.True(Throws(() => Updater.Checked(Updater.Parse(ReleaseFor("v99.0.0"), current), sums, sig, exe, pub, current)), "a tag that isn't the signed version");
                Run.True(Throws(() => Updater.Checked(r, sums, sig, exe, pub, new Version(1, 7, 0))), "not newer than this copy");
                Run.True(Throws(() => Updater.Checked(r, sums, "", exe, pub, current)), "no signature");
            }
        }

        static bool Throws(Action a)
        {
            try { a(); return false; }
            catch (Exception) { return true; }
        }

        public static void UpdateWaitsForGames()
        {
            Run.True(!TrayApp.UpdateMustWait(0, false, false), "nothing running: it installs");
            Run.True(TrayApp.UpdateMustWait(1, false, false), "a followed game is running");
            Run.True(TrayApp.UpdateMustWait(0, true, false), "the countdown back to SDR / the saved resolution");
            Run.True(TrayApp.UpdateMustWait(0, false, true), "Steam is starting a game");
        }
    }
}
