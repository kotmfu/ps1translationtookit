using System.Text.Json;
using Avalonia.Input;
using static SDL.SDL3;
using SDL;

namespace Ps1tl.Port;

/// <summary>PS1 pad buttons, in the bit order of the controller protocol</summary>
public enum PadButton { Select, L3, R3, Start, Up, Right, Down, Left, L2, R2, L1, R1, Triangle, Circle, Cross, Square }

/// <summary>
/// Player 1 input: every connected game controller (via SDL3: Xbox, DualShock/DualSense, Switch, generic pads,
/// hot-plugged) plus the keyboard, mapped to the PS1 pad through controls.json in the game's data folder.
/// </summary>
public sealed unsafe class Input
{
    /// <summary>controls.json: PS1 button -> controller button / keyboard key names</summary>
    public sealed class Config
    {
        public Dictionary<string, string[]> Pad { get; set; } = new()
        {
            ["Cross"] = ["south"], ["Circle"] = ["east"], ["Square"] = ["west"], ["Triangle"] = ["north"],
            ["Start"] = ["start"], ["Select"] = ["back"],
            ["L1"] = ["leftshoulder"], ["R1"] = ["rightshoulder"], ["L2"] = ["lefttrigger"], ["R2"] = ["righttrigger"],
            ["L3"] = ["leftstick"], ["R3"] = ["rightstick"],
            ["Up"] = ["dpup", "-lefty"], ["Down"] = ["dpdown", "+lefty"], ["Left"] = ["dpleft", "-leftx"], ["Right"] = ["dpright", "+leftx"],
        };
        public Dictionary<string, string[]> Keys { get; set; } = new()
        {
            ["Cross"] = ["Z"], ["Circle"] = ["X"], ["Square"] = ["A"], ["Triangle"] = ["S"],
            ["Start"] = ["Enter"], ["Select"] = ["Back", "RightShift"],
            ["L1"] = ["Q"], ["R1"] = ["W"], ["L2"] = ["D1"], ["R2"] = ["D2"],
            ["Up"] = ["Up"], ["Down"] = ["Down"], ["Left"] = ["Left"], ["Right"] = ["Right"],
        };
        /// <summary>stick/trigger travel (0..1) that counts as pressed</summary>
        public double Deadzone { get; set; } = 0.45;
        /// <summary>controller button held for fast forward (or "none")</summary>
        public string FastForward { get; set; } = "guide";
    }

    readonly Config cfg;
    readonly List<(int Bit, SDL_GamepadButton Btn)> buttons = new();
    readonly List<(int Bit, SDL_GamepadAxis Axis, int Sign)> axes = new();
    readonly Dictionary<Key, int> keys = new();
    readonly SDL_GamepadButton ffButton = SDL_GamepadButton.SDL_GAMEPAD_BUTTON_INVALID;
    readonly Dictionary<SDL_JoystickID, IntPtr> open = new();
    readonly bool sdl;
    int scan;
    public bool FastForward { get; private set; }
    public string Status { get; private set; } = "";

    public Input(string configPath)
    {
        cfg = Load(configPath);
        foreach (var (name, binds) in cfg.Pad)
        {
            if (!Enum.TryParse<PadButton>(name, true, out var pb)) { Psx.Log($"controls: unknown PS1 button {name}"); continue; }
            foreach (var b in binds)
            {
                if (b.Length > 1 && b[0] is '+' or '-' && Axis(b[1..]) is { } ax) axes.Add(((int)pb, ax, b[0] == '-' ? -1 : 1));
                else if (Axis(b) is { } tr) axes.Add(((int)pb, tr, 1));
                else if (Button(b) is { } bt) buttons.Add(((int)pb, bt));
                else Psx.Log($"controls: unknown controller input {b}");
            }
        }
        foreach (var (name, ks) in cfg.Keys)
            if (Enum.TryParse<PadButton>(name, true, out var pb))
                foreach (var k in ks)
                    if (Enum.TryParse<Key>(k, true, out var key)) keys[key] = (int)pb; else Psx.Log($"controls: unknown key {k}");
        if (Button(cfg.FastForward) is { } ff) ffButton = ff;
        try
        {
            // we have no SDL window: read controllers whether or not SDL thinks it has focus
            SDL_SetHint(SDL_HINT_JOYSTICK_ALLOW_BACKGROUND_EVENTS, "1");
            sdl = SDL_Init(SDL_InitFlags.SDL_INIT_GAMEPAD);
            if (!sdl) Psx.Log("controllers unavailable: " + SDL_GetError());
        }
        catch (Exception e) { Psx.Log("controllers unavailable: " + e.Message); }
    }

