using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media.Imaging;
using VideoConverter.Core;
using Windows.Storage.Streams;

namespace VideoConverter;

public sealed partial class MainWindow
{
    // The packet/frame index and preview buffers are created only when needed.
    // A source change cancels both operations before clearing their UI state.
    private FrameTimeline? frameTimeline;
    private Task<FrameTimeline>? timelineTask;
    private CancellationTokenSource? timelineCancellation, previewCancellation;
    private readonly SemaphoreSlim previewGate = new(1, 1);
    private bool frameUiUpdating, motionRangeEdited;
    private string previewError = "";
    private double jobDuration;
    private bool PhotoMode => Control<SelectorBar>("WorkPage").SelectedItem == Control<SelectorBarItem>("PhotoPage");
    private bool TrimVideo => !PhotoMode && Control<CheckBox>("TrimEnabled").IsChecked == true;
    private bool MotionImage => Control<CheckBox>("IncludeMotion").IsChecked == true;
    private int SelectedFrame => Math.Max(0, (int)Control<NumberBox>("FrameNumber").Value - 1);
    private string ImageFormat => Value(Control<ComboBox>("ImageFormat"));
    private FrameRange VideoFrames => ReadRange("Trim");
    private FrameRange MotionFrames => ReadRange("Motion");
    private FrameRange ReadRange(string prefix) => new((int)Control<NumberBox>(prefix + "Start").Value - 1,
        (int)Control<NumberBox>(prefix + "End").Value);
    private ImageExportOptions SelectedImageOptions() => new(SelectedFrame, ImageFormat, Value(InputKind),
        Value(Profile) is { Length: > 0 } profile ? profile : null, MotionImage, MotionFrames,
        (int)Control<Slider>("ImageQuality").Value, Value(Control<ComboBox>("MotionEncoder")), Value(Control<ComboBox>("MotionResolution")));

    private void InitializeFrameControls()
    {
        Choices(Control<ComboBox>("ImageFormat"), ("ultrahdr", "Ultra HDR · JPEG"), ("hdr", "HDR · AVIF"),
            ("sdr", "SDR · JPEG"), ("png", "SDR · PNG"));
        Choices(Control<ComboBox>("MotionEncoder"), ("archive", L.T("软件编码 · CPU")), ("share", L.T("硬件编码 · Intel")));
        Choices(Control<ComboBox>("MotionResolution"), ("source", L.T("原始分辨率")), ("1080", L.T("最高 1080p")), ("720", L.T("最高 720p")));
        Control<ComboBox>("MotionEncoder").SelectionChanged += (_, _) => { if (!updating) { Refresh(); } };
        Control<SelectorBar>("WorkPage").SelectionChanged += async (_, _) =>
        {
            Refresh();
            UpdateTransferStats();
            if (PhotoMode)
            {
                await PreparePreviewAsync();
            }
            else if (Control<Microsoft.UI.Xaml.Controls.Primitives.ToggleButton>("ShowPreview").IsChecked != true)
            {
                previewCancellation?.Cancel();
                Control<Image>("FramePreview").Source = null;
            }
        };
        Control<Microsoft.UI.Xaml.Controls.Primitives.ToggleButton>("ShowPreview").Click += async (_, _) =>
        {
            UpdateFramePresentation();
            if (Control<Microsoft.UI.Xaml.Controls.Primitives.ToggleButton>("ShowPreview").IsChecked == true)
            {
                await PreparePreviewAsync();
            }
            else
            {
                previewCancellation?.Cancel();
                Control<Image>("FramePreview").Source = null;
            }
        };
        Control<Slider>("FrameSeek").ValueChanged += (_, args) => ChangePreviewFrame((int)Math.Round(args.NewValue) - 1);
        Control<NumberBox>("FrameNumber").ValueChanged += (_, args) =>
        {
            if (double.IsFinite(args.NewValue))
            {
                ChangePreviewFrame((int)Math.Round(args.NewValue) - 1);
            }
        };
        Control<Button>("PreviousFrame").Click += (_, _) => ChangePreviewFrame(SelectedFrame - 1);
        Control<Button>("NextFrame").Click += (_, _) => ChangePreviewFrame(SelectedFrame + 1);
        foreach (var prefix in new[] { "Trim", "Motion" })
        {
            foreach (var edge in new[] { "Start", "End" })
            {
                Control<NumberBox>(prefix + edge).ValueChanged += (_, _) =>
                {
                    if (frameUiUpdating || updating)
                    {
                        return;
                    }
                    if (prefix == "Motion")
                    {
                        motionRangeEdited = true;
                    }
                    Refresh();
                    UpdateTransferStats();
                };
                Control<Button>("Set" + prefix + edge).Click += (_, _) => Control<NumberBox>(prefix + edge).Value = SelectedFrame + 1;
            }
        }
        Control<CheckBox>("TrimEnabled").Click += (_, _) => { Refresh(); UpdateTransferStats(); };
        Control<CheckBox>("IncludeMotion").Click += (_, _) => { Refresh(); UpdateTransferStats(); };
        Control<ComboBox>("ImageFormat").SelectionChanged += (_, _) => { if (!updating) { Refresh(); UpdateTransferStats(); } };
        Profile.SelectionChanged += (_, _) =>
        {
            if (!updating)
            {
                Refresh();
                QueueFramePreview();
            }
        };
        Closed += (_, _) => ResetFrameSource();
    }

