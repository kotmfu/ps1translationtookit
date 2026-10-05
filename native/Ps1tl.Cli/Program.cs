using System.Security.Cryptography;
using Ps1tl;

// ps1tl-cli: command line for the native core (scripting and checks).
//   extract  game.cue out.script.json
//   insert   game.cue project.script.json        -> md5 of the rebuilt FILELINK.FLB + report
//   patch    game.cue project.script.json outdir name
//   apply    original.bin patch.bps out.bin
Console.OutputEncoding = System.Text.Encoding.UTF8;
System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
var a = args;
IGame GameOf(Disc d) => Games.BySerial(d.Serial()) ?? throw new InvalidDataException($"unknown game {d.Serial()}; supported: {Games.Supported}");
if (a.Length < 2) { Console.WriteLine("usage: extract|insert|patch|apply ..."); return 1; }
switch (a[0])
{
    case "extract":
    {
        using var disc = new Disc(a[1]);
        var s = GameOf(disc).Extract(disc);
        s.Save(a[2]);
        Console.WriteLine($"{s.Lines.Count} lines, {s.Glyphs.Count} glyphs, {s.Images?.Count ?? 0} pictures -> {a[2]}");
        break;
    }
    case "insert":
    {
        using var disc = new Disc(a[1]);
        var t0 = DateTime.Now;
        var (repl, rep) = GameOf(disc).Insert(disc, Script.Load(a[2]), Font.Load(), a.Length > 3 && a[3] == "labels");
        foreach (var (p, d) in repl) Console.WriteLine($"{p} {d.Length} {Convert.ToHexStringLower(MD5.HashData(d))}");
        Console.WriteLine($"too wide: {rep.TooWide.Count} rows: " + string.Join(" ", rep.TooWide.GroupBy(t => t.Split(':')[0]).Select(g => $"{g.Key}x{g.Count()}")));
        Console.WriteLine($"changed {rep.FilesChanged} skipped {rep.FilesSkipped.Count} english {rep.LinesWithEnglish} ({(DateTime.Now - t0).TotalSeconds:F1}s)");
        break;
    }
    case "patch":
    {
        var t0 = DateTime.Now;
        using var disc = new Disc(a[1]);
        var (repl, rep) = GameOf(disc).Insert(disc, Script.Load(a[2]), Font.Load(), a.Length > 5 && a[5] == "labels");
        var (cue, bin) = Build.WritePatched(a[1], repl, a[3], a[4]);
        var bps = Path.Combine(a[3], a[4] + ".bps");
        Build.WriteBps(disc.Path, bin, bps);
        Console.WriteLine($"{cue}\n{bps}\nchanged {rep.FilesChanged} skipped {rep.FilesSkipped.Count} ({(DateTime.Now - t0).TotalSeconds:F1}s)");
        foreach (var (k, v) in rep.FilesSkipped) Console.WriteLine($"  skipped {k}: {v}");
        break;
    }
    case "llm-test":   // two tiny parallel requests with streaming output: checks the Claude connection
    {
        int chunks = 0;
        Llm.Live = (label, text) => { if (text != null) Interlocked.Increment(ref chunks); };
        var png = a.Length > 2 && a[2] == "text" ? null : new Raster(40, 20, Raster.Rgb(255, 255, 255)).Png();   // "text": no image
        const string schema = """{"type":"object","properties":{"c":{"type":"array","items":{"type":"string"}}},"required":["c"],"additionalProperties":false}""";
        var t0 = DateTime.Now;
        Llm.RunParallel(Enumerable.Repeat<Func<System.Text.Json.JsonElement>>(() => Llm.Ask(png, "List 3 colours.", schema, model: a[1], effort: "low"), 2).ToList(),
            () => false, (i, r) => Console.WriteLine($"request {i + 1}: {r}"));
        Console.WriteLine($"{Llm.Backend()} · {chunks} streamed chunks · {(DateTime.Now - t0).TotalSeconds:F1}s");
        break;
    }
    case "glyph-check":   // re-learn the glyph table in memory (nothing saved) and report how it agrees with picture readings
    {
        var s = Script.Load(a[1]);
        int Agree() => s.Lines.Count(l => l.JaSource is "pic" && l.Glyphs.Count > 0
            && Tfile.NormJa(string.Concat(l.Glyphs.Select(g => s.Chars!.GetValueOrDefault(g, "#")))) == Tfile.NormJa(l.Ja));
        GlyphTable.MarkLegacySources(s);
        int pic = s.Lines.Count(l => l.JaSource == "pic");
        Console.WriteLine($"picture-read lines: {pic}; table agrees with {Agree()} before");
        var before = s.Lines.Select(l => l.Ja).ToList();
        int changedGlyphs = GlyphTable.Learn(s), changedLines = GlyphTable.Apply(s);
        Console.WriteLine($"after learning: agrees with {Agree()}; {changedGlyphs} glyph readings changed, {changedLines} other lines' Japanese corrected");
        foreach (var grp in GlyphTable.Report(s).GroupBy(g => g.Status)) Console.WriteLine($"  {grp.Key}: {grp.Count()} shapes, {grp.Sum(g => g.Uses)} uses");
        var t1 = DateTime.Now;
        var trusted = GlyphTable.TrustedGids(s);
        var todo = s.Lines.Where(l => l.Glyphs.Count > 0 && l.En.Length == 0).ToList();
        Console.WriteLine($"untranslated: {todo.Count}, of which text-safe {todo.Count(l => GlyphTable.TextSafe(l, trusted))} " +
                          $"(trusted set in {(DateTime.Now - t1).TotalMilliseconds:F0} ms)");
        var doubt = GlyphTable.DoubtfulGids(s);
        var cover = new HashSet<string>(); int need = 0;
        foreach (var sh in s.Lines.Where(l => l.JaSource == null).Select(l => l.Glyphs.Where(doubt.ContainsKey).Select(g => doubt[g]).ToHashSet())
                              .Where(x => x.Count > 0).OrderByDescending(x => x.Count))
            if (!sh.IsSubsetOf(cover)) { cover.UnionWith(sh); need++; }
        Console.WriteLine($"character check: {need} lines cover {cover.Count} of {doubt.Values.Distinct().Count()} doubtful shapes");
        foreach (var (l, i) in s.Lines.Select((l, i) => (l, i)).Where(t => t.l.Ja != before[t.i]).Take(8))
            Console.WriteLine($"  {before[i]}  ->  {l.Ja}");
        break;
    }
    case "job-test":   // job-test <script copy> <model>: one line by text and by picture, a 2-line character check, 4 mixed lines (saves to the copy)
    {
        var s = Script.Load(a[1]);
        void Log(string m) => Console.WriteLine(m);
        void Save() => s.Save(a[1]);
        if (GlyphTable.MarkLegacySources(s)) { GlyphTable.Learn(s); GlyphTable.Apply(s); }   // as the app does on open
        int i = s.Lines.FindIndex(l => l.Glyphs.Count > 10 && l.En.Length == 0);
        Jobs.TranslateOne(s, new(), i, true, "", Log, Save, a[2]);
        Jobs.TranslateOne(s, new(), i, false, "", Log, Save, a[2]);
        Jobs.VerifyCharacters(s, 2, Log, Save, () => false, a[2]);
        using var disc = new Disc(a[3]);   // job-test <script copy> <model> <cue>
        var game = GameOf(disc);
        Jobs.TranslateScenes(s, game.Scenes(disc).Where(sc => sc.Path == (a.Length > 4 ? a[4] : "/002/579")).ToList(), 100, true, "", Log, Save, () => false, a[2], game.BoxRule);
        break;
    }
    case "scene-test":   // scene-test <script copy> <model> <cue> <scene path>: translate one scene as the app does (saves to the copy)
    {
        var s = Script.Load(a[1]);
        using var disc = new Disc(a[3]);
        var game = GameOf(disc);
        Jobs.TranslateScenes(s, game.Scenes(disc).Where(sc => sc.Path == a[4]).ToList(), null, false, "", Console.WriteLine, () => s.Save(a[1]), () => false, a[2], game.BoxRule);
        break;
    }
    case "tl-plan":   // tl-plan <script> <cue> [redo]: how a Translate run would batch (no requests are sent, nothing saved)
    {
        var s = Script.Load(a[1]);
        using var disc = new Disc(a[2]);
        var game = GameOf(disc);
        Jobs.TranslateScenes(s, game.Scenes(disc), null, a.Length > 3, "", Console.WriteLine, () => { }, () => true, "opus", game.BoxRule);
        break;
    }
    case "line-png":   // line-png <script> <ja substring> <out.png>: the first matching line in the game font
    {
        var s = Script.Load(a[1]);
        var l = s.Lines.First(l => l.Ja.Contains(a[2]));
        File.WriteAllBytes(a[3], Jobs.RenderJapanese([l], s.Glyphs, numbered: false, scale: 3));
        Console.WriteLine($"{l.Key} {l.Glyphs.Count} glyphs: {string.Join(" ", l.Glyphs)}");
        break;
    }
    case "acd-stats":   // acd-stats game.cue: per ACD file: variant, messages, longest message (glyphs), ctrl values
    {
        using var disc = new Disc(a[1]);
        var (lba, size) = disc.Files()["/DATA/FILELINK.FLB"];
        foreach (var (path, d) in Flb.Walk(disc.Read(lba, size)))
        {
            var p = Yuuyami.ParseAcd(d);
            if (p == null || p.Msgs.Count == 0) continue;
            Console.WriteLine($"{path} {(d[3] == 0 ? "ACD0" : "ACD_")} msgs={p.Msgs.Count} max={p.Msgs.Max(m => m.Seq.Count)} " +
                              $"ctrl={string.Join(",", p.Msgs.Select(m => m.Ctrl).Distinct().Order().Select(c => c.ToString("x")))}");
        }
        break;
    }
    case "acd-widths":   // acd-widths game.cue: Japanese row width percentiles (px) for 'ACD ' (cutscene) vs 'ACD\0' files
    {
        using var disc = new Disc(a[1]);
        var (lba, size) = disc.Files()["/DATA/FILELINK.FLB"];
        var w = new Dictionary<bool, List<(int W, int N, string Where)>> { [true] = new(), [false] = new() };
        foreach (var (path, d) in Flb.Walk(disc.Read(lba, size)))
        {
            var p = Yuuyami.ParseAcd(d);
            if (p == null) continue;
            for (int m = 0; m < p.Msgs.Count; m++)
                if (p.Msgs[m].Seq.Count > 0) w[d[3] == ' '].Add((p.Msgs[m].Seq.Sum(k => p.Metrics[k].W + 2), p.Msgs[m].Seq.Count, $"{path}:{m}"));
        }
        foreach (var (cut, l) in w)
        {
            var o = l.OrderBy(x => x.W).ToList();
            Console.WriteLine($"{(cut ? "cutscene" : "dialogue")}: {o.Count} rows; px p50={o[o.Count / 2].W} p99={o[(int)(o.Count * .99)].W} p999={o[(int)(o.Count * .999)].W} max={o[^1].W}; top: " +
                              string.Join(", ", o.TakeLast(5).Select(x => $"{x.W}px/{x.N} {x.Where}")));
        }
        break;
    }
    case "acd-show":   // acd-show game.cue project.script.json /002/579: each message: ctrl, glyph count, Japanese, English, packed cells
    {
        using var disc = new Disc(a[1]);
        var s = Script.Load(a[2]);
        var font = Font.Load();
        var byKey = s.Lines.ToDictionary(l => l.Key);
        var shared = Tfile.SharedEn(s);
        var (lba, size) = disc.Files()["/DATA/FILELINK.FLB"];
        var d = Flb.Walk(disc.Read(lba, size)).First(x => x.Item1 == a[3]).Item2;
        var p = Yuuyami.ParseAcd(d)!;
        var ids = p.Bitmaps.Select(Yuuyami.Gid).ToList();
        int W(string t) => font.Pack(t).Sum(tok => Font.ToCell(font.Join(tok)).Item3 + 2);   // as Insert measures
        var texts = p.Msgs.Select(mm => mm.Seq.Count == 0 ? null : byKey.GetValueOrDefault(Yuuyami.LineKey(mm.Seq.Select(k => ids[k]))) is { } l
            ? Tfile.EffectiveEn(l, shared) : null).ToList();
        var wide = new List<int>();
        var flowed = Yuuyami.Stack(p, Yuuyami.Reflow(p, texts, W, wide), W, Font.Load("en_small"), wide);
        Console.WriteLine($"limit {p.Msgs.Where(m => m.Seq.Count > 0).Max(m => m.Seq.Sum(k => p.Metrics[k].W + 2))}px; pages still too wide: {string.Join(", ", wide)}");
        for (int m = 0; m < p.Msgs.Count; m++)
        {
            var (seq, ctrl) = p.Msgs[m];
            var en = flowed[m] ?? "";
            Console.WriteLine($"{m,3} {(ctrl == 0x200 ? "+" : ".")} jw={seq.Sum(k => p.Metrics[k].W + 2),3} ew={(en.Length == 0 ? 0 : en[0] == Yuuyami.StackMark ? Yuuyami.StackTokens(en, Font.Load("en_small")).Count * 18 : W(en)),3} " +
                              $"{(flowed[m] != texts[m] ? "*" : " ")} {en}");
        }
        break;
    }
    case "mock-stack":   // mock-stack out.png "line 1" "line 2": current font vs two small lines stacked in one 16px row
    {
        var big = Font.Load(); var small = Font.Load("en_small");
        var im = new Raster(256, 44, Raster.Rgb(0, 0, 0));
        uint white = Raster.Rgb(255, 255, 255);
        im.Text(4, 3, a[2] + " " + a[3], white, big);       // today: one row
        im.Text(4, 25, a[2], white, small);                 // proposed: same row height, two lines
        im.Text(4, 33, a[3], white, small);
        File.WriteAllBytes(a[1], im.Scale(4).Png());
        break;
    }
    case "stack-png":   // stack-png out.png "line 1" "line 2": a stacked row drawn cell by cell as the game places them
    {
        var small = Font.Load("en_small");
        var tok = $"{Yuuyami.StackMark}{a[2]}{Yuuyami.StackSep}{a[3]}";
        var cellsT = Yuuyami.StackTokens(tok, small);
        var im = new Raster(cellsT.Count * 16 + 8, 20, Raster.Rgb(40, 40, 70));
        int x = 4;
        foreach (var t in cellsT)
        {
            var (bm, cx, w) = Yuuyami.StackCell(t, small);
            for (int i = 0; i < 256; i++)
            {
                int v = i % 2 == 0 ? bm[i / 2] & 15 : bm[i / 2] >> 4;
                if (v != 15) im.Set(x + i % 16 - cx, 2 + i / 16, v >= 6 ? Raster.Rgb(255, 255, 255) : Raster.Rgb(0, 0, 0));
            }
            x += w;
        }
        File.WriteAllBytes(a[1], im.Scale(4).Png());
        Console.WriteLine($"{cellsT.Count} cells");
        break;
    }
    case "track-check":   // track-check game.cue: retiming with the original lengths must leave every track table unchanged
    {
        using var disc = new Disc(a[1]);
        var (lba, size) = disc.Files()["/DATA/FILELINK.FLB"];
        int files = 0, bad = 0;
        foreach (var (path, d) in Flb.Walk(disc.Read(lba, size)))
        {
            var p = Yuuyami.ParseAcd(d);
            if (p == null) continue;
            int t0 = BitConverter.ToInt32(d, 12), t1 = BitConverter.ToInt32(d, 16);
            var copy = (byte[])d.Clone();
            bool ok = Yuuyami.RetimeTracks(copy, t0, t1, p, p.Msgs.Select(m => (int?)null).ToList()) && copy.AsSpan().SequenceEqual(d);
            files++;
            if (!ok) { if (bad++ < 5) Console.WriteLine($"differs: {path}"); }
        }
        Console.WriteLine($"{files} files, {bad} differ");
        break;
    }
    case "page-check":   // page-check game.cue project.script.json: glyph sprites per page (rows joined by 0x200), English vs Japanese
    {
        using var disc = new Disc(a[1]);
        var s = Script.Load(a[2]);
        var font = Font.Load();
        var byKey = s.Lines.ToDictionary(l => l.Key);
        var shared = Tfile.SharedEn(s);
        int pages = 0, over78 = 0, overJa = 0, maxEn = 0;
        foreach (var sc in Yuuyami.Scenes(disc))
        {
            int en = 0, ja = 0;
            foreach (var r in sc.Rows)
            {
                if (!byKey.TryGetValue(r.Key, out var l)) continue;   // not in this project yet
                var t = new string(Tfile.EffectiveEn(l, shared).Replace('\n', ' ').Select(c => font.Has(c) ? c : '?').ToArray());
                en += t.Length > 0 ? font.Pack(t).Count : l.Glyphs.Count;
                ja += l.Glyphs.Count;
                if (r.More) continue;
                pages++;
                if (en > 78) { over78++; if (en > ja) overJa++; }
                maxEn = Math.Max(maxEn, en);
                en = ja = 0;
            }
        }
        Console.WriteLine($"{pages} pages; English > 78 sprites: {over78} (of which more than the Japanese: {overJa}); most: {maxEn}");
        break;
    }
    case "verify-tracks":   // verify-tracks game.cue project.script.json: in the English build, text tracks tile each file's entries and durations = delay sums
    {
        using var disc = new Disc(a[1]);
        var (repl, _) = Yuuyami.Insert(disc, Script.Load(a[2]), Font.Load(), false);
        int files = 0, bad = 0;
        foreach (var (path, d) in Flb.Walk(repl["/DATA/FILELINK.FLB"]))
        {
            if (Yuuyami.ParseAcd(d) == null) continue;
            int t0 = BitConverter.ToInt32(d, 12), t1 = BitConverter.ToInt32(d, 16), m0 = BitConverter.ToInt32(d, 28), m1 = BitConverter.ToInt32(d, 32);
            int entries = (m1 - m0) / 4, next = 0;
            bool ok = true;
            foreach (var at in Enumerable.Range(0, (t1 - t0) / 16).Select(i => t0 + 16 * i)
                         .Where(at => (BitConverter.ToUInt16(d, at + 14) & 1) != 0 && BitConverter.ToUInt16(d, at + 10) > 0)
                         .OrderBy(at => BitConverter.ToUInt16(d, at + 8)))
            {
                int st = BitConverter.ToUInt16(d, at + 8), n = BitConverter.ToUInt16(d, at + 10), dur = BitConverter.ToUInt16(d, at + 12);
                ok &= st == next && Enumerable.Range(st, n).Sum(e => d[m0 + 4 * e + 3]) == dur;
                next = st + n;
            }
            ok &= next == 0 || next == entries;
            int f0 = BitConverter.ToInt32(d, 16);
            foreach (var at in Enumerable.Range(0, (t1 - t0) / 16).Select(i => t0 + 16 * i).Where(at => (BitConverter.ToUInt16(d, at + 14) & 1) != 0))
            {
                int fa = BitConverter.ToUInt16(d, at), fn = BitConverter.ToUInt16(d, at + 2), dur = BitConverter.ToUInt16(d, at + 12);
                if (fn < 2) continue;
                var frames = Enumerable.Range(fa, fn).Select(k => (Dur: (int)d[f0 + 8 * k + 6], Bits: d[f0 + 8 * k + 7])).ToList();
                ok &= frames.Sum(f => f.Dur) == dur;
                if (a.Length > 3 && path == a[3] && frames.Any(f => (f.Bits & 3) != 0))
                {
                    // as 0x8005E7D4: a glyph belongs to the frame its start time falls in
                    int st = BitConverter.ToUInt16(d, at + 8), n = BitConverter.ToUInt16(d, at + 10), t = 0;
                    var count = new int[fn];
                    for (int e = st; e < st + n; e++)
                    {
                        int k = 0, T = frames[0].Dur;
                        while (k < fn - 1 && t >= T) T += frames[++k].Dur;
                        if (BitConverter.ToInt16(d, m0 + 4 * e) >= 0) count[k]++;
                        t += d[m0 + 4 * e + 3];
                    }
                    Console.WriteLine($"  choice track at {at - t0 >> 4}: " + string.Join(" | ", frames.Select((f, k) => $"bits {f.Bits}: {count[k]} glyphs")));
                }
            }
            files++;
            if (!ok && bad++ < 5) Console.WriteLine($"bad tracks: {path}");
        }
        Console.WriteLine($"{files} files checked, {bad} bad");
        break;
    }
    case "open":   // open game.cue: open/upgrade the project next to the cue as the app does, and summarise it
    {
        var pr = new Project();
        pr.Open(a[1]);
        var s = pr.Script!;
        Console.WriteLine($"{s.Lines.Count} lines · translated {s.Lines.Count(l => l.En.Length > 0)} · " +
                          $"unused {s.Lines.Count(l => !l.IsImage && l.Refs.Count == 0)} · untranslated in use {s.Lines.Count(l => l.Refs.Count > 0 && l.En.Length == 0 && !l.IsImage)}");
        var t0 = DateTime.Now;
        var groups = pr.SceneGroups();
        Console.WriteLine($"{groups.Count} scenes ({(DateTime.Now - t0).TotalSeconds:F1}s), {groups.Sum(g => g.Lines.Count)} lines in them; first: {groups[0].Label} " +
                          string.Join(" | ", groups[0].Lines.Take(3).Select(i => pr.Label(s.Lines[i].Refs[0]))));
        foreach (var q in a.Skip(2))   // open game.cue [label or search text ...]
            Console.WriteLine($"  {q}: label -> {(pr.FindLabel(q) is int i ? s.Lines[i].Ja.Replace('\n', ' ') : "-")}; search -> {pr.Matches("all", q).Count} lines");
        break;
    }
    case "choices":   // choices project.script.json: every choice row, its Japanese layout and the English cells (or why it doesn't fit)
    {
        var s = Script.Load(a[1]);
        var font = Font.Load();
        int ok = 0, bad = 0;
        foreach (var l in s.Lines.Where(l => !l.IsImage && l.Refs.Count > 0))
        {
            if (Yuuyami.ChoiceLayout(l) is not { } lay) continue;
            var en = new string(l.En.Select(c => font.Has(c) ? c : '?').ToArray());
            var tk = en.Length > 0 ? Yuuyami.ChoiceTokens(en, lay, font.Pack)?.Tokens : null;
            if (tk != null) ok++; else bad++;
            if (tk == null || ok <= 5)
                Console.WriteLine($"{l.Refs[0]} {l.Ja} [{string.Join(",", lay.Select(r => (r.Sep ? "s" : "") + r.Len))}] | {l.En} -> " +
                                  (tk == null ? "DOESN'T FIT" : string.Join("", tk.Select(t => $"[{t}]"))));
        }
        Console.WriteLine($"{ok} choices fit, {bad} don't");
        break;
    }
    case "pack":   // pack "text": the packed cells with their advance and the running total (as inserted)
    {
        var font = Font.Load();
        int x = 0, i = 0;
        foreach (var tok in font.Pack(a[1]))
        {
            int w = Font.ToCell(font.Join(tok)).Item3;
            x += w;
            Console.WriteLine($"{++i,2} [{tok}] w={w,2} end={x}");
        }
        break;
    }
    case "gpm-roundtrip":   // gpm-roundtrip game.cue: EVD/EVDATA rebuilt unchanged must be byte-identical; every event must parse
    {
        using var disc = new Disc(a[1]);
        var (lba, size) = disc.Files()["/EVDATA.BIN"];
        var ev = disc.Read(lba, size);
        var exe = disc.Read(disc.Files()["/SCPS_101.36"].Lba, disc.Files()["/SCPS_101.36"].Size);
        var args8 = exe.AsSpan((int)(0x800a96d0 - 0x80010010) + 0x800, 256).ToArray().Select(b => (sbyte)b).ToArray();
        int files = 0, msgs = 0, bad = 0;
        var same = new Dictionary<string, byte[]>();
        foreach (var (name, d) in Evdata.Walk(ev).Where(f => f.Name.EndsWith(".EVD")))
        {
            var e = Gunparade.ParseEvd(d, args8);
            files++; msgs += e.Msgs.Count;
            // every message rewritten with its own Japanese bytes must give the file back
            var (nw, _, err) = Gunparade.InsertEvd(d, e, _ => null);
            if (nw != null || err != null) { bad++; Console.WriteLine($"{name}: changed without English ({err})"); }
            same[name] = d;
        }
        bool archiveSame = Evdata.Rebuild(ev, new Dictionary<string, byte[]>()).AsSpan().SequenceEqual(ev);
        bool replacedSame = Evdata.Rebuild(ev, same).AsSpan().SequenceEqual(ev);
        var blank = new Gunparade().Extract(disc);
        foreach (var l in blank.Lines) l.En = "";
        var (repl, _) = new Gunparade().Insert(disc, blank, Font.Load(), false);
        Console.WriteLine($"{files} events, {msgs} messages, {bad} bad; archive rebuilt unchanged: {archiveSame}, with files re-put: {replacedSame}; " +
                          $"insert with no English changes {repl.Count} files");
        if (bad > 0 || !archiveSame || !replacedSame || repl.Count > 0) return 1;
        break;
    }
    case "gpm-pics":   // gpm-pics game.cue [outdir]: every picture re-put unchanged must decode the same; packed vs original size; PNGs
    {
        using var disc = new Disc(a[1]);
        var game = new Gunparade();
        var files = game.ImageFiles(disc);
        int pics = 0, bad = 0, skipped = 0; long orig = 0, packed = 0;
        foreach (var (p, d) in files.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            var es = GpmPics.Parse(d)!;
            for (int i = 0; i < es.Count; i++)
            {
                if (GpmPics.Data(d, i) is not var (idx, pal)) { if (es[i].Colors == 0) skipped++; continue; }
                pics++;
                var nw = GpmPics.Put(d, i, idx);
                var back = GpmPics.Data(nw, i);
                bool same = back != null && back.Value.Idx.Cast<byte>().SequenceEqual(idx.Cast<byte>()) && back.Value.Pal.SequenceEqual(pal)
                    && Enumerable.Range(0, es.Count).Where(k => k != i).All(k => GpmPics.Data(nw, k) is var o && GpmPics.Data(d, k) is var q
                        && (o == null) == (q == null) && (o == null || o.Value.Idx.Cast<byte>().SequenceEqual(q!.Value.Idx.Cast<byte>())));
                if (!same) { bad++; Console.WriteLine($"{p}:{i} differs after re-put"); }
                orig += es[i].End - es[i].Off; packed += nw.Length - d.Length + es[i].End - es[i].Off;
                if (a.Length > 2 && idx.GetLength(0) <= Images.MaxH)
                {
                    Directory.CreateDirectory(a[2]);
                    File.WriteAllBytes(Path.Combine(a[2], $"{p.Trim('/').Replace('/', '_')}_{i}.png"), Images.Png(idx, pal, 1));
                }
            }
        }
        Console.WriteLine($"{files.Count} files, {pics} pictures ({skipped} 16bpp/unreadable skipped), {bad} bad; packed {packed} bytes vs original {orig}");
        if (bad > 0) return 1;
        break;
    }
    case "evd-show":   // evd-show game.cue EV0001 [project]: tokens of one event (opcodes by name, rows), English rows as inserted
    {
        using var disc = new Disc(a[1]);
        var files = disc.Files();
        var ev = disc.Read(files["/EVDATA.BIN"].Lba, files["/EVDATA.BIN"].Size);
        var exe = disc.Read(files["/SCPS_101.36"].Lba, files["/SCPS_101.36"].Size);
        int Off(uint addr) => (int)(addr - 0x80010010) + 0x800;
        var args8 = exe.AsSpan(Off(0x800a96d0), 256).ToArray().Select(b => (sbyte)b).ToArray();
        string OpName(byte op) { int p = Off(BitConverter.ToUInt32(exe, Off(0x800a92d0) + 4 * op)); return System.Text.Encoding.ASCII.GetString(exe, p, Array.IndexOf(exe, (byte)0, p) - p); }
        var d = Evdata.Walk(ev).First(f => f.Name == a[2] + ".EVD").Data;
        var e = Gunparade.ParseEvd(d, args8);
        var sjis = System.Text.Encoding.GetEncoding(932);
        Console.WriteLine("labels: " + string.Join(" ", e.Labels.Select(l => $"{l.Label:x}@{l.Offset:x}")));
        var first = e.Msgs.Select((m, i) => (m, i)).ToDictionary(x => x.m.First, x => x.i);
        foreach (var (t, i) in e.Toks.Select((t, i) => (t, i)))
            Console.WriteLine(t.Row == null ? $"{t.At:x5}  {OpName(t.Raw[1])}({string.Join(",", Enumerable.Range(0, (t.Raw.Length - 2) / 2).Select(k => BitConverter.ToUInt16(t.Raw, 2 + 2 * k).ToString("x")))})"
                                            : $"{t.At:x5}  {(first.TryGetValue(i, out var m) ? $"[{m}]" : "   ")} {sjis.GetString(t.Row)}");
        if (a.Length > 3)
        {
            var s = Script.Load(a[3]);
            var shared = Tfile.SharedEn(s);
            var byRef = s.Lines.SelectMany(l => l.Refs.Select(r => (r, l))).ToDictionary(x => x.r, x => x.l);
            for (int k = 0; k < e.Msgs.Count; k++)
                if (byRef.TryGetValue($"{a[2]}:{k}", out var l) && Tfile.EffectiveEn(l, shared) is { Length: > 0 } en)
                    Console.WriteLine($"[{k}] " + string.Join(" | ", Gunparade.Wrap(en)));
        }
        break;
    }
    case "gpm-font":   // gpm-font out.png: the English half-width font as patched into FONTDATA.BIN, sample text drawn with it
    {
        using var disc = new Disc(a[1]);
        var f = disc.Files()["/FONT/FONTDATA.BIN"];
        var font = Gunparade.PatchFont(disc.Read(f.Lba, f.Size));
        int ank = BitConverter.ToInt32(font, 12);
        var lines = new[] { "The quick brown fox jumps over", "the lazy dog. HELLO, $0! 0123", "Shiba: \"Are you ready?\" (yes/no)" };
        var im = new Raster(30 * 8 + 8, lines.Length * 14 + 6, Raster.Rgb(20, 20, 60));
        for (int r = 0; r < lines.Length; r++)
            for (int c = 0; c < lines[r].Length; c++)
            {
                int code = Gunparade.Encode(lines[r][c].ToString())[1];
                for (int y = 0; y < 12; y++)
                    for (int x = 0; x < 8; x++)
                        if ((font[ank + (code - 0x20) * 12 + y] >> (7 - x) & 1) != 0) im.Set(4 + c * 8 + x, 3 + r * 14 + y, Raster.Rgb(255, 255, 255));
            }
        File.WriteAllBytes(a[2], im.Scale(3).Png());
        break;
    }
    case "files":   // files game.cue: serial, then every ISO file by LBA with its size
    {
        using var disc = new Disc(a[1]);
        Console.WriteLine(disc.Serial());
        foreach (var (p, (lba, size)) in disc.Files().OrderBy(kv => kv.Value.Lba))
            Console.WriteLine($"{lba,7} {size,10} {p}");
        break;
    }
    case "gpm-strings":   // gpm-strings game.cue [file filter]: Shift-JIS strings found in the EXE and overlays
    {
        using var disc = new Disc(a[1]);
        foreach (var (path, b) in GpmStrings.Files(disc))
        {
            if (a.Length > 2 && !path.Contains(a[2], StringComparison.OrdinalIgnoreCase)) continue;
            var (lba, size) = disc.Files()[path];
            var ss = GpmStrings.Strings(disc.Read(lba, size), b);
            Console.WriteLine($"== {path} @{b:x8}: {ss.Count} strings, {ss.Sum(s => s.Len)} bytes");
            if (a.Length > 2) foreach (var s in ss) Console.WriteLine($"{s.Addr:x8} {s.Len,4} {s.Ptrs.Count}{(s.Ptrs.All(p => p.Word || p.Lui >= 0) ? "" : "!")} {s.Ja.Replace("\n", "\\n")}");
        }
        break;
    }
    case "gpm-strings-test":   // gpm-strings-test game.cue: names get long English, every pointer must reach it; others untouched
    {
        using var disc = new Disc(a[1]);
        int bad = 0, moved = 0, kept = 0, noRoomAll = 0;
        foreach (var (path, b) in GpmStrings.Files(disc))
        {
            var (lba, size) = disc.Files()[path];
            var d = disc.Read(lba, size);
            var ss = GpmStrings.Strings(d, b);
            string Fake(GpmStrings.Str s) => s.Addr % 3 == 0 ? "Yoshino Haruka the Teacher" : "Ok";   // some grow, some shrink
            int room = GpmStrings.Room(disc, path);
            var (nw, noRoom) = GpmStrings.Insert(d, b, Fake, room);
            noRoomAll += noRoom.Count;
            if (nw == null) continue;
            if (nw.Length < d.Length || nw.Length > Math.Max(room, d.Length)) { bad++; Console.WriteLine($"{path}: size {nw.Length} outside {d.Length}..{room}"); }
            if (nw.Length != d.Length) Console.WriteLine($"{path}: grew {d.Length} -> {nw.Length} (slot {room})");
            foreach (var s in ss)
            {
                if (noRoom.Contains(s.Addr)) continue;
                var want = GpmStrings.Encode(Fake(s), s.Ja);
                foreach (var p in s.Ptrs)
                {
                    uint at = p.Word ? BitConverter.ToUInt32(nw, p.At)
                        : (p.Lui >= 0 ? BitConverter.ToUInt32(nw, p.Lui) << 16 : (s.Addr - (uint)(short)(BitConverter.ToUInt32(d, p.At) & 0xFFFF)))
                          + (uint)(short)(BitConverter.ToUInt32(nw, p.At) & 0xFFFF);
                    int o = (int)(at - b);
                    if (o < 0 || o + want.Length >= nw.Length || !nw.AsSpan(o, want.Length).SequenceEqual(want) || nw[o + want.Length] != 0)
                    { bad++; if (bad < 10) Console.WriteLine($"{path} {s.Addr:x8}: pointer at {p.At:x} reaches {at:x8}"); }
                }
                if (at0(s) != s.Addr) moved++; else kept++;
                uint at0(GpmStrings.Str s) => s.Ptrs[0].Word ? BitConverter.ToUInt32(nw, s.Ptrs[0].At) : s.Addr;
            }
        }
        Console.WriteLine($"bad {bad}, moved {moved}, in place {kept}, no room {noRoomAll}");
        if (bad > 0) return 1;
        break;
    }
    case "put":   // put game.cue outdir name /ISO/PATH=localfile ...: copy of the disc with those files replaced (RE experiments)
    {
        var repl = a.Skip(4).Select(x => x.Split('=', 2)).ToDictionary(x => x[0], x => File.ReadAllBytes(x[1]));
        Console.WriteLine(Build.WritePatched(a[1], repl, a[2], a[3]).Cue);
        break;
    }
    case "dump":   // dump game.cue outdir [path filter]: copy ISO files out (for hex inspection)
    {
        using var disc = new Disc(a[1]);
        foreach (var (p, (lba, size)) in disc.Files())
        {
            if (a.Length > 3 && !p.Contains(a[3])) continue;
            var o = Path.Combine(a[2], p.TrimStart('/'));
            Directory.CreateDirectory(Path.GetDirectoryName(o)!);
            File.WriteAllBytes(o, disc.Read(lba, size));
        }
        break;
    }
    case "sjis-scan":   // sjis-scan game.cue [min chars] [path filter]: per file, runs of valid 2-byte Shift-JIS (count, chars, longest)
    {
        using var disc = new Disc(a[1]);
        int min = a.Length > 2 ? int.Parse(a[2]) : 6;
        var sjis = System.Text.Encoding.GetEncoding(932);
        foreach (var (p, (lba, size)) in disc.Files().OrderBy(kv => kv.Value.Lba))
        {
            if (a.Length > 3 && !p.Contains(a[3])) continue;
            if (p.EndsWith(".XA") || p.EndsWith(".STR")) continue;
            var d = disc.Read(lba, size);
            var runs = new List<(int At, int Len)>();
            for (int i = 0; i + 1 < d.Length;)
            {
                int j = i;
                while (j + 1 < d.Length && (d[j] is >= 0x81 and <= 0x9F or >= 0xE0 and <= 0xEF) && d[j + 1] is >= 0x40 and <= 0xFC and not 0x7F) j += 2;
                if ((j - i) / 2 >= min) runs.Add((i, j - i));
                i = j > i ? j : i + 1;
            }
            if (runs.Count == 0) continue;
            var top = runs.MaxBy(r => r.Len);
            Console.WriteLine($"{p} runs={runs.Count} chars={runs.Sum(r => r.Len) / 2} longest@{top.At:x}: {sjis.GetString(d, top.At, Math.Min(top.Len, 80))}");
        }
        break;
    }
    case "apply":
        Build.ApplyBps(a[1], a[2], a[3]);
        Console.WriteLine("ok");
        break;
    default:
        Console.WriteLine("unknown command"); return 1;
}
return 0;
