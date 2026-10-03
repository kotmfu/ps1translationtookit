using System.Text.Json;

namespace Ps1tl;

/// <summary>Claude-backed jobs: read glyphs (OCR), translate dialogue, find and translate picture text.
/// Each runs Llm.Workers requests at a time, applies results as they arrive and saves after each one.</summary>
public static class Jobs
{
    // --- rendering ----------------------------------------------------------------------------------------
    /// <summary>Japanese lines in the game's own glyphs, white-on-black like the game</summary>
    public static byte[] RenderJapanese(IList<Line> lines, Dictionary<string, GlyphEntry> glyphs, bool numbered = true, int scale = 2)
    {
        int pad = numbered ? 44 : 4, rowH = 20;
        int width = pad + Math.Max(16, lines.Count == 0 ? 0 : lines.Max(l => l.Glyphs.Sum(g => glyphs[g].W + 2))) + 8;
        var im = new Raster(width, rowH * lines.Count + 4, Raster.Rgb(0, 0, 0));
        for (int r = 0; r < lines.Count; r++)
        {
            int y = r * rowH + 2;
            if (numbered) im.Text(2, y + 5, (r + 1).ToString().PadLeft(3), Raster.Rgb(160, 160, 160), Font.Load());
            int x = pad;
            foreach (var g in lines[r].Glyphs)
            {
                var e = glyphs[g];
                var px = Yuuyami.GlyphPixels(e);
                for (int gy = 0; gy < 16; gy++)
                    for (int gx = 0; gx < 16; gx++)
                    {
                        int v = px[gy, gx];
                        if (v != 15) im.Set(x + gx - e.X, y + gy, v >= 6 ? Raster.Rgb(255, 255, 255) : Raster.Rgb(90, 90, 90));
                    }
                x += e.W + 2;
            }
        }
        return im.Scale(scale).Png();
    }

    /// <summary>English preview in the built-in dialogue font, as it will look in game</summary>
    public static byte[] RenderEnglish(string text, Font font, int scale = 2)
    {
        int w = 4 + text.Sum(c => font.Width(c) + 2) + 8;
        var im = new Raster(w, 24, Raster.Rgb(0, 0, 0));
        int x = 4;
        foreach (var c in text)
        {
            var (bm, cx, cw) = Font.ToCell(font[c]);
            for (int i = 0; i < 256; i++)
            {
                int v = i % 2 == 0 ? bm[i / 2] & 15 : bm[i / 2] >> 4;
                if (v != 15) im.Set(x + i % 16 - cx, 2 + i / 16, v >= 6 ? Raster.Rgb(255, 255, 255) : Raster.Rgb(90, 90, 90));
            }
            x += cw;
        }
        return im.Scale(scale).Png();
    }

    public static int LineWidth(Line l, Dictionary<string, GlyphEntry> glyphs) => l.Glyphs.Sum(g => glyphs[g].W + 2);

    // --- OCR ----------------------------------------------------------------------------------------------
    const int PerSheet = 100, Cols = 10, CellScale = 3;
    const string OcrSchema = """{"type":"object","properties":{"glyphs":{"type":"array","items":{"type":"object","properties":{"n":{"type":"integer"},"ch":{"type":"string"}},"required":["n","ch"],"additionalProperties":false}}},"required":["glyphs"],"additionalProperties":false}""";
    const string OcrPrompt = "Each numbered cell shows one character from a Japanese game font (hiragana, katakana, kanji, " +
        "punctuation such as 、。・！？ー～「」, digits or letters; small kana like っゃゅょ are drawn smaller). " +
        "For every number 1-{0}, give the single character it shows. Use full-width forms for punctuation.";

    static byte[] GlyphSheet(IList<string> gids, Dictionary<string, GlyphEntry> glyphs)
    {
        int cell = 16 * CellScale + 14, rows = (gids.Count + Cols - 1) / Cols;
        var im = new Raster(Cols * cell, rows * cell, Raster.Rgb(0, 0, 0));
        for (int k = 0; k < gids.Count; k++)
        {
            int x0 = k % Cols * cell, y0 = k / Cols * cell;
            im.Text(x0 + 2, y0 + 2, (k + 1).ToString(), Raster.Rgb(140, 140, 140));
            var px = Yuuyami.GlyphPixels(glyphs[gids[k]]);
            for (int y = 0; y < 16; y++)
                for (int x = 0; x < 16; x++)
                    if (px[y, x] != 15)
                        im.FillRect(x0 + 12 + x * CellScale, y0 + 12 + y * CellScale, CellScale, CellScale,
                                    px[y, x] >= 6 ? Raster.Rgb(255, 255, 255) : Raster.Rgb(90, 90, 90));
        }
        return im.Png();
    }

