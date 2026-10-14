using System.Runtime.InteropServices;

namespace Ps1tl.Port;

/// <summary>
/// The sound chip: 512 KB of sound RAM, 24 ADPCM voices with ADSR envelopes, CD (XA) audio input, mixed to
/// 44.1 kHz stereo. No reverb. Output goes to <see cref="AudioOut"/>.
/// </summary>
public sealed class Spu(Psx m)
{
    public readonly byte[] Ram = new byte[0x80000];
    readonly ushort[] regs = new ushort[0x200];
    readonly Voice[] voices = Enumerable.Range(0, 24).Select(_ => new Voice()).ToArray();
    uint xferAddr;
    ushort cnt, stat;
    uint endx;
    uint irqAddr;
    long lastCycles;
    public AudioOut? Out;
    public delegate void SampleSink(ReadOnlySpan<short> s);
    public SampleSink? Sink;

    // CD audio, already at 44.1 kHz
    readonly Queue<(short L, short R)> cd = new();
    public void PushCd(short l, short r) { CdSamples++; if (cd.Count < 44100) cd.Enqueue((l, r)); }

    enum Phase { Off, Attack, Decay, Sustain, Release }

    sealed class Voice
    {
        public uint Addr, Start, Repeat;
        public uint Counter;
        public readonly short[] Buf = new short[28];
        public short Old, Older;
        public int Prev3, Prev2, Prev1;
        public Phase Ph;
        public int Level, EnvCounter;
        public ushort Pitch, AdsrLo, AdsrHi, VolL, VolR;
        public bool Decoded;
        public byte Flags;
    }

    public uint Read(uint p)
    {
        int r = (int)(p - 0x1F801C00) >> 1;
        if (r < 0xC0)
        {
            var v = voices[r >> 3];
            switch (r & 7)
            {
                case 6: return (uint)v.Level;
                case 7: return v.Repeat >> 3;
            }
        }
        switch (p)
        {
            case 0x1F801D9C: return endx & 0xFFFF;
            case 0x1F801D9E: return endx >> 16;
            case 0x1F801DA6: return xferAddr >> 3;
            case 0x1F801DAA: return cnt;
            case 0x1F801DAE: return stat;
            case 0x1F801DB8: case 0x1F801DBA: return 0;
        }
        return regs[r & 0x1FF];
    }

    public void Write(uint p, ushort v)
    {
        int r = (int)(p - 0x1F801C00) >> 1;
        regs[r & 0x1FF] = v;
        if (r < 0xC0)
        {
            var vc = voices[r >> 3];
            switch (r & 7)
            {
                case 0: vc.VolL = v; break;
                case 1: vc.VolR = v; break;
                case 2: vc.Pitch = v; break;
                case 3: vc.Start = (uint)v << 3; break;
                case 4: vc.AdsrLo = v; break;
                case 5: vc.AdsrHi = v; break;
                case 6: vc.Level = (short)v; break;
                case 7: vc.Repeat = (uint)v << 3; break;
            }
            return;
        }
        switch (p)
        {
            case 0x1F801D88: KeyOn(v); break;
            case 0x1F801D8A: KeyOn((uint)v << 16); break;
            case 0x1F801D8C: KeyOff(v); break;
            case 0x1F801D8E: KeyOff((uint)v << 16); break;
            case 0x1F801DA4: irqAddr = (uint)v << 3; break;
            case 0x1F801DA6: xferAddr = (uint)v << 3; break;
            case 0x1F801DA8: Ram[xferAddr & 0x7FFFF] = (byte)v; Ram[(xferAddr + 1) & 0x7FFFF] = (byte)(v >> 8); xferAddr = (xferAddr + 2) & 0x7FFFF; break;
            case 0x1F801DAA:
                cnt = v;
                stat = (ushort)((stat & ~0x3F) | (v & 0x3F));
                if ((v & 0x40) == 0) stat &= unchecked((ushort)~0x40);
                break;
        }
    }

    public void DmaWrite(uint w)
    {
        for (int i = 0; i < 4; i++) { Ram[xferAddr] = (byte)(w >> (i * 8)); CheckIrq(xferAddr); xferAddr = (xferAddr + 1) & 0x7FFFF; }
    }

    public uint DmaRead()
    {
        uint w = 0;
        for (int i = 0; i < 4; i++) { w |= (uint)Ram[xferAddr] << (i * 8); xferAddr = (xferAddr + 1) & 0x7FFFF; }
        return w;
    }

