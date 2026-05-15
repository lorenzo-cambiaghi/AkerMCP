using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;

namespace AkerMcp.Server
{
    [SupportedOSPlatform("windows")]
    public static class ImageProcessor
    {
        public const int DefaultMaxDimension = 1920;
        public const int DefaultJpegQuality = 85;

        /// <summary>
        /// Decode any image bytes (PNG/JPEG), resize to fit maxDimension on the longest
        /// side, re-encode as JPEG. Single chokepoint for output normalization.
        /// </summary>
        public static byte[] NormalizeToJpeg(byte[] sourceBytes,
            int maxDimension = DefaultMaxDimension, int quality = DefaultJpegQuality)
        {
            using var srcStream = new MemoryStream(sourceBytes);
            using var src = Image.FromStream(srcStream);

            var (w, h) = FitWithin(src.Width, src.Height, maxDimension);

            using var resized = new Bitmap(w, h);
            using (var gfx = Graphics.FromImage(resized))
            {
                gfx.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                gfx.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                gfx.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                gfx.DrawImage(src, 0, 0, w, h);
            }

            var jpegCodec = ImageCodecInfo.GetImageEncoders()
                .First(c => c.FormatID == ImageFormat.Jpeg.Guid);
            var encParams = new EncoderParameters(1);
            encParams.Param[0] = new EncoderParameter(Encoder.Quality, (long)quality);

            using var outStream = new MemoryStream();
            resized.Save(outStream, jpegCodec, encParams);
            return outStream.ToArray();
        }

        private static (int w, int h) FitWithin(int srcW, int srcH, int max)
        {
            if (srcW <= max && srcH <= max) return (srcW, srcH);
            double scale = (double)max / System.Math.Max(srcW, srcH);
            return ((int)(srcW * scale), (int)(srcH * scale));
        }
    }
}
