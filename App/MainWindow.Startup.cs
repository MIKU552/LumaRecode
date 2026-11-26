using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;

namespace VideoConverter;

public sealed partial class MainWindow
{
    private nint startupHwnd;
    private bool startupTransitionsDisabled;

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmFlush();

    private void PrepareStartupWindow()
    {
        startupHwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        startupTransitionsDisabled = StartupCover.SetTransitionsDisabled(startupHwnd, true);
    }

    internal void ActivateWithFirstFrame()
    {
        const int iconSizeDip = 56;
        const double fadeMilliseconds = 180;
        int iconSize = (int)Math.Round(iconSizeDip * GetDpiForWindow(startupHwnd) / 96d);
        var cover = StartupCover.TryShow(startupHwnd, appearance.StartupBackground, iconSize);
        if (cover is not null)
        {
            _ = DwmFlush();
        }

        bool waiting = true;
        bool frameQueued = false;
        bool backdropEnabled = false;
        long fadeStarted = 0;
        var timeout = DispatcherQueue.CreateTimer();
        timeout.Interval = TimeSpan.FromSeconds(2);
        timeout.IsRepeating = false;
        CompositionTarget.Rendered += OnRendered;
        Closed += OnClosed;
        timeout.Tick += OnTimeout;
        timeout.Start();
        try
        {
            Activate();
        }
        catch
        {
            Stop();
            throw;
        }

        void OnRendered(object? sender, RenderedEventArgs args)
        {
            if (!root.IsLoaded || frameQueued)
            {
                return;
            }

            bool wasBackdropEnabled = backdropEnabled;
            frameQueued = DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                if (waiting && wasBackdropEnabled == backdropEnabled)
                {
                    Advance(afterRender: true);
                }
            });
        }

        void OnTimeout(DispatcherQueueTimer sender, object args) => Advance(afterRender: false);
        void OnClosed(object sender, WindowEventArgs args) => Stop();

        void OnFadeTick(DispatcherQueueTimer sender, object args)
        {
            double progress = Math.Min(1, Stopwatch.GetElapsedTime(fadeStarted).TotalMilliseconds / fadeMilliseconds);
            double opacity = 1 - progress * progress * (3 - 2 * progress);
            if (progress >= 1 || cover?.SetOpacity(opacity) != true)
            {
                Stop();
            }
        }

        void Stop()
        {
            waiting = false;
            CompositionTarget.Rendered -= OnRendered;
            Closed -= OnClosed;
            timeout.Tick -= OnTimeout;
            timeout.Tick -= OnFadeTick;
            timeout.Stop();
            cover?.Dispose();
            if (startupTransitionsDisabled)
            {
                _ = StartupCover.SetTransitionsDisabled(startupHwnd, false);
                startupTransitionsDisabled = false;
            }
        }

        void Advance(bool afterRender)
        {
            if (!waiting)
            {
                return;
            }

            frameQueued = false;
            if (afterRender)
            {
                _ = DwmFlush();
            }
            if (!backdropEnabled)
            {
                backdropEnabled = true;
                appearance.EnableNativeBackdrop();
                // Let the live backdrop submit a frame before removing the native cover.
                timeout.Stop();
                timeout.Interval = TimeSpan.FromMilliseconds(350);
                timeout.Start();
                return;
            }

            if (!afterRender || cover is null || !new UISettings().AnimationsEnabled)
            {
                Stop();
                return;
            }

            CompositionTarget.Rendered -= OnRendered;
            timeout.Stop();
            timeout.Tick -= OnTimeout;
            timeout.Tick += OnFadeTick;
            timeout.Interval = TimeSpan.FromMilliseconds(16);
            timeout.IsRepeating = true;
            fadeStarted = Stopwatch.GetTimestamp();
            timeout.Start();
        }
    }
}
