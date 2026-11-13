using System.Text.Json;
using VideoConverter.Core;

/// <summary>Injects specific initialization failures before forwarding recovered jobs to real FFmpeg.</summary>
internal static class FfmpegStub
{
    public static async Task<int?> TryRunAsync(string[] args)
    {
        var scenario = Environment.GetEnvironmentVariable("LUMARECODE_QSV_STUB");
        if (string.IsNullOrEmpty(scenario) || args.FirstOrDefault() != "-hide_banner")
        {
            return null;
        }
        if (args.Contains("-progress"))
        {
            File.AppendAllText(Environment.GetEnvironmentVariable("LUMARECODE_QSV_TRACE")!, JsonSerializer.Serialize(args) + "\n");
            var input = Array.IndexOf(args, "-i");
            bool hardware = args.Take(input).Contains("-hwaccel");
            var power = Array.IndexOf(args, "-low_power");
            var frames = Array.IndexOf(args, "-bf:v");
            string? error = scenario switch
            {
                "fatal" => "[hevc_qsv @ test] Error during encoding: device failed (-17)",
                "file-exists" => "[vf#0:0 @ test] Task finished with error code: -17 (File exists)",
                _ when scenario.Contains("decoder") && hardware => "[hevc_qsv @ test] Error initializing the MFX video decoder: unsupported (-3)",
                _ when scenario.Contains("format") && hardware => "Impossible to convert between the formats supported by the filter 'Parsed_vpp_qsv_0' and the filter 'auto_scale_0'",
                _ when scenario.Contains("power") && power >= 0 && args[power + 1] == "1" => "[hevc_qsv @ test] Low power mode is unsupported",
                _ when scenario.Contains("timestamp") && frames < 0 => "[vost#0:0/hevc_qsv @ test] Non-monotonous DTS in output stream 0:0; previous: 20, current: 20",
                _ => null
            };
            if (error is not null)
            {
                // Exercise deletion of an incomplete private result on retry.
                await File.WriteAllTextAsync(args[^1], "partial result");
                Console.Error.WriteLine(error);
                return 1;
            }
        }
        var result = await ProcessRunner.CaptureAsync(ProcessRunner.FindTool("ffmpeg"), args, timeoutSeconds: 120);
        Console.Write(result.Output);
        Console.Error.Write(result.Error);
        return result.ExitCode;
    }
}
