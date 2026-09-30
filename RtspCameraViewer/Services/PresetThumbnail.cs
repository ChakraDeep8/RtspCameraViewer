using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RtspCameraViewer.Services
{
    /// <summary>
    /// Draws the little preview square shown on each preset button.
    ///
    /// The preview is generated, not shipped as artwork: a small synthetic scene is rendered once
    /// and each preset's effect is applied to a copy of it in managed code. That keeps the
    /// thumbnails honest — the Sepia square really is sepia-toned pixels, Posterize really is
    /// banded — without asking VLC to decode anything, and without a folder of PNGs that would
    /// drift out of step the moment a preset's parameters change.
    ///
    /// The maths here APPROXIMATES what VLC's filters do; it is a label, not a simulation. The
    /// real answer for any preset is the live feed a click away.
    /// </summary>
    public static class PresetThumbnail
    {
        private const int Width = 68;
        private const int Height = 44;

        private static byte[]? _scene;

        /// <summary>
        /// A synthetic "scene" with the features that make filters legible: a bright sky gradient,
        /// a saturated colour block, skin-ish midtones, a hard edge and a dark corner.
        /// </summary>
        private static byte[] Scene()
        {
            if (_scene != null) return _scene;

            var px = new byte[Width * Height * 4]; // BGRA
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    double fx = x / (double)Width, fy = y / (double)Height;
                    double r, g, b;

                    if (fy < 0.42)
                    {
                        // Sky: pale blue falling to warm near the horizon.
                        double t = fy / 0.42;
                        r = Lerp(120, 225, t); g = Lerp(170, 205, t); b = Lerp(235, 170, t);
                    }
                    else if (fx < 0.34)
                    {
                        // A strongly saturated block, so saturation changes are obvious.
                        r = 200; g = 45; b = 55;
                    }
                    else if (fx < 0.67)
                    {
                        // Midtones with a vertical ramp — where contrast and gamma show.
                        double t = (fy - 0.42) / 0.58;
                        r = Lerp(215, 70, t); g = Lerp(180, 55, t); b = Lerp(150, 45, t);
                    }
                    else
                    {
                        // Cool green-blue, plus a hard vertical edge for the edge filters.
                        bool stripe = ((int)(fx * 22) % 2) == 0;
                        r = stripe ? 40 : 90; g = stripe ? 120 : 165; b = stripe ? 110 : 150;
                    }

                    int i = (y * Width + x) * 4;
                    px[i + 0] = Clamp(b);
                    px[i + 1] = Clamp(g);
                    px[i + 2] = Clamp(r);
                    px[i + 3] = 255;
                }
            }
            return _scene = px;
        }

        public static BitmapSource Render(FilterPreset preset)
        {
            var px = (byte[])Scene().Clone();

            // The adjust-filter half of the preset is real maths, the same knobs the sliders drive.
            ApplyAdjust(px, preset.ToAdjustments());

            // The video-filter half is approximated per module.
            switch (preset.Id)
            {
                case "invert": Invert(px); break;
                case "blur": Blur(px, 2); break;
                case "denoise": Blur(px, 1); break;
                case "edges": Edges(px, invertOutput: false); break;
                case "pencil": Edges(px, invertOutput: true); break;
                case "sepia": Sepia(px); break;
                case "vintage": Sepia(px); Grain(px, 18); Darken(px, 0.88); break;
                case "posterize": Posterize(px, 4); break;
                case "cartoon": Posterize(px, 3); Edges(px, invertOutput: false, overlay: true); break;
                case "sharpen": Sharpen(px); break;
                case "grain": Grain(px, 26); break;
            }

            var bmp = new WriteableBitmap(Width, Height, 96, 96, PixelFormats.Bgra32, null);
            bmp.WritePixels(new System.Windows.Int32Rect(0, 0, Width, Height), px, Width * 4, 0);
            bmp.Freeze();
            return bmp;
        }

        // ------------------------------------------------------------------ effects

        private static void ApplyAdjust(byte[] px, VideoAdjustments a)
        {
            if (a.IsNeutral) return;

            double hueRad = a.Hue * Math.PI / 180.0;
            double cosH = Math.Cos(hueRad), sinH = Math.Sin(hueRad);

            for (int i = 0; i < px.Length; i += 4)
            {
                double b = px[i] / 255.0, g = px[i + 1] / 255.0, r = px[i + 2] / 255.0;

                // Gamma, then brightness, then contrast around mid-grey — the order VLC uses.
                if (Math.Abs(a.Gamma - 1f) > 0.001f)
                {
                    double inv = 1.0 / a.Gamma;
                    r = Math.Pow(r, inv); g = Math.Pow(g, inv); b = Math.Pow(b, inv);
                }
                r *= a.Brightness; g *= a.Brightness; b *= a.Brightness;
                r = (r - 0.5) * a.Contrast + 0.5;
                g = (g - 0.5) * a.Contrast + 0.5;
                b = (b - 0.5) * a.Contrast + 0.5;

                // Saturation and hue in YUV, which is where the adjust filter works.
                double yy = 0.299 * r + 0.587 * g + 0.114 * b;
                double u = (b - yy) * 0.565, v = (r - yy) * 0.713;
                u *= a.Saturation; v *= a.Saturation;
                double u2 = u * cosH - v * sinH;
                double v2 = u * sinH + v * cosH;
                r = yy + v2 / 0.713;
                b = yy + u2 / 0.565;
                g = (yy - 0.299 * r - 0.114 * b) / 0.587;

                px[i] = Clamp(b * 255); px[i + 1] = Clamp(g * 255); px[i + 2] = Clamp(r * 255);
            }
        }

        private static void Invert(byte[] px)
        {
            for (int i = 0; i < px.Length; i += 4)
            {
                px[i] = (byte)(255 - px[i]);
                px[i + 1] = (byte)(255 - px[i + 1]);
                px[i + 2] = (byte)(255 - px[i + 2]);
            }
        }

        private static void Sepia(byte[] px)
        {
            for (int i = 0; i < px.Length; i += 4)
            {
                double b = px[i], g = px[i + 1], r = px[i + 2];
                px[i + 2] = Clamp(0.393 * r + 0.769 * g + 0.189 * b);
                px[i + 1] = Clamp(0.349 * r + 0.686 * g + 0.168 * b);
                px[i + 0] = Clamp(0.272 * r + 0.534 * g + 0.131 * b);
            }
        }

        private static void Posterize(byte[] px, int levels)
        {
            double step = 255.0 / (levels - 1);
            for (int i = 0; i < px.Length; i += 4)
            {
                for (int c = 0; c < 3; c++)
                    px[i + c] = Clamp(Math.Round(px[i + c] / step) * step);
            }
        }

        private static void Darken(byte[] px, double factor)
        {
            for (int i = 0; i < px.Length; i += 4)
            {
                for (int c = 0; c < 3; c++) px[i + c] = Clamp(px[i + c] * factor);
            }
        }

        private static void Grain(byte[] px, int amount)
        {
            // Deterministic, so a thumbnail does not shimmer when the panel is reopened.
            var rng = new Random(20260921);
            for (int i = 0; i < px.Length; i += 4)
            {
                int n = rng.Next(-amount, amount + 1);
                for (int c = 0; c < 3; c++) px[i + c] = Clamp(px[i + c] + n);
            }
        }

        private static void Blur(byte[] px, int radius)
        {
            var src = (byte[])px.Clone();
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    int rs = 0, gs = 0, bs = 0, n = 0;
                    for (int dy = -radius; dy <= radius; dy++)
                    {
                        int yy = y + dy; if (yy < 0 || yy >= Height) continue;
                        for (int dx = -radius; dx <= radius; dx++)
                        {
                            int xx = x + dx; if (xx < 0 || xx >= Width) continue;
                            int j = (yy * Width + xx) * 4;
                            bs += src[j]; gs += src[j + 1]; rs += src[j + 2]; n++;
                        }
                    }
                    int i = (y * Width + x) * 4;
                    px[i] = (byte)(bs / n); px[i + 1] = (byte)(gs / n); px[i + 2] = (byte)(rs / n);
                }
            }
        }

        private static void Sharpen(byte[] px)
        {
            var src = (byte[])px.Clone();
            for (int y = 1; y < Height - 1; y++)
            {
                for (int x = 1; x < Width - 1; x++)
                {
                    int i = (y * Width + x) * 4;
                    for (int c = 0; c < 3; c++)
                    {
                        int centre = src[i + c];
                        int sum = 5 * centre
                                  - src[((y - 1) * Width + x) * 4 + c]
                                  - src[((y + 1) * Width + x) * 4 + c]
                                  - src[(y * Width + x - 1) * 4 + c]
                                  - src[(y * Width + x + 1) * 4 + c];
                        px[i + c] = Clamp(sum);
                    }
                }
            }
        }

        /// <summary>Sobel magnitude. <paramref name="overlay"/> draws edges onto the existing
        /// picture (cartoon) instead of replacing it.</summary>
        private static void Edges(byte[] px, bool invertOutput, bool overlay = false)
        {
            var src = (byte[])px.Clone();
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    int i = (y * Width + x) * 4;
                    if (x == 0 || y == 0 || x == Width - 1 || y == Height - 1)
                    {
                        if (!overlay) { px[i] = px[i + 1] = px[i + 2] = invertOutput ? (byte)255 : (byte)0; }
                        continue;
                    }

                    double gx = Luma(src, x + 1, y) - Luma(src, x - 1, y);
                    double gy = Luma(src, x, y + 1) - Luma(src, x, y - 1);
                    double mag = Math.Sqrt(gx * gx + gy * gy);

                    if (overlay)
                    {
                        if (mag > 45)
                            px[i] = px[i + 1] = px[i + 2] = 20; // ink the outline
                    }
                    else
                    {
                        byte v = Clamp(invertOutput ? 255 - mag * 1.6 : mag * 1.6);
                        px[i] = px[i + 1] = px[i + 2] = v;
                    }
                }
            }
        }

        private static double Luma(byte[] px, int x, int y)
        {
            int i = (y * Width + x) * 4;
            return 0.299 * px[i + 2] + 0.587 * px[i + 1] + 0.114 * px[i];
        }

        private static double Lerp(double a, double b, double t) => a + (b - a) * t;

        private static byte Clamp(double v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
    }
}
