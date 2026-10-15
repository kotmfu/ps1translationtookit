namespace Ps1tl.Port;

/// <summary>
/// Game code that is not in the boot executable: modules the game reads from disc into RAM and runs (overlays).
/// Each recompiled overlay function knows the address range and CRC of the code it was made from; when code is
/// about to run at an address with no native function, a candidate whose bytes match what is in RAM now is
/// switched in. A disc load over a switched-in function switches it out again. Code that matches nothing runs
/// in the interpreter, and the disc load it came from is saved to overlays/ for the next recompile.
/// </summary>
public sealed class Overlays(Psx m)
{
    public sealed record Fn(uint Addr, uint Lo, uint Hi, uint Crc, Action<Psx> Run);

    readonly Dictionary<uint, List<Fn>> known = new();
    readonly Dictionary<uint, Fn> active = new();
    readonly List<(uint Start, uint End, bool Dumped)> loads = new();
    /// <summary>where unknown overlays are saved for the next recompile (null: don't)</summary>
    public string? DumpDir;
    public uint ExeStart, ExeEnd;
    public int Switches;

    public void Add(uint addr, uint lo, uint hi, uint crc, Action<Psx> run)
    {
        if (!known.TryGetValue(addr, out var l)) known[addr] = l = new();
        l.Add(new Fn(addr, lo, hi, crc, run));
    }

    public void Clear() => known.Clear();

    /// <summary>a disc load wrote [a, a+len): extend the load it continues, switch out functions it overwrote</summary>
    public void Loaded(uint a, uint len)
    {
        a |= 0x80000000;
        uint b = a + len;
        if (active.Count > 0)
            foreach (var f in active.Values.Where(f => a < f.Hi && b > f.Lo).ToList())
            {
                active.Remove(f.Addr);
                m.Funcs.Remove(f.Addr);
            }
        int k = loads.FindIndex(l => l.End == a && !l.Dumped);
        if (k >= 0) { loads[k] = (loads[k].Start, b, false); return; }
        loads.RemoveAll(l => a < l.End && b > l.Start);
        loads.Add((a, b, false));
    }

    /// <summary>code is about to run at pc, which has no native function: switch one in if its code matches</summary>
    public void Touch(uint pc)
    {
        if (known.TryGetValue(pc, out var cands))
            foreach (var f in cands)
                if (Ps1tl.Crc32.Of(m.RamSpan(f.Lo, (int)(f.Hi - f.Lo))) == f.Crc)
                {
                    m.Funcs[pc] = f.Run;
                    active[pc] = f;
                    Switches++;
                    return;
                }
        if (DumpDir == null) return;
        for (int i = 0; i < loads.Count; i++)
        {
            var (s, e, done) = loads[i];
            if (pc < s || pc >= e || done) continue;
            loads[i] = (s, e, true);   // once per load: the module's data changes as soon as it runs
            uint crc = Ps1tl.Crc32.Of(m.RamSpan(s, (int)(e - s)));
            Directory.CreateDirectory(DumpDir);
            var path = Path.Combine(DumpDir, $"{s:X8}_{crc:X8}.bin");
            if (!File.Exists(path)) { File.WriteAllBytes(path, m.RamSpan(s, (int)(e - s)).ToArray()); Psx.Log($"new overlay saved: {path} (pc {pc:X8}, frame {m.Frame})"); }
            return;
        }
    }
}
