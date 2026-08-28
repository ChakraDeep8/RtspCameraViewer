using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Microsoft.Win32;
using RtspCameraViewer.Models;
using RtspCameraViewer.Services;

namespace RtspCameraViewer.Views
{
    /// <summary>
    /// Lets the user pick an Excel sheet of cameras (DEVICE NAME / LOCAL IP / TAILSCALE IP,
    /// same shape as the storewise device lists) and import/merge it into the existing camera
    /// list. Rows are matched to existing cameras by RTSP URL so re-importing an updated sheet
    /// just refreshes names/store codes instead of duplicating entries.
    /// </summary>
    public partial class ImportCamerasDialog : Window
    {
        private readonly List<Camera> _existingCameras;
        private List<ImportedCameraRow> _rows = new();

        /// <summary>Cameras to add, populated after a successful Import click.</summary>
        public List<Camera> NewCameras { get; } = new();
        public int UpdatedCount { get; private set; }
        public int SkippedCount { get; private set; }

        public ImportCamerasDialog(List<Camera> existingCameras)
        {
            InitializeComponent();
            _existingCameras = existingCameras;
        }

        private IpPreference Preference => PreferTailscaleRadio.IsChecked == true
            ? IpPreference.PreferTailscale
            : IpPreference.PreferLocal;

        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Filter = "Excel Workbook|*.xlsx" };
            if (dlg.ShowDialog() != true) return;

            FilePathBox.Text = dlg.FileName;
            try
            {
                _rows = CameraExcelImporter.Read(dlg.FileName);
                RefreshPreview();
            }
            catch (Exception ex)
            {
                ShowError($"Could not read this file.\n\n{ex.Message}");
                _rows = new List<ImportedCameraRow>();
                RefreshPreview();
            }
        }

        private void Preference_Changed(object sender, RoutedEventArgs e) => RefreshPreview();

        private void RefreshPreview()
        {
            if (SummaryText == null) return; // guards against the Checked event XAML fires during InitializeComponent, before this control exists
            PreviewList.Items.Clear();
            ErrorText.Visibility = Visibility.Collapsed;

            if (_rows.Count == 0)
            {
                SummaryText.Text = "Choose a file to preview.";
                ImportButton.IsEnabled = false;
                return;
            }

            var stores = _rows.Select(r => r.Store).Distinct().Count();
            var withUrl = _rows.Count(r => r.ResolveUrl(Preference) != null);
            SummaryText.Text = $"{_rows.Count} camera(s) across {stores} store(s) — {withUrl} with a usable URL, {_rows.Count - withUrl} skipped (no IP in either column).";

            foreach (var row in _rows)
            {
                var url = row.ResolveUrl(Preference);
                var line = url != null
                    ? $"[{row.Store}]  {row.DeviceName}  →  {url}"
                    : $"[{row.Store}]  {row.DeviceName}  →  (no IP — skipped)";
                if (!string.IsNullOrEmpty(row.Note)) line += $"   ({row.Note})";
                PreviewList.Items.Add(line);
            }

            ImportButton.IsEnabled = withUrl > 0;
        }

        private void Import_Click(object sender, RoutedEventArgs e)
        {
            NewCameras.Clear();
            UpdatedCount = 0;
            SkippedCount = 0;

            foreach (var row in _rows)
            {
                var url = row.ResolveUrl(Preference);
                if (url == null) { SkippedCount++; continue; }

                var existing = _existingCameras.FirstOrDefault(c =>
                    string.Equals(c.Url, url, StringComparison.OrdinalIgnoreCase));

                if (existing != null)
                {
                    existing.Name = row.DeviceName;
                    existing.Store = row.Store;
                    UpdatedCount++;
                }
                else
                {
                    NewCameras.Add(new Camera
                    {
                        Name = row.DeviceName,
                        Url = url,
                        Store = row.Store
                    });
                }
            }

            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void ShowError(string message)
        {
            ErrorText.Text = message;
            ErrorText.Visibility = Visibility.Visible;
        }
    }
}
