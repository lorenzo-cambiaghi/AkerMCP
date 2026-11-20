using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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
    ///   "background": <colour> | null,       // null/absent/"none" = transparent
    ///   "shapes": [                           // drawn in order (painter's algorithm)
    ///     { "type":"ellipse", "cx":32,"cy":32,"rx":20,"ry":18, "fill":"#FFCC00", "stroke":"#222","strokeWidth":2 },
    ///     { "type":"rect", "x":4,"y":4,"w":56,"h":56,"rx":8, "fill": <paint> },
    ///     { "type":"polygon", "points":[[x,y],...], "fill":"#FF8800" },
    ///     { "type":"line"|"polyline", "points":[[x,y],...], "stroke":"#000","strokeWidth":3 },
    ///     { "type":"path", "d":"M0 0 L10 10 Q.. C..  Z", "fill":"#000" }
    ///   ]
    /// }
    /// A colour is a hex string (#rgb, #rgba, #rrggbb, #rrggbbaa), a CSS colour name ("red"),
    /// rgb(r,g,b) / rgba(r,g,b,a), or "none"/"transparent" for no paint. A paint (fill or
    /// stroke) is a colour or a linear gradient:
    ///   { "gradient":"linear", "x1":0,"y1":0,"x2":0,"y2":64,
    ///     "stops":[{"offset":0,"color":"#fff"},{"offset":1,"color":"#888"}] }
    /// Optional per-shape "opacity" (0..1) multiplies the paint alpha. A closed shape with
    /// neither fill nor stroke is filled black.
    ///
    /// Everything wrong with a spec is an error naming the shape, collected before anything is
    /// drawn. The caller is a model that never sees the sprite: a shape that silently draws
    /// nothing — an unknown type, a colour name, an arc command — comes back as a successful
    /// import with a hole in it, and nothing in the answer says which shape went missing.
    /// </summary>
    public static class SpriteRasterizer
    {
        public const int DefaultSupersample = 4;

        /// <summary>Problems reported per spec; past this a model has enough to start fixing.</summary>
        private const int MaxReportedProblems = 8;

        private const string SupportedShapes = "ellipse (or circle), rect (or rectangle), polygon, polyline, line, path";

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

            // Every shape is built — and every problem found — before a pixel is drawn.
            var problems = new List<string>();
            Color? background = null;
            if (spec.TryGetProperty("background", out var bgEl))
            {
                try { background = ParseColorValue(bgEl, 1.0, "background"); }
                catch (ArgumentException ex) { problems.Add($"background: {ex.Message}"); }
            }

            var ops = new List<DrawOp>();
            if (spec.TryGetProperty("shapes", out var shapes) && shapes.ValueKind != JsonValueKind.Null)
            {
                if (shapes.ValueKind != JsonValueKind.Array)
                {
                    problems.Add("'shapes' must be an array of shape objects");
                }
                else
                {
                    int index = 0;
                    foreach (var shape in shapes.EnumerateArray())
                    {
                        index++;
                        try
                        {
                            var op = BuildShape(shape, transform, strokeScale);
                            if (op != null) ops.Add(op);
                        }
                        catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException)
                        {
                            problems.Add($"shape {index}{Label(shape)}: {ex.Message}");
                        }
                    }
                }
            }

            if (problems.Count > 0)
            {
                var shown = string.Join("; ", problems.Take(MaxReportedProblems));
                var more = problems.Count > MaxReportedProblems ? $" (and {problems.Count - MaxReportedProblems} more)" : "";
                throw new ArgumentException($"The shape-spec has problems: {shown}{more}.");
            }

            using var image = new Image<Rgba32>(renderW, renderH, Color.Transparent);

            // Antialiasing is on by default; SSAA on top gives crisp vector-like edges.
            var drawOpts = new DrawingOptions
            {
                GraphicsOptions = new GraphicsOptions { Antialias = true }
            };

            image.Mutate(ctx =>
            {
                if (background is Color bg) ctx.Fill(bg);
                foreach (var op in ops)
                {
                    if (op.Fill != null) ctx.Fill(drawOpts, op.Fill, op.Path);
                    if (op.Stroke != null) ctx.Draw(drawOpts, op.Stroke, op.Path);
                }
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

        /// <summary>One shape, ready to draw: its outline in render pixels and what paints it.</summary>
        private sealed record DrawOp(IPath Path, Brush? Fill, Pen? Stroke);

        private static string Label(JsonElement shape)
            => TryGetString(shape, "type", out var t) && !string.IsNullOrWhiteSpace(t) ? $" ({t})" : "";

        /// <summary>
        /// The outline and the paints of one shape, or null when it explicitly paints nothing
        /// ("fill":"none" and no stroke). Throws <see cref="ArgumentException"/> for anything
        /// it cannot draw as asked.
        /// </summary>
        private static DrawOp? BuildShape(JsonElement shape, Matrix3x2 transform, float strokeScale)
        {
            if (shape.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("a shape must be a JSON object with a \"type\"");

            if (!TryGetString(shape, "type", out var t) || string.IsNullOrWhiteSpace(t))
                throw new ArgumentException($"no \"type\"; use one of {SupportedShapes}");
            var type = t!.Trim().ToLowerInvariant();
            double opacity = Math.Clamp(GetDouble(shape, "opacity", 1.0), 0, 1);

            IPath path = type switch
            {
                "ellipse" or "circle" => BuildEllipse(shape),
                "rect" or "rectangle" => BuildRect(shape),
                "polygon" => BuildPolygon(shape, closed: true),
                "polyline" or "line" => BuildPolygon(shape, closed: false),
                "path" => BuildPath(shape),
                _ => throw new ArgumentException($"unknown type '{t}'; use one of {SupportedShapes}"),
            };
            path = path.Transform(transform);

            bool hasFill = shape.TryGetProperty("fill", out var fillEl);
            bool hasStroke = shape.TryGetProperty("stroke", out var strokeEl);

            Brush? fill = hasFill ? ParseBrush(fillEl, opacity, transform, "fill") : null;

            Pen? stroke = null;
            if (hasStroke)
            {
                var strokeBrush = ParseBrush(strokeEl, opacity, transform, "stroke");
                double w = GetDouble(shape, "strokeWidth", 1.0) * strokeScale;
                if (strokeBrush != null && w > 0)
                    stroke = Pens.Solid(strokeBrush, (float)w);
            }

            if (!hasFill && !hasStroke)
            {
                // Bare geometry is drawn in black, so a spec that is only shapes still shows
                // them. An open line or a path has no inside to fill the way a model means it —
                // a filled polyline is a wedge — so those have to say how they are painted.
                bool isOpen = type is "polyline" or "line";
                if (isOpen || type == "path")
                    throw new ArgumentException(
                        $"has neither \"fill\" nor \"stroke\", so it would draw nothing; give it a " +
                        (isOpen ? "\"stroke\" (and \"strokeWidth\")" : "\"fill\" or a \"stroke\""));
                fill = new SolidBrush(ApplyOpacity(Color.Black, opacity));
            }

            return fill == null && stroke == null ? null : new DrawOp(path, fill, stroke);
        }

        private static IPath BuildEllipse(JsonElement s)
        {
            float cx = (float)GetDouble(s, "cx", 0);
            float cy = (float)GetDouble(s, "cy", 0);
            double r = GetDouble(s, "r", 0);
            double rx = GetDouble(s, "rx", r);
            double ry = GetDouble(s, "ry", r);
            // EllipsePolygon throws ArgumentOutOfRangeException for a zero radius, which used to
            // fail the whole sprite with a message about a parameter nobody wrote.
            if (rx <= 0 || ry <= 0)
                throw new ArgumentException($"rx and ry (or r) must be positive, got rx {F(rx)} and ry {F(ry)}");
            return new EllipsePolygon(cx, cy, (float)rx, (float)ry);
        }

        private static IPath BuildRect(JsonElement s)
        {
            float x = (float)GetDouble(s, "x", 0);
            float y = (float)GetDouble(s, "y", 0);
            double w = GetDouble(s, "w", GetDouble(s, "width", 0));
            double h = GetDouble(s, "h", GetDouble(s, "height", 0));
            if (w <= 0 || h <= 0)
                throw new ArgumentException($"w and h must be positive, got w {F(w)} and h {F(h)}");
            float r = (float)GetDouble(s, "rx", GetDouble(s, "r", 0));

            if (r <= 0)
                return new RectangularPolygon(x, y, (float)w, (float)h);

            r = Math.Min(r, (float)Math.Min(w, h) / 2f);
            return BuildRoundedRect(x, y, (float)w, (float)h, r);
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

        private static IPath BuildPolygon(JsonElement s, bool closed)
        {
            var pts = ParsePoints(s);
            int needed = closed ? 3 : 2;
            if (pts.Count < needed)
                throw new ArgumentException(
                    $"needs at least {needed} points in \"points\" ([[x,y],...]), got {pts.Count}");
            if (closed)
                return new Polygon(new LinearLineSegment(pts.ToArray()));

            var pb = new PathBuilder();
            pb.AddLines(pts.ToArray());
            return pb.Build();
        }

        private static List<PointF> ParsePoints(JsonElement s)
        {
            var result = new List<PointF>();
            if (!s.TryGetProperty("points", out var pts) || pts.ValueKind == JsonValueKind.Null)
                return result;
            if (pts.ValueKind != JsonValueKind.Array)
                throw new ArgumentException("\"points\" must be an array of [x,y] pairs");

            // A point that is not two numbers is named, rather than read as 0: GetDouble on a
            // string threw InvalidOperationException and failed the sprite, and a one-element
            // pair quietly put its point on the top edge.
            int i = 0;
            foreach (var p in pts.EnumerateArray())
            {
                if (p.ValueKind == JsonValueKind.Array && p.GetArrayLength() == 2
                    && p[0].ValueKind == JsonValueKind.Number && p[1].ValueKind == JsonValueKind.Number)
                {
                    result.Add(new PointF((float)p[0].GetDouble(), (float)p[1].GetDouble()));
                }
                else if (p.ValueKind == JsonValueKind.Object
                         && p.TryGetProperty("x", out var px) && px.ValueKind == JsonValueKind.Number
                         && p.TryGetProperty("y", out var py) && py.ValueKind == JsonValueKind.Number)
                {
                    result.Add(new PointF((float)px.GetDouble(), (float)py.GetDouble()));
                }
                else
                {
                    throw new ArgumentException(
                        $"point {i} is {Abbreviate(p.GetRawText())}; a point is [x,y] or {{\"x\":..,\"y\":..}} with numbers");
                }
                i++;
            }
            return result;
        }

        private static IPath BuildPath(JsonElement s)
        {
            if (!TryGetString(s, "d", out var d) || string.IsNullOrWhiteSpace(d))
                throw new ArgumentException("needs path data in \"d\", e.g. \"M0 0 L10 0 L5 8 Z\"");
            return BuildPathData(d!);
        }

        internal const string SupportedPathCommands = "M L H V C Q Z (lowercase for relative)";

        /// <summary>
        /// The SVG path-data subset: M/m L/l H/h V/v C/c Q/q Z/z. Anything else is an error that
        /// says so: it used to return null, and an arc or a smooth curve drew nothing at all
        /// while the import reported success.
        /// </summary>
        internal static IPath BuildPathData(string d)
        {
            var pb = new PathBuilder();
            var reader = new PathReader(d);
            PointF cur = PointF.Empty;
            PointF startPt = PointF.Empty;
            bool open = false;
            bool any = false;

            // After a Z — or at the very start — the next drawing command begins a new subpath at
            // the current point. Only M and L used to do that, so in "M2 2 H10 V10 Z H20 V20 Z" the
            // second square's segments joined no figure and its Z, seeing nothing open, was dropped.
            void EnsureFigure()
            {
                if (open) return;
                pb.StartFigure();
                open = true;
                startPt = cur;
            }

            PointF Next(PointF p, bool rel) => rel ? new PointF(cur.X + p.X, cur.Y + p.Y) : p;

            if (!reader.AtEnd && !reader.AtCommand)
                throw new ArgumentException($"path data must start with a command such as M, not '{reader.Peek}'");

            while (reader.TryReadCommand(out char cmd))
            {
                bool rel = char.IsLower(cmd);
                switch (char.ToUpperInvariant(cmd))
                {
                    case 'M':
                    {
                        var p = reader.ReadPoint(cmd);
                        cur = Next(p, rel);
                        pb.StartFigure(); open = true; startPt = cur;
                        // Subsequent implicit pairs are treated as L.
                        while (reader.HasNumber)
                        {
                            var np = Next(reader.ReadPoint(cmd), rel);
                            pb.AddLine(cur, np); cur = np; any = true;
                        }
                        break;
                    }
                    case 'L':
                        do
                        {
                            var np = Next(reader.ReadPoint(cmd), rel);
                            EnsureFigure();
                            pb.AddLine(cur, np); cur = np; any = true;
                        } while (reader.HasNumber);
                        break;
                    case 'H':
                        do
                        {
                            float x = reader.ReadNumber(cmd);
                            var np = new PointF(rel ? cur.X + x : x, cur.Y);
                            EnsureFigure();
                            pb.AddLine(cur, np); cur = np; any = true;
                        } while (reader.HasNumber);
                        break;
                    case 'V':
                        do
                        {
                            float y = reader.ReadNumber(cmd);
                            var np = new PointF(cur.X, rel ? cur.Y + y : y);
                            EnsureFigure();
                            pb.AddLine(cur, np); cur = np; any = true;
                        } while (reader.HasNumber);
                        break;
                    case 'C':
                        do
                        {
                            var c1 = Next(reader.ReadPoint(cmd), rel);
                            var c2 = Next(reader.ReadPoint(cmd), rel);
                            var e = Next(reader.ReadPoint(cmd), rel);
                            EnsureFigure();
                            pb.AddCubicBezier(cur, c1, c2, e); cur = e; any = true;
                        } while (reader.HasNumber);
                        break;
                    case 'Q':
                        do
                        {
                            var c = Next(reader.ReadPoint(cmd), rel);
                            var e = Next(reader.ReadPoint(cmd), rel);
                            EnsureFigure();
                            pb.AddQuadraticBezier(cur, c, e); cur = e; any = true;
                        } while (reader.HasNumber);
                        break;
                    case 'Z':
                        if (open) { pb.CloseFigure(); open = false; cur = startPt; }
                        break;
                    case 'A':
                    case 'S':
                    case 'T':
                        throw new ArgumentException(
                            $"path command '{cmd}' is not supported; supported: {SupportedPathCommands}. " +
                            (char.ToUpperInvariant(cmd) == 'A'
                                ? "Approximate an arc with C curves, or use an ellipse shape."
                                : "Write the smooth curve out as C or Q with its control point."));
                    default:
                        throw new ArgumentException(
                            $"'{cmd}' is not an SVG path command; supported: {SupportedPathCommands}");
                }

                if (!reader.AtEnd && !reader.AtCommand)
                    throw new ArgumentException($"unexpected '{reader.Peek}' after the '{cmd}' command in the path data");
            }

            if (!any)
                throw new ArgumentException("the path data draws no segment (it needs an L, H, V, C or Q after the M)");
            return pb.Build();
        }

        /// <summary>
        /// Number/command reader for SVG path data, following the SVG number grammar: a
        /// number has at most one decimal point and one exponent, so ".5.5" is two numbers
        /// (0.5 and 0.5) and "1-2" is two as well. It used to read ".5.5" as one and fail to
        /// parse it, which dropped the whole path.
        /// </summary>
        internal sealed class PathReader
        {
            private readonly string _s;
            private int _i;
            public PathReader(string s) { _s = s; _i = 0; }

            private void SkipSep()
            {
                while (_i < _s.Length && (char.IsWhiteSpace(_s[_i]) || _s[_i] == ',')) _i++;
            }

            public bool AtEnd { get { SkipSep(); return _i >= _s.Length; } }

            public bool AtCommand { get { SkipSep(); return _i < _s.Length && char.IsLetter(_s[_i]); } }

            public char Peek { get { SkipSep(); return _i < _s.Length ? _s[_i] : '\0'; } }

            public bool TryReadCommand(out char cmd)
            {
                if (AtCommand) { cmd = _s[_i++]; return true; }
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

            public float ReadNumber(char command = '\0')
            {
                SkipSep();
                int start = _i;
                if (_i < _s.Length && (_s[_i] == '-' || _s[_i] == '+')) _i++;

                int digits = 0;
                while (_i < _s.Length && char.IsDigit(_s[_i])) { _i++; digits++; }
                if (_i < _s.Length && _s[_i] == '.')
                {
                    _i++;
                    while (_i < _s.Length && char.IsDigit(_s[_i])) { _i++; digits++; }
                }
                // An exponent only when digits follow it, so a stray 'e' is reported where it is.
                if (digits > 0 && _i < _s.Length && (_s[_i] == 'e' || _s[_i] == 'E'))
                {
                    int mark = _i++;
                    if (_i < _s.Length && (_s[_i] == '-' || _s[_i] == '+')) _i++;
                    if (_i < _s.Length && char.IsDigit(_s[_i]))
                        while (_i < _s.Length && char.IsDigit(_s[_i])) _i++;
                    else
                        _i = mark;
                }

                if (digits == 0)
                {
                    var found = start < _s.Length ? $"'{_s[start]}'" : "the end of the data";
                    var after = command != '\0' ? $" for '{command}'" : "";
                    throw new ArgumentException($"expected a number{after} in the path data at position {start}, found {found}");
                }
                return float.Parse(_s.AsSpan(start, _i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
            }

            public PointF ReadPoint(char command = '\0') => new PointF(ReadNumber(command), ReadNumber(command));
        }

        // ---- paint / color helpers ----

        /// <summary>A fill or stroke: a colour, a gradient, or null for "none".</summary>
        private static Brush? ParseBrush(JsonElement paint, double opacity, Matrix3x2 transform, string what)
        {
            if (paint.ValueKind != JsonValueKind.Object)
            {
                var color = ParseColorValue(paint, opacity, what);
                return color is Color c ? new SolidBrush(c) : null;
            }

            var kind = TryGetString(paint, "gradient", out var g) ? g!.Trim().ToLowerInvariant() : "linear";
            if (kind != "linear")
                throw new ArgumentException(
                    $"{what}: gradient '{g}' is not supported; only \"linear\" is (x1,y1 to x2,y2 with stops)");

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
                    // A stop that is "none"/"transparent" fades to nothing, which is what a
                    // gradient to transparent means — not a stop that is not there.
                    var col = st.ValueKind == JsonValueKind.Object && st.TryGetProperty("color", out var c)
                        ? ParseColorValue(c, opacity, $"{what} gradient stop") ?? Color.Transparent
                        : Color.Black;
                    stops.Add(new ColorStop(Math.Clamp(off, 0f, 1f), col));
                }
            }
            if (stops.Count == 0)
                throw new ArgumentException($"{what}: a gradient needs \"stops\", e.g. [{{\"offset\":0,\"color\":\"#fff\"}},{{\"offset\":1,\"color\":\"#888\"}}]");
            return new LinearGradientBrush(new PointF(p1.X, p1.Y), new PointF(p2.X, p2.Y),
                GradientRepetitionMode.None, stops.ToArray());
        }

        /// <summary>A colour from JSON: null and "none"/"transparent"/"" mean no paint (null).</summary>
        private static Color? ParseColorValue(JsonElement value, double opacity, string what)
        {
            if (value.ValueKind == JsonValueKind.Null) return null;
            if (value.ValueKind != JsonValueKind.String)
                throw new ArgumentException($"{what} must be a colour string such as \"#FFCC00\" or \"red\", got {Abbreviate(value.GetRawText())}");
            return ParseColor(value.GetString()!, opacity, what);
        }

        /// <summary>
        /// Hex, a CSS colour name, rgb()/rgba(), or "none"/"transparent" (null: no paint). A
        /// colour name or "none" used to reach Color.ParseHex and fail the whole sprite, for
        /// the spelling every SVG example on the web uses.
        /// </summary>
        internal static Color? ParseColor(string text, double opacity, string what = "colour")
        {
            var s = text.Trim();
            if (s.Length == 0 || s.Equals("none", StringComparison.OrdinalIgnoreCase)
                || s.Equals("transparent", StringComparison.OrdinalIgnoreCase))
                return null;

            Color color;
            if (s.StartsWith("rgb", StringComparison.OrdinalIgnoreCase))
                color = ParseRgbFunction(s, what);
            else if (!Color.TryParse(s.StartsWith('#') ? s.Substring(1) : s, out color))
                throw new ArgumentException(
                    $"{what} '{text}' is not a colour; use hex (#rgb, #rrggbb, #rrggbbaa), a CSS name such as " +
                    "\"red\", rgb(r,g,b), rgba(r,g,b,a), or \"none\"");
            return ApplyOpacity(color, opacity);
        }

        private static Color ApplyOpacity(Color color, double opacity)
        {
            if (opacity >= 1.0) return color;
            var p = color.ToPixel<Rgba32>();
            p.A = (byte)Math.Clamp(p.A * opacity, 0, 255);
            return Color.FromPixel(p);
        }

        /// <summary>rgb(255, 0, 0), rgba(255,0,0,0.5), rgb(100% 0% 0% / 50%): CSS's functional notation.</summary>
        private static Color ParseRgbFunction(string s, string what)
        {
            int open = s.IndexOf('('), close = s.LastIndexOf(')');
            var name = open > 0 ? s.Substring(0, open).Trim().ToLowerInvariant() : "";
            if ((name != "rgb" && name != "rgba") || close < open || close != s.Length - 1)
                throw new ArgumentException($"{what} '{s}' is not rgb(r,g,b) or rgba(r,g,b,a)");

            var parts = s.Substring(open + 1, close - open - 1)
                         .Split(new[] { ',', ' ', '/', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length is not (3 or 4))
                throw new ArgumentException($"{what} '{s}' needs three channels and an optional alpha");

            byte Channel(string part, bool alpha)
            {
                bool percent = part.EndsWith('%');
                var number = percent ? part.Substring(0, part.Length - 1) : part;
                if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                    throw new ArgumentException($"{what} '{s}': '{part}' is not a number");
                double unit = percent ? v / 100.0 : (alpha ? v : v / 255.0);
                return (byte)Math.Round(Math.Clamp(unit, 0, 1) * 255);
            }

            byte a = parts.Length == 4 ? Channel(parts[3], alpha: true) : (byte)255;
            return Color.FromPixel(new Rgba32(Channel(parts[0], false), Channel(parts[1], false), Channel(parts[2], false), a));
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

        private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        private static string Abbreviate(string raw) => raw.Length <= 40 ? raw : raw.Substring(0, 37) + "...";
    }
}
