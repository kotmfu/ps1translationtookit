namespace Ps1tl.Port;

/// <summary>
/// Native libcd + libcds (CD access and movie streaming), part of the PsyQ replacement. Commands go straight to
/// the drive model (disc image, seek/read timing, XA audio to the SPU) and its answers come back as libcd's
/// sync / ready callbacks, run in interrupt context from the scheduler: no CD registers, no IRQ 2, no DMA 3.
/// libcds' ring buffer is filled here from the sectors the drive reads, with the same layout and rules as
/// Sony's StCdInterrupt, so the game's movie player reads it unchanged.
/// </summary>
public sealed class LibCd(Psx m)
{
    uint A0 => m.R[4];
    uint A1 => m.R[5];
    uint A2 => m.R[6];
    uint A3 => m.R[7];
    uint V0 { set => m.R[2] = value; }

    const byte NoIntr = 0, Complete = 2, DiskError = 5;

    uint syncCb, readyCb, dataCb, stReady;
    byte syncStatus = Complete, readyStatus;
    readonly byte[] syncResult = new byte[8], readyResult = new byte[8];
    bool syncBusy, acked;
    byte com;
    byte[] sector = [];
    int sectorPos;
    Dictionary<string, (int Lba, int Size)>? files;

    public Dictionary<string, Action> Impl(IReadOnlyDictionary<string, uint> symbols)
    {
        stReady = symbols.GetValueOrDefault("@StReady");
        if (symbols.ContainsKey("CdControlF")) m.Cd.Sink = OnInt;
        return new()
        {
            ["CdInit"] = () => V0 = 1,
            ["CdReset"] = () => V0 = 1,
            ["CdFlush"] = () => { },
            ["CdSetDebug"] = () => V0 = 0,
            ["CdSync"] = () => V0 = Sync((int)A0, A1),
            ["CdReady"] = () => V0 = Ready((int)A0, A1),
            ["CdSyncCallback"] = () => { V0 = syncCb; syncCb = A0; },
            ["CdReadyCallback"] = () => { V0 = readyCb; readyCb = A0; },
            ["CdDataCallback"] = () => { V0 = dataCb; dataCb = A0; },
            ["CdDataSync"] = () => V0 = 0,                    // sector copies are done at once
            ["CdControl"] = () => V0 = Control((byte)A0, Param((byte)A0, A1), A2, wait: 1),
            ["CdControlF"] = () => V0 = Control((byte)A0, Param((byte)A0, A1), 0, wait: 0),
            ["CdControlB"] = () => V0 = Control((byte)A0, Param((byte)A0, A1), A2, wait: 2),
            ["CdMix"] = () => V0 = 1,                         // CD audio goes to the SPU at full volume
            ["CdGetSector"] = () => { GetSector(A0, (int)A1); V0 = 1; },
            ["CdSearchFile"] = () => V0 = SearchFile(A0, A1),
            ["CdRead2"] = () => V0 = Read2(A0),
            ["StSetRing"] = () => { ring = A0; size = (int)A1; ClearRing(); },
            ["StClearRing"] = ClearRing,
            ["StUnSetRing"] = () => { readyCb = 0; dataCb = 0; },
            ["StSetStream"] = () =>
            {
                (mask, start, end) = (1, A1, A2);
                func1 = A3; func2 = m.Read32(m.R[29] + 16);
                chan = 0; secCount = 0; curFrame = 0;
            },
            ["StSetMask"] = () => (mask, start, end) = (A0, A1, A2),
            ["StGetNext"] = () => V0 = GetNext(A0, A1),
            ["StFreeRing"] = () => V0 = FreeRing(A0),
            ["StGetBackloc"] = () => V0 = GetBackloc(A0),
            ["StCdInterrupt"] = () => { },                   // never deferred here, so nothing is ever pending
        };
    }

    // ------------------------------------------------------------------ commands

    /// <summary>commands that answer twice (acknowledge, then complete); the rest are complete when acknowledged</summary>
    static bool TwoStep(byte c) => c is 0x07 or 0x08 or 0x09 or 0x0A or 0x15 or 0x16 or 0x1A or 0x1E;

    static int ParamLen(byte c) => c switch { 0x02 => 3, 0x0D => 2, 0x0E => 1, 0x14 => 1, 0x19 => 1, _ => 0 };

    static int Bcd(uint b) => (int)((b >> 4) * 10 + (b & 15));
    static byte ToBcd(int v) => (byte)(v / 10 << 4 | v % 10);

