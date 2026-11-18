using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace VideoConverter.Core;

/// <summary>Reads HEVC HDR10+ SEI payloads without decoding image samples.</summary>
internal static class Hdr10PlusBitstream
{
    internal sealed class FallbackException : Exception;
    private static InvalidDataException Invalid() => new("无法完整保留此 HDR10+ 元数据：invalid or unsupported bitstream。请选择标准 HDR。");

    public static async Task ExtractAsync(MediaTools tools, MediaInfo media, string destination, CancellationToken cancellation, FrameRange? selection = null)
    {
        var directory = Path.GetDirectoryName(destination)!;
        // Store only SEI bytes and a matching size/PTS index. A separate small
        // packet index retains demuxer discard flags, including MP4 edit-list
        // preroll. Both passes read compressed packets; neither decodes pixels.
        var arguments = new[] { "-hide_banner", "-nostdin", "-n", "-xerror", "-v", "error", "-copyts", "-i", media.Path,
            "-map", "0:" + media.Video.Text("index"), "-c:v", "copy", "-copyinkf", "-copytb", "1", "-bsf:v", "hevc_mp4toannexb", "-f", "tee",
            "[f=data:bsfs/v=filter_units=pass_types=39-40]hdr10plus-sei.bin|[f=framehash:hash=crc32:bsfs/v=filter_units=pass_types=39-40]hdr10plus-sei.txt" };
        using var process = new Process { StartInfo = ProcessRunner.StartInfo(tools.Ffmpeg, arguments, directory) };
        cancellation.ThrowIfCancellationRequested();
        process.Start();
        using var registration = cancellation.Register(() => ProcessRunner.Stop(process));
        var errors = Hdr10PlusMetadata.ReadErrorTailAsync(process.StandardError);
        var output = Hdr10PlusMetadata.ReadErrorTailAsync(process.StandardOutput);
        try
        {
            await process.WaitForExitAsync(cancellation);
            if (process.ExitCode != 0)
            {
                throw new InvalidDataException("HDR10+ 元数据提取失败：" + await errors);
            }
            await IndexPacketsAsync(tools, media, directory, cancellation);
            using var reader = new PacketReader(directory, media.Video.Text("time_base"), cancellation);
            await using var json = File.Create(destination);
            await Hdr10PlusMetadata.ExportAsync(reader, json, cancellation, selection);
        }
        finally
        {
            ProcessRunner.Stop(process);
            await process.WaitForExitAsync();
            await Task.WhenAll(errors, output);
        }
    }

    private static async Task IndexPacketsAsync(MediaTools tools, MediaInfo media, string directory, CancellationToken cancellation)
    {
        var arguments = new[] { "-v", "error", "-select_streams", media.Video.Text("index"), "-show_packets",
            "-show_entries", "packet=pts,flags:packet_side_data=", "-of", "csv=p=0", media.Path };
        using var process = new Process { StartInfo = ProcessRunner.StartInfo(tools.Ffprobe, arguments) };
        cancellation.ThrowIfCancellationRequested();
        process.Start();
        using var registration = cancellation.Register(() => ProcessRunner.Stop(process));
        var errors = Hdr10PlusMetadata.ReadErrorTailAsync(process.StandardError);
        try
        {
            await using var output = File.Create(Path.Combine(directory, "hdr10plus-packets.txt"));
            await process.StandardOutput.BaseStream.CopyToAsync(output, cancellation);
            await process.WaitForExitAsync(cancellation);
            if (process.ExitCode != 0)
            {
                throw new InvalidDataException("HDR10+ 元数据提取失败：" + await errors);
            }
        }
        finally
        {
            ProcessRunner.Stop(process);
            await process.WaitForExitAsync();
            await errors;
        }
    }

    private readonly record struct Packet(long Pts, long Offset, int Size);

    private sealed class PacketReader : TextReader
    {
        private readonly FileStream bytes;
        private readonly BoundedMediaIndex<Packet> packets = new();
        private int current;

