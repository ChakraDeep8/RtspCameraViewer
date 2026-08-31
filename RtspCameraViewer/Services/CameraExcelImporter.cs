using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using RtspCameraViewer.Models;

namespace RtspCameraViewer.Services
{
    public enum IpPreference { PreferTailscale, PreferLocal }

    public class ImportedCameraRow
    {
        public string DeviceName { get; set; } = "";
        public string? LocalIp { get; set; }
        public string? TailscaleIp { get; set; }
        public string Store { get; set; } = "UNASSIGNED";
        public string? Note { get; set; }

        /// <summary>The URL that will actually be imported, given the caller's IP preference.</summary>
        public string? ResolveUrl(IpPreference preference)
        {
            var first = preference == IpPreference.PreferTailscale ? TailscaleIp : LocalIp;
            var second = preference == IpPreference.PreferTailscale ? LocalIp : TailscaleIp;

            // Both columns are IP columns, so a usable entry has a literal IP address for a host.
            // Sheets carry placeholder text in that column when a device was never provisioned
            // (e.g. a setup-failed marker), which parses as a perfectly valid URL and would
            // otherwise be imported as a camera that can never resolve. Prefer whichever column
            // actually holds an IP before falling back to raw non-empty text.
            return PickHostedUrl(first) ?? PickHostedUrl(second) ?? FirstNonEmpty(first, second);
        }

