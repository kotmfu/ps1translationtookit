using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Ps1tl;

/// <summary>
/// Koukidou Gensou: Gunparade March (SCPS-10136, Alfa System / SCE 2000).
///
/// Files are found by name through the ISO directory (DsSearchFile); nothing is loaded by LBA, and the last file,
/// GUNPARAD.DAT, is 27MB of zero padding, so EVDATA.BIN may grow (Build moves the padding to the end).
///
/// EVDATA.BIN: archive, see Evdata. Its EVxxxx.EVD files (1183) are the event scripts with the story text:
///   header  u16 0, u16 header size, then (u16 label, u16 offset) pairs ending with (0xFFFF, file size).
///           Every goto/gosub/branch goes through a label (EXE 0x80061c7c), so text may change length: only these
///           offsets move.
///   body    1B op + args, u16 each (counts per opcode in the EXE at 0x800a96d0, names at 0x800a92d0: 0F talk(char,
///           face), 06 pause, 05 winclose, 10 voice, C0 naplay, 17 select / 1D select8 / 91 selectex, 30 mojiopen,
///           3D title ...), or a text row: Shift-JIS ending in 0D, then NUL, zero padded to an even length.
///   Rows are drawn into 3 row slots of 64 bytes (EXE 0x80061bc4, assert "EvLine&lt;3"); the slot counter resets
///   after every opcode, so a page is a run of at most 3 rows. Rows after a select are its options, one each; rows
///   after mojiopen/thrust/wakuopen/title are read by that opcode (caption boxes sized to their rows).
///   "$0".."$3" in a row becomes a character name (EXE 0x800619b0) before drawing.
/// Text drawing (EXE 0x80025898): control bytes 01/02 = hiragana/katakana for half-width kana, 04 = half-width mode
///   (8x12 font), 05 = full-width mode (12x12, the default at the start of every row), 0D = new line. 0x60 is drawn
///   as a space and 0x7A as '…' in every mode. A row is 240px: 20 full-width or 30 half-width characters.
/// FONT/FONTDATA.BIN: u32 3, then section offsets: 12x12 kanji blocks, a palette, the 8x12 half-width font (1bpp,
///   12 bytes a glyph). Half-width code c (0x20-0x7F) draws glyph c-0x20; 0x61-0x7F are half-width hiragana with
///   dakuten in the default hiragana state, which the English font replaces with a-y (z goes to 0x7B).
/// Pictures (menu buttons, status labels, title screens): GpmPics. English is drawn into them like Yuuyami's (Images).
/// Not handled yet: menus and system messages (plain Shift-JIS in the EXE and MOD/*.BIN overlays), speaker names,
///   the talk system's phrases (MTALK, DATA/KAIWA.BIN).
/// </summary>
public sealed class Gunparade : IGame
{
    public string[] Serials => ["SCPS_101.36"];
    public string Name => "Koukidou Gensou: Gunparade March";
    public int ExtractorVersion => 5;   // 2: pictures; 3-4: EXE/overlay strings (names, prologue, menus); 5: menu pictures read by hand
    public const int RowCols = 30, PageRows = 3, CharPx = 8, NameCols = 8, EvdMax = 0x7000;
    public int Budget => PageRows * RowCols * CharPx;
    public string BoxRule => "Each numbered row is one text-box page (\\n marks the Japanese line breaks). Write the English as one " +
        "plain line: the game wraps it at 30 letters a line, 3 lines a page (about 80 letters; longer text gets an extra page). " +
        "Rows that are menu choices must stay under 30 letters. Keep $0 $1 $2 $3 exactly: they become character names. " +
        "Rows from the program (names, menus, messages) are drawn in the space the Japanese had: keep them about as short " +
        "(a Japanese character is as wide as 1.5 English letters), and keep %s %d %4d %-d and $ codes exactly, in order.";

    const string ArchivePath = "/EVDATA.BIN", FontPath = "/FONT/FONTDATA.BIN", ExePath = "/SCPS_101.36";
    const int ArgTable = (int)(0x800a96d0 - 0x80010010) + 0x800;   // EXE file offset of the opcode argument counts
    const byte Esc = 0x1B, Pause = 0x06, Talk = 0x0F;
    static readonly byte[] Selects = [0x17, 0x1D, 0x91];
    static readonly byte[] Boxes = [0x28, 0x30, 0x33, 0x3D, 0x58, 0x5B, 0xA3];   // thrust, mojiopen, wakuopen, title, *fade, winchangese

