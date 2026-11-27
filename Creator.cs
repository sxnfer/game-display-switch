// The resolution creator's engine room: adding a resolution to a monitor the way ToastyX's Custom Resolution Utility
// (CRU, https://www.monitortests.com/forum/Thread-Custom-Resolution-Utility-CRU) does, without CRU's window.
//
// CRU itself has no command line. What it does when you click OK is write the monitor's edited EDID to the registry,
//   HKLM\SYSTEM\CurrentControlSet\Enum\DISPLAY\<MonitorID>\<Instance>\Device Parameters\EDID_OVERRIDE
// as REG_BINARY values named "0", "1", … – one per 128-byte block – and the graphics driver reads it the next time it
// starts; CRU's restart64.exe restarts the driver. So this file does the same: Edid.cs works out the new EDID, this
// writes it there and runs the user's own copy of restart64.exe /q. CRU is never downloaded or bundled: the user gets it
// from its page and points the app at the folder once.
//
// Writing under HKLM and restarting the driver need administrator rights, and the app runs without them. So the app
// starts itself once more through UAC (Elevate: the "runas" verb) with --apply-edid or --undo-edid and a request file;
// that copy (Elevated) does only that one job – no tray, no single-instance lock – writes a result file and exits.
//
// What keeps it safe:
//  - A backup first. Before the first write for a monitor, whether EDID_OVERRIDE existed and its exact values go to
//    %AppData%\HdrSwitch\edid-backups\<MonitorID>_<Instance>.txt. Later additions keep that first backup.
//  - It builds on what is there. An existing EDID_OVERRIDE (the user's own CRU edits) is the base the resolution is
//    added to; one that can't be read is never written over.
//  - The base can't change underneath. The request carries a hash of the base the user was shown; the elevated copy
//    reads the registry again and refuses if it is different.
//  - Undo restores the backup exactly – the old values back, or the key gone if there was none – and restarts the driver.
//  - Every step is written to the switch log (display.log), every failure comes back as a result with its reason.
//
// The registry and the driver restart sit behind IStore and a delegate, so the checks run the whole of Apply and Undo
// against a made-up registry.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace HdrSwitch
{
    static class Creator
    {
        public const string CruPage = "https://www.monitortests.com/forum/Thread-Custom-Resolution-Utility-CRU";
        public const string OverrideKey = "EDID_OVERRIDE";
        public const string ApplyFlag = "--apply-edid", UndoFlag = "--undo-edid";

        // What the elevated copy exits with (and writes to its result file).
        public const int Ok = 0;
        public const int BadRequest = 2;      // the request file can't be read or asks for something that makes no sense
        public const int Changed = 3;         // the monitor's EDID or override is no longer what the user was shown
        public const int CantAdd = 4;         // the EDID can't take the resolution (Edid.AddResolution says why)
        public const int BackupFailed = 5;    // the backup couldn't be written or read: nothing was changed
        public const int WriteFailed = 6;     // the registry couldn't be written; what was there was put back
        public const int RestartFailed = 7;   // the registry is written, the driver restart didn't run
        public const int NoBackup = 8;        // undo: there is no backup for this monitor
        public const int Crashed = 9;         // something unexpected; the message has it
        // Only from Elevate, never an exit code:
        public const int Cancelled = 100;     // the UAC prompt was declined: nothing was changed
        public const int NoAnswer = 101;      // the elevated copy didn't finish in time or left no result

        /// <summary>One registry value of EDID_OVERRIDE: its name ("0", "1", …) and its bytes.</summary>
        public sealed class Value
        {
            public string Name;
            public byte[] Data;
        }

        /// <summary>What the registry holds for a monitor: its EDID as read from it, and its override, if any.</summary>
        public sealed class Monitor
        {
            public byte[] Edid;                 // the EDID value, or null
            public bool OverrideExisted;        // the EDID_OVERRIDE key is there
            public List<Value> Values = new List<Value>();   // its values, exactly
        }

        /// <summary>The monitor's part of the registry. The real one is RegistryStore; the checks have one in memory.</summary>
        public interface IStore
        {
            /// <summary>Reads the monitor under this key path (below HKLM). Throws if it can't be read.</summary>
            Monitor Read(string keyPath);
            /// <summary>Makes EDID_OVERRIDE hold exactly these values: the key is created, its old values go, these are written.</summary>
            void Write(string keyPath, IList<Value> values);
            /// <summary>Removes the EDID_OVERRIDE key.</summary>
            void Remove(string keyPath);
        }

        /// <summary>What the elevated copy is asked to do.</summary>
        public sealed class Request
        {
            public bool Undo;            // remove what was added (restore the backup) instead of adding
            public string Display;       // the monitor's device path: \\?\DISPLAY#AUS27BC#5&2a1b3c4&0&UID4352#{…}
            public string Name;          // the monitor's name, for the backup and the log
            public int Width, Height;
            public double Hz;            // the native rate the mode gets (143.912…), as Edid.MatchNativeRate gave it
            public string BaseHash;      // BaseHash() of the base the user was shown
            public string CruFolder;     // where the user's restart64.exe is
            public string Dir;           // the app's data folder (the asking user's %AppData%\HdrSwitch): backups and logs go here
        }

        /// <summary>The state of a monitor's EDID_OVERRIDE before Display Switch first wrote to it.</summary>
        public sealed class Backup
        {
            public string Display, Name;
            public string Saved;         // when, "2026-11-27 12:03:04"
            public bool Existed;         // the EDID_OVERRIDE key was there
            public List<Value> Values = new List<Value>();   // its values, exactly
            public string Written;       // BytesHash of the override Display Switch wrote last, or null
        }

        public sealed class Result
        {
            public int Code;
            public string Message = "";  // for the user
            public string Where = "";    // where in the EDID the resolution went
            public bool Ok { get { return Code == Creator.Ok; } }
        }

        /// <summary>What adding a resolution would do, for the confirmation step.</summary>
        public sealed class Plan
        {
            public byte[] Base, After;
            public bool FromOverride;    // built on the monitor's existing EDID_OVERRIDE
            public string Hash;          // BaseHash of Base
            public string Where;         // "base block, descriptor 4"
            public string Error;         // why it can't be done (then After is null)
            public List<string> Added = new List<string>();     // the lines Edid.Describe gains
            public List<string> Removed = new List<string>();   // … and loses (the slot that was free)
        }

        // ---- the monitor's place in the registry ----

        static readonly Regex DevicePath = new Regex(@"^\\\\\?\\DISPLAY#([^#\\/]+)#([^#\\/]+)#\{[0-9A-Fa-f]{8}(-[0-9A-Fa-f]{4}){3}-[0-9A-Fa-f]{12}\}$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        static bool Parts(string devicePath, out string id, out string instance)
        {
            id = instance = null;
            if (devicePath == null || devicePath.Length > 300) return false;
            var m = DevicePath.Match(devicePath);
            if (!m.Success) return false;
            id = m.Groups[1].Value;
            instance = m.Groups[2].Value;
            // A registry key name: printable, and never a way out of Enum\DISPLAY.
            foreach (string part in new[] { id, instance })
                if (part.Trim().Length == 0 || part == "." || part == ".." || part.Any(c => c < ' ' || c == '*' || c == '?' || c == '"')) return false;
            return true;
        }

        /// <summary>
        /// The registry key (below HKLM) of the monitor with this device path:
        /// \\?\DISPLAY#AUS27BC#5&amp;2a1b3c4&amp;0&amp;UID4352#{guid} → SYSTEM\CurrentControlSet\Enum\DISPLAY\AUS27BC\5&amp;2a1b3c4&amp;0&amp;UID4352\Device Parameters.
        /// Null for anything that isn't a monitor's device path (a GDI name, say).
        /// </summary>
        public static string KeyPath(string devicePath)
        {
            string id, instance;
            return Parts(devicePath, out id, out instance) ? @"SYSTEM\CurrentControlSet\Enum\DISPLAY\" + id + @"\" + instance + @"\Device Parameters" : null;
        }

        /// <summary>"AUS27BC_5&amp;2a1b3c4&amp;0&amp;UID4352": the monitor as a file name. Null for anything that isn't a monitor's device path.</summary>
        public static string FileTag(string devicePath)
        {
            string id, instance;
            if (!Parts(devicePath, out id, out instance)) return null;
            var sb = new StringBuilder();
            foreach (char c in id + "_" + instance)
                sb.Append((c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || c == '&' || c == '_' || c == '-' || c == '.' ? c : '_');
            return sb.ToString();
        }

        public static string BackupDir(string dir) { return Path.Combine(dir, "edid-backups"); }

        /// <summary>Where this monitor's backup is kept under the app's data folder, or null if the path isn't a monitor's.</summary>
        public static string BackupFile(string dir, string devicePath)
        {
            string tag = FileTag(devicePath);
            return tag == null ? null : Path.Combine(BackupDir(dir), tag + ".txt");
        }

        // ---- EDID_OVERRIDE's values and the EDID they make ----

        /// <summary>
        /// The EDID a set of EDID_OVERRIDE values makes: blocks "0", "1", … in order, 128 bytes each. Null without an
        /// error when there are no values; null with the reason when they aren't that – a name that isn't a block
        /// number, a block missing in between, a block that isn't 128 bytes.
        /// </summary>
        public static byte[] OverrideBytes(IList<Value> values, out string error)
        {
            error = null;
            if (values == null || values.Count == 0) return null;
            var blocks = new SortedDictionary<int, byte[]>();
            foreach (var v in values)
            {
                int n;
                string name = v == null ? null : v.Name;
                if (name == null || name.Length == 0 || name.Length > 3 || !name.All(c => c >= '0' && c <= '9') || (name.Length > 1 && name[0] == '0')
                    || !int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out n))
                {
                    error = "it has a value named \"" + (name ?? "") + "\", which isn't a block number";
                    return null;
                }
                if (v.Data == null || v.Data.Length != Edid.BlockSize)
                {
                    error = "block " + n + " is " + (v.Data == null ? 0 : v.Data.Length) + " bytes, not " + Edid.BlockSize;
                    return null;
                }
                if (blocks.ContainsKey(n)) { error = "block " + n + " is there twice"; return null; }
                blocks[n] = v.Data;
            }
            var all = new byte[blocks.Count * Edid.BlockSize];
            int at = 0;
            foreach (var kv in blocks)
            {
                if (kv.Key != at) { error = "block " + at + " is missing"; return null; }
                Array.Copy(kv.Value, 0, all, at * Edid.BlockSize, Edid.BlockSize);
                at++;
            }
            return all;
        }

        /// <summary>An EDID as EDID_OVERRIDE's values: "0", "1", … with 128 bytes each.</summary>
        public static List<Value> OverrideValues(byte[] edid)
        {
            if (edid == null || edid.Length == 0 || edid.Length % Edid.BlockSize != 0)
                throw new ArgumentException("An EDID is whole 128-byte blocks.");
            var list = new List<Value>();
            for (int b = 0; b < edid.Length / Edid.BlockSize; b++)
            {
                var data = new byte[Edid.BlockSize];
                Array.Copy(edid, b * Edid.BlockSize, data, 0, Edid.BlockSize);
                list.Add(new Value { Name = b.ToString(CultureInfo.InvariantCulture), Data = data });
            }
            return list;
        }

        /// <summary>
        /// The EDID a new resolution is added to: the monitor's EDID_OVERRIDE when it has one (the user's own CRU
        /// edits stay), else its EDID. Null with the reason when neither can be used. An override that is there but
        /// can't be read as an EDID is never passed over for the plain EDID: writing would lose it.
        /// </summary>
        public static byte[] ChooseBase(Monitor m, out bool fromOverride, out string error)
        {
            fromOverride = false;
            error = null;
            if (m == null) { error = "The monitor isn't in the registry."; return null; }
            if (m.Values.Count > 0)
            {
                string why;
                byte[] over = OverrideBytes(m.Values, out why);
                if (over != null && Edid.Parse(over, out why) != null) { fromOverride = true; return Exact(over); }
                error = "This monitor already has an EDID override that can't be read (" + Sentence(why) + "). Display Switch won't write over it: open CRU to check it, or delete it there, then try again.";
                return null;
            }
            if (m.Edid == null || m.Edid.Length == 0) { error = "Windows has no EDID for this monitor in the registry."; return null; }
            string bad;
            if (Edid.Parse(m.Edid, out bad) == null)
            {
                error = "The monitor's EDID in the registry can't be used (" + Sentence(bad) + ").";
                return null;
            }
            return Exact(m.Edid);
        }

        /// <summary>Just the blocks the EDID announces: what Edid.Parse read, without what trails them.</summary>
        static byte[] Exact(byte[] edid) { return Edid.Parse(edid).Bytes; }

        static string Sentence(string s)
        {
            s = (s ?? "").Trim().TrimEnd('.');
            return s.Length == 0 ? "no details" : char.ToLowerInvariant(s[0]) + s.Substring(1);
        }

        /// <summary>SHA-256 of some bytes, in hex.</summary>
        public static string BytesHash(byte[] bytes)
        {
            using (var sha = SHA256.Create())
                return string.Concat(sha.ComputeHash(bytes ?? new byte[0]).Select(b => b.ToString("x2")).ToArray());
        }

        /// <summary>
        /// What the hash guard compares: the base's bytes and where they came from. A request made over the plain EDID
        /// doesn't pass once an override has appeared, even one with the very same bytes.
        /// </summary>
        public static string BaseHash(byte[] baseEdid, bool fromOverride)
        {
            var tagged = new byte[(baseEdid == null ? 0 : baseEdid.Length) + 1];
            tagged[0] = (byte)(fromOverride ? 1 : 0);
            if (baseEdid != null) Array.Copy(baseEdid, 0, tagged, 1, baseEdid.Length);
            return BytesHash(tagged);
        }

        /// <summary>What adding w × h at hz to this monitor would do. Never null: Error says why it can't be done.</summary>
        public static Plan PlanFor(Monitor m, int w, int h, double hz)
        {
            var plan = new Plan();
            plan.Base = ChooseBase(m, out plan.FromOverride, out plan.Error);
            if (plan.Base == null) return plan;
            plan.Hash = BaseHash(plan.Base, plan.FromOverride);
            string where;
            plan.After = Edid.AddResolution(plan.Base, w, h, hz, out where);
            if (plan.After == null) { plan.Error = where; return plan; }
            plan.Where = where;
            var before = Edid.Describe(plan.Base).Split('\n');
            var after = Edid.Describe(plan.After).Split('\n');
            plan.Added = Diff(after, before);
            plan.Removed = Diff(before, after);
            return plan;
        }

        /// <summary>The lines of a that b doesn't have (each line of b accounts for one of a).</summary>
        static List<string> Diff(string[] a, string[] b)
        {
            var left = b.ToList();
            var only = new List<string>();
            foreach (string line in a)
                if (!left.Remove(line)) only.Add(line.Trim());
            return only;
        }

        // ---- the files: request, backup, result ----
        //
        // Plain text, one "key=value" a line under a header line, so a person can read a backup and the next version can
        // add keys. A value never has a line break in it.

        const string RequestHead = "Display Switch EDID request 1";
        const string BackupHead = "Display Switch EDID backup 1";
        const string ResultHead = "Display Switch EDID result 1";

        static string OneLine(string s) { return (s ?? "").Replace("\r", " ").Replace("\n", " "); }

        static Dictionary<string, string> Fields(string text, string head, List<string> repeated, string repeatedKey)
        {
            if (text == null) return null;
            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            if (lines.Length == 0 || lines[0].Trim() != head) return null;
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 1; i < lines.Length; i++)
            {
                int eq = lines[i].IndexOf('=');
                if (eq <= 0) continue;
                string key = lines[i].Substring(0, eq), value = lines[i].Substring(eq + 1);
                if (repeated != null && key == repeatedKey) repeated.Add(value);
                else d[key] = value;
            }
            return d;
        }

        static string Get(Dictionary<string, string> d, string key)
        {
            string v;
            return d.TryGetValue(key, out v) ? v : null;
        }

        public static string Serialize(Request r)
        {
            return string.Join("\r\n", new[]
            {
                RequestHead,
                "action=" + (r.Undo ? "undo" : "apply"),
                "display=" + OneLine(r.Display),
                "name=" + OneLine(r.Name),
                "width=" + r.Width.ToString(CultureInfo.InvariantCulture),
                "height=" + r.Height.ToString(CultureInfo.InvariantCulture),
                "hz=" + r.Hz.ToString("R", CultureInfo.InvariantCulture),
                "base=" + OneLine(r.BaseHash),
                "cru=" + OneLine(r.CruFolder),
                "dir=" + OneLine(r.Dir),
            }) + "\r\n";
        }

        /// <summary>Reads a request. Null with the reason if it isn't one or asks for something that makes no sense.</summary>
        public static Request ParseRequest(string text, out string error)
        {
            error = null;
            var d = Fields(text, RequestHead, null, null);
            if (d == null) { error = "It isn't a Display Switch EDID request."; return null; }
            var r = new Request { Display = Get(d, "display"), Name = Get(d, "name") ?? "", BaseHash = Get(d, "base") ?? "", CruFolder = Get(d, "cru") ?? "", Dir = Get(d, "dir") ?? "" };
            string action = Get(d, "action");
            if (action != "apply" && action != "undo") { error = "It doesn't say whether to add or to remove."; return null; }
            r.Undo = action == "undo";
            if (KeyPath(r.Display) == null) { error = "It doesn't name a monitor."; return null; }
            if (r.Dir.Length == 0 || r.CruFolder.Length == 0) { error = "It doesn't say where CRU or the app's files are."; return null; }
            if (r.Undo) return r;
            if (!int.TryParse(Get(d, "width"), NumberStyles.None, CultureInfo.InvariantCulture, out r.Width)
                || !int.TryParse(Get(d, "height"), NumberStyles.None, CultureInfo.InvariantCulture, out r.Height)
                || !double.TryParse(Get(d, "hz"), NumberStyles.Float, CultureInfo.InvariantCulture, out r.Hz)
                || r.Width < 320 || r.Height < 200 || r.Width > 16384 || r.Height > 16384 || !(r.Hz >= 1 && r.Hz <= 1000))
            {
                error = "Its resolution or refresh rate makes no sense.";
                return null;
            }
            if (!Regex.IsMatch(r.BaseHash, "^[0-9a-f]{64}$")) { error = "It has no hash of the EDID it was made for."; return null; }
            return r;
        }

        public static string Serialize(Backup b)
        {
            var lines = new List<string>
            {
                BackupHead,
                "display=" + OneLine(b.Display),
                "name=" + OneLine(b.Name),
                "saved=" + OneLine(b.Saved),
                "existed=" + (b.Existed ? "yes" : "no"),
            };
            if (b.Written != null) lines.Add("written=" + b.Written);
            // A value: its name, a tab, its bytes in hex. (A registry value's name can be anything but a line break.)
            foreach (var v in b.Values) lines.Add("value=" + v.Name + "\t" + Edid.ToHex(v.Data).Replace(" ", "").Replace("\r", "").Replace("\n", ""));
            return string.Join("\r\n", lines.ToArray()) + "\r\n";
        }

        /// <summary>Reads a backup. Null with the reason if it isn't one, or isn't whole.</summary>
        public static Backup ParseBackup(string text, out string error)
        {
            error = null;
            var raw = new List<string>();
            var d = Fields(text, BackupHead, raw, "value");
            if (d == null) { error = "It isn't a Display Switch EDID backup."; return null; }
            var b = new Backup { Display = Get(d, "display"), Name = Get(d, "name") ?? "", Saved = Get(d, "saved") ?? "", Written = Get(d, "written") };
            if (KeyPath(b.Display) == null) { error = "It doesn't name a monitor."; return null; }
            string existed = Get(d, "existed");
            if (existed != "yes" && existed != "no") { error = "It doesn't say whether there was an override."; return null; }
            b.Existed = existed == "yes";
            foreach (string line in raw)
            {
                int tab = line.LastIndexOf('\t');
                byte[] data = null;
                if (tab >= 0)
                {
                    try { data = Edid.FromHex(line.Substring(tab + 1)); }
                    catch (Exception) { data = null; }
                }
                if (data == null) { error = "One of its values can't be read."; return null; }
                b.Values.Add(new Value { Name = line.Substring(0, tab), Data = data });
            }
            if (!b.Existed && b.Values.Count > 0) { error = "It says there was no override, yet has values."; return null; }
            return b;
        }

        public static string Serialize(Result r)
        {
            return string.Join("\r\n", new[] { ResultHead, "code=" + r.Code.ToString(CultureInfo.InvariantCulture), "where=" + OneLine(r.Where), "message=" + OneLine(r.Message) }) + "\r\n";
        }

        public static Result ParseResult(string text)
        {
            var d = Fields(text, ResultHead, null, null);
            int code;
            if (d == null || !int.TryParse(Get(d, "code"), NumberStyles.Integer, CultureInfo.InvariantCulture, out code)) return null;
            return new Result { Code = code, Where = Get(d, "where") ?? "", Message = Get(d, "message") ?? "" };
        }

        /// <summary>A backup of what the registry holds now.</summary>
        public static Backup BackupOf(Monitor m, string display, string name, DateTime at)
        {
            var b = new Backup { Display = display, Name = name ?? "", Saved = at.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), Existed = m.OverrideExisted };
            foreach (var v in m.Values) b.Values.Add(new Value { Name = v.Name, Data = (byte[])v.Data.Clone() });
            return b;
        }

        /// <summary>Whether a backup can hold these values exactly (a name with a line break in it can't be written down).</summary>
        static bool Storable(IList<Value> values)
        {
            return values.All(v => v.Name != null && v.Data != null && v.Name.IndexOfAny(new[] { '\r', '\n' }) < 0);
        }

        public static bool SameValues(IList<Value> a, IList<Value> b)
        {
            if (a.Count != b.Count) return false;
            foreach (var v in a)
            {
                var other = b.FirstOrDefault(x => x.Name == v.Name);
                if (other == null || !other.Data.SequenceEqual(v.Data)) return false;
            }
            return true;
        }

        /// <summary>The backups in the app's data folder: one per monitor Display Switch has added resolutions to. Never throws.</summary>
        public static List<Backup> Backups(string dir)
        {
            var list = new List<Backup>();
            try
            {
                string folder = BackupDir(dir);
                if (!Directory.Exists(folder)) return list;
                foreach (string file in Directory.GetFiles(folder, "*.txt").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    string error;
                    var b = ParseBackup(File.ReadAllText(file), out error);
                    if (b != null && string.Equals(BackupFile(dir, b.Display), file, StringComparison.OrdinalIgnoreCase)) list.Add(b);
                    else Guard.Log("Not a usable EDID backup: " + file + (error != null ? " (" + error + ")" : ""));
                }
            }
            catch (IOException e) { Guard.Log("Reading the EDID backups", e); }
            catch (UnauthorizedAccessException e) { Guard.Log("Reading the EDID backups", e); }
            return list;
        }

        // ---- CRU's folder ----

        /// <summary>CRU's tool that restarts the graphics driver: restart64.exe on 64-bit Windows, restart.exe on 32-bit.</summary>
        public static string RestartTool(string folder, bool os64)
        {
            return Path.Combine(folder, os64 ? "restart64.exe" : "restart.exe");
        }

        /// <summary>Null if this folder holds CRU (CRU.exe and the restart tool), else what is missing.</summary>
        public static string FolderProblem(string folder, bool os64)
        {
            try
            {
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return "That folder doesn't exist.";
                string tool = Path.GetFileName(RestartTool(folder, os64));
                bool cru = File.Exists(Path.Combine(folder, "CRU.exe")), restart = File.Exists(RestartTool(folder, os64));
                if (cru && restart) return null;
                return (!cru && !restart ? "CRU.exe and " + tool + " aren't" : !cru ? "CRU.exe isn't" : tool + " isn't")
                    + " in that folder. Choose the folder you unzipped CRU into.";
            }
            catch (Exception e)
            {
                return "That folder can't be read: " + e.Message;
            }
        }

        /// <summary>
        /// The folder CRU is in, for the one the user chose: that folder, or – when it isn't there but exactly one
        /// folder directly inside it holds it (the zip unpacked into a folder of its own) – that one.
        /// </summary>
        public static string FindFolder(string chosen, bool os64)
        {
            try
            {
                if (FolderProblem(chosen, os64) == null || string.IsNullOrEmpty(chosen) || !Directory.Exists(chosen)) return chosen;
                var inside = Directory.GetDirectories(chosen).Where(d => FolderProblem(d, os64) == null).ToList();
                return inside.Count == 1 ? inside[0] : chosen;
            }
            catch (Exception) { return chosen; }
        }

        // ---- the job itself: what the elevated copy does ----

        static string Want(Request r) { return r.Width + "x" + r.Height + "@" + r.Hz.ToString("0.###", CultureInfo.InvariantCulture); }

        static Result Fail(string what, int code, string message)
        {
            SwitchLog.Write(what, SwitchLog.Then, "failed", "code", code, "why", message);
            return new Result { Code = code, Message = message };
        }

        /// <summary>
        /// Adds the request's resolution to its monitor's EDID_OVERRIDE and restarts the driver. In order: read the
        /// registry; choose the base and check it is the one the user was shown; work out the new EDID; make sure the
        /// backup is there (written now if this is the first time); write; read back; restart the driver. Whatever stops
        /// it comes back as the result, and a write that went wrong is undone first.
        /// restart: runs CRU's restart tool and returns null, or why it didn't run.
        /// </summary>
        public static Result Apply(Request r, IStore store, Func<string> restart, DateTime now)
        {
            const string What = "edid-apply";
            string key = KeyPath(r.Display);
            SwitchLog.Write(What, "display", r.Display, "monitor", r.Name, "want", Want(r), "key", key);
            if (key == null || r.Undo) return Fail(What, BadRequest, "The request doesn't name a monitor.");

            Monitor m;
            try { m = store.Read(key); }
            catch (Exception e) { return Fail(What, WriteFailed, "The monitor's registry key can't be read: " + e.Message); }

            bool fromOverride;
            string error;
            byte[] baseEdid = ChooseBase(m, out fromOverride, out error);
            if (baseEdid == null) return Fail(What, CantAdd, error);
            string hash = BaseHash(baseEdid, fromOverride);
            SwitchLog.Write("edid-base", "from", fromOverride ? "EDID_OVERRIDE" : "EDID", "blocks", baseEdid.Length / Edid.BlockSize, "hash", hash.Substring(0, 12));
            if (hash != r.BaseHash)
                return Fail(What, Changed, "The monitor's EDID or its override changed since you were shown what would be added. Nothing was written: open the creator again.");

            string where;
            byte[] after = Edid.AddResolution(baseEdid, r.Width, r.Height, r.Hz, out where);
            if (after == null) return Fail(What, CantAdd, where);
            var values = OverrideValues(after);

            // The backup: the state before Display Switch's first write for this monitor. A later addition keeps it.
            string file = BackupFile(r.Dir, r.Display);
            Backup backup;
            try
            {
                Directory.CreateDirectory(BackupDir(r.Dir));
                if (File.Exists(file))
                {
                    backup = ParseBackup(File.ReadAllText(file), out error);
                    if (backup == null || !string.Equals(backup.Display, r.Display, StringComparison.OrdinalIgnoreCase))
                        return Fail(What, BackupFailed, "This monitor's backup (" + file + ") can't be read" + (error != null ? ": " + Sentence(error) : "") + ". Nothing was written.");
                    SwitchLog.Write("edid-backup", "file", file, SwitchLog.Then, "kept", "from", backup.Saved);
                }
                else
                {
                    if (!Storable(m.Values)) return Fail(What, BackupFailed, "The monitor's override has a value a backup can't hold. Nothing was written.");
                    backup = BackupOf(m, r.Display, r.Name, now);
                    string text = Serialize(backup);
                    if (!Guard.Save(file, text)) return Fail(What, BackupFailed, "The backup couldn't be written to " + file + ". Nothing was written to the registry.");
                    var check = ParseBackup(File.ReadAllText(file), out error);   // it has to be there, and whole, before anything is written
                    if (check == null || check.Existed != m.OverrideExisted || !SameValues(check.Values, m.Values))
                    {
                        Guard.Delete(file);
                        return Fail(What, BackupFailed, "The backup didn't read back as it was written. Nothing was written to the registry.");
                    }
                    SwitchLog.Write("edid-backup", "file", file, "overrideExisted", m.OverrideExisted, "values", m.Values.Count, SwitchLog.Then, "written");
                }
            }
            catch (Exception e) { return Fail(What, BackupFailed, "The backup couldn't be made (" + e.Message + "). Nothing was written to the registry."); }

            // The write, and the check that it took.
            try
            {
                store.Write(key, values);
                var readBack = store.Read(key);
                if (!readBack.OverrideExisted || !SameValues(readBack.Values, values)) throw new InvalidOperationException("the registry doesn't hold what was written");
            }
            catch (Exception e)
            {
                string put = PutBack(store, key, m);
                return Fail(What, WriteFailed, "The registry couldn't be written (" + e.Message + "). " + put);
            }
            SwitchLog.Write("edid-write", "key", key + @"\" + OverrideKey, "blocks", values.Count, "where", where, "hash", BytesHash(after).Substring(0, 12), SwitchLog.Then, "written");
            foreach (string line in Diff(Edid.Describe(after).Split('\n'), Edid.Describe(baseEdid).Split('\n'))) SwitchLog.Write("edid-diff", "added", line);

            backup.Written = BytesHash(after);
            if (!Guard.Save(file, Serialize(backup))) SwitchLog.Write("edid-backup", "file", file, SwitchLog.Then, "couldn't note what was written (the backup itself is unchanged)");

            string failed = RunRestart(restart);
            if (failed != null)
                return new Result { Code = RestartFailed, Where = where, Message = "The resolution is written, but the graphics driver wasn't restarted (" + failed + "). It appears after Windows restarts; Settings › Resolutions › Remove takes it out again." };
            SwitchLog.Write(What, "want", Want(r), "where", where, SwitchLog.Then, "done");
            return new Result { Code = Ok, Where = where, Message = "Added to the " + where + "." };
        }

        /// <summary>
        /// Removes what was added: EDID_OVERRIDE goes back to exactly what the backup holds (its values, or no key at all
        /// if there was none), the driver restarts, and the backup is deleted – it has done its work.
        /// </summary>
        public static Result Undo(Request r, IStore store, Func<string> restart)
        {
            const string What = "edid-undo";
            string key = KeyPath(r.Display), file = BackupFile(r.Dir ?? "", r.Display);
            SwitchLog.Write(What, "display", r.Display, "key", key, "backup", file);
            if (key == null || !r.Undo) return Fail(What, BadRequest, "The request doesn't name a monitor.");

            Backup backup;
            string error = null;
            try
            {
                if (!File.Exists(file)) return Fail(What, NoBackup, "There is no backup for this monitor: nothing to remove.");
                backup = ParseBackup(File.ReadAllText(file), out error);
            }
            catch (Exception e) { return Fail(What, NoBackup, "The backup can't be read: " + e.Message); }
            if (backup == null || !string.Equals(backup.Display, r.Display, StringComparison.OrdinalIgnoreCase))
                return Fail(What, NoBackup, "The backup (" + file + ") can't be read" + (error != null ? ": " + Sentence(error) : "") + ". Nothing was changed.");

            Monitor m;
            try { m = store.Read(key); }
            catch (Exception e) { return Fail(What, WriteFailed, "The monitor's registry key can't be read: " + e.Message); }
            string why;
            byte[] current = OverrideBytes(m.Values, out why);
            bool ours = backup.Written != null && current != null && BytesHash(current) == backup.Written;
            SwitchLog.Write("edid-current", "overrideExists", m.OverrideExisted, "values", m.Values.Count, "asWritten", ours, "restoring", backup.Existed ? backup.Values.Count + " values" : "no override");

            try
            {
                if (backup.Existed) store.Write(key, backup.Values);
                else store.Remove(key);
                var readBack = store.Read(key);
                if (readBack.OverrideExisted != backup.Existed || !SameValues(readBack.Values, backup.Values)) throw new InvalidOperationException("the registry doesn't hold what the backup has");
            }
            catch (Exception e)
            {
                return Fail(What, WriteFailed, "The registry couldn't be put back (" + e.Message + "). The backup is kept: " + file);
            }
            SwitchLog.Write("edid-restore", "key", key + @"\" + OverrideKey, SwitchLog.Then, backup.Existed ? "the override from before is back" : "the override is gone");
            if (!Guard.Delete(file)) SwitchLog.Write("edid-backup", "file", file, SwitchLog.Then, "couldn't be deleted");

            string failed = RunRestart(restart);
            if (failed != null)
                return new Result { Code = RestartFailed, Message = "The monitor's override is back as it was, but the graphics driver wasn't restarted (" + failed + "). Restart Windows to finish." };
            SwitchLog.Write(What, SwitchLog.Then, "done");
            return new Result { Code = Ok, Message = backup.Existed ? "The monitor's override is back as it was before." : "The added resolutions are gone." };
        }

        /// <summary>After a write that failed: the registry back to what it held (m). Says, for the user, how that went.</summary>
        static string PutBack(IStore store, string key, Monitor m)
        {
            try
            {
                if (m.OverrideExisted) store.Write(key, m.Values);
                else store.Remove(key);
                var now = store.Read(key);
                if (now.OverrideExisted == m.OverrideExisted && SameValues(now.Values, m.Values))
                {
                    SwitchLog.Write("edid-putback", SwitchLog.Then, "as it was");
                    return "It is as it was before.";
                }
            }
            catch (Exception e) { SwitchLog.Write("edid-putback", SwitchLog.Then, "failed", "why", e.Message); }
            return "It may be half written: use Settings › Resolutions › Remove, or CRU, to put it right.";
        }

        static string RunRestart(Func<string> restart)
        {
            string failed;
            try { failed = restart(); }
            catch (Exception e) { failed = e.Message; }
            SwitchLog.Write("edid-restart", SwitchLog.Then, failed == null ? "the driver restart ran" : "failed", "why", failed);
            return failed;
        }

        // ---- the real registry and the real restart ----

        /// <summary>HKLM, as it is. Reading works for everyone; writing needs administrator rights.</summary>
        public sealed class RegistryStore : IStore
        {
            public Monitor Read(string keyPath)
            {
                var m = new Monitor();
                using (var k = Registry.LocalMachine.OpenSubKey(keyPath))
                {
                    if (k == null) throw new InvalidOperationException(@"HKLM\" + keyPath + " isn't there");
                    m.Edid = k.GetValue("EDID") as byte[];
                    using (var o = k.OpenSubKey(OverrideKey))
                    {
                        if (o == null) return m;
                        m.OverrideExisted = true;
                        foreach (string name in o.GetValueNames())
                        {
                            var data = o.GetValue(name) as byte[];
                            if (data == null || o.GetValueKind(name) != RegistryValueKind.Binary)
                                throw new InvalidOperationException("EDID_OVERRIDE has a value that isn't binary (\"" + name + "\")");
                            m.Values.Add(new Value { Name = name, Data = data });
                        }
                    }
                }
                return m;
            }

            public void Write(string keyPath, IList<Value> values)
            {
                using (var k = Registry.LocalMachine.OpenSubKey(keyPath, true))
                {
                    if (k == null) throw new InvalidOperationException(@"HKLM\" + keyPath + " isn't there");
                    using (var o = k.CreateSubKey(OverrideKey))
                    {
                        foreach (string name in o.GetValueNames()) o.DeleteValue(name, false);
                        foreach (var v in values) o.SetValue(v.Name, v.Data, RegistryValueKind.Binary);
                    }
                }
            }

            public void Remove(string keyPath)
            {
                using (var k = Registry.LocalMachine.OpenSubKey(keyPath, true))
                {
                    if (k == null) throw new InvalidOperationException(@"HKLM\" + keyPath + " isn't there");
                    k.DeleteSubKeyTree(OverrideKey, false);
                }
            }
        }

        /// <summary>Reads a monitor from the registry without changing anything (no rights needed). Null with the reason if it can't be.</summary>
        public static Monitor ReadMonitor(string devicePath, out string error)
        {
            error = null;
            string key = KeyPath(devicePath);
            if (key == null) { error = "Windows gave no device path for this monitor."; return null; }
            try { return new RegistryStore().Read(key); }
            catch (Exception e)
            {
                error = "The monitor's registry key can't be read: " + e.Message;
                return null;
            }
        }

        /// <summary>Runs CRU's restart tool with /q (no questions) and waits for it. Null, or why it didn't run.</summary>
        static string RestartDriver(string folder)
        {
            bool os64 = Environment.Is64BitOperatingSystem;
            string problem = FolderProblem(folder, os64);
            if (problem != null) return problem;
            string tool = RestartTool(folder, os64);
            SwitchLog.Write("edid-restart", "tool", tool, "args", "/q");
            using (var p = Process.Start(new ProcessStartInfo(tool, "/q") { UseShellExecute = false, WorkingDirectory = folder }))
            {
                if (p == null) return Path.GetFileName(tool) + " didn't start";
                if (!p.WaitForExit(45000)) return Path.GetFileName(tool) + " didn't finish within 45 seconds";
                SwitchLog.Write("edid-restart", "tool", Path.GetFileName(tool), "exitCode", p.ExitCode);
                return null;
            }
        }

        /// <summary>
        /// The elevated copy: HdrSwitch.exe --apply-edid &lt;request file&gt; or --undo-edid &lt;request file&gt;, started through
        /// UAC by Elevate. Does the one job, writes &lt;request file&gt;.result, and returns the exit code. No tray, no lock.
        /// </summary>
        public static int Elevated(string[] args)
        {
            Result result;
            string resultFile = null;
            try
            {
                bool undo = args.Contains(UndoFlag);
                int at = Array.IndexOf(args, undo ? UndoFlag : ApplyFlag);
                string requestFile = at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
                if (requestFile == null || !File.Exists(requestFile)) return BadRequest;
                string error;
                var r = ParseRequest(File.ReadAllText(requestFile), out error);
                if (r != null && r.Undo != undo) { r = null; error = "It asks for the other thing than the command line does."; }
                if (r != null && (!Path.IsPathRooted(r.Dir) || !Directory.Exists(r.Dir))) { r = null; error = "The app's folder it names isn't there."; }
                if (r == null) result = new Result { Code = BadRequest, Message = "The request can't be used. " + error };
                else
                {
                    // The asking user's folder, also when another account's administrator approved the prompt.
                    Guard.LogFile = Path.Combine(r.Dir, "error.log");
                    SwitchLog.LogFile = Path.Combine(r.Dir, "display.log");
                    resultFile = requestFile + ".result";
                    string folder = r.CruFolder;
                    string problem = FolderProblem(folder, Environment.Is64BitOperatingSystem);
                    if (problem != null) result = Fail(undo ? "edid-undo" : "edid-apply", BadRequest, "CRU's folder (" + folder + "): " + problem + " Nothing was changed.");
                    else result = undo ? Undo(r, new RegistryStore(), () => RestartDriver(folder)) : Apply(r, new RegistryStore(), () => RestartDriver(folder), DateTime.Now);
                }
                if (resultFile == null) resultFile = requestFile + ".result";
            }
            catch (Exception e)
            {
                Guard.Log("EDID job", e);
                result = new Result { Code = Crashed, Message = "Something unexpected happened: " + e.Message };
            }
            try { if (resultFile != null) File.WriteAllText(resultFile, Serialize(result)); }
            catch (Exception e) { Guard.Log("Couldn't write " + resultFile, e); }
            return result.Code;
        }

        /// <summary>
        /// From the app: runs the request in an elevated copy of this exe (UAC asks), waits for it and returns what it
        /// said. waiting: called once the prompt was approved and the job runs. Declined at the prompt: Cancelled,
        /// nothing was changed. Blocks – call it from a background thread.
        /// </summary>
        public static Result Elevate(Request r, string exe, Action approved)
        {
            string what = r.Undo ? "edid-undo" : "edid-apply";
            string requestFile = Path.Combine(r.Dir, "edid-request-" + Guid.NewGuid().ToString("N") + ".txt"), resultFile = requestFile + ".result";
            try
            {
                Directory.CreateDirectory(r.Dir);
                if (!Guard.Save(requestFile, Serialize(r))) return new Result { Code = BadRequest, Message = "The request couldn't be written to " + r.Dir + ". Nothing was changed." };
                SwitchLog.Write("edid-elevate", "what", what, "display", r.Display, "request", requestFile);
                Process p;
                try
                {
                    p = Process.Start(new ProcessStartInfo(exe, (r.Undo ? UndoFlag : ApplyFlag) + " \"" + requestFile + "\"") { UseShellExecute = true, Verb = "runas" });
                }
                catch (Win32Exception e)
                {
                    bool declined = e.NativeErrorCode == 1223;   // ERROR_CANCELLED: No at the UAC prompt
                    SwitchLog.Write("edid-elevate", SwitchLog.Then, declined ? "declined at the prompt" : "failed", "why", e.Message);
                    return new Result { Code = declined ? Cancelled : NoAnswer, Message = declined ? "Not allowed at the administrator prompt. Nothing was changed." : "Windows couldn't start it as administrator (" + e.Message + "). Nothing was changed." };
                }
                using (p)
                {
                    if (approved != null) approved();
                    if (p == null || !p.WaitForExit(60000))
                    {
                        SwitchLog.Write("edid-elevate", SwitchLog.Then, "no answer within 60 s");
                        return new Result { Code = NoAnswer, Message = "It didn't finish within a minute. Settings › Switch log shows how far it got." };
                    }
                    Result result = null;
                    try { if (File.Exists(resultFile)) result = ParseResult(File.ReadAllText(resultFile)); }
                    catch (IOException e) { Guard.Log("Reading " + resultFile, e); }
                    if (result == null) result = new Result { Code = p.ExitCode == Ok ? NoAnswer : p.ExitCode, Message = "It ended without saying what it did (code " + p.ExitCode + "). Settings › Switch log shows how far it got." };
                    SwitchLog.Write("edid-elevate", "exitCode", p.ExitCode, SwitchLog.Then, result.Ok ? "done" : "failed", "why", result.Ok ? null : result.Message);
                    return result;
                }
            }
            catch (Exception e)
            {
                Guard.Log("EDID job (asking)", e);
                return new Result { Code = NoAnswer, Message = "It couldn't be started: " + e.Message };
            }
            finally
            {
                Guard.Delete(requestFile);
                Guard.Delete(resultFile);
            }
        }
    }
}
