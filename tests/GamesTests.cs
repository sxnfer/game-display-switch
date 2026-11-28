// Checks for the Games tab's decisions that don't need a window: the line under each game in the library, the search,
// a game's page (its five cards, which of them can be changed, the setup hint), the to-scale picture of a competitive
// area and the SDR brightness slider's levels.

using System;
using System.Collections.Generic;
using System.Drawing;

namespace HdrSwitch.Tests
{
    static partial class Checks
    {
        static Resolution.Mode M(int w, int h) { return new Resolution.Mode { Width = w, Height = h }; }

        static TrayApp.GameInfo Game(bool hdr, int bright = -1, Resolution.Mode area = null, Resolution.Mode stretch = null)
        {
            return new TrayApp.GameInfo { Exe = "valorant", Name = "VALORANT", Hdr = hdr, Brightness = bright, Area = area, Stretch = stretch };
        }

        // ---- the library ----

        public static void GamesRecipe()
        {
            Run.Equal("HDR", TrayApp.GameRecipe(Game(true)), "HDR alone");
            Run.Equal("Stays in SDR", TrayApp.GameRecipe(Game(false)), "never");
            Run.Equal("HDR · SDR 40", TrayApp.GameRecipe(Game(true, 40)), "HDR and its SDR brightness");
            Run.Equal("HDR · SDR 0", TrayApp.GameRecipe(Game(true, 0)), "level 0 is a level, not Windows'");
            Run.Equal("HDR · SDR 40 · 2368 × 1332", TrayApp.GameRecipe(Game(true, 40, M(2368, 1332))), "HDR, brightness and an area");
            Run.Equal("HDR · 2368 × 1332 · stretched 4:3", TrayApp.GameRecipe(Game(true, -1, M(2368, 1332), M(1776, 1332))), "an area with a 4:3 stretch");
            Run.Equal("HDR · SDR 60 · 2368 × 1332 · stretched 16:10", TrayApp.GameRecipe(Game(true, 60, M(2368, 1332), M(1680, 1050))), "everything at once");
            Run.Equal("Stays in SDR · 1920 × 1080", TrayApp.GameRecipe(Game(false, -1, M(1920, 1080))), "never, with an area");
            Run.Equal("Stays in SDR", TrayApp.GameRecipe(Game(false, 40)), "never: its brightness doesn't count");
            Run.Equal("HDR", TrayApp.GameRecipe(Game(true, -1, null, M(1776, 1332))), "a stretch without an area: nothing to stretch over");
        }

        // ---- a page's size list ----

        static string Rows(List<TrayApp.SizeRow> rows)
        {
            var parts = new List<string>();
            foreach (var r in rows) parts.Add(r.Mode.Width + "x" + r.Mode.Height + (r.Only != null ? " (" + r.Only + ")" : "@" + r.Mode.Hz));
            return string.Join(" | ", parts.ToArray());
        }

