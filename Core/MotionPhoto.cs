using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace VideoConverter.Core;

public sealed record MotionPhotoInfo(long VideoOffset, long VideoLength, long PresentationTimestampUs, bool UltraHdr);

/// <summary>Google Motion Photo 1.0 JPEG packaging, including Ultra HDR gain-map entries.</summary>
public static class MotionPhoto
{
    private static readonly byte[] xmpPrefix = "http://ns.adobe.com/xap/1.0/\0"u8.ToArray();
    private static readonly XNamespace rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private static readonly XNamespace camera = "http://ns.google.com/photos/1.0/camera/";
    private static readonly XNamespace container = "http://ns.google.com/photos/1.0/container/";
    private static readonly XNamespace item = "http://ns.google.com/photos/1.0/container/item/";
    private readonly record struct Segment(int Start, int Length, byte Marker)
    {
        public int Payload => Start + 4;
    }
    private sealed record Jpeg(int Length, List<Segment> Segments);
    private static InvalidDataException Invalid() => new("动态照片的 JPEG、增益图或容器索引无效。");

    private static Jpeg ParseJpeg(ReadOnlySpan<byte> data)
    {
        if (!data.StartsWith((ReadOnlySpan<byte>)[255, 216]))
        {
            throw Invalid();
        }
        var segments = new List<Segment>();
        int position = 2;
        while (position < data.Length)
        {
            if (data[position] != 255 || position + 1 >= data.Length)
            {
                throw Invalid();
            }

            position++;
            while (position < data.Length && data[position] == 255)
            {
                position++;
            }
            if (position >= data.Length)
            {
                throw Invalid();
            }
            byte marker = data[position++];
            int start = position - 2;
            if (marker == 217)
            {
                return new(position, segments);
            }
            if (marker is 0 or 216 || position + 2 > data.Length)
            {
                throw Invalid();
            }
            int length = BinaryPrimitives.ReadUInt16BigEndian(data[position..]);
            if (length < 2 || length > data.Length - position)
            {
                throw Invalid();
            }
            segments.Add(new(start, position + length - start, marker));
            position += length;
            if (marker != 218)
            {
                continue;
            }
            // Entropy-coded bytes may contain FF00 escapes and restart
            // markers. Progressive JPEGs can have more than one SOS segment.
            while (position < data.Length)
            {
                if (data[position] != 255)
                {
                    position++;
                    continue;
                }
                int next = position + 1;
                while (next < data.Length && data[next] == 255)
                {
                    next++;
                }
                if (next >= data.Length)
                {
                    throw Invalid();
                }
                if (data[next] == 0 || data[next] is >= 208 and <= 215)
                {
                    position = next + 1;
                    continue;
                }
                break;
            }
        }
        throw Invalid();
    }

