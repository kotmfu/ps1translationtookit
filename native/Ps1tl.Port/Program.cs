using Avalonia;

namespace Ps1tl.Port;

static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        string? cue = args.FirstOrDefault(a => a.EndsWith(".cue", StringComparison.OrdinalIgnoreCase))
            ?? Directory.GetFiles(AppContext.BaseDirectory, "*.cue").FirstOrDefault();
        if (cue == null) { Console.Error.WriteLine("usage: ps1port <game.cue> (or put the .cue/.bin next to ps1port)"); return 1; }
        var flags = args.Where(a => a.StartsWith("--")).ToHashSet();
        var disc = new Ps1tl.Disc(cue);
        var m = new Psx(disc);
        m.Bios.Trace = flags.Contains("--trace");
        Cdrom.Trace = flags.Contains("--cdtrace");
        Gpu.Trace = flags.Contains("--gputrace");
        if (!flags.Contains("--nocard")) m.Bios.Card = new MemCard(Path.ChangeExtension(cue, ".mcd"));
        Boot.Load(m, disc);
        if (args.FirstOrDefault(a => a.StartsWith("--recomp=")) is { } rc)
        {
            var extra = File.Exists("entries.txt") ? File.ReadAllLines("entries.txt").Select(l => Convert.ToUInt32(l.Trim(), 16)) : [];
            Recomp.Run(Boot.BootExe(disc).Data, rc[9..], extra);
            return 0;
        }
        Gpu.Dither = flags.Contains("--dither");
        if (!flags.Contains("--interp") && !Recompiled.Register(m, Boot.BootExe(disc).Data))
            Psx.Log("no recompiled code for this executable: running it in the interpreter");
        if (args.FirstOrDefault(a => a.StartsWith("--dis=")) is { } dis)
        {
            var p = dis[6..].Split(',');
            Disasm.Dump(m, Convert.ToUInt32(p[0], 16), p.Length > 1 ? int.Parse(p[1]) : 64);
            return 0;
        }
        if (flags.Contains("--headless"))
        {
            var shots = (args.FirstOrDefault(a => a.StartsWith("--shots="))?[8..] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToHashSet();
            int frames = int.TryParse(args.FirstOrDefault(a => a.StartsWith("--frames="))?[9..], out var f) ? f : 600;
            // --press=frame:button[+button],... holds the buttons for 6 frames from that frame
            var presses = (args.FirstOrDefault(a => a.StartsWith("--press="))?[8..] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Split(':')).Select(x => (Frame: int.Parse(x[0]), Bits: x[1].Split('+').Aggregate(0, (b, n) => b | 1 << Array.IndexOf(PadNames, n)))).ToList();
            var wav = flags.Contains("--wav") ? new WavOut("audio.wav") : null;
            if (wav != null) m.Spu.Sink = wav.Write;
            m.OnVBlank = () =>
            {
                int bits = 0;
                foreach (var (pf, pb) in presses) if (m.Frame >= pf && m.Frame < pf + 6) bits |= pb;
                m.Buttons = (ushort)~bits;
                if (shots.Contains(m.Frame)) Boot.DumpFrame(m, $"shot{m.Frame}.png");
                if (m.Frame % 60 == 0) Psx.Log($"-- frame {m.Frame} pc={m.Pc:X8} ra={m.R[31]:X8}");
                if (m.Frame >= frames) throw new StopRun();
            };
            try { Boot.Run(m); } catch (StopRun) { }
            wav?.Dispose();
            Boot.Report(m);
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
    static readonly string[] PadNames = ["select", "l3", "r3", "start", "up", "right", "down", "left", "l2", "r2", "l1", "r1", "triangle", "circle", "cross", "square"];
}

sealed class PortApp : Avalonia.Application
{
    public override void Initialize() => Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime d)
            d.MainWindow = new GameWindow(Program.Game!, Program.Title);
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
