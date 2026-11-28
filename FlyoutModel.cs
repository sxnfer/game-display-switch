// The flyout's decisions about what it says, kept apart from the window so the checks can run them: the three tabs and
// which one it opens on, and the Live tab – the status line and the stack of five layers (HDR, SDR brightness,
// competitive area, stretch, NVIDIA scaling) worked out from a snapshot of the app's state; and the Games tab – the
// line under each game in the library, the search, and a game's page: its five layer cards, the to-scale picture of
// its competitive area, the SDR brightness slider and the setup hint.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace HdrSwitch
{
    /// <summary>The flyout's three pages: what is switched now, the games, everything global.</summary>
    internal enum FlyoutTab { Live, Games, Settings }

    sealed partial class TrayApp
    {
        internal static readonly FlyoutTab[] FlyoutTabs = { FlyoutTab.Live, FlyoutTab.Games, FlyoutTab.Settings };

        /// <summary>The tab after (dir 1) or before (dir -1) this one, round and round: Ctrl+Tab, Ctrl+Shift+Tab, the arrows on a tab.</summary>
        internal static FlyoutTab NextTab(FlyoutTab t, int dir)
        {
            int n = FlyoutTabs.Length, at = Array.IndexOf(FlyoutTabs, t);
            return FlyoutTabs[((at + Math.Sign(dir)) % n + n) % n];
        }

        /// <summary>
        /// The tab the flyout opens on: Live while a game runs or something is held (paused, the countdown back to SDR,
        /// a recorder keeping HDR off) – what is switched right now is what matters then. Otherwise the tab last used
        /// while the app has been running, so going back to the games or the settings is one click.
        /// </summary>
        internal static FlyoutTab OpeningTab(bool gameRunning, bool held, FlyoutTab last)
        {
            return gameRunning || held ? FlyoutTab.Live : last;
        }

        /// <summary>What the Live tab is made from: the app's state at one moment, read by the flyout (Flyout.Snapshot).</summary>
        internal sealed class LiveState
        {
            public bool Paused;
            public bool HdrSupported = true;   // some display can do HDR
            public bool HdrOurs;               // this app has HDR on (it owns a display)
            public bool HdrElsewhere;          // HDR is on, but not by this app
            public string Game;                // the running game's name as shown, or null
            public bool GameHdr, GameBrightness, GameArea, GameStretch;   // what that game has set
            public string Screen;              // the monitor's name, when known
            public int Delay = 3;              // the wait after a game closes, in seconds
            public int GraceLeft = -1;         // seconds left of that wait while it counts down, else -1
            public string BackTo;              // what comes back after it: "SDR", or the desktop's resolution
            public int Brightness = -1;        // the SDR brightness this app set (slider 0…100), or -1
            public bool BrightnessLeft;        // the user moved the slider during the game: left as they set it
            public Resolution.Mode Area;       // the competitive area this app set, or null
            public Resolution.Mode Stretch;    // the game's own resolution stretched over it, or null
            public bool StretchLost;           // the game has set its own mode since, so it isn't stretched now
            public string AreaDetail;          // "92.5 % · 24.6″": how much of the panel the area covers
            public bool ScalingTaken;          // this app set NVIDIA's No scaling for the area, to put back after
            public bool ScalingStray;          // a No scaling it set is still to be put back
            public bool RecordingOn;           // "SDR while recording" is on
            public string Recorder;            // the recording app running while it is on, as shown, or null
        }

        /// <summary>One row of the Live tab's layer stack.</summary>
        internal sealed class LiveLayer
        {
            public string Key;     // hdr, bright, area, stretch, scaling
            public string Title, Desc, Value;
            public bool Active;    // in effect now: a lit icon and value (HDR: the amber ON marker)
        }

        internal const string NoValue = "—";   // a layer with nothing to show: no game, nothing in effect

        /// <summary>
        /// The layer stack, top to bottom in the order they apply: HDR, SDR brightness, competitive area, stretch, NVIDIA
        /// scaling. A layer in effect shows its value; one that isn't shows "Off" (or Windows' own) while a game runs,
        /// and a dash when there is no game for it to apply to.
        /// </summary>
        internal static List<LiveLayer> LiveLayers(LiveState s)
        {
            bool game = s.Game != null;
            var list = new List<LiveLayer>();

            var hdr = new LiveLayer { Key = "hdr", Title = "HDR", Active = s.HdrOurs };
            hdr.Value = s.HdrOurs ? "ON" : s.HdrElsewhere ? "On" : game ? "Off" : NoValue;
            hdr.Desc = s.HdrOurs ? "Turned on by Display Switch"
                : s.HdrElsewhere ? "Turned on outside Display Switch"
                : s.Paused ? "Paused"
                : !s.HdrSupported ? "No HDR display"
                : game && s.GameHdr && s.Recorder != null ? "Held off: " + s.Recorder + " is running"
                : game && !s.GameHdr ? "Not on for this game"
                : game ? "Not switched"
                : "On while a game from the list runs";
            list.Add(hdr);

            var bright = new LiveLayer { Key = "bright", Title = "SDR brightness", Active = s.Brightness >= 0 };
            bright.Value = bright.Active ? s.Brightness + " of 100" : game ? "Windows'" : NoValue;
            bright.Desc = bright.Active ? "Desktop and SDR menus"
                : s.Paused ? "Paused"
                : s.BrightnessLeft ? "Your level, left as you set it"
                : game && s.GameBrightness ? "Only while HDR is on"
                : game ? "Windows' own level"
                : "Set per game, while HDR is on";
            list.Add(bright);

            var area = new LiveLayer { Key = "area", Title = "Competitive area", Active = s.Area != null };
            area.Value = area.Active ? s.Area.Text : game ? "Off" : NoValue;
            area.Desc = area.Active ? "Centred" + (string.IsNullOrEmpty(s.AreaDetail) ? "" : ", " + s.AreaDetail)
                : s.Paused ? "Paused"
                : game && s.GameArea ? "Not switched"
                : game ? "Not set for this game"
                : "Set per game: a smaller, centred picture";
            list.Add(area);

            var stretch = new LiveLayer { Key = "stretch", Title = "Stretch", Active = s.Stretch != null };
            stretch.Value = stretch.Active ? s.Stretch.Text : game ? "Off" : NoValue;
            stretch.Desc = stretch.Active ? (s.StretchLost ? "The game set its own mode since" : "Stretched over " + (s.Area != null ? s.Area.Text : "the area"))
                : s.Paused ? "Paused"
                : game && s.GameStretch ? "Not switched"
                : game ? "Not set for this game"
                : "Set per game, over its area";
            list.Add(stretch);

            var scaling = new LiveLayer { Key = "scaling", Title = "NVIDIA scaling", Active = s.ScalingTaken || s.ScalingStray };
            scaling.Value = scaling.Active ? "No scaling" : game || s.Area != null ? "Unchanged" : NoValue;
            scaling.Desc = s.ScalingTaken ? "Set automatically, put back after"
                : s.ScalingStray ? "Still to be put back"
                : s.Stretch != null ? "Not needed for a stretch"
                : s.Paused ? "Paused"
                : s.Area != null ? "Left alone: it already centres"
                : "Set with a competitive area";
            list.Add(scaling);
            return list;
        }

        /// <summary>
        /// The Live tab's title and the line under it. Title: the running game, or Paused, Switching back (the wait after
        /// it closed), No HDR display or Waiting for a game. The line: the monitor when known, then when things go back.
        /// </summary>
        internal static void LiveStatus(LiveState s, out string title, out string sub)
        {
            if (s.Paused) { title = "Paused"; sub = "Nothing switches · " + HotKey.Text + " resumes"; return; }
            bool counting = s.GraceLeft >= 0;
            title = s.Game ?? (counting ? "Switching back" : !s.HdrSupported ? "No HDR display" : "Waiting for a game");
            var parts = new List<string>();
            if (s.Screen != null && (s.Game != null || counting)) parts.Add(s.Screen);
            string to = s.BackTo ?? "SDR";
            if (counting) parts.Add("back to " + to + " in " + Math.Max(1, s.GraceLeft) + "s");
            else if (s.Game != null && (s.HdrOurs || s.Area != null)) parts.Add("back to " + to + " " + s.Delay + " s after it closes");
            else if (s.Game != null && s.Recorder != null && s.GameHdr) parts.Add("HDR held off while recording");
            else if (s.Game != null) parts.Add("Nothing switched");
            else if (!s.HdrSupported) parts.Add("Check Windows display settings");
            else parts.Add(s.HdrElsewhere ? "HDR is on, turned on outside Display Switch" : "HDR off");
            sub = string.Join(" · ", parts);
        }

        /// <summary>
        /// How tall the scrolling body of the Live and Settings tabs is: all of its content when it fits in the working
        /// area with the rest of the panel (fixedH) and the margin above and below, else what fits (it then scrolls),
        /// but never less than one card row (minH). In pixels, at the monitor's scale.
        /// </summary>
        internal static int FlyoutBody(int workAreaH, int margin, int fixedH, int contentH, int minH)
        {
            int fit = workAreaH - 2 * margin - fixedH;
            return Math.Max(Math.Min(contentH, minH), Math.Min(contentH, fit));
        }

        /// <summary>
        /// The Games tab's height without its list rows, in pixels at scale k: the tab bar (or a view's back header),
        /// the gap above the list, the search box above the library's list (search), below it a view's note (subView)
        /// or a small gap, and the footer bar.
        /// </summary>
        internal static int GamesFixed(float k, bool subView, bool search)
        {
            Func<double, int> S = v => (int)Math.Round(v * k);
            return S(48) + S(4) + (search ? S(40) : 0) + (subView ? S(52) : S(4)) + S(8) + S(48);
        }

        // ---- Games: the library and a game's page ----

        /// <summary>One game as the Games tab shows it: what it is and what it changes.</summary>
        internal sealed class GameInfo
        {
            public string Exe;                 // the list's key: "cs2" (shown as cs2.exe)
            public string Name;                // as shown: "Counter-Strike 2"
            public string Launcher;            // "Steam", "Riot", "Added by hand", or null
            public bool Hdr;                   // HDR on while it runs (games.txt); off: never
            public bool Running;
            public int Brightness = -1;        // its SDR brightness, 0…100, or -1: Windows' own
            public Resolution.Mode Area;       // its competitive area, or null
            public Resolution.Mode Stretch;    // its own resolution stretched over the area (one that fits), or null
            public bool Nvidia = true;         // an NVIDIA driver runs the display (NVIDIA scaling can be set)
            public string AreaDetail;          // "92.5 % · 24.5″": how much of the panel the area covers
        }

        /// <summary>
        /// The line under a game in the library, what it changes: "HDR · SDR 40 · 2368 × 1332", "HDR · 2368 × 1332 ·
        /// stretched 4:3", "Stays in SDR", "HDR". The SDR brightness only counts with HDR on, so a game that stays in
        /// SDR doesn't list it.
        /// </summary>
        internal static string GameRecipe(GameInfo g)
        {
            var parts = new List<string> { g.Hdr ? "HDR" : "Stays in SDR" };
            if (g.Hdr && g.Brightness >= 0) parts.Add("SDR " + g.Brightness);
            if (g.Area != null) parts.Add(g.Area.Text);
            if (g.Area != null && g.Stretch != null) parts.Add("stretched " + AspectText(g.Stretch));
            return string.Join(" · ", parts.ToArray());
        }

        /// <summary>
        /// The search: every word typed is in the game's name or its exe name, ignoring case ("counter 2", "cs2.exe").
        /// Nothing typed: every game.
        /// </summary>
        internal static bool GameMatches(string query, string name, string exe)
        {
            if (query == null) return true;
            foreach (string w in query.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                if ((name ?? "").IndexOf(w, StringComparison.OrdinalIgnoreCase) < 0 && ((exe ?? "") + ".exe").IndexOf(w, StringComparison.OrdinalIgnoreCase) < 0)
                    return false;
            return true;
        }

        /// <summary>"Steam" from "Steam: Counter-Strike 2", "Added by hand", or null: the label names no launcher.</summary>
        internal static string LabelLauncher(string label)
        {
            if (string.IsNullOrEmpty(label)) return null;
            if (label == "added by hand") return "Added by hand";
            int colon = label.IndexOf(": ");
            return colon > 0 && colon < 10 ? label.Substring(0, colon) : null;
        }

        /// <summary>The library's footer: "32 with HDR · 38 games"; while searching, "3 of 38 games".</summary>
        internal static string GamesSummary(int withHdr, int total, int shown, bool searching)
        {
            string games = total == 1 ? " game" : " games";
            return searching ? shown + " of " + total + games : withHdr + " with HDR · " + total + games;
        }

        /// <summary>One card of a game's page: a layer, in Live's order.</summary>
        internal sealed class GameCard
        {
            public string Key;       // hdr, bright, area, stretch, scaling
            public string Title, Desc, Value;
            public bool On;          // set for this game: a lit icon (HDR: amber)
            public bool Enabled;     // it can be changed now (stretch needs an area; scaling is only information)
        }

        /// <summary>
        /// A game's page, top to bottom in the order the layers apply (Live's): HDR (on or never), SDR brightness (its
        /// own level or Windows'), competitive area, stretch (only over an area) and NVIDIA scaling, which the app sets
        /// by itself and the page only explains.
        /// </summary>
        internal static List<GameCard> GameCards(GameInfo g)
        {
            var list = new List<GameCard>();
            list.Add(new GameCard { Key = "hdr", Title = "HDR", On = g.Hdr, Enabled = true, Value = g.Hdr ? "On" : "Never",
                Desc = g.Hdr ? "On while it runs" : "Stays in SDR, never switched" });
            bool own = g.Brightness >= 0;
            list.Add(new GameCard { Key = "bright", Title = "SDR brightness", On = own, Enabled = true, Value = BrightnessText(g.Brightness),
                Desc = BrightnessDesc(g.Brightness, g.Hdr) });
            bool area = g.Area != null;
            list.Add(new GameCard { Key = "area", Title = "Competitive area", On = area, Enabled = true, Value = area ? g.Area.Text : "Off",
                Desc = area ? "Centred" + (string.IsNullOrEmpty(g.AreaDetail) ? "" : ", " + g.AreaDetail) : "A smaller, centred picture" });
            bool stretch = area && g.Stretch != null;
            list.Add(new GameCard { Key = "stretch", Title = "Stretch", On = stretch, Enabled = area,
                Value = stretch ? g.Stretch.Text + " · " + AspectText(g.Stretch) : "Off",
                Desc = !area ? "Needs a competitive area" : stretch ? "Borderless games only" : "A lower resolution over the area" });
            bool scaling = g.Nvidia && area && !stretch;
            list.Add(new GameCard { Key = "scaling", Title = "NVIDIA scaling", On = scaling, Enabled = false,
                Value = !g.Nvidia ? "Not available" : scaling ? "No scaling" : "Not needed",
                Desc = !g.Nvidia ? "No NVIDIA graphics on this display" : scaling ? "Set to No scaling while it runs" : area ? "Not needed: the stretch fills the area" : "Not needed without an area" });
            return list;
        }

        /// <summary>What the SDR brightness card says under its title, for a level (-1: Windows' own) and whether the game has HDR.</summary>
        internal static string BrightnessDesc(int level, bool hdr)
        {
            return level < 0 ? "Windows' own until you change it" : hdr ? "Desktop and SDR menus, while it runs" : "Only if HDR is on while it runs";
        }

        /// <summary>The SDR brightness card's value: "Windows'" for Windows' own level (-1), else the level.</summary>
        internal static string BrightnessText(int level) { return level < 0 ? "Windows'" : level.ToString(); }

        /// <summary>How bright SDR white is at a slider level, as Windows' own slider maps it: 80 nits at 0, 480 at 100.</summary>
        internal static int BrightnessNits(int level) { return 80 + 4 * Math.Max(0, Math.Min(100, level)); }

        /// <summary>Windows' level, where it can't be read: the middle of the slider.</summary>
        internal const int UnknownWindowsLevel = 50;

        /// <summary>
        /// Where the slider's thumb sits, 0 → 1: at the game's own level, or – for Windows' own (-1) – at the level
        /// Windows has now (windowsNow, -1 when it can't be read: the middle).
        /// </summary>
        internal static double SliderFraction(int level, int windowsNow)
        {
            int at = level >= 0 ? level : windowsNow >= 0 ? windowsNow : UnknownWindowsLevel;
            return Math.Max(0, Math.Min(100, at)) / 100.0;
        }

        /// <summary>The level at a point of the slider's track (x from its left end, w its length): 0…100, rounded.</summary>
        internal static int SliderLevel(double x, double w)
        {
            if (w <= 0) return 0;
            return (int)Math.Round(Math.Max(0, Math.Min(1, x / w)) * 100);
        }

        /// <summary>
        /// The level after a key on the slider: delta 1 or -1 (arrows), 10 (Page Up / Down), 100 (Home / End). From
        /// Windows' own it starts where the thumb shows it, at Windows' level now. Never below 0: Windows' own is the
        /// check box under the slider.
        /// </summary>
        internal static int StepBrightness(int level, int windowsNow, int delta)
        {
            int from = level >= 0 ? level : windowsNow >= 0 ? windowsNow : UnknownWindowsLevel;
            return Math.Max(0, Math.Min(100, from + delta));
        }

        /// <summary>A competitive area drawn to scale: where it and the stretched resolution sit on the screen, as fractions of it.</summary>
        internal struct AreaPicture
        {
            public RectangleF Area;      // the area, 1:1 in the middle of the screen
            public RectangleF Stretch;   // the game's own resolution at its own size (1:1), in the middle; empty when not stretched
        }

        /// <summary>
        /// The page's picture of the screen: the area shown 1:1 in the middle (2368 × 1332 on 2560 × 1440: 92.5 % each
        /// way), and inside it the game's own resolution at its own size, which the GPU stretches over the area.
        /// </summary>
        internal static AreaPicture AreaPreview(Resolution.Mode full, Resolution.Mode area, Resolution.Mode stretch)
        {
            var p = new AreaPicture();
            if (full == null || full.Width <= 0 || full.Height <= 0 || area == null) return p;
            Func<Resolution.Mode, RectangleF> centred = m =>
            {
                float w = Math.Min(1f, (float)m.Width / full.Width), h = Math.Min(1f, (float)m.Height / full.Height);
                return new RectangleF((1 - w) / 2, (1 - h) / 2, w, h);
            };
            p.Area = centred(area);
            if (stretch != null) p.Stretch = centred(stretch);
            return p;
        }

        /// <summary>The picture's label: "2368 × 1332 · 24.5″" (the area's diagonal), or "· 92.5 %" when the panel's size is unknown.</summary>
        internal static string AreaLabel(Resolution.Mode area, Resolution.Mode full, double inches)
        {
            if (area == null) return "";
            if (full == null || full.Width == 0) return area.Text;
            double ratio = Diagonal(area) / Diagonal(full);
            return area.Text + " · " + (inches > 0 ? (inches * ratio).ToString("0.0") + "″" : (ratio * 100).ToString("0.0") + " %");
        }

        static double Diagonal(Resolution.Mode m) { return Math.Sqrt((double)m.Width * m.Width + (double)m.Height * m.Height); }

        /// <summary>
        /// The setup hint a game's page shows: the latest notification when it is about this game (name), else what has
        /// to be created first – the area or the stretched resolution, when the display doesn't offer it at the rate
        /// (hz) it is switched at. Before both, problem: the latest notice about this game's own switch (a missing
        /// resolution, a fallback, a refusal), which stays until the game is next switched. Null: nothing to set up.
        /// </summary>
        internal static string SetupHint(GameInfo g, string notice, bool areaExists, bool stretchExists, int hz, string problem = null)
        {
            if (!string.IsNullOrEmpty(problem)) return problem;
            if (notice != null && g.Name != null && notice.IndexOf(g.Name, StringComparison.Ordinal) >= 0) return notice;
            Resolution.Mode missing = g.Area != null && !areaExists ? g.Area : g.Area != null && g.Stretch != null && !stretchExists ? g.Stretch : null;
            if (missing == null) return null;
            return g.Nvidia
                ? "Create " + missing.Text + " at " + hz + " Hz before it can switch: Create… under Competitive area, or NVIDIA Control Panel (Change resolution › Customize)."
                : "The display has no " + missing.Text + " at " + hz + " Hz. Create it as a custom resolution in the graphics driver first.";
        }

        /// <summary>"92.5 % · 24.6″": how much of the panel a mode covers when shown 1:1 in the middle.</summary>
        internal static string SizeText(Resolution.Mode m, Resolution.Mode full, double inches)
        {
            if (full == null || full.Width == 0) return "";
            double ratio = Diagonal(m) / Diagonal(full);
            string s = (ratio * 100).ToString("0.0") + " %";
            return inches > 0 ? s + " · " + (inches * ratio).ToString("0.0") + "″" : s;
        }

        /// <summary>"4:3", "16:10", "16:9" … or "1.78:1" for a shape without a short name.</summary>
        /// <summary>A row of a page's size list: the size, at the rate it is switched at – or, with Only set, a size the display has at other rates only.</summary>
        internal sealed class SizeRow
        {
            public Resolution.Mode Mode;   // Hz: the rate the display has it at (the wanted one, or one Hz off it); 0 with Only
            public string Only;            // "only at 60 Hz", "only at 60, 120 Hz" – null: it can be picked
        }

        /// <summary>
        /// The sizes of a game's list, from every mode the display offers (each size at each of its rates), for the
        /// rate hz the game is switched at. One row per size, and a size counts as there at hz if the display has it at
        /// hz or one Hz above or below it – Windows reports the same rate as 239 on one size and 240 on another
        /// (Fallback.Near, which is how the switch looks for it too). Sizes the display has at other rates only come
        /// last, each saying at which, and can't be picked.
        ///  - The area's list (area null): every size smaller than the panel (full) and not beyond it either way, the
        ///    panel's own shape first, then the largest first.
        ///  - The stretch's list (area set): the sizes that fit inside the area, the largest first.
        /// </summary>
        internal static List<SizeRow> SizeRows(IEnumerable<Resolution.Mode> all, int hz, Resolution.Mode full, Resolution.Mode area)
        {
            var rows = new List<SizeRow>();
            if (all == null || (area == null && full == null)) return rows;
            var sizes = new List<Resolution.Mode>();
            var rates = new Dictionary<string, SortedSet<int>>();
            foreach (var m in all)
            {
                if (m == null || m.Width <= 0 || m.Height <= 0) continue;
                if (area != null ? !Fits(m, area) : !(m.Width <= full.Width && m.Height <= full.Height && (m.Width < full.Width || m.Height < full.Height))) continue;
                string key = m.Width + "x" + m.Height;
                SortedSet<int> of;
                if (!rates.TryGetValue(key, out of))
                {
                    rates[key] = of = new SortedSet<int>();
                    sizes.Add(m);
                }
                if (m.Hz > 0) of.Add(m.Hz);
            }
            sizes.Sort((a, b) =>
            {
                int byShape = area != null ? 0 : (SameAspect(a, full) ? 0 : 1).CompareTo(SameAspect(b, full) ? 0 : 1);
                if (byShape != 0) return byShape;
                int byPixels = ((long)b.Width * b.Height).CompareTo((long)a.Width * a.Height);
                return byPixels != 0 ? byPixels : b.Width.CompareTo(a.Width);
            });
            var other = new List<SizeRow>();
            foreach (var m in sizes)
            {
                var of = rates[m.Width + "x" + m.Height];
                int near = Fallback.Near(hz, of);
                if (near > 0) rows.Add(new SizeRow { Mode = new Resolution.Mode { Width = m.Width, Height = m.Height, Hz = near } });
                else if (of.Count > 0)
                    other.Add(new SizeRow { Mode = new Resolution.Mode { Width = m.Width, Height = m.Height },
                        Only = "only at " + string.Join(", ", of.Select(r => r.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray()) + " Hz" });
            }
            rows.AddRange(other);
            return rows;
        }

        internal static string AspectText(Resolution.Mode m)
        {
            int a = m.Width, b = m.Height;
            while (b != 0) { int t = a % b; a = b; b = t; }
            if (a == 0) return "";
            int w = m.Width / a, h = m.Height / a;
            if (w == 8 && h == 5) return "16:10";
            return w <= 21 && h <= 10 ? w + ":" + h : ((double)m.Width / m.Height).ToString("0.00") + ":1";
        }

        internal static bool SameAspect(Resolution.Mode m, Resolution.Mode full)
        {
            return Math.Abs((long)m.Width * full.Height - (long)m.Height * full.Width) * 100 < (long)full.Width * full.Height;
        }
    }
}
