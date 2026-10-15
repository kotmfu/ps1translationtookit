using System.Text;

namespace Ps1tl.Port;

/// <summary>
/// Static recompiler: turns the game's MIPS code into C# methods, one per function entry, with gotos for the
/// control flow inside it. Entries are the EXE entry point, every jal target, function pointers found in the
/// data, and (profile-guided) addresses the interpreter had to run in earlier sessions. Anything not found here
/// still works through the interpreter.
/// </summary>
public static class Recomp
{
    const int MaxBody = 30000;

    public sealed class Image
    {
        public uint Base, End;
        public uint[] Words = [];
        public bool In(uint a) => a >= Base && a < End && (a & 3) == 0;
        public uint At(uint a) => Words[(a - Base) >> 2];
    }

    public static Image Load(byte[] exe)
    {
        uint b = BitConverter.ToUInt32(exe, 0x18), size = BitConverter.ToUInt32(exe, 0x1C);
        var img = new Image { Base = b, End = b + size, Words = new uint[size / 4] };
        Buffer.BlockCopy(exe, 0x800, img.Words, 0, (int)Math.Min(size, exe.Length - 0x800) & ~3);
        return img;
    }

    static int Op(uint i) => (int)(i >> 26);
    static int Rs(uint i) => (int)(i >> 21) & 31;
    static int Rt(uint i) => (int)(i >> 16) & 31;
    static int Rd(uint i) => (int)(i >> 11) & 31;
    static int Fn(uint i) => (int)(i & 63);

    static bool IsBranch(uint i) => Op(i) is 1 or >= 4 and <= 7;
    static bool IsJump(uint i) => Op(i) is 2 or 3 || (Op(i) == 0 && Fn(i) is 8 or 9);
    static bool IsCti(uint i) => IsBranch(i) || IsJump(i);
    static uint BranchTarget(uint i, uint pc) => pc + 4 + ((uint)(short)i << 2);
    static uint JumpTarget(uint i, uint pc) => (pc & 0xF0000000) | (i & 0x3FFFFFF) << 2;

