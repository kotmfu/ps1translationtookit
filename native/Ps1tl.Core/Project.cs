using System.Text.RegularExpressions;

namespace Ps1tl;

/// <summary>A running background task: its log, Claude's live output per request, and the result.</summary>
public sealed class Job
{
    public string Name { get; init; } = "";
    public DateTime Started { get; } = DateTime.Now;
    public List<string> Log { get; } = new();
    public Dictionary<string, string> Live { get; set; } = new();   // replaced, never mutated in place
    public bool Running { get; set; } = true;
    public object? Result { get; set; }
}

public sealed record Row(int Index, string Ja, string En, string Auto, string Notes, int Refs, int JapaneseWidth, int EnglishWidth, bool Over, bool Picture);

/// <summary>An open translation project (rom + its .script.json). Saves are atomic; a rolling backup is kept in
/// backups/ next to the project. Long tasks run on a background thread; watch Job.</summary>
public sealed class Project
{
    const int BackupEverySeconds = 600, BackupsKept = 30;

    public string? Cue { get; private set; }
    public IGame? Game { get; private set; }
    public string? ScriptPath { get; private set; }
    public Script? Script { get; private set; }
    public int Budget { get; private set; }
    public Font Font { get; } = Font.Load();
    public Job? Job { get; private set; }
    public volatile bool Stop;
    readonly object saveLock = new(), liveLock = new();
    DateTime lastBackup = DateTime.MinValue;
    Dictionary<string, byte[]>? pics;
    (Func<string, byte[]> Ja, Func<string, byte[]> En)? previews;
    bool previewsRead;

    public bool IsOpen => Script != null;
    public bool Busy => Job is { Running: true };

    // --- files -------------------------------------------------------------------------------------------
    /// <summary>open a rom; extracts its text the first time (creates &lt;rom&gt;.script.json), and merges in text
    /// a newer extractor finds, keeping all existing work</summary>
    public void Open(string cue)
    {
        using var disc = new Disc(cue);
        var serial = disc.Serial();
        var game = Games.BySerial(serial) ?? throw new InvalidDataException($"unknown game {serial}; supported: {Games.Supported}");
        var path = Path.ChangeExtension(cue, ".script.json");   // next to the .cue the user opened
        bool fresh = !File.Exists(path);
        var script = fresh ? game.Extract(disc) : Script.Load(path);
        bool stale = script.Extractor != game.ExtractorVersion;
        if (stale)
        {
            if (!fresh) Merge(script, game.Extract(disc));
            script.Extractor = game.ExtractorVersion;
        }
        // projects from before line sources were recorded: re-learn the glyph table from trusted lines only
        bool relearn = !fresh && GlyphTable.MarkLegacySources(script);
        if (relearn) { GlyphTable.Learn(script); GlyphTable.Apply(script); }
        stale |= relearn;
        lock (saveLock)
        {
            (Cue, Game, ScriptPath, Script, pics, groups, scenes, sceneGroups) = (cue, game, path, script, null, null, null, null);
            Budget = game.Budget;
        }
        if (fresh || stale)
        {
            if (!fresh) Backup("before-reextract");
            Save();
        }
    }

    static void Merge(Script old, Script nw)
    {
        var byKey = old.Lines.ToDictionary(l => l.Key);
        // dialogue refs come from the new extraction only (message numbers shift when the extractor splits rows);
        // lines it no longer finds keep their text but are used nowhere. Picture lines aren't extracted here.
        foreach (var o in old.Lines) if (!o.IsImage) o.Refs = new();
        foreach (var l in nw.Lines)
        {
            if (byKey.TryGetValue(l.Key, out var o)) o.Refs = l.Refs;
            else old.Lines.Add(l);
        }
        foreach (var (k, g) in nw.Glyphs) old.Glyphs.TryAdd(k, g);
        Images.MergeCatalog(old, nw.Images ?? new());
        GlyphTable.Apply(old);   // new lines made of already-read glyphs get their Japanese straight away
    }

