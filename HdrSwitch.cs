// Display Switch (formerly HDR Switch) – a tiny tray app: Windows HDR turns on while a game from your list runs
// and goes back to SDR a few seconds after it closes.
//
// Games are found automatically (Steam, Epic, GOG, Ubisoft, EA, Battle.net, Riot, Rockstar, Xbox and game folders on every drive) and caught
// as soon as their process starts – Steam games even earlier, the moment Play is pressed – so HDR is on
// before the game checks for it. Only HDR this app turned on is ever switched back off.
//
// A game can also have a competitive resolution (resolutions.txt): while it runs the desktop switches to a
// smaller mode shown 1:1 in the middle of the panel, and goes back to the saved mode after it closes. Same
// rules: temporary, and only a mode this app set is ever put back. A game in borderless mode can also run at a
// lower resolution of its own, stretched over that smaller area by the GPU's scan-out scaler (the "stretch" part
// of a line in resolutions.txt; see Resolution.cs).
//
// Build with build.cmd (uses the C# compiler that ships with Windows / .NET Framework 4.8).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

[assembly: System.Reflection.AssemblyTitle("Display Switch")]
[assembly: System.Reflection.AssemblyProduct("Display Switch")]
// AssemblyVersion: written by build.cmd into Version.g.cs

namespace HdrSwitch
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            // An unexpected error mustn't end the app with HDR, a competitive resolution or NVIDIA scaling left switched on.
            // On the UI thread (clicks, timers, painting – nearly everything) it is logged and the app carries on, with no
            // crash dialog whose Quit would skip putting the displays back. On another thread .NET ends the process
            // whatever a handler does, so that one is logged and the displays are put back first, as far as that gets.
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => Guard.Log("Unexpected error", e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                Guard.Log("Unexpected error, closing", e.ExceptionObject as Exception);
                TrayApp.LastChance();
            };
            // --updated: started by the old version during an update – wait for it to exit instead of giving up.
            bool updated = args.Contains("--updated");
            if (args.Contains("--render-flyout"))   // draws the flyout over made-up state into a PNG (Flyout.cs): checks its look, even without Windows
            {
                Environment.ExitCode = TrayApp.RenderFlyout(args);
                return;
            }
            if (args.Contains("--render-island"))   // draws the island for one sample act into a PNG (Island.cs)
            {
                Environment.ExitCode = Island.RenderSample(args);
                return;
            }
            if (args.Contains("--island-demo"))   // a preview of the island alone: before the mutex, so it runs beside an installed copy
            {
                Application.EnableVisualStyles();
                Island.Demo();
                return;
            }
            using (var mutex = new Mutex(false, @"Local\HdrSwitch.SingleInstance"))
            {
                bool mine;
                try { mine = mutex.WaitOne(updated ? 15000 : 0); }
                catch (AbandonedMutexException) { mine = true; }
                if (!mine)
                {
                    MessageBox.Show("Display Switch is already running.", "Display Switch");
                    return;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                Application.Run(new TrayApp(updated, args.Contains("--open")));
                mutex.ReleaseMutex();
            }
        }
    }

    sealed partial class TrayApp : ApplicationContext
    {
        // How long to wait after the last game closes before SDR and the saved mode come back: it covers launchers that
        // restart the game. 3 s unless changed in the flyout; a launcher that takes longer needs one of the longer waits.
        internal static readonly int[] DelayChoices = { 3, 10, 30, 60 };
        int sdrDelay = ValidDelay(ReadInt("SdrDelay", 3));
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        // The app was called HDR Switch until 1.6.0. These names stay as they were so an update keeps the
        // startup entry, settings, game list and the single-instance lock of the copy it replaces.
        const string RunValue = "HDR Switch";
        const string SettingsKey = @"Software\HDR Switch";
        static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HdrSwitch");
        static readonly string ListFile = Path.Combine(Dir, "games.txt");
        static readonly string ProfilesFile = Path.Combine(Dir, "resolutions.txt");
        static readonly string StateFile = Path.Combine(Dir, "resolution.state");   // the mode this app set – for crash recovery
        static readonly string RecordersFile = Path.Combine(Dir, "recorders.txt");
        static readonly string BrightnessFile = Path.Combine(Dir, "brightness.txt");
        static readonly string BrightStateFile = Path.Combine(Dir, "brightness.state");   // the level this app set – for crash recovery
        static readonly string HdrStateFile = Path.Combine(Dir, "hdr.state");   // the displays this app switched to HDR – for crash recovery

        sealed class Running { public string Name; public RegisteredWaitHandle Wait; public WaitHandle Handle; }
        sealed class ActiveMode
        {
            public string Gdi;
            public string Id;                    // the display, as it is known after a reboot (see Hdr.Display.Id)
            public Resolution.Mode Mode, Back;   // Back: the saved mode it returns to
            public Resolution.Mode Area;         // the competitive area Mode is stretched over – or null: Mode is the area itself, shown 1:1
            public string Game;
            public int ScalingBack = -1;         // the NVIDIA scaling setting to put back, or -1 if this app didn't change it
            public bool ScalingPending;          // No scaling couldn't be taken (the desktop wasn't at its saved mode): tried again with the next re-apply
            public Resolution.Mode FellBack;     // the stretched resolution the profile asks for, when the plain area is used instead (Fallback.cs)
            public bool Unverified;              // a stretched mode that didn't show as stretched right after it was set: VerifyStretch looks again
            // Keeping it while the game runs (ModeKeeper.cs):
            public bool Centred;                 // a 1:1 mode was shown centred right after it was set: losing that is a reset
            public readonly List<long> Reapplied = new List<long>();   // when it was set again after a reset, for the budget
            public bool GaveUp;                  // the budget ran out, and the desktop hasn't been in this mode since
            public ModeKeeper.Verdict Last;      // the last decision about it, and the mode the desktop was in then
            public Resolution.Mode LastCurrent;
            /// <summary>"2368 × 1332", or "1776 × 1332 stretched to 2368 × 1332".</summary>
            public string Text { get { return Area == null ? Mode.Text : Mode.Text + " stretched to " + Area.Text; } }
        }

        readonly SynchronizationContext ui = SynchronizationContext.Current;
        readonly NotifyIcon tray = new NotifyIcon();
        readonly Icon iconOn = MakeIcon(true);
        readonly Icon iconOff = MakeIcon(false);
        readonly System.Windows.Forms.Timer sdrTimer = new System.Windows.Forms.Timer();
        readonly System.Windows.Forms.Timer scanTimer = new System.Windows.Forms.Timer { Interval = 1000 };     // catches games as their process starts
        readonly System.Windows.Forms.Timer steamTimeout = new System.Windows.Forms.Timer { Interval = 60000 }; // Play pressed, but no game appeared
        readonly System.Windows.Forms.Timer reloadTimer = new System.Windows.Forms.Timer { Interval = 300 };   // debounces edits to games.txt / resolutions.txt
        readonly System.Windows.Forms.Timer modeCheck = new System.Windows.Forms.Timer { Interval = 2000 };   // the display changed under us: still our mode?
        readonly FileSystemWatcher watcher;
        readonly SteamWatcher steam;
        readonly EventHandler onDisplayChanged;
        readonly HashSet<string> hinted = new HashSet<string>();      // games already told "restart if HDR is greyed out"
        readonly HashSet<string> resHinted = new HashSet<string>();   // games already told "restart if it doesn't fit"

        HashSet<string> games = new HashSet<string>();
        HashSet<string> excluded = new HashSet<string>();                 // "-name.exe" lines: never switch, never re-added by a scan
        Dictionary<string, string> labels = new Dictionary<string, string>();   // name → note shown in the list ("Steam: Cyberpunk 2077")
        Dictionary<string, Resolution.Mode> profiles = new Dictionary<string, Resolution.Mode>();   // name → competitive resolution (Hz 0: current rate)
        Dictionary<string, Resolution.Mode> stretches = new Dictionary<string, Resolution.Mode>();  // name → the game's own resolution, stretched over its competitive one
        Dictionary<string, string> profileLabels = new Dictionary<string, string>();               // notes from resolutions.txt, used if games.txt has none
        bool scanning;
        readonly Dictionary<int, Running> running = new Dictionary<int, Running>();
        readonly List<Hdr.Display> owned = new List<Hdr.Display>();   // displays this app switched to HDR
        ActiveMode res;                                               // the competitive mode this app set, while it is set
        Flyout flyout;
        Island island;
        bool islandOn = ReadSetting("Island", true);
        readonly Stopwatch grace = new Stopwatch();   // since the grace period began: the island counts it down
        string notice;            // the latest setup hint, kept in the flyout's status card until things settle
        Updater.Release update;   // a newer release, once found
        bool updating;
        string updateStep;        // while updating: the step under way, shown on the update button
        string updateError;       // why the last update didn't install
        byte[] updateExe;         // downloaded and checked, waiting to be swapped in once no game needs the displays
        bool restarting;          // "Restarting" is showing; the swap follows
        bool updateInstalled;     // swapped in but not started: it runs from the next start, so stop looking
        const string UpdateFailed = "Update failed";
        const string UpdateWaits = "After the game";   // the update button while a game holds the update back
        const int StepMs = 700;   // each update step stays on the button at least this long

        // Recording apps (process name → name shown). An HDR game captured into an SDR recording comes out washed out,
        // so with "SDR while recording" on, nothing switches HDR on while one of these runs. Only apps that are opened to
        // record or stream belong here: one that runs in the background all day (the NVIDIA overlay, Xbox Game Bar,
        // Discord, Medal) would keep HDR off all day. Anything else can go in recorders.txt.
        internal static readonly Dictionary<string, string> BuiltInRecorders = new Dictionary<string, string>
        {
            { "obs64", "OBS Studio" }, { "obs32", "OBS Studio" },
            { "streamlabs obs", "Streamlabs" }, { "streamlabs desktop", "Streamlabs" },
            { "xsplit.core", "XSplit" }, { "prismlivestudio", "PRISM Live Studio" }, { "twitchstudio", "Twitch Studio" },
            { "vmix64", "vMix" }, { "vmix", "vMix" }, { "bdcam", "Bandicam" }
        };
        Dictionary<string, string> recorders = new Dictionary<string, string>(BuiltInRecorders);   // the built-in ones and recorders.txt
        bool sdrWhileRecording = ReadSetting("SdrWhileRecording");
        string recorder;   // the recording app that is running, while "SDR while recording" is on

        /// <summary>"OBS Studio", or "medal.exe" for one from recorders.txt without a note.</summary>
        string RecorderName(string name)
        {
            string l;
            return recorders.TryGetValue(name, out l) && l.Length > 0 ? l : name + ".exe";
        }

        public TrayApp(bool updated, bool open)   // --open: show the flyout right away (development, screenshots)
        {
            current = this;
            uiThread = Thread.CurrentThread;
            Directory.CreateDirectory(Dir);
            Updater.CleanUp();
            bool firstRun = !File.Exists(ListFile);
            LoadList();
            LoadProfiles();
            LoadRecorders();
            LoadBrightness();
            flyout = new Flyout(this);
            // Clicks pass through the island while a game runs or is about to start.
            island = new Island(() => flyout.Toggle(), () => running.Count > 0 || steamTimeout.Enabled, IslandScreen, Notify, Trim);

            tray.MouseUp += (s, e) => { if (e.Button == MouseButtons.Left || e.Button == MouseButtons.Right) flyout.Toggle(); };
            tray.Visible = true;

            sdrTimer.Interval = sdrDelay * 1000;
            sdrTimer.Tick += (s, e) => { sdrTimer.Stop(); SwitchLog.Write("grace-fire", "afterSeconds", sdrDelay); Settle(); };
            scanTimer.Tick += (s, e) => ScanTick();
            steamTimeout.Tick += (s, e) =>
            {
                steamTimeout.Stop();
                SwitchLog.Write("steam-timeout", "steamGame", steamGame, "running", running.Count, SwitchLog.Then, "the game Steam was starting didn't appear within 60 s of Play: settling");
                steamGame = steamLabel = null;
                Settle();
            };
            reloadTimer.Tick += (s, e) => { reloadTimer.Stop(); LoadList(); LoadProfiles(); LoadRecorders(); LoadBrightness(); ForgetRemoved(); Rescan(); ReapplyProfiles(); };
            stretchVerify.Tick += (s, e) => VerifyStretch();
            modeCheck.Tick += (s, e) => { modeCheck.Stop(); OnDisplayChanged(false); };   // also Resume, unlock, after HDR, a retry: see CheckSoon
            LogSystemEvents(true);
            SwitchLog.Write("start", "version", Updater.CurrentText, "games", games.Count, "profiles", profiles.Count, "target", TargetGdi(), "why", "primary(a game's resolution goes on the screen the game was on last time, else here)",
                ModesOf(TargetGdi()), "hdr", HdrText());

            watcher = new FileSystemWatcher(Dir, "*.txt") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
            FileSystemEventHandler changed = (s, e) => ui.Post(_ => { reloadTimer.Stop(); reloadTimer.Start(); }, null);
            watcher.Changed += changed;
            watcher.Created += changed;
            watcher.Renamed += (s, e) => changed(s, e);
            watcher.EnableRaisingEvents = true;

            steam = new SteamWatcher(id => ui.Post(_ => OnSteamStarted(id), null));
            hotkey = new HotKey(() => SetPaused(!paused));
            onDisplayChanged = (s, e) => ui.Post(_ => { lastChange = keepClock.ElapsedMilliseconds; OnDisplayChanged(true); }, null);
            SystemEvents.DisplaySettingsChanged += onDisplayChanged;

            RecoverResolution();   // a previous run was killed while a game had its resolution
            RecoverHdr();          // … or its HDR
            RecoverBrightness();   // … or its SDR brightness
            Rescan();              // games that were already running
            if (!ProfiledRunning())
            {
                SwitchLog.Write("startup-revert", "ours", OursText(res), "strayScaling", strayScaling, SwitchLog.Then, "no profiled game is running: putting back what a previous run left");
                RevertResolution();
                RestoreStrayScaling();
            }
            StartGrace();          // HDR taken back from that run, with no game left that wants it: SDR after the usual wait
            SetScanRate();
            scanTimer.Start();
            UpdateTray();

            // Look for newly installed games: right away on first run, otherwise once, 30 s after startup.
            if (firstRun) ScanInBackground(true);
            else
            {
                var later = new System.Windows.Forms.Timer { Interval = 30000 };
                later.Tick += (s, e) => { later.Dispose(); ScanInBackground(false); };
                later.Start();
            }

            var trim = new System.Windows.Forms.Timer { Interval = 5000 };
            trim.Tick += (s, e) => { trim.Dispose(); Trim(); };
            trim.Start();

            if (updated) Announce(new Island.Act { Id = "update", Text = "Updated to " + Updater.CurrentText, Balloon = "Updated to " + Updater.CurrentText + "." });
            if (open)
            {
                var t = new System.Windows.Forms.Timer { Interval = 500 };
                t.Tick += (s, e) => { t.Dispose(); flyout.Toggle(); };
                t.Start();
            }

            // Updates: first look 1 minute after startup, then once a day.
            if (Updater.Enabled)
            {
                var updates = new System.Windows.Forms.Timer { Interval = 60000 };
                updates.Tick += (s, e) => { updates.Interval = 24 * 60 * 60 * 1000; CheckForUpdate(); };
                updates.Start();
            }
        }

        // ---- updates ----

        void CheckForUpdate()
        {
            if (update != null || updating || updateInstalled) return;
            new Thread(() =>
            {
                Updater.Release r = null;
                try { r = Updater.Latest(); } catch { }   // offline, rate-limited… try again tomorrow
                if (r != null) ui.Post(_ => OnUpdateFound(r), null);
            }) { IsBackground = true }.Start();
        }

        void OnUpdateFound(Updater.Release r)
        {
            update = r;
            flyout.Sync();
            Announce(new Island.Act { Id = "update", Title = "Update available", Sub = "Version " + r.VersionText, Text = "Update available",
                Balloon = "Version " + r.VersionText + " is available. Click the tray icon to update." });
        }

        void InstallUpdate()
        {
            if (update == null || updating) return;
            updating = true;
            updateError = null;
            updateStep = "Downloading";
            flyout.Sync();
            var r = update;
            new Thread(() =>
            {
                // The button rolls through the steps. Each stays up for a moment: the download is about 120 KB, and a
                // label that flashes past says nothing. null only waits out the step that is showing.
                var shown = Stopwatch.StartNew();
                Action<string> step = s =>
                {
                    int wait = StepMs - (int)shown.ElapsedMilliseconds;
                    if (wait > 0) Thread.Sleep(wait);
                    shown.Restart();
                    if (s != null) ui.Post(_ => { updateStep = s; flyout.Sync(); }, null);
                };
                string error = null;
                byte[] exe = null;
                try { exe = Updater.Download(r, step); }
                catch (Exception e) { error = e.Message; }
                step(null);   // "Verifying", or the step that failed, stays up for its moment too
                ui.Post(_ =>
                {
                    if (error != null) { UpdateFailedWith(error); return; }
                    updateExe = exe;
                    FinishUpdate();
                }, null);
            }) { IsBackground = true }.Start();
        }

        /// <summary>
        /// An update restarts the app, and the copy that exits puts back what it switched: HDR off, the saved resolution –
        /// in the middle of a game, and a game that only looks for HDR as it starts would not get it back. So the swap
        /// waits while a followed game runs, while the countdown back to SDR runs, and while Steam is starting a game.
        /// </summary>
        internal static bool UpdateMustWait(int gamesRunning, bool countingDown, bool steamStarting)
        {
            return gamesRunning > 0 || countingDown || steamStarting;
        }

        /// <summary>Swaps in the checked update once nothing holds it back – from the scan timer while something does.</summary>
        void FinishUpdate()
        {
            if (updateExe == null || restarting) return;
            if (UpdateMustWait(running.Count, sdrTimer.Enabled, steamTimeout.Enabled))
            {
                if (updateStep != UpdateWaits) { updateStep = UpdateWaits; flyout.Sync(); }
                return;
            }
            restarting = true;
            updateStep = "Restarting";
            flyout.Sync();
            var t = new System.Windows.Forms.Timer { Interval = StepMs };   // "Restarting" stays up for its moment
            t.Tick += (s, e) =>
            {
                t.Dispose();
                restarting = false;
                // A game may have started in that moment.
                if (UpdateMustWait(running.Count, sdrTimer.Enabled, steamTimeout.Enabled)) { FinishUpdate(); return; }
                byte[] exe = updateExe;
                updateExe = null;
                bool started;
                try { started = Updater.Swap(exe); }
                catch (Exception ex) { UpdateFailedWith(ex.Message); return; }
                if (started) { ExitThread(); return; }   // the new version waits for this copy to exit
                // In place, but it didn't start: this copy carries on, and the new one runs from the next start.
                updateInstalled = true;
                updating = false;
                updateStep = null;
                var v = update.VersionText;
                update = null;
                flyout.Sync();
                Announce(new Island.Act { Id = "update", Text = "Restart to finish", Title = "Updated to " + v, Sub = "Restart Display Switch to finish",
                    Balloon = "Updated to " + v + " – restart Display Switch to finish." });
            };
            t.Start();
        }

        void UpdateFailedWith(string error)
        {
            // The button says so for a moment before it opens up again with the reason underneath.
            updateStep = UpdateFailed;
            flyout.Sync();
            Announce(new Island.Act { Id = "update", Text = "Update failed", Dot = Island.Mark.Critical, Balloon = "Update failed: " + error });
            var back = new System.Windows.Forms.Timer { Interval = 1400 };
            back.Tick += (s2, e2) =>
            {
                back.Dispose();
                updating = false;
                updateStep = null;
                updateError = error;   // the flyout shows the reason under the update button
                flyout.Sync();
            };
            back.Start();
        }

        /// <summary>The line under the update button: why the last try failed, or what it is waiting for.</summary>
        internal string UpdateNote
        {
            get
            {
                if (updateError != null) return "Update failed: " + updateError;
                if (updating && updateStep == UpdateWaits) return "Downloaded and checked. It installs once the game has closed and the display is back.";
                return null;
            }
        }

        // ---- game tracking ----

        /// <summary>A game is followed if it is on the HDR list or has a competitive resolution.</summary>
        bool Followed(string name) { return games.Contains(name) || profiles.ContainsKey(name); }

        bool HdrRunning() { return running.Values.Any(r => games.Contains(r.Name)); }
        bool ProfiledRunning() { return running.Values.Any(r => profiles.ContainsKey(r.Name)); }

        /// <summary>Follows a running game: its resolution and/or HDR now, back once it closes.</summary>
        void Track(int pid, string name)
        {
            IntPtr h = Proc.OpenForWait(pid);
            var r = new Running { Name = name };
            if (h != IntPtr.Zero)   // otherwise its exit is noticed by the once-a-second process scan
            {
                r.Handle = new ProcessWaitHandle(h);
                r.Wait = ThreadPool.RegisterWaitForSingleObject(r.Handle,
                    (state, timedOut) => ui.Post(_ => Exited((int)state), null), pid, Timeout.Infinite, true);
            }
            running[pid] = r;
            // How long it has been running: its start time, or – for a game Windows won't let us open – the time since the
            // last look at the process list that didn't have it.
            double real = Proc.AgeSeconds(pid);
            long absent = AbsentAt(pid);
            long age = Follow.AgeMs(real, keepClock.ElapsedMilliseconds, absent);
            SwitchLog.Write("scan-detected", "pid", pid, "exe", name, "ageMs", Follow.AgeText(age), "ageFrom", Follow.AgeSource(real, absent), "scanEveryMs", scanTimer.Interval, "hdrListed", games.Contains(name), "profiled", profiles.ContainsKey(name),
                "exitWatched", h != IntPtr.Zero, "running", running.Count, "steamPending", steamTimeout.Enabled, "graceRunning", sdrTimer.Enabled);

            sdrTimer.Stop();
            island.End("sdr");
            EndSteamWindow(name);
            bool late = Follow.Late(age, NoteLaunch(name, false));   // not late if Steam's Play switched before the game started
            // HDR first, the mode after it: switching HDR makes Windows reapply the saved display mode, which would
            // throw away a competitive mode set just before. A mode change leaves HDR alone.
            bool hdr = games.Contains(name) && SwitchOn(name);
            if (hdr && late && hinted.Add(name))
                Hint("HDR came on after " + DisplayName(name) + " started. If HDR is greyed out in the game, restart it.");
            bool mode = profiles.ContainsKey(name) && ApplyResolution(name);
            if (mode && late && resHinted.Add(name))
                Hint(ResHint(name));
            if ((hdr || mode) && late)
                SwitchLog.Write("late", "exe", name, "ageMs", Follow.AgeText(age), "hdrSwitched", hdr, "modeSwitched", mode, SwitchLog.Then,
                    "the switch came after the game had started (more than " + Follow.LateMs + " ms, or for an unknown time): 'restart the game' is said once per game");
            if (hdr || mode) AnnounceGame(name, hdr);
            if (hdr && res != null) CheckSoon(ModeKeeper.How.HdrVerify, 1500);   // some drivers reset the mode a moment after an HDR switch
            SwitchLog.Write("tracked", "pid", pid, "exe", name, "hdrSwitched", hdr, "modeSwitched", mode, "ours", OursText(res), "hdr", HdrText());
            StartGrace();   // e.g. HDR was about to go off when a game that only has a resolution started
            UpdateTray();
        }

        /// <summary>The island: this game just got HDR, its resolution, or both.</summary>
        void AnnounceGame(string name, bool hdr)
        {
            string sub = hdr ? "HDR on" + (res != null ? " · " + res.Text : "") : res.Text;
            Announce(new Island.Act { Id = "game", Title = DisplayName(name), Sub = sub, Text = hdr ? "HDR on" : res.Mode.Text, Dot = hdr ? Island.Mark.Hdr : Island.Mark.On });
        }

        /// <summary>
        /// Starts the grace period if something this app switched is no longer needed, and counts it down on the island:
        /// "SDR in 3s", or the resolution the screen goes back to.
        /// </summary>
        void StartGrace()
        {
            if (!NeedsSettling() || sdrTimer.Enabled)
            {
                if (owned.Count > 0 || res != null)   // something is switched, and stays: say why
                    SwitchLog.Write("grace-skip", "ours", OursText(res), "hdrOwned", owned.Count, "hdrRunning", HdrRunning(), "profiledRunning", ProfiledRunning(), SwitchLog.Then,
                        sdrTimer.Enabled && NeedsSettling() ? "already counting down" : steamTimeout.Enabled ? "Steam is starting a game" : "still needed by a running game");
                return;
            }
            sdrTimer.Start();
            grace.Restart();
            bool hdr = owned.Count > 0 && !HdrRunning();
            string what = hdr ? "SDR" : res != null && res.Back != null ? res.Back.Text : "Resolution";
            SwitchLog.Write("grace-start", "seconds", sdrDelay, "for", what, "ours", OursText(res), "hdrOwned", owned.Count, "running", running.Count);
            Announce(new Island.Act { Id = "sdr", Hold = true, Dot = hdr ? Island.Mark.Hdr : Island.Mark.On,
                Live = () => what + " in " + Math.Max(1, (int)Math.Ceiling((sdrDelay * 1000 - grace.ElapsedMilliseconds) / 1000.0)) + "s" });
        }

        /// <summary>Says something on the island – or, with the island switched off, as a notification if it has one.</summary>
        void Announce(Island.Act a)
        {
            if (islandOn) island.Start(a);
            else if (a.Balloon != null) Notify(a.Balloon);
        }

        /// <summary>A setup hint: too long for the island, so it is a notification, and stays in the flyout's status card.</summary>
        void Hint(string text)
        {
            notice = text;
            SwitchLog.Write("hint", "text", text);
            Notify(text);
        }

        void SetIsland(bool on)
        {
            islandOn = on;
            WriteSetting("Island", on);
            if (!on) island.Clear();
        }

        /// <summary>The screen Display Switch switches: the island's news is about that one.</summary>
        Screen IslandScreen()
        {
            var hdr = res != null || owned.Count > 0 ? null : Hdr.Primary();
            string gdi = res != null ? res.Gdi : owned.Count > 0 ? owned[0].Gdi : hdr != null && hdr.Gdi.Length > 0 ? hdr.Gdi : TargetGdi();
            return Screen.AllScreens.FirstOrDefault(s => s.DeviceName == gdi) ?? Screen.PrimaryScreen;
        }

        /// <summary>Steam says Play was pressed: for a game from the list, the switch happens before the game even starts.</summary>
        void OnSteamStarted(int appId)
        {
            string label = GameScanner.SteamLabel(appId);
            if (label == null)
            {
                SwitchLog.Write("steam-running", "appId", appId, SwitchLog.Then, "no match (Steam has no installed app with this id)");
                return;
            }
            string name = games.Concat(profiles.Keys).FirstOrDefault(g => LabelOf(g) == label);
            if (name == null)
            {
                SwitchLog.Write("steam-running", "appId", appId, "label", label, SwitchLog.Then, "no match (no game on the lists has this label: it is caught by the process scan, if it is listed)");
                return;
            }
            SwitchLog.Write("steam-running", "appId", appId, "label", label, "exe", name, "hdrListed", games.Contains(name), "profiled", profiles.ContainsKey(name), SwitchLog.Then, "switching before the game starts");
            sdrTimer.Stop();
            island.End("sdr");
            NoteLaunch(name, true);
            bool hdr = games.Contains(name) && SwitchOn(name);              // HDR first – see Track
            bool mode = profiles.ContainsKey(name) && ApplyResolution(name);
            if (hdr || mode) AnnounceGame(name, hdr);
            if (hdr && res != null) CheckSoon(ModeKeeper.How.HdrVerify, 1500);
            steamTimeout.Stop();
            steamTimeout.Start();   // back to normal if the game doesn't show up within a minute
            steamGame = name;       // only this game ends the wait (see EndSteamWindow)
            steamLabel = label;
            SwitchLog.Write("steam-prelaunch", "exe", name, "hdrSwitched", hdr, "modeSwitched", mode, "ours", OursText(res), SwitchLog.Then, "waiting up to 60 s for the game's process");
            UpdateTray();
        }

        void Exited(int pid)
        {
            Running r;
            if (!running.TryGetValue(pid, out r)) return;
            running.Remove(pid);
            SwitchLog.Write("game-exit", "pid", pid, "exe", r.Name, "running", running.Count, "hdrRunning", HdrRunning(), "profiledRunning", ProfiledRunning(), "ours", OursText(res));
            if (r.Wait != null)
            {
                r.Wait.Unregister(null);
                r.Handle.Dispose();
            }
            StartGrace();
            UpdateTray();
        }

        /// <summary>Something this app switched is no longer needed by a running game – and Steam isn't about to start one.</summary>
        bool NeedsSettling()
        {
            if (steamTimeout.Enabled) return false;
            return (owned.Count > 0 && !HdrRunning()) || (res != null && !ProfiledRunning());
        }

        /// <summary>After the grace period: whatever no running game needs any more goes back – SDR, the saved mode.</summary>
        void Settle()
        {
            bool hdrBack = !HdrRunning() && owned.Count > 0;
            string modeBack = res != null && !ProfiledRunning() ? (res.Back != null ? res.Back.Text : "Resolution") : null;
            SwitchLog.Write("settle", "hdrBack", hdrBack, "modeBack", modeBack != null, "ours", OursText(res), "running", running.Count, "hdrRunning", HdrRunning(),
                "profiledRunning", ProfiledRunning(), "strayScaling", strayScaling);
            if (!ProfiledRunning())   // the mode first: switching HDR off resets the mode anyway
            {
                RevertResolution();
                RestoreStrayScaling();
            }
            if (!HdrRunning() && owned.Count > 0)
            {
                bool ours = InOurMode();
                RestoreBrightness();
                AllToSdr();
                if (ours) KeepMode();   // a game with only a resolution may still be running
            }
            if (running.Count == 0) notice = null;
            // The countdown ends on what it counted down to, for a moment.
            if (islandOn && (hdrBack || modeBack != null))
                island.Start(new Island.Act { Id = "sdr", Text = hdrBack ? "SDR" : modeBack, Dot = Island.Mark.Off, Seconds = 1.5 });
            else island.End("sdr");
            UpdateTray();
        }

        /// <summary>
        /// Once a second, or four times while no game runs (Follow.ScanInterval): spots games as soon as their process
        /// starts – while they are still loading, long before their window shows – so HDR is on by the time the game
        /// asks Windows about it. A process snapshot holds only names and ids, and the lists it fills are kept between
        /// looks, so a look is cheap.
        /// </summary>
        void Rescan()
        {
            bool unwaitable = running.Values.Any(r => r.Wait == null);
            if (inScan) return;   // a look is in progress further up the stack: the next tick looks again
            if (games.Count == 0 && profiles.Count == 0 && !unwaitable && !sdrWhileRecording)
            {
                absentAt.Clear();   // not looking: whatever is found when it starts again was there for an unknown time
                lastScanAt = -1;
                return;
            }
            inScan = true;
            try
            {
                long now = keepClock.ElapsedMilliseconds;
                var alive = scanAlive;
                var snapshot = scanList;
                alive.Clear();
                Proc.Snapshot(snapshot);
                // The recorder first, so a game found in the same scan already sees it.
                SetRecorder(sdrWhileRecording ? snapshot.Select(p => p.Value).FirstOrDefault(recorders.ContainsKey) : null);
                for (int i = 0; i < snapshot.Count; i++)
                {
                    var p = snapshot[i];
                    alive.Add(p.Key);
                    if (!absentAt.ContainsKey(p.Key)) absentAt[p.Key] = lastScanAt;   // new since the last look (-1: this is the first)
                    if (Followed(p.Value) && !running.ContainsKey(p.Key)) Track(p.Key, p.Value);
                }
                if (absentAt.Count != alive.Count)   // some ended: forgotten, so a process id used again counts as new
                {
                    scanDead.Clear();
                    foreach (int pid in absentAt.Keys) if (!alive.Contains(pid)) scanDead.Add(pid);
                    foreach (int pid in scanDead) absentAt.Remove(pid);
                }
                lastScanAt = now;
                if (unwaitable)
                    foreach (int pid in running.Where(kv => kv.Value.Wait == null && !alive.Contains(kv.Key)).Select(kv => kv.Key).ToList())
                        Exited(pid);
            }
            finally { inScan = false; }
        }

        /// <summary>Stops following running games that were taken off both lists.</summary>
        void ForgetRemoved()
        {
            foreach (int pid in running.Where(kv => !Followed(kv.Value.Name)).Select(kv => kv.Key).ToList())
                Exited(pid);
        }

        // ---- HDR ----

        /// <summary>
        /// True if HDR was switched on just now: on the screen the game was on last time, or the main one. game null: the
        /// first running game from the HDR list.
        /// </summary>
        bool SwitchOn(string game = null)
        {
            if (recorder != null || paused)   // SDR while recording, or paused
            {
                SwitchLog.Write("hdr-on", "game", game, SwitchLog.Then, recorder != null ? "skipped (held by recorder " + recorder + ")" : "skipped (paused)");
                return false;
            }
            var d = HdrDisplayFor(game ?? running.Values.Select(r => r.Name).FirstOrDefault(games.Contains));
            if (d == null || d.On)   // no HDR display, or HDR already on (not ours to switch back)
            {
                SwitchLog.Write("hdr-on", "game", game, "display", d == null ? null : d.Gdi, SwitchLog.Then, d == null ? "skipped (no display that can do HDR)" : "skipped (HDR already on: not ours to switch back)");
                return false;
            }
            bool ours = InOurMode();
            if (!Hdr.Set(d, true))
            {
                SwitchLog.Write("hdr-on", "game", game, "display", d.Gdi, "inOurMode", ours, SwitchLog.Then, "failed (Windows refused)");
                return false;
            }
            SwitchLog.Write("hdr-on", "game", game, "display", d.Gdi, "inOurMode", ours, SwitchLog.Then, "on");
            Own(d);
            if (ours) KeepMode();   // e.g. an HDR game started while another game has its competitive resolution
            return true;
        }

        // Which displays this app switched to HDR is kept in hdr.state as well, one display per line (its Id, which stays
        // the same across reboots). Written after HDR went on and after it went off, so a crash in between errs towards
        // not owning: HDR it can't be sure it switched is never switched off. If the app is killed mid-game, the next start
        // takes those displays back (RecoverHdr) and switches them to SDR once no game that wants HDR runs.

        /// <summary>HDR this app has just switched on: remembered, also in hdr.state.</summary>
        void Own(Hdr.Display d)
        {
            if (owned.Any(o => o.Key == d.Key)) return;
            owned.Add(d);
            SaveOwned();
        }

        /// <summary>HDR this app has just switched off again.</summary>
        void Disown(Hdr.Display d)
        {
            if (owned.RemoveAll(o => o.Key == d.Key) > 0) SaveOwned();
        }

        /// <summary>Every display this app switched to HDR goes back to SDR.</summary>
        void AllToSdr() { AllToSdr(false); }

        /// <summary>
        /// A display Windows refuses to switch back stays this app's (HDR is still on there, and still ours to switch off):
        /// the scan tries again (RetrySdr), a few times. fromScan: this is such a retry.
        /// </summary>
        void AllToSdr(bool fromScan)
        {
            int before = owned.Count;
            foreach (var d in owned.ToList())
                if (ToSdr(d, "nothing needs HDR on it any more", fromScan)) owned.RemoveAll(o => o.Key == d.Key);
            if (owned.Count != before || !fromScan) SaveOwned();
        }

        void SaveOwned()
        {
            if (owned.Count == 0) Guard.Delete(HdrStateFile);
            else Guard.Save(HdrStateFile, FormatHdrState(owned.Select(d => d.Id)));
        }

        /// <summary>
        /// A previous run ended while it had HDR on: each display in hdr.state that still has HDR on is this app's again. One
        /// with HDR off now (switched by hand, or gone) is dropped. HDR that isn't listed was never this app's and stays as it is.
        /// </summary>
        void RecoverHdr()
        {
            string text;
            try
            {
                if (!File.Exists(HdrStateFile)) return;
                text = File.ReadAllText(HdrStateFile);
            }
            catch (IOException) { return; }
            catch (UnauthorizedAccessException) { return; }
            var ids = ParseHdrState(text);
            foreach (var d in Hdr.All())
                if (d.On && ids.Any(d.Is) && !owned.Any(o => o.Key == d.Key)) owned.Add(d);
            SwitchLog.Write("recover-hdr", "listed", ids.Count, "takenBack", owned.Count, "hdr", HdrText(), SwitchLog.Then,
                owned.Count > 0 ? "HDR left on by a previous run is this app's again" : "nothing to take back (HDR is off there now, or the display is gone)");
            SaveOwned();   // only what was taken back stays listed
        }

        /// <summary>hdr.state: one display Id per line, each once.</summary>
        internal static string FormatHdrState(IEnumerable<string> ids)
        {
            var sb = new StringBuilder();
            foreach (string id in ParseHdrState(string.Join("\n", ids.Where(i => i != null)))) sb.Append(id).Append("\r\n");
            return sb.ToString();
        }

        /// <summary>
        /// The display Ids in hdr.state, each once, in order. Blank lines, notes (#) and lines no display could have as its Id
        /// (control characters, far too long) are skipped; an Id that matches no display now is simply never matched.
        /// </summary>
        internal static List<string> ParseHdrState(string text)
        {
            var ids = new List<string>();
            if (text == null) return ids;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in text.Split('\n'))
            {
                string id = raw.Trim();
                if (id.Length == 0 || id.Length > 512 || id[0] == '#' || id.Any(char.IsControl)) continue;
                if (seen.Add(id)) ids.Add(id);
            }
            return ids;
        }

        // ---- the game's own screen ----
        //
        // HDR goes on the screen a game is on. That screen is only known once its window is up, often seconds after HDR
        // had to be on, so a game starts with HDR on the screen it used last time (the main one the first time), and once
        // a second its windows are looked at: a game on another screen that can do HDR gets HDR there, and a screen this
        // app switched that no running game is on goes back to SDR.

        Dictionary<string, string> screens = ReadScreens();   // game → the screen (\\.\DISPLAY2) it was on last time
        const string ScreensKey = SettingsKey + @"\Screens";

        /// <summary>The HDR-capable display a game goes on: the one it was on last time, or the main one.</summary>
        Hdr.Display HdrDisplayFor(string game)
        {
            string gdi;
            if (game != null && screens.TryGetValue(game, out gdi))
            {
                var d = Hdr.All().FirstOrDefault(x => x.Gdi == gdi && x.Supported);
                if (d != null) return d;
            }
            return Hdr.Primary();
        }

        void FollowGameScreens()
        {
            windowOf.Clear();
            if (running.Count == 0)
            {
                FollowModeScreen();   // nothing to follow: forgets a move it was waiting on
                return;
            }
            // Where every followed game's window is: remembered for its next start, for HDR and for its resolution alike.
            var hdrGames = new List<string>();   // the HDR games, and the screen each one's window is on (null: no window yet)
            var now = new List<string>();
            foreach (var kv in running)
            {
                string gdi = Proc.WindowScreen(kv.Key);
                string name = kv.Value.Name;
                if (gdi != null) RememberScreen(name, gdi);
                if (games.Contains(name))
                {
                    hdrGames.Add(name);
                    now.Add(gdi);
                }
                string seen;
                if (profiles.ContainsKey(name) && (!windowOf.TryGetValue(name, out seen) || seen == null)) windowOf[name] = gdi;
            }
            if (!paused && recorder == null && hdrGames.Count > 0) FollowHdrScreens(hdrGames, now);
            FollowModeScreen();
        }

        void FollowHdrScreens(List<string> hdrGames, List<string> now)
        {
            List<Hdr.Display> on, off;
            PlanScreens(now, Hdr.All(), new HashSet<string>(owned.Select(o => o.Key)), out on, out off);
            if (on.Count == 0 && off.Count == 0) return;
            bool ours = InOurMode();
            bool changed = false;
            foreach (var d in on)
            {
                // Windows may refuse: tried a few times for this game on this screen, then said once and left alone until
                // the game is next started.
                int at = now.IndexOf(d.Gdi);
                string game = at < 0 ? "" : hdrGames[at];
                string key = "on|" + game + "|" + d.Key;
                if (!tries.May(key)) continue;
                if (Hdr.Set(d, true))
                {
                    tries.Clear(key);
                    SwitchLog.Write("hdr-on", "game", game, "display", d.Gdi, "why", "the game's window is on this screen", "inOurMode", ours, SwitchLog.Then, "on");
                    Own(d);
                    changed = true;
                    continue;
                }
                int n = tries.Fail(key);
                bool again = tries.May(key);
                SwitchLog.Write("hdr-on", "game", game, "display", d.Gdi, "why", "the game's window is on this screen", "try", n + "/" + Follow.HdrTries, SwitchLog.Then,
                    again ? "FAILED (Windows refused): tried again in a second" : "FAILED (Windows refused " + Follow.HdrTries + " times): not tried again for this game on this screen until the game is next started");
                if (!again && game.Length > 0)
                    Problem(game, "hdr-screen", "Couldn't switch HDR on for " + DisplayName(game) + " on the screen it is on: Windows refused. It is tried again when the game is next started.");
            }
            foreach (var d in off)
            {
                if (!tries.May("off|" + d.Key)) continue;   // refused a few times already: left for the next settle
                if (d.Is(brightKey)) RestoreBrightness();
                if (!ToSdr(d, "no running game is on this screen", true)) continue;   // refused: HDR is still on, and still ours
                Disown(d);
                changed = true;
            }
            if (!changed) return;
            if (ours) KeepMode();
            UpdateTray();
        }

        /// <summary>
        /// Which displays to switch, given the screens the HDR games' windows are on (null: no window yet). A screen with a
        /// game that can do HDR gets it. One this app switched (owned) goes back only when every game's screen is known,
        /// so HDR never goes off under a game that is still starting.
        /// </summary>
        internal static void PlanScreens(IList<string> gameScreens, IList<Hdr.Display> all, ICollection<string> owned,
            out List<Hdr.Display> turnOn, out List<Hdr.Display> turnOff)
        {
            turnOn = new List<Hdr.Display>();
            turnOff = new List<Hdr.Display>();
            var wanted = new HashSet<string>();
            bool unsure = false;
            foreach (string gdi in gameScreens)
            {
                var d = gdi == null ? null : all.FirstOrDefault(x => x.Gdi == gdi);
                if (d != null && d.Supported) wanted.Add(d.Key);
                else unsure = true;   // no window yet, or on a screen without HDR: leave HDR where it is for that game
            }
            if (wanted.Count == 0) return;
            foreach (var d in all) if (wanted.Contains(d.Key) && !d.On) turnOn.Add(d);
            if (unsure) return;
            foreach (var d in all) if (!wanted.Contains(d.Key) && d.On && owned.Contains(d.Key)) turnOff.Add(d);
        }

        void RememberScreen(string game, string gdi)
        {
            string was;
            if (screens.TryGetValue(game, out was) && was == gdi) return;
            screens[game] = gdi;
            try { using (var k = Registry.CurrentUser.CreateSubKey(ScreensKey)) k.SetValue(game, gdi); } catch { }
        }

        static Dictionary<string, string> ReadScreens()
        {
            var d = new Dictionary<string, string>();
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(ScreensKey))
                    if (k != null)
                        foreach (string n in k.GetValueNames())
                        {
                            string v = k.GetValue(n) as string;
                            if (!string.IsNullOrEmpty(v)) d[n] = v;
                        }
            }
            catch { }
            return d;
        }

        /// <summary>
        /// A recording app started (name) or closed (null). While it runs, HDR this app turned on goes back to SDR, so a
        /// recording keeps its colours; once it closes, a game that is still running gets its HDR back. HDR turned on
        /// elsewhere is left alone, as always.
        /// </summary>
        void SetRecorder(string name)
        {
            if (name == recorder) return;
            recorder = name;
            SwitchLog.Write("recorder", "exe", name, "hdrOwned", owned.Count, "hdrRunning", HdrRunning(), SwitchLog.Then,
                name == null ? "closed: HDR may come back for a running game" : owned.Count > 0 ? "running: HDR this app switched goes off and is held off" : "running: HDR is held off");
            if (name != null)
            {
                if (owned.Count > 0)
                {
                    bool ours = InOurMode();
                    RestoreBrightness();
                    AllToSdr();
                    if (ours) KeepMode();
                    Announce(new Island.Act { Id = "rec", Text = RecorderName(name) + " · SDR", Dot = Island.Mark.Off, Split = true, Bubble = Island.Mark.Critical, Seconds = 4,
                        Balloon = "HDR is off while " + RecorderName(name) + " runs." });
                }
            }
            else if (HdrRunning() && SwitchOn()) Announce(new Island.Act { Id = "game", Text = "HDR on", Dot = Island.Mark.Hdr });
            UpdateTray();
        }

        void SetSdrWhileRecording(bool on)
        {
            sdrWhileRecording = on;
            WriteSetting("SdrWhileRecording", on);
            if (on) Rescan();
            else SetRecorder(null);
        }

        // ---- pause and the wait before SDR ----

        HotKey hotkey;
        bool paused;   // for this session: nothing switches until it is resumed (Ctrl+Alt+H or the flyout)

        /// <summary>
        /// Paused: what this app switched goes back at once (the mode, then SDR) and nothing switches until it is resumed.
        /// Games are still followed meanwhile, so resuming switches for the ones that are running.
        /// </summary>
        void SetPaused(bool on)
        {
            if (on == paused) return;
            paused = on;
            SwitchLog.Write(on ? "pause" : "resume", "ours", OursText(res), "hdrOwned", owned.Count, "running", running.Count, SwitchLog.Then,
                on ? "everything this app switched goes back now" : "switching again for the games that run");
            if (on)
            {
                sdrTimer.Stop();
                steamTimeout.Stop();
                steamGame = steamLabel = null;
                island.End("sdr");
                RevertResolution();
                RestoreStrayScaling();
                RestoreBrightness();
                AllToSdr();
                Announce(new Island.Act { Id = "pause", Text = "Paused", Dot = Island.Mark.Paused, Seconds = 2,
                    Balloon = "Paused: HDR and resolutions stay as they are until you resume (" + HotKey.Text + ")." });
            }
            else
            {
                bool hdr = HdrRunning() && SwitchOn();
                ReapplyProfiles();
                Announce(new Island.Act { Id = "pause", Text = hdr ? "HDR on" : "Resumed", Dot = hdr ? Island.Mark.Hdr : Island.Mark.On, Seconds = 2,
                    Balloon = "Resumed." });
            }
            UpdateTray();
        }

        void SetDelay(int seconds)
        {
            sdrDelay = ValidDelay(seconds);
            WriteInt("SdrDelay", sdrDelay);
            bool counting = sdrTimer.Enabled;
            sdrTimer.Stop();
            sdrTimer.Interval = sdrDelay * 1000;
            if (counting) { sdrTimer.Start(); grace.Restart(); }   // the countdown starts again at the new length
        }

        /// <summary>The next of the delays the flyout offers, round and round.</summary>
        internal static int NextDelay(int seconds)
        {
            foreach (int d in DelayChoices) if (d > seconds) return d;
            return DelayChoices[0];
        }

        internal static int ValidDelay(int seconds) { return Math.Max(1, Math.Min(600, seconds)); }

        static int ReadInt(string name, int unset)
        {
            using (var k = Registry.CurrentUser.OpenSubKey(SettingsKey))
            {
                object v = k == null ? null : k.GetValue(name);
                return v is int ? (int)v : unset;
            }
        }

        static void WriteInt(string name, int value)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(SettingsKey))
                k.SetValue(name, value, RegistryValueKind.DWord);
        }

        static bool ReadSetting(string name) { return ReadSetting(name, false); }

        /// <summary>A setting, or its default while it has never been changed.</summary>
        static bool ReadSetting(string name, bool unset)
        {
            using (var k = Registry.CurrentUser.OpenSubKey(SettingsKey))
            {
                object v = k == null ? null : k.GetValue(name);
                return v is int ? (int)v != 0 : unset;
            }
        }

        static void WriteSetting(string name, bool on)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(SettingsKey))
                k.SetValue(name, on ? 1 : 0, RegistryValueKind.DWord);
        }

        /// <summary>The desktop is in the competitive mode this app set.</summary>
        bool InOurMode()
        {
            return res != null && res.Mode.Same(Resolution.Current(res.Gdi))
                && (res.Area == null || Resolution.Stretched(res.Gdi, res.Area.Width, res.Area.Height));
        }

        /// <summary>
        /// A stretched mode was replaced by one set elsewhere – in practice by a game in exclusive fullscreen. Shown in the
        /// flyout and the tooltip, because the notification about it is easily missed (or switched off in Windows).
        /// </summary>
        bool StretchLost() { return res != null && res.Area != null && !InOurMode() && !Resolution.AtSaved(res.Gdi); }

        /// <summary>
        /// Switching HDR on or off makes Windows reapply the display mode saved in settings – usually at once, before the
        /// call returns, with some drivers a moment later – which drops the competitive mode. Called right after our own
        /// HDR switch, and only if the desktop was in our mode just before it, so a mode the user picked by hand is never
        /// overridden. Decided and logged like every other lost mode (CheckMode): Windows refusing the mode gets another
        /// try, and the mode is looked at once more 1.5 s later.
        /// </summary>
        void KeepMode()
        {
            if (res == null)
            {
                SwitchLog.Write("keep-mode", SwitchLog.Then, "nothing to keep (no mode of ours)");
                return;
            }
            var v = CheckMode(ModeKeeper.How.OwnSwitch);
            SwitchLog.Write("keep-mode", "ours", OursText(res), "decided", v, SwitchLog.Then, res == null ? "let go of" : "looked at again in 1.5 s");
            if (res != null) CheckSoon(ModeKeeper.How.HdrVerify, 1500);
        }

        /// <summary>Switches the desktop to a competitive mode: shown 1:1 in the middle, or stretched over its area.</summary>
        static bool SetMode(ActiveMode m)
        {
            return m.Area == null ? Resolution.Apply(m.Gdi, m.Mode.Width, m.Mode.Height, m.Mode.Hz)
                : Resolution.ApplyStretched(m.Gdi, m.Mode.Width, m.Mode.Height, m.Mode.Hz, m.Area.Width, m.Area.Height);
        }

        /// <summary>A mode change can take HDR down with it: put back what this app owns. True if HDR was switched.</summary>
        bool KeepHdr()
        {
            if (owned.Count == 0) return false;
            bool switched = false;
            var all = Hdr.All();
            foreach (var o in owned)
            {
                var d = all.FirstOrDefault(x => x.Key == o.Key);
                if (d != null && !d.On)
                {
                    bool ok = Hdr.Set(d, true);
                    SwitchLog.Write("hdr-keep", "display", d.Gdi, SwitchLog.Then, ok ? "on again (the mode change had taken HDR down)" : "failed (Windows refused)");
                    switched |= ok;
                }
            }
            return switched;
        }

        // ---- competitive resolution ----

        /// <summary>
        /// The main display: where a competitive resolution goes for a game with no remembered screen. For a game, see
        /// TargetGdiFor (Follow.cs): the screen it was on last time comes first.
        /// </summary>
        static string TargetGdi()
        {
            return Screen.PrimaryScreen.DeviceName;
        }

        /// <summary>"2368 × 1332", or "2368 × 1332 @ 240 Hz" when the profile names a rate.</summary>
        static string ProfileText(Resolution.Mode p)
        {
            return p.Hz > 0 ? p.Text + " @ " + p.Hz + " Hz" : p.Text;
        }

        string ResHint(string name)
        {
            return "Screen switched to " + res.Text + " after " + DisplayName(name) + " started. If it doesn't fit, restart the game.";
        }

        /// <summary>
        /// Switches the desktop to a game's competitive resolution. The first profiled game to start sets it and it stays
        /// until no profiled game runs. True if the mode was switched just now.
        /// </summary>
        bool ApplyResolution(string name)
        {
            Resolution.Mode p;
            if (paused)
            {
                SwitchLog.Write("apply", "game", name, SwitchLog.Then, "skipped (paused)");
                return false;
            }
            if (!profiles.TryGetValue(name, out p))
            {
                SwitchLog.Write("apply", "game", name, SwitchLog.Then, "skipped (no competitive resolution for this game)");
                return false;
            }
            if (res != null && res.Game.Length == 0 && !Matches(res, p, StretchOf(name)))
            {
                // Taken over from a previous run (see RecoverResolution), but it isn't what this game asks for now: back to
                // the saved mode, then this game's own, as if nothing had been left.
                SwitchLog.Write("apply", "game", name, "want", p, "display", res.Gdi, ModesOf(res.Gdi), "ours", OursText(res), SwitchLog.Then,
                    "the mode a previous run left isn't this game's profile: putting it back, then applying this game's");
                RevertResolution();
            }
            if (res != null)
            {
                bool adopted = res.Game.Length == 0;
                if (adopted)
                {
                    res.Game = name;
                    res.Centred = res.Area == null && Centred(Resolution.Read(res.Gdi));
                }
                bool other = res.Game != name;
                bool lost = !InOurMode();
                SwitchLog.Write("apply", "game", name, "want", p, "display", res.Gdi, ModesOf(res.Gdi), "ours", OursText(res), "inOurMode", !lost, "matchesProfile", MatchesText(name),
                    SwitchLog.Then, (adopted ? "the mode a previous run left is this game's: taken over for it"
                        : other ? "held by " + res.Game + ": the first game's mode keeps priority while both run, this game's profile is not applied"
                        : "already held for this game") + (lost ? "; the desktop is NOT in that mode: checking why" : "; the desktop is in it: nothing to set"));
                if (!lost) return false;
                // Lost meanwhile (a reset during the grace period or the Steam start, or under the game that holds it):
                // set again for the game that holds it, unless it was changed on purpose.
                var holder = res;
                return CheckMode(ModeKeeper.How.GameStart) == ModeKeeper.Verdict.Reapply && res == holder && InOurMode();
            }
            string targetWhy;
            string gdi = TargetGdiFor(name, out targetWhy);   // the game's screen, else the main one
            modeTried[name] = gdi;
            var now = Resolution.Current(gdi);
            if (now == null)
            {
                SwitchLog.Write("apply", "game", name, "want", p, "display", gdi, "why", targetWhy, SwitchLog.Then, "skipped (Windows gave no current mode for this display)");
                return false;
            }
            notices.Clear(name);   // a new switch: what was wrong last time is kept only if it comes up again
            int wantHz = p.Hz > 0 ? p.Hz : now.Hz;
            var area = new Resolution.Mode { Width = p.Width, Height = p.Height, Hz = wantHz };
            // A game in borderless mode can have a lower resolution of its own, stretched over the competitive one.
            Resolution.Mode own = StretchOf(name);
            if (own == null && stretches.ContainsKey(name))
            {
                SwitchLog.Write("apply-stretch", "game", name, "stretch", stretches[name], "area", area, SwitchLog.Then, "stretch dropped (it doesn't fit inside the area): the plain area is used");
                Problem(name, "fit", stretches[name].Text + " doesn't fit inside " + area.Text + ", so " + DisplayName(name) + " isn't stretched. Pick a smaller one.");
            }
            var wanted = own != null ? new Resolution.Mode { Width = own.Width, Height = own.Height, Hz = wantHz } : area;
            // What exists decides: the stretch at the wanted rate, else the plain area, else a lower rate (Fallback.cs).
            var areaRates = Resolution.Rates(gdi, area.Width, area.Height);
            var ownRates = own == null ? null : Resolution.Rates(gdi, own.Width, own.Height);
            var plan = Fallback.Choose(area, own, wantHz, p.Hz > 0, now.Hz, areaRates, ownRates, DisplayName(name));
            var mode = plan.Mode ?? wanted;
            bool stretch = plan.Area != null;
            if (plan.Mode != null) area = plan.Area ?? plan.Mode;
            // What every "apply" line starts with: the game, the display and why it was picked, what is wanted, what is there.
            var asked = SwitchLog.Pairs("game", name, "display", gdi, "why", targetWhy, "want", wanted, "area", own != null ? new Resolution.Mode { Width = p.Width, Height = p.Height, Hz = wantHz } : null, "profileHz", p.Hz > 0 ? p.Hz + "" : "current",
                "current", now, "saved", Resolution.Saved(gdi), "plan", plan.Why);
            if (leftover != null && (leftover.Gdi != gdi || !leftover.Mode.Same(now)))   // gone since: nothing of ours is left
            {
                leftover = null;
                if (strayScaling < 0) DeleteState();
            }
            if (plan.Mode == null)
            {
                SwitchLog.Write("apply", asked, "sizeExistsAtHz", RatesText(gdi, wanted.Width, wanted.Height), "areaExistsAtHz", own != null ? RatesText(gdi, area.Width, area.Height) : null, SwitchLog.Then,
                    "unavailable (the display has no " + wanted.Width + "x" + wanted.Height + " at " + wantHz + " Hz" + (own != null ? ", and not the plain area either" : "") + ": nothing switched)");
                Problem(name, plan.Reason, plan.Message);
                return false;
            }
            if (mode.Same(now) && (!stretch || Resolution.Stretched(gdi, area.Width, area.Height)))
            {
                if (leftover == null)   // already in that mode, set by hand: not ours to switch back
                {
                    SwitchLog.Write("apply", asked, SwitchLog.Then, "skipped (the desktop is already in this mode, set elsewhere: not tracked, NVIDIA scaling not touched, nothing to put back)");
                    return false;
                }
                // Already in that mode because this app left it there (Windows refused to put it back): it is ours, so it
                // is tracked and put back like one set just now, and the scaling is taken as usual.
                var kept = new ActiveMode { Gdi = gdi, Id = DisplayIdOf(gdi), Mode = mode, Area = stretch ? area : null, Back = leftover.Back ?? Resolution.Saved(gdi), Game = name,
                    ScalingBack = TakeScaling(gdi), FellBack = plan.Kind == Fallback.Kind.NoStretch ? own : null };
                kept.ScalingPending = lastTake == Fallback.Take.NotAtSaved;
                kept.Centred = !stretch && Centred(Resolution.Read(gdi));
                leftover = null;
                res = kept;
                SaveState();
                SwitchLog.Write("apply", asked, "back", kept.Back, "scalingBack", kept.ScalingBack, "picture", Resolution.Read(gdi), SwitchLog.Then,
                    "taken over (the desktop is already in this mode, left there by this app when it couldn't be put back: tracked again, put back when the game closes)");
                Problem(name, plan.Reason, plan.Message);
                ScalingProblem(name);
                return false;
            }
            Problem(name, plan.Reason, plan.Message);   // not what the profile asks for: said before it shows
            var before = Resolution.Read(gdi);
            // Before the mode: the game's own mode change must find No scaling already set. For a stretched mode too –
            // it only lasts while the game sets no mode of its own, and a game that does is then shown 1:1 in the middle
            // rather than over the whole panel.
            int scalingBack = TakeScaling(gdi);
            var active = new ActiveMode { Gdi = gdi, Id = DisplayIdOf(gdi), Mode = mode, Area = stretch ? area : null, Back = Resolution.Saved(gdi) ?? now, Game = name, ScalingBack = scalingBack,
                ScalingPending = lastTake == Fallback.Take.NotAtSaved, FellBack = plan.Kind == Fallback.Kind.NoStretch ? own : null };
            ScalingProblem(name);
            bool ok = SetMode(active);
            string trace = SetModeTrace(active);
            if (!ok && stretch)
            {
                // The driver wouldn't stretch it (ApplyStretched has put the saved mode back): the plain area instead.
                SwitchLog.Write("apply", asked, "pictureBefore", before, "scalingBack", scalingBack, "how", trace, "now", Resolution.Current(gdi), SwitchLog.Then,
                    "the stretch FAILED (Windows refused it: " + trace + "): falling back to the plain area");
                ok = FallBackToArea(active);
                if (ok)
                {
                    stretch = false;
                    trace += ",fallback(cds=" + SwitchLog.CdsName(Resolution.LastCds) + ")";
                    Problem(name, "stretch-failed", Fallback.StretchFailedText(mode, area));
                    mode = active.Mode;
                }
            }
            if (!ok)
            {
                SwitchLog.Write("apply", asked, "pictureBefore", before, "scalingBack", scalingBack, "how", trace, "now", Resolution.Current(gdi), SwitchLog.Then,
                    "failed (Windows refused the mode: " + trace + ")");
                RestoreScaling(gdi, scalingBack);
                Problem(name, "refused", "Couldn't switch to " + active.Text + " at " + mode.Hz + " Hz for " + DisplayName(name) + ".");
                return false;
            }
            res = active;
            leftover = null;
            SwitchLog.Write("apply", asked, "back", active.Back, "scalingBack", scalingBack, "how", trace, SwitchLog.Then,
                stretch ? "applied (stretched over the area)" : active.FellBack != null ? "applied (1:1, centred: the plain area, without the stretch)" : "applied (1:1, centred)");
            SaveState();
            if (KeepHdr()) KeepMode();   // putting HDR back would itself reset the mode
            if (res != active) return false;   // let go of meanwhile (see CheckMode)
            if (active.Area != null)
            {
                // SetDisplayConfig saying yes isn't the stretch showing: looked at now, and once more in 500 ms if not.
                bool held = Resolution.Stretched(gdi, area.Width, area.Height);
                SwitchLog.Write("picture", "game", name, "display", gdi, "before", before, "after", Resolution.Read(gdi), "now", Resolution.Current(gdi), "stretched", held, SwitchLog.Then,
                    held ? "the desktop is stretched over the area" : "NOT stretched over the area after the switch: looked at again in 500 ms, then set once more or replaced by the plain area");
                active.Unverified = !held;
                stretchVerify.Stop();
                if (!held) stretchVerify.Start();
                return true;
            }
            CheckCentred(active, name, before);   // the panel should still get its native signal, with the smaller desktop centred in it
            return true;
        }

        /// <summary>Back to the saved mode – but only if the desktop is still in the mode this app set.</summary>
        void RevertResolution()
        {
            if (res == null)
            {
                SwitchLog.Write("revert", SwitchLog.Then, "nothing to put back (no mode of ours)");
                return;
            }
            var r = res;
            res = null;
            DeleteState();
            bool left = false;
            var current = Resolution.Current(r.Gdi);
            if (r.Mode.Same(current))   // otherwise changed meanwhile (settings, sleep, …): not ours any more
            {
                var saved = Resolution.Saved(r.Gdi);
                if (r.Mode.Same(saved) && r.Back != null && !r.Back.Same(saved))
                {
                    bool ok = Resolution.Save(r.Gdi, r.Back);   // something saved our temporary mode (a Control Panel change mid-game): undo that for good
                    SwitchLog.Write("revert", "display", r.Gdi, "ours", OursText(r), "current", current, "saved", saved, "back", r.Back, "cds", SwitchLog.CdsName(Resolution.LastCds), "now", Resolution.Current(r.Gdi),
                        SwitchLog.Then, (ok ? "put back" : "FAILED to put back") + " by saving " + SwitchLog.Value(r.Back) + " (our temporary mode had been saved in display settings)");
                }
                else
                {
                    bool ok = Resolution.Restore(r.Gdi);
                    SwitchLog.Write("revert", "display", r.Gdi, "ours", OursText(r), "current", current, "saved", saved, "cds", SwitchLog.CdsName(Resolution.LastCds), "now", Resolution.Current(r.Gdi),
                        SwitchLog.Then, ok ? "put back to the saved mode" : "FAILED to put back the saved mode");
                }
                KeepHdr();
                left = StillLeft(r);
            }
            else SwitchLog.Write("revert", "display", r.Gdi, "ours", OursText(r), "current", current, "saved", Resolution.Saved(r.Gdi), SwitchLog.Then,
                "mode left alone (the desktop is no longer in our mode: changed elsewhere, not ours to put back)");
            RestoreScaling(r.Gdi, r.ScalingBack);   // the scaling setting is separate from the mode: put it back either way
            if (!left) return;
            // Windows refused and the desktop is still in our temporary mode. It stays ours: remembered here and in
            // resolution.state, so the next game that wants it – or the next start of the app – takes it over and puts it
            // back, instead of finding a mode "set elsewhere".
            leftover = r;
            bool strayHere = strayScaling >= 0 && (strayDisplay == null || GdiOf(strayDisplay) == r.Gdi);
            r.ScalingBack = strayHere ? strayScaling : -1;   // not put back at a temporary mode: RestoreScaling keeps it for later
            if (strayHere || strayScaling < 0) WriteState(FormatModeState(StateOf(r)));   // another display's scaling record stays as it is
            SwitchLog.Write("revert", "display", r.Gdi, "ours", OursText(r), "now", Resolution.Current(r.Gdi), SwitchLog.Then,
                "the desktop is still in our mode: remembered as ours (the next game that wants it, or the next start, takes it over)");
        }

        /// <summary>After a revert: the desktop is still in the temporary mode this app set (and it isn't the saved one).</summary>
        static bool StillLeft(ActiveMode r)
        {
            var now = Resolution.Current(r.Gdi);
            return r.Mode.Same(now) && !Resolution.AtSaved(r.Gdi);
        }

        /// <summary>The display path shows the desktop centred (or 1:1 on a signal of its own size).</summary>
        static bool Centred(Resolution.Picture p) { return p != null && p.Centered; }

        int strayScaling = -1;   // a scaling setting still to put back: left by a killed previous run, or deferred (see RestoreScaling)
        string strayDisplay;     // the display it belongs to (its Id, or a GDI name from an older resolution.state)

        /// <summary>A display's Id (see Hdr.Display.Id) from its GDI name; the GDI name itself if no display has it now.</summary>
        static string DisplayIdOf(string gdi)
        {
            var d = Hdr.All().FirstOrDefault(x => x.Gdi == gdi);
            return d != null ? d.Id : gdi;
        }

        /// <summary>The GDI name the display with this Id (or GDI name) has now, or null if it isn't connected.</summary>
        static string GdiOf(string id)
        {
            var d = Hdr.All().FirstOrDefault(x => x.Is(id));
            return d != null && d.Gdi.Length > 0 ? d.Gdi : null;
        }

        /// <summary>
        /// Switches the NVIDIA driver's scaling default to No scaling, so a game that sets its own mode in exclusive fullscreen
        /// stays centred too. Returns the setting to put back afterwards, or -1 if nothing was changed: no NVIDIA driver, it
        /// already centres (set that way by the user – or left switched by this app, whose original is then taken over), or
        /// the desktop isn't at its saved mode (see AtSaved below).
        /// </summary>
        int TakeScaling(string gdi)
        {
            int s = Nvidia.Scaling(gdi);
            lastTake = Fallback.Take.Unreadable;
            if (s < 0)
            {
                SwitchLog.Write("scaling-take", "display", gdi, "nvidia", SwitchLog.NvidiaName(s), SwitchLog.Then,
                    "not taken (the setting couldn't be read: no NVIDIA driver, another GPU's display, or the driver answered with an error) – a game in exclusive fullscreen is scaled as the driver is set");
                return -1;
            }
            lastTake = Fallback.Take.AlreadyCentres;
            if (Nvidia.Centres(s))
            {
                if (strayScaling >= 0 && strayDisplay != null && GdiOf(strayDisplay) != gdi)   // another display's: put back on its own
                {
                    SwitchLog.Write("scaling-take", "display", gdi, "nvidia", SwitchLog.NvidiaName(s), "strayScaling", strayScaling, "strayDisplay", strayDisplay, SwitchLog.Then,
                        "not taken (it already centres; the setting still to put back belongs to another display)");
                    return -1;
                }
                int back = strayScaling;
                strayScaling = -1;
                strayDisplay = null;
                SwitchLog.Write("scaling-take", "display", gdi, "nvidia", SwitchLog.NvidiaName(s), "back", back, SwitchLog.Then,
                    back >= 0 ? "not changed (it already centres, left that way by this app: the original " + SwitchLog.NvidiaName(back) + " is put back afterwards)"
                        : "not changed (it already centres, set that way by the user: nothing to put back)");
                return back;
            }
            // The driver saves the whole display configuration with the setting, the current resolution included – so it is
            // only ever changed while the desktop is at the mode saved in display settings, never at a temporary one.
            if (!Resolution.AtSaved(gdi))
            {
                lastTake = Fallback.Take.NotAtSaved;
                SwitchLog.Write("scaling-take", "display", gdi, "nvidia", SwitchLog.NvidiaName(s), ModesOf(gdi), SwitchLog.Then,
                    "not taken (the desktop isn't at its saved mode, and the driver would save this one with the setting): tried again when our mode is next set from the saved one – until then a game in exclusive fullscreen is scaled as " + SwitchLog.NvidiaName(s));
                return -1;
            }
            bool set = Nvidia.SetScaling(gdi, Nvidia.NoScaling);
            int after = Nvidia.Scaling(gdi);   // read back: the driver saying yes isn't the setting being there
            lastTake = Fallback.Took(set, after);
            SwitchLog.Write("scaling-take", "display", gdi, "before", SwitchLog.NvidiaName(s), "setAnswered", set, "after", SwitchLog.NvidiaName(after), SwitchLog.Then,
                lastTake == Fallback.Take.Taken ? "taken (No scaling until the game closes)"
                : lastTake == Fallback.Take.Unverified ? "taken, not verified (the driver took No scaling, but the setting can't be read back): put back when the game closes"
                : lastTake == Fallback.Take.NotStuck ? "DID NOT STICK (the driver took No scaling, but the setting reads " + SwitchLog.NvidiaName(after) + ") – a game in exclusive fullscreen is scaled as that; nothing to put back"
                : "FAILED (the driver refused No scaling) – a game in exclusive fullscreen is scaled as " + SwitchLog.NvidiaName(after < 0 ? s : after));
            return Fallback.Changed(lastTake) ? s : -1;
        }

        /// <summary>
        /// Puts the scaling setting back – only if it is still the No scaling this app set. If the desktop isn't at its saved
        /// mode right now (another game's own mode, say) or the setting can't be read, it is kept for later: the next
        /// display change or grace period. Decided by Fallback.RestoreScaling.
        /// </summary>
        void RestoreScaling(string gdi, int back)
        {
            int set = back < 0 ? -1 : Nvidia.Scaling(gdi);
            bool atSaved = back >= 0 && Resolution.AtSaved(gdi);
            var what = Fallback.RestoreScaling(set, back, atSaved);
            if (what == Fallback.Restore.Nothing)
            {
                SwitchLog.Write("scaling-restore", "display", gdi, SwitchLog.Then, "nothing to put back (this app didn't change it)");
                return;
            }
            if (what == Fallback.Restore.UserChanged)
            {
                SwitchLog.Write("scaling-restore", "display", gdi, "nvidia", SwitchLog.NvidiaName(set), "back", SwitchLog.NvidiaName(back), SwitchLog.Then,
                    "left alone (it reads as another setting than the No scaling this app set: changed elsewhere, that one is kept)");
                return;
            }
            if (what == Fallback.Restore.PutBack && Nvidia.SetScaling(gdi, back))
            {
                SwitchLog.Write("scaling-restore", "display", gdi, "before", SwitchLog.NvidiaName(set), "after", SwitchLog.NvidiaName(Nvidia.Scaling(gdi)), SwitchLog.Then, "put back to " + SwitchLog.NvidiaName(back));
                return;
            }
            // Kept, here and in resolution.state. An unreadable setting is not one the user changed: the driver may only
            // be busy (a mode change under way, the display asleep), and the original would be lost for good.
            SwitchLog.Write("scaling-restore", "display", gdi, "nvidia", SwitchLog.NvidiaName(set), "back", SwitchLog.NvidiaName(back), ModesOf(gdi), SwitchLog.Then,
                what == Fallback.Restore.Unreadable ? "deferred (the setting can't be read right now, which doesn't mean it was changed): " + SwitchLog.NvidiaName(back) + " is kept and tried again at the next display change or grace period"
                    : what == Fallback.Restore.PutBack ? "deferred (the driver refused): tried again at the next display change or grace period"
                    : "deferred (the desktop isn't at its saved mode): tried again at the next display change or grace period");
            strayScaling = back;
            strayDisplay = DisplayIdOf(gdi);
            if (res == null) WriteState(FormatModeState(ModeState.ScalingOnly(gdi, back, strayDisplay)));   // so a crash before the retry still puts it back
        }

        /// <summary>A scaling setting still to put back goes back now, on the display it came from, if it is connected.</summary>
        void RestoreStrayScaling()
        {
            if (strayScaling < 0) return;
            string gdi = strayDisplay == null ? TargetGdi() : GdiOf(strayDisplay);
            if (gdi == null)   // that display isn't connected now: kept, in resolution.state too, for when it is
            {
                SwitchLog.Write("scaling-stray", "strayScaling", strayScaling, "strayDisplay", strayDisplay, SwitchLog.Then, "kept (that display isn't connected now)");
                return;
            }
            int back = strayScaling;
            strayScaling = -1;
            SwitchLog.Write("scaling-stray", "display", gdi, "back", SwitchLog.NvidiaName(back), SwitchLog.Then, "putting back a scaling setting left from earlier");
            RestoreScaling(gdi, back);
            if (strayScaling >= 0) return;   // deferred again
            strayDisplay = null;
            if (res == null && leftover != null)
            {
                leftover.ScalingBack = -1;
                WriteState(FormatModeState(StateOf(leftover)));   // the mode left behind is still to put back
            }
            else if (res == null) DeleteState();
        }

        /// <summary>
        /// Windows says the display settings changed. While a game with a resolution runs, the mode is kept: a second
        /// after the last change of a burst, CheckMode says whether the desktop is still in our mode and, if not, whether
        /// Windows reset it (set again), the game set its own (left alone) or the display settings were changed on purpose
        /// (let go of) – see ModeKeeper.cs. With no such game running (the grace period, a Steam start that never came) a
        /// mode that isn't ours any more isn't ours to put back; that is checked again 2 s later first, in case the change
        /// was a transient one. Also what the pending check runs when it was started for another reason (CheckSoon).
        /// </summary>
        void OnDisplayChanged(bool first)
        {
            // The log: the state as it is found, on every change Windows reports and on the check after one.
            var how = first ? ModeKeeper.How.DisplayChange : TakeCheck();
            string logAs = first ? "display-changed" : how == ModeKeeper.How.DisplayChange ? "display-recheck" : "mode-recheck";
            var found = how == ModeKeeper.How.DisplayChange ? ChangeState() : SwitchLog.Pairs("how", how, ChangeState());
            if (res != null && !ProfiledRunning() && !res.Mode.Same(Resolution.Current(res.Gdi)))
            {
                if (first)
                {
                    SwitchLog.Write(logAs, found, SwitchLog.Then, "our mode is gone and no profiled game runs: checking again in 2 s before giving it up");
                    CheckSoon(ModeKeeper.How.DisplayChange, 2000); return;
                }
                SwitchLog.Write(logAs, found, SwitchLog.Then, "our mode is gone and no profiled game runs: no longer ours, nothing is put back but the NVIDIA scaling");
                var r = res;
                res = null;
                DeleteState();
                RestoreScaling(r.Gdi, r.ScalingBack);   // the mode isn't ours any more, but the scaling setting still is
            }
            else if (res != null && ProfiledRunning())
            {
                if (first)
                {
                    // Not judged in the middle of a burst: a game going into exclusive fullscreen, HDR switching or a
                    // monitor waking up reports several changes, and the desktop passes through the saved mode on the way.
                    SwitchLog.Write(logAs, found, SwitchLog.Then, "a profiled game runs: deciding about our mode 1 s after the last change");
                    CheckSoon(ModeKeeper.How.DisplayChange, 1000);
                }
                else
                {
                    SwitchLog.Write(logAs, found, SwitchLog.Then, "a profiled game runs: deciding about our mode now");
                    CheckMode(how);
                }
            }
            else SwitchLog.Write(logAs, found, SwitchLog.Then,
                res == null ? "no mode of ours: nothing to do"
                : InOurMode() ? "still in our mode: nothing to do"
                : "our mode's size and rate are still set, without its stretch, and no profiled game runs: nothing to do until the grace period ends");
            if (!ProfiledRunning()) RestoreStrayScaling();   // a deferred restore: maybe the desktop is back at its saved mode now
            island.Relayout();   // the top-centre may have moved
            UpdateTray();
        }

        /// <summary>Profiles changed while games run: a new or changed one applies at once, a removed one goes back.</summary>
        void ReapplyProfiles()
        {
            if (res != null)
            {
                Resolution.Mode p;
                bool keep = profiles.TryGetValue(res.Game, out p) && Matches(res, p, StretchOf(res.Game));
                if (!keep) SwitchLog.Write("profiles-changed", "ours", OursText(res), SwitchLog.Then, "the profile behind our mode was removed or changed: putting the mode back, then applying what the lists say now");
                if (!keep) RevertResolution();
            }
            foreach (var r in running.Values.ToList())
                if (profiles.ContainsKey(r.Name) && ApplyResolution(r.Name) && resHinted.Add(r.Name))
                    Hint(ResHint(r.Name));
            UpdateTray();
        }

        void SaveState()
        {
            WriteState(FormatModeState(StateOf(res)));
        }

        /// <summary>A mode of ours as it is written to resolution.state, with the mode it goes back to.</summary>
        static ModeState StateOf(ActiveMode a)
        {
            return new ModeState { Gdi = a.Gdi, Id = a.Id, Width = a.Mode.Width, Height = a.Mode.Height, Hz = a.Mode.Hz,
                ScalingBack = a.ScalingBack, AreaWidth = a.Area == null ? 0 : a.Area.Width, AreaHeight = a.Area == null ? 0 : a.Area.Height,
                BackWidth = a.Back == null ? 0 : a.Back.Width, BackHeight = a.Back == null ? 0 : a.Back.Height, BackHz = a.Back == null ? 0 : a.Back.Hz };
        }

        /// <summary>
        /// resolution.state: the mode this app set and what to put back. Width 0 is a record of the NVIDIA scaling setting only,
        /// left to put back after the mode itself was gone.
        /// </summary>
        internal sealed class ModeState
        {
            public string Gdi;            // \\.\DISPLAY1, as it was when written
            public string Id;             // the display across reboots (Hdr.Display.Id), or null: a file from an older version
            public int Width, Height, Hz;
            public int ScalingBack = -1;  // the NVIDIA scaling setting to put back, or -1
            public int AreaWidth, AreaHeight;   // the area a stretched mode fills, or 0
            public int BackWidth, BackHeight, BackHz;   // the saved mode when it was set – what it goes back to – or 0: not recorded

            /// <summary>The recorded mode to go back to, or null.</summary>
            public Resolution.Mode Back()
            {
                return BackWidth > 0 && BackHeight > 0 && BackHz > 0 ? new Resolution.Mode { Width = BackWidth, Height = BackHeight, Hz = BackHz } : null;
            }

            public static ModeState ScalingOnly(string gdi, int back, string id)
            {
                return new ModeState { Gdi = gdi, Id = id, ScalingBack = back };
            }
        }

        /// <summary>
        /// "\\.\DISPLAY1|2368|1332|240|7|0|0|\\?\DISPLAY#…|2560|1440|240": GDI name, mode, scaling to put back, stretched area,
        /// display Id, the mode to go back to (left out if it isn't known). Older versions read the first seven fields the
        /// same way and ignore the rest.
        /// </summary>
        internal static string FormatModeState(ModeState m)
        {
            return m.Gdi + "|" + m.Width + "|" + m.Height + "|" + m.Hz + "|" + m.ScalingBack + "|" + m.AreaWidth + "|" + m.AreaHeight
                + (m.Back() != null ? "|" + m.Id + "|" + m.BackWidth + "|" + m.BackHeight + "|" + m.BackHz : string.IsNullOrEmpty(m.Id) ? "" : "|" + m.Id);
        }

        /// <summary>
        /// resolution.state as written by this version or an older one ("gdi|w|h|hz", "…|scaling", "…|scaling|areaW|areaH"), or
        /// null if it isn't one.
        /// </summary>
        internal static ModeState ParseModeState(string text)
        {
            if (text == null) return null;
            string[] f = text.Trim().Split('|');
            int w, h, hz, back = -1, areaW = 0, areaH = 0;
            if (f.Length < 4 || f[0].Trim().Length == 0 || !int.TryParse(f[1], out w) || !int.TryParse(f[2], out h) || !int.TryParse(f[3], out hz)
                || (f.Length > 4 && !int.TryParse(f[4], out back))
                || (f.Length > 6 && (!int.TryParse(f[5], out areaW) || !int.TryParse(f[6], out areaH)))) return null;
            if (w < 0 || h < 0 || hz < 0 || areaW < 0 || areaH < 0) return null;
            string id = f.Length > 7 ? f[7].Trim() : "";
            int backW = 0, backH = 0, backHz = 0;
            if (f.Length > 10 && !(int.TryParse(f[8], out backW) && int.TryParse(f[9], out backH) && int.TryParse(f[10], out backHz) && backW > 0 && backH > 0 && backHz > 0))
                backW = backH = backHz = 0;   // a broken mode to go back to: as if none was recorded
            return new ModeState { Gdi = f[0].Trim(), Id = id.Length > 0 ? id : null, Width = w, Height = h, Hz = hz,
                ScalingBack = back < 0 ? -1 : back, AreaWidth = areaW, AreaHeight = areaH, BackWidth = backW, BackHeight = backH, BackHz = backHz };
        }

        /// <summary>The active mode is what this profile – and its stretched resolution, if it has one – asks for.</summary>
        static bool Matches(ActiveMode a, Resolution.Mode p, Resolution.Mode own)
        {
            var area = a.Area ?? a.Mode;
            if (p.Width != area.Width || p.Height != area.Height || (p.Hz != 0 && Math.Abs(p.Hz - a.Mode.Hz) > 1)) return false;   // one Hz off is the same rate (Fallback.Rate)
            // A plain area used because the stretch couldn't be had (Fallback.cs) is still that profile's mode.
            if (a.Area == null) return own == null || (a.FellBack != null && own.Width == a.FellBack.Width && own.Height == a.FellBack.Height);
            return own != null && own.Width == a.Mode.Width && own.Height == a.Mode.Height;
        }

        /// <summary>The game's own resolution to stretch over its competitive one – or null: it has none, or it doesn't fit.</summary>
        Resolution.Mode StretchOf(string name)
        {
            Resolution.Mode own, p;
            return stretches.TryGetValue(name, out own) && profiles.TryGetValue(name, out p) && Fits(own, p) ? own : null;
        }

        /// <summary>The GPU's scaler only enlarges, so a stretched resolution has to lie inside the area (and not be the area).</summary>
        static bool Fits(Resolution.Mode own, Resolution.Mode area)
        {
            return own.Width <= area.Width && own.Height <= area.Height && (own.Width < area.Width || own.Height < area.Height);
        }

        static void WriteState(string text)
        {
            Guard.Save(StateFile, text);
        }

        void DeleteState()
        {
            Guard.Delete(StateFile);
        }

        /// <summary>
        /// A previous run was killed while a game had its resolution: if the desktop is still in that mode, take it over –
        /// the first Rescan adopts the running game, or the constructor puts the display back if the game is gone.
        /// (Windows drops a CDS_FULLSCREEN mode the moment the process that set it dies – but not a stretched one, which
        /// stays until the next mode change: this is what puts that back.) The NVIDIA scaling
        /// setting is saved by the driver, so it does survive: it is put back too, or handed to the running game's session.
        /// </summary>
        void RecoverResolution()
        {
            string text;
            try
            {
                if (!File.Exists(StateFile)) return;
                text = File.ReadAllText(StateFile);
            }
            catch (IOException) { return; }
            catch (UnauthorizedAccessException) { return; }
            var m = ParseModeState(text);
            if (m == null)
            {
                SwitchLog.Write("recover-mode", SwitchLog.Then, "resolution.state isn't readable as one: deleted, nothing taken over");
                DeleteState(); return;
            }
            var mode = new Resolution.Mode { Width = m.Width, Height = m.Height, Hz = m.Hz };
            if (m.Width == 0 || !mode.Same(Resolution.Current(m.Gdi)))   // the mode is gone (killed, reboot, put back by hand)
            {
                SwitchLog.Write("recover-mode", "display", m.Gdi, "stateMode", m.Width == 0 ? null : mode, "current", Resolution.Current(m.Gdi), "scalingBack", m.ScalingBack, SwitchLog.Then,
                    (m.Width == 0 ? "a scaling-only record" : "the mode a previous run set is gone") + (m.ScalingBack >= 0 ? ": its NVIDIA scaling is still to put back" : ": nothing left to put back"));
                // The scaling setting outlives it, and stays in the file – as a scaling-only record – until it is back, so
                // another crash before then still has it.
                strayScaling = m.ScalingBack;
                strayDisplay = m.Id ?? m.Gdi;
                if (m.ScalingBack >= 0) WriteState(FormatModeState(ModeState.ScalingOnly(m.Gdi, m.ScalingBack, strayDisplay)));
                else DeleteState();
                return;
            }
            // What it goes back to is the saved mode – unless our own temporary mode is what got saved before the crash:
            // then the mode recorded in the file, or the panel's native one (ModeKeeper.RecoveredBack).
            string backWhy;
            var saved = Resolution.Saved(m.Gdi);
            var goBack = ModeKeeper.RecoveredBack(mode, saved, m.Back(), Resolution.Read(m.Gdi), out backWhy);
            res = new ActiveMode { Gdi = m.Gdi, Id = m.Id ?? DisplayIdOf(m.Gdi), Mode = mode, Back = goBack, Game = "", ScalingBack = m.ScalingBack,
                Area = m.AreaWidth > 0 && m.AreaHeight > 0 ? new Resolution.Mode { Width = m.AreaWidth, Height = m.AreaHeight, Hz = m.Hz } : null };
            res.Centred = res.Area == null && Centred(Resolution.Read(m.Gdi));
            SwitchLog.Write("recover-mode", "display", m.Gdi, "ours", OursText(res), "saved", saved, "recorded", m.Back(), "back", goBack, "backFrom", backWhy, "inOurMode", InOurMode(), SwitchLog.Then,
                "the desktop is still in the mode a previous run set: taken over (a running profiled game adopts it, otherwise it is put back)");
        }

        void LoadProfiles()
        {
            if (!File.Exists(ProfilesFile)) SaveProfiles();
            string[] lines;
            try { lines = File.ReadAllLines(ProfilesFile); }
            catch (IOException) { return; }   // file busy (being saved) – the watcher fires again
            catch (UnauthorizedAccessException e) { Guard.Log("Couldn't read " + ProfilesFile, e); return; }

            var newProfiles = new Dictionary<string, Resolution.Mode>();
            var newStretches = new Dictionary<string, Resolution.Mode>();
            var newLabels = new Dictionary<string, string>();
            foreach (string raw in lines)
            {
                string name, label;
                Resolution.Mode p, own;
                if (!ParseProfile(raw, out name, out p, out own, out label)) continue;
                newProfiles[name] = p;
                if (own != null) newStretches[name] = own;
                if (label.Length > 0) newLabels[name] = label;
            }
            foreach (var kv in newProfiles)   // a changed line gets its "doesn't exist" notice again
            {
                Resolution.Mode old, oldOwn, newOwn;
                stretches.TryGetValue(kv.Key, out oldOwn);
                newStretches.TryGetValue(kv.Key, out newOwn);
                bool same = profiles.TryGetValue(kv.Key, out old) && old.Same(kv.Value) && (oldOwn == null ? newOwn == null : oldOwn.Same(newOwn));
                if (!same) ForgetNotices(kv.Key);
            }
            profiles = newProfiles;
            stretches = newStretches;
            profileLabels = newLabels;
        }

        /// <summary>
        /// One line of resolutions.txt: the game, its competitive resolution, the stretched one (or null: none, or too small)
        /// and the note after #. False for anything else – a note, a blank line, a typo.
        /// </summary>
        internal static bool ParseProfile(string raw, out string name, out Resolution.Mode mode, out Resolution.Mode own, out string label)
        {
            string line = raw;
            name = null;
            mode = own = null;
            label = "";
            int hash = line.IndexOf('#');
            if (hash >= 0)
            {
                label = line.Substring(hash + 1).Trim();
                line = line.Substring(0, hash);
            }
            var m = ProfileLine.Match(line.Trim());
            if (!m.Success) return false;
            name = Proc.Normalize(m.Groups[1].Value);
            if (name == null) return false;
            mode = new Resolution.Mode
            {
                Width = int.Parse(m.Groups[2].Value), Height = int.Parse(m.Groups[3].Value),
                Hz = m.Groups[4].Success ? int.Parse(m.Groups[4].Value) : 0
            };
            if (mode.Width < 320 || mode.Height < 200) return false;
            if (m.Groups[5].Success)
            {
                own = new Resolution.Mode { Width = int.Parse(m.Groups[5].Value), Height = int.Parse(m.Groups[6].Value) };
                if (own.Width < 320 || own.Height < 200) own = null;
            }
            return true;
        }

        // "cs2.exe  2368x1332", "cs2.exe 2368 × 1332 @ 240", "cs2.exe 2368x1332 stretch 1776x1332" (the note after # is already gone)
        internal static readonly Regex ProfileLine = new Regex(@"^(.+?)\s+(\d{3,5})\s*[x×]\s*(\d{3,5})(?:\s*@\s*(\d{2,4})(?:\s*hz)?)?"
            + @"(?:\s+stretch(?:ed)?\s*:?\s*(\d{3,5})\s*[x×]\s*(\d{3,5}))?$", RegexOptions.IgnoreCase);

        /// <summary>A game's profile changed: it gets its one-time notices again.</summary>
        void ForgetNotices(string name)
        {
            notices.Forget(name);
        }

        static readonly string[] ProfileHeader =
        {
            "# Display Switch - competitive resolutions. While one of these games runs, the screen switches to this",
            "# resolution, shown 1:1 in the middle with black borders, and goes back when the game closes.",
            "# One game per line: exe, resolution, optional @refresh rate (otherwise the current one). Text after # is a note.",
            "# The resolution must be in Windows' list: create it in NVIDIA Control Panel > Change resolution > Customize.",
            "# For a game in borderless (fullscreen windowed) mode, add  stretch 1776x1332  after the resolution: the game",
            "# then runs at that lower resolution, stretched over the competitive one. It has to fit inside it.",
            "# Easier: the tray icon, Games, then the game: its page sets both. Changes apply as soon as you save.",
            ""
        };

        void SaveProfiles()
        {
            string[] old;
            if (!ReadForSave(ProfilesFile, out old)) return;
            Guard.Save(ProfilesFile, ProfileFile(old, profiles, stretches, LabelOf));
        }

        /// <summary>
        /// The lines of a file about to be saved again (null: there is none yet), so the save can keep what it doesn't
        /// understand. False if it exists but can't be read: then it isn't saved, rather than saved without those lines.
        /// </summary>
        static bool ReadForSave(string path, out string[] lines)
        {
            lines = null;
            try
            {
                if (File.Exists(path)) lines = File.ReadAllLines(path);
                return true;
            }
            catch (IOException e) { Guard.Log("Not saved, couldn't read " + path + " first", e); }
            catch (UnauthorizedAccessException e) { Guard.Log("Not saved, couldn't read " + path + " first", e); }
            return false;
        }

        /// <summary>
        /// resolutions.txt as it is to be saved, from the lines it has now (null: a new file, which gets the header). A game's
        /// line whose resolution didn't change stays exactly as written; a changed one is rewritten in its place; a removed
        /// one goes, as does a second line for the same game. Games that have no line yet are added at the end. Every line
        /// that isn't a resolution – notes, blank lines, a line with a typo – stays where it is, word for word.
        /// </summary>
        internal static List<string> ProfileFile(IList<string> old, IDictionary<string, Resolution.Mode> profiles,
            IDictionary<string, Resolution.Mode> stretches, Func<string, string> labelOf)
        {
            var lines = new List<string>();
            var done = new HashSet<string>();
            foreach (string raw in old ?? ProfileHeader)
            {
                string name, label;
                Resolution.Mode mode, own, want, wantOwn;
                if (!ParseProfile(raw, out name, out mode, out own, out label)) { lines.Add(raw); continue; }
                if (!profiles.TryGetValue(name, out want) || !done.Add(name)) continue;
                stretches.TryGetValue(name, out wantOwn);
                bool same = mode.Same(want) && (own == null ? wantOwn == null : own.Same(wantOwn));
                lines.Add(same ? raw : FormatProfile(name, want, wantOwn, labelOf(name)));
            }
            lines.AddRange(profiles.Keys.Where(g => !done.Contains(g)).OrderBy(labelOf, StringComparer.OrdinalIgnoreCase).Select(g =>
            {
                Resolution.Mode own;
                stretches.TryGetValue(g, out own);
                return FormatProfile(g, profiles[g], own, labelOf(g));
            }));
            return lines;
        }

        /// <summary>"cs2.exe   2368x1332@240   stretch 1776x1332   # Counter-Strike 2" (no note if it would only repeat the name).</summary>
        static string FormatProfile(string g, Resolution.Mode p, Resolution.Mode own, string label)
        {
            string line = (g + ".exe").PadRight(35) + " " + (p.Width + "x" + p.Height + (p.Hz > 0 ? "@" + p.Hz : "")).PadRight(16)
                + (own == null ? "" : " " + ("stretch " + own.Width + "x" + own.Height).PadRight(18));
            return label == g ? line.TrimEnd() : line + " # " + label;
        }

        /// <summary>From the flyout: this game's resolution (null: off). Saved, and applied right away if the game runs.</summary>
        void SetProfile(string name, Resolution.Mode mode)
        {
            Resolution.Mode own;
            if (mode == null) { profiles.Remove(name); stretches.Remove(name); }
            else
            {
                profiles[name] = mode;
                if (stretches.TryGetValue(name, out own) && !Fits(own, mode)) stretches.Remove(name);   // no longer inside the new area
            }
            ForgetNotices(name);
            SaveProfiles();
            ForgetRemoved();
            Rescan();
            ReapplyProfiles();
        }

        /// <summary>This game's own resolution, stretched over its competitive one (null: none). Saved and applied right away.</summary>
        void SetStretch(string name, Resolution.Mode own)
        {
            if (!profiles.ContainsKey(name)) return;
            if (own == null) stretches.Remove(name);
            else stretches[name] = new Resolution.Mode { Width = own.Width, Height = own.Height };
            ForgetNotices(name);
            SaveProfiles();
            ReapplyProfiles();
        }

        // ---- SDR brightness per game ----
        //
        // Windows' "SDR content brightness" slider sets how bright everything that isn't HDR looks while HDR is on: the
        // desktop, SDR games, a game's own SDR menus. A game can have its own level (brightness.txt, or the flyout). While
        // it runs with HDR on, the display gets that level; afterwards the level from before comes back – only if it is
        // still the one this app set, as with resolutions. Moving the slider yourself while the game runs wins: the level is
        // then left as you set it, during the game and after. Reading the level is documented; setting it is not, and uses
        // the same call as Windows' own slider (see Hdr.SetSdrWhite).

        Dictionary<string, int> brightness = new Dictionary<string, int>();   // name → slider position 0…100
        string brightKey;      // the display whose level this app changed: its Id (see Hdr.Display.Id), which a reboot keeps
        int brightBack = -1;   // its level before (raw, see Hdr.SdrWhite), or -1: nothing changed
        int brightSet = -1;    // the level this app set
        string brightLeft;     // the display whose level the user moved during the game: left alone until no game wants a level

        /// <summary>The level a running game asks for, as a slider position, or -1.</summary>
        int WantedBrightness()
        {
            if (paused) return -1;
            foreach (var r in running.Values)
            {
                int p;
                if (brightness.TryGetValue(r.Name, out p)) return p;
            }
            return -1;
        }

        /// <summary>Gives the HDR display the level a running game wants, or puts the old one back once none does.</summary>
        void SyncBrightness()
        {
            int want = WantedBrightness();
            if (want < 0) brightLeft = null;   // the user's own level counted for the game it was set during
            var d = want < 0 ? null : HdrDisplayFor(running.Values.Select(r => r.Name).FirstOrDefault(brightness.ContainsKey));
            if (d == null || !d.On) { RestoreBrightness(); return; }
            if (d.Is(brightLeft)) return;   // the user's level, not ours: left as it is
            if (brightBack >= 0 && !d.Is(brightKey)) RestoreBrightness();   // another display now
            int now = Hdr.SdrWhite(d), level = Hdr.LevelOf(want);
            if (now < 0) return;   // not readable: leave it alone
            if (UserMovedBrightness(now, level, brightBack, brightSet))
            {
                // Not ours any more: no longer set, and not put back when the game closes.
                brightLeft = d.Id;
                ForgetBrightness();
                return;
            }
            if (brightBack < 0) { brightBack = now; brightKey = d.Id; }
            if (now != level && !Hdr.SetSdrWhite(d, level)) { brightBack = -1; return; }
            if (brightSet != level) { brightSet = level; WriteBrightState(); }
        }

        /// <summary>The level from before, if the display still has the one this app set.</summary>
        void RestoreBrightness()
        {
            if (brightBack < 0) return;
            var d = Hdr.All().FirstOrDefault(x => x.Is(brightKey));   // a level from before a reboot finds its display by Id
            if (d != null && Hdr.SdrWhite(d) == brightSet) Hdr.SetSdrWhite(d, brightBack);
            ForgetBrightness();
        }

        void ForgetBrightness()
        {
            brightBack = brightSet = -1;
            brightKey = null;
            Guard.Delete(BrightStateFile);
        }

        /// <summary>
        /// While a game has its level: the slider was moved by someone else – it is neither the level this app set, nor the
        /// game's level (changed in the flyout just now), nor the level from before. Only once this app has set one.
        /// </summary>
        internal static bool UserMovedBrightness(int now, int want, int back, int set)
        {
            return back >= 0 && set >= 0 && now >= 0 && now != set && now != want && now != back;
        }

        void WriteBrightState()
        {
            Guard.Save(BrightStateFile, FormatBrightState(brightKey, brightBack, brightSet));
        }

        /// <summary>brightness.state: "display Id|level before|level set".</summary>
        internal static string FormatBrightState(string display, int back, int set)
        {
            return display + "|" + back + "|" + set;
        }

        /// <summary>
        /// brightness.state, or false if it isn't one. The display is an Id (\\?\DISPLAY#…) or, written by an older version,
        /// "adapter:target" for that boot only – which still finds its display if Windows wasn't restarted since, and is
        /// otherwise dropped.
        /// </summary>
        internal static bool ParseBrightState(string text, out string display, out int back, out int set)
        {
            display = null;
            back = set = -1;
            if (text == null) return false;
            string[] f = text.Trim().Split('|');
            if (f.Length != 3 || f[0].Trim().Length == 0 || !int.TryParse(f[1], out back) || !int.TryParse(f[2], out set) || back < 0 || set < 0)
            {
                back = set = -1;
                return false;
            }
            display = f[0].Trim();
            return true;
        }

        /// <summary>A previous run ended while a game had its level: take it over, so the next sync puts it back.</summary>
        void RecoverBrightness()
        {
            try
            {
                if (!File.Exists(BrightStateFile)) return;
                string display;
                int back, set;
                if (ParseBrightState(File.ReadAllText(BrightStateFile), out display, out back, out set))
                {
                    brightKey = display;
                    brightBack = back;
                    brightSet = set;
                    RestoreBrightness();   // a game that still runs gets its level again with the first sync
                }
                else Guard.Delete(BrightStateFile);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        void LoadBrightness()
        {
            if (!File.Exists(BrightnessFile)) SaveBrightness();
            string[] lines;
            try { lines = File.ReadAllLines(BrightnessFile); }
            catch (IOException) { return; }   // file busy (being saved) – the watcher fires again
            catch (UnauthorizedAccessException e) { Guard.Log("Couldn't read " + BrightnessFile, e); return; }
            brightness = ParseBrightness(lines);
        }

        /// <summary>"cyberpunk2077.exe  60  # note" → cyberpunk2077: 60. A level is Windows' slider position, 0…100.</summary>
        internal static Dictionary<string, int> ParseBrightness(IEnumerable<string> lines)
        {
            var all = new Dictionary<string, int>();
            foreach (string raw in lines)
            {
                string name;
                int p;
                if (ParseBrightness(raw, out name, out p)) all[name] = p;
            }
            return all;
        }

        /// <summary>One line of brightness.txt. False for anything else – a note, a blank line, a level above 100.</summary>
        internal static bool ParseBrightness(string raw, out string name, out int percent)
        {
            string line = raw;
            name = null;
            percent = -1;
            int hash = line.IndexOf('#');
            if (hash >= 0) line = line.Substring(0, hash);
            var m = BrightnessLine.Match(line.Trim());
            if (!m.Success) return false;
            name = Proc.Normalize(m.Groups[1].Value);
            percent = int.Parse(m.Groups[2].Value);
            return name != null && percent <= 100;
        }

        static readonly Regex BrightnessLine = new Regex(@"^(.+?)\s+(\d{1,3})\s*%?$");

        static readonly string[] BrightnessHeader =
        {
            "# Display Switch - SDR content brightness per game: Windows' slider of that name (0-100), set while the game",
            "# runs with HDR on and put back when it closes. It decides how bright the desktop, SDR games and a game's",
            "# SDR menus look in HDR. One game per line: exe and level. Text after # is a note. Easier: the flyout's",
            "# Games tab, then the game: SDR brightness on its page.",
            ""
        };

        void SaveBrightness()
        {
            string[] old;
            if (!ReadForSave(BrightnessFile, out old)) return;
            Guard.Save(BrightnessFile, BrightnessFileLines(old, brightness, LabelOf));
        }

        /// <summary>
        /// brightness.txt as it is to be saved, kept the same way as resolutions.txt (see ProfileFile): unchanged levels
        /// as written, changed ones rewritten in place, removed ones gone, new ones at the end, everything else untouched.
        /// </summary>
        internal static List<string> BrightnessFileLines(IList<string> old, IDictionary<string, int> levels, Func<string, string> labelOf)
        {
            var lines = new List<string>();
            var done = new HashSet<string>();
            foreach (string raw in old ?? BrightnessHeader)
            {
                string name;
                int p, want;
                if (!ParseBrightness(raw, out name, out p)) { lines.Add(raw); continue; }
                if (!levels.TryGetValue(name, out want) || !done.Add(name)) continue;
                lines.Add(p == want ? raw : FormatBrightness(name, want, labelOf(name)));
            }
            lines.AddRange(levels.Keys.Where(g => !done.Contains(g)).OrderBy(labelOf, StringComparer.OrdinalIgnoreCase)
                .Select(g => FormatBrightness(g, levels[g], labelOf(g))));
            return lines;
        }

        static string FormatBrightness(string g, int percent, string label)
        {
            string line = (g + ".exe").PadRight(35) + " " + percent;
            return label == g ? line : line.PadRight(41) + " # " + label;
        }

        /// <summary>From the flyout: this game's level (-1: Windows' own). Saved, and applied right away if the game runs.</summary>
        void SetBrightness(string name, int percent)
        {
            if (percent < 0) brightness.Remove(name);
            else brightness[name] = Math.Min(100, percent);
            SaveBrightness();
            UpdateTray();
        }

        // ---- recording apps ----

        void LoadRecorders()
        {
            if (!File.Exists(RecordersFile))
            {
                var lines = new List<string>
                {
                    "# Display Switch - more recording apps for \"SDR while recording\". While one of these runs, HDR stays off.",
                    "# One exe per line; text after # is the name shown. Already known: " + string.Join(", ", BuiltInRecorders.Values.Distinct()) + ".",
                    "# Only add an app you open to record or stream: one that runs in the background all day keeps HDR off all day.",
                    "# For example:  recorder.exe  # My recorder",
                    ""
                };
                Guard.Save(RecordersFile, lines);
            }
            string[] text;
            try { text = File.ReadAllLines(RecordersFile); }
            catch (IOException) { return; }   // file busy (being saved) – the watcher fires again
            catch (UnauthorizedAccessException e) { Guard.Log("Couldn't read " + RecordersFile, e); return; }
            recorders = ParseRecorders(text);
            if (sdrWhileRecording && recorder != null && !recorders.ContainsKey(recorder)) SetRecorder(null);   // taken off the list
        }

        /// <summary>The built-in recording apps and the lines of recorders.txt ("medal.exe  # Medal").</summary>
        internal static Dictionary<string, string> ParseRecorders(IEnumerable<string> lines)
        {
            var all = new Dictionary<string, string>(BuiltInRecorders);
            foreach (string raw in lines)
            {
                string line = raw, label = "";
                int hash = line.IndexOf('#');
                if (hash >= 0)
                {
                    label = line.Substring(hash + 1).Trim();
                    line = line.Substring(0, hash);
                }
                string name = line.Trim().Length == 0 ? null : Proc.Normalize(line);
                if (name != null) all[name] = label.Length > 0 ? label : all.ContainsKey(name) ? all[name] : "";
            }
            return all;
        }

        // ---- game list ----

        void LoadList()
        {
            if (!File.Exists(ListFile)) SaveList();
            string[] lines;
            try { lines = File.ReadAllLines(ListFile); }
            catch (IOException) { return; }   // file busy (being saved) – the watcher fires again
            catch (UnauthorizedAccessException e) { Guard.Log("Couldn't read " + ListFile, e); return; }

            var newGames = new HashSet<string>();
            var newExcluded = new HashSet<string>();
            var newLabels = new Dictionary<string, string>();
            foreach (string raw in lines)
            {
                string line = raw, label = "";
                int hash = line.IndexOf('#');
                if (hash >= 0)
                {
                    label = line.Substring(hash + 1).Trim();
                    line = line.Substring(0, hash);
                }
                line = line.Trim();
                if (line.Length == 0) continue;
                bool off = line.StartsWith("-");
                string name = Proc.Normalize(off ? line.Substring(1) : line);
                if (name == null) continue;
                (off ? newExcluded : newGames).Add(name);
                if (label.Length > 0) newLabels[name] = label;
            }
            newGames.ExceptWith(newExcluded);
            games = newGames;
            excluded = newExcluded;
            labels = newLabels;
        }

        void SaveList()
        {
            var lines = new List<string>
            {
                "# Display Switch - HDR turns on while one of these runs. One exe per line; text after # is just a note.",
                "# Games are found automatically (Steam, Epic, GOG, Ubisoft, EA, Battle.net, Riot, Rockstar, Xbox, and game folders on your drives).",
                "# Missing one? Add its exe on a new line. Changes apply as soon as you save.",
                "# To stop a game switching HDR, put a - in front of it (deleting the line lets the next scan add it again).",
                ""
            };
            lines.AddRange(games.OrderBy(LabelOf, StringComparer.OrdinalIgnoreCase).Select(g => ListLine(g, false)));
            if (excluded.Count > 0)
            {
                lines.Add("");
                lines.Add("# Never switch HDR for these:");
                lines.AddRange(excluded.OrderBy(LabelOf, StringComparer.OrdinalIgnoreCase).Select(g => ListLine(g, true)));
            }
            Guard.Save(ListFile, lines);
        }

        string LabelOf(string name)
        {
            string l;
            if (labels.TryGetValue(name, out l) && l.Length > 0) return l;
            return profileLabels.TryGetValue(name, out l) && l.Length > 0 ? l : name;
        }

        string ListLine(string name, bool off)
        {
            string line = (off ? "-" : "") + name + ".exe";
            string l;
            return labels.TryGetValue(name, out l) && l.Length > 0 ? line.PadRight(35) + " # " + l : line;
        }

        void ScanInBackground(bool announce)
        {
            if (scanning) return;
            scanning = true;
            var t = new Thread(() =>
            {
                List<GameScanner.Found> found;
                try { found = GameScanner.Scan(); }
                catch { found = new List<GameScanner.Found>(); }
                ui.Post(_ => { scanning = false; MergeFound(found, announce); }, null);
            }) { IsBackground = true, Priority = ThreadPriority.BelowNormal };
            t.Start();
        }

        void MergeFound(List<GameScanner.Found> found, bool announce)
        {
            var added = found.Where(f => !games.Contains(f.Name) && !excluded.Contains(f.Name)).ToList();
            foreach (var f in added)
            {
                games.Add(f.Name);
                labels[f.Name] = f.Label;
            }
            // Games already on the list without a note (added by hand) get the launcher's name.
            var unnamed = found.Where(f => games.Contains(f.Name) && (!labels.ContainsKey(f.Name) || labels[f.Name] == "added by hand")).ToList();
            foreach (var f in unnamed) labels[f.Name] = f.Label;
            if (unnamed.Count > 0 && added.Count == 0) SaveList();
            if (added.Count > 0)
            {
                SaveList();
                Rescan();
            }
            if (added.Count > 0)
            {
                string count = added.Count + " new game" + (added.Count == 1 ? "" : "s"), names = string.Join(", ", added.Select(f => f.Label).Take(3)) + (added.Count > 3 ? "…" : "");
                Announce(new Island.Act { Id = "scan", Title = count + " found", Sub = names, Text = count + " found", Balloon = "Found " + count + ": " + names });
            }
            else if (announce) Announce(new Island.Act { Id = "scan", Text = "No new games", Balloon = "No new games found." });
            flyout.Sync();
            Trim();
        }

        void Add(string name)
        {
            games.Add(name);
            excluded.Remove(name);
            if (!labels.ContainsKey(name)) labels[name] = "added by hand";
            SaveList();
            Rescan();
        }

        void Remove(string name)
        {
            games.Remove(name);
            excluded.Add(name);   // so the next scan doesn't add it again
            SaveList();
            ForgetRemoved();
        }

        // ---- tray ----

        /// <summary>
        /// "HDR on – Dying Light: The Beast", "Waiting for a game", … (the tray icon's tooltip), with a second line
        /// such as "2368 × 1332 – Counter-Strike 2" while a competitive resolution is set.
        /// </summary>
        string StatusText()
        {
            var displays = Hdr.All();
            var hdrGames = running.Values.Where(r => games.Contains(r.Name)).Select(r => DisplayName(r.Name)).Distinct().ToList();
            if (paused) return "Paused – " + HotKey.Text + " resumes";
            string hdr = !displays.Any(d => d.Supported) ? "No HDR display"
                : recorder != null && !displays.Any(d => d.On) ? "SDR – " + RecorderName(recorder) + " running"
                : hdrGames.Count > 0 ? "HDR on – " + string.Join(", ", hdrGames)
                : displays.Any(d => d.On) ? "HDR is on" : "Waiting for a game";
            if (res == null) return hdr;
            string mode = (StretchLost() ? res.Mode.Text + " not stretched"
                : res.Last == ModeKeeper.Verdict.GameOwnMode ? res.Text + " (the game set its own mode)"
                : res.Last == ModeKeeper.Verdict.GiveUp ? res.Text + " (reset by Windows)" : res.Text) + " – " + DisplayName(res.Game);
            return hdrGames.Count > 0 || recorder != null || displays.Any(d => d.On) ? hdr + "\n" + mode : mode;   // "Waiting for a game" adds nothing here
        }

        /// <summary>"Dying Light: The Beast" (launcher name without "Steam: "), or "game.exe".</summary>
        string DisplayName(string name)
        {
            if (name.Length == 0) return "a game";   // a mode taken over after a restart, before its game is found
            string l = LabelOf(name);
            if (l == name || l == "added by hand") return name + ".exe";
            int colon = l.IndexOf(": ");
            return colon > 0 && colon < 10 ? l.Substring(colon + 2) : l;
        }

        static IEnumerable<string> WindowedApps()
        {
            var skip = new HashSet<string> { "explorer", "hdrswitch", "applicationframehost", "textinputhost", "systemsettings",
                "shellexperiencehost", "searchhost", "startmenuexperiencehost", "lockapp", "notepad" };
            var names = new SortedSet<string>();
            var windowed = Proc.WindowedPids();   // one pass over the windows, not one per process
            foreach (var p in Proc.Snapshot())
                if (windowed.Contains(p.Key) && !skip.Contains(p.Value)) names.Add(p.Value);
            return names;
        }

        void UpdateTray()
        {
            SyncBrightness();   // every change of state comes through here
            tray.Icon = Hdr.All().Any(d => d.On) ? iconOn : iconOff;
            string text = "Display Switch " + Updater.CurrentText + "\n" + StatusText();
            tray.Text = text.Length > 63 ? text.Substring(0, 62) + "…" : text;   // NotifyIcon limit
            if (flyout != null) flyout.Sync();
        }

        void Notify(string text)
        {
            tray.ShowBalloonTip(3000, "Display Switch", text, ToolTipIcon.None);
        }

        static bool IsStartup()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                    return k != null && k.GetValue(RunValue) != null;
            }
            catch (System.Security.SecurityException e) { Guard.Log("Couldn't read Start with Windows", e); }
            catch (UnauthorizedAccessException e) { Guard.Log("Couldn't read Start with Windows", e); }
            catch (IOException e) { Guard.Log("Couldn't read Start with Windows", e); }
            return false;
        }

        /// <summary>Start with Windows on or off. A Run key that can't be changed (a policy, security software) is logged and left as it is.</summary>
        static void SetStartup(bool on)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (on) k.SetValue(RunValue, "\"" + Application.ExecutablePath + "\"");
                    else k.DeleteValue(RunValue, false);
                }
            }
            catch (System.Security.SecurityException e) { Guard.Log("Couldn't change Start with Windows", e); }
            catch (UnauthorizedAccessException e) { Guard.Log("Couldn't change Start with Windows", e); }
            catch (IOException e) { Guard.Log("Couldn't change Start with Windows", e); }
        }

        /// <summary>
        /// The tray icon: the app's mark, amber for HDR and grey for SDR. The H is three whole-pixel bars rather than a
        /// letter from a font, so it is as crisp at 16 px as at 20, 24 and 32.
        /// </summary>
        static Icon MakeIcon(bool on)
        {
            int s = SystemInformation.SmallIconSize.Width;
            using (var bmp = new Bitmap(s, s))
            using (var g = Graphics.FromImage(bmp))
            using (var bars = new SolidBrush(on ? Color.Black : Color.White))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                Theme.Fill(g, new RectangleF(0, 0, s, s), s * 0.3f, on ? Theme.Amber : Theme.Grey);
                g.SmoothingMode = SmoothingMode.None;
                int t = Math.Max(2, (int)(s / 8.0 + 0.5)), m = (int)(s * 0.25 + 0.5), top = (int)(s * 0.22 + 0.5);
                g.FillRectangle(bars, m, top, t, s - 2 * top);
                g.FillRectangle(bars, s - m - t, top, t, s - 2 * top);
                g.FillRectangle(bars, m, (s - t) / 2, s - 2 * m, t);
                return Icon.FromHandle(bmp.GetHicon());   // two icons for the app's lifetime – handles are never freed
            }
        }

        static void Trim()
        {
            Native.SetProcessWorkingSetSize(Native.GetCurrentProcess(), (IntPtr)(-1), (IntPtr)(-1));
        }

        static TrayApp current;   // for LastChance
        Thread uiThread;
        static int dying, puttingBack;

        /// <summary>
        /// The process is about to end of an error on another thread: put back what this app switched, as Exit would –
        /// on the UI thread if it answers within 3 s, otherwise from here. Runs once; never throws.
        /// </summary>
        internal static void LastChance()
        {
            var app = current;
            if (app == null || Interlocked.Exchange(ref dying, 1) != 0) return;
            try
            {
                if (Thread.CurrentThread == app.uiThread) { app.PutBack(); return; }
                var done = new ManualResetEvent(false);   // not disposed: a late answer from the UI thread may still set it
                app.ui.Post(_ => { try { app.PutBack(); } finally { done.Set(); } }, null);
                if (!done.WaitOne(3000)) app.PutBack();   // the UI thread is stuck: from this thread then
            }
            catch (Exception e) { Guard.Log("Putting the displays back", e); }
        }

        /// <summary>
        /// Everything this app switched goes back, whatever is still running: the mode, NVIDIA scaling, SDR brightness, HDR.
        /// Only the displays – nothing on screen is touched, so it can run from any thread. Each step on its own, so one that
        /// fails doesn't stop the rest; once only.
        /// </summary>
        void PutBack()
        {
            if (Interlocked.Exchange(ref puttingBack, 1) != 0) return;
            SwitchLog.Write("last-chance", SwitchLog.Then, "the app is ending on an error: putting back whatever is still switched");
            Action[] steps = { RevertResolution, RestoreStrayScaling, RestoreBrightness, AllToSdr };
            foreach (var step in steps)
            {
                try { step(); }
                catch (Exception e) { Guard.Log("Putting the displays back", e); }
            }
        }

        protected override void ExitThreadCore()
        {
            watcher.Dispose();
            steam.Dispose();
            hotkey.Dispose();
            SystemEvents.DisplaySettingsChanged -= onDisplayChanged;
            LogSystemEvents(false);
            SwitchLog.Write("exit", "ours", OursText(res), "hdrOwned", owned.Count, "running", running.Count, SwitchLog.Then, "putting back whatever is still switched");
            scanTimer.Stop();
            modeCheck.Stop();
            stretchVerify.Stop();
            sdrTimer.Stop();
            steamTimeout.Stop();
            foreach (var r in running.Values.Where(x => x.Wait != null))
            {
                r.Wait.Unregister(null);
                r.Handle.Dispose();
            }
            running.Clear();
            islandOn = false;   // for this exit only: nothing is announced any more
            Settle();   // SDR and the saved mode, whatever is still running
            island.Dispose();
            flyout.Dispose();
            tray.Visible = false;
            tray.Dispose();
            base.ExitThreadCore();
        }
    }

    /// <summary>
    /// Updates from this project's GitHub releases. Only api.github.com and the release downloads of this repository
    /// are ever contacted, and a download is only installed if its SHA256 matches the release's SHA256SUMS.txt – and
    /// that file is only believed if SHA256SUMS.txt.sig is its signature by the project's update key. The checksum
    /// alone shows the download arrived intact; the signature shows SHA256SUMS.txt came from the release workflow,
    /// which holds the key as a secret, and not from a file changed or added to the release afterwards.
    /// The signed file also names the version it was built as ("# version 1.2.3"), and that must be the release's
    /// tag: the tag itself isn't signed, so without it an old signed build could be put up under a newer tag – every
    /// copy would "update" to the old build, which would then find the same release newer than itself, again and again.
    /// The check runs before a release is offered (SHA256SUMS.txt and its signature are small) and again on install.
    /// Install: the running exe is renamed to HdrSwitch.old.exe (Windows allows that), the new one takes its name
    /// and is started with --updated; it waits for this copy to exit and removes the old file.
    /// </summary>
    static class Updater
    {
        // The repository was renamed from hdr-auto-switch to game-display-switch. Releases are looked up under the
        // new name first and the old one as a fallback, and downloads from either are accepted (GitHub serves the
        // same repository under both), so updates keep working on either side of the rename.
        public const string Repo = "sxnfer/game-display-switch";
        const string OldRepo = "sxnfer/hdr-auto-switch";
        const string ExeName = "HdrSwitch.exe";   // kept: copies before 1.6.0 look for this asset name
        static readonly string[] DownloadPrefixes =
        {
            "https://github.com/" + Repo + "/releases/download/",
            "https://github.com/" + OldRepo + "/releases/download/",
        };

        public sealed class Release
        {
            public Version Version;
            public string ExeUrl, SumsUrl, SigUrl;
            public string VersionText { get { return Text(Version); } }
        }

        public static readonly Version Current = ThreeParts(typeof(Updater).Assembly.GetName().Version);
        public static string CurrentText { get { return Text(Current); } }

        /// <summary>Dev builds (0.0.0, built without a version) never update themselves.</summary>
        public static bool Enabled { get { return Current > new Version(0, 0, 0); } }

        /// <summary>The latest release if it is newer than this copy and its signed checksum file vouches for it, otherwise null.</summary>
        public static Release Latest()
        {
            string json;
            try { using (var web = Client()) json = web.DownloadString("https://api.github.com/repos/" + Repo + "/releases/latest"); }
            catch (System.Net.WebException) { using (var web = Client()) json = web.DownloadString("https://api.github.com/repos/" + OldRepo + "/releases/latest"); }
            using (var web = Client()) return Offer(json, Current, web.DownloadData, PublicKeyXml);
        }

        /// <summary>
        /// The release to offer: newer than current, with all three files, and SHA256SUMS.txt (fetched with download,
        /// together with its signature) signed by the key and naming the tag's version. Otherwise null, and why goes to
        /// the log – a release that fails here would only fail again at "Verifying".
        /// </summary>
        internal static Release Offer(string json, Version current, Func<string, byte[]> download, string publicKeyXml)
        {
            var r = Parse(json, current);
            if (r == null) return null;
            byte[] sums = download(r.SumsUrl);
            string sig = Encoding.ASCII.GetString(download(r.SigUrl));
            string problem = Problem(r, sums, sig, publicKeyXml, current);
            if (problem == null) return r;
            Guard.Log("Update to " + r.VersionText + " not offered: " + problem);
            return null;
        }

        /// <summary>The release in GitHub's JSON if it is newer than current and has both files, otherwise null.</summary>
        internal static Release Parse(string json, Version current)
        {
            var data = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);

            Version v;
            string tag = data.ContainsKey("tag_name") ? data["tag_name"] as string : null;
            if (tag == null || !Version.TryParse(tag.TrimStart('v', 'V'), out v)) return null;
            var r = new Release { Version = ThreeParts(v) };
            if (r.Version <= current || !data.ContainsKey("assets")) return null;

            foreach (object o in (System.Collections.IEnumerable)data["assets"])
            {
                var a = o as Dictionary<string, object>;
                if (a == null) continue;
                string name = a["name"] as string, url = a["browser_download_url"] as string;
                if (url == null || !DownloadPrefixes.Any(p => url.StartsWith(p, StringComparison.OrdinalIgnoreCase))) continue;
                if (name == ExeName) r.ExeUrl = url;
                else if (name == "SHA256SUMS.txt") r.SumsUrl = url;
                else if (name == "SHA256SUMS.txt.sig") r.SigUrl = url;
            }
            return r.ExeUrl != null && r.SumsUrl != null && r.SigUrl != null ? r : null;   // an unsigned release is never offered
        }

        /// <summary>
        /// Downloads the release and checks it all over again (the files may have changed since it was offered):
        /// the signature, the signed version, and the exe against its checksum. Returns the exe; nothing is written.
        /// Throws with a short reason on failure. step: told when it moves on to "Verifying", for the update button.
        /// </summary>
        public static byte[] Download(Release r, Action<string> step)
        {
            byte[] bytes, sums;
            string sig;
            using (var web = Client())
            {
                sums = web.DownloadData(r.SumsUrl);
                sig = Encoding.ASCII.GetString(web.DownloadData(r.SigUrl));
                bytes = web.DownloadData(r.ExeUrl);
            }
            step("Verifying");
            return Checked(r, sums, sig, bytes, PublicKeyXml, Current);
        }

        /// <summary>The exe if the checksum file vouches for this release and the exe matches it; otherwise throws why.</summary>
        internal static byte[] Checked(Release r, byte[] sums, string sig, byte[] exe, string publicKeyXml, Version current)
        {
            string problem = Problem(r, sums, sig, publicKeyXml, current);
            if (problem != null) throw new Exception(problem + " – nothing was changed.");
            string actual;
            using (var sha = System.Security.Cryptography.SHA256.Create())
                actual = BitConverter.ToString(sha.ComputeHash(exe)).Replace("-", "").ToLowerInvariant();
            if (actual != ExpectedHash(Encoding.ASCII.GetString(sums))) throw new Exception("the download doesn't match its checksum – nothing was changed.");
            return exe;
        }

        /// <summary>
        /// Why a release's checksum file can't be believed, or null if it can: it must carry a valid signature by the
        /// key, name a version, that version must be the release's tag and newer than current, and it must have the exe's checksum.
        /// </summary>
        internal static string Problem(Release r, byte[] sums, string sig, string publicKeyXml, Version current)
        {
            if (!Verified(sums, sig, publicKeyXml)) return "the release's signature doesn't match";
            string text = Encoding.ASCII.GetString(sums);
            Version signed = SignedVersion(text);
            if (signed == null) return "the release's checksum file doesn't say which version it is";
            if (signed != r.Version) return "the release is tagged " + r.VersionText + " but was built as " + Text(signed);
            if (signed <= current) return "the release isn't newer than " + Text(current);
            if (ExpectedHash(text) == null) return "the release has no checksum for " + ExeName;
            return null;
        }

        /// <summary>
        /// The version a checksum file was signed for, from its "# version 1.2.3" line (sha256sum -c passes over lines
        /// that start with #), or null: no such line, one that isn't a version, or more than one.
        /// </summary>
        internal static Version SignedVersion(string sums)
        {
            Version found = null;
            foreach (string raw in (sums ?? "").Split('\n'))
            {
                string l = raw.Trim();
                if (!l.StartsWith("#")) continue;
                string[] parts = l.Substring(1).Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0 || parts[0] != "version") continue;   // some other comment
                Version v;
                if (found != null || parts.Length != 2 || !Version.TryParse(parts[1], out v)) return null;   // trust neither of two
                found = ThreeParts(v);
            }
            return found;
        }

        /// <summary>
        /// Puts the checked exe in place of the running one and starts it with --updated. True: the new version is
        /// starting. False: it is in place but didn't start – it runs from the next start. Throws with a short reason if
        /// it couldn't be put in place; the running version is then back under its own name, or the log says why not.
        /// </summary>
        public static bool Swap(byte[] bytes)
        {
            string exe = Application.ExecutablePath;
            string dir = Path.GetDirectoryName(exe);
            string fresh = Path.Combine(dir, "HdrSwitch.new.exe"), old = Path.Combine(dir, "HdrSwitch.old.exe");

            try
            {
                File.WriteAllBytes(fresh, bytes);
                if (File.Exists(old)) File.Delete(old);
                File.Move(exe, old);
            }
            catch (UnauthorizedAccessException)
            {
                throw new Exception("no permission to write to " + dir + ".");
            }
            try { File.Move(fresh, exe); }
            catch (Exception e)
            {
                Guard.Log("Update: putting the new version in place", e);
                if (PutBack(old, exe)) throw;
                throw new Exception(e.Message + " " + ExeName + " couldn't be put back either: rename HdrSwitch.old.exe to " + ExeName + ".", e);
            }

            try
            {
                Process.Start(new ProcessStartInfo(exe, "--updated") { UseShellExecute = false, WorkingDirectory = dir });
                return true;
            }
            catch (Exception e)
            {
                Guard.Log("Update: starting the new version", e);
                return false;
            }
        }

        /// <summary>After a failed swap: the running version back under its own name, so the sign-in start finds it.</summary>
        static bool PutBack(string old, string exe)
        {
            try { File.Move(old, exe); return true; }
            catch (Exception e) { Guard.Log("Update: moving the running version back", e); }
            try { File.Copy(old, exe, true); return true; }   // the running exe can't move, but it can still be read
            catch (Exception e) { Guard.Log("Update: copying the running version back", e); }
            return false;
        }

        /// <summary>
        /// The public half of the update key (RSA 3072). The private half signs SHA256SUMS.txt in the release workflow
        /// (the UPDATE_SIGNING_KEY secret). A new key means only copies built with it accept the releases signed by it.
        /// </summary>
        internal const string PublicKeyXml = "<RSAKeyValue><Modulus>x2yxTidmEx/SLfU+h1US6nznvuUl+VxJ+0KgQZciLRio04OU8sJYRxco6bF8r5nMFqrMcMiGXoUTkG+peJVqTypTgyodY6jpQt0VIlRjz01Qm0NWdCiOBglvtuREaxJdrzTTEDPO6cEtrlSM31C0/OKH+wEvZmcyrevcbnyNNiN0/mZb7bZZY68rJa6gfRAh/23OGHW9Iaxoj+qNmPN8/BbX4WkjUJKFuWaU9G1k9ncsfgRmouG/CQtPBUzMe5N3Tt2JKBU0852gBi5h6N2nvUT7s2/H6RNf7Vr9t25ty1Lm0x1kAV+7PP78GpJ53tjZ0bHLnOpBy1GVUkiaHot0Qzv07tSLs9FYeiuQXy6HNH4HBjSGL56H+8i3aCQYEtpckS0s9dOhaNQXtigra6XhuXdGO4vnxNmd9lIlBgL7urxHnYCjtbDho0LuofwuFoxMPG0UBHGNmVDMDB1uJRzRgh1/Y5S8rU/ds7d/GSVYdPKxsTw2EHJyHdU9W5g0PHM/</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>";

        /// <summary>True if sig (base64) is a valid RSA-SHA256 signature of data by this key.</summary>
        internal static bool Verified(byte[] data, string sig, string publicKeyXml)
        {
            try
            {
                byte[] s = Convert.FromBase64String((sig ?? "").Trim());
                if (s.Length == 0) return false;
                using (var rsa = new System.Security.Cryptography.RSACryptoServiceProvider())
                {
                    rsa.PersistKeyInCsp = false;
                    rsa.FromXmlString(publicKeyXml);
                    return rsa.VerifyData(data, "SHA256", s);
                }
            }
            catch (FormatException) { return false; }
            catch (System.Security.Cryptography.CryptographicException) { return false; }
        }

        /// <summary>The SHA256 that SHA256SUMS.txt gives for the exe, in lower case, or null.</summary>
        internal static string ExpectedHash(string sums)
        {
            var line = sums.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.EndsWith(" " + ExeName) || l.EndsWith("*" + ExeName));
            return line == null ? null : line.Split(' ')[0].ToLowerInvariant();
        }

        /// <summary>Removes what an update left behind (the previous exe can only go once it has exited).</summary>
        public static void CleanUp()
        {
            string dir = Path.GetDirectoryName(Application.ExecutablePath);
            foreach (string f in new[] { "HdrSwitch.old.exe", "HdrSwitch.new.exe" })
                try { File.Delete(Path.Combine(dir, f)); } catch { }
        }

        static System.Net.WebClient Client()
        {
            System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
            var web = new System.Net.WebClient();
            web.Headers[System.Net.HttpRequestHeader.UserAgent] = "game-display-switch/" + CurrentText;   // required by the GitHub API
            return web;
        }

        static Version ThreeParts(Version v)
        {
            return v == null ? new Version(0, 0, 0) : new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }

        static string Text(Version v)
        {
            return v.Major + "." + v.Minor + "." + v.Build;
        }
    }

    /// <summary>
    /// Steam marks a game as running (HKCU\Software\Valve\Steam\Apps\[id]\Running = 1) as soon as Play is pressed,
    /// before the game's process exists. Windows signals the registry change, so this costs nothing while idle.
    /// </summary>
    sealed class SteamWatcher : IDisposable
    {
        const string AppsKey = @"Software\Valve\Steam\Apps";
        const uint REG_NOTIFY_CHANGE_NAME = 0x1, REG_NOTIFY_CHANGE_LAST_SET = 0x4;
        readonly ManualResetEvent stop = new ManualResetEvent(false);
        readonly Action<int> onStarted;   // called on the watcher thread

        public SteamWatcher(Action<int> onStarted)
        {
            this.onStarted = onStarted;
            new Thread(Watch) { IsBackground = true, Name = "Steam watcher" }.Start();
        }

        /// <summary>
        /// Follows Steam's key while it is there. Without it (Steam installed after the app started, or uninstalled) it
        /// looks again once a minute: one registry lookup, so it costs nothing either. Meanwhile, and if the key can't be
        /// read, switching still works through the process scan.
        /// </summary>
        void Watch()
        {
            bool logged = false;
            while (true)
            {
                try
                {
                    using (var key = Registry.CurrentUser.OpenSubKey(AppsKey))
                        if (key != null && Follow(key)) return;
                }
                catch (Exception e)   // the key vanished mid-watch or is unreadable
                {
                    if (!logged) Guard.Log("Steam watcher", e);
                    logged = true;
                }
                if (stop.WaitOne(60000)) return;
            }
        }

        /// <summary>Reports each app Steam marks as running until stopped (true), or until Windows won't watch the key (false).</summary>
        bool Follow(RegistryKey key)
        {
            using (var changed = new AutoResetEvent(false))
            {
                var wasRunning = RunningApps(key);
                while (true)
                {
                    if (RegNotifyChangeKeyValue(key.Handle, true, REG_NOTIFY_CHANGE_NAME | REG_NOTIFY_CHANGE_LAST_SET, changed.SafeWaitHandle, true) != 0) return false;
                    if (WaitHandle.WaitAny(new WaitHandle[] { stop, changed }) == 0) return true;
                    var now = RunningApps(key);
                    foreach (int id in now)
                        if (!wasRunning.Contains(id)) onStarted(id);
                    wasRunning = now;
                }
            }
        }

        static HashSet<int> RunningApps(RegistryKey apps)
        {
            var ids = new HashSet<int>();
            foreach (string sub in apps.GetSubKeyNames())
            {
                int id;
                if (!int.TryParse(sub, out id)) continue;
                using (var k = apps.OpenSubKey(sub))
                {
                    object v = k == null ? null : k.GetValue("Running");
                    if (v is int && (int)v == 1) ids.Add(id);
                }
            }
            return ids;
        }

        public void Dispose()
        {
            stop.Set();
        }

        [DllImport("advapi32.dll")]
        static extern int RegNotifyChangeKeyValue(SafeRegistryHandle key, bool subtree, uint filter, SafeWaitHandle ev, bool async);
    }

    /// <summary>A key combination that works from anywhere, even in a game: Ctrl+Alt+H pauses or resumes switching.</summary>
    sealed class HotKey : NativeWindow, IDisposable
    {
        public const string Text = "Ctrl+Alt+H";
        const int Id = 1, WM_HOTKEY = 0x312;
        const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_NOREPEAT = 0x4000;
        readonly Action pressed;

        /// <summary>False if another program has the combination already.</summary>
        public readonly bool Registered;

        public HotKey(Action pressed)
        {
            this.pressed = pressed;
            CreateHandle(new CreateParams());   // a hidden window, only for the message
            Registered = RegisterHotKey(Handle, Id, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, (uint)Keys.H);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY && (int)m.WParam == Id) pressed();
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            if (Handle == IntPtr.Zero) return;
            if (Registered) UnregisterHotKey(Handle, Id);
            DestroyHandle();
        }

        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    }

    sealed class ProcessWaitHandle : WaitHandle
    {
        public ProcessWaitHandle(IntPtr handle) { SafeWaitHandle = new SafeWaitHandle(handle, true); }
    }

    /// <summary>
    /// Finds installed games: launcher libraries first (Steam, Epic, GOG, Ubisoft), then games that the EA app,
    /// Battle.net, Riot and Rockstar registered with Windows, the Xbox app's folders, and the usual game folders on
    /// every fixed drive (Games, EA Games, …). For each game folder the largest exe that is not a setup / crash
    /// reporter / anti-cheat helper is taken as the game.
    /// </summary>
    public static class GameScanner
    {
        public sealed class Found { public string Name, Label; }

        static readonly string[] DriveRoots =
        {
            "Games", "XboxGames", "GOG Games", "Epic Games", @"Program Files\Epic Games", @"Program Files (x86)\GOG Galaxy\Games",
            @"Program Files\EA Games", @"Program Files (x86)\Origin Games", @"Program Files (x86)\Ubisoft\Ubisoft Game Launcher\games",
            @"Program Files\Rockstar Games", @"SteamLibrary\steamapps\common", @"Program Files (x86)\Steam\steamapps\common",
            @"Program Files\Steam\steamapps\common", "Riot Games"
        };

        // Not games: launcher/overlay tools that would turn HDR on whenever they are in front. Matched against a label and
        // a folder name with _ and - read as spaces (see Plain), so Steam's "wallpaper_engine" folder is Wallpaper Engine.
        // "UE 5.4" is an Unreal Engine install (Epic's UE_5.4 folder).
        static readonly Regex Tool = new Regex(@"Redistributable|Proton|Steam ?Linux ?Runtime|Steamworks|SteamVR|Wallpaper Engine|Lossless Scaling|Soundpad|Unreal Engine|^UE \d|Steam Controller Configs", RegexOptions.IgnoreCase);
        // Launchers that install into the same folders as their games and start with Windows: Riot Games\Riot Client,
        // Rockstar Games\Social Club. A folder called Launcher only counts under Rockstar Games and Epic Games (LauncherOf).
        static readonly Regex LauncherDir = new Regex(@"^(Riot Client|Social Club)$", RegexOptions.IgnoreCase);
        static readonly Regex LauncherOf = new Regex(@"^(Rockstar Games|Epic Games)$", RegexOptions.IgnoreCase);
        // dosbox and scummvm: GOG runs every DOS and adventure classic through its own copy of one of them, so the
        // process (all this app sees of a running game) is the same for all of them and can't be told apart.
        static readonly Regex SkipExe = new Regex(@"^(unins.*|setup.*|.*install.*|.*redist.*|dxsetup|ue\dprereq.*|.*crash.*|.*report.*|.*easyanticheat.*|eac(launcher)?|.*battleye.*|be_.*|.*update.*|.*helper.*|.*cef.*|.*browser.*|dotnet.*|vc_.*|.*touchup.*|.*cleanup.*|.*activation.*|quicksfv|.*benchmark.*|.*server.*|.*editor.*|.*diagnostic.*|.*dump.*|7z.*|python.*|.*register.*|vconsole.*|resourcecompiler|.*viewer|hlmv.*|.*tools?|steam|epicgameslauncher|galaxyclient|upc|ubisoftconnect|eadesktop|origin|rockstarservice|launcher|gamelaunchhelper|dosbox.*|scummvm)$", RegexOptions.IgnoreCase);
        static readonly Regex SkipDir = new Regex(@"^(_?commonredist|_?redist|redistributables?|directx|vcredist|dotnet.*|support|installers?|__installer|prereq.*|thirdparty|easyanticheat|battleye|crashpad|paks|movies?|videos?|audio|sounds?|localization|shaders?|__overlay|tools|mods?|\.git)$", RegexOptions.IgnoreCase);

        public static List<Found> Scan()
        {
            var folders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // game folder → label
            var exes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);      // exe named by a launcher → label
            Try("Steam", () => Steam(folders));
            Try("Epic", () => Epic(folders, exes));
            Try("GOG", () => Gog(folders, exes));
            Try("Ubisoft", () => Ubisoft(folders));
            Try("installed apps", () => Installed(folders));
            Try("Xbox", () => Xbox(folders));
            Try("drives", () => Drives(folders));

            var found = new Dictionary<string, Found>();
            foreach (var kv in exes) Add(found, kv.Key, kv.Value);
            foreach (var kv in folders)
                if (kv.Value != null) Add(found, MainExe(kv.Key), kv.Value);   // null: a tool or a launcher (AddFolder)
            return found.Values.ToList();
        }

        /// <summary>
        /// True for a tool or a launcher rather than a game, from its label ("Steam: Wallpaper Engine"), its folder's name
        /// ("wallpaper_engine") and the name of the folder that one is in ("Rockstar Games"). Every source's folders go
        /// through here (AddFolder), so a folder that one pass knows as a tool isn't added by another under another name.
        /// </summary>
        internal static bool NotAGame(string label, string folder, string parent)
        {
            string l = Plain(label), f = Plain(folder);
            int colon = l.IndexOf(": ");
            string bare = colon > 0 && colon < 10 ? l.Substring(colon + 2) : l;   // without the launcher's "Steam: "
            return Tool.IsMatch(l) || Tool.IsMatch(f) || LauncherDir.IsMatch(bare) || LauncherDir.IsMatch(f)
                || (f.Equals("Launcher", StringComparison.OrdinalIgnoreCase) && LauncherOf.IsMatch(Plain(parent)));
        }

        /// <summary>True for an exe that is never taken as a game: a setup, a crash reporter, a launcher, DOSBox, …</summary>
        internal static bool SkippedExe(string name)
        {
            return SkipExe.IsMatch(name);
        }

        /// <summary>"wallpaper_engine" → "wallpaper engine": _ and - as spaces, runs of spaces as one.</summary>
        static string Plain(string s)
        {
            return s == null ? "" : Regex.Replace(s.Replace('_', ' ').Replace('-', ' '), @"\s+", " ").Trim();
        }

        static void Add(Dictionary<string, Found> found, string exe, string label)
        {
            string name = exe == null ? null : Proc.Normalize(exe);
            if (name == null || SkipExe.IsMatch(name) || found.ContainsKey(name)) return;
            found[name] = new Found { Name = name, Label = label };
        }

        /// <summary>
        /// Adds a game's folder, unless it is a tool or a launcher (NotAGame): that one is kept with a null label, so the
        /// pass over the drives' game folders, which goes by folder names, can't add it again. False for such a folder.
        /// </summary>
        internal static bool AddFolder(Dictionary<string, string> folders, string path, string label)
        {
            try
            {
                path = Path.GetFullPath(path).TrimEnd('\\', '/');
                string known;
                if (folders.TryGetValue(path, out known)) return known != null;
                string parent = Path.GetDirectoryName(path);
                if (NotAGame(label, Path.GetFileName(path), parent == null ? null : Path.GetFileName(parent)))
                {
                    folders[path] = null;
                    return false;
                }
                if (Directory.Exists(path)) folders[path] = label;
            }
            catch (ArgumentException) { }
            catch (NotSupportedException) { }
            return true;
        }

        /// <summary>Every Steam library's steamapps folder.</summary>
        static List<string> SteamApps()
        {
            var result = new List<string>();
            string steam = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
            if (string.IsNullOrEmpty(steam)) return result;
            var libs = new List<string> { steam };
            Try("Steam's library list", () =>
            {
                string vdf = Path.Combine(steam, @"steamapps\libraryfolders.vdf");
                if (File.Exists(vdf))
                    foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                        libs.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
            });
            foreach (string lib in libs.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                Try("Steam library " + lib, () =>   // a path with a typo, or a drive that is gone, skips only that library
                {
                    string apps = Path.Combine(lib, "steamapps");
                    if (Directory.Exists(apps)) result.Add(apps);
                });
            }
            return result;
        }

        /// <summary>"Steam: Dying Light: The Beast" for a Steam app id – the label the scan gives that game.</summary>
        /// <summary>Never throws: it runs on the UI thread, the moment Play is pressed.</summary>
        public static string SteamLabel(int appId)
        {
            List<string> libraries = null;
            Try("Steam's libraries", () => libraries = SteamApps());
            if (libraries == null) return null;
            foreach (string apps in libraries)
            {
                string label = null;
                Try("Steam app " + appId + " in " + apps, () =>
                {
                    string acf = Path.Combine(apps, "appmanifest_" + appId + ".acf");
                    if (!File.Exists(acf)) return;
                    string text = File.ReadAllText(acf);
                    label = "Steam: " + (Vdf(text, "name") ?? Vdf(text, "installdir"));
                });
                if (label != null) return label;
            }
            return null;
        }

        static void Steam(Dictionary<string, string> folders)
        {
            SteamLibraries(SteamApps(), folders);
        }

        /// <summary>The games in these steamapps folders. A library or a manifest that can't be read skips only itself.</summary>
        internal static void SteamLibraries(IEnumerable<string> libraries, Dictionary<string, string> folders)
        {
            foreach (string apps in libraries)
            {
                string[] manifests = null;
                Try("Steam library " + apps, () => manifests = Directory.GetFiles(apps, "appmanifest_*.acf"));
                if (manifests == null) continue;
                foreach (string acf in manifests)
                {
                    Try(acf, () =>
                    {
                        string text = File.ReadAllText(acf);
                        string dir = Vdf(text, "installdir");
                        if (dir != null) AddFolder(folders, Path.Combine(apps, "common", dir), "Steam: " + (Vdf(text, "name") ?? dir));
                    });
                }
            }
        }

        static void Epic(Dictionary<string, string> folders, Dictionary<string, string> exes)
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Epic\EpicGamesLauncher\Data\Manifests");
            if (!Directory.Exists(dir)) return;
            foreach (string item in Directory.GetFiles(dir, "*.item"))
            {
                Try(item, () =>
                {
                    string json = File.ReadAllText(item);
                    string loc = Json(json, "InstallLocation"), exe = Json(json, "LaunchExecutable");
                    if (loc == null || json.IndexOf("\"games\"", StringComparison.OrdinalIgnoreCase) < 0) return;   // AppCategories: games only
                    string label = "Epic: " + (Json(json, "DisplayName") ?? Path.GetFileName(loc));
                    if (AddFolder(folders, loc, label) && !string.IsNullOrEmpty(exe)) exes[Path.Combine(loc, exe)] = label;
                });
            }
        }

        static void Gog(Dictionary<string, string> folders, Dictionary<string, string> exes)
        {
            using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\GOG.com\Games"))
            {
                if (key == null) return;
                foreach (string id in key.GetSubKeyNames())
                {
                    Try("GOG game " + id, () =>
                    {
                        using (var g = key.OpenSubKey(id))
                        {
                            if (g == null || !string.IsNullOrEmpty(g.GetValue("dependsOn") as string)) return;   // DLC: same folder as its game
                            string path = g.GetValue("path") as string, exe = g.GetValue("exe") as string;
                            string label = "GOG: " + (g.GetValue("gameName") as string ?? id);
                            bool game = path != null ? AddFolder(folders, path, label) : !NotAGame(label, null, null);
                            if (game && exe != null) exes[exe] = label;
                        }
                    });
                }
            }
        }

        static void Ubisoft(Dictionary<string, string> folders)
        {
            using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Ubisoft\Launch\Installs"))
            {
                if (key == null) return;
                foreach (string id in key.GetSubKeyNames())
                {
                    Try("Ubisoft game " + id, () =>
                    {
                        using (var g = key.OpenSubKey(id))
                        {
                            string path = g == null ? null : g.GetValue("InstallDir") as string;
                            if (path != null) AddFolder(folders, path, "Ubisoft: " + Path.GetFileName(path.TrimEnd('\\', '/')));
                        }
                    });
                }
            }
        }

        // Launchers that register their games with Windows' installed apps, by publisher → the label's prefix.
        static readonly KeyValuePair<string, string>[] Publishers =
        {
            new KeyValuePair<string, string>("Electronic Arts", "EA"),
            new KeyValuePair<string, string>("Blizzard Entertainment", "Battle.net"),
            new KeyValuePair<string, string>("Riot Games", "Riot"),
            new KeyValuePair<string, string>("Rockstar Games", "Rockstar"),
        };

        // The launchers themselves and their helpers, which register under the same publishers.
        static readonly Regex LauncherApp = new Regex(@"^(EA app|EA Desktop|Origin|Battle\.net|Blizzard Battle\.net|Riot Client|Rockstar Games Launcher|Rockstar Games Social Club|Social Club|Vanguard|Riot Vanguard)\b", RegexOptions.IgnoreCase);

        /// <summary>"EA: Battlefield 6" for a game an EA app, Battle.net, Riot or Rockstar installed – or null: not one of those, or the launcher itself.</summary>
        internal static string InstalledLabel(string publisher, string name)
        {
            if (string.IsNullOrEmpty(publisher) || string.IsNullOrEmpty(name) || LauncherApp.IsMatch(name.Trim())) return null;
            foreach (var p in Publishers)
                if (publisher.IndexOf(p.Key, StringComparison.OrdinalIgnoreCase) >= 0) return p.Value + ": " + name.Trim();
            return null;
        }

        /// <summary>Games from Windows' list of installed apps (both registry views, and the user's own).</summary>
        static void Installed(Dictionary<string, string> folders)
        {
            const string Uninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
            var roots = new[]
            {
                RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64),
                RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32),
                RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default),
            };
            foreach (var root in roots)
            {
                using (root)
                using (var key = root.OpenSubKey(Uninstall))
                {
                    if (key == null) continue;
                    foreach (string id in key.GetSubKeyNames())
                    {
                        Try("installed app " + id, () =>
                        {
                            using (var g = key.OpenSubKey(id))
                            {
                                if (g == null) return;
                                string label = InstalledLabel(g.GetValue("Publisher") as string, g.GetValue("DisplayName") as string);
                                string path = g.GetValue("InstallLocation") as string;
                                if (label != null && !string.IsNullOrEmpty(path)) AddFolder(folders, path.Trim().Trim('"'), label);
                            }
                        });
                    }
                }
            }
        }

        /// <summary>
        /// The Xbox app's game folders. Each drive it installs to has a .GamingRoot file at its root naming the folder
        /// ("XboxGames" unless it was changed); every game is a folder in it, with the game itself under Content.
        /// </summary>
        static void Xbox(Dictionary<string, string> folders)
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                Try("Xbox games on " + drive.Name, () =>   // one drive that can't be read skips only its own games
                {
                    if (drive.DriveType != DriveType.Fixed || !drive.IsReady) return;
                    string file = Path.Combine(drive.RootDirectory.FullName, ".GamingRoot");
                    if (!File.Exists(file)) return;
                    foreach (string root in GamingRoots(File.ReadAllBytes(file)))
                    {
                        Try("Xbox folder " + root + " on " + drive.Name, () =>
                        {
                            string dir = Path.Combine(drive.RootDirectory.FullName, root.TrimStart('\\', '/'));
                            if (!Directory.Exists(dir)) return;
                            foreach (string game in Directory.GetDirectories(dir))
                            {
                                string content = Path.Combine(game, "Content");
                                AddFolder(folders, Directory.Exists(content) ? content : game, "Xbox: " + Path.GetFileName(game));
                            }
                        });
                    }
                });
            }
        }

        /// <summary>The folders in a .GamingRoot file: "RGBX", a count, then UTF-16 paths each ending in a 0.</summary>
        internal static List<string> GamingRoots(byte[] data)
        {
            var list = new List<string>();
            if (data == null || data.Length < 8 || Encoding.ASCII.GetString(data, 0, 4) != "RGBX") return list;
            foreach (string part in Encoding.Unicode.GetString(data, 8, (data.Length - 8) / 2 * 2).Split('\0'))
            {
                string p = part.Trim();
                if (p.Length > 0 && p.IndexOfAny(Path.GetInvalidPathChars()) < 0 && !list.Contains(p)) list.Add(p);
            }
            return list;
        }

        static void Drives(Dictionary<string, string> folders)
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                Try("game folders on " + drive.Name, () =>
                {
                    if (drive.DriveType != DriveType.Fixed || !drive.IsReady) return;
                    foreach (string root in DriveRoots)
                    {
                        string dir = Path.Combine(drive.RootDirectory.FullName, root);
                        if (Directory.Exists(dir)) Try(dir, () => Subfolders(dir, folders));
                    }
                });
            }
        }

        /// <summary>Each folder in dir as a game, by its folder's name – but not a tool or a launcher (AddFolder).</summary>
        internal static void Subfolders(string dir, Dictionary<string, string> folders)
        {
            foreach (string game in Directory.GetDirectories(dir))
                AddFolder(folders, game, Path.GetFileName(game));
        }

        /// <summary>The largest exe in a game folder (5 levels deep), skipping installers, redistributables and helpers.</summary>
        static string MainExe(string folder)
        {
            string best = null;
            long bestSize = 0;
            var stack = new Stack<KeyValuePair<string, int>>();
            stack.Push(new KeyValuePair<string, int>(folder, 0));
            while (stack.Count > 0)
            {
                var cur = stack.Pop();
                try
                {
                    foreach (string f in Directory.EnumerateFiles(cur.Key, "*.exe"))
                    {
                        if (SkipExe.IsMatch(Path.GetFileNameWithoutExtension(f))) continue;
                        long size = new FileInfo(f).Length;
                        if (size > bestSize) { best = f; bestSize = size; }
                    }
                    if (cur.Value < 5)
                        foreach (string d in Directory.EnumerateDirectories(cur.Key))
                            if (!SkipDir.IsMatch(Path.GetFileName(d))) stack.Push(new KeyValuePair<string, int>(d, cur.Value + 1));
                }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }
            }
            return best;
        }

        internal static string Vdf(string text, string key)
        {
            var m = Regex.Match(text, "\"" + key + "\"\\s+\"([^\"]*)\"");
            return m.Success ? m.Groups[1].Value : null;
        }

        internal static string Json(string text, string key)
        {
            var m = Regex.Match(text, "\"" + key + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            if (!m.Success) return null;
            try { return Regex.Unescape(m.Groups[1].Value); } catch (ArgumentException) { return m.Groups[1].Value; }
        }

        /// <summary>One step of the scan: an error in it (a locked manifest, a library on a drive that is gone) skips that step only, and is logged.</summary>
        static void Try(string what, Action a)
        {
            try { a(); }
            catch (Exception e) { Guard.Log("Game scan skipped " + what + ": " + e.GetType().Name + ": " + e.Message); }
        }
    }

    static class Proc
    {
        const uint Synchronize = 0x100000;

        /// <summary>A handle to wait on for pid's exit, or IntPtr.Zero if Windows doesn't allow it.</summary>
        public static IntPtr OpenForWait(int pid)
        {
            return Native.OpenProcess(Synchronize, false, pid);
        }

        /// <summary>
        /// How long the process has been running, in seconds – or -1 if Windows won't say: a game that runs elevated or
        /// behind anti-cheat can't be opened. (0 would read as "just started".)
        /// </summary>
        public static double AgeSeconds(int pid)
        {
            try { using (var p = Process.GetProcessById(pid)) return Math.Max(0, (DateTime.Now - p.StartTime).TotalSeconds); }
            catch { return -1; }
        }

        /// <summary>All running processes as (pid, normalized name) – a light snapshot, no Process objects.</summary>
        public static List<KeyValuePair<int, string>> Snapshot()
        {
            var list = new List<KeyValuePair<int, string>>(400);
            Snapshot(list);
            return list;
        }

        static readonly Dictionary<string, string> normalized = new Dictionary<string, string>(StringComparer.Ordinal);   // "Game.exe" → "game"
        static readonly int entrySize = Marshal.SizeOf(typeof(ProcessEntry));

        /// <summary>The same, into a list the caller keeps: the scan runs up to four times a second (UI thread only).</summary>
        public static void Snapshot(List<KeyValuePair<int, string>> list)
        {
            list.Clear();
            IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snap == (IntPtr)(-1)) return;
            try
            {
                var e = new ProcessEntry { Size = (uint)entrySize };
                for (bool ok = Process32FirstW(snap, ref e); ok; ok = Process32NextW(snap, ref e))
                    list.Add(new KeyValuePair<int, string>((int)e.ProcessId, Lowered(e.ExeFile)));
            }
            finally { Native.CloseHandle(snap); }
        }

        /// <summary>"Game.exe" → "game". Remembered, so a look at the process list doesn't cut and lower-case every name again.</summary>
        static string Lowered(string exe)
        {
            string name;
            lock (normalized)
            {
                if (normalized.TryGetValue(exe, out name)) return name;
                name = exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exe.Substring(0, exe.Length - 4) : exe;
                name = name.ToLowerInvariant();
                if (normalized.Count > 4096) normalized.Clear();
                normalized[exe] = name;
                return name;
            }
        }

        const uint TH32CS_SNAPPROCESS = 2;

        /// <summary>The screen (\\.\DISPLAY2) of a process's largest visible window, or null while it has none.</summary>
        public static string WindowScreen(int pid)
        {
            IntPtr best = IntPtr.Zero;
            long bestArea = 0;
            EnumWindows((h, l) =>
            {
                uint owner;
                GetWindowThreadProcessId(h, out owner);
                Rect r;
                if (owner != (uint)pid || !IsWindowVisible(h) || IsIconic(h) || !GetWindowRect(h, out r)) return true;
                long w = r.Right - r.Left, ht = r.Bottom - r.Top;
                if (w >= 320 && ht >= 200 && w * ht > bestArea) { best = h; bestArea = w * ht; }   // not a splash's tiny helper
                return true;
            }, IntPtr.Zero);
            if (best == IntPtr.Zero) return null;
            IntPtr mon = MonitorFromWindow(best, 0);   // MONITOR_DEFAULTTONULL
            if (mon == IntPtr.Zero) return null;
            var mi = new MonitorInfoEx { Size = Marshal.SizeOf(typeof(MonitorInfoEx)) };
            return GetMonitorInfo(mon, ref mi) ? mi.Device : null;
        }

        /// <summary>
        /// The processes with a main window: a visible top-level window that no other window owns, as
        /// Process.MainWindowHandle counts one – but found in one pass over the windows instead of one per process.
        /// </summary>
        public static HashSet<int> WindowedPids()
        {
            var pids = new HashSet<int>();
            EnumWindows((h, l) =>
            {
                uint owner;
                if (IsWindowVisible(h) && GetWindow(h, GW_OWNER) == IntPtr.Zero && GetWindowThreadProcessId(h, out owner) != 0) pids.Add((int)owner);
                return true;
            }, IntPtr.Zero);
            return pids;
        }

        const uint GW_OWNER = 4;
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);

        delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr param);
        [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct MonitorInfoEx
        {
            public int Size;
            public Rect Monitor, Work;
            public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
        }
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc callback, IntPtr param);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out Rect r);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct ProcessEntry
        {
            public uint Size, Usage, ProcessId;
            public IntPtr DefaultHeapId;
            public uint ModuleId, Threads, ParentProcessId;
            public int PriorityClassBase;
            public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
        }

        [DllImport("kernel32.dll")] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32FirstW(IntPtr snap, ref ProcessEntry e);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32NextW(IntPtr snap, ref ProcessEntry e);

        /// <summary>"C:\Games\Cyberpunk2077.exe" → "cyberpunk2077" (same form as Process.ProcessName).</summary>
        public static string Normalize(string s)
        {
            try
            {
                s = Path.GetFileName(s.Trim().Trim('"')).ToLowerInvariant();
                if (s.EndsWith(".exe")) s = s.Substring(0, s.Length - 4);
                return s.Length > 0 ? s : null;
            }
            catch (ArgumentException) { return null; }
        }
    }

    /// <summary>Reads and switches Windows HDR per display (Windows display configuration API).</summary>
    public static class Hdr
    {
        [StructLayout(LayoutKind.Sequential)] public struct LUID { public uint Low; public int High; }

        public sealed class Display
        {
            public LUID Adapter;
            public uint Target;
            public string Gdi;       // \\.\DISPLAY1 – matches Screen.DeviceName
            public bool Supported;  // HDR can be turned on
            public bool On;         // HDR is on
            public string Key { get { return Adapter.High + ":" + Adapter.Low + ":" + Target; } }   // for this boot only: the adapter's LUID changes with every start of Windows

            internal string id;
            /// <summary>
            /// Who the display is across reboots, for the app's .state files: the monitor's device path
            /// (\\?\DISPLAY#DEL41B8#…#{…}), which names the monitor and the port it is on. The GDI name if the path can't
            /// be read; that one can change between boots, but is the best there is then.
            /// </summary>
            public string Id
            {
                get
                {
                    if (id == null)
                    {
                        string path = DevicePath(this);
                        id = path.Length > 0 ? path : Gdi != null && Gdi.Length > 0 ? Gdi : Key;
                    }
                    return id;
                }
            }

            /// <summary>This is the display named: by its Id, its GDI name, or its Key (a .state file from an older version, read in the same boot).</summary>
            public bool Is(string idOrName)
            {
                return !string.IsNullOrEmpty(idOrName) && (string.Equals(Id, idOrName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Gdi, idOrName, StringComparison.OrdinalIgnoreCase) || Key == idOrName);
            }
        }

        /// <summary>The monitor's device path (DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME), or "" if it can't be read.</summary>
        static string DevicePath(Display d)
        {
            try
            {
                var t = new TargetName { H = Header(2, typeof(TargetName), d.Adapter, d.Target) };   // GET_TARGET_NAME
                return DisplayConfigGetDeviceInfo(ref t) == 0 && t.Path != null ? t.Path.Trim() : "";
            }
            catch (DllNotFoundException) { return ""; }
            catch (EntryPointNotFoundException) { return ""; }
        }

        /// <summary>The monitor's own name ("LG 27GR95QE", from its EDID), or null if it can't be read.</summary>
        public static string FriendlyName(Display d)
        {
            try
            {
                var t = new TargetName { H = Header(2, typeof(TargetName), d.Adapter, d.Target) };   // GET_TARGET_NAME
                return DisplayConfigGetDeviceInfo(ref t) == 0 && !string.IsNullOrEmpty(t.Friendly) && t.Friendly.Trim().Length > 0 ? t.Friendly.Trim() : null;
            }
            catch (DllNotFoundException) { return null; }
            catch (EntryPointNotFoundException) { return null; }
        }

        /// <summary>The size of DISPLAYCONFIG_TARGET_DEVICE_NAME as marshalled: 420 bytes, as in the Windows SDK.</summary>
        internal static int TargetNameSize { get { return Marshal.SizeOf(typeof(TargetName)); } }

        public static List<Display> All()
        {
            var list = new List<Display>();
            for (int attempt = 0; attempt < 3; attempt++)
            {
                uint np, nm;
                if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out np, out nm) != 0) return list;
                var paths = new PathInfo[np];
                IntPtr modes = Marshal.AllocHGlobal((int)Math.Max(1, nm) * 64);
                try
                {
                    int r = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref np, paths, ref nm, modes, IntPtr.Zero);
                    if (r == ERROR_INSUFFICIENT_BUFFER) continue;   // a display was added meanwhile
                    if (r != 0) return list;
                    for (int i = 0; i < np; i++)
                    {
                        var src = new SourceName { H = Header(1, typeof(SourceName), paths[i].Source.Adapter, paths[i].Source.Id) };
                        var d = new Display
                        {
                            Adapter = paths[i].Target.Adapter,
                            Target = paths[i].Target.Id,
                            Gdi = DisplayConfigGetDeviceInfo(ref src) == 0 ? src.Gdi : ""
                        };
                        Read(d);
                        list.Add(d);
                    }
                    return list;
                }
                finally { Marshal.FreeHGlobal(modes); }
            }
            return list;
        }

        /// <summary>The main display if it can do HDR, otherwise the first one that can.</summary>
        public static Display Primary()
        {
            var hdr = All().Where(d => d.Supported).ToList();
            return hdr.FirstOrDefault(d => d.Gdi == Screen.PrimaryScreen.DeviceName) ?? hdr.FirstOrDefault();
        }

        /// <summary>
        /// The display's SDR white level – Windows' "SDR content brightness" – in its raw unit (1000 = 80 nits), or -1.
        /// </summary>
        public static int SdrWhite(Display d)
        {
            var w = new SdrWhiteLevel { H = Header(11, typeof(SdrWhiteLevel), d.Adapter, d.Target) };   // GET_SDR_WHITE_LEVEL
            return DisplayConfigGetDeviceInfo(ref w) == 0 ? (int)w.Level : -1;
        }

        /// <summary>
        /// Sets the SDR white level. Not in Windows' documentation: device info type 0xFFFFFFEE is what the Settings slider
        /// itself uses (also used by tools such as Twinkle Tray), with "final" meaning the value is kept, not a preview.
        /// </summary>
        public static bool SetSdrWhite(Display d, int level)
        {
            var s = new SetSdrWhiteLevel { H = Header(unchecked((int)0xFFFFFFEE), typeof(SetSdrWhiteLevel), d.Adapter, d.Target), Level = (uint)level, Final = 1 };
            try { return DisplayConfigSetDeviceInfo(ref s) == 0; }
            catch (EntryPointNotFoundException) { return false; }
        }

        /// <summary>Slider position 0…100 → raw level: the slider runs from 80 to 480 nits, and 1000 is 80 nits.</summary>
        public static int LevelOf(int percent) { return 1000 + 50 * Math.Max(0, Math.Min(100, percent)); }

        /// <summary>Raw level → slider position 0…100.</summary>
        public static int PercentOf(int level) { return Math.Max(0, Math.Min(100, (int)Math.Round((level - 1000) / 50.0))); }

        public static bool Set(Display d, bool on)
        {
            // Windows 11 24H2+: SET_HDR_STATE. Older Windows: SET_ADVANCED_COLOR_STATE.
            var s = new SetState { H = Header(16, typeof(SetState), d.Adapter, d.Target), Value = on ? 1u : 0u };
            if (DisplayConfigSetDeviceInfo(ref s) == 0) return true;
            s.H.Type = 10;
            return DisplayConfigSetDeviceInfo(ref s) == 0;
        }

        static void Read(Display d)
        {
            var c2 = new ColorInfo2 { H = Header(15, typeof(ColorInfo2), d.Adapter, d.Target) };
            if (DisplayConfigGetDeviceInfo(ref c2) == 0)
            {
                d.Supported = (c2.Value & 0x10) != 0;   // highDynamicRangeSupported
                d.On = c2.ActiveColorMode == 2;         // DISPLAYCONFIG_ADVANCED_COLOR_MODE_HDR
                return;
            }
            var c = new ColorInfo { H = Header(9, typeof(ColorInfo), d.Adapter, d.Target) };
            if (DisplayConfigGetDeviceInfo(ref c) == 0)
            {
                d.Supported = (c.Value & 0x1) != 0 && (c.Value & 0x8) == 0;   // supported and not force-disabled
                d.On = (c.Value & 0x2) != 0;
            }
        }

        static DeviceInfoHeader Header(int type, Type structType, LUID adapter, uint id)
        {
            return new DeviceInfoHeader { Type = type, Size = Marshal.SizeOf(structType), Adapter = adapter, Id = id };
        }

        const uint QDC_ONLY_ACTIVE_PATHS = 2;
        const int ERROR_INSUFFICIENT_BUFFER = 122;

        [StructLayout(LayoutKind.Sequential)] struct PathSource { public LUID Adapter; public uint Id; public uint ModeIdx; public uint Flags; }
        [StructLayout(LayoutKind.Sequential)]
        struct PathTarget
        {
            public LUID Adapter; public uint Id; public uint ModeIdx; public int Technology; public int Rotation; public int Scaling;
            public uint RefreshNum, RefreshDen; public int ScanLineOrdering; public int Available; public uint Flags;
        }
        [StructLayout(LayoutKind.Sequential)] struct PathInfo { public PathSource Source; public PathTarget Target; public uint Flags; }
        [StructLayout(LayoutKind.Sequential)] struct DeviceInfoHeader { public int Type; public int Size; public LUID Adapter; public uint Id; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SourceName { public DeviceInfoHeader H; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Gdi; }
        // DISPLAYCONFIG_TARGET_DEVICE_NAME (wingdi.h): header 20, flags 4, outputTechnology 4, edidManufactureId 2,
        // edidProductCodeId 2, connectorInstance 4, monitorFriendlyDeviceName WCHAR[64] = 128, monitorDevicePath WCHAR[128] = 256:
        // 420 bytes, every field at its natural alignment, so no padding.
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct TargetName
        {
            public DeviceInfoHeader H; public uint Flags; public int Technology; public ushort EdidMaker; public ushort EdidProduct; public uint Connector;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string Friendly;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Path;
        }
        [StructLayout(LayoutKind.Sequential)] struct ColorInfo { public DeviceInfoHeader H; public uint Value; public uint Encoding; public uint Bits; }
        [StructLayout(LayoutKind.Sequential)] struct ColorInfo2 { public DeviceInfoHeader H; public uint Value; public uint Encoding; public uint Bits; public uint ActiveColorMode; }
        [StructLayout(LayoutKind.Sequential)] struct SetState { public DeviceInfoHeader H; public uint Value; }
        [StructLayout(LayoutKind.Sequential)] struct SdrWhiteLevel { public DeviceInfoHeader H; public uint Level; }
        [StructLayout(LayoutKind.Sequential)] struct SetSdrWhiteLevel { public DeviceInfoHeader H; public uint Level; public byte Final; }

        [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);
        [DllImport("user32.dll")] static extern int QueryDisplayConfig(uint flags, ref uint numPaths, [Out] PathInfo[] paths, ref uint numModes, IntPtr modes, IntPtr topology);
        [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref SourceName info);
        [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref TargetName info);
        [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref ColorInfo info);
        [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref ColorInfo2 info);
        [DllImport("user32.dll")] static extern int DisplayConfigSetDeviceInfo(ref SetState info);
        [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref SdrWhiteLevel info);
        [DllImport("user32.dll")] static extern int DisplayConfigSetDeviceInfo(ref SetSdrWhiteLevel info);
    }

    static class Native
    {
        [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll")] public static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] public static extern bool SetProcessWorkingSetSize(IntPtr proc, IntPtr min, IntPtr max);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        // Motion and the island's layered window.
        [StructLayout(LayoutKind.Sequential)] public struct BlendFunction { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
        [StructLayout(LayoutKind.Sequential)]
        public struct BitmapInfo   // BITMAPINFOHEADER: 32-bit needs no colour table after it
        {
            public int Size, Width, Height;
            public short Planes, BitCount;
            public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant;
        }
        [DllImport("dwmapi.dll")] public static extern int DwmFlush();
        [DllImport("winmm.dll")] public static extern uint timeBeginPeriod(uint ms);
        [DllImport("winmm.dll")] public static extern uint timeEndPeriod(uint ms);
        [DllImport("user32.dll")] public static extern bool SystemParametersInfo(uint action, uint param, out bool value, uint winIni);
        [DllImport("user32.dll")] public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref Point dst, ref Size size, IntPtr hdcSrc, ref Point src, int key, ref BlendFunction blend, int flags);
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr hwnd, int index, int value);
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(Point p, uint flags);
        [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
        [DllImport("shell32.dll")] public static extern int SHQueryUserNotificationState(out int state);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
    }
}
