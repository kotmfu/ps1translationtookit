using System.Buffers.Binary;

namespace Ps1tl;

/// <summary>
/// FLB 2.00 archive (Spike engine; Yuuyami Doori Tankentai).
/// header: +00 'FLB\x90' +04 '2.00' +0C dir-table off +10 data off +14 entry count
///         +18 subdir count +1C data size; entries at +28: (offset | subdir index, tag).
/// tag >> 24: 0/1 = file at data+offset, >=2 = subdir (16-byte (off, count, 0, size) record in dir table).
/// Children sit back to back in the data area; a file's span runs to the next item (keeps original padding).
/// </summary>
public static class Flb
{
    /// <summary>size changes are rounded up to whole sectors so everything after a change moves by whole
    /// sectors; a delta patch (BPS) can then express the shifted data as cheap copies</summary>
    public const int Align = 2048;

    static int U32(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadInt32LittleEndian(b[o..]);
    static void Put(Span<byte> b, int o, int v) => BinaryPrimitives.WriteInt32LittleEndian(b[o..], v);

    /// <summary>'/008/137/018' -> '8/137/18' (the label a diagnostic build shows)</summary>
    public static string Short(string path) => string.Join('/', path.Trim('/').Split('/').Select(x => int.Parse(x).ToString()));
    /// <summary>'8/137/18' -> '/008/137/018'</summary>
    public static string Long(string label) => "/" + string.Join('/', label.Split('/').Select(x => int.Parse(x).ToString("000")));

    record struct Item(int Index, bool IsDir, int Offset);

    static (int toff, int doff, int total, List<Item> items, List<Item> order, Dictionary<int, (int s, int e)> spans) Items(byte[] b)
    {
        int toff = U32(b, 12), doff = U32(b, 16), n1 = U32(b, 20), total = U32(b, 28);
        var items = new List<Item>();
        for (int i = 0; i < n1; i++)
        {
            int off = U32(b, 0x28 + i * 8);
            uint tag = (uint)U32(b, 0x2C + i * 8);
            bool dir = tag >> 24 >= 2;
            items.Add(new Item(i, dir, dir ? U32(b, toff + off * 16) : off));
        }
        var order = items.OrderBy(t => t.Offset).ToList();   // stable, like Python's sorted
        var spans = new Dictionary<int, (int, int)>();
        for (int k = 0; k < order.Count; k++)
            spans[order[k].Index] = (order[k].Offset, k + 1 < order.Count ? order[k + 1].Offset : total);
        return (toff, doff, total, items, order, spans);
    }

    static byte[] Child(byte[] b, int doff, int start, int end)
    {
        var c = b.AsSpan(doff + start, end - start);
        return c[..(U32(c, 16) + U32(c, 28))].ToArray();
    }

    /// <summary>every leaf file as (path, data)</summary>
    public static IEnumerable<(string Path, byte[] Data)> Walk(byte[] b, string path = "")
    {
        if (b[0] != 'F' || b[1] != 'L' || b[2] != 'B' || b[3] != 0x90) throw new InvalidDataException($"not an FLB: {path}");
        var (_, doff, _, items, _, spans) = Items(b);
        foreach (var it in items)
        {
            var (s, e) = spans[it.Index];
            var p = $"{path}/{it.Index:000}";
            if (it.IsDir) foreach (var x in Walk(Child(b, doff, s, e), p)) yield return x;
            else yield return (p, b[(doff + s)..(doff + e)]);
        }
    }

    static int Span(int oldSpan, int newLen) =>
        newLen <= oldSpan ? oldSpan : oldSpan + (newLen - oldSpan + Align - 1) / Align * Align;

    /// <summary>new archive bytes with repl = {path: new file data}; unchanged subtrees are copied verbatim.
    /// Every item's span changes by a multiple of Align (files pad with zeros).</summary>
    public static byte[] Rebuild(byte[] b, IReadOnlyDictionary<string, byte[]> repl, string path = "")
    {
        if (!repl.Keys.Any(p => p.StartsWith(path + "/"))) return b;
        var (toff, doff, _, _, order, spans) = Items(b);
        var hdr = b[..doff];
        using var outp = new MemoryStream();
        foreach (var it in order)
        {
            var (s, e) = spans[it.Index];
            var p = $"{path}/{it.Index:000}";
            int pos = (int)outp.Length;
            if (it.IsDir)
            {
                var old = Child(b, doff, s, e);
                var nw = Rebuild(old, repl, p);
                outp.Write(nw);
                outp.Write(new byte[(e - s) - old.Length]);   // keep the original slack; new - old is k * Align
                int rec = toff + U32(hdr, 0x28 + it.Index * 8) * 16;
                Put(hdr, rec, pos);
                Put(hdr, rec + 12, U32(nw, 28));
            }
            else
            {
                if (repl.TryGetValue(p, out var blob))
                {
                    outp.Write(blob);
                    outp.Write(new byte[Span(e - s, blob.Length) - blob.Length]);
                }
                else outp.Write(b, doff + s, e - s);
                Put(hdr, 0x28 + it.Index * 8, pos);
            }
        }
        Put(hdr, 28, (int)outp.Length);
        return [.. hdr, .. outp.ToArray()];
    }
}
