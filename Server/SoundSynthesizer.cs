using System;
using System.IO;
using System.Text.Json;

namespace AkerMcp.Server
{
    /// <summary>
    /// Turns an AI-authored "sound-spec" (a small jsfxr-style JSON) into 16-bit mono PCM
    /// WAV bytes — the audio analog of <see cref="SpriteRasterizer"/>. Pure-managed (no
    /// dependencies).
    ///
    /// <para>It used to be sfxr in name only: five oscillators, a trapezoid envelope, a
    /// linear sweep and vibrato. What that leaves out is exactly what separates a beep from
    /// a game sound, and one omission was a plain bug — the noise oscillator ignored
    /// frequency entirely, so "noise 200" and "noise 4000" produced the same white hiss, and
    /// a hit, a footstep and an explosion all came out identical. The tool's own documented
    /// recipe passed a frequency that did nothing.</para>
    ///
    /// <para>Now it carries the rest of sfxr: pitched noise, sfxr's own low-pass with
    /// resonance and a high-pass, square duty cycle and its sweep, an arpeggio jump, an
    /// initial punch, envelope retriggering, and a phaser. The filter matters most — a raw
    /// square holds every harmonic up to Nyquist, which is what "harsh" means when someone
    /// says a placeholder is unpleasant.</para>
    /// </summary>
    public static class SoundSynthesizer
    {
        public static byte[] RenderToWav(JsonElement spec)
        {
            int sampleRate = Clamp((int)ReadNum(spec, "sample_rate", 44100), 8000, 48000);

            string wave = ReadStr(spec, "wave", "square").ToLowerInvariant();
            double baseFreq = ReadNum(spec, "freq", 440);
            double freqSweep = ReadNum(spec, "freq_sweep", 0);      // Hz added per second
            double vibDepth = ReadNum(spec, "vibrato_depth", 0);    // 0..1 fraction of freq
            double vibRate = ReadNum(spec, "vibrato_rate", 0);      // Hz
            double volume = Clamp(ReadNum(spec, "volume", 0.5), 0, 1);

            // Square-wave character: 0.5 is a plain square, away from it the tone thins out.
            double duty = Clamp(ReadNum(spec, "duty", 0.5), 0.01, 0.99);
            double dutySweep = ReadNum(spec, "duty_sweep", 0);      // per second

            // The pitch jump that makes a power-up sound like a power-up.
            double arpeggio = ReadNum(spec, "arpeggio", 1.0);       // frequency multiplier
            double arpeggioDelay = Math.Max(0, ReadNum(spec, "arpeggio_delay", 0.08));

            double punch = Clamp(ReadNum(spec, "punch", 0), 0, 1);  // extra amplitude at the start
            double repeat = Math.Max(0, ReadNum(spec, "repeat", 0));// retriggers per second

            double resonance = Clamp(ReadNum(spec, "resonance", 0), 0, 1);
            double lowpassHz = ReadNum(spec, "lowpass", 0);         // 0 = open
            double lowpassSweep = ReadNum(spec, "lowpass_sweep", 0);// octaves per second
            double highpassHz = Math.Max(0, ReadNum(spec, "highpass", 0));

            double phaserOffset = ReadNum(spec, "phaser_offset", 0);  // 0..1 of the buffer
            double phaserSweep = ReadNum(spec, "phaser_sweep", 0);    // per second

            // Envelope (seconds). Total duration defaults to attack+sustain+decay, or an
            // explicit "duration" that scales the three if given.
            double attack = Math.Max(0, ReadNum(spec, "attack", 0.01));
            double sustain = Math.Max(0, ReadNum(spec, "sustain", 0.12));
            double decay = Math.Max(0, ReadNum(spec, "decay", 0.12));
            double envTotal = attack + sustain + decay;
            if (envTotal <= 0) { sustain = 0.15; envTotal = 0.15; }

            double duration = ReadNum(spec, "duration", envTotal);
            duration = Clamp(duration, 0.02, 10.0);
            double scale = duration / envTotal;
            attack *= scale; sustain *= scale; decay *= scale;

            // Exponential by default: a linear fade is the sound of an envelope, not of a
            // thing that stopped. "linear" keeps what earlier specs produced.
            bool linearDecay = ReadStr(spec, "decay_curve", "exponential")
                .Equals("linear", StringComparison.OrdinalIgnoreCase);

            int total = (int)(duration * sampleRate);
            if (total < 1) total = 1;

            var samples = new short[total];
            var rng = new Random(12345);              // deterministic noise (repeatable SFX)

            // sfxr's noise is a 32-slot table resampled by the oscillator's own phase, which
            // is what gives it a pitch: refilled once per cycle, so a low frequency is a
            // rumble and a high one a hiss. Sampling Random() per output sample instead —
            // what this did — is white noise at any setting.
            var noiseTable = new double[32];
            for (int i = 0; i < noiseTable.Length; i++) noiseTable[i] = rng.NextDouble() * 2 - 1;

            // sfxr's filter pair: a resonant low-pass followed by a high-pass.
            double fltp = 0, fltdp = 0, fltphp = 0;
            double phaserBufferLength = 1024;
            var phaserBuffer = new double[(int)phaserBufferLength];
            int phaserPos = 0;

            double phase = 0, f = baseFreq, dutyNow = duty;
            double periodStart = 0;                    // when the current retrigger began
            double lowpassNow = lowpassHz;
            double phaserNow = phaserOffset;
            bool arpeggioDone = false;

            for (int i = 0; i < total; i++)
            {
                double t = (double)i / sampleRate;

                // Retrigger: sfxr's repeat restarts pitch, duty and the envelope clock.
                if (repeat > 0 && t - periodStart >= 1.0 / repeat)
                {
                    periodStart = t;
                    f = baseFreq;
                    dutyNow = duty;
                    arpeggioDone = false;
                    lowpassNow = lowpassHz;
                }
                double te = t - periodStart;           // time inside the current trigger

                if (!arpeggioDone && arpeggio != 1.0 && te >= arpeggioDelay)
                {
                    f *= arpeggio;
                    arpeggioDone = true;
                }

                double instF = f;
                if (vibDepth > 0 && vibRate > 0)
                    instF *= 1.0 + vibDepth * Math.Sin(2 * Math.PI * vibRate * t);
                if (instF < 0) instF = 0;

                double prevPhase = phase;
                phase += instF / sampleRate;
                double ph = phase - Math.Floor(phase);

                // A completed cycle refills the noise table: this is the resampling that
                // turns white noise into a pitched one.
                if (wave == "noise" && Math.Floor(phase) != Math.Floor(prevPhase))
                    for (int n = 0; n < noiseTable.Length; n++) noiseTable[n] = rng.NextDouble() * 2 - 1;

                double sample = wave switch
                {
                    "sine" => Math.Sin(2 * Math.PI * ph),
                    "square" => ph < dutyNow ? 1.0 : -1.0,
                    "saw" => 2.0 * ph - 1.0,
                    "triangle" => 4.0 * Math.Abs(ph - 0.5) - 1.0,
                    "noise" => noiseTable[(int)(ph * noiseTable.Length) % noiseTable.Length],
                    _ => ph < dutyNow ? 1.0 : -1.0,
                };

                // ---- filters (sfxr's, so the character is the one people expect) ----
                if (lowpassNow > 0)
                {
                    double fltw = Clamp(2.0 * Math.Sin(Math.PI * Math.Min(lowpassNow, sampleRate * 0.49) / sampleRate), 0, 1);
                    double fltdmp = Math.Min(0.8, 5.0 / (1.0 + resonance * resonance * 20) * (0.01 + fltw));
                    fltp += fltdp;
                    fltdp += (sample - fltp) * fltw;
                    fltdp -= fltdp * fltdmp;
                    double before = fltphp;
                    fltphp += fltp - before;           // carries the low-passed signal forward
                    sample = fltp;
                }
                if (highpassHz > 0)
                {
                    double flthp = Clamp(2.0 * Math.Sin(Math.PI * Math.Min(highpassHz, sampleRate * 0.49) / sampleRate), 0, 1);
                    fltphp += sample - fltp;
                    fltphp -= fltphp * flthp;
                    sample = fltphp;
                }

                if (phaserOffset != 0 || phaserSweep != 0)
                {
                    phaserBuffer[phaserPos & 1023] = sample;
                    int offset = (int)(Math.Abs(phaserNow) * (phaserBufferLength - 1));
                    sample += phaserBuffer[(phaserPos - offset + 1024) & 1023];
                    sample *= 0.5;
                    phaserPos++;
                }

                // ---- envelope ----
                double amp;
                if (te < attack) amp = attack > 0 ? te / attack : 1.0;
                else if (te < attack + sustain)
                {
                    // Punch is a decaying overshoot across the sustain, not a constant boost.
                    double s = sustain > 0 ? (te - attack) / sustain : 1.0;
                    amp = 1.0 + (1.0 - s) * 2.0 * punch;
                }
                else
                {
                    double d = decay > 0 ? (te - attack - sustain) / decay : 1.0;
                    d = Clamp(d, 0, 1);
                    amp = linearDecay ? 1.0 - d : Math.Exp(-5.0 * d) - Math.Exp(-5.0) * d;
                    if (amp < 0) amp = 0;
                }

                double v = sample * amp * volume;
                if (v > 1) v = 1; else if (v < -1) v = -1;
                samples[i] = (short)(v * short.MaxValue);

                // Per-sample parameter sweeps.
                f += freqSweep / sampleRate;
                if (f < 0) f = 0;
                dutyNow = Clamp(dutyNow + dutySweep / sampleRate, 0.01, 0.99);
                if (lowpassNow > 0 && lowpassSweep != 0)
                    lowpassNow = Math.Max(20, lowpassNow * Math.Pow(2, lowpassSweep / sampleRate));
                phaserNow = Clamp(phaserNow + phaserSweep / sampleRate, 0, 0.99);
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
