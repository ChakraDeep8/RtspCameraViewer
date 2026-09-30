using System;
using System.Collections.Generic;
using System.Linq;

namespace RtspCameraViewer.Services
{
    /// <summary>
    /// A named look that can be applied to a camera in one click — Grayscale, Sepia, Blur and so
    /// on — as opposed to dialling the five sliders by hand.
    ///
    /// A preset reaches the picture by one of two routes, and the difference is visible to the
    /// user, so it is modelled explicitly rather than hidden:
    ///
    /// * <see cref="Adjustments"/> go through LibVLC's "adjust" filter, which is already in the
    ///   render chain. They land on the next decoded frame — instant, no interruption.
    ///
    /// * <see cref="VideoFilter"/> names a VLC video-filter module (sepia, posterize, …). Those
    ///   are built into the chain when playback starts and CANNOT be swapped on a running player,
    ///   so choosing one of these presets restarts that camera's stream. It is a second or two of
    ///   reconnect, which is why presets that need it say so in the UI.
    ///
    /// Everything here is a display treatment. The DVR's recording, and what any other client
    /// sees, is untouched.
    /// </summary>
    public class FilterPreset
    {
        /// <summary>Stable key stored on the camera. Never change these — they are persisted.</summary>
        public string Id { get; init; } = "none";

        public string Label { get; init; } = "None";

        /// <summary>
        /// VLC video-filter module name, or null when the preset is pure adjust. Colon-separated
        /// for a chain, which is how VLC itself spells a filter chain.
        /// </summary>
        public string? VideoFilter { get; init; }

        /// <summary>
        /// Extra parameters the filter module needs, e.g. "sepia-intensity=120". Passed on the
        /// LibVLC command line (see LibVlcPool) because that is the only place VLC reads them.
        /// </summary>
        public string[] FilterOptions { get; init; } = Array.Empty<string>();

        /// <summary>Adjust-filter values this preset sets. Null leaves the knob neutral.</summary>
        public float? Brightness { get; init; }
        public float? Contrast { get; init; }
        public float? Saturation { get; init; }
        public float? Hue { get; init; }
        public float? Gamma { get; init; }

        /// <summary>
        /// True when applying this preset has to restart the stream — i.e. it uses a video-filter
        /// module. The UI marks these so a brief reconnect is expected rather than alarming.
        /// </summary>
        public bool NeedsRestart => !string.IsNullOrEmpty(VideoFilter);

        /// <summary>Builds the adjustment values this preset implies, starting from neutral.</summary>
        public VideoAdjustments ToAdjustments()
        {
            var a = new VideoAdjustments();
            if (Brightness.HasValue) a.Brightness = Brightness.Value;
            if (Contrast.HasValue) a.Contrast = Contrast.Value;
            if (Saturation.HasValue) a.Saturation = Saturation.Value;
            if (Hue.HasValue) a.Hue = Hue.Value;
            if (Gamma.HasValue) a.Gamma = Gamma.Value;
            return a;
        }

        public const string NoneId = "none";

        /// <summary>
        /// The presets offered, in the order they appear in the picker.
        ///
        /// Three looks that were asked for are absent, because LibVLC 3 ships no filter that does
        /// them and faking them would mean a preset that quietly does something else:
        /// Vignette (no vignette module at all), Solarize (colorthres isolates a hue, it does not
        /// solarize) and Emboss (the gradient module does gradient, edge and cartoon, not emboss).
        /// Doing those properly means post-processing frames ourselves rather than asking VLC.
        /// </summary>
        public static readonly FilterPreset[] All =
        {
            new() { Id = NoneId, Label = "None" },

            // --- pure adjust: instant, no restart ---
            new() { Id = "grayscale", Label = "Grayscale", Saturation = 0f },
            new() { Id = "bw", Label = "Black && White", Saturation = 0f, Contrast = 1.45f },
            new() { Id = "warm", Label = "Warm", Hue = 20f, Saturation = 1.18f, Gamma = 1.05f },
            new() { Id = "cool", Label = "Cool", Hue = 340f, Saturation = 1.1f },
            new() { Id = "bright", Label = "Bright", Brightness = 1.35f, Contrast = 1.1f },

            // --- video-filter modules: restart the stream ---
            new() { Id = "invert", Label = "Invert", VideoFilter = "invert" },
            new() { Id = "blur", Label = "Blur", VideoFilter = "gaussianblur",
                    FilterOptions = new[] { "gaussianblur-sigma=3.5" } },
            new() { Id = "denoise", Label = "Denoise", VideoFilter = "hqdn3d" },
            new() { Id = "edges", Label = "Edges", VideoFilter = "edgedetection" },
            new() { Id = "sepia", Label = "Sepia", VideoFilter = "sepia",
                    FilterOptions = new[] { "sepia-intensity=120" } },
            new() { Id = "vintage", Label = "Vintage", VideoFilter = "oldmovie" },
            new() { Id = "posterize", Label = "Posterize", VideoFilter = "posterize",
                    FilterOptions = new[] { "posterize-level=6" } },
            new() { Id = "sharpen", Label = "Sharpen", VideoFilter = "sharpen",
                    FilterOptions = new[] { "sharpen-sigma=1.5" } },
            new() { Id = "cartoon", Label = "Cartoon", VideoFilter = "gradient",
                    FilterOptions = new[] { "gradient-mode=hough", "gradient-cartoon" } },
            // Edge detection with the colour pulled out reads as a chalk drawing.
            new() { Id = "pencil", Label = "Pencil Sketch", VideoFilter = "edgedetection", Saturation = 0f },
            new() { Id = "grain", Label = "Film Grain", VideoFilter = "grain" },
        };

        public static FilterPreset ById(string? id) =>
            All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)) ?? All[0];

        /// <summary>
        /// Command-line arguments for the LibVLC instance that runs this preset.
        ///
        /// These are INSTANCE arguments, not media options, and that distinction is the whole
        /// reason LibVlcPool exists: ":video-filter=…" attached to a Media is accepted and then
        /// ignored, with no error and no filtering.
        /// </summary>
        public IEnumerable<string> InstanceArgs()
        {
            if (string.IsNullOrEmpty(VideoFilter)) yield break;
            yield return "--video-filter=" + VideoFilter;
            foreach (var option in FilterOptions) yield return "--" + option;
        }
    }
}
