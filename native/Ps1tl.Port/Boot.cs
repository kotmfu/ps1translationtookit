using System.Text.RegularExpressions;

namespace Ps1tl.Port;

public static class Boot
{
    public const uint Exit = 0xFFFF0004;
    public static uint Entry;

    /// <summary>the boot executable named in SYSTEM.CNF: (path, bytes)</summary>
    public static (string Path, byte[] Data) BootExe(Ps1tl.Disc disc)
    {
        var files = disc.Files();
        var (lba, size) = files["/SYSTEM.CNF"];
        var cnf = System.Text.Encoding.ASCII.GetString(disc.Read(lba, size));
        var name = Regex.Match(cnf, @"cdrom:\\?([^;\s]+)").Groups[1].Value.Replace('\\', '/');
        var (elba, esize) = files["/" + name];
        return (name, disc.Read(elba, esize));
    }

    /// <summary>copy the PS-X EXE into RAM and set up registers the way the BIOS leaves them</summary>
    public static void Load(Psx m, Ps1tl.Disc disc)
    {
        var (_, exe) = BootExe(disc);
        uint U(int o) => BitConverter.ToUInt32(exe, o);
        Entry = U(0x10);
        uint gp = U(0x14), dst = U(0x18), size = U(0x1C), sp = U(0x30);
        exe.AsSpan(0x800, (int)Math.Min(size, exe.Length - 0x800)).CopyTo(m.RamSpan(dst, (int)size));
        // BIOS function tables live in kernel RAM; games locate them via B0:57 / C0 tables but only patch them
        m.Bios.InitKernel();
        m.R[28] = gp;
        m.R[29] = sp != 0 ? sp : 0x801FFF00;
        m.R[30] = m.R[29];
        m.R[31] = Exit;
        m.Sr = 0x401;   // interrupts on, hardware irq line unmasked
        m.IMask = 0;
    }

    public static void Run(Psx m)
    {
        m.Call(Entry);
        Psx.Log("game returned from main");
    }

    public static void Report(Psx m)
    {
        // entry points the interpreter had to run: fed back into the next recompile
        var known = File.Exists("entries.txt") ? File.ReadAllLines("entries.txt").ToHashSet() : [];
        foreach (var a in m.InterpHits.Keys) known.Add($"{a:X8}");
        File.WriteAllLines("entries.txt", known.Order());
        Psx.Log($"funcs {m.Funcs.Count}, frames {m.Frame}, cycles {m.Cycles}, interpreted {m.Interpreted} instructions, pc-starts {m.InterpHits.Count}");
        foreach (var (a, n) in m.InterpHits.OrderByDescending(kv => kv.Value).Take(20)) Psx.Log($"  interp entry {a:X8} x{n}");
        Psx.Log("gp0: " + string.Join(' ', Enumerable.Range(0, 256).Where(i => m.Gpu.Counts[i] > 0).Select(i => $"{i:X2}x{m.Gpu.Counts[i]}")) + " " + m.Gpu.Areas);
        Psx.Log(m.Spu.Debug);
        Psx.Log($"display {m.Gpu.HRes}x{m.Gpu.VRes} at {m.Gpu.DispX},{m.Gpu.DispY} off={m.Gpu.DisplayOff} 24bit={m.Gpu.Rgb24}");
    }

    public static void DumpVram(Psx m, string path)
    {
        var px = new uint[1024 * 512];
        for (int i = 0; i < px.Length; i++)
        {
            ushort p = m.Gpu.Vram[i];
            px[i] = (uint)((p & 31) << 19 | (p >> 5 & 31) << 11 | (p >> 10 & 31) << 3);
        }
        Png(path, px, 1024, 512);
    }

    public static void DumpFrame(Psx m, string path)
    {
        var buf = new uint[640 * 512];
        var (w, h) = m.Gpu.Render(buf);
        Png(path, buf, w, h);
    }

    /// <summary>0xRRGGBB pixels -> PNG file</summary>
    public static void Png(string path, uint[] px, int w, int h)
    {
        var raw = new MemoryStream();
        for (int y = 0; y < h; y++)
        {
            raw.WriteByte(0);
            for (int x = 0; x < w; x++) { uint c = px[y * w + x]; raw.WriteByte((byte)(c >> 16)); raw.WriteByte((byte)(c >> 8)); raw.WriteByte((byte)c); }
        }
        var z = new MemoryStream();
        using (var zs = new System.IO.Compression.ZLibStream(z, System.IO.Compression.CompressionLevel.Fastest, true)) raw.WriteTo(zs);
        using var f = File.Create(path);
        f.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        void Chunk(string type, byte[] d)
        {
            var be = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(be, d.Length); f.Write(be);
            var td = System.Text.Encoding.ASCII.GetBytes(type).Concat(d).ToArray();
            f.Write(td);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(be, Ps1tl.Crc32.Of(td)); f.Write(be);
        }
        var ih = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ih, w);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ih.AsSpan(4), h);
        ih[8] = 8; ih[9] = 2;
        Chunk("IHDR", ih);
        Chunk("IDAT", z.ToArray());
        Chunk("IEND", []);
    }
}
