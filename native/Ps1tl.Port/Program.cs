using Avalonia;

namespace Ps1tl.Port;

static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--selftest")) MemCard.SelfTest();
        string? cue = args.FirstOrDefault(a => a.EndsWith(".cue", StringComparison.OrdinalIgnoreCase))
            ?? Directory.GetFiles(AppContext.BaseDirectory, "*.cue").FirstOrDefault();
        if (cue == null && args.Contains("--selftest")) return 0;
        if (cue == null) { Console.Error.WriteLine("usage: ps1port <game.cue> (or put the .cue/.bin next to ps1port)"); return 1; }
        var flags = args.Where(a => a.StartsWith("--")).ToHashSet();
        var disc = new Ps1tl.Disc(cue);
        var m = new Psx(disc);
        if (flags.Contains("--selftest")) { m.Sdk.Cd.SelfTest(); return 0; }
        m.Bios.Trace = flags.Contains("--trace");
        Cdrom.Trace = flags.Contains("--cdtrace");
        Gpu.Trace = flags.Contains("--gputrace");
        Psx.DmaTrace = flags.Contains("--dmatrace");
        // per-game data folder (saves, controls.json); headless test runs keep theirs in the working directory
        string data = flags.Contains("--headless") ? Path.GetFullPath("portdata")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ps1port", disc.Serial() ?? Path.GetFileNameWithoutExtension(cue));
        DataDir = data;
        m.Overlays.DumpDir = Path.Combine(data, "overlays");
        if (!flags.Contains("--nocard")) m.Bios.Card = new MemCard(Path.Combine(data, "saves"), Path.ChangeExtension(cue, ".mcd"));
        Boot.Load(m, disc);
        if (args.FirstOrDefault(a => a.StartsWith("--recomp=")) is { } rc)
        {
            var extra = File.Exists("entries.txt") ? File.ReadAllLines("entries.txt").Select(l => Convert.ToUInt32(l.Trim(), 16)) : [];
            Recomp.Run(Boot.BootExe(disc).Data, rc[9..], extra, args.FirstOrDefault(a => a.StartsWith("--overlays="))?[11..] ?? Path.Combine(data, "overlays"));
            return 0;
        }
        if (args.FirstOrDefault(a => a.StartsWith("--map=")) is { } mp)
        {
            var extra = File.Exists("entries.txt") ? File.ReadAllLines("entries.txt").Select(l => Convert.ToUInt32(l.Trim(), 16)) : [];
            Recomp.Map(Boot.BootExe(disc).Data, mp[6..], extra);
            return 0;
        }
        if (args.FirstOrDefault(a => a.StartsWith("--trap=")) is { } trap) m.TrapPc = Convert.ToUInt32(trap[7..], 16);
        Gpu.Dither = flags.Contains("--dither");
        if (!flags.Contains("--interp") && !Recompiled.Register(m, Boot.BootExe(disc).Data, overlays: !flags.Contains("--no-overlays")))
            Psx.Log("no recompiled code for this executable: running it in the interpreter");
        PsyQ.Trace = flags.Contains("--sdktrace");
        LibPress.Check = flags.Contains("--vlccheck");
        // native SDK: replaces the hardware-level library code (--emulate-sdk keeps Sony's code on emulated hardware)
        if (!flags.Contains("--emulate-sdk") && Symbols.ByExe.TryGetValue(Ps1tl.Crc32.Of(Boot.BootExe(disc).Data), out var syms))
        {
            // --emulate-cd / -press / -spu / -pad: keep Sony's code for just that library
            string[] keep = [.. flags.Contains("--emulate-cd") ? ["Cd", "St", "@St"] : Array.Empty<string>(),
                             .. flags.Contains("--emulate-press") ? ["DecDCT", "@DecDCT"] : Array.Empty<string>(),
                             .. flags.Contains("--emulate-spu") ? ["_spu", "@_spu"] : Array.Empty<string>(),
                             .. flags.Contains("--emulate-pad") ? ["Pad"] : Array.Empty<string>()];
            Psx.Log($"native SDK: {m.Sdk.Install(syms.Where(k => !keep.Any(k.Key.StartsWith)).ToDictionary())} functions");
        }
        // --dis=addr[,count]: disassemble the loaded executable, or memory at the end of a --headless run (overlays)
        var dis = args.FirstOrDefault(a => a.StartsWith("--dis="));
        if (dis != null && !flags.Contains("--headless"))
        {
            var p = dis[6..].Split(',');
            Disasm.Dump(m, Convert.ToUInt32(p[0], 16), p.Length > 1 ? int.Parse(p[1]) : 64);
            return 0;
        }
        if (flags.Contains("--headless"))
        {
            var shots = (args.FirstOrDefault(a => a.StartsWith("--shots="))?[8..] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToHashSet();
            int frames = int.TryParse(args.FirstOrDefault(a => a.StartsWith("--frames="))?[9..], out var f) ? f : 600;
            // --press=frame:button[+button][:frames],... holds the buttons from that frame (default 6 frames)
            var presses = (args.FirstOrDefault(a => a.StartsWith("--press="))?[8..] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Split(':')).Select(x => (Frame: int.Parse(x[0]), Len: x.Length > 2 ? int.Parse(x[2]) : 6, Bits: x[1].Split('+').Aggregate(0, (b, n) => b | 1 << Array.IndexOf(PadNames, n)))).ToList();
            int prof = int.TryParse(args.FirstOrDefault(a => a.StartsWith("--prof="))?[7..], out var pf0) ? pf0 : -1;
            var wav = flags.Contains("--wav") ? new WavOut("audio.wav") : null;
            if (wav != null) m.Spu.Sink = wav.Write;
            m.OnVBlank = () =>
            {
                int bits = 0;
                foreach (var (pf, pl, pb) in presses) if (m.Frame >= pf && m.Frame < pf + pl) bits |= pb;
                m.Buttons = (ushort)~bits;
                if (m.Frame == prof) m.Prof = new();
                if (shots.Contains(m.Frame)) Boot.DumpFrame(m, $"shot{m.Frame}.png");
                if (m.Frame % 60 == 0) Psx.Log($"-- frame {m.Frame} pc={m.Pc:X8} ra={m.R[31]:X8}{(m.Bios.InException ? " (in interrupt)" : "")}");
                if (m.Frame >= frames) throw new StopRun();
            };
            if (flags.Contains("--ioprof")) m.IoProf = new();
            try { Boot.Run(m); } catch (StopRun) { }
            if (m.IoProf != null) File.WriteAllLines("ioprof.txt", m.IoProf.OrderBy(kv => kv.Key.Pc).Select(kv => $"{kv.Key.Pc:X8} {kv.Key.Reg:X8} {kv.Value}"));
            wav?.Dispose();
            m.Bios.Card?.Flush();
            Boot.Report(m);
            if (flags.Contains("--ram")) File.WriteAllBytes("ram.bin", m.RamArr);
            if (dis != null) { var p = dis[6..].Split(','); Disasm.Dump(m, Convert.ToUInt32(p[0], 16), p.Length > 1 ? int.Parse(p[1]) : 64); }
            Boot.DumpFrame(m, "frame.png");
            Boot.DumpVram(m, "vram.png");
            return 0;
        }
        Game = m;
        Title = Path.GetFileNameWithoutExtension(cue);
        Avalonia.AppBuilder.Configure<PortApp>().UsePlatformDetect().LogToTrace().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static Psx? Game;
    public static string Title = "";
    public static string DataDir = "";
    static readonly string[] PadNames = ["select", "l3", "r3", "start", "up", "right", "down", "left", "l2", "r2", "l1", "r1", "triangle", "circle", "cross", "square"];
}

