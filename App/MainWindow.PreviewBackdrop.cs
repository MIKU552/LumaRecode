using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI.ViewManagement;

namespace VideoConverter;

public sealed partial class MainWindow
{
    private const double BackgroundBlurOpacity = 0.53;

    // This normalized, symmetric binomial kernel approximates a Gaussian.
    // Two separable passes preserve the softness without a full-size capture.
    private static ReadOnlySpan<int> BlurWeights => [1, 12, 66, 220, 495, 792, 924, 792, 495, 220, 66, 12, 1];
    private Storyboard? previewTransition;
    private ScrollBarVisibility? previousWorkspaceScrollbar;
    private int previewOverlayGeneration;
    private bool previewOverlayRequested;

    private void ShowPreviewOverlay(bool show)
    {
        if (show == previewOverlayRequested)
        {
            return;
        }
        previewOverlayRequested = show;
        int generation = ++previewOverlayGeneration;
        var overlay = Control<Grid>("PreviewOverlay");
        var workspace = Control<Grid>("Workspace");
        var panel = Control<Grid>("OverlayPreviewSurface");
        var blur = Control<Image>("BlurredWorkspace");
        var offset = (TranslateTransform)panel.RenderTransform;

        if (previewTransition is { } running)
        {
            // Keep the current values when reversing an interrupted transition.
            double workspaceOpacity = workspace.Opacity;
            double panelOpacity = panel.Opacity;
            double blurOpacity = blur.Opacity;
            double y = offset.Y;
            previewTransition = null;
            running.Stop();
            workspace.Opacity = workspaceOpacity;
            panel.Opacity = panelOpacity;
            blur.Opacity = blurOpacity;
            offset.Y = y;
        }
        if (!show)
        {
            if (PhotoMode || panel.Opacity <= 0)
            {
                CompletePreviewClose();
            }
            else
            {
                AnimatePreviewOverlay(false, generation);
            }
            return;
        }

        var scroll = Control<ScrollViewer>("WorkspaceScroll");
        previousWorkspaceScrollbar ??= scroll.VerticalScrollBarVisibility;
        // Hide the scroll indicator before taking the snapshot. Hidden keeps
        // scrolling enabled and lets the workspace restore its original layout.
        scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
        var material = Control<SystemBackdropElement>("PreviewMaterial");
        bool highContrast = new AccessibilitySettings().HighContrast;
        if (!highContrast && settings.Backdrop != "solid")
        {
            material.SystemBackdrop ??= new MicaBackdrop();
        }
        if (overlay.Visibility != Visibility.Visible)
        {
            offset.Y = new UISettings().AnimationsEnabled ? 12 : 0;
            overlay.Visibility = Visibility.Visible;
        }
        _ = PreparePreviewBackdropAsync(generation, !highContrast && new UISettings().AdvancedEffectsEnabled);
    }

    private async Task PreparePreviewBackdropAsync(int generation, bool effectsEnabled)
    {
        var blur = Control<Image>("BlurredWorkspace");
        if (effectsEnabled && blur.Source is null)
        {
            var snapshot = await CapturePreviewBackdropAsync(Control<Grid>("Workspace"), generation);
            if (closing || generation != previewOverlayGeneration)
            {
                return;
            }
            blur.Source = snapshot;
        }

        if (!closing && generation == previewOverlayGeneration)
        {
            AnimatePreviewOverlay(true, generation);
        }
    }

    private void AnimatePreviewOverlay(bool open, int generation)
    {
        var panel = Control<Grid>("OverlayPreviewSurface");
        var workspace = Control<Grid>("Workspace");
        var blur = Control<Image>("BlurredWorkspace");
        var offset = (TranslateTransform)panel.RenderTransform;
        bool motion = new UISettings().AnimationsEnabled;
        double y = open || !motion ? 0 : 8;
        double panelOpacity = open ? 1 : 0;
        double blurOpacity = open && blur.Source is not null ? BackgroundBlurOpacity : 0;
        double workspaceOpacity = blurOpacity > 0 ? 0 : 1;
        var duration = new Duration(TimeSpan.FromMilliseconds(motion ? open ? 205 : 160 : 100));
        var easing = new CubicEase { EasingMode = open ? EasingMode.EaseOut : EasingMode.EaseInOut };
        var transition = new Storyboard();
        Add(panel, "Opacity", panel.Opacity, panelOpacity);
        Add(blur, "Opacity", blur.Opacity, blurOpacity);
        Add(workspace, "Opacity", workspace.Opacity, workspaceOpacity);
        Add(offset, "Y", offset.Y, y);

        // Start all four properties on one clock so the backdrop does not
        // appear separately from the panel. Keep alpha neutral over the Mica.
        previewTransition = transition;
        transition.Completed += (_, _) =>
        {
            if (generation != previewOverlayGeneration || !ReferenceEquals(previewTransition, transition))
            {
                return;
            }
            previewTransition = null;
            panel.Opacity = panelOpacity;
            blur.Opacity = blurOpacity;
            workspace.Opacity = workspaceOpacity;
            offset.Y = y;
            transition.Stop();
            if (!open)
            {
                CompletePreviewClose();
            }
        };
        transition.Begin();

        void Add(DependencyObject target, string property, double from, double to)
        {
            if (from == to)
            {
                return;
            }
            var animation = new DoubleAnimation { From = from, To = to, Duration = duration, EasingFunction = easing };
            Storyboard.SetTarget(animation, target);
            Storyboard.SetTargetProperty(animation, property);
            transition.Children.Add(animation);
        }
    }