    private void ResetFrameSource()
    {
        timelineCancellation?.Cancel();
        previewCancellation?.Cancel();
        timelineCancellation = null;
        previewCancellation = null;
        timelineTask = null;
        frameTimeline = null;
        previewError = "";
        motionRangeEdited = false;
        frameUiUpdating = true;
        Control<Image>("FramePreview").Source = null;
        Control<TextBlock>("PreviewStatus").Text = "";
        Control<CheckBox>("TrimEnabled").IsChecked = false;
        Control<CheckBox>("IncludeMotion").IsChecked = false;
        foreach (var name in new[] { "FrameNumber", "TrimStart", "TrimEnd", "MotionStart", "MotionEnd" })
        {
            Control<NumberBox>(name).Value = 1;
        }
        frameUiUpdating = false;
    }

    private async Task<FrameTimeline> EnsureTimelineAsync()
    {
        var source = sourceMedia ?? throw new InvalidDataException(L.T("请选择有效的视频文件。"));
        if (frameTimeline?.Matches(source.Path) == true)
        {
            return frameTimeline;
        }
        if (timelineTask is not null)
        {
            return await timelineTask;
        }
        using var cancellation = new CancellationTokenSource();
        timelineCancellation = cancellation;
        var task = FrameTimeline.LoadAsync(ToolsObject(), source, cancellation.Token);
        timelineTask = task;
        Control<ProgressRing>("PreviewBusy").IsActive = true;
        Control<TextBlock>("PreviewStatus").Text = L.T("正在读取逐帧时间轴…");
        try
        {
            var timeline = await task;
            cancellation.Token.ThrowIfCancellationRequested();
            frameTimeline = timeline;
            frameUiUpdating = true;
            Control<Slider>("FrameSeek").Maximum = Math.Max(2, timeline.Count);
            Control<Slider>("FrameSeek").Value = 1;
            foreach (var name in new[] { "FrameNumber", "TrimStart", "TrimEnd", "MotionStart", "MotionEnd" })
            {
                Control<NumberBox>(name).Maximum = timeline.Count;
            }
            Control<NumberBox>("TrimEnd").Value = timeline.Count;
            SetDefaultMotionRange();
            frameUiUpdating = false;
            return timeline;
        }
        finally
        {
            if (ReferenceEquals(timelineTask, task))
            {
                timelineTask = null;
                timelineCancellation = null;
                Control<ProgressRing>("PreviewBusy").IsActive = false;
                Refresh();
            }
        }
    }