        /// <summary>Returns <paramref name="candidate"/> only when its host is a literal IP address.</summary>
        private static string? PickHostedUrl(string? candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate)) return null;
            var trimmed = candidate.Trim();

            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)) return null;
            return IPAddress.TryParse(uri.Host, out _) ? trimmed : null;
        }

        private static string? FirstNonEmpty(string? a, string? b)
        {
            if (!string.IsNullOrWhiteSpace(a)) return a.Trim();
            if (!string.IsNullOrWhiteSpace(b)) return b.Trim();
            return null;
        }
    }

    /// <summary>
    /// Reads a storewise camera list from an Excel workbook. Handles several shapes we've seen
    /// in practice: a single flat table (DEVICE NAME | LOCAL IP | TAILSCALE IP | ...), a workbook
    /// with an extra "Summary" sheet before the real data, and a sheet where the device table is
    /// broken into one block per store with the header row repeated above each block. Column
    /// names are matched loosely/case-insensitively; only the matching keywords are hardcoded
    /// here — never any actual device/IP data.
    /// </summary>
    public static class CameraExcelImporter
    {
        // Matches section-title rows like "Store C130 (6 devices)" that sit above a repeated
        // header row in a per-store-block layout — never a real device row.
        private static readonly Regex StoreTitleRow = new(@"^\s*Store\s+\S+.*\(\s*\d+", RegexOptions.IgnoreCase);

        public static List<ImportedCameraRow> Read(string path)
        {
            var rows = new List<ImportedCameraRow>();

            using var wb = new XLWorkbook(path);
            foreach (var ws in wb.Worksheets)
                rows.AddRange(ReadSheet(ws));

            // Sorted storewise (then by device name) regardless of the row/sheet order in the
            // workbook, so the preview — and whatever gets imported from it — already reads
            // store-by-store. De-duplicated by device name in case the same device appears in
            // more than one sheet (e.g. a "By Store" and an "All Devices" tab).
            return rows
                .GroupBy(r => r.DeviceName, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(r => r.Store, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.DeviceName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static List<ImportedCameraRow> ReadSheet(IXLWorksheet ws)
        {
            var rows = new List<ImportedCameraRow>();

            var firstRow = ws.FirstRowUsed();
            var lastRow = ws.LastRowUsed();
            if (firstRow == null || lastRow == null) return rows;

            int nameCol = -1, localCol = -1, tailscaleCol = -1, statusCol = -1, blurCol = -1, blockCol = -1;

            for (int r = firstRow.RowNumber(); r <= lastRow.RowNumber(); r++)
            {
                var row = ws.Row(r);

                // A header row for this block? Update the column map and move on — a sheet can
                // have one header at the top, or one repeated above every per-store block.
                var headerNameCol = FindExactNameColumn(row);
                if (headerNameCol != -1 && LooksLikeHeaderRow(row, headerNameCol))
                {
                    nameCol = headerNameCol;
                    localCol = FindColumn(row, "LOCAL");
                    tailscaleCol = FindColumn(row, "TAIL");
                    statusCol = FindColumn(row, "STATUS");
                    blurCol = FindColumn(row, "BLUR");
                    blockCol = FindColumn(row, "BLOCK");
                    continue;
                }

                if (nameCol == -1) continue; // no header seen yet on this sheet (e.g. a title/summary sheet)

                var name = row.Cell(nameCol).GetString().Trim();
                if (string.IsNullOrWhiteSpace(name)) continue; // blank separator row
                if (StoreTitleRow.IsMatch(name)) continue; // "Store C130 (6 devices)" section title, not a device

                var notes = new List<string>();
                if (statusCol != -1)
                {
                    var status = row.Cell(statusCol).GetString().Trim();
                    if (!string.IsNullOrWhiteSpace(status)) notes.Add(status);
                }
                if (blurCol != -1 && row.Cell(blurCol).GetString().Trim().Length > 0) notes.Add("BLUR");
                if (blockCol != -1 && row.Cell(blockCol).GetString().Trim().Length > 0) notes.Add("BLOCKED");

                rows.Add(new ImportedCameraRow
                {
                    DeviceName = name,
                    LocalIp = localCol != -1 ? row.Cell(localCol).GetString().Trim() : null,
                    TailscaleIp = tailscaleCol != -1 ? row.Cell(tailscaleCol).GetString().Trim() : null,
                    Store = StoreCodeParser.FromDeviceName(name),
                    Note = notes.Count > 0 ? string.Join(", ", notes) : null
                });
            }

            return rows;
        }

        /// <summary>
        /// A row is treated as a header only if its "name" cell is a short label like "Device
        /// Name" rather than an actual device — i.e. it doesn't itself look like a device name
        /// (no digits, no '-'-separated device-code shape) and at least one neighboring cell also
        /// looks like a column label (LOCAL/TAIL/STATUS/BLUR/BLOCK/IP/NOTES).
        /// </summary>
        private static bool LooksLikeHeaderRow(IXLRow row, int nameCol)
        {
            var nameCell = row.Cell(nameCol).GetString().Trim();
            if (nameCell.Length == 0 || nameCell.Any(char.IsDigit) || nameCell.Contains('-'))
                return false;

            var lastCol = row.LastCellUsed()?.Address.ColumnNumber ?? 0;
            for (int c = 1; c <= lastCol; c++)
            {
                if (c == nameCol) continue;
                var header = row.Cell(c).GetString().ToUpperInvariant();
                if (header.Contains("LOCAL") || header.Contains("TAIL") || header.Contains("STATUS") ||
                    header.Contains("BLUR") || header.Contains("BLOCK") || header.Contains("IP") || header.Contains("NOTE"))
                    return true;
            }
            return false;
        }

        // Exact-label match (not substring) so a summary sheet's "Total Devices" column, or
        // similar, never gets mistaken for the actual device-name column.
        private static readonly string[] NameColumnLabels = { "DEVICE NAME", "DEVICE", "NAME", "CAMERA NAME", "CAMERA" };

        private static int FindExactNameColumn(IXLRow row)
        {
            var lastCol = row.LastCellUsed()?.Address.ColumnNumber ?? 0;
            for (int c = 1; c <= lastCol; c++)
            {
                var header = row.Cell(c).GetString().Trim().ToUpperInvariant();
                if (NameColumnLabels.Contains(header))
                    return c;
            }
            return -1;
        }

        private static int FindColumn(IXLRow row, params string[] contains)
        {
            var lastCol = row.LastCellUsed()?.Address.ColumnNumber ?? 0;
            for (int c = 1; c <= lastCol; c++)
            {
                var header = row.Cell(c).GetString().ToUpperInvariant();
                if (contains.Any(k => header.Contains(k)))
                    return c;
            }
            return -1;
        }
    }
}
