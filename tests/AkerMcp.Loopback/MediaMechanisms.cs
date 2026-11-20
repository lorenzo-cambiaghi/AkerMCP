using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using AkerMcp.Server;
using AkerMcp.Server.Platform;
using AkerMcp.Server.Platform.Windows;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Metadata.Profiles.Icc;
using SixLabors.ImageSharp.PixelFormats;

namespace AkerMcp.Loopback
{
    /// <summary>
    /// Contracts of the media and OS layer — what a sprite spec is refused for, which numbers a path
    /// reads, which channel a part plays on, when a parsed font is reused, how a shortcut is spelled,
    /// what a capture may be decoded as. Nothing here pins how a sprite looks or a tune sounds beyond
    /// "something was drawn" or "nothing was", and nothing injects real input into the desktop.
    /// </summary>
    internal static class MediaMechanisms
    {
        public static void Run(Action<string, bool, string?> check)
        {
            Sprites(check);
            PathData(check);
            Waves(check);
            Scores(check);
            FontCache(check);
            Chords(check);
            Captures(check);
        }

        // ---- create_sprite: a spec is drawn as written or refused with the reason ----
        private static void Sprites(Action<string, bool, string?> check)
        {
            var colours = Sprite("{\"width\":8,\"height\":8,\"background\":\"none\",\"shapes\":[" +
                "{\"type\":\"rect\",\"x\":0,\"y\":0,\"w\":8,\"h\":8,\"fill\":\"red\",\"stroke\":\"none\"}," +
                "{\"type\":\"rect\",\"x\":0,\"y\":0,\"w\":1,\"h\":1,\"fill\":\"rgb(0, 128, 255)\"}," +
                "{\"type\":\"rect\",\"x\":0,\"y\":0,\"w\":1,\"h\":1,\"fill\":\"rgba(0,0,0,0.5)\"}," +
                "{\"type\":\"rect\",\"x\":0,\"y\":0,\"w\":1,\"h\":1,\"fill\":\"transparent\",\"stroke\":\"#00ff00\"}]}", 8);
            var centre = colours.image?[4, 4] ?? default;
            check("create_sprite takes CSS names, rgb()/rgba() and none/transparent, and a name paints its colour",
                colours.error == null && centre.R > 200 && centre.G < 40 && centre.B < 40 && centre.A > 200,
                colours.error ?? centre.ToString());

            var bare = Sprite("{\"width\":8,\"height\":8,\"shapes\":[{\"type\":\"rect\",\"x\":0,\"y\":0,\"w\":8,\"h\":8}]}", 8);
            check("a closed shape with neither fill nor stroke is drawn, not dropped",
                bare.error == null && Opaque(bare.image!) > 0, bare.error);
            var none = Sprite("{\"width\":8,\"height\":8,\"shapes\":[{\"type\":\"rect\",\"x\":0,\"y\":0,\"w\":8,\"h\":8,\"fill\":\"none\"}]}", 8);
            check("fill:none with no stroke draws nothing, without an error",
                none.error == null && Opaque(none.image!) == 0, none.error);

            var star = Sprite("{\"shapes\":[{\"type\":\"star\",\"points\":[[0,0],[4,4],[0,4]],\"fill\":\"#000\"}]}", 8).error ?? "(no error)";
            check("an unknown shape type is an error naming it and the supported ones",
                star.Contains("'star'") && star.Contains("ellipse") && star.Contains("path"), star);
            var radial = Sprite("{\"shapes\":[{\"type\":\"rect\",\"w\":4,\"h\":4,\"fill\":{\"gradient\":\"radial\"," +
                "\"stops\":[{\"offset\":0,\"color\":\"#fff\"}]}}]}", 8).error ?? "(no error)";
            check("an unsupported gradient is an error naming the one there is",
                radial.Contains("'radial'") && radial.Contains("linear"), radial);
            var arc = Sprite("{\"shapes\":[{\"type\":\"path\",\"d\":\"M0 0 A 4 4 0 0 1 8 8\",\"fill\":\"#000\"}]}", 8).error ?? "(no error)";
            check("an unsupported path command is an error naming the supported ones",
                arc.Contains("'A'") && arc.Contains(SpriteRasterizer.SupportedPathCommands), arc);

            var degenerate = Sprite("{\"shapes\":[" +
                "{\"type\":\"ellipse\",\"cx\":4,\"cy\":4,\"rx\":0,\"ry\":3,\"fill\":\"#000\"}," +
                "{\"type\":\"polygon\",\"points\":[[1],[2,3],[4,\"x\"]],\"fill\":\"#000\"}]}", 8).error ?? "(no error)";
            check("a zero-radius ellipse and a malformed point are both reported, each with its shape",
                degenerate.Contains("shape 1 (ellipse)") && degenerate.Contains("rx")
                && degenerate.Contains("shape 2 (polygon)") && degenerate.Contains("point 0"), degenerate);
        }

