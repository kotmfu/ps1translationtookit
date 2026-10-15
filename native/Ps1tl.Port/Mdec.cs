namespace Ps1tl.Port;

/// <summary>Motion decoder: RLE/DCT macroblocks (FMV frames) to 15- or 24-bit pixels, fed and drained by DMA0/1</summary>
public sealed class Mdec(Psx m)
{
    readonly byte[] qY = new byte[64], qC = new byte[64];
    readonly Queue<uint> outq = new();
    readonly List<ushort> input = new();
    int need;          // halfwords still expected for the current command
    int cmd;           // 1 decode, 2 quant tables, 3 scale table
    uint cmdWord;
    int depth; bool signed, bit15;
    int remaining;

    static readonly int[] Zig =
    [
        0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5, 12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51, 58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63,
    ];
    static readonly double[,] Cos = BuildCos();
    static double[,] BuildCos()
    {
        var c = new double[8, 8];
        for (int x = 0; x < 8; x++)
            for (int u = 0; u < 8; u++)
                c[x, u] = (u == 0 ? Math.Sqrt(0.125) : 0.5) * Math.Cos((2 * x + 1) * u * Math.PI / 16);
        return c;
    }

    public void WriteCmd(uint v)
    {
        if (need > 0)
        {
            input.Add((ushort)v); input.Add((ushort)(v >> 16));
            need -= 2;
            if (need <= 0) Run();
            return;
        }
        cmdWord = v;
        if (Gpu.Trace) Psx.Log($"mdec cmd {v:X8}");
        cmd = (int)(v >> 29);
        input.Clear();
        switch (cmd)
        {
            case 1:
                depth = (int)(v >> 27 & 3); signed = (v & 0x04000000) != 0; bit15 = (v & 0x02000000) != 0;
                need = (int)(v & 0xFFFF) * 2;
                remaining = need;
                break;
            case 2: need = (v & 1) != 0 ? 64 : 32; break;
            case 3: need = 64; break;
            default: need = 0; break;
        }
        if (need == 0) Run();
    }

    void Run()
    {
        if (cmd == 2)
        {
            for (int i = 0; i < 64; i++) qY[i] = (byte)(input[i / 2] >> (i & 1) * 8);
            if (input.Count >= 64) for (int i = 0; i < 64; i++) qC[i] = (byte)(input[32 + i / 2] >> (i & 1) * 8);
        }
        else if (cmd == 1) DecodeAll();
        need = 0; remaining = 0;
        m.MdecReady();
    }

    public void WriteCtrl(uint v)
    {
        if ((v & 0x80000000) != 0) { outq.Clear(); input.Clear(); need = 0; }
    }

    public uint ReadData() => outq.Count > 0 ? outq.Dequeue() : 0;
    public int OutWords => outq.Count;

    public uint ReadStat()
    {
        uint s = (outq.Count == 0 ? 0x80000000u : 0) | (need > 0 ? 0x20000000u : 0) | (uint)depth << 25
            | (signed ? 1u << 24 : 0) | (bit15 ? 1u << 23 : 0) | 0x10000000 | (outq.Count > 0 ? 0x08000000u : 0);
        return s | (uint)((need / 2 - 1) & 0xFFFF);
    }

    void DecodeAll()
    {
        int p = 0;
        var cr = new double[64]; var cb = new double[64];
        var y = new double[4][];
        for (int i = 0; i < 4; i++) y[i] = new double[64];
        while (p < input.Count)
        {
            if (depth >= 2)
            {
                if (!Block(ref p, cr, qC) || !Block(ref p, cb, qC)) break;
                bool ok = true;
                for (int i = 0; i < 4 && ok; i++) ok = Block(ref p, y[i], qY);
                if (!ok) break;
                EmitColor(cr, cb, y);
            }
            else
            {
                if (!Block(ref p, y[0], qY)) break;
                EmitMono(y[0]);
            }
        }
    }