sealed class PortApp : Avalonia.Application
{
    public override void Initialize() => Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime d)
            d.MainWindow = new GameWindow(Program.Game!, Program.Title, new Input(Path.Combine(Program.DataDir, "controls.json")));
        base.OnFrameworkInitializationCompleted();
    }
}

sealed class StopRun : Exception;

/// <summary>headless audio capture (16-bit stereo 44.1 kHz)</summary>
sealed class WavOut(string path) : IDisposable
{
    readonly FileStream f = File.Create(path);
    readonly BinaryWriter w = new(File.Create(path + ".tmp"));
    long n;
    public void Write(ReadOnlySpan<short> s) { foreach (var x in s) w.Write(x); n += s.Length; }
    public void Dispose()
    {
        w.Flush(); w.BaseStream.Position = 0;
        var bw = new BinaryWriter(f);
        bw.Write("RIFF"u8); bw.Write((int)(36 + n * 2)); bw.Write("WAVEfmt "u8); bw.Write(16); bw.Write((short)1); bw.Write((short)2);
        bw.Write(44100); bw.Write(44100 * 4); bw.Write((short)4); bw.Write((short)16); bw.Write("data"u8); bw.Write((int)(n * 2));
        w.BaseStream.CopyTo(f);
        w.Dispose(); f.Dispose();
        File.Delete(path + ".tmp");
    }
}
