using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LumaRecode.Native;

namespace VideoConverter.Core;

internal sealed class NativeCleanupException(string message) : IOException(message);

/// <summary>Adapts existing host models to the shared C ABI without owning media policy.</summary>
internal static class NativeBackend
{
    internal static byte[] JsonBytes(JsonNode value) => Encoding.UTF8.GetBytes(value.ToJsonString());

    internal static byte[] Snapshot(MediaInfo media, bool formatOnly = false)
    {
        var bytes = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(bytes))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema_version", 1);
            writer.WriteString("kind", media.Kind);
            writer.WriteString("profile", media.Profile);
            writer.WritePropertyName("video");
            if (formatOnly)
            {
                writer.WriteStartObject();
                foreach (string name in new[] { "pix_fmt", "bits_per_raw_sample", "bits_per_sample", "color_range", "_monochrome" })
                {
                    if (media.Video.TryGetPropertyValue(name, out var node))
                    {
                        writer.WritePropertyName(name);
                        if (node is null)
                        {
                            writer.WriteNullValue();
                        }
                        else
                        {
                            node.WriteTo(writer);
                        }
                    }
                }
                if (media.Raw is not null)
                {
                    writer.WriteStartObject("_raw");
                    writer.WriteEndObject();
                }
                writer.WriteEndObject();
            }
            else
            {
                writer.WriteStartObject();
                foreach (string name in new[] { "index", "codec_name", "pix_fmt", "width", "height", "bits_per_raw_sample", "bits_per_sample",
                    "color_primaries", "color_transfer", "color_space", "color_range", "avg_frame_rate", "r_frame_rate", "time_base", "field_order",
                    "_monochrome", "_format_start_time", "start_time", "start_pts", "_raw", "side_data_list" })
                {
                    if (media.Video.TryGetPropertyValue(name, out var node))
                    {
                        writer.WritePropertyName(name);
                        if (node is null)
                        {
                            writer.WriteNullValue();
                        }
                        else
                        {
                            node.WriteTo(writer);
                        }
                    }
                }
                writer.WriteEndObject();
                writer.WriteString("path", Path.GetFullPath(media.Path));
                writer.WriteNumber("duration", media.Duration);
                writer.WritePropertyName("frames");
                media.Frames.WriteTo(writer);
                writer.WriteStartArray("streams");
                foreach (var stream in media.Audio)
                {
                    stream.WriteTo(writer);
                }

                writer.WriteEndArray();
            }
            writer.WritePropertyName("pixel");
            media.Pixel.WriteTo(writer);
            writer.WriteEndObject();
        }
        return bytes.WrittenSpan.ToArray();
    }

    internal static void Check(NativeStatus status, string diagnostic, bool cleanupConfirmed = true,
        CancellationToken cancellation = default)
    {
        if (!cleanupConfirmed)
        {
            throw new NativeCleanupException("外部进程尚未确认回收，已保留任务文件。 " + diagnostic);
        }

        // Keep the host's translation templates; native planning owns policy.
        diagnostic = diagnostic switch
        {
            "ProRes RAW requires its calibrated source interpretation." => "ProRes RAW 使用自动识别与相机参数显影。",
            "ProRes RAW requires FFmpeg 9 or later." => "ProRes RAW 显影需要 FFmpeg 9 或更新版本。",
            "ProRes RAW is not supported by the selected decoder." => "当前 FFmpeg 无法解码此 ProRes RAW。请先用支持该相机的 RAW 软件显影为 ProRes 或 HEVC。",
            "Unknown output container." => "请选择自动、MP4 或 MKV 封装。",
            "Unknown output dynamic range." => "请选择 SDR 或 HDR 输出。",
            "Unknown audio choice." => "请选择有效的音轨格式。",
            "Fast variant requires archive AV1." => "快速备用质量档仅用于软件归档 AV1。",
            "Unknown input interpretation." => "请选择有效的输入类型。",
            "Unknown output sampling." => "请选择自动、保持原采样、4:2:0、4:2:2 或 4:4:4。",
            "Unknown sample range." => "请选择自动、保持原范围、Limited 或 Full。",
            "Unknown bit depth." => "请选择自动、8、10 或 12 位输出。",
            "Source precision and encoder cannot satisfy the requested bit depth." => "输出位深须在原片和编码器的位深上限内选择；HDR 至少需要 10 位。",
            _ => diagnostic
        };
        switch (status)
        {
            case NativeStatus.Ok:
                return;
            case NativeStatus.Cancelled:
                throw new OperationCanceledException("操作已取消。", cancellation);
            case NativeStatus.TimedOut:
                throw new TimeoutException("External tool timed out. " + diagnostic);
            case NativeStatus.NotFound:
                throw new FileNotFoundException(diagnostic);
            case NativeStatus.SourceChanged:
                throw new IOException("素材已更改，请重新添加。 " + diagnostic);
            case NativeStatus.AlreadyExists:
                throw new IOException("输出文件已存在，请更换文件名。");
            case NativeStatus.IoError:
                throw new IOException(diagnostic);
            default:
                throw new InvalidDataException(diagnostic);
        }
    }

    internal static MediaInfo FromSnapshot(ReadOnlySpan<byte> data)
    {
        var snapshot = JsonNode.Parse(data)!.AsObject();
        if (snapshot.Integer("schema_version") != 1)
        {
            throw new InvalidDataException("Unsupported native media snapshot.");
        }

        var video = snapshot["video"]!.AsObject();
        var frames = snapshot["frames"] as JsonArray ?? [];
        var tags = snapshot["tags"] as JsonObject ?? [];
        return new(snapshot.Text("path"), video, frames, tags, snapshot.Text("kind"),
            snapshot["profile"]?.GetValue<string>(), Evidence(snapshot), JsonFields.Number(snapshot.Text("duration")),
            snapshot["streams"] as JsonArray ?? [], snapshot["pixel"] as JsonObject ?? []);
    }

    internal static string Evidence(JsonObject snapshot)
    {
        var classification = snapshot["classification"];
        int index = classification.Integer("tag_index", -1);
        var tag = index >= 0 ? (snapshot["tags"] as JsonObject)?.ElementAtOrDefault(index).Value?.ToString().ToLowerInvariant() : null;
        return (MediaEvidence)classification.Integer("evidence") switch
        {
            MediaEvidence.Raw => "ProRes RAW · 相机白平衡与色彩矩阵",
            MediaEvidence.AppleLog => "Apple Log · BT.2020",
            MediaEvidence.AppleLog2 => "Apple Log 2 · Apple Wide Gamut",
            MediaEvidence.MiLog => "小米相机元数据确认 Mi-Log",
            MediaEvidence.SamsungLog => "三星相机元数据确认 Samsung Log",
            MediaEvidence.DjiDLogM => "DJI 专用元数据确认 D-Log M",
            MediaEvidence.DjiOtherDLogM => "DJI 元数据为 D-Log M，请确认机型对应的曲线",
            MediaEvidence.DjiLog => $"DJI Log 标记 ColorMode={snapshot["dji_color"].Text("mode")}，请选择对应曲线",
            MediaEvidence.DjiHlg => "DJI 专用元数据确认 HLG",
            MediaEvidence.DjiSdr => "DJI 元数据为普通色彩模式",
            MediaEvidence.CameraTag => $"厂商色彩标记 {tag}",
            MediaEvidence.LogTag => "读到了 Log 标记，具体曲线需要手动确认",
            MediaEvidence.LogSwitch => "读到了 Log 开关，具体曲线需要手动确认",
            MediaEvidence.TransferHlg => "视频传递曲线为 HLG",
            MediaEvidence.TransferPq => "视频传递曲线为 PQ",
            MediaEvidence.TransferSdr => "视频使用标准 SDR 传递曲线",
            MediaEvidence.DolbyVision => $"Dolby Vision Profile {videoDovi(snapshot).Integer("dv_profile")}",
            _ => "传递曲线未标明，请确认输入类型或选择 Log 转换"
        };
        static JsonObject? videoDovi(JsonObject source) => source["video"]?["side_data_list"].Objects()
            .Concat(source["frames"].Objects().SelectMany(frame => frame["side_data_list"].Objects()))
            .FirstOrDefault(side => side.Text("side_data_type") == "DOVI configuration record");
    }
}
