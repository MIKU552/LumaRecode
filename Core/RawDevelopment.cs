using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LumaRecode.Native;

namespace VideoConverter.Core;

/// <summary>Develops ProRes RAW using its per-frame camera calibration and scene-linear Bayer samples.</summary>
/// <remarks>Packet offsets are used only for MOV samples whose complete frame header is validated.
/// FFmpeg performs decoding and bilinear demosaicing; this class supplies color calibration and HLG rendering.</remarks>
public static class RawDevelopment
{
    private const double MatrixScale = 1024;
    // sendcmd uses FFmpeg duration syntax, which rejects exponent notation.
    private static string N(double value) => value.ToString("0.#################", CultureInfo.InvariantCulture);

    /// <summary>Reads calibration from a validated, directly addressable ProRes RAW frame.</summary>
    public static JsonObject ReadHeader(FileStream file, long position, long packetSize)
    {
        if (position < 0 || packetSize < 86 || position > file.Length - packetSize)
        {
            throw new InvalidDataException("ProRes RAW 帧位置无效。");
        }
        Span<byte> header = stackalloc byte[86];
        file.Position = position;
        file.ReadExactly(header);
        var raw = NativeMedia.ReadRawCalibration(header, (ulong)packetSize)
            ?? throw new InvalidDataException("当前 ProRes RAW 帧头或相机校准不受支持。");
        return new()
        {
            ["width"] = (int)raw.Width,
            ["height"] = (int)raw.Height,
            ["crop"] = new JsonArray(raw.Crop.Select(v => (JsonNode?)JsonValue.Create((int)v)).ToArray()),
            ["matrix"] = new JsonArray(raw.Matrix.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()),
            ["white_balance_kelvin"] = (int)raw.Kelvin,
            ["bayer_pattern"] = (int)raw.BayerPattern
        };
    }

    public static async Task<JsonObject> InspectAsync(MediaTools tools, string path, int stream, CancellationToken cancellation)
    {
        var packets = await MediaProbe.ReadAsync(tools, ["-select_streams", stream.ToString(CultureInfo.InvariantCulture),
            "-read_intervals", "%+#1", "-show_packets", "-show_entries", "packet=pos,size", "-of", "json", path], cancellation);
        var packet = packets["packets"].Objects().FirstOrDefault() ?? throw new InvalidDataException("ProRes RAW 中没有可读取的帧。");
        using var file = File.OpenRead(path);
        var raw = ReadHeader(file, long.Parse(packet.Text("pos"), CultureInfo.InvariantCulture), long.Parse(packet.Text("size"), CultureInfo.InvariantCulture));
        var version = await ProcessRunner.CaptureAsync(tools.Ffmpeg, ["-version"], cancellation, 10);
        var codecVersion = Regex.Match(version.Output, @"libavcodec\s+(\d+)\.");
        bool versionSupported = codecVersion.Success && int.Parse(codecVersion.Groups[1].Value, CultureInfo.InvariantCulture) >= 63;
        raw["decoder_version_supported"] = versionSupported;
        if (!versionSupported)
        {
            raw["decoder_supported"] = false;
            return raw;
        }
        // Decoder presence alone does not establish support for a camera's
        // Bayer layout. In particular, FFmpeg 9.0.2 rejects iPhone pattern 3.
        var trial = await ProcessRunner.CaptureAsync(tools.Ffmpeg, ["-hide_banner", "-nostdin", "-v", "error", "-xerror", "-threads:v", "2", .. PlatformDefaults.RawCpuArguments,
            "-i", path, "-map", "0:" + stream, "-frames:v", "1", "-an", "-sn", "-dn", "-f", "null", "-"], cancellation, 30);
        raw["decoder_supported"] = trial.ExitCode == 0;
        return raw;
    }

