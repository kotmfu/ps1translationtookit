namespace Ps1tl.Port;

/// <summary>
/// The PS1 GPU in software: 1 MB of VRAM (1024x512 15-bit), GP0 drawing commands (polygons, rectangles, lines,
/// transfers), GP1 display control, and conversion of the displayed area to a 32-bit frame for the front end.
/// </summary>
public sealed class Gpu(Psx m)
{
    public readonly ushort[] Vram = new ushort[1024 * 512];

    // draw state
    int texX, texY, semi, depth;
    bool dither, drawToDisplay, texDisable, flipX, flipY, setMask, checkMask;
    int twMaskX, twMaskY, twOffX, twOffY;
    int areaL, areaT, areaR, areaB, offX, offY;

    // display state
    public int DispX, DispY, HRes = 320, VRes = 240;
    public bool Rgb24, Interlace, DisplayOff = true, Pal;
    int hStart = 0x260, hEnd = 0xC60, vStart = 0x10, vEnd = 0x100;
    uint dmaDir, stat31;
    bool irq;

    // command assembly
    readonly uint[] cmd = new uint[16];
    int cmdLen, cmdNeed;
    // CPU->VRAM / VRAM->CPU transfers
    int xferX, xferY, xferW, xferH, xferI, xferLeft;
    bool toVram, fromVram;
    // polyline
    bool polyline; int polyStage;
    uint gpuRead;

    public uint Stat()
    {
        stat31 ^= 0x80000000;
        uint s = (uint)(texX / 64) | (uint)(texY / 256) << 4 | (uint)semi << 5 | (uint)depth << 7
            | (dither ? 1u << 9 : 0) | (drawToDisplay ? 1u << 10 : 0) | (setMask ? 1u << 11 : 0) | (checkMask ? 1u << 12 : 0)
            | (texDisable ? 1u << 15 : 0) | (Pal ? 1u << 20 : 0) | (Rgb24 ? 1u << 21 : 0) | (Interlace ? 1u << 22 : 0)
            | (DisplayOff ? 1u << 23 : 0) | (irq ? 1u << 24 : 0) | dmaDir << 29;
        s |= HRes switch { 256 => 0u, 320 => 1u << 17, 512 => 2u << 17, 640 => 3u << 17, _ => 1u << 16 };
        if (VRes == 480) s |= 1u << 19;
        // ready for commands / vram->cpu / dma
        s |= 0x1C000000;
        s |= dmaDir switch { 1 => 1u << 25, 2 => 1u << 25, 3 => fromVram ? 1u << 25 : 0, _ => 0 };
        if (Interlace) s |= (uint)(m.Frame & 1) << 13;
        return s | stat31;
    }

    public uint Read()
    {
        if (!fromVram) return gpuRead;
        uint lo = VramRead(), hi = VramRead();
        return lo | hi << 16;
    }

    ushort VramRead()
    {
        if (xferLeft <= 0) { fromVram = false; return 0; }
        int x = (xferX + xferI % xferW) & 1023, y = (xferY + xferI / xferW) & 511;
        xferI++;
        if (--xferLeft == 0) fromVram = false;
        return Vram[y * 1024 + x];
    }

    public void VBlank() { }

    // ------------------------------------------------------------------- GP1

    public void Gp1(uint v)
    {
        switch (v >> 24)
        {
            case 0x00:
                Gp1(0x01000000); irq = false; DisplayOff = true; dmaDir = 0;
                texX = texY = semi = depth = 0; texDisable = flipX = flipY = setMask = checkMask = dither = drawToDisplay = false;
                areaL = areaT = 0; areaR = areaB = 0; offX = offY = 0; twMaskX = twMaskY = twOffX = twOffY = 0;
                DispX = DispY = 0; HRes = 320; VRes = 240; Rgb24 = Interlace = false;
                break;
            case 0x01: cmdLen = cmdNeed = 0; toVram = fromVram = false; polyline = false; break;
            case 0x02: irq = false; break;
            case 0x03: DisplayOff = (v & 1) != 0; break;
            case 0x04: dmaDir = v & 3; break;
            case 0x05: DispX = (int)(v & 0x3FE); DispY = (int)(v >> 10 & 0x1FF); break;
            case 0x06: hStart = (int)(v & 0xFFF); hEnd = (int)(v >> 12 & 0xFFF); break;
            case 0x07: vStart = (int)(v & 0x3FF); vEnd = (int)(v >> 10 & 0x3FF); break;
            case 0x08:
                HRes = (v & 0x40) != 0 ? 368 : (v & 3) switch { 0 => 256, 1 => 320, 2 => 512, _ => 640 };
                Interlace = (v & 0x20) != 0;
                VRes = (v & 4) != 0 && Interlace ? 480 : 240;
                Pal = (v & 8) != 0;
                Rgb24 = (v & 0x10) != 0;
                break;
            case 0x10:
                gpuRead = (v & 7) switch
                {
                    2 => (uint)(twMaskX | twMaskY << 5 | twOffX << 10 | twOffY << 15),
                    3 => (uint)(areaL | areaT << 10),
                    4 => (uint)(areaR | areaB << 10),
                    5 => (uint)((offX & 0x7FF) | (offY & 0x7FF) << 11),
                    7 => 2u,
                    _ => gpuRead,
                };
                break;
        }
    }

