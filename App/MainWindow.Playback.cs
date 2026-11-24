using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using VideoConverter.Core;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;

namespace VideoConverter;

public sealed partial class MainWindow
{
    // The player owns decoding during live navigation. The source frame index
    // remains authoritative for stepping, selection, and export.
    private MediaPlayer? previewPlayer;
    private MediaSource? playbackSource;
    private DispatcherQueueTimer? playbackClock;
    private string? playbackPath;
    private int playbackGeneration;
    private int? pendingPlayerSeek;
    private long seekStarted;
    private bool playerReady, previewPlaying, draggingPreview;

    private void InitializePlayback()
    {
        Control<Button>("PlayPreview").Click += (_, _) => TogglePreviewPlayback();
        var slider = Control<Slider>("FrameSeek");
        slider.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) =>
        {
            PausePreviewPlayback(drawExact: false);
            draggingPreview = true;
            QueuePlayerSeek(SelectedFrame);
        }), handledEventsToo: true);
        var endDrag = new PointerEventHandler((_, _) => FinishPreviewDrag());
        slider.AddHandler(UIElement.PointerReleasedEvent, endDrag, handledEventsToo: true);
        slider.AddHandler(UIElement.PointerCanceledEvent, endDrag, handledEventsToo: true);
        slider.AddHandler(UIElement.PointerCaptureLostEvent, endDrag, handledEventsToo: true);

        playbackClock = DispatcherQueue.CreateTimer();
        playbackClock.Interval = TimeSpan.FromMilliseconds(33);
        playbackClock.Tick += (_, _) => UpdatePlaybackPosition();
    }

    private async Task OpenPreviewPlayerAsync(MediaInfo source)
    {
        if (playbackPath == source.Path)
        {
            return;
        }
        ClosePreviewPlayer();
        playbackPath = source.Path;
        int generation = playbackGeneration;
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(source.Path);
            if (closing || generation != playbackGeneration || !PreviewIsVisible)
            {
                return;
            }
            var player = new MediaPlayer
            {
                AutoPlay = false,
                IsMuted = true,
                CommandManager = { IsEnabled = false }
            };
            previewPlayer = player;
            playbackSource = MediaSource.CreateFromStorageFile(file);
            // Media callbacks can arrive after close or a source replacement.
            // Capture the owner, rather than comparing the event's COM projection.
            player.MediaOpened += (_, _) => DispatcherQueue.TryEnqueue(() =>
            {
                if (previewPlayer == player)
                {
                    playerReady = true;
                    UpdatePlaybackButton();
                    if (displayedFrame != SelectedFrame || draggingPreview)
                    {
                        QueuePlayerSeek(SelectedFrame);
                    }
                }
            });
            player.MediaEnded += (_, _) => DispatcherQueue.TryEnqueue(() =>
            {
                if (previewPlayer == player)
                {
                    PausePreviewPlayback(drawExact: true);
                }
            });
            player.MediaFailed += (_, _) => DispatcherQueue.TryEnqueue(() =>
            {
                if (previewPlayer == player)
                {
                    ClosePreviewPlayer();
                    playbackPath = source.Path;
                    QueueFramePreview();
                }
            });
            player.PlaybackSession.SeekCompleted += (_, _) => DispatcherQueue.TryEnqueue(() =>
            {
                if (previewPlayer == player)
                {
                    seekStarted = 0;
                }
            });
            Control<MediaPlayerElement>("SourcePlayer").SetMediaPlayer(player);
            player.Source = playbackSource;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or ArgumentException)
        {
            if (generation == playbackGeneration)
            {
                ClosePreviewPlayer();
                playbackPath = source.Path;
            }
        }
    }

    private bool PreviewIsVisible => sourceMedia is not null && (PhotoMode || previewOverlayRequested);

    private void SetPlayerPreviewVisible(bool visible)
    {
        Control<MediaPlayerElement>("SourcePlayer").Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        Control<Image>("FramePreview").Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
        Control<TextBlock>("PreviewModeNote").Text = L.T(visible ? "原片预览" : "SDR 预览");
    }

    private void UpdatePlaybackButton()
    {
        var button = Control<Button>("PlayPreview");
        button.IsEnabled = playerReady && frameTimeline is not null && activeJob is null;
        var label = L.T(previewPlaying ? "暂停预览" : "播放预览");
        AutomationProperties.SetName(button, label);
        ToolTipService.SetToolTip(button, label);
        Control<SymbolIcon>("PlaybackGlyph").Symbol = previewPlaying ? Symbol.Pause : Symbol.Play;
    }

    private void QueuePlayerSeek(int frame)
    {
        if (!playerReady || frameTimeline is null || previewPlayer?.PlaybackSession.CanSeek != true)
        {
            return;
        }
        pendingPlayerSeek = frame;
        SetPlayerPreviewVisible(true);
        Control<ProgressRing>("PreviewBusy").IsActive = false;
        playbackClock?.Start();
    }

    private void UpdatePlaybackPosition()
    {
        if (previewPlayer is not { } player || frameTimeline is not { } timeline)
        {
            playbackClock?.Stop();
            return;
        }
        if (pendingPlayerSeek is { } frame)
        {
            // Keep only the newest target while the decoder is seeking. A
            // deadline also handles decoders that coalesce completion events.
            if (seekStarted != 0 && Stopwatch.GetElapsedTime(seekStarted) < TimeSpan.FromMilliseconds(140))
            {
                return;
            }
            pendingPlayerSeek = null;
            seekStarted = Stopwatch.GetTimestamp();
            SeekPlayer(player, timeline, frame);
        }
        else if (previewPlaying)
        {
            SynchronizePlayhead(player, timeline);
        }
        if (!previewPlaying && pendingPlayerSeek is null)
        {
            playbackClock?.Stop();
        }
    }

    private static void SeekPlayer(MediaPlayer player, FrameTimeline timeline, int frame)
    {
        // Seek slightly inside the selected frame to avoid rounding to its
        // predecessor when the player converts between container and WinRT ticks.
        double next = frame + 1 < timeline.Count ? timeline.Position(frame + 1) : timeline.Duration;
        double current = timeline.Position(frame);
        player.PlaybackSession.Position = TimeSpan.FromSeconds(current + Math.Clamp((next - current) / 4, 0, 0.003));
    }

    private void SynchronizePlayhead(MediaPlayer player, FrameTimeline timeline)
    {
        int frame = timeline.AtOrBefore(player.PlaybackSession.Position.TotalSeconds);
        if (SelectedFrame == frame)
        {
            return;
        }
        frameUiUpdating = true;
        Control<Slider>("FrameSeek").Value = frame + 1;
        Control<NumberBox>("FrameNumber").Value = frame + 1;
        frameUiUpdating = false;
        ShowFramePosition(timeline, frame);
    }

    private void TogglePreviewPlayback()
    {
        if (!playerReady || previewPlayer is not { } player || frameTimeline is not { } timeline)
        {
            return;
        }
        if (previewPlaying)
        {
            PausePreviewPlayback(drawExact: true);
            return;
        }

        // Cancel stale still work when switching to the real-time surface.
        // The still decoder resumes only after the playhead has settled.
        CancelFramePreview();
        previewError = "";
        previewPlaying = true;
        SetPlayerPreviewVisible(true);
        SeekPlayer(player, timeline, SelectedFrame == timeline.Count - 1 ? 0 : SelectedFrame);
        pendingPlayerSeek = null;
        player.Play();
        playbackClock?.Start();
        UpdatePlaybackButton();
    }

    private void PausePreviewPlayback(bool drawExact)
    {
        if (previewPlaying && previewPlayer is { } player && frameTimeline is { } timeline)
        {
            player.Pause();
            SynchronizePlayhead(player, timeline);
        }
        previewPlaying = false;
        UpdatePlaybackButton();
        if (frameTimeline is not null && !frameUiUpdating)
        {
            frameUiUpdating = true;
            if (!motionRangeEdited)
            {
                SetDefaultMotionRange();
            }
            frameUiUpdating = false;
            UpdateFrameSelection();
            UpdateFrameOutput();
        }
        if (drawExact)
        {
            QueueFramePreview();
        }
    }

    private void FinishPreviewDrag()
    {
        if (!draggingPreview)
        {
            return;
        }
        draggingPreview = false;
        PausePreviewPlayback(drawExact: false);
        UpdateTransferStats();
        QueueFramePreview();
    }

    private void ClosePreviewPlayer()
    {
        playbackGeneration++;
        playbackPath = null;
        playerReady = previewPlaying = draggingPreview = false;
        pendingPlayerSeek = null;
        seekStarted = 0;
        playbackClock?.Stop();
        if (previewPlayer is { } player)
        {
            previewPlayer = null;
            // WinUI still owns the rendering surface until SetMediaPlayer.
            // Detach before disposal, including when the overlay is closing.
            if (player.PlaybackSession.CanPause)
            {
                player.Pause();
            }
            player.Source = null;
            Control<MediaPlayerElement>("SourcePlayer").SetMediaPlayer(null);
            playbackSource?.Dispose();
            playbackSource = null;
            player.Dispose();
        }
        SetPlayerPreviewVisible(false);
        UpdatePlaybackButton();
    }

}
