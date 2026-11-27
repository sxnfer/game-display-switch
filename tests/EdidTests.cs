// Checks for the EDID engine (Edid.cs): reading real monitors' EDIDs, the CVT reduced-blanking timings against
// published values, the DTD and DisplayID encoders against the bytes monitors ship, where AddResolution puts a new
// mode and when it refuses, and the size for a diagonal.
//
// The three sample EDIDs are real ones from the public linuxhw/EDID collection (github.com/linuxhw/EDID, Digital/…),
// with the values edid-decode prints for them there:
//  - AOC Q27G2G4 (AOC2702/00B035B28824): 2560×1440 144 Hz, base block full, CTA-861 extension with 6 bytes free.
//  - ASRock PG27QFT1B (ASRABA1/0EF966B921E8): 2560×1440 180 Hz, CTA-861 with 47 bytes free, a DisplayID 1.2
//    extension with one Type I timing.
//  - ASUS ROG PG27AQN (AUS27BC/7C884E2B9E1C): 2560×1440 360 Hz, CTA-861 with 5 bytes free, a DisplayID extension
//    with five Type I timings and 5 bytes free. Its owner had already added 2368×1332 at 240 and 360 Hz.

using System;
using System.Collections.Generic;
using System.Linq;

namespace HdrSwitch.Tests
{
    static partial class Checks
    {
        const string EdidAocHex =
            "00 ff ff ff ff ff ff 00 05 e3 02 27 41 02 00 00 " +
            "01 1f 01 04 a5 3c 22 78 3b b3 e5 ac 50 48 a4 26 " +
            "11 50 54 bf ef 00 d1 c0 81 80 31 68 31 7c 45 68 " +
            "45 7c 61 68 61 7c 56 5e 00 a0 a0 a0 29 50 30 20 " +
            "35 00 55 50 21 00 00 1e 40 e7 00 6a a0 a0 67 50 " +
            "08 20 98 04 55 50 21 00 00 1a 00 00 00 fc 00 51 " +
            "32 37 47 32 47 34 0a 20 20 20 20 20 00 00 00 fd " +
            "00 30 90 e6 e6 3c 01 0a 20 20 20 20 20 20 01 5b " +

            "02 03 1f f1 4c 01 03 05 14 04 13 1f 12 02 11 90 " +
            "3f 23 09 07 07 83 01 00 00 65 03 0c 00 10 00 6f " +
            "c2 00 a0 a0 a0 55 50 30 20 35 00 55 50 21 00 00 " +
            "1e 5a a0 00 a0 a0 a0 46 50 30 20 35 00 55 50 21 " +
            "00 00 1e 02 3a 80 18 71 38 2d 40 58 2c 45 00 55 " +
            "50 21 00 00 1e ab 22 a0 a0 50 84 1a 30 30 20 36 " +
            "00 55 50 21 00 00 1a f0 3c 00 d0 51 a0 35 50 60 " +
            "88 3a 00 55 50 21 00 00 1c 00 00 00 00 00 00 80";

        const string EdidAsrockHex =
            "00 ff ff ff ff ff ff 00 06 72 a1 ab 00 00 00 00 " +
            "1c 22 01 04 b5 3c 22 78 3b 43 d5 aa 54 42 ac 24 " +
            "0f 50 54 bf cf 00 81 40 81 80 95 00 71 4f 81 c0 " +
            "b3 00 d1 c0 01 01 56 5e 00 a0 a0 a0 29 50 30 20 " +
            "35 00 55 50 21 00 00 1e 00 00 00 ff 00 48 37 4a " +
            "4c 30 41 30 30 31 35 33 31 0a 00 00 00 fc 00 50 " +
            "47 32 37 51 46 54 31 42 0a 20 20 20 00 00 00 fd " +
            "0c 30 b4 0a 0a 5a 01 0a 20 20 20 20 20 20 02 17 " +

            "02 03 2c f1 48 01 03 04 13 1f 12 10 3f 23 09 07 " +
            "07 83 01 00 00 68 1a 00 00 01 01 30 b4 00 e2 00 " +
            "ea e3 05 e0 00 e6 06 07 01 61 61 01 f8 e3 00 a0 " +
            "a0 a0 32 50 30 20 98 04 55 50 21 00 00 1a 6f c2 " +
            "00 a0 a0 a0 55 50 30 20 35 00 55 50 21 00 00 1e " +
            "00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 " +
            "00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 " +
            "00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 62 " +

            "70 12 79 03 00 03 01 14 23 19 01 84 ff 09 9f 00 " +
            "2f 80 1f 00 9f 05 1d 00 02 00 04 00 00 00 00 00 " +
            "00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 " +
            "00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 " +
            "00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 " +
            "00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 " +
            "00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 " +
            "00 00 00 00 00 00 00 00 00 00 00 00 00 00 5d 90";

        const string EdidAsusHex =
            "00 ff ff ff ff ff ff 00 06 b3 bc 27 8d ea 0f ff " +
            "80 33 01 04 b5 3c 22 78 3b d3 c1 ad 50 43 ab 24 " +
            "0d 50 54 21 08 00 01 01 01 01 01 01 01 01 01 01 " +
            "01 01 01 01 01 01 56 5e 00 a0 a0 a0 29 50 30 20 " +
            "15 08 54 4f 21 00 00 1a 00 00 00 ff 00 23 73 78 " +
            "6f 7a 47 44 41 59 47 41 73 45 00 00 00 fd 0e 01 " +
            "69 3a 3a 97 01 0a 20 20 20 20 20 20 00 00 00 fc " +
            "00 52 4f 47 20 50 47 32 37 41 51 4e 0a 20 02 cc " +

            "02 03 20 f6 23 09 07 01 83 01 00 00 65 4b 04 00 " +
            "01 01 e3 05 c0 00 e2 00 6a e6 06 05 01 78 60 1f " +
            "6f c2 00 a0 a0 a0 55 50 30 20 f5 0c 54 4f 21 00 " +
            "00 1a 79 e9 00 82 a0 a0 67 50 08 20 f5 0c 54 4f " +
            "21 00 00 1a 86 6f 80 a0 70 38 40 40 30 20 85 0c " +
            "54 4f 21 00 00 1a 82 e9 80 82 70 38 87 40 08 20 " +
            "f5 0c 54 4f 21 00 00 1a 5a a0 00 a0 a0 a0 46 50 " +
            "30 20 e5 0c 54 4f 21 00 00 1a 00 00 00 00 00 65 " +

