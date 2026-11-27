// Checks for the resolution creator (Creator.cs, and the arithmetic of its view in FlyoutCreator.cs): where a monitor
// is in the registry, EDID_OVERRIDE's values and the EDID they make, which EDID a resolution is added to, the request,
// backup and result files, and the whole of Apply and Undo – the hash guard, the backup, building on the user's own
// override, every way it can fail – against a registry made up in memory. Nothing here touches the real registry.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace HdrSwitch.Tests
{
    static partial class Checks
    {
        const string AsusPath = @"\\?\DISPLAY#AUS27BC#5&2a1b3c4&0&UID4352#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
        const string AsusKey = @"SYSTEM\CurrentControlSet\Enum\DISPLAY\AUS27BC\5&2a1b3c4&0&UID4352\Device Parameters";

        /// <summary>A registry in memory: monitors by key path. It can be told to fail a write half way.</summary>
        sealed class FakeRegistry : Creator.IStore
        {
            public readonly Dictionary<string, Creator.Monitor> Keys = new Dictionary<string, Creator.Monitor>(StringComparer.OrdinalIgnoreCase);
            public int Writes, Removes, FailWrites;

            static Creator.Monitor Copy(Creator.Monitor m)
            {
                var c = new Creator.Monitor { Edid = m.Edid == null ? null : (byte[])m.Edid.Clone(), OverrideExisted = m.OverrideExisted };
                foreach (var v in m.Values) c.Values.Add(new Creator.Value { Name = v.Name, Data = (byte[])v.Data.Clone() });
                return c;
            }

            public Creator.Monitor Read(string keyPath)
            {
                Creator.Monitor m;
                if (!Keys.TryGetValue(keyPath, out m)) throw new InvalidOperationException("no such key");
                return Copy(m);
            }

            public void Write(string keyPath, IList<Creator.Value> values)
            {
                var m = Keys[keyPath];
                m.OverrideExisted = true;
                m.Values.Clear();
                if (FailWrites > 0)
                {
                    FailWrites--;
                    if (values.Count > 0) m.Values.Add(new Creator.Value { Name = values[0].Name, Data = (byte[])values[0].Data.Clone() });   // half written
                    throw new UnauthorizedAccessException("access denied");
                }
                Writes++;
                foreach (var v in values) m.Values.Add(new Creator.Value { Name = v.Name, Data = (byte[])v.Data.Clone() });
            }

            public void Remove(string keyPath)
            {
                var m = Keys[keyPath];
                Removes++;
                m.OverrideExisted = false;
                m.Values.Clear();
            }
        }

        /// <summary>A registry with the one monitor: this EDID, and this override if given.</summary>
        static FakeRegistry RegistryWith(byte[] edid, byte[] over)
        {
            var reg = new FakeRegistry();
            var m = new Creator.Monitor { Edid = edid };
            if (over != null) { m.OverrideExisted = true; m.Values = Creator.OverrideValues(over); }
            reg.Keys[AsusKey] = m;
            return reg;
        }

        static double NativeRate(byte[] edid) { return Edid.NativeRates(Edid.Parse(edid))[0]; }

        /// <summary>A request to add 2368 × 1332 at the monitor's native rate, made over what the registry holds now.</summary>
        static Creator.Request RequestFor(FakeRegistry reg, string dir)
        {
            var m = reg.Read(AsusKey);
            bool fromOverride;
            string error;
            byte[] b = Creator.ChooseBase(m, out fromOverride, out error);
            return new Creator.Request { Display = AsusPath, Name = "PG27AQN", Width = 2368, Height = 1332, Hz = NativeRate(b), BaseHash = Creator.BaseHash(b, fromOverride), CruFolder = @"C:\Tools\CRU", Dir = dir };
        }

        static Creator.Request UndoFor(string dir)
        {
            return new Creator.Request { Undo = true, Display = AsusPath, Name = "PG27AQN", CruFolder = @"C:\Tools\CRU", Dir = dir };
        }

        static readonly DateTime CreatorNow = new DateTime(2026, 11, 27, 12, 3, 4);

        // ---- where a monitor is in the registry ----

        public static void CreatorKeyPath()
        {
            Run.Equal(AsusKey, Creator.KeyPath(AsusPath), "a monitor's device path names its key under Enum\\DISPLAY");
            Run.Equal(@"SYSTEM\CurrentControlSet\Enum\DISPLAY\DELA252\5&312b7513&0&UID4355\Device Parameters",
                Creator.KeyPath(@"\\?\DISPLAY#DELA252#5&312b7513&0&UID4355#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}"), "a Dell on another port");
            Run.Equal(@"SYSTEM\CurrentControlSet\Enum\DISPLAY\GSM5BBF\4&1f2e3d4c&0&UID200195\Device Parameters",
                Creator.KeyPath(@"\\?\display#GSM5BBF#4&1f2e3d4c&0&UID200195#{E6F07B5F-EE97-4A90-B076-33F57BF4EAA7}"), "lower-case prefix, upper-case class GUID");
            Run.Equal(@"SYSTEM\CurrentControlSet\Enum\DISPLAY\Default_Monitor\1&8713bca&0&UID0\Device Parameters",
                Creator.KeyPath(@"\\?\DISPLAY#Default_Monitor#1&8713bca&0&UID0#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}"), "a monitor without an ID of its own");
            foreach (string bad in new[]
            {
                null, "", @"\\.\DISPLAY1", "AUS27BC", @"\\?\DISPLAY#AUS27BC#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}",
                @"\\?\DISPLAY#AUS27BC#5&2a1b3c4&0&UID4352", @"\\?\DISPLAY#AUS27BC#5&2a1b3c4&0&UID4352#{not-a-guid}",
                @"\\?\DISPLAY#..#5&2a1b3c4&0&UID4352#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}",
                @"\\?\DISPLAY#AUS27BC\..\..\..\Services#5&2a#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}",
                @"\\?\DISPLAY#AUS27BC#5&2a/x#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}",
                @"\\?\USB#VID_046D#5&2a1b3c4#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}",
                @"\\?\DISPLAY#AUS27BC#5&2a1b3c4&0&UID4352#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}\extra",
                @"\\?\DISPLAY#AUS27BC#5&2a1b3c4&0&UID4352#extra#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}",
                @"\\?\DISPLAY# #5&2a1b3c4&0&UID4352#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}",
            })
            {
                Run.Equal(null, Creator.KeyPath(bad), "not a monitor's device path: " + (bad ?? "null"));
                Run.Equal(null, Creator.FileTag(bad), "… and no file name for it: " + (bad ?? "null"));
            }
            Run.Equal("AUS27BC_5&2a1b3c4&0&UID4352", Creator.FileTag(AsusPath), "the monitor as a file name");
            Run.Equal(Path.Combine(Path.Combine("data", "edid-backups"), "AUS27BC_5&2a1b3c4&0&UID4352.txt"), Creator.BackupFile("data", AsusPath), "where its backup goes");
            Run.Equal(null, Creator.BackupFile("data", @"\\.\DISPLAY1"), "no backup file for something that isn't a monitor");
        }

        // ---- EDID_OVERRIDE's values ----

        public static void CreatorOverrideValues()
        {
            foreach (byte[] edid in new[] { EdidAoc(), EdidAsrock(), EdidAsus() })
            {
                var values = Creator.OverrideValues(edid);
                Run.Equal(edid.Length / 128, values.Count, "one value per block");
                for (int i = 0; i < values.Count; i++)
                {
                    Run.Equal(i.ToString(), values[i].Name, "named by its block number");
                    Run.Equal(128, values[i].Data.Length, "128 bytes each");
                }
                string error;
                byte[] back = Creator.OverrideBytes(values, out error);
                Run.True(back != null && back.SequenceEqual(edid) && error == null, "and back to the same bytes");
                values.Reverse();   // the registry lists values in no particular order
                back = Creator.OverrideBytes(values, out error);
                Run.True(back != null && back.SequenceEqual(edid), "whatever order they come in");
            }
            // Twelve blocks: "10" and "11" come after "9", not after "1".
            var many = new List<Creator.Value>();
            for (int i = 0; i < 12; i++) many.Add(new Creator.Value { Name = i.ToString(), Data = Enumerable.Repeat((byte)i, 128).ToArray() });
            string why;
            byte[] all = Creator.OverrideBytes(many.OrderBy(v => v.Name, StringComparer.Ordinal).ToList(), out why);
            Run.True(all != null && all[9 * 128] == 9 && all[10 * 128] == 10 && all[11 * 128] == 11, "blocks are ordered as numbers");

            Run.True(Creator.OverrideBytes(new List<Creator.Value>(), out why) == null && why == null, "no values: no override, and no error");
            Run.True(Creator.OverrideBytes(null, out why) == null && why == null, "null: the same");
            var block = new byte[128];
            Func<string[], int[], string> rejected = (names, sizes) =>
            {
                var list = new List<Creator.Value>();
                for (int i = 0; i < names.Length; i++) list.Add(new Creator.Value { Name = names[i], Data = new byte[sizes[i]] });
                string error;
                return Creator.OverrideBytes(list, out error) == null ? error : null;
            };
            Run.True((rejected(new[] { "0", "2" }, new[] { 128, 128 }) ?? "").Contains("block 1 is missing"), "a gap is refused");
            Run.True((rejected(new[] { "1" }, new[] { 128 }) ?? "").Contains("block 0 is missing"), "no block 0 is refused");
            Run.True((rejected(new[] { "0", "1" }, new[] { 128, 127 }) ?? "").Contains("127 bytes"), "a short block is refused");
            Run.True((rejected(new[] { "0" }, new[] { 256 }) ?? "").Contains("256 bytes"), "a long block is refused");
            Run.True((rejected(new[] { "0" }, new[] { 0 }) ?? "").Contains("0 bytes"), "an empty block is refused");
            foreach (string name in new[] { "", "x", "00", "01", "-1", "1.0", " 1", "0x1", "1234" })
                Run.True((rejected(new[] { "0", name }, new[] { 128, 128 }) ?? "").Contains("isn't a block number"), "a value named \"" + name + "\" is refused");
            foreach (byte[] bad in new[] { null, new byte[0], new byte[127], new byte[129] })
            {
                bool threw = false;
                try { Creator.OverrideValues(bad); }
                catch (ArgumentException) { threw = true; }
                Run.True(threw, "an EDID that isn't whole blocks can't become values");
            }

        }

        // ---- which EDID a resolution is added to ----

        public static void CreatorChoosesBase()
        {
            bool fromOverride;
            string error;
            byte[] edid = EdidAsrock(), user = EdidWithFreeSlot();

            byte[] b = Creator.ChooseBase(new Creator.Monitor { Edid = edid }, out fromOverride, out error);
            Run.True(b != null && b.SequenceEqual(edid) && !fromOverride && error == null, "no override: the monitor's EDID");

            var m = new Creator.Monitor { Edid = edid, OverrideExisted = true, Values = Creator.OverrideValues(user) };
            b = Creator.ChooseBase(m, out fromOverride, out error);
            Run.True(b != null && b.SequenceEqual(user) && fromOverride, "an override: that, so the user's own CRU edits stay");

            b = Creator.ChooseBase(new Creator.Monitor { Edid = edid, OverrideExisted = true }, out fromOverride, out error);
            Run.True(b != null && b.SequenceEqual(edid) && !fromOverride, "an EDID_OVERRIDE key without values: the EDID");

            // Windows may keep fewer blocks in the registry than byte 126 announces.
            b = Creator.ChooseBase(new Creator.Monitor { Edid = edid.Take(128).ToArray() }, out fromOverride, out error);
            Run.True(b == null && error != null && error.Contains("can't be used"), "an EDID with blocks missing is refused, with Parse's reason: " + error);
            b = Creator.ChooseBase(new Creator.Monitor { Edid = edid.Concat(new byte[128]).ToArray() }, out fromOverride, out error);
            Run.True(b != null && b.SequenceEqual(edid), "bytes after the announced blocks are left out");
            Run.True(Creator.ChooseBase(new Creator.Monitor(), out fromOverride, out error) == null && error.Contains("no EDID"), "no EDID at all");
            Run.True(Creator.ChooseBase(null, out fromOverride, out error) == null && error != null, "no monitor");

            // An override that can't be read is never passed over for the plain EDID: writing would lose it.
            var broken = Creator.OverrideValues(user);
            broken[1].Data[5] ^= 0xFF;
            b = Creator.ChooseBase(new Creator.Monitor { Edid = edid, OverrideExisted = true, Values = broken }, out fromOverride, out error);
            Run.True(b == null && error.Contains("won't write over it"), "an override with a bad checksum: refused, not replaced: " + error);
            var gap = Creator.OverrideValues(user);
            gap.RemoveAt(1);
            b = Creator.ChooseBase(new Creator.Monitor { Edid = edid, OverrideExisted = true, Values = gap }, out fromOverride, out error);
            Run.True(b == null && error.Contains("block 1 is missing"), "an override with a block missing: refused: " + error);
            var partial = new List<Creator.Value> { Creator.OverrideValues(user)[0] };
            b = Creator.ChooseBase(new Creator.Monitor { Edid = edid, OverrideExisted = true, Values = partial }, out fromOverride, out error);
            Run.True(b == null && error.Contains("won't write over it"), "an override of only the first block: refused: " + error);
        }

        public static void CreatorHashes()
        {
            byte[] edid = EdidAsrock(), other = EdidAsrock();
            other[200] ^= 1;
            Run.Equal(64, Creator.BaseHash(edid, false).Length, "SHA-256 in hex");
            Run.Equal(Creator.BaseHash(edid, false), Creator.BaseHash((byte[])edid.Clone(), false), "the same bytes: the same hash");
            Run.True(Creator.BaseHash(edid, false) != Creator.BaseHash(other, false), "one bit off: another hash");
            Run.True(Creator.BaseHash(edid, false) != Creator.BaseHash(edid, true), "the same bytes from an override: another hash");
            Run.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", Creator.BytesHash(new byte[0]), "the SHA-256 of nothing");
        }

        // ---- the files ----

        public static void CreatorRequestFile()
        {
            var r = new Creator.Request
            {
                Display = AsusPath, Name = "ROG PG27AQN", Width = 2368, Height = 1332, Hz = 359.99712643678163, BaseHash = Creator.BaseHash(EdidAsus(), false),
                CruFolder = @"C:\Tools\CRU 1.5.2", Dir = @"C:\Users\Zoë\AppData\Roaming\HdrSwitch",
            };
            string error;
            var back = Creator.ParseRequest(Creator.Serialize(r), out error);
            Run.True(back != null, "a request reads back (" + error + ")");
            if (back == null) return;
            Run.True(!back.Undo && back.Display == r.Display && back.Name == r.Name && back.Width == 2368 && back.Height == 1332, "the monitor and the size");
            Run.True(back.Hz == r.Hz, "the rate, to the last digit");
            Run.True(back.BaseHash == r.BaseHash && back.CruFolder == r.CruFolder && back.Dir == r.Dir, "the hash and the folders");

            var undo = Creator.ParseRequest(Creator.Serialize(UndoFor(@"C:\data")), out error);
            Run.True(undo != null && undo.Undo && undo.Display == AsusPath, "a request to remove needs no size or hash");

            string text = Creator.Serialize(r);
            Run.Equal(null, Creator.ParseRequest(null, out error), "nothing");
            Run.Equal(null, Creator.ParseRequest("", out error), "an empty file");
            Run.Equal(null, Creator.ParseRequest("width=2368\r\n" + text, out error), "the header has to be the first line");
            Run.Equal(null, Creator.ParseRequest(text.Replace("action=apply", "action=format"), out error), "an action it doesn't know");
            Run.Equal(null, Creator.ParseRequest(text.Replace(AsusPath, @"\\.\DISPLAY1"), out error), "not a monitor");
            Run.Equal(null, Creator.ParseRequest(text.Replace("width=2368", "width=0"), out error), "no width");
            Run.Equal(null, Creator.ParseRequest(text.Replace("width=2368", "width=99999"), out error), "an absurd width");
            Run.Equal(null, Creator.ParseRequest(text.Replace("height=1332", "height=-1332"), out error), "a negative height");
            Run.Equal(null, Creator.ParseRequest(text.Replace("hz=359", "hz=x359"), out error), "a rate that isn't a number");
            Run.Equal(null, Creator.ParseRequest(text.Replace("hz=359", "hz=NaN"), out error), "a rate that is NaN");
            Run.Equal(null, Creator.ParseRequest(text.Replace("base=" + r.BaseHash, "base=1234"), out error), "no proper hash");
            Run.Equal(null, Creator.ParseRequest(text.Replace("cru=" + r.CruFolder, "cru="), out error), "no CRU folder");
            Run.Equal(null, Creator.ParseRequest(text.Replace("dir=" + r.Dir, "dir="), out error), "no data folder");
            Run.True(error != null, "… each with a reason");
            // A line break in a name can't smuggle in another line.
            r.Name = "PG27AQN\r\naction=undo";
            back = Creator.ParseRequest(Creator.Serialize(r), out error);
            Run.True(back != null && !back.Undo && back.Name == "PG27AQN  action=undo", "a line break in a value stays in the value");
        }

        public static void CreatorBackupFile()
        {
            var m = new Creator.Monitor { Edid = EdidAsus(), OverrideExisted = true, Values = Creator.OverrideValues(EdidAsus()) };
            m.Values.Add(new Creator.Value { Name = "", Data = new byte[0] });                      // the key's default value, empty
            m.Values.Add(new Creator.Value { Name = "note = mine\tok", Data = new byte[] { 1, 2, 255 } });   // something of the user's own
            var b = Creator.BackupOf(m, AsusPath, "ROG PG27AQN", CreatorNow);
            b.Written = Creator.BytesHash(EdidAsus());
            string error;
            var back = Creator.ParseBackup(Creator.Serialize(b), out error);
            Run.True(back != null, "a backup reads back (" + error + ")");
            if (back == null) return;
            Run.True(back.Existed && back.Display == AsusPath && back.Name == "ROG PG27AQN" && back.Saved == "2026-11-27 12:03:04" && back.Written == b.Written, "its facts");
            Run.Equal(m.Values.Count, back.Values.Count, "every value");
            for (int i = 0; i < m.Values.Count; i++)
                Run.True(back.Values[i].Name == m.Values[i].Name && back.Values[i].Data.SequenceEqual(m.Values[i].Data), "value \"" + m.Values[i].Name + "\" exactly, name and bytes");
            Run.True(Creator.SameValues(back.Values, m.Values) && Creator.SameValues(m.Values, back.Values), "SameValues agrees");

            var none = Creator.BackupOf(new Creator.Monitor { Edid = EdidAsus() }, AsusPath, "", CreatorNow);
            back = Creator.ParseBackup(Creator.Serialize(none), out error);
            Run.True(back != null && !back.Existed && back.Values.Count == 0 && back.Written == null, "no override before: says so, and has no values");
            var empty = Creator.BackupOf(new Creator.Monitor { OverrideExisted = true }, AsusPath, "", CreatorNow);
            back = Creator.ParseBackup(Creator.Serialize(empty), out error);
            Run.True(back != null && back.Existed && back.Values.Count == 0, "an empty EDID_OVERRIDE key before: existed, no values");

            string text = Creator.Serialize(b);
            Run.Equal(null, Creator.ParseBackup("", out error), "an empty file");
            Run.Equal(null, Creator.ParseBackup(Creator.Serialize(RequestFor(RegistryWith(EdidAsus(), null), "d")), out error), "a request isn't a backup");
            Run.Equal(null, Creator.ParseBackup(text.Replace("existed=yes", "existed=maybe"), out error), "it has to say whether there was an override");
            Run.Equal(null, Creator.ParseBackup(text.Replace("existed=yes", "existed=no"), out error), "no override, yet values: not whole");
            Run.Equal(null, Creator.ParseBackup(text.Replace("value=0\t00ff", "value=0\t00zz"), out error), "a value that isn't hex");
            Run.Equal(null, Creator.ParseBackup(text.Replace("value=1\t", "value=1 "), out error), "a value without its bytes");
            Run.Equal(null, Creator.ParseBackup(text.Replace(AsusPath, "DISPLAY1"), out error), "not a monitor");
            Run.True(!Creator.SameValues(m.Values, Creator.OverrideValues(EdidAsus())), "SameValues sees a value more");

            var res = Creator.ParseResult(Creator.Serialize(new Creator.Result { Code = Creator.Changed, Message = "It changed.\r\nReally.", Where = "base block, descriptor 2" }));
            Run.True(res != null && res.Code == Creator.Changed && !res.Ok && res.Message == "It changed.  Really." && res.Where == "base block, descriptor 2", "a result reads back");
            Run.Equal(null, Creator.ParseResult("code=0"), "a result needs its header");
            Run.Equal(null, Creator.ParseResult(null), "no result file");
        }

        // ---- the plan ----

        public static void CreatorPlan()
        {
            byte[] edid = EdidWithFreeSlot();
            var plan = Creator.PlanFor(new Creator.Monitor { Edid = edid }, 2368, 1332, NativeRate(edid));
            Run.True(plan.Error == null && plan.After != null && !plan.FromOverride, "a resolution that fits: a plan (" + plan.Error + ")");
            Run.Equal("base block, descriptor 2", plan.Where, "where it goes");
            Run.Equal(Creator.BaseHash(edid, false), plan.Hash, "the hash of the base it was made for");
            Run.True(plan.Added.Count == 1 && plan.Added[0].StartsWith("2: 2368 × 1332 @"), "what the EDID gains: " + string.Join(" | ", plan.Added.ToArray()));
            Run.True(plan.Removed.Count == 1 && plan.Removed[0] == "2: free", "what it loses: the free slot: " + string.Join(" | ", plan.Removed.ToArray()));
            Run.True(edid.SequenceEqual(EdidWithFreeSlot()), "the monitor's bytes aren't touched");

            plan = Creator.PlanFor(new Creator.Monitor { Edid = EdidAsrock(), OverrideExisted = true, Values = Creator.OverrideValues(edid) }, 2368, 1332, NativeRate(edid));
            Run.True(plan.Error == null && plan.FromOverride && plan.Base.SequenceEqual(edid), "over an override: built on it");

            plan = Creator.PlanFor(new Creator.Monitor { Edid = edid }, 2368, 1332, 77);
            Run.True(plan.After == null && plan.Error != null && plan.Error.Contains("77"), "a rate the display doesn't have: " + plan.Error);
            plan = Creator.PlanFor(new Creator.Monitor { Edid = edid }, 3840, 2160, NativeRate(edid));
            Run.True(plan.After == null && plan.Error.Contains("larger"), "larger than the panel: " + plan.Error);
            plan = Creator.PlanFor(new Creator.Monitor { Edid = new byte[128] }, 2368, 1332, 60);
            Run.True(plan.After == null && plan.Base == null && plan.Error != null, "an EDID that isn't one: " + plan.Error);
        }

        // ---- adding, and removing again ----

        public static void CreatorApplyAndUndo()
        {
            string dir = TempDir();
            SwitchLog.LogFile = Path.Combine(dir, "display.log");
            try
            {
                byte[] edid = EdidWithFreeSlot();
                var reg = RegistryWith(edid, null);
                int restarts = 0;
                Func<string> restart = () => { restarts++; return null; };
                var r = RequestFor(reg, dir);
                string file = Creator.BackupFile(dir, AsusPath);

                var res = Creator.Apply(r, reg, restart, CreatorNow);
                Run.True(res.Ok && res.Where == "base block, descriptor 2", "it is added: " + res.Code + " " + res.Message);
                string where;
                byte[] expected = Edid.AddResolution(edid, 2368, 1332, r.Hz, out where);
                var now = reg.Read(AsusKey);
                string error;
                byte[] written = Creator.OverrideBytes(now.Values, out error);
                Run.True(now.OverrideExisted && written != null && written.SequenceEqual(expected), "EDID_OVERRIDE holds the EDID with the resolution added");
                EdidSound(written, "what was written");
                Run.True(EdidFind(written, 2368, 1332, r.Hz) != null, "… which has 2368 × 1332 at the native rate");
                Run.True(now.Edid.SequenceEqual(edid), "the EDID value itself is never written");
                Run.Equal(1, restarts, "the driver was restarted once");
                Run.True(File.Exists(file), "a backup was written");
                var backup = Creator.ParseBackup(File.ReadAllText(file), out error);
                Run.True(backup != null && !backup.Existed && backup.Values.Count == 0 && backup.Display == AsusPath && backup.Saved == "2026-11-27 12:03:04", "it says there was no override before");
                Run.Equal(Creator.BytesHash(expected), backup == null ? null : backup.Written, "and notes what was written");
                Run.Equal(1, Creator.Backups(dir).Count, "the backup is listed for Settings");

                // The same request again: the base is the override now, so the request no longer matches.
                res = Creator.Apply(r, reg, restart, CreatorNow);
                Run.Equal(Creator.Changed, res.Code, "an old request is refused once the registry moved on");
                Run.Equal(1, restarts, "and the driver is left alone");

                // A second resolution: built on the first, and the first backup is kept.
                var r2 = RequestFor(reg, dir);
                r2.Width = 1920;
                r2.Height = 1080;
                res = Creator.Apply(r2, reg, restart, CreatorNow.AddDays(1));
                Run.True(res.Ok, "a second resolution: " + res.Code + " " + res.Message);
                written = Creator.OverrideBytes(reg.Read(AsusKey).Values, out error);
                EdidSound(written, "after the second");
                Run.True(EdidFind(written, 2368, 1332, r.Hz) != null && EdidFind(written, 1920, 1080, r.Hz) != null, "both are there");
                backup = Creator.ParseBackup(File.ReadAllText(file), out error);
                Run.True(backup != null && !backup.Existed && backup.Saved == "2026-11-27 12:03:04", "the backup is still the state before the first");
                Run.Equal(Creator.BytesHash(written), backup == null ? null : backup.Written, "with what was written last");

                // Remove: the key goes, the backup has done its work.
                res = Creator.Undo(UndoFor(dir), reg, restart);
                Run.True(res.Ok, "removing: " + res.Code + " " + res.Message);
                now = reg.Read(AsusKey);
                Run.True(!now.OverrideExisted && now.Values.Count == 0 && now.Edid.SequenceEqual(edid), "no override before: none after");
                Run.Equal(3, restarts, "the driver was restarted again");
                Run.True(!File.Exists(file) && Creator.Backups(dir).Count == 0, "the backup is deleted");
                res = Creator.Undo(UndoFor(dir), reg, restart);
                Run.Equal(Creator.NoBackup, res.Code, "nothing left to remove");
                Run.Equal(3, restarts, "and no restart for nothing");

                string log = File.ReadAllText(SwitchLog.LogFile);
                foreach (string what in new[] { " edid-apply ", " edid-base ", " edid-backup ", " edid-write ", " edid-diff ", " edid-restart ", " edid-undo ", " edid-restore " })
                    Run.True(log.Contains(what), "the switch log has a line for" + what.TrimEnd());
                Run.True(log.Contains("-> failed code=" + Creator.Changed) && log.Contains("-> failed code=" + Creator.NoBackup), "and the refusals with their codes");
            }
            finally { Clean(dir); }
        }

        public static void CreatorKeepsTheUsersOverride()
        {
            string dir = TempDir();
            SwitchLog.LogFile = Path.Combine(dir, "display.log");
            try
            {
                // The user's own CRU edit: the ASRock with its serial descriptor freed. The monitor's EDID is the plain one.
                byte[] edid = EdidAsrock(), user = EdidWithFreeSlot();
                var reg = RegistryWith(edid, user);
                var before = reg.Read(AsusKey);
                Func<string> restart = () => null;
                var r = RequestFor(reg, dir);
                Run.Equal(Creator.BaseHash(user, true), r.BaseHash, "the request is made over the override");

                var res = Creator.Apply(r, reg, restart, CreatorNow);
                Run.True(res.Ok, "added over the user's override: " + res.Code + " " + res.Message);
                string error, where;
                byte[] written = Creator.OverrideBytes(reg.Read(AsusKey).Values, out error);
                Run.True(written.SequenceEqual(Edid.AddResolution(user, 2368, 1332, r.Hz, out where)), "it is the user's override with the resolution added, not the monitor's EDID");
                Run.True(Edid.Parse(written).Descriptors[1].Kind == Edid.DescriptorKind.Timing && Edid.Parse(written).SerialText == null, "the user's edit (no serial) is still there");
                var backup = Creator.ParseBackup(File.ReadAllText(Creator.BackupFile(dir, AsusPath)), out error);
                Run.True(backup != null && backup.Existed && Creator.SameValues(backup.Values, before.Values), "the backup holds the user's override exactly");

                res = Creator.Undo(UndoFor(dir), reg, restart);
                Run.True(res.Ok, "removed: " + res.Message);
                var after = reg.Read(AsusKey);
                Run.True(after.OverrideExisted && Creator.SameValues(after.Values, before.Values) && Creator.SameValues(before.Values, after.Values), "the user's override is back, byte for byte");
            }
            finally { Clean(dir); }
        }

        public static void CreatorHashGuard()
        {
            string dir = TempDir();
            SwitchLog.LogFile = Path.Combine(dir, "display.log");
            try
            {
                byte[] edid = EdidWithFreeSlot();
                int restarts = 0;
                Func<string> restart = () => { restarts++; return null; };
                Action<string, FakeRegistry, Creator.Request> refused = (what, reg, r) =>
                {
                    var before = reg.Read(AsusKey);
                    var res = Creator.Apply(r, reg, restart, CreatorNow);
                    var after = reg.Read(AsusKey);
                    Run.Equal(Creator.Changed, res.Code, what + ": refused (" + res.Message + ")");
                    Run.True(after.OverrideExisted == before.OverrideExisted && Creator.SameValues(after.Values, before.Values) && reg.Writes == 0 && reg.Removes == 0, what + ": nothing written");
                    Run.True(!File.Exists(Creator.BackupFile(dir, AsusPath)), what + ": no backup made for a write that didn't happen");
                };

                // The user was shown the plain EDID; meanwhile they saved an override in CRU.
                var first = RegistryWith(edid, null);
                var request = RequestFor(first, dir);
                refused("an override appeared", RegistryWith(edid, EdidAsrock()), request);
                // … even one with the very same bytes as the EDID.
                refused("an override with the EDID's own bytes appeared", RegistryWith(edid, edid), request);
                // The override the user was shown was edited since.
                var over = RegistryWith(EdidAsrock(), edid);
                request = RequestFor(over, dir);
                byte[] edited = (byte[])edid.Clone();
                edited[40] ^= 1;
                Edid.FixBlock(edited, 0);
                refused("the override was edited", RegistryWith(EdidAsrock(), edited), request);
                refused("the override was deleted", RegistryWith(edid, null), request);
                // Another monitor on the port (same key): its EDID isn't the one the request was made for.
                request = RequestFor(first, dir);
                refused("the EDID changed", RegistryWith(EdidAsus(), null), request);
                // A request with someone else's hash.
                request = RequestFor(first, dir);
                request.BaseHash = Creator.BaseHash(new byte[0], false);
                refused("a made-up hash", RegistryWith(edid, null), request);
                Run.Equal(0, restarts, "the driver is never restarted for a refusal");

                // Unchanged: it goes through.
                var same = RegistryWith(edid, null);
                Run.True(Creator.Apply(RequestFor(first, dir), same, restart, CreatorNow).Ok, "the same EDID as shown: accepted");
            }
            finally { Clean(dir); }
        }

        public static void CreatorFailuresChangeNothing()
        {
            string dir = TempDir();
            SwitchLog.LogFile = Path.Combine(dir, "display.log");
            try
            {
                byte[] edid = EdidWithFreeSlot(), user = EdidAsrock();
                int restarts = 0;
                Func<string> restart = () => { restarts++; return null; };
                string file = Creator.BackupFile(dir, AsusPath);

                // Not a monitor / the wrong kind of request.
                var reg = RegistryWith(edid, null);
                var r = RequestFor(reg, dir);
                r.Display = @"\\.\DISPLAY1";
                Run.Equal(Creator.BadRequest, Creator.Apply(r, reg, restart, CreatorNow).Code, "not a monitor: a bad request");
                Run.Equal(Creator.BadRequest, Creator.Apply(UndoFor(dir), reg, restart, CreatorNow).Code, "Apply doesn't take a request to remove");
                Run.Equal(Creator.BadRequest, Creator.Undo(RequestFor(reg, dir), reg, restart).Code, "Undo doesn't take a request to add");

                // The monitor's key is gone (unplugged since).
                Run.Equal(Creator.WriteFailed, Creator.Apply(RequestFor(reg, dir), new FakeRegistry(), restart, CreatorNow).Code, "no such key: can't be read");

                // The resolution can't be added: no such rate, and it is there already.
                r = RequestFor(reg, dir);
                r.Hz = 77;
                var res = Creator.Apply(r, reg, restart, CreatorNow);
                Run.True(res.Code == Creator.CantAdd && res.Message.Contains("77"), "a rate the display doesn't have: " + res.Message);
                Run.True(reg.Writes == 0 && !File.Exists(file), "nothing written, no backup");

                // An override that can't be read: never written over.
                var broken = RegistryWith(edid, user);
                broken.Keys[AsusKey].Values[0].Data[10] ^= 0xFF;
                r = RequestFor(reg, dir);
                res = Creator.Apply(r, broken, restart, CreatorNow);
                Run.True(res.Code == Creator.CantAdd && res.Message.Contains("won't write over it") && broken.Writes == 0, "an unreadable override stays as it is: " + res.Message);

                // The backup can't be written (its folder's place is taken by a file): nothing goes to the registry.
                File.WriteAllText(Creator.BackupDir(dir), "in the way");
                res = Creator.Apply(RequestFor(reg, dir), reg, restart, CreatorNow);
                Run.True(res.Code == Creator.BackupFailed && reg.Writes == 0 && !reg.Read(AsusKey).OverrideExisted, "no backup, no write: " + res.Message);
                File.Delete(Creator.BackupDir(dir));

                // A backup that is there but can't be read: not replaced, and nothing written.
                Directory.CreateDirectory(Creator.BackupDir(dir));
                File.WriteAllText(file, "scribble");
                res = Creator.Apply(RequestFor(reg, dir), reg, restart, CreatorNow);
                Run.True(res.Code == Creator.BackupFailed && reg.Writes == 0 && File.ReadAllText(file) == "scribble", "an unreadable backup is left alone: " + res.Message);
                Run.Equal(Creator.NoBackup, Creator.Undo(UndoFor(dir), reg, restart).Code, "and Undo won't guess from it");
                Run.Equal(0, Creator.Backups(dir).Count, "nor is it listed");
                File.Delete(file);

                // The write fails half way (no rights): what was there is put back.
                var denied = RegistryWith(edid, user);
                var before = denied.Read(AsusKey);
                denied.FailWrites = 1;
                res = Creator.Apply(RequestFor(denied, dir), denied, restart, CreatorNow);
                var after = denied.Read(AsusKey);
                Run.True(res.Code == Creator.WriteFailed && res.Message.Contains("as it was before"), "a failed write says so: " + res.Message);
                Run.True(after.OverrideExisted && Creator.SameValues(after.Values, before.Values), "and the user's override is back");
                Run.True(File.Exists(file), "the backup made for it is kept");
                File.Delete(file);
                var deniedNew = RegistryWith(edid, null);
                deniedNew.FailWrites = 1;
                res = Creator.Apply(RequestFor(deniedNew, dir), deniedNew, restart, CreatorNow);
                Run.True(res.Code == Creator.WriteFailed && !deniedNew.Read(AsusKey).OverrideExisted, "with no override before, the half-written key is removed");
                File.Delete(file);
                Run.Equal(0, restarts, "none of these restarted the driver");

                // The restart tool fails: the override is written, and the result says what to do.
                foreach (bool throws in new[] { false, true })
                {
                    var written = RegistryWith(edid, null);
                    Func<string> noRestart = () => { if (throws) throw new InvalidOperationException("restart64.exe is missing"); return "restart64.exe didn't finish within 45 seconds"; };
                    res = Creator.Apply(RequestFor(written, dir), written, noRestart, CreatorNow);
                    Run.True(res.Code == Creator.RestartFailed && res.Message.Contains("restart64.exe") && res.Message.Contains("Remove"), "the restart failed: " + res.Message);
                    Run.True(written.Read(AsusKey).OverrideExisted && File.Exists(file), "the override and its backup are there");
                    res = Creator.Undo(UndoFor(dir), written, noRestart);
                    Run.True(res.Code == Creator.RestartFailed && !written.Read(AsusKey).OverrideExisted && !File.Exists(file), "Undo still restores, and says the restart is missing: " + res.Message);
                }

                // Undo whose write fails: the backup is kept for another try.
                var stuck = RegistryWith(edid, user);
                Run.True(Creator.Apply(RequestFor(stuck, dir), stuck, restart, CreatorNow).Ok, "added");
                stuck.FailWrites = 1;
                res = Creator.Undo(UndoFor(dir), stuck, restart);
                Run.True(res.Code == Creator.WriteFailed && File.Exists(file), "a failed restore keeps the backup: " + res.Message);
                res = Creator.Undo(UndoFor(dir), stuck, restart);
                Run.True(res.Ok && Creator.SameValues(stuck.Read(AsusKey).Values, Creator.OverrideValues(user)), "and the next try restores it");
            }
            finally { Clean(dir); }
        }

        // ---- CRU's folder ----

        public static void CreatorCruFolder()
        {
            string dir = TempDir();
            try
            {
                Run.Equal(Path.Combine("x", "restart64.exe"), Creator.RestartTool("x", true), "64-bit Windows: restart64.exe");
                Run.Equal(Path.Combine("x", "restart.exe"), Creator.RestartTool("x", false), "32-bit Windows: restart.exe");
                Run.True(Creator.FolderProblem(null, true) != null && Creator.FolderProblem("", true) != null, "no folder");
                Run.True(Creator.FolderProblem(Path.Combine(dir, "nowhere"), true).Contains("doesn't exist"), "a folder that isn't there");
                string cru = Path.Combine(dir, "cru-1.5.2");
                Directory.CreateDirectory(cru);
                Run.True(Creator.FolderProblem(cru, true).Contains("CRU.exe and restart64.exe aren't"), "an empty folder: both are missing");
                File.WriteAllText(Path.Combine(cru, "CRU.exe"), "");
                Run.True(Creator.FolderProblem(cru, true).Contains("restart64.exe isn't"), "CRU.exe alone: the restart tool is missing");
                File.WriteAllText(Path.Combine(cru, "restart.exe"), "");
                Run.True(Creator.FolderProblem(cru, true) != null, "restart.exe doesn't do on 64-bit Windows");
                Run.Equal(null, Creator.FolderProblem(cru, false), "… but is the one on 32-bit Windows");
                File.WriteAllText(Path.Combine(cru, "restart64.exe"), "");
                Run.Equal(null, Creator.FolderProblem(cru, true), "CRU.exe and restart64.exe: that is CRU's folder");
                File.Delete(Path.Combine(cru, "CRU.exe"));
                Run.True(Creator.FolderProblem(cru, true).Contains("CRU.exe isn't"), "the restart tool alone: CRU.exe is missing");
                File.WriteAllText(Path.Combine(cru, "CRU.exe"), "");

                Run.Equal(cru, Creator.FindFolder(cru, true), "the folder itself");
                Run.Equal(cru, Creator.FindFolder(dir, true), "the folder the zip was unpacked into, one level up");
                string second = Path.Combine(dir, "cru-old");
                Directory.CreateDirectory(second);
                File.WriteAllText(Path.Combine(second, "CRU.exe"), "");
                File.WriteAllText(Path.Combine(second, "restart64.exe"), "");
                Run.Equal(dir, Creator.FindFolder(dir, true), "two copies inside: not guessed");
                Run.Equal(null, Creator.FindFolder(null, true), "nothing chosen");
            }
            finally { Clean(dir); }
        }

        // ---- the view's arithmetic ----

        public static void CreatorSizes()
        {
            int min, max;
            TrayApp.CreatorRange(26.5, out min, out max);
            Run.True(min == 150 && max == 265, "a 26.5″ panel: 15.0″ to 26.5″, in tenths");
            TrayApp.CreatorRange(26.97, out min, out max);
            Run.True(min == 150 && max == 269, "26.97″: up to 26.9″");
            TrayApp.CreatorRange(15.6, out min, out max);
            Run.True(min == 146 && max == 156, "a small panel: an inch of range");
            TrayApp.CreatorRange(0, out min, out max);
            Run.True(min == 500 && max == 1000, "no diagonal known: 50 % to 100 %");

            Run.Equal(245, TrayApp.CreatorStart(26.5, null, 2560, 1440), "no area yet: two inches under the panel");
            Run.Equal(245, TrayApp.CreatorStart(26.5, new Resolution.Mode { Width = 2368, Height = 1332 }, 2560, 1440), "the game's own area: its diagonal, 24.5″");
            Run.Equal(199, TrayApp.CreatorStart(26.5, new Resolution.Mode { Width = 1920, Height = 1080 }, 2560, 1440), "1920 × 1080 on it: 19.9″");
            Run.Equal(150, TrayApp.CreatorStart(26.5, new Resolution.Mode { Width = 640, Height = 480 }, 2560, 1440), "never under the slider's start");
            Run.Equal(900, TrayApp.CreatorStart(0, null, 2560, 1440), "no diagonal: 90 %");
            Run.Equal(925, TrayApp.CreatorStart(0, new Resolution.Mode { Width = 2368, Height = 1332 }, 2560, 1440), "no diagonal, an area: its 92.5 %");

            var s = TrayApp.CreatorSize(2560, 1440, 26.5, 245, 0);
            Run.True(s != null && s.Width == 2368 && s.Height == 1332, "24.5″ of 26.5″: 2368 × 1332, which is 24.51″ – not the size below it");
            Run.Equal("2368 × 1332 · 24.51″ · 92.5 %", TrayApp.CreatorResult(s, true), "the result line");
            Run.Equal("2368 × 1332 · 92.5 %", TrayApp.CreatorResult(s, false), "… without a known diagonal");
            Run.Equal("No size of that shape fits", TrayApp.CreatorResult(null, true), "… with nothing to show");
            s = TrayApp.CreatorSize(2560, 1440, 27.0, 250, 0);
            Run.True(s != null && s.Width == 2368 && s.Height == 1332, "25″ of a 27″ panel: 2368 × 1332 too");
            Run.Equal("2368 × 1332 · 24.98″ · 92.5 %", TrayApp.CreatorResult(s, true), "at 24.98″");
            s = TrayApp.CreatorSize(2560, 1440, 26.5, 265, 0);
            Run.True(s != null && s.Width == 2560 && s.Height == 1440, "the slider's end: the whole panel");
            s = TrayApp.CreatorSize(2560, 1440, 26.5, 265, 3);
            Run.True(s != null && s.Width == 1920 && s.Height == 1440, "4:3 at the end: the panel's full height");
            s = TrayApp.CreatorSize(2560, 1440, 26.5, 245, 2);
            Run.True(s != null && s.Width % 8 == 0 && Math.Abs((double)s.Width / s.Height - 1.6) < 0.01 && s.Inches <= 24.55, "16:10 at 24.5″: " + TrayApp.CreatorResult(s, true));
            s = TrayApp.CreatorSize(2560, 1440, 26.5, 245, 4);
            Run.True(s != null && Math.Abs((double)s.Width / s.Height - 1.25) < 0.01 && s.Height <= 1440, "5:4 at 24.5″: " + TrayApp.CreatorResult(s, true));
            for (int t = 150; t <= 265; t++)
                for (int shape = 0; shape < TrayApp.CreatorShapes.Length; shape++)
                {
                    s = TrayApp.CreatorSize(2560, 1440, 26.5, t, shape);
                    if (s == null || s.Width > 2560 || s.Height > 1440 || s.Inches >= t / 10.0 + 0.05 || (s.Height < 1440 && s.Inches < t / 10.0 - 0.3))
                        Run.Fail("the size at " + t / 10.0 + "″, shape " + TrayApp.CreatorShapes[shape] + ": " + TrayApp.CreatorResult(s, true));
                }
            s = TrayApp.CreatorSize(2560, 1440, 0, 925, 0);
            Run.True(s != null && s.Width == 2368 && s.Height == 1332, "without a diagonal the slider is per cent: 92.5 % is 2368 × 1332");

            Run.Equal("24.5″", TrayApp.CreatorTarget(245, true), "the slider's value");
            Run.Equal("92.5 %", TrayApp.CreatorTarget(925, false), "… in per cent");
            Run.Equal(150, TrayApp.CreatorTenths(-5, 100, 150, 265), "left of the track: its start");
            Run.Equal(265, TrayApp.CreatorTenths(140, 100, 150, 265), "right of it: its end");
            Run.Equal(208, TrayApp.CreatorTenths(50, 100, 150, 265), "the middle");
            Run.Equal(150, TrayApp.CreatorTenths(50, 0, 150, 265), "no track");

            int aw, ah;
            TrayApp.CreatorShape(0, 3440, 1440, out aw, out ah);
            Run.True(aw == 3440 && ah == 1440, "Native is the panel's own shape");
            TrayApp.CreatorShape(2, 3440, 1440, out aw, out ah);
            Run.True(aw == 16 && ah == 10, "16:10");

            s = TrayApp.CreatorSize(2560, 1440, 26.5, 245, 0);
            Run.Equal("New · it will be added at 240 Hz", TrayApp.CreatorStatus(s, 2560, 1440, false, 240), "a size the display lacks");
            Run.Equal("The display already has it at 240 Hz", TrayApp.CreatorStatus(s, 2560, 1440, true, 240), "one it has");
            Run.Equal("That is the whole screen", TrayApp.CreatorStatus(TrayApp.CreatorSize(2560, 1440, 26.5, 265, 0), 2560, 1440, true, 240), "the panel's own size");
            Run.Equal("Try a larger size or another shape", TrayApp.CreatorStatus(null, 2560, 1440, false, 240), "nothing fits");
        }

        public static void CreatorRefreshRates()
        {
            double chosen;
            var asus = Edid.NativeRates(Edid.Parse(EdidAsus()));
            var rates = TrayApp.CreatorRates(asus, 360, out chosen);
            Run.True(rates.Count >= 1 && rates.Count <= 4 && Math.Round(rates[0]) == 360 && rates.SequenceEqual(rates.OrderByDescending(x => x)), "the display's native rates, highest first, at most four: "
                + string.Join(", ", rates.Select(x => x.ToString("0.###")).ToArray()));
            Run.True(Math.Round(chosen) == 360 && asus.Contains(chosen), "the desktop's rate, as the exact rate the EDID has");
            Run.True(Edid.MatchNativeRate(Edid.Parse(EdidAsus()), chosen) == chosen, "which AddResolution accepts as it is");

            rates = TrayApp.CreatorRates(new[] { 59.951, 239.97, 143.912, 119.998, 99.9, 60.0 }, 144, out chosen);
            Run.True(rates.SequenceEqual(new[] { 239.97, 143.912, 119.998, 99.9 }), "sorted, one per whole number, four at most");
            Run.Equal(143.912, chosen, "144 Hz on the desktop: 143.912");
            rates = TrayApp.CreatorRates(new[] { 59.951, 239.97, 143.912, 119.998, 99.9 }, 60, out chosen);
            Run.True(rates.SequenceEqual(new[] { 239.97, 143.912, 119.998, 59.951 }) && chosen == 59.951, "the desktop's rate is kept among the four");
            rates = TrayApp.CreatorRates(new[] { 59.951, 239.97 }, 165, out chosen);
            Run.Equal(239.97, chosen, "a desktop rate the display doesn't name: the highest");
            rates = TrayApp.CreatorRates(new double[0], 60, out chosen);
            Run.True(rates.Count == 0 && chosen == 0, "no rates: none chosen");
            rates = TrayApp.CreatorRates(null, 60, out chosen);
            Run.True(rates.Count == 0 && chosen == 0, "null: the same");

            Run.Equal(0, TrayApp.CreatorProfileHz(239.97, 240), "created at the desktop's rate: the profile follows the display");
            Run.Equal(144, TrayApp.CreatorProfileHz(143.912, 240), "created at another rate: the profile names it");
        }
    }
}
