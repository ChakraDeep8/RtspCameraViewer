using System.Collections.Generic;
using RtspCameraViewer.Models;

namespace RtspCameraViewer.Services
{
    /// <summary>
    /// Cameras pre-loaded on first run (when no cameras.json exists yet). Empty by default —
    /// add your own cameras via "+ Add Camera" once the app is running.
    /// </summary>
    public static class DefaultCameras
    {
        public static List<Camera> Get() => new();
    }
}