    internal static readonly Encoding Sjis = GetSjis();
    static Encoding GetSjis() { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); return Encoding.GetEncoding(932); }
    static int U16(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadUInt16LittleEndian(d[o..]);

    /// <summary>a token: an opcode with its args, or a text row (Row = its text bytes, without 0D/NUL/padding)</summary>
    public sealed record Tok(int At, byte[] Raw, byte[]? Row);
    /// <summary>a message: rows First..First+Count-1 (a page, or one select option), Prev = the opcode before them</summary>
    public sealed record Msg(int First, int Count, byte Prev, bool Choice);
    public sealed record Evd(int HeaderSize, List<(int Label, int Offset)> Labels, List<Tok> Toks, List<Msg> Msgs);

    static byte[] Read(Disc disc, string path) { var (lba, size) = disc.Files()[path]; return disc.Read(lba, size); }
    static sbyte[] ArgCounts(Disc disc) => Read(disc, ExePath).AsSpan(ArgTable, 256).ToArray().Select(b => (sbyte)b).ToArray();

    public static Evd ParseEvd(byte[] d, sbyte[] args)
    {
        int h = U16(d, 2);
        var labels = new List<(int, int)>();
        for (int o = 4; o + 4 <= h; o += 4) labels.Add((U16(d, o), U16(d, o + 2)));
        var toks = new List<Tok>();
        for (int p = h; p < d.Length;)
        {
            if (d[p] == Esc)
            {
                int n = 2 + 2 * args[d[p + 1]];
                toks.Add(new Tok(p, d[p..(p + n)], null));
                p += n;
                continue;
            }
            int e = Array.IndexOf(d, (byte)0, p);
            if (e < 0) throw new InvalidDataException($"text row at {p:x} has no end");
            int q = e + 1;
            while (q < d.Length && d[q] == 0) q++;
            toks.Add(new Tok(p, d[p..q], d[p..(e > p && d[e - 1] == 0x0D ? e - 1 : e)]));
            p = q;
        }
        var msgs = new List<Msg>();
        for (int i = 0, j; i < toks.Count; i = j)
        {
            for (j = i; j < toks.Count && toks[j].Row != null; j++) { }
            if (j == i) { j++; continue; }
            byte prev = i > 0 ? toks[i - 1].Raw[1] : (byte)0;
            if (Selects.Contains(prev)) for (int k = i; k < j; k++) msgs.Add(new Msg(k, 1, prev, true));
            else msgs.Add(new Msg(i, j - i, prev, false));
        }
        return new Evd(h, labels, toks, msgs);
    }

    public static string Ja(Evd e, Msg m) => string.Join("\n", e.Toks.Skip(m.First).Take(m.Count).Select(t => Sjis.GetString(t.Row!)));
    static string Key(string ja) => Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(ja)))[..16];
    static string Ref(string name) => Path.GetFileNameWithoutExtension(name);   // EV0001

    static IEnumerable<(string Name, Evd Evd, byte[] Data)> Events(Disc disc, byte[] archive)
    {
        var args = ArgCounts(disc);
        return Evdata.Walk(archive).Where(f => f.Name.EndsWith(".EVD")).Select(f => (f.Name, ParseEvd(f.Data, args), f.Data));
    }

    public Script Extract(Disc disc)
    {
        var lines = new Dictionary<string, Line>();
        foreach (var (name, e, _) in Events(disc, Read(disc, ArchivePath)))
            for (int m = 0; m < e.Msgs.Count; m++)
            {
                var ja = Ja(e, e.Msgs[m]);
                if (ja.Trim().Length == 0) continue;
                var key = Key(ja);
                if (!lines.TryGetValue(key, out var l)) lines[key] = l = new Line { Key = key, Ja = ja, JaSource = "rom" };
                l.Refs.Add($"{Ref(name)}:{m}");
            }
        foreach (var (path, ja, r) in Strings(disc))
        {
            var key = Key(ja);
            if (!lines.TryGetValue(key, out var l)) lines[key] = l = new Line { Key = key, Ja = ja, JaSource = "rom" };
            l.Refs.Add(r);
        }
        var images = ImageCatalog(ImageFiles(disc));
        foreach (var (key, img, ja, en) in Seeds(images))
            lines.TryAdd(key, new Line { Key = key, Image = img, Refs = images[img].Refs, Ja = ja, En = en, JaSource = "seed" });
        return new Script { Game = Name, Lines = lines.Values.ToList(), Images = images };
    }

    /// <summary>menu, title and status pictures read by hand (seeds/gpm_pictures.json: file, entry, Japanese, English)
    /// -> picture lines, so they need no 'find menu text' run</summary>
    static IEnumerable<(string Key, string Image, string Ja, string En)> Seeds(Dictionary<string, ImageEntry> images)
    {
        using var st = typeof(Gunparade).Assembly.GetManifestResourceStream("seeds.gpm_pictures.json")!;
        foreach (var e in System.Text.Json.JsonDocument.Parse(st).RootElement.EnumerateArray())
        {
            var r = $"{e[0].GetString()}:{e[1].GetInt32()}";
            if (images.FirstOrDefault(kv => kv.Value.Refs.Contains(r)).Key is string img)
                yield return (Images.Prefix + img, img, e[2].GetString()!, e[3].GetString()!);
        }
    }

    /// <summary>(file, editor text, ref) of every program string (GpmStrings)</summary>
    static IEnumerable<(string Path, string Ja, string Ref)> Strings(Disc disc)
    {
        foreach (var (path, b) in GpmStrings.Files(disc))
            foreach (var s in GpmStrings.Strings(Read(disc, path), b))
                if (GpmStrings.Display(s.Ja).Trim().Length > 0) yield return (path, GpmStrings.Display(s.Ja), GpmStrings.Ref(path, s.Addr));
    }

    public List<Scene> Scenes(Disc disc)
    {
        var outp = new List<Scene>();
        var seen = new HashSet<string>();
        foreach (var (name, e, _) in Events(disc, Read(disc, ArchivePath)))
        {
            var rows = e.Msgs.Select(m => Ja(e, m)).Where(ja => ja.Trim().Length > 0).Select(ja => new SceneRow(Key(ja), false)).ToList();
            if (rows.Count > 0 && seen.Add(string.Join(",", rows.Select(r => r.Key)))) outp.Add(new Scene(Ref(name), rows));
        }
        foreach (var g in Strings(disc).GroupBy(x => x.Path))   // program strings: one group per file
            outp.Add(new Scene(Path.GetFileNameWithoutExtension(g.Key), g.Select(x => new SceneRow(Key(x.Ja), false)).DistinctBy(r => r.Key).ToList()));
        return outp;
    }

    /// <summary>{iso path: bytes} of every picture file (big archives and movies skipped)</summary>
    public Dictionary<string, byte[]> ImageFiles(Disc disc) =>
        disc.Files().Where(f => f.Value.Size < 4 << 20).Select(f => (f.Key, Data: disc.Read(f.Value.Lba, f.Value.Size)))
            .Where(f => GpmPics.Parse(f.Data) != null).ToDictionary(f => f.Key, f => f.Data);

    public (byte[,] Idx, ushort[] Pal) ImageData(byte[] file, int i) => GpmPics.Data(file, i)!.Value;
    public byte[] PutImage(byte[] file, int i, byte[,] idx) => GpmPics.Put(file, i, idx);
    /// <summary>program strings: 'FINAMISC@800f53dc' -> 'FIN53dc', short enough to show in game in a diagnostic build</summary>
    public string Label(string reference) =>
        reference.IndexOf('@') is int at and > 0 ? $"{reference[..Math.Min(3, at)]}{reference[^4..]}" : reference;
    public IReadOnlyList<LineFilter> Filters => [new("pictures", "Menus & titles (pictures)", l => l.IsImage)];

    /// <summary>{key: entry} of every 4/8bpp picture; identical ones (pixels + palette) share a key</summary>
    static Dictionary<string, ImageEntry> ImageCatalog(Dictionary<string, byte[]> files)
    {
        var outp = new Dictionary<string, ImageEntry>();
        foreach (var (p, d) in files.OrderBy(f => f.Key, StringComparer.Ordinal))
            for (int i = 0; i < GpmPics.Parse(d)!.Count; i++)
            {
                if (GpmPics.Data(d, i) is not var (idx, pal)) continue;
                var bytes = idx.Cast<byte>().Concat(pal.SelectMany(BitConverter.GetBytes)).ToArray();
                var key = Convert.ToHexStringLower(MD5.HashData(bytes))[..16];
                if (!outp.TryGetValue(key, out var e)) outp[key] = e = new ImageEntry { W = idx.GetLength(1), H = idx.GetLength(0) };
                e.Refs.Add($"{p}:{i}");
            }
        return outp;
    }

    public (Dictionary<string, byte[]> Repl, InsertReport Report) Insert(Disc disc, Script script, Font font, bool labels)
    {
        var shared = Tfile.SharedEn(script);
        var en = new Dictionary<string, string>();
        foreach (var l in script.Lines)
            if (Tfile.EffectiveEn(l, shared).Trim() is { Length: > 0 } t) en[l.Key] = t;
        var archive = Read(disc, ArchivePath);
        var repl = new Dictionary<string, byte[]>();
        var problems = new Dictionary<string, string>();
        var tooWide = new List<string>();
        foreach (var (name, e, data) in Events(disc, archive))
        {
            string? Text(int m)
            {
                var ja = Ja(e, e.Msgs[m]);
                return ja.Trim().Length == 0 ? null : labels ? $"{Ref(name)}:{m}" : en.GetValueOrDefault(Key(ja));
            }
            var (nw, wide, err) = InsertEvd(data, e, Text);
            tooWide.AddRange(wide.Select(m => $"{Ref(name)}:{m}"));
            if (err != null) problems[name] = err;
            else if (nw != null) repl[name] = nw;
        }
        var outp = new Dictionary<string, byte[]>();
        if (repl.Count > 0) outp[ArchivePath] = Evdata.Rebuild(archive, repl);
        foreach (var (path, b) in GpmStrings.Files(disc))
        {
            var (nw, noRoom) = GpmStrings.Insert(Read(disc, path), b, x =>
                GpmStrings.Display(x.Ja) is var t && t.Trim().Length == 0 ? null
                : labels ? Label(GpmStrings.Ref(path, x.Addr)) : en.GetValueOrDefault(Key(t)), GpmStrings.Room(disc, path));
            if (nw != null) { outp[path] = nw; repl[path] = nw; }
            foreach (var a in noRoom) problems[GpmStrings.Ref(path, a)] = "no room in the file for this English: kept Japanese";
        }
        if (outp.Count > 0) outp[FontPath] = PatchFont(Read(disc, FontPath));
        var (pics, tight) = Images.Apply(this, ImageFiles(disc), script, l => en.GetValueOrDefault(l.Key), labels);
        foreach (var (k, v) in pics) outp[k] = v;
        foreach (var k in tight) problems[k] = "English clipped: does not fit the picture";
        return (outp, new InsertReport(repl.Count + pics.Count, problems, en.Count, tooWide));
    }

    /// <summary>
    /// rewrite one event script; text(m) -> English for message m, or null to keep the Japanese. English is wrapped
    /// into 30-column rows; a window page longer than 3 rows continues on a new page (pause, and the talk opcode
    /// again when the page began with one). -> (new bytes or null if unchanged, messages that do not fit, problem)
    /// </summary>
    public static (byte[]? Data, List<int> TooWide, string? Problem) InsertEvd(byte[] d, Evd e, Func<int, string?> text)
    {
        var news = new Dictionary<int, List<byte[]>>();   // first row of a message -> what replaces its rows
        var gone = new HashSet<int>();
        var wide = new List<int>();
        for (int m = 0; m < e.Msgs.Count; m++)
        {
            var msg = e.Msgs[m];
            if (text(m) is not { Length: > 0 } t) continue;
            var rows = Wrap(t);
            bool window = !msg.Choice && !Boxes.Contains(msg.Prev);
            if (msg.Choice ? rows.Count > 1 : !window && rows.Count > Math.Max(msg.Count, PageRows)) wide.Add(m);
            // an option is one row of the 240px choice box; a caption box holds at most 3 rows (assert EvLine<3): cut
            if (msg.Choice) rows = rows.Take(1).ToList();
            else if (!window) rows = rows.Take(PageRows).ToList();
            var outRows = new List<byte[]>();
            for (int r = 0; r < rows.Count; r++)
            {
                if (window && r > 0 && r % PageRows == 0)
                {
                    outRows.Add([Esc, Pause]);
                    if (msg.Prev == Talk) outRows.Add(e.Toks[msg.First - 1].Raw);
                }
                outRows.Add(Encode(rows[r]));
            }
            news[msg.First] = outRows;
            for (int k = msg.First; k < msg.First + msg.Count; k++) gone.Add(k);
        }
        if (news.Count == 0) return (null, wide, null);
        var body = new MemoryStream();
        var moved = new Dictionary<int, int>();   // old offset of a token -> new
        int h = e.HeaderSize;
        for (int i = 0; i < e.Toks.Count; i++)
        {
            moved[e.Toks[i].At] = h + (int)body.Length;
            if (news.TryGetValue(i, out var rep)) foreach (var b in rep) body.Write(b);
            if (!gone.Contains(i)) body.Write(e.Toks[i].Raw);
        }
        int size = h + (int)body.Length;
        moved[d.Length] = size;
        if (size > EvdMax) return (null, wide, $"event script would pass {EvdMax / 1024}KB (the game's event buffer, EXE 0x80061634)");
        var outp = new byte[size];
        d.AsSpan(0, h).CopyTo(outp);
        body.ToArray().CopyTo(outp, h);
        for (int k = 0; k < e.Labels.Count; k++)
        {
            if (!moved.TryGetValue(e.Labels[k].Offset, out var no)) return (null, wide, $"label {e.Labels[k].Label:x} not at a token");
            BinaryPrimitives.WriteUInt16LittleEndian(outp.AsSpan(4 + 4 * k + 2), (ushort)no);
        }
        return (outp, wide, null);
    }

    /// <summary>columns a word takes: names ($0-$3, full-width) count NameCols, '…' (full-width) 2</summary>
    static int Cols(string w)
    {
        int n = 0;
        for (int i = 0; i < w.Length; i++)
        {
            if (w[i] == '$' && i + 1 < w.Length && w[i + 1] is >= '0' and <= '3') { n += NameCols; i++; }
            else n += w[i] == '…' ? 2 : 1;
        }
        return n;
    }

    /// <summary>English -> rows of at most RowCols columns, broken at spaces (a longer word is cut)</summary>
    public static List<string> Wrap(string text, int cols = RowCols)
    {
        var rows = new List<string>();
        var cur = "";
        foreach (var word in text.Replace('\n', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var w = word;
            while (Cols(w) > cols)
            {
                if (cur.Length > 0) { rows.Add(cur); cur = ""; }
                int k = Math.Min(cols, w.Length);   // names count 8 columns, so a short word can be too wide
                while (Cols(w[..k]) > cols || w[k - 1] == '$' && w[k] is >= '0' and <= '3') k--;   // never split $n
                rows.Add(w[..k]); w = w[k..];
            }
            var next = cur.Length == 0 ? w : cur + " " + w;
            if (Cols(next) <= cols) { cur = next; continue; }
            rows.Add(cur); cur = w;
        }
        if (cur.Length > 0 || rows.Count == 0) rows.Add(cur);
        return rows;
    }

    /// <summary>half-width code of an English character (lowercase sits on the glyphs FontData repaints)</summary>
    internal static byte Code(char c) => c switch
    {
        'z' => 0x7B,
        '…' => 0x7A,
        >= 'a' and <= 'y' => (byte)c,
        '$' => (byte)'S',   // a bare '$' would be read as a name
        '`' or '‘' or '’' => (byte)'\'',
        '“' or '”' => (byte)'"',
        '–' or '—' or '~' => (byte)'-',
        >= ' ' and <= '_' => (byte)c,
        _ => (byte)'?',
    };

    /// <summary>English row -> text row bytes: 04 (half-width), characters, a space if needed, 0D, NUL (even length).
    /// $0-$3 (names, full-width Shift-JIS) are wrapped in 05 .. 04, since half-width mode skips 2-byte characters.</summary>
    public static byte[] Encode(string row)
    {
        var b = new List<byte> { 0x04 };
        for (int i = 0; i < row.Length; i++)
        {
            if (row[i] == '$' && i + 1 < row.Length && row[i + 1] is >= '0' and <= '3') { b.AddRange([0x05, (byte)'$', (byte)row[++i], 0x04]); continue; }
            b.Add(Code(row[i]));
        }
        // the NUL must land on an odd offset, as it always does after Shift-JIS: the choice counter (EXE 0x80061b44)
        // only steps past a NUL at an odd address and loops forever on one at an even address. Pad with a space.
        if (b.Count % 2 == 1) b.Add((byte)' ');
        b.Add(0x0D); b.Add(0);
        return b.ToArray();
    }

    /// <summary>FONTDATA.BIN with the English half-width glyphs of fonts/gpm_ank.txt drawn in</summary>
    public static byte[] PatchFont(byte[] d)
    {
        var outp = (byte[])d.Clone();
        int ank = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(12));
        foreach (var (c, rows) in Font.Load("gpm_ank").Glyphs)
        {
            int code = c == 'z' ? 0x7B : c;
            if (code is < 0x20 or > 0x7F) continue;
            var g = outp.AsSpan(ank + (code - 0x20) * 12, 12);
            g.Clear();
            for (int y = 0; y < rows.Length && y + 2 < 12; y++)
                for (int x = 0; x < rows[y].Length && x + 1 < 8; x++)
                    if (rows[y][x] == '#') g[y + 2] |= (byte)(0x80 >> (x + 1));
        }
        return outp;
    }

    public (Func<string, byte[]> Ja, Func<string, byte[]> En)? Previews(Disc disc)
    {
        var font = Read(disc, FontPath);
        var ja = new GpmFont(font, Read(disc, ExePath));
        var en = new GpmFont(PatchFont(font), Read(disc, ExePath));
        return (ja.Render, t => en.Render(string.Join("\n", t.Split('\n').SelectMany(r => Wrap(r))
            .Select(r => new string(r.Select(c => c == '$' ? c : (char)Code(c)).ToArray())))));   // as Encode writes them ($0-$3 left as names)
    }
}

