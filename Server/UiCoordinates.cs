using System;

namespace AkerMcp.Server
{
    /// <summary>
    /// What ties an image the model has seen to the screen it came from, so a click can be
    /// expressed in the pixels the model read off that image.
    ///
    /// Every OS-window capture records one: where the window's client area sits on the
    /// virtual screen, how large it really was, and how large the JPEG that left the server
    /// is — <see cref="ImageProcessor"/> scales anything over 1920 px down. Without those
    /// three numbers a model looking at a screenshot has to guess both the scale and the
    /// window origin before it can name a point, and a click then lands tens of pixels off
    /// with nothing in the result to say that it did.
    /// </summary>
    public sealed class CaptureAnchor
    {
        /// <summary>The substring the caller searched for.</summary>
        public string TitleQuery { get; set; } = "";
        /// <summary>The full title of the window that matched.</summary>
        public string WindowTitle { get; set; } = "";

        /// <summary>Client-area origin in virtual-screen pixels (may be negative on a left monitor).</summary>
        public int OriginX { get; set; }
        public int OriginY { get; set; }

        /// <summary>Client-area size in real pixels: what was captured before normalization.</summary>
        public int SourceWidth { get; set; }
        public int SourceHeight { get; set; }

        /// <summary>Size of the image actually handed to the model.</summary>
        public int ImageWidth { get; set; }
        public int ImageHeight { get; set; }

        public DateTime CapturedAtUtc { get; set; } = DateTime.UtcNow;

        public double ScaleX => ImageWidth <= 0 ? 1.0 : (double)SourceWidth / ImageWidth;
        public double ScaleY => ImageHeight <= 0 ? 1.0 : (double)SourceHeight / ImageHeight;

        private bool IsUnscaled => Math.Abs(ScaleX - 1.0) < 0.001 && Math.Abs(ScaleY - 1.0) < 0.001;

        /// <summary>
        /// Image pixel to virtual-screen pixel. Maps pixel centres, so an unscaled capture
        /// converts to the origin plus the coordinate exactly, instead of drifting by half a
        /// pixel and rounding the wrong way at the edges.
        /// </summary>
        public (int x, int y) ToScreen(double imageX, double imageY)
        {
            var sx = OriginX + ((imageX + 0.5) * ScaleX) - 0.5;
            var sy = OriginY + ((imageY + 0.5) * ScaleY) - 0.5;
            return ((int)Math.Round(sx, MidpointRounding.AwayFromZero),
                    (int)Math.Round(sy, MidpointRounding.AwayFromZero));
        }

        /// <summary>True when the point is inside the image the model was shown.</summary>
        public bool Contains(double imageX, double imageY)
            => imageX >= 0 && imageY >= 0 && imageX < ImageWidth && imageY < ImageHeight;

        /// <summary>
        /// The same image, at the window's new position. A window that was dragged between
        /// the capture and the click still shows what the screenshot shows — only its origin
        /// moved — so the anchor follows it instead of being thrown away.
        /// </summary>
        public CaptureAnchor WithOrigin(int originX, int originY) => new CaptureAnchor
        {
            TitleQuery = TitleQuery,
            WindowTitle = WindowTitle,
            OriginX = originX,
            OriginY = originY,
            SourceWidth = SourceWidth,
            SourceHeight = SourceHeight,
            ImageWidth = ImageWidth,
            ImageHeight = ImageHeight,
            CapturedAtUtc = CapturedAtUtc,
        };

        /// <summary>
        /// The line that travels with the image. It states what the pixels mean, because a
        /// model that is about to name a coordinate has no other way to know the picture it
        /// is looking at was shrunk.
        /// </summary>
        public string Describe()
        {
            var scale = IsUnscaled
                ? "1:1 with the window"
                : $"scaled {ScaleX:0.###}x from {SourceWidth}x{SourceHeight}";
            return $"Window '{WindowTitle}' | image {ImageWidth}x{ImageHeight} px, {scale} | " +
                   $"origin on screen {OriginX},{OriginY}. " +
                   "Coordinates in this image are what ui_action takes.";
        }
    }
}
