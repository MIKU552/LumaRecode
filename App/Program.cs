using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace VideoConverter;

/// <summary>Selects the headless entry before initializing WinUI, COM wrappers or GUI resources.</summary>
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (JitProcessHost.RestartIfNeeded(args) is { } exitCode)
        {
            return exitCode;
        }
        if (StartupMode.IsCommandLine(args))
        {
            return CommandLineHost.RunAsync(args[0] == "--cli" ? args[1..] : args).GetAwaiter().GetResult();
        }

        RunGui();
        return 0;
    }
    private static void RunGui()
    {
        JobNotifications.ConfigureIdentity();
        WinRT.ComWrappersSupport.InitializeComWrappers();
        // Stable WinUI 2.6 optimizations are opt-in for template compatibility.
        foreach (var change in new[] {
            Microsoft.UI.Xaml.Settings.XamlChangeId.IconNoGridOptimization,
            Microsoft.UI.Xaml.Settings.XamlChangeId.OptimizeApplyStyles,
            Microsoft.UI.Xaml.Settings.XamlChangeId.DefaultStyleOptimizations,
            Microsoft.UI.Xaml.Settings.XamlChangeId.DeferContextFlyoutInit })
        {
            Microsoft.UI.Xaml.Settings.XamlOptionalChanges.EnableChange(change);
        }

        Application.Start(parameters =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            try
            {
                _ = new ConverterApp();
            }
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
    private MainWindow? window;
    public ConverterApp()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            var folder = UserSettings.Folder;
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "error.log"), e.Exception.ToString());
        };
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var settings = UserSettings.Load();
        L.Configure(settings.Language);
        window = new MainWindow(settings);
        window.ActivateWithFirstFrame();
    }
}