/// <summary>
/// The game's text font, for the editor's previews. Full-width (EXE 0x80021aa0): Shift-JIS with lead byte 0x81-0x98
/// directly; rarer kanji through two code lists in the EXE (0x80010851: 0x61 codes, drawn as 839F+i; 0x80010914:
/// 0x1F codes, as 8840+i). The lead byte picks a block (byte table at 0x80010838, FF = none) in FONTDATA's first
/// section (u32 offsets from the section start), trail - 0x40 a glyph of 18 bytes: 12 bytes of the left 8 pixels
/// of each row, then 6 bytes of the right 4 (high nibble an even row, low nibble the next). Half-width: 8x12,
/// 1bpp, 12 bytes a glyph, code - 0x20 (PatchFont).
/// </summary>
public sealed class GpmFont
{
    const int Sect = 16, RowH = 14;
    static int Exe(uint a) => (int)(a - 0x80010010) + 0x800;
    readonly byte[] font, blocks;
    readonly Dictionary<int, int> extra = new();

    public GpmFont(byte[] font, byte[] exe)
    {
        this.font = font;
        blocks = exe.AsSpan(Exe(0x80010838), 128).ToArray();
        for (int i = 0; i < 0x61; i++) extra.TryAdd(BinaryPrimitives.ReadUInt16BigEndian(exe.AsSpan(Exe(0x80010851) + 2 * i)), 0x839F + i);
        for (int i = 0; i < 0x1F; i++) extra.TryAdd(BinaryPrimitives.ReadUInt16BigEndian(exe.AsSpan(Exe(0x80010914) + 2 * i)), 0x8840 + i);
    }

