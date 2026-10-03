using System.Security.Cryptography;
using Ps1tl;

// ps1tl-cli: command line for the native core (scripting, CI, and checking it against the Python tool).
//   extract  game.cue out.script.json
//   insert   game.cue project.script.json        -> md5 of the rebuilt FILELINK.FLB + report
//   patch    game.cue project.script.json outdir name
//   apply    original.bin patch.bps out.bin
var a = args;
if (a.Length < 2) { Console.WriteLine("usage: extract|insert|patch|apply ..."); return 1; }
switch (a[0])
{
    case "extract":
    {
        using var disc = new Disc(a[1]);
        var s = Yuuyami.Extract(disc);
        s.Save(a[2]);
        Console.WriteLine($"{s.Lines.Count} lines, {s.Glyphs.Count} glyphs, {s.Images!.Count} pictures -> {a[2]}");
        break;
    }
    case "insert":
    {
        using var disc = new Disc(a[1]);
        var t0 = DateTime.Now;
        var (repl, rep) = Yuuyami.Insert(disc, Script.Load(a[2]), Font.Load());
        foreach (var (p, d) in repl) Console.WriteLine($"{p} {d.Length} {Convert.ToHexStringLower(MD5.HashData(d))}");
        Console.WriteLine($"changed {rep.FilesChanged} skipped {rep.FilesSkipped.Count} english {rep.LinesWithEnglish} ({(DateTime.Now - t0).TotalSeconds:F1}s)");
        break;
    }
    case "patch":
    {
        var t0 = DateTime.Now;
        using var disc = new Disc(a[1]);
        var (repl, rep) = Yuuyami.Insert(disc, Script.Load(a[2]), Font.Load());
        var (cue, bin) = Build.WritePatched(a[1], repl, a[3], a[4]);
        var bps = Path.Combine(a[3], a[4] + ".bps");
        Build.WriteBps(disc.Path, bin, bps);
        Console.WriteLine($"{cue}\n{bps}\nchanged {rep.FilesChanged} skipped {rep.FilesSkipped.Count} ({(DateTime.Now - t0).TotalSeconds:F1}s)");
        break;
    }
    case "llm-test":   // two tiny parallel requests with streaming output: checks the Claude connection
    {
        int chunks = 0;
        Llm.Live = (label, text) => { if (text != null) Interlocked.Increment(ref chunks); };
        var png = new Raster(40, 20, Raster.Rgb(255, 255, 255)).Png();
        const string schema = """{"type":"object","properties":{"c":{"type":"array","items":{"type":"string"}}},"required":["c"],"additionalProperties":false}""";
        var t0 = DateTime.Now;
        Llm.RunParallel(Enumerable.Repeat<Func<System.Text.Json.JsonElement>>(() => Llm.Ask(png, "List 3 colours.", schema, model: a[1], effort: "low"), 2).ToList(),
            () => false, (i, r) => Console.WriteLine($"request {i + 1}: {r}"));
        Console.WriteLine($"{Llm.Backend()} · {chunks} streamed chunks · {(DateTime.Now - t0).TotalSeconds:F1}s");
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
