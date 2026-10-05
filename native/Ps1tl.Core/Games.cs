namespace Ps1tl;

public sealed record InsertReport(int FilesChanged, Dictionary<string, string> FilesSkipped, int LinesWithEnglish, List<string> TooWide);

/// <summary>a dialogue file's rows in order (line key, continues on the next row of the same page)</summary>
public sealed record SceneRow(string Key, bool More);
public sealed record Scene(string Path, List<SceneRow> Rows);

/// <summary>a game's own entry in the app's line filter (Key is what Project.Matches receives)</summary>
public sealed record LineFilter(string Key, string Label, Func<Line, bool> Match);

/// <summary>A supported game: finds its text on the disc and writes English back.</summary>
public interface IGame
{
    string[] Serials { get; }
    string Name { get; }
    /// <summary>bump when Extract() finds more text; open projects merge the new lines in</summary>
    int ExtractorVersion { get; }
    /// <summary>English width (px) a line may take before it is flagged too long</summary>
    int Budget { get; }
    /// <summary>how much English fits, for the translation prompt</summary>
    string BoxRule { get; }
    Script Extract(Disc disc);
    /// <summary>-> ({iso path: new file bytes} for changed files, report). labels: diagnostic build, every message
    /// shows its own ref instead of English.</summary>
    (Dictionary<string, byte[]> Repl, InsertReport Report) Insert(Disc disc, Script script, Font font, bool labels);
    /// <summary>{path: bytes} of every file holding pictures with text (empty if the game has none)</summary>
    Dictionary<string, byte[]> ImageFiles(Disc disc);
    /// <summary>-> (palette indices [h, w], palette) of picture i in an ImageFiles file</summary>
    (byte[,] Idx, ushort[] Pal) ImageData(byte[] file, int i);
    /// <summary>picture i redrawn with idx (same size) -> new file bytes</summary>
    byte[] PutImage(byte[] file, int i, byte[,] idx);
    List<Scene> Scenes(Disc disc);
    /// <summary>what the diagnostic build shows in game for a ref ("file:message") or a scene path; the editor shows
    /// and searches these</summary>
    string Label(string reference) => reference;
    /// <summary>extra line filters that only make sense for this game (the app's own ones are generic)</summary>
    IReadOnlyList<LineFilter> Filters => [];
    /// <summary>(Japanese, English) text -> PNG in the game's own font, for games without a glyph table; null = the
    /// app's own previews</summary>
    (Func<string, byte[]> Ja, Func<string, byte[]> En)? Previews(Disc disc) => null;
}

public static class Games
{
    public static readonly IGame[] All = [new YuuyamiGame(), new Gunparade()];
    public static IGame? BySerial(string? serial) => All.FirstOrDefault(g => g.Serials.Contains(serial));
    public static IGame Of(Script s) => All.First(g => g.Name == s.Game);
    public static string Supported => string.Join("; ", All.Select(g => $"{g.Name} ({string.Join(", ", g.Serials)})"));
}

sealed class YuuyamiGame : IGame
{
    public string[] Serials => Yuuyami.Serials;
    public string Name => Yuuyami.Name;
    public int ExtractorVersion => Yuuyami.ExtractorVersion;
    public int Budget => Yuuyami.RowMax;   // the text box (288px, see Yuuyami.BoxWidth) with a little margin
    public string BoxRule => "Each row must fit a text box 288 pixels wide: about 40 letters at most.";
    public Script Extract(Disc disc) => Yuuyami.Extract(disc);
    public (Dictionary<string, byte[]> Repl, InsertReport Report) Insert(Disc disc, Script script, Font font, bool labels) => Yuuyami.Insert(disc, script, font, labels);
    public Dictionary<string, byte[]> ImageFiles(Disc disc) => Yuuyami.ImageFiles(disc);
    public (byte[,] Idx, ushort[] Pal) ImageData(byte[] file, int i) => Yuuyami.ImageData(file, i);
    public byte[] PutImage(byte[] file, int i, byte[,] idx) => Yuuyami.PutImage(file, i, idx);
    public List<Scene> Scenes(Disc disc) => Yuuyami.Scenes(disc);
    public IReadOnlyList<LineFilter> Filters => [new("pictures", "Menus & notebook (pictures)", l => l.IsImage)];
    /// <summary>'/008/137/018:6' -> '8/137/18:6', as Insert writes it in game</summary>
    public string Label(string reference)
    {
        int c = reference.LastIndexOf(':');
        return c < 0 ? Flb.Short(reference) : $"{Flb.Short(reference[..c])}{reference[c..]}";
    }
}
