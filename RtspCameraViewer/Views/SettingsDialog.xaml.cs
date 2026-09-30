using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using RtspCameraViewer.Models;
using RtspCameraViewer.Services;

namespace RtspCameraViewer.Views
{
    /// <summary>One existing class, with the name the user may have edited.</summary>
    public class ClassRow : INotifyPropertyChanged
    {
        private string _name = "";

        /// <summary>The name as it was when the dialog opened; the rename matches on this.</summary>
        public string OriginalName { get; init; } = "";

        public string Name
        {
            get => _name;
            set { _name = value; OnChanged(nameof(Name)); }
        }

        public int CameraCount { get; init; }

        public string Summary => CameraCount == 1 ? "1 camera" : $"{CameraCount} cameras";

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnChanged(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }

    /// <summary>
    /// One camera as the settings list shows it: its name, which class it sits in, and whether
    /// it is marked for removal. Edits stay on the row until Save, so Cancel really is a cancel.
    /// </summary>
    public class CameraRow : INotifyPropertyChanged
    {
        private string _name = "";
        private string _className = "";
        private bool _removed;

        public Camera Camera { get; init; } = null!;

        /// <summary>Name and class as they were when the dialog opened, for change detection.</summary>
        public string OriginalName { get; init; } = "";
        public string OriginalClass { get; init; } = "";

        public string Name
        {
            get => _name;
            set { _name = value; OnChanged(nameof(Name)); }
        }

        /// <summary>Empty string means "no class" — the editable combo box cannot hold null.</summary>
        public string ClassName
        {
            get => _className;
            set { _className = value ?? ""; OnChanged(nameof(ClassName)); }
        }

        /// <summary>
        /// Marked for removal. The row stays visible but greyed and locked, so a mis-click is
        /// obvious and undoable before Save rather than a camera vanishing from the list.
        /// </summary>
        public bool Removed
        {
            get => _removed;
            set
            {
                _removed = value;
                OnChanged(nameof(Removed));
                OnChanged(nameof(IsPresent));
                OnChanged(nameof(RowOpacity));
                OnChanged(nameof(RemoveGlyph));
                OnChanged(nameof(RemoveTip));
            }
        }

        public bool IsPresent => !_removed;
        public double RowOpacity => _removed ? 0.45 : 1.0;

        /// <summary>Delete glyph normally; an undo arrow once marked, because the button toggles.</summary>
        public string RemoveGlyph => _removed ? "" : "";
        public string RemoveTip => _removed ? "Keep this camera after all" : "Remove this camera on Save";

        public string Url => Camera.Url;

        public bool NameChanged => !string.Equals(Name?.Trim() ?? "", OriginalName, StringComparison.Ordinal);
        public bool ClassChanged => !string.Equals(ClassName?.Trim() ?? "", OriginalClass, StringComparison.Ordinal);

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnChanged(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }

    /// <summary>
    /// Settings that act on groups of cameras rather than one at a time: which of a DVR's
    /// encodings to pull, what the classes are called, and which camera belongs where.
    /// </summary>
    public partial class SettingsDialog : Window
    {
        private const string AllCamerasScope = "— All cameras —";

        private readonly List<Camera> _cameras;
        private readonly ObservableCollection<ClassRow> _classes = new();

        /// <summary>Every camera as an editable row, unfiltered. CameraList shows a filtered view.</summary>
        private readonly List<CameraRow> _cameraRows = new();

        /// <summary>
        /// Class names offered by each row's picker. Public and observable because the rows bind
        /// to it through the window, and it has to grow the moment a new class name is typed.
        /// </summary>
        public ObservableCollection<string> ClassNameOptions { get; } = new();

        /// <summary>The empty entry in the class picker, meaning "belongs to no class".</summary>
        private const string NoClassOption = "";