    // ------------------------------------------------------------------- GP0

    public void Gp0(uint v)
    {
        if (toVram) { VramWrite((ushort)v); if (toVram) VramWrite((ushort)(v >> 16)); return; }
        if (polyline)
        {
            if ((v & 0xF000F000) == 0x50005000 && polyStage == 0) { polyline = false; return; }
            PolylineWord(v);
            return;
        }
        if (cmdLen == 0)
        {
            cmdNeed = Length(v);
            if (cmdNeed == 0) return;
        }
        cmd[cmdLen++] = v;
        if (cmdLen < cmdNeed) return;
        cmdLen = 0;
        Execute();
    }

    static int Length(uint v)
    {
        uint c = v >> 24;
        switch (c >> 5)
        {
            case 1:
            {
                bool g = (c & 0x10) != 0, q = (c & 8) != 0, t = (c & 4) != 0;
                int n = q ? 4 : 3;
                return 1 + n * (1 + (t ? 1 : 0)) + (g ? n - 1 : 0);
            }
            case 2: return (c & 8) != 0 ? 3 + ((c & 0x10) != 0 ? 1 : 0) : ((c & 0x10) != 0 ? 4 : 3);
            case 3: return 2 + ((c & 4) != 0 ? 1 : 0) + ((c & 0x18) == 0 ? 1 : 0);
            case 4: return 4;
            case 5: return 3;
            case 6: return 3;
        }
        return c switch { 0x02 => 3, 0xE1 or 0xE2 or 0xE3 or 0xE4 or 0xE5 or 0xE6 or 0x1F or 0x01 => 1, _ => 0 };
    }

    public static bool Trace;
    /// <summary>the console's 4x4 ordered dither (off by default: the port renders clean gradients)</summary>
    public static bool Dither;
    public readonly long[] Counts = new long[256];
    public string Areas => $"area {areaL},{areaT}-{areaR},{areaB} off {offX},{offY} tex {texX},{texY} d{depth}";

    void Execute()
    {
        uint c = cmd[0] >> 24;
        Counts[c]++;
        if (Trace) Psx.Log($"gp0 {string.Join(' ', cmd.Take(cmdNeed == 0 ? 1 : cmdNeed).Select(w => w.ToString("X8")))} f{m.Frame}");
        switch (c >> 5)
        {
            case 1: Polygon(c); return;
            case 2: Line(c); return;
            case 3: Rect(c); return;
            case 4: VramCopy(); return;
            case 5:
                xferX = (int)(cmd[1] & 0x3FF); xferY = (int)(cmd[1] >> 16 & 0x1FF);
                xferW = (int)(((cmd[2] & 0xFFFF) - 1) & 0x3FF) + 1; xferH = (int)(((cmd[2] >> 16) - 1) & 0x1FF) + 1;
                xferI = 0; xferLeft = xferW * xferH; toVram = true;
                return;
            case 6:
                xferX = (int)(cmd[1] & 0x3FF); xferY = (int)(cmd[1] >> 16 & 0x1FF);
                xferW = (int)(((cmd[2] & 0xFFFF) - 1) & 0x3FF) + 1; xferH = (int)(((cmd[2] >> 16) - 1) & 0x1FF) + 1;
                xferI = 0; xferLeft = xferW * xferH; fromVram = true;
                return;
        }
        uint v = cmd[0];
        switch (c)
        {
            case 0x02: Fill(); break;
            case 0x1F: irq = true; m.Irq(1); break;
            case 0xE1:
                texX = (int)(v & 15) * 64; texY = (int)(v >> 4 & 1) * 256; semi = (int)(v >> 5 & 3); depth = (int)(v >> 7 & 3);
                dither = (v & 0x200) != 0; drawToDisplay = (v & 0x400) != 0; texDisable = (v & 0x800) != 0;
                flipX = (v & 0x1000) != 0; flipY = (v & 0x2000) != 0;
                break;
            case 0xE2: twMaskX = (int)(v & 31); twMaskY = (int)(v >> 5 & 31); twOffX = (int)(v >> 10 & 31); twOffY = (int)(v >> 15 & 31); break;
            case 0xE3: areaL = (int)(v & 0x3FF); areaT = (int)(v >> 10 & 0x1FF); break;
            case 0xE4: areaR = (int)(v & 0x3FF); areaB = (int)(v >> 10 & 0x1FF); break;
            case 0xE5: offX = (int)(v << 21) >> 21; offY = (int)(v << 10) >> 21; break;
            case 0xE6: setMask = (v & 1) != 0; checkMask = (v & 2) != 0; break;
        }
    }

