using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace VideoConverter.Core;

public sealed record ProcessResult(int ExitCode, string Output, string Error);

public static class ProcessRunner
{
    public static ProcessStartInfo StartInfo(string executable, IEnumerable<string> arguments, string? directory = null)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8 };
        if (directory is not null) info.WorkingDirectory = directory;
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return info;
    }

    public static void Stop(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) when (process.HasExited) { }
    }

    public static async Task<ProcessResult> CaptureAsync(string executable, IEnumerable<string> arguments,
        CancellationToken cancellation = default, int timeoutSeconds = 25)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, timeout.Token);
        using var process = new Process { StartInfo = StartInfo(executable, arguments) };
        cancellation.ThrowIfCancellationRequested();
        process.Start();
        using var registration = linked.Token.Register(() => Stop(process));
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await stdout; var error = await stderr;
        cancellation.ThrowIfCancellationRequested();
        if (timeout.IsCancellationRequested) throw new TimeoutException("External tool timed out.");
        return new(process.ExitCode, output, error);
    }

    public static string FindTool(string name, string saved = "")
    {
        if (File.Exists(saved)) return Path.GetFullPath(saved);
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var path = Path.Combine(folder.Trim('"'), name + (OperatingSystem.IsWindows() ? ".exe" : ""));
            if (File.Exists(path)) return Path.GetFullPath(path);
        }
        return "";
    }

    public static bool SameFile(string left, string right)
    {
        if (string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return true;
        if (!File.Exists(left) || !File.Exists(right)) return false;
        if (OperatingSystem.IsWindows())
        {
            using var a = File.OpenHandle(left, access: FileAccess.Read, share: FileShare.ReadWrite | FileShare.Delete);
            using var b = File.OpenHandle(right, access: FileAccess.Read, share: FileShare.ReadWrite | FileShare.Delete);
            if (!GetFileInformationByHandle(a, out var x) || !GetFileInformationByHandle(b, out var y))
                throw new IOException("Unable to compare file identities.");
            return x.Volume == y.Volume && x.IndexHigh == y.IndexHigh && x.IndexLow == y.IndexLow;
        }
        return new FileInfo(left).ResolveLinkTarget(true)?.FullName == Path.GetFullPath(right)
            || new FileInfo(right).ResolveLinkTarget(true)?.FullName == Path.GetFullPath(left);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);
}
