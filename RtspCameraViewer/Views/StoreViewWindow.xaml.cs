using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LibVLCSharp.Shared;
using RtspCameraViewer.Controls;
using RtspCameraViewer.Models;
using RtspCameraViewer.Services;

namespace RtspCameraViewer.Views
{
    /// <summary>
    /// Storewise "watch" window: pick a store on the left, its cameras stream live in a grid on
    /// the right. Opens fresh tiles for the selected store (independent of the main window's
    /// grid) and disposes them whenever the selection changes or the window closes.
    /// </summary>
    public partial class StoreViewWindow : Window
    {
        private readonly List<Camera> _cameras;
        private readonly LibVLC _libVlc;
        private readonly List<CameraTile> _activeTiles = new();
        private string? _selectedStore;

        public StoreViewWindow(List<Camera> cameras, LibVLC libVlc)
        {
            InitializeComponent();
            _cameras = cameras;
            _libVlc = libVlc;

            RefreshStoreList();
            Closed += (_, _) => DisposeActiveTiles();
        }

        private static string? StoreOf(Camera c) => string.IsNullOrWhiteSpace(c.Store) ? null : c.Store;

        private void RefreshStoreList()
        {
            var stores = _cameras
                .Select(StoreOf).Where(s => s != null).Select(s => s!)
                .Distinct(System.StringComparer.OrdinalIgnoreCase)
                .OrderBy(s => s, System.StringComparer.OrdinalIgnoreCase)
                .ToList();

            StoreListPanel.Children.Clear();

            if (stores.Count == 0)
            {
                StoreListPanel.Children.Add(new TextBlock
                {
                    Text = "No stores yet.\nUse \"Import Excel…\" first.",
                    Foreground = (Brush)FindResource("TextSecondary"),
                    FontSize = 11.5,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(6, 6, 6, 0)
                });
                return;
            }

            foreach (var store in stores)
            {
                var count = _cameras.Count(c => string.Equals(StoreOf(c), store, System.StringComparison.OrdinalIgnoreCase));
                StoreListPanel.Children.Add(MakeStoreRow(store, count));
            }
        }

        private Border MakeStoreRow(string store, int count)
        {
            bool selected = string.Equals(_selectedStore, store, System.StringComparison.OrdinalIgnoreCase);
            var row = new Border
            {
                Background = selected ? (Brush)FindResource("TileBackground") : Brushes.Transparent,
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 0, 0, 2),
                Cursor = Cursors.Hand,
                Child = new TextBlock
                {
                    Text = $"{store}  ({count})",
                    FontSize = 13,
                    Foreground = (Brush)FindResource(selected ? "TextPrimary" : "TextSecondary")
                }
            };
            row.MouseLeftButtonDown += (_, _) => SelectStore(store);
            return row;
        }

        private void SelectStore(string store)
        {
            _selectedStore = store;
            SelectedStoreText.Text = $"Store {store}";
            RefreshStoreList();

            DisposeActiveTiles();
            GridHost.Children.Clear();

            var cameras = _cameras
                .Where(c => string.Equals(StoreOf(c), store, System.StringComparison.OrdinalIgnoreCase))
                .OrderBy(c => c.Name, System.StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var camera in cameras)
            {
                var tile = new CameraTile(camera, _libVlc);
                _activeTiles.Add(tile);
                GridHost.Children.Add(tile);
            }

            EmptyStateText.Visibility = cameras.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyStateText.Text = cameras.Count == 0 ? "No cameras in this store." : "";
            GridHost.Visibility = cameras.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        private void DisposeActiveTiles()
        {
            foreach (var tile in _activeTiles) tile.Dispose();
            _activeTiles.Clear();
        }
    }
}
