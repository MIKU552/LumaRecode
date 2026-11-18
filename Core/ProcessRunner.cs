using System.Buffers;
using System.Diagnostics;
using System.Text;

namespace VideoConverter.Core;

/// <summary>Complete output of a short-lived tool invocation; stderr remains separate from machine-readable stdout.</summary>
public sealed record ProcessResult(int ExitCode, string Output, string Error);
public sealed record BinaryProcessResult(int ExitCode, byte[] Output, string Error);

/// <summary>Starts external tools, drains their pipes and owns cancellation and file-identity checks.</summary>
public static class ProcessRunner
{
    /// <summary>Captures through the native process owner, joining pipes and descendants before returning.</summary>
    public static async Task<BinaryProcessResult> CaptureBinaryAsync(string executable, IEnumerable<string> arguments,
        CancellationToken cancellation = default, int maximumBytes = 8 * 1024 * 1024, string? directory = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        var result = await LumaRecode.Native.NativeProcess.RunAsync(new(Path.GetFullPath(executable), arguments.ToArray(),
            directory is null ? null : Path.GetFullPath(directory), OutputCapacity: maximumBytes, TimeoutMilliseconds: 60000), cancellation).ConfigureAwait(false);
        CheckNativeProcess(result, cancellation);
        return new(result.ExitCode, result.Output, result.ErrorText.Trim());
    }

    private static void CheckNativeProcess(LumaRecode.Native.NativeProcessResult result, CancellationToken cancellation)
    {
        // A normal nonzero tool exit is returned to its caller. Transport errors,
        // resource limits and unconfirmed cleanup remain failures of the owner.
        if (result.Status == LumaRecode.Native.NativeStatus.IoError && result.ChildExited)
        {
            return;
        }

        NativeBackend.Check(result.Status, result.Diagnostic, result.CleanupConfirmed, cancellation);
    }
    /// <summary>Retains recent diagnostics while consuming the full pipe to prevent a blocked child.</summary>
    internal static async Task<string> ReadTailAsync(TextReader reader, int capacity)
    {
        char[] buffer = new char[Math.Min(capacity, 4096)];
        var tail = new StringBuilder();
        int read;
        while ((read = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            tail.Append(buffer, 0, read);
            if (tail.Length > capacity)
            {
                tail.Remove(0, tail.Length - capacity);
            }
        }
        return tail.ToString().Trim();
    }

    /// <summary>Creates a hidden, shell-free invocation with UTF-8 output and one ArgumentList entry per argument.</summary>
    public static ProcessStartInfo StartInfo(string executable, IEnumerable<string> arguments, string? directory = null)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        if (directory is not null)
        {
            info.WorkingDirectory = directory;
        }

        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        return info;
    }

    public static void Stop(Process process)
    {
        // The process may exit between HasExited and Kill. That race is normal;
        // an unrelated access error must still reach the caller.
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) when (process.HasExited) { }
    }

    /// <summary>Captures bounded UTF-8 metadata through the shared process owner.</summary>
    public static async Task<ProcessResult> CaptureAsync(string executable, IEnumerable<string> arguments,
        CancellationToken cancellation = default, int timeoutSeconds = 25, string? directory = null,
        int maximumBytes = MediaResourceLimits.TextOutputBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(timeoutSeconds);
        var result = await LumaRecode.Native.NativeProcess.RunAsync(new(Path.GetFullPath(executable), arguments.ToArray(),
            directory is null ? null : Path.GetFullPath(directory), OutputCapacity: maximumBytes,
            TimeoutMilliseconds: checked((ulong)timeoutSeconds * 1000)), cancellation).ConfigureAwait(false);
        CheckNativeProcess(result, cancellation);
        return new(result.ExitCode, Encoding.UTF8.GetString(result.Output).TrimStart('\uFEFF'), result.ErrorText.Trim());
    }
    private static async Task<string> ReadTextAsync(Stream stream, int maximumBytes, CancellationToken cancellation)
    {
        byte[] buffer = new byte[8192];
        char[] characters = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];
        var decoder = Encoding.UTF8.GetDecoder();
        var text = new StringBuilder();
        int total = 0;
        bool started = false;
        while (true)
        {
            // Read at most one byte beyond the limit, including at exact EOF.
            int count = await stream.ReadAsync(buffer.AsMemory(0,
                (int)Math.Min(buffer.Length, (long)maximumBytes - total + 1)), cancellation).ConfigureAwait(false);
            if (count > maximumBytes - total)
            {
                throw new InvalidDataException($"外部工具的文本输出超过 {maximumBytes} 字节，已停止读取。");
            }
            total += count;
            int decoded = decoder.GetChars(buffer.AsSpan(0, count), characters, flush: count == 0);
            int start = 0;
            if (!started && decoded > 0)
            {
                started = true;
                start = characters[0] == '\uFEFF' ? 1 : 0;
            }
            text.Append(characters, start, decoded - start);
            if (count == 0)
            {
                return text.ToString();
            }
        }
    }

    /// <summary>Uses an existing saved path first, then searches PATH for a bare executable name.</summary>
    /// <returns>The full path, or an empty string when no candidate exists.</returns>
    public static string FindTool(string name, string saved = "")
    {
        if (File.Exists(saved))
        {
            return Path.GetFullPath(saved);
        }

        var executable = OperatingSystem.IsWindows() && !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name + ".exe" : name;
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var path = Path.Combine(folder.Trim('"'), executable);
            if (File.Exists(path))
            {
                return Path.GetFullPath(path);
            }
        }
        return "";
    }

    /// <summary>Compares file identities through the shared cross-platform filesystem implementation.</summary>
    public static bool SameFile(string left, string right)
    {
        left = Path.GetFullPath(left);
        right = Path.GetFullPath(right);
        if (string.Equals(left, right, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            return true;
        }

        if (!File.Exists(left) || !File.Exists(right))
        {
            return false;
        }

        var status = LumaRecode.Native.NativeOutputTransaction.FilesSame(left, right, out bool same, out var diagnostic);
        if (status == LumaRecode.Native.NativeStatus.NotFound)
        {
            return false;
        }

        NativeBackend.Check(status, diagnostic);
        return same;
    }
}
