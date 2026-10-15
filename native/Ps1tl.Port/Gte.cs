namespace Ps1tl.Port;

/// <summary>Geometry transformation engine (cop2): perspective transform, lighting, colour ops; flags as on hardware</summary>
public sealed class Gte
{
    // data registers
    readonly short[] vx = new short[3], vy = new short[3], vz = new short[3];
    byte rgbcR, rgbcG, rgbcB, rgbcC;
    ushort otz;
    short ir0, ir1, ir2, ir3;
    readonly short[] sx = new short[3], sy = new short[3];
    readonly ushort[] sz = new ushort[4];
    readonly uint[] rgbFifo = new uint[3];
    uint res1;
    int mac0, mac1, mac2, mac3;
    uint lzcs; uint lzcr;
    // control registers
    readonly short[,] rt = new short[3, 3], lm = new short[3, 3], lc = new short[3, 3];
    int trX, trY, trZ, rbk, gbk, bbk, rfc, gfc, bfc, ofx, ofy;
    ushort h; short dqa; int dqb; short zsf3, zsf4;
    uint flag;

    public uint Read(int r) => r switch
    {
        0 => (ushort)vx[0] | (uint)(ushort)vy[0] << 16, 1 => (uint)vz[0],
        2 => (ushort)vx[1] | (uint)(ushort)vy[1] << 16, 3 => (uint)vz[1],
        4 => (ushort)vx[2] | (uint)(ushort)vy[2] << 16, 5 => (uint)vz[2],
        6 => rgbcR | (uint)rgbcG << 8 | (uint)rgbcB << 16 | (uint)rgbcC << 24,
        7 => otz, 8 => (uint)ir0, 9 => (uint)ir1, 10 => (uint)ir2, 11 => (uint)ir3,
        12 => Sxy(0), 13 => Sxy(1), 14 => Sxy(2), 15 => Sxy(2),
        16 => sz[0], 17 => sz[1], 18 => sz[2], 19 => sz[3],
        20 => rgbFifo[0], 21 => rgbFifo[1], 22 => rgbFifo[2], 23 => res1,
        24 => (uint)mac0, 25 => (uint)mac1, 26 => (uint)mac2, 27 => (uint)mac3,
        28 or 29 => Orgb(), 30 => lzcs, 31 => lzcr,
        _ => 0,
    };

    uint Sxy(int i) => (ushort)sx[i] | (uint)(ushort)sy[i] << 16;
    uint Orgb() => (uint)(Math.Clamp(ir1 >> 7, 0, 31) | Math.Clamp(ir2 >> 7, 0, 31) << 5 | Math.Clamp(ir3 >> 7, 0, 31) << 10);

    public void Write(int r, uint v)
    {
        switch (r)
        {
            case 0: vx[0] = (short)v; vy[0] = (short)(v >> 16); break;
            case 1: vz[0] = (short)v; break;
            case 2: vx[1] = (short)v; vy[1] = (short)(v >> 16); break;
            case 3: vz[1] = (short)v; break;
            case 4: vx[2] = (short)v; vy[2] = (short)(v >> 16); break;
            case 5: vz[2] = (short)v; break;
            case 6: rgbcR = (byte)v; rgbcG = (byte)(v >> 8); rgbcB = (byte)(v >> 16); rgbcC = (byte)(v >> 24); break;
            case 7: otz = (ushort)v; break;
            case 8: ir0 = (short)v; break;
            case 9: ir1 = (short)v; break;
            case 10: ir2 = (short)v; break;
            case 11: ir3 = (short)v; break;
            case 12: sx[0] = (short)v; sy[0] = (short)(v >> 16); break;
            case 13: sx[1] = (short)v; sy[1] = (short)(v >> 16); break;
            case 14: sx[2] = (short)v; sy[2] = (short)(v >> 16); break;
            case 15: sx[0] = sx[1]; sy[0] = sy[1]; sx[1] = sx[2]; sy[1] = sy[2]; sx[2] = (short)v; sy[2] = (short)(v >> 16); break;
            case 16: sz[0] = (ushort)v; break;
            case 17: sz[1] = (ushort)v; break;
            case 18: sz[2] = (ushort)v; break;
            case 19: sz[3] = (ushort)v; break;
            case 20: rgbFifo[0] = v; break;
            case 21: rgbFifo[1] = v; break;
            case 22: rgbFifo[2] = v; break;
            case 23: res1 = v; break;
            case 24: mac0 = (int)v; break;
            case 25: mac1 = (int)v; break;
            case 26: mac2 = (int)v; break;
            case 27: mac3 = (int)v; break;
            case 28: ir1 = (short)((v & 31) << 7); ir2 = (short)((v >> 5 & 31) << 7); ir3 = (short)((v >> 10 & 31) << 7); break;
            case 30:
                lzcs = v;
                lzcr = (uint)System.Numerics.BitOperations.LeadingZeroCount((int)v < 0 ? ~v : v);
                break;
        }
    }