    /// <summary>atomic + durable write, plus a rolling backup at most every 10 minutes</summary>
    public void Save()
    {
        lock (saveLock)
        {
            Script!.Save(ScriptPath!);
            if ((DateTime.Now - lastBackup).TotalSeconds > BackupEverySeconds) Backup();
        }
    }

    /// <summary>timestamped copy in backups/ next to the project; keeps the newest 30</summary>
    public void Backup(string reason = "auto")
    {
        if (ScriptPath == null || !File.Exists(ScriptPath)) return;
        var dir = Path.Combine(Path.GetDirectoryName(ScriptPath)!, "backups");
        Directory.CreateDirectory(dir);
        var name = Path.GetFileName(ScriptPath).Replace(".script.json", "");
        File.Copy(ScriptPath, Path.Combine(dir, $"{name} {DateTime.Now:yyyy-MM-dd HHmmss} {reason}.script.json"), true);
        lastBackup = DateTime.Now;
        foreach (var old in Directory.GetFiles(dir, name + " *.script.json").OrderByDescending(File.GetLastWriteTime).Skip(BackupsKept))
            File.Delete(old);
    }

    public void ExportCsv(string path) => Tfile.Save(Script!, path);

    public (int Updated, int Unknown) ImportCsv(string path)
    {
        Backup("before-import");
        var r = Tfile.Load(Script!, path);
        Save();
        return r;
    }

    // --- lines -------------------------------------------------------------------------------------------
    /// <summary>false for dialogue lines the current extractor no longer finds in the game (kept for their text)</summary>
    public static bool InUse(Line l) => l.IsImage || l.Refs.Count > 0;
    public int InUseCount => Script?.Lines.Count(InUse) ?? 0;

    public int TranslatedCount()
    {
        var shared = Tfile.SharedEn(Script!);
        return Script!.Lines.Count(l => InUse(l) && Tfile.EffectiveEn(l, shared).Length > 0);
    }

    /// <summary>row width as the game measures it: each packed cell's width + 1 (letters the font lacks count as '?')</summary>
    public int EnglishWidth(string text) =>
        Font.Pack(new string(text.Replace('\n', ' ').Select(c => Font.Has(c) ? c : '?').ToArray())).Sum(t => Font.ToCell(Font.Join(t)).W + 1);

    /// <summary>what the diagnostic build shows in game for a ref (or a scene path)</summary>
    public string Label(string reference) => Game?.Label(reference) ?? reference;

    /// <summary>a label from a diagnostic build ('8/137/18:6', 'EV1061:8') -> line index, or null</summary>
    public int? FindLabel(string label)
    {
        label = label.Trim();
        if (label.Length < 4) return null;
        int i = Script!.Lines.FindIndex(l => l.Refs.Any(r => string.Equals(Label(r), label, StringComparison.OrdinalIgnoreCase)));
        return i >= 0 ? i : null;
    }

    /// <summary>lines the last build could not fit (they wrap in game even as two small lines)</summary>
    public HashSet<int> TooLong => Script?.TooLong is { } keys ? Script.Lines.Select((l, i) => (l, i)).Where(t => keys.Contains(t.l.Key)).Select(t => t.i).ToHashSet() : new();

    /// <summary>indexes of lines passing a filter (all/todo/done/over/pictures/toolong) and a search string.
    /// A diagnostic label ('8/137/18:6') jumps straight to that line.</summary>
    public List<int> Matches(string filter = "all", string text = "")
    {
        if (FindLabel(text) is int hit) return [hit];
        text = text.ToLowerInvariant();
        var shared = Tfile.SharedEn(Script!);
        var gameFilter = Game?.Filters.FirstOrDefault(f => f.Key == filter);
        var outp = new List<int>();
        for (int i = 0; i < Script!.Lines.Count; i++)
        {
            var l = Script.Lines[i];
            if (!InUse(l)) continue;
            var en = Tfile.EffectiveEn(l, shared);
            if (filter == "todo" && en.Length > 0) continue;
            if (filter == "done" && en.Length == 0) continue;
            if (filter == "over" && !(en.Length > 0 && Widths(l, en).Over)) continue;
            if (gameFilter != null && !gameFilter.Match(l)) continue;
            if (filter == "toolong" && Script.TooLong?.Contains(l.Key) != true) continue;
            if (text.Length > 0 && !l.Ja.ToLowerInvariant().Contains(text) && !en.ToLowerInvariant().Contains(text)
                && !(l.Notes ?? "").ToLowerInvariant().Contains(text) && !l.Key.Contains(text)
                && !l.Refs.Any(r => Label(r).ToLowerInvariant().Contains(text))) continue;   // 'EV1061' lists that scene
            outp.Add(i);
        }
        return outp;
    }