    /// <summary>text ('\n' = new row) as the game draws it, white on black, scaled 2x</summary>
    public byte[] Render(string text)
    {
        var rows = text.Split('\n').Select(r => Gunparade.Sjis.GetBytes(r)).ToList();
        int w = Math.Max(16, rows.Max(r => r.Length * 8)) + 8;   // 1-byte glyphs 8px, 2-byte 12px (6 a byte)
        var im = new Raster(w, rows.Count * RowH + 4, Raster.Rgb(0, 0, 0));
        for (int r = 0; r < rows.Count; r++)
        {
            var b = rows[r];
            int x = 4, y = 2 + r * RowH;
            for (int i = 0; i < b.Length; i++)
            {
                if (b[i] is >= 0x81 and <= 0x9F or >= 0xE0 && i + 1 < b.Length) { Full(im, x, y, b[i] << 8 | b[++i]); x += 12; }
                else { Half(im, x, y, b[i]); x += 8; }
            }
        }
        return im.Scale(2).Png();
    }

    void Half(Raster im, int x, int y, int c)
    {
        int o = BinaryPrimitives.ReadInt32LittleEndian(font.AsSpan(12)) + (c - 0x20) * 12;
        if (c < 0x20 || o + 12 > font.Length) return;
        for (int gy = 0; gy < 12; gy++)
            for (int gx = 0; gx < 8; gx++)
                if ((font[o + gy] & 0x80 >> gx) != 0) im.Set(x + gx, y + gy, Raster.Rgb(255, 255, 255));
    }

