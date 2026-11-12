using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace VideoConverter;

internal static class CommandLineHost
{
    public static async Task<int> RunAsync(string[] args)
    {
        ConsoleConnection.Connect();
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
            if (args.Contains("--json")) Console.Out.WriteLine(JsonSerializer.Serialize(
                new HostError(1, "error", new HostErrorData("runtime_unavailable", error.Message)), WorkerJsonContext.Default.HostError));
            else Console.Error.WriteLine(error.Message);
            return cancelled ? 130 : 3;
        }
    }
}
