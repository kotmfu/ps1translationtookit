using System.Buffers.Binary;
using System.Text;

namespace Ps1tl;

/// <summary>
/// Shift-JIS strings in Gunparade March's EXE and code overlays: character names (EXE tables at 0x800a12b8,
/// 0x800a1494), the prologue (MOD/FINAMISC.BIN, $w wait, $n new line, $z end), menus and system messages.
/// A string is any NUL-terminated Shift-JIS text with kana/kanji that something points at: a data word (pointer
/// tables) or a lui/addiu pair in code. English is written half-width (04 first, as in event rows; the name plate
/// and the $-text drawer both honour it). A string that no longer fits its bytes moves into free space of the same
/// file - the bytes of strings that moved, and strings only the debug printf reads (EXE 0x8001b70c, silent unless a
/// debug flag is set) - and every pointer to it is rewritten. The EXE cannot grow (its BSS follows). Overlays load
/// at 0x800b54d8 (big ones) or 0x800f2800 (small ones, below the EVDATA sector table at 0x800ff3d0); each slot is as
/// big as its largest overlay, so a smaller overlay may grow into the slot past its own variables (the highest
/// address its code touches).
/// </summary>
public static class GpmStrings
{
    public const string ExePath = "/SCPS_101.36";
    const uint ExeBase = 0x80010010 - 0x800, Printf = 0x8001b70c, Assert = 0x8002cbf8;
    const uint Big = 0x800b54d8, Small = 0x800f2800;
    static readonly Dictionary<string, uint> Overlays = new()
    {
        ["/MOD/AROUND.BIN"] = Big, ["/MOD/BTLDEMO.BIN"] = Big, ["/MOD/FREE.BIN"] = Big, ["/MOD/INITCHR.BIN"] = Big,
        ["/MOD/TITLE.BIN"] = Big, ["/OTHER/BATTLE.BIN"] = Big,
        ["/MOD/BTLDBG.BIN"] = Small, ["/MOD/ENDING.BIN"] = Small, ["/MOD/EVMISC.BIN"] = Small, ["/MOD/EX_DEBUG.BIN"] = Small,
        ["/MOD/EX_LUNCH.BIN"] = Small, ["/MOD/EX_STWIN.BIN"] = Small, ["/MOD/EX_TEIAN.BIN"] = Small, ["/MOD/FINAMISC.BIN"] = Small,
        ["/MOD/JOSO.BIN"] = Small, ["/MOD/LOOKSTR.BIN"] = Small, ["/MOD/LSNSTR.BIN"] = Small, ["/MOD/MAINT.BIN"] = Small,
        ["/MOD/MISC.BIN"] = Small, ["/MOD/MISC2.BIN"] = Small, ["/MOD/MMAP.BIN"] = Small, ["/OTHER/BTLEND.BIN"] = Small,
        ["/OTHER/BTLINP.BIN"] = Small, ["/OTHER/BTLOPEN.BIN"] = Small, ["/OTHER/BTLSTG.BIN"] = Small, ["/OTHER/BTLTUTO.BIN"] = Small,
    };