            "70 12 79 03 00 0f 00 0a a4 14 0e 0e 07 01 20 45 " +
            "00 00 03 01 64 ca 9c 01 04 ff 09 9f 00 2f 80 1f " +
            "00 9f 05 b2 00 aa 00 04 00 a7 4b 02 04 ff 09 4f " +
            "00 07 80 1f 00 9f 05 8e 00 86 00 04 00 e7 7a 01 " +
            "04 7f 07 9f 00 2f 80 1f 00 37 04 d6 00 ce 00 04 " +
            "00 fa 62 01 04 3f 09 9f 00 2f 80 1f 00 33 05 a5 " +
            "00 9d 00 04 00 ac 37 02 04 3f 09 9f 00 2f 80 1f " +
            "00 33 05 08 01 00 01 04 00 00 00 00 00 00 67 90";

        static byte[] EdidAoc() { return Edid.FromHex(EdidAocHex); }
        static byte[] EdidAsrock() { return Edid.FromHex(EdidAsrockHex); }
        static byte[] EdidAsus() { return Edid.FromHex(EdidAsusHex); }

        /// <summary>Every block's checksum, and every DisplayID section's own, add up; the length matches byte 126.</summary>
        static void EdidSound(byte[] edid, string what)
        {
            Run.True(edid != null && edid.Length == (edid[126] + 1) * 128, what + ": length matches the extension count");
            if (edid == null) return;
            for (int b = 0; b * 128 < edid.Length; b++) Run.True(Edid.BlockChecksumOk(edid, b), what + ": checksum of block " + b);
            string error;
            var p = Edid.Parse(edid, out error);
            Run.True(p != null, what + ": parses (" + error + ")");
            if (p == null) return;
            foreach (var x in p.Extensions)
            {
                if (x.DisplayId != null) Run.True(x.DisplayId.Valid && x.DisplayId.ChecksumOk, what + ": DisplayID section of block " + x.Block);
                if (x.Cta != null) Run.True(x.Cta.Valid, what + ": CTA-861 layout of block " + x.Block);
            }
        }

        static Edid.Timing EdidFind(byte[] edid, int w, int h, double hz)
        {
            var p = Edid.Parse(edid);
            return p == null ? null : p.Timings.FirstOrDefault(t => t.Width == w && t.Height == h && Math.Abs(t.Hz - hz) < 0.5);
        }

        /// <summary>The ASRock sample with its serial descriptor (the second) turned into an unused one.</summary>
        static byte[] EdidWithFreeSlot()
        {
            byte[] e = EdidAsrock();
            for (int i = 72; i < 90; i++) e[i] = 0;
            e[75] = 0x10;
            Edid.FixBlock(e, 0);
            return e;
        }

        /// <summary>The ASRock sample as a 240 Hz monitor: its DisplayID timing (2560×1440 at 180 Hz) replaced by one at 240 Hz.</summary>
        static byte[] EdidWith240Hz()
        {
            byte[] e = EdidAsrock();
            var native = Edid.WithClockStep(Edid.Cvt(2560, 1440, 240, Edid.CvtMode.ReducedBlanking2), 10000);
            native.Preferred = true;
            Array.Copy(Edid.EncodeDisplayIdTiming(native, false), 0, e, 256 + 8, 20);
            e[256 + 126] = Edid.Checksum(e, 256 + 1, 126);
            Edid.FixBlock(e, 2);
            return e;
        }

        // ---- reading ----

        public static void EdidParsesAocSample()
        {
            string error;
            var p = Edid.Parse(EdidAoc(), out error);
            Run.True(p != null, "parses: " + error);
            if (p == null) return;
            Run.Equal(2, p.BlockCount, "blocks");
            Run.True(Edid.BlockChecksumOk(p.Bytes, 0) && Edid.BlockChecksumOk(p.Bytes, 1), "checksums");
            Run.Equal("AOC", p.Manufacturer, "manufacturer");
            Run.Equal(0x2702, p.Product, "product code");
            Run.Equal(577L, p.Serial, "serial");
            Run.Equal("1.4", p.Version + "." + p.Revision, "EDID version");
            Run.Equal("60x34", p.WidthCm + "x" + p.HeightCm, "size in cm");
            Run.Equal("597x336", p.WidthMm + "x" + p.HeightMm, "size in mm");
            Run.Near(26.97, p.Inches, 0.01, "diagonal");

            var n = p.Native;
            Run.Equal("2560x1440", n.Width + "x" + n.Height, "native size");
            Run.Equal(241500000L, n.ClockHz, "native pixel clock");
            Run.Near(59.950550, n.Hz, 0.000001, "native refresh");
            Run.Equal("48/32/80 3/5/33", n.HFront + "/" + n.HSync + "/" + n.HBack + " " + n.VFront + "/" + n.VSync + "/" + n.VBack, "native porches");
            Run.True(n.HPositive && n.VPositive, "native sync polarity");

            Run.Equal("Timing,Timing,Name,Range", string.Join(",", p.Descriptors.Select(q => q.Kind.ToString()).ToArray()), "descriptors");
            Run.Near(143.911545, p.Descriptors[1].Timing.Hz, 0.000001, "second DTD refresh");
            Run.Equal(592000000L, p.Descriptors[1].Timing.ClockHz, "second DTD clock");
            Run.True(p.Descriptors[1].Timing.HPositive && !p.Descriptors[1].Timing.VPositive, "second DTD polarity");
            Run.Equal("Q27G2G4", p.Name, "name");
            Run.Equal("48-144 230-230 600", p.Range.MinVHz + "-" + p.Range.MaxVHz + " " + p.Range.MinHKhz + "-" + p.Range.MaxHKhz + " " + p.Range.MaxClockMhz, "range limits");

            Run.Equal(1, p.Extensions.Count, "extensions");
            var c = p.Extensions[0].Cta;
            Run.True(c != null && c.Valid, "CTA-861 extension");
            Run.Equal(3, c.Revision, "CTA revision");
            Run.Equal(31, c.DtdOffset, "DTD offset");
            Run.Equal("2:12,1:3,4:3,3:5", string.Join(",", c.DataBlocks.Select(b => b.Tag + ":" + b.Length).ToArray()), "data blocks (tag:length)");
            Run.Equal(5, c.Timings.Count, "CTA DTDs");
            Run.Equal(6, c.FreeBytes, "CTA free bytes");
            Run.Equal(121, c.FreeOffset, "CTA free offset");
            Run.Equal("2560 × 1440 @ 119.998 Hz, 497.75 MHz", c.Timings[0].Text, "first CTA DTD");
            Run.Equal("1280 × 1440 @ 59.913 Hz, 156.00 MHz", c.Timings[4].Text, "last CTA DTD");
            Run.Equal(7, p.Timings.Count, "all detailed timings");

            Run.Equal("59.951, 143.912, 119.998, 99.946", string.Join(", ", Edid.NativeRates(p).Select(r => r.ToString("0.000")).ToArray()), "native rates");
            Run.Near(143.911545, Edid.MatchNativeRate(p, 144), 0.000001, "144 means the 143.912 the display has");
            Run.Equal(0.0, Edid.MatchNativeRate(p, 165), "no 165 Hz");
        }

