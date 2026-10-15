namespace Ps1tl.Port;

/// <summary>
/// CD-ROM controller at register level (what libcd talks to): command/parameter/response FIFOs, the five
/// interrupt types, sector reads at 1x/2x speed from the bin image, and XA-ADPCM sectors routed to the SPU.
/// </summary>
public sealed class Cdrom(Psx m, Ps1tl.Disc disc)
{
    const int SectorCycles = Psx.CpuHz / 75;

    int index;
    byte ie = 0x1F, iflag;   // the BIOS leaves all CD interrupts enabled
    readonly Queue<byte> param = new();
    readonly Queue<byte> resp = new();
    // interrupts waiting for the previous one to be acknowledged: (type, response bytes)
    readonly Queue<(byte Int, byte[] Resp)> pending = new();
    bool busy;

    byte mode;
    byte stat = 0x02;   // motor on
    int setLoc, readLoc;
    bool setLocPending;
    bool reading, muted;
    byte filterFile, filterChan;
    long readGen;        // bumped to cancel a running read

    readonly byte[] raw = new byte[2352];
    readonly byte[] sectorBuf = new byte[2352];
    int sectorLen;
    readonly byte[] data = new byte[2352];
    int dataLen, dataPos;
    byte lastHeaderMin, lastHeaderSec, lastHeaderFrame, lastHeaderMode, lastSub0, lastSub1, lastSub2, lastSub3;

    public readonly Xa Xa = new();
    public static bool Trace;
    public int LastLba;
    public Ps1tl.Disc Disc => disc;

    /// <summary>
    /// native libcd (PsyQ.Cd.cs): when set, the drive's interrupts go here with their response bytes (and, for
    /// data ready, a copy of the sector) instead of through the registers and IRQ 2
    /// </summary>
    public Action<byte, byte[], byte[]?>? Sink;

    /// <summary>native libcd: issue a command with its parameter bytes</summary>
    public void Send(byte cmd, ReadOnlySpan<byte> p)
    {
        param.Clear();
        foreach (var b in p) param.Enqueue(b);
        Command(cmd);
    }

    /// <summary>native libcd: set the read position without a Setloc command (libcd's implicit Setloc)</summary>
    public void Seek(int lba) { setLoc = lba; setLocPending = true; }

    public uint Read(uint reg)
    {
        uint v = Read0(reg);
        if (Trace && traceN++ < 400) Psx.Log($"cd r{reg}.{index} -> {v:X2}");
        return v;
    }
    int traceN;

    uint Read0(uint reg)
    {
        switch (reg)
        {
            case 0:
                return (uint)(index | (param.Count == 0 ? 8 : 0) | (param.Count < 16 ? 0x10 : 0)
                    | (resp.Count > 0 ? 0x20 : 0) | (dataPos < dataLen ? 0x40 : 0) | (busy ? 0x80 : 0));
            case 1: return resp.Count > 0 ? resp.Dequeue() : 0u;
            case 2: return dataPos < dataLen ? data[dataPos++] : 0u;
            default: return (index & 1) == 0 ? (uint)(ie | 0xE0) : (uint)(iflag | 0xE0);
        }
    }

    public void Write(uint reg, byte v)
    {
        if (Trace && traceN++ < 400) Psx.Log($"cd w{reg}.{index} <- {v:X2}");
        switch (reg, index)
        {
            case (0, _): index = v & 3; break;
            case (1, 0): Command(v); break;
            case (2, 0): if (param.Count < 16) param.Enqueue(v); break;
            case (2, 1): ie = (byte)(v & 0x1F); Raise(); break;
            case (3, 0):
                if ((v & 0x80) != 0) { if (dataPos >= dataLen) { Array.Copy(sectorBuf, data, sectorLen); dataLen = sectorLen; dataPos = 0; } }
                else { dataLen = dataPos = 0; }
                break;
            case (3, 1):
                iflag &= (byte)~(v & 0x1F);
                if ((v & 0x40) != 0) param.Clear();
                if (iflag == 0 && pending.Count > 0) m.After(800, Deliver);
                break;
            default: break;   // audio volume / sound map: CD audio goes straight to the SPU at full volume
        }
    }

