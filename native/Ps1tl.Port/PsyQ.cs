namespace Ps1tl.Port;

/// <summary>
/// Native replacements for Sony's PsyQ SDK libraries. The game's own code is recompiled; the SDK functions it
/// links against are swapped for these C# versions, which talk to the renderer / disc / frame loop directly
/// instead of programming emulated hardware registers, DMA and interrupts.
/// Arguments come in a0-a3 (R[4..7]), results go to v0 (R[2]), as the MIPS calling convention has them.
/// </summary>
public sealed class PsyQ(Psx m)
{
    uint A0 => m.R[4];
    uint A1 => m.R[5];
    uint A2 => m.R[6];
    uint A3 => m.R[7];
    uint V0 { set => m.R[2] = value; }
    public int Calls;
    /// <summary>--sdktrace: log every native SDK call</summary>
    public static bool Trace;
    public readonly LibCd Cd = new(m);
    public readonly LibPress Press = new(m);

    /// <summary>
    /// Plug the native functions in at the addresses the symbol table gives for this executable;
    /// returns the number replaced.
    /// </summary>
    public int Install(IReadOnlyDictionary<string, uint> symbols)
    {
        var impl = new Dictionary<string, Action>
        {
            // ---------------------------------------------------------------- libgpu
            ["ResetGraph"] = ResetGraph,
            ["SetDispMask"] = () => { m.Gpu.Gp1(0x03000000 | (A0 != 0 ? 0u : 1u)); V0 = 0; },
            ["DrawSync"] = () => V0 = 0,                     // drawing is synchronous: never anything queued
            ["DrawSyncCallback"] = () => { V0 = drawSyncCb; drawSyncCb = A0; },
            ["ClearImage"] = ClearImage,
            ["LoadImage"] = () => { Transfer(A0, A1, toVram: true); V0 = 0; },
            ["StoreImage"] = () => { Transfer(A0, A1, toVram: false); V0 = 0; },
            ["ClearOTag"] = ClearOTag,
            ["ClearOTagR"] = ClearOTagR,
            ["DrawOTag"] = () => { DrawOTag(A0); V0 = 0; },
            ["PutDrawEnv"] = () => { PutDrawEnv(A0); V0 = A0; },
            ["PutDispEnv"] = () => { PutDispEnv(A0); V0 = A0; },
            // ---------------------------------------------------------------- libetc
            ["VSync"] = () => V0 = VSync((int)A0),
        };
        foreach (var (k, v) in Cd.Impl(symbols)) impl[k] = v;
        foreach (var (k, v) in Press.Impl(symbols)) impl[k] = v;
        int n = 0;
        foreach (var (name, addr) in symbols)
            if (impl.TryGetValue(name, out var f))
            {
                m.Funcs[addr] = _ =>
                {
                    Calls++;
                    if (Trace) Psx.Log($"sdk: {name}({A0:X}, {A1:X}, {A2:X}) from {m.R[31]:X8}{(m.Bios.InException ? " (in interrupt)" : "")} f{m.Frame}");
                    f();
                };
                n++;
            }
        vcount = symbols.GetValueOrDefault("@Vcount");
        return n;
    }

    // ------------------------------------------------------------------ libgpu

    uint drawSyncCb;

    void ResetGraph()
    {
        // 0: full reset, 1: cancel drawing only, 3: reset but keep the display settings
        if (A0 is 0) m.Gpu.Gp1(0);
        m.Gpu.Gp1(0x01000000);
        V0 = 0;
    }

    static (int X, int Y, int W, int H) Rect(Psx m, uint p) =>
        ((short)m.Read16(p), (short)m.Read16(p + 2), (short)m.Read16(p + 4), (short)m.Read16(p + 6));

    void ClearImage()
    {
        var (x, y, w, h) = Rect(m, A0);
        uint rgb = (A1 & 0xFF) | (A2 & 0xFF) << 8 | (A3 & 0xFF) << 16;
        Fill(x, y, w, h, rgb);
        V0 = 0;
    }

    void Fill(int x, int y, int w, int h, uint rgb)
    {
        var g = m.Gpu;
        g.Gp0(0x02000000 | rgb);
        g.Gp0((uint)(x & 0x3FF) | (uint)(y & 0x1FF) << 16);
        g.Gp0((uint)(w & 0x7FF) | (uint)(h & 0x1FF) << 16);
    }