    /// <summary>is this a plausible instruction (used to reject data when following pointers)</summary>
    static bool Valid(uint i)
    {
        switch (Op(i))
        {
            case 0: return Fn(i) is 0 or 2 or 3 or 4 or 6 or 7 or 8 or 9 or 12 or 13 or 16 or 17 or 18 or 19 or 24 or 25 or 26 or 27 or (>= 32 and <= 39) or 42 or 43;
            case 1: return Rt(i) is 0 or 1 or 16 or 17;
            case 16: case 18: return true;
            case >= 2 and <= 15: return true;
            case 32 or 33 or 34 or 35 or 36 or 37 or 38 or 40 or 41 or 42 or 43 or 46 or 50 or 58: return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ discovery

    sealed class Func
    {
        public uint Entry;
        public SortedSet<uint> Body = new();
        public HashSet<uint> Labels = new();
        public Dictionary<uint, uint[]> Tables = new();   // jr pc -> jump table targets
        public bool Truncated;
    }

    public static HashSet<uint> FindEntries(Image img, uint entry, IEnumerable<uint> extra)
    {
        var entries = new HashSet<uint> { entry };
        for (uint a = img.Base; a < img.End; a += 4)
        {
            uint i = img.At(a);
            if (Op(i) == 3 && img.In(JumpTarget(i, a)) && Valid(img.At(JumpTarget(i, a)))) entries.Add(JumpTarget(i, a));
        }
        // function pointers: words in the image pointing at a function prologue ("addiu sp, sp, -n")
        for (uint a = img.Base; a < img.End; a += 4)
        {
            uint w = img.At(a);
            if (!img.In(w)) continue;
            uint i = img.At(w);
            if ((i & 0xFFFF8000) == 0x27BD8000) entries.Add(w);
        }
        // addresses built in code ("lui r, hi; addiu r, r, lo") that land on a function start: the instruction
        // before is the delay slot after a "jr ra"
        for (uint a = img.Base; a + 4 < img.End; a += 4)
        {
            uint i = img.At(a);
            if (Op(i) != 15) continue;
            for (uint b = a + 4; b < Math.Min(img.End, a + 32); b += 4)
            {
                uint j = img.At(b);
                if (Op(j) == 9 && Rs(j) == Rt(i))
                {
                    uint t = ((i & 0xFFFF) << 16) + (uint)(short)j;
                    if (img.In(t) && t >= img.Base + 8 && img.At(t - 8) == 0x03E00008 && Valid(img.At(t))) entries.Add(t);
                    break;
                }
            }
        }
        foreach (var e in extra) if (img.In(e)) entries.Add(e);
        return entries;
    }

    static Func Discover(Image img, uint entry, HashSet<uint> entries)
    {
        var f = new Func { Entry = entry };
        f.Labels.Add(entry);
        var work = new Stack<uint>();
        work.Push(entry);
        while (work.Count > 0)
        {
            uint pc = work.Pop();
            while (true)
            {
                if (!img.In(pc) || f.Body.Contains(pc)) break;
                if (f.Body.Count > MaxBody) { f.Truncated = true; break; }
                uint i = img.At(pc);
                if (!Valid(i)) break;   // falls into data: the emitter hands over to the interpreter here
                f.Body.Add(pc);
                if (!IsCti(i)) { pc += 4; continue; }
                // the delay slot belongs to the branch
                if (img.In(pc + 4)) f.Body.Add(pc + 4);
                if (IsBranch(i))
                {
                    uint t = BranchTarget(i, pc);
                    f.Labels.Add(t); work.Push(t);
                    pc += 8;
                    f.Labels.Add(pc);
                    continue;
                }
                switch (Op(i))
                {
                    case 2:
                    {
                        uint t = JumpTarget(i, pc);
                        if (entries.Contains(t) && t != entry) break;   // tail call
                        f.Labels.Add(t); work.Push(t);
                        break;
                    }
                    case 3: pc += 8; f.Labels.Add(pc); continue;   // jal: execution resumes after the delay slot
                    default:
                        if (Fn(i) == 9) { pc += 8; f.Labels.Add(pc); continue; }   // jalr
                        if (Rs(i) != 31 && JumpTable(img, pc) is { } tab)
                        {
                            f.Tables[pc] = tab;
                            foreach (var t in tab) { f.Labels.Add(t); work.Push(t); }
                        }
                        break;
                }
                break;
            }
        }
        return f;
    }

    /// <summary>recognise "lui at, hi; addu at, at, idx; lw rX, lo(at); jr rX" and read the table</summary>
    static uint[]? JumpTable(Image img, uint jrPc)
    {
        uint jr = img.At(jrPc);
        int reg = Rs(jr);
        int baseReg = -1; short lo = 0; int k;
        for (k = 1; k <= 8; k++)
        {
            uint i = img.At(jrPc - (uint)k * 4);
            if (Op(i) == 35 && Rt(i) == reg) { baseReg = Rs(i); lo = (short)i; break; }
        }
        if (baseReg < 0) return null;
        uint hi = 0; bool found = false;
        int count = -1;
        for (int j = k + 1; j <= k + 12; j++)
        {
            uint i = img.At(jrPc - (uint)j * 4);
            if (!found && Op(i) == 15 && Rt(i) == baseReg) { hi = (i & 0xFFFF) << 16; found = true; }
            if (count < 0 && Op(i) == 11) count = (short)i;   // sltiu bound
        }
        if (!found) return null;
        uint table = hi + (uint)lo;
        var targets = new List<uint>();
        for (int n = 0; n < (count > 0 ? count : 512); n++)
        {
            uint a = table + (uint)n * 4;
            if (!img.In(a)) break;
            uint t = img.At(a);
            if (!img.In(t) || !Valid(img.At(t))) break;
            targets.Add(t);
        }
        return targets.Count > 0 ? targets.ToArray() : null;
    }

    // ------------------------------------------------------------------ emission

    static string R(int r) => r == 0 ? "0u" : $"r[{r}]";
    static string Hex(uint v) => $"0x{v:X8}u";

    /// <summary>C# for one non-control-transfer instruction (empty for nops)</summary>
    static string Simple(uint i, uint pc)
    {
        int rs = Rs(i), rt = Rt(i), rd = Rd(i), sh = (int)(i >> 6 & 31);
        string imm = Hex((uint)(short)i);
        string uimm = $"0x{i & 0xFFFF:X}u";
        string A() => $"{R(rs)} + {imm}";
        string Set(int d, string e) => d == 0 ? "" : $"r[{d}] = {e};";
        switch (Op(i))
        {
            case 0:
                switch (Fn(i))
                {
                    case 0: return i == 0 ? "" : Set(rd, $"{R(rt)} << {sh}");
                    case 2: return Set(rd, $"{R(rt)} >> {sh}");
                    case 3: return Set(rd, $"(uint)((int){R(rt)} >> {sh})");
                    case 4: return Set(rd, $"{R(rt)} << (int)({R(rs)} & 31)");
                    case 6: return Set(rd, $"{R(rt)} >> (int)({R(rs)} & 31)");
                    case 7: return Set(rd, $"(uint)((int){R(rt)} >> (int)({R(rs)} & 31))");
                    case 12: return $"m.Bios.Syscall({Hex(pc)});";
                    case 13: return $"Psx.Log(\"break at {pc:X8}\");";
                    case 16: return Set(rd, "m.Hi");
                    case 17: return $"m.Hi = {R(rs)};";
                    case 18: return Set(rd, "m.Lo");
                    case 19: return $"m.Lo = {R(rs)};";
                    case 24: return $"{{ long p = (long)(int){R(rs)} * (int){R(rt)}; m.Lo = (uint)p; m.Hi = (uint)(p >> 32); }}";
                    case 25: return $"{{ ulong p = (ulong){R(rs)} * {R(rt)}; m.Lo = (uint)p; m.Hi = (uint)(p >> 32); }}";
                    case 26: return $"m.Div({R(rs)}, {R(rt)});";
                    case 27: return $"m.Divu({R(rs)}, {R(rt)});";
                    case 32: case 33: return Set(rd, $"{R(rs)} + {R(rt)}");
                    case 34: case 35: return Set(rd, $"{R(rs)} - {R(rt)}");
                    case 36: return Set(rd, $"{R(rs)} & {R(rt)}");
                    case 37: return Set(rd, $"{R(rs)} | {R(rt)}");
                    case 38: return Set(rd, $"{R(rs)} ^ {R(rt)}");
                    case 39: return Set(rd, $"~({R(rs)} | {R(rt)})");
                    case 42: return Set(rd, $"(int){R(rs)} < (int){R(rt)} ? 1u : 0u");
                    case 43: return Set(rd, $"{R(rs)} < {R(rt)} ? 1u : 0u");
                }
                break;
            case 8: case 9: return Set(rt, $"{R(rs)} + {imm}");
            case 10: return Set(rt, $"(int){R(rs)} < {(short)i} ? 1u : 0u");
            case 11: return Set(rt, $"{R(rs)} < {imm} ? 1u : 0u");
            case 12: return Set(rt, $"{R(rs)} & {uimm}");
            case 13: return Set(rt, $"{R(rs)} | {uimm}");
            case 14: return Set(rt, $"{R(rs)} ^ {uimm}");
            case 15: return Set(rt, Hex((i & 0xFFFF) << 16));
            case 16: return $"m.Cop0({Hex(i)}, {Hex(pc)});";
            case 18: return $"m.Cop2({Hex(i)});";
            // loads write through a temp: the address register may be the destination
            case 32: return rt == 0 ? $"m.Read8({A()});" : $"r[{rt}] = (uint)(sbyte)m.Read8({A()});";
            case 33: return rt == 0 ? $"m.Read16({A()});" : $"r[{rt}] = (uint)(short)m.Read16({A()});";
            case 34: return Set(rt, $"m.Lwl({A()}, {R(rt)})");
            case 35: return rt == 0 ? $"m.Read32({A()});" : $"r[{rt}] = m.Read32({A()});";
            case 36: return rt == 0 ? $"m.Read8({A()});" : $"r[{rt}] = m.Read8({A()});";
            case 37: return rt == 0 ? $"m.Read16({A()});" : $"r[{rt}] = m.Read16({A()});";
            case 38: return Set(rt, $"m.Lwr({A()}, {R(rt)})");
            case 40: return $"m.Write8({A()}, {R(rt)});";
            case 41: return $"m.Write16({A()}, {R(rt)});";
            case 42: return $"m.Swl({A()}, {R(rt)});";
            case 43: return $"m.Write32({A()}, {R(rt)});";
            case 46: return $"m.Swr({A()}, {R(rt)});";
            case 50: return $"m.Gte.Write({rt}, m.Read32({A()}));";
            case 58: return $"m.Write32({A()}, m.Gte.Read({rt}));";
        }
        return $"Psx.Log(\"bad instruction {i:X8} at {pc:X8}\");";
    }

    static string Name(uint a) => $"F_{a:X8}";

    sealed class Emitter(Image img, Func f, Dictionary<uint, string> direct, string? selfName = null)
    {
        readonly StringBuilder sb = new();
        int pending;   // instructions since the last cycle flush

        void Line(string s) { if (s.Length > 0) sb.Append("        ").AppendLine(s); }
        void Flush() { if (pending > 0) { Line($"m.Cycles += {pending * 2};"); pending = 0; } }

        string Call(uint t) => direct.TryGetValue(t, out var n) ? $"{n}(m);" : $"m.Call({Hex(t)});";

        /// <summary>jump to t: inside the function = goto, otherwise a tail call</summary>
        string Goto(uint t, uint from)
        {
            if (f.Body.Contains(t) && f.Labels.Contains(t))
                return t <= from ? $"{{ if (m.Cycles >= m.NextEvent) m.Tick(); goto L_{t:X8}; }}" : $"goto L_{t:X8};";
            return direct.TryGetValue(t, out var n) ? $"{{ {n}(m); return; }}" : $"{{ m.Jump({Hex(t)}, ra0, sp0); return; }}";
        }

        public string Emit()
        {
            sb.AppendLine($"    public static void {selfName ?? direct.GetValueOrDefault(f.Entry, Name(f.Entry))}(Psx m)");
            sb.AppendLine("    {");
            Line("var r = m.R;");
            Line("bool bc; uint bt;");
            Line("uint ra0 = r[31], sp0 = r[29];   // how the interpreter recognises this function returning");
            Line("if (m.Cycles >= m.NextEvent) m.Tick();");
            if (!f.Labels.Contains(f.Entry) || f.Body.Count == 0) { Line($"m.Interpret({Hex(f.Entry)}, ra0, sp0); return;"); }
            Line($"goto L_{f.Entry:X8};");
            var skip = new HashSet<uint>();   // delay slots already emitted with their branch
            uint? prevEnd = null;
            foreach (var pc in f.Body)
            {
                if (skip.Contains(pc) && !f.Labels.Contains(pc)) continue;
                if (prevEnd is { } pe && pe != pc)
                {
                    // the previous instruction falls through to something not emitted next
                    Flush();
                    Line(f.Body.Contains(pe) ? $"goto L_{pe:X8};" : $"{{ m.Interpret({Hex(pe)}, ra0, sp0); return; }}");
                }
                if (f.Labels.Contains(pc)) { Flush(); sb.AppendLine($"    L_{pc:X8}:"); }
                uint i = img.At(pc);
                if (skip.Contains(pc))
                {
                    // a jump lands on a delay slot: run it on its own, then continue after the branch
                    Line($"// {pc:X8}: {Disasm.Op(i, pc)} (delay slot entry)");
                    Line(Simple(i, pc)); pending++;
                    prevEnd = pc + 4;
                    continue;
                }
                Line($"// {pc:X8}: {Disasm.Op(i, pc)}");
                if (!IsCti(i))
                {
                    Line(Simple(i, pc)); pending++;
                    prevEnd = pc + 4;
                    continue;
                }
                uint d = img.In(pc + 4) ? img.At(pc + 4) : 0;
                if (IsCti(d)) Psx.Log($"recomp: branch in delay slot at {pc:X8}");
                string delay = IsCti(d) ? "" : Simple(d, pc + 4);
                skip.Add(pc + 4);
                pending += 2;
                prevEnd = EmitCti(i, pc, delay);
            }
            if (prevEnd is { } last)
            {
                Flush();
                Line(f.Body.Contains(last) ? $"goto L_{last:X8};" : $"m.Interpret({Hex(last)}, ra0, sp0);");
            }
            sb.AppendLine("    }");
            return sb.ToString();
        }

        /// <summary>emit a branch/jump with its delay slot; returns the fall-through address (null if none)</summary>
        uint? EmitCti(uint i, uint pc, string delay)
        {
            int rs = Rs(i), rt = Rt(i);
            if (IsBranch(i))
            {
                string cond = Op(i) switch
                {
                    4 => $"{R(rs)} == {R(rt)}",
                    5 => $"{R(rs)} != {R(rt)}",
                    6 => $"(int){R(rs)} <= 0",
                    7 => $"(int){R(rs)} > 0",
                    _ => (rt & 1) != 0 ? $"(int){R(rs)} >= 0" : $"(int){R(rs)} < 0",
                };
                uint t = BranchTarget(i, pc);
                Line($"bc = {cond};");
                if (Op(i) == 1 && (rt & 0x1E) == 0x10) Line($"r[31] = {Hex(pc + 8)};");
                Line(delay);
                Flush();
                Line($"if (bc) {Goto(t, pc)}");
                return pc + 8;
            }
            switch (Op(i))
            {
                case 2:
                    Line(delay); Flush();
                    Line(Goto(JumpTarget(i, pc), pc));
                    return null;
                case 3:
                    Line($"r[31] = {Hex(pc + 8)};");
                    Line(delay); Flush();
                    Line(Call(JumpTarget(i, pc)));
                    return pc + 8;
            }
            if (Fn(i) == 9)
            {
                Line($"bt = {R(rs)};");
                if (Rd(i) != 0) Line($"r[{Rd(i)}] = {Hex(pc + 8)};");
                Line(delay); Flush();
                Line("m.Call(bt);");
                return pc + 8;
            }
            // jr
            if (rs == 31) { Line(delay); Flush(); Line("return;"); return null; }
            Line($"bt = {R(rs)};");
            Line(delay); Flush();
            if (f.Tables.TryGetValue(pc, out var tab))
            {
                Line("switch (bt)");
                Line("{");
                foreach (var t in tab.Distinct()) Line($"    case {Hex(t)}: goto L_{t:X8};");
                Line("}");
            }
            Line("m.Jump(bt, ra0, sp0); return;");
            return null;
        }
    }

    /// <summary>
    /// --map: one line per function (linear extent up to the next entry) with the hardware registers it touches,
    /// BIOS calls, strings it references, callees and callers. The PsyQ libraries show up as the I/O leaves.
    /// </summary>
    public static void Map(byte[] exe, string path, IEnumerable<uint> extra)
    {
        var img = Load(exe);
        var entries = FindEntries(img, BitConverter.ToUInt32(exe, 0x10), extra).OrderBy(e => e).ToList();
        var callers = new Dictionary<uint, List<uint>>();
        var lines = new List<string>();
        var info = new Dictionary<uint, (SortedSet<uint> Io, SortedSet<string> Bios, SortedSet<string> Str, SortedSet<uint> Calls)>();
        for (int k = 0; k < entries.Count; k++)
        {
            uint e = entries[k], end = k + 1 < entries.Count ? entries[k + 1] : img.End;
            var io = new SortedSet<uint>(); var bios = new SortedSet<string>(); var str = new SortedSet<string>(); var calls = new SortedSet<uint>();
            var hi = new uint?[32];
            int t1 = -1;
            for (uint a = e; a < end; a += 4)
            {
                uint i = img.At(a);
                if (Op(i) == 15) { hi[Rt(i)] = (i & 0xFFFF) << 16; continue; }
                if (Op(i) == 3) { uint t = JumpTarget(i, a); calls.Add(t); if (!callers.TryGetValue(t, out var l)) callers[t] = l = new(); l.Add(e); }
                if (Op(i) == 9 && Rs(i) == 0 && Rt(i) == 9) t1 = (int)(i & 0xFFFF);
                if (Op(i) == 9 && Rs(i) == 0 && Rt(i) == 10 && (i & 0xFFFF) is 0xA0 or 0xB0 or 0xC0) bios.Add($"{i & 0xFF:X2}");
                if ((Op(i) is 9 or 13 or (>= 32 and <= 46)) && hi[Rs(i)] is { } h)
                {
                    uint addr = Op(i) == 13 ? h | (i & 0xFFFF) : h + (uint)(short)i;
                    if ((addr & 0xFFFFF000) == 0x1F801000) io.Add(addr);
                    else if (img.In(addr) || (addr >= img.Base && addr < img.End + 0x100000))
                    {
                        if (img.In(addr & ~3u))
                        {
                            var sb = new StringBuilder();
                            for (uint q = addr; q < Math.Min(img.End, addr + 40); q++)
                            {
                                byte c = (byte)(img.At(q & ~3u) >> (int)((q & 3) * 8));
                                if (c == 0) break;
                                if (c < 32 || c > 126) { sb.Clear(); break; }
                                sb.Append((char)c);
                            }
                            if (sb.Length >= 4) str.Add(sb.ToString());
                        }
                    }
                }
                if (Op(i) == 9 && Rt(i) != Rs(i)) hi[Rt(i)] = null;
            }
            if (bios.Count > 0 && t1 >= 0) { var b = bios.First(); bios.Clear(); bios.Add($"{b}:{t1:X2}"); }
            info[e] = (io, bios, str, calls);
        }
        foreach (var e in entries)
        {
            var (io, bios, str, calls) = info[e];
            var sb = new StringBuilder($"{e:X8}");
            if (io.Count > 0) sb.Append(" io[" + string.Join(' ', io.Select(x => (x & 0xFFF).ToString("X3"))) + "]");
            if (bios.Count > 0) sb.Append(" bios[" + string.Join(' ', bios) + "]");
            if (str.Count > 0) sb.Append(" str[" + string.Join(" | ", str.Take(3)) + "]");
            sb.Append($" calls[{calls.Count}] callers[{(callers.TryGetValue(e, out var c) ? c.Count : 0)}]");
            lines.Add(sb.ToString());
        }
        File.WriteAllLines(path, lines);
    }

    /// <summary>
    /// Recompile the boot EXE, and every overlay saved in overlayDir ("&lt;load address&gt;_&lt;crc&gt;.bin"), into C# files
    /// in outDir; returns the number of functions.
    /// </summary>
    public static int Run(byte[] exe, string outDir, IEnumerable<uint> extraEntries, string? overlayDir = null)
    {
        var extra = extraEntries.ToList();
        var img = Load(exe);
        // profile entries where a disc-loaded module put different code belong to that module, not the EXE
        var dumps = overlayDir != null && Directory.Exists(overlayDir)
            ? Directory.GetFiles(overlayDir, "*.bin").Order().Select(p => (Start: Convert.ToUInt32(Path.GetFileNameWithoutExtension(p).Split('_')[0], 16), Bytes: File.ReadAllBytes(p))).ToList()
            : [];
        bool Overwritten(uint a) => dumps.Any(d => a >= d.Start && a + 4 <= d.Start + (uint)d.Bytes.Length && BitConverter.ToUInt32(d.Bytes, (int)(a - d.Start)) != img.At(a));
        var exeExtra = extra.Where(a => img.In(a) && !Overwritten(a)).ToList();
        // EXE functions that only overlays call
        foreach (var (start, bytes) in dumps)
            for (int o = 0; o + 4 <= bytes.Length; o += 4)
            {
                uint i = BitConverter.ToUInt32(bytes, o), t = JumpTarget(i, start + (uint)o);
                if (Op(i) == 3 && img.In(t) && !Overwritten(t) && Valid(img.At(t))) exeExtra.Add(t);
            }
        uint entry = BitConverter.ToUInt32(exe, 0x10);
        var entries = FindEntries(img, entry, exeExtra);
        var funcs = entries.OrderBy(e => e).Select(e => Discover(img, e, entries)).Where(f => f.Body.Count > 0).ToList();
        // SDK functions are called through the dispatch table so their native versions (PsyQ.cs) take over
        var sdk = Symbols.ByExe.TryGetValue(Ps1tl.Crc32.Of(exe), out var syms) ? syms.Values.ToHashSet() : [];
        var exeDirect = funcs.Where(f => !sdk.Contains(f.Entry)).ToDictionary(f => f.Entry, f => Name(f.Entry));
        Directory.CreateDirectory(outDir);
        foreach (var old in Directory.GetFiles(outDir, "*.g.cs")) File.Delete(old);
        int file = 0, total = funcs.Count;
        void Write(Image im, List<Func> fs, Dictionary<uint, string> direct, Func<Func, string>? nameOf = null)
        {
            const int PerFile = 40;
            for (int k = 0; k * PerFile < fs.Count; k++)
            {
                var sb = new StringBuilder();
                sb.AppendLine("// <auto-generated> recompiled game code: do not edit, do not distribute </auto-generated>");
                sb.AppendLine("namespace Ps1tl.Port;");
                sb.AppendLine("static partial class Recompiled");
                sb.AppendLine("{");
                foreach (var f in fs.Skip(k * PerFile).Take(PerFile))
                {
                    if (f.Truncated) Psx.Log($"recomp: {f.Entry:X8} truncated");
                    sb.Append(new Emitter(im, f, direct, nameOf?.Invoke(f)).Emit());
                }
                sb.AppendLine("}");
                File.WriteAllText(Path.Combine(outDir, $"Game{file++:D3}.g.cs"), sb.ToString());
            }
        }
        // the executable's SDK functions still get compiled (the fallback with --emulate-sdk), just never called directly
        Write(img, funcs, exeDirect);

        // overlay functions: each identified by the CRC of the code it covers; calls between them go through the
        // dispatcher so every callee is checked against what is in RAM
        var ovl = new List<(uint Addr, uint Lo, uint Hi, uint Crc, string Name)>();
        var seen = new HashSet<(uint, uint)>();
        foreach (var (start, bytes) in dumps)
        {
            var oi = new Image { Base = start, End = start + (uint)(bytes.Length & ~3), Words = new uint[bytes.Length / 4] };
            Buffer.BlockCopy(bytes, 0, oi.Words, 0, bytes.Length & ~3);
            var known = extra.Where(oi.In).ToList();
            var oe = FindEntries(oi, known.Count > 0 ? known[0] : start, known);
            oe.RemoveWhere(e => !oi.In(e) || !Valid(oi.At(e)) || (img.In(e) && img.At(e) == oi.At(e)));   // unchanged EXE code is the EXE's
            var ofs = new List<Func>();
            var names = new Dictionary<Func, string>();
            foreach (var f in oe.Order().Select(e => Discover(oi, e, oe)).Where(f => f.Body.Count > 0))
            {
                uint lo = f.Body.Min(), hi = f.Body.Max() + 4;
                uint crc = Ps1tl.Crc32.Of(bytes.AsSpan((int)(lo - start), (int)(hi - lo)));
                if (!seen.Add((f.Entry, crc))) continue;   // the same code saved from another load
                names[f] = $"O{crc:X8}_{Name(f.Entry)}";
                ofs.Add(f);
                ovl.Add((f.Entry, lo, hi, crc, names[f]));
            }
            if (ofs.Count == 0) continue;
            Write(oi, ofs, exeDirect, f => names[f]);
            total += ofs.Count;
            Psx.Log($"recomp: overlay load {start:X8}: {ofs.Count} new functions");
        }

        var reg = new StringBuilder();
        reg.AppendLine("// <auto-generated/>");
        reg.AppendLine("namespace Ps1tl.Port;");
        reg.AppendLine("static partial class Recompiled");
        reg.AppendLine("{");
        reg.AppendLine($"    const uint ExeCrc = 0x{Ps1tl.Crc32.Of(exe):X8};");
        reg.AppendLine("    static partial void Crc(ref uint crc) => crc = ExeCrc;");
        reg.AppendLine("    static partial void RegisterAll(Psx m)");
        reg.AppendLine("    {");
        foreach (var f in funcs) reg.AppendLine($"        m.Funcs[0x{f.Entry:X8}] = {Name(f.Entry)};");
        foreach (var (addr, lo, hi, crc, name) in ovl)
            reg.AppendLine($"        m.Overlays.Add(0x{addr:X8}, 0x{lo:X8}, 0x{hi:X8}, 0x{crc:X8}, {name});");
        reg.AppendLine("    }");
        reg.AppendLine("}");
        File.WriteAllText(Path.Combine(outDir, "Register.g.cs"), reg.ToString());
        Psx.Log($"recomp: {total} functions ({ovl.Count} in overlays)");
        return total;
    }
}

/// <summary>the generated code plugs in here (nothing happens if it has not been generated)</summary>
static partial class Recompiled
{
    static partial void RegisterAll(Psx m);
    static partial void Crc(ref uint crc);

    /// <summary>plug in the recompiled functions if they were made from this exact executable</summary>
    public static bool Register(Psx m, byte[] exe, bool overlays = true)
    {
        uint want = 0;
        Crc(ref want);
        if (want == 0 || want != Ps1tl.Crc32.Of(exe)) return false;
        RegisterAll(m);
        if (!overlays) m.Overlays.Clear();
        return true;
    }
}
