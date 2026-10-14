namespace Ps1tl.Port;

/// <summary>
/// High-level BIOS: the A0/B0/C0 function tables, syscalls and the interrupt/exception path, done in C# instead of
/// running Sony's ROM. Only what the PsyQ libraries actually call needs to be here.
/// </summary>
public sealed class Bios(Psx m)
{
    const uint Sentinel = 0xFFFF0000;   // return address handed to guest interrupt handlers
    public bool InException;
    public readonly HashSet<string> Unknown = new();
    public bool Trace;

    sealed class ReturnFromException : Exception;

    // interrupt handler chain (SysEnqIntRP), by priority
    readonly List<uint>[] chain = [new(), new(), new(), new()];
    uint customExit;       // SetCustomExitFromException jmp_buf
    readonly uint[] saved = new uint[32];
    uint savedHi, savedLo;

    // events
    sealed class Ev { public uint Class, Spec, Mode, Func, Status; }
    readonly Ev?[] events = new Ev?[32];

    // pads (InitPad / StartPad): BIOS fills these buffers every vblank
    uint padBuf1, padBuf2;
    bool padStarted;

    // InitHeap / malloc
    uint heapStart, heapEnd;
    readonly SortedDictionary<uint, uint> heapUsed = new();

    /// <summary>
    /// kernel RAM the libraries peek at: the device control block table (0x150 = table, 0x154 = size in bytes,
    /// 0x50 per device). PsyQ's firstfile fix-up looks for the "bu" device there and patches a handler.
    /// </summary>
    public void InitKernel()
    {
        const uint Table = 0xE000, Names = 0xE400;
        string[] devs = ["cdrom", "bu", "tty"];
        for (int i = 0; i < devs.Length; i++)
        {
            uint name = Names + (uint)i * 16, dcb = Table + (uint)i * 0x50;
            for (int c = 0; c < devs[i].Length; c++) m.Write8(name + (uint)c, devs[i][c]);
            m.Write32(dcb, name);
        }
        m.Write32(0x150, Table);
        m.Write32(0x154, (uint)devs.Length * 0x50);
    }

    public static bool IsVector(uint pc) => (pc & 0x1FFFFFFF) is 0xA0 or 0xB0 or 0xC0;

    public bool Hle(uint pc)
    {
        if (!IsVector(pc)) return false;
        uint t = pc & 0xFF;
        uint fn = m.R[9] & 0xFF;
        Call(t, fn);
        return true;
    }

    uint A0 => m.R[4];
    uint A1 => m.R[5];
    uint A2 => m.R[6];
    uint A3 => m.R[7];
    uint V0 { set => m.R[2] = value; }