    void Raise() { if ((iflag & ie) != 0) m.Irq(2); }

    /// <summary>queue an interrupt with its response; delivered once the game has acknowledged the previous one</summary>
    void Int(byte type, params byte[] r)
    {
        if (Sink != null) { busy = false; Sink(type, r, type == 1 ? sectorBuf.AsSpan(0, sectorLen).ToArray() : null); return; }
        pending.Enqueue((type, r));
        if (iflag == 0) Deliver();
    }

    void Deliver()
    {
        if (iflag != 0 || pending.Count == 0) return;
        var (t, r) = pending.Dequeue();
        resp.Clear();
        foreach (var b in r) resp.Enqueue(b);
        iflag = t;
        if (Trace) Psx.Log($"cd int{t} [{string.Join(' ', r.Select(b => b.ToString("X2")))}] ie={ie:X2}");
        busy = false;
        Raise();
    }

    void Command(byte c)
    {
        var p = param.ToArray();
        param.Clear();
        if (Trace) Psx.Log($"cd cmd {c:X2} [{string.Join(' ', p.Select(b => b.ToString("X2")))}] at {m.Cycles}");
        busy = true;
        const int Ack = 20000;
        switch (c)
        {
            case 0x01: Later(Ack, 3, Stat()); break;                           // Getstat
            case 0x02:                                                            // Setloc
                setLoc = (Bcd(p[0]) * 60 + Bcd(p[1])) * 75 + Bcd(p[2]) - 150;
                setLocPending = true;
                Later(Ack, 3, Stat());
                break;
            case 0x03: Later(Ack, 3, Stat()); break;                           // Play (no CD-DA on this disc)
            case 0x06: case 0x1B:                                                 // ReadN / ReadS
                StopRead();
                if (setLocPending) { readLoc = setLoc; setLocPending = false; }
                stat = 0x22;
                Later(Ack, 3, Stat());
                StartRead();
                break;
            case 0x07: stat = 0x02; Later(Ack, 3, Stat()); Later(Ack * 4, 2, Stat()); break;   // MotorOn
            case 0x08: StopRead(); stat = 0; Later(Ack, 3, Stat()); Later(Ack * 4, 2, Stat()); break;   // Stop
            case 0x09:                                                            // Pause
                Later(Ack, 3, Stat());
                StopRead(); stat = 0x02;
                Later(Ack * 4, 2, Stat());
                break;
            case 0x0A:                                                            // Init
                StopRead(); mode = 0; stat = 0x02;
                Later(Ack, 3, Stat()); Later(Ack * 4, 2, Stat());
                break;
            case 0x0B: muted = true; Later(Ack, 3, Stat()); break;
            case 0x0C: muted = false; Later(Ack, 3, Stat()); break;
            case 0x0D: filterFile = p[0]; filterChan = p[1]; Later(Ack, 3, Stat()); break;
            case 0x0E: mode = p[0]; Later(Ack, 3, Stat()); break;
            case 0x0F: Later(Ack, 3, Stat(), mode, 0, filterFile, filterChan); break;
            case 0x10: Later(Ack, 3, lastHeaderMin, lastHeaderSec, lastHeaderFrame, lastHeaderMode, lastSub0, lastSub1, lastSub2, lastSub3); break;
            case 0x11:                                                            // GetlocP
            {
                int l = readLoc + 150;
                Later(Ack, 3, 1, 1, ToBcd(l / 75 / 60), ToBcd(l / 75 % 60), ToBcd(l % 75), ToBcd(l / 75 / 60), ToBcd(l / 75 % 60), ToBcd(l % 75));
                break;
            }
            case 0x13: Later(Ack, 3, Stat(), 1, 1); break;                     // GetTN
            case 0x14:                                                            // GetTD
            {
                int end = (int)disc.Sectors + 150;
                if (p.Length > 0 && p[0] == 1) Later(Ack, 3, Stat(), 0, 2);
                else Later(Ack, 3, Stat(), ToBcd(end / 75 / 60), ToBcd(end / 75 % 60));
                break;
            }
            case 0x15: case 0x16:                                                 // SeekL / SeekP
                StopRead();
                if (setLocPending) { readLoc = setLoc; setLocPending = false; }
                stat = 0x42;
                Later(Ack, 3, Stat());
                m.After(Ack * 6, () => { stat = 0x02; Int(2, Stat()); });
                break;
            case 0x19:                                                            // Test
                if (p.Length > 0 && p[0] == 0x20) Later(Ack, 3, 0x97, 0x01, 0x10, 0xC2);
                else Later(Ack, 3, Stat());
                break;
            case 0x1A:                                                            // GetID: licensed Japanese disc
                Later(Ack, 3, Stat());
                Later(Ack * 3, 2, 0x02, 0x00, 0x20, 0x00, (byte)'S', (byte)'C', (byte)'E', (byte)'I');
                break;
            case 0x1E: Later(Ack, 3, Stat()); Later(Ack * 20, 2, Stat()); break;   // ReadTOC
            default:
                Psx.Log($"cd: unknown command {c:X2}");
                Later(Ack, 5, 0x11, 0x40);
                break;
        }
    }

