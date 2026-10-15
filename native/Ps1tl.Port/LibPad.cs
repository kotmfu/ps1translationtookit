namespace Ps1tl.Port;

/// <summary>
/// Native libpad (controllers), part of the PsyQ replacement. Sony's driver talks to the pads over the serial
/// port byte by byte from the vblank interrupt; here the pad buffers given to PadInitDirect are simply filled
/// from the player's input once per frame. Player 1 is a digital pad, player 2 is unplugged.
/// </summary>
public sealed class LibPad(Psx m)
{
    uint A0 => m.R[4];
    uint A1 => m.R[5];
    uint V0 { set => m.R[2] = value; }

    /// <summary>
    /// CPU time Sony's driver takes in each vblank interrupt (measured: ~10,000 cycles). The game's scene loading
    /// depends on it: without it Yuuyami's school scene corrupts a model's part table and crashes (frame 2355)
    /// </summary>
    const long DriverCycles = 10_000;
    uint buf1, buf2;
    bool running, found;   // found: the driver has seen the pad (from the first poll after PadStartCom)

    public Dictionary<string, Action> Impl(IReadOnlyDictionary<string, uint> symbols) => new()
    {
        ["PadInitDirect"] = () => (buf1, buf2) = (A0, A1),
        ["PadStartCom"] = () => running = true,
        ["PadStopCom"] = () => running = found = false,
        // states: 0 disconnected ... 2 found a pad without actuators (a digital pad's steady state)
        ["PadGetState"] = () => V0 = A0 == 0 && found ? 2u : 0,
        // current id 4 (digital pad); it has no extended (analog / DualShock) modes, so the rest answer 0
        ["PadInfoMode"] = () => V0 = A0 == 0 && A1 == 1 ? 4u : 0,
        ["PadSetAct"] = () => { },
        ["PadSetActAlign"] = () => V0 = 0,
    };

    /// <summary>
    /// once per frame: what the driver leaves in the buffers after polling both ports. Each buffer is a status
    /// byte, the pad's id, then four 8-byte slots (one per multitap socket, the first holding a pad's data);
    /// slots with nothing in them say 0xFF, as Sony's driver writes them.
    /// </summary>
    public void VBlank()
    {
        if (!running) return;
        found = true;
        m.Cycles += DriverCycles;
        if (buf1 != 0)
        {
            m.Write8(buf1, 0x00);   // transfer ok
            m.Write8(buf1 + 1, 0x41);   // digital pad, one halfword of buttons
            m.Write8(buf1 + 2, m.Buttons);
            m.Write8(buf1 + 3, (uint)m.Buttons >> 8);
            for (uint s = 1; s < 4; s++) m.Write8(buf1 + 2 + s * 8, 0xFF);
        }
        if (buf2 != 0)
        {
            m.Write8(buf2, 0xFF);   // nothing connected
            for (uint s = 0; s < 4; s++) m.Write8(buf2 + 2 + s * 8, 0xFF);
        }
    }
}
