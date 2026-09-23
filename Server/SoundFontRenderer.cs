using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using MeltySynth;

// The loopback suite checks the SoundFont timeline without the 151 MB font: the event order is
// where this renderer broke, and it is pure arithmetic that needs no synthesiser to exercise.
[assembly: InternalsVisibleTo("AkerMcp.Loopback")]

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
        /// <summary>MeltySynth refuses a lower rate outright.</summary>
        public const int MinSampleRate = 16000;

        /// <summary>
        /// Room after the last step for the release of the last notes; a sampled piano rings far
        /// longer than a square wave, so this is seconds rather than the tracker's half-second.
        /// </summary>
        public const double TailSeconds = 2.0;

        internal readonly record struct NoteEvent(int Sample, bool On, int Channel, int Key, int Velocity);

        internal readonly record struct ChannelSetup(int MidiChannel, int? Program, double Volume);

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
            ["chord"] = 89,     // the tracker's other name for pad
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

        /// <summary>The instrument names this renderer knows, for the error when one is unknown.</summary>
        public static IEnumerable<string> KnownInstruments => Programs.Keys;

        /// <summary>The drum alphabet on this engine, for the same error.</summary>
        public static ICollection<string> DrumTokens => DrumKeys.Keys;

        public static byte[] Render(string soundFontPath, MusicSequencer.Score score)
        {
            var setups = new List<ChannelSetup>();
            var events = Timeline(score, setups);

            var synthesizer = new Synthesizer(soundFontPath, score.SampleRate);
            foreach (var setup in setups)
            {
                if (setup.Program is int program) synthesizer.ProcessMidiMessage(setup.MidiChannel, 0xC0, program, 0);
                // Volume as a MIDI controller, so the synthesiser mixes rather than the caller
                // scaling samples after the fact and losing the instrument's own dynamics.
                synthesizer.ProcessMidiMessage(setup.MidiChannel, 0xB0, 7, (int)(Math.Clamp(setup.Volume, 0, 1) * 127));
            }

            // The music plus the release of its last notes. A one-shot keeps that release as its
            // tail; a loop folds it back onto its start, where it sounds when the clip comes round.
            int loopSamples = score.LoopSamples;
            int rendered = loopSamples + (int)(TailSeconds * score.SampleRate);
            var left = new float[rendered];
            var right = new float[rendered];

            int cursor = 0, index = 0;
            while (cursor < rendered)
            {
                int nextEvent = index < events.Count ? Math.Min(events[index].Sample, rendered) : rendered;
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
            var mix = new double[score.TotalSamples];
            for (int i = 0; i < rendered; i++)
            {
                double v = (left[i] + right[i]) * 0.5;
                if (score.Loop) mix[i % loopSamples] += v;
                else if (i < mix.Length) mix[i] = v;
            }
            return MusicSequencer.Normalize(mix, score.MasterVolume, score.SampleRate);
        }

        /// <summary>
        /// The score as note events in time order, on the same step grid as the tracker, and the
        /// MIDI channel each part plays on. Needs no font, so the suite exercises it.
        /// </summary>
        internal static List<NoteEvent> Timeline(MusicSequencer.Score score, List<ChannelSetup>? setups = null)
        {
            var events = new List<NoteEvent>();
            int midiChannel = 0;
            int drumHold = Math.Min(score.StepSamples, (int)(0.12 * score.SampleRate));

            foreach (var channel in score.Channels)
            {
                // Channel 9 is percussion in General MIDI and nothing else may use it; melodic
                // parts take the remaining channels in order and skip over it.
                int ch = channel.IsDrums ? 9 : NextMelodicChannel(ref midiChannel);
                setups?.Add(new ChannelSetup(ch, channel.IsDrums ? null : ProgramFor(channel.Instrument), channel.Volume));

                var steps = channel.Steps;
                for (int pass = 0; pass < score.RepeatCount; pass++)
                {
                    for (int s = 0; s < steps.Count; s++)
                    {
                        var step = steps[s];
                        if (step.IsRest || step.Held) continue;

                        int key = channel.IsDrums
                            ? (DrumKeys.TryGetValue(step.Token, out var d) ? d : -1)
                            : (MusicSequencer.TryNoteToFrequency(step.Token, out var hz) ? KeyOf(hz) : -1);
                        if (key < 0) continue;

                        // Each pass starts where the whole score starts again, not where this
                        // part runs out, so parts of different lengths stay together.
                        int start = (pass * score.Steps + s) * score.StepSamples;
                        // A drum is a one-shot: hold it briefly and let the sample ring out.
                        int held = channel.IsDrums ? drumHold : (int)(step.Length * score.StepSamples * 0.98);
                        int end = start + Math.Max(1, held);

                        events.Add(new NoteEvent(start, true, ch, key, channel.IsDrums ? 110 : 96));
                        events.Add(new NoteEvent(end, false, ch, key, 0));
                    }
                }
            }

            // At one instant a note-off goes before a note-on, so a key struck again is not cut
            // by the release of its previous strike. It has to be a real ordering: written as
            // (a.On ? 1 : -1), two simultaneous note-offs were each "before" the other, and
            // List.Sort threw on every two-part score that ended on a shared note.
            events.Sort(static (a, b) => a.Sample != b.Sample ? a.Sample.CompareTo(b.Sample) : a.On.CompareTo(b.On));
            return events;
        }

        private static int NextMelodicChannel(ref int next)
        {
            if (next == 9) next = 10;          // never hand out the percussion channel
            if (next > 15) next = 0;
            return next++;
        }

        /// <summary>A program for an instrument name or a General MIDI number, or false when it is neither.</summary>
        public static bool TryProgramFor(string instrument, out int program)
        {
            if (int.TryParse(instrument, NumberStyles.None, CultureInfo.InvariantCulture, out program))
                return program <= 127;
            return Programs.TryGetValue(instrument, out program);
        }

        private static int ProgramFor(string instrument) => TryProgramFor(instrument, out var p) ? p : 0;

        /// <summary>Frequency back to a MIDI key, so note parsing stays in one place.</summary>
        private static int KeyOf(double frequency)
            => Math.Clamp((int)Math.Round(69 + 12 * Math.Log2(frequency / 440.0)), 0, 127);
    }
}
