using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace VideoConverter;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--cli") return CommandLineHost.RunAsync(args[1..]).GetAwaiter().GetResult();
        if (args.Length > 0 && args[0] != "--gui" &&
            (args[0].StartsWith('-') || new[] { "probe", "presets", "profiles", "doctor", "license", "convert", "plan" }.Contains(args[0]) || !File.Exists(args[0])))
            return CommandLineHost.RunAsync(args).GetAwaiter().GetResult();
        RunGui();
        return 0;
    }
    private static void RunGui()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(parameters =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            try { _ = new ConverterApp(); }
            catch (Exception error)
            {
                Directory.CreateDirectory(UserSettings.Folder);
                File.WriteAllText(Path.Combine(UserSettings.Folder, "error.log"), error.ToString());
                throw;
            }
        });
    }
}

public sealed partial class ConverterApp : Application
{
    private Window? window;
    public ConverterApp()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            var folder = UserSettings.Folder;
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "error.log"), e.Exception.ToString());
        };
        L.Configure(UserSettings.Load().Language);
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        window = new MainWindow();
        window.Activate();
    }
}
