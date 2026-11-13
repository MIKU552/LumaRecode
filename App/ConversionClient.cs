using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using VideoConverter.Core;

namespace VideoConverter;

public sealed partial class ConversionClient : IDisposable
{
    private CancellationTokenSource? cancellation;
    public bool Running => cancellation is not null;
    private static readonly Lazy<Transcoder> engine = new(() => new(Path.Combine(AppContext.BaseDirectory, "Assets", "Media")));
    public static Transcoder Engine => engine.Value;
    public async Task RunAsync(ConversionRequest request, Action<string, JsonElement> receive)
    {
        using var lifetime = new CancellationTokenSource();
        cancellation = lifetime;
        var events = Channel.CreateUnbounded<(string Type, JsonNode? Data)>(new UnboundedChannelOptions { SingleReader = true });
        var execution = Task.Run(async () =>
        {
            void Emit(string type, JsonNode? data) => events.Writer.TryWrite((type, data?.DeepClone()));
            try
            {
                var tools = new MediaTools(request.Tools.Ffmpeg, request.Tools.Ffprobe);
                var media = await MediaProbe.InspectAsync(tools, request.Path, lifetime.Token);
                if (request.Action == "probe") Emit("media", MediaProbe.Describe(media));
                else
                {
                    var o = request.Options ?? throw new InvalidOperationException("Missing encoding options.");
                    var options = new EncodeOptions(o.Purpose, o.Codec, o.Output, o.Transform, o.Profile, o.Sampling,
                        o.Variant, o.Audio, o.SourceKind, o.CpuDecode, o.Container, o.ColorRange, o.BitDepth);
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
            await foreach (var (type, data) in events.Reader.ReadAllAsync())
            {
                using var document = JsonDocument.Parse(BackendJson.Write(data));
                receive(type, document.RootElement.Clone());
            }
            await execution;
        }
        finally
        {
            lifetime.Cancel();
            await execution;
            cancellation = null;
        }
    }
    public Task CancelAsync() { cancellation?.Cancel(); return Task.CompletedTask; }
    public void Dispose() => cancellation?.Cancel();
    public static string FindTool(string name, string saved) => ProcessRunner.FindTool(name, saved);
    public static async Task CheckToolAsync(string path, string name)
    {
        if (!File.Exists(path)) throw new InvalidOperationException(L.T($"请选择 {name}.exe。"));
        try
        {
            var result = await ProcessRunner.CaptureAsync(path, (string[])["-version"], timeoutSeconds: 10);
            if (result.ExitCode != 0 || !result.Output.Contains(name + " version", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(L.T($"所选文件未通过 {name} 版本检测。"));
        }
        catch (TimeoutException) { throw new InvalidOperationException(L.T($"{name} 版本检测超时。")); }
    }
}