    void Full(Raster im, int x, int y, int c)
    {
        if (c is < 0x8140 or >= 0x9940) c = extra.GetValueOrDefault(c, -1);
        int blk = c < 0 ? 0xFF : blocks[c >> 8 & 0x7F];
        if (blk == 0xFF || (c & 0xFF) < 0x40) { im.FillRect(x + 1, y + 1, 10, 10, Raster.Rgb(90, 90, 90)); return; }   // the game draws nothing useful either
        int o = Sect + BinaryPrimitives.ReadInt32LittleEndian(font.AsSpan(Sect + 4 * blk)) + ((c & 0xFF) - 0x40) * 18;
        for (int gy = 0; gy < 12; gy++)
            for (int gx = 0; gx < 12; gx++)
            {
                int bit = gx < 8 ? font[o + gy] >> (7 - gx) : font[o + 12 + gy / 2] >> (gy % 2 == 0 ? 15 - gx : 11 - gx);
                if ((bit & 1) != 0) im.Set(x + gx, y + gy, Raster.Rgb(255, 255, 255));
            }
    }
}

/// <summary>
/// EVDATA.BIN archive (Gunparade March). 32-byte records: char[20] name, u32 sector (2048-byte units), u32 size,
/// u32 date. Record 0 is "." with the record count in its sector field and the archive size in its size field; the
/// files follow in record order, sector aligned. "+" records are empty placeholders pointing at the next file.
/// The game ignores the records' sectors and uses a second index in the header area (SectorTable).
/// </summary>
public static class Evdata
{
    static int U32(byte[] d, int o) => BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(o));
    static string Name(byte[] a, int o) => Encoding.ASCII.GetString(a, o, 20).TrimEnd('\0');
    static int Sectors(int size) => (size + 2047) / 2048;

    public static List<(string Name, byte[] Data)> Walk(byte[] a)
    {
        int n = U32(a, 20);
        return Enumerable.Range(1, n).Select(i => 32 * i)
            .Select(o => (Name(a, o), a.AsSpan(U32(a, o + 20) * 2048, U32(a, o + 24)).ToArray())).ToList();
    }

    /// <summary>the game's own index: sector-aligned in the header area, u16 file count, then u16 start sector per
    /// record and the archive's end sector (0x1C000 on the Japanese disc)</summary>
    static int SectorTable(byte[] a)
    {
        int n = U32(a, 20), first = U32(a, 52);
        for (int o = (32 * (n + 1) + 2047) / 2048 * 2048; o < first * 2048; o += 2048)
            if (U16(a, o) == n && U16(a, o + 2) == first) return o;
        throw new InvalidDataException("EVDATA.BIN: sector table not found");
    }
    static int U16(byte[] d, int o) => BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(o));

    /// <summary>archive with files replaced {name: bytes}: files laid out again in record order, sector aligned, with
    /// both the records and the game's sector table (SectorTable) updated. Unchanged input gives the same bytes.
    /// The game finds files only through the sector table: moving files without it hangs the game at the title
    /// (all files shifted) or shows the old text (files moved to the end).</summary>
    public static byte[] Rebuild(byte[] a, IReadOnlyDictionary<string, byte[]> repl)
    {
        int n = U32(a, 20), table = SectorTable(a), first = U32(a, 52);
        var outp = new MemoryStream();
        outp.Write(a, 0, first * 2048);
        var hdr = a.AsSpan(0, first * 2048).ToArray();
        for (int i = 1; i <= n; i++)
        {
            int o = 32 * i, sec = (int)(outp.Length / 2048);
            var data = repl.TryGetValue(Name(a, o), out var r) ? r : a.AsSpan(U32(a, o + 20) * 2048, U32(a, o + 24)).ToArray();
            BinaryPrimitives.WriteInt32LittleEndian(hdr.AsSpan(o + 20), sec);
            BinaryPrimitives.WriteInt32LittleEndian(hdr.AsSpan(o + 24), data.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(hdr.AsSpan(table + 2 * i), (ushort)sec);
            outp.Write(data);
            outp.Write(new byte[Sectors(data.Length) * 2048 - data.Length]);
        }
        if (outp.Length / 2048 > 0xFFFF) throw new InvalidDataException("EVDATA.BIN would pass 128MB (u16 sector table)");
        BinaryPrimitives.WriteUInt16LittleEndian(hdr.AsSpan(table + 2 * (n + 1)), (ushort)(outp.Length / 2048));
        BinaryPrimitives.WriteInt32LittleEndian(hdr.AsSpan(24), (int)outp.Length);
        var b = outp.ToArray();
        hdr.CopyTo(b, 0);
        return b;
    }
}

