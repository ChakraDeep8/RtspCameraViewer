using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using RtspCameraViewer.Models;

namespace RtspCameraViewer.Views
{
    /// <summary>
    /// Modal dialog used to add a new camera or edit an existing one.
    /// </summary>
    public partial class CameraEditDialog : Window
    {
        public Camera Result { get; private set; }

        private readonly bool _isEdit;

        /// <param name="existingClasses">
        /// Class names already in use, offered in the picker so a camera can be added to an
        /// existing group rather than everyone re-typing the name slightly differently.
        /// </param>
        public CameraEditDialog(Camera? existing = null, IEnumerable<string>? existingClasses = null)
        {
            InitializeComponent();
            _isEdit = existing != null;
            Result = existing ?? new Camera();

            Title = _isEdit ? "Edit Camera" : "Add Camera";
            SaveButton.Content = _isEdit ? "Save" : "Add";

            NameBox.Text = Result.Name;
            UrlBox.Text = Result.Url;
            UserBox.Text = Result.Username ?? "";
            PassBox.Password = Result.Password ?? "";

            foreach (var name in (existingClasses ?? Enumerable.Empty<string>())
                     .Where(c => !string.IsNullOrWhiteSpace(c))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(c => c, StringComparer.OrdinalIgnoreCase))
            {
                ClassCombo.Items.Add(name);
            }
            ClassCombo.Text = Result.Store ?? "";
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            var name = NameBox.Text.Trim();
            var url = UrlBox.Text.Trim();

            if (string.IsNullOrWhiteSpace(name))
            {
                ShowError("Please enter a camera name.");
                return;
            }

            if (string.IsNullOrWhiteSpace(url))
            {
                ShowError("Please enter an RTSP URL.");
                return;
            }

            if (!url.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase))
            {
                ShowError("URL must start with rtsp://");
                return;
            }

            Result.Name = name;
            Result.Url = url;
            // Blank means "no class"; anything else groups this camera under that name.
            var className = ClassCombo.Text?.Trim();
            Result.Store = string.IsNullOrWhiteSpace(className) ? null : className;
            Result.Username = string.IsNullOrWhiteSpace(UserBox.Text) ? null : UserBox.Text.Trim();
            Result.Password = string.IsNullOrEmpty(PassBox.Password) ? null : PassBox.Password;

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