    /// <summary>LoadImage / StoreImage: RAM &lt;-&gt; VRAM rectangle, done at once</summary>
    void Transfer(uint rect, uint p, bool toVram)
    {
        var (x, y, w, h) = Rect(m, rect);
        var vram = m.Gpu.Vram;
        for (int j = 0; j < h; j++)
            for (int i = 0; i < w; i++)
            {
                int v = ((y + j) & 511) * 1024 + ((x + i) & 1023);
                uint a = p + (uint)(j * w + i) * 2;
                if (toVram) vram[v] = (ushort)m.Read16(a); else m.Write16(a, vram[v]);
            }
    }

    void ClearOTag()
    {
        uint ot = A0; int n = (int)A1;
        for (int i = 0; i < n - 1; i++) m.Write32(ot + (uint)i * 4, (ot + (uint)(i + 1) * 4) & 0xFFFFFF);
        if (n > 0) m.Write32(ot + (uint)(n - 1) * 4, 0x00FFFFFF);
        V0 = ot;
    }

    /// <summary>reverse ordering table: each entry links to the one before it, entry 0 ends the list</summary>
    void ClearOTagR()
    {
        uint ot = A0; int n = (int)A1;
        for (int i = n - 1; i > 0; i--) m.Write32(ot + (uint)i * 4, (ot + (uint)(i - 1) * 4) & 0xFFFFFF);
        if (n > 0) m.Write32(ot, 0x00FFFFFF);
        V0 = ot;
    }

    /// <summary>walk the primitive linked list and hand each packet's GPU words to the renderer</summary>
    void DrawOTag(uint p)
    {
        var g = m.Gpu;
        for (int guard = 0; guard < 0x40000; guard++)
        {
            uint hdr = m.Read32(p);
            int len = (int)(hdr >> 24);
            for (int i = 0; i < len; i++) g.Gp0(m.Read32(p + 4 + (uint)i * 4));
            if ((hdr & 0xFFFFFF) == 0xFFFFFF) break;
            p = 0x80000000 | (hdr & 0xFFFFFF);
        }
        if (drawSyncCb != 0) m.Bios.Callback(drawSyncCb);
    }

    /// <summary>DRAWENV: clip RECT, ofs[2], tw RECT, tpage, dtd, dfe, isbg, r0 g0 b0</summary>
    void PutDrawEnv(uint env)
    {
        var g = m.Gpu;
        var (cx, cy, cw, ch) = Rect(m, env);
        int ox = (short)m.Read16(env + 8), oy = (short)m.Read16(env + 10);
        var (tx, ty, tw, th) = Rect(m, env + 12);
        uint tpage = m.Read16(env + 20);
        uint dtd = m.Read8(env + 22), dfe = m.Read8(env + 23), isbg = m.Read8(env + 24);
        uint rgb = m.Read8(env + 25) | m.Read8(env + 26) << 8 | m.Read8(env + 27) << 16;
        g.Gp0(0xE1000000 | (tpage & 0x9FF) | (dtd != 0 ? 0x200u : 0) | (dfe != 0 ? 0x400u : 0));
        // texture window: mask/offset in 8-pixel units (a zero-size window means none)
        uint twMaskX = tw == 0 ? 0 : (uint)((-tw >> 3) & 0x1F), twMaskY = th == 0 ? 0 : (uint)((-th >> 3) & 0x1F);
        g.Gp0(0xE2000000 | twMaskX | twMaskY << 5 | (uint)((tx >> 3) & 0x1F) << 10 | (uint)((ty >> 3) & 0x1F) << 15);
        int x1 = Math.Clamp(cx + cw - 1, 0, 1023), y1 = Math.Clamp(cy + ch - 1, 0, 511);
        g.Gp0(0xE3000000 | (uint)(cx & 0x3FF) | (uint)(cy & 0x3FF) << 10);
        g.Gp0(0xE4000000 | (uint)x1 | (uint)y1 << 10);
        g.Gp0(0xE5000000 | (uint)(ox & 0x7FF) | (uint)(oy & 0x7FF) << 11);
        if (isbg != 0) Fill(cx, cy, cw, ch, rgb);
    }

