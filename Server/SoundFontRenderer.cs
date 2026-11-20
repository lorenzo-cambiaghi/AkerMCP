using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
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

        /// <summary>
        /// MIDI's sixteen channels less channel 10 (index 9), which General MIDI keeps for drums.
        /// Every drum part shares that one, so only the melodic parts count against this.
        /// </summary>
        public const int MaxMelodicParts = 15;

        /// <summary>
        /// The parsed font, kept between calls. Building a synthesiser from the path re-read and
        /// re-parsed all 151 MB on every create_music, so a model iterating on a tune paid the
        /// parse each time it listened. MeltySynth's SoundFont is immutable once loaded and a
        /// Synthesizer only reads it, so one instance serves every render.
        ///
        /// It is let go after ten idle minutes, because it is ~150 MB of samples in a server that
        /// otherwise lives in a few tens: tunes are written in bursts — render, listen, change a
        /// bar, render again — and a burst stays inside that window, while a session that wrote
        /// its music an hour ago should not still be carrying an orchestra.
        /// </summary>
        internal static readonly ParsedFileCache<SoundFont> Fonts =
            new ParsedFileCache<SoundFont>(path => new SoundFont(path)) { IdleRelease = TimeSpan.FromMinutes(10) };

        public static byte[] Render(string soundFontPath, MusicSequencer.Score score)
        {
            var setups = new List<ChannelSetup>();
            var events = Timeline(score, setups);

            var synthesizer = new Synthesizer(Fonts.Get(soundFontPath), score.SampleRate);
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
            bool drumsSetUp = false;

            foreach (var channel in score.Channels)
            {
                // Channel 9 is percussion in General MIDI and nothing else may use it; melodic
                // parts take the remaining channels in order and skip over it.
                int ch = channel.IsDrums ? 9 : NextMelodicChannel(ref midiChannel);

                // A melodic part has a channel of its own, so its volume is the channel's volume
                // controller. Drum parts all share channel 9, and setting its controller per part
                // let the last one decide for all — a quiet hat line under a loud kick made the
                // kick quiet too. So channel 9 stays at full volume and each drum part scales the
                // velocity of its own hits. MeltySynth applies the same square law to velocity as
                // to the volume controller (measured: velocity 44 of 110 and CC7 51 of 127 both
                // give 0.17 of the level), so the part comes out as loud as a melodic part would.
                int velocity = channel.IsDrums ? (int)Math.Round(DrumVelocity * channel.Volume) : MelodicVelocity;
                if (!channel.IsDrums)
                    setups?.Add(new ChannelSetup(ch, ProgramFor(channel.Instrument), channel.Volume));
                else if (!drumsSetUp)
                {
                    setups?.Add(new ChannelSetup(9, null, 1.0));
                    drumsSetUp = true;
                }
                // Velocity 0 is a note-off in MIDI, not a quiet note: a silent drum part plays nothing.
                if (velocity <= 0) continue;

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

                        events.Add(new NoteEvent(start, true, ch, key, velocity));
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

        private const int MelodicVelocity = 96;
        private const int DrumVelocity = 110;

        private static int NextMelodicChannel(ref int next)
        {
            if (next == 9) next = 10;          // never hand out the percussion channel
            // Score.Parse refuses a score with more melodic parts than this, so getting here is
            // a bug; wrapping to channel 0, as this did, silently re-voiced the first part.
            if (next > 15)
                throw new InvalidOperationException(
                    $"More than {MaxMelodicParts} melodic parts reached the SoundFont timeline.");
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

    /// <summary>
    /// One file, parsed once and kept while it is in use. Keyed by the full path, the last write
    /// time and the length, so a font replaced on disk — a new AKER_MCP_SOUNDFONT, the download
    /// landing over an old copy — is read again rather than served stale.
    ///
    /// <para>One slot, not a dictionary: the value is a whole SoundFont, and two of them would be
    /// twice the memory for a case — switching fonts back and forth — nobody has. A new file
    /// drops the old value before parsing, so the two are never held together.</para>
    /// </summary>
    internal sealed class ParsedFileCache<T> where T : class
    {
        private readonly Func<string, T> _load;
        private readonly object _sync = new object();
        private string? _path;
        private DateTime _stamp;
        private long _length;
        private T? _value;
        private DateTime _lastUse;
        private Timer? _release;

        public ParsedFileCache(Func<string, T> load) { _load = load; }

        /// <summary>How long the value is kept without a call; zero or less keeps it for good.</summary>
        public TimeSpan IdleRelease { get; set; } = Timeout.InfiniteTimeSpan;

        /// <summary>How many times the file has been parsed, for the suite.</summary>
        public int Loads { get; private set; }

        public bool IsLoaded { get { lock (_sync) return _value != null; } }

        public T Get(string path)
        {
            var info = new FileInfo(path);
            if (!info.Exists) throw new FileNotFoundException($"No such file: {path}", path);
            var full = info.FullName;
            var stamp = info.LastWriteTimeUtc;
            var length = info.Length;

            // The parse runs under the lock on purpose: two calls arriving together must not
            // each read 151 MB, and the second is better off waiting for the first's result.
            lock (_sync)
            {
                bool same = _value != null && _stamp == stamp && _length == length
                            && string.Equals(_path, full, OperatingSystem.IsWindows()
                                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
                if (!same)
                {
                    _value = null;               // let the old one go before the new one is built
                    _value = _load(full);
                    Loads++;
                    _path = full;
                    _stamp = stamp;
                    _length = length;
                }
                _lastUse = DateTime.UtcNow;
                ArmRelease();
                return _value!;
            }
        }

        private void ArmRelease()
        {
            if (IdleRelease <= TimeSpan.Zero) return;
            _release ??= new Timer(_ => ReleaseIfIdle(), null, Timeout.Infinite, Timeout.Infinite);
            _release.Change(IdleRelease, Timeout.InfiniteTimeSpan);
        }

        private void ReleaseIfIdle()
        {
            lock (_sync)
            {
                if (_value == null) return;
                var idle = DateTime.UtcNow - _lastUse;
                if (idle < IdleRelease)
                {
                    // A call landed while the timer was firing: wait out the rest of its window.
                    _release?.Change(IdleRelease - idle, Timeout.InfiniteTimeSpan);
                    return;
                }
                _value = null;
                _path = null;
            }
            // Dropping the reference alone gives nothing back: the samples sit on the large
            // object heap until the next full collection, and an idle server can go hours
            // without one. Releasing is the point, so the collection is asked for here, once
            // per idle period at most.
            GC.Collect();
        }
    }
}