        public static void GamesSizeListArea()
        {
            var full = M(2560, 1440, 240);
            var all = new List<Resolution.Mode> { M(2560, 1440, 240), M(2560, 1440, 60), M(2368, 1332, 240), M(2368, 1332, 60), M(1920, 1440, 240), M(1920, 1080, 240), M(1280, 960, 240) };
            Run.Equal("2368x1332@240 | 1920x1080@240 | 1920x1440@240 | 1280x960@240", Rows(TrayApp.SizeRows(all, 240, full, null)),
                "at the exact rate: one row per size, not the panel's own, its shape first, then the largest first");

            // The same rate as Windows rounds it for another size: 239 next to 240 is one row, and 239 alone counts.
            all = new List<Resolution.Mode> { M(2368, 1332, 239), M(2368, 1332, 240), M(2072, 1296, 239), M(1920, 1080, 241), M(1600, 900, 238), M(1600, 900, 242) };
            var rows = TrayApp.SizeRows(all, 240, full, null);
            Run.Equal("2368x1332@240 | 1920x1080@241 | 2072x1296@239 | 1600x900 (only at 238, 242 Hz)", Rows(rows), "one Hz off counts, two don't; the exact rate wins");
            Run.Equal(1, rows.FindAll(r => r.Mode.Width == 2368).Count, "239 and 240 never make two rows");
            Run.Equal("2368x1332@239 | 2072x1296@239", Rows(TrayApp.SizeRows(new List<Resolution.Mode> { M(2368, 1332, 239), M(2368, 1332, 240), M(2072, 1296, 239) }, 239, full, null)), "asked at 239: the same");
            Run.Equal("2368x1332@144 | 2072x1296@143", Rows(TrayApp.SizeRows(new List<Resolution.Mode> { M(2368, 1332, 144), M(2368, 1332, 143), M(2072, 1296, 143) }, 144, full, null)), "at 144");

            // Sizes the display has at other rates only: at the end, in the same order, saying at which, not to be picked.
            all = new List<Resolution.Mode> { M(1024, 768, 60), M(1280, 720, 120), M(1280, 720, 60), M(2368, 1332, 240), M(1920, 1080, 60), M(1920, 1080, 240), M(800, 600, 60), M(800, 600, 75), M(800, 600, 120) };
            rows = TrayApp.SizeRows(all, 240, full, null);
            Run.Equal("2368x1332@240 | 1920x1080@240 | 1280x720 (only at 60, 120 Hz) | 1024x768 (only at 60 Hz) | 800x600 (only at 60, 75, 120 Hz)", Rows(rows), "only at other rates: last");
            Run.True(rows[0].Only == null && rows[1].Only == null && rows[2].Only != null && rows[2].Mode.Hz == 0, "which rows can be picked");
            Run.Equal("1920x1080@60 | 1280x720@60 | 1024x768@60 | 800x600@60 | 2368x1332 (only at 240 Hz)", Rows(TrayApp.SizeRows(all, 60, full, null)), "the same display at 60 Hz");

            // Nothing beyond the panel either way, and not the panel itself, at any rate.
            all = new List<Resolution.Mode> { M(3840, 2160, 240), M(3840, 1080, 240), M(1920, 1600, 240), M(2560, 1440, 240), M(2560, 1440, 60), M(2560, 1080, 240), M(3840, 2160, 60) };
            Run.Equal("2560x1080@240", Rows(TrayApp.SizeRows(all, 240, full, null)), "larger than native in width or height: left out, also from the greyed rows");

            Run.Equal("", Rows(TrayApp.SizeRows(null, 240, full, null)), "no modes");
            Run.Equal("", Rows(TrayApp.SizeRows(all, 240, null, null)), "no panel size");
            Run.Equal("2560x1080 (only at 240 Hz)", Rows(TrayApp.SizeRows(all, 0, full, null)), "no rate: nothing can be picked");
        }

        public static void GamesSizeListStretch()
        {
            var full = M(2560, 1440, 240);
            var area = M(2368, 1332);
            var all = new List<Resolution.Mode> { M(2560, 1440, 240), M(2368, 1332, 240), M(1920, 1440, 240), M(1920, 1080, 240), M(1776, 1332, 240), M(2368, 1080, 240), M(1280, 960, 239), M(1280, 960, 240), M(1440, 1080, 239), M(1024, 768, 60) };
            Run.Equal("2368x1080@240 | 1776x1332@240 | 1920x1080@240 | 1440x1080@239 | 1280x960@240 | 1024x768 (only at 60 Hz)", Rows(TrayApp.SizeRows(all, 240, full, area)),
                "the sizes inside the area – not the area itself, nothing taller or wider – the largest first, whatever their shape");
            Run.Equal("", Rows(TrayApp.SizeRows(new List<Resolution.Mode> { M(2368, 1332, 240), M(2560, 1440, 240) }, 240, full, area)), "nothing fits inside");
            Run.Equal("1024x768@60 | 2368x1080 (only at 240 Hz) | 1776x1332 (only at 240 Hz) | 1920x1080 (only at 240 Hz) | 1440x1080 (only at 239 Hz) | 1280x960 (only at 239, 240 Hz)",
                Rows(TrayApp.SizeRows(all, 60, null, area)), "at 60 Hz, and without the panel's size");
        }