/// <summary>
/// Picture files (Gunparade March): DATA/TITLE2.BIN, DATA/MENU.BIN, DATA/STWINDOW.BIN, OTHER/TITLEV.BIN and about
/// 100 more. u16 entry count, u16 bank (the game overwrites it with the bank it loads the file into), u32 entry
/// offsets (4-aligned). An entry is a palette (u16 colours, u16 0, the colours) or a picture: u16 width in VRAM
/// words, u16 height | depth &lt;&lt; 14 (0 = 4bpp, 1 = 8bpp, 2 = 16bpp), u32 kept as is, then Lz data.
/// Files do not say which palette a picture uses (the game picks it in code). When a file has as many palettes as
/// pictures they pair in order (every index then fits its palette); otherwise the nearest earlier palette of the
/// right size is a guess that looks right. EXE: 0x80019008 registers a file, 0x80018ef0 maps id (bank &lt;&lt; 8 |
/// entry) to an entry, 0x800224b8 unpacks a picture into VRAM.
/// </summary>
public static class GpmPics
{
    public sealed record Entry(int Off, int End, int W, int H, int Depth, int Colors);   // W, H in pixels; Colors > 0 = palette

    static int U16(byte[] d, int o) => BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(o));
    static int U32(byte[] d, int o) => BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(o));

    /// <summary>-> entries, or null if d is not a picture file</summary>
    public static List<Entry>? Parse(byte[] d)
    {
        if (d.Length < 8) return null;
        int n = U16(d, 0);
        if (n == 0 || 4 + 4 * n > d.Length || U32(d, 4) != 4 + 4 * n) return null;
        var outp = new List<Entry>();
        for (int i = 0; i < n; i++)
        {
            int o = U32(d, 4 + 4 * i), e = i + 1 < n ? U32(d, 8 + 4 * i) : d.Length;
            if (e < o || e > d.Length || e - o < 4) return null;
            int w = U16(d, o), h = U16(d, o + 2);
            if (h == 0) { if (4 + 2 * w != e - o) return null; outp.Add(new(o, e, 0, 0, 0, w)); continue; }
            int depth = h >> 14;
            outp.Add(new(o, e, depth switch { 0 => w * 4, 1 => w * 2, _ => w }, h & 0x3FFF, depth, 0));
        }
        return outp;
    }

    /// <summary>bytes of picture data (rows of 2 * width-in-words)</summary>
    static int RowBytes(Entry e) => e.Depth switch { 0 => e.W / 2, 1 => e.W, _ => e.W * 2 };

    /// <summary>-> (palette indices [h, w], palette) of picture i; null for a palette, a 16bpp picture or bad data</summary>
    public static (byte[,] Idx, ushort[] Pal)? Data(byte[] d, int i)
    {
        var es = Parse(d);
        if (es == null || i >= es.Count) return null;
        var e = es[i];
        if (e.Colors > 0 || e.Depth > 1) return null;
        byte[] raw;
        try { raw = Lz.Unpack(d, e.Off + 8, RowBytes(e) * e.H, RowBytes(e)); } catch (Exception x) when (x is IndexOutOfRangeException or ArgumentOutOfRangeException) { return null; }
        var idx = new byte[e.H, e.W];
        for (int y = 0; y < e.H; y++)
            for (int x = 0; x < e.W; x++)
                idx[y, x] = e.Depth == 1 ? raw[y * e.W + x] : (byte)(raw[(y * e.W + x) / 2] >> (x % 2 * 4) & 15);
        var pal = new ushort[e.Depth == 0 ? 16 : 256];
        if (PaletteOf(es, i) is Entry p)
            for (int k = 0; k < Math.Min(p.Colors, pal.Length); k++) pal[k] = (ushort)U16(d, p.Off + 4 + 2 * k);
        return (idx, pal);
    }

    static Entry? PaletteOf(List<Entry> es, int i)
    {
        var pals = es.Where(e => e.Colors > 0).ToList();
        var pics = es.Where(e => e.Colors == 0).ToList();
        if (pals.Count == pics.Count) return pals[pics.IndexOf(es[i])];
        var fit = pals.Where(p => (p.Colors == 16) == (es[i].Depth == 0)).ToList();
        return fit.LastOrDefault(p => p.Off < es[i].Off) ?? fit.FirstOrDefault();
    }

    /// <summary>picture i redrawn with idx (same size) -> new file bytes; later entries move</summary>
    public static byte[] Put(byte[] d, int i, byte[,] idx)
    {
        var es = Parse(d)!;
        var e = es[i];
        var raw = new byte[RowBytes(e) * e.H];
        for (int y = 0; y < e.H; y++)
            for (int x = 0; x < e.W; x++)
            {
                if (e.Depth == 1) raw[y * e.W + x] = idx[y, x];
                else raw[(y * e.W + x) / 2] |= (byte)(idx[y, x] << (x % 2 * 4));
            }
        var body = d.AsSpan(e.Off, 8).ToArray().Concat(Lz.Pack(raw, RowBytes(e))).ToList();
        while (body.Count % 4 != 0) body.Add(0);
        int grow = body.Count - (e.End - e.Off);
        var outp = d.AsSpan(0, e.Off).ToArray().Concat(body).Concat(d.AsSpan(e.End).ToArray()).ToArray();
        for (int k = i + 1; k < es.Count; k++) BinaryPrimitives.WriteInt32LittleEndian(outp.AsSpan(4 + 4 * k), es[k].Off + grow);
        return outp;
    }
}

