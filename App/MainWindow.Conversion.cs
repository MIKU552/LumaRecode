using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml.Controls;
using VideoConverter.Core;
using Windows.ApplicationModel.DataTransfer;

namespace VideoConverter;

public sealed partial class MainWindow
{
    private async Task StartAsync()
    {
        if (activeJob is not null || media is null || dialogOpen || closing)
        {
            return;
        }

        var output = Output.Text.Trim('"', ' ');
        if (!Directory.Exists(Path.GetDirectoryName(output)) || Path.GetExtension(output).ToLowerInvariant() != Suffix)
        {
            await MessageAsync(L.T("请调整输出位置"), L.T($"请选择已有目录，文件后缀应为 {Suffix}。"));
            return;
        }
        var overwrite = File.Exists(output);
        if (overwrite)
        {
            try
            {
                if (ProcessRunner.SameFile(output, MediaText("path")))
                {
                    await MessageAsync(L.T("转换失败"), L.T("不能将源文件用作输出。"));
                    return;
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                await MessageAsync(L.T("转换失败"), error.Message);
                return;
            }
            if (!await ConfirmOverwriteAsync(output))
            {
                return;
            }
        }
        var request = new ConversionRequest("convert", ToolsObject(), MediaText("path"), output,
            new EncodeOptions(Purpose: Value(Purpose), Codec: Value(Codec), Output: Hdr ? "hdr" : "sdr",
                Transform: Value(Transform), Profile: Value(Profile) == "" ? null : Value(Profile),
                Sampling: Value(Sampling), Variant: Value(Variant), Audio: Value(Audio),
                Container: Value(Container), SourceKind: Value(InputKind),
                CpuDecode: Control<CheckBox>("CpuDecode").IsChecked == true,
                ColorRange: Value(ColorRange), BitDepth: Value(BitDepth)), Overwrite: overwrite);

        using var client = new ConversionClient();
        activeJob = client;
        jobInputPath = request.Path;
        jobTerminalHandled = false;
        completed = false;
        encodedSeconds = encodedFrames = conversionSpeed = currentFps = 0;
        clock.Restart();
        clockTimer.Start();
        UpdateTransferStats();
        conversionLog.Clear();
        FlushLog();
        Control<ProgressBar>("Progress").Value = 0;
        SetBusy(true);
        SetStatus("正在启动转换…");
        try
        {
            await client.RunAsync(request, ReceiveAsync);
        }
        catch (Exception error)
        {
            SetStatus("转换失败");
            NotifyJobFinished(null, error.Message);
            await MessageAsync(L.T("转换失败"), error.Message);
        }
        finally
        {
            clock.Stop();
            clockTimer.Stop();
            activeJob = null;
            UpdateTransferStats();
            SetBusy(false);
            Refresh();
            if (closing)
            {
                Close();
            }
        }
    }
    private async Task ReceiveAsync(string type, JsonNode? data)
    {
        if (type == "log")
        {
            conversionLog.Append((data?.ToString() ?? "") + "\r\n");
            // Batch native TextBox updates while retaining every recent line.
            // Progress, terminal events and the clock also flush pending output.
            if (System.Diagnostics.Stopwatch.GetElapsedTime(lastLogUpdate).TotalMilliseconds >= 100)
            {
                FlushLog();
            }
        }
        else if (type == "progress")
        {
            if (TryNumber(data.Text("out_time_us"), out var us))
            {
                encodedSeconds = Math.Max(encodedSeconds, us / 1_000_000);
            }

            if (TryNumber(data.Text("frame"), out var frames))
            {
                encodedFrames = Math.Max(encodedFrames, frames);
            }

            if (TryNumber(data.Text("speed").TrimEnd('x'), out var rate) && rate > 0)
            {
                conversionSpeed = conversionSpeed > 0 ? .7 * conversionSpeed + .3 * rate : rate;
            }

            if (TryNumber(data.Text("fps"), out var fps))
            {
                currentFps = fps;
            }

            FlushLog();
            UpdateTransferStats();
            SetStatus($"正在转换 · {data.Text("frame", "0")} 帧");
        }
        else if (type == "status")
        {
            SetStatus(data?.ToString() ?? "");
        }
        else if (type == "done")
        {
            completed = true;
            completedOutput = data?.ToString() ?? "";
            Control<ProgressBar>("Progress").Value = 1000;
            SetStatus("转换完成 · 已核对色彩与采样格式");
            if (activeJob?.CompletedResult is { } result)
            {
                NotifyJobFinished(result, null);
            }
        }
        else if (type == "cancelled")
        {
            encodedSeconds = encodedFrames = conversionSpeed = currentFps = 0;
            Control<ProgressBar>("Progress").Value = 0;
            SetStatus("已取消");
        }
        else if (type == "error")
        {
            SetStatus("转换失败");
            Control<Expander>("LogPanel").IsExpanded = true;
            conversionLog.Append(L.T(data?.ToString()));
            FlushLog();
            NotifyJobFinished(null, data?.ToString() ?? "");
            await MessageAsync(L.T("转换失败"), data?.ToString() ?? "");
        }
        if (type is "done" or "cancelled" or "error")
        {
            FlushLog();
        }
    }
    private void NotifyJobFinished(ConversionResult? result, string? error)
    {
        if (jobTerminalHandled)
        {
            return;
        }
        jobTerminalHandled = true;
        clock.Stop();
        clockTimer.Stop();
        if (!settings.NotifyWhenFinished)
        {
            return;
        }
        var payload = result is not null
            ? JobNotificationContent.Success(jobInputPath, clock.Elapsed, result, L.T)
            : JobNotificationContent.Failure(jobInputPath, clock.Elapsed, error ?? "", L.T);
        if (!notifications.TryShow(payload, out var deliveryError))
        {
            conversionLog.Append(L.T($"Windows 通知未发送：{deliveryError}") + "\r\n");
            FlushLog();
        }
    }

    private async Task CopyLogAsync()
    {
        FlushLog();
        var text = Control<TextBox>("Log").Text;
        if (text.Length == 0)
        {
            return;
        }
        var generation = ++copyLogGeneration;
        var button = Control<Button>("CopyLog");
        var label = Control<TextBlock>("CopyLogLabel");
        try
        {
            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetText(text);
            Clipboard.SetContent(package);
            Clipboard.Flush();
            label.Text = L.T("已复制");
            ToolTipService.SetToolTip(button, L.T("已复制"));
        }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException or UnauthorizedAccessException or InvalidOperationException)
        {
            label.Text = L.T("复制失败");
            ToolTipService.SetToolTip(button, error.Message);
        }
        await Task.Delay(1800);
        if (!closing && copyLogGeneration == generation)
        {
            label.Text = L.T("复制");
            ToolTipService.SetToolTip(button, L.T("复制全部日志"));
        }
    }