        /// <summary>The rendered sprite, or the error it was refused with.</summary>
        private static (Image<Rgba32>? image, string? error) Sprite(string spec, int size)
        {
            try
            {
                var png = SpriteRasterizer.RenderToPng(Parse(spec), size, size);
                return (Image.Load<Rgba32>(png), null);
            }
            catch (Exception ex) { return (null, ex.Message); }
        }

        private static int Opaque(Image<Rgba32> image)
        {
            int n = 0;
            for (int y = 0; y < image.Height; y++)
                for (int x = 0; x < image.Width; x++)
                    if (image[x, y].A > 0) n++;
            return n;
        }

        // ---- SVG path data: the number grammar and where a subpath starts ----
        private static void PathData(Action<string, bool, string?> check)
        {
            var numbers = new List<float>();
            string? error = null;
            try
            {
                var reader = new SpriteRasterizer.PathReader(".5.5-1e-2.5");
                while (reader.HasNumber) numbers.Add(reader.ReadNumber());
            }
            catch (Exception ex) { error = ex.Message; }
            check("path numbers follow the SVG grammar: '.5.5-1e-2.5' is .5, .5, -0.01, .5",
                error == null && numbers.SequenceEqual(new[] { 0.5f, 0.5f, -0.01f, 0.5f }),
                error ?? string.Join(" ", numbers));

            string? subpathError = null;
            List<bool>? closed = null;
            try
            {
                closed = SpriteRasterizer.BuildPathData("M2 2 H10 V10 Z H20 V20 Z").Flatten().Select(p => p.IsClosed).ToList();
            }
            catch (Exception ex) { subpathError = ex.Message; }
            check("after Z, H and V start a new subpath, and its own Z closes it",
                closed != null && closed.Count == 2 && closed.All(c => c),
                subpathError ?? (closed == null ? "" : string.Join(",", closed)));
        }

        // ---- create_sound: a wave is what it is named, and an unknown name is refused ----
        private static void Waves(Action<string, bool, string?> check)
        {
            byte[] Wav(string wave) => SoundSynthesizer.RenderToWav(Parse($"{{\"wave\":\"{wave}\",\"freq\":440,\"duration\":0.1}}"));

            var saw = Wav("saw");
            var sawtooth = Wav("sawtooth");
            var square = Wav("square");
            check("wave sawtooth is the saw, not a square",
                sawtooth.AsSpan().SequenceEqual(saw) && !sawtooth.AsSpan().SequenceEqual(square), null);

            var unknown = Fails(() => Wav("sqaure"));
            check("an unknown wave is an error naming the waves there are",
                unknown.Contains("'sqaure'") && unknown.Contains("triangle") && unknown.Contains("noise"), unknown);
        }