        /// <summary>
        /// Quality as it was when the dialog opened. Apply edits the live Camera objects so the
        /// impact line can reflect the result immediately, which means Cancel has to be able to
        /// put them back - otherwise cancelling would leave the change applied in memory and it
        /// would reappear the next time anything saved.
        /// </summary>
        private readonly Dictionary<string, StreamQuality> _qualityOnOpen;
        private readonly Dictionary<string, (string? Aspect, DisplayFit Fit)> _shapeOnOpen;

        /// <summary>True when anything was changed, so the host knows to save and restart streams.</summary>
        public bool Changed { get; private set; }

        public SettingsDialog(List<Camera> cameras, string? currentClass)
        {
            InitializeComponent();
            FluentWindow.Attach(this);
            _cameras = cameras;
            _qualityOnOpen = cameras.ToDictionary(c => c.Id, c => c.Quality);
            _shapeOnOpen = cameras.ToDictionary(c => c.Id, c => (c.DisplayAspect, c.DisplayFit));

            // --- quality scope: all cameras, or one class ---
            ScopeCombo.Items.Add(AllCamerasScope);
            foreach (var c in ClassNames()) ScopeCombo.Items.Add(c);
            ScopeCombo.SelectedItem = currentClass != null && ScopeCombo.Items.Contains(currentClass)
                ? currentClass
                : AllCamerasScope;

            QualityCombo.Items.Add(new ComboBoxItem { Content = "Leave as configured", Tag = StreamQuality.AsConfigured });
            QualityCombo.Items.Add(new ComboBoxItem { Content = "Full resolution (main stream)", Tag = StreamQuality.Main });
            QualityCombo.Items.Add(new ComboBoxItem { Content = "Low resolution (sub stream)", Tag = StreamQuality.Sub });
            QualityCombo.SelectedIndex = 0;

            foreach (var shape in DisplayShape.All) ShapeCombo.Items.Add(shape);
            ShapeCombo.SelectedIndex = 0;

            FitCombo.Items.Add(new ComboBoxItem { Content = "Stretch to fill", Tag = DisplayFit.Stretch });
            FitCombo.Items.Add(new ComboBoxItem { Content = "Crop to fill", Tag = DisplayFit.Crop });
            FitCombo.SelectedIndex = 0;

            // --- classes ---
            foreach (var name in ClassNames())
            {
                _classes.Add(new ClassRow
                {
                    OriginalName = name,
                    Name = name,
                    CameraCount = _cameras.Count(c => Matches(c, name))
                });
            }
            ClassList.ItemsSource = _classes;

            // Renaming a class here has to reach the camera rows, or the camera list would still
            // be showing (and on Save, writing back) the old name for every camera in it.
            foreach (var row in _classes) row.PropertyChanged += ClassRow_PropertyChanged;

            // --- cameras ---
            DataContext = this;
            BuildCameraRows();
            RefreshClassOptions();
            ApplyCameraFilter("");

            UpdateImpact();
            UpdateShapeNote();
        }

        // =====================================================================
        // Cameras: rename, move between classes, remove
        // =====================================================================