    private void FlushLog()
    {
        if (!conversionLog.Changed)
        {
            return;
        }

        var text = conversionLog.Snapshot();
        Control<TextBox>("Log").Text = text;
        Control<Button>("CopyLog").IsEnabled = text.Length > 0;
        lastLogUpdate = System.Diagnostics.Stopwatch.GetTimestamp();
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
        var duration = JsonFields.Number(media.Text("duration"));
        var percent = completed ? 100 : duration > 0 ? Math.Clamp(100 * encodedSeconds / duration, 0, 99.5) : 0;
        var position = completed ? duration : duration > 0 ? Math.Min(encodedSeconds, duration) : encodedSeconds;
        Control<TextBlock>("ProgressPercent").Text = percent.ToString("0.0", CultureInfo.InvariantCulture) + "% · " + DisplayTime(position) + " / " + (duration > 0 ? DisplayTime(duration) : "—");
        Control<ProgressBar>("Progress").IsIndeterminate = activeJob is not null && encodedSeconds <= 0;
        if (activeJob is not null || completed)
        {
            Control<ProgressBar>("Progress").Value = 10 * percent;
        }

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
        {
            Control<Microsoft.UI.Xaml.Controls.Control>(name).IsEnabled = !busy;
        }

        Control<Button>("ToolsButton").IsEnabled = !busy;
        UpdateEditingEnabled(!busy && media is not null && !probing);
        Control<Button>("Start").IsEnabled = !busy && media is not null && !probing;
        Control<Button>("Cancel").IsEnabled = busy;
        Control<Button>("OpenFolder").IsEnabled = !busy && File.Exists(completedOutput);
    }
    private void UpdateEditingEnabled(bool enabled)
    {
        foreach (var name in new[] { "InputKind", "OutputRange", "Purpose", "Codec", "Variant", "Container", "Transform", "Sampling", "ColorRange", "Audio", "CpuDecode" })
        {
            Control<Microsoft.UI.Xaml.Controls.Control>(name).IsEnabled = enabled;
        }

        Profile.IsEnabled = enabled && Value(Transform).StartsWith("log-");
        BitDepth.IsEnabled = enabled && AllowedDepths().Length > 0;
        Output.IsEnabled = enabled;
        Control<Button>("ChooseOutput").IsEnabled = enabled;
    }
}
