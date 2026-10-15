namespace Ps1tl.Port;

/// <summary>R3000 disassembler (debugging and comments in generated code)</summary>
public static class Disasm
{
    static readonly string[] Reg =
    [
        "zero", "at", "v0", "v1", "a0", "a1", "a2", "a3", "t0", "t1", "t2", "t3", "t4", "t5", "t6", "t7",
        "s0", "s1", "s2", "s3", "s4", "s5", "s6", "s7", "t8", "t9", "k0", "k1", "gp", "sp", "fp", "ra",
    ];

    public static string Op(uint i, uint pc)
    {
        string rs = Reg[i >> 21 & 31], rt = Reg[i >> 16 & 31], rd = Reg[i >> 11 & 31];
        int sh = (int)(i >> 6 & 31);
        short imm = (short)i;
        uint uimm = i & 0xFFFF;
        uint bt = pc + 4 + (uint)(imm << 2);
        uint jt = (pc & 0xF0000000) | (i & 0x3FFFFFF) << 2;
        string Mem(string n) => $"{n} {rt}, {imm}({rs})";
        switch (i >> 26)
        {
            case 0:
                return (i & 63) switch
                {
                    0 => i == 0 ? "nop" : $"sll {rd}, {rt}, {sh}",
                    2 => $"srl {rd}, {rt}, {sh}", 3 => $"sra {rd}, {rt}, {sh}",
                    4 => $"sllv {rd}, {rt}, {rs}", 6 => $"srlv {rd}, {rt}, {rs}", 7 => $"srav {rd}, {rt}, {rs}",
                    8 => $"jr {rs}", 9 => $"jalr {rd}, {rs}", 12 => "syscall", 13 => "break",
                    16 => $"mfhi {rd}", 17 => $"mthi {rs}", 18 => $"mflo {rd}", 19 => $"mtlo {rs}",
                    24 => $"mult {rs}, {rt}", 25 => $"multu {rs}, {rt}", 26 => $"div {rs}, {rt}", 27 => $"divu {rs}, {rt}",
                    32 => $"add {rd}, {rs}, {rt}", 33 => $"addu {rd}, {rs}, {rt}", 34 => $"sub {rd}, {rs}, {rt}", 35 => $"subu {rd}, {rs}, {rt}",
                    36 => $"and {rd}, {rs}, {rt}", 37 => $"or {rd}, {rs}, {rt}", 38 => $"xor {rd}, {rs}, {rt}", 39 => $"nor {rd}, {rs}, {rt}",
                    42 => $"slt {rd}, {rs}, {rt}", 43 => $"sltu {rd}, {rs}, {rt}",
                    _ => $"special? {i:X8}",
                };
            case 1:
                return (i >> 16 & 31) switch { 0 => "bltz", 1 => "bgez", 16 => "bltzal", 17 => "bgezal", _ => "regimm?" } + $" {rs}, {bt:X8}";
            case 2: return $"j {jt:X8}";
            case 3: return $"jal {jt:X8}";
            case 4: return $"beq {rs}, {rt}, {bt:X8}";
            case 5: return $"bne {rs}, {rt}, {bt:X8}";
            case 6: return $"blez {rs}, {bt:X8}";
            case 7: return $"bgtz {rs}, {bt:X8}";
            case 8: return $"addi {rt}, {rs}, {imm}";
            case 9: return $"addiu {rt}, {rs}, {imm}";
            case 10: return $"slti {rt}, {rs}, {imm}";
            case 11: return $"sltiu {rt}, {rs}, {imm}";
            case 12: return $"andi {rt}, {rs}, 0x{uimm:X}";
            case 13: return $"ori {rt}, {rs}, 0x{uimm:X}";
            case 14: return $"xori {rt}, {rs}, 0x{uimm:X}";
            case 15: return $"lui {rt}, 0x{uimm:X}";
            case 16: return $"cop0 {i:X8}";
            case 18: return $"cop2 {i:X8}";
            case 32: return Mem("lb"); case 33: return Mem("lh"); case 34: return Mem("lwl"); case 35: return Mem("lw");
            case 36: return Mem("lbu"); case 37: return Mem("lhu"); case 38: return Mem("lwr");
            case 40: return Mem("sb"); case 41: return Mem("sh"); case 42: return Mem("swl"); case 43: return Mem("sw"); case 46: return Mem("swr");
            case 50: return $"lwc2 {i >> 16 & 31}, {imm}({rs})";
            case 58: return $"swc2 {i >> 16 & 31}, {imm}({rs})";
        }
        return $"? {i:X8}";
    }

    public static void Dump(Psx m, uint from, int n)
    {
        for (int k = 0; k < n; k++)
        {
            uint a = from + (uint)k * 4, i = m.Read32(a);
            Psx.Log($"{a:X8}  {i:X8}  {Op(i, a)}");
        }
    }
}