        public static void GamesSearch()
        {
            Run.True(TrayApp.GameMatches("", "Counter-Strike 2", "cs2"), "nothing typed: every game");
            Run.True(TrayApp.GameMatches("   ", "Counter-Strike 2", "cs2"), "spaces only: every game");
            Run.True(TrayApp.GameMatches("counter", "Counter-Strike 2", "cs2"), "by name, any case");
            Run.True(TrayApp.GameMatches("STRIKE", "Counter-Strike 2", "cs2"), "upper case");
            Run.True(TrayApp.GameMatches("cs2", "Counter-Strike 2", "cs2"), "by exe name");
            Run.True(TrayApp.GameMatches("cs2.exe", "Counter-Strike 2", "cs2"), "with .exe");
            Run.True(TrayApp.GameMatches("counter 2", "Counter-Strike 2", "cs2"), "every word, in any place");
            Run.True(TrayApp.GameMatches("strike cs2", "Counter-Strike 2", "cs2"), "a word of the name and one of the exe");
            Run.True(!TrayApp.GameMatches("counter 3", "Counter-Strike 2", "cs2"), "a word that isn't there: no");
            Run.True(!TrayApp.GameMatches("valorant", "Counter-Strike 2", "cs2"), "another game: no");
            Run.True(TrayApp.GameMatches("r5a", "Apex Legends", "r5apex"), "the exe of a game whose name differs");
            var names = new List<string> { "Apex Legends", "Counter-Strike 2", "Stardew Valley", "Hades II" };
            var hit = names.FindAll(n => TrayApp.GameMatches("st", n, ""));
            Run.Equal("Counter-Strike 2,Stardew Valley", string.Join(",", hit.ToArray()), "the list filtered as the box is typed into");
        }

        public static void GamesSummaryAndLauncher()
        {
            Run.Equal("32 with HDR · 38 games", TrayApp.GamesSummary(32, 38, 38, false), "the footer");
            Run.Equal("3 of 38 games", TrayApp.GamesSummary(32, 38, 3, true), "while searching");
            Run.Equal("1 with HDR · 1 game", TrayApp.GamesSummary(1, 1, 1, false), "one game");
            Run.Equal("Steam", TrayApp.LabelLauncher("Steam: Counter-Strike 2"), "the launcher from the label");
            Run.Equal("Added by hand", TrayApp.LabelLauncher("added by hand"), "added by hand");
            Run.Equal(null, TrayApp.LabelLauncher("cs2"), "a label without a launcher");
            Run.Equal(null, TrayApp.LabelLauncher(null), "no label");
        }

        // ---- a game's page ----

        static string Cards(TrayApp.GameInfo g)
        {
            var parts = new List<string>();
            foreach (var c in TrayApp.GameCards(g)) parts.Add(c.Key + "=" + c.Value + (c.On ? "*" : "") + (c.Enabled ? "" : "!"));
            return string.Join(" ", parts.ToArray());
        }

        static TrayApp.GameCard Card(TrayApp.GameInfo g, string key) { return TrayApp.GameCards(g).Find(c => c.Key == key); }

