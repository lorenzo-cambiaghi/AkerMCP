using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AkerMcp.Server
{
    /// <summary>
    /// Turns a short chiptune score into 16-bit mono PCM WAV bytes: a tracker over the sfxr
    /// voice in <see cref="SoundSynthesizer"/>, so every note inherits the same filters and
    /// envelopes rather than growing a second, worse synth beside it.
    ///
    /// <para>Chiptune rather than General MIDI on purpose. A SoundFont would mean shipping or
    /// fetching tens of megabytes, and the good ones carry licences that reach into the audio
    /// they produce — the obvious small one is GPL, which for a tool whose output lands in
    /// someone else's game is the wrong risk to take. Square waves cost nothing, carry no
    /// licence at all, and are the idiom a prototype is expected to sound like.</para>
    ///
    /// <para>The score is written the way a person hums it: tokens separated by spaces, one
    /// step each. <c>C4 E4 G4 .</c> is three notes and a rest; <c>-</c> holds the note before
    /// it; <c>C4*4</c> is a note four steps long. A drum channel spells its tokens
    /// <c>k s h o</c>. Everything else — tempo, how long a step lasts, which voice plays a
    /// channel — is named once at the top.</para>
    /// </summary>
    public static class MusicSequencer
    {
        /// <summary>
        /// Render a score. Synchronous entry point, kept for callers that cannot await; the
        /// SoundFont engine needs a file that may have to be fetched, so it goes through
        /// <see cref="RenderToWavAsync"/> instead.
        /// </summary>
        public static byte[] RenderToWav(JsonElement spec)
            => RenderToWavAsync(spec, CancellationToken.None).GetAwaiter().GetResult();

        public static async Task<byte[]> RenderToWavAsync(JsonElement spec, CancellationToken ct)
        {
            var engine = ReadStr(spec, "engine", "chiptune").ToLowerInvariant();
            if (engine is "soundfont" or "gm" or "instruments")
            {
                var (path, error) = await SoundFontLibrary.Resolve(ct).ConfigureAwait(false);
                if (path == null) throw new InvalidOperationException(error ?? "No SoundFont available.");
                return RenderWithSoundFont(spec, path);
            }
            return RenderChiptune(spec);
        }

        private static byte[] RenderChiptune(JsonElement spec)
        {
            int sampleRate = ClampInt(ReadInt(spec, "sample_rate", 44100), 8000, 48000);
            double bpm = Clamp(ReadNum(spec, "bpm", 120), 20, 300);
            int stepsPerBeat = ClampInt(ReadInt(spec, "steps_per_beat", 4), 1, 16);
            double stepSeconds = 60.0 / bpm / stepsPerBeat;
            double masterVolume = Clamp(ReadNum(spec, "volume", 0.8), 0, 1);
            int repeatCount = ClampInt(ReadInt(spec, "repeat", 1), 1, 8);

            var channels = new List<(string instrument, string notes, double volume)>();
            if (spec.ValueKind == JsonValueKind.Object
                && spec.TryGetProperty("channels", out var chans) && chans.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in chans.EnumerateArray())
                {
                    if (c.ValueKind != JsonValueKind.Object) continue;
                    channels.Add((
                        ReadStr(c, "instrument", "lead").ToLowerInvariant(),
                        ReadStr(c, "notes", ""),
                        Clamp(ReadNum(c, "volume", 1.0), 0, 1)));
                }
            }
            if (channels.Count == 0)
                throw new ArgumentException(
                    "A score needs at least one channel: " +
                    "{\"channels\":[{\"instrument\":\"lead\",\"notes\":\"C4 E4 G4 C5\"}]}");

            // Length is the longest channel, so a two-bar bass under an eight-bar lead simply
            // stops rather than truncating the piece.
            int steps = 0;
            var parsed = new List<List<Step>>();
            foreach (var (_, notes, _) in channels)
            {
                var seq = ParseSteps(notes);
                parsed.Add(seq);
                steps = Math.Max(steps, seq.Count);
            }
            if (steps == 0) throw new ArgumentException("Every channel is empty.");

            int stepSamples = Math.Max(1, (int)(stepSeconds * sampleRate));
            // A tail so the last note's decay is not cut off mid-air.
            int total = steps * repeatCount * stepSamples + sampleRate / 2;
            var mix = new double[total];

            for (int c = 0; c < channels.Count; c++)
            {
                var (instrument, _, channelVolume) = channels[c];
                var seq = parsed[c];

                for (int pass = 0; pass < repeatCount; pass++)
                {
                    for (int s = 0; s < seq.Count; s++)
                    {
                        var step = seq[s];
                        if (step.IsRest || step.Held) continue;

                        double seconds = step.Steps * stepSeconds;
                        var voice = VoiceFor(instrument, step.Token, seconds, sampleRate);
                        if (voice == null) continue;

                        var rendered = SoundSynthesizer.RenderSamples(voice.Value, out _);
                        int at = (pass * seq.Count + s) * stepSamples;
                        for (int i = 0; i < rendered.Length && at + i < total; i++)
                            mix[at + i] += rendered[i] / (double)short.MaxValue * channelVolume;
                    }
                }
            }

            // Normalize instead of clipping: summing four channels overshoots almost always,
            // and a clipped chiptune is the harshness this whole exercise set out to remove.
            double peak = 0;
            foreach (var v in mix) peak = Math.Max(peak, Math.Abs(v));
            double gain = peak > 0 ? masterVolume / peak : 1.0;

            var samples = new short[total];
            for (int i = 0; i < total; i++)
                samples[i] = (short)(Math.Clamp(mix[i] * gain, -1, 1) * short.MaxValue);

            return SoundSynthesizer.WriteWav(samples, sampleRate);
        }

        /// <summary>
        /// The same score, played by sampled instruments. Only the parsing is shared — which
        /// is the point: a tune written for the tracker plays here unchanged, and the choice
        /// of engine is a property of the render, not of the music.
        /// </summary>
        private static byte[] RenderWithSoundFont(JsonElement spec, string soundFontPath)
        {
            int sampleRate = ClampInt(ReadInt(spec, "sample_rate", 44100), 8000, 48000);
            double bpm = Clamp(ReadNum(spec, "bpm", 120), 20, 300);
            int stepsPerBeat = ClampInt(ReadInt(spec, "steps_per_beat", 4), 1, 16);
            double masterVolume = Clamp(ReadNum(spec, "volume", 0.8), 0, 1);
            int repeatCount = ClampInt(ReadInt(spec, "repeat", 1), 1, 8);

            var channels = new List<(string, IReadOnlyList<(string, int, bool, bool)>, double)>();
            if (spec.ValueKind == JsonValueKind.Object
                && spec.TryGetProperty("channels", out var chans) && chans.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in chans.EnumerateArray())
                {
                    if (c.ValueKind != JsonValueKind.Object) continue;
                    var steps = ParseSteps(ReadStr(c, "notes", ""));
                    var plain = new List<(string, int, bool, bool)>(steps.Count);
                    foreach (var s in steps) plain.Add((s.Token, s.Steps, s.Held, s.IsRest));
                    channels.Add((
                        ReadStr(c, "instrument", "lead").ToLowerInvariant(),
                        plain,
                        Clamp(ReadNum(c, "volume", 1.0), 0, 1)));
                }
            }
            if (channels.Count == 0)
                throw new ArgumentException("A score needs at least one channel.");

            return SoundFontRenderer.Render(
                soundFontPath, channels, 60.0 / bpm / stepsPerBeat, repeatCount, sampleRate, masterVolume);
        }

        private readonly struct Step
        {
            public Step(string token, int steps, bool isRest, bool held)
            { Token = token; Steps = steps; IsRest = isRest; Held = held; }
            public string Token { get; }
            public int Steps { get; }
            public bool IsRest { get; }
            /// <summary>A '-' slot: the previous note keeps sounding, nothing retriggers.</summary>
            public bool Held { get; }
        }

        private static List<Step> ParseSteps(string notes)
        {
            var result = new List<Step>();
            if (string.IsNullOrWhiteSpace(notes)) return result;

            foreach (var raw in notes.Split(new[] { ' ', '\t', '\n', '\r', '|' },
                                            StringSplitOptions.RemoveEmptyEntries))
            {
                var token = raw.Trim();
                if (token == "." || token == "_")
                {
                    result.Add(new Step(token, 1, isRest: true, held: false));
                    continue;
                }
                if (token == "-")
                {
                    // Extend the note before it, so "C4 - - -" is one note four steps long.
                    if (result.Count > 0)
                    {
                        var last = LastSounding(result);
                        if (last >= 0)
                            result[last] = new Step(result[last].Token, result[last].Steps + 1, false, false);
                    }
                    result.Add(new Step("-", 1, isRest: false, held: true));
                    continue;
                }

                int length = 1;
                var star = token.IndexOf('*');
                if (star > 0 && int.TryParse(token.Substring(star + 1), NumberStyles.Integer,
                                             CultureInfo.InvariantCulture, out var n) && n > 0)
                {
                    length = Math.Min(n, 64);
                    token = token.Substring(0, star);
                }

                result.Add(new Step(token, length, isRest: false, held: false));
                // A note longer than one step occupies the slots that follow it.
                for (int i = 1; i < length; i++) result.Add(new Step("-", 1, false, held: true));
            }
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
        private static JsonElement? VoiceFor(string instrument, string token, double seconds, int sampleRate)
        {
            // Percussion reads its own alphabet; everything else reads note names.
            if (instrument is "drums" or "drum" or "perc")
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
                    "o" or "openhat" =>
                        "{\"wave\":\"noise\",\"freq\":6000,\"highpass\":3500,\"attack\":0.001," +
                        "\"sustain\":0.02,\"decay\":0.25}",
                    _ => "",
                };
                if (drum.Length == 0) return null;
                return WithDuration(drum, Math.Min(seconds * 1.5, 1.0), sampleRate);
            }

            if (!TryNoteToFrequency(token, out double freq)) return null;

            // Held notes overlap the next step slightly, which is what stops a melody sounding
            // like a row of separate beeps.
            double dur = Math.Min(seconds * 1.05, 10.0);

            string voice = instrument switch
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
            return WithDuration(voice, dur, sampleRate);
        }

        private static JsonElement WithDuration(string json, double seconds, int sampleRate)
        {
            var withTail = json.Substring(0, json.Length - 1) +
                           $",\"duration\":{F(Math.Max(0.02, seconds))},\"sample_rate\":{sampleRate}}}";
            return JsonSerializer.Deserialize<JsonElement>(withTail);
        }

        private static string F(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

        /// <summary>
        /// "C4", "F#3", "Bb5" — name, optional accidental, optional octave (4 if absent).
        /// A4 is 440 Hz, and everything else follows from equal temperament.
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
            if (i < s.Length && int.TryParse(s.Substring(i), NumberStyles.Integer,
                                             CultureInfo.InvariantCulture, out var oct))
                octave = Math.Clamp(oct, 0, 9);

            int midi = 12 * (octave + 1) + semitone;
            frequency = 440.0 * Math.Pow(2, (midi - 69) / 12.0);
            return frequency > 0 && frequency < 20000;
        }

        private static double ReadNum(JsonElement obj, string name, double fallback)
            => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v)
               && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : fallback;

        private static int ReadInt(JsonElement obj, string name, int fallback)
            => (int)Math.Round(ReadNum(obj, name, fallback));

        private static string ReadStr(JsonElement obj, string name, string fallback)
            => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v)
               && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? fallback) : fallback;

        private static int ClampInt(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
        private static double Clamp(double v, double lo, double hi) => v < lo ? lo : (v > hi ? hi : v);
    }
}
