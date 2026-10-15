using System.Runtime.CompilerServices;

namespace Ps1tl.Port;

/// <summary>
/// Calls between recompiled code, the interpreter and the HLE BIOS. Any guest address can be called: a recompiled
/// function runs natively, a BIOS vector runs in C#, anything else (code the recompiler did not find, or code
/// loaded at run time) is interpreted until it returns.
/// </summary>
public sealed unsafe partial class Psx
{
    /// <summary>recompiled functions by guest entry address (filled by the generated code)</summary>
    public readonly Dictionary<uint, Action<Psx>> Funcs = new();
    public long Interpreted;
    /// <summary>last interpreted pc (diagnostics)</summary>
    public uint Pc;
    public readonly Dictionary<uint, long> InterpHits = new();
    /// <summary>--prof: interpreted instructions per pc (diagnostics)</summary>
    public Dictionary<uint, long>? Prof;
    /// <summary>--trap: log the registers whenever the interpreter executes this pc (diagnostics)</summary>
    public uint TrapPc = 1;

    /// <summary>call the function at addr; returns when it returns (R[31] is the caller's return address)</summary>
    public void Call(uint addr)
    {
        if (Cycles >= NextEvent) Tick();
        if (!Funcs.ContainsKey(addr)) Overlays.Touch(addr);
        if (Funcs.TryGetValue(addr, out var f)) { f(this); return; }
        if (Bios.Hle(addr)) return;
        Interpret(addr, R[31], R[29]);
    }

    /// <summary>a jump (not a call) out of recompiled code: continue at addr until the current function returns</summary>
    public void Jump(uint addr, uint ra0, uint sp0)
    {
        if (!Funcs.ContainsKey(addr)) Overlays.Touch(addr);
        if (Funcs.TryGetValue(addr, out var f)) { f(this); return; }
        if (Bios.Hle(addr)) return;
        Interpret(addr, ra0, sp0);
    }

    /// <summary>
    /// Run guest code from pc until it jumps to stopRa with the stack pointer back at stopSp (the function returned).
    /// </summary>
    public void Interpret(uint pc, uint stopRa, uint stopSp)
    {
        // a longjmp to a setjmp made in this frame lands here (LibC)
        int frame = Sdk.C.Enter();
        try
        {
            while (true)
                try { Run(pc, stopRa, stopSp); return; }
                catch (LibC.LongJmp j) when (j.Frame == frame) { pc = j.Pc; }
        }
        finally { Sdk.C.Leave(); }
    }