    /// <summary>a command's parameter bytes from the guest's param pointer</summary>
    byte[] Param(byte c, uint param)
    {
        // reads, seeks and play take their start position as the parameter: libcd sends Setloc first
        if (param != 0 && c is 0x03 or 0x06 or 0x1B or 0x15 or 0x16)
            m.Cd.Seek((Bcd(m.Read8(param)) * 60 + Bcd(m.Read8(param + 1))) * 75 + Bcd(m.Read8(param + 2)) - 150);
        var p = new byte[param == 0 ? 0 : ParamLen(c)];
        for (int i = 0; i < p.Length; i++) p[i] = (byte)m.Read8(param + (uint)i);
        return p;
    }

    /// <summary>CdControl / F / B: wait 0 = don't, 1 = for the acknowledge, 2 = for completion</summary>
    uint Control(byte c, byte[] p, uint result, int wait)
    {
        if (Cdrom.Trace) Psx.Log($"libcd: control{(wait == 0 ? "F" : wait == 2 ? "B" : "")} {c:X2} [{string.Join(' ', p.Select(b => b.ToString("X2")))}] f{m.Frame}");
        com = c;
        syncBusy = true; acked = false;
        syncStatus = NoIntr;
        m.Cd.Send(c, p);
        if (wait == 0) return 1;
        Wait(() => wait == 1 ? acked : !syncBusy);
        if (result != 0) for (int i = 0; i < 8; i++) m.Write8(result + (uint)i, syncResult[i]);
        return syncStatus == DiskError ? 0u : 1u;
    }

    uint Sync(int mode, uint result)
    {
        if (mode == 0) Wait(() => !syncBusy);
        if (result != 0) for (int i = 0; i < 8; i++) m.Write8(result + (uint)i, syncResult[i]);
        return syncStatus;
    }

    uint Ready(int mode, uint result)
    {
        if (mode == 0) Wait(() => readyStatus != NoIntr);
        if (result != 0) for (int i = 0; i < 8; i++) m.Write8(result + (uint)i, readyResult[i]);
        return readyStatus;
    }

    /// <summary>let time pass until the drive has answered (the drive's answers are handled even with interrupts off)</summary>
    void Wait(Func<bool> done)
    {
        for (long guard = 0; !done() && guard < 10_000_000; guard++)
        {
            if (m.Soft.Count > 0) { m.Bios.RunSoft(m.Soft.Dequeue()); continue; }
            m.Cycles = Math.Max(m.Cycles, m.NextEvent);
            m.Tick();
        }
    }

    /// <summary>the drive answered: handled in interrupt context, like libcd's IRQ handler</summary>
    void OnInt(byte type, byte[] resp, byte[]? data) => m.Post(() => Handle(type, resp, data));

    void Handle(byte type, byte[] resp, byte[]? data)
    {
        if (Cdrom.Trace) Psx.Log($"libcd: int{type} [{string.Join(' ', resp.Select(b => b.ToString("X2")))}] after {com:X2}" + (data is { Length: > 2048 } ? $" sector {data[0]:X2}:{data[1]:X2}:{data[2]:X2}" : ""));
        switch (type)
        {
            case 3:   // acknowledge
                acked = true;
                Fill(syncResult, resp);
                if (!TwoStep(com)) Done(Complete);
                break;
            case 2:
                Fill(syncResult, resp);
                Done(Complete);
                break;
            case 1:
            case 4:
                if (data != null) { sector = data; sectorPos = 0; }
                Fill(readyResult, resp);
                readyStatus = type;
                if (type == 1 && readyCb != 0 && readyCb == stReady) StInterrupt();
                else if (readyCb != 0) m.Bios.IrqCallback(readyCb, type, readyResult);
                break;
            case 5:
                if (syncBusy) { acked = true; Fill(syncResult, resp); Done(DiskError); }
                else { Fill(readyResult, resp); readyStatus = DiskError; if (readyCb != 0 && readyCb != stReady) m.Bios.IrqCallback(readyCb, DiskError, readyResult); }
                break;
        }
    }

    void Done(byte status)
    {
        syncBusy = false;
        syncStatus = status;
        if (syncCb != 0) m.Bios.IrqCallback(syncCb, status, syncResult);
    }

    static void Fill(byte[] dst, byte[] src)
    {
        Array.Clear(dst);
        src.AsSpan(0, Math.Min(8, src.Length)).CopyTo(dst);
    }

    void GetSector(uint madr, int words)
    {
        for (int i = 0; i < words * 4; i++, sectorPos++)
            m.Write8(madr + (uint)i, sectorPos < sector.Length ? sector[sectorPos] : 0u);
        m.Overlays.Loaded(madr, (uint)words * 4);
    }

