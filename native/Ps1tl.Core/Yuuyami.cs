using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Ps1tl;

/// <summary>
/// Yuuyami Doori Tankentai (SLPS-02274, Spike 1999).
///
/// Dialogue lives in ACD files inside DATA/FILELINK.FLB. Each ACD carries its own font page:
///   header +08: 13 u32 = file size, then section offsets s[0..11]. 'ACD ' (space) files are the same, but
///               +08 holds 0x20 (or 0) instead of the size; they carry extra timing tables in s[0]..s[3]
///   s[4]..s[5]  messages: u16 pairs. (glyph, 0x0200) = glyph + more follows, (glyph, 0) = last glyph,
///               (0xFFFF, ctrl) closes the message (ctrl preserved); a track starting mid-row also splits it
///   s[5]        font header; s[6]..s[7] per-glyph metrics (0, 0, x, width) -> glyph count
///   s[8]..s[9]  texture table (12 bytes), s[9]..s[10] tail table (8 bytes): u32 offsets relative to s[10]
///   s[10]       16-colour palette, glyphs follow at +0x20 (4bpp 16x16, 128 bytes each; 15 = background)
/// Glyph order differs per file, so lines are keyed by glyph *bitmaps*, not indices.
///
/// Menus, the notebook and map labels are EAS image containers (text baked into pictures):
///   header +08: u32 section offsets t[0..]; t[6] = pixel data start
///   t[0]..t[1]  image records, 20 bytes: u32 pixel offset (rel. t[6]), u16 palette index, u16 palette count,
///               u16 hotspot x, y, u16 w, h, u16 bits per pixel (4/8), u16 0
///   t[1]..t[2]  palette records, 8 bytes: u32 offset (rel. t[6]), u32 bpp; 16 or 256 PS1 15-bit colours
/// </summary>
public static class Yuuyami
{
    public static readonly string[] Serials = ["SLPS_022.74"];
    public const string Name = "Yuuyami Doori Tankentai";
    /// <summary>bump when Extract() finds more text; open projects merge the new lines in</summary>
    public const int ExtractorVersion = 5;   // 4: text after a file's last 0xFFFF; 5: rows split where a track starts
    /// <summary>largest font page the game itself ships; growing past it is untested</summary>
    public const int MaxGlyphs = 465;
    const string FlbPath = "/DATA/FILELINK.FLB";