    void Run(uint pc, uint stopRa, uint stopSp)
    {
        uint next = pc + 4;
        bool jumped = true;       // pc was reached by a jump: check for natives / HLE / the return
        bool inDelay = false;
        CountEntry(pc);
        while (true)
        {
            if (jumped && !inDelay)
            {
                jumped = false;
                if (pc == stopRa && R[29] == stopSp) return;
                if (Cycles >= NextEvent) Tick();
                if (!Funcs.ContainsKey(pc)) Overlays.Touch(pc);
                if (Funcs.TryGetValue(pc, out var f) || Bios.IsVector(pc))
                {
                    // a call (or tail call) into native code: it returns to R[31]
                    if (f != null) f(this); else Bios.Hle(pc);
                    pc = R[31]; next = pc + 4; jumped = true;
                    continue;
                }
            }
            uint ins = Read32(pc);
            uint cur = pc;
            Pc = cur;
            if (cur == TrapPc) Log($"trap {cur:X8} f{Frame} " + string.Join(' ', Enumerable.Range(1, 31).Select(i => $"r{i}={R[i]:X8}")));
            if (Prof != null) System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(Prof, cur, out _)++;
            pc = next; next = pc + 4;
            bool wasDelay = inDelay;
            inDelay = false;
            Cycles += 2;
            Interpreted++;
            uint target = Exec(ins, cur, out bool branch);
            if (branch)
            {
                // the delay slot runs next, then the jump lands
                inDelay = true;
                next = target;
                jumped = true;
                if (wasDelay) Log($"branch in delay slot at {cur:X8}");
            }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    void CountEntry(uint pc)
    {
        ref long v = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(InterpHits, pc, out _);
        v++;
    }

    static int Rs(uint i) => (int)(i >> 21) & 31;
    static int Rt(uint i) => (int)(i >> 16) & 31;
    static int Rd(uint i) => (int)(i >> 11) & 31;
    static uint Imm(uint i) => (uint)(short)i;

    /// <summary>execute one instruction; for a taken jump/branch returns the target with branch = true</summary>
    uint Exec(uint i, uint pc, out bool branch)
    {
        branch = false;
        var r = R;
        int rs = Rs(i), rt = Rt(i), rd = Rd(i);
        uint imm = Imm(i);
        switch (i >> 26)
        {
            case 0:
                switch (i & 63)
                {
                    case 0x00: if (rd != 0) r[rd] = r[rt] << (int)(i >> 6 & 31); break;
                    case 0x02: if (rd != 0) r[rd] = r[rt] >> (int)(i >> 6 & 31); break;
                    case 0x03: if (rd != 0) r[rd] = (uint)((int)r[rt] >> (int)(i >> 6 & 31)); break;
                    case 0x04: if (rd != 0) r[rd] = r[rt] << (int)(r[rs] & 31); break;
                    case 0x06: if (rd != 0) r[rd] = r[rt] >> (int)(r[rs] & 31); break;
                    case 0x07: if (rd != 0) r[rd] = (uint)((int)r[rt] >> (int)(r[rs] & 31)); break;
                    case 0x08: branch = true; return r[rs];
                    case 0x09: { uint t = r[rs]; if (rd != 0) r[rd] = pc + 8; branch = true; return t; }
                    case 0x0C: Bios.Syscall(pc); break;
                    case 0x0D: Log($"break at {pc:X8} code {i >> 6 & 0xFFFFF:X}"); break;
                    case 0x10: if (rd != 0) r[rd] = Hi; break;
                    case 0x11: Hi = r[rs]; break;
                    case 0x12: if (rd != 0) r[rd] = Lo; break;
                    case 0x13: Lo = r[rs]; break;
                    case 0x18: { long p = (long)(int)r[rs] * (int)r[rt]; Lo = (uint)p; Hi = (uint)(p >> 32); break; }
                    case 0x19: { ulong p = (ulong)r[rs] * r[rt]; Lo = (uint)p; Hi = (uint)(p >> 32); break; }
                    case 0x1A: Div(r[rs], r[rt]); break;
                    case 0x1B: Divu(r[rs], r[rt]); break;
                    case 0x20: case 0x21: if (rd != 0) r[rd] = r[rs] + r[rt]; break;
                    case 0x22: case 0x23: if (rd != 0) r[rd] = r[rs] - r[rt]; break;
                    case 0x24: if (rd != 0) r[rd] = r[rs] & r[rt]; break;
                    case 0x25: if (rd != 0) r[rd] = r[rs] | r[rt]; break;
                    case 0x26: if (rd != 0) r[rd] = r[rs] ^ r[rt]; break;
                    case 0x27: if (rd != 0) r[rd] = ~(r[rs] | r[rt]); break;
                    case 0x2A: if (rd != 0) r[rd] = (int)r[rs] < (int)r[rt] ? 1u : 0; break;
                    case 0x2B: if (rd != 0) r[rd] = r[rs] < r[rt] ? 1u : 0; break;
                    default: Log($"bad special {i:X8} at {pc:X8}"); break;
                }
                break;
            case 1:
            {
                bool link = (rt & 0x1E) == 0x10;
                bool take = (rt & 1) != 0 ? (int)r[rs] >= 0 : (int)r[rs] < 0;
                if (link) r[31] = pc + 8;
                if (take) { branch = true; return pc + 4 + (imm << 2); }
                break;
            }
            case 2: branch = true; return (pc & 0xF0000000) | (i & 0x3FFFFFF) << 2;
            case 3: r[31] = pc + 8; branch = true; return (pc & 0xF0000000) | (i & 0x3FFFFFF) << 2;
            case 4: if (r[rs] == r[rt]) { branch = true; return pc + 4 + (imm << 2); } break;
            case 5: if (r[rs] != r[rt]) { branch = true; return pc + 4 + (imm << 2); } break;
            case 6: if ((int)r[rs] <= 0) { branch = true; return pc + 4 + (imm << 2); } break;
            case 7: if ((int)r[rs] > 0) { branch = true; return pc + 4 + (imm << 2); } break;
            case 8: case 9: if (rt != 0) r[rt] = r[rs] + imm; break;
            case 10: if (rt != 0) r[rt] = (int)r[rs] < (int)imm ? 1u : 0; break;
            case 11: if (rt != 0) r[rt] = r[rs] < imm ? 1u : 0; break;
            case 12: if (rt != 0) r[rt] = r[rs] & (i & 0xFFFF); break;
            case 13: if (rt != 0) r[rt] = r[rs] | (i & 0xFFFF); break;
            case 14: if (rt != 0) r[rt] = r[rs] ^ (i & 0xFFFF); break;
            case 15: if (rt != 0) r[rt] = i << 16; break;
            case 16: Cop0(i, pc); break;
            case 18: Cop2(i); break;
            case 32: { uint v = (uint)(sbyte)Read8(r[rs] + imm); if (rt != 0) r[rt] = v; break; }
            case 33: { uint v = (uint)(short)Read16(r[rs] + imm); if (rt != 0) r[rt] = v; break; }
            case 34: { uint v = Lwl(r[rs] + imm, r[rt]); if (rt != 0) r[rt] = v; break; }
            case 35: { uint v = Read32(r[rs] + imm); if (rt != 0) r[rt] = v; break; }
            case 36: { uint v = Read8(r[rs] + imm); if (rt != 0) r[rt] = v; break; }
            case 37: { uint v = Read16(r[rs] + imm); if (rt != 0) r[rt] = v; break; }
            case 38: { uint v = Lwr(r[rs] + imm, r[rt]); if (rt != 0) r[rt] = v; break; }
            case 40: Write8(r[rs] + imm, r[rt]); break;
            case 41: Write16(r[rs] + imm, r[rt]); break;
            case 42: Swl(r[rs] + imm, r[rt]); break;
            case 43: Write32(r[rs] + imm, r[rt]); break;
            case 46: Swr(r[rs] + imm, r[rt]); break;
            case 50: Gte.Write(rt, Read32(r[rs] + imm)); break;
            case 58: Write32(r[rs] + imm, Gte.Read(rt)); break;
            default: Log($"bad opcode {i:X8} at {pc:X8}"); break;
        }
        return 0;
    }

    public void Cop0(uint i, uint pc)
    {
        int rt = Rt(i), rd = Rd(i);
        switch (Rs(i))
        {
            case 0: if (rt != 0) R[rt] = rd switch { 12 => Sr, 13 => Cause, 14 => Epc, 15 => 2, _ => 0 }; break;
            case 4:
                if (rd == 12) { Sr = R[rt]; if (IrqPending) NextEvent = Cycles; }
                else if (rd == 13) Cause = (Cause & ~0x300u) | (R[rt] & 0x300);
                break;
            case 16: if ((i & 63) == 0x10) Sr = (Sr & ~0xFu) | (Sr >> 2 & 0xF); break;
        }
    }

    public void Cop2(uint i)
    {
        int rt = Rt(i), rd = Rd(i);
        if ((i & 0x02000000) != 0) { Gte.Command(i); return; }
        switch (Rs(i))
        {
            case 0: if (rt != 0) R[rt] = Gte.Read(rd); break;
            case 2: if (rt != 0) R[rt] = Gte.ReadCtrl(rd); break;
            case 4: Gte.Write(rd, R[rt]); break;
            case 6: Gte.WriteCtrl(rd, R[rt]); break;
        }
    }

    public void Div(uint a, uint b)
    {
        int n = (int)a, d = (int)b;
        if (d == 0) { Hi = a; Lo = n >= 0 ? 0xFFFFFFFF : 1; }
        else if (n == int.MinValue && d == -1) { Hi = 0; Lo = a; }
        else { Lo = (uint)(n / d); Hi = (uint)(n % d); }
    }

    public void Divu(uint a, uint b)
    {
        if (b == 0) { Hi = a; Lo = 0xFFFFFFFF; }
        else { Lo = a / b; Hi = a % b; }
    }

    public uint Lwl(uint a, uint old)
    {
        uint w = Read32(a & ~3u);
        return (a & 3) switch
        {
            0 => (old & 0x00FFFFFF) | w << 24,
            1 => (old & 0x0000FFFF) | w << 16,
            2 => (old & 0x000000FF) | w << 8,
            _ => w,
        };
    }
    public uint Lwr(uint a, uint old)
    {
        uint w = Read32(a & ~3u);
        return (a & 3) switch
        {
            0 => w,
            1 => (old & 0xFF000000) | w >> 8,
            2 => (old & 0xFFFF0000) | w >> 16,
            _ => (old & 0xFFFFFF00) | w >> 24,
        };
    }
    public void Swl(uint a, uint v)
    {
        uint al = a & ~3u, w = Read32(al);
        Write32(al, (a & 3) switch
        {
            0 => (w & 0xFFFFFF00) | v >> 24,
            1 => (w & 0xFFFF0000) | v >> 16,
            2 => (w & 0xFF000000) | v >> 8,
            _ => v,
        });
    }
    public void Swr(uint a, uint v)
    {
        uint al = a & ~3u, w = Read32(al);
        Write32(al, (a & 3) switch
        {
            0 => v,
            1 => (w & 0x000000FF) | v << 8,
            2 => (w & 0x0000FFFF) | v << 16,
            _ => (w & 0x00FFFFFF) | v << 24,
        });
    }
}
