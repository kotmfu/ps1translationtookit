using System.Text;

namespace Ps1tl.Port;

/// <summary>
/// A memory card as a standard 128 KB .mcd image (same format as DuckStation/ePSXe saves): 16 blocks of 8 KB,
/// block 0 holds the header and the 15 directory frames. The BIOS file API (bu00:) works on top of this.
/// </summary>
public sealed class MemCard
{
    public const int Size = 0x20000, Frame = 128, BlockSize = 0x2000;
    public readonly byte[] Data;
    readonly string path;

    public MemCard(string path)
    {
        this.path = path;
        Data = File.Exists(path) && new FileInfo(path).Length == Size ? File.ReadAllBytes(path) : Format();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllBytes(path, Data);
    }

    static byte[] Format()
    {
        var d = new byte[Size];
        void Csum(int f) { byte x = 0; for (int i = 0; i < 127; i++) x ^= d[f * Frame + i]; d[f * Frame + 127] = x; }
        d[0] = (byte)'M'; d[1] = (byte)'C'; Csum(0);
        for (int f = 1; f < 16; f++)
        {
            int o = f * Frame;
            d[o] = 0xA0; d[o + 8] = 0xFF; d[o + 9] = 0xFF;
            Csum(f);
        }
        for (int f = 16; f < 36; f++)
        {
            int o = f * Frame;
            d[o] = d[o + 1] = d[o + 2] = d[o + 3] = 0xFF; d[o + 8] = 0xFF; d[o + 9] = 0xFF;
            Csum(f);
        }
        d[63 * Frame] = (byte)'M'; d[63 * Frame + 1] = (byte)'C'; Csum(63);
        return d;
    }

    // ------------------------------------------------------------------ directory

    int Dir(int block) => block * Frame;   // directory frame for data block 1..15
    public uint State(int b) => BitConverter.ToUInt32(Data, Dir(b));
    public string Name(int b) => Encoding.ASCII.GetString(Data, Dir(b) + 10, 21).TrimEnd('\0').Split('\0')[0];
    public int FileSize(int b) => BitConverter.ToInt32(Data, Dir(b) + 4);
    int Next(int b) => BitConverter.ToUInt16(Data, Dir(b) + 8);

    void SetDir(int b, uint state, int size, int next, string name)
    {
        int o = Dir(b);
        Array.Clear(Data, o, Frame);
        BitConverter.GetBytes(state).CopyTo(Data, o);
        BitConverter.GetBytes(size).CopyTo(Data, o + 4);
        BitConverter.GetBytes((ushort)next).CopyTo(Data, o + 8);
        Encoding.ASCII.GetBytes(name).AsSpan(0, Math.Min(name.Length, 20)).CopyTo(Data.AsSpan(o + 10));
        byte x = 0; for (int i = 0; i < 127; i++) x ^= Data[o + i];
        Data[o + 127] = x;
    }

    /// <summary>first block of each file</summary>
    public IEnumerable<int> Files() => Enumerable.Range(1, 15).Where(b => State(b) == 0x51);

    public int Find(string name) => Files().FirstOrDefault(b => Name(b) == name);

    /// <summary>the data blocks of a file in order</summary>
    public List<int> Chain(int first)
    {
        var l = new List<int>();
        for (int b = first; b is >= 1 and <= 15 && l.Count < 15; b = Next(b) + 1)
        {
            l.Add(b);
            if (Next(b) == 0xFFFF) break;
        }
        return l;
    }

    public int Free => Enumerable.Range(1, 15).Count(b => (State(b) & 0xF0) == 0xA0);

    public int Create(string name, int blocks)
    {
        if (blocks < 1 || Find(name) != 0) return 0;
        var free = Enumerable.Range(1, 15).Where(b => (State(b) & 0xF0) == 0xA0).Take(blocks).ToList();
        if (free.Count < blocks) return 0;
        for (int i = 0; i < blocks; i++)
        {
            uint st = i == 0 ? 0x51u : i == blocks - 1 ? 0x53u : 0x52u;
            int next = i == blocks - 1 ? 0xFFFF : free[i + 1] - 1;
            SetDir(free[i], st, i == 0 ? blocks * BlockSize : 0, next, i == 0 ? name : "");
            Array.Clear(Data, free[i] * BlockSize, BlockSize);
        }
        Save();
        return free[0];
    }

    public bool Erase(string name)
    {
        int b = Find(name);
        if (b == 0) return false;
        foreach (var x in Chain(b)) SetDir(x, 0xA0, 0, 0xFFFF, "");
        Save();
        return true;
    }

    /// <summary>byte offset in the image of file position pos</summary>
    public int Offset(int first, int pos)
    {
        var c = Chain(first);
        int k = pos / BlockSize;
        return k < c.Count ? c[k] * BlockSize + pos % BlockSize : -1;
    }
}
