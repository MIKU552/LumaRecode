using System.Reflection;
using System.Text.Json.Nodes;
using LumaRecode.Native;
using VideoConverter.Core;

internal static class HdrParityChecks
{
    private delegate string PayloadReader(ReadOnlySpan<byte> bytes);

    internal static async Task RunAsync(Action<bool, string> check)
    {
        // Developer-only differential adapter. The baseline's span parser is
        // invoked directly; production interop uses generated imports only.
        var type = typeof(Transcoder).Assembly.GetType("VideoConverter.Core.Hdr10PlusBitstream")!;
        var reader = type.GetMethod("ParseT35", BindingFlags.NonPublic | BindingFlags.Static)!
            .CreateDelegate<PayloadReader>();
        foreach (bool curve in new[] { false, true })
        {
            var original = Fixture(curve);
            var corpus = new List<byte[]> { original };
            for (int length = 6; length < original.Length; length++)
            {
                corpus.Add(original[..length]);
            }
            for (int position = 6; position < original.Length; position++)
            {
                for (int bit = 0; bit < 8; bit++)
                {
                    var changed = (byte[])original.Clone();
                    changed[position] ^= (byte)(1 << bit);
                    corpus.Add(changed);
                }
            }
            foreach (var bytes in corpus)
            {
                JsonObject? expected = null;
                try
                {
                    using var input = new StringReader(reader(bytes) + "\n");
                    using var output = new MemoryStream();
                    await VideoConverter.Core.Hdr10PlusMetadata.ExportAsync(input, output);
                    expected = JsonNode.Parse(output.ToArray())!["SceneInfo"]![0]!.AsObject();
                }
                catch (InvalidDataException) { }
                var status = NativeCore.ParseHdr10Plus(bytes, out var actual, out var diagnostic);
                string context = $"HDR10+ {Convert.ToHexString(bytes)}: {diagnostic}";
                check((expected is not null) == (status == NativeStatus.Ok), "Acceptance differs: " + context);
                if (expected is not null && status == NativeStatus.Ok)
                {
                    check(EqualsExpected(in actual, expected), "Integer code values differ: " + context);
                }
            }
        }
    }

    private static unsafe bool EqualsExpected(in LumaRecode.Native.Hdr10PlusMetadata actual, JsonObject expected)
    {
        var luminance = expected["LuminanceParameters"]!;
        if (actual.ApplicationVersion != 1 || actual.NumberOfWindows != 1
            || actual.TargetedDisplayLuminance != expected["TargetedSystemDisplayMaximumLuminance"]!.GetValue<uint>()
            || actual.AverageRgb != luminance["AverageRGB"]!.GetValue<uint>())
        {
            return false;
        }
        fixed (uint* maxScl = actual.MaxScl)
        fixed (uint* indices = actual.DistributionIndices)
        fixed (uint* values = actual.DistributionValues)
        fixed (uint* anchors = actual.Anchors)
        {
            for (int i = 0; i < 3; i++)
            {
                if (maxScl[i] != luminance["MaxScl"]![i]!.GetValue<uint>())
                {
                    return false;
                }
            }
            var distributions = luminance["LuminanceDistributions"]!;
            if (actual.DistributionCount != distributions["DistributionIndex"]!.AsArray().Count)
            {
                return false;
            }
            for (int i = 0; i < actual.DistributionCount; i++)
            {
                if (indices[i] != distributions["DistributionIndex"]![i]!.GetValue<uint>()
                    || values[i] != distributions["DistributionValues"]![i]!.GetValue<uint>())
                {
                    return false;
                }
            }
            if (expected["BezierCurveData"] is not JsonObject curve)
            {
                return actual.ToneMapping == 0;
            }
            if (actual.ToneMapping != 1 || actual.KneePointX != curve["KneePointX"]!.GetValue<uint>()
                || actual.KneePointY != curve["KneePointY"]!.GetValue<uint>()
                || actual.AnchorCount != curve["Anchors"]!.AsArray().Count)
            {
                return false;
            }
            for (int i = 0; i < actual.AnchorCount; i++)
            {
                if (anchors[i] != curve["Anchors"]![i]!.GetValue<uint>())
                {
                    return false;
                }
            }
        }
        return true;
    }

    private static byte[] Fixture(bool curve)
    {
        List<byte> bytes = [0xb5, 0, 0x3c, 0, 1, 4];
        int pending = 0, used = 0;
        void Put(int value, int width)
        {
            for (int bit = width - 1; bit >= 0; bit--)
            {
                pending = (pending << 1) | ((value >> bit) & 1);
                if (++used == 8)
                {
                    bytes.Add((byte)pending);
                    pending = 0;
                    used = 0;
                }
            }
        }
        Put(1, 8); Put(1, 2); Put(curve ? 1000 : 0, 27); Put(0, 1);
        Put(1000, 17); Put(2000, 17); Put(3000, 17); Put(1500, 17);
        Put(2, 4); Put(1, 7); Put(500, 17); Put(100, 7); Put(3000, 17);
        Put(0, 10); Put(0, 1); Put(curve ? 1 : 0, 1);
        if (curve)
        {
            Put(1024, 12); Put(2048, 12); Put(2, 4); Put(100, 10); Put(900, 10);
        }
        Put(0, 1);
        if (used != 0)
        {
            bytes.Add((byte)(pending << (8 - used)));
        }
        return [.. bytes];
    }
}
