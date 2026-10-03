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
    public string? ScriptPath { get; private set; }
    public Script? Script { get; private set; }
    public int Budget { get; private set; }
    public Font Font { get; } = Font.Load();
    public Job? Job { get; private set; }
    public volatile bool Stop;
    readonly object saveLock = new(), liveLock = new();
    DateTime lastBackup = DateTime.MinValue;
    Dictionary<string, byte[]>? pics;

    public bool IsOpen => Script != null;
    public bool Busy => Job is { Running: true };

    // --- files -------------------------------------------------------------------------------------------
    /// <summary>open a rom; extracts its text the first time (creates &lt;rom&gt;.script.json), and merges in text
    /// a newer extractor finds, keeping all existing work</summary>
    public void Open(string cue)
    {
        using var disc = new Disc(cue);
        var serial = disc.Serial();
        if (serial == null || !Yuuyami.Serials.Contains(serial))
            throw new InvalidDataException($"unknown game {serial}; supported: {Yuuyami.Name} ({string.Join(", ", Yuuyami.Serials)})");
        var path = Path.ChangeExtension(disc.Path, ".script.json");
        bool fresh = !File.Exists(path);
        var script = fresh ? Yuuyami.Extract(disc) : Script.Load(path);
        bool stale = script.Extractor != Yuuyami.ExtractorVersion;
        if (stale)
        {
            if (!fresh) Merge(script, Yuuyami.Extract(disc));
            script.Extractor = Yuuyami.ExtractorVersion;
        }
        lock (saveLock)
        {
            (Cue, ScriptPath, Script, pics) = (cue, path, script, null);
            // text box width ~= what Japanese lines use; 99th percentile ignores a few odd non-dialogue strings
            var w = script.Lines.Where(l => !l.IsImage).Select(l => Jobs.LineWidth(l, script.Glyphs)).Order().ToList();
            Budget = w.Count > 0 ? w[(int)(w.Count * 0.99)] : 0;
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
        foreach (var l in nw.Lines)
        {
            if (byKey.TryGetValue(l.Key, out var o)) o.Refs = o.Refs.Union(l.Refs).Order(StringComparer.Ordinal).ToList();
            else old.Lines.Add(l);
        }
        foreach (var (k, g) in nw.Glyphs) old.Glyphs.TryAdd(k, g);
        Images.MergeCatalog(old, nw.Images ?? new());
        Jobs.ApplyChars(old);   // new lines made of already-read glyphs get their Japanese straight away
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
    public int TranslatedCount()
    {
        var shared = Tfile.SharedEn(Script!);
        return Script!.Lines.Count(l => Tfile.EffectiveEn(l, shared).Length > 0);
    }

    public int EnglishWidth(string text) => text.Length > 0 ? Font.TextWidth(text) : 0;

    /// <summary>'8/137/18:6' (from a diagnostic build) -> line index, or null</summary>
    public int? FindLabel(string label)
    {
        var m = Regex.Match(label, @"^\s*(\d+(?:/\d+)*):(\d+)\s*$");
        if (!m.Success) return null;
        var r = $"{Flb.Long(m.Groups[1].Value)}:{m.Groups[2].Value}";
        int i = Script!.Lines.FindIndex(l => l.Refs.Contains(r));
        return i >= 0 ? i : null;
    }

    /// <summary>indexes of lines passing a filter (all/todo/done/over/pictures) and a search string.
    /// A diagnostic label ('8/137/18:6') jumps straight to that line.</summary>
    public List<int> Matches(string filter = "all", string text = "")
    {
        if (FindLabel(text) is int hit) return [hit];
        text = text.ToLowerInvariant();
        var shared = Tfile.SharedEn(Script!);
        var outp = new List<int>();
        for (int i = 0; i < Script!.Lines.Count; i++)
        {
            var l = Script.Lines[i];
            var en = Tfile.EffectiveEn(l, shared);
            if (filter == "todo" && en.Length > 0) continue;
            if (filter == "done" && en.Length == 0) continue;
            if (filter == "over" && !(en.Length > 0 && Widths(l, en).Over)) continue;
            if (filter == "pictures" && !l.IsImage) continue;
            if (text.Length > 0 && !l.Ja.ToLowerInvariant().Contains(text) && !en.ToLowerInvariant().Contains(text)
                && !(l.Notes ?? "").ToLowerInvariant().Contains(text) && !l.Key.Contains(text)) continue;
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
        int ew = EnglishWidth(en);
        return (ew, Jobs.LineWidth(l, Script!.Glyphs), ew > Budget);
    }

    /// <summary>picture files of the rom (read once)</summary>
    public Dictionary<string, byte[]> Pics()
    {
        if (pics == null) { using var d = new Disc(Cue!); pics = Yuuyami.ImageFiles(d); }
        return pics;
    }

    public (byte[,] Idx, ushort[] Pal) Picture(Line l) => Images.Pixels(Pics(), Script!.Images![l.Image!]);

    /// <summary>update ja/en/notes of line i and save -> (english width px, chars missing from the font, too long?)</summary>
    public (int Width, List<char> Missing, bool Over) SetLine(int i, string? ja = null, string? en = null, string? notes = null)
    {
        var l = Script!.Lines[i];
        if (ja != null) l.Ja = ja.Trim();
        if (en != null) l.En = en.Trim();
        if (notes != null) l.Notes = notes.Trim();
        Save();
        return (EnglishWidth(l.En), Font.Missing(l.En.Replace("\n", "")), Widths(l, l.En).Over);
    }

    public byte[] JapanesePng(int i)
    {
        var l = Script!.Lines[i];
        if (l.IsImage) { var (idx, pal) = Picture(l); return Images.Png(idx, pal, 3); }
        return Jobs.RenderJapanese([l], Script.Glyphs, numbered: false);
    }

    /// <summary>English preview; for a picture line, the picture as it will look with this text drawn in</summary>
    public byte[] EnglishPng(string text, int? i = null)
    {
        if (i is int k && Script!.Lines[k].IsImage)
        {
            var (idx, pal) = Picture(Script.Lines[k]);
            return Images.Png(text.Length > 0 ? Images.Draw(idx, pal, text).Idx : idx, pal, 3);
        }
        return Jobs.RenderEnglish(text, Font);
    }

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

    public void StartTranslate(int n, string glossary, string model, string key, bool picturesOnly)
    {
        SetKey(key);
        Backup("before-translate");
        Run("translate", log =>
        {
            if (!picturesOnly) Jobs.TranslateLines(Script!, n, glossary, log, Save, () => Stop, model);
            if (!Stop) Jobs.TranslatePictures(Script!, Pics(), n, glossary, log, Save, () => Stop, model);
            return null;
        });
    }

    public void StartOcr(string model, string key)
    {
        SetKey(key);
        Run("read japanese", log => { Jobs.LabelGlyphs(Script!, log, Save, () => Stop, model); return null; });
    }

    public void StartReadPictures(string model, string key)
    {
        SetKey(key);
        Backup("before-pictures");
        Run("find menu text", log => { Jobs.ReadPictures(Script!, Pics(), log, Save, () => Stop, model); return null; });
    }

    public sealed record BuildResult(string? Cue, string? Patch, Yuuyami.InsertReport Report, string? Error);

    /// <summary>labels: diagnostic disc where every message shows its 'file:message' label (search it to find the line)</summary>
    public void StartBuild(string outDir, bool labels)
    {
        var dir = outDir.Length > 0 ? outDir : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(Cue!))!, labels ? "diagnostic" : "patched");
        var name = Path.GetFileNameWithoutExtension(Cue) + (labels ? " (labels)" : " (English)");
        Run("build", log =>
        {
            log("inserting text...");
            using var disc = new Disc(Cue!);
            var (repl, rep) = Yuuyami.Insert(disc, Script!, Font, labels);
            if (repl.Count == 0) return new BuildResult(null, null, rep, "no translated lines to insert");
            log($"{rep.FilesChanged} files changed, {rep.FilesSkipped.Count} kept in Japanese; writing disc...");
            var (cue, bin) = Build.WritePatched(Cue!, repl, dir, name);
            log("writing BPS patch...");
            var bps = Path.Combine(dir, name + ".bps");
            Build.WriteBps(disc.Path, bin, bps);
            log($"done (patch {new FileInfo(bps).Length / 1e6:F1} MB)");
            return new BuildResult(cue, bps, rep, null);
        });
    }
}
