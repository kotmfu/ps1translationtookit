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
sealed record LineItem(int Index, string Ja, string En, bool Auto, bool Picture);

sealed class MainWindow : Window
{
    readonly Project p = new();
    static readonly FontFamily Mono = new("Cascadia Mono, Consolas, DejaVu Sans Mono, monospace");
    static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#8a8494"));
    static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#e0a050"));
    static readonly IBrush Warn = new SolidColorBrush(Color.Parse("#e06a5a"));

    // top
    readonly TextBlock gameLbl = new() { VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold };
    readonly ProgressBar prog = new() { Width = 220, Height = 10, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock progLbl = new() { VerticalAlignment = VerticalAlignment.Center, Foreground = Muted };
    // list
    readonly ComboBox filter = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    readonly TextBox search = new() { PlaceholderText = "Search, or paste a label like 8/137/18:6" };
    readonly TextBlock countLbl = new() { Foreground = Muted };
    readonly ListBox list = new();
    // editor
    readonly Image jaImg = new() { Stretch = Stretch.None, HorizontalAlignment = HorizontalAlignment.Left };
    readonly TextBox jaBox = new() { PlaceholderText = "(Japanese not read yet)" };
    readonly TextBox enBox = new() { PlaceholderText = "English", AcceptsReturn = false };
    readonly Image enImg = new() { Stretch = Stretch.None, HorizontalAlignment = HorizontalAlignment.Left };
    readonly TextBlock meter = new() { Foreground = Muted };
    readonly TextBox notesBox = new() { PlaceholderText = "notes" };
    readonly TextBlock lineInfo = new() { Foreground = Muted };
    // tools
    readonly ComboBox model = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    readonly TextBox apiKey = new() { PlaceholderText = "API key (optional: blank uses your Claude Code login)", PasswordChar = '•' };
    readonly TextBox glossary = new() { PlaceholderText = "Glossary: Nao = Nao ...", AcceptsReturn = true, Height = 70, TextWrapping = TextWrapping.Wrap };
    readonly NumericUpDown nLines = new() { Minimum = 1, Maximum = 100000, Value = 80, Increment = 20, FormatString = "0" };
    readonly Button trBtn = new() { Content = "Translate next untranslated lines" };
    readonly Button ocrBtn = new() { Content = "Read Japanese (label all glyphs)" };
    readonly Button picBtn = new() { Content = "Find menu text (pictures)" };
    readonly TextBlock backendLbl = new() { Foreground = Muted, TextWrapping = TextWrapping.Wrap };
    readonly TextBlock statsLbl = new() { Foreground = Muted, TextWrapping = TextWrapping.Wrap };
    readonly TextBox outDir = new() { PlaceholderText = "output folder (default: next to the rom)" };
    readonly CheckBox labelsBox = new() { Content = "Diagnostic labels (each message shows its file:message id)" };
    readonly Button buildBtn = new() { Content = "Build patched disc + patch file", Classes = { "accent" } };
    readonly Button stopBtn = new() { Content = "Stop", IsEnabled = false };
    readonly ProgressBar busyBar = new() { IsIndeterminate = true, Height = 6, IsVisible = false };
    readonly TextBlock busyLbl = new() { Foreground = Accent, IsVisible = false, TextWrapping = TextWrapping.Wrap };
    readonly TextBox live = new() { IsReadOnly = true, FontFamily = Mono, FontSize = 11, TextWrapping = TextWrapping.Wrap, Height = 180 };
    readonly TextBox log = new() { IsReadOnly = true, FontFamily = Mono, FontSize = 11, TextWrapping = TextWrapping.Wrap, Height = 140 };
    readonly Border liveBox;

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
        Background = new SolidColorBrush(Color.Parse("#17151c"));

        foreach (var (t, v) in new[] { ("All lines", "all"), ("Untranslated", "todo"), ("Translated", "done"), ("Too wide", "over"), ("Menus & notebook (pictures)", "pictures") })
            filter.Items.Add(new ComboBoxItem { Content = t, Tag = v });
        filter.SelectedIndex = 0;
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
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("52,*,*"), Margin = new Thickness(0, 1) };
            g.Children.Add(new TextBlock { Text = $"#{it.Index}", Foreground = Muted, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
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

        var listPane = new DockPanel { Margin = new Thickness(10, 0, 5, 10) };
        var listTop = new StackPanel { Spacing = 6, Margin = new Thickness(0, 0, 0, 6), Children = { filter, search, countLbl } };
        DockPanel.SetDock(listTop, Dock.Top);
        listPane.Children.Add(listTop);
        listPane.Children.Add(list);

        var editor = new StackPanel
        {
            Spacing = 8, Margin = new Thickness(5, 0, 5, 10),
            Children =
            {
                lineInfo,
                Caption("Japanese (game font)"), Framed(jaImg),
                Caption("Japanese text (editable)"), jaBox,
                Caption("English  (Enter = next line)"), enBox,
                Caption("Preview in the game font"), Framed(enImg), meter,
                notesBox,
            },
        };

        var csvSave = new Button { Content = "Save translation file (CSV)" };
        var csvLoad = new Button { Content = "Load translation file (CSV)" };
        csvSave.Click += async (_, _) => await SaveCsv();
        csvLoad.Click += async (_, _) => await LoadCsv();
        var outPick = new Button { Content = "…" };
        outPick.Click += async (_, _) => await PickOut();
        liveBox = new Border { IsVisible = false, Child = new StackPanel { Spacing = 4, Children = { Caption("Claude output (live)"), live } } };
        var tools = new StackPanel
        {
            Spacing = 8, Margin = new Thickness(5, 0, 10, 10),
            Children =
            {
                Group("Translation file", csvSave, csvLoad),
                Group("Claude", backendLbl, model, apiKey, glossary, Row(6, new TextBlock { Text = "Lines", VerticalAlignment = VerticalAlignment.Center }, nLines),
                      trBtn, ocrBtn, picBtn, statsLbl),
                Group("Build", labelsBox, Grid2(outDir, outPick), buildBtn, stopBtn),
            },
        };

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("5*,4*,360") };
        body.Children.Add(listPane);
        var ed = new ScrollViewer { Content = editor }; Grid.SetColumn(ed, 1); body.Children.Add(ed);
        // right column: tools scroll; progress, Claude's live output and the log stay docked below, always visible
        var status = new StackPanel { Spacing = 6, Margin = new Thickness(5, 4, 10, 10), Children = { busyBar, busyLbl, liveBox, Caption("Log"), log } };
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
        search.TextChanged += (_, _) => { searchTimer.Stop(); searchTimer.Start(); };
        searchTimer.Tick += (_, _) => { searchTimer.Stop(); Reload(); };
        list.SelectionChanged += (_, _) => { if (list.SelectedItem is LineItem it) Select(it.Index); };
        enBox.TextChanged += (_, _) => { if (!loading) { saveTimer.Stop(); saveTimer.Start(); Preview(); } };
        jaBox.TextChanged += (_, _) => { if (!loading) { saveTimer.Stop(); saveTimer.Start(); } };
        notesBox.TextChanged += (_, _) => { if (!loading) { saveTimer.Stop(); saveTimer.Start(); } };
        saveTimer.Tick += (_, _) => { saveTimer.Stop(); Commit(); };
        enBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;   // a line is a single row in game; Enter jumps to the next line
            Commit();
            if (list.SelectedIndex + 1 < list.ItemCount) { list.SelectedIndex++; list.ScrollIntoView(list.SelectedIndex); enBox.Focus(); }
        };
        trBtn.Click += (_, _) => StartJob(() =>
        {
            Settings.Set("glossary", glossary.Text ?? ""); Settings.Set("model", Model);
            p.StartTranslate((int)(nLines.Value ?? 80), glossary.Text ?? "", Model, apiKey.Text ?? "", FilterValue == "pictures");
        });
        ocrBtn.Click += (_, _) => StartJob(() => p.StartOcr(Model, apiKey.Text ?? ""));
        picBtn.Click += (_, _) => StartJob(() => p.StartReadPictures(Model, apiKey.Text ?? ""));
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
        return new Border { Child = s, Padding = new Thickness(10), CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(Color.Parse("#211e28")) };
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
        Say($"opened {p.Script.Game}: {p.Script.Lines.Count:N0} lines · project {p.ScriptPath}");
        Reload(); UpdateStats();
    }

    void Reload()
    {
        if (!p.IsOpen) return;
        var keep = current;
        idx = p.Matches(FilterValue, search.Text ?? "");
        var shared = Tfile.SharedEn(p.Script!);
        var items = idx.Select(i =>
        {
            var l = p.Script!.Lines[i];
            var auto = l.En.Length == 0 ? Tfile.EffectiveEn(l, shared) : "";
            return new LineItem(i, l.Ja, l.En.Length > 0 ? l.En : auto, auto.Length > 0, l.IsImage);
        }).ToList();
        list.ItemsSource = items;
        countLbl.Text = $"{items.Count:N0} lines";
        int at = keep is int k ? idx.IndexOf(k) : -1;
        if (p.FindLabel(search.Text ?? "") != null && items.Count > 0) at = 0;
        if (at >= 0) { list.SelectedIndex = at; list.ScrollIntoView(at); }
    }

    void UpdateStats()
    {
        bool open = p.IsOpen, busy = p.Busy;
        foreach (var b in new[] { trBtn, ocrBtn, picBtn, buildBtn }) b.IsEnabled = open && !busy;
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
        prog.Maximum = s.Lines.Count; prog.Value = done;
        progLbl.Text = $"{done:N0} / {s.Lines.Count:N0} translated";
        int pictures = s.Lines.Count(l => l.IsImage), unchecked_ = p.PicturesUnchecked;
        statsLbl.Text = $"Japanese read: {s.Lines.Count(l => l.Ja.Length > 0):N0} lines · {s.Chars?.Count ?? 0:N0}/{s.Glyphs.Count:N0} glyphs\n" +
                        $"Menu pictures with text: {pictures:N0}" + (unchecked_ > 0 ? $" · {unchecked_:N0} not checked yet" : " · all checked");
        trBtn.Content = FilterValue == "pictures" ? "Translate untranslated menu text" : "Translate next untranslated lines";
    }

    // --- editing --------------------------------------------------------------------------------------------
    void Select(int i)
    {
        Commit();
        current = i;
        var l = p.Script!.Lines[i];
        loading = true;
        jaBox.Text = l.Ja; enBox.Text = l.En; notesBox.Text = l.Notes ?? "";
        var auto = l.En.Length == 0 ? Tfile.EffectiveEn(l, Tfile.SharedEn(p.Script)) : "";
        enBox.PlaceholderText = auto.Length > 0 ? $"= {auto}  (same Japanese as another line)" : "English";
        loading = false;
        lineInfo.Text = $"line #{i} · used {l.Refs.Count}× · " + (l.IsImage ? $"picture {p.Script.Images![l.Image!].W}×{p.Script.Images[l.Image!].H}px" : string.Join(", ", l.Refs.Take(3)));
        try { jaImg.Source = Png(p.JapanesePng(i)); } catch (Exception e) { Say($"error: {e.Message}"); }
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
        int at = idx.IndexOf(i);
        if (at >= 0 && list.ItemsSource is List<LineItem> items)
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
        busyBar.IsVisible = busyLbl.IsVisible = liveBox.IsVisible = busy;
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
            }
            Reload(); UpdateStats();
            if (current is int c) Select(c);
        }
    }
}
