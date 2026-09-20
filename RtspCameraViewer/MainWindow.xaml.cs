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
        private Rect _preFullscreenBounds = Rect.Empty;

        /// <summary>null = "All Stores" (no filter); otherwise only cameras with a matching Store show.</summary>
        private string? _selectedStore;
        private bool _suppressStoreSelectionChanged;

        /// <summary>Window count and slot assignments, per view. See <see cref="ViewLayout"/>.</summary>
        private readonly Dictionary<string, ViewLayout> _layouts;

        /// <summary>Cameras the adjustment flyout is currently editing; empty when it is closed.</summary>
        private List<Camera> _adjustTargets = new();
        private bool _suppressAdjustChanged;

        public MainWindow()
        {
            // Before InitializeComponent, so the accent is in place when the first styles resolve.
            FluentWindow.ApplyAccent(Application.Current.Resources);
            InitializeComponent();
            FluentWindow.Attach(this);

            Core.Initialize();
            _libVlc = new LibVLC(enableDebugLogs: false);

            _cameras = CameraStore.Load();
            _layouts = ViewLayoutStore.Load();
            BuildAdjustPanel();
            // Tiles are created by RefreshLayout, for the cameras actually on screen. Creating
            // all of them up front left tiles alive outside the visual tree, which is exactly
            // what leaks video windows (see the pruning in RefreshLayout).
            RefreshLayout();

            StateChanged += (_, _) => UpdateMaximizedInset();

            // Open full screen. This is a wall-display app — it is normally left running on a
            // screen someone glances at, where the taskbar and title bar are only there to be
            // covered by something else. Esc and F11 still leave, and leaving restores the
            // centred 1200x800 window captured below, so the escape route is unchanged.
            //
            // Deferred to Loaded rather than run here: sizing to the monitor needs the window's
            // handle to know which monitor it is on, and that does not exist until it is shown.
            Loaded += (_, _) =>
            {
                if (!_isAppFullscreen) ToggleAppFullscreen();
            };
        }

        /// <summary>
        /// A maximized window with custom chrome is sized past the screen edge by its (invisible)
        /// frame, so without an inset the top of the title bar - and the caption buttons - sit
        /// off-screen. True full screen has no frame, so no inset there.
        /// </summary>
        private void UpdateMaximizedInset()
        {
            if (WindowState == WindowState.Maximized && !_isAppFullscreen)
            {
                var frame = SystemParameters.WindowResizeBorderThickness;
                const double paddedBorder = 4;
                RootBorder.Margin = new Thickness(frame.Left + paddedBorder, frame.Top + paddedBorder,
                                                  frame.Right + paddedBorder, frame.Bottom + paddedBorder);
            }
            else
            {
                RootBorder.Margin = new Thickness(0);
            }
        }


        private CameraTile CreateTile(Camera camera)
        {
            var tile = new CameraTile(camera, _libVlc);
            tile.FullscreenRequested += Tile_FullscreenRequested;
            tile.RemoveRequested += Tile_RemoveRequested;
            tile.MoveRequested += Tile_MoveRequested;
            tile.SourceChangeRequested += Tile_SourceChangeRequested;
            // The first stream to report its real dimensions re-shapes the grid around them.
            tile.VideoAspectKnown += UpdateGridShape;
            _tiles[camera.Id] = tile;
            return tile;
        }

        private void RefreshLayout()
        {
            RefreshStoreBar();

            // The user's own order (set with the move arrows), filtered to one class when selected.
            // Cameras that have never been placed are slotted in class-by-class, so a fresh list
            // still reads store-by-store until the user rearranges it.
            EnsureOrder();
            var inView = CamerasInView();

            // What the grid actually shows: one camera per window, the count and the assignment
            // both the user's (see ViewLayout). Everything else in the class stays listed in each
            // window's source picker rather than being forced on screen — which is what lets a
            // 49-camera list be watched on a 12-window board without paging through it.
            var visible = inView;

            // Expanding is just "show one camera": the tile stays in the grid and the grid
            // narrows to it. Reparenting it into an overlay instead was the wrong shape - the
            // overlay could not hide the other tiles, because a VideoView's hosted window is
            // native and survives its WPF parent being collapsed, so seven other feeds carried
            // on painting around the expanded one. Filtering here means the code below stops
            // them and takes them out of the tree for real.
            if (_fullscreenTile != null && visible.All(c => c.Id != _fullscreenTile.Camera.Id))
                SetExpanded(null); // the expanded camera is not in this class - drop back to the grid

            visible = _fullscreenTile != null
                ? visible.Where(c => c.Id == _fullscreenTile.Camera.Id).ToList()
                : ResolveSlots(inView);

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

            var desired = visible.Select(c => _tiles.TryGetValue(c.Id, out var t) ? t : CreateTile(c)).ToList();

            // Leave the tree alone when it already matches. Rebuilding takes every tile out and puts
            // it back, and a VideoView re-parented while its video output is attached is exactly
            // what killed the process natively before. A move rearranges the two tiles it touches
            // itself, stopped first, and this pass must not then shuffle all the others.
            if (!GridHost.Children.Cast<UIElement>().SequenceEqual(desired))
            {
                GridHost.Children.Clear();
                foreach (var tile in desired) GridHost.Children.Add(tile);
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
            // Every window on screen streams. That is the point of the window count replacing the
            // old fixed cap: the number of open streams is now something the user chose and can
            // see, rather than a hidden limit that quietly paused the tiles past the twelfth.
            var streaming = visible;
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

            // Each window offers the whole view in its picker, so any camera in the class can be
            // brought into any window without changing the count.
            foreach (var tile in GridHost.Children.OfType<CameraTile>())
                tile.SetSourceOptions(inView);

            UpdateViewBar(inView.Count, visible.Count);

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
        /// Above this many simultaneous streams the picture starts to suffer — measured here,
        /// decoders begin losing buffers and frames tear. It is no longer a hard cap (the window
        /// count is the user's, up to <see cref="ViewLayout.MaxWindows"/>), but crossing it is
        /// worth saying out loud, so the count bar does.
        /// </summary>
        private const int ComfortableStreams = 12;

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
            UpdateMoveAvailability();
        }

        /// <summary>
        /// Gives every camera a place in the grid order. Existing positions are kept; cameras
        /// without one (new, imported, or everything on the first run) are appended in
        /// class-then-name order so they land grouped rather than scattered.
        /// </summary>
        private void EnsureOrder()
        {
            if (_cameras.All(c => c.Order.HasValue)) return;

            int next = _cameras.Where(c => c.Order.HasValue).Select(c => c.Order!.Value).DefaultIfEmpty(-1).Max() + 1;
            foreach (var camera in _cameras
                         .Where(c => !c.Order.HasValue)
                         .OrderBy(c => StoreOf(c) == null ? 1 : 0)
                         .ThenBy(c => StoreOf(c), System.StringComparer.OrdinalIgnoreCase)
                         .ThenBy(c => c.Name, System.StringComparer.OrdinalIgnoreCase)
                         .ToList())
            {
                camera.Order = next++;
            }
            CameraStore.Save(_cameras);
        }

        /// <summary>Tells each tile which arrows lead somewhere from its current cell.</summary>
        private void UpdateMoveAvailability()
        {
            var tiles = GridHost.Children.OfType<CameraTile>().ToList();
            int columns = Math.Max(1, GridHost.Columns);
            // Nothing to rearrange in the expanded single-camera view.
            bool grid = _fullscreenTile == null && tiles.Count > 1;

            for (int i = 0; i < tiles.Count; i++)
            {
                tiles[i].SetMoveAvailability(
                    up: grid && i - columns >= 0,
                    down: grid && i + columns < tiles.Count,
                    left: grid && i > 0,
                    right: grid && i < tiles.Count - 1);
            }
        }

        /// <summary>
        /// Swaps a camera with its neighbour in the given direction. Left and right step through
        /// reading order (wrapping across row ends); up and down swap with the cell a whole row
        /// away. What is swapped is the stored position, so it survives restarts and holds in
        /// every view that shows both cameras.
        /// </summary>
        private void Tile_MoveRequested(CameraTile tile, MoveDirection direction)
        {
            var tiles = GridHost.Children.OfType<CameraTile>().ToList();
            int index = tiles.IndexOf(tile);
            if (index < 0) return;

            int columns = Math.Max(1, GridHost.Columns);
            int target = direction switch
            {
                MoveDirection.Up => index - columns,
                MoveDirection.Down => index + columns,
                MoveDirection.Left => index - 1,
                _ => index + 1
            };
            if (target < 0 || target >= tiles.Count) return;

            var other = tiles[target];
            (tile.Camera.Order, other.Camera.Order) = (other.Camera.Order, tile.Camera.Order);

            // Stop both BEFORE touching the tree: moving a VideoView while its video output is
            // attached kills the process natively. RefreshLayout restarts whichever of the two
            // are still inside the streaming limit at their new positions.
            tile.StopPlayback("moving…");
            other.StopPlayback("moving…");

            int lo = Math.Min(index, target), hi = Math.Max(index, target);
            var first = tiles[lo];
            var second = tiles[hi];
            GridHost.Children.RemoveAt(hi);
            GridHost.Children.RemoveAt(lo);
            GridHost.Children.Insert(lo, second);
            GridHost.Children.Insert(hi, first);

            CameraStore.Save(_cameras);
            RefreshLayout();
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
                // The bar itself stays: it also carries the window stepper and the filter button,
                // which are useful with or without classes.
                ClassPickerPanel.Visibility = Visibility.Collapsed;
                _selectedStore = null;
                return;
            }

            if (_selectedStore != null && !stores.Contains(_selectedStore, System.StringComparer.OrdinalIgnoreCase))
                _selectedStore = null; // the store that was selected no longer has any cameras

            ClassPickerPanel.Visibility = Visibility.Visible;

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
            AdjustPopup.IsOpen = false; // its scope was the class being left
            RefreshLayout();
        }

        // ===================== Windows: how many, and showing what =====================

        /// <summary>Every camera the current view covers, in the user's order — not just the ones on screen.</summary>
        private List<Camera> CamerasInView() =>
            (_selectedStore == null
                    ? _cameras.AsEnumerable()
                    : _cameras.Where(c => string.Equals(StoreOf(c), _selectedStore, System.StringComparison.OrdinalIgnoreCase)))
                .OrderBy(c => c.Order ?? int.MaxValue)
                .ThenBy(c => c.Name, System.StringComparer.OrdinalIgnoreCase)
                .ToList();

        /// <summary>The window count and slot assignment belonging to the view on screen.</summary>
        private ViewLayout CurrentLayout
        {
            get
            {
                var key = ViewLayoutStore.KeyFor(_selectedStore);
                if (!_layouts.TryGetValue(key, out var layout))
                    _layouts[key] = layout = new ViewLayout();
                return layout;
            }
        }

        private void SaveLayouts() => ViewLayoutStore.Save(_layouts);

        /// <summary>
        /// Works out which camera belongs in each window.
        ///
        /// Stored slots win, so a board the user arranged stays arranged. A slot whose camera has
        /// been deleted, or filtered out of this view, is refilled from the remaining cameras in
        /// order rather than left as a hole — an empty window is a worse answer than a different
        /// camera. The resolved assignment is written back, so what the user sees is exactly what
        /// is saved and there is no second, invisible truth.
        /// </summary>
        private List<Camera> ResolveSlots(List<Camera> inView)
        {
            var layout = CurrentLayout;
            int windows = Math.Min(ViewLayout.Clamp(layout.WindowCount), inView.Count);

            var byId = inView.ToDictionary(c => c.Id);
            var taken = new HashSet<string>();
            var slots = new Camera?[windows];

            for (int i = 0; i < windows; i++)
            {
                var id = i < layout.Slots.Count ? layout.Slots[i] : null;
                if (id != null && byId.TryGetValue(id, out var camera) && taken.Add(id))
                    slots[i] = camera;
            }

            // Fill whatever the stored assignment did not cover, in view order.
            var spare = new Queue<Camera>(inView.Where(c => !taken.Contains(c.Id)));
            for (int i = 0; i < windows; i++)
            {
                if (slots[i] != null) continue;
                if (spare.Count == 0) break;
                slots[i] = spare.Dequeue();
            }

            var resolved = slots.Where(c => c != null).Select(c => c!).ToList();

            var assignment = resolved.Select(c => (string?)c.Id).ToList();
            if (!assignment.SequenceEqual(layout.Slots))
            {
                layout.Slots = assignment;
                SaveLayouts();
            }
            return resolved;
        }

        /// <summary>Refreshes the window stepper, the count line and the filter button's state.</summary>
        private void UpdateViewBar(int inViewCount, int shownCount)
        {
            var layout = CurrentLayout;

            // Pull a stored count that outgrew its view back down to what the view can fill. Without
            // this the stepper reads "12" over a grid of five, and pressing − appears to do nothing
            // for seven presses while it walks an invisible number back into range.
            if (_fullscreenTile == null && inViewCount > 0 && layout.WindowCount > inViewCount)
            {
                layout.WindowCount = inViewCount;
                SaveLayouts();
            }

            int requested = ViewLayout.Clamp(layout.WindowCount);

            WindowCountText.Text = (_fullscreenTile != null ? 1 : requested).ToString();
            FewerWindowsButton.IsEnabled = requested > ViewLayout.MinWindows && _fullscreenTile == null;
            MoreWindowsButton.IsEnabled = requested < ViewLayout.MaxWindows && _fullscreenTile == null
                                          && requested < inViewCount;
            WindowCountText.Opacity = _fullscreenTile == null ? 1 : 0.4;

            CameraCountText.Text = inViewCount == 1 ? "1 camera" : $"{inViewCount} cameras";
            if (_selectedStore != null) CameraCountText.Text += $"  ·  {_selectedStore}";

            if (_fullscreenTile != null)
            {
                CameraCountText.Text += "  ·  expanded";
            }
            else
            {
                if (shownCount < inViewCount)
                    CameraCountText.Text += $"  ·  showing {shownCount} — pick a camera in any window to swap it in";
                if (shownCount > ComfortableStreams)
                    CameraCountText.Text += $"  ·  {shownCount} streams at once may tear";
            }

            UpdateFilterAffordance();
        }

        private void FewerWindows_Click(object sender, RoutedEventArgs e) => ChangeWindowCount(-1);

        private void MoreWindows_Click(object sender, RoutedEventArgs e) => ChangeWindowCount(+1);

        /// <summary>
        /// Adds or removes a window. Growing past what the view holds is refused rather than
        /// producing blank cells, and the expanded view has one window by definition.
        /// </summary>
        private void ChangeWindowCount(int delta)
        {
            if (_fullscreenTile != null) return;

            var layout = CurrentLayout;
            int inView = CamerasInView().Count;
            int next = ViewLayout.Clamp(layout.WindowCount + delta);
            if (delta > 0 && next > inView) next = Math.Max(ViewLayout.MinWindows, inView);
            if (next == layout.WindowCount) return;

            layout.WindowCount = next;
            SaveLayouts();
            RefreshLayout();
        }

        /// <summary>
        /// Puts a different camera in one window. If that camera already occupies another window
        /// the two trade places, so choosing it never opens the same stream twice — two decoders
        /// on one URL costs double and shows nothing new.
        /// </summary>
        private void Tile_SourceChangeRequested(CameraTile tile, Camera chosen)
        {
            var tiles = GridHost.Children.OfType<CameraTile>().ToList();
            int index = tiles.IndexOf(tile);
            if (index < 0) return;

            int existing = tiles.FindIndex(t => t.Camera.Id == chosen.Id);

            // Stopped before the tree is touched, for the same reason a move stops its two tiles:
            // a VideoView re-parented while its output is attached takes the process down.
            tile.StopPlayback("switching…");
            if (existing >= 0) tiles[existing].StopPlayback("switching…");

            var layout = CurrentLayout;
            while (layout.Slots.Count < tiles.Count) layout.Slots.Add(null);

            if (existing >= 0) layout.Slots[existing] = tile.Camera.Id;
            layout.Slots[index] = chosen.Id;

            SaveLayouts();
            RefreshLayout();
        }

        // ============================ Picture adjustment ============================

        private readonly Dictionary<VideoAdjustments.Knob, Slider> _knobSliders = new();
        private readonly Dictionary<VideoAdjustments.Knob, TextBlock> _knobReadouts = new();

        /// <summary>
        /// Builds one labelled slider per knob, straight from the range table, so the UI cannot
        /// offer a value LibVLC would clamp away.
        /// </summary>
        private void BuildAdjustPanel()
        {
            foreach (var knob in VideoAdjustments.AllKnobs)
            {
                var range = VideoAdjustments.RangeOf(knob);

                var header = new Grid { Margin = new Thickness(0, 6, 0, 0) };
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var icon = new TextBlock
                {
                    Text = range.Icon,
                    FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                    FontSize = 12,
                    Foreground = (System.Windows.Media.Brush)FindResource("TextSecondary"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0)
                };
                Grid.SetColumn(icon, 0);
                header.Children.Add(icon);

                var label = new TextBlock
                {
                    Text = range.Label,
                    FontSize = 12,
                    Foreground = (System.Windows.Media.Brush)FindResource("TextPrimary"),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(label, 1);
                header.Children.Add(label);

                var readout = new TextBlock
                {
                    Text = VideoAdjustments.Format(knob, range.Neutral),
                    FontSize = 11,
                    Foreground = (System.Windows.Media.Brush)FindResource("TextSecondary"),
                    VerticalAlignment = VerticalAlignment.Center,
                    MinWidth = 42,
                    TextAlignment = TextAlignment.Right
                };
                Grid.SetColumn(readout, 2);
                header.Children.Add(readout);

                var slider = new Slider
                {
                    Minimum = range.Min,
                    Maximum = range.Max,
                    Value = range.Neutral,
                    Style = (Style)FindResource("FluentSlider"),
                    // A step fine enough to dial in, coarse enough that the keyboard is usable.
                    SmallChange = knob == VideoAdjustments.Knob.Hue ? 5 : 0.05,
                    LargeChange = knob == VideoAdjustments.Knob.Hue ? 30 : 0.25,
                    Tag = knob
                };
                slider.ValueChanged += Knob_ValueChanged;

                // Double-click a slider to put just that one back to neutral.
                slider.MouseDoubleClick += (s, _) =>
                {
                    if (s is Slider sl) sl.Value = VideoAdjustments.RangeOf((VideoAdjustments.Knob)sl.Tag!).Neutral;
                };

                _knobSliders[knob] = slider;
                _knobReadouts[knob] = readout;

                AdjustKnobPanel.Children.Add(header);
                AdjustKnobPanel.Children.Add(slider);
            }
        }

        /// <summary>
        /// Which cameras the flyout edits: the one camera in the expanded view, otherwise every
        /// camera currently in a window. Empty in All Classes, which is what disables the button.
        /// </summary>
        private List<Camera> AdjustTargets()
        {
            if (_fullscreenTile != null) return new List<Camera> { _fullscreenTile.Camera };
            if (_selectedStore == null) return new List<Camera>();
            return GridHost.Children.OfType<CameraTile>().Select(t => t.Camera).ToList();
        }

        /// <summary>
        /// Enables the Filter button only where a filter has a well-defined subject, and flags
        /// when an adjustment is in force so a picture that looks wrong is traceable to a slider
        /// rather than blamed on the camera.
        /// </summary>
        private void UpdateFilterAffordance()
        {
            bool available = AdjustTargets().Count > 0;
            FilterButton.IsEnabled = available;
            FilterButton.ToolTip = available
                ? "Brightness, contrast, saturation, hue and gamma for the cameras on screen"
                : "Pick a camera class (or expand one camera) to adjust the picture";

            bool adjusted = AdjustTargets().Any(c => !c.Adjustments.IsNeutral);
            FilterActiveDot.Visibility = adjusted ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Filter_Click(object sender, RoutedEventArgs e)
        {
            var targets = AdjustTargets();
            if (targets.Count == 0) return;

            _adjustTargets = targets;

            AdjustScopeText.Text = targets.Count == 1
                ? targets[0].Name
                : $"{targets.Count} cameras on screen";
            AdjustScopeHint.Text = targets.Count == 1
                ? "Display only — the recording and what other clients see are untouched."
                : $"Applies to every window in {_selectedStore}. Display only — nothing is re-encoded.";

            LoadAdjustValues(targets[0].Adjustments);

            // Anchored to whichever button was pressed, and nudged left so a 340px panel opening
            // from a right-aligned button stays on screen.
            AdjustPopup.PlacementTarget = sender as UIElement ?? FilterButton;
            AdjustPopup.HorizontalOffset = -300;
            AdjustPopup.IsOpen = true;
        }

        /// <summary>Puts saved values into the sliders without those assignments looking like user edits.</summary>
        private void LoadAdjustValues(VideoAdjustments source)
        {
            _suppressAdjustChanged = true;
            foreach (var knob in VideoAdjustments.AllKnobs)
            {
                float value = source.Get(knob);
                _knobSliders[knob].Value = value;
                _knobReadouts[knob].Text = VideoAdjustments.Format(knob, value);
            }
            _suppressAdjustChanged = false;
        }

        /// <summary>
        /// A slider moved: write it to every target camera and push it into their running players.
        /// This is the whole point of using LibVLC's adjust filter — the change lands on the next
        /// decoded frame, so the feed responds as the slider moves and is never interrupted.
        /// </summary>
        private void Knob_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressAdjustChanged || sender is not Slider slider || slider.Tag is not VideoAdjustments.Knob knob)
                return;

            float value = (float)slider.Value;
            _knobReadouts[knob].Text = VideoAdjustments.Format(knob, value);

            foreach (var camera in _adjustTargets)
            {
                camera.Adjustments.Set(knob, value);
                if (_tiles.TryGetValue(camera.Id, out var tile)) tile.ApplyAdjustments();
            }

            FilterActiveDot.Visibility = _adjustTargets.Any(c => !c.Adjustments.IsNeutral)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void AdjustReset_Click(object sender, RoutedEventArgs e)
        {
            foreach (var camera in _adjustTargets)
            {
                camera.Adjustments.Reset();
                if (_tiles.TryGetValue(camera.Id, out var tile)) tile.ApplyAdjustments();
            }
            LoadAdjustValues(new VideoAdjustments());
            FilterActiveDot.Visibility = Visibility.Collapsed;
            CameraStore.Save(_cameras);
        }

        /// <summary>
        /// Closing the flyout is what commits the adjustment to disk. Saving on every slider tick
        /// would rewrite the whole camera list dozens of times per drag.
        /// </summary>
        private void AdjustDone_Click(object sender, RoutedEventArgs e) => AdjustPopup.IsOpen = false;

        private void AdjustPopup_Closed(object sender, System.EventArgs e)
        {
            if (_adjustTargets.Count == 0) return;
            CameraStore.Save(_cameras);
            _adjustTargets = new List<Camera>();
            UpdateFilterAffordance();
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

        private void AppFullscreen_Click(object sender, RoutedEventArgs e) => ToggleAppFullscreen();

        private void ToggleAppFullscreen()
        {
            if (!_isAppFullscreen)
            {
                _preFullscreenState = WindowState;
                _preFullscreenStyle = WindowStyle;
                _preFullscreenResizeMode = ResizeMode;
                _preFullscreenBounds = WindowState == WindowState.Normal
                    ? new Rect(Left, Top, Width, Height)
                    : RestoreBounds;
                _isAppFullscreen = true;

                // Sized to the monitor rather than maximized: with custom window chrome a
                // maximized window stops at the work area, so "full screen" kept the taskbar.
                var screen = FluentWindow.GetMonitorBounds(this);
                WindowStyle = WindowStyle.None;
                ResizeMode = ResizeMode.NoResize;
                WindowState = WindowState.Normal;
                Left = screen.Left;
                Top = screen.Top;
                Width = screen.Width;
                Height = screen.Height;

                AppFullscreenButton.ToolTip = "Exit full screen (Esc / F11)";
                UpdateMaximizedInset();
            }
            else
            {
                // Cleared first, so restoring to Maximized gets its frame inset back.
                _isAppFullscreen = false;
                WindowStyle = _preFullscreenStyle;
                ResizeMode = _preFullscreenResizeMode;
                if (!_preFullscreenBounds.IsEmpty)
                {
                    var bounds = FitToWorkArea(_preFullscreenBounds);
                    Left = bounds.Left;
                    Top = bounds.Top;
                    Width = bounds.Width;
                    Height = bounds.Height;
                }
                WindowState = _preFullscreenState;
                AppFullscreenButton.ToolTip = "Full screen (F11)";
                UpdateMaximizedInset();
            }
        }

        /// <summary>
        /// Shrinks and nudges a window rectangle until it sits inside the monitor's work area.
        ///
        /// The default size is 1200x800, which is TALLER than a 1366x768 screen, and centring a
        /// window taller than the screen puts its top edge — and therefore the title bar and the
        /// caption buttons — above the top of the display. That was survivable while it only
        /// affected the first launch; now that the app opens full screen, it is where every Esc
        /// and every F11 lands, so the restored window has to be placed somewhere reachable.
        /// </summary>
        private Rect FitToWorkArea(Rect bounds)
        {
            var work = FluentWindow.GetWorkAreaBounds(this);
            if (work.Width <= 0 || work.Height <= 0) return bounds;

            double width = Math.Min(bounds.Width, work.Width);
            double height = Math.Min(bounds.Height, work.Height);
            double left = Math.Clamp(bounds.Left, work.Left, Math.Max(work.Left, work.Right - width));
            double top = Math.Clamp(bounds.Top, work.Top, Math.Max(work.Top, work.Bottom - height));
            return new Rect(left, top, width, height);
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
            else if (Keyboard.Modifiers == ModifierKeys.Control &&
                     e.Key is Key.OemPlus or Key.Add)
            {
                ChangeWindowCount(+1);
            }
            else if (Keyboard.Modifiers == ModifierKeys.Control &&
                     e.Key is Key.OemMinus or Key.Subtract)
            {
                ChangeWindowCount(-1);
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