        public static void GamePageCards()
        {
            // value, * set for this game, ! can't be changed (stretch without an area; scaling is only information)
            Run.Equal("hdr=Never bright=Windows' area=Off stretch=Off! scaling=Not needed!", Cards(Game(false)), "nothing set");
            Run.Equal("HDR,SDR brightness,Competitive area,Stretch,NVIDIA scaling", string.Join(",", TrayApp.GameCards(Game(true)).ConvertAll(c => c.Title).ToArray()), "Live's order");
            Run.Equal("Needs a competitive area", Card(Game(true), "stretch").Desc, "stretch gated by the area: why");
            var area = Game(true, 40, M(2368, 1332));
            area.AreaDetail = "92.5 % · 24.5″";
            Run.Equal("hdr=On* bright=40* area=2368 × 1332* stretch=Off scaling=No scaling*!", Cards(area), "an area: stretch can be set, NVIDIA set to No scaling");
            Run.Equal("Centred, 92.5 % · 24.5″", Card(area, "area").Desc, "how much of the panel");
            Run.Equal("Set to No scaling while it runs", Card(area, "scaling").Desc, "what NVIDIA scaling does");
            var stretched = Game(true, -1, M(2368, 1332), M(1776, 1332));
            Run.Equal("hdr=On* bright=Windows' area=2368 × 1332* stretch=1776 × 1332 · 4:3* scaling=Not needed!", Cards(stretched), "a stretch: scaling not needed");
            Run.Equal("Not needed: the stretch fills the area", Card(stretched, "scaling").Desc, "… and why");
            var amd = Game(true, -1, M(2368, 1332));
            amd.Nvidia = false;
            Run.Equal("Not available", Card(amd, "scaling").Value, "no NVIDIA: not available");
            Run.True(Card(Game(true), "hdr").Enabled && Card(Game(true), "bright").Enabled && Card(Game(true), "area").Enabled, "HDR, brightness and area can always be changed");
            Run.True(!Card(Game(false, -1, M(2368, 1332)), "scaling").Enabled, "scaling never is a control");
            Run.Equal("Only if HDR is on while it runs", Card(Game(false, 40), "bright").Desc, "a level for a game without HDR: when it counts");
        }

        public static void GamePageSetupHint()
        {
            var g = Game(true, -1, M(2304, 1296));
            Run.Equal(null, TrayApp.SetupHint(g, null, true, true, 240), "the display has the area: nothing to set up");
            Run.Equal("Create 2304 × 1296 at 240 Hz before it can switch: Create… under Competitive area, or NVIDIA Control Panel (Change resolution › Customize).",
                TrayApp.SetupHint(g, null, false, true, 240), "the area is missing: create it");
            var s = Game(true, -1, M(2368, 1332), M(1776, 1332));
            Run.True(TrayApp.SetupHint(s, null, true, false, 240).StartsWith("Create 1776 × 1332 at 240 Hz"), "the stretched resolution is missing: create that");
            g.Nvidia = false;
            Run.True(TrayApp.SetupHint(g, null, false, true, 144).Contains("graphics driver"), "no NVIDIA: the driver's own tool");
            string n = "VALORANT set its own display mode, so 1776 × 1332 is no longer stretched.";
            Run.Equal(n, TrayApp.SetupHint(s, n, true, true, 240), "the latest notification about this game");
            Run.Equal(null, TrayApp.SetupHint(s, "Counter-Strike 2 started.", true, true, 240), "one about another game: not here");
            Run.Equal(null, TrayApp.SetupHint(Game(true), null, false, false, 240), "no area: nothing to check");
        }

        // ---- the competitive area's picture ----

        public static void GamePageAreaPreview()
        {
            var full = M(2560, 1440);
            var p = TrayApp.AreaPreview(full, M(2368, 1332), null);
            Run.Near(0.925, p.Area.Width, 1e-6, "2368 × 1332 on 2560 × 1440: 92.5 % wide");
            Run.Near(0.925, p.Area.Height, 1e-6, "… and 92.5 % high");
            Run.Near(0.0375, p.Area.X, 1e-6, "centred: 3.75 % from the left");
            Run.Near(0.0375, p.Area.Y, 1e-6, "… and from the top");
            Run.True(p.Stretch.IsEmpty, "not stretched: no stretch rectangle");
            p = TrayApp.AreaPreview(full, M(2368, 1332), M(1776, 1332));
            Run.Near(0.69375, p.Stretch.Width, 1e-6, "1776 × 1332 inside it: 69.4 % wide");
            Run.Near(0.925, p.Stretch.Height, 1e-6, "… as high as the area");
            Run.Near(0.153125, p.Stretch.X, 1e-6, "… centred");
            Run.True(p.Area.Contains(p.Stretch), "the stretch lies inside the area");
            p = TrayApp.AreaPreview(M(1920, 1080), M(1440, 1080), null);
            Run.Near(0.75, p.Area.Width, 1e-6, "4:3 on 1080p: three quarters wide");
            Run.Near(1.0, p.Area.Height, 1e-6, "… full height");
            Run.Near(0.125, p.Area.X, 1e-6, "… pillarboxed");
            p = TrayApp.AreaPreview(M(3440, 1440), M(2560, 1440), M(1920, 1440));
            Run.Near(2560.0 / 3440, p.Area.Width, 1e-6, "16:9 on an ultrawide");
            Run.Near(1920.0 / 3440, p.Stretch.Width, 1e-6, "4:3 stretched over it");
            p = TrayApp.AreaPreview(full, M(3840, 2160), null);
            Run.Near(1.0, p.Area.Width, 1e-6, "never bigger than the screen");
            Run.True(TrayApp.AreaPreview(null, M(2368, 1332), null).Area.IsEmpty, "panel unknown: nothing drawn");
            Run.Equal("2368 × 1332 · 24.5″", TrayApp.AreaLabel(M(2368, 1332), full, 26.5), "the label: size and diagonal");
            Run.Equal("2368 × 1332 · 92.5 %", TrayApp.AreaLabel(M(2368, 1332), full, 0), "panel size unknown: the share instead");
        }