    private void CompletePreviewClose()
    {
        Control<Grid>("PreviewOverlay").Visibility = Visibility.Collapsed;
        Control<Grid>("OverlayPreviewSurface").Opacity = 0;
        Control<SystemBackdropElement>("PreviewMaterial").SystemBackdrop = null;
        Control<Grid>("Workspace").Opacity = 1;
        var blur = Control<Image>("BlurredWorkspace");
        blur.Source = null;
        blur.Opacity = 0;
        if (previousWorkspaceScrollbar is { } visibility)
        {
            Control<ScrollViewer>("WorkspaceScroll").VerticalScrollBarVisibility = visibility;
            previousWorkspaceScrollbar = null;
        }
        if (!PhotoMode)
        {
            ClosePreviewPlayer();
            CancelFramePreview();
            Control<Image>("FramePreview").Source = null;
            Control<Microsoft.UI.Xaml.Controls.Primitives.ToggleButton>("ShowPreview").Focus(FocusState.Programmatic);
        }
    }

    private async Task<WriteableBitmap?> CapturePreviewBackdropAsync(Grid workspace, int generation)
    {
        // Only capture XAML controls. The original OS material stays behind
        // both regions of the window and does not enter the blur kernel.
        double scale = 256 / Math.Max(workspace.ActualWidth, workspace.ActualHeight);
        if (!double.IsFinite(scale) || workspace.ActualWidth < 1 || workspace.ActualHeight < 1)
        {
            return null;
        }
        try
        {
            var snapshot = new RenderTargetBitmap();
            await snapshot.RenderAsync(workspace, Math.Max(1, (int)Math.Round(workspace.ActualWidth * scale)),
                Math.Max(1, (int)Math.Round(workspace.ActualHeight * scale)));
            var buffer = await snapshot.GetPixelsAsync();
            int width = snapshot.PixelWidth;
            int height = snapshot.PixelHeight;
            if (closing || generation != previewOverlayGeneration || width <= 0 || height <= 0 ||
                buffer.Length != (long)width * height * 4)
            {
                return null;
            }

            // Average premultiplied color and alpha together so the margins and
            // gaps between controls still reveal the live Mica underneath.
            byte[] pixels = buffer.ToArray();
            await Task.Run(() =>
            {
                byte[] pass = new byte[pixels.Length];
                BlurLines(pixels, pass, width, height, 4, width * 4);
                BlurLines(pass, pixels, height, width, width * 4, 4);
            });
            var bitmap = new WriteableBitmap(width, height);
            using (var output = bitmap.PixelBuffer.AsStream())
            {
                output.Write(pixels);
            }
            bitmap.Invalidate();
            return bitmap;
        }
        catch (COMException)
        {
            // A lost compositor can prevent capture during a display change.
            // The panel can still open over the live workspace.
            return null;
        }
    }

    private static void BlurLines(byte[] input, byte[] output, int count, int lines, int step, int pitch)
    {
        var weights = BlurWeights;
        int radius = weights.Length / 2;
        for (int line = 0; line < lines; line++)
        {
            int offset = line * pitch;
            for (int index = 0; index < count; index++)
            {
                int blue = 0, green = 0, red = 0, alpha = 0;
                for (int tap = 0; tap < weights.Length; tap++)
                {
                    int sample = offset + Math.Clamp(index + tap - radius, 0, count - 1) * step;
                    int weight = weights[tap];
                    blue += input[sample] * weight;
                    green += input[sample + 1] * weight;
                    red += input[sample + 2] * weight;
                    alpha += input[sample + 3] * weight;
                }

                int target = offset + index * step;
                output[target] = (byte)((blue + 2048) >> 12);
                output[target + 1] = (byte)((green + 2048) >> 12);
                output[target + 2] = (byte)((red + 2048) >> 12);
                output[target + 3] = (byte)((alpha + 2048) >> 12);
            }
        }
    }
}
