using System.Reflection;

namespace Ps1tl;

/// <summary>Built-in pixel fonts. A font file (fonts/*.txt) holds '#'-art glyphs; text before the first
/// "== c" line is a header comment. Glyph width = longest row.</summary>
public sealed class Font
{
    public const int Core = 13, Edge = 3, Bg = 15;
    const int Top = 2;          // cell row where font row 0 lands (rows 2..13, outline 1..14)
    const int SpaceW = 3;
    public const int CellInk = 14;   // widest ink a 16px cell holds with its outline

    public Dictionary<char, string[]> Glyphs { get; } = new();
    public string[] this[char c] => Glyphs.TryGetValue(c, out var g) ? g : Glyphs['?'];
    public bool Has(char c) => Glyphs.ContainsKey(c);
    public int Width(char c) => this[c].Max(r => r.Length);

    static readonly Dictionary<string, Font> Cache = new();

    public static Font Load(string name = "en_pixel")
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(name, out var f)) return f;
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream($"fonts.{name}.txt")
                          ?? throw new FileNotFoundException($"font {name}");
            f = Parse(new StreamReader(s).ReadToEnd());
            return Cache[name] = f;
        }
    }

    public static Font Parse(string text)
    {
        var rows = new Dictionary<char, List<string>>();
        List<string>? cur = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("== ")) rows[line.Length > 3 ? line[3] : ' '] = cur = new List<string>();
            else if (cur != null && line.Length > 0) cur.Add(line);   // text before the first '==' is a comment
        }
        var font = new Font();
        foreach (var (c, r) in rows) font.Glyphs[c] = r.ToArray();
        font.Glyphs[' '] = [new string('.', SpaceW)];
        return font;
    }

    /// <summary>'#'-art -> (128 bytes 4bpp 16x16 cell, x, w). Light core, dark 1px outline, 15 = transparent.</summary>
    public static (byte[] Bitmap, int X, int W) ToCell(string[] rows)
    {
        var px = new int[16, 16];
        for (int y = 0; y < 16; y++) for (int x = 0; x < 16; x++) px[y, x] = Bg;
        var ink = new List<(int y, int x)>();
        for (int y = 0; y < rows.Length; y++)
            for (int x = 0; x < rows[y].Length; x++)
                if (rows[y][x] == '#') ink.Add((Top + y, 1 + x));
        foreach (var (y, x) in ink)
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                    if (y + dy is >= 0 and < 16 && x + dx is >= 0 and < 16 && px[y + dy, x + dx] == Bg) px[y + dy, x + dx] = Edge;
        foreach (var (y, x) in ink) px[y, x] = Core;
        var data = new byte[128];
        for (int i = 0; i < 256; i += 2) data[i / 2] = (byte)(px[i / 16, i % 16] | px[(i + 1) / 16, (i + 1) % 16] << 4);
        return (data, 0, rows.Max(r => r.Length) + 2);
    }

    /// <summary>'#'-art of several characters side by side, spaced exactly as separate glyphs would be</summary>
    public string[] Join(string text)
    {
        var parts = text.Select(c => Glyphs[c]).ToArray();
        int h = parts.Max(p => p.Length);
        var widths = parts.Select(g => g.Max(r => r.Length)).ToArray();
        return Enumerable.Range(0, h).Select(y =>
            string.Join("..", parts.Select((g, i) => (y < g.Length ? g[y] : "").PadRight(widths[i], '.')))).ToArray();
    }

    /// <summary>split text into runs that each fit one 16px cell. The engine draws one sprite per cell and only
    /// has a small pool of them for the dialogue box (~30, sized for Japanese), so narrow letters share cells.</summary>
    public List<string> Pack(string text)
    {
        var outp = new List<string>();
        foreach (var c in text)
        {
            if (outp.Count > 0 && Join(outp[^1] + c)[0].Length <= CellInk) outp[^1] += c;
            else outp.Add(c.ToString());
        }
        return outp;
    }

    public List<char> Missing(string text) => text.Where(c => !Glyphs.ContainsKey(c)).Distinct().Order().ToList();

    /// <summary>pixel width of text drawn with 2px spacing (dialogue metric: width + 2 per glyph)</summary>
    public int TextWidth(string text) => text.Sum(c => Width(c) + 2);
}