    void Call(uint table, uint fn)
    {
        if (Trace) Psx.Log($"bios {table:X2}:{fn:X2} ({A0:X}, {A1:X}, {A2:X}, {A3:X}) ra={m.R[31]:X8}");
        switch (table << 8 | fn)
        {
            // ---------------------------------------------------------------- A0
            case 0xA013: Setjmp(A0); V0 = 0; return;
            case 0xA017: V0 = (uint)string.CompareOrdinal(m.CString(A0), m.CString(A1)); return;
            case 0xA019: { var s = m.CString(A1); for (int i = 0; i <= s.Length; i++) m.Write8(A0 + (uint)i, i < s.Length ? s[i] : 0u); V0 = A0; return; }
            case 0xA01B: V0 = (uint)m.CString(A0).Length; return;
            case 0xA025: V0 = A0 is >= 'a' and <= 'z' ? A0 - 32 : A0; return;
            case 0xA028: for (uint i = 0; i < A1; i++) m.Write8(A0 + i, 0); V0 = A0; return;
            case 0xA02A: for (uint i = 0; i < A2; i++) m.Write8(A0 + i, m.Read8(A1 + i)); V0 = A0; return;
            case 0xA02B: for (uint i = 0; i < A2; i++) m.Write8(A0 + i, A1); V0 = A0; return;
            case 0xA02F: rand = rand * 0x41C64E6D + 12345; V0 = rand >> 16 & 0x7FFF; return;
            case 0xA030: rand = A0; return;
            case 0xA033: V0 = Malloc(A0); return;
            case 0xA034: heapUsed.Remove(A0); return;
            case 0xA039: heapStart = A0; heapEnd = A0 + A1; heapUsed.Clear(); return;
            case 0xA03F: Psx.Log("printf: " + m.CString(A0).TrimEnd()); return;
            case 0xA044: return;   // FlushCache
            case 0xA049: m.Gpu.Gp1(A0); return;   // GPU_cw
            case 0xA072: case 0xA096: case 0xA097: case 0xA099: case 0xA09F: case 0xA0A3: V0 = 0; return;
            case 0xA0A1: Psx.Log("SystemError"); return;
            case 0xA070: V0 = 0; return;
            // ---------------------------------------------------------------- B0
            case 0xB000: V0 = Malloc(A0); return;
            case 0xB007: DeliverEvent(A0, A1); return;
            case 0xB008: V0 = OpenEvent(A0, A1, A2, A3); return;
            case 0xB009: events[A0 & 31] = null; V0 = 1; return;
            case 0xB00A: V0 = WaitEvent(A0); return;
            case 0xB00B: V0 = TestEvent(A0); return;
            case 0xB00C: if (events[A0 & 31] is { } e) e.Status = 0x2000; V0 = 1; return;
            case 0xB00D: if (events[A0 & 31] is { } d) d.Status = 0x1000; V0 = 1; return;
            case 0xB00E: V0 = 0xFF000000; return;   // OpenTh: single-threaded
            case 0xB00F: V0 = 1; return;
            case 0xB010: V0 = 1; return;
            case 0xB012: padBuf1 = A0; padBuf2 = A2; V0 = 2; return;
            case 0xB013: padStarted = true; m.Sr |= 0x401; return;
            case 0xB014: padStarted = false; return;
            case 0xB017: ReturnFromExc(); return;
            case 0xB018: customExit = 0; return;
            case 0xB019: customExit = A0; return;
            case 0xB020: UnDeliverEvent(A0, A1); return;
            case 0xB035:   // write: stdout or a memory card file
                if (A0 == 1) { var sb = new System.Text.StringBuilder(); for (uint i = 0; i < A2; i++) sb.Append((char)m.Read8(A1 + i)); Psx.Log("tty: " + sb.ToString().TrimEnd()); V0 = A2; }
                else V0 = FileIo(A0, A1, A2, true);
                return;
            case 0xB032: V0 = Open(m.CString(A0), A1); return;
            case 0xB033: V0 = Seek(A0, (int)A1, A2); return;
            case 0xB034: V0 = FileIo(A0, A1, A2, false); return;
            case 0xB036: V0 = files.Remove(A0) ? A0 : 0xFFFFFFFF; return;
            case 0xB041: V0 = Card != null ? 1u : 0; return;   // format: the image is always formatted
            case 0xB042: dirPos = 0; dirPattern = m.CString(A0); V0 = NextFile(A1); if (Card != null) CardEvent(0xF0000011, 0x0004); return;   // the BIOS reads the directory: a low-level card event
            case 0xB043: V0 = NextFile(A0); return;
            case 0xB045: V0 = Card != null && CardName(m.CString(A0)) is { } en && Card.Erase(en) ? 1u : 0; return;
            case 0xB054: case 0xB055: V0 = 0; return;   // errno
            case 0xA0AB: case 0xA0AC:   // _card_info / _card_load: connected (IOE) or not (error)
                V0 = 1;
                if ((A0 & 0x10) != 0 || Card == null) CardEvent(0xF4000001, 0x8000);
                else CardEvent(0xF4000001, 0x0004);
                return;
            case 0xB04E: case 0xB04F:   // _card_write / _card_read(port, sector, buf)
                V0 = 1;
                if ((A0 & 0x10) != 0 || Card == null || A1 >= 1024) { CardEvent(0xF0000011, 0x8000); return; }
                for (uint i = 0; i < MemCard.Frame; i++)
                    if (fn == 0x4E) Card.Data[A1 * MemCard.Frame + i] = (byte)m.Read8(A2 + i);
                    else m.Write8(A2 + i, Card.Data[A1 * MemCard.Frame + i]);
                if (fn == 0x4E) Card.Save();
                CardEvent(0xF0000011, 0x0004);
                return;
            case 0xA09D: m.Write32(A0, 16); m.Write32(A1, 4); m.Write32(A2, 0x801FFF00); return;   // GetConf
            case 0xB03D: Console.Write((char)A0); return;
            case 0xB03F: Psx.Log(m.CString(A0)); return;
            case 0xB04A: case 0xB04B: case 0xB04C: case 0xB050: V0 = 1; return;   // InitCard / StartCard / StopCard / _new_card
            case 0xB056: V0 = 0x674; return;
            case 0xB057: V0 = 0x874; return;
            case 0xB058: V0 = 0; return;
            case 0xB05B: return;   // ChangeClearPad
            case 0xB05C: V0 = 0; return;
            case 0xB05D: V0 = 0; return;
            // ---------------------------------------------------------------- C0
            case 0xC002: if (!chain[A0 & 3].Contains(A1)) chain[A0 & 3].Add(A1); V0 = 0; return;
            case 0xC003: chain[A0 & 3].Remove(A1); V0 = 0; return;
            case 0xC00A: { int t = (int)(A0 & 3); V0 = clearRcnt[t] ? 1u : 0; clearRcnt[t] = A1 != 0; return; }   // ChangeClearRCnt
            case 0xC000: case 0xC001: case 0xC007: case 0xC008: case 0xC00C: case 0xC012: case 0xC01C: return;
        }
        if (Unknown.Add($"{table:X2}:{fn:X2}")) Psx.Log($"unknown bios {table:X2}:{fn:X2} ({A0:X}, {A1:X}, {A2:X}) ra={m.R[31]:X8}");
        V0 = 0;
    }