    uint Read2(uint mode)
    {
        Control(0x0E, [(byte)mode], 0, wait: 1);
        if ((mode & 0x100) != 0) { wholeSector = (mode & 0x20) != 0; readyCb = stReady; }
        Control(0x1B, [], 0, wait: 1);
        return 1;
    }

    /// <summary>CdSearchFile(CdlFILE *fp, char *name): position, size and name of a file, or 0</summary>
    uint SearchFile(uint fp, uint namePtr)
    {
        var sb = new System.Text.StringBuilder();
        for (uint i = 0; i < 64 && m.Read8(namePtr + i) is var c and not 0; i++) sb.Append((char)c);
        string name = sb.ToString();
        string key = "/" + name.Split(';')[0].Replace('\\', '/').TrimStart('/').ToUpperInvariant();
        files ??= m.Cd.Disc.Files();
        if (!files.TryGetValue(key, out var f)) { Psx.Log($"CdSearchFile: {name} not found"); return 0; }
        int l = f.Lba + 150;
        m.Write8(fp, ToBcd(l / 75 / 60)); m.Write8(fp + 1, ToBcd(l / 75 % 60)); m.Write8(fp + 2, ToBcd(l % 75)); m.Write8(fp + 3, 0);
        m.Write32(fp + 4, (uint)f.Size);
        var leaf = name.Replace('\\', '/').Split('/')[^1];
        for (int i = 0; i < 16; i++) m.Write8(fp + 8 + (uint)i, i < leaf.Length ? leaf[i] : 0u);
        return fp;
    }

    // ------------------------------------------------------------------ libcds ring buffer

    // ring: `size` 32-byte sector headers (u16 state: 0 free, 1 wrap marker, 2 frame ready, 3 written, 4 in use;
    // a copy of the STR sector header otherwise, its last word replaced by the sector's CD position), then
    // `size` 2016-byte data slots. A frame takes consecutive slots; its first header says the frame's state.
    uint ring;
    int size;
    int write, frameStart, read;      // slot being written, first slot of the frame being written, next to read
    int secCount;
    uint curFrame, chan;
    uint mask, start, end, func1, func2;
    bool wholeSector = true;
    uint lastLoc, lastFrame;

    uint Hdr(int i) => ring + (uint)i * 32;
    uint Slot(int i) => ring + (uint)size * 32 + (uint)i * 2016;
    uint State(int i) => m.Read16(Hdr(i));

    void ClearRing()
    {
        read = frameStart = write = secCount = 0;
        curFrame = 0;
        for (int i = 0; i < size; i++) m.Write32(Hdr(i), 0);
    }

    void Free(int from, int n) { for (int i = 0; i < n; i++) m.Write32(Hdr(from + i), 0); }

    /// <summary>
    /// abandon the frame being assembled (a sector was lost or out of order)
    /// </summary>
    void DropFrame()
    {
        Free(frameStart, write - frameStart);
        write = frameStart;
        secCount = 0;
        curFrame = 0;
    }

    /// <summary>one movie sector has arrived: StCdInterrupt</summary>
    void StInterrupt()
    {
        if (ring == 0 || size == 0) return;
        if ((readyResult[0] & 0x04) != 0) return;
        uint p = Hdr(write);
        if (State(write) != 0) return;   // ring full: the sector is lost
        int o = wholeSector ? 12 : 0;    // header + subheader in front of the data
        if (sector.Length < o + 32 + 2016) return;
        for (int i = 0; i < 32; i++) m.Write8(p + (uint)i, sector[o + i]);
        for (int i = 0; i < 4; i++) m.Write8(p + 28 + (uint)i, sector[i]);
        if (mask == 1 && start != 0)
        {
            if (m.Read16(p + 8) != start) { m.Write16(p, 0); return; }
            mask = 0;
        }
        if (m.Read16(p) != 0x160 || (m.Read16(p + 2) >> 10 & 0x1F) != chan) { m.Write16(p, 0); return; }
        uint sec = m.Read16(p + 4), count = m.Read16(p + 6), frame = m.Read16(p + 8);
        if (secCount != sec || curFrame != 0 && curFrame != frame) { DropFrame(); m.Write16(p, 0); return; }
        if (sec == 0)
        {
            secCount = 0;
            curFrame = frame;
            if (end != 0 && frame >= end)
            {
                DropFrame(); m.Write16(p, 0); mask = 1;
                if (func2 != 0) m.Bios.IrqCallback(func2);
                return;
            }
            if (size - write - 1 < count)
            {
                // no room before the end of the ring: leave a wrap marker and start again at slot 0
                if (end == 0) { m.Write16(p, 1); mask = 1; if (func2 != 0) m.Bios.IrqCallback(func2); return; }
                if (State(0) != 0) { m.Write16(p, 0); return; }
                m.Write16(p, 1);
                for (uint i = 0; i < 32; i += 4) m.Write32(Hdr(0) + i, m.Read32(p + i));
                write = 0;
                p = Hdr(0);
            }
            frameStart = write;
        }
        secCount++;
        m.RamSpan(Slot(write), 2016).Clear();
        sector.AsSpan(o + 32, 2016).CopyTo(m.RamSpan(Slot(write), 2016));
        m.Write16(p, 3);
        write++;
        if (sec == count - 1)
        {
            secCount = 0;
            curFrame = 0;
            uint f = Hdr(frameStart);
            m.Write16(f, 2);
            lastLoc = m.Read32(f + 28);
            lastFrame = m.Read32(f + 8);
            frameStart = write;
            if (Cdrom.Trace) Psx.Log($"libcds: frame {lastFrame} ready f{m.Frame}");
            if (func1 != 0) m.Bios.IrqCallback(func1);
        }
    }