    void VramWrite(ushort p)
    {
        int x = (xferX + xferI % xferW) & 1023, y = (xferY + xferI / xferW) & 511;
        xferI++;
        ref ushort d = ref Vram[y * 1024 + x];
        if (!checkMask || (d & 0x8000) == 0) d = (ushort)(p | (setMask ? 0x8000 : 0));
        if (--xferLeft == 0) toVram = false;
    }

    void Fill()
    {
        ushort col = Rgb15(cmd[0]);
        int x0 = (int)(cmd[1] & 0x3F0), y0 = (int)(cmd[1] >> 16 & 0x1FF);
        int w = (int)(((cmd[2] & 0x3FF) + 15) & ~15u), h = (int)(cmd[2] >> 16 & 0x1FF);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                Vram[((y0 + y) & 511) * 1024 + ((x0 + x) & 1023)] = col;
    }

    void VramCopy()
    {
        int sx = (int)(cmd[1] & 0x3FF), sy = (int)(cmd[1] >> 16 & 0x1FF);
        int dx = (int)(cmd[2] & 0x3FF), dy = (int)(cmd[2] >> 16 & 0x1FF);
        int w = (int)(((cmd[3] & 0xFFFF) - 1) & 0x3FF) + 1, h = (int)(((cmd[3] >> 16) - 1) & 0x1FF) + 1;
        var tmp = new ushort[w];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++) tmp[x] = Vram[((sy + y) & 511) * 1024 + ((sx + x) & 1023)];
            for (int x = 0; x < w; x++)
            {
                ref ushort d = ref Vram[((dy + y) & 511) * 1024 + ((dx + x) & 1023)];
                if (!checkMask || (d & 0x8000) == 0) d = (ushort)(tmp[x] | (setMask ? 0x8000 : 0));
            }
        }
    }

    static ushort Rgb15(uint c) => (ushort)((c >> 3 & 31) | (c >> 11 & 31) << 5 | (c >> 19 & 31) << 10);

    // ------------------------------------------------------------------- primitives

    struct Vtx { public int X, Y, R, G, B, U, V; }

    void Polygon(uint c)
    {
        bool g = (c & 0x10) != 0, quad = (c & 8) != 0, tex = (c & 4) != 0, st = (c & 2) != 0, raw = (c & 1) != 0;
        int n = quad ? 4 : 3;
        Span<Vtx> v = stackalloc Vtx[4];
        int k = 1;
        int clutX = 0, clutY = 0, pX = texX, pY = texY, pDepth = depth, pSemi = semi;
        for (int i = 0; i < n; i++)
        {
            uint col = i > 0 && g ? cmd[k++] : cmd[0];
            uint xy = cmd[k++];
            v[i].X = ((int)(xy << 21) >> 21) + offX; v[i].Y = ((int)((xy >> 16) << 21) >> 21) + offY;
            v[i].R = (int)(col & 0xFF); v[i].G = (int)(col >> 8 & 0xFF); v[i].B = (int)(col >> 16 & 0xFF);
            if (tex)
            {
                uint uv = cmd[k++];
                v[i].U = (int)(uv & 0xFF); v[i].V = (int)(uv >> 8 & 0xFF);
                if (i == 0) { clutX = (int)(uv >> 16 & 0x3F) * 16; clutY = (int)(uv >> 22 & 0x1FF); }
                if (i == 1)
                {
                    uint tp = uv >> 16;
                    pX = (int)(tp & 15) * 64; pY = (int)(tp >> 4 & 1) * 256; pSemi = (int)(tp >> 5 & 3); pDepth = (int)(tp >> 7 & 3);
                    texX = pX; texY = pY; semi = pSemi; depth = pDepth; texDisable = (tp & 0x800) != 0;
                }
            }
        }
        var ctx = new Ctx { Tex = tex && !texDisable, Raw = raw, Gouraud = g, Semi = st ? pSemi : -1, TexX = pX, TexY = pY, Depth = pDepth, ClutX = clutX, ClutY = clutY, Dither = Dither && dither && (g || (tex && !raw)) };
        Triangle(v[0], v[1], v[2], ctx);
        if (quad) Triangle(v[1], v[2], v[3], ctx);
    }

    struct Ctx
    {
        public bool Tex, Raw, Gouraud, Dither;
        public int Semi, TexX, TexY, Depth, ClutX, ClutY;
    }

    static long Edge(in Vtx a, in Vtx b, int x, int y) => (long)(b.X - a.X) * (y - a.Y) - (long)(b.Y - a.Y) * (x - a.X);

    void Triangle(Vtx a, Vtx b, Vtx c, in Ctx ctx)
    {
        long area = Edge(a, b, c.X, c.Y);
        if (area == 0) return;
        if (area < 0) { (b, c) = (c, b); area = -area; }
        int minX = Math.Min(a.X, Math.Min(b.X, c.X)), maxX = Math.Max(a.X, Math.Max(b.X, c.X));
        int minY = Math.Min(a.Y, Math.Min(b.Y, c.Y)), maxY = Math.Max(a.Y, Math.Max(b.Y, c.Y));
        if (maxX - minX > 1023 || maxY - minY > 511) return;
        minX = Math.Max(minX, areaL); maxX = Math.Min(maxX, areaR);
        minY = Math.Max(minY, areaT); maxY = Math.Min(maxY, areaB);
        if (minX > maxX || minY > maxY) return;
        // top-left rule: pixels exactly on a right/bottom edge are not drawn
        int biasA = IsTopLeft(b, c) ? 0 : -1, biasB = IsTopLeft(c, a) ? 0 : -1, biasC = IsTopLeft(a, b) ? 0 : -1;
        double inv = 1.0 / area;
        for (int y = minY; y <= maxY; y++)
        {
            long w0 = Edge(b, c, minX, y), w1 = Edge(c, a, minX, y), w2 = Edge(a, b, minX, y);
            long d0 = -(c.Y - b.Y), d1 = -(a.Y - c.Y), d2 = -(b.Y - a.Y);
            for (int x = minX; x <= maxX; x++, w0 += d0, w1 += d1, w2 += d2)
            {
                if (w0 + biasA < 0 || w1 + biasB < 0 || w2 + biasC < 0) continue;
                double l0 = w0 * inv, l1 = w1 * inv, l2 = w2 * inv;
                int r = a.R, g = a.G, bl = a.B;
                if (ctx.Gouraud)
                {
                    r = (int)(a.R * l0 + b.R * l1 + c.R * l2 + 0.5);
                    g = (int)(a.G * l0 + b.G * l1 + c.G * l2 + 0.5);
                    bl = (int)(a.B * l0 + b.B * l1 + c.B * l2 + 0.5);
                }
                int u = 0, vv = 0;
                if (ctx.Tex)
                {
                    u = (int)(a.U * l0 + b.U * l1 + c.U * l2 + 0.5);
                    vv = (int)(a.V * l0 + b.V * l1 + c.V * l2 + 0.5);
                }
                Plot(x, y, r, g, bl, u, vv, ctx);
            }
        }
    }

    // with positive area in y-down screen space, a top edge runs left to right and a left edge runs upward
    static bool IsTopLeft(in Vtx a, in Vtx b) => (b.Y == a.Y && b.X > a.X) || b.Y < a.Y;

    static readonly int[] DitherTab = [-4, 0, -3, 1, 2, -2, 3, -1, -3, 1, -4, 0, 3, -1, 2, -2];

    void Plot(int x, int y, int r, int g, int b, int u, int v, in Ctx ctx)
    {
        ref ushort dst = ref Vram[(y & 511) * 1024 + (x & 1023)];
        if (checkMask && (dst & 0x8000) != 0) return;
        int pr, pg, pb; bool stBit;
        if (ctx.Tex)
        {
            ushort t = Texel(u, v, ctx);
            if (t == 0) return;
            stBit = (t & 0x8000) != 0;
            pr = (t & 31) << 3; pg = (t >> 5 & 31) << 3; pb = (t >> 10 & 31) << 3;
            if (!ctx.Raw)
            {
                pr = Math.Min(255, pr * r >> 7); pg = Math.Min(255, pg * g >> 7); pb = Math.Min(255, pb * b >> 7);
            }
        }
        else { pr = r; pg = g; pb = b; stBit = true; }
        if (ctx.Dither)
        {
            int d = DitherTab[(y & 3) * 4 + (x & 3)];
            pr = Math.Clamp(pr + d, 0, 255); pg = Math.Clamp(pg + d, 0, 255); pb = Math.Clamp(pb + d, 0, 255);
        }
        int R5 = pr >> 3, G5 = pg >> 3, B5 = pb >> 3;
        if (ctx.Semi >= 0 && stBit)
        {
            int br = dst & 31, bg = dst >> 5 & 31, bb = dst >> 10 & 31;
            switch (ctx.Semi)
            {
                case 0: R5 = (br + R5) >> 1; G5 = (bg + G5) >> 1; B5 = (bb + B5) >> 1; break;
                case 1: R5 = Math.Min(31, br + R5); G5 = Math.Min(31, bg + G5); B5 = Math.Min(31, bb + B5); break;
                case 2: R5 = Math.Max(0, br - R5); G5 = Math.Max(0, bg - G5); B5 = Math.Max(0, bb - B5); break;
                case 3: R5 = Math.Min(31, br + (R5 >> 2)); G5 = Math.Min(31, bg + (G5 >> 2)); B5 = Math.Min(31, bb + (B5 >> 2)); break;
            }
        }
        dst = (ushort)(R5 | G5 << 5 | B5 << 10 | (setMask || (ctx.Tex && stBit) ? 0x8000 : 0));
    }

    ushort Texel(int u, int v, in Ctx ctx)
    {
        u &= 0xFF; v &= 0xFF;
        u = (u & ~(twMaskX * 8)) | ((twOffX & twMaskX) * 8);
        v = (v & ~(twMaskY * 8)) | ((twOffY & twMaskY) * 8);
        switch (ctx.Depth)
        {
            case 0:
            {
                ushort w = Vram[((ctx.TexY + v) & 511) * 1024 + ((ctx.TexX + u / 4) & 1023)];
                int idx = w >> ((u & 3) * 4) & 15;
                return Vram[ctx.ClutY * 1024 + ((ctx.ClutX + idx) & 1023)];
            }
            case 1:
            {
                ushort w = Vram[((ctx.TexY + v) & 511) * 1024 + ((ctx.TexX + u / 2) & 1023)];
                int idx = w >> ((u & 1) * 8) & 0xFF;
                return Vram[ctx.ClutY * 1024 + ((ctx.ClutX + idx) & 1023)];
            }
            default:
                return Vram[((ctx.TexY + v) & 511) * 1024 + ((ctx.TexX + u) & 1023)];
        }
    }

    void Rect(uint c)
    {
        bool tex = (c & 4) != 0, st = (c & 2) != 0, raw = (c & 1) != 0;
        int k = 1;
        uint xy = cmd[k++];
        int x0 = ((int)(xy << 21) >> 21) + offX, y0 = ((int)((xy >> 16) << 21) >> 21) + offY;
        int u0 = 0, v0 = 0, clutX = 0, clutY = 0;
        if (tex)
        {
            uint uv = cmd[k++];
            u0 = (int)(uv & 0xFF); v0 = (int)(uv >> 8 & 0xFF);
            clutX = (int)(uv >> 16 & 0x3F) * 16; clutY = (int)(uv >> 22 & 0x1FF);
        }
        int w, h;
        switch (c >> 3 & 3)
        {
            case 0: w = (int)(cmd[k] & 0x3FF); h = (int)(cmd[k] >> 16 & 0x1FF); break;
            case 1: w = h = 1; break;
            case 2: w = h = 8; break;
            default: w = h = 16; break;
        }
        var ctx = new Ctx { Tex = tex && !texDisable, Raw = raw, Semi = st ? semi : -1, TexX = texX, TexY = texY, Depth = depth, ClutX = clutX, ClutY = clutY };
        int r = (int)(cmd[0] & 0xFF), g = (int)(cmd[0] >> 8 & 0xFF), b = (int)(cmd[0] >> 16 & 0xFF);
        int ys = Math.Max(y0, areaT), ye = Math.Min(y0 + h - 1, areaB);
        int xs = Math.Max(x0, areaL), xe = Math.Min(x0 + w - 1, areaR);
        for (int y = ys; y <= ye; y++)
            for (int x = xs; x <= xe; x++)
            {
                int du = x - x0, dv = y - y0;
                Plot(x, y, r, g, b, flipX ? u0 - du : u0 + du, flipY ? v0 - dv : v0 + dv, ctx);
            }
    }

    // lines: (polyline words come in one at a time after the first two vertices)
    Vtx lineLast; uint lineColor; bool lineG, lineSemi;

    void Line(uint c)
    {
        bool g = (c & 0x10) != 0, poly = (c & 8) != 0;
        lineG = g; lineSemi = (c & 2) != 0;
        var a = LineVtx(cmd[0], cmd[1]);
        var b = g ? LineVtx(cmd[2], cmd[3]) : LineVtx(cmd[0], cmd[2]);
        DrawLine(a, b);
        lineLast = b; lineColor = cmd[0];
        if (poly) { polyline = true; polyStage = 0; }
    }

    void PolylineWord(uint v)
    {
        if (lineG)
        {
            if (polyStage == 0) { lineColor = v; polyStage = 1; return; }
            polyStage = 0;
        }
        var b = LineVtx(lineColor, v);
        DrawLine(lineLast, b);
        lineLast = b;
    }

    Vtx LineVtx(uint col, uint xy) => new()
    {
        X = ((int)(xy << 21) >> 21) + offX, Y = ((int)((xy >> 16) << 21) >> 21) + offY,
        R = (int)(col & 0xFF), G = (int)(col >> 8 & 0xFF), B = (int)(col >> 16 & 0xFF),
    };

    void DrawLine(Vtx a, Vtx b)
    {
        int dx = b.X - a.X, dy = b.Y - a.Y;
        int n = Math.Max(Math.Abs(dx), Math.Abs(dy));
        if (Math.Abs(dx) > 1023 || Math.Abs(dy) > 511) return;
        var ctx = new Ctx { Semi = lineSemi ? semi : -1, Dither = Dither && dither && lineG };
        for (int i = 0; i <= n; i++)
        {
            double t = n == 0 ? 0 : (double)i / n;
            int x = a.X + (int)Math.Round(dx * t), y = a.Y + (int)Math.Round(dy * t);
            if (x < areaL || x > areaR || y < areaT || y > areaB) continue;
            int r = lineG ? (int)(a.R + (b.R - a.R) * t) : a.R, g = lineG ? (int)(a.G + (b.G - a.G) * t) : a.G, bb = lineG ? (int)(a.B + (b.B - a.B) * t) : a.B;
            Plot(x, y, r, g, bb, 0, 0, ctx);
        }
    }

    // ------------------------------------------------------------------- output

    /// <summary>the displayed area as BGRA pixels; returns (width, height)</summary>
    public (int W, int H) Render(uint[] dst)
    {
        int w = HRes, h = VRes;
        // vertical display range shrinks/grows the picture (e.g. 224-line games)
        int lines = Math.Clamp(vEnd - vStart, 16, 256) * (VRes == 480 ? 2 : 1);
        if (lines < h) h = lines;
        if (DisplayOff) { Array.Clear(dst, 0, w * h); return (w, h); }
        for (int y = 0; y < h; y++)
        {
            int row = ((DispY + y) & 511) * 1024;
            if (!Rgb24)
                for (int x = 0; x < w; x++)
                {
                    ushort p = Vram[row + ((DispX + x) & 1023)];
                    uint r = (uint)(p & 31) << 3, g = (uint)(p >> 5 & 31) << 3, b = (uint)(p >> 10 & 31) << 3;
                    dst[y * w + x] = 0xFF000000 | (r | r >> 5) << 16 | (g | g >> 5) << 8 | (b | b >> 5);
                }
            else
                for (int x = 0; x < w; x++)
                {
                    int bo = DispX * 2 + x * 3;
                    uint Byte(int o) { ushort p = Vram[row + ((o >> 1) & 1023)]; return (o & 1) == 0 ? (uint)(p & 0xFF) : (uint)(p >> 8); }
                    dst[y * w + x] = 0xFF000000 | Byte(bo) << 16 | Byte(bo + 1) << 8 | Byte(bo + 2);
                }
        }
        return (w, h);
    }
}
