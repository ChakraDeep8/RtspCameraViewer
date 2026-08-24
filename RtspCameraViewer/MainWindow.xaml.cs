using System.Collections.Generic;
using System.Linq;
using System.Windows;
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
            GridHost.Children.Clear();
            foreach (var camera in _cameras)
            {
                if (_tiles.TryGetValue(camera.Id, out var tile))
                    GridHost.Children.Add(tile);
            }

            CameraCountText.Text = _cameras.Count == 1 ? "1 camera" : $"{_cameras.Count} cameras";
            EmptyStateText.Visibility = _cameras.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            GridHost.Visibility = _cameras.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
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

            GridHost.Children.Remove(tile);
            FullscreenContent.Content = tile;
            FullscreenHost.Visibility = Visibility.Visible;
            _fullscreenTile = tile;
        }

        private void ExitFullscreen_Click(object sender, RoutedEventArgs e) => ExitFullscreen();

        private void ExitFullscreen()
        {
            if (_fullscreenTile == null) return;

            FullscreenContent.Content = null;
            FullscreenHost.Visibility = Visibility.Collapsed;
            _fullscreenTile = null;
            RefreshLayout();
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
