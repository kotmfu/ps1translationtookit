namespace Ps1tl.Port;

/// <summary>
/// Native libpress (FMV decoding), part of the PsyQ replacement. DecDCTvlc turns a movie frame's bitstream into
/// MDEC run-length codes on the CPU; DecDCTin / DecDCTout hand them to the MDEC decoder and take the decoded
/// pixels back directly (no MDEC registers, no DMA 0/1), and the "slice done" callback comes as a native interrupt.
/// </summary>
public sealed class LibPress(Psx m)
{
    uint A0 => m.R[4];
    uint A1 => m.R[5];
    uint A2 => m.R[6];
    uint V0 { set => m.R[2] = value; }

    uint outCb, resetCallback, quant, scale, vlcState;
    /// <summary>--vlccheck: run Sony's DecDCTvlc next to the native one and report any difference</summary>
    public static bool Check;
    Action<Psx>? sonyVlc;

    public Dictionary<string, Action> Impl(IReadOnlyDictionary<string, uint> symbols)
    {
        resetCallback = symbols.GetValueOrDefault("@ResetCallback");
        quant = symbols.GetValueOrDefault("@DecDCTquant");
        scale = symbols.GetValueOrDefault("@DecDCTscale");
        vlcState = symbols.GetValueOrDefault("@DecDCTvlcState");
        if (Check && symbols.TryGetValue("DecDCTvlc", out var a)) sonyVlc = m.Funcs.GetValueOrDefault(a);
        return new()
        {
            ["DecDCTReset"] = () => Reset(A0),
            ["DecDCTin"] = () => In(A0, A1),
            ["DecDCTout"] = () => Out(A0, A1),
            ["DecDCToutCallback"] = () => { V0 = outCb; outCb = A0; },
            ["DecDCTvlc"] = () => V0 = Check ? CheckedVlc(A0, A1, A2) : Vlc(A0, A1, A2),
        };
    }

    /// <summary>DecDCTReset(mode): 0 = full reset with the standard quantisation / scale tables, 1 = decoder only</summary>
    void Reset(uint mode)
    {
        if (mode == 0 && resetCallback != 0) m.Bios.Callback(resetCallback);
        if (mode > 1) return;
        m.Mdec.WriteCtrl(0x80000000);
        if (mode == 0) { Send(quant, 32); Send(scale, 32); }
    }

    /// <summary>a command word and its data words to the decoder (whole 32-word blocks, as the DMA moves them)</summary>
    void Send(uint addr, uint words)
    {
        m.Mdec.WriteCmd(m.Read32(addr));
        for (uint i = 0; i < (words >> 5) * 32; i++) m.Mdec.WriteCmd(m.Read32(addr + 4 + i * 4));
    }

    /// <summary>DecDCTin(runlevel, mode): mode bit 0 = 24-bit output, bit 1 = set the STP bit</summary>
    void In(uint buf, uint mode)
    {
        uint w = m.Read32(buf);
        w = (mode & 1) != 0 ? w & ~0x08000000u : w | 0x08000000;
        w = (mode & 2) != 0 ? w | 0x02000000 : w & ~0x02000000u;
        m.Write32(buf, w);
        Send(buf, w & 0xFFFF);
    }

    /// <summary>DecDCTout(buf, words): take decoded pixels; the callback follows once a DMA would have finished</summary>
    void Out(uint buf, uint words)
    {
        uint n = (words >> 5) * 32;
        for (uint i = 0; i < n; i++) m.Write32(buf + i * 4, m.Mdec.ReadData());
        m.After(Math.Max(16, n), () => m.Post(() => { if (outCb != 0) m.Bios.IrqCallback(outCb); }));
    }

    // ------------------------------------------------------------------ DecDCTvlc