    public Row RowAt(int i, Dictionary<string, string>? shared = null)
    {
        var l = Script!.Lines[i];
        shared ??= Tfile.SharedEn(Script);
        var auto = l.En.Length > 0 ? "" : Tfile.EffectiveEn(l, shared);
        var (ew, jw, over) = Widths(l, l.En.Length > 0 ? l.En : auto);
        return new Row(i, l.Ja, l.En, auto, l.Notes ?? "", l.Refs.Count, jw, ew, over, l.IsImage);
    }

    /// <summary>-> (english px, japanese px, too long?). Pictures: their box width, and whether the English fits it.</summary>
    public (int English, int Japanese, bool Over) Widths(Line l, string en)
    {
        if (l.IsImage)
        {
            var w = Script!.Images![l.Image!].W;
            if (en.Length == 0) return (0, w, false);
            var (idx, pal) = Picture(l);
            return (EnglishWidth(en), w, !Images.Draw(idx, pal, en).Fits);
        }
        // ponytail: text games (no glyphs) measure English as 8px half-width letters; a per-game width if one differs
        int ew = l.Glyphs.Count == 0 ? en.Replace("\n", " ").Length * Gunparade.CharPx : EnglishWidth(en);
        return (ew, Jobs.LineWidth(l, Script!.Glyphs), ew > Budget);
    }

    /// <summary>picture files of the rom (read once)</summary>
    public Dictionary<string, byte[]> Pics()
    {
        if (pics == null) { using var d = new Disc(Cue!); pics = Game!.ImageFiles(d); }
        return pics;
    }

    public (byte[,] Idx, ushort[] Pal) Picture(Line l) => Images.Pixels(Game!, Pics(), Script!.Images![l.Image!]);

    /// <summary>update ja/en/notes of line i and save -> (english width px, chars missing from the font, too long?)</summary>
    public (int Width, List<char> Missing, bool Over) SetLine(int i, string? ja = null, string? en = null, string? notes = null)
    {
        var l = Script!.Lines[i];
        if (ja != null && ja.Trim() != l.Ja) { l.Ja = ja.Trim(); l.JaSource = "user"; }   // a person's reading teaches the glyph table
        if (en != null) l.En = en.Trim();
        if (notes != null) l.Notes = notes.Trim();
        Save();
        return (EnglishWidth(l.En), Font.Missing(l.En.Replace("\n", "")), Widths(l, l.En).Over);
    }

    // --- glyph grid ----------------------------------------------------------------------------------------
    Dictionary<string, List<string>>? groups;
    Dictionary<string, List<string>> Groups() => groups ??= GlyphTable.Groups(Script!);

    /// <summary>every character shape, most doubtful first</summary>
    public List<GlyphGroup> GlyphReport() => GlyphTable.Report(Script!, Groups());

    /// <summary>a person set or confirmed one shape's character -> lines whose Japanese changed</summary>
    public int SetGlyph(GlyphGroup g, string c)
    {
        int n = GlyphTable.SetChar(Script!, g.Gids, c);
        Save();
        return n;
    }

    public byte[] GlyphPng(string gid, int scale) => Jobs.RenderGlyph(Script!.Glyphs[gid], scale);

    /// <summary>indexes of lines that use any glyph of the group (first `max`)</summary>
    public List<int> LinesUsing(GlyphGroup g, int max = 6)
    {
        var set = g.Gids.ToHashSet();
        var outp = new List<int>();
        for (int i = 0; i < Script!.Lines.Count && outp.Count < max; i++)
            if (Script.Lines[i].Glyphs.Any(set.Contains)) outp.Add(i);
        return outp;
    }