        private void BuildCameraRows()
        {
            _cameraRows.Clear();
            foreach (var camera in _cameras
                         .OrderBy(c => StoreOrEmpty(c) == "" ? 1 : 0)
                         .ThenBy(StoreOrEmpty, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
            {
                _cameraRows.Add(new CameraRow
                {
                    Camera = camera,
                    Name = camera.Name,
                    OriginalName = camera.Name,
                    ClassName = StoreOrEmpty(camera),
                    OriginalClass = StoreOrEmpty(camera)
                });
            }
        }

        private static string StoreOrEmpty(Camera c) => string.IsNullOrWhiteSpace(c.Store) ? "" : c.Store!;

        /// <summary>
        /// Rebuilds the class picker's options from the classes that exist right now — including
        /// ones only just typed into a row, so a new class can be reused on the next camera
        /// without retyping it.
        /// </summary>
        private void RefreshClassOptions()
        {
            var names = _classes.Select(c => (c.Name ?? "").Trim())
                .Concat(_cameraRows.Select(r => (r.ClassName ?? "").Trim()))
                .Where(n => n.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();

            ClassNameOptions.Clear();
            ClassNameOptions.Add(NoClassOption); // "no class" — an empty row in the list
            foreach (var name in names) ClassNameOptions.Add(name);
        }

        /// <summary>Keeps camera rows in step when their class is renamed in the class list above.</summary>
        private void ClassRow_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(ClassRow.Name) || sender is not ClassRow row) return;

            var newName = (row.Name ?? "").Trim();
            foreach (var camera in _cameraRows)
            {
                // Only rows still showing the old name follow the rename. A row the user has
                // already moved elsewhere by hand is their explicit choice and stays put.
                if (string.Equals(camera.ClassName?.Trim() ?? "", row.OriginalName, StringComparison.OrdinalIgnoreCase))
                    camera.ClassName = newName;
            }
            RefreshClassOptions();
        }

        private void CameraSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            CameraSearchHint.Visibility = string.IsNullOrEmpty(CameraSearch.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;
            ApplyCameraFilter(CameraSearch.Text);
        }

        /// <summary>Shows the rows matching the search, on name, class or URL.</summary>
        private void ApplyCameraFilter(string search)
        {
            search = (search ?? "").Trim();

            var shown = _cameraRows.Where(r =>
                search.Length == 0 ||
                Contains(r.Name, search) || Contains(r.ClassName, search) || Contains(r.Url, search)).ToList();

            CameraList.ItemsSource = shown;
            NoCamerasText.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            int removing = _cameraRows.Count(r => r.Removed);
            CameraCountText.Text = search.Length == 0
                ? Pluralise(_cameraRows.Count) + (removing > 0 ? $"  ·  {removing} to remove" : "")
                : $"{shown.Count} of {_cameraRows.Count} shown";

            static string Pluralise(int n) => n == 1 ? "1 camera" : $"{n} cameras";
            static bool Contains(string? value, string term) =>
                value != null && value.Contains(term, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Toggles a camera between "keep" and "remove on Save". A toggle rather than an instant
        /// delete: nothing is lost until Save, and the same button puts it back.
        /// </summary>
        private void RemoveCamera_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: CameraRow row }) return;
            row.Removed = !row.Removed;
            ApplyCameraFilter(CameraSearch.Text);
        }

