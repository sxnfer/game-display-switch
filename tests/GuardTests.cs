// Checks for Guard.cs and the saving of the app's own files: the error log, the write-beside-then-swap save, and that
// resolutions.txt and brightness.txt keep lines they don't understand. Each check works in a temporary folder of its own.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace HdrSwitch.Tests
{
    static partial class Checks
    {
        // ---- error log and saving ----

        /// <summary>A new empty folder under the system's temp folder, with the error log pointed into it while the check runs.</summary>
        static string TempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "HdrSwitchTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            Guard.LogFile = Path.Combine(dir, "error.log");
            return dir;
        }

        static void Clean(string dir)
        {
            try
            {
                foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
                Directory.Delete(dir, true);
            }
            catch (Exception) { }
        }

        static string LogText()
        {
            return File.Exists(Guard.LogFile) ? File.ReadAllText(Guard.LogFile) : "";
        }

        static Exception Thrown(string message)
        {
            try { throw new InvalidOperationException(message); }
            catch (Exception e) { return e; }
        }

        public static void LogWritesAnError()
        {
            string dir = TempDir();
            try
            {
                Guard.Log("Flyout click", Thrown("first error"));
                string log = LogText();
                Run.True(log.Contains("Flyout click"), "where it happened");
                Run.True(log.Contains("System.InvalidOperationException: first error"), "the type and message");
                Run.True(log.Contains("Thrown"), "the stack");
                Run.True(log.Substring(0, 4) == DateTime.Now.Year.ToString(), "starts with the time");

                Guard.Log("Flyout click", Thrown("first error"));
                Run.Equal(log, LogText(), "the same error again at once is not written twice");
                Guard.Log("Timer", Thrown("second error"));
                Run.True(LogText().Contains("second error") && LogText().StartsWith(log), "another one is added after it");
            }
            finally { Clean(dir); }
        }

        public static void LogKeepsItsNewestHalf()
        {
            string dir = TempDir();
            try
            {
                var filler = Enumerable.Range(0, 20000).Select(i => "old line " + i).ToList();   // about 270 KB
                File.WriteAllLines(Guard.LogFile, filler);
                Run.True(new FileInfo(Guard.LogFile).Length > Guard.MaxLog, "the log starts over its limit");
                Guard.Log("Timer", Thrown("after the cut"));
                string log = LogText();
                Run.True(log.Length <= Guard.MaxLog / 2 + 2000, "cut to about half: " + log.Length + " bytes");
                Run.True(log.Contains("after the cut"), "the new entry is there");
                Run.True(!log.Contains("old line 0\r") && !log.Contains("old line 0\n"), "the oldest lines went");
                Run.True(log.Contains("old line 19999"), "the newest old lines stayed");
                Run.True(log.StartsWith("old line "), "it starts at the start of a line");
            }
            finally { Clean(dir); }
        }

        public static void LogNeverThrows()
        {
            string dir = TempDir();
            try
            {
                File.WriteAllText(Path.Combine(dir, "file"), "");
                Guard.LogFile = Path.Combine(dir, "file", "error.log");   // its folder is a file: it can't be written
                Guard.Log("Somewhere", Thrown("lost"));
                Guard.Log("Somewhere", null);
                Run.True(true, "no exception");
            }
            finally { Clean(dir); }
        }

        public static void SaveWritesAndReplaces()
        {
            string dir = TempDir();
            try
            {
                string path = Path.Combine(dir, "games.txt");
                Run.True(Guard.Save(path, new[] { "first" }), "a new file");
                Run.Equal("first", File.ReadAllLines(path).Single(), "its content");
                Run.True(Guard.Save(path, new[] { "second", "third" }), "over an existing file");
                Run.Equal("second|third", string.Join("|", File.ReadAllLines(path)), "the new content");
                Run.True(Guard.Save(Path.Combine(dir, "resolution.state"), "a|1|2|3"), "a short text");
                Run.Equal("a|1|2|3", File.ReadAllText(Path.Combine(dir, "resolution.state")), "its content");
                Run.True(!File.Exists(path + ".tmp") && !File.Exists(Path.Combine(dir, "resolution.state.tmp")), "no .tmp left");
                Run.True(!File.Exists(Guard.LogFile), "nothing logged");
            }
            finally { Clean(dir); }
        }

        public static void SaveLeavesAReadOnlyFile()
        {
            string dir = TempDir();
            string path = Path.Combine(dir, "games.txt");
            try
            {
                File.WriteAllLines(path, new[] { "-cs2.exe", "my own line" });
                File.SetAttributes(path, FileAttributes.ReadOnly);
                bool saved = true;
                try { saved = Guard.Save(path, new[] { "something else" }); }
                catch (Exception e) { Run.Fail("threw " + e.GetType().Name); }
                Run.True(!saved, "returns false");
                Run.Equal("-cs2.exe|my own line", string.Join("|", File.ReadAllLines(path)), "the file is unchanged");
                Run.True(LogText().Contains("read-only") && LogText().Contains(path), "the reason is logged");
                Run.True(!File.Exists(path + ".tmp"), "no .tmp left");
            }
            finally { Clean(dir); }
        }

        public static void SaveKeepsTheOldFileIfTheTempCantBeWritten()
        {
            string dir = TempDir();
            try
            {
                // The .tmp is a folder: nothing can be written there.
                string path = Path.Combine(dir, "resolutions.txt");
                File.WriteAllLines(path, new[] { "cs2.exe 2368x1332", "# mine" });
                Directory.CreateDirectory(path + ".tmp");
                Run.True(!Guard.Save(path, new[] { "valorant.exe 1920x1080" }), "returns false when the .tmp is a folder");
                Run.Equal("cs2.exe 2368x1332|# mine", string.Join("|", File.ReadAllLines(path)), "the old content is intact");
                Run.True(Directory.Exists(path + ".tmp"), "the folder in its way is left alone");
                Run.True(LogText().Contains("Couldn't save " + path), "the failure is logged");

                // The .tmp is held open by someone else.
                path = Path.Combine(dir, "brightness.txt");
                File.WriteAllLines(path, new[] { "cs2.exe 40" });
                using (new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
                    Run.True(!Guard.Save(path, new[] { "cs2.exe 80" }), "returns false when the .tmp is locked");
                Run.Equal("cs2.exe 40", string.Join("|", File.ReadAllLines(path)), "the old content is intact");
            }
            finally { Clean(dir); }
        }

        public static void DeleteFiles()
        {
            string dir = TempDir();
            try
            {
                string path = Path.Combine(dir, "brightness.state");
                Run.True(Guard.Delete(path), "a file that isn't there");
                File.WriteAllText(path, "x");
                Run.True(Guard.Delete(path) && !File.Exists(path), "a file that is");
                Run.True(!File.Exists(Guard.LogFile), "nothing logged");
            }
            finally { Clean(dir); }
        }

        // ---- keeping the user's own lines ----

        static Dictionary<string, Resolution.Mode> Profiles(IEnumerable<string> lines, Dictionary<string, Resolution.Mode> stretches)
        {
            var all = new Dictionary<string, Resolution.Mode>();
            foreach (string raw in lines)
            {
                string name, label;
                Resolution.Mode p, own;
                if (!TrayApp.ParseProfile(raw, out name, out p, out own, out label)) continue;
                all[name] = p;
                if (own != null) stretches[name] = own;
            }
            return all;
        }

        public static void ProfileSaveKeepsUnparsedLines()
        {
            var old = new[]
            {
                "# my notes about these",
                "cs2.exe 2368x1332   # CS2",
                "valorant.exe 2368x1332@240 stretch 1776x1332",
                "apex.exe 1920 × 1080 @ 165 Hz",
                "",
                "fortnight.exe 1920 by 1080   # a typo: not a resolution",
                "tiny.exe 100x100",
            };
            var stretches = new Dictionary<string, Resolution.Mode>();
            var profiles = Profiles(old, stretches);
            Run.Equal(3, profiles.Count, "three lines are resolutions");

            // From the flyout: CS2 gets a rate, Valorant is switched off, a new game gets one.
            profiles["cs2"] = new Resolution.Mode { Width = 2368, Height = 1332, Hz = 240 };
            profiles.Remove("valorant");
            stretches.Remove("valorant");
            profiles["deadlock"] = new Resolution.Mode { Width = 1920, Height = 1080 };
            Func<string, string> label = n => n == "cs2" ? "CS2" : n;

            var saved = TrayApp.ProfileFile(old, profiles, stretches, label);
            Run.Equal("# my notes about these", saved[0], "a note stays first");
            Run.True(saved[1].StartsWith("cs2.exe") && saved[1].Contains("2368x1332@240") && saved[1].EndsWith("# CS2"), "a changed line is rewritten in its place: " + saved[1]);
            Run.Equal("apex.exe 1920 × 1080 @ 165 Hz", saved[2], "an unchanged line stays as written");
            Run.Equal("", saved[3], "a blank line stays");
            Run.Equal("fortnight.exe 1920 by 1080   # a typo: not a resolution", saved[4], "a line that isn't a resolution stays, word for word");
            Run.Equal("tiny.exe 100x100", saved[5], "a resolution too small to use stays too");
            Run.True(saved[6].StartsWith("deadlock.exe") && saved[6].Contains("1920x1080"), "a new game goes at the end");
            Run.Equal(7, saved.Count, "the switched-off game's line went");

            var again = new Dictionary<string, Resolution.Mode>();
            var reloaded = Profiles(saved, again);
            Run.Equal(3, reloaded.Count, "reads back the same games");
            Run.True(reloaded["cs2"].Same(profiles["cs2"]) && reloaded["deadlock"].Same(profiles["deadlock"]), "and the same resolutions");
        }

        public static void ProfileSaveKeepsStretchAndDropsDuplicates()
        {
            var old = new[] { "valorant.exe 2368x1332 # mine", "valorant.exe 1920x1080" };
            var stretches = new Dictionary<string, Resolution.Mode>();
            var profiles = Profiles(old, stretches);   // the second line wins when loading
            var saved = TrayApp.ProfileFile(old, profiles, stretches, n => n);
            Run.Equal(1, saved.Count, "one line per game");
            Run.True(saved[0].StartsWith("valorant.exe") && saved[0].Contains("1920x1080"), "the line says what was loaded: " + saved[0]);

            old = new[] { "valorant.exe 2368x1332 stretch 1776x1332 # mine" };
            stretches.Clear();
            profiles = Profiles(old, stretches);
            saved = TrayApp.ProfileFile(old, profiles, stretches, n => n);
            Run.Equal("valorant.exe 2368x1332 stretch 1776x1332 # mine", saved.Single(), "an unchanged stretched line stays as written");
        }

        public static void ProfileSaveNewFile()
        {
            var profiles = new Dictionary<string, Resolution.Mode> { { "cs2", new Resolution.Mode { Width = 2368, Height = 1332 } } };
            var saved = TrayApp.ProfileFile(null, profiles, new Dictionary<string, Resolution.Mode>(), n => n);
            Run.True(saved[0].StartsWith("# Display Switch - competitive resolutions"), "a new file starts with the explanation");
            Run.Equal("cs2.exe                             2368x1332", saved.Last(), "then the games");
        }

        public static void BrightnessSaveKeepsUnparsedLines()
        {
            var old = new[] { "# levels", "cs2.exe 35%  # Counter-Strike 2", "bad.exe 140", "cyberpunk2077.exe 60", "nolevel.exe" };
            var levels = TrayApp.ParseBrightness(old);
            levels["cyberpunk2077"] = 70;
            levels["hades"] = 20;
            var saved = TrayApp.BrightnessFileLines(old, levels, n => n);
            Run.Equal("# levels", saved[0], "a note stays");
            Run.Equal("cs2.exe 35%  # Counter-Strike 2", saved[1], "an unchanged level stays as written");
            Run.Equal("bad.exe 140", saved[2], "a level above 100 stays as written");
            Run.True(saved[3].StartsWith("cyberpunk2077.exe") && saved[3].TrimEnd().EndsWith("70"), "a changed level is rewritten in place: " + saved[3]);
            Run.Equal("nolevel.exe", saved[4], "a line without a level stays");
            Run.True(saved[5].StartsWith("hades.exe") && saved[5].EndsWith("20"), "a new one goes at the end");
            Run.Equal(6, saved.Count, "nothing else");
        }
    }
}