    /// <summary>Returns the orthogonal display transform to apply after RAW demosaicing.</summary>
    public static string[] Orientation(MediaInfo media)
    {
        var side = media.SideData.FirstOrDefault(s => s.Text("side_data_type") == "Display Matrix");
        if (side is null)
        {
            return [];
        }

        var values = Regex.Matches(side.Text("displaymatrix"), @"(?m)^\s*[0-9a-fA-F]+:\s*(-?\d+)\s+(-?\d+)\s+(-?\d+)")
            .SelectMany(m => new[] { m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value })
            .Select(v => long.Parse(v, CultureInfo.InvariantCulture)).ToArray();
        if (values.Length != 9)
        {
            throw new InvalidDataException("无法读取 RAW 素材的画面方向。");
        }

        return (Math.Sign(values[0]), Math.Sign(values[1]), Math.Sign(values[3]), Math.Sign(values[4])) switch
        {
            (1, 0, 0, 1) => [],
            (-1, 0, 0, 1) => ["hflip"],
            (1, 0, 0, -1) => ["vflip"],
            (-1, 0, 0, -1) => ["hflip", "vflip"],
            (0, -1, 1, 0) => ["transpose=cclock"],
            (0, 1, -1, 0) => ["transpose=clock"],
            (0, 1, 1, 0) => ["transpose=cclock_flip"],
            (0, -1, -1, 0) => ["transpose=clock_flip"],
            _ => throw new InvalidDataException("RAW 素材的画面方向需要手动校正。")
        };
    }

