using System.Text.Json.Nodes;

namespace VideoConverter.Core;

/// <summary>Constrains dynamic HDR preservation to encoders and sample representations that can carry it.</summary>
public static class DynamicHdr
{
    public static bool SupportsDolbyVision(JsonObject config)
        => config.Integer("rpu_present_flag") == 1 && config.Integer("bl_present_flag") == 1 && config.Integer("el_present_flag") == 0 && (config.Integer("dv_profile") == 5
            ? config.Integer("dv_bl_signal_compatibility_id") == 0
            : config.Integer("dv_profile") is 8 or 10 && config.Integer("dv_bl_signal_compatibility_id") is 1 or 4);
    public static bool Preserve(MediaInfo media, EncodeOptions options)
        => media.DynamicHdr && options.DynamicHdr == "preserve" && options.Output == "hdr" && options.Transform == "none";

    public static void Validate(MediaInfo media, EncodeOptions options)
    {
        if (options.DynamicHdr is not ("preserve" or "standard"))
        {
            throw new InvalidDataException("请选择保留动态 HDR 或转换为标准 HDR。");
        }
        if (!media.DynamicHdr)
        {
            return;
        }
        if (options.SourceKind != "auto" && options.SourceKind != media.Kind)
        {
            throw new InvalidDataException("动态 HDR 的输入解释由文件元数据确定。");
        }
        if (media.DolbyVision is { } dovi && (dovi.Integer("dv_profile") is not (5 or 8 or 10) || dovi.Integer("el_present_flag") != 0))
        {
            throw new InvalidDataException("当前支持单层 Dolby Vision Profile 5、8 和 10。此素材需要包含增强层处理的 Dolby Vision 工作流。");
        }
        if (media.DolbyVision is { } config && !SupportsDolbyVision(config))
        {
            throw new InvalidDataException("此 Dolby Vision 兼容层暂不受支持。");
        }
        if (!Preserve(media, options))
        {
            return;
        }
        if (options.Purpose != "archive" || options.Codec == "avc")
        {
            throw new InvalidDataException("保留动态 HDR 请选择软件归档 HEVC / AV1，或将动态 HDR 设为标准 HDR。");
        }
        if (media.Hdr10Plus && options.Codec != "hevc")
        {
            throw new InvalidDataException("保留 HDR10+ 请选择软件归档 HEVC。");
        }
        if (media.DolbyVision?.Integer("dv_profile") == 5 && options.Codec != "hevc")
        {
            throw new InvalidDataException("保留 Dolby Vision Profile 5 请选择软件归档 HEVC。");
        }
        if (options.Sampling != "420" || options.Depth != 10 || options.ColorRange != OutputPolicy.SourceRange(media))
        {
            throw new InvalidDataException("保留动态 HDR 需要原片范围、10 位和 4:2:0。");
        }
        if (media.SideData.Any(s => s.Text("side_data_type") == "Display Matrix"))
        {
            throw new InvalidDataException("此素材带画面方向变换。请选择标准 HDR，以同步转换画面和色彩。");
        }
    }

    /// <summary>Exports decoded HDR10+ frame side data to x265's JSON format.</summary>
    public static async Task ExtractAsync(MediaTools tools, MediaInfo media, string destination, CancellationToken cancellation)
    {
        if (media.Video.Text("codec_name") == "hevc" && media.Video.Text("start_pts") != "")
        {
            try
            {
                await Hdr10PlusBitstream.ExtractAsync(tools, media, destination, cancellation);
                return;
            }
            catch (Hdr10PlusBitstream.FallbackException)
            {
                // An ambiguous packet timeline or key-picture reset needs
                // decoder state to associate metadata with output pictures.
            }
        }
        // Decoded frames arrive in presentation order, including B frames and
        // AV1 show-existing frames. Parsing raw NAL/OBU order would not preserve
        // the association between a frame and its metadata.
        var args = new[] { "-v", "error", "-err_detect", "explode", "-threads", "2", "-select_streams", media.Video.Text("index"),
            "-show_frames", "-show_entries", "frame=:frame_side_data", "-of", "compact=p=1:nk=0", media.Path };
        using var probe = new System.Diagnostics.Process { StartInfo = ProcessRunner.StartInfo(tools.Ffprobe, args) };
        cancellation.ThrowIfCancellationRequested();
        probe.Start();
        using var registration = cancellation.Register(() => ProcessRunner.Stop(probe));
        var errors = Hdr10PlusMetadata.ReadErrorTailAsync(probe.StandardError);
        try
        {
            await using var output = File.Create(destination);
            await Hdr10PlusMetadata.ExportAsync(probe.StandardOutput, output, cancellation);
            await probe.WaitForExitAsync(cancellation);
            if (probe.ExitCode != 0)
            {
                throw new InvalidDataException("HDR10+ 元数据提取失败：" + await errors);
            }
        }
        finally
        {
            ProcessRunner.Stop(probe);
            await probe.WaitForExitAsync();
            await errors;
        }
    }
}
