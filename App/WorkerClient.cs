using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VideoConverter;

public sealed class WorkerClient : IDisposable
{
    private Process? process;
    public bool Running => process is { HasExited: false };
    public async Task RunAsync(object request, Action<string, JsonElement> receive)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Payload", "ConverterWorker.exe");
        if (!File.Exists(path)) throw new FileNotFoundException("程序的转换组件缺失，请重新下载完整 EXE。", path);
        using var child = new Process { StartInfo = new ProcessStartInfo(path) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8 } };
        process = child;
        child.Start();
        var errors = child.StandardError.ReadToEndAsync();
        await child.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request));
        await child.StandardInput.FlushAsync();
        var terminal = false;
        while (await child.StandardOutput.ReadLineAsync() is { } line)
        {
            using var message = JsonDocument.Parse(line);
            var type = message.RootElement.GetProperty("event").GetString() ?? "";
            if (type is "done" or "cancelled" or "error" or "media") terminal = true;
            receive(type, message.RootElement.GetProperty("data").Clone());
        }
        await child.WaitForExitAsync();
        var diagnostic = await errors;
        process = null;
        if (!terminal) throw new InvalidOperationException("转换组件意外退出。" + (diagnostic.Length > 1500 ? diagnostic[^1500..] : diagnostic));
    }
    public async Task CancelAsync()
    {
        var child = process;
        if (child is null || child.HasExited) return;
        try { await child.StandardInput.WriteLineAsync("cancel"); await child.StandardInput.FlushAsync(); }
        catch (IOException) { }
    }
    public void Dispose()
    {
        try { if (process is { HasExited: false }) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
    }
    public static string FindTool(string name, string saved)
    {
        if (File.Exists(saved)) return saved;
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var candidate = Path.Combine(folder.Trim('"'), name + ".exe");
            if (File.Exists(candidate)) return candidate;
        }
        return "";
    }
    public static async Task CheckToolAsync(string path, string name)
    {
        if (!File.Exists(path)) throw new InvalidOperationException($"请选择 {name}.exe。");
        using var child = Process.Start(new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, ArgumentList = { "-version" } })!;
        var output = child.StandardOutput.ReadToEndAsync();
        var error = child.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await child.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { child.Kill(entireProcessTree: true); throw new InvalidOperationException($"{name} 版本检测超时。"); }
        if (child.ExitCode != 0 || !(await output).Contains(name + " version", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"所选文件未通过 {name} 版本检测。");
        await error;
    }
}
