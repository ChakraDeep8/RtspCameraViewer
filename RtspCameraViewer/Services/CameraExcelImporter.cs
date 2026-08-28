using System;
using System.Collections.Generic;
using System.Linq;
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
            var url = !string.IsNullOrWhiteSpace(first) ? first : second;
            return string.IsNullOrWhiteSpace(url) ? null : url!.Trim();
        }
    }

    /// <summary>
    /// Reads a storewise camera list from an Excel sheet shaped like:
    /// DEVICE NAME | LOCAL IP | TAILSCALE IP | STATUS | BLUR | BLOCK
    /// (column names are matched loosely/case-insensitively; extra/missing columns are fine).
    /// Only the sheet structure is hardcoded here — never any actual device/IP data.
    /// </summary>
    public static class CameraExcelImporter
    {
        public static List<ImportedCameraRow> Read(string path)
        {
            var rows = new List<ImportedCameraRow>();

            using var wb = new XLWorkbook(path);
            var ws = wb.Worksheets.First();
            var headerRow = ws.FirstRowUsed();
            if (headerRow == null) return rows;

            int nameCol = FindColumn(headerRow, "DEVICE", "NAME", "CAMERA");
            int localCol = FindColumn(headerRow, "LOCAL");
            int tailscaleCol = FindColumn(headerRow, "TAIL");
            int statusCol = FindColumn(headerRow, "STATUS");
            int blurCol = FindColumn(headerRow, "BLUR");
            int blockCol = FindColumn(headerRow, "BLOCK");

            if (nameCol == -1) return rows; // can't do anything without a name column

            var lastRow = ws.LastRowUsed()?.RowNumber() ?? headerRow.RowNumber();
            for (int r = headerRow.RowNumber() + 1; r <= lastRow; r++)
            {
                var row = ws.Row(r);
                var name = row.Cell(nameCol).GetString().Trim();
                if (string.IsNullOrWhiteSpace(name)) continue; // blank separator row

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

            // Sorted storewise (then by device name) regardless of the row order in the sheet,
            // so the preview — and whatever gets imported from it — already reads store-by-store.
            return rows
                .OrderBy(r => r.Store, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.DeviceName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static int FindColumn(IXLRow headerRow, params string[] contains)
        {
            var lastCol = headerRow.LastCellUsed()?.Address.ColumnNumber ?? 0;
            for (int c = 1; c <= lastCol; c++)
            {
                var header = headerRow.Cell(c).GetString().ToUpperInvariant();
                if (contains.Any(k => header.Contains(k)))
                    return c;
            }
            return -1;
        }
    }
}
