using System.Runtime.CompilerServices;

namespace Ps1tl.Port;

/// <summary>
/// The machine the recompiled code runs on: CPU registers, memory map, interrupt controller, DMA, timers and the
/// controller port. Recompiled functions and the interpreter both read and write this state directly; the
/// "hardware" is advanced lazily from <see cref="Cycles"/> whenever code passes a checkpoint (<see cref="Tick"/>).
/// </summary>
public sealed unsafe partial class Psx
{
    public const int CpuHz = 33868800;
    public const int LineCycles = 2152, Lines = 263, FrameCycles = LineCycles * Lines;

    public readonly uint[] R = new uint[32];
    public uint Hi, Lo;
    /// <summary>cop0: status register, cause, exception pc</summary>
    public uint Sr, Cause, Epc;

    public readonly byte[] RamArr = GC.AllocateArray<byte>(0x200000, pinned: true);
    public readonly byte* Ram;
    readonly byte[] scratchArr = GC.AllocateArray<byte>(0x400, pinned: true);
    readonly byte* scratch;

    public long Cycles;
    /// <summary>cycle at which <see cref="Tick"/> next has something to do</summary>
    public long NextEvent;

    public readonly Gpu Gpu;
    public readonly Cdrom Cd;
    public readonly Spu Spu;
    public readonly Mdec Mdec;
    public readonly Gte Gte = new();
    public readonly Bios Bios;
    public readonly PsyQ Sdk;
    public readonly Overlays Overlays;

    public static bool DmaTrace;
    public uint IStat, IMask;
    public ushort Buttons = 0xFFFF;   // active low, set by the front end
    public int Frame;
    /// <summary>called on every vblank with the finished frame (front end presents and paces)</summary>
    public Action? OnVBlank;

    public Psx(Ps1tl.Disc disc)
    {
        fixed (byte* p = RamArr) Ram = p;
        fixed (byte* p = scratchArr) scratch = p;
        Gpu = new Gpu(this);
        Cd = new Cdrom(this, disc);
        Spu = new Spu(this);
        Mdec = new Mdec(this);
        Bios = new Bios(this);
        Sdk = new PsyQ(this);
        Overlays = new Overlays(this);
        nextVblank = FrameCycles;
        for (int i = 0; i < 3; i++) timers[i] = new Timer { Index = i };
        Reschedule();
    }

