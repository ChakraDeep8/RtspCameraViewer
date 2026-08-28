namespace RtspCameraViewer.Services
{
    /// <summary>
    /// Derives a store/site code from a device name like "ESTPL-SGL-C032-BANGLE" so cameras can
    /// be grouped in the storewise view. Purely structural — no site-specific names or codes are
    /// hardcoded here, it just reads the naming convention the device list already uses.
    /// </summary>
    public static class StoreCodeParser
    {
        /// <summary>
        /// Returns the store code (the second-to-last '-'-separated segment, e.g. "C032" from
        /// "ESTPL-SGL-C032-BANGLE" or "C032-BANGLE"), or "UNASSIGNED" if the name doesn't have
        /// enough segments.
        /// </summary>
        public static string FromDeviceName(string? deviceName)
        {
            if (string.IsNullOrWhiteSpace(deviceName)) return "UNASSIGNED";

            var parts = deviceName.Trim().Split('-', System.StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return "UNASSIGNED";

            return parts[^2].ToUpperInvariant();
        }
    }
}
