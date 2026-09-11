using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Threading;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using RtspCameraViewer.Models;
using RtspCameraViewer.Services;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace RtspCameraViewer.Controls
{
    public enum CameraStatus { Connecting, Live, Error, Stopped }

    public enum MoveDirection { Up, Down, Left, Right }

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

        /// <summary>Raised when one of the edge arrows is clicked.</summary>
        public event Action<CameraTile, MoveDirection>? MoveRequested;

        private const double EdgeIdle = 6;
        private const double EdgeActive = 22;

        // Which moves make sense from this tile's current cell, set by the host after each layout.
        // An edge with nowhere to go never widens, so a dead arrow is never offered.
        private bool _canUp, _canDown, _canLeft, _canRight;

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

        /// <summary>
        /// Whether the current playback has produced a decoded frame. The Playing event fires
        /// before that, and a video surface with nothing to show renders solid white - so the
        /// placeholder stays up until a frame actually exists.
        /// </summary>
        private bool _frameSeen;
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
            MouseLeave += (_, _) =>
            {
                HoverToolbar.Visibility = Visibility.Collapsed;
                CollapseAllEdges();
            };
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
            _frameSeen = false;
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

                // Display shape, if the user asked for one. Set on the player rather than as a
                // media option: video filters are not reliably honoured per-media, whereas these
                // two properties are, and they are what the shape actually needs.
                if (!string.IsNullOrWhiteSpace(Camera.DisplayAspect))
                {
                    if (Camera.DisplayFit == DisplayFit.Crop)
                        _mediaPlayer.CropGeometry = Camera.DisplayAspect;
                    else
                        _mediaPlayer.AspectRatio = Camera.DisplayAspect;
                }
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
            // Runs on every playback, not just the first: it is also what reveals the video once
            // a frame has been decoded, and a restarted stream is white again until then.
            if (_aspectProbe != null) return;

            int attempts = 0;
            _aspectProbe = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _aspectProbe.Tick += (_, _) =>
            {
                attempts++;
                if (_disposed || !IsRunning || _frameSeen || attempts > 80)
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
                        _frameSeen = true;
                        StopAspectProbe();
                        PlaceholderPanel.Visibility = Visibility.Collapsed;

                        if (VideoAspect == null)
                        {
                            VideoAspect = width / (double)height;
                            VideoAspectKnown?.Invoke();
                        }
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
            PlaceholderPanel.Visibility = live && _frameSeen ? Visibility.Collapsed : Visibility.Visible;

            PlaceholderText.Text = status switch
            {
                CameraStatus.Connecting => "Connecting…",
                CameraStatus.Error => "Reconnecting…",
                CameraStatus.Stopped => "Stopped",
                _ => "Starting video…"
            };
            StatusDot.Fill = status switch
            {
                CameraStatus.Live => (Brush)FindResource("OkBrush"),
                CameraStatus.Error => (Brush)FindResource("ErrorBrush"),
                // Stopped is not a warning - a grid of paused cameras should read as calm, not as
                // a wall of yellow alerts.
                CameraStatus.Stopped => (Brush)FindResource("TextSecondary"),
                _ => (Brush)FindResource("WarnBrush")
            };
        }

        /// <summary>Tells the tile which arrows can move it from where it now sits.</summary>
        public void SetMoveAvailability(bool up, bool down, bool left, bool right)
        {
            _canUp = up; _canDown = down; _canLeft = left; _canRight = right;
            CollapseAllEdges();
        }

        private bool CanMove(MoveDirection d) => d switch
        {
            MoveDirection.Up => _canUp,
            MoveDirection.Down => _canDown,
            MoveDirection.Left => _canLeft,
            _ => _canRight
        };

        private static MoveDirection DirectionOf(object sender) =>
            Enum.Parse<MoveDirection>((string)((FrameworkElement)sender).Tag);

        private void Edge_MouseEnter(object sender, MouseEventArgs e)
        {
            var direction = DirectionOf(sender);
            if (!CanMove(direction)) return;
            SetEdge((Border)sender, direction, expanded: true);
        }

        private void Edge_MouseLeave(object sender, MouseEventArgs e) =>
            SetEdge((Border)sender, DirectionOf(sender), expanded: false);

        private static void SetEdge(Border edge, MoveDirection direction, bool expanded)
        {
            double size = expanded ? EdgeActive : EdgeIdle;
            if (direction is MoveDirection.Up or MoveDirection.Down) edge.Height = size;
            else edge.Width = size;
            if (edge.Child is UIElement arrow)
                arrow.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        }

        private void CollapseAllEdges()
        {
            SetEdge(EdgeUp, MoveDirection.Up, false);
            SetEdge(EdgeDown, MoveDirection.Down, false);
            SetEdge(EdgeLeft, MoveDirection.Left, false);
            SetEdge(EdgeRight, MoveDirection.Right, false);
        }

        private void Move_Click(object sender, RoutedEventArgs e)
        {
            var direction = DirectionOf(sender);
            CollapseAllEdges();
            if (CanMove(direction)) MoveRequested?.Invoke(this, direction);
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
