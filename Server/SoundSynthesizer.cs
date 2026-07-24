using System;
using System.IO;
using System.Text.Json;

namespace AkerMcp.Server
{
    /// <summary>
    /// Turns an AI-authored "sound-spec" (a small jsfxr-style JSON) into 16-bit mono PCM
    /// WAV bytes — the audio analog of <see cref="SpriteRasterizer"/>. Pure-managed (no
    /// dependencies): a waveform oscillator + ADSR-ish envelope + optional frequency sweep
    /// and vibrato. The engine receives a ready WAV and never needs its own synth.
    /// </summary>
    public static class SoundSynthesizer
    {
        public static byte[] RenderToWav(JsonElement spec)
        {
            int sampleRate = Clamp((int)ReadNum(spec, "sample_rate", 44100), 8000, 48000);

            string wave = ReadStr(spec, "wave", "square").ToLowerInvariant();
            double freq = ReadNum(spec, "freq", 440);
            double freqSweep = ReadNum(spec, "freq_sweep", 0);      // Hz added per second
            double vibDepth = ReadNum(spec, "vibrato_depth", 0);    // 0..1 fraction of freq
            double vibRate = ReadNum(spec, "vibrato_rate", 0);      // Hz
            double volume = Clamp(ReadNum(spec, "volume", 0.5), 0, 1);

            // Envelope (seconds). Total duration defaults to attack+sustain+decay, or an
            // explicit "duration" that scales the three if given.
            double attack = Math.Max(0, ReadNum(spec, "attack", 0.01));
            double sustain = Math.Max(0, ReadNum(spec, "sustain", 0.12));
            double decay = Math.Max(0, ReadNum(spec, "decay", 0.12));
            double envTotal = attack + sustain + decay;
            if (envTotal <= 0) { sustain = 0.15; envTotal = 0.15; }

            double duration = ReadNum(spec, "duration", envTotal);
            duration = Clamp(duration, 0.02, 10.0);
            // Scale the envelope segments to fill the requested duration.
            double scale = duration / envTotal;
            attack *= scale; sustain *= scale; decay *= scale;

            int total = (int)(duration * sampleRate);
            if (total < 1) total = 1;

            var samples = new short[total];
            double phase = 0;
            double f = freq;
            var rng = new Random(12345); // deterministic noise (repeatable SFX)

            for (int i = 0; i < total; i++)
            {
                double t = (double)i / sampleRate;

                // Instantaneous frequency: base + linear sweep + vibrato.
                double instF = f;
                if (vibDepth > 0 && vibRate > 0)
                    instF *= 1.0 + vibDepth * Math.Sin(2 * Math.PI * vibRate * t);
                if (instF < 0) instF = 0;

                phase += instF / sampleRate;
                double ph = phase - Math.Floor(phase); // 0..1

                double sample = wave switch
                {
                    "sine" => Math.Sin(2 * Math.PI * ph),
                    "square" => ph < 0.5 ? 1.0 : -1.0,
                    "saw" => 2.0 * ph - 1.0,
                    "triangle" => 4.0 * Math.Abs(ph - 0.5) - 1.0,
                    "noise" => rng.NextDouble() * 2.0 - 1.0,
                    _ => ph < 0.5 ? 1.0 : -1.0,
                };

                // Envelope amplitude.
                double amp;
                if (t < attack) amp = attack > 0 ? t / attack : 1.0;
                else if (t < attack + sustain) amp = 1.0;
                else
                {
                    double d = t - attack - sustain;
                    amp = decay > 0 ? Math.Max(0, 1.0 - d / decay) : 0.0;
                }

                double v = sample * amp * volume;
                if (v > 1) v = 1; else if (v < -1) v = -1;
                samples[i] = (short)(v * short.MaxValue);

                // Advance the linear sweep.
                f += freqSweep / sampleRate;
                if (f < 0) f = 0;
            }

            return WriteWav(samples, sampleRate);
        }

        private static byte[] WriteWav(short[] samples, int sampleRate)
        {
            int dataSize = samples.Length * 2;
            using var ms = new MemoryStream(44 + dataSize);
            using var w = new BinaryWriter(ms);

            void Tag(string s) { foreach (char c in s) w.Write((byte)c); }

            Tag("RIFF");
            w.Write(36 + dataSize);
            Tag("WAVE");
            Tag("fmt ");
            w.Write(16);                 // PCM fmt chunk size
            w.Write((short)1);           // audio format = PCM
            w.Write((short)1);           // channels = mono
            w.Write(sampleRate);
            w.Write(sampleRate * 2);     // byte rate (mono, 16-bit)
            w.Write((short)2);           // block align
            w.Write((short)16);          // bits per sample
            Tag("data");
            w.Write(dataSize);
            foreach (var s in samples) w.Write(s);

            w.Flush();
            return ms.ToArray();
        }

        private static double ReadNum(JsonElement obj, string name, double fallback)
            => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v)
               && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : fallback;

        private static string ReadStr(JsonElement obj, string name, string fallback)
            => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v)
               && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? fallback) : fallback;

        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
        private static double Clamp(double v, double lo, double hi) => v < lo ? lo : (v > hi ? hi : v);
    }
}