    // ---------------------------------------------------------------- memory

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint Read32(uint a)
    {
        uint p = a & 0x1FFFFFFF;
        if (p < 0x800000) return *(uint*)(Ram + (p & 0x1FFFFC));
        return Read32Slow(p);
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint Read16(uint a)
    {
        uint p = a & 0x1FFFFFFF;
        if (p < 0x800000) return *(ushort*)(Ram + (p & 0x1FFFFE));
        return Read16Slow(p);
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint Read8(uint a)
    {
        uint p = a & 0x1FFFFFFF;
        if (p < 0x800000) return Ram[p & 0x1FFFFF];
        return Read8Slow(p);
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write32(uint a, uint v)
    {
        uint p = a & 0x1FFFFFFF;
        if (p < 0x800000 && (Sr & 0x10000) == 0) *(uint*)(Ram + (p & 0x1FFFFC)) = v;
        else Write32Slow(p, v);
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write16(uint a, uint v)
    {
        uint p = a & 0x1FFFFFFF;
        if (p < 0x800000 && (Sr & 0x10000) == 0) *(ushort*)(Ram + (p & 0x1FFFFE)) = (ushort)v;
        else Write16Slow(p, v);
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write8(uint a, uint v)
    {
        uint p = a & 0x1FFFFFFF;
        if (p < 0x800000 && (Sr & 0x10000) == 0) Ram[p & 0x1FFFFF] = (byte)v;
        else Write8Slow(p, v);
    }

    /// <summary>--ioprof: (pc, register | 1 for a write) -> count, to find the library code behind each device</summary>
    public Dictionary<(uint Pc, uint Reg), long>? IoProf;
    void IoHit(uint p, bool w) { if (IoProf != null && p - 0x1F801000 < 0x2000) System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(IoProf, (Pc, p | (w ? 1u : 0)), out _)++; }

    uint Read32Slow(uint p)
    {
        IoHit(p, false);
        if (p - 0x1F800000 < 0x400) return *(uint*)(scratch + (p & 0x3FC));
        if (p - 0x1F801000 < 0x2000) return Io32(p & ~3u);
        return 0;
    }
    uint Read16Slow(uint p)
    {
        IoHit(p, false);
        if (p - 0x1F800000 < 0x400) return *(ushort*)(scratch + (p & 0x3FE));
        if (p - 0x1F801000 < 0x2000) return IoRead(p & ~1u, 2);
        return 0;
    }
    uint Read8Slow(uint p)
    {
        IoHit(p, false);
        if (p - 0x1F800000 < 0x400) return scratch[p & 0x3FF];
        if (p - 0x1F801000 < 0x2000) return IoRead(p, 1);
        if (p - 0x1F000000 < 0x800000) return 0xFF;
        return 0;
    }
    void Write32Slow(uint p, uint v)
    {
        IoHit(p, true);
        if ((Sr & 0x10000) != 0) return;   // cache isolated: writes go to the (unemulated) i-cache
        if (p - 0x1F800000 < 0x400) *(uint*)(scratch + (p & 0x3FC)) = v;
        else if (p - 0x1F801000 < 0x2000) IoWrite(p & ~3u, v, 4);
    }
    void Write16Slow(uint p, uint v)
    {
        IoHit(p, true);
        if ((Sr & 0x10000) != 0) return;
        if (p - 0x1F800000 < 0x400) *(ushort*)(scratch + (p & 0x3FE)) = (ushort)v;
        else if (p - 0x1F801000 < 0x2000) IoWrite(p & ~1u, v & 0xFFFF, 2);
    }
    void Write8Slow(uint p, uint v)
    {
        IoHit(p, true);
        if ((Sr & 0x10000) != 0) return;
        if (p - 0x1F800000 < 0x400) scratch[p & 0x3FF] = (byte)v;
        else if (p - 0x1F801000 < 0x2000) IoWrite(p, v & 0xFF, 1);
    }

    public Span<byte> RamSpan(uint a, int n) => RamArr.AsSpan((int)(a & 0x1FFFFF), n);
    public string CString(uint a)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < 256; i++) { var c = Read8(a + (uint)i); if (c == 0) break; sb.Append((char)c); }
        return sb.ToString();
    }

    // ---------------------------------------------------------------- I/O

    uint Io32(uint p)
    {
        if (p == 0x1F801810) return Gpu.Read();
        if (p == 0x1F801814) return Gpu.Stat();
        if (p == 0x1F801820) return Mdec.ReadData();
        if (p == 0x1F801824) return Mdec.ReadStat();
        if (p >= 0x1F801C00 && p < 0x1F802000) return IoRead(p, 2) | IoRead(p + 2, 2) << 16;
        return IoRead(p, 4);
    }

    public HashSet<uint> UnknownIo = new();

    uint IoRead(uint p, int size)
    {
        if (Prof != null) System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(Prof, p | 1, out _)++;   // odd key: io read
        // upper halves of 32-bit DMA / timer registers
        if (size < 4 && p >= 0x1F801080 && p < 0x1F801130 && (p & 3) != 0) return IoRead(p & ~3u, 4) >> (int)((p & 3) * 8);
        switch (p)
        {
            case 0x1F801070: return IStat;
            case 0x1F801072: return IStat >> 16;
            case 0x1F801074: return IMask;
            case 0x1F801040: return SioRead();
            case 0x1F801044: return SioStat();
            case 0x1F801048: return sioMode;
            case 0x1F80104A: return sioCtrl;
            case 0x1F80104E: return sioBaud;
            case 0x1F8010F0: return dpcr;
            case 0x1F8010F4: return dicr;
            case 0x1F801810: return Gpu.Read();
            case 0x1F801814: return Gpu.Stat();
        }
        if (p >= 0x1F801080 && p < 0x1F8010F0)
        {
            var ch = dma[(p >> 4) & 7];
            return (p & 0xC) switch { 0 => ch.Madr, 4 => ch.Bcr, 8 => ch.Chcr, _ => 0 };
        }
        if (p >= 0x1F801100 && p < 0x1F801130)
        {
            var t = timers[(p >> 4) & 3];
            UpdateTimer(t);
            switch (p & 0xF)
            {
                case 0: return t.Count;
                case 4: { uint m = t.Mode; t.Mode &= ~0x1800u; return m; }
                case 8: return t.Target;
            }
            return 0;
        }
        if (p >= 0x1F801800 && p < 0x1F801804) return Cd.Read(p & 3);
        if (p >= 0x1F801C00 && p < 0x1F802000) return Spu.Read(p);
        if (p >= 0x1F801000 && p < 0x1F801064) return 0;   // memory control: games read back what they wrote
        if (UnknownIo.Add(p)) Log($"io read {p:X8}");
        return 0;
    }

    void IoWrite(uint p, uint v, int size)
    {
        if (size < 4 && p >= 0x1F801080 && p < 0x1F801100 && (p & 3) != 0)
        {
            int sh = (int)(p & 3) * 8;
            uint mask = (size == 2 ? 0xFFFFu : 0xFFu) << sh;
            uint cur = p >> 2 << 2 == 0x1F8010F4 ? dicr & 0x00FFFFFF : IoRead(p & ~3u, 4);
            IoWrite(p & ~3u, (cur & ~mask) | (v << sh & mask), 4);
            return;
        }
        switch (p)
        {
            case 0x1F801070: IStat &= v; CheckIrq(); return;
            case 0x1F801074: IMask = v & 0x7FF; CheckIrq(); return;
            case 0x1F801040: SioWrite((byte)v); return;
            case 0x1F801048: sioMode = (ushort)v; return;
            case 0x1F80104A: SioCtrl((ushort)v); return;
            case 0x1F80104E: sioBaud = (ushort)v; return;
            case 0x1F8010F0: dpcr = v; return;
            case 0x1F8010F4:
                dicr = (dicr & 0x7F000000 & ~(v & 0x7F000000)) | (v & 0x00FF803F);
                UpdateDicr();
                return;
            case 0x1F801810: Gpu.Gp0(v); return;
            case 0x1F801814: Gpu.Gp1(v); return;
            case 0x1F801820: Mdec.WriteCmd(v); return;
            case 0x1F801824: Mdec.WriteCtrl(v); return;
        }
        if (p >= 0x1F801080 && p < 0x1F8010F0)
        {
            int n = (int)(p >> 4) & 7;
            var ch = dma[n];
            switch (p & 0xC)
            {
                case 0: ch.Madr = v & 0xFFFFFF; break;
                case 4: ch.Bcr = v; break;
                case 8: ch.Chcr = v; if ((v & 0x01000000) != 0) DmaRun(n); break;
            }
            return;
        }
        if (p >= 0x1F801100 && p < 0x1F801130)
        {
            var t = timers[(p >> 4) & 3];
            UpdateTimer(t);
            switch (p & 0xF)
            {
                case 0: t.Count = v & 0xFFFF; break;
                case 4: t.Mode = (v & 0x3FF) | 0x400; t.Count = 0; break;
                case 8: t.Target = v & 0xFFFF; break;
            }
            ScheduleTimer(t);
            return;
        }
        if (p >= 0x1F801800 && p < 0x1F801804) { Cd.Write(p & 3, (byte)v); return; }
        if (p >= 0x1F801C00 && p < 0x1F802000)
        {
            Spu.Write(p, (ushort)v);
            if (size == 4) Spu.Write(p + 2, (ushort)(v >> 16));
            return;
        }
        if (p >= 0x1F801000 && p < 0x1F801064) return;
        if (UnknownIo.Add(p | 0x80000000)) Log($"io write {p:X8} = {v:X}");
    }

    // ---------------------------------------------------------------- interrupts

    public void Irq(int bit) { IStat |= 1u << bit; CheckIrq(); }

    void CheckIrq()
    {
        if ((IStat & IMask) != 0) Cause |= 0x400; else Cause &= ~0x400u;
        if (IrqPending) NextEvent = Cycles;   // service at the next checkpoint
    }

    public bool IrqPending => ((IStat & IMask) != 0 || Soft.Count > 0) && (Sr & 0x401) == 0x401;

    /// <summary>
    /// native SDK callbacks waiting for interrupt context (e.g. libcd's sync/ready callbacks): run like an
    /// interrupt handler would, when the game has interrupts enabled, but without any hardware behind them
    /// </summary>
    public readonly Queue<Action> Soft = new();

    public void Post(Action a)
    {
        Soft.Enqueue(a);
        if (IrqPending) NextEvent = Cycles;
    }

    // ---------------------------------------------------------------- scheduler

    long nextVblank;
    readonly List<(long At, Action Do)> timed = new();

    /// <summary>run `act` once `delay` cycles from now</summary>
    public void After(long delay, Action act)
    {
        timed.Add((Cycles + delay, act));
        if (Cycles + delay < NextEvent) NextEvent = Cycles + delay;
    }

    /// <summary>let time pass up to the next vblank, running everything due on the way (a native frame wait)</summary>
    public void RunToVBlank()
    {
        int f = Frame;
        while (Frame == f)
        {
            Cycles = Math.Max(Cycles, Math.Min(NextEvent, nextVblank));
            Tick();
        }
    }

    void Reschedule()
    {
        long n = nextVblank;
        foreach (var (at, _) in timed) if (at < n) n = at;
        foreach (var t in timers) if (t.NextIrq < n) n = t.NextIrq;
        NextEvent = IrqPending ? Cycles : n;
    }

    /// <summary>
    /// Checkpoint: called by generated code and the interpreter when Cycles reaches NextEvent (loop back-edges and
    /// calls). Advances the hardware and takes a pending interrupt.
    /// </summary>
    public void Tick()
    {
        if (Cycles >= nextVblank)
        {
            nextVblank += FrameCycles;
            Frame++;
            Gpu.VBlank();
            Irq(0);
            Spu.Frame();
            Bios.Card?.Tick();
            Sdk.Pad.VBlank();
            OnVBlank?.Invoke();
        }
        for (int i = 0; i < timed.Count; i++)
            if (timed[i].At <= Cycles)
            {
                var a = timed[i].Do;
                timed.RemoveAt(i--);
                a();
            }
        foreach (var t in timers) if (t.NextIrq <= Cycles) { UpdateTimer(t); ScheduleTimer(t); }
        // one interrupt per checkpoint: an irq nobody acknowledges must not stall the game
        bool take = IrqPending && !Bios.InException;
        if (take) { if ((IStat & IMask) != 0) Bios.Interrupt(); else Bios.RunSoft(Soft.Dequeue()); }
        Reschedule();
        if (take && IrqPending || Bios.InException) NextEvent = Math.Max(NextEvent, Cycles + 64);
    }

    // ---------------------------------------------------------------- DMA

    sealed class DmaCh { public uint Madr, Bcr, Chcr; }
    readonly DmaCh[] dma = [new(), new(), new(), new(), new(), new(), new(), new()];
    uint dpcr = 0x07654321, dicr;

    void UpdateDicr()
    {
        bool master = (dicr & 0x8000) != 0 || ((dicr & 0x800000) != 0 && (dicr >> 24 & dicr >> 16 & 0x7F) != 0);
        bool was = (dicr & 0x80000000) != 0;
        if (master) dicr |= 0x80000000; else dicr &= 0x7FFFFFFF;
        if (master && !was) Irq(3);
    }

    void DmaRun(int n)
    {
        var ch = dma[n];
        uint chcr = ch.Chcr;
        int sync = (int)(chcr >> 9) & 3;
        if (sync == 0 && (chcr & 0x10000000) == 0 && n != 6) return;   // manual mode waits for the trigger bit
        bool toRam = (chcr & 1) == 0;
        int step = (chcr & 2) != 0 ? -4 : 4;
        uint addr = ch.Madr & 0x1FFFFC;
        int words = sync switch
        {
            0 => (int)(ch.Bcr & 0xFFFF) is 0 ? 0x10000 : (int)(ch.Bcr & 0xFFFF),
            1 => (int)(ch.Bcr & 0xFFFF) * (int)(ch.Bcr >> 16),
            _ => 0,
        };
        long cost = words;
        // MDEC output is only there once the input has been decoded: wait for it
        if (n == 1 && Mdec.OutWords < words) { mdecOutWaiting = true; return; }
        if (n == 1) mdecOutWaiting = false;
        if (n == 6)
        {
            // ordering table clear: a reverse linked list of empty entries ending in the 0xFFFFFF terminator
            for (int i = 0; i < words; i++)
            {
                uint next = i == words - 1 ? 0xFFFFFF : (addr - 4) & 0x1FFFFC;
                *(uint*)(Ram + addr) = next;
                addr = (addr - 4) & 0x1FFFFC;
            }
        }
        else if (sync == 2)
        {
            // GPU linked list
            int guard = 0;
            while (guard++ < 0x40000)
            {
                uint hdr = *(uint*)(Ram + addr);
                int cnt = (int)(hdr >> 24);
                for (int i = 0; i < cnt; i++) Gpu.Gp0(*(uint*)(Ram + ((addr + 4 + (uint)i * 4) & 0x1FFFFC)));
                cost += cnt + 1;
                if ((hdr & 0x800000) != 0) break;
                addr = hdr & 0x1FFFFC;
            }
            ch.Madr = 0xFFFFFF;
        }
        else
        {
            if (n == 3) Overlays.Loaded(addr, (uint)words * 4);
            if (DmaTrace && n == 3) Log($"cd dma -> {addr | 0x80000000:X8} {words * 4} bytes, lba {Cd.LastLba} f{Frame}");
            for (int i = 0; i < words; i++)
            {
                if (toRam)
                {
                    uint v = n switch { 1 => Mdec.ReadData(), 2 => Gpu.Read(), 3 => Cd.DmaWord(), 4 => Spu.DmaRead(), _ => 0 };
                    *(uint*)(Ram + addr) = v;
                }
                else
                {
                    uint v = *(uint*)(Ram + addr);
                    switch (n) { case 0: Mdec.WriteCmd(v); break; case 2: Gpu.Gp0(v); break; case 4: Spu.DmaWrite(v); break; }
                }
                addr = (uint)(addr + step) & 0x1FFFFC;
            }
            if (sync == 1) { ch.Madr = addr; ch.Bcr &= 0xFFFF; }
        }
        // finish after a delay proportional to the size: the games poll CHCR / wait on the DMA irq
        After(Math.Max(16, cost), () => DmaDone(n));
    }

    bool mdecOutWaiting;
    public void MdecReady() { if (mdecOutWaiting && (dma[1].Chcr & 0x01000000) != 0) DmaRun(1); }

    void DmaDone(int n)
    {
        dma[n].Chcr &= ~0x11000000u;
        if ((dicr & (1u << (16 + n))) != 0) { dicr |= 1u << (24 + n); UpdateDicr(); }
    }

    // ---------------------------------------------------------------- timers

    sealed class Timer
    {
        public int Index;
        public uint Count, Mode = 0x400, Target;
        public long Last;      // cycle the count was last brought up to date
        public long Frac;      // sub-tick cycles carried over
        public long NextIrq = long.MaxValue;
    }
    readonly Timer[] timers = new Timer[3];

    int TimerDiv(Timer t)
    {
        int src = (int)(t.Mode >> 8) & 3;
        return t.Index switch
        {
            0 => (src & 1) != 0 ? 5 : 1,                 // dot clock ~ cpu/5 at 320px
            1 => (src & 1) != 0 ? LineCycles : 1,       // hblank
            _ => (src & 2) != 0 ? 8 : 1,
        };
    }

    void UpdateTimer(Timer t)
    {
        long el = Cycles - t.Last + t.Frac;
        t.Last = Cycles;
        int div = TimerDiv(t);
        long ticks = el / div;
        t.Frac = el % div;
        if (ticks == 0) return;
        uint target = t.Target == 0 ? 0x10000 : t.Target;
        long c = t.Count + ticks;
        bool resetAtTarget = (t.Mode & 8) != 0;
        bool hit = false, wrap = false;
        if (t.Count < target && c >= target) { hit = true; t.Mode |= 0x800; if (resetAtTarget) c = (c - target) % target; }
        else if (resetAtTarget && t.Count < target && c > target) c %= target;
        if (c > 0xFFFF) { wrap = true; t.Mode |= 0x1000; c &= 0xFFFF; }
        t.Count = (uint)c;
        if ((hit && (t.Mode & 0x10) != 0) || (wrap && (t.Mode & 0x20) != 0)) Irq(4 + t.Index);
    }

    void ScheduleTimer(Timer t)
    {
        t.NextIrq = long.MaxValue;
        if ((t.Mode & 0x30) == 0) return;
        uint target = (t.Mode & 0x10) != 0 && t.Target != 0 ? t.Target : 0x10000;
        long ticks = target > t.Count ? target - t.Count : 0x10000 - t.Count + target;
        t.NextIrq = Cycles + ticks * TimerDiv(t) - t.Frac;
        if (t.NextIrq <= Cycles) t.NextIrq = Cycles + 1;
    }

    // ---------------------------------------------------------------- controller port (digital pad in port 1)

    ushort sioMode, sioCtrl, sioBaud;
    int sioStep;           // position in the current pad transfer, -1 = device not answering
    byte sioRx; bool sioRxFull;

    byte SioRead() { sioRxFull = false; return sioRx; }
    uint SioStat() => 5u | (sioRxFull ? 2u : 0) | ((IStat & 0x80) != 0 ? 0x200u : 0);

    void SioCtrl(ushort v)
    {
        if ((v & 0x40) != 0) { sioCtrl = 0; sioStep = 0; sioRxFull = false; return; }
        if ((v & 0x10) != 0) IStat &= ~0x80u;   // acknowledge
        sioCtrl = (ushort)(v & ~0x50);
        if ((v & 2) == 0) sioStep = 0;           // /SEL released: next byte starts a new transfer
    }

    void SioWrite(byte b)
    {
        byte rx = 0xFF; bool ack = false;
        bool port2 = (sioCtrl & 0x2000) != 0;
        if (port2 || sioStep < 0) { sioStep = -1; }
        else
        {
            switch (sioStep)
            {
                case 0: if (b == 0x01) { ack = true; sioStep = 1; } else sioStep = -1; break;   // 0x81 = memory card: absent
                case 1: rx = 0x41; ack = b == 0x42; sioStep = ack ? 2 : -1; break;
                case 2: rx = 0x5A; ack = true; sioStep = 3; break;
                case 3: rx = (byte)Buttons; ack = true; sioStep = 4; break;
                case 4: rx = (byte)(Buttons >> 8); ack = false; sioStep = -1; break;
            }
        }
        sioRx = rx; sioRxFull = true;
        if (ack && (sioCtrl & 0x1000) != 0)
            After(400, () => Irq(7));
    }

    // ---------------------------------------------------------------- misc

    public static Action<string> Log = Console.WriteLine;
}
