using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using ClosedXML.Excel;

namespace RtspCameraViewer.Services
{
    /// <summary>One RTSP URL found in a file, with the best name that could be inferred for it.</summary>
    public class DiscoveredCamera
    {
        public string Name { get; set; } = "Camera";
        public string Url { get; set; } = "";

        /// <summary>Where in the file this came from, shown in the preview so the user can tell
        /// which entry is which when several resolve to similar names.</summary>
        public string Source { get; set; } = "";
    }

    /// <summary>
    /// Pulls RTSP URLs out of whatever file the user has to hand — a .env, a plain text or
    /// config file, a CSV, or a spreadsheet.
    ///
    /// The design assumption is that these files are written for humans and DVR installers, not
    /// for this app, so there is no schema to rely on: the URL itself is the only thing that can
    /// be recognised reliably. Everything around it is treated as a hint for the camera's name.
    /// </summary>
    public static class RtspFileImporter
    {
        /// <summary>
        /// Deliberately excludes , ; " ' &lt; &gt; and whitespace so a URL sitting in a CSV cell or
        /// quoted in a config file stops at the delimiter rather than swallowing it. Query
        /// strings (?channel=1&amp;subtype=0) and percent-escaped credentials survive intact.
        /// </summary>
        private static readonly Regex UrlPattern =
            new(@"rtsps?://[^\s,;""'<>]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly string[] TextExtensions =
            { ".env", ".txt", ".csv", ".tsv", ".ini", ".conf", ".cfg", ".log", ".json", ".yaml", ".yml", ".xml", ".md" };

        public static bool IsSpreadsheet(string path) =>
            Path.GetExtension(path).Equals(".xlsx", StringComparison.OrdinalIgnoreCase);

        /// <summary>Filter string for the file picker: everything this importer can read.</summary>
        public const string FileFilter =
            "All supported files|*.env;*.txt;*.csv;*.tsv;*.ini;*.conf;*.cfg;*.log;*.json;*.yaml;*.yml;*.xml;*.xlsx;*.*|" +
            "Environment files|*.env;env|" +
            "Text and config|*.txt;*.csv;*.tsv;*.ini;*.conf;*.cfg;*.log|" +
            "Excel workbook|*.xlsx|" +
            "All files|*.*";

        public static List<DiscoveredCamera> Read(string path)
        {
            var found = IsSpreadsheet(path) ? ReadSpreadsheet(path) : ReadText(path);

            // Same stream listed twice in one file adds nothing; keep the first, which is the one
            // whose surrounding comment the name was taken from.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return found.Where(c => seen.Add(c.Url)).ToList();
        }

        // =====================================================================
        // Text-shaped files (.env, .txt, .ini, .csv, ...)
        // =====================================================================

        private static List<DiscoveredCamera> ReadText(string path)
        {
            var results = new List<DiscoveredCamera>();
            var lines = File.ReadAllLines(path, DetectEncoding(path));

            // The last comment seen, kept until a URL consumes it. In a .env the camera's real
            // name is almost always the comment sitting above the entry:
            //     # dev_room
            //     RTSP_CHANNEL_1=rtsp://...
            // A blank line does NOT clear it - files in the wild put a blank line between the
            // comment and its entry as often as not.
            string? pendingComment = null;

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (string.IsNullOrWhiteSpace(line)) continue;

                var matches = UrlPattern.Matches(line);
                if (matches.Count == 0)
                {
                    var comment = ExtractComment(line);
                    if (comment != null) pendingComment = comment;
                    continue;
                }

                foreach (Match m in matches)
                {
                    var url = CleanUrl(m.Value);
                    if (url == null) continue;

                    results.Add(new DiscoveredCamera
                    {
                        Url = url,
                        Name = NameForTextLine(line, url, pendingComment, results.Count),
                        Source = $"line {i + 1}"
                    });

                    // One comment names one camera; a second URL under the same comment has to
                    // fall back, or every channel would come in with an identical name.
                    pendingComment = null;
                }
            }

            return results;
        }

        /// <summary>Returns the text of a whole-line comment, or null if the line isn't one.</summary>
        private static string? ExtractComment(string line)
        {
            var t = line.TrimStart();
            string? body = null;
            if (t.StartsWith("#")) body = t.TrimStart('#');
            else if (t.StartsWith("//")) body = t[2..];
            else if (t.StartsWith(";")) body = t.TrimStart(';');
            if (body == null) return null;

            body = body.Trim();
            // A divider or heading ("# DVR RTSP Camera Streams", "# ---") is not a camera name.
            if (body.Length == 0 || body.All(c => c == '-' || c == '=' || c == '*')) return null;
            return body;
        }

        private static string NameForTextLine(string line, string url, string? comment, int index)
        {
            if (!string.IsNullOrWhiteSpace(comment)) return Prettify(comment);

            // KEY=rtsp://...  ->  the key names the camera.
            var beforeUrl = line[..line.IndexOf(url, StringComparison.OrdinalIgnoreCase)];
            var eq = beforeUrl.LastIndexOf('=');
            if (eq > 0)
            {
                var key = beforeUrl[..eq].Trim().Trim('"', '\'', ',', ':');
                if (key.Length > 0 && key.Length <= 60) return Prettify(key);
            }

            // A label written inline before the URL: "Gate camera - rtsp://...", "Lobby: rtsp://..."
            var inline = beforeUrl.Trim().TrimEnd('-', ':', '|', '\t', ' ').Trim().Trim('"', '\'');
            if (inline.Length > 0 && inline.Length <= 60 && !inline.Contains(',') && !inline.Contains('\t'))
                return Prettify(inline);

            // A delimited row: take the first cell that isn't the URL.
            foreach (var cell in line.Split(',', '\t', ';'))
            {
                var v = cell.Trim().Trim('"');
                if (v.Length > 0 && !v.Contains("rtsp", StringComparison.OrdinalIgnoreCase))
                    return Prettify(v);
            }

            return NameFromUrl(url, index);
        }