    /// <summary>DISPENV: disp RECT (the VRAM area shown), screen RECT, isinter, isrgb24</summary>
    void PutDispEnv(uint env)
    {
        var g = m.Gpu;
        var (dx, dy, dw, dh) = Rect(m, env);
        bool inter = m.Read8(env + 16) != 0, rgb24 = m.Read8(env + 17) != 0;
        uint mode = dw switch { <= 256 => 0u, <= 320 => 1u, <= 384 => 0x40u, <= 512 => 2u, _ => 3u };
        if (dh > 256) mode |= 0x24;   // 480 lines, interlaced
        else if (inter) mode |= 0x20;
        if (rgb24) mode |= 0x10;
        g.Gp1(0x08000000 | mode);
        g.Gp1(0x05000000 | (uint)(dx & 0x3FF) | (uint)(dy & 0x1FF) << 10);
    }

    // ------------------------------------------------------------------ libetc

    uint vcount;      // libetc's vblank counter (kept by its vblank callback)
    int lastVSync;

    /// <summary>
    /// VSync(-1): vblank count; VSync(0): wait for the next frame; VSync(n): wait until n frames since the last
    /// call. Returns the frame count / lines elapsed. Waiting runs the frame loop instead of spinning on the
    /// counter.
    /// </summary>
    uint VSync(int mode)
    {
        if (mode < 0) return vcount != 0 ? m.Read32(vcount) : (uint)m.Frame;
        long start = m.Cycles;
        if (mode != 1)
        {
            int target = Math.Max(m.Frame + (mode == 0 ? 1 : 0), lastVSync + Math.Max(1, mode));
            while (m.Frame < target) m.RunToVBlank();
            lastVSync = m.Frame;
        }
        return (uint)((m.Cycles - start) / Psx.LineCycles);
    }
}

/// <summary>where the SDK functions sit in each known executable (by CRC32 of the EXE file)</summary>
public static class Symbols
{
    public static readonly Dictionary<uint, Dictionary<string, uint>> ByExe = new()
    {
        // Yuuyami Doori Tankentai (SLPS-02274)
        [0x8A84BE16] = new()
        {
            ["ResetGraph"] = 0x80036F38, ["DrawSyncCallback"] = 0x800371BC, ["SetDispMask"] = 0x8003721C,
            ["DrawSync"] = 0x800372B4, ["ClearImage"] = 0x80037438, ["LoadImage"] = 0x80037560,
            ["StoreImage"] = 0x800375C0, ["ClearOTag"] = 0x800376D8, ["ClearOTagR"] = 0x800377A0,
            ["DrawOTag"] = 0x800378A8, ["PutDrawEnv"] = 0x80037918, ["PutDispEnv"] = 0x80037AE4,
            ["VSync"] = 0x800311C8, ["@Vcount"] = 0x8005A52C,
            ["CdInit"] = 0x80031EB8, ["CdReset"] = 0x80032044, ["CdFlush"] = 0x800320B0, ["CdSetDebug"] = 0x800320D0,
            ["CdSync"] = 0x8003214C, ["CdReady"] = 0x8003216C, ["CdSyncCallback"] = 0x8003218C,
            ["CdReadyCallback"] = 0x800321A0, ["CdControl"] = 0x800321B4, ["CdControlF"] = 0x800322F0,
            ["CdControlB"] = 0x80032424, ["CdMix"] = 0x80032570, ["CdGetSector"] = 0x80032590,
            ["CdDataCallback"] = 0x800325D0, ["CdDataSync"] = 0x800325F4, ["CdSearchFile"] = 0x80033D88,
            ["CdRead2"] = 0x800350C8, ["StSetRing"] = 0x80031E88, ["StClearRing"] = 0x80035178,
            ["StUnSetRing"] = 0x800351D8, ["StGetBackloc"] = 0x800352E4, ["StSetStream"] = 0x80035348,
            ["StFreeRing"] = 0x800353D8, ["StGetNext"] = 0x800354C8, ["StSetMask"] = 0x80035588,
            ["StCdInterrupt"] = 0x800355A8, ["@StReady"] = 0x8003514C,
            ["DecDCTReset"] = 0x8003B9D8, ["DecDCTin"] = 0x8003BB30, ["DecDCTout"] = 0x8003BBAC,
            ["DecDCToutCallback"] = 0x8003BC68, ["DecDCTvlc"] = 0x8003C088, ["@ResetCallback"] = 0x800313D8,
            ["@DecDCTquant"] = 0x8004FDAC, ["@DecDCTscale"] = 0x8004FE30, ["@DecDCTvlcState"] = 0x8004FEFC,
        },
    };
}