    static int U32(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadInt32LittleEndian(d[o..]);
    static int U16(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadUInt16LittleEndian(d[o..]);

    /// <summary>
    /// A choice row ("おいていく　／　一緒にいる") is coloured by the game per region of its track's frames (s[1]):
    /// option A, the separator, option B. Each English option (split at "/") is packed into cells of its own and the
    /// separator gets its own cell, so RetimeTracks can put the frame boundaries exactly between them (Anchors: the
    /// Japanese run boundary -> the English one, as offsets in the message). layout: the Japanese runs (length, separator?).
    /// -> null when the English doesn't have the same number of options
    /// </summary>
    public static (List<string> Tokens, List<(int Old, int New)> Anchors)? ChoiceTokens(string text, List<(int Len, bool Sep)> layout, Func<string, List<string>> pack)
    {
        var opts = text.Split('/').Select(o => o.Trim()).ToList();
        if (opts.Count != layout.Count(r => !r.Sep) || opts.Any(o => o.Length == 0)) return null;
        var outp = new List<string>();
        var anchors = new List<(int, int)>();
        int k = 0, old = 0;
        foreach (var (len, sep) in layout)
        {
            if (old > 0) anchors.Add((old, outp.Count));
            outp.AddRange(sep ? pack(" / ") : pack(opts[k++]));
            old += len;
        }
        return (outp, anchors);
    }

    /// <summary>a choice row's Japanese as runs of option text and separators (／ and spaces), or null if it is not a
    /// choice or its Japanese doesn't have one character per glyph</summary>
    public static List<(int Len, bool Sep)>? ChoiceLayout(Line l)
    {
        var ja = l.Ja.EnumerateRunes().Select(r => r.ToString()).ToList();
        if (!l.Ja.Contains('／') || ja.Count != l.Glyphs.Count) return null;
        var runs = new List<(int Len, bool Sep)>();
        foreach (var c in ja)
        {
            bool sep = c is "／" or "　" or " " or "/";
            if (runs.Count > 0 && runs[^1].Sep == sep) runs[^1] = (runs[^1].Len + 1, sep);
            else runs.Add((1, sep));
        }
        return runs.Count(r => !r.Sep) >= 2 ? runs : null;
    }

    /// <summary>Delays[m] = the frames each glyph of message m waits before the next (entry byte 3; typing speed)</summary>
    /// Track[m] = the text track (s[0]) message m starts in, -1 if none: one track's rows are shown together and the
    /// script clears the box between tracks, so a track is a page.</summary>
    public sealed record Acd(List<byte[]> Bitmaps, List<(int X, int W)> Metrics, List<(List<int> Seq, int Ctrl)> Msgs, List<List<int>> Delays, List<int> Track)
    {
        /// <summary>message m+1 is shown on the same page as m</summary>
        public bool SamePage(int m) => m + 1 < Msgs.Count && Track[m] >= 0 && Track[m] == Track[m + 1];
    }

    /// <summary>-> glyph bitmaps, metrics, messages; null if the file is not an ACD with a font</summary>
    public static Acd? ParseAcd(byte[] d)
    {
        if (d.Length <= 0x50 || !(d[0] == 'A' && d[1] == 'C' && d[2] == 'D' && (d[3] == 0 || d[3] == ' '))
            || !d.AsSpan(4, 4).SequenceEqual("1.20"u8)) return null;
        var s = Enumerable.Range(0, 12).Select(i => U32(d, 12 + i * 4)).ToArray();
        if (s[6] - s[5] < 0x14) return null;
        int n = (s[7] - s[6]) / 4;   // metrics size; the u16 count in the font header undercounts some files
        int g0 = s[10] + 0x20;
        var bitmaps = Enumerable.Range(0, n).Select(i => d[(g0 + i * 128)..(g0 + (i + 1) * 128)]).ToList();
        var metrics = Enumerable.Range(0, n).Select(i => ((int)d[s[6] + i * 4 + 2], (int)d[s[6] + i * 4 + 3])).ToList();
        var msgs = new List<(List<int>, int)>();
        var cur = new List<int>();
        var curD = new List<int>();
        var delays = new List<List<int>>();
        // text tracks: entries start..start+count (s[0] table, 16 bytes: id, 1, id, 1, start, count, duration, flags)
        var tracks = new List<(int Start, int End)>();
        for (int at = s[0]; at + 16 <= s[1]; at += 16)
            if ((U16(d, at + 14) & 1) != 0 && U16(d, at + 10) > 0) tracks.Add((U16(d, at + 8), U16(d, at + 8) + U16(d, at + 10)));
        var trackStarts = tracks.Select(t => t.Start).ToHashSet();
        int count = (s[5] - s[4]) / 2;
        for (int k = 0; k + 1 < count; k += 2)
        {
            // a track that starts mid-row (e.g. a choice, then the next line on the same row) splits the row into
            // separate messages; the first part has no 0xFFFF (NoEnd)
            if (cur.Count > 0 && trackStarts.Contains(k / 2)) { msgs.Add((cur, NoEnd)); delays.Add(curD); cur = new List<int>(); curD = new List<int>(); }
            int a = U16(d, s[4] + k * 2), b = U16(d, s[4] + k * 2 + 2);
            if (a == 0xFFFF) { msgs.Add((cur, b)); delays.Add(curD); cur = new List<int>(); curD = new List<int>(); }
            else if (a < n) { cur.Add(a); curD.Add(b >> 8); }
            else throw new InvalidDataException($"glyph {a} out of range {n}");
        }
        if (cur.Count > 0) { msgs.Add((cur, NoEnd)); delays.Add(curD); }   // trailing text without a 0xFFFF
        var track = new List<int>();
        int e = 0;
        foreach (var (seq, ctrl) in msgs)
        {
            track.Add(tracks.FindIndex(t => t.Start <= e && e < t.End));
            e += seq.Count + (ctrl < 0 ? 0 : 1);
        }
        return new Acd(bitmaps, metrics, msgs, delays, track);
    }

    static string Md5Hex(ReadOnlySpan<byte> b) => Convert.ToHexStringLower(MD5.HashData(b));
    public static string Gid(byte[] bitmap) => Md5Hex(bitmap)[..12];
    public static string LineKey(IEnumerable<string> ids) => Md5Hex(Encoding.UTF8.GetBytes(string.Concat(ids)))[..16];

    static byte[] Archive(Disc disc)
    {
        var (lba, size) = disc.Files()[FlbPath];
        return disc.Read(lba, size);
    }

    /// <summary>every unique dialogue line, the glyph bitmaps, and the picture catalog</summary>
    public static Script Extract(Disc disc)
    {
        var glyphs = new Dictionary<string, GlyphEntry>();
        var lines = new Dictionary<string, Line>();
        var pics = new Dictionary<string, byte[]>();
        foreach (var (path, data) in Flb.Walk(Archive(disc)))
        {
            if (ParseEas(data) != null) pics[path] = data;
            var p = ParseAcd(data);
            if (p == null) continue;
            var ids = p.Bitmaps.Select(Gid).ToList();
            for (int i = 0; i < ids.Count; i++)
                glyphs.TryAdd(ids[i], new GlyphEntry { Bitmap = Convert.ToHexStringLower(p.Bitmaps[i]), X = p.Metrics[i].X, W = p.Metrics[i].W });
            for (int m = 0; m < p.Msgs.Count; m++)
            {
                var seq = p.Msgs[m].Seq;
                if (seq.Count == 0) continue;
                var g = seq.Select(k => ids[k]).ToList();
                var key = LineKey(g);
                if (!lines.TryGetValue(key, out var line)) lines[key] = line = new Line { Key = key, Glyphs = g };
                line.Refs.Add($"{path}:{m}");
            }
        }
        return new Script { Game = Name, Glyphs = glyphs, Lines = lines.Values.ToList(), Images = ImageCatalog(pics) };
    }

    /// <summary>
    /// rewrite one ACD with English. lookup(message index, line key) -> text or null; cell(token) -> glyph cell;
    /// pack(text) -> tokens, one glyph cell each (several narrow letters can share a cell, see Font.Pack).
    /// Glyph slots used by untranslated lines keep their Japanese bitmap; free slots get English cells, and the
    /// font page grows when needed (new cells go at the end of the glyph block; every texture offset in the
    /// texture/tail tables, relative to s[10] and all past the glyphs, moves by the same amount).
    /// -> (new bytes or null if unchanged, problem or null)
    /// </summary>
    public static (byte[]? Data, string? Problem) InsertAcd(byte[] d, Func<int, string, string?> lookup,
        Func<string, (byte[] Bitmap, int X, int W)> cell, Func<string, List<string>> pack,
        Func<string, int>? width = null, List<int>? tooWide = null, Font? small = null,
        Func<string, List<(int Len, bool Sep)>?>? choice = null)
    {
        var p = ParseAcd(d);
        if (p == null) return (null, null);
        int n = p.Bitmaps.Count;
        var ids = p.Bitmaps.Select(Gid).ToList();
        var keys = p.Msgs.Select(mm => mm.Seq.Count > 0 ? LineKey(mm.Seq.Select(k => ids[k])) : null).ToList();
        var texts = p.Msgs.Select((mm, m) => keys[m] != null ? lookup(m, keys[m]!) : null).ToList();
        if (!texts.Any(t => !string.IsNullOrEmpty(t))) return (null, null);
        var layouts = keys.Select(k => k != null ? choice?.Invoke(k) : null).ToList();
        var plain = width != null ? Reflow(p, texts, width) : texts;
        var stacked = width != null && small != null ? Stack(p, plain, width, small) : plain;
        for (int m = 0; m < texts.Count; m++)   // choice rows keep their own text and layout (see ChoiceTokens)
            if (layouts[m] != null) { plain[m] = texts[m]; stacked[m] = texts[m]; }
        var longChoice = new HashSet<int>();
        var anchors = new Dictionary<int, List<(int Old, int New)>>();
        var keep = new HashSet<int>();
        for (int m = 0; m < p.Msgs.Count; m++) if (string.IsNullOrEmpty(plain[m])) keep.UnionWith(p.Msgs[m].Seq);
        var free = Enumerable.Range(0, n).Where(i => !keep.Contains(i)).ToList();
        bool Fits(List<List<string>?> toks) =>
            n + Math.Max(0, toks.Where(t => t != null).SelectMany(t => t!).Distinct().Count() - free.Count) <= MaxGlyphs;
        // fewest sprites first; fall back to one letter per cell, then ALL CAPS (fewer distinct letters).
        // Stacked rows need their own cells; while the font page can't take them all, the row that overflows
        // least goes back to plain text first.
        var tries = new Func<string, List<string>>[] { pack, t => t.Select(c => c.ToString()).ToList(), t => t.ToUpperInvariant().Select(c => c.ToString()).ToList() };
        List<string> Row(int m, string t, Func<string, List<string>> f)
        {
            if (t[0] == StackMark) return StackTokens(t, small!);
            if (layouts[m] is { } lay)
            {
                if (ChoiceTokens(t, lay, f) is var (ct, an)) { anchors[m] = an; return ct; }
                longChoice.Add(m);   // the English lost the options: packed as plain text and reported
            }
            return f(t);
        }
        List<List<string>?> Tok(List<string?> src, Func<string, List<string>> f) =>
            src.Select((t, m) => string.IsNullOrEmpty(t) ? null : Row(m, t, f)).ToList();
        var cur = stacked.ToList();
        var unstack = Enumerable.Range(0, cur.Count).Where(m => cur[m] != plain[m]).OrderBy(m => width!(plain[m]!)).ToList();
        List<List<string>?>? toks = null;
        for (int i = 0; toks == null; i++)
        {
            foreach (var f in tries)
            {
                var cand = Tok(cur, f);
                if (Fits(cand)) { toks = cand; break; }
            }
            if (toks != null || i >= unstack.Count) break;
            cur[unstack[i]] = plain[unstack[i]];
        }
        if (toks == null) return (null, $"font page would exceed {MaxGlyphs} glyphs");
        if (width != null)   // rows that will still wrap in game, and choices whose options don't fit their cells
            tooWide?.AddRange(Enumerable.Range(0, cur.Count).Where(m => longChoice.Contains(m) ||
                cur[m] is { Length: > 0 } t && t[0] != StackMark && layouts[m] == null && width(t) > RowLimit(p)));
        var chars = toks.Where(t => t != null).SelectMany(t => t!).Distinct().Order(StringComparer.Ordinal).ToList();
        int grow = Math.Max(0, chars.Count - free.Count);
        var slots = free.Concat(Enumerable.Range(n, grow)).ToList();
        var slot = chars.Select((c, i) => (c, slots[i])).ToDictionary(t => t.c, t => t.Item2);

        var s = Enumerable.Range(0, 13).Select(i => U32(d, 8 + i * 4)).ToArray();
        var sec = s[1..];
        int gend = 0x20 + n * 128;   // glyph block end, relative to s[10]
        using var stream = new MemoryStream();
        Span<byte> pair = stackalloc byte[4];
        for (int m = 0; m < p.Msgs.Count; m++)
        {
            var idx = toks[m] != null ? toks[m]!.Select(c => slot[c]).ToList() : p.Msgs[m].Seq;
            var dl = GlyphDelays(p, m, toks[m]?.Count);
            for (int j = 0; j < idx.Count; j++)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(pair, (ushort)idx[j]);
                BinaryPrimitives.WriteUInt16LittleEndian(pair[2..], (ushort)(dl[j] << 8));
                stream.Write(pair);
            }
            if (p.Msgs[m].Ctrl < 0) continue;   // text that runs to the end of the stream without a 0xFFFF
            BinaryPrimitives.WriteUInt16LittleEndian(pair, 0xFFFF);
            BinaryPrimitives.WriteUInt16LittleEndian(pair[2..], (ushort)p.Msgs[m].Ctrl);
            stream.Write(pair);
        }
        var met = new byte[sec[7] - sec[6] + 4 * grow];
        d.AsSpan(sec[6], sec[7] - sec[6]).CopyTo(met);
        var tables = d[sec[7]..sec[10]];
        for (int o = sec[8] - sec[7]; o < sec[9] - sec[7]; o += 12)   // texture table: u32 offset, u16 w, u16 h, u32
            BinaryPrimitives.WriteInt32LittleEndian(tables.AsSpan(o), U32(tables, o) + 128 * grow);
        for (int o = sec[9] - sec[7]; o < sec[10] - sec[7]; o += 8)   // tail table: u32 offset, u32
            BinaryPrimitives.WriteInt32LittleEndian(tables.AsSpan(o), U32(tables, o) + 128 * grow);
        var glyphs = new byte[gend + 128 * grow];
        d.AsSpan(sec[10], gend).CopyTo(glyphs);
        foreach (var (c, i) in slot)
        {
            var (bm, x, w) = cell(c);
            bm.CopyTo(glyphs, 0x20 + i * 128);
            met[i * 4] = 0; met[i * 4 + 1] = 0; met[i * 4 + 2] = (byte)x; met[i * 4 + 3] = (byte)w;
        }
        var fhdr = d[sec[5]..sec[6]];
        foreach (var o in new[] { 8, 14 })   // glyph count fields, when they hold the real count
            if (U16(fhdr, o) == n) BinaryPrimitives.WriteUInt16LittleEndian(fhdr.AsSpan(o), (ushort)(n + grow));
        var outp = (byte[])[.. d.AsSpan(0, sec[4]), .. stream.ToArray(), .. fhdr, .. met, .. tables, .. glyphs, .. d.AsSpan(sec[10] + gend)];
        var nw = new int[7];
        nw[0] = sec[4]; nw[1] = sec[4] + (int)stream.Length; nw[2] = nw[1] + fhdr.Length; nw[3] = nw[2] + met.Length;
        nw[4] = nw[3] + sec[8] - sec[7]; nw[5] = nw[3] + sec[9] - sec[7]; nw[6] = nw[3] + sec[10] - sec[7];
        if (d[3] == 0) s[0] = outp.Length;   // 'ACD ' files keep their +08 value
        Array.Copy(nw, 0, s, 5, 7);
        var news = toks.Select(t => t?.Count).ToList();
        if (!RetimeTracks(outp, sec[0], sec[1], p, news, anchors)) return (null, "track table not before the messages");
        for (int i = 0; i < 13; i++) BinaryPrimitives.WriteInt32LittleEndian(outp.AsSpan(8 + i * 4), s[i]);
        return (outp, null);
    }

    /// <summary>
    /// s[0] is the track table (16 bytes: u16 id, 1, id, 1, start, count, duration, flags). A text track (flags bit 0)
    /// plays entries start..start+count of the message stream; entry = s16 glyph, s8 font, u8 delay in frames
    /// (glyph -1 = row end). The game shows only entries whose running delay total is below duration, so after the
    /// text changes length every text track must get the new start, count and duration (sum of the delays), or the
    /// English is cut at the old Japanese positions and runs into the next track. Rewritten in place in d.
    /// newText[m] = English cells of message m, or null when it keeps its Japanese (each message still ends with its 0xFFFF entry).
    /// -> false if the table is not where it can be patched (before the messages)
    /// </summary>
    public static bool RetimeTracks(byte[] d, int t0, int t1, Acd p, List<int?> newText,
        Dictionary<int, List<(int Old, int New)>>? anchors = null)
    {
        if (t1 <= t0) return true;
        int msgs = U32(d, 12 + 4 * 4), f0s = U32(d, 12 + 4 * 1), f1s = U32(d, 12 + 4 * 2);
        if (t1 > msgs) return false;
        int M = p.Msgs.Count;
        var newLen = newText.Select((t, m) => t ?? p.Msgs[m].Seq.Count).ToList();
        var oldStart = new int[M + 1];
        var newStart = new int[M + 1];
        var delay = new List<int>();      // per new entry
        var oldDelay = new List<int>();   // per original entry
        for (int m = 0; m < M; m++)
        {
            int ff = p.Msgs[m].Ctrl < 0 ? 0 : 1;   // its 0xFFFF entry, if any
            oldStart[m + 1] = oldStart[m] + p.Msgs[m].Seq.Count + ff;
            newStart[m + 1] = newStart[m] + newLen[m] + ff;
            delay.AddRange(GlyphDelays(p, m, newText[m]));
            oldDelay.AddRange(p.Delays[m]);
            if (ff == 1) { delay.Add(p.Msgs[m].Ctrl >> 8 & 0xFF); oldDelay.Add(p.Msgs[m].Ctrl >> 8 & 0xFF); }
        }
        // an old entry index -> the new one: same message, same place (row start / its 0xFFFF / a choice row's option
        // boundary / otherwise scaled)
        int Map(int e)
        {
            if (e >= oldStart[M]) return newStart[M];
            int m = Array.BinarySearch(oldStart, e);
            if (m < 0) m = ~m - 1;
            int o = e - oldStart[m], oldLen = p.Msgs[m].Seq.Count;
            if (o == 0) return newStart[m];
            if (o >= oldLen) return newStart[m] + newLen[m] + (o - oldLen);
            if (anchors?.GetValueOrDefault(m) is { } an && an.FindIndex(x => x.Old == o) is int i && i >= 0) return newStart[m] + an[i].New;
            return newStart[m] + Math.Clamp((int)Math.Round((double)o * newLen[m] / oldLen), 0, newLen[m]);
        }
        for (int at = t0; at + 16 <= t1; at += 16)
        {
            if ((U16(d, at + 14) & 1) == 0 || U16(d, at + 10) == 0) continue;
            int start = U16(d, at + 8), end = start + U16(d, at + 10);
            int ns = Map(start), ne = Map(end);
            BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(at + 8), (ushort)ns);
            BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(at + 10), (ushort)(ne - ns));
            BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(at + 12), (ushort)delay.Skip(ns).Take(ne - ns).Sum());
            // the track's frames (s[1], 8 bytes: u16 x3, u8 duration, u8 flags) split its time into regions; choice
            // menus colour the glyphs of each region (0x8005E7D4 / 0x8006617C), so their durations must follow the text.
            // Frames whose boundaries don't fall on entries in the original are left alone.
            int fa = U16(d, at), fn = U16(d, at + 2);
            if (fn < 2 || f0s + 8 * (fa + fn) > Math.Min(f1s, msgs)) continue;
            var bounds = new List<int>();   // original entry index where each frame ends
            int e = start, acc = 0, time = 0;
            bool aligned = true;
            for (int k = 0; k < fn; k++)
            {
                time += d[f0s + 8 * (fa + k) + 6];
                while (e < end && acc + oldDelay[e] <= time) acc += oldDelay[e++];
                if (acc != time) aligned = false;
                bounds.Add(e);
            }
            if (!aligned)
            {
                // timing-only frames (e.g. an animation stepping every 3 units): stretch them to the new duration so
                // they still add up to it; the rounding goes on the last frame
                int oldDur = time, newDur = delay.Skip(ns).Take(ne - ns).Sum(), cum = 0, done = 0;
                if (oldDur == 0) continue;
                for (int k = 0; k < fn; k++)   // round the running total, so the frames add up exactly
                {
                    int at6 = f0s + 8 * (fa + k) + 6;
                    cum += d[at6];
                    int upto = (int)Math.Round(cum * (double)newDur / oldDur);
                    d[at6] = (byte)Math.Min(255, upto - done);   // ponytail: a frame past 255 units would clamp
                    done = upto;
                }
                continue;
            }
            bounds[^1] = end;
            int prev = ns;
            for (int k = 0; k < fn; k++)
            {
                int nb = k == fn - 1 ? ne : Math.Clamp(Map(bounds[k]), prev, ne);
                d[f0s + 8 * (fa + k) + 6] = (byte)Math.Min(255, delay.Skip(prev).Take(nb - prev).Sum());   // ponytail: >255 clamps
                prev = nb;
            }
        }
        return true;
    }

    /// <summary>frames each glyph of message m waits (as written): the original delays when it keeps its Japanese;
    /// for newLen English cells, the message's usual typing speed and its last glyph's original delay</summary>
    public static List<int> GlyphDelays(Acd p, int m, int? newLen)
    {
        var od = p.Delays[m];
        if (newLen is not int n) return od;
        int speed = od.Count > 1 ? od.SkipLast(1).GroupBy(x => x).OrderByDescending(g => g.Count()).First().Key : 2;
        int last = od.Count > 0 ? od[^1] : 0;
        return Enumerable.Range(0, n).Select(j => j < n - 1 ? speed : last).ToList();
    }

    /// <summary>Ctrl of trailing text that has no closing 0xFFFF</summary>
    public const int NoEnd = -1;

    /// <summary>every dialogue file's rows in order (line key, continues on the next row of the page);
    /// files with exactly the same rows as an earlier one are left out</summary>
    public static List<Scene> Scenes(Disc disc)
    {
        var outp = new List<Scene>();
        var seen = new HashSet<string>();
        foreach (var (path, data) in Flb.Walk(Archive(disc)))
        {
            var p = ParseAcd(data);
            if (p == null) continue;
            var ids = p.Bitmaps.Select(Gid).ToList();
            var rows = Enumerable.Range(0, p.Msgs.Count).Where(m => p.Msgs[m].Seq.Count > 0)
                .Select(m => new SceneRow(LineKey(p.Msgs[m].Seq.Select(k => ids[k])), p.SamePage(m))).ToList();
            if (rows.Count > 0 && seen.Add(string.Join(",", rows.Select(r => r.Key + r.More)))) outp.Add(new Scene(path, rows));
        }
        return outp;
    }

    /// <summary>row b starts a new sentence (a capital letter, or the row before ends one): rows are not merged
    /// across that point, since the rows of a page can be different speakers</summary>
    static bool NewSentence(string prev, string next) =>
        next.TrimStart('.', ' ', '\'', '"').FirstOrDefault() is char c && char.IsUpper(c) || prev.TrimEnd().EndsWith('.') || prev.TrimEnd().EndsWith('!') || prev.TrimEnd().EndsWith('?');

    /// <summary>
    /// The game wraps a row that is too wide by itself, anywhere (fine for Japanese, mid-word for English), and the
    /// overflow pushes the page's other rows down onto an extra page. The widest Japanese row of the file is a
    /// width every row of its text window is known to hold. A page (one track) with an English row
    /// wider than that is re-flowed across its rows at word boundaries; when it cannot fit, it is kept as written
    /// and its first message goes to tooWide.
    /// </summary>
    /// <summary>the text box is 288px wide and centres each row by the sum of (glyph width + 1); it never wraps,
    /// so a wider row spills out of both sides (overlay 0x8005B3F8: box init 0x80061394, centring 0x800604D8).
    /// A little margin is kept.</summary>
    public const int BoxWidth = 288, RowMax = 280;
    public static int RowLimit(Acd p) => RowMax;

    /// <summary>marks a row drawn as two small lines: StackMark + line 1 + StackSep + line 2</summary>
    public const char StackMark = '\u0001', StackSep = '\u0002', StackCellSep = '\u0003';

    /// <summary>rows still wider than the limit become two balanced lines of the small font in the same row (when
    /// that fits); the rest go to tooWide</summary>
    public static List<string?> Stack(Acd p, List<string?> texts, Func<string, int> width, Font small, List<int>? tooWide = null)
    {
        int limit = RowLimit(p);
        var outp = texts.ToList();
        for (int m = 0; m < texts.Count; m++)
        {
            if (texts[m] is not { Length: > 0 } t || width(t) <= limit) continue;
            if (!t.All(small.Has)) { tooWide?.Add(m); continue; }   // the small font lacks a character
            var lines = Wrap(t.Split(' ', StringSplitOptions.RemoveEmptyEntries), 2, limit, s => small.LineWidth(s) + 2);
            // drawn width as the game measures rows: each 16px cell + 1
            if (lines != null && small.StackCells(lines[0], lines[1]) * 17 <= limit)
                outp[m] = $"{StackMark}{lines[0]}{StackSep}{lines[1]}";
            else tooWide?.Add(m);
        }
        return outp;
    }

    /// <summary>a stacked row -> one token per 16px cell: the row's text + cell number (decoded by StackCell)</summary>
    public static List<string> StackTokens(string t, Font small)
    {
        var parts = t[1..].Split(StackSep);
        return Enumerable.Range(0, small.StackCells(parts[0], parts[1])).Select(k => $"{t}{StackCellSep}{k}").ToList();
    }

    /// <summary>the cell a StackTokens token stands for</summary>
    public static (byte[] Bitmap, int X, int W) StackCell(string tok, Font small)
    {
        var parts = tok[1..].Split(StackSep, StackCellSep);
        return small.StackCell(parts[0], parts[1], int.Parse(parts[2]));
    }

    public static List<string?> Reflow(Acd p, List<string?> texts, Func<string, int> width, List<int>? tooWide = null)
    {
        int limit = RowLimit(p);
        var outp = texts.ToList();
        for (int a = 0, b; a < p.Msgs.Count; a = b + 1)
        {
            for (b = a; p.SamePage(b); b++) { }
            var rows = Enumerable.Range(a, b - a + 1).Where(m => p.Msgs[m].Seq.Count > 0).ToList();
            if (!rows.Any(m => !string.IsNullOrEmpty(texts[m]) && width(texts[m]!) > limit)) continue;
            if (rows.Zip(rows.Skip(1)).Any(z => texts[z.First] is { } x && texts[z.Second] is { } y && NewSentence(x, y))) continue;
            var fit = rows.All(m => !string.IsNullOrEmpty(texts[m]))
                ? Wrap(string.Join(' ', rows.Select(m => texts[m]!)).Split(' ', StringSplitOptions.RemoveEmptyEntries), rows.Count, limit, width)
                : null;   // a page only partly translated: leave it alone
            if (fit == null) { tooWide?.Add(a); continue; }
            for (int i = 0; i < rows.Count; i++) outp[rows[i]] = fit[i];
        }
        return outp;
    }

    /// <summary>words into exactly `rows` rows no wider than limit, as even as possible (the narrowest width at which
    /// filling rows greedily still fits); null if they don't fit. A row left without words gets a space, because an
    /// empty message would keep its Japanese.</summary>
    public static List<string>? Wrap(string[] words, int rows, int limit, Func<string, int> width)
    {
        List<string>? Greedy(int lim)
        {
            var outp = new List<string>();
            var cur = "";
            foreach (var w in words)
            {
                var next = cur.Length == 0 ? w : cur + " " + w;
                if (width(next) <= lim) { cur = next; continue; }
                if (cur.Length == 0) return null;   // one word wider than the row
                outp.Add(cur); cur = w;
                if (width(w) > lim) return null;
            }
            outp.Add(cur);
            return outp.Count <= rows ? outp : null;
        }
        if (Greedy(limit) == null) return null;
        int lo = 1, hi = limit;   // narrowest width that still fits
        while (lo < hi) { int mid = (lo + hi) / 2; if (Greedy(mid) != null) hi = mid; else lo = mid + 1; }
        var best = Greedy(lo)!;
        while (best.Count < rows) best.Add(" ");
        return best;
    }

    /// <summary>-> ({iso path: new file bytes}, report). Only lines with English are replaced.
    /// labels: diagnostic build, every message shows its own 'file:message' id instead of text.</summary>
    public static (Dictionary<string, byte[]> Repl, InsertReport Report) Insert(Disc disc, Script script, Font font, bool labels = false)
    {
        var cells = new Dictionary<string, (byte[], int, int)>();
        var small = Font.Load("en_small");
        (byte[], int, int) Cell(string tok)
        {
            lock (cells)
                return cells.TryGetValue(tok, out var c) ? c
                     : cells[tok] = tok[0] == StackMark ? StackCell(tok, small) : Font.ToCell(font.Join(tok));
        }
        var shared = Tfile.SharedEn(script);
        var en = new Dictionary<string, string>();
        foreach (var l in script.Lines)
        {
            var t = Tfile.EffectiveEn(l, shared).Trim();
            if (t.Length == 0) continue;
            en[l.Key] = l.IsImage ? t   // pictures: Images.Draw handles line breaks / missing letters
                : new string(t.Replace('\n', ' ').Select(c => font.Has(c) ? c : '?').ToArray());
        }
        // drawn width as the game measures it: each packed cell's width + 1
        int Width(string t) => font.Pack(t).Sum(tok => Cell(tok).Item3 + 1);
        var tooWide = new List<string>();
        var choices = script.Lines.Where(l => !l.IsImage).Select(l => (l.Key, Lay: ChoiceLayout(l))).Where(x => x.Lay != null)
            .ToDictionary(x => x.Key, x => x.Lay);
        var archive = Archive(disc);
        var repl = new Dictionary<string, byte[]>();
        var problems = new Dictionary<string, string>();
        var pics = new Dictionary<string, byte[]>();
        foreach (var (path, data) in Flb.Walk(archive))
        {
            if (ParseEas(data) != null) pics[path] = data;
            var tag = Flb.Short(path);
            Func<int, string, string?> lookup = labels ? (m, k) => $"{tag}:{m}" : (m, k) => en.GetValueOrDefault(k);
            var wide = new List<int>();
            var (nw, err) = InsertAcd(data, lookup, Cell, font.Pack, labels ? null : Width, wide, small, labels ? null : choices.GetValueOrDefault);
            tooWide.AddRange(wide.Select(m => $"{tag}:{m}"));
            if (err != null) problems[path] = err;
            if (nw != null) repl[path] = nw;
        }
        var (newPics, tight) = Images.Apply(Games.Of(script), pics, script, l => en.GetValueOrDefault(l.Key), labels);
        foreach (var (k, v) in newPics) repl[k] = v;
        foreach (var k in tight) problems[k] = "English clipped: does not fit the picture";
        var report = new InsertReport(repl.Count, problems, en.Count, tooWide);
        return (repl.Count > 0 ? new() { [FlbPath] = Flb.Rebuild(archive, repl) } : new(), report);
    }

    /// <summary>16x16 palette values of a glyph (15 = background, low = dark outline, high = light fill)</summary>
    public static int[,] GlyphPixels(GlyphEntry e)
    {
        var b = Convert.FromHexString(e.Bitmap);
        var px = new int[16, 16];
        for (int i = 0; i < 128; i++) { px[i * 2 / 16, i * 2 % 16] = b[i] & 15; px[(i * 2 + 1) / 16, (i * 2 + 1) % 16] = b[i] >> 4; }
        return px;
    }

    // --- EAS pictures -------------------------------------------------------------------------------------
    public sealed record EasImage(int W, int H, int Bpp, int Off, int? Pal);

    public static List<EasImage>? ParseEas(byte[] d)
    {
        if (d.Length < 0x24 || !(d[0] == 'E' && d[1] == 'A' && d[2] == 'S' && d[3] == 0x90)) return null;
        var t = Enumerable.Range(0, 7).Select(i => U32(d, 8 + i * 4)).ToArray();
        var pals = new List<int>();
        for (int o = t[1]; o < t[2]; o += 8) pals.Add(U32(d, o) + t[6]);
        var outp = new List<EasImage>();
        for (int o = t[0]; o < t[1]; o += 20)
        {
            int off = U32(d, o), pi = U16(d, o + 4), w = U16(d, o + 12), h = U16(d, o + 14), bpp = U16(d, o + 16);
            outp.Add(new EasImage(w, h, bpp, off + t[6], pi < pals.Count ? pals[pi] : null));
        }
        return outp;
    }

    /// <summary>{flb path: bytes} of every file holding pictures</summary>
    public static Dictionary<string, byte[]> ImageFiles(Disc disc) =>
        Flb.Walk(Archive(disc)).Where(x => ParseEas(x.Data) != null).ToDictionary(x => x.Path, x => x.Data);

    static int Stride(EasImage im) => (im.W * im.Bpp + 15) / 16 * 2;   // rows are padded to 16 bits (VRAM words)

    /// <summary>{key: entry}; identical pictures (pixels + palette) share a key</summary>
    public static Dictionary<string, ImageEntry> ImageCatalog(Dictionary<string, byte[]> files)
    {
        var outp = new Dictionary<string, ImageEntry>();
        foreach (var (p, d) in files)
        {
            var ims = ParseEas(d)!;
            for (int i = 0; i < ims.Count; i++)
            {
                var im = ims[i];
                if (im.W * im.H == 0 || im.Pal is not int pal) continue;
                int plen = im.Bpp == 4 ? 32 : 512;
                var key = Md5Hex([.. d.AsSpan(im.Off, Stride(im) * im.H), .. d.AsSpan(pal, plen)])[..16];
                if (!outp.TryGetValue(key, out var e)) outp[key] = e = new ImageEntry { W = im.W, H = im.H };
                e.Refs.Add($"{p}:{i}");
            }
        }
        return outp;
    }

    /// <summary>-> (palette indices [h, w], palette) of picture i in file d</summary>
    public static (byte[,] Idx, ushort[] Pal) ImageData(byte[] d, int i)
    {
        var im = ParseEas(d)![i];
        int st = Stride(im);
        var idx = new byte[im.H, im.W];
        for (int y = 0; y < im.H; y++)
            for (int x = 0; x < im.W; x++)
            {
                if (im.Bpp == 4) { byte b = d[im.Off + y * st + x / 2]; idx[y, x] = (byte)((x & 1) == 0 ? b & 15 : b >> 4); }
                else idx[y, x] = d[im.Off + y * st + x];
            }
        var pal = new ushort[im.Bpp == 4 ? 16 : 256];
        for (int k = 0; k < pal.Length; k++) pal[k] = (ushort)U16(d, im.Pal!.Value + k * 2);
        return (idx, pal);
    }

    /// <summary>write palette indices (same size as the original) back into picture i -> new file bytes.
    /// Row padding keeps its original bytes.</summary>
    public static byte[] PutImage(byte[] d, int i, byte[,] idx)
    {
        var im = ParseEas(d)![i];
        int st = Stride(im);
        var outp = (byte[])d.Clone();
        for (int y = 0; y < im.H; y++)
            for (int x = 0; x < im.W; x++)
            {
                int o = im.Off + y * st;
                if (im.Bpp == 4)
                {
                    ref byte b = ref outp[o + x / 2];
                    b = (x & 1) == 0 ? (byte)(b & 0xF0 | idx[y, x]) : (byte)(b & 0x0F | idx[y, x] << 4);
                }
                else outp[o + x] = idx[y, x];
            }
        return outp;
    }
}
