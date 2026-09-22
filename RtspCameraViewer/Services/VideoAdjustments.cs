using System;
using System.Text.Json.Serialization;
using LibVLCSharp.Shared;

namespace RtspCameraViewer.Services
{
    /// <summary>
    /// Picture adjustments applied to a live feed — brightness, contrast, saturation, hue and
    /// gamma.
    ///
    /// These run in LibVLC's own "adjust" video filter, which sits in the render chain and takes
    /// effect on the NEXT frame. That is what makes this usable on a live camera: nothing is
    /// re-negotiated, the RTSP session is not touched, and the stream does not blink. It is also
    /// purely a display treatment — the recording on the DVR is unaffected, and so is anything
    /// the camera sends to other clients.
    ///
    /// The ranges are LibVLC's, not ours. Sending a value outside them is silently clamped by
    /// the filter, which reads as "the slider does nothing past here", so the UI is built from
    /// <see cref="Ranges"/> instead of hard-coded bounds.
    /// </summary>
    public class VideoAdjustments
    {
        public float Brightness { get; set; } = 1.0f;
        public float Contrast { get; set; } = 1.0f;
        public float Saturation { get; set; } = 1.0f;
        public float Gamma { get; set; } = 1.0f;

        /// <summary>Hue rotation in degrees. 0 and 360 are the same picture.</summary>
        public float Hue { get; set; } = 0f;

        /// <summary>A knob's identity, range and neutral value, shared by the UI and the applier.</summary>
        public readonly struct Range
        {
            public Range(string label, float min, float max, float neutral, string icon)
            {
                Label = label; Min = min; Max = max; Neutral = neutral; Icon = icon;
            }

            public string Label { get; }
            public float Min { get; }
            public float Max { get; }
            public float Neutral { get; }

            /// <summary>Segoe MDL2 Assets glyph shown beside the slider.</summary>
            public string Icon { get; }
        }

        public enum Knob { Brightness, Contrast, Saturation, Hue, Gamma }

        /// <summary>
        /// LibVLC's accepted range for each knob. Brightness/contrast/saturation/gamma are
        /// multipliers around 1.0; hue is an angle.
        /// </summary>
        public static Range RangeOf(Knob knob) => knob switch
        {
            Knob.Brightness => new Range("Brightness", 0f, 2f, 1f, ""),
            Knob.Contrast => new Range("Contrast", 0f, 2f, 1f, ""),
            Knob.Saturation => new Range("Saturation", 0f, 3f, 1f, ""),
            Knob.Hue => new Range("Hue", 0f, 360f, 0f, ""),
            _ => new Range("Gamma", 0.01f, 10f, 1f, "")
        };

        public static readonly Knob[] AllKnobs =
            { Knob.Brightness, Knob.Contrast, Knob.Saturation, Knob.Hue, Knob.Gamma };

        public float Get(Knob knob) => knob switch
        {
            Knob.Brightness => Brightness,
            Knob.Contrast => Contrast,
            Knob.Saturation => Saturation,
            Knob.Hue => Hue,
            _ => Gamma
        };

        public void Set(Knob knob, float value)
        {
            var range = RangeOf(knob);
            value = Math.Clamp(value, range.Min, range.Max);
            switch (knob)
            {
                case Knob.Brightness: Brightness = value; break;
                case Knob.Contrast: Contrast = value; break;
                case Knob.Saturation: Saturation = value; break;
                case Knob.Hue: Hue = value; break;
                default: Gamma = value; break;
            }
        }

        /// <summary>
        /// True when every knob sits at its neutral value, i.e. the picture is untouched. Used to
        /// skip enabling the filter at all, and to show which cameras carry an adjustment.
        /// </summary>
        [JsonIgnore]
        public bool IsNeutral
        {
            get
            {
                foreach (var knob in AllKnobs)
                {
                    if (Math.Abs(Get(knob) - RangeOf(knob).Neutral) > 0.001f) return false;
                }
                return true;
            }
        }

        public void Reset()
        {
            foreach (var knob in AllKnobs) Set(knob, RangeOf(knob).Neutral);
        }

        public VideoAdjustments Clone() => new()
        {
            Brightness = Brightness,
            Contrast = Contrast,
            Saturation = Saturation,
            Hue = Hue,
            Gamma = Gamma
        };

        /// <summary>
        /// Pushes these values into a running player. Safe to call on every slider tick: LibVLC
        /// applies them to the next frame rather than restarting anything.
        ///
        /// The filter is left DISABLED while the values are neutral. An enabled adjust filter is
        /// an extra pass over every frame of every tile, and paying for it to change nothing is
        /// exactly the cost that matters when a dozen streams are decoding at once.
        /// </summary>
        public void ApplyTo(MediaPlayer? player)
        {
            if (player == null) return;

            try
            {
                if (IsNeutral)
                {
                    player.SetAdjustInt(VideoAdjustOption.Enable, 0);
                    return;
                }

                player.SetAdjustInt(VideoAdjustOption.Enable, 1);
                player.SetAdjustFloat(VideoAdjustOption.Brightness, Brightness);
                player.SetAdjustFloat(VideoAdjustOption.Contrast, Contrast);
                player.SetAdjustFloat(VideoAdjustOption.Saturation, Saturation);
                player.SetAdjustFloat(VideoAdjustOption.Hue, Hue);
                player.SetAdjustFloat(VideoAdjustOption.Gamma, Gamma);
            }
            catch
            {
                // A player torn down between the null check and the call. Nothing to repair —
                // the next StartPlayback re-applies from the camera's saved values.
            }
        }

        /// <summary>Human-readable value for the slider read-out, e.g. "1.20" or "180°".</summary>
        public static string Format(Knob knob, float value) =>
            knob == Knob.Hue ? $"{value:0}°" : $"{value:0.00}";
    }
}
