using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AkerMcp.Server
{
    /// <summary>
    /// Turns a short score into 16-bit mono PCM WAV bytes, on one of two engines that read the
    /// same notation.
    ///
    /// <para>The default is a chiptune tracker over the sfxr voice in
    /// <see cref="SoundSynthesizer"/>, so every note inherits the same filters and envelopes
    /// rather than growing a second, worse synth beside it. It needs nothing: no download, no
    /// licence, and square waves are the idiom a prototype is expected to sound like.
    /// <c>"engine":"soundfont"</c> plays the same score on sampled General MIDI instruments
    /// (<see cref="SoundFontRenderer"/>), with a font fetched once on first use — FluidR3, which
    /// is MIT, rather than the small GPL one everyone reaches for first, because this tool's
    /// output lands in someone else's game.</para>
    ///
    /// <para>The score is written the way a person hums it: tokens separated by spaces, one
    /// step each. <c>C4 E4 G4 .</c> is three notes and a rest; <c>-</c> holds the slot before
    /// it; <c>C4*4</c> is a note four steps long. A drum channel spells its tokens
    /// <c>k s h o</c>. Everything else — tempo, how long a step lasts, which voice plays a
    /// channel — is named once at the top.</para>
    ///
    /// <para>Everything that can be wrong with a score is found before anything is fetched or
    /// allocated: a typo must not cost a 151 MB download, a long score must not cost a
    /// gigabyte, and a token nobody recognises is an error that names it rather than a
    /// silent step.</para>
    /// </summary>
    public static class MusicSequencer
    {
        /// <summary>
        /// The most PCM one score may render to. The engine link carries a single frame of at
        /// most 64 MB, and a clip over it does not fail politely — the plugin drops the
        /// connection. 60 MB leaves room for the header and the metadata: about eleven and a
        /// half minutes at 44.1 kHz.
        /// </summary>
        public const long MaxPcmBytes = 60L * 1024 * 1024;

        /// <summary>A one-shot score ends with this much room for its last note to decay.</summary>
        private const double ChiptuneTailSeconds = 0.5;

        /// <summary>No single chiptune note outlasts the whole score, which the limit above bounds.</summary>
        private const double MaxNoteSeconds = 15 * 60;

        private static readonly string[] ChiptuneInstruments = { "lead", "bass", "pad", "chord", "arp", "pluck" };
        private static readonly string[] ChiptuneDrumTokens = { "k", "kick", "b", "s", "snare", "h", "hat", "o", "openhat" };
        private static readonly char[] Separators = { ' ', '\t', '\n', '\r', '|' };

        /// <summary>
        /// Render a score. Synchronous entry point, kept for callers that cannot await; the
        /// SoundFont engine needs a file that may have to be fetched, so it goes through
        /// <see cref="RenderToWavAsync(JsonElement, bool, CancellationToken)"/> instead.
        /// </summary>
        public static byte[] RenderToWav(JsonElement spec) => RenderToWav(spec, loop: false);

        public static byte[] RenderToWav(JsonElement spec, bool loop)
            => RenderToWavAsync(spec, loop, CancellationToken.None).GetAwaiter().GetResult();

        public static Task<byte[]> RenderToWavAsync(JsonElement spec, CancellationToken ct)
            => RenderToWavAsync(spec, loop: false, ct);

        /// <summary>
        /// Render a score. With <paramref name="loop"/> the clip is exactly the music — steps ×
        /// repeat × step length — and the release of its last notes is folded back onto its
        /// start, which is where it sounds when the clip loops. Without it a one-shot keeps a
        /// tail after the last step instead: a looping clip that carried that tail stumbled at
        /// every loop point, the downbeat arriving half a second (two with the SoundFont) late.
        /// </summary>
        public static async Task<byte[]> RenderToWavAsync(JsonElement spec, bool loop, CancellationToken ct)
        {
            var score = Score.Parse(spec, loop);
            if (score.Engine == Engine.SoundFont)
            {
                var (path, error) = await SoundFontLibrary.Resolve(ct).ConfigureAwait(false);
                if (path == null) throw new InvalidOperationException(error ?? "No SoundFont available.");
                return SoundFontRenderer.Render(path, score);
            }
            return RenderChiptune(score);
        }

        private static byte[] RenderChiptune(Score score)
        {
            int loopSamples = score.LoopSamples;
            int total = score.TotalSamples;
            var mix = new double[total];

            foreach (var channel in score.Channels)
            {
                var seq = channel.Steps;
                for (int pass = 0; pass < score.RepeatCount; pass++)
                {
                    for (int s = 0; s < seq.Count; s++)
                    {
                        var step = seq[s];
                        if (step.IsRest || step.Held) continue;

                        var voice = VoiceFor(channel, step.Token, step.Length * score.StepSeconds, score.SampleRate);
                        var rendered = SoundSynthesizer.RenderSamples(voice, out _, MaxNoteSeconds);

                        // Every pass starts where the whole score starts again, not where this
                        // channel runs out: a two-bar bass under an eight-bar lead plays under
                        // the first two bars of each pass instead of racing ahead of the lead.
                        long at = (long)(pass * score.Steps + s) * score.StepSamples;
                        for (int i = 0; i < rendered.Length; i++)
                        {
                            long pos = at + i;
                            double v = rendered[i] / (double)short.MaxValue * channel.Volume;
                            if (score.Loop) mix[pos % loopSamples] += v;
                            else if (pos < total) mix[pos] += v;
                            else break;
                        }
                    }
                }
            }

            return Normalize(mix, score.MasterVolume, score.SampleRate);
        }

        /// <summary>
        /// Normalize instead of clipping: summing four channels overshoots almost always, and a
        /// clipped mix is the harshness this whole exercise set out to remove.
        /// </summary>
        internal static byte[] Normalize(double[] mix, double masterVolume, int sampleRate)
        {
            double peak = 0;
            foreach (var v in mix) peak = Math.Max(peak, Math.Abs(v));
            double gain = peak > 0 ? Math.Clamp(masterVolume, 0, 1) / peak : 1.0;

            var samples = new short[mix.Length];
            for (int i = 0; i < mix.Length; i++)
                samples[i] = (short)(Math.Clamp(mix[i] * gain, -1, 1) * short.MaxValue);

            return SoundSynthesizer.WriteWav(samples, sampleRate);
        }

        internal enum Engine { Chiptune, SoundFont }

        internal readonly record struct Step(string Token, int Length, bool IsRest, bool Held);

        internal sealed record Channel(string Instrument, bool IsDrums, IReadOnlyList<Step> Steps, double Volume);

        /// <summary>A score after every check: what the renderers are allowed to trust.</summary>
        internal sealed class Score
        {
            public Engine Engine { get; private init; }
            public bool Loop { get; private init; }
            public int SampleRate { get; private init; }
            public double StepSeconds { get; private init; }
            /// <summary>The grid both engines place notes on, so a loop is the same length on either.</summary>
            public int StepSamples { get; private init; }
            public int RepeatCount { get; private init; }
            public double MasterVolume { get; private init; }
            /// <summary>One pass: the longest channel.</summary>
            public int Steps { get; private init; }
            public IReadOnlyList<Channel> Channels { get; private init; } = Array.Empty<Channel>();
            public int TailSamples { get; private init; }

            public int LoopSamples => Steps * RepeatCount * StepSamples;
            public int TotalSamples => LoopSamples + TailSamples;

            public static Score Parse(JsonElement spec, bool loop)
            {
                var engineName = ReadStr(spec, "engine", "chiptune").Trim().ToLowerInvariant();
                var engine = engineName switch
                {
                    "chiptune" or "" => Engine.Chiptune,
                    "soundfont" or "gm" or "instruments" => Engine.SoundFont,
                    _ => throw new ArgumentException(
                        $"Unknown engine '{engineName}'. Use \"chiptune\" (the default) or \"soundfont\"."),
                };

                // MeltySynth refuses anything under 16 kHz; the tracker goes down to 8.
                int minRate = engine == Engine.SoundFont ? SoundFontRenderer.MinSampleRate : 8000;
                int sampleRate = ClampInt(ReadInt(spec, "sample_rate", 44100), minRate, 48000);
                double bpm = Clamp(ReadNum(spec, "bpm", 120), 20, 300);
                int stepsPerBeat = ClampInt(ReadInt(spec, "steps_per_beat", 4), 1, 16);
                double stepSeconds = 60.0 / bpm / stepsPerBeat;
                int stepSamples = Math.Max(1, (int)(stepSeconds * sampleRate));
                int repeatCount = ClampInt(ReadInt(spec, "repeat", 1), 1, 8);
                double tailSeconds = engine == Engine.SoundFont ? SoundFontRenderer.TailSeconds : ChiptuneTailSeconds;
                int tail = loop ? 0 : (int)(tailSeconds * sampleRate);

                // A channel longer than this cannot fit even played once. Parsing stops there,
                // rather than building a list of millions of steps the limit refuses anyway.
                long maxSteps = (MaxPcmBytes / 2 - tail) / stepSamples;

                var channels = new List<Channel>();
                var problems = new List<string>();
                bool unknownInstrument = false, badNotes = false, badDrums = false;
                if (spec.ValueKind == JsonValueKind.Object
                    && spec.TryGetProperty("channels", out var chans) && chans.ValueKind == JsonValueKind.Array)
                {
                    int index = 0;
                    foreach (var c in chans.EnumerateArray())
                    {
                        if (c.ValueKind != JsonValueKind.Object) continue;
                        index++;
                        var instrument = ReadStr(c, "instrument", "lead").Trim().ToLowerInvariant();
                        bool isDrums = instrument is "drums" or "drum" or "perc";
                        if (!isDrums && !IsKnownInstrument(engine, instrument))
                        {
                            problems.Add($"channel {index}: unknown instrument '{instrument}'");
                            unknownInstrument = true;
                        }

                        var steps = ParseSteps(ReadStr(c, "notes", ""), maxSteps, sampleRate);
                        var bad = steps.Where(s => !s.IsRest && !s.Held && !IsPlayable(engine, isDrums, s.Token))
                                       .Select(s => $"'{s.Token}'").Distinct().Take(6).ToList();
                        if (bad.Count > 0)
                        {
                            problems.Add($"channel {index} ({instrument}): unrecognised {string.Join(", ", bad)}");
                            if (isDrums) badDrums = true; else badNotes = true;
                        }

                        channels.Add(new Channel(instrument, isDrums, steps, Clamp(ReadNum(c, "volume", 1.0), 0, 1)));
                    }
                }
                if (channels.Count == 0)
                    throw new ArgumentException(
                        "A score needs at least one channel: " +
                        "{\"channels\":[{\"instrument\":\"lead\",\"notes\":\"C4 E4 G4 C5\"}]}");

                if (problems.Count > 0)
                    throw new ArgumentException(
                        "The score has problems: " + string.Join("; ", problems) + "." +
                        Help(engine, unknownInstrument, badNotes, badDrums));

                int longest = channels.Max(c => c.Steps.Count);
                if (longest == 0) throw new ArgumentException("Every channel is empty.");
                if (!channels.Any(c => c.Steps.Any(s => !s.IsRest && !s.Held)))
                    throw new ArgumentException("The score has no notes to play, only rests.");

                long total = (long)longest * repeatCount * stepSamples + tail;
                if (total * 2 > MaxPcmBytes) throw TooLong(total, sampleRate);

                return new Score
                {
                    Engine = engine,
                    Loop = loop,
                    SampleRate = sampleRate,
                    StepSeconds = stepSeconds,
                    StepSamples = stepSamples,
                    RepeatCount = repeatCount,
                    MasterVolume = Clamp(ReadNum(spec, "volume", 0.8), 0, 1),
                    Steps = longest,
                    Channels = channels,
                    TailSamples = tail,
                };
            }
        }

        private static ArgumentException TooLong(long? totalSamples, int sampleRate)
        {
            double limitMinutes = MaxPcmBytes / 2.0 / sampleRate / 60.0;
            string size = totalSamples is long t
                ? $"{t / (double)sampleRate / 60.0:0.#} minutes, {t * 2 / (1024.0 * 1024.0):0} MB of audio"
                : $"over {limitMinutes:0.#} minutes of audio";
            return new ArgumentException(
                $"The score is too long: {size}, and a clip can carry at most {MaxPcmBytes / (1024 * 1024)} MB " +
                $"to the engine (about {limitMinutes:0.#} minutes at {sampleRate} Hz). Shorten it, lower repeat, or raise bpm.");
        }

        private static string Help(Engine engine, bool instruments, bool notes, bool drums)
        {
            var help = "";
            if (instruments)
            {
                help += engine == Engine.SoundFont
                    ? $" Instruments: {string.Join(", ", SoundFontRenderer.KnownInstruments)}, a General MIDI program number 0-127, or drums."
                    : $" Instruments: {string.Join(", ", ChiptuneInstruments)} or drums; piano, strings and the other sampled instruments need \"engine\":\"soundfont\".";
            }
            if (notes)
                help += " A note is a letter A-G, an optional # or b, and an optional octave 0-9 (C4, F#3, Bb5; 4 if omitted); " +
                        "'.' is a rest, '-' holds the slot before it, 'C4*4' lasts four steps.";
            if (drums)
                help += " Drum tokens: " + string.Join(" ", engine == Engine.SoundFont
                    ? SoundFontRenderer.DrumTokens : ChiptuneDrumTokens) + ".";
            return help;
        }

        private static bool IsKnownInstrument(Engine engine, string instrument)
            => engine == Engine.SoundFont
                ? SoundFontRenderer.TryProgramFor(instrument, out _)
                : Array.IndexOf(ChiptuneInstruments, instrument) >= 0;

        private static bool IsPlayable(Engine engine, bool isDrums, string token)
        {
            if (!isDrums) return TryNoteToFrequency(token, out _);
            var lower = token.ToLowerInvariant();
            return engine == Engine.SoundFont
                ? SoundFontRenderer.DrumTokens.Contains(lower)
                : Array.IndexOf(ChiptuneDrumTokens, lower) >= 0;
        }

        private static List<Step> ParseSteps(string notes, long maxSteps, int sampleRate)
        {
            var result = new List<Step>();
            if (string.IsNullOrWhiteSpace(notes)) return result;

            foreach (var raw in notes.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
            {
                if (result.Count > maxSteps) throw TooLong(null, sampleRate);

                var token = raw.Trim();
                if (token == "." || token == "_")
                {
                    result.Add(new Step(token, 1, IsRest: true, Held: false));
                    continue;
                }
                if (token == "-")
                {
                    // A hold continues whatever the slot before it was. After a note that is the
                    // note, so "C4 - - -" is one note four steps long; after a rest it is more
                    // rest. Reaching back past the rest to extend the note before it, as this
                    // used to, filled the rest with sound and left the hold silent.
                    if (result.Count == 0 || result[^1].IsRest)
                    {
                        result.Add(new Step("-", 1, IsRest: true, Held: false));
                        continue;
                    }
                    var last = LastSounding(result);
                    result[last] = result[last] with { Length = result[last].Length + 1 };
                    result.Add(new Step("-", 1, IsRest: false, Held: true));
                    continue;
                }

                int length = 1;
                var star = token.IndexOf('*');
                if (star > 0 && int.TryParse(token.AsSpan(star + 1), NumberStyles.None,
                                             CultureInfo.InvariantCulture, out var n) && n > 0)
                {
                    length = Math.Min(n, 64);
                    token = token.Substring(0, star);
                }

                result.Add(new Step(token, length, IsRest: false, Held: false));
                // A note longer than one step occupies the slots that follow it.
                for (int i = 1; i < length; i++) result.Add(new Step("-", 1, IsRest: false, Held: true));
            }
            if (result.Count > maxSteps) throw TooLong(null, sampleRate);
            return result;
        }

        private static int LastSounding(List<Step> steps)
        {
            for (int i = steps.Count - 1; i >= 0; i--)
                if (!steps[i].Held && !steps[i].IsRest) return i;
            return -1;
        }

        /// <summary>
        /// The sfxr spec for one note of one instrument. Presets rather than a synth the
        /// caller has to design: a score should be about the tune, and a model asked to pick
        /// filter cutoffs per note will spend its attention there instead.
        /// </summary>
        private static JsonElement VoiceFor(Channel channel, string token, double seconds, int sampleRate)
        {
            // Percussion reads its own alphabet; everything else reads note names.
            if (channel.IsDrums)
            {
                string drum = token.ToLowerInvariant() switch
                {
                    "k" or "kick" or "b" =>
                        "{\"wave\":\"sine\",\"freq\":150,\"freq_sweep\":-900,\"punch\":0.9," +
                        "\"attack\":0.001,\"sustain\":0.02,\"decay\":0.12,\"lowpass\":900}",
                    "s" or "snare" =>
                        "{\"wave\":\"noise\",\"freq\":1200,\"lowpass\":3500,\"highpass\":300,\"punch\":0.6," +
                        "\"attack\":0.001,\"sustain\":0.01,\"decay\":0.14}",
                    "h" or "hat" =>
                        "{\"wave\":\"noise\",\"freq\":6000,\"highpass\":4000,\"attack\":0.001," +
                        "\"sustain\":0.005,\"decay\":0.04}",
                    _ =>  // o, openhat — the parse has already refused anything else
                        "{\"wave\":\"noise\",\"freq\":6000,\"highpass\":3500,\"attack\":0.001," +
                        "\"sustain\":0.02,\"decay\":0.25}",
                };
                // A drum is its own envelope, whatever the grid. Handing it the step's length
                // as "duration" rescaled attack, sustain and decay to fit: a hat lasted four
                // times longer at one step per beat than at four.
                return WithTail(drum, $"\"sample_rate\":{sampleRate}");
            }

            TryNoteToFrequency(token, out double freq);

            // Held notes overlap the next step slightly, which is what stops a melody sounding
            // like a row of separate beeps.
            double dur = seconds * 1.05;

            string voice = channel.Instrument switch
            {
                "bass" =>
                    $"{{\"wave\":\"triangle\",\"freq\":{F(freq / 2)},\"attack\":0.005,\"sustain\":{F(dur * 0.5)}," +
                    $"\"decay\":{F(dur * 0.5)},\"lowpass\":1200,\"volume\":0.9}}",
                "pad" or "chord" =>
                    $"{{\"wave\":\"triangle\",\"freq\":{F(freq)},\"attack\":{F(dur * 0.3)},\"sustain\":{F(dur * 0.4)}," +
                    $"\"decay\":{F(dur * 0.3)},\"lowpass\":2200,\"volume\":0.5}}",
                "arp" =>
                    $"{{\"wave\":\"square\",\"freq\":{F(freq)},\"duty\":0.5,\"arpeggio\":1.5," +
                    $"\"arpeggio_delay\":{F(dur * 0.5)},\"attack\":0.002,\"sustain\":{F(dur * 0.4)}," +
                    $"\"decay\":{F(dur * 0.6)},\"lowpass\":4000,\"volume\":0.6}}",
                "pluck" =>
                    $"{{\"wave\":\"saw\",\"freq\":{F(freq)},\"attack\":0.002,\"sustain\":{F(dur * 0.15)}," +
                    $"\"decay\":{F(dur * 0.85)},\"lowpass\":2600,\"resonance\":0.3,\"volume\":0.6}}",
                _ =>  // lead
                    $"{{\"wave\":\"square\",\"freq\":{F(freq)},\"duty\":0.25,\"attack\":0.004," +
                    $"\"sustain\":{F(dur * 0.5)},\"decay\":{F(dur * 0.5)},\"lowpass\":3800,\"volume\":0.7}}",
            };
            return WithTail(voice, $"\"duration\":{F(Math.Max(0.02, dur))},\"sample_rate\":{sampleRate}");
        }

        private static JsonElement WithTail(string json, string fields)
            => JsonSerializer.Deserialize<JsonElement>(json.Substring(0, json.Length - 1) + "," + fields + "}");

        private static string F(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

        /// <summary>
        /// "C4", "F#3", "Bb5" — name, optional accidental, optional octave 0-9 (4 if absent).
        /// A4 is 440 Hz, and everything else follows from equal temperament. Anything after the
        /// accidentals has to be the octave and nothing else: "C5x" and "C5," used to fall back
        /// to octave 4 and play C4, a wrong note nobody would ever trace back to a stray comma.
        /// </summary>
        public static bool TryNoteToFrequency(string token, out double frequency)
        {
            frequency = 0;
            if (string.IsNullOrWhiteSpace(token)) return false;

            var s = token.Trim();
            int semitone = char.ToUpperInvariant(s[0]) switch
            {
                'C' => 0, 'D' => 2, 'E' => 4, 'F' => 5, 'G' => 7, 'A' => 9, 'B' => 11,
                _ => -1,
            };
            if (semitone < 0) return false;

            int i = 1;
            while (i < s.Length && (s[i] == '#' || s[i] == 'b' || s[i] == 'B'))
            {
                // 'b' is a flat only after the letter; "Bb" is B-flat, "B" alone is B.
                if (s[i] == '#') semitone++;
                else semitone--;
                i++;
            }

            int octave = 4;
            if (i < s.Length
                && (!int.TryParse(s.AsSpan(i), NumberStyles.None, CultureInfo.InvariantCulture, out octave) || octave > 9))
                return false;

            int midi = 12 * (octave + 1) + semitone;
            frequency = 440.0 * Math.Pow(2, (midi - 69) / 12.0);
            return frequency > 0 && frequency < 20000;
        }

        private static double ReadNum(JsonElement obj, string name, double fallback)
            => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v)
               && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : fallback;

        private static int ReadInt(JsonElement obj, string name, int fallback)
            => (int)Math.Round(Clamp(ReadNum(obj, name, fallback), int.MinValue, int.MaxValue));

        private static string ReadStr(JsonElement obj, string name, string fallback)
            => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v)
               && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? fallback) : fallback;

        private static int ClampInt(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
        private static double Clamp(double v, double lo, double hi) => v < lo ? lo : (v > hi ? hi : v);
    }
}