    /// <summary>fill each line's ja from the glyph table -> number of lines filled</summary>
    public static int ApplyChars(Script script, bool overwrite = false)
    {
        var chars = script.Chars ?? new();
        int n = 0;
        foreach (var l in script.Lines)
            if (l.Glyphs.Count > 0 && (overwrite || l.Ja.Length == 0) && l.Glyphs.All(chars.ContainsKey))
            { l.Ja = string.Concat(l.Glyphs.Select(g => chars[g])); n++; }
        return n;
    }

    /// <summary>vote glyph -> char from lines whose ja has exactly one char per glyph</summary>
    public static int Learn(Script script)
    {
        var votes = new Dictionary<string, Dictionary<string, int>>();
        foreach (var l in script.Lines)
        {
            var ja = l.Ja.EnumerateRunes().Select(r => r.ToString()).ToList();
            if (ja.Count == 0 || ja.Count != l.Glyphs.Count) continue;
            for (int i = 0; i < ja.Count; i++)
            {
                if (!votes.TryGetValue(l.Glyphs[i], out var v)) votes[l.Glyphs[i]] = v = new();
                v[ja[i]] = v.GetValueOrDefault(ja[i]) + 1;
            }
        }
        script.Chars ??= new();
        foreach (var (g, v) in votes) script.Chars[g] = v.MaxBy(kv => kv.Value).Key;   // first-seen wins ties, like Counter
        return votes.Count;
    }

    public static void LabelGlyphs(Script script, Action<string> log, Action save, Func<bool> stop, string model = "opus")
    {
        script.Chars ??= new();
        var todo = script.Glyphs.Keys.Where(g => !script.Chars.ContainsKey(g)).ToList();
        var sheets = todo.Chunk(PerSheet).ToList();
        log($"labelling {todo.Count} glyphs with {model} via {Llm.Backend() ?? "nothing"}: {sheets.Count} requests, {Llm.Workers} at a time");
        var tasks = sheets.Select(g => (Func<JsonElement>)(() =>
            Llm.Ask(GlyphSheet(g, script.Glyphs), string.Format(OcrPrompt, g.Length), OcrSchema, model: model, effort: "medium"))).ToList();
        Llm.RunParallel(tasks, stop, (k, r) =>
        {
            if (r is LlmException e) { log($"request {k + 1} failed: {e.Message}"); return; }
            var gids = sheets[k];
            foreach (var item in ((JsonElement)r).GetProperty("glyphs").EnumerateArray())
            {
                int n = item.GetProperty("n").GetInt32();
                var ch = item.GetProperty("ch").GetString() ?? "";
                if (n >= 1 && n <= gids.Length && ch.EnumerateRunes().Count() == 1) script.Chars[gids[n - 1]] = ch;
            }
            int filled = ApplyChars(script);
            save();
            log($"{script.Chars.Count}/{script.Glyphs.Count} glyphs labelled, {filled} more lines got Japanese text");
        });
        if (stop()) log("stopped");
    }

    // --- dialogue translation ---------------------------------------------------------------------------
    const int Batch = 20;   // lines per request; Llm.Workers requests run at once
    const string TlSchema = """{"type":"object","properties":{"lines":{"type":"array","items":{"type":"object","properties":{"n":{"type":"integer"},"ja":{"type":"string"},"en":{"type":"string"}},"required":["n","ja","en"],"additionalProperties":false}}},"required":["lines"],"additionalProperties":false}""";
    const string TlSystem = """
        You are translating the Japanese PS1 horror/adventure game "{0}" into natural English for a fan patch.
        You get an image of numbered dialogue lines rendered in the game's font. For each line number:
        - ja: transcribe the Japanese exactly ("・・" is the game's ellipsis; keep it as ・・).
        - en: translate it. Keep it short: each line is one row of a narrow text box; aim for at most ~1.6x the Japanese
          character count in English letters. Consecutive lines are often one sentence split over two rows: translate
          them so each row reads naturally on its own. Render a trailing ・・ as "..". Keep names in Hepburn romanization.
        Use only ASCII letters, digits and . , ! ? ' " - : ; ( ) ~ & / + %.
        Lines appear roughly in story order; earlier context is given when available.
        {1}
        """;

