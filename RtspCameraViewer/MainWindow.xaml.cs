using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LibVLCSharp.Shared;
using RtspCameraViewer.Controls;
using RtspCameraViewer.Models;
using RtspCameraViewer.Services;
using RtspCameraViewer.Views;

namespace RtspCameraViewer
{
    public partial class MainWindow : Window
    {
        private readonly LibVLC _libVlc;
        private readonly List<Camera> _cameras;
        private readonly Dictionary<string, CameraTile> _tiles = new();
        private CameraTile? _fullscreenTile;

        private bool _isAppFullscreen;
        private WindowState _preFullscreenState;
        private WindowStyle _preFullscreenStyle;
        private ResizeMode _preFullscreenResizeMode;

        /// <summary>null = "All Stores" (no filter); otherwise only cameras with a matching Store show.</summary>
        private string? _selectedStore;
        private bool _suppressStoreSelectionChanged;

        public MainWindow()
        {
            InitializeComponent();

            Core.Initialize();
            _libVlc = new LibVLC(enableDebugLogs: false);

            _cameras = CameraStore.Load();
            foreach (var camera in _cameras)
                CreateTile(camera);

            RefreshLayout();

            StateChanged += (_, _) => UpdateRestoreIcon();
            UpdateRestoreIcon();
        }

        private void UpdateRestoreIcon()
        {
            // Segoe MDL2 Assets glyphs:  = restore (overlapping squares),  = maximize (single square)
            RestoreButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
            RestoreButton.ToolTip = WindowState == WindowState.Maximized ? "Restore" : "Maximize";
        }

        private CameraTile CreateTile(Camera camera)
        {
            var tile = new CameraTile(camera, _libVlc);
            tile.FullscreenRequested += Tile_FullscreenRequested;
            tile.RemoveRequested += Tile_RemoveRequested;
            _tiles[camera.Id] = tile;
            return tile;
        }