    /// <summary>StGetNext(u_long **addr, u_long **header): the oldest complete frame, 0 = got one</summary>
    uint GetNext(uint addr, uint header)
    {
        if (ring == 0) return 1;
        if (State(read) == 1) { if (end != 0) m.Write16(Hdr(read), 0); read = 0; }
        if (State(read) != 2) return 1;
        m.Write16(Hdr(read), 4);
        m.Write32(addr, Slot(read));
        m.Write32(header, Hdr(read));
        return 0;
    }

    /// <summary>StFreeRing(u_long *base): give a frame's slots back</summary>
    uint FreeRing(uint data)
    {
        int i = (int)((data - Slot(0)) / 2016);
        if (i < 0 || i >= size || State(i) != 4) return 1;
        int n = (short)m.Read16(Hdr(i) + 6);
        Free(i, Math.Max(n, 0));
        read = i + Math.Max(n, 0);
        return 0;
    }

    /// <summary>
    /// --selftest: movie frames written sector by sector come out of StGetNext whole and in order, also across
    /// the ring's wrap-around
    /// </summary>
    public void SelfTest()
    {
        const uint ringAt = 0x80100000;
        const int frames = 6, sectors = 8;
        ring = ringAt; size = 20; wholeSector = true; ClearRing();
        (mask, start, end, func1, func2, chan) = (1, 1, 0xFFFFFFFF, 0, 0, 0);
        const uint next = ringAt - 16;   // where StGetNext puts its two pointers
        for (int f = 1; f <= frames; f++)
        {
            for (int k = 0; k < sectors; k++)
            {
                var sec = new byte[0x924];
                BitConverter.GetBytes((ushort)0x160).CopyTo(sec, 12);
                BitConverter.GetBytes((ushort)0x8001).CopyTo(sec, 14);
                BitConverter.GetBytes((ushort)k).CopyTo(sec, 16);
                BitConverter.GetBytes((ushort)sectors).CopyTo(sec, 18);
                BitConverter.GetBytes(f).CopyTo(sec, 20);
                sec.AsSpan(44, 2016).Fill((byte)(f * 16 + k));
                sector = sec;
                StInterrupt();
            }
            if (GetNext(next, next + 4) != 0) throw new Exception($"frame {f} not ready");
            uint data = m.Read32(next);
            for (int k = 0; k < sectors; k++)
                if (m.Read8(data + (uint)(k * 2016 + 100)) != (uint)(f * 16 + k)) throw new Exception($"frame {f} sector {k} wrong");
            if (m.Read32(m.Read32(next + 4) + 8) != f || FreeRing(data) != 0) throw new Exception($"frame {f} header/free");
        }
        if (GetNext(next, next + 4) == 0) throw new Exception("phantom frame");
        Psx.Log("libcds ring selftest ok");
    }

    /// <summary>StGetBackloc(CdlLOC *loc): the position after the last complete frame, and its frame number</summary>
    uint GetBackloc(uint loc)
    {
        if (!wholeSector) return 0xFFFFFFFF;
        int l = (Bcd(lastLoc & 0xFF) * 60 + Bcd(lastLoc >> 8 & 0xFF)) * 75 + Bcd(lastLoc >> 16 & 0xFF) + 1;
        m.Write8(loc, ToBcd(l / 75 / 60)); m.Write8(loc + 1, ToBcd(l / 75 % 60)); m.Write8(loc + 2, ToBcd(l % 75));
        return lastFrame;
    }
}
