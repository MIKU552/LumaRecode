using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace VideoConverter;

/// <summary>A detached event snapshot; the producer does not mutate Data after emitting it.</summary>
internal readonly record struct ConversionEvent(string Type, JsonNode? Data);

/// <summary>Bounds queued work while preserving event order and the caller's synchronization context.</summary>
internal static class ConversionEventPump
{
    internal static async Task RunAsync(
        Func<Action<string, JsonNode?>, CancellationToken, Task<ConversionEvent>> execute,
        Func<string, JsonNode?, Task> receive,
        CancellationTokenSource lifetime)
    {
        var events = Channel.CreateBounded<ConversionEvent>(new BoundedChannelOptions(128)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait
        });
        var execution = Task.Run(async () =>
        {
            // This callback only runs on backend threads. Backpressure prevents
            // a slow UI from retaining unbounded logs; cancellation releases a
            // producer waiting for space before joining its child process.
            void Emit(string type, JsonNode? data) => events.Writer
                .WriteAsync(new(type, data), lifetime.Token).AsTask().GetAwaiter().GetResult();
            try
            {
                return await execute(Emit, lifetime.Token);
            }
            catch (OperationCanceledException) { return new ConversionEvent("cancelled", null); }
            catch (Exception error) { return new ConversionEvent("error", JsonValue.Create(error.Message)); }
            finally { events.Writer.TryComplete(); }
        });
        try
        {
            // Do not suppress the captured context: receive updates WinUI.
            await foreach (var value in events.Reader.ReadAllAsync())
            {
                await receive(value.Type, value.Data);
            }
            // Terminal events are returned separately so a full queue cannot
            // prevent cancellation or lose the final result.
            var terminal = await execution;
            await receive(terminal.Type, terminal.Data);
        }
        finally
        {
            // Callback failures also cancel the producer. Join it before the
            // caller disposes the token source, even if receiving an event failed.
            lifetime.Cancel();
            await execution;
        }
    }
}