    public uint ReadCtrl(int r)
    {
        static uint P(short a, short b) => (ushort)a | (uint)(ushort)b << 16;
        return r switch
        {
            0 => P(rt[0, 0], rt[0, 1]), 1 => P(rt[0, 2], rt[1, 0]), 2 => P(rt[1, 1], rt[1, 2]), 3 => P(rt[2, 0], rt[2, 1]), 4 => (uint)rt[2, 2],
            5 => (uint)trX, 6 => (uint)trY, 7 => (uint)trZ,
            8 => P(lm[0, 0], lm[0, 1]), 9 => P(lm[0, 2], lm[1, 0]), 10 => P(lm[1, 1], lm[1, 2]), 11 => P(lm[2, 0], lm[2, 1]), 12 => (uint)lm[2, 2],
            13 => (uint)rbk, 14 => (uint)gbk, 15 => (uint)bbk,
            16 => P(lc[0, 0], lc[0, 1]), 17 => P(lc[0, 2], lc[1, 0]), 18 => P(lc[1, 1], lc[1, 2]), 19 => P(lc[2, 0], lc[2, 1]), 20 => (uint)lc[2, 2],
            21 => (uint)rfc, 22 => (uint)gfc, 23 => (uint)bfc,
            24 => (uint)ofx, 25 => (uint)ofy, 26 => (uint)(short)h, 27 => (uint)dqa, 28 => (uint)dqb,
            29 => (uint)zsf3, 30 => (uint)zsf4,
            31 => flag | ((flag & 0x7F87E000) != 0 ? 0x80000000 : 0),
            _ => 0,
        };
    }

    public void WriteCtrl(int r, uint v)
    {
        void M(short[,] mx, int i, uint w)
        {
            int a = i * 2, b = a + 1;
            mx[a / 3, a % 3] = (short)w;
            if (b < 9) mx[b / 3, b % 3] = (short)(w >> 16);
        }
        switch (r)
        {
            case <= 4: M(rt, r, v); break;
            case 5: trX = (int)v; break;
            case 6: trY = (int)v; break;
            case 7: trZ = (int)v; break;
            case <= 12: M(lm, r - 8, v); break;
            case 13: rbk = (int)v; break;
            case 14: gbk = (int)v; break;
            case 15: bbk = (int)v; break;
            case <= 20: M(lc, r - 16, v); break;
            case 21: rfc = (int)v; break;
            case 22: gfc = (int)v; break;
            case 23: bfc = (int)v; break;
            case 24: ofx = (int)v; break;
            case 25: ofy = (int)v; break;
            case 26: h = (ushort)v; break;
            case 27: dqa = (short)v; break;
            case 28: dqb = (int)v; break;
            case 29: zsf3 = (short)v; break;
            case 30: zsf4 = (short)v; break;
            case 31: flag = v & 0x7FFFF000; break;
        }
    }

    // ---------------------------------------------------------------- helpers

    int sf; bool lmF;

    long A(int i, long v)
    {
        if (v > 0x7FFFFFFFFFFL) flag |= 1u << (30 - i);
        else if (v < -0x80000000000L) flag |= 1u << (27 - i);
        return (v << 20) >> 20;   // 44-bit wrap
    }

    int SetMac(int i, long v)
    {
        v = A(i, v) >> sf;
        int r = (int)v;
        switch (i) { case 1: mac1 = r; break; case 2: mac2 = r; break; case 3: mac3 = r; break; }
        return r;
    }

    short Ir(int i, int v, bool lmFlag)
    {
        int lo = lmFlag ? 0 : -0x8000;
        if (v < lo) { flag |= 1u << (25 - i); return (short)lo; }
        if (v > 0x7FFF) { flag |= 1u << (25 - i); return 0x7FFF; }
        return (short)v;
    }

    void SetIr(int i, int v, bool lmFlag)
    {
        var s = Ir(i, v, lmFlag);
        switch (i) { case 1: ir1 = s; break; case 2: ir2 = s; break; case 3: ir3 = s; break; }
    }

