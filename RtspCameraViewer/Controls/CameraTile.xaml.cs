using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Threading;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using RtspCameraViewer.Models;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace RtspCameraViewer.Controls
{
    public enum CameraStatus { Connecting, Live, Error, Stopped }

    /// <summary>
    /// A single live-preview tile for one RTSP camera, with auto-reconnect.
    /// </summary>
    public partial class CameraTile : UserControl
    {
        public static readonly RoutedEvent FullscreenRequestedEvent = EventManager.RegisterRoutedEvent(
            nameof(FullscreenRequested), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(CameraTile));

        public event RoutedEventHandler FullscreenRequested
        {
            add => AddHandler(FullscreenRequestedEvent, value);
            remove => RemoveHandler(FullscreenRequestedEvent, value);
        }

        public static readonly RoutedEvent RemoveRequestedEvent = EventManager.RegisterRoutedEvent(
            nameof(RemoveRequested), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(CameraTile));

        public event RoutedEventHandler RemoveRequested
        {
            add => AddHandler(RemoveRequestedEvent, value);
            remove => RemoveHandler(RemoveRequestedEvent, value);
        }

        public Camera Camera { get; }

        private readonly LibVLC _libVlc;
        private MediaPlayer? _mediaPlayer;
        private readonly DispatcherTimer _reconnectTimer;
        private bool _disposed;

        /// <summary>True while this tile is meant to be streaming. False once StopPlayback is
        /// called, until StartPlayback brings it back.</summary>
        public bool IsRunning { get; private set; }

        /// <summary>
        /// Width/height of the decoded stream, once known. Null until the first frame arrives —
        /// the real aspect cannot be assumed, and guessing 16:9 for a camera that is closer to
        /// 4:3 leaves every tile pillarboxed.
        /// </summary>
        public double? VideoAspect { get; private set; }

        /// <summary>Raised on the UI thread the first time VideoAspect becomes known.</summary>
        public event Action? VideoAspectKnown;

        private DispatcherTimer? _aspectProbe;
        private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);

        public CameraTile(Camera camera, LibVLC libVlc)
        {
            InitializeComponent();
            Camera = camera;
            _libVlc = libVlc;
            NameText.Text = camera.Name;

            _reconnectTimer = new DispatcherTimer { Interval = ReconnectDelay };
            _reconnectTimer.Tick += (_, _) =>
            {
                _reconnectTimer.Stop();
                StartPlayback();
            };

            MouseEnter += (_, _) => HoverToolbar.Visibility = Visibility.Visible;
            MouseLeave += (_, _) => HoverToolbar.Visibility = Visibility.Collapsed;
            // Double-click the name bar to expand. A single click is deliberately inert: it was
            // previously enough to expand, which fired on any stray click while scanning the grid.
            // Only the name bar responds, not the video - the video surface is a native window
            // that consumes mouse input before WPF ever sees it, so a double-click there cannot
            // reach this handler. The toolbar button is the discoverable route either way.
            MouseLeftButtonDown += (_, e) =>
            {
                if (e.ClickCount == 2)
                    RaiseEvent(new RoutedEventArgs(FullscreenRequestedEvent, this));
            };

            // Deliberately NOT started here. The host decides which tiles may stream (see
            // MainWindow.RefreshLayout) so that creating tiles for a large import does not
            // briefly open every stream at once before the hidden ones are stopped again.
            SetStatus(CameraStatus.Stopped, "");
        }

        public void StartPlayback()
        {
            if (_disposed) return;
            IsRunning = true;
            SetStatus(CameraStatus.Connecting, "connecting…");

            try
            {
                DisposeMediaPlayer();

                var url = Camera.GetPlaybackUrl();
                if (string.IsNullOrWhiteSpace(url))
                {
                    SetStatus(CameraStatus.Error, "no URL");
                    return;
                }

                _mediaPlayer = new MediaPlayer(_libVlc)
                {
                    EnableHardwareDecoding = true
                };
                Video.MediaPlayer = _mediaPlayer;

                using var media = new Media(_libVlc, url, FromType.FromLocation);
                // Reduce latency and force TCP for more reliable RTSP behind NAT/firewalls.
                media.AddOption(":rtsp-tcp");
                media.AddOption(":network-caching=800");

                // Surveillance streams carry no audio worth playing, and every enabled track
                // costs a decoder, an output chain and buffers per tile.
                media.AddOption(":no-audio");
                media.AddOption(":no-spu");
                media.AddOption(":no-osd");
                media.AddOption(":no-video-title-show");

                _mediaPlayer.Media = media;

                // Named handlers so teardown can detach them, and BeginInvoke rather than
                // Invoke: these fire on LibVLC worker threads, and a BLOCKING Invoke there
                // deadlocks against a UI thread that is itself inside MediaPlayer.Stop().
                _mediaPlayer.Playing += OnPlayerPlaying;
                _mediaPlayer.EncounteredError += OnPlayerFailed;
                _mediaPlayer.EndReached += OnPlayerFailed;

                _mediaPlayer.Play();
            }
            catch (Exception ex)
            {
                SetStatus(CameraStatus.Error, "error");
                System.Diagnostics.Debug.WriteLine($"Camera '{Camera.Name}' failed to start: {ex.Message}");
                ScheduleReconnect();
            }
        }

        private void OnPlayerPlaying(object? sender, EventArgs e) =>
            Dispatcher.BeginInvoke(new Action(() =>
            {
                // BeginInvoke is queued, so this can land after the tile was stopped or disposed.
                if (_disposed || !IsRunning) return;
                SetStatus(CameraStatus.Live, "live");
                StartAspectProbe();
            }));

        /// <summary>
        /// Polls for the decoded frame size after playback starts. MediaPlayer.Size is not
        /// populated at the instant the Playing event fires — the video output has not been set
        /// up yet — so asking once there always came back empty and the host was left assuming
        /// an aspect instead of knowing one.
        /// </summary>
        private void StartAspectProbe()
        {
            if (VideoAspect != null || _aspectProbe != null) return;

            int attempts = 0;
            _aspectProbe = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _aspectProbe.Tick += (_, _) =>
            {
                attempts++;
                if (_disposed || !IsRunning || VideoAspect != null || attempts > 20)
                {
                    StopAspectProbe();
                    return;
                }

                var player = _mediaPlayer;
                if (player == null) return;

                try
                {
                    uint width = 0, height = 0;
                    if (player.Size(0, ref width, ref height) && width > 0 && height > 0)
                    {
                        VideoAspect = width / (double)height;
                        StopAspectProbe();
                        VideoAspectKnown?.Invoke();
                    }
                }
                catch { /* keep trying until the attempt budget runs out */ }
            };
            _aspectProbe.Start();
        }

        private void StopAspectProbe()
        {
            _aspectProbe?.Stop();
            _aspectProbe = null;
        }

        private void OnPlayerFailed(object? sender, EventArgs e) =>
            Dispatcher.BeginInvoke(new Action(HandlePlaybackFailure));

        private void HandlePlaybackFailure()
        {
            if (_disposed) return;
            SetStatus(CameraStatus.Error, "reconnecting…");
            ScheduleReconnect();
        }

        /// <summary>
        /// Stops this tile’s stream and releases its decoder without tearing the tile down —
        /// StartPlayback brings it back. A hidden tile that keeps streaming still consumes
        /// bandwidth and a hardware decoder slot, which is what corrupted the visible feeds when
        /// every store’s cameras ran at once.
        /// </summary>
        public void StopPlayback(string label = "stopped")
        {
            if (!IsRunning) return;
            IsRunning = false;
            _reconnectTimer.Stop();
            StopAspectProbe();
            DisposeMediaPlayer();
            SetStatus(CameraStatus.Stopped, label);
        }

        private void ScheduleReconnect()
        {
            // A stopped tile must not resurrect itself through the reconnect timer.
            if (_disposed || !IsRunning) return;
            _reconnectTimer.Stop();
            _reconnectTimer.Start();
        }

        private void SetStatus(CameraStatus status, string label)
        {
            StatusText.Text = label;

            bool live = status == CameraStatus.Live;
            PlaceholderPanel.Visibility = live ? Visibility.Collapsed : Visibility.Visible;

            PlaceholderText.Text = status switch
            {
                CameraStatus.Connecting => "Connecting…",
                CameraStatus.Error => "Reconnecting…",
                CameraStatus.Stopped => "Stopped",
                _ => ""
            };
            StatusDot.Fill = status switch
            {
                CameraStatus.Live => (Brush)FindResource("OkBrush"),
                CameraStatus.Error => (Brush)FindResource("ErrorBrush"),
                _ => (Brush)FindResource("WarnBrush")
            };
        }

        private void FullscreenButton_Click(object sender, RoutedEventArgs e) =>
            RaiseEvent(new RoutedEventArgs(FullscreenRequestedEvent, this));

        private void ReconnectButton_Click(object sender, RoutedEventArgs e) => StartPlayback();

        private void RemoveButton_Click(object sender, RoutedEventArgs e) =>
            RaiseEvent(new RoutedEventArgs(RemoveRequestedEvent, this));

        private void DisposeMediaPlayer()
        {
            var player = _mediaPlayer;
            if (player == null) return;
            _mediaPlayer = null;

            // Detach first so a callback arriving mid-teardown cannot touch a player that is
            // being disposed, and cannot queue more work against this tile.
            player.Playing -= OnPlayerPlaying;
            player.EncounteredError -= OnPlayerFailed;
            player.EndReached -= OnPlayerFailed;

            Video.MediaPlayer = null; // a WPF control property: must be set on the UI thread

            // Stop() blocks until LibVLC has wound its worker threads down. Doing that on the UI
            // thread is what hung the app when switching stores stopped ~26 players in one go,
            // so the blocking part runs on the thread pool instead.
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { player.Stop(); player.Dispose(); }
                catch { /* ignore teardown races */ }
            });
        }

        public void Dispose()
        {
            _disposed = true;
            IsRunning = false;
            _reconnectTimer.Stop();
            StopAspectProbe();
            DisposeMediaPlayer();

            // Disposing the VideoView is what actually destroys its native video window and the
            // overlay window that hosts the placeholder. Dropping the tile without this leaves
            // both on screen, unowned, painting over whatever replaced it.
            try { Video.Dispose(); } catch { /* already torn down */ }
        }
    }
}
