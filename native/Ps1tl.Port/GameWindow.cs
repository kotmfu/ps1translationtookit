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
/// and feeds it the controllers and keyboard through <see cref="Input"/>.
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

    readonly Input input;

    public GameWindow(Psx machine, string title, Input input)
    {
        m = machine;
        this.input = input;
        Title = title;
        Width = 960; Height = 720;
        Background = Brushes.Black;
        RenderOptions.SetBitmapInterpolationMode(view, Avalonia.Media.Imaging.BitmapInterpolationMode.LowQuality);
        Content = new Viewbox { Stretch = Stretch.Uniform, Child = new Border { Width = 640, Height = 480, Child = view } };
        view.Stretch = Stretch.Fill;
        KeyDown += (_, e) => { lock (held) held.Add(e.Key); if (e.Key == Key.Tab) fast = true; if (e.Key == Key.F11) WindowState = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen; };
        KeyUp += (_, e) => { lock (held) held.Remove(e.Key); if (e.Key == Key.Tab) fast = false; };
        Deactivated += (_, _) => { lock (held) held.Clear(); fast = false; };
        Closed += (_, _) => { m.Bios.Card?.Flush(); Environment.Exit(0); };
        DispatcherTimer.Run(() => { Present(); return true; }, TimeSpan.FromMilliseconds(8));
        m.Spu.Out = new AudioOut();
        var clock = Stopwatch.StartNew();
        long frames = 0;
        m.OnVBlank = () =>
        {
            lock (gate) { (fw, fh) = m.Gpu.Render(frame); fresh = true; }
            Key[] keys;
            lock (held) keys = held.ToArray();
            m.Buttons = (ushort)~input.Read(keys);
            frames++;
            if (fast || input.FastForward) { frames = 0; clock.Restart(); return; }
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
