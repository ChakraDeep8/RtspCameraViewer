using System;
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

        public CameraEditDialog(Camera? existing = null)
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
