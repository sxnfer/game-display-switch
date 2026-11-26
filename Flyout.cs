// The panel that opens from the tray icon, drawn by hand as a Windows 11 flyout: a solid surface in the Windows mode
// (light or dark), cards and standard controls on it in WinUI's colours (Palette.cs), the user's accent colour for
// what is on, and amber only for HDR. Nothing here changes any state itself – every click calls back into TrayApp and
// rebuilds the view.
//
// An ordinary window, not a layered one: Windows draws its rounded corners, border and shadow (DWM corner preference
// and CS_DROPSHADOW; Windows 10 gets square corners and a 1 px border drawn here), and it paints in OnPaint into
// WinForms' double buffer. The fills under text are opaque, so TextRenderer draws ClearType text. Invalidate() queues
// a paint as usual; a frame of motion paints at once.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using Microsoft.Win32;

namespace HdrSwitch
{
    sealed partial class TrayApp
    {
        sealed partial class Flyout : Form
        {
            /// <summary>How an item looks right now: each runs 0 → 1 on a spring.</summary>
            sealed class Look
            {
                public double Hover, Press, On;
                public double Busy;     // 0 → 1: a spinner takes the item's place
                public double Width;    // a pill that sizes to what it says (Item.Width), in pixels
                public double Shake;    // a sideways nudge, in pixels: something didn't work
                public static readonly Look None = new Look();
            }

            /// <summary>The springs behind a Look. Kept by the item's Key, so they survive a rebuild of the item list.</summary>
            sealed class Anim
            {
                public readonly Spring Hover = Motion.Control(0), Press = Motion.Control(0), On = Motion.Thumb(0);
                public readonly Spring Busy = Motion.Fade(0), Width = Motion.Shape(0, 0.5), Shake = Motion.Shape(0, 0.1);
                public readonly Look Look = new Look();
                public bool Seen, Shaken;
            }

            /// <summary>One painted thing: a card, a tile, a row, a button. Click == null: not interactive.</summary>
            sealed class Item
            {
                public Rectangle Rect;
                public Action<Graphics, Rectangle, Look> Paint;
                public Action Click;
                public bool Scrolls;   // lives in the scrolling list
                public string Tip;
                public string Key;     // unique; without one the item never animates
                public bool On;        // a switch, a lit tile: where Look.On is heading
                public bool Busy;      // where Look.Busy is heading
                public float Width;    // where Look.Width is heading (0: not used)
                public bool Shake;     // gives it a nudge when this turns true
                public bool InCard;    // inside the status card: clipped to its height while that animates
                public bool Grows;     // the status card itself: its height is the animated one
                public bool Below;     // below the status card: moves with its height while that animates
                public string Name;    // what a screen reader says (the tooltip when not set)
                public AccessibleRole Role = AccessibleRole.PushButton;
                public bool Selected;  // a switch that is on, the chosen row: "checked" for a screen reader
                public bool Listed;    // kept for the keyboard and screen readers while it can't be clicked: a busy button says its step
                public bool RingInside;   // a row: the focus ring goes just inside it, so it doesn't cover the rows beside it
                public float Radius;      // the focus ring's corner (4 px controls when 0)
                public Action Space;      // what Space does instead of Click (a game's row: flips its HDR switch)
                public Action Right;      // what the Right arrow does (a game's row: opens its page)
                public Action<int> Slide; // a slider: the mouse at this x (client pixels) while pressed or dragged
                public bool ArrowSkip;    // Up and Down pass it by (a row's own switch: Tab reaches it)
                public string Value;      // what a screen reader reads as its value (a slider's level, the search text)
                public bool Expands, Expanded;   // a card that opens below itself, and whether it is open
            }

            readonly TrayApp app;
            float k = 1;   // DPI scale of the monitor it opened on
            Theme.Fonts fonts;
            readonly ToolTip tip = new ToolTip { InitialDelay = 600 };
            readonly List<Item> items = new List<Item>();
            readonly Dictionary<string, Anim> anims = new Dictionary<string, Anim>();
            readonly Dictionary<string, RollText> rolls = new Dictionary<string, RollText>();   // text that rolls when it changes, by key
            readonly TextFormatFlags TfLeft, TfCenter, TfWrap;
            Rectangle listClip;
            bool rounded;            // Windows draws the corners and the border (Windows 11); else a 1 px border is drawn here
            int scroll, scrollMax, rowH;
            int hover = -1, pressed = -1;
            int focus = -1;       // the item the keyboard is on (Tab, arrows; Enter or Space presses it)
            string focusKey;      // … kept by key across a rebuild
            bool keyboard;        // the focus ring shows: the keyboard was used since the mouse last moved
            bool picking;     // the list shows running programs to add, instead of the games
            readonly FlyoutTrail trail = new FlyoutTrail();   // where each open view was entered from: back returns there
            FlyoutTab tab = FlyoutTab.Live;       // the page it shows: Live, Games or Settings
            FlyoutTab lastTab = FlyoutTab.Live;   // the one last chosen while the app runs: it opens there when nothing is going on
            readonly Dictionary<FlyoutTab, double> tabScroll = new Dictionary<FlyoutTab, double>();   // each tab's place, while it is open
            string cardView;      // the view the status block's height belongs to
            int belowFrom;        // the first item below the status block: it moves with the block's height
            // While the wait before switching back counts down, Live's status line rolls with it.
            readonly System.Windows.Forms.Timer clock = new System.Windows.Forms.Timer { Interval = 250 };
            bool entered;         // a view was just entered: the keyboard starts on its first row
            bool returned;        // just back from a view: the keyboard is on the control that opened it
            bool repeat;          // the key being handled is held down (auto-repeat): it moves, but never presses
            int wheel;            // what the wheel moved that is less than a pixel, kept for its next turn
            string screenName;    // the monitor it opened on: it stays there until it closes
            readonly Spring open = Motion.Open(0);           // 0 hidden → 1 in place: it slides in from the taskbar
            readonly Spring scrollTo = Motion.Scroll(0);     // the wheel moves its target, the list follows
            readonly Spring thumb = Motion.Fade(0);          // the scroll bar: there while scrolling, gone 0.8 s later
            // The status card's height, in pixels. A hint or an error appearing makes it taller: the card grows, what is
            // below it moves down and the window grows with it, instead of all of it jumping.
            readonly Spring cardH = Motion.Shape(0, 0.5);
            Rectangle card;       // the status card at its full (target) height
            int contentH;         // the window's height once the card has its full height
            readonly System.Windows.Forms.Timer thumbRest = new System.Windows.Forms.Timer { Interval = 800 };
            readonly Func<double, bool> tick;
            bool closing;
            int closedAt = -100000;
            Point final, slide;   // where it sits when open, and the offset it slides in from

            public Flyout(TrayApp app)
            {
                this.app = app;
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                TopMost = true;
                StartPosition = FormStartPosition.Manual;
                BackColor = P.Surface;
                Text = "Display Switch";
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

                const TextFormatFlags Base = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter;
                TfLeft = Base | TextFormatFlags.Left;
                TfCenter = Base | TextFormatFlags.HorizontalCenter;
                TfWrap = TextFormatFlags.NoPadding | TextFormatFlags.WordBreak | TextFormatFlags.Left | TextFormatFlags.Top;

                tick = Tick;
                thumbRest.Tick += (s, e) => { thumbRest.Stop(); thumb.To(0); Motion.Run(tick); };
                clock.Tick += (s, e) =>
                {
                    if (!Visible || closing || tab != FlyoutTab.Live || Depth() > 0 || !app.sdrTimer.Enabled) return;
                    string t, u;
                    LiveStatus(Snapshot(), out t, out u);
                    if (t != shownTitle || u != shownSub) Do(Rebuild);
                };
                prefs = (s, e) =>
                {
                    if (e.Category == UserPreferenceCategory.Color || e.Category == UserPreferenceCategory.General || e.Category == UserPreferenceCategory.Accessibility)
                        ThemeChanged();
                };
                SystemEvents.UserPreferenceChanged += prefs;   // high contrast, and colours on systems that don't broadcast the messages below
                brightSave.Tick += (s, e) => Do(() => { brightSave.Stop(); CommitBrightness(); if (Visible && !closing) Rebuild(); });
            }

            readonly UserPreferenceChangedEventHandler prefs;

            /// <summary>The colours in use: Windows' light or dark mode, accent and high contrast (Palette.Load).</summary>
            static Palette P { get { return Palette.Current; } }

            /// <summary>
            /// Windows' mode, accent or high contrast changed: the colours are read again and, if it is open, it is built
            /// and painted again in them. Several messages come for one change; each is cheap.
            /// </summary>
            void ThemeChanged()
            {
                if (IsDisposed) return;
                Palette.Load();
                BackColor = P.Surface;
                if (Visible && !closing) Rebuild();
                Invalidate();
            }

            protected override void WndProc(ref Message m)
            {
                const int WM_SETTINGCHANGE = 0x1A, WM_SYSCOLORCHANGE = 0x15, WM_THEMECHANGED = 0x31A, WM_DWMCOLORIZATIONCOLORCHANGED = 0x320;
                base.WndProc(ref m);
                if (m.Msg == WM_DWMCOLORIZATIONCOLORCHANGED || m.Msg == WM_SYSCOLORCHANGE || m.Msg == WM_THEMECHANGED) ThemeChanged();
                else if (m.Msg == WM_SETTINGCHANGE && m.LParam != IntPtr.Zero)
                {
                    string area = null;
                    try { area = System.Runtime.InteropServices.Marshal.PtrToStringUni(m.LParam); } catch { }
                    if (area == "ImmersiveColorSet") ThemeChanged();   // light/dark mode or the accent colour
                }
            }

            // ---- open / close ----

            /// <summary>Tray icon clicked: open, or close if it is open.</summary>
            public void Toggle()
            {
                // The click that closed it: Deactivate comes first and starts the close, then the tray icon reports the click.
                if (Environment.TickCount - closedAt < 300) return;
                if (Visible && !closing) { Dismiss(false); return; }
                Open();
            }

            void Open()
            {
                picking = false;
                LeavePage();
                search = "";
                trail.Clear();
                tabScroll.Clear();
                entered = returned = false;
                wheel = 0;
                // Live while a game runs or something is held; otherwise the tab used last.
                bool held = app.paused || app.sdrTimer.Enabled || app.recorder != null;
                tab = OpeningTab(app.running.Count > 0, held, lastTab);
                Motion.Refresh();
                Palette.Load();
                BackColor = P.Surface;
                var at = Cursor.Position;
                screenName = Screen.FromPoint(at).DeviceName;
                float scale = Theme.Scale(at);
                if (fonts == null || scale != k)
                {
                    if (fonts != null) fonts.Dispose();
                    k = scale;
                    fonts = new Theme.Fonts(k);
                    rolls.Clear();   // measured in the old fonts
                }
                bool wasVisible = Visible;   // caught while closing: it turns round from where it is
                closing = false;
                focus = -1;
                focusKey = null;
                keyboard = false;
                ScrollHome();
                thumb.Snap(0);
                Rebuild();
                final = Corner(out slide);
                Motion.Opening(open);
                if (!wasVisible) open.Snap(0);
                open.To(1);
                Place();
                if (!wasVisible) Show();
                clock.Start();
                Invalidate();
                Update();
                Activate();
                Native.SetForegroundWindow(Handle);
                Motion.Run(tick);
            }

            /// <summary>Closes it: sliding out, or at once (now) when something else is about to take the screen.</summary>
            void Dismiss(bool now)
            {
                if (!Visible) return;
                tip.Hide(this);
                closedAt = Environment.TickCount;
                if (now || Motion.Reduced) { Finish(); return; }
                if (closing) return;
                closing = true;
                Motion.Closing(open);
                open.To(0);
                Motion.Run(tick);
            }

            void Finish()
            {
                Hide();
                closing = false;   // after Hide: the Deactivate it causes must still see the close as under way
                open.Snap(0);
                hover = pressed = -1;
                foreach (var a in anims.Values) { a.Hover.Snap(0); a.Press.Snap(0); }
                thumbRest.Stop();
                clock.Stop();
                dragging = false;
                Do(CommitBrightness);   // a level stepped with the keys just before it closed
                Trim();
            }

            /// <summary>
            /// The window follows the open spring: from the slide offset to its place. Its size and place change in one
            /// move, so a window that grows upwards doesn't first grow down and then jump. There is no fade: an ordinary
            /// window has no per-pixel alpha, and a whole-window alpha would make it layered again.
            /// </summary>
            void Place()
            {
                double o = Math.Max(0, Math.Min(1, open.Value));
                var p = new Point(final.X + (int)Math.Round(slide.X * (1 - o)), final.Y + (int)Math.Round(slide.Y * (1 - o)));
                var size = SizeFromClientSize(new Size(ClientSize.Width, Math.Max(1, contentH + Grow)));
                if (Location != p || Size != size) SetBounds(p.X, p.Y, size.Width, size.Height);
            }

            /// <summary>One frame of motion: the window, the list's scrolling and every item's hover, press and on/off.</summary>
            bool Tick(double dt)
            {
                if (!Visible) return false;
                bool moving = open.Step(dt) | scrollTo.Step(dt) | thumb.Step(dt) | cardH.Step(dt);
                foreach (var a in anims.Values)
                {
                    moving |= a.Hover.Step(dt) | a.Press.Step(dt) | a.On.Step(dt) | a.Busy.Step(dt) | a.Width.Step(dt) | a.Shake.Step(dt);
                    if (a.Busy.Value > 0.01) moving = true;   // a spinner keeps turning
                }
                foreach (var r in rolls.Values) moving |= r.Step(dt);
                Fit();
                if (closing && !open.Moving) { Finish(); return false; }
                int s = (int)Math.Round(scrollTo.Value);
                if (s != scroll)
                {
                    scroll = s;
                    SetHover(HitTest(PointToClient(Cursor.Position)));   // another row is under the mouse now
                }
                Invalidate();
                Update();   // this frame now: a paint waits for an empty queue, which motion doesn't leave
                return moving;
            }