    /// <summary>one 8x8 block: RLE -> dequantised coefficients -> IDCT; false at end of data</summary>
    bool Block(ref int p, double[] dst, byte[] q)
    {
        while (p < input.Count && input[p] == 0xFE00) p++;   // padding
        if (p >= input.Count) return false;
        Span<int> coef = stackalloc int[64];
        coef.Clear();
        ushort w = input[p++];
        int qs = w >> 10;
        int k = 0;
        int dc = (short)(w << 6) >> 6;
        coef[0] = qs == 0 ? dc * 2 : dc * q[0];
        while (p < input.Count)
        {
            w = input[p++];
            if (w == 0xFE00) break;
            k += (w >> 10) + 1;
            if (k > 63) break;
            int ac = (short)(w << 6) >> 6;
            coef[Zig[k]] = qs == 0 ? ac * 2 : (ac * q[k] * qs + 4) / 8;
        }
        // separable orthonormal IDCT: a DC of 1024 (512 x quant 2) is full brightness
        Span<double> tmp = stackalloc double[64];
        for (int yy = 0; yy < 8; yy++)
            for (int x = 0; x < 8; x++)
            {
                double s = 0;
                for (int u = 0; u < 8; u++) s += Cos[x, u] * coef[yy * 8 + u];
                tmp[yy * 8 + x] = s;
            }
        for (int x = 0; x < 8; x++)
            for (int yy = 0; yy < 8; yy++)
            {
                double s = 0;
                for (int v = 0; v < 8; v++) s += Cos[yy, v] * tmp[v * 8 + x];
                dst[yy * 8 + x] = s;
            }
        return true;
    }

    readonly List<byte> pending = new();

    void EmitColor(double[] cr, double[] cb, double[][] y)
    {
        Span<uint> px = stackalloc uint[256];
        for (int py = 0; py < 16; py++)
            for (int pxx = 0; pxx < 16; pxx++)
            {
                int blk = (py >= 8 ? 2 : 0) + (pxx >= 8 ? 1 : 0);
                double Y = y[blk][(py & 7) * 8 + (pxx & 7)];
                double R = cr[(py / 2) * 8 + pxx / 2], B = cb[(py / 2) * 8 + pxx / 2];
                int r = Clamp(Y + 1.402 * R), g = Clamp(Y - 0.3437 * B - 0.7143 * R), b = Clamp(Y + 1.772 * B);
                if (!signed) { r ^= 0x80; g ^= 0x80; b ^= 0x80; }
                px[py * 16 + pxx] = (uint)(r & 0xFF | (g & 0xFF) << 8 | (b & 0xFF) << 16);
            }
        if (depth == 3)
        {
            for (int i = 0; i < 256; i += 2)
                outq.Enqueue(To15(px[i]) | To15(px[i + 1]) << 16);
        }
        else
        {
            foreach (var c in px) { pending.Add((byte)c); pending.Add((byte)(c >> 8)); pending.Add((byte)(c >> 16)); }
            Flush();
        }
    }

    void EmitMono(double[] y)
    {
        foreach (var v in y)
        {
            int l = Clamp(v);
            if (!signed) l ^= 0x80;
            pending.Add(depth == 0 ? (byte)((l & 0xFF) >> 4) : (byte)l);
        }
        if (depth == 0)
        {
            for (int i = 0; i + 7 < pending.Count; i += 8)
            {
                uint w = 0;
                for (int j = 0; j < 8; j++) w |= (uint)(pending[i + j] & 15) << (j * 4);
                outq.Enqueue(w);
            }
            pending.Clear();
        }
        else Flush();
    }

    void Flush()
    {
        int n = pending.Count / 4 * 4;
        for (int i = 0; i < n; i += 4) outq.Enqueue((uint)(pending[i] | pending[i + 1] << 8 | pending[i + 2] << 16 | pending[i + 3] << 24));
        pending.RemoveRange(0, n);
    }

    uint To15(uint c) => (c >> 3 & 31) | (c >> 11 & 31) << 5 | (c >> 19 & 31) << 10 | (bit15 ? 0x8000u : 0);

    static int Clamp(double v) => Math.Clamp((int)Math.Round(v), -128, 127);
}
