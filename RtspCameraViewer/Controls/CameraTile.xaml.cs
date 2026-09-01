using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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

            MouseEnter += (_, _) => AnimateToolbar(1);
            MouseLeave += (_, _) => AnimateToolbar(0);
            // Double-click anywhere on the cell expands it. A single click is deliberately inert:
            // it was previously enough to expand, which fired on any stray click while scanning
            // the grid. Hover-toolbar buttons handle their own clicks before they bubble here, so
            // Reconnect/Remove/Fullscreen keep working.
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

        private void AnimateToolbar(double target)
        {
            var anim = new System.Windows.Media.Animation.DoubleAnimation(target, TimeSpan.FromMilliseconds(120));
            HoverToolbar.BeginAnimation(OpacityProperty, anim);
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
                _mediaPlayer.Media = media;

                _mediaPlayer.Playing += (_, _) => Dispatcher.Invoke(() => SetStatus(CameraStatus.Live, "live"));
                _mediaPlayer.EncounteredError += (_, _) => Dispatcher.Invoke(HandlePlaybackFailure);
                _mediaPlayer.EndReached += (_, _) => Dispatcher.Invoke(HandlePlaybackFailure);

                _mediaPlayer.Play();
            }
            catch (Exception ex)
            {
                SetStatus(CameraStatus.Error, "error");
                System.Diagnostics.Debug.WriteLine($"Camera '{Camera.Name}' failed to start: {ex.Message}");
                ScheduleReconnect();
            }
        }

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
        public void StopPlayback()
        {
            if (!IsRunning) return;
            IsRunning = false;
            _reconnectTimer.Stop();
            DisposeMediaPlayer();
            SetStatus(CameraStatus.Stopped, "stopped");
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
            PlaceholderPanel.Visibility = status == CameraStatus.Live ? Visibility.Collapsed : Visibility.Visible;
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
            if (_mediaPlayer == null) return;
            try
            {
                _mediaPlayer.Stop();
                Video.MediaPlayer = null;
                _mediaPlayer.Dispose();
            }
            catch { /* ignore teardown races */ }
            finally { _mediaPlayer = null; }
        }

        public void Dispose()
        {
            _disposed = true;
            IsRunning = false;
            _reconnectTimer.Stop();
            DisposeMediaPlayer();
        }
    }
}
