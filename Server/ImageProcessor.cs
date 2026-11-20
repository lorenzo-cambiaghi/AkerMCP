using System;
using System.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
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
            using var image = Decode(sourceBytes);
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

        /// <summary>
        /// Only the two formats a capture ever arrives in. Every path that feeds this produces
        /// PNG — the Windows and macOS captures, Unity's EncodeToPNG, Godot's SavePngToBuffer,
        /// Stride's back-buffer save — and IScreenCapture lets a third-party adapter answer
        /// JPEG. Image.Load's default configuration also carries the TIFF, BMP, GIF, WebP, TGA,
        /// PBM and QOI decoders, and ImageSharp 3.1 has published advisories against its TIFF
        /// decoder (a heap write out of bounds on tiled fax data, an endless BigTIFF loop); 3.2
        /// fixes them but needs a licence key to build. A decoder that is not registered cannot
        /// be reached, whatever the bytes claim to be.
        ///
        /// <para>Metadata is skipped as well, and the ICC profile dropped by hand. The remaining
        /// ICC advisory (a CLUT sized from the profile before it is checked) is reached in 3.1
        /// only when a profile's entries are parsed, which nothing here does: the JPEG encoder
        /// copies the profile's raw bytes. But it did copy them — 3.1.12's PNG decoder reads
        /// iCCP whatever SkipMetadata says (measured), so a capture's profile travelled into the
        /// JPEG handed to the client, for whatever parses it there. It costs no pixel: 3.1 does
        /// not colour-convert on decode (measured identical), and the JPEG is for a model to
        /// look at, not to colour-manage.</para>
        /// </summary>
        private static readonly DecoderOptions CaptureDecoding = new DecoderOptions
        {
            Configuration = new Configuration(new PngConfigurationModule(), new JpegConfigurationModule()),
            SkipMetadata = true,
        };

        private static Image Decode(byte[] sourceBytes)
        {
            try
            {
                var image = Image.Load(CaptureDecoding, sourceBytes);
                image.Metadata.IccProfile = null;
                return image;
            }
            catch (UnknownImageFormatException)
            {
                var head = Convert.ToHexString(sourceBytes, 0, Math.Min(8, sourceBytes.Length));
                throw new NotSupportedException(
                    $"The image is neither PNG nor JPEG (it starts with 0x{head}); captures are decoded " +
                    "only in those two formats.");
            }
        }

        private static (int w, int h) FitWithin(int srcW, int srcH, int max)
        {
            if (srcW <= max && srcH <= max) return (srcW, srcH);
            double scale = (double)max / System.Math.Max(srcW, srcH);
            return ((int)(srcW * scale), (int)(srcH * scale));
        }
    }
}