    private async Task PreparePreviewAsync()
    {
        if (sourceMedia is null)
        {
            return;
        }
        try
        {
            await EnsureTimelineAsync();
            QueueFramePreview();
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            previewError = error.Message;
            Control<TextBlock>("PreviewStatus").Text = L.T(error.Message);
        }
    }

    private void ChangePreviewFrame(int frame)
    {
        if (frameUiUpdating || frameTimeline is null || activeJob is not null)
        {
            return;
        }
        frame = Math.Clamp(frame, 0, frameTimeline.Count - 1);
        frameUiUpdating = true;
        Control<NumberBox>("FrameNumber").Value = frame + 1;
        Control<Slider>("FrameSeek").Value = frame + 1;
        if (!motionRangeEdited)
        {
            SetDefaultMotionRange();
        }
        frameUiUpdating = false;
        Refresh();
        UpdateTransferStats();
        QueueFramePreview();
    }

    private void SetDefaultMotionRange()
    {
        if (frameTimeline is null)
        {
            return;
        }
        var selection = FrameExporter.DefaultMotionRange(frameTimeline, SelectedFrame);
        Control<NumberBox>("MotionStart").Value = selection.Start + 1;
        Control<NumberBox>("MotionEnd").Value = selection.End ?? frameTimeline.Count;
    }

    // Each scrub cancels the preceding request; the gate prevents overlapping
    // FFmpeg decoders from allocating multiple full-resolution frame buffers.
    private async void QueueFramePreview()
    {
        previewCancellation?.Cancel();
        if (sourceMedia is not { } source || frameTimeline is not { } timeline || activeJob is not null ||
            (!PhotoMode && Control<Microsoft.UI.Xaml.Controls.Primitives.ToggleButton>("ShowPreview").IsChecked != true))
        {
            return;
        }
        using var cancellation = new CancellationTokenSource();
        previewCancellation = cancellation;
        var options = SelectedImageOptions();
        var tools = ToolsObject();
        try
        {
            await Task.Delay(150, cancellation.Token);
            await previewGate.WaitAsync(cancellation.Token);
            try
            {
                Control<ProgressRing>("PreviewBusy").IsActive = true;
                var pixels = await Task.Run(() => new FrameExporter(ConversionClient.Engine).PreviewAsync(tools, source, timeline, options, cancellation.Token), cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                using var stream = new InMemoryRandomAccessStream();
                using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
                {
                    writer.WriteBytes(pixels);
                    await writer.StoreAsync();
                }
                stream.Seek(0);
                var bitmap = new BitmapImage();
                await bitmap.SetSourceAsync(stream);
                cancellation.Token.ThrowIfCancellationRequested();
                Control<Image>("FramePreview").Source = bitmap;
                previewError = "";
            }
            finally { previewGate.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!cancellation.IsCancellationRequested) { previewError = error.Message; } }
        finally
        {
            if (ReferenceEquals(previewCancellation, cancellation))
            {
                previewCancellation = null;
                Control<ProgressRing>("PreviewBusy").IsActive = false;
                UpdateFramePresentation();
            }
        }
    }

    private static string FrameTime(double seconds) => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture);

