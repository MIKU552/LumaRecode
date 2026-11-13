namespace VideoConverter.Core;

/// <summary>Reuses successful capability reports within one batch, never media probes or encodes.</summary>
/// <remarks>The caller owns the cache lifetime. A changed executable invalidates its reports;
/// failed, cancelled and timed-out invocations are always retried.</remarks>
public sealed class ToolReportCache
{
    private const int Capacity = 32;
    private readonly object gate = new();
    private readonly Dictionary<ReportKey, ProcessResult> reports = new();

    public async Task<ProcessResult> CaptureAsync(string executable, string[] arguments,
        CancellationToken cancellation = default, int timeoutSeconds = 15)
    {
        cancellation.ThrowIfCancellationRequested();
        var key = Identify(executable, arguments);
        if (key is not null)
        {
            lock (gate)
            {
                if (reports.TryGetValue(key, out var cached))
                {
                    return cached;
                }
            }
        }

        var result = await ProcessRunner.CaptureAsync(executable, arguments, cancellation, timeoutSeconds);
        if (key is not null && result.ExitCode == 0 && key == Identify(executable, arguments))
        {
            lock (gate)
            {
                if (reports.Count >= Capacity)
                {
                    reports.Clear();
                }
                reports[key] = result;
            }
        }
        return result;
    }

    private static ReportKey? Identify(string executable, string[] arguments)
    {
        try
        {
            var file = new FileInfo(executable);
            return file.Exists ? new(file.FullName, file.Length, file.LastWriteTimeUtc.Ticks,
                string.Join('\0', arguments)) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private sealed record ReportKey(string Executable, long Length, long Modified, string Arguments);
}