    public byte[] JapanesePng(int i)
    {
        var l = Script!.Lines[i];
        if (l.IsImage) { var (idx, pal) = Picture(l); return Images.Png(idx, pal, 3); }
        if (Previews() is { } pv) return pv.Ja(l.Ja);
        return Jobs.RenderJapanese([l], Script.Glyphs, numbered: false);
    }

    /// <summary>the game's own font renderers, if it has them (read from the rom once)</summary>
    (Func<string, byte[]> Ja, Func<string, byte[]> En)? Previews()
    {
        if (!previewsRead) { using var d = new Disc(Cue!); previews = Game!.Previews(d); previewsRead = true; }
        return previews;
    }

    /// <summary>English preview; for a picture line, the picture as it will look with this text drawn in</summary>
    public byte[] EnglishPng(string text, int? i = null)
    {
        if (i is int k && Script!.Lines[k].IsImage)
        {
            var (idx, pal) = Picture(Script.Lines[k]);
            return Images.Png(text.Length > 0 ? Images.Draw(idx, pal, text).Idx : idx, pal, 3);
        }
        return Previews() is { } pv ? pv.En(text) : Jobs.RenderEnglish(text, Font);
    }

    /// <summary>the game has pictures with text (menus etc.) at all</summary>
    public bool HasPictures => Script?.Images?.Count > 0;

    /// <summary>a game filter that selects only picture lines (Translate then does just those)</summary>
    public bool IsPictureFilter(string filter) =>
        Game?.Filters.FirstOrDefault(f => f.Key == filter) is { } gf && Script!.Lines.Where(gf.Match).All(l => l.IsImage);

    public int PicturesUnchecked => Script?.Images?.Values.Count(v => !v.Checked && v.H <= Images.MaxH) ?? 0;

    // --- background jobs ---------------------------------------------------------------------------------
    void Run(string name, Func<Action<string>, object?> fn)
    {
        if (Busy) throw new InvalidOperationException($"{Job!.Name} is already running");
        var job = new Job { Name = name };
        Job = job;
        Stop = false;
        Llm.Live = (label, text) =>
        {
            lock (liveLock)
            {
                var live = new Dictionary<string, string>(job.Live);
                if (text == null) live.Remove(label);
                else { var t = live.GetValueOrDefault(label, "") + text; live[label] = t.Length > 4000 ? t[^4000..] : t; }
                job.Live = live;
            }
        };
        void Log(string m) { lock (job.Log) job.Log.Add(m); }
        new Thread(() =>
        {
            try { job.Result = fn(Log); }
            catch (Exception e) { Log($"error: {e.Message}"); }
            finally { job.Running = false; }
        }) { IsBackground = true }.Start();
    }

    /// <summary>running requests' output for display, newest text of each, one item per line</summary>
    public string LiveText(int per = 900)
    {
        var live = Job?.Live ?? new();
        static int Num(string k) { var m = Regex.Match(k, @"(\d+)/"); return m.Success ? int.Parse(m.Groups[1].Value) : 0; }
        return string.Join("\n\n", live.Keys.OrderBy(Num).Select(k =>
        {
            var t = live[k].Replace("}, {", "},\n{");
            return $"── {k} ──\n" + (t.Length > per ? t[^per..] : t);
        }));
    }