        public static void EdidParsesDisplayIdSample()
        {
            var p = Edid.Parse(EdidAsrock());
            Run.True(p != null, "parses");
            if (p == null) return;
            Run.Equal(3, p.BlockCount, "blocks");
            Run.Equal("ASR", p.Manufacturer, "manufacturer");
            Run.Equal(0xABA1, p.Product, "product code");
            Run.Equal("Timing,Serial,Name,Range", string.Join(",", p.Descriptors.Select(q => q.Kind.ToString()).ToArray()), "descriptors");
            Run.Equal("PG27QFT1B", p.Name, "name");
            Run.Equal("H7JL0A001531", p.SerialText, "serial text");
            Run.Equal("48-180 265-265 900", p.Range.MinVHz + "-" + p.Range.MaxVHz + " " + p.Range.MinHKhz + "-" + p.Range.MaxHKhz + " " + p.Range.MaxClockMhz, "range limits with the +255 offsets");
            Run.Equal(47, p.Extensions[0].Cta.FreeBytes, "CTA free bytes");
            Run.Equal(2, p.Extensions[0].Cta.Timings.Count, "CTA DTDs");

            var d = p.Extensions[1].DisplayId;
            Run.True(d != null && d.Valid && d.ChecksumOk, "DisplayID extension and its section checksum");
            Run.Equal(0x12, d.Version, "DisplayID version");
            Run.Equal(121, d.SectionLength, "section length");
            Run.Equal(3, d.ProductType, "product type");
            Run.Equal(0, d.ExtensionCount, "extension count");
            Run.Equal("3/1/20", string.Join(",", d.DataBlocks.Select(b => b.Tag + "/" + b.Revision + "/" + b.Length).ToArray()), "data blocks (tag/revision/length)");
            Run.Equal(98, d.FreeBytes, "DisplayID free bytes");
            Run.Equal(1, d.Timings.Count, "Type I timings");
            var t = d.Timings[0];
            Run.Equal("2560x1440", t.Width + "x" + t.Height, "Type I size");
            Run.Equal(719720000L, t.ClockHz, "Type I clock");
            Run.Near(180.002001, t.Hz, 0.000001, "Type I refresh");
            Run.Equal("48/32/80 3/5/22", t.HFront + "/" + t.HSync + "/" + t.HBack + " " + t.VFront + "/" + t.VSync + "/" + t.VBack, "Type I porches");
            Run.True(t.HPositive && !t.VPositive && t.Preferred, "Type I polarity and preferred flag");
            Run.Equal("59.951, 180.002, 143.999, 119.998", string.Join(", ", Edid.NativeRates(p).Select(r => r.ToString("0.000")).ToArray()), "native rates, DisplayID's included");

            p = Edid.Parse(EdidAsus());
            Run.True(p != null, "ASUS parses");
            if (p == null) return;
            Run.Equal("AUS", p.Manufacturer, "ASUS manufacturer");
            Run.Equal("ROG PG27AQN", p.Name, "ASUS name");
            Run.Equal("1-360 313-313 1510", p.Range.MinVHz + "-" + p.Range.MaxVHz + " " + p.Range.MinHKhz + "-" + p.Range.MaxHKhz + " " + p.Range.MaxClockMhz, "ASUS range limits");
            Run.Equal(5, p.Extensions[0].Cta.FreeBytes, "ASUS CTA free bytes");
            d = p.Extensions[1].DisplayId;
            Run.True(d.Valid, "ASUS DisplayID");
            Run.Equal("15/0/10,3/1/100", string.Join(",", d.DataBlocks.Select(b => b.Tag + "/" + b.Revision + "/" + b.Length).ToArray()), "ASUS data blocks");
            Run.Equal(5, d.FreeBytes, "ASUS DisplayID free bytes");
            Run.Equal("2560 × 1440 @ 239.970 Hz, 1056.75 MHz|2560 × 1440 @ 359.980 Hz, 1504.40 MHz|1920 × 1080 @ 360.113 Hz, 970.00 MHz|2368 × 1332 @ 239.969 Hz, 908.75 MHz|2368 × 1332 @ 359.963 Hz, 1453.25 MHz",
                string.Join("|", d.Timings.Select(x => x.Text).ToArray()), "ASUS Type I timings");
            Run.Equal("171/5/3", d.Timings[0].VFront + "/" + d.Timings[0].VSync + "/" + d.Timings[0].VBack, "ASUS 240 Hz vertical porches");
            Run.Equal("59.951, 359.980, 239.970, 143.998, 119.998, 99.946", string.Join(", ", Edid.NativeRates(p).Select(r => r.ToString("0.000")).ToArray()), "ASUS native rates");
        }

        public static void EdidRejectsWhatIsNotAnEdid()
        {
            string error;
            Run.True(Edid.Parse(null, out error) == null && error != null, "null");
            Run.True(Edid.Parse(new byte[64], out error) == null, "too short");

            byte[] e = EdidAoc();
            e[1] = 0;
            Run.True(Edid.Parse(e, out error) == null && error.Contains("header"), "wrong header: " + error);

            e = EdidAoc();
            e[130]++;
            Run.True(Edid.Parse(e, out error) == null && error.Contains("block 1"), "wrong extension checksum: " + error);

            e = EdidAoc();
            e[20]++;
            Run.True(Edid.Parse(e, out error) == null && error.Contains("block 0"), "wrong base checksum: " + error);

            e = EdidAoc().Take(128).ToArray();
            Run.True(Edid.Parse(e, out error) == null && error.Contains("extension"), "an extension is missing: " + error);

            e = EdidAoc().Concat(new byte[128]).ToArray();
            var p = Edid.Parse(e, out error);
            Run.True(p != null && p.Bytes.Length == 256, "bytes after the announced blocks are ignored");

            Run.True(Edid.FromHex("00 ff zz") == null && Edid.FromHex("0") == null, "hex that isn't");
            Run.Equal("00 ff 10", Edid.ToHex(Edid.FromHex("0x00, 0xFF\n10")), "hex with 0x and commas");
            string where;
            Run.True(Edid.AddResolution(new byte[128], 2368, 1332, 60, out where) == null && where.Contains("header"), "AddResolution on a non-EDID: " + where);
        }

        // ---- timings ----

        static string EdidShape(Edid.Timing t)
        {
            return t.HTotal + "x" + t.VTotal + " " + t.HFront + "/" + t.HSync + "/" + t.HBack + " " + t.VFront + "/" + t.VSync + "/" + t.VBack
                + " " + (t.HPositive ? "+" : "-") + (t.VPositive ? "+" : "-") + " " + t.ClockHz;
        }

