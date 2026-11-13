using VideoConverter.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Controls.Primitives;
using System.Diagnostics;
using System.Reflection;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;

namespace VideoConverter;

/// <summary>Owns GUI job state, source-dependent choices and the responsive WinUI layout.</summary>
/// <remarks>Core performs final validation and encoding; this class previews those constraints before starting a job.</remarks>
public sealed class MainWindow : Window
{
    private static readonly JsonElement encodingConfig = LoadEncodingConfig();
    private readonly Grid root;
    private readonly ViewLocalization viewLocalization = new();
    private readonly Dictionary<string, object> controls = new();
    private readonly List<(ComboBoxItem Item, string Original)> choiceLabels = new();
    private readonly Dictionary<string, RadioMenuFlyoutItem> languageItems = new();
    private readonly UserSettings settings = UserSettings.Load();
    private readonly Appearance appearance;
    private ConversionClient? activeJob;
    // Selection changes also fire during programmatic updates. The updating
    // guard prevents recursive Refresh calls; default flags preserve explicit
    // user audio/depth choices until the purpose or source changes.
    private bool updating, probing, closing, dialogOpen, audioUsesDefault = true, depthUsesDefault = true;
    // Ignore delayed probe events after the selected source/window lifecycle changes.
    private int generation;
    private string ffmpeg = "", ffprobe = "", automaticOutput = "", completedOutput = "";
    private JsonElement? media;
    private readonly Stopwatch clock = new();
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer clockTimer;
    private double encodedSeconds, encodedFrames, conversionSpeed, currentFps;
    private bool completed;
    private readonly List<ConversionClient> probes = new();
    private TextBox Input => Control<TextBox>("InputPath");
    private TextBox Output => Control<TextBox>("Destination");
    private ComboBox Purpose => Control<ComboBox>("Purpose");
    private ComboBox Codec => Control<ComboBox>("Codec");
    private ComboBox Variant => Control<ComboBox>("Variant");
    private ComboBox Container => Control<ComboBox>("Container");
    private RadioButtons Range => Control<RadioButtons>("OutputRange");
    private ComboBox Transform => Control<ComboBox>("Transform");
    private ComboBox Profile => Control<ComboBox>("Profile");
    private ComboBox InputKind => Control<ComboBox>("InputKind");
    private ComboBox Sampling => Control<ComboBox>("Sampling");
    private ComboBox BitDepth => Control<ComboBox>("BitDepth");
    private ComboBox ColorRange => Control<ComboBox>("ColorRange");
    private ComboBox Audio => Control<ComboBox>("Audio");
    private string Kind => Value(InputKind) == "auto" ? MediaText("kind", "unknown") : Value(InputKind);
    private bool Hdr => Range.SelectedIndex == 1;
    private string previousSuffix = ".mp4";
    private string Suffix => Value(Container) switch { "mp4" => ".mp4", "mkv" => ".mkv", _ => Value(Purpose) == "archive" || Value(Audio) == "flac" ? ".mkv" : ".mp4" };
    private int Depth => int.TryParse(Value(BitDepth), out var depth) ? depth : 8;
    private int SourceDepth => media is { } item && item.TryGetProperty("source_depth", out var value) ? value.GetInt32() : 8;