    uint rand = 1;

    // ---------------------------------------------------------------- memory card files (bu00:)

    public MemCard? Card;
    sealed class Fd { public int First; public int Pos; public bool Async; }
    readonly Dictionary<uint, Fd> files = new();
    string dirPattern = "";
    int dirPos;

    /// <summary>the card a "buXY:" device name refers to (only slot 1), -> file name</summary>
    string? CardName(string dev)
    {
        if (Card == null || !dev.StartsWith("bu00:", StringComparison.OrdinalIgnoreCase)) return null;
        return dev[5..];
    }

    /// <summary>card completion events arrive a little later, as from the real (slow) card</summary>
    void CardEvent(uint cls, uint spec) => m.After(20000, () => DeliverEvent(cls, spec));

    uint Open(string dev, uint mode)
    {
        if (Trace) Psx.Log($"  open {dev} {mode:X}");
        if (CardName(dev) is not { } name) return 0xFFFFFFFF;
        int b = Card!.Find(name);
        if (b == 0 && (mode & 0x200) != 0) b = Card.Create(name, (int)Math.Max(1, mode >> 16));
        if (b == 0) return 0xFFFFFFFF;
        uint fd = 2;
        while (files.ContainsKey(fd)) fd++;
        files[fd] = new Fd { First = b, Async = (mode & 0x8000) != 0 };
        return fd;
    }

    uint Seek(uint fd, int off, uint whence)
    {
        if (!files.TryGetValue(fd, out var f)) return 0xFFFFFFFF;
        f.Pos = whence switch { 0 => off, 1 => f.Pos + off, _ => Card!.Chain(f.First).Count * MemCard.BlockSize + off };
        return (uint)f.Pos;
    }

    uint FileIo(uint fd, uint buf, uint len, bool write)
    {
        if (!files.TryGetValue(fd, out var f)) return 0xFFFFFFFF;
        for (uint i = 0; i < len; i++)
        {
            int o = Card!.Offset(f.First, f.Pos + (int)i);
            if (o < 0) { len = i; break; }
            if (write) Card.Data[o] = (byte)m.Read8(buf + i); else m.Write8(buf + i, Card.Data[o]);
        }
        f.Pos += (int)len;
        if (write) Card!.Save();
        if (f.Async) CardEvent(0xF4000001, 0x0004);
        return len;
    }