        public static void EdidCvtReducedBlankingMatchesPublishedTimings()
        {
            var rb = Edid.CvtMode.ReducedBlanking;
            // VESA's CVT-RB modes (the DMT table lists them; cvt -r prints the same).
            Run.Equal("2080x1111 48/32/80 3/5/23 +- 138500000", EdidShape(Edid.Cvt(1920, 1080, 60, rb)), "1920×1080 @ 60");
            Run.Equal("2720x1481 48/32/80 3/5/33 +- 241500000", EdidShape(Edid.Cvt(2560, 1440, 60, rb)), "2560×1440 @ 60");
            Run.Equal("2080x1235 48/32/80 3/6/26 +- 154000000", EdidShape(Edid.Cvt(1920, 1200, 60, rb)), "1920×1200 @ 60");
            Run.Equal("4000x2222 48/32/80 3/5/54 +- 533250000", EdidShape(Edid.Cvt(3840, 2160, 60, rb)), "3840×2160 @ 60");
            Run.Near(59.934, Edid.Cvt(1920, 1080, 60, rb).Hz, 0.001, "1920×1080 @ 60: the real rate");
            // The CVT-RB DTDs in the sample EDIDs.
            Run.Equal("1600x926 48/32/80 3/6/17 +- 88750000", EdidShape(Edid.Cvt(1440, 900, 60, rb)), "1440×900 @ 60 (AOC's DTD)");
            Run.Equal("2720x1525 48/32/80 3/5/77 +- 497750000", EdidShape(Edid.Cvt(2560, 1440, 120, rb)), "2560×1440 @ 120 (AOC's DTD)");
            Run.Equal("2720x1510 48/32/80 3/5/62 +- 410500000", EdidShape(Edid.Cvt(2560, 1440, 100, rb)), "2560×1440 @ 100 (AOC's DTD)");
            var aoc = Edid.Parse(EdidAoc());
            Run.True(aoc.Extensions[0].Cta.Timings[3].SameTiming(Edid.Cvt(1440, 900, 60, rb)), "1440×900 equals AOC's DTD field for field");
            Run.True(aoc.Extensions[0].Cta.Timings[1].VBack == 62 && aoc.Extensions[0].Cta.Timings[1].ClockHz == 410500000, "AOC's 100 Hz DTD is that timing");
            // The ASUS sample's 2368×1332 @ 240 has these totals and this clock (with the porches the other way round).
            var t = Edid.Cvt(2368, 1332, 240, rb);
            Run.Equal("2528x1498 908750000", t.HTotal + "x" + t.VTotal + " " + t.ClockHz, "2368×1332 @ 240");
            // The sync width names the shape.
            Run.Equal(4, Edid.Cvt(1776, 1332, 60, rb).VSync, "4:3");
            Run.Equal(7, Edid.Cvt(1280, 1024, 60, rb).VSync, "5:4");
            Run.Equal(10, Edid.Cvt(1280, 1440, 60, rb).VSync, "no named shape");
        }

        public static void EdidCvtReducedBlanking2()
        {
            var rb2 = Edid.CvtMode.ReducedBlanking2;
            Run.Equal("2000x1111 8/32/40 17/8/6 +- 133320000", EdidShape(Edid.Cvt(1920, 1080, 60, rb2)), "1920×1080 @ 60");
            Run.Equal("3920x2222 8/32/40 48/8/6 +- 522614000", EdidShape(Edid.Cvt(3840, 2160, 60, rb2)), "3840×2160 @ 60");
            Run.Near(60.0, Edid.Cvt(1920, 1080, 60, rb2).Hz, 0.0001, "1920×1080 @ 60: the real rate");

            // Everything else: the rules hold. 80 px and at least 460 µs of blanking, 8 lines of sync, 6 of back porch,
            // and the clock is the largest multiple of 1 kHz that doesn't exceed the rate asked for.
            int[][] sizes = { new[] { 2368, 1332 }, new[] { 2320, 1306 }, new[] { 1776, 1332 }, new[] { 2560, 1440 }, new[] { 1280, 960 }, new[] { 3840, 2160 } };
            double[] rates = { 60, 100, 119.998, 143.912, 144, 165, 240, 360, 480 };
            foreach (var s in sizes)
                foreach (double hz in rates)
                {
                    var t = Edid.Cvt(s[0], s[1], hz, rb2);
                    string what = s[0] + "×" + s[1] + " @ " + hz;
                    Run.True(t.Width == s[0] && t.Height == s[1] && t.HBlank == 80 && t.VSync == 8 && t.VBack == 6 && t.VFront >= 1, what + ": shape");
                    Run.True(t.ClockHz % 1000 == 0, what + ": clock in 1 kHz steps");
                    double exact = (double)t.HTotal * t.VTotal * hz;
                    Run.True(t.ClockHz <= exact + 0.001 && t.ClockHz > exact - 1000.001, what + ": clock = h total × v total × rate within a step");
                    Run.True(t.Hz <= hz + 1e-9 && t.Hz > hz * 0.9999, what + ": the real rate is a hair under");
                    double blankUs = t.VBlank * 1e6 * t.HTotal / t.ClockHz, lessUs = (t.VBlank - 1) * 1e6 * t.HTotal / t.ClockHz;
                    Run.True(blankUs >= 460, what + ": at least 460 µs of vertical blanking");
                    Run.True(t.VBlank == 15 || lessUs < 461, what + ": and no more lines than that needs");

                    var v1 = Edid.Cvt(s[0], s[1], hz, Edid.CvtMode.ReducedBlanking);
                    Run.True(v1.HBlank == 160 && v1.VFront == 3 && v1.VBack >= 6 && v1.ClockHz % 250000 == 0 && v1.Hz <= hz + 1e-9 && v1.Hz > hz * 0.998, what + ": v1 holds its rules too");
                }
            Run.True(Edid.Cvt(0, 1080, 60, rb2) == null && Edid.Cvt(1920, 1080, 0, rb2) == null && Edid.Cvt(1920, 1080, 3000, rb2) == null, "nonsense gives null");
        }

        // ---- encoders ----

