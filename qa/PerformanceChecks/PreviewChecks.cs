using System.Diagnostics;
using VideoConverter.Core;

/// <summary>Deterministic ownership, scheduling and pipe-boundary regressions.</summary>
internal static class PreviewChecks
{
    private static int assertions;
    private static void Check(bool condition, string name)
    {
        if (!condition)
        {
            throw new InvalidOperationException(name);
        }
        assertions++;
    }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static FramePreviewRequest Frame(int value) => new(value, "auto", null);
    private static Task Unexpected(Exception error, CancellationToken _) => Task.FromException(error);

    internal static async Task RunAsync()
    {
        await CheckCoalescingAsync();
        await CheckCacheAsync();
        await CheckCancellationAsync();
        await CheckStaleResultAsync();
        await CheckFailureAsync();
        await CheckBinaryPipeAsync();
        Console.WriteLine($"Preview checks passed: {assertions}");
    }

    private static async Task CheckCoalescingAsync()
    {
        var started = Signal();
        var release = Signal();
        var rendered = new List<int>();
        var shown = new List<int>();
        int running = 0, maximum = 0;
        await using var session = new FramePreviewSession(async (request, token) =>
        {
            maximum = Math.Max(maximum, Interlocked.Increment(ref running));
            rendered.Add(request.Frame);
            if (request.Frame == 1)
            {
                started.SetResult();
                await release.Task.WaitAsync(token);
            }
            Interlocked.Decrement(ref running);
            return [(byte)request.Frame];
        }, (request, _, _) => { shown.Add(request.Frame); return Task.CompletedTask; }, Unexpected);
        session.Request(Frame(1));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        for (int i = 2; i <= 1000; i++)
        {
            session.Request(Frame(i));
        }
        release.SetResult();
        await session.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Check(maximum == 1 && rendered.SequenceEqual([1, 1000]), "one decoder and only the newest pending request");
        Check(shown.SequenceEqual([1, 1000]), "drag feedback continues before the final frame");
        session.Request(Frame(1000));
        await session.WaitForIdleAsync();
        Check(rendered.Count == 2 && shown.Count == 3, "idle cached request presents without another decode");
    }

    private static async Task CheckCacheAsync()
    {
        int renders = 0;
        await using var session = new FramePreviewSession((_, _) => { renders++; return Task.FromResult(new byte[4]); },
            (_, _, _) => Task.CompletedTask, Unexpected, maximumCacheBytes: 8);
        foreach (int frame in new[] { 1, 2, 1, 3, 1, 2 })
        {
            session.Request(Frame(frame));
            await session.WaitForIdleAsync();
            Check(session.CachedBytes <= 8, "PNG cache stays within its byte budget");
        }
        Check(renders == 4, "least recently used frame is evicted");
        await session.DisposeAsync();
        Check(session.CachedBytes == 0, "source disposal releases cache references");
        try
        {
            session.Request(Frame(1));
            Check(false, "disposed session rejects work");
        }
        catch (ObjectDisposedException) { assertions++; }

        renders = 0;
        await using var disabled = new FramePreviewSession((_, _) => { renders++; return Task.FromResult(new byte[1]); },
            (_, _, _) => Task.CompletedTask, Unexpected, maximumCacheBytes: 0);
        for (int i = 0; i < 2; i++)
        {
            disabled.Request(Frame(1));
            await disabled.WaitForIdleAsync();
        }
        Check(renders == 2 && disabled.CachedBytes == 0, "zero budget disables caching");
    }

    private static async Task CheckCancellationAsync()
    {
        var started = Signal();
        bool exited = false, reentered = false;
        int shown = 0;
        FramePreviewSession? session = null;
        await using var owner = session = new FramePreviewSession(async (request, token) =>
        {
            if (request.Frame == 1)
            {
                using var registration = token.Register(() => { _ = session!.CachedBytes; reentered = true; });
                started.SetResult();
                try
                {
                    await Task.Delay(Timeout.Infinite, token);
                }
                finally { exited = true; }
            }
            return [1];
        }, (_, _, _) => { shown++; return Task.CompletedTask; }, Unexpected);
        session.Request(Frame(1));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        session.Request(Frame(2));
        await session.CancelAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Check(exited && reentered && shown == 0, "cancel joins work without a stale callback or lock re-entry deadlock");
        session.Request(Frame(3));
        await session.WaitForIdleAsync();
        Check(shown == 1, "cancelled session can render a new request");
    }

