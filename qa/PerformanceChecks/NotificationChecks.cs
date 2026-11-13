using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using VideoConverter;

internal static class NotificationChecks
{
    internal static void Run()
    {
        int count = 0;
        void Check(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }

            count++;
        }
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        var languages = new[] { "en", "ja", "es" }.ToDictionary(code => code, code =>
            JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(root, "App", $"Strings.{code}.json")))!);
        var catalog = new TranslationCatalog(languages);
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            var result = new ConversionResult(@"C:\out\output.mp4", 10485760, 125.7, "MP4 · HEVC / H.265");
            foreach (var code in new[] { "zh", "en", "ja", "es" })
            {
                string Translate(string text) => catalog.Translate(text, code);
                var payload = JobNotificationContent.Success(@"C:\input\视频 & café 🙂.mov", TimeSpan.FromSeconds(65), result, Translate);
                var document = XDocument.Parse(payload);
                var texts = document.Descendants("text").Select(node => node.Value).ToArray();
                Check(texts[0] == Translate("转换完成"), "completion status follows the selected language");
                Check(texts[1] == "视频 & café 🙂.mov", "filename survives XML escaping and Unicode");
                Check(texts.Length == 6, "success includes status, filename and all four output details");
                Check(texts.Any(text => text.EndsWith("01:05")), "elapsed time is separate from media duration");
                Check(texts.Any(text => text.EndsWith("10 MiB")), "file size uses the actual output byte count");
                Check(texts.Any(text => text.EndsWith("02:05")), "notification uses the verified output duration");
                Check(texts.Any(text => text.EndsWith(result.Format)), "notification includes container and video codec");
                Check(document.Root?.Attribute("launch")?.Value == "show", "notification activation carries no file or shell command");
                Check(!document.Descendants("audio").Any(), "sound remains controlled by Windows");

                var failed = XDocument.Parse(JobNotificationContent.Failure(@"C:\input\视频 & café 🙂.mov",
                    TimeSpan.FromSeconds(7), "转换失败", Translate));
                var failures = failed.Descendants("text").Select(node => node.Value).ToArray();
                Check(failures.Length == 4 && failures[0] == Translate("转换失败"), "failure includes only status, filename, elapsed time and reason");
                Check(failures[^1].EndsWith(Translate("转换失败")), "failure reasons use the selected language");
            }
            var longError = string.Concat(Enumerable.Repeat("失败 & <reason> 🙂\0\u0001\r\n", 2000));
            var bounded = JobNotificationContent.Failure(new string('视', 255) + ".mp4", TimeSpan.Zero, longError, text => text);
            XDocument.Parse(bounded);
            Check(Encoding.UTF8.GetByteCount(bounded) < 4096, "notification stays below the native toast payload limit");
            Check(!bounded.Contains('\0') && !bounded.Contains('\u0001'), "invalid XML control characters are removed");
            Check(JobNotificationContent.Duration(360001) == "100:00:01", "long recordings do not wrap at 24 hours");
            Check(JobNotificationContent.Duration(double.NaN) == "—", "unknown media duration remains explicit");
            Check(JobNotificationContent.Size(0) == "0 B" && JobNotificationContent.Size(long.MaxValue).EndsWith("EiB"), "file-size formatting handles its boundaries");
        }
        finally { CultureInfo.CurrentCulture = previous; }
        Console.WriteLine($"Notification payload checks passed: {count}");
    }
}
