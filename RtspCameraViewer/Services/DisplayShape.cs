namespace RtspCameraViewer.Services
{
    /// <summary>How to make a stream fill a shape it was not encoded at.</summary>
    public enum DisplayFit
    {
        /// <summary>Distort the picture to exactly fill the requested shape.</summary>
        Stretch = 0,

        /// <summary>Cut the edges off so what remains has the requested shape, undistorted.</summary>
        Crop = 1
    }

    /// <summary>
    /// A shape offered in Settings for displaying a stream.
    ///
    /// Worth being precise about what this can and cannot do. The camera decides the encoded
    /// resolution; nothing here changes that, and the number of pixels actually drawn is decided
    /// by the tile's size on screen. What this controls is the picture's SHAPE — which matters
    /// when a stream's own shape does not suit the grid, for instance a portrait-mounted camera
    /// sending 960x1080 into a landscape tile.
    ///
    /// A true 1280x720 stream has to come from the DVR's own encoder settings.
    /// </summary>
    public class DisplayShape
    {
        public string Label { get; init; } = "";

        /// <summary>"W:H" passed to the player, or null for "leave the stream alone".</summary>
        public string? Aspect { get; init; }

        public static readonly DisplayShape Native = new() { Label = "Native (as the camera sends it)", Aspect = null };

        public static readonly DisplayShape[] All =
        {
            Native,
            new() { Label = "1080 x 720", Aspect = "1080:720" },
            new() { Label = "1280 x 720 (720p)", Aspect = "1280:720" },
            new() { Label = "1920 x 1080 (1080p)", Aspect = "1920:1080" },
            new() { Label = "960 x 540", Aspect = "960:540" },
            new() { Label = "4:3", Aspect = "4:3" },
        };

        public override string ToString() => Label;
    }
}
