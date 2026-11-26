// The island: a small pill that drops in at the top-centre of the screen when something happens – HDR came on,
// a game closed, an update is there – and leaves again by itself. Compact (a dot and a few words), expanded (a title
// and a second line), or split (the pill and a small bubble beside it).
//
// It must never get in a game's way, so:
//  - It is a layered window drawn through UpdateLayeredWindow: smooth edges, truly transparent around the pill, and no
//    painting messages. It never takes focus (WS_EX_NOACTIVATE, MA_NOACTIVATE) and has no taskbar or Alt+Tab entry.
//  - It only shows while Windows says notifications are welcome (SHQueryUserNotificationState). With a game in
//    exclusive fullscreen, any fullscreen window in front, or presentation mode it stays away: what it had to say
//    waits up to 10 s for a better moment, then goes out as an ordinary notification instead, if it has one.
//  - While a game runs, clicks go straight through it (WS_EX_TRANSPARENT).
// Hidden means hidden: no window on screen, no timer, no bitmap.
//
// It looks like the flyout (Palette.cs): Windows 11's solid flyout surface in the Windows mode, its 1 px stroke and a
// soft shadow, Windows' text colours, the accent for what is on, amber only for HDR, and a contrast theme's own colours
// with a 2 px border. A change of mode, accent or contrast theme repaints it while it shows.
//
// Text is GDI+'s grayscale antialiasing, drawn straight onto the opaque pill. GDI (TextRenderer) would write alpha 0
// into a 32-bit surface and has no per-character alpha, which the rolling text and the crossfades need; ClearType has no
// meaning on a window with per-pixel alpha, whatever draws it.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace HdrSwitch
{
    sealed class Island : Form
    {
        /// <summary>What a dot says. The colour comes from the palette when it is drawn (DotColor).</summary>
        public enum Mark
        {
            On,         // something this app switched is on (a resolution), or plain news: the accent
            Hdr,        // HDR is on: amber
            Off,        // SDR, nothing on: grey
            Paused,     // the caution colour
            Critical    // a recording app, a failed update: the critical colour (red)
        }

        /// <summary>One thing to show. The newest is on screen; when it ends, the one before it comes back.</summary>
        public sealed class Act
        {
            public string Id;               // starting another with the same Id replaces this one
            public string Title, Sub;       // expanded, held for 2.5 s first – Sub == null: compact from the start
            public string Text;             // compact
            public Func<string> Live;       // compact text that changes while it shows ("SDR in 3s")
            public Mark Dot = Mark.On;
            public bool Split;              // compact, with a bubble beside the pill
            public Mark Bubble = Mark.Off;
            public bool Hold;               // stays until End(Id)
            public double Seconds = 3;      // how long it stays compact otherwise
            public string Balloon;          // said as a notification instead, if the island can't show in time
            internal bool Big;
            internal double Left;
            internal int Waited;
        }

        /// <summary>What is drawn: a snapshot of an Act, so the previous one can fade out as the next fades in.</summary>
        sealed class View
        {
            public bool Big, Split;
            public string Title, Sub, Text;
            public Mark Dot, Bubble;
        }

        const double BigSeconds = 2.5;
        const int WaitSeconds = 10;   // Poll comes once a second

        readonly Action click, hidden;
        readonly Func<bool> inGame;
        readonly Func<Screen> where;
        readonly Action<string> balloon;
        readonly List<Act> stack = new List<Act>(), pending = new List<Act>();
        readonly System.Windows.Forms.Timer life = new System.Windows.Forms.Timer { Interval = 200 };   // runs only while it shows
        readonly Stopwatch lifeClock = new Stopwatch();
        readonly Spring w = Motion.Shape(0, 0.25), h = Motion.Shape(0, 0.25), r = Motion.Shape(0, 0.25), drop = Motion.Shape(0, 0.25);
        readonly Spring bubble = Motion.Shape(0, 0.004), alpha = Motion.Fade(0), content = Motion.Fade(1);
        readonly Func<double, bool> tick;
        readonly StringFormat fmt = new StringFormat(StringFormat.GenericTypographic) { Trimming = StringTrimming.EllipsisCharacter, LineAlignment = StringAlignment.Center };
        readonly StringFormat glyphFmt = new StringFormat(StringFormat.GenericTypographic);   // one character of rolling text, from its top-left
        readonly Dictionary<string, float> glyphW = new Dictionary<string, float>();
        RollText roll;   // the compact text: one compact message turning into the next rolls instead of crossfading
        readonly Spring dotMix = Motion.Fade(1);   // while the text rolls, the dot (and the text) turn from …From to dotTo ("SDR in 1s" amber → "SDR" grey)
        Color dotFrom, textFrom;
        Mark dotTo;
        readonly UserPreferenceChangedEventHandler prefs;

        float k = 1;
        Theme.Fonts fonts;
        Size size;                       // the window: wide enough for the largest pill, as tall as it plus its way in
        Surface surface;                 // what UpdateLayeredWindow shows
        Graphics g;                      // draws on it
        RectangleF drawnPill, drawnBubble;   // where the last frame put them, in window coordinates
        View cur, prev;
        bool leaving, hovering, through = true;

        /// <summary>
        /// click: the island was clicked (never while inGame says a game runs). where: the screen to show on.
        /// balloon: says a text as a notification. hidden: it has just left the screen.
        /// </summary>
        public Island(Action click, Func<bool> inGame, Func<Screen> where, Action<string> balloon, Action hidden)
        {
            this.click = click;
            this.inGame = inGame;
            this.where = where;
            this.balloon = balloon;
            this.hidden = hidden;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Text = "Display Switch";
            fmt.FormatFlags |= StringFormatFlags.NoWrap;
            glyphFmt.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces;
            tick = Tick;
            life.Tick += (s, e) => Live();
            prefs = (s, e) =>
            {
                if (e.Category == UserPreferenceCategory.Color || e.Category == UserPreferenceCategory.General || e.Category == UserPreferenceCategory.Accessibility)
                    ThemeChanged();
            };
            SystemEvents.UserPreferenceChanged += prefs;   // a contrast theme, and colours on systems that don't broadcast the messages below
        }

        /// <summary>The colours in use: Windows' mode, accent and contrast theme (Palette.Load).</summary>
        static Palette P { get { return Palette.Current; } }

        /// <summary>Windows' mode, accent or contrast theme changed: the colours are read again and, if it shows, it is painted in them.</summary>
        void ThemeChanged()
        {
            if (IsDisposed) return;
            Palette.Load();
            if (Visible && g != null) Render();
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.Style = unchecked((int)0x80000000);   // WS_POPUP
                cp.ExStyle |= 0x80000 | 0x08000000 | 0x80 | 0x8 | 0x20;   // WS_EX_LAYERED, NOACTIVATE, TOOLWINDOW, TOPMOST, TRANSPARENT
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_SETTINGCHANGE = 0x1A, WM_SYSCOLORCHANGE = 0x15, WM_THEMECHANGED = 0x31A, WM_DWMCOLORIZATIONCOLORCHANGED = 0x320;
            if (m.Msg == 0x21) { m.Result = (IntPtr)3; return; }   // WM_MOUSEACTIVATE → MA_NOACTIVATE: a click never takes focus
            base.WndProc(ref m);
            if (m.Msg == WM_DWMCOLORIZATIONCOLORCHANGED || m.Msg == WM_SYSCOLORCHANGE || m.Msg == WM_THEMECHANGED) ThemeChanged();
            else if (m.Msg == WM_SETTINGCHANGE && m.LParam != IntPtr.Zero)
            {
                string area = null;
                try { area = System.Runtime.InteropServices.Marshal.PtrToStringUni(m.LParam); } catch { }
                if (area == "ImmersiveColorSet") ThemeChanged();   // light/dark mode or the accent colour
            }
        }

        // ---- activities ----

        /// <summary>Shows this now, on top of whatever is showing – or keeps it for a better moment (see the top of this file).</summary>
        public void Start(Act a)
        {
            Remove(a.Id);
            a.Big = a.Sub != null;
            a.Left = a.Big ? BigSeconds : a.Seconds;
            if (!Welcome())
            {
                a.Waited = 0;
                pending.Add(a);
                return;
            }
            stack.Add(a);
            Present();
        }

        /// <summary>Ends an activity. If it was the one showing, the one before it returns, or the island leaves.</summary>
        public void End(string id)
        {
            bool top = stack.Count > 0 && stack[stack.Count - 1].Id == id;
            Remove(id);
            if (!top) return;
            if (stack.Count > 0) Present();
            else Depart();
        }

        /// <summary>Drops everything: the island was switched off.</summary>
        public void Clear()
        {
            stack.Clear();
            pending.Clear();
            Depart();
        }

        /// <summary>Called once a second by the app's process scan: no timer of its own while hidden.</summary>
        public void Poll()
        {
            if (pending.Count == 0) return;
            if (Welcome())
            {
                stack.AddRange(pending);
                pending.Clear();
                Present();
                return;
            }
            foreach (var a in pending.ToArray())
            {
                if (++a.Waited < WaitSeconds) continue;
                pending.Remove(a);
                if (a.Balloon != null && balloon != null) balloon(a.Balloon);
            }
        }

        /// <summary>The display changed (a competitive resolution, a monitor): move to the new top-centre.</summary>
        public void Relayout()
        {
            if (!Visible || leaving || stack.Count == 0) return;
            Place();
            Present();
        }

        void Remove(string id)
        {
            stack.RemoveAll(a => a.Id == id);
            pending.RemoveAll(a => a.Id == id);
        }

        /// <summary>Windows would show a notification right now: nothing fullscreen in front, no presentation, a user at the desk.</summary>
        static bool Welcome()
        {
            int state;
            try { if (Native.SHQueryUserNotificationState(out state) != 0) return true; }
            catch (EntryPointNotFoundException) { return true; }
            return state == 5 || state == 6;   // QUNS_ACCEPTS_NOTIFICATIONS, QUNS_QUIET_TIME (only toasts are held back)
        }

        // ---- showing, morphing, leaving ----

        /// <summary>Puts the newest activity on screen: the pill morphs to its shape while the old content fades into the new.</summary>
        void Present()
        {
            var a = stack[stack.Count - 1];
            if (a.Live != null) a.Text = a.Live();
            bool wasVisible = Visible;
            if (!wasVisible)
            {
                Motion.Refresh();
                Palette.Load();
                Place();
            }
            var v = new View { Big = a.Big, Split = a.Split && !a.Big, Title = a.Title, Sub = a.Sub, Text = a.Text ?? a.Title, Dot = a.Dot, Bubble = a.Bubble };
            float tw, th, tr;
            Measure(v, out tw, out th, out tr);
            if (!wasVisible)
            {
                // It grows out of a small pill that slides down from the screen's edge.
                w.Snap(tw * 0.6); h.Snap(th); r.Snap(tr);
                drop.Snap(-S(28)); alpha.Snap(0); bubble.Snap(0);
                prev = null;
            }
            // Compact to compact ("SDR in 3s" → "SDR in 2s" → "SDR"): the text rolls and the pill follows its width.
            // Anything else – into or out of the expanded shape, a bubble coming or going – crossfades.
            bool rolls = wasVisible && !leaving && cur != null && !cur.Big && !v.Big && cur.Split == v.Split;
            if (wasVisible) prev = leaving || rolls ? null : cur;
            if (rolls) roll.Set(v.Text);
            else roll.Snap(v.Text);
            if (!rolls) { dotTo = v.Dot; dotFrom = DotColor(v.Dot, P); textFrom = TextColor(v.Dot, P); dotMix.Snap(1); }
            else if (v.Dot != dotTo)
            {
                dotFrom = DotNow();
                textFrom = TextNow();
                dotTo = v.Dot;
                dotMix.Snap(0);
                dotMix.To(1);
            }
            cur = v;
            leaving = false;
            w.To(tw); h.To(th); r.To(tr); drop.To(0); alpha.To(1);
            bubble.To(v.Split ? 1 : 0);
            content.Snap(prev == null ? 1 : 0);
            content.To(1);
            if (!wasVisible)
            {
                Visible = true;   // SW_SHOWNOACTIVATE, see ShowWithoutActivation
                lifeClock.Restart();
                life.Start();
            }
            SetThrough(inGame());
            Render();
            Motion.Run(tick);
        }

        void Depart()
        {
            if (!Visible) return;
            leaving = true;
            alpha.To(0);
            drop.To(-S(20));
            w.To(w.Value * 0.7);
            if (Motion.Reduced) Vanish();
            else Motion.Run(tick);
        }

        void Vanish()
        {
            leaving = false;
            hovering = false;
            life.Stop();
            Visible = false;
            FreeSurface();
            cur = prev = null;
            if (hidden != null) hidden();
        }

        bool Tick(double dt)
        {
            if (!Visible) return false;
            bool moving = w.Step(dt) | h.Step(dt) | r.Step(dt) | drop.Step(dt) | bubble.Step(dt) | alpha.Step(dt) | content.Step(dt) | roll.Step(dt) | dotMix.Step(dt);
            if (leaving && alpha.Value < 0.02) { Vanish(); return false; }
            Render();
            return moving;
        }

        /// <summary>
        /// Five times a second while it shows: counts the showing activity down (not while the mouse rests on it),
        /// refreshes a live text, and leaves at once if something went fullscreen meanwhile.
        /// </summary>
        void Live()
        {
            double dt = lifeClock.Elapsed.TotalSeconds;
            lifeClock.Restart();
            if (leaving || stack.Count == 0) return;
            if (!Welcome()) { stack.Clear(); Depart(); return; }
            SetThrough(inGame());
            var a = stack[stack.Count - 1];
            if (a.Live != null && a.Live() != cur.Text) Present();
            if (hovering && !through) return;
            a.Left -= dt;
            if (a.Left > 0) return;
            if (a.Big) { a.Big = false; a.Left = a.Seconds; Present(); }
            else if (!a.Hold) End(a.Id);
        }

        void SetThrough(bool on)
        {
            if (on == through || !IsHandleCreated) return;
            through = on;
            int ex = Native.GetWindowLong(Handle, -20);   // GWL_EXSTYLE
            Native.SetWindowLong(Handle, -20, on ? ex | 0x20 : ex & ~0x20);
            if (on) hovering = false;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            through = true;   // as created, see CreateParams
        }

        // The window takes the mouse wherever it drew something, its faint shadow included; only the pill and the
        // bubble count as the island, as when the window's region was the pill.
        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); hovering = OnShape(e.Location); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hovering = false; }

        bool OnShape(Point at) { return drawnPill.Contains(at) || drawnBubble.Contains(at); }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left || through || !OnShape(e.Location)) return;
            stack.Clear();
            Depart();
            if (click != null) click();
        }

        // ---- layout and drawing ----

        int S(double v) { return (int)Math.Round(v * k); }

        /// <summary>Top-centre of the screen, under a taskbar that sits at the top. Fonts and the surface follow the screen's scale.</summary>
        void Place()
        {
            var scr = where() ?? Screen.PrimaryScreen;
            var b = scr.Bounds;
            float scale = Theme.Scale(new Point(b.X + b.Width / 2, b.Y + b.Height / 2));
            if (fonts == null || scale != k)
            {
                if (fonts != null) fonts.Dispose();
                k = scale;
                fonts = new Theme.Fonts(k);
                glyphW.Clear();
                roll = null;
            }
            if (roll == null) roll = new RollText(GlyphWidth);
            var s = new Size(Math.Min(b.Width, S(540)), S(10) + S(64) + S(12));
            Bounds = new Rectangle(b.X + (b.Width - s.Width) / 2, Math.Max(b.Top, scr.WorkingArea.Top), s.Width, s.Height);
            if (s != size || surface == null)
            {
                size = s;
                CreateSurface();
            }
        }

        void Measure(View v, out float tw, out float th, out float tr)
        {
            float max = size.Width - S(36) - S(6) - S(8);
            if (v.Big)
            {
                float text = Math.Max(TextWidth(v.Title, fonts.IslandTitle), TextWidth(v.Sub, fonts.IslandSub));
                tw = Math.Min(max, Math.Max(S(300), S(18) + S(10) + S(14) + text + S(24)));
                th = S(64);
                tr = S(28);
            }
            else
            {
                tw = Math.Min(max, S(12) + S(8) + S(8) + TextWidth(v.Text, fonts.IslandText) + S(16));
                th = S(36);
                tr = S(18);
            }
        }

        float TextWidth(string s, Font f) { return (float)Math.Ceiling(g.MeasureString(s ?? "", f, 100000, fmt).Width) + 1; }

        /// <summary>One character's advance in the compact font, for the rolling text.</summary>
        float GlyphWidth(string ch)
        {
            float wd;
            if (string.IsNullOrEmpty(ch) || g == null) return 0;
            if (!glyphW.TryGetValue(ch, out wd))
            {
                wd = g.MeasureString(ch, fonts.IslandText, 100000, glyphFmt).Width;
                glyphW[ch] = wd;
            }
            return wd;
        }

        void Render()
        {
            if (g == null) return;
            Paint();
            surface.Present(Handle, Location, alpha.Value);
        }

        /// <summary>
        /// The frame as it is now, onto g: the shadow, the opaque pill (and bubble) in the flyout's surface colour, the
        /// content, and the stroke on top. Hit-testing follows the drawn alpha, so no window region is needed.
        /// </summary>
        void Paint()
        {
            var p = P;
            g.Clear(Color.Transparent);
            float W = (float)w.Value, H = (float)h.Value, R = (float)Math.Min(r.Value, H / 2), b = (float)Math.Max(0, bubble.Value);
            // While compact text rolls, the pill is never narrower than the text is right now: the spring would otherwise
            // reach the new width first and cut off the characters still rolling out.
            if (cur != null && !cur.Big && prev == null && !leaving && roll.Moving)
                W = Math.Max(W, Math.Min(size.Width - S(36) - S(6) - S(8), S(12) + S(8) + S(8) + roll.Width + S(16)));
            float bd = S(36), gap = S(6);
            float x = (size.Width - W - b * (gap + bd)) / 2, y = S(10) + (float)drop.Value;
            var pill = new RectangleF(x, y, W, H);
            var br = RectangleF.Empty;
            float d = 0;
            if (b > 0.02f)
            {
                d = bd * Math.Min(1, b);
                br = new RectangleF(pill.Right + gap * Math.Min(1, b), y + (S(36) - d) / 2, d, d);
            }
            drawnPill = pill;
            drawnBubble = br;
            if (W < 2 || H < 2) return;
            Shadow(pill, R, p);
            if (!br.IsEmpty) Shadow(br, d / 2, p);
            Theme.Fill(g, pill, R, p.Surface);
            using (var clip = Theme.Round(pill, R))
            {
                g.SetClip(clip);   // text that is still too wide for a growing pill stays inside it
                double c = Math.Max(0, Math.Min(1, content.Value));
                if (prev != null && c < 1) Draw(prev, pill, 1 - c, p);
                Draw(cur, pill, c, p);
                g.ResetClip();
            }
            Edge(pill, R, p);
            if (!br.IsEmpty)
            {
                Theme.Fill(g, br, d / 2, p.Surface);
                Edge(br, d / 2, p);
                Theme.Dot(g, br.X + d / 2, br.Y + d / 2, S(10) * Math.Min(1, b), DotColor(cur.Bubble, p));
            }
        }

        /// <summary>
        /// A soft shadow under a shape, a little lower than it, like Windows' own flyouts: rings of faint black, each
        /// a little larger, so the darkness fades out over 8 px. Under the opaque pill only the part outside it shows.
        /// </summary>
        void Shadow(RectangleF r, float radius, Palette p)
        {
            int layers = 8, peak = p.Mode == Palette.Kind.Light ? 9 : 16;   // together about 19 % (light) or 30 % (dark) black at the edge
            float spread = S(8), dy = S(2);
            for (int i = layers; i >= 1; i--)
            {
                float grow = spread * i / layers;
                var s = new RectangleF(r.X - grow, r.Y - grow + dy, r.Width + 2 * grow, r.Height + 2 * grow);
                Theme.Fill(g, s, radius + grow, Color.FromArgb(peak * (layers - i + 1) / layers + 2, 0, 0, 0));
            }
        }

        /// <summary>The flyout's stroke just inside the shape: 1 px, or 2 px of the text colour in a contrast theme.</summary>
        void Edge(RectangleF r, float radius, Palette p)
        {
            float sw = p.HighContrast ? Math.Max(2, S(2)) : 1;
            if (p.Stroke.A == 0 || r.Width <= sw || r.Height <= sw) return;
            using (var path = Theme.Round(RectangleF.Inflate(r, -sw / 2, -sw / 2), Math.Max(0, radius - sw / 2)))
            using (var pen = new Pen(p.Stroke, sw)) g.DrawPath(pen, path);
        }

        Color DotNow() { return Theme.Mix(dotFrom, DotColor(dotTo, P), dotMix.Value); }
        Color TextNow() { return Theme.Mix(textFrom, TextColor(dotTo, P), dotMix.Value); }

        void Draw(View v, RectangleF pill, double a, Palette p)
        {
            if (a <= 0.01) return;
            if (v.Big)
            {
                float tx = pill.X + S(18) + S(10) + S(14), tw = pill.Right - S(24) - tx;
                Theme.Dot(g, pill.X + S(18) + S(5), pill.Y + pill.Height / 2, S(10), Theme.Fade(v == cur ? DotNow() : DotColor(v.Dot, p), a));
                Say(v.Title, fonts.IslandTitle, Theme.Fade(p.Text1, a), new RectangleF(tx, pill.Y + S(11), tw, S(22)), fmt);
                Say(v.Sub, fonts.IslandSub, Theme.Fade(SubColor(v.Dot, p), a), new RectangleF(tx, pill.Y + S(33), tw, S(20)), fmt);
            }
            else
            {
                float tx = pill.X + S(12) + S(8) + S(8), tw = pill.Right - S(14) - tx;
                Theme.Dot(g, pill.X + S(12) + S(4), pill.Y + pill.Height / 2, S(8), Theme.Fade(v == cur ? DotNow() : DotColor(v.Dot, p), a));
                Color ink = v == cur ? TextNow() : TextColor(v.Dot, p);
                if (v == cur && (roll.Moving || roll.TargetWidth <= tw))
                {
                    float lineH = fonts.IslandText.Height, y = pill.Y + (pill.Height - lineH) / 2;
                    roll.Draw(tx, y, lineH, (ch, gx, gy, ga, smear) =>
                    {
                        // The trailing copy stands in for motion blur, which GDI+ can't do cheaply.
                        if (smear != 0) Say(ch, fonts.IslandText, Theme.Fade(ink, a * ga * 0.35), new RectangleF(gx, gy + smear, 0, 0), glyphFmt);
                        Say(ch, fonts.IslandText, Theme.Fade(ink, a * ga), new RectangleF(gx, gy, 0, 0), glyphFmt);
                    });
                }
                else Say(v.Text, fonts.IslandText, Theme.Fade(ink, a), new RectangleF(tx, pill.Y, tw, pill.Height), fmt);
            }
        }

        /// <summary>Text on the opaque pill. An empty box (no width) draws from its top-left point.</summary>
        void Say(string s, Font f, Color c, RectangleF r, StringFormat sf)
        {
            if (string.IsNullOrEmpty(s) || c.A == 0) return;
            using (var fg = new SolidBrush(c))
            {
                if (r.Width <= 0) g.DrawString(s, f, fg, r.X, r.Y, sf);
                else g.DrawString(s, f, fg, r, sf);
            }
        }

        // ---- the colours (tested) ----

        /// <summary>
        /// A dot's colour: amber for HDR, the critical colour for a recording app or a failure, the caution colour for
        /// Paused, grey (tertiary text) for SDR, the accent for anything else that is on. In a contrast theme: the
        /// highlight colour for HDR and what is on, the text colour for the rest (the words say which is which).
        /// </summary>
        public static Color DotColor(Mark m, Palette p)
        {
            switch (m)
            {
                case Mark.Hdr: return p.HighContrast ? p.Accent : p.HdrDot;
                case Mark.Critical: return p.Critical;
                case Mark.Paused: return p.Caution;
                case Mark.Off: return p.Text3;
                default: return p.Accent;
            }
        }

        /// <summary>The compact text: HDR's text tint while HDR is on (not in a contrast theme), else primary text.</summary>
        public static Color TextColor(Mark m, Palette p)
        {
            return m == Mark.Hdr && !p.HighContrast ? p.HdrText : p.Text1;
        }

        /// <summary>The expanded shape's second line ("HDR on · 2368 × 1332"): HDR's text tint, else secondary text.</summary>
        public static Color SubColor(Mark m, Palette p)
        {
            return m == Mark.Hdr && !p.HighContrast ? p.HdrText : p.Text2;
        }

        // ---- the surface ----

        void CreateSurface()
        {
            FreeSurface();
            surface = new Surface(size);
            g = surface.G;
        }

        void FreeSurface()
        {
            if (surface != null) { surface.Dispose(); surface = null; }
            g = null;
        }

        static void Prepare(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;   // grayscale: ClearType has no meaning with per-pixel alpha
        }

        /// <summary>
        /// A top-down 32-bit premultiplied DIB that GDI+ draws into directly and UpdateLayeredWindow reads from: no copy
        /// per frame.
        /// </summary>
        sealed class Surface : IDisposable
        {
            public readonly Size Size;
            public readonly Graphics G;
            readonly Bitmap bitmap;
            IntPtr dc, dib, old;

            public Surface(Size size)
            {
                Size = size;
                IntPtr bits;
                var bi = new Native.BitmapInfo { Size = 40, Width = size.Width, Height = -size.Height, Planes = 1, BitCount = 32 };
                dc = Native.CreateCompatibleDC(IntPtr.Zero);
                dib = Native.CreateDIBSection(dc, ref bi, 0, out bits, IntPtr.Zero, 0);
                old = Native.SelectObject(dc, dib);
                bitmap = new Bitmap(size.Width, size.Height, size.Width * 4, PixelFormat.Format32bppPArgb, bits);
                G = Graphics.FromImage(bitmap);
                Prepare(G);
            }

            /// <summary>Shows what was drawn, at this screen point, faded by alpha (0 → 1).</summary>
            public void Present(IntPtr hwnd, Point at, double alpha)
            {
                G.Flush(FlushIntention.Sync);
                var blend = new Native.BlendFunction { SourceConstantAlpha = (byte)Math.Round(255 * Math.Max(0, Math.Min(1, alpha))), AlphaFormat = 1 };   // AC_SRC_ALPHA
                Point src = Point.Empty;
                Size sz = Size;
                Native.UpdateLayeredWindow(hwnd, IntPtr.Zero, ref at, ref sz, dc, ref src, 0, ref blend, 2);   // ULW_ALPHA
            }

            public void Dispose()
            {
                G.Dispose();
                bitmap.Dispose();
                if (dc != IntPtr.Zero)
                {
                    Native.SelectObject(dc, old);
                    Native.DeleteObject(dib);
                    Native.DeleteDC(dc);
                    dc = dib = old = IntPtr.Zero;
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                SystemEvents.UserPreferenceChanged -= prefs;   // a static event: it would keep this alive
                life.Dispose();
                FreeSurface();
                if (fonts != null) fonts.Dispose();
                fmt.Dispose();
                glyphFmt.Dispose();
            }
            base.Dispose(disposing);
        }

        // ---- the render harness (--render-island) ----

        /// <summary>The sample acts of --render-island, as the app announces them. compact: in the compact shape from the start.</summary>
        internal static Act SampleAct(string name, bool compact)
        {
            Act a;
            switch (name)
            {
                case "hdr": a = new Act { Id = "game", Title = "Cyberpunk 2077", Sub = "HDR on · 2368 × 1332", Text = "HDR on", Dot = Mark.Hdr }; break;
                case "resolution": a = new Act { Id = "game", Title = "Counter-Strike 2", Sub = "2368 × 1332", Text = "2368 × 1332", Dot = Mark.On }; break;
                case "sdr-countdown": a = new Act { Id = "sdr", Text = "SDR in 3s", Dot = Mark.Hdr }; break;
                case "sdr": a = new Act { Id = "sdr", Text = "SDR", Dot = Mark.Off }; break;
                case "paused": a = new Act { Id = "pause", Text = "Paused", Dot = Mark.Paused }; break;
                case "resumed": a = new Act { Id = "pause", Text = "Resumed", Dot = Mark.On }; break;
                case "recording": a = new Act { Id = "rec", Text = "OBS Studio · SDR", Dot = Mark.Off, Split = true, Bubble = Mark.Critical }; break;
                case "update": a = new Act { Id = "update", Title = "Version 1.9.0 is available", Sub = "Click to update", Text = "Update available", Dot = Mark.On }; break;
                case "failed": a = new Act { Id = "update", Text = "Update failed", Dot = Mark.Critical }; break;
                default: return null;
            }
            a.Big = a.Sub != null && !compact;
            return a;
        }

        /// <summary>
        /// HdrSwitch.exe --render-island out.png [--theme dark|light|hc] [--act hdr|resolution|sdr-countdown|sdr|paused|
        /// resumed|recording|update|failed] [--compact] [--scale 1.5]: draws the island at rest for one act into a PNG,
        /// over a mid-grey backdrop so its shadow shows, and exits. Its look can be checked without Windows (under Mono,
        /// say). Never part of a normal run. --compact: the compact shape of an act that starts expanded.
        /// </summary>
        internal static int RenderSample(string[] args)
        {
            Func<string, string, string> arg = (name, unset) =>
            {
                int i = Array.IndexOf(args, name);
                return i >= 0 && i + 1 < args.Length ? args[i + 1] : unset;
            };
            string path = arg("--render-island", "island.png"), actName = arg("--act", "hdr");
            Palette.Current = Palette.Sample(arg("--theme", "dark"));
            float scale;
            if (!float.TryParse(arg("--scale", "1"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out scale) || scale <= 0) scale = 1;
            var a = SampleAct(actName, Array.IndexOf(args, "--compact") >= 0);
            if (a == null) { Console.Error.WriteLine("Unknown --act " + actName); return 2; }
            using (var island = new Island(null, () => false, null, null, null))
            using (var bmp = island.Snapshot(a, scale))
                bmp.Save(path, ImageFormat.Png);
            Console.WriteLine("Wrote " + path);
            return 0;
        }

        /// <summary>The island at rest showing this act, drawn as Render draws it, over a mid-grey backdrop.</summary>
        Bitmap Snapshot(Act a, float scale)
        {
            k = scale;
            fonts = new Theme.Fonts(k);
            size = new Size(S(540), S(10) + S(64) + S(12));
            int pad = S(16);
            var output = new Bitmap(size.Width + 2 * pad, size.Height + 2 * pad, PixelFormat.Format32bppArgb);
            using (var layer = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb))
            {
                g = Graphics.FromImage(layer);
                Prepare(g);
                roll = new RollText(GlyphWidth);
                var v = new View { Big = a.Big, Split = a.Split && !a.Big, Title = a.Title, Sub = a.Sub, Text = a.Text ?? a.Title, Dot = a.Dot, Bubble = a.Bubble };
                float tw, th, tr;
                Measure(v, out tw, out th, out tr);
                w.Snap(tw); h.Snap(th); r.Snap(tr); drop.Snap(0); alpha.Snap(1); content.Snap(1);
                bubble.Snap(v.Split ? 1 : 0);
                cur = v;
                prev = null;
                roll.Snap(v.Text);
                dotTo = v.Dot; dotFrom = DotColor(v.Dot, P); textFrom = TextColor(v.Dot, P); dotMix.Snap(1);
                Paint();
                g.Dispose();
                g = null;
                using (var o = Graphics.FromImage(output))
                {
                    using (var back = new LinearGradientBrush(new Rectangle(0, 0, output.Width, output.Height), Color.FromArgb(0x86, 0x8C, 0x94), Color.FromArgb(0x6A, 0x70, 0x78), 90f))
                        o.FillRectangle(back, 0, 0, output.Width, output.Height);
                    o.DrawImage(layer, pad, pad, layer.Width, layer.Height);
                }
            }
            return output;
        }

        // ---- preview ----

        /// <summary>
        /// --island-demo: every state in turn on the screen the mouse is on, three rounds; a click on the island ends
        /// it early. Nothing else of the app runs – no tray icon, no HDR switching – so it can run beside an installed copy.
        /// </summary>
        public static void Demo()
        {
            int step = 0;
            var until = Stopwatch.StartNew();
            var island = new Island(Application.Exit, () => false, () => Screen.FromPoint(Cursor.Position), null, null);
            var next = new System.Windows.Forms.Timer { Interval = 500 };
            next.Tick += (s, e) =>
            {
                switch (step++ % 7)
                {
                    case 0:
                        island.Start(new Act { Id = "hdr", Title = "Cyberpunk 2077", Sub = "HDR on · 2368 × 1332", Text = "HDR on", Dot = Mark.Hdr });
                        next.Interval = 6500;
                        break;
                    case 1:
                        until.Restart();
                        island.Start(new Act { Id = "sdr", Hold = true, Dot = Mark.Hdr, Live = () => "SDR in " + Math.Max(1, 3 - (int)until.Elapsed.TotalSeconds) + "s" });
                        next.Interval = 3000;
                        break;
                    case 2:
                        island.Start(new Act { Id = "sdr", Text = "SDR", Dot = Mark.Off, Seconds = 1.5 });
                        next.Interval = 2800;
                        break;
                    case 3:
                        island.Start(new Act { Id = "rec", Text = "OBS Studio · SDR", Dot = Mark.Off, Split = true, Bubble = Mark.Critical, Seconds = 4 });
                        next.Interval = 5000;
                        break;
                    case 4:
                        island.Start(new Act { Id = "update", Title = "Version 1.5.0 is available", Sub = "Click to update", Text = "Update available" });
                        next.Interval = 6500;
                        break;
                    case 5:
                        island.Start(new Act { Id = "hdr", Title = "Counter-Strike 2", Sub = "2368 × 1332", Text = "2368 × 1332", Dot = Mark.On });
                        next.Interval = 3500;   // the next one arrives while this is still up: it morphs instead of leaving
                        break;
                    case 6:
                        island.Start(new Act { Id = "scan", Text = "3 new games found", Balloon = "Found 3 new games." });
                        next.Interval = 4500;
                        if (step >= 21) { next.Stop(); var end = new System.Windows.Forms.Timer { Interval = 4000 }; end.Tick += (s2, e2) => Application.Exit(); end.Start(); }
                        break;
                }
            };
            next.Start();
            Application.Run();
            island.Dispose();
        }
    }
}
