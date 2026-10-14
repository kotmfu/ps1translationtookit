using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace Ps1tl.Port;

/// <summary>
/// The game window: runs the machine on its own thread at 60 Hz, shows each finished frame scaled to 4:3,
/// and maps the keyboard (and an XInput pad on Windows) to the PS1 controller.
/// </summary>
sealed class GameWindow : Window
{
    readonly Psx m;
    readonly Image view = new() { Stretch = Stretch.Uniform };
    WriteableBitmap? bmp;
    readonly uint[] frame = new uint[640 * 512];
    readonly uint[] shown = new uint[640 * 512];
    int fw, fh;
    volatile bool fresh;
    readonly object gate = new();
    readonly HashSet<Key> held = new();
    volatile bool fast;

    // PS1 pad bits (active low in Psx.Buttons)
    static readonly Dictionary<Key, int> Keys = new()
    {
        [Key.Back] = 0, [Key.RightShift] = 0, [Key.Enter] = 3,
        [Key.Up] = 4, [Key.Right] = 5, [Key.Down] = 6, [Key.Left] = 7,
        [Key.D1] = 8, [Key.D2] = 9, [Key.Q] = 10, [Key.W] = 11,
        [Key.S] = 12, [Key.X] = 13, [Key.Z] = 14, [Key.A] = 15,
    };

    public GameWindow(Psx machine, string title)
    {
        m = machine;
        Title = title;
        Width = 960; Height = 720;
        Background = Brushes.Black;
        RenderOptions.SetBitmapInterpolationMode(view, Avalonia.Media.Imaging.BitmapInterpolationMode.LowQuality);
        Content = new Viewbox { Stretch = Stretch.Uniform, Child = new Border { Width = 640, Height = 480, Child = view } };
        view.Stretch = Stretch.Fill;
        KeyDown += (_, e) => { held.Add(e.Key); if (e.Key == Key.Tab) fast = true; if (e.Key == Key.F11) WindowState = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen; };
        KeyUp += (_, e) => { held.Remove(e.Key); if (e.Key == Key.Tab) fast = false; };
        Closed += (_, _) => Environment.Exit(0);
        DispatcherTimer.Run(() => { Present(); return true; }, TimeSpan.FromMilliseconds(8));
        m.Spu.Out = new AudioOut();
        var clock = Stopwatch.StartNew();
        long frames = 0;
        m.OnVBlank = () =>
        {
            lock (gate) { (fw, fh) = m.Gpu.Render(frame); fresh = true; }
            m.Buttons = Pad();
            frames++;
            if (fast) { frames = 0; clock.Restart(); return; }
            // pace to the console's refresh rate
            double due = frames * 1000.0 / 59.94;
            double ahead = due - clock.Elapsed.TotalMilliseconds;
            if (ahead > 1) Thread.Sleep((int)ahead);
            else if (ahead < -100) { frames = 0; clock.Restart(); }   // fell behind: don't try to catch up
        };
        new Thread(() =>
        {
            try { Boot.Run(m); }
            catch (Exception ex) { Psx.Log(ex.ToString()); Dispatcher.UIThread.Post(() => Title += " - stopped: " + ex.Message); }
        }) { IsBackground = true, Name = "psx" }.Start();
    }

    ushort Pad()
    {
        int bits = 0;
        foreach (var k in held) if (Keys.TryGetValue(k, out var b)) bits |= 1 << b;
        bits |= XPad.Read();
        return (ushort)~bits;
    }

    void Present()
    {
        if (!fresh) return;
        int w, h;
        lock (gate)
        {
            fresh = false;
            w = fw; h = fh;
            Array.Copy(frame, shown, w * h);
        }
        if (w <= 0 || h <= 0) return;
        if (bmp == null || bmp.PixelSize.Width != w || bmp.PixelSize.Height != h)
        {
            bmp = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            view.Source = bmp;
        }
        using (var fb = bmp.Lock())
            for (int y = 0; y < h; y++)
                Marshal.Copy((int[])(object)shown, y * w, fb.Address + y * fb.RowBytes, w);
        view.InvalidateVisual();
    }
}

/// <summary>first XInput controller -> PS1 pad bits (Windows only)</summary>
static class XPad
{
    [StructLayout(LayoutKind.Sequential)]
    struct State { public uint Packet; public ushort Buttons; public byte LT, RT; public short LX, LY, RX, RY; }
    [DllImport("xinput1_4.dll")] static extern int XInputGetState(int i, out State s);
    static bool dead = !OperatingSystem.IsWindows();

    public static int Read()
    {
        if (dead) return 0;
        State s;
        try { if (XInputGetState(0, out s) != 0) return 0; }
        catch { dead = true; return 0; }
        int b = 0;
        void Map(int x, int bit) { if ((s.Buttons & x) != 0) b |= 1 << bit; }
        Map(0x0001, 4); Map(0x0008, 5); Map(0x0002, 6); Map(0x0004, 7);
        Map(0x0010, 3); Map(0x0020, 0); Map(0x0100, 10); Map(0x0200, 11);
        Map(0x1000, 14); Map(0x2000, 13); Map(0x4000, 15); Map(0x8000, 12);
        if (s.LT > 100) b |= 1 << 8;
        if (s.RT > 100) b |= 1 << 9;
        if (s.LX < -16000) b |= 1 << 7; if (s.LX > 16000) b |= 1 << 5;
        if (s.LY > 16000) b |= 1 << 4; if (s.LY < -16000) b |= 1 << 6;
        return b;
    }
}
