namespace Ps1tl;

/// <summary>CD-ROM Mode 2 Form 1 sector EDC/ECC.</summary>
public static class SectorEcc
{
    static readonly byte[] F = new byte[256], B = new byte[256];
    static readonly uint[] Edc = new uint[256];

    static SectorEcc()
    {
        for (int i = 0; i < 256; i++)
        {
            int j = ((i << 1) ^ ((i & 0x80) != 0 ? 0x11D : 0)) & 0xFF;
            F[i] = (byte)j; B[i ^ j] = (byte)i;
            uint e = (uint)i;
            for (int k = 0; k < 8; k++) e = (e >> 1) ^ ((e & 1) != 0 ? 0xD8018001 : 0);
            Edc[i] = e;
        }
    }

    static uint EdcOf(ReadOnlySpan<byte> data)
    {
        uint crc = 0;
        foreach (var x in data) crc = (crc >> 8) ^ Edc[(crc ^ x) & 0xFF];
        return crc;
    }

    static void EccBlock(ReadOnlySpan<byte> src, int majorCount, int minorCount, int majorMult, int minorInc, Span<byte> dst)
    {
        int size = majorCount * minorCount;
        for (int major = 0; major < majorCount; major++)
        {
            int index = (major >> 1) * majorMult + (major & 1);
            byte a = 0, b = 0;
            for (int m = 0; m < minorCount; m++)
            {
                byte t = src[index];
                index += minorInc;
                if (index >= size) index -= size;
                a ^= t; b ^= t; a = F[a];
            }
            a = B[F[a] ^ b];
            dst[major] = a; dst[major + majorCount] = (byte)(a ^ b);
        }
    }

    /// <summary>one 2352-byte Mode 2 Form 1 sector with sync/header/subheader/data set: fills EDC + ECC in place</summary>
    public static void FixForm1(Span<byte> s)
    {
        uint e = EdcOf(s.Slice(16, 8 + 2048));
        s[0x818] = (byte)e; s[0x819] = (byte)(e >> 8); s[0x81A] = (byte)(e >> 16); s[0x81B] = (byte)(e >> 24);
        Span<byte> hdr = stackalloc byte[4];
        s.Slice(12, 4).CopyTo(hdr);
        s.Slice(12, 4).Clear();                       // Mode 2: ECC computed with the header zeroed
        EccBlock(s[12..], 86, 24, 2, 86, s.Slice(0x81C, 172));
        EccBlock(s[12..], 52, 43, 86, 88, s.Slice(0x8C8, 104));
        hdr.CopyTo(s.Slice(12, 4));
    }

    /// <summary>header address bytes (BCD minute/second/frame, +150 pregap)</summary>
    public static byte[] Msf(int lba)
    {
        lba += 150;
        static byte Bcd(int v) => (byte)((v / 10) << 4 | v % 10);
        return [Bcd(lba / 4500), Bcd(lba / 75 % 60), Bcd(lba % 75)];
    }
}
