// The flyout's Games tab: the library (a search box over the games, each with what it changes, its HDR switch and the
// way to its page), a game's page (its layers in Live's order, each with its own control: HDR, SDR brightness with a
// slider, the competitive area with a to-scale picture of the screen, the stretch over it, and what NVIDIA scaling
// does), the lists opened from the page (the area's resolutions, the stretch's), and the running programs to add.
// As everywhere in the flyout, nothing here changes state itself: each control calls into TrayApp and rebuilds.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace HdrSwitch
{
    sealed partial class TrayApp
    {
        sealed partial class Flyout
        {
            const string SearchKey = "search";
            const int NoSlide = int.MinValue;

            string page;            // the game whose page is open, or null: the library (or the programs to add)
            string sub;             // a list opened from the page: "res" (the area's resolutions) or "stretch", or null
            bool brightOpen;        // the page's SDR brightness card is open: its slider shows
            string search = "";     // what is typed into the library's search box
            int slideLevel = NoSlide;   // the slider's level while it is dragged or stepped, before it is saved
            int windowsLevel = -1;  // Windows' own SDR brightness when the page opened (0…100), -1 unknown
            bool nvidiaHere;        // an NVIDIA driver runs the display: read when the page opened
            bool dragging;          // the slider follows the mouse
            // Keys step the slider at once; the level is saved once they rest, not with every step of a held key.
            readonly System.Windows.Forms.Timer brightSave = new System.Windows.Forms.Timer { Interval = 400 };

            // ---- entering and leaving a game's page ----

            void EnterPage(string name)
            {
                CommitBrightness();
                page = name;
                sub = null;
                brightOpen = false;
                slideLevel = NoSlide;
                windowsLevel = ReadWindowsLevel();
                nvidiaHere = ReadNvidia();
            }

            void LeavePage()
            {
                CommitBrightness();
                page = null;
                sub = null;
                brightOpen = false;
                slideLevel = NoSlide;
            }

            /// <summary>A game's row (or Enter in the search): its page, the keyboard coming back to from.</summary>
            void OpenPage(string name, string from)
            {
                EnterPage(name);
                Into(from);
                Rebuild();
            }

            /// <summary>A list from the page: the area's resolutions or the stretch's, the keyboard coming back to from.</summary>
            void OpenSub(string which, string from)
            {
                CommitBrightness();
                sub = which;
                Into(from);
                Rebuild();
            }

            /// <summary>Before laying out: a stretch list whose game lost its area (resolutions.txt edited) closes.</summary>
            void Settle()
            {
                if (page != null && sub == "stretch" && !app.profiles.ContainsKey(page)) sub = null;
            }

            int ReadWindowsLevel()
            {
                if (sample != null) return sample.WindowsLevel;
                try
                {
                    if (app.brightBack >= 0) return Hdr.PercentOf(app.brightBack);   // a game's level is in effect: Windows' own is the one from before
                    var d = Hdr.Primary();
                    int raw = d != null ? Hdr.SdrWhite(d) : -1;
                    return raw >= 0 ? Hdr.PercentOf(raw) : -1;
                }
                catch (Exception e) { Guard.Log("Reading Windows' SDR brightness", e); return -1; }
            }

            bool ReadNvidia()
            {
                if (sample != null) return sample.Nvidia;
                try { return Nvidia.Scaling(TargetGdi()) >= 0; }
                catch (Exception e) { Guard.Log("Asking the NVIDIA driver", e); return false; }
            }

            // ---- the search ----

            /// <summary>Typing goes into the search: the library is showing, on the Games tab.</summary>
            bool Searching() { return tab == FlyoutTab.Games && Depth() == 0 && !closing; }

            bool OnSearch() { return Focusable(focus) && items[focus].Key == SearchKey; }

            /// <summary>The search becomes text: the list filters, starts at the top, and the keyboard is in the box.</summary>
            void Type(string text)
            {
                bool there = OnSearch();   // already in the box: a screen reader hears the new text (Tell), not the focus again
                search = text;
                ScrollHome();
                wheel = 0;
                focusKey = SearchKey;
                keyboard = true;
                Rebuild();
                int i = items.FindIndex(it => it.Key == SearchKey);
                if (i >= 0 && !there) SetFocus(i);
            }

            /// <summary>Enter in the search box: the first game it finds.</summary>
            void OpenFirstMatch()
            {
                string first = LibraryNames().FirstOrDefault(n => GameMatches(search, app.DisplayName(n), n));
                if (first != null) OpenPage(first, "row:" + first);
            }

            /// <summary>Every game the app knows: with HDR, never, or with only a resolution – by the name shown.</summary>
            List<string> LibraryNames()
            {
                return app.games.Concat(app.excluded).Concat(app.profiles.Keys).Distinct().OrderBy(app.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
            }

            // ---- the SDR brightness slider ----

            bool OnSlider() { return Focusable(focus) && items[focus].Slide != null; }

            /// <summary>The level the page shows: the one being dragged or stepped, else the saved one (-1: Windows').</summary>
            int ShownLevel()
            {
                if (slideLevel != NoSlide) return slideLevel;
                int l;
                return page != null && app.brightness.TryGetValue(page, out l) ? l : -1;
            }

            /// <summary>An arrow, Page Up/Down, Home or End on the slider: shown at once, saved once the keys rest.</summary>
            void StepSlider(int delta)
            {
                slideLevel = StepBrightness(ShownLevel(), windowsLevel, delta);
                brightSave.Stop();
                brightSave.Start();
                Rebuild();
            }

            /// <summary>
            /// The level dragged or stepped to is saved to brightness.txt – and applied, if the game runs with HDR on
            /// (SetBrightness, as choosing a level always has).
            /// </summary>
            void CommitBrightness()
            {
                brightSave.Stop();
                if (slideLevel == NoSlide) return;
                int level = slideLevel;
                slideLevel = NoSlide;
                if (page == null) return;
                int now;
                if (!app.brightness.TryGetValue(page, out now)) now = -1;
                if (level != now) app.SetBrightness(page, level);
            }

            // ---- what the tab shows ----

            /// <summary>One game as the tab shows it, from the app's lists.</summary>
            GameInfo Info(string name)
            {
                var g = new GameInfo
                {
                    Exe = name, Name = app.DisplayName(name), Launcher = LabelLauncher(app.LabelOf(name)), Hdr = app.games.Contains(name),
                    Running = app.running.Values.Any(r => r.Name == name), Nvidia = nvidiaHere,
                };
                int b;
                if (app.brightness.TryGetValue(name, out b)) g.Brightness = b;
                Resolution.Mode p;
                if (app.profiles.TryGetValue(name, out p)) g.Area = p;
                g.Stretch = g.Area != null ? app.StretchOf(name) : null;
                return g;
            }

            /// <summary>The display the resolutions are for: its GDI name, the panel's own size, and its diagonal.</summary>
            void ScreenNow(out string gdi, out Resolution.Mode full, out double inches)
            {
                if (sample != null) { gdi = null; full = sample.Full; inches = sample.Inches; return; }
                gdi = TargetGdi();
                var cur = Resolution.Current(gdi);
                var pic = Resolution.Read(gdi);   // the panel's own size is the target of the display path
                full = pic != null ? new Resolution.Mode { Width = pic.TargetW, Height = pic.TargetH, Hz = cur != null ? cur.Hz : 0 } : cur;
                inches = Resolution.Inches(gdi);
            }

            /// <summary>The display offers this size at this rate (else it has to be created first).</summary>
            bool Offered(string gdi, Resolution.Mode m, int hz)
            {
                if (sample != null) return sample.Modes.Any(x => x.Width == m.Width && x.Height == m.Height);
                try { return Resolution.Available(gdi, m.Width, m.Height, hz); }
                catch (Exception e) { Guard.Log("Asking for a resolution", e); return true; }
            }

            /// <summary>The refresh rate a game's resolutions are listed at: its own, else the display's now.</summary>
            int HzFor(string game)
            {
                Resolution.Mode chosen;
                app.profiles.TryGetValue(game, out chosen);
                if (chosen != null && chosen.Hz > 0) return chosen.Hz;
                var cur = sample != null ? sample.Current : Resolution.Current(TargetGdi());
                return cur != null ? cur.Hz : 60;
            }

            /// <summary>The Games tab: the library, a game's page, or a list (a page's resolutions or stretch, the programs to add).</summary>
            int BuildGames(int W, int M, int y)
            {
                if (page != null && sub == null) return BuildPage(W, M, y);
                if (page == null && !picking) return BuildLibrary(W, M, y);
                return BuildList(W, M, y);
            }

            /// <summary>
            /// The top row of a view: on a game's page, back to the library ("‹ Games"; the game's name heads the page
            /// itself); in a list, the back arrow, what the list is, and the rate or the area it is for.
            /// </summary>
            int ViewBar(int W, int M)
            {
                Color sbg = P.Surface;
                if (page != null && sub == null)
                {
                    int bw = S(8) + S(16) + S(6) + TextRenderer.MeasureText("Games", fonts.Body, Size.Empty, TfLeft).Width + S(10);
                    items.Add(new Item { Rect = new Rectangle(M - S(4), S(8), bw, S(32)), Key = "back", Name = "Back to Games", Click = GoBack, Paint = (g, rc, lk) =>
                    {
                        SubtleButton(g, rc, lk, "", fonts.Body, sbg, sbg);
                        Color fg = Theme.Mix(P.Ink(P.Text2, sbg), P.Ink(P.Text1, sbg), lk.Hover);
                        LineIcon(g, "back", new Rectangle(rc.X + S(8), rc.Y + (rc.Height - S(16)) / 2, S(16), S(16)), fg);
                        Draw(g, "Games", fonts.Body, fg, new Rectangle(rc.X + S(8) + S(16) + S(6), rc.Y, rc.Width - S(30), rc.Height), TfLeft);
                    } });
                    return S(48);
                }
                var back = new Rectangle(M - S(4), S(8), S(32), S(32));
                items.Add(new Item { Rect = back, Key = "back", Name = "Back", Click = GoBack, Paint = (g, rc, lk) =>
                {
                    SubtleButton(g, rc, lk, "", fonts.Body, sbg, sbg);
                    LineIcon(g, "back", new Rectangle(rc.X + (rc.Width - S(16)) / 2, rc.Y + (rc.Height - S(16)) / 2, S(16), S(16)), P.Ink(P.Text1, sbg));
                } });
                Resolution.Mode area = null;
                if (page != null) app.profiles.TryGetValue(page, out area);
                string head = picking ? "Add a program" : sub == "res" ? "Competitive area" : "Stretch";
                string headRight = picking ? "" : sub == "res" ? "at " + HzFor(page) + " Hz" : area != null ? "inside " + area.Text : "";
                string forGame = page != null ? " for " + app.DisplayName(page) : "";
                int rightW = headRight.Length == 0 ? 0 : TextRenderer.MeasureText(headRight, fonts.Caption, Size.Empty, TfLeft).Width + S(8);
                int hx = back.Right + S(8);
                items.Add(new Item { Rect = new Rectangle(hx, back.Y, W - M - hx, back.Height), Role = AccessibleRole.StaticText, Name = head + forGame + (headRight.Length > 0 ? ", " + headRight : ""), Paint = (g, rc, lk) =>
                {
                    Draw(g, head, fonts.Strong, P.Ink(P.Text1, sbg), new Rectangle(rc.X, rc.Y, rc.Width - rightW, rc.Height), TfLeft);
                    Draw(g, headRight, fonts.Caption, P.Ink(P.Text2, sbg), new Rectangle(rc.Right - rightW, rc.Y, rightW, rc.Height), TfLeft | TextFormatFlags.Right);
                } });
                return S(48);
            }

            /// <summary>The footer bar of the Games tab: a line of text and the pencil for the file behind what it shows.</summary>
            void GamesFooter(int W, int M, int y, int barH, RollText text, string file)
            {
                Color fbg = Footer(W, y, barH);
                var edit = new Rectangle(W - M - S(32), y + (barH - S(32)) / 2, S(32), S(32));
                items.Add(new Item { Rect = new Rectangle(M + S(4), y, edit.X - S(8) - M - S(4), barH), Paint = (g, rc, lk) =>
                    DrawRoll(g, text, fonts.Caption, P.Ink(P.Text2, fbg), fbg, rc, -1) });
                items.Add(new Item { Rect = edit, Key = "edit", Tip = "Edit " + System.IO.Path.GetFileName(file), Click = () =>
                {
                    Dismiss(true);   // at once: Notepad is about to come to the front
                    try { Process.Start("notepad.exe", "\"" + file + "\""); } catch { }
                }, Paint = (g, rc, lk) => SubtleButton(g, rc, lk, "", fonts.Glyph, Theme.Mix(P.Ink(P.Text2, fbg), P.Ink(P.Text1, fbg), lk.Hover), fbg) });
            }

            // ---- the library ----

            /// <summary>
            /// The library: the search box, then the games that match, 44 px rows (up to five, fewer when the panel
            /// wouldn't fit above the taskbar; the list scrolls), and the footer bar with how many have HDR and the pencil
            /// for games.txt.
            /// </summary>
            int BuildLibrary(int W, int M, int y)
            {
                int cw = W - 2 * M, G = S(8);
                rowH = S(44);
                var all = LibraryNames();
                bool searching = search.Trim().Length > 0;
                var names = searching ? all.Where(n => GameMatches(search, app.DisplayName(n), n)).ToList() : all;
                SearchBox(new Rectangle(M, y + S(4), cw, S(32)), all.Count);
                int count = names.Count;
                int visible = FlyoutRows(Where().WorkingArea.Height, S(12), GamesFixed(k, false, true), rowH, count);
                listClip = new Rectangle(M, y + S(4) + S(40), cw, visible * rowH);
                scrollMax = Math.Max(0, count * rowH - listClip.Height);
                if (scrollTo.Target > scrollMax) scrollTo.Snap(scrollMax);
                scroll = (int)Math.Round(scrollTo.Value);
                Color sbg = P.Surface;
                if (count == 0)
                {
                    string msg = searching ? "No games match “" + search.Trim() + "”" : app.scanning ? "Looking…" : "No games";
                    items.Add(new Item { Rect = listClip, Paint = (g, rc, lk) => Draw(g, msg, fonts.Body, P.Ink(P.Text3, sbg), rc, TfCenter) });
                }
                for (int i = 0; i < count; i++) GameRow(new Rectangle(M, listClip.Y + i * rowH, cw, rowH), Info(names[i]));
                y = listClip.Bottom + S(4) + G;
                int withHdr = all.Count(n => app.games.Contains(n));
                GamesFooter(W, M, y, S(48), Roll("count", GamesSummary(withHdr, all.Count, count, searching), fonts.Caption), FlyoutFile("games", false));
                return y + S(48);
            }

            /// <summary>
            /// WinUI's search box: the magnifier, what is typed (or "Search 38 games"), a caret and the accent underline
            /// while the keyboard is in it, and a clear button once something is typed. Typing anywhere on the library
            /// types here.
            /// </summary>
            void SearchBox(Rectangle r, int total)
            {
                string text = search, placeholder = "Search " + total + (total == 1 ? " game" : " games");
                bool hasText = text.Length > 0;
                int clearW = hasText ? S(32) : 0;
                items.Add(new Item { Rect = r, Key = SearchKey, Role = AccessibleRole.Text, Name = "Search games", Value = text, Tip = null, Click = () =>
                {
                    int i = items.FindIndex(it => it.Key == SearchKey);
                    if (i >= 0) SetFocus(i);
                }, Paint = (g, rc, lk) =>
                {
                    Color under = P.Surface;
                    bool active = Focusable(focus) && items[focus].Key == SearchKey;
                    Color fill = active ? (P.HighContrast ? P.Surface : Palette.Over(P.Card, P.Surface)) : Theme.Mix(P.ControlFill, P.ControlFill2, lk.Hover);
                    Theme.Fill(g, rc, S(4), fill);
                    Outline(g, rc, S(4), P.HighContrast && (lk.Hover > 0.5 || active) ? P.HoverStroke : P.ControlStroke);
                    // The underline: 1 px ControlStrongStroke, 2 px accent while typing into it.
                    int uh = active ? Math.Max(2, S(2)) : 1;
                    using (var clip = Theme.Round(rc, S(4)))
                    {
                        var st = g.Save();
                        g.SetClip(clip, CombineMode.Intersect);
                        using (var b = new SolidBrush(active ? P.Accent : P.ControlStrong)) g.FillRectangle(b, rc.X, rc.Bottom - uh, rc.Width, uh);
                        g.Restore(st);
                    }
                    Color bg = Palette.Over(fill, under);
                    LineIcon(g, "search", new Rectangle(rc.X + S(10), rc.Y + (rc.Height - S(16)) / 2, S(16), S(16)), P.Ink(P.Text3, bg));
                    var tr = new Rectangle(rc.X + S(10) + S(16) + S(8), rc.Y, rc.Width - S(34) - S(10) - clearW, rc.Height - 1);
                    if (!hasText) Draw(g, placeholder, fonts.Body, P.Ink(P.Text3, bg), tr, TfLeft);
                    else Draw(g, text, fonts.Body, P.Ink(P.Text1, bg), tr, TfLeft);
                    if (active)
                    {
                        int tw = hasText ? Math.Min(tr.Width, TextRenderer.MeasureText(g, text, fonts.Body, Size.Empty, TfLeft).Width) : 0;
                        using (var b = new SolidBrush(P.Ink(P.Text1, bg))) g.FillRectangle(b, tr.X + tw + 1, rc.Y + S(7), Math.Max(1, S(1)), rc.Height - S(14));
                    }
                } });
                if (hasText)
                    items.Add(new Item { Rect = new Rectangle(r.Right - clearW, r.Y, clearW, r.Height), Key = "search:clear", Name = "Clear search", ArrowSkip = true,
                        Click = () => Type(""), Paint = (g, rc, lk) =>
                    {
                        var hr = Rectangle.Inflate(rc, -S(4), -S(4));
                        SubtleButton(g, hr, lk, "", fonts.Body, P.Surface, P.Surface);
                        LineIcon(g, "clear", new Rectangle(rc.X + (rc.Width - S(16)) / 2, rc.Y + (rc.Height - S(16)) / 2, S(16), S(16)), P.Ink(P.Text2, Palette.Over(P.ControlFill, P.Surface)));
                    } });
            }

            /// <summary>
            /// A game in the library: its name (with "· running" in the HDR colour while it runs), what it changes under
            /// it, its HDR switch (on or never) and a chevron. The row opens the game's page; the switch is a control of
            /// its own. From the keyboard: Enter or Right opens the page, Space flips the switch, Up and Down go from row
            /// to row; Tab also reaches the switch.
            /// </summary>
            void GameRow(Rectangle rr, GameInfo gi)
            {
                string name = gi.Exe, shown = gi.Name, recipe = GameRecipe(gi);
                bool on = gi.Hdr, running = gi.Running;
                int L = S(12);
                int chevOff = rr.Width - L - S(16);              // from the row's left
                int tgOff = chevOff - S(10) - S(40);
                string key = "row:" + name;
                Action open = () => OpenPage(name, key);
                Action flip = () => { if (app.games.Contains(name)) app.Remove(name); else app.Add(name); Rebuild(); };
                Color sbg = P.Surface;
                Color t1 = P.Ink(P.Text1, sbg), t2 = P.Ink(P.Text2, sbg), t3 = P.Ink(P.Text3, sbg);
                const string RunMark = " · running";
                items.Add(new Item { Rect = rr, Scrolls = true, RingInside = true, Key = key, Name = shown + (running ? ", running" : "") + ", " + recipe,
                    Tip = null, Click = open, Right = open, Space = flip, Paint = (g, rc, lk) =>
                {
                    RowBg(g, rc, lk, false);
                    int tx = rc.X + L, tw = tgOff - S(12) - L;
                    int top = rc.Y + (rc.Height - S(36)) / 2;
                    int runW = running ? TextRenderer.MeasureText(g, RunMark, fonts.Caption, Size.Empty, TfLeft).Width : 0;
                    int nameW = Math.Min(tw - runW, TextRenderer.MeasureText(g, shown, fonts.Body, Size.Empty, TfLeft).Width);
                    Draw(g, shown, fonts.Body, t1, new Rectangle(tx, top, nameW, S(20)), TfLeft);
                    if (running) Draw(g, RunMark, fonts.Caption, P.Ink(P.HighContrast ? P.Accent : P.HdrText, sbg), new Rectangle(tx + nameW, top + S(1), runW, S(20)), TfLeft);
                    Draw(g, recipe, fonts.Caption, t2, new Rectangle(tx, top + S(20), tw, S(16)), TfLeft);
                    LineIcon(g, "chev", new Rectangle(rc.X + chevOff, rc.Y + (rc.Height - S(16)) / 2, S(16), S(16)), Theme.Mix(t3, t1, lk.Hover));
                } });
                var tg = new Rectangle(rr.X + tgOff - S(4), rr.Y + (rowH - S(28)) / 2, S(48), S(28));
                items.Add(new Item { Rect = tg, Scrolls = true, Key = "hdr:" + name, On = on, Role = AccessibleRole.CheckButton, Selected = on, ArrowSkip = true,
                    Name = "HDR for " + shown, Radius = S(14), Click = flip,
                    Paint = (g, rc, lk) => Toggle(g, new RectangleF(rc.X + S(4), rc.Y + (rc.Height - S(20)) / 2f, S(40), S(20)), lk.On, lk.Hover, sbg) });
            }

            // ---- a game's page ----

            /// <summary>
            /// A game's page: its name and exe (and launcher), a setup hint when something has to be created first, then
            /// one card per layer in Live's order – HDR, SDR brightness (opens a slider), competitive area (opens a
            /// to-scale picture and the size), stretch (only over an area) and NVIDIA scaling (what the app does by
            /// itself). The cards scroll when they don't fit; the footer bar has the pencil for the file behind them.
            /// </summary>
            int BuildPage(int W, int M, int y)
            {
                int cw = W - 2 * M, G = S(8), rowCH = S(52), gap = S(4);
                string name = page;
                var gi = Info(name);
                string gdi;
                Resolution.Mode full;
                double inches;
                ScreenNow(out gdi, out full, out inches);
                if (gi.Area != null && full != null) gi.AreaDetail = SizeText(gi.Area, full, inches);
                int hz = HzFor(name);
                var cards = GameCards(gi);
                Color sbg = P.Surface;

                // The game's name and what it is.
                string subline = gi.Exe + ".exe" + (gi.Launcher != null ? " · " + gi.Launcher : "");
                bool running = gi.Running;
                var st = new Rectangle(M, y - S(4), cw, S(28) + S(16));
                items.Add(new Item { Rect = st, Key = "title", Role = AccessibleRole.StaticText, Name = gi.Name + ", " + subline + (running ? ", running" : ""), Paint = (g, rc, lk) =>
                {
                    int textW = rc.Width - S(8);
                    Draw(g, gi.Name, fonts.Title, P.Ink(P.Text1, sbg), new Rectangle(rc.X + S(4), rc.Y, textW, S(28)), TfLeft);
                    const string RunMark = " · running";
                    int runW = running ? TextRenderer.MeasureText(g, RunMark, fonts.Caption, Size.Empty, TfLeft).Width : 0;
                    int subW = Math.Min(textW - runW, TextRenderer.MeasureText(g, subline, fonts.Caption, Size.Empty, TfLeft).Width);
                    Draw(g, subline, fonts.Caption, P.Ink(P.Text2, sbg), new Rectangle(rc.X + S(4), rc.Y + S(28), subW, S(16)), TfLeft);
                    if (running) Draw(g, RunMark, fonts.Caption, P.Ink(P.HighContrast ? P.Accent : P.HdrText, sbg), new Rectangle(rc.X + S(4) + subW, rc.Y + S(28), runW, S(16)), TfLeft);
                } });
                y = st.Bottom + G;
                int top = y, from = items.Count;

                string hint = SetupHint(gi, app.notice, gi.Area == null || Offered(gdi, gi.Area, hz), gi.Stretch == null || Offered(gdi, gi.Stretch, hz), hz);
                if (hint != null) y += HintBar(new Rectangle(M, y, cw, 0), hint) + G;

                // HDR: on, or never.
                Group(new Rectangle(M, y, cw, rowCH), 1);
                CardToggle(new Rectangle(M, y, cw, rowCH), "ghdr", "hdr", "HDR", cards[0].Desc, gi.Hdr, true, true,
                    () => { if (app.games.Contains(name)) app.Remove(name); else app.Add(name); Rebuild(); }, false, "Never", true);
                y += rowCH + gap;
                y = BrightCard(new Rectangle(M, y, cw, rowCH), gi) + gap;
                y = AreaCard(new Rectangle(M, y, cw, rowCH), gi, cards[2], full, inches) + gap;
                var stretch = cards[3];
                Group(new Rectangle(M, y, cw, rowCH), 1);
                PageRow(new Rectangle(M, y, cw, rowCH), "card:stretch", stretch, true, stretch.Enabled ? (Action)(() => OpenSub("stretch", "card:stretch")) : null);
                y += rowCH + gap;
                Group(new Rectangle(M, y, cw, rowCH), 1);
                PageRow(new Rectangle(M, y, cw, rowCH), "card:scaling", cards[4], false, null);
                y += rowCH;
                int content = y - top + S(4);

                int barH = S(48);
                int shown = FlyoutBody(Where().WorkingArea.Height, S(12), top + S(8) + barH, content, rowCH);
                Scroller(from, new Rectangle(0, top, W, shown), content, rowCH);
                y = top + shown + S(8);
                string file = FlyoutFile("game", brightOpen);
                GamesFooter(W, M, y, barH, Roll("foot:page", "Saved in " + System.IO.Path.GetFileName(file), fonts.Caption), file);
                return y + barH;
            }

            /// <summary>
            /// A setup hint on the page, as WinUI's InfoBar (warning): what has to be created in NVIDIA Control Panel, or
            /// the latest notification about this game. Returns its height.
            /// </summary>
            int HintBar(Rectangle r, string text)
            {
                bool light = P.Mode == Palette.Kind.Light;
                Color fill = P.HighContrast ? P.Surface : light ? Color.FromArgb(0xFF, 0xF4, 0xCE) : Color.FromArgb(0x43, 0x35, 0x19);   // SystemFillColorCautionBackground
                int tx = S(14) + S(20) + S(12), tw = r.Width - tx - S(12);
                int textH = TextRenderer.MeasureText(text, fonts.Caption, new Size(tw, 2000), TfWrap).Height;
                int h = Math.Max(S(48), textH + S(24));
                items.Add(new Item { Rect = new Rectangle(r.X, r.Y, r.Width, h), Key = "hint", Role = AccessibleRole.StaticText, Name = text, Paint = (g, rc, lk) =>
                {
                    Theme.Fill(g, rc, S(4), fill);
                    Outline(g, rc, S(4), P.HighContrast ? P.Text1 : P.CardStroke);
                    LineIcon(g, "info", new Rectangle(rc.X + S(14), rc.Y + S(12), S(20), S(20)), P.HighContrast ? P.Text1 : P.Caution);
                    Draw(g, text, fonts.Caption, P.Ink(P.Text1, fill), new Rectangle(rc.X + tx, rc.Y + (rc.Height - textH) / 2, tw, textH), TfWrap);
                } });
                return h;
            }

            /// <summary>
            /// A card of the page that isn't a switch: its icon, title and description, its value on the right and – when
            /// it opens a list – a chevron. click null: it can't be changed now (stretch without an area: dimmed, its
            /// description says why) or it only informs (NVIDIA scaling: read as text).
            /// </summary>
            void PageRow(Rectangle r, string key, GameCard c, bool opens, Action click)
            {
                bool enabled = click != null, info = !opens;
                string value = c.Value;
                items.Add(new Item { Rect = r, Key = key, On = c.On, Role = info ? AccessibleRole.StaticText : AccessibleRole.PushButton, RingInside = true,
                    Name = c.Title + ", " + value, Tip = c.Desc, Click = click, Listed = opens && !enabled, Paint = (g, rc, lk) =>
                {
                    Color bg = P.CardSolid;
                    if (enabled) CardHover(g, rc, lk, true, true);
                    int vw = TextRenderer.MeasureText(g, value, fonts.Caption, Size.Empty, TfLeft).Width + S(2);
                    int chevW = opens ? S(16) + S(8) : 0;
                    int ix = rc.X + S(14), tx = ix + S(20) + S(12), tw = rc.Right - S(12) - chevW - vw - S(12) - tx;
                    double on = Math.Max(0, Math.Min(1, lk.On));
                    Color dim = P.Ink(P.TextDisabled, bg);
                    Color ic = !enabled && opens ? dim : Theme.Mix(P.Ink(P.Text2, bg), P.Accent, on);
                    LineIcon(g, c.Key, new Rectangle(ix, rc.Y + (rc.Height - S(20)) / 2, S(20), S(20)), ic);
                    int ty = rc.Y + (rc.Height - S(36)) / 2;
                    Draw(g, c.Title, fonts.Body, !enabled && opens ? dim : P.Ink(P.Text1, bg), new Rectangle(tx, ty, tw, S(20)), TfLeft);
                    Draw(g, c.Desc, fonts.Caption, P.Ink(P.Text2, bg), new Rectangle(tx, ty + S(20), tw, S(16)), TfLeft);
                    Draw(g, value, fonts.Caption, !enabled && opens ? dim : P.Ink(P.Text2, bg), new Rectangle(rc.Right - S(12) - chevW - vw, rc.Y, vw, rc.Height), TfLeft | TextFormatFlags.Right);
                    if (opens) LineIcon(g, "chev", new Rectangle(rc.Right - S(12) - S(16), rc.Y + (rc.Height - S(16)) / 2, S(16), S(16)), enabled ? Theme.Mix(P.Ink(P.Text3, bg), P.Ink(P.Text1, bg), lk.Hover) : dim);
                } });
            }

            /// <summary>A card with something open below it: one card round both, a divider between the row and what it opened.</summary>
            void OpenCard(Rectangle r, int rowH_)
            {
                items.Add(new Item { Rect = r, Paint = (g, rc, lk) =>
                {
                    CardBox(g, rc);
                    if (rc.Height > rowH_)
                        using (var b = new SolidBrush(P.Divider)) g.FillRectangle(b, rc.X + DividerWidth(), rc.Y + rowH_, rc.Width - 2 * DividerWidth(), DividerWidth());
                } });
            }

            /// <summary>
            /// SDR brightness: the level (or "Windows'") on the right; a click opens a slider 0–100 under it, with the
            /// nits it means, and "Use Windows' level". Dragging shows the level at once and saves it when let go; the
            /// keys save it once they rest. Saved to brightness.txt and applied at once if the game runs with HDR on.
            /// Returns where the card ends.
            /// </summary>
            int BrightCard(Rectangle row, GameInfo gi)
            {
                string name = gi.Exe;
                bool open = brightOpen, hdr = gi.Hdr;
                int level = ShownLevel();
                int expH = open ? S(8) + S(32) + S(32) + S(12) : 0;
                OpenCard(new Rectangle(row.X, row.Y, row.Width, row.Height + expH), row.Height);
                items.Add(new Item { Rect = row, Key = "card:bright", On = level >= 0, RingInside = true, Expands = true, Expanded = open,
                    Name = "SDR brightness", Value = BrightnessText(level), Tip = open ? "Hide the slider" : "Set its own level",
                    Click = () => { CommitBrightness(); brightOpen = !brightOpen; Rebuild(); }, Paint = (g, rc, lk) =>
                {
                    Color bg = P.CardSolid;
                    CardHover(g, rc, lk, true, !open);
                    int lv = ShownLevel();   // follows a drag
                    string value = BrightnessText(lv);
                    int vw = TextRenderer.MeasureText(g, value, fonts.Caption, Size.Empty, TfLeft).Width + S(2);
                    int right = S(12) + S(16) + S(8) + vw + S(12);
                    RowText(g, rc, "bright", "SDR brightness", BrightnessDesc(lv, hdr), lv >= 0 ? 1 : 0, right);
                    Draw(g, value, fonts.Caption, P.Ink(P.Text2, bg), new Rectangle(rc.Right - S(12) - S(16) - S(8) - vw, rc.Y, vw, rc.Height), TfLeft | TextFormatFlags.Right);
                    LineIcon(g, open ? "up" : "down", new Rectangle(rc.Right - S(12) - S(16), rc.Y + (rc.Height - S(16)) / 2, S(16), S(16)), Theme.Mix(P.Ink(P.Text3, bg), P.Ink(P.Text1, bg), lk.Hover));
                } });
                if (!open) return row.Bottom;

                // The slider: its track from the text column to the nits, the thumb 18 px.
                int left = row.X + S(46), nitsW = S(64);
                var sr = new Rectangle(left - S(9), row.Bottom + S(8), row.Right - S(12) - nitsW - S(8) - (left - S(9)), S(32));
                int trackX = sr.X + S(9), trackW = sr.Width - S(18);
                items.Add(new Item { Rect = sr, Key = "slider", Role = AccessibleRole.Slider, Name = "SDR brightness for " + gi.Name, Radius = S(4),
                    Value = level < 0 ? "Windows' level" : level + ", " + BrightnessNits(level) + " nits", Click = () => { }, Listed = true,
                    Slide = x => { slideLevel = SliderLevel(x - trackX, trackW); Invalidate(); }, Paint = (g, rc, lk) =>
                {
                    Color bg = P.CardSolid;
                    int lv = ShownLevel();
                    bool own = lv >= 0;
                    float cy = rc.Y + rc.Height / 2f;
                    float tx0 = rc.X + S(9), tw = rc.Width - S(18);
                    float at = tx0 + tw * (float)SliderFraction(lv, windowsLevel);
                    Theme.Fill(g, new RectangleF(tx0, cy - S(2), tw, S(4)), S(2), P.ControlStrong);
                    if (own) Theme.Fill(g, new RectangleF(tx0, cy - S(2), at - tx0, S(4)), S(2), P.Accent);
                    Theme.Dot(g, at, cy, S(18) + 2, P.HighContrast ? P.Text1 : Palette.Over(P.ControlStroke, bg));   // the thumb's 1 px outline
                    Theme.Dot(g, at, cy, S(18), P.Surface);
                    float inner = S(12) + (S(14) - S(12)) * (float)Math.Max(0, Math.Min(1, lk.Hover));
                    Theme.Dot(g, at, cy, inner, own ? P.Accent : P.Ink(P.Text3, bg));
                    // What the thumb's place means; for Windows' own, dimmed: the level Windows has now.
                    int at_ = own ? lv : StepBrightness(-1, windowsLevel, 0);
                    Draw(g, BrightnessNits(at_) + " nits", fonts.Caption, P.Ink(own ? P.Text2 : P.Text3, bg), new Rectangle(rc.Right + S(8), rc.Y, nitsW, rc.Height), TfLeft | TextFormatFlags.Right);
                } });

                // "Use Windows' level": a check box. Unticked, the game gets Windows' level now as its own.
                bool windows = level < 0;
                string label = "Use Windows' level";
                int cbW = S(20) + S(8) + TextRenderer.MeasureText(label, fonts.Body, Size.Empty, TfLeft).Width + S(8);
                var cb = new Rectangle(left - S(4), sr.Bottom, cbW + S(4), S(32));
                items.Add(new Item { Rect = cb, Key = "bright:windows", Role = AccessibleRole.CheckButton, Selected = windows, On = windows, Name = label, Click = () =>
                {
                    slideLevel = NoSlide;
                    brightSave.Stop();
                    app.SetBrightness(name, windows ? StepBrightness(-1, windowsLevel, 0) : -1);
                    Rebuild();
                }, Paint = (g, rc, lk) => CheckBox(g, rc, lk, label) });
                return row.Bottom + expH;
            }

            /// <summary>WinUI's CheckBox: a 20 px box (the accent with a tick when checked) and its label.</summary>
            void CheckBox(Graphics g, Rectangle rc, Look lk, string label)
            {
                Color bg = P.CardSolid;
                var box = new RectangleF(rc.X + S(4), rc.Y + (rc.Height - S(20)) / 2f, S(20), S(20));
                double t = Math.Max(0, Math.Min(1, lk.On));
                if (t < 0.99)
                {
                    Theme.Fill(g, box, S(4), Theme.Fade(Theme.Mix(P.ControlFill2, P.ControlFill3, lk.Hover), 1 - t));
                    using (var pen = new Pen(Theme.Fade(P.ControlStrong, 1 - t), Math.Max(1, S(1))))
                    using (var p = Theme.Round(RectangleF.Inflate(box, -0.5f, -0.5f), S(4) - 0.5f)) g.DrawPath(pen, p);
                }
                if (t > 0.01)
                {
                    Theme.Fill(g, box, S(4), Theme.Fade(Theme.Mix(P.Accent, P.AccentHover, lk.Hover), t));
                    LineIcon(g, "check", Rectangle.Round(box), Theme.Mix(bg, P.OnAccent, t));
                }
                Draw(g, label, fonts.Body, P.Ink(P.Text1, bg), new Rectangle((int)box.Right + S(8), rc.Y, rc.Right - (int)box.Right - S(8), rc.Height), TfLeft);
            }

            /// <summary>
            /// Competitive area: a switch. Off → on opens the list of sizes (none is chosen yet); on → off takes the area
            /// (and its stretch) away. While on, it shows the screen to scale with the area 1:1 in the middle and the
            /// stretched resolution inside it, and the size with "More…", which opens the full list. Returns where it ends.
            /// </summary>
            int AreaCard(Rectangle row, GameInfo gi, GameCard c, Resolution.Mode full, double inches)
            {
                string name = gi.Exe;
                bool on = gi.Area != null;
                int pw = row.Width - S(46) - S(12);
                int ph = full != null && full.Width > 0 ? (int)Math.Round((double)pw * full.Height / full.Width) : pw * 9 / 16;
                int expH = on ? S(10) + ph + S(8) + S(32) + S(12) : 0;
                OpenCard(new Rectangle(row.X, row.Y, row.Width, row.Height + expH), row.Height);
                CardToggle(row, "garea", "area", "Competitive area", c.Desc, on, true, !on,
                    on ? (Action)(() => { app.SetProfile(name, null); Rebuild(); }) : () => OpenSub("res", "tile:garea"), false);
                if (!on) return row.Bottom;

                var area = gi.Area;
                var stretch = gi.Stretch;
                var pic = AreaPreview(full, area, stretch);
                string label = AreaLabel(area, full, inches);
                var scr = new Rectangle(row.X + S(46), row.Bottom + S(10), pw, ph);
                items.Add(new Item { Rect = scr, Key = "preview", Role = AccessibleRole.StaticText,
                    Name = "Competitive area " + label + (stretch != null ? ", " + stretch.Text + " stretched over it" : ""), Paint = (g, rc, lk) =>
                {
                    // The screen: dark in every mode, as a screen is; in high contrast the window colours.
                    Color screen = P.HighContrast ? P.Surface : Color.FromArgb(0x0B, 0x0B, 0x0D);
                    Color line = P.HighContrast ? P.Accent : Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF);
                    Color ink = P.HighContrast ? P.Text1 : Color.White;
                    Theme.Fill(g, rc, S(4), screen);
                    Outline(g, rc, S(4), P.HighContrast ? P.Text1 : P.ControlStroke);
                    Func<RectangleF, RectangleF> on_ = f => new RectangleF(rc.X + f.X * rc.Width, rc.Y + f.Y * rc.Height, f.Width * rc.Width, f.Height * rc.Height);
                    var ar = on_(pic.Area);
                    if (stretch != null)
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
                    using (var pen = new Pen(line, Math.Max(1.5f, 1.5f * k)))
                    using (var p = Theme.Round(RectangleF.Inflate(ar, -0.75f * k, -0.75f * k), S(2))) g.DrawPath(pen, p);
                    string l1 = stretch != null ? stretch.Text + " stretched" : label, l2 = stretch != null ? "over " + label : null;
                    int lh = S(16);
                    int ly = rc.Y + (rc.Height - (l2 != null ? 2 * lh : lh)) / 2;
                    Draw(g, l1, fonts.Caption, ink, new Rectangle(rc.X, ly, rc.Width, lh), TfCenter);
                    if (l2 != null) Draw(g, l2, fonts.Caption, ink, new Rectangle(rc.X, ly + lh, rc.Width, lh), TfCenter);
                } });

                // The size: the one chosen, and More… for the full list (at the rate it is switched at).
                var seg = new Rectangle(scr.X, scr.Bottom + S(8), pw, S(32));
                items.Add(new Item { Rect = seg, Paint = (g, rc, lk) =>
                {
                    Theme.Fill(g, rc, S(4), P.ControlFill2);
                    Outline(g, rc, S(4), P.ControlStroke);
                } });
                string cur = area.Hz > 0 ? area.Text + " @ " + area.Hz + " Hz" : area.Text;
                int moreW = TextRenderer.MeasureText("More…", fonts.Label, Size.Empty, TfLeft).Width + S(32);
                var curR = new Rectangle(seg.X + S(2), seg.Y + S(2), seg.Width - S(4) - moreW, seg.Height - S(4));
                var moreR = new Rectangle(curR.Right, curR.Y, moreW, curR.Height);
                items.Add(new Item { Rect = curR, Key = "seg:area", On = true, Role = AccessibleRole.RadioButton, Selected = true, Radius = S(3),
                    Name = "Competitive area " + cur, Tip = "Choose another size", Click = () => OpenSub("res", "seg:area"), Paint = (g, rc, lk) => Segment(g, rc, lk, cur) });
                items.Add(new Item { Rect = moreR, Key = "seg:more", Radius = S(3), Name = "More sizes", Tip = "Every size the display has, at " + HzFor(name) + " Hz",
                    Click = () => OpenSub("res", "seg:more"), Paint = (g, rc, lk) => Segment(g, rc, lk, "More…") });
                return row.Bottom + expH;
            }

            // ---- the lists: a page's resolutions or stretch, the programs to add ----

            /// <summary>
            /// A list under a view: the programs running with a window (to add one), or – from a game's page – every size
            /// the display offers at the game's rate (Off, then the screen's own shape first, each with how much of the
            /// panel it covers), or the sizes that fit inside its area, to stretch over it. A pick saves it, applies it
            /// if the game runs, and goes back to the page.
            /// </summary>
            int BuildList(int W, int M, int y)
            {
                int cw = W - 2 * M, G = S(8);
                rowH = S(44);
                string game = page;
                bool areaList = sub == "res", stretchList = sub == "stretch";
                var names = new List<string>();
                var modes = new List<Resolution.Mode>();   // null first: Off / Not stretched
                Resolution.Mode chosen = null, full = null;
                double inches = 0;
                int hz = 0;
                if (picking) names = WindowedNow().Where(n => !app.games.Contains(n)).ToList();
                else
                {
                    string gdi;
                    ScreenNow(out gdi, out full, out inches);
                    app.profiles.TryGetValue(game, out chosen);
                    hz = HzFor(game);
                    int hzNow = hz;
                    string gdiNow = gdi;
                    Func<IEnumerable<Resolution.Mode>> all = () => sample != null ? (IEnumerable<Resolution.Mode>)sample.Modes : Resolution.Modes(gdiNow, hzNow);
                    modes.Add(null);
                    if (stretchList && chosen != null)   // the game's own resolution: the sizes inside the area, which the GPU stretches over it
                        modes.AddRange(all().Where(m => Fits(m, chosen)).OrderByDescending(m => (long)m.Width * m.Height));
                    else if (areaList && full != null)
                        modes.AddRange(all()
                            .Where(m => m.Width < full.Width || m.Height < full.Height)
                            .OrderBy(m => SameAspect(m, full) ? 0 : 1).ThenByDescending(m => (long)m.Width * m.Height));
                }
                int count = picking ? names.Count : modes.Count;
                bool subView = !picking;
                int footH = subView ? S(52) : S(4);
                int visible = FlyoutRows(Where().WorkingArea.Height, S(12), GamesFixed(k, subView, false), rowH, count);
                var c3 = new Rectangle(M, y + S(4), cw, visible * rowH + footH);
                Color sbg = P.Surface;
                listClip = new Rectangle(c3.X, c3.Y, c3.Width, visible * rowH);
                scrollMax = Math.Max(0, count * rowH - listClip.Height);
                if (scrollTo.Target > scrollMax) scrollTo.Snap(scrollMax);
                scroll = (int)Math.Round(scrollTo.Value);
                if (count == 0)
                    items.Add(new Item { Rect = listClip, Paint = (g, rc, lk) => Draw(g, "No programs", fonts.Body, P.Ink(P.Text3, sbg), rc, TfCenter) });
                Color t1 = P.Ink(P.Text1, sbg), t2 = P.Ink(P.Text2, sbg), t3 = P.Ink(P.Text3, sbg);
                int L = S(12);   // a row's text inset
                Resolution.Mode ownNow = game != null ? app.StretchOf(game) : null;
                for (int i = 0; i < count; i++)
                {
                    var rr = new Rectangle(c3.X, listClip.Y + i * rowH, c3.Width, rowH);
                    if (picking)
                    {
                        string name = names[i];
                        items.Add(new Item { Rect = rr, Scrolls = true, RingInside = true, Key = "pick:" + name, Name = "Add " + name + ".exe", Click = () => { app.Add(name); picking = false; Out(); Rebuild(); }, Paint = (g, rc, lk) =>
                        {
                            RowBg(g, rc, lk, false);
                            Draw(g, name + ".exe", fonts.Body, t1, new Rectangle(rc.X + L, rc.Y, rc.Width - 2 * L - S(24), rowH), TfLeft);
                            LineIcon(g, "chev", new Rectangle(rc.Right - L - S(16), rc.Y + (rowH - S(16)) / 2, S(16), S(16)), Theme.Mix(t3, t1, lk.Hover));
                        } });
                        continue;
                    }
                    var m = modes[i];
                    string key = "mode:" + (stretchList ? "own:" : "") + (m == null ? "off" : m.Width + "x" + m.Height);
                    bool isOn;
                    string label, detail;
                    Action pick;
                    if (stretchList)
                    {
                        isOn = m == null ? ownNow == null : ownNow != null && m.Width == ownNow.Width && m.Height == ownNow.Height;
                        label = m == null ? "Not stretched" : m.Text;
                        detail = m == null ? "1:1 in the area" : AspectText(m);
                        pick = () => { sub = null; Out(); app.SetStretch(game, m); Rebuild(); };
                    }
                    else
                    {
                        int keepHz = chosen != null ? chosen.Hz : 0;
                        isOn = m == null ? chosen == null : chosen != null && m.Width == chosen.Width && m.Height == chosen.Height;
                        label = m == null ? "Off" : m.Text;
                        detail = m == null ? (full == null ? "" : "Full screen") : SizeText(m, full, inches);
                        pick = () => { sub = null; Out(); app.SetProfile(game, m == null ? null : new Resolution.Mode { Width = m.Width, Height = m.Height, Hz = keepHz }); Rebuild(); };
                    }
                    bool sel = isOn;
                    string lab = label, det = detail;
                    items.Add(new Item { Rect = rr, Scrolls = true, RingInside = true, Key = key, Name = lab + (det.Length > 0 ? ", " + det : ""), Role = AccessibleRole.RadioButton, Selected = sel, Click = pick, Paint = (g, rc, lk) =>
                    {
                        RowBg(g, rc, lk, sel);
                        Draw(g, lab, fonts.Body, t1, new Rectangle(rc.X + L, rc.Y, S(130), rowH), TfLeft);
                        Draw(g, det, fonts.Caption, t2, new Rectangle(rc.X + L + S(130), rc.Y, rc.Width - 2 * L - S(130), rowH), TfLeft | TextFormatFlags.Right);
                    } });
                }
                if (subView)
                {
                    string note = stretchList
                        ? "For borderless games. Stretched by the GPU over " + (chosen != null ? chosen.Text : "the area") + "."
                        : "Missing one? Create it in NVIDIA Control Panel, at " + hz + " Hz.";
                    items.Add(new Item { Rect = new Rectangle(c3.X + S(4), listClip.Bottom + S(4), c3.Width - S(8), footH - S(8)), Paint = (g, rc, lk) =>
                        Draw(g, note, fonts.Caption, t2, rc, TextFormatFlags.NoPadding | TextFormatFlags.WordBreak | TextFormatFlags.Left | TextFormatFlags.VerticalCenter) });
                }
                y = c3.Bottom + G;
                string file = FlyoutFile(picking ? "add" : sub, false);
                string foot = picking ? "Programs with a window open" : "Saved in " + System.IO.Path.GetFileName(file);
                GamesFooter(W, M, y, S(48), Roll("foot:list", foot, fonts.Caption), file);
                return y + S(48);
            }
        }
    }
}