        public static void EdidDtdRoundTrips()
        {
            // Every DTD the samples ship: decode, encode, the same 18 bytes.
            foreach (byte[] e in new[] { EdidAoc(), EdidAsrock(), EdidAsus() })
            {
                var p = Edid.Parse(e);
                var offsets = new List<int>();
                foreach (var d in p.Descriptors) if (d.Kind == Edid.DescriptorKind.Timing) offsets.Add(d.Offset);
                var cta = p.Extensions[0].Cta;
                for (int i = 0; i < cta.Timings.Count; i++) offsets.Add(128 + cta.DtdOffset + i * 18);
                foreach (int o in offsets)
                {
                    byte[] again = Edid.EncodeDtd(Edid.DecodeDtd(e, o));
                    Run.True(again != null && again.SequenceEqual(e.Skip(o).Take(18)), p.Name + ": the DTD at " + o + " encodes back to its bytes");
                }
            }

            // Ours: encode, decode, the same timing and image size.
            foreach (var mode in new[] { Edid.CvtMode.ReducedBlanking, Edid.CvtMode.ReducedBlanking2 })
            {
                var t = Edid.WithClockStep(Edid.Cvt(2368, 1332, 60, mode), 10000);
                t.WidthMm = 597;
                t.HeightMm = 336;
                byte[] b = Edid.EncodeDtd(t);
                Run.True(b != null && b.Length == 18, mode + ": 18 bytes");
                var back = Edid.DecodeDtd(b, 0);
                Run.True(back.SameTiming(t), mode + ": the timing comes back");
                Run.Equal("597x336", back.WidthMm + "x" + back.HeightMm, mode + ": image size");
                Run.Equal(0x1A, (int)b[17], mode + ": digital separate sync, H+ V-");
            }

            // The limits: 4095 px, 655.35 MHz, a 6-bit vertical front porch, a clock in 10 kHz.
            var big = Edid.Cvt(4095, 2000, 30, Edid.CvtMode.ReducedBlanking);
            Run.True(Edid.FitsDtd(big) && Edid.DecodeDtd(Edid.EncodeDtd(big), 0).SameTiming(big), "4095 wide fits");
            Run.True(Edid.EncodeDtd(Edid.Cvt(4096, 2000, 30, Edid.CvtMode.ReducedBlanking)) == null, "4096 wide doesn't");
            Run.True(Edid.EncodeDtd(Edid.Cvt(2368, 1332, 240, Edid.CvtMode.ReducedBlanking)) == null, "908.75 MHz doesn't");
            var rb2 = Edid.Cvt(2368, 1332, 144, Edid.CvtMode.ReducedBlanking2);
            Run.True(rb2.VFront > 63 && Edid.EncodeDtd(Edid.WithClockStep(rb2, 10000)) == null, "an RB2 front porch of " + rb2.VFront + " lines doesn't");
            Run.True(Edid.EncodeDtd(Edid.Cvt(3840, 2160, 60, Edid.CvtMode.ReducedBlanking2)) == null, "522.614 MHz isn't a multiple of 10 kHz");
            var rounded = Edid.WithClockStep(Edid.Cvt(3840, 2160, 60, Edid.CvtMode.ReducedBlanking2), 10000);
            Run.True(rounded.ClockHz == 522610000 && Edid.EncodeDtd(rounded) != null, "522.61 MHz is");
        }

        public static void EdidDisplayIdTimingRoundTrips()
        {
            // The Type I timings the samples ship: decode, encode, the same 20 bytes.
            foreach (byte[] e in new[] { EdidAsrock(), EdidAsus() })
            {
                var d = Edid.Parse(e).Extensions[1].DisplayId;
                var blk = d.DataBlocks.First(b => b.Tag == 3);
                for (int i = 0; i < blk.Length; i += 20)
                {
                    int o = 256 + blk.Offset + 3 + i;
                    byte[] again = Edid.EncodeDisplayIdTiming(Edid.DecodeDisplayIdTiming(e, o, false), false);
                    Run.True(again != null && again.SequenceEqual(e.Skip(o).Take(20)), "the Type I timing at " + o + " encodes back to its bytes");
                }
            }

            var t = Edid.Cvt(2368, 1332, 240, Edid.CvtMode.ReducedBlanking2);
            Run.True(t.ClockHz > Edid.MaxDtdClock, "2368×1332 @ 240 is beyond a DTD: " + t.ClockHz);

            // Type I: the clock in 10 kHz.
            Run.True(Edid.EncodeDisplayIdTiming(t, false) == null || t.ClockHz % 10000 == 0, "Type I wants a multiple of 10 kHz");
            var t10 = Edid.WithClockStep(t, 10000);
            byte[] one = Edid.EncodeDisplayIdTiming(t10, false);
            Run.True(one != null && one.Length == 20, "Type I: 20 bytes");
            Run.True(Edid.DecodeDisplayIdTiming(one, 0, false).SameTiming(t10), "Type I: the timing comes back");
            Run.Equal(4, one[3] & 0x0F, "Type I: 16:9");
            Run.Equal(8, Edid.EncodeDisplayIdTiming(Edid.WithClockStep(Edid.Cvt(2320, 1306, 240, Edid.CvtMode.ReducedBlanking2), 10000), false)[3] & 0x0F, "Type I: no named shape");

            // Type VII: the same layout, the clock in 1 kHz.
            byte[] seven = Edid.EncodeDisplayIdTiming(t, true);
            Run.True(seven != null && Edid.DecodeDisplayIdTiming(seven, 0, true).SameTiming(t), "Type VII: the timing comes back, to the kHz");
            Run.True(seven.Skip(3).SequenceEqual(one.Skip(3)), "Type VII differs from Type I in the clock only");

            var wide = Edid.Cvt(7680, 4320, 120, Edid.CvtMode.ReducedBlanking2);
            Run.True(Edid.DecodeDisplayIdTiming(Edid.EncodeDisplayIdTiming(wide, true), 0, true).SameTiming(wide), "7680×4320 @ 120 fits");
        }

        // ---- adding a resolution ----

        public static void EdidAddUsesAFreeBaseDescriptorFirst()
        {
            byte[] e = EdidWithFreeSlot(), before = (byte[])e.Clone();
            string where;
            byte[] r = Edid.AddResolution(e, 2368, 1332, 144, out where);
            Run.Equal("base block, descriptor 2", where, "where");
            Run.True(e.SequenceEqual(before), "the input isn't changed");
            EdidSound(r, "result");
            if (r == null) return;
            Run.Equal(e.Length, r.Length, "no block added");
            Run.True(r.Take(72).SequenceEqual(e.Take(72)) && r.Skip(90).Take(37).SequenceEqual(e.Skip(90).Take(37)) && r.Skip(128).SequenceEqual(e.Skip(128)),
                "only the descriptor and the checksum changed");
            var p = Edid.Parse(r);
            Run.Equal("Timing,Timing,Name,Range", string.Join(",", p.Descriptors.Select(q => q.Kind.ToString()).ToArray()), "descriptors");
            var t = p.Descriptors[1].Timing;
            // At 144 Hz RB2's front porch is beyond a DTD's 6 bits, so this one is CVT-RB v1, at the native 143.999 Hz.
            var expected = Edid.Cvt(2368, 1332, Edid.MatchNativeRate(Edid.Parse(e), 144), Edid.CvtMode.ReducedBlanking);
            Run.True(t.SameTiming(expected), "the CVT-RB timing: " + t.Text);
            Run.Equal("597x336", t.WidthMm + "x" + t.HeightMm, "image size copied from the native DTD");
            Run.True(p.Native.SameTiming(Edid.Parse(e).Native), "the native DTD is untouched");

            // At 60 Hz RB2 fits a DTD.
            r = Edid.AddResolution(e, 2368, 1332, 60, out where);
            Run.Equal("base block, descriptor 2", where, "60 Hz: where");
            t = Edid.Parse(r).Descriptors[1].Timing;
            Run.Equal("8/32/40", t.HFront + "/" + t.HSync + "/" + t.HBack, "60 Hz: CVT-RB2");
            Run.True(t.VSync == 8 && t.VBack == 6 && t.ClockHz % 10000 == 0, "60 Hz: CVT-RB2 vertical, clock in 10 kHz");
            Run.Near(59.95, t.Hz, 0.01, "60 Hz: the native 59.951");
        }