    void CheckIrq(uint a)
    {
        if ((cnt & 0x40) != 0 && (a & ~7u) == irqAddr && (stat & 0x40) == 0) { stat |= 0x40; m.Irq(9); }
    }

    public long KeyOns, CdSamples;
    public string Debug => $"spu cnt={cnt:X4} main={regs[0xC0]:X4}/{regs[0xC1]:X4} cdvol={regs[0xD8]:X4} keyons={KeyOns} cd={CdSamples} active={voices.Count(v => v.Ph != Phase.Off)}";

    void KeyOn(uint bits)
    {
        KeyOns += System.Numerics.BitOperations.PopCount(bits);
        for (int i = 0; i < 24; i++)
            if ((bits & (1u << i)) != 0)
            {
                var v = voices[i];
                v.Addr = v.Start; v.Counter = 0; v.Decoded = false;
                v.Old = v.Older = 0; v.Prev1 = v.Prev2 = v.Prev3 = 0;
                v.Ph = Phase.Attack; v.Level = 0; v.EnvCounter = 0;
                endx &= ~(1u << i);
            }
    }

    void KeyOff(uint bits)
    {
        for (int i = 0; i < 24; i++)
            if ((bits & (1u << i)) != 0 && voices[i].Ph != Phase.Off) { voices[i].Ph = Phase.Release; voices[i].EnvCounter = 0; }
    }

    static readonly int[] Pos = [0, 60, 115, 98, 122], Neg = [0, 0, -52, -55, -60];

    void DecodeBlock(Voice v, int idx)
    {
        uint a = v.Addr & 0x7FFF0;
        CheckIrq(a);
        byte hdr = Ram[a];
        v.Flags = Ram[a + 1];
        int shift = hdr & 15, f = Math.Min(4, hdr >> 4 & 7);
        if (shift > 12) shift = 9;
        if ((v.Flags & 4) != 0) v.Repeat = a;
        for (int i = 0; i < 28; i++)
        {
            int b = Ram[a + 2 + i / 2];
            int nib = (i & 1) == 0 ? b & 15 : b >> 4;
            int s = (short)(nib << 12) >> shift;
            s += (v.Old * Pos[f] + v.Older * Neg[f] + 32) >> 6;
            s = Math.Clamp(s, -32768, 32767);
            v.Older = v.Old; v.Old = (short)s;
            v.Buf[i] = (short)s;
        }
        v.Decoded = true;
        _ = idx;
    }

    static void Env(Voice v, int shift, int step, bool exp, bool dec)
    {
        int cyc = 1 << Math.Max(0, shift - 11);
        int st = step << Math.Max(0, 11 - shift);
        if (exp && !dec && v.Level > 0x6000) cyc *= 4;
        if (exp && dec) st = st * v.Level >> 15;
        if (++v.EnvCounter < cyc) return;
        v.EnvCounter = 0;
        v.Level = Math.Clamp(v.Level + st, 0, 0x7FFF);
    }

    static void Envelope(Voice v)
    {
        int lo = v.AdsrLo, hi = v.AdsrHi;
        switch (v.Ph)
        {
            case Phase.Attack:
                Env(v, lo >> 10 & 31, 7 - (lo >> 8 & 3), (lo & 0x8000) != 0, false);
                if (v.Level >= 0x7FFF) { v.Ph = Phase.Decay; v.EnvCounter = 0; }
                break;
            case Phase.Decay:
                Env(v, (lo >> 4 & 15) << 2, -8, true, true);
                if (v.Level <= ((lo & 15) + 1) * 0x800) { v.Ph = Phase.Sustain; v.EnvCounter = 0; }
                break;
            case Phase.Sustain:
            {
                bool dec = (hi & 0x4000) != 0;
                int step = dec ? -8 + (hi >> 6 & 3) : 7 - (hi >> 6 & 3);
                Env(v, hi >> 8 & 31, step, (hi & 0x8000) != 0, dec);
                break;
            }
            case Phase.Release:
                Env(v, (hi & 31) << 2, -8, (hi & 0x20) != 0, true);
                if (v.Level <= 0) v.Ph = Phase.Off;
                break;
        }
    }

    static int Vol(ushort v) => (v & 0x8000) != 0 ? 0x7FFF : (short)(v << 1);