    uint NextFile(uint ent)
    {
        if (CardName(dirPattern) is not { } pat) return 0;
        var rx = new System.Text.RegularExpressions.Regex("^" + System.Text.RegularExpressions.Regex.Escape(pat).Replace(@"\*", ".*").Replace(@"\?", ".") + "$");
        var list = Card!.Files().ToList();
        while (dirPos < list.Count)
        {
            int b = list[dirPos++];
            var name = Card.Name(b);
            if (!rx.IsMatch(name)) continue;
            for (uint i = 0; i < 40; i++) m.Write8(ent + i, 0);
            for (int i = 0; i < Math.Min(19, name.Length); i++) m.Write8(ent + (uint)i, name[i]);
            m.Write32(ent + 0x14, 0x50);
            m.Write32(ent + 0x18, (uint)Card.FileSize(b));
            m.Write32(ent + 0x20, (uint)b);
            return ent;
        }
        return 0;
    }
    /// <summary>ChangeClearRCnt: whether the BIOS acknowledges root counter 0-2 / vblank (3) irqs itself</summary>
    readonly bool[] clearRcnt = [true, true, true, true];

    uint Malloc(uint size)
    {
        size = (size + 7) & ~7u;
        uint at = heapStart;
        foreach (var (a, s) in heapUsed)
        {
            if (a - at >= size) break;
            at = Math.Max(at, a + s);
        }
        if (at + size > heapEnd) { Psx.Log($"malloc({size}) failed"); return 0; }
        heapUsed[at] = size;
        return at;
    }

    // ---------------------------------------------------------------- events

    uint OpenEvent(uint cls, uint spec, uint mode, uint func)
    {
        for (int i = 0; i < events.Length; i++)
            if (events[i] == null)
            {
                events[i] = new Ev { Class = cls, Spec = spec, Mode = mode, Func = func, Status = 0x1000 };
                if (Trace) Psx.Log($"  OpenEvent {cls:X8} {spec:X} {mode:X} {func:X8} -> {i}");
                return 0xF1000000 | (uint)i;
            }
        return 0xFFFFFFFF;
    }

    public void DeliverEvent(uint cls, uint spec)
    {
        foreach (var e in events)
        {
            if (e == null || e.Class != cls || e.Status != 0x2000 || (e.Spec & spec) == 0) continue;
            if (e.Mode == 0x2000) e.Status = 0x4000;
            else if (e.Func != 0) CallGuest(e.Func);
        }
    }

    void UnDeliverEvent(uint cls, uint spec)
    {
        foreach (var e in events)
            if (e != null && e.Class == cls && e.Status == 0x4000 && (e.Spec & spec) != 0 && e.Mode == 0x2000) e.Status = 0x2000;
    }

    uint TestEvent(uint h)
    {
        var e = events[h & 31];
        if (e == null || e.Status != 0x4000) return 0;
        e.Status = 0x2000;
        return 1;
    }

    uint WaitEvent(uint h)
    {
        var e = events[h & 31];
        if (e == null || e.Status is not (0x2000 or 0x4000)) return 0;
        while (e.Status != 0x4000) { m.Cycles = Math.Max(m.Cycles, m.NextEvent); m.Tick(); }
        e.Status = 0x2000;
        return 1;
    }

    /// <summary>call a guest function from BIOS code, preserving the caller's registers</summary>
    void CallGuest(uint func, uint a0 = 0)
    {
        var keep = (uint[])m.R.Clone();
        uint hi = m.Hi, lo = m.Lo;
        m.R[4] = a0;
        m.R[31] = Sentinel;
        if (m.R[29] == 0) m.R[29] = 0x801FFF00;
        m.R[29] -= 0x40;
        m.Call(func);
        uint v0 = m.R[2];
        Array.Copy(keep, m.R, 32);
        m.Hi = hi; m.Lo = lo;
        m.R[2] = v0;
    }

