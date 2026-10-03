using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Ps1tl;

/// <summary>Write a patched bin/cue (+ BPS patch) with replaced ISO files.</summary>
public static class Build
{
    const int S = Disc.Sector;
    static readonly byte[] Sync = [0, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0];

    static byte[] ReadRaw(FileStream f, int lba, int n)
    {
        var b = new byte[n * S];
        f.Seek((long)lba * S, SeekOrigin.Begin);
        f.ReadExactly(b);
        return b;
    }

    /// <summary>write data as Mode 2 Form 1 sectors starting at lba, reusing the old subheaders</summary>
    static void WriteFileSectors(FileStream f, int lba, byte[] data, List<byte[]> subheaders)
    {
        int n = (data.Length + 2047) / 2048;
        const int Chunk = 8192;
        for (int c = 0; c < n; c += Chunk)
        {
            int k = Math.Min(Chunk, n - c);
            var s = new byte[k * S];
            Parallel.For(0, k, j =>
            {
                int i = c + j;
                var sec = s.AsSpan(j * S, S);
                byte[] sub = i < subheaders.Count - 1 ? subheaders[Math.Min(i, subheaders.Count - 1)]
                           : i == n - 1 ? [0, 0, 0x89, 0, 0, 0, 0x89, 0] : [0, 0, 0x08, 0, 0, 0, 0x08, 0];
                Sync.CopyTo(sec);
                SectorEcc.Msf(lba + i).CopyTo(sec[12..]);
                sec[15] = 2;
                sub.CopyTo(sec[16..]);
                int from = i * 2048, len = Math.Max(0, Math.Min(2048, data.Length - from));
                data.AsSpan(from, len).CopyTo(sec[24..]);
                SectorEcc.FixForm1(sec);
            });
            f.Seek((long)(lba + c) * S, SeekOrigin.Begin);
            f.Write(s);
        }
    }

    static void FixAndWrite(FileStream f, int lba, byte[] raw)
    {
        SectorEcc.FixForm1(raw);
        f.Seek((long)lba * S, SeekOrigin.Begin);
        f.Write(raw);
    }

