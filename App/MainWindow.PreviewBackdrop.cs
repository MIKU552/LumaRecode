using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;
using VirtualKey = Windows.System.VirtualKey;

namespace VideoConverter;

public sealed partial class MainWindow
{
    private ScrollBarVisibility? previousWorkspaceScrollbar;
    private int previewOverlayGeneration;
    private bool previewOverlayRequested;

    private void InitializePreviewOverlayInput(Button close)
    {
        var panel = Control<Grid>("OverlayPreviewSurface");
        Control<Border>("PreviewDismissArea").Tapped += (_, args) =>
        {
            if (previewOverlayRequested)
            {
                args.Handled = true;
                CloseTimelineOverlay();
            }
        };

        // A window-scoped shortcut remains available even after the native
        // caption has handled a pointer interaction.
        var escape = new KeyboardAccelerator { Key = VirtualKey.Escape };
        escape.Invoked += (_, args) =>
        {
            if (previewOverlayRequested)
            {
                args.Handled = true;
                CloseTimelineOverlay();
            }
        };
        root.KeyboardAccelerators.Add(escape);

        root.PreviewKeyDown += (_, args) =>
        {
            if (!previewOverlayRequested || frameTimeline is null ||
                args.Key is not (VirtualKey.Left or VirtualKey.Right))
            {
                return;
            }
            for (var element = args.OriginalSource as DependencyObject; element is not null;
                element = VisualTreeHelper.GetParent(element))
            {
                if (element is TextBox or NumberBox or ComboBox)
                {
                    return;
                }
                if (element == panel)
                {
                    break;
                }
            }

            ChangePreviewFrame(SelectedFrame + (args.Key == VirtualKey.Left ? -1 : 1));
            args.Handled = true;
        };

        AppWindow.Changed += (_, args) =>
        {
            if (args.DidPositionChange && previewOverlayRequested)
            {
                DispatcherQueue.TryEnqueue(RestorePreviewFocus);
            }
        };
        Activated += (_, args) =>
        {
            if (args.WindowActivationState != WindowActivationState.Deactivated && previewOverlayRequested)
            {
                RestorePreviewFocus();
            }
        };
        Closed += (_, _) => previewOverlayGeneration++;

        void RestorePreviewFocus()
        {
            if (!previewOverlayRequested || root.XamlRoot is null)
            {
                return;
            }
            for (var element = FocusManager.GetFocusedElement(root.XamlRoot) as DependencyObject; element is not null;
                element = VisualTreeHelper.GetParent(element))
            {
                if (element == panel)
                {
                    return;
                }
            }

            close.Focus(FocusState.Programmatic);
        }
    }

