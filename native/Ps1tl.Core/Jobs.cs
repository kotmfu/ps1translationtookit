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

    /// <summary>one glyph, white fill / grey outline on black (glyph grid tiles)</summary>
    public static byte[] RenderGlyph(GlyphEntry e, int scale)
    {
        var im = new Raster(16, 16, Raster.Rgb(0, 0, 0));
        var px = Yuuyami.GlyphPixels(e);
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
                if (px[y, x] != 15) im.Set(x, y, px[y, x] >= 6 ? Raster.Rgb(255, 255, 255) : Raster.Rgb(110, 110, 110));
        return im.Scale(scale).Png();
    }

    /// <summary>row width as the game measures it: each glyph's width + 1</summary>
    public static int LineWidth(Line l, Dictionary<string, GlyphEntry> glyphs) =>
        l.Glyphs.Count == 0 ? l.Ja.Replace("\n", "").Length * 12 : l.Glyphs.Sum(g => glyphs[g].W + 1);   // text lines: 12px full-width characters

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
            GlyphTable.Learn(script);
            int filled = GlyphTable.Apply(script);
            save();
            log($"{script.Chars.Count}/{script.Glyphs.Count} glyphs labelled, {filled} more lines got Japanese text");
        });
        if (stop()) log("stopped");
    }

    // --- dialogue translation ---------------------------------------------------------------------------
    // A line goes as text when every character of it is verified/confirmed (or its Japanese was read from its
    // picture / typed by a person); otherwise the picture of the line is sent so Claude reads the characters itself.
    const int Batch = 20;       // picture lines per request (keeps the image small enough not to be downscaled)
    const int TextBatch = 60;   // text lines per request; Llm.Workers requests run at once
    const string TlSchema = """{"type":"object","properties":{"lines":{"type":"array","items":{"type":"object","properties":{"n":{"type":"integer"},"ja":{"type":"string"},"en":{"type":"string"}},"required":["n","ja","en"],"additionalProperties":false}}},"required":["lines"],"additionalProperties":false}""";
    const string TxtSchema = """{"type":"object","properties":{"lines":{"type":"array","items":{"type":"object","properties":{"n":{"type":"integer"},"en":{"type":"string"}},"required":["n","en"],"additionalProperties":false}}},"required":["lines"],"additionalProperties":false}""";
    const string JaSchema = """{"type":"object","properties":{"lines":{"type":"array","items":{"type":"object","properties":{"n":{"type":"integer"},"ja":{"type":"string"}},"required":["n","ja"],"additionalProperties":false}}},"required":["lines"],"additionalProperties":false}""";
    const string TlIntro = "You are translating the Japanese PS1 game \"{0}\" into natural English for a fan patch.\n";
    const string TlRules = """
          Keep it short: each line is one row of a narrow text box; aim for at most ~1.6x the Japanese
          character count in English letters. Consecutive lines are often one sentence split over two rows: translate
          them so each row reads naturally on its own. Render a trailing ・・ as "..". Keep names in Hepburn romanization.
        Use only ASCII letters, digits and . , ! ? ' " - : ; ( ) ~ & / + %.
        Lines appear roughly in story order; earlier context is given when available.
        {1}
        """;
    const string TlSystem = TlIntro + """
        You get an image of numbered dialogue lines rendered in the game's font. For each line number:
        - ja: transcribe the Japanese exactly ("・・" is the game's ellipsis; keep it as ・・).
        - en: translate it.
        """ + "\n" + TlRules;
    const string TxtSystem = TlIntro + """
        You get numbered Japanese dialogue lines as text ("・・" is the game's ellipsis). For each line number:
        - en: translate it.
        """ + "\n" + TlRules;
    const string JaSystem = """
        You transcribe Japanese text from a PS1 game. You get an image of numbered dialogue lines rendered in the
        game's font. For each line number give ja: the Japanese exactly as shown, one character per drawn character
        (small kana like っゃゅょ are drawn smaller; "・・" is the game's ellipsis; use full-width punctuation).
        """;

    static string Glossary(string g) => g.Length > 0 ? $"Glossary (use these):\n{g}" : "";

    static string Listing(IEnumerable<Line> b) =>
        string.Join("\n", b.Select((l, i) => $"{i + 1}. {JsonSerializer.Serialize(l.Ja, ScriptJson.Relaxed.String)}"));

    /// <summary>drawn characters per line, so a picture reading lines up one-to-one with the glyphs (and can vote)</summary>
    static string Counts(Line[] b) =>
        "\nCharacters drawn per line (each drawn character, including each ・ and punctuation mark, is one): " +
        string.Join(", ", b.Select((l, i) => $"{i + 1}: {l.Glyphs.Count}"));

    static string Context(IEnumerable<Line> lines) =>
        string.Join("\n", lines.Select(l => $"{l.Ja} => {l.En}"));

    /// <summary>one request for a batch of dialogue lines, as text or as a picture</summary>
    static JsonElement AskLines(Script script, Line[] b, bool text, string context, string glossary, string model)
    {
        var pre = context.Length > 0 ? $"Previous lines for context:\n{context}\n\n" : "";
        return text
            ? Llm.Ask(null, pre + $"The lines:\n{Listing(b)}\n\nTranslate lines 1-{b.Length}.", TxtSchema, string.Format(TxtSystem, script.Game, Glossary(glossary)), model)
            : Llm.Ask(RenderJapanese(b, script.Glyphs), pre + $"Transcribe and translate lines 1-{b.Length}." + Counts(b), TlSchema, string.Format(TlSystem, script.Game, Glossary(glossary)), model);
    }

    /// <summary>store a batch's answer; picture answers also carry the Japanese, which teaches the character table</summary>
    static void ApplyLines(Script script, Line[] b, bool text, JsonElement r)
    {
        foreach (var item in r.GetProperty("lines").EnumerateArray())
        {
            int n = item.GetProperty("n").GetInt32();
            if (n < 1 || n > b.Length) continue;
            if (!text)
            {
                b[n - 1].Ja = item.GetProperty("ja").GetString() ?? "";
                b[n - 1].JaSource = "pic";   // read from the whole-line picture: teaches the glyph table
            }
            b[n - 1].En = item.GetProperty("en").GetString() ?? "";
        }
        if (!text) { GlyphTable.Learn(script); GlyphTable.Apply(script); }   // picture readings fix the glyph table, which fixes other lines
    }

    // --- whole-scene translation ------------------------------------------------------------------------
    // A scene file is sent in order with its pages marked, so a sentence that runs over two rows is translated as
    // one sentence and then divided over its rows (Claude sees why the Japanese breaks where it does).
    const string SceneRules = """
        The rows come in snippets; each snippet's rows are from one scene, in order, with earlier lines of that scene
        for context (already translated: do not translate those). Rows are grouped into pages: a page is shown on
        screen at once, its rows top to
        bottom, and "+" after a row means the speech continues on the next row of the same page. For each page,
        understand it as a whole (a sentence often runs across its rows) and translate it naturally, then divide the
        English over the page's rows in the same order as the Japanese, at word boundaries (never split a word over two
        rows). A row whose Japanese starts a new sentence or a new speaker starts a new English sentence.
        {2}
        A row with ／ is a choice menu: translate each option briefly and keep them separated by " / ".
        Render a trailing ・・ as "..". Keep names in Hepburn romanization.
        Use only ASCII letters, digits and . , ! ? ' " - : ; ( ) ~ & / + %.
        {1}
        """;
    const string SceneTextSystem = TlIntro + "You get the rows as Japanese text (\"・・\" is the game's ellipsis). For each row number give en.\n" + SceneRules;
    const string ScenePicSystem = TlIntro + "You get an image of the numbered rows in the game's font and the page structure. For each row number:\n" +
        "- ja: transcribe the Japanese exactly (\"・・\" is the game's ellipsis; keep it as ・・).\n- en: the English for that row.\n" + SceneRules;

    /// <summary>a stretch of one scene to translate: whole pages, plus the scene's lines just before it as context</summary>
    sealed record Snippet(List<(Line Line, bool More)> Rows, List<Line> Context);

    /// <summary>the snippets of a request for the prompt, rows numbered across the request
    /// (row text omitted for picture requests: the image carries it)</summary>
    static string Snippets(List<Snippet> snips, bool withText)
    {
        var sb = new System.Text.StringBuilder();
        int n = 0;
        for (int k = 0; k < snips.Count; k++)
        {
            sb.Append($"snippet {k + 1}\n");
            if (snips[k].Context.Count > 0)
            {
                sb.Append("context:\n");
                foreach (var c in snips[k].Context)
                    sb.Append("  ").Append(c.Ja).Append(c.En.Length > 0 ? " => " + c.En : "").Append('\n');
            }
            var rows = snips[k].Rows;
            for (int i = 0; i < rows.Count; i++)
            {
                if (i == 0 || !rows[i - 1].More) sb.Append("page:\n");
                sb.Append($"  {++n}.");
                if (withText) sb.Append(' ').Append(JsonSerializer.Serialize(rows[i].Line.Ja, ScriptJson.Relaxed.String));
                sb.Append(rows[i].More ? " +\n" : "\n");
            }
        }
        return sb.ToString();
    }

    /// <summary>translate with scene context. redo: also rows already translated row by row (not those done by scene).
    /// Rows to do are cut into snippets (their pages + a few earlier lines of the scene), and snippets from several
    /// scenes share a request, so scattered rows don't each cost a request. limit: rows to translate.</summary>
    public static void TranslateScenes(Script script, List<Scene> scenes, int? limit, bool redo, string glossary,
        Action<string> log, Action save, Func<bool> stop, string model = "opus", string boxRule = "")
    {
        const int ContextRows = 6;
        var byKey = script.Lines.Where(l => !l.IsImage).ToDictionary(l => l.Key);
        var shared = Tfile.SharedEn(script);
        bool Needs(Line l) => redo ? l.Tl != "scene" : Tfile.EffectiveEn(l, shared).Length == 0;
        var trusted = GlyphTable.TrustedGids(script);
        var snips = new List<Snippet>();
        var queued = new HashSet<Line>();
        int todo = 0;
        foreach (var sc in scenes)
        {
            if (todo >= (limit ?? int.MaxValue)) break;
            var rows = sc.Rows.Where(r => byKey.ContainsKey(r.Key)).Select(r => (Line: byKey[r.Key], r.More)).ToList();
            var cur = new List<(Line Line, bool More)>();
            int curAt = 0;
            void Flush()
            {
                if (cur.Count > 0)
                {
                    snips.Add(new Snippet(cur, rows.Take(curAt).TakeLast(ContextRows).Select(r => r.Line).ToList()));
                    foreach (var r in cur) if (Needs(r.Line) && queued.Add(r.Line)) todo++;
                }
                cur = new();
            }
            // runs of consecutive pages that have rows to do, at most Batch rows each
            for (int i = 0; i < rows.Count;)
            {
                int j = i;
                while (j < rows.Count - 1 && rows[j].More) j++;
                var page = rows.GetRange(i, j - i + 1);
                if (page.Any(r => Needs(r.Line) && !queued.Contains(r.Line)) && todo < (limit ?? int.MaxValue))
                {
                    if (cur.Count + page.Count > Batch) Flush();
                    if (cur.Count == 0) curAt = i;
                    cur.AddRange(page);
                }
                else Flush();
                i = j + 1;
            }
            Flush();
        }
        // pack snippets into requests: up to TextBatch rows when all can go as text, else Batch rows as a picture
        var chunks = new List<(List<Snippet> Snips, bool Text)>();
        foreach (var text in new[] { true, false })
        {
            var cur = new List<Snippet>();
            int max = text ? TextBatch : Batch;
            foreach (var sn in snips.Where(sn => sn.Rows.All(r => GlyphTable.TextSafe(r.Line, trusted)) == text))
            {
                if (cur.Count > 0 && cur.Sum(c => c.Rows.Count) + sn.Rows.Count > max) { chunks.Add((cur, text)); cur = new(); }
                cur.Add(sn);
            }
            if (cur.Count > 0) chunks.Add((cur, text));
        }
        log($"translating {todo} rows by scene with {model} via {Llm.Backend() ?? "nothing"}: {snips.Count} snippets in {chunks.Count} requests " +
            $"({chunks.Count(c => c.Text)} as text, {chunks.Count(c => !c.Text)} as pictures), {Llm.Workers} at a time");
        var tasks = chunks.Select(c => (Func<JsonElement>)(() =>
        {
            var lines = c.Snips.SelectMany(sn => sn.Rows).Select(r => r.Line).ToArray();
            return c.Text
                ? Llm.Ask(null, $"{Snippets(c.Snips, true)}\nTranslate rows 1-{lines.Length}.", TxtSchema,
                          string.Format(SceneTextSystem, script.Game, Glossary(glossary), boxRule), model)
                : Llm.Ask(RenderJapanese(lines, script.Glyphs), $"{Snippets(c.Snips, false)}\nTranscribe and translate rows 1-{lines.Length}." + Counts(lines),
                          TlSchema, string.Format(ScenePicSystem, script.Game, Glossary(glossary), boxRule), model);
        })).ToList();
        int finished = 0;
        Llm.RunParallel(tasks, stop, (k, r) =>
        {
            if (r is LlmException e) { log($"request {k + 1} failed: {e.Message}"); return; }
            var c = chunks[k];
            var rows = c.Snips.SelectMany(sn => sn.Rows).ToList();
            foreach (var item in ((JsonElement)r).GetProperty("lines").EnumerateArray())
            {
                int n = item.GetProperty("n").GetInt32();
                if (n < 1 || n > rows.Count) continue;
                var l = rows[n - 1].Line;
                if (!c.Text && l.JaSource != "user")
                {
                    l.Ja = item.GetProperty("ja").GetString() ?? "";
                    l.JaSource = "pic";
                }
                if (!Needs(l)) continue;   // a row of the page that is already done: it was there for its sentence
                l.En = (item.GetProperty("en").GetString() ?? "").Trim();
                l.Tl = "scene";
            }
            if (!c.Text) { GlyphTable.Learn(script); GlyphTable.Apply(script); }
            save();
            finished++;
            log($"request {finished}/{chunks.Count} done ({(c.Text ? "text" : "picture")}) · {script.Lines.Count(l => l.Tl == "scene"):N0} rows translated by scene");
        });
        if (stop()) log("stopped");
    }

    /// <summary>ask Claude to read lines that contain characters still in doubt (from their pictures, no translation).
    /// Each answer becomes a picture reading that votes in the character table.</summary>
    public static void VerifyCharacters(Script script, int? limit, Action<string> log, Action save, Func<bool> stop, string model = "opus")
    {
        var doubt = GlyphTable.DoubtfulGids(script);
        // greedy cover: lines with the most doubtful shapes first, each line must add a shape not yet covered
        var covered = new HashSet<string>();
        var todo = new List<Line>();
        var cands = script.Lines.Where(l => l.Glyphs.Count > 0 && l.JaSource == null)
            .Select(l => (l, shapes: l.Glyphs.Where(doubt.ContainsKey).Select(g => doubt[g]).ToHashSet()))
            .Where(x => x.shapes.Count > 0).OrderByDescending(x => x.shapes.Count);
        foreach (var (l, shapes) in cands)
        {
            if (todo.Count >= (limit ?? int.MaxValue)) break;
            if (shapes.IsSubsetOf(covered)) continue;
            covered.UnionWith(shapes); todo.Add(l);
        }
        int shapesTotal = doubt.Values.Distinct().Count();
        var batches = todo.Chunk(Batch).ToList();
        log($"checking {shapesTotal:N0} doubtful characters: reading {todo.Count} lines that contain {covered.Count:N0} of them " +
            $"with {model} via {Llm.Backend() ?? "nothing"}: {batches.Count} requests, {Llm.Workers} at a time");
        var tasks = batches.Select(b => (Func<JsonElement>)(() =>
            Llm.Ask(RenderJapanese(b, script.Glyphs), $"Transcribe lines 1-{b.Length}." + Counts(b), JaSchema, JaSystem, model, "medium"))).ToList();
        int finished = 0;
        Llm.RunParallel(tasks, stop, (k, r) =>
        {
            if (r is LlmException e) { log($"request {k + 1} failed: {e.Message}"); return; }
            var b = batches[k];
            foreach (var item in ((JsonElement)r).GetProperty("lines").EnumerateArray())
            {
                int n = item.GetProperty("n").GetInt32();
                if (n < 1 || n > b.Length) continue;
                b[n - 1].Ja = item.GetProperty("ja").GetString() ?? "";
                b[n - 1].JaSource = "pic";
            }
            GlyphTable.Learn(script); GlyphTable.Apply(script);
            save();
            finished++;
            var st = GlyphTable.Report(script).GroupBy(g => g.Status).ToDictionary(g => g.Key, g => g.Count());
            log($"request {finished}/{batches.Count} done · characters: {st.GetValueOrDefault(GlyphStatus.Verified) + st.GetValueOrDefault(GlyphStatus.Confirmed):N0} verified/confirmed, " +
                $"{st.GetValueOrDefault(GlyphStatus.Unverified):N0} not verified, {st.GetValueOrDefault(GlyphStatus.Disagree)} disagree");
        });
        if (stop()) log("stopped");
    }

    /// <summary>translate one line (dialogue or picture), as text or from its picture; overwrites its English</summary>
    public static void TranslateOne(Script script, Dictionary<string, byte[]> files, int i, bool byText, string glossary, Action<string> log, Action save, string model = "opus")
    {
        var l = script.Lines[i];
        log($"translating line #{i} {(byText ? "as text" : "from its picture")} with {model} via {Llm.Backend() ?? "nothing"}");
        if (l.IsImage)
        {
            var r = AskPictures(script, files, [l], glossary, model, withPicture: !byText);
            ApplyPictures([l], r);
        }
        else
        {
            var context = Context(script.Lines.Take(i).Where(x => !x.IsImage && x.En.Length > 0).TakeLast(15));
            ApplyLines(script, [l], byText, AskLines(script, [l], byText, context, glossary, model));
        }
        save();
        log($"#{i}: {l.En}");
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
            Llm.Ask(Images.Sheet(keys.Select(k => Images.Pixels(Games.Of(script), files, imgs[k])).ToList()), string.Format(ReadPrompt, keys.Length), ReadSchema, model: model, effort: "medium"))).ToList();
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

    /// <summary>one request for picture texts: the Japanese with each box's letter budget, plus the pictures unless text-only</summary>
    static JsonElement AskPictures(Script script, Dictionary<string, byte[]> files, Line[] b, string glossary, string model, bool withPicture)
    {
        var imgs = script.Images!;
        var listing = string.Join("\n", b.Select((l, i) =>
            $"{i + 1}. {JsonSerializer.Serialize(l.Ja, ScriptJson.Relaxed.String)} - box {imgs[l.Image!].W}x{imgs[l.Image!].H} px, at most {Images.Budget(imgs[l.Image!])} letters"));
        var png = withPicture ? Images.Sheet(b.Select(l => Images.Pixels(Games.Of(script), files, imgs[l.Image!])).ToList()) : null;
        return Llm.Ask(png, $"The numbered {(withPicture ? "pictures and their " : "")}Japanese text:\n{listing}\n\nTranslate items 1-{b.Length}.",
                       PicSchema, string.Format(PicSystem, script.Game, Glossary(glossary)), model);
    }

    static void ApplyPictures(Line[] b, JsonElement r)
    {
        foreach (var item in r.GetProperty("lines").EnumerateArray())
        {
            int n = item.GetProperty("n").GetInt32();
            if (n >= 1 && n <= b.Length) b[n - 1].En = (item.GetProperty("en").GetString() ?? "").Trim();
        }
    }

    public static void TranslatePictures(Script script, Dictionary<string, byte[]> files, int? limit, string glossary, Action<string> log, Action save, Func<bool> stop, string model = "opus")
    {
        var shared = Tfile.SharedEn(script);
        var todo = script.Lines.Where(l => l.IsImage && Tfile.EffectiveEn(l, shared).Length == 0).Take(limit ?? int.MaxValue).ToList();
        var batches = todo.Chunk(PicsPerSheet).ToList();
        log($"translating {todo.Count} picture texts with {model}: {batches.Count} requests, {Llm.Workers} at a time");
        var tasks = batches.Select(b => (Func<JsonElement>)(() => AskPictures(script, files, b, glossary, model, withPicture: true))).ToList();
        int finished = 0;
        Llm.RunParallel(tasks, stop, (k, r) =>
        {
            if (r is LlmException e) { log($"request {k + 1} failed: {e.Message}"); return; }
            var batch = batches[k];
            ApplyPictures(batch, (JsonElement)r);
            save();
            finished += batch.Length;
            log($"{finished}/{todo.Count} picture texts translated");
        });
        if (stop()) log("stopped");
    }
}
