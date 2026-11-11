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

public sealed class MainWindow : Window
{
    private readonly Grid root;
    private readonly UserSettings settings = UserSettings.Load();
    private readonly Appearance appearance;
    private WorkerClient? activeJob;
    private bool updating, probing, closing, dialogOpen;
    private int generation;
    private string ffmpeg = "", ffprobe = "", automaticOutput = "", completedOutput = "";
    private JsonElement? media;
    private readonly Stopwatch clock = new();
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer clockTimer;
    private double encodedSeconds, encodedFrames, conversionSpeed, currentFps;
    private bool completed;
    private readonly List<WorkerClient> probes = new();
    private TextBox Input => Control<TextBox>("InputPath");
    private TextBox Output => Control<TextBox>("Destination");
    private ComboBox Purpose => Control<ComboBox>("Purpose");
    private ComboBox Codec => Control<ComboBox>("Codec");
    private ComboBox Variant => Control<ComboBox>("Variant");
    private RadioButtons Range => Control<RadioButtons>("OutputRange");
    private ComboBox Transform => Control<ComboBox>("Transform");
    private ComboBox Profile => Control<ComboBox>("Profile");
    private ComboBox InputKind => Control<ComboBox>("InputKind");
    private ComboBox Sampling => Control<ComboBox>("Sampling");
    private ComboBox Audio => Control<ComboBox>("Audio");
    private string Kind => Value(InputKind) == "auto" ? MediaText("kind", "unknown") : Value(InputKind);
    private bool Hdr => Range.SelectedIndex == 1;
    private string Suffix => Value(Purpose) == "archive" ? ".mkv" : ".mp4";
    private int Depth => Hdr || Value(Purpose) == "archive" && Value(Codec) != "avc" ? 10 : 8;

