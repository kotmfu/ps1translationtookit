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
///               (0xFFFF, ctrl) closes the message (ctrl preserved)
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
    public const int ExtractorVersion = 3;
    /// <summary>largest font page the game itself ships; growing past it is untested</summary>
    public const int MaxGlyphs = 465;
    const string FlbPath = "/DATA/FILELINK.FLB";

    static int U32(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadInt32LittleEndian(d[o..]);
    static int U16(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadUInt16LittleEndian(d[o..]);

    public sealed record Acd(List<byte[]> Bitmaps, List<(int X, int W)> Metrics, List<(List<int> Seq, int Ctrl)> Msgs);

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
        int count = (s[5] - s[4]) / 2;
        for (int k = 0; k + 1 < count; k += 2)
        {
            int a = U16(d, s[4] + k * 2), b = U16(d, s[4] + k * 2 + 2);
            if (a == 0xFFFF) { msgs.Add((cur, b)); cur = new List<int>(); }
            else if (a < n) cur.Add(a);
            else throw new InvalidDataException($"glyph {a} out of range {n}");
        }
        return new Acd(bitmaps, metrics, msgs);
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
        Func<string, (byte[] Bitmap, int X, int W)> cell, Func<string, List<string>> pack)
    {
        var p = ParseAcd(d);
        if (p == null) return (null, null);
        int n = p.Bitmaps.Count;
        var ids = p.Bitmaps.Select(Gid).ToList();
        var texts = p.Msgs.Select((mm, m) => mm.Seq.Count > 0 ? lookup(m, LineKey(mm.Seq.Select(k => ids[k]))) : null).ToList();
        if (!texts.Any(t => !string.IsNullOrEmpty(t))) return (null, null);
        var keep = new HashSet<int>();
        for (int m = 0; m < p.Msgs.Count; m++) if (string.IsNullOrEmpty(texts[m])) keep.UnionWith(p.Msgs[m].Seq);
        var free = Enumerable.Range(0, n).Where(i => !keep.Contains(i)).ToList();
        bool Fits(List<List<string>?> toks) =>
            n + Math.Max(0, toks.Where(t => t != null).SelectMany(t => t!).Distinct().Count() - free.Count) <= MaxGlyphs;
        // fewest sprites first; fall back to one letter per cell, then ALL CAPS (fewer distinct letters)
        var tries = new Func<string, List<string>>[] { pack, t => t.Select(c => c.ToString()).ToList(), t => t.ToUpperInvariant().Select(c => c.ToString()).ToList() };
        List<List<string>?>? toks = null;
        foreach (var f in tries)
        {
            var cand = texts.Select(t => string.IsNullOrEmpty(t) ? null : f(t)).ToList();
            if (Fits(cand)) { toks = cand; break; }
        }
        if (toks == null) return (null, $"font page would exceed {MaxGlyphs} glyphs");
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
            for (int j = 0; j < idx.Count; j++)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(pair, (ushort)idx[j]);
                BinaryPrimitives.WriteUInt16LittleEndian(pair[2..], (ushort)(j < idx.Count - 1 ? 0x200 : 0));
                stream.Write(pair);
            }
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
        for (int i = 0; i < 13; i++) BinaryPrimitives.WriteInt32LittleEndian(outp.AsSpan(8 + i * 4), s[i]);
        return (outp, null);
    }

    public sealed record InsertReport(int FilesChanged, Dictionary<string, string> FilesSkipped, int LinesWithEnglish);

    /// <summary>-> ({iso path: new file bytes}, report). Only lines with English are replaced.
    /// labels: diagnostic build, every message shows its own 'file:message' id instead of text.</summary>
    public static (Dictionary<string, byte[]> Repl, InsertReport Report) Insert(Disc disc, Script script, Font font, bool labels = false)
    {
        var cells = new Dictionary<string, (byte[], int, int)>();
        (byte[], int, int) Cell(string tok) { lock (cells) return cells.TryGetValue(tok, out var c) ? c : cells[tok] = Font.ToCell(font.Join(tok)); }
        var shared = Tfile.SharedEn(script);
        var en = new Dictionary<string, string>();
        foreach (var l in script.Lines)
        {
            var t = Tfile.EffectiveEn(l, shared).Trim();
            if (t.Length == 0) continue;
            en[l.Key] = l.IsImage ? t   // pictures: Images.Draw handles line breaks / missing letters
                : new string(t.Replace('\n', ' ').Select(c => font.Has(c) ? c : '?').ToArray());
        }
        var archive = Archive(disc);
        var repl = new Dictionary<string, byte[]>();
        var problems = new Dictionary<string, string>();
        var pics = new Dictionary<string, byte[]>();
        foreach (var (path, data) in Flb.Walk(archive))
        {
            if (ParseEas(data) != null) pics[path] = data;
            var tag = Flb.Short(path);
            Func<int, string, string?> lookup = labels ? (m, k) => $"{tag}:{m}" : (m, k) => en.GetValueOrDefault(k);
            var (nw, err) = InsertAcd(data, lookup, Cell, font.Pack);
            if (err != null) problems[path] = err;
            if (nw != null) repl[path] = nw;
        }
        var (newPics, tight) = Images.Apply(pics, script, l => en.GetValueOrDefault(l.Key), labels);
        foreach (var (k, v) in newPics) repl[k] = v;
        foreach (var k in tight) problems[k] = "English clipped: does not fit the picture";
        var report = new InsertReport(repl.Count, problems, en.Count);
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
