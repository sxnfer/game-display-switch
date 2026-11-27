// Checks for SwitchLog.cs: how a line is put together, that the log keeps its newest half, that it never throws, and
// that lines written from many threads at once come out whole. Each check works in a temporary folder of its own.

using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace HdrSwitch.Tests
{
    static partial class Checks
    {
        // ---- the switch log ----

        /// <summary>A new empty folder with the switch log pointed into it.</summary>
        static string SwitchLogDir()
        {
            string dir = TempDir();
            SwitchLog.LogFile = Path.Combine(dir, "display.log");
            return dir;
        }

        static string[] SwitchLogLines()
        {
            return File.Exists(SwitchLog.LogFile) ? File.ReadAllText(SwitchLog.LogFile).Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries) : new string[0];
        }

        public static void SwitchLogLineFormat()
        {
            var at = new DateTime(2026, 11, 27, 12, 3, 4, 512);
            var want = new Resolution.Mode { Width = 2368, Height = 1332, Hz = 240 };
            var now = new Resolution.Mode { Width = 2560, Height = 1440, Hz = 240 };
            Run.Equal(@"2026-11-27 12:03:04.512 apply game=cs2 display=\\.\DISPLAY1 want=2368x1332@240 area=- current=2560x1440@240 -> applied (1:1, centred)",
                SwitchLog.Line(at, "apply", "game", "cs2", "display", @"\\.\DISPLAY1", "want", want, "area", null, "current", now, SwitchLog.Then, "applied (1:1, centred)"),
                "time to the millisecond, what, pairs, then the outcome in words");
            Run.Equal("2026-11-27 12:03:04.512 grace-fire", SwitchLog.Line(at, "grace-fire"), "no pairs: just what happened");
            Run.Equal("2026-01-02 03:04:05.006 x", SwitchLog.Line(new DateTime(2026, 1, 2, 3, 4, 5, 6), "x"), "the time is padded");
            Run.Equal("2026-11-27 12:03:04.512 scan-detected pid=4120 exe=cs2 ageMs=812 profiled=yes hdrListed=no",
                SwitchLog.Line(at, "scan-detected", "pid", 4120, "exe", "cs2", "ageMs", 812L, "profiled", true, "hdrListed", false), "numbers as they are, yes and no");
            Run.Equal("2026-11-27 12:03:04.512 hint text=\"No 2368 × 1332 at 240 Hz for 'CS 2'.\"",
                SwitchLog.Line(at, "hint", "text", "No 2368 × 1332 at 240 Hz for \"CS 2\"."), "text with spaces is quoted, its quotes made single");
            Run.Equal("2026-11-27 12:03:04.512 a b=\"two lines\" c=\"\"", SwitchLog.Line(at, "a", "b", "two\nlines", "c", ""), "a line break becomes a space: one event, one line");
            Run.Equal("2026-11-27 12:03:04.512 a b=-", SwitchLog.Line(at, "a", "b"), "a key without a value");
            Run.Equal("2026-11-27 12:03:04.512 a ratio=0.925", SwitchLog.Line(at, "a", "ratio", 0.925), "a decimal point whatever the PC's language");
            Run.Equal("2026-11-27 12:03:04.512 display-changed current=2560x1440@240 saved=- inOurMode=no -> nothing to do",
                SwitchLog.Line(at, "display-changed", SwitchLog.Pairs("current", now, "saved", null), "inOurMode", false, SwitchLog.Then, "nothing to do"), "pairs put together elsewhere go in as they are");
            Run.Equal("2026-11-27 12:03:04.512 a -> -", SwitchLog.Line(at, "a", SwitchLog.Then, null), "an outcome with nothing in it");
            Run.Equal("2026-11-27 12:03:04.512 a", SwitchLog.Line(at, "a", (object[])null), "no list at all");
        }

        public static void SwitchLogValues()
        {
            Run.Equal("-", SwitchLog.Value(null), "nothing");
            Run.Equal("1776x1332@144", SwitchLog.Value(new Resolution.Mode { Width = 1776, Height = 1332, Hz = 144 }), "a mode");
            Run.Equal("2368x1332>2560x1440/centred(2)", SwitchLog.Value(new Resolution.Picture { SourceW = 2368, SourceH = 1332, TargetW = 2560, TargetH = 1440, Scaling = 2 }), "a display path: desktop, signal, scaling");
            Run.Equal("2368x1332>2368x1332/identity(1)", SwitchLog.Value(new Resolution.Picture { SourceW = 2368, SourceH = 1332, TargetW = 2368, TargetH = 1332, Scaling = 1 }), "the monitor scales: the signal shrank");
            Run.Equal("stretched(3)", SwitchLog.ScalingName(3), "Windows' scaling in words");
            Run.Equal("scaling(64)", SwitchLog.ScalingName(64), "one it has no word for");
            Run.Equal("no-scaling(3)", SwitchLog.NvidiaName(Nvidia.NoScaling), "NVIDIA's No scaling");
            Run.Equal("full-screen(2)", SwitchLog.NvidiaName(Nvidia.FullScreen), "NVIDIA's Full screen");
            Run.Equal("unreadable(-1)", SwitchLog.NvidiaName(-1), "no NVIDIA driver, or an error");
            Run.Equal("nvidia(5)", SwitchLog.NvidiaName(5), "another NVIDIA setting");
            Run.Equal("bad-mode(-2)", SwitchLog.CdsName(-2), "Windows refused the mode");
            Run.Equal("ok(0)", SwitchLog.CdsName(0), "Windows took the mode");
            Run.Equal("code(-9)", SwitchLog.CdsName(-9), "an answer it has no word for");
        }

        public static void SwitchLogWritesWholeLines()
        {
            string dir = SwitchLogDir();
            try
            {
                Run.Equal(0, SwitchLogLines().Length, "nothing yet");
                SwitchLog.Write("apply", "game", "cs2", SwitchLog.Then, "skipped (paused)");
                SwitchLog.Write("apply", "game", "cs2", SwitchLog.Then, "skipped (paused)");
                var lines = SwitchLogLines();
                Run.Equal(2, lines.Length, "every event is written, the same one twice too");
                Run.True(Regex.IsMatch(lines[0], @"^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3} apply game=cs2 -> skipped \(paused\)$"), "a line as it is in the file: " + lines[0]);
                Run.True(File.ReadAllText(SwitchLog.LogFile).EndsWith("\r\n"), "each line ends, so Notepad shows them apart");

                SwitchLog.Periodic("k", "hdr-on", "display", "D1", SwitchLog.Then, "failed");
                SwitchLog.Periodic("k", "hdr-on", "display", "D1", SwitchLog.Then, "failed");
                Run.Equal(3, SwitchLogLines().Length, "something retried every second is written once a minute");
                SwitchLog.Periodic("k", "hdr-on", "display", "D1", SwitchLog.Then, "on");
                SwitchLog.Periodic("other", "hdr-on", "display", "D1", SwitchLog.Then, "on");
                Run.Equal(5, SwitchLogLines().Length, "… a different outcome, or another key, at once");
            }
            finally { Clean(dir); }
        }

        public static void SwitchLogKeepsItsNewestHalf()
        {
            string dir = SwitchLogDir();
            try
            {
                var filler = Enumerable.Range(0, 40000).Select(i => "old line " + i).ToList();   // about 570 KB
                File.WriteAllLines(SwitchLog.LogFile, filler);
                Run.True(new FileInfo(SwitchLog.LogFile).Length > SwitchLog.MaxLog, "the log is over its limit");
                SwitchLog.Write("revert", SwitchLog.Then, "put back to the saved mode");
                long size = new FileInfo(SwitchLog.LogFile).Length;
                Run.True(size <= SwitchLog.MaxLog / 2 + 200, "cut to its newest half: " + size + " bytes");
                var lines = File.ReadAllLines(SwitchLog.LogFile);
                Run.True(lines[0].StartsWith("old line "), "it starts at the start of a line: " + lines[0]);
                Run.Equal("old line 39999", lines[lines.Length - 2], "the newest old line is kept");
                Run.True(lines[lines.Length - 1].EndsWith("revert -> put back to the saved mode"), "the new line is the last");
                Run.True(!File.Exists(SwitchLog.LogFile + ".tmp"), "nothing is left beside it");

                // Many more lines than fit: it never grows past its limit by more than a line.
                string pad = new string('x', 1000);
                for (int i = 0; i < 1200; i++) SwitchLog.Write("fill", "n", i, "pad", pad);
                Run.True(new FileInfo(SwitchLog.LogFile).Length <= SwitchLog.MaxLog + 1100, "still capped after 1.2 MB of lines");
                lines = File.ReadAllLines(SwitchLog.LogFile);
                Run.True(lines[lines.Length - 1].Contains(" fill n=1199 "), "the last line written is the last in the file");
                Run.True(lines.All(l => l.Contains(" fill n=") && l.EndsWith(pad)), "only whole lines are left");
            }
            finally { Clean(dir); }
        }

        public static void SwitchLogNeverThrows()
        {
            string dir = SwitchLogDir();
            try
            {
                // A file where the folder should be: the log can't be created.
                string blocker = Path.Combine(dir, "blocker");
                File.WriteAllText(blocker, "not a folder");
                SwitchLog.LogFile = Path.Combine(blocker, "display.log");
                SwitchLog.Write("apply", "game", "cs2");
                SwitchLog.Periodic("k", "apply", "game", "cs2");
                Run.True(!File.Exists(SwitchLog.LogFile), "an unwritable path: nothing written, nothing thrown");

                // The log's own name taken by a folder.
                SwitchLog.LogFile = Path.Combine(dir, "taken");
                Directory.CreateDirectory(SwitchLog.LogFile);
                SwitchLog.Write("apply", "game", "cs2");
                Run.True(Directory.Exists(SwitchLog.LogFile), "a folder in its place: left alone, nothing thrown");

                // A path that is no path, and none at all.
                SwitchLog.LogFile = "bad\0name";
                SwitchLog.Write("apply", "game", "cs2");
                SwitchLog.LogFile = "";
                SwitchLog.Write("apply", "game", "cs2");
                SwitchLog.LogFile = null;
                SwitchLog.Write("apply", "game", "cs2");
                SwitchLog.Periodic("k2", "apply", "game", "cs2");
                Run.True(true, "no path at all: nothing thrown");

                // Odd things to write.
                SwitchLog.LogFile = Path.Combine(dir, "display.log");
                SwitchLog.Write(null);
                SwitchLog.Write(null, (object[])null);
                SwitchLog.Write("odd", null, null, "key");
                SwitchLog.Write("odd", new object(), new ThrowsOnToString());
                SwitchLog.Periodic(null, "odd");
                Run.Equal("", SwitchLog.Pairs("a", new ThrowsOnToString()).Text, "pairs that can't be put together are left out");
                Run.True(SwitchLogLines().Length >= 3, "the lines that could be written were");
                Run.True(SwitchLogLines().All(l => !l.Contains("\n")), "… one line each");
            }
            finally { Clean(dir); }
        }

        sealed class ThrowsOnToString
        {
            public override string ToString() { throw new InvalidOperationException("no text"); }
        }

        public static void SwitchLogFromManyThreads()
        {
            string dir = SwitchLogDir();
            try
            {
                const int threads = 8, each = 150;
                string pad = new string('p', 300);
                var go = new ManualResetEvent(false);
                var all = Enumerable.Range(0, threads).Select(t => new Thread(() =>
                {
                    go.WaitOne();
                    for (int i = 0; i < each; i++) SwitchLog.Write("smoke", "thread", t, "n", i, "pad", pad, SwitchLog.Then, "done " + t + "/" + i);
                })).ToList();
                foreach (var t in all) t.Start();
                go.Set();
                foreach (var t in all) t.Join();

                var lines = SwitchLogLines();
                Run.Equal(threads * each, lines.Length, "every line is there");
                var whole = new Regex(@"^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3} smoke thread=(\d+) n=(\d+) pad=p{300} -> done \1/\2$");
                Run.True(lines.All(l => whole.IsMatch(l)), "each one whole, none run into another");
                Run.Equal(threads * each, lines.Select(l => whole.Match(l).Groups[1].Value + "/" + whole.Match(l).Groups[2].Value).Distinct().Count(), "each one once");
                for (int t = 0; t < threads; t++)
                {
                    var mine = lines.Select(l => whole.Match(l)).Where(m => m.Groups[1].Value == t + "").Select(m => int.Parse(m.Groups[2].Value)).ToList();
                    Run.True(mine.SequenceEqual(Enumerable.Range(0, each)), "thread " + t + "'s lines are in the order it wrote them");
                }
            }
            finally { Clean(dir); }
        }
    }
}