    (int L, int R) Sample()
    {
        int l = 0, r = 0;
        for (int i = 0; i < 24; i++)
        {
            var v = voices[i];
            if (v.Ph == Phase.Off) continue;
            if (!v.Decoded) DecodeBlock(v, i);
            int pos = (int)(v.Counter >> 12);
            int s0 = v.Buf[Math.Min(pos, 27)];
            int frac = (int)(v.Counter & 0xFFF);
            int s = v.Prev1 + ((s0 - v.Prev1) * frac >> 12);
            int pitch = Math.Min((int)v.Pitch, 0x4000);
            v.Counter += (uint)pitch;
            while ((v.Counter >> 12) >= 28)
            {
                v.Prev1 = v.Buf[27];
                v.Counter -= 28 << 12;
                if ((v.Flags & 1) != 0)
                {
                    endx |= 1u << i;
                    v.Addr = v.Repeat;
                    if ((v.Flags & 2) == 0) { v.Ph = Phase.Off; v.Level = 0; }
                }
                else v.Addr = (v.Addr + 16) & 0x7FFFF;
                DecodeBlock(v, i);
            }
            if ((v.Counter >> 12) > 0) v.Prev1 = v.Buf[(v.Counter >> 12) - 1];
            Envelope(v);
            s = s * v.Level >> 15;
            l += s * Vol(v.VolL) >> 15;
            r += s * Vol(v.VolR) >> 15;
        }
        int mainL = Vol(regs[0xC0]), mainR = Vol(regs[0xC1]);
        l = l * mainL >> 15; r = r * mainR >> 15;
        if (cd.Count > 0)
        {
            var (cl, cr) = cd.Dequeue();
            int cvl = regs[0xD8] == 0 ? 0x7FFF : (short)regs[0xD8], cvr = regs[0xD9] == 0 ? 0x7FFF : (short)regs[0xD9];
            if ((cnt & 1) != 0) { l += cl * cvl >> 15; r += cr * cvr >> 15; }
        }
        if ((cnt & 0x8000) == 0 || (cnt & 0x4000) == 0) return (0, 0);
        return (Math.Clamp(l, -32768, 32767), Math.Clamp(r, -32768, 32767));
    }

    readonly short[] mix = new short[4096];

    /// <summary>generate the samples for the cycles elapsed since the last call (called each vblank)</summary>
    public void Frame()
    {
        long n = (m.Cycles - lastCycles) / 768;
        lastCycles += n * 768;
        n = Math.Min(n, mix.Length / 2);
        for (int i = 0; i < n; i++)
        {
            var (l, r) = Sample();
            mix[i * 2] = (short)l; mix[i * 2 + 1] = (short)r;
        }
        Out?.Write(mix.AsSpan(0, (int)n * 2));
        Sink?.Invoke(mix.AsSpan(0, (int)n * 2));
    }
}

/// <summary>XA-ADPCM sector decoder (CD audio streams), resampled to 44.1 kHz into the SPU's CD input</summary>
public sealed class Xa
{
    static readonly int[] K0 = [0, 60, 115, 98], K1 = [0, 0, -52, -55];
    int oldL, olderL, oldR, olderR;
    double phase;
    short lastL, lastR;
    readonly List<(short, short)> pcm = new(4032);

