// The EDID engine for the resolution creator: reading a monitor's EDID, working out timings for a new resolution
// and writing that resolution into a copy of the EDID, the way ToastyX's Custom Resolution Utility (CRU) does.
// Everything here is plain arithmetic on byte arrays: no registry, no files, no processes, no P/Invoke. Writing
// the result to EDID_OVERRIDE and restarting the driver is the caller's job; undo is restoring the original bytes.
//
// The formats, briefly (E-EDID 1.4, CTA-861, DisplayID 1.3 / 2.0, VESA CVT 1.2):
//  - An EDID is 128-byte blocks, each ending in a checksum that makes the block's bytes sum to 0 mod 256. Block 0 is
//    the base block; its byte 126 says how many extension blocks follow.
//  - The base block has four 18-byte descriptors (at 54, 72, 90, 108). The first is the native mode as a detailed
//    timing (DTD); the others are more DTDs or display descriptors: the name (tag FC), the range limits (FD), the
//    serial (FF) or an unused "dummy" (10). A DTD holds at most 4095 × 4095 at 655.35 MHz.
//  - A CTA-861 extension (tag 02) has data blocks from byte 4 up to the offset in byte 2, then DTDs up to the
//    padding before the checksum.
//  - A DisplayID extension (tag 70) wraps a DisplayID section: version, length, product type, extension count, data
//    blocks, a checksum of its own. Its detailed timings (Type I, tag 03, clock in 10 kHz; Type VII in DisplayID 2.0,
//    tag 22, clock in 1 kHz) are 20 bytes with 16-bit sizes and a 24-bit clock, so this is where a mode goes that a
//    DTD can't hold: 240 Hz and up at 1440p, for a start.
//
// Where a new resolution goes (AddResolution), in this order: a dummy descriptor in the base block, the DTD area of
// a CTA-861 extension, the DisplayID extension (an existing one with room, else a new block). The native DTD, the
// name, the range limits and the serial are never touched.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace HdrSwitch
{
    public static class Edid
    {
        public const int BlockSize = 128;
        /// <summary>The most blocks an EDID is allowed to grow to here: the base block and three extensions.</summary>
        public const int MaxBlocks = 4;
        /// <summary>What a DTD's 16-bit clock field holds: 655.35 MHz.</summary>
        public const long MaxDtdClock = 655350000;
        /// <summary>Bytes kept unused in a CTA-861 extension after adding a DTD (CRU: NVIDIA ignores the override otherwise).</summary>
        public const int CtaPadding = 2;
        /// <summary>The bytes of a DisplayID section's data blocks that fit in an EDID extension block.</summary>
        public const int DisplayIdRoom = 121;

        public enum CvtMode { ReducedBlanking, ReducedBlanking2 }

        public enum DescriptorKind { Timing, Name, Range, Serial, Dummy, Other }

        /// <summary>A detailed timing: the visible size and the blanking around it, in pixels and lines.</summary>
        public sealed class Timing
        {
            public int Width, Height;
            public int HFront, HSync, HBack, VFront, VSync, VBack;
            public bool HPositive, VPositive, Interlaced, Preferred;
            public long ClockHz;
            public int WidthMm, HeightMm;   // the image size a DTD carries; 0 where the format has none
            public string Source = "";      // where in the EDID it was read from

            public int HBlank { get { return HFront + HSync + HBack; } }
            public int VBlank { get { return VFront + VSync + VBack; } }
            public int HTotal { get { return Width + HBlank; } }
            public int VTotal { get { return Height + VBlank; } }

            /// <summary>The refresh rate the numbers give: clock / (h total × v total). Fields per second when interlaced.</summary>
            public double Hz
            {
                get
                {
                    double lines = Interlaced ? VTotal + 0.5 : VTotal;
                    return HTotal <= 0 || lines <= 0 ? 0 : ClockHz / (HTotal * lines);
                }
            }

            public bool SameTiming(Timing t)
            {
                return t != null && t.Width == Width && t.Height == Height && t.HFront == HFront && t.HSync == HSync && t.HBack == HBack
                    && t.VFront == VFront && t.VSync == VSync && t.VBack == VBack && t.HPositive == HPositive && t.VPositive == VPositive
                    && t.Interlaced == Interlaced && t.ClockHz == ClockHz;
            }

            public Timing Copy() { return (Timing)MemberwiseClone(); }

            /// <summary>"2368 × 1332 @ 143.912 Hz, 519.25 MHz".</summary>
            public string Text
            {
                get
                {
                    return Width + " × " + (Interlaced ? Height * 2 + "i" : Height.ToString(CultureInfo.InvariantCulture)) + " @ "
                        + Hz.ToString("0.000", CultureInfo.InvariantCulture) + " Hz, " + (ClockHz / 1e6).ToString("0.00#", CultureInfo.InvariantCulture) + " MHz";
                }
            }
        }

        public sealed class Descriptor
        {
            public int Index, Offset, Tag;
            public DescriptorKind Kind;
            public Timing Timing;   // Kind == Timing
            public string Text;     // name and serial
        }

        /// <summary>The range limits descriptor (FD): what the monitor says it can sync to.</summary>
        public sealed class RangeLimits
        {
            public int MinVHz, MaxVHz, MinHKhz, MaxHKhz, MaxClockMhz;
        }

        public sealed class DataBlock
        {
            public int Tag, ExtendedTag, Revision, Offset, Length;   // Length: payload bytes after the header
        }

        public sealed class CtaExtension
        {
            public int Revision, DtdOffset, Flags;
            public bool Valid;            // the DTD offset and the data blocks add up
            public int FreeOffset;        // where the next DTD would go
            public int FreeBytes;         // unused bytes before the checksum (0 if what follows the DTDs isn't padding)
            public List<DataBlock> DataBlocks = new List<DataBlock>();
            public List<Timing> Timings = new List<Timing>();
        }

        public sealed class DisplayIdExtension
        {
            public int Version, SectionLength, ProductType, ExtensionCount;
            public bool Valid;            // the length, the data blocks and the section checksum add up
            public bool ChecksumOk;
            public int UsedBytes;         // of the section's data block area
            public int FreeBytes;         // DisplayIdRoom - UsedBytes (0 if not Valid)
            public bool IsV2 { get { return Version >= 0x20; } }
            public List<DataBlock> DataBlocks = new List<DataBlock>();
            public List<Timing> Timings = new List<Timing>();
        }

        public sealed class Extension
        {
            public int Block, Tag;
            public CtaExtension Cta;
            public DisplayIdExtension DisplayId;
        }

        public sealed class Parsed
        {
            public byte[] Bytes;          // exactly the blocks byte 126 announces
            public int BlockCount;
            public string Manufacturer;   // three letters, "AOC"
            public int Product;
            public long Serial;
            public int Version, Revision;
            public int WidthCm, HeightCm; // bytes 21/22
            public int WidthMm, HeightMm; // from the native DTD, 0 if it has none
            public Timing Native;         // the first descriptor when it is a timing, else null
            public Descriptor[] Descriptors = new Descriptor[4];
            public string Name, SerialText;
            public RangeLimits Range;
            public List<Extension> Extensions = new List<Extension>();

            /// <summary>The panel's diagonal in inches: from the native DTD's millimetres, else the centimetres, else 0.</summary>
            public double Inches
            {
                get
                {
                    double w = WidthMm > 0 && HeightMm > 0 ? WidthMm / 10.0 : WidthCm, h = WidthMm > 0 && HeightMm > 0 ? HeightMm / 10.0 : HeightCm;
                    return w <= 0 || h <= 0 ? 0 : Math.Sqrt(w * w + h * h) / 2.54;
                }
            }

            /// <summary>Every detailed timing in the EDID, in block order: base DTDs, CTA-861 DTDs, DisplayID timings.</summary>
            public List<Timing> Timings
            {
                get
                {
                    var list = new List<Timing>();
                    foreach (var d in Descriptors) if (d != null && d.Kind == DescriptorKind.Timing) list.Add(d.Timing);
                    foreach (var e in Extensions)
                    {
                        if (e.Cta != null) list.AddRange(e.Cta.Timings);
                        if (e.DisplayId != null) list.AddRange(e.DisplayId.Timings);
                    }
                    return list;
                }
            }
        }

        /// <summary>The size for the creator: the largest w × h of a shape within a diagonal. See SizeFor.</summary>
        public sealed class Size
        {
            public int Width, Height;
            public double Inches;    // the exact diagonal of Width × Height on this panel
            public double Percent;   // of the panel's diagonal
        }

        // ---- bytes ----

        /// <summary>"00 ff ff …" or "00FFFF…" (spaces, line breaks, commas, 0x allowed) to bytes; null if it isn't hex.</summary>
        public static byte[] FromHex(string hex)
        {
            if (hex == null) return null;
            var digits = new StringBuilder();
            string s = hex.Replace("0x", " ").Replace("0X", " ");
            foreach (char c in s)
            {
                if (Uri.IsHexDigit(c)) digits.Append(c);
                else if (!char.IsWhiteSpace(c) && c != ',') return null;
            }
            if (digits.Length % 2 != 0) return null;
            var bytes = new byte[digits.Length / 2];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = Convert.ToByte(digits.ToString(i * 2, 2), 16);
            return bytes;
        }

        public static string ToHex(byte[] bytes)
        {
            var sb = new StringBuilder();
            for (int i = 0; bytes != null && i < bytes.Length; i++)
            {
                if (i > 0) sb.Append(i % 16 == 0 ? "\n" : " ");
                sb.Append(bytes[i].ToString("x2"));
            }
            return sb.ToString();
        }

        /// <summary>The byte that makes count bytes from offset sum to 0 mod 256, given the count - 1 before it.</summary>
        internal static byte Checksum(byte[] b, int offset, int count)
        {
            int sum = 0;
            for (int i = 0; i < count - 1; i++) sum += b[offset + i];
            return (byte)((256 - (sum & 0xFF)) & 0xFF);
        }

        /// <summary>This 128-byte block's bytes sum to 0 mod 256.</summary>
        public static bool BlockChecksumOk(byte[] edid, int block)
        {
            if (edid == null || block < 0 || (block + 1) * BlockSize > edid.Length) return false;
            return Checksum(edid, block * BlockSize, BlockSize) == edid[block * BlockSize + BlockSize - 1];
        }

        internal static void FixBlock(byte[] edid, int block)
        {
            edid[block * BlockSize + BlockSize - 1] = Checksum(edid, block * BlockSize, BlockSize);
        }

        // ---- reading ----

        public static Parsed Parse(byte[] edid)
        {
            string error;
            return Parse(edid, out error);
        }

        /// <summary>
        /// Reads an EDID. Null with the reason if it isn't one: a wrong header, a block whose checksum is off, or fewer
        /// blocks than byte 126 announces. Bytes after the announced blocks are ignored.
        /// </summary>
        public static Parsed Parse(byte[] edid, out string error)
        {
            error = null;
            if (edid == null || edid.Length < BlockSize) { error = "The EDID is shorter than one 128-byte block."; return null; }
            byte[] header = { 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00 };
            for (int i = 0; i < 8; i++)
                if (edid[i] != header[i]) { error = "The EDID doesn't start with the EDID header."; return null; }
            int blocks = 1 + edid[126];
            if (edid.Length < blocks * BlockSize)
            {
                error = "The EDID announces " + edid[126] + " extension block(s) but only has " + (edid.Length / BlockSize - 1) + ".";
                return null;
            }
            for (int b = 0; b < blocks; b++)
                if (!BlockChecksumOk(edid, b)) { error = "The checksum of block " + b + " is wrong."; return null; }

            var p = new Parsed { BlockCount = blocks, Bytes = new byte[blocks * BlockSize] };
            Array.Copy(edid, p.Bytes, p.Bytes.Length);
            byte[] e = p.Bytes;

            int id = (e[8] << 8) | e[9];
            p.Manufacturer = "" + Letter(id >> 10) + Letter(id >> 5) + Letter(id);
            p.Product = e[10] | (e[11] << 8);
            p.Serial = (uint)(e[12] | (e[13] << 8) | (e[14] << 16) | (e[15] << 24));
            p.Version = e[18];
            p.Revision = e[19];
            p.WidthCm = e[21];
            p.HeightCm = e[22];

            for (int i = 0; i < 4; i++)
            {
                int o = 54 + i * 18;
                var d = new Descriptor { Index = i, Offset = o };
                if (e[o] != 0 || e[o + 1] != 0)
                {
                    d.Kind = DescriptorKind.Timing;
                    d.Tag = -1;
                    d.Timing = DecodeDtd(e, o);
                    d.Timing.Source = "base block, descriptor " + (i + 1);
                }
                else
                {
                    d.Tag = e[o + 3];
                    d.Kind = d.Tag == 0xFC ? DescriptorKind.Name : d.Tag == 0xFD ? DescriptorKind.Range : d.Tag == 0xFF ? DescriptorKind.Serial
                        : d.Tag == 0x10 ? DescriptorKind.Dummy : DescriptorKind.Other;
                    if (d.Kind == DescriptorKind.Name || d.Kind == DescriptorKind.Serial)
                    {
                        d.Text = DescriptorText(e, o);
                        if (d.Kind == DescriptorKind.Name && p.Name == null) p.Name = d.Text;
                        if (d.Kind == DescriptorKind.Serial && p.SerialText == null) p.SerialText = d.Text;
                    }
                    if (d.Kind == DescriptorKind.Range && p.Range == null)
                    {
                        // Byte 4 (EDID 1.4): add 255 to the vertical (bits 0-1) and horizontal (bits 2-3) rates:
                        // 10 = to the maximum, 11 = to both.
                        int f = e[o + 4];
                        p.Range = new RangeLimits
                        {
                            MinVHz = e[o + 5] + ((f & 0x03) == 0x03 ? 255 : 0),
                            MaxVHz = e[o + 6] + ((f & 0x02) != 0 ? 255 : 0),
                            MinHKhz = e[o + 7] + ((f & 0x0C) == 0x0C ? 255 : 0),
                            MaxHKhz = e[o + 8] + ((f & 0x08) != 0 ? 255 : 0),
                            MaxClockMhz = e[o + 9] * 10
                        };
                    }
                }
                p.Descriptors[i] = d;
            }
            if (p.Descriptors[0].Kind == DescriptorKind.Timing)
            {
                p.Native = p.Descriptors[0].Timing;
                p.Native.Preferred = true;
                p.WidthMm = p.Native.WidthMm;
                p.HeightMm = p.Native.HeightMm;
            }

            for (int b = 1; b < blocks; b++)
            {
                int o = b * BlockSize;
                var x = new Extension { Block = b, Tag = e[o] };
                if (x.Tag == 0x02) x.Cta = ParseCta(e, o, b);
                else if (x.Tag == 0x70) x.DisplayId = ParseDisplayId(e, o, b);
                p.Extensions.Add(x);
            }
            return p;
        }

        static char Letter(int five)
        {
            five &= 0x1F;
            return five >= 1 && five <= 26 ? (char)('A' + five - 1) : '?';
        }

        static string DescriptorText(byte[] e, int o)
        {
            var sb = new StringBuilder();
            for (int i = 5; i < 18; i++)
            {
                byte c = e[o + i];
                if (c == 0x0A || c == 0) break;
                sb.Append(c >= 0x20 && c < 0x7F ? (char)c : '?');
            }
            return sb.ToString().TrimEnd();
        }

        static CtaExtension ParseCta(byte[] e, int o, int block)
        {
            var c = new CtaExtension { Revision = e[o + 1], DtdOffset = e[o + 2], Flags = e[o + 3] };
            int d = c.DtdOffset;
            // 0: no data blocks and no DTDs. Otherwise the DTDs start at d, with the data blocks between byte 4 and d.
            if (d != 0 && (d < 4 || d > 127)) return c;
            int pos = 4, dataEnd = d == 0 ? 4 : d;
            while (pos < dataEnd)
            {
                int len = e[o + pos] & 0x1F, tag = e[o + pos] >> 5;
                if (pos + 1 + len > dataEnd) return c;
                c.DataBlocks.Add(new DataBlock { Tag = tag, ExtendedTag = tag == 7 && len > 0 ? e[o + pos + 1] : -1, Offset = pos, Length = len });
                pos += 1 + len;
            }
            pos = dataEnd;
            while (d != 0 && pos + 18 <= 127 && (e[o + pos] != 0 || e[o + pos + 1] != 0))
            {
                var t = DecodeDtd(e, o + pos);
                t.Source = "CTA-861 extension (block " + block + ")";
                c.Timings.Add(t);
                pos += 18;
            }
            c.Valid = true;
            c.FreeOffset = pos;
            c.FreeBytes = 127 - pos;
            for (int i = pos; i < 127; i++)
                if (e[o + i] != 0) { c.FreeBytes = 0; break; }   // something that isn't padding: leave it alone
            return c;
        }

        static DisplayIdExtension ParseDisplayId(byte[] e, int o, int block)
        {
            var x = new DisplayIdExtension { Version = e[o + 1], SectionLength = e[o + 2], ProductType = e[o + 3], ExtensionCount = e[o + 4] };
            if (x.SectionLength > DisplayIdRoom) return x;
            // The section is the version byte up to its own checksum: 5 bytes around SectionLength bytes of data blocks.
            x.ChecksumOk = Checksum(e, o + 1, x.SectionLength + 5) == e[o + 5 + x.SectionLength];
            int pos = 5, end = 5 + x.SectionLength;
            bool ok = true;
            while (pos + 3 <= end)
            {
                int tag = e[o + pos], rev = e[o + pos + 1], len = e[o + pos + 2];
                if (tag == 0 && rev == 0 && len == 0) break;   // padding
                if (pos + 3 + len > end) { ok = false; break; }
                x.DataBlocks.Add(new DataBlock { Tag = tag, ExtendedTag = -1, Revision = rev, Offset = pos, Length = len });
                if (tag == 0x03 || tag == 0x22)
                    for (int i = 0; i + 20 <= len; i += 20)
                    {
                        var t = DecodeDisplayIdTiming(e, o + pos + 3 + i, tag == 0x22);
                        t.Source = "DisplayID extension (block " + block + ")";
                        x.Timings.Add(t);
                    }
                pos += 3 + len;
            }
            for (int i = pos; ok && i < end; i++)
                if (e[o + i] != 0) ok = false;
            x.UsedBytes = pos - 5;
            x.Valid = ok && x.ChecksumOk;
            x.FreeBytes = x.Valid ? DisplayIdRoom - x.UsedBytes : 0;
            return x;
        }

        // ---- the 18-byte detailed timing descriptor ----

        public static Timing DecodeDtd(byte[] b, int o)
        {
            var t = new Timing();
            t.ClockHz = (b[o] | (b[o + 1] << 8)) * 10000L;
            t.Width = b[o + 2] | ((b[o + 4] & 0xF0) << 4);
            int hBlank = b[o + 3] | ((b[o + 4] & 0x0F) << 8);
            t.Height = b[o + 5] | ((b[o + 7] & 0xF0) << 4);
            int vBlank = b[o + 6] | ((b[o + 7] & 0x0F) << 8);
            t.HFront = b[o + 8] | ((b[o + 11] & 0xC0) << 2);
            t.HSync = b[o + 9] | ((b[o + 11] & 0x30) << 4);
            t.VFront = (b[o + 10] >> 4) | ((b[o + 11] & 0x0C) << 2);
            t.VSync = (b[o + 10] & 0x0F) | ((b[o + 11] & 0x03) << 4);
            t.HBack = hBlank - t.HFront - t.HSync;
            t.VBack = vBlank - t.VFront - t.VSync;
            t.WidthMm = b[o + 12] | ((b[o + 14] & 0xF0) << 4);
            t.HeightMm = b[o + 13] | ((b[o + 14] & 0x0F) << 8);
            int flags = b[o + 17];
            t.Interlaced = (flags & 0x80) != 0;
            // Bits 4-3: 11 = digital separate sync, with the vertical polarity in bit 2 and the horizontal in bit 1.
            t.HPositive = (flags & 0x10) != 0 && (flags & 0x02) != 0;
            t.VPositive = (flags & 0x18) == 0x18 && (flags & 0x04) != 0;
            return t;
        }

        /// <summary>The timing fits a DTD: 12-bit sizes and blanking, 10-bit horizontal and 6-bit vertical porch and sync, a clock in 10 kHz up to 655.35 MHz.</summary>
        public static bool FitsDtd(Timing t)
        {
            return t != null && !t.Interlaced && t.Width > 0 && t.Height > 0 && t.Width <= 4095 && t.Height <= 4095
                && t.HBlank <= 4095 && t.VBlank <= 4095 && t.HFront >= 0 && t.HFront <= 1023 && t.HSync > 0 && t.HSync <= 1023
                && t.VFront >= 0 && t.VFront <= 63 && t.VSync > 0 && t.VSync <= 63 && t.HBack >= 0 && t.VBack >= 0
                && t.ClockHz >= 10000 && t.ClockHz <= MaxDtdClock && t.ClockHz % 10000 == 0
                && t.WidthMm >= 0 && t.WidthMm <= 4095 && t.HeightMm >= 0 && t.HeightMm <= 4095;
        }

        /// <summary>The 18 bytes of a DTD (digital separate sync, no border, no stereo), or null if the timing doesn't fit one.</summary>
        public static byte[] EncodeDtd(Timing t)
        {
            if (!FitsDtd(t)) return null;
            var b = new byte[18];
            long clock = t.ClockHz / 10000;
            b[0] = (byte)(clock & 0xFF);
            b[1] = (byte)(clock >> 8);
            b[2] = (byte)(t.Width & 0xFF);
            b[3] = (byte)(t.HBlank & 0xFF);
            b[4] = (byte)(((t.Width >> 8) << 4) | (t.HBlank >> 8));
            b[5] = (byte)(t.Height & 0xFF);
            b[6] = (byte)(t.VBlank & 0xFF);
            b[7] = (byte)(((t.Height >> 8) << 4) | (t.VBlank >> 8));
            b[8] = (byte)(t.HFront & 0xFF);
            b[9] = (byte)(t.HSync & 0xFF);
            b[10] = (byte)(((t.VFront & 0x0F) << 4) | (t.VSync & 0x0F));
            b[11] = (byte)(((t.HFront >> 8) << 6) | ((t.HSync >> 8) << 4) | ((t.VFront >> 4) << 2) | (t.VSync >> 4));
            b[12] = (byte)(t.WidthMm & 0xFF);
            b[13] = (byte)(t.HeightMm & 0xFF);
            b[14] = (byte)(((t.WidthMm >> 8) << 4) | (t.HeightMm >> 8));
            b[17] = (byte)(0x18 | (t.VPositive ? 0x04 : 0) | (t.HPositive ? 0x02 : 0));
            return b;
        }

        // ---- the 20-byte DisplayID detailed timing (Type I; Type VII is the same with the clock in 1 kHz) ----

        public static Timing DecodeDisplayIdTiming(byte[] b, int o, bool typeVii)
        {
            var t = new Timing();
            long clock = (b[o] | (b[o + 1] << 8) | (b[o + 2] << 16)) + 1L;
            t.ClockHz = clock * (typeVii ? 1000 : 10000);
            t.Preferred = (b[o + 3] & 0x80) != 0;
            t.Interlaced = (b[o + 3] & 0x10) != 0;
            t.Width = Word(b, o + 4) + 1;
            int hBlank = Word(b, o + 6) + 1;
            t.HFront = (Word(b, o + 8) & 0x7FFF) + 1;
            t.HPositive = (b[o + 9] & 0x80) != 0;
            t.HSync = Word(b, o + 10) + 1;
            t.Height = Word(b, o + 12) + 1;
            int vBlank = Word(b, o + 14) + 1;
            t.VFront = (Word(b, o + 16) & 0x7FFF) + 1;
            t.VPositive = (b[o + 17] & 0x80) != 0;
            t.VSync = Word(b, o + 18) + 1;
            t.HBack = hBlank - t.HFront - t.HSync;
            t.VBack = vBlank - t.VFront - t.VSync;
            return t;
        }

        static int Word(byte[] b, int o) { return b[o] | (b[o + 1] << 8); }

        static void PutWord(byte[] b, int o, int value)
        {
            b[o] = (byte)(value & 0xFF);
            b[o + 1] = (byte)((value >> 8) & 0xFF);
        }

        /// <summary>The timing fits a DisplayID detailed timing: 16-bit sizes, porches from 1 line, a 24-bit clock in the type's unit.</summary>
        public static bool FitsDisplayId(Timing t, bool typeVii)
        {
            long unit = typeVii ? 1000 : 10000;
            return t != null && !t.Interlaced && t.Width >= 1 && t.Width <= 65536 && t.Height >= 1 && t.Height <= 65536
                && t.HBlank >= 1 && t.HBlank <= 65536 && t.VBlank >= 1 && t.VBlank <= 65536
                && t.HFront >= 1 && t.HFront <= 32768 && t.VFront >= 1 && t.VFront <= 32768
                && t.HSync >= 1 && t.HSync <= 65536 && t.VSync >= 1 && t.VSync <= 65536 && t.HBack >= 0 && t.VBack >= 0
                && t.ClockHz >= unit && t.ClockHz % unit == 0 && t.ClockHz / unit <= 0x1000000;
        }

        /// <summary>The 20 bytes of a DisplayID Type I (10 kHz clock) or Type VII (1 kHz) detailed timing, or null if it doesn't fit.</summary>
        public static byte[] EncodeDisplayIdTiming(Timing t, bool typeVii)
        {
            if (!FitsDisplayId(t, typeVii)) return null;
            var b = new byte[20];
            long clock = t.ClockHz / (typeVii ? 1000 : 10000) - 1;
            b[0] = (byte)(clock & 0xFF);
            b[1] = (byte)((clock >> 8) & 0xFF);
            b[2] = (byte)((clock >> 16) & 0xFF);
            b[3] = (byte)((t.Preferred ? 0x80 : 0) | AspectCode(t.Width, t.Height));
            PutWord(b, 4, t.Width - 1);
            PutWord(b, 6, t.HBlank - 1);
            PutWord(b, 8, (t.HFront - 1) | (t.HPositive ? 0x8000 : 0));
            PutWord(b, 10, t.HSync - 1);
            PutWord(b, 12, t.Height - 1);
            PutWord(b, 14, t.VBlank - 1);
            PutWord(b, 16, (t.VFront - 1) | (t.VPositive ? 0x8000 : 0));
            PutWord(b, 18, t.VSync - 1);
            return b;
        }

        /// <summary>DisplayID's aspect ratio code for the timing's options byte; 8 = not one of the named ones.</summary>
        static int AspectCode(int w, int h)
        {
            int[,] named = { { 1, 1 }, { 5, 4 }, { 4, 3 }, { 15, 9 }, { 16, 9 }, { 16, 10 }, { 64, 27 }, { 256, 135 } };
            for (int i = 0; i < 8; i++)
                if ((long)w * named[i, 1] == (long)h * named[i, 0]) return i;
            return 8;
        }

        // ---- timing maths: VESA CVT 1.2, reduced blanking ----

        /// <summary>
        /// The CVT reduced-blanking timing for w × h at hz (progressive, no margins).
        ///  - ReducedBlanking (v1): 160 px of horizontal blanking (48 front, 32 sync, 80 back), 3 lines of front porch, a
        ///    sync width that names the aspect ratio (4 for 4:3, 5 for 16:9, 6 for 16:10, 7 for 5:4 and 15:9, else 10),
        ///    a back porch that takes the rest, a clock in steps of 0.25 MHz.
        ///  - ReducedBlanking2: 80 px (8 front, 32 sync, 40 back), 8 lines of sync, 6 of back porch, a front porch that
        ///    takes the rest, a clock in steps of 1 kHz.
        /// Both keep at least 460 µs of vertical blanking, with horizontal sync positive and vertical negative. The
        /// width is used as given: v1's rounding to a multiple of 8 is left out, because the creator has to get
        /// exactly the size it asked for. The clock is rounded down, so the real rate (Timing.Hz) is a hair under hz.
        /// </summary>
        public static Timing Cvt(int w, int h, double hz, CvtMode mode)
        {
            if (w <= 0 || h <= 0 || hz <= 0 || double.IsNaN(hz) || double.IsInfinity(hz)) return null;
            bool v2 = mode == CvtMode.ReducedBlanking2;
            const double minVBlankUs = 460;
            double frameUs = 1000000.0 / hz;
            if (frameUs <= minVBlankUs) return null;

            var t = new Timing { Width = w, Height = h, HPositive = true, VPositive = false };
            t.HSync = 32;
            t.HBack = v2 ? 40 : 80;
            t.HFront = v2 ? 8 : 48;
            t.VSync = v2 ? 8 : CvtSyncLines(w, h);
            const int minBack = 6;
            int minFront = v2 ? 1 : 3;

            double hPeriodUs = (frameUs - minVBlankUs) / h;
            int vBlank = (int)Math.Floor(minVBlankUs / hPeriodUs) + 1;
            vBlank = Math.Max(vBlank, minFront + t.VSync + minBack);
            if (v2) { t.VBack = minBack; t.VFront = vBlank - t.VSync - t.VBack; }
            else { t.VFront = minFront; t.VBack = vBlank - t.VFront - t.VSync; }

            long step = v2 ? 1000 : 250000;
            // The tiny nudge keeps a product that is a whole number of steps from landing just under it in floating point.
            t.ClockHz = (long)Math.Floor(hz * t.VTotal * t.HTotal / step + 1e-7) * step;
            return t.ClockHz > 0 ? t : null;
        }

        /// <summary>CVT v1's vertical sync width, which tells the display the aspect ratio.</summary>
        static int CvtSyncLines(int w, int h)
        {
            int[,] table = { { 4, 3, 4 }, { 16, 9, 5 }, { 16, 10, 6 }, { 5, 4, 7 }, { 15, 9, 7 } };
            int cells = w / 8;
            for (int i = 0; i < 5; i++)
                if ((int)((long)h * table[i, 0] / table[i, 1]) / 8 == cells) return table[i, 2];
            return 10;
        }

        /// <summary>The same timing with the clock rounded down to a multiple of step Hz (10 kHz for a DTD or a Type I timing).</summary>
        public static Timing WithClockStep(Timing t, long step)
        {
            if (t == null) return null;
            var c = t.Copy();
            c.ClockHz = t.ClockHz / step * step;
            return c;
        }

        // ---- refresh rates ----

        /// <summary>
        /// The refresh rates the display has at its native size, from every detailed timing of that size (base block,
        /// CTA-861, DisplayID): the native mode's own rate first, the others from the highest down. A resolution that the
        /// GPU scales has to use one of these (ToastyX: a lower resolution needs a refresh rate that exists at native).
        /// The range limits don't add rates: they say what the monitor can sync to, not which modes it lists.
        /// </summary>
        public static List<double> NativeRates(Parsed p)
        {
            var rates = new List<double>();
            if (p == null || p.Native == null) return rates;
            rates.Add(p.Native.Hz);
            foreach (var t in p.Timings.Where(x => x.Width == p.Native.Width && x.Height == p.Native.Height && !x.Interlaced).OrderByDescending(x => x.Hz))
                if (!rates.Any(r => Math.Abs(r - t.Hz) < 0.5)) rates.Add(t.Hz);
            return rates;
        }

        /// <summary>The native rate that hz means (within half a hertz: 144 for 143.912), or 0 if the display has none.</summary>
        public static double MatchNativeRate(Parsed p, double hz)
        {
            foreach (double r in NativeRates(p))
                if (Math.Abs(r - hz) < 0.5) return r;
            return 0;
        }

        // ---- adding a resolution ----

        /// <summary>
        /// A copy of the EDID with w × h at hz added, and where it went. Null with the reason in where if it can't be:
        /// an EDID that doesn't parse, a size above native, a rate the native size doesn't have, a mode that is there
        /// already, or no room. The rate becomes the native one it matches exactly (144 → 143.912), so the new mode
        /// reports the same whole rate as the native mode it is switched from. The input is never changed.
        /// </summary>
        public static byte[] AddResolution(byte[] edid, int w, int h, double hz, out string where)
        {
            string error;
            var p = Parse(edid, out error);
            if (p == null) { where = error; return null; }
            if (p.Native == null) { where = "The EDID has no native mode in its first descriptor."; return null; }
            if (w < 320 || h < 200) { where = "The resolution is too small."; return null; }
            if (w > p.Native.Width || h > p.Native.Height)
            {
                where = w + " × " + h + " is larger than the display's " + p.Native.Width + " × " + p.Native.Height + ".";
                return null;
            }
            double rate = MatchNativeRate(p, hz);
            if (rate <= 0)
            {
                where = "The display has no " + hz.ToString("0.###", CultureInfo.InvariantCulture) + " Hz at " + p.Native.Width + " × " + p.Native.Height
                    + " (it has " + string.Join(", ", NativeRates(p).Select(r => r.ToString("0.###", CultureInfo.InvariantCulture)).ToArray()) + " Hz).";
                return null;
            }
            foreach (var t in p.Timings)
                if (t.Width == w && t.Height == h && !t.Interlaced && Math.Abs(t.Hz - rate) < 0.5)
                {
                    where = w + " × " + h + " at " + Math.Round(rate) + " Hz is already there: " + t.Source + ".";
                    return null;
                }

            // The timing: CVT-RB2, the one current drivers and CRU default to. Its front porch grows with the refresh
            // rate and a DTD has 6 bits for it, so for a DTD the fallback is CVT-RB v1, whose back porch grows instead.
            Timing rb2 = Cvt(w, h, rate, CvtMode.ReducedBlanking2), rb1 = Cvt(w, h, rate, CvtMode.ReducedBlanking);
            if (rb2 == null || rb1 == null) { where = "No timing can be worked out for that size and rate."; return null; }
            Timing dtd = WithClockStep(rb2, 10000);
            if (!FitsDtd(dtd)) dtd = FitsDtd(rb1) ? rb1 : null;

            var result = (byte[])p.Bytes.Clone();
            if (dtd != null)
            {
                dtd.WidthMm = p.Native.WidthMm;
                dtd.HeightMm = p.Native.HeightMm;
                byte[] bytes = EncodeDtd(dtd);

                // (a) A dummy descriptor in the base block. Never the first (the native mode), the name, range or serial.
                for (int i = 1; i < 4; i++)
                    if (p.Descriptors[i].Kind == DescriptorKind.Dummy)
                    {
                        Array.Copy(bytes, 0, result, p.Descriptors[i].Offset, 18);
                        FixBlock(result, 0);
                        where = "base block, descriptor " + (i + 1);
                        return result;
                    }

                // (b) The DTD area of a CTA-861 extension, keeping CtaPadding bytes unused.
                foreach (var x in p.Extensions)
                {
                    if (x.Cta == null || !x.Cta.Valid || x.Cta.FreeBytes < 18 + CtaPadding) continue;
                    int o = x.Block * BlockSize;
                    if (x.Cta.DtdOffset == 0) result[o + 2] = 4;   // a block with nothing in it: the DTDs start right after the header
                    Array.Copy(bytes, 0, result, o + x.Cta.FreeOffset, 18);
                    FixBlock(result, x.Block);
                    where = "CTA-861 extension (block " + x.Block + ")";
                    return result;
                }
            }

            // (c) DisplayID: an extension that has room, else a new block.
            Extension first = p.Extensions.FirstOrDefault(x => x.DisplayId != null);
            foreach (var x in p.Extensions)
            {
                if (x.DisplayId == null || !x.DisplayId.Valid) continue;
                if (AppendDisplayIdTiming(result, x, rb2))
                {
                    where = "DisplayID extension (block " + x.Block + ")";
                    return result;
                }
            }
            if (p.BlockCount >= MaxBlocks)
            {
                where = "The EDID is full: no free descriptor, no room in its extension blocks, and it already has " + p.BlockCount + " blocks.";
                return null;
            }
            // A second DisplayID section continues the first: same version, product type 0, and the first one counts it.
            bool v2 = first != null && first.DisplayId.IsV2;
            if (first != null && !first.DisplayId.Valid)
            {
                where = "The EDID's DisplayID extension can't be read, so a second one can't be added to it.";
                return null;
            }
            byte[] timing = EncodeDisplayIdTiming(v2 ? rb2 : WithClockStep(rb2, 10000), v2);
            if (timing == null) { where = "The timing doesn't fit a DisplayID detailed timing."; return null; }
            var grown = new byte[result.Length + BlockSize];
            Array.Copy(result, grown, result.Length);
            int n = result.Length;
            grown[n] = 0x70;
            grown[n + 1] = (byte)(first != null ? first.DisplayId.Version : 0x12);
            grown[n + 2] = DisplayIdRoom;
            grown[n + 3] = (byte)(first != null ? 0x00 : 0x03);   // the first section: a standalone display, as monitors ship it
            grown[n + 4] = 0;
            grown[n + 5] = (byte)(v2 ? 0x22 : 0x03);
            grown[n + 6] = (byte)(v2 ? 0x00 : 0x01);
            grown[n + 7] = 20;
            Array.Copy(timing, 0, grown, n + 8, 20);
            grown[n + 5 + DisplayIdRoom] = Checksum(grown, n + 1, DisplayIdRoom + 5);
            FixBlock(grown, p.BlockCount);
            if (first != null)
            {
                int o = first.Block * BlockSize;
                grown[o + 4]++;
                grown[o + 5 + first.DisplayId.SectionLength] = Checksum(grown, o + 1, first.DisplayId.SectionLength + 5);
                FixBlock(grown, first.Block);
            }
            grown[126]++;
            FixBlock(grown, 0);
            where = "new DisplayID extension (block " + p.BlockCount + ")";
            return grown;
        }

        /// <summary>
        /// Adds the timing to this DisplayID extension in place: into its detailed timing data block (Type I, or Type VII
        /// in a DisplayID 2.0 section) if it has one, else as a new data block after the others. False if there is no room.
        /// </summary>
        static bool AppendDisplayIdTiming(byte[] edid, Extension x, Timing rb2)
        {
            var d = x.DisplayId;
            bool v2 = d.IsV2;
            int tag = v2 ? 0x22 : 0x03, o = x.Block * BlockSize;
            byte[] timing = EncodeDisplayIdTiming(v2 ? rb2 : WithClockStep(rb2, 10000), v2);
            if (timing == null) return false;

            var data = new List<byte>();
            bool placed = false;
            foreach (var blk in d.DataBlocks)
            {
                for (int i = 0; i < 3 + blk.Length; i++) data.Add(edid[o + blk.Offset + i]);
                if (!placed && blk.Tag == tag && blk.Length % 20 == 0 && blk.Length + 20 <= 248)
                {
                    data.AddRange(timing);
                    data[data.Count - blk.Length - 20 - 1] = (byte)(blk.Length + 20);
                    placed = true;
                }
            }
            if (!placed)
            {
                data.Add((byte)tag);
                data.Add((byte)(v2 ? 0x00 : 0x01));
                data.Add(20);
                data.AddRange(timing);
            }
            if (data.Count > DisplayIdRoom) return false;

            // The section keeps its length if the data still fits it, else it takes the whole block.
            int length = data.Count <= d.SectionLength ? d.SectionLength : DisplayIdRoom;
            for (int i = 5; i < 127; i++) edid[o + i] = 0;
            for (int i = 0; i < data.Count; i++) edid[o + 5 + i] = data[i];
            edid[o + 2] = (byte)length;
            edid[o + 5 + length] = Checksum(edid, o + 1, length + 5);
            FixBlock(edid, x.Block);
            return true;
        }

        // ---- the size for a diagonal ----

        /// <summary>
        /// The largest w × h of the shape aspectW:aspectH whose diagonal on this panel is at most targetInches, and
        /// never larger than native. The rule: the largest width that is a multiple of 8 (what CVT and video scalers
        /// like) for which an even height next to the exact one fits the diagonal and keeps the shape within 0.5 %; of
        /// the two even heights around the exact one, the nearer if it fits, the upper on a tie. If no multiple of 8
        /// keeps the shape (only at very small sizes), the width is merely even. Null if nothing fits.
        /// The diagonal assumes square pixels: it is the panel's, scaled by the pixel diagonals.
        /// </summary>
        public static Size SizeFor(int nativeW, int nativeH, double panelInches, double targetInches, int aspectW, int aspectH)
        {
            if (nativeW <= 0 || nativeH <= 0 || panelInches <= 0 || targetInches <= 0 || aspectW <= 0 || aspectH <= 0) return null;
            double nativeDiagonal = Math.Sqrt((double)nativeW * nativeW + (double)nativeH * nativeH);
            double limit = nativeDiagonal * Math.Min(1, targetInches / panelInches) + 1e-9;
            double aspect = (double)aspectW / aspectH;
            foreach (int step in new[] { 8, 2 })
            {
                // A little above the exact shape's width: a width one step up can still fit with the height rounded down.
                int start = (int)Math.Min(nativeW, Math.Floor(limit * aspectW / Math.Sqrt((double)aspectW * aspectW + (double)aspectH * aspectH)) + 2 * step);
                for (int w = start / step * step; w >= step; w -= step)
                {
                    double exact = w / aspect;
                    int below = (int)Math.Floor(exact / 2 + 1e-9) * 2;
                    bool upperFirst = below + 2 - exact <= exact - below;
                    foreach (int h in upperFirst ? new[] { below + 2, below } : new[] { below, below + 2 })
                    {
                        if (h <= 0 || h > nativeH) continue;
                        if (h != below && Math.Abs(exact - below) < 1e-6) continue;                 // exact and even: only that one
                        if (Math.Abs((double)w / h - aspect) > aspect * 0.005) continue;
                        double diagonal = Math.Sqrt((double)w * w + (double)h * h);
                        if (diagonal > limit) continue;
                        return new Size { Width = w, Height = h, Inches = panelInches * diagonal / nativeDiagonal, Percent = 100 * diagonal / nativeDiagonal };
                    }
                }
            }
            return null;
        }

        // ---- a listing for the log and the UI ----

        /// <summary>What the EDID says, one line per fact: the monitor, each block, each timing and the free room. Or why it can't be read.</summary>
        public static string Describe(byte[] edid)
        {
            string error;
            var p = Parse(edid, out error);
            if (p == null) return error;
            var lines = new List<string>();
            string head = p.Manufacturer + " " + p.Product.ToString("X4") + (p.Name != null ? " · " + p.Name : "") + " · EDID " + p.Version + "." + p.Revision;
            if (p.Inches > 0) head += " · " + p.Inches.ToString("0.0", CultureInfo.InvariantCulture) + "″";
            lines.Add(head + " · " + p.BlockCount + (p.BlockCount == 1 ? " block" : " blocks"));
            if (p.Range != null)
                lines.Add("Range limits: " + p.Range.MinVHz + "–" + p.Range.MaxVHz + " Hz, " + p.Range.MinHKhz + "–" + p.Range.MaxHKhz + " kHz, up to " + p.Range.MaxClockMhz + " MHz");
            lines.Add("Native rates: " + string.Join(", ", NativeRates(p).Select(r => r.ToString("0.###", CultureInfo.InvariantCulture)).ToArray()) + " Hz");
            lines.Add("Block 0, base:");
            foreach (var d in p.Descriptors)
            {
                string what = d.Kind == DescriptorKind.Timing ? d.Timing.Text + (d.Index == 0 ? " (native)" : "")
                    : d.Kind == DescriptorKind.Name ? "name \"" + d.Text + "\""
                    : d.Kind == DescriptorKind.Serial ? "serial"
                    : d.Kind == DescriptorKind.Range ? "range limits"
                    : d.Kind == DescriptorKind.Dummy ? "free"
                    : "descriptor " + d.Tag.ToString("X2");
                lines.Add("  " + (d.Index + 1) + ": " + what);
            }
            foreach (var x in p.Extensions)
            {
                if (x.Cta != null)
                {
                    lines.Add("Block " + x.Block + ", CTA-861 revision " + x.Cta.Revision + ": " + x.Cta.DataBlocks.Count + " data blocks, "
                        + (x.Cta.Valid ? x.Cta.FreeBytes + " bytes free" : "unreadable"));
                    foreach (var t in x.Cta.Timings) lines.Add("  " + t.Text);
                }
                else if (x.DisplayId != null)
                {
                    lines.Add("Block " + x.Block + ", DisplayID " + (x.DisplayId.Version >> 4) + "." + (x.DisplayId.Version & 0x0F) + ": " + x.DisplayId.DataBlocks.Count + " data blocks, "
                        + (x.DisplayId.Valid ? x.DisplayId.FreeBytes + " bytes free" : "unreadable"));
                    foreach (var t in x.DisplayId.Timings) lines.Add("  " + t.Text + (t.Preferred ? " (preferred)" : ""));
                }
                else lines.Add("Block " + x.Block + ", extension " + x.Tag.ToString("X2"));
            }
            return string.Join("\n", lines.ToArray());
        }
    }
}
