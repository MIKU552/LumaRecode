using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VideoConverter.Core;

namespace VideoConverter;

public sealed partial class MainWindow
{
    private HardwareEncoder[] hardwareEncoders = [];
    private MediaTools? hardwareProbeTools;
    private (DateTime Encoder, DateTime Probe) hardwareToolVersions;
    private CancellationTokenSource? hardwareProbeCancellation;
    private ComboBox HardwareChoice => Control<ComboBox>("HardwareEncoder");
    private string SelectedHardware => Value(HardwareChoice) is { Length: > 0 } selected ? selected : "qsv";

    private async Task InspectHardwareAsync()
    {
        var tools = ToolsObject();
        var versions = (File.GetLastWriteTimeUtc(tools.Ffmpeg), File.GetLastWriteTimeUtc(tools.Ffprobe));
        if (hardwareProbeTools == tools && hardwareToolVersions == versions)
        {
            return;
        }

        using var cancellation = new CancellationTokenSource();
        hardwareProbeCancellation = cancellation;
        try
        {
            SetStatus("正在检测硬件编码器…");
            hardwareEncoders = await HardwareEncoders.ProbeAsync(ConversionClient.Engine, tools, cancellation.Token);
            hardwareProbeTools = tools;
            hardwareToolVersions = versions;
            if (!closing)
            {
                SetStatus("正在读取相机元数据…");
            }
        }
        catch (Exception error) when (error is TimeoutException or InvalidDataException or IOException)
        {
            // A broken hardware trial must not prevent browsing the source or
            // exporting with the software path. Recheck on the next input.
            hardwareEncoders = [];
            hardwareProbeTools = null;
        }
        finally
        {
            hardwareProbeCancellation = null;
        }
    }

    private bool HasHardware(string codec, int depth, string? backend) => hardwareEncoders.Any(item =>
        item.Available && item.Codec == codec && item.Depth == depth && (backend is null || item.Backend == backend));

    private bool HasHardwareCodec(string codec, string? backend) =>
        OutputPolicy.DepthChoices(SourceDepth, "share", codec, Hdr ? "hdr" : "sdr")
            .Any(depth => HasHardware(codec, depth, backend));

    private void RefreshHardwareChoices(bool preserve, bool selectPreferred = false)
    {
        string[] codecOrder = ["hevc", "av1", "avc"];
        var backends = hardwareEncoders.Where(item => item.Available)
            .DistinctBy(item => item.Backend)
            .Where(item => codecOrder.Any(codec => HasHardwareCodec(codec, item.Backend)))
            .ToArray();
        var share = (ComboBoxItem)Purpose.Items[0];
        share.IsEnabled = !preserve && (media is null || backends.Length > 0);
        share.Visibility = share.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
        if (!share.IsEnabled && Value(Purpose) == "share")
        {
            Select(Purpose, "archive");
            ApplyPurposeDefaults();
        }

        bool show = media is not null && Value(Purpose) == "share" && backends.Length > 0;
        HardwareChoice.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show)
        {
            return;
        }

        var selected = SelectedHardware;
        if (!HardwareChoice.Items.OfType<ComboBoxItem>().Select(item => (string)item.Tag)
            .SequenceEqual(backends.Select(item => item.Backend)))
        {
            HardwareChoice.Items.Clear();
            foreach (var item in backends)
            {
                HardwareChoice.Items.Add(new ComboBoxItem { Tag = item.Backend, Content = item.Label });
            }
            Select(HardwareChoice, selected);
        }
        if (selectPreferred && HardwareEncoders.Select(hardwareEncoders, codecOrder,
            codec => OutputPolicy.DepthChoices(SourceDepth, "share", codec, Hdr ? "hdr" : "sdr")) is { } preferred)
        {
            Select(HardwareChoice, preferred.Backend);
            Select(Codec, preferred.Codec);
            if (audioUsesDefault)
            {
                ApplyDefaultAudio();
            }
        }
        if (!HasHardwareCodec(Value(Codec), SelectedHardware))
        {
            Select(Codec, codecOrder.First(codec => HasHardwareCodec(codec, SelectedHardware)));
            if (audioUsesDefault)
            {
                ApplyDefaultAudio();
            }
        }
    }
}
