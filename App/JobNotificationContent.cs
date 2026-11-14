using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace VideoConverter;

/// <summary>Builds bounded, escaped native-toast content without depending on the Windows notification service.</summary>
internal static class JobNotificationContent
{
    internal static string Success(string input, TimeSpan elapsed, ConversionResult result, Func<string, string> translate)
        => Build(input, elapsed, null, result, translate);

    internal static string Failure(string input, TimeSpan elapsed, string reason, Func<string, string> translate)
        => Build(input, elapsed, reason, null, translate);

    private static string Build(string input, TimeSpan elapsed, string? error, ConversionResult? result,
        Func<string, string> translate)
    {
        string Label(string template, object value) => string.Format(CultureInfo.CurrentCulture, translate(template), value);
        var rows = new List<XElement>
        {
            Text(Label("耗时：{0}", Duration(elapsed.TotalSeconds)))
        };
        if (result is not null)
        {
            rows.Add(Text(Label("大小：{0}", Size(result.Bytes))));
            if (result.Duration > 0)
            {
                rows.Add(Text(Label("时长：{0}", Duration(result.Duration))));
            }
            rows.Add(Text(Label("格式：{0}", result.Format)));
        }
        else
        {
            rows.Add(Text(Label("失败原因：{0}", Clean(translate(error ?? ""), 500)), 5));
        }

        // Adaptive rows keep all requested fields available in the expanded
        // Windows notification. The OS controls banner visibility and sound.
        return new XElement("toast", new XAttribute("launch", "show"),
            new XElement("visual", new XElement("binding", new XAttribute("template", "ToastGeneric"),
                Text(translate(result is null ? "转换失败" : "转换完成")),
                Text(Clean(System.IO.Path.GetFileName(input), 260), 2),
                new XElement("group", new XElement("subgroup", rows)))))
            .ToString(SaveOptions.DisableFormatting);
    }

    private static XElement Text(string value, int maximumLines = 1) => new("text",
        new XAttribute("hint-wrap", "true"), new XAttribute("hint-maxLines", maximumLines), value);

    internal static string Duration(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0)
        {
            return "—";
        }
        var whole = (long)Math.Min(seconds, 359999999);
        return whole >= 3600 ? $"{whole / 3600}:{whole / 60 % 60:00}:{whole % 60:00}"
            : $"{whole / 60:00}:{whole % 60:00}";
    }

    internal static string Size(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB", "EiB"];
        double size = Math.Max(0, bytes);
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return size.ToString(unit == 0 ? "0" : "0.##", CultureInfo.CurrentCulture) + " " + units[unit];
    }

    private static string Clean(string text, int limit)
    {
        var result = new StringBuilder(Math.Min(text.Length, limit));
        int count = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (count++ >= limit)
            {
                result.Append('…');
                break;
            }
            var value = rune.Value;
            if (value is 9 or 10 or 13)
            {
                result.Append(' ');
            }
            else if (value is >= 0x20 and <= 0xd7ff or >= 0xe000 and <= 0xfffd or >= 0x10000 and <= 0x10ffff)
            {
                result.Append(rune.ToString());
            }
        }
        return result.ToString().Trim();
    }
}