        public PacketReader(string directory, string timeBase, CancellationToken cancellation)
        {
            long offset = 0;
            var indexPath = Path.Combine(directory, "hdr10plus-sei.txt");
            if (!File.ReadLines(indexPath).Any(line => line == "#tb 0: " + timeBase))
            {
                throw new FallbackException();
            }
            using var records = Records(indexPath).GetEnumerator();
            using var payloads = File.OpenRead(Path.Combine(directory, "hdr10plus-sei.bin"));
            Packet? latest = null;
            bool present = records.MoveNext();
            foreach (var line in File.ReadLines(Path.Combine(directory, "hdr10plus-packets.txt")))
            {
                cancellation.ThrowIfCancellationRequested();
                var fields = line.Split(',');
                if (fields.Length != 2 || !long.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pts))
                {
                    throw new FallbackException();
                }
                bool discard = fields[1].Contains('D');
                bool updated = false;
                if (present && records.Current.Pts == pts)
                {
                    var (_, size) = records.Current;
                    if (size is <= 0 or > 1048576)
                    {
                        throw Invalid();
                    }
                    var data = new byte[size];
                    payloads.ReadExactly(data);
                    if (ParsePacket(data) is not null)
                    {
                        latest = new(pts, offset, size);
                        updated = true;
                    }
                    offset = checked(offset + size);
                    present = records.MoveNext();
                }
                // HEVC's HDR10+ SEI state persists in decode order. x265's
                // dhdr10-opt deliberately omits repeated records. Resolve that
                // state before sorting PTS, including discarded preroll packets.
                // A key picture without fresh metadata needs decoder reset rules.
                if (latest is null || !updated && fields[1].Contains('K'))
                {
                    throw new FallbackException();
                }
                if (!discard)
                {
                    packets.Add(latest.Value with
                    {
                        Pts = pts
                    });
                }
            }
            if (present || offset != new FileInfo(Path.Combine(directory, "hdr10plus-sei.bin")).Length)
            {
                throw Invalid();
            }
            packets.Sort((a, b) => a.Pts.CompareTo(b.Pts));
            for (int i = 1; i < packets.Count; i++)
            {
                if (packets[i - 1].Pts == packets[i].Pts)
                {
                    throw new FallbackException();
                }
            }
            bytes = File.OpenRead(Path.Combine(directory, "hdr10plus-sei.bin"));
        }

        private static IEnumerable<(long Pts, int Size)> Records(string path)
        {
            foreach (var line in File.ReadLines(path))
            {
                if (line.StartsWith('#') || string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }
                var fields = line.Split(',');
                if (fields.Length < 6 || fields[0].Trim() != "0"
                    || !long.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pts)
                    || !int.TryParse(fields[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var size))
                {
                    throw Invalid();
                }
                yield return (pts, size);
            }
        }

        public override ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (current == packets.Count)
            {
                return new((string?)null);
            }
            var packet = packets[current++];
            var data = new byte[packet.Size];
            bytes.Position = packet.Offset;
            bytes.ReadExactly(data);
            return new(ParsePacket(data) ?? throw Invalid());
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                bytes.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    private static int StartCode(ReadOnlySpan<byte> bytes, int start)
    {
        for (int i = start; i + 2 < bytes.Length; i++)
        {
            if (bytes[i] == 0 && bytes[i + 1] == 0 && bytes[i + 2] == 1)
            {
                return i;
            }
        }
        return bytes.Length;
    }

