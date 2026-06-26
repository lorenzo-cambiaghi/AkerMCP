using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text.Json;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace AkerMcp.Server
{
    /// <summary>
    /// Renders a flat-geometric "shape-spec" (emitted by the AI) into a PNG sprite
    /// with transparency, entirely server-side and pure-managed (ImageSharp.Drawing).
    /// This is engine-agnostic: the engine receives a ready raster, so it never needs
    /// SVG/vector support of its own.
    ///
    /// Distinct from <see cref="ImageProcessor"/>: that one normalizes screenshots to
    /// JPEG (no alpha). Sprites REQUIRE alpha, so this always outputs RGBA PNG.
    ///
    /// Shape-spec (JSON):
    /// {
    ///   "width": 64, "height": 64,          // logical coordinate space (defaults to target px)
    ///   "background": "#rrggbb" | null,      // null/absent = transparent
    ///   "shapes": [                           // drawn in order (painter's algorithm)
    ///     { "type":"ellipse", "cx":32,"cy":32,"rx":20,"ry":18, "fill":"#FFCC00", "stroke":"#222","strokeWidth":2 },
    ///     { "type":"rect", "x":4,"y":4,"w":56,"h":56,"rx":8, "fill": <paint> },
    ///     { "type":"polygon", "points":[[x,y],...], "fill":"#FF8800" },
    ///     { "type":"line"|"polyline", "points":[[x,y],...], "stroke":"#000","strokeWidth":3 },
    ///     { "type":"path", "d":"M0 0 L10 10 Q.. C..  Z", "fill":"#000" }
    ///   ]
    /// }
    /// A <paint> is either a hex string or a linear gradient:
    ///   { "gradient":"linear", "x1":0,"y1":0,"x2":0,"y2":64,
    ///     "stops":[{"offset":0,"color":"#fff"},{"offset":1,"color":"#888"}] }
    /// Optional per-shape "opacity" (0..1) multiplies the paint alpha.
    /// </summary>
    public static class SpriteRasterizer
    {
        public const int DefaultSupersample = 4;

        public static byte[] RenderToPng(JsonElement spec, int targetWidth, int targetHeight,
            int supersample = DefaultSupersample)
        {
            if (targetWidth <= 0 || targetHeight <= 0)
                throw new ArgumentException("Target width/height must be positive.");
            if (targetWidth > 4096 || targetHeight > 4096)
                throw new ArgumentException("Target width/height must be <= 4096.");

            supersample = Math.Clamp(supersample, 1, 4);

            // Guard against OOM: a large target × supersample can blow up the render
            // buffer (4096 × 4 = 16384² ≈ 4 GB). Drop the SSAA factor for big targets so
            // the render canvas never exceeds ~4096 on a side.
            const int maxRenderDimension = 4096;
            while (supersample > 1 &&
                   ((long)targetWidth * supersample > maxRenderDimension ||
                    (long)targetHeight * supersample > maxRenderDimension))
                supersample--;

            // Logical coordinate space the AI authored in (defaults to the target size).
            double specW = GetDouble(spec, "width", targetWidth);
            double specH = GetDouble(spec, "height", targetHeight);
            if (specW <= 0 || specH <= 0)
                throw new ArgumentException("Spec width/height must be positive.");

            int renderW = targetWidth * supersample;
            int renderH = targetHeight * supersample;

            // spec coords -> render pixels
            float sx = (float)(renderW / specW);
            float sy = (float)(renderH / specH);
            var transform = Matrix3x2.CreateScale(sx, sy);
            float strokeScale = (sx + sy) / 2f;

            using var image = new Image<Rgba32>(renderW, renderH, Color.Transparent);

            // Antialiasing is on by default; SSAA on top gives crisp vector-like edges.
            var drawOpts = new DrawingOptions
            {
                GraphicsOptions = new GraphicsOptions { Antialias = true }
            };

            image.Mutate(ctx =>
            {
                if (TryGetString(spec, "background", out var bg) && !string.IsNullOrWhiteSpace(bg))
                    ctx.Fill(ParseColor(bg, 1.0));

                if (!spec.TryGetProperty("shapes", out var shapes) || shapes.ValueKind != JsonValueKind.Array)
                    return;

                foreach (var shape in shapes.EnumerateArray())
                    DrawShape(ctx, drawOpts, shape, transform, strokeScale);
            });

            if (supersample > 1)
            {
                image.Mutate(ctx => ctx.Resize(new ResizeOptions
                {
                    Size = new Size(targetWidth, targetHeight),
                    Mode = ResizeMode.Stretch,
                    Sampler = KnownResamplers.Lanczos3,
                }));
            }

            using var outStream = new MemoryStream();
            image.SaveAsPng(outStream, new PngEncoder
            {
                ColorType = PngColorType.RgbWithAlpha,
                BitDepth = PngBitDepth.Bit8,
            });
            return outStream.ToArray();
        }

        private static void DrawShape(IImageProcessingContext ctx, DrawingOptions opts,
            JsonElement shape, Matrix3x2 transform, float strokeScale)
        {
            var type = TryGetString(shape, "type", out var t) ? t!.ToLowerInvariant() : "";
            double opacity = GetDouble(shape, "opacity", 1.0);

            IPath? path = type switch
            {
                "ellipse" or "circle" => BuildEllipse(shape),
                "rect" or "rectangle" => BuildRect(shape),
                "polygon" => BuildPolygon(shape, closed: true),
                "polyline" or "line" => BuildPolygon(shape, closed: false),
                "path" => BuildPath(shape),
                _ => null
            };
            if (path == null) return;

            path = path.Transform(transform);

            // Fill (skip for open lines/polylines unless an explicit fill is given).
            bool isOpen = type is "polyline" or "line";
            if (shape.TryGetProperty("fill", out var fill) && fill.ValueKind != JsonValueKind.Null)
            {
                var brush = ParseBrush(fill, opacity, transform);
                if (brush != null)
                    ctx.Fill(opts, brush, path);
            }
            else if (!isOpen && type != "path")
            {
                // Default solid black fill keeps simple specs (just geometry) visible.
                // Paths and open lines must opt in via "fill"/"stroke".
            }

            // Stroke.
            if (TryGetString(shape, "stroke", out var strokeHex) && !string.IsNullOrWhiteSpace(strokeHex))
            {
                double w = GetDouble(shape, "strokeWidth", 1.0) * strokeScale;
                if (w > 0)
                    ctx.Draw(opts, Pens.Solid(ParseColor(strokeHex!, opacity), (float)w), path);
            }
        }

        private static IPath BuildEllipse(JsonElement s)
        {
            float cx = (float)GetDouble(s, "cx", 0);
            float cy = (float)GetDouble(s, "cy", 0);
            float rx = (float)GetDouble(s, "rx", GetDouble(s, "r", 0));
            float ry = (float)GetDouble(s, "ry", GetDouble(s, "r", 0));
            return new EllipsePolygon(cx, cy, rx, ry);
        }

        private static IPath BuildRect(JsonElement s)
        {
            float x = (float)GetDouble(s, "x", 0);
            float y = (float)GetDouble(s, "y", 0);
            float w = (float)GetDouble(s, "w", GetDouble(s, "width", 0));
            float h = (float)GetDouble(s, "h", GetDouble(s, "height", 0));
            float r = (float)GetDouble(s, "rx", GetDouble(s, "r", 0));

            if (r <= 0)
                return new RectangularPolygon(x, y, w, h);

            r = Math.Min(r, Math.Min(w, h) / 2f);
            return BuildRoundedRect(x, y, w, h, r);
        }

        // ImageSharp.Drawing has no rounded-rect primitive; compose one from 4 corner
        // arcs + 4 edges via a PathBuilder.
        private static IPath BuildRoundedRect(float x, float y, float w, float h, float r)
        {
            var pb = new PathBuilder();
            pb.StartFigure();
            pb.AddLine(new PointF(x + r, y), new PointF(x + w - r, y));
            pb.AddArc(new PointF(x + w - r, y + r), r, r, 0, 270, 90);
            pb.AddLine(new PointF(x + w, y + r), new PointF(x + w, y + h - r));
            pb.AddArc(new PointF(x + w - r, y + h - r), r, r, 0, 0, 90);
            pb.AddLine(new PointF(x + w - r, y + h), new PointF(x + r, y + h));
            pb.AddArc(new PointF(x + r, y + h - r), r, r, 0, 90, 90);
            pb.AddLine(new PointF(x, y + h - r), new PointF(x, y + r));
            pb.AddArc(new PointF(x + r, y + r), r, r, 0, 180, 90);
            pb.CloseFigure();
            return pb.Build();
        }

        private static IPath? BuildPolygon(JsonElement s, bool closed)
        {
            var pts = ParsePoints(s);
            if (pts.Count < 2) return null;
            if (closed)
                return new Polygon(new LinearLineSegment(pts.ToArray()));

            var pb = new PathBuilder();
            pb.AddLines(pts.ToArray());
            return pb.Build();
        }

        private static List<PointF> ParsePoints(JsonElement s)
        {
            var result = new List<PointF>();
            if (!s.TryGetProperty("points", out var pts) || pts.ValueKind != JsonValueKind.Array)
                return result;

            foreach (var p in pts.EnumerateArray())
            {
                if (p.ValueKind == JsonValueKind.Array)
                {
                    var arr = p.EnumerateArray();
                    float px = 0, py = 0; int i = 0;
                    foreach (var c in arr)
                    {
                        if (i == 0) px = (float)c.GetDouble();
                        else if (i == 1) py = (float)c.GetDouble();
                        i++;
                    }
                    result.Add(new PointF(px, py));
                }
                else if (p.ValueKind == JsonValueKind.Object)
                {
                    result.Add(new PointF((float)GetDouble(p, "x", 0), (float)GetDouble(p, "y", 0)));
                }
            }
            return result;
        }

        // Minimal SVG path-data subset: M/m L/l H/h V/v C/c Q/q Z/z.
        // Returns null on malformed input rather than throwing, so a bad spec degrades
        // to "nothing drawn" instead of failing the whole sprite.
        private static IPath? BuildPath(JsonElement s)
        {
            if (!TryGetString(s, "d", out var d) || string.IsNullOrWhiteSpace(d))
                return null;
            return BuildPathStructured(d!);
        }

        private static IPath? BuildPathStructured(string d)
        {
            try
            {
                var pb = new PathBuilder();
                var reader = new PathReader(d);
                PointF cur = PointF.Empty;
                PointF startPt = PointF.Empty;
                bool open = false;
                char cmd;

                while (reader.TryReadCommand(out cmd))
                {
                    bool rel = char.IsLower(cmd);
                    switch (char.ToUpperInvariant(cmd))
                    {
                        case 'M':
                            {
                                var p = reader.ReadPoint();
                                cur = rel ? new PointF(cur.X + p.X, cur.Y + p.Y) : p;
                                pb.StartFigure(); open = true; startPt = cur;
                                // Subsequent implicit pairs are treated as L.
                                while (reader.HasNumber)
                                {
                                    var p2 = reader.ReadPoint();
                                    var np = rel ? new PointF(cur.X + p2.X, cur.Y + p2.Y) : p2;
                                    pb.AddLine(cur, np); cur = np;
                                }
                                break;
                            }
                        case 'L':
                            while (reader.HasNumber)
                            {
                                var p = reader.ReadPoint();
                                var np = rel ? new PointF(cur.X + p.X, cur.Y + p.Y) : p;
                                if (!open) { pb.StartFigure(); open = true; startPt = cur; }
                                pb.AddLine(cur, np); cur = np;
                            }
                            break;
                        case 'H':
                            while (reader.HasNumber)
                            {
                                float x = reader.ReadNumber();
                                var np = new PointF(rel ? cur.X + x : x, cur.Y);
                                pb.AddLine(cur, np); cur = np;
                            }
                            break;
                        case 'V':
                            while (reader.HasNumber)
                            {
                                float y = reader.ReadNumber();
                                var np = new PointF(cur.X, rel ? cur.Y + y : y);
                                pb.AddLine(cur, np); cur = np;
                            }
                            break;
                        case 'C':
                            while (reader.HasNumber)
                            {
                                var c1 = reader.ReadPoint(); var c2 = reader.ReadPoint(); var e = reader.ReadPoint();
                                var pc1 = rel ? new PointF(cur.X + c1.X, cur.Y + c1.Y) : c1;
                                var pc2 = rel ? new PointF(cur.X + c2.X, cur.Y + c2.Y) : c2;
                                var pe = rel ? new PointF(cur.X + e.X, cur.Y + e.Y) : e;
                                pb.AddCubicBezier(cur, pc1, pc2, pe); cur = pe;
                            }
                            break;
                        case 'Q':
                            while (reader.HasNumber)
                            {
                                var c = reader.ReadPoint(); var e = reader.ReadPoint();
                                var pc = rel ? new PointF(cur.X + c.X, cur.Y + c.Y) : c;
                                var pe = rel ? new PointF(cur.X + e.X, cur.Y + e.Y) : e;
                                pb.AddQuadraticBezier(cur, pc, pe); cur = pe;
                            }
                            break;
                        case 'Z':
                            if (open) { pb.CloseFigure(); open = false; cur = startPt; }
                            break;
                        default:
                            return null; // unsupported command
                    }
                }
                return pb.Build();
            }
            catch
            {
                return null;
            }
        }

        // Lightweight number/command reader for SVG path data.
        private sealed class PathReader
        {
            private readonly string _s;
            private int _i;
            public PathReader(string s) { _s = s; _i = 0; }

            private void SkipSep()
            {
                while (_i < _s.Length && (char.IsWhiteSpace(_s[_i]) || _s[_i] == ',')) _i++;
            }

            public bool TryReadCommand(out char cmd)
            {
                SkipSep();
                if (_i < _s.Length && char.IsLetter(_s[_i])) { cmd = _s[_i++]; return true; }
                cmd = '\0';
                return false;
            }

            public bool HasNumber
            {
                get
                {
                    SkipSep();
                    return _i < _s.Length && (char.IsDigit(_s[_i]) || _s[_i] == '-' || _s[_i] == '+' || _s[_i] == '.');
                }
            }

            public float ReadNumber()
            {
                SkipSep();
                int start = _i;
                if (_i < _s.Length && (_s[_i] == '-' || _s[_i] == '+')) _i++;
                while (_i < _s.Length && (char.IsDigit(_s[_i]) || _s[_i] == '.' || _s[_i] == 'e' || _s[_i] == 'E'
                       || ((_s[_i] == '-' || _s[_i] == '+') && (_s[_i - 1] == 'e' || _s[_i - 1] == 'E'))))
                    _i++;
                return float.Parse(_s.Substring(start, _i - start), CultureInfo.InvariantCulture);
            }

            public PointF ReadPoint() => new PointF(ReadNumber(), ReadNumber());
        }

        // ---- paint / color helpers ----

        private static Brush? ParseBrush(JsonElement paint, double opacity, Matrix3x2 transform)
        {
            if (paint.ValueKind == JsonValueKind.String)
                return new SolidBrush(ParseColor(paint.GetString()!, opacity));

            if (paint.ValueKind == JsonValueKind.Object)
            {
                var kind = TryGetString(paint, "gradient", out var g) ? g!.ToLowerInvariant() : "linear";
                if (kind == "linear")
                {
                    var p1 = Vector2.Transform(new Vector2(
                        (float)GetDouble(paint, "x1", 0), (float)GetDouble(paint, "y1", 0)), transform);
                    var p2 = Vector2.Transform(new Vector2(
                        (float)GetDouble(paint, "x2", 0), (float)GetDouble(paint, "y2", 0)), transform);

                    var stops = new List<ColorStop>();
                    if (paint.TryGetProperty("stops", out var arr) && arr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var st in arr.EnumerateArray())
                        {
                            float off = (float)GetDouble(st, "offset", 0);
                            var col = TryGetString(st, "color", out var c) ? ParseColor(c!, opacity) : Color.Black;
                            stops.Add(new ColorStop(Math.Clamp(off, 0f, 1f), col));
                        }
                    }
                    if (stops.Count == 0) return null;
                    return new LinearGradientBrush(new PointF(p1.X, p1.Y), new PointF(p2.X, p2.Y),
                        GradientRepetitionMode.None, stops.ToArray());
                }
            }
            return null;
        }

        private static Color ParseColor(string hex, double opacity)
        {
            hex = hex.Trim();
            if (hex.StartsWith("#")) hex = hex.Substring(1);
            var color = Color.ParseHex(hex);
            if (opacity < 1.0)
            {
                var p = color.ToPixel<Rgba32>();
                p.A = (byte)Math.Clamp(p.A * opacity, 0, 255);
                color = Color.FromPixel(p);
            }
            return color;
        }

        // ---- JSON helpers ----

        private static double GetDouble(JsonElement e, string name, double fallback)
        {
            if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
                && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d))
                return d;
            return fallback;
        }

        private static bool TryGetString(JsonElement e, string name, out string? value)
        {
            value = null;
            if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
                && v.ValueKind == JsonValueKind.String)
            {
                value = v.GetString();
                return value != null;
            }
            return false;
        }
    }
}
