using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using Microsoft.Win32;
using RtspCameraViewer.Models;
using RtspCameraViewer.Services;

namespace RtspCameraViewer.Views
{
    /// <summary>One discovered URL as shown in the preview, with the user's edits applied to it.</summary>
    public class ImportRow : INotifyPropertyChanged
    {
        private bool _include = true;
        private string _name = "";

        public bool Include
        {
            get => _include;
            set { _include = value; OnChanged(nameof(Include)); }
        }

        public string Name
        {
            get => _name;
            set { _name = value; OnChanged(nameof(Name)); }
        }

        public string Url { get; set; } = "";

        /// <summary>Why this row starts unticked, e.g. it is already in the camera list.</summary>
        public string Note { get; set; } = "";

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnChanged(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }

    /// <summary>
    /// Reads RTSP URLs out of a file the user already has — a DVR's .env, an installer's text or
    /// CSV list, a spreadsheet — and adds them as cameras, all into one class.
    ///
    /// Everything is previewed and editable before anything is added: the file can only ever be
    /// a guess at what the cameras are called, so the names it produces are a starting point
    /// rather than a result.
    /// </summary>
    public partial class ImportCamerasDialog : Window
    {
        private readonly List<Camera> _existingCameras;
        private readonly ObservableCollection<ImportRow> _rows = new();

        /// <summary>Cameras to add, populated after a successful Add click.</summary>
        public List<Camera> NewCameras { get; } = new();

        public ImportCamerasDialog(List<Camera> existingCameras, IEnumerable<string>? existingClasses = null, string? preselectedClass = null)
        {
            InitializeComponent();
            FluentWindow.Attach(this);
            _existingCameras = existingCameras;
            PreviewList.ItemsSource = _rows;

            if (existingClasses != null)
                foreach (var c in existingClasses)
                    ClassCombo.Items.Add(c);

            ClassCombo.Text = preselectedClass ?? "";
        }

        // =====================================================================
        // Picking a file
        // =====================================================================

        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Filter = RtspFileImporter.FileFilter, Title = "Choose a file containing RTSP URLs" };
            if (dlg.ShowDialog() != true) return;
            LoadFile(dlg.FileName);
        }

        private void Window_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
                LoadFile(files[0]);
        }

        private void LoadFile(string path)
        {
            FilePathBox.Text = path;
            HideError();
            _rows.Clear();

            List<DiscoveredCamera> found;
            try
            {
                found = RtspFileImporter.Read(path);
            }
            catch (Exception ex)
            {
                ShowError($"Could not read this file.\n\n{ex.Message}");
                UpdateSummary(0);
                return;
            }

            if (found.Count == 0)
            {
                ShowError($"No rtsp:// addresses found in {Path.GetFileName(path)}.");
                UpdateSummary(0);
                return;
            }

            // A URL already in the list is shown but unticked rather than hidden: silently
            // dropping it looks like the file was misread.
            var known = new HashSet<string>(_existingCameras.Select(c => c.Url), StringComparer.OrdinalIgnoreCase);

            foreach (var c in found)
            {
                bool duplicate = known.Contains(c.Url);
                _rows.Add(new ImportRow
                {
                    Name = c.Name,
                    Url = c.Url,
                    Include = !duplicate,
                    Note = duplicate ? "already added" : ""
                });
            }

            UpdateSummary(found.Count);
        }

        private void UpdateSummary(int found)
        {
            int dupes = _rows.Count(r => r.Note.Length > 0);
            SummaryText.Text = found == 0
                ? "Nothing found yet."
                : dupes > 0
                    ? $"Found {found} camera{(found == 1 ? "" : "s")} — {dupes} already in your list"
                    : $"Found {found} camera{(found == 1 ? "" : "s")}";
            ImportButton.IsEnabled = _rows.Count > 0;
        }

        private void SelectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var r in _rows) r.Include = true;
        }

        private void SelectNone_Click(object sender, RoutedEventArgs e)
        {
            foreach (var r in _rows) r.Include = false;
        }

        // =====================================================================
        // Committing
        // =====================================================================

        private void Import_Click(object sender, RoutedEventArgs e)
        {
            var chosen = _rows.Where(r => r.Include).ToList();
            if (chosen.Count == 0)
            {
                ShowError("Tick at least one camera to add.");
                return;
            }

            var className = ClassCombo.Text?.Trim();
            NewCameras.Clear();

            foreach (var row in chosen)
            {
                NewCameras.Add(new Camera
                {
                    Name = string.IsNullOrWhiteSpace(row.Name) ? "Camera" : row.Name.Trim(),
                    // Credentials embedded in the URL are left exactly as they were written:
                    // they are already percent-escaped in these files, and re-encoding them
                    // is how a working URL turns into one that silently fails to authenticate.
                    Url = row.Url,
                    Store = string.IsNullOrWhiteSpace(className) ? null : className
                });
            }

            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        private void ShowError(string message)
        {
            ErrorText.Text = message;
            ErrorText.Visibility = Visibility.Visible;
        }

        private void HideError() => ErrorText.Visibility = Visibility.Collapsed;
    }
}
