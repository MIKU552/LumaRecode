using VideoConverter;
using VideoConverter.Core;

internal static class RuntimeChecks
{
    private static int assertions;

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }

        assertions++;
    }

    internal static async Task RunAsync()
    {
        CheckLog();
        await CheckEventsAsync();
        await CheckSynchronizationContextAsync();
        await CheckReportsAsync();
        Console.WriteLine($"Runtime performance checks passed: {assertions}");
    }

    private static void CheckLog()
    {
        var log = new ConversionLog();
        Check(!log.Changed, "new log starts unchanged");
        log.Append("first\n");
        log.Append("second\n");
        Check(log.Changed && log.Snapshot() == "first\nsecond\n" && !log.Changed, "append order and flush state");
        log.Clear();
        var expected = "";
        for (int i = 0; i < 2000; i++)
        {
            var line = $"日志 {i:D4} 🙂 test\n";
            expected += line;
            log.Append(line);
            if (i % 71 != 0)
            {
                continue;
            }

            var tail = log.Snapshot();
            Check(tail.Length <= 40000 && expected.EndsWith(tail, StringComparison.Ordinal), "bounded log keeps the newest complete UTF-16 tail");
            Check(tail.Length == 0 || !char.IsLowSurrogate(tail[0]), "tail starts at a Unicode scalar boundary");
        }
        var large = string.Concat(Enumerable.Repeat("🙂", 30001));
        log.Append(large);
        var recent = log.Snapshot();
        Check(recent.Length <= 40000 && large.EndsWith(recent, StringComparison.Ordinal), "oversized entry keeps its tail");
        Check(!char.IsLowSurrogate(recent[0]), "oversized Unicode entry has no orphaned surrogate");
        log.Clear();
        Check(log.Changed && log.Snapshot() == "", "clear flushes the display");
    }

    private static async Task CheckEventsAsync()
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var sent = 0;
        var received = 0;
        var maximum = 0;
        var terminal = false;
        await ConversionEventPump.RunAsync(async (emit, token) =>
        {
            for (int i = 0; i < 400; i++)
            {
                emit("log", System.Text.Json.Nodes.JsonValue.Create(i));
                var ahead = Interlocked.Increment(ref sent) - Volatile.Read(ref received);
                maximum = Math.Max(maximum, ahead);
            }
            await Task.Yield();
            return new("done", null);
        }, async (type, data) =>
        {
            if (type == "done")
            {
                await Task.Delay(1);
                terminal = true;
                return;
            }
            Check(data!.GetValue<int>() == received, "events remain in order");
            Interlocked.Increment(ref received);
            await Task.Delay(1);
        }, lifetime);
        Check(sent == 400 && received == 400 && terminal, "all events and asynchronous terminal callback complete");
        Check(maximum <= 129, "queue plus the dispatched event stays bounded");

        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var stopped = false;
        var cancelTerminal = false;
        await ConversionEventPump.RunAsync((emit, token) =>
        {
            try
            {
                for (int i = 0; i < 10000; i++)
                {
                    emit("log", null);
                }

                return Task.FromResult(new ConversionEvent("done", null));
            }
            finally { stopped = true; }
        }, async (type, _) =>
        {
            if (type == "log")
            {
                await Task.Delay(10);
                cancel.Cancel();
            }
            cancelTerminal |= type == "cancelled";
        }, cancel);
        Check(stopped && cancelTerminal, "cancellation releases a blocked producer and preserves its terminal event");

        using var failing = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        stopped = false;
        try
        {
            await ConversionEventPump.RunAsync((emit, _) =>
            {
                try
                {
                    for (int i = 0; i < 10000; i++)
                    {
                        emit("log", null);
                    }

                    return Task.FromResult(new ConversionEvent("done", null));
                }
                finally { stopped = true; }
            }, (_, _) => throw new IOException("receiver failed"), failing);
            Check(false, "receiver failure must propagate");
        }
        catch (IOException error) when (error.Message == "receiver failed")
        {
            Check(stopped, "receiver failure joins producer before returning");
        }
    }

    private static async Task CheckReportsAsync()
    {
        var folder = Path.Combine(Path.GetTempPath(), "LumaRecode-reports-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var executable = Environment.ProcessPath!;
            var prefix = Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
                ? new[] { typeof(RuntimeChecks).Assembly.Location } : Array.Empty<string>();
            string[] Arguments(string name, int delay = 0, int status = 0) =>
                [.. prefix, "--report-tool", Path.Combine(folder, name), delay.ToString(), status.ToString()];
            var cache = new ToolReportCache();
            var successful = Arguments("success");
            var first = await cache.CaptureAsync(executable, successful);
            var second = await cache.CaptureAsync(executable, successful);
            Check(first == second && first.Output.Contains("capability report"), "successful report is reused");
            Check(File.ReadAllLines(Path.Combine(folder, "success")).Length == 1, "reused report does not start a process");
            var failed = Arguments("failure", status: 3);
            await cache.CaptureAsync(executable, failed);
            await cache.CaptureAsync(executable, failed);
            Check(File.ReadAllLines(Path.Combine(folder, "failure")).Length == 2, "failed report is not cached");
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            try
            {
                await cache.CaptureAsync(executable, successful, cancelled.Token);
                Check(false, "cached access must respect cancellation");
            }
            catch (OperationCanceledException) { assertions++; }
            var slow = Arguments("timeout", delay: 3000);
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    await cache.CaptureAsync(executable, slow, timeoutSeconds: 1);
                    Check(false, "slow report must time out");
                }
                catch (TimeoutException) { assertions++; }
            }
            Check(File.ReadAllLines(Path.Combine(folder, "timeout")).Length == 2, "timed-out reports are retried");
            var newBatch = new ToolReportCache();
            await newBatch.CaptureAsync(executable, successful);
            Check(File.ReadAllLines(Path.Combine(folder, "success")).Length == 2, "new batch starts with a fresh capability report");

            var appHost = Path.Combine(AppContext.BaseDirectory, "PerformanceChecks.exe");
            if (File.Exists(appHost))
            {
                var toolFolder = Path.Combine(folder, "tool");
                Directory.CreateDirectory(toolFolder);
                foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory))
                {
                    File.Copy(file, Path.Combine(toolFolder, Path.GetFileName(file)));
                }
                var copiedHost = Path.Combine(toolFolder, Path.GetFileName(appHost));
                string[] stampArguments = ["--report-tool", Path.Combine(folder, "stamp"), "0", "0"];
                await cache.CaptureAsync(copiedHost, stampArguments);
                await cache.CaptureAsync(copiedHost, stampArguments);
                File.SetLastWriteTimeUtc(copiedHost, File.GetLastWriteTimeUtc(copiedHost).AddSeconds(2));
                await cache.CaptureAsync(copiedHost, stampArguments);
                Check(File.ReadAllLines(Path.Combine(folder, "stamp")).Length == 2, "changed executable invalidates its cached report");
            }
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    private static async Task CheckSynchronizationContextAsync()
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            using var context = new TestContext();
            SynchronizationContext.SetSynchronizationContext(context);
            var owner = Environment.CurrentManagedThreadId;
            async Task RunAsync()
            {
                try
                {
                    using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await ConversionEventPump.RunAsync((emit, _) =>
                    {
                        Check(Environment.CurrentManagedThreadId != owner, "producer runs outside the UI context");
                        emit("log", null);
                        return Task.FromResult(new ConversionEvent("done", null));
                    }, async (_, _) =>
                    {
                        Check(Environment.CurrentManagedThreadId == owner, "receiver runs on the captured UI context");
                        await Task.Yield();
                        Check(Environment.CurrentManagedThreadId == owner, "asynchronous receiver retains its context");
                    }, lifetime);
                    completed.SetResult();
                }
                catch (Exception error) { completed.SetException(error); }
                finally { context.Complete(); }
            }
            _ = RunAsync();
            context.Run();
        })
        {
            IsBackground = true
        };
        thread.Start();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(15));
        thread.Join();
    }

    private sealed class TestContext : SynchronizationContext, IDisposable
    {
        private readonly System.Collections.Concurrent.BlockingCollection<(SendOrPostCallback Callback, object? State)> work = new();
        public override void Post(SendOrPostCallback callback, object? state) => work.Add((callback, state));
        internal void Run()
        {
            foreach (var (callback, state) in work.GetConsumingEnumerable())
            {
                callback(state);
            }
        }
        internal void Complete() => work.CompleteAdding();
        public void Dispose() => work.Dispose();
    }

}
