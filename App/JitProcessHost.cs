using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace VideoConverter;

/// <summary>Hosts JIT CLI workers and opts capable x64 machines into APX before CLR startup.</summary>
internal static class JitProcessHost
{
    private const string WorkerEnvironment = "LUMARECODE_CLI_WORKER";
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessorFeaturePresent(uint feature);

    internal static bool NeedsApxRestart(Architecture architecture, bool apxAvailable, string? dotnet, string? legacy) =>
        architecture == Architecture.X64 && apxAvailable && dotnet is null && legacy is null;

    internal static bool NeedsLaunch(bool cli, bool worker, bool apx) => apx || cli && !worker;

    internal static int? RestartIfNeeded(string[] arguments)
    {
        // CoreCLR reads ISA switches during startup, before managed Main. AOT
        // has already fixed its instructions; it must not enter this path.
        if (!RuntimeFeature.IsDynamicCodeSupported || !OperatingSystem.IsWindows())
        {
            return null;
        }
        bool cli = StartupMode.IsCommandLine(arguments);
        bool worker = Environment.GetEnvironmentVariable(WorkerEnvironment) == "1";
        bool apx = NeedsApxRestart(RuntimeInformation.ProcessArchitecture, IsProcessorFeaturePresent(90),
            Environment.GetEnvironmentVariable("DOTNET_EnableAPX"), Environment.GetEnvironmentVariable("COMPlus_EnableAPX"));
        if (!NeedsLaunch(cli, worker, apx))
        {
            return null;
        }
        var info = StartInfo(Environment.ProcessPath ?? throw new InvalidOperationException("Application path is unavailable."),
            Path.Combine(AppContext.BaseDirectory, "LumaRecode.dll"), arguments, apx, cli);
        // PowerShell can kill its direct pipeline process on Ctrl+C. The worker
        // receives the console signal and can stop FFmpeg and clean staging files.
        if (cli)
        {
            ConsoleConnection.Connect();
        }
        ConsoleCancelEventHandler keepWaiting = (_, e) => e.Cancel = true;
        if (cli)
        {
            Console.CancelKeyPress += keepWaiting;
        }
        try
        {
            using var child = Process.Start(info) ?? throw new InvalidOperationException("Unable to start the LumaRecode worker.");
            if (!cli)
            {
                return 0;
            }
            child.WaitForExit();
            return child.ExitCode;
        }
        finally
        {
            if (cli)
            {
                Console.CancelKeyPress -= keepWaiting;
            }
        }
    }

    internal static ProcessStartInfo StartInfo(string executable, string assemblyPath, IReadOnlyList<string> arguments, bool enableApx, bool cliWorker)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = !cliWorker };
        // Framework-dependent applications can also be invoked as dotnet app.dll.
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            info.ArgumentList.Add(assemblyPath);
        }
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }
        // These values affect the child only. Explicit ISA overrides are retained.
        if (enableApx)
        {
            info.Environment["DOTNET_EnableAPX"] = "1";
        }
        if (cliWorker)
        {
            info.Environment[WorkerEnvironment] = "1";
        }
        return info;
    }
}
