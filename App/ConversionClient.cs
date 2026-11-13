using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using VideoConverter.Core;

namespace VideoConverter;

/// <summary>Runs core jobs off the UI thread and delivers ordered events on the caller's synchronization context.</summary>
/// <remarks>Each client owns one job. Disposing or cancelling it also cancels ffprobe/FFmpeg child processes.</remarks>
// CsWinRT generates AOT-safe interface metadata for IDisposable on this type.
public sealed partial class ConversionClient : IDisposable
{
    private readonly object gate = new();
    private CancellationTokenSource? cancellation;
    public bool Running
    {
        get
        {
            lock (gate)
                return cancellation is not null;
        }
    }
    private static readonly Lazy<Transcoder> engine = new(() => new(Path.Combine(AppContext.BaseDirectory, "Assets", "Media")));
    public static Transcoder Engine => engine.Value;
    public async Task RunAsync(ConversionRequest request, Action<string, JsonElement> receive)
    {
        using var lifetime = new CancellationTokenSource();
        lock (gate)
        {
            if (cancellation is not null)
                throw new InvalidOperationException("This client already has an active job.");
            cancellation = lifetime;
        }
        // The producer never touches WinUI. Nodes are detached snapshots, since
        // mutable JsonNode objects must not be shared while the producer edits them.
        var events = Channel.CreateUnbounded<(string Type, JsonNode? Data)>(new UnboundedChannelOptions { SingleReader = true });
        var execution = Task.Run(async () =>
        {
            void Emit(string type, JsonNode? data) => events.Writer.TryWrite((type, data?.DeepClone()));
            try
            {
                if (request.Action is not ("probe" or "convert"))
                    throw new InvalidOperationException("Unknown job action.");
                var tools = request.Tools;
                var media = await MediaProbe.InspectAsync(tools, request.Path, lifetime.Token);
                if (request.Action == "probe")
                    Emit("media", MediaProbe.Describe(media));
                else
                {
                    var options = request.Options ?? throw new InvalidOperationException("Missing encoding options.");
                    await Engine.RunAsync(tools, media, options, request.Output!, Emit, lifetime.Token);
                    Emit("done", JsonValue.Create(request.Output));
                }
            }
            catch (OperationCanceledException) { Emit("cancelled", null); }
            catch (Exception e) { Emit("error", JsonValue.Create(e.Message)); }
            finally { events.Writer.TryComplete(); }
        });
        try
        {
            // Deliberately retain the captured UI context here. Only the work
            // above runs in Task.Run; receive() updates controls on the UI thread.
            await foreach (var (type, data) in events.Reader.ReadAllAsync())
            {
                using var document = JsonDocument.Parse(BackendJson.Write(data));
                receive(type, document.RootElement.Clone());
            }
            await execution;
        }
        finally
        {
            // A callback can fail before the backend finishes. Cancel and join
            // the producer before disposing its token or releasing this client.
            lifetime.Cancel();
            try
            {
                await execution;
            }
            finally { lock (gate) cancellation = null; }
        }
    }
    public Task CancelAsync()
    {
        // Serialize access with RunAsync's teardown to avoid cancelling a
        // disposed CancellationTokenSource from a late window-close callback.
        lock (gate)
            cancellation?.Cancel();
        return Task.CompletedTask;
    }
    public void Dispose()
    {
        lock (gate)
            cancellation?.Cancel();
    }
    public static string FindTool(string name, string saved) => ProcessRunner.FindTool(name, saved);
    public static async Task CheckToolAsync(string path, string name)
    {
        if (!File.Exists(path))
            throw new InvalidOperationException(L.T($"请选择 {name}.exe。"));
        try
        {
            var result = await ProcessRunner.CaptureAsync(path, (string[])["-version"], timeoutSeconds: 10);
            if (result.ExitCode != 0 || !result.Output.Contains(name + " version", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(L.T($"所选文件未通过 {name} 版本检测。"));
        }
        catch (TimeoutException) { throw new InvalidOperationException(L.T($"{name} 版本检测超时。")); }
    }
}
