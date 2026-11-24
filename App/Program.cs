using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

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
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var settings = UserSettings.Load();
        L.Configure(settings.Language);
        window = new MainWindow(settings);
        ActivateWithFirstFrame(window);
    }

    private const uint DwmwaCloak = 13;

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(nint hwnd, uint attribute, ref int value, uint size);

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmFlush();

    private static void ActivateWithFirstFrame(Window window)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        int cloak = 1;
        if (DwmSetWindowAttribute(hwnd, DwmwaCloak, ref cloak, sizeof(int)) < 0)
        {
            window.Activate();
            return;
        }

        // Rendering fires before XAML has drawn. Wait for the loaded tree's
        // Rendered event, then let DWM consume that submission before uncloaking.
        var content = (FrameworkElement)window.Content;
        bool waiting = true;
        bool revealQueued = false;
        var timeout = window.DispatcherQueue.CreateTimer();
        timeout.Interval = TimeSpan.FromMilliseconds(350);
        timeout.IsRepeating = false;
        CompositionTarget.Rendered += Render;
        window.Closed += Closed;
        timeout.Tick += TimedOut;
        timeout.Start();
        window.Activate();

        void Render(object? sender, RenderedEventArgs args)
        {
            if (content.IsLoaded && !revealQueued)
            {
                revealQueued = window.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
                {
                    if (waiting)
                    {
                        _ = DwmFlush();
                        Reveal();
                    }
                });
            }
        }
        void TimedOut(DispatcherQueueTimer sender, object args) => Reveal();
        void Closed(object sender, WindowEventArgs args) => Stop();
        void Stop()
        {
            waiting = false;
            CompositionTarget.Rendered -= Render;
            timeout.Tick -= TimedOut;
            timeout.Stop();
            window.Closed -= Closed;
        }
        void Reveal()
        {
            if (waiting)
            {
                Stop();
                cloak = 0;
                _ = DwmSetWindowAttribute(hwnd, DwmwaCloak, ref cloak, sizeof(int));
            }
        }
    }
}