    void Mac0(long v)
    {
        if (v > int.MaxValue) flag |= 1u << 16;
        else if (v < int.MinValue) flag |= 1u << 15;
        mac0 = (int)v;
    }

    void Ir0(long v)
    {
        if (v < 0) { flag |= 1u << 12; ir0 = 0; }
        else if (v > 0x1000) { flag |= 1u << 12; ir0 = 0x1000; }
        else ir0 = (short)v;
    }

    void PushSz(long v)
    {
        if (v < 0) { flag |= 1u << 18; v = 0; } else if (v > 0xFFFF) { flag |= 1u << 18; v = 0xFFFF; }
        sz[0] = sz[1]; sz[1] = sz[2]; sz[2] = sz[3]; sz[3] = (ushort)v;
    }

    void PushSxy(long x, long y)
    {
        if (x < -0x400) { flag |= 1u << 14; x = -0x400; } else if (x > 0x3FF) { flag |= 1u << 14; x = 0x3FF; }
        if (y < -0x400) { flag |= 1u << 13; y = -0x400; } else if (y > 0x3FF) { flag |= 1u << 13; y = 0x3FF; }
        sx[0] = sx[1]; sy[0] = sy[1]; sx[1] = sx[2]; sy[1] = sy[2]; sx[2] = (short)x; sy[2] = (short)y;
    }

    byte Col(int i, int v)
    {
        if (v < 0) { flag |= 1u << (22 - i); return 0; }
        if (v > 255) { flag |= 1u << (22 - i); return 255; }
        return (byte)v;
    }

    void PushColor()
    {
        rgbFifo[0] = rgbFifo[1]; rgbFifo[1] = rgbFifo[2];
        rgbFifo[2] = Col(1, mac1 >> 4) | (uint)Col(2, mac2 >> 4) << 8 | (uint)Col(3, mac3 >> 4) << 16 | (uint)rgbcC << 24;
    }

    static readonly byte[] Unr = BuildUnr();
    static byte[] BuildUnr()
    {
        var t = new byte[257];
        for (int i = 0; i < 257; i++) t[i] = (byte)Math.Max(0, (0x40000 / (i + 0x100) + 1) / 2 - 0x101);
        return t;
    }

    uint Divide()
    {
        uint n = h, d = sz[3];
        if (n >= d * 2) { flag |= 1u << 17; return 0x1FFFF; }
        int z = System.Numerics.BitOperations.LeadingZeroCount((uint)(ushort)d) - 16;
        n <<= z; d <<= z;
        uint u = Unr[(d - 0x7FC0) >> 7] + 0x101u;
        d = (uint)((0x2000080 - (long)d * u) >> 8);
        d = (uint)((0x0000080 + (long)d * u) >> 8);
        return (uint)Math.Min(0x1FFFF, ((long)n * d + 0x8000) >> 16);
    }

    // ---------------------------------------------------------------- commands