        // ---- create_music: what a score is refused for, and which channel a part plays on ----
        private static void Scores(Action<string, bool, string?> check)
        {
            var longNote = Fails(() => MusicSequencer.Score.Parse(Parse(
                "{\"channels\":[{\"instrument\":\"pad\",\"notes\":\"C4*100 E4\"}]}"), loop: false));
            check("a note longer than the cap is an error naming it, not a shorter note",
                longNote.Contains("'C4*100'") && longNote.Contains(MusicSequencer.MaxNoteSteps.ToString()), longNote);

            string Parts(string engine, int melodic, int drums)
            {
                var channels = Enumerable.Range(0, melodic).Select(_ => "{\"instrument\":\"lead\",\"notes\":\"C4\"}")
                    .Concat(Enumerable.Range(0, drums).Select(_ => "{\"instrument\":\"drums\",\"notes\":\"k\"}"));
                return Fails(() => MusicSequencer.Score.Parse(Parse(
                    $"{{\"engine\":\"{engine}\",\"channels\":[{string.Join(",", channels)}]}}"), loop: false));
            }
            var sixteen = Parts("soundfont", SoundFontRenderer.MaxMelodicParts + 1, 0);
            var fifteenAndDrums = Parts("soundfont", SoundFontRenderer.MaxMelodicParts, 2);
            var chiptune = Parts("chiptune", SoundFontRenderer.MaxMelodicParts + 1, 0);
            check("the soundfont engine refuses a melodic part past MIDI's fifteen, and drums do not count",
                sixteen.Contains(SoundFontRenderer.MaxMelodicParts.ToString()) && sixteen.Contains("melodic")
                && fifteenAndDrums == "(no error)" && chiptune == "(no error)",
                $"{sixteen} | {fifteenAndDrums} | {chiptune}");

            // Every drum part shares channel 9: a part's level travels on its own notes, so a quiet
            // hat line cannot turn the kick down with it, and the channel is set up once.
            var band = MusicSequencer.Score.Parse(Parse("{\"engine\":\"soundfont\",\"channels\":[" +
                "{\"instrument\":\"drums\",\"notes\":\"k k k k\",\"volume\":1}," +
                "{\"instrument\":\"drums\",\"notes\":\"h h h h\",\"volume\":0.4}," +
                "{\"instrument\":\"drums\",\"notes\":\"s s s s\",\"volume\":0}]}"), loop: false);
            var setups = new List<SoundFontRenderer.ChannelSetup>();
            var hits = SoundFontRenderer.Timeline(band, setups).Where(e => e.On).ToList();
            var kicks = hits.Where(e => e.Key == 36).Select(e => e.Velocity).ToList();
            var hats = hits.Where(e => e.Key == 42).Select(e => e.Velocity).ToList();
            check("drum parts on the shared channel keep their own levels, and a silent one plays nothing",
                setups.Count(s => s.MidiChannel == 9) == 1 && kicks.Count == 4 && hats.Count == 4
                && hats.Max() < kicks.Min() && hats.Min() > 0 && !hits.Any(e => e.Key == 38),
                $"channel-9 setups {setups.Count(s => s.MidiChannel == 9)}, kick {string.Join("/", kicks)}, hat {string.Join("/", hats)}");
        }

        // ---- The parsed SoundFont: reused while the file is unchanged, let go when idle ----
        private static void FontCache(Action<string, bool, string?> check)
        {
            var file = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aker-font-cache-" + Guid.NewGuid().ToString("N") + ".sf2");
            try
            {
                File.WriteAllBytes(file, new byte[16]);
                var cache = new ParsedFileCache<object>(_ => new object());
                var first = cache.Get(file);
                var again = cache.Get(file);
                check("the parsed font is reused while the file is unchanged",
                    ReferenceEquals(first, again) && cache.Loads == 1, $"{cache.Loads} loads");

                File.SetLastWriteTimeUtc(file, File.GetLastWriteTimeUtc(file).AddMinutes(1));
                var replaced = cache.Get(file);
                check("a font replaced on disk is read again",
                    !ReferenceEquals(first, replaced) && cache.Loads == 2, $"{cache.Loads} loads");

                var idle = new ParsedFileCache<object>(_ => new object()) { IdleRelease = TimeSpan.FromMilliseconds(100) };
                idle.Get(file);
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (idle.IsLoaded && DateTime.UtcNow < deadline) Thread.Sleep(50);
                bool released = !idle.IsLoaded;
                idle.Get(file);
                check("an idle font is let go, and the next call reads it again",
                    released && idle.Loads == 2, $"released {released}, {idle.Loads} loads");

                check("the SoundFont renderer lets its font go when idle",
                    SoundFontRenderer.Fonts.IdleRelease > TimeSpan.Zero, SoundFontRenderer.Fonts.IdleRelease.ToString());
            }
            finally
            {
                try { File.Delete(file); } catch { /* temp */ }
            }

            // The real path, with a font small enough to build here: the 151 MB one is not in CI,
            // so without this the SoundFont renderer never runs in the suite at all.
            var font = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aker-tiny-" + Guid.NewGuid().ToString("N") + ".sf2");
            var configured = Environment.GetEnvironmentVariable(SoundFontLibrary.PathVariable);
            try
            {
                File.WriteAllBytes(font, TinySoundFont.Build());
                Environment.SetEnvironmentVariable(SoundFontLibrary.PathVariable, font);
                int loadsBefore = SoundFontRenderer.Fonts.Loads;
                var score = Parse("{\"engine\":\"soundfont\",\"channels\":[" +
                    "{\"instrument\":\"piano\",\"notes\":\"A4 . A4 .\"},{\"instrument\":\"drums\",\"notes\":\"k . k .\"}]}");
                string? renderError = null;
                byte[] first = Array.Empty<byte>(), second = Array.Empty<byte>();
                try
                {
                    first = MusicSequencer.RenderToWav(score);
                    second = MusicSequencer.RenderToWav(score);
                }
                catch (Exception ex) { renderError = ex.Message; }
                int loads = SoundFontRenderer.Fonts.Loads - loadsBefore;
                bool sounded = false;
                for (int i = 44; i + 1 < first.Length && !sounded; i += 2) sounded = BitConverter.ToInt16(first, i) != 0;
                check("a soundfont score renders, and a second render reuses the parsed font",
                    renderError == null && sounded && loads == 1 && first.AsSpan().SequenceEqual(second),
                    renderError ?? $"{loads} parses for two renders, sounded {sounded}");
            }
            finally
            {
                Environment.SetEnvironmentVariable(SoundFontLibrary.PathVariable, configured);
                try { File.Delete(font); } catch { /* temp */ }
            }
        }

