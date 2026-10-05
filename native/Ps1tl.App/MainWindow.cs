using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace Ps1tl.App;

/// <summary>one row of the line list</summary>
/// <summary>a row of the line list: a line (Index >= 0) or a scene header (Header set, Index -1)</summary>
sealed record LineItem(int Index, string Ja, string En, bool Auto, bool Picture, string Label = "", string? Header = null);

sealed class MainWindow : Window
{
    readonly Project p = new();
    static readonly FontFamily Mono = new("Cascadia Mono, Consolas, DejaVu Sans Mono, monospace");
    static readonly IBrush Muted = Palette.Muted, Accent = Palette.Accent, Warn = Palette.Warn;

    // top
    readonly TextBlock gameLbl = new() { VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold };
    readonly ProgressBar prog = new() { Width = 220, Height = 10, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock progLbl = new() { VerticalAlignment = VerticalAlignment.Center, Foreground = Muted };
    // list
    readonly ComboBox filter = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    readonly TextBox search = new() { PlaceholderText = "Search text or labels; paste a label from a diagnostic build to jump to it" };
    readonly CheckBox groupBox = new() { Content = "Group by scene" };
    readonly TextBlock countLbl = new() { Foreground = Muted };
    readonly ListBox list = new();
    readonly GlyphGrid glyphs;
    readonly TabControl tabs;
    // editor
    readonly Image jaImg = new() { Stretch = Stretch.None, HorizontalAlignment = HorizontalAlignment.Left };
    readonly TextBox jaBox = new() { PlaceholderText = "(Japanese not read yet)" };
    readonly TextBox enBox = new() { PlaceholderText = "English", AcceptsReturn = false };
    readonly Image enImg = new() { Stretch = Stretch.None, HorizontalAlignment = HorizontalAlignment.Left };
    readonly TextBlock meter = new() { Foreground = Muted };
    readonly TextBox notesBox = new() { PlaceholderText = "Context for translators, kept in the project" };
    readonly TextBlock lineInfo = new() { Foreground = Muted };
    // tools
    readonly ComboBox model = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    readonly TextBox apiKey = new() { PlaceholderText = "API key (optional: blank uses your Claude Code login)", PasswordChar = '•' };
    readonly TextBox glossary = new() { PlaceholderText = "Glossary: Nao = Nao ...", AcceptsReturn = true, Height = 70, TextWrapping = TextWrapping.Wrap };
    readonly NumericUpDown nLines = new() { Minimum = 1, Maximum = 100000, Value = 80, Increment = 20, FormatString = "0" };
    readonly Button trBtn = new() { Content = "Translate next untranslated lines" };
    readonly CheckBox redoBox = new() { Content = "Redo translated lines with their whole scene" };
    readonly Button ocrBtn = new() { Content = "Read Japanese (label all characters)" };
    readonly Button checkBtn = new() { Content = "Check doubtful characters with Claude" };
    readonly Button oneTextBtn = new() { Content = "By text" };
    readonly Button onePicBtn = new() { Content = "By picture" };
    readonly TextBlock oneHint = new() { Foreground = Muted, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    readonly Button picBtn = new() { Content = "Find text in pictures" };
    readonly TextBlock backendLbl = new() { Foreground = Muted, TextWrapping = TextWrapping.Wrap };
    readonly TextBlock statsLbl = new() { Foreground = Muted, TextWrapping = TextWrapping.Wrap };
    readonly TextBox outDir = new() { PlaceholderText = "output folder (default: next to the rom)" };
    readonly CheckBox labelsBox = new() { Content = "Diagnostic labels (each message shows its file:message id)" };
    readonly Button buildBtn = new() { Content = "Build patched disc + patch file", Classes = { "accent" } };
    readonly Button stopBtn = new() { Content = "Stop", IsEnabled = false };
    readonly ProgressBar busyBar = new() { IsIndeterminate = true, Height = 6, IsVisible = false };
    readonly TextBlock busyLbl = new() { Foreground = Accent, IsVisible = false, TextWrapping = TextWrapping.Wrap };
    readonly TextBox live = new() { IsReadOnly = true, FontFamily = Mono, FontSize = 11, TextWrapping = TextWrapping.Wrap, PlaceholderText = "Claude's output appears here while a job runs." };
    readonly TextBox log = new() { IsReadOnly = true, FontFamily = Mono, FontSize = 11, TextWrapping = TextWrapping.Wrap, Height = 150 };
    readonly Border liveBox;
    readonly StackPanel editorBody;
    readonly TextBlock emptyEditor = new()
    {
        Text = "Select a line on the left to edit its English.", Foreground = Palette.Muted,
        Margin = new Thickness(0, 24, 0, 24), HorizontalAlignment = HorizontalAlignment.Center,
    };

    List<int> idx = new();
    int? current;
    int seenLog;
    bool loading;
    readonly DispatcherTimer saveTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    readonly DispatcherTimer searchTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    public MainWindow(string? cue)
    {
        Title = "ps1tl — PS1 translation";
        Width = 1500; Height = 920;
        Background = Palette.Bg;

        FillFilter(null);
        foreach (var (t, v) in new[] { ("Opus (best quality)", "opus"), ("Sonnet (faster, uses less)", "sonnet"), ("Haiku (fastest, rougher)", "haiku") })
            model.Items.Add(new ComboBoxItem { Content = t, Tag = v });
        model.SelectedIndex = Math.Max(0, new[] { "opus", "sonnet", "haiku" }.ToList().IndexOf(Settings.Get("model", "opus")));
        glossary.Text = Settings.Get("glossary");
        outDir.Text = Settings.Get("out");
        RenderOptions.SetBitmapInterpolationMode(jaImg, Avalonia.Media.Imaging.BitmapInterpolationMode.None);
        RenderOptions.SetBitmapInterpolationMode(enImg, Avalonia.Media.Imaging.BitmapInterpolationMode.None);

        list.ItemTemplate = new FuncDataTemplate<LineItem>((it, _) =>
        {
            if (it == null) return new TextBlock();
            if (it.Header != null)
                return new TextBlock { Text = it.Header, FontWeight = FontWeight.SemiBold, Foreground = Accent, Margin = new Thickness(0, 8, 0, 2) };
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("96,*,*"), Margin = new Thickness(0, 1) };
            g.Children.Add(new TextBlock { Text = it.Label, Foreground = Muted, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            var ja = new TextBlock { Text = (it.Picture ? "▣ " : "") + it.Ja, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(ja, 1); g.Children.Add(ja);
            var en = new TextBlock { Text = it.Auto ? "= " + it.En : it.En, Foreground = it.Auto ? Muted : Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(8, 0, 0, 0) };
            Grid.SetColumn(en, 2); g.Children.Add(en);
            return g;
        });

        // --- layout ---
        var openBtn = new Button { Content = "Open rom…", Classes = { "accent" } };
        openBtn.Click += async (_, _) => await PickRom();
        var top = Row(8, openBtn, gameLbl, prog, progLbl);
        top.Margin = new Thickness(10, 8);

        glyphs = new GlyphGrid(p, changed => { Say(changed); Reload(); UpdateStats(); }, checkBtn);
        var listPane = new DockPanel { Margin = new Thickness(10, 0, 5, 10) };
        var listTop = new StackPanel { Spacing = 6, Margin = new Thickness(0, 0, 0, 6), Children = { filter, search, Row(12, groupBox, countLbl) } };
        DockPanel.SetDock(listTop, Dock.Top);
        listPane.Children.Add(listTop);
        listPane.Children.Add(list);

        var oneLbl = Caption("Translate this line");
        oneLbl.VerticalAlignment = VerticalAlignment.Center;
        editorBody = new StackPanel
        {
            Spacing = 14, IsVisible = false,
            Children =
            {
                lineInfo,
                Field("Japanese (game font)", Framed(jaImg)),
                Field("Japanese text (editable)", jaBox),
                Field("English (Enter = next line)", enBox),
                Field("Preview in the game font", Framed(enImg), meter),
                Field("Notes", notesBox),
                Field(null, Row(8, oneLbl, oneTextBtn, onePicBtn), oneHint),
            },
        };
        var editor = new Panel
        {
            Margin = new Thickness(5, 0, 5, 14),
            Children = { editorBody, emptyEditor },
        };

        var csvSave = new Button { Content = "Save translation file (CSV)" };
        var csvLoad = new Button { Content = "Load translation file (CSV)" };
        csvSave.Click += async (_, _) => await SaveCsv();
        csvLoad.Click += async (_, _) => await LoadCsv();
        var outPick = new Button { Content = "…" };
        outPick.Click += async (_, _) => await PickOut();
        // Claude panel under the editor: running status + clock in the header, streamed output fills the rest
        var liveTitle = Caption("Claude output (live)");
        liveTitle.VerticalAlignment = busyLbl.VerticalAlignment = VerticalAlignment.Center;
        var liveRow = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(stopBtn, Dock.Right);
        liveRow.Children.Add(stopBtn); liveRow.Children.Add(Row(10, liveTitle, busyLbl));
        var liveHead = new StackPanel { Spacing = 6, Margin = new Thickness(0, 0, 0, 8), Children = { liveRow, busyBar } };
        var liveDock = new DockPanel();
        DockPanel.SetDock(liveHead, Dock.Top);
        liveDock.Children.Add(liveHead); liveDock.Children.Add(live);
        liveBox = new Border { Child = liveDock, Padding = new Thickness(10), CornerRadius = new CornerRadius(6), Margin = new Thickness(5, 0, 5, 10),
                               Background = Palette.Surface };
        var tools = new StackPanel
        {
            Spacing = 12, Margin = new Thickness(5, 0, 10, 10),
            Children =
            {
                Group("Translate", Grid2(new TextBlock { Text = "Lines per run", VerticalAlignment = VerticalAlignment.Center }, nLines),
                      trBtn, redoBox, ocrBtn, picBtn, statsLbl),
                Group("Claude settings", backendLbl, model, apiKey, glossary),
                Group("Build", labelsBox, Grid2(outDir, outPick), buildBtn),
                Group("Translation file", csvSave, csvLoad),
            },
        };

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("5*,4*,360") };
        listPane.Margin = new Thickness(0, 6, 0, 0);
        tabs = new TabControl
        {
            Margin = new Thickness(10, 0, 5, 10),
            Items = { new TabItem { Header = "Lines", Content = listPane }, new TabItem { Header = "Characters", Content = glyphs.Pane } },
        };
        // SelectionChanged bubbles up from the lists inside the tabs; only react to the tab strip itself
        tabs.SelectionChanged += (_, e) => { if (e.Source == tabs && tabs.SelectedIndex == 1) glyphs.Refresh(); };
        body.Children.Add(tabs);
        // middle column: the line editor on top, the Claude panel takes the space below it
        var middle = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        middle.Children.Add(editor);
        Grid.SetRow(liveBox, 1); middle.Children.Add(liveBox);
        Grid.SetColumn(middle, 1); body.Children.Add(middle);
        // right column: tools scroll; the log stays docked below, always visible
        var status = new StackPanel { Spacing = 6, Margin = new Thickness(5, 4, 10, 10), Children = { Caption("Log"), log } };
        var right = new DockPanel();
        DockPanel.SetDock(status, Dock.Bottom);
        right.Children.Add(status);
        right.Children.Add(new ScrollViewer { Content = tools });
        Grid.SetColumn(right, 2); body.Children.Add(right);
        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top); root.Children.Add(body);
        Content = root;

        // --- behaviour ---
        filter.SelectionChanged += (_, _) => Reload();
        groupBox.IsChecked = Settings.Get("group", "1") == "1";
        groupBox.IsCheckedChanged += (_, _) => { Settings.Set("group", groupBox.IsChecked == true ? "1" : "0"); Reload(); };
        search.TextChanged += (_, _) => { searchTimer.Stop(); searchTimer.Start(); };
        searchTimer.Tick += (_, _) => { searchTimer.Stop(); Reload(); };
        list.SelectionChanged += (_, _) => { if (list.SelectedItem is LineItem { Header: null } it) Select(it.Index); };
        enBox.TextChanged += (_, _) => { if (!loading) { saveTimer.Stop(); saveTimer.Start(); Preview(); } };
        jaBox.TextChanged += (_, _) => { if (!loading) { saveTimer.Stop(); saveTimer.Start(); } };
        notesBox.TextChanged += (_, _) => { if (!loading) { saveTimer.Stop(); saveTimer.Start(); } };
        saveTimer.Tick += (_, _) => { saveTimer.Stop(); Commit(); };
        enBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;   // a line is a single row in game; Enter jumps to the next line
            Commit();
            int next = list.SelectedIndex + 1;
            while (next < list.ItemCount && (list.ItemsSource as List<LineItem>)?[next].Header != null) next++;   // skip scene headers
            if (next < list.ItemCount) { list.SelectedIndex = next; list.ScrollIntoView(next); enBox.Focus(); }
        };
        trBtn.Click += (_, _) => StartJob(() =>
        {
            Settings.Set("glossary", glossary.Text ?? ""); Settings.Set("model", Model);
            p.StartTranslate((int)(nLines.Value ?? 80), glossary.Text ?? "", Model, apiKey.Text ?? "", p.IsPictureFilter(FilterValue), redoBox.IsChecked == true);
        });
        redoBox.IsCheckedChanged += (_, _) => UpdateStats();
        ToolTip.SetTip(redoBox, "Sends whole scenes so sentences that run over two rows are translated together. Overwrites the English of lines translated row by row (Lines = how many); lines already done by scene are kept. A backup is made first.");
        ocrBtn.Click += (_, _) => StartJob(() => p.StartOcr(Model, apiKey.Text ?? ""));
        picBtn.Click += (_, _) => StartJob(() => p.StartReadPictures(Model, apiKey.Text ?? ""));
        checkBtn.Click += (_, _) => StartJob(() => p.StartVerifyCharacters((int)(nLines.Value ?? 80), Model, apiKey.Text ?? ""));
        oneTextBtn.Click += (_, _) => TranslateOne(true);
        onePicBtn.Click += (_, _) => TranslateOne(false);
        ToolTip.SetTip(checkBtn, "Sends lines that contain unverified characters to Claude as pictures (up to the Lines count in the Claude panel); its readings verify or correct those characters everywhere");
        buildBtn.Click += (_, _) => StartJob(() => { Settings.Set("out", outDir.Text ?? ""); p.StartBuild(outDir.Text ?? "", labelsBox.IsChecked == true); });
        stopBtn.Click += (_, _) => { p.Stop = true; Say("stopping after the requests already running…"); };
        new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => PollJob()).Start();
        Closing += (_, _) => Commit();

        UpdateStats();
        cue ??= Settings.Get("cue");
        if (cue.Length > 0 && File.Exists(cue)) Dispatcher.UIThread.Post(() => OpenRom(cue));
        else Say("Open a .cue file to start (Open rom…).");
    }

