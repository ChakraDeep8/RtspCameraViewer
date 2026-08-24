using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using RtspCameraViewer.Models;

namespace RtspCameraViewer.Services
{
    /// <summary>
    /// Loads and persists the camera list as JSON under %AppData%\RtspCameraViewer.
    /// </summary>
    public static class CameraStore
    {
        private static readonly string StoreDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RtspCameraViewer");

        private static readonly string StoreFile = Path.Combine(StoreDir, "cameras.json");

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true
        };

        public static List<Camera> Load()
        {
            try
            {
                if (!File.Exists(StoreFile))
                {
                    // First run: seed with the default camera list and persist it.
                    var defaults = DefaultCameras.Get();
                    Save(defaults);
                    return defaults;
                }

                var json = File.ReadAllText(StoreFile);
                var cameras = JsonSerializer.Deserialize<List<Camera>>(json, JsonOptions);
                return cameras ?? new List<Camera>();
            }
            catch
            {
                // Corrupt or unreadable file: start fresh rather than crash the app.
                return new List<Camera>();
            }
        }

        public static void Save(IEnumerable<Camera> cameras)
        {
            Directory.CreateDirectory(StoreDir);
            var json = JsonSerializer.Serialize(cameras, JsonOptions);
            File.WriteAllText(StoreFile, json);
        }

        public static string FilePath => StoreFile;
    }
}
