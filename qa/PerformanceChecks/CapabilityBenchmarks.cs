using System.Diagnostics;
using System.Text.Json;
using VideoConverter.Core;

internal static class CapabilityBenchmarks
{
    internal static async Task RunAsync()
    {
        var executable = ProcessRunner.FindTool("ffmpeg");
        if (executable.Length == 0)
        {
            throw new FileNotFoundException("FFmpeg is required for --tools.");
        }

        string[][] reports = [["-hide_banner", "-encoders"], ["-hide_banner", "-h", "encoder=libx265"], ["-hide_banner", "-filters"]];
        async Task ReadAsync(ToolReportCache cache)
        {
            foreach (var arguments in reports)
            {
                var report = await cache.CaptureAsync(executable, arguments);
                if (report.ExitCode != 0)
                {
                    throw new InvalidDataException(report.Error);
                }
            }
        }
        var warm = new ToolReportCache();
        await ReadAsync(warm);
        var coldSamples = new List<double>();
        var warmSamples = new List<double>();
        for (int trial = 0; trial < 5; trial++)
        {
            var start = Stopwatch.GetTimestamp();
            await ReadAsync(new ToolReportCache());
            coldSamples.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            start = Stopwatch.GetTimestamp();
            await ReadAsync(warm);
            warmSamples.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }
        var version = await ProcessRunner.CaptureAsync(executable, ["-version"]);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            ffmpeg = version.Output.Split('\n')[0].Trim(),
            reports_per_sample = reports.Length,
            trials = 5,
            cold_median_ms = coldSamples.Order().ElementAt(2),
            cached_median_ms = warmSamples.Order().ElementAt(2)
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