            /// <summary>Where it sits when open: in the corner by the taskbar, 12 px in, like the system flyouts.</summary>
            Point Corner(out Point slideFrom)
            {
                var scr = Where();
                var size = SizeFromClientSize(new Size(ClientSize.Width, Math.Max(1, contentH + Grow)));
                return FlyoutCorner(scr.WorkingArea, scr.Bounds, size, S(12), S(24), out slideFrom);
            }

            /// <summary>
            /// The monitor it opened on, read afresh (the taskbar may have moved). Not the one under the mouse: moving the
            /// mouse to another monitor while it is open must not carry it there at this monitor's scale.
            /// </summary>
            Screen Where()
            {
                var all = Screen.AllScreens;
                var cursor = Screen.FromPoint(Cursor.Position);
                string name = FlyoutScreen(screenName, all.Select(s => s.DeviceName).ToList(), cursor.DeviceName);
                return all.FirstOrDefault(s => s.DeviceName == name) ?? cursor;
            }

            /// <summary>Something changed while it is open (a game started, a scan finished…).</summary>
            public void Sync()
            {
                if (Visible && !closing) Rebuild();
            }

            protected override CreateParams CreateParams
            {
                get
                {
                    var cp = base.CreateParams;
                    cp.ExStyle |= 0x80;            // WS_EX_TOOLWINDOW: no Alt+Tab entry
                    cp.ClassStyle |= 0x20000;      // CS_DROPSHADOW: the shadow Windows gives menus and flyouts
                    return cp;
                }
            }