/// <summary>
/// LZ of Gunparade March pictures (EXE 0x80022318). Flag bits, low bit first, a flag byte read whenever one is needed:
/// 1 = literal byte; 01 = byte b: copy (b &gt;&gt; 6) + 2 bytes from (b &amp; 63) - 64 back; 00 = big-endian u16 a:
/// copy (a &gt;&gt; 12) + 2 bytes from (a &amp; 0xFFF) - 0x1000 back, or when a &gt;&gt; 12 is 0 a further byte n
/// gives n + 2 bytes, and n = 0 ends the stream. Pictures over 0xFFFF bytes are cut into separate streams of whole
/// rows (0x10000 - 0x10000 % row bytes each).
/// </summary>
public static class Lz
{
    public static byte[] Decode(byte[] d, ref int p)
    {
        var o = new List<byte>();
        int bits = 0, n = 0;
        while (true)
        {
            if (Bit(d, ref p, ref bits, ref n)) { o.Add(d[p++]); continue; }
            int len, off;
            if (Bit(d, ref p, ref bits, ref n)) { int b = d[p++]; len = (b >> 6) + 2; off = (b & 63) - 64; }
            else
            {
                int a = d[p] << 8 | d[p + 1]; p += 2;
                off = (a & 0xFFF) - 0x1000; len = a >> 12;
                if (len == 0) { int k = d[p++]; if (k == 0) return o.ToArray(); len = k + 2; }
                else len += 2;
            }
            int s = o.Count + off;
            for (int i = 0; i < len; i++) o.Add(o[s + i]);
        }
    }