    public void Command(uint op)
    {
        flag = 0;
        sf = (op & 0x80000) != 0 ? 12 : 0;
        lmF = (op & 0x400) != 0;
        switch (op & 63)
        {
            case 0x01: Rtp(0, true); break;
            case 0x30: Rtp(0, false); Rtp(1, false); Rtp(2, true); break;
            case 0x06: Mac0((long)sx[0] * sy[1] + (long)sx[1] * sy[2] + (long)sx[2] * sy[0] - (long)sx[0] * sy[2] - (long)sx[1] * sy[0] - (long)sx[2] * sy[1]); break;
            case 0x0C:
                SetMac(1, (long)rt[1, 1] * ir3 - (long)rt[2, 2] * ir2);
                SetMac(2, (long)rt[2, 2] * ir1 - (long)rt[0, 0] * ir3);
                SetMac(3, (long)rt[0, 0] * ir2 - (long)rt[1, 1] * ir1);
                SetIr(1, mac1, lmF); SetIr(2, mac2, lmF); SetIr(3, mac3, lmF);
                break;
            case 0x10: Dpc(rgbcR << 16, rgbcG << 16, rgbcB << 16); break;
            case 0x2A: for (int i = 0; i < 3; i++) { uint c = rgbFifo[0]; Dpc((int)(c & 0xFF) << 16, (int)(c >> 8 & 0xFF) << 16, (int)(c >> 16 & 0xFF) << 16); } break;
            case 0x11: Dpc((long)ir1 << 12, (long)ir2 << 12, (long)ir3 << 12); break;
            case 0x29: Dpc((long)rgbcR * ir1 << 4, (long)rgbcG * ir2 << 4, (long)rgbcB * ir3 << 4); break;
            case 0x12: Mvmva(op); break;
            case 0x13: Ncd(0); break;
            case 0x16: Ncd(0); Ncd(1); Ncd(2); break;
            case 0x14: ColorFromIr(); Dcpl2(); break;
            case 0x1B: Ncc(0); break;
            case 0x3F: Ncc(0); Ncc(1); Ncc(2); break;
            case 0x1C: ColorFromIr(); Cc2(); break;
            case 0x1E: Nc(0); break;
            case 0x20: Nc(0); Nc(1); Nc(2); break;
            case 0x28:
                SetMac(1, (long)ir1 * ir1); SetMac(2, (long)ir2 * ir2); SetMac(3, (long)ir3 * ir3);
                SetIr(1, mac1, lmF); SetIr(2, mac2, lmF); SetIr(3, mac3, lmF);
                break;
            case 0x2D: { long v = (long)zsf3 * (sz[1] + sz[2] + sz[3]); Mac0(v); otz = Otz(v); break; }
            case 0x2E: { long v = (long)zsf4 * (sz[0] + sz[1] + sz[2] + sz[3]); Mac0(v); otz = Otz(v); break; }
            case 0x3D:
                SetMac(1, (long)ir0 * ir1); SetMac(2, (long)ir0 * ir2); SetMac(3, (long)ir0 * ir3);
                SetIr(1, mac1, lmF); SetIr(2, mac2, lmF); SetIr(3, mac3, lmF); PushColor();
                break;
            case 0x3E:
                SetMac(1, ((long)mac1 << sf) + (long)ir0 * ir1); SetMac(2, ((long)mac2 << sf) + (long)ir0 * ir2); SetMac(3, ((long)mac3 << sf) + (long)ir0 * ir3);
                SetIr(1, mac1, lmF); SetIr(2, mac2, lmF); SetIr(3, mac3, lmF); PushColor();
                break;
        }
    }

    ushort Otz(long v)
    {
        v >>= 12;
        if (v < 0) { flag |= 1u << 18; return 0; }
        if (v > 0xFFFF) { flag |= 1u << 18; return 0xFFFF; }
        return (ushort)v;
    }

    void Rtp(int i, bool last)
    {
        long x = A(1, A(1, A(1, ((long)trX << 12) + (long)rt[0, 0] * vx[i]) + (long)rt[0, 1] * vy[i]) + (long)rt[0, 2] * vz[i]);
        long y = A(2, A(2, A(2, ((long)trY << 12) + (long)rt[1, 0] * vx[i]) + (long)rt[1, 1] * vy[i]) + (long)rt[1, 2] * vz[i]);
        long z = A(3, A(3, A(3, ((long)trZ << 12) + (long)rt[2, 0] * vx[i]) + (long)rt[2, 1] * vy[i]) + (long)rt[2, 2] * vz[i]);
        mac1 = (int)(x >> sf); mac2 = (int)(y >> sf); mac3 = (int)(z >> sf);
        SetIr(1, mac1, lmF); SetIr(2, mac2, lmF);
        // IR3 saturation flag uses the unshifted z when sf = 0
        int z12 = (int)(z >> 12);
        if (z12 < (lmF ? 0 : -0x8000) || z12 > 0x7FFF) flag |= 1u << 22;
        ir3 = (short)Math.Clamp(mac3, lmF ? 0 : -0x8000, 0x7FFF);
        PushSz(z >> 12);
        long d = Divide();
        long sxv = (d * ir1 + ofx) >> 16, syv = (d * ir2 + ofy) >> 16;
        if ((d * ir1 + ofx) is > int.MaxValue or < int.MinValue) flag |= 1u << 16;
        PushSxy(sxv, syv);
        if (last)
        {
            long dq = d * dqa + dqb;
            Mac0(dq);
            Ir0(dq >> 12);
        }
    }

