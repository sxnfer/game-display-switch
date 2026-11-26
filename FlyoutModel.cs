// The flyout's decisions about what it says, kept apart from the window so the checks can run them: the three tabs and
// which one it opens on, and the Live tab – the status line and the stack of five layers (HDR, SDR brightness,
// competitive area, stretch, NVIDIA scaling) worked out from a snapshot of the app's state.

using System;
using System.Collections.Generic;

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
                : s.Stretch != null ? "Left alone: a stretched game doesn't need it"
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
        /// the gap above the list, below it a view's note (subView) or a small gap, and the footer bar.
        /// </summary>
        internal static int GamesFixed(float k, bool subView)
        {
            Func<double, int> S = v => (int)Math.Round(v * k);
            return S(48) + S(4) + (subView ? S(52) : S(4)) + S(8) + S(48);
        }
    }
}
