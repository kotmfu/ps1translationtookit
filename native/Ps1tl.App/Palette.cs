using Avalonia.Media;

namespace Ps1tl.App;

/// <summary>the app's colours (dark): one place, shared by every window part</summary>
static class Palette
{
    static IBrush B(string hex) => new SolidColorBrush(Color.Parse(hex));
    public static readonly IBrush Bg = B("#17151c");        // window
    public static readonly IBrush Surface = B("#211e28");   // grouped panels
    public static readonly IBrush Muted = B("#8a8494");     // captions, secondary text (≥4.5:1 on Bg and Surface)
    public static readonly IBrush Accent = B("#e0a050");    // running state, look-alike characters
    public static readonly IBrush Warn = B("#e06a5a");      // too wide, missing letters, disagreements
    public static readonly IBrush Ok = B("#3f7a52");        // verified
    public static readonly IBrush Info = B("#4f7fd0");      // confirmed by a person
    public static readonly IBrush Idle = B("#4a4554");      // not verified yet
}