    private static string? ParsePacket(ReadOnlySpan<byte> bytes)
    {
        string? metadata = null;
        int position = StartCode(bytes, 0);
        while (position < bytes.Length)
        {
            int start = position + 3;
            position = StartCode(bytes, start);
            if (start + 2 > position)
            {
                throw Invalid();
            }
            int type = bytes[start] >> 1 & 63;
            if (type is not (39 or 40))
            {
                throw Invalid();
            }
            // Convert EBSP to RBSP before reading SEI payloadType/payloadSize.
            // A four-byte Annex B prefix leaves one trailing zero in the prior
            // unit; SEI parsing stops at rbsp_trailing_bits before reaching it.
            var rbsp = new byte[position - start - 2];
            int length = 0, zeroes = 0;
            for (int i = start + 2; i < position; i++)
            {
                byte value = bytes[i];
                if (zeroes >= 2 && value == 3)
                {
                    if (i + 1 >= position || bytes[i + 1] > 3)
                    {
                        throw Invalid();
                    }
                    zeroes = 0;
                    continue;
                }
                rbsp[length++] = value;
                zeroes = value == 0 ? zeroes + 1 : 0;
            }
            int cursor = 0;
            int Extended()
            {
                int result = 0, value;
                do
                {
                    if (cursor >= length)
                    {
                        throw Invalid();
                    }
                    value = rbsp[cursor++];
                    result = checked(result + value);
                } while (value == 255);
                return result;
            }
            while (cursor < length)
            {
                // 0x80 is a valid extended SEI payload type too. It denotes
                // rbsp_trailing_bits only when the remaining bytes are zero.
                if (rbsp[cursor] == 128 && rbsp.AsSpan(cursor + 1, length - cursor - 1).IndexOfAnyExcept((byte)0) < 0)
                {
                    break;
                }
                int payloadType = Extended(), size = Extended();
                if (size > length - cursor)
                {
                    throw Invalid();
                }
                var payload = rbsp.AsSpan(cursor, size);
                if (payloadType == 4 && payload.StartsWith((ReadOnlySpan<byte>)[0xb5, 0, 0x3c, 0, 1, 4]))
                {
                    if (metadata is not null)
                    {
                        throw Invalid();
                    }
                    metadata = ParseT35(payload);
                }
                cursor += size;
            }
        }
        return metadata;
    }

    private static unsafe string ParseT35(ReadOnlySpan<byte> payload)
    {
        if (LumaRecode.Native.NativeCore.ParseHdr10Plus(payload, out var metadata, out _) != LumaRecode.Native.NativeStatus.Ok)
        {
            throw Invalid();
        }

        // Match ffprobe's compact side-data representation so HEVC and AV1
        // share the same streaming validation and x265 JSON writer.
        var line = new StringBuilder("frame|side_data_type=HDR Dynamic Metadata SMPTE2094-40 (HDR10+)");
        void Field(string key, uint value, int denominator = 1)
        {
            line.Append('|').Append(key).Append('=').Append(value.ToString(CultureInfo.InvariantCulture));
            if (denominator != 1)
            {
                line.Append('/').Append(denominator.ToString(CultureInfo.InvariantCulture));
            }
        }

        Field("application version", metadata.ApplicationVersion);
        Field("num_windows", metadata.NumberOfWindows);
        Field("targeted_system_display_maximum_luminance", metadata.TargetedDisplayLuminance);
        for (int index = 0; index < 3; index++)
        {
            Field("maxscl", metadata.MaxScl[index], 100000);
        }
        Field("average_maxrgb", metadata.AverageRgb, 100000);
        Field("num_distribution_maxrgb_percentiles", metadata.DistributionCount);
        for (int index = 0; index < metadata.DistributionCount; index++)
        {
            Field("distribution_maxrgb_percentage", metadata.DistributionIndices[index]);
            Field("distribution_maxrgb_percentile", metadata.DistributionValues[index], 100000);
        }
        // The native parser accepts only zero for the unsupported fraction.
        Field("fraction_bright_pixels", 0, 1000);
        if (metadata.ToneMapping != 0)
        {
            Field("knee_point_x", metadata.KneePointX, 4095);
            Field("knee_point_y", metadata.KneePointY, 4095);
            Field("num_bezier_curve_anchors", metadata.AnchorCount);
            for (int index = 0; index < metadata.AnchorCount; index++)
            {
                Field("bezier_curve_anchors", metadata.Anchors[index], 1023);
            }
        }
        return line.ToString();
    }
}
