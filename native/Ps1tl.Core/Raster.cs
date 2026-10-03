using System.Buffers.Binary;
using System.IO.Compression;

namespace Ps1tl;

/// <summary>Small RGBA image: previews, Claude sheets, PNG encoding. No imaging dependency needed.</summary>
public sealed class Raster
{
    public int W { get; }
    public int H { get; }
    public uint[] Px { get; }   // 0xAARRGGBB

    public Raster(int w, int h, uint fill = 0xFF000000)
    {
        W = Math.Max(1, w); H = Math.Max(1, h);
        Px = new uint[W * H];
        Array.Fill(Px, fill);
    }

    public static uint Rgb(int r, int g, int b, int a = 255) => (uint)(a << 24 | r << 16 | g << 8 | b);

    public void Set(int x, int y, uint c) { if (x >= 0 && y >= 0 && x < W && y < H) Px[y * W + x] = c; }
    public uint Get(int x, int y) => Px[y * W + x];

    public void FillRect(int x0, int y0, int w, int h, uint c)
    {
        for (int y = y0; y < y0 + h; y++) for (int x = x0; x < x0 + w; x++) Set(x, y, c);
    }

    /// <summary>alpha-composite src at (x0, y0)</summary>
    public void Blit(Raster src, int x0, int y0)
    {
        for (int y = 0; y < src.H; y++)
            for (int x = 0; x < src.W; x++)
            {
                uint c = src.Px[y * src.W + x];
                uint a = c >> 24;
                if (a == 255) Set(x0 + x, y0 + y, c);
                else if (a > 0 && x0 + x >= 0 && y0 + y >= 0 && x0 + x < W && y0 + y < H)
                {
                    uint d = Get(x0 + x, y0 + y);
                    uint Mix(int sh) => ((c >> sh & 255) * a + (d >> sh & 255) * (255 - a)) / 255;
                    Set(x0 + x, y0 + y, 0xFF000000 | Mix(16) << 16 | Mix(8) << 8 | Mix(0));
                }
            }
    }

    public Raster Scale(int s)
    {
        var r = new Raster(W * s, H * s);
        for (int y = 0; y < r.H; y++) for (int x = 0; x < r.W; x++) r.Px[y * r.W + x] = Px[y / s * W + x / s];
        return r;
    }

    /// <summary>text in a built-in pixel font (labels on sheets), 1px spacing</summary>
    public void Text(int x, int y, string text, uint color, Font? font = null)
    {
        font ??= Font.Load("en_small");
        foreach (var ch in text)
        {
            var rows = font[ch];
            for (int ry = 0; ry < rows.Length; ry++)
                for (int rx = 0; rx < rows[ry].Length; rx++)
                    if (rows[ry][rx] == '#') Set(x + rx, y + ry, color);
            x += rows.Max(r => r.Length) + 1;
        }
    }

    public byte[] Png()
    {
        var raw = new byte[H * (W * 4 + 1)];
        for (int y = 0; y < H; y++)
        {
            int o = y * (W * 4 + 1);
            for (int x = 0; x < W; x++)
            {
                uint c = Px[y * W + x];
                raw[o + 1 + x * 4] = (byte)(c >> 16); raw[o + 2 + x * 4] = (byte)(c >> 8);
                raw[o + 3 + x * 4] = (byte)c; raw[o + 4 + x * 4] = (byte)(c >> 24);
            }
        }
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, W); BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), H);
        ihdr[8] = 8; ihdr[9] = 6;   // 8-bit RGBA
        Chunk(ms, "IHDR", ihdr);
        using (var z = new MemoryStream())
        {
            using (var zs = new ZLibStream(z, CompressionLevel.Fastest, true)) zs.Write(raw);
            Chunk(ms, "IDAT", z.ToArray());
        }
        Chunk(ms, "IEND", []);
        return ms.ToArray();
    }

    static void Chunk(Stream s, string type, byte[] data)
    {
        Span<byte> n = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(n, data.Length); s.Write(n);
        var td = new byte[4 + data.Length];
        System.Text.Encoding.ASCII.GetBytes(type, td); data.CopyTo(td, 4);
        s.Write(td);
        BinaryPrimitives.WriteUInt32BigEndian(n, Crc32.Of(td)); s.Write(n);
    }
}
