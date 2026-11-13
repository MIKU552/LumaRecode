using Microsoft.Graphics.Display;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace VideoConverter;

/// <summary>Keep native Mica within the XAML composition tree on HDR displays.</summary>
public sealed class HdrBackdropCompatibility
{
    private readonly Grid root;
    private readonly Window window;
    private readonly UserSettings settings;
    private readonly DisplayInformation? display;
    private readonly UISettings system = new();
    private readonly AccessibilitySettings accessibility = new();
    private readonly SystemBackdropElement micaLayer;
    private bool hdrActive;
    private bool effectsEvent;
    private bool disposed;
    private string lastState = "";

    public HdrBackdropCompatibility(Grid root, Window window, UserSettings settings)
    {
        this.root = root;
        this.window = window;
        this.settings = settings;
        micaLayer = new SystemBackdropElement { IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        Grid.SetRowSpan(micaLayer, Math.Max(1, root.RowDefinitions.Count));
        root.Children.Insert(0, micaLayer);
        try
        {
            system.AdvancedEffectsEnabledChanged += OnAdvancedEffectsChanged;
            effectsEvent = true;
        }
        catch (System.Runtime.InteropServices.COMException) { }
        window.Activated += (_, e) => { if (e.WindowActivationState != WindowActivationState.Deactivated) { Refresh(); } };
        try
        {
            display = DisplayInformation.CreateForWindowId(window.AppWindow.Id);
            display.AdvancedColorInfoChanged += OnAdvancedColorInfoChanged;
        }
        catch (System.Runtime.InteropServices.COMException) { }
        window.Closed += (_, _) =>
        {
            disposed = true;
            if (effectsEvent)
            {
                system.AdvancedEffectsEnabledChanged -= OnAdvancedEffectsChanged;
            }

            if (display is not null)
            {
                display.AdvancedColorInfoChanged -= OnAdvancedColorInfoChanged;
                display.Dispose();
            }
        };
    }

    private void OnAdvancedColorInfoChanged(DisplayInformation sender, object args) => root.DispatcherQueue.TryEnqueue(Refresh);
    private void OnAdvancedEffectsChanged(UISettings sender, object args) => root.DispatcherQueue.TryEnqueue(Refresh);

    public void Refresh()
    {
        if (disposed)
        {
            return;
        }

        bool hdr;
        try
        {
            hdr = display?.GetAdvancedColorInfo().CurrentAdvancedColorKind == DisplayAdvancedColorKind.HighDynamicRange;
        }
        catch (System.Runtime.InteropServices.COMException) { return; }
        hdrActive = hdr;
        if (accessibility.HighContrast || settings.Backdrop == "solid")
        {
            micaLayer.Visibility = Visibility.Collapsed;
            micaLayer.SystemBackdrop = null;
            root.Background = new SolidColorBrush(accessibility.HighContrast ? system.GetColorValue(UIColorType.Background)
                : root.ActualTheme == ElementTheme.Dark ? Color.FromArgb(255, 32, 32, 32) : Color.FromArgb(255, 243, 243, 243));
            if (window.SystemBackdrop is not null)
            {
                window.SystemBackdrop = null;
            }
        }
        // Keep genuine Mica in the XAML tree on HDR displays. A window-level
        // backdrop combined with in-root popup composition caused visible halos
        // on the tested system; Appearance also uses a separate popup surface.
        else if (hdr)
        {
            root.Background = new SolidColorBrush(root.ActualTheme == ElementTheme.Dark ? Color.FromArgb(255, 30, 30, 30) : Color.FromArgb(255, 245, 245, 245));
            if (window.SystemBackdrop is not null)
            {
                window.SystemBackdrop = null;
            }

            if (micaLayer.SystemBackdrop is not MicaBackdrop)
            {
                micaLayer.SystemBackdrop = new MicaBackdrop();
            }

            micaLayer.Visibility = Visibility.Visible;
        }
        else
        {
            micaLayer.Visibility = Visibility.Collapsed;
            micaLayer.SystemBackdrop = null;
            root.Background = null;
            if (window.SystemBackdrop is not MicaBackdrop)
            {
                window.SystemBackdrop = new MicaBackdrop();
            }
        }
        RecordState();
    }

    // Write only when the effective mode changes. This small diagnostic helps
    // distinguish a display/driver fallback from an application theme choice.
    private void RecordState()
    {
        if (disposed)
        {
            return;
        }

        var state = $"HDR={hdrActive}; HighContrast={accessibility.HighContrast}; Backdrop={window.SystemBackdrop?.GetType().Name ?? (micaLayer.Visibility == Visibility.Visible ? "NativeMicaElement" : "Opaque")}; Theme={root.ActualTheme}";
        if (state != lastState)
        {
            lastState = state;
            try
            {
                Directory.CreateDirectory(UserSettings.Folder);
                File.WriteAllText(Path.Combine(UserSettings.Folder, "backdrop-state.txt"), state);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
