namespace Ps1tl.Port;

/// <summary>
/// Native libspu transfers, part of the PsyQ replacement. Everything libspu and libsnd upload to or read from
/// sound RAM (sample banks, reverb clears, ...) goes through _spu_t (DMA) or _spu_writeByIO (I/O mode); here
/// both copy straight into the SPU's RAM, with no DMA 4, no transfer-mode polling and no DMA interrupt. The
/// "transfer done" callback or SPU event comes as a native interrupt, when the DMA would have finished.
/// Voice and volume settings still reach the SPU mixer through its registers.
/// </summary>
public sealed class LibSpu(Psx m)
{
    uint A0 => m.R[4];
    uint A1 => m.R[5];
    uint A2 => m.R[6];
    uint V0 { set => m.R[2] = value; }

    uint tsa, memShift, xferCallback, dir;   // libspu's own variables, shared with the code still recompiled

    public Dictionary<string, Action> Impl(IReadOnlyDictionary<string, uint> symbols)
    {
        tsa = symbols.GetValueOrDefault("@_spu_tsa");
        memShift = symbols.GetValueOrDefault("@_spu_mem_mode_plus");
        xferCallback = symbols.GetValueOrDefault("@_spu_transferCallback");
        dir = symbols.GetValueOrDefault("@_spu_dir");
        return new()
        {
            ["_spu_t"] = () => V0 = T(A0, A1, A2),
            ["_spu_writeByIO"] = () => m.Spu.Transfer(SpuAddr, m.RamSpan(A0, (int)A1), toSpu: true),
        };
    }

    /// <summary>the transfer start address, in bytes</summary>
    uint SpuAddr => m.Read16(tsa) << 3;

    /// <summary>_spu_t(mode, ...): 0 / 1 = next transfer reads / writes, 2 = set the start address, 3 = transfer</summary>
    uint T(uint mode, uint a1, uint a2)
    {
        switch (mode)
        {
            case 0: m.Write32(dir, 1); break;
            case 1: m.Write32(dir, 0); break;
            case 2: m.Write16(tsa, a1 >> (int)m.Read32(memShift)); break;
            case 3:
            {
                // the DMA moves whole 64-byte blocks
                int bytes = (int)((a2 + 63) / 64 * 64);
                m.Spu.Transfer(SpuAddr, m.RamSpan(a1, bytes), toSpu: m.Read32(dir) != 1);
                m.After(Math.Max(16, bytes / 4), () => m.Post(Done));
                break;
            }
        }
        return 0;
    }

    /// <summary>what libspu's DMA interrupt handler does: the transfer callback, or else the SPU event</summary>
    void Done()
    {
        uint cb = m.Read32(xferCallback);
        if (cb != 0) m.Bios.IrqCallback(cb);
        else m.Bios.DeliverEvent(0xF0000009, 0x20);
    }
}