    private void UpdateFramePresentation()
    {
        bool photo = PhotoMode;
        bool enabled = sourceMedia is not null && activeJob is null && !probing;
        bool indexed = frameTimeline is not null;
        bool preview = sourceMedia is not null && (photo || Control<Microsoft.UI.Xaml.Controls.Primitives.ToggleButton>("ShowPreview").IsChecked == true);
        foreach (var name in new[] { "OutputCard", "ColorSettings", "FormatCard" })
        {
            Control<Border>(name).Visibility = photo ? Visibility.Collapsed : Visibility.Visible;
        }
        Control<Border>("PhotoSettings").Visibility = photo ? Visibility.Visible : Visibility.Collapsed;
        Control<Border>("PreviewCard").Visibility = preview ? Visibility.Visible : Visibility.Collapsed;
        Control<Microsoft.UI.Xaml.Controls.Primitives.ToggleButton>("ShowPreview").Visibility = photo ? Visibility.Collapsed : Visibility.Visible;
        Control<CheckBox>("TrimEnabled").Visibility = photo ? Visibility.Collapsed : Visibility.Visible;
        Control<Grid>("VideoTrimFields").Visibility = TrimVideo ? Visibility.Visible : Visibility.Collapsed;
        Control<Grid>("MotionFields").Visibility = photo && MotionImage ? Visibility.Visible : Visibility.Collapsed;
        Control<ComboBox>("MotionEncoder").Visibility = photo && MotionImage ? Visibility.Visible : Visibility.Collapsed;
        Control<ComboBox>("MotionEncoder").IsEnabled = enabled;
        Control<ComboBox>("MotionResolution").Visibility = photo && MotionImage ? Visibility.Visible : Visibility.Collapsed;
        Control<ComboBox>("MotionResolution").IsEnabled = enabled;
        Control<TextBlock>("MotionEncoderDetail").Visibility = photo && MotionImage ? Visibility.Visible : Visibility.Collapsed;
        if (photo && MotionImage)
        {
            string purpose = Value(Control<ComboBox>("MotionEncoder"));
            var preset = ConversionClient.Engine.MotionConfig[purpose]![ImageFormat == "sdr" ? "avc" : "hevc"]!;
            bool software = purpose == "archive";
            string encoder = preset.Text("encoder").Replace("libx", "x");
            bool mono = sourceMedia?.Audio.FirstOrDefault().Integer("channels") == 1;
            Control<TextBlock>("MotionEncoderDetail").Text = $"{encoder} · {(software ? preset.Text("preset") + " / CRF" : "TU" + preset.Text("preset") + " / ICQ")}{preset.Text("quality")} · AAC-LC / 48 kHz / {(mono ? 80 : 160)} kbps";
        }
        Control<SelectorBar>("WorkPage").IsEnabled = activeJob is null;
        foreach (var name in new[] { "ShowPreview", "ImageFormat", "ImageQuality" })
        {
            Control<Microsoft.UI.Xaml.Controls.Control>(name).IsEnabled = enabled;
        }
        foreach (var name in new[] { "FrameSeek", "FrameNumber", "PreviousFrame", "NextFrame", "TrimEnabled", "TrimStart", "TrimEnd", "SetTrimStart", "SetTrimEnd", "MotionStart", "MotionEnd", "SetMotionStart", "SetMotionEnd" })
        {
            Control<Microsoft.UI.Xaml.Controls.Control>(name).IsEnabled = enabled && indexed;
        }
        bool hdr = Kind is "hlg" or "pq" or "log" or "raw";
        var format = Control<ComboBox>("ImageFormat");
        foreach (var item in format.Items.OfType<ComboBoxItem>())
        {
            item.IsEnabled = hdr || (string)item.Tag is "sdr" or "png";
        }
        if (!hdr && ImageFormat is "hdr" or "ultrahdr")
        {
            Select(format, "sdr");
        }
        bool jpeg = ImageFormat is "sdr" or "ultrahdr";
        if (!jpeg)
        {
            Control<CheckBox>("IncludeMotion").IsChecked = false;
        }
        Control<CheckBox>("IncludeMotion").IsEnabled = enabled && indexed && jpeg;
        Control<Slider>("ImageQuality").IsEnabled = enabled && ImageFormat != "png";
        Profile.Visibility = Kind == "log" ? Visibility.Visible : Visibility.Collapsed;
        Profile.IsEnabled = enabled && Kind == "log";
        Control<TextBlock>("LocalizedElement56").Text = L.T(photo ? MotionImage ? "导出动态照片" : "导出图片" : "开始转换");
        AutomationProperties.SetName(Control<Button>("Start"), Control<TextBlock>("LocalizedElement56").Text);
        Control<TextBlock>("ImageFormatNote").Text = L.T(ImageFormat switch
        {
            "hdr" => "BT.2020 · PQ · 10/12 位 AVIF",
            "ultrahdr" => "SDR 主图与 HDR 增益图，兼容 Ultra HDR 查看器。",
            "png" => "sRGB · 无损 PNG",
            _ => "sRGB · JPEG"
        });
        string warning = "";
        if (Kind == "unknown")
        {
            warning = L.T("请手动选择输入解释。");
        }
        else if (Kind == "log" && Value(Profile) == "")
        {
            warning = L.T("请选择相机曲线。");
        }
        else if (media?["raw"] is System.Text.Json.Nodes.JsonObject raw && !raw.Flag("decoder_supported"))
        {
            warning = L.T("当前 FFmpeg 无法解码此 ProRes RAW。请先用支持该相机的 RAW 软件显影为 ProRes 或 HEVC。");
        }
        else if (photo && ImageFormat is "hdr" or "ultrahdr" && SourceDepth < 10)
        {
            warning = L.T("HDR 输出需要至少 10 位的源素材，请选择 SDR 或高位深原片。");
        }
        if (TrimVideo && !audioUsesDefault && Value(Audio) == "copy" && sourceMedia?.Audio.Any() == true)
        {
            warning = L.T("逐帧裁剪需要重新编码音轨，请选择 AAC、Opus 或 FLAC。");
        }
        if (frameTimeline is { } timeline)
        {
            var frame = Math.Clamp(SelectedFrame, 0, timeline.Count - 1);
            Control<TextBlock>("PreviewStatus").Text = previewError.Length > 0 ? L.T(previewError)
                : $"{frame + 1:N0} / {timeline.Count:N0} · {FrameTime(timeline.Position(frame))} / {FrameTime(timeline.Duration)}";
            foreach (var prefix in new[] { "Trim", "Motion" })
            {
                var range = ReadRange(prefix);
                if (range.Start >= 0 && range.Start < timeline.Count)
                {
                    Control<TextBlock>(prefix + "StartTime").Text = FrameTime(timeline.Position(range.Start));
                }
                if (range.End is > 0 && range.End <= timeline.Count)
                {
                    Control<TextBlock>(prefix + "EndTime").Text = FrameTime(timeline.Position(range.End.Value - 1));
                }
                if (prefix == "Trim" && TrimVideo || prefix == "Motion" && photo && MotionImage)
                {
                    try
                    {
                        timeline.Resolve(range);
                        if (prefix == "Motion" && (frame < range.Start || frame >= range.End))
                        {
                            warning = L.T("封面帧必须位于动态照片的视频范围内。");
                        }
                    }
                    catch (InvalidDataException error) { warning = L.T(error.Message); }
                }
            }
        }
        Control<TextBlock>("PhotoWarning").Text = media is null ? "" : warning;
        if (photo)
        {
            Control<Button>("Start").IsEnabled = enabled && indexed && warning == "";
        }
        else if (TrimVideo && warning != "")
        {
            Control<Button>("Start").IsEnabled = false;
        }
        if (root.ActualWidth > 0)
        {
            Reflow(root.ActualWidth);
        }
    }

    private void UpdateFrameOutput()
    {
        if (PhotoMode && media is not null && (Output.Text == "" || Output.Text == automaticOutput))
        {
            string source = MediaText("path");
            automaticOutput = Path.Combine(Path.GetDirectoryName(source)!, $"{Path.GetFileNameWithoutExtension(source)}_{SelectedFrame + 1:000000}_{ImageFormat}{(MotionImage ? "_MP" : "")}{Suffix}");
            Output.Text = automaticOutput;
        }
        else if (PhotoMode && MotionImage && Output.Text.Length > 0 &&
            Path.GetExtension(Output.Text).Equals(".jpg", StringComparison.OrdinalIgnoreCase) &&
            !Path.GetFileNameWithoutExtension(Output.Text).EndsWith("MP", StringComparison.Ordinal))
        {
            Output.Text = Path.Combine(Path.GetDirectoryName(Output.Text) ?? "",
                Path.GetFileNameWithoutExtension(Output.Text) + "_MP.jpg");
        }
    }
}