        // ---- the SDR brightness slider ----

        public static void GamePageBrightnessSlider()
        {
            Run.Near(0.4, TrayApp.SliderFraction(40, 20), 1e-9, "a level: its place on the slider");
            Run.Near(0.2, TrayApp.SliderFraction(-1, 20), 1e-9, "Windows' own: where Windows has it now");
            Run.Near(0.5, TrayApp.SliderFraction(-1, -1), 1e-9, "Windows' level unknown: the middle");
            Run.Near(0.0, TrayApp.SliderFraction(0, 20), 1e-9, "0 at the left");
            Run.Near(1.0, TrayApp.SliderFraction(100, 20), 1e-9, "100 at the right");
            Run.Equal(40, TrayApp.SliderLevel(80, 200), "the mouse 40 % along the track: 40");
            Run.Equal(0, TrayApp.SliderLevel(-30, 200), "left of the track: 0");
            Run.Equal(100, TrayApp.SliderLevel(250, 200), "right of it: 100");
            Run.Equal(50, TrayApp.SliderLevel(99.5, 199), "rounded");
            Run.Equal(0, TrayApp.SliderLevel(5, 0), "no track: 0");
            for (int l = 0; l <= 100; l += 10)
                Run.Equal(l, TrayApp.SliderLevel(TrayApp.SliderFraction(l, -1) * 236, 236), "level " + l + " → place → the same level");
            Run.Equal(41, TrayApp.StepBrightness(40, 20, 1), "Right: one up");
            Run.Equal(30, TrayApp.StepBrightness(40, 20, -10), "Page Down: ten down");
            Run.Equal(21, TrayApp.StepBrightness(-1, 20, 1), "from Windows' own: from where Windows has it");
            Run.Equal(19, TrayApp.StepBrightness(-1, 20, -1), "… down as well");
            Run.Equal(0, TrayApp.StepBrightness(3, 20, -10), "never below 0 (Windows' own is the check box)");
            Run.Equal(100, TrayApp.StepBrightness(95, 20, 100), "End: 100");
            Run.Equal(0, TrayApp.StepBrightness(60, 20, -100), "Home: 0");
            Run.Equal(20, TrayApp.StepBrightness(-1, 20, 0), "Windows' level unticked: the game gets the level Windows has now");
            Run.Equal(TrayApp.UnknownWindowsLevel, TrayApp.StepBrightness(-1, -1, 0), "… or the middle, unknown");
            Run.Equal("Windows'", TrayApp.BrightnessText(-1), "the card's value: Windows'");
            Run.Equal("40", TrayApp.BrightnessText(40), "… or the level");
            Run.Equal(240, TrayApp.BrightnessNits(40), "40: 240 nits");
            Run.Equal(80, TrayApp.BrightnessNits(0), "0: 80 nits");
            Run.Equal(480, TrayApp.BrightnessNits(100), "100: 480 nits");
        }
    }
}