    static readonly Encoding Sjis = GetSjis();
    static Encoding GetSjis() { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); return Encoding.GetEncoding(932); }

    /// <summary>a pointer to a string: a data word at At, or the addiu at At (Lui = its lui, -1 if none to patch)</summary>
    public sealed record Ptr(int At, bool Word, int Lui);
    public sealed record Str(uint Addr, int Off, int Len, string Ja, List<Ptr> Ptrs);

    public static IEnumerable<(string Path, uint Base)> Files(Disc disc) =>
        new[] { (ExePath, ExeBase) }.Concat(Overlays.Select(kv => (kv.Key, kv.Value))).Where(f => disc.Files().ContainsKey(f.Item1));

    /// <summary>how big a file may become: the EXE as it is, an overlay up to the largest overlay of its slot</summary>
    public static int Room(Disc disc, string path) =>
        path == ExePath ? disc.Files()[path].Size
        : Overlays.Where(kv => kv.Value == Overlays[path] && disc.Files().ContainsKey(kv.Key)).Max(kv => disc.Files()[kv.Key].Size);

    static uint W(byte[] d, int o) => BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o));

    /// <summary>every referenced address in the file -> its pointers; also the addresses only debug printf/assert use</summary>
    static (Dictionary<uint, List<Ptr>> Refs, HashSet<uint> DebugOnly, int Top) Scan(byte[] d, uint b, int room = 0)
    {
        int top = d.Length;   // past the highest address the code reads or writes (its variables), as a file offset
        int start = b == ExeBase ? 0x800 : 0;
        uint lo = b + (uint)start, hi = b + (uint)d.Length;
        var refs = new Dictionary<uint, List<Ptr>>();
        var use = new Dictionary<uint, bool>();   // address -> used only as a0 of printf/assert so far
        void Add(uint t, Ptr p, bool debug)
        {
            if (t < lo || t >= hi) return;
            (refs.TryGetValue(t, out var l) ? l : refs[t] = new()).Add(p);
            use[t] = use.GetValueOrDefault(t, true) && debug;
        }
        var lui = new (uint Val, int At)[32];
        int dest = 0;   // register the previous instruction wrote: its lui value is gone
        for (int o = start; o + 4 <= d.Length; o += 4)
        {
            if (dest != 0) lui[dest] = default;
            uint x = W(d, o);
            uint op0 = x >> 26;
            dest = op0 switch
            {
                0 => (int)(x >> 11 & 31),
                3 => 31,
                >= 8 and <= 0xE or >= 0x20 and <= 0x26 => (int)(x >> 16 & 31),
                _ => 0,
            };
            Add(x, new Ptr(o, true, -1), false);
            uint op = x >> 26, rs = x >> 21 & 31, rt = x >> 16 & 31;
            if (op == 0xF) { lui[rt] = (x << 16, o); continue; }
            if (op is >= 0x20 and <= 0x2E && lui[rs].At > 0 && o - lui[rs].At <= 48)
            {
                uint m = lui[rs].Val + (uint)(short)(x & 0xFFFF);
                if (m >= b && m < b + (uint)room) top = Math.Max(top, (int)(m - b) + 4);
            }
            if (op == 9 && lui[rs].At > 0 && o - lui[rs].At <= 48)
            {
                uint m = lui[rs].Val + (uint)(short)(x & 0xFFFF);
                if (m >= b && m < b + (uint)room) top = Math.Max(top, (int)(m - b) + 4);
            }
            if (op != 9 || lui[rs].At <= 0 && lui[rs].Val == 0 || o - lui[rs].At > 48) continue;
            uint t = lui[rs].Val + (uint)(short)(x & 0xFFFF);
            // debug use: the address goes to a0 and the next call is printf or assert
            bool debug = false;
            if (rt == 4)
                for (int k = o + 4; k < Math.Min(d.Length - 4, o + 32); k += 4)
                    if (W(d, k) >> 26 == 3) { uint j = 0x80000000 | (W(d, k) & 0x3FFFFFF) << 2; debug = j is Printf or Assert; break; }
            Add(t, new Ptr(o, false, rs == rt ? lui[rs].At : -1), debug);
        }
        return (refs, use.Where(kv => kv.Value).Select(kv => kv.Key).ToHashSet(), top);
    }

    /// <summary>Shift-JIS text at off up to its NUL, with at least one kana/kanji, else null</summary>
    static string? TextAt(byte[] d, int off, out int len)
    {
        len = 0;
        bool jp = false;
        int p = off;
        while (p < d.Length && d[p] != 0)
        {
            byte c = d[p];
            if (c is >= 0x81 and <= 0x9F or >= 0xE0 and <= 0xFC)
            {
                if (p + 1 >= d.Length || d[p + 1] is < 0x40 or 0x7F or > 0xFC) return null;
                if (c >= 0x82 && (c, d[p + 1]) is not (0x82, < 0x9F)) jp = true;   // past full-width digits/letters
                p += 2;
            }
            else if (c is >= 0x20 and < 0x7F || c == 0x0D) p++;
            else return null;
        }
        if (!jp || p >= d.Length) return null;
        len = p - off;
        var t = Sjis.GetString(d, off, len);
        // a long run of kanji with no kana, punctuation or codes is a glyph list (EXE 0x80010914), not text
        if (len > 24 && !t.Any(ch => ch is >= '　' and <= 'ヿ' or '%' or '$' or '！' or '？')) return null;
        return t;
    }

    public static List<Str> Strings(byte[] d, uint b)
    {
        var (refs, debug, _) = Scan(d, b);
        var outp = new List<Str>();
        foreach (var (a, ptrs) in refs.OrderBy(kv => kv.Key))
        {
            if (debug.Contains(a)) continue;
            int off = (int)(a - b);
            if (outp.Count > 0 && off <= outp[^1].Off + outp[^1].Len) continue;   // a pointer into the middle of a string
            if (TextAt(d, off, out int len) is not string ja) continue;
            outp.Add(new Str(a, off, len, ja, ptrs));
        }
        return outp;
    }

    /// <summary>(leading $-codes, text, trailing $-codes) of a prologue-style string; plain strings have no codes</summary>
    static (string Lead, string Body, string Tail) Split(string ja)
    {
        int i = 0;
        while (i + 1 < ja.Length && ja[i] == '$' && ja[i + 1] != 'n') i += 2;
        int j = ja.Length;
        while (j - 2 >= i && ja[j - 2] == '$') j -= 2;
        return (ja[..i], ja[i..j], ja[j..]);
    }

    /// <summary>how a string shows in the editor: no wait/end codes, $n as a new line</summary>
    public static string Display(string ja) => Split(ja).Body.Replace("$w", "").Replace("$z", "").Replace("$n", "\n");
    public static string Ref(string path, uint addr) => $"{Path.GetFileNameWithoutExtension(path)}@{addr:x8}";

    /// <summary>English -> bytes. Plain strings: 04, half-width characters (the name plate and message windows draw
    /// them like event rows). Strings with $-codes (the prologue): their drawer reads only 2-byte characters, so the
    /// English goes full-width (Ａｂｃ), wrapped into 22-column lines joined by $n, between the Japanese leading and
    /// trailing codes.</summary>
    public static byte[] Encode(string en, string ja)
    {
        var (lead, _, tail) = Split(ja);
        if (ja.Contains('$'))
        {
            var text = string.Join("$n", Gunparade.Wrap(en, FullCols).Select(row =>
                new string(row.Select(c => c switch
                {
                    ' ' => '　', >= '!' and <= '~' => (char)(c - '!' + '！'),
                    '‘' or '’' or '`' => '＇', '“' or '”' => '＂', '–' or '—' => '－', _ => c,
                }).ToArray())));
            var outp = new List<byte>(Sjis.GetBytes(lead));
            foreach (var ch in text)   // a character Shift-JIS lacks becomes a full-width '?'
                outp.AddRange(Sjis.GetBytes(Sjis.GetString(Sjis.GetBytes(ch.ToString())) == ch.ToString() ? ch.ToString() : "？"));
            outp.AddRange(Sjis.GetBytes(tail));
            return outp.ToArray();
        }
        var b = new List<byte> { 0x04 };
        en = en.Replace("\r", "").Replace('\n', ' ');
        for (int i = 0; i < en.Length; i++)
            b.Add(en[i] switch
            {
                'z' => 0x7B, '…' => 0x7A, >= 'a' and <= 'y' => (byte)en[i],
                '`' or '‘' or '’' => (byte)'\'', '“' or '”' => (byte)'"', '–' or '—' or '~' => (byte)'-',
                >= ' ' and <= '_' => (byte)en[i], _ => (byte)'?',
            });
        return b.ToArray();
    }

    /// <summary>full-width letters per prologue line (its window is 0x118 = 280 px, 12 px a letter)</summary>
    const int FullCols = 22;

    /// <summary>
    /// write English into one file. en(string) -> English or null to keep. -> (new bytes or null if unchanged, strings
    /// that found no room). In place when the English fits the Japanese bytes, else into the file's free space.
    /// </summary>
    public static (byte[]? Data, List<uint> NoRoom) Insert(byte[] d, uint b, Func<Str, string?> en, int room = 0)
    {
        var strs = Strings(d, b);
        var (_, debug, top) = Scan(d, b, room);
        var outp = new byte[Math.Max(room, d.Length)];
        d.CopyTo(outp, 0);
        var news = new List<(Str S, byte[] Bytes)>();
        foreach (var s in strs)
            if (en(s) is { Length: > 0 } t) news.Add((s, Encode(t, s.Ja)));
        if (news.Count == 0) return (null, new());
        // free space: debug-only strings, and the Japanese of strings that move
        var free = new List<(int Off, int Len)>();
        foreach (var a in debug)
        {
            int o = (int)(a - b);
            if (o >= 0 && o < d.Length && (o == 0 || d[o - 1] == 0)) { int e = Array.IndexOf(d, (byte)0, o); if (e > o) free.Add((o, e - o + 1)); }
        }
        int tail = (top + 15) & ~15;   // the slot past the overlay's own variables
        if (tail < room) free.Add((tail, room - tail));
        int used = d.Length;
        var moving = new List<(Str S, byte[] Bytes)>();
        foreach (var (s, bytes) in news)
            if (bytes.Length <= s.Len) { Array.Clear(outp, s.Off, s.Len); bytes.CopyTo(outp, s.Off); }
            else moving.Add((s, bytes));
        // place the biggest first; a string's own bytes are free only once it has moved, so one that finds no room
        // keeps its Japanese intact
        var noRoom = new List<uint>();
        var todo = moving.OrderByDescending(m => m.Bytes.Length).ToList();
        for (bool progress = true; progress && todo.Count > 0;)
        {
            progress = false;
            free = Merge(free);
            foreach (var m in todo.ToList())
            {
                int k = free.FindIndex(f => f.Len >= m.Bytes.Length + 1 && m.S.Ptrs.All(p => CanPoint(p, b + (uint)f.Off, m.S.Addr)));
                if (k < 0) continue;
                var (fo, fl) = free[k];
                free[k] = (fo + m.Bytes.Length + 1, fl - m.Bytes.Length - 1);
                Array.Clear(outp, fo, m.Bytes.Length + 1);
                m.Bytes.CopyTo(outp, fo);
                used = Math.Max(used, fo + m.Bytes.Length + 1);
                foreach (var p in m.S.Ptrs) Repoint(outp, p, b + (uint)fo);
                Array.Clear(outp, m.S.Off, m.S.Len);   // old bytes: free (zeroed, so a stale pointer reads an empty string)
                free.Add((m.S.Off, m.S.Len + 1));
                todo.Remove(m);
                progress = true;
            }
        }
        noRoom.AddRange(todo.Select(m => m.S.Addr));
        return (outp[..(used > d.Length ? (used + 3) & ~3 : d.Length)], noRoom);
    }

    /// <summary>a lui/addiu pair whose lui is shared can only reach addresses with the same upper half</summary>
    static bool CanPoint(Ptr p, uint to, uint from) => p.Word || p.Lui >= 0 || Hi(to) == Hi(from);
    static uint Hi(uint a) => (a >> 16) + ((a & 0x8000) != 0 ? 1u : 0);

    static List<(int Off, int Len)> Merge(List<(int Off, int Len)> f)
    {
        var outp = new List<(int Off, int Len)>();
        foreach (var r in f.OrderBy(r => r.Off))
            if (outp.Count > 0 && outp[^1].Off + outp[^1].Len >= r.Off)
                outp[^1] = (outp[^1].Off, Math.Max(outp[^1].Off + outp[^1].Len, r.Off + r.Len) - outp[^1].Off);
            else outp.Add(r);
        return outp;
    }

    static void Repoint(byte[] d, Ptr p, uint a)
    {
        if (p.Word) { BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(p.At), a); return; }
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(p.At), W(d, p.At) & 0xFFFF0000 | a & 0xFFFF);
        if (p.Lui >= 0) BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(p.Lui), W(d, p.Lui) & 0xFFFF0000 | Hi(a));
    }
}
