using System;
using System.Text;

namespace RtspCameraViewer.Models
{
    /// <summary>
    /// Represents a single RTSP camera entry saved by the user.
    /// </summary>
    public class Camera
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        public string Name { get; set; } = "Camera";

        /// <summary>
        /// RTSP URL WITHOUT embedded credentials, e.g. rtsp://192.168.1.10:554/stream1
        /// If the user pastes a URL that already contains user:pass@, it is kept as-is
        /// and Username/Password are left empty.
        /// </summary>
        public string Url { get; set; } = string.Empty;

        public string? Username { get; set; }

        public string? Password { get; set; }

        /// <summary>
        /// Builds the effective RTSP URL used for playback, injecting credentials
        /// into the URL if they were supplied separately.
        /// </summary>
        public string GetPlaybackUrl()
        {
            if (string.IsNullOrWhiteSpace(Url))
                return string.Empty;

            if (string.IsNullOrWhiteSpace(Username))
                return Url;

            try
            {
                var uri = new Uri(Url);
                var userInfo = string.IsNullOrEmpty(Password)
                    ? Uri.EscapeDataString(Username)
                    : $"{Uri.EscapeDataString(Username)}:{Uri.EscapeDataString(Password)}";

                var sb = new StringBuilder();
                sb.Append(uri.Scheme).Append("://").Append(userInfo).Append('@').Append(uri.Host);
                if (!uri.IsDefaultPort)
                    sb.Append(':').Append(uri.Port);
                sb.Append(uri.PathAndQuery);
                return sb.ToString();
            }
            catch
            {
                // Fall back to the raw URL if it isn't a well-formed absolute URI.
                return Url;
            }
        }

        public override string ToString() => Name;
    }
}