    // ---------------------------------------------------------------- exceptions

    public void Syscall(uint pc)
    {
        switch (m.R[4])
        {
            case 1: { bool was = (m.Sr & 0x401) == 0x401; m.Sr &= ~0x404u; m.R[2] = was ? 1u : 0; break; }   // EnterCriticalSection
            case 2: m.Sr |= 0x404; if (m.IrqPending) m.NextEvent = m.Cycles; break;                            // ExitCriticalSection
            default: Psx.Log($"syscall {m.R[4]} at {pc:X8}"); break;
        }
    }

    /// <summary>take a hardware interrupt: run the SysEnqIntRP chain, then the game's custom exit, then resume</summary>
    public void Interrupt()
    {
        InException = true;
        Array.Copy(m.R, saved, 32);
        savedHi = m.Hi; savedLo = m.Lo;
        uint sr = m.Sr;
        m.Sr = (sr & ~0x3Fu) | (sr << 2 & 0x3C);
        try
        {
            if (padStarted && (m.IStat & 1) != 0) FillPads();
            for (int p = 0; p < 4; p++)
                foreach (var s in chain[p].ToArray())
                {
                    uint first = m.Read32(s + 8), second = m.Read32(s + 4);
                    if (first == 0) continue;
                    m.R[29] = 0x801FFF00 - 0x400;   // the BIOS runs handlers on its own stack
                    CallGuestRaw(first, 0);
                    if (m.R[2] != 0 && second != 0) CallGuestRaw(second, m.R[2]);
                }
            DefaultHandlers();
            if (customExit != 0)
            {
                // longjmp into the game's handler; it ends with ReturnFromException
                uint b = customExit;
                m.R[31] = Sentinel;
                m.R[29] = m.Read32(b + 4);
                m.R[30] = m.Read32(b + 8);
                for (int i = 0; i < 8; i++) m.R[16 + i] = m.Read32(b + 12 + (uint)i * 4);
                m.R[28] = m.Read32(b + 44);
                m.R[2] = 1;
                m.Call(m.Read32(b));
            }
            Restore();
        }
        catch (ReturnFromException) { }
        m.Sr = sr;
        InException = false;
    }

    void CallGuestRaw(uint func, uint a0)
    {
        m.R[4] = a0;
        m.R[31] = Sentinel;
        m.Call(func);
    }

    void Restore()
    {
        Array.Copy(saved, m.R, 32);
        m.Hi = savedHi; m.Lo = savedLo;
    }

    void ReturnFromExc()
    {
        if (!InException) { Psx.Log("ReturnFromException outside an exception"); return; }
        Restore();
        throw new ReturnFromException();
    }

    /// <summary>what Sony's own handlers do: root counter / vblank events</summary>
    void DefaultHandlers()
    {
        uint st = m.IStat & m.IMask;
        if ((st & 1) != 0) { DeliverEvent(0xF2000003, 2); if (clearRcnt[3]) m.IStat &= ~1u; }
        for (int t = 0; t < 3; t++)
            if ((st & (0x10u << t)) != 0) { DeliverEvent(0xF2000000 + (uint)t, 2); if (clearRcnt[t]) m.IStat &= ~(0x10u << t); }
    }

    void FillPads()
    {
        if (padBuf1 != 0)
        {
            m.Write8(padBuf1, 0); m.Write8(padBuf1 + 1, 0x41);
            m.Write8(padBuf1 + 2, m.Buttons); m.Write8(padBuf1 + 3, (uint)m.Buttons >> 8);
        }
        if (padBuf2 != 0) m.Write8(padBuf2, 0xFF);
    }

    void Setjmp(uint b)
    {
        m.Write32(b, m.R[31]); m.Write32(b + 4, m.R[29]); m.Write32(b + 8, m.R[30]);
        for (int i = 0; i < 8; i++) m.Write32(b + 12 + (uint)i * 4, m.R[16 + i]);
        m.Write32(b + 44, m.R[28]);
    }
}