        private IEnumerable<string> ClassNames() =>
            _cameras.Select(c => c.Store)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Select(s => s!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(s => s, StringComparer.OrdinalIgnoreCase);

        private static bool Matches(Camera c, string className) =>
            string.Equals(c.Store, className, StringComparison.OrdinalIgnoreCase);

        // =====================================================================
        // Stream quality
        // =====================================================================

        private List<Camera> ScopedCameras()
        {
            var scope = ScopeCombo.SelectedItem as string;
            if (scope == null || scope == AllCamerasScope) return _cameras;
            return _cameras.Where(c => Matches(c, scope)).ToList();
        }

        private StreamQuality SelectedQuality =>
            QualityCombo.SelectedItem is ComboBoxItem { Tag: StreamQuality q } ? q : StreamQuality.AsConfigured;

        private DisplayShape SelectedShape =>
            ShapeCombo.SelectedItem as DisplayShape ?? DisplayShape.Native;

        private DisplayFit SelectedFit =>
            FitCombo.SelectedItem is ComboBoxItem { Tag: DisplayFit f } ? f : DisplayFit.Stretch;

        private void Shape_Changed(object sender, SelectionChangedEventArgs e) => UpdateShapeNote();

        /// <summary>
        /// Spells out what the shape setting does to THESE cameras. A stream is displayed at the
        /// tile's pixel size whatever happens here, so calling this a resolution change would be
        /// a lie; and forcing a landscape shape on a portrait camera either squashes people or
        /// cuts most of the room away, which the user should know before saving rather than
        /// after.
        /// </summary>
        private void UpdateShapeNote()
        {
            if (ShapeNoteText == null) return;

            var shape = SelectedShape;
            if (shape.Aspect == null)
            {
                ShapeNoteText.Text = "Shows each stream at its own shape, letterboxed into the tile.";
                FitCombo.IsEnabled = false;
                return;
            }

            FitCombo.IsEnabled = true;
            ShapeNoteText.Text = SelectedFit == DisplayFit.Crop
                ? $"Crops each stream to {shape.Label} — nothing is distorted, but whatever falls " +
                  "outside that shape is cut off. A portrait camera loses most of its height."
                : $"Stretches each stream to fill {shape.Label} — the whole picture stays visible, " +
                  "but a stream shaped differently will look squashed or elongated.";
        }

        private void Scope_Changed(object sender, SelectionChangedEventArgs e) { UpdateImpact(); UpdateShapeNote(); }
        private void Quality_Changed(object sender, SelectionChangedEventArgs e) => UpdateImpact();

        /// <summary>
        /// Says up front how many of the scoped cameras this can actually affect. Most sites here
        /// stream through a relay with no substream convention to switch, and silently doing
        /// nothing to those would read as the setting being broken.
        /// </summary>
        private void UpdateImpact()
        {
            if (QualityImpactText == null) return; // fires during InitializeComponent

            var scoped = ScopedCameras();
            int switchable = scoped.Count(c => StreamQualityRewriter.CanSwitch(c.Url));
            int total = scoped.Count;

            if (total == 0)
            {
                QualityImpactText.Text = "No cameras in this group.";
                ApplyQualityButton.IsEnabled = false;
                return;
            }

            ApplyQualityButton.IsEnabled = total > 0;

            if (switchable == 0)
            {
                QualityImpactText.Text =
                    $"None of these {total} cameras expose a switchable sub stream — they are direct " +
                    "stream URLs with no main/sub convention, so their resolution is fixed at the source.";
                return;
            }

            var current = scoped.Where(c => StreamQualityRewriter.CanSwitch(c.Url))
                                .Select(c => c.Quality == StreamQuality.AsConfigured
                                    ? StreamQualityRewriter.Detect(c.Url)
                                    : c.Quality)
                                .Distinct().ToList();

            var now = current.Count == 1 ? StreamQualityRewriter.Describe(current[0]) : "mixed";
            QualityImpactText.Text = switchable == total
                ? $"{total} camera{(total == 1 ? "" : "s")}, currently on {now}."
                : $"{switchable} of {total} cameras can switch (currently {now}); the rest have no sub stream.";
        }

        private void ApplyQuality_Click(object sender, RoutedEventArgs e)
        {
            var quality = SelectedQuality;
            var shape = SelectedShape;
            var fit = SelectedFit;
            var scoped = ScopedCameras();
            var notes = new List<string>();

            if (quality != StreamQuality.AsConfigured)
            {
                int applied = 0;
                foreach (var camera in scoped.Where(c => StreamQualityRewriter.CanSwitch(c.Url)))
                {
                    if (camera.Quality == quality) continue;
                    camera.Quality = quality;
                    applied++;
                }
                if (applied > 0)
                {
                    notes.Add($"{applied} set to {StreamQualityRewriter.Describe(quality)}");
                    Changed = true;
                }
            }

            // The shape applies to every camera in scope, not only the switchable ones: it is a
            // display setting and has nothing to do with what the device can encode.
            int shaped = 0;
            foreach (var camera in scoped)
            {
                if (camera.DisplayAspect == shape.Aspect && camera.DisplayFit == fit) continue;
                camera.DisplayAspect = shape.Aspect;
                camera.DisplayFit = fit;
                shaped++;
            }
            if (shaped > 0)
            {
                notes.Add(shape.Aspect == null
                    ? $"{shaped} back to native shape"
                    : $"{shaped} shown at {shape.Label}");
                Changed = true;
            }

            HideError();
            UpdateImpact();

            QualityImpactText.Text = notes.Count == 0
                ? "Already set — nothing to change."
                : string.Join("; ", notes) + ". Save to reconnect them.";
        }

        // =====================================================================
        // Saving
        // =====================================================================

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            var renames = _classes
                .Where(r => !string.Equals(r.Name?.Trim() ?? "", r.OriginalName, StringComparison.Ordinal))
                .ToList();

            // Two classes ending up with the same name is a merge, which is a reasonable thing to
            // want; two rows being renamed INTO each other in one go is not, and would make the
            // result depend on which row was applied first.
            var collidingRenames = renames
                .Select(r => (r.Name ?? "").Trim())
                .Where(n => n.Length > 0)
                .GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();

            if (collidingRenames.Count > 0)
            {
                ShowError($"Two classes are being renamed to \"{collidingRenames[0]}\" at once. " +
                          "Rename one, save, then rename the other into it to merge them.");
                return;
            }

            // A camera cannot be saved without a name — it is the only thing identifying it in
            // the grid, and an empty one would leave a nameless tile nobody can pick out.
            var blank = _cameraRows.FirstOrDefault(r => !r.Removed && string.IsNullOrWhiteSpace(r.Name));
            if (blank != null)
            {
                ShowError($"\"{blank.OriginalName}\" has no name. Give it one, or remove the camera.");
                return;
            }

            var removing = _cameraRows.Where(r => r.Removed).ToList();
            if (removing.Count > 0)
            {
                var names = string.Join(", ", removing.Take(4).Select(r => r.OriginalName));
                if (removing.Count > 4) names += $" and {removing.Count - 4} more";

                var confirm = MessageBox.Show(this,
                    $"Remove {(removing.Count == 1 ? "this camera" : $"these {removing.Count} cameras")}?\n\n{names}\n\n" +
                    "They are taken out of the list entirely. Their RTSP URLs are not recoverable from here.",
                    "Remove cameras", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (confirm != MessageBoxResult.Yes) return;
            }

            foreach (var rename in renames)
            {
                var newName = (rename.Name ?? "").Trim();
                foreach (var camera in _cameras.Where(c => Matches(c, rename.OriginalName)))
                    camera.Store = newName.Length == 0 ? null : newName;
                Changed = true;
            }

            // Per-camera edits go on AFTER the class renames, so moving one camera out of a class
            // that is being renamed does what it looks like rather than being overwritten by the
            // group rename.
            foreach (var row in _cameraRows.Where(r => !r.Removed))
            {
                if (row.NameChanged)
                {
                    row.Camera.Name = row.Name.Trim();
                    Changed = true;
                }
                if (row.ClassChanged)
                {
                    var className = (row.ClassName ?? "").Trim();
                    row.Camera.Store = className.Length == 0 ? null : className;
                    Changed = true;
                }
            }

            if (removing.Count > 0)
            {
                var ids = new HashSet<string>(removing.Select(r => r.Camera.Id));
                _cameras.RemoveAll(c => ids.Contains(c.Id));
                Changed = true;
            }

            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            foreach (var camera in _cameras)
            {
                if (_qualityOnOpen.TryGetValue(camera.Id, out var quality))
                    camera.Quality = quality;
                if (_shapeOnOpen.TryGetValue(camera.Id, out var shape))
                {
                    camera.DisplayAspect = shape.Aspect;
                    camera.DisplayFit = shape.Fit;
                }
            }

            Changed = false;
            DialogResult = false;
        }

        private void ShowError(string message)
        {
            ErrorText.Text = message;
            ErrorText.Visibility = Visibility.Visible;
        }

        private void HideError() => ErrorText.Visibility = Visibility.Collapsed;
    }
}