        // =====================================================================
        // Spreadsheets
        // =====================================================================

        private static List<DiscoveredCamera> ReadSpreadsheet(string path)
        {
            var results = new List<DiscoveredCamera>();
            using var workbook = new XLWorkbook(path);

            foreach (var sheet in workbook.Worksheets)
            {
                var used = sheet.RangeUsed();
                if (used == null) continue;

                foreach (var row in used.RowsUsed())
                {
                    foreach (var cell in row.CellsUsed())
                    {
                        var text = cell.GetString();
                        var match = UrlPattern.Match(text);
                        if (!match.Success) continue;

                        var url = CleanUrl(match.Value);
                        if (url == null) continue;

                        results.Add(new DiscoveredCamera
                        {
                            Url = url,
                            Name = NameForSpreadsheetRow(row, cell.Address.ColumnNumber, results.Count, url),
                            Source = $"{sheet.Name}!{cell.Address.ColumnLetter}{cell.Address.RowNumber}"
                        });
                    }
                }
            }

            return results;
        }

        private static string NameForSpreadsheetRow(IXLRangeRow row, int urlColumn, int index, string url)
        {
            // The label is normally to the LEFT of the URL, so walk outwards from the URL cell
            // rather than taking the first non-empty cell in the row.
            foreach (var cell in row.CellsUsed().OrderBy(c => Math.Abs(c.Address.ColumnNumber - urlColumn)))
            {
                if (cell.Address.ColumnNumber == urlColumn) continue;
                var v = cell.GetString().Trim();
                if (v.Length == 0) continue;
                if (v.Contains("rtsp", StringComparison.OrdinalIgnoreCase)) continue;
                // Skip bare numbers - a channel index or a row number is not a name.
                if (double.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out _)) continue;
                if (v.Length <= 60) return Prettify(v);
            }

            return NameFromUrl(url, index);
        }

        // =====================================================================
        // Shared
        // =====================================================================

        /// <summary>Validates and tidies a matched URL, returning null if it isn't usable.</summary>
        private static string? CleanUrl(string raw)
        {
            var url = raw.Trim().TrimEnd('.', ',', ';', ')', ']', '}', '"', '\'');
            if (url.Length < "rtsp://x".Length) return null;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
            return string.IsNullOrEmpty(uri.Host) ? null : url;
        }

        /// <summary>
        /// Last-resort name built from the URL: host plus whatever looks like a channel, so eight
        /// channels off one DVR still come out distinguishable rather than eight "Camera"s.
        /// </summary>
        private static string NameFromUrl(string url, int index)
        {
            try
            {
                var uri = new Uri(url);
                var channel = Regex.Match(uri.Query, @"channel=(\d+)", RegexOptions.IgnoreCase);
                if (channel.Success) return $"{uri.Host} ch{channel.Groups[1].Value}";

                var lastSegment = uri.Segments.LastOrDefault()?.Trim('/');
                if (!string.IsNullOrWhiteSpace(lastSegment)) return $"{uri.Host} {Prettify(lastSegment)}";
                return uri.Host;
            }
            catch
            {
                return $"Camera {index + 1}";
            }
        }

        /// <summary>
        /// Turns file-shaped labels into readable camera names: "side passage_1" -> "Side Passage 1",
        /// "RTSP_CHANNEL_8" -> "Rtsp Channel 8". Names are what fill the tile headers, so it is
        /// worth doing; the user can still edit any of them before importing.
        /// </summary>
        private static string Prettify(string raw)
        {
            var cleaned = raw.Replace('_', ' ').Replace('-', ' ').Trim();
            cleaned = Regex.Replace(cleaned, @"\s+", " ");
            if (cleaned.Length == 0) return raw.Trim();

            var words = cleaned.Split(' ').Select(w =>
            {
                if (w.Length == 0) return w;
                // Leave anything with mixed case or digits alone (C006, IPCam2, ch3).
                if (w.Any(char.IsDigit) && w.Any(char.IsLetter)) return w;
                if (w.Any(char.IsUpper) && w.Any(char.IsLower)) return w;
                return char.ToUpper(w[0], CultureInfo.InvariantCulture) + w[1..].ToLower(CultureInfo.InvariantCulture);
            });

            var result = string.Join(' ', words);
            return result.Length > 60 ? result[..60].TrimEnd() : result;
        }

        /// <summary>Honours a BOM when present, otherwise UTF-8 — .env files are UTF-8 in practice.</summary>
        private static Encoding DetectEncoding(string path)
        {
            using var fs = File.OpenRead(path);
            var bom = new byte[4];
            int read = fs.Read(bom, 0, 4);
            if (read >= 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF) return Encoding.UTF8;
            if (read >= 2 && bom[0] == 0xFF && bom[1] == 0xFE) return Encoding.Unicode;
            if (read >= 2 && bom[0] == 0xFE && bom[1] == 0xFF) return Encoding.BigEndianUnicode;
            return Encoding.UTF8;
        }
    }
}
