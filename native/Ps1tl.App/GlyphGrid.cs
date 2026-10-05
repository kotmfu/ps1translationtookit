using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Ps1tl.App;

/// <summary>
/// The character grid: every character shape of the game's font with the character the app reads it as.
/// Most doubtful first (Claude's line readings disagree, then unverified look-alikes, ...). Fixing or confirming
/// one tile fixes that character in every line that uses it.
/// </summary>
sealed class GlyphGrid
{
    const int MaxTiles = 600;
    static readonly IBrush Muted = Palette.Muted;
    static readonly Dictionary<GlyphStatus, IBrush> StatusBrush = new()
    {
        [GlyphStatus.Disagree] = Palette.Warn,
        [GlyphStatus.Unverified] = Palette.Idle,
        [GlyphStatus.Verified] = Palette.Ok,
        [GlyphStatus.Confirmed] = Palette.Info,
    };
    static readonly IBrush LookAlike = Palette.Accent;

    readonly Project p;
    readonly Action<string> changed;
    readonly Dictionary<string, Bitmap> cache = new();
    public Control Pane { get; }

    readonly ComboBox filter = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    readonly TextBox search = new() { PlaceholderText = "Find a character (e.g. メ)" };
    readonly TextBlock count = new() { Foreground = Muted, TextWrapping = TextWrapping.Wrap };
    readonly ListBox tiles = new();
    readonly Image big = new() { Width = 96, Height = 96, Stretch = Stretch.None };
    readonly TextBox reading = new() { FontSize = 22, Width = 70, HorizontalContentAlignment = HorizontalAlignment.Center };
    readonly TextBlock info = new() { Foreground = Muted, TextWrapping = TextWrapping.Wrap };
    readonly StackPanel samples = new() { Spacing = 2 };
    readonly Border detail;
    List<GlyphGroup> all = new();
    GlyphGroup? cur;

    record Tile(GlyphGroup G, Bitmap Img);

