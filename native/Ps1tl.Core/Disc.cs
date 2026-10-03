using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace Ps1tl;

/// <summary>Single-track MODE2/2352 bin/cue reading + ISO9660 listing.</summary>
public sealed class Disc : IDisposable
{
    public const int Sector = 2352;
    public string Path { get; }
    readonly FileStream f;
    /// <summary>path -> (directory sector lba, byte offset of the record in it), filled by Files()</summary>
    public Dictionary<string, (int Lba, int Offset)> Records { get; } = new();

    public Disc(string cue)
    {
        Path = BinPath(cue);
        f = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    }

    public static string BinPath(string cue)
    {
        var text = File.ReadAllText(cue);
        var m = Regex.Match(text, "FILE \"(.+?)\" BINARY");
        if (!m.Success || !text.Contains("MODE2/2352"))
            throw new InvalidDataException("only MODE2/2352 bin/cue is supported");
        return System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(cue))!, m.Groups[1].Value);
    }

    /// <summary>user data (2048 bytes per sector) of `size` bytes starting at lba</summary>
    public byte[] Read(int lba, int size)
    {
        var n = (size + 2047) / 2048;
        var out_ = new byte[n * 2048];
        var raw = new byte[Sector];
        for (int i = 0; i < n; i++)
        {
            f.Seek((long)(lba + i) * Sector, SeekOrigin.Begin);
            f.ReadExactly(raw);
            Buffer.BlockCopy(raw, 24, out_, i * 2048, 2048);
        }
        return size == out_.Length ? out_ : out_[..size];
    }

    /// <summary>{path: (lba, size)} for every file in the ISO9660 tree</summary>
    public Dictionary<string, (int Lba, int Size)> Files()
    {
        var root = Read(16, 2048).AsSpan(156, 34);
        var stack = new Stack<(string, int, int)>();
        stack.Push(("/", BinaryPrimitives.ReadInt32LittleEndian(root[2..]), BinaryPrimitives.ReadInt32LittleEndian(root[10..])));
        var outp = new Dictionary<string, (int, int)>();
        var seen = new HashSet<int>();
        Records.Clear();
        while (stack.Count > 0)
        {
            var (path, lba, size) = stack.Pop();
            if (!seen.Add(lba)) continue;
            var data = Read(lba, size);
            int i = 0;
            while (i < data.Length)
            {
                int l = data[i];
                if (l == 0) { i = (i / 2048 + 1) * 2048; continue; }
                var r = data.AsSpan(i, l);
                var rec = (lba + i / 2048, i % 2048);
                i += l;
                var name = r.Slice(33, r[32]);
                if (name.Length == 1 && name[0] <= 1) continue;
                int elba = BinaryPrimitives.ReadInt32LittleEndian(r[2..]), esz = BinaryPrimitives.ReadInt32LittleEndian(r[10..]);
                var s = Encoding.ASCII.GetString(name).Split(';')[0];
                if ((r[25] & 2) != 0) stack.Push((path + s + "/", elba, esz));
                else { outp[path + s] = (elba, esz); Records[path + s] = rec; }
            }
        }
        return outp;
    }

    public long Sectors => new FileInfo(Path).Length / Sector;

    /// <summary>game serial from SYSTEM.CNF, e.g. 'SLPS_022.74'</summary>
    public string? Serial()
    {
        var (lba, size) = Files()["/SYSTEM.CNF"];
        var m = Regex.Match(Encoding.ASCII.GetString(Read(lba, size)), @"cdrom:\\?([A-Z]{4}_\d{3}\.\d{2})");
        return m.Success ? m.Groups[1].Value : null;
    }

    public void Dispose() => f.Dispose();
}
