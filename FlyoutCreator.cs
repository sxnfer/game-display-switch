// The resolution creator in the flyout: a view opened from a game's Competitive area ("Create…" on the card, "Create a
// size…" under its list) that makes a resolution by its diagonal – "24.5″ of this 26.5″ panel" – and adds it to the
// monitor, and the Settings rows that go with it (CRU's folder, and Remove for what was added).
//
// The view is a form (the size on a slider in 0.1″ steps, the shape, the refresh rate, with the result and a to-scale
// picture of the screen), then a confirmation that says what is about to happen, then the job's steps, then how it
// went. The work itself is Creator.cs: this file only gathers what to ask for and shows the answer. The job belongs to
// the app, not to the flyout, so it carries on – and its result is still there – when the flyout closes.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace HdrSwitch
{
    sealed partial class TrayApp
    {
        // ---- the app's side: CRU's folder and the job ----

        /// <summary>The folder with the user's copy of CRU (restart64.exe is run from it), or null: not chosen yet.</summary>
        string cruFolder = ReadText("CruFolder");

        /// <summary>A resolution being added to a monitor, or what was added being removed: where it is, and how it went.</summary>
        internal sealed class EdidJob
        {
            public bool Undo;            // removing instead of adding
            public bool Busy;
            public string Game;          // the game it is created for (its profile gets the size)
            public string Display;       // the monitor's device path
            public string Monitor;       // its name
            public string Gdi;
            public int Width, Height, Hz;   // Hz: the whole rate Windows lists the mode at
            public int ProfileHz;        // what the game's profile gets as its rate (0: the display's own)
            public string Step;          // what it is doing, while busy
            public string Done, Error;   // how it went: one of them, once it isn't busy
        }

        EdidJob edidJob;   // the latest one, kept until the next starts or its result has been seen

        static string ReadText(string name)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(SettingsKey))
                {
                    string v = k == null ? null : k.GetValue(name) as string;
                    return string.IsNullOrEmpty(v) ? null : v;
                }
            }
            catch (Exception e) { Guard.Log("Reading the setting " + name, e); return null; }
        }

        static void WriteText(string name, string value)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(SettingsKey))
                    k.SetValue(name, value ?? "", RegistryValueKind.String);
            }
            catch (Exception e) { Guard.Log("Saving the setting " + name, e); }
        }

        void SetCruFolder(string folder)
        {
            cruFolder = folder;
            WriteText("CruFolder", folder);
            SwitchLog.Write("cru-folder", "folder", folder);
        }

        /// <summary>Why a job can't start now, or null.</summary>
        string EdidJobBlocked()
        {
            if (edidJob != null && edidJob.Busy) return "Another change to a monitor is still running.";
            if (running.Count > 0)
                return "Close " + DisplayName(running.Values.First().Name) + " first: restarting the graphics driver under a running game can crash it.";
            string problem = Creator.FolderProblem(cruFolder, Environment.Is64BitOperatingSystem);
            return problem == null ? null : "CRU's folder: " + problem;
        }

        /// <summary>
        /// From the creator's confirmation: adds w × h at rate to the monitor (through UAC, in an elevated copy of the
        /// app), waits for the driver to come back, checks that Windows now lists the mode, and gives it to the game.
        /// </summary>
        void StartCreate(string game, string gdi, string display, string monitor, int w, int h, double rate, string baseHash, int profileHz)
        {
            var job = new EdidJob { Game = game, Gdi = gdi, Display = display, Monitor = monitor, Width = w, Height = h, Hz = (int)Math.Round(rate), ProfileHz = profileHz };
            string blocked = EdidJobBlocked();
            SwitchLog.Write("edid-create", "game", game, "display", display, "want", w + "x" + h + "@" + rate.ToString("0.###", CultureInfo.InvariantCulture), SwitchLog.Then, blocked == null ? "asking for administrator rights" : "not started", "why", blocked);
            edidJob = job;
            if (blocked != null) { job.Error = blocked; flyout.Sync(); return; }
            var r = new Creator.Request { Display = display, Name = monitor, Width = w, Height = h, Hz = rate, BaseHash = baseHash, CruFolder = cruFolder, Dir = Dir };
            RunEdidJob(job, r);
        }

        /// <summary>From Settings: removes what was added to this monitor (restores its backup) and restarts the driver.</summary>
        void StartUndo(Creator.Backup backup)
        {
            var job = new EdidJob { Undo = true, Display = backup.Display, Monitor = backup.Name };
            string blocked = EdidJobBlocked();
            SwitchLog.Write("edid-remove", "display", backup.Display, SwitchLog.Then, blocked == null ? "asking for administrator rights" : "not started", "why", blocked);
            edidJob = job;
            if (blocked != null) { job.Error = blocked; flyout.Sync(); return; }
            RunEdidJob(job, new Creator.Request { Undo = true, Display = backup.Display, Name = backup.Name, CruFolder = cruFolder, Dir = Dir });
        }

        void RunEdidJob(EdidJob job, Creator.Request r)
        {
            job.Busy = true;
            job.Step = "Waiting for administrator…";
            flyout.Sync();
            string exe = Application.ExecutablePath;
            new Thread(() =>
            {
                Creator.Result result;
                bool listed = false;
                try
                {
                    result = Creator.Elevate(r, exe, () => ui.Post(_ => { job.Step = "Restarting graphics driver…"; flyout.Sync(); }, null));
                    if (result.Ok && !job.Undo)
                    {
                        // The driver is back within a few seconds, and Windows lists the new mode once it is.
                        ui.Post(_ => { job.Step = "Checking…"; flyout.Sync(); }, null);
                        for (int i = 0; i < 30 && !listed; i++)
                        {
                            Thread.Sleep(500);
                            try { listed = Resolution.Available(job.Gdi, job.Width, job.Height, job.Hz); }
                            catch (Exception e) { Guard.Log("Asking for the new resolution", e); }
                        }
                    }
                }
                catch (Exception e)
                {
                    Guard.Log("EDID job", e);
                    result = new Creator.Result { Code = Creator.NoAnswer, Message = "Something unexpected happened: " + e.Message };
                }
                ui.Post(_ => FinishEdidJob(job, result, listed), null);
            }) { IsBackground = true, Name = "EDID job" }.Start();
        }

        void FinishEdidJob(EdidJob job, Creator.Result result, bool listed)
        {
            job.Busy = false;
            job.Step = null;
            string size = job.Width + " × " + job.Height + " at " + job.Hz + " Hz";
            if (job.Undo)
            {
                if (result.Ok) job.Done = result.Message;
                else job.Error = result.Message;
                SwitchLog.Write("edid-remove", "display", job.Display, SwitchLog.Then, result.Ok ? "removed" : "failed", "code", result.Code, "why", result.Ok ? null : result.Message);
            }
            else if (!result.Ok)
            {
                job.Error = result.Message;
                SwitchLog.Write("edid-create", "game", job.Game, "want", job.Width + "x" + job.Height + "@" + job.Hz, SwitchLog.Then, "failed", "code", result.Code, "why", result.Message);
            }
            else if (!listed)
            {
                job.Error = "It was written and the driver restarted, but Windows doesn't list " + size + ". A monitor that uses DSC can make the NVIDIA driver ignore it. Settings › Resolutions › Remove takes it out again.";
                SwitchLog.Write("edid-create", "game", job.Game, "want", job.Width + "x" + job.Height + "@" + job.Hz, "where", result.Where, SwitchLog.Then, "written, but Windows doesn't list the mode");
            }
            else
            {
                job.Done = size + " is ready.";
                SwitchLog.Write("edid-create", "game", job.Game, "want", job.Width + "x" + job.Height + "@" + job.Hz, "where", result.Where, SwitchLog.Then, "Windows lists the mode");
                try { SetProfile(job.Game, new Resolution.Mode { Width = job.Width, Height = job.Height, Hz = job.ProfileHz }); }
                catch (Exception e) { Guard.Log("Giving the game its new resolution", e); }
            }
            if (!flyout.ShowsJob(job)) Notify(job.Done ?? job.Error);   // closed, or on another view, meanwhile: the answer still arrives
            flyout.Sync();
            flyout.Reclaim();
        }

        // ---- the creator's arithmetic, kept apart from the window so the checks can run it ----

        internal static readonly string[] CreatorShapes = { "Native", "16:9", "16:10", "4:3", "5:4" };

        /// <summary>The shape a choice means; Native is the panel's own.</summary>
        internal static void CreatorShape(int index, int nativeW, int nativeH, out int aw, out int ah)
        {
            switch (index)
            {
                case 1: aw = 16; ah = 9; break;
                case 2: aw = 16; ah = 10; break;
                case 3: aw = 4; ah = 3; break;
                case 4: aw = 5; ah = 4; break;
                default: aw = nativeW; ah = nativeH; break;
            }
        }

        /// <summary>
        /// The slider's ends, in tenths: from 15″ (or a little under the panel, for a small one) to the panel's own
        /// diagonal. Without a known diagonal (inches 0) it is per cent of it instead: 50 to 100.
        /// </summary>
        internal static void CreatorRange(double inches, out int min, out int max)
        {
            if (inches <= 0) { min = 500; max = 1000; return; }
            max = Math.Max(10, (int)Math.Floor(inches * 10 + 1e-6));
            min = Math.Max(1, Math.Min(150, max - 10));
        }

        /// <summary>Where the slider starts: at the game's own area if it has one, else two inches under the panel (90 % without a diagonal).</summary>
        internal static int CreatorStart(double inches, Resolution.Mode area, int nativeW, int nativeH)
        {
            int min, max;
            CreatorRange(inches, out min, out max);
            double panel = inches > 0 ? inches : 100, at = inches > 0 ? inches - 2 : 90;
            if (area != null && nativeW > 0 && nativeH > 0)
                at = panel * Math.Sqrt((double)area.Width * area.Width + (double)area.Height * area.Height) / Math.Sqrt((double)nativeW * nativeW + (double)nativeH * nativeH);
            return Math.Max(min, Math.Min(max, (int)Math.Round(at * 10)));
        }

        /// <summary>
        /// The size for a place on the slider: the largest of that shape whose diagonal still reads as that many tenths
        /// (24.5″ gives 2368 × 1332 on a 26.5″ panel, which is 24.51″ – not the size below it). Null if nothing fits.
        /// </summary>
        internal static Edid.Size CreatorSize(int nativeW, int nativeH, double inches, int tenths, int shape)
        {
            int aw, ah;
            CreatorShape(shape, nativeW, nativeH, out aw, out ah);
            double panel = inches > 0 ? inches : 100;
            return Edid.SizeFor(nativeW, nativeH, panel, Math.Min(panel, tenths / 10.0 + 0.0499), aw, ah);
        }

        /// <summary>"2368 × 1332 · 24.51″ · 92.5 %"; without a known diagonal, "2368 × 1332 · 92.5 %".</summary>
        internal static string CreatorResult(Edid.Size s, bool inchesKnown)
        {
            if (s == null) return "No size of that shape fits";
            return s.Width + " × " + s.Height + (inchesKnown ? " · " + s.Inches.ToString("0.00", CultureInfo.InvariantCulture) + "″" : "")
                + " · " + s.Percent.ToString("0.0", CultureInfo.InvariantCulture) + " %";
        }

        /// <summary>"24.5″", or "92.5 %" without a known diagonal.</summary>
        internal static string CreatorTarget(int tenths, bool inchesKnown)
        {
            return (tenths / 10.0).ToString("0.0", CultureInfo.InvariantCulture) + (inchesKnown ? "″" : " %");
        }

        /// <summary>
        /// The refresh rates on offer: the display's rates at its native size (a GPU-scaled resolution needs one of
        /// those), one per whole number, highest first, at most four. chosen: the desktop's rate now if it is one of
        /// them (it is kept among the four), else the highest.
        /// </summary>
        internal static List<double> CreatorRates(IList<double> native, int currentHz, out double chosen)
        {
            var all = new List<double>();
            foreach (double r in (native ?? new double[0]).Where(x => x > 0).OrderByDescending(x => x))
                if (!all.Any(x => Math.Round(x) == Math.Round(r))) all.Add(r);
            chosen = 0;
            foreach (double r in all) if (Math.Abs(r - currentHz) < 0.5) chosen = r;
            var shown = all.Take(4).ToList();
            if (chosen > 0 && !shown.Contains(chosen)) shown[shown.Count - 1] = chosen;
            if (chosen == 0 && shown.Count > 0) chosen = shown[0];
            return shown;
        }

        /// <summary>The rate a game's profile gets for a resolution created at rate: none (the display's own) when that is the desktop's now.</summary>
        internal static int CreatorProfileHz(double rate, int currentHz)
        {
            int hz = (int)Math.Round(rate);
            return hz == currentHz ? 0 : hz;
        }

        /// <summary>The slider's place for the mouse at x on a track w wide.</summary>
        internal static int CreatorTenths(double x, double w, int min, int max)
        {
            if (w <= 0) return min;
            return Math.Max(min, Math.Min(max, min + (int)Math.Round((max - min) * x / w)));
        }

        /// <summary>The line under the result: what Create would do, or why there is nothing to do.</summary>
        internal static string CreatorStatus(Edid.Size s, int nativeW, int nativeH, bool exists, int hz)
        {
            if (s == null) return "Try a larger size or another shape";
            if (s.Width == nativeW && s.Height == nativeH) return "That is the whole screen";
            return exists ? "The display already has it at " + hz + " Hz" : "New · it will be added at " + hz + " Hz";
        }

        sealed partial class Flyout
        {
            /// <summary>The monitor the creator works on, as read when it opened.</summary>
            internal sealed class CreatorScreen
            {
                public string Gdi, Display, Name;
                public string Error;             // why nothing can be created on it
                public int NativeW, NativeH, CurrentHz;
                public double Inches;            // 0: the EDID doesn't say
                public List<double> Rates = new List<double>();   // at the native size, exact (143.912…)
                public Creator.Monitor Monitor;  // what the registry holds for it
            }

            CreatorScreen crScreen;
            int crTenths;         // the slider: the diagonal asked for, in tenths of an inch (of a per cent without a diagonal)
            int crShape;          // an index into CreatorShapes
            double crRate;        // the refresh rate chosen, exact
            string crStage;       // null: the form; "confirm": what is about to happen
            Creator.Plan crPlan;  // … and what would be written
            string crNote;        // what was wrong with the folder just chosen
            bool holdOpen;        // a dialog of its own is up: losing the focus to it doesn't close the flyout

            /// <summary>Stays open without the focus: under the UAC prompt and the driver restart of a job, or its own folder dialog.</summary>
            bool KeepOpen() { return holdOpen || (app.edidJob != null && app.edidJob.Busy); }

            /// <summary>After a job: the keyboard back in the flyout, if Windows allows (it lost the focus to the UAC prompt).</summary>
            internal void Reclaim()
            {
                if (!Visible || closing) return;
                try { Activate(); Native.SetForegroundWindow(Handle); }
                catch (Exception e) { Guard.Log("Bringing the flyout back", e); }
            }

            /// <summary>The flyout is open on the view that shows this job's result: the creator for its game, or Settings for a removal.</summary>
            internal bool ShowsJob(EdidJob job)
            {
                if (!Visible || closing) return false;
                return job.Undo ? tab == FlyoutTab.Settings && Depth() == 0 : sub == "create" && page == job.Game;
            }

            string CreatorMonitor() { return crScreen != null && crScreen.Name != null ? crScreen.Name : ""; }

            /// <summary>The job this view shows: the one creating a resolution for this game.</summary>
            EdidJob JobHere() { return app.edidJob != null && !app.edidJob.Undo && app.edidJob.Game == page ? app.edidJob : null; }

            /// <summary>Opens the creator from the control with this key. From the area's list, the list makes way: back is the page.</summary>
            void OpenCreator(string from)
            {
                CommitBrightness();
                if (app.edidJob != null && !app.edidJob.Busy) app.edidJob = null;   // an old result has been seen
                crScreen = ReadCreatorScreen();
                StartCreator();
                if (sub == "res")
                {
                    sub = "create";
                    ScrollHome();
                    wheel = 0;
                    entered = true;
                }
                else
                {
                    sub = "create";
                    Into(from);
                }
                Rebuild();
            }

            /// <summary>The form as it opens: the game's own size (or two inches under the panel), the panel's shape, the desktop's rate.</summary>
            void StartCreator()
            {
                var s = crScreen;
                crStage = null;
                crPlan = null;
                crNote = null;
                crShape = 0;
                Resolution.Mode area = null;
                if (page != null) app.profiles.TryGetValue(page, out area);
                crTenths = CreatorStart(s.Inches, area, s.NativeW, s.NativeH);
                var shown = CreatorRates(s.Rates, s.CurrentHz, out crRate);
                if (area != null && area.Hz > 0)   // the game's own rate, if the display has it
                    foreach (double r in shown) if ((int)Math.Round(r) == area.Hz) crRate = r;
            }

            /// <summary>What the creator needs to know about the display the resolutions are for. Never throws: Error says what is wrong.</summary>
            CreatorScreen ReadCreatorScreen()
            {
                var s = new CreatorScreen();
                if (sample != null)
                {
                    s.Name = sample.ScreenName ?? "LG 27GR95QE";
                    s.Display = @"\\?\DISPLAY#GSM5BBF#5&2a1b3c4&0&UID4352#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
                    s.NativeW = sample.Full.Width;
                    s.NativeH = sample.Full.Height;
                    s.Inches = sample.Inches;
                    s.CurrentHz = sample.Current.Hz;
                    s.Rates.AddRange(new[] { 239.97, 143.912, 119.998, 59.951 });
                    return s;
                }
                try
                {
                    s.Gdi = TargetGdi();
                    s.Display = Resolution.MonitorPath(s.Gdi);
                    var cur = Resolution.Current(s.Gdi);
                    s.CurrentHz = cur != null ? cur.Hz : 0;
                    s.Monitor = Creator.ReadMonitor(s.Display, out s.Error);
                    if (s.Monitor == null) return s;
                    bool fromOverride;
                    byte[] edid = Creator.ChooseBase(s.Monitor, out fromOverride, out s.Error);
                    if (edid == null) return s;
                    var p = Edid.Parse(edid);
                    if (p.Native == null) { s.Error = "The monitor's EDID doesn't name its native mode."; return s; }
                    s.Name = p.Name ?? p.Manufacturer + " " + p.Product.ToString("X4");
                    s.NativeW = p.Native.Width;
                    s.NativeH = p.Native.Height;
                    s.Inches = p.Inches > 0 ? p.Inches : Resolution.Inches(s.Gdi);
                    s.Rates = Edid.NativeRates(p);
                    SwitchLog.Write("creator-open", "display", s.Display, "monitor", s.Name, "native", s.NativeW + "x" + s.NativeH, "inches", Math.Round(s.Inches, 2),
                        "rates", string.Join(",", s.Rates.Select(r => r.ToString("0.###", CultureInfo.InvariantCulture)).ToArray()), "desktopHz", s.CurrentHz, "base", fromOverride ? "EDID_OVERRIDE" : "EDID");
                }
                catch (Exception e)
                {
                    Guard.Log("Reading the monitor for the creator", e);
                    s.Error = "The monitor couldn't be read: " + e.Message;
                }
                return s;
            }

            /// <summary>The render harness: the creator in the state the sample names.</summary>
            void SampleCreator()
            {
                crScreen = ReadCreatorScreen();
                StartCreator();
                string state = sample.Creator;
                if (state != "exists") crTenths = 250;   // a size the sample display doesn't have yet
                var size = CreatorSize(crScreen.NativeW, crScreen.NativeH, crScreen.Inches, crTenths, crShape);
                var job = new EdidJob { Game = page, Display = crScreen.Display, Monitor = crScreen.Name, Width = size.Width, Height = size.Height, Hz = (int)Math.Round(crRate) };
                app.edidJob = null;
                if (state == "confirm")
                {
                    crStage = "confirm";
                    crPlan = new Creator.Plan { Where = "base block, descriptor 4", Hash = "" };
                }
                else if (state == "busy") { job.Busy = true; job.Step = "Restarting graphics driver…"; app.edidJob = job; }
                else if (state == "done") { job.Done = size.Width + " × " + size.Height + " at " + job.Hz + " Hz is ready."; app.edidJob = job; }
                else if (state == "failed") { job.Error = "Not allowed at the administrator prompt. Nothing was changed."; app.edidJob = job; }
            }

            Edid.Size CreatorSizeNow()
            {
                var s = crScreen;
                return CreatorSize(s.NativeW, s.NativeH, s.Inches, crTenths, crShape);
            }

            /// <summary>The display lists this size at the rate chosen.</summary>
            bool CreatorExists(Edid.Size size)
            {
                if (size == null) return false;
                if (sample != null) return sample.Modes.Any(m => m.Width == size.Width && m.Height == size.Height);
                try { return Resolution.Available(crScreen.Gdi, size.Width, size.Height, (int)Math.Round(crRate)); }
                catch (Exception e) { Guard.Log("Asking for a resolution", e); return false; }
            }

            /// <summary>An arrow (±1), Page Up/Down (±10) or Home/End (±100) on the size slider: a tenth, a whole inch, an end.</summary>
            void StepCreator(int delta)
            {
                int min, max;
                CreatorRange(crScreen.Inches, out min, out max);
                crTenths = Math.Abs(delta) >= 100 ? (delta > 0 ? max : min) : Math.Max(min, Math.Min(max, crTenths + delta));
                Rebuild();
            }

            /// <summary>Back, inside the creator: from the confirmation or a failure to the form. False: nothing of its own to leave, the view closes.</summary>
            bool CreatorBack()
            {
                var job = JobHere();
                if (job != null && job.Busy) return false;   // the job carries on; its result is here when the view is opened again
                if (job != null)
                {
                    bool done = job.Done != null;
                    app.edidJob = null;
                    crStage = null;
                    if (done) return false;   // it is the game's size now: back to its page
                    focusKey = "cr:go";
                    Rebuild();
                    return true;
                }
                if (crStage != "confirm") return false;
                crStage = null;
                focusKey = "cr:go";
                Rebuild();
                return true;
            }

            void CloseCreator()
            {
                app.edidJob = null;
                crStage = null;
                sub = null;
                Out();
                Rebuild();
            }

            /// <summary>The folder dialog for CRU. A folder that holds CRU in a folder of its own inside it (the zip's) is fine too.</summary>
            void ChooseCruFolder()
            {
                if (sample != null) return;
                holdOpen = true;
                try
                {
                    using (var d = new FolderBrowserDialog { Description = "Choose the folder you unzipped CRU into: the one with CRU.exe and restart64.exe.", ShowNewFolderButton = false })
                    {
                        if (app.cruFolder != null) d.SelectedPath = app.cruFolder;
                        if (d.ShowDialog(this) == DialogResult.OK)
                        {
                            bool os64 = Environment.Is64BitOperatingSystem;
                            string folder = Creator.FindFolder(d.SelectedPath, os64);
                            crNote = Creator.FolderProblem(folder, os64);
                            if (crNote == null) app.SetCruFolder(folder);
                            else SwitchLog.Write("cru-folder", "folder", d.SelectedPath, SwitchLog.Then, "not CRU's", "why", crNote);
                        }
                    }
                }
                finally { holdOpen = false; }
                Reclaim();
                Rebuild();
            }

            bool CruReady()
            {
                if (sample != null) return app.cruFolder != null;
                return app.cruFolder != null && Creator.FolderProblem(app.cruFolder, Environment.Is64BitOperatingSystem) == null;
            }

            // ---- the view ----

            sealed class FootButton
            {
                public string Key, Text, Name;
                public bool Accent, Enabled = true;
                public Action Click;
            }

            /// <summary>
            /// The creator: its form, the confirmation, the job's steps or how it went, over a footer bar with the
            /// buttons for that stage. The body scrolls when the flyout can't be tall enough for it.
            /// </summary>
            int BuildCreator(int W, int M, int y)
            {
                int cw = W - 2 * M, rowCH = S(52);
                if (crScreen == null) { crScreen = ReadCreatorScreen(); StartCreator(); }
                var scr = crScreen;
                var job = JobHere();
                string stage = job != null && job.Busy ? "busy" : job != null && job.Done != null ? "done" : job != null && job.Error != null ? "failed"
                    : scr.Error != null ? "unreadable" : crStage ?? "form";
                int top = y, from = items.Count;
                y += S(4);
                var buttons = new List<FootButton>();
                string foot = "";
                string gameName = app.DisplayName(page);
                if (stage == "unreadable")
                {
                    y += InfoBar(new Rectangle(M, y, cw, 0), "cr:info", scr.Error, null);
                    foot = "Nothing can be created on this display";
                }
                else if (stage == "busy")
                {
                    y = CreatorBusy(M, cw, y, job);
                    foot = "This takes a few seconds";
                }
                else if (stage == "done")
                {
                    y = CreatorDone(M, cw, y, job, gameName);
                    buttons.Add(new FootButton { Key = "cr:close", Text = "Done", Name = "Done, back to " + gameName, Accent = true, Click = CloseCreator });
                }
                else if (stage == "failed")
                {
                    y += InfoBar(new Rectangle(M, y, cw, 0), "cr:info", job.Error, null);
                    foot = "See Settings › Switch log";
                    buttons.Add(new FootButton { Key = "cr:retry", Text = "Back", Name = "Back to the size", Click = () => CreatorBack() });
                }
                else if (stage == "confirm")
                {
                    y = CreatorConfirm(M, cw, y, scr, buttons);
                }
                else
                {
                    y = CreatorForm(M, cw, y, rowCH, scr, buttons, out foot);
                }
                int content = y - top + S(4);
                int barH = S(52);
                int shown = FlyoutBody(Where().WorkingArea.Height, S(12), top + S(8) + barH, content, rowCH);
                Scroller(from, new Rectangle(0, top, W, shown), content, rowCH);
                y = top + shown + S(8);

                // The footer bar: what the stage is about, and its buttons, the main one last.
                Color fbg = Footer(W, y, barH);
                int x = W - M;
                var rects = new List<Rectangle>();
                for (int i = buttons.Count - 1; i >= 0; i--)
                {
                    int bw = TextRenderer.MeasureText(buttons[i].Text, buttons[i].Accent ? fonts.Strong : fonts.Body, Size.Empty, TfLeft).Width + S(24);
                    rects.Insert(0, new Rectangle(x - bw, y + (barH - S(32)) / 2, bw, S(32)));
                    x -= bw + S(8);
                }
                if (foot.Length > 0)
                {
                    RollText roll = Roll("foot:creator", foot, fonts.Caption);
                    items.Add(new Item { Rect = new Rectangle(M + S(4), y, Math.Max(0, x - M - S(4)), barH), Paint = (g, rc, lk) => DrawRoll(g, roll, fonts.Caption, P.Ink(P.Text2, fbg), fbg, rc, -1) });
                }
                for (int i = 0; i < buttons.Count; i++)
                {
                    var b = buttons[i];
                    items.Add(new Item { Rect = rects[i], Key = b.Key, Name = b.Name ?? b.Text, Click = b.Enabled ? b.Click : null, Listed = !b.Enabled, Paint = (g, rc, lk) =>
                    {
                        if (b.Accent) AccentButton(g, rc, lk, fbg, b.Text, b.Enabled);
                        else StandardButton(g, rc, lk, fbg, null, b.Text);
                    } });
                }
                return y + barH;
            }

            /// <summary>
            /// The form: what CRU is needed for (until its folder is chosen), the screen to scale with the size in the
            /// middle and what it comes to, then the size, the shape and the refresh rate.
            /// </summary>
            int CreatorForm(int M, int cw, int y, int rowCH, CreatorScreen scr, List<FootButton> buttons, out string foot)
            {
                int G = S(8);
                bool cru = CruReady(), inches = scr.Inches > 0;
                var size = CreatorSizeNow();
                bool exists = CreatorExists(size);
                bool whole = size != null && size.Width == scr.NativeW && size.Height == scr.NativeH;
                int hz = (int)Math.Round(crRate);
                Color cbg = P.CardSolid;

                if (!cru && !exists)
                {
                    string text = crNote ?? (app.cruFolder != null ? "CRU isn't in " + app.cruFolder + " any more. Choose its folder again."
                        : "Needs CRU (free). Download it from monitortests.com, unzip it, then choose its folder.");
                    y += InfoBar(new Rectangle(M, y, cw, 0), "cr:cru", text, new[]
                    {
                        new FootButton { Key = "cr:download", Text = "Open download page", Click = () =>
                        {
                            if (sample != null) return;
                            Dismiss(true);   // at once: the browser is about to come to the front
                            try { Process.Start(Creator.CruPage); } catch (Exception e) { Guard.Log("Opening CRU's page", e); }
                        } },
                        new FootButton { Key = "cr:folder", Text = "Choose folder…", Click = ChooseCruFolder },
                    }) + G;
                }

                // The screen to scale, the size in the middle of it, and what it comes to.
                int pw = cw - 2 * S(46), ph = scr.NativeW > 0 ? (int)Math.Round((double)pw * scr.NativeH / scr.NativeW) : pw * 9 / 16;
                var card = new Rectangle(M, y, cw, S(12) + ph + S(10) + S(20) + S(18) + S(10));
                items.Add(new Item { Rect = card, Paint = (g, rc, lk) => CardBox(g, rc) });
                var full = new Resolution.Mode { Width = scr.NativeW, Height = scr.NativeH };
                var pr = new Rectangle(M + S(46), y + S(12), pw, ph);
                items.Add(new Item { Rect = pr, Paint = (g, rc, lk) =>
                {
                    var now = CreatorSizeNow();   // follows a drag
                    var area = now != null ? new Resolution.Mode { Width = now.Width, Height = now.Height } : null;
                    ScreenPicture(g, rc, AreaPreview(full, area, null), false, now != null ? now.Width + " × " + now.Height : "", null);
                } });
                string status = CreatorStatus(size, scr.NativeW, scr.NativeH, exists, hz);
                items.Add(new Item { Rect = new Rectangle(M + S(12), pr.Bottom + S(10), cw - S(24), S(38)), Key = "cr:result", Role = AccessibleRole.StaticText,
                    Name = CreatorResult(size, inches) + ", " + status, Paint = (g, rc, lk) =>
                {
                    var now = CreatorSizeNow();
                    bool same = now != null && size != null && now.Width == size.Width && now.Height == size.Height || now == null && size == null;
                    string line = same ? status : CreatorStatus(now, scr.NativeW, scr.NativeH, CreatorExists(now), hz);
                    Draw(g, CreatorResult(now, inches), fonts.Strong, P.Ink(P.Text1, cbg), new Rectangle(rc.X, rc.Y, rc.Width, S(20)), TfCenter);
                    Draw(g, line, fonts.Caption, P.Ink(P.Text2, cbg), new Rectangle(rc.X, rc.Y + S(20), rc.Width, S(18)), TfCenter);
                } });
                y = card.Bottom + S(4);

                Group(new Rectangle(M, y, cw, 3 * rowCH), 3);

                // The size: a slider from 15″ to the panel's own diagonal, a tenth at a time.
                int min, max;
                CreatorRange(scr.Inches, out min, out max);
                var row = new Rectangle(M, y, cw, rowCH);
                int valueW = S(46), sliderW = S(130);
                string of = inches ? "of " + scr.Inches.ToString("0.0", CultureInfo.InvariantCulture) + "″" : "of the diagonal";
                items.Add(new Item { Rect = row, Paint = (g, rc, lk) => RowText(g, rc, "area", "Screen size", of, 0, S(12) + valueW + S(8) + sliderW + S(12)) });
                var sr = new Rectangle(row.Right - S(12) - valueW - S(8) - sliderW, row.Y + (rowCH - S(32)) / 2, sliderW, S(32));
                int trackX = sr.X + S(9), trackW = sr.Width - S(18);
                items.Add(new Item { Rect = sr, Key = "cr:size", Role = AccessibleRole.Slider, Name = "Screen size", Radius = S(4), Listed = true, Click = () => { },
                    Value = CreatorTarget(crTenths, inches).Replace("″", " inches") + ", " + (size != null ? size.Width + " × " + size.Height : "no size"),
                    Tip = "Left and Right: a tenth. Page Up and Page Down: a whole one.",
                    Slide = x => { crTenths = CreatorTenths(x - trackX, trackW, min, max); Invalidate(); }, Paint = (g, rc, lk) =>
                {
                    float cy = rc.Y + rc.Height / 2f;
                    float tx0 = rc.X + S(9), tw = rc.Width - S(18);
                    float at = tx0 + tw * (max > min ? (float)(crTenths - min) / (max - min) : 1);
                    Theme.Fill(g, new RectangleF(tx0, cy - S(2), tw, S(4)), S(2), P.ControlStrong);
                    Theme.Fill(g, new RectangleF(tx0, cy - S(2), at - tx0, S(4)), S(2), P.Accent);
                    Theme.Dot(g, at, cy, S(18) + 2, P.HighContrast ? P.Text1 : Palette.Over(P.ControlStroke, cbg));
                    Theme.Dot(g, at, cy, S(18), P.Surface);
                    Theme.Dot(g, at, cy, S(12) + (S(14) - S(12)) * (float)Math.Max(0, Math.Min(1, lk.Hover)), P.Accent);
                    Draw(g, CreatorTarget(crTenths, inches), fonts.Body, P.Ink(P.Text1, cbg), new Rectangle(rc.Right + S(8), rc.Y, valueW, rc.Height), TfLeft | TextFormatFlags.Right);
                } });
                y += rowCH;

                // The shape and the refresh rate: segmented controls.
                var shapes = new List<KeyValuePair<string, Action>>();
                for (int i = 0; i < CreatorShapes.Length; i++)
                {
                    int index = i;
                    shapes.Add(new KeyValuePair<string, Action>(CreatorShapes[i], () => { crShape = index; Rebuild(); }));
                }
                Choice(new Rectangle(M, y, cw, rowCH), "stretch", "Shape", "Aspect ratio", "cr:shape:", S(40), shapes, crShape, i => CreatorShapes[i] + " shape");
                y += rowCH;
                double chosen;
                var rates = CreatorRates(scr.Rates, scr.CurrentHz, out chosen);
                var rateChoices = new List<KeyValuePair<string, Action>>();
                foreach (double r in rates)
                {
                    double rate = r;
                    rateChoices.Add(new KeyValuePair<string, Action>(Math.Round(r).ToString(CultureInfo.InvariantCulture), () => { crRate = rate; Rebuild(); }));
                }
                int rateAt = rates.FindIndex(r => r == crRate);
                Choice(new Rectangle(M, y, cw, rowCH), "clock", "Refresh rate", rates.Count == 0 ? "The display names none" : "In hertz", "cr:rate:", S(40), rateChoices, rateAt,
                    i => Math.Round(rates[i]) + " hertz");
                y += rowCH;

                // What the main button does: use a size that is there, or create one that isn't.
                foot = "";
                if (size != null && !whole && exists)
                {
                    foot = "No need to create it";
                    buttons.Add(new FootButton { Key = "cr:go", Text = "Use it", Name = "Use " + size.Width + " × " + size.Height + " for " + app.DisplayName(page), Accent = true, Click = () =>
                    {
                        string game = page;
                        var mode = new Resolution.Mode { Width = size.Width, Height = size.Height, Hz = CreatorProfileHz(crRate, scr.CurrentHz) };
                        sub = null;
                        Out();
                        app.SetProfile(game, mode);
                        Rebuild();
                    } });
                }
                else
                {
                    bool can = size != null && !whole && cru && crRate > 0;
                    foot = !cru ? "Needs CRU's folder first" : can ? "Added the way CRU does it" : "";
                    buttons.Add(new FootButton { Key = "cr:go", Text = "Create…", Name = "Create " + (size != null ? size.Width + " × " + size.Height : "a size"), Accent = true, Enabled = can, Click = () =>
                    {
                        crPlan = sample != null ? new Creator.Plan { Where = "base block, descriptor 4" } : Creator.PlanFor(scr.Monitor, size.Width, size.Height, crRate);
                        crStage = "confirm";
                        ScrollHome();
                        focusKey = "cr:cancel";
                        Rebuild();
                    } });
                }
                return y;
            }

            /// <summary>A card row with a segmented control: its icon and title, and the choices, the chosen one raised.</summary>
            void Choice(Rectangle row, string icon, string title, string desc, string key, int each, List<KeyValuePair<string, Action>> choices, int chosen, Func<int, string> name)
            {
                int n = choices.Count, segW = n * each + S(4), segH = S(28);
                items.Add(new Item { Rect = row, Paint = (g, rc, lk) => RowText(g, rc, icon, title, desc, 0, n > 0 ? S(12) + segW + S(12) : S(12)) });
                if (n == 0) return;
                var seg = new Rectangle(row.Right - S(12) - segW, row.Y + (row.Height - segH) / 2, segW, segH);
                items.Add(new Item { Rect = seg, Paint = (g, rc, lk) =>
                {
                    Theme.Fill(g, rc, S(4), P.ControlFill2);
                    Outline(g, rc, S(4), P.ControlStroke);
                } });
                for (int i = 0; i < n; i++)
                {
                    bool on = i == chosen;
                    string text = choices[i].Key;
                    items.Add(new Item { Rect = new Rectangle(seg.X + S(2) + i * each, seg.Y + S(2), each, segH - S(4)), Key = key + i, On = on, Role = AccessibleRole.RadioButton, Selected = on,
                        Radius = S(3), Name = title + ", " + name(i), Click = choices[i].Value, Paint = (g, rc, lk) => Segment(g, rc, lk, text) });
                }
            }

            /// <summary>
            /// The confirmation: what is about to happen, line by line, before anything is written. If the resolution
            /// can't be added (the EDID is full, the override can't be read), that instead, and no way on.
            /// </summary>
            int CreatorConfirm(int M, int cw, int y, CreatorScreen scr, List<FootButton> buttons)
            {
                var size = CreatorSizeNow();
                var plan = crPlan ?? new Creator.Plan { Error = "Nothing was worked out." };
                int hz = (int)Math.Round(crRate);
                buttons.Add(new FootButton { Key = "cr:cancel", Text = "Cancel", Click = () => CreatorBack() });
                if (plan.Error != null || size == null)
                {
                    y += InfoBar(new Rectangle(M, y, cw, 0), "cr:info", plan.Error ?? "No size of that shape fits.", null);
                    return y;
                }
                string title = "Create " + size.Width + " × " + size.Height + " at " + hz + " Hz?";
                var lines = new List<string>
                {
                    (plan.FromOverride ? "It is added to the EDID override " + scr.Name + " already has: your own CRU edits stay." : "It is added to " + scr.Name + "'s EDID override in the registry, the way CRU does it.")
                        + " It goes into the " + plan.Where + ".",
                    "The graphics driver restarts: the screen goes black for a few seconds.",
                    "If the picture isn't back after 15 seconds, press F8 for CRU's recovery mode.",
                    "A monitor that uses DSC can make the NVIDIA driver ignore it. Then nothing changes.",
                    "A backup is saved first. Settings › Resolutions › Remove puts everything back.",
                    "Windows asks for administrator permission.",
                };
                Color cbg = P.CardSolid;
                int pad = S(14), tx = pad + S(14), tw = cw - tx - pad;
                int h = S(12) + S(24) + S(4);
                var tops = new List<int>();
                var heights = new List<int>();
                foreach (string line in lines)
                {
                    int lh = TextRenderer.MeasureText(line, fonts.Caption, new Size(tw, 2000), TfWrap).Height;
                    tops.Add(h);
                    heights.Add(lh);
                    h += lh + S(8);
                }
                h += S(6);
                var card = new Rectangle(M, y, cw, h);
                items.Add(new Item { Rect = card, Paint = (g, rc, lk) => CardBox(g, rc) });
                items.Add(new Item { Rect = new Rectangle(M + pad, y + S(12), cw - 2 * pad, S(24)), Key = "cr:title", Role = AccessibleRole.StaticText, Name = title,
                    Paint = (g, rc, lk) => Draw(g, title, fonts.Strong, P.Ink(P.Text1, cbg), rc, TfLeft) });
                for (int i = 0; i < lines.Count; i++)
                {
                    string line = lines[i];
                    bool warn = i == 1 || i == 2;
                    items.Add(new Item { Rect = new Rectangle(M + pad, y + tops[i], cw - 2 * pad, heights[i]), Key = "cr:line:" + i, Role = AccessibleRole.StaticText, Name = line, Paint = (g, rc, lk) =>
                    {
                        Theme.Dot(g, rc.X + S(3), rc.Y + S(8), S(5), warn && !P.HighContrast ? P.Caution : P.Ink(P.Text3, cbg));
                        Draw(g, line, fonts.Caption, P.Ink(warn ? P.Text1 : P.Text2, cbg), new Rectangle(rc.X + S(14), rc.Y, rc.Width - S(14), rc.Height), TfWrap);
                    } });
                }
                buttons.Add(new FootButton { Key = "cr:create", Text = "Create and restart driver", Accent = true, Click = () =>
                {
                    if (sample != null) return;
                    crStage = null;
                    app.StartCreate(page, scr.Gdi, scr.Display, scr.Name, size.Width, size.Height, crRate, plan.Hash, CreatorProfileHz(crRate, scr.CurrentHz));
                    Rebuild();
                } });
                return y + h;
            }

            /// <summary>While the job runs: a spinner and the step it is at.</summary>
            int CreatorBusy(int M, int cw, int y, EdidJob job)
            {
                Color cbg = P.CardSolid;
                var card = new Rectangle(M, y, cw, S(120));
                string step = job.Step ?? "Working…";
                string what = "Creating " + job.Width + " × " + job.Height + " at " + job.Hz + " Hz";
                RollText roll = Roll("cr:step", step, fonts.Strong);
                items.Add(new Item { Rect = card, Key = "cr:busy", Busy = true, Role = AccessibleRole.StaticText, Name = step, Tip = what, Paint = (g, rc, lk) =>
                {
                    CardBox(g, rc);
                    Theme.Spinner(g, new PointF(rc.X + rc.Width / 2f, rc.Y + S(34)), S(24), Math.Max(2f, 2.5f * k), P.Ink(P.AccentText, cbg), Math.Max(0.35, lk.Busy));
                    DrawRoll(g, roll, fonts.Strong, P.Ink(P.Text1, cbg), cbg, new Rectangle(rc.X + S(12), rc.Y + S(58), rc.Width - S(24), S(22)), 0);
                    Draw(g, what + " · the screen may go black", fonts.Caption, P.Ink(P.Text2, cbg), new Rectangle(rc.X + S(12), rc.Y + S(82), rc.Width - S(24), S(18)), TfCenter);
                } });
                return card.Bottom;
            }

            /// <summary>It worked: the resolution is there and the game has it.</summary>
            int CreatorDone(int M, int cw, int y, EdidJob job, string gameName)
            {
                Color cbg = P.CardSolid;
                var card = new Rectangle(M, y, cw, S(120));
                string line = gameName + " uses it as its competitive area now.";
                items.Add(new Item { Rect = card, Key = "cr:done", Role = AccessibleRole.StaticText, Name = job.Done + " " + line, Paint = (g, rc, lk) =>
                {
                    CardBox(g, rc);
                    Color mark = P.HighContrast ? P.Accent : P.Success;
                    Theme.Dot(g, rc.X + rc.Width / 2f, rc.Y + S(34), S(28), mark);
                    LineIcon(g, "check", new Rectangle(rc.X + rc.Width / 2 - S(10), rc.Y + S(24), S(20), S(20)), P.HighContrast ? P.OnAccent : P.Surface);
                    Draw(g, job.Done, fonts.Strong, P.Ink(P.Text1, cbg), new Rectangle(rc.X + S(12), rc.Y + S(58), rc.Width - S(24), S(22)), TfCenter);
                    Draw(g, line, fonts.Caption, P.Ink(P.Text2, cbg), new Rectangle(rc.X + S(12), rc.Y + S(82), rc.Width - S(24), S(18)), TfCenter);
                } });
                return card.Bottom;
            }

            /// <summary>
            /// WinUI's InfoBar (warning): an icon, a text that wraps, and its buttons under the text. Read as text by a
            /// screen reader; the buttons are controls of their own. Returns its height.
            /// </summary>
            int InfoBar(Rectangle r, string key, string text, FootButton[] buttons)
            {
                bool light = P.Mode == Palette.Kind.Light;
                Color fill = P.HighContrast ? P.Surface : light ? Color.FromArgb(0xFF, 0xF4, 0xCE) : Color.FromArgb(0x43, 0x35, 0x19);   // SystemFillColorCautionBackground
                int tx = S(14) + S(20) + S(12), tw = r.Width - tx - S(12);
                int textH = TextRenderer.MeasureText(text, fonts.Caption, new Size(tw, 2000), TfWrap).Height;
                bool has = buttons != null && buttons.Length > 0;
                int h = Math.Max(S(48), textH + S(24)) + (has ? S(32) + S(10) : 0);
                int textTop = has ? S(12) : (h - textH) / 2;
                items.Add(new Item { Rect = new Rectangle(r.X, r.Y, r.Width, h), Key = key, Role = AccessibleRole.StaticText, Name = text, Paint = (g, rc, lk) =>
                {
                    Theme.Fill(g, rc, S(4), fill);
                    Outline(g, rc, S(4), P.HighContrast ? P.Text1 : P.CardStroke);
                    LineIcon(g, "info", new Rectangle(rc.X + S(14), rc.Y + S(12), S(20), S(20)), P.HighContrast ? P.Text1 : P.Caution);
                    Draw(g, text, fonts.Caption, P.Ink(P.Text1, fill), new Rectangle(rc.X + tx, rc.Y + textTop, tw, textH), TfWrap);
                } });
                if (!has) return h;
                // Under the text, from its left edge – further left when they wouldn't fit beside each other otherwise.
                int total = buttons.Sum(b => TextRenderer.MeasureText(b.Text, fonts.Body, Size.Empty, TfLeft).Width + S(24)) + (buttons.Length - 1) * S(8);
                int x = Math.Max(r.X + S(12), Math.Min(r.X + tx, r.Right - S(12) - total));
                foreach (var b in buttons)
                {
                    var button = b;
                    int bw = TextRenderer.MeasureText(b.Text, fonts.Body, Size.Empty, TfLeft).Width + S(24);
                    items.Add(new Item { Rect = new Rectangle(x, r.Y + h - S(12) - S(32), bw, S(32)), Key = b.Key, Name = b.Name ?? b.Text, Click = b.Click,
                        Paint = (g, rc, lk) => StandardButton(g, rc, lk, fill, null, button.Text) });
                    x += bw + S(8);
                }
                return h;
            }

            /// <summary>WinUI's accent button: the accent colour, Body Strong text on it. Not enabled: a dimmed standard button.</summary>
            void AccentButton(Graphics g, Rectangle rc, Look lk, Color bg, string text, bool enabled)
            {
                if (!enabled)
                {
                    Theme.Fill(g, rc, S(4), P.ControlFill);
                    Outline(g, rc, S(4), P.ControlStroke);
                    Draw(g, text, fonts.Strong, P.Ink(P.TextDisabled, Palette.Over(P.ControlFill, bg)), rc, TfCenter);
                    return;
                }
                Color fill = Theme.Mix(Theme.Mix(P.Accent, P.AccentHover, lk.Hover), P.AccentPress, lk.Press);
                Theme.Fill(g, rc, S(4), fill);
                if (P.HighContrast && lk.Hover > 0.5) Outline(g, rc, S(4), P.HoverStroke);
                Draw(g, text, fonts.Strong, P.Ink(P.OnAccent, Palette.Over(fill, bg)), rc, TfCenter);
            }

            /// <summary>
            /// The screen to scale: dark as a screen is, the area's outline 1:1 in the middle, a stretched resolution
            /// striped inside it at its own size, and one or two lines of text in the middle.
            /// </summary>
            void ScreenPicture(Graphics g, Rectangle rc, AreaPicture pic, bool stretched, string l1, string l2)
            {
                // The screen: dark in every mode, as a screen is; in high contrast the window colours.
                Color screen = P.HighContrast ? P.Surface : Color.FromArgb(0x0B, 0x0B, 0x0D);
                Color line = P.HighContrast ? P.Accent : Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF);
                Color ink = P.HighContrast ? P.Text1 : Color.White;
                Theme.Fill(g, rc, S(4), screen);
                Outline(g, rc, S(4), P.HighContrast ? P.Text1 : P.ControlStroke);
                Func<RectangleF, RectangleF> on_ = f => new RectangleF(rc.X + f.X * rc.Width, rc.Y + f.Y * rc.Height, f.Width * rc.Width, f.Height * rc.Height);
                var ar = on_(pic.Area);
                if (stretched)
                {
                    // The game's own resolution at its own size, which the GPU stretches over the area: stripes.
                    var sr2 = on_(pic.Stretch);
                    var st = g.Save();
                    g.SetClip(sr2, CombineMode.Intersect);
                    using (var b = new SolidBrush(P.HighContrast ? Theme.Fade(P.Text2, 0.3) : Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)))
                        for (float x = sr2.X; x < sr2.Right; x += S(10)) g.FillRectangle(b, x, sr2.Y, S(5), sr2.Height);
                    g.Restore(st);
                    using (var pen = new Pen(Theme.Fade(line, 0.55), 1) { DashStyle = DashStyle.Dash })
                        g.DrawRectangle(pen, sr2.X, sr2.Y, sr2.Width - 1, sr2.Height - 1);
                }
                if (ar.Width > 1 && ar.Height > 1)
                    using (var pen = new Pen(line, Math.Max(1.5f, 1.5f * k)))
                    using (var p = Theme.Round(RectangleF.Inflate(ar, -0.75f * k, -0.75f * k), S(2))) g.DrawPath(pen, p);
                int lh = S(16);
                int ly = rc.Y + (rc.Height - (l2 != null ? 2 * lh : lh)) / 2;
                Draw(g, l1, fonts.Caption, ink, new Rectangle(rc.X, ly, rc.Width, lh), TfCenter);
                if (l2 != null) Draw(g, l2, fonts.Caption, ink, new Rectangle(rc.X, ly + lh, rc.Width, lh), TfCenter);
            }

            // ---- Settings › Resolutions ----

            /// <summary>
            /// The Settings tab's Resolutions group: CRU's folder with Choose…, and for each monitor resolutions were
            /// added to, Remove – which restores its backup and restarts the driver. Returns where the group ends.
            /// </summary>
            int SettingsResolutions(int M, int cw, int y, int rowCH, int secH)
            {
                var backups = new List<Creator.Backup>();
                if (sample != null) foreach (string name in sample.Backups) backups.Add(new Creator.Backup { Name = name, Display = name });
                else backups = Creator.Backups(Dir);
                var job = app.edidJob != null && app.edidJob.Undo ? app.edidJob : null;
                bool jobShown = job == null || backups.Any(b => b.Display == job.Display);
                int rows = 1 + backups.Count + (jobShown ? 0 : 1);
                Section("Resolutions", M, y, secH);
                y += secH;
                Group(new Rectangle(M, y, cw, rows * rowCH), rows);

                // CRU's folder.
                var fr = new Rectangle(M, y, cw, rowCH);
                int chooseW = TextRenderer.MeasureText("Choose…", fonts.Body, Size.Empty, TfLeft).Width + S(24);
                bool ready = CruReady();
                string folderDesc = crNote ?? (app.cruFolder == null ? "Not set · needed to create resolutions" : ready ? app.cruFolder : "CRU isn't there any more: " + app.cruFolder);
                items.Add(new Item { Rect = fr, Key = "cru", Role = AccessibleRole.StaticText, Name = "CRU folder", Tip = folderDesc,
                    Paint = (g, rc, lk) => RowText(g, rc, "folder", "CRU folder", folderDesc, 0, S(12) + chooseW + S(12)) });
                items.Add(new Item { Rect = new Rectangle(fr.Right - S(12) - chooseW, fr.Y + (rowCH - S(32)) / 2, chooseW, S(32)), Key = "cru:choose", Name = "Choose CRU's folder",
                    Tip = "The folder you unzipped CRU into, from monitortests.com", Click = ChooseCruFolder, Paint = (g, rc, lk) => StandardButton(g, rc, lk, P.CardSolid, null, "Choose…") });
                y += rowCH;

                // What was added, per monitor.
                int removeW = TextRenderer.MeasureText("Remove", fonts.Body, Size.Empty, TfLeft).Width + S(24);
                for (int i = 0; i < backups.Count; i++)
                {
                    var b = backups[i];
                    var mine = job != null && job.Display == b.Display ? job : null;
                    bool busy = mine != null && mine.Busy;
                    string on = b.Name.Length > 0 ? "On " + b.Name : "On a monitor";
                    string desc = busy ? mine.Step : mine != null && mine.Error != null ? mine.Error : on;
                    var rr = new Rectangle(M, y, cw, rowCH);
                    items.Add(new Item { Rect = rr, Key = "added:" + i, Role = AccessibleRole.StaticText, Name = "Added resolutions, " + on, Tip = desc,
                        Paint = (g, rc, lk) => RowText(g, rc, "area", "Added resolutions", desc, 0, S(12) + removeW + S(12)) });
                    items.Add(new Item { Rect = new Rectangle(rr.Right - S(12) - removeW, rr.Y + (rowCH - S(32)) / 2, removeW, S(32)), Key = "added:remove:" + i, Busy = busy, Listed = busy,
                        Name = busy ? mine.Step : "Remove the resolutions added " + on.Substring(0, 1).ToLowerInvariant() + on.Substring(1),
                        Tip = "Puts the monitor's EDID override back as it was before Display Switch added to it, and restarts the graphics driver. Needs administrator permission.",
                        Click = busy || sample != null ? null : (Action)(() => { app.StartUndo(b); Rebuild(); }), Paint = (g, rc, lk) =>
                        {
                            if (lk.Busy < 0.5) { StandardButton(g, rc, lk, P.CardSolid, null, "Remove"); return; }
                            Theme.Fill(g, rc, S(4), P.ControlFill);
                            Outline(g, rc, S(4), P.ControlStroke);
                            Theme.Spinner(g, new PointF(rc.X + rc.Width / 2f, rc.Y + rc.Height / 2f), S(16), Math.Max(1.5f, 2 * k), P.Ink(P.Text1, P.CardSolid), lk.Busy);
                        } });
                    y += rowCH;
                }
                if (!jobShown)
                {
                    // The monitor's backup is gone (it was removed) or never was: the job's answer has a row of its own.
                    string desc = job.Busy ? job.Step : job.Done ?? job.Error;
                    items.Add(new Item { Rect = new Rectangle(M, y, cw, rowCH), Key = "added:result", Role = AccessibleRole.StaticText, Name = "Added resolutions, " + desc, Tip = desc,
                        Paint = (g, rc, lk) => RowText(g, rc, "area", "Added resolutions", desc, 0, S(12)) });
                    y += rowCH;
                }
                return y;
            }
        }
    }
}