    static bool Bit(byte[] d, ref int p, ref int bits, ref int n)
    {
        if (n == 0) { bits = d[p++]; n = 8; }
        bool b = (bits & 1) != 0; bits >>= 1; n--;
        return b;
    }

    static int Chunk(int left, int rowBytes) => left > 0xFFFF ? 0x10000 - 0x10000 % rowBytes : left;

    public static byte[] Unpack(byte[] d, int p, int total, int rowBytes)
    {
        var o = new List<byte>(total);
        while (o.Count < total) o.AddRange(Decode(d, ref p));
        return o.Take(total).ToArray();
    }

    public static byte[] Pack(byte[] raw, int rowBytes)
    {
        var o = new List<byte>();
        for (int at = 0; at < raw.Length;)
        {
            int n = Chunk(raw.Length - at, rowBytes);
            o.AddRange(Encode(raw.AsSpan(at, n)));
            at += n;
        }
        return o.ToArray();
    }

    /// <summary>greedy: longest match in the last 4096 bytes, the 1-byte form when it reaches</summary>
    // ponytail: brute-force match search, O(n * 4096); fine for menu-sized pictures, hash chains if whole screens get re-packed
    public static byte[] Encode(ReadOnlySpan<byte> s)
    {
        var o = new List<byte>();
        int flagAt = -1, used = 8;
        void Bit(bool b)
        {
            if (used == 8) { flagAt = o.Count; o.Add(0); used = 0; }
            if (b) o[flagAt] |= (byte)(1 << used);
            used++;
        }
        for (int i = 0; i < s.Length;)
        {
            int best = 0, bestOff = 0;
            for (int j = i - 1; j >= Math.Max(0, i - 0x1000); j--)
            {
                int k = 0;
                while (k < 257 && i + k < s.Length && s[j + k] == s[i + k]) k++;
                if (k > best) { best = k; bestOff = j; }   // ties keep the nearest
                if (best == 257) break;
            }
            int back = i - bestOff;
            if (best >= 2 && back <= 64 && best <= 5)
            {
                Bit(false); Bit(true);
                o.Add((byte)((best - 2) << 6 | (64 - back)));
                i += best;
            }
            else if (best >= 3)
            {
                Bit(false); Bit(false);
                int a = 0x1000 - back;
                if (best <= 17) { a |= (best - 2) << 12; o.Add((byte)(a >> 8)); o.Add((byte)a); }
                else { o.Add((byte)(a >> 8)); o.Add((byte)a); o.Add((byte)(best - 2)); }
                i += best;
            }
            else { Bit(true); o.Add(s[i]); i++; }
        }
        Bit(false); Bit(false); o.AddRange([0, 0, 0]);
        return o.ToArray();
    }
}