        public static void EdidAddUsesTheCtaExtensionNext()
        {
            byte[] e = EdidAsrock();
            string where;
            byte[] r = Edid.AddResolution(e, 2368, 1332, 144, out where);
            Run.Equal("CTA-861 extension (block 1)", where, "where");
            EdidSound(r, "result");
            if (r == null) return;
            Run.Equal(e.Length, r.Length, "no block added");
            Run.True(r.Take(128).SequenceEqual(e.Take(128)) && r.Skip(256).SequenceEqual(e.Skip(256)), "only the CTA block changed");
            var c = Edid.Parse(r).Extensions[0].Cta;
            Run.Equal(3, c.Timings.Count, "CTA DTDs");
            Run.Equal(29, c.FreeBytes, "CTA free bytes");
            Run.Equal("2368x1332 597x336", c.Timings[2].Width + "x" + c.Timings[2].Height + " " + c.Timings[2].WidthMm + "x" + c.Timings[2].HeightMm, "the new DTD");
            Run.Near(144.0, c.Timings[2].Hz, 0.1, "its rate");

            // A second one leaves 11 bytes; a third would leave fewer than 2 + 18, so it goes to DisplayID.
            r = Edid.AddResolution(r, 2320, 1306, 144, out where);
            Run.Equal("CTA-861 extension (block 1)", where, "second: where");
            Run.Equal(11, Edid.Parse(r).Extensions[0].Cta.FreeBytes, "second: CTA free bytes");
            byte[] third = Edid.AddResolution(r, 2304, 1296, 144, out where);
            Run.Equal("DisplayID extension (block 2)", where, "third: where");
            EdidSound(third, "third");
            Run.True(third.Take(256).SequenceEqual(r.Take(256)), "third: the CTA block keeps its padding");

            // A CTA block with exactly 20 bytes free takes one; with 19 it doesn't (2 bytes stay unused).
            byte[] aoc = EdidAoc();
            for (int i = 128 + 103; i < 128 + 127; i++) aoc[i] = 0;   // drop its last DTD: 24 bytes free
            Edid.FixBlock(aoc, 1);
            Run.Equal(24, Edid.Parse(aoc).Extensions[0].Cta.FreeBytes, "AOC without its last DTD");
            r = Edid.AddResolution(aoc, 2368, 1332, 144, out where);
            Run.Equal("CTA-861 extension (block 1)", where, "24 bytes free: where");
            Run.Equal(6, Edid.Parse(r).Extensions[0].Cta.FreeBytes, "24 bytes free: 6 left");
            aoc[128 + 103] = 0x01;   // five bytes of something that isn't padding
            Edid.FixBlock(aoc, 1);
            Edid.AddResolution(aoc, 2368, 1332, 144, out where);
            Run.Equal("new DisplayID extension (block 2)", where, "not padding after the DTDs: left alone");

            // A CTA block with nothing in it (DTD offset 0): the DTDs start at byte 4.
            byte[] empty = EdidAoc();
            for (int i = 128 + 2; i < 256; i++) empty[i] = 0;
            Edid.FixBlock(empty, 1);
            r = Edid.AddResolution(empty, 2368, 1332, 144, out where);
            Run.Equal("CTA-861 extension (block 1)", where, "empty CTA: where");
            EdidSound(r, "empty CTA");
            c = Edid.Parse(r).Extensions[0].Cta;
            Run.Equal("4 1 105", c.DtdOffset + " " + c.Timings.Count + " " + c.FreeBytes, "empty CTA: offset, DTDs, free");
        }