    static void SetKey(string key) { if (key.Length > 0) Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", key); }

    List<Scene>? scenes;
    List<(string Label, List<int> Lines)>? sceneGroups;

    /// <summary>scenes in disc order with their line indexes in play order; a line used by several scenes is listed
    /// under the first. Read from the rom once.</summary>
    public List<(string Label, List<int> Lines)> SceneGroups()
    {
        if (sceneGroups != null) return sceneGroups;
        var byKey = new Dictionary<string, int>();
        for (int i = 0; i < Script!.Lines.Count; i++) byKey.TryAdd(Script.Lines[i].Key, i);
        var seen = new HashSet<int>();
        return sceneGroups = Scenes().Select(sc => (Label(sc.Path),
            sc.Rows.Select(r => byKey.GetValueOrDefault(r.Key, -1)).Where(i => i >= 0 && seen.Add(i)).ToList()))
            .Where(g => g.Item2.Count > 0).ToList();
    }
    /// <summary>dialogue files with their rows in order (read from the rom once)</summary>
    public List<Scene> Scenes()
    {
        if (scenes == null) { using var d = new Disc(Cue!); scenes = Game!.Scenes(d); }
        return scenes;
    }

    /// <summary>redo: retranslate rows that were translated row by row, now with their whole scene</summary>
    public void StartTranslate(int n, string glossary, string model, string key, bool picturesOnly, bool redo = false)
    {
        SetKey(key);
        Backup("before-translate");
        Run("translate", log =>
        {
            if (!picturesOnly) Jobs.TranslateScenes(Script!, Scenes(), n, redo, glossary, log, Save, () => Stop, model, Game!.BoxRule);
            if (!Stop && !redo) Jobs.TranslatePictures(Script!, Pics(), n, glossary, log, Save, () => Stop, model);
            return null;
        });
    }

    public void StartOcr(string model, string key)
    {
        SetKey(key);
        Run("read japanese", log => { Jobs.LabelGlyphs(Script!, log, Save, () => Stop, model); return null; });
    }

    public void StartVerifyCharacters(int n, string model, string key)
    {
        SetKey(key);
        Backup("before-check");
        Run("check characters", log => { Jobs.VerifyCharacters(Script!, n, log, Save, () => Stop, model); return null; });
    }

    /// <summary>translate line i only, as text or from its picture (overwrites its English)</summary>
    public void StartTranslateOne(int i, bool byText, string glossary, string model, string key)
    {
        SetKey(key);
        Run("translate line", log => { Jobs.TranslateOne(Script!, Script!.Lines[i].IsImage ? Pics() : new(), i, byText, glossary, log, Save, model); return i; });
    }

    /// <summary>every character of line i is verified or confirmed (or its Japanese was read from the picture / typed)</summary>
    public bool LineTextSafe(int i) => Script!.Lines[i].IsImage || GlyphTable.TextSafe(Script.Lines[i], GlyphTable.TrustedGids(Script, Groups()));

    public void StartReadPictures(string model, string key)
    {
        SetKey(key);
        Backup("before-pictures");
        Run("find menu text", log => { Jobs.ReadPictures(Script!, Pics(), log, Save, () => Stop, model); return null; });
    }

    public sealed record BuildResult(string? Cue, string? Patch, InsertReport Report, string? Error);

    /// <summary>labels: diagnostic disc where every message shows its 'file:message' label (search it to find the line)</summary>
    public void StartBuild(string outDir, bool labels)
    {
        var dir = outDir.Length > 0 ? outDir : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(Cue!))!, labels ? "diagnostic" : "patched");
        var name = Path.GetFileNameWithoutExtension(Cue) + (labels ? " (labels)" : " (English)");
        Run("build", log =>
        {
            log("inserting text...");
            using var disc = new Disc(Cue!);
            var (repl, rep) = Game!.Insert(disc, Script!, Font, labels);
            if (repl.Count == 0) return new BuildResult(null, null, rep, "no translated lines to insert");
            log($"{rep.FilesChanged} files changed, {rep.FilesSkipped.Count} kept in Japanese; writing disc...");
            Script!.TooLong = rep.TooWide.Select(FindLabel).OfType<int>().Select(i => Script.Lines[i].Key).ToHashSet();
            Save();
            if (rep.TooWide.Count > 0)
                log($"{rep.TooWide.Count} rows are too long for their text window even as two small lines and will wrap in game; " +
                    $"shorten them (paste a label into the search box): {string.Join("  ", rep.TooWide.Take(20))}");
            var (cue, bin) = Build.WritePatched(Cue!, repl, dir, name);
            log("writing BPS patch...");
            var bps = Path.Combine(dir, name + ".bps");
            Build.WriteBps(disc.Path, bin, bps);
            log($"done (patch {new FileInfo(bps).Length / 1e6:F1} MB)");
            return new BuildResult(cue, bps, rep, null);
        });
    }
}