    string Model => (model.SelectedItem as ComboBoxItem)?.Tag as string ?? "opus";
    /// <summary>the app's generic filters, plus the open game's own (IGame.Filters) before the last one</summary>
    void FillFilter(IGame? game)
    {
        var keep = FilterValue;
        var items = new List<(string, string)> { ("All lines", "all"), ("Untranslated", "todo"), ("Translated", "done"), ("Too wide", "over") };
        items.AddRange((game?.Filters ?? []).Select(f => (f.Label, f.Key)));
        items.Add(("Too long in game (last build)", "toolong"));
        filter.Items.Clear();
        foreach (var (t, v) in items) filter.Items.Add(new ComboBoxItem { Content = t, Tag = v });
        filter.SelectedIndex = Math.Max(0, items.FindIndex(x => x.Item2 == keep));
    }

    string FilterValue => (filter.SelectedItem as ComboBoxItem)?.Tag as string ?? "all";

    static StackPanel Row(double spacing, params Control[] c)
    {
        var s = new StackPanel { Orientation = Orientation.Horizontal, Spacing = spacing };
        foreach (var x in c) s.Children.Add(x);
        return s;
    }

    static Grid Grid2(Control a, Control b)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        g.Children.Add(a); Grid.SetColumn(b, 1); b.Margin = new Thickness(4, 0, 0, 0); g.Children.Add(b);
        return g;
    }

    static TextBlock Caption(string t) => new() { Text = t, Foreground = Muted, FontSize = 12 };

    /// <summary>a caption with the controls it labels, kept tight together</summary>
    static StackPanel Field(string? caption, params Control[] c)
    {
        var s = new StackPanel { Spacing = 4 };
        if (caption != null) s.Children.Add(Caption(caption));
        foreach (var x in c) s.Children.Add(x);
        return s;
    }

    static Border Framed(Control c) => new()
    {
        Child = c, Background = Brushes.Black, Padding = new Thickness(6), MinHeight = 40, CornerRadius = new CornerRadius(4),
        HorizontalAlignment = HorizontalAlignment.Stretch,
    };

    static Border Group(string title, params Control[] children)
    {
        var s = new StackPanel { Spacing = 6 };
        s.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold });
        foreach (var c in children) { if (c is Button b) b.HorizontalAlignment = HorizontalAlignment.Stretch; s.Children.Add(c); }
        return new Border { Child = s, Padding = new Thickness(10), CornerRadius = new CornerRadius(6), Background = Palette.Surface };
    }

    static Bitmap Png(byte[] data) => new(new MemoryStream(data));

    void Say(string m)
    {
        log.Text = (log.Text ?? "") + m + "\n";
        log.CaretIndex = log.Text.Length;
    }

    // --- rom / project ---------------------------------------------------------------------------------------
    async Task PickRom()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open PS1 disc", AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("PS1 disc (.cue)") { Patterns = ["*.cue"] }],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) OpenRom(path);
    }

    void OpenRom(string cue)
    {
        Say($"opening {cue} …");
        try { p.Open(cue); }
        catch (Exception e) { Say($"error: {e.Message}"); return; }
        Settings.Set("cue", cue);
        gameLbl.Text = p.Script!.Game;
        FillFilter(p.Game);
        Say($"opened {p.Script.Game}: {p.Script.Lines.Count:N0} lines · project {p.ScriptPath}"); glyphs.Refresh();
        Reload(); UpdateStats();
    }

    void Reload()
    {
        if (!p.IsOpen) return;
        var keep = current;
        idx = p.Matches(FilterValue, search.Text ?? "");
        var shared = Tfile.SharedEn(p.Script!);
        LineItem Item(int i)
        {
            var l = p.Script!.Lines[i];
            var auto = l.En.Length == 0 ? Tfile.EffectiveEn(l, shared) : "";
            return new LineItem(i, l.Ja, l.En.Length > 0 ? l.En : auto, auto.Length > 0, l.IsImage, l.Refs.Count > 0 ? p.Label(l.Refs[0]) : "");
        }
        var items = new List<LineItem>();
        if (groupBox.IsChecked == true)
        {
            // scenes in disc order, their lines in play order; lines in no scene (pictures, unused) at the end
            var wanted = idx.ToHashSet();
            List<(string Label, List<int> Lines)> groups;
            try { groups = p.SceneGroups(); }
            catch (Exception e) { Say($"error reading scenes: {e.Message}"); groups = new(); }
            foreach (var (label, lines) in groups)
            {
                var hit = lines.Where(wanted.Contains).ToList();
                if (hit.Count == 0) continue;
                int done = hit.Count(i => Tfile.EffectiveEn(p.Script!.Lines[i], shared).Length > 0);
                items.Add(new LineItem(-1, "", "", false, false, Header: $"{label}  ·  {hit.Count} lines, {done} translated"));
                items.AddRange(hit.Select(Item));
                wanted.ExceptWith(hit);
            }
            var rest = idx.Where(wanted.Contains).ToList();
            if (rest.Count > 0 && items.Count > 0) items.Add(new LineItem(-1, "", "", false, false, Header: $"Other  ·  {rest.Count} lines"));
            items.AddRange(rest.Select(Item));
        }
        else items.AddRange(idx.Select(Item));
        list.ItemsSource = items;
        countLbl.Text = $"{idx.Count:N0} lines";
        int at = keep is int k ? items.FindIndex(x => x.Index == k) : -1;
        if (p.FindLabel(search.Text ?? "") != null) at = items.FindIndex(x => x.Index >= 0);
        if (at >= 0) { list.SelectedIndex = at; list.ScrollIntoView(at); }
    }

    void UpdateStats()
    {
        bool open = p.IsOpen, busy = p.Busy;
        foreach (var b in new[] { trBtn, ocrBtn, picBtn, buildBtn, checkBtn }) b.IsEnabled = open && !busy;
        oneTextBtn.IsEnabled = onePicBtn.IsEnabled = open && !busy && current != null;
        stopBtn.IsEnabled = busy;
        backendLbl.Text = Llm.Backend() switch
        {
            "api" => "Using: Anthropic API key",
            "cli" => "Using: your Claude Code login (no API account needed)",
            _ => "No Claude access: install Claude Code (and log in) or enter an API key",
        };
        if (!open) return;
        var s = p.Script!;
        int done = p.TranslatedCount();
        prog.Maximum = p.InUseCount; prog.Value = done;
        progLbl.Text = $"{done:N0} / {p.InUseCount:N0} translated";
        int pictures = s.Lines.Count(l => l.IsImage), unchecked_ = p.PicturesUnchecked;
        picBtn.IsVisible = p.HasPictures;
        statsLbl.Text = $"Japanese read: {s.Lines.Count(l => l.Ja.Length > 0):N0} lines · {s.Chars?.Count ?? 0:N0}/{s.Glyphs.Count:N0} characters labelled\n" +
                        (p.HasPictures ? $"Pictures with text: {pictures:N0}" + (unchecked_ > 0 ? $" · {unchecked_:N0} not checked yet" : " · all checked") : "");
        trBtn.Content = p.IsPictureFilter(FilterValue) ? "Translate untranslated picture text"
                      : redoBox.IsChecked == true ? $"Retranslate next lines by scene ({s.Lines.Count(l => l.Tl == "scene" ):N0} done)"
                      : "Translate next untranslated lines";
    }

    // --- editing --------------------------------------------------------------------------------------------
    void Select(int i)
    {
        Commit();
        current = i;
        editorBody.IsVisible = true; emptyEditor.IsVisible = false;
        var l = p.Script!.Lines[i];
        loading = true;
        jaBox.Text = l.Ja; enBox.Text = l.En; notesBox.Text = l.Notes ?? "";
        var auto = l.En.Length == 0 ? Tfile.EffectiveEn(l, Tfile.SharedEn(p.Script)) : "";
        enBox.PlaceholderText = auto.Length > 0 ? $"= {auto}  (same Japanese as another line)" : "English";
        loading = false;
        lineInfo.Text = $"line #{i} · used {l.Refs.Count}× · " + (l.IsImage ? $"picture {p.Script.Images![l.Image!].W}×{p.Script.Images[l.Image!].H}px · " : "")
                        + string.Join(", ", l.Refs.Take(6).Select(p.Label)) + (l.Refs.Count > 6 ? ", …" : "");
        try { jaImg.Source = Png(p.JapanesePng(i)); } catch (Exception e) { Say($"error: {e.Message}"); }
        oneHint.Text = l.IsImage ? "Picture: by picture sends the image, by text only its Japanese and the box size."
                     : p.LineTextSafe(i) ? "All characters confirmed: text is safe." : "Some characters not confirmed yet: by picture is safer.";
        oneTextBtn.IsEnabled = onePicBtn.IsEnabled = !p.Busy;
        Preview();
    }

    void Preview()
    {
        if (current is not int i) return;
        var l = p.Script!.Lines[i];
        var t = enBox.Text ?? "";
        var shown = t.Length > 0 ? t : Tfile.EffectiveEn(l, Tfile.SharedEn(p.Script));
        enImg.Source = shown.Length > 0 || l.IsImage ? Png(p.EnglishPng(shown, i)) : null;
        var (ew, jw, over) = p.Widths(l, shown);
        var missing = p.Font.Missing(shown.Replace("\n", ""));
        meter.Text = (l.IsImage ? (over ? "does not fit the picture: shorten it" : $"fits the picture ({jw}px wide)")
                                : $"{ew} / {p.Budget}px  (Japanese {jw}px)" + (over ? "  · too wide" : ""))
                     + (missing.Count > 0 ? $"  · not in font: {string.Join(' ', missing)}" : "");
        meter.Foreground = over || missing.Count > 0 ? Warn : Muted;
    }

    void Commit()
    {
        saveTimer.Stop();
        if (current is not int i || !p.IsOpen) return;
        var l = p.Script!.Lines[i];
        string ja = jaBox.Text ?? "", en = enBox.Text ?? "", notes = notesBox.Text ?? "";
        if (ja.Trim() == l.Ja && en.Trim() == l.En && notes.Trim() == (l.Notes ?? "")) return;
        p.SetLine(i, ja, en, notes);
        if (list.ItemsSource is List<LineItem> items && items.FindIndex(x => x.Index == i) is var at and >= 0)
        {
            items[at] = items[at] with { Ja = l.Ja, En = l.En, Auto = false };
            list.ItemsSource = null; list.ItemsSource = items; list.SelectedIndex = at;
        }
        UpdateStats();
    }

    // --- files ----------------------------------------------------------------------------------------------
    async Task SaveCsv()
    {
        if (!p.IsOpen) return;
        var f = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save translation file", SuggestedFileName = "translation.csv", DefaultExtension = "csv",
            FileTypeChoices = [new FilePickerFileType("CSV") { Patterns = ["*.csv"] }],
        });
        if (f?.TryGetLocalPath() is { } path) { Commit(); p.ExportCsv(path); Say($"saved {path}"); }
    }

    async Task LoadCsv()
    {
        if (!p.IsOpen) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Load translation file", FileTypeFilter = [new FilePickerFileType("CSV") { Patterns = ["*.csv"] }],
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;
        Commit();
        var (updated, unknown) = p.ImportCsv(path);
        Say($"{updated:N0} lines updated, {unknown:N0} unknown keys (a backup was made first)");
        Reload(); UpdateStats();
    }

    async Task PickOut()
    {
        var d = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Output folder" });
        if (d.Count > 0 && d[0].TryGetLocalPath() is { } path) outDir.Text = path;
    }

    /// <summary>small modal message with OK (and "Open folder" when a folder is given)</summary>
    /// <summary>list the lines the last build couldn't fit</summary>
    void ShowTooLong()
    {
        tabs.SelectedIndex = 0;
        search.Text = "";
        filter.SelectedIndex = filter.Items.Cast<ComboBoxItem>().ToList().FindIndex(i => (string?)i.Tag == "toolong");
    }

    void Popup(string title, string text, string? folder, (string Label, Action Do)? extra = null)
    {
        var dlg = new Window
        {
            Title = title, SizeToContent = SizeToContent.WidthAndHeight, MaxWidth = 640, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Background,
        };
        var ok = new Button { Content = "OK", Classes = { "accent" }, IsDefault = true, IsCancel = true };
        ok.Click += (_, _) => dlg.Close();
        var buttons = Row(8);
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        if (folder != null)
        {
            var open = new Button { Content = "Open folder" };
            open.Click += async (_, _) => { await Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(folder)); dlg.Close(); };
            buttons.Children.Add(open);
        }
        if (extra is var (label, act))
        {
            var b = new Button { Content = label };
            b.Click += (_, _) => { act(); dlg.Close(); };
            buttons.Children.Add(b);
        }
        buttons.Children.Add(ok);
        dlg.Content = new StackPanel
        {
            Spacing = 14, Margin = new Thickness(20),
            Children = { new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, buttons },
        };
        dlg.ShowDialog(this);
    }

    void TranslateOne(bool byText)
    {
        if (current is not int i) return;
        StartJob(() => { Settings.Set("glossary", glossary.Text ?? ""); p.StartTranslateOne(i, byText, glossary.Text ?? "", Model, apiKey.Text ?? ""); });
    }

    // --- jobs -----------------------------------------------------------------------------------------------
    void StartJob(Action start)
    {
        Commit();
        try { start(); }
        catch (Exception e) { Say($"error: {e.Message}"); return; }
        seenLog = 0;
        PollJob();
    }

    void PollJob()
    {
        var j = p.Job;
        if (j == null) return;
        List<string> fresh;
        lock (j.Log) fresh = j.Log.Skip(seenLog).ToList();
        foreach (var m in fresh) Say(m);
        seenLog += fresh.Count;
        bool busy = j.Running;
        busyBar.IsVisible = busyLbl.IsVisible = busy;
        if (busy)
        {
            var t = DateTime.Now - j.Started;
            busyLbl.Text = $"{char.ToUpper(j.Name[0]) + j.Name[1..]} running · {(int)t.TotalMinutes}:{t.Seconds:00}" + (p.Stop ? " · stopping after current requests" : "");
            var text = p.LiveText();
            if (text.Length == 0) text = "Waiting for Claude to start writing…";
            if (text != live.Text) { live.Text = text; live.CaretIndex = text.Length; }
            if (fresh.Count > 0) { UpdateStats(); }
        }
        else if (stopBtn.IsEnabled)   // just finished
        {
            if (j.Result is Project.BuildResult r)
            {
                if (r.Error != null) Say("error: " + r.Error);
                if (r.Patch != null) Say($"patch ready:\n  {r.Cue}\n  {r.Patch}");
                if (r.Report.FilesSkipped.Count > 0)
                    Say($"{r.Report.FilesSkipped.Count} kept in Japanese: " + string.Join("; ", r.Report.FilesSkipped.Values.Distinct().Take(3)));
                if (r.Patch != null)
                    Popup("Build finished", $"The patched disc is ready:\n{r.Cue}\n\nPatch file:\n{r.Patch}" +
                          (p.TooLong.Count > 0 ? $"\n\n{p.TooLong.Count} lines are still too long and will wrap in game." : ""),
                          Path.GetDirectoryName(r.Cue), p.TooLong.Count > 0 ? ("Show them", ShowTooLong) : null);
                else Popup("Build failed", r.Error ?? "see the log", null);
            }
            else if (j.Name == "build") Popup("Build failed", "see the log", null);   // the build threw
            // the job may have changed the open line: load the editor from it first, so reloading the list
            // (which re-selects and commits) does not write the editor's old text back over the job's result
            if (current is int c) { current = null; Select(c); }
            Reload(); UpdateStats(); glyphs.Refresh();
        }
    }
}
