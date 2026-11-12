using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace VideoConverter;

internal static class CommandLineHost
{
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AttachConsole(uint process);
    [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int kind);
    [DllImport("kernel32.dll")] private static extern bool SetStdHandle(int kind, IntPtr handle);
    [DllImport("kernel32.dll")] private static extern uint GetFileType(IntPtr handle);

    private static void ConnectConsole()
    {
        // AttachConsole may replace standard handles. Preserve inherited pipes/files.
        var handles = new[] { -10, -11, -12 }.Select(k => (kind: k, handle: GetStdHandle(k)))
            .Where(h => h.handle != IntPtr.Zero && h.handle != new IntPtr(-1) && GetFileType(h.handle) != 0).ToArray();
        AttachConsole(uint.MaxValue);
        foreach (var h in handles) SetStdHandle(h.kind, h.handle);
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true });
    }

    public static async Task<int> RunAsync(string[] args)
    {
        ConnectConsole();
        var noSettings = args.TakeWhile(a => a != "--").Contains("--no-settings");
        var settings = noSettings ? new UserSettings() : UserSettings.Load();
        WorkerClient.ExternalPython = WorkerClient.FindTool("python", settings.Python);
        string? python = null;
        for (var i = 0; i < args.Length && args[i] != "--"; i++)
        {
            if (args[i] == "--python" && i + 1 < args.Length) python = args[++i];
            else if (args[i].StartsWith("--python=", StringComparison.Ordinal)) python = args[i][9..];
        }
        var cancelled = false;
        try
        {
            if (python is not null)
            {
                python = File.Exists(python) ? Path.GetFullPath(python) : WorkerClient.FindTool(Path.GetFileNameWithoutExtension(python), "");
                await WorkerClient.CheckPythonAsync(python);
            }
            var info = WorkerClient.CreateStartInfo("cli.py", python);
            foreach (var arg in args) info.ArgumentList.Add(arg);
            info.Environment["FRAMEFLOW_HOSTED"] = "1";
            info.Environment["FRAMEFLOW_LANGUAGE"] = LanguagePolicy.Resolve(settings.Language, System.Globalization.CultureInfo.CurrentUICulture.Name);
            info.Environment["FRAMEFLOW_FFMPEG"] = settings.Ffmpeg;
            info.Environment["FRAMEFLOW_FFPROBE"] = settings.Ffprobe;
            info.Environment["FRAMEFLOW_LICENSE_DIR"] = LegalNotices.Folder;
            info.Environment["FRAMEFLOW_TERMS_ACCEPTED"] = settings.AcceptedThirdPartyTerms == LegalNotices.Revision ? "1" : "0";
            info.Environment["FRAMEFLOW_VERSION"] = typeof(CommandLineHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "unknown";
            using var child = new Process { StartInfo = info };
            child.Start();
            ConsoleCancelEventHandler handler = (_, e) =>
            {
                e.Cancel = true; cancelled = true;
                try { child.StandardInput.WriteLine("cancel"); child.StandardInput.Flush(); }
                catch (Exception error) when (error is IOException or InvalidOperationException) { }
            };
            Console.CancelKeyPress += handler;
            async Task ForwardAsync(Stream source, Stream destination)
            {
                try { await source.CopyToAsync(destination); }
                catch (IOException)
                {
                    cancelled = true;
                    try { await child.StandardInput.WriteLineAsync("cancel"); await child.StandardInput.FlushAsync(); }
                    catch (Exception error) when (error is IOException or InvalidOperationException) { }
                    await source.CopyToAsync(Stream.Null);
                }
            }
            try
            {
                await Task.WhenAll(ForwardAsync(child.StandardOutput.BaseStream, Console.OpenStandardOutput()),
                    ForwardAsync(child.StandardError.BaseStream, Console.OpenStandardError()), child.WaitForExitAsync());
            }
            finally { Console.CancelKeyPress -= handler; }
            if (!noSettings && child.ExitCode == 0 && settings.AcceptedThirdPartyTerms != LegalNotices.Revision && args.TakeWhile(a => a != "--").Contains("--accept-terms"))
            {
                var current = UserSettings.Load();
                current.AcceptedThirdPartyTerms = LegalNotices.Revision;
                current.Save();
            }
            return cancelled ? 130 : child.ExitCode;
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            if (args.Contains("--json")) Console.Out.WriteLine(JsonSerializer.Serialize(new { schema_version = 1, @event = "error", data = new { code = "runtime_unavailable", message = error.Message } }));
            else Console.Error.WriteLine(error.Message);
            return cancelled ? 130 : 3;
        }
    }
}