    public void Decode(byte[] raw, Spu spu)
    {
        byte coding = raw[19];
        bool stereo = (coding & 1) != 0, half = (coding & 4) != 0, eight = (coding & 0x10) != 0;
        int rate = half ? 18900 : 37800;
        pcm.Clear();
        var monoL = new List<short>(4032);
        var right = new List<short>(2016);
        for (int g = 0; g < 18; g++)
        {
            int go = 24 + g * 128;
            int units = eight ? 4 : 8;
            for (int u = 0; u < units; u++)
            {
                byte h = raw[go + 4 + u];
                int shift = h & 15, f = (h >> 4) & 3;
                if (shift > 12) shift = 9;
                bool isRight = stereo && (u & 1) != 0;
                ref int old = ref isRight ? ref oldR : ref oldL;
                ref int older = ref isRight ? ref olderR : ref olderL;
                for (int j = 0; j < 28; j++)
                {
                    int b = raw[go + 16 + j * 4 + (eight ? u : u / 2)];
                    int s = eight ? (short)(b << 8) >> shift : (short)((((u & 1) != 0 ? b >> 4 : b & 15)) << 12) >> shift;
                    s += (old * K0[f] + older * K1[f] + 32) >> 6;
                    s = Math.Clamp(s, -32768, 32767);
                    older = old; old = s;
                    (isRight ? right : monoL).Add((short)s);
                }
            }
        }
        if (stereo) for (int i = 0; i < Math.Min(monoL.Count, right.Count); i++) pcm.Add((monoL[i], right[i]));
        else foreach (var s in monoL) pcm.Add((s, s));
        // resample to 44.1 kHz
        double step = rate / 44100.0;
        while (phase < pcm.Count)
        {
            int i = (int)phase;
            double fr = phase - i;
            var (al, ar) = i == 0 ? (lastL, lastR) : pcm[i - 1];
            var (bl, br) = pcm[i];
            spu.PushCd((short)(al + (bl - al) * fr), (short)(ar + (br - ar) * fr));
            phase += step;
        }
        phase -= pcm.Count;
        (lastL, lastR) = pcm[^1];
    }
}

/// <summary>44.1 kHz stereo output through winmm (Windows); silent elsewhere</summary>
public sealed class AudioOut : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    struct WaveFormat { public ushort Tag, Channels; public uint Rate, BytesPerSec; public ushort Align, Bits, Size; }
    [StructLayout(LayoutKind.Sequential)]
    struct WaveHdr { public IntPtr Data; public uint Length, Recorded; public IntPtr User; public uint Flags, Loops; public IntPtr Next, Reserved; }

    [DllImport("winmm.dll")] static extern int waveOutOpen(out IntPtr h, int dev, ref WaveFormat f, IntPtr cb, IntPtr inst, int flags);
    [DllImport("winmm.dll")] static extern int waveOutPrepareHeader(IntPtr h, IntPtr hdr, int size);
    [DllImport("winmm.dll")] static extern int waveOutUnprepareHeader(IntPtr h, IntPtr hdr, int size);
    [DllImport("winmm.dll")] static extern int waveOutWrite(IntPtr h, IntPtr hdr, int size);
    [DllImport("winmm.dll")] static extern int waveOutClose(IntPtr h);

    const int Bufs = 8, BufSamples = 1024;   // ~186 ms of queue
    readonly IntPtr h;
    readonly IntPtr[] hdrs = new IntPtr[Bufs];
    readonly IntPtr[] mem = new IntPtr[Bufs];
    readonly short[] acc = new short[BufSamples * 2];
    int accN, next;
    readonly bool ok;

    public AudioOut()
    {
        if (!OperatingSystem.IsWindows()) return;
        var f = new WaveFormat { Tag = 1, Channels = 2, Rate = 44100, Bits = 16, Align = 4, BytesPerSec = 44100 * 4 };
        if (waveOutOpen(out h, -1, ref f, 0, 0, 0) != 0) return;
        int hs = Marshal.SizeOf<WaveHdr>();
        for (int i = 0; i < Bufs; i++)
        {
            mem[i] = Marshal.AllocHGlobal(BufSamples * 4);
            hdrs[i] = Marshal.AllocHGlobal(hs);
            Marshal.StructureToPtr(new WaveHdr { Data = mem[i], Length = BufSamples * 4, Flags = 1 /* done */ }, hdrs[i], false);
        }
        ok = true;
    }

    public void Write(ReadOnlySpan<short> s)
    {
        if (!ok) return;
        int hs = Marshal.SizeOf<WaveHdr>();
        foreach (var x in s)
        {
            acc[accN++] = x;
            if (accN < acc.Length) continue;
            accN = 0;
            var hp = hdrs[next];
            var hdr = Marshal.PtrToStructure<WaveHdr>(hp);
            if ((hdr.Flags & 1) == 0) continue;   // queue full: drop this buffer
            waveOutUnprepareHeader(h, hp, hs);
            Marshal.Copy(acc, 0, mem[next], acc.Length);
            hdr.Flags = 0; hdr.Length = BufSamples * 4;
            Marshal.StructureToPtr(hdr, hp, false);
            waveOutPrepareHeader(h, hp, hs);
            waveOutWrite(h, hp, hs);
            next = (next + 1) % Bufs;
        }
    }

    public void Dispose() { if (ok) waveOutClose(h); }
}