    static string Glossary(string g) => g.Length > 0 ? $"Glossary (use these):\n{g}" : "";

    public static void TranslateLines(Script script, int? limit, string glossary, Action<string> log, Action save, Func<bool> stop, string model = "opus")
    {
        var shared = Tfile.SharedEn(script);
        var todo = script.Lines.Where(l => l.Glyphs.Count > 0 && Tfile.EffectiveEn(l, shared).Length == 0).Take(limit ?? int.MaxValue).ToList();
        var system = string.Format(TlSystem, script.Game, Glossary(glossary));
        var done = script.Lines.Where(l => l.En.Length > 0).ToList();
        var batches = todo.Chunk(Batch).ToList();
        log($"translating {todo.Count} lines with {model} via {Llm.Backend() ?? "nothing"}: {batches.Count} requests, {Llm.Workers} at a time (each takes about a minute)");
        var tasks = batches.Select(b => (Func<JsonElement>)(() =>
        {
            string context;
            lock (done) context = string.Join("\n", done.TakeLast(15).Select(l => $"{l.Ja} => {l.En}"));
            var prompt = (context.Length > 0 ? $"Previous lines for context:\n{context}\n\n" : "") + $"Transcribe and translate lines 1-{b.Length}.";
            return Llm.Ask(RenderJapanese(b, script.Glyphs), prompt, TlSchema, system, model);
        })).ToList();
        int finished = 0;
        Llm.RunParallel(tasks, stop, (k, r) =>
        {
            if (r is LlmException e) { log($"request {k + 1} failed: {e.Message}"); return; }
            var batch = batches[k];
            foreach (var item in ((JsonElement)r).GetProperty("lines").EnumerateArray())
            {
                int n = item.GetProperty("n").GetInt32();
                if (n < 1 || n > batch.Length) continue;
                batch[n - 1].Ja = item.GetProperty("ja").GetString() ?? "";
                batch[n - 1].En = item.GetProperty("en").GetString() ?? "";
            }
            lock (done) done.AddRange(batch.Where(l => l.En.Length > 0));
            Learn(script); ApplyChars(script);   // transcriptions teach the glyph table, which fills other lines
            save();
            finished++;
            log($"request {finished}/{batches.Count} done · {script.Lines.Count(l => l.En.Length > 0)} lines translated in total");
        });
        if (stop()) log("stopped");
    }

    // --- pictures -------------------------------------------------------------------------------------------
    const int PicsPerSheet = 30;
    const string ReadSchema = """{"type":"object","properties":{"images":{"type":"array","items":{"type":"object","properties":{"n":{"type":"integer"},"ja":{"type":"string"}},"required":["n","ja"],"additionalProperties":false}}},"required":["images"],"additionalProperties":false}""";
    const string ReadPrompt = "Each numbered row shows one picture from a Japanese PS1 game, twice (on black and on white). " +
        "For every number 1-{0}: if the picture contains Japanese text (menu labels, buttons, place names, " +
        "messages, single kanji such as 月 or 日), give that text exactly as ja, with a line break between " +
        "lines of text. Otherwise (icons, people, photos, maps, only digits or Latin letters) give ja = \"\".";
    const string PicSchema = """{"type":"object","properties":{"lines":{"type":"array","items":{"type":"object","properties":{"n":{"type":"integer"},"en":{"type":"string"}},"required":["n","en"],"additionalProperties":false}}},"required":["lines"],"additionalProperties":false}""";
    const string PicSystem = """
        You translate menu and interface text of the Japanese PS1 game "{0}" into English for a fan patch.
        Each item is text drawn inside a small picture of fixed size; the English is redrawn in the same box with a
        pixel font, so it MUST be short: stay within the given letter budget (abbreviate if needed, e.g. "Rumor", "Opt.").
        Single kanji used in dates (月 日 and weekdays) become short forms such as "/" or "Mon". Keep names in Hepburn.
        Use only ASCII letters, digits and . , ! ? ' " - : ; ( ) ~ & / + %.
        {1}
        """;