    private static XDocument ReadXmp(ReadOnlySpan<byte> bytes)
    {
        using var text = new StringReader(Encoding.UTF8.GetString(bytes));
        using var reader = XmlReader.Create(text, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 65536 });
        return XDocument.Load(reader);
    }

    private static XElement MediaItem(string semantic, string mime, long? length = null)
        => new(rdf + "li", new XAttribute(rdf + "parseType", "Resource"),
            new XElement(container + "Item", new XAttribute(item + "Semantic", semantic), new XAttribute(item + "Mime", mime),
                length is { } size ? new XAttribute(item + "Length", size) : null));

    private static byte[] AddMetadata(ReadOnlySpan<byte> jpeg, long videoLength, long timestampUs)
    {
        var primary = ParseJpeg(jpeg);
        int gainLength = jpeg.Length - primary.Length;
        if (gainLength != 0 && ParseJpeg(jpeg[primary.Length..]).Length != gainLength)
        {
            throw Invalid();
        }
        var xmpList = new List<Segment>();
        foreach (var segment in primary.Segments)
        {
            if (segment.Marker == 225 && jpeg.Slice(segment.Payload, segment.Length - 4).StartsWith(xmpPrefix))
            {
                xmpList.Add(segment);
            }
        }
        var xmpSegments = xmpList.ToArray();
        if (xmpSegments.Length > 1)
        {
            throw Invalid();
        }
        var previous = xmpSegments.FirstOrDefault();
        XNamespace x = "adobe:ns:meta/";
        var document = xmpSegments.Length == 0 ? new XDocument(new XElement(x + "xmpmeta", new XAttribute(XNamespace.Xmlns + "x", x),
            new XElement(rdf + "RDF", new XAttribute(XNamespace.Xmlns + "rdf", rdf), new XElement(rdf + "Description", new XAttribute(rdf + "about", "")))))
            : ReadXmp(jpeg.Slice(previous.Payload + xmpPrefix.Length, previous.Length - 4 - xmpPrefix.Length));
        var descriptions = document.Descendants(rdf + "Description").ToArray();
        var description = descriptions.FirstOrDefault() ?? throw Invalid();
        foreach (var node in descriptions)
        {
            node.Attributes().Where(a => a.Name.Namespace == camera).Remove();
            node.Elements(container + "Directory").Remove();
        }
        description.SetAttributeValue(XNamespace.Xmlns + "Camera", camera);
        description.SetAttributeValue(XNamespace.Xmlns + "Container", container);
        description.SetAttributeValue(XNamespace.Xmlns + "Item", item);
        description.SetAttributeValue(camera + "MotionPhoto", 1);
        description.SetAttributeValue(camera + "MotionPhotoVersion", 1);
        description.SetAttributeValue(camera + "MotionPhotoPresentationTimestampUs", timestampUs);
        var sequence = new XElement(rdf + "Seq", MediaItem("Primary", "image/jpeg"));
        if (gainLength != 0)
        {
            sequence.Add(MediaItem("GainMap", "image/jpeg", gainLength));
        }
        sequence.Add(MediaItem("MotionPhoto", "video/mp4", videoLength));
        description.Add(new XElement(container + "Directory", sequence));
        byte[] xml = Encoding.UTF8.GetBytes(document.ToString(SaveOptions.DisableFormatting));
        int segmentLength = checked(2 + xmpPrefix.Length + xml.Length);
        if (segmentLength > ushort.MaxValue)
        {
            throw Invalid();
        }
        using var output = new MemoryStream();
        int insertion = xmpSegments.Length == 0 ? 2 : previous.Start;
        output.Write(jpeg[..insertion]);
        output.WriteByte(255);
        output.WriteByte(225);
        output.WriteByte((byte)(segmentLength >> 8));
        output.WriteByte((byte)segmentLength);
        output.Write(xmpPrefix);
        output.Write(xml);
        int after = xmpSegments.Length == 0 ? insertion : previous.Start + previous.Length;
        output.Write(jpeg[after..primary.Length]);
        var rewritten = output.ToArray();
        if (gainLength != 0)
        {
            PatchMpf(rewritten, gainLength);
        }
        output.Position = 0;
        output.Write(rewritten);
        output.Write(jpeg[primary.Length..]);
        return output.ToArray();
    }

    private static void PatchMpf(byte[] jpeg, int gainLength)
    {
        var segment = ParseJpeg(jpeg).Segments.Single(s => s.Marker == 226 && jpeg.AsSpan(s.Payload, s.Length - 4).StartsWith("MPF\0"u8));
        int tiff = segment.Payload + 4, limit = segment.Start + segment.Length;
        bool little = jpeg.AsSpan(tiff, 2).SequenceEqual("II"u8);
        if (!little && !jpeg.AsSpan(tiff, 2).SequenceEqual("MM"u8))
        {
            throw Invalid();
        }
        uint U32(int offset)
        {
            if (offset < tiff || offset > limit - 4)
            {
                throw Invalid();
            }
            return little ? BinaryPrimitives.ReadUInt32LittleEndian(jpeg.AsSpan(offset)) : BinaryPrimitives.ReadUInt32BigEndian(jpeg.AsSpan(offset));
        }
        ushort U16(int offset) => little ? BinaryPrimitives.ReadUInt16LittleEndian(jpeg.AsSpan(offset)) : BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(offset));
        void Put(int offset, uint value)
        {
            if (little)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(jpeg.AsSpan(offset), value);
            }
            else
            {
                BinaryPrimitives.WriteUInt32BigEndian(jpeg.AsSpan(offset), value);
            }
        }
        int directory = checked(tiff + (int)U32(tiff + 4));
        if (directory < tiff || directory > limit - 2 || U16(tiff + 2) != 42)
        {
            throw Invalid();
        }
        int entries = U16(directory), images = 0, table = 0;
        for (int i = 0; i < entries; i++)
        {
            int tag = checked(directory + 2 + i * 12);
            if (tag > limit - 12)
            {
                throw Invalid();
            }
            if (U16(tag) == 0xb001)
            {
                images = checked((int)U32(tag + 8));
            }
            if (U16(tag) == 0xb002 && U32(tag + 4) == 32)
            {
                table = checked(tiff + (int)U32(tag + 8));
            }
        }
        if (images != 2 || table < tiff || table > limit - 32)
        {
            throw Invalid();
        }
        Put(table + 4, checked((uint)jpeg.Length));
        Put(table + 8, 0);
        Put(table + 20, checked((uint)gainLength));
        Put(table + 24, checked((uint)(jpeg.Length - tiff)));
    }

    /// <summary>Appends MP4 bytes last and declares the exact selected cover-frame timestamp.</summary>
    public static async Task WriteAsync(byte[] jpeg, string video, string destination, long timestampUs, CancellationToken cancellation = default)
    {
        if (timestampUs < 0)
        {
            throw Invalid();
        }
        await using var clip = File.OpenRead(video);
        if (clip.Length < 12)
        {
            throw Invalid();
        }
        byte[] header = new byte[12];
        await clip.ReadExactlyAsync(header, cancellation);
        if (!header.AsSpan(4, 4).SequenceEqual("ftyp"u8))
        {
            throw Invalid();
        }
        clip.Position = 0;
        byte[] image = AddMetadata(jpeg, clip.Length, timestampUs);
        await using var output = File.Create(destination);
        await output.WriteAsync(image, cancellation);
        await clip.CopyToAsync(output, cancellation);
    }

    /// <summary>Checks semantic order, tight byte extents, JPEG boundaries and the final MP4 header.</summary>
    public static MotionPhotoInfo Inspect(string path)
    {
        using var file = File.OpenRead(path);
        // XMP is bounded by a JPEG APP1 segment. Read only enough header bytes
        // to find it, then load the image prefix instead of the appended movie.
        byte[] prefix = new byte[(int)Math.Min(file.Length, 1048576)];
        file.ReadExactly(prefix);
        int marker = prefix.AsSpan().IndexOf(xmpPrefix);
        if (marker < 4)
        {
            throw Invalid();
        }
        int size = BinaryPrimitives.ReadUInt16BigEndian(prefix.AsSpan(marker - 2)) - 2;
        if (size < xmpPrefix.Length || marker + size > prefix.Length)
        {
            throw Invalid();
        }
        var xml = ReadXmp(prefix.AsSpan(marker + xmpPrefix.Length, size - xmpPrefix.Length));
        var description = xml.Descendants(rdf + "Description").First(d => d.Attribute(camera + "MotionPhoto")?.Value == "1");
        if (description.Attribute(camera + "MotionPhotoVersion")?.Value != "1"
            || !long.TryParse(description.Attribute(camera + "MotionPhotoPresentationTimestampUs")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var timestamp) || timestamp < 0)
        {
            throw Invalid();
        }
        var items = xml.Descendants(container + "Item").ToArray();
        bool ultra = items.Length == 3;
        string[] expected = ultra ? ["Primary", "GainMap", "MotionPhoto"] : ["Primary", "MotionPhoto"];
        if (items.Length != expected.Length || !items.Select(i => i.Attribute(item + "Semantic")?.Value).SequenceEqual(expected)
            || items[0].Attribute(item + "Mime")?.Value != "image/jpeg" || items[^1].Attribute(item + "Mime")?.Value != "video/mp4"
            || !long.TryParse(items[^1].Attribute(item + "Length")?.Value, out var length) || length <= 0 || length >= file.Length)
        {
            throw Invalid();
        }
        long offset = file.Length - length;
        if (offset > 268435456)
        {
            throw Invalid();
        }
        file.Position = 0;
        byte[] image = new byte[checked((int)offset)];
        file.ReadExactly(image);
        var primary = ParseJpeg(image);
        if (ultra)
        {
            long gain = long.Parse(items[1].Attribute(item + "Length")?.Value ?? "0", CultureInfo.InvariantCulture);
            if (gain != offset - primary.Length || items[1].Attribute(item + "Mime")?.Value != "image/jpeg"
                || ParseJpeg(image.AsSpan(primary.Length)).Length != gain)
            {
                throw Invalid();
            }
            UltraHdrCodec.Inspect(image);
        }
        else if (primary.Length != offset)
        {
            throw Invalid();
        }
        Span<byte> videoHeader = stackalloc byte[12];
        file.ReadExactly(videoHeader);
        if (!videoHeader[4..8].SequenceEqual("ftyp"u8))
        {
            throw Invalid();
        }
        return new(offset, length, timestamp, ultra);
    }
}