    void Mvmva(uint op)
    {
        int mx = (int)(op >> 17 & 3), vv = (int)(op >> 15 & 3), cv = (int)(op >> 13 & 3);
        short[,] mat = mx switch { 0 => rt, 1 => lm, 2 => lc, _ => new short[3, 3] };
        if (mx == 3)
        {
            // garbage matrix as on hardware
            mat[0, 0] = (short)-(rgbcR << 4); mat[0, 1] = (short)(rgbcR << 4); mat[0, 2] = ir0;
            mat[1, 0] = mat[1, 1] = mat[1, 2] = rt[0, 2];
            mat[2, 0] = mat[2, 1] = mat[2, 2] = rt[1, 1];
        }
        short x, y, z;
        switch (vv) { case 3: x = ir1; y = ir2; z = ir3; break; default: x = vx[vv]; y = vy[vv]; z = vz[vv]; break; }
        long tx, ty, tz;
        switch (cv) { case 0: tx = trX; ty = trY; tz = trZ; break; case 1: tx = rbk; ty = gbk; tz = bbk; break; case 2: tx = rfc; ty = gfc; tz = bfc; break; default: tx = ty = tz = 0; break; }
        if (cv == 2)
        {
            // far colour bug: the first column is only used for flags
            SetMac(1, A(1, A(1, (long)mat[0, 1] * y) + (long)mat[0, 2] * z));
            SetMac(2, A(2, A(2, (long)mat[1, 1] * y) + (long)mat[1, 2] * z));
            SetMac(3, A(3, A(3, (long)mat[2, 1] * y) + (long)mat[2, 2] * z));
        }
        else
        {
            SetMac(1, A(1, A(1, (tx << 12) + (long)mat[0, 0] * x) + (long)mat[0, 1] * y) + (long)mat[0, 2] * z);
            SetMac(2, A(2, A(2, (ty << 12) + (long)mat[1, 0] * x) + (long)mat[1, 1] * y) + (long)mat[1, 2] * z);
            SetMac(3, A(3, A(3, (tz << 12) + (long)mat[2, 0] * x) + (long)mat[2, 1] * y) + (long)mat[2, 2] * z);
        }
        SetIr(1, mac1, lmF); SetIr(2, mac2, lmF); SetIr(3, mac3, lmF);
    }

    void MulMat(short[,] mat, long x, long y, long z, long tx = 0, long ty = 0, long tz = 0)
    {
        SetMac(1, A(1, A(1, (tx << 12) + mat[0, 0] * x) + mat[0, 1] * y) + mat[0, 2] * z);
        SetMac(2, A(2, A(2, (ty << 12) + mat[1, 0] * x) + mat[1, 1] * y) + mat[1, 2] * z);
        SetMac(3, A(3, A(3, (tz << 12) + mat[2, 0] * x) + mat[2, 1] * y) + mat[2, 2] * z);
        SetIr(1, mac1, lmF); SetIr(2, mac2, lmF); SetIr(3, mac3, lmF);
    }

    void Light(int i)
    {
        MulMat(lm, vx[i], vy[i], vz[i]);
        MulMat(lc, ir1, ir2, ir3, rbk, gbk, bbk);
    }

    void Nc(int i) { Light(i); PushColor(); }

    void Ncc(int i)
    {
        Light(i);
        ColorFromIr();
        Cc2();
    }

    void Ncd(int i)
    {
        Light(i);
        ColorFromIr();
        Dcpl2();
    }

    /// <summary>MAC = RGBC * IR (<<4)</summary>
    void ColorFromIr()
    {
        // stash for the following step
        cIr1 = ir1; cIr2 = ir2; cIr3 = ir3;
    }
    short cIr1, cIr2, cIr3;

    void Cc2()
    {
        SetMac(1, (long)rgbcR * cIr1 << 4); SetMac(2, (long)rgbcG * cIr2 << 4); SetMac(3, (long)rgbcB * cIr3 << 4);
        SetIr(1, mac1, lmF); SetIr(2, mac2, lmF); SetIr(3, mac3, lmF);
        PushColor();
    }

    void Dcpl2()
    {
        long r = (long)rgbcR * cIr1 << 4, g = (long)rgbcG * cIr2 << 4, b = (long)rgbcB * cIr3 << 4;
        DpcCore(r, g, b);
    }

    void Dpc(long r, long g, long b) => DpcCore(r, g, b);

    /// <summary>interpolate (r,g,b) toward the far colour by IR0, push the colour</summary>
    void DpcCore(long r, long g, long b)
    {
        int s = sf;
        long f1 = A(1, ((long)rfc << 12) - r), f2 = A(2, ((long)gfc << 12) - g), f3 = A(3, ((long)bfc << 12) - b);
        short t1 = Ir(1, (int)(f1 >> s), false), t2 = Ir(2, (int)(f2 >> s), false), t3 = Ir(3, (int)(f3 >> s), false);
        SetMac(1, r + (long)ir0 * t1); SetMac(2, g + (long)ir0 * t2); SetMac(3, b + (long)ir0 * t3);
        SetIr(1, mac1, lmF); SetIr(2, mac2, lmF); SetIr(3, mac3, lmF);
        PushColor();
    }
}