    private void ShowPreviewOverlay(bool show)
    {
        if (show == previewOverlayRequested)
        {
            return;
        }

        previewOverlayRequested = show;
        int generation = ++previewOverlayGeneration;
        var overlay = Control<Grid>("PreviewOverlay");
        var panel = ElementCompositionPreview.GetElementVisual(Control<Grid>("OverlayPreviewSurface"));
        var workspaceElement = Control<Grid>("Workspace");
        var workspace = ElementCompositionPreview.GetElementVisual(workspaceElement);
        var scroll = Control<ScrollViewer>("WorkspaceScroll");

        if (show)
        {
            workspaceElement.IsHitTestVisible = false;
            previousWorkspaceScrollbar ??= scroll.VerticalScrollBarVisibility;
            if (overlay.Visibility != Visibility.Visible)
            {
                // Prepare the composited layer before showing its XAML host.
                // The main window's Mica always sits behind both layers.
                panel.Opacity = 0;
                var material = Control<SystemBackdropElement>("PreviewMaterial");
                if (settings.Backdrop != "solid" && !new AccessibilitySettings().HighContrast)
                {
                    material.SystemBackdrop = new MicaBackdrop();
                }

                overlay.Visibility = Visibility.Visible;
            }
        }
        else
        {
            if (previousWorkspaceScrollbar is { } visibility)
            {
                // Restore the original mode while the workspace is still
                // faded out, so its scrollbar fades in with the other controls.
                scroll.VerticalScrollBarVisibility = visibility;
            }
            if (PhotoMode)
            {
                panel.StopAnimation("Opacity");
                workspace.StopAnimation("Opacity");
                workspace.Opacity = 1;
                workspaceElement.IsHitTestVisible = true;
                CompletePreviewClose();
                return;
            }
        }

        var compositor = panel.Compositor;
        bool reducedMotion = !new UISettings().AnimationsEnabled;
        int openMilliseconds = reducedMotion ? 110 : 200;
        int closeMilliseconds = reducedMotion ? 80 : 115;
        var duration = TimeSpan.FromMilliseconds(show ? openMilliseconds : closeMilliseconds);
        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        batch.Completed += (_, _) =>
        {
            batch.Dispose();
            if (generation != previewOverlayGeneration)
            {
                return;
            }
            workspace.Opacity = 0;
            panel.Opacity = show ? 1 : 0;
            if (show)
            {
                // Keep the layout alive behind the overlay. The scrollbar has
                // already faded with the workspace when its mode changes here.
                scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
            }
            else
            {
                CompletePreviewClose();
                RevealWorkspaceAfterPreviewFrame(workspaceElement, workspace, generation, reducedMotion);
            }
        };

        AnimatePreviewOpacity(workspace, 0, duration);
        AnimatePreviewOpacity(panel, show ? 1 : 0, duration);
        batch.End();
    }

    private void RevealWorkspaceAfterPreviewFrame(FrameworkElement workspaceElement, Visual workspace,
        int generation, bool reducedMotion)
    {
        // SystemBackdropElement has its own compositor surface. Wait until
        // XAML has submitted its removal before fading in the controls behind it.
        var timeout = DispatcherQueue.CreateTimer();
        timeout.Interval = TimeSpan.FromMilliseconds(55);
        timeout.IsRepeating = false;
        bool waiting = true;
        bool queued = false;
        CompositionTarget.Rendered += Rendered;
        timeout.Tick += TimedOut;
        timeout.Start();

        void Rendered(object? sender, RenderedEventArgs args)
        {
            if (!queued)
            {
                queued = DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, Reveal);
            }
        }

        void TimedOut(DispatcherQueueTimer sender, object args) => Reveal();

        void Reveal()
        {
            if (!waiting)
            {
                return;
            }
            waiting = false;
            CompositionTarget.Rendered -= Rendered;
            timeout.Tick -= TimedOut;
            timeout.Stop();
            if (closing || generation != previewOverlayGeneration)
            {
                return;
            }
            workspaceElement.IsHitTestVisible = true;
            AnimatePreviewOpacity(workspace, 1, TimeSpan.FromMilliseconds(reducedMotion ? 85 : 120));
        }
    }

    private static void AnimatePreviewOpacity(Visual visual, float opacity, TimeSpan duration)
    {
        var compositor = visual.Compositor;
        using var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.24f, 0.05f), new Vector2(0.30f, 1));
        using var animation = compositor.CreateScalarKeyFrameAnimation();
        animation.Duration = duration;
        animation.StopBehavior = AnimationStopBehavior.SetToFinalValue;
        animation.InsertExpressionKeyFrame(0, "this.StartingValue");
        animation.InsertKeyFrame(1, opacity, easing);
        visual.StartAnimation("Opacity", animation);
    }

    private void CompletePreviewClose()
    {
        if (!PhotoMode)
        {
            // Detach the player while its WinUI surface is still connected.
            ClosePreviewPlayer();
            CancelFramePreview();
            Control<Image>("FramePreview").Source = null;
        }

        Control<SystemBackdropElement>("PreviewMaterial").SystemBackdrop = null;
        Control<Grid>("PreviewOverlay").Visibility = Visibility.Collapsed;
        previousWorkspaceScrollbar = null;
        if (!PhotoMode)
        {
            Control<Microsoft.UI.Xaml.Controls.Primitives.ToggleButton>("ShowPreview").Focus(FocusState.Programmatic);
        }
    }
}