        // ---- Shortcuts: one spelling on every OS ----
        private static void Chords(Action<string, bool, string?> check)
        {
            string Read(string chord) => KeyChordSyntax.TryParse(chord, out var mods, out var key, out var error)
                ? string.Join("+", mods.Append("[" + key + "]")) : "error: " + error;

            var trailing = Read("ctrl+");
            check("a chord ending in a single '+' names no key and is refused, pointing at ctrl++",
                trailing.StartsWith("error:") && trailing.Contains("ctrl++"), trailing);
            var plus = new[] { Read("ctrl++"), Read("ctrl+plus"), Read("ctrl + +"), Read("+") };
            check("ctrl++ and ctrl+plus are the plus key, and '+' alone is that key",
                plus[0] == "ctrl+[plus]" && plus[1] == "ctrl+[plus]" && plus[2] == "ctrl+[plus]" && plus[3] == "[plus]",
                string.Join(" | ", plus));
            var plain = new[] { Read("ctrl+shift+s"), Read("Escape"), Read("ctrl++s") };
            check("modifiers come first and the key last; an empty name between two '+' is refused",
                plain[0] == "ctrl+shift+[s]" && plain[1] == "[Escape]" && plain[2].StartsWith("error:"),
                string.Join(" | ", plain));

            if (OperatingSystem.IsWindows())
            {
                const uint scancode = 0x0008;
                check("a key with no scancode on the layout is sent by virtual key, not as scancode 0",
                    (WindowsInputInjector.KeyEventFlags(0xBB, 0, down: true) & scancode) == 0
                    && (WindowsInputInjector.KeyEventFlags(0x41, 0x1E, down: true) & scancode) != 0, null);
            }
        }