        public static void EdidAddUsesDisplayIdForWhatADtdCannotHold()
        {
            // 2368×1332 @ 240: 880 MHz, beyond a DTD. Into the DisplayID extension the monitor has.
            byte[] e = EdidWith240Hz();
            EdidSound(e, "the 240 Hz sample");
            string where;
            byte[] r = Edid.AddResolution(e, 2368, 1332, 240, out where);
            Run.Equal("DisplayID extension (block 2)", where, "where");
            EdidSound(r, "result");
            if (r == null) return;
            Run.Equal(e.Length, r.Length, "no block added");
            Run.True(r.Take(256).SequenceEqual(e.Take(256)), "base and CTA blocks untouched, free descriptor or not");
            var d = Edid.Parse(r).Extensions[1].DisplayId;
            Run.Equal("3/1/40", string.Join(",", d.DataBlocks.Select(b => b.Tag + "/" + b.Revision + "/" + b.Length).ToArray()), "the Type I block grew by one timing");
            Run.Equal(121, d.SectionLength, "section length");
            var t = d.Timings[1];
            var native = Edid.Parse(e).Extensions[1].DisplayId.Timings[0];
            Run.True(t.SameTiming(Edid.WithClockStep(Edid.Cvt(2368, 1332, native.Hz, Edid.CvtMode.ReducedBlanking2), 10000)), "CVT-RB2 at the native rate: " + t.Text);
            Run.True(t.ClockHz > Edid.MaxDtdClock && !t.Preferred, "beyond 655.35 MHz, not preferred");
            Run.True(d.Timings[0].SameTiming(native) && d.Timings[0].Preferred, "the monitor's own timing is untouched");

            // With a free base descriptor too: still DisplayID.
            byte[] free = EdidWith240Hz();
            for (int i = 72; i < 90; i++) free[i] = 0;
            free[75] = 0x10;
            Edid.FixBlock(free, 0);
            Edid.AddResolution(free, 2368, 1332, 240, out where);
            Run.Equal("DisplayID extension (block 2)", where, "a free descriptor doesn't matter at 240 Hz");

            // No DisplayID extension, no free descriptor, no room in CTA: a new DisplayID 1.2 block.
            e = EdidAoc();
            r = Edid.AddResolution(e, 2368, 1332, 144, out where);
            Run.Equal("new DisplayID extension (block 2)", where, "AOC: where");
            EdidSound(r, "AOC");
            Run.Equal(384, r.Length, "AOC: one block more");
            Run.Equal(2, (int)r[126], "AOC: byte 126");
            Run.True(r.Take(126).SequenceEqual(e.Take(126)) && r.Skip(128).Take(128).SequenceEqual(e.Skip(128)), "AOC: nothing else changed");
            Run.Equal("70 12 79 03 00 03 01 14", Edid.ToHex(r.Skip(256).Take(8).ToArray()), "AOC: the section header and the Type I block header");
            var p = Edid.Parse(r);
            d = p.Extensions[1].DisplayId;
            Run.Equal(98, d.FreeBytes, "AOC: DisplayID free bytes");
            Run.True(d.Timings.Count == 1 && d.Timings[0].SameTiming(Edid.WithClockStep(Edid.Cvt(2368, 1332, 143.911545, Edid.CvtMode.ReducedBlanking2), 10000)), "AOC: CVT-RB2 at 143.912");
            Run.Near(143.91, d.Timings[0].Hz, 0.01, "AOC: its rate");

            // A base block alone.
            e = EdidAoc().Take(128).ToArray();
            e[126] = 0;
            Edid.FixBlock(e, 0);
            r = Edid.AddResolution(e, 2368, 1332, 144, out where);
            Run.Equal("new DisplayID extension (block 1)", where, "base only: where");
            EdidSound(r, "base only");
            Run.Equal(1, (int)r[126], "base only: byte 126");

            // A DisplayID 2.0 section gets a Type VII timing (clock in 1 kHz).
            e = EdidWith240Hz();
            e[257] = 0x20;
            e[256 + 5] = 0x22;
            e[256 + 6] = 0x00;
            var native7 = Edid.Cvt(2560, 1440, 240, Edid.CvtMode.ReducedBlanking2);
            native7.Preferred = true;
            Array.Copy(Edid.EncodeDisplayIdTiming(native7, true), 0, e, 256 + 8, 20);
            e[256 + 126] = Edid.Checksum(e, 257, 126);
            Edid.FixBlock(e, 2);
            double rate = Edid.Parse(e).Extensions[1].DisplayId.Timings[0].Hz;
            r = Edid.AddResolution(e, 2368, 1332, rate, out where);
            Run.Equal("DisplayID extension (block 2)", where, "DisplayID 2.0: where");
            EdidSound(r, "DisplayID 2.0");
            d = Edid.Parse(r).Extensions[1].DisplayId;
            Run.Equal("34/0/40", string.Join(",", d.DataBlocks.Select(b => b.Tag + "/" + b.Revision + "/" + b.Length).ToArray()), "DisplayID 2.0: the Type VII block grew");
            Run.True(d.Timings[1].SameTiming(Edid.Cvt(2368, 1332, rate, Edid.CvtMode.ReducedBlanking2)), "DisplayID 2.0: CVT-RB2 to the kHz: " + d.Timings[1].Text);
        }

        public static void EdidAddOpensASecondDisplayIdBlockAndStopsWhenFull()
        {
            // The ASUS sample: no free descriptor, 5 bytes in CTA, 5 bytes in DisplayID.
            byte[] e = EdidAsus();
            string where;
            byte[] r = Edid.AddResolution(e, 2320, 1306, 240, out where);
            Run.Equal("new DisplayID extension (block 3)", where, "where");
            EdidSound(r, "result");
            if (r == null) return;
            Run.Equal(512, r.Length, "four blocks");
            Run.Equal(3, (int)r[126], "byte 126");
            var p = Edid.Parse(r);
            Run.Equal(1, p.Extensions[1].DisplayId.ExtensionCount, "the first DisplayID section counts the second");
            Run.Equal(5, p.Extensions[1].DisplayId.Timings.Count, "and keeps its timings");
            Run.Equal("70 12 79 00 00 03 01 14", Edid.ToHex(r.Skip(384).Take(8).ToArray()), "the second section: same version, product type 0");
            Run.Near(239.97, p.Extensions[2].DisplayId.Timings[0].Hz, 0.01, "at the native 239.970");
            Run.True(r.Skip(128).Take(128).SequenceEqual(e.Skip(128).Take(128)), "the CTA block is untouched");

            // The new block takes five timings; after that there is nowhere left and no fifth block.
            int added = 1;
            for (int k = 71; k > 40 && r != null; k--)
            {
                byte[] next = Edid.AddResolution(r, 32 * k, 18 * k, 240, out where);
                if (next == null) break;
                EdidSound(next, 32 * k + "×" + 18 * k);
                Run.Equal("DisplayID extension (block 3)", where, 32 * k + "×" + 18 * k + ": where");
                r = next;
                added++;
            }
            Run.Equal(5, added, "timings that fit the new block");
            Run.True(where.Contains("full") && where.Contains("4 blocks"), "then: " + where);
            Run.Equal(512, r.Length, "still four blocks");
            Run.Equal(10, Edid.Parse(r).Extensions.Sum(x => x.DisplayId == null ? 0 : x.DisplayId.Timings.Count), "ten DisplayID timings in all");
        }

        public static void EdidAddRefuses()
        {
            string where;
            // The ASUS sample already has 2368×1332 at 240 Hz (its owner added it).
            Run.True(Edid.AddResolution(EdidAsus(), 2368, 1332, 240, out where) == null, "a duplicate");
            Run.Equal("2368 × 1332 at 240 Hz is already there: DisplayID extension (block 2).", where, "a duplicate: why");
            Run.True(Edid.AddResolution(EdidAsus(), 2368, 1332, 144, out where) != null, "the same size at another rate is a different mode: " + where);
            Run.True(Edid.AddResolution(EdidAoc(), 1440, 900, 60, out where) == null && where.Contains("already there: CTA-861"), "a duplicate of a CTA DTD: " + where);
            Run.True(Edid.AddResolution(EdidAoc(), 2560, 1440, 144, out where) == null && where.Contains("already there: base block, descriptor 2"), "the native size at a native rate: " + where);

            // Adding twice.
            byte[] once = Edid.AddResolution(EdidAoc(), 2368, 1332, 144, out where);
            Run.True(Edid.AddResolution(once, 2368, 1332, 144, out where) == null && where.Contains("already there"), "the same mode twice: " + where);

            // A rate the native size doesn't have.
            Run.True(Edid.AddResolution(EdidAoc(), 2368, 1332, 240, out where) == null, "240 Hz on a 144 Hz monitor");
            Run.Equal("The display has no 240 Hz at 2560 × 1440 (it has 59.951, 143.912, 119.998, 99.946 Hz).", where, "240 Hz: why");
            Run.True(Edid.AddResolution(EdidAoc(), 2368, 1332, 75, out where) == null, "75 Hz, inside the range limits but not a native rate");

            Run.True(Edid.AddResolution(EdidAoc(), 2720, 1530, 60, out where) == null && where.Contains("larger"), "above native: " + where);
            Run.True(Edid.AddResolution(EdidAoc(), 0, 0, 60, out where) == null, "no size");

            byte[] bad = EdidAoc();
            bad[200]++;
            Run.True(Edid.AddResolution(bad, 2368, 1332, 144, out where) == null && where.Contains("checksum"), "an EDID with a wrong checksum: " + where);

            // Four blocks, all of them full: the ASUS sample with a block the engine doesn't know appended.
            byte[] four = EdidAsus().Concat(new byte[128]).ToArray();
            four[384] = 0x40;
            four[126] = 3;
            Edid.FixBlock(four, 0);
            Edid.FixBlock(four, 3);
            Run.True(Edid.AddResolution(four, 2320, 1306, 240, out where) == null && where.Contains("full"), "a full EDID: " + where);
        }

