using System.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace AkerMcp.Server
{
    public static class ImageProcessor
    {
        public const int DefaultMaxDimension = 1920;
        public const int DefaultJpegQuality = 85;

        /// <summary>
        /// Decode any image bytes (PNG/JPEG), resize to fit maxDimension on the longest
        /// side, re-encode as JPEG. Single chokepoint for output normalization.
        /// Cross-platform: pure-managed via ImageSharp.
        /// </summary>
        public static byte[] NormalizeToJpeg(byte[] sourceBytes,
            int maxDimension = DefaultMaxDimension, int quality = DefaultJpegQuality)
        {
            using var image = Image.Load(sourceBytes);

            var (w, h) = FitWithin(image.Width, image.Height, maxDimension);
            if (w != image.Width || h != image.Height)
            {
                image.Mutate(x => x.Resize(new ResizeOptions
                {
                    Size = new Size(w, h),
                    Mode = ResizeMode.Stretch,
                    Sampler = KnownResamplers.Lanczos3,
                }));
            }

            using var outStream = new MemoryStream();
            image.SaveAsJpeg(outStream, new JpegEncoder { Quality = quality });
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