        // ---- Captures: only the formats a capture arrives in are decoded ----
        private static void Captures(Action<string, bool, string?> check)
        {
            // The head of a little-endian TIFF: ImageSharp 3.1's TIFF decoder has published
            // advisories, and a capture never arrives as one.
            var tiff = new byte[] { 0x49, 0x49, 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
            var refused = Fails(() => ImageProcessor.Normalize(tiff));
            check("a TIFF handed to the capture normaliser is refused before any TIFF decoder sees it",
                refused.Contains("neither PNG nor JPEG") && refused.Contains("49492A00"), refused);

            // An embedded ICC profile is never read: the remaining ICC advisory sits behind a parsed
            // profile, and a capture that carries none cannot reach it, now or after a change here.
            var profile = new byte[132];
            profile[3] = 132;                                    // size, big-endian
            Encoding.ASCII.GetBytes("acsp").CopyTo(profile, 36); // the signature every profile carries
            byte[] png;
            using (var source = new Image<Rgba32>(4, 4, new Rgba32(10, 20, 30, 255)))
            {
                source.Metadata.IccProfile = new IccProfile(profile);
                using var ms = new MemoryStream();
                source.Save(ms, new PngEncoder());
                png = ms.ToArray();
            }
            bool carried = Contains(png, "iCCP");
            string? normaliseError = null;
            byte[] jpeg = Array.Empty<byte>();
            try { jpeg = ImageProcessor.Normalize(png).Bytes; }
            catch (Exception ex) { normaliseError = ex.Message; }
            check("an ICC profile in a capture is not carried into the delivered JPEG",
                carried && normaliseError == null && jpeg.Length > 0 && !Contains(jpeg, "ICC_PROFILE"),
                normaliseError ?? $"source carried iCCP: {carried}, output carries ICC_PROFILE: {Contains(jpeg, "ICC_PROFILE")}");
        }

        private static bool Contains(byte[] haystack, string ascii)
            => haystack.AsSpan().IndexOf(Encoding.ASCII.GetBytes(ascii)) >= 0;

        /// <summary>
        /// The smallest SoundFont MeltySynth takes: one looped sine sample, one instrument, a melodic
        /// preset (bank 0, program 0) and a drum kit (bank 128), about 4.5 KB. Enough to drive the
        /// renderer end to end; it sounds like nothing in particular, and nothing asks it to.
        /// </summary>
        private static class TinySoundFont
        {
            public static byte[] Build()
            {
                // 20 periods of a sine (100 samples each), looped over the middle 18, then the
                // 46 zero samples the SoundFont specification puts after every sample.
                const int length = 2000;
                var smpl = Rec(b =>
                {
                    for (int i = 0; i < length; i++) b.Write((short)(Math.Sin(2 * Math.PI * i / 100.0) * 16000));
                    for (int i = 0; i < 46; i++) b.Write((short)0);
                });

                var info = List("INFO",
                    Chunk("ifil", Rec(b => { b.Write((ushort)2); b.Write((ushort)1); })),
                    Chunk("isng", Encoding.ASCII.GetBytes("EMU8000\0")),
                    Chunk("INAM", Encoding.ASCII.GetBytes("Tiny\0\0")));
                var sdta = List("sdta", Chunk("smpl", smpl));
                var pdta = List("pdta",
                    Chunk("phdr", Cat(Preset("Melodic", 0, 0), Preset("Drums", 128, 1), Preset("EOP", 0, 2))),
                    Chunk("pbag", Cat(Bag(0), Bag(1), Bag(2))),
                    Chunk("pmod", new byte[10]),
                    Chunk("pgen", Cat(Gen(41, 0), Gen(41, 0), Gen(0, 0))),           // 41: instrument 0
                    Chunk("inst", Cat(Rec(b => { b.Write(Name("Sine")); b.Write((ushort)0); }),
                                      Rec(b => { b.Write(Name("EOI")); b.Write((ushort)1); }))),
                    Chunk("ibag", Cat(Bag(0), Bag(2))),
                    Chunk("imod", new byte[10]),
                    Chunk("igen", Cat(Gen(54, 1), Gen(53, 0), Gen(0, 0))),           // 54: loop, 53: sample 0
                    Chunk("shdr", Cat(Rec(b =>
                    {
                        b.Write(Name("Sine")); b.Write(0u); b.Write((uint)length); b.Write(100u); b.Write(1900u);
                        b.Write(44000u); b.Write((byte)69); b.Write((sbyte)0); b.Write((ushort)0); b.Write((ushort)1);
                    }), new byte[46])));

                return Chunk("RIFF", Cat(Encoding.ASCII.GetBytes("sfbk"), info, sdta, pdta));
            }

            private static byte[] Preset(string name, ushort bank, ushort bag) => Rec(b =>
            {
                b.Write(Name(name)); b.Write((ushort)0); b.Write(bank); b.Write(bag); b.Write(0u); b.Write(0u); b.Write(0u);
            });

            private static byte[] Bag(ushort gen) => Rec(b => { b.Write(gen); b.Write((ushort)0); });

            private static byte[] Gen(ushort oper, ushort amount) => Rec(b => { b.Write(oper); b.Write(amount); });

            private static byte[] Name(string s)
            {
                var b = new byte[20];
                Encoding.ASCII.GetBytes(s).CopyTo(b, 0);
                return b;
            }

            private static byte[] Chunk(string id, byte[] data) => Rec(b =>
            {
                b.Write(Encoding.ASCII.GetBytes(id));
                b.Write(data.Length);
                b.Write(data);
                if (data.Length % 2 == 1) b.Write((byte)0);
            });

            private static byte[] List(string type, params byte[][] chunks)
                => Chunk("LIST", Cat(new[] { Encoding.ASCII.GetBytes(type) }.Concat(chunks).ToArray()));

            private static byte[] Cat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

            private static byte[] Rec(Action<BinaryWriter> write)
            {
                using var ms = new MemoryStream();
                using (var writer = new BinaryWriter(ms)) write(writer);
                return ms.ToArray();
            }
        }

        private static JsonElement Parse(string json)
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }

        /// <summary>The exception's message, or "(no error)" when the action went through.</summary>
        private static string Fails(Action action)
        {
            try { action(); return "(no error)"; }
            catch (Exception ex) { return ex.Message; }
        }
    }
}
