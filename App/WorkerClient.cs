using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VideoConverter;

public sealed partial class WorkerClient : IDisposable
{
    public static string ExternalPython { get; set; } = "";
    public static bool HasBundledPython => File.Exists(Path.Combine(AppContext.BaseDirectory, "Payload", "Runtime", "python.exe"));
    private Process? process;
    public bool Running => process is { HasExited: false };
    internal static ProcessStartInfo CreateStartInfo(string script, string? pythonOverride = null)
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "Payload", "Worker");
        var path = Path.Combine(folder, script);
        if (!File.Exists(path)) throw new FileNotFoundException(L.T("程序的转换组件缺失，请重新下载完整 EXE。"), path);
        var python = pythonOverride ?? (HasBundledPython ? Path.Combine(AppContext.BaseDirectory, "Payload", "Runtime", "python.exe") : FindTool("python", ExternalPython));
        if (python == "") throw new FileNotFoundException(L.T("未找到 Python，请在工具路径中选择 Python 3.14 或更新版本。"));
        var info = new ProcessStartInfo(python) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8 };
        // Isolate the interpreter from user site/PYTHONPATH and add only our worker folder.
        // Explicit insertion also supports the official embeddable Python's ._pth mode.
        foreach (var argument in new[] { "-I", "-B", "-c", "import runpy,sys;folder,path=sys.argv[1:3];sys.path.insert(0,folder);sys.argv=sys.argv[2:];runpy.run_path(path,run_name='__main__')", folder, path })
            info.ArgumentList.Add(argument);
        return info;
    }
    public async Task RunAsync(WorkerRequest request, Action<string, JsonElement> receive)
    {
        using var child = new Process { StartInfo = CreateStartInfo("worker.py") };
        process = child;
        child.Start();
        var errors = child.StandardError.ReadToEndAsync();
        await child.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request, WorkerJsonContext.Default.WorkerRequest));
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
        if (!terminal) throw new InvalidOperationException(L.T("转换组件意外退出。") + (diagnostic.Length > 1500 ? diagnostic[^1500..] : diagnostic));
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
        if (name == "python") { await CheckPythonAsync(path); return; }
        if (!File.Exists(path)) throw new InvalidOperationException(L.T($"请选择 {name}.exe。"));
        using var child = Process.Start(new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, ArgumentList = { "-version" } })!;
        var output = child.StandardOutput.ReadToEndAsync();
        var error = child.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await child.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { child.Kill(entireProcessTree: true); throw new InvalidOperationException(L.T($"{name} 版本检测超时。")); }
        if (child.ExitCode != 0 || !(await output).Contains(name + " version", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(L.T($"所选文件未通过 {name} 版本检测。"));
        await error;
    }
    public static async Task CheckPythonAsync(string path)
    {
        if (!File.Exists(path)) throw new InvalidOperationException(L.T("请选择 python.exe。"));
        using var child = Process.Start(new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            ArgumentList = { "-I", "-c", "import json,sys;print(json.dumps(list(sys.version_info[:3])))" } })!;
        var output = child.StandardOutput.ReadToEndAsync();
        var error = child.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await child.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { child.Kill(entireProcessTree: true); throw new InvalidOperationException(L.T("Python 版本检测超时。")); }
        var text = await output; await error;
        try
        {
            var version = JsonSerializer.Deserialize(text.Trim(), AppJsonContext.Default.Int32Array);
            if (child.ExitCode == 0 && version is { Length: 3 } && version[0] == 3 && version[1] >= 14) return;
        }
        catch (JsonException) { }
        throw new InvalidOperationException(L.T("需要 Python 3.14 或更新版本，推荐 Python 3.15.2。"));
    }
}
