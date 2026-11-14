using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using VideoConverter;

// Keep Microsoft's single-file extraction instead of introducing a custom SFX format.
// The GUI runs in the AOT child; this entry process exits as soon as it is started.
// For CLI use it waits and returns the child's exit code. Standard handles go directly
// to the child, without text conversion, a second pipe relay, or argument reparsing.
internal static class Program
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr owner, string message, string title, uint type);

#if LUMARECODE_APX
    // Windows reports CPU support together with OS enablement. The generic
    // launcher checks this before loading any APX-compiled application code.
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessorFeaturePresent(uint feature);
#endif

    [STAThread]
    public static int Main(string[] args)
    {
        bool cli = StartupMode.IsCommandLine(args);
        if (cli)
        {
            ConsoleConnection.Connect();
        }

        try
        {
#if LUMARECODE_APX
            const uint apxFeature = 90; // PF_APX_F_INSTRUCTIONS_AVAILABLE, Windows SDK winnt.h.
            if (!IsProcessorFeaturePresent(apxFeature))
            {
                throw new InvalidOperationException("This APX build requires an APX-capable CPU and Windows with APX enabled. Use the Native build on this PC.\nAPX 版本需要支持并启用 APX 的 CPU 和 Windows，请使用适合本机的 Native 版本。");
            }
#endif
            var executable = Path.Combine(AppContext.BaseDirectory, "Application", "LumaRecode.exe");
            if (!File.Exists(executable))
            {
                throw new FileNotFoundException("LumaRecode application is missing. Please download the complete EXE again.");
            }

            var info = new ProcessStartInfo(executable) { UseShellExecute = false };
            foreach (var argument in args)
            {
                info.ArgumentList.Add(argument);
            }
            // Keep the original working directory for relative input/output paths.
            // Do not use KillOnParentExit: the GUI must survive the extraction entry.
            ConsoleCancelEventHandler keepWaiting = (_, e) => e.Cancel = true;
            if (cli)
            {
                Console.CancelKeyPress += keepWaiting;
            }

            try
            {
                using var child = Process.Start(info) ?? throw new InvalidOperationException("Unable to start LumaRecode.");
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
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        {
            if (cli)
            {
                if (args.Contains("--json"))
                {
                    Console.Out.WriteLine(JsonSerializer.Serialize(
                    new HostError(1, "error", new HostErrorData("runtime_unavailable", error.Message)), CliErrorJsonContext.Default.HostError));
                }
                else
                {
                    Console.Error.WriteLine(error.Message);
                }
            }
            else
            {
                MessageBoxW(IntPtr.Zero, error.Message, "LumaRecode", 0x10);
            }

            return 3;
        }
    }
}