        private void RefreshLayout()
        {
            RefreshStoreBar();

            // Grouped storewise (unassigned cameras last) even in "All Stores", so the grid
            // always reads store-by-store; filtered further to one store when selected.
            var visible = (_selectedStore == null
                    ? _cameras.AsEnumerable()
                    : _cameras.Where(c => string.Equals(StoreOf(c), _selectedStore, System.StringComparison.OrdinalIgnoreCase)))
                .OrderBy(c => StoreOf(c) == null ? 1 : 0)
                .ThenBy(c => StoreOf(c), System.StringComparer.OrdinalIgnoreCase)
                .ThenBy(c => c.Name, System.StringComparer.OrdinalIgnoreCase)
                .ToList();

            // A tile being shown in the expand overlay is the logical child of that
            // ContentControl. Adding it to the grid as well throws "Specified element is already
            // the logical child of another element", which crashed the app outright whenever the
            // layout was rebuilt (any store switch) while a tile was expanded. If the expanded
            // camera is no longer visible under the current filter, close the overlay; otherwise
            // leave it there and skip it below. Unwound inline rather than calling
            // ExitFullscreen, which would recurse back into RefreshLayout.
            if (_fullscreenTile != null && visible.All(c => c.Id != _fullscreenTile.Camera.Id))
            {
                _fullscreenTile.StopPlayback();
                FullscreenContent.Content = null;
                FullscreenHost.Visibility = Visibility.Collapsed;
                _fullscreenTile = null;
            }

            GridHost.Children.Clear();
            foreach (var camera in visible)
            {
                if (!_tiles.TryGetValue(camera.Id, out var tile)) continue;
                if (ReferenceEquals(tile, _fullscreenTile)) continue; // it lives in the overlay
                GridHost.Children.Add(tile);
            }

            // Only the tiles actually on screen may stream. Previously a filtered-out tile was
            // merely removed from the visual tree while its MediaPlayer kept decoding, so
            // selecting one store still ran every camera in the list — saturating bandwidth and
            // the hardware decoder, which showed up as torn and smeared frames on the visible
            // feeds. Stopping the hidden ones frees that capacity for the store being watched.
            // Cap how many stream at once. Measured on this machine: 26 concurrent streams
            // produced VLC "buffer deadlock prevented" errors at ~0.9/s and left roughly half of
            // the connected streams without a decoder at all (which is what "RTSP not opening"
            // and the torn frames actually were), while 6 concurrent streams dropped that to
            // ~0.2/s. The limit is the number of simultaneous streams, not the decode settings —
            // tuning those (software decode, skipping the loop filter, single-threaded decode)
            // measurably did NOT help, so the honest fix is to run fewer at a time.
            var streaming = visible.Take(MaxConcurrentStreams).ToList();
            var streamingIds = new HashSet<string>(streaming.Select(c => c.Id));

            // Stop first, and immediately: this frees sockets and decoders before the new set
            // asks for them. Teardown no longer blocks the UI thread (see CameraTile).
            foreach (var (id, tile) in _tiles)
            {
                if (streamingIds.Contains(id) || !tile.IsRunning) continue;
                tile.StopPlayback(visibleIdsContains(id) ? "paused — over limit" : "stopped");
            }

            bool visibleIdsContains(string id) => visible.Any(c => c.Id == id);

            var toStart = streaming
                .Select(c => _tiles.TryGetValue(c.Id, out var t) ? t : null)
                .Where(t => t is { IsRunning: false })
                .Select(t => t!)
                .ToList();

            _ = StartStaggeredAsync(toStart);

            CameraCountText.Text = visible.Count == 1 ? "1 camera" : $"{visible.Count} cameras";
            if (_selectedStore != null) CameraCountText.Text += $"  ·  store {_selectedStore}";
            if (visible.Count > MaxConcurrentStreams)
                CameraCountText.Text += $"  ·  streaming {MaxConcurrentStreams} at a time — pick a store to see the rest";

            EmptyStateText.Visibility = visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyStateText.Text = _cameras.Count == 0
                ? "No cameras yet. Click \"+ Add Camera\" to add your first RTSP stream."
                : "No cameras in this store.";
            GridHost.Visibility = visible.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>
        /// Brings streams up one at a time rather than all at once. Opening a whole grid
        /// simultaneously means every stream negotiates RTSP and spins up a decoder in the same
        /// instant, which starves them all — connections established but no decoder, and VLC
        /// logging "buffer deadlock prevented". Spacing the starts lets each settle.
        /// </summary>
        private async Task StartStaggeredAsync(List<CameraTile> tiles)
        {
            const int staggerMs = 180;

            // A newer layout pass supersedes this one, so an in-flight sequence for a store the
            // user has already switched away from stops instead of reopening its streams.
            int generation = ++_startGeneration;

            foreach (var tile in tiles)
            {
                if (generation != _startGeneration) return;
                if (!tile.IsRunning) tile.StartPlayback();
                await Task.Delay(staggerMs);
            }
        }

        private int _startGeneration;

        /// <summary>
        /// How many cameras may stream simultaneously. Raising this degrades every stream rather
        /// than showing more of them: past roughly a dozen, decoders start losing picture buffers
        /// and frames tear. Cameras beyond the cap stay listed but paused.
        /// </summary>
        private const int MaxConcurrentStreams = 12;

        private static string? StoreOf(Camera c) => string.IsNullOrWhiteSpace(c.Store) ? null : c.Store;

        /// <summary>
        /// Rebuilds the "SELECT A STORE" dropdown from whatever store codes are currently
        /// present. Hidden entirely until at least one camera has a store code (e.g. after an
        /// Excel import).
        /// </summary>
        private void RefreshStoreBar()
        {
            var stores = _cameras.Select(StoreOf).Where(s => s != null).Select(s => s!)
                .Distinct(System.StringComparer.OrdinalIgnoreCase)
                .OrderBy(s => s, System.StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (stores.Count == 0)
            {
                StoreBar.Visibility = Visibility.Collapsed;
                _selectedStore = null;
                return;
            }

            if (_selectedStore != null && !stores.Contains(_selectedStore, System.StringComparer.OrdinalIgnoreCase))
                _selectedStore = null; // the store that was selected no longer has any cameras

            StoreBar.Visibility = Visibility.Visible;

            _suppressStoreSelectionChanged = true;
            StoreSelector.Items.Clear();
            StoreSelector.Items.Add(new ComboBoxItem { Content = "All Stores", Tag = null });
            foreach (var store in stores)
                StoreSelector.Items.Add(new ComboBoxItem { Content = store, Tag = store });

            StoreSelector.SelectedIndex = 0;
            foreach (ComboBoxItem item in StoreSelector.Items)
            {
                if (Equals(item.Tag, _selectedStore)) { StoreSelector.SelectedItem = item; break; }
            }
            _suppressStoreSelectionChanged = false;
        }

        private void StoreSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressStoreSelectionChanged) return;
            _selectedStore = (StoreSelector.SelectedItem as ComboBoxItem)?.Tag as string;
            RefreshLayout();
        }

