using System.Linq;
using System.Text.RegularExpressions;

namespace RtspCameraViewer.Services
{
    /// <summary>
    /// Derives a store/site code from a device name like "ESTPL-SGL-C032-BANGLE" so cameras can
    /// be grouped in the storewise view. Purely structural — no site-specific names or codes are
    /// hardcoded here, it just reads the naming convention the device list already uses.
    /// </summary>
    public static class StoreCodeParser
    {
        // The store code segment always looks like a letter (or two) followed by digits —
        // "C032", "C130" — regardless of how many other segments (prefix, camera position)
        // surround it. E.g. "C087-EVERLITE-PLATINUM" must still resolve to "C087", not
        // "EVERLITE", which a purely positional (second-to-last segment) rule would get wrong.
        private static readonly Regex StoreCodeSegment = new(@"^[A-Za-z]{1,3}\d{2,5}$");

        /// <summary>
        /// Returns the store code from a '-'-separated device name (e.g. "C032" from
        /// "ESTPL-SGL-C032-BANGLE" or "C087-EVERLITE-PLATINUM"), or "UNASSIGNED" if no segment
        /// matches the letter+digits store-code shape.
        /// </summary>
        public static string FromDeviceName(string? deviceName)
        {
            if (string.IsNullOrWhiteSpace(deviceName)) return "UNASSIGNED";

            var parts = deviceName.Trim().Split('-', System.StringSplitOptions.RemoveEmptyEntries);
            var match = parts.FirstOrDefault(p => StoreCodeSegment.IsMatch(p));
            if (match != null) return match.ToUpperInvariant();

            // Fallback for names that don't follow the letter+digits convention: the
            // second-to-last segment (skips a trailing camera-position suffix like "-BANGLE").
            return parts.Length >= 2 ? parts[^2].ToUpperInvariant() : "UNASSIGNED";
        }
    }
}
