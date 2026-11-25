using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VideoConverter.Core;
using Windows.Storage.Pickers;

namespace VideoConverter;

public sealed partial class MainWindow
{
    private async Task ChooseInputAsync()
    {
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        foreach (var type in new[] { ".mp4", ".mov", ".mkv", ".m4v", ".webm", ".avi", ".mts" })
        {
            picker.FileTypeFilter.Add(type);
        }

        var file = await picker.PickSingleFileAsync();
        if (file is not null)
        {
            Input.Text = file.Path;
            await InspectAsync();
        }
    }
    private void ClearMedia()
    {
        ResetFrameSource();
        media = null;
        sourceMedia = null;
        softwareDecodeFallback = false;
        updating = true;
        ResetSourceOptions();
        updating = false;
        Control<TextBlock>("SourceSummary").Text = L.T("尚未选择素材");
        Control<TextBlock>("SourceEvidence").Text = L.T("识别相机 Log、HDR 与常规 SDR。");
        Control<TextBlock>("SourceBadge").Text = L.T("等待添加");
        foreach (var name in new[] { "SourceVideo", "SourceColor", "SourceAudio" })
        {
            Control<TextBlock>(name).Visibility = Visibility.Collapsed;
        }

        if (Output.Text == automaticOutput)
        {
            Output.Text = "";
        }

        automaticOutput = completedOutput = "";
        completed = false;
        clock.Reset();
        encodedSeconds = encodedFrames = conversionSpeed = currentFps = 0;
        Control<ProgressBar>("Progress").Value = 0;
        Control<Button>("OpenFolder").IsEnabled = false;
        UpdateTransferStats();
        SetStatus("添加素材后开始");
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
        {
            Output.Text = file.Path;
        }
    }
    /// <summary>Probes one source asynchronously and applies only the current generation's results.</summary>
    private async Task InspectAsync(bool force = false)
    {
        await initialTools.Task;
        if (closing || activeJob is not null || probing)
        {
            return;
        }

        var path = Input.Text.Trim('"', ' ');
        if (path == "" || !force && path.Equals(MediaText("path"), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!File.Exists(path))
        {
            await MessageAsync(L.T("无法读取"), L.T("请选择有效的视频文件。"));
            return;
        }
        if (ffmpeg == "" || ffprobe == "")
        {
            await ConfigureToolsAsync();
            if (ffmpeg == "" || ffprobe == "")
            {
                return;
            }
        }
        var token = ++generation;
        ResetFrameSource();
        media = null;
        sourceMedia = null;
        softwareDecodeFallback = false;
        probing = true;
        SetStatus("正在读取相机元数据…");
        foreach (var name in new[] { "SourceVideo", "SourceColor", "SourceAudio" })
        {
            Control<TextBlock>(name).Visibility = Visibility.Collapsed;
        }

        Control<TextBlock>("SourceSummary").Text = L.T("正在识别素材…");
        Refresh();
        using var client = new ConversionClient();
        probes.Add(client);
        EncodeOptions? automatic = null;
        string? destination = null;
        var hardware = InspectHardwareAsync();
        try
        {
            await client.RunAsync(new ConversionRequest("probe", ToolsObject(), path), (type, data) =>
            {
                if (token != generation)
                {
                    return;
                }

                if (type == "media")
                {
                    sourceMedia = client.SourceMedia;
                    ApplyMedia(data!.AsObject());
                    automatic = SelectedOptions();
                    destination = Output.Text;
                }
                else if (type == "error")
                {
                    Control<TextBlock>("SourceSummary").Text = L.T("读取失败");
                    Control<TextBlock>("SourceEvidence").Text = L.T(data?.ToString());
                    SetStatus("读取失败");
                }
            });
        }
        catch (OperationCanceledException) when (closing) { }
        catch (Exception error)
        {
            if (!closing)
            {
                await MessageAsync(L.T("读取失败"), error.Message);
            }
        }
        finally
        {
            probes.Remove(client);
            probing = false;
            if (!closing)
            {
                Refresh();
            }
        }

        // Source inspection and the timeline do not need encoder sessions.
        // Late results update the defaults only while the user still has the
        // initial settings and has not started a job.
        await hardware;
        if (closing || token != generation || sourceMedia is null || activeJob is not null)
        {
            return;
        }
        if (!MediaFlag("dynamic_hdr") && automatic is { Purpose: "archive" } &&
            automatic == SelectedOptions() && Output.Text == destination &&
            hardwareEncoders.Any(item => item.Available))
        {
            updating = true;
            Select(Purpose, "share");
            ApplyPurposeDefaults();
            RefreshHardwareChoices(preserve: false, selectPreferred: true);
            updating = false;
        }
        Refresh();
    }
    // Pick defaults from the source and the available encoders. Refresh retains
    // subsequent user choices while they remain compatible.
    private void ResetSourceOptions()
    {
        Select(InputKind, "auto");
        Select(Purpose, MediaFlag("dynamic_hdr") || !hardwareEncoders.Any(item => item.Available) ? "archive" : "share");
        Select(DynamicMetadata, "preserve");
        Select(Codec, "hevc");
        Select(Variant, "standard");
        ApplyPurposeDefaults();
        Range.SelectedIndex = MediaText("kind") is "log" or "hlg" or "pq" or "raw" ? 1 : 0;
        Select(Profile, MediaText("profile"));
        if (Value(Purpose) == "share")
        {
            RefreshHardwareChoices(preserve: false, selectPreferred: true);
        }
    }

    private void ApplyMedia(JsonObject data)
    {
        updating = true;
        media = data;
        ResetSourceOptions();
        Select(Control<ComboBox>("ImageFormat"), Kind is "hlg" or "pq" or "log" or "raw" ? "ultrahdr" : "sdr");
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
        SetStatus("已识别素材，默认选项已就绪");
        updating = false;
        Refresh();
        UiTypography.Refresh(root);
        if (PhotoMode || Control<Microsoft.UI.Xaml.Controls.Primitives.ToggleButton>("ShowPreview").IsChecked == true)
        {
            _ = PreparePreviewAsync();
        }
    }
    private void UpdateMediaPresentation()
    {
        if (media is not { } data)
        {
            return;
        }

        var video = data["video"]!.AsObject();
        var name = MediaText("profile") switch
        {
            "milog" => L.T("小米 Mi-Log"),
            "pocket3" => "Pocket 3 D-Log M",
            "samsung" => "Samsung Log",
            "applelog" => "Apple Log",
            "applelog2" => "Apple Log 2",
            _ => MediaText("kind") switch { "raw" => "ProRes RAW", "hlg" => "HLG HDR", "pq" => "PQ HDR", "sdr" => "SDR", "log" => L.T("Log · 曲线待确认"), _ => L.T("色彩类型待确认") }
        };
        if (MediaFlag("dynamic_hdr"))
        {
            name = media?["dolby_vision"] is JsonObject dv ? $"Dolby Vision Profile {dv.Integer("dv_profile")}" : "HDR10+";
        }
        var pixel = video.Text("pix_fmt");
        var sampling = MediaText("source_sampling") switch
        {
            "420" => "4:2:0",
            "422" => "4:2:2",
            "444" => MediaFlag("pixel_rgb") ? "RGB" : "4:4:4",
            "gray" => L.T("灰度"),
            _ => pixel
        };
        if (MediaText("kind") == "raw")
        {
            sampling = "Bayer";
        }

        var depths = (data["component_depths"] as JsonArray ?? []).Select(item => item!.GetValue<int>()).Distinct().ToArray();
        var depth = depths.Length > 0 ? string.Join("/", depths) + L.T(" 位") : int.TryParse(video.Text("bits_per_raw_sample"), out var bits) && bits > 0 ? bits + L.T(" 位") : L.T("位深未标明");
        if (MediaText("kind") == "raw")
        {
            depth = "16" + L.T(" 位");
        }

        if (data.Flag("pixel_float"))
        {
            depth += L.T("浮点");
        }

        var duration = JsonFields.Number(data.Text("duration"));
        Control<TextBlock>("SourceSummary").Text = $"{name} · {video["width"]} × {video["height"]} · {sampling}";
        SetSourceDetail("SourceVideo", DescribeVideo(video, depth));
        SetSourceDetail("SourceColor", DescribeColors(video));
        SetSourceDetail("SourceAudio", DescribeAudio(data));
        Control<TextBlock>("SourceEvidence").Text = L.T(MediaText("evidence"));
        Control<TextBlock>("SourceBadge").Text = L.T($"{depth} · {duration:F1} 秒");
    }
    private void SetSourceDetail(string name, string text)
    {
        var control = Control<TextBlock>(name);
        control.Text = text;
        control.Visibility = Visibility.Visible;
    }
    private static string Field(JsonObject item, string key, string fallback = "") => item.Text(key, fallback);
    private static string Bitrate(JsonObject item)
    {
        return TryNumber(Field(item, "bit_rate"), out var rate) && rate > 0 ? rate >= 1_000_000 ? $"{rate / 1_000_000:0.0} Mb/s" : $"{rate / 1000:0.#} kb/s" : "";
    }
    private static string DescribeVideo(JsonObject video, string depth)
    {
        var parts = new List<string> { L.T("视频：") + Field(video, "codec_name").ToUpperInvariant() };
        var profile = Field(video, "profile");
        if (profile != "" && profile != "unknown")
        {
            parts.Add(profile);
        }

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
        {
            parts.Add(bitrate);
        }

        return string.Join(" · ", parts);
    }
    private static string DescribeColors(JsonObject video)
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
    private static string DescribeAudio(JsonObject data)
    {
        var audio = data["audio"].Objects().ToArray();
        if (audio.Length == 0)
        {
            return L.T("音轨：无");
        }

        var lines = new List<string>();
        var index = 0;
        foreach (var stream in audio)
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
            {
                parts.Insert(1, profile);
            }

            if (TryNumber(Field(stream, "sample_rate"), out var hz) && hz > 0)
            {
                parts.Add($"{hz / 1000:0.###} kHz");
            }

            var audioBits = Field(stream, "bits_per_raw_sample");
            if (!int.TryParse(audioBits, out var depth) || depth <= 0)
            {
                int.TryParse(Field(stream, "bits_per_sample"), out depth);
            }

            if (depth > 0)
            {
                parts.Add(L.T($"{depth} 位"));
            }

            var bitrate = Bitrate(stream);
            if (bitrate != "")
            {
                parts.Add(bitrate);
            }

            lines.Add(string.Join(" · ", parts));
        }
        return string.Join("\n", lines);
    }
    /// <summary>Snapshots current choices so edits cannot change the parameters of an active conversion.</summary>
}
