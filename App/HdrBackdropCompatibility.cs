using Microsoft.Graphics.Display;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace VideoConverter;

/// <summary>Keep SDR XAML alpha away from the HDR Mica composition path.</summary>
public sealed class HdrBackdropCompatibility
{
    private readonly Grid root;
    private readonly Window window;
    private readonly DisplayInformation? display;
    private readonly UISettings system = new();
    private readonly AccessibilitySettings accessibility = new();
    private bool disposed;
    private string lastState = "";

    public HdrBackdropCompatibility(Grid root, Window window)
    {
        this.root = root; this.window = window;
        try
        {
            display = DisplayInformation.CreateForWindowId(window.AppWindow.Id);
            display.AdvancedColorInfoChanged += OnAdvancedColorInfoChanged;
        }
        catch (System.Runtime.InteropServices.COMException) { }
        window.Closed += (_, _) =>
        {
            disposed = true;
            if (display is not null) { display.AdvancedColorInfoChanged -= OnAdvancedColorInfoChanged; display.Dispose(); }
        };
    }

    private void OnAdvancedColorInfoChanged(DisplayInformation sender, object args) => root.DispatcherQueue.TryEnqueue(Refresh);

    public void Refresh()
    {
        if (disposed) return;
        bool hdr;
        try { hdr = display?.GetAdvancedColorInfo().CurrentAdvancedColorKind == DisplayAdvancedColorKind.HighDynamicRange; }
        catch (System.Runtime.InteropServices.COMException) { return; }
        if (hdr || accessibility.HighContrast)
        {
            // Native menu shadows/animations remain enabled. Only the window's
            // HDR Mica backdrop is bypassed; this does not change Windows HDR.
            root.Background = new SolidColorBrush(accessibility.HighContrast ? system.GetColorValue(UIColorType.Background)
                : root.ActualTheme == ElementTheme.Dark ? Color.FromArgb(255, 32, 32, 32) : Color.FromArgb(255, 243, 243, 243));
            if (window.SystemBackdrop is not null) window.SystemBackdrop = null;
        }
        else
        {
            root.Background = null;
            if (window.SystemBackdrop is not MicaBackdrop) window.SystemBackdrop = new MicaBackdrop();
        }
        var state = $"HDR={hdr}; HighContrast={accessibility.HighContrast}; Backdrop={window.SystemBackdrop?.GetType().Name ?? "Opaque"}; Theme={root.ActualTheme}";
        if (state != lastState)
        {
            lastState = state;
            try { Directory.CreateDirectory(UserSettings.Folder); File.WriteAllText(Path.Combine(UserSettings.Folder, "backdrop-state.txt"), state); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