    /// <summary>
    /// DecDCTvlc(bs, buf, table): Huffman-decode a frame (STR v2/v3) into MDEC run-length halfwords, using the
    /// lookup table DecDCTvlcBuild unpacked into RAM: table+0 / +1024 the v3 DC tables (chroma / luma), table+2048
    /// the AC codes by their top 13 bits (8 bytes: the code's halfword in bits 16-31 and length in bits 0-7, plus
    /// up to two more halfwords when one lookup decodes several codes), table+2048+64K the longer AC codes by
    /// the 9 bits after an 8-bit zero prefix. Returns 0 when done, 1 when DecDCTvlcSize's limit stopped it
    /// (a later DecDCTvlc(0, buf, table) carries on).
    /// </summary>
    uint Vlc(uint bs, uint buf, uint table)
    {
        uint ac = table + 2048, acLong = ac + 0x10000;
        uint limit = m.Read32(vlcState) * 2;
        uint save = vlcState + 4, endPtr = vlcState + 0x28;
        uint src, dst, bits, used, qscale, block, dcCr, dcCb, dcY;   // block: 0 = v2, else v3 block 1..6 (Cr Cb Y Y Y Y)

        if (bs == 0)
        {
            src = m.Read32(save); dst = m.Read32(save + 4); bits = m.Read32(save + 8); used = m.Read32(save + 12);
            qscale = m.Read32(save + 16); block = m.Read32(save + 20);
            dcCr = m.Read32(save + 24); dcCb = m.Read32(save + 28); dcY = m.Read32(save + 32);
        }
        else
        {
            dcCr = dcCb = dcY = 0;
            uint head = m.Read32(bs);
            qscale = m.Read16(bs + 4) << 10;
            block = m.Read16(bs + 6) >= 3 ? 1u : 0;
            bits = m.Read16(bs + 8) << 16 | m.Read16(bs + 10);
            used = 0;
            src = bs + 12;
            m.Write32(buf, head);   // the MDEC command word: halfword count | 0x3800 << 16
            m.Write32(endPtr, buf + 4 + (head & 0xFFFF) * 4);
            dst = buf + 2;
        }
        uint end = (bs == 0 ? dst : buf) + limit;

        // `bits` holds the stream MSB first; `used` counts bits consumed from the last halfword loaded into it
        void Take(int n)
        {
            bits <<= n;
            used += (uint)n;
            if ((used & 0x10) != 0)
            {
                used &= 0xF;
                bits |= m.Read16(src) << (int)used;
                src += 2;
            }
            else used &= 0xF;
        }
        void Put(uint h) => m.Write16(dst, h);

        bool resume = bs == 0;
        while (true)
        {
            if (!resume)
            {
                // ---- DC coefficient of the next block
                dst += 2;
                if (block == 0)
                {
                    uint dc = bits >> 22;
                    if (dc == 0x1FF) break;   // end of frame
                    Take(10);
                    Put(qscale | dc);
                }
                else
                {
                    if (bits >> 22 == 0x3FF) break;
                    uint t = (block < 3 ? table + 1024 : table) + (bits >> 24) * 4;
                    int codeLen = (int)m.Read16(t), size = (int)m.Read16(t + 2);
                    bits <<= codeLen;
                    uint diff = 0;
                    if (size != 0)
                    {
                        diff = bits >> (32 - size);
                        if ((int)bits >= 0) diff -= 0xFFFFFFFF >> (32 - size);   // leading 0: negative
                        bits <<= size;
                        used += (uint)size;
                    }
                    used += (uint)codeLen;
                    Take(0);
                    ref uint pred = ref block == 1 ? ref dcCr : ref block == 2 ? ref dcCb : ref dcY;
                    pred += diff;
                    Put((pred << 2 & 0x3FF) | qscale);
                    if (++block == 7) block = 1;
                }
                if ((int)(dst - end) >= 0)
                {
                    dst += 2;
                    m.Write32(save, src); m.Write32(save + 4, dst); m.Write32(save + 8, bits); m.Write32(save + 12, used);
                    m.Write32(save + 16, qscale); m.Write32(save + 20, block);
                    m.Write32(save + 24, dcCr); m.Write32(save + 28, dcCb); m.Write32(save + 32, dcY);
                    return 1;
                }
                dst += 2;
            }
            resume = false;

            // ---- AC coefficients up to the end-of-block code; one lookup can yield up to three halfwords
            while (true)
            {
                uint e = ac + (bits >> 19) * 8;
                uint code = m.Read32(e), more = 0;
                if (code != 0) more = m.Read32(e + 4);
                else
                {
                    Take(8);
                    code = m.Read32(acLong + (bits >> 23) * 4);
                }
                Take((int)(code & 0xFF));
                uint h = code >> 16;
                bool escape = h == 0x7C1F;
                if (!escape)
                {
                    Put(h);
                    if (h == 0xFE00) break;
                    dst += 2;
                    if (more == 0) continue;
                    h = more & 0xFFFF;
                    escape = h == 0x7C1F;
                    if (!escape)
                    {
                        Put(h);
                        if (h == 0xFE00) break;
                        h = more >> 16;
                        dst += 2;
                        if (h == 0) continue;
                        escape = h == 0x7C1F;
                        if (!escape)
                        {
                            Put(h);
                            if (h == 0xFE00) break;
                            dst += 2;
                            continue;
                        }
                    }
                }
                // escape: the next 16 bits are the run/level halfword itself
                Put(bits >> 16);
                dst += 2;
                bits = bits << 16 | m.Read16(src) << (int)used;
                src += 2;
            }
        }
        // end of frame: pad with end-of-block codes up to the size in the header
        for (uint stop = m.Read32(endPtr); (int)(dst - stop) < 0; dst += 2) m.Write16(dst, 0xFE00);
        return 0;
    }

    /// <summary>--vlccheck: decode natively, then with Sony's code into the same buffer, and compare</summary>
    uint CheckedVlc(uint bs, uint buf, uint table)
    {
        uint r = Vlc(bs, buf, table);
        if (sonyVlc == null) return r;
        int len = (int)(m.Read32(vlcState + 0x28) - buf);
        var mine = m.RamSpan(buf, len).ToArray();
        uint a0 = m.R[4], a1 = m.R[5], a2 = m.R[6], ra = m.R[31], sp = m.R[29];
        sonyVlc!(m);
        uint sony = m.R[2];
        (m.R[4], m.R[5], m.R[6], m.R[31], m.R[29]) = (a0, a1, a2, ra, sp);
        var theirs = m.RamSpan(buf, len);
        int diff = 0, first = -1;
        for (int i = 0; i < len; i++) if (mine[i] != theirs[i]) { diff++; if (first < 0) first = i; }
        if (diff != 0 || r != sony) Psx.Log($"vlccheck: frame at {bs:X8}: {diff} bytes differ (first at +{first}), result {r} vs {sony}");
        else checkedFrames++;
        if (checkedFrames % 100 == 1) Psx.Log($"vlccheck: {checkedFrames} frames identical");
        return sony;
    }
    int checkedFrames;
}
