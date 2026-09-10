using System;
using System.Collections.Generic;
using System.Globalization;
using MeltySynth;

namespace AkerMcp.Server
{
    /// <summary>
    /// Renders a score with real instruments, through MeltySynth and a General MIDI SoundFont.
    ///
    /// <para>The same score the chiptune tracker plays: only the voice changes, so a model
    /// that has written a tune does not rewrite it to hear it another way. What changes is
    /// what an instrument name means — <c>lead</c> becomes a program number rather than a
    /// square wave — and that the notes are events on a timeline instead of rendered
    /// one-by-one, because a sampled instrument's release has to overlap what follows it.</para>
    ///
    /// <para>MeltySynth writes into buffers the caller owns and never touches an audio device,
    /// which is the whole reason it is here: this server produces WAV bytes for an engine to
    /// import, and a library that wants to play sound would have to be talked out of it.</para>
    /// </summary>
    internal static class SoundFontRenderer
    {
        private readonly record struct NoteEvent(int Sample, bool On, int Channel, int Key, int Velocity);

        /// <summary>
        /// General MIDI programs for the instrument names a score already uses, plus the ones
        /// worth having by name. A number passes through, so anything in the standard set is
        /// reachable without this table growing to 128 rows.
        /// </summary>
        private static readonly Dictionary<string, int> Programs = new(StringComparer.OrdinalIgnoreCase)
        {
            // The chiptune vocabulary, mapped to something that plays the same role.
            ["lead"] = 80,      // square lead — the same idea, sampled
            ["bass"] = 33,      // fingered bass
            ["pad"] = 89,       // warm pad
            ["arp"] = 81,       // saw lead
            ["pluck"] = 45,     // pizzicato strings
            // Names a score is likely to ask for directly.
            ["piano"] = 0,
            ["epiano"] = 4,
            ["harpsichord"] = 6,
            ["marimba"] = 12,
            ["organ"] = 19,
            ["guitar"] = 24,
            ["guitar_clean"] = 27,
            ["guitar_dist"] = 30,
            ["acoustic_bass"] = 32,
            ["slap_bass"] = 36,
            ["strings"] = 48,
            ["ensemble"] = 49,
            ["choir"] = 52,
            ["trumpet"] = 56,
            ["trombone"] = 57,
            ["brass"] = 61,
            ["sax"] = 65,
            ["oboe"] = 68,
            ["clarinet"] = 71,
            ["flute"] = 73,
            ["ocarina"] = 79,
            ["harp"] = 46,
            ["timpani"] = 47,
            ["bell"] = 14,
        };

        /// <summary>Percussion tokens on MIDI channel 10, where the key IS the drum.</summary>
        private static readonly Dictionary<string, int> DrumKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            ["k"] = 36, ["kick"] = 36, ["b"] = 36,
            ["s"] = 38, ["snare"] = 38,
            ["h"] = 42, ["hat"] = 42,
            ["o"] = 46, ["openhat"] = 46,
            ["c"] = 49, ["crash"] = 49,
            ["t"] = 45, ["tom"] = 45,
            ["r"] = 51, ["ride"] = 51,
        };

