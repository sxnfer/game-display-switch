// The panel that opens from the tray icon. Drawn by hand in the same look as the island: liquid glass (Glass.cs)
// with soft cards on it, capsule buttons, sliding switches, white glass for what is on, amber for HDR and nothing else.
// Nothing here changes any state itself – every click calls back into TrayApp and rebuilds the view.
//
// Like the island it is a layered window drawn through UpdateLayeredWindow: per-pixel alpha, so the glass shows the
// blurred desktop and its corners are its own. Its region is its rounded shape, which keeps the blur inside it. The
// price: no ClearType (grey anti-aliasing on glass) and no system shadow. Invalidate() still works as usual: it
// queues one Render for the next message, which paints the whole surface and hands it to Windows.

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
        sealed class Flyout : Form
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
            }

            readonly TrayApp app;
            float k = 1;   // DPI scale of the monitor it opened on
            Theme.Fonts fonts;
            readonly ToolTip tip = new ToolTip { InitialDelay = 600 };
            readonly List<Item> items = new List<Item>();
            readonly Dictionary<string, Anim> anims = new Dictionary<string, Anim>();
            readonly Dictionary<string, RollText> rolls = new Dictionary<string, RollText>();   // text that rolls when it changes, by key
            readonly TextFormatFlags TfLeft, TfCenter, TfCaption, TfWrap;   // TfCaption: right-aligned, never shortened
            Rectangle listClip;
            Glass.Surface surface;   // what UpdateLayeredWindow shows; kept while the flyout is open
            Size shaped;             // the size the window's region was last cut to
            bool queued, blurOn;
            double fade = 1;         // the window's alpha: 0 → 1 as it opens
            int scroll, scrollMax, rowH;
            int hover = -1, pressed = -1;
            int focus = -1;       // the item the keyboard is on (Tab, arrows; Enter or Space presses it)
            string focusKey;      // … kept by key across a rebuild
            bool keyboard;        // the focus ring shows: the keyboard was used since the mouse last moved
            bool picking;     // the list shows running programs to add, instead of the games
            string choosing;  // the list shows the resolutions for this game, instead of the games
            bool stretching;  // choosing: the list shows the game's own resolutions, to stretch over its competitive one
            bool brightening; // choosing: the list shows the SDR brightness levels for the game
            readonly FlyoutTrail trail = new FlyoutTrail();   // where each open view was entered from: back returns there
            bool entered;         // a view was just entered: the keyboard starts on its first row
            bool returned;        // just back from a view: the keyboard is on the control that opened it
            bool repeat;          // the key being handled is held down (auto-repeat): it moves, but never presses
            int wheel;            // what the wheel moved that is less than a pixel, kept for its next turn
            string screenName;    // the monitor it opened on: it stays there until it closes
            readonly Spring open = Motion.Open(0);           // 0 hidden → 1 in place: slide and fade
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
                BackColor = Theme.Solid;
                Text = "Display Switch";
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

                const TextFormatFlags Base = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter;
                TfLeft = Base | TextFormatFlags.Left;
                TfCenter = Base | TextFormatFlags.HorizontalCenter;
                TfCaption = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.Right;
                TfWrap = TextFormatFlags.NoPadding | TextFormatFlags.WordBreak | TextFormatFlags.Left | TextFormatFlags.Top;

                tick = Tick;
                thumbRest.Tick += (s, e) => { thumbRest.Stop(); thumb.To(0); Motion.Run(tick); };
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
                choosing = null;
                stretching = false;
                brightening = false;
                trail.Clear();
                entered = returned = false;
                wheel = 0;
                Motion.Refresh();
                Glass.Refresh();
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
                Render();
                Activate();
                Native.SetForegroundWindow(Handle);
                Motion.Run(tick);
            }

            /// <summary>Closes it: sliding and fading out, or at once (now) when something else is about to take the screen.</summary>
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
                if (surface != null) { surface.Dispose(); surface = null; }
                Glass.ClearText();
                Trim();
            }

            /// <summary>The window follows the open spring: from the slide offset to its place, fading in on the way.</summary>
            void Place()
            {
                double o = Math.Max(0, Math.Min(1, open.Value));
                var p = new Point(final.X + (int)Math.Round(slide.X * (1 - o)), final.Y + (int)Math.Round(slide.Y * (1 - o)));
                if (Location != p) Location = p;
                fade = Math.Min(1, o * 1.5);
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
                Place();
                if (closing && !open.Moving) { Finish(); return false; }
                int s = (int)Math.Round(scrollTo.Value);
                if (s != scroll)
                {
                    scroll = s;
                    SetHover(HitTest(PointToClient(Cursor.Position)));   // another row is under the mouse now
                }
                Invalidate();
                return moving;
            }

            /// <summary>Where it sits when open: in the corner by the taskbar, 12 px in, like the system flyouts.</summary>
            Point Corner(out Point slideFrom)
            {
                var scr = Where();
                return FlyoutCorner(scr.WorkingArea, scr.Bounds, Size, S(12), S(24), out slideFrom);
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
                get { var cp = base.CreateParams; cp.ExStyle |= 0x80 | 0x80000; return cp; }   // WS_EX_TOOLWINDOW: no Alt+Tab entry; WS_EX_LAYERED
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
                    case Keys.Escape:
                        if (repeat) return true;
                        if (Depth() > 0) Do(GoBack); else Dismiss(false);
                        return true;
                    case Keys.Tab: case Keys.Down: case Keys.Right: MoveFocus(1); return true;
                    case Keys.Shift | Keys.Tab: case Keys.Up: case Keys.Left: MoveFocus(-1); return true;
                    case Keys.Home: MoveFocus(0); return true;
                    case Keys.Enter: Press(); return true;
                    case Keys.Back:
                        if (!repeat && Depth() > 0) Do(GoBack);
                        return true;
                }
                return base.ProcessDialogKey(keyData);
            }

            protected override void OnKeyDown(KeyEventArgs e)
            {
                base.OnKeyDown(e);
                if (e.KeyCode == Keys.Space) { Press(); e.Handled = true; }
            }

            // ---- keyboard ----

            bool Focusable(int i) { return i >= 0 && i < items.Count && (items[i].Click != null || items[i].Listed) && !closing; }

            /// <summary>To the next (1) or previous (-1) control, round and round; 0: the first one.</summary>
            void MoveFocus(int dir)
            {
                var order = Enumerable.Range(0, items.Count).Where(Focusable).ToList();
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
            void Press()
            {
                switch (FlyoutKeyAction(keyboard, repeat, Focusable(focus)))
                {
                    case FlyoutKey.Ring: SetFocus(focus); break;
                    case FlyoutKey.Click:
                        var click = items[focus].Click;   // null: a busy button, which has nothing to do yet
                        if (click != null) Do(click);
                        break;
                }
            }

            /// <summary>How many views deep it is: 0 the games list, 1 a game's resolutions or the programs to add, 2 below those.</summary>
            int Depth() { return picking ? 1 : choosing == null ? 0 : stretching || brightening ? 2 : 1; }

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
                if (stretching) stretching = false;
                else if (brightening) brightening = false;
                else { picking = false; choosing = null; }
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

            /// <summary>The ring round the item the keyboard is on.</summary>
            void FocusRing(Graphics g)
            {
                if (!keyboard || !Focusable(focus)) return;
                var it = items[focus];
                var r = RectangleF.Inflate(OnScreen(it), S(2), S(2));
                var state = g.Save();
                if (it.Scrolls) g.SetClip(RectangleF.Inflate(ListNow(), 0, S(2)), CombineMode.Intersect);
                float radius = Math.Min(r.Height / 2f, S(20));
                using (var pen = new Pen(Theme.Fade(Theme.Text1, 0.9), Math.Max(2f, 2 * k)))
                using (var p = Theme.Round(r, radius)) g.DrawPath(pen, p);
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
                    get { var item = Now; return item == null || item.Click == null ? null : item.Role == AccessibleRole.CheckButton ? "Toggle" : "Press"; }
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
                }
            }

            /// <summary>The state a screen reader hears that comes from the item itself: checked, busy, or unavailable while it can't be clicked.</summary>
            static AccessibleStates Marks(Item it)
            {
                var st = AccessibleStates.None;
                if (it.Click == null && it.Role != AccessibleRole.StaticText) st |= AccessibleStates.Unavailable;   // the update button while busy
                if (it.Selected) st |= AccessibleStates.Checked;
                if (it.Busy) st |= AccessibleStates.Busy;
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
                }
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    thumbRest.Dispose();
                    tip.Dispose();
                    if (surface != null) surface.Dispose();
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
                int W = S(376), M = S(16), G = S(12), CP = S(16);
                int y = M;

                // Status card: the island at rest – dot, state, what is running, the app's mark. Below that the latest
                // setup hint, and the update button when there is one.
                string title, sub;
                bool on;
                Status(out title, out sub, out on);
                bool hasUpdate = app.update != null;
                string notice = app.notice, error = hasUpdate ? app.UpdateNote : null;   // why it failed, or what it waits for
                int textW = W - 2 * M - 2 * CP;
                int noticeH = notice == null ? 0 : TextRenderer.MeasureText(notice, fonts.Caption, new Size(textW, 2000), TfWrap).Height;
                int errorH = error == null ? 0 : TextRenderer.MeasureText(error, fonts.Caption, new Size(textW, 2000), TfWrap).Height;
                int noticeY = CP + S(44) + S(10);
                int updateY = CP + S(44) + (notice != null ? S(10) + noticeH : 0) + S(14);
                int h1 = (hasUpdate ? updateY + S(36) + (error != null ? S(8) + errorH : 0) : updateY - S(14)) + CP;
                var c1 = new Rectangle(M, y, W - 2 * M, h1);
                RollText titleRoll = Roll("title", title, fonts.Title), subRoll = Roll("sub", sub, fonts.Caption);
                items.Add(new Item { Rect = c1, Key = "status", On = on, InCard = true, Grows = true, Role = AccessibleRole.StaticText,
                    Name = title + ". " + sub + (notice != null ? ". " + notice : "") + (error != null ? ". " + error : ""), Paint = (g, rc, lk) =>
                {
                    Glass.Chip(g, rc, S(20), Theme.Chip);
                    Theme.Dot(g, rc.X + CP + S(9), rc.Y + CP + S(22), S(10), Theme.Mix(Theme.Grey, Theme.Amber, lk.On));
                    int tx = rc.X + CP + S(30), tw = rc.Right - CP - S(36) - S(12) - tx;
                    DrawRoll(g, titleRoll, fonts.Title, Theme.Text1, new Rectangle(tx, rc.Y + CP - S(1), tw, S(26)), -1);
                    DrawRoll(g, subRoll, fonts.Caption, Theme.Text2, new Rectangle(tx, rc.Y + CP + S(26), tw, S(18)), -1);
                    Theme.Badge(g, new RectangleF(rc.Right - CP - S(36), rc.Y + CP + S(4), S(36), S(36)), lk.On);
                    if (notice != null) Draw(g, notice, fonts.Caption, Theme.Text2, new Rectangle(rc.X + CP, rc.Y + noticeY, textW, noticeH), TfWrap);
                    if (error != null) Draw(g, error, fonts.Caption, Theme.Text2, new Rectangle(rc.X + CP, rc.Y + updateY + S(36) + S(8), textW, errorH), TfWrap);
                } });
                if (hasUpdate)
                {
                    // White and full-width while it waits for a click. Once clicked it turns dark and shrinks around a
                    // spinner, its label rolling through the steps of the update; a failure says so with a nudge, then
                    // it opens up again with the reason underneath. Held back by a game it keeps its label, without the spinner.
                    bool busy = app.updating, failed = app.updateStep == UpdateFailed, waits = app.updateStep == UpdateWaits;
                    string label = busy ? app.updateStep ?? "Downloading" : "Update to " + app.update.VersionText;
                    var ur = new Rectangle(c1.X + CP, c1.Y + updateY, c1.Width - 2 * CP, S(36));
                    RollText roll = Roll("update", label, fonts.Strong);
                    int spin = S(16), gap = S(8);
                    float fit = !busy ? ur.Width : (failed || waits ? S(18) : S(12) + spin + gap) + roll.TargetWidth + S(18);
                    items.Add(new Item
                    {
                        Name = label, Rect = ur, Key = "update", On = busy, Busy = busy && !failed && !waits, Width = Math.Min(ur.Width, fit), Shake = failed, InCard = true, Listed = true,
                        Click = busy ? null : (Action)(() => { app.InstallUpdate(); Rebuild(); }),
                        Paint = (g, rc, lk) =>
                        {
                            // Busy, it is never narrower than its text is right now: the outgoing step's characters
                            // finish rolling out inside the pill instead of being cut off by it.
                            float b = (float)Math.Max(0, Math.Min(1, lk.Busy));
                            float need = lk.On > 0.01 ? roll.Width + b * (spin + gap) + S(36) - b * S(6) : 0;
                            float w = (float)Math.Max(rc.Height, Math.Min(rc.Width, Math.Max(lk.Width, need)));
                            var pill = new RectangleF(rc.X + (rc.Width - w) / 2 + (float)lk.Shake, rc.Y, w, rc.Height);
                            Color idle = Theme.Mix(Theme.Mix(Theme.Lit, Theme.LitHover, lk.Hover), Theme.LitPress, lk.Press);
                            Color fill = Theme.Mix(idle, Theme.Raised, lk.On);
                            Theme.Fill(g, pill, pill.Height / 2f, fill);
                            Glass.Edge(g, pill, pill.Height / 2f, lk.On);
                            float x = pill.X + (pill.Width - (b * (spin + gap) + roll.Width)) / 2;
                            Theme.Spinner(g, new PointF(x + spin / 2f, pill.Y + pill.Height / 2), spin * (0.6f + 0.4f * b), Math.Max(1.5f, 2 * k), Theme.Text1, b);
                            DrawRollAt(g, roll, fonts.Strong, Theme.Mix(Theme.Ink, Theme.Text1, lk.On), Rectangle.Round(pill), x + b * (spin + gap));
                        }
                    });
                }
                y = c1.Bottom + G;
                card = c1;
                int firstBelow = items.Count;

                // Tiles for the three settings: lit (white) when on.
                int tileH = S(76), tg = S(8), tw3 = (W - 2 * M - 2 * tg) / 3;
                AddTile(new Rectangle(M, y, tw3, tileH), "startup", "", "Start with Windows", IsStartup(), null,
                    () => { SetStartup(!IsStartup()); Rebuild(); });
                AddTile(new Rectangle(M + tw3 + tg, y, tw3, tileH), "recording", "", "SDR while recording", app.sdrWhileRecording, null,
                    () => { app.SetSdrWhileRecording(!app.sdrWhileRecording); Rebuild(); });
                AddTile(new Rectangle(W - M - tw3, y, tw3, tileH), "island", null, "Island", app.islandOn, "Pill at the top of the screen",
                    () => { app.SetIsland(!app.islandOn); Rebuild(); });
                y += tileH + G;

                // How switching behaves: how long to wait before switching back, and a pause (also on Ctrl+Alt+H).
                int barH = S(52), pillH = S(32);
                var c2 = new Rectangle(M, y, W - 2 * M, barH);
                int waitW = TextRenderer.MeasureText("Switch back after", fonts.Body, Size.Empty, TfLeft).Width + S(4);
                items.Add(new Item { Rect = c2, Paint = (g, rc, lk) =>
                {
                    Glass.Chip(g, rc, S(20), Theme.Chip);
                    Draw(g, "Switch back after", fonts.Body, Theme.Text1, new Rectangle(rc.X + CP, rc.Y, waitW, rc.Height), TfLeft);
                } });
                RollText delay = Roll("delay", app.sdrDelay + " s", fonts.Strong);
                var dr = new Rectangle(c2.X + CP + waitW + S(8), c2.Y + (barH - pillH) / 2, S(64), pillH);
                items.Add(new Item { Rect = dr, Key = "delay", Name = "Switch back after " + app.sdrDelay + " seconds", Tip = "The wait after a game closes, for launchers that restart it. Click for the next one.",
                    Click = () => { app.SetDelay(NextDelay(app.sdrDelay)); Rebuild(); },
                    Paint = (g, rc, lk) =>
                    {
                        Color fill = Theme.Mix(Theme.Mix(Theme.Raised, Theme.Hover, lk.Hover), Theme.Press, lk.Press);
                        Theme.Fill(g, rc, rc.Height / 2f, fill);
                        Glass.Edge(g, rc, rc.Height / 2f, 1);
                        DrawRoll(g, delay, fonts.Strong, Theme.Text1, rc, 0);
                    } });
                bool paused = app.paused;
                RollText pause = Roll("pause", paused ? "Resume" : "Pause", fonts.Strong);
                int pauseW = Math.Max(TextRenderer.MeasureText("Resume", fonts.Strong, Size.Empty, TfLeft).Width, TextRenderer.MeasureText("Pause", fonts.Strong, Size.Empty, TfLeft).Width) + S(36);
                var pr = new Rectangle(c2.Right - CP + S(6) - pauseW, dr.Y, pauseW, pillH);
                items.Add(new Item { Rect = pr, Key = "pause", On = paused, Name = paused ? "Resume switching" : "Pause switching",
                    Tip = (paused ? "Switching is paused. " : "Stops all switching until you resume. ") + HotKey.Text + (app.hotkey.Registered ? " does this from anywhere." : " is taken by another program."),
                    Click = () => { app.SetPaused(!app.paused); Rebuild(); },
                    Paint = (g, rc, lk) =>
                    {
                        // Lit like a tile while paused, so the paused state is seen at a glance.
                        Color rest = Theme.Mix(Theme.Raised, Theme.Lit, lk.On);
                        Color over = Theme.Mix(Theme.Hover, Theme.LitHover, lk.On), down = Theme.Mix(Theme.Press, Theme.LitPress, lk.On);
                        Theme.Fill(g, rc, rc.Height / 2f, Theme.Mix(Theme.Mix(rest, over, lk.Hover), down, lk.Press));
                        Glass.Edge(g, rc, rc.Height / 2f, 1 - lk.On);
                        DrawRoll(g, pause, fonts.Strong, Theme.Mix(Theme.Text1, Theme.Ink, lk.On), rc, 0);
                    } });
                y += barH + G;

                // The list: games with a switch each, running programs to add, or the resolutions for one game.
                int headH = S(44);
                rowH = S(44);
                var names = new List<string>();
                var modes = new List<Resolution.Mode>();   // choosing: null for "Off", then the display's sizes
                var levels = new List<int>();              // brightening: -1 for Windows' own, then 0…100
                Resolution.Mode chosen = null, full = null;
                double inches = 0;
                int hz = 0;
                if (picking) names = WindowedApps().Where(n => !app.games.Contains(n)).ToList();
                else if (choosing != null)
                {
                    string gdi = TargetGdi();
                    var cur = Resolution.Current(gdi);
                    app.profiles.TryGetValue(choosing, out chosen);
                    hz = chosen != null && chosen.Hz > 0 ? chosen.Hz : cur != null ? cur.Hz : 60;
                    var pic = Resolution.Read(gdi);   // the panel's own size is the target of the display path
                    full = pic != null ? new Resolution.Mode { Width = pic.TargetW, Height = pic.TargetH, Hz = hz } : cur;
                    inches = Resolution.Inches(gdi);
                    if (chosen == null) stretching = false;   // nothing to stretch over
                    if (brightening)
                    {
                        levels.Add(-1);
                        for (int p = 0; p <= 100; p += 10) levels.Add(p);
                    }
                    else if (stretching)
                    {
                        // The game's own resolution: the sizes that lie inside the competitive one, which the GPU stretches over it.
                        modes.Add(null);
                        modes.AddRange(Resolution.Modes(gdi, hz).Where(m => Fits(m, chosen)).OrderByDescending(m => (long)m.Width * m.Height));
                    }
                    else
                    {
                        modes.Add(BrightRow);
                        if (chosen != null) modes.Add(StretchRow);
                        modes.Add(null);
                    }
                    if (!stretching && !brightening && full != null)
                        modes.AddRange(Resolution.Modes(gdi, hz)
                            .Where(m => m.Width < full.Width || m.Height < full.Height)
                            .OrderBy(m => SameAspect(m, full) ? 0 : 1).ThenByDescending(m => (long)m.Width * m.Height));
                }
                else names = app.games.Concat(app.excluded).Concat(app.profiles.Keys).Distinct().OrderBy(app.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
                int count = choosing != null ? (brightening ? levels.Count : modes.Count) : names.Count;
                int footH = choosing != null ? S(52) : S(8);
                // Up to five rows, fewer when the whole panel wouldn't fit above the taskbar (a high scale, a tall status card).
                int visible = FlyoutRows(Where().WorkingArea.Height, S(12), y + headH + footH + G + S(32) + M, rowH, count);
                var c3 = new Rectangle(M, y, W - 2 * M, headH + visible * rowH + footH);
                items.Add(new Item { Rect = c3, Paint = (g, rc, lk) => Glass.Chip(g, rc, S(20), Theme.Chip) });
                if (picking || choosing != null)
                {
                    var back = new Rectangle(c3.X + S(8), c3.Y + S(6), S(32), S(32));
                    items.Add(new Item { Rect = back, Key = "back", Name = "Back", Click = GoBack,
                        Paint = (g, rc, lk) => Button(g, rc, lk, Theme.Raised, "", fonts.GlyphSmall, Theme.Text1) });
                    string head = picking ? "Add a program" : app.DisplayName(choosing);
                    string headRight = picking ? "" : brightening ? "SDR brightness" : stretching ? "inside " + chosen.Text : "at " + hz + " Hz";
                    items.Add(new Item { Rect = c3, Paint = (g, rc, lk) =>
                    {
                        Draw(g, head, fonts.Strong, Theme.Text1, new Rectangle(rc.X + S(50), rc.Y, rc.Width - S(50) - CP - S(84), headH), TfLeft);
                        Draw(g, headRight, fonts.Caption, Theme.Text3, new Rectangle(rc.X + CP, rc.Y, rc.Width - 2 * CP, headH), TfLeft | TextFormatFlags.Right);
                    } });
                }
                else
                {
                    RollText onCount = Roll("count", app.games.Count + " on", fonts.Caption);
                    int gamesW = TextRenderer.MeasureText("Games", fonts.Strong, Size.Empty, TfLeft).Width + S(6);   // measured a little short for semibold
                    items.Add(new Item { Rect = c3, Paint = (g, rc, lk) =>
                    {
                        Draw(g, "Games", fonts.Strong, Theme.Text1, new Rectangle(rc.X + CP, rc.Y, gamesW, headH), TfLeft);
                        DrawRoll(g, onCount, fonts.Caption, Theme.Text3, new Rectangle(rc.X + CP + gamesW + S(8), rc.Y + S(1), S(100), headH), -1);
                    } });
                    // The two things to do with the list sit in its header: scan again, add something by hand.
                    var add = new Rectangle(c3.Right - S(10) - S(30), c3.Y + S(7), S(30), S(30));
                    var look = new Rectangle(add.X - S(6) - S(30), add.Y, S(30), S(30));
                    bool scanning = app.scanning;
                    items.Add(new Item { Rect = look, Key = "look", Tip = scanning ? "Looking for games…" : "Look for games", Busy = scanning, Listed = true,
                        Click = scanning ? null : (Action)(() => { app.ScanInBackground(true); Rebuild(); }),
                        Paint = (g, rc, lk) =>
                        {
                            // While it looks, the magnifier fades out and a spinner takes its place.
                            Button(g, rc, lk, Theme.Raised, "", fonts.GlyphSmall, Theme.Fade(Theme.Text1, 1 - lk.Busy));
                            Theme.Spinner(g, new PointF(rc.X + rc.Width / 2f, rc.Y + rc.Height / 2f), S(13), Math.Max(1.5f, 1.75f * k), Theme.Text1, lk.Busy);
                        } });
                    items.Add(new Item { Rect = add, Key = "add", Tip = "Add a program", Click = () => { picking = true; Into("add"); Rebuild(); },
                        Paint = (g, rc, lk) => Button(g, rc, lk, Theme.Raised, "", fonts.GlyphSmall, Theme.Text1) });
                }
                // A little narrower than the card, so the list's square bitmap stays clear of the card's round corners.
                listClip = new Rectangle(c3.X + S(4), c3.Y + headH, c3.Width - S(8), visible * rowH);
                scrollMax = Math.Max(0, count * rowH - listClip.Height);
                if (scrollTo.Target > scrollMax) scrollTo.Snap(scrollMax);
                scroll = (int)Math.Round(scrollTo.Value);
                if (count == 0)
                {
                    string msg = picking ? "No programs" : app.scanning ? "Looking…" : "No games";
                    items.Add(new Item { Rect = listClip, Paint = (g, rc, lk) => Draw(g, msg, fonts.Body, Theme.Text3, rc, TfCenter) });
                }
                for (int i = 0; i < count; i++)
                {
                    var rr = new Rectangle(c3.X, listClip.Y + i * rowH, c3.Width, rowH);
                    if (choosing != null && brightening)
                    {
                        int level = levels[i];
                        string game = choosing;
                        int mine;
                        bool isMine = app.brightness.TryGetValue(game, out mine) ? mine == level : level < 0;
                        string levelText = level < 0 ? "Windows setting" : level + "";
                        string levelDetail = level < 0 ? "Not changed" : (80 + 4 * level) + " nits";
                        items.Add(new Item { Rect = rr, Scrolls = true, Key = "bright:" + level, Name = levelText + ", " + levelDetail, Role = AccessibleRole.RadioButton, Selected = isMine, Click = () =>
                        {
                            brightening = false; Out();
                            app.SetBrightness(game, level);
                            Rebuild();
                        }, Paint = (g, rc, lk) =>
                        {
                            RowBg(g, rc, lk);
                            Draw(g, levelText, fonts.Body, Theme.Text1, new Rectangle(rc.X + CP, rc.Y, S(150), rowH), TfLeft);
                            Draw(g, levelDetail, fonts.Caption, Theme.Text3, new Rectangle(rc.X + CP + S(150), rc.Y, rc.Width - 2 * CP - S(150) - S(28), rowH), TfLeft | TextFormatFlags.Right);
                            if (isMine) Draw(g, "\uE73E", fonts.GlyphSmall, Theme.Text1, new Rectangle(rc.Right - CP - S(20), rc.Y, S(20), rowH), TfCenter);
                        } });
                        continue;
                    }
                    if (choosing != null)
                    {
                        var m = modes[i];
                        string game = choosing;
                        if (ReferenceEquals(m, BrightRow))
                        {
                            // Opens the SDR brightness levels: how bright everything that isn't HDR looks while this game runs.
                            int mine;
                            string brightText = app.brightness.TryGetValue(game, out mine) ? mine + " (" + (80 + 4 * mine) + " nits)" : "Windows setting";
                            bool set = app.brightness.ContainsKey(game);
                            items.Add(new Item { Rect = rr, Scrolls = true, Key = "mode:bright", Name = "SDR brightness: " + brightText, Click = () => { brightening = true; Into("mode:bright"); Rebuild(); }, Paint = (g, rc, lk) =>
                            {
                                RowBg(g, rc, lk);
                                Draw(g, "SDR brightness", fonts.Body, Theme.Text1, new Rectangle(rc.X + CP, rc.Y, S(130), rowH), TfLeft);
                                Draw(g, brightText, fonts.Caption, set ? Theme.Text1 : Theme.Text3, new Rectangle(rc.X + CP + S(130), rc.Y, rc.Width - 2 * CP - S(130) - S(28), rowH), TfLeft | TextFormatFlags.Right);
                                Draw(g, "\uE76C", fonts.GlyphSmall, Theme.Text3, new Rectangle(rc.Right - CP - S(20), rc.Y, S(20), rowH), TfCenter);
                            } });
                            continue;
                        }
                        if (ReferenceEquals(m, StretchRow))
                        {
                            // Opens the list of resolutions the game itself can run at, stretched over this one.
                            Resolution.Mode ownNow = app.StretchOf(game);
                            string ownText = ownNow == null ? "Not stretched" : ownNow.Text + ", stretched";
                            items.Add(new Item { Rect = rr, Scrolls = true, Key = "mode:stretch", Name = "Game resolution: " + ownText, Click = () => { stretching = true; Into("mode:stretch"); Rebuild(); }, Paint = (g, rc, lk) =>
                            {
                                RowBg(g, rc, lk);
                                Draw(g, "Game resolution", fonts.Body, Theme.Text1, new Rectangle(rc.X + CP, rc.Y, S(130), rowH), TfLeft);
                                Draw(g, ownText, fonts.Caption, ownNow != null ? Theme.Text1 : Theme.Text3, new Rectangle(rc.X + CP + S(130), rc.Y, rc.Width - 2 * CP - S(130) - S(28), rowH), TfLeft | TextFormatFlags.Right);
                                Draw(g, "", fonts.GlyphSmall, Theme.Text3, new Rectangle(rc.Right - CP - S(20), rc.Y, S(20), rowH), TfCenter);
                            } });
                            continue;
                        }
                        string key = "mode:" + (stretching ? "own:" : "") + (m == null ? "off" : m.Width + "x" + m.Height);
                        if (stretching)
                        {
                            Resolution.Mode ownNow = app.StretchOf(game);
                            bool isOwn = m == null ? ownNow == null : ownNow != null && m.Width == ownNow.Width && m.Height == ownNow.Height;
                            string ownLabel = m == null ? "Not stretched" : m.Text;
                            string ownDetail = m == null ? "" : AspectText(m);
                            items.Add(new Item { Rect = rr, Scrolls = true, Key = key, Name = ownLabel + (ownDetail.Length > 0 ? ", " + ownDetail : ""), Role = AccessibleRole.RadioButton, Selected = isOwn, Click = () =>
                            {
                                stretching = false; choosing = null; Out();
                                app.SetStretch(game, m);
                                Rebuild();
                            }, Paint = (g, rc, lk) =>
                            {
                                RowBg(g, rc, lk);
                                Draw(g, ownLabel, fonts.Body, Theme.Text1, new Rectangle(rc.X + CP, rc.Y, S(120), rowH), TfLeft);
                                Draw(g, ownDetail, fonts.Caption, Theme.Text3, new Rectangle(rc.X + CP + S(120), rc.Y, rc.Width - 2 * CP - S(120) - S(28), rowH), TfLeft | TextFormatFlags.Right);
                                if (isOwn) Draw(g, "", fonts.GlyphSmall, Theme.Text1, new Rectangle(rc.Right - CP - S(20), rc.Y, S(20), rowH), TfCenter);
                            } });
                            continue;
                        }
                        int keepHz = chosen != null ? chosen.Hz : 0;
                        bool isChosen = m == null ? chosen == null : chosen != null && m.Width == chosen.Width && m.Height == chosen.Height;
                        string label = m == null ? "Off" : m.Text;
                        string detail = m == null ? (full == null ? "" : "Full screen") : SizeText(m, full, inches);
                        items.Add(new Item { Rect = rr, Scrolls = true, Key = key, Name = label + (detail.Length > 0 ? ", " + detail : ""), Role = AccessibleRole.RadioButton, Selected = isChosen, Click = () =>
                        {
                            choosing = null; Out();
                            app.SetProfile(game, m == null ? null : new Resolution.Mode { Width = m.Width, Height = m.Height, Hz = keepHz });
                            Rebuild();
                        }, Paint = (g, rc, lk) =>
                        {
                            RowBg(g, rc, lk);
                            Draw(g, label, fonts.Body, Theme.Text1, new Rectangle(rc.X + CP, rc.Y, S(120), rowH), TfLeft);
                            Draw(g, detail, fonts.Caption, Theme.Text3, new Rectangle(rc.X + CP + S(120), rc.Y, rc.Width - 2 * CP - S(120) - S(28), rowH), TfLeft | TextFormatFlags.Right);
                            if (isChosen) Draw(g, "", fonts.GlyphSmall, Theme.Text1, new Rectangle(rc.Right - CP - S(20), rc.Y, S(20), rowH), TfCenter);
                        } });
                        continue;
                    }
                    string name = names[i];
                    if (picking)
                        items.Add(new Item { Rect = rr, Scrolls = true, Key = "pick:" + name, Name = "Add " + name + ".exe", Click = () => { app.Add(name); picking = false; Out(); Rebuild(); }, Paint = (g, rc, lk) =>
                        {
                            RowBg(g, rc, lk);
                            Draw(g, name + ".exe", fonts.Body, Theme.Text1, new Rectangle(rc.X + CP, rc.Y, rc.Width - 2 * CP - S(24), rowH), TfLeft);
                            Draw(g, "", fonts.GlyphSmall, Theme.Mix(Theme.Text3, Theme.Text1, lk.Hover), new Rectangle(rc.Right - CP - S(20), rc.Y, S(20), rowH), TfCenter);
                        } });
                    else
                    {
                        bool isOn = app.games.Contains(name);
                        Resolution.Mode prof;
                        app.profiles.TryGetValue(name, out prof);
                        Resolution.Mode own = prof == null ? null : app.StretchOf(name);
                        string cap = prof == null ? null : own == null ? ProfileText(prof) : own.Text + " stretched";
                        int capW = cap == null ? 0 : TextRenderer.MeasureText(cap, fonts.Caption, new Size(int.MaxValue, rowH), TfCaption).Width + S(10);
                        int monX = rr.Right - CP - S(44) - S(6) - S(32);   // the monitor symbol, left of the switch
                        var mon = new Rectangle(monX, rr.Y + (rowH - S(32)) / 2, S(32), S(32));
                        items.Add(new Item { Rect = rr, Scrolls = true, Key = "row:" + name, On = isOn, Role = AccessibleRole.CheckButton, Selected = isOn,
                            Name = app.DisplayName(name) + ", HDR" + (cap != null ? ", " + cap : ""), Click = () => { if (app.games.Contains(name)) app.Remove(name); else app.Add(name); Rebuild(); }, Paint = (g, rc, lk) =>
                        {
                            RowBg(g, rc, lk);
                            int right = rc.X + (monX - rr.X) - S(4);
                            if (cap != null) Draw(g, cap, fonts.Caption, Theme.Text3, new Rectangle(right - capW, rc.Y, capW, rowH), TfCaption);
                            Draw(g, app.DisplayName(name), fonts.Body, Theme.Mix(Theme.Text2, Theme.Text1, lk.On), new Rectangle(rc.X + CP, rc.Y, right - capW - rc.X - CP, rowH), TfLeft);
                            Theme.Switch(g, new RectangleF(rc.Right - CP - S(44), rc.Y + (rowH - S(26)) / 2f, S(44), S(26)), lk.On, lk.Hover);
                        } });
                        // The monitor symbol opens the game's resolution and brightness view; white when it has either.
                        items.Add(new Item { Rect = mon, Scrolls = true, Key = "mon:" + name, Tip = "Resolution and SDR brightness", Name = "Resolution and SDR brightness for " + app.DisplayName(name), Click = () => { choosing = name; stretching = false; brightening = false; Into("mon:" + name); Rebuild(); },
                            Paint = (g, rc, lk) => Button(g, rc, lk, Theme.None, "", fonts.Glyph, prof != null || app.brightness.ContainsKey(name) ? Theme.Text1 : Theme.Mix(Theme.Text3, Theme.Text1, lk.Hover)) });
                    }
                }
                if (choosing != null)
                {
                    string note = brightening
                        ? "How bright the desktop and SDR content look while this game runs with HDR on. Put back when it closes."
                        : stretching
                        ? "For borderless games. Stretched over " + chosen.Text + "."
                        : "Missing one? Create it in NVIDIA Control Panel, at " + hz + " Hz.";
                    items.Add(new Item { Rect = new Rectangle(c3.X + CP, listClip.Bottom + S(4), c3.Width - 2 * CP, footH - S(12)), Paint = (g, rc, lk) =>
                        Draw(g, note, fonts.Caption, Theme.Text3, rc, TextFormatFlags.NoPadding | TextFormatFlags.WordBreak | TextFormatFlags.Left | TextFormatFlags.VerticalCenter) });
                }
                y = c3.Bottom + G;

                // Bottom bar, straight on the window: version, edit the list, exit.
                string version = "Display Switch " + Updater.CurrentText;
                int exitW = TextRenderer.MeasureText("Exit", fonts.Body, Size.Empty, TfLeft).Width + S(32);
                var exit = new Rectangle(W - M - exitW, y, exitW, S(32));
                items.Add(new Item { Rect = new Rectangle(M + S(4), y, exit.X - M - S(48), S(32)), Paint = (g, rc, lk) => Draw(g, version, fonts.Caption, Theme.Text3, rc, TfLeft) });
                items.Add(new Item { Rect = exit, Key = "exit", Name = "Exit Display Switch", Click = () => app.ExitThread(), Paint = (g, rc, lk) =>
                {
                    Theme.Fill(g, rc, rc.Height / 2f, Theme.Mix(Theme.Mix(Theme.Raised, Theme.Hover, lk.Hover), Theme.Press, lk.Press));
                    Glass.Edge(g, rc, rc.Height / 2f, 1);
                    Draw(g, "Exit", fonts.Body, Theme.Text1, rc, TfCenter);
                } });
                var edit = new Rectangle(exit.X - S(8) - S(32), y, S(32), S(32));
                string file = FlyoutFile(choosing != null, brightening);   // the file behind what the list shows
                items.Add(new Item { Rect = edit, Key = "edit", Tip = "Edit " + System.IO.Path.GetFileName(file), Click = () =>
                {
                    Dismiss(true);   // at once: Notepad is about to come to the front
                    try { Process.Start("notepad.exe", "\"" + file + "\""); } catch { }
                }, Paint = (g, rc, lk) => Button(g, rc, lk, Theme.None, "", fonts.Glyph, Theme.Mix(Theme.Text2, Theme.Text1, lk.Hover)) });
                y += S(32) + M;

                for (int i = firstBelow; i < items.Count; i++) items[i].Below = true;
                contentH = y;
                if (Visible && !closing) cardH.To(card.Height);
                else cardH.Snap(card.Height);

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

            void Status(out string title, out string sub, out bool on)
            {
                var displays = Hdr.All();
                on = displays.Any(d => d.On);
                var hdrGames = app.running.Values.Where(r => app.games.Contains(r.Name)).Select(r => app.DisplayName(r.Name)).Distinct().ToList();
                var res = app.res;
                if (app.paused) { title = "Paused"; sub = "Nothing switches · " + HotKey.Text + " resumes"; return; }
                if (!displays.Any(d => d.Supported)) { title = "No HDR display"; sub = "Check Windows display settings"; }
                else if (app.recorder != null && !on)
                {
                    title = "SDR";
                    sub = app.RecorderName(app.recorder) + " running" + (hdrGames.Count > 0 ? " · " + string.Join(", ", hdrGames) : "");
                }
                else if (hdrGames.Count > 0) { title = "HDR on"; sub = string.Join(", ", hdrGames); }
                else if (res != null) { title = res.Mode.Text; sub = app.DisplayName(res.Game); }
                else if (on) { title = "HDR is on"; sub = "Turned on elsewhere"; }
                else { title = "Waiting for a game"; sub = "HDR off"; }
                if (res != null && hdrGames.Count > 0) sub += " · " + res.Text;
                else if (res != null && res.Area != null) sub += app.StretchLost() ? " · not stretched" : " · stretched";
            }

            /// <summary>"92.5 % · 24.6″": how much of the panel a mode covers when shown 1:1 in the middle.</summary>
            static string SizeText(Resolution.Mode m, Resolution.Mode full, double inches)
            {
                if (full == null || full.Width == 0) return "";
                double ratio = Math.Sqrt((double)m.Width * m.Width + (double)m.Height * m.Height) / Math.Sqrt((double)full.Width * full.Width + (double)full.Height * full.Height);
                string s = (ratio * 100).ToString("0.0") + " %";
                return inches > 0 ? s + " · " + (inches * ratio).ToString("0.0") + "″" : s;
            }

            static readonly Resolution.Mode StretchRow = new Resolution.Mode();   // stands for the "Game resolution" row among the modes
            static readonly Resolution.Mode BrightRow = new Resolution.Mode();    // stands for the "SDR brightness" row

            /// <summary>"4:3", "16:10", "16:9" … or "1.78:1" for a shape without a short name.</summary>
            static string AspectText(Resolution.Mode m)
            {
                int a = m.Width, b = m.Height;
                while (b != 0) { int t = a % b; a = b; b = t; }
                int w = m.Width / a, h = m.Height / a;
                if (w == 8 && h == 5) return "16:10";
                return w <= 21 && h <= 10 ? w + ":" + h : ((double)m.Width / m.Height).ToString("0.00") + ":1";
            }

            static bool SameAspect(Resolution.Mode m, Resolution.Mode full)
            {
                return Math.Abs((long)m.Width * full.Height - (long)m.Height * full.Width) * 100 < (long)full.Width * full.Height;
            }

            /// <summary>A setting as a rounded square: symbol above, name below; white glass with dark content when it is on.</summary>
            void AddTile(Rectangle r, string key, string icon, string label, bool on, string tipText, Action click)
            {
                RollText state = Roll("tile:" + key, on ? "On" : "Off", fonts.Caption);
                items.Add(new Item { Rect = r, Key = "tile:" + key, On = on, Tip = tipText, Name = label, Role = AccessibleRole.CheckButton, Selected = on, Click = click, Paint = (g, rc, lk) =>
                {
                    Color rest = Theme.Mix(Theme.Chip, Theme.Lit, lk.On);
                    Color over = Theme.Mix(Theme.ChipHover, Theme.LitHover, lk.On), down = Theme.Mix(Theme.Press, Theme.LitPress, lk.On);
                    Color fill = Theme.Mix(Theme.Mix(rest, over, lk.Hover), down, lk.Press);
                    Theme.Fill(g, rc, S(18), fill);
                    Glass.Edge(g, rc, S(18), 1 - lk.On);
                    Color fg = Theme.Mix(Theme.Text1, Theme.Ink, lk.On);
                    if (icon != null) Draw(g, icon, fonts.Glyph, fg, new Rectangle(rc.X + S(11), rc.Y + S(11), S(20), S(20)), TfCenter);
                    else   // the island's own symbol: a small pill
                        using (var pen = new Pen(fg, Math.Max(1f, 1.5f * k)))
                        using (var p = Theme.Round(new RectangleF(rc.X + S(12), rc.Y + S(15), S(20), S(11)), S(11) / 2f)) g.DrawPath(pen, p);
                    // The state in words as well: three lit tiles side by side don't say by themselves that lit means on.
                    DrawRoll(g, state, fonts.Caption, Theme.Mix(Theme.Text2, Theme.InkSoft, lk.On), new Rectangle(rc.Right - S(12) - S(40), rc.Y + S(11), S(40), S(20)), 1);
                    Draw(g, label, fonts.Label, fg, new Rectangle(rc.X + S(12), rc.Bottom - S(10) - S(32), rc.Width - S(20), S(32)),
                        TextFormatFlags.NoPadding | TextFormatFlags.WordBreak | TextFormatFlags.Left | TextFormatFlags.Bottom);
                } });
            }

            // ---- painting ----

            protected override void OnInvalidated(InvalidateEventArgs e)
            {
                base.OnInvalidated(e);
                if (queued || !IsHandleCreated) return;
                queued = true;   // one Render for however many Invalidates come before it
                BeginInvoke((Action)Render);
            }

            /// <summary>Paints the whole window onto its surface and hands it to Windows. A paint that fails is logged, not fatal.</summary>
            void Render()
            {
                queued = false;
                if (!Visible) return;   // queued before it closed: painting now would rebuild the surface Finish let go
                try { PaintSurface(); }
                catch (Exception e) { Guard.Log("Painting the flyout", e); }
            }

            /// <summary>The painting itself, with the blur and the shape.</summary>
            void PaintSurface()
            {
                if (fonts == null || !IsHandleCreated || ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
                var all = new RectangleF(0, 0, ClientSize.Width, ClientSize.Height);
                // The surface is made for the tallest the window gets (the status card at its full height) and only made
                // again when the window outgrows it: while the card's height animates, each frame shows less of it, not a new one.
                if (surface == null || surface.Size.Width < ClientSize.Width || surface.Size.Height < ClientSize.Height)
                {
                    if (surface != null) surface.Dispose();
                    surface = new Glass.Surface(new Size(ClientSize.Width, Math.Max(ClientSize.Height, contentH)));
                    shaped = Size.Empty;
                }
                if (shaped != ClientSize)   // the shape follows the window's height; a region is cheap next to a new surface
                {
                    using (var p = Theme.Round(all, S(26))) Glass.SetShape(Handle, surface.G, p);
                    shaped = ClientSize;
                }
                var g = surface.G;
                g.Clear(Color.Transparent);
                Glass.Panel(g, all, S(26));
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
                // The list scrolls inside a clip that keeps a half-scrolled row in the card.
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
                    Theme.Fill(g, new RectangleF(list.Right - S(5), thumbY, S(3), thumbH), S(3) / 2f, Theme.Fade(Color.FromArgb(150, 255, 255, 255), thumb.Value));
                }
                FocusRing(g);
                Glass.Rim(g, all, S(26));
                bool blur = Glass.Transparency && fade >= 0.5;
                if (blur != blurOn) { Glass.SetBlur(Handle, blur); blurOn = blur; }
                surface.Present(Handle, Location, ClientSize, fade);
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
                if (h == ClientSize.Height || h <= 0) return;
                ClientSize = new Size(ClientSize.Width, h);
                final = Corner(out slide);
            }

            /// <summary>A row lights up as a rounded bar under the mouse.</summary>
            void RowBg(Graphics g, Rectangle rc, Look lk)
            {
                if (lk.Hover < 0.01) return;
                Color c = Theme.Mix(Theme.None, Theme.Mix(Theme.ChipHover, Theme.Press, lk.Press), lk.Hover);
                Theme.Fill(g, new RectangleF(rc.X + S(8), rc.Y + S(2), rc.Width - S(16), rc.Height - S(4)), S(10), c);
            }

            /// <summary>A round button with a symbol. rest: its fill when idle – the colour behind it for one without a fill.</summary>
            void Button(Graphics g, Rectangle rc, Look lk, Color rest, string symbol, Font font, Color fg)
            {
                Color fill = Theme.Mix(Theme.Mix(rest, Theme.Hover, lk.Hover), Theme.Press, lk.Press);
                Theme.Fill(g, rc, rc.Height / 2f, fill);
                Glass.Edge(g, rc, rc.Height / 2f, fill.A / (double)Theme.Raised.A);
                Draw(g, symbol, font, fg, rc, TfCenter);
            }

            static void Draw(Graphics g, string s, Font f, Color c, Rectangle r, TextFormatFlags flags)
            {
                Glass.Text(g, s, f, r, c, flags);
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

            /// <summary>Rolling text in r: align -1 left, 0 centred, 1 right. At rest and too wide, it is shortened like any other text.</summary>
            void DrawRoll(Graphics g, RollText rt, Font f, Color fg, Rectangle r, int align)
            {
                float w = rt.Width;
                if (w > r.Width && !rt.Moving)
                {
                    Draw(g, rt.Text, f, fg, r, align < 0 ? TfLeft : align > 0 ? TfLeft | TextFormatFlags.Right : TfCenter);
                    return;
                }
                float x = align < 0 ? r.X : align > 0 ? r.Right - w : r.X + (r.Width - w) / 2;
                DrawRollAt(g, rt, f, fg, r, x);
            }

            /// <summary>
            /// Rolling text from x, centred on clip's height and kept inside it. A character fades as it rolls, and its
            /// trailing copy (the motion blur) is fainter still.
            /// </summary>
            static void DrawRollAt(Graphics g, RollText rt, Font f, Color fg, Rectangle clip, float x)
            {
                const TextFormatFlags F = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
                int lineH = f.Height;
                float y = clip.Y + (clip.Height - lineH) / 2f;
                var state = g.Save();
                g.SetClip(clip, CombineMode.Intersect);
                rt.Draw(x, y, lineH, (ch, gx, gy, alpha, smear) =>
                {
                    var at = new Point((int)Math.Round(gx), (int)Math.Round(gy));
                    if (smear != 0) Glass.TextAt(g, ch, f, new Point(at.X, at.Y + (int)Math.Round(smear)), Theme.Fade(fg, alpha * 0.35), F);
                    Glass.TextAt(g, ch, f, at, Theme.Fade(fg, alpha), F);
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
                SetHover(HitTest(e.Location));
            }
            protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); SetHover(-1); }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                if (e.Button != MouseButtons.Left) return;
                pressed = HitTest(e.Location);
                Aim();
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                base.OnMouseUp(e);
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

        /// <summary>The file the flyout's pencil opens: the one behind what its list shows.</summary>
        internal static string FlyoutFile(bool choosing, bool brightening)
        {
            return !choosing ? ListFile : brightening ? BrightnessFile : ProfilesFile;
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