        private void AddCamera_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new CameraEditDialog { Owner = this };
            if (dialog.ShowDialog() == true)
            {
                var camera = dialog.Result;
                _cameras.Add(camera);
                CameraStore.Save(_cameras);
                CreateTile(camera);
                RefreshLayout();
            }
        }

        private void ImportExcel_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new ImportCamerasDialog(_cameras) { Owner = this };
            if (dialog.ShowDialog() != true) return;

            foreach (var camera in dialog.NewCameras)
            {
                _cameras.Add(camera);
                CreateTile(camera);
            }

            CameraStore.Save(_cameras);
            RefreshLayout();

            var addedText = dialog.NewCameras.Count == 1 ? "1 camera" : $"{dialog.NewCameras.Count} cameras";
            MessageBox.Show(this,
                $"Imported: {addedText} added, {dialog.UpdatedCount} updated, {dialog.SkippedCount} skipped (no IP).",
                "Import Cameras", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void Tile_RemoveRequested(object sender, RoutedEventArgs e)
        {
            if (sender is not CameraTile tile) return;

            var confirm = MessageBox.Show(this,
                $"Remove camera \"{tile.Camera.Name}\"?", "Remove Camera",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            if (_fullscreenTile == tile)
                ExitFullscreen();

            tile.Dispose();
            _tiles.Remove(tile.Camera.Id);
            _cameras.RemoveAll(c => c.Id == tile.Camera.Id);
            CameraStore.Save(_cameras);
            RefreshLayout();
        }

        private void Tile_FullscreenRequested(object sender, RoutedEventArgs e)
        {
            if (sender is not CameraTile tile) return;

            // Stop the stream BEFORE reparenting. Moving the tile hands its VideoView a new
            // parent window, and doing that while a Direct3D video output is still attached to
            // the old one kills the process natively — no managed exception, no event log entry,
            // just a silent exit. Restarting after the move costs a short reconnect and is the
            // difference between a working expand and a coin flip.
            tile.StopPlayback("expanding…");

            GridHost.Children.Remove(tile);
            FullscreenContent.Content = tile;
            FullscreenHost.Visibility = Visibility.Visible;
            _fullscreenTile = tile;

            tile.StartPlayback();
        }

        private void ExitFullscreen_Click(object sender, RoutedEventArgs e) => ExitFullscreen();

        private void ExitFullscreen()
        {
            if (_fullscreenTile == null) return;

            // Same reasoning as the expand path: never reparent a tile that is still streaming.
            _fullscreenTile.StopPlayback("collapsing…");

            FullscreenContent.Content = null;
            FullscreenHost.Visibility = Visibility.Collapsed;
            _fullscreenTile = null;
            RefreshLayout(); // puts the tile back in the grid and restarts it there
        }

        private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void MaximizeRestore_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }

        private void CloseWindow_Click(object sender, RoutedEventArgs e) => Close();

        private void AppFullscreen_Click(object sender, RoutedEventArgs e) => ToggleAppFullscreen();

        private void ToggleAppFullscreen()
        {
            if (!_isAppFullscreen)
            {
                _preFullscreenState = WindowState;
                _preFullscreenStyle = WindowStyle;
                _preFullscreenResizeMode = ResizeMode;

                WindowStyle = WindowStyle.None;
                ResizeMode = ResizeMode.NoResize;
                WindowState = WindowState.Maximized;
                _isAppFullscreen = true;
                AppFullscreenButton.ToolTip = "Exit full screen (Esc / F11)";
            }
            else
            {
                WindowState = _preFullscreenState;
                WindowStyle = _preFullscreenStyle;
                ResizeMode = _preFullscreenResizeMode;
                _isAppFullscreen = false;
                AppFullscreenButton.ToolTip = "Full screen (F11)";
            }
        }

        private void MainWindow_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                if (FullscreenHost.Visibility == Visibility.Visible)
                    ExitFullscreen();
                else if (_isAppFullscreen)
                    ToggleAppFullscreen();
            }
            else if (e.Key == Key.F11)
            {
                ToggleAppFullscreen();
            }
        }

        protected override void OnClosed(System.EventArgs e)
        {
            foreach (var tile in _tiles.Values)
                tile.Dispose();
            _libVlc.Dispose();
            base.OnClosed(e);
        }
    }
}