        public static byte[] Render(
            string soundFontPath,
            IReadOnlyList<(string instrument, IReadOnlyList<(string token, int steps, bool held, bool rest)> steps, double volume)> channels,
            double stepSeconds, int repeatCount, int sampleRate, double masterVolume)
        {
            var synthesizer = new Synthesizer(soundFontPath, sampleRate);

            var events = new List<NoteEvent>();
            int longest = 0;
            int midiChannel = 0;

            for (int c = 0; c < channels.Count; c++)
            {
                var (instrument, steps, channelVolume) = channels[c];
                bool isDrums = instrument is "drums" or "drum" or "perc";

                // Channel 9 is percussion in General MIDI and nothing else may use it; melodic
                // parts take the remaining channels in order and skip over it.
                int ch = isDrums ? 9 : NextMelodicChannel(ref midiChannel);
                if (!isDrums) synthesizer.ProcessMidiMessage(ch, 0xC0, ProgramFor(instrument), 0);

                // Volume as a MIDI controller, so the synthesiser mixes rather than the caller
                // scaling samples after the fact and losing the instrument's own dynamics.
                synthesizer.ProcessMidiMessage(ch, 0xB0, 7, (int)(Math.Clamp(channelVolume, 0, 1) * 127));

                for (int pass = 0; pass < repeatCount; pass++)
                {
                    for (int s = 0; s < steps.Count; s++)
                    {
                        var step = steps[s];
                        if (step.rest || step.held) continue;

                        int key = isDrums
                            ? (DrumKeys.TryGetValue(step.token, out var d) ? d : -1)
                            : (MusicSequencer.TryNoteToFrequency(step.token, out var hz) ? KeyOf(hz) : -1);
                        if (key < 0) continue;

                        int start = (int)((pass * steps.Count + s) * stepSeconds * sampleRate);
                        // A drum is a one-shot: hold it briefly and let the sample ring out.
                        double heldSeconds = isDrums ? Math.Min(stepSeconds, 0.12) : step.steps * stepSeconds * 0.98;
                        int end = start + Math.Max(1, (int)(heldSeconds * sampleRate));

                        events.Add(new NoteEvent(start, true, ch, key, isDrums ? 110 : 96));
                        events.Add(new NoteEvent(end, false, ch, key, 0));
                        longest = Math.Max(longest, end);
                    }
                }
            }

            if (events.Count == 0)
                throw new ArgumentException("The score has no playable notes for the SoundFont engine.");

            events.Sort((a, b) => a.Sample != b.Sample ? a.Sample.CompareTo(b.Sample) : (a.On ? 1 : -1));

            // A tail for the release of the last note; a sampled piano rings far longer than a
            // square wave, so this is seconds rather than the tracker's half-second.
            int total = longest + sampleRate * 2;
            var left = new float[total];
            var right = new float[total];

            int cursor = 0, index = 0;
            while (cursor < total)
            {
                int nextEvent = index < events.Count ? Math.Min(events[index].Sample, total) : total;
                int block = nextEvent - cursor;
                if (block > 0)
                {
                    // Render straight into the destination: MeltySynth fills any span it is given.
                    synthesizer.Render(left.AsSpan(cursor, block), right.AsSpan(cursor, block));
                    cursor += block;
                }

                while (index < events.Count && events[index].Sample <= cursor)
                {
                    var e = events[index++];
                    if (e.On) synthesizer.NoteOn(e.Channel, e.Key, e.Velocity);
                    else synthesizer.NoteOff(e.Channel, e.Key);
                }

                if (index >= events.Count && block <= 0) break;
            }

            // Sum to mono — the engines import a mono clip today — then normalise, for the same
            // reason the tracker does: several instruments at once overshoot, and a clipped
            // placeholder is worse than a quiet one.
            var mix = new double[total];
            double peak = 0;
            for (int i = 0; i < total; i++)
            {
                mix[i] = (left[i] + right[i]) * 0.5;
                peak = Math.Max(peak, Math.Abs(mix[i]));
            }
            double gain = peak > 0 ? Math.Clamp(masterVolume, 0, 1) / peak : 1.0;

            var samples = new short[total];
            for (int i = 0; i < total; i++)
                samples[i] = (short)(Math.Clamp(mix[i] * gain, -1, 1) * short.MaxValue);

            return SoundSynthesizer.WriteWav(samples, sampleRate);
        }

        private static int NextMelodicChannel(ref int next)
        {
            if (next == 9) next = 10;          // never hand out the percussion channel
            if (next > 15) next = 0;
            return next++;
        }

        private static int ProgramFor(string instrument)
        {
            if (int.TryParse(instrument, NumberStyles.Integer, CultureInfo.InvariantCulture, out var program))
                return Math.Clamp(program, 0, 127);
            return Programs.TryGetValue(instrument, out var p) ? p : 0;   // grand piano
        }

        /// <summary>Frequency back to a MIDI key, so note parsing stays in one place.</summary>
        private static int KeyOf(double frequency)
            => Math.Clamp((int)Math.Round(69 + 12 * Math.Log2(frequency / 440.0)), 0, 127);

        /// <summary>The instrument names this renderer knows, for the error when one is unknown.</summary>
        public static IEnumerable<string> KnownInstruments => Programs.Keys;
    }
}
