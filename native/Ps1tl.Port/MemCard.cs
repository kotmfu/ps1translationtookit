using System.Text;

namespace Ps1tl.Port;

/// <summary>
/// The game's saves. The game still sees a memory card (BIOS card/file API on a 128 KB card layout in memory),
/// but there is no card image on disk: each save the game writes becomes its own file in the saves folder,
/// and those files are what the card is built from at start-up.
/// </summary>
public sealed class MemCard
{
    public const int Size = 0x20000, Frame = 128, BlockSize = 0x2000;
    public readonly byte[] Data;
    readonly string dir;
    readonly Dictionary<string, byte[]> written = new();   // what is on disk, by save name
    int dirtyFrames = -1;   // vblanks since the last change, -1 = nothing to write

    public MemCard(string saveDir, string? importImage = null)
    {
        dir = saveDir;
        Data = Format();
        Directory.CreateDirectory(dir);
        var files = Directory.GetFiles(dir, "*.sav");
        if (files.Length == 0 && importImage != null && File.Exists(importImage) && new FileInfo(importImage).Length == Size)
        {
            // first run after the .mcd era: take its saves over
            var old = new MemCard(File.ReadAllBytes(importImage));
            foreach (var b in old.Files()) Add(old.Name(b), old.Read(b));
            dirtyFrames = 0;
            Flush();
            return;
        }
        foreach (var f in files)
        {
            var bytes = File.ReadAllBytes(f);
            var name = Path.GetFileNameWithoutExtension(f);
            if (Add(name, bytes)) written[name] = bytes;
            else Psx.Log($"saves: no room for {name}");
        }
    }

    MemCard(byte[] image) { dir = ""; Data = image; }

    bool Add(string name, byte[] bytes)
    {
        int b = Create(name, Math.Max(1, (bytes.Length + BlockSize - 1) / BlockSize), persist: false);
        if (b == 0) return false;
        for (int i = 0; i < bytes.Length; i++) Data[Offset(b, i)] = bytes[i];
        return true;
    }

    /// <summary>a save's contents</summary>
    public byte[] Read(int first)
    {
        var c = Chain(first);
        var d = new byte[c.Count * BlockSize];
        for (int i = 0; i < c.Count; i++) Array.Copy(Data, c[i] * BlockSize, d, i * BlockSize, BlockSize);
        return d;
    }

    /// <summary>the game changed the card: written to disk once it has been quiet for half a second</summary>
    public void Save() => dirtyFrames = 0;

    public void Tick() { if (dirtyFrames >= 0 && ++dirtyFrames >= 30) Flush(); }

    /// <summary>write changed saves to their files (atomically), delete files of erased saves</summary>
    public void Flush()
    {
        if (dirtyFrames < 0 || dir.Length == 0) return;
        dirtyFrames = -1;
        var now = Files().ToDictionary(Name, Read);
        foreach (var (name, bytes) in now)
        {
            if (written.TryGetValue(name, out var old) && old.AsSpan().SequenceEqual(bytes)) continue;
            var path = Path.Combine(dir, Safe(name) + ".sav");
            File.WriteAllBytes(path + ".tmp", bytes);
            File.Move(path + ".tmp", path, overwrite: true);
            written[name] = bytes;
            Psx.Log($"saved {name}");
        }
        foreach (var name in written.Keys.Except(now.Keys).ToList())
        {
            File.Delete(Path.Combine(dir, Safe(name) + ".sav"));
            written.Remove(name);
        }
    }

    static string Safe(string name) => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

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

    public int Create(string name, int blocks, bool persist = true)
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
        if (persist) Save();
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

    /// <summary>--selftest: a save written through the card layout comes back from its file, and erasing deletes it</summary>
    public static void SelfTest()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ps1port-selftest");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        var c = new MemCard(dir);
        int b = c.Create("BISLPS-02274TEST", 2);
        for (int i = 0; i < 2 * BlockSize; i++) c.Data[c.Offset(b, i)] = (byte)(i * 7);
        c.Save();
        for (int i = 0; i < 30; i++) c.Tick();
        var file = Path.Combine(dir, "BISLPS-02274TEST.sav");
        if (!File.Exists(file) || new FileInfo(file).Length != 2 * BlockSize) throw new Exception("save not written");
        var r = new MemCard(dir);
        int rb = r.Find("BISLPS-02274TEST");
        if (rb == 0 || r.Read(rb)[12345] != unchecked((byte)(12345 * 7))) throw new Exception("save not read back");
        r.Erase("BISLPS-02274TEST");
        r.Flush();
        if (File.Exists(file)) throw new Exception("erased save still on disk");
        Directory.Delete(dir, true);
        Psx.Log("saves selftest ok");
    }
}
