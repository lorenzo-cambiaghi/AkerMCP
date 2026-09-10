using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AkerMcp.UiProbe
{
    /// <summary>
    /// The bench for ui_action: does a point named in an image reach that point on screen, and
    /// does the image show what is actually there?
    ///
    /// Both questions were answered wrongly at some point by code that tested clean. The
    /// coordinate maths agreed with itself while the picture it described began 31 px higher
    /// than the anchor claimed, so every click landed a title bar low; and menus were absent
    /// from captures entirely, because a dropdown is a window of its own. Neither shows up in
    /// a unit test. Both show up here in one line of output.
    ///
    /// Run it against any window — the editor, a text editor, anything with a title:
    ///
    ///   dotnet run --project tools/AkerMcp.UiProbe -- coords "SkelForge"
    ///   dotnet run --project tools/AkerMcp.UiProbe -- shot   "SkelForge" out.jpg
    ///   dotnet run --project tools/AkerMcp.UiProbe -- run    calls.json
    /// </summary>
    public static class Program
    {
        public static async Task<int> Main(string[] args)
        {
            Pointer.MatchServerCoordinateSpace();

            var rest = new List<string>();
            string? serverOverride = null;
            string profile = "full";
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--server" && i + 1 < args.Length) serverOverride = args[++i];
                else if (args[i] == "--profile" && i + 1 < args.Length) profile = args[++i];
                else rest.Add(args[i]);
            }

            if (rest.Count == 0) { Usage(); return 1; }

            var serverPath = serverOverride ?? FindServer();
            Console.WriteLine($"server: {serverPath}");

            try
            {
                return rest[0].ToLowerInvariant() switch
                {
                    "coords" => await Coords(serverPath, profile, Arg(rest, 1)),
                    "shot" => await Shot(serverPath, profile, Arg(rest, 1), rest.Count > 2 ? rest[2] : "probe.jpg"),
                    "run" => await Run(serverPath, profile, Arg(rest, 1)),
                    _ => Unknown(rest[0]),
                };
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"{ex.GetType().Name}: {ex.Message}");
                return 1;
            }
        }

        private static string Arg(List<string> args, int i)
            => i < args.Count ? args[i] : throw new ArgumentException("Missing argument; see the usage above.");

        private static int Unknown(string what)
        {
            Console.Error.WriteLine($"Unknown command '{what}'.");
            Usage();
            return 1;
        }

        private static void Usage() => Console.WriteLine(
            """
            AkerMcp.UiProbe — measures ui_action against the operating system.

              coords <window-title>          move the pointer to points read off a screenshot and
                                             ask the OS where it went; prints the error in pixels
              shot   <window-title> [file]   capture the window and save it, to see what the model sees
              run    <calls.json>            run a JSON array of {"name":…,"arguments":{…}} tool calls

            Options:
              --server <path>   the server binary (default: the repo's Debug build)
              --profile <name>  tool profile to load (default: full, so ui_action is present)

            The window is matched on a substring of its title, exactly as the tools do.
            """);

        /// <summary>
        /// The measurement that matters. Take a screenshot to establish the anchor, then for a
        /// handful of points across it: send the pointer there and ask the OS where it ended up.
        ///
        /// Anything but zero is a bug in the conversion — with one honest exception, noted in the
        /// output: a maximized window's client area starts a few pixels off the screen, and a
        /// point outside the desktop clamps to its edge.
        /// </summary>
        private static async Task<int> Coords(string serverPath, string profile, string title)
        {
            using var mcp = await McpStdio.Start(serverPath, profile, null);

            var shot = await mcp.Call("ui_action", new { action = "screenshot", window_title = title });
            if (shot.IsError) { Console.Error.WriteLine(shot.Text); return 1; }

            var anchor = Anchor.Parse(shot.Text);
            if (anchor == null)
            {
                Console.Error.WriteLine($"Could not read the anchor from: {shot.Text}");
                return 1;
            }
            Console.WriteLine(anchor);

            var points = new (int x, int y)[]
            {
                (0, 0),
                (10, 10),
                (anchor.ImageWidth / 2, anchor.ImageHeight / 2),
                (anchor.ImageWidth - 1, anchor.ImageHeight - 1),
                (anchor.ImageWidth / 4, anchor.ImageHeight * 3 / 4),
            };

            int worst = 0;
            foreach (var (px, py) in points)
            {
                var (wantX, wantY) = anchor.ToScreen(px, py);
                int gotX = 0, gotY = 0, dx = 0, dy = 0;
                bool retried = false;

                // Twice, if the first answer is wrong. A hand on the physical mouse moves the
                // pointer after the tool has put it somewhere, and the reading is then a
                // measurement of the hand, not of the conversion: one such false alarm here
                // read 265 px and sent me looking for a regression that did not exist. A real
                // conversion error is deterministic and survives the second attempt; an
                // interfering hand almost never lands on the same wrong pixel twice.
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    var moved = await mcp.Call("ui_action", new
                    {
                        action = "mouse_move",
                        x = px,
                        y = py,
                        screenshot = false,
                    });
                    if (moved.IsError) { Console.Error.WriteLine(moved.Text); return 1; }

                    await Task.Delay(120);
                    if (!Pointer.TryGetPosition(out gotX, out gotY, out var err))
                    {
                        Console.Error.WriteLine(err);
                        return 1;
                    }

                    dx = Math.Abs(gotX - wantX);
                    dy = Math.Abs(gotY - wantY);
                    if (dx == 0 && dy == 0) break;
                    retried = attempt == 0;
                }

                worst = Math.Max(worst, Math.Max(dx, dy));
                Console.WriteLine(
                    $"  image ({px,5},{py,5}) -> expected ({wantX,5},{wantY,5})  pointer ({gotX,5},{gotY,5})  " +
                    $"off by {dx},{dy}{(retried && dx == 0 && dy == 0 ? "  (first read disagreed: something moved the mouse)" : "")}");
            }

            Console.WriteLine($"  worst error: {worst} px");
            Console.WriteLine(worst == 0
                ? "  exact."
                : "  NOT exact. A maximized window can legitimately show a few px at its very first " +
                  "pixels (its client area starts off-screen and the pointer clamps); anywhere else, " +
                  "the image and the anchor disagree.");
            return worst == 0 ? 0 : 2;
        }

        private static async Task<int> Shot(string serverPath, string profile, string title, string outFile)
        {
            using var mcp = await McpStdio.Start(serverPath, profile, null);
            var shot = await mcp.Call("ui_action", new { action = "screenshot", window_title = title });
            Console.WriteLine(shot.Text);
            if (shot.Images.Length == 0)
            {
                Console.Error.WriteLine("No image came back.");
                return 1;
            }
            await File.WriteAllBytesAsync(outFile, shot.Images[0]);
            Console.WriteLine($"  wrote {outFile} ({shot.Images[0].Length / 1024} KB)");
            Console.WriteLine("  look for: no title bar at the top (the image is the client area), and any " +
                              "menu you left open drawn in place.");
            return 0;
        }

        /// <summary>
        /// Replays a JSON array of tool calls, so a gesture can be scripted without a client.
        /// Images are written next to the file as run-1.jpg, run-2.jpg …
        /// </summary>
        private static async Task<int> Run(string serverPath, string profile, string callsFile)
        {
            var json = JsonSerializer.Deserialize<JsonElement>(await File.ReadAllTextAsync(callsFile));
            if (json.ValueKind != JsonValueKind.Array)
            {
                Console.Error.WriteLine("The file must hold a JSON array of {\"name\":…,\"arguments\":{…}}.");
                return 1;
            }

            using var mcp = await McpStdio.Start(serverPath, profile, null);
            var dir = Path.GetDirectoryName(Path.GetFullPath(callsFile)) ?? ".";
            int n = 0, failures = 0;

            foreach (var call in json.EnumerateArray())
            {
                n++;
                var name = call.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "";
                object arguments = call.TryGetProperty("arguments", out var a)
                    ? JsonSerializer.Deserialize<object>(a.GetRawText())!
                    : new { };

                var started = DateTime.UtcNow;
                var answer = await mcp.Call(name, arguments);
                var ms = (int)(DateTime.UtcNow - started).TotalMilliseconds;

                Console.WriteLine($"  [{n} {name}] {ms}ms {(answer.IsError ? "REFUSED/ERROR" : "ok")} :: {answer.Text}");
                if (answer.IsError) failures++;

                for (int i = 0; i < answer.Images.Length; i++)
                {
                    var file = Path.Combine(dir, $"run-{n}{(i == 0 ? "" : $"-{i}")}.jpg");
                    await File.WriteAllBytesAsync(file, answer.Images[i]);
                    Console.WriteLine($"       -> {file}");
                }
            }

            var log = mcp.DrainLog();
            foreach (var line in log.Split('\n'))
                if (line.Contains("Protocol", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("Connected", StringComparison.OrdinalIgnoreCase))
                    Console.WriteLine($"  {line.Trim()}");

            return failures == 0 ? 0 : 2;
        }

        /// <summary>The repo's own Debug build, which is what someone iterating has just compiled.</summary>
        private static string FindServer()
        {
            var dir = AppContext.BaseDirectory;
            for (int up = 0; up < 8 && dir != null; up++)
            {
                var candidate = Path.Combine(dir, "Server", "bin", "Debug", "net8.0",
                    OperatingSystem.IsWindows() ? "AkerMcp.Server.exe" : "AkerMcp.Server");
                if (File.Exists(candidate)) return candidate;
                dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
            }
            return OperatingSystem.IsWindows()
                ? Path.Combine("Server", "bin", "Debug", "net8.0", "AkerMcp.Server.exe")
                : Path.Combine("Server", "bin", "Debug", "net8.0", "AkerMcp.Server");
        }
    }

    /// <summary>
    /// The line capture_window and ui_action print beside every image, parsed back. It is the
    /// server's own description of what its pixels mean, so measuring against it tests the
    /// promise the model is given rather than a reimplementation of it.
    /// </summary>
    public sealed class Anchor
    {
        public int ImageWidth, ImageHeight, SourceWidth, SourceHeight, OriginX, OriginY;

        public (int x, int y) ToScreen(double imageX, double imageY)
        {
            double scaleX = ImageWidth == 0 ? 1 : (double)SourceWidth / ImageWidth;
            double scaleY = ImageHeight == 0 ? 1 : (double)SourceHeight / ImageHeight;
            return ((int)Math.Round(OriginX + (imageX + 0.5) * scaleX - 0.5, MidpointRounding.AwayFromZero),
                    (int)Math.Round(OriginY + (imageY + 0.5) * scaleY - 0.5, MidpointRounding.AwayFromZero));
        }

        public static Anchor? Parse(string description)
        {
            var size = Regex.Match(description, @"image (\d+)x(\d+)");
            var origin = Regex.Match(description, @"origin on screen (-?\d+),(-?\d+)");
            if (!size.Success || !origin.Success) return null;

            var anchor = new Anchor
            {
                ImageWidth = int.Parse(size.Groups[1].Value, CultureInfo.InvariantCulture),
                ImageHeight = int.Parse(size.Groups[2].Value, CultureInfo.InvariantCulture),
                OriginX = int.Parse(origin.Groups[1].Value, CultureInfo.InvariantCulture),
                OriginY = int.Parse(origin.Groups[2].Value, CultureInfo.InvariantCulture),
            };

            var scaled = Regex.Match(description, @"scaled [\d.]+x from (\d+)x(\d+)");
            anchor.SourceWidth = scaled.Success
                ? int.Parse(scaled.Groups[1].Value, CultureInfo.InvariantCulture) : anchor.ImageWidth;
            anchor.SourceHeight = scaled.Success
                ? int.Parse(scaled.Groups[2].Value, CultureInfo.InvariantCulture) : anchor.ImageHeight;
            return anchor;
        }

        public override string ToString()
            => $"  image {ImageWidth}x{ImageHeight}, window {SourceWidth}x{SourceHeight} at {OriginX},{OriginY}, " +
               $"scale {(ImageWidth == 0 ? 1 : (double)SourceWidth / ImageWidth):0.####}";
    }
}