    static void PutBoth(byte[] raw, int p, int v)
    {
        BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(p), v);
        BinaryPrimitives.WriteInt32BigEndian(raw.AsSpan(p + 4), v);
    }

    /// <summary>patch an ISO9660 directory record (both-endian LBA + size) and refresh that sector's ECC</summary>
    static void SetRecord(FileStream f, Disc disc, string path, int lba, int size)
    {
        var (rlba, off) = disc.Records[path];
        var raw = ReadRaw(f, rlba, 1);
        PutBoth(raw, 24 + off + 2, lba);
        PutBoth(raw, 24 + off + 10, size);
        FixAndWrite(f, rlba, raw);
    }

    /// <summary>relocate a file's raw sectors (any form) to endLba; only header addresses change</summary>
    static int MoveToEnd(FileStream f, Disc disc, string path, int endLba)
    {
        var (lba, size) = disc.Files()[path];
        int n = (size + 2047) / 2048;
        for (int c = 0; c < n; c += 4096)
        {
            int k = Math.Min(4096, n - c);
            var raw = ReadRaw(f, lba + c, k);
            for (int j = 0; j < k; j++) SectorEcc.Msf(endLba + c + j).CopyTo(raw, j * S + 12);
            f.Seek((long)(endLba + c) * S, SeekOrigin.Begin);
            f.Write(raw);
        }
        SetRecord(f, disc, path, endLba, size);
        return endLba + n;
    }

    static void SetVolumeSize(FileStream f, int nSectors)
    {
        var raw = ReadRaw(f, 16, 1);
        PutBoth(raw, 24 + 80, nSectors);
        FixAndWrite(f, 16, raw);
    }

    /// <summary>copy the disc, replace ISO files {path: data} -> (new cue path, new bin path)</summary>
    public static (string Cue, string Bin) WritePatched(string cue, IReadOnlyDictionary<string, byte[]> repl, string outDir, string name)
    {
        using var disc = new Disc(cue);
        Directory.CreateDirectory(outDir);
        var outBin = Path.Combine(outDir, name + ".bin");
        File.Copy(disc.Path, outBin, true);
        using (var f = new FileStream(outBin, FileMode.Open, FileAccess.ReadWrite))
            foreach (var (path, data) in repl) Replace(f, disc, path, data);
        var outCue = Path.Combine(outDir, name + ".cue");
        File.WriteAllText(outCue, $"FILE \"{name}.bin\" BINARY\n  TRACK 01 MODE2/2352\n    INDEX 01 00:00:00\n");
        return (outCue, outBin);
    }

    static void Replace(FileStream f, Disc disc, string path, byte[] data)
    {
        var files = disc.Files();
        var (lba, size) = files[path];
        int oldN = (size + 2047) / 2048, newN = (data.Length + 2047) / 2048;
        var subs = Enumerable.Range(0, oldN).Select(i => ReadRaw(f, lba + i, 1)[16..24]).ToList();
        if (newN > oldN)
        {
            // make room: move files that start inside the new extent to the end of the image
            int end = (int)(f.Length / S);
            foreach (var (l, p) in files.Where(kv => lba < kv.Value.Lba && kv.Value.Lba < lba + newN)
                                        .Select(kv => (kv.Value.Lba, kv.Key)).OrderBy(t => t.Lba).ThenBy(t => t.Key, StringComparer.Ordinal))
                end = MoveToEnd(f, disc, p, end);
            SetVolumeSize(f, end);
        }
        WriteFileSectors(f, lba, data, subs);
        SetRecord(f, disc, path, lba, data.Length);
    }

    // --- BPS -----------------------------------------------------------------------------------------------
    static void Varint(Stream o, long n)
    {
        while (true)
        {
            int x = (int)(n & 0x7F); n >>= 7;
            if (n == 0) { o.WriteByte((byte)(0x80 | x)); return; }
            o.WriteByte((byte)x); n--;
        }
    }

    static Int128 Key(ReadOnlySpan<byte> sectorBody)
    {
        Span<byte> h = stackalloc byte[16];
        MD5.HashData(sectorBody, h);
        return BinaryPrimitives.ReadInt128LittleEndian(h);
    }

    /// <summary>BPS delta patch, sector-aware: unchanged sectors -> SourceRead, sectors whose content moved
    /// (same bytes apart from the 16-byte sync/address header) -> header literal + SourceCopy, rest literal.</summary>
    public static void WriteBps(string orig, string patched, string outPath)
    {
        const int Ch = 4096;
        var index = new Dictionary<Int128, long>();
        using (var a = File.OpenRead(orig))
        {
            var buf = new byte[Ch * S]; long lba = 0; int got;
            while ((got = a.ReadAtLeast(buf, buf.Length, false)) > 0)
            {
                for (int j = 0; j < got / S; j++) index.TryAdd(Key(buf.AsSpan(j * S + 16, S - 16)), lba + j);
                lba += got / S;
            }
        }
        long srcSize = new FileInfo(orig).Length, dstSize = new FileInfo(patched).Length;
        using var body = new MemoryStream();
        int kind = 0; long count = 0; long rel = 0;   // kind: 0 none, 1 read, 2 literal
        var lit = new MemoryStream();
        void Flush()
        {
            if (kind == 1) Varint(body, ((count - 1) << 2) | 0);
            else if (kind == 2) { Varint(body, ((lit.Length - 1) << 2) | 1); lit.WriteTo(body); lit.SetLength(0); }
            kind = 0; count = 0;
        }
        void SourceRead(int n) { if (kind != 1) { Flush(); kind = 1; } count += n; }
        void Literal(ReadOnlySpan<byte> b) { if (kind != 2) { Flush(); kind = 2; } lit.Write(b); }
        void SourceCopy(long off, int n)
        {
            Flush();
            long d = off - rel;
            Varint(body, ((long)(n - 1) << 2) | 2);
            Varint(body, (Math.Abs(d) << 1) | (d < 0 ? 1L : 0L));
            rel = off + n;
        }
        using (var a = File.OpenRead(orig))
        using (var b = File.OpenRead(patched))
        {
            var ybuf = new byte[Ch * S]; var xbuf = new byte[Ch * S]; int yn;
            while ((yn = b.ReadAtLeast(ybuf, ybuf.Length, false)) > 0)
            {
                int xn = a.ReadAtLeast(xbuf, yn, false);
                int k = yn / S;
                for (int j = 0; j < k; j++)
                {
                    var sec = ybuf.AsSpan(j * S, S);
                    if ((j + 1) * S <= xn && sec.SequenceEqual(xbuf.AsSpan(j * S, S))) { SourceRead(S); continue; }
                    if (!index.TryGetValue(Key(sec[16..]), out var src)) { Literal(sec); continue; }
                    Literal(sec[..16]); SourceCopy(src * S + 16, S - 16);
                }
                if (yn % S != 0) Literal(ybuf.AsSpan(k * S, yn % S));
            }
        }
        Flush();
        uint sc = Crc32.Of(orig), tc = Crc32.Of(patched);
        using var patch = new MemoryStream();
        patch.Write("BPS1"u8);
        Varint(patch, srcSize); Varint(patch, dstSize); Varint(patch, 0);
        body.WriteTo(patch);
        Span<byte> w = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(w, sc); patch.Write(w);
        BinaryPrimitives.WriteUInt32LittleEndian(w, tc); patch.Write(w);
        BinaryPrimitives.WriteUInt32LittleEndian(w, Crc32.Of(patch.GetBuffer().AsSpan(0, (int)patch.Length))); patch.Write(w);
        File.WriteAllBytes(outPath, patch.ToArray());
    }

    /// <summary>reference BPS applier (used to verify patches)</summary>
    public static void ApplyBps(string orig, string patchPath, string outPath)
    {
        var p = File.ReadAllBytes(patchPath);
        if (!p.AsSpan(0, 4).SequenceEqual("BPS1"u8) || Crc32.Of(p.AsSpan(0, p.Length - 4)) != BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(p.Length - 4)))
            throw new InvalidDataException("bad patch");
        int pos = 4;
        long Vi()
        {
            long d = 0, s = 1;
            while (true)
            {
                int x = p[pos++];
                d += (x & 0x7F) * s;
                if ((x & 0x80) != 0) return d;
                s <<= 7; d += s;
            }
        }
        var src = File.ReadAllBytes(orig);
        long ssz = Vi(), tsz = Vi(), msz = Vi(); pos += (int)msz;
        var t = new byte[tsz]; long tl = 0, srel = 0, trel = 0;
        while (pos < p.Length - 12)
        {
            long x = Vi(); int cmd = (int)(x & 3); int n = (int)((x >> 2) + 1);
            if (cmd == 0) { Array.Copy(src, tl, t, tl, n); tl += n; }
            else if (cmd == 1) { Array.Copy(p, pos, t, tl, n); pos += n; tl += n; }
            else
            {
                long d = Vi(); d = (d & 1) != 0 ? -(d >> 1) : d >> 1;
                if (cmd == 2) { srel += d; Array.Copy(src, srel, t, tl, n); srel += n; tl += n; }
                else { trel += d; for (int i = 0; i < n; i++) t[tl++] = t[trel++]; }
            }
        }
        if (tl != tsz || Crc32.Of(t) != BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(p.Length - 8)))
            throw new InvalidDataException("target mismatch");
        File.WriteAllBytes(outPath, t);
    }
}

/// <summary>zlib-compatible CRC-32</summary>
public static class Crc32
{
    static readonly uint[] T = Enumerable.Range(0, 256).Select(i =>
    {
        uint c = (uint)i;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    public static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        crc = ~crc;
        foreach (var b in data) crc = T[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return ~crc;
    }

    public static uint Of(ReadOnlySpan<byte> data) => Update(0, data);

    public static uint Of(string path)
    {
        using var f = File.OpenRead(path);
        var buf = new byte[1 << 22]; uint c = 0; int n;
        while ((n = f.Read(buf)) > 0) c = Update(c, buf.AsSpan(0, n));
        return c;
    }
}
