using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using RtspCameraViewer.Models;

namespace RtspCameraViewer.Services
{
    /// <summary>
    /// Cameras pre-loaded on first run, when no cameras.json exists yet.
    ///
    /// Empty unless a seed file is sitting beside the executable. That file is deliberately NOT
    /// in source control: a real camera list is RTSP URLs, DVR addresses and, for some cameras,
    /// credentials embedded in the URL — none of which belongs in a public repository or in a
    /// release anyone can download. <c>cameras.seed.json</c> is gitignored, kept on the machines
    /// that need it, and read only when the app has no list of its own yet.
    ///
    /// The format is exactly the format of cameras.json, so a seed is made by copying a working
    /// %AppData%\RtspCameraViewer\cameras.json next to the exe. Nothing new to maintain.
    /// </summary>
    public static class DefaultCameras
    {
        /// <summary>Seed file name, looked for in the application directory.</summary>
        public const string SeedFileName = "cameras.seed.json";

        public static List<Camera> Get()
        {
            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, SeedFileName);
                if (!File.Exists(path)) return new List<Camera>();

                var cameras = JsonSerializer.Deserialize<List<Camera>>(File.ReadAllText(path));
                return cameras ?? new List<Camera>();
            }
            catch
            {
                // A malformed seed must not stop the app starting. An empty list is the same
                // place a fresh install would be, and "+ Add Camera" still works from there.
                return new List<Camera>();
            }
        }
    }
}