    public MainWindow()
    {
        Title = L.T("映码 · LumaRecode");
        // Compiled XBF avoids runtime XML parsing and reflection over controls.
        // Appearance selects the appropriate native Mica host once the view exists.
        root = new MainView();
        Content = root;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(Control<Grid>("TitleDrag"));
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "LumaRecode.ico"));
        var dpi = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96d;
        var work = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary).WorkArea;
        var width = (int)Math.Min(1120 * dpi, work.Width - 64 * dpi);
        var height = (int)Math.Min(900 * dpi, work.Height - 64 * dpi);
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(work.X + (work.Width - width) / 2,
            work.Y + (work.Height - height) / 2, width, height));
        clockTimer = DispatcherQueue.CreateTimer();
        clockTimer.Interval = TimeSpan.FromSeconds(1);
        clockTimer.Tick += (_, _) => UpdateTransferStats();
        FillChoices();
        viewLocalization.Apply(root);
        UiTypography.Attach(root);
        appearance = new Appearance(root, this, settings);
        BuildLanguageMenu();
        WireEvents();
        ResolveTools();
        Refresh();
        root.Loaded += async (_, _) =>
        {
            dialogOpen = true;
            try
            {
                if (!await LegalNotices.RequireAcceptanceAsync(root, settings, Relocalize))
                {
                    Close();
                    return;
                }
            }
            finally { dialogOpen = false; }
            Reflow(root.ActualWidth);
            if (string.IsNullOrEmpty(ffmpeg) || string.IsNullOrEmpty(ffprobe))
                await ConfigureToolsAsync();
            var args = Environment.GetCommandLineArgs();
            if (args.Length > 1 && File.Exists(args[1]))
            {
                Input.Text = args[1];
                await InspectAsync();
            }
        };
        AppWindow.Closing += OnClosing;
    }

    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    private T Control<T>(string name) where T : class
    {
        if (!controls.TryGetValue(name, out var control))
            controls[name] = control = root.FindName(name);
        return (T)control;
    }
    private static string Value(ComboBox combo) => (combo.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
    private static void Select(ComboBox combo, string? key)
    {
        var item = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == (key ?? ""));
        combo.SelectedItem = item ?? combo.Items[0];
    }
    private void Choices(ComboBox combo, params (string key, string name)[] choices)
    {
        foreach (var (key, name) in choices)
        {
            var item = new ComboBoxItem { Tag = key, Content = name };
            combo.Items.Add(item);
            choiceLabels.Add((item, name));
        }
        combo.SelectedIndex = 0;
    }
    private void BuildLanguageMenu()
    {
        var menu = new MenuFlyout
        {
            Placement = FlyoutPlacementMode.BottomEdgeAlignedRight,
            ShouldConstrainToRootBounds = false
        };
        foreach (var (key, name) in new[] { ("system", L.T("跟随系统")) }.Concat(LanguagePolicy.Choices))
        {
            var code = key == "system" ? L.Code : key;
            var item = new RadioMenuFlyoutItem
            {
                Text = name,
                Tag = key,
                GroupName = "AppLanguage",
                IsChecked = settings.Language == key,
                FontFamily = UiTypography.Family(code),
                Language = LanguagePolicy.XamlLanguage(code)
            };
            item.Click += (_, _) =>
            {
                settings.Language = key;
                settings.Save();
                L.Configure(key);
                Relocalize();
            };
            menu.Items.Add(item);
            languageItems[key] = item;
        }
        Control<DropDownButton>("LanguageButton").Flyout = menu;
        UpdateLanguageMenu();
    }
    private void UpdateLanguageMenu()
    {
        foreach (var (key, item) in languageItems)
            item.IsChecked = key == settings.Language;
        languageItems["system"].Text = L.T("跟随系统");
        languageItems["system"].FontFamily = UiTypography.Family(L.Code);
        languageItems["system"].Language = LanguagePolicy.XamlLanguage(L.Code);
        var button = Control<DropDownButton>("LanguageButton");
        button.Content = LanguagePolicy.Choices.FirstOrDefault(choice => choice.Code == settings.Language).Name ?? L.T("语言 · 系统");

        // Size both surfaces from every label, including the menu's check column.
        double TextWidth(string text, FontFamily family, string language)
        {
            var label = new TextBlock
            {
                Text = text,
                FontFamily = family,
                FontSize = button.FontSize,
                FontWeight = button.FontWeight,
                Language = language
            };
            label.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            return label.DesiredSize.Width;
        }
        var menuWidth = languageItems.Values.Max(item => TextWidth(item.Text, item.FontFamily, item.Language)) + 64;
        var buttonWidth = LanguagePolicy.Choices.Select(choice => choice.Name).Append(L.T("语言 · 系统"))
            .Max(text => TextWidth(text, UiTypography.Family(L.Code), root.Language)) + 48;
        var width = 4 * Math.Ceiling(Math.Max(128, Math.Max(menuWidth, buttonWidth)) / 4);
        button.Width = width;
        var presenter = (Style)XamlReader.Load("""
            <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                   TargetType="MenuFlyoutPresenter" BasedOn="{StaticResource DefaultMenuFlyoutPresenterStyle}" />
            """);
        presenter.Setters.Add(new Setter(FrameworkElement.WidthProperty, width));
        presenter.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, width));
        presenter.Setters.Add(new Setter(FrameworkElement.MaxWidthProperty, width));
        presenter.Setters.Add(new Setter(Microsoft.UI.Xaml.Controls.Control.FontFamilyProperty, UiTypography.Family(L.Code)));
        ((MenuFlyout)button.Flyout).MenuFlyoutPresenterStyle = presenter;
    }
    private void Relocalize()
    {
        // Keep controls and job state alive. Only replace presentation strings.
        var status = Control<TextBlock>("Status").Text;
        var source = new[] { "SourceSummary", "SourceEvidence", "SourceBadge" }.ToDictionary(name => name, name => Control<TextBlock>(name).Text);
        var cancelEnabled = Control<Button>("Cancel").IsEnabled;
        viewLocalization.Apply(root);
        Title = L.T("映码 · LumaRecode");
        updating = true;
        try
        {
            foreach (var (item, original) in choiceLabels)
                item.Content = L.T(original);
            foreach (var name in new[] { "InputKind", "Purpose", "Codec", "Variant", "Container", "Transform", "Profile", "Sampling", "BitDepth", "ColorRange", "Audio" })
            {
                var combo = Control<ComboBox>(name);
                var selected = combo.SelectedIndex;
                // WinUI caches the selected label separately from the item's Content.
                combo.SelectedIndex = -1;
                combo.SelectedIndex = selected;
            }
        }
        finally { updating = false; }
        appearance.Relocalize();
        UpdateLanguageMenu();
        if (media is not null)
            UpdateMediaPresentation();
        else
            foreach (var (name, text) in source)
                Control<TextBlock>(name).Text = L.T(text);
        SetStatus(status);
        Refresh();
        UpdateTransferStats();
        UiTypography.Refresh(root);
        if (activeJob is not null)
        {
            SetBusy(true);
            Control<Button>("Cancel").IsEnabled = cancelEnabled;
        }
    }
    private void FillChoices()
    {
        Choices(InputKind, ("auto", L.T("自动识别")), ("sdr", L.T("手动指定 · SDR")), ("hlg", L.T("手动指定 · HLG HDR")), ("pq", L.T("手动指定 · PQ HDR")), ("log", L.T("手动指定 · Log")));
        Choices(Purpose, ("share", L.T("快速分享 · Intel 硬件")), ("archive", L.T("软件归档 · CPU")));
        Choices(Codec, ("hevc", "HEVC"), ("av1", "AV1"), ("avc", "H.264 / AVC"));
        Choices(Variant, ("standard", L.T("标准实测档")), ("fast", L.T("AV1 更快备用档")));
        Choices(Container, ("auto", L.T("自动 · 按用途选择")), ("mp4", "MP4"), ("mkv", "MKV"));
        Choices(Transform, ("none", L.T("不套用 · 保留 / 标准化")), ("log-hdr", L.T("Log → HDR（HLG）")), ("log-sdr", "Log → SDR"), ("hdr-sdr", "HDR → SDR"));
        Choices(Profile, ("", L.T("请选择 Log 曲线…")), ("milog", L.T("小米 Mi-Log")), ("pocket3", "Pocket 3 D-Log M"), ("samsung", "Samsung Log"));
        Choices(Sampling, ("source", L.T("保持原采样")), ("420", "4:2:0"), ("422", "4:2:2"), ("444", "4:4:4"), ("gray", L.T("灰度")));
        Choices(BitDepth, ("8", L.T("8 位")), ("10", L.T("10 位")), ("12", L.T("12 位")));
        Choices(ColorRange, ("source", L.T("保持原范围")), ("limited", L.T("Limited · 有限范围")), ("full", L.T("Full · 全范围")));
        Choices(Audio, ("copy", L.T("复制原音轨")), ("aac", "AAC · 256 kbps"), ("opus", "Opus · 192 kbps"), ("flac", L.T("FLAC · 无损")));
        ApplyPurposeDefaults();
    }
    private void ApplyDefaultAudio()
    {
        var audio = encodingConfig.GetProperty("audio");
        Select(Audio, (Value(Purpose) == "archive" ? audio.GetProperty("archive_default") : audio.GetProperty("share_default").GetProperty(Value(Codec))).GetString());
    }
    private void ApplyPurposeDefaults()
    {
        var archive = Value(Purpose) == "archive";
        Select(Sampling, DefaultSampling());
        Select(ColorRange, archive ? "source" : "limited");
        audioUsesDefault = true;
        ApplyDefaultAudio();
        depthUsesDefault = true;
    }
    private string DefaultSampling() => Value(Purpose) == "archive" && Value(Codec) != "av1" ? MediaText("archive_sampling", "source") : "420";
    private void WireEvents()
    {
        Control<Button>("ChooseInput").Click += async (_, _) => await ChooseInputAsync();
        Control<Button>("ChooseOutput").Click += async (_, _) => await ChooseOutputAsync();
        Control<Button>("ToolsButton").Click += async (_, _) => await ConfigureToolsAsync();
        Control<Button>("AboutButton").Click += async (_, _) =>
        {
            if (dialogOpen || closing)
                return;
            dialogOpen = true;
            try
            {
                await AboutDialog.ShowAsync(root);
            }
            finally { dialogOpen = false; }
        };
        Control<Button>("Start").Click += async (_, _) => await StartAsync();
        Control<Button>("Cancel").Click += async (_, _) => { if (activeJob is not null) { Control<Button>("Cancel").IsEnabled = false; SetStatus(L.T("正在取消…")); await activeJob.CancelAsync(); } };
        Control<Button>("OpenFolder").Click += (_, _) => { if (File.Exists(completedOutput)) Process.Start(new ProcessStartInfo("explorer.exe", Path.GetDirectoryName(completedOutput)!) { UseShellExecute = true }); };
        Input.KeyDown += async (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) await InspectAsync(); };
        Input.LostFocus += async (_, _) => { if (File.Exists(Input.Text.Trim('"', ' '))) await InspectAsync(); };
        Input.TextChanged += (_, _) =>
        {
            if (activeJob is null && !probing && media is not null && !Input.Text.Trim('"', ' ').Equals(MediaText("path"), StringComparison.OrdinalIgnoreCase))
                ClearMedia();
        };
        foreach (var name in new[] { "Variant", "Container", "Sampling", "ColorRange", "Profile" })
            Control<ComboBox>(name).SelectionChanged += (_, _) => Refresh();
        Purpose.SelectionChanged += (_, _) =>
        {
            if (updating)
                return;
            updating = true;
            try
            {
                ApplyPurposeDefaults();
            }
            finally { updating = false; }
            Refresh();
        };
        Codec.SelectionChanged += (_, e) =>
        {
            if (updating)
                return;
            updating = true;
            try
            {
                if (audioUsesDefault)
                    ApplyDefaultAudio();
                if (Value(Codec) == "av1" || Value(Purpose) == "share" || e.RemovedItems.OfType<ComboBoxItem>().Any(item => (string)item.Tag == "av1"))
                    Select(Sampling, DefaultSampling());
            }
            finally { updating = false; }
            Refresh();
        };
        Audio.SelectionChanged += (_, _) => { if (!updating) { audioUsesDefault = false; Refresh(); } };
        BitDepth.SelectionChanged += (_, _) => { if (!updating) { depthUsesDefault = false; Refresh(); } };
        Range.SelectionChanged += (_, _) => Refresh();
        InputKind.SelectionChanged += (_, _) =>
        {
            if (updating)
                return;
            updating = true;
            try
            {
                Range.SelectedIndex = Kind is "hlg" or "pq" or "log" ? 1 : 0;
                depthUsesDefault = true;
            }
            finally { updating = false; }
            Refresh();
        };
        Transform.SelectionChanged += (_, _) => Refresh();
        var card = Control<Border>("SourceCard");
        card.DragOver += (_, e) => { if (activeJob is null && e.DataView.Contains(StandardDataFormats.StorageItems)) e.AcceptedOperation = DataPackageOperation.Copy; };
        card.Drop += async (_, e) =>
        {
            if (activeJob is not null || !e.DataView.Contains(StandardDataFormats.StorageItems))
                return;
            var files = await e.DataView.GetStorageItemsAsync();
            if (files.Count == 1)
            {
                Input.Text = files[0].Path;
                await InspectAsync();
            }
        };
        root.SizeChanged += (_, e) => Reflow(e.NewSize.Width);
    }
    /// <summary>Repositions the same controls at two width breakpoints, without rebuilding job state.</summary>
    /// <remarks>Auto rows stretch the paired cards together in two columns; a single column uses each card's natural height.</remarks>
    private void Reflow(double width)
    {
        var narrow = width < 960;
        var grid = Control<Grid>("SettingsGrid");
        var rows = narrow ? 4 : 3;
        while (grid.RowDefinitions.Count < rows)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        while (grid.RowDefinitions.Count > rows)
            grid.RowDefinitions.RemoveAt(grid.RowDefinitions.Count - 1);
        grid.ColumnDefinitions[0].MinWidth = narrow ? 0 : 380;
        grid.ColumnDefinitions[1].MinWidth = narrow ? 0 : 300;
        grid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
        grid.ColumnDefinitions[1].Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        grid.ColumnSpacing = narrow ? 0 : 24;
        Grid.SetColumnSpan(Control<Border>("SourceCard"), narrow ? 1 : 2);
        var color = Control<Border>("ColorSettings");
        Grid.SetColumn(color, narrow ? 0 : 1);
        Grid.SetRow(color, narrow ? 2 : 1);
        var format = Control<Border>("FormatCard");
        Grid.SetRow(format, narrow ? 3 : 2);
        Grid.SetColumnSpan(format, narrow ? 1 : 2);

        var compact = width < 760;
        var fields = Control<Grid>("FormatFields");
        var fieldRows = compact ? 4 : 2;
        while (fields.RowDefinitions.Count < fieldRows)
            fields.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        while (fields.RowDefinitions.Count > fieldRows)
            fields.RowDefinitions.RemoveAt(fields.RowDefinitions.Count - 1);
        fields.ColumnDefinitions[1].Width = compact ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        fields.ColumnSpacing = compact ? 0 : 24;
        Grid.SetColumn(Audio, compact ? 0 : 1);
        Grid.SetRow(Audio, compact ? 2 : 0);
        var audioNote = Control<TextBlock>("AudioNote");
        Grid.SetColumn(audioNote, compact ? 0 : 1);
        Grid.SetRow(audioNote, compact ? 3 : 1);
        var details = Control<Grid>("SourceDetails");
        details.ColumnDefinitions[2].Width = new GridLength(compact ? 0 : 240);
        details.RowSpacing = compact ? 16 : 0;
        Grid.SetColumnSpan(Control<StackPanel>("SourceInfo"), compact ? 2 : 1);
        Grid.SetColumn(InputKind, compact ? 1 : 2);
        Grid.SetRow(InputKind, compact ? 1 : 0);
        Grid.SetColumnSpan(InputKind, compact ? 2 : 1);

        var buttons = Control<StackPanel>("ActionButtons");
        Grid.SetColumn(buttons, compact ? 0 : 1);
        Grid.SetRow(buttons, compact ? 1 : 0);
        Grid.SetColumnSpan(buttons, compact ? 2 : 1);
        Grid.SetColumnSpan(Control<TextBlock>("Status"), compact ? 2 : 1);
        Control<Grid>("ActionGrid").RowSpacing = compact ? 12 : 0;
    }
    private string MediaText(string key, string fallback = "") => media is { } item && item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : fallback;
    private void SetStatus(string text) => Control<TextBlock>("Status").Text = L.T(text);
    private MediaTools ToolsObject() => new(ffmpeg, ffprobe);
    private void ResolveTools()
    {
        ffmpeg = ConversionClient.FindTool("ffmpeg", settings.Ffmpeg);
        ffprobe = ConversionClient.FindTool("ffprobe", settings.Ffprobe);
    }
    private static JsonElement LoadEncodingConfig()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("VideoConverter.EncodingPresets.json")!;
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.Clone();
    }
    private string DescribePreset()
    {
        var preset = Value(Variant) == "fast" ? encodingConfig.GetProperty("archive_fast_av1") : encodingConfig.GetProperty(Value(Purpose)).GetProperty(Value(Codec));
        var encoder = preset.GetProperty("encoder").GetString() switch
        {
            "libx265" => "x265",
            "libx264" => "x264",
            "libsvtav1" => "SVT-AV1",
            var name => name
        };
        var share = Value(Purpose) == "share";
        var result = new List<string> { encoder ?? "", share ? $"TU{preset.GetProperty("preset")} / ICQ{preset.GetProperty("quality")}" : $"{preset.GetProperty("preset")} / CRF{preset.GetProperty("quality")}" };
        if (share)
        {
            result.Add("low_power=1");
            result.Add($"async_depth={preset.GetProperty("async_depth")}");
        }
        else
        {
            if (Value(Codec) == "hevc")
                result.Add("pools=16");
            foreach (var key in new[] { "tune", "lp" })
                if (preset.TryGetProperty(key, out var item))
                    result.Add(key + "=" + item);
            if (preset.TryGetProperty("encoder_parameters", out var extra))
                foreach (var item in extra.EnumerateObject())
                    result.Add(item.Name + "=" + item.Value);
        }
        return string.Join(" · ", result);
    }
    private bool MediaFlag(string key) => media is { } item && item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.True;
    private string SelectedSampling()
    {
        var selected = Value(Sampling);
        return selected == "source" ? MediaText("source_sampling", "420") : selected;
    }
    private string SelectedColorRange()
    {
        var selected = Value(ColorRange);
        if (selected != "source")
            return selected;
        return SourceColorRange();
    }
    private string SourceColorRange() => MediaFlag("range_inferred") && Value(Profile) == "milog" && Value(Transform).StartsWith("log-") ? "full" : MediaText("source_color_range", "limited");
    private string[] SourceSamplingChoices() => media is { } item && item.TryGetProperty("allowed_sampling", out var values)
        ? values.EnumerateArray().Select(value => value.GetString() ?? "").ToArray() : ["420"];
    private int[] AllowedDepths() => OutputPolicy.DepthChoices(SourceDepth, Value(Purpose), Value(Codec), Hdr ? "hdr" : "sdr");
    private void UpdateDepthChoices()
    {
        var allowed = AllowedDepths();
        foreach (var item in BitDepth.Items.OfType<ComboBoxItem>())
            item.IsEnabled = allowed.Contains(int.Parse((string)item.Tag));
        if (allowed.Length == 0)
        {
            BitDepth.SelectedIndex = -1;
            return;
        }
        if (depthUsesDefault || !allowed.Contains(Depth))
            Select(BitDepth, (Value(Purpose) == "archive" ? allowed.Max() : Hdr ? 10 : 8).ToString(CultureInfo.InvariantCulture));
    }
    /// <summary>Applies compatible choices, output naming and start-button availability as one guarded update.</summary>
    private void Refresh()
    {
        if (updating)
            return;
        updating = true;
        try
        {
            if (Kind == "sdr" && Hdr)
                Range.SelectedIndex = 0;
            Control<RadioButton>("HdrChoice").IsEnabled = Kind is "hlg" or "pq" or "log";
            if (Hdr && Value(Codec) == "avc")
                Select(Codec, "hevc");
            UpdateDepthChoices();
            foreach (var item in Codec.Items.OfType<ComboBoxItem>())
                item.IsEnabled = !Hdr || (string)item.Tag != "avc";
            var fullSampling = Value(Purpose) == "archive" && Value(Codec) != "av1";
            var sourceChoices = SourceSamplingChoices();
            foreach (var item in Sampling.Items.OfType<ComboBoxItem>())
            {
                var key = (string)item.Tag;
                item.IsEnabled = key == "source" ? fullSampling && DefaultSampling() == "source" && sourceChoices.Contains(MediaText("source_sampling", "420"))
                    : sourceChoices.Contains(key) && (fullSampling || key == "420");
            }
            if (Sampling.SelectedItem is ComboBoxItem selectedSampling && !selectedSampling.IsEnabled)
                Select(Sampling, DefaultSampling());
            var flacAllowed = MediaFlag("flac_eligible");
            Audio.Items.OfType<ComboBoxItem>().Single(item => (string)item.Tag == "flac").IsEnabled = flacAllowed;
            ((ComboBoxItem)Container.Items[1]).IsEnabled = Value(Audio) != "flac";
            if (Value(Audio) == "flac" && Value(Container) == "mp4")
                Select(Container, "mkv");
            Control<TextBlock>("AudioNote").Text = Value(Audio) == "flac" ? L.T("保留每条音轨的采样率、声道与整数精度。")
                : media is null ? L.T("FLAC 支持整数 PCM 和可确认的无损音轨。")
                : flacAllowed ? L.T("此素材可转为 FLAC。") : L.T(MediaText("flac_reason"));
            var fast = Value(Purpose) == "archive" && Value(Codec) == "av1";
            Variant.Visibility = fast ? Visibility.Visible : Visibility.Collapsed;
            if (!fast)
                Select(Variant, "standard");
            string[] allowed = Kind switch
            {
                "hlg" or "pq" => Hdr ? ["none"] : ["hdr-sdr"],
                "log" => Hdr ? ["log-hdr"] : ["log-sdr"],
                _ => ["none"]
            };
            foreach (var item in Transform.Items.OfType<ComboBoxItem>())
                item.IsEnabled = allowed.Contains((string)item.Tag);
            if (!allowed.Contains(Value(Transform)))
                Select(Transform, allowed[0]);
            Profile.IsEnabled = Value(Transform).StartsWith("log-");
            Profile.Visibility = Profile.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
            var chosenSampling = SelectedSampling();
            var sampling = chosenSampling switch
            {
                "420" => "4:2:0",
                "422" => "4:2:2",
                "444" => "4:4:4",
                "gray" => L.T("灰度"),
                _ => L.T("采样待选择")
            };
            var dataRange = SelectedColorRange() == "full" ? "Full" : "Limited";
            var curve = Kind == "pq" && !Profile.IsEnabled ? "PQ" : "HLG";
            Control<TextBlock>("TargetNote").Text = (Hdr ? L.T($"BT.2020 {curve} · {Depth} 位 · {sampling}") : L.T($"Rec.709 · {Depth} 位 · {sampling}")) + " · " + dataRange;
            var formatNotes = new List<string>();
            if (MediaFlag("pixel_rgb"))
                formatNotes.Add(L.T("RGB 输入转换为 YCbCr。"));
            if (MediaFlag("pixel_alpha"))
                formatNotes.Add(L.T("输出为不带透明通道的视频。"));
            if (MediaFlag("range_inferred"))
                formatNotes.Add(L.T($"原片未标明范围，按 {(SourceColorRange() == "full" ? "Full" : "Limited")} 处理。"));
            Control<TextBlock>("FormatNote").Text = string.Join("\n", formatNotes);
            Control<TextBlock>("FormatNote").Visibility = formatNotes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            var codecName = Value(Codec) switch
            {
                "av1" => "AV1",
                "avc" => "H.264",
                _ => "HEVC"
            };
            Control<TextBlock>("PresetName").Text = $"{(Hdr ? "HDR" : "SDR")} · {codecName} · {(Value(Purpose) == "share" ? L.T("快速分享") : L.T("软件归档"))}";
            var quality = DescribePreset();
            Control<TextBlock>("PresetDetail").Text = L.T($"{quality}\n{Depth} 位 {sampling} · {Suffix[1..].ToUpperInvariant()}") + " · " + dataRange;
            Control<TextBlock>("ContainerNote").Text = Value(Container) == "auto"
                ? Value(Audio) == "flac" ? L.T("自动 → MKV。FLAC 音轨默认使用 MKV 封装。") : L.T($"自动 → {Suffix[1..].ToUpperInvariant()}。快速分享默认 MP4，软件归档默认 MKV。")
                : Suffix == ".mp4" ? L.T("MP4。复制原音轨时，会在编码前检测封装兼容性。") : L.T("MKV。封装独立于编码质量档和动态范围。");
            Control<TextBlock>("TransformNote").Text = Value(Transform) switch
            {
                "log-hdr" => L.T($"使用对应相机的曲线，转换为 {Depth} 位 HLG。"),
                "log-sdr" => L.T("解码对应 Log，并将高光映射到 Rec.709。"),
                "hdr-sdr" => L.T("按 HLG / PQ 原片做色调映射，输出 Rec.709。"),
                _ => L.T("保留原片动态范围，按目标格式标准化色彩。")
            };
            var warning = "";
            if (string.IsNullOrEmpty(ffmpeg) || string.IsNullOrEmpty(ffprobe))
                warning = L.T("未找到 FFmpeg / ffprobe，请设置工具路径。");
            else if (media is not null && Profile.IsEnabled && Value(Profile) == "")
                warning = L.T("请确认相机的 Log 曲线。");
            else if (media is not null && Kind == "unknown" && !Profile.IsEnabled)
                warning = L.T("没有可靠的曲线标记，请确认输入解释。");
            else if (media is not null && Profile.IsEnabled && SourceDepth < 10)
                warning = L.T("Log 转换需要对应相机的高位深原片。");
            else if (media is not null && AllowedDepths().Length == 0)
                warning = L.T("HDR 输出需要至少 10 位的源素材，请选择 SDR 或高位深原片。");
            else if (media is not null && !sourceChoices.Contains(chosenSampling))
                warning = L.T("输出采样须在原片采样上限内选择。");
            else if (media is not null && !new[] { "420", "422", "444", "gray" }.Contains(chosenSampling))
                warning = L.T("当前编码器无法保持原采样，请手动选择 4:2:0、4:2:2 或 4:4:4。");
            else if (media is not null && chosenSampling != "420" && !fullSampling)
                warning = L.T("保持此采样需要软件 HEVC 或 H.264；也可手动选择 4:2:0。");
            else if (MediaFlag("pixel_alpha") && Value(Sampling) == "source")
                warning = L.T("素材含透明通道。请选择具体采样格式，输出为不带透明通道的视频。");
            else if (Value(Audio) == "flac" && !flacAllowed)
                warning = L.T(MediaText("flac_reason"));
            var notice = Control<InfoBar>("Notice");
            notice.Message = warning;
            notice.IsOpen = warning != "";
            Control<TextBlock>("ChooseInputLabel").Text = L.T(media is null ? "选择视频" : "更换视频");
            Control<Button>("ChooseInput").Style = (Style)root.Resources[media is null ? "SourceChooserAccent" : "PathActionButtonStyle"];
            Control<Button>("Start").IsEnabled = media is not null && !probing && activeJob is null && warning == "";
            // Regenerate only a name still owned by the application. A user
            // path survives option changes, except for a changed container suffix.
            if (media is not null && (Output.Text == "" || Output.Text == automaticOutput))
            {
                var source = MediaText("path");
                automaticOutput = Path.Combine(Path.GetDirectoryName(source)!, $"{Path.GetFileNameWithoutExtension(source)}_{(Hdr ? "HDR" : "SDR")}_{Value(Codec)}_{Value(Purpose)}{Suffix}");
                Output.Text = automaticOutput;
            }
            else if (media is not null && previousSuffix != Suffix && new[] { ".mp4", ".mkv" }.Contains(Path.GetExtension(Output.Text).ToLowerInvariant()))
                Output.Text = Path.ChangeExtension(Output.Text, Suffix);
            previousSuffix = Suffix;
            UpdateEditingEnabled(media is not null && !probing && activeJob is null);
            Input.IsEnabled = activeJob is null && !probing;
            Control<Button>("ChooseInput").IsEnabled = Input.IsEnabled;
        }
        finally { updating = false; }
    }
    private async Task ChooseInputAsync()
    {
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        foreach (var type in new[] { ".mp4", ".mov", ".mkv", ".m4v", ".webm", ".avi", ".mts" })
            picker.FileTypeFilter.Add(type);
        var file = await picker.PickSingleFileAsync();
        if (file is not null)
        {
            Input.Text = file.Path;
            await InspectAsync();
        }
    }
    private void ClearMedia()
    {
        media = null;
        Control<TextBlock>("SourceSummary").Text = L.T("尚未选择素材");
        Control<TextBlock>("SourceEvidence").Text = L.T("识别相机 Log、HDR 与常规 SDR。");
        Control<TextBlock>("SourceBadge").Text = L.T("等待添加");
        foreach (var name in new[] { "SourceVideo", "SourceColor", "SourceAudio" })
            Control<TextBlock>(name).Visibility = Visibility.Collapsed;
        if (Output.Text == automaticOutput)
            Output.Text = "";
        automaticOutput = completedOutput = "";
        completed = false;
        clock.Reset();
        encodedSeconds = encodedFrames = conversionSpeed = currentFps = 0;
        Control<ProgressBar>("Progress").Value = 0;
        Control<Button>("OpenFolder").IsEnabled = false;
        UpdateTransferStats();
        SetStatus(L.T("添加素材后开始"));
        Refresh();
    }
    private async Task ChooseOutputAsync()
    {
        var picker = new Microsoft.Windows.Storage.Pickers.FileSavePicker(AppWindow.Id)
        {
            SuggestedFileName = Path.GetFileNameWithoutExtension(Output.Text),
            DefaultFileExtension = Suffix
        };
        picker.FileTypeChoices.Add(Suffix[1..].ToUpperInvariant(), new[] { Suffix });
        var file = await picker.PickSaveFileAsync();
        if (file is not null)
            Output.Text = file.Path;
    }
    /// <summary>Probes one source asynchronously and applies only the current generation's results.</summary>
    private async Task InspectAsync()
    {
        if (activeJob is not null || probing)
            return;
        var path = Input.Text.Trim('"', ' ');
        if (path == "" || path.Equals(MediaText("path"), StringComparison.OrdinalIgnoreCase))
            return;
        if (!File.Exists(path))
        {
            await MessageAsync(L.T("无法读取"), L.T("请选择有效的视频文件。"));
            return;
        }
        if (ffmpeg == "" || ffprobe == "")
        {
            await ConfigureToolsAsync();
            if (ffmpeg == "" || ffprobe == "")
                return;
        }
        var token = ++generation;
        media = null;
        probing = true;
        SetStatus(L.T("正在读取相机元数据…"));
        foreach (var name in new[] { "SourceVideo", "SourceColor", "SourceAudio" })
            Control<TextBlock>(name).Visibility = Visibility.Collapsed;
        Control<TextBlock>("SourceSummary").Text = L.T("正在识别素材…");
        Refresh();
        using var client = new ConversionClient();
        probes.Add(client);
        try
        {
            await client.RunAsync(new ConversionRequest("probe", ToolsObject(), path), (type, data) =>
            {
                if (token != generation)
                    return;
                if (type == "media")
                    ApplyMedia(data);
                else if (type == "error")
                {
                    Control<TextBlock>("SourceSummary").Text = L.T("读取失败");
                    Control<TextBlock>("SourceEvidence").Text = L.T(data.GetString());
                    SetStatus(L.T("读取失败"));
                }
            });
        }
        catch (Exception error) { if (!closing) await MessageAsync(L.T("读取失败"), error.Message); }
        finally
        {
            probes.Remove(client);
            probing = false;
            if (!closing)
                Refresh();
        }
    }
    // A new source starts from share defaults; subsequent user selections are
    // retained by Refresh as long as the source and encoder can support them.
    private void ApplyMedia(JsonElement data)
    {
        updating = true;
        media = data;
        Select(InputKind, "auto");
        Select(Purpose, "share");
        Select(Codec, "hevc");
        Select(Variant, "standard");
        ApplyPurposeDefaults();
        Range.SelectedIndex = MediaText("kind") is "log" or "hlg" or "pq" ? 1 : 0;
        Select(Profile, MediaText("profile"));
        Output.Text = "";
        automaticOutput = "";
        completed = false;
        clock.Reset();
        encodedSeconds = encodedFrames = conversionSpeed = currentFps = 0;
        completedOutput = "";
        Control<Button>("OpenFolder").IsEnabled = false;
        Control<ProgressBar>("Progress").Value = 0;
        UpdateTransferStats();
        UpdateMediaPresentation();
        SetStatus(L.T("已识别素材，默认选项已就绪"));
        updating = false;
        Refresh();
    }
    private void UpdateMediaPresentation()
    {
        if (media is not { } data)
            return;
        var video = data.GetProperty("video");
        var name = MediaText("profile") switch
        {
            "milog" => L.T("小米 Mi-Log"),
            "pocket3" => "Pocket 3 D-Log M",
            "samsung" => "Samsung Log",
            _ => MediaText("kind") switch { "hlg" => "HLG HDR", "pq" => "PQ HDR", "sdr" => "SDR", "log" => L.T("Log · 曲线待确认"), _ => L.T("色彩类型待确认") }
        };
        var pixel = video.GetProperty("pix_fmt").GetString() ?? "";
        var sampling = MediaText("source_sampling") switch
        {
            "420" => "4:2:0",
            "422" => "4:2:2",
            "444" => MediaFlag("pixel_rgb") ? "RGB" : "4:4:4",
            "gray" => L.T("灰度"),
            _ => pixel
        };
        var depths = data.TryGetProperty("component_depths", out var components) ? components.EnumerateArray().Select(item => item.GetInt32()).Distinct().ToArray() : [];
        var depth = depths.Length > 0 ? string.Join("/", depths) + L.T(" 位") : video.TryGetProperty("bits_per_raw_sample", out var raw) && int.TryParse(raw.ToString(), out var bits) && bits > 0 ? bits + L.T(" 位") : L.T("位深未标明");
        if (data.TryGetProperty("pixel_float", out var floating) && floating.GetBoolean())
            depth += L.T("浮点");
        var duration = data.GetProperty("duration").GetDouble();
        Control<TextBlock>("SourceSummary").Text = $"{name} · {video.GetProperty("width")} × {video.GetProperty("height")} · {sampling}";
        SetSourceDetail("SourceVideo", DescribeVideo(video, depth));
        SetSourceDetail("SourceColor", DescribeColors(video));
        SetSourceDetail("SourceAudio", DescribeAudio(data));
        Control<TextBlock>("SourceEvidence").Text = L.T(MediaText("evidence"));
        Control<TextBlock>("SourceBadge").Text = L.T($"{depth} · {duration:F1} 秒");
        UiTypography.Refresh(root);
    }
    private void SetSourceDetail(string name, string text)
    {
        var control = Control<TextBlock>(name);
        control.Text = text;
        control.Visibility = Visibility.Visible;
    }
    private static string Field(JsonElement item, string key, string fallback = "") => item.TryGetProperty(key, out var value) ? value.ToString() : fallback;
    private static string Bitrate(JsonElement item)
    {
        return TryNumber(Field(item, "bit_rate"), out var rate) && rate > 0 ? rate >= 1_000_000 ? $"{rate / 1_000_000:0.0} Mb/s" : $"{rate / 1000:0.#} kb/s" : "";
    }
    private static string DescribeVideo(JsonElement video, string depth)
    {
        var parts = new List<string> { L.T("视频：") + Field(video, "codec_name").ToUpperInvariant() };
        var profile = Field(video, "profile");
        if (profile != "" && profile != "unknown")
            parts.Add(profile);
        foreach (var key in new[] { "avg_frame_rate", "r_frame_rate" })
        {
            var ratio = Field(video, key).Split('/');
            if (ratio.Length == 2 && TryNumber(ratio[0], out var a) && TryNumber(ratio[1], out var b) && a > 0 && b > 0)
            {
                parts.Add($"{a / b:0.###} fps");
                break;
            }
        }
        parts.Add(depth);
        var bitrate = Bitrate(video);
        if (bitrate != "")
            parts.Add(bitrate);
        return string.Join(" · ", parts);
    }
    private static string DescribeColors(JsonElement video)
    {
        var primaries = Field(video, "color_primaries") switch
        {
            "bt709" => "BT.709",
            "bt2020" => "BT.2020",
            "" or "unknown" => L.T("色域未标明"),
            var value => value
        };
        var transfer = Field(video, "color_transfer") switch
        {
            "bt709" => L.T("Rec.709 曲线"),
            "arib-std-b67" => "HLG",
            "smpte2084" => "PQ",
            "" or "unknown" => L.T("曲线未标明"),
            var value => value
        };
        var range = Field(video, "color_range") switch
        {
            "pc" => L.T("全范围"),
            "tv" => L.T("有限范围"),
            _ => L.T("范围未标明")
        };
        return L.T($"色彩标签：{primaries} · {transfer} · {range}");
    }
    private static string DescribeAudio(JsonElement data)
    {
        if (!data.TryGetProperty("audio", out var audio) || audio.GetArrayLength() == 0)
            return L.T("音轨：无");
        var lines = new List<string>();
        var index = 0;
        foreach (var stream in audio.EnumerateArray())
        {
            var layout = Field(stream, "channel_layout") switch
            {
                "stereo" => L.T("立体声"),
                "mono" => L.T("单声道"),
                "" or "unknown" => Field(stream, "channels", "?") + L.T(" 声道"),
                var value => value
            };
            var parts = new List<string> { L.T($"音轨 {++index}：{Field(stream, "codec_name").ToUpperInvariant()}"), layout };
            var profile = Field(stream, "profile");
            if (profile != "" && profile != "unknown")
                parts.Insert(1, profile);
            if (TryNumber(Field(stream, "sample_rate"), out var hz) && hz > 0)
                parts.Add($"{hz / 1000:0.###} kHz");
            var audioBits = Field(stream, "bits_per_raw_sample");
            if (!int.TryParse(audioBits, out var depth) || depth <= 0)
                int.TryParse(Field(stream, "bits_per_sample"), out depth);
            if (depth > 0)
                parts.Add(L.T($"{depth} 位"));
            var bitrate = Bitrate(stream);
            if (bitrate != "")
                parts.Add(bitrate);
            lines.Add(string.Join(" · ", parts));
        }
        return string.Join("\n", lines);
    }
    /// <summary>Snapshots current choices so edits cannot change the parameters of an active conversion.</summary>
    private async Task StartAsync()
    {
        if (activeJob is not null || media is null)
            return;
        var output = Output.Text.Trim('"', ' ');
        if (File.Exists(output))
        {
            await MessageAsync(L.T("请更换输出位置"), L.T("输出文件已存在，程序不会覆盖文件。"));
            return;
        }
        if (!Directory.Exists(Path.GetDirectoryName(output)) || Path.GetExtension(output).ToLowerInvariant() != Suffix)
        {
            await MessageAsync(L.T("请调整输出位置"), L.T($"请选择已有目录，文件后缀应为 {Suffix}。"));
            return;
        }
        var request = new ConversionRequest("convert", ToolsObject(), MediaText("path"), output,
            new EncodeOptions(Purpose: Value(Purpose), Codec: Value(Codec), Output: Hdr ? "hdr" : "sdr",
                Transform: Value(Transform), Profile: Value(Profile) == "" ? null : Value(Profile),
                Sampling: Value(Sampling), Variant: Value(Variant), Audio: Value(Audio),
                Container: Value(Container), SourceKind: Value(InputKind),
                CpuDecode: Control<CheckBox>("CpuDecode").IsChecked == true,
                ColorRange: Value(ColorRange), BitDepth: Value(BitDepth)));

        using var client = new ConversionClient();
        activeJob = client;
        completed = false;
        encodedSeconds = encodedFrames = conversionSpeed = currentFps = 0;
        clock.Restart();
        clockTimer.Start();
        UpdateTransferStats();
        Control<TextBox>("Log").Text = "";
        Control<ProgressBar>("Progress").Value = 0;
        SetBusy(true);
        SetStatus(L.T("正在启动转换…"));
        try
        {
            await client.RunAsync(request, Receive);
        }
        catch (Exception error) { await MessageAsync(L.T("转换失败"), error.Message); SetStatus(L.T("转换失败")); }
        finally { clock.Stop(); clockTimer.Stop(); activeJob = null; UpdateTransferStats(); SetBusy(false); Refresh(); if (closing) Close(); }
    }
    private async void Receive(string type, JsonElement data)
    {
        if (type == "log")
        {
            var log = Control<TextBox>("Log");
            log.Text += data.GetString() + "\r\n";
            if (log.Text.Length > 40000)
                log.Text = log.Text[^30000..];
        }
        else if (type == "progress")
        {
            if (data.TryGetProperty("out_time_us", out var time) && TryNumber(time.ToString(), out var us))
                encodedSeconds = Math.Max(encodedSeconds, us / 1_000_000);
            if (data.TryGetProperty("frame", out var count) && TryNumber(count.ToString(), out var frames))
                encodedFrames = Math.Max(encodedFrames, frames);
            if (data.TryGetProperty("speed", out var speed) && TryNumber(speed.ToString().TrimEnd('x'), out var rate) && rate > 0)
                conversionSpeed = conversionSpeed > 0 ? .7 * conversionSpeed + .3 * rate : rate;
            if (data.TryGetProperty("fps", out var fps) && TryNumber(fps.ToString(), out var framesPerSecond))
                currentFps = framesPerSecond;
            UpdateTransferStats();
            SetStatus(L.T($"正在转换 · {(data.TryGetProperty("frame", out var frame) ? frame.ToString() : "0")} 帧"));
        }
        else if (type == "status")
            SetStatus(data.GetString() ?? "");
        else if (type == "done")
        {
            completed = true;
            completedOutput = data.GetString() ?? "";
            Control<ProgressBar>("Progress").Value = 1000;
            SetStatus(L.T("转换完成 · 已核对色彩与采样格式"));
        }
        else if (type == "cancelled")
        {
            encodedSeconds = encodedFrames = conversionSpeed = currentFps = 0;
            Control<ProgressBar>("Progress").Value = 0;
            SetStatus(L.T("已取消"));
        }
        else if (type == "error")
        {
            SetStatus(L.T("转换失败"));
            Control<Expander>("LogPanel").IsExpanded = true;
            Control<TextBox>("Log").Text += L.T(data.GetString());
            await MessageAsync(L.T("转换失败"), data.GetString() ?? "");
        }
    }
    private static bool TryNumber(string text, out double value) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    private static string DisplayTime(double seconds)
    {
        var value = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, 359999999));
        return value.TotalHours >= 1 ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}" : $"{value.Minutes:00}:{value.Seconds:00}";
    }
    /// <summary>Displays encoded position, encoder fps and wall-clock ETA using separate units.</summary>
    /// <remarks>FFmpeg speed is media-seconds per wall-second. Keep progress below 100% until output verification succeeds.</remarks>
    private void UpdateTransferStats()
    {
        var duration = media is { } item ? item.GetProperty("duration").GetDouble() : 0;
        var percent = completed ? 100 : duration > 0 ? Math.Clamp(100 * encodedSeconds / duration, 0, 99.5) : 0;
        var position = completed ? duration : duration > 0 ? Math.Min(encodedSeconds, duration) : encodedSeconds;
        Control<TextBlock>("ProgressPercent").Text = percent.ToString("0.0", CultureInfo.InvariantCulture) + "% · " + DisplayTime(position) + " / " + (duration > 0 ? DisplayTime(duration) : "—");
        Control<ProgressBar>("Progress").IsIndeterminate = activeJob is not null && encodedSeconds <= 0;
        if (activeJob is not null || completed)
            Control<ProgressBar>("Progress").Value = 10 * percent;
        Control<TextBlock>("ElapsedTime").Text = L.T("已用 ") + DisplayTime(clock.Elapsed.TotalSeconds);
        var speed = conversionSpeed > 0 ? conversionSpeed : encodedSeconds > 0 && clock.Elapsed.TotalSeconds > 0 ? encodedSeconds / clock.Elapsed.TotalSeconds : 0;
        var fps = currentFps > 0 ? currentFps : encodedFrames > 0 && encodedSeconds > 0 && conversionSpeed > 0
            ? encodedFrames * conversionSpeed / encodedSeconds : 0;
        Control<TextBlock>("TransferSpeed").Text = fps > 0 ? L.T($"速度 {fps:0.0} fps") : L.T("速度 —");
        var remaining = completed || activeJob is null ? "—" : duration <= 0 || speed <= 0 ? L.T("估算中") : encodedSeconds >= duration ? L.T("正在收尾") : L.T("约 ") + DisplayTime((duration - encodedSeconds) / speed);
        Control<TextBlock>("RemainingTime").Text = L.T("剩余 ") + remaining;
    }
    private void SetBusy(bool busy)
    {
        foreach (var name in new[] { "InputPath", "ChooseInput" })
            Control<Microsoft.UI.Xaml.Controls.Control>(name).IsEnabled = !busy;
        Control<Button>("ToolsButton").IsEnabled = !busy;
        UpdateEditingEnabled(!busy && media is not null && !probing);
        Control<Button>("Start").IsEnabled = !busy && media is not null && !probing;
        Control<Button>("Cancel").IsEnabled = busy;
        Control<Button>("OpenFolder").IsEnabled = !busy && File.Exists(completedOutput);
    }
    private void UpdateEditingEnabled(bool enabled)
    {
        foreach (var name in new[] { "InputKind", "OutputRange", "Purpose", "Codec", "Variant", "Container", "Transform", "Sampling", "ColorRange", "Audio", "CpuDecode" })
            Control<Microsoft.UI.Xaml.Controls.Control>(name).IsEnabled = enabled;
        Profile.IsEnabled = enabled && Value(Transform).StartsWith("log-");
        BitDepth.IsEnabled = enabled && AllowedDepths().Length > 0;
        Output.IsEnabled = enabled;
        Control<Button>("ChooseOutput").IsEnabled = enabled;
    }
    private async Task ConfigureToolsAsync()
    {
        if (activeJob is not null || dialogOpen)
            return;
        dialogOpen = true;
        var panel = new StackPanel { Spacing = 16, MinWidth = 460 };
        panel.Children.Add(new TextBlock { Text = L.T("自动查找 PATH，也可以选择自己的 FFmpeg 和 ffprobe。"), TextWrapping = TextWrapping.Wrap });
        var fields = new Dictionary<string, TextBox>();
        var toolFields = new List<(string name, string value)> { ("ffmpeg", ffmpeg), ("ffprobe", ffprobe) };
        foreach (var (name, value) in toolFields)
        {
            var grid = new Grid { ColumnSpacing = 8 };
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var box = new TextBox
            {
                Header = name + ".exe",
                Text = value,
                MinWidth = 340,
                IsSpellCheckEnabled = false,
                IsTextPredictionEnabled = false
            };
            var button = new Button { Content = L.T("浏览…"), VerticalAlignment = VerticalAlignment.Bottom };
            Grid.SetColumn(button, 1);
            grid.Children.Add(box);
            grid.Children.Add(button);
            panel.Children.Add(grid);
            fields[name] = box;
            button.Click += async (_, _) =>
            {
                var picker = new FileOpenPicker();
                picker.FileTypeFilter.Add(".exe");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
                var file = await picker.PickSingleFileAsync();
                if (file is null)
                    return;
                box.Text = file.Path;
                var peer = name == "ffmpeg" ? "ffprobe" : "ffmpeg";
                var candidate = Path.Combine(Path.GetDirectoryName(file.Path)!, peer + ".exe");
                if (fields[peer].Text == "" && File.Exists(candidate))
                    fields[peer].Text = candidate;
            };
        }
        var notice = new InfoBar { IsClosable = false, Severity = InfoBarSeverity.Error, IsOpen = false };
        panel.Children.Add(notice);
        var dialog = UiTypography.Prepare(new ContentDialog { Title = L.T("工具路径"), Content = panel, PrimaryButtonText = L.T("检测并保存"), CloseButtonText = L.T("取消"), XamlRoot = root.XamlRoot, RequestedTheme = root.RequestedTheme });
        dialog.PrimaryButtonClick += async (_, e) =>
        {
            var deferral = e.GetDeferral();
            dialog.IsPrimaryButtonEnabled = false;
            try
            {
                foreach (var (name, box) in fields)
                    await ConversionClient.CheckToolAsync(box.Text.Trim('"', ' '), name);
                ffmpeg = fields["ffmpeg"].Text.Trim('"', ' ');
                ffprobe = fields["ffprobe"].Text.Trim('"', ' ');
                settings.Ffmpeg = ffmpeg;
                settings.Ffprobe = ffprobe;
                settings.Save();

            }
            catch (Exception error) { e.Cancel = true; notice.Message = L.T(error.Message); notice.IsOpen = true; }
            finally { dialog.IsPrimaryButtonEnabled = true; deferral.Complete(); }
        };
        try
        {
            await dialog.ShowAsync();
        }
        finally { dialogOpen = false; Refresh(); }
    }
    private async Task MessageAsync(string title, string message)
    {
        if (closing || dialogOpen)
            return;
        dialogOpen = true;
        try
        {
            await UiTypography.Prepare(new ContentDialog { Title = L.T(title), Content = L.T(message), CloseButtonText = L.T("确定"), XamlRoot = root.XamlRoot, RequestedTheme = root.RequestedTheme }).ShowAsync();
        }
        finally { dialogOpen = false; }
    }
    // Hold the window open until cancellation has joined FFmpeg and cleaned
    // the temporary output. Close() is called by the completed job's finally block.
    private async void OnClosing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (closing || activeJob is null)
        {
            // Invalidate queued probe results before releasing the window.
            // A cancelled probe can still have a previously queued media event.
            closing = true;
            generation++;
            foreach (var probe in probes)
                probe.Dispose();
            return;
        }
        args.Cancel = true;
        if (dialogOpen)
            return;
        dialogOpen = true;
        try
        {
            var result = await UiTypography.Prepare(new ContentDialog { Title = L.T("取消转换并退出？"), Content = L.T("本次临时输出将清理，原片保留。"), PrimaryButtonText = L.T("取消转换并退出"), CloseButtonText = L.T("继续转换"), XamlRoot = root.XamlRoot, RequestedTheme = root.RequestedTheme }).ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                closing = true;
                if (activeJob is not null)
                    await activeJob.CancelAsync();
                else
                    Close();
            }
        }
        finally { dialogOpen = false; }
    }
}
