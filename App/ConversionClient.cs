using System.Text.Json.Nodes;
using VideoConverter.Core;

namespace VideoConverter;

/// <summary>Runs core jobs off the UI thread and delivers ordered events on the caller's synchronization context.</summary>
/// <remarks>Each client owns one job. Disposing or cancelling it also cancels ffprobe/FFmpeg child processes.</remarks>
// CsWinRT generates AOT-safe interface metadata for IDisposable on this type.
public sealed partial class ConversionClient : IDisposable
{
    private readonly object gate = new();
    private CancellationTokenSource? cancellation;
    private bool disposed;
    public bool Running
    {
        get
        {
            lock (gate)
            {
                return cancellation is not null;
            }
        }
    }
    private static readonly Lazy<Transcoder> engine = new(() => new(Path.Combine(AppContext.BaseDirectory, "Assets", "Media")));
    public static Transcoder Engine => engine.Value;
    public Task RunAsync(ConversionRequest request, Action<string, JsonNode?> receive)
        => RunAsync(request, (type, data) => { receive(type, data); return Task.CompletedTask; });

    public async Task RunAsync(ConversionRequest request, Func<string, JsonNode?, Task> receive)
    {
        using var lifetime = new CancellationTokenSource();
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (cancellation is not null)
            {
                throw new InvalidOperationException("This client already has an active job.");
            }

            cancellation = lifetime;
        }
        try
        {
            await ConversionEventPump.RunAsync(async (emit, token) =>
            {
                if (request.Action is not ("probe" or "convert"))
                {
                    throw new InvalidOperationException("Unknown job action.");
                }

                var media = await MediaProbe.InspectAsync(request.Tools, request.Path, token);
                if (request.Action == "probe")
                {
                    return new ConversionEvent("media", MediaProbe.Describe(media));
                }

                var options = request.Options ?? throw new InvalidOperationException("Missing encoding options.");
                await Engine.RunAsync(request.Tools, media, options, request.Output!, emit, token);
                return new ConversionEvent("done", JsonValue.Create(request.Output));
            }, receive, lifetime);
        }
        finally
        {
            lock (gate)
            {
                cancellation = null;
            }
        }
    }

    /// <summary>Signals cancellation. RunAsync completes after the backend has joined its child processes.</summary>
    public Task CancelAsync()
    {
        // Serialize access with RunAsync's teardown to avoid cancelling a
        // disposed CancellationTokenSource from a late window-close callback.
        lock (gate)
        {
            cancellation?.Cancel();
        }

        return Task.CompletedTask;
    }
    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            cancellation?.Cancel();
        }
    }
    public static string FindTool(string name, string saved) => ProcessRunner.FindTool(name, saved);
    public static async Task CheckToolAsync(string path, string name)
    {
        if (!File.Exists(path))
        {
            throw new InvalidOperationException(L.T($"请选择 {name}.exe。"));
        }

        try
        {
            var result = await ProcessRunner.CaptureAsync(path, (string[])["-version"], timeoutSeconds: 10);
            if (result.ExitCode != 0 || !result.Output.Contains(name + " version", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(L.T($"所选文件未通过 {name} 版本检测。"));
            }
        }
        catch (TimeoutException) { throw new InvalidOperationException(L.T($"{name} 版本检测超时。")); }
    }
}
