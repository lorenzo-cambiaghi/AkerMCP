using System.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace AkerMcp.Server
{
    /// <summary>
    /// A normalized image and the two sizes that matter about it: what came in and what
    /// goes out. The difference between them is the scale a caller has to undo before a
    /// coordinate read off the delivered image means anything on screen.
    /// </summary>
    public readonly struct NormalizedImage
    {
        public NormalizedImage(byte[] bytes, int sourceWidth, int sourceHeight, int width, int height)
        {
            Bytes = bytes;
            SourceWidth = sourceWidth;
            SourceHeight = sourceHeight;
            Width = width;
            Height = height;
        }

        public byte[] Bytes { get; }
        public int SourceWidth { get; }
        public int SourceHeight { get; }
        public int Width { get; }
        public int Height { get; }
    }

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
            => Normalize(sourceBytes, maxDimension, quality).Bytes;

        /// <summary>
        /// The same normalization, reporting the sizes as well. Callers that hand the image
        /// to a model which will point at it need them: the resize happens here, so this is
        /// the only place that knows both numbers.
        /// </summary>
        public static NormalizedImage Normalize(byte[] sourceBytes,
            int maxDimension = DefaultMaxDimension, int quality = DefaultJpegQuality)
        {
            using var image = Image.Load(sourceBytes);
            int srcW = image.Width, srcH = image.Height;

            var (w, h) = FitWithin(srcW, srcH, maxDimension);
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
            return new NormalizedImage(outStream.ToArray(), srcW, srcH, image.Width, image.Height);
        }

        private static (int w, int h) FitWithin(int srcW, int srcH, int max)
        {
            if (srcW <= max && srcH <= max) return (srcW, srcH);
            double scale = (double)max / System.Math.Max(srcW, srcH);
            return ((int)(srcW * scale), (int)(srcH * scale));
        }
    }
}
