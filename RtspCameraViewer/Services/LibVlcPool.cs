using System;
using System.Collections.Generic;
using System.Linq;
using LibVLCSharp.Shared;

namespace RtspCameraViewer.Services
{
    /// <summary>
    /// Hands out a LibVLC instance configured for a given video-filter chain, creating each one
    /// at most once.
    ///
    /// Why this exists: VLC reads "video-filter" when it builds a video output, from the
    /// INSTANCE's configuration. Passing ":video-filter=sepia" as a media option — the obvious
    /// thing, and what the per-media display settings in this app already do — is silently
    /// ignored: playback starts, no error is reported anywhere, and the picture is simply
    /// unfiltered. The only reliable way to get a filter module into the chain through LibVLCSharp
    /// is to start an instance with "--video-filter=…" on its command line.
    ///
    /// So each distinct chain gets its own instance, made on first use. In practice a site uses
    /// one or two looks, so this is one or two extra instances; the plugin cache is shared between
    /// them, and a camera with no preset keeps using the plain instance as before.
    ///
    /// A MediaPlayer and its Media must come from the SAME instance, which is why the tile asks
    /// this for an instance and builds both from it.
    /// </summary>
    public sealed class LibVlcPool : IDisposable
    {
        private readonly Dictionary<string, LibVLC> _byChain = new(StringComparer.Ordinal);
        private bool _disposed;

        /// <summary>The instance for cameras with no filter preset.</summary>
        public LibVLC Plain => For(FilterPreset.All[0]);

        public LibVLC For(FilterPreset preset)
        {
            // Key on the whole argument list, not just the module name: two presets could use the
            // same module with different parameters (two blurs at different sigmas), and they need
            // separate instances because the parameter is instance configuration too.
            var args = preset.InstanceArgs().ToArray();
            var key = string.Join(" ", args);

            if (_byChain.TryGetValue(key, out var existing)) return existing;

            var vlc = args.Length == 0 ? new LibVLC(enableDebugLogs: false) : new LibVLC(args);
            _byChain[key] = vlc;
            return vlc;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var vlc in _byChain.Values)
            {
                try { vlc.Dispose(); } catch { /* shutting down anyway */ }
            }
            _byChain.Clear();
        }
    }
}
