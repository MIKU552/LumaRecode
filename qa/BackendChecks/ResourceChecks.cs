using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using VideoConverter.Core;

/// <summary>Real pipe lifecycle checks and allocation-boundary tests, without a GPU or video fixture.</summary>
internal static class ResourceChecks
{
    private static int assertions;
    private static readonly string unicode = new string('a', 8191) + "中文🙂\r\n" + new string('z', 9000);

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
        assertions++;
    }

    internal static async Task<int> RunToolAsync(string[] args)
    {
        File.WriteAllText(args[1], Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        Console.Error.Write(new string('e', 100_000) + "diagnostic-tail");
        await using var output = Console.OpenStandardOutput();
        if (args[2] is "unicode" or "bom")
        {
            if (args[2] == "bom")
            {
                await output.WriteAsync(Encoding.UTF8.GetPreamble());
            }
            await output.WriteAsync(Encoding.UTF8.GetBytes(unicode));
        }
        else if (args[2] == "overflow")
        {
            byte[] chunk = new byte[8192];
            while (true)
            {
                await output.WriteAsync(chunk);
            }
        }
        else if (args[2] == "wait")
        {
            await Task.Delay(Timeout.Infinite);
        }
        else if (args[2] == "line-wait")
        {
            await output.WriteAsync("progress=continue\n"u8.ToArray());
            await output.FlushAsync();
            await Task.Delay(Timeout.Infinite);
        }
        return 0;
    }

    internal static async Task RunAsync()
    {
        await CheckPipesAsync();
        await CheckLinesAsync();
        CheckIndex();
        Console.WriteLine($"Resource budget checks passed: {assertions}");
    }

    private static async Task CheckPipesAsync()
    {
        var directory = Directory.CreateTempSubdirectory("LumaRecode-resource-check-");
        try
        {
            string executable = Environment.ProcessPath!;
            string[] prefix = Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
                ? [typeof(ResourceChecks).Assembly.Location] : [];
            string pidFile = Path.Combine(directory.FullName, "child.pid");
            string[] Arguments(string mode) => [.. prefix, "--resource-tool", pidFile, mode];
            foreach (var mode in new[] { "empty", "unicode", "bom" })
            {
                int expected = mode == "empty" ? 0 : Encoding.UTF8.GetByteCount(unicode);
                int maximum = Math.Max(1, expected + (mode == "bom" ? 3 : 0));
                var result = await ProcessRunner.CaptureAsync(executable, Arguments(mode), maximumBytes: maximum);
                Check(result.ExitCode == 0 && result.Output == (mode == "empty" ? "" : unicode), "exact UTF-8 byte limit, split code points and BOM: " + mode);
                Check(result.Error.Length <= 8192 && result.Error.EndsWith("diagnostic-tail", StringComparison.Ordinal), "stderr drains concurrently");
            }
            try
            {
                await ProcessRunner.CaptureAsync(executable, Arguments("unicode"), maximumBytes: Encoding.UTF8.GetByteCount(unicode) - 1);
                Check(false, "text one byte beyond limit must fail");
            }
            catch (InvalidDataException) { assertions++; }
            AssertExited(int.Parse(File.ReadAllText(pidFile), CultureInfo.InvariantCulture));

            var elapsed = Stopwatch.StartNew();
            try
            {
                await ProcessRunner.CaptureAsync(executable, Arguments("overflow"), timeoutSeconds: 20, maximumBytes: 31);
                Check(false, "unterminated output flood must fail");
            }
            catch (InvalidDataException) { assertions++; }
            Check(elapsed.Elapsed < TimeSpan.FromSeconds(10), "output rejection stops a blocked child before timeout");
            AssertExited(int.Parse(File.ReadAllText(pidFile), CultureInfo.InvariantCulture));

            try
            {
                await ProcessRunner.CaptureAsync(executable, Arguments("wait"), timeoutSeconds: 1);
                Check(false, "text timeout must fail");
            }
            catch (TimeoutException) { assertions++; }
            AssertExited(int.Parse(File.ReadAllText(pidFile), CultureInfo.InvariantCulture));

            File.Delete(pidFile);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var task = ProcessRunner.CaptureAsync(executable, Arguments("wait"), cancellation.Token);
            int pid = 0;
            while (pid == 0)
            {
                if (File.Exists(pidFile))
                {
                    int.TryParse(await File.ReadAllTextAsync(pidFile, cancellation.Token), out pid);
                }
                if (pid == 0)
                {
                    await Task.Delay(10, cancellation.Token);
                }
            }
            cancellation.Cancel();
            try
            {
                await task;
                Check(false, "cancelled text capture must fail");
            }
            catch (OperationCanceledException) { assertions++; }
            AssertExited(pid);

            using var progress = new Process { StartInfo = ProcessRunner.StartInfo(executable, Arguments("line-wait")) };
            progress.Start();
            var errors = ProcessRunner.ReadTailAsync(progress.StandardError, 8192);
            try
            {
                var reader = new BoundedLineReader(progress.StandardOutput);
                var line = await reader.ReadLineAsync(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                Check(line == "progress=continue" && !progress.HasExited, "progress arrives before a pipe fills or the encoder exits");
            }
            finally
            {
                ProcessRunner.Stop(progress);
                await progress.WaitForExitAsync();
                await errors;
            }
        }
        finally { directory.Delete(recursive: true); }
    }

    private static void AssertExited(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            Check(process.HasExited, "capture joins the child before returning");
        }
        catch (ArgumentException) { assertions++; }
    }

    private sealed class Chunks(string text, int chunk) : TextReader
    {
        private int offset;
        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = Math.Min(Math.Min(buffer.Length, chunk), text.Length - offset);
            text.AsMemory(offset, count).CopyTo(buffer);
            offset += count;
            return new(count);
        }
    }

    private static async Task CheckLinesAsync()
    {
        foreach (int chunk in new[] { 1, 2, 4096 })
        {
            var reader = new BoundedLineReader(new Chunks("\r\na\rb\n中文🙂\r\nlast", chunk), 8);
            var lines = new List<string>();
            while (await reader.ReadLineAsync(CancellationToken.None) is { } line)
            {
                lines.Add(line);
            }
            Check(lines.SequenceEqual(["", "a", "b", "中文🙂", "last"]), "bounded lines preserve CR/LF, chunks and EOF: " + chunk);
        }
        var exact = new BoundedLineReader(new Chunks("12345678\r\n\n", 1), 8);
        Check(await exact.ReadLineAsync() == "12345678" && await exact.ReadLineAsync() == "" && await exact.ReadLineAsync() is null,
            "line accepts its exact character limit without an extra EOF record");
        try
        {
            await new BoundedLineReader(new Chunks(new string('a', 100_000), 4096), 8).ReadLineAsync();
            Check(false, "oversized line must fail before building the entire record");
        }
        catch (InvalidDataException) { assertions++; }
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            await exact.ReadLineAsync(cancelled.Token);
            Check(false, "buffered reader still observes cancellation");
        }
        catch (OperationCanceledException) { assertions++; }
    }

    private static void CheckIndex()
    {
        const int records = 1000;
        long bytes = 2 * (records * (long)Unsafe.SizeOf<FramePoint>() + 3 * IntPtr.Size);
        var index = new BoundedMediaIndex<FramePoint>(bytes);
        for (int i = records - 1; i >= 0; i--)
        {
            index.Add(new(i * 1001L, 1001, i % 30 == 0));
        }
        index.Sort((a, b) => a.Pts.CompareTo(b.Pts));
        var snapshot = index.Snapshot();
        Check(snapshot.Length == records && snapshot[0].Pts == 0 && snapshot[^1].Pts == 999999,
            "index preserves B-frame sorting and exact boundary records");
        try
        {
            index.Add(new(0, 1, false));
            Check(false, "index must reject an element beyond its live-plus-copy budget");
        }
        catch (InvalidDataException) { assertions++; }
        Check(index.Count == records && snapshot.Length == records, "budget failure leaves the existing index intact");
        try
        {
            new BoundedMediaIndex<long>(1).Add(0);
            Check(false, "tiny index budget must reject before initial allocation");
        }
        catch (InvalidDataException) { assertions++; }
    }

    /// <summary>Exercises a 12-hour 60 fps index using timestamp records, without generating or storing pixels.</summary>
    internal static async Task CheckLongRecordingAsync()
    {
        const int records = 12 * 60 * 60 * 60;
        var index = new BoundedMediaIndex<FramePoint>();
        for (int i = 0; i < records; i++)
        {
            index.Add(new(i * 1000L, 1000, i % 120 == 0));
        }
        var snapshot = index.Snapshot();
        Check(snapshot.Length == records && snapshot[^1].Pts == (records - 1L) * 1000, "long 4K/HDR recording index fits normal memory budget");
        Console.WriteLine($"Long-recording index passed: {records} frames; {snapshot.LongLength * Unsafe.SizeOf<FramePoint>()} retained bytes");

        // A streamed metadata pass has a per-record bound, not the aggregate
        // text-capture limit. Model a long movie without retaining its text.
        const int rows = 1_000_000;
        var source = new RepeatedLines(rows);
        var reader = new BoundedLineReader(source);
        int count = 0;
        while (await reader.ReadLineAsync(CancellationToken.None) is { } line)
        {
            Check(line.Length == RepeatedLines.Record.Length - 1, "streamed record remains complete");
            count++;
        }
        Check(count == rows && source.Characters > MediaResourceLimits.TextOutputBytes, "long metadata streams may exceed the captured-text budget");
        Console.WriteLine($"Long metadata stream passed: {count} records; {source.Characters} characters; bounded individual lines");
    }

    private sealed class RepeatedLines(int rows) : TextReader
    {
        internal const string Record = "frame|side_data_type=HDR Dynamic Metadata SMPTE2094-40 (HDR10+)|application version=1|num_windows=1\n";
        internal long Characters => (long)rows * Record.Length;
        private long offset;
        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = (int)Math.Min(buffer.Length, Characters - offset);
            int copied = 0;
            while (copied < count)
            {
                int start = (int)(offset % Record.Length);
                int size = Math.Min(count - copied, Record.Length - start);
                Record.AsMemory(start, size).CopyTo(buffer[copied..]);
                copied += size;
                offset += size;
            }
            return new(count);
        }
    }
}