    public MainWindow()
    {
        Title = "视频转换 · WinUI 3";
        SystemBackdrop = new MicaBackdrop();
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("VideoConverter.View.xaml.txt")!;
        using var reader = new StreamReader(stream);
        root = (Grid)XamlReader.Load(reader.ReadToEnd());
        Content = root;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(Control<Grid>("TitleDrag"));
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "app.ico"));
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
        appearance = new Appearance(root, this, settings);
        WireEvents();
        ResolveTools(); Refresh();
        root.Loaded += async (_, _) =>
        {
            if (string.IsNullOrEmpty(ffmpeg) || string.IsNullOrEmpty(ffprobe)) await ConfigureToolsAsync();
            var args = Environment.GetCommandLineArgs();
            if (args.Length > 1 && File.Exists(args[1])) { Input.Text = args[1]; await InspectAsync(); }
        };
        AppWindow.Closing += OnClosing;
    }

    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    private T Control<T>(string name) where T : class => (T)root.FindName(name);
    private static string Value(ComboBox combo) => (combo.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
    private static void Select(ComboBox combo, string? key)
    {
        var item = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == (key ?? ""));
        combo.SelectedItem = item ?? combo.Items[0];
    }
    private static void Choices(ComboBox combo, params (string key, string name)[] choices)
    {
        foreach (var (key, name) in choices) combo.Items.Add(new ComboBoxItem { Tag = key, Content = name });
        combo.SelectedIndex = 0;
    }
    private void FillChoices()
    {
        Choices(InputKind, ("auto", "自动识别"), ("sdr", "手动指定 · SDR"), ("hlg", "手动指定 · HLG HDR"), ("pq", "手动指定 · PQ HDR"), ("log", "手动指定 · Log"));
        Choices(Purpose, ("share", "快速分享 · Intel 硬件"), ("archive", "软件归档 · CPU"));
        Choices(Codec, ("hevc", "HEVC"), ("av1", "AV1"), ("avc", "H.264 / AVC"));
        Choices(Variant, ("standard", "标准实测档"), ("fast", "AV1 更快备用档"));
        Choices(Transform, ("none", "不套用 · 保留 / 标准化"), ("log-hdr", "Log → HDR（HLG）"), ("log-sdr", "Log → SDR"), ("hdr-sdr", "HDR → SDR"));
        Choices(Profile, ("", "请选择 Log 曲线…"), ("milog", "小米 Mi-Log"), ("pocket3", "Pocket 3 D-Log M"), ("samsung", "Samsung Log"));
        Choices(Sampling, ("420", "4:2:0 · 播放兼容优先"), ("422", "4:2:2 · 软件归档"));
        Choices(Audio, ("auto", "按编码档默认"), ("aac", "AAC · 256 kbps"), ("opus", "Opus · 192 kbps"), ("copy", "复制原音轨"));
    }
    private void WireEvents()
    {
        Control<Button>("ChooseInput").Click += async (_, _) => await ChooseInputAsync();
        Control<Button>("ChooseOutput").Click += async (_, _) => await ChooseOutputAsync();
        Control<Button>("ToolsButton").Click += async (_, _) => await ConfigureToolsAsync();
        Control<Button>("Start").Click += async (_, _) => await StartAsync();
        Control<Button>("Cancel").Click += async (_, _) => { if (activeJob is not null) { Control<Button>("Cancel").IsEnabled = false; SetStatus("正在取消…"); await activeJob.CancelAsync(); } };
        Control<Button>("OpenFolder").Click += (_, _) => { if (File.Exists(completedOutput)) Process.Start(new ProcessStartInfo("explorer.exe", Path.GetDirectoryName(completedOutput)!) { UseShellExecute = true }); };
        Input.KeyDown += async (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) await InspectAsync(); };
        Input.LostFocus += async (_, _) => { if (File.Exists(Input.Text.Trim('"', ' '))) await InspectAsync(); };
        foreach (var name in new[] { "Purpose", "Codec", "Variant", "Sampling", "Profile", "Audio" })
            Control<ComboBox>(name).SelectionChanged += (_, _) => Refresh();
        Range.SelectionChanged += (_, _) => Refresh();
        InputKind.SelectionChanged += (_, _) => { if (!updating && Kind == "sdr") Range.SelectedIndex = 0; Refresh(); };
        Transform.SelectionChanged += (_, _) =>
        {
            if (!updating && Value(Transform).StartsWith("log-") && Kind == "sdr") Select(InputKind, "log");
            Refresh();
        };
        var card = Control<Border>("SourceCard");
        card.DragOver += (_, e) => { if (activeJob is null && e.DataView.Contains(StandardDataFormats.StorageItems)) e.AcceptedOperation = DataPackageOperation.Copy; };
        card.Drop += async (_, e) =>
        {
            if (activeJob is not null || !e.DataView.Contains(StandardDataFormats.StorageItems)) return;
            var files = await e.DataView.GetStorageItemsAsync();
            if (files.Count == 1) { Input.Text = files[0].Path; await InspectAsync(); }
        };
        root.SizeChanged += (_, e) => Reflow(e.NewSize.Width < 960);
    }
    private void Reflow(bool narrow)
    {
        var grid = Control<Grid>("SettingsGrid");
        while (grid.RowDefinitions.Count < 3) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.ColumnDefinitions[0].MinWidth = narrow ? 0 : 380;
        grid.ColumnDefinitions[1].MinWidth = narrow ? 0 : 300;
        grid.ColumnDefinitions[0].Width = new GridLength(narrow ? 1 : 3, GridUnitType.Star);
        grid.ColumnDefinitions[1].Width = narrow ? new GridLength(0) : new GridLength(2, GridUnitType.Star);
        Grid.SetColumnSpan((FrameworkElement)grid.Children[0], narrow ? 1 : 2);
        Grid.SetColumn((FrameworkElement)grid.Children[2], narrow ? 0 : 1);
        Grid.SetRow((FrameworkElement)grid.Children[2], narrow ? 2 : 1);
    }
    private string MediaText(string key, string fallback = "") => media is { } item && item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : fallback;
    private void SetStatus(string text) => Control<TextBlock>("Status").Text = text;
    private object ToolsObject() => new { ffmpeg, ffprobe };
    private void ResolveTools()
    {
        ffmpeg = WorkerClient.FindTool("ffmpeg", settings.Ffmpeg);
        ffprobe = WorkerClient.FindTool("ffprobe", settings.Ffprobe);
    }
    private void Refresh()
    {
        if (updating) return;
        updating = true;
        try
        {
            if (Kind == "sdr" && Hdr) Range.SelectedIndex = 0;
            Control<RadioButton>("HdrChoice").IsEnabled = Kind != "sdr";
            if (Hdr && Value(Codec) == "avc") Select(Codec, "hevc");
            foreach (var item in Codec.Items.OfType<ComboBoxItem>()) item.IsEnabled = !Hdr || (string)item.Tag != "avc";
            var can422 = Value(Purpose) == "archive" && Value(Codec) != "av1";
            ((ComboBoxItem)Sampling.Items[1]).IsEnabled = can422;
            if (!can422) Select(Sampling, "420");
            var fast = Value(Purpose) == "archive" && Value(Codec) == "av1";
            Variant.Visibility = fast ? Visibility.Visible : Visibility.Collapsed;
            if (!fast) Select(Variant, "standard");
            string[] allowed = Kind switch {
                "hlg" or "pq" => Hdr ? ["none"] : ["hdr-sdr"],
                "log" => Hdr ? ["log-hdr"] : ["log-sdr"],
                _ => Hdr ? ["log-hdr"] : ["none", "log-sdr"] };
            foreach (var item in Transform.Items.OfType<ComboBoxItem>()) item.IsEnabled = allowed.Contains((string)item.Tag);
            if (!allowed.Contains(Value(Transform))) Select(Transform, allowed[0]);
            Profile.IsEnabled = Value(Transform).StartsWith("log-");
            Profile.Visibility = Profile.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
            var sampling = Value(Sampling) == "420" ? "4:2:0" : "4:2:2";
            var curve = Kind == "pq" && !Profile.IsEnabled ? "PQ" : "HLG";
            Control<TextBlock>("TargetNote").Text = Hdr ? $"BT.2020 {curve} · {Depth} 位 · {sampling}" : $"Rec.709 · {Depth} 位 · {sampling}";
            var codecName = Value(Codec) switch { "av1" => "AV1", "avc" => "H.264", _ => "HEVC" };
            Control<TextBlock>("PresetName").Text = $"{(Hdr ? "HDR" : "SDR")} · {codecName} · {(Value(Purpose) == "share" ? "快速分享" : "软件归档")}";
            var quality = Value(Purpose) == "share" ? Value(Codec) switch { "av1" => "av1_qsv · TU4 / ICQ26", "avc" => "h264_qsv · TU4 / ICQ22", _ => "hevc_qsv · TU4 / ICQ24" }
                : Value(Codec) switch { "av1" => Value(Variant) == "fast" ? "SVT-AV1 · preset6 / CRF32 / lp4" : "SVT-AV1 · preset4 / CRF32", "avc" => "x264 · medium / CRF21", _ => "x265 · medium / CRF23" };
            Control<TextBlock>("PresetDetail").Text = $"{quality}\n{Depth} 位 {sampling} · {Suffix[1..].ToUpperInvariant()}";
            Control<TextBlock>("TransformNote").Text = Value(Transform) switch {
                "log-hdr" => "使用对应相机的曲线，转换为 10 位 HLG。",
                "log-sdr" => "解码对应 Log，并将高光映射到 Rec.709。",
                "hdr-sdr" => "按 HLG / PQ 原片做色调映射，输出 Rec.709。",
                _ => "保留原片动态范围，按目标格式标准化色彩。" };
            var warning = "";
            if (string.IsNullOrEmpty(ffmpeg) || string.IsNullOrEmpty(ffprobe)) warning = "未找到 FFmpeg / ffprobe，请设置工具路径。";
            else if (media is not null && Profile.IsEnabled && Value(Profile) == "") warning = "请确认相机的 Log 曲线。";
            else if (media is not null && Kind == "unknown" && !Profile.IsEnabled) warning = "没有可靠的曲线标记，请确认输入解释。";
            else if (media is { } m && Profile.IsEnabled && !System.Text.RegularExpressions.Regex.IsMatch(m.GetProperty("video").GetProperty("pix_fmt").GetString() ?? "", "10|12|16")) warning = "Log 转换需要对应相机的高位深原片。";
            var notice = Control<InfoBar>("Notice"); notice.Message = warning; notice.IsOpen = warning != "";
            Control<Button>("Start").IsEnabled = media is not null && !probing && activeJob is null && warning == "";
            if (media is not null && (Output.Text == "" || Output.Text == automaticOutput))
            {
                var source = MediaText("path");
                automaticOutput = Path.Combine(Path.GetDirectoryName(source)!, $"{Path.GetFileNameWithoutExtension(source)}_{(Hdr ? "HDR" : "SDR")}_{Value(Codec)}_{Value(Purpose)}{Suffix}");
                Output.Text = automaticOutput;
            }
        }
        finally { updating = false; }
    }
    private async Task ChooseInputAsync()
    {
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        foreach (var type in new[] { ".mp4", ".mov", ".mkv", ".m4v", ".webm", ".avi", ".mts" }) picker.FileTypeFilter.Add(type);
        var file = await picker.PickSingleFileAsync();
        if (file is not null) { Input.Text = file.Path; await InspectAsync(); }
    }
    private async Task ChooseOutputAsync()
    {
        var picker = new FileSavePicker { SuggestedFileName = Path.GetFileNameWithoutExtension(Output.Text) };
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        picker.FileTypeChoices.Add(Suffix[1..].ToUpperInvariant(), new[] { Suffix });
        var file = await picker.PickSaveFileAsync();
        if (file is not null) Output.Text = file.Path;
    }
    private async Task InspectAsync()
    {
        if (activeJob is not null || probing) return;
        var path = Input.Text.Trim('"', ' ');
        if (path == "" || path.Equals(MediaText("path"), StringComparison.OrdinalIgnoreCase)) return;
        if (!File.Exists(path)) { await MessageAsync("无法读取", "请选择有效的视频文件。"); return; }
        if (ffmpeg == "" || ffprobe == "") { await ConfigureToolsAsync(); if (ffmpeg == "" || ffprobe == "") return; }
        var token = ++generation;
        media = null; probing = true; SetStatus("正在读取相机元数据…");
        Control<TextBlock>("SourceSummary").Text = "正在识别素材…"; Refresh();
        using var worker = new WorkerClient(); probes.Add(worker);
        try
        {
            await worker.RunAsync(new { action = "probe", tools = ToolsObject(), path }, (type, data) =>
            {
                if (token != generation) return;
                if (type == "media") ApplyMedia(data);
                else if (type == "error") { Control<TextBlock>("SourceSummary").Text = "读取失败"; Control<TextBlock>("SourceEvidence").Text = data.GetString(); SetStatus("读取失败"); }
            });
        }
        catch (Exception error) { if (!closing) await MessageAsync("读取失败", error.Message); }
        finally { probes.Remove(worker); probing = false; Refresh(); }
    }
    private void ApplyMedia(JsonElement data)
    {
        updating = true; media = data; Select(InputKind, "auto"); Select(Purpose, "share"); Select(Codec, "hevc");
        Select(Variant, "standard"); Select(Sampling, "420"); Select(Audio, "auto");
        Range.SelectedIndex = MediaText("kind") is "log" or "hlg" or "pq" ? 1 : 0;
        Select(Profile, MediaText("profile")); Output.Text = ""; automaticOutput = "";
        var video = data.GetProperty("video");
        var name = MediaText("profile") switch { "milog" => "小米 Mi-Log", "pocket3" => "Pocket 3 D-Log M", "samsung" => "Samsung Log", _ => MediaText("kind") switch { "hlg" => "HLG HDR", "pq" => "PQ HDR", "sdr" => "SDR", "log" => "Log · 曲线待确认", _ => "色彩类型待确认" } };
        var pixel = video.GetProperty("pix_fmt").GetString() ?? "";
        var sampling = pixel.Contains("422") ? "4:2:2" : pixel.Contains("420") || pixel == "p010le" ? "4:2:0" : pixel;
        var depth = System.Text.RegularExpressions.Regex.IsMatch(pixel, "10|12|16") ? "高位深" : "8 位";
        var duration = data.GetProperty("duration").GetDouble();
        completed = false; clock.Reset(); encodedSeconds = encodedFrames = conversionSpeed = currentFps = 0;
        completedOutput = ""; Control<Button>("OpenFolder").IsEnabled = false;
        Control<ProgressBar>("Progress").Value = 0; UpdateTransferStats();
        Control<TextBlock>("SourceSummary").Text = $"{name} · {video.GetProperty("width")} × {video.GetProperty("height")} · {sampling}";
        Control<TextBlock>("SourceEvidence").Text = MediaText("evidence");
        Control<TextBlock>("SourceBadge").Text = $"{depth} · {duration:F1} 秒";
        SetStatus("已识别素材，默认选项已就绪");
        updating = false; Refresh();
    }
    private async Task StartAsync()
    {
        if (activeJob is not null || media is null) return;
        var output = Output.Text.Trim('"', ' ');
        if (File.Exists(output)) { await MessageAsync("请更换输出位置", "输出文件已存在，程序不会覆盖文件。"); return; }
        if (!Directory.Exists(Path.GetDirectoryName(output)) || Path.GetExtension(output).ToLowerInvariant() != Suffix)
        { await MessageAsync("请调整输出位置", $"请选择已有目录，文件后缀应为 {Suffix}。"); return; }
        var request = new { action = "convert", tools = ToolsObject(), path = MediaText("path"), output,
            options = new { purpose = Value(Purpose), codec = Value(Codec), output = Hdr ? "hdr" : "sdr", transform = Value(Transform),
                profile = Value(Profile) == "" ? null : Value(Profile), sampling = Value(Sampling), variant = Value(Variant),
                audio = Value(Audio), source_kind = Value(InputKind), cpu_decode = Control<CheckBox>("CpuDecode").IsChecked == true } };
        using var worker = new WorkerClient(); activeJob = worker;
        completed = false; encodedSeconds = encodedFrames = conversionSpeed = currentFps = 0;
        clock.Restart(); clockTimer.Start(); UpdateTransferStats();
        Control<TextBox>("Log").Text = ""; Control<ProgressBar>("Progress").Value = 0; SetBusy(true); SetStatus("正在启动转换…");
        try { await worker.RunAsync(request, Receive); }
        catch (Exception error) { await MessageAsync("转换失败", error.Message); SetStatus("转换失败"); }
        finally { clock.Stop(); clockTimer.Stop(); activeJob = null; UpdateTransferStats(); SetBusy(false); Refresh(); if (closing) Close(); }
    }
    private async void Receive(string type, JsonElement data)
    {
        if (type == "log")
        {
            var log = Control<TextBox>("Log"); log.Text += data.GetString() + "\r\n";
            if (log.Text.Length > 40000) log.Text = log.Text[^30000..];
        }
        else if (type == "progress")
        {
            if (data.TryGetProperty("out_time_us", out var time) && TryNumber(time.ToString(), out var us)) encodedSeconds = Math.Max(encodedSeconds, us / 1_000_000);
            if (data.TryGetProperty("frame", out var count) && TryNumber(count.ToString(), out var frames)) encodedFrames = Math.Max(encodedFrames, frames);
            if (data.TryGetProperty("speed", out var speed) && TryNumber(speed.ToString().TrimEnd('x'), out var rate) && rate > 0)
                conversionSpeed = conversionSpeed > 0 ? .7 * conversionSpeed + .3 * rate : rate;
            if (data.TryGetProperty("fps", out var fps) && TryNumber(fps.ToString(), out var framesPerSecond)) currentFps = framesPerSecond;
            UpdateTransferStats();
            SetStatus($"正在转换 · {(data.TryGetProperty("frame", out var frame) ? frame.ToString() : "0")} 帧");
        }
        else if (type == "status") SetStatus(data.GetString() ?? "");
        else if (type == "done") { completed = true; completedOutput = data.GetString() ?? ""; Control<ProgressBar>("Progress").Value = 1000; SetStatus("转换完成 · 已核对色彩与采样格式"); }
        else if (type == "cancelled") { encodedSeconds = encodedFrames = conversionSpeed = currentFps = 0; Control<ProgressBar>("Progress").Value = 0; SetStatus("已取消"); }
        else if (type == "error") { SetStatus("转换失败"); Control<Expander>("LogPanel").IsExpanded = true; Control<TextBox>("Log").Text += data.GetString(); await MessageAsync("转换失败", data.GetString() ?? ""); }
    }
    private static bool TryNumber(string text, out double value) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    private static string DisplayTime(double seconds)
    {
        var value = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, 359999999));
        return value.TotalHours >= 1 ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}" : $"{value.Minutes:00}:{value.Seconds:00}";
    }
    private void UpdateTransferStats()
    {
        var duration = media is { } item ? item.GetProperty("duration").GetDouble() : 0;
        var percent = completed ? 100 : duration > 0 ? Math.Clamp(100 * encodedSeconds / duration, 0, 99.5) : 0;
        var position = completed ? duration : duration > 0 ? Math.Min(encodedSeconds, duration) : encodedSeconds;
        Control<TextBlock>("ProgressPercent").Text = percent.ToString("0.0", CultureInfo.InvariantCulture) + "% · " + DisplayTime(position) + " / " + (duration > 0 ? DisplayTime(duration) : "—");
        Control<ProgressBar>("Progress").IsIndeterminate = activeJob is not null && encodedSeconds <= 0;
        if (activeJob is not null || completed) Control<ProgressBar>("Progress").Value = 10 * percent;
        Control<TextBlock>("ElapsedTime").Text = "已用 " + DisplayTime(clock.Elapsed.TotalSeconds);
        var speed = conversionSpeed > 0 ? conversionSpeed : encodedSeconds > 0 && clock.Elapsed.TotalSeconds > 0 ? encodedSeconds / clock.Elapsed.TotalSeconds : 0;
        var fps = currentFps > 0 ? currentFps : encodedFrames > 0 && encodedSeconds > 0 && conversionSpeed > 0
            ? encodedFrames * conversionSpeed / encodedSeconds : 0;
        Control<TextBlock>("TransferSpeed").Text = fps > 0 ? $"速度 {fps:0.0} fps" : "速度 —";
        var remaining = completed || activeJob is null ? "—" : duration <= 0 || speed <= 0 ? "估算中" : encodedSeconds >= duration ? "正在收尾" : "约 " + DisplayTime((duration - encodedSeconds) / speed);
        Control<TextBlock>("RemainingTime").Text = "剩余 " + remaining;
    }
    private void SetBusy(bool busy)
    {
        foreach (var name in new[] { "InputPath", "ChooseInput", "InputKind", "OutputRange", "Purpose", "Codec", "Variant", "Transform", "Profile", "Sampling", "Audio", "CpuDecode" })
            Control<Microsoft.UI.Xaml.Controls.Control>(name).IsEnabled = !busy;
        Control<Button>("ToolsButton").IsEnabled = !busy;
        Output.IsEnabled = !busy; Control<Button>("ChooseOutput").IsEnabled = !busy;
        Control<Button>("Start").IsEnabled = !busy;
        Control<Button>("Cancel").IsEnabled = busy;
        Control<Button>("OpenFolder").IsEnabled = !busy && File.Exists(completedOutput);
    }
    private async Task ConfigureToolsAsync()
    {
        if (activeJob is not null || dialogOpen) return;
        dialogOpen = true;
        var panel = new StackPanel { Spacing = 16, MinWidth = 460 };
        panel.Children.Add(new TextBlock { Text = "自动查找 PATH，也可以选择自己的 FFmpeg 和 ffprobe。", TextWrapping = TextWrapping.Wrap });
        var fields = new Dictionary<string, TextBox>();
        foreach (var (name, value) in new[] { ("ffmpeg", ffmpeg), ("ffprobe", ffprobe) })
        {
            var grid = new Grid { ColumnSpacing = 8 }; grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var box = new TextBox { Header = name + ".exe", Text = value, MinWidth = 340 };
            var button = new Button { Content = "浏览…", VerticalAlignment = VerticalAlignment.Bottom };
            Grid.SetColumn(button, 1); grid.Children.Add(box); grid.Children.Add(button); panel.Children.Add(grid); fields[name] = box;
            button.Click += async (_, _) =>
            {
                var picker = new FileOpenPicker(); picker.FileTypeFilter.Add(".exe"); WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
                var file = await picker.PickSingleFileAsync();
                if (file is null) return; box.Text = file.Path;
                var peer = name == "ffmpeg" ? "ffprobe" : "ffmpeg"; var candidate = Path.Combine(Path.GetDirectoryName(file.Path)!, peer + ".exe");
                if (fields[peer].Text == "" && File.Exists(candidate)) fields[peer].Text = candidate;
            };
        }
        var notice = new InfoBar { IsClosable = false, Severity = InfoBarSeverity.Error, IsOpen = false }; panel.Children.Add(notice);
        var dialog = new ContentDialog { Title = "工具路径", Content = panel, PrimaryButtonText = "检测并保存", CloseButtonText = "取消", XamlRoot = root.XamlRoot, RequestedTheme = root.RequestedTheme };
        dialog.PrimaryButtonClick += async (_, e) =>
        {
            var deferral = e.GetDeferral(); dialog.IsPrimaryButtonEnabled = false;
            try
            {
                foreach (var (name, box) in fields) await WorkerClient.CheckToolAsync(box.Text.Trim('"', ' '), name);
                ffmpeg = fields["ffmpeg"].Text.Trim('"', ' '); ffprobe = fields["ffprobe"].Text.Trim('"', ' ');
                settings.Ffmpeg = ffmpeg; settings.Ffprobe = ffprobe; settings.Save();
            }
            catch (Exception error) { e.Cancel = true; notice.Message = error.Message; notice.IsOpen = true; }
            finally { dialog.IsPrimaryButtonEnabled = true; deferral.Complete(); }
        };
        try { await dialog.ShowAsync(); } finally { dialogOpen = false; Refresh(); }
    }
    private async Task MessageAsync(string title, string message)
    {
        if (closing || dialogOpen) return;
        dialogOpen = true;
        try { await new ContentDialog { Title = title, Content = message, CloseButtonText = "确定", XamlRoot = root.XamlRoot, RequestedTheme = root.RequestedTheme }.ShowAsync(); }
        finally { dialogOpen = false; }
    }
    private async void OnClosing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (closing || activeJob is null) { foreach (var probe in probes) probe.Dispose(); return; }
        args.Cancel = true;
        if (dialogOpen) return;
        dialogOpen = true;
        try
        {
            var result = await new ContentDialog { Title = "取消转换并退出？", Content = "本次临时输出将清理，原片保留。", PrimaryButtonText = "取消转换并退出", CloseButtonText = "继续转换", XamlRoot = root.XamlRoot, RequestedTheme = root.RequestedTheme }.ShowAsync();
            if (result == ContentDialogResult.Primary) { closing = true; if (activeJob is not null) await activeJob.CancelAsync(); else Close(); }
        }
        finally { dialogOpen = false; }
    }
}