            /// <summary>
            /// Asks Windows 11 for rounded corners (DWMWA_WINDOW_CORNER_PREFERENCE = DWMWCP_ROUND, 8 px), which come
            /// with Windows' own border. Windows 10 refuses: square corners, and OnPaint draws the border.
            /// </summary>
            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                rounded = false;
                try
                {
                    int round = 2;
                    rounded = Native.DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int)) == 0;
                }
                catch (DllNotFoundException) { }
                catch (EntryPointNotFoundException) { }
            }

            protected override void OnDeactivate(EventArgs e)
            {
                base.OnDeactivate(e);
                if (Visible && !closing) Dismiss(false);
            }

            /// <summary>Every key passes here first: notes whether it is held down, which Windows marks in bit 30.</summary>
            public override bool PreProcessMessage(ref Message msg)
            {
                if (msg.Msg == 0x100) repeat = (msg.LParam.ToInt64() & 0x40000000) != 0;   // WM_KEYDOWN: the key was already down
                return base.PreProcessMessage(ref msg);
            }

            protected override bool ProcessDialogKey(Keys keyData)
            {
                switch (keyData)
                {
                    // Esc goes back a view first, and closes from the games list. Held down, it stops at the first.
                    // Esc clears the search first, then goes back a view, and closes from a tab. Held down, it stops at the first.
                    case Keys.Escape:
                        if (repeat) return true;
                        if (Searching() && search.Length > 0) Do(() => Type(""));
                        else if (Depth() > 0) Do(GoBack); else Dismiss(false);
                        return true;
                    case Keys.Tab: MoveFocus(1); return true;
                    case Keys.Shift | Keys.Tab: MoveFocus(-1); return true;
                    // Up and Down go from row to row, past a row's own switch (Tab reaches that).
                    case Keys.Down: MoveFocus(1, true); return true;
                    case Keys.Up: MoveFocus(-1, true); return true;
                    // On a tab, the arrows switch tabs (and the focus goes along); on a slider they move it; Right on a
                    // game opens its page; elsewhere they move like Tab.
                    case Keys.Right: case Keys.Left:
                        int dir = keyData == Keys.Right ? 1 : -1;
                        if (OnTab()) Do(() => SwitchTab(NextTab(tab, dir), true));
                        else if (OnSlider()) Do(() => StepSlider(dir));
                        else if (dir > 0 && Focusable(focus) && items[focus].Right != null) { if (!repeat) Do(items[focus].Right); }
                        else MoveFocus(dir);
                        return true;
                    case Keys.PageUp: case Keys.PageDown:
                        if (OnSlider()) { Do(() => StepSlider(keyData == Keys.PageUp ? 10 : -10)); return true; }
                        break;
                    case Keys.End:
                        if (OnSlider()) { Do(() => StepSlider(100)); return true; }
                        break;
                    // Ctrl+Tab and Ctrl+Page Down: the next tab, from anywhere in it; with Shift, or Page Up, the one before.
                    case Keys.Control | Keys.Tab: case Keys.Control | Keys.PageDown: Do(() => SwitchTab(NextTab(tab, 1), true)); return true;
                    case Keys.Control | Keys.Shift | Keys.Tab: case Keys.Control | Keys.PageUp: Do(() => SwitchTab(NextTab(tab, -1), true)); return true;
                    case Keys.Home:
                        if (OnSlider()) Do(() => StepSlider(-100)); else MoveFocus(0);
                        return true;
                    case Keys.Enter:
                        if (OnSearch()) { if (!repeat) Do(OpenFirstMatch); }
                        else Press(false);
                        return true;
                    // Backspace takes a character off the search; elsewhere it goes back a view.
                    case Keys.Back:
                        if (Searching() && search.Length > 0) Do(() => Type(search.Substring(0, search.Length - 1)));
                        else if (!repeat && Depth() > 0) Do(GoBack);
                        return true;
                }
                return base.ProcessDialogKey(keyData);
            }

            protected override void OnKeyDown(KeyEventArgs e)
            {
                base.OnKeyDown(e);
                if (e.KeyCode == Keys.Space && !OnSearch()) { Press(true); e.Handled = true; }   // in the search box it is a space
            }

            /// <summary>Typing on the games list: the characters go into the search, wherever the keyboard is on it.</summary>
            protected override void OnKeyPress(KeyPressEventArgs e)
            {
                base.OnKeyPress(e);
                char c = e.KeyChar;
                if (!Searching() || char.IsControl(c) || (c == ' ' && !OnSearch())) return;
                e.Handled = true;
                Do(() => Type(search + c));
            }

            // ---- keyboard ----

            bool Focusable(int i) { return i >= 0 && i < items.Count && (items[i].Click != null || items[i].Listed) && !closing; }

            /// <summary>To the next (1) or previous (-1) control, round and round; 0: the first one. arrows: Up or Down, which pass a row's own switch by.</summary>
            void MoveFocus(int dir, bool arrows = false)
            {
                var order = Enumerable.Range(0, items.Count).Where(i => Focusable(i) && (!arrows || !items[i].ArrowSkip || i == focus)).ToList();
                if (order.Count == 0) return;
                int at = order.IndexOf(focus);
                int next = dir == 0 || at < 0 ? (dir < 0 ? order.Count - 1 : 0) : (at + dir + order.Count) % order.Count;
                SetFocus(order[next]);
            }

            void SetFocus(int i)
            {
                if (!Focusable(i)) return;
                focus = i;
                focusKey = items[i].Key;
                keyboard = true;
                ScrollInto(items[i]);
                Invalidate();
                int child = AccessibleItems().IndexOf(i);
                if (child >= 0) AccessibilityNotifyClients(AccessibleEvents.Focus, child);
            }

            /// <summary>
            /// Enter or Space. After the mouse took over the ring is hidden, so the first press only shows where the
            /// keyboard is; a held key presses once, not with every repeat (a switch would flip on and off).
            /// </summary>
            void Press(bool space)
            {
                switch (FlyoutKeyAction(keyboard, repeat, Focusable(focus)))
                {
                    case FlyoutKey.Ring: SetFocus(focus); break;
                    case FlyoutKey.Click:
                        var it = items[focus];
                        var click = space && it.Space != null ? it.Space : it.Click;   // null: a busy button, which has nothing to do yet
                        if (click != null) Do(click);
                        break;
                }
            }

            /// <summary>How many views deep it is: 0 a tab, 1 a game's page or the programs to add, 2 a list opened from the page.</summary>
            int Depth() { return picking ? 1 : page == null ? 0 : sub != null ? 2 : 1; }

            /// <summary>Opens a view from the control with this key: the list's place is kept for the way back.</summary>
            void Into(string from)
            {
                string stale;
                double was;
                trail.Pop(Depth() - 1, out stale, out was);   // a view a rebuild closed by itself (its game lost its resolution) leaves no step behind
                trail.Push(from, scrollTo.Target);
                ScrollHome();
                wheel = 0;
                entered = true;
            }

            /// <summary>After a view closed (one or more levels): the list back where it was, the focus on what opened it.</summary>
            void Out()
            {
                string from;
                double at;
                wheel = 0;
                if (!trail.Pop(Depth(), out from, out at)) { ScrollHome(); return; }
                scrollTo.Snap(at);   // Rebuild keeps it within the list
                scroll = (int)Math.Round(at);
                focusKey = from;
                returned = true;
            }

            /// <summary>The back button, Backspace and Esc: one view up.</summary>
            void GoBack()
            {
                if (sub != null) sub = null;
                else if (page != null) LeavePage();
                else picking = false;
                Out();
                Rebuild();
            }

            /// <summary>
            /// Runs a click. One that fails is written to error.log and the view is rebuilt from the app's real state, so a
            /// switch never shows a change that didn't happen – and the flyout stays usable.
            /// </summary>
            void Do(Action click)
            {
                try { click(); }
                catch (Exception e)
                {
                    Guard.Log("Flyout click", e);
                    try { if (Visible && !closing) Rebuild(); }
                    catch (Exception again) { Guard.Log("Flyout rebuild after a failed click", again); }
                }
            }

            /// <summary>Scrolls the list so a row the keyboard moved to is in view.</summary>
            void ScrollInto(Item it)
            {
                if (!it.Scrolls) return;
                double to = scrollTo.Target;
                if (it.Rect.Top - to < listClip.Top) to = it.Rect.Top - listClip.Top;
                else if (it.Rect.Bottom - to > listClip.Bottom) to = it.Rect.Bottom - listClip.Bottom;
                to = Math.Max(0, Math.Min(scrollMax, to));
                if (to == scrollTo.Target) return;
                scrollTo.To(to);
                Motion.Run(tick);
                if (Motion.Reduced) Tick(0);
            }

            /// <summary>
            /// Windows' focus visual round the item the keyboard is on: an outer 2 px ring in FocusOuter and an inner 1 px
            /// ring in FocusInner, so it shows on any background. Round a control, just outside it; on a row, just inside.
            /// Only after the keyboard was used (keyboard).
            /// </summary>
            void FocusRing(Graphics g)
            {
                if (!keyboard || !Focusable(focus)) return;
                var it = items[focus];
                if (it.Key == SearchKey) return;   // a text box shows the focus with its accent underline and caret instead
                int o = Math.Max(2, S(2)), i = Math.Max(1, S(1));
                float radius = it.Radius > 0 ? it.Radius : S(4);
                RectangleF edge = it.RingInside ? (RectangleF)OnScreen(it) : RectangleF.Inflate(OnScreen(it), o + i, o + i);
                if (!it.RingInside) radius += o + i;
                var state = g.Save();
                if (it.Scrolls) g.SetClip(ListNow(), CombineMode.Intersect);
                using (var outer = new Pen(P.FocusOuter, o))
                using (var po = Theme.Round(RectangleF.Inflate(edge, -o / 2f, -o / 2f), Math.Max(0, radius - o / 2f))) g.DrawPath(outer, po);
                using (var inner = new Pen(P.FocusInner, i))
                using (var pi = Theme.Round(RectangleF.Inflate(edge, -o - i / 2f, -o - i / 2f), Math.Max(0, radius - o - i / 2f))) g.DrawPath(inner, pi);
                g.Restore(state);
            }

            // ---- screen readers ----
            //
            // Everything here is painted, so Windows sees one window and nothing in it. The accessible object below gives
            // screen readers (Narrator, NVDA) each control instead: its name, its role, whether it is on, where it is, and a
            // default action that clicks it. Moving the keyboard focus tells them which one has it.

            /// <summary>The items a screen reader is told about, in order: every control and the status card.</summary>
            List<int> AccessibleItems()
            {
                var list = new List<int>();
                for (int i = 0; i < items.Count; i++)
                    if ((items[i].Click != null || items[i].Listed || items[i].Role == AccessibleRole.StaticText) && (items[i].Name ?? items[i].Tip) != null) list.Add(i);
                return list;
            }

            protected override AccessibleObject CreateAccessibilityInstance() { return new PanelAccess(this); }

            sealed class PanelAccess : ControlAccessibleObject
            {
                readonly Flyout f;
                public PanelAccess(Flyout f) : base(f) { this.f = f; }
                public override string Name { get { return "Display Switch"; } }
                public override AccessibleRole Role { get { return AccessibleRole.Dialog; } }
                public override int GetChildCount() { return f.AccessibleItems().Count; }

                public override AccessibleObject GetChild(int index)
                {
                    var list = f.AccessibleItems();
                    return index >= 0 && index < list.Count ? new ItemAccess(f, list[index], this) : null;
                }

                public override AccessibleObject GetFocused()
                {
                    return f.Focusable(f.focus) ? new ItemAccess(f, f.focus, this) : (AccessibleObject)this;
                }
            }

            sealed class ItemAccess : AccessibleObject
            {
                readonly Flyout f;
                readonly AccessibleObject parent;
                readonly string key;   // every rebuild replaces the items: the one this stands for is found by its key each time
                readonly Item first;   // the item as it was asked for: what it was called once it is gone

                public ItemAccess(Flyout f, int index, AccessibleObject parent)
                {
                    this.f = f;
                    this.parent = parent;
                    first = f.items[index];
                    key = first.Key;
                }

                int Index { get { return key != null ? f.items.FindIndex(it => it.Key == key) : f.items.IndexOf(first); } }
                Item Now { get { int i = Index; return i >= 0 ? f.items[i] : null; } }
                Item Said { get { return Now ?? first; } }
                public override string Name { get { return Said.Name ?? Said.Tip; } }
                public override string Description { get { return Said.Name != null ? Said.Tip : null; } }
                public override string Value { get { return Said.Value; } set { } }
                public override AccessibleRole Role { get { return Said.Role; } }
                public override AccessibleObject Parent { get { return parent; } }

                public override AccessibleStates State
                {
                    get
                    {
                        int i = Index;
                        if (i < 0) return AccessibleStates.Unavailable;
                        var item = f.items[i];
                        var st = Marks(item);
                        if (f.Focusable(i)) st |= AccessibleStates.Focusable;
                        if (f.focus == i) st |= AccessibleStates.Focused;
                        if (item.Scrolls && !f.ListNow().IntersectsWith(f.OnScreen(item))) st |= AccessibleStates.Offscreen;
                        return st;
                    }
                }

                public override Rectangle Bounds { get { var item = Now; return item != null ? f.RectangleToScreen(f.OnScreen(item)) : Rectangle.Empty; } }

                public override string DefaultAction
                {
                    get { var item = Now; return item == null || item.Click == null ? null : item.Role == AccessibleRole.CheckButton ? "Toggle" : item.Role == AccessibleRole.PageTab ? "Switch" : "Press"; }
                }

                public override void DoDefaultAction()
                {
                    var item = Now;
                    var click = item != null ? item.Click : null;
                    if (click != null) f.BeginInvoke((Action)(() => f.Do(click)));   // not inside the screen reader's call
                }

                public override void Select(AccessibleSelection flags)
                {
                    int i = Index;
                    if (i >= 0 && (flags & AccessibleSelection.TakeFocus) != 0) f.SetFocus(i);
                    var item = Now;   // a tab selected by a screen reader: shown
                    if (item != null && item.Role == AccessibleRole.PageTab && (flags & AccessibleSelection.TakeSelection) != 0 && item.Click != null)
                    {
                        var click = item.Click;
                        f.BeginInvoke((Action)(() => f.Do(click)));
                    }
                }
            }

            /// <summary>The state a screen reader hears that comes from the item itself: checked, busy, or unavailable while it can't be clicked.</summary>
            static AccessibleStates Marks(Item it)
            {
                var st = AccessibleStates.None;
                if (it.Click == null && it.Role != AccessibleRole.StaticText) st |= AccessibleStates.Unavailable;   // the update button while busy
                if (it.Role == AccessibleRole.PageTab) st |= AccessibleStates.Selectable | (it.Selected ? AccessibleStates.Selected : AccessibleStates.None);
                else if (it.Selected) st |= AccessibleStates.Checked;
                if (it.Busy) st |= AccessibleStates.Busy;
                if (it.Expands) st |= it.Expanded ? AccessibleStates.Expanded : AccessibleStates.Collapsed;
                return st;
            }

            /// <summary>
            /// After a rebuild: tells screen readers what changed in place – a switch flipped on the focused control, a
            /// new label, the update's next step – which they don't notice by themselves in a window they see as painted.
            /// </summary>
            void Tell(Dictionary<string, Item> before)
            {
                if (!Visible || closing || !IsHandleCreated) return;
                var list = AccessibleItems();
                for (int c = 0; c < list.Count; c++)
                {
                    var it = items[list[c]];
                    Item was;
                    if (it.Key == null || !before.TryGetValue(it.Key, out was)) continue;
                    if (list[c] != focus && it.Key != "update") continue;   // what a screen reader follows: the focus, and the update's progress
                    if ((was.Name ?? was.Tip) != (it.Name ?? it.Tip)) AccessibilityNotifyClients(AccessibleEvents.NameChange, c);
                    if (Marks(was) != Marks(it)) AccessibilityNotifyClients(AccessibleEvents.StateChange, c);
                    if (was.Value != it.Value) AccessibilityNotifyClients(AccessibleEvents.ValueChange, c);
                }
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    SystemEvents.UserPreferenceChanged -= prefs;   // a static event: it would keep this alive
                    thumbRest.Dispose();
                    clock.Dispose();
                    brightSave.Dispose();
                    tip.Dispose();
                    if (fonts != null) fonts.Dispose();
                }
                base.Dispose(disposing);
            }

            // ---- layout: builds the item list from the app's state ----

            int S(double v) { return (int)Math.Round(v * k); }

            void ScrollHome()
            {
                scrollTo.Snap(0);
                scroll = 0;
            }

            void Rebuild()
            {
                var before = new Dictionary<string, Item>();   // for Tell: what a screen reader was told last
                foreach (var it in items) if (it.Key != null) before[it.Key] = it;
                items.Clear();
                foreach (var r in rolls.Values) r.Seen = false;
                Settle();
                if (Depth() > 0) tab = FlyoutTab.Games;   // the games' own views live under their tab
                int W = S(372), M = S(12);
                int y = TopBar(W, M);
                card = new Rectangle(M, y, W - 2 * M, 0);   // only Live has a status block that grows
                belowFrom = items.Count;
                if (tab == FlyoutTab.Live) y = BuildLive(W, M, y);
                else if (tab == FlyoutTab.Settings) y = BuildSettings(W, M, y);
                else y = BuildGames(W, M, y);

                for (int i = belowFrom; i < items.Count; i++) items[i].Below = true;
                contentH = y;
                string view = tab + ":" + Depth();   // another view starts at its own height: nothing to grow from
                if (Visible && !closing && view == cardView) cardH.To(card.Height);
                else cardH.Snap(card.Height);
                cardView = view;

                // The springs follow the new items: kept by key, new ones start where they belong, old ones go.
                foreach (var a in anims.Values) a.Seen = false;
                foreach (var it in items)
                {
                    if (it.Key == null) continue;
                    Anim a;
                    bool fresh = !anims.TryGetValue(it.Key, out a);
                    if (fresh) anims[it.Key] = a = new Anim();
                    a.Seen = true;
                    if (Visible && !fresh)
                    {
                        a.On.To(it.On ? 1 : 0);
                        a.Busy.To(it.Busy ? 1 : 0);
                        a.Width.To(it.Width);
                        if (it.Shake && !a.Shaken && !Motion.Reduced) a.Shake.Velocity = 520 * k;   // the Shape spring brings it back with a wobble
                    }
                    else
                    {
                        a.On.Snap(it.On ? 1 : 0);
                        a.Busy.Snap(it.Busy ? 1 : 0);
                        a.Width.Snap(it.Width);
                    }
                    a.Shaken = it.Shake;
                }
                foreach (string gone in anims.Where(kv => !kv.Value.Seen).Select(kv => kv.Key).ToList()) anims.Remove(gone);
                foreach (string gone in rolls.Where(kv => !kv.Value.Seen).Select(kv => kv.Key).ToList()) rolls.Remove(gone);

                ClientSize = new Size(W, contentH + Grow);
                pressed = -1;
                focus = focusKey == null ? -1 : items.FindIndex(it => it.Key == focusKey);
                if (!Focusable(focus)) focus = -1;
                bool into = entered, outOf = returned;
                entered = returned = false;
                int firstRow = items.FindIndex(it => it.Scrolls && it.Click != null);
                if (into && keyboard && firstRow >= 0) SetFocus(firstRow);       // a view opened: the keyboard starts on its first row
                else if (outOf && keyboard && focus >= 0) SetFocus(focus);        // back: on the control that opened it, scrolled into view
                else if (focus < 0 && focusKey != null && keyboard) MoveFocus(0);   // its control went: start at the top
                Tell(before);
                if (Visible)
                {
                    final = Corner(out slide);
                    Place();
                    hover = HitTest(PointToClient(Cursor.Position));
                    tip.SetToolTip(this, hover >= 0 ? items[hover].Tip : null);
                }
                else hover = -1;
                Aim();
            }

            // ---- the tabs ----

            static string TabKey(FlyoutTab t) { return "tab:" + t.ToString().ToLowerInvariant(); }

            /// <summary>
            /// To another tab (a click, Ctrl+Tab, an arrow on a tab). A games view that is open closes; each tab keeps
            /// where it was scrolled while the flyout stays open. focusTab: the keyboard moved, so the focus goes to it.
            /// </summary>
            void SwitchTab(FlyoutTab t, bool focusTab)
            {
                if (Depth() == 0) tabScroll[tab] = scrollTo.Target;
                picking = false;
                LeavePage();
                trail.Clear();
                entered = returned = false;
                wheel = 0;
                tab = lastTab = t;
                double at;
                scrollTo.Snap(tabScroll.TryGetValue(t, out at) ? at : 0);   // Rebuild keeps it within the new list
                scroll = (int)Math.Round(scrollTo.Value);
                if (focusTab || keyboard) focusKey = TabKey(t);
                Rebuild();
                if (focusTab)
                {
                    int i = items.FindIndex(it => it.Key == focusKey);
                    if (i >= 0) SetFocus(i);
                }
            }

            /// <summary>The keyboard is on one of the tabs.</summary>
            bool OnTab() { return Focusable(focus) && items[focus].Role == AccessibleRole.PageTab; }

            /// <summary>
            /// The top row: the three tabs (WinUI's SelectorBar: text, the chosen one semibold over a 16 × 3 accent pill)
            /// and the tab's own buttons on the right – or, in one of the games' views, its back button and title.
            /// Returns where the content below it starts.
            /// </summary>
            int TopBar(int W, int M)
            {
                Color sbg = P.Surface;
                if (Depth() > 0) return ViewBar(W, M);
                int x = M;
                foreach (var t in FlyoutTabs)
                {
                    var tt = t;
                    string name = t.ToString();
                    bool sel = t == tab;
                    int w = TextRenderer.MeasureText(name, fonts.Strong, Size.Empty, TfLeft).Width + S(20);   // sized for semibold: it doesn't move when chosen
                    var r = new Rectangle(x, S(6), w, S(36));
                    x += w + S(2);
                    items.Add(new Item { Rect = r, Key = TabKey(t), Name = name, Role = AccessibleRole.PageTab, Selected = sel, On = sel,
                        Click = () => SwitchTab(tt, false), Paint = (g, rc, lk) =>
                    {
                        if (lk.Hover > 0.01)
                        {
                            if (P.HighContrast) { if (lk.Hover > 0.5) Outline(g, rc, S(4), P.HoverStroke); }
                            else Theme.Fill(g, rc, S(4), Theme.Fade(Theme.Mix(P.SubtleHover, P.SubtlePress, lk.Press), lk.Hover));
                        }
                        Color fg = sel ? P.Ink(P.Text1, sbg) : Theme.Mix(P.Ink(P.Text2, sbg), P.Ink(P.Text1, sbg), lk.Hover * 0.6);
                        Draw(g, name, sel ? fonts.Strong : fonts.Body, fg, new Rectangle(rc.X, rc.Y + S(6), rc.Width, S(20)), TfCenter);
                        if (lk.On > 0.01)   // the pill grows in from the middle as a tab is chosen
                        {
                            float pw = S(16) * (float)Math.Min(1, lk.On);
                            Theme.Fill(g, new RectangleF(rc.X + (rc.Width - pw) / 2f, rc.Bottom - S(2) - S(3), pw, S(3)), 1.5f * k, P.Accent);
                        }
                    } });
                }
                // The tab's own buttons, at the right.
                var right = new Rectangle(W - M - S(32), S(8), S(32), S(32));
                if (tab == FlyoutTab.Live)
                {
                    bool paused = app.paused;
                    bool keyOk = app.hotkey != null && app.hotkey.Registered;
                    items.Add(new Item { Rect = right, Key = "pause", Name = paused ? "Resume switching" : "Pause switching",
                        Tip = (paused ? "Switching is paused. " : "Stops all switching until you resume. ") + HotKey.Text + (keyOk || sample != null ? " does this from anywhere." : " is taken by another program."),
                        Click = () => { app.SetPaused(!app.paused); Rebuild(); },
                        Paint = (g, rc, lk) => SubtleButton(g, rc, lk, paused ? "" : "", fonts.GlyphSmall, Theme.Mix(P.Ink(P.Text2, sbg), P.Ink(P.Text1, sbg), lk.Hover), sbg) });
                }
                else if (tab == FlyoutTab.Games)
                {
                    // The two things to do with the list: scan again, add something by hand.
                    var look = new Rectangle(right.X - S(4) - S(32), right.Y, S(32), S(32));
                    bool scanning = app.scanning;
                    items.Add(new Item { Rect = look, Key = "look", Tip = scanning ? "Looking for games…" : "Look for games", Busy = scanning, Listed = true,
                        Click = scanning ? null : (Action)(() => { app.ScanInBackground(true); Rebuild(); }),
                        Paint = (g, rc, lk) =>
                        {
                            // While it looks, the symbol fades out and a spinner takes its place.
                            Color fg = P.Ink(P.Text2, sbg);
                            SubtleButton(g, rc, lk, "", fonts.GlyphSmall, Theme.Mix(sbg, fg, 1 - lk.Busy), sbg);
                            Theme.Spinner(g, new PointF(rc.X + rc.Width / 2f, rc.Y + rc.Height / 2f), S(13), Math.Max(1.5f, 1.75f * k), fg, lk.Busy);
                        } });
                    items.Add(new Item { Rect = right, Key = "add", Tip = "Add a program", Click = () => { picking = true; Into("add"); Rebuild(); },
                        Paint = (g, rc, lk) => SubtleButton(g, rc, lk, "", fonts.GlyphSmall, P.Ink(P.Text2, sbg), sbg) });
                }
                return S(48);
            }

            /// <summary>
            /// The scrolling part of a tab: items from 'from' on scroll inside clip, which is as tall as fits (shown) of
            /// content pixels; a wheel notch moves by step.
            /// </summary>
            void Scroller(int from, Rectangle clip, int content, int step)
            {
                for (int i = from; i < items.Count; i++) items[i].Scrolls = true;
                listClip = clip;
                rowH = step;
                scrollMax = Math.Max(0, content - clip.Height);
                if (scrollTo.Target > scrollMax) scrollTo.Snap(scrollMax);
                scroll = (int)Math.Round(scrollTo.Value);
            }

            /// <summary>The footer bar, the whole width of the window under a divider. Returns its (opaque) colour.</summary>
            Color Footer(int W, int y, int h)
            {
                Color fbg = P.HighContrast ? P.Surface : P.FootSolid;
                items.Add(new Item { Rect = new Rectangle(0, y, W, h), Paint = (g, rc, lk) =>
                {
                    using (var b = new SolidBrush(fbg)) g.FillRectangle(b, rc);
                    using (var b = new SolidBrush(P.Divider)) g.FillRectangle(b, rc.X, rc.Y, rc.Width, DividerWidth());
                } });
                return fbg;
            }

            /// <summary>A section's header in the Settings tab (Body Strong), as Windows' Settings has them.</summary>
            void Section(string text, int M, int y, int h)
            {
                Color sbg = P.Surface;
                items.Add(new Item { Rect = new Rectangle(M + S(4), y, S(300), h), Role = AccessibleRole.StaticText, Name = text,
                    Paint = (g, rc, lk) => Draw(g, text, fonts.Strong, P.Ink(P.Text1, sbg), new Rectangle(rc.X, rc.Bottom - S(24), rc.Width, S(20)), TfLeft) });
            }

            // ---- Live: what is switched right now ----

            string shownTitle, shownSub;   // the status Live shows, for the countdown's clock to see when it changes

            /// <summary>
            /// The Live tab: the running game and when things go back, then the layers in effect as one grouped card
            /// (HDR, SDR brightness, competitive area, stretch, NVIDIA scaling), then what holds switching back: the
            /// pause and SDR while recording. A new version waits in the footer bar.
            /// </summary>
            int BuildLive(int W, int M, int y)
            {
                int cw = W - 2 * M, G = S(8), rowCH = S(52);
                var s = Snapshot();
                string title, sub;
                LiveStatus(s, out title, out sub);
                shownTitle = title;
                shownSub = sub;
                string notice = app.notice;   // the latest setup hint, until things settle
                int textW = cw - S(8);
                int noticeH = notice == null ? 0 : TextRenderer.MeasureText(notice, fonts.Caption, new Size(textW, 2000), TfWrap).Height;
                var st = new Rectangle(M, y, cw, S(28) + S(16) + (notice != null ? S(6) + noticeH : 0));
                RollText titleRoll = Roll("title", title, fonts.Title), subRoll = Roll("sub", sub, fonts.Caption);
                items.Add(new Item { Rect = st, Key = "status", InCard = true, Grows = true, Role = AccessibleRole.StaticText,
                    Name = title + ". " + sub + (notice != null ? ". " + notice : ""), Paint = (g, rc, lk) =>
                {
                    Color bg = P.Surface;
                    DrawRoll(g, titleRoll, fonts.Title, P.Ink(P.Text1, bg), bg, new Rectangle(rc.X + S(4), rc.Y, textW, S(28)), -1);
                    DrawRoll(g, subRoll, fonts.Caption, P.Ink(P.Text2, bg), bg, new Rectangle(rc.X + S(4), rc.Y + S(28), textW, S(16)), -1);
                    if (notice != null) Draw(g, notice, fonts.Caption, P.Ink(P.Text2, bg), new Rectangle(rc.X + S(4), rc.Y + S(50), textW, noticeH), TfWrap);
                } });
                card = st;
                y = st.Bottom + G;
                belowFrom = items.Count;

                // The body scrolls when the panel wouldn't fit above the taskbar.
                int top = y, from = items.Count;
                var layers = LiveLayers(s);
                Group(new Rectangle(M, y, cw, layers.Count * rowCH), layers.Count);
                for (int i = 0; i < layers.Count; i++) LayerRow(new Rectangle(M, y + i * rowCH, cw, rowCH), layers[i]);
                y += layers.Count * rowCH + G;
                if (s.Paused) { PausedBar(new Rectangle(M, y, cw, rowCH)); y += rowCH + G; }
                Group(new Rectangle(M, y, cw, rowCH), 1);
                string rec = s.Recorder;
                string recDesc = rec != null ? rec + " is running · HDR held off" : s.RecordingOn ? "Holds HDR off while OBS, Streamlabs… run" : "OBS, Streamlabs, XSplit, vMix…";
                CardToggle(new Rectangle(M, y, cw, rowCH), "recording", "rec", "SDR while recording", recDesc, app.sdrWhileRecording, true, true,
                    () => { app.SetSdrWhileRecording(!app.sdrWhileRecording); Rebuild(); }, rec != null);
                y += rowCH;
                int content = y - top + S(4);

                // A new version: a footer bar with what it is (or why it waits, or why it failed) and the button.
                int footH = 0, btnW = 0, noteW = 0, noteH = 0;
                string note = null;
                if (app.update != null)
                {
                    note = app.UpdateNote ?? "Version " + app.update.VersionText + " is ready";
                    btnW = UpdateWidth();
                    noteW = cw - S(28) - S(12) - btnW;
                    noteH = TextRenderer.MeasureText(note, fonts.Caption, new Size(noteW, 2000), TfWrap).Height;
                    footH = Math.Max(S(52), noteH + S(24));
                }
                int gap = footH > 0 ? G : M;
                int shown = FlyoutBody(Where().WorkingArea.Height, S(12), top + gap + footH, content, rowCH);
                Scroller(from, new Rectangle(0, top, W, shown), content, rowCH);
                y = top + shown + gap;
                if (footH > 0)
                {
                    Color fbg = Footer(W, y, footH);
                    string noteText = note;
                    items.Add(new Item { Rect = new Rectangle(M, y, noteW + S(28), footH), Paint = (g, rc, lk) =>
                    {
                        LineIcon(g, "update", new Rectangle(rc.X, rc.Y + (rc.Height - S(20)) / 2, S(20), S(20)), P.Ink(P.Text2, fbg));
                        Draw(g, noteText, fonts.Caption, P.Ink(P.Text2, fbg), new Rectangle(rc.X + S(28), rc.Y + (rc.Height - noteH) / 2, noteW, noteH), TfWrap);
                    } });
                    UpdateButton(new Rectangle(W - M - btnW, y + (footH - S(32)) / 2, btnW, S(32)), fbg);
                    y += footH;
                }
                return y;
            }

            /// <summary>The app's state now, for the Live tab (the render harness's made-up state instead of Windows').</summary>
            LiveState Snapshot()
            {
                var s = new LiveState { Paused = app.paused, Delay = app.sdrDelay, RecordingOn = app.sdrWhileRecording };
                bool anyOn;
                if (sample != null) { anyOn = sample.HdrOn; s.HdrSupported = sample.HdrSupported; }
                else
                {
                    var displays = Hdr.All();
                    anyOn = displays.Any(d => d.On);
                    s.HdrSupported = displays.Any(d => d.Supported);
                }
                s.HdrOurs = app.owned.Count > 0;
                s.HdrElsewhere = anyOn && !s.HdrOurs;
                // The game: one with HDR first, else one with a resolution.
                var names = app.running.Values.Select(r => r.Name).Distinct().ToList();
                string game = names.FirstOrDefault(n => app.games.Contains(n)) ?? names.FirstOrDefault(n => app.profiles.ContainsKey(n)) ?? names.FirstOrDefault();
                var res = app.res;
                if (game != null)
                {
                    s.Game = app.DisplayName(game);
                    s.GameHdr = app.games.Contains(game);
                    s.GameArea = app.profiles.ContainsKey(game);
                    s.GameStretch = app.StretchOf(game) != null;
                    s.GameBrightness = app.brightness.ContainsKey(game);
                }
                if (sample != null ? sample.Grace >= 0 : app.sdrTimer.Enabled)
                    s.GraceLeft = sample != null ? sample.Grace : Math.Max(1, (int)Math.Ceiling((app.sdrDelay * 1000 - app.grace.ElapsedMilliseconds) / 1000.0));
                s.BackTo = s.HdrOurs ? "SDR" : res != null && res.Back != null ? res.Back.Text : null;
                if (app.brightBack >= 0 && app.brightSet >= 0) s.Brightness = Hdr.PercentOf(app.brightSet);
                s.BrightnessLeft = app.brightLeft != null;
                if (res != null)
                {
                    s.Area = res.Area ?? res.Mode;
                    s.Stretch = res.Area != null ? res.Mode : null;
                    s.StretchLost = sample == null && app.StretchLost();   // asks the driver
                    s.ScalingTaken = res.ScalingBack >= 0;
                    Resolution.Mode full;
                    double inches;
                    if (sample != null) { full = sample.Full; inches = sample.Inches; }
                    else
                    {
                        var pic = Resolution.Read(res.Gdi);   // the panel's own size is the target of the display path
                        full = pic != null ? new Resolution.Mode { Width = pic.TargetW, Height = pic.TargetH } : res.Back;
                        inches = Resolution.Inches(res.Gdi);
                    }
                    s.AreaDetail = full != null ? SizeText(s.Area, full, inches) : null;
                }
                s.ScalingStray = app.strayScaling >= 0;
                s.Recorder = app.recorder != null ? app.RecorderName(app.recorder) : null;
                if (sample != null) s.Screen = sample.ScreenName;
                else
                {
                    // The monitor in play: the one with HDR on for the game, else the one with its resolution.
                    try
                    {
                        Hdr.Display d = app.owned.FirstOrDefault();
                        if (d == null && res != null) d = Hdr.All().FirstOrDefault(x => x.Gdi == res.Gdi);
                        if (d != null) s.Screen = Hdr.FriendlyName(d);
                    }
                    catch (Exception e) { Guard.Log("Reading the monitor's name", e); }
                }
                return s;
            }

            /// <summary>
            /// One layer of the stack: its icon (the accent colour while in effect, amber for HDR), title and what it does,
            /// and its value on the right – for HDR in effect, the amber ON marker. Not a control: a screen reader reads
            /// it as text, its name and value.
            /// </summary>
            void LayerRow(Rectangle r, LiveLayer l)
            {
                bool hdr = l.Key == "hdr", marker = hdr && l.Active;
                string value = l.Value;
                items.Add(new Item { Rect = r, Key = "layer:" + l.Key, On = l.Active, Role = AccessibleRole.StaticText,
                    Name = l.Title + ", " + (marker ? "on" : value == NoValue ? "nothing" : value), Tip = l.Desc, Paint = (g, rc, lk) =>
                {
                    Color bg = P.CardSolid;
                    int vw = marker ? MarkW() : TextRenderer.MeasureText(value, fonts.Caption, Size.Empty, TfLeft).Width + S(2);
                    int ix = rc.X + S(14), tx = ix + S(20) + S(12), tw = rc.Right - S(12) - vw - S(12) - tx;
                    double on = Math.Max(0, Math.Min(1, lk.On));
                    Color ic = Theme.Mix(P.Ink(P.Text3, bg), hdr && !P.HighContrast ? P.HdrDot : P.Accent, on);
                    LineIcon(g, l.Key, new Rectangle(ix, rc.Y + (rc.Height - S(20)) / 2, S(20), S(20)), ic);
                    int ty = rc.Y + (rc.Height - S(36)) / 2;
                    Draw(g, l.Title, fonts.Body, Theme.Mix(P.Ink(P.Text2, bg), P.Ink(P.Text1, bg), on), new Rectangle(tx, ty, tw, S(20)), TfLeft);
                    Draw(g, l.Desc, fonts.Caption, P.Ink(P.Text2, bg), new Rectangle(tx, ty + S(20), tw, S(16)), TfLeft);
                    var vr = new Rectangle(rc.Right - S(12) - vw, rc.Y, vw, rc.Height);
                    if (marker) HdrMark(g, new Rectangle(vr.X, rc.Y + (rc.Height - S(20)) / 2, vw, S(20)), bg);
                    else Draw(g, value, fonts.Caption, P.Ink(value == NoValue ? P.Text3 : P.Text2, bg), vr, TfLeft | TextFormatFlags.Right);
                } });
            }

            /// <summary>
            /// Paused: a caution bar (WinUI's InfoBar, warning) under the layers, with Resume. Ctrl+Alt+H does the same.
            /// </summary>
            void PausedBar(Rectangle r)
            {
                bool light = P.Mode == Palette.Kind.Light;
                Color fill = P.HighContrast ? P.Surface : light ? Color.FromArgb(0xFF, 0xF4, 0xCE) : Color.FromArgb(0x43, 0x35, 0x19);   // SystemFillColorCautionBackground
                int resumeW = TextRenderer.MeasureText("Resume", fonts.Body, Size.Empty, TfLeft).Width + S(24);
                items.Add(new Item { Rect = r, Key = "paused", Role = AccessibleRole.StaticText, Name = "Paused · nothing switches", Tip = HotKey.Text + " resumes, from anywhere",
                    Paint = (g, rc, lk) =>
                {
                    Theme.Fill(g, rc, S(4), fill);
                    Outline(g, rc, S(4), P.HighContrast ? P.Text1 : P.CardStroke);
                    Color ic = P.HighContrast ? P.Text1 : P.Caution;
                    int ix = rc.X + S(14), tx = ix + S(20) + S(12), tw = rc.Right - S(12) - resumeW - S(12) - tx;
                    LineIcon(g, "pause", new Rectangle(ix, rc.Y + (rc.Height - S(20)) / 2, S(20), S(20)), ic);
                    int ty = rc.Y + (rc.Height - S(36)) / 2;
                    Draw(g, "Paused · nothing switches", fonts.Strong, P.Ink(P.Text1, fill), new Rectangle(tx, ty, tw, S(20)), TfLeft);
                    Draw(g, HotKey.Text + " resumes, from anywhere", fonts.Caption, P.Ink(P.Text2, fill), new Rectangle(tx, ty + S(20), tw, S(16)), TfLeft);
                } });
                var br = new Rectangle(r.Right - S(12) - resumeW, r.Y + (r.Height - S(32)) / 2, resumeW, S(32));
                items.Add(new Item { Rect = br, Key = "resume", Name = "Resume switching", Click = () => { app.SetPaused(false); Rebuild(); },
                    Paint = (g, rc, lk) => StandardButton(g, rc, lk, fill, null, "Resume") });
            }

            // ---- Settings: everything that isn't per game ----

            /// <summary>
            /// The Settings tab, in two groups as Windows' Settings has them. Switching: the wait before switching back,
            /// SDR while recording, the pause shortcut. App: start with Windows, the island, updates. The footer bar says
            /// where the files are, opens that folder, and exits.
            /// </summary>
            int BuildSettings(int W, int M, int y)
            {
                int cw = W - 2 * M, rowCH = S(52), secH = S(36);
                int top = y, from = items.Count;
                y -= S(4);
                Section("Switching", M, y, secH);
                y += secH;
                Group(new Rectangle(M, y, cw, 3 * rowCH), 3);
                // How long to wait before switching back: a segmented control.
                int segW = S(140), segH = S(28);
                var wait = new Rectangle(M, y, cw, rowCH);
                items.Add(new Item { Rect = wait, Paint = (g, rc, lk) => RowText(g, rc, "clock", "Switch back after", "After a game closes", 0, S(12) + segW + S(12)) });
                var seg = new Rectangle(wait.Right - S(12) - segW, wait.Y + (rowCH - segH) / 2, segW, segH);
                items.Add(new Item { Rect = seg, Paint = (g, rc, lk) =>
                {
                    Theme.Fill(g, rc, S(4), P.ControlFill2);
                    Outline(g, rc, S(4), P.ControlStroke);
                } });
                int ns = DelayChoices.Length, sw = (segW - S(4)) / ns, sx = seg.X + S(2);
                foreach (int v in DelayChoices)
                {
                    bool chosenDelay = app.sdrDelay == v;
                    string text = v + " s";
                    var sr = new Rectangle(sx, seg.Y + S(2), v == DelayChoices[ns - 1] ? seg.Right - S(2) - sx : sw, segH - S(4));
                    sx += sw;
                    items.Add(new Item { Rect = sr, Key = "delay:" + v, On = chosenDelay, Role = AccessibleRole.RadioButton, Selected = chosenDelay, Radius = S(3),
                        Name = "Switch back after " + v + " seconds", Tip = "The wait after a game closes, for launchers that restart it.",
                        Click = () => { app.SetDelay(v); Rebuild(); }, Paint = (g, rc, lk) => Segment(g, rc, lk, text) });
                }
                CardToggle(new Rectangle(M, y + rowCH, cw, rowCH), "recording", "rec", "SDR while recording", "OBS, Streamlabs, XSplit, vMix…", app.sdrWhileRecording, false, false,
                    () => { app.SetSdrWhileRecording(!app.sdrWhileRecording); Rebuild(); }, false);
                bool keyOk = app.hotkey != null && app.hotkey.Registered || sample != null;
                string keys = HotKey.Text.Replace("+", " + ");
                int keysW = TextRenderer.MeasureText(keys, fonts.Caption, Size.Empty, TfLeft).Width + S(2);
                string keyDesc = keyOk ? "Stops all switching, from anywhere" : "Taken by another program";
                items.Add(new Item { Rect = new Rectangle(M, y + 2 * rowCH, cw, rowCH), Key = "shortcut", Role = AccessibleRole.StaticText, Name = "Pause shortcut, " + HotKey.Text, Tip = keyDesc,
                    Paint = (g, rc, lk) =>
                {
                    RowText(g, rc, "pause", "Pause shortcut", keyDesc, 0, S(12) + keysW + S(12));
                    Draw(g, keys, fonts.Caption, P.Ink(P.Text2, P.CardSolid), new Rectangle(rc.Right - S(12) - keysW, rc.Y, keysW, rc.Height), TfLeft | TextFormatFlags.Right);
                } });
                y += 3 * rowCH;

                Section("App", M, y, secH);
                y += secH;
                Group(new Rectangle(M, y, cw, 3 * rowCH), 3);
                CardToggle(new Rectangle(M, y, cw, rowCH), "startup", "power", "Start with Windows", "Runs in the tray", IsStartupNow(), true, false,
                    () => { SetStartup(!IsStartup()); Rebuild(); }, false);
                CardToggle(new Rectangle(M, y + rowCH, cw, rowCH), "island", "island", "Island", "A pill at the top of the screen", app.islandOn, false, false,
                    () => { app.SetIsland(!app.islandOn); Rebuild(); }, false);
                // Updates: the version running, and the button when there is a newer one.
                var ur = new Rectangle(M, y + 2 * rowCH, cw, rowCH);
                string version = Updater.CurrentText;
                int verW = TextRenderer.MeasureText(version, fonts.Caption, Size.Empty, TfLeft).Width + S(2);
                bool hasUpdate = app.update != null;
                int btnW = hasUpdate ? UpdateWidth() : 0, btnNow = hasUpdate ? UpdateFit() : 0;
                string upDesc = !hasUpdate ? "Signed releases, checked daily" : app.UpdateNote ?? "Version " + app.update.VersionText + " is ready";
                int rightW = S(12) + verW + (hasUpdate ? S(12) + btnNow : 0) + S(12);
                items.Add(new Item { Rect = ur, Key = "updates", Role = AccessibleRole.StaticText, Name = "Updates, version " + version, Tip = upDesc, Paint = (g, rc, lk) =>
                {
                    RowText(g, rc, "update", "Updates", upDesc, 0, rightW);
                    int vx = rc.Right - rightW + S(12);
                    Draw(g, version, fonts.Caption, P.Ink(P.Text2, P.CardSolid), new Rectangle(vx, rc.Y, verW, rc.Height), TfLeft | TextFormatFlags.Right);
                } });
                if (hasUpdate) UpdateButton(new Rectangle(ur.Right - S(12) - btnW, ur.Y + (rowCH - S(32)) / 2, btnW, S(32)), P.CardSolid);
                y += 3 * rowCH;
                int content = y - top + S(4);

                // The footer bar: where the files are, open that folder, exit.
                int barH = S(52);
                int shown = FlyoutBody(Where().WorkingArea.Height, S(12), top + S(8) + barH, content, rowCH);
                Scroller(from, new Rectangle(0, top, W, shown), content, rowCH);
                y = top + shown + S(8);
                Color fbg = Footer(W, y, barH);
                int exitW = TextRenderer.MeasureText("Exit", fonts.Body, Size.Empty, TfLeft).Width + S(24);
                int openW = TextRenderer.MeasureText("Open folder", fonts.Body, Size.Empty, TfLeft).Width + S(24);
                var exit = new Rectangle(W - M - exitW, y + (barH - S(32)) / 2, exitW, S(32));
                var open = new Rectangle(exit.X - S(8) - openW, exit.Y, openW, S(32));
                const string FilesAt = @"Files in %AppData%\HdrSwitch";
                items.Add(new Item { Rect = new Rectangle(M + S(4), y, open.X - S(8) - M - S(4), barH), Paint = (g, rc, lk) =>
                    Draw(g, FilesAt, fonts.Caption, P.Ink(P.Text2, fbg), rc, TfLeft) });
                items.Add(new Item { Rect = open, Key = "folder", Name = "Open folder", Tip = Dir, Click = () =>
                {
                    Dismiss(true);   // at once: Explorer is about to come to the front
                    try { Process.Start("explorer.exe", "\"" + Dir + "\""); } catch { }
                }, Paint = (g, rc, lk) => StandardButton(g, rc, lk, fbg, null, "Open folder") });
                items.Add(new Item { Rect = exit, Key = "exit", Name = "Exit Display Switch", Click = () => app.ExitThread(),
                    Paint = (g, rc, lk) => StandardButton(g, rc, lk, fbg, null, "Exit") });
                return y + barH;
            }

            // ---- the update button: in Live's footer bar and in Settings' Updates row ----

            /// <summary>Wide enough for every step the button shows, so it never outgrows its place.</summary>
            int UpdateWidth()
            {
                int spin = S(16), gap = S(8), w = TextRenderer.MeasureText("Update", fonts.Strong, Size.Empty, TfLeft).Width + S(24);
                foreach (string step in new[] { "Downloading", "Verifying", "Restarting", UpdateWaits, UpdateFailed, app.updateStep ?? "" })
                    w = Math.Max(w, TextRenderer.MeasureText(step, fonts.Strong, Size.Empty, TfLeft).Width + S(12) + spin + gap + S(18));
                return w;
            }

            /// <summary>How wide the update button is now, for its label: what the text beside it can't use.</summary>
            int UpdateFit()
            {
                bool busy = app.updating, failed = app.updateStep == UpdateFailed, waits = app.updateStep == UpdateWaits;
                string label = busy ? app.updateStep ?? "Downloading" : "Update";
                int text = TextRenderer.MeasureText(label, fonts.Strong, Size.Empty, TfLeft).Width;
                return (!busy ? S(24) : failed || waits ? S(18) : S(12) + S(16) + S(8)) + text + (busy ? S(18) : 0);
            }

            /// <summary>
            /// The update button, at the right of ur. An accent (primary) button while it waits for a click; once clicked a
            /// standard button around a spinner, its label rolling through the steps of the update; a failure says so with
            /// a nudge, and the reason is in the line beside it. Held back by a game it says "After the game", without the
            /// spinner.
            /// </summary>
            void UpdateButton(Rectangle ur, Color bg)
            {
                bool busy = app.updating, failed = app.updateStep == UpdateFailed, waits = app.updateStep == UpdateWaits;
                string label = busy ? app.updateStep ?? "Downloading" : "Update";
                RollText roll = Roll("update", label, fonts.Strong);
                int spin = S(16), gap = S(8);
                float fit = (!busy ? S(24) : failed || waits ? S(18) : S(12) + spin + gap) + roll.TargetWidth + (busy ? S(18) : 0);
                items.Add(new Item
                {
                    Name = busy ? label : "Update to " + app.update.VersionText, Tip = app.UpdateNote, Rect = ur, Key = "update", On = busy, Busy = busy && !failed && !waits,
                    Width = Math.Min(ur.Width, fit), Shake = failed, Listed = true,
                    Click = busy ? null : (Action)(() => { app.InstallUpdate(); Rebuild(); }),
                    Paint = (g, rc, lk) =>
                    {
                        // Busy, it is never narrower than its text is right now: the outgoing step's characters finish
                        // rolling out inside the button instead of being cut off by it.
                        float b = (float)Math.Max(0, Math.Min(1, lk.Busy));
                        float need = lk.On > 0.01 ? roll.Width + b * (spin + gap) + S(36) - b * S(6) : 0;
                        float w = (float)Math.Max(rc.Height, Math.Min(rc.Width, Math.Max(lk.Width, need)));
                        var pill = new RectangleF(rc.Right - w + (float)lk.Shake, rc.Y, w, rc.Height);
                        Color primary = Theme.Mix(Theme.Mix(P.Accent, P.AccentHover, lk.Hover), P.AccentPress, lk.Press);
                        Color standard = Theme.Mix(Theme.Mix(P.ControlFill, P.ControlFill2, lk.Hover), P.ControlFill3, lk.Press);
                        Color fill = Theme.Mix(primary, standard, lk.On);
                        Theme.Fill(g, pill, S(4), fill);
                        Outline(g, pill, S(4), Theme.Fade(P.ControlStroke, lk.On));
                        Color under = Palette.Over(fill, bg);
                        Color fg = Theme.Mix(P.Ink(P.OnAccent, under), P.Ink(P.Text1, under), lk.On);
                        float x = pill.X + (pill.Width - (b * (spin + gap) + roll.Width)) / 2;
                        Theme.Spinner(g, new PointF(x + spin / 2f, pill.Y + pill.Height / 2), spin * (0.6f + 0.4f * b), Math.Max(1.5f, 2 * k), fg, b);
                        DrawRollAt(g, roll, fonts.Strong, fg, under, Rectangle.Round(pill), x + b * (spin + gap));
                    }
                });
            }

            // ---- the render harness (--render-flyout): Windows' answers, made up ----

            /// <summary>What the displays, the registry and the running programs would say, for drawing the flyout without Windows.</summary>
            internal sealed class Sample
            {
                public bool HdrOn = true, HdrSupported = true, Startup = true;
                public Resolution.Mode Current, Full;
                public double Inches;
                public List<Resolution.Mode> Modes = new List<Resolution.Mode>();
                public List<string> Windowed = new List<string>();
                public string ScreenName;      // the monitor's name
                public bool Nvidia = true;     // an NVIDIA driver runs the display
                public int WindowsLevel = 20;  // Windows' own SDR brightness
                public int Grace = -1;         // seconds left of the wait before switching back, or -1
            }

            internal Sample sample;   // null: ask Windows, as always

            bool IsStartupNow() { return sample != null ? sample.Startup : IsStartup(); }

            IEnumerable<string> WindowedNow() { return sample != null ? (IEnumerable<string>)sample.Windowed : WindowedApps(); }

            /// <summary>
            /// Draws the flyout as it would look on this tab and view, at scale k, into a new bitmap: no window, no Windows.
            /// view (the Games tab): games (the library, filtered by find), add, game (game's page), res, stretch (the
            /// lists opened from its page). expand: the page's cards shown open (brightness; the area is open whenever
            /// it is set). focusKey: an item that shows the keyboard's focus ring; hoverKey: one under the mouse. win11:
            /// Windows draws the corners (otherwise the 1 px border shows).
            /// </summary>
            internal Bitmap Render(float scale, FlyoutTab pageTab, string view, string game, string find, string expand, string focusKey, string hoverKey, bool win11)
            {
                tab = pageTab;
                k = scale;
                if (fonts != null) fonts.Dispose();
                fonts = new Theme.Fonts(k);
                rolls.Clear();
                picking = view == "add";
                search = find ?? "";
                if (view == "game" || view == "res" || view == "stretch") EnterPage(game);
                sub = view == "res" || view == "stretch" ? view : null;
                brightOpen = expand == "brightness";
                rounded = win11;
                Rebuild();
                if (hoverKey != null)
                {
                    hover = items.FindIndex(it => it.Key == hoverKey);
                    Anim a;
                    if (hover >= 0 && anims.TryGetValue(hoverKey, out a)) a.Hover.Snap(1);
                }
                if (focusKey != null)
                {
                    focus = items.FindIndex(it => it.Key == focusKey);
                    keyboard = focus >= 0;
                }
                var size = new Size(S(372), contentH);
                var bmp = new Bitmap(size.Width, size.Height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
                using (var g = Graphics.FromImage(bmp)) PaintAll(g, size);
                return bmp;
            }

            // ---- the controls: WinUI's, drawn with the palette ----

            const string MarkOn = "ON";

            /// <summary>The thickness of a card's outline and of a divider: 1 px, 2 px in high contrast.</summary>
            int DividerWidth() { return P.HighContrast ? Math.Max(2, S(2)) : 1; }

            /// <summary>A card: its fill and its outline, 4 px corners.</summary>
            void CardBox(Graphics g, Rectangle rc)
            {
                Theme.Fill(g, rc, S(4), P.Card);
                Outline(g, rc, S(4), P.CardStroke);
            }

            /// <summary>An outline just inside the shape, as thick as DividerWidth.</summary>
            void Outline(Graphics g, RectangleF r, float radius, Color c)
            {
                float w = DividerWidth();
                if (c.A == 0 || r.Width <= w || r.Height <= w) return;
                using (var p = Theme.Round(RectangleF.Inflate(r, -w / 2, -w / 2), Math.Max(0, radius - w / 2)))
                using (var pen = new Pen(c, w)) g.DrawPath(pen, p);
            }

            /// <summary>A group of card rows that touch: one card round them all, a divider between one row and the next.</summary>
            void Group(Rectangle r, int rows)
            {
                items.Add(new Item { Rect = r, Paint = (g, rc, lk) =>
                {
                    CardBox(g, rc);
                    int dw = DividerWidth(), rh = rc.Height / rows;
                    using (var b = new SolidBrush(P.Divider))
                        for (int i = 1; i < rows; i++) g.FillRectangle(b, rc.X + dw, rc.Y + i * rh, rc.Width - 2 * dw, dw);
                } });
            }

            /// <summary>
            /// A card row with a toggle switch: icon, title and description, "On" or "Off", the switch. The whole row is
            /// the control (a click anywhere flips it); first and last: its place in its group, for the hover's corners.
            /// </summary>
            void CardToggle(Rectangle r, string key, string icon, string label, string desc, bool on, bool first, bool last, Action click, bool recording, string offWord = "Off", bool hdr = false)
            {
                RollText state = Roll("tile:" + key, on ? "On" : offWord, fonts.Body);
                int stateW = Math.Max(TextRenderer.MeasureText(offWord, fonts.Body, Size.Empty, TfLeft).Width, TextRenderer.MeasureText("On", fonts.Body, Size.Empty, TfLeft).Width) + S(2);
                items.Add(new Item { Rect = r, Key = "tile:" + key, On = on, Name = label, Tip = null, Role = AccessibleRole.CheckButton, Selected = on, Click = click, RingInside = true,
                    Paint = (g, rc, lk) =>
                {
                    CardHover(g, rc, lk, first, last);
                    Color bg = P.CardSolid;
                    RowText(g, rc, icon, label, desc, lk.On, S(12) + S(40) + S(12) + stateW + S(4), recording, hdr);
                    var tg = new RectangleF(rc.Right - S(12) - S(40), rc.Y + (rc.Height - S(20)) / 2f, S(40), S(20));
                    // The state in words too, as Windows' own settings do: a switch alone doesn't say which side is on.
                    DrawRoll(g, state, fonts.Body, P.Ink(P.Text1, bg), bg, new Rectangle((int)tg.X - S(12) - stateW, rc.Y, stateW, rc.Height), 1);
                    Toggle(g, tg, lk.On, lk.Hover, bg);
                } });
            }

            /// <summary>A card row under the mouse: a subtle fill (its group's outer corners kept), or in high contrast an outline.</summary>
            void CardHover(Graphics g, Rectangle rc, Look lk, bool first, bool last)
            {
                if (lk.Hover < 0.01) return;
                float radius = S(4);
                if (P.HighContrast)
                {
                    if (lk.Hover > 0.5) Outline(g, rc, first || last ? radius : 0, P.HoverStroke);
                    return;
                }
                Color c = Theme.Fade(Theme.Mix(P.SubtleHover, P.SubtlePress, lk.Press), lk.Hover);
                using (var p = Corners(rc, radius, first, last))
                using (var b = new SolidBrush(c)) g.FillPath(b, p);
            }

            /// <summary>A rectangle with round corners at the top, the bottom, both or neither.</summary>
            static GraphicsPath Corners(RectangleF r, float radius, bool top, bool bottom)
            {
                var p = new GraphicsPath();
                float d = Math.Min(2 * radius, Math.Min(r.Width, r.Height));
                if (top) { p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90); }
                else { p.AddLine(r.X, r.Y, r.Right, r.Y); }
                if (bottom) { p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90); }
                else { p.AddLine(r.Right, r.Bottom, r.X, r.Bottom); }
                p.CloseFigure();
                return p;
            }

            /// <summary>
            /// A card row's left side: a 16 px icon in a 20 px column, the title (Body) and a description (Caption,
            /// secondary). on 0 → 1 lights the icon in the accent colour. right: what the row's control takes on the right.
            /// recording: a recorder is running – the icon and a dot before the description in red.
            /// </summary>
            void RowText(Graphics g, Rectangle rc, string icon, string title, string desc, double on, int right, bool recording = false, bool hdr = false)
            {
                Color bg = P.CardSolid;
                int ix = rc.X + S(14), tx = ix + S(20) + S(12), tw = rc.Right - right - tx;
                Color red = P.HighContrast ? P.Text1 : P.Critical;
                Color ic = recording ? red : Theme.Mix(P.Ink(P.Text2, bg), hdr && !P.HighContrast ? P.HdrDot : P.Accent, on);
                LineIcon(g, icon, new Rectangle(ix, rc.Y + (rc.Height - S(20)) / 2, S(20), S(20)), ic);
                int top = rc.Y + (rc.Height - S(36)) / 2;
                Draw(g, title, fonts.Body, P.Ink(P.Text1, bg), new Rectangle(tx, top, tw, S(20)), TfLeft);
                int dx = 0;
                if (recording)
                {
                    Theme.Dot(g, tx + S(3), top + S(28), S(6), red);
                    dx = S(10);
                }
                Draw(g, desc, fonts.Caption, P.Ink(P.Text2, bg), new Rectangle(tx + dx, top + S(20), tw - dx, S(16)), TfLeft);
            }

            /// <summary>
            /// A 16 px line icon, centred in box, drawn like Segoe Fluent's (and the mockup's): the five layers (hdr,
            /// bright, area, stretch, scaling) and the settings (clock, rec, pause, power, island, update). Drawn rather
            /// than taken from the icon font, which has no symbol for most of them.
            /// </summary>
            void LineIcon(Graphics g, string key, Rectangle box, Color c)
            {
                if (key == "island") { IslandIcon(g, box, c); return; }
                float u = S(16) / 16f, ox = box.X + (box.Width - S(16)) / 2f, oy = box.Y + (box.Height - S(16)) / 2f;
                Func<float, float, PointF> P_ = (px, py) => new PointF(ox + px * u, oy + py * u);
                Func<float, float, float, float, RectangleF> R = (x, y, w, h) => new RectangleF(ox + x * u, oy + y * u, w * u, h * u);
                using (var pen = new Pen(c, Math.Max(1f, 1.3f * u)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                {
                    Action<float, float, float, float> line = (x1, y1, x2, y2) => g.DrawLine(pen, P_(x1, y1), P_(x2, y2));
                    Action<RectangleF, float> box_ = (r, radius) => { using (var p = Theme.Round(r, radius * u)) g.DrawPath(pen, p); };
                    switch (key)
                    {
                        case "hdr":
                            g.DrawEllipse(pen, R(5, 5, 6, 6));
                            line(8, 1.5f, 8, 3); line(8, 13, 8, 14.5f); line(1.5f, 8, 3, 8); line(13, 8, 14.5f, 8);
                            line(3.4f, 3.4f, 4.4f, 4.4f); line(11.6f, 11.6f, 12.6f, 12.6f); line(3.4f, 12.6f, 4.4f, 11.6f); line(11.6f, 4.4f, 12.6f, 3.4f);
                            break;
                        case "bright":
                            g.DrawEllipse(pen, R(2.5f, 2.5f, 11, 11));
                            using (var b = new SolidBrush(c)) g.FillPie(b, R(2.5f, 2.5f, 11, 11).X, R(2.5f, 2.5f, 11, 11).Y, 11 * u, 11 * u, -90, 180);
                            break;
                        case "area":
                            box_(R(1.5f, 3, 13, 10), 1);
                            box_(R(4, 5, 8, 6), 0.5f);
                            break;
                        case "stretch":
                            box_(R(1.5f, 3, 13, 10), 1);
                            line(4.5f, 8, 11.5f, 8);
                            line(4.5f, 8, 6.1f, 6.4f); line(4.5f, 8, 6.1f, 9.6f); line(11.5f, 8, 9.9f, 6.4f); line(11.5f, 8, 9.9f, 9.6f);
                            break;
                        case "scaling":
                            box_(R(1.5f, 4, 13, 8), 1);
                            g.DrawEllipse(pen, R(8, 6, 4, 4));
                            line(4, 7, 6, 7); line(4, 9, 6, 9); line(3, 12, 3, 13.5f); line(6, 12, 6, 13.5f);
                            break;
                        case "rec":
                            box_(R(1.5f, 4, 10, 8), 1);
                            g.DrawLines(pen, new[] { P_(11.5f, 7), P_(14.5f, 5), P_(14.5f, 11), P_(11.5f, 9) });
                            break;
                        case "clock":
                            g.DrawEllipse(pen, R(2, 2, 12, 12));
                            g.DrawLines(pen, new[] { P_(8, 4.5f), P_(8, 8), P_(10.5f, 9.5f) });
                            break;
                        case "pause":
                            line(5.5f, 3.5f, 5.5f, 12.5f); line(10.5f, 3.5f, 10.5f, 12.5f);
                            break;
                        case "power":
                            line(8, 2, 8, 8);
                            g.DrawArc(pen, R(3, 2.87f, 10, 10), 227.2f, -274.4f);
                            break;
                        case "update":
                            line(8, 2, 8, 10);
                            g.DrawLines(pen, new[] { P_(4.5f, 6.5f), P_(8, 10), P_(11.5f, 6.5f) });
                            line(2.5f, 13.5f, 13.5f, 13.5f);
                            break;
                        case "chev":     // a row that opens a page or a list
                            g.DrawLines(pen, new[] { P_(6, 3.5f), P_(10.5f, 8), P_(6, 12.5f) });
                            break;
                        case "back":
                            g.DrawLines(pen, new[] { P_(10, 3.5f), P_(5.5f, 8), P_(10, 12.5f) });
                            break;
                        case "down":     // a card that opens below itself
                            g.DrawLines(pen, new[] { P_(3.5f, 6), P_(8, 10.5f), P_(12.5f, 6) });
                            break;
                        case "up":
                            g.DrawLines(pen, new[] { P_(3.5f, 10), P_(8, 5.5f), P_(12.5f, 10) });
                            break;
                        case "search":
                            g.DrawEllipse(pen, R(2.5f, 2.5f, 9, 9));
                            line(10.5f, 10.5f, 14, 14);
                            break;
                        case "clear":
                            line(4.5f, 4.5f, 11.5f, 11.5f); line(11.5f, 4.5f, 4.5f, 11.5f);
                            break;
                        case "info":
                            g.DrawEllipse(pen, R(1.5f, 1.5f, 13, 13));
                            line(8, 7.5f, 8, 11.5f);
                            using (var b = new SolidBrush(c)) g.FillEllipse(b, R(7.2f, 4.2f, 1.6f, 1.6f));
                            break;
                        case "check":
                            g.DrawLines(pen, new[] { P_(3.5f, 8.5f), P_(6.5f, 11.5f), P_(12.5f, 4.5f) });
                            break;
                    }
                }
            }

            /// <summary>The island's symbol, which the icon font hasn't got: a screen with a small pill at its top.</summary>
            void IslandIcon(Graphics g, Rectangle r, Color c)
            {
                float u = S(16) / 16f, x = r.X + (r.Width - S(16)) / 2f, y = r.Y + (r.Height - S(16)) / 2f;
                using (var pen = new Pen(c, Math.Max(1f, u)))
                {
                    using (var p = Theme.Round(new RectangleF(x + 1.5f * u, y + 2.5f * u, 13 * u, 11 * u), 1.5f * u)) g.DrawPath(pen, p);
                    using (var p = Theme.Round(new RectangleF(x + 5 * u, y + 4.5f * u, 6 * u, 2.5f * u), 1.25f * u)) g.DrawPath(pen, p);
                }
            }

            /// <summary>
            /// WinUI's ToggleSwitch, 40 × 20: off, an outline (ControlStrongStroke) with a 12 px knob on the left; on, the
            /// accent colour with the knob on the right. on runs 0 → 1 as it moves; hover makes the knob 14 px.
            /// </summary>
            void Toggle(Graphics g, RectangleF r, double on, double hover, Color bg)
            {
                double t = Math.Max(0, Math.Min(1, on));
                float radius = r.Height / 2;
                if (t < 0.99)
                    using (var pen = new Pen(Theme.Fade(P.ControlStrong, 1 - t), Math.Max(1, S(1))))
                    using (var p = Theme.Round(RectangleF.Inflate(r, -0.5f, -0.5f), radius - 0.5f)) g.DrawPath(pen, p);
                if (t > 0.01) Theme.Fill(g, r, radius, Theme.Fade(Theme.Mix(P.Accent, P.AccentHover, hover), t));
                float d = S(12) + (S(14) - S(12)) * (float)Math.Max(0, Math.Min(1, hover));
                float x0 = r.X + S(10), x1 = r.Right - S(10);
                float cx = x0 + (x1 - x0) * (float)Math.Max(-0.05, Math.Min(1.05, on));
                Theme.Dot(g, cx, r.Y + r.Height / 2, d, Theme.Mix(P.Ink(P.Text2, bg), P.OnAccent, t));
            }

            /// <summary>
            /// HDR's marker on Live's HDR layer: a small amber dot and "ON" in amber on a faint amber fill – the one place
            /// amber is used. In high contrast the word carries it, in an outline.
            /// </summary>
            void HdrMark(Graphics g, Rectangle r, Color bg)
            {
                if (P.HighContrast) Outline(g, r, S(4), P.Text1);
                else Theme.Fill(g, r, S(4), P.HdrSoft);
                Color under = Palette.Over(P.HdrSoft, bg);
                Theme.Dot(g, r.X + S(6) + S(3), r.Y + r.Height / 2f, S(6), P.HdrDot);
                Draw(g, MarkOn, fonts.Label, P.Ink(P.HdrText, under), new Rectangle(r.X + S(6) + S(6) + S(5), r.Y, r.Width - S(17), r.Height), TfLeft);
            }

            /// <summary>The marker's width: dot, gap, the word and the padding either side.</summary>
            int MarkW() { return TextRenderer.MeasureText(MarkOn, fonts.Label, Size.Empty, TfLeft).Width + S(6) + S(6) + S(5) + S(6) + S(2); }

            /// <summary>One item of a segmented control: the chosen one raised (ControlFill, an outline, semibold).</summary>
            void Segment(Graphics g, Rectangle rc, Look lk, string text)
            {
                Color under = Palette.Over(P.ControlFill2, P.CardSolid);
                double t = Math.Max(0, Math.Min(1, lk.On));
                if (t < 0.99 && lk.Hover > 0.01)
                {
                    if (P.HighContrast) { if (lk.Hover > 0.5) Outline(g, rc, S(3), P.HoverStroke); }
                    else Theme.Fill(g, rc, S(3), Theme.Fade(Theme.Mix(P.SubtleHover, P.SubtlePress, lk.Press), lk.Hover * (1 - t)));
                }
                if (P.HighContrast)
                {
                    if (t > 0.5) Theme.Fill(g, rc, S(3), P.Accent);
                }
                else if (t > 0.01)
                {
                    Theme.Fill(g, rc, S(3), Theme.Fade(P.ControlFill, t));
                    using (var pen = new Pen(Theme.Fade(P.ControlStroke, t), 1))
                    using (var p = Theme.Round(RectangleF.Inflate(rc, -0.5f, -0.5f), S(3) - 0.5f)) g.DrawPath(pen, p);
                }
                Color sel = P.HighContrast ? P.OnAccent : P.Ink(P.Text1, Palette.Over(P.ControlFill, under));
                Color fg = Theme.Mix(P.Ink(P.Text2, under), sel, t);
                Draw(g, text, t >= 0.5 ? fonts.Label : fonts.Caption, fg, rc, TfCenter);
            }

            /// <summary>WinUI's standard button: ControlFill with an outline, 4 px corners, Body text. Rolling text, or plain.</summary>
            void StandardButton(Graphics g, Rectangle rc, Look lk, Color bg, RollText roll, string text)
            {
                Color fill = Theme.Mix(Theme.Mix(P.ControlFill, P.ControlFill2, lk.Hover), P.ControlFill3, lk.Press);
                Theme.Fill(g, rc, S(4), fill);
                Outline(g, rc, S(4), P.HighContrast && lk.Hover > 0.5 ? P.HoverStroke : P.ControlStroke);
                Color under = Palette.Over(fill, bg);
                Color fg = Theme.Mix(P.Ink(P.Text1, under), P.Ink(P.Text2, under), lk.Press);
                if (roll != null) DrawRoll(g, roll, fonts.Body, fg, under, rc, 0);
                else Draw(g, text, fonts.Body, fg, rc, TfCenter);
            }

            /// <summary>A subtle button: a symbol with no fill until the mouse is on it. fg: the symbol's (opaque) colour.</summary>
            void SubtleButton(Graphics g, Rectangle rc, Look lk, string symbol, Font font, Color fg, Color bg)
            {
                if (lk.Hover > 0.01)
                {
                    if (P.HighContrast) { if (lk.Hover > 0.5) Outline(g, rc, S(4), P.HoverStroke); }
                    else Theme.Fill(g, rc, S(4), Theme.Fade(Theme.Mix(P.SubtleHover, P.SubtlePress, lk.Press), lk.Hover));
                }
                Draw(g, symbol, font, fg, rc, TfCenter);
            }

            /// <summary>
            /// A list row: a subtle fill under the mouse, 2 px apart from the next one. selected: WinUI's selection – a
            /// subtle fill and a 3 × 16 accent pill on its left.
            /// </summary>
            void RowBg(Graphics g, Rectangle rc, Look lk, bool selected)
            {
                var r = new RectangleF(rc.X, rc.Y + S(2), rc.Width, rc.Height - S(4));
                if (selected)
                {
                    if (P.HighContrast) Outline(g, r, S(4), P.Accent);
                    else Theme.Fill(g, r, S(4), P.SubtleHover);
                    Theme.Fill(g, new RectangleF(r.X + S(1), r.Y + (r.Height - S(16)) / 2, S(3), S(16)), S(3) / 2f, P.Accent);
                }
                if (lk.Hover < 0.01) return;
                if (P.HighContrast) { if (lk.Hover > 0.5 && !selected) Outline(g, r, S(4), P.HoverStroke); return; }
                Theme.Fill(g, r, S(4), Theme.Fade(Theme.Mix(P.SubtleHover, P.SubtlePress, lk.Press), lk.Hover));
            }

            // ---- painting ----

            protected override void OnPaintBackground(PaintEventArgs e) { }   // OnPaint covers all of it

            /// <summary>Paints the whole window into WinForms' double buffer. A paint that fails is logged, not fatal.</summary>
            protected override void OnPaint(PaintEventArgs e)
            {
                try { PaintAll(e.Graphics, ClientSize); }
                catch (Exception x) { Guard.Log("Painting the flyout", x); }
            }

            /// <summary>Everything, onto g: the window's own paint and the render harness (RenderFlyout) both come here.</summary>
            void PaintAll(Graphics g, Size size)
            {
                if (fonts == null || size.Width <= 0 || size.Height <= 0) return;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.Clear(P.Surface);
                var shown = CardNow();
                for (int i = 0; i < items.Count; i++)
                {
                    var it = items[i];
                    if (it.Scrolls) continue;
                    if (!it.InCard || Grow == 0) { it.Paint(g, OnScreen(it), LookOf(i)); continue; }
                    var cs = g.Save();
                    g.SetClip(shown, CombineMode.Intersect);   // what the card doesn't show yet stays hidden in it
                    it.Paint(g, it.Grows ? shown : it.Rect, LookOf(i));
                    g.Restore(cs);
                }
                // The list scrolls inside a clip that keeps a half-scrolled row in its place.
                var list = ListNow();
                if (list.Width > 0 && list.Height > 0)
                {
                    var state = g.Save();
                    g.SetClip(list, CombineMode.Intersect);
                    for (int i = 0; i < items.Count; i++)
                    {
                        if (!items[i].Scrolls) continue;
                        var r = OnScreen(items[i]);
                        if (r.Bottom > list.Top && r.Top < list.Bottom) items[i].Paint(g, r, LookOf(i));
                    }
                    g.Restore(state);
                }
                if (scrollMax > 0 && thumb.Value > 0.01)   // a thin bar that is only there while the list moves
                {
                    int content = list.Height + scrollMax;
                    int trackH = list.Height - S(8);
                    int thumbH = Math.Max(S(24), trackH * list.Height / content);
                    float thumbY = list.Y + S(4) + (trackH - thumbH) * (float)Math.Max(0, Math.Min(scrollMax, scrollTo.Value)) / scrollMax;
                    Theme.Fill(g, new RectangleF(list.Right - S(2) - S(2), thumbY, S(2), thumbH), S(1), Theme.Fade(P.ControlStrong, thumb.Value));
                }
                FocusRing(g);
                Border(g, size);
            }

            /// <summary>
            /// Where Windows doesn't draw the border (Windows 10, square corners), a 1 px one in the flyout's stroke colour;
            /// in high contrast always, 2 px in the window text colour.
            /// </summary>
            void Border(Graphics g, Size size)
            {
                if (rounded && !P.HighContrast) return;
                float w = P.HighContrast ? Math.Max(2, S(2)) : 1;
                Color c = P.HighContrast ? P.Stroke : Palette.Over(P.Stroke, P.Surface);
                var r = RectangleF.Inflate(new RectangleF(0, 0, size.Width, size.Height), -w / 2, -w / 2);
                using (var pen = new Pen(c, w))
                using (var p = Theme.Round(r, rounded ? Math.Max(0, S(8) - w / 2) : 0)) g.DrawPath(pen, p);
            }

            Look LookOf(int i)
            {
                Anim a;
                if (items[i].Key == null || !anims.TryGetValue(items[i].Key, out a)) return Look.None;
                a.Look.Hover = a.Hover.Value;
                a.Look.Press = a.Press.Value;
                a.Look.On = a.On.Value;
                a.Look.Busy = a.Busy.Value;
                a.Look.Width = a.Width.Value;
                a.Look.Shake = a.Shake.Value;
                return a.Look;
            }

            /// <summary>Points every item's hover and press springs at where the mouse is now.</summary>
            void Aim()
            {
                for (int i = 0; i < items.Count; i++)
                {
                    Anim a;
                    if (items[i].Key == null || !anims.TryGetValue(items[i].Key, out a)) continue;
                    a.Hover.To(i == hover ? 1 : 0);
                    a.Press.To(i == hover && i == pressed ? 1 : 0);
                }
                Invalidate();
                if (Visible) Motion.Run(tick);
            }

            /// <summary>Where an item is right now: scrolled with the list, moved with the status card's height.</summary>
            Rectangle OnScreen(Item it)
            {
                int dy = (it.Scrolls ? -scroll : 0) + (it.Below || it.Scrolls ? Grow : 0);
                return dy == 0 ? it.Rect : new Rectangle(it.Rect.X, it.Rect.Y + dy, it.Rect.Width, it.Rect.Height);
            }

            /// <summary>How far the status card's animated height is from its full height: negative while it grows.</summary>
            int Grow { get { return (int)Math.Round(cardH.Value) - card.Height; } }

            Rectangle CardNow() { return new Rectangle(card.X, card.Y, card.Width, Math.Max(0, card.Height + Grow)); }

            Rectangle ListNow() { return new Rectangle(listClip.X, listClip.Y + Grow, listClip.Width, listClip.Height); }

            /// <summary>The window follows the status card's height. Its bottom stays put by the taskbar, so it grows upwards.</summary>
            void Fit()
            {
                int h = contentH + Grow;
                if (h != ClientSize.Height && h > 0) final = Corner(out slide);
                Place();
            }

            /// <summary>Keeps the Graphics' clip (the list, the status card) and its offset: TextRenderer ignores both otherwise.</summary>
            const TextFormatFlags Keep = TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.PreserveGraphicsTranslateTransform;

            /// <summary>
            /// GDI text, so ClearType on the opaque fills. c must be opaque – GDI has no alpha; a token is made opaque over
            /// what it sits on with Palette.Ink first. Anything translucent that slips through is put over the surface.
            /// </summary>
            static void Draw(Graphics g, string s, Font f, Color c, Rectangle r, TextFormatFlags flags)
            {
                if (string.IsNullOrEmpty(s) || r.Width <= 0 || r.Height <= 0) return;
                if (c.A < 255) c = Palette.Over(c, P.Surface);
                TextRenderer.DrawText(g, s, f, r, c, flags | Keep);
            }

            /// <summary>
            /// The text under this key, rolling to its new value when it differs from the last rebuild's (Roll.cs).
            /// Only while the flyout is open: it always opens on what is true now, without replaying what changed.
            /// </summary>
            RollText Roll(string key, string text, Font font)
            {
                RollText r;
                if (!rolls.TryGetValue(key, out r))
                {
                    rolls[key] = r = new RollText(RollText.Gdi(font));
                    r.Snap(text);
                }
                else if (Visible && !closing) r.Set(text);
                else r.Snap(text);
                r.Seen = true;
                return r;
            }

            /// <summary>Rolling text in r on the opaque colour under: align -1 left, 0 centred, 1 right. At rest and too wide, it is shortened like any other text.</summary>
            void DrawRoll(Graphics g, RollText rt, Font f, Color fg, Color under, Rectangle r, int align)
            {
                float w = rt.Width;
                if (w > r.Width && !rt.Moving)
                {
                    Draw(g, rt.Text, f, fg, r, align < 0 ? TfLeft : align > 0 ? TfLeft | TextFormatFlags.Right : TfCenter);
                    return;
                }
                float x = align < 0 ? r.X : align > 0 ? r.Right - w : r.X + (r.Width - w) / 2;
                DrawRollAt(g, rt, f, fg, under, r, x);
            }

            /// <summary>
            /// Rolling text from x, centred on clip's height and kept inside it. A character fades as it rolls – towards
            /// the colour under it, since GDI text has no alpha – and its trailing copy (the motion blur) is fainter still.
            /// </summary>
            static void DrawRollAt(Graphics g, RollText rt, Font f, Color fg, Color under, Rectangle clip, float x)
            {
                const TextFormatFlags F = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | Keep;
                if (under.A < 255) under = Palette.Over(under, P.Surface);
                if (fg.A < 255) fg = Palette.Over(fg, under);
                int lineH = f.Height;
                float y = clip.Y + (clip.Height - lineH) / 2f;
                var state = g.Save();
                g.SetClip(clip, CombineMode.Intersect);
                rt.Draw(x, y, lineH, (ch, gx, gy, alpha, smear) =>
                {
                    var at = new Point((int)Math.Round(gx), (int)Math.Round(gy));
                    if (smear != 0) TextRenderer.DrawText(g, ch, f, new Point(at.X, at.Y + (int)Math.Round(smear)), Theme.Mix(under, fg, alpha * 0.35), F);
                    TextRenderer.DrawText(g, ch, f, at, Theme.Mix(under, fg, alpha), F);
                });
                g.Restore(state);
            }

            // ---- mouse ----

            int HitTest(Point p)
            {
                if (closing) return -1;
                for (int i = items.Count - 1; i >= 0; i--)
                {
                    var it = items[i];
                    if (it.Click == null) continue;
                    if (it.Scrolls && !ListNow().Contains(p)) continue;
                    if (it.InCard && !CardNow().Contains(p)) continue;
                    if (OnScreen(it).Contains(p)) return i;
                }
                return -1;
            }

            void SetHover(int i)
            {
                if (i == hover) return;
                hover = i;
                tip.SetToolTip(this, i >= 0 ? items[i].Tip : null);
                Aim();
            }

            Point lastMouse;

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                if (keyboard && e.Location != lastMouse) { keyboard = false; Invalidate(); }   // the mouse took over: no ring
                lastMouse = e.Location;
                if (dragging)
                {
                    int i = items.FindIndex(it => it.Slide != null);
                    if (i >= 0) Do(() => items[i].Slide(e.X));
                    return;
                }
                SetHover(HitTest(e.Location));
            }
            protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); SetHover(-1); }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                if (e.Button != MouseButtons.Left) return;
                pressed = HitTest(e.Location);
                if (pressed >= 0 && items[pressed].Slide != null)
                {
                    // A slider follows the mouse from the press on; the level is saved when it is let go.
                    dragging = true;
                    var slide = items[pressed].Slide;
                    Do(() => slide(e.X));
                }
                Aim();
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                base.OnMouseUp(e);
                if (dragging)
                {
                    dragging = false;
                    pressed = -1;
                    Do(() => { CommitBrightness(); Rebuild(); });
                    return;
                }
                int hit = HitTest(e.Location);
                var click = pressed >= 0 && hit == pressed ? items[pressed].Click : null;
                pressed = -1;
                Aim();
                if (click != null) Do(click);
            }

            protected override void OnMouseWheel(MouseEventArgs e)
            {
                base.OnMouseWheel(e);
                if (scrollMax == 0 || closing || !ListNow().Contains(e.Location)) return;
                // In proportion to the turn: a notch (120) moves a set distance, a touchpad's many small deltas add up to
                // the same, and what is less than a pixel waits for the next one.
                int step = WheelStep(SystemInformation.MouseWheelScrollLines, rowH, listClip.Height / Math.Max(1, rowH));
                int px = WheelPixels(ref wheel, e.Delta, step);
                if (px == 0) return;
                scrollTo.To(Math.Max(0, Math.Min(scrollMax, scrollTo.Target - px)));
                thumb.To(1);
                thumbRest.Stop();
                thumbRest.Start();
                Motion.Run(tick);
                if (Motion.Reduced) Tick(0);   // nothing animates: take the new position now
            }
        }

        // ---- the render harness ----

        /// <summary>
        /// A TrayApp that only holds state, for the render harness: no tray icon shown, no timers, no hotkey, no watchers,
        /// nothing read from or written to disk, nothing switched.
        /// </summary>
        TrayApp(Flyout.Sample sample)
        {
            flyout = new Flyout(this) { sample = sample };
        }

        /// <summary>
        /// HdrSwitch.exe --render-flyout out.png [--theme dark|light|hc] [--tab live|games|settings]
        /// [--state running|stretch|grace|recording|paused|idle] [--view games|add|game|res|stretch] [--game exe]
        /// [--search text] [--expand brightness|area] [--notice text] [--no-nvidia] [--scale 1.5] [--focus key]
        /// [--hover key] [--win10] [--no-update]: draws the flyout over made-up state into a PNG and exits, so its look
        /// can be checked without Windows (under Mono, say). Never part of a normal run. The states: running –
        /// Counter-Strike 2 with HDR, SDR brightness 40, a competitive area and No scaling; stretch – VALORANT
        /// stretched over its area; grace – it closed, back to SDR in 2 s; recording – OBS keeps HDR off; paused; idle.
        /// The library has ten games: with HDR or never, brightness, areas, a stretch, and one area the display lacks.
        /// --view game shows --game's page (cs2 unless given); --expand brightness opens its slider (the area is open
        /// whenever it is set).
        /// </summary>
        internal static int RenderFlyout(string[] args)
        {
            Func<string, string, string> arg = (name, unset) =>
            {
                int i = Array.IndexOf(args, name);
                return i >= 0 && i + 1 < args.Length ? args[i + 1] : unset;
            };
            string path = arg("--render-flyout", "flyout.png"), theme = arg("--theme", "dark");
            Palette.Current = Palette.Sample(theme);
            float scale;
            if (!float.TryParse(arg("--scale", "1"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out scale) || scale <= 0) scale = 1;

            var sample = new Flyout.Sample
            {
                Current = new Resolution.Mode { Width = 2560, Height = 1440, Hz = 240 },
                Full = new Resolution.Mode { Width = 2560, Height = 1440, Hz = 240 },
                Inches = 26.5,
                Windowed = new List<string> { "discord", "obs64", "spotify" },
            };
            foreach (var m in new[] { new[] { 2368, 1332 }, new[] { 1920, 1080 }, new[] { 1920, 1440 }, new[] { 1776, 1332 }, new[] { 1600, 900 }, new[] { 1280, 960 } })
                sample.Modes.Add(new Resolution.Mode { Width = m[0], Height = m[1], Hz = 240 });
            var app = new TrayApp(sample);
            sample.Nvidia = !args.Contains("--no-nvidia");
            foreach (var g in new[] { "cs2", "cyberpunk2077", "eldenring", "valorant", "r5apex", "bg3", "forzahorizon5", "helldivers2" }) app.games.Add(g);
            foreach (var g in new[] { "hades2", "stardewvalley" }) app.excluded.Add(g);
            app.labels["cs2"] = "Steam: Counter-Strike 2";
            app.labels["cyberpunk2077"] = "Steam: Cyberpunk 2077";
            app.labels["eldenring"] = "Steam: ELDEN RING";
            app.labels["valorant"] = "Riot: VALORANT";
            app.labels["hades2"] = "Steam: Hades II";
            app.labels["r5apex"] = "EA: Apex Legends";
            app.labels["bg3"] = "GOG: Baldur's Gate 3";
            app.labels["forzahorizon5"] = "Xbox: Forza Horizon 5";
            app.labels["helldivers2"] = "Steam: HELLDIVERS 2";
            app.labels["stardewvalley"] = "added by hand";
            app.profiles["cs2"] = new Resolution.Mode { Width = 2368, Height = 1332 };
            app.profiles["valorant"] = new Resolution.Mode { Width = 2368, Height = 1332 };
            app.stretches["valorant"] = new Resolution.Mode { Width = 1776, Height = 1332 };
            app.profiles["r5apex"] = new Resolution.Mode { Width = 2304, Height = 1296, Hz = 240 };   // not among the display's modes: a setup hint
            app.profiles["stardewvalley"] = new Resolution.Mode { Width = 1920, Height = 1080 };
            app.brightness["cs2"] = 40;
            app.brightness["cyberpunk2077"] = 60;
            app.brightness["forzahorizon5"] = 30;
            string notice = arg("--notice", null);
            if (notice != null) app.notice = notice;
            app.update = args.Contains("--no-update") ? null : new Updater.Release { Version = new Version(1, 9, 0) };
            app.islandOn = true;
            app.sdrWhileRecording = false;
            app.sdrDelay = 3;
            string state = arg("--state", "running");
            var display = new Hdr.Display { Gdi = @"\\.\DISPLAY1", Supported = true, On = true };
            var desktop = new Resolution.Mode { Width = 2560, Height = 1440, Hz = 240 };
            sample.HdrOn = false;
            if (state == "running" || state == "grace" || state == "stretch")
            {
                bool stretched = state == "stretch";
                string game = stretched ? "valorant" : "cs2";
                if (state != "grace") app.running[1] = new Running { Name = game };
                else sample.Grace = 2;
                app.owned.Add(display);
                sample.HdrOn = true;
                sample.ScreenName = "LG 27GR95QE";
                app.res = new ActiveMode { Gdi = display.Gdi, Game = game, Back = desktop, ScalingBack = stretched ? -1 : 2,
                    Mode = stretched ? new Resolution.Mode { Width = 1776, Height = 1332, Hz = 240 } : new Resolution.Mode { Width = 2368, Height = 1332, Hz = 240 },
                    Area = stretched ? new Resolution.Mode { Width = 2368, Height = 1332, Hz = 240 } : null };
                if (state == "running") { app.brightBack = Hdr.LevelOf(20); app.brightSet = Hdr.LevelOf(40); }
            }
            else if (state == "recording")
            {
                app.running[1] = new Running { Name = "cs2" };
                app.sdrWhileRecording = true;
                app.recorder = "obs64";
                sample.ScreenName = "LG 27GR95QE";
                app.res = new ActiveMode { Gdi = display.Gdi, Game = "cs2", Back = desktop, ScalingBack = 2, Mode = new Resolution.Mode { Width = 2368, Height = 1332, Hz = 240 } };
            }
            else if (state == "paused")
            {
                app.running[1] = new Running { Name = "cs2" };
                app.paused = true;
            }
            FlyoutTab page;
            string tabArg = arg("--tab", "games");
            page = tabArg == "live" ? FlyoutTab.Live : tabArg == "settings" ? FlyoutTab.Settings : FlyoutTab.Games;
            using (var bmp = app.flyout.Render(scale, page, arg("--view", "games"), arg("--game", "cs2"), arg("--search", null), arg("--expand", null), arg("--focus", null), arg("--hover", null), !args.Contains("--win10")))
                bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            Console.WriteLine("Wrote " + path);
            return 0;
        }

        // ---- the flyout's decisions, kept apart from the window so the checks can run them ----

        /// <summary>
        /// The monitor the flyout uses: the one it opened on while that one is still there, else the one under the mouse.
        /// </summary>
        internal static string FlyoutScreen(string openedOn, ICollection<string> present, string underMouse)
        {
            return openedOn != null && present.Contains(openedOn) ? openedOn : underMouse;
        }

        /// <summary>
        /// Where a panel this size sits on a monitor: in the corner by the taskbar, margin in, and the offset it slides in
        /// from (towards the taskbar). The taskbar is wherever the working area stops short of the monitor's bounds.
        /// </summary>
        internal static Point FlyoutCorner(Rectangle wa, Rectangle b, Size size, int margin, int slide, out Point slideFrom)
        {
            int x = wa.Right - margin - size.Width, y = wa.Bottom - margin - size.Height;
            slideFrom = new Point(0, slide);                                                     // taskbar at the bottom
            if (wa.Top > b.Top) { y = wa.Top + margin; slideFrom = new Point(0, -slide); }        // at the top
            else if (wa.Left > b.Left) { x = wa.Left + margin; slideFrom = new Point(-slide, 0); }
            else if (wa.Right < b.Right) slideFrom = new Point(slide, 0);
            return new Point(x, y);
        }

        /// <summary>
        /// How many list rows the flyout shows: up to five, but only as many as fit in the working area's height with the
        /// rest of the panel (fixedH: everything but the rows) and the margin above and below it. Never fewer than one:
        /// the list scrolls. All in pixels, at the monitor's scale.
        /// </summary>
        internal static int FlyoutRows(int workAreaH, int margin, int fixedH, int rowH, int count)
        {
            int fit = rowH > 0 ? (workAreaH - 2 * margin - fixedH) / rowH : 1;
            return Math.Max(1, Math.Min(Math.Min(5, count), fit));
        }

        internal enum FlyoutKey { Nothing, Ring, Click }

        /// <summary>
        /// What Enter or Space does. A held key (auto-repeat) does nothing; with the ring hidden (the mouse took over)
        /// the first press only shows where the keyboard is; then it clicks.
        /// </summary>
        internal static FlyoutKey FlyoutKeyAction(bool ringShown, bool repeat, bool hasFocus)
        {
            if (repeat || !hasFocus) return FlyoutKey.Nothing;
            return ringShown ? FlyoutKey.Click : FlyoutKey.Ring;
        }

        /// <summary>
        /// How far one wheel notch (120) scrolls the list, in pixels: half a row for each line of Windows' "lines to
        /// scroll" setting (3, the default: one and a half rows), never more than the list less a row, so a notch
        /// always leaves a row of what was in view. "One screen at a time" (-1) scrolls that much; 0 doesn't scroll.
        /// </summary>
        internal static int WheelStep(int lines, int rowH, int visibleRows)
        {
            int page = Math.Max(1, visibleRows - 1) * rowH;
            if (lines == 0) return 0;
            if (lines < 0) return page;
            return Math.Min(page, Math.Max(1, lines * rowH / 2));
        }

        /// <summary>
        /// The pixels a wheel delta scrolls (positive: towards the top), step per 120. What doesn't make a whole pixel
        /// stays in carry for the next delta, so many small touchpad deltas move exactly as far as one notch.
        /// </summary>
        internal static int WheelPixels(ref int carry, int delta, int step)
        {
            long total = carry + (long)delta * step;
            int px = (int)(total / 120);
            carry = (int)(total - px * 120L);
            return px;
        }

        /// <summary>
        /// The file the flyout's pencil opens: the one behind what it shows. view: games (the library) and add (the
        /// programs to add): games.txt; game (a game's page): resolutions.txt, or brightness.txt while its SDR
        /// brightness card is open (brightOpen); res and stretch (the lists opened from the page): resolutions.txt.
        /// </summary>
        internal static string FlyoutFile(string view, bool brightOpen)
        {
            if (view == "game") return brightOpen ? BrightnessFile : ProfilesFile;
            return view == "res" || view == "stretch" ? ProfilesFile : ListFile;
        }

        /// <summary>
        /// The way back through the flyout's views: for each open one, the control that opened it and where the list
        /// below was scrolled.
        /// </summary>
        internal sealed class FlyoutTrail
        {
            readonly List<KeyValuePair<string, double>> steps = new List<KeyValuePair<string, double>>();

            public int Count { get { return steps.Count; } }
            public void Clear() { steps.Clear(); }
            public void Push(string from, double scroll) { steps.Add(new KeyValuePair<string, double>(from, scroll)); }

            /// <summary>Back to this many open views (one level or several at once): where the outermost one closed was entered from.</summary>
            public bool Pop(int depth, out string from, out double scroll)
            {
                from = null;
                scroll = 0;
                depth = Math.Max(0, depth);
                if (steps.Count <= depth) return false;
                from = steps[depth].Key;
                scroll = steps[depth].Value;
                steps.RemoveRange(depth, steps.Count - depth);
                return true;
            }
        }
    }
}
