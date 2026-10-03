using System.Text;

namespace Ps1tl;

/// <summary>Translation files: shareable CSV (key, ja, en, notes) that people edit and load back.
/// Lines are matched by key (hash of the line's glyph bitmaps), so a file made from one copy of the game
/// applies to any copy of the same game.</summary>
public static class Tfile
{
    static readonly string[] Columns = ["key", "ja", "en", "notes"];

    /// <summary>matching key for Japanese text: smooths over common OCR mix-ups (一/ー, half/full-width ?!)</summary>
    public static string NormJa(string? ja)
    {
        var sb = new StringBuilder();
        foreach (var c in ja ?? "")
        {
            switch (c)
            {
                case '一' or '－': sb.Append('ー'); break;
                case '?': sb.Append('？'); break;
                case '!': sb.Append('！'); break;
                case ' ' or '　': break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>{normalised ja: en} from translated lines. The same sentence is often stored with slightly
    /// different glyph bitmaps (so a different key); one translation covers every copy.</summary>
    public static Dictionary<string, string> SharedEn(Script script)
    {
        var outp = new Dictionary<string, string>();
        foreach (var l in script.Lines)
            if (l.En.Length > 0 && l.Ja.Length > 0) outp.TryAdd(NormJa(l.Ja), l.En);
        return outp;
    }

    /// <summary>the line's own English, else a translation of identical Japanese elsewhere ("" if none)</summary>
    public static string EffectiveEn(Line l, Dictionary<string, string> shared) =>
        l.En.Length > 0 ? l.En : l.Ja.Length > 0 && shared.TryGetValue(NormJa(l.Ja), out var e) ? e : "";

    public static void Save(Script script, string path)
    {
        var sb = new StringBuilder();
        sb.Append(string.Join(',', Columns)).Append("\r\n");
        foreach (var l in script.Lines)
            sb.Append(string.Join(',', new[] { l.Key, l.Ja, l.En, l.Notes ?? "" }.Select(Quote))).Append("\r\n");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));   // BOM so Excel shows Japanese
    }

    static string Quote(string v) =>
        v.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;

    /// <summary>merge a translation file into script -> (updated, unknown keys)</summary>
    public static (int Updated, int Unknown) Load(Script script, string path, bool overwrite = true)
    {
        var byKey = new Dictionary<string, Line>();
        foreach (var l in script.Lines) byKey.TryAdd(l.Key, l);
        var rows = ParseCsv(File.ReadAllText(path));   // ReadAllText drops the BOM
        if (rows.Count == 0) return (0, 0);
        var head = rows[0];
        int updated = 0, unknown = 0;
        foreach (var row in rows.Skip(1))
        {
            string Col(string name) { int i = head.IndexOf(name); return i >= 0 && i < row.Count ? row[i] : ""; }
            if (!byKey.TryGetValue(Col("key"), out var l)) { unknown++; continue; }
            foreach (var col in new[] { "ja", "en", "notes" })
            {
                var v = Col(col).Trim();
                if (v.Length == 0) continue;
                if (col == "ja" && (overwrite || l.Ja.Length == 0)) l.Ja = v;
                if (col == "en" && (overwrite || l.En.Length == 0)) l.En = v;
                if (col == "notes" && (overwrite || string.IsNullOrEmpty(l.Notes))) l.Notes = v;
            }
            updated++;
        }
        return (updated, unknown);
    }

    /// <summary>RFC 4180 CSV (what Python's csv module and Excel write)</summary>
    public static List<List<string>> ParseCsv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        bool quoted = false, any = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else cell.Append(c);
                continue;
            }
            switch (c)
            {
                case '"': quoted = true; any = true; break;
                case ',': row.Add(cell.ToString()); cell.Clear(); any = true; break;
                case '\r': break;
                case '\n': row.Add(cell.ToString()); cell.Clear(); rows.Add(row); row = new(); any = false; break;
                default: cell.Append(c); any = true; break;
            }
        }
        if (any || cell.Length > 0) { row.Add(cell.ToString()); rows.Add(row); }
        return rows;
    }
}
