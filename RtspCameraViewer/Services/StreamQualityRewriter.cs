using System;
using System.Text.RegularExpressions;

namespace RtspCameraViewer.Services
{
    /// <summary>Which of a DVR's parallel encodings to ask for.</summary>
    public enum StreamQuality
    {
        /// <summary>Whatever the saved URL asks for — leave it alone.</summary>
        AsConfigured = 0,

        /// <summary>Main stream: the DVR's full resolution.</summary>
        Main = 1,

        /// <summary>Sub stream: the DVR's low-resolution encoding, typically CIF/D1.</summary>
        Sub = 2
    }

    /// <summary>
    /// Switches an RTSP URL between a DVR's main and sub streams.
    ///
    /// This is the ONLY honest way to change a feed's resolution. Nothing here re-encodes: the
    /// camera decides what it sends, and asking VLC to scale after decode would cost MORE work,
    /// not less, while the full-resolution frames still crossed the network. DVRs already
    /// encode every channel twice — a full-resolution main stream and a small sub stream — and
    /// picking the sub stream cuts resolution, bandwidth and decoder load together, which is
    /// what actually lets more cameras run at once.
    ///
    /// Only URL shapes whose substream convention is known are touched. Anything else is
    /// returned unchanged rather than guessed at, because a URL edited into a shape the device
    /// does not serve fails as a dead tile with no clue why.
    /// </summary>
    public static class StreamQualityRewriter
    {
        // Dahua / CP Plus / many OEMs:  /cam/realmonitor?channel=1&subtype=0
        private static readonly Regex DahuaSubtype =
            new(@"(?<=[?&]subtype=)\d+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Hikvision:  /Streaming/Channels/101   (channel 1, stream 1 = main; 102 = sub)
        private static readonly Regex HikChannel =
            new(@"(?<=/Streaming/Channels/)(?<channel>\d+)(?<stream>\d)(?=/?$|\?)",
                RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>True when this URL's substream convention is one we recognise.</summary>
        public static bool CanSwitch(string url) =>
            !string.IsNullOrWhiteSpace(url) && (DahuaSubtype.IsMatch(url) || HikChannel.IsMatch(url));

        /// <summary>
        /// Returns <paramref name="url"/> pointed at the requested stream, or unchanged when the
        /// quality is AsConfigured or the URL's convention is not recognised.
        /// </summary>
        public static string Apply(string url, StreamQuality quality)
        {
            if (quality == StreamQuality.AsConfigured || string.IsNullOrWhiteSpace(url)) return url;

            if (DahuaSubtype.IsMatch(url))
                return DahuaSubtype.Replace(url, quality == StreamQuality.Sub ? "1" : "0", 1);

            if (HikChannel.IsMatch(url))
            {
                return HikChannel.Replace(url, m =>
                    m.Groups["channel"].Value + (quality == StreamQuality.Sub ? "2" : "1"), 1);
            }

            return url;
        }

        /// <summary>Which stream a URL currently points at, for showing the user where they are.</summary>
        public static StreamQuality Detect(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return StreamQuality.AsConfigured;

            var dahua = DahuaSubtype.Match(url);
            if (dahua.Success) return dahua.Value == "0" ? StreamQuality.Main : StreamQuality.Sub;

            var hik = HikChannel.Match(url);
            if (hik.Success) return hik.Groups["stream"].Value == "1" ? StreamQuality.Main : StreamQuality.Sub;

            return StreamQuality.AsConfigured;
        }

        public static string Describe(StreamQuality quality) => quality switch
        {
            StreamQuality.Main => "main stream (full resolution)",
            StreamQuality.Sub => "sub stream (low resolution)",
            _ => "as configured"
        };
    }
}