    public GlyphGrid(Project project, Action<string> onChanged, Control? action = null)
    {
        p = project; changed = onChanged;
        foreach (var t in new[] { "Doubtful first", "Claude disagrees", "Not verified", "Verified by Claude's readings", "Confirmed by you", "All" })
            filter.Items.Add(new ComboBoxItem { Content = t });
        filter.SelectedIndex = 0;
        RenderOptions.SetBitmapInterpolationMode(big, BitmapInterpolationMode.None);

        tiles.ItemsPanel = new FuncTemplate<Panel?>(() => new WrapPanel());
        tiles.ItemTemplate = new FuncDataTemplate<Tile>((t, _) =>
        {
            if (t == null) return new Border();
            var img = new Image { Source = t.Img, Width = 32, Height = 32 };
            RenderOptions.SetBitmapInterpolationMode(img, BitmapInterpolationMode.None);
            return new Border
            {
                BorderBrush = t.G.Status == GlyphStatus.Unverified && t.G.Confusable ? LookAlike : StatusBrush[t.G.Status],
                BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(4), Padding = new Thickness(3), Width = 52,
                Child = new StackPanel
                {
                    Spacing = 1,
                    Children = { img, new TextBlock { Text = t.G.Char.Length > 0 ? t.G.Char : "?", HorizontalAlignment = HorizontalAlignment.Center, FontSize = 15 } },
                },
            };
        });

        var save = new Button { Content = "Save (Enter)" };
        var ok = new Button { Content = "Correct, next" };
        save.Click += (_, _) => Commit(reading.Text ?? "");
        ok.Click += (_, _) => Commit(cur?.Char ?? "");
        reading.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Commit(reading.Text ?? ""); } };
        ToolTip.SetTip(ok, "The reading is right: mark it confirmed and go to the next character");

        var bigBox = new Border { Child = big, Background = Brushes.Black, Padding = new Thickness(4), CornerRadius = new CornerRadius(4), VerticalAlignment = VerticalAlignment.Top };
        var right = new StackPanel
        {
            Spacing = 6, Margin = new Thickness(10, 0, 0, 0),
            Children =
            {
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new TextBlock { Text = "Reads as", VerticalAlignment = VerticalAlignment.Center }, reading, save, ok } },
                info, samples,
            },
        };
        var dg = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        dg.Children.Add(bigBox); Grid.SetColumn(right, 1); dg.Children.Add(right);
        detail = new Border { Child = dg, Padding = new Thickness(10), CornerRadius = new CornerRadius(6), IsVisible = false,
                              Background = Palette.Surface, Margin = new Thickness(0, 0, 0, 6) };

        // border colours of the tiles, as swatches that wrap whole
        var legend = new WrapPanel();
        foreach (var (brush, text) in new[] { (Palette.Warn, "Claude's readings disagree"), (Palette.Accent, "look-alike, check it"),
                                              (Palette.Ok, "verified"), (Palette.Info, "confirmed by you") })
            legend.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 5, Margin = new Thickness(0, 0, 14, 2),
                Children =
                {
                    new Border { Width = 11, Height = 11, BorderBrush = brush, BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = text, Foreground = Muted, FontSize = 12 },
                },
            });
        var top = new StackPanel { Spacing = 6, Margin = new Thickness(0, 6, 0, 6), Children = { filter, search, count, legend, detail } };
        if (action != null) { action.HorizontalAlignment = HorizontalAlignment.Stretch; top.Children.Insert(0, action); }
        var pane = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        pane.Children.Add(top); pane.Children.Add(tiles);
        Pane = pane;

        filter.SelectionChanged += (_, _) => Show();
        search.TextChanged += (_, _) => Show();
        tiles.SelectionChanged += (_, _) => { if (tiles.SelectedItem is Tile t) Select(t.G); };
    }

    /// <summary>recompute statuses (after translations, edits, or opening another rom)</summary>
    public void Refresh()
    {
        if (!p.IsOpen) return;
        all = p.GlyphReport();
        Show();
    }

    Bitmap Img(string gid, int scale)
    {
        var k = $"{gid}:{scale}";
        return cache.TryGetValue(k, out var b) ? b : cache[k] = new Bitmap(new MemoryStream(p.GlyphPng(gid, scale)));
    }

    void Show()
    {
        var q = (search.Text ?? "").Trim();
        IEnumerable<GlyphGroup> sel = filter.SelectedIndex switch
        {
            1 => all.Where(g => g.Status == GlyphStatus.Disagree),
            2 => all.Where(g => g.Status == GlyphStatus.Unverified),
            3 => all.Where(g => g.Status == GlyphStatus.Verified),
            4 => all.Where(g => g.Status == GlyphStatus.Confirmed),
            _ => all,
        };
        if (q.Length > 0) sel = sel.Where(g => g.Char == q || g.Votes.ContainsKey(q));
        var list = sel.ToList();
        var shown = list.Take(MaxTiles).Select(g => new Tile(g, Img(g.Gids[0], 2))).ToList();
        tiles.ItemsSource = shown;
        int dis = all.Count(g => g.Status == GlyphStatus.Disagree), unv = all.Count(g => g.Status == GlyphStatus.Unverified);
        count.Text = $"{list.Count:N0} characters" + (list.Count > MaxTiles ? $" (first {MaxTiles} shown; search to narrow)" : "") +
                     $"  ·  {dis} disagree · {unv:N0} not verified · {all.Count(g => g.Status == GlyphStatus.Verified):N0} verified · {all.Count(g => g.Status == GlyphStatus.Confirmed):N0} confirmed";
        if (shown.Count > 0) tiles.SelectedIndex = 0; else detail.IsVisible = false;
    }

    void Select(GlyphGroup g)
    {
        cur = g;
        detail.IsVisible = true;
        big.Source = Img(g.Gids[0], 6);
        reading.Text = g.Char;
        var votes = g.Votes.Count == 0 ? "no picture readings yet"
            : "Claude read it in context as: " + string.Join(", ", g.Votes.OrderByDescending(v => v.Value).Select(v => $"{v.Key} ×{v.Value}"));
        info.Text = $"{g.Status switch { GlyphStatus.Disagree => "Claude's readings disagree", GlyphStatus.Unverified => "not verified", GlyphStatus.Verified => "verified by Claude's readings", _ => "confirmed by you" }}" +
                    $" · used {g.Uses:N0}× · {g.Gids.Count} copies\n{votes}";
        samples.Children.Clear();
        foreach (var i in p.LinesUsing(g))
            samples.Children.Add(new TextBlock { Text = $"#{i}  {p.Script!.Lines[i].Ja}", TextTrimming = TextTrimming.CharacterEllipsis });
        reading.Focus();
        reading.SelectAll();
    }

    void Commit(string c)
    {
        if (cur == null) return;
        c = c.Trim();
        if (c.EnumerateRunes().Count() != 1) { info.Text = "Type exactly one character."; return; }
        int lines = p.SetGlyph(cur, c);
        changed(c == cur.Char ? $"confirmed {c}" : $"{cur.Char} → {c}: {lines:N0} lines' Japanese updated");
        // update this tile in place and move on; the list order stays put until the filter changes
        var updated = cur with { Char = c, Status = GlyphStatus.Confirmed };
        int at = all.IndexOf(cur);
        if (at >= 0) all[at] = updated;
        if (tiles.ItemsSource is List<Tile> items)
        {
            int k = tiles.SelectedIndex;
            if (k >= 0 && k < items.Count)
            {
                items[k] = items[k] with { G = updated };
                tiles.ItemsSource = null; tiles.ItemsSource = items;
                tiles.SelectedIndex = Math.Min(k + 1, items.Count - 1);
                tiles.ScrollIntoView(tiles.SelectedIndex);
            }
        }
    }
}
