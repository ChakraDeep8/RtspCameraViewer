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
    /// Two settings that both act on groups of cameras rather than one: which of a DVR's
    /// encodings to pull, and what the classes are called.
    /// </summary>
    public partial class SettingsDialog : Window
    {
        private const string AllCamerasScope = "— All cameras —";

        private readonly List<Camera> _cameras;
        private readonly ObservableCollection<ClassRow> _classes = new();

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

            UpdateImpact();
            UpdateShapeNote();
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

            foreach (var rename in renames)
            {
                var newName = (rename.Name ?? "").Trim();
                foreach (var camera in _cameras.Where(c => Matches(c, rename.OriginalName)))
                    camera.Store = newName.Length == 0 ? null : newName;
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
