using System;
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
            // Tiles are created by RefreshLayout, for the cameras actually on screen. Creating
            // all of them up front left tiles alive outside the visual tree, which is exactly
            // what leaks video windows (see the pruning in RefreshLayout).
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
            // The first stream to report its real dimensions re-shapes the grid around them.
            tile.VideoAspectKnown += UpdateGridShape;
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

            // Expanding is just "show one camera": the tile stays in the grid and the grid
            // narrows to it. Reparenting it into an overlay instead was the wrong shape - the
            // overlay could not hide the other tiles, because a VideoView's hosted window is
            // native and survives its WPF parent being collapsed, so seven other feeds carried
            // on painting around the expanded one. Filtering here means the code below stops
            // them and takes them out of the tree for real.
            if (_fullscreenTile != null && visible.All(c => c.Id != _fullscreenTile.Camera.Id))
                SetExpanded(null); // the expanded camera is not in this class - drop back to the grid

            if (_fullscreenTile != null)
                visible = visible.Where(c => c.Id == _fullscreenTile.Camera.Id).ToList();

            // Tear down the tiles that are not on screen, rather than merely un-parenting them.
            // A VideoView's video and overlay windows are NATIVE and outlive their WPF parent, so
            // a tile left alive outside the tree kept painting - filtering to one class, or
            // expanding one camera, left a full grid of ghost panels floating over the result.
            var visibleIds = new HashSet<string>(visible.Select(c => c.Id));
            foreach (var id in _tiles.Keys.Where(id => !visibleIds.Contains(id)).ToList())
            {
                _tiles[id].Dispose();
                _tiles.Remove(id);
            }

            GridHost.Children.Clear();
            foreach (var camera in visible)
            {
                if (!_tiles.TryGetValue(camera.Id, out var tile)) tile = CreateTile(camera);
                GridHost.Children.Add(tile);
            }

            // _fullscreenTile must point at a LIVE tile: if the expanded camera's tile was just
            // rebuilt, the old reference is disposed and would expand nothing.
            if (_fullscreenTile != null && _tiles.TryGetValue(_fullscreenTile.Camera.Id, out var current))
                _fullscreenTile = current;

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

            UpdateGridShape();

            CameraCountText.Text = visible.Count == 1 ? "1 camera" : $"{visible.Count} cameras";
            if (_selectedStore != null) CameraCountText.Text += $"  ·  {_selectedStore}";
            if (visible.Count > MaxConcurrentStreams)
                CameraCountText.Text += $"  ·  streaming {MaxConcurrentStreams} at a time — pick a class to see the rest";

            EmptyStateText.Visibility = visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyStateText.Text = _cameras.Count == 0
                ? "No cameras yet. Click \"+ Add Camera\" to add your first RTSP stream."
                : "No cameras in this class.";
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

        /// <summary>
        /// Fallback until a stream reports its real size. Only used before the first frame
        /// arrives — see ObservedAspect.
        /// </summary>
        private const double FallbackAspect = 16.0 / 9.0;

        /// <summary>
        /// The aspect the grid should shape itself around: whatever the streams actually report.
        /// Assuming 16:9 for cameras that are nearer 4:3 is what left every tile pillarboxed with
        /// black bars down both sides.
        /// </summary>
        private double ObservedAspect =>
            _tiles.Values.Select(t => t.VideoAspect).FirstOrDefault(a => a is > 0) ?? FallbackAspect;

        private void GridHost_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateGridShape();

        /// <summary>
        /// Picks the column count that gives the video the most actual pixels.
        ///
        /// Left to itself, UniformGrid lays out a roughly SQUARE arrangement no matter what shape
        /// the window is. In a wide, short grid area that makes every cell far wider than a 16:9
        /// frame, so each feed is pillarboxed into a narrow strip with black bars down both sides
        /// - the video ends up tiny while most of the tile is wasted. Trying every column count
        /// and keeping whichever maximises the letterboxed video area fixes that, and re-adapts
        /// whenever the window is resized.
        /// </summary>
        private void UpdateGridShape()
        {
            int count = GridHost.Children.Count;
            if (count == 0) return;

            double width = GridHost.ActualWidth, height = GridHost.ActualHeight;
            if (width <= 0 || height <= 0) return;

            double aspect = ObservedAspect;
            int bestColumns = 1;
            double bestArea = -1;

            for (int columns = 1; columns <= count; columns++)
            {
                int rows = (int)Math.Ceiling(count / (double)columns);
                double cellWidth = width / columns;
                double cellHeight = height / rows;

                // How large the video actually renders once fitted inside that cell.
                double videoWidth = Math.Min(cellWidth, cellHeight * aspect);
                double area = videoWidth * (videoWidth / aspect);

                if (area > bestArea)
                {
                    bestArea = area;
                    bestColumns = columns;
                }
            }

            GridHost.Columns = bestColumns;
            GridHost.Rows = (int)Math.Ceiling(count / (double)bestColumns);
        }

        /// <summary>Class name a camera belongs to, or null when it has not been given one.</summary>
        private static string? StoreOf(Camera c) => string.IsNullOrWhiteSpace(c.Store) ? null : c.Store;

        /// <summary>Class names already in use, offered when adding a camera so it can join an
        /// existing group instead of a near-duplicate being typed.</summary>
        private IEnumerable<string> ExistingClasses() =>
            _cameras.Select(StoreOf)
                    .Where(s => s != null)
                    .Select(s => s!)
                    .Distinct(System.StringComparer.OrdinalIgnoreCase)
                    .OrderBy(s => s, System.StringComparer.OrdinalIgnoreCase);

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
            StoreSelector.Items.Add(new ComboBoxItem { Content = "All Classes", Tag = null });
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
            var dialog = new CameraEditDialog(null, ExistingClasses()) { Owner = this };
            if (dialog.ShowDialog() == true)
            {
                _cameras.Add(dialog.Result);
                CameraStore.Save(_cameras);
                RefreshLayout();
                return;
            }

            if (dialog.UploadRequested)
                ImportFromFile(dialog.TypedClass);
        }

        /// <summary>
        /// Bulk-adds cameras read out of a file the user already has - a DVR's .env, an
        /// installer's list, a spreadsheet - all into one class.
        /// </summary>
        private void ImportFromFile(string? preselectedClass)
        {
            var dialog = new ImportCamerasDialog(_cameras, ExistingClasses(), preselectedClass) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.NewCameras.Count == 0) return;

            _cameras.AddRange(dialog.NewCameras);
            CameraStore.Save(_cameras);

            // Jump to the class the cameras just landed in, otherwise an import into a class
            // other than the one on screen looks like it did nothing.
            var landedIn = dialog.NewCameras[0].Store;
            if (!string.IsNullOrWhiteSpace(landedIn)) _selectedStore = landedIn;

            RefreshLayout();

            int count = dialog.NewCameras.Count;
            CameraCountText.Text += $"  ·  added {count} camera{(count == 1 ? "" : "s")}";
        }

        private void Settings_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SettingsDialog(_cameras, _selectedStore) { Owner = this };
            if (dialog.ShowDialog() != true || !dialog.Changed) return;

            CameraStore.Save(_cameras);

            // Streams have to come back up against the new URLs, and a renamed class has to be
            // re-selected under its new name or the filter would silently fall back to "all".
            foreach (var tile in _tiles.Values) tile.StopPlayback();
            RefreshLayout();
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
            SetExpanded(ReferenceEquals(tile, _fullscreenTile) ? null : tile);
            RefreshLayout();
        }

        /// <summary>Expands one camera to fill the grid, or returns to the full grid when null.</summary>
        private void SetExpanded(CameraTile? tile)
        {
            _fullscreenTile = tile;
            ExpandBar.Visibility = tile == null ? Visibility.Collapsed : Visibility.Visible;
            // The tile carries its own name in its header, so the bar says what STATE
            // this is and how to leave it rather than repeating the camera name.
            ExpandNameText.Text = "Expanded  ·  Esc or ✕ to go back to the grid";
        }

        private void ExitFullscreen_Click(object sender, RoutedEventArgs e) => ExitFullscreen();

        private void ExitFullscreen()
        {
            if (_fullscreenTile == null) return;
            SetExpanded(null);
            RefreshLayout(); // restores the whole grid and restarts the tiles in it
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
                if (_fullscreenTile != null)
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