    byte Stat() => stat;

    void Later(int delay, byte type, params byte[] r) => m.After(delay, () => Int(type, r));

    static int Bcd(byte b) => (b >> 4) * 10 + (b & 15);
    static byte ToBcd(int v) => (byte)(v / 10 << 4 | v % 10);

    void StopRead()
    {
        reading = false;
        readGen++;
        stat &= 0x1F;
    }

    void StartRead()
    {
        reading = true;
        long gen = ++readGen;
        int speed = (mode & 0x80) != 0 ? SectorCycles / 2 : SectorCycles;
        void Next()
        {
            if (!reading || gen != readGen) return;
            ReadSector();
            m.After(speed, Next);
        }
        m.After(speed * 3, Next);   // seek time
    }

    void ReadSector()
    {
        int lba = readLoc++;
        LastLba = lba;
        if (lba < 0 || lba >= disc.Sectors) { Int(5, 0x11, 0x80); StopRead(); return; }
        disc.ReadRaw(lba, raw);
        lastHeaderMin = raw[12]; lastHeaderSec = raw[13]; lastHeaderFrame = raw[14]; lastHeaderMode = raw[15];
        lastSub0 = raw[16]; lastSub1 = raw[17]; lastSub2 = raw[18]; lastSub3 = raw[19];
        byte file = raw[16], chan = raw[17], sub = raw[18];
        bool audio = (sub & 0x04) != 0 && (sub & 0x40) != 0 || (sub & 0x24) == 0x24;
        if ((mode & 0x40) != 0 && (sub & 0x04) != 0)
        {
            // XA-ADPCM: played, never delivered to the CPU
            if ((mode & 0x08) == 0 || (file == filterFile && chan == filterChan))
                if (!muted) Xa.Decode(raw, m.Spu);
            if ((sub & 0x80) != 0 && (mode & 0x08) != 0) { }   // end of record: the game stops the read itself
            return;
        }
        if ((mode & 0x08) != 0 && audio) return;
        if ((mode & 0x20) != 0) { Array.Copy(raw, 12, sectorBuf, 0, 0x924); sectorLen = 0x924; }
        else { Array.Copy(raw, 24, sectorBuf, 0, 0x800); sectorLen = 0x800; }
        if (Sink == null && pending.Count > 4) return;   // the game is not keeping up: drop, like the real buffer would
        Int(1, Stat());
    }

    public uint DmaWord()
    {
        uint v = 0;
        for (int i = 0; i < 4; i++) v |= (uint)(dataPos < dataLen ? data[dataPos++] : 0) << (i * 8);
        return v;
    }
}
