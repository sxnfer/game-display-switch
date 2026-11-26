// Text that changes in place – a status, a count, a countdown, a button going busy – rolls to its new value one
// character at a time: what changed slides out while the new character slides in, each a moment after the one before
// it. Whatever stays the same stays put, so "SDR in 3s" → "SDR in 2s" moves only the digit. New text comes in from
// below and leaves upwards; a number that counts down turns the other way, like a counter.
//
// This class only does the motion: where each character is, how visible, how far it is smeared along its way, and how
// wide the whole text is right now (a pill around it can follow that). The drawing is the caller's, because the flyout
// draws GDI text on an opaque card and the island GDI+ text (it has per-character alpha) on a layered window. Neither has a cheap blur, so the
// motion blur is a second, fainter copy that trails a moving character.
//
// With Windows' "Animation effects" off it simply shows the new text (the springs snap, see Spring.To).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace HdrSwitch
{
    sealed class RollText
    {
        /// <summary>Draws one character with its left edge at x and its line's top at y. alpha 0…1; smear: where its trailing copy goes, in pixels.</summary>
        public delegate void Glyph(string ch, float x, float y, double alpha, float smear);

        sealed class Slot
        {
            public string From, To;
            public float WFrom, WTo;
            public Spring W;          // the slot's width, from the old character's to the new one's
            public Spring In, Out;    // in lines: the new character ±1 → 0, the old one 0 → ∓1 (Out == null: unchanged)
            public double Start;      // seconds after Set when it starts to move
            public int Dir;           // 1: in from below, out upwards; -1: the other way
            public bool Started;
        }

        const double Stagger = 0.026;   // between one changed character and the next
        const float Travel = 0.8f;      // how far a character moves, in lines

        readonly Func<string, float> measure;
        readonly List<Slot> slots = new List<Slot>();
        double clock;

        public string Text { get; private set; }

        /// <summary>For owners that keep them by key across a rebuild: still in use.</summary>
        public bool Seen;

        /// <summary>measure: the advance width of one character, in pixels.</summary>
        public RollText(Func<string, float> measure)
        {
            this.measure = measure;
            Text = "";
        }

        /// <summary>Shows this text at once, without rolling.</summary>
        public void Snap(string text)
        {
            text = text ?? "";
            Text = text;
            slots.Clear();
            foreach (string ch in Chars(text))
            {
                float w = measure(ch);
                slots.Add(new Slot { From = ch, To = ch, WFrom = w, WTo = w, W = Motion.Shape(w, 0.1), In = Motion.Roll(0), Started = true });
            }
        }

        /// <summary>Rolls to this text. The same text again does nothing, so it is safe to call on every rebuild.</summary>
        public void Set(string text)
        {
            text = text ?? "";
            if (text == Text) return;
            if (Motion.Reduced || slots.Count == 0) { Snap(text); return; }
            string[] a = Chars(Text), b = Chars(text);
            Text = text;

            // What both share at the start and at the end stays; the middle rolls, character by character.
            int p = 0;
            while (p < a.Length && p < b.Length && a[p] == b[p]) p++;
            int s = 0;
            while (s < a.Length - p && s < b.Length - p && a[a.Length - 1 - s] == b[b.Length - 1 - s]) s++;
            string[] ma = Sub(a, p, a.Length - p - s), mb = Sub(b, p, b.Length - p - s);
            int n = Math.Max(ma.Length, mb.Length);

            // Numbers line up from the right, like a counter, and turn downwards when they count down.
            long na, nb;
            bool numA = Number(ma, out na), numB = Number(mb, out nb);
            bool numeric = numA && numB;
            int dir = numeric && nb < na ? -1 : 1;
            int padA = numeric ? n - ma.Length : 0, padB = numeric ? n - mb.Length : 0;

            var old = new List<Slot>();   // the slots of the characters on screen now, one per character of the old text
            foreach (var sl in slots) if (sl.To.Length > 0) old.Add(sl);
            slots.Clear();
            for (int i = 0; i < p; i++) slots.Add(Keep(old, i, a[i]));
            for (int i = 0; i < n; i++)
            {
                string from = i - padA >= 0 && i - padA < ma.Length ? ma[i - padA] : "";
                string to = i - padB >= 0 && i - padB < mb.Length ? mb[i - padB] : "";
                if (from == to) { slots.Add(Fresh(to)); continue; }
                float wf = measure(from), wt = measure(to);
                slots.Add(new Slot
                {
                    From = from, To = to, WFrom = wf, WTo = wt,
                    W = Motion.Shape(wf, 0.1), In = Motion.Roll(dir), Out = Motion.Roll(0),
                    Start = clock + i * Stagger, Dir = dir,
                });
            }
            for (int i = a.Length - s; i < a.Length; i++) slots.Add(Keep(old, i, a[i]));
        }

        /// <summary>One frame. True while something still moves or waits for its turn.</summary>
        public bool Step(double dt)
        {
            clock += dt;
            bool moving = false;
            foreach (var sl in slots)
            {
                if (!sl.Started)
                {
                    if (clock < sl.Start) { moving = true; continue; }
                    sl.Started = true;
                    sl.W.To(sl.WTo);
                    sl.In.To(0);
                    sl.Out.To(-sl.Dir);
                }
                moving |= sl.W.Step(dt) | sl.In.Step(dt);
                if (sl.Out != null) moving |= sl.Out.Step(dt);
            }
            return moving;
        }

        public bool Moving
        {
            get
            {
                foreach (var sl in slots)
                    if (!sl.Started || sl.W.Moving || sl.In.Moving || (sl.Out != null && sl.Out.Moving)) return true;
                return false;
            }
        }

        /// <summary>How wide it is right now, while the characters change.</summary>
        public float Width
        {
            get { float w = 0; foreach (var sl in slots) w += (float)sl.W.Value; return w; }
        }

        /// <summary>How wide it will be once it has rolled.</summary>
        public float TargetWidth
        {
            get { float w = 0; foreach (var sl in slots) w += sl.WTo; return w; }
        }

        /// <summary>Hands every visible character to glyph, the text's left edge at x and its line from y to y + lineH.</summary>
        public void Draw(float x, float y, float lineH, Glyph glyph)
        {
            foreach (var sl in slots)
            {
                float w = (float)sl.W.Value;
                if (sl.To.Length > 0) Put(sl.To, sl.WTo, x, w, y, lineH, sl.In, glyph);
                if (sl.Out != null && sl.From.Length > 0) Put(sl.From, sl.WFrom, x, w, y, lineH, sl.Out, glyph);
                x += w;
            }
        }

        static void Put(string ch, float gw, float x, float w, float y, float lineH, Spring at, Glyph glyph)
        {
            double alpha = Math.Max(0, 1 - Math.Abs(at.Value) * 1.4);
            if (alpha <= 0.01) return;
            float smear = (float)Math.Max(-4, Math.Min(4, -at.Velocity * lineH * Travel * 0.012));
            glyph(ch, x + (w - gw) / 2, y + (float)at.Value * lineH * Travel, alpha, Math.Abs(smear) < 0.4f ? 0 : smear);
        }

        Slot Keep(List<Slot> old, int i, string ch)
        {
            // An unchanged character keeps its slot, so one still rolling in from the last change finishes its way.
            if (i < old.Count && old[i].To == ch) return old[i];
            return Fresh(ch);
        }

        Slot Fresh(string ch)
        {
            float w = measure(ch);
            return new Slot { From = ch, To = ch, WFrom = w, WTo = w, W = Motion.Shape(w, 0.1), In = Motion.Roll(0), Started = true };
        }

        /// <summary>Characters as strings: a surrogate pair stays one character.</summary>
        static string[] Chars(string s)
        {
            var list = new List<string>();
            for (int i = 0; i < s.Length; i++)
            {
                if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length) { list.Add(s.Substring(i, 2)); i++; }
                else list.Add(s[i].ToString());
            }
            return list.ToArray();
        }

        static string[] Sub(string[] a, int start, int count)
        {
            var r = new string[Math.Max(0, count)];
            Array.Copy(a, start, r, 0, r.Length);
            return r;
        }

        static bool Number(string[] a, out long v)
        {
            v = 0;
            if (a.Length == 0 || a.Length > 15) return false;
            foreach (string ch in a)
            {
                if (ch.Length != 1 || ch[0] < '0' || ch[0] > '9') return false;
                v = v * 10 + (ch[0] - '0');
            }
            return true;
        }

        /// <summary>A character's advance in GDI text (TextRenderer), measured between two x's so spaces count too.</summary>
        public static Func<string, float> Gdi(Font font)
        {
            var cache = new Dictionary<string, float>();
            const TextFormatFlags F = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
            var big = new Size(int.MaxValue, int.MaxValue);
            float xx = TextRenderer.MeasureText("xx", font, big, F).Width;
            return ch =>
            {
                if (string.IsNullOrEmpty(ch)) return 0;
                float w;
                if (!cache.TryGetValue(ch, out w))
                {
                    w = Math.Max(0, TextRenderer.MeasureText("x" + ch + "x", font, big, F).Width - xx);
                    cache[ch] = w;
                }
                return w;
            };
        }
    }
}
