namespace Ps1tl;

public enum GlyphStatus { Disagree, Unverified, Verified, Confirmed }

/// <summary>one character shape: every glyph bitmap with the same pixels (they differ only in shading levels)</summary>
public sealed record GlyphGroup(string Key, List<string> Gids, string Char, int Uses, Dictionary<string, int> Votes,
                                GlyphStatus Status, bool Confusable);

/// <summary>
/// The glyph -> character table (Script.Chars), which turns the game's glyph numbers into Japanese text.
/// Learning only trusts lines whose Japanese was read from the whole-line picture by Claude or typed by a person
/// (Line.JaSource); lines filled from the table itself never vote, so they cannot outvote corrections.
/// Glyphs with identical pixels are one group and always share one character. Characters confirmed by a person
/// in the glyph grid (Script.CharsOk) are never changed by learning.
/// </summary>
public static class GlyphTable
{
    /// <summary>characters the glyph OCR tends to confuse: small kana, dakuten/handakuten, look-alike kana/kanji/marks</summary>
    const string ConfusableChars =
        "ぁぃぅぇぉっゃゅょゎァィゥェォッャュョヮヵヶヴ" +
        "がぎぐげござじずぜぞだぢづでどばびぶべぼぱぴぷぺぽ" +
        "ガギグゲゴザジズゼゾダヂヅデドバビブベボパピプペポ" +
        "はひふへほハヒフヘホかカりリへヘべベぺペ" +
        "ー一－ロ口カ力エ工ニ二ハ八タ夕ト卜×メ・、。゛゜♪』「」";

    public static bool IsConfusable(string c) => c.Length > 0 && ConfusableChars.Contains(c);

    /// <summary>shape key: which pixels are ink and which of those are light fill (ignores exact shade)</summary>
    public static string ShapeKey(GlyphEntry e)
    {
        var px = Yuuyami.GlyphPixels(e);
        var key = new char[256];
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
            {
                int v = px[y, x];
                key[y * 16 + x] = v == 15 ? '.' : v >= 6 ? '#' : '+';
            }
        return new string(key);
    }

    public static Dictionary<string, List<string>> Groups(Script s)
    {
        var g = new Dictionary<string, List<string>>();
        foreach (var (gid, e) in s.Glyphs)
        {
            var k = ShapeKey(e);
            if (!g.TryGetValue(k, out var list)) g[k] = list = new List<string>();
            list.Add(gid);
        }
        return g;
    }

    static bool Trusted(Line l) => l.JaSource is "pic" or "user" or "rom";   // rom: decoded from the game's own text encoding

    static List<string> Runes(string s) => s.EnumerateRunes().Select(r => r.ToString()).ToList();

    /// <summary>half-width ASCII read as its full-width form (the font's cells are full-width): ? and ？ are one reading</summary>
    static string Wide(string c) => c.Length == 1 && c[0] is > ' ' and <= '~' ? ((char)(c[0] + 0xFEE0)).ToString() : c == " " ? "　" : c;

    /// <summary>votes per glyph from trusted lines whose Japanese has exactly one character per glyph</summary>
    static Dictionary<string, Dictionary<string, int>> Votes(Script s)
    {
        var votes = new Dictionary<string, Dictionary<string, int>>();
        foreach (var l in s.Lines)
        {
            if (!Trusted(l) || l.Glyphs.Count == 0) continue;
            var ja = Runes(l.Ja);
            if (ja.Count != l.Glyphs.Count) continue;
            for (int i = 0; i < ja.Count; i++)
            {
                if (!votes.TryGetValue(l.Glyphs[i], out var v)) votes[l.Glyphs[i]] = v = new();
                var c = Wide(ja[i]);
                v[c] = v.GetValueOrDefault(c) + 1;
            }
        }
        return votes;
    }

    static Dictionary<string, int> GroupVotes(List<string> gids, Dictionary<string, Dictionary<string, int>> votes)
    {
        var gv = new Dictionary<string, int>();
        foreach (var g in gids)
            if (votes.TryGetValue(g, out var v))
                foreach (var (c, n) in v) gv[c] = gv.GetValueOrDefault(c) + n;
        return gv;
    }

    static Dictionary<string, int> Uses(Script s)
    {
        var u = new Dictionary<string, int>();
        foreach (var l in s.Lines) foreach (var g in l.Glyphs) u[g] = u.GetValueOrDefault(g) + 1;
        return u;
    }