    private static async Task CheckFailureAsync()
    {
        int errors = 0, shown = 0;
        await using var session = new FramePreviewSession((request, _) => request.Frame == 1
            ? Task.FromException<byte[]>(new IOException("decode failed")) : Task.FromResult(new byte[1]),
            (_, _, _) => { shown++; return Task.CompletedTask; },
            (error, _) => { Check(error.Message == "decode failed", "decode error reaches UI callback"); errors++; return Task.CompletedTask; });
        session.Request(Frame(1));
        await session.WaitForIdleAsync();
        session.Request(Frame(2));
        await session.WaitForIdleAsync();
        Check(errors == 1 && shown == 1, "decode failure does not stop later requests");
    }

    private static async Task CheckStaleResultAsync()
    {
        var started = Signal();
        var release = Signal();
        var shown = new List<int>();
        await using var session = new FramePreviewSession(async (request, _) =>
        {
            if (request.Frame == 1)
            {
                started.SetResult();
                // Model a decoder that completes successfully during cancellation.
                await release.Task;
            }
            return new byte[4];
        }, (request, _, _) => { shown.Add(request.Frame); return Task.CompletedTask; }, Unexpected);
        session.Request(Frame(1));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var cancelled = session.CancelAsync();
        session.Request(Frame(2));
        release.SetResult();
        await cancelled.WaitAsync(TimeSpan.FromSeconds(10));
        await session.WaitForIdleAsync();
        Check(shown.SequenceEqual([2]) && session.CachedBytes == 4, "stale successful decode is neither presented nor cached");
    }

    private static async Task CheckBinaryPipeAsync()
    {
        var folder = Directory.CreateTempSubdirectory("LumaRecode-pipe-check-");
        try
        {
            var executable = Environment.ProcessPath!;
            string[] prefix = Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
                ? [typeof(PreviewChecks).Assembly.Location] : [];
            var pidFile = Path.Combine(folder.FullName, "child.pid");
            string[] Arguments(string size) => [.. prefix, "--binary-tool", pidFile, size];
            foreach (int size in new[] { 0, 1, 31, 262144, 262145, 700000 })
            {
                var result = await ProcessRunner.CaptureBinaryAsync(executable, Arguments(size.ToString()), maximumBytes: Math.Max(1, size));
                Check(result.ExitCode == 0 && result.Output.Length == size, "binary pipe accepts its exact byte limit: " + size);
                Check(result.Output.Select((b, i) => b == (byte)i).All(value => value), "binary pipe preserves all bytes");
                Check(result.Error.Length <= 8192 && result.Error.EndsWith("diagnostic-tail"), "stderr drains while retaining a bounded tail");
            }
            try
            {
                await ProcessRunner.CaptureBinaryAsync(executable, Arguments("32"), maximumBytes: 31);
                Check(false, "oversize pipe must fail");
            }
            catch (InvalidDataException) { assertions++; }
            File.Delete(pidFile);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var task = ProcessRunner.CaptureBinaryAsync(executable, Arguments("wait"), cancellation.Token);
            while (!File.Exists(pidFile))
            {
                await Task.Delay(10, cancellation.Token);
            }
            int pid = int.Parse(await File.ReadAllTextAsync(pidFile, cancellation.Token));
            cancellation.Cancel();
            try
            {
                await task;
                Check(false, "cancelled pipe must fail");
            }
            catch (OperationCanceledException) { assertions++; }
            try
            {
                using var child = Process.GetProcessById(pid);
                Check(child.HasExited, "cancelled child has exited");
            }
            catch (ArgumentException) { assertions++; }
        }
        finally { folder.Delete(recursive: true); }
    }
}