        // ---- the size for a diagonal ----

        static string EdidSize(Edid.Size s)
        {
            return s == null ? "null" : s.Width + "x" + s.Height + " " + s.Inches.ToString("0.00") + " " + s.Percent.ToString("0.00");
        }

        public static void EdidSizeForADiagonal()
        {
            // The documented one: 25" on a 27" 2560×1440 panel is 2368 × 1332, exactly 16:9, 92.5 % of the diagonal.
            Run.Equal("2368x1332 24.98 92.50", EdidSize(Edid.SizeFor(2560, 1440, 27, 25, 16, 9)), "25\" of 27\"");
            // 24.5": the width a multiple of 8, the height the even number next to the exact 1305.
            Run.Equal("2320x1306 24.47 90.64", EdidSize(Edid.SizeFor(2560, 1440, 27, 24.5, 16, 9)), "24.5\" of 27\"");
            Run.Equal("2272x1278 23.96 88.75", EdidSize(Edid.SizeFor(2560, 1440, 27, 24, 16, 9)), "24\" of 27\"");
            // 4:3 can't be 24" on this panel: it is as tall as the panel at 22.06".
            Run.Equal("1920x1440 22.06 81.71", EdidSize(Edid.SizeFor(2560, 1440, 27, 24, 4, 3)), "24\" 4:3 inside 16:9");
            Run.Equal("1760x1320 20.22 74.90", EdidSize(Edid.SizeFor(2560, 1440, 27, 20.25, 4, 3)), "20.25\" 4:3");
            // The stretch inside the area: the largest 4:3 in 2368 × 1332 is 1776 × 1332 (docs).
            var area = Edid.SizeFor(2368, 1332, 24.975, 24.975, 4, 3);
            Run.Equal("1776x1332", area.Width + "x" + area.Height, "the largest 4:3 inside 2368 × 1332");
            // Never above native.
            Run.Equal("2560x1440 27.00 100.00", EdidSize(Edid.SizeFor(2560, 1440, 27, 27, 16, 9)), "the whole panel");
            Run.Equal("2560x1440 27.00 100.00", EdidSize(Edid.SizeFor(2560, 1440, 27, 32, 16, 9)), "a target above the panel");
            Run.Equal("3840x2160 32.00 100.00", EdidSize(Edid.SizeFor(3840, 2160, 32, 40, 16, 9)), "4K");
            Run.Equal("2304x1296 24.30 90.00", EdidSize(Edid.SizeFor(2560, 1440, 27, 24.3, 16, 9)), "24.3\"");
            Run.Equal("null", EdidSize(Edid.SizeFor(2560, 1440, 0, 24, 16, 9)), "no panel size");

            // The rule, over a sweep: even, width a multiple of 8, within the target, within 0.5 % of the shape, within
            // native, and the next width up doesn't fit.
            double nd = Math.Sqrt(2560.0 * 2560 + 1440.0 * 1440);
            int[][] shapes = { new[] { 16, 9 }, new[] { 4, 3 }, new[] { 16, 10 }, new[] { 5, 4 } };
            foreach (var a in shapes)
                for (double inches = 15; inches <= 27; inches += 0.25)
                {
                    var s = Edid.SizeFor(2560, 1440, 27, inches, a[0], a[1]);
                    string what = inches + "\" " + a[0] + ":" + a[1] + " = " + EdidSize(s);
                    Run.True(s != null && s.Width % 8 == 0 && s.Height % 2 == 0 && s.Width <= 2560 && s.Height <= 1440, what + ": even, multiple of 8, within native");
                    Run.True(s.Inches <= inches + 1e-9, what + ": within the target");
                    Run.True(Math.Abs((double)s.Width / s.Height / ((double)a[0] / a[1]) - 1) <= 0.005, what + ": the shape");
                    Run.Near(27 * Math.Sqrt((double)s.Width * s.Width + (double)s.Height * s.Height) / nd, s.Inches, 1e-9, what + ": the exact diagonal");
                    double wider = s.Width + 8, lower = Math.Floor(wider * a[1] / a[0] / 2) * 2;
                    Run.True(wider > 2560 || lower > 1440 || 27 * Math.Sqrt(wider * wider + lower * lower) / nd > inches
                        || Math.Abs(wider / lower / ((double)a[0] / a[1]) - 1) > 0.005, what + ": the next width up doesn't fit");
                }
        }

        // ---- the listing ----

        public static void EdidDescribeListsEveryTiming()
        {
            string text = Edid.Describe(EdidAsrock());
            string[] expected =
            {
                "ASR ABA1 · PG27QFT1B · EDID 1.4 · 27.0″ · 3 blocks",
                "Range limits: 48–180 Hz, 265–265 kHz, up to 900 MHz",
                "Native rates: 59.951, 180.002, 143.999, 119.998 Hz",
                "Block 0, base:",
                "  1: 2560 × 1440 @ 59.951 Hz, 241.50 MHz (native)",
                "  2: serial",
                "  3: name \"PG27QFT1B\"",
                "  4: range limits",
                "Block 1, CTA-861 revision 3: 7 data blocks, 47 bytes free",
                "  2560 × 1440 @ 143.999 Hz, 583.60 MHz",
                "  2560 × 1440 @ 119.998 Hz, 497.75 MHz",
                "Block 2, DisplayID 1.2: 1 data blocks, 98 bytes free",
                "  2560 × 1440 @ 180.002 Hz, 719.72 MHz (preferred)"
            };
            Run.Equal(string.Join("\n", expected), text, "the ASRock sample");
            Run.True(Edid.Describe(EdidWithFreeSlot()).Contains("  2: free"), "a free descriptor");
            Run.Equal("The EDID is shorter than one 128-byte block.", Edid.Describe(new byte[3]), "not an EDID");
        }
    }
}
