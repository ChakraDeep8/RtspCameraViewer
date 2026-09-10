using System;
using System.Text;
using RtspCameraViewer.Services;

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
        /// Store/site code this camera belongs to, used to group cameras in the storewise view
        /// (e.g. imported from an Excel sheet, or derived from the device name). Null/empty
        /// means unassigned.
        /// </summary>
        public string? Store { get; set; }

        /// <summary>
        /// Which of the DVR's parallel encodings to request. Held as a preference and applied at
        /// playback rather than written into Url, so switching back to full resolution restores
        /// exactly the URL the user supplied instead of a reconstruction of it.
        /// </summary>
        public StreamQuality Quality { get; set; } = StreamQuality.AsConfigured;

        /// <summary>
        /// Builds the effective RTSP URL used for playback: the saved URL pointed at the
        /// preferred stream, with credentials injected if they were supplied separately.
        /// </summary>
        public string GetPlaybackUrl()
        {
            if (string.IsNullOrWhiteSpace(Url))
                return string.Empty;

            var url = StreamQualityRewriter.Apply(Url, Quality);

            if (string.IsNullOrWhiteSpace(Username))
                return url;

            try
            {
                var uri = new Uri(url);
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
                return url;
            }
        }

        public override string ToString() => Name;
    }
}