    static Config Load(string path)
    {
        var opt = new JsonSerializerOptions { WriteIndented = true, PropertyNameCaseInsensitive = true };
        try
        {
            if (File.Exists(path)) return JsonSerializer.Deserialize<Config>(File.ReadAllText(path), opt) ?? new();
        }
        catch (Exception e) { Psx.Log($"controls: {path} unreadable ({e.Message}), using defaults"); return new(); }
        var c = new Config();
        try { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, JsonSerializer.Serialize(c, opt)); } catch { }
        return c;
    }

    static SDL_GamepadButton? Button(string s)
    {
        // face buttons by position, so the same config reads right on Xbox, PlayStation and Switch pads
        switch (s.ToLowerInvariant())
        {
            case "south": return SDL_GamepadButton.SDL_GAMEPAD_BUTTON_SOUTH;
            case "east": return SDL_GamepadButton.SDL_GAMEPAD_BUTTON_EAST;
            case "west": return SDL_GamepadButton.SDL_GAMEPAD_BUTTON_WEST;
            case "north": return SDL_GamepadButton.SDL_GAMEPAD_BUTTON_NORTH;
        }
        var b = SDL_GetGamepadButtonFromString(s);
        return b == SDL_GamepadButton.SDL_GAMEPAD_BUTTON_INVALID ? null : b;
    }

    static SDL_GamepadAxis? Axis(string s)
    {
        var a = SDL_GetGamepadAxisFromString(s);
        return a == SDL_GamepadAxis.SDL_GAMEPAD_AXIS_INVALID ? null : a;
    }

    /// <summary>pressed PS1 buttons (bit set = pressed) from the controllers and the held keys</summary>
    public int Read(IEnumerable<Key> held)
    {
        int bits = 0;
        foreach (var k in held) if (keys.TryGetValue(k, out var b)) bits |= 1 << b;
        FastForward = false;
        if (!sdl) return bits;
        SDL_UpdateGamepads();
        if (scan-- <= 0) { scan = 30; Rescan(); }
        short dz = (short)(cfg.Deadzone * 32767);
        foreach (var p in open.Values)
        {
            var g = (SDL_Gamepad*)p;
            foreach (var (bit, btn) in buttons) if (SDL_GetGamepadButton(g, btn)) bits |= 1 << bit;
            foreach (var (bit, ax, sign) in axes)
            {
                int v = SDL_GetGamepadAxis(g, ax) * sign;
                if (v > dz) bits |= 1 << bit;
            }
            if (ffButton != SDL_GamepadButton.SDL_GAMEPAD_BUTTON_INVALID && SDL_GetGamepadButton(g, ffButton)) FastForward = true;
        }
        return bits;
    }

    /// <summary>open newly connected controllers, close unplugged ones</summary>
    void Rescan()
    {
        foreach (var (id, p) in open.ToList())
            if (!SDL_GamepadConnected((SDL_Gamepad*)p)) { SDL_CloseGamepad((SDL_Gamepad*)p); open.Remove(id); Psx.Log("controller disconnected"); }
        using var ids = SDL_GetGamepads();
        if (ids == null) return;
        for (int i = 0; i < ids.Count; i++)
        {
            var id = ids[i];
            if (open.ContainsKey(id)) continue;
            var g = SDL_OpenGamepad(id);
            if (g == null) continue;
            open[id] = (IntPtr)g;
            Psx.Log($"controller connected: {SDL_GetGamepadName(g)}");
        }
        Status = open.Count == 0 ? "" : $"{open.Count} controller(s)";
    }
}