    /// <summary>ask Claude which pictures hold Japanese text; those become lines</summary>
    public static void ReadPictures(Script script, Dictionary<string, byte[]> files, Action<string> log, Action save, Func<bool> stop, string model = "opus")
    {
        script.Images ??= new();
        var imgs = script.Images;
        var todo = imgs.Where(kv => !kv.Value.Checked && kv.Value.H <= Images.MaxH).Select(kv => kv.Key).ToList();
        var have = script.Lines.Select(l => l.Key).ToHashSet();
        var groups = todo.Chunk(PicsPerSheet).ToList();
        log($"reading {todo.Count} pictures with {model} via {Llm.Backend() ?? "nothing"}: {groups.Count} requests, {Llm.Workers} at a time");
        var tasks = groups.Select(keys => (Func<JsonElement>)(() =>
            Llm.Ask(Images.Sheet(keys.Select(k => Images.Pixels(files, imgs[k])).ToList()), string.Format(ReadPrompt, keys.Length), ReadSchema, model: model, effort: "medium"))).ToList();
        int checkedN = 0;
        Llm.RunParallel(tasks, stop, (g, r) =>
        {
            if (r is LlmException e) { log($"request {g + 1} failed: {e.Message}"); return; }
            var keys = groups[g];
            var found = new Dictionary<int, string>();
            foreach (var item in ((JsonElement)r).GetProperty("images").EnumerateArray())
                found[item.GetProperty("n").GetInt32()] = (item.GetProperty("ja").GetString() ?? "").Trim();
            for (int n = 1; n <= keys.Length; n++)
            {
                var k = keys[n - 1];
                imgs[k].Checked = true;
                var ja = found.GetValueOrDefault(n, "");
                if (ja.Length > 0 && have.Add(Images.Prefix + k))
                    script.Lines.Add(new Line { Key = Images.Prefix + k, Image = k, Refs = imgs[k].Refs, Ja = ja });
            }
            save();
            checkedN += keys.Length;
            log($"{checkedN}/{todo.Count} pictures checked, {script.Lines.Count(l => l.IsImage)} with text");
        });
        if (stop()) log("stopped");
    }

    public static void TranslatePictures(Script script, Dictionary<string, byte[]> files, int? limit, string glossary, Action<string> log, Action save, Func<bool> stop, string model = "opus")
    {
        var shared = Tfile.SharedEn(script);
        var imgs = script.Images!;
        var todo = script.Lines.Where(l => l.IsImage && Tfile.EffectiveEn(l, shared).Length == 0).Take(limit ?? int.MaxValue).ToList();
        var system = string.Format(PicSystem, script.Game, Glossary(glossary));
        var batches = todo.Chunk(PicsPerSheet).ToList();
        log($"translating {todo.Count} picture texts with {model}: {batches.Count} requests, {Llm.Workers} at a time");
        var tasks = batches.Select(b => (Func<JsonElement>)(() =>
        {
            var listing = string.Join("\n", b.Select((l, i) =>
                $"{i + 1}. {JsonSerializer.Serialize(l.Ja, ScriptJson.Relaxed.String)} - box {imgs[l.Image!].W}x{imgs[l.Image!].H} px, at most {Images.Budget(imgs[l.Image!])} letters"));
            return Llm.Ask(Images.Sheet(b.Select(l => Images.Pixels(files, imgs[l.Image!])).ToList()),
                $"The numbered pictures and their Japanese text:\n{listing}\n\nTranslate items 1-{b.Length}.", PicSchema, system, model);
        })).ToList();
        int finished = 0;
        Llm.RunParallel(tasks, stop, (k, r) =>
        {
            if (r is LlmException e) { log($"request {k + 1} failed: {e.Message}"); return; }
            var batch = batches[k];
            foreach (var item in ((JsonElement)r).GetProperty("lines").EnumerateArray())
            {
                int n = item.GetProperty("n").GetInt32();
                if (n >= 1 && n <= batch.Length) batch[n - 1].En = (item.GetProperty("en").GetString() ?? "").Trim();
            }
            save();
            finished += batch.Length;
            log($"{finished}/{todo.Count} picture texts translated");
        });
        if (stop()) log("stopped");
    }
}
