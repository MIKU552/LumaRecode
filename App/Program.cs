using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace VideoConverter;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
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