    private static IEnumerable<string> Coefficients(JsonObject raw)
    {
        var values = raw["matrix"]!.AsArray();
        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 4; j++)
            {
                yield return "rgb"[i].ToString() + "rgba"[j] + "=" + values[i * 4 + j];
            }
        }
    }

    /// <summary>Builds a float filter chain, applying crop only after the Bayer mosaic has been demosaiced.</summary>
    public static string Filter(JsonObject raw, string commands, int previewSize = 0)
    {
        var crop = raw["crop"]!.AsArray().Select(v => v!.GetValue<int>()).ToArray();
        string cropFilter = $"crop={raw.Integer("width") - crop[0] - crop[1]}:{raw.Integer("height") - crop[2] - crop[3]}:{crop[0]}:{crop[2]}:exact=1";
        var filters = new List<string> { "format=gbrapf32le" };
        if (previewSize > 0)
        {
            // Demosaic first: cropping the Bayer mosaic would change its phase.
            // Calibration and highlight rendering then operate on the viewport.
            filters.Add(cropFilter);
            filters.Add(FramePreview.ScaleFilter(previewSize));
        }
        filters.AddRange(["sendcmd=f=" + commands,
            "colorchannelmixer@raw=" + string.Join(':', Coefficients(raw)), "format=gbrpf32le"]);
        static string Render(int channel)
        {
            // Work in scene-linear BT.2020. Compress negative channels toward
            // neutral at constant luminance, then roll highlights into HLG.
            string common = $"st(0,r(X,Y)*{MatrixScale});st(1,g(X,Y)*{MatrixScale});st(2,b(X,Y)*{MatrixScale});"
                + "st(3,max(0,0.2627*ld(0)+0.6780*ld(1)+0.0593*ld(2)));st(4,min(ld(0),min(ld(1),ld(2))));"
                + "st(5,if(lt(ld(4),0),if(gt(ld(3),0),ld(3)/(ld(3)-ld(4)),0),1));"
                + "st(0,max(0,ld(3)+ld(5)*(ld(0)-ld(3)))*0.2674074074074074);"
                + "st(1,max(0,ld(3)+ld(5)*(ld(1)-ld(3)))*0.2674074074074074);"
                + "st(2,max(0,ld(3)+ld(5)*(ld(2)-ld(3)))*0.2674074074074074);"
                + "st(3,max(ld(0),max(ld(1),ld(2))));"
                + "st(4,if(lte(ld(3),0.45),1,(0.45+0.55*(1-exp(-(ld(3)-0.45)/0.55)))/ld(3)));"
                + $"st(5,ld({channel})*ld(4));";
            return "'" + common + "if(lte(ld(5),1/12),sqrt(3*ld(5)),0.17883277*log(12*ld(5)-0.28466892)+0.55991073)'";
        }
        filters.Add("geq=r=" + Render(0) + ":g=" + Render(1) + ":b=" + Render(2));
        if (previewSize == 0)
        {
            filters.Add(cropFilter);
        }
        filters.Add("setparams=range=full:color_primaries=bt2020:color_trc=arib-std-b67:colorspace=gbr");
        filters.Add("zscale=matrix=bt2020nc:range=limited,format=yuv444p16le");
        return string.Join(',', filters);
    }

    /// <summary>Streams packet headers into timestamped calibration commands without retaining video frames in memory.</summary>
    public static async Task PrepareAsync(MediaTools tools, MediaInfo media, string destination, double? seconds, CancellationToken cancellation, FrameWindow? window = null)
    {
        var args = new List<string> { "-v", "error", "-select_streams", media.Video.Text("index"), "-show_packets", "-show_entries", "packet=pos,size,pts", "-of", "compact=p=0:nk=0" };
        if (window is not null)
        {
            args.AddRange(["-read_intervals", N(window.SeekFrame > 0 ? window.SeekSeconds : 0) + "%" + N(window.EndSeconds + 0.1)]);
        }
        else if (seconds is not null)
        {
            args.AddRange(["-read_intervals", "%+" + N(seconds.Value + 0.1)]);
        }
        args.Add(media.Path);
        cancellation.ThrowIfCancellationRequested();
        using var file = File.OpenRead(media.Path);
        using var writer = new StreamWriter(destination, false, new UTF8Encoding(false));
        var initial = media.Video["_raw"]!.AsObject();
        var timeBase = media.Video.Text("time_base").Split('/');
        long numerator = long.Parse(timeBase[0], CultureInfo.InvariantCulture), denominator = long.Parse(timeBase[1], CultureInfo.InvariantCulture);
        string previous = "";
        var lines = new Utf8LineConsumer(line =>
        {
            cancellation.ThrowIfCancellationRequested();
            if (line.Length == 0)
            {
                return;
            }

            var fields = line.Split('|').Select(p => p.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1]);
            var raw = ReadHeader(file, long.Parse(fields["pos"], CultureInfo.InvariantCulture), long.Parse(fields["size"], CultureInfo.InvariantCulture));
            if (raw.Integer("width") != initial.Integer("width") || raw.Integer("height") != initial.Integer("height")
                || !JsonNode.DeepEquals(raw["crop"], initial["crop"]))
            {
                throw new InvalidDataException("ProRes RAW 素材在录像中改变了画面尺寸或裁切。");
            }
            string coefficients = string.Join(',', Coefficients(raw).Select(p => "colorchannelmixer@raw " + p.Replace('=', ' ')));
            if (coefficients == previous)
            {
                return;
            }

            double sourceTime = (double)long.Parse(fields["pts"], CultureInfo.InvariantCulture) * numerator / denominator;
            double time = previous.Length == 0 ? 0 : Math.Max(0, sourceTime
                - (window is { SeekFrame: > 0 } ? window.SeekSeconds : JsonFields.Number(media.Video.Text("start_time"))));
            writer.WriteLine(N(time) + " " + coefficients + ";");
            previous = coefficients;
        });
        var result = await NativeProcess.RunStreamingAsync(
            new(tools.Ffprobe, args, OutputMode: NativeCaptureMode.Discard, TimeoutMilliseconds: 0),
            (stream, bytes) =>
            {
                if (stream == 1)
                {
                    lines.Feed(bytes);
                }
            }, cancellation).ConfigureAwait(false);
        NativeBackend.Check(result.ChildExited && result.Status == NativeStatus.IoError ? NativeStatus.Ok : result.Status,
            result.Diagnostic, result.CleanupConfirmed, cancellation);
        if (result.ExitCode != 0)
        {
            throw new InvalidDataException("无法读取 ProRes RAW 帧参数：" + result.ErrorText);
        }
        lines.Feed([], flush: true);
    }
}
