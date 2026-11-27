// The switch log: what Display Switch did to the displays and why, one plain line per decision, in
// %AppData%\HdrSwitch\display.log. When a game starts at the wrong size, the line that says why is in here: the mode
// that was wanted and the one that was there, whether NVIDIA's scaling could be taken, what Windows answered. It only
// writes when something happens (a game starts or closes, the display changes, HDR or a mode is switched), never on a
// timer, so an idle app costs nothing. Like the error log (Guard.cs) it is capped and never throws.
//
// A line reads: time, what happened, key=value pairs, and after "->" what was done about it.
//   2026-11-27 12:03:04.512 apply game=cs2 display=\\.\DISPLAY1 want=2368x1332@240 current=2560x1440@240 -> applied

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace HdrSwitch
{
    static class SwitchLog
    {
        /// <summary>Where the lines go. The checks point it at a folder of their own.</summary>
        internal static string LogFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HdrSwitch", "display.log");
        /// <summary>Above this size the log keeps only its newest half.</summary>
        internal const int MaxLog = 512 * 1024;
        /// <summary>Marks the outcome in a list of pairs: Write("apply", "game", "cs2", Then, "applied").</summary>
        internal const string Then = "->";

        static readonly object sync = new object();
        static readonly Encoding Utf8 = new UTF8Encoding(false);
        static readonly Dictionary<string, KeyValuePair<string, DateTime>> lastOf = new Dictionary<string, KeyValuePair<string, DateTime>>();

        /// <summary>Pairs already written out ("current=… saved=…"), to pass along with others.</summary>
        internal sealed class Part
        {
            public readonly string Text;
            public Part(string text) { Text = text ?? ""; }
        }

        /// <summary>
        /// Writes one line: the time, what happened, then key and value in turn ("game", "cs2", "pid", 4120). A Part goes in
        /// as it is; the key Then ("->") starts the outcome. Never throws, from any thread.
        /// </summary>
        internal static void Write(string what, params object[] kv)
        {
            try { Append(Line(DateTime.Now, what, kv)); }
            catch (Exception) { }   // a log that can't be written must never cost a switch
        }

        /// <summary>
        /// For something that can come round again and again (a switch retried by the once-a-second scan): the same line
        /// under the same key is written once a minute, a different one at once. Never throws.
        /// </summary>
        internal static void Periodic(string key, string what, params object[] kv)
        {
            try
            {
                var now = DateTime.Now;
                string body = Body(what, kv);
                lock (sync)
                {
                    KeyValuePair<string, DateTime> last;
                    if (lastOf.TryGetValue(key, out last) && last.Key == body && (now - last.Value).TotalSeconds < 60) return;
                    if (lastOf.Count > 64) lastOf.Clear();
                    lastOf[key] = new KeyValuePair<string, DateTime>(body, now);
                }
                Append(Stamp(now) + " " + body);
            }
            catch (Exception) { }
        }

        /// <summary>Several pairs as one Part, to hand to Write among others.</summary>
        internal static Part Pairs(params object[] kv)
        {
            try { return new Part(Join(kv)); }
            catch (Exception) { return new Part(""); }
        }

        /// <summary>The whole line, without its line end: "2026-11-27 12:03:04.512 apply game=cs2 -> applied".</summary>
        internal static string Line(DateTime at, string what, params object[] kv)
        {
            return Stamp(at) + " " + Body(what, kv);
        }

        static string Stamp(DateTime at) { return at.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture); }

        static string Body(string what, object[] kv)
        {
            string pairs = Join(kv);
            return OneLine(what ?? "") + (pairs.Length > 0 ? " " + pairs : "");
        }

        static string Join(object[] kv)
        {
            var sb = new StringBuilder();
            if (kv == null) return "";
            for (int i = 0; i < kv.Length; i++)
            {
                string piece;
                var part = kv[i] as Part;
                if (part != null) piece = part.Text;
                else
                {
                    string key = kv[i] == null ? "" : kv[i].ToString();
                    object value = i + 1 < kv.Length ? kv[i + 1] : null;
                    i++;
                    // The outcome is words for a person, so it stays unquoted; a value is one token.
                    piece = key == Then ? Then + " " + OneLine(value == null ? "-" : value.ToString()) : OneLine(key) + "=" + Value(value);
                }
                if (piece.Length == 0) continue;
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(piece);
            }
            return sb.ToString();
        }

        /// <summary>
        /// A value as one token: "-" for nothing, yes/no, 2368x1332@240 for a mode, 2368x1332>2560x1440/centred(2) for a
        /// display path, numbers as they are, and text in quotes when it has a space in it.
        /// </summary>
        internal static string Value(object v)
        {
            if (v == null) return "-";
            if (v is bool) return (bool)v ? "yes" : "no";
            var mode = v as Resolution.Mode;
            if (mode != null) return mode.Width + "x" + mode.Height + "@" + mode.Hz;
            var pic = v as Resolution.Picture;
            if (pic != null) return pic.SourceW + "x" + pic.SourceH + ">" + pic.TargetW + "x" + pic.TargetH + "/" + ScalingName(pic.Scaling);
            var f = v as IFormattable;
            string s = OneLine(f != null ? f.ToString(null, CultureInfo.InvariantCulture) : v.ToString());
            if (s.Length == 0) return "\"\"";
            return s.IndexOf(' ') >= 0 || s.IndexOf('"') >= 0 ? "\"" + s.Replace("\"", "'") + "\"" : s;
        }

        /// <summary>DISPLAYCONFIG_SCALING in words: what Windows says the display path does with the desktop.</summary>
        internal static string ScalingName(int scaling)
        {
            switch (scaling)
            {
                case 1: return "identity(1)";
                case 2: return "centred(2)";
                case 3: return "stretched(3)";
                case 4: return "aspect(4)";
                case 5: return "custom(5)";
                case 128: return "preferred(128)";
                default: return "scaling(" + scaling + ")";
            }
        }

        /// <summary>The NVIDIA driver's scaling setting in words (see Nvidia.cs); -1 is "couldn't be read".</summary>
        internal static string NvidiaName(int scaling)
        {
            if (scaling < 0) return "unreadable(" + scaling + ")";
            if (scaling == Nvidia.FullScreen) return "full-screen(2)";
            if (scaling == Nvidia.NoScaling) return "no-scaling(3)";
            if (scaling == 7) return "balanced-no-scaling(7)";
            return "nvidia(" + scaling + ")";
        }

        /// <summary>What ChangeDisplaySettingsEx answered, in words.</summary>
        internal static string CdsName(int code)
        {
            switch (code)
            {
                case 0: return "ok(0)";
                case 1: return "restart-needed(1)";
                case -1: return "failed(-1)";
                case -2: return "bad-mode(-2)";
                case -3: return "not-updated(-3)";
                case -4: return "bad-flags(-4)";
                case -5: return "bad-param(-5)";
                case -6: return "bad-dualview(-6)";
                default: return "code(" + code + ")";
            }
        }

        /// <summary>Line breaks and other control characters become spaces: one event, one line.</summary>
        static string OneLine(string s)
        {
            if (s == null) return "";
            for (int i = 0; i < s.Length; i++)
                if (char.IsControl(s[i]))
                {
                    var sb = new StringBuilder(s.Length);
                    foreach (char c in s) sb.Append(char.IsControl(c) ? ' ' : c);
                    return sb.ToString();
                }
            return s;
        }

        static void Append(string line)
        {
            lock (sync)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogFile));
                Guard.Cap(LogFile, MaxLog);
                File.AppendAllText(LogFile, line + "\r\n", Utf8);
            }
        }
    }

    // What the log says about the app's state: put together here so the code that switches only gains a line per decision.
    sealed partial class TrayApp
    {
        /// <summary>"current=2560x1440@240 saved=2560x1440@240" for a display.</summary>
        static SwitchLog.Part ModesOf(string gdi)
        {
            try { return SwitchLog.Pairs("current", Resolution.Current(gdi), "saved", Resolution.Saved(gdi)); }
            catch (Exception) { return SwitchLog.Pairs("current", "?"); }
        }

        /// <summary>The mode this app set: "1776x1332@240/area=2368x1332(game=cs2,scalingBack=2)", or "-".</summary>
        static string OursText(ActiveMode m)
        {
            if (m == null) return "-";
            return SwitchLog.Value(m.Mode) + (m.Area == null ? "" : "/area=" + m.Area.Width + "x" + m.Area.Height)
                + "(game=" + (m.Game.Length == 0 ? "?" : m.Game) + ",scalingBack=" + m.ScalingBack + ")";
        }

        /// <summary>Every display's HDR: "\\.\DISPLAY1:on:ours,\\.\DISPLAY2:off,\\.\DISPLAY3:unsupported".</summary>
        string HdrText()
        {
            try
            {
                var all = Hdr.All();
                if (all.Count == 0) return "none";
                return string.Join(",", all.Select(d => d.Gdi + ":" + (!d.Supported ? "unsupported" : d.On ? "on" : "off")
                    + (owned.Any(o => o.Key == d.Key) ? ":ours" : "")));
            }
            catch (Exception) { return "?"; }
        }

        /// <summary>Why TargetGdi picks the display it does (it follows the HDR feature's choice, not the game's screen).</summary>
        static string TargetWhy()
        {
            try
            {
                string primary = Screen.PrimaryScreen.DeviceName;
                var d = Hdr.Primary();
                if (d == null || d.Gdi.Length == 0) return "primary(no-hdr-display)";
                return d.Gdi == primary ? "primary(hdr-capable)" : "first-hdr-display(primary=" + primary + ",not-hdr-capable)";
            }
            catch (Exception) { return "?"; }
        }

        /// <summary>How the last Resolution.Apply / ApplyStretched went: the steps taken and what Windows answered.</summary>
        static string SetModeTrace(ActiveMode m)
        {
            return m.Area == null ? "cds=" + SwitchLog.CdsName(Resolution.LastCds) : Resolution.LastStretch ?? "?";
        }

        /// <summary>The refresh rates at which the display offers this size: "60,144,240", or "none".</summary>
        static string RatesText(string gdi, int w, int h)
        {
            var rates = Resolution.Rates(gdi, w, h);
            return rates.Count == 0 ? "none" : string.Join(",", rates);
        }

        /// <summary>Whether the active mode is what the game's profile asks for now; "-" without a profile.</summary>
        string MatchesText(string name)
        {
            Resolution.Mode p;
            if (res == null || !profiles.TryGetValue(name, out p)) return "-";
            return Matches(res, p, StretchOf(name)) ? "yes" : "no";
        }

        /// <summary>The state every display-change line carries: the modes, whose they are, what runs, HDR.</summary>
        SwitchLog.Part ChangeState()
        {
            try
            {
                string gdi = res != null ? res.Gdi : TargetGdi();
                return SwitchLog.Pairs("display", gdi, ModesOf(gdi), "picture", Resolution.Read(gdi), "ours", OursText(res), "inOurMode", InOurMode(),
                    "stretchLost", StretchLost(), "profiledRunning", ProfiledRunning(), "running", running.Count, "paused", paused, "hdr", HdrText());
            }
            catch (Exception) { return SwitchLog.Pairs("state", "?"); }
        }

        // Sleep, resume, lock and unlock change nothing here: they are only written down, because a display mode lost
        // around one of them is otherwise a mystery.
        Microsoft.Win32.PowerModeChangedEventHandler onPowerLog;
        Microsoft.Win32.SessionSwitchEventHandler onSessionLog;

        void LogSystemEvents(bool on)
        {
            try
            {
                if (on)
                {
                    onPowerLog = (s, e) => { string m = e.Mode.ToString(); ui.Post(_ => SwitchLog.Write("power", "event", m, ChangeState()), null); };
                    onSessionLog = (s, e) => { string r = e.Reason.ToString(); ui.Post(_ => SwitchLog.Write("session", "event", r, ChangeState()), null); };
                    Microsoft.Win32.SystemEvents.PowerModeChanged += onPowerLog;
                    Microsoft.Win32.SystemEvents.SessionSwitch += onSessionLog;
                }
                else
                {
                    if (onPowerLog != null) Microsoft.Win32.SystemEvents.PowerModeChanged -= onPowerLog;
                    if (onSessionLog != null) Microsoft.Win32.SystemEvents.SessionSwitch -= onSessionLog;
                }
            }
            catch (Exception e) { Guard.Log("Switch log: power and session events", e); }
        }

        /// <summary>The Settings row's button: the log in the default editor. False if there is nothing in it yet.</summary>
        internal static bool OpenSwitchLog()
        {
            try
            {
                var info = new FileInfo(SwitchLog.LogFile);
                if (!info.Exists || info.Length == 0) return false;
                System.Diagnostics.Process.Start(SwitchLog.LogFile);
                return true;
            }
            catch (Exception e)
            {
                Guard.Log("Couldn't open " + SwitchLog.LogFile, e);
                return true;   // there is a log; why it didn't open is in error.log
            }
        }
    }
}