    /// <summary>re-derive every group's character: confirmed > trusted votes > most-used member's current reading.
    /// -> number of glyph entries whose character changed</summary>
    public static int Learn(Script s, Dictionary<string, List<string>>? groups = null)
    {
        groups ??= Groups(s);
        s.Chars ??= new();
        var ok = s.CharsOk ?? new();
        var votes = Votes(s);
        var uses = Uses(s);
        int changed = 0;
        foreach (var gids in groups.Values)
        {
            string? c = gids.Where(ok.Contains).Select(g => s.Chars.GetValueOrDefault(g)).FirstOrDefault(x => x != null);
            if (c == null)
            {
                var gv = GroupVotes(gids, votes);
                if (gv.Count > 0)
                {
                    int best = gv.Values.Max();
                    var top = gv.Where(kv => kv.Value == best).Select(kv => kv.Key).ToList();
                    var cur = gids.Select(g => s.Chars.GetValueOrDefault(g)).FirstOrDefault(x => x != null && top.Contains(x));
                    c = cur ?? top[0];   // ties keep the current reading
                }
                else c = gids.Where(s.Chars.ContainsKey).OrderByDescending(g => uses.GetValueOrDefault(g)).Select(g => s.Chars[g]).FirstOrDefault();
            }
            if (c == null) continue;
            foreach (var g in gids)
                if (s.Chars.GetValueOrDefault(g) != c) { s.Chars[g] = c; changed++; }
        }
        return changed;
    }

    /// <summary>refill the Japanese of every line that came from the table (trusted lines keep theirs)
    /// -> number of lines whose text changed</summary>
    public static int Apply(Script s)
    {
        var chars = s.Chars ?? new();
        int n = 0;
        foreach (var l in s.Lines)
        {
            if (l.Glyphs.Count == 0 || Trusted(l) || !l.Glyphs.All(chars.ContainsKey)) continue;
            var ja = string.Concat(l.Glyphs.Select(g => chars[g]));
            if (ja != l.Ja) { l.Ja = ja; n++; }
        }
        return n;
    }

    /// <summary>a person set (or confirmed) the character of one shape: applies to every glyph of the group</summary>
    public static int SetChar(Script s, List<string> gids, string c)
    {
        s.Chars ??= new();
        s.CharsOk ??= new();
        foreach (var g in gids) { s.Chars[g] = c; s.CharsOk.Add(g); }
        return Apply(s);
    }

    /// <summary>every shape with its reading, how trusted lines read it, and a status; most doubtful first:
    /// disagreements, then unverified look-alikes, then other unverified, then verified, then confirmed
    /// (each by how often it appears)</summary>
    public static List<GlyphGroup> Report(Script s, Dictionary<string, List<string>>? groups = null)
    {
        groups ??= Groups(s);
        var votes = Votes(s);
        var uses = Uses(s);
        var ok = s.CharsOk ?? new();
        var chars = s.Chars ?? new();
        var outp = new List<GlyphGroup>();
        foreach (var (key, gids) in groups)
        {
            var c = gids.Select(g => chars.GetValueOrDefault(g)).FirstOrDefault(x => x != null) ?? "";
            var gv = GroupVotes(gids, votes);
            // a stray misreading (e.g. 498 × 、 and 1 × っ) is noise; another reading with 10%+ of the votes is real doubt
            int total = gv.Values.Sum(), alt = gv.Where(kv => kv.Key != Wide(c)).Select(kv => kv.Value).DefaultIfEmpty(0).Max();
            var status = gids.Any(ok.Contains) ? GlyphStatus.Confirmed
                       : alt > 0 && alt * 10 >= total ? GlyphStatus.Disagree
                       : gv.Count > 0 ? GlyphStatus.Verified : GlyphStatus.Unverified;
            outp.Add(new GlyphGroup(key, gids, c, gids.Sum(g => uses.GetValueOrDefault(g)), gv, status, IsConfusable(c)));
        }
        int Rank(GlyphGroup g) => g.Status switch
        {
            GlyphStatus.Disagree => 0,
            GlyphStatus.Unverified => g.Confusable ? 1 : 2,
            GlyphStatus.Verified => 3,
            _ => 4,
        };
        return outp.OrderBy(Rank).ThenByDescending(g => g.Uses).ToList();
    }

    /// <summary>glyphs whose character is verified by Claude's picture readings or confirmed by a person</summary>
    public static HashSet<string> TrustedGids(Script s, Dictionary<string, List<string>>? groups = null) =>
        Report(s, groups).Where(g => g.Status >= GlyphStatus.Verified).SelectMany(g => g.Gids).ToHashSet();

    /// <summary>glyphs whose character is still in doubt (unverified or disagreeing), mapped to their shape key</summary>
    public static Dictionary<string, string> DoubtfulGids(Script s, Dictionary<string, List<string>>? groups = null) =>
        Report(s, groups).Where(g => g.Status < GlyphStatus.Verified).SelectMany(g => g.Gids.Select(id => (id, g.Key))).ToDictionary();

    /// <summary>the line's Japanese can be sent as text: read from its picture / typed, or every character trusted</summary>
    public static bool TextSafe(Line l, HashSet<string> trusted) => Trusted(l) || l.Glyphs.All(trusted.Contains);

    /// <summary>projects made before line sources were recorded: translated lines had their Japanese
    /// transcribed from the picture by the translate step, so they count as picture-read</summary>
    public static bool MarkLegacySources(Script s)
    {
        if (s.Lines.Any(l => l.JaSource != null)) return false;
        foreach (var l in s.Lines)
            if (l.Glyphs.Count > 0 && l.En.Length > 0 && l.Ja.Length > 0) l.JaSource = "pic";
        return true;
    }
}
